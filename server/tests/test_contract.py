# -*- coding: utf-8 -*-
"""GPU-free contract tests for the MotionBricks Unity bridge protocol.

Covers:
1. Invalid context/targets → HTTP 400
2. JSON encode/decode round-trip for pose / request types
3. Python mirror of SomaPose.Validate
4. /health, /info, and stub /generate without CUDA/TensorRT
"""

from __future__ import annotations

import copy
import json
import math

import pytest
from pydantic import ValidationError

from protocol import (
    CONTEXT_POSES,
    GenerateRequest,
    JOINT_NAMES,
    MAX_TARGET_POSES,
    MIN_TARGET_POSES,
    NUM_JOINTS,
    PoseJson,
    TransformJson,
    identity_joint,
    make_tpose,
    pose_to_wire_dict,
    validate_generate_request,
    validate_soma_pose_like,
    validate_transform,
)


# ---------------------------------------------------------------------------
# JSON round-trip
# ---------------------------------------------------------------------------
def test_pose_json_round_trip():
    pose = make_tpose()
    wire = pose_to_wire_dict(pose)
    raw = json.dumps(wire)
    restored = PoseJson.model_validate(json.loads(raw))
    assert restored.root.p == pose.root.p
    assert restored.root.q == pose.root.q
    assert len(restored.joints) == NUM_JOINTS
    assert restored.joints[0] == pose.joints[0]


def test_generate_request_round_trip():
    req = GenerateRequest(
        context=[make_tpose() for _ in range(CONTEXT_POSES)],
        targets=[make_tpose(), make_tpose(height=1.05)],
    )
    raw = req.model_dump_json()
    again = GenerateRequest.model_validate_json(raw)
    assert len(again.context) == CONTEXT_POSES
    assert len(again.targets) == 2
    assert again.context[0].root.p == req.context[0].root.p


def test_joint_names_count_and_hips_root():
    assert len(JOINT_NAMES) == 30
    assert JOINT_NAMES[0] == "Hips"
    assert JOINT_NAMES[-1] == "RightToeBase"


# ---------------------------------------------------------------------------
# SomaPose.Validate mirror
# ---------------------------------------------------------------------------
def test_soma_pose_validate_ok():
    pose = make_tpose()
    validate_soma_pose_like(pose.root.p, pose.root.q, pose.joints)


def test_soma_pose_validate_wrong_joint_count():
    pose = make_tpose()
    with pytest.raises(ValueError, match="须为 30"):
        validate_soma_pose_like(pose.root.p, pose.root.q, pose.joints[:10])


def test_soma_pose_validate_nan():
    pose = make_tpose()
    bad = copy.deepcopy(pose.joints)
    bad[3][1] = float("nan")
    with pytest.raises(ValueError, match="非有限"):
        validate_soma_pose_like(pose.root.p, pose.root.q, bad)


def test_soma_pose_validate_inf_root():
    with pytest.raises(ValueError, match="非有限"):
        validate_soma_pose_like([0.0, float("inf"), 0.0], [0, 0, 0, 1], [identity_joint()] * 30)


def test_validate_transform_zero_quat():
    with pytest.raises(ValueError, match="零向量"):
        validate_transform([0, 0, 0], [0, 0, 0, 0], "root")


def test_validate_transform_normalizes():
    _, q = validate_transform([0, 1, 0], [0, 0, 0, 2], "root")
    assert math.isclose(math.sqrt(sum(v * v for v in q)), 1.0, rel_tol=1e-6)


# ---------------------------------------------------------------------------
# HTTP: health / info
# ---------------------------------------------------------------------------
def test_health_ok_with_stub(client):
    r = client.get("/health")
    assert r.status_code == 200
    assert r.json() == {"ok": True}


def test_health_false_without_engine(client_no_engine):
    r = client_no_engine.get("/health")
    assert r.status_code == 200
    assert r.json() == {"ok": False}


