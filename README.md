# MotionBricks ↔ Unity Bridge

Drive Unity Humanoid characters with NVIDIA MotionBricks (SIGGRAPH 2026, a real-time motion-generation model).
Two ways to use it:

- **Realtime client** — a **Python FastAPI inference server** (runs on an NVIDIA-GPU host and reuses the official
  `MotionBricksSOMAEngine`, so encoding / inference / decoding / endpoint correction exactly match the official demo)
  + a **Unity C# client** (HTTP calls, Humanoid bone mapping, 30fps playback with infinite chained generation).
- **Offline clip baker** ("motion factory" editor tool) — calls the same inference server from the Unity Editor and
  bakes the generated motion into `.anim` files. This is the recommended route for **mobile games**: zero inference,
  zero networking, and zero GPU cost at runtime.

> 中文版 README 见 [README.zh-CN.md](README.zh-CN.md)

```
┌─────────────────────────────┐         HTTP/JSON          ┌──────────────────────────────┐
│ Unity client                 │   POST /generate           │ Inference server (GPU host)  │
│                             │ ─────────────────────────► │                              │
│ SomaHumanoidMapper           │   {context: 4 poses,       │ motionbricks_server.py       │
│   capture Humanoid pose      │    targets: 1-4 poses}     │   MotionBricksSOMAEngine     │
│   Unity left-handed →        │                            │   (official engine,          │
│   SOMA right-handed          │ ◄───────────────────────── │    FP32/FP16 ONNX)           │
│                             │                            │                              │
│ MotionBricksPlayer          │   {frames: [...],          │ Calls create_input /        │
│   30fps playback + chained   │    num_frames,            │ update_from_poses /          │
│   generation at transition   │    sample_rate: 30}       │ generate_animation / sample  │
│                             │                            │ directly                     │
│ LocomotionDemo              │                            │                              │
│   WASD → target → loop       │                            │ First launch builds a        │
│                             │                            │ TensorRT plan (minutes,      │
│ Editor: Clip Baker          │                            │ then cached in .trt-cache)   │
│   Tools > MotionBricks >     │                            │                              │
│   Clip Baker (offline bake)  │                            │                              │
└─────────────────────────────┘                            └──────────────────────────────┘
```

## Repository layout

```
motionbricks-unity-bridge/
├── README.md                        # English (this file)
├── README.zh-CN.md                  # 中文版
├── CHANGELOG.md                     # Keep a Changelog
├── LICENSE                          # Apache 2.0
├── docs/
│   └── TESTING.md                   # how to run GPU-free contract tests
├── server/
│   ├── protocol.py                  # GPU-free models + validation (shared with tests)
│   ├── motionbricks_server.py       # FastAPI inference server (lazy-loads warp/motionbricks)
│   ├── requirements.txt             # fastapi + uvicorn (install notes below)
│   ├── requirements-dev.txt         # pytest + httpx for contract tests
│   └── tests/                       # GPU-free contract tests (TestClient + stub engine)
└── unity/
    ├── Scripts/                     # runtime (copy into any non-Editor folder)
    │   ├── SomaPose.cs              # pose structs + hand-written JSON codec (no dependencies)
    │   ├── MotionBricksClient.cs    # HTTP client (UnityWebRequest, async Task API)
    │   ├── SomaHumanoidMapper.cs    # SOMA 30 joints ↔ HumanBodyBones mapping, capture/playback
    │   ├── MotionBricksPlayer.cs    # 30fps player + auto-chained generation
    │   └── LocomotionDemo.cs        # WASD demo controller
    └── Editor/                      # editor-only (must live under an Editor folder)
        ├── MotionBricksClipBaker.cs # Clip Baker window: Tools > MotionBricks > Clip Baker
        └── MotionBricksBakeRunner.cs# hidden coroutine driver for bake-time HTTP requests
```

## Requirements

Server (GPU host):
- NVIDIA GPU, CUDA 12, TensorRT, Python 3.12
- The motionbricks repository (`git clone https://github.com/NVlabs/motionbricks.git`) with its uv environment
  (heavy dependencies like warp / tensorrt only come from the repo environment)

Client:
- Unity 2021.3+, character with a Humanoid Avatar; only UnityWebRequest + hand-written JSON, no third-party packages

## Testing

GPU-free contract tests (no CUDA): see [docs/TESTING.md](docs/TESTING.md).

```sh
python3 -m venv .venv
.venv/bin/pip install -r server/requirements-dev.txt
.venv/bin/pytest server/tests -v
```

