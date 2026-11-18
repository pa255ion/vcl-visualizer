// Entry point: builds the classroom (board, teacher, lights), connects to
// `vcl serve`, and feeds IR into the Runtime. Works in a headset (OpenXR) and,
// without one, as a flat desktop view.
//
// Server address, first found wins:
//   1. command line:  -vcl ws://host:8093/ws   (Android: adb shell am start ... -e unity "-vcl ws://…")
//   2. <persistentDataPath>/server.txt         (Quest: tools/set-server.sh)
//   3. ws://127.0.0.1:8093/ws                   (Quest over USB after `adb reverse tcp:8093 tcp:8093`)
//
// Controls (headset): A/X = lesson menu, trigger = next step (or choose, in
// the menu), B/Y = step mode on/off, thumbstick click = recentre, hold it =
// switch between upright and reclined (the room follows your view, for
// lying down). Desktop: M = menu (arrows + Enter), Space/→/Enter = next,
// S = step mode.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using TMPro;
using UnityEngine;
using UnityEngine.XR;

namespace Vcl
{
    public class VclApp : MonoBehaviour
    {
        public static readonly Vector3 BoardCenter = new Vector3(0, 1.55f, 2.3f);

        public Camera Cam;
        public Transform Classroom;
        public Board Board;
        public Avatar Avatar;
        public Runtime Runtime;
        Connection conn;
        TextMeshPro status;
        readonly List<string> logLines = new List<string>();
        bool xr, recentred;
        readonly Dictionary<string, bool> wasPressed = new Dictionary<string, bool>();
        public Menu Menu;
        static readonly HttpClient http = new HttpClient { Timeout = System.TimeSpan.FromSeconds(15) };
        /// <summary>Reclined: recentring aligns the room with the view, pitch and roll too.</summary>
        bool reclined;
        float stickDown = -1;
        bool stickLong;
        XRNode pointerHand = XRNode.RightHand;
        int stickPage; // last thumbstick page direction (edge-detect flicks)

        float smokeAt = -1; // -vclSmoke SECS: report what played, then quit (headless tests)

        void Start()
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-vclSmoke") smokeAt = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
            Build();
            var url = ServerUrl();
            Debug.Log($"[vcl] server {url}");
            conn = new Connection(url);
            Runtime.Stepping = PlayerPrefs.GetInt("vcl.step", 0) == 1;
            reclined = PlayerPrefs.GetInt("vcl.recline", 0) == 1;
            FetchLessons();
        }

        /// <summary>http://host:port of the server the WebSocket talks to.</summary>
        string HttpBase => conn == null ? "http://127.0.0.1:8093" : System.Text.RegularExpressions.Regex.Replace(conn.Url, "^ws", "http").Replace("/ws", "");

        // ── lessons over HTTP (await resumes on Unity's main thread) ──

        async void FetchLessons()
        {
            var list = new List<LessonInfo>();
            try
            {
                var builtIn = (List<object>)Json.Parse(await http.GetStringAsync(HttpBase + "/lessons/index.json"));
                foreach (Dictionary<string, object> l in builtIn)
                    list.Add(new LessonInfo { Path = "lessons/" + l["file"], Title = l["title"] as string, Folder = "Built-in lessons" });
                try
                {
                    var lib = (Dictionary<string, object>)Json.Parse(await http.GetStringAsync(HttpBase + "/library/index.json"));
                    foreach (Dictionary<string, object> l in (List<object>)lib["lessons"])
                    {
                        var file = (string)l["file"];
                        int slash = file.LastIndexOf('/');
                        list.Add(new LessonInfo { Path = "library/" + file, Title = l["title"] as string, Folder = slash > 0 ? file.Substring(0, slash) : "Library" });
                    }
                }
                catch (HttpRequestException) { } // no --lessons library
                Menu.Status = $"{list.Count} lessons from {HttpBase}";
            }
            catch (System.Exception e)
            {
                Menu.Status = $"can't list lessons from {HttpBase}: {e.Message}";
            }
            Menu.Lessons = list;
            if (Menu.Open) Menu.Rebuild();
        }

