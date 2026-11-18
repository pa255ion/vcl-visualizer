// `svg`: SVG markup (inline or from a URL) drawn as meshes. The web viewer lets
// the browser rasterize it; here the common subset is parsed: path (all
// commands, arcs included), rect, circle, ellipse, line, polyline, polygon and
// text, with fill, stroke, stroke-width, opacity, transform (inherited through
// <g>) and style="…". Fills are triangulated per subpath (holes are not cut);
// shapes keep document order as consecutive submeshes of one mesh.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using TMPro;
using UnityEngine;

namespace Vcl
{
    public partial class Runtime
    {
        void BuildSvg(Entity e, Cmd c)
        {
            float width = c.Flt("size", 4);
            e.Obj.localPosition = e.ToParent(c["at"]);
            var src = c.Str("src") ?? "";
            void Show(string markup)
            {
                var redraw = Rebuilder(e, box =>
                {
                    var r = Svg.Render(box, markup, width);
                    foreach (var m in r.materials) e.Track(m.mat, "solid", m.alpha);
                    foreach (var t in r.texts) e.Track(t, "text");
                    if (e.Label) e.Label.transform.localPosition = new Vector3(0, -r.height / 2 - 0.3f, 0);
                });
                try { redraw(); }
                catch (Exception ex) { Log("warning", $"line {c.Line}: svg: {ex.Message}"); }
            }
            LabelFor(e, c, new Vector3(0, -width / 3, 0));
            if (src.TrimStart().StartsWith("<")) Show(src);
            else if (LoadBytes == null) Log("warning", $"line {c.Line}: svg: no loader for {src}");
            else LoadBytes(src, bytes =>
            {
                if (!e.Obj) return;
                if (bytes == null) { Log("warning", $"line {c.Line}: svg: could not load {src}"); return; }
                Show(System.Text.Encoding.UTF8.GetString(bytes));
            });
        }
    }

    public static class Svg
    {
        public struct Result { public List<(Material mat, float alpha)> materials; public List<TextMeshPro> texts; public float width, height; }

        class Paint
        {
            public Color? Fill = Color.black, Stroke;
            public float StrokeWidth = 1, Opacity = 1, FillOpacity = 1, StrokeOpacity = 1, FontSize = 16;
            public string Anchor = "start", Join = "miter", Cap = "butt";
            public bool Bold;
            public Matrix4x4 M = Matrix4x4.identity;
            public Paint Clone() => (Paint)MemberwiseClone();
        }

        class Ctx
        {
            public List<Vector3> Verts = new List<Vector3>();
            public List<List<int>> Subs = new List<List<int>>();
            public List<(Material, float)> Mats = new List<(Material, float)>();
            public List<TextMeshPro> Texts = new List<TextMeshPro>();
            public Transform Parent;
            public float K; // svg units → board units
            public Vector2 Origin; // svg point at the board origin
            public int Order;
            public Vector3 P(Vector2 p) => new Vector3((p.x - Origin.x) * K, -(p.y - Origin.y) * K, 0);
        }

