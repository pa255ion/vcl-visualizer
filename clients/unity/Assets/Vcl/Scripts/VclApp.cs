// Entry point: builds the classroom (board, teacher, lights), connects to
// `vcl serve`, and feeds IR into the Runtime. Works in a headset (OpenXR) and,
// without one, as a flat desktop view.
//
// Server address, first found wins:
//   1. command line:  -vcl ws://host:8093/ws   (Android: adb shell am start ... -e unity "-vcl ws://…")
//   2. <persistentDataPath>/server.txt         (Quest: tools/set-server.sh)
//   3. ws://127.0.0.1:8093/ws                   (Quest over USB after `adb reverse tcp:8093 tcp:8093`)
//
// Controls (headset): A/X = lesson menu, trigger = press what the ray points
// at (menu, question, transcript), else next step in step mode; B/Y = pause /
// resume; thumbstick left/right = back / forward a step; thumbstick click =
// recentre, hold it = switch between upright and reclined (the room follows
// your view, for lying down). Move mode (menu): left stick walks, right
// stick turns (left/right) and rises or sinks (up/down); stepping is then on
// the transcript's buttons. Desktop: M = menu (arrows + Enter), Space =
// pause (or next, in step mode), ←/→ = back/forward a step, Enter = next,
// S = step mode, the mouse presses buttons.
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
        public Transcript Transcript;
        LineRenderer uiLaser;
        Transform uiDot;
        Hit uiHover, uiHeld;
        XRNode? uiHand;
        bool wasBusy;
        string finishedNote = "";
        string practiceNote; // set when a practice ends
        int stickSide;
        /// <summary>The tracked space: the camera moves in it, and it moves (move mode) in the world.</summary>
        public Transform Rig;
        public Scenery Scenery;
        public Ambience Ambience;
        Light sun;
        MeshRenderer floor;
        bool moving;
        static readonly float[] Speeds = { 1, 1.25f, 1.5f, 1.75f, 2, 0.75f };
        /// <summary>VRM avatars on the server (assets/…vrm): the teacher choices after the robot.</summary>
        readonly List<string> teachers = new List<string>();
        string teacher = ""; // "" = the robot
        bool loadingTeacher;
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
            Runtime.SetSpeed(PlayerPrefs.GetFloat("vcl.speed", 1));
            moving = PlayerPrefs.GetInt("vcl.move", 0) == 1;
            teacher = PlayerPrefs.GetString("vcl.teacher", "");
            if (teacher.Length > 0) LoadTeacher(teacher);
            reclined = PlayerPrefs.GetInt("vcl.recline", 0) == 1;
            FetchLessons();
        }

        /// <summary>http://host:port of the server the WebSocket talks to.</summary>
        string HttpBase => conn == null ? "http://127.0.0.1:8093" : System.Text.RegularExpressions.Regex.Replace(conn.Url, "^ws", "http").Replace("/ws", "");

        // ── lessons over HTTP (await resumes on Unity's main thread) ──

        /// <summary>Make `path` (a .vrm under the server's assets, or "" for the robot) the teacher.</summary>
        public void LoadTeacher(string path)
        {
            teacher = path;
            if (path.Length == 0) { Avatar.SetModel(null); Menu?.Rebuild(); return; }
            loadingTeacher = true;
            Menu?.Rebuild();
            LoadBytes(path, async bytes =>
            {
                try
                {
                    if (bytes == null) throw new System.Exception("couldn't fetch it");
                    var m = await VrmTeacher.Load(bytes, path, Classroom, Avatar.FloorSpot);
                    if (teacher == path) Avatar.SetModel(m); else m.Dispose();
                }
                catch (System.Exception e) { Log("warning", $"teacher {path}: {e.Message}"); if (teacher == path) Avatar.SetModel(null); }
                loadingTeacher = false;
                Menu?.Rebuild();
            });
        }

        async void FetchLessons()
        {
            // VRM avatars among the server's assets can be the teacher.
            try
            {
                var assets = (Dictionary<string, object>)Json.Parse(await http.GetStringAsync(HttpBase + "/assets/index.json"));
                teachers.Clear();
                foreach (var f in (List<object>)assets["files"])
                    if (f is string s && s.EndsWith(".vrm", System.StringComparison.OrdinalIgnoreCase)) teachers.Add("assets/" + s);
            }
            catch (System.Exception e) { Debug.Log($"[vcl] no asset list: {e.Message}"); }
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
            // The camera lives in a rig: tracking moves it within, move mode moves the rig.
            if (!Rig)
            {
                Rig = new GameObject("rig").transform;
                Cam.transform.SetParent(Rig, true);
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

            sun = new GameObject("sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.1f;
            sun.shadows = LightShadows.None;
            sun.transform.rotation = Quaternion.LookRotation(-new Vector3(1.5f, 4, -2));

            Classroom = new GameObject("classroom").transform;
            var floorMat = Draw.Unlit(Draw.Hex("#0e131b"), 2000, false);
            floorMat.SetFloat("_ZWrite", 1);
            floor = Draw.MeshObject(Classroom, "floor", Draw.Primitive(PrimitiveType.Quad), floorMat, -20);
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
            Runtime.OnAnswer = a =>
            {
                PostAnswer(a);
                if (!(bool)a["skipped"]) Ambience?.Answer((bool)a["correct"]);
            };
            Runtime.OnBegin = SoundFor;
            Runtime.OnPractice = (right, total) =>
            {
                practiceNote = "✓ Practice done" + (total > 0 ? $" · {right} of {total} right" : "");
                Avatar.Hear(practiceNote, false, 8);
                Log("info", practiceNote);
            };
            Scenery = new Scenery(Classroom, Cam, sun, floor.gameObject);
            Scenery.Set(PlayerPrefs.GetString("vcl.scene", "night"));
            if (Application.isPlaying)
            {
                Ambience = new GameObject("ambience").AddComponent<Ambience>();
                Ambience.SetMusicVolume(PlayerPrefs.GetFloat("vcl.musicVol", 0.6f));
                Ambience.SetEffectsVolume(PlayerPrefs.GetFloat("vcl.fxVol", 0.7f));
                Ambience.SetScene(Scenery.Name);
            }
            Transcript = new Transcript(Runtime);
            uiLaser = new GameObject("ui laser").AddComponent<LineRenderer>();
            uiLaser.useWorldSpace = true;
            uiLaser.widthMultiplier = 0.004f;
            uiLaser.sharedMaterial = Draw.Unlit(Draw.Hex("#8ec5ff"), 3150);
            uiLaser.sortingOrder = 60;
            uiLaser.positionCount = 2;
            uiLaser.enabled = false;
            uiDot = Draw.MeshObject(null, "ui dot", Draw.Disc(0.008f), Draw.Unlit(Color.white, 3160), 61).transform;
            uiDot.gameObject.SetActive(false);
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
                SpeedLabel = () => $"{Runtime.Speed:0.##}×",
                CycleSpeed = () =>
                {
                    int i = System.Array.FindIndex(Speeds, s => Mathf.Approximately(s, Runtime.Speed));
                    Runtime.SetSpeed(Speeds[(i + 1) % Speeds.Length]);
                    PlayerPrefs.SetFloat("vcl.speed", Runtime.Speed);
                },
                Moving = () => moving,
                ToggleMove = () => { moving = !moving; PlayerPrefs.SetInt("vcl.move", moving ? 1 : 0); },
                TeacherLabel = () => loadingTeacher ? "loading…" : teacher.Length == 0 ? "Robot" : System.IO.Path.GetFileNameWithoutExtension(teacher),
                CycleTeacher = () =>
                {
                    var all = new List<string> { "" };
                    all.AddRange(teachers);
                    if (teacher.Length > 0 && !all.Contains(teacher)) all.Add(teacher);
                    var next = all[(all.IndexOf(teacher) + 1) % all.Count];
                    PlayerPrefs.SetString("vcl.teacher", next);
                    LoadTeacher(next);
                },
                Scenes = Scenery.Names,
                Scene = () => Scenery.Name,
                SetScene = name =>
                {
                    Scenery.Set(name);
                    Ambience?.SetScene(name);
                    PlayerPrefs.SetString("vcl.scene", name);
                },
                MusicVolume = () => Ambience ? Ambience.MusicVolume : 0,
                EffectsVolume = () => Ambience ? Ambience.EffectsVolume : 0,
                SetMusicVolume = v => { if (Ambience) { Ambience.SetMusicVolume(v); PlayerPrefs.SetFloat("vcl.musicVol", v); } },
                SetEffectsVolume = v => { if (Ambience) { Ambience.SetEffectsVolume(v); PlayerPrefs.SetFloat("vcl.fxVol", v); } },
                TranscriptShown = () => Transcript.Visible,
                ToggleTranscript = () => Transcript.Visible = !Transcript.Visible,
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
            Scenery.Update(Time.deltaTime);
            Transcript.Update();
            Avatar.Update(Time.deltaTime, Cam.transform.position);
            UpdateStatus();
            if (smokeAt > 0 && Time.realtimeSinceStartup > smokeAt)
            {
                Debug.Log($"[vcl] smoke: link={conn?.State} entities={Runtime.Entities.Count} [{string.Join(",", Runtime.Entities.Keys)}] busy={Runtime.Timeline.Busy} waiting={Runtime.Waiting} board={Board.Size}");
                smokeAt = -1;
                Application.Quit();
            }
        }

        // ── scene sounds ──

        static readonly HashSet<string> Pops = new HashSet<string> { "pt", "node", "box", "sphere", "cyl", "model" };
        static readonly HashSet<string> Fades = new HashSet<string> { "text", "tex", "title", "img", "svg", "fig", "code", "table", "grid", "bits", "band" };
        static readonly HashSet<string> Draws = new HashSet<string> { "line", "arrow", "path", "poly", "rect", "circle", "angle", "edge", "axes", "plot", "surf",
            "part", "wire", "block", "frame", "wave", "nn", "bars", "hist", "scatter", "errbar", "contour" };
        float lastSound;

        /// <summary>A soft sound for each thing that appears (not too many at once); the music dips under speech.</summary>
        void SoundFor(Task t)
        {
            if (!Ambience || t.Cmd == null) return;
            string op = t.Cmd.Op;
            float secs = (float)t.Dur / Runtime.Speed;
            if (secs < 0.05f) return; // instant: replaying or skipping
            if (op == "say") { Ambience.Duck(secs); return; }
            if (op == "quiz" || op == "pick" || op == "estimate" || op == "recall") { Ambience.Question(); return; }
            if (Time.realtimeSinceStartup - lastSound < 0.09f) return;
            if (Draws.Contains(op)) Ambience.DrawSound(secs);
            else if (Pops.Contains(op)) Ambience.Pop();
            else if (Fades.Contains(op)) Ambience.Fade();
            else return;
            lastSound = Time.realtimeSinceStartup;
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

        /// <summary>The controller's pointing ray in world space (OpenXR aim pose; else the grip pose
        /// tilted forward). Poses come in tracking space, which the rig carries when moving.</summary>
        Ray? HandRay(XRNode hand)
        {
            var d = InputDevices.GetDeviceAtXRNode(hand);
            if (!d.isValid) return null;
            Ray? local = null;
            if (d.TryGetFeatureValue(new InputFeatureUsage<Vector3>("PointerPosition"), out var p) &&
                d.TryGetFeatureValue(new InputFeatureUsage<Quaternion>("PointerRotation"), out var q) && q != default)
                local = new Ray(p, q * Vector3.forward);
            else if (d.TryGetFeatureValue(CommonUsages.devicePosition, out p) && d.TryGetFeatureValue(CommonUsages.deviceRotation, out q))
                local = new Ray(p, q * Quaternion.Euler(35, 0, 0) * Vector3.forward);
            if (!local.HasValue || !Rig) return local;
            return new Ray(Rig.TransformPoint(local.Value.origin), Rig.TransformDirection(local.Value.direction));
        }

        /// <summary>Answers go to the server, which passes them to the tutor.</summary>
        void PostAnswer(Dictionary<string, object> a)
        {
            Log("info", $"answer ({a["kind"]}): {((bool)a["skipped"] ? "skipped" : (bool)a["correct"] ? "right" : "wrong")}");
            if (conn == null) return;
            _ = http.PostAsync(HttpBase + "/answer", new StringContent(Json.Write(a)));
        }

        /// <summary>What the learner can press outside the menu: the open question, the transcript.</summary>
        IEnumerable<Hit> UiHits()
        {
            if (Runtime.Question != null) foreach (var h in Runtime.Question.Hits) yield return h;
            if (Transcript.Visible) foreach (var h in Transcript.Hits) yield return h;
        }

        /// <summary>The nearest hit along a ray (higher Order wins ties), with where it is hit.</summary>
        Hit UiTest(Ray ray, out Vector3 at)
        {
            Hit best = null;
            float bestD = float.MaxValue;
            at = Vector3.zero;
            foreach (var h in UiHits())
            {
                var p = h.Test(ray, out var d);
                if (!p.HasValue) continue;
                d -= h.Order * 0.01f;
                if (d < bestD) { best = h; bestD = d; at = h.T.TransformPoint(p.Value); }
            }
            return best;
        }

        void SetUiHover(Hit h)
        {
            if (uiHover == h) return;
            uiHover?.Hover?.Invoke(false);
            uiHover = h;
            uiHover?.Hover?.Invoke(true);
        }

        /// <summary>Aim at the question and transcript; returns true if `press` pressed something.</summary>
        bool PointUi(Ray? ray, bool press, bool held)
        {
            if (!ray.HasValue) { SetUiHover(null); uiLaser.enabled = false; uiDot.gameObject.SetActive(false); return false; }
            var r = ray.Value;
            var hit = UiTest(r, out var at);
            if (uiHeld != null && !held) uiHeld = null;
            if (uiHeld?.Drag != null)
            {
                var p = uiHeld.PlanePoint(r, out _);
                if (p.HasValue) { uiHeld.Drag(p.Value); at = uiHeld.T.TransformPoint(p.Value); hit = uiHeld; }
            }
            SetUiHover(hit);
            bool show = xr && hit != null;
            uiLaser.enabled = show;
            uiDot.gameObject.SetActive(show);
            if (show)
            {
                uiLaser.SetPosition(0, r.origin);
                uiLaser.SetPosition(1, at);
                uiDot.position = at;
                uiDot.rotation = Quaternion.LookRotation(r.direction);
            }
            if (!press || hit == null) return false;
            uiHeld = hit;
            var at0 = hit.PlanePoint(r, out _);
            if (at0.HasValue) hit.PressAt?.Invoke(at0.Value);
            if (hit.Drag != null && at0.HasValue) hit.Drag(at0.Value);
            hit.Press?.Invoke();
            return true;
        }

        void HandleInput()
        {
            bool next = false, toggleStep = false, recentre = false, menu = false, press = false, pause = false, held = false;
            int page = 0, keyMove = 0, side = 0;
            Ray? uiRay = null;
            if (xr)
            {
                foreach (var hand in new[] { XRNode.LeftHand, XRNode.RightHand })
                {
                    bool trig = Pressed(hand, CommonUsages.triggerButton, hand + "trig");
                    if (trig) pointerHand = hand;
                    next |= trig;
                    press |= trig;
                    pause |= Pressed(hand, CommonUsages.secondaryButton, hand + "b");
                    menu |= Pressed(hand, CommonUsages.primaryButton, hand + "a");
                    var dev = InputDevices.GetDeviceAtXRNode(hand);
                    if (dev.isValid && dev.TryGetFeatureValue(CommonUsages.primary2DAxis, out var stick))
                    {
                        if (Mathf.Abs(stick.y) > 0.7f) page = stick.y > 0 ? -1 : 1;
                        if (!moving && Mathf.Abs(stick.x) > 0.7f && Mathf.Abs(stick.y) < 0.5f) side = stick.x > 0 ? 1 : -1;
                    }
                }
                // The UI pointer: the hand that last pulled the trigger, or whichever points at something.
                var mainRay = HandRay(pointerHand);
                var otherHand = pointerHand == XRNode.RightHand ? XRNode.LeftHand : XRNode.RightHand;
                var otherRay = HandRay(otherHand);
                if (mainRay.HasValue && UiTest(mainRay.Value, out _) != null) uiHand = pointerHand;
                else if (otherRay.HasValue && UiTest(otherRay.Value, out _) != null) uiHand = otherHand;
                else uiHand = pointerHand;
                uiRay = HandRay(uiHand.Value);
                held = Held(uiHand.Value, CommonUsages.triggerButton);
                // Only the trigger of the hand aiming at the UI presses it (the other one's is "next").
                if (press && uiHand != pointerHand) press = false;
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
                else
                {
                    next |= enter;
                    if (kb.spaceKey.wasPressedThisFrame) { if (Runtime.Waiting) next = true; else pause = true; }
                    if (kb.rightArrowKey.wasPressedThisFrame) side = 1;
                    if (kb.leftArrowKey.wasPressedThisFrame) side = -1;
                }
                toggleStep |= kb.sKey.wasPressedThisFrame;
            }
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (!xr && mouse != null && Cam)
            {
                uiRay = Cam.ScreenPointToRay(mouse.position.ReadValue());
                press |= mouse.leftButton.wasPressedThisFrame;
                held = mouse.leftButton.isPressed;
            }
#endif
            if (xr && moving && !Menu.Open) Locomote(Time.deltaTime);
            if (menu) Menu.Toggle(Cam.transform);
            if (Menu.Open)
            {
                PointUi(null, false, false);
                if (xr) Menu.Point(HandRay(pointerHand), press);
                if (page != 0 && page != stickPage) Menu.Page(page);
                stickPage = page;
            }
            else
            {
                // The trigger presses what it points at; otherwise it is "next step" (step mode).
                bool pressedUi = PointUi(uiRay, press, held);
                if (next && !pressedUi && xr) Runtime.Next();
                else if (next && !xr && !press) Runtime.Next();
                if (pause) Runtime.TogglePause();
                if (side != 0 && side != stickSide) { if (side > 0) Runtime.Forward(); else Runtime.Back(); }
            }
            stickSide = side;
            if (toggleStep) ToggleStep();
            if (recentre) Recentre();
        }

        /// <summary>Move mode: left stick walks where you look, right stick turns (x) and
        /// rises or sinks (y).</summary>
        void Locomote(float dt)
        {
            Vector2 Stick(XRNode hand)
            {
                var d = InputDevices.GetDeviceAtXRNode(hand);
                if (!d.isValid || !d.TryGetFeatureValue(CommonUsages.primary2DAxis, out var v)) return Vector2.zero;
                return new Vector2(Mathf.Abs(v.x) > 0.15f ? v.x : 0, Mathf.Abs(v.y) > 0.15f ? v.y : 0);
            }
            var walk = Stick(XRNode.LeftHand);
            var turn = Stick(XRNode.RightHand);
            var fwd = Cam.transform.forward;
            fwd.y = 0;
            if (fwd.sqrMagnitude < 0.05f) { fwd = Cam.transform.up; fwd.y = 0; } // lying down: "ahead" is up the view
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            var right = Vector3.Cross(Vector3.up, fwd);
            Rig.position += (fwd * walk.y + right * walk.x) * (1.6f * dt);
            if (turn.x != 0) Rig.RotateAround(Cam.transform.position, Vector3.up, turn.x * 75 * dt);
            if (turn.y != 0)
            {
                var p = Rig.position;
                p.y = Mathf.Clamp(p.y + turn.y * 1.0f * dt, -1.5f, 3f);
                Rig.position = p;
            }
        }

        void UpdateStatus()
        {
            if (!status) return;
            string link = conn == null ? "" : conn.State == "live" ? "<color=#66bb6a>● live</color>" : $"<color=#ffa726>● {conn.State}</color> {conn.Url}" + (conn.LastError != null ? $"\n<size=70%>{conn.LastError}</size>" : "");
            // Playback, and once it ends, "finished" (with the score, if there were questions).
            var state = Runtime.State;
            bool busy = state != "idle";
            if (!busy && wasBusy && practiceNote != null)
            {
                finishedNote = practiceNote; // the practice's own score is showing
                practiceNote = null;
            }
            else if (!busy && wasBusy)
            {
                var (right, total) = Runtime.Score;
                finishedNote = "✓ Finished" + (total > 0 ? $" · {right} of {total} right" : "");
                Avatar.Hear(finishedNote, false, 8);
            }
            if (busy) finishedNote = "";
            wasBusy = busy;
            string play = state == "question" ? "<color=#7fe3ff>your turn: answer the question</color>"
                : state == "paused" ? "<color=#ffa726>paused · B/Y to resume</color>"
                : state == "waiting" ? "<color=#ffe066>step mode · trigger for next</color>"
                : state == "playing" ? (Runtime.Practicing != null ? $"<color=#b58cff>practice · question {Runtime.Practicing.I + 1} of {Runtime.Practicing.Steps.Count}</color>" : "playing")
                : finishedNote.Length > 0 ? $"<color=#66bb6a>{finishedNote}</color>" : "";
            Transcript.Status = play.Length > 0 ? play : "<color=#8796ab>ready</color>";
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
            if (Transcript != null)
            {
                Transcript.Update();
                pts.AddRange(Transcript.WorldCorners());
            }
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
