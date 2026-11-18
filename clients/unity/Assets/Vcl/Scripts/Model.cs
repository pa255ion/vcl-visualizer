// `model`: 3D files loaded at runtime, as the web viewer does with three.js's
// loaders. glTF 2.0 (.gltf with embedded or relative buffers, and .glb):
// meshes, the node tree, base colour (factor and texture), metallic/roughness,
// double-sided; no skins, morphs, animation or Draco. STL (binary or ASCII,
// CAD Z-up turned Y-up) and OBJ (v/vt/vn/f, polygons fanned) take the
// command's colour. The model is centred and scaled so its largest side is
// `size` (default 3). glTF space is three.js's, so z flips into Unity's.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Vcl
{
    public partial class Runtime
    {
        /// <summary>Fetch a file the lesson names (relative to the server, or a URL).</summary>
        public Action<string, Action<byte[]>> LoadBytes;

        void BuildModel(Entity e, Cmd c)
        {
            e.Obj.localPosition = e.ToParent(c["at"]);
            float size = c.Flt("size", 3);
            var url = c.Str("url") ?? "";
            var ext = url.Split('?', '#')[0];
            ext = ext.Substring(ext.LastIndexOf('.') + 1).ToLowerInvariant();
            LabelFor(e, c, new Vector3(0, -size / 2 - 0.35f, 0));
            if (e.Label) e.Billboards.Add(e.Label.transform); // stays readable while the model turns
            void Fail(string why) => Log("warning", $"line {c.Line}: model: could not load {url}{(why != null ? ": " + why : "")}");
            if (LoadBytes == null) { Fail("no loader"); return; }
            var color = Draw.Hex(c.Str("color") ?? "#b8c4d0");
            Material Plain() => Draw.Lit(color, smooth: 0.55f, metal: 0.35f);
            void Place(Transform root)
            {
                if (!e.Obj) { Draw.Kill(root.gameObject); return; }
                var holder = Draw.Child(e.Body, "model").transform;
                root.SetParent(holder, false);
                // Fit: centre the bounds, largest side = size.
                var rs = root.GetComponentsInChildren<MeshFilter>();
                if (rs.Length == 0) { Fail("no meshes"); return; }
                var b = new Bounds();
                bool first = true;
                foreach (var mf in rs)
                {
                    var mb = mf.sharedMesh.bounds;
                    for (int k = 0; k < 8; k++)
                    {
                        var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1));
                        var p = holder.InverseTransformPoint(mf.transform.TransformPoint(corner));
                        if (first) { b = new Bounds(p, Vector3.zero); first = false; } else b.Encapsulate(p);
                    }
                }
                float k2 = size / Mathf.Max(b.size.x, b.size.y, b.size.z, 1e-6f);
                root.localPosition -= b.center;
                holder.localScale = Vector3.one * k2;
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
                    foreach (var m in r.sharedMaterials) e.Track(m, "solid", m.color.a);
                e.ApplyOpacity();
            }
            LoadBytes(url, bytes =>
            {
                if (bytes == null) { Fail(null); return; }
                try
                {
                    if (ext == "stl") Place(Model.Stl(bytes, Plain()));
                    else if (ext == "obj") Place(Model.Obj(Encoding.UTF8.GetString(bytes), Plain()));
                    else
                    {
                        string dir = url.Contains("/") ? url.Substring(0, url.LastIndexOf('/') + 1) : "";
                        Model.Gltf(bytes, (uri, got) => LoadBytes(dir + uri, got), Place, Fail);
                    }
                }
                catch (Exception ex) { Fail(ex.Message); Debug.LogException(ex); }
            });
        }
    }

    public static class Model
    {
        static Transform Holder(string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go.transform;
        }

        static Mesh Flat(List<Vector3> verts, string name)
        {
            var tris = Enumerable.Range(0, verts.Count).ToArray();
            var m = new Mesh { name = name, indexFormat = verts.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        // ── STL ──

        /// <summary>CAD is Z-up: (x, y, z) → three.js (x, z, -y) → Unity (x, z, y).</summary>
        static Vector3 FromCad(float x, float y, float z) => new Vector3(x, z, y);

        public static Transform Stl(byte[] b, Material mat)
        {
            var verts = new List<Vector3>();
            bool binary = b.Length >= 84 && 84 + 50L * BitConverter.ToUInt32(b, 80) == b.Length;
            if (binary)
            {
                uint n = BitConverter.ToUInt32(b, 80);
                for (int t = 0; t < n; t++)
                {
                    int o = 84 + t * 50 + 12;
                    var v = new Vector3[3];
                    for (int k = 0; k < 3; k++)
                        v[k] = FromCad(BitConverter.ToSingle(b, o + k * 12), BitConverter.ToSingle(b, o + k * 12 + 4), BitConverter.ToSingle(b, o + k * 12 + 8));
                    // The axis swap mirrors, so the winding turns over.
                    verts.Add(v[0]); verts.Add(v[2]); verts.Add(v[1]);
                }
            }
            else
            {
                var tri = new List<Vector3>();
                foreach (var raw in Encoding.ASCII.GetString(b).Split('\n'))
                {
                    var l = raw.Trim();
                    if (!l.StartsWith("vertex")) continue;
                    var p = l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    tri.Add(FromCad(F(p[1]), F(p[2]), F(p[3])));
                    if (tri.Count == 3) { verts.Add(tri[0]); verts.Add(tri[2]); verts.Add(tri[1]); tri.Clear(); }
                }
            }
            if (verts.Count == 0) throw new Exception("no triangles");
            return Holder("stl", Flat(verts, "stl"), mat);
        }

        // ── OBJ ──

        public static Transform Obj(string text, Material mat)
        {
            var vs = new List<Vector3>();
            var verts = new List<Vector3>();
            foreach (var raw in text.Split('\n'))
            {
                var l = raw.Trim();
                if (l.StartsWith("v "))
                {
                    var p = l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    vs.Add(new Vector3(F(p[1]), F(p[2]), -F(p[3]))); // three.js → Unity: z flips
                }
                else if (l.StartsWith("f "))
                {
                    var p = l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Skip(1)
                        .Select(s => int.Parse(s.Split('/')[0], CultureInfo.InvariantCulture)).Select(ix => ix < 0 ? vs.Count + ix : ix - 1).ToList();
                    for (int k = 1; k + 1 < p.Count; k++) { verts.Add(vs[p[0]]); verts.Add(vs[p[k + 1]]); verts.Add(vs[p[k]]); }
                }
            }
            if (verts.Count == 0) throw new Exception("no faces");
            return Holder("obj", Flat(verts, "obj"), mat);
        }

        // ── glTF ──

        class Doc
        {
            public Dictionary<string, object> J;
            public byte[][] Buffers;
            public Texture2D[] Images;
            public Material[] Materials;
        }

        static List<object> L(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v as List<object> ?? new List<object>() : new List<object>();
        static Dictionary<string, object> D(object o) => o as Dictionary<string, object>;
        static int I(Dictionary<string, object> d, string k, int def = -1) => d != null && d.TryGetValue(k, out var v) && v is double x ? (int)x : def;
        static float Fd(Dictionary<string, object> d, string k, float def) => d != null && d.TryGetValue(k, out var v) && v is double x ? (float)x : def;
        static float[] Fs(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v is List<object> l ? l.Select(o => (float)(double)o).ToArray() : null;

        /// <summary>Parse a .gltf or .glb; external buffers and images come through `fetch`.</summary>
        public static void Gltf(byte[] bytes, Action<string, Action<byte[]>> fetch, Action<Transform> done, Action<string> fail)
        {
            var doc = new Doc();
            byte[] glbBin = null;
            if (bytes.Length >= 12 && bytes[0] == 'g' && bytes[1] == 'l' && bytes[2] == 'T' && bytes[3] == 'F')
            {
                int o = 12;
                while (o + 8 <= bytes.Length)
                {
                    int len = BitConverter.ToInt32(bytes, o), type = BitConverter.ToInt32(bytes, o + 4);
                    if (type == 0x4E4F534A) doc.J = D(Json.Parse(Encoding.UTF8.GetString(bytes, o + 8, len)));
                    else if (type == 0x004E4942) { glbBin = new byte[len]; Buffer.BlockCopy(bytes, o + 8, glbBin, 0, len); }
                    o += 8 + len;
                }
            }
            else doc.J = D(Json.Parse(Encoding.UTF8.GetString(bytes)));
            if (doc.J == null) { fail("not glTF"); return; }

            // Buffers (and then images) may need fetching: count down, then build.
            var bufs = L(doc.J, "buffers");
            doc.Buffers = new byte[bufs.Count][];
            var imgs = L(doc.J, "images");
            doc.Images = new Texture2D[imgs.Count];
            int pending = 1;
            bool failed = false;
            void Done()
            {
                if (--pending > 0 || failed) return;
                try
                {
                    for (int i = 0; i < imgs.Count; i++)
                    {
                        var im = D(imgs[i]);
                        int bv = I(im, "bufferView");
                        if (bv >= 0 && doc.Images[i] == null) doc.Images[i] = Tex(View(doc, bv));
                    }
                    done(Build(doc));
                }
                catch (Exception e) { fail(e.Message); }
            }
            void Get(string uri, Action<byte[]> set)
            {
                if (uri.StartsWith("data:"))
                {
                    int comma = uri.IndexOf(',');
                    set(Convert.FromBase64String(uri.Substring(comma + 1)));
                    return;
                }
                pending++;
                fetch(Uri.UnescapeDataString(uri), b =>
                {
                    if (b == null) { if (!failed) { failed = true; fail($"missing {uri}"); } return; }
                    set(b);
                    Done();
                });
            }
            for (int i = 0; i < bufs.Count; i++)
            {
                int k = i;
                var bd = D(bufs[i]);
                if (bd.TryGetValue("uri", out var u) && u is string uri) Get(uri, b => doc.Buffers[k] = b);
                else doc.Buffers[k] = glbBin;
            }
            for (int i = 0; i < imgs.Count; i++)
            {
                int k = i;
                if (D(imgs[i]).TryGetValue("uri", out var u) && u is string uri) Get(uri, b => doc.Images[k] = Tex(b));
            }
            Done();
        }

        static Texture2D Tex(byte[] b)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            return t.LoadImage(b) ? t : null;
        }

        static byte[] View(Doc doc, int index)
        {
            var v = D(L(doc.J, "bufferViews")[index]);
            var buf = doc.Buffers[I(v, "buffer", 0)];
            int off = I(v, "byteOffset", 0), len = I(v, "byteLength", 0);
            var r = new byte[len];
            Buffer.BlockCopy(buf, off, r, 0, len);
            return r;
        }

        /// <summary>An accessor's values as floats, `n` per element (normalized ints mapped to 0..1 / -1..1).</summary>
        static float[] Read(Doc doc, int index, out int n)
        {
            var a = D(L(doc.J, "accessors")[index]);
            int count = I(a, "count", 0), ct = I(a, "componentType", 5126);
            n = ((string)a["type"]) switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => 1 };
            bool norm = a.TryGetValue("normalized", out var nv) && nv is bool nb && nb;
            var r = new float[count * n];
            int bvi = I(a, "bufferView");
            if (bvi < 0) return r; // all zero (sparse-only accessors aren't supported)
            var v = D(L(doc.J, "bufferViews")[bvi]);
            var buf = doc.Buffers[I(v, "buffer", 0)];
            int csize = ct == 5126 || ct == 5125 ? 4 : ct == 5123 || ct == 5122 ? 2 : 1;
            int stride = I(v, "byteStride", 0);
            if (stride <= 0) stride = csize * n;
            int baseOff = I(v, "byteOffset", 0) + I(a, "byteOffset", 0);
            for (int i = 0; i < count; i++)
                for (int k = 0; k < n; k++)
                {
                    int o = baseOff + i * stride + k * csize;
                    float x;
                    switch (ct)
                    {
                        case 5126: x = BitConverter.ToSingle(buf, o); break;
                        case 5125: x = BitConverter.ToUInt32(buf, o); break;
                        case 5123: x = BitConverter.ToUInt16(buf, o); if (norm) x /= 65535f; break;
                        case 5122: x = BitConverter.ToInt16(buf, o); if (norm) x = Mathf.Max(x / 32767f, -1); break;
                        case 5121: x = buf[o]; if (norm) x /= 255f; break;
                        default: x = (sbyte)buf[o]; if (norm) x = Mathf.Max(x / 127f, -1); break;
                    }
                    r[i * n + k] = x;
                }
            return r;
        }

        static Material MaterialOf(Doc doc, int index)
        {
            if (doc.Materials == null) doc.Materials = new Material[L(doc.J, "materials").Count];
            if (index < 0 || index >= doc.Materials.Length) return Draw.Lit(Draw.Hex("#b8c4d0"), smooth: 0.55f, metal: 0.35f);
            if (doc.Materials[index]) return doc.Materials[index];
            var m = D(L(doc.J, "materials")[index]);
            var pbr = m.TryGetValue("pbrMetallicRoughness", out var p) ? D(p) : null;
            var f = Fs(pbr, "baseColorFactor") ?? new[] { 1f, 1, 1, 1 };
            var col = new Color(f[0], f[1], f[2], f[3]).gamma; // glTF factors are linear
            col.a = f[3];
            bool doubleSided = m.TryGetValue("doubleSided", out var ds) && ds is bool b && b;
            var mat = Draw.Lit(col, doubleSided, smooth: 1 - Fd(pbr, "roughnessFactor", 1), metal: Fd(pbr, "metallicFactor", 1));
            if (pbr != null && pbr.TryGetValue("baseColorTexture", out var bt))
            {
                int ti = I(D(bt), "index");
                var texs = L(doc.J, "textures");
                if (ti >= 0 && ti < texs.Count)
                {
                    int src = I(D(texs[ti]), "source");
                    if (src >= 0 && src < doc.Images.Length && doc.Images[src]) mat.mainTexture = doc.Images[src];
                }
            }
            return doc.Materials[index] = mat;
        }

        static Transform Build(Doc doc)
        {
            var root = new GameObject("gltf").transform;
            var scenes = L(doc.J, "scenes");
            int si = I(doc.J, "scene", 0);
            var nodes = L(doc.J, "nodes");
            List<object> top = scenes.Count > 0 ? L(D(scenes[Mathf.Clamp(si, 0, scenes.Count - 1)]), "nodes") : Enumerable.Range(0, nodes.Count).Select(i => (object)(double)i).ToList();
            var meshCache = new Dictionary<int, List<(Mesh, int)>>();
            void Node(int ni, Transform parent, int depth)
            {
                if (depth > 64) return;
                var n = D(nodes[ni]);
                var t = new GameObject(n.TryGetValue("name", out var nm) ? nm as string ?? "node" : "node").transform;
                t.SetParent(parent, false);
                var mtx = Fs(n, "matrix");
                if (mtx != null && mtx.Length == 16)
                {
                    var M = new Matrix4x4();
                    for (int k = 0; k < 16; k++) M[k % 4, k / 4] = mtx[k]; // column-major
                    var Z = Matrix4x4.Scale(new Vector3(1, 1, -1));
                    M = Z * M * Z;
                    t.localPosition = M.GetColumn(3);
                    t.localRotation = M.rotation;
                    t.localScale = M.lossyScale;
                }
                else
                {
                    var tr = Fs(n, "translation"); var ro = Fs(n, "rotation"); var sc = Fs(n, "scale");
                    if (tr != null) t.localPosition = new Vector3(tr[0], tr[1], -tr[2]);
                    if (ro != null) t.localRotation = new Quaternion(-ro[0], -ro[1], ro[2], ro[3]);
                    if (sc != null) t.localScale = new Vector3(sc[0], sc[1], sc[2]);
                }
                int mi = I(n, "mesh");
                if (mi >= 0)
                {
                    if (!meshCache.TryGetValue(mi, out var prims)) meshCache[mi] = prims = Prims(doc, mi);
                    foreach (var (mesh, mat) in prims)
                    {
                        var h = Holder("mesh", mesh, MaterialOf(doc, mat));
                        h.SetParent(t, false);
                    }
                }
                foreach (var ch in L(n, "children")) Node((int)(double)ch, t, depth + 1);
            }
            foreach (var ni in top) Node((int)(double)ni, root, 0);
            return root;
        }

        static List<(Mesh, int)> Prims(Doc doc, int mi)
        {
            var r = new List<(Mesh, int)>();
            var mesh = D(L(doc.J, "meshes")[mi]);
            foreach (var po in L(mesh, "primitives"))
            {
                var p = D(po);
                int mode = I(p, "mode", 4);
                if (mode != 4) continue; // triangles only
                var attrs = D(p["attributes"]);
                if (!attrs.ContainsKey("POSITION")) continue;
                var pos = Read(doc, I(attrs, "POSITION"), out _);
                int nv = pos.Length / 3;
                var verts = new Vector3[nv];
                for (int i = 0; i < nv; i++) verts[i] = new Vector3(pos[i * 3], pos[i * 3 + 1], -pos[i * 3 + 2]);
                int[] idx;
                if (p.ContainsKey("indices")) idx = Read(doc, I(p, "indices"), out _).Select(x => (int)x).ToArray();
                else idx = Enumerable.Range(0, nv).ToArray();
                for (int i = 0; i + 2 < idx.Length; i += 3) { int t = idx[i + 1]; idx[i + 1] = idx[i + 2]; idx[i + 2] = t; } // z flip turns winding
                var m = new Mesh { name = "gltf", indexFormat = nv > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
                m.vertices = verts;
                if (attrs.ContainsKey("TEXCOORD_0"))
                {
                    var uv = Read(doc, I(attrs, "TEXCOORD_0"), out _);
                    m.uv = Enumerable.Range(0, nv).Select(i => new Vector2(uv[i * 2], 1 - uv[i * 2 + 1])).ToArray();
                }
                if (attrs.ContainsKey("COLOR_0"))
                {
                    var c = Read(doc, I(attrs, "COLOR_0"), out int cn);
                    m.colors = Enumerable.Range(0, nv).Select(i => new Color(c[i * cn], c[i * cn + 1], c[i * cn + 2], cn == 4 ? c[i * cn + 3] : 1).gamma).ToArray();
                }
                m.triangles = idx;
                if (attrs.ContainsKey("NORMAL"))
                {
                    var nn = Read(doc, I(attrs, "NORMAL"), out _);
                    m.normals = Enumerable.Range(0, nv).Select(i => new Vector3(nn[i * 3], nn[i * 3 + 1], -nn[i * 3 + 2])).ToArray();
                }
                else m.RecalculateNormals();
                m.RecalculateBounds();
                r.Add((m, I(p, "material")));
            }
            return r;
        }

        static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