        public static Result Render(Transform parent, string markup, float width)
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(Regex.Replace(markup, "<!DOCTYPE[^>]*>", ""));
            var svg = doc.DocumentElement;
            if (svg == null || svg.LocalName != "svg") throw new Exception("not an SVG document");
            var vb = (svg.GetAttribute("viewBox") ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(F).ToArray();
            float w = Len(svg.GetAttribute("width"), vb.Length == 4 ? vb[2] : 300), h = Len(svg.GetAttribute("height"), vb.Length == 4 ? vb[3] : 150);
            if (vb.Length != 4) vb = new[] { 0f, 0, w, h };
            float aspect = vb[3] / Mathf.Max(1e-6f, vb[2]);
            var ctx = new Ctx { Parent = parent, K = width / vb[2], Origin = new Vector2(vb[0] + vb[2] / 2, vb[1] + vb[3] / 2) };
            var paint = new Paint();
            Style(svg, paint);
            foreach (XmlNode n in svg.ChildNodes) if (n is XmlElement el) Walk(el, paint, ctx);

            if (ctx.Subs.Count > 0)
            {
                var mesh = new Mesh { name = "svg", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.SetVertices(ctx.Verts);
                mesh.subMeshCount = ctx.Subs.Count;
                for (int i = 0; i < ctx.Subs.Count; i++) mesh.SetTriangles(ctx.Subs[i], i);
                mesh.RecalculateBounds();
                var r = Draw.MeshObject(parent, "svg", mesh, ctx.Mats[0].Item1, 1);
                r.sharedMaterials = ctx.Mats.Select(m => m.Item1).ToArray();
            }
            return new Result { materials = ctx.Mats, texts = ctx.Texts, width = width, height = width * aspect };
        }

        // ── tree ──

        static void Walk(XmlElement el, Paint inherited, Ctx ctx)
        {
            var name = el.LocalName;
            if (name == "defs" || name == "title" || name == "desc" || name == "metadata" || name == "style" || name == "clipPath" || name == "mask" || name == "symbol") return;
            var p = inherited.Clone();
            var tr = el.GetAttribute("transform");
            if (!string.IsNullOrEmpty(tr)) p.M = p.M * Transform(tr);
            Style(el, p);
            if (el.GetAttribute("display") == "none" || el.GetAttribute("visibility") == "hidden") return;
            switch (name)
            {
                case "g": case "a": case "svg":
                    foreach (XmlNode n in el.ChildNodes) if (n is XmlElement ch) Walk(ch, p, ctx);
                    return;
                case "path": Shape(Path(el.GetAttribute("d")), p, ctx); return;
                case "rect":
                {
                    float x = A(el, "x"), y = A(el, "y"), w = A(el, "width"), h = A(el, "height");
                    float rx = A(el, "rx", -1), ry = A(el, "ry", -1);
                    if (rx < 0) rx = ry; if (ry < 0) ry = rx;
                    rx = Mathf.Clamp(rx, 0, w / 2); ry = Mathf.Clamp(ry, 0, h / 2);
                    var pts = new List<Vector2>();
                    if (rx <= 0) pts.AddRange(new[] { new Vector2(x, y), new Vector2(x + w, y), new Vector2(x + w, y + h), new Vector2(x, y + h) });
                    else
                    {
                        void Corner(float cx, float cy, float a0)
                        {
                            for (int i = 0; i <= 6; i++) { float t = a0 + i / 6f * Mathf.PI / 2; pts.Add(new Vector2(cx + rx * Mathf.Cos(t), cy + ry * Mathf.Sin(t))); }
                        }
                        Corner(x + w - rx, y + ry, -Mathf.PI / 2);
                        Corner(x + w - rx, y + h - ry, 0);
                        Corner(x + rx, y + h - ry, Mathf.PI / 2);
                        Corner(x + rx, y + ry, Mathf.PI);
                    }
                    Shape(new List<(List<Vector2>, bool)> { (pts, true) }, p, ctx);
                    return;
                }
                case "circle": case "ellipse":
                {
                    float cx = A(el, "cx"), cy = A(el, "cy");
                    float rx = name == "circle" ? A(el, "r") : A(el, "rx"), ry = name == "circle" ? rx : A(el, "ry");
                    var pts = new List<Vector2>();
                    int n = 48;
                    for (int i = 0; i < n; i++) { float t = i / (float)n * 2 * Mathf.PI; pts.Add(new Vector2(cx + rx * Mathf.Cos(t), cy + ry * Mathf.Sin(t))); }
                    Shape(new List<(List<Vector2>, bool)> { (pts, true) }, p, ctx);
                    return;
                }
                case "line":
                {
                    var pts = new List<Vector2> { new Vector2(A(el, "x1"), A(el, "y1")), new Vector2(A(el, "x2"), A(el, "y2")) };
                    p.Fill = null;
                    Shape(new List<(List<Vector2>, bool)> { (pts, false) }, p, ctx);
                    return;
                }
                case "polyline": case "polygon":
                {
                    var nums = Nums(el.GetAttribute("points"));
                    var pts = new List<Vector2>();
                    for (int i = 0; i + 1 < nums.Count; i += 2) pts.Add(new Vector2(nums[i], nums[i + 1]));
                    Shape(new List<(List<Vector2>, bool)> { (pts, name == "polygon") }, p, ctx);
                    return;
                }
                case "text":
                    Text(el, p, ctx);
                    return;
            }
        }

        // ── drawing ──

        static void Shape(List<(List<Vector2> pts, bool closed)> subpaths, Paint p, Ctx ctx)
        {
            var world = subpaths.Select(sp => (sp.pts.Select(q => (Vector2)p.M.MultiplyPoint3x4(q)).ToList(), sp.closed)).ToList();
            if (p.Fill.HasValue && p.FillOpacity * p.Opacity > 0)
            {
                var tris = new List<int>();
                foreach (var (pts, _) in world)
                {
                    if (pts.Count < 3) continue;
                    var m = Draw.Fill(pts.Select(ctx.P).ToList());
                    int v0 = ctx.Verts.Count;
                    ctx.Verts.AddRange(m.vertices.Select(v => v + new Vector3(0, 0, -ctx.Order * 1e-4f)));
                    tris.AddRange(m.triangles.Select(t => t + v0));
                    Draw.Kill(m);
                }
                Add(ctx, tris, p.Fill.Value, p.FillOpacity * p.Opacity);
            }
            if (p.Stroke.HasValue && p.StrokeOpacity * p.Opacity > 0 && p.StrokeWidth > 0)
            {
                // Width in svg units, scaled by the transform.
                float scale = Mathf.Sqrt(Mathf.Abs(p.M.m00 * p.M.m11 - p.M.m01 * p.M.m10));
                float hw = p.StrokeWidth * scale * ctx.K / 2;
                var tris = new List<int>();
                foreach (var (pts, closed) in world)
                {
                    var q = pts.Select(ctx.P).ToList();
                    if (closed && q.Count > 2) q.Add(q[0]);
                    Strip(ctx, tris, q, hw, p.Join == "round" || p.Cap == "round" || q.Count > 2);
                }
                Add(ctx, tris, p.Stroke.Value, p.StrokeOpacity * p.Opacity);
            }
        }

        static void Add(Ctx ctx, List<int> tris, Color c, float alpha)
        {
            if (tris.Count == 0) return;
            ctx.Subs.Add(tris);
            c.a = 1;
            ctx.Mats.Add((Draw.Unlit(c, 3001), alpha)); // one queue: submeshes draw in order
            ctx.Order++;
        }

        /// <summary>A thick polyline as quads, with discs at the joints so corners close.</summary>
        static void Strip(Ctx ctx, List<int> tris, List<Vector3> pts, float hw, bool roundJoins)
        {
            float z = -ctx.Order * 1e-4f;
            for (int j = 0; j + 1 < pts.Count; j++)
            {
                Vector2 a = pts[j], b = pts[j + 1], d = b - a;
                if (d.sqrMagnitude < 1e-12f) continue;
                d.Normalize();
                var n = new Vector2(-d.y, d.x) * hw;
                int v0 = ctx.Verts.Count;
                ctx.Verts.Add(new Vector3(a.x + n.x, a.y + n.y, z));
                ctx.Verts.Add(new Vector3(a.x - n.x, a.y - n.y, z));
                ctx.Verts.Add(new Vector3(b.x - n.x, b.y - n.y, z));
                ctx.Verts.Add(new Vector3(b.x + n.x, b.y + n.y, z));
                tris.AddRange(new[] { v0, v0 + 1, v0 + 2, v0, v0 + 2, v0 + 3 });
            }
            if (!roundJoins) return;
            for (int j = 1; j + 1 < pts.Count; j++)
            {
                int c0 = ctx.Verts.Count;
                ctx.Verts.Add(new Vector3(pts[j].x, pts[j].y, z));
                const int n = 8;
                for (int i = 0; i < n; i++)
                {
                    float t = i / (float)n * 2 * Mathf.PI;
                    ctx.Verts.Add(new Vector3(pts[j].x + hw * Mathf.Cos(t), pts[j].y + hw * Mathf.Sin(t), z));
                    tris.AddRange(new[] { c0, c0 + 1 + i, c0 + 1 + (i + 1) % n });
                }
            }
        }

        static void Text(XmlElement el, Paint p, Ctx ctx)
        {
            var s = el.InnerText.Trim();
            if (s.Length == 0 || !p.Fill.HasValue) return;
            var at = (Vector2)p.M.MultiplyPoint3x4(new Vector2(A(el, "x"), A(el, "y")));
            float scale = Mathf.Sqrt(Mathf.Abs(p.M.m00 * p.M.m11 - p.M.m01 * p.M.m10));
            float em = p.FontSize * scale * ctx.K;
            string align = p.Anchor == "middle" ? "center" : p.Anchor == "end" ? "right" : "left";
            var t = Draw.Text(ctx.Parent, s, em * 1.2f, p.Fill.Value, align, p.Bold);
            t.alignment = align == "left" ? TextAlignmentOptions.BaselineLeft : align == "right" ? TextAlignmentOptions.BaselineRight : TextAlignmentOptions.Baseline;
            var c = p.Fill.Value; c.a = p.FillOpacity * p.Opacity; t.color = c;
            var pos = ctx.P(at);
            t.transform.localPosition = new Vector3(pos.x, pos.y, -ctx.Order * 1e-4f - 1e-3f);
            t.renderer.sortingOrder = 3;
            ctx.Texts.Add(t);
        }

        // ── attributes and styles ──

        static void Style(XmlElement el, Paint p)
        {
            void Set(string k, string v)
            {
                v = v.Trim();
                switch (k)
                {
                    case "fill": p.Fill = Col(v, p.Fill); break;
                    case "stroke": p.Stroke = Col(v, p.Stroke); break;
                    case "stroke-width": p.StrokeWidth = Len(v, p.StrokeWidth); break;
                    case "opacity": p.Opacity *= F(v); break;
                    case "fill-opacity": p.FillOpacity = F(v); break;
                    case "stroke-opacity": p.StrokeOpacity = F(v); break;
                    case "font-size": p.FontSize = Len(v, p.FontSize); break;
                    case "font-weight": p.Bold = v == "bold" || v == "bolder" || (int.TryParse(v, out var fw) && fw >= 600); break;
                    case "text-anchor": p.Anchor = v; break;
                    case "stroke-linejoin": p.Join = v; break;
                    case "stroke-linecap": p.Cap = v; break;
                }
            }
            foreach (var k in new[] { "fill", "stroke", "stroke-width", "opacity", "fill-opacity", "stroke-opacity", "font-size", "font-weight", "text-anchor", "stroke-linejoin", "stroke-linecap" })
                if (el.HasAttribute(k)) Set(k, el.GetAttribute(k));
            var style = el.GetAttribute("style");
            if (!string.IsNullOrEmpty(style))
                foreach (var decl in style.Split(';'))
                {
                    int i = decl.IndexOf(':');
                    if (i > 0) Set(decl.Substring(0, i).Trim(), decl.Substring(i + 1));
                }
        }

        static readonly Dictionary<string, string> Named = new Dictionary<string, string>
        {
            ["black"] = "#000000", ["white"] = "#ffffff", ["red"] = "#ff0000", ["green"] = "#008000", ["blue"] = "#0000ff",
            ["yellow"] = "#ffff00", ["orange"] = "#ffa500", ["purple"] = "#800080", ["gray"] = "#808080", ["grey"] = "#808080",
            ["cyan"] = "#00ffff", ["magenta"] = "#ff00ff", ["lime"] = "#00ff00", ["navy"] = "#000080", ["teal"] = "#008080",
            ["maroon"] = "#800000", ["olive"] = "#808000", ["silver"] = "#c0c0c0", ["pink"] = "#ffc0cb", ["brown"] = "#a52a2a",
            ["lightgray"] = "#d3d3d3", ["lightgrey"] = "#d3d3d3", ["darkgray"] = "#a9a9a9", ["darkgrey"] = "#a9a9a9",
            ["gold"] = "#ffd700", ["steelblue"] = "#4682b4", ["skyblue"] = "#87ceeb", ["tomato"] = "#ff6347", ["crimson"] = "#dc143c",
            ["coral"] = "#ff7f50", ["salmon"] = "#fa8072", ["violet"] = "#ee82ee", ["indigo"] = "#4b0082", ["darkblue"] = "#00008b",
            ["darkred"] = "#8b0000", ["darkgreen"] = "#006400", ["lightblue"] = "#add8e6", ["lightgreen"] = "#90ee90",
        };

        static Color? Col(string v, Color? cur)
        {
            v = v.Trim().ToLowerInvariant();
            if (v == "none" || v == "transparent") return null;
            if (v == "currentcolor") return Draw.FG;
            if (v.StartsWith("url(")) return Draw.Hex("#7f8c9d"); // gradients/patterns: a neutral stand-in
            if (Named.TryGetValue(v, out var hex)) v = hex;
            if (v.StartsWith("#"))
            {
                if (v.Length == 4) v = "#" + v[1] + v[1] + v[2] + v[2] + v[3] + v[3];
                if (ColorUtility.TryParseHtmlString(v, out var c)) return c;
            }
            var m = Regex.Match(v, @"rgba?\(([^)]*)\)");
            if (m.Success)
            {
                var parts = m.Groups[1].Value.Split(',').Select(x => x.Trim()).ToArray();
                float Ch(string x) => x.EndsWith("%") ? F(x.TrimEnd('%')) / 100 : F(x) / 255;
                if (parts.Length >= 3) return new Color(Ch(parts[0]), Ch(parts[1]), Ch(parts[2]));
            }
            return cur;
        }

        static float F(string s) => float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        static float Len(string s, float def)
        {
            if (string.IsNullOrWhiteSpace(s)) return def;
            s = s.Trim();
            if (s.EndsWith("%")) return def;
            var m = Regex.Match(s, @"^[-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?");
            return m.Success ? F(m.Value) : def;
        }
        static float A(XmlElement el, string k, float def = 0) => el.HasAttribute(k) ? Len(el.GetAttribute(k), def) : def;

        static List<float> Nums(string s) =>
            Regex.Matches(s ?? "", @"[-+]?(?:[0-9]*\.[0-9]+|[0-9]+\.?)(?:[eE][-+]?[0-9]+)?").Cast<Match>().Select(m => F(m.Value)).ToList();

        static Matrix4x4 Transform(string s)
        {
            var M = Matrix4x4.identity;
            foreach (Match m in Regex.Matches(s, @"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)"))
            {
                var a = Nums(m.Groups[2].Value);
                var t = Matrix4x4.identity;
                switch (m.Groups[1].Value)
                {
                    case "matrix":
                        if (a.Count == 6) { t.m00 = a[0]; t.m10 = a[1]; t.m01 = a[2]; t.m11 = a[3]; t.m03 = a[4]; t.m13 = a[5]; }
                        break;
                    case "translate": t = Matrix4x4.Translate(new Vector3(a.ElementAtOrDefault(0), a.ElementAtOrDefault(1), 0)); break;
                    case "scale": t = Matrix4x4.Scale(new Vector3(a.ElementAtOrDefault(0), a.Count > 1 ? a[1] : a.ElementAtOrDefault(0), 1)); break;
                    case "rotate":
                    {
                        var r = Matrix4x4.Rotate(Quaternion.Euler(0, 0, a.ElementAtOrDefault(0)));
                        if (a.Count >= 3) { var c = new Vector3(a[1], a[2], 0); t = Matrix4x4.Translate(c) * r * Matrix4x4.Translate(-c); }
                        else t = r;
                        break;
                    }
                    case "skewX": t.m01 = Mathf.Tan(a.ElementAtOrDefault(0) * Mathf.Deg2Rad); break;
                    case "skewY": t.m10 = Mathf.Tan(a.ElementAtOrDefault(0) * Mathf.Deg2Rad); break;
                }
                M = M * t;
            }
            return M;
        }

        // ── path data ──

        static List<(List<Vector2>, bool)> Path(string d)
        {
            var subs = new List<(List<Vector2>, bool)>();
            var cur = new List<Vector2>();
            Vector2 p = Vector2.zero, start = Vector2.zero, lastCtrl = Vector2.zero;
            char lastCmd = ' ';
            var toks = Regex.Matches(d ?? "", @"[MmLlHhVvCcSsQqTtAaZz]|[-+]?(?:[0-9]*\.[0-9]+|[0-9]+\.?)(?:[eE][-+]?[0-9]+)?").Cast<Match>().Select(m => m.Value).ToList();
            int i = 0;
            char cmd = ' ';
            float N() => i < toks.Count ? F(toks[i++]) : 0;
            bool HasNum() => i < toks.Count && !char.IsLetter(toks[i][0]);
            void Flush(bool closed)
            {
                if (cur.Count > 1) subs.Add((cur, closed));
                cur = new List<Vector2>();
            }
            void Cubic(Vector2 c1, Vector2 c2, Vector2 e)
            {
                for (int k = 1; k <= 16; k++)
                {
                    float t = k / 16f, u = 1 - t;
                    cur.Add(u * u * u * p + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * e);
                }
                lastCtrl = c2; p = e;
            }
            void Quad(Vector2 c1, Vector2 e)
            {
                for (int k = 1; k <= 12; k++)
                {
                    float t = k / 12f, u = 1 - t;
                    cur.Add(u * u * p + 2 * u * t * c1 + t * t * e);
                }
                lastCtrl = c1; p = e;
            }
            while (i < toks.Count)
            {
                if (char.IsLetter(toks[i][0])) cmd = toks[i++][0];
                else if (cmd == 'M') cmd = 'L';
                else if (cmd == 'm') cmd = 'l';
                bool rel = char.IsLower(cmd);
                Vector2 o = rel ? p : Vector2.zero;
                switch (char.ToUpper(cmd))
                {
                    case 'M':
                        Flush(false);
                        p = o + new Vector2(N(), N()); start = p; cur.Add(p);
                        break;
                    case 'L': p = o + new Vector2(N(), N()); cur.Add(p); break;
                    case 'H': p = new Vector2((rel ? p.x : 0) + N(), p.y); cur.Add(p); break;
                    case 'V': p = new Vector2(p.x, (rel ? p.y : 0) + N()); cur.Add(p); break;
                    case 'C': { var c1 = o + new Vector2(N(), N()); var c2 = o + new Vector2(N(), N()); var e = o + new Vector2(N(), N()); Cubic(c1, c2, e); break; }
                    case 'S':
                    {
                        var c1 = "CcSs".IndexOf(lastCmd) >= 0 ? 2 * p - lastCtrl : p;
                        var c2 = o + new Vector2(N(), N()); var e = o + new Vector2(N(), N());
                        Cubic(c1, c2, e);
                        break;
                    }
                    case 'Q': { var c1 = o + new Vector2(N(), N()); var e = o + new Vector2(N(), N()); Quad(c1, e); break; }
                    case 'T':
                    {
                        var c1 = "QqTt".IndexOf(lastCmd) >= 0 ? 2 * p - lastCtrl : p;
                        Quad(c1, o + new Vector2(N(), N()));
                        break;
                    }
                    case 'A':
                    {
                        float rx = N(), ry = N(), rot = N(); bool large = N() != 0, sweep = N() != 0;
                        var e = o + new Vector2(N(), N());
                        Arc(cur, p, e, rx, ry, rot, large, sweep);
                        p = e;
                        break;
                    }
                    case 'Z':
                        cur.Add(start);
                        Flush(true);
                        p = start;
                        cur.Add(p);
                        break;
                    default: i++; break;
                }
                lastCmd = cmd;
                if (char.ToUpper(cmd) == 'Z' && HasNum()) cmd = 'L';
            }
            Flush(false);
            return subs;
        }

        /// <summary>SVG's endpoint arc → points (the spec's F.6.5 centre parameterization).</summary>
        static void Arc(List<Vector2> pts, Vector2 a, Vector2 b, float rx, float ry, float rotDeg, bool large, bool sweep)
        {
            if (rx == 0 || ry == 0) { pts.Add(b); return; }
            rx = Mathf.Abs(rx); ry = Mathf.Abs(ry);
            float phi = rotDeg * Mathf.Deg2Rad, cs = Mathf.Cos(phi), sn = Mathf.Sin(phi);
            var d = (a - b) / 2;
            float x1 = cs * d.x + sn * d.y, y1 = -sn * d.x + cs * d.y;
            float lam = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
            if (lam > 1) { float s = Mathf.Sqrt(lam); rx *= s; ry *= s; }
            float num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
            float den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
            float co = Mathf.Sqrt(Mathf.Max(0, num / Mathf.Max(1e-12f, den))) * (large == sweep ? -1 : 1);
            float cxp = co * rx * y1 / ry, cyp = -co * ry * x1 / rx;
            var mid = (a + b) / 2;
            float cx = cs * cxp - sn * cyp + mid.x, cy = sn * cxp + cs * cyp + mid.y;
            float Ang(float ux, float uy, float vx, float vy) => Mathf.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            float t1 = Ang(1, 0, (x1 - cxp) / rx, (y1 - cyp) / ry);
            float dt = Ang((x1 - cxp) / rx, (y1 - cyp) / ry, (-x1 - cxp) / rx, (-y1 - cyp) / ry);
            if (!sweep && dt > 0) dt -= 2 * Mathf.PI;
            else if (sweep && dt < 0) dt += 2 * Mathf.PI;
            int n = Mathf.Max(4, Mathf.CeilToInt(Mathf.Abs(dt) / (Mathf.PI / 16)));
            for (int k = 1; k <= n; k++)
            {
                float t = t1 + dt * k / n;
                float ex = rx * Mathf.Cos(t), ey = ry * Mathf.Sin(t);
                pts.Add(new Vector2(cs * ex - sn * ey + cx, sn * ex + cs * ey + cy));
            }
        }
    }
}
