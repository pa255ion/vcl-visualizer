// Stroke/fill/arrowhead/text building blocks: a port of viewer/js/draw.js and
// text.js.
//
// Coordinates: board units, Unity axes (x right, y up, z AWAY from the
// learner). IR points have z toward the learner; Runtime.B() flips it.
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Vcl
{
    public static class Draw
    {
        /// <summary>Board units → metres.</summary>
        public const float StageScale = 0.15f;

        public static readonly Color FG = Hex("#e8edf3");

        static Shader unlit, lit;
        public static Shader UnlitShader => unlit ? unlit : unlit = Shader.Find("Vcl/Unlit");
        public static Shader LitShader => lit ? lit : lit = Shader.Find("Vcl/Lit");

        public static Material Unlit(Color c, int queue = 3002, bool cullOff = true)
        {
            var m = new Material(UnlitShader) { color = c, renderQueue = queue };
            m.SetFloat("_Cull", cullOff ? 0 : 2);
            return m;
        }

        public static Material Lit(Color c, bool doubleSided = false, float smooth = 0.55f, float metal = 0.05f)
        {
            var m = new Material(LitShader) { color = c, renderQueue = 3000 };
            m.SetFloat("_Cull", doubleSided ? 0 : 2);
            m.SetFloat("_Glossiness", smooth);
            m.SetFloat("_Metallic", metal);
            return m;
        }

        public static Color Hex(string s, Color? fallback = null)
        {
            if (s != null && ColorUtility.TryParseHtmlString(s, out var c)) return c;
            return fallback ?? FG;
        }

        /// <summary>Destroy (play mode) or DestroyImmediate (edit-mode snapshots).</summary>
        public static void Kill(Object o)
        {
            if (!o) return;
            if (Application.isPlaying) Object.Destroy(o); else Object.DestroyImmediate(o);
        }

        public static GameObject Child(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go;
        }

        /// <summary>Transparent things draw in sortingOrder: board -10, fills 1, strokes 2, text 3, subtitles 6.</summary>
        public static MeshRenderer MeshObject(Transform parent, string name, Mesh mesh, Material mat, int order = 1)
        {
            var go = Child(parent, name);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sortingOrder = order;
            return r;
        }

        /// <summary>Unity's primitive meshes have no vertex colours; the shaders multiply by them.</summary>
        public static Mesh WithWhite(Mesh m)
        {
            var cols = new Color[m.vertexCount];
            for (int i = 0; i < cols.Length; i++) cols[i] = Color.white;
            m.colors = cols;
            return m;
        }

        static readonly Dictionary<PrimitiveType, Mesh> prims = new Dictionary<PrimitiveType, Mesh>();
        public static Mesh Primitive(PrimitiveType t)
        {
            if (prims.TryGetValue(t, out var m) && m) return m;
            var go = GameObject.CreatePrimitive(t);
            m = Object.Instantiate(go.GetComponent<MeshFilter>().sharedMesh);
            Object.DestroyImmediate(go);
            return prims[t] = WithWhite(m);
        }

        // ── polylines ──

        public static float Length(List<Vector3> pl)
        {
            float L = 0;
            for (int i = 0; i + 1 < pl.Count; i++) L += Vector3.Distance(pl[i], pl[i + 1]);
            return L;
        }

        /// <summary>First fraction p (by arc length) of a list of polylines.</summary>
        public static List<List<Vector3>> Partial(List<List<Vector3>> polys, float p)
        {
            if (p >= 1) return polys;
            float total = 0;
            foreach (var pl in polys) total += Length(pl);
            float left = total * Mathf.Max(0, p);
            var outp = new List<List<Vector3>>();
            foreach (var pl in polys)
            {
                if (left <= 0) break;
                var cur = new List<Vector3> { pl[0] };
                for (int i = 0; i + 1 < pl.Count; i++)
                {
                    float d = Vector3.Distance(pl[i], pl[i + 1]);
                    if (d >= left)
                    {
                        cur.Add(Vector3.Lerp(pl[i], pl[i + 1], d > 0 ? left / d : 0));
                        left = 0;
                        break;
                    }
                    cur.Add(pl[i + 1]);
                    left -= d;
                }
                outp.Add(cur);
            }
            return outp;
        }

        /// <summary>End point and direction of a polyline set, or null.</summary>
        public static (Vector3 pos, Vector3 dir)? Tip(List<List<Vector3>> polys)
        {
            if (polys.Count == 0) return null;
            var pl = polys[polys.Count - 1];
            if (pl.Count < 2) return null;
            var d = pl[pl.Count - 1] - pl[pl.Count - 2];
            if (d.sqrMagnitude < 1e-12f) return null;
            return (pl[pl.Count - 1], d.normalized);
        }

        public static List<Vector3> TrimEnd(List<Vector3> pl, float d)
        {
            var o = new List<Vector3>(pl);
            while (o.Count >= 2 && d > 0)
            {
                var a = o[o.Count - 2];
                var b = o[o.Count - 1];
                float L = Vector3.Distance(a, b);
                if (L > d) { o[o.Count - 1] = Vector3.Lerp(b, a, d / L); return o; }
                o.RemoveAt(o.Count - 1);
                d -= L;
            }
            return o;
        }

        public static List<Vector3> TrimStart(List<Vector3> pl, float d)
        {
            var r = new List<Vector3>(pl);
            r.Reverse();
            r = TrimEnd(r, d);
            r.Reverse();
            return r;
        }

        public static List<Vector3> CirclePoints(float r, int n = 96)
        {
            var pts = new List<Vector3>(n + 1);
            for (int i = 0; i <= n; i++)
            {
                float a = i / (float)n * Mathf.PI * 2;
                pts.Add(new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0));
            }
            return pts;
        }

        /// <summary>Closed outline of a w×h rectangle with corner radius r, centred.</summary>
        public static List<Vector3> RoundedRect(float w, float h, float r)
        {
            r = Mathf.Max(0, Mathf.Min(r, Mathf.Min(w / 2, h / 2)));
            float x = -w / 2, y = -h / 2;
            var pts = new List<Vector3>();
            void Corner(float cx, float cy, float a0)
            {
                const int n = 10;
                for (int i = 0; i <= n; i++)
                {
                    float a = a0 + i / (float)n * Mathf.PI / 2;
                    pts.Add(new Vector3(cx + Mathf.Cos(a) * r, cy + Mathf.Sin(a) * r, 0));
                }
            }
            if (r <= 0)
            {
                pts.Add(new Vector3(x, y)); pts.Add(new Vector3(x + w, y));
                pts.Add(new Vector3(x + w, y + h)); pts.Add(new Vector3(x, y + h));
            }
            else
            {
                Corner(x + w - r, y + r, -Mathf.PI / 2);
                Corner(x + w - r, y + h - r, 0);
                Corner(x + r, y + h - r, Mathf.PI / 2);
                Corner(x + r, y + r, Mathf.PI);
            }
            pts.Add(pts[0]);
            return pts;
        }

        /// <summary>"Nice" tick step for a range (1, 2, 5 × 10^k).</summary>
        /// <summary>Log-axis ticks over r (r[0] &gt; 0), as viewer/js/draw.js logTicks.</summary>
        public static List<double> LogTicks(double[] r)
        {
            int k0 = (int)System.Math.Floor(System.Math.Log10(r[0]) + 1e-9), k1 = (int)System.Math.Ceiling(System.Math.Log10(r[1]) - 1e-9);
            double decades = System.Math.Log10(r[1] / r[0]);
            int[] mults = decades >= 3 ? new[] { 1 } : decades >= 1 ? new[] { 1, 2, 5 } : new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
            int step = System.Math.Max(1, (int)System.Math.Ceiling(decades / 7));
            var outv = new List<double>();
            for (int k = k0; k <= k1; k++)
            {
                if (mults.Length == 1 && (k - k0) % step != 0) continue;
                foreach (int m in mults)
                {
                    double v = double.Parse((m * System.Math.Pow(10, k)).ToString("G12", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
                    if (v >= r[0] * (1 - 1e-9) && v <= r[1] * (1 + 1e-9)) outv.Add(v);
                }
            }
            if (outv.Count < 2)
            {
                outv.Clear();
                double st = NiceStep(r[1] - r[0], 4);
                for (double v = System.Math.Ceiling(r[0] / st - 1e-9) * st; v <= r[1] + 1e-9; v += st) outv.Add(v);
            }
            return outv;
        }

        public static double NiceStep(double span, double target = 6)
        {
            double raw = span / target;
            double mag = System.Math.Pow(10, System.Math.Floor(System.Math.Log10(raw)));
            double n = raw / mag;
            return (n < 1.5 ? 1 : n < 3.5 ? 2 : n < 7.5 ? 5 : 10) * mag;
        }

        public static string FmtTick(double v)
        {
            // Very large or small magnitudes (log axes) as powers of ten: 10⁻⁵, 3×10⁶.
            if (v != 0 && (System.Math.Abs(v) >= 1e5 || System.Math.Abs(v) < 1e-3))
            {
                int k = (int)System.Math.Floor(System.Math.Log10(System.Math.Abs(v)) + 1e-9);
                double m = System.Math.Round(v / System.Math.Pow(10, k) * 100) / 100;
                var sup = new System.Text.StringBuilder();
                foreach (char ch in k.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    sup.Append("⁻⁰¹²³⁴⁵⁶⁷⁸⁹"["-0123456789".IndexOf(ch)]);
                string mant = m == 1 ? "" : m == -1 ? "-" : m.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";
                return mant + "10" + sup;
            }
            double r = System.Math.Round(v * 1000) / 1000;
            if (r == 0) return "0";
            return r.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        // ── meshes ──

        /// <summary>Flat filled polygon (ear clipping), z from the first point.</summary>
        public static Mesh Fill(List<Vector3> pts)
        {
            var p = new List<Vector3>(pts);
            if (p.Count > 1 && (p[0] - p[p.Count - 1]).sqrMagnitude < 1e-10f) p.RemoveAt(p.Count - 1);
            var mesh = new Mesh();
            if (p.Count < 3) return mesh;
            // Orientation (shoelace); ears are convex vertices with no other vertex inside.
            float area = 0;
            for (int i = 0; i < p.Count; i++)
            {
                var a = p[i];
                var b = p[(i + 1) % p.Count];
                area += a.x * b.y - b.x * a.y;
            }
            float sign = area >= 0 ? 1 : -1;
            var idx = new List<int>();
            for (int i = 0; i < p.Count; i++) idx.Add(i);
            var tris = new List<int>();
            int guard = 0;
            while (idx.Count > 3 && guard++ < 10000)
            {
                bool clipped = false;
                for (int k = 0; k < idx.Count; k++)
                {
                    int i0 = idx[(k + idx.Count - 1) % idx.Count], i1 = idx[k], i2 = idx[(k + 1) % idx.Count];
                    Vector2 a = p[i0], b = p[i1], c = p[i2];
                    float cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                    if (cross * sign <= 1e-9f) continue;
                    bool inside = false;
                    foreach (int j in idx)
                    {
                        if (j == i0 || j == i1 || j == i2) continue;
                        if (InTri(p[j], a, b, c)) { inside = true; break; }
                    }
                    if (inside) continue;
                    tris.Add(i0); tris.Add(i1); tris.Add(i2);
                    idx.RemoveAt(k);
                    clipped = true;
                    break;
                }
                if (!clipped) break; // degenerate: fan the rest
            }
            for (int k = 1; k + 1 < idx.Count; k++) { tris.Add(idx[0]); tris.Add(idx[k]); tris.Add(idx[k + 1]); }
            float z = p[0].z;
            var verts = new Vector3[p.Count];
            for (int i = 0; i < p.Count; i++) verts[i] = new Vector3(p[i].x, p[i].y, z);
            mesh.vertices = verts;
            mesh.triangles = tris.ToArray();
            WithWhite(mesh);
            mesh.RecalculateBounds();
            return mesh;
        }

        static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        /// <summary>Cone along +y with its tip at the origin.</summary>
        public static Mesh Cone(float radius, float height, int n = 20)
        {
            var v = new List<Vector3> { Vector3.zero, new Vector3(0, -height, 0) };
            for (int i = 0; i < n; i++)
            {
                float a = i / (float)n * Mathf.PI * 2;
                v.Add(new Vector3(Mathf.Cos(a) * radius, -height, Mathf.Sin(a) * radius));
            }
            var t = new List<int>();
            for (int i = 0; i < n; i++)
            {
                int a = 2 + i, b = 2 + (i + 1) % n;
                t.Add(0); t.Add(b); t.Add(a);
                t.Add(1); t.Add(a); t.Add(b);
            }
            var m = new Mesh { vertices = v.ToArray(), triangles = t.ToArray() };
            WithWhite(m);
            m.RecalculateNormals();
            return m;
        }

        static readonly Dictionary<int, Mesh> discs = new Dictionary<int, Mesh>();
        /// <summary>Filled circle of radius r in the xy plane (cached per radius).</summary>
        public static Mesh Disc(float r, int n = 32)
        {
            int key = Mathf.RoundToInt(r * 10000) * 1000 + n;
            if (discs.TryGetValue(key, out var m) && m) return m;
            var v = new List<Vector3> { Vector3.zero };
            var t = new List<int>();
            for (int i = 0; i < n; i++)
            {
                float a = i / (float)n * Mathf.PI * 2;
                v.Add(new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0));
                t.Add(0); t.Add(1 + i); t.Add(1 + (i + 1) % n);
            }
            m = new Mesh { vertices = v.ToArray(), triangles = t.ToArray() };
            WithWhite(m);
            m.RecalculateBounds();
            return discs[key] = m;
        }

        /// <summary>Annulus r0..r1 in the xy plane.</summary>
        public static Mesh Ring(float r0, float r1, int n = 40)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            for (int i = 0; i < n; i++)
            {
                float a = i / (float)n * Mathf.PI * 2;
                var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0);
                v.Add(d * r0); v.Add(d * r1);
                int j = (i + 1) % n;
                t.AddRange(new[] { 2 * i, 2 * i + 1, 2 * j, 2 * j, 2 * i + 1, 2 * j + 1 });
            }
            var m = new Mesh { vertices = v.ToArray(), triangles = t.ToArray() };
            WithWhite(m);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>A unit quad scaled to the rectangle with corners a and b.</summary>
        public static void SetQuad(Transform q, Vector3 a, Vector3 b)
        {
            q.localPosition = (a + b) / 2;
            q.localScale = new Vector3(Mathf.Max(1e-4f, Mathf.Abs(b.x - a.x)), Mathf.Max(1e-4f, Mathf.Abs(b.y - a.y)), 1);
        }

        public static Transform QuadObj(Transform parent, Material mat, int order = 1) =>
            MeshObject(parent, "quad", Primitive(PrimitiveType.Quad), mat, order).transform;

        // ── text ──

        static TMP_FontAsset font, mono;

        /// <summary>A dynamic SDF font asset made at runtime from a TTF in Resources/Fonts.</summary>
        static TMP_FontAsset Dynamic(string name)
        {
            var ttf = Resources.Load<Font>("Fonts/" + name);
            return ttf ? TMP_FontAsset.CreateFontAsset(ttf, 72, 7, UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024) : null;
        }

        /// <summary>LiberationSans, falling back to DejaVu Sans for what it lacks (⊕ ≪ ₀ ᴸ …).</summary>
        public static TMP_FontAsset Font
        {
            get
            {
                if (font) return font;
                font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
                var fb = Dynamic("DejaVuSans");
                if (font && fb)
                {
                    font.fallbackFontAssetTable ??= new List<TMP_FontAsset>();
                    font.fallbackFontAssetTable.RemoveAll(f => f == null || f.name.StartsWith("DejaVuSans"));
                    font.fallbackFontAssetTable.Add(fb);
                }
                return font;
            }
        }

        /// <summary>DejaVu Sans Mono (code panels), falling back to the main font.</summary>
        public static TMP_FontAsset Mono
        {
            get
            {
                if (mono) return mono;
                mono = Dynamic("DejaVuSansMono");
                if (mono && Font) mono.fallbackFontAssetTable = new List<TMP_FontAsset> { Font };
                return mono ? mono : Font;
            }
        }

        /// <summary>World text; `size` is the line height in board units (as text.js).</summary>
        public static TextMeshPro Text(Transform parent, string text, float size, Color color, string align = "center", bool bold = true, TMP_FontAsset face = null)
        {
            var go = Child(parent, "text");
            var t = go.AddComponent<TextMeshPro>();
            if (face ?? Font) t.font = face ?? Font;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow;
            t.fontSize = size * 10f / 1.2f; // TMP: fontSize 10 ≈ 1 unit per line
            t.color = color;
            t.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            t.alignment = align == "left" ? TextAlignmentOptions.MidlineLeft : align == "right" ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.Midline;
            t.rectTransform.sizeDelta = Vector2.zero;
            t.rectTransform.pivot = new Vector2(align == "left" ? 0 : align == "right" ? 1 : 0.5f, 0.5f);
            t.text = text;
            var r = t.GetComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            t.renderer.sortingOrder = 3;
            // Unity's +z faces away from the learner; text faces -z by default, toward them.
            return t;
        }

        public static Vector2 TextSize(TextMeshPro t)
        {
            var v = t.GetPreferredValues(t.text);
            return v;
        }

        static Texture2D dashTex;
        public static Texture2D DashTexture
        {
            get
            {
                if (dashTex) return dashTex;
                dashTex = new Texture2D(16, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
                for (int i = 0; i < 16; i++) dashTex.SetPixel(i, 0, i < 10 ? Color.white : new Color(1, 1, 1, 0));
                dashTex.Apply();
                return dashTex;
            }
        }
    }

    /// <summary>Thick polylines with a width in board units (one local-space LineRenderer per polyline).</summary>
    public class Stroke
    {
        public readonly GameObject Obj;
        public readonly Material Material;
        public float Width;
        readonly bool dash, faceView;
        readonly List<LineRenderer> lines = new List<LineRenderer>();
        public int Order = 2;

        public Stroke(Transform parent, Color color, float width, bool dash = false, bool faceView = false)
        {
            Obj = Draw.Child(parent, "stroke");
            Width = width;
            this.dash = dash;
            this.faceView = faceView;
            Material = Draw.Unlit(color, 3002);
            if (dash)
            {
                Material.mainTexture = Draw.DashTexture;
                // Tile mode repeats the texture once per world unit: one dash period = 0.36 board units.
                Material.mainTextureScale = new Vector2(1f / (0.36f * Draw.StageScale), 1);
            }
        }

        public void Set(List<List<Vector3>> polys)
        {
            int n = 0;
            foreach (var pl in polys)
            {
                if (pl.Count < 2) continue;
                if (n == lines.Count) lines.Add(NewLine());
                var lr = lines[n++];
                lr.enabled = true;
                lr.positionCount = pl.Count;
                lr.SetPositions(pl.ToArray());
            }
            for (int i = n; i < lines.Count; i++) lines[i].enabled = false;
            UpdateWidth();
        }

        /// <summary>Flat (TransformZ) lines are scaled by their transform, so the width stays in board
        /// units; camera-facing (View) lines take it in world units, so scale it here.</summary>
        public void UpdateWidth()
        {
            float w = faceView ? Width * Obj.transform.lossyScale.x : Width;
            foreach (var lr in lines) lr.widthMultiplier = w;
        }

        LineRenderer NewLine()
        {
            var go = Draw.Child(Obj.transform, "line");
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.sharedMaterial = Material;
            lr.alignment = faceView ? LineAlignment.View : LineAlignment.TransformZ;
            lr.numCornerVertices = 3;
            lr.numCapVertices = 3;
            lr.textureMode = dash ? LineTextureMode.Tile : LineTextureMode.Stretch;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.startColor = lr.endColor = Color.white;
            lr.sortingOrder = Order;
            return lr;
        }
    }

    /// <summary>Cone arrowhead whose tip sits at a point.</summary>
    public class Head
    {
        public readonly GameObject Obj;
        public readonly Material Material;

        public Head(Transform parent, Color color, float size)
        {
            Material = Draw.Unlit(color, 3002);
            Obj = Draw.MeshObject(parent, "head", Draw.Cone(size * 0.42f, size), Material, 2).gameObject;
        }

        public void Place((Vector3 pos, Vector3 dir)? tip)
        {
            Obj.SetActive(tip.HasValue);
            if (!tip.HasValue) return;
            Obj.transform.localPosition = tip.Value.pos;
            Obj.transform.localRotation = Quaternion.FromToRotation(Vector3.up, tip.Value.dir);
        }
    }
}
