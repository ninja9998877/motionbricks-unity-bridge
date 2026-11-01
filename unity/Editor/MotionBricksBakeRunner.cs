// MotionBricks 离线烘焙的网络驱动器（仅编辑器使用）。
//
// 背景：MotionBricksClient.GenerateAsync / GetInfoAsync 内部用 StartCoroutine
// 驱动 UnityWebRequest；在编辑模式下，只有标记 [ExecuteAlways] 的 MonoBehaviour
// 的协程会被执行。因此烘焙时创建一个隐藏的 runner GameObject 来承载请求循环，
// 烘焙结束后立即销毁，不污染场景。
// （MotionBricksClient 因此也带了 [ExecuteAlways]，运行时行为不受影响。）
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using MotionBricksBridge;

/// <summary>一次烘焙的输入：场景角色的 mapper + 快照后的 Take 列表。</summary>
internal class BakePlan
{
    public SomaHumanoidMapper mapper;
    public List<BakeTake> takes = new List<BakeTake>();

    /// <summary>预计总段数（用于进度条）。</summary>
    public int TotalSegments
    {
        get
        {
            int n = 0;
            foreach (var t in takes)
            {
                int targets = t.mode == BakeMode.PoseSequence
                    ? (t.poseTargets != null ? t.poseTargets.Count : 0)
                    : (t.waypoints != null ? t.waypoints.Count : 0);
                n += targets * Mathf.Max(1, t.segmentsPerTarget);
            }
            return Mathf.Max(1, n);
        }
    }
}

/// <summary>单个 Take 的烘焙结果。</summary>
internal class TakeBakeResult
{
    public string takeName;
    public BakeTake take;
    public List<SomaPose> frames;   // 全部拼接好的模型空间帧（30fps）
    public string error;            // 失败时非空

    public bool Success => string.IsNullOrEmpty(error) && frames != null && frames.Count > 0;
}

[ExecuteAlways]
internal class MotionBricksBakeRunner : MonoBehaviour
{
    /// <summary>进度回调（0~1，消息）。</summary>
    public event Action<float, string> Progress;
    /// <summary>全部 Take 结束（成功/失败各自记录在结果里）。</summary>
    public event Action<List<TakeBakeResult>> Finished;

    private MotionBricksClient _client;

    /// <summary>挂载客户端并开始烘焙。</summary>
    public void StartBake(string serverUrl, BakePlan plan)
    {
        _client = gameObject.AddComponent<MotionBricksClient>();
        _client.serverUrl = serverUrl;
        _client.timeoutSeconds = 120f;
        StartCoroutine(BakeCoroutine(plan));
    }

    /// <summary>跑一条一次性协程（测试连接用）。</summary>
    public void RunOneShot(IEnumerator routine) => StartCoroutine(routine);

    public MotionBricksClient Client => _client;

    // ------------------------------------------------------------------
    // 烘焙主循环
    // ------------------------------------------------------------------

    // Set by RequestSegment instead of throwing across yields (C# forbids yield in try/catch).
    private string _segmentError;

    private IEnumerator BakeCoroutine(BakePlan plan)
    {
        var results = new List<TakeBakeResult>();
        int total = plan.TotalSegments;
        int done = 0;

        foreach (var take in plan.takes)
        {
            var result = new TakeBakeResult { takeName = take.takeName, take = take };
            var frames = new List<SomaPose>();
            List<SomaPose> context = null;
            SomaPose seed = null;
            string setupError = null;

            // Setup without yields so try/catch is legal.
            try
            {
                // CapturePose 复用 SomaHumanoidMapper 的抓取 + 坐标转换逻辑。
                seed = plan.mapper.CapturePose();
                context = new List<SomaPose>
                {
                    seed.Clone(), seed.Clone(), seed.Clone(), seed.Clone()
                };
                if (take.mode == BakeMode.PoseSequence)
                {
                    if (take.poseTargets == null || take.poseTargets.Count == 0)
                        throw new MotionBricksException("姿态序列为空：请先在场景摆好姿态并逐个抓取目标");
                }
                else
                {
                    if (take.waypoints == null || take.waypoints.Count == 0)
                        throw new MotionBricksException("路径点为空：请先添加 waypoint");
                }
            }
            catch (Exception e)
            {
                setupError = UnwrapMessage(e);
            }

            if (setupError != null)
            {
                result.error = setupError;
                results.Add(result);
                continue;
            }

            if (take.mode == BakeMode.PoseSequence)
            {
                int ti = 0;
                foreach (var target in take.poseTargets)
                {
                    ti++;
                    bool takeFailed = false;
                    for (int s = 0; s < take.segmentsPerTarget; s++)
                    {
                        _segmentError = null;
                        yield return RequestSegment(context,
                            new List<SomaPose> { target.Clone() }, frames);
                        if (_segmentError != null)
                        {
                            result.error = _segmentError;
                            takeFailed = true;
                            break;
                        }
                        done++;
                        Progress?.Invoke((float)done / total,
                            $"[{take.takeName}] 目标 {ti}/{take.poseTargets.Count} · 段 {s + 1}/{take.segmentsPerTarget}");
                    }
                    if (takeFailed) break;
                }
            }
            else // BakeMode.Path
            {
                float s = plan.mapper.PositionScale;
                // Hips 高度（Unity 空间）：waypoint 只定水平位置，高度保持不变。
                float hipsHeightU = SomaHumanoidMapper.MirrorPosition(seed.root.p).y * s;
                Vector3 prevFlat = new Vector3(
                    SomaHumanoidMapper.MirrorPosition(seed.root.p).x * s, 0f,
                    SomaHumanoidMapper.MirrorPosition(seed.root.p).z * s);
                // 身体模板：首段用场景姿态，之后用上一段的最后一帧，保证身体连贯。
                SomaPose bodyTemplate = seed;
                int wi = 0;
                foreach (var wp in take.waypoints)
                {
                    wi++;
                    SomaPose target = null;
                    string wpError = null;
                    try
                    {
                        if (wp == null)
                            throw new MotionBricksException($"第 {wi} 个 waypoint 未设置（None），请检查路径点列表");
                        Vector3 flat = new Vector3(wp.position.x, 0f, wp.position.z);
                        target = BuildWaypointTarget(bodyTemplate, prevFlat, flat, hipsHeightU, s);
                        prevFlat = flat;
                    }
                    catch (Exception e)
                    {
                        wpError = UnwrapMessage(e);
                    }
                    if (wpError != null)
                    {
                        result.error = wpError;
                        break;
                    }

                    bool takeFailed = false;
                    for (int sg = 0; sg < take.segmentsPerTarget; sg++)
                    {
                        _segmentError = null;
                        yield return RequestSegment(context,
                            new List<SomaPose> { target }, frames);
                        if (_segmentError != null)
                        {
                            result.error = _segmentError;
                            takeFailed = true;
                            break;
                        }
                        bodyTemplate = frames[frames.Count - 1];
                        done++;
                        Progress?.Invoke((float)done / total,
                            $"[{take.takeName}] 路径点 {wi}/{take.waypoints.Count} · 段 {sg + 1}/{take.segmentsPerTarget}");
                    }
                    if (takeFailed) break;
                }
            }

            if (string.IsNullOrEmpty(result.error))
            {
                if (frames.Count == 0)
                    result.error = "未生成任何帧";
                else
                    result.frames = frames;
            }
            results.Add(result);
        }

        Finished?.Invoke(results);
    }