        async void PlayLesson(LessonInfo l)
        {
            try
            {
                var text = await http.GetStringAsync($"{HttpBase}/{System.Uri.EscapeUriString(l.Path)}");
                StopLesson(false);
                await http.PostAsync(HttpBase + "/vcl?mode=vcl", new StringContent("clear\nboard\n"));
                if (!text.EndsWith("\n")) text += "\n";
                var mode = l.Path.EndsWith(".md") ? "mixed" : "vcl";
                var res = await http.PostAsync($"{HttpBase}/vcl?mode={mode}", new StringContent(text));
                Debug.Log($"[vcl] played {l.Path}: {(await res.Content.ReadAsStringAsync()).Trim().Split('\n').LastOrDefault()}");
            }
            catch (System.Exception e) { Log("error", $"{l.Path}: {e.Message}"); }
        }

        /// <summary>Drop what is playing here (and, unless only local, tell the server to clear).</summary>
        void StopLesson(bool tellServer)
        {
            Runtime.Reset();
            Board.Apply(Board.Default);
            Avatar.Say("", 0);
            if (tellServer) _ = http.PostAsync(HttpBase + "/vcl?mode=vcl", new StringContent("clear\nboard\n"));
        }

        void LoadTexture(string url, System.Action<Texture2D> done) => LoadBytes(url, bytes =>
        {
            var tex = new Texture2D(2, 2);
            done(bytes != null && tex.LoadImage(bytes) ? tex : null);
        });

        /// <summary>Where lesson files are read from directly (the editor snapshot test
        /// points it at viewer/); otherwise they come from the server over HTTP.</summary>
        public string AssetRoot;

