// SOMA 30 关节 ↔ Unity Humanoid 骨骼的映射，以及姿态抓取 / 回放。
// 挂在与 Animator 同一个 GameObject 上（该物体即角色根节点）。
using System;
using UnityEngine;

namespace MotionBricksBridge
{
    /// <summary>
    /// 职责：
    /// 1) CapturePose：从 Humanoid 角色抓取当前姿态，转为 SOMA 模型空间（Y-up 右手系，米制）；
    /// 2) ApplyPose：把模型空间的生成姿态写回角色；
    /// 3) 简单的比例重定向：按角色身高 / SOMA 参考身高缩放位置，旋转直接映射（近似方案）。
    ///
    /// 关键约定：
    /// - SOMA 骨架的根是 Hips（index 0），它没有父节点，其"局部变换"就是世界放置。
    ///   Unity 里角色根 GameObject 与 Hips 骨骼之间多了一层（Hips 本地偏移 o）。
    ///   ApplyPose 时根物体的变换按 R = W · o⁻¹ 求解，保证 Hips 骨骼的世界变换恰好等于
    ///   模型输出的 W；CapturePose 时直接读 Hips 骨骼的世界变换。两者严格互逆。
    /// - Animator 的 Controller 请留空（或关掉 applyRootMotion），不要有其他 Clip 在播，
    ///   否则会跟 ApplyPose 打架。
    /// </summary>
    public class SomaHumanoidMapper : MonoBehaviour
    {
        [Tooltip("带 Humanoid Avatar 的 Animator，留空则自动取本物体的")]
        public Animator animator;

        [Tooltip("SOMA 参考身高（米）。服务端模型按约 1.7m 人体训练。")]
        public float somaReferenceHeight = 1.7f;

        [Tooltip("角色身高（米）。0 = 自动估算（Hips 高度 × 2，近似值），也可在 Inspector 手动填写更准的值。")]
        public float characterHeight = 0f;

        // 与服务端 manifest 完全一致的关节顺序（index 0 = Hips = root）。
        public static readonly string[] SomaJointNames =
        {
            "Hips", "Spine1", "Spine2", "Chest", "Neck1", "Neck2", "Head", "Jaw",
            "LeftEye", "RightEye",
            "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
            "LeftHandThumbEnd", "LeftHandMiddleEnd",
            "RightShoulder", "RightArm", "RightForeArm", "RightHand",
            "RightHandThumbEnd", "RightHandMiddleEnd",
            "LeftLeg", "LeftShin", "LeftFoot", "LeftToeBase",
            "RightLeg", "RightShin", "RightFoot", "RightToeBase",
        };

        private const int Neck1Index = 4;
        private const int Neck2Index = 5;

        // SOMA 关节 index → Unity HumanBodyBones。null = Unity 无此骨骼：
        // - Neck2：抓取时填参考偏移 + 单位旋转（颈段不折叠）；回放时按公式并入 Neck，
        //          保证 Head 的世界变换与模型输出一致（见 ApplyPose 注释）。
        // - 手指末端 / 眼睛 / 下巴：部分 Humanoid 模型没有这些骨骼，缺失时抓取用参考姿态，
        //   回放时跳过（不报错）。
        private static readonly HumanBodyBones?[] SomaToHumanoid =
        {
            HumanBodyBones.Hips,            // 0 Hips
            HumanBodyBones.Spine,           // 1 Spine1
            HumanBodyBones.Chest,           // 2 Spine2
            HumanBodyBones.UpperChest,      // 3 Chest
            HumanBodyBones.Neck,            // 4 Neck1
            null,                           // 5 Neck2（无对应骨骼，特殊处理）
            HumanBodyBones.Head,            // 6 Head
            HumanBodyBones.Jaw,             // 7 Jaw
            HumanBodyBones.LeftEye,         // 8 LeftEye
            HumanBodyBones.RightEye,        // 9 RightEye
            HumanBodyBones.LeftShoulder,    // 10 LeftShoulder
            HumanBodyBones.LeftUpperArm,    // 11 LeftArm
            HumanBodyBones.LeftLowerArm,    // 12 LeftForeArm
            HumanBodyBones.LeftHand,        // 13 LeftHand
            HumanBodyBones.LeftThumbDistal, // 14 LeftHandThumbEnd
            HumanBodyBones.LeftMiddleDistal,// 15 LeftHandMiddleEnd
            HumanBodyBones.RightShoulder,   // 16 RightShoulder
            HumanBodyBones.RightUpperArm,   // 17 RightArm
            HumanBodyBones.RightLowerArm,   // 18 RightForeArm
            HumanBodyBones.RightHand,       // 19 RightHand
            HumanBodyBones.RightThumbDistal,// 20 RightHandThumbEnd
            HumanBodyBones.RightMiddleDistal,// 21 RightHandMiddleEnd
            HumanBodyBones.LeftUpperLeg,    // 22 LeftLeg
            HumanBodyBones.LeftLowerLeg,    // 23 LeftShin
            HumanBodyBones.LeftFoot,        // 24 LeftFoot
            HumanBodyBones.LeftToes,        // 25 LeftToeBase
            HumanBodyBones.RightUpperLeg,   // 26 RightLeg
            HumanBodyBones.RightLowerLeg,   // 27 RightShin
            HumanBodyBones.RightFoot,       // 28 RightFoot
            HumanBodyBones.RightToes,       // 29 RightToeBase
        };

