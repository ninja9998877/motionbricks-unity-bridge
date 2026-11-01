#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""MotionBricks SOMA 推理 HTTP 服务（Unity 桥接的服务端）。

把 Unity 客户端发来的姿态 JSON 转成 MotionBricksSOMAEngine 的输入，
直接复用官方引擎的编码 / 推理 / 解码 / 端点修正流程（与官方 demo 完全一致），
再把生成的动作逐帧采样成 JSON 返回。

运行环境（GPU 主机）：
    NVIDIA GPU + CUDA 12 + TensorRT + Python 3.12，
    必须在 motionbricks 仓库的 uv 环境里运行（仓库自带 warp / tensorrt 等依赖）：

        cd /path/to/motionbricks
        uv run --with fastapi --with uvicorn \
            python /path/to/motionbricks_server.py --host 0.0.0.0 --port 8000

注意：首次启动会构建 TensorRT plan，需要几分钟；之后走 .trt-cache 缓存，启动很快。

环境变量：
    MOTIONBRICKS_PRECISION  fp32（默认）或 fp16（用仓库自带的混合精度 ONNX）
    MOTIONBRICKS_CACHE_DIR   TensorRT plan 缓存目录，默认 .trt-cache
    MOTIONBRICKS_FORCE_REBUILD  设为 1 时强制重建 plan
    MOTIONBRICKS_SKIP_ENGINE    设为 1 时跳过引擎加载（仅供契约测试；/generate 需注入假引擎）

协议见 README.md。未经 GPU 实测：本服务只能在有 NVIDIA GPU 的机器上运行，
作者的开发机没有 GPU，推理链路尚未端到端验证，首次部署请按 README 的验证步骤走。
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import threading
from contextlib import asynccontextmanager
from typing import Any

from fastapi import FastAPI, HTTPException

from protocol import (
    CONTEXT_POSES,
    DEFAULT_MODEL_NAME,
    GenerateRequest,
    JOINT_NAMES,
    MAX_TARGET_POSES,
    MIN_TARGET_POSES,
    NUM_JOINTS,
    PoseJson,
    SAMPLE_RATE,
    info_payload,
    validate_generate_counts,
    validate_pose,
    validate_transform,
)

log = logging.getLogger("motionbricks_server")

# SOMA 骨架的父子关系（按 JOINT_NAMES 的顺序索引）。
# 只用于构造服务端的 Skeleton 占位骨架：引擎要求骨架包含全部 30 个模型关节、
# root 在 index 0；参考姿态仅在有"悬空"关节时参与解码，本骨架全部连通，
# 因此这里的偏移只是合理近似值，不影响推理结果。
_JOINT_PARENTS = {
    "Hips": -1,
    "Spine1": 0, "Spine2": 1, "Chest": 2,
    "Neck1": 3, "Neck2": 4, "Head": 5, "Jaw": 6,
    "LeftEye": 6, "RightEye": 6,
    "LeftShoulder": 3, "LeftArm": 10, "LeftForeArm": 11, "LeftHand": 12,
    "LeftHandThumbEnd": 13, "LeftHandMiddleEnd": 13,
    "RightShoulder": 3, "RightArm": 16, "RightForeArm": 17, "RightHand": 18,
    "RightHandThumbEnd": 19, "RightHandMiddleEnd": 19,
    "LeftLeg": 0, "LeftShin": 22, "LeftFoot": 23, "LeftToeBase": 24,
    "RightLeg": 0, "RightShin": 26, "RightFoot": 27, "RightToeBase": 28,
}
# 参考姿态的局部位置偏移（Y-up，米，约 1.7m 人体近似值），旋转全为单位四元数。
_JOINT_OFFSETS = {
    "Hips": (0.00, 0.98, 0.00),
    "Spine1": (0.00, 0.10, 0.00), "Spine2": (0.00, 0.12, 0.00),
    "Chest": (0.00, 0.12, 0.00),
    "Neck1": (0.00, 0.10, 0.00), "Neck2": (0.00, 0.08, 0.00),
    "Head": (0.00, 0.08, 0.00), "Jaw": (0.00, -0.02, 0.06),
    "LeftEye": (-0.03, 0.04, 0.07), "RightEye": (0.03, 0.04, 0.07),
    "LeftShoulder": (-0.19, 0.05, 0.00), "LeftArm": (-0.09, -0.03, 0.00),
    "LeftForeArm": (-0.27, 0.00, 0.00), "LeftHand": (-0.25, 0.00, 0.00),
    "LeftHandThumbEnd": (-0.03, -0.02, 0.02), "LeftHandMiddleEnd": (-0.09, 0.00, 0.00),
    "RightShoulder": (0.19, 0.05, 0.00), "RightArm": (0.09, -0.03, 0.00),
    "RightForeArm": (0.27, 0.00, 0.00), "RightHand": (0.25, 0.00, 0.00),
    "RightHandThumbEnd": (0.03, -0.02, 0.02), "RightHandMiddleEnd": (0.09, 0.00, 0.00),
    "LeftLeg": (-0.10, -0.06, 0.00), "LeftShin": (0.00, -0.42, 0.00),
    "LeftFoot": (0.00, -0.42, 0.00), "LeftToeBase": (0.00, -0.05, 0.15),
    "RightLeg": (0.10, -0.06, 0.00), "RightShin": (0.00, -0.42, 0.00),
    "RightFoot": (0.00, -0.42, 0.00), "RightToeBase": (0.00, -0.05, 0.15),
}