GPU first-boot on Nick (RTX 3060, 2026-11-01 Asia/Shanghai) **PASS** — see [docs/TESTING.md](docs/TESTING.md) and [docs/GPU_VERIFY_NICK_2026-11-01.md](docs/GPU_VERIFY_NICK_2026-11-01.md). Re-run the same checklist on any new CUDA host.

## Starting the server

```sh
cd /path/to/motionbricks
# Option 1: inject dependencies temporarily (recommended, leaves the repo env untouched)
uv run --with fastapi --with uvicorn \
    python /path/to/motionbricks-unity-bridge/server/motionbricks_server.py \
    --host 0.0.0.0 --port 8000

# Option 2: install into the repo environment
uv pip install -r /path/to/motionbricks-unity-bridge/server/requirements.txt
uv run python /path/to/motionbricks-unity-bridge/server/motionbricks_server.py
```

Environment variables: `MOTIONBRICKS_PRECISION=fp16` selects the mixed-precision ONNX (default fp32);
`MOTIONBRICKS_CACHE_DIR` changes the plan cache directory; `MOTIONBRICKS_FORCE_REBUILD=1` forces a plan rebuild.

Verify: `curl http://localhost:8000/info` should return the model name, the 30 joint names and the
coordinate-system notes; `curl http://localhost:8000/health` should return `{"ok": true}`.

## Unity setup (realtime client)

1. Copy the 5 `.cs` files from `unity/Scripts/` into your Unity project (any non-Editor folder).
2. Character requirements: a prefab with a Humanoid Avatar; leave the Animator's Controller **empty**
   (no other clip playing; disabling applyRootMotion also works, empty is simplest).