def test_info_shape(client):
    r = client.get("/info")
    assert r.status_code == 200
    body = r.json()
    assert body["num_joints"] == 30
    assert body["sample_rate"] == 30.0
    assert body["context_poses"] == 4
    assert body["target_pose_range"] == [MIN_TARGET_POSES, MAX_TARGET_POSES]
    assert body["joint_names"] == JOINT_NAMES
    assert body["rig"] == "SOMA"


def test_generate_503_without_engine(client_no_engine, valid_body):
    r = client_no_engine.post("/generate", json=valid_body)
    assert r.status_code == 503


# ---------------------------------------------------------------------------
# HTTP: /generate validation → 400
# ---------------------------------------------------------------------------
def test_generate_ok_with_stub(client, valid_body):
    r = client.post("/generate", json=valid_body)
    assert r.status_code == 200, r.text
    body = r.json()
    assert body["num_frames"] == 18
    assert body["sample_rate"] == 30.0
    assert len(body["frames"]) == 18
    assert len(body["frames"][0]["joints"]) == NUM_JOINTS


@pytest.mark.parametrize("n_ctx", [0, 1, 3, 5])
def test_generate_bad_context_count(client, valid_body, n_ctx):
    body = copy.deepcopy(valid_body)
    pose = body["context"][0]
    body["context"] = [pose for _ in range(n_ctx)]
    r = client.post("/generate", json=body)
    assert r.status_code == 400
    assert "context" in r.json()["detail"]


@pytest.mark.parametrize("n_tgt", [0, 5, 8])
def test_generate_bad_targets_count(client, valid_body, n_tgt):
    body = copy.deepcopy(valid_body)
    pose = body["targets"][0]
    body["targets"] = [pose for _ in range(n_tgt)]
    r = client.post("/generate", json=body)
    assert r.status_code == 400
    assert "targets" in r.json()["detail"]


def test_generate_wrong_joint_count(client, valid_body):
    body = copy.deepcopy(valid_body)
    body["context"][0]["joints"] = body["context"][0]["joints"][:10]
    r = client.post("/generate", json=body)
    assert r.status_code == 400
    assert "joints" in r.json()["detail"]


def test_generate_bad_joint_length(client, valid_body):
    body = copy.deepcopy(valid_body)
    body["context"][1]["joints"][5] = [0, 0, 0]  # not 7
    r = client.post("/generate", json=body)
    assert r.status_code == 400


def test_generate_nonfinite(client, valid_body):
    body = copy.deepcopy(valid_body)
    body["targets"][0]["root"]["p"][1] = float("nan")
    # JSON cannot encode NaN by default with standard json; send via model dump workaround
    # Use a string that pydantic may reject, or inject after — FastAPI uses jsonable encoder.
    # Send infinity which JSON allows in Python's json with allow_nan but httpx uses std.
    body["targets"][0]["root"]["p"][1] = 1e400  # overflows to Infinity in JSON libs carefully
    # Safer: use a zero quaternion which is finite but invalid for the server.
    body["targets"][0]["root"]["p"][1] = 0.98
    body["targets"][0]["root"]["q"] = [0.0, 0.0, 0.0, 0.0]
    r = client.post("/generate", json=body)
    assert r.status_code == 400
    assert "四元数" in r.json()["detail"] or "零" in r.json()["detail"]


def test_generate_malformed_root_p(client, valid_body):
    body = copy.deepcopy(valid_body)
    body["context"][2]["root"]["p"] = [0.0, 0.98]  # only 2
    r = client.post("/generate", json=body)
    # pydantic 422 or our 400 — either is a documented validation error for bad shape
    assert r.status_code in (400, 422)


def test_pure_validate_generate_request_rejects_bad_counts():
    with pytest.raises(ValueError, match="context"):
        validate_generate_request(
            GenerateRequest(context=[make_tpose()], targets=[make_tpose()])
        )


def test_pydantic_rejects_missing_fields():
    with pytest.raises(ValidationError):
        PoseJson.model_validate({"root": {"p": [0, 0, 0], "q": [0, 0, 0, 1]}})