        // 参考局部位置偏移（模型空间，Y-up，米），与服务端 Skeleton 的占位参考姿态一致。
        // 用途：缺失骨骼的回退值；Neck2 抓取时的颈段长度（保持颈部比例，避免折叠）。
        private static readonly Vector3[] ReferenceOffsets =
        {
            new Vector3( 0.00f, 0.98f, 0.00f), // Hips
            new Vector3( 0.00f, 0.10f, 0.00f), // Spine1
            new Vector3( 0.00f, 0.12f, 0.00f), // Spine2
            new Vector3( 0.00f, 0.12f, 0.00f), // Chest
            new Vector3( 0.00f, 0.10f, 0.00f), // Neck1
            new Vector3( 0.00f, 0.08f, 0.00f), // Neck2
            new Vector3( 0.00f, 0.08f, 0.00f), // Head
            new Vector3( 0.00f,-0.02f, 0.06f), // Jaw
            new Vector3(-0.03f, 0.04f, 0.07f), // LeftEye
            new Vector3( 0.03f, 0.04f, 0.07f), // RightEye
            new Vector3(-0.19f, 0.05f, 0.00f), // LeftShoulder
            new Vector3(-0.09f,-0.03f, 0.00f), // LeftArm
            new Vector3(-0.27f, 0.00f, 0.00f), // LeftForeArm
            new Vector3(-0.25f, 0.00f, 0.00f), // LeftHand
            new Vector3(-0.03f,-0.02f, 0.02f), // LeftHandThumbEnd
            new Vector3(-0.09f, 0.00f, 0.00f), // LeftHandMiddleEnd
            new Vector3( 0.19f, 0.05f, 0.00f), // RightShoulder
            new Vector3( 0.09f,-0.03f, 0.00f), // RightArm
            new Vector3( 0.27f, 0.00f, 0.00f), // RightForeArm
            new Vector3( 0.25f, 0.00f, 0.00f), // RightHand
            new Vector3( 0.03f,-0.02f, 0.02f), // RightHandThumbEnd
            new Vector3( 0.09f, 0.00f, 0.00f), // RightHandMiddleEnd
            new Vector3(-0.10f,-0.06f, 0.00f), // LeftLeg
            new Vector3( 0.00f,-0.42f, 0.00f), // LeftShin
            new Vector3( 0.00f,-0.42f, 0.00f), // LeftFoot
            new Vector3( 0.00f,-0.05f, 0.15f), // LeftToeBase
            new Vector3( 0.10f,-0.06f, 0.00f), // RightLeg
            new Vector3( 0.00f,-0.42f, 0.00f), // RightShin
            new Vector3( 0.00f,-0.42f, 0.00f), // RightFoot
            new Vector3( 0.00f,-0.05f, 0.15f), // RightToeBase
        };

        private Transform[] _bones = new Transform[SomaPose.JointCount];
        private Vector3 _hipsLocalPos;        // Hips 骨骼相对角色根的本地偏移（Unity 空间）
        private Quaternion _hipsLocalRot;
        private bool _hasHipsBone;
        private bool _ready;

        // ------------------------------------------------------------------
        // 坐标系镜像转换
        // Unity：左手系 Y-up；SOMA 模型空间：右手系 Y-up。
        // 标准镜像转换（沿 XY 平面）：位置 z 取反，四元数 (x,y,z,w) → (-x,-y,z,w)。
        // 该变换是自逆的，两个方向用同一函数。
        // 首次使用请用 T-pose 对照验证：
        //   1) 角色在 Unity 里摆 T-pose，调 CapturePose() 抓一个姿态；
        //   2) 把 30 个关节按父子链算出世界位置，与 Unity 里对应骨骼的世界位置对比
        //      （注意 z 符号与左右）；
        //   3) 若左右或前后反了，说明镜像轴假设与你的角色朝向不符，需要调整。
        // ------------------------------------------------------------------
        public static Vector3 MirrorPosition(Vector3 p) => new Vector3(p.x, p.y, -p.z);

        public static Quaternion MirrorRotation(Quaternion q) =>
            new Quaternion(-q.x, -q.y, q.z, q.w);

        /// <summary>位置缩放比 = 角色身高 / SOMA 参考身高。抓取时除，应用时乘。</summary>
        public float PositionScale =>
            (characterHeight > 0f ? characterHeight : somaReferenceHeight) / somaReferenceHeight;

        void Awake()
        {
            if (animator == null)
                animator = GetComponent<Animator>();
        }

