// MotionBricks 离线动作烘焙工具（"动作工厂"）。
// 菜单：Tools > MotionBricks > Clip Baker
//
// 用途：把 MotionBricks 当动作工厂用——在编辑器里调用推理服务生成动作，
// 一次性烘焙成 Unity AnimationClip（.anim），供手机游戏等离线场景使用。
// 烘焙出的 clip 就是普通动画文件：运行时零推理、零网络、零 GPU 成本，
// 断网照玩，还能进版本管理、做二次剪辑。
//
// 工作流（三步）：
//   1) 场景里摆好角色逐个抓取姿态（姿态序列模式），或放好 waypoint（路径模式）；
//   2) 点"开始烘焙全部 Takes"，工具链式请求服务端并拼接全部帧；
//   3) clip 存到 Assets/MotionBricks/Baked/<Take名>.anim，直接拖进 Animator 即用。
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using MotionBricksBridge;

/// <summary>烘焙模式。</summary>
public enum BakeMode
{
    PoseSequence, // 姿态序列：用户在场景摆好姿态逐个抓取，按顺序链式生成
    Path,         // 路径：用户放若干 waypoint，角色 root 依次走过去（面朝行进方向）
}

/// <summary>一个烘焙任务。挂在 EditorWindow 上，随窗口序列化保存。</summary>
[Serializable]
public class BakeTake
{
    public string takeName = "Take_01";
    public BakeMode mode = BakeMode.PoseSequence;

    [Tooltip("勾选 = 原地（去掉水平位移，只留上下起伏，适合代码驱动移动的游戏）；" +
             "不勾 = 带位移（根运动烘进 Hips，适合直接播放的过场/待机动作）")]
    public bool bakeInPlace = false;

    [Tooltip("每个目标连续生成的段数。路径模式想走长距离可设 2~3。")]
    public int segmentsPerTarget = 1;

    [Tooltip("姿态序列模式的目标（场景抓取，SOMA 模型空间）")]
    public List<SomaPose> poseTargets = new List<SomaPose>();

    [Tooltip("路径模式的路径点（场景空 GameObject，按顺序行走）")]
    public List<Transform> waypoints = new List<Transform>();

    public BakeTake Clone()
    {
        var c = (BakeTake)MemberwiseClone();
        c.poseTargets = new List<SomaPose>();
        foreach (var p in poseTargets)
            c.poseTargets.Add(p != null ? p.Clone() : null);
        c.waypoints = new List<Transform>(waypoints);
        return c;
    }
}

public class MotionBricksClipBaker : EditorWindow
{
    private const string SaveFolder = "Assets/MotionBricks/Baked";
    private const float BakeFrameRate = 30f;

    [SerializeField] private GameObject characterRoot;
    [SerializeField] private string serverUrl = "http://127.0.0.1:8000";
    [SerializeField] private List<BakeTake> takes = new List<BakeTake>();
    [SerializeField] private Vector2 scrollPos;
    [SerializeField] private bool showHelp = true;

    // 不序列化：运行时状态
    private readonly List<bool> _foldouts = new List<bool>();
    private bool _baking;
    private string _connResult = "";
    private bool _connOk;
    private GameObject _runnerGO;

    // SOMA 关节 index → Unity HumanBodyBones（与 SomaHumanoidMapper 内部映射表一致）。
    // index 5（Neck2）在 Unity 无对应骨骼：ApplyPose 已把它合并进 Neck，
    // 烘焙时直接读取副本 Neck 骨骼的本地变换即为合并结果，无需特殊处理。
    private struct BoneMapEntry
    {
        public int somaIndex;
        public HumanBodyBones bone;
    }

