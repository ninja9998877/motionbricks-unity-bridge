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

## GPU first-boot (Nick PC — verified 2026-11-01 Asia/Shanghai)

**Status: PASS** on host **Nick** (RTX 3060, CUDA 13.2 / driver stack, TensorRT via motionbricks `uv` env).

| Item | Result |
|---|---|
| motionbricks tip | `54fc563` (`D:\projects\motionbricks`) |
| bridge tip at verify | `27977d5` (+ BakeRunner yield/try fix in this commit) |
| Server | `http://127.0.0.1:8000` (real engine; **not** `MOTIONBRICKS_SKIP_ENGINE`) |
| `/health` | `{"ok": true}` |
| `/info` | `mmm_v6_soma_fp32`, 30 joints, 30 Hz |
| First TensorRT plan | ~116 s (then cached) |
| CLI `/generate` (T-pose×4 +1) | PASS — e.g. `num_frames=50`, no NaN |
| Unity 2022.3.62f3 compile | PASS (demo project `D:\projects\motionbricks-unity-demo`) |
| Unity Editor HTTP smoke | PASS — `/health` `/info` `/generate` via same wire as Clip Baker (`num_frames=26`, frame0 root.y=0.98) |
| Unity `.anim` write | PASS — `Assets/MotionBricks/Baked/GpuVerifyTPose.anim` (root curves from generate frames) |
| Full Humanoid Clip Baker / Play Mode | **Skipped** — no Humanoid character asset on disk for a quick import |

Checklist log: [GPU_VERIFY_NICK_2026-11-01.md](GPU_VERIFY_NICK_2026-11-01.md).

### How to re-run on another CUDA host

1. Start the real server (see README — **do not** set `MOTIONBRICKS_SKIP_ENGINE`).
2. `curl http://127.0.0.1:8000/health` → `{"ok": true}`
3. `curl http://127.0.0.1:8000/info` → model name, 30 joint names
4. POST a small `/generate` with 4 context + 1 target T-poses; expect `num_frames` about 18–58,
   frame 0 ≈ last context, no NaNs in joints.
5. Optional: Unity Editor project with `unity/Scripts` + `unity/Editor` imported; open **Tools > MotionBricks > Clip Baker** and Test Connection; with a Humanoid + mapper, Bake All Takes.