    /// <summary>
    /// 向服务端请求一段生成，把返回帧拼到 frames 末尾，并用最后 4 帧刷新 context。
    /// 失败自动重试 1 次；仍失败则写入 _segmentError（不能跨 yield 抛进 try/catch）。
    /// </summary>
    private IEnumerator RequestSegment(List<SomaPose> context, List<SomaPose> targets,
        List<SomaPose> frames)
    {
        Task<GeneratedClip> task = null;
        try
        {
            task = _client.GenerateAsync(context, targets);
        }
        catch (Exception e)
        {
            _segmentError = $"请求构造失败：{e.Message}";
            yield break;
        }

        int attempt = 0;
        while (true)
        {
            while (!task.IsCompleted)
                yield return null;

            if (task.IsFaulted)
            {
                attempt++;
                string msg = UnwrapMessage(task.Exception);
                if (attempt >= 2)
                {
                    _segmentError = $"服务端请求失败（已重试 1 次）：{msg}";
                    yield break;
                }
                Debug.LogWarning($"[MotionBricks] 烘焙请求失败，正在重试：{msg}");
                try
                {
                    task = _client.GenerateAsync(context, targets);
                }
                catch (Exception e)
                {
                    _segmentError = $"请求构造失败：{e.Message}";
                    yield break;
                }
                continue;
            }
            break;
        }

        var clip = task.Result;
        if (clip == null || clip.frames.Count == 0)
        {
            _segmentError = "服务端返回 0 帧";
            yield break;
        }

        // 服务端第 0 帧 ≈ context 末姿态（端点修正保证）；后续段跳过该重复帧，
        // 避免曲线上出现 doubled keyframe。
        int start = frames.Count == 0 ? 0 : 1;
        for (int i = start; i < clip.frames.Count; i++)
            frames.Add(clip.frames[i]);

        context.Clear();
        for (int i = frames.Count - 4; i < frames.Count; i++)
            context.Add(frames[i].Clone());
    }

    /// <summary>
    /// 由身体模板构造 waypoint 目标姿态：root 放到目标点（保持 Hips 高度），
    /// 面朝行进方向；身体其余部分沿用模板（场景姿态或上一段末帧）。
    /// </summary>
    private static SomaPose BuildWaypointTarget(SomaPose template, Vector3 prevFlat,
        Vector3 flat, float hipsHeightU, float s)
    {
        Vector3 dir = flat - prevFlat;
        Quaternion faceQ = SomaHumanoidMapper.MirrorRotation(template.root.q);
        if (dir.sqrMagnitude > 1e-6f)
            faceQ = Quaternion.LookRotation(dir.normalized, Vector3.up);

        var pose = template.Clone();
        Vector3 targetU = new Vector3(flat.x, hipsHeightU, flat.z);
        pose.root = new PosRot
        {
            p = SomaHumanoidMapper.MirrorPosition(targetU / s),
            q = SomaHumanoidMapper.MirrorRotation(faceQ),
        };
        return pose;
    }

    private static string UnwrapMessage(Exception e)
    {
        if (e is AggregateException agg && agg.InnerException != null)
            return agg.InnerException.Message;
        return e != null ? e.Message : "未知错误";
    }
}
