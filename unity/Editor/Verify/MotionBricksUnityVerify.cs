// Batchmode GPU+Unity smoke for MotionBricks bridge (Nick / CUDA host).
// Unity.exe -batchmode -nographics -projectPath <demo> -quit \
//   -executeMethod MotionBricksBridge.Editor.MotionBricksUnityVerify.Run -logFile <log>
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using UnityEditor;
using UnityEngine;
using MotionBricksBridge;

namespace MotionBricksBridge.Editor
{
    public static class MotionBricksUnityVerify
    {
        const string DefaultServer = "http://127.0.0.1:8000";
        const string ReportDir = @"D:\projects\motionbricks-gpu-verify";
        const string ReportPath = ReportDir + @"\unity_verify_2026-11-01.json";
        const string ClipAssetPath = "Assets/MotionBricks/Baked/GpuVerifyTPose.anim";

        public static void Run()
        {
            var report = new Dictionary<string, object>();
            report["when_asia_shanghai"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            report["unity_version"] = Application.unityVersion;
            report["project_path"] = Directory.GetCurrentDirectory();
            report["server"] = DefaultServer;
            report["humanoid_bake"] = "skipped_no_humanoid_asset";
            bool ok = true;

            try
            {
                Directory.CreateDirectory(ReportDir);
                Directory.CreateDirectory("Assets/MotionBricks/Baked");
                report["compile"] = "PASS";

                string health = HttpGet(DefaultServer + "/health");
                report["health_raw"] = health;
                if (health == null || health.IndexOf("true", StringComparison.Ordinal) < 0)
                    throw new Exception("health failed: " + health);

                string info = HttpGet(DefaultServer + "/info");
                report["info_raw_len"] = info != null ? info.Length : 0;
                if (info == null || info.IndexOf("num_joints", StringComparison.Ordinal) < 0)
                    throw new Exception("info failed");

                string body = BuildTPoseGenerateBody();
                string gen = HttpPostJson(DefaultServer + "/generate", body);
                if (string.IsNullOrEmpty(gen))
                    throw new Exception("generate empty response");

                var clip = SomaWire.ParseGenerateResponse(gen);
                int n = clip.frames != null ? clip.frames.Count : 0;
                report["generate_num_frames"] = n;
                report["generate_sample_rate"] = clip.sampleRate;
                if (n < 10)
                    throw new Exception("too few frames: " + n);

                for (int i = 0; i < clip.frames.Count; i++)
                    clip.frames[i].Validate();

                report["frame0_root_y"] = clip.frames[0].root.p.y;
                report["health"] = "PASS";
                report["info"] = "PASS";
                report["generate"] = "PASS";
                report["no_nan"] = "PASS";

                string animPath = WriteRootAnimFromFrames(clip.frames);
                report["anim_asset"] = animPath;
                var loaded = AssetDatabase.LoadAssetAtPath<AnimationClip>(animPath);
                if (loaded == null)
                    throw new Exception("failed to load written anim: " + animPath);
                report["anim_length_sec"] = loaded.length;
                report["anim_frame_rate"] = loaded.frameRate;
                report["anim_write"] = "PASS";
                report["overall"] = "PASS";
                Debug.Log("[MotionBricksUnityVerify] PASS frames=" + n + " anim=" + animPath);
            }
            catch (Exception e)
            {
                ok = false;
                report["overall"] = "FAIL";
                report["error"] = e.ToString();
                Debug.LogError("[MotionBricksUnityVerify] FAIL: " + e);
            }

            File.WriteAllText(ReportPath, ToJson(report), Encoding.UTF8);
            Debug.Log("[MotionBricksUnityVerify] wrote " + ReportPath);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        static SomaPose MakeTPose(float height)
        {
            var pose = new SomaPose();
            pose.root = new PosRot { p = new Vector3(0f, height, 0f), q = Quaternion.identity };
            for (int j = 0; j < SomaPose.JointCount; j++)
                pose.joints[j] = new PosRot { p = Vector3.zero, q = Quaternion.identity };
            pose.joints[0] = pose.root;
            return pose;
        }

        static string BuildTPoseGenerateBody()
        {
            var ctx = MakeTPose(0.98f);
            var tgt = MakeTPose(1.0f);
            var context = new List<SomaPose> { ctx, ctx.Clone(), ctx.Clone(), ctx.Clone() };
            var targets = new List<SomaPose> { tgt };
            return SomaWire.BuildGenerateRequest(context, targets);
        }

        static string WriteRootAnimFromFrames(List<SomaPose> frames)
        {
            var clip = new AnimationClip { frameRate = 30f };
            var px = new AnimationCurve();
            var py = new AnimationCurve();
            var pz = new AnimationCurve();
            var qx = new AnimationCurve();
            var qy = new AnimationCurve();
            var qz = new AnimationCurve();
            var qw = new AnimationCurve();

            for (int i = 0; i < frames.Count; i++)
            {
                float t = i / 30f;
                var p = frames[i].root.p;
                var q = frames[i].root.q;
                px.AddKey(t, p.x);
                py.AddKey(t, p.y);
                pz.AddKey(t, p.z);
                qx.AddKey(t, q.x);
                qy.AddKey(t, q.y);
                qz.AddKey(t, q.z);
                qw.AddKey(t, q.w);
            }

            clip.SetCurve("", typeof(Transform), "m_LocalPosition.x", px);
            clip.SetCurve("", typeof(Transform), "m_LocalPosition.y", py);
            clip.SetCurve("", typeof(Transform), "m_LocalPosition.z", pz);
            clip.SetCurve("", typeof(Transform), "m_LocalRotation.x", qx);
            clip.SetCurve("", typeof(Transform), "m_LocalRotation.y", qy);
            clip.SetCurve("", typeof(Transform), "m_LocalRotation.z", qz);
            clip.SetCurve("", typeof(Transform), "m_LocalRotation.w", qw);

            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipAssetPath) != null)
                AssetDatabase.DeleteAsset(ClipAssetPath);
            AssetDatabase.CreateAsset(clip, ClipAssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return ClipAssetPath;
        }

        static string HttpGet(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 30000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        static string HttpPostJson(string url, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 180000;
            req.ReadWriteTimeout = 180000;
            req.ContentLength = bytes.Length;
            using (var s = req.GetRequestStream())
                s.Write(bytes, 0, bytes.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        static string ToJson(Dictionary<string, object> d)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            bool first = true;
            foreach (var kv in d)
            {
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append("  ").Append(JsonString(kv.Key)).Append(": ");
                if (kv.Value == null) sb.Append("null");
                else if (kv.Value is bool b) sb.Append(b ? "true" : "false");
                else if (kv.Value is int || kv.Value is long || kv.Value is float || kv.Value is double)
                    sb.Append(Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture));
                else sb.Append(JsonString(kv.Value.ToString()));
            }
            sb.Append("\n}\n");
            return sb.ToString();
        }

        static string JsonString(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
        }
    }
}
