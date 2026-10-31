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

协议见 README.md。未经 GPU 实测：本服务只能在有 NVIDIA GPU 的机器上运行，
作者的开发机没有 GPU，推理链路尚未端到端验证，首次部署请按 README 的验证步骤走。
"""

from __future__ import annotations

import argparse
import json
import logging
import math
import os
import threading
from contextlib import asynccontextmanager
from typing import Any

import warp as wp
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

from motionbricks.animation.skeleton import Skeleton
from motionbricks.inference.builder import TensorRTBuildOptions
from motionbricks.model.soma.contract import (
    SOMA_N1_CONTRACT,
    SOMA_N1_FP16_ONNX_PATH,
    SOMA_N1_FP32_ONNX_PATH,
    SOMA_N1_MANIFEST_PATH,
)
from motionbricks.model.soma.engine import MotionBricksSOMAEngine

log = logging.getLogger("motionbricks_server")

# ---------------------------------------------------------------------------
# 模型常量（来自仓库 manifest：30 个关节，root 为 index 0，30Hz）
# ---------------------------------------------------------------------------
JOINT_NAMES: list[str] = list(SOMA_N1_CONTRACT.joint_names)
NUM_JOINTS = len(JOINT_NAMES)          # 30
SAMPLE_RATE = 30.0
CONTEXT_POSES = 4                      # context 固定 4 个姿态
MIN_TARGET_POSES = 1
MAX_TARGET_POSES = 4

with open(SOMA_N1_MANIFEST_PATH, encoding="utf-8") as _f:
    MODEL_NAME = json.load(_f).get("name", "soma_n1")

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


# ---------------------------------------------------------------------------
# warp 小 kernel：把输出 buffer 的有效帧展平成 [帧, 关节, 7] 的 float 数组
# ---------------------------------------------------------------------------
@wp.kernel
def _flatten_poses_kernel(
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


# ---------------------------------------------------------------------------
# 请求 / 响应模型
# ---------------------------------------------------------------------------
class TransformJson(BaseModel):
    p: list[float]  # [x, y, z]，米
    q: list[float]  # [x, y, z, w] 四元数


class PoseJson(BaseModel):
    # root：姿态的世界放置（Hips 的世界变换，即 index 0 的关节变换）；
    # joints：30 个关节的局部变换，index 0 为 Hips（其局部变换即世界放置，
    #         服务端以 root 字段为准，joints[0] 会被 root 覆盖）。
    root: TransformJson
    joints: list[list[float]]  # 30 × [x, y, z, x, y, z, w]


class GenerateRequest(BaseModel):
    context: list[PoseJson]  # 必须 4 个
    targets: list[PoseJson]  # 1~4 个


# ---------------------------------------------------------------------------
# 全局引擎状态（lifespan 里初始化）
# ---------------------------------------------------------------------------
_engine: MotionBricksSOMAEngine | None = None
_skeleton: Skeleton | None = None
_engine_lock = threading.Lock()


def _build_skeleton() -> Skeleton:
    """构造与模型关节顺序一致的占位骨架（父子关系 + 近似参考姿态）。"""
    parent_indices = [_JOINT_PARENTS[name] for name in JOINT_NAMES]
    local = []
    for name in JOINT_NAMES:
        x, y, z = _JOINT_OFFSETS[name]
        local.append(wp.transform(wp.vec3(x, y, z), wp.quat(0.0, 0.0, 0.0, 1.0)))
    return Skeleton(JOINT_NAMES, parent_indices, local)


def _load_engine() -> tuple[MotionBricksSOMAEngine, Skeleton]:
    precision = os.environ.get("MOTIONBRICKS_PRECISION", "fp32").lower()
    cache_dir = os.environ.get("MOTIONBRICKS_CACHE_DIR", ".trt-cache")
    force_rebuild = os.environ.get("MOTIONBRICKS_FORCE_REBUILD", "0") == "1"

    if precision == "fp16":
        # 混合精度 ONNX：敏感算子保持 FP32，输入输出仍是 FP32。
        onnx_path = SOMA_N1_FP16_ONNX_PATH
        build_options = TensorRTBuildOptions(enable_fp16=True)
    elif precision == "fp32":
        onnx_path = SOMA_N1_FP32_ONNX_PATH
        build_options = None
    else:
        raise ValueError(f"MOTIONBRICKS_PRECISION 只支持 fp32/fp16， got {precision!r}")

    log.info("正在加载 MotionBricks SOMA 引擎（%s，%s）…", MODEL_NAME, precision)
    log.info("首次启动会构建 TensorRT plan，需要几分钟，请耐心等待。")
    # SOMA 模型空间本身就是 Y-up，与协议一致，用默认的 identity model_to_runtime。
    engine = MotionBricksSOMAEngine(
        onnx_path,
        build_options=build_options,
        cache_dir=cache_dir,
        force_rebuild=force_rebuild,
    )
    skeleton = _build_skeleton()
    log.info("引擎加载完成，device=%s", engine.device)
    return engine, skeleton


@asynccontextmanager
async def _lifespan(app: FastAPI):
    global _engine, _skeleton
    _engine, _skeleton = _load_engine()
    yield
    if _engine is not None:
        _engine.close()
        _engine = None


app = FastAPI(title="MotionBricks Unity Bridge", lifespan=_lifespan)


# ---------------------------------------------------------------------------
# JSON → warp 变换
# ---------------------------------------------------------------------------
def _parse_transform(p: list[float], q: list[float], what: str) -> wp.transform:
    if len(p) != 3 or len(q) != 4:
        raise ValueError(f"{what}：p 须 3 个数、q 须 4 个数")
    if not all(math.isfinite(v) for v in (*p, *q)):
        raise ValueError(f"{what}：含非有限数值")
    norm = math.sqrt(sum(v * v for v in q))
    if norm < 1e-8:
        raise ValueError(f"{what}：四元数接近零向量")
    q = [v / norm for v in q]  # 归一化，避免非法旋转污染推理
    return wp.transform(wp.vec3(*p), wp.quat(*q))


def _pose_to_transforms(pose: PoseJson, index: int) -> list[wp.transform]:
    """一个 PoseJson → 30 个 wp.transform（index 0 为 root 的世界放置）。"""
    if len(pose.joints) != NUM_JOINTS:
        raise ValueError(f"姿态 {index}：joints 须 {NUM_JOINTS} 个，got {len(pose.joints)}")
    transforms = [None] * NUM_JOINTS
    transforms[0] = _parse_transform(pose.root.p, pose.root.q, f"姿态 {index} 的 root")
    for j in range(1, NUM_JOINTS):
        vals = pose.joints[j]
        if len(vals) != 7:
            raise ValueError(f"姿态 {index} 关节 {j}：须 7 个数，got {len(vals)}")
        transforms[j] = _parse_transform(vals[:3], vals[3:], f"姿态 {index} 关节 {j}")
    # joints[0] 视为冗余：以 root 为准（两者在正确客户端里本就一致）。
    return transforms


# ---------------------------------------------------------------------------
# 路由
# ---------------------------------------------------------------------------
@app.get("/info")
def info() -> dict[str, Any]:
    return {
        "model": MODEL_NAME,
        "rig": "SOMA",
        "sample_rate": SAMPLE_RATE,
        "num_joints": NUM_JOINTS,
        "joint_names": JOINT_NAMES,
        "coordinate_system": (
            "Y-up 右手系，米制；即 SOMA 模型空间。"
            "每个姿态 30 个关节局部变换，index 0（Hips）为 root，其局部变换即世界放置。"
        ),
        "context_poses": CONTEXT_POSES,
        "target_pose_range": [MIN_TARGET_POSES, MAX_TARGET_POSES],
    }


@app.get("/health")
def health() -> dict[str, Any]:
    return {"ok": _engine is not None}


@app.post("/generate")
def generate(req: GenerateRequest) -> dict[str, Any]:
    """context（4 个姿态）+ targets（1~4 个姿态）→ 生成的动作帧。"""
    if _engine is None or _skeleton is None:
        raise HTTPException(status_code=503, detail="引擎尚未就绪")
    if len(req.context) != CONTEXT_POSES:
        raise HTTPException(
            status_code=400,
            detail=f"context 须恰好 {CONTEXT_POSES} 个姿态，got {len(req.context)}",
        )
    if not MIN_TARGET_POSES <= len(req.targets) <= MAX_TARGET_POSES:
        raise HTTPException(
            status_code=400,
            detail=f"targets 须 {MIN_TARGET_POSES}~{MAX_TARGET_POSES} 个姿态，got {len(req.targets)}",
        )

    try:
        context_transforms = [
            t for i, pose in enumerate(req.context)
            for t in _pose_to_transforms(pose, i)
        ]
        target_transforms = [
            t for i, pose in enumerate(req.targets)
            for t in _pose_to_transforms(pose, i)
        ]
    except ValueError as e:
        raise HTTPException(status_code=400, detail=str(e))

    try:
        with _engine_lock:  # GPU 推理串行化，避免并发请求互相踩 buffer
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
            # generate_animation 内部已 synchronize；下面再读有效帧并展平。

            num_frames = output.num_frames
            flat = wp.empty((num_frames, NUM_JOINTS, 7), dtype=wp.float32, device=engine.device)
            wp.launch(
                _flatten_poses_kernel,
                dim=(num_frames, NUM_JOINTS),
                inputs=[output.poses, flat],
                device=engine.device,
                stream=engine.stream,
            )
            engine.synchronize()
            frames_np = flat.numpy()  # [F, 30, 7]，float32
    except HTTPException:
        raise
    except Exception as e:  # 推理异常 → 500 并返回错误信息
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


# ---------------------------------------------------------------------------
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
