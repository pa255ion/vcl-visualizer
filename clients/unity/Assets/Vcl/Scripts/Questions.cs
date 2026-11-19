// Questions for the learner (docs/SPEC.md §4.10): quiz, pick, estimate, recall.
// A port of viewer/js/questions.js: a card floating in front of the board (a
// child of the stage, in board units). Everything pressable is a Hit, a
// rectangle in its own transform's plane, which the app tests the controller
// rays (or the mouse) against. After the answer the card shows what was right
// and `explain=`, and Continue lets the lesson go on.
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Vcl
{
    public class QuestionResult
    {
        public object Given;   // List<object> of 1-based numbers (quiz, pick), a double (estimate), a bool (recall); null if skipped
        public bool Correct, Skipped;
    }

    /// <summary>Something the learner can press: a rectangle in T's local XY plane.</summary>
    public class Hit
    {
        public Transform T;
        public Rect R;
        public Action Press;
        public Action<Vector3> PressAt; // where it was pressed, in T's local space
        public Action<bool> Hover;
        public Action<Vector3> Drag; // a point in T's local space
        public int Order;            // higher wins when hits overlap

        /// <summary>Where a world ray meets T's plane inside R (local point), or null.</summary>
        public Vector3? Test(Ray ray, out float dist)
        {
            dist = 0;
            if (!T) return null;
            var p = PlanePoint(ray, out dist);
            return p.HasValue && R.Contains(new Vector2(p.Value.x, p.Value.y)) ? p : null;
        }

        public Vector3? PlanePoint(Ray ray, out float dist)
        {
            dist = 0;
            var o = T.InverseTransformPoint(ray.origin);
            var d = T.InverseTransformDirection(ray.direction);
            if (Mathf.Abs(d.z) < 1e-6f) return null;
            float t = -o.z / d.z;
            if (t <= 0) return null;
            var at = o + d * t;
            dist = Vector3.Distance(ray.origin, T.TransformPoint(at));
            return at;
        }
    }

    public class QuestionCard
    {
        static readonly Color Card = Draw.Hex("#101824"), Edge = Draw.Hex("#3b4a5e"), Muted = Draw.Hex("#8796ab"),
            ButtonC = Draw.Hex("#1c2a3d"), HoverC = Draw.Hex("#2f6db5"), Chosen = Draw.Hex("#2c6b80"),
            Ok = Draw.Hex("#2e7d32"), OkFg = Draw.Hex("#66bb6a"), Err = Draw.Hex("#8e2b2b"), ErrFg = Draw.Hex("#ef5350");
        const float W = 9, Pad = 0.45f, Z = 1.5f, TextH = 0.36f;
        const int Order = 40;

        readonly Runtime rt;
        public readonly Cmd Cmd;
        readonly Action<QuestionResult> done;
        readonly Action onContinue;
        readonly bool multi;
        readonly HashSet<int> chosen = new HashSet<int>();
        double value;
        readonly double step;
        bool revealed;
        public QuestionResult Result { get; private set; }
        public bool Answered => Result != null;
        public readonly List<Hit> Hits = new List<Hit>();
        readonly Transform root, badges;
        Transform body;
        float height;

        public QuestionCard(Runtime rt, Cmd cmd, Action<QuestionResult> done, Action onContinue)
        {
            this.rt = rt;
            Cmd = cmd;
            this.done = done;
            this.onContinue = onContinue;
            multi = (cmd.Op == "quiz" || cmd.Op == "pick") && Answer.Count > 1;
            if (cmd.Op == "estimate")
            {
                var r = cmd.Range("range");
                step = cmd.Num("step", NiceStep(r[0], r[1]));
                value = Snap((r[0] + r[1]) / 2);
            }
            root = Draw.Child(rt.Stage, "question").transform;
            if (cmd.Op == "pick") badges = Draw.Child(rt.Stage, "question badges").transform;
            Render();
        }

        List<int> Answer => (Cmd.List("answer") ?? new List<object>()).Select(o => (int)(double)o).ToList();
        List<string> Strings(string k) => (Cmd.List(k) ?? new List<object>()).Select(o => o as string).ToList();

        public static double NiceStep(double lo, double hi)
        {
            double raw = (hi - lo) / 100, k = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            foreach (var m in new[] { 1, 2, 5, 10 }) if (m * k >= raw) return m * k;
            return 10 * k;
        }

        double Snap(double v)
        {
            var r = Cmd.Range("range");
            return Math.Min(r[1], Math.Max(r[0], r[0] + Math.Round((v - r[0]) / step) * step));
        }

        string Format(double v)
        {
            int dec = Math.Max(0, -(int)Math.Floor(Math.Log10(step) + 1e-9));
            var s = v.ToString("F" + dec, System.Globalization.CultureInfo.InvariantCulture);
            var unit = Cmd.Str("unit");
            return unit != null ? s + " " + unit : s;
        }

        public static string Wrap(string text, int n)
        {
            var outLines = new List<string>();
            foreach (var para in (text ?? "").Split('\n'))
            {
                var cur = "";
                foreach (var w in para.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (cur.Length > 0 && (cur + " " + w).Length > n) { outLines.Add(cur); cur = w; }
                    else cur = cur.Length > 0 ? cur + " " + w : w;
                }
                outLines.Add(cur);
            }
            return string.Join("\n", outLines);
        }

        static int CharsFor(float width, float size) => Math.Max(8, (int)(width / (size * 0.5f)));

        /// <summary>Is this answer right?</summary>
        public static bool Grade(Cmd c, object given)
        {
            if (c.Op == "estimate")
            {
                var r = c.Range("range");
                double tol = c.Num("tol", 0.05 * (r[1] - r[0]));
                return Math.Abs((double)given - c.Num("answer", 0)) <= tol + 1e-9;
            }
            if (c.Op == "recall") return given is bool b && b;
            var want = (c.List("answer") ?? new List<object>()).Select(o => (int)(double)o).OrderBy(x => x).ToList();
            var got = ((List<object>)given).Select(o => (int)(double)o).OrderBy(x => x).ToList();
            return want.SequenceEqual(got);
        }

        // ── layout ──

        float y;
        const float X0 = -W / 2 + Pad, Inner = W - 2 * Pad;

        TextMeshPro Text(string s, float size, Color color, float x = X0, float width = Inner, bool bold = true)
        {
            var t = Draw.Text(body, Wrap(s, CharsFor(width, size)), size, color, "left", bold);
            var sz = Draw.TextSize(t);
            t.transform.localPosition = new Vector3(x, y - sz.y / 2, -0.02f);
            t.renderer.sortingOrder = Order + 3;
            y -= sz.y;
            return t;
        }

        void Render()
        {
            foreach (var h in Hits) h.Hover?.Invoke(false);
            Hits.Clear();
            if (body) Draw.Kill(body.gameObject);
            foreach (var old in root.Cast<Transform>().ToList()) Draw.Kill(old.gameObject);
            body = Draw.Child(root, "body").transform;
            y = -Pad;
            var c = Cmd;
            var res = Result;
            bool pick = c.Op == "pick";
            string kind = c.Op == "quiz" ? (multi ? "Choose every right answer" : "Question")
                : pick ? (multi ? "Point at every right one on the board" : "Point at it on the board")
                : c.Op == "estimate" ? "Estimate" : "Recall";
            Text(kind.ToUpperInvariant(), 0.24f, Muted);
            y -= 0.12f;
            Text(c.Str("q"), 0.44f, Draw.FG, X0, pick && res == null ? Inner - 2.2f : Inner);
            y -= pick ? 0.15f : 0.3f;

            var answer = Answer;
            if (c.Op == "quiz")
            {
                var opts = Strings("options");
                for (int i = 0; i < opts.Count; i++)
                {
                    int n = i + 1;
                    bool right = answer.Contains(n), picked = res != null ? Given(res).Contains(n) : chosen.Contains(n);
                    string mark = res != null ? (right ? "✓" : picked ? "✗" : " ") : multi ? (picked ? "☑" : "☐") : ((char)('A' + i)).ToString();
                    var label = Wrap($"{mark}   {opts[i]}", CharsFor(Inner - 0.5f, TextH));
                    int lines = label.Split('\n').Length;
                    float h = lines * TextH * 1.25f + 0.3f;
                    var bg = res != null ? (right ? Ok : picked ? Err : ButtonC) : picked ? Chosen : ButtonC;
                    Button(X0, y, Inner, h, label, res != null ? (Action)null : () => Choose(n), bg, "left");
                    y -= h + 0.15f;
                }
            }
            else if (pick) RenderBadges();
            else if (c.Op == "estimate")
            {
                y -= 0.5f;
                Slider(X0 + 0.3f, y, Inner - 0.6f);
                y -= 1.0f;
            }
            else if (c.Op == "recall")
            {
                if (!revealed)
                {
                    Text("Think of the answer, then reveal it.", 0.3f, Muted, bold: false);
                    y -= 0.2f;
                    Button(X0, y, 3.4f, 0.75f, "Show answer", () => { revealed = true; Render(); });
                    y -= 0.9f;
                }
                else
                {
                    Text(c.Str("answer"), 0.4f, Draw.Hex("#ffe066"), X0 + 0.1f);
                    y -= 0.3f;
                }
            }

            y -= 0.1f;
            if (res != null)
            {
                string head = res.Skipped ? "Skipped" : res.Correct ? (c.Op == "recall" ? "✓ Good" : "✓ Correct") : (c.Op == "recall" ? "Worth another look" : "✗ Not quite");
                Text(head, 0.42f, res.Skipped ? Muted : res.Correct ? OkFg : ErrFg);
                if (c.Op == "estimate" && !res.Skipped) Text($"You said {Format((double)res.Given)}; the answer is {Format(c.Num("answer", 0))}.", 0.32f, Draw.FG, bold: false);
                var explain = c.Str("explain");
                if (explain != null) { y -= 0.1f; Text(explain, 0.34f, Draw.FG, bold: false); }
                y -= 0.25f;
                Button(W / 2 - Pad - 3, y, 3, 0.75f, "Continue ▸", () => onContinue(), Chosen);
                y -= 0.75f;
            }
            else
            {
                bool canCheck = multi || c.Op == "estimate";
                if (pick && !canCheck)
                {
                    // Short card: Skip sits beside the question.
                    Button(W / 2 - Pad - 1.9f, -Pad - 0.4f, 1.9f, 0.7f, "Skip", Skip, ButtonC, "center", 0.3f);
                }
                else
                {
                    if (c.Op == "recall" && revealed)
                    {
                        Button(X0, y, 3.4f, 0.75f, "I knew it", () => Finish(true), Ok);
                        Button(X0 + 3.6f, y, 3.4f, 0.75f, "Not yet", () => Finish(false), Err);
                    }
                    else if (canCheck)
                    {
                        bool ready = c.Op == "estimate" || chosen.Count > 0;
                        Button(X0, y, 2.6f, 0.75f, "Check", ready ? () => Finish(c.Op == "estimate" ? (object)value : chosen.Select(n => (object)(double)n).ToList()) : (Action)null, ready ? Chosen : ButtonC);
                    }
                    Button(W / 2 - Pad - 1.9f, y, 1.9f, 0.75f, "Skip", Skip, ButtonC, "center", 0.3f);
                    y -= 0.75f;
                }
            }
            y -= Pad;

            height = -y;
            var bgMat = Draw.Unlit(Card, 3010);
            var bgObj = Draw.MeshObject(root, "card", Draw.Fill(Draw.RoundedRect(W, height, 0.3f)), bgMat, Order);
            bgObj.transform.localPosition = new Vector3(0, -height / 2, 0);
            var edge = new Stroke(root, Edge, 0.05f) { Order = Order + 1 };
            edge.Set(new List<List<Vector3>> { Draw.RoundedRect(W, height, 0.3f).Select(p => p + new Vector3(0, -height / 2, -0.005f)).ToList() });
            // A drag handle in the corner; the whole card (not its buttons) moves it.
            var grip = Draw.Text(root, "✥ drag to move", 0.22f, Muted, "right", false);
            grip.transform.localPosition = new Vector3(W / 2 - Pad, -Pad - 0.11f, -0.02f);
            grip.renderer.sortingOrder = Order + 3;
            var bgT = bgObj.transform;
            Vector3? grab = null;
            Vector3 OnStage(Vector3 local) => rt.Stage.InverseTransformPoint(bgT.TransformPoint(local));
            Hits.Add(new Hit
            {
                T = bgT, R = new Rect(-W / 2, -height / 2, W, height), Order = -1,
                PressAt = local => grab = OnStage(local) - root.localPosition,
                Drag = local =>
                {
                    if (!grab.HasValue) return;
                    var p = OnStage(local) - grab.Value;
                    float bw = rt.Board != null ? rt.Board.Width : 16, bh = rt.Board != null ? rt.Board.Height : 9;
                    p.x = Mathf.Clamp(p.x, -bw / 2 - 6, bw / 2 + 6);
                    p.y = Mathf.Clamp(p.y, -bh / 2 - 3, bh / 2 + 6);
                    root.localPosition = new Vector3(p.x, p.y, root.localPosition.z);
                    var home = Home();
                    PlayerPrefs.SetString(Slot, FormattableString.Invariant($"{p.x - home.x},{p.y - home.y}"));
                },
            });
            Place();
        }

        string Slot => Cmd.Op == "pick" ? "vcl.questionOffset.pick" : "vcl.questionOffset.card";

        /// <summary>The card's usual top-centre, before any dragging.</summary>
        Vector2 Home()
        {
            float bh = rt.Board != null ? rt.Board.Height : 9;
            return new Vector2(0, Cmd.Op == "pick" ? bh / 2 - 0.1f : Mathf.Min(bh / 2 - 0.3f, height - bh / 2 + 0.9f));
        }

        static List<int> Given(QuestionResult r) => r.Given is List<object> l ? l.Select(o => (int)(double)o).ToList() : new List<int>();

        /// <summary>Over the lower board, clear of the subtitles (pick: a short card over the title).</summary>
        void Place()
        {
            // Where the learner last dragged this kind of card, from its usual place (remembered).
            var off = Vector2.zero;
            var saved = PlayerPrefs.GetString(Slot, "").Split(',');
            if (saved.Length == 2 && float.TryParse(saved[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ox)
                && float.TryParse(saved[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var oy)) off = new Vector2(ox, oy);
            var home = Home();
            // VCL's +z (toward the learner) is Unity's -z.
            root.localPosition = new Vector3(home.x + off.x, home.y + off.y, -Z);
            root.localRotation = Quaternion.Euler(Cmd.Op == "pick" ? -3.5f : 3.5f, 0, 0);
        }

        void Button(float x, float yTop, float w, float h, string label, Action onPress, Color? bg = null, string align = "center", float size = TextH)
        {
            var color = bg ?? ButtonC;
            var mat = Draw.Unlit(color, 3011);
            var m = Draw.MeshObject(body, "button", Draw.Fill(Draw.RoundedRect(w, h, 0.15f)), mat, Order + 2).transform;
            m.localPosition = new Vector3(x + w / 2, yTop - h / 2, -0.01f);
            var t = Draw.Text(body, label, size, onPress != null || color != ButtonC ? Draw.FG : Muted, align == "left" ? "left" : "center", true);
            t.transform.localPosition = new Vector3(align == "left" ? x + 0.25f : x + w / 2, yTop - h / 2, -0.03f);
            t.renderer.sortingOrder = Order + 4;
            if (onPress == null) return;
            Hits.Add(new Hit
            {
                T = m, R = new Rect(-w / 2, -h / 2, w, h), Press = onPress, Order = 1,
                Hover = on => { if (mat) mat.color = Color.Lerp(color, HoverC, on ? 0.6f : 0); },
            });
        }

        void Slider(float x, float yy, float w)
        {
            var r = Cmd.Range("range");
            double lo = r[0], hi = r[1];
            var res = Result;
            float At(double v) => x + (float)((v - lo) / (hi - lo)) * w;
            var track = Draw.MeshObject(body, "track", Draw.Fill(Draw.RoundedRect(w, 0.12f, 0.06f)), Draw.Unlit(Edge, 3011), Order + 2).transform;
            track.localPosition = new Vector3(x + w / 2, yy, -0.01f);
            TextMeshPro Label(string s, float px, float py, float size, Color color)
            {
                var t = Draw.Text(body, s, size, color, "center", false);
                t.transform.localPosition = new Vector3(px, py, -0.03f);
                t.renderer.sortingOrder = Order + 4;
                return t;
            }
            Label(Format(lo), x, yy - 0.4f, 0.28f, Muted);
            Label(Format(hi), x + w, yy - 0.4f, 0.28f, Muted);
            if (res != null && !res.Skipped)
            {
                var right = Draw.MeshObject(body, "right", Draw.Fill(Draw.RoundedRect(0.08f, 0.6f, 0.02f)), Draw.Unlit(OkFg, 3012), Order + 3).transform;
                right.localPosition = new Vector3(At(Cmd.Num("answer", 0)), yy, -0.02f);
            }
            var knob = Draw.MeshObject(body, "knob", Draw.Disc(0.22f), Draw.Unlit(res != null ? (res.Correct ? OkFg : ErrFg) : Draw.Hex("#7fe3ff"), 3013), Order + 4).transform;
            knob.localPosition = new Vector3(At(value), yy, -0.03f);
            var val = Label(Format(value), At(value), yy + 0.45f, 0.34f, Draw.FG);
            if (res != null) return;
            // A tall strip around the track takes presses and drags.
            var grab = Draw.Child(body, "grab").transform;
            grab.localPosition = new Vector3(x + w / 2, yy, -0.01f);
            void Set(Vector3 local)
            {
                value = Snap(lo + (local.x + w / 2) / w * (hi - lo));
                knob.localPosition = new Vector3(At(value), yy, -0.03f);
                val.text = Format(value);
                val.transform.localPosition = new Vector3(At(value), yy + 0.45f, -0.03f);
            }
            Hits.Add(new Hit
            {
                T = grab, R = new Rect(-w / 2 - 0.3f, -0.5f, w + 0.6f, 1.0f), Press = () => { }, Drag = Set, Order = 1,
                Hover = on => { if (knob) knob.localScale = Vector3.one * (on ? 1.25f : 1); },
            });
        }

        /// <summary>Numbered markers on the things to pick from.</summary>
        void RenderBadges()
        {
            foreach (var c in badges.Cast<Transform>().ToList()) Draw.Kill(c.gameObject);
            var res = Result;
            var ids = Strings("ids");
            var answer = Answer;
            for (int i = 0; i < ids.Count; i++)
            {
                int n = i + 1;
                var pos = TargetPos(ids[i]);
                if (!pos.HasValue) continue;
                bool right = answer.Contains(n), picked = res != null ? Given(res).Contains(n) : chosen.Contains(n);
                var color = res != null ? (right ? Ok : picked ? Err : Draw.Hex("#33415a")) : picked ? Chosen : Draw.Hex("#1f5f99");
                var b = Draw.Child(badges, "badge").transform;
                b.localPosition = pos.Value;
                var mat = Draw.Unlit(color, 3012);
                Draw.MeshObject(b, "disc", Draw.Disc(0.34f), mat, Order - 2);
                var ring = Draw.MeshObject(b, "ring", Draw.Ring(0.34f, 0.4f), Draw.Unlit(Draw.FG, 3012), Order - 2).transform;
                var t = Draw.Text(b, res != null ? (right ? "✓" : picked ? "✗" : n.ToString()) : n.ToString(), 0.4f, Color.white, "center", true);
                t.transform.localPosition = new Vector3(0, 0, -0.01f);
                t.renderer.sortingOrder = Order - 1;
                if (res != null) continue;
                Hits.Add(new Hit
                {
                    T = b, R = new Rect(-0.4f, -0.4f, 0.8f, 0.8f), Press = () => Choose(n), Order = 2,
                    Hover = on => { if (mat) mat.color = Color.Lerp(color, HoverC, on ? 0.6f : 0); if (ring) ring.localScale = Vector3.one * (on ? 1.15f : 1); },
                });
            }
        }

        /// <summary>A pick target's position in stage space, a little in front of it.</summary>
        Vector3? TargetPos(string id)
        {
            int dot = id.IndexOf('.');
            string name = dot < 0 ? id : id.Substring(0, dot), part = dot < 0 ? "center" : id.Substring(dot + 1);
            try
            {
                var e = rt.Get(name);
                Vector3 world;
                if (dot < 0 || (e.Pins != null && e.Pins.ContainsKey(part))) world = rt.AnchorWorld(e, part);
                else
                {
                    var t = rt.TargetOf(id);
                    var rends = t.Objs.Where(o => o).SelectMany(o => o.GetComponentsInChildren<Renderer>()).ToList();
                    if (rends.Count == 0) world = rt.AnchorWorld(e, "center");
                    else
                    {
                        var bounds = rends[0].bounds;
                        foreach (var r in rends) bounds.Encapsulate(r.bounds);
                        world = bounds.center;
                    }
                }
                return rt.Stage.InverseTransformPoint(world) + new Vector3(0, 0, -0.3f);
            }
            catch (Exception ex)
            {
                rt.Log("warning", $"pick: {ex.Message}");
                return null;
            }
        }

        void Choose(int n)
        {
            if (Result != null) return;
            if (!multi) { Finish(new List<object> { (double)n }); return; }
            if (!chosen.Remove(n)) chosen.Add(n);
            Render();
        }

        /// <summary>Answer as if the learner had (tests).</summary>
        public void Submit(object given) { if (Cmd.Op == "recall") revealed = true; Finish(given); }

        void Finish(object given)
        {
            if (Result != null) return;
            if (given is double v) value = v;
            Result = new QuestionResult { Given = given, Correct = Grade(Cmd, given) };
            Render();
            done(Result);
        }

        public void Skip()
        {
            if (Result != null) { onContinue(); return; }
            Result = new QuestionResult { Skipped = true };
            done(Result);
            onContinue();
        }

        public void Update(float dt) { }

        public void Dispose()
        {
            foreach (var h in Hits) h.Hover?.Invoke(false);
            Hits.Clear();
            if (root) Draw.Kill(root.gameObject);
            if (badges) Draw.Kill(badges.gameObject);
        }
    }
}