assert list(_JOINT_PARENTS.keys()) == JOINT_NAMES, "JOINT_NAMES must match skeleton parent table"
assert list(_JOINT_OFFSETS.keys()) == JOINT_NAMES, "JOINT_NAMES must match skeleton offset table"

# ---------------------------------------------------------------------------
# 全局引擎状态（lifespan 里初始化；测试可注入）
# ---------------------------------------------------------------------------
_engine: Any | None = None
_skeleton: Any | None = None
_engine_lock = threading.Lock()
_model_name: str = DEFAULT_MODEL_NAME
_flatten_poses_kernel = None  # set on first real engine load


def set_engine_for_tests(engine: Any, skeleton: Any = True, model_name: str | None = None) -> None:
    """Inject a stub engine for GPU-free contract tests. skeleton may be any truthy sentinel."""
    global _engine, _skeleton, _model_name
    _engine = engine
    _skeleton = skeleton
    if model_name is not None:
        _model_name = model_name


def clear_engine_for_tests() -> None:
    global _engine, _skeleton
    _engine = None
    _skeleton = None


def _import_runtime():
    """Lazy-import warp + motionbricks (only needed on a real GPU host)."""
    import warp as wp
    from motionbricks.animation.skeleton import Skeleton
    from motionbricks.inference.builder import TensorRTBuildOptions
    from motionbricks.model.soma.contract import (
        SOMA_N1_CONTRACT,
        SOMA_N1_FP16_ONNX_PATH,
        SOMA_N1_FP32_ONNX_PATH,
        SOMA_N1_MANIFEST_PATH,
    )
    from motionbricks.model.soma.engine import MotionBricksSOMAEngine

    return {
        "wp": wp,
        "Skeleton": Skeleton,
        "TensorRTBuildOptions": TensorRTBuildOptions,
        "SOMA_N1_CONTRACT": SOMA_N1_CONTRACT,
        "SOMA_N1_FP16_ONNX_PATH": SOMA_N1_FP16_ONNX_PATH,
        "SOMA_N1_FP32_ONNX_PATH": SOMA_N1_FP32_ONNX_PATH,
        "SOMA_N1_MANIFEST_PATH": SOMA_N1_MANIFEST_PATH,
        "MotionBricksSOMAEngine": MotionBricksSOMAEngine,
    }


