# -*- coding: utf-8 -*-
"""Pytest fixtures: GPU-free FastAPI TestClient with a stub engine."""

from __future__ import annotations

import os
import sys
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

SERVER_DIR = Path(__file__).resolve().parents[1]
if str(SERVER_DIR) not in sys.path:
    sys.path.insert(0, str(SERVER_DIR))

# Must be set before the app lifespan runs.
os.environ["MOTIONBRICKS_SKIP_ENGINE"] = "1"

from protocol import (  # noqa: E402
    NUM_JOINTS,
    PoseJson,
    SAMPLE_RATE,
    make_tpose,
    pose_to_wire_dict,
)
import motionbricks_server as mbs  # noqa: E402


class FakeEngine:
    """Stub SOMA engine: echoes last context pose as a short clip (no CUDA)."""

    def __init__(self, num_frames: int = 18):
        self.num_frames = num_frames

    def generate_from_request(self, req):
        last = req.context[-1]
        frame = pose_to_wire_dict(last)
        frames = [frame for _ in range(self.num_frames)]
        return {
            "frames": frames,
            "num_frames": self.num_frames,
            "sample_rate": SAMPLE_RATE,
        }


@pytest.fixture
def fake_engine():
    return FakeEngine()


@pytest.fixture
def client(fake_engine):
    mbs.clear_engine_for_tests()
    with TestClient(mbs.app) as c:
        mbs.set_engine_for_tests(fake_engine, skeleton=True, model_name="soma_n1-test")
        yield c
    mbs.clear_engine_for_tests()


@pytest.fixture
def client_no_engine():
    mbs.clear_engine_for_tests()
    with TestClient(mbs.app) as c:
        yield c
    mbs.clear_engine_for_tests()


@pytest.fixture
def valid_body():
    ctx = [make_tpose() for _ in range(4)]
    tgt = [make_tpose(height=1.0)]
    return {
        "context": [pose_to_wire_dict(p) for p in ctx],
        "targets": [pose_to_wire_dict(p) for p in tgt],
    }
