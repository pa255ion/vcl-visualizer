// The newer scenes (math, circuits, space, lakeside): the Unity twins of the
// builders in viewer/js/scenery.js. Positions are written in the browser's
// coordinates and mirrored by B() (the browser's -z is this app's +z), so the
// two stay easy to compare.
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Vcl
{
    public partial class Scenery
    {
        /// <summary>A point given in browser coordinates.</summary>
        static Vector3 B(float x, float y, float z) => new Vector3(x, y, -z);

        static float Wrap(float a) => Mathf.Atan2(Mathf.Sin(a), Mathf.Cos(a));

        /// <summary>Is browser angle a (x = cos a, z = sin a) within `half` of straight behind the board (-z)?</summary>
        static bool BehindBoard(float a, float half) => Mathf.Abs(Wrap(a + Mathf.PI / 2)) < half;

        /// <summary>Flat strips on the ground, one per segment (browser x0, z0, x1, z1), `w` wide; ends extended so joints close.</summary>
        static Mesh Strips(List<(float x0, float z0, float x1, float z1)> segs, float w, float y)
        {
            var v = new List<Vector3>(); var tr = new List<int>();
            foreach (var (x0, z0, x1, z1) in segs)
            {
                var a = B(x0, y, z0); var b = B(x1, y, z1);
                var d = b - a; float L = d.magnitude; d = L > 0 ? d / L : Vector3.right;
                a -= d * w / 2; b += d * w / 2;
                var p = new Vector3(-d.z, 0, d.x) * (w / 2);
                int k = v.Count;
                v.Add(a + p); v.Add(a - p); v.Add(b - p); v.Add(b + p);
                tr.AddRange(new[] { k, k + 1, k + 2, k, k + 2, k + 3, k, k + 2, k + 1, k, k + 3, k + 2 }); // both faces
            }
            var m = new Mesh { indexFormat = v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            m.SetVertices(v); m.SetTriangles(tr, 0);
            m.colors = Fill(v.Count, Color.white);
            m.SetNormals(Up(v.Count)); m.RecalculateBounds();
            return m;
        }

        static Vector3[] Up(int n) { var a = new Vector3[n]; for (int i = 0; i < n; i++) a[i] = Vector3.up; return a; }

        /// <summary>Copies of meshes, placed and tinted, merged into one (vertex colours).</summary>
        static Mesh Merge(List<(Mesh mesh, Matrix4x4 m, Color c)> parts, bool flat = false)
        {
            var v = new List<Vector3>(); var n = new List<Vector3>(); var col = new List<Color>(); var tr = new List<int>();
            foreach (var (mesh, m, c) in parts)
            {
                var mv = mesh.vertices; var mn = mesh.normals; var mt = mesh.triangles;
                if (flat)
                    for (int i = 0; i < mt.Length; i += 3)
                    {
                        var a = m.MultiplyPoint3x4(mv[mt[i]]); var b = m.MultiplyPoint3x4(mv[mt[i + 1]]); var d = m.MultiplyPoint3x4(mv[mt[i + 2]]);
                        var fn = Vector3.Cross(b - a, d - a).normalized;
                        // Convex parts: face away from the part's centre (and wind to match, for culling).
                        if (Vector3.Dot(fn, (a + b + d) / 3 - m.MultiplyPoint3x4(Vector3.zero)) < 0) { fn = -fn; (b, d) = (d, b); }
                        int k = v.Count;
                        v.Add(a); v.Add(b); v.Add(d); n.Add(fn); n.Add(fn); n.Add(fn); col.Add(c); col.Add(c); col.Add(c);
                        tr.Add(k); tr.Add(k + 1); tr.Add(k + 2);
                    }
                else
                {
                    int k = v.Count;
                    for (int i = 0; i < mv.Length; i++) { v.Add(m.MultiplyPoint3x4(mv[i])); n.Add(mn.Length > i ? m.MultiplyVector(mn[i]).normalized : Vector3.up); col.Add(c); }
                    foreach (var t in mt) tr.Add(k + t);
                }
            }
            var o = new Mesh { indexFormat = v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            o.SetVertices(v); o.SetNormals(n); o.SetColors(col); o.SetTriangles(tr, 0);
            o.RecalculateBounds();
            return o;
        }

        /// <summary>A cone of height 1 and radius 1 (base at y -0.5), `seg` sides.</summary>
        static Mesh Cone(int seg)
        {
            var v = new List<Vector3>(); var tr = new List<int>();
            for (int i = 0; i < seg; i++)
            {
                float a0 = i / (float)seg * Mathf.PI * 2, a1 = (i + 1) / (float)seg * Mathf.PI * 2;
                int k = v.Count;
                v.Add(new Vector3(0, 0.5f, 0)); v.Add(new Vector3(Mathf.Cos(a1), -0.5f, Mathf.Sin(a1))); v.Add(new Vector3(Mathf.Cos(a0), -0.5f, Mathf.Sin(a0)));
                tr.Add(k); tr.Add(k + 1); tr.Add(k + 2);
            }
            var m = new Mesh(); m.SetVertices(v); m.SetTriangles(tr, 0); m.RecalculateNormals();
            return m;
        }

        /// <summary>A regular dodecahedron (the icosahedron's dual): its mesh and its pentagon outlines.</summary>
        static (Mesh mesh, List<Vector3> edges) Dodecahedron(float r)
        {
            var ico = Icosahedron(1);
            var iv = ico.vertices; var it = ico.triangles;
            var centers = new List<Vector3>();
            for (int i = 0; i < it.Length; i += 3) centers.Add(((iv[it[i]] + iv[it[i + 1]] + iv[it[i + 2]]) / 3).normalized * r);
            var v = new List<Vector3>(); var tr = new List<int>(); var edges = new List<Vector3>();
            for (int k = 0; k < iv.Length; k++)
            {
                var axis = iv[k].normalized;
                var ring = new List<Vector3>();
                for (int f = 0; f < it.Length / 3; f++)
                    if (it[f * 3] == k || it[f * 3 + 1] == k || it[f * 3 + 2] == k) ring.Add(centers[f]);
                var u = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized; var w = Vector3.Cross(axis, u);
                ring.Sort((a, b) => Mathf.Atan2(Vector3.Dot(a, w), Vector3.Dot(a, u)).CompareTo(Mathf.Atan2(Vector3.Dot(b, w), Vector3.Dot(b, u))));
                int b0 = v.Count;
                v.AddRange(ring);
                for (int i = 1; i + 1 < ring.Count; i++) { tr.Add(b0); tr.Add(b0 + i + 1); tr.Add(b0 + i); }
                for (int i = 0; i < ring.Count; i++) { edges.Add(ring[i]); edges.Add(ring[(i + 1) % ring.Count]); }
            }
            var m = new Mesh(); m.SetVertices(v); m.SetTriangles(tr, 0);
            return (m, edges);
        }

        static Mesh Tetrahedron(float r)
        {
            var v = new[] { new Vector3(1, 1, 1), new Vector3(-1, -1, 1), new Vector3(-1, 1, -1), new Vector3(1, -1, -1) };
            for (int i = 0; i < 4; i++) v[i] = v[i].normalized * r;
            return new Mesh { vertices = v, triangles = new[] { 2, 1, 0, 0, 3, 2, 1, 3, 0, 2, 3, 1 } };
        }

        /// <summary>A mesh with every triangle on its own vertices (flat shading).</summary>
        static Mesh Flat(Mesh m) => Merge(new List<(Mesh, Matrix4x4, Color)> { (m, Matrix4x4.identity, Color.white) }, true);

        static Texture2D SoftDot(Color inner, Color outer)
        {
            const int S = 64;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float d = Mathf.Sqrt((x - S / 2f + 0.5f) * (x - S / 2f + 0.5f) + (y - S / 2f + 0.5f) * (y - S / 2f + 0.5f)) / (S / 2f);
                    var c = Color.Lerp(inner, outer, Mathf.Clamp01(d / 0.45f));
                    c.a *= Mathf.Clamp01(1 - d) * Mathf.Clamp01(1 - d);
                    px[y * S + x] = c;
                }
            tex.SetPixels(px); tex.Apply();
            return tex;
        }

        /// <summary>A sprite mesh whose sprites move: Set(i, centre, colour) then Apply().</summary>
        class MovingSprites
        {
            public readonly Mesh Mesh; readonly Vector3[] v; readonly Color[] c;
            public MovingSprites(int n, float size)
            {
                var list = new List<(Vector3, float, float, float, Color)>();
                for (int i = 0; i < n; i++) list.Add((Vector3.zero, size, 0, 0, Color.clear));
                Mesh = Sprites(list);
                Mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 200);
                v = Mesh.vertices; c = Mesh.colors;
            }
            public void Set(int i, Vector3 p, Color col) { for (int k = 0; k < 4; k++) { v[i * 4 + k] = p; c[i * 4 + k] = col; } }
            public void Apply() { Mesh.vertices = v; Mesh.colors = c; }
        }

        /// <summary>A line mesh whose points move: set Points, then Apply().</summary>
        class MovingLines
        {
            public readonly Mesh Mesh; public readonly Vector3[] Points;
            public MovingLines(int pairs)
            {
                Points = new Vector3[pairs * 2];
                Mesh = Lines(new List<Vector3>(Points));
                Mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 240);
            }
            public void Apply() => Mesh.vertices = Points;
        }

        static TMP_FontAsset serif;

        // ── Math ──

        Look BuildMath()
        {
            var R = new System.Random(13);
            float Rf() => (float)R.NextDouble();
            // Graph paper to the horizon.
            Obj("paper", Ground(120), GridMat("#9db8ff", "#141436", 0.5f, 10, 55, 0.95f, false, 0)).transform.localPosition = new Vector3(0, -0.002f, 0);
            // The axes through the origin, with ticks every metre.
            foreach (var axis in new[] { 'x', 'z' })
            {
                var segs = new List<(float, float, float, float)> { axis == 'x' ? (-45f, 0f, 45f, 0f) : (0f, -45f, 0f, 45f) };
                for (int k = -40; k <= 40; k++) if (Mathf.Abs(k) >= 7) segs.Add(axis == 'x' ? (k, -0.18f, k, 0.18f) : (-0.18f, k, 0.18f, k));
                var c = Draw.Hex(axis == 'x' ? "#ffb86b" : "#6be3d4"); c.a = 0.75f;
                Obj("axis", Strips(segs, 0.05f, axis == 'x' ? 0.003f : 0.004f), Draw.Unlit(c, 2450));
            }
            // A sunflower of golden-angle points around the classroom.
            float golden = Mathf.PI * (3 - Mathf.Sqrt(5));
            var seeds = new List<(Vector3, float, float, float, Color)>();
            for (int n = 0; n < 700; n++)
            {
                float r = 1.05f * Mathf.Sqrt(n + 45), a = n * golden;
                seeds.Add((B(Mathf.Cos(a) * r, 0.03f, Mathf.Sin(a) * r), 0.26f, 0, 0, Draw.Hex("#ffcf6b")));
            }
            var flower = Obj("sunflower", Sprites(seeds), SpriteMat(true, true));
            // Symbols drifting in the air.
            serif ??= Draw.Dynamic("DejaVuSerif") ?? Draw.Font;
            var glyphs = new[] { "π", "∑", "∫", "√", "∞", "∂", "∇", "θ", "φ", "λ", "Δ", "e", "i", "≈", "≠", "≤", "∈", "ℝ", "∀", "∃", "σ", "μ", "ω", "±", "×", "ψ", "ζ", "Ω", "ℕ", "⊂" };
            var symbols = new List<(Transform t, float a, float r, float y, float ph)>();
            foreach (var g in glyphs)
            {
                float a = Rf() * Mathf.PI * 2;
                if (BehindBoard(a, 0.35f)) a += 0.7f;
                var t = Draw.Text(root, g, 1.6f + Rf() * 1.8f, new Color(0.89f, 0.86f, 1f, 0.6f), "center", false, serif);
                float r = 16 + Rf() * 30, y = 3 + Rf() * 13;
                t.transform.localPosition = B(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r);
                t.transform.localRotation = Quaternion.LookRotation(t.transform.localPosition - new Vector3(0, 1.6f, 0));
                symbols.Add((t.transform, a, r, y, Rf() * 6.28f));
            }
            // Solids, turning slowly.
            var dode = Dodecahedron(2.4f);
            var defs = new (Mesh mesh, List<Vector3> edges, string col, Vector3 at)[]
            {
                (dode.mesh, dode.edges, "#ff9e7a", B(-22, 9, -18)),
                (Icosahedron(2.2f), null, "#8fd3ff", B(24, 11, -15)),
                (Torus(2, 0.7f, 40, 16), null, "#c9a6ff", B(-26, 7, 12)),
                (Octahedron(2.4f), null, "#9dffc4", B(21, 8, 19)),
                (Tetrahedron(2.6f), null, "#ffe08a", B(-3, 14, 30)),
            };
            var solids = new List<(Transform t, float y, float ph)>();
            for (int i = 0; i < defs.Length; i++)
            {
                var o = Draw.Child(root, "solid").transform;
                o.localPosition = defs[i].at;
                var face = Draw.Lit(Draw.Hex(defs[i].col), true, 0.4f, 0.05f);
                face.renderQueue = 2000;
                Obj("faces", Flat(defs[i].mesh), face, o);
                Obj("edges", Lines(defs[i].edges ?? Edges(defs[i].mesh)), Draw.Unlit(new Color(1, 1, 1, 0.7f), 2460), o);
                solids.Add((o, defs[i].at.y, i * 1.3f));
            }
            // The horizon is a function: two sums of sines all the way round.
            var waves = new List<(MovingLines l, float r, float y0, float a1, float f1, float a2, float f2)>();
            foreach (var (r, col, y0, a1, f1, a2, f2) in new[] { (60f, "#ffcf6b", 7f, 2.2f, 5f, 0.8f, 13f), (67f, "#9db8ff", 10f, 1.6f, 3f, 0.9f, 8f) })
            {
                var l = new MovingLines(360);
                var c = Draw.Hex(col); c.a = 0.75f;
                Obj("wave", l.Mesh, Draw.Unlit(c, 2460));
                waves.Add((l, r, y0, a1, f1, a2, f2));
            }
            // A trefoil knot, far off to the side.
            var knotPts = new List<Vector3>();
            Vector3 KP(int i) { float u = i / 600f * Mathf.PI * 4, rr = 9 + 3.5f * Mathf.Cos(1.5f * u); return B(rr * Mathf.Cos(u), 3.5f * Mathf.Sin(1.5f * u), rr * Mathf.Sin(u)); }
            for (int i = 0; i < 600; i++) { knotPts.Add(KP(i)); knotPts.Add(KP(i + 1)); }
            var knotC = Draw.Hex("#ffcf6b"); knotC.a = 0.8f;
            var knot = Obj("knot", Lines(knotPts), Draw.Unlit(knotC, 2460)).transform;
            knot.localPosition = B(52, 18, 30);
            anim.Add((time, dt) =>
            {
                flower.transform.localRotation = Quaternion.Euler(0, -time * 0.01f * Mathf.Rad2Deg, 0);
                for (int i = 0; i < symbols.Count; i++)
                {
                    var s = symbols[i];
                    s.a += dt * 0.006f;
                    symbols[i] = s;
                    s.t.localPosition = B(Mathf.Cos(s.a) * s.r, s.y + Mathf.Sin(time * 0.35f + s.ph) * 0.5f, Mathf.Sin(s.a) * s.r);
                    s.t.localRotation = Quaternion.LookRotation(s.t.localPosition - new Vector3(0, 1.6f, 0));
                }
                foreach (var (t, y, ph) in solids)
                {
                    t.localRotation = Quaternion.Euler((time * 0.13f + ph) * Mathf.Rad2Deg, -time * 0.19f * Mathf.Rad2Deg, 0);
                    var p = t.localPosition; p.y = y + Mathf.Sin(time * 0.4f + ph) * 0.6f; t.localPosition = p;
                }
                foreach (var w in waves)
                {
                    for (int i = 0; i < 360; i++)
                    {
                        Vector3 P(int k) { float a = k / 360f * Mathf.PI * 2; return B(Mathf.Cos(a) * w.r, w.y0 + w.a1 * Mathf.Sin(w.f1 * a + time * 0.35f) + w.a2 * Mathf.Sin(w.f2 * a - time * 0.6f), Mathf.Sin(a) * w.r); }
                        w.l.Points[i * 2] = P(i); w.l.Points[i * 2 + 1] = P((i + 1) % 360);
                    }
                    w.l.Apply();
                }
                knot.localRotation = Quaternion.Euler(-(0.5f + Mathf.Sin(time * 0.1f) * 0.2f) * Mathf.Rad2Deg, -time * 0.05f * Mathf.Rad2Deg, 0);
            });
            return new Look
            {
                Sky = SkyMat("#0d0b2a", "#6a5797", "#141436", "#ffd7a0", "#ffd7a0", new Vector3(-0.5f, 0.1f, 1), 0, 0.8f),
                Fog = Draw.Hex("#2a2452"), FogNear = 30, FogFar = 110,
                AmbSky = Draw.Hex("#e2dcff") * 0.7f, AmbGround = Draw.Hex("#141436"),
                SunColor = Draw.Hex("#ffe8c8"), SunIntensity = 0.9f, SunFrom = B(-3, 6, 2),
            };
        }

        // ── Circuits ──

        Look BuildCircuits()
        {
            var R = new System.Random(17);
            float Rf() => (float)R.NextDouble();
            var pcb = Obj("pcb", Ground(200), Solid("#0c3d2c", 0.55f, 0.1f));
            pcb.transform.localPosition = new Vector3(0, -0.004f, 0);
            Obj("silk", Ground(120), GridMat("#2f8a63", "#000000", 2.54f, 4, 50, 0, false, 0)).transform.localPosition = new Vector3(0, -0.002f, 0);
            // Buses of parallel traces routed outward at 45° steps (browser coordinates).
            var paths = new List<List<Vector2>>();
            for (int i = 0; i < 26; i++)
            {
                float a = Rf() * Mathf.PI * 2, r0 = 7 + Rf() * 0.8f;
                float x = Mathf.Cos(a) * r0, z = Mathf.Sin(a) * r0, h = Mathf.Round(a / (Mathf.PI / 4)) * (Mathf.PI / 4);
                var mid = new List<Vector2> { new Vector2(x, z) };
                while (Mathf.Sqrt(x * x + z * z) < 50 && mid.Count < 12)
                {
                    float L = 3 + Rf() * 7;
                    x += Mathf.Cos(h) * L; z += Mathf.Sin(h) * L;
                    mid.Add(new Vector2(x, z));
                    float k = Rf();
                    if (k < 0.3f) h += Mathf.PI / 4; else if (k < 0.6f) h -= Mathf.PI / 4;
                    float outA = Mathf.Atan2(z, x);
                    if (Mathf.Abs(Wrap(h - outA)) > 1.2f) h = Mathf.Round(outA / (Mathf.PI / 4)) * (Mathf.PI / 4);
                }
                int n = 2 + (int)(Rf() * 6);
                for (int j = 0; j < n; j++)
                {
                    float d = (j - (n - 1) / 2f) * 0.32f;
                    var pts = new List<Vector2>();
                    for (int k = 0; k < mid.Count; k++)
                    {
                        var p = mid[k]; var a0 = mid[Mathf.Max(0, k - 1)]; var a1 = mid[Mathf.Min(mid.Count - 1, k + 1)];
                        float t0 = k > 0 ? Mathf.Atan2(p.y - a0.y, p.x - a0.x) : Mathf.Atan2(a1.y - p.y, a1.x - p.x);
                        float t1 = k < mid.Count - 1 ? Mathf.Atan2(a1.y - p.y, a1.x - p.x) : t0;
                        float tm = t0 + Wrap(t1 - t0) / 2, m = d / Mathf.Cos(Wrap(t1 - t0) / 2);
                        pts.Add(new Vector2(p.x - Mathf.Sin(tm) * m, p.y + Mathf.Cos(tm) * m));
                    }
                    int keep = Mathf.Max(2, mid.Count - (int)(Rf() * 3));
                    paths.Add(pts.GetRange(0, keep));
                }
            }
            var segs = new List<(float, float, float, float)>(); var pads = new List<(float, float, float, float)>(); var vias = new List<(Mesh, Matrix4x4, Color)>();
            var viaMesh = Draw.Ring(0.07f, 0.17f, 16);
            foreach (var pts in paths)
            {
                for (int i = 1; i < pts.Count; i++) segs.Add((pts[i - 1].x, pts[i - 1].y, pts[i].x, pts[i].y));
                var e = pts[pts.Count - 1];
                pads.Add((e.x - 0.24f, e.y, e.x + 0.24f, e.y));
                for (int i = 1; i < pts.Count - 1; i++)
                    if (Rf() < 0.18f) vias.Add((viaMesh, Matrix4x4.TRS(B(pts[i].x, 0.006f, pts[i].y), Quaternion.Euler(90, 0, 0), Vector3.one), Color.white));
            }
            var copper = Solid("#d39a4c", 0.7f, 0.85f);
            Obj("traces", Strips(segs, 0.12f, 0.004f), copper);
            Obj("pads", Strips(pads, 0.48f, 0.005f), copper);
            if (vias.Count > 0) Obj("vias", Merge(vias), Draw.Unlit(Draw.Hex("#d8dde3"), 2000));
            // Chips with pins, and capacitors.
            var cube = Draw.Primitive(PrimitiveType.Cube); var cyl = Draw.Primitive(PrimitiveType.Cylinder);
            var bodies = new List<(Mesh, Matrix4x4, Color)>(); var pins = new List<(Mesh, Matrix4x4, Color)>(); var marks = new List<(Mesh, Matrix4x4, Color)>();
            for (int i = 0; i < 18; i++)
            {
                float a = Rf() * Mathf.PI * 2;
                if (BehindBoard(a, 0.3f)) a += 0.6f;
                float r = 14 + Rf() * 26, w = 2 + Rf() * 4, d = 1.6f + Rf() * 3, h = 0.45f + Rf() * 0.5f;
                var rot = Quaternion.Euler(0, Mathf.Round(Rf() * 4) * 45, 0);
                var at = B(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
                var M = Matrix4x4.TRS(at, rot, Vector3.one);
                bodies.Add((cube, M * Matrix4x4.TRS(new Vector3(0, h / 2 + 0.05f, 0), Quaternion.identity, new Vector3(w, h, d)), Draw.Hex("#15171c")));
                marks.Add((Draw.Disc(0.12f, 16), M * Matrix4x4.TRS(new Vector3(-w / 2 + 0.35f, h + 0.06f, d / 2 - 0.35f), Quaternion.Euler(90, 0, 0), Vector3.one), Draw.Hex("#cfd6df")));
                int n = Mathf.Max(2, (int)(w / 0.55f));
                for (int k = 0; k < n; k++) foreach (var side in new[] { -1, 1 })
                        pins.Add((cube, M * Matrix4x4.TRS(new Vector3(-w / 2 + (k + 0.5f) * (w / n), 0.12f, side * (d / 2 + 0.15f)), Quaternion.identity, new Vector3(0.14f, 0.1f, 0.34f)), Draw.Hex("#c9ced6")));
            }
            Obj("chips", Merge(bodies), Solid("#ffffff", 0.45f, 0.05f));
            Obj("pins", Merge(pins), Solid("#ffffff", 0.7f, 0.9f));
            Obj("marks", Merge(marks), Draw.Unlit(Color.white, 2000));
            var caps = new List<(Mesh, Matrix4x4, Color)>();
            for (int i = 0; i < 12; i++)
            {
                float a = Rf() * Mathf.PI * 2;
                if (BehindBoard(a, 0.3f)) a -= 0.6f;
                float r = 20 + Rf() * 28, cr = 0.8f + Rf() * 1.0f, h = 2 + Rf() * 3.5f;
                var at = B(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
                caps.Add((cyl, Matrix4x4.TRS(at + Vector3.up * h / 2, Quaternion.identity, new Vector3(cr * 2, h / 2, cr * 2)), Draw.Hex("#1d3478")));
                caps.Add((cyl, Matrix4x4.TRS(at + Vector3.up * (h + 0.02f), Quaternion.identity, new Vector3(cr * 1.94f, 0.025f, cr * 1.94f)), Draw.Hex("#c8ccd4")));
                // The stripe, on the side facing the classroom.
                var toward = (-at).normalized;
                caps.Add((cube, Matrix4x4.TRS(at + toward * cr * 0.9f + Vector3.up * h / 2, Quaternion.LookRotation(toward), new Vector3(cr * 0.7f, h * 0.96f, cr * 0.25f)), Draw.Hex("#8f98b0")));
            }
            Obj("capacitors", Merge(caps), Solid("#ffffff", 0.6f, 0.2f));
            Obj("ring", Torus(6.5f, 0.04f, 160, 8), copper).transform.localPosition = new Vector3(0, 0.02f, 0);
            // Pulses running out along the traces.
            var lens = new List<float[]>();
            foreach (var pts in paths)
            {
                var c = new float[pts.Count];
                for (int i = 1; i < pts.Count; i++) c[i] = c[i - 1] + Vector2.Distance(pts[i], pts[i - 1]);
                lens.Add(c);
            }
            const int NP = 45;
            var pulses = new MovingSprites(NP, 0.7f);
            var pm = SpriteMat(true, false, SoftDot(new Color(1, 1, 1, 1), new Color(0.55f, 1, 0.78f, 0.8f)));
            Obj("pulses", pulses.Mesh, pm);
            var state = new (int p, float d, float v, Color c)[NP];
            for (int i = 0; i < NP; i++) state[i] = (R.Next(paths.Count), Rf() * 30, 4 + Rf() * 5, Rf() < 0.25f ? Draw.Hex("#ffd27a") : Draw.Hex("#8dffc8"));
            anim.Add((time, dt) =>
            {
                for (int i = 0; i < NP; i++)
                {
                    var s = state[i];
                    s.d += s.v * dt;
                    var c = lens[s.p]; var pts = paths[s.p];
                    if (s.d >= c[c.Length - 1]) { s.p = R.Next(paths.Count); s.d = 0; state[i] = s; continue; }
                    int k = 1;
                    while (c[k] < s.d) k++;
                    float f = (s.d - c[k - 1]) / Mathf.Max(1e-4f, c[k] - c[k - 1]);
                    var q = Vector2.Lerp(pts[k - 1], pts[k], f);
                    pulses.Set(i, B(q.x, 0.08f, q.y), s.c);
                    state[i] = s;
                }
                pulses.Apply();
            });
            return new Look
            {
                Sky = SkyMat("#010604", "#1d5a40", "#03100b", "#ffb45c", "#ffb45c", new Vector3(0.3f, 0.04f, 1), 0, 0.9f),
                Fog = Draw.Hex("#0a2a1e"), FogNear = 24, FogFar = 85,
                AmbSky = Draw.Hex("#c8ffe6") * 0.6f, AmbGround = Draw.Hex("#0c3d2c"),
                SunColor = Draw.Hex("#ffe2b8"), SunIntensity = 1f, SunFrom = B(3, 7, 4),
            };
        }

        // ── Space ──

        static Texture2D PlanetTexture()
        {
            const int W = 512, H = 256;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
            var pal = new[] { Draw.Hex("#e8c9a0"), Draw.Hex("#c98f5a"), Draw.Hex("#f3e2c4"), Draw.Hex("#a8693d"), Draw.Hex("#dcb486") };
            var storm = Draw.Hex("#c0543a");
            var px = new Color[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float u = x / (float)W * Mathf.PI * 2;
                    float yy = y + 4 * Mathf.Sin(u * 3 + y * 0.05f) + 2 * Mathf.Sin(u * 7 - y * 0.11f);
                    float b = Mathf.Clamp01((Mathf.Sin(yy * 0.09f) + Mathf.Sin(yy * 0.23f + 1.3f) * 0.6f + Mathf.Sin(yy * 0.51f) * 0.25f) / 3.7f + 0.5f);
                    int i = Mathf.Min(pal.Length - 2, (int)(b * (pal.Length - 1)));
                    var c = Color.Lerp(pal[i], pal[i + 1], b * (pal.Length - 1) - i);
                    float dx = (x - W * 0.62f) / 26, dy = (y - H * 0.38f) / 12;
                    c = Color.Lerp(c, storm, Mathf.Exp(-(dx * dx + dy * dy)) * 0.7f);
                    px[y * W + x] = c;
                }
            tex.SetPixels(px); tex.Apply();
            return tex;
        }

        /// <summary>A planet's rings: an annulus whose vertex alpha carries the bands.</summary>
        static Mesh RingBands(float r0, float r1, int seg = 160, int bands = 96)
        {
            var v = new List<Vector3>(); var col = new List<Color>(); var tr = new List<int>();
            var cream = Draw.Hex("#d9c9a8");
            for (int j = 0; j <= bands; j++)
            {
                float u = j / (float)bands, r = Mathf.Lerp(r0, r1, u);
                float a = (0.45f + 0.3f * Mathf.Sin(u * 70) + 0.25f * Mathf.Sin(u * 23 + 1)) * Mathf.Min(1, Mathf.Min(u * 8, (1 - u) * 6)) * (u > 0.62f && u < 0.67f ? 0.1f : 1);
                var c = cream; c.a = Mathf.Clamp01(a);
                for (int i = 0; i <= seg; i++)
                {
                    float t = i / (float)seg * Mathf.PI * 2;
                    v.Add(new Vector3(Mathf.Cos(t) * r, 0, Mathf.Sin(t) * r)); col.Add(c);
                }
            }
            for (int j = 0; j < bands; j++)
                for (int i = 0; i < seg; i++)
                {
                    int a = j * (seg + 1) + i, b = a + seg + 1;
                    tr.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
                }
            var m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(v); m.SetColors(col); m.SetTriangles(tr, 0); m.RecalculateBounds();
            return m;
        }

        Look BuildSpace()
        {
            var R = new System.Random(23);
            float Rf() => (float)R.NextDouble();
            // The deck: dark metal with light rings and running lights.
            var deck = Obj("deck", Draw.Primitive(PrimitiveType.Cylinder), Solid("#1b2230", 0.15f, 0.2f));
            deck.transform.localScale = new Vector3(13.2f, 0.075f, 13.2f);
            deck.transform.localPosition = new Vector3(0, -0.076f, 0);
            Obj("rim", Torus(6.55f, 0.05f, 160, 8), Draw.Unlit(Draw.Hex("#5fd3ff"), 2000, false)).transform.localPosition = new Vector3(0, 0.005f, 0);
            foreach (var r in new[] { 4.2f, 2.2f }) Obj("ring", Torus(r, 0.012f, 160, 6), Draw.Unlit(Draw.Hex("#2a3a55"), 2000, false)).transform.localPosition = new Vector3(0, 0.005f, 0);
            const int NL = 48;
            var lightParts = new List<(Mesh, Matrix4x4, Color)>();
            for (int i = 0; i < NL; i++)
            {
                float a = i / (float)NL * Mathf.PI * 2;
                lightParts.Add((Draw.Disc(0.07f, 10), Matrix4x4.TRS(B(Mathf.Cos(a) * 6.62f, 0.02f, Mathf.Sin(a) * 6.62f), Quaternion.Euler(90, 0, 0), Vector3.one), Draw.Hex("#5fd3ff")));
            }
            var lightMesh = Merge(lightParts);
            Obj("lights", lightMesh, Draw.Unlit(Color.white, 2000));
            var lightCols = lightMesh.colors; int perLight = lightCols.Length / NL;
            // Stars all round, and the galaxy's band.
            var tints = new[] { "#ffffff", "#cfe0ff", "#fff4d6", "#ffd2a1", "#aac8ff" };
            var pole = new Vector3(0.25f, 1, 0.35f).normalized;
            var e1 = Vector3.Cross(Vector3.right, pole).normalized; var e2 = Vector3.Cross(pole, e1);
            var stars = new List<(Vector3, float, float, float, Color)>();
            void Layer(int n, float size, bool band)
            {
                for (int i = 0; i < n; i++)
                {
                    Vector3 v;
                    if (band) { float a = Rf() * Mathf.PI * 2, off = (Rf() + Rf() + Rf() - 1.5f) * 0.18f; v = (e1 * Mathf.Cos(a) + e2 * Mathf.Sin(a) + pole * off).normalized; }
                    else { float u = Rf() * 2 - 1, a = Rf() * Mathf.PI * 2, s = Mathf.Sqrt(1 - u * u); v = new Vector3(Mathf.Cos(a) * s, u, Mathf.Sin(a) * s); }
                    var c = Draw.Hex(tints[R.Next(tints.Length)]) * (band ? 0.35f + Rf() * 0.4f : 0.5f + Rf() * 0.5f); c.a = 1;
                    stars.Add((v * 100, size, 0, 0, c));
                }
            }
            Layer(2600, 0.45f, false); Layer(300, 0.8f, false); Layer(3000, 0.3f, true);
            var starMat = SpriteMat(true, true); starMat.renderQueue = 1001;
            Obj("stars", Sprites(stars), starMat);
            // Nebulae along the band.
            var nebCols = new[] { new Color(0.48f, 0.24f, 1f), new Color(1f, 0.25f, 0.66f), new Color(0.18f, 0.84f, 0.78f), new Color(0.31f, 0.47f, 1f) };
            for (int k = 0; k < 4; k++)
            {
                var neb = new List<(Vector3, float, float, float, Color)>();
                for (int i = k; i < 16; i += 4)
                {
                    float a = Rf() * Mathf.PI * 2;
                    var v = (e1 * Mathf.Cos(a) + e2 * Mathf.Sin(a) + pole * (Rf() - 0.5f) * 0.25f).normalized;
                    var c = Color.white; c.a = 0.35f + Rf() * 0.35f;
                    neb.Add((v * 95, 28 + Rf() * 30, 0, 0, c));
                }
                var nm = SpriteMat(true, false, SoftDot(new Color(nebCols[k].r, nebCols[k].g, nebCols[k].b, 0.55f), new Color(nebCols[k].r, nebCols[k].g, nebCols[k].b, 0.2f)));
                nm.renderQueue = 1002;
                Obj("nebula", Sprites(neb), nm);
            }
            // The ringed gas giant and its moon.
            var planet = Draw.Child(root, "planet").transform;
            planet.localPosition = B(62, 22, -72);
            planet.localRotation = Quaternion.Euler(0, 0, -0.35f * Mathf.Rad2Deg);
            var globeMat = Solid("#ffffff", 0, 0); globeMat.mainTexture = PlanetTexture();
            var globe = Obj("globe", Draw.Primitive(PrimitiveType.Sphere), globeMat, planet).transform;
            globe.localScale = Vector3.one * 26;
            var rings = Obj("rings", RingBands(17, 28), Draw.Unlit(Color.white, 2400), planet).transform;
            rings.localRotation = Quaternion.Euler(-0.25f * Mathf.Rad2Deg, 0, 0);
            var moon = Obj("moon", Draw.Primitive(PrimitiveType.Sphere), Solid("#9a9ca3", 0, 0)).transform;
            moon.localScale = Vector3.one * 4.8f;
            // Tumbling asteroids.
            var ico = Icosahedron(1);
            var rv = ico.vertices;
            for (int i = 0; i < rv.Length; i++)
            {
                var p = rv[i];
                float k = 0.75f + 0.5f * Mathf.Abs(Mathf.Sin(p.x * 12.9f + p.y * 78.2f + p.z * 37.7f) * 0.5f + Mathf.Sin(p.x * 4.1f - p.z * 3.3f) * 0.5f);
                rv[i] = p * k;
            }
            ico.vertices = rv;
            var rock = Flat(ico);
            var rockMat = Solid("#7d7468", 0, 0);
            var asteroids = new List<(Transform t, float a, float r, float y, float s, float w)>();
            for (int i = 0; i < 24; i++)
            {
                float a = Rf() * Mathf.PI * 2;
                if (BehindBoard(a, 0.6f)) a += 1.2f;
                var t = Obj("asteroid", rock, rockMat).transform;
                t.localScale = new Vector3(0.4f + Rf() * 1.6f, 0.4f + Rf() * 1.2f, 0.4f + Rf() * 1.4f);
                asteroids.Add((t, a, 26 + Rf() * 26, -14 + Rf() * 22, (Rf() - 0.5f) * 0.6f, 0.002f + Rf() * 0.003f));
            }
            // Now and then, a meteor.
            var met = new MovingLines(1);
            var metMat = Draw.Unlit(new Color(1, 1, 1, 0), 2470);
            Obj("meteor", met.Mesh, metMat);
            float metNext = 3, metAge = 9; Vector3 metP = default, metV = default;
            anim.Add((time, dt) =>
            {
                globe.localRotation = Quaternion.Euler(0, -time * 0.01f * Mathf.Rad2Deg, 0);
                float ma = time * 0.02f;
                moon.localPosition = B(62 + Mathf.Cos(ma) * 26, 22 + Mathf.Sin(ma) * 5, -72 + Mathf.Sin(ma) * 26);
                for (int i = 0; i < asteroids.Count; i++)
                {
                    var s = asteroids[i];
                    s.a += s.w * dt;
                    asteroids[i] = s;
                    s.t.localPosition = B(Mathf.Cos(s.a) * s.r, s.y, Mathf.Sin(s.a) * s.r);
                    s.t.localRotation = Quaternion.Euler(time * s.s * Mathf.Rad2Deg, time * s.s * 0.7f * Mathf.Rad2Deg, 0);
                }
                for (int i = 0; i < NL; i++)
                {
                    float k = Mathf.Pow(Mathf.Max(0, Mathf.Cos(i / (float)NL * Mathf.PI * 2 * 3 - time * 1.5f)), 8);
                    var c = Color.Lerp(Draw.Hex("#1d5f7a"), Draw.Hex("#bff2ff"), k);
                    for (int j = 0; j < perLight; j++) lightCols[i * perLight + j] = c;
                }
                lightMesh.colors = lightCols;
                metNext -= dt;
                if (metNext <= 0)
                {
                    metNext = 4 + Rf() * 6; metAge = 0;
                    float a = Rf() * Mathf.PI * 2;
                    metP = B(Mathf.Cos(a) * 80, 25 + Rf() * 35, Mathf.Sin(a) * 80);
                    metV = B(-Mathf.Sin(a), 0, Mathf.Cos(a)) * (45 * (Rf() < 0.5f ? 1 : -1)); metV.y = -15;
                }
                metAge += dt;
                if (metAge < 1.2f)
                {
                    metP += metV * dt;
                    met.Points[0] = metP; met.Points[1] = metP - metV * 0.12f; met.Apply();
                    metMat.color = new Color(1, 1, 1, Mathf.Sin(metAge / 1.2f * Mathf.PI) * 0.9f);
                }
                else metMat.color = new Color(1, 1, 1, 0);
            });
            return new Look
            {
                Sky = SkyMat("#000004", "#05051a", "#000004", "#fff1d6", "#fff1d6", new Vector3(-0.6f, 0.22f, 0.75f), 0.012f, 0.7f),
                Fog = Draw.Hex("#02020a"), FogNear = 60, FogFar = 160,
                AmbSky = Draw.Hex("#8a9ad0") * 0.5f, AmbGround = Draw.Hex("#05050f"),
                SunColor = Draw.Hex("#fff4e0"), SunIntensity = 1f, SunFrom = B(-6, 2.2f, -7.5f),
            };
        }

        // ── Lakeside ──

        Look BuildLakeside()
        {
            var R = new System.Random(31);
            float Rf() => (float)R.NextDouble();
            var sunDirB = new Vector3(-0.35f, 0.07f, -1).normalized; // browser
            float sunAz = Mathf.Atan2(sunDirB.z, sunDirB.x);
            var wm = Mat("Vcl/Water", 1995); // before the (depth-less) lit dock, land and trees, which then draw over it
            wm.SetColor("_Deep", Draw.Hex("#1d4258")); wm.SetColor("_Horizon", Draw.Hex("#ffc995")); wm.SetColor("_Top", Draw.Hex("#4a78b4"));
            wm.SetColor("_Sun", Draw.Hex("#fff0c8")); wm.SetVector("_SunDir", B(sunDirB.x, sunDirB.y, sunDirB.z));
            Obj("water", Ground(260), wm).transform.localPosition = new Vector3(0, -0.4f, 0);
            // The dock.
            var cube = Draw.Primitive(PrimitiveType.Cube); var cyl = Draw.Primitive(PrimitiveType.Cylinder);
            var woods = new[] { "#8b6a4a", "#7a5c3f", "#94734f", "#6f5339" };
            var planks = new List<(Mesh, Matrix4x4, Color)>();
            for (float z = -7.2f; z < 6.8f; z += 0.3f)
                planks.Add((cube, Matrix4x4.TRS(B((Rf() - 0.5f) * 0.15f, -0.045f + (Rf() - 0.5f) * 0.008f, z + 0.14f), Quaternion.identity, new Vector3(13 + (Rf() - 0.5f) * 0.3f, 0.08f, 0.27f)), Draw.Hex(woods[R.Next(woods.Length)])));
            for (float x = -6.4f; x <= 6.4f; x += 12.8f)
                for (float z = -7; z <= 6.7f; z += 2.75f)
                    planks.Add((cyl, Matrix4x4.TRS(B(x, -0.45f, z), Quaternion.identity, new Vector3(0.28f, 0.65f, 0.28f)), Draw.Hex("#5a4430")));
            Obj("dock", Merge(planks), Solid("#ffffff", 0.15f, 0));
            // Far shore with a ragged edge.
            float Edge(float a) => 36 + 6 * Mathf.Sin(a * 3 + 1) + 3 * Mathf.Sin(a * 7) + 2 * Mathf.Sin(a * 13 + 2);
            {
                const int seg = 160;
                var v = new List<Vector3>(); var tr = new List<int>();
                for (int i = 0; i <= seg; i++)
                {
                    float a = i / (float)seg * Mathf.PI * 2;
                    foreach (var r in new[] { Edge(a), 60f, 100f, 140f }) v.Add(B(Mathf.Cos(a) * r, -0.3f, Mathf.Sin(a) * r));
                }
                for (int i = 0; i < seg; i++)
                    for (int j = 0; j < 3; j++)
                    {
                        int a = i * 4 + j, b = a + 4;
                        tr.AddRange(new[] { a, a + 1, b, b, a + 1, b + 1, a, b, a + 1, b, b + 1, a + 1 });
                    }
                var land = new Mesh(); land.SetVertices(v); land.SetTriangles(tr, 0); land.colors = Fill(v.Count, Color.white);
                land.SetNormals(Up(v.Count));
                Obj("land", land, Solid("#2b4430", 0, 0));
            }
            // Pines (none in front of the sun).
            var cone = Cone(7);
            var greens = new[] { "#1d3526", "#24402d", "#2a4a33", "#1a2f22" };
            var trees = new List<(Mesh, Matrix4x4, Color)>();
            for (int i = 0; i < 400 && trees.Count < 260; i++)
            {
                float a = Rf() * Mathf.PI * 2;
                if (Mathf.Abs(Wrap(a - sunAz)) < 0.22f) continue;
                float r = Edge(a) + 3 + Rf() * 30, h = 3.5f + Rf() * 5, w = h * (0.22f + Rf() * 0.08f);
                trees.Add((cone, Matrix4x4.TRS(B(Mathf.Cos(a) * r, -0.3f + h / 2, Mathf.Sin(a) * r), Quaternion.identity, new Vector3(w, h, w)), Draw.Hex(greens[R.Next(greens.Length)])));
            }
            Obj("pines", Merge(trees, true), Solid("#ffffff", 0, 0));
            // Mountain ridges, hazier with distance, low where the sun goes down.
            foreach (var (r, col, k, s) in new[] { (106f, "#a596b4", 0.65f, 3.1f), (94f, "#7a7c9e", 0.8f, 1.7f), (82f, "#53607c", 1.0f, 0.3f) })
            {
                const int n = 256;
                var v = new List<Vector3>(); var tr = new List<int>();
                for (int i = 0; i <= n; i++)
                {
                    float a = i / (float)n * Mathf.PI * 2, da = Wrap(a - sunAz);
                    float h = (Mathf.Abs(Mathf.Sin(a * 2 + s)) * 13 + Mathf.Abs(Mathf.Sin(a * 5 + s * 2)) * 6 + Mathf.Abs(Mathf.Sin(a * 11 + s)) * 2) * k * (1 - 0.75f * Mathf.Exp(-da * da * 10));
                    v.Add(B(Mathf.Cos(a) * r, -0.4f, Mathf.Sin(a) * r)); v.Add(B(Mathf.Cos(a) * r, h + 2, Mathf.Sin(a) * r));
                    if (i < n) tr.AddRange(new[] { i * 2, i * 2 + 1, i * 2 + 2, i * 2 + 1, i * 2 + 3, i * 2 + 2 });
                }
                var m = new Mesh(); m.SetVertices(v); m.SetTriangles(tr, 0); m.colors = Fill(v.Count, Color.white); m.RecalculateBounds();
                var rm = Draw.Unlit(Draw.Hex(col), 1990, true);
                rm.SetFloat("_ZWrite", 1); // opaque queues draw front to back
                Obj("ridge", m, rm);
            }
            // Reeds by the dock, swaying.
            var clumps = new List<(Transform t, float ph)>();
            foreach (var (x, z) in new[] { (-7.3f, -6.4f), (7.2f, 4.8f), (-7.1f, 2.6f), (7.0f, -3.9f), (-2.5f, 7.4f), (3.4f, 7.3f) })
            {
                var parts = new List<(Mesh, Matrix4x4, Color)>();
                for (int i = 0; i < 8; i++)
                {
                    float h = 1.1f + Rf() * 0.8f, ox = (Rf() - 0.5f) * 0.7f, oz = (Rf() - 0.5f) * 0.7f;
                    var tilt = Quaternion.Euler((Rf() - 0.5f) * 8, 0, (Rf() - 0.5f) * 8);
                    parts.Add((cyl, Matrix4x4.TRS(new Vector3(ox, h / 2, oz), tilt, new Vector3(0.03f, h / 2, 0.03f)), Draw.Hex("#5d6b3a")));
                    if (Rf() < 0.6f) parts.Add((cyl, Matrix4x4.TRS(new Vector3(ox, h - 0.2f, oz), tilt, new Vector3(0.09f, 0.12f, 0.09f)), Draw.Hex("#5a3b22")));
                }
                var c = Obj("reeds", Merge(parts), Solid("#ffffff", 0.1f, 0)).transform;
                c.localPosition = B(x, -0.4f, z);
                clumps.Add((c, Rf() * 6.28f));
            }
            // Fireflies over the water near the dock.
            const int NF = 60;
            var flies = new MovingSprites(NF, 0.16f);
            Obj("fireflies", flies.Mesh, SpriteMat(true, false, SoftDot(new Color(1, 1, 0.86f, 1), new Color(1, 0.89f, 0.48f, 0.7f))));
            var fs = new (float x, float z, float y, float ph, float f)[NF];
            for (int i = 0; i < NF; i++) { float a = Rf() * Mathf.PI * 2, r = 7.5f + Rf() * 9; fs[i] = (Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0.2f + Rf() * 2.2f, Rf() * 6.28f, 0.4f + Rf() * 0.7f); }
            // Birds circling far off.
            const int NB = 6;
            var birdLines = new MovingLines(NB * 2);
            Obj("birds", birdLines.Mesh, Draw.Unlit(Draw.Hex("#2a2230"), 2470));
            var birds = new (float a, float r, float y, float v, float ph, float s)[NB];
            for (int i = 0; i < NB; i++) birds[i] = (Rf() * 6.28f, 42 + Rf() * 18, 15 + Rf() * 9, 0.03f + Rf() * 0.02f, Rf() * 6.28f, 0.7f + Rf() * 0.4f);
            // A few warm clouds, low and far.
            var clouds = new List<(Vector3, float, float, float, Color)>();
            for (int i = 0; i < 9; i++)
            {
                float a = Rf() * Mathf.PI * 2, r = 70 + Rf() * 20;
                var c = Draw.Hex("#ffd2b8"); c.a = 0.75f;
                clouds.Add((B(Mathf.Cos(a) * r, 18 + Rf() * 14, Mathf.Sin(a) * r), 30 + Rf() * 20, 9 + Rf() * 5, 0, c));
            }
            var cloudMat = SpriteMat(false, false, CloudTexture(41));
            cloudMat.renderQueue = 2400;
            var cloudObj = Obj("clouds", Sprites(clouds), cloudMat);
            anim.Add((time, dt) =>
            {
                foreach (var (t, ph) in clumps) t.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(time * 0.8f + ph) * 2f);
                for (int i = 0; i < NF; i++)
                {
                    var u = fs[i];
                    var p = B(u.x + Mathf.Sin(time * 0.3f * u.f + u.ph) * 0.8f, u.y + Mathf.Sin(time * 0.5f * u.f + u.ph * 2) * 0.3f, u.z + Mathf.Cos(time * 0.27f * u.f + u.ph) * 0.8f);
                    flies.Set(i, p, new Color(1, 1, 1, Mathf.Pow(Mathf.Max(0, Mathf.Sin(time * u.f + u.ph)), 6)));
                }
                flies.Apply();
                for (int i = 0; i < NB; i++)
                {
                    var b = birds[i];
                    b.a += b.v * dt; birds[i] = b;
                    float cx = Mathf.Cos(b.a) * b.r, cz = Mathf.Sin(b.a) * b.r, fx = -Mathf.Sin(b.a), fz = Mathf.Cos(b.a);
                    float sx = -fz, sz = fx, flap = Mathf.Sin(time * 5 + b.ph) * 0.5f * b.s, span = 1.1f * b.s;
                    var c = B(cx, b.y, cz);
                    birdLines.Points[i * 4] = B(cx + sx * span, b.y + flap, cz + sz * span); birdLines.Points[i * 4 + 1] = c;
                    birdLines.Points[i * 4 + 2] = c; birdLines.Points[i * 4 + 3] = B(cx - sx * span, b.y + flap, cz - sz * span);
                }
                birdLines.Apply();
                cloudObj.transform.localRotation = Quaternion.Euler(0, -time * 0.17f, 0);
            });
            return new Look
            {
                Sky = SkyMat("#3e6fae", "#ffc58f", "#f6c9a2", "#fff1c8", "#fff1c8", B(sunDirB.x, sunDirB.y, sunDirB.z), 0.035f, 1.8f),
                Fog = Draw.Hex("#f1c39c"), FogNear = 45, FogFar = 150,
                AmbSky = Draw.Hex("#ffe0bd") * 0.65f, AmbGround = Draw.Hex("#4a5e48") * 0.7f,
                SunColor = Draw.Hex("#ffd49a"), SunIntensity = 1.1f, SunFrom = B(-3.5f, 1.2f, -10),
            };
        }
    }
}
