// The transcript: a panel beside the board listing the lesson's steps (what
// the teacher says, and the questions with how they went), the current one
// highlighted, with the playback buttons on top: back a step, pause/resume,
// forward a step. Pointing at a step and pressing the trigger jumps there.
// The headset's counterpart to the browser editor's "line being played".
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Vcl
{
    public class Transcript
    {
        const float W = 7, Pad = 0.35f, Gap = 1.0f, Size = 0.3f;
        const int Order = 30;
        static readonly Color Bg = Draw.Hex("#0d1420"), EdgeC = Draw.Hex("#2a3647"), Muted = Draw.Hex("#8796ab"),
            Dim = Draw.Hex("#56637a"), Accent = Draw.Hex("#7fe3ff"), ButtonC = Draw.Hex("#1c2a3d"), HoverC = Draw.Hex("#2f6db5");

        readonly Runtime rt;
        readonly Transform root;
        public readonly List<Hit> Hits = new List<Hit>();
        public bool Visible
        {
            get => root.gameObject.activeSelf;
            set { root.gameObject.SetActive(value); PlayerPrefs.SetInt("vcl.transcript", value ? 1 : 0); }
        }
        /// <summary>The status line ("playing", "finished · 3 of 4 right"…), set by the app.</summary>
        public string Status = "";
        int lastHistory = -1, lastStep, lastResults, lastPractice = -2;
        float lastBh;
        string lastState, lastStatus;

        public Transcript(Runtime rt)
        {
            this.rt = rt;
            root = Draw.Child(rt.Stage, "transcript").transform;
            root.gameObject.SetActive(PlayerPrefs.GetInt("vcl.transcript", 1) == 1);
        }

        /// <summary>The panel's corners in world space (for fitting the desktop camera).</summary>
        public IEnumerable<Vector3> WorldCorners()
        {
            if (!Visible) yield break;
            float bh = rt.Board?.Height ?? 9;
            foreach (var x in new[] { -W / 2, W / 2 }) foreach (var y in new[] { -bh / 2, bh / 2 }) yield return root.TransformPoint(new Vector3(x, y, 0));
        }

        /// <summary>One entry per step: what is said in it, or the question.</summary>
        List<(int step, string text, string mark)> Entries()
        {
            var list = new List<(int step, string text, string mark)>();
            foreach (var (cmd, step, instant) in rt.History)
            {
                if (instant || step == 0) continue;
                string text = null, mark = null;
                if (cmd.Op == "say") text = cmd.Str("text");
                else if (cmd.Op == "quiz" || cmd.Op == "pick" || cmd.Op == "estimate" || cmd.Op == "recall")
                {
                    text = cmd.Str("q");
                    mark = rt.Results.TryGetValue(cmd, out var r) ? (r.Skipped ? "–" : r.Correct ? "✓" : "✗") : "?";
                }
                if (text == null) continue;
                if (list.Count > 0 && list[list.Count - 1].step == step && mark == null && list[list.Count - 1].mark == null)
                    list[list.Count - 1] = (step, list[list.Count - 1].text + " " + text, list[list.Count - 1].mark);
                else list.Add((step, text, mark));
            }
            return list;
        }

        /// <summary>Redraw when something changed (cheap to call every frame).</summary>
        public void Update()
        {
            if (!Visible) return;
            // Beside the board's right edge, turned a little toward the learner.
            float bw = rt.Board?.Width ?? 16, bh = rt.Board?.Height ?? 9;
            root.localPosition = new Vector3(bw / 2 + Gap + W / 2 * 0.94f, 0, -W / 2 * 0.34f);
            root.localRotation = Quaternion.Euler(0, 20, 0);
            // Redraw only when something it shows changed.
            int h = rt.History.Count;
            int practice = rt.Practicing?.I ?? -1;
            if (h == lastHistory && rt.Step == lastStep && rt.Results.Count == lastResults && bh == lastBh && rt.State == lastState && Status == lastStatus && practice == lastPractice) return;
            (lastHistory, lastStep, lastResults, lastBh, lastState, lastStatus, lastPractice) = (h, rt.Step, rt.Results.Count, bh, rt.State, Status, practice);
            Render(Entries(), bh);
        }

        void Render(List<(int step, string text, string mark)> entries, float bh)
        {
            foreach (var old in Hits) old.Hover?.Invoke(false);
            Hits.Clear();
            foreach (var c in root.Cast<Transform>().ToList()) Draw.Kill(c.gameObject);
            float top = bh / 2, h = bh, x0 = -W / 2 + Pad, inner = W - 2 * Pad;
            var bg = Draw.MeshObject(root, "bg", Draw.Fill(Draw.RoundedRect(W, h, 0.3f)), Draw.Unlit(Bg, 3005), Order);
            bg.transform.localPosition = new Vector3(0, 0, 0.01f);
            var edge = new Stroke(root, EdgeC, 0.04f) { Order = Order + 1 };
            edge.Set(new List<List<Vector3>> { Draw.RoundedRect(W, h, 0.3f) });

            // Header: the playback buttons and Practice (just the questions again); then the status.
            float y = top - Pad;
            bool paused = rt.State == "paused";
            float bx = W / 2 - Pad - 0.85f;
            Button(bx, y - 0.25f, 0.85f, 0.6f, "▶▶", () => rt.Forward());
            Button(bx - 0.95f, y - 0.25f, 0.85f, 0.6f, paused ? "▶" : "||", () => rt.TogglePause());
            Button(bx - 1.9f, y - 0.25f, 0.85f, 0.6f, "◀◀", () => rt.Back());
            if (rt.Practicing != null || rt.QuestionSteps.Count > 0)
                Button(x0 - 0.05f, y - 0.25f, 2.6f, 0.6f, rt.Practicing != null ? "End practice" : "Practice", () =>
                {
                    if (rt.Practicing != null) rt.EndPractice(); else rt.Practice();
                }, 0.3f);
            y -= 0.75f;
            Label(Status, x0, y - 0.2f, 0.28f, Draw.FG, "left");
            y -= 0.55f;
            var rule = Draw.MeshObject(root, "rule", Draw.Fill(Draw.RoundedRect(inner, 0.03f, 0.01f)), Draw.Unlit(EdgeC, 3006), Order + 1).transform;
            rule.localPosition = new Vector3(0, y, -0.01f);
            y -= 0.25f;

            if (entries.Count == 0)
            {
                Label("The lesson's sentences and questions appear here as it plays.", x0, y - 0.3f, 0.26f, Muted, "left", inner);
                return;
            }
            // From a little before the current step, as many as fit.
            int cur = Math.Max(0, entries.FindIndex(e => e.step >= Math.Max(1, rt.Step)));
            if (cur < 0) cur = entries.Count - 1;
            int first = Math.Max(0, cur - 2);
            float bottom = -bh / 2 + Pad;
            for (int i = first; i < entries.Count; i++)
            {
                var (step, text, mark) = entries[i];
                bool now = step == rt.Step, past = step < rt.Step;
                var color = now ? Draw.FG : past ? Muted : Dim;
                var wrapped = QuestionCard.Wrap((mark != null ? mark + " " : "") + text, Math.Max(8, (int)((inner - 0.3f) / (Size * 0.42f))));
                var t = Draw.Text(root, wrapped, Size, color, "left", now);
                var sz = Draw.TextSize(t);
                if (y - sz.y < bottom) { Draw.Kill(t.gameObject); break; }
                t.transform.localPosition = new Vector3(x0 + 0.3f, y - sz.y / 2, -0.02f);
                t.renderer.sortingOrder = Order + 3;
                float rowH = sz.y + 0.2f;
                var row = Draw.Child(root, "row").transform;
                row.localPosition = new Vector3(0, y - rowH / 2 + 0.1f, -0.01f);
                Material mat = null;
                if (now)
                {
                    var bar = Draw.MeshObject(row, "bar", Draw.Fill(Draw.RoundedRect(0.08f, sz.y, 0.03f)), Draw.Unlit(Accent, 3006), Order + 2).transform;
                    bar.localPosition = new Vector3(x0 + 0.08f, 0, 0);
                }
                var hl = Draw.MeshObject(row, "hover", Draw.Fill(Draw.RoundedRect(inner + 0.2f, rowH, 0.1f)), mat = Draw.Unlit(new Color(0.18f, 0.43f, 0.71f, 0f), 3006), Order + 1);
                int target = step;
                Hits.Add(new Hit
                {
                    T = row, R = new Rect(-(inner + 0.2f) / 2, -rowH / 2, inner + 0.2f, rowH), Press = () => rt.Seek(target), Order = 0,
                    Hover = on => { if (mat) mat.color = new Color(0.18f, 0.43f, 0.71f, on ? 0.35f : 0); },
                });
                y -= rowH;
            }
        }

        TextMeshPro Label(string text, float x, float y, float size, Color color, string align = "left", float wrapW = 0)
        {
            var t = Draw.Text(root, wrapW > 0 ? QuestionCard.Wrap(text, (int)(wrapW / (size * 0.5f))) : text, size, color, align, false);
            t.transform.localPosition = new Vector3(x, y, -0.02f);
            t.renderer.sortingOrder = Order + 3;
            return t;
        }

        void Button(float x, float y, float w, float h, string label, Action press, float size = 0.34f)
        {
            var mat = Draw.Unlit(ButtonC, 3006);
            var b = Draw.MeshObject(root, "button", Draw.Fill(Draw.RoundedRect(w, h, 0.12f)), mat, Order + 2).transform;
            b.localPosition = new Vector3(x + w / 2, y, -0.01f);
            var t = Draw.Text(root, label, size, Draw.FG, "center", true);
            t.transform.localPosition = new Vector3(x + w / 2, y, -0.03f);
            t.renderer.sortingOrder = Order + 3;
            Hits.Add(new Hit
            {
                T = b, R = new Rect(-w / 2, -h / 2, w, h), Press = press, Order = 1,
                Hover = on => { if (mat) mat.color = Color.Lerp(ButtonC, HoverC, on ? 0.6f : 0); },
            });
        }
    }
}
