# MotionBricks ↔ Unity 桥接

用 NVIDIA MotionBricks（SIGGRAPH 2026，实时动作生成模型）驱动 Unity Humanoid 角色。两种用法：

- **实时客户端**——**Python FastAPI 推理服务**（跑在有 NVIDIA GPU 的主机上，直接复用官方
  `MotionBricksSOMAEngine`，保证编码 / 推理 / 解码 / 端点修正与官方 demo 完全一致）
  + **Unity C# 客户端**（HTTP 调用、Humanoid 骨骼映射、30fps 播放与无限链式生成）。
- **离线烘焙**（"动作工厂"编辑器工具）——在 Unity 编辑器里调用同一个推理服务，
  把生成的动作烘焙成 `.anim` 文件。**手机游戏推荐走这条路**：运行时零推理、零网络、零 GPU 成本。

> English version: [README.md](README.md)

```
┌─────────────────────────────┐         HTTP/JSON          ┌──────────────────────────────┐
│ Unity 客户端                 │   POST /generate           │ 推理服务（GPU 主机）          │
│                             │ ─────────────────────────► │                              │
│ SomaHumanoidMapper           │   {context: 4姿态,         │ motionbricks_server.py       │
│   抓取 Humanoid 姿态         │    targets: 1~4姿态}       │   MotionBricksSOMAEngine     │
│   Unity左手系 → SOMA右手系   │                            │   (官方引擎，FP32/FP16 ONNX)  │
│                             │ ◄───────────────────────── │                              │
│ MotionBricksPlayer          │   {frames: [...],          │ 直接调 create_input /        │
│   30fps 播放 + 过渡点        │    num_frames,            │ update_from_poses /          │
│   自动链式生成               │    sample_rate: 30}       │ generate_animation / sample  │
│                             │                            │                              │
│ LocomotionDemo              │                            │ 首次启动构建 TensorRT plan   │
│   WASD → 目标点 → 循环生成   │                            │ （几分钟，之后走 .trt-cache） │
│                             │                            │                              │
│ 编辑器：Clip Baker           │                            │                              │
│   Tools > MotionBricks >     │                            │                              │
│   Clip Baker（离线烘焙）     │                            │                              │
└─────────────────────────────┘                            └──────────────────────────────┘
```

## 目录结构

```
motionbricks-unity-bridge/
├── README.md                        # 英文版
├── README.zh-CN.md                  # 中文版（本文件）
├── CHANGELOG.md                     # 变更记录
├── LICENSE                          # Apache 2.0
├── docs/
│   └── TESTING.md                   # 无 GPU 契约测说明
├── server/
│   ├── protocol.py                  # 无 GPU 的协议模型与校验（测试共用）
│   ├── motionbricks_server.py       # FastAPI 推理服务（懒加载 warp/motionbricks）
│   ├── requirements.txt             # fastapi + uvicorn（装法见下）
│   ├── requirements-dev.txt         # pytest + httpx（契约测）
│   └── tests/                       # 无 GPU 契约测（TestClient + 假引擎）
└── unity/
    ├── Scripts/                     # 运行时脚本（拷进任意非 Editor 目录）
    │   ├── SomaPose.cs              # 姿态数据结构 + 手写 JSON 编解码（无第三方依赖）
    │   ├── MotionBricksClient.cs    # HTTP 客户端（UnityWebRequest，async Task 接口）
    │   ├── SomaHumanoidMapper.cs    # SOMA 30关节 ↔ HumanBodyBones 映射 + 抓取/回放
    │   ├── MotionBricksPlayer.cs    # 30fps 播放器 + 过渡点自动链式生成
    │   └── LocomotionDemo.cs        # WASD 演示控制器
    └── Editor/                      # 仅编辑器（必须放在 Editor 目录下）
        ├── MotionBricksClipBaker.cs # 烘焙窗口：Tools > MotionBricks > Clip Baker
        └── MotionBricksBakeRunner.cs# 烘焙时 HTTP 请求的隐藏协程驱动器
```

## 环境要求

服务端（GPU 主机）：
- NVIDIA GPU、CUDA 12、TensorRT、Python 3.12
- motionbricks 仓库（`git clone https://github.com/NVlabs/motionbricks.git`）及其 uv 环境
  （warp / tensorrt 等重型依赖只能从仓库环境来）

客户端：
- Unity 2021.3+，角色带 Humanoid Avatar；只用 UnityWebRequest + 手写 JSON，不依赖第三方包

## 测试

无 GPU 契约测说明见 [docs/TESTING.md](docs/TESTING.md)：

```sh
python3 -m venv .venv
.venv/bin/pip install -r server/requirements-dev.txt
.venv/bin/pytest server/tests -v
```