    private static readonly BoneMapEntry[] BoneMap =
    {
        new BoneMapEntry { somaIndex = 0,  bone = HumanBodyBones.Hips },
        new BoneMapEntry { somaIndex = 1,  bone = HumanBodyBones.Spine },
        new BoneMapEntry { somaIndex = 2,  bone = HumanBodyBones.Chest },
        new BoneMapEntry { somaIndex = 3,  bone = HumanBodyBones.UpperChest },
        new BoneMapEntry { somaIndex = 4,  bone = HumanBodyBones.Neck },
        // 5 Neck2：无对应骨骼，跳过（已并入 Neck）
        new BoneMapEntry { somaIndex = 6,  bone = HumanBodyBones.Head },
        new BoneMapEntry { somaIndex = 7,  bone = HumanBodyBones.Jaw },
        new BoneMapEntry { somaIndex = 8,  bone = HumanBodyBones.LeftEye },
        new BoneMapEntry { somaIndex = 9,  bone = HumanBodyBones.RightEye },
        new BoneMapEntry { somaIndex = 10, bone = HumanBodyBones.LeftShoulder },
        new BoneMapEntry { somaIndex = 11, bone = HumanBodyBones.LeftUpperArm },
        new BoneMapEntry { somaIndex = 12, bone = HumanBodyBones.LeftLowerArm },
        new BoneMapEntry { somaIndex = 13, bone = HumanBodyBones.LeftHand },
        new BoneMapEntry { somaIndex = 14, bone = HumanBodyBones.LeftThumbDistal },
        new BoneMapEntry { somaIndex = 15, bone = HumanBodyBones.LeftMiddleDistal },
        new BoneMapEntry { somaIndex = 16, bone = HumanBodyBones.RightShoulder },
        new BoneMapEntry { somaIndex = 17, bone = HumanBodyBones.RightUpperArm },
        new BoneMapEntry { somaIndex = 18, bone = HumanBodyBones.RightLowerArm },
        new BoneMapEntry { somaIndex = 19, bone = HumanBodyBones.RightHand },
        new BoneMapEntry { somaIndex = 20, bone = HumanBodyBones.RightThumbDistal },
        new BoneMapEntry { somaIndex = 21, bone = HumanBodyBones.RightMiddleDistal },
        new BoneMapEntry { somaIndex = 22, bone = HumanBodyBones.LeftUpperLeg },
        new BoneMapEntry { somaIndex = 23, bone = HumanBodyBones.LeftLowerLeg },
        new BoneMapEntry { somaIndex = 24, bone = HumanBodyBones.LeftFoot },
        new BoneMapEntry { somaIndex = 25, bone = HumanBodyBones.LeftToes },
        new BoneMapEntry { somaIndex = 26, bone = HumanBodyBones.RightUpperLeg },
        new BoneMapEntry { somaIndex = 27, bone = HumanBodyBones.RightLowerLeg },
        new BoneMapEntry { somaIndex = 28, bone = HumanBodyBones.RightFoot },
        new BoneMapEntry { somaIndex = 29, bone = HumanBodyBones.RightToes },
    };

    [MenuItem("Tools/MotionBricks/Clip Baker")]
    public static void Open()
    {
        var w = GetWindow<MotionBricksClipBaker>("MotionBricks 烘焙");
        w.minSize = new Vector2(430, 600);
    }

    void OnDisable()
    {
        DestroyRunner();
        EditorUtility.ClearProgressBar();
    }

    // ------------------------------------------------------------------
    // UI
    // ------------------------------------------------------------------