        async void LoadBytes(string url, System.Action<byte[]> done)
        {
            byte[] bytes = null;
            try
            {
                if (AssetRoot != null && !url.StartsWith("http") && System.IO.File.Exists(System.IO.Path.Combine(AssetRoot, url)))
                    bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(AssetRoot, url));
                else
                {
                    var full = url.StartsWith("http") ? url : $"{HttpBase}/{url.TrimStart('/')}";
                    bytes = await http.GetByteArrayAsync(full);
                }
            }
            catch (System.Exception e) { Debug.LogWarning($"[vcl] fetch {url}: {e.Message}"); }
            done(bytes);
        }

        /// <summary>Everything except the connection (also used by the editor snapshot test).</summary>
        public void Build()
        {
            if (!Cam) Cam = Camera.main;
            if (!Cam)
            {
                var cg = new GameObject("Main Camera") { tag = "MainCamera" };
                Cam = cg.AddComponent<Camera>();
            }
            Cam.clearFlags = CameraClearFlags.SolidColor;
            Cam.backgroundColor = Draw.Hex("#0b0f16");
            Cam.nearClipPlane = 0.03f;
            Cam.farClipPlane = 60;
            Cam.fieldOfView = 55;
            Cam.transform.position = new Vector3(0, 1.6f, 0.05f);
            Cam.transform.rotation = Quaternion.identity;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = Cam.backgroundColor;
            RenderSettings.fogStartDistance = 6;
            RenderSettings.fogEndDistance = 16;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Draw.Hex("#c8d8ff") * 0.9f;
            RenderSettings.ambientEquatorColor = Draw.Hex("#5a6478") * 0.9f;
            RenderSettings.ambientGroundColor = Draw.Hex("#1a1f2a");

            var sun = new GameObject("sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.1f;
            sun.shadows = LightShadows.None;
            sun.transform.rotation = Quaternion.LookRotation(-new Vector3(1.5f, 4, -2));

            Classroom = new GameObject("classroom").transform;
            var floorMat = Draw.Unlit(Draw.Hex("#0e131b"), 2000, false);
            floorMat.SetFloat("_ZWrite", 1);
            var floor = Draw.MeshObject(Classroom, "floor", Draw.Primitive(PrimitiveType.Quad), floorMat, -20);
            floor.transform.localRotation = Quaternion.Euler(90, 0, 0);
            floor.transform.localScale = new Vector3(14, 14, 1);
            floor.transform.localPosition = new Vector3(0, 0, 1.5f);

            var stage = Draw.Child(Classroom, "stage").transform;
            stage.localScale = Vector3.one * Draw.StageScale;
            Board = new Board(stage, BoardCenter);
            Avatar = new Avatar(Classroom, new Vector3(-1.55f, 0.25f, 2.1f), new Vector3(0, BoardCenter.y - 0.86f, 2.15f));
            // The teacher stands just left of the board; subtitles hang under its front edge.
            float gap = -1.55f + 8.5f * Draw.StageScale, subZ = Avatar.SubRoot.localPosition.z;
            Board.Changed += s =>
            {
                var p = Avatar.Root.localPosition;
                p.x = -(s.x / 2 + 0.5f) * Draw.StageScale + gap;
                Avatar.Root.localPosition = p;
                var q = Avatar.SubRoot.localPosition;
                q.z = subZ - s.z * Draw.StageScale;
                Avatar.SubRoot.localPosition = q;
                if (!xr) FitCamera();
            };
            Board.Apply(Board.Default);

            status = Draw.Text(Classroom, "", 0.045f, Draw.Hex("#9fb4cc"), "left", false);
            status.renderer.sortingOrder = 6;

            Runtime = new Runtime(stage, Board, Avatar, Cam, Log);
            Runtime.LoadTexture = (u, done) => LoadTexture(u, done);
            Runtime.LoadBytes = (u, done) => LoadBytes(u, done);
            Menu = new Menu
            {
                Play = PlayLesson,
                Stop = () => StopLesson(true),
                ToggleStep = ToggleStep,
                Stepping = () => Runtime.Stepping,
                Reclined = () => reclined,
                Upright = () => SetReclined(false),
                Recline = () => SetReclined(true),
                Refresh = FetchLessons,
                Voiced = () => Avatar.Voice.On,
                ToggleVoice = () => Avatar.Voice.SetOn(!Avatar.Voice.On),
            };
            if (!Cam.GetComponent<AudioListener>()) Cam.gameObject.AddComponent<AudioListener>();
            xr = XRSettings.isDeviceActive;
            if (xr)
            {
                // Heights are from the floor (the board is 1.55 m up).
                var subs = new List<XRInputSubsystem>();
                SubsystemManager.GetInstances(subs);
                foreach (var sub in subs) sub.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
                Application.onBeforeRender += TrackHead;
            }
            else FitCamera();
        }

        void OnDestroy()
        {
            Application.onBeforeRender -= TrackHead;
            conn?.Dispose();
        }

        static string ServerUrl()
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-vcl") return args[i + 1];
            try
            {
                var f = Path.Combine(Application.persistentDataPath, "server.txt");
                if (File.Exists(f))
                {
                    var s = File.ReadAllText(f).Trim();
                    if (s.Length > 0) return s;
                }
            }
            catch (IOException) { }
            return "ws://127.0.0.1:8093/ws";
        }

        public void Log(string level, string msg)
        {
            if (level == "error") Debug.LogWarning("[vcl] " + msg); else Debug.Log($"[vcl] {level}: {msg}");
            if (level != "error" && level != "warning") return;
            logLines.Insert(0, (level == "error" ? "<color=#ef5350>" : "<color=#ffa726>") + msg + "</color>");
            if (logLines.Count > 4) logLines.RemoveAt(logLines.Count - 1);
        }

        /// <summary>Run one line of IR JSON.</summary>
        public void Feed(string json, bool instant = false)
        {
            try { Runtime.Exec(new Cmd((Dictionary<string, object>)Json.Parse(json)), instant); }
            catch (System.Exception e) { Log("error", "bad IR: " + e.Message); }
        }

        void Update()
        {
            if (conn != null)
                for (int n = 0; n < 200 && conn.Inbox.TryDequeue(out var msg); n++) Feed(msg);
            HandleInput();
            Runtime.Update(Time.deltaTime);
            Avatar.Update(Time.deltaTime, Cam.transform.position);
            UpdateStatus();
            if (smokeAt > 0 && Time.realtimeSinceStartup > smokeAt)
            {
                Debug.Log($"[vcl] smoke: link={conn?.State} entities={Runtime.Entities.Count} [{string.Join(",", Runtime.Entities.Keys)}] busy={Runtime.Timeline.Busy} waiting={Runtime.Waiting} board={Board.Size}");
                smokeAt = -1;
                Application.Quit();
            }
        }

        // ── headset ──

        void TrackHead()
        {
            var head = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
            if (head.TryGetFeatureValue(CommonUsages.centerEyePosition, out var pos)) Cam.transform.localPosition = pos;
            if (head.TryGetFeatureValue(CommonUsages.centerEyeRotation, out var rot)) Cam.transform.localRotation = rot;
            if (!recentred && head.isValid && pos != Vector3.zero)
            {
                Recentre();
                recentred = true;
                var usages = new List<InputFeatureUsage>();
                var hand = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
                if (hand.isValid && hand.TryGetFeatureUsages(usages))
                    Debug.Log($"[vcl] right hand '{hand.name}': {string.Join(", ", usages.Select(u => u.name))}");
            }
        }

        void SetReclined(bool on)
        {
            reclined = on;
            PlayerPrefs.SetInt("vcl.recline", on ? 1 : 0);
            Recentre();
        }

        /// <summary>Put the classroom in front of wherever the learner now looks. Upright:
        /// turn it about the vertical only. Reclined: align it with the view itself
        /// (pitch and roll too), so the board is ahead of your eyes lying down.</summary>
        void Recentre()
        {
            if (reclined)
            {
                var r = Cam.transform.rotation;
                Classroom.SetPositionAndRotation(Cam.transform.position - r * new Vector3(0, 1.6f, 0.05f), r);
                return;
            }
            var fwd = Cam.transform.forward;
            fwd.y = 0;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            var yaw = Quaternion.LookRotation(fwd.normalized, Vector3.up);
            var p = Cam.transform.position;
            Classroom.SetPositionAndRotation(new Vector3(p.x, 0, p.z) - yaw * new Vector3(0, 0, 0.05f), yaw);
        }

        bool Pressed(XRNode hand, InputFeatureUsage<bool> usage, string key)
        {
            var d = InputDevices.GetDeviceAtXRNode(hand);
            bool down = d.isValid && d.TryGetFeatureValue(usage, out var v) && v;
            wasPressed.TryGetValue(key, out var was);
            wasPressed[key] = down;
            return down && !was;
        }

        void ToggleStep()
        {
            Runtime.Stepping = !Runtime.Stepping;
            PlayerPrefs.SetInt("vcl.step", Runtime.Stepping ? 1 : 0);
            if (!Runtime.Stepping) Runtime.Next();
        }

        static bool Held(XRNode hand, InputFeatureUsage<bool> usage)
        {
            var d = InputDevices.GetDeviceAtXRNode(hand);
            return d.isValid && d.TryGetFeatureValue(usage, out var v) && v;
        }

        /// <summary>The controller's pointing ray (OpenXR aim pose; else the grip pose tilted forward).</summary>
        static Ray? HandRay(XRNode hand)
        {
            var d = InputDevices.GetDeviceAtXRNode(hand);
            if (!d.isValid) return null;
            if (d.TryGetFeatureValue(new InputFeatureUsage<Vector3>("PointerPosition"), out var p) &&
                d.TryGetFeatureValue(new InputFeatureUsage<Quaternion>("PointerRotation"), out var q) && q != default)
                return new Ray(p, q * Vector3.forward);
            if (d.TryGetFeatureValue(CommonUsages.devicePosition, out p) && d.TryGetFeatureValue(CommonUsages.deviceRotation, out q))
                return new Ray(p, q * Quaternion.Euler(35, 0, 0) * Vector3.forward);
            return null;
        }

        void HandleInput()
        {
            bool next = false, toggleStep = false, recentre = false, menu = false, press = false;
            int page = 0, keyMove = 0;
            if (xr)
            {
                foreach (var hand in new[] { XRNode.LeftHand, XRNode.RightHand })
                {
                    bool trig = Pressed(hand, CommonUsages.triggerButton, hand + "trig");
                    if (trig) pointerHand = hand;
                    next |= trig;
                    press |= trig;
                    toggleStep |= Pressed(hand, CommonUsages.secondaryButton, hand + "b");
                    menu |= Pressed(hand, CommonUsages.primaryButton, hand + "a");
                    var dev = InputDevices.GetDeviceAtXRNode(hand);
                    if (dev.isValid && dev.TryGetFeatureValue(CommonUsages.primary2DAxis, out var stick) && Mathf.Abs(stick.y) > 0.7f) page = stick.y > 0 ? -1 : 1;
                }
                // Thumbstick click: recentre; hold it (0.8 s): switch upright ↔ reclined.
                bool stickHeld = Held(XRNode.LeftHand, CommonUsages.primary2DAxisClick) || Held(XRNode.RightHand, CommonUsages.primary2DAxisClick);
                if (stickHeld && stickDown < 0) { stickDown = Time.time; stickLong = false; }
                if (stickHeld && !stickLong && Time.time - stickDown > 0.8f) { stickLong = true; SetReclined(!reclined); }
                if (!stickHeld && stickDown >= 0) { if (!stickLong) recentre = true; stickDown = -1; }
            }
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null)
            {
                bool enter = kb.enterKey.wasPressedThisFrame;
                menu |= kb.mKey.wasPressedThisFrame || (Menu.Open && kb.escapeKey.wasPressedThisFrame);
                if (Menu.Open)
                {
                    keyMove = kb.downArrowKey.wasPressedThisFrame ? 1 : kb.upArrowKey.wasPressedThisFrame ? -1 : 0;
                    press |= enter;
                    if (keyMove != 0 || enter) Menu.Key(keyMove, enter);
                }
                else next |= kb.spaceKey.wasPressedThisFrame || kb.rightArrowKey.wasPressedThisFrame || enter;
                toggleStep |= kb.sKey.wasPressedThisFrame;
            }
