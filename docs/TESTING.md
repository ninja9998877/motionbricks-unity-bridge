# Testing

## GPU-free contract tests (default)

These tests **do not** need NVIDIA GPU, CUDA, TensorRT, warp, or the motionbricks repo.
They use FastAPI `TestClient` with `MOTIONBRICKS_SKIP_ENGINE=1` and a stub engine.

```sh
cd /path/to/motionbricks-unity-bridge
python3 -m venv .venv
.venv/bin/pip install -r server/requirements-dev.txt
.venv/bin/pytest server/tests -v
```

Also useful without pytest:

```sh
.venv/bin/python -m py_compile server/protocol.py server/motionbricks_server.py
```

### What is covered
| Check | Where |
|---|---|
| Invalid `context` / `targets` counts → HTTP 400 | `test_generate_bad_*` |
| Bad joint count / joint length / zero quat → 400 | `test_generate_wrong_*`, `test_generate_nonfinite` |
| JSON encode/decode round-trip (`PoseJson`, `GenerateRequest`) | `test_*_round_trip` |
| Python mirror of C# `SomaPose.Validate` (30 joints + finite) | `test_soma_pose_validate_*` |
| `/health`, `/info`, stub `/generate` without GPU | `test_health_*`, `test_info_shape`, `test_generate_ok_with_stub` |

C# `SomaPose.Validate` cannot run here without Unity; `protocol.validate_soma_pose_like` mirrors its rules.
Server-only extras (near-zero quaternion rejection, count checks) are tested via HTTP 400 paths.

## GPU tests (later, on your PC)

Not automated yet. On a host with CUDA 12 + TensorRT + the motionbricks uv env:

1. Start the real server (see README — **do not** set `MOTIONBRICKS_SKIP_ENGINE`).
2. `curl http://127.0.0.1:8000/health` → `{"ok": true}`
3. `curl http://127.0.0.1:8000/info` → model name, 30 joint names
4. POST a small `/generate` with 4 context + 1 target T-poses; expect `num_frames` about 18–58,
   frame 0 ≈ last context, no NaNs in joints.

Those steps are the P0 GPU first-boot acceptance checklist.
