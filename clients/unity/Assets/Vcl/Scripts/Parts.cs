// Schematic symbols as plain geometry and the orthogonal wire router: a port
// of viewer/js/parts.js (keep them in step). Board units, pins along ±x unless
// noted; pin names must match `part_pins` in core/src/vcl.zig.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Vcl
{
    public class Symbol
    {
        public List<List<Vector2>> Lines = new List<List<Vector2>>();
        public List<List<Vector2>> Fills = new List<List<Vector2>>();
        public List<(string text, float x, float y, float size, bool value)> Texts = new List<(string, float, float, float, bool)>();
        public Dictionary<string, (Vector2 at, Vector2 dir)> Pins = new Dictionary<string, (Vector2, Vector2)>();
    }

    public static class Parts
    {
        static List<Vector2> Arc(float cx, float cy, float r, float a0, float a1, int n = 24)
        {
            var pts = new List<Vector2>();
            for (int i = 0; i <= n; i++)
            {
                float a = a0 + (a1 - a0) * i / n;
                pts.Add(new Vector2(cx + r * Mathf.Cos(a), cy + r * Mathf.Sin(a)));
            }
            return pts;
        }
        static List<Vector2> Circle(float cx, float cy, float r, int n = 40) => Arc(cx, cy, r, 0, 2 * Mathf.PI, n);
        static List<Vector2> Quad(Vector2 a, Vector2 c, Vector2 b, int n = 16)
        {
            var pts = new List<Vector2>();
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n, u = 1 - t;
                pts.Add(u * u * a + 2 * u * t * c + t * t * b);
            }
            return pts;
        }
        /// <summary>Small filled arrowhead with its tip at `tip`, pointing along (dx,dy).</summary>
        static List<Vector2> Head(Vector2 tip, float dx, float dy, float s = 0.16f)
        {
            float l = Mathf.Sqrt(dx * dx + dy * dy), ux = dx / l, uy = dy / l;
            float bx = tip.x - ux * s, by = tip.y - uy * s;
            return new List<Vector2> { tip, new Vector2(bx - uy * s * 0.5f, by + ux * s * 0.5f), new Vector2(bx + uy * s * 0.5f, by - ux * s * 0.5f) };
        }
        static List<Vector2> L(params float[] xy)
        {
            var o = new List<Vector2>();
            for (int i = 0; i + 1 < xy.Length; i += 2) o.Add(new Vector2(xy[i], xy[i + 1]));
            return o;
        }
        static (Vector2, Vector2) Pin(float x, float y, float dx, float dy) => (new Vector2(x, y), new Vector2(dx, dy));
        static void TwoPin(Symbol s) { s.Pins["a"] = Pin(-1, 0, -1, 0); s.Pins["b"] = Pin(1, 0, 1, 0); }
        static List<Vector2> Cat(params IEnumerable<Vector2>[] parts) => parts.SelectMany(p => p).ToList();

        static Symbol Gate(string kind)
        {
            var s = new Symbol();
            bool inv = kind == "nand" || kind == "nor" || kind == "xnor" || kind == "not";
            string b = kind == "nand" ? "and" : kind == "nor" ? "or" : kind == "xnor" ? "xor" : kind;
            float backX = -0.6f;
            if (b == "and")
                s.Lines.Add(Cat(L(-0.6f, 0.5f, 0.1f, 0.5f), Arc(0.1f, 0, 0.5f, Mathf.PI / 2, -Mathf.PI / 2).Skip(1), L(-0.6f, -0.5f, -0.6f, 0.5f)));
            else if (b == "or" || b == "xor")
            {
                s.Lines.Add(Quad(new Vector2(-0.6f, 0.5f), new Vector2(-0.35f, 0), new Vector2(-0.6f, -0.5f)));
                s.Lines.Add(Cat(Quad(new Vector2(-0.6f, 0.5f), new Vector2(0.15f, 0.5f), new Vector2(0.6f, 0)), Quad(new Vector2(0.6f, 0), new Vector2(0.15f, -0.5f), new Vector2(-0.6f, -0.5f)).Skip(1)));
                backX = -0.5f;
                if (b == "xor") { s.Lines.Add(Quad(new Vector2(-0.75f, 0.5f), new Vector2(-0.5f, 0), new Vector2(-0.75f, -0.5f))); backX = -0.65f; }
            }
            else
            {
                s.Lines.Add(L(-0.5f, 0.45f, 0.5f, 0, -0.5f, -0.45f, -0.5f, 0.45f));
                backX = -0.5f;
            }
            float outX = b == "buf" || b == "not" ? 0.5f : 0.6f;
            if (inv) { s.Lines.Add(Circle(outX + 0.09f, 0, 0.09f, 20)); outX += 0.18f; }
            s.Lines.Add(L(outX, 0, 1.2f, 0));
            s.Pins["y"] = Pin(1.2f, 0, 1, 0);
            if (b == "buf" || b == "not")
            {
                s.Lines.Add(L(-1.2f, 0, backX, 0));
                s.Pins["a"] = Pin(-1.2f, 0, -1, 0);
            }
            else
            {
                s.Lines.Add(L(-1.2f, 0.25f, backX, 0.25f));
                s.Lines.Add(L(-1.2f, -0.25f, backX, -0.25f));
                s.Pins["a"] = Pin(-1.2f, 0.25f, -1, 0);
                s.Pins["b"] = Pin(-1.2f, -0.25f, -1, 0);
            }
            return s;
        }

        static Symbol Transistor(string kind)
        {
            var s = new Symbol();
            s.Lines.Add(Circle(0.2f, 0, 0.62f));
            if (kind == "npn" || kind == "pnp")
            {
                s.Lines.Add(L(-1, 0, -0.15f, 0));
                s.Lines.Add(L(-0.15f, -0.4f, -0.15f, 0.4f));
                s.Lines.Add(L(-0.15f, 0.18f, 0.5f, 0.55f, 0.5f, 1));
                s.Lines.Add(L(-0.15f, -0.18f, 0.5f, -0.55f, 0.5f, -1));
                Vector2 e0 = new Vector2(-0.15f, -0.18f), e1 = new Vector2(0.5f, -0.55f);
                if (kind == "npn") s.Fills.Add(Head(new Vector2(0.38f, -0.48f), e1.x - e0.x, e1.y - e0.y));
                else s.Fills.Add(Head(new Vector2(-0.02f, -0.255f), e0.x - e1.x, e0.y - e1.y));
                s.Pins["b"] = Pin(-1, 0, -1, 0); s.Pins["c"] = Pin(0.5f, 1, 0, 1); s.Pins["e"] = Pin(0.5f, -1, 0, -1);
                return s;
            }
            bool p = kind == "pmos";
            s.Lines.Add(L(-1, 0, p ? -0.55f : -0.35f, 0));
            s.Lines.Add(L(-0.35f, -0.45f, -0.35f, 0.45f));
            s.Lines.Add(L(-0.2f, -0.5f, -0.2f, 0.5f));
            if (p) s.Lines.Add(Circle(-0.45f, 0, 0.1f, 20));
            s.Lines.Add(L(-0.2f, 0.35f, 0.5f, 0.35f, 0.5f, 1));
            s.Lines.Add(L(-0.2f, -0.35f, 0.5f, -0.35f, 0.5f, -1));
            s.Fills.Add(p ? Head(new Vector2(-0.05f, -0.35f), -1, 0) : Head(new Vector2(0.3f, -0.35f), 1, 0));
            s.Pins["g"] = Pin(-1, 0, -1, 0); s.Pins["d"] = Pin(0.5f, 1, 0, 1); s.Pins["s"] = Pin(0.5f, -1, 0, -1);
            return s;
        }

        public static Symbol Get(string kind)
        {
            var s = new Symbol();
            switch (kind)
            {
                case "resistor":
                    s.Lines.Add(L(-1, 0, -0.6f, 0, -0.5f, 0.18f, -0.3f, -0.18f, -0.1f, 0.18f, 0.1f, -0.18f, 0.3f, 0.18f, 0.5f, -0.18f, 0.6f, 0, 1, 0));
                    TwoPin(s); break;
                case "capacitor":
                    s.Lines.Add(L(-1, 0, -0.12f, 0)); s.Lines.Add(L(-0.12f, -0.4f, -0.12f, 0.4f));
                    s.Lines.Add(L(0.12f, -0.4f, 0.12f, 0.4f)); s.Lines.Add(L(0.12f, 0, 1, 0));
                    TwoPin(s); break;
                case "inductor":
                    s.Lines.Add(Cat(L(-1, 0, -0.6f, 0), new[] { -0.45f, -0.15f, 0.15f, 0.45f }.SelectMany(cx => Arc(cx, 0, 0.15f, Mathf.PI, 0, 12).Skip(1)), L(1, 0)));
                    TwoPin(s); break;
                case "vsource":
                    s.Lines.Add(L(-1, 0, -0.45f, 0)); s.Lines.Add(Circle(0, 0, 0.45f)); s.Lines.Add(L(0.45f, 0, 1, 0));
                    s.Texts.Add(("+", -0.2f, 0, 0.34f, false)); s.Texts.Add(("−", 0.2f, 0, 0.34f, false));
                    TwoPin(s); break;
                case "isource":
                    s.Lines.Add(L(-1, 0, -0.45f, 0)); s.Lines.Add(Circle(0, 0, 0.45f)); s.Lines.Add(L(0.45f, 0, 1, 0)); s.Lines.Add(L(-0.25f, 0, 0.12f, 0));
                    s.Fills.Add(Head(new Vector2(0.27f, 0), 1, 0));
                    TwoPin(s); break;
                case "battery":
                    s.Lines.Add(L(-1, 0, -0.1f, 0)); s.Lines.Add(L(-0.1f, -0.35f, -0.1f, 0.35f));
                    s.Lines.Add(L(0.1f, -0.18f, 0.1f, 0.18f)); s.Lines.Add(L(0.1f, 0, 1, 0));
                    s.Texts.Add(("+", -0.32f, 0.32f, 0.26f, false));
                    TwoPin(s); break;
                case "switch":
                    s.Lines.Add(L(-1, 0, -0.4f, 0)); s.Lines.Add(L(-0.4f, 0, 0.35f, 0.32f)); s.Lines.Add(L(0.4f, 0, 1, 0));
                    s.Fills.Add(Circle(-0.4f, 0, 0.06f, 16)); s.Fills.Add(Circle(0.4f, 0, 0.06f, 16));
                    TwoPin(s); break;
                case "diode":
                case "led":
                    s.Lines.Add(L(-1, 0, -0.3f, 0)); s.Lines.Add(L(-0.3f, 0.3f, 0.3f, 0, -0.3f, -0.3f, -0.3f, 0.3f));
                    s.Lines.Add(L(0.3f, -0.3f, 0.3f, 0.3f)); s.Lines.Add(L(0.3f, 0, 1, 0));
                    if (kind == "led")
                    {
                        s.Lines.Add(L(0, 0.38f, 0.24f, 0.62f)); s.Lines.Add(L(0.22f, 0.32f, 0.46f, 0.56f));
                        s.Fills.Add(Head(new Vector2(0.32f, 0.7f), 1, 1, 0.13f)); s.Fills.Add(Head(new Vector2(0.54f, 0.64f), 1, 1, 0.13f));
                    }
                    s.Pins["a"] = Pin(-1, 0, -1, 0); s.Pins["k"] = Pin(1, 0, 1, 0);
                    break;
                case "ground":
                    s.Lines.Add(L(0, 0, 0, -0.3f)); s.Lines.Add(L(-0.35f, -0.3f, 0.35f, -0.3f));
                    s.Lines.Add(L(-0.22f, -0.42f, 0.22f, -0.42f)); s.Lines.Add(L(-0.09f, -0.54f, 0.09f, -0.54f));
                    s.Pins["a"] = Pin(0, 0, 0, 1); break;
                case "vdd":
                    s.Lines.Add(L(0, 0, 0, 0.3f)); s.Lines.Add(L(-0.3f, 0.3f, 0.3f, 0.3f));
                    s.Texts.Add(("VDD", 0, 0.56f, 0.28f, true));
                    s.Pins["a"] = Pin(0, 0, 0, -1); break;
                case "npn": case "pnp": case "nmos": case "pmos": return Transistor(kind);
                case "opamp":
                    s.Lines.Add(L(-0.8f, 0.9f, 0.9f, 0, -0.8f, -0.9f, -0.8f, 0.9f)); s.Lines.Add(L(-1.4f, 0.45f, -0.8f, 0.45f));
                    s.Lines.Add(L(-1.4f, -0.45f, -0.8f, -0.45f)); s.Lines.Add(L(0.9f, 0, 1.4f, 0));
                    s.Texts.Add(("−", -0.6f, 0.45f, 0.34f, false)); s.Texts.Add(("+", -0.6f, -0.45f, 0.34f, false));
                    s.Pins["inn"] = Pin(-1.4f, 0.45f, -1, 0); s.Pins["inp"] = Pin(-1.4f, -0.45f, -1, 0); s.Pins["out"] = Pin(1.4f, 0, 1, 0);
                    break;
                case "and": case "or": case "nand": case "nor": case "xor": case "xnor": case "not": case "buf": return Gate(kind);
                case "dff":
                    s.Lines.Add(L(-0.8f, -1, 0.8f, -1, 0.8f, 1, -0.8f, 1, -0.8f, -1)); s.Lines.Add(L(-0.8f, -0.32f, -0.58f, -0.5f, -0.8f, -0.68f));
                    s.Lines.Add(L(-1.3f, 0.5f, -0.8f, 0.5f)); s.Lines.Add(L(-1.3f, -0.5f, -0.8f, -0.5f));
                    s.Lines.Add(L(0.8f, 0.5f, 1.3f, 0.5f)); s.Lines.Add(L(0.8f, -0.5f, 1.3f, -0.5f));
                    s.Texts.Add(("D", -0.55f, 0.5f, 0.3f, false)); s.Texts.Add(("Q", 0.55f, 0.5f, 0.3f, false)); s.Texts.Add(("Q̄", 0.55f, -0.5f, 0.3f, false));
                    s.Pins["d"] = Pin(-1.3f, 0.5f, -1, 0); s.Pins["clk"] = Pin(-1.3f, -0.5f, -1, 0); s.Pins["q"] = Pin(1.3f, 0.5f, 1, 0); s.Pins["qn"] = Pin(1.3f, -0.5f, 1, 0);
                    break;
                case "mux2":
                    s.Lines.Add(L(-0.5f, 1, 0.5f, 0.6f, 0.5f, -0.6f, -0.5f, -1, -0.5f, 1)); s.Lines.Add(L(-1.1f, 0.5f, -0.5f, 0.5f));
                    s.Lines.Add(L(-1.1f, -0.5f, -0.5f, -0.5f)); s.Lines.Add(L(0.5f, 0, 1.1f, 0)); s.Lines.Add(L(0, -1.3f, 0, -0.8f));
                    s.Texts.Add(("0", -0.3f, 0.5f, 0.28f, false)); s.Texts.Add(("1", -0.3f, -0.5f, 0.28f, false));
                    s.Pins["a"] = Pin(-1.1f, 0.5f, -1, 0); s.Pins["b"] = Pin(-1.1f, -0.5f, -1, 0); s.Pins["s"] = Pin(0, -1.3f, 0, -1); s.Pins["y"] = Pin(1.1f, 0, 1, 0);
                    break;
                default: throw new Exception($"unknown part '{kind}'");
            }
            return s;
        }

        public struct RoutePt
        {
            public Vector3 P;
            public Vector2? Dir;
        }

        /// <summary>Orthogonal ("Manhattan") route through points, schematic style (parts.js route).</summary>
        public static List<Vector3> Route(List<RoutePt> pts, string mode = "auto")
        {
            var outp = new List<Vector3> { pts[0].P };
            void Push(float x, float y, float z)
            {
                var last = outp[outp.Count - 1];
                if (Mathf.Abs(last.x - x) < 1e-6f && Mathf.Abs(last.y - y) < 1e-6f) return;
                outp.Add(new Vector3(x, y, z));
            }
            bool? Horiz(Vector2? d) => d.HasValue ? Mathf.Abs(d.Value.x) >= Mathf.Abs(d.Value.y) : (bool?)null;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[i + 1];
                float z = b.P.z;
                string m = mode;
                if (m == "auto")
                {
                    bool? ha = Horiz(a.Dir), hb = Horiz(b.Dir);
                    if (ha == null && hb == null) m = "hv";
                    else if (ha != null && hb != null) m = ha.Value && hb.Value ? "zh" : !ha.Value && !hb.Value ? "zv" : ha.Value ? "hv" : "vh";
                    else m = (ha ?? hb).Value ? (ha != null ? "hv" : "vh") : (ha != null ? "vh" : "hv");
                }
                if (m == "direct") Push(b.P.x, b.P.y, z);
                else if (m == "hv") { Push(b.P.x, a.P.y, z); Push(b.P.x, b.P.y, z); }
                else if (m == "vh") { Push(a.P.x, b.P.y, z); Push(b.P.x, b.P.y, z); }
                else if (m == "zh")
                {
                    float xm = (a.P.x + b.P.x) / 2;
                    if (a.Dir.HasValue && (xm - a.P.x) * a.Dir.Value.x < 0.3f && b.Dir.HasValue && (xm - b.P.x) * b.Dir.Value.x < 0.3f)
                        xm = a.P.x + 0.3f * (a.Dir.Value.x < 0 ? -1 : 1);
                    Push(xm, a.P.y, z); Push(xm, b.P.y, z); Push(b.P.x, b.P.y, z);
                }
                else
                {
                    float ym = (a.P.y + b.P.y) / 2;
                    Push(a.P.x, ym, z); Push(b.P.x, ym, z); Push(b.P.x, b.P.y, z);
                }
            }
            var merged = new List<Vector3> { outp[0] };
            for (int i = 1; i < outp.Count; i++)
            {
                var p = merged[merged.Count - 1]; var q = outp[i];
                if (i + 1 < outp.Count)
                {
                    var n = outp[i + 1];
                    if ((Mathf.Abs(p.x - q.x) < 1e-6f && Mathf.Abs(q.x - n.x) < 1e-6f) || (Mathf.Abs(p.y - q.y) < 1e-6f && Mathf.Abs(q.y - n.y) < 1e-6f)) continue;
                }
                merged.Add(q);
            }
            return merged;
        }
    }
}