Nick（RTX 3060，2026-11-01 Asia/Shanghai）GPU 首启已 **PASS**——见 [docs/TESTING.md](docs/TESTING.md) 与 [docs/GPU_VERIFY_NICK_2026-11-01.md](docs/GPU_VERIFY_NICK_2026-11-01.md)。换新 CUDA 主机请重跑同一清单。

## 服务端启动

```sh
cd /path/to/motionbricks
# 方式一：临时注入依赖（推荐，不动仓库环境）
uv run --with fastapi --with uvicorn \
    python /path/to/motionbricks-unity-bridge/server/motionbricks_server.py \
    --host 0.0.0.0 --port 8000

# 方式二：装进仓库环境
uv pip install -r /path/to/motionbricks-unity-bridge/server/requirements.txt
uv run python /path/to/motionbricks-unity-bridge/server/motionbricks_server.py
```

环境变量：`MOTIONBRICKS_PRECISION=fp16` 切混合精度 ONNX（默认 fp32）；
`MOTIONBRICKS_CACHE_DIR` 改 plan 缓存目录；`MOTIONBRICKS_FORCE_REBUILD=1` 强制重建 plan。

验证：`curl http://localhost:8000/info` 应返回模型名、30 个关节名、坐标系说明；
`curl http://localhost:8000/health` 应返回 `{"ok": true}`。

## Unity 导入与挂载（实时客户端）

1. 把 `unity/Scripts/` 下 5 个 `.cs` 文件拷进 Unity 项目（任意 Editor 外目录）。
2. 角色要求：带 Humanoid Avatar 的角色预制体，Animator 组件的 Controller **留空**
   （不要有其他 Clip 在播；applyRootMotion 关掉也行，留空最省心）。
3. 在角色根物体上挂载：`SomaHumanoidMapper`、`MotionBricksClient`、`MotionBricksPlayer`
  （后两者留空会自动取本物体的引用；mapper 的 animator 留空自动取本物体）。
4. `MotionBricksClient.serverUrl` 填服务端地址，如 `http://192.168.1.10:8000`。
5. `SomaHumanoidMapper.characterHeight`：0 = 自动估算（Hips 高 × 2，近似），
   最好手动量一下填准确值。
6. 演示：再挂 `LocomotionDemo`，运行后 WASD/方向键移动，Shift 跑。
7. 进阶：自己写脚本调 `player.SetLocomotionTarget(worldPos, speed)` /
   `player.SetTargetPose(pose)` 即可驱动；`player.PlayOneShot(clip)` 可直接播外部 clip。

## 离线烘焙（"动作工厂"）

烘焙工具（`Tools > MotionBricks > Clip Baker`）把 MotionBricks 当"动作工厂"用：
在编辑器里生成一次，产出普通的 `.anim` 文件随游戏发布。

**为什么手机游戏用离线烘焙，而不是实时请求？**
- 手机没有 CUDA GPU，模型在端上根本跑不动；
- 走云端的话，按并发玩家数收 GPU 推理费用，规模稍大就扛不住；
- 单机游戏不能依赖网络，断网即不可用是致命的。

烘焙出的 clip 就是普通 `AnimationClip`：零延迟、零服务器成本、断网照玩，
还能进版本管理、做二次剪辑。

**工作流（三步走）：**
1. **配 Take**：姿态序列模式——在场景里摆好角色，逐个点"从场景抓取当前姿态为目标"，
   工具按顺序链式生成并拼接；路径模式——拖若干空 GameObject 做 waypoint，
   工具让角色 root 依次走到各点并面朝行进方向（"每目标生成段数"设 2~3 可走长距离）。
2. **开始烘焙**：工具链式请求服务端（每次用已生成帧的最后 4 帧做 context），
   拼成完整动作，进度条实时显示；单个 Take 失败只记错误，不中断后面的 Take。
3. **拿去用**：clip 存到 `Assets/MotionBricks/Baked/<Take名>.anim`，直接拖进 Animator。
   每个 Take 可选：原地（去掉水平位移，适合代码驱动移动）/ 带位移
   （根运动烘进 Hips，适合直接播放的动作）。

**烘焙 clip 的约定**（generic transform 曲线，30fps）：
- Hips 承载归一化后的根运动（减掉起点、去掉初始 yaw）——播放时请把角色根放在原点；
- 其余骨骼记 ApplyPose 写下的本地变换（Neck 已含 Neck2 的合并结果；
  角色缺失的骨骼自动跳过，不报错）；
- 写入时做了四元数符号连续性修正，曲线插值不会抽搐。

## HTTP 协议

`POST /generate`，`Content-Type: application/json`。

请求（context 恰好 4 个姿态，targets 1~4 个；单位米、Y-up 右手系 = SOMA 模型空间）：