#endif
            if (menu) Menu.Toggle(Cam.transform);
            if (Menu.Open)
            {
                if (xr) Menu.Point(HandRay(pointerHand), press);
                if (page != 0 && page != stickPage) Menu.Page(page);
                stickPage = page;
            }
            else if (next) Runtime.Next();
            if (toggleStep) ToggleStep();
            if (recentre) Recentre();
        }

        void UpdateStatus()
        {
            if (!status) return;
            string link = conn == null ? "" : conn.State == "live" ? "<color=#66bb6a>● live</color>" : $"<color=#ffa726>● {conn.State}</color> {conn.Url}" + (conn.LastError != null ? $"\n<size=70%>{conn.LastError}</size>" : "");
            string play = Runtime.Waiting ? "<color=#ffe066>paused · trigger for next</color>" : Runtime.Timeline.Busy ? "playing" : "";
            string step = (Runtime.Stepping ? "  step mode" : "") + (reclined ? "  reclined" : "");
            status.text = $"{link}  {play}{step}" + (logLines.Count > 0 ? "\n<size=80%>" + string.Join("\n", logLines) + "</size>" : "");
            // Top-left corner of the board, just above it.
            float hw = Board.Width / 2 + 0.5f, top = Board.Height / 2 + 0.4f;
            var stage = Runtime.Stage;
            status.transform.position = stage.TransformPoint(new Vector3(-hw, top + 0.5f, 0));
            status.transform.rotation = stage.rotation;
        }

        // ── desktop ──

        /// <summary>Fit the board, its stage, the teacher and the subtitles into the window.</summary>
        public void FitCamera()
        {
            if (!Cam || xr) return;
            var pts = new List<Vector3>(Board.WorldCorners());
            var a = Avatar.Root.position;
            var sub = Avatar.SubRoot.position;
            pts.Add(new Vector3(a.x - 0.32f, sub.y - 0.12f, a.z));
            pts.Add(new Vector3(a.x - 0.32f, BoardCenter.y + 0.2f, a.z));
            pts.Add(new Vector3(sub.x, sub.y - 0.12f, sub.z));
            var b = new Bounds(pts[0], Vector3.zero);
            foreach (var p in pts) b.Encapsulate(p);
            var center = new Vector3(Runtime?.Stage.position.x ?? 0, b.center.y, b.center.z);
            var dir = new Vector3(0, 0.02f, -1).normalized;
            bool Fits(float dist)
            {
                Cam.transform.position = center + dir * dist;
                Cam.transform.LookAt(center);
                foreach (var p in pts)
                {
                    var v = Cam.WorldToViewportPoint(p);
                    if (v.z <= 0 || v.x < 0.02f || v.x > 0.98f || v.y < 0.03f || v.y > 0.97f) return false;
                }
                return true;
            }
            float lo = 0.5f, hi = 30;
            for (int i = 0; i < 30; i++) { float m = (lo + hi) / 2; if (Fits(m)) hi = m; else lo = m; }
            Fits(hi);
        }
    }
}
