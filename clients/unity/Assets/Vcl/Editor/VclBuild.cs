// Project setup, the Quest build, and a headless snapshot test. All of it runs
// from the command line (see clients/unity/README.md):
//
//   tools/unity.sh -batchmode -quit -executeMethod Vcl.EditorTools.VclBuild.Setup
//   tools/unity.sh -batchmode -quit -executeMethod Vcl.EditorTools.VclBuild.Quest     (in `nix develop .#unity`)
//   VCL_IR=lesson.jsonl VCL_SHOT=out.png tools/unity.sh -batchmode -quit -executeMethod Vcl.EditorTools.VclBuild.Snapshot
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Interactions;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;

namespace Vcl.EditorTools
{
    public static class VclBuild
    {
        const string ScenePath = "Assets/Vcl/Main.unity";
        const string TmpPackage = "Packages/com.unity.textmeshpro/Package Resources/TMP Essential Resources.unitypackage";

        [MenuItem("VCL/Set up project")]
        public static void Setup()
        {
            ImportTmp();
            ConfigurePlayer();
            ConfigureXR();
            CreateScene();
            AssetDatabase.SaveAssets();
            Debug.Log("[vcl] setup done");
        }

        /// <summary>TextMesh Pro's fonts and shaders. AssetDatabase.ImportPackage finishes
        /// asynchronously (after a batch-mode -quit), so unpack the .unitypackage
        /// (a gzipped tar of guid/{pathname,asset,asset.meta}) ourselves.</summary>
        static void ImportTmp()
        {
            if (AssetDatabase.IsValidFolder("Assets/TextMesh Pro")) return;
            var entries = new Dictionary<string, Dictionary<string, byte[]>>();
            using (var gz = new System.IO.Compression.GZipStream(File.OpenRead(Path.GetFullPath(TmpPackage)), System.IO.Compression.CompressionMode.Decompress))
            {
                var header = new byte[512];
                while (ReadFull(gz, header) == 512)
                {
                    string name = System.Text.Encoding.ASCII.GetString(header, 0, 100).TrimEnd('\0');
                    if (name.Length == 0) break;
                    string prefix = System.Text.Encoding.ASCII.GetString(header, 345, 155).TrimEnd('\0');
                    if (prefix.Length > 0) name = prefix + "/" + name;
                    long size = Convert.ToInt64(System.Text.Encoding.ASCII.GetString(header, 124, 12).Trim('\0', ' '), 8);
                    var data = new byte[size];
                    ReadFull(gz, data);
                    var pad = new byte[(512 - size % 512) % 512];
                    ReadFull(gz, pad);
                    if (header[156] != '0' && header[156] != 0) continue; // not a regular file
                    if (name.StartsWith("./")) name = name.Substring(2);
                    var parts = name.Split('/');
                    if (parts.Length != 2) continue;
                    if (!entries.TryGetValue(parts[0], out var e)) entries[parts[0]] = e = new Dictionary<string, byte[]>();
                    e[parts[1]] = data;
                }
            }
            foreach (var e in entries.Values)
            {
                if (!e.TryGetValue("pathname", out var pn)) continue;
                var path = System.Text.Encoding.UTF8.GetString(pn).Split('\n')[0].Trim();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (e.TryGetValue("asset", out var asset)) File.WriteAllBytes(path, asset);
                else Directory.CreateDirectory(path);
                if (e.TryGetValue("asset.meta", out var meta)) File.WriteAllBytes(path + ".meta", meta);
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        static int ReadFull(Stream s, byte[] buf)
        {
            int n = 0;
            while (n < buf.Length)
            {
                int r = s.Read(buf, n, buf.Length - n);
                if (r <= 0) break;
                n += r;
            }
            return n;
        }

        static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "pa255ion";
            PlayerSettings.productName = "VCL";
            PlayerSettings.bundleVersion = "0.1";
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "io.github.pa255ion.vcl");
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Standalone, "io.github.pa255ion.vcl");
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)32;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Android, ManagedStrippingLevel.Minimal);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.runInBackground = true;
            // Keyboard via the Input System, controllers via UnityEngine.XR: allow both.
            var ps = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
            if (ps != null)
            {
                var so = new SerializedObject(ps);
                var p = so.FindProperty("activeInputHandler");
                if (p != null) { p.intValue = 2; so.ApplyModifiedProperties(); }
            }
        }

        static void ConfigureXR()
        {
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget) || perTarget == null)
            {
                if (!AssetDatabase.IsValidFolder("Assets/XR")) AssetDatabase.CreateFolder("Assets", "XR");
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
            }
            var group = BuildTargetGroup.Android;
            if (!perTarget.HasSettingsForBuildTarget(group)) perTarget.CreateDefaultSettingsForBuildTarget(group);
            if (!perTarget.HasManagerSettingsForBuildTarget(group)) perTarget.CreateDefaultManagerSettingsForBuildTarget(group);
            var general = perTarget.SettingsForBuildTarget(group);
            general.InitManagerOnStart = true;
            if (!general.Manager.activeLoaders.Any(l => l is OpenXRLoader))
                XRPackageMetadataStore.AssignLoader(general.Manager, typeof(OpenXRLoader).FullName, group);
            EditorUtility.SetDirty(perTarget);

            var oxr = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            if (oxr == null) { Debug.LogError("[vcl] no OpenXR settings for Android"); return; }
            UnityEditor.XR.OpenXR.Features.FeatureHelpers.RefreshFeatures(group); // creates the feature objects
            oxr.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
            foreach (var f in oxr.GetFeatures())
                if (f is MetaQuestFeature || f is OculusTouchControllerProfile) f.enabled = true;
            EditorUtility.SetDirty(oxr);
            Debug.Log("[vcl] OpenXR (Android) features on: " + string.Join(", ", oxr.GetFeatures().Where(f => f.enabled).Select(f => f.GetType().Name)));
        }

        static void CreateScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = new GameObject("Main Camera") { tag = "MainCamera" };
            cam.AddComponent<Camera>();
            var app = new GameObject("VCL").AddComponent<VclApp>();
            app.Cam = cam.GetComponent<Camera>();
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        // ── Quest build ──

        public static void Quest()
        {
            Setup();
            string sdk = Env("VCL_ANDROID_SDK"), ndk = Env("VCL_ANDROID_NDK"), jdk = Env("VCL_JDK");
            if (sdk == null || ndk == null || jdk == null)
                throw new Exception("VCL_ANDROID_SDK, VCL_ANDROID_NDK and VCL_JDK must be set: run inside `nix develop .#unity`");
            // Point the editor at the flake's toolchain for this build only, then put the
            // user's preferences back (they are shared with every other Unity project).
            var saved = new Dictionary<string, (bool isBool, object value, bool had)>();
            void SetS(string k, string v) { saved[k] = (false, EditorPrefs.GetString(k), EditorPrefs.HasKey(k)); EditorPrefs.SetString(k, v); }
            void SetB(string k, bool v) { saved[k] = (true, EditorPrefs.GetBool(k, true), EditorPrefs.HasKey(k)); EditorPrefs.SetBool(k, v); }
            SetB("SdkUseEmbedded", false); SetS("AndroidSdkRoot", sdk);
            SetB("NdkUseEmbedded", false); SetS("AndroidNdkRootR23B", ndk);
            SetB("JdkUseEmbedded", false); SetS("JdkPath", jdk);
            try
            {
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
                Directory.CreateDirectory("Builds");
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = "Builds/vcl-quest.apk",
                    target = BuildTarget.Android,
                    options = BuildOptions.None,
                });
                Debug.Log($"[vcl] build {report.summary.result}: {report.summary.totalSize / 1e6:F1} MB, {report.summary.totalErrors} errors");
                if (report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
            }
            finally
            {
                foreach (var kv in saved)
                {
                    if (!kv.Value.had) EditorPrefs.DeleteKey(kv.Key);
                    else if (kv.Value.isBool) EditorPrefs.SetBool(kv.Key, (bool)kv.Value.value);
                    else EditorPrefs.SetString(kv.Key, (string)kv.Value.value);
                }
            }
        }

        /// <summary>Flat desktop build (no XR) for Linux: Builds/linux/vcl.</summary>
        public static void Linux()
        {
            Setup();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = "Builds/linux/vcl",
                target = BuildTarget.StandaloneLinux64,
            });
            Debug.Log($"[vcl] build {report.summary.result}");
            if (report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
        }

        static string Env(string k)
        {
            var v = Environment.GetEnvironmentVariable(k);
            return string.IsNullOrEmpty(v) ? null : v;
        }

        // ── snapshot (no headset needed) ──

        /// <summary>Play IR lines (VCL_IR, one JSON object per line) to the end and save a PNG (VCL_SHOT).</summary>
        /// <summary>Render lessons to PNGs. VCL_IR: one or more .jsonl files (':'-separated).
        /// With VCL_SHOTS=DIR, every board (the scene before each `clear`, and the end)
        /// becomes DIR/NAME-K.png; otherwise the end of the last file goes to VCL_SHOT.</summary>
        public static void Snapshot()
        {
            string irList = Env("VCL_IR") ?? throw new Exception("set VCL_IR");
            string shot = Env("VCL_SHOT") ?? "snapshot.png", dir = Env("VCL_SHOTS");
            int w = int.Parse(Env("VCL_W") ?? "1600"), h = int.Parse(Env("VCL_H") ?? "900");
            ImportTmp();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            cam.aspect = w / (float)h;
            var app = new GameObject("VCL").AddComponent<VclApp>();
            app.Cam = cam;
            app.Build();
            void Render(string path)
            {
                app.Runtime.Timeline.Flush();
                app.FitCamera();
                app.Runtime.Update(0);
                app.Avatar.Update(0.01f, cam.transform.position);
                foreach (var t in UnityEngine.Object.FindObjectsOfType<TMP_Text>()) t.ForceMeshUpdate(true, true);
                var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
                cam.targetTexture = null;
                RenderTexture.active = null;
                rt.Release();
                Debug.Log($"[vcl] snapshot {path}: {app.Runtime.Entities.Count} entities");
            }
            foreach (var irPath in irList.Split(':'))
            {
                app.Runtime.Reset();
                app.Board.Apply(Board.Default);
                string name = Path.GetFileNameWithoutExtension(irPath);
                int k = 0;
                foreach (var line in File.ReadAllLines(irPath))
                {
                    if (line.Trim().Length == 0) continue;
                    if (dir != null && line.Contains("\"op\":\"clear\""))
                    {
                        app.Runtime.Timeline.Flush();
                        if (app.Runtime.Entities.Count > 0) Render(Path.Combine(dir, $"{name}-{++k}.png"));
                    }
                    app.Feed(line);
                }
                Render(dir != null ? Path.Combine(dir, $"{name}-{++k}.png") : shot);
            }
            // VCL_MENU=1: the lesson menu over the last board, with sample entries.
            if (Env("VCL_MENU") != null && dir != null)
            {
                var m = app.Menu;
                m.Refresh = null;
                m.Lessons = new List<LessonInfo>
                {
                    new LessonInfo { Path = "lessons/garden.vcl", Title = "Lissajous garden", Folder = "Built-in lessons" },
                    new LessonInfo { Path = "library/u01/01.md", Title = "Lesson 1.1 — What is learning? A model, a loss, and data", Folder = "u01-learning-machines" },
                    new LessonInfo { Path = "library/u01/02.md", Title = "Lesson 1.2 — Gradient descent: walking downhill on the loss", Folder = "u01-learning-machines" },
                    new LessonInfo { Path = "library/u02/03.md", Title = "Lesson 2.3 — Relaxation: inference as gradient descent on the states", Folder = "u02-predictive-coding" },
                };
                m.Status = "4 lessons from http://127.0.0.1:8093";
                m.Toggle(cam.transform);
                Render(Path.Combine(dir, "menu-1.png"));
                m.Key(1, false);
                m.Key(1, false);
                m.Key(1, false);
                m.Key(0, true); // into the first folder
                m.Key(2, false);
                Render(Path.Combine(dir, "menu-2.png"));
            }
        }
    }
}