        private void EnsureReady()
        {
            if (_ready) return;
            if (animator == null)
                throw new MotionBricksException("SomaHumanoidMapper 未指定 Animator");

            for (int j = 0; j < SomaPose.JointCount; j++)
            {
                var hb = SomaToHumanoid[j];
                _bones[j] = hb.HasValue ? animator.GetBoneTransform(hb.Value) : null;
            }

            // 记录 Hips 骨骼相对角色根的本地偏移（ApplyPose 求解根变换用，之后不再改动它）。
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            _hasHipsBone = hips != null;
            if (_hasHipsBone)
            {
                _hipsLocalPos = hips.localPosition;
                _hipsLocalRot = hips.localRotation;
            }
            else
            {
                _hipsLocalPos = Vector3.zero;
                _hipsLocalRot = Quaternion.identity;
                Debug.LogWarning("[MotionBricks] 未找到 Hips 骨骼，根节点将直接按 Hips 处理（近似）。");
            }

            if (characterHeight <= 0f)
            {
                characterHeight = _hasHipsBone
                    ? hips.position.y * 2f   // 粗略估计：Hips 高度 × 2
                    : somaReferenceHeight;
                Debug.Log($"[MotionBricks] 自动估算角色身高 {characterHeight:F2}m（近似值，" +
                          "可在 Inspector 手动填写更准的值）");
            }
            _ready = true;
        }

        /// <summary>
        /// 从角色抓取当前姿态，转为 SOMA 模型空间。
        /// root = Hips 世界变换；其余关节为本地变换（Neck2 用参考偏移 + 单位旋转）。
        /// </summary>
        public SomaPose CapturePose()
        {
            EnsureReady();
            float s = PositionScale;
            var pose = new SomaPose();

            if (_hasHipsBone)
            {
                var hips = _bones[0];
                pose.root = new PosRot
                {
                    p = MirrorPosition(hips.position / s),
                    q = MirrorRotation(hips.rotation),
                };
                pose.joints[0] = pose.root; // 服务端以 root 为准，joints[0] 保持一致即可
            }
            else
            {
                var t = animator.transform;
                pose.root = new PosRot
                {
                    p = MirrorPosition(t.position / s),
                    q = MirrorRotation(t.rotation),
                };
                pose.joints[0] = pose.root;
            }

            for (int j = 1; j < SomaPose.JointCount; j++)
            {
                if (j == Neck2Index)
                {
                    // Unity 无 Neck2：保留参考颈段长度 + 单位旋转，避免颈部在模型空间折叠。
                    pose.joints[j] = new PosRot { p = ReferenceOffsets[j], q = Quaternion.identity };
                    continue;
                }
                var bone = _bones[j];
                if (bone == null)
                {
                    // 角色缺失该骨骼（常见于手指/眼睛/下巴）：用参考姿态回退。
                    pose.joints[j] = new PosRot { p = ReferenceOffsets[j], q = Quaternion.identity };
                    continue;
                }
                pose.joints[j] = new PosRot
                {
                    p = MirrorPosition(bone.localPosition / s),
                    q = MirrorRotation(bone.localRotation),
                };
            }
            return pose;
        }

        /// <summary>
        /// 把模型空间的姿态写回角色。
        /// 根物体变换按 R = W · o⁻¹ 求解（W = 模型输出的 Hips 世界变换，
        /// o = Hips 骨骼本地偏移），使 Hips 骨骼的世界变换恰好等于 W；
        /// 其余关节直接写本地变换（位置乘身高比，旋转直接映射）。
        /// </summary>
        public void ApplyPose(SomaPose pose)
        {
            EnsureReady();
            float s = PositionScale;

            // 模型空间 → Unity 空间
            Vector3 wPos = MirrorPosition(pose.root.p) * s;   // Hips 世界位置（Unity 空间）
            Quaternion wRot = MirrorRotation(pose.root.q);    // Hips 世界旋转

            // 求解角色根物体变换 R，使得 R · o = W（o 为 Hips 本地偏移，ApplyPose 不改动它）
            Quaternion rootRot = wRot * Quaternion.Inverse(_hipsLocalRot);
            Vector3 rootPos = wPos - rootRot * _hipsLocalPos;

            var t = animator.transform;
            t.position = rootPos;
            t.rotation = rootRot;

            for (int j = 1; j < SomaPose.JointCount; j++)
            {
                if (j == Neck2Index) continue; // 已并入 Neck，见下
                var bone = _bones[j];
                if (bone == null) continue;
                var local = pose.joints[j];
                if (j == Neck1Index)
                {
                    // Unity 无 Neck2：把 Neck1 · Neck2 合并写进 Neck。
                    // 旋转：U = L1 · L2；位置：U_p = L1_p + L1_q · L2_p，
                    // 可验证 Head 的世界变换与模型输出一致。
                    Quaternion q1 = MirrorRotation(local.q);
                    Quaternion q2 = MirrorRotation(pose.joints[Neck2Index].q);
                    Vector3 p1 = MirrorPosition(local.p) * s;
                    Vector3 p2 = MirrorPosition(pose.joints[Neck2Index].p) * s;
                    bone.localRotation = q1 * q2;
                    bone.localPosition = p1 + q1 * p2;
                }
                else
                {
                    bone.localPosition = MirrorPosition(local.p) * s;
                    bone.localRotation = MirrorRotation(local.q);
                }
            }
        }
    }
}
