# GPU + Unity verify — Nick — 2026-11-01 (Asia/Shanghai)

Concise checklist log for MotionBricks ↔ Unity bridge.

## Host

- Machine: Nick (`50d42bb6-d5a1-427e-b12b-d60f20cef560`)
- GPU: NVIDIA RTX 3060
- CUDA / driver: CUDA 13.2 stack (as reported by local toolkit/driver)
- Python: 3.12 + `uv` env under `D:\projects\motionbricks`
- motionbricks tip: `54fc563`
- bridge tip before this docs commit: `27977d5`

## Server (real engine)

- Path: `D:\projects\motionbricks` (+ bridge `server/motionbricks_server.py`)
- Listen: `http://127.0.0.1:8000`
- First TensorRT plan build: **~116 s**, then cached
- `/health` → `{"ok":true}`
- `/info` → `mmm_v6_soma_fp32`, SOMA, 30 joints, 30 Hz
- CLI `/generate` (4× T-pose context + 1 target): **PASS** (example `num_frames=50`, no NaN)

## Unity

- Editor: **2022.3.62f3** (`C:\Program Files\Unity\Hub\Editor\2022.3.62f3`)
- Also installed: 6000.3.23f1 (not used for this smoke)
- Project: `D:\projects\motionbricks-unity-demo` (created `-batchmode -createProject`)
- Imported: `unity/Scripts` + `unity/Editor` (+ one-off Editor verify method)

| Check | Result |
|---|---|
| Script compile | PASS (after BakeRunner CS1626 fix) |
| Editor HTTP `/health` `/info` `/generate` | PASS — `num_frames=26`, `frame0_root_y=0.98`, Validate/no NaN |
| Write `.anim` from generate frames | PASS — `Assets/MotionBricks/Baked/GpuVerifyTPose.anim` (25 KB, 30 fps, length ~0.83 s) |
| Humanoid Clip Baker / Play Mode locomotion | **SKIPPED** — no ready Humanoid FBX/character on disk for a quick drop-in |
| Server still up after Unity | YES — `/health` ok |

Report JSON on Nick: `D:\projects\motionbricks-gpu-verify\unity_verify_2026-11-01.json`.

## Follow-ups

1. Import any Humanoid, attach `SomaHumanoidMapper` + run **Tools > MotionBricks > Clip Baker** Bake All Takes.
2. Optional Play Mode `LocomotionDemo` against the same server.
