// One builder per drawing op: a port of the BUILD.* functions in
// viewer/js/runtime.js. Ops the Unity client can't draw yet show a dimmed
// placeholder naming the op, so a lesson still plays through.
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Vcl
{
    public partial class Runtime
    {
        static readonly Vector3 V0 = Vector3.zero;

        void Build(Entity e, Cmd c)
        {
            switch (c.Op)
            {
                case "pt": BuildPt(e, c); break;
                case "line": case "arrow": BuildLine(e, c); break;
                case "path": BuildPath(e, c); break;
                case "poly": BuildPoly(e, c); break;
                case "rect": BuildRect(e, c); break;
                case "circle": BuildCircle(e, c); break;
                case "angle": BuildAngle(e, c); break;
                case "text": TextEntity(e, c, 0.5f); break;
                case "tex": TextEntity(e, c.With("text", Tex.ToText(c.Str("text"))), 0.5f, bold: false); break;
                case "title": TextEntity(e, c.With("at", new List<object> { 0.0, (double)((Board?.Height ?? 9) / 2 - 0.65f), 0.0 }).With("size", 0.62), 0.62f); break;
                case "node": BuildNode(e, c); break;
                case "edge": BuildEdge(e, c); break;
                case "axes": BuildAxes(e, c); break;
                case "plot": BuildPlot(e, c); break;
                case "surf": BuildSurf(e, c); break;
                case "box": BuildBox(e, c); break;
                case "sphere": BuildSphere(e, c); break;
                case "cyl": BuildCyl(e, c); break;
                default: BuildPlaceholder(e, c); break;
            }
        }

        // ── strokes ──

        class StrokeSpec
        {
            public List<List<Vector3>> Polys;
            public List<Vector3> Fill;
            public Vector3[] Ends;
            public Vector3? Label;
            public bool Live, FixedOrigin;
        }

        class StrokeOpts
        {
            public float Width = 0.06f;
            public Color Color = Draw.FG;
            public bool Dash;
            public Color? FillColor;
            public float FillOpacity = 0.2f;
            public bool HeadEnd, HeadStart;
        }

        /// <summary>Shared machinery for anything made of strokes (+ optional heads/fill).</summary>
        void StrokeEntity(Entity e, Func<StrokeSpec> spec, StrokeOpts o)
        {
            var stroke = new Stroke(e.Body, o.Color, o.Width, o.Dash, e.In3D);
            e.Strokes.Add(stroke);
            e.Track(stroke.Material, "stroke");
            float headSize = Mathf.Max(0.2f, o.Width * 4.2f);
            Head MkHead()
            {
                var h = new Head(e.Body, o.Color, headSize);
                e.Track(h.Material, "stroke");
                return h;
            }
            var endHead = o.HeadEnd ? MkHead() : null;
            var startHead = o.HeadStart ? MkHead() : null;
            Material fillMat = null;
            MeshRenderer fill = null;
            if (o.FillColor.HasValue) fillMat = e.Track(Draw.Unlit(o.FillColor.Value, 3001), "fill", o.FillOpacity);

            var s = spec();
            var all = s.Polys.SelectMany(p => p).Concat(s.Fill ?? new List<Vector3>()).ToList();
            if (!s.FixedOrigin && all.Count > 0)
            {
                var b = new Bounds(all[0], Vector3.zero);
                foreach (var v in all) b.Encapsulate(v);
                e.Obj.localPosition = b.center;
            }
            e.Live = s.Live;

            List<List<Vector3>> local = null;
            void Compute()
            {
                local = s.Polys.Select(pl => pl.Select(e.ParentToLocal).ToList()).ToList();
                e.Ends = s.Ends?.Select(e.ParentToLocal).ToArray();
                if (fillMat != null)
                {
                    if (fill) Destroy(fill.gameObject);
                    fill = null;
                    if (s.Fill != null && s.Fill.Count >= 3)
                        fill = Draw.MeshObject(e.Body, "fill", Draw.Fill(s.Fill.Select(e.ParentToLocal).ToList()), fillMat);
                }
                if (e.Label && s.Label.HasValue) e.Label.transform.localPosition = e.ParentToLocal(s.Label.Value) + e.LabelShift;
            }
            Compute();

            e.Render = p =>
            {
                var polys = Draw.Partial(local, p);
                if (endHead != null)
                {
                    var tip = Draw.Tip(polys);
                    endHead.Place(p > 0 ? tip : null);
                    if (tip.HasValue) polys = polys.Select((pl, i) => i == polys.Count - 1 ? Draw.TrimEnd(pl, headSize * 0.8f) : pl).ToList();
                }
                if (startHead != null && polys.Count > 0 && polys[0].Count >= 2)
                {
                    var first = polys[0];
                    var tip = Draw.Tip(new List<List<Vector3>> { new List<Vector3> { first[1], first[0] } });
                    startHead.Place(p > 0 ? tip : null);
                    if (tip.HasValue) polys = new[] { Draw.TrimStart(first, headSize * 0.8f) }.Concat(polys.Skip(1)).ToList();
                }
                else startHead?.Place(null);
                stroke.Set(polys);
                e.Factor["fill"] = p;
                e.ApplyOpacity();
            };

            string key = "";
            e.Refresh = () =>
            {
                StrokeSpec next;
                try { next = spec(); } catch { return; } // a referenced entity vanished mid-fade
                var k = string.Join(";", next.Polys.SelectMany(p => p).Select(v => $"{v.x:F4},{v.y:F4},{v.z:F4}"));
                if (k == key) return;
                key = k;
                s = next;
                Compute();
                e.Render(e.P);
            };
        }

        static void Destroy(UnityEngine.Object o) => Draw.Kill(o);

        static void LabelFor(Entity e, Cmd c, Vector3 pos, string align = "center")
        {
            if (c.Has("label")) e.AddLabel(c.Str("label"), pos, align);
        }

        void BuildPt(Entity e, Cmd c)
        {
            float r = c.Flt("size", 0.13f);
            var mat = e.Track(Draw.Unlit(Draw.Hex(c.Str("color")), 3003), "stroke");
            var m = Draw.MeshObject(e.Body, "dot", Draw.Primitive(PrimitiveType.Sphere), mat, 2);
            m.transform.localScale = Vector3.one * r * 2;
            e.Obj.localPosition = e.ToParent(c["at"]);
            LabelFor(e, c, new Vector3(r + 0.08f, r + 0.12f, 0), "left");
        }

        static void LineLabel(Entity e, Cmd c, Vector3 a, Vector3 b)
        {
            if (!c.Has("label")) return;
            var d = b - a;
            var n = new Vector3(-d.y, d.x, 0).normalized * 0.32f;
            if (n.y < 0 || (Mathf.Abs(n.y) < 1e-6f && n.x > 0)) n = -n; // prefer above/left
            e.AddLabel(c.Str("label"), V0);
            e.LabelShift = n;
        }

        void BuildLine(Entity e, Cmd c)
        {
            StrokeSpec Spec()
            {
                var a = e.ToParent(c["from"]);
                var b = e.ToParent(c["to"]);
                return new StrokeSpec { Polys = new List<List<Vector3>> { new List<Vector3> { a, b } }, Ends = new[] { a, b }, Label = (a + b) / 2, Live = IsRef(c["from"]) || IsRef(c["to"]) };
            }
            var s0 = Spec();
            LineLabel(e, c, s0.Ends[0], s0.Ends[1]);
            StrokeEntity(e, Spec, new StrokeOpts
            {
                Width = c.Flt("width", 0.06f), Color = Draw.Hex(c.Str("color")), Dash = c.Bool("dash"),
                HeadEnd = c.Op == "arrow", HeadStart = c.Op == "arrow" && c.Str("head") == "both",
            });
        }

        void BuildPath(Entity e, Cmd c)
        {
            var raw = c.List("pts");
            bool closed = c.Bool("closed"), smooth = c.Bool("smooth");
            StrokeSpec Spec()
            {
                var pts = raw.Select(e.ToParent).ToList();
                if (smooth && pts.Count > 2) pts = CatmullRom(pts, closed, 48 * pts.Count);
                if (closed) pts.Add(pts[0]);
                return new StrokeSpec { Polys = new List<List<Vector3>> { pts }, Ends = new[] { pts[0], pts[pts.Count - 1] }, Label = pts[0], Live = raw.Any(IsRef) };
            }
            if (c.Has("label")) { e.AddLabel(c.Str("label"), V0); e.LabelShift = new Vector3(0, 0.35f, 0); }
            StrokeEntity(e, Spec, new StrokeOpts { Width = c.Flt("width", 0.06f), Color = Draw.Hex(c.Str("color")), Dash = c.Bool("dash") });
        }

        /// <summary>Centripetal Catmull-Rom through the points (three.js CatmullRomCurve3 'centripetal').</summary>
        static List<Vector3> CatmullRom(List<Vector3> p, bool closed, int n)
        {
            int segs = closed ? p.Count : p.Count - 1;
            var outp = new List<Vector3>();
            Vector3 P(int i)
            {
                if (closed) return p[((i % p.Count) + p.Count) % p.Count];
                if (i < 0) return p[0] * 2 - p[1];
                if (i >= p.Count) return p[p.Count - 1] * 2 - p[p.Count - 2];
                return p[i];
            }
            int per = Mathf.Max(2, n / segs);
            for (int s = 0; s < segs; s++)
            {
                Vector3 p0 = P(s - 1), p1 = P(s), p2 = P(s + 1), p3 = P(s + 2);
                float Tj(float ti, Vector3 a, Vector3 b) => ti + Mathf.Max(1e-4f, Mathf.Pow((b - a).sqrMagnitude, 0.25f));
                float t0 = 0, t1 = Tj(t0, p0, p1), t2 = Tj(t1, p1, p2), t3 = Tj(t2, p2, p3);
                for (int k = 0; k < per; k++)
                {
                    float t = Mathf.Lerp(t1, t2, k / (float)per);
                    var a1 = (t1 - t) / (t1 - t0) * p0 + (t - t0) / (t1 - t0) * p1;
                    var a2 = (t2 - t) / (t2 - t1) * p1 + (t - t1) / (t2 - t1) * p2;
                    var a3 = (t3 - t) / (t3 - t2) * p2 + (t - t2) / (t3 - t2) * p3;
                    var b1 = (t2 - t) / (t2 - t0) * a1 + (t - t0) / (t2 - t0) * a2;
                    var b2 = (t3 - t) / (t3 - t1) * a2 + (t - t1) / (t3 - t1) * a3;
                    outp.Add((t2 - t) / (t2 - t1) * b1 + (t - t1) / (t2 - t1) * b2);
                }
            }
            if (!closed) outp.Add(p[p.Count - 1]);
            return outp;
        }

        void BuildPoly(Entity e, Cmd c)
        {
            var raw = c.List("pts");
            StrokeSpec Spec()
            {
                var pts = raw.Select(e.ToParent).ToList();
                var b = new Bounds(pts[0], V0);
                foreach (var v in pts) b.Encapsulate(v);
                return new StrokeSpec { Polys = new List<List<Vector3>> { pts.Append(pts[0]).ToList() }, Fill = pts, Label = b.center, Live = raw.Any(IsRef) };
            }
            if (c.Has("label")) e.AddLabel(c.Str("label"), V0);
            var color = Draw.Hex(c.Str("color"));
            StrokeEntity(e, Spec, new StrokeOpts { Width = c.Flt("width", 0.05f), Color = color, FillColor = c.Has("fill") ? Draw.Hex(c.Str("fill")) : color, FillOpacity = c.Has("fill") ? 0.55f : 0.18f });
        }

        void ClosedShape(Entity e, Cmd c, List<Vector3> pts)
        {
            StrokeSpec Spec()
            {
                var at = e.Obj.localPosition;
                var moved = pts.Select(p => p + at).ToList();
                return new StrokeSpec { Polys = new List<List<Vector3>> { moved }, Fill = c.Has("fill") ? moved : null, FixedOrigin = true };
            }
            StrokeEntity(e, Spec, new StrokeOpts { Width = c.Flt("width", 0.05f), Color = Draw.Hex(c.Str("color")), FillColor = c.Has("fill") ? Draw.Hex(c.Str("fill")) : (Color?)null, FillOpacity = 0.55f });
        }

        void BuildRect(Entity e, Cmd c)
        {
            var sz = Raw(c["size"]);
            e.Obj.localPosition = e.ToParent(c["at"]);
            ClosedShape(e, c, Draw.RoundedRect(sz.x, sz.y, c.Flt("radius", 0)));
            LabelFor(e, c, new Vector3(0, sz.y / 2 + 0.3f, 0));
        }

        void BuildCircle(Entity e, Cmd c)
        {
            float r = c.Flt("r", 1);
            e.Obj.localPosition = e.ToParent(c["at"]);
            ClosedShape(e, c, Draw.CirclePoints(r).Select(p => new Vector3(p.y, p.x, 0)).ToList()); // start at the top, clockwise
            LabelFor(e, c, new Vector3(0, r + 0.3f, 0));
        }

        void BuildAngle(Entity e, Cmd c)
        {
            float r = c.Flt("radius", 0.6f);
            bool right = c.Bool("right");
            StrokeSpec Spec()
            {
                var A = e.ToParent(c["a"]);
                var O = e.ToParent(c["vertex"]);
                var B = e.ToParent(c["b"]);
                var u = (A - O).normalized;
                var w = (B - O).normalized;
                var arc = new List<Vector3>();
                if (right)
                {
                    float s = r * 0.6f;
                    arc.Add(O + u * s); arc.Add(O + u * s + w * s); arc.Add(O + w * s);
                }
                else
                {
                    float th = Mathf.Acos(Mathf.Clamp(Vector3.Dot(u, w), -1, 1));
                    const int n = 32;
                    for (int i = 0; i <= n; i++)
                    {
                        float t = i / (float)n;
                        float k1 = th > 1e-6f ? Mathf.Sin((1 - t) * th) / Mathf.Sin(th) : 1 - t;
                        float k2 = th > 1e-6f ? Mathf.Sin(t * th) / Mathf.Sin(th) : t;
                        arc.Add(O + u * (k1 * r) + w * (k2 * r));
                    }
                }
                var bis = (u + w).normalized;
                var fill = new List<Vector3> { O };
                fill.AddRange(arc);
                return new StrokeSpec { Polys = new List<List<Vector3>> { arc }, Fill = fill, Label = O + bis * (r + 0.35f), Live = new[] { c["a"], c["vertex"], c["b"] }.Any(IsRef) };
            }
            if (c.Has("label")) e.AddLabel(c.Str("label"), V0);
            var color = Draw.Hex(c.Str("color") ?? "#ffcc66");
            StrokeEntity(e, Spec, new StrokeOpts { Width = c.Flt("width", 0.045f), Color = color, FillColor = color, FillOpacity = right ? 0 : 0.18f });
        }

        // ── text ──

        void TextEntity(Entity e, Cmd c, float defSize, bool bold = true)
        {
            float size = c.Flt("size", defSize);
            var t = Draw.Text(e.Body, c.Str("text") ?? "", size, Draw.Hex(c.Str("color")), c.Str("align") ?? "center", bold);
            e.Track(t, "text");
            e.Obj.localPosition = e.ToParent(c["at"]);
            if (e.In3D) e.Billboards.Add(t.transform);
            e.SetText = s => t.text = c.Op == "tex" ? Tex.ToText(s) : s;
            LabelFor(e, c, new Vector3(0, -size * 0.9f, 0));
        }

        void BuildNode(Entity e, Cmd c)
        {
            e.Obj.localPosition = e.ToParent(c["at"]);
            float size = c.Flt("size", 0.45f);
            var t = Draw.Text(e.Body, c.Str("text") ?? "", size, Draw.Hex(c.Str("color")));
            e.Track(t, "text");
            t.transform.localPosition = new Vector3(0, 0, -0.01f);
            var strokeColor = Draw.Hex(c.Str("color") ?? "#7fa7d9");
            var fillMat = e.Track(Draw.Unlit(Draw.Hex(c.Str("fill") ?? "#1b2635"), 3001), "fill", 0.95f);
            var stroke = new Stroke(e.Body, strokeColor, 0.05f);
            e.Strokes.Add(stroke);
            e.Track(stroke.Material, "stroke");
            MeshRenderer fill = null;
            void Rebuild()
            {
                if (fill) Destroy(fill.gameObject);
                var sz = Draw.TextSize(t);
                string kind = c.Str("shape") ?? (t.text.Length <= 3 ? "circle" : "box");
                List<Vector3> outline;
                if (kind == "circle")
                {
                    float r = Mathf.Max(sz.x, sz.y) / 2 + size * 0.35f;
                    outline = Draw.CirclePoints(r, 64);
                    e.Shape = (kind, r, r);
                }
                else
                {
                    float bw = sz.x + size * 0.7f, bh = sz.y + size * 0.5f;
                    outline = Draw.RoundedRect(bw, bh, kind == "pill" ? bh / 2 : kind == "box" ? size * 0.25f : 0);
                    e.Shape = (kind, bw / 2, bh / 2);
                }
                if (kind != "none")
                {
                    fill = Draw.MeshObject(e.Body, "fill", Draw.Fill(outline), fillMat);
                    stroke.Set(new List<List<Vector3>> { outline });
                }
                else stroke.Set(new List<List<Vector3>>());
            }
            Rebuild();
            e.SetText = s => { t.text = s; Rebuild(); };
        }

        void BuildEdge(Entity e, Cmd c)
        {
            var A = Get(c.Str("from"));
            var B = Get(c.Str("to"));
            float bend = c.Flt("bend", 0);
            StrokeSpec Spec()
            {
                var ca = Stage.InverseTransformPoint(AnchorWorld(A));
                var cb = Stage.InverseTransformPoint(AnchorWorld(B));
                var d = cb - ca;
                var n = new Vector3(-d.y, d.x, 0).normalized;
                var ctrl = (ca + cb) / 2 + n * (bend * d.magnitude * 0.5f);
                var pa = BorderPoint(A, ctrl - ca);
                var pb = BorderPoint(B, ctrl - cb);
                Vector3 Q(float t) => (1 - t) * (1 - t) * pa + 2 * (1 - t) * t * ctrl + t * t * pb;
                var pts = bend != 0 ? Enumerable.Range(0, 41).Select(i => Q(i / 40f)).ToList() : new List<Vector3> { pa, pb };
                Vector3 ToP(Vector3 v) => e.ParentObj.InverseTransformPoint(Stage.TransformPoint(v));
                var P = pts.Select(ToP).ToList();
                var mid = ToP(bend != 0 ? Q(0.5f) : (pa + pb) / 2);
                return new StrokeSpec { Polys = new List<List<Vector3>> { P }, Ends = new[] { P[0], P[P.Count - 1] }, Label = mid, Live = true };
            }
            if (c.Has("label"))
            {
                var s0 = Spec();
                LineLabel(e, c, s0.Ends[0], s0.Ends[1]);
            }
            string dir = c.Str("dir") ?? "fwd";
            StrokeEntity(e, Spec, new StrokeOpts
            {
                Width = c.Flt("width", 0.05f), Color = Draw.Hex(c.Str("color") ?? "#9fb4cc"), Dash = c.Bool("dash"),
                HeadEnd = dir == "fwd" || dir == "both", HeadStart = dir == "back" || dir == "both",
            });
        }

        // ── solids ──

        MeshRenderer Solid(Entity e, Cmd c, Mesh mesh)
        {
            var mat = e.Track(Draw.Lit(Draw.Hex(c.Str("color") ?? Palette[0])), "solid");
            var r = Draw.MeshObject(e.Body, "solid", mesh, mat, 0);
            if (c.Bool("wire")) mat.SetFloat("_Glossiness", 0.1f); // no wireframe on mobile GPUs; see README
            return r;
        }

        void BuildBox(Entity e, Cmd c)
        {
            e.Obj.localPosition = e.ToParent(c["at"]);
            var s = Raw(c["size"]);
            var m = Solid(e, c, Draw.Primitive(PrimitiveType.Cube));
            m.transform.localScale = new Vector3(s.x, s.y, s.z > 0 ? s.z : Mathf.Min(s.x, s.y));
            LabelFor(e, c, new Vector3(0, s.y / 2 + 0.3f, 0));
        }

        void BuildSphere(Entity e, Cmd c)
        {
            e.Obj.localPosition = e.ToParent(c["at"]);
            float r = c.Flt("r", 1);
            Solid(e, c, Draw.Primitive(PrimitiveType.Sphere)).transform.localScale = Vector3.one * 2 * r;
            LabelFor(e, c, new Vector3(0, r + 0.3f, 0));
        }

        void BuildCyl(Entity e, Cmd c)
        {
            var a = e.ToParent(c["from"]);
            var b = e.ToParent(c["to"]);
            float r = c.Flt("r", 0.5f);
            e.Obj.localPosition = (a + b) / 2;
            var d = b - a;
            var m = Solid(e, c, Draw.Primitive(PrimitiveType.Cylinder)); // height 2 along y
            m.transform.localScale = new Vector3(2 * r, d.magnitude / 2, 2 * r);
            m.transform.localRotation = Quaternion.FromToRotation(Vector3.up, d.normalized);
            e.Ends = new[] { a - e.Obj.localPosition, b - e.Obj.localPosition };
            LabelFor(e, c, new Vector3(0, r + 0.3f, 0));
        }

        // ── axes, plot, surf ──

        void BuildAxes(Entity e, Cmd c)
        {
            bool is3 = c.Has("z");
            var xr = c.Range("x") ?? new double[] { -5, 5 };
            var yr = c.Range("y") ?? (is3 ? new double[] { -5, 5 } : new double[] { -3, 3 });
            var zr = c.Range("z") ?? new double[] { 0, 0 };
            var size = c.Has("size") ? Raw(c["size"]) : (is3 ? new Vector3(5, 5, 5) : new Vector3(8, 5, 0));
            float W = size.x, Hh = size.y, Dd = size.z != 0 ? size.z : size.x;
            float sx = W / (float)(xr[1] - xr[0]), sy = Hh / (float)(yr[1] - yr[0]), sz = is3 ? Dd / (float)(zr[1] - zr[0]) : 1;
            float mx = (float)(xr[0] + xr[1]) / 2, my = (float)(yr[0] + yr[1]) / 2, mz = (float)(zr[0] + zr[1]) / 2;
            float smin = Mathf.Min(sx, sy);
            // Data (x, y, z) → axes-local. 3D: data z is up, data y goes into the board.
            Func<Vector3, Vector3> map = is3
                ? (Func<Vector3, Vector3>)(v => new Vector3((v.x - mx) * sx, (v.z - mz) * sz, (v.y - my) * sy))
                : v => new Vector3((v.x - mx) * sx, (v.y - my) * sy, -v.z * smin);
            e.Obj.localPosition = e.ToParent(c["at"]); // before AxesSelf is set: `at` is in board space
            e.AxesSelf = new AxesInfo { Is3 = is3, X = xr, Y = yr, Z = zr, Map = map };
            if (is3) { e.Euler = new Vector3(0.35f, -0.55f, 0); e.Obj.localRotation = ThreeEuler(e.Euler); }

            double Clamp(double v, double[] r) => Math.Min(Math.Max(v, r[0]), r[1]);
            float ox = (float)Clamp(0, xr), oy = (float)Clamp(0, yr), oz = (float)Clamp(0, zr);
            Vector3 P(double x, double y, double? z = null) => map(new Vector3((float)x, (float)y, z.HasValue ? (float)z.Value : oz));
            var color = Draw.Hex(c.Str("color") ?? "#8a98ab");
            var axisPolys = new List<List<Vector3>>
            {
                new List<Vector3> { P(xr[0], oy), P(xr[1], oy) },
                new List<Vector3> { P(ox, yr[0]), P(ox, yr[1]) },
            };
            if (is3) axisPolys.Add(new List<Vector3> { P(ox, oy, zr[0]), P(ox, oy, zr[1]) });

            var strokes = axisPolys.Select(_ =>
            {
                var s = new Stroke(e.Body, color, 0.045f, false, is3);
                e.Strokes.Add(s);
                e.Track(s.Material, "stroke");
                var h = new Head(e.Body, color, 0.22f);
                e.Track(h.Material, "stroke");
                return (s, h);
            }).ToList();

            // Ticks, grid and labels (fade in with the 'label' factor).
            var deco = Draw.Child(e.Obj, "deco").transform;
            var tickStroke = new Stroke(deco, color, 0.03f, false, is3);
            e.Strokes.Add(tickStroke);
            e.Track(tickStroke.Material, "label");
            var gridStroke = new Stroke(deco, Draw.FG, 0.018f, false, is3);
            e.Strokes.Add(gridStroke);
            e.Track(gridStroke.Material, "label", 0.12f);
            var ticks = new List<List<Vector3>>();
            var grid = new List<List<Vector3>>();
            const float tl = 0.1f;
            void Label(string text, Vector3 pos, string align = "center", float sizeL = 0.26f, string col = "#aab6c5")
            {
                var tp = Draw.Text(deco, text, sizeL, Draw.Hex(col), align, false);
                e.Track(tp, "label");
                tp.transform.localPosition = pos;
                if (is3) e.Billboards.Add(tp.transform);
            }
            bool showTicks = c.Bool("ticks", true), showGrid = c.Bool("grid", true);
            void Each(double[] r, Action<double> f)
            {
                double st = Draw.NiceStep(r[1] - r[0]);
                for (double v = Math.Ceiling(r[0] / st - 1e-9) * st; v <= r[1] + 1e-9; v += st) f(v);
            }
            Each(xr, v =>
            {
                if (showGrid) grid.Add(new List<Vector3> { P(v, yr[0]), P(v, yr[1]) });
                if (!showTicks || Math.Abs(v - ox) < 1e-9) return;
                var p = P(v, oy);
                ticks.Add(new List<Vector3> { p + new Vector3(0, -tl, 0), p + new Vector3(0, tl, 0) });
                Label(Draw.FmtTick(v), p + new Vector3(0, -0.32f, 0));
            });
            Each(yr, v =>
            {
                if (showGrid) grid.Add(new List<Vector3> { P(xr[0], v), P(xr[1], v) });
                if (!showTicks || Math.Abs(v - oy) < 1e-9) return;
                var p = P(ox, v);
                ticks.Add(new List<Vector3> { p + new Vector3(-tl, 0, 0), p + new Vector3(tl, 0, 0) });
                Label(Draw.FmtTick(v), p + new Vector3(-0.2f, 0, 0), "right");
            });
            if (is3) Each(zr, v =>
            {
                if (!showTicks || Math.Abs(v - oz) < 1e-9) return;
                var p = P(ox, oy, v);
                ticks.Add(new List<Vector3> { p + new Vector3(-tl, 0, 0), p + new Vector3(tl, 0, 0) });
                Label(Draw.FmtTick(v), p + new Vector3(-0.2f, 0, 0), "right");
            });
            tickStroke.Set(ticks);
            gridStroke.Set(grid);
            Label(c.Str("xlabel") ?? "x", P(xr[1], oy) + new Vector3(0.35f, 0, 0), "left", 0.34f, "#e8edf3");
            Label(c.Str("ylabel") ?? "y", P(ox, yr[1]) + (is3 ? new Vector3(0, 0, 0.35f) : new Vector3(0, 0.35f, 0)), "center", 0.34f, "#e8edf3");
            if (is3) Label(c.Str("zlabel") ?? "z", P(ox, oy, zr[1]) + new Vector3(0, 0.35f, 0), "center", 0.34f, "#e8edf3");

            e.Render = p =>
            {
                for (int i = 0; i < axisPolys.Count; i++)
                {
                    var part = Draw.Partial(new List<List<Vector3>> { axisPolys[i] }, p);
                    var (s, h) = strokes[i];
                    var tip = Draw.Tip(part);
                    h.Place(p > 0 ? tip : null);
                    s.Set(tip.HasValue ? new List<List<Vector3>> { Draw.TrimEnd(part[0], 0.18f) } : part);
                }
            };
        }

        AxesInfo AxesOf(Entity e, Cmd c)
        {
            var ax = Get(c.Str("axes"));
            if (ax.AxesSelf == null) throw new Exception($"'{c.Str("axes")}' is not axes");
            // Plots always live in their axes' space.
            if (e.ParentEnt != ax)
            {
                e.Obj.SetParent(ax.Obj, false);
                e.ParentEnt = ax;
            }
            e.Axes = ax.AxesSelf;
            return ax.AxesSelf;
        }

        void BuildPlot(Entity e, Cmd c)
        {
            var A = AxesOf(e, c);
            var color = Draw.Hex(c.Str("color") ?? Palette[A.Plots++ % Palette.Length]);
            var raw = c.List("polys") ?? throw new Exception("no sampled points (polys): the server is older than this client");
            var polys = raw.Select(pl => ((List<object>)pl).Select(v => A.Map(Raw(v))).ToList()).ToList();
            if (polys.Count == 0) throw new Exception("nothing to draw in this range");
            var last = polys[polys.Count - 1];
            StrokeEntity(e, () => new StrokeSpec { Polys = polys, Ends = new[] { polys[0][0], last[last.Count - 1] }, Label = last[last.Count - 1], FixedOrigin = true },
                new StrokeOpts { Width = c.Flt("width", 0.06f), Color = color, Dash = c.Bool("dash") });
            if (c.Has("label"))
            {
                e.AddLabel(c.Str("label"), V0, "left");
                e.Label.transform.localPosition = e.Ends[1] + new Vector3(0.2f, 0.2f, 0);
            }
        }

        void BuildSurf(Entity e, Cmd c)
        {
            var A = AxesOf(e, c);
            if (!A.Is3) throw new Exception("surf needs 3D axes (give the axes a z=range)");
            var zg = c["zgrid"] as Dictionary<string, object> ?? throw new Exception("no sampled heights (zgrid): the server is older than this client");
            int n = (int)(double)zg["n"];
            var zs = ((List<object>)zg["z"]).Select(v => (float)(double)v).ToArray();
            var xr = c.Range("x") ?? A.X;
            var yr = c.Range("y") ?? A.Y;
            float zlo = (float)A.Z[0], zhi = (float)A.Z[1];
            float z0 = Mathf.Clamp(0, zlo, zhi);
            int N = n + 1;
            var verts = new Vector3[N * N];
            var cols = new Color[N * N];
            var stops = new[] { Draw.Hex("#3949ab"), Draw.Hex("#26a69a"), Draw.Hex("#ffee58") };
            Color? fillC = c.Has("fill") ? Draw.Hex(c.Str("fill")) : (Color?)null;
            for (int k = 0; k < zs.Length; k++)
            {
                float t = (zs[k] - zlo) / (zhi - zlo == 0 ? 1 : zhi - zlo);
                cols[k] = fillC ?? (t < 0.5f ? Color.Lerp(stops[0], stops[1], t * 2) : Color.Lerp(stops[1], stops[2], (t - 0.5f) * 2));
            }
            var tris = new List<int>();
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int a = j * N + i, b = a + 1, d = a + N, f = d + 1;
                    tris.AddRange(new[] { a, d, b, b, d, f });
                }
            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = verts;
            mesh.colors = cols;
            mesh.triangles = tris.ToArray();
            var mat = e.Track(Draw.Lit(Color.white, doubleSided: true, smooth: 0.4f), "fill", 0.92f);
            Draw.MeshObject(e.Body, "surf", mesh, mat, 0);
            Stroke wire = null;
            if (c.Bool("wire"))
            {
                wire = new Stroke(e.Body, Draw.Hex("#0b0f16"), 0.02f, false, true);
                e.Strokes.Add(wire);
                e.Track(wire.Material, "stroke", 0.35f);
            }
            e.Render = p =>
            {
                for (int j = 0; j < N; j++)
                    for (int i = 0; i < N; i++)
                    {
                        float x = (float)(xr[0] + (xr[1] - xr[0]) * i / n), y = (float)(yr[0] + (yr[1] - yr[0]) * j / n);
                        verts[j * N + i] = A.Map(new Vector3(x, y, z0 + (zs[j * N + i] - z0) * p));
                    }
                mesh.vertices = verts;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                if (wire != null)
                {
                    int stride = Mathf.Max(1, n / 16);
                    var lines = new List<List<Vector3>>();
                    for (int j = 0; j <= n; j += stride) lines.Add(Enumerable.Range(0, N).Select(i => verts[j * N + i]).ToList());
                    for (int i = 0; i <= n; i += stride) lines.Add(Enumerable.Range(0, N).Select(j => verts[j * N + i]).ToList());
                    wire.Set(lines);
                }
                e.Factor["fill"] = 1;
            };
        }

        // ── not drawn yet ──

        void BuildPlaceholder(Entity e, Cmd c)
        {
            if (c.Has("at")) e.Obj.localPosition = e.ToParent(c["at"]);
            else if (c.Has("pts")) e.Obj.localPosition = e.ToParent(c.List("pts")[0]);
            var t = Draw.Text(e.Body, $"[{c.Op}]", 0.4f, Draw.Hex("#6b7a8f"), "center", false);
            e.Track(t, "text");
            Log("warning", $"line {c.Line}: {c.Op}: not drawn by the Unity client yet (shown as a placeholder)");
        }
    }

    /// <summary>LaTeX → readable plain text with Unicode symbols (TMP has no math layout).</summary>
    public static class Tex
    {
        static readonly (string, string)[] Symbols =
        {
            ("\\alpha", "α"), ("\\beta", "β"), ("\\gamma", "γ"), ("\\delta", "δ"), ("\\Delta", "Δ"), ("\\epsilon", "ε"),
            ("\\varepsilon", "ε"), ("\\theta", "θ"), ("\\lambda", "λ"), ("\\mu", "μ"), ("\\pi", "π"), ("\\rho", "ρ"),
            ("\\sigma", "σ"), ("\\Sigma", "Σ"), ("\\tau", "τ"), ("\\phi", "φ"), ("\\varphi", "φ"), ("\\omega", "ω"),
            ("\\Omega", "Ω"), ("\\infty", "∞"), ("\\cdot", "·"), ("\\times", "×"), ("\\div", "÷"), ("\\pm", "±"),
            ("\\leq", "≤"), ("\\le", "≤"), ("\\geq", "≥"), ("\\ge", "≥"), ("\\neq", "≠"), ("\\approx", "≈"),
            ("\\to", "→"), ("\\rightarrow", "→"), ("\\Rightarrow", "⇒"), ("\\leftarrow", "←"), ("\\partial", "∂"),
            ("\\nabla", "∇"), ("\\int", "∫"), ("\\sum", "Σ"), ("\\prod", "Π"), ("\\sqrt", "√"), ("\\in", "∈"),
            ("\\lim", "lim"), ("\\sin", "sin"), ("\\cos", "cos"), ("\\tan", "tan"), ("\\ln", "ln"), ("\\log", "log"),
            ("\\left", ""), ("\\right", ""), ("\\,", " "), ("\\;", " "), ("\\quad", "  "), ("\\!", ""),
        };

        public static string ToText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("$", "");
            // \frac{a}{b} → (a)/(b)
            s = s.Replace("\\tfrac", "\\frac").Replace("\\dfrac", "\\frac");
            for (int guard = 0; guard < 20; guard++)
            {
                int i = s.IndexOf("\\frac", StringComparison.Ordinal);
                if (i < 0) break;
                int j = i + 5;
                string a = Group(s, ref j), b = Group(s, ref j);
                if (a == null || b == null) break;
                string wrap(string x) => x.Length <= 1 ? x : $"({x})";
                s = s.Substring(0, i) + wrap(a) + "/" + wrap(b) + s.Substring(j);
            }
            foreach (var (k, v) in Symbols.OrderByDescending(p => p.Item1.Length)) s = s.Replace(k, v);
            s = Script(s, '^', "<sup>", "</sup>");
            s = Script(s, '_', "<sub>", "</sub>");
            return s.Replace("{", "").Replace("}", "").Replace("\\", "");
        }

        static string Group(string s, ref int j)
        {
            while (j < s.Length && s[j] == ' ') j++;
            if (j >= s.Length) return null;
            if (s[j] != '{') return s[j++].ToString();
            int depth = 0, st = j;
            for (; j < s.Length; j++)
            {
                if (s[j] == '{') depth++;
                else if (s[j] == '}' && --depth == 0) { j++; return s.Substring(st + 1, j - st - 2); }
            }
            return null;
        }

        static string Script(string s, char mark, string open, string close)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != mark || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                int j = i + 1;
                var g = Group(s, ref j);
                if (g == null) { sb.Append(s[i]); continue; }
                sb.Append(open).Append(g).Append(close);
                i = j - 1;
            }
            return sb.ToString();
        }
    }
}