    void OnGUI()
    {
        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

        GUILayout.Label("MotionBricks 离线烘焙（动作工厂）", EditorStyles.boldLabel);
        showHelp = EditorGUILayout.Foldout(showHelp, "说明", true);
        if (showHelp)
        {
            EditorGUILayout.HelpBox(
                "本工具把 MotionBricks 当\"动作工厂\"：在编辑器里调推理服务生成动作，" +
                "一次性烘焙成 .anim 文件。\n" +
                "• 为什么手机游戏用离线烘焙而不是实时请求：手机没有 CUDA GPU 跑不动模型；" +
                "云端推理按并发收 GPU 费用；单机游戏断网即不可用。\n" +
                "• 烘焙后动作就是普通 AnimationClip：零延迟、零服务器成本、断网照玩，" +
                "可进版本管理、可二次剪辑。\n" +
                "• Hips 骨骼承载归一化后的根运动（起点归零、初始朝向归正），" +
                "播放 clip 时请把角色根放在原点。",
                MessageType.Info);
        }

        EditorGUILayout.Space();
        GUILayout.Label("1. 场景角色", EditorStyles.boldLabel);
        characterRoot = (GameObject)EditorGUILayout.ObjectField(
            "Character Root", characterRoot, typeof(GameObject), true);
        var mapper = GetMapper(out string why);
        EditorGUILayout.HelpBox(
            mapper != null ? $"已就绪：{characterRoot.name}" : why,
            mapper != null ? MessageType.Info : MessageType.Warning);

        EditorGUILayout.Space();
        GUILayout.Label("2. 推理服务", EditorStyles.boldLabel);
        serverUrl = EditorGUILayout.TextField("Server URL", serverUrl);
        EditorGUI.BeginDisabledGroup(_baking);
        if (GUILayout.Button("测试连接", GUILayout.Width(120)))
            TestConnection();
        EditorGUI.EndDisabledGroup();
        if (!string.IsNullOrEmpty(_connResult))
            EditorGUILayout.HelpBox(_connResult, _connOk ? MessageType.Info : MessageType.Error);

        EditorGUILayout.Space();
        GUILayout.Label("3. 烘焙任务（Takes）", EditorStyles.boldLabel);
        EditorGUI.BeginDisabledGroup(_baking);
        if (GUILayout.Button("+ 添加 Take"))
            takes.Add(new BakeTake { takeName = string.Format("Take_{0:00}", takes.Count + 1) });
        EditorGUI.EndDisabledGroup();

        while (_foldouts.Count < takes.Count) _foldouts.Add(true);
        if (_foldouts.Count > takes.Count)
            _foldouts.RemoveRange(takes.Count, _foldouts.Count - takes.Count);
        for (int i = 0; i < takes.Count; i++)
        {
            if (DrawTake(i, takes[i], mapper))
            {
                i--; // 刚删了一个，索引回退
            }
        }

        EditorGUILayout.Space();
        GUILayout.Label("4. 开始烘焙", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("输出目录", SaveFolder);
        EditorGUI.BeginDisabledGroup(_baking || takes.Count == 0);
        if (GUILayout.Button(_baking ? "烘焙中…" : "开始烘焙全部 Takes", GUILayout.Height(36)))
            StartBake();
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.EndScrollView();
    }

    /// <summary>绘制单个 Take；返回 true 表示该 Take 被删除。</summary>
    private bool DrawTake(int index, BakeTake take, SomaHumanoidMapper mapper)
    {
        bool deleted = false;
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        _foldouts[index] = EditorGUILayout.Foldout(
            _foldouts[index], $"Take {index + 1}：{take.takeName}", true);
        if (_foldouts[index])
        {
            EditorGUI.BeginDisabledGroup(_baking);
            take.takeName = EditorGUILayout.TextField("名称", take.takeName);
            take.mode = (BakeMode)EditorGUILayout.EnumPopup("模式", take.mode);
            take.bakeInPlace = EditorGUILayout.Toggle("原地烘焙（去水平位移）", take.bakeInPlace);
            take.segmentsPerTarget = Mathf.Max(1,
                EditorGUILayout.IntField("每目标生成段数", take.segmentsPerTarget));

            if (take.mode == BakeMode.PoseSequence)
            {
                EditorGUILayout.LabelField($"已抓取目标姿态：{take.poseTargets.Count} 个");
                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginDisabledGroup(mapper == null);
                if (GUILayout.Button("从场景抓取当前姿态为目标"))
                {
                    try
                    {
                        take.poseTargets.Add(mapper.CapturePose());
                    }
                    catch (Exception e)
                    {
                        EditorUtility.DisplayDialog("抓取失败", e.Message, "确定");
                    }
                }
                EditorGUI.EndDisabledGroup();
                EditorGUI.BeginDisabledGroup(take.poseTargets.Count == 0);
                if (GUILayout.Button("删除最后一个", GUILayout.Width(96)))
                    take.poseTargets.RemoveAt(take.poseTargets.Count - 1);
                EditorGUI.EndDisabledGroup();
                if (GUILayout.Button("清空", GUILayout.Width(56)))
                    take.poseTargets.Clear();
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.HelpBox(
                    "在场景里摆好角色姿态 → 点抓取 → 换下一个姿态 → 再抓取。" +
                    "烘焙时按抓取顺序链式生成并拼接成一段完整动作。",
                    MessageType.None);
            }
            else
            {
                EditorGUILayout.LabelField("路径点（按顺序行走）");
                for (int w = 0; w < take.waypoints.Count; w++)
                {
                    EditorGUILayout.BeginHorizontal();
                    take.waypoints[w] = (Transform)EditorGUILayout.ObjectField(
                        $"点 {w + 1}", take.waypoints[w], typeof(Transform), true);
                    if (GUILayout.Button("-", GUILayout.Width(28)))
                    {
                        take.waypoints.RemoveAt(w);
                        w--;
                    }
                    EditorGUILayout.EndHorizontal();
                }
                if (GUILayout.Button("+ 添加路径点"))
                    take.waypoints.Add(null);
                EditorGUILayout.HelpBox(
                    "把空 GameObject 拖进来做路径点。烘焙时角色 root 依次走到各点，" +
                    "并面朝行进方向；想走长距离可把\"每目标生成段数\"设为 2~3。",
                    MessageType.None);
            }

            if (GUILayout.Button("删除此 Take"))
            {
                takes.RemoveAt(index);
                deleted = true;
            }
            EditorGUI.EndDisabledGroup();
        }
        EditorGUILayout.EndVertical();
        return deleted;
    }

    // ------------------------------------------------------------------
    // 连接测试
    // ------------------------------------------------------------------

    private SomaHumanoidMapper GetMapper(out string why)
    {
        why = "";
        if (characterRoot == null)
        {
            why = "请先指定场景角色（Character Root）。";
            return null;
        }
        var m = characterRoot.GetComponent<SomaHumanoidMapper>();
        if (m == null)
        {
            why = "角色上没有 SomaHumanoidMapper 组件（先按 README 挂好运行时脚本）。";
            return null;
        }
        if (m.animator == null && characterRoot.GetComponent<Animator>() == null)
        {
            why = "角色上没有 Animator 组件。";
            return null;
        }
        return m;
    }

    private void EnsureRunner()
    {
        if (_runnerGO != null) return;
        _runnerGO = new GameObject("MotionBricksBakeRunner");
        _runnerGO.hideFlags = HideFlags.HideAndDontSave;
        _runnerGO.AddComponent<MotionBricksBakeRunner>();
    }

    private void DestroyRunner()
    {
        if (_runnerGO != null)
        {
            DestroyImmediate(_runnerGO);
            _runnerGO = null;
        }
    }

    private void TestConnection()
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            _connOk = false;
            _connResult = "请先填写 Server URL。";
            return;
        }
        EnsureRunner();
        var runner = _runnerGO.GetComponent<MotionBricksBakeRunner>();
        var client = _runnerGO.GetComponent<MotionBricksClient>();
        if (client == null)
            client = _runnerGO.AddComponent<MotionBricksClient>();
        client.serverUrl = serverUrl.Trim();
        _connOk = false;
        _connResult = "连接中…";
        runner.RunOneShot(TestCoroutine(client));
        Repaint();
    }

    private IEnumerator TestCoroutine(MotionBricksClient client)
    {
        var task = client.GetInfoAsync();
        while (!task.IsCompleted)
            yield return null;

        if (task.IsFaulted)
        {
            _connOk = false;
            _connResult = "连接失败：" + UnwrapMessage(task.Exception);
        }
        else
        {
            var d = task.Result;
            string model = d.TryGetValue("model", out var m) ? m.ToString() : "?";
            string rig = d.TryGetValue("rig", out var r) ? r.ToString() : "?";
            string nj = d.TryGetValue("num_joints", out var n) ? n.ToString() : "?";
            string sr = d.TryGetValue("sample_rate", out var s) ? s.ToString() : "?";
            _connOk = true;
            _connResult = $"连接成功：{model}（{rig}，{nj} 关节，{sr}fps）";
        }
        Repaint();
    }

    private static string UnwrapMessage(Exception e)
    {
        if (e is AggregateException agg && agg.InnerException != null)
            return agg.InnerException.Message;
        return e != null ? e.Message : "未知错误";
    }

    // ------------------------------------------------------------------
    // 烘焙执行
    // ------------------------------------------------------------------

    private void StartBake()
    {
        var mapper = GetMapper(out string why);
        if (mapper == null)
        {
            EditorUtility.DisplayDialog("无法烘焙", why, "确定");
            return;
        }
        if (takes.Count == 0)
        {
            EditorUtility.DisplayDialog("无法烘焙", "请先添加 Take。", "确定");
            return;
        }
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            EditorUtility.DisplayDialog("无法烘焙", "请先填写 Server URL。", "确定");
            return;
        }

        // 校验每个 Take 的配置
        for (int i = 0; i < takes.Count; i++)
        {
            var t = takes[i];
            if (string.IsNullOrWhiteSpace(t.takeName))
                t.takeName = string.Format("Take_{0:00}", i + 1);
            t.segmentsPerTarget = Mathf.Max(1, t.segmentsPerTarget);
            if (t.mode == BakeMode.PoseSequence && t.poseTargets.Count == 0)
            {
                EditorUtility.DisplayDialog("无法烘焙",
                    $"Take「{t.takeName}」：姿态序列为空，请先抓取目标姿态。", "确定");
                return;
            }
            if (t.mode == BakeMode.Path)
            {
                if (t.waypoints.Count == 0)
                {
                    EditorUtility.DisplayDialog("无法烘焙",
                        $"Take「{t.takeName}」：路径点为空，请先添加 waypoint。", "确定");
                    return;
                }
                foreach (var wp in t.waypoints)
                {
                    if (wp == null)
                    {
                        EditorUtility.DisplayDialog("无法烘焙",
                            $"Take「{t.takeName}」：存在未设置的 waypoint（None）。", "确定");
                        return;
                    }
                }
            }
        }

        EnsureRunner();
        var plan = new BakePlan { mapper = mapper };
        foreach (var t in takes)
            plan.takes.Add(t.Clone()); // 快照：烘焙期间不再读窗口里的可变配置

        var runner = _runnerGO.GetComponent<MotionBricksBakeRunner>();
        runner.Progress += OnBakeProgress;
        runner.Finished += OnBakeFinished;
        _baking = true;
        runner.StartBake(serverUrl.Trim(), plan);
        Repaint();
    }

    private void OnBakeProgress(float p, string msg)
    {
        EditorUtility.DisplayProgressBar("MotionBricks 烘焙", msg, p);
    }

    private void OnBakeFinished(System.Collections.Generic.List<TakeBakeResult> results)
    {
        try
        {
            EditorUtility.ClearProgressBar();
            _baking = false;
            var runner = _runnerGO != null
                ? _runnerGO.GetComponent<MotionBricksBakeRunner>()
                : null;
            if (runner != null)
            {
                runner.Progress -= OnBakeProgress;
                runner.Finished -= OnBakeFinished;
            }

            var mapper = GetMapper(out _);
            int ok = 0, fail = 0;
            var sb = new StringBuilder();
            foreach (var r in results)
            {
                if (!r.Success)
                {
                    fail++;
                    sb.AppendLine($"❌ {r.takeName}：{r.error}");
                    Debug.LogError($"[MotionBricks] 烘焙失败 [{r.takeName}]：{r.error}");
                    continue;
                }
                try
                {
                    string path = BuildAndSaveClip(r.take, r.frames, mapper);
                    ok++;
                    sb.AppendLine($"✅ {r.takeName}：{r.frames.Count} 帧 → {path}");
                }
                catch (Exception e)
                {
                    fail++;
                    sb.AppendLine($"❌ {r.takeName}（存盘失败）：{e.Message}");
                    Debug.LogError($"[MotionBricks] 存盘失败 [{r.takeName}]：{e}");
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("烘焙完成",
                $"成功 {ok} 个，失败 {fail} 个\n\n{sb}", "确定");
        }
        finally
        {
            DestroyRunner();
            EditorUtility.ClearProgressBar();
            Repaint();
        }
    }

    // ------------------------------------------------------------------
    // AnimationClip 烘焙
    //
    // 做法：在场景角色的隐藏副本上复用 SomaHumanoidMapper.ApplyPose
    // （全部坐标转换逻辑零重复），逐帧把写好的骨骼变换读出来记成曲线。
    // 曲线约定（generic transform clip，30fps）：
    //   • Hips：世界变换归一化（减起点、去初始 yaw）后记为 local 曲线，
    //     即根运动被烘进 Hips；播放时要求角色根在原点。
    //     bakeInPlace = true 时再去掉水平位移，只留上下起伏（原地动作）。
    //   • 其余骨骼：ApplyPose 写下的本地变换直接记曲线
    //     （Neck 已含 Neck2 的合并结果；角色缺失的骨骼自动跳过）。
    // ------------------------------------------------------------------

    private string BuildAndSaveClip(BakeTake take, List<SomaPose> frames,
        SomaHumanoidMapper srcMapper)
    {
        if (frames == null || frames.Count == 0)
            throw new MotionBricksException("没有帧可烘焙");
        if (srcMapper == null)
            throw new MotionBricksException("场景角色的 SomaHumanoidMapper 丢失");

        // 1) 隐藏副本
        var copy = Instantiate(characterRoot);
        copy.name = characterRoot.name + "__BakeTemp";
        copy.hideFlags = HideFlags.HideAndDontSave;
        var copyAnimator = copy.GetComponent<Animator>();
        if (copyAnimator == null)
            throw new MotionBricksException("角色上没有 Animator（不应发生）");
        copyAnimator.enabled = false; // 编辑模式本就不会播放，关掉更保险
        var copyMapper = copy.AddComponent<SomaHumanoidMapper>();
        copyMapper.animator = copyAnimator;
        copyMapper.somaReferenceHeight = srcMapper.somaReferenceHeight;
        // 与源 mapper 用完全相同的身高缩放（源在抓取种子姿态时已解析出准确值）
        copyMapper.characterHeight = srcMapper.characterHeight > 0f
            ? srcMapper.characterHeight
            : srcMapper.somaReferenceHeight;

        try
        {
            // 2) 烘焙原点：第 0 帧的 Hips 世界变换
            copyMapper.ApplyPose(frames[0]);
            var hipsBone = copyAnimator.GetBoneTransform(HumanBodyBones.Hips);
            if (hipsBone == null)
                throw new MotionBricksException("角色没有 Hips 骨骼");
            Vector3 originPos = hipsBone.position;
            Quaternion yawInv = Quaternion.Inverse(YawOnly(hipsBone.rotation));

            // 3) 逐帧采集骨骼变换 → 曲线
            var tracks = new Dictionary<string, BoneTrack>();
            int n = frames.Count;
            for (int f = 0; f < n; f++)
            {
                copyMapper.ApplyPose(frames[f]);
                float t = f / BakeFrameRate;

                Vector3 hp = yawInv * (hipsBone.position - originPos);
                Quaternion hq = yawInv * hipsBone.rotation;
                if (take.bakeInPlace)
                {
                    hp.x = 0f;
                    hp.z = 0f;
                }
                AddKey(tracks, BonePath(hipsBone, copy.transform), t, hp, hq);

                foreach (var m in BoneMap)
                {
                    if (m.somaIndex == 0)
                        continue; // Hips 已单独处理
                    var bone = copyAnimator.GetBoneTransform(m.bone);
                    if (bone == null)
                        continue; // 角色缺失该骨骼则跳过，不报错
                    AddKey(tracks, BonePath(bone, copy.transform), t,
                        bone.localPosition, bone.localRotation);
                }

                if ((f & 31) == 0)
                {
                    EditorUtility.DisplayProgressBar("MotionBricks 烘焙",
                        $"[{take.takeName}] 写曲线 {f + 1}/{n}",
                        0.5f + 0.5f * (float)f / n);
                }
            }

            // 4) 组装 AnimationClip
            var clip = new AnimationClip { frameRate = BakeFrameRate };
            foreach (var kv in tracks)
            {
                var tr = kv.Value;
                clip.SetCurve(kv.Key, typeof(Transform), "m_LocalPosition.x", tr.px);
                clip.SetCurve(kv.Key, typeof(Transform), "m_LocalPosition.y", tr.py);
                clip.SetCurve(kv.Key, typeof(Transform), "m_LocalPosition.z", tr.pz);
                clip.SetCurve(kv.Key, typeof(Transform), "m_LocalRotation.x", tr.qx);
                clip.SetCurve(kv.Key, typeof(Transform), "m_LocalRotation.y", tr.qy);
                clip.SetCurve(kv.Key, typeof(Transform), "m_LocalRotation.z", tr.qz);
                clip.SetCurve(kv.Key, typeof(Transform), "m_LocalRotation.w", tr.qw);
            }

            // 5) 存盘（同名覆盖）
            EnsureFolder(SaveFolder);
            string assetPath = $"{SaveFolder}/{SanitizeFileName(take.takeName)}.anim";
            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath) != null)
                AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.CreateAsset(clip, assetPath);
            return assetPath;
        }
        finally
        {
            DestroyImmediate(copy);
            EditorUtility.ClearProgressBar();
        }
    }

    private static string BonePath(Transform bone, Transform root)
    {
        return AnimationUtility.CalculateTransformPath(bone, root);
    }

    private static void AddKey(Dictionary<string, BoneTrack> tracks, string path,
        float time, Vector3 p, Quaternion q)
    {
        if (!tracks.TryGetValue(path, out var tr))
        {
            tr = new BoneTrack();
            tracks[path] = tr;
        }
        tr.Add(time, p, q);
    }

    /// <summary>只保留 yaw 分量的旋转（用于烘焙原点的朝向归一化）。</summary>
    private static Quaternion YawOnly(Quaternion q)
    {
        var e = q.eulerAngles;
        return Quaternion.Euler(0f, e.y, 0f);
    }

    /// <summary>单根骨骼的 7 条曲线，写入时做四元数符号连续性修正，避免曲线插值抽搐。</summary>
    private sealed class BoneTrack
    {
        public readonly AnimationCurve px = new AnimationCurve();
        public readonly AnimationCurve py = new AnimationCurve();
        public readonly AnimationCurve pz = new AnimationCurve();
        public readonly AnimationCurve qx = new AnimationCurve();
        public readonly AnimationCurve qy = new AnimationCurve();
        public readonly AnimationCurve qz = new AnimationCurve();
        public readonly AnimationCurve qw = new AnimationCurve();

        private Quaternion _prev = Quaternion.identity;
        private bool _hasPrev;

        public void Add(float time, Vector3 p, Quaternion q)
        {
            // q 与 -q 表示同一旋转；相邻帧点积为负就取反，保证曲线连续。
            if (_hasPrev && Quaternion.Dot(_prev, q) < 0f)
                q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            _prev = q;
            _hasPrev = true;

            px.AddKey(time, p.x);
            py.AddKey(time, p.y);
            pz.AddKey(time, p.z);
            qx.AddKey(time, q.x);
            qy.AddKey(time, q.y);
            qz.AddKey(time, q.z);
            qw.AddKey(time, q.w);
        }
    }

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder))
            return;
        const string assetsPrefix = "Assets/";
        if (!folder.StartsWith(assetsPrefix))
            throw new MotionBricksException($"输出目录必须在 Assets 下：{folder}");
        string parent = "Assets";
        foreach (var part in folder.Substring(assetsPrefix.Length).Split('/'))
        {
            string next = $"{parent}/{part}";
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(parent, part);
            parent = next;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        string s = sb.ToString().Trim();
        return string.IsNullOrEmpty(s) ? "Take" : s;
    }
}
