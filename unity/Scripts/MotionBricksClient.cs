// MotionBricks 推理服务的 HTTP 客户端（UnityWebRequest，无第三方依赖）。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace MotionBricksBridge
{
    /// <summary>
    /// 挂在场景任意 GameObject 上（建议与 MotionBricksPlayer 同一物体）。
    /// GenerateAsync 用协程驱动 UnityWebRequest，再以 Task 形式返回，不阻塞主线程。
    /// [ExecuteAlways]：让编辑器烘焙工具（Tools > MotionBricks > Clip Baker）
    /// 在编辑模式下也能驱动它的协程；运行时行为不受影响。
    /// </summary>
    [ExecuteAlways]
    public class MotionBricksClient : MonoBehaviour
    {
        [Tooltip("推理服务地址，例如 http://192.168.1.10:8000")]
        public string serverUrl = "http://127.0.0.1:8000";

        [Tooltip("单次生成请求的超时（秒）。不含服务端首次构建 TensorRT plan 的时间。")]
        public float timeoutSeconds = 60f;

        /// <summary>
        /// 请求一次动作生成。context 须 4 个姿态，targets 须 1~4 个姿态（均为 SOMA 模型空间）。
        /// 参数非法直接抛 ArgumentException；网络/服务端错误以 Faulted Task 形式返回。
        /// </summary>
        public Task<GeneratedClip> GenerateAsync(List<SomaPose> context, List<SomaPose> targets)
        {
            if (context == null || context.Count != 4)
                throw new ArgumentException("context 须恰好 4 个姿态", nameof(context));
            if (targets == null || targets.Count < 1 || targets.Count > 4)
                throw new ArgumentException("targets 须 1~4 个姿态", nameof(targets));
            foreach (var p in context) p.Validate();
            foreach (var p in targets) p.Validate();

            var tcs = new TaskCompletionSource<GeneratedClip>();
            StartCoroutine(GenerateCoroutine(context, targets, tcs));
            return tcs.Task;
        }

        private IEnumerator GenerateCoroutine(
            List<SomaPose> context, List<SomaPose> targets,
            TaskCompletionSource<GeneratedClip> tcs)
        {
            string body;
            try
            {
                body = SomaWire.BuildGenerateRequest(context, targets);
            }
            catch (Exception e)
            {
                tcs.SetException(e);
                yield break;
            }

            string url = serverUrl.TrimEnd('/') + "/generate";
            byte[] payload = Encoding.UTF8.GetBytes(body);
            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(payload);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = Mathf.Max(1, Mathf.CeilToInt(timeoutSeconds));

                yield return req.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
                bool ok = req.result == UnityWebRequest.Result.Success;
#else
                bool ok = !req.isNetworkError && !req.isHttpError;
#endif
                if (!ok)
                {
                    tcs.SetException(new MotionBricksException(
                        $"请求失败：{req.error}（HTTP {(int)req.responseCode}）"));
                    yield break;
                }
                if (req.responseCode != 200)
                {
                    tcs.SetException(new MotionBricksException(
                        $"服务端返回 HTTP {(int)req.responseCode}：{req.downloadHandler.text}"));
                    yield break;
                }
                try
                {
                    tcs.SetResult(SomaWire.ParseGenerateResponse(req.downloadHandler.text));
                }
                catch (Exception e)
                {
                    tcs.SetException(new MotionBricksException($"解析服务端返回失败：{e.Message}"));
                }
            }
        }

        /// <summary>GET /info，返回服务端的模型与坐标系信息（原始 JSON 解析结果）。</summary>
        public Task<Dictionary<string, object>> GetInfoAsync()
        {
            var tcs = new TaskCompletionSource<Dictionary<string, object>>();
            StartCoroutine(GetInfoCoroutine(tcs));
            return tcs.Task;
        }

        private IEnumerator GetInfoCoroutine(TaskCompletionSource<Dictionary<string, object>> tcs)
        {
            string url = serverUrl.TrimEnd('/') + "/info";
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 10;
                yield return req.SendWebRequest();
#if UNITY_2020_2_OR_NEWER
                bool ok = req.result == UnityWebRequest.Result.Success;
#else
                bool ok = !req.isNetworkError && !req.isHttpError;
#endif
                if (!ok || req.responseCode != 200)
                {
                    tcs.SetException(new MotionBricksException($"GET /info 失败：{req.error}"));
                    yield break;
                }
                try
                {
                    var d = MiniJson.Parse(req.downloadHandler.text) as Dictionary<string, object>;
                    tcs.SetResult(d ?? new Dictionary<string, object>());
                }
                catch (Exception e)
                {
                    tcs.SetException(new MotionBricksException($"解析 /info 失败：{e.Message}"));
                }
            }
        }
    }
}
