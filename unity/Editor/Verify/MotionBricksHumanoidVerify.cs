// Batchmode Humanoid Clip Baker path for MotionBricks (Nick / CUDA host).
// Unity.exe -batchmode -nographics -projectPath <demo> -quit \
//   -executeMethod MotionBricksBridge.Editor.MotionBricksHumanoidVerify.Run -logFile <log>
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
    public static class MotionBricksHumanoidVerify
    {
        const string DefaultServer = "http://127.0.0.1:8000";
        const string ReportDir = @"D:\projects\motionbricks-gpu-verify";
        const string ReportPath = ReportDir + @"\humanoid_verify_2026-11-01.json";
        const string FbxAssetPath = "Assets/Characters/DefaultMale/Models/DefaultMale.fbx";
        const string PrefabPath = "Assets/Characters/DefaultMale/DefaultMale_MotionBricks.prefab";
        const string ClipAssetPath = "Assets/MotionBricks/Baked/HumanoidDefaultMale_CaptureBake.anim";
        const float BakeFrameRate = 30f;

        // Same bone map as ClipBaker (SOMA index → HumanBodyBones); Neck2 skipped.
        struct BoneMapEntry
        {
            public int somaIndex;
            public HumanBodyBones bone;
        }

        static readonly BoneMapEntry[] BoneMap =
        {
            new BoneMapEntry { somaIndex = 0,  bone = HumanBodyBones.Hips },
            new BoneMapEntry { somaIndex = 1,  bone = HumanBodyBones.Spine },
            new BoneMapEntry { somaIndex = 2,  bone = HumanBodyBones.Chest },
            new BoneMapEntry { somaIndex = 3,  bone = HumanBodyBones.UpperChest },
            new BoneMapEntry { somaIndex = 4,  bone = HumanBodyBones.Neck },
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

        public static void Run()
        {
            var report = new Dictionary<string, object>();
            report["when_asia_shanghai"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            report["unity_version"] = Application.unityVersion;
            report["project_path"] = Directory.GetCurrentDirectory();
            report["server"] = DefaultServer;
            report["character"] = "Unity Timeline sample DefaultMale.fbx (com.unity.timeline@1.7.7 GameplaySequenceDemo)";
            report["character_asset"] = FbxAssetPath;
            bool ok = true;
            GameObject instance = null;

            try
            {
                Directory.CreateDirectory(ReportDir);
                Directory.CreateDirectory("Assets/MotionBricks/Baked");
                Directory.CreateDirectory("Assets/Characters/DefaultMale");

                EnsureHumanoidImport(FbxAssetPath, report);

                var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FbxAssetPath);
                if (fbx == null)
                    throw new Exception("failed to load FBX: " + FbxAssetPath);

                instance = PrefabUtility.InstantiatePrefab(fbx) as GameObject;
                if (instance == null)
                    instance = UnityEngine.Object.Instantiate(fbx);
                instance.name = "DefaultMale_MotionBricks";
                instance.transform.position = Vector3.zero;
                instance.transform.rotation = Quaternion.identity;

                var animator = instance.GetComponent<Animator>();
                if (animator == null)
                    throw new Exception("DefaultMale has no Animator");
                animator.runtimeAnimatorController = null;
                animator.applyRootMotion = false;
                report["animator_isHuman"] = animator.isHuman;
                report["avatar_valid"] = animator.avatar != null && animator.avatar.isValid;
                if (!animator.isHuman)
                    throw new Exception("Animator is not Humanoid after import");
                if (animator.avatar == null || !animator.avatar.isValid)
                    throw new Exception("Humanoid avatar missing/invalid");

                var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                if (hips == null)
                    throw new Exception("Humanoid avatar has no Hips bone");
                report["hips_path"] = AnimationUtility.CalculateTransformPath(hips, instance.transform);
                report["hips_world_y"] = hips.position.y;

                var mapper = instance.GetComponent<SomaHumanoidMapper>();
                if (mapper == null) mapper = instance.AddComponent<SomaHumanoidMapper>();
                mapper.animator = animator;
                mapper.characterHeight = 0f; // auto

                var client = instance.GetComponent<MotionBricksClient>();
                if (client == null) client = instance.AddComponent<MotionBricksClient>();
                client.serverUrl = DefaultServer;

                var player = instance.GetComponent<MotionBricksPlayer>();
                if (player == null) player = instance.AddComponent<MotionBricksPlayer>();

                // Prefab for user hand-off (components attached).
                EnsureFolder("Assets/Characters/DefaultMale");
                PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath);
                report["prefab"] = PrefabPath;

                // Capture Humanoid rest pose → SOMA (this is the Clip Baker seed path).
                SomaPose seed = mapper.CapturePose();
                seed.Validate();
                report["capture_root_y"] = seed.root.p.y;
                report["capture"] = "PASS";

                // Slightly different target so generate has somewhere to go (Clip Baker PoseSequence).
                SomaPose target = seed.Clone();
                target.root.p = new Vector3(seed.root.p.x + 0.35f, seed.root.p.y, seed.root.p.z + 0.55f);

                string health = HttpGet(DefaultServer + "/health");
                report["health_raw"] = health;
                if (health == null || health.IndexOf("true", StringComparison.Ordinal) < 0)
                    throw new Exception("health failed: " + health);

                var context = new List<SomaPose> { seed.Clone(), seed.Clone(), seed.Clone(), seed.Clone() };
                var targets = new List<SomaPose> { target };
                string body = SomaWire.BuildGenerateRequest(context, targets);
                string gen = HttpPostJson(DefaultServer + "/generate", body);
                if (string.IsNullOrEmpty(gen))
                    throw new Exception("generate empty");

                var clipData = SomaWire.ParseGenerateResponse(gen);
                int n = clipData.frames != null ? clipData.frames.Count : 0;
                report["generate_num_frames"] = n;
                if (n < 10)
                    throw new Exception("too few frames: " + n);
                for (int i = 0; i < clipData.frames.Count; i++)
                    clipData.frames[i].Validate();
                report["generate"] = "PASS";
                report["no_nan"] = "PASS";

                // Smoke ApplyPose on frame 0 + mid (Play path without Enter Play Mode).
                mapper.ApplyPose(clipData.frames[0]);
                mapper.ApplyPose(clipData.frames[n / 2]);
                mapper.ApplyPose(clipData.frames[n - 1]);
                report["apply_pose_smoke"] = "PASS";

                string animPath = BuildAndSaveHumanoidClip(instance, mapper, clipData.frames, ClipAssetPath, bakeInPlace: false);
                report["anim_asset"] = animPath;
                var loaded = AssetDatabase.LoadAssetAtPath<AnimationClip>(animPath);
                if (loaded == null)
                    throw new Exception("failed to load baked anim: " + animPath);
                report["anim_length_sec"] = loaded.length;
                report["anim_frame_rate"] = loaded.frameRate;
                var bindings = AnimationUtility.GetCurveBindings(loaded);
                report["anim_curve_bindings"] = bindings != null ? bindings.Length : 0;
                if (bindings == null || bindings.Length < 7)
                    throw new Exception("baked anim has too few curves: " + (bindings == null ? 0 : bindings.Length));
                report["anim_write_humanoid_bones"] = "PASS";
                report["humanoid_clip_baker_path"] = "PASS";
                report["overall"] = "PASS";
                Debug.Log("[MotionBricksHumanoidVerify] PASS frames=" + n +
                          " curves=" + bindings.Length + " anim=" + animPath + " prefab=" + PrefabPath);
            }
            catch (Exception e)
            {
                ok = false;
                report["overall"] = "FAIL";
                report["error"] = e.ToString();
                Debug.LogError("[MotionBricksHumanoidVerify] FAIL: " + e);
            }
            finally
            {
                if (instance != null)
                    UnityEngine.Object.DestroyImmediate(instance);
            }

            File.WriteAllText(ReportPath, ToJson(report), Encoding.UTF8);
            Debug.Log("[MotionBricksHumanoidVerify] wrote " + ReportPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorApplication.Exit(ok ? 0 : 1);
        }

        static void EnsureHumanoidImport(string assetPath, Dictionary<string, object> report)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null)
                throw new Exception("ModelImporter missing for " + assetPath);

            bool dirty = false;
            if (importer.animationType != ModelImporterAnimationType.Human)
            {
                importer.animationType = ModelImporterAnimationType.Human;
                dirty = true;
            }
            if (importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
            {
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                dirty = true;
            }
            report["import_animationType"] = importer.animationType.ToString();
            report["import_avatarSetup"] = importer.avatarSetup.ToString();
            if (dirty)
            {
                importer.SaveAndReimport();
                report["reimported_humanoid"] = true;
            }
            else
            {
                report["reimported_humanoid"] = false;
            }
        }

        static string BuildAndSaveHumanoidClip(GameObject characterRoot, SomaHumanoidMapper srcMapper,
            List<SomaPose> frames, string assetPath, bool bakeInPlace)
        {
            if (frames == null || frames.Count == 0)
                throw new Exception("no frames");

            var copy = UnityEngine.Object.Instantiate(characterRoot);
            copy.name = characterRoot.name + "__BakeTemp";
            copy.hideFlags = HideFlags.HideAndDontSave;
            var copyAnimator = copy.GetComponent<Animator>();
            copyAnimator.enabled = false;
            copyAnimator.runtimeAnimatorController = null;
            var copyMapper = copy.AddComponent<SomaHumanoidMapper>();
            copyMapper.animator = copyAnimator;
            copyMapper.somaReferenceHeight = srcMapper.somaReferenceHeight;
            copyMapper.characterHeight = srcMapper.characterHeight > 0f
                ? srcMapper.characterHeight
                : srcMapper.somaReferenceHeight;

            try
            {
                copyMapper.ApplyPose(frames[0]);
                var hipsBone = copyAnimator.GetBoneTransform(HumanBodyBones.Hips);
                if (hipsBone == null)
                    throw new Exception("no Hips on bake copy");
                Vector3 originPos = hipsBone.position;
                Quaternion yawInv = Quaternion.Inverse(YawOnly(hipsBone.rotation));

                var tracks = new Dictionary<string, BoneTrack>();
                int n = frames.Count;
                for (int f = 0; f < n; f++)
                {
                    copyMapper.ApplyPose(frames[f]);
                    float t = f / BakeFrameRate;

                    Vector3 hp = yawInv * (hipsBone.position - originPos);
                    Quaternion hq = yawInv * hipsBone.rotation;
                    if (bakeInPlace)
                    {
                        hp.x = 0f;
                        hp.z = 0f;
                    }
                    AddKey(tracks, AnimationUtility.CalculateTransformPath(hipsBone, copy.transform), t, hp, hq);

                    foreach (var m in BoneMap)
                    {
                        if (m.somaIndex == 0) continue;
                        var bone = copyAnimator.GetBoneTransform(m.bone);
                        if (bone == null) continue;
                        AddKey(tracks, AnimationUtility.CalculateTransformPath(bone, copy.transform), t,
                            bone.localPosition, bone.localRotation);
                    }
                }

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

                EnsureFolder(Path.GetDirectoryName(assetPath).Replace('\\', '/'));
                if (AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath) != null)
                    AssetDatabase.DeleteAsset(assetPath);
                AssetDatabase.CreateAsset(clip, assetPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                return assetPath;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }

        static Quaternion YawOnly(Quaternion q)
        {
            var e = q.eulerAngles;
            return Quaternion.Euler(0f, e.y, 0f);
        }

        static void AddKey(Dictionary<string, BoneTrack> tracks, string path, float time, Vector3 p, Quaternion q)
        {
            if (!tracks.TryGetValue(path, out var tr))
            {
                tr = new BoneTrack();
                tracks[path] = tr;
            }
            tr.Add(time, p, q);
        }

        sealed class BoneTrack
        {
            public readonly AnimationCurve px = new AnimationCurve();
            public readonly AnimationCurve py = new AnimationCurve();
            public readonly AnimationCurve pz = new AnimationCurve();
            public readonly AnimationCurve qx = new AnimationCurve();
            public readonly AnimationCurve qy = new AnimationCurve();
            public readonly AnimationCurve qz = new AnimationCurve();
            public readonly AnimationCurve qw = new AnimationCurve();
            Quaternion _prev = Quaternion.identity;
            bool _hasPrev;

            public void Add(float time, Vector3 p, Quaternion q)
            {
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

        static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder))
                return;
            const string assetsPrefix = "Assets/";
            if (!folder.StartsWith(assetsPrefix))
                throw new Exception("folder must be under Assets: " + folder);
            string parent = "Assets";
            foreach (var part in folder.Substring(assetsPrefix.Length).Split('/'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                string next = parent + "/" + part;
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(parent, part);
                parent = next;
            }
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