def _ensure_flatten_kernel(wp):
    global _flatten_poses_kernel
    if _flatten_poses_kernel is not None:
        return _flatten_poses_kernel

    @wp.kernel
    def _flatten_poses_kernel_impl(
        poses: wp.array2d(dtype=wp.transform),
        flat: wp.array3d(dtype=wp.float32),
    ):
        frame, joint = wp.tid()
        t = poses[frame, joint]
        flat[frame, joint, 0] = t.p[0]
        flat[frame, joint, 1] = t.p[1]
        flat[frame, joint, 2] = t.p[2]
        flat[frame, joint, 3] = t.q[0]
        flat[frame, joint, 4] = t.q[1]
        flat[frame, joint, 5] = t.q[2]
        flat[frame, joint, 6] = t.q[3]

    _flatten_poses_kernel = _flatten_poses_kernel_impl
    return _flatten_poses_kernel


def _build_skeleton(rt) -> Any:
    """构造与模型关节顺序一致的占位骨架（父子关系 + 近似参考姿态）。"""
    wp = rt["wp"]
    Skeleton = rt["Skeleton"]
    parent_indices = [_JOINT_PARENTS[name] for name in JOINT_NAMES]
    local = []
    for name in JOINT_NAMES:
        x, y, z = _JOINT_OFFSETS[name]
        local.append(wp.transform(wp.vec3(x, y, z), wp.quat(0.0, 0.0, 0.0, 1.0)))
    return Skeleton(JOINT_NAMES, parent_indices, local)


def _load_engine() -> tuple[Any, Any]:
    global _model_name
    rt = _import_runtime()
    contract_names = list(rt["SOMA_N1_CONTRACT"].joint_names)
    if contract_names != JOINT_NAMES:
        raise RuntimeError(
            f"protocol.JOINT_NAMES 与 SOMA_N1_CONTRACT 不一致：{contract_names!r}"
        )

    with open(rt["SOMA_N1_MANIFEST_PATH"], encoding="utf-8") as f:
        _model_name = json.load(f).get("name", DEFAULT_MODEL_NAME)

    precision = os.environ.get("MOTIONBRICKS_PRECISION", "fp32").lower()
    cache_dir = os.environ.get("MOTIONBRICKS_CACHE_DIR", ".trt-cache")
    force_rebuild = os.environ.get("MOTIONBRICKS_FORCE_REBUILD", "0") == "1"

    if precision == "fp16":
        onnx_path = rt["SOMA_N1_FP16_ONNX_PATH"]
        build_options = rt["TensorRTBuildOptions"](enable_fp16=True)
    elif precision == "fp32":
        onnx_path = rt["SOMA_N1_FP32_ONNX_PATH"]
        build_options = None
    else:
        raise ValueError(f"MOTIONBRICKS_PRECISION 只支持 fp32/fp16， got {precision!r}")

    log.info("正在加载 MotionBricks SOMA 引擎（%s，%s）…", _model_name, precision)
    log.info("首次启动会构建 TensorRT plan，需要几分钟，请耐心等待。")
    engine = rt["MotionBricksSOMAEngine"](
        onnx_path,
        build_options=build_options,
        cache_dir=cache_dir,
        force_rebuild=force_rebuild,
    )
    skeleton = _build_skeleton(rt)
    _ensure_flatten_kernel(rt["wp"])
    log.info("引擎加载完成，device=%s", engine.device)
    return engine, skeleton


@asynccontextmanager
async def _lifespan(app: FastAPI):
    global _engine, _skeleton
    if os.environ.get("MOTIONBRICKS_SKIP_ENGINE", "0") == "1":
        log.info("MOTIONBRICKS_SKIP_ENGINE=1：跳过引擎加载（契约测试模式）")
        yield
        return
    _engine, _skeleton = _load_engine()
    yield
    if _engine is not None:
        close = getattr(_engine, "close", None)
        if callable(close):
            close()
        _engine = None


app = FastAPI(title="MotionBricks Unity Bridge", lifespan=_lifespan)


