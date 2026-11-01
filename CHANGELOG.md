# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed
- `MotionBricksBakeRunner`: do not `yield` inside `try/catch` (CS1626 on Unity 2022+); segment errors use `_segmentError` instead

### Verified
- **2026-11-01 Asia/Shanghai — Nick PC GPU first-boot PASS** (RTX 3060): `/health` `/info` `/generate`, TensorRT ~116 s first plan then cache; Unity 2022.3.62f3 scripts compile + Editor generate smoke + root `.anim` write. Full Humanoid baker Play Mode skipped (no Humanoid asset). See [docs/GPU_VERIFY_NICK_2026-11-01.md](docs/GPU_VERIFY_NICK_2026-11-01.md).

### Planned
- T-pose mirror-axis verification checklist automation notes for `SomaHumanoidMapper`
- Optional CI job for `pytest` + `py_compile` (no GPU)
- Humanoid end-to-end Clip Baker bake once a character asset is available

## [0.1.0] - 2026-10-31

Initial tagged release of the MotionBricks ↔ Unity bridge.

### Added
- FastAPI inference server (`server/motionbricks_server.py`) wrapping official `MotionBricksSOMAEngine`
  (context ×4 + targets ×1–4 → generated frames at 30 Hz)
- GPU-free protocol module (`server/protocol.py`): joint-name constants, request models, validation
  shared with the HTTP layer
- GPU-free contract tests (`server/tests/`): invalid context/targets → 400, JSON round-trip,
  Python mirror of `SomaPose.Validate`, stub-engine `/health` `/info` `/generate` via TestClient
- Unity runtime scripts: `SomaPose` / wire JSON, `MotionBricksClient`, `SomaHumanoidMapper`,
  `MotionBricksPlayer`, `LocomotionDemo`
- Editor offline Clip Baker (`MotionBricksClipBaker` + `MotionBricksBakeRunner`) to bake `.anim`
- English + Chinese READMEs, Apache 2.0 license for bridge code
- Testing guide: [docs/TESTING.md](docs/TESTING.md)

### Known limits
- Inference GPU first-boot was later verified on Nick (2026-11-01); see Unreleased / docs. Retarget still approximate.
- Retargeting is approximate (height-ratio scale + direct rotation map); large proportion mismatches
  need a proper retarget pipeline.
- Model output is kinematic-only (no physics feasibility).
- No automated CI was present before this release; contract tests are local/`pytest` only.
- MotionBricks pretrained weights remain under the NVIDIA Open Model License (not shipped here).

[Unreleased]: https://github.com/ninja9998877/motionbricks-unity-bridge/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/ninja9998877/motionbricks-unity-bridge/releases/tag/v0.1.0
