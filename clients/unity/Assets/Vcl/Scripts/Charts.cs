// Charts on axes (bars, hist, scatter, errbar, band, contour) and structured
// diagrams (nn, grid, bits): a port of viewer/js/charts.js. The parser has
// already turned their data strings into arrays and listed their pins
// (docs/IR.md). Diagrams expose Parts that hl/color/fade can target.
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Vcl
{
    public partial class Runtime
    {
        static readonly Dictionary<string, string[]> Cmaps = new Dictionary<string, string[]>
        {
            ["gray"] = new[] { "#101418", "#f5f5f5" },
            ["heat"] = new[] { "#440154", "#3b528b", "#21918c", "#5ec962", "#fde725" }, // viridis
            ["div"] = new[] { "#2166ac", "#f7f7f7", "#b2182b" },
            ["levels"] = new[] { "#3949ab", "#26a69a", "#ffee58" }, // as surf
        };

        static Color Cmap(string name, float t)
        {
            var stops = Cmaps.TryGetValue(name, out var s) ? s : Cmaps["gray"];
            float x = Mathf.Clamp01(t) * (stops.Length - 1);
            int i = Mathf.Min(stops.Length - 2, Mathf.FloorToInt(x));
            return Color.Lerp(Draw.Hex(stops[i]), Draw.Hex(stops[i + 1]), x - i);
        }

        /// <summary>Short number for a cell: 3, 0.25, 12.5, 1.2e+3.</summary>
        static string Fmt(double v)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (v == Math.Floor(v) && Math.Abs(v) < 1e15) return ((long)v).ToString(inv);
            double a = Math.Abs(v);
            if (a >= 1e4 || (a > 0 && a < 1e-3)) return v.ToString("0.0e+0", inv);
            return Math.Round(v, a >= 100 ? 0 : a >= 10 ? 1 : 2).ToString("0.##", inv);
        }

        static List<string> Csv(string s) => s == null ? new List<string>() : s.Split(',').Select(x => x.Trim()).ToList();
        static double D(object o) => o is double d ? d : 0;

        Part PartOf(Entity e, string name, IEnumerable<Tint> tints, IEnumerable<Transform> objs = null, Action apply = null)
        {
            e.Parts ??= new Dictionary<string, Part>();
            var p = new Part { Tints = tints.ToList(), Objs = objs?.ToList() ?? new List<Transform>(), Apply = apply };
            e.Parts[name] = p;
            return p;
        }

        // ── nn: layers of neurons, fully connected ──

        void BuildNn(Entity e, Cmd c)
        {
            var sizes = c.List("sizes").Select(x => (int)D(x)).ToList();
            var shown = c.List("shown").Select(l => ((List<object>)l).Select(x => (int)D(x)).ToList()).ToList();
            int L = sizes.Count;
            var sz = c.Has("size") ? Raw(c["size"]) : new Vector3(Mathf.Max(2, 2.6f * (L - 1)), 4, 0);
            float W = sz.x, H = sz.y;
            e.Obj.localPosition = e.ToParent(c["at"]);
            var collapsed = sizes.Select((n, i) => shown[i].Count < n).ToList();
            var slots = shown.Select((s, i) => s.Count + (collapsed[i] ? 1 : 0)).ToList();
            int maxSlots = slots.Max();
            float d = maxSlots > 1 ? Mathf.Min(0.95f, H / (maxSlots - 1)) : 1;
            float r = Mathf.Min(0.3f, d * 0.34f);
            Color color = Draw.Hex(c.Str("color") ?? "#7fa7d9"), errColor = Draw.Hex("#ef9a9a"), edgeColor = Draw.Hex("#6b7a8f");
            var labels = Csv(c.Str("labels"));
            float XOf(int i) => L > 1 ? -W / 2 + i * W / (L - 1) : 0;
            bool pairs = c.Bool("pairs"), feedbackOn = c.Bool("feedback"), edges = c.Str("edges") != "none";
            e.Parts = new Dictionary<string, Part>();
            e.Pins = new Dictionary<string, (Vector3, Vector3)>();
            var fillMesh = Draw.Disc(1, 40);
            var ringMesh = Draw.Ring(0.8f, 1, 40);

            (Transform obj, List<Tint> tints, Vector3 at) Neuron(float x, float y, float rad, Color col)
            {
                var g = Draw.Child(e.Body, "neuron").transform;
                g.localPosition = V2(x, y, -0.01f);
                var fm = Draw.Unlit(Draw.Hex("#1b2635"), 3002);
                var rm = Draw.Unlit(col, 3002);
                var f = Draw.MeshObject(g, "disc", fillMesh, fm, 3).transform;
                var rr = Draw.MeshObject(g, "ring", ringMesh, rm, 4).transform;
                f.localScale = rr.localScale = Vector3.one * rad;
                rr.localPosition = V2(0, 0, -0.001f);
                var tints = new List<Tint> { e.TrackT(rm, "stroke"), e.TrackT(fm, "fill", 0.95f) };
                return (g, tints, V2(x, y));
            }

            var layers = new List<(List<(Transform obj, List<Tint> tints, Vector3 at)> vals, List<(Transform obj, List<Tint> tints, Vector3 at)> errs, List<Transform> objs)>();
            float yLow = -((maxSlots - 1) * d) / 2 - r;
            for (int i = 0; i < L; i++)
            {
                float x = XOf(i);
                var s = shown[i];
                float y0 = (slots[i] - 1) * d / 2;
                int top = 0;
                while (top < s.Count && s[top] == top + 1) top++;
                var vals = new List<(Transform, List<Tint>, Vector3)>();
                var errs = new List<(Transform, List<Tint>, Vector3)>();
                var objs = new List<Transform>();
                int slot = 0;
                for (int k = 0; k < s.Count; k++)
                {
                    int j = s[k];
                    if (collapsed[i] && k == top)
                    {
                        var dots = Draw.Child(e.Body, "dots").transform; // ⋮
                        dots.localPosition = V2(x, y0 - slot * d, -0.01f);
                        var dm = e.Track(Draw.Unlit(Draw.Hex("#aab6c5"), 3002), "stroke");
                        foreach (int dy in new[] { -1, 0, 1 })
                        {
                            var m = Draw.MeshObject(dots, "dot", Draw.Disc(Mathf.Max(0.025f, r * 0.13f)), dm, 3).transform;
                            m.localPosition = V2(0, dy * Mathf.Min(d * 0.28f, 0.2f));
                        }
                        objs.Add(dots);
                        slot++;
                    }
                    float y = y0 - slot * d;
                    slot++;
                    var v = Neuron(x, y, r, color);
                    vals.Add(v);
                    objs.Add(v.obj);
                    PartOf(e, $"L{i}_{j}", v.tints, new[] { v.obj });
                    e.Pins[$"L{i}_{j}"] = (v.at, V2(1, 0));
                    if (pairs)
                    {
                        var er = Neuron(x + r * 1.45f, y - r * 0.75f, r * 0.48f, errColor);
                        errs.Add(er);
                        objs.Add(er.obj);
                        PartOf(e, $"L{i}_{j}e", er.tints, new[] { er.obj });
                        e.Pins[$"L{i}_{j}e"] = (er.at, V2(1, 0));
                    }
                }
                var all = vals.Concat(errs).ToList();
                var part = PartOf(e, $"L{i}", all.SelectMany(n => n.Item2), all.Select(n => n.Item1));
                e.Parts[$"L{i}_top"] = e.Parts[$"L{i}_bottom"] = part;
                e.Pins[$"L{i}"] = (V2(x, 0), V2(0, 1));
                e.Pins[$"L{i}_top"] = (V2(x, y0 + r), V2(0, 1));
                e.Pins[$"L{i}_bottom"] = (V2(x, -y0 - r), V2(0, -1));
                string lab = i < labels.Count ? labels[i] : "";
                if (collapsed[i]) lab = lab.Length > 0 ? $"{lab} ({sizes[i]})" : sizes[i].ToString();
                if (lab.Length > 0) TextAt(e, e.Obj, lab, V2(x, yLow - 0.38f, -0.01f), 0.32f, Draw.FG);
                layers.Add((vals, errs, objs));
            }

            List<Vector3> Link(Vector3 a, Vector3 b, float ra, float rb)
            {
                var dir = (b - a).normalized;
                return new List<Vector3> { a + dir * ra, b - dir * rb };
            }
            var forward = new List<(Stroke s, List<List<Vector3>> polys, int i)>();
            var feedback = new List<(Stroke s, List<List<Vector3>> polys)>();
            for (int i = 1; i < L; i++)
            {
                if (edges)
                {
                    var s = new Stroke(e.Body, edgeColor, 0.022f) { Order = 1 };
                    e.Strokes.Add(s);
                    var tint = e.TrackT(s.Material, "stroke", 0.75f);
                    var polys = new List<List<Vector3>>();
                    foreach (var a in layers[i - 1].vals) foreach (var b in layers[i].vals) polys.Add(Link(a.at, b.at, r, r));
                    forward.Add((s, polys, i));
                    PartOf(e, $"E{i}", new[] { tint });
                    e.Pins[$"E{i}"] = (V2((XOf(i - 1) + XOf(i)) / 2, 0), V2(0, 1));
                }
                if (feedbackOn)
                {
                    var s = new Stroke(e.Body, errColor, 0.02f, true) { Order = 1 };
                    e.Strokes.Add(s);
                    var tint = e.TrackT(s.Material, "stroke", 0.5f);
                    var targets = pairs ? layers[i - 1].errs : layers[i - 1].vals;
                    float rt = pairs ? r * 0.48f : r;
                    var polys = new List<List<Vector3>>();
                    foreach (var a in layers[i].vals)
                        foreach (var b in targets)
                        {
                            var ln = Link(a.at, b.at, r, rt);
                            Vector3 p0 = ln[0], p1 = ln[1];
                            var mid = Vector3.Lerp(p0, p1, 0.5f) + V2(0, -0.12f * Vector3.Distance(p0, p1));
                            var curve = new List<Vector3>();
                            for (int q = 0; q <= 12; q++)
                            {
                                float t = q / 12f, u = 1 - t;
                                curve.Add(u * u * p0 + 2 * u * t * mid + t * t * p1);
                            }
                            polys.Add(curve);
                        }
                    feedback.Add((s, polys));
                    PartOf(e, $"F{i}", new[] { tint });
                    e.Pins[$"F{i}"] = (V2((XOf(i - 1) + XOf(i)) / 2, -0.3f), V2(0, -1));
                }
            }

            e.Render = p =>
            {
                float k = p * L;
                for (int i = 0; i < layers.Count; i++)
                {
                    float a = Mathf.Clamp01((k - i) * 1.6f);
                    foreach (var o in layers[i].objs) o.localScale = Vector3.one * Mathf.Max(1e-3f, a);
                }
                foreach (var (s, polys, i) in forward)
                {
                    float f = Mathf.Clamp01(k - i + 0.4f);
                    s.Set(f > 0 ? polys.Select(pl => new List<Vector3> { pl[0], Vector3.Lerp(pl[0], pl[1], f) }).ToList() : new List<List<Vector3>>());
                }
                float fb = Mathf.Clamp01((p - 0.7f) / 0.3f);
                foreach (var (s, polys) in feedback)
                    s.Set(fb > 0 ? polys.Select(pl => Draw.Partial(new List<List<Vector3>> { pl }, fb)).Where(x => x.Count > 0).Select(x => x[0]).ToList() : new List<List<Vector3>>());
            };
            LabelFor(e, c, V2(0, (maxSlots - 1) * d / 2 + r + 0.4f));
        }

        // ── grid: a heatmap; each cell is a part ──

        void BuildGrid(Entity e, Cmd c)
        {
            var dims = c.List("dims");
            int R = (int)Math.Round(D(dims[0])), C = (int)Math.Round(D(dims[1]));
            float cell0 = Mathf.Min(0.45f, Mathf.Min(8f / C, 6f / R));
            var sz = c.Has("size") ? Raw(c["size"]) : new Vector3(C * cell0, R * cell0, 0);
            float W = sz.x, H = sz.y;
            e.Obj.localPosition = e.ToParent(c["at"]);
            // One texel per cell, point-filtered; a 1-texel gap between big cells.
            int px = Mathf.Clamp(Mathf.FloorToInt(1024f / Mathf.Max(R, C)), 1, 32);
            bool gap = px >= 8;
            var tex = new Texture2D(C * px, R * px, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var mat = e.Track(Draw.Unlit(Color.white, 3001), "fill");
            mat.mainTexture = tex;
            var q = Draw.QuadObj(e.Body, mat, 1);
            q.localScale = new Vector3(W, H, 1);
            StrokeIn(e, e.Body, Draw.Hex("#5d6b7e"), 0.025f).Set(new List<List<Vector3>> { Rect(-W / 2, -H / 2, W / 2, H / 2, -0.002f) });

            var vals = c.List("cells").Select(D).ToList();
            // Cells are tints outside the entity's own list: hl/color/fade change them, then redraw.
            var cells = vals.Select(_ => new Tint { Role = "cell", BaseAlpha = 1, Apply = _ => { } }).ToList();
            bool showText = c.Bool("text");
            var labels = new List<TextMeshPro>();
            var labelTints = new List<Tint>();
            float cw = W / C, ch = H / R;
            if (showText)
                for (int k = 0; k < vals.Count; k++)
                {
                    labels.Add(TextAt(e, e.Body, "", V2(-W / 2 + (k % C + 0.5f) * cw, H / 2 - (k / C + 0.5f) * ch, -0.01f), Mathf.Min(cw, ch) * 0.42f, Color.white, role: "text", bold: true));
                    labelTints.Add(e.Tints[e.Tints.Count - 1]);
                }
            void Recolor()
            {
                double lo = vals.Min(), hi = vals.Max();
                string name = c.Str("cmap") ?? (lo < 0 ? "div" : "gray");
                var range = c.Range("range");
                if (range != null) { lo = range[0]; hi = range[1]; }
                else if (name == "div") { double m = Math.Max(Math.Abs(lo), Math.Abs(hi)); if (m == 0) m = 1; lo = -m; hi = m; }
                double span = hi - lo == 0 ? 1 : hi - lo;
                for (int k = 0; k < vals.Count; k++) cells[k].Color = Cmap(name, (float)((vals[k] - lo) / span));
            }
            var bg = Draw.Hex("#0b0f16");
            var pixels = new Color32[tex.width * tex.height];
            void Paint()
            {
                for (int k = 0; k < vals.Count; k++)
                {
                    var col = Color.Lerp(bg, cells[k].Color, cells[k].Dim);
                    int cx = k % C, cy = R - 1 - k / C; // texture rows go up
                    for (int yy = 0; yy < px; yy++)
                        for (int xx = 0; xx < px; xx++)
                        {
                            bool edge = gap && (xx == 0 || yy == 0);
                            pixels[(cy * px + yy) * tex.width + cx * px + xx] = edge ? (Color32)bg : (Color32)col;
                        }
                    if (showText)
                    {
                        var lc = cells[k].Color;
                        float lum = 0.2126f * lc.linear.r + 0.7152f * lc.linear.g + 0.0722f * lc.linear.b;
                        labels[k].text = Fmt(vals[k]);
                        labelTints[k].Color = lum > 0.22f ? Draw.Hex("#111111") : Draw.Hex("#f5f5f5");
                    }
                }
                tex.SetPixels32(pixels);
                tex.Apply(false);
                if (showText) e.ApplyOpacity();
            }
            Recolor();
            Paint();

            e.Parts = new Dictionary<string, Part>();
            e.Pins = new Dictionary<string, (Vector3, Vector3)>();
            for (int i = 1; i <= R; i++)
            {
                PartOf(e, $"r{i}", cells.Skip((i - 1) * C).Take(C), null, Paint);
                e.Pins[$"r{i}"] = (V2(-W / 2, H / 2 - (i - 0.5f) * ch), V2(-1, 0));
            }
            for (int j = 1; j <= C; j++)
            {
                PartOf(e, $"c{j}", cells.Where((_, k) => k % C == j - 1), null, Paint);
                e.Pins[$"c{j}"] = (V2(-W / 2 + (j - 0.5f) * cw, H / 2), V2(0, 1));
            }
            if (R * C <= 4096)
                for (int i = 1; i <= R; i++)
                    for (int j = 1; j <= C; j++)
                    {
                        PartOf(e, $"r{i}c{j}", new[] { cells[(i - 1) * C + j - 1] }, null, Paint);
                        e.Pins[$"r{i}c{j}"] = (V2(-W / 2 + (j - 0.5f) * cw, H / 2 - (i - 0.5f) * ch), V2(0, 1));
                    }
            // set g text="…": new values, same shape.
            e.SetText = t =>
            {
                var next = t.Split(new[] { ' ', ',', ';', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToList();
                if (next.Count != vals.Count || next.Any(double.IsNaN)) { Log("warning", $"grid '{e.Id}': set text= needs {vals.Count} numbers"); return; }
                vals = next;
                Recolor();
                Paint();
            };
            LabelFor(e, c, V2(0, H / 2 + 0.3f));
        }

        // ── bits: a register / fixed-point format ──

        void BuildBits(Entity e, Cmd c)
        {
            var groups = c.Str("pattern").Split('|').Select(s => s.Replace(" ", "")).ToList();
            var widths = c.List("widths")?.Select(x => (int)D(x)).ToList() ?? groups.Select(g => g.Length).ToList();
            int n = widths.Sum();
            float cell = c.Flt("size", Mathf.Min(0.55f, 14f / Mathf.Max(n, 1)));
            e.Obj.localPosition = e.ToParent(c["at"]);
            float W = n * cell, x0 = -W / 2, yb = -0.3f * cell, yt = yb + cell;
            var labels = Csv(c.Str("labels"));
            var pattern = string.Concat(groups);
            float lw = Mathf.Min(0.035f, cell * 0.07f);
            e.Parts = new Dictionary<string, Part>();
            e.Pins = new Dictionary<string, (Vector3, Vector3)>();
            var digits = new List<(TextMeshPro t, Tint tint)>();
            int i = 0;
            for (int f = 0; f < widths.Count; f++)
            {
                int w = widths[f];
                var col = Draw.Hex(c.Str("color") ?? Palette[f % Palette.Length]);
                var fieldTints = new List<Tint>();
                float fx0 = x0 + i * cell;
                for (int k = 0; k < w; k++, i++)
                {
                    int bit = n - 1 - i;
                    float cx = x0 + (i + 0.5f) * cell, cy = (yb + yt) / 2;
                    var grp = Draw.Child(e.Body, $"b{bit}").transform;
                    grp.localPosition = V2(cx, cy);
                    var fm = Draw.Unlit(col, 3001);
                    var q = Draw.QuadObj(grp, fm, 1);
                    Draw.SetQuad(q, V2(-cell / 2, -cell / 2), V2(cell / 2, cell / 2));
                    var s = new Stroke(grp, col, lw);
                    e.Strokes.Add(s);
                    float hh = cell / 2;
                    s.Set(new List<List<Vector3>> { Rect(-hh, -hh, hh, hh, -0.002f) });
                    var dg = Draw.Text(grp, " ", cell * 0.62f, Draw.FG, "center", true);
                    dg.transform.localPosition = V2(0, 0, -0.01f);
                    digits.Add((dg, e.TrackT(dg, "text")));
                    if (c.Bool("index", true)) TextAt(e, e.Body, bit.ToString(), V2(cx, yt + cell * 0.32f, -0.01f), Mathf.Max(0.16f, cell * 0.36f), Draw.Hex("#8a98ab"));
                    var tints = new List<Tint> { e.TrackT(s.Material, "stroke"), e.TrackT(fm, "fill", 0.22f) };
                    fieldTints.AddRange(tints);
                    PartOf(e, $"b{bit}", tints, new[] { grp });
                    e.Pins[$"b{bit}"] = (V2(cx, yt + cell * 0.6f), V2(0, 1));
                }
                float fx1 = x0 + i * cell, xm = (fx0 + fx1) / 2;
                float by = yb - 0.08f * cell, bd = Mathf.Max(0.12f, 0.3f * cell);
                var br = new Stroke(e.Body, col, lw * 0.9f);
                e.Strokes.Add(br);
                var brTint = e.TrackT(br.Material, "label");
                float inset = Mathf.Min(0.06f, (fx1 - fx0) / 6);
                br.Set(new List<List<Vector3>> { new List<Vector3> { V2(fx0 + inset, by), V2(fx0 + inset, by - bd * 0.45f), V2(xm - bd * 0.4f, by - bd * 0.45f), V2(xm, by - bd),
                    V2(xm + bd * 0.4f, by - bd * 0.45f), V2(fx1 - inset, by - bd * 0.45f), V2(fx1 - inset, by) } });
                float ly = by - bd - Mathf.Max(0.2f, cell * 0.42f);
                if (f < labels.Count && labels[f].Length > 0) TextAt(e, e.Body, labels[f], V2(xm, ly, -0.01f), Mathf.Max(0.26f, cell * 0.5f), col);
                var part = PartOf(e, $"F{f + 1}", fieldTints.Append(brTint));
                var pin = (V2(xm, ly - Mathf.Max(0.2f, cell * 0.4f)), V2(0, -1));
                e.Pins[$"F{f + 1}"] = pin;
                var named = f < labels.Count && labels[f].Length > 0 ? Panels.WavePin(labels[f]) : null;
                if (named != null && !e.Pins.ContainsKey(named)) { e.Parts[named] = part; e.Pins[named] = pin; }
            }
            // Values in the boxes; without them, the pattern's letters, dimmed.
            e.SetText = t =>
            {
                var v = (t ?? "").Replace("|", "").Replace(" ", "");
                for (int k = 0; k < digits.Count; k++)
                {
                    digits[k].t.text = k < v.Length ? v[k].ToString() : k < pattern.Length ? pattern[k].ToString() : " ";
                    digits[k].tint.BaseAlpha = k < v.Length ? 1 : 0.45f;
                }
                e.ApplyOpacity();
            };
            e.SetText(c.Str("values") ?? "");
            LabelFor(e, c, V2(0, yt + cell * 0.95f));
        }

        // ── bars and histograms on axes ──

        void BarsEntity(Entity e, Cmd c, float? barW)
        {
            var A = AxesOf(e, c);
            var bars = c.List("bars").Select(b => (List<object>)b).Select(b => (x: (float)D(b[0]), ys: ((List<object>)b[1]).Select(y => (float)D(y)).ToList())).ToList();
            var xs = bars.Select(b => b.x).Distinct().OrderBy(x => x).ToList();
            float step = float.PositiveInfinity;
            for (int i = 1; i < xs.Count; i++) step = Mathf.Min(step, xs[i] - xs[i - 1]);
            if (float.IsInfinity(step)) step = 1;
            float w = barW ?? c.Flt("width", 0.6f * step);
            int depth = bars.Max(b => b.ys.Count);
            var single = c.Str("color") ?? Palette[A.Plots++ % Palette.Length];
            var colors = Enumerable.Range(0, depth).Select(k => Draw.Hex(depth > 1 ? Palette[k % Palette.Length] : single)).ToList();
            // Values count from 0; what lies outside the y range is clipped.
            float ClampY(float v) => Mathf.Clamp(v, (float)A.Y[0], (float)A.Y[1]);
            float baseV = ClampY(0);
            var segs = new List<(Transform q, Vector3 a, Vector3 b)>();
            e.Parts = new Dictionary<string, Part>();
            e.Pins = new Dictionary<string, (Vector3, Vector3)>();
            for (int i = 0; i < bars.Count; i++)
            {
                var (x, ys) = bars[i];
                float cum = 0;
                var tints = new List<Tint>();
                for (int k = 0; k < ys.Count; k++)
                {
                    var m = Draw.Unlit(colors[k], 3001);
                    tints.Add(e.TrackT(m, "solid", 0.82f));
                    var q = Draw.QuadObj(e.Body, m, 1);
                    segs.Add((q, A.Map(V2(x - w / 2, ClampY(cum))), A.Map(V2(x + w / 2, ClampY(cum + ys[k])))));
                    cum += ys[k];
                }
                PartOf(e, $"B{i + 1}", tints);
                bool up = cum >= 0;
                e.Pins[$"B{i + 1}"] = (A.Map(V2(x, ClampY(cum))) + V2(0, up ? 0.08f : -0.08f), V2(0, up ? 1 : -1));
            }
            float y0 = A.Map(V2((float)A.X[0], baseV)).y;
            e.Render = p =>
            {
                foreach (var (q, a, b) in segs)
                    Draw.SetQuad(q, V2(a.x, y0 + (a.y - y0) * p, a.z), V2(b.x, y0 + (b.y - y0) * p, b.z));
            };
            var labels = Csv(c.Str("labels"));
            for (int i = 0; i < labels.Count && i < bars.Count; i++)
                TextAt(e, e.Obj, labels[i], A.Map(V2(bars[i].x, (float)A.Y[0])) + V2(0, -0.34f, -0.01f), 0.28f, Draw.FG);
            var series = Csv(c.Str("series"));
            if (series.Count > 0)
            {
                var corner = A.Map(V2((float)A.X[1], (float)A.Y[1]));
                for (int k = 0; k < series.Count; k++)
                {
                    float y = corner.y - 0.15f - k * 0.42f, x = corner.x + 0.45f;
                    var q = Draw.QuadObj(e.Obj, e.Track(Draw.Unlit(colors[k % colors.Count], 3001), "label", 0.82f), 1);
                    Draw.SetQuad(q, V2(x, y - 0.12f), V2(x + 0.24f, y + 0.12f));
                    TextAt(e, e.Obj, series[k], V2(x + 0.36f, y, -0.01f), 0.28f, Draw.FG, "left");
                }
            }
        }

        // ── scatter, error bars, bands ──

        void BuildScatter(Entity e, Cmd c)
        {
            var A = AxesOf(e, c);
            var color = Draw.Hex(c.Str("color") ?? Palette[A.Plots++ % Palette.Length]);
            float r = c.Flt("size", 0.09f);
            var mat = e.Track(Draw.Unlit(color, 3002), "stroke");
            var mesh = A.Is3 ? Draw.Primitive(PrimitiveType.Sphere) : Draw.Disc(r, 20);
            e.Pins = new Dictionary<string, (Vector3, Vector3)>();
            var pts = c.List("pts").Select(p => (List<object>)p).ToList();
            var dots = new List<Transform>();
            for (int i = 0; i < pts.Count; i++)
            {
                var pt = pts[i];
                var m = Draw.MeshObject(e.Body, "dot", mesh, mat, 2).transform;
                m.localPosition = A.Map(new Vector3((float)D(pt[0]), (float)D(pt[1]), pt.Count > 2 ? (float)D(pt[2]) : 0)) + (A.Is3 ? Vector3.zero : V2(0, 0, -0.01f));
                if (A.Is3) m.localScale = Vector3.one * 2 * r;
                e.Pins[$"P{i + 1}"] = (m.localPosition, V2(0, 1));
                dots.Add(m);
            }
            var full = dots.Select(m => m.localScale).ToList();
            e.Render = p =>
            {
                for (int i = 0; i < dots.Count; i++) dots[i].localScale = full[i] * Mathf.Max(1e-3f, Mathf.Clamp01(p * dots.Count * 1.5f - i));
            };
            if (c.Has("label") && dots.Count > 0)
            {
                e.AddLabel(c.Str("label"), Vector3.zero, "left");
                e.Label.transform.localPosition = dots[dots.Count - 1].localPosition + V2(0.2f, 0.2f);
            }
        }

        void BuildErrbar(Entity e, Cmd c)
        {
            var A = AxesOf(e, c);
            var color = Draw.Hex(c.Str("color") ?? Palette[A.Plots++ % Palette.Length]);
            float cap = c.Flt("cap", 0.18f) / 2, r = c.Flt("size", 0.08f);
            bool horiz = c.Bool("horiz");
            var s = StrokeIn(e, e.Body, color, c.Flt("width", 0.04f));
            var dotMat = e.Track(Draw.Unlit(color, 3002), "stroke");
            var items = new List<(List<List<Vector3>> polys, Vector3 mid, Transform dot)>();
            e.Pins = new Dictionary<string, (Vector3, Vector3)>();
            var bars = c.List("bars").Select(b => (List<object>)b).ToList();
            for (int i = 0; i < bars.Count; i++)
            {
                var b = bars[i];
                float x = (float)D(b[0]), lo = (float)D(b[2]), hi = (float)D(b[3]);
                bool hasY = b[1] is double;
                Vector3 At(float u, float v) => A.Map(horiz ? V2(v, u) : V2(u, v));
                Vector3 pa = At(x, lo), pb = At(x, hi);
                var t = horiz ? V2(0, cap) : V2(cap, 0);
                var mid = hasY ? At(x, (float)D(b[1])) : Vector3.Lerp(pa, pb, 0.5f);
                var polys = new List<List<Vector3>> { new List<Vector3> { pa, pb }, new List<Vector3> { pa - t, pa + t }, new List<Vector3> { pb - t, pb + t } };
                Transform dot = null;
                if (hasY)
                {
                    dot = Draw.MeshObject(e.Body, "dot", Draw.Disc(r, 20), dotMat, 3).transform;
                    dot.localPosition = mid + V2(0, 0, -0.01f);
                }
                items.Add((polys, mid, dot));
                e.Pins[$"E{i + 1}"] = (mid, horiz ? V2(0, 1) : V2(1, 0));
            }
            e.Render = p =>
            {
                s.Set(items.SelectMany(it => it.polys.Select(pl => pl.Select(v => Vector3.Lerp(it.mid, v, p)).ToList())).ToList());
                foreach (var it in items) if (it.dot) it.dot.localScale = Vector3.one * Mathf.Max(1e-3f, p);
            };
        }

        void BuildBand(Entity e, Cmd c)
        {
            var A = AxesOf(e, c);
            var xr = c.Range("x") ?? A.X;
            var yr = c.Range("y") ?? A.Y;
            float x0 = (float)xr[0], x1 = (float)xr[1], y0 = (float)yr[0], y1 = (float)yr[1];
            var color = Draw.Hex(c.Str("fill") ?? c.Str("color") ?? "#90a4ae");
            var q = Draw.QuadObj(e.Body, e.Track(Draw.Unlit(color, 3000), "fill", 0.2f), 0);
            var a = A.Map(V2(x0, y0)); var b = A.Map(V2(x1, y1));
            a.z = b.z = 0.01f;
            Draw.SetQuad(q, a, b);
            var edges = new List<List<Vector3>>();
            if (c.Has("y")) { edges.Add(new List<Vector3> { A.Map(V2(x0, y0)), A.Map(V2(x1, y0)) }); edges.Add(new List<Vector3> { A.Map(V2(x0, y1)), A.Map(V2(x1, y1)) }); }
            if (c.Has("x")) { edges.Add(new List<Vector3> { A.Map(V2(x0, y0)), A.Map(V2(x0, y1)) }); edges.Add(new List<Vector3> { A.Map(V2(x1, y0)), A.Map(V2(x1, y1)) }); }
            StrokeIn(e, e.Body, color, 0.03f, dash: true, baseAlpha: 0.85f).Set(edges);
            if (c.Has("label"))
            {
                e.AddLabel(c.Str("label"), Vector3.zero, "left");
                e.Label.transform.localPosition = A.Map(V2(x0, y1)) + V2(0.15f, -0.25f, -0.01f);
            }
        }

        // ── contour lines (sampled by the parser) ──

        void BuildContour(Entity e, Cmd c)
        {
            var A = AxesOf(e, c);
            var cont = c.List("contours") ?? throw new Exception("no samples in the IR (the server is older than this client)");
            int n = cont.Count;
            var levels = new List<(Stroke s, List<List<Vector3>> polys)>();
            for (int i = 0; i < n; i++)
            {
                var lv = (Dictionary<string, object>)cont[i];
                var color = c.Has("color") ? Draw.Hex(c.Str("color")) : Cmap("levels", n > 1 ? i / (float)(n - 1) : 0.5f);
                var s = StrokeIn(e, e.Body, color, c.Flt("width", 0.04f));
                var polys = ((List<object>)lv["polys"]).Select(pl => ((List<object>)pl).Select(v => { var xy = (List<object>)v; return A.Map(V2((float)D(xy[0]), (float)D(xy[1]))); }).ToList()).ToList();
                levels.Add((s, polys));
            }
            e.Render = p => { foreach (var (s, polys) in levels) s.Set(Draw.Partial(polys, p)); };
        }
    }
}