```json
{
  "context": [
    {
      "root": {"p": [0.0, 0.98, 0.0], "q": [0, 0, 0, 1]},
      "joints": [[0.0, 0.98, 0.0, 0, 0, 0, 1], [0.0, 0.10, 0.0, 0, 0, 0, 1], "...30个×7个数..."]
    }
  ],
  "targets": ["...同上格式，1~4个..."]
}
```

- 每个姿态 = `root`（Hips 的世界放置）+ `joints`（30 个关节局部变换，
  每个 `[x, y, z, x, y, z, w]`，index 0 = Hips，服务端以 `root` 为准）。
- 非法输入（关节数不对、targets 越界、非有限数值）→ `400`；推理异常 → `500` + 错误信息。

响应：

```json
{
  "frames": ["...与请求姿态同格式..."],
  "num_frames": 42,
  "sample_rate": 30
}
```

`num_frames` 为有效帧数（18~58，约 0.6~1.9 秒）；服务端已做边界帧去除与端点修正，
第 0 帧 ≈ context 末姿态，可直接续播。

## 关节映射表（SOMA index → Unity HumanBodyBones）

| SOMA | Unity | SOMA | Unity |
|---|---|---|---|
| 0 Hips | Hips | 15 LeftHandMiddleEnd | LeftMiddleDistal |
| 1 Spine1 | Spine | 16 RightShoulder | RightShoulder |
| 2 Spine2 | Chest | 17 RightArm | RightUpperArm |
| 3 Chest | UpperChest | 18 RightForeArm | RightLowerArm |
| 4 Neck1 | Neck（回放时与 Neck2 合并写入） | 19 RightHand | RightHand |
| 5 Neck2 | （无）抓取填参考偏移+单位旋转；回放并入 Neck | 20 RightHandThumbEnd | RightThumbDistal |
| 6 Head | Head | 21 RightHandMiddleEnd | RightMiddleDistal |
| 7 Jaw | Jaw（缺失则回退） | 22 LeftLeg | LeftUpperLeg |
| 8 LeftEye | LeftEye（缺失则回退） | 23 LeftShin | LeftLowerLeg |
| 9 RightEye | RightEye（缺失则回退） | 24 LeftFoot | LeftFoot |
| 10 LeftShoulder | LeftShoulder | 25 LeftToeBase | LeftToes |
| 11 LeftArm | LeftUpperArm | 26 RightLeg | RightUpperLeg |
| 12 LeftForeArm | LeftLowerArm | 27 RightShin | RightLowerLeg |
| 13 LeftHand | LeftHand | 28 RightFoot | RightFoot |
| 14 LeftHandThumbEnd | LeftThumbDistal | 29 RightToeBase | RightToes |

坐标系：Unity 左手系 Y-up ↔ SOMA 右手系 Y-up，采用标准镜像转换
（位置 `z → -z`，四元数 `(x,y,z,w) → (-x,-y,z,w)`，自逆），
见 `SomaHumanoidMapper.MirrorPosition/MirrorRotation`。

## 已知限制（必读）

1. **GPU 首启已在 Nick（2026-11-01）验证**；随后 Humanoid Clip Baker 路径已在 Nick 用 Timeline `DefaultMale` 验过（见 GPU 验收补记）。
   换新机器部署时仍建议先 `curl /info`，再用少量请求确认帧数/动作合理性后再接 Unity。
2. **坐标系镜像转换需用 T-pose 验证**：`z → -z` 是标准镜像假设，
   但不同角色的建模朝向可能不同。首次使用请按 `SomaHumanoidMapper` 注释里的三步
   用 T-pose 对照验证，左右/前后反了就调整镜像函数。
3. **重定向是近似方案**：只按身高比缩放位置、旋转直接映射。
   体型差异大（如 Q 版角色、非人比例）会出现穿插/脚滑，需要专业重定向管线
   （如 Unity Animation Rigging 或离线重定向工具），不要指望本桥接搞定。
4. **模型只管运动学**：MotionBricks 生成的是关节姿态，不管物理 feasibility；
   生成动作可能出现轻微穿插，高速/大步幅目标更容易出现。
5. **烘焙 clip 播放时要求角色根在原点**（Hips 位移已归一化）；
   烘焙出的是 generic transform 曲线，不是 Humanoid muscle 空间。
6. 其他：单次生成 0.6~1.9 秒，靠链式续接；服务端 GPU 推理串行化（多角色请排队
   或起多实例）；局域网延迟决定 locomotion 响应速度，`regenerateCooldown`
   默认 0.5 秒。

## 许可证

- 本桥接代码（`server/`、`unity/`）：Apache 2.0。
- MotionBricks 官方代码：Apache 2.0；**预训练权重遵循 NVIDIA Open Model License，
  商用需署名**并遵守其 trustworthy-AI 条款。权重随 motionbricks 仓库自带 ONNX 分发，
  不包含在本项目内。