3. Attach to the character root: `SomaHumanoidMapper`, `MotionBricksClient`, `MotionBricksPlayer`
   (the latter two auto-resolve references on the same object; the mapper's animator auto-resolves too).
4. Set `MotionBricksClient.serverUrl`, e.g. `http://192.168.1.10:8000`.
5. `SomaHumanoidMapper.characterHeight`: 0 = auto-estimate (hips height × 2, approximate);
   measuring it manually is more accurate.
6. Demo: also attach `LocomotionDemo`, press Play, move with WASD / arrow keys, hold Shift to run.
7. Advanced: drive it from your own scripts via `player.SetLocomotionTarget(worldPos, speed)` /
   `player.SetTargetPose(pose)`; `player.PlayOneShot(clip)` plays an externally obtained clip.

## Offline clip baker ("motion factory")

The baker (`Tools > MotionBricks > Clip Baker`) turns MotionBricks into a motion factory:
generate once in the Editor, ship plain `.anim` files with your game.

**Why offline instead of realtime requests for mobile games?**
- Phones have no CUDA GPU — the model cannot run on-device.
- Cloud inference bills per concurrent GPU — prohibitive at game scale.
- Offline single-player games cannot depend on a network connection.

Baked clips are ordinary `AnimationClip`s: zero latency, zero server cost, playable offline,
version-controllable, and open to further editing.

**Workflow (3 steps):**
1. **Configure Takes** in the baker window:
   - *Pose Sequence mode* — pose your character in the scene, click "Capture current pose as target"
     for each key pose; the tool chains generations through them in order.
   - *Path mode* — drop empty GameObjects as waypoints; the tool walks the character root through
     each waypoint facing the travel direction ("segments per target" = 2–3 for longer walks).
2. **Bake All Takes** — the tool chains `/generate` requests (each new context = the last 4 generated
   frames), concatenates everything, and shows progress; a failed Take is logged without aborting the rest.
3. **Use the clips** — baked to `Assets/MotionBricks/Baked/<TakeName>.anim`; drag into your Animator.
   Options per Take: *in-place* (horizontal displacement removed, for code-driven movement) or
   *with displacement* (root motion baked into the Hips bone, for directly playable actions).

**Baked clip conventions** (generic transform curves, 30fps):
- Hips carries normalized root motion (origin subtracted, initial yaw removed) — play the clip with
  the character root at the origin.
- All other bones store the local transforms written by the mapper (Neck already includes the
  merged Neck2; bones missing on your character are skipped silently).
- Quaternion sign continuity is enforced across frames so curve interpolation never pops.

## HTTP protocol

`POST /generate`, `Content-Type: application/json`.

Request (exactly 4 context poses, 1–4 target poses; meters, Y-up right-handed = SOMA model space):

```json
{
  "context": [
    {
      "root": {"p": [0.0, 0.98, 0.0], "q": [0, 0, 0, 1]},
      "joints": [[0.0, 0.98, 0.0, 0, 0, 0, 1], [0.0, 0.10, 0.0, 0, 0, 0, 1], "...30 × 7 numbers..."]
    }
  ],
  "targets": ["...same format, 1-4 poses..."]
}
```

- One pose = `root` (world placement of Hips) + `joints` (local transforms of the 30 joints,
  each `[x, y, z, x, y, z, w]`, index 0 = Hips; the server trusts `root`).
- Invalid input (wrong joint count, out-of-range target count, non-finite numbers) → `400`;
  inference failure → `500` + error message.

Response:

```json
{
  "frames": ["...same pose format..."],
  "num_frames": 42,
  "sample_rate": 30
}
```

`num_frames` is the valid frame count (18–58, about 0.6–1.9 s); boundary frames are trimmed and
endpoint correction applied server-side, so frame 0 ≈ the last context pose and plays seamlessly.

## Joint mapping (SOMA index → Unity HumanBodyBones)

| SOMA | Unity | SOMA | Unity |
|---|---|---|---|
| 0 Hips | Hips | 15 LeftHandMiddleEnd | LeftMiddleDistal |
| 1 Spine1 | Spine | 16 RightShoulder | RightShoulder |
| 2 Spine2 | Chest | 17 RightArm | RightUpperArm |
| 3 Chest | UpperChest | 18 RightForeArm | RightLowerArm |
| 4 Neck1 | Neck (merged with Neck2 on playback) | 19 RightHand | RightHand |
| 5 Neck2 | (none) capture uses reference offset + identity; merged into Neck on playback | 20 RightHandThumbEnd | RightThumbDistal |
| 6 Head | Head | 21 RightHandMiddleEnd | RightMiddleDistal |
| 7 Jaw | Jaw (falls back if missing) | 22 LeftLeg | LeftUpperLeg |
| 8 LeftEye | LeftEye (falls back if missing) | 23 LeftShin | LeftLowerLeg |
| 9 RightEye | RightEye (falls back if missing) | 24 LeftFoot | LeftFoot |
| 10 LeftShoulder | LeftShoulder | 25 LeftToeBase | LeftToes |
| 11 LeftArm | LeftUpperArm | 26 RightLeg | RightUpperLeg |
| 12 LeftForeArm | LeftLowerArm | 27 RightShin | RightLowerLeg |
| 13 LeftHand | LeftHand | 28 RightFoot | RightFoot |
| 14 LeftHandThumbEnd | LeftThumbDistal | 29 RightToeBase | RightToes |

Coordinate systems: Unity left-handed Y-up ↔ SOMA right-handed Y-up via the standard mirror
transform (position `z → -z`, quaternion `(x,y,z,w) → (-x,-y,z,w)`, self-inverse);
see `SomaHumanoidMapper.MirrorPosition/MirrorRotation`.

## Limitations (must read)

1. **GPU first-boot verified on Nick (2026-11-01)**; Humanoid Clip Baker path later PASS on Nick with Timeline `DefaultMale` (see GPU verify addendum).
   On first deployment, `curl /info` first, then validate frame counts / motion sanity with a few
   small requests before wiring up Unity.
2. **Verify the mirror transform with a T-pose**: `z → -z` is the standard mirror assumption,
   but authoring orientation differs per character. Follow the three-step T-pose check in the
   `SomaHumanoidMapper` comments on first use; flip the mirror axis if left/right or
   front/back come out reversed.
3. **Retargeting is approximate**: positions are scaled by height ratio, rotations mapped directly.
   Very different proportions (chibi characters, non-human ratios) will show interpenetration /
   foot sliding — use a proper retargeting pipeline (e.g. Unity Animation Rigging or an offline
   retargeting tool); don't expect this bridge to solve it.
4. **The model is kinematic-only**: MotionBricks outputs joint poses with no physics feasibility
   guarantees; slight interpenetration can occur, more likely with fast / long-stride targets.
5. **Baked clips assume the character root at the origin on playback** (Hips motion is normalized);
   baked clips are generic transform clips, not Humanoid muscle-space clips.
6. Misc: one generation is 0.6–1.9 s, extended by chaining; server-side GPU inference is
   serialized (queue multiple characters or run more instances); LAN latency sets locomotion
   responsiveness, `regenerateCooldown` defaults to 0.5 s.

## License

- This bridge's code (`server/`, `unity/`): Apache 2.0.
- MotionBricks official code: Apache 2.0; **pretrained weights are under the NVIDIA Open Model
  License — commercial use requires attribution** and compliance with its trustworthy-AI terms.
  Weights ship as ONNX with the motionbricks repository and are not included in this project.
