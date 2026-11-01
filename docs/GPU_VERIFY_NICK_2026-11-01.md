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
| Humanoid Clip Baker path (Capture→generate→bone `.anim`) | **PASS** — Timeline sample `DefaultMale.fbx` (Humanoid); prefab + bake (see addendum) |
| Server still up after Unity | YES — `/health` ok |

Report JSON on Nick: `D:\projects\motionbricks-gpu-verify\unity_verify_2026-11-01.json`.

## Addendum — Humanoid Clip Baker path (same day, Asia/Shanghai)

Character source (no Asset Store login): Unity package cache
`com.unity.timeline@1.7.7` → `Samples~/GameplaySequenceDemo/Character`
copied into the demo project.

| Item | Path / result |
|---|---|
| FBX | `D:\projects\motionbricks-unity-demo\Assets\Characters\DefaultMale\Models\DefaultMale.fbx` (`animationType=Human`) |
| Prefab (mapper+client+player) | `Assets/Characters/DefaultMale/DefaultMale_MotionBricks.prefab` |
| Baked clip | `Assets/MotionBricks/Baked/HumanoidDefaultMale_CaptureBake.anim` — 50 frames, 30 fps, length ≈1.63 s, **203** transform curve bindings |
| Capture / ApplyPose smoke | PASS (`capture_root_y≈0.85`, hips `Skeleton/Root/Hips`) |
| Report JSON | `D:\projects\motionbricks-gpu-verify\humanoid_verify_2026-11-01.json` |
| Batchmethod | `MotionBricksBridge.Editor.MotionBricksHumanoidVerify.Run` |

Play Mode interactive locomotion was not required for this gate; Editor capture + ApplyPose + bone-curve bake covers the Clip Baker Humanoid path.

## Follow-ups

1. Optional: open demo, drop `DefaultMale_MotionBricks` prefab, Play Mode + `LocomotionDemo` against `http://127.0.0.1:8000`.
2. Optional: UI **Tools > MotionBricks > Clip Baker** Bake All Takes on the same prefab (same wire as batch verify).
