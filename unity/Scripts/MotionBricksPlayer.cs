// MotionBricks 生成动作的播放器：30fps 播放 + 过渡点自动链式生成。
// 挂在与 MotionBricksClient、SomaHumanoidMapper 同一角色物体上。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace MotionBricksBridge
{
    /// <summary>
    /// 播放循环（协程驱动，不阻塞主线程）：
    /// 1) 按 30fps 采样当前片段并在 LateUpdate 写回角色（避开 Animator 的写入时机）；
    /// 2) 实际播出的每一帧都记入历史；
    /// 3) 播放到过渡点（剩余帧 ≤ transitionFrameThreshold）或 locomotion 输入变化时，
    ///    用历史最后 4 个姿态做 context，向服务端请求下一段，实现无限链式；
    /// 4) 新片段第 0 帧 ≈ 上一段末姿态（服务端端点修正保证），直接续播无跳变。
    /// </summary>
    public class MotionBricksPlayer : MonoBehaviour
    {
        [Header("引用（留空自动取本物体的）")]
        public MotionBricksClient client;
        public SomaHumanoidMapper mapper;

        [Header("播放")]
        [Tooltip("播放采样率，须与模型一致（30）")]
        public float sampleRate = 30f;
        [Tooltip("剩余帧数 ≤ 此值时触发下一次生成（过渡点）")]
        public int transitionFrameThreshold = 4;
        [Tooltip("locomotion 输入变化后，距离上次请求超过此秒数才允许提前重新生成")]
        public float regenerateCooldown = 0.5f;
        [Tooltip("生成失败后的重试间隔（秒）")]
        public float retryDelay = 1f;
        [Tooltip("历史姿态保留上限（帧），默认 600 = 20 秒")]
        public int maxHistoryFrames = 600;

        private List<SomaPose> _frames;      // 当前播放片段（模型空间）
        private float _playIndex;
        private readonly List<SomaPose> _history = new List<SomaPose>(); // 已播出的姿态（模型空间）
        private SomaPose _pendingApply;      // LateUpdate 待写回的姿态

        private SomaPose _locomotionTarget;  // 最新的 locomotion 目标（可被覆盖）
        private bool _hasLocomotionTarget;
        private bool _locomotionDirty;
        private Vector3 _lastLocomotionRoot; // 去重用（Unity 空间）
        private readonly Queue<List<SomaPose>> _targetQueue = new Queue<List<SomaPose>>();

        private bool _generating;
        private float _lastRequestTime = -100f;
        private int _failStreak;

        public bool IsPlaying => _frames != null && _playIndex < _frames.Count - 1;
        public float TimeToTransition =>
            _frames == null ? 0f : Mathf.Max(0f, (_frames.Count - 1 - _playIndex) / sampleRate);
        public int HistoryCount => _history.Count;
        public bool IsGenerating => _generating;

        void Awake()
        {
            if (client == null) client = GetComponent<MotionBricksClient>();
            if (mapper == null) mapper = GetComponent<SomaHumanoidMapper>();
            if (client == null)
                Debug.LogError("[MotionBricks] MotionBricksPlayer 需要同物体上的 MotionBricksClient");
            if (mapper == null)
                Debug.LogError("[MotionBricks] MotionBricksPlayer 需要同物体上的 SomaHumanoidMapper");
        }

        void Start()
        {
            StartCoroutine(PlaybackLoop());
        }

        // 在 LateUpdate 写回骨骼：Animator 在此之前已写完骨骼，不会被覆盖。
        void LateUpdate()
        {
            if (_pendingApply != null && mapper != null)
            {
                mapper.ApplyPose(_pendingApply);
                _pendingApply = null;
            }
        }

        // ------------------------------------------------------------------
        // 对外 API
        // ------------------------------------------------------------------

        /// <summary>
        /// 设置 locomotion 目标：角色在世界坐标 worldPos 处停下（保持当前高度），
        /// 面朝目标方向。speed &lt; 0.05 视为原地停留。同一目标重复调用会被去重。
        /// 内部在 Unity 空间构造目标姿态（当前身体姿态 + 移动后的根），再转回模型空间。
        /// </summary>
        public void SetLocomotionTarget(Vector3 worldPos, float speed)
        {
            if (mapper == null) return;
            var current = mapper.CapturePose(); // 模型空间
            float s = mapper.PositionScale;

            // 模型空间 → Unity 空间（直观地摆目标）
            Vector3 hipsU = SomaHumanoidMapper.MirrorPosition(current.root.p) * s;
            Quaternion hipsQ = SomaHumanoidMapper.MirrorRotation(current.root.q);

            Vector3 flatDir = worldPos - hipsU;
            flatDir.y = 0f;
            Quaternion faceQ = hipsQ;
            if (flatDir.sqrMagnitude > 1e-6f)
                faceQ = Quaternion.LookRotation(flatDir.normalized, Vector3.up);

            Vector3 targetU = worldPos;
            targetU.y = hipsU.y; // 保持当前 Hips 高度，不飞天/入地

            // 去重：目标几乎没变就不更新，避免刷屏式重复请求
            if (_hasLocomotionTarget && (targetU - _lastLocomotionRoot).sqrMagnitude < 1e-6f)
                return;

            var pose = current.Clone();
            pose.root = new PosRot
            {
                p = SomaHumanoidMapper.MirrorPosition(targetU / s),
                q = SomaHumanoidMapper.MirrorRotation(faceQ),
            };
            // 单姿态 target：只定终点（位置+朝向+身体姿态），速度由连续重生成自然形成。
            _locomotionTarget = pose;
            _hasLocomotionTarget = true;
            _lastLocomotionRoot = targetU;
            _locomotionDirty = true;
        }

        /// <summary>排队一个一次性目标姿态（单姿态）。有排队目标时优先于 locomotion 目标使用。</summary>
        public void SetTargetPose(SomaPose target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            target.Validate();
            _targetQueue.Enqueue(new List<SomaPose> { target.Clone() });
        }

        /// <summary>排队一组目标姿态（1~4 个，多姿态可携带速度/加速度信息）。</summary>
        public void SetTargetPoses(List<SomaPose> targets)
        {
            if (targets == null || targets.Count < 1 || targets.Count > 4)
                throw new ArgumentException("targets 须 1~4 个姿态", nameof(targets));
            foreach (var t in targets) t.Validate();
            _targetQueue.Enqueue(new List<SomaPose>(targets));
        }

        /// <summary>直接播放一段外部拿到的 clip（会清空当前播放进度）。</summary>
        public void PlayOneShot(GeneratedClip clip)
        {
            if (clip == null || clip.frames.Count == 0)
                throw new ArgumentException("空 clip", nameof(clip));
            _frames = clip.frames;
            _playIndex = 0f;
        }

        public void ClearTargets()
        {
            _targetQueue.Clear();
            _hasLocomotionTarget = false;
            _locomotionDirty = false;
        }

        // ------------------------------------------------------------------
        // 播放循环
        // ------------------------------------------------------------------

        private IEnumerator PlaybackLoop()
        {
            // 用当前姿态播种历史（同一姿态 ×4），之后只记录真实播出的帧。
            var seed = mapper.CapturePose();
            for (int i = 0; i < 4; i++)
                _history.Add(seed.Clone());

            while (true)
            {
                if (_frames == null || _playIndex >= _frames.Count - 1)
                {
                    // 片段播完（或尚未开始）：有目标就生成
                    if (HasTarget() && !_generating)
                        yield return GenerateNext();
                    else
                        yield return null;
                    continue;
                }

                // 推进播放并采样
                _playIndex += Time.deltaTime * sampleRate;
                int i0 = Mathf.Clamp(Mathf.FloorToInt(_playIndex), 0, _frames.Count - 1);
                int i1 = Mathf.Min(i0 + 1, _frames.Count - 1);
                float t = Mathf.Clamp01(_playIndex - i0);
                var pose = SomaPose.Lerp(_frames[i0], _frames[i1], t);

                _pendingApply = pose;      // LateUpdate 写回角色
                _history.Add(pose);        // 记录实际播出的姿态
                if (_history.Count > maxHistoryFrames)
                    _history.RemoveRange(0, _history.Count - maxHistoryFrames);

                // 过渡点或输入变化 → 提前生成下一段
                float remaining = _frames.Count - 1 - _playIndex;
                bool early = _locomotionDirty
                    && Time.time - _lastRequestTime >= regenerateCooldown
                    && remaining <= _frames.Count * 0.75f;
                if (!_generating && HasTarget() && (remaining <= transitionFrameThreshold || early))
                {
                    _locomotionDirty = false;
                    yield return GenerateNext();
                }
                else
                {
                    yield return null;
                }
            }
        }

        private bool HasTarget() => _targetQueue.Count > 0 || _hasLocomotionTarget;

        private List<SomaPose> DequeueTargets()
        {
            if (_targetQueue.Count > 0)
                return _targetQueue.Dequeue();
            return new List<SomaPose> { _locomotionTarget.Clone() };
        }

        private IEnumerator GenerateNext()
        {
            _generating = true;
            _lastRequestTime = Time.time;

            var targets = DequeueTargets();
            var context = new List<SomaPose>(4);
            for (int i = _history.Count - 4; i < _history.Count; i++)
                context.Add(_history[i].Clone());

            Task<GeneratedClip> task = null;
            try
            {
                task = client.GenerateAsync(context, targets);
            }
            catch (Exception e)
            {
                Debug.LogError($"[MotionBricks] 请求构造失败：{e.Message}");
            }

            if (task != null)
            {
                while (!task.IsCompleted)
                    yield return null;

                if (task.IsFaulted)
                {
                    _failStreak++;
                    var inner = task.Exception != null ? task.Exception.InnerException : null;
                    Debug.LogError($"[MotionBricks] 生成失败（连续 {_failStreak} 次）：" +
                                   $"{(inner != null ? inner.Message : task.Exception.Message)}，" +
                                   $"{retryDelay} 秒后重试");
                    yield return new WaitForSeconds(retryDelay);
                }
                else
                {
                    var clip = task.Result;
                    if (clip.frames.Count == 0)
                    {
                        Debug.LogError("[MotionBricks] 服务端返回 0 帧");
                    }
                    else
                    {
                        _failStreak = 0;
                        _frames = clip.frames;
                        // 第 0 帧 ≈ 上一段末姿态（服务端端点修正保证），直接续播
                        _playIndex = 0f;
                    }
                }
            }
            _generating = false;
        }
    }
}