def _pose_to_transforms(pose: PoseJson, index: int, wp) -> list[Any]:
    """一个 PoseJson → 30 个 wp.transform（index 0 为 root 的世界放置）。"""
    validate_pose(pose, index)
    transforms = [None] * NUM_JOINTS
    p, q = validate_transform(pose.root.p, pose.root.q, f"姿态 {index} 的 root")
    transforms[0] = wp.transform(wp.vec3(*p), wp.quat(*q))
    for j in range(1, NUM_JOINTS):
        vals = pose.joints[j]
        jp, jq = validate_transform(vals[:3], vals[3:], f"姿态 {index} 关节 {j}")
        transforms[j] = wp.transform(wp.vec3(*jp), wp.quat(*jq))
    return transforms


@app.get("/info")
def info() -> dict[str, Any]:
    return info_payload(_model_name)


@app.get("/health")
def health() -> dict[str, Any]:
    return {"ok": _engine is not None}


@app.post("/generate")
def generate(req: GenerateRequest) -> dict[str, Any]:
    """context（4 个姿态）+ targets（1~4 个姿态）→ 生成的动作帧。"""
    if _engine is None or _skeleton is None:
        raise HTTPException(status_code=503, detail="引擎尚未就绪")
    try:
        validate_generate_counts(len(req.context), len(req.targets))
    except ValueError as e:
        raise HTTPException(status_code=400, detail=str(e))

    # Stub / fake engines used in tests: expose generate_from_request and skip warp.
    stub = getattr(_engine, "generate_from_request", None)
    if callable(stub):
        try:
            for i, pose in enumerate(req.context):
                validate_pose(pose, i)
            for i, pose in enumerate(req.targets):
                validate_pose(pose, i)
        except ValueError as e:
            raise HTTPException(status_code=400, detail=str(e))
        try:
            with _engine_lock:
                return stub(req)
        except HTTPException:
            raise
        except Exception as e:
            log.exception("推理失败")
            raise HTTPException(status_code=500, detail=f"推理失败：{e}")

    try:
        import warp as wp
    except ImportError as e:
        raise HTTPException(status_code=503, detail=f"warp 不可用：{e}")

    try:
        context_transforms = [
            t for i, pose in enumerate(req.context)
            for t in _pose_to_transforms(pose, i, wp)
        ]
        target_transforms = [
            t for i, pose in enumerate(req.targets)
            for t in _pose_to_transforms(pose, i, wp)
        ]
    except ValueError as e:
        raise HTTPException(status_code=400, detail=str(e))

    try:
        with _engine_lock:
            engine, skeleton = _engine, _skeleton
            context = engine.create_input(skeleton)
            target = engine.create_input(skeleton)
            output = engine.create_output(skeleton)

            context.update_from_poses(
                wp.array(context_transforms, dtype=wp.transform, device=engine.device)
                .reshape((len(req.context), NUM_JOINTS)),
                stream=engine.stream,
            )
            target.update_from_poses(
                wp.array(target_transforms, dtype=wp.transform, device=engine.device)
                .reshape((len(req.targets), NUM_JOINTS)),
                stream=engine.stream,
            )
            engine.generate_animation(context, target, output)

            num_frames = output.num_frames
            flat = wp.empty((num_frames, NUM_JOINTS, 7), dtype=wp.float32, device=engine.device)
            wp.launch(
                _ensure_flatten_kernel(wp),
                dim=(num_frames, NUM_JOINTS),
                inputs=[output.poses, flat],
                device=engine.device,
                stream=engine.stream,
            )
            engine.synchronize()
            frames_np = flat.numpy()
    except HTTPException:
        raise
    except Exception as e:
        log.exception("推理失败")
        raise HTTPException(status_code=500, detail=f"推理失败：{e}")

    frames: list[dict[str, Any]] = []
    for f in range(num_frames):
        joints = [frames_np[f, j].tolist() for j in range(NUM_JOINTS)]
        root = joints[0]
        frames.append({
            "root": {"p": root[:3], "q": root[3:]},
            "joints": joints,
        })
    return {"frames": frames, "num_frames": num_frames, "sample_rate": SAMPLE_RATE}


def main() -> None:
    parser = argparse.ArgumentParser(description="MotionBricks SOMA 推理 HTTP 服务")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8000)
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    import uvicorn

    uvicorn.run(app, host=args.host, port=args.port, log_level="info")


if __name__ == "__main__":
    main()
