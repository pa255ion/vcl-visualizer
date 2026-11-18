// The in-headset menu: pick a lesson (built-in or from the server's
// --lessons library), stop, step mode, and how the room is aligned (upright,
// or to your view when reclining). A world-space panel, pointed at with
// either controller; the trigger presses, the thumbstick pages.
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Vcl
{
    public class LessonInfo
    {
        public string Path;   // URL path on the server: lessons/x.vcl or library/u01/x.md
        public string Title;
        public string Folder; // "Built-in" or the library folder
    }

    public class Menu
    {
        class Button
        {
            public Rect R;
            public Action Do;
            public Transform Bg;
            public Material Mat;
            public Color Base;
        }

        const float Scale = 0.05f;         // panel units → metres
        const float W = 22, H = 16, RowH = 1.12f;
        const int PerPage = 9;

        public bool Open => root && root.gameObject.activeSelf;
        public List<LessonInfo> Lessons = new List<LessonInfo>();
        public string Status = "";

        // What the app does for us.
        public Action<LessonInfo> Play;
        public Action Stop, ToggleStep, ToggleVoice, Upright, Recline, Refresh;
        public Func<bool> Stepping, Reclined, Voiced;

        Transform root, content;
        readonly List<Button> buttons = new List<Button>();
        string folder; // null: the folder list
        int page;
        Button hover;
        int keyIndex = -1; // desktop: keyboard selection
        LineRenderer laser;
        Transform dot;

        public void Toggle(Transform head)
        {
            if (Open) { Close(); return; }
            if (!root) BuildRoot();
            // In front of the eyes, upright to the view (works lying down too).
            var fwd = head.forward;
            root.position = head.position + fwd * 0.85f - head.up * 0.05f;
            root.rotation = Quaternion.LookRotation(fwd, head.up);
            root.gameObject.SetActive(true);
            Refresh?.Invoke();
            Rebuild();
        }

        public void Close()
        {
            if (root) root.gameObject.SetActive(false);
            if (laser) laser.enabled = false;
            if (dot) dot.gameObject.SetActive(false);
        }

        void BuildRoot()
        {
            root = new GameObject("menu").transform;
            root.localScale = Vector3.one * Scale;
            var bg = Draw.Unlit(Draw.Hex("#0d1420"), 3100);
            Draw.MeshObject(root, "bg", Draw.Fill(Draw.RoundedRect(W, H, 0.6f)), bg, 20);
            var border = new Stroke(root, Draw.Hex("#3b4a5e"), 0.08f) { Order = 21 };
            border.Set(new List<List<Vector3>> { Draw.RoundedRect(W, H, 0.6f) });
            content = Draw.Child(root, "content").transform;
            laser = new GameObject("laser").AddComponent<LineRenderer>();
            laser.useWorldSpace = true;
            laser.widthMultiplier = 0.004f;
            laser.sharedMaterial = Draw.Unlit(Draw.Hex("#8ec5ff"), 3150);
            laser.sortingOrder = 30;
            laser.positionCount = 2;
            laser.enabled = false;
            var dm = Draw.Unlit(Color.white, 3160);
            dot = Draw.MeshObject(root, "dot", Draw.Disc(0.18f), dm, 31).transform;
            dot.gameObject.SetActive(false);
        }

        static string Short(string t, int max)
        {
            if (t.StartsWith("Lesson ")) t = t.Substring(7);
            return t.Length <= max ? t : t.Substring(0, max - 1) + "…";
        }

        /// <summary>Redraw the panel for the current folder and page.</summary>
        public void Rebuild()
        {
            if (!root) return;
            foreach (Transform c in content) Draw.Kill(c.gameObject);
            buttons.Clear();
            hover = null;
            float top = H / 2 - 0.9f, left = -W / 2 + 0.7f, right = W / 2 - 0.7f;

            // Header: where we are, and the actions.
            Label(folder == null ? "Lessons" : Short(folder, 14), new Vector3(left, top, 0), 0.95f, Draw.FG, "left", true);
            float x = right;
            x = ActionButton(x, top, Reclined() ? "Upright" : "Recline", () => { if (Reclined()) Upright(); else Recline(); Rebuild(); });
            x = ActionButton(x, top, Stepping() ? "Step: on" : "Step: off", () => { ToggleStep(); Rebuild(); });
            x = ActionButton(x, top, Voiced() ? "Voice: on" : "Voice: off", () => { ToggleVoice(); Rebuild(); });
            ActionButton(x, top, "Stop", () => { Stop(); Rebuild(); });

            // Rows: folders, or the lessons of one folder.
            var rows = new List<(string text, Action act, string hint)>();
            if (folder == null)
            {
                foreach (var g in Lessons.GroupBy(l => l.Folder))
                {
                    var name = g.Key;
                    rows.Add(($"{name}", () => { folder = name; page = 0; Rebuild(); }, $"{g.Count()} ›"));
                }
                if (rows.Count == 0) rows.Add(("(no lessons yet: is the server reachable?)", Refresh, ""));
            }
            else
            {
                rows.Add(("‹ All folders", () => { folder = null; page = 0; Rebuild(); }, ""));
                foreach (var l in Lessons.Where(l => l.Folder == folder))
                {
                    var lesson = l;
                    rows.Add((Short(l.Title, 52), () => { Play(lesson); Close(); }, ""));
                }
            }
            int pages = Mathf.Max(1, (rows.Count + PerPage - 1) / PerPage);
            page = Mathf.Clamp(page, 0, pages - 1);
            float y = top - 1.5f;
            foreach (var r in rows.Skip(page * PerPage).Take(PerPage))
            {
                AddButton(new Rect(left - 0.2f, y - RowH / 2 + 0.06f, right - left + 0.4f, RowH - 0.12f), "#16202c", r.act);
                Label(r.text, new Vector3(left + 0.2f, y, -0.02f), 0.66f, Draw.Hex("#e6edf3"), "left");
                if (r.hint.Length > 0) Label(r.hint, new Vector3(right - 0.1f, y, -0.02f), 0.6f, Draw.Hex("#8a98ab"), "right");
                y -= RowH;
            }

            // Footer: pages, and what is going on.
            float fy = -H / 2 + 0.9f;
            if (pages > 1)
            {
                AddButton(new Rect(left - 0.2f, fy - 0.45f, 2.6f, 0.9f), "#1c2a3d", () => { page = Mathf.Max(0, page - 1); Rebuild(); });
                Label("‹ Prev", new Vector3(left + 1.1f, fy, -0.02f), 0.6f, Draw.FG);
                AddButton(new Rect(right - 2.4f, fy - 0.45f, 2.6f, 0.9f), "#1c2a3d", () => { page = Mathf.Min(pages - 1, page + 1); Rebuild(); });
                Label("Next ›", new Vector3(right - 1.1f, fy, -0.02f), 0.6f, Draw.FG);
                Label($"{page + 1} / {pages}", new Vector3(0, fy, -0.02f), 0.55f, Draw.Hex("#8a98ab"));
            }
            Label(Status, new Vector3(0, fy - 0.95f, -0.02f), 0.45f, Draw.Hex("#8a98ab"));
            Label("trigger: choose · stick: page · A/X: close", new Vector3(0, -H / 2 - 0.6f, -0.02f), 0.45f, Draw.Hex("#6b7a8f"));
        }

        float ActionButton(float right, float y, string text, Action act)
        {
            float w = Mathf.Max(2.6f, text.Length * 0.36f + 0.8f);
            AddButton(new Rect(right - w, y - 0.48f, w, 0.96f), "#1c2a3d", act);
            Label(text, new Vector3(right - w / 2, y, -0.02f), 0.58f, Draw.FG);
            return right - w - 0.3f;
        }

        void AddButton(Rect r, string color, Action act)
        {
            var mat = Draw.Unlit(Draw.Hex(color), 3110);
            var q = Draw.MeshObject(content, "button", Draw.Fill(Draw.RoundedRect(r.width, r.height, 0.25f)), mat, 22).transform;
            q.localPosition = new Vector3(r.center.x, r.center.y, -0.01f);
            buttons.Add(new Button { R = r, Do = act, Bg = q, Mat = mat, Base = mat.color });
        }

        void Label(string text, Vector3 pos, float size, Color color, string align = "center", bool bold = false)
        {
            var t = Draw.Text(content, text, size, color, align, bold);
            t.transform.localPosition = pos;
            t.renderer.sortingOrder = 24;
        }

        /// <summary>A pointer ray in world space; returns true if it pressed a button.</summary>
        public bool Point(Ray? ray, bool press)
        {
            if (!Open) return false;
            Button hit = null;
            Vector3 at = Vector3.zero;
            if (ray.HasValue)
            {
                var o = root.InverseTransformPoint(ray.Value.origin);
                var d = root.InverseTransformDirection(ray.Value.direction);
                if (Mathf.Abs(d.z) > 1e-5f)
                {
                    float t = -o.z / d.z;
                    if (t > 0)
                    {
                        at = o + d * t;
                        hit = buttons.FirstOrDefault(b => b.R.Contains(new Vector2(at.x, at.y)));
                    }
                }
                bool onPanel = t0(at) && ray.HasValue;
                laser.enabled = true;
                laser.SetPosition(0, ray.Value.origin);
                laser.SetPosition(1, onPanel ? root.TransformPoint(at) : ray.Value.origin + ray.Value.direction * 1.5f);
                dot.gameObject.SetActive(onPanel);
                dot.localPosition = new Vector3(at.x, at.y, -0.05f);
            }
            SetHover(hit);
            if (press && hit != null) { hit.Do(); return true; }
            return false;
        }

        static bool t0(Vector3 at) => Mathf.Abs(at.x) <= W / 2 && Mathf.Abs(at.y) <= H / 2;

        void SetHover(Button b)
        {
            if (hover == b) return;
            if (hover != null && hover.Mat) hover.Mat.color = hover.Base;
            hover = b;
            if (hover != null) hover.Mat.color = Color.Lerp(hover.Base, Draw.Hex("#2f6db5"), 0.7f);
        }

        public void Page(int delta)
        {
            page += delta;
            Rebuild();
        }

        /// <summary>Desktop: move the selection with the arrow keys, Enter to press.</summary>
        public void Key(int delta, bool press)
        {
            if (!Open || buttons.Count == 0) return;
            if (delta != 0) keyIndex = ((keyIndex + delta) % buttons.Count + buttons.Count) % buttons.Count;
            if (keyIndex >= 0 && keyIndex < buttons.Count) SetHover(buttons[keyIndex]);
            if (press && hover != null) hover.Do();
        }
    }
}
