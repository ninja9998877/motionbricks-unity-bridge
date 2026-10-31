// SOMA 姿态数据结构 + HTTP 线路格式的 JSON 编解码。
// 只用 UnityEngine 自带类型与手写解析器，不依赖第三方包。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace MotionBricksBridge
{
    /// <summary>位置 + 旋转（四元数 x,y,z,w）。</summary>
    [Serializable]
    public struct PosRot
    {
        public Vector3 p;      // 米
        public Quaternion q;   // (x, y, z, w)
    }

    /// <summary>
    /// SOMA 模型空间（Y-up 右手系，米制）下的一个姿态。
    /// root：Hips 的世界放置；joints：30 个关节的局部变换（index 0 = Hips，
    /// 其局部变换即世界放置，与 root 一致）。
    /// </summary>
    [Serializable]
    public class SomaPose
    {
        public const int JointCount = 30;

        public PosRot root;
        public PosRot[] joints = new PosRot[JointCount];

        public SomaPose Clone()
        {
            var c = new SomaPose { root = root };
            Array.Copy(joints, c.joints, JointCount);
            return c;
        }

        /// <summary>两姿态插值（位置 Lerp、四元数 Slerp），用于 30fps 播放采样。</summary>
        public static SomaPose Lerp(SomaPose a, SomaPose b, float t)
        {
            var r = new SomaPose { root = LerpPosRot(a.root, b.root, t) };
            for (int j = 0; j < JointCount; j++)
                r.joints[j] = LerpPosRot(a.joints[j], b.joints[j], t);
            return r;
        }

        private static PosRot LerpPosRot(PosRot a, PosRot b, float t)
        {
            return new PosRot
            {
                p = Vector3.Lerp(a.p, b.p, t),
                q = Quaternion.Slerp(a.q, b.q, t),
            };
        }

        /// <summary>发送前的合法性检查：关节数 30、数值有限。</summary>
        public void Validate()
        {
            if (joints == null || joints.Length != JointCount)
                throw new MotionBricksException($"姿态 joints 须为 {JointCount} 个");
            CheckFinite(root.p, "root.p");
            CheckFiniteQ(root.q, "root.q");
            for (int j = 0; j < JointCount; j++)
            {
                CheckFinite(joints[j].p, $"joints[{j}].p");
                CheckFiniteQ(joints[j].q, $"joints[{j}].q");
            }
        }

        private static void CheckFinite(Vector3 v, string name)
        {
            if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
                float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z))
                throw new MotionBricksException($"姿态含非有限数值：{name}");
        }

        private static void CheckFiniteQ(Quaternion q, string name)
        {
            if (float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w) ||
                float.IsInfinity(q.x) || float.IsInfinity(q.y) || float.IsInfinity(q.z) || float.IsInfinity(q.w))
                throw new MotionBricksException($"姿态含非有限数值：{name}");
        }
    }

    /// <summary>服务端一次生成返回的动作片段。</summary>
    public class GeneratedClip
    {
        public List<SomaPose> frames = new List<SomaPose>();
        public int numFrames;
        public float sampleRate = 30f;

        public float DurationSeconds => numFrames <= 1 ? 0f : (numFrames - 1) / sampleRate;
    }

    public class MotionBricksException : Exception
    {
        public MotionBricksException(string message) : base(message) { }
    }

    /// <summary>
    /// HTTP 线路格式的 JSON 编解码（手写，不依赖第三方包）。
    /// 请求：{"context": [pose×4], "targets": [pose×1..4]}
    /// 姿态：{"root": {"p": [x,y,z], "q": [x,y,z,w]}, "joints": [[x,y,z,x,y,z,w]×30]}
    /// 响应：{"frames": [pose...], "num_frames": N, "sample_rate": 30}
    /// </summary>
    public static class SomaWire
    {
        public static string BuildGenerateRequest(List<SomaPose> context, List<SomaPose> targets)
        {
            var sb = new StringBuilder(8192);
            sb.Append("{\"context\":[");
            for (int i = 0; i < context.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendPose(sb, context[i]);
            }
            sb.Append("],\"targets\":[");
            for (int i = 0; i < targets.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendPose(sb, targets[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendPose(StringBuilder sb, SomaPose pose)
        {
            sb.Append("{\"root\":");
            AppendPosRot(sb, pose.root);
            sb.Append(",\"joints\":[");
            for (int j = 0; j < SomaPose.JointCount; j++)
            {
                if (j > 0) sb.Append(',');
                AppendJoint(sb, pose.joints[j]);
            }
            sb.Append("]}");
        }

        private static void AppendPosRot(StringBuilder sb, PosRot pr)
        {
            sb.Append("{\"p\":[");
            AppendFloat(sb, pr.p.x); sb.Append(',');
            AppendFloat(sb, pr.p.y); sb.Append(',');
            AppendFloat(sb, pr.p.z);
            sb.Append("],\"q\":[");
            AppendFloat(sb, pr.q.x); sb.Append(',');
            AppendFloat(sb, pr.q.y); sb.Append(',');
            AppendFloat(sb, pr.q.z); sb.Append(',');
            AppendFloat(sb, pr.q.w);
            sb.Append("]}");
        }

        private static void AppendJoint(StringBuilder sb, PosRot pr)
        {
            sb.Append('[');
            AppendFloat(sb, pr.p.x); sb.Append(',');
            AppendFloat(sb, pr.p.y); sb.Append(',');
            AppendFloat(sb, pr.p.z); sb.Append(',');
            AppendFloat(sb, pr.q.x); sb.Append(',');
            AppendFloat(sb, pr.q.y); sb.Append(',');
            AppendFloat(sb, pr.q.z); sb.Append(',');
            AppendFloat(sb, pr.q.w);
            sb.Append(']');
        }

        private static void AppendFloat(StringBuilder sb, float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
                throw new MotionBricksException("姿态含非有限数值，拒绝发送");
            sb.Append(v.ToString("G9", CultureInfo.InvariantCulture));
        }

        public static GeneratedClip ParseGenerateResponse(string json)
        {
            var root = MiniJson.Parse(json) as Dictionary<string, object>;
            if (root == null)
                throw new MotionBricksException("服务端返回不是 JSON 对象");

            var clip = new GeneratedClip();
            if (root.TryGetValue("num_frames", out object nf))
                clip.numFrames = Convert.ToInt32(nf, CultureInfo.InvariantCulture);
            if (root.TryGetValue("sample_rate", out object sr))
                clip.sampleRate = Convert.ToSingle(sr, CultureInfo.InvariantCulture);
            if (!(root.TryGetValue("frames", out object fo) && fo is List<object> frames))
                throw new MotionBricksException("服务端返回缺少 frames 字段");

            foreach (var f in frames)
                clip.frames.Add(ParsePose(f));
            if (clip.numFrames <= 0)
                clip.numFrames = clip.frames.Count;
            return clip;
        }

        private static SomaPose ParsePose(object o)
        {
            var d = o as Dictionary<string, object>;
            if (d == null)
                throw new MotionBricksException("帧不是 JSON 对象");
            var pose = new SomaPose { root = ParsePosRot(d["root"]) };
            var jl = d["joints"] as List<object>;
            if (jl == null || jl.Count != SomaPose.JointCount)
                throw new MotionBricksException($"帧的 joints 须为 {SomaPose.JointCount} 个");
            for (int j = 0; j < SomaPose.JointCount; j++)
                pose.joints[j] = ParseJoint(jl[j]);
            return pose;
        }

        private static PosRot ParsePosRot(object o)
        {
            var d = o as Dictionary<string, object>;
            if (d == null)
                throw new MotionBricksException("root 不是 JSON 对象");
            return new PosRot
            {
                p = ParseVec3(d["p"]),
                q = ParseQuat(d["q"]),
            };
        }

        private static PosRot ParseJoint(object o)
        {
            var l = o as List<object>;
            if (l == null || l.Count != 7)
                throw new MotionBricksException("关节须为 7 个数 [x,y,z,x,y,z,w]");
            return new PosRot
            {
                p = new Vector3(ToF(l[0]), ToF(l[1]), ToF(l[2])),
                q = new Quaternion(ToF(l[3]), ToF(l[4]), ToF(l[5]), ToF(l[6])),
            };
        }

        private static Vector3 ParseVec3(object o)
        {
            var l = o as List<object>;
            if (l == null || l.Count != 3)
                throw new MotionBricksException("p 须为 3 个数");
            return new Vector3(ToF(l[0]), ToF(l[1]), ToF(l[2]));
        }

        private static Quaternion ParseQuat(object o)
        {
            var l = o as List<object>;
            if (l == null || l.Count != 4)
                throw new MotionBricksException("q 须为 4 个数");
            return new Quaternion(ToF(l[0]), ToF(l[1]), ToF(l[2]), ToF(l[3]));
        }

        private static float ToF(object o) => Convert.ToSingle(o, CultureInfo.InvariantCulture);
    }

    /// <summary>极简 JSON 解析器（对象/数组/字符串/数字/布尔/null），够用且无依赖。</summary>
    internal static class MiniJson
    {
        public static object Parse(string json)
        {
            var p = new Parser(json);
            p.SkipWhitespace();
            object v = p.ParseValue();
            p.SkipWhitespace();
            if (!p.End)
                throw new FormatException("JSON 末尾有多余字符");
            return v;
        }

        private sealed class Parser
        {
            private readonly string s;
            private int i;

            public Parser(string s) { this.s = s; }
            public bool End => i >= s.Length;

            public void SkipWhitespace()
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }

            public object ParseValue()
            {
                SkipWhitespace();
                if (i >= s.Length)
                    throw new FormatException("JSON 意外结束");
                char c = s[i];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ParseNumber();
                }
            }

            private void Expect(string word)
            {
                if (s.Length - i < word.Length || s.Substring(i, word.Length) != word)
                    throw new FormatException("JSON 解析错误：期望 " + word);
                i += word.Length;
            }

            private Dictionary<string, object> ParseObject()
            {
                var dict = new Dictionary<string, object>();
                i++; // {
                SkipWhitespace();
                if (i < s.Length && s[i] == '}')
                {
                    i++;
                    return dict;
                }
                while (true)
                {
                    SkipWhitespace();
                    if (i >= s.Length || s[i] != '"')
                        throw new FormatException("JSON 对象键须为字符串");
                    string key = ParseString();
                    SkipWhitespace();
                    if (i >= s.Length || s[i] != ':')
                        throw new FormatException("JSON 对象键后须为冒号");
                    i++;
                    dict[key] = ParseValue();
                    SkipWhitespace();
                    if (i >= s.Length)
                        throw new FormatException("JSON 对象意外结束");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; break; }
                    throw new FormatException("JSON 对象格式错误");
                }
                return dict;
            }

            private List<object> ParseArray()
            {
                var list = new List<object>();
                i++; // [
                SkipWhitespace();
                if (i < s.Length && s[i] == ']')
                {
                    i++;
                    return list;
                }
                while (true)
                {
                    list.Add(ParseValue());
                    SkipWhitespace();
                    if (i >= s.Length)
                        throw new FormatException("JSON 数组意外结束");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; break; }
                    throw new FormatException("JSON 数组格式错误");
                }
                return list;
            }

            private string ParseString()
            {
                var sb = new StringBuilder();
                i++; // "
                while (true)
                {
                    if (i >= s.Length)
                        throw new FormatException("JSON 字符串意外结束");
                    char c = s[i++];
                    if (c == '"') break;
                    if (c == '\\')
                    {
                        if (i >= s.Length)
                            throw new FormatException("JSON 转义意外结束");
                        char e = s[i++];
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (i + 4 > s.Length)
                                    throw new FormatException("JSON \\u 转义不完整");
                                sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                                i += 4;
                                break;
                            default: throw new FormatException("JSON 非法转义 \\" + e);
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                return sb.ToString();
            }

            private double ParseNumber()
            {
                int start = i;
                if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' ||
                       s[i] == 'e' || s[i] == 'E' || s[i] == '-' || s[i] == '+')) i++;
                string token = s.Substring(start, i - start);
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    throw new FormatException("JSON 数字非法：" + token);
                return v;
            }
        }
    }
}
