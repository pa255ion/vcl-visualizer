// Executes VCL IR (docs/IR.md) against a Unity scene: a port of
// viewer/js/runtime.js. Builders for each drawing op are in Builders.cs.
//
// Coordinates: every entity's transform lives in board units under `Stage`
// (or under an axes/group entity). IR vectors are [x, y, z] with z toward the
// learner; Unity's z points away, so B() flips it. Points `on` an axes are
// data coordinates, mapped by that axes (AxesInfo.Map).
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Vcl
{
    /// <summary>Something whose colour/alpha the entity drives (a material or a text).</summary>
    public class Tint
    {
        public string Role; // stroke, fill, solid, text, label
        public Color Color;
        public float BaseAlpha;
        /// <summary>A part faded on its own (fade n.L1 0.2).</summary>
        public float Dim = 1;
        public Action<Color> Apply;
    }

    /// <summary>A named piece of a diagram that hl/color/fade can target (n.L1_3, g.r2c3).</summary>
    public class Part
    {
        public List<Tint> Tints = new List<Tint>();
        public List<Transform> Objs = new List<Transform>();
        public Action Apply;
    }

    /// <summary>What a change op acts on: a whole entity or one of its parts.</summary>
    public class Target
    {
        public Entity E;
        public List<Tint> Tints;
        public List<Transform> Objs;
        public Action Apply;
        public bool Whole;
    }

    public class AxesInfo
    {
        public bool Is3;
        public double[] X, Y, Z;
        public Func<Vector3, Vector3> Map; // data → axes-local (Unity axes)
        public int Plots;
    }

    public class Entity
    {
        public readonly Runtime Rt;
        public readonly string Id, Op;
        public readonly Cmd Cmd;
        public Entity ParentEnt;
        public AxesInfo Axes;      // set when the entity's points are axes data
        public AxesInfo AxesSelf;  // set on axes entities (and figures from code, once their axes are known)
        public List<Action> Waiting; // figures from code: what sits on it, until its axes are known
        public readonly Transform Obj, Body;
        public readonly List<Tint> Tints = new List<Tint>();
        public readonly Dictionary<string, float> Factor = new Dictionary<string, float> { ["fill"] = 1, ["label"] = 1 };
        public float Opacity = 1, TargetOpacity = 1, P;
        public bool Live;
        public Vector3[] Ends; // obj-local
        public Vector3? Spin;  // rad/s, three.js Euler convention
        public Vector3 Euler;  // rad, three.js Euler convention (XYZ)
        public string Fx;
        public Action<float> Render;
        public Action Refresh;
        public Action<string> SetText;
        public Action<double[]> SetFocus;
        /// <summary>Named connection points, obj-local (wires and refs attach here).</summary>
        public Dictionary<string, (Vector3 at, Vector3 dir)> Pins;
        public Dictionary<string, Part> Parts;
        public readonly List<Transform> Billboards = new List<Transform>();
        public readonly List<Stroke> Strokes = new List<Stroke>();
        public TextMeshPro Label;
        public Vector3 LabelShift;
        public (string kind, float hw, float hh)? Shape;

        public Entity(Runtime rt, Cmd cmd, Entity parentEnt)
        {
            Rt = rt;
            Id = cmd.Id;
            Op = cmd.Op;
            Cmd = cmd;
            ParentEnt = parentEnt;
            Axes = parentEnt?.AxesSelf != null && cmd.Has("on") ? parentEnt.AxesSelf : null;
            Obj = Draw.Child(parentEnt != null ? parentEnt.Obj : rt.Stage, cmd.Id ?? cmd.Op).transform;
            Body = Draw.Child(Obj, "body").transform;
            TargetOpacity = cmd.Flt("opacity", 1);
            Fx = cmd.Str("fx") ?? (Runtime.DefaultFx.TryGetValue(Op, out var f) ? f : Runtime.Drawable.Contains(Op) ? "draw" : "fade");
            if (Fx == "draw" && !Runtime.Drawable.Contains(Op)) Fx = "fade";
        }

        public Transform ParentObj => Obj.parent;
        public bool In3D => (Axes != null && Axes.Is3) || (ParentEnt?.AxesSelf != null && ParentEnt.AxesSelf.Is3) || (AxesSelf != null && AxesSelf.Is3);

        public Material Track(Material m, string role, float baseAlpha = 1)
        {
            TrackT(m, role, baseAlpha);
            return m;
        }

        public TextMeshPro Track(TextMeshPro t, string role)
        {
            TrackT(t, role);
            return t;
        }

        public Tint TrackT(Material m, string role, float baseAlpha = 1)
        {
            var c = m.color;
            c.a = 1;
            var tint = new Tint { Role = role, Color = c, BaseAlpha = baseAlpha, Apply = col => { if (m) m.color = col; } };
            Tints.Add(tint);
            return tint;
        }

        public Tint TrackT(TextMeshPro t, string role, float baseAlpha = -1)
        {
            var c = t.color;
            var tint = new Tint { Role = role, Color = c, BaseAlpha = baseAlpha >= 0 ? baseAlpha : c.a, Apply = col => { if (t) t.color = col; } };
            Tints.Add(tint);
            return tint;
        }

        public void ApplyOpacity()
        {
            foreach (var t in Tints)
            {
                float f = Factor.TryGetValue(t.Role, out var x) ? x : 1;
                var c = t.Color;
                c.a = t.BaseAlpha * Opacity * f * t.Dim;
                t.Apply(c);
            }
            bool vis = Opacity > 0.002f;
            if (Body.gameObject.activeSelf != vis) Body.gameObject.SetActive(vis);
            foreach (Transform ch in Obj)
                if (ch != Body && Rt.EntityOf(ch) == null && ch.gameObject.activeSelf != vis) ch.gameObject.SetActive(vis);
        }

        public void SetOpacity(float o)
        {
            Opacity = o;
            ApplyOpacity();
        }

        public IEnumerable<Tint> ColorTints()
        {
            foreach (var t in Tints) if (t.Role == "stroke" || t.Role == "solid" || t.Role == "text") yield return t;
        }

        /// <summary>Entrance animation.</summary>
        public void Enter(float eased, float raw)
        {
            P = eased;
            Factor["label"] = Mathf.Clamp01((eased - 0.6f) / 0.4f);
            switch (Fx)
            {
                case "draw": Render?.Invoke(eased); SetOpacity(TargetOpacity); break;
                case "fade": Render?.Invoke(1); SetOpacity(eased * TargetOpacity); break;
                case "grow": Render?.Invoke(1); Obj.localScale = Vector3.one * Mathf.Max(eased, 1e-3f); SetOpacity(TargetOpacity); break;
                case "pop": Render?.Invoke(1); Obj.localScale = Vector3.one * Mathf.Max(Eases.BackOut(raw), 1e-3f); SetOpacity(TargetOpacity); break;
                default: Render?.Invoke(1); Factor["label"] = 1; SetOpacity(TargetOpacity); break;
            }
        }

        /// <summary>IR point → parent space.</summary>
        public Vector3 ToParent(object pt)
        {
            if (!Runtime.IsRef(pt)) return Axes != null ? Axes.Map(Runtime.Raw(pt)) : Runtime.B(pt);
            return ParentObj.InverseTransformPoint(Rt.RefWorld((Dictionary<string, object>)pt));
        }

        public Vector3 ParentToLocal(Vector3 v) => Obj.InverseTransformPoint(ParentObj.TransformPoint(v));

        public TextMeshPro AddLabel(string text, Vector3 pos, string align = "center")
        {
            var t = Draw.Text(Obj, text, 0.36f, Draw.FG, align);
            Track(t, "label");
            t.transform.localPosition = pos;
            if (In3D) Billboards.Add(t.transform);
            Label = t;
            return t;
        }

        public void Dispose()
        {
            if (Obj) Draw.Kill(Obj.gameObject);
        }
    }

    public partial class Runtime
    {
        public static readonly string[] Palette = { "#42a5f5", "#ffa726", "#66bb6a", "#f06292", "#ab47bc", "#26a69a", "#ffee58" };
        public static readonly Dictionary<string, string> DefaultFx = new Dictionary<string, string>
        {
            ["pt"] = "pop", ["text"] = "fade", ["tex"] = "fade", ["title"] = "fade", ["node"] = "pop", ["img"] = "fade",
            ["svg"] = "fade", ["fig"] = "fade", ["code"] = "fade", ["table"] = "fade", ["grid"] = "fade", ["bits"] = "fade", ["band"] = "fade",
            ["box"] = "grow", ["sphere"] = "grow", ["cyl"] = "grow", ["model"] = "grow",
        };
        public static readonly HashSet<string> Drawable = new HashSet<string>
        {
            "line", "arrow", "path", "poly", "rect", "circle", "angle", "edge", "axes", "plot", "surf",
            "part", "wire", "block", "frame", "wave", "nn", "bars", "hist", "scatter", "errbar", "contour",
        };

        public readonly Transform Stage;
        public readonly Board Board;
        public readonly Avatar Avatar;
        public Camera Camera;
        public readonly Action<string, string> Log;
        public readonly Dictionary<string, Entity> Entities = new Dictionary<string, Entity>();
        readonly Dictionary<Transform, Entity> byObj = new Dictionary<Transform, Entity>();
        public readonly Timeline Timeline;

        /// <summary>Step mode: hold each step after the first of a burst until Next().</summary>
        public bool Stepping;
        int saysInBurst;

        static readonly HashSet<string> Questions = new HashSet<string> { "quiz", "pick", "estimate", "recall" };
        /// <summary>Not replayed when jumping past them: talk, pauses and questions.</summary>
        static readonly HashSet<string> Quiet = new HashSet<string> { "say", "point", "wait", "quiz", "pick", "estimate", "recall" };
        /// <summary>A step starts at each sentence (a `say` without `&`) and each question.</summary>
        static bool StartsStep(Cmd c) => (c.Op == "say" && !c.Par) || Questions.Contains(c.Op);

        /// <summary>Everything run since the last reset, with its step: what going back replays.</summary>
        public readonly List<(Cmd cmd, int step, bool instant)> History = new List<(Cmd, int, bool)>();
        public int Steps { get; private set; }  // steps numbered so far (1 is the first)
        public int Step { get; private set; }   // the step playing, or last played
        double stepAt;
        // The clock: clockAt lesson seconds at real time realAt, running at Speed.
        double clockAt, realAt = Real;
        double? pausedAt;
        public float Speed { get; private set; } = 1;
        public QuestionCard Question { get; private set; }
        public readonly Dictionary<Cmd, QuestionResult> Results = new Dictionary<Cmd, QuestionResult>();
        public Action<Task> OnBegin;
        public Action<Dictionary<string, object>> OnAnswer; // for the server and the tutor

        public Runtime(Transform stage, Board board, Avatar avatar, Camera camera, Action<string, string> log)
        {
            Stage = stage;
            Board = board;
            Avatar = avatar;
            Camera = camera;
            Log = log;
            Timeline = new Timeline((e, t) => Log("error", $"line {t.Cmd?.Line}: {t.Cmd?.Op}: {e.Message}"));
            Timeline.OnBegin = t =>
            {
                if (t.StartsStep) { Step = t.Step; stepAt = Now; }
                OnBegin?.Invoke(t);
            };
        }

        static double Real => Time.realtimeSinceStartupAsDouble;
        /// <summary>The timeline's clock: real seconds, stopped while paused.</summary>
        public double Now => clockAt + ((pausedAt ?? Real) - realAt) * Speed;

        /// <summary>Play faster or slower (0.5–2): animations follow the clock, and speech is
        /// synthesized at that rate. What is queued was voiced at the old rate, so the
        /// current step restarts at the new one.</summary>
        public void SetSpeed(float s)
        {
            s = Mathf.Clamp(s, 0.5f, 2);
            if (Mathf.Approximately(s, Speed)) return;
            double real = pausedAt ?? Real;
            clockAt = Now;
            realAt = real;
            Speed = s;
            if (Avatar != null) Avatar.Voice.Rate = s;
            if (Timeline.Busy && Steps > 0) Seek(Math.Max(1, Step));
        }
        public bool Paused => pausedAt.HasValue;

        public void Pause()
        {
            if (Paused) return;
            pausedAt = Real;
            Avatar?.Voice.Pause();
            if (Avatar != null) Avatar.Paused = true;
        }

        public void Resume()
        {
            if (!Paused) return;
            realAt += Real - pausedAt.Value;
            pausedAt = null;
            Avatar?.Voice.Resume();
            if (Avatar != null) Avatar.Paused = false;
        }

        public void TogglePause() { if (Paused) Resume(); else Pause(); }

        /// <summary>"question" (waiting for an answer), "paused", "waiting" (step mode), "playing" or "idle".</summary>
        public string State =>
            Question != null && !Question.Answered ? "question"
            : Paused ? "paused"
            : Timeline.Waiting != null && !Timeline.Waiting.Question ? "waiting"
            : Timeline.Busy || Question != null ? "playing"
            : "idle";

        /// <summary>Right answers out of those given (skipped ones don't count).</summary>
        public (int right, int total) Score
        {
            get
            {
                int right = 0, total = 0;
                foreach (var r in Results.Values) if (!r.Skipped) { total++; if (r.Correct) right++; }
                return (right, total);
            }
        }

        /// <summary>Skip the rest of this step (an open question counts as skipped) and play the next.</summary>
        public bool Forward()
        {
            Resume();
            if (Timeline.Waiting != null && !Timeline.Waiting.Question) return Timeline.Release(Now);
            if (!Timeline.Busy && Question == null) return false;
            if (Question != null && !Question.Answered) Question.Skip();
            else if (Question != null) CloseQuestion();
            Avatar?.Voice.Stop();
            Avatar?.Silence();
            // Between steps (in a wait, say), just start the next one now.
            var q = Timeline.Queue;
            int cur = Timeline.ActiveCount > 0 || (q.Count > 0 && q[0].Step <= Step) ? Step : (q.Count > 0 ? q[0].Step : 1) - 1;
            Timeline.FinishThrough(cur, Now, t => t.Cmd != null && Quiet.Contains(t.Cmd.Op));
            return true;
        }

        /// <summary>Back to the start of this step if it has played a while, else the one before.</summary>
        public bool Back()
        {
            if (Steps == 0) return false;
            double into = Now - stepAt;
            Seek(into > 3 || Step == 0 ? Math.Max(1, Step) : Step - 1);
            return true;
        }

        /// <summary>Jump to the start of step n: rebuild the board as it was then and play on from there.</summary>
        public void Seek(int n)
        {
            if (Steps == 0) return;
            n = Math.Max(1, Math.Min(Steps, n));
            var hist = new List<(Cmd cmd, int step, bool instant)>(History);
            Reset(keep: true);
            Board?.Apply(Board.Default);
            foreach (var h in hist) if (h.instant || h.step < n) Exec(h.cmd, true, h.step);
            foreach (var h in hist) if (!h.instant && h.step >= n) Exec(h.cmd, false, h.step);
            Step = n;
            stepAt = Now;
        }

        /// <summary>Speak outside the timeline (the teacher's reaction to an answer).</summary>
        public void Speak(string text)
        {
            var spoken = Avatar?.Voice.Prepare(text);
            float dur = spoken.HasValue ? (float)spoken.Value.dur : text.Split(' ').Length * 0.4f / Speed;
            Avatar?.Say(text, dur, spoken?.clip);
        }

        void Ask(Cmd cmd, Task gate)
        {
            CloseQuestion();
            QuestionCard card = null;
            card = new QuestionCard(this, cmd, r =>
            {
                Results[cmd] = r;
                if (!r.Skipped)
                {
                    string reaction = cmd.Op == "recall" ? (r.Correct ? "Good." : "That one is worth another look.") : r.Correct ? "Correct." : "Not quite.";
                    var explain = cmd.Str("explain");
                    Speak(explain != null ? reaction + " " + explain : reaction);
                }
                var a = new Dictionary<string, object>
                {
                    ["id"] = cmd.Id, ["line"] = (double)cmd.Line, ["kind"] = cmd.Op, ["q"] = cmd.Str("q"),
                    ["given"] = r.Given, ["correct"] = r.Correct, ["skipped"] = r.Skipped,
                };
                foreach (var k in new[] { "options", "ids", "answer", "unit" }) if (cmd.Has(k)) a[k] = cmd[k];
                OnAnswer?.Invoke(a);
            }, () =>
            {
                if (Question != card) return;
                CloseQuestion();
                Avatar?.Voice.Stop();
                // Open the gate (it may not be holding yet) and play the next step without another press.
                gate.Gate = false;
                if (Timeline.Waiting == gate) Timeline.Release(Now, true);
                var q = Timeline.Queue;
                if (q.Count > 0 && q[0].Gate && !q[0].Question) q[0].Gate = false;
            });
            Question = card;
        }

        public void CloseQuestion()
        {
            Question?.Dispose();
            Question = null;
        }

        public Entity EntityOf(Transform t) => byObj.TryGetValue(t, out var e) ? e : null;

        public void Exec(Cmd cmd, bool instant = false, int? replayStep = null)
        {
            var op = cmd.Op;
            if (op == "diag") { Log(cmd.Str("level"), $"line {cmd.Line}: {cmd.Str("msg")}"); return; }
            if (op == "replay")
            {
                Reset();
                Board?.Apply(Board.Default);
                foreach (var c in cmd.List("cmds")) Exec(new Cmd((Dictionary<string, object>)c), true);
                return;
            }
            if (op == "ask" || op == "status") { Avatar?.Hear(cmd.Str("text"), op == "status" && cmd.Str("text") == "thinking"); return; }
            if (op == "answer") return; // (ours, or another viewer's: the tutor's business)
            // Remember it with its step, so going back can replay it (unless this is that replay).
            int step;
            if (replayStep.HasValue) step = replayStep.Value;
            else
            {
                if (!instant && StartsStep(cmd)) Steps++;
                step = Steps;
                History.Add((cmd, step, instant));
            }
            Task task, gate = null;
            if (Questions.Contains(op))
            {
                var g = gate = new Task { Gate = true, Question = true, Cmd = cmd, Step = step };
                task = new Task { Begin = () => Ask(cmd, g) };
            }
            else try { task = Handle(cmd); }
            catch (Exception e) { Log("error", $"line {cmd.Line}: {op}: {e.Message}"); return; }
            if (task == null) { Log("warning", $"unity: unsupported op '{op}'"); return; }
            task.Cmd = cmd;
            task.Dur = cmd.Dur;
            task.Par = cmd.Par;
            task.Ease = cmd.Ease;
            task.Step = step;
            task.StartsStep = !instant && StartsStep(cmd);
            if (!instant && !Timeline.Busy) saysInBurst = 0; // a new burst (lesson run, tutor reply)
            if (op == "say" && !instant)
            {
                // Speak it: the say lasts as long as its audio.
                if (!cmd.Spoken.HasValue || cmd.SpokenRate != Speed)
                {
                    cmd.Spoken = Avatar?.Voice.Prepare(cmd.Str("text"));
                    cmd.SpokenRate = Speed;
                }
                if (cmd.Spoken.HasValue)
                {
                    var (clip, dur) = cmd.Spoken.Value;
                    // Lesson time runs Speed times faster than the audio's real seconds.
                    task.Dur = (dur - 0.25) * Speed + 0.25;
                    task.Begin = () => Avatar.Say(cmd.Str("text"), (float)dur, clip);
                }
            }
            if (task.StartsStep && gate == null)
            {
                task.Gate = Stepping && saysInBurst > 0;
                saysInBurst++;
            }
            if (instant)
            {
                if (Quiet.Contains(op)) return;
                Timeline.RunNow(task);
                return;
            }
            Timeline.Add(task, Now);
            // A question holds the lesson until it is answered (or skipped).
            if (gate != null) Timeline.Add(gate, Now);
        }

        public bool Next() => Timeline.Release(Now);
        public bool Waiting => Timeline.Waiting != null && !Timeline.Waiting.Question;

        /// <summary>Drop everything. keep: keep the history (going back rebuilds from it).</summary>
        public void Reset(bool keep = false)
        {
            Timeline.Reset();
            saysInBurst = 0;
            CloseQuestion();
            Avatar?.Voice.Stop();
            Resume();
            foreach (var id in new List<string>(Entities.Keys)) Remove(id);
            Avatar?.Point(null, 0);
            Step = 0;
            if (!keep)
            {
                History.Clear();
                Steps = 0;
                Results.Clear();
            }
        }

        public void Update(float dt)
        {
            Timeline.Tick(Now);
            Question?.Update(dt);
            dt = Paused ? 0 : dt * Speed;
            var camRot = Camera ? Camera.transform.rotation : Quaternion.identity;
            foreach (var e in Entities.Values)
            {
                if (e.Live && e.P > 0) e.Refresh?.Invoke();
                if (e.Spin.HasValue)
                {
                    e.Euler += e.Spin.Value * dt;
                    e.Obj.localRotation = ThreeEuler(e.Euler);
                }
                foreach (var b in e.Billboards) if (b) b.rotation = camRot;
                foreach (var s in e.Strokes) s.UpdateWidth();
            }
        }

        public Entity Get(string id)
        {
            if (id != null && Entities.TryGetValue(id, out var e)) return e;
            throw new Exception($"unknown name '{id}'");
        }

        /// <summary>An entity, or one part of a diagram ('n.L1_3', 'g.r2c3').</summary>
        public Target TargetOf(string id)
        {
            int dot = id?.IndexOf('.') ?? -1;
            if (dot < 0)
            {
                var w = Get(id);
                return new Target { E = w, Tints = new List<Tint>(w.ColorTints()), Objs = new List<Transform> { w.Obj }, Whole = true };
            }
            var e = Get(id.Substring(0, dot));
            var name = id.Substring(dot + 1);
            if (e.Parts == null || !e.Parts.TryGetValue(name, out var part)) throw new Exception($"'{e.Id}' has no part '{name}'");
            return new Target { E = e, Tints = part.Tints, Objs = part.Objs, Apply = part.Apply };
        }

        public void Remove(string id)
        {
            if (id == null || !Entities.TryGetValue(id, out var e)) return;
            foreach (var c in new List<Entity>(Entities.Values)) if (c.ParentEnt == e) Remove(c.Id);
            Entities.Remove(id);
            byObj.Remove(e.Obj);
            e.Dispose();
        }

        void Register(Entity e)
        {
            Entities[e.Id] = e;
            byObj[e.Obj] = e;
        }

        // ── coordinates ──

        public static bool IsRef(object pt) => pt is Dictionary<string, object>;

        static Vector3 Vec(object a, bool flipZ)
        {
            var l = (List<object>)a;
            float z = l.Count > 2 ? (float)(double)l[2] : 0;
            return new Vector3((float)(double)l[0], (float)(double)l[1], flipZ ? -z : z);
        }

        /// <summary>IR board vector → Unity board space (z flipped).</summary>
        public static Vector3 B(object a) => Vec(a, true);

        /// <summary>IR vector as given (axes data coordinates).</summary>
        public static Vector3 Raw(object a) => Vec(a, false);

        /// <summary>three.js Euler (XYZ, radians, right-handed) → Unity rotation of the mirrored board.</summary>
        public static Quaternion ThreeEuler(Vector3 r) =>
            Quaternion.AngleAxis(-r.x * Mathf.Rad2Deg, Vector3.right) *
            Quaternion.AngleAxis(-r.y * Mathf.Rad2Deg, Vector3.up) *
            Quaternion.AngleAxis(r.z * Mathf.Rad2Deg, Vector3.forward);

        // ── geometry queries ──

        /// <summary>Stage-space bounds of an entity's visible parts.</summary>
        public Bounds? StageBounds(Entity e)
        {
            Bounds? b = null;
            foreach (var r in e.Body.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled) continue;
                if (r is LineRenderer lr && lr.positionCount < 2) continue;
                var wb = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = wb.center + Vector3.Scale(wb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = Stage.InverseTransformPoint(c);
                    if (b == null) b = new Bounds(p, Vector3.zero);
                    else { var bb = b.Value; bb.Encapsulate(p); b = bb; }
                }
            }
            return b;
        }

        /// <summary>World position of an entity anchor.</summary>
        public Vector3 AnchorWorld(Entity e, string anchor = "center")
        {
            anchor ??= "center";
            if (e.Pins != null && e.Pins.TryGetValue(anchor, out var pin)) return e.Obj.TransformPoint(pin.at);
            if ((anchor == "start" || anchor == "end") && e.Ends != null)
                return e.Obj.TransformPoint(e.Ends[anchor == "start" ? 0 : 1]);
            if (anchor == "start") anchor = "left";
            if (anchor == "end") anchor = "right";
            if (e.Op == "pt" && anchor == "center") return e.Obj.position;
            var b = StageBounds(e);
            if (b == null) return e.Obj.position;
            Vector3 mn = b.Value.min, mx = b.Value.max, c = b.Value.center;
            Vector3 p;
            switch (anchor)
            {
                case "top": p = new Vector3(c.x, mx.y, c.z); break;
                case "bottom": p = new Vector3(c.x, mn.y, c.z); break;
                case "left": p = new Vector3(mn.x, c.y, c.z); break;
                case "right": p = new Vector3(mx.x, c.y, c.z); break;
                case "front": p = new Vector3(c.x, c.y, mn.z); break; // toward the learner
                case "back": p = new Vector3(c.x, c.y, mx.z); break;
                default: p = c; break;
            }
            return Stage.TransformPoint(p);
        }

        /// <summary>World position of a {ref, anchor, off} point. Offsets are in board units.</summary>
        public Vector3 RefWorld(Dictionary<string, object> r)
        {
            var e = Get(r["ref"] as string);
            var w = AnchorWorld(e, r.TryGetValue("anchor", out var a) ? a as string : null);
            var s = Stage.InverseTransformPoint(w) + (r.TryGetValue("off", out var off) && off != null ? B(off) : Vector3.zero);
            return Stage.TransformPoint(s);
        }

        /// <summary>Outward direction of a pin reference, in stage space (null if not a pin).</summary>
        public Vector3? PinDir(Dictionary<string, object> r)
        {
            if (!(r["ref"] is string id) || !Entities.TryGetValue(id, out var e) || e.Pins == null) return null;
            if (!(r.TryGetValue("anchor", out var a) && a is string an && e.Pins.TryGetValue(an, out var pin))) return null;
            var p0 = Stage.InverseTransformPoint(e.Obj.TransformPoint(pin.at));
            var p1 = Stage.InverseTransformPoint(e.Obj.TransformPoint(pin.at + pin.dir));
            return (p1 - p0).normalized;
        }

        public Vector3 PointWorld(object pt) => IsRef(pt) ? RefWorld((Dictionary<string, object>)pt) : Stage.TransformPoint(B(pt));

        /// <summary>Point on an entity's border in direction `dir` (stage space).</summary>
        public Vector3 BorderPoint(Entity e, Vector3 dir)
        {
            var c = Stage.InverseTransformPoint(AnchorWorld(e, "center"));
            var d = dir.normalized;
            float k = e.Obj.lossyScale.x / Stage.lossyScale.x;
            float hw, hh;
            bool circ;
            if (e.Shape.HasValue)
            {
                hw = e.Shape.Value.hw * k;
                hh = e.Shape.Value.hh * k;
                circ = e.Shape.Value.kind == "circle";
            }
            else
            {
                var b = StageBounds(e);
                var size = b?.size ?? Vector3.zero;
                hw = size.x / 2;
                hh = size.y / 2;
                circ = e.Op == "pt" || e.Op == "circle" || e.Op == "sphere";
            }
            if (circ) return c + d * Mathf.Max(hw, hh);
            float tx = Mathf.Abs(d.x) > 1e-6f ? hw / Mathf.Abs(d.x) : float.PositiveInfinity;
            float ty = Mathf.Abs(d.y) > 1e-6f ? hh / Mathf.Abs(d.y) : float.PositiveInfinity;
            float t = Mathf.Min(tx, ty);
            return c + d * (float.IsInfinity(t) ? 0 : t);
        }

        // ── creation ──

        Task Create(Cmd cmd)
        {
            Entity e = null;
            return new Task
            {
                Begin = () =>
                {
                    Remove(cmd.Id);
                    Entity parent = cmd.Has("on") ? Get(cmd.Str("on")) : null;
                    void Make()
                    {
                        e = new Entity(this, cmd, parent);
                        try { Build(e, cmd); }
                        catch { e.Dispose(); throw; }
                        Register(e);
                    }
                    // On a figure from code whose axes aren't known yet (still rendering): when they are.
                    if (parent != null && parent.Op == "fig" && parent.AxesSelf == null)
                    {
                        (parent.Waiting ??= new List<Action>()).Add(() =>
                        {
                            if (!Entities.TryGetValue(cmd.Str("on"), out var now) || now != parent) return;
                            try { Make(); e.Enter(1, 1); }
                            catch (Exception ex) { Log("error", $"line {cmd.Line}: {cmd.Op}: {ex.Message}"); }
                        });
                        return;
                    }
                    Make();
                    e.Enter(0, 0);
                },
                Update = (eased, raw) => e?.Enter(eased, raw),
                Finish = () => e?.Enter(1, 1),
            };
        }

        /// <summary>Tween helper for change ops.</summary>
        Task Change(Cmd cmd, Func<Entity, Action<float, float>> setup)
        {
            Action<float, float> u = null;
            return new Task
            {
                Begin = () => u = setup(Get(cmd.Str("target"))),
                Update = (p, raw) => u?.Invoke(p, raw),
            };
        }

        // ── handlers ──

        static readonly HashSet<string> CreateOps = new HashSet<string>
        {
            "pt", "line", "arrow", "path", "poly", "rect", "circle", "angle", "text", "tex", "node", "edge", "img",
            "axes", "plot", "surf", "box", "sphere", "cyl", "model", "svg", "fig", "part", "wire", "block", "frame", "code", "table", "wave",
            "nn", "grid", "bits", "bars", "hist", "scatter", "errbar", "band", "contour",
        };

        Task Handle(Cmd c)
        {
            if (CreateOps.Contains(c.Op)) return Create(c);
            switch (c.Op)
            {
                case "wait": return new Task();
                case "say": return new Task { Begin = () => Avatar?.Say(c.Str("text"), (float)c.Dur / Speed) };
                case "point":
                    return new Task
                    {
                        Begin = () =>
                        {
                            Vector3? at = c.Has("at") ? PointWorld(c["at"]) : (Vector3?)null;
                            // In axes' or a figure's data coordinates.
                            if (c.Has("on") && c.Has("at") && !IsRef(c["at"]) && Entities.TryGetValue(c.Str("on"), out var on))
                                at = on.Obj.TransformPoint(on.AxesSelf != null ? on.AxesSelf.Map(Raw(c["at"])) : Vector3.zero);
                            Avatar?.Point(at, (float)c.Dur);
                        },
                    };
                case "title": return Create(c.With("id", "__title").With("op", "title"));
                case "board": return BoardTask(c);
                case "clear": return FadeOutAndRemove(() => { Avatar?.Point(null, 0); return new List<Entity>(Entities.Values); });
                case "del":
                    return FadeOutAndRemove(() =>
                    {
                        var l = new List<Entity>();
                        foreach (var id in c.List("ids")) l.Add(Get(id as string));
                        return l;
                    });
                case "group": return GroupTask(c);
                case "move": return Change(c, e => MoveSetup(e, c["to"]));
                case "rot": return Change(c, e => RotSetup(e, Raw(c["rot"])));
                case "scale": return Change(c, e => ScaleSetup(e, c.Flt("scale", 1)));
                case "fade":
                    if (c.Str("target").Contains("."))
                    {
                        // One part: its own dimming factor.
                        Target t = null;
                        float[] from = null;
                        float to = c.Flt("opacity", 1);
                        return new Task
                        {
                            Begin = () => { t = TargetOf(c.Str("target")); from = t.Tints.ConvertAll(x => x.Dim).ToArray(); },
                            Update = (p, raw) =>
                            {
                                for (int i = 0; i < t.Tints.Count; i++) t.Tints[i].Dim = from[i] + (to - from[i]) * p;
                                t.E.ApplyOpacity();
                                t.Apply?.Invoke();
                            },
                        };
                    }
                    return Change(c, e => FadeSetup(e, c.Flt("opacity", 1)));
                case "color":
                    {
                        Action<float, float> u = null;
                        return new Task { Begin = () => u = ColorSetup(TargetOf(c.Str("target")), Draw.Hex(c.Str("color"))), Update = (p, raw) => u?.Invoke(p, raw) };
                    }
                case "set":
                    return Change(c, e =>
                    {
                        var parts = new List<Action<float, float>>();
                        if (c.Has("text")) e.SetText?.Invoke(c.Str("text"));
                        if (c.Has("pos")) parts.Add(MoveSetup(e, c["pos"]));
                        if (c.Has("rot")) parts.Add(RotSetup(e, Raw(c["rot"])));
                        if (c.Has("scale")) parts.Add(ScaleSetup(e, c.Flt("scale", 1)));
                        if (c.Has("opacity")) parts.Add(FadeSetup(e, c.Flt("opacity", 1)));
                        if (c.Has("color")) parts.Add(ColorSetup(TargetOf(e.Id), Draw.Hex(c.Str("color"))));
                        if (c.Has("focus")) e.SetFocus?.Invoke(c.Range("focus"));
                        return (p, raw) => { foreach (var u in parts) u(p, raw); };
                    });
                case "hl": return HlTask(c);
                case "spin":
                    return Change(c, e =>
                    {
                        // Degrees/s about the board's axes, three.js signs (ThreeEuler mirrors them).
                        var r = Raw(c["rate"]) * Mathf.Deg2Rad;
                        e.Spin = r.sqrMagnitude > 0 ? r : (Vector3?)null;
                        return (p, raw) => { };
                    });
            }
            return null;
        }

        Task BoardTask(Cmd c)
        {
            if (Board == null) return new Task();
            var size = c.Has("size") ? Raw(c["size"]) : Board.Default;
            var t = Board.Tween(size);
            void Title()
            {
                if (Entities.TryGetValue("__title", out var e))
                {
                    var p = e.Obj.localPosition;
                    p.y = Board.Height / 2 - 0.65f;
                    e.Obj.localPosition = p;
                }
            }
            return new Task
            {
                Begin = t.Begin,
                Update = (p, raw) => { t.Update(p, raw); Title(); },
                Finish = () => { t.Finish(); Title(); },
            };
        }

        Task FadeOutAndRemove(Func<List<Entity>> pick)
        {
            List<Entity> list = null;
            float[] o0 = null;
            return new Task
            {
                Begin = () =>
                {
                    list = pick();
                    o0 = list.ConvertAll(e => e.Opacity).ToArray();
                },
                Update = (p, raw) => { for (int i = 0; i < list.Count; i++) if (list[i].Obj) list[i].SetOpacity(o0[i] * (1 - p)); },
                Finish = () => { foreach (var e in list) if (Entities.TryGetValue(e.Id, out var cur) && cur == e) Remove(e.Id); },
            };
        }

        Task GroupTask(Cmd c)
        {
            return new Task
            {
                Begin = () =>
                {
                    var members = new List<Entity>();
                    foreach (var id in c.List("ids")) members.Add(Get(id as string));
                    Remove(c.Id);
                    var g = new Entity(this, new Cmd(new Dictionary<string, object> { ["id"] = c.Id, ["op"] = "group" }), null);
                    Bounds? box = null;
                    foreach (var m in members)
                        foreach (var r in m.Obj.GetComponentsInChildren<Renderer>())
                        {
                            if (box == null) box = r.bounds;
                            else { var b = box.Value; b.Encapsulate(r.bounds); box = b; }
                        }
                    if (box != null) g.Obj.localPosition = Stage.InverseTransformPoint(box.Value.center);
                    foreach (var m in members)
                    {
                        m.Obj.SetParent(g.Obj, true);
                        m.ParentEnt = g;
                    }
                    g.P = 1;
                    Register(g);
                },
            };
        }

        Action<float, float> MoveSetup(Entity e, object to)
        {
            var dest = e.ToParent(to);
            var center = e.ParentObj.InverseTransformPoint(AnchorWorld(e, "center"));
            var from = e.Obj.localPosition;
            var target = from + (dest - center);
            return (p, raw) => e.Obj.localPosition = Vector3.Lerp(from, target, p);
        }

        /// <summary>`rot`: degrees about the board's axes, applied like three.js Euler XYZ.</summary>
        Action<float, float> RotSetup(Entity e, Vector3 rotDeg)
        {
            e.Spin = null;
            var r = rotDeg * Mathf.Deg2Rad;
            var from = e.Obj.localRotation;
            var to = ThreeEuler(r);
            return (p, raw) =>
            {
                e.Obj.localRotation = Quaternion.Slerp(from, to, p);
                if (p >= 1) e.Euler = r;
            };
        }

        Action<float, float> ScaleSetup(Entity e, float s)
        {
            float from = e.Obj.localScale.x;
            return (p, raw) => e.Obj.localScale = Vector3.one * Mathf.Max(1e-3f, from + (s - from) * p);
        }

        Action<float, float> FadeSetup(Entity e, float o)
        {
            float from = e.Opacity;
            return (p, raw) => e.SetOpacity(from + (o - from) * p);
        }

        Action<float, float> ColorSetup(Target t, Color to)
        {
            var tints = t.Tints;
            var from = tints.ConvertAll(x => x.Color);
            return (p, raw) =>
            {
                for (int i = 0; i < tints.Count; i++)
                {
                    var c = Color.Lerp(from[i], to, p);
                    c.a = from[i].a;
                    tints[i].Color = c;
                }
                t.E.ApplyOpacity();
                t.Apply?.Invoke();
            };
        }

        Task HlTask(Cmd c)
        {
            var us = new List<Action<float>>();
            return new Task
            {
                Begin = () =>
                {
                    var hl = Draw.Hex(c.Str("color") ?? "#ffe066");
                    foreach (var id in c.List("ids"))
                    {
                        var t = TargetOf(id as string);
                        var tints = t.Tints;
                        var from = tints.ConvertAll(x => x.Color);
                        var s0 = t.Objs.ConvertAll(o => o.localScale);
                        float grow = t.Whole ? 0.12f : 0.3f;
                        us.Add(raw =>
                        {
                            float k = Mathf.Sin(Mathf.PI * raw);
                            for (int i = 0; i < tints.Count; i++)
                            {
                                var col = Color.Lerp(from[i], hl, k * 0.85f);
                                col.a = from[i].a;
                                tints[i].Color = col;
                            }
                            t.E.ApplyOpacity();
                            for (int i = 0; i < t.Objs.Count; i++) if (t.Objs[i]) t.Objs[i].localScale = s0[i] * (1 + grow * k);
                            t.Apply?.Invoke();
                        });
                    }
                },
                Update = (p, raw) => { foreach (var u in us) u(raw); },
            };
        }
    }
}
