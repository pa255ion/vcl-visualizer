// Scenery: the world around the board, the Unity twin of viewer/js/scenery.js.
// Each scene sets the sky, fog, light and floor and adds a few slow-moving
// things far enough away not to compete with the board. All generated here
// (Resources/Vcl*.shader, procedural meshes and textures): nothing to download
// or license. The browser's -z is this app's +z (the board is at +z here).
//
//   night  the plain dark room (the original look)
//   aero   bright sky, glossy platform, green hills, clouds and soap bubbles
//   tech   deep blue night, glowing floor grid, distant towers, rising data
//   demo   demoscene / synthwave: striped sun, stars, scrolling grid, vector shapes
//   math, circuits, space, lakeside: see SceneryWorlds.cs
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Vcl
{
    public partial class Scenery
    {
        public static readonly string[] Names = { "night", "aero", "tech", "demo", "math", "circuits", "space", "lakeside" };
        public static string Label(string n) => n.Length == 0 ? n : char.ToUpper(n[0]) + n.Substring(1);

        readonly Transform parent;
        readonly Camera cam;
        readonly Light sun;
        readonly GameObject plainFloor;
        Transform root;
        readonly List<Action<float, float>> anim = new List<Action<float, float>>();
        float t;
        public string Name { get; private set; } = "night";

        struct Defaults { public Color bg, fog, sky, eq, ground, sunC; public float fogNear, fogFar, sunI, far; public Quaternion sunRot; }
        readonly Defaults d;

        public Scenery(Transform parent, Camera cam, Light sun, GameObject plainFloor)
        {
            this.parent = parent; this.cam = cam; this.sun = sun; this.plainFloor = plainFloor;
            d = new Defaults
            {
                bg = cam.backgroundColor, fog = RenderSettings.fogColor, fogNear = RenderSettings.fogStartDistance, fogFar = RenderSettings.fogEndDistance,
                sky = RenderSettings.ambientSkyColor, eq = RenderSettings.ambientEquatorColor, ground = RenderSettings.ambientGroundColor,
                sunC = sun.color, sunI = sun.intensity, sunRot = sun.transform.rotation, far = cam.farClipPlane,
            };
        }

        static Material Mat(string shader, int queue = -1)
        {
            var m = new Material(Shader.Find(shader));
            if (queue >= 0) m.renderQueue = queue;
            return m;
        }

        /// <summary>Lit, opaque-queued (drawn before the lesson's transparent panels).</summary>
        static Material Solid(string hex, float smooth = 0.3f, float metal = 0.05f)
        {
            var m = Draw.Lit(Draw.Hex(hex), false, smooth, metal);
            m.renderQueue = 2000;
            return m;
        }

        GameObject Obj(string name, Mesh mesh, Material mat, Transform p = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(p ?? root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        public void Set(string name)
        {
            if (Array.IndexOf(Names, name) < 0) name = "night";
            Name = name;
            if (root) Draw.Kill(root.gameObject);
            anim.Clear();
            root = Draw.Child(parent, "scenery").transform;
            cam.backgroundColor = d.bg;
            RenderSettings.fogColor = d.fog; RenderSettings.fogStartDistance = d.fogNear; RenderSettings.fogEndDistance = d.fogFar;
            RenderSettings.ambientSkyColor = d.sky; RenderSettings.ambientEquatorColor = d.eq; RenderSettings.ambientGroundColor = d.ground;
            sun.color = d.sunC; sun.intensity = d.sunI; sun.transform.rotation = d.sunRot;
            cam.farClipPlane = d.far;
            if (plainFloor) plainFloor.SetActive(name == "night");
            if (name == "night") return;

            Look look = name switch
            {
                "aero" => BuildAero(), "tech" => BuildTech(), "math" => BuildMath(), "circuits" => BuildCircuits(),
                "space" => BuildSpace(), "lakeside" => BuildLakeside(), _ => BuildDemo(),
            };
            var sky = Obj("sky", Draw.Primitive(PrimitiveType.Sphere), look.Sky);
            sky.transform.localScale = Vector3.one * 220;
            cam.backgroundColor = look.Fog;
            RenderSettings.fogColor = look.Fog; RenderSettings.fogStartDistance = look.FogNear; RenderSettings.fogEndDistance = look.FogFar;
            RenderSettings.ambientSkyColor = look.AmbSky; RenderSettings.ambientEquatorColor = Color.Lerp(look.AmbSky, look.AmbGround, 0.5f); RenderSettings.ambientGroundColor = look.AmbGround;
            sun.color = look.SunColor; sun.intensity = look.SunIntensity;
            sun.transform.rotation = Quaternion.LookRotation(-look.SunFrom);
            cam.farClipPlane = 150;
        }

        public void Update(float dt)
        {
            t += dt;
            foreach (var f in anim) f(t, dt);
        }

        class Look
        {
            public Material Sky;
            public Color Fog, AmbSky, AmbGround, SunColor;
            public float FogNear, FogFar, SunIntensity;
            public Vector3 SunFrom;
        }

        static Material SkyMat(string top, string horizon, string bottom, string sun, string sun2, Vector3 sunDir, float sunSize, float glow, bool stripes = false, float band = 0.25f)
        {
            var m = Mat("Vcl/Sky");
            m.SetColor("_Top", Draw.Hex(top)); m.SetColor("_Horizon", Draw.Hex(horizon)); m.SetColor("_Bottom", Draw.Hex(bottom));
            m.SetColor("_Sun", Draw.Hex(sun)); m.SetColor("_Sun2", Draw.Hex(sun2));
            m.SetVector("_SunDir", sunDir.normalized);
            m.SetFloat("_SunSize", sunSize); m.SetFloat("_Glow", glow); m.SetFloat("_Stripes", stripes ? 1 : 0); m.SetFloat("_Band", band);
            return m;
        }

        static Material GridMat(string color, string baseC, float cell, float major, float fade, float baseAlpha, bool pulse, float scroll)
        {
            var m = Mat("Vcl/Grid");
            m.SetColor("_Color", Draw.Hex(color)); m.SetColor("_Base", Draw.Hex(baseC));
            m.SetFloat("_Cell", cell); m.SetFloat("_Major", major); m.SetFloat("_Fade", fade);
            m.SetFloat("_BaseAlpha", baseAlpha); m.SetFloat("_Pulse", pulse ? 1 : 0); m.SetFloat("_Scroll", scroll);
            return m;
        }

        /// <summary>A flat square in the xz plane, in metres (the grid shader works in object space).</summary>
        static Mesh Ground(float size)
        {
            float h = size / 2;
            var m = new Mesh
            {
                vertices = new[] { new Vector3(-h, 0, -h), new Vector3(-h, 0, h), new Vector3(h, 0, h), new Vector3(h, 0, -h) },
                triangles = new[] { 0, 1, 2, 0, 2, 3 },
                normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up },
            };
            m.bounds = new Bounds(Vector3.zero, new Vector3(size, 1, size));
            return m;
        }

        static Mesh Torus(float R, float r, int seg = 128, int sides = 10)
        {
            var v = new List<Vector3>(); var n = new List<Vector3>(); var tr = new List<int>();
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2;
                var c = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                for (int j = 0; j <= sides; j++)
                {
                    float b = j / (float)sides * Mathf.PI * 2;
                    var dir = c * Mathf.Cos(b) + Vector3.up * Mathf.Sin(b);
                    v.Add(c * R + dir * r); n.Add(dir);
                }
            }
            for (int i = 0; i < seg; i++)
                for (int j = 0; j < sides; j++)
                {
                    int a = i * (sides + 1) + j, b = a + sides + 1;
                    tr.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
                }
            var m = new Mesh { vertices = v.ToArray(), normals = n.ToArray(), triangles = tr.ToArray() };
            m.colors = Fill(v.Count, Color.white);
            return m;
        }

        static Color[] Fill(int n, Color c) { var a = new Color[n]; for (int i = 0; i < n; i++) a[i] = c; return a; }

        /// <summary>Sprites (see Vcl/Sprites): centre, width, height (0 = square), rise speed, colour.</summary>
        static Mesh Sprites(List<(Vector3 p, float w, float h, float rise, Color c)> s)
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var uv2 = new List<Vector2>(); var uv3 = new List<Vector2>();
            var col = new List<Color>(); var tr = new List<int>();
            var corners = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            var lo = Vector3.positiveInfinity; var hi = Vector3.negativeInfinity;
            foreach (var (p, w, h, rise, c) in s)
            {
                int b = v.Count;
                foreach (var k in corners) { v.Add(p); uv.Add(k); uv2.Add(new Vector2(w, rise)); uv3.Add(new Vector2(h, 0)); col.Add(c); }
                tr.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
            }
            var m = new Mesh();
            m.indexFormat = v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            m.SetVertices(v); m.SetUVs(0, uv); m.SetUVs(1, uv2); m.SetUVs(2, uv3); m.SetColors(col); m.SetTriangles(tr, 0);
            var b2 = new Bounds((lo + hi) / 2, hi - lo + Vector3.one * 60); // sprites rise and grow: generous
            m.bounds = b2;
            return m;
        }

        static Material SpriteMat(bool additive, bool disc, Texture tex = null, float floor = 0, float ceil = 12)
        {
            var m = Mat("Vcl/Sprites");
            m.SetFloat("_Disc", disc ? 1 : 0);
            m.SetFloat("_Src", additive ? (float)UnityEngine.Rendering.BlendMode.One : (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_Dst", additive ? (float)UnityEngine.Rendering.BlendMode.One : (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_Floor", floor); m.SetFloat("_Ceil", ceil);
            if (tex) m.mainTexture = tex;
            return m;
        }

        static Mesh Lines(List<Vector3> pairs)
        {
            var m = new Mesh();
            m.indexFormat = pairs.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            m.SetVertices(pairs);
            var idx = new int[pairs.Count];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            m.SetIndices(idx, MeshTopology.Lines, 0);
            m.colors = Fill(pairs.Count, Color.white);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Unique edges of a mesh as line pairs (for wireframe shapes).</summary>
        static List<Vector3> Edges(Mesh mesh)
        {
            var verts = mesh.vertices; var tris = mesh.triangles;
            var seen = new HashSet<(Vector3, Vector3)>(); var outp = new List<Vector3>();
            Vector3 Q(Vector3 p) => new Vector3(Mathf.Round(p.x * 1000) / 1000, Mathf.Round(p.y * 1000) / 1000, Mathf.Round(p.z * 1000) / 1000);
            for (int i = 0; i < tris.Length; i += 3)
                for (int k = 0; k < 3; k++)
                {
                    var a = Q(verts[tris[i + k]]); var b = Q(verts[tris[i + (k + 1) % 3]]);
                    var key = a.x < b.x || (a.x == b.x && (a.y < b.y || (a.y == b.y && a.z < b.z))) ? (a, b) : (b, a);
                    if (seen.Add(key)) { outp.Add(a); outp.Add(b); }
                }
            return outp;
        }

        static Mesh Icosahedron(float r)
        {
            float p = (1 + Mathf.Sqrt(5)) / 2;
            var v = new[] { new Vector3(-1, p, 0), new Vector3(1, p, 0), new Vector3(-1, -p, 0), new Vector3(1, -p, 0), new Vector3(0, -1, p), new Vector3(0, 1, p),
                new Vector3(0, -1, -p), new Vector3(0, 1, -p), new Vector3(p, 0, -1), new Vector3(p, 0, 1), new Vector3(-p, 0, -1), new Vector3(-p, 0, 1) };
            for (int i = 0; i < v.Length; i++) v[i] = v[i].normalized * r;
            var t = new[] { 0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11, 1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8, 3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9, 4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1 };
            return new Mesh { vertices = v, triangles = t };
        }

        static Mesh Octahedron(float r)
        {
            var v = new[] { Vector3.right * r, Vector3.left * r, Vector3.up * r, Vector3.down * r, Vector3.forward * r, Vector3.back * r };
            var t = new[] { 0,2,4, 4,2,1, 1,2,5, 5,2,0, 0,4,3, 4,1,3, 1,5,3, 5,0,3 };
            return new Mesh { vertices = v, triangles = t };
        }

        /// <summary>A torus knot as a polyline ring with a few rings around it (wireframe look).</summary>
        static List<Vector3> TorusKnot(float R, float r, int p = 2, int q = 3, int n = 160)
        {
            var pts = new List<Vector3>();
            Vector3 P(float u) { float cu = Mathf.Cos(q * u / p), rr = R + r * cu; return new Vector3(rr * Mathf.Cos(u), r * Mathf.Sin(q * u / p), rr * Mathf.Sin(u)); }
            var lines = new List<Vector3>();
            float tube = r * 0.45f;
            for (int i = 0; i < n; i++)
            {
                float u0 = i / (float)n * Mathf.PI * 2 * p, u1 = (i + 1) / (float)n * Mathf.PI * 2 * p;
                var a = P(u0); var b = P(u1);
                var tng = (b - a).normalized; var nrm = Vector3.Cross(tng, Vector3.up).normalized; var bin = Vector3.Cross(tng, nrm);
                var b2 = P((i + 2) / (float)n * Mathf.PI * 2 * p);
                var tng2 = (b2 - b).normalized; var nrm2 = Vector3.Cross(tng2, Vector3.up).normalized; var bin2 = Vector3.Cross(tng2, nrm2);
                for (int k = 0; k < 6; k++)
                {
                    float ang = k / 6f * Mathf.PI * 2;
                    var o1 = (nrm * Mathf.Cos(ang) + bin * Mathf.Sin(ang)) * tube;
                    var o2 = (nrm2 * Mathf.Cos(ang) + bin2 * Mathf.Sin(ang)) * tube;
                    lines.Add(a + o1); lines.Add(b + o2);
                    if (i % 3 == 0)
                    {
                        var o3 = (nrm * Mathf.Cos(ang + Mathf.PI / 3) + bin * Mathf.Sin(ang + Mathf.PI / 3)) * tube;
                        lines.Add(a + o1); lines.Add(a + o3);
                    }
                }
            }
            return lines;
        }

        static Texture2D CloudTexture(int seed)
        {
            const int W = 128, H = 64;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[W * H];
            var rnd = new System.Random(seed);
            var blobs = new List<(float x, float y, float r)>();
            for (int i = 0; i < 9; i++) blobs.Add((25 + (float)rnd.NextDouble() * 78, 30 + (float)rnd.NextDouble() * 12, 13 + (float)rnd.NextDouble() * 15));
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float a = 0, shade = 0;
                    foreach (var (bx, by, br) in blobs)
                    {
                        float dd = Mathf.Sqrt((x - bx) * (x - bx) + ((H - 1 - y) - by) * ((H - 1 - y) - by)) / br;
                        float k = Mathf.Clamp01(1 - dd);
                        a = 1 - (1 - a) * (1 - k * 0.95f);
                        shade = Mathf.Max(shade, k * Mathf.Clamp01(0.5f + ((H - 1 - y) - by) / br * -0.5f));
                    }
                    float g = Mathf.Lerp(0.9f, 1f, shade);
                    px[y * W + x] = new Color(g, g, 1, Mathf.Clamp01(a * 1.1f));
                }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        static float Smooth(float x, float a, float b) { float k = Mathf.Clamp01((x - a) / (b - a)); return k * k * (3 - 2 * k); }

        Look BuildAero()
        {
            var R = new System.Random(7);
            float Rf() => (float)R.NextDouble();
            // Glossy platform with an aqua rim.
            var plat = Obj("platform", Draw.Primitive(PrimitiveType.Cylinder), Solid("#c9dbe8", 0.75f, 0.15f));
            plat.transform.localScale = new Vector3(13, 0.06f, 13);
            plat.transform.localPosition = new Vector3(0, -0.061f, 0);
            Obj("rim", Torus(6.62f, 0.05f), Draw.Unlit(Draw.Hex("#5fd3ff"), 2000, false));
            // Rolling hills: flat near the platform, rising toward the horizon.
            const int N = 100; const float S = 240;
            var v = new Vector3[(N + 1) * (N + 1)]; var col = new Color[v.Length]; var tri = new List<int>();
            Color lo = Draw.Hex("#3f9e45"), hi = Draw.Hex("#b6e86a");
            for (int j = 0; j <= N; j++)
                for (int i = 0; i <= N; i++)
                {
                    float x = -S / 2 + S * i / N, z = -S / 2 + S * j / N, r = Mathf.Sqrt(x * x + z * z);
                    float n = Mathf.Sin(x * 0.045f) * Mathf.Cos(z * 0.05f) * 3.2f + Mathf.Sin(x * 0.11f + z * 0.07f) * 1.3f + Mathf.Cos(z * 0.13f - x * 0.03f) * 0.9f;
                    float k = Smooth(r, 9, 45);
                    v[j * (N + 1) + i] = new Vector3(x, -0.15f + (n + 3) * k * 1.4f + k * 2, z);
                    col[j * (N + 1) + i] = Color.Lerp(lo, hi, Mathf.Clamp01((n + 4) / 9));
                }
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N; i++)
                {
                    int a = j * (N + 1) + i, b = a + N + 1;
                    tri.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
                }
            var hills = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, vertices = v, colors = col };
            hills.SetTriangles(tri, 0);
            hills.RecalculateNormals();
            Obj("hills", hills, Solid("#ffffff", 0.25f, 0));
            // Lake.
            var lake = Obj("lake", Draw.Disc(14, 64), Solid("#2aa6e6", 0.95f, 0.3f));
            lake.transform.localRotation = Quaternion.Euler(90, 0, 0);
            lake.transform.localPosition = new Vector3(-26, 0.6f, 14);
            // Round glossy trees (none straight behind the board).
            var leaf = Solid("#5cc84a", 0.7f); var trunk = Solid("#8a6a4a", 0.2f);
            for (int i = 0; i < 16; i++)
            {
                float a = Rf() * Mathf.PI * 2, r = 12 + Rf() * 16, s = 0.7f + Rf() * 0.9f;
                if (Mathf.Abs(Mathf.DeltaAngle(a * Mathf.Rad2Deg, 90)) < 29) continue;
                float x = Mathf.Cos(a) * r, z = Mathf.Sin(a) * r, bse = -0.15f + Smooth(r, 9, 45) * 2;
                var tk = Obj("trunk", Draw.Primitive(PrimitiveType.Cylinder), trunk);
                tk.transform.localPosition = new Vector3(x, bse + 0.7f * s, z);
                tk.transform.localScale = new Vector3(0.3f * s, 0.7f * s, 0.3f * s);
                var cr = Obj("crown", Draw.Primitive(PrimitiveType.Sphere), leaf);
                cr.transform.localPosition = new Vector3(x, bse + 1.7f * s, z);
                cr.transform.localScale = Vector3.one * 1.8f * s;
            }
            // Clouds: one sprite mesh that turns slowly.
            var clouds = new List<(Vector3, float, float, float, Color)>();
            for (int i = 0; i < 16; i++)
            {
                float a = Rf() * Mathf.PI * 2, r = 55 + Rf() * 30;
                clouds.Add((new Vector3(Mathf.Cos(a) * r, 14 + Rf() * 18, Mathf.Sin(a) * r), 26 + Rf() * 18, 12 + Rf() * 6, 0, new Color(1, 1, 1, 0.92f)));
            }
            var cloudMat = SpriteMat(false, false, CloudTexture(11));
            cloudMat.renderQueue = 2400;
            var cloudObj = Obj("clouds", Sprites(clouds), cloudMat);
            // Bubbles rising around the learner.
            var bm = Mat("Vcl/Bubble");
            var bubbles = new List<(Transform t, float v, float ph)>();
            for (int i = 0; i < 26; i++)
            {
                var b = Obj("bubble", Draw.Primitive(PrimitiveType.Sphere), bm);
                float a = Rf() * Mathf.PI * 2, r = 3.5f + Rf() * 6;
                b.transform.localPosition = new Vector3(Mathf.Cos(a) * r, Rf() * 6, Mathf.Sin(a) * r);
                b.transform.localScale = Vector3.one * (0.12f + Rf() * 0.4f);
                bubbles.Add((b.transform, 0.08f + Rf() * 0.15f, Rf() * 6.28f));
            }
            anim.Add((time, dt) =>
            {
                cloudObj.transform.localRotation = Quaternion.Euler(0, -time * 0.23f, 0);
                foreach (var (b, sp, ph) in bubbles)
                {
                    var p = b.localPosition;
                    p.y += sp * dt;
                    p.x += Mathf.Sin(time * 0.6f + ph) * dt * 0.08f;
                    if (p.y > 7) p.y = -0.2f;
                    b.localPosition = p;
                }
            });
            return new Look
            {
                Sky = SkyMat("#2f8fe8", "#d8f1ff", "#bfe6ff", "#fffbe8", "#fffbe8", new Vector3(0.5f, 0.55f, 0.7f), 0.05f, 1.2f),
                Fog = Draw.Hex("#d8f1ff"), FogNear = 35, FogFar = 140,
                AmbSky = Draw.Hex("#e6f4ff") * 0.55f, AmbGround = Draw.Hex("#6fae58") * 0.6f,
                SunColor = Draw.Hex("#fff6e0"), SunIntensity = 0.8f, SunFrom = new Vector3(4, 8, -3),
            };
        }

        Look BuildTech()
        {
            var R = new System.Random(3);
            float Rf() => (float)R.NextDouble();
            Obj("grid", Ground(100), GridMat("#29d3ff", "#04101c", 1, 5, 45, 0.92f, true, 0)).transform.localPosition = new Vector3(0, -0.002f, 0);
            // Towers merged into one mesh; vertex colour red = seed.
            var cube = Draw.Primitive(PrimitiveType.Cube);
            var cv = cube.vertices; var cn = cube.normals; var ct = cube.triangles;
            var v = new List<Vector3>(); var n = new List<Vector3>(); var c = new List<Color>(); var tr = new List<int>();
            for (int i = 0; i < 70; i++)
            {
                float a = Rf() * Mathf.PI * 2, r = 28 + Rf() * 30, w = 2 + Rf() * 4, h = 6 + Mathf.Pow(Rf(), 2) * 34, dz = w * (0.6f + Rf());
                var m = Matrix4x4.TRS(new Vector3(Mathf.Cos(a) * r, h / 2, Mathf.Sin(a) * r), Quaternion.Euler(0, a * Mathf.Rad2Deg, 0), new Vector3(w, h, dz));
                int b = v.Count;
                for (int k = 0; k < cv.Length; k++) { v.Add(m.MultiplyPoint3x4(cv[k])); n.Add(m.MultiplyVector(cn[k]).normalized); c.Add(new Color(i / 70f, 0, 0, 1)); }
                foreach (var k in ct) tr.Add(b + k);
            }
            var towers = new Mesh();
            towers.SetVertices(v); towers.SetNormals(n); towers.SetColors(c); towers.SetTriangles(tr, 0);
            Obj("towers", towers, Mat("Vcl/Towers"));
            // Data motes rising.
            var motes = new List<(Vector3, float, float, float, Color)>();
            for (int i = 0; i < 500; i++)
            {
                float a = Rf() * Mathf.PI * 2, r = 3 + Rf() * 22;
                motes.Add((new Vector3(Mathf.Cos(a) * r, Rf() * 12, Mathf.Sin(a) * r), 0.09f, 0, 0.15f + Rf() * 0.4f, Draw.Hex("#6fe0ff")));
            }
            Obj("motes", Sprites(motes), SpriteMat(true, true, null, 0, 12));
            var ring = Obj("ring", Torus(6.5f, 0.025f, 160, 8), Draw.Unlit(Draw.Hex("#29d3ff"), 2000, false));
            ring.transform.localPosition = new Vector3(0, 0.01f, 0);
            return new Look
            {
                Sky = SkyMat("#01030a", "#0b2a44", "#04101c", "#29d3ff", "#29d3ff", new Vector3(0, 0.02f, 1), 0, 0.35f),
                Fog = Draw.Hex("#061626"), FogNear = 20, FogFar = 75,
                AmbSky = Draw.Hex("#9fd8ff") * 0.8f, AmbGround = Draw.Hex("#06101c"),
                SunColor = Draw.Hex("#bfe9ff"), SunIntensity = 0.9f, SunFrom = new Vector3(1.5f, 4, -2),
            };
        }

        Look BuildDemo()
        {
            var R = new System.Random(5);
            float Rf() => (float)R.NextDouble();
            Obj("grid", Ground(120), GridMat("#ff3fb4", "#0d0220", 1.5f, 1, 55, 0.95f, false, 1.2f)).transform.localPosition = new Vector3(0, -0.002f, 0);
            // Stars.
            var stars = new List<(Vector3, float, float, float, Color)>();
            for (int i = 0; i < 900; i++)
            {
                float u = Rf() * 2 - 1, a = Rf() * Mathf.PI * 2, y = Mathf.Abs(u) * 0.95f + 0.05f, r = Mathf.Sqrt(1 - y * y);
                stars.Add((new Vector3(Mathf.Cos(a) * r * 90, y * 90, Mathf.Sin(a) * r * 90), 0.5f, 0, 0, Color.white));
            }
            var starMat = SpriteMat(true, true);
            starMat.renderQueue = 1001;
            Obj("stars", Sprites(stars), starMat);
            // Wireframe ridges, low under the sun.
            var sunDir = new Vector3(0.62f, 0.16f, 1);
            float sunAz = Mathf.Atan2(sunDir.z, sunDir.x);
            const int ring = 256;
            var ridge = new Vector3[ring];
            for (int i = 0; i < ring; i++)
            {
                float a = i / (float)ring * Mathf.PI * 2, da = Mathf.DeltaAngle(a * Mathf.Rad2Deg, sunAz * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                float h = (Mathf.Abs(Mathf.Sin(a * 3.1f)) * 9 + Mathf.Abs(Mathf.Sin(a * 7.3f + 1)) * 5 + Mathf.Abs(Mathf.Sin(a * 17.7f)) * 2.5f) * (1 - 0.85f * Mathf.Exp(-da * da * 6));
                ridge[i] = new Vector3(Mathf.Cos(a), h, Mathf.Sin(a));
            }
            var pairs = new List<Vector3>();
            foreach (var r in new[] { 70f, 60f })
                for (int i = 0; i < ring; i++)
                {
                    var p = ridge[i]; var q = ridge[(i + 1) % ring]; float k = r / 70;
                    pairs.Add(new Vector3(p.x * r, p.y * k, p.z * r)); pairs.Add(new Vector3(q.x * r, q.y * k, q.z * r));
                    if (i % 4 == 0) { pairs.Add(new Vector3(p.x * r, 0, p.z * r)); pairs.Add(new Vector3(p.x * r, p.y * k, p.z * r)); }
                }
            Obj("ridges", Lines(pairs), Draw.Unlit(new Color(0.7f, 0.24f, 1f, 0.85f), 2100));
            // Rotating vector shapes, cycling colour.
            var shapes = new List<(Transform t, Material m, float h)>();
            var defs = new (List<Vector3> lines, Vector3 at)[]
            {
                (Edges(Icosahedron(2.2f)), new Vector3(-14, 7, 22)),
                (TorusKnot(1.6f, 0.9f), new Vector3(15, 8, 20)),
                (Edges(Octahedron(2)), new Vector3(-20, 9, -8)),
                (Edges(Torus(2, 0.6f, 24, 10)), new Vector3(20, 6, -10)),
            };
            for (int i = 0; i < defs.Length; i++)
            {
                var m = Draw.Unlit(Color.white, 2100);
                var o = Obj("shape", Lines(defs[i].lines), m);
                o.transform.localPosition = defs[i].at;
                shapes.Add((o.transform, m, i * 0.25f));
            }
            anim.Add((time, dt) =>
            {
                foreach (var (tr, m, h) in shapes)
                {
                    tr.localRotation = Quaternion.Euler((time * 0.4f + h * 4) * Mathf.Rad2Deg, time * 0.55f * Mathf.Rad2Deg, 0);
                    m.color = Color.HSVToRGB((time * 0.05f + h) % 1, 0.65f, 1);
                }
            });
            return new Look
            {
                Sky = SkyMat("#000000", "#ff2a6d", "#12051f", "#ffe066", "#ff2a8a", sunDir, 0.17f, 1.4f, true, 0.12f),
                Fog = Draw.Hex("#14041f"), FogNear = 25, FogFar = 90,
                AmbSky = Draw.Hex("#ffb3e6") * 0.8f, AmbGround = Draw.Hex("#14041f"),
                SunColor = Draw.Hex("#ffd1f0"), SunIntensity = 1f, SunFrom = new Vector3(1.5f, 4, -2),
            };
        }
    }
}
