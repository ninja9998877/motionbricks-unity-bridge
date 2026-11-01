# -*- coding: utf-8 -*-
"""GPU-free protocol constants, request models, and validation.

Mirrors the HTTP contract documented in README.md and the checks in
``SomaPose.Validate`` (unity/Scripts/SomaPose.cs). No warp / motionbricks /
CUDA imports — safe to unit-test on any machine.
"""

from __future__ import annotations

import math
from typing import Any

from pydantic import BaseModel, Field

# Joint order matches SomaHumanoidMapper.SomaJointNames / SOMA N1 manifest.
JOINT_NAMES: list[str] = [
    "Hips",
    "Spine1",
    "Spine2",
    "Chest",
    "Neck1",
    "Neck2",
    "Head",
    "Jaw",
    "LeftEye",
    "RightEye",
    "LeftShoulder",
    "LeftArm",
    "LeftForeArm",
    "LeftHand",
    "LeftHandThumbEnd",
    "LeftHandMiddleEnd",
    "RightShoulder",
    "RightArm",
    "RightForeArm",
    "RightHand",
    "RightHandThumbEnd",
    "RightHandMiddleEnd",
    "LeftLeg",
    "LeftShin",
    "LeftFoot",
    "LeftToeBase",
    "RightLeg",
    "RightShin",
    "RightFoot",
    "RightToeBase",
]
NUM_JOINTS = len(JOINT_NAMES)  # 30
SAMPLE_RATE = 30.0
CONTEXT_POSES = 4
MIN_TARGET_POSES = 1
MAX_TARGET_POSES = 4
DEFAULT_MODEL_NAME = "soma_n1"


class TransformJson(BaseModel):
    p: list[float]  # [x, y, z], meters
    q: list[float]  # [x, y, z, w] quaternion


class PoseJson(BaseModel):
    # root: world placement of Hips; joints: 30 local transforms (index 0 = Hips).
    root: TransformJson
    joints: list[list[float]]  # 30 × [x, y, z, x, y, z, w]


class GenerateRequest(BaseModel):
    context: list[PoseJson] = Field(description="exactly 4 poses")
    targets: list[PoseJson] = Field(description="1–4 poses")


def _finite(values: list[float], what: str) -> None:
    if not all(math.isfinite(v) for v in values):
        raise ValueError(f"{what}：含非有限数值")


def validate_transform(p: list[float], q: list[float], what: str) -> tuple[list[float], list[float]]:
    """Validate position + quaternion; return normalized quaternion.

    Same rules as motionbricks_server._parse_transform (without creating warp types).
    """
    if len(p) != 3 or len(q) != 4:
        raise ValueError(f"{what}：p 须 3 个数、q 须 4 个数")
    _finite([*p, *q], what)
    norm = math.sqrt(sum(v * v for v in q))
    if norm < 1e-8:
        raise ValueError(f"{what}：四元数接近零向量")
    q_n = [v / norm for v in q]
    return list(p), q_n


def validate_pose(pose: PoseJson, index: int) -> None:
    """Validate one pose (joint count + finite transforms)."""
    if len(pose.joints) != NUM_JOINTS:
        raise ValueError(f"姿态 {index}：joints 须 {NUM_JOINTS} 个，got {len(pose.joints)}")
    validate_transform(pose.root.p, pose.root.q, f"姿态 {index} 的 root")
    for j in range(1, NUM_JOINTS):
        vals = pose.joints[j]
        if len(vals) != 7:
            raise ValueError(f"姿态 {index} 关节 {j}：须 7 个数，got {len(vals)}")
        validate_transform(vals[:3], vals[3:], f"姿态 {index} 关节 {j}")


def validate_generate_counts(context_len: int, targets_len: int) -> None:
    """Raise ValueError with the same messages the HTTP layer turns into 400."""
    if context_len != CONTEXT_POSES:
        raise ValueError(f"context 须恰好 {CONTEXT_POSES} 个姿态，got {context_len}")
    if not MIN_TARGET_POSES <= targets_len <= MAX_TARGET_POSES:
        raise ValueError(
            f"targets 须 {MIN_TARGET_POSES}~{MAX_TARGET_POSES} 个姿态，got {targets_len}"
        )


def validate_generate_request(req: GenerateRequest) -> None:
    validate_generate_counts(len(req.context), len(req.targets))
    for i, pose in enumerate(req.context):
        validate_pose(pose, i)
    for i, pose in enumerate(req.targets):
        validate_pose(pose, i)


def validate_soma_pose_like(root_p: list[float], root_q: list[float], joints: list[list[float]]) -> None:
    """Python mirror of C# SomaPose.Validate (joint count 30 + finite numbers).

    Does not require Unity. Quaternion near-zero is *not* checked here — that is a
    server-side extra in validate_transform; C# only rejects NaN/Inf.
    """
    if joints is None or len(joints) != NUM_JOINTS:
        raise ValueError(f"姿态 joints 须为 {NUM_JOINTS} 个")
    _finite(root_p, "root.p")
    _finite(root_q, "root.q")
    for j, joint in enumerate(joints):
        if len(joint) != 7:
            raise ValueError(f"姿态含非法关节长度：joints[{j}]")
        _finite(joint[:3], f"joints[{j}].p")
        _finite(joint[3:], f"joints[{j}].q")


def identity_joint() -> list[float]:
    return [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0]


def make_tpose(height: float = 0.98) -> PoseJson:
    """Minimal valid standing-ish pose for contract tests (identity rotations)."""
    joints = [identity_joint() for _ in range(NUM_JOINTS)]
    joints[0] = [0.0, height, 0.0, 0.0, 0.0, 0.0, 1.0]
    return PoseJson(
        root=TransformJson(p=[0.0, height, 0.0], q=[0.0, 0.0, 0.0, 1.0]),
        joints=joints,
    )


def pose_to_wire_dict(pose: PoseJson) -> dict[str, Any]:
    return {
        "root": {"p": list(pose.root.p), "q": list(pose.root.q)},
        "joints": [list(j) for j in pose.joints],
    }


def info_payload(model_name: str = DEFAULT_MODEL_NAME) -> dict[str, Any]:
    return {
        "model": model_name,
        "rig": "SOMA",
        "sample_rate": SAMPLE_RATE,
        "num_joints": NUM_JOINTS,
        "joint_names": list(JOINT_NAMES),
        "coordinate_system": (
            "Y-up 右手系，米制；即 SOMA 模型空间。"
            "每个姿态 30 个关节局部变换，index 0（Hips）为 root，其局部变换即世界放置。"
        ),
        "context_poses": CONTEXT_POSES,
        "target_pose_range": [MIN_TARGET_POSES, MAX_TARGET_POSES],
    }
