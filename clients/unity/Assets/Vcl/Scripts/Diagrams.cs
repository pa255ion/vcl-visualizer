// Engineering diagrams and panels: part, wire, block, frame, code, table,
// wave, img. A port of the matching BUILD.* functions in viewer/js/runtime.js.
// Unity's z points away from the learner, so "in front" is -z here.
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Vcl
{
    public partial class Runtime
    {
        /// <summary>Loads an image URL (relative to the server) for `img`; set by the app.</summary>
        public Action<string, Action<Texture2D>> LoadTexture;

        static Vector3 V2(float x, float y, float z = 0) => new Vector3(x, y, z);

        /// <summary>Text at a local position, tracked for opacity.</summary>
        static TextMeshPro TextAt(Entity e, Transform parent, string s, Vector3 pos, float size, Color color, string align = "center", string role = "label", bool bold = false, float baseAlpha = -1, TMP_FontAsset face = null)
        {
            var t = Draw.Text(parent, s, size, color, align, bold, face);
            t.transform.localPosition = pos;
            e.TrackT(t, role, baseAlpha);
            if (e.In3D) e.Billboards.Add(t.transform);
            return t;
        }

        static Stroke StrokeIn(Entity e, Transform parent, Color color, float width, string role = "stroke", bool dash = false, float baseAlpha = 1)
        {
            var s = new Stroke(parent, color, width, dash, e.In3D);
            e.Strokes.Add(s);
            e.Track(s.Material, role, baseAlpha);
            return s;
        }

        static List<Vector3> Rect(float x0, float y0, float x1, float y1, float z = 0) =>
            new List<Vector3> { V2(x0, y0, z), V2(x1, y0, z), V2(x1, y1, z), V2(x0, y1, z), V2(x0, y0, z) };

        // ── part: a schematic symbol, with pins ──

        void BuildPart(Entity e, Cmd c)
        {
            var sym = Parts.Get(c.Str("kind"));
            float k = c.Flt("size", 1);
            float rot = c.Flt("rot", 0) * Mathf.Deg2Rad, cs = Mathf.Cos(rot), sn = Mathf.Sin(rot);
            float flip = c.Bool("flip") ? -1 : 1;
            Vector3 Xf(Vector2 q, float scale)
            {
                float xx = q.x * flip * scale, yy = q.y * scale;
                return V2(xx * cs - yy * sn, xx * sn + yy * cs);
            }
            e.Obj.localPosition = e.ToParent(c["at"]);
            var color = Draw.Hex(c.Str("color"));
            var lines = sym.Lines.Select(pl => pl.Select(q => Xf(q, k)).ToList()).ToList();
            var stroke = StrokeIn(e, e.Body, color, 0.045f);
            var fillMat = e.Track(Draw.Unlit(color, 3002), "stroke");
            foreach (var f in sym.Fills) Draw.MeshObject(e.Body, "fill", Draw.Fill(f.Select(q => Xf(q, k)).ToList()), fillMat, 2);
            foreach (var t in sym.Texts)
                TextAt(e, e.Body, t.value && c.Has("value") ? c.Str("value") : t.text, Xf(new Vector2(t.x, t.y), k) + V2(0, 0, -0.01f), t.size * k, color, role: "text");
            e.Pins = new Dictionary<string, (Vector3, Vector3)>();
            foreach (var kv in sym.Pins) e.Pins[kv.Key] = (Xf(kv.Value.at, k), Xf(kv.Value.dir, 1).normalized);
            // Designator and value: above/below a wide part, beside a tall one (where its leads aren't).
            var all = lines.SelectMany(p => p).ToList();
            var box = new Bounds(all[0], Vector3.zero);
            foreach (var v in all) box.Encapsulate(v);
            var ctr = box.center;
            bool tall = box.size.y > box.size.x * 1.2f;
            var core = all.Where(v => Mathf.Abs(v.y - ctr.y) < (box.max.y - ctr.y) * 0.6f).ToList();
            float bodyMaxX = core.Count > 0 ? core.Max(v => v.x) : ctr.x;
            float side = tall ? Mathf.Max(bodyMaxX, ctr.x + 0.2f) + 0.2f : 0;
            if (c.Has("label"))
                e.AddLabel(c.Str("label"), tall ? V2(side, ctr.y + 0.22f) : V2(ctr.x, box.max.y + 0.28f), tall ? "left" : "center");
            if (c.Has("value") && !sym.Texts.Any(t => t.value))
                TextAt(e, e.Obj, c.Str("value"), tall ? V2(side, ctr.y - (c.Has("label") ? 0.22f : 0)) : V2(ctr.x, box.min.y - 0.28f), 0.32f, Draw.Hex("#aab6c5"), tall ? "left" : "center");
            e.Render = p =>
            {
                stroke.Set(Draw.Partial(lines, p));
                e.Factor["fill"] = p;
                e.ApplyOpacity();
            };
        }

        // ── wire: orthogonal, between points and pins ──

        void BuildWire(Entity e, Cmd c)
        {
            var raw = c.List("pts");
            string mode = c.Str("route") ?? "auto";
            bool bus = c.Has("bus");
            string dotAt = c.Str("dot");
            StrokeSpec Spec()
            {
                var P = raw.Select(pt =>
                {
                    var v = e.ToParent(pt);
                    Vector3? d = IsRef(pt) && e.Axes == null ? PinDir((Dictionary<string, object>)pt) : null;
                    return new Parts.RoutePt { P = v, Dir = d.HasValue ? new Vector2(d.Value.x, d.Value.y) : (Vector2?)null };
                }).ToList();
                var pts = Parts.Route(P, mode);
                float best = 0;
                int bi = 0;
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    float d = Vector3.Distance(pts[i], pts[i + 1]);
                    if (d > best) { best = d; bi = i; }
                }
                Vector3 a = pts[bi], b = pts[Mathf.Min(bi + 1, pts.Count - 1)];
                var mid = (a + b) / 2;
                var polys = new List<List<Vector3>> { pts };
                if (bus)
                {
                    var dd = (b - a).normalized;
                    var n = V2(-dd.y, dd.x);
                    polys.Add(new List<Vector3> { mid - dd * 0.12f - n * 0.18f, mid + dd * 0.12f + n * 0.18f });
                }
                var dots = new List<Vector3>();
                if (dotAt == "start" || dotAt == "both") dots.Add(pts[0]);
                if (dotAt == "end" || dotAt == "both") dots.Add(pts[pts.Count - 1]);
                if (e.Label)
                {
                    bool vertical = Mathf.Abs(b.x - a.x) < Mathf.Abs(b.y - a.y);
                    e.LabelShift = vertical ? V2(0.22f + Draw.TextSize(e.Label).x / 2, 0) : V2(0, bus ? 0.42f : 0.3f);
                }
                return new StrokeSpec { Polys = polys, Ends = new[] { pts[0], pts[pts.Count - 1] }, Label = mid, Dots = dots, Live = raw.Any(IsRef) };
            }
            string text = c.Has("label") ? c.Str("label") : bus ? FormatNum(c.Num("bus", 0)) : null;
            if (text != null) e.AddLabel(text, Vector3.zero);
            StrokeEntity(e, Spec, new StrokeOpts { Width = c.Flt("width", 0.04f), Color = Draw.Hex(c.Str("color") ?? "#8ec5ff"), Dash = c.Bool("dash"), HeadEnd = c.Bool("arrow") });
        }

        static string FormatNum(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        static List<string> PortList(string s) => (s ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        // ── block: a box with named ports on its sides ──

        void BuildBlock(Entity e, Cmd c)
        {
            var sz = Raw(c["size"]);
            float w = sz.x, h = sz.y;
            e.Obj.localPosition = e.ToParent(c["at"]);
            var color = Draw.Hex(c.Str("color") ?? "#7fa7d9");
            var outline = Draw.RoundedRect(w, h, Mathf.Min(0.15f, Mathf.Min(w / 4, h / 4)));
            var stroke = StrokeIn(e, e.Body, color, 0.05f);
            var fillMat = e.Track(Draw.Unlit(Draw.Hex(c.Str("fill") ?? "#16202c"), 3001), "fill", 0.95f);
            Draw.MeshObject(e.Body, "fill", Draw.Fill(outline), fillMat, 1);
            e.Shape = ("box", w / 2, h / 2);
            bool band = PortList(c.Str("in")).Count + PortList(c.Str("out")).Count > 0;
            float topGap = PortList(c.Str("top")).Count > 0 ? 0.32f : 0.08f;
            float sideTop = h / 2 - (topGap > 0.08f ? topGap : 0);
            if (c.Has("text"))
            {
                var t = TextAt(e, e.Body, c.Str("text"), V2(0, 0, -0.01f), 0.42f, Draw.FG, role: "text", bold: true);
                if (band)
                {
                    float th = Draw.TextSize(t).y;
                    t.transform.localPosition = V2(0, h / 2 - topGap - th / 2, -0.01f);
                    sideTop = h / 2 - topGap - th;
                }
            }
            e.Pins = new Dictionary<string, (Vector3, Vector3)>();
            var ticks = new List<List<Vector3>>();
            void Side(List<string> names, Func<int, int, (Vector3 at, Vector3 dir, Vector3 text, string align)> place)
            {
                for (int i = 0; i < names.Count; i++)
                {
                    var (at, dir, tp, align) = place(i, names.Count);
                    e.Pins[names[i]] = (at, dir);
                    ticks.Add(new List<Vector3> { at, at + dir * 0.12f });
                    TextAt(e, e.Obj, names[i], tp + V2(0, 0, -0.01f), 0.26f, Draw.Hex("#aab6c5"), align);
                }
            }
            float SideY(int i, int n) => sideTop - (i + 1) * (sideTop + h / 2) / (n + 1);
            (Vector3, Vector3, Vector3, string) Lp(int i, int n) { float y = SideY(i, n); return (V2(-w / 2, y), V2(-1, 0), V2(-w / 2 + 0.12f, y), "left"); }
            (Vector3, Vector3, Vector3, string) Rp(int i, int n) { float y = SideY(i, n); return (V2(w / 2, y), V2(1, 0), V2(w / 2 - 0.12f, y), "right"); }
            bool flip = c.Bool("flip");
            Side(PortList(c.Str("in")), flip ? Rp : (Func<int, int, (Vector3, Vector3, Vector3, string)>)Lp);
            Side(PortList(c.Str("out")), flip ? Lp : (Func<int, int, (Vector3, Vector3, Vector3, string)>)Rp);
            Side(PortList(c.Str("top")), (i, n) => { float x = -w / 2 + (i + 1) * w / (n + 1); return (V2(x, h / 2), V2(0, 1), V2(x, h / 2 - 0.22f), "center"); });
            Side(PortList(c.Str("bottom")), (i, n) => { float x = -w / 2 + (i + 1) * w / (n + 1); return (V2(x, -h / 2), V2(0, -1), V2(x, -h / 2 + 0.22f), "center"); });
            if (c.Has("label")) e.AddLabel(c.Str("label"), V2(0, h / 2 + 0.3f));
            var all = new List<List<Vector3>> { outline };
            all.AddRange(ticks);
            e.Render = p =>
            {
                stroke.Set(Draw.Partial(all, p));
                e.Factor["fill"] = p;
                e.ApplyOpacity();
            };
        }

        // ── frame: a titled boundary that follows its members ──

        void BuildFrame(Entity e, Cmd c)
        {
            var members = c.List("ids").Select(id => Get(id as string)).ToList();
            float pad = c.Flt("pad", 0.35f);
            StrokeSpec Spec()
            {
                Bounds? box = null;
                foreach (var m in members)
                {
                    var b = StageBounds(m);
                    if (b == null) continue;
                    if (box == null) box = b; else { var bb = box.Value; bb.Encapsulate(b.Value); box = bb; }
                }
                var bx = box ?? new Bounds(Vector3.zero, new Vector3(1, 1, 0));
                float w = bx.size.x + pad * 2, h = bx.size.y + pad * 2;
                Vector3 ToP(Vector3 v) => e.ParentObj.InverseTransformPoint(Stage.TransformPoint(v));
                var pts = Draw.RoundedRect(w, h, 0.25f).Select(q => ToP(V2(q.x + bx.center.x, q.y + bx.center.y, 0.02f))).ToList();
                return new StrokeSpec { Polys = new List<List<Vector3>> { pts }, Fill = c.Has("fill") ? pts : null, Label = ToP(V2(bx.min.x - pad, bx.max.y + pad)), Live = true };
            }
            if (c.Has("title"))
            {
                e.AddLabel(c.Str("title"), Vector3.zero, "left");
                e.Tints[e.Tints.Count - 1].Color = Draw.Hex("#aab6c5");
                e.LabelShift = V2(0.05f, 0.28f);
            }
            StrokeEntity(e, Spec, new StrokeOpts
            {
                Width = 0.035f, Color = Draw.Hex(c.Str("color") ?? "#5f7590"), Dash = c.Bool("dash", true),
                FillColor = c.Has("fill") ? Draw.Hex(c.Str("fill")) : (Color?)null, FillOpacity = 0.12f,
            });
        }

        // ── panels: code and table, rebuilt on set text= / focus= ──

        /// <summary>Runs `build` into a fresh child, replacing (and untracking) the previous one.</summary>
        static Action Rebuilder(Entity e, Action<Transform> build)
        {
            Transform box = null;
            var mine = new List<Tint>();
            var mineStrokes = new List<Stroke>();
            return () =>
            {
                if (box)
                {
                    Draw.Kill(box.gameObject);
                    e.Tints.RemoveAll(mine.Contains);
                    e.Strokes.RemoveAll(mineStrokes.Contains);
                    mine.Clear();
                    mineStrokes.Clear();
                }
                box = Draw.Child(e.Body, "panel").transform;
                int t0 = e.Tints.Count, s0 = e.Strokes.Count;
                // Build with the body active (it is hidden at opacity 0, e.g. when a file
                // arrives before the entrance): TextMeshPro set up while inactive breaks.
                // ApplyOpacity below hides it again if it should be.
                e.Body.gameObject.SetActive(true);
                build(box);
                mine.AddRange(e.Tints.GetRange(t0, e.Tints.Count - t0));
                mineStrokes.AddRange(e.Strokes.GetRange(s0, e.Strokes.Count - s0));
                e.ApplyOpacity();
            };
        }

        static void PanelBack(Entity e, Transform box, float W, float H, float r, string fill, string border)
        {
            var outline = Draw.RoundedRect(W, H, r);
            Draw.MeshObject(box, "bg", Draw.Fill(outline), e.Track(Draw.Unlit(Draw.Hex(fill), 3001), "panel"), 1);
            StrokeIn(e, box, Draw.Hex(border), 0.03f, "panel").Set(new List<List<Vector3>> { outline });
        }

        static void Band(Entity e, Transform box, float x0, float y0, float x1, float y1, string color)
        {
            var q = Draw.QuadObj(box, e.Track(Draw.Unlit(Draw.Hex(color), 3001), "panel"), 2);
            Draw.SetQuad(q, V2(x0, y0, -0.003f), V2(x1, y1, -0.003f));
        }

        static string Esc(string s) => "<noparse>" + s.Replace("</noparse>", "") + "</noparse>";

        /// <summary>Width of one monospaced character at glyph size `s`.</summary>
        static float MonoWidth(Transform parent, float s)
        {
            var t = Draw.Text(parent, "MMMMMMMMMM", s, Color.white, "left", false, Draw.Mono);
            float w = Draw.TextSize(t).x / 10;
            Draw.Kill(t.gameObject);
            return w;
        }

        void BuildCode(Entity e, Cmd c)
        {
            float size = c.Flt("size", 0.48f);
            e.Obj.localPosition = e.ToParent(c["at"]);
            string text = c.Str("text") ?? "", lang = c.Str("lang"), title = c.Str("title");
            bool lines = c.Bool("lines", true);
            var focus = c.Range("focus");
            float H = 0;
            var redraw = Rebuilder(e, box =>
            {
                var rows = Panels.Highlight(text, lang);
                int n = rows.Count;
                float s = size * 0.85f, cw = MonoWidth(box, s), pad = size * 0.43f, titleH = title != null ? size * 1.2f : 0;
                int digits = n.ToString().Length;
                int maxChars = Math.Max(1, rows.Max(r => r.Sum(t => t.Text.Length))) + (lines ? digits + 2 : 0);
                float W = maxChars * cw + pad * 1.5f;
                H = n * size + pad * 2 + titleH;
                PanelBack(e, box, W, H, size * 0.29f, Panels.Theme["bg"], Panels.Theme["border"]);
                if (title != null)
                {
                    Band(e, box, -W / 2 + 0.03f, H / 2 - titleH, W / 2 - 0.03f, H / 2 - 0.03f, Panels.Theme["title"]);
                    TextAt(e, box, title, V2(-W / 2 + pad, H / 2 - titleH / 2, -0.01f), size * 0.8f, Draw.Hex(Panels.Theme["gutter"]), "left", "text");
                }
                bool In(int k) => focus == null || (k >= focus[0] && k <= focus[1]);
                e.Pins = new Dictionary<string, (Vector3, Vector3)>();
                for (int i = 0; i < n; i++)
                {
                    float y = H / 2 - titleH - pad - (i + 0.5f) * size;
                    if (focus != null && In(i + 1)) Band(e, box, -W / 2 + 0.03f, y - size / 2, W / 2 - 0.03f, y + size / 2, Panels.Theme["focus"]);
                    var sb = new System.Text.StringBuilder();
                    if (lines) sb.Append($"<color={Panels.Theme["gutter"]}>{(i + 1).ToString().PadLeft(digits)}  </color>");
                    foreach (var t in rows[i]) sb.Append($"<color={Panels.Theme[Panels.Theme.ContainsKey(t.Type) ? t.Type : "plain"]}>{Esc(t.Text)}</color>");
                    TextAt(e, box, sb.ToString(), V2(-W / 2 + pad * 0.75f, y, -0.01f), s, Color.white, "left", "text", baseAlpha: In(i + 1) ? 1 : 0.4f, face: Draw.Mono);
                    e.Pins[$"L{i + 1}"] = (V2(W / 2, y), V2(1, 0));
                }
            });
            redraw();
            e.SetText = t => { text = t; redraw(); };
            e.SetFocus = f => { focus = f; redraw(); };
            LabelFor(e, c, V2(0, H / 2 + 0.3f));
        }

        void BuildTable(Entity e, Cmd c)
        {
            float size = c.Flt("size", 0.65f);
            e.Obj.localPosition = e.ToParent(c["at"]);
            var rowsRaw = c.List("rows")?.Select(x => x as string ?? "").ToList() ?? (c.Str("rows") ?? "").Split('\n').ToList();
            bool header = c.Bool("header", true);
            var focus = c.Range("focus");
            float H = 0;
            var redraw = Rebuilder(e, box =>
            {
                var grid = Panels.Cells(rowsRaw);
                int ncol = grid.Max(r => r.Length), nrow = grid.Count;
                float s = size * 0.75f, padX = size * 0.44f;
                var texts = new TextMeshPro[nrow, ncol];
                var widths = new float[ncol];
                for (int i = 0; i < nrow; i++)
                    for (int k = 0; k < ncol; k++)
                    {
                        bool head = header && i == 0;
                        var t = Draw.Text(box, k < grid[i].Length ? grid[i][k] : "", s, Draw.Hex(head ? "#ffffff" : "#e6edf3"), "center", head);
                        texts[i, k] = t;
                        widths[k] = Mathf.Max(widths[k], Draw.TextSize(t).x + padX * 2);
                    }
                float W = widths.Sum() + 0.06f;
                H = nrow * size + 0.06f;
                PanelBack(e, box, W, H, size * 0.2f, "#111823", "#30363d");
                bool InFocus(int dataRow) => focus != null && dataRow >= focus[0] && dataRow <= focus[1];
                e.Pins = new Dictionary<string, (Vector3, Vector3)>();
                var rules = new List<List<Vector3>>();
                for (int i = 0; i < nrow; i++)
                {
                    float y0 = H / 2 - 0.03f - (i + 1) * size, y1 = y0 + size, ym = (y0 + y1) / 2;
                    int dataRow = header ? i : i + 1;
                    bool head = header && i == 0;
                    if (head) Band(e, box, -W / 2 + 0.03f, y0, W / 2 - 0.03f, y1, "#1c2a3d");
                    else if (InFocus(dataRow)) Band(e, box, -W / 2 + 0.03f, y0, W / 2 - 0.03f, y1, "#1f3a5f");
                    else if (i % 2 == 0) Band(e, box, -W / 2 + 0.03f, y0, W / 2 - 0.03f, y1, "#0f151e");
                    float x = -W / 2 + 0.03f;
                    for (int k = 0; k < ncol; k++)
                    {
                        var t = texts[i, k];
                        t.transform.localPosition = V2(x + widths[k] / 2, ym, -0.01f);
                        e.TrackT(t, "text", focus != null && !head && !InFocus(dataRow) ? 0.45f : 1);
                        x += widths[k];
                    }
                    if (!head) e.Pins[$"R{dataRow}"] = (V2(W / 2, ym), V2(1, 0));
                }
                if (header) rules.Add(new List<Vector3> { V2(-W / 2 + 0.03f, H / 2 - 0.03f - size, -0.005f), V2(W / 2 - 0.03f, H / 2 - 0.03f - size, -0.005f) });
                float xr = -W / 2 + 0.03f;
                for (int k = 0; k + 1 < ncol; k++)
                {
                    xr += widths[k];
                    rules.Add(new List<Vector3> { V2(xr, H / 2 - 0.06f, -0.005f), V2(xr, -H / 2 + 0.06f, -0.005f) });
                }
                StrokeIn(e, box, Draw.Hex("#3b4a5e"), 0.025f, "panel").Set(rules);
            });
            redraw();
            e.SetText = t => { rowsRaw = t.Split('\n').ToList(); redraw(); };
            e.SetFocus = f => { focus = f; redraw(); };
            LabelFor(e, c, V2(0, H / 2 + 0.3f));
        }

        // ── wave: a digital timing diagram ──

        void BuildWave(Entity e, Cmd c)
        {
            var rows = Panels.ParseWave(c.List("rows").Select(x => x as string ?? ""));
            float step = c.Flt("step", 0.5f), h = c.Flt("size", 0.45f), gap = h * 0.9f, t = Mathf.Min(0.08f, step * 0.2f);
            e.Obj.localPosition = e.ToParent(c["at"]);
            int nsteps = rows.Max(r => r.Steps.Count);
            float W = nsteps * step, rowH = h + gap, Htot = rows.Count * rowH;
            float nameW = rows.Max(r => r.Name.Length) * Mathf.Max(0.32f, h * 0.62f) * 0.55f + 0.3f;
            float x0 = -(W + nameW) / 2 + nameW, yTop = Htot / 2;
            var polys = new List<List<Vector3>>();
            var hatch = new List<List<Vector3>>();
            e.Pins = new Dictionary<string, (Vector3, Vector3)>();
            for (int ri = 0; ri < rows.Count; ri++)
            {
                var row = rows[ri];
                float ym = yTop - ri * rowH - rowH / 2, hi = ym + h / 2, lo = ym - h / 2;
                float Lvl(char v) => v == '1' ? hi : v == '0' ? lo : ym;
                TextAt(e, e.Obj, row.Name, V2(x0 - 0.25f, ym, -0.01f), Mathf.Max(0.32f, h * 0.62f), Draw.FG, "right");
                var pin = Panels.WavePin(row.Name);
                if (pin != null && !e.Pins.ContainsKey(pin)) e.Pins[pin] = (V2(x0 + row.Steps.Count * step, ym), V2(1, 0));
                List<Vector3> pen = null;
                void End() { if (pen != null && pen.Count > 1) polys.Add(pen); pen = null; }
                for (int j = 0; j < row.Steps.Count; j++)
                {
                    var s = row.Steps[j];
                    float xa = x0 + j * step, xb = xa + step;
                    if (s.V == '0' || s.V == '1' || s.V == 'z')
                    {
                        float y = Lvl(s.V);
                        if (pen == null) pen = new List<Vector3> { V2(xa, y) };
                        else if (pen[pen.Count - 1].y != y) pen.Add(V2(xa, y));
                        pen.Add(V2(xb, y));
                    }
                    else if (s.V == 'p' || s.V == 'n')
                    {
                        float a = s.V == 'p' ? hi : lo, b = s.V == 'p' ? lo : hi, xm = (xa + xb) / 2;
                        if (pen == null) pen = new List<Vector3> { V2(xa, b) };
                        pen.Add(V2(xa, a)); pen.Add(V2(xm, a)); pen.Add(V2(xm, b)); pen.Add(V2(xb, b));
                    }
                    else
                    {
                        End();
                        int k = j;
                        while (k + 1 < row.Steps.Count && row.Steps[k + 1].V == s.V && !row.Steps[k + 1].Start) k++;
                        float xe = x0 + (k + 1) * step;
                        polys.Add(new List<Vector3> { V2(xa, ym), V2(xa + t, hi), V2(xe - t, hi), V2(xe, ym) });
                        polys.Add(new List<Vector3> { V2(xa, ym), V2(xa + t, lo), V2(xe - t, lo), V2(xe, ym) });
                        if (s.V == 'x') for (float x = xa + t + 0.12f; x < xe - t; x += 0.16f) hatch.Add(new List<Vector3> { V2(x - 0.1f, lo), V2(x + 0.04f, hi) });
                        if (!string.IsNullOrEmpty(s.Label))
                        {
                            var tp = TextAt(e, e.Obj, s.Label, V2((xa + xe) / 2, ym, -0.01f), h * 0.55f, Draw.Hex("#e6edf3"));
                            float maxW = xe - xa - 2 * t - 0.04f, tw = Draw.TextSize(tp).x;
                            if (tw > maxW) tp.transform.localScale *= Mathf.Max(0.4f, maxW / tw); // shrink to fit its run
                        }
                        j = k;
                    }
                }
                End();
            }
            var grid = new List<List<Vector3>>();
            for (int j = 0; j <= nsteps; j++) grid.Add(new List<Vector3> { V2(x0 + j * step, yTop, 0.01f), V2(x0 + j * step, yTop - Htot, 0.01f) });
            StrokeIn(e, e.Body, Draw.FG, 0.012f, "label", baseAlpha: 0.12f).Set(grid);
            var stroke = StrokeIn(e, e.Body, Draw.Hex(c.Str("color") ?? "#7ee787"), 0.035f);
            var hatchStroke = StrokeIn(e, e.Body, Draw.Hex("#ef5350"), 0.02f);
            List<List<Vector3>> ClipX(List<List<Vector3>> pls, float xmax)
            {
                var outp = new List<List<Vector3>>();
                foreach (var pl in pls)
                {
                    if (pl[0].x > xmax) continue;
                    var o = new List<Vector3> { pl[0] };
                    for (int i = 1; i < pl.Count; i++)
                    {
                        Vector3 a = pl[i - 1], b = pl[i];
                        if (b.x <= xmax + 1e-6f) { o.Add(b); continue; }
                        if (a.x < xmax) o.Add(Vector3.Lerp(a, b, (xmax - a.x) / (b.x - a.x)));
                        break;
                    }
                    if (o.Count > 1) outp.Add(o);
                }
                return outp;
            }
            e.Render = p =>
            {
                float xmax = x0 + W * p;
                stroke.Set(ClipX(polys, xmax));
                hatchStroke.Set(ClipX(hatch, xmax));
            };
            LabelFor(e, c, V2(0, Htot / 2 + 0.3f));
        }

        // ── img: a picture from the server ──

        void BuildImg(Entity e, Cmd c) => ShowImage(e, c, c.Str("url"), c.Flt("size", 4), "img");

        /// <summary>A figure drawn by code on the server (`fig`): its PNG (this client
        /// draws only a subset of SVG; matplotlib's needs more).</summary>
        void BuildFig(Entity e, Cmd c)
        {
            float w = c.Flt("size", 8);
            ShowImage(e, c, $"figs/{c.Str("key")}.png", w, "fig");
            // Where its (first) axes are, so things can sit on it (`on=f`) in its data coordinates.
            LoadBytes?.Invoke($"figs/{c.Str("key")}.json", bytes =>
            {
                if (bytes == null || !e.Obj) return;
                try
                {
                    var meta = (Dictionary<string, object>)Json.Parse(System.Text.Encoding.UTF8.GetString(bytes));
                    var axes = (List<object>)meta["axes"];
                    if (axes.Count == 0) return;
                    var A = (Dictionary<string, object>)axes[0];
                    var size = (List<object>)meta["size"];
                    float h = w * (float)((double)size[1] / (double)size[0]);
                    double[] D(string k) => ((List<object>)A[k]).Select(o => (double)o).ToArray();
                    var box = D("box");
                    double[] xr = D("x"), yr = D("y");
                    bool xlog = A.TryGetValue("xlog", out var xl) && xl is bool xb && xb, ylog = A.TryGetValue("ylog", out var yl) && yl is bool yb && yb;
                    float F(double v, double[] r, bool log) => (float)(log ? (Math.Log10(v) - Math.Log10(r[0])) / (Math.Log10(r[1]) - Math.Log10(r[0])) : (v - r[0]) / (r[1] - r[0]));
                    e.AxesSelf = new AxesInfo
                    {
                        Is3 = false, X = xr, Y = yr, Z = new double[] { 0, 1 },
                        Map = v => new Vector3(
                            -w / 2 + (float)(box[0] + F(v.x, xr, xlog) * (box[2] - box[0])) * w,
                            -h / 2 + (float)(box[1] + F(v.y, yr, ylog) * (box[3] - box[1])) * h,
                            -0.02f - v.z),
                    };
                    var waiting = e.Waiting;
                    e.Waiting = null;
                    if (waiting != null) foreach (var run in waiting) run();
                }
                catch (Exception ex) { Log("warning", $"line {c.Line}: fig: where its axes are: {ex.Message}"); }
            });
        }

        void ShowImage(Entity e, Cmd c, string url, float w, string op)
        {
            e.Obj.localPosition = e.ToParent(c["at"]);
            var mat = e.Track(Draw.Unlit(Color.white, 3001), "solid");
            var q = Draw.QuadObj(e.Body, mat, 1);
            q.localScale = new Vector3(w, w * 0.6f, 1);
            if (op == "fig") q.gameObject.SetActive(false); // nothing to show until it has rendered
            if (LoadTexture == null) { Log("warning", $"line {c.Line}: {op}: no image loader"); return; }
            LoadTexture(url, tex =>
            {
                if (!q) return;
                if (tex == null) { Log("warning", $"line {c.Line}: {op}: could not load {url}"); return; }
                q.gameObject.SetActive(true);
                mat.mainTexture = tex;
                q.localScale = new Vector3(w, w * tex.height / tex.width, 1);
            });
        }
    }
}
