// Typeset maths for `tex`: a small TeX-style layout engine (the web viewer
// uses MathJax, which Unity doesn't have). LaTeX is parsed into atoms, laid out
// as boxes (width, height above the baseline, depth below) with TeX's rules for
// fractions, scripts, radicals, limits, delimiters, accents, braces, matrices
// and inter-atom spacing, then drawn as TextMeshPro runs in DejaVu Serif plus
// one mesh for the rules (fraction bars, radicals, braces, brackets, accents).
// All lengths are in em until Render scales them.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;

namespace Vcl
{
    public partial class Runtime
    {
        void BuildTex(Entity e, Cmd c)
        {
            float size = c.Flt("size", 0.5f);
            string align = c.Str("align") ?? "center";
            e.Obj.localPosition = e.ToParent(c["at"]);
            string src = c.Str("text") ?? "";
            var color = Draw.Hex(c.Str("color"));
            float height = size;
            var redraw = Rebuilder(e, box =>
            {
                var r = MathTex.Render(box, src, size * MathTex.EmPerSize, color, align);
                foreach (var t in r.texts) e.Track(t, "text");
                if (r.rules) e.Track(r.rules.sharedMaterial, "text");
                height = r.height;
                if (e.In3D) e.Billboards.Add(box);
            });
            redraw();
            e.SetText = s => { src = s; redraw(); };
            LabelFor(e, c, new Vector3(0, -Mathf.Max(size * 0.9f, height / 2 + size * 0.4f), 0));
        }
    }

    public static class MathTex
    {
        /// <summary>em per `size` unit: one line of ordinary maths ≈ one size, as in the web viewer.</summary>
        public const float EmPerSize = 0.95f;

        // ── fonts and glyph metrics ──

        public enum Face { Upright, Italic, Bold, Sans }

        static TMP_FontAsset[] faces;
        static TMP_FontAsset FontOf(Face f)
        {
            if (faces == null)
            {
                faces = new TMP_FontAsset[4];
                var names = new[] { "DejaVuSerif", "DejaVuSerif-Italic", "DejaVuSerif-Bold" };
                for (int i = 0; i < 3; i++)
                {
                    faces[i] = Draw.Dynamic(names[i]);
                    if (faces[i] && Draw.Font) faces[i].fallbackFontAssetTable = new List<TMP_FontAsset> { Draw.Font };
                }
                faces[3] = Draw.Font;
                for (int i = 0; i < 3; i++) if (!faces[i]) faces[i] = Draw.Font;
            }
            return faces[(int)f];
        }

        struct Metric { public float W, H, D, Base; }
        static TextMeshPro probe;
        static readonly Dictionary<(string, Face), Metric> metrics = new Dictionary<(string, Face), Metric>();

        static TextMeshPro Probe()
        {
            if (probe) return probe;
            var go = new GameObject("tex-probe") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.position = new Vector3(0, -1000, 0);
            probe = Setup(go.AddComponent<TextMeshPro>());
            probe.renderer.enabled = false;
            return probe;
        }

        static TextMeshPro Setup(TextMeshPro t)
        {
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow;
            t.alignment = TextAlignmentOptions.BaselineLeft;
            t.rectTransform.pivot = new Vector2(0, 0.5f);
            t.rectTransform.sizeDelta = Vector2.zero;
            t.richText = false;
            t.extraPadding = true;
            t.fontSize = 10; // 1 em = 1 unit
            return t;
        }

        /// <summary>Width (advance), ink height and depth, and where TMP puts the baseline, at 1 em.</summary>
        static Metric Measure(string text, Face face)
        {
            if (metrics.TryGetValue((text, face), out var m)) return m;
            var t = Probe();
            t.font = FontOf(face);
            t.text = text;
            t.ForceMeshUpdate(true, true);
            float bl = t.textInfo.lineCount > 0 ? t.textInfo.lineInfo[0].baseline : 0;
            // Ink, from each glyph's box (textBounds is the line box: ascender to descender).
            float w = t.preferredWidth, top = float.MinValue, bot = float.MaxValue, right = 0;
            var info = t.textInfo;
            for (int k = 0; k < info.characterCount; k++)
            {
                var ci = info.characterInfo[k];
                if (!ci.isVisible) continue;
                var g = ci.textElement?.glyph;
                if (g == null) continue;
                // The glyph's own box (topLeft/bottomLeft include the SDF padding).
                var gm = g.metrics;
                float yTop = ci.baseLine + gm.horizontalBearingY * ci.scale;
                top = Mathf.Max(top, yTop);
                bot = Mathf.Min(bot, yTop - gm.height * ci.scale);
                right = Mathf.Max(right, ci.origin + (gm.horizontalBearingX + gm.width) * ci.scale);
            }
            m = new Metric { W = Mathf.Max(w, right), Base = bl };
            if (top > bot) { m.H = Mathf.Max(0, top - bl); m.D = Mathf.Max(0, bl - bot); }
            else { m.H = 0.7f; m.D = 0; }
            metrics[(text, face)] = m;
            return m;
        }

        /// <summary>Glyph metrics for a few characters (snapshot debugging: VCL_TEXDBG=1).</summary>
        public static string DebugMetrics(string chars)
        {
            var sb = new StringBuilder();
            foreach (var ch in chars) { var m = Measure(ch.ToString(), Face.Upright); sb.Append($"{ch}: W={m.W:F2} H={m.H:F2} D={m.D:F2} base={m.Base:F2}; "); }
            return sb.ToString();
        }

        static float axis = -1, xHeight;
        static float Axis { get { if (axis < 0) Init(); return axis; } }
        static float XHeight { get { if (axis < 0) Init(); return xHeight; } }
        static void Init()
        {
            var plus = Measure("+", Face.Upright);
            axis = (plus.H - plus.D) / 2;
            xHeight = Measure("x", Face.Upright).H;
        }
        const float Rule = 0.05f;

        // ── boxes ──

        class Glyph { public string Text; public Face Face; public float X, Y, Scale = 1, SX = 1, SY = 1; }

        class Box
        {
            public float W, H, D;
            public readonly List<Glyph> Glyphs = new List<Glyph>();
            public readonly List<List<Vector2>> Strokes = new List<List<Vector2>>();
            public readonly List<float> Widths = new List<float>();
            public readonly List<List<Vector2>> Fills = new List<List<Vector2>>();

            public void Add(Box b, float dx, float dy)
            {
                foreach (var g in b.Glyphs) Glyphs.Add(new Glyph { Text = g.Text, Face = g.Face, X = g.X + dx, Y = g.Y + dy, Scale = g.Scale, SX = g.SX, SY = g.SY });
                for (int i = 0; i < b.Strokes.Count; i++) { Strokes.Add(b.Strokes[i].Select(p => p + new Vector2(dx, dy)).ToList()); Widths.Add(b.Widths[i]); }
                foreach (var f in b.Fills) Fills.Add(f.Select(p => p + new Vector2(dx, dy)).ToList());
            }

            public void Line(float w, params Vector2[] pts) { Strokes.Add(pts.ToList()); Widths.Add(w); }
            public void Rect(float x0, float y0, float x1, float y1) =>
                Fills.Add(new List<Vector2> { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) });

            /// <summary>Lay boxes side by side on one baseline.</summary>
            public static Box Row(IEnumerable<Box> parts)
            {
                var r = new Box();
                foreach (var p in parts)
                {
                    r.Add(p, r.W, 0);
                    r.W += p.W;
                    r.H = Mathf.Max(r.H, p.H);
                    r.D = Mathf.Max(r.D, p.D);
                }
                return r;
            }

            public static Box Space(float w) => new Box { W = w };

            public Box Scaled(float k)
            {
                var r = new Box { W = W * k, H = H * k, D = D * k };
                foreach (var g in Glyphs) r.Glyphs.Add(new Glyph { Text = g.Text, Face = g.Face, X = g.X * k, Y = g.Y * k, Scale = g.Scale * k, SX = g.SX, SY = g.SY });
                for (int i = 0; i < Strokes.Count; i++) { r.Strokes.Add(Strokes[i].Select(p => p * k).ToList()); r.Widths.Add(Widths[i] * k); }
                foreach (var f in Fills) r.Fills.Add(f.Select(p => p * k).ToList());
                return r;
            }
        }

        static Box Run(string s, Face face)
        {
            var m = Measure(s, face);
            var b = new Box { W = m.W, H = m.H, D = m.D };
            b.Glyphs.Add(new Glyph { Text = s, Face = face });
            return b;
        }

        // ── styles ──

        enum Style { Display, Text, Script, ScriptScript }
        static Style Sup(Style s) => s <= Style.Text ? Style.Script : Style.ScriptScript;
        static Style FracPart(Style s) => s == Style.Display ? Style.Text : Sup(s);
        static float Scale(Style s) => s == Style.Script ? 0.7f : s == Style.ScriptScript ? 0.5f : 1;

        /// <summary>Lay out `n` in style s at its own size, then scale relative to `parent`.</summary>
        static Box In(Node n, Style s, Style parent) => n.Layout(s).Scaled(Scale(s) / Scale(parent));

        // ── atoms ──

        enum Kind { Ord, Op, Bin, Rel, Open, Close, Punct, Inner }

        abstract class Node
        {
            public Kind Kind = Kind.Ord;
            public abstract Box Layout(Style s); // in em of style s
            public virtual bool Limits(Style s) => false;
            public virtual bool Italic => false;
        }

        class Sym : Node
        {
            public string Text; public Face Face;
            public Sym(string t, Face f, Kind k) { Text = t; Face = f; Kind = k; }
            public override bool Italic => Face == Face.Italic;
            public override Box Layout(Style s) => Run(Text, Face);
        }

        class Kern : Node
        {
            public float W;
            public Kern(float w) { W = w; Kind = Kind.Inner; }
            public override Box Layout(Style s) => Box.Space(W);
        }

        class Seq : Node
        {
            public List<Node> Items = new List<Node>();
            public override bool Italic => Items.Count == 1 && Items[0].Italic;
            public override Box Layout(Style s) => LayoutList(Items, s);
        }

        class StyleSet : Node
        {
            public Style To;
            public Seq Body;
            public override Box Layout(Style s) => In(Body, To, s);
        }

        class Scripts : Node
        {
            public Node Base, Sub, Sup;
            public override bool Italic => Base?.Italic ?? false;
            public override Box Layout(Style s)
            {
                var b = Base?.Layout(s) ?? new Box();
                if (Base != null && Base.Limits(s)) return LimitsLayout(b, s);
                var ss = Sup(s);
                Box up = Sup != null ? In(Sup, ss, s) : null, dn = Sub != null ? In(Sub, ss, s) : null;
                bool single = b.H < 0.8f && b.D < 0.3f;
                float u = single ? (s == Style.Display ? 0.41f : 0.36f) : b.H - 0.25f;
                float v = single ? 0.17f : b.D + 0.1f;
                if (up != null) u = Mathf.Max(u, up.D + XHeight / 4);
                if (dn != null) v = Mathf.Max(v, dn.H - XHeight * 0.8f);
                if (up != null && dn != null)
                {
                    float gap = (u - up.D) - (dn.H - v);
                    if (gap < 4 * Rule) v += 4 * Rule - gap;
                }
                float italic = Base != null && Base.Italic ? 0.06f : 0;
                var r = new Box { W = b.W, H = b.H, D = b.D };
                r.Add(b, 0, 0);
                float w = 0;
                if (up != null) { r.Add(up, b.W + italic, u); w = Mathf.Max(w, up.W + italic); r.H = Mathf.Max(r.H, u + up.H); }
                if (dn != null) { r.Add(dn, b.W - (Base is BigOp ? 0.12f : 0), -v); w = Mathf.Max(w, dn.W); r.D = Mathf.Max(r.D, v + dn.D); }
                r.W = b.W + w + 0.04f;
                return r;
            }

            Box LimitsLayout(Box b, Style s)
            {
                var ss = Sup(s);
                Box up = Sup != null ? In(Sup, ss, s) : null, dn = Sub != null ? In(Sub, ss, s) : null;
                float W = Mathf.Max(b.W, Mathf.Max(up?.W ?? 0, dn?.W ?? 0));
                var r = new Box { W = W, H = b.H, D = b.D };
                r.Add(b, (W - b.W) / 2, 0);
                float gap0 = Base is HBrace ? 0.24f : 0.14f;
                if (up != null) { float y = b.H + gap0 + up.D; r.Add(up, (W - up.W) / 2, y); r.H = y + up.H; }
                float gap = Base is HBrace ? 0.24f : 0.14f;
                if (dn != null) { float y = b.D + gap + dn.H; r.Add(dn, (W - dn.W) / 2, -y); r.D = y + dn.D; }
                return r;
            }
        }

        class Frac : Node
        {
            public Node Num, Den; public Style? Force; public bool Bar = true;
            public Frac() { Kind = Kind.Inner; }
            public override Box Layout(Style s0)
            {
                var s = Force ?? s0;
                var ps = FracPart(s);
                var n = In(Num, ps, s0);
                var d = In(Den, ps, s0);
                float k = Scale(s) / Scale(s0); // a forced \tfrac inside display shrinks its parts, not the bar
                float a = Axis, gap = s == Style.Display ? 3 * Rule : Rule;
                float u = Mathf.Max(s == Style.Display ? 0.68f * k : 0.39f * k, a + Rule / 2 + gap + n.D);
                float v = Mathf.Max(s == Style.Display ? 0.69f * k : 0.35f * k, -(a - Rule / 2 - gap) + d.H);
                if (!Bar) { u = Mathf.Max(u, n.D + 0.3f); v = Mathf.Max(v, d.H + 0.3f); }
                float W = Mathf.Max(n.W, d.W) + 0.24f;
                var r = new Box { W = W + 0.12f, H = u + n.H, D = v + d.D };
                r.Add(n, 0.06f + (W - n.W) / 2, u);
                r.Add(d, 0.06f + (W - d.W) / 2, -v);
                if (Bar) r.Rect(0.06f, a - Rule / 2, 0.06f + W, a + Rule / 2);
                return r;
            }
        }

        class Sqrt : Node
        {
            public Node Body, Index;
            public override Box Layout(Style s)
            {
                var b = Body.Layout(s);
                float clr = s == Style.Display ? Rule + XHeight / 4 : Rule * 1.25f + Rule;
                float top = Mathf.Max(b.H, XHeight) + clr + Rule, bot = Mathf.Max(b.D, 0.1f) + 0.06f;
                float h = top + bot, sw = Mathf.Min(0.75f, 0.42f + 0.08f * h);
                var r = new Box { H = top + Rule / 2, D = bot };
                float x0 = 0.05f;
                var idx = Index != null ? In(Index, Style.ScriptScript, s) : null;
                if (idx != null) x0 = Mathf.Max(x0, idx.W - sw * 0.45f);
                float yMid = -bot + h * 0.42f;
                // The radical: a short tick up, a thick stroke down, a long stroke up, then the vinculum.
                r.Line(Rule * 0.9f, new Vector2(x0, yMid - 0.02f), new Vector2(x0 + sw * 0.18f, yMid + 0.06f));
                r.Line(Rule * 1.7f, new Vector2(x0 + sw * 0.18f, yMid + 0.06f), new Vector2(x0 + sw * 0.48f, -bot));
                r.Line(Rule * 0.9f, new Vector2(x0 + sw * 0.48f, -bot), new Vector2(x0 + sw, top), new Vector2(x0 + sw + b.W + 0.1f, top));
                r.Add(b, x0 + sw + 0.05f, 0);
                if (idx != null) { r.Add(idx, x0 + sw * 0.45f - idx.W, yMid + 0.12f + idx.D); r.H = Mathf.Max(r.H, yMid + 0.12f + idx.D + idx.H); }
                r.W = x0 + sw + b.W + 0.16f;
                return r;
            }
        }

        class BigOp : Node
        {
            public string Sym; public bool Integral; public bool? ForceLimits;
            public BigOp() { Kind = Kind.Op; }
            public override bool Limits(Style s) => ForceLimits ?? (!Integral && s == Style.Display);
            public override Box Layout(Style s)
            {
                float k = Integral ? (s == Style.Display ? 2.0f : 1.4f) : (s == Style.Display ? 1.55f : 1.15f);
                var g = Run(Sym, Integral ? Face.Upright : Face.Sans).Scaled(k);
                // Centre it on the axis.
                float mid = (g.H - g.D) / 2, dy = Axis - mid;
                var r = new Box { W = g.W + (Integral ? 0.08f : 0.04f), H = g.H + dy, D = g.D - dy };
                r.Add(g, Integral ? 0.04f : 0.02f, dy);
                return r;
            }
        }

        /// <summary>An operator name: lim, max, sin…; limits go under it in display style.</summary>
        class OpName : Node
        {
            public string Name; public bool CanLimit; public bool? ForceLimits;
            public OpName() { Kind = Kind.Op; }
            public override bool Limits(Style s) => ForceLimits ?? (CanLimit && s == Style.Display);
            public override Box Layout(Style s) => Run(Name, Face.Upright);
        }

        /// <summary>A delimiter at a given total height (null: natural size).</summary>
        static Box Delim(string d, float? want)
        {
            if (string.IsNullOrEmpty(d) || d == ".") return Box.Space(0.12f);
            var nat = Run(d, Face.Upright);
            float natH = nat.H + nat.D;
            if (want == null || want.Value <= natH * 1.08f)
            {
                return nat;
            }
            float h = want.Value, a = Axis, top = a + h / 2, bot = a - h / 2;
            float w = Rule * 1.1f;
            var r = new Box { H = top, D = -bot };
            float thick = Mathf.Min(0.09f, 0.05f + h * 0.01f);
            switch (d)
            {
                case "[": case "]": case "⌊": case "⌋": case "⌈": case "⌉":
                {
                    float x0 = 0.12f, x1 = 0.12f + 0.24f;
                    bool left = d == "[" || d == "⌊" || d == "⌈";
                    bool hasTop = d == "[" || d == "]" || d == "⌈" || d == "⌉", hasBot = d == "[" || d == "]" || d == "⌊" || d == "⌋";
                    float xs = left ? x0 : x1, xe = left ? x1 : x0;
                    r.Line(thick * 0.9f, new Vector2(xs, bot), new Vector2(xs, top));
                    if (hasTop) r.Line(w, new Vector2(xs, top - w / 2), new Vector2(xe, top - w / 2));
                    if (hasBot) r.Line(w, new Vector2(xs, bot + w / 2), new Vector2(xe, bot + w / 2));
                    r.W = 0.48f;
                    return r;
                }
                case "|": case "‖":
                {
                    r.Line(w * 1.2f, new Vector2(0.14f, bot), new Vector2(0.14f, top));
                    if (d == "‖") { r.Line(w * 1.2f, new Vector2(0.3f, bot), new Vector2(0.3f, top)); r.W = 0.44f; }
                    else r.W = 0.28f;
                    return r;
                }
                case "⟨": case "⟩":
                {
                    float xa = 0.1f, xb = 0.1f + Mathf.Min(0.45f, 0.18f + h * 0.08f);
                    if (d == "⟨") r.Line(w, new Vector2(xb, top), new Vector2(xa, a), new Vector2(xb, bot));
                    else r.Line(w, new Vector2(xa, top), new Vector2(xb, a), new Vector2(xa, bot));
                    r.W = xb + 0.1f;
                    return r;
                }
                case "(": case ")":
                {
                    // A crescent: two arcs, thin at the ends and thick in the middle.
                    float bulge = Mathf.Min(0.42f, 0.16f + h * 0.06f), x = 0.1f, W = 2 * x + bulge + thick;
                    var outer = new List<Vector2>();
                    var inner = new List<Vector2>();
                    const int n = 24;
                    for (int i = 0; i <= n; i++)
                    {
                        float t = i / (float)n, y = Mathf.Lerp(bot, top, t), c = Mathf.Sin(t * Mathf.PI);
                        float xo = x + bulge * (1 - c), xi = xo + 0.02f + thick * c;
                        if (d == ")") { xo = W - xo; xi = W - xi; }
                        outer.Add(new Vector2(xo, y));
                        inner.Add(new Vector2(xi, y));
                    }
                    inner.Reverse();
                    outer.AddRange(inner);
                    r.Fills.Add(outer);
                    r.W = W;
                    return r;
                }
                case "{": case "}":
                {
                    float bw = 0.36f;
                    var pts = BracePath(h, bw);
                    var shifted = pts.Select(p => new Vector2(d == "{" ? 0.1f + p.y : 0.1f + bw - p.y, a + p.x - h / 2)).ToList();
                    r.Strokes.Add(shifted); r.Widths.Add(thick);
                    r.W = bw + 0.2f;
                    return r;
                }
            }
            // Anything else: stretch the glyph.
            var g = Run(d, Face.Upright);
            var gl = g.Glyphs[0];
            gl.SY = h / Mathf.Max(0.1f, natH);
            gl.Y = bot + nat.D * gl.SY;
            return new Box { W = g.W, H = top, D = -bot, Glyphs = { gl } };
        }

        /// <summary>A brace along x from 0 to len: its ends at y = bw, its shaft at
        /// bw/2 and its tip at (len/2, 0).</summary>
        static List<Vector2> BracePath(float len, float bw)
        {
            var pts = new List<Vector2>();
            float r = Mathf.Min(bw / 2, len / 4);
            void Arc(float cx, float cy, float a0, float a1)
            {
                for (int i = 0; i <= 8; i++)
                {
                    float t = Mathf.Lerp(a0, a1, i / 8f);
                    pts.Add(new Vector2(cx + r * Mathf.Cos(t), cy + r * Mathf.Sin(t)));
                }
            }
            float pi = Mathf.PI;
            Arc(r, bw, pi, 1.5f * pi);
            Arc(len / 2 - r, bw - 2 * r, 0.5f * pi, 0);
            Arc(len / 2 + r, bw - 2 * r, pi, 0.5f * pi);
            Arc(len - r, bw, 1.5f * pi, 2 * pi);
            return pts;
        }

        class LeftRight : Node
        {
            public string L, R; public Seq Body; public List<string> Middles = new List<string>();
            public LeftRight() { Kind = Kind.Inner; }
            public override Box Layout(Style s)
            {
                var b = Body.Layout(s);
                float a = Axis, half = Mathf.Max(b.H - a, b.D + a);
                float want = Mathf.Max(half * 2 * 0.901f, half * 2 - 0.5f) + 0.1f;
                var l = Delim(L, want); var r = Delim(R, want);
                return Box.Row(new[] { l, b, r });
            }
        }

        class Big : Node
        {
            public string D; public float Size;
            public override Box Layout(Style s) => Delim(D, Size);
        }

        class Accent : Node
        {
            public Node Body; public string What; public bool Wide;
            public override bool Italic => Body.Italic;
            public override Box Layout(Style s)
            {
                var b = Body.Layout(s);
                float skew = Body.Italic ? 0.06f : 0, cx = b.W / 2 + skew, y = Mathf.Max(b.H, XHeight) + 0.05f;
                var r = new Box { W = b.W, H = b.H, D = b.D };
                r.Add(b, 0, 0);
                float aw = Wide ? b.W : Mathf.Min(b.W * 0.9f, 0.42f);
                float lw = Rule * 0.9f;
                switch (What)
                {
                    case "hat":
                    {
                        float hh = Wide ? Mathf.Min(0.25f, 0.12f + aw * 0.04f) : 0.13f;
                        aw = Wide ? aw : Mathf.Max(aw, 0.3f);
                        r.Line(lw, new Vector2(cx - aw / 2, y), new Vector2(cx, y + hh), new Vector2(cx + aw / 2, y));
                        r.H = y + hh + lw;
                        break;
                    }
                    case "check":
                        r.Line(lw, new Vector2(cx - 0.15f, y + 0.12f), new Vector2(cx, y), new Vector2(cx + 0.15f, y + 0.12f));
                        r.H = y + 0.15f;
                        break;
                    case "bar":
                    case "overline":
                    {
                        float x0 = What == "overline" ? 0.02f : cx - Mathf.Max(aw, 0.3f) / 2;
                        float x1 = What == "overline" ? b.W - 0.02f : cx + Mathf.Max(aw, 0.3f) / 2;
                        r.Rect(x0, y, x1, y + Rule);
                        r.H = y + Rule + 0.02f;
                        break;
                    }
                    case "underline":
                    {
                        float yd = -Mathf.Max(b.D, 0) - 0.1f;
                        r.Rect(0.02f, yd - Rule, b.W - 0.02f, yd);
                        r.D = -yd + Rule + 0.02f;
                        break;
                    }
                    case "vec":
                    {
                        float len = Wide ? b.W : Mathf.Max(0.4f, aw), x0 = cx - len / 2, x1 = cx + len / 2, yy = y + 0.06f;
                        r.Line(lw, new Vector2(x0, yy), new Vector2(x1, yy));
                        r.Line(lw, new Vector2(x1 - 0.1f, yy + 0.07f), new Vector2(x1, yy), new Vector2(x1 - 0.1f, yy - 0.07f));
                        r.H = yy + 0.1f;
                        break;
                    }
                    case "tilde":
                    {
                        float len = Wide ? b.W : Mathf.Max(0.36f, aw);
                        var pts = new List<Vector2>();
                        for (int i = 0; i <= 16; i++)
                        {
                            float t = i / 16f;
                            pts.Add(new Vector2(cx - len / 2 + len * t, y + 0.06f + 0.05f * Mathf.Sin(t * 2 * Mathf.PI)));
                        }
                        r.Strokes.Add(pts); r.Widths.Add(lw);
                        r.H = y + 0.14f;
                        break;
                    }
                    case "dot":
                    case "ddot":
                    {
                        var xs = What == "dot" ? new[] { cx } : new[] { cx - 0.09f, cx + 0.09f };
                        foreach (var x in xs) r.Fills.Add(Circle(new Vector2(x, y + 0.05f), 0.045f));
                        r.H = y + 0.1f;
                        break;
                    }
                }
                return r;
            }
        }

        static List<Vector2> Circle(Vector2 c, float rad)
        {
            var p = new List<Vector2>();
            for (int i = 0; i < 12; i++) { float t = i / 12f * 2 * Mathf.PI; p.Add(c + new Vector2(Mathf.Cos(t), Mathf.Sin(t)) * rad); }
            return p;
        }

        class HBrace : Node
        {
            public Node Body; public bool Under; public Node Label;
            public override bool Limits(Style s) => true;
            public override Box Layout(Style s)
            {
                var b = Body.Layout(s);
                float bw = 0.22f;
                var pts = BracePath(Mathf.Max(b.W, 0.5f), bw);
                var r = new Box { W = b.W, H = b.H, D = b.D };
                r.Add(b, 0, 0);
                if (Under)
                {
                    float y0 = -b.D - 0.1f;
                    r.Strokes.Add(pts.Select(p => new Vector2(p.x, y0 - (bw - p.y))).ToList()); r.Widths.Add(Rule * 1.1f);
                    r.D = -y0 + bw + 0.03f;
                }
                else
                {
                    float y0 = b.H + 0.1f;
                    r.Strokes.Add(pts.Select(p => new Vector2(p.x, y0 + (bw - p.y))).ToList()); r.Widths.Add(Rule * 1.1f);
                    r.H = y0 + bw + 0.03f;
                }
                return r;
            }
        }

        /// <summary>\xrightarrow{above}, \overset, \stackrel, \underset: a base with things above/below.</summary>
        class Stack : Node
        {
            public Node Base, Over, Under; public string Arrow; // "→" or "←" drawn to fit
            public Stack() { Kind = Kind.Rel; }
            public override Box Layout(Style s)
            {
                var ss = Sup(s);
                Box o = Over != null ? In(Over, ss, s) : null, u = Under != null ? In(Under, ss, s) : null;
                Box b;
                if (Arrow != null)
                {
                    float len = Mathf.Max(1.1f, Mathf.Max(o?.W ?? 0, u?.W ?? 0) + 0.5f), a = Axis, lw = Rule * 0.9f;
                    b = new Box { W = len + 0.2f, H = a + 0.12f, D = -(a - 0.12f) };
                    float x0 = 0.1f, x1 = 0.1f + len;
                    b.Line(lw, new Vector2(x0, a), new Vector2(x1, a));
                    if (Arrow == "→") b.Line(lw, new Vector2(x1 - 0.14f, a + 0.1f), new Vector2(x1, a), new Vector2(x1 - 0.14f, a - 0.1f));
                    else b.Line(lw, new Vector2(x0 + 0.14f, a + 0.1f), new Vector2(x0, a), new Vector2(x0 + 0.14f, a - 0.1f));
                }
                else b = Base.Layout(s);
                float W = Mathf.Max(b.W, Mathf.Max(o?.W ?? 0, u?.W ?? 0));
                var r = new Box { W = W, H = b.H, D = b.D };
                r.Add(b, (W - b.W) / 2, 0);
                if (o != null) { float y = b.H + 0.08f + o.D; r.Add(o, (W - o.W) / 2, y); r.H = y + o.H; }
                if (u != null) { float y = b.D + 0.08f + u.H; r.Add(u, (W - u.W) / 2, -y); r.D = y + u.D; }
                return r;
            }
        }

        class Matrix : Node
        {
            public List<List<Seq>> Rows = new List<List<Seq>>();
            public string Env;
            public Matrix() { Kind = Kind.Inner; }
            public override Box Layout(Style s)
            {
                bool aligned = Env == "aligned" || Env == "align" || Env == "align*" || Env == "split" || Env == "alignat" || Env == "eqnarray";
                bool cases = Env == "cases" || Env == "dcases" || Env == "rcases";
                bool gathered = Env == "gathered" || Env == "gather" || Env == "gather*";
                bool small = Env == "smallmatrix";
                var cs = aligned || gathered || Env == "dcases" ? s : small ? Style.Script : s == Style.Display ? Style.Text : s;
                var cells = Rows.Select(row => row.Select(c => In(c, cs, s)).ToList()).ToList();
                int nc = cells.Count == 0 ? 0 : cells.Max(r => r.Count);
                var colW = new float[nc];
                foreach (var row in cells) for (int j = 0; j < row.Count; j++) colW[j] = Mathf.Max(colW[j], row[j].W);
                float gapX = aligned ? 0 : cases ? 1.0f : small ? 0.35f : 0.8f;
                float strutH = small ? 0.5f : 0.72f, strutD = small ? 0.2f : 0.28f, gapY = aligned || gathered ? 0.35f : small ? 0.1f : 0.22f;
                var r = new Box();
                float y = 0;
                var rowY = new List<float>();
                for (int i = 0; i < cells.Count; i++)
                {
                    float h = Mathf.Max(strutH, cells[i].Count == 0 ? 0 : cells[i].Max(c => c.H));
                    float d = Mathf.Max(strutD, cells[i].Count == 0 ? 0 : cells[i].Max(c => c.D));
                    if (i > 0) y -= gapY + h;
                    rowY.Add(y);
                    float x = 0;
                    for (int j = 0; j < nc; j++)
                    {
                        if (j < cells[i].Count)
                        {
                            var c = cells[i][j];
                            // aligned: right, left, right, left…; cases: left; matrices: centred.
                            float cx = aligned ? (j % 2 == 0 ? x + colW[j] - c.W : x) : cases ? x : x + (colW[j] - c.W) / 2;
                            r.Add(c, cx, y);
                        }
                        x += colW[j] + (aligned ? (j % 2 == 1 ? 1.2f : 0) : gapX);
                    }
                    r.W = Mathf.Max(r.W, x - (aligned ? (nc % 2 == 0 ? 1.2f : 0) : gapX));
                    if (i == 0) r.H = h;
                    y -= d;
                }
                r.D = -y;
                // Centre on the axis.
                float mid = (r.H - r.D) / 2, dy = Axis - mid;
                var c2 = new Box { W = r.W, H = r.H + dy, D = r.D - dy };
                c2.Add(r, 0, dy);
                string L = null, R = null;
                switch (Env)
                {
                    case "pmatrix": L = "("; R = ")"; break;
                    case "bmatrix": L = "["; R = "]"; break;
                    case "Bmatrix": L = "{"; R = "}"; break;
                    case "vmatrix": L = "|"; R = "|"; break;
                    case "Vmatrix": L = "‖"; R = "‖"; break;
                    case "cases": case "dcases": L = "{"; break;
                    case "rcases": R = "}"; break;
                }
                if (L == null && R == null) return c2;
                float want = c2.H + c2.D + 0.1f;
                var parts = new List<Box>();
                if (L != null) { parts.Add(Delim(L, want)); parts.Add(Box.Space(0.08f)); }
                parts.Add(c2);
                if (R != null) { parts.Add(Box.Space(0.08f)); parts.Add(Delim(R, want)); }
                return Box.Row(parts);
            }
        }

        // ── spacing between atoms (TeX's table, in mu = em/18) ──

        static int Space(Kind a, Kind b, bool script)
        {
            int thin = 3, med = 4, thick = 5;
            int v;
            switch (a)
            {
                case Kind.Ord: v = b == Kind.Op ? thin : b == Kind.Bin ? med : b == Kind.Rel ? thick : b == Kind.Inner ? thin : 0; break;
                case Kind.Op: v = b == Kind.Ord || b == Kind.Op ? thin : b == Kind.Rel ? thick : b == Kind.Inner ? thin : 0; break;
                case Kind.Bin: v = b == Kind.Ord || b == Kind.Op || b == Kind.Open || b == Kind.Inner ? med : 0; break;
                case Kind.Rel: v = b == Kind.Ord || b == Kind.Op || b == Kind.Open || b == Kind.Inner ? thick : 0; break;
                case Kind.Close: v = b == Kind.Op ? thin : b == Kind.Bin ? med : b == Kind.Rel ? thick : b == Kind.Inner ? thin : 0; break;
                case Kind.Punct: v = b == Kind.Rel || b == Kind.Close || b == Kind.Punct ? (b == Kind.Rel ? thin : 0) : thin; break;
                case Kind.Inner: v = b == Kind.Bin ? med : b == Kind.Rel ? thick : b == Kind.Close || b == Kind.Punct ? (b == Kind.Punct ? thin : 0) : thin; break;
                default: v = 0; break;
            }
            if (script && v != thin) return 0;
            if (script && !(a == Kind.Op || b == Kind.Op)) return 0;
            return v;
        }

        static Box LayoutList(List<Node> items, Style s)
        {
            // A binary operator with nothing on its left (or after another operator) is an ordinary sign.
            var kinds = items.Select(n => n.Kind).ToList();
            Kind? prev = null;
            for (int i = 0; i < kinds.Count; i++)
            {
                if (items[i] is Kern) continue;
                if (kinds[i] == Kind.Bin)
                {
                    bool leftOk = prev.HasValue && prev != Kind.Bin && prev != Kind.Op && prev != Kind.Rel && prev != Kind.Open && prev != Kind.Punct;
                    int nx = i + 1;
                    while (nx < kinds.Count && items[nx] is Kern) nx++;
                    bool rightOk = nx < kinds.Count && kinds[nx] != Kind.Rel && kinds[nx] != Kind.Close && kinds[nx] != Kind.Punct;
                    if (!leftOk || !rightOk) kinds[i] = Kind.Ord;
                }
                prev = kinds[i];
            }
            var parts = new List<Box>();
            bool script = s >= Style.Script;
            Kind? last = null;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is Kern k) { parts.Add(Box.Space(k.W)); continue; }
                if (last.HasValue)
                {
                    int mu = Space(last.Value, kinds[i], script);
                    if (mu > 0) parts.Add(Box.Space(mu / 18f));
                }
                parts.Add(items[i].Layout(s));
                last = kinds[i];
            }
            return Box.Row(parts);
        }

        // ── parsing ──

        static readonly Dictionary<string, (string, Kind)> Symbols = new Dictionary<string, (string, Kind)>
        {
            // Greek (lower case italic, upper case upright, as TeX)
            ["alpha"] = ("α", Kind.Ord), ["beta"] = ("β", Kind.Ord), ["gamma"] = ("γ", Kind.Ord), ["delta"] = ("δ", Kind.Ord),
            ["epsilon"] = ("ϵ", Kind.Ord), ["varepsilon"] = ("ε", Kind.Ord), ["zeta"] = ("ζ", Kind.Ord), ["eta"] = ("η", Kind.Ord),
            ["theta"] = ("θ", Kind.Ord), ["vartheta"] = ("ϑ", Kind.Ord), ["iota"] = ("ι", Kind.Ord), ["kappa"] = ("κ", Kind.Ord),
            ["lambda"] = ("λ", Kind.Ord), ["mu"] = ("μ", Kind.Ord), ["nu"] = ("ν", Kind.Ord), ["xi"] = ("ξ", Kind.Ord),
            ["pi"] = ("π", Kind.Ord), ["varpi"] = ("ϖ", Kind.Ord), ["rho"] = ("ρ", Kind.Ord), ["varrho"] = ("ϱ", Kind.Ord),
            ["sigma"] = ("σ", Kind.Ord), ["varsigma"] = ("ς", Kind.Ord), ["tau"] = ("τ", Kind.Ord), ["upsilon"] = ("υ", Kind.Ord),
            ["phi"] = ("ϕ", Kind.Ord), ["varphi"] = ("φ", Kind.Ord), ["chi"] = ("χ", Kind.Ord), ["psi"] = ("ψ", Kind.Ord), ["omega"] = ("ω", Kind.Ord),
            ["Gamma"] = ("Γ", Kind.Ord), ["Delta"] = ("Δ", Kind.Ord), ["Theta"] = ("Θ", Kind.Ord), ["Lambda"] = ("Λ", Kind.Ord),
            ["Xi"] = ("Ξ", Kind.Ord), ["Pi"] = ("Π", Kind.Ord), ["Sigma"] = ("Σ", Kind.Ord), ["Upsilon"] = ("Υ", Kind.Ord),
            ["Phi"] = ("Φ", Kind.Ord), ["Psi"] = ("Ψ", Kind.Ord), ["Omega"] = ("Ω", Kind.Ord),
            // ordinary symbols
            ["infty"] = ("∞", Kind.Ord), ["partial"] = ("∂", Kind.Ord), ["nabla"] = ("∇", Kind.Ord), ["ell"] = ("ℓ", Kind.Ord),
            ["hbar"] = ("ℏ", Kind.Ord), ["emptyset"] = ("∅", Kind.Ord), ["varnothing"] = ("∅", Kind.Ord), ["forall"] = ("∀", Kind.Ord),
            ["exists"] = ("∃", Kind.Ord), ["neg"] = ("¬", Kind.Ord), ["lnot"] = ("¬", Kind.Ord), ["top"] = ("⊤", Kind.Ord), ["bot"] = ("⊥", Kind.Ord),
            ["prime"] = ("′", Kind.Ord), ["angle"] = ("∠", Kind.Ord), ["triangle"] = ("△", Kind.Ord), ["dagger"] = ("†", Kind.Ord),
            ["ldots"] = ("…", Kind.Inner), ["dots"] = ("…", Kind.Inner), ["cdots"] = ("⋯", Kind.Inner), ["vdots"] = ("⋮", Kind.Ord), ["ddots"] = ("⋱", Kind.Ord),
            ["Re"] = ("ℜ", Kind.Ord), ["Im"] = ("ℑ", Kind.Ord), ["aleph"] = ("ℵ", Kind.Ord), ["checkmark"] = ("✓", Kind.Ord),
            ["%"] = ("%", Kind.Ord), ["&"] = ("&", Kind.Ord), ["#"] = ("#", Kind.Ord), ["_"] = ("_", Kind.Ord), ["$"] = ("$", Kind.Ord),
            ["{"] = ("{", Kind.Open), ["}"] = ("}", Kind.Close),
            // binary operators
            ["cdot"] = ("⋅", Kind.Bin), ["times"] = ("×", Kind.Bin), ["div"] = ("÷", Kind.Bin), ["pm"] = ("±", Kind.Bin), ["mp"] = ("∓", Kind.Bin),
            ["ast"] = ("∗", Kind.Bin), ["star"] = ("⋆", Kind.Bin), ["circ"] = ("∘", Kind.Bin), ["bullet"] = ("∙", Kind.Bin),
            ["oplus"] = ("⊕", Kind.Bin), ["ominus"] = ("⊖", Kind.Bin), ["otimes"] = ("⊗", Kind.Bin), ["odot"] = ("⊙", Kind.Bin),
            ["cup"] = ("∪", Kind.Bin), ["cap"] = ("∩", Kind.Bin), ["land"] = ("∧", Kind.Bin), ["wedge"] = ("∧", Kind.Bin),
            ["lor"] = ("∨", Kind.Bin), ["vee"] = ("∨", Kind.Bin), ["setminus"] = ("∖", Kind.Bin), ["wr"] = ("≀", Kind.Bin),
            // relations
            ["leq"] = ("≤", Kind.Rel), ["le"] = ("≤", Kind.Rel), ["geq"] = ("≥", Kind.Rel), ["ge"] = ("≥", Kind.Rel),
            ["neq"] = ("≠", Kind.Rel), ["ne"] = ("≠", Kind.Rel), ["approx"] = ("≈", Kind.Rel), ["sim"] = ("∼", Kind.Rel),
            ["simeq"] = ("≃", Kind.Rel), ["cong"] = ("≅", Kind.Rel), ["equiv"] = ("≡", Kind.Rel), ["propto"] = ("∝", Kind.Rel),
            ["ll"] = ("≪", Kind.Rel), ["gg"] = ("≫", Kind.Rel), ["in"] = ("∈", Kind.Rel), ["notin"] = ("∉", Kind.Rel), ["ni"] = ("∋", Kind.Rel),
            ["subset"] = ("⊂", Kind.Rel), ["subseteq"] = ("⊆", Kind.Rel), ["supset"] = ("⊃", Kind.Rel), ["supseteq"] = ("⊇", Kind.Rel),
            ["to"] = ("→", Kind.Rel), ["rightarrow"] = ("→", Kind.Rel), ["leftarrow"] = ("←", Kind.Rel), ["gets"] = ("←", Kind.Rel),
            ["Rightarrow"] = ("⇒", Kind.Rel), ["Leftarrow"] = ("⇐", Kind.Rel), ["implies"] = ("⟹", Kind.Rel), ["impliedby"] = ("⟸", Kind.Rel),
            ["leftrightarrow"] = ("↔", Kind.Rel), ["Leftrightarrow"] = ("⇔", Kind.Rel), ["iff"] = ("⟺", Kind.Rel),
            ["longrightarrow"] = ("⟶", Kind.Rel), ["longleftarrow"] = ("⟵", Kind.Rel), ["Longrightarrow"] = ("⟹", Kind.Rel),
            ["mapsto"] = ("↦", Kind.Rel), ["uparrow"] = ("↑", Kind.Rel), ["downarrow"] = ("↓", Kind.Rel), ["updownarrow"] = ("↕", Kind.Rel),
            ["Uparrow"] = ("⇑", Kind.Rel), ["Downarrow"] = ("⇓", Kind.Rel), ["rightleftharpoons"] = ("⇌", Kind.Rel),
            ["mid"] = ("∣", Kind.Rel), ["parallel"] = ("∥", Kind.Rel), ["perp"] = ("⊥", Kind.Rel), ["models"] = ("⊨", Kind.Rel),
            ["vdash"] = ("⊢", Kind.Rel), ["prec"] = ("≺", Kind.Rel), ["succ"] = ("≻", Kind.Rel), ["preceq"] = ("⪯", Kind.Rel),
            ["doteq"] = ("≐", Kind.Rel), ["triangleq"] = ("≜", Kind.Rel), ["coloneqq"] = ("≔", Kind.Rel), [":="] = ("≔", Kind.Rel),
            ["leqslant"] = ("⩽", Kind.Rel), ["geqslant"] = ("⩾", Kind.Rel), ["lesssim"] = ("≲", Kind.Rel), ["gtrsim"] = ("≳", Kind.Rel),
            // punctuation and fences
            ["colon"] = (":", Kind.Punct), ["lbrace"] = ("{", Kind.Open), ["rbrace"] = ("}", Kind.Close),
            ["langle"] = ("⟨", Kind.Open), ["rangle"] = ("⟩", Kind.Close), ["lfloor"] = ("⌊", Kind.Open), ["rfloor"] = ("⌋", Kind.Close),
            ["lceil"] = ("⌈", Kind.Open), ["rceil"] = ("⌉", Kind.Close), ["lvert"] = ("|", Kind.Open), ["rvert"] = ("|", Kind.Close),
            ["lVert"] = ("‖", Kind.Open), ["rVert"] = ("‖", Kind.Close), ["vert"] = ("|", Kind.Ord), ["Vert"] = ("‖", Kind.Ord), ["|"] = ("‖", Kind.Ord),
            ["lbrack"] = ("[", Kind.Open), ["rbrack"] = ("]", Kind.Close), ["backslash"] = ("∖", Kind.Ord),
        };

        static readonly Dictionary<string, string> BigOps = new Dictionary<string, string>
        {
            ["sum"] = "∑", ["prod"] = "∏", ["coprod"] = "∐", ["bigcup"] = "⋃", ["bigcap"] = "⋂", ["bigoplus"] = "⨁", ["bigotimes"] = "⨂",
            ["bigvee"] = "⋁", ["bigwedge"] = "⋀", ["int"] = "∫", ["iint"] = "∬", ["iiint"] = "∭", ["oint"] = "∮",
        };

        static readonly HashSet<string> LimitNames = new HashSet<string> { "lim", "liminf", "limsup", "max", "min", "sup", "inf", "det", "Pr", "gcd", "argmax", "argmin" };
        static readonly HashSet<string> FnNames = new HashSet<string>
        {
            "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh", "log", "ln", "lg", "exp",
            "arg", "deg", "dim", "hom", "ker", "mod", "sgn", "tr", "rank", "diag", "Var", "Cov", "softmax",
        };

        static readonly Dictionary<char, string> Cal = new Dictionary<char, string>
        {
            ['A'] = "𝒜", ['B'] = "ℬ", ['C'] = "𝒞", ['D'] = "𝒟", ['E'] = "ℰ", ['F'] = "ℱ", ['G'] = "𝒢", ['H'] = "ℋ", ['I'] = "ℐ", ['J'] = "𝒥",
            ['K'] = "𝒦", ['L'] = "ℒ", ['M'] = "ℳ", ['N'] = "𝒩", ['O'] = "𝒪", ['P'] = "𝒫", ['Q'] = "𝒬", ['R'] = "ℛ", ['S'] = "𝒮", ['T'] = "𝒯",
            ['U'] = "𝒰", ['V'] = "𝒱", ['W'] = "𝒲", ['X'] = "𝒳", ['Y'] = "𝒴", ['Z'] = "𝒵",
        };
        static readonly Dictionary<char, string> BB = new Dictionary<char, string>
        { ['R'] = "ℝ", ['N'] = "ℕ", ['Z'] = "ℤ", ['Q'] = "ℚ", ['C'] = "ℂ", ['P'] = "ℙ", ['E'] = "𝔼", ['1'] = "𝟙", ['H'] = "ℍ" };

        enum Alpha { Math, Rm, Bf, Cal, BB, It, Tt, Sf }

        class Parser
        {
            readonly string s;
            int i;
            public Parser(string src) { s = src; }

            bool End => i >= s.Length;
            void SkipWs() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

            string PeekCmd()
            {
                if (i >= s.Length || s[i] != '\\') return null;
                int j = i + 1;
                if (j < s.Length && char.IsLetter(s[j])) { while (j < s.Length && char.IsLetter(s[j])) j++; return s.Substring(i + 1, j - i - 1); }
                return j < s.Length ? s[j].ToString() : "";
            }

            string ReadCmd()
            {
                var c = PeekCmd();
                i += 1 + c.Length;
                return c;
            }

            /// <summary>A list of atoms until '}', '&', '\\', \right, \end or the end.</summary>
            public Seq ParseList(Alpha font)
            {
                var l = new Seq();
                while (true)
                {
                    SkipWs();
                    if (End) break;
                    char ch = s[i];
                    if (ch == '}' || ch == '&') break;
                    var pc = PeekCmd();
                    if (pc == "\\" || pc == "right" || pc == "end" || pc == "middle" || pc == "cr") break;
                    if (ch == '\'')
                    {
                        // The font's prime already sits high: append it at full size rather than raise it again.
                        string primes = "";
                        while (i < s.Length && s[i] == '\'') { primes += "′"; i++; }
                        var last = l.Items.Count > 0 ? l.Items[l.Items.Count - 1] : null;
                        var seq = new Seq { Kind = last?.Kind ?? Kind.Ord };
                        if (last != null) { seq.Items.Add(last); l.Items.RemoveAt(l.Items.Count - 1); }
                        seq.Items.Add(new Sym(primes, Face.Upright, Kind.Ord));
                        l.Items.Add(seq);
                        continue;
                    }
                    if (ch == '^' || ch == '_')
                    {
                        var target = l.Items.Count > 0 ? l.Items[l.Items.Count - 1] : null;
                        Scripts sc = target as Scripts;
                        if (sc == null || ch == '^' && sc.Sup != null || ch == '_' && sc.Sub != null)
                        {
                            sc = new Scripts { Base = target, Kind = target?.Kind ?? Kind.Ord };
                            if (target != null) l.Items[l.Items.Count - 1] = sc; else l.Items.Add(sc);
                        }
                        i++;
                        var arg = Arg(font);
                        if (ch == '^') sc.Sup = Prepend(sc.Sup, arg); else sc.Sub = arg;
                        continue;
                    }
                    var atom = Atom(font, l);
                    if (atom != null) l.Items.Add(atom);
                }
                return Merge(l);
            }

            static Node Prepend(Node prev, Node add)
            {
                if (prev == null) return add;
                var l = new Seq(); l.Items.Add(prev); l.Items.Add(add);
                return l;
            }

            /// <summary>Join neighbouring digits (and a decimal point) into one run, like 0.39999.</summary>
            static Seq Merge(Seq l)
            {
                var r = new Seq();
                foreach (var n in l.Items)
                {
                    if (n is Sym a && r.Items.Count > 0 && r.Items[r.Items.Count - 1] is Sym b
                        && a.Face == Face.Upright && b.Face == Face.Upright && IsNum(a.Text) && IsNum(b.Text))
                    { r.Items[r.Items.Count - 1] = new Sym(b.Text + a.Text, Face.Upright, Kind.Ord); continue; }
                    r.Items.Add(n);
                }
                return r;
            }

            static bool IsNum(string t) => t.All(c => char.IsDigit(c) || c == '.');

            /// <summary>A macro argument: a {group} or a single token.</summary>
            Node Arg(Alpha font)
            {
                SkipWs();
                if (End) return new Seq();
                if (s[i] == '{')
                {
                    i++;
                    var l = ParseList(font);
                    if (!End && s[i] == '}') i++;
                    return l;
                }
                if (s[i] == '\\')
                {
                    var l = new Seq();
                    var a = Atom(font, l);
                    if (a != null) l.Items.Add(a);
                    return l.Items.Count == 1 ? l.Items[0] : l;
                }
                return CharAtom(s[i++], font);
            }

            string RawArg()
            {
                SkipWs();
                if (End) return "";
                if (s[i] != '{') return s[i++].ToString();
                int depth = 0, st = i;
                for (; i < s.Length; i++)
                {
                    if (s[i] == '\\') { i++; continue; }
                    if (s[i] == '{') depth++;
                    else if (s[i] == '}' && --depth == 0) { i++; return s.Substring(st + 1, i - st - 2); }
                }
                return s.Substring(st + 1);
            }

            string Optional()
            {
                SkipWs();
                if (End || s[i] != '[') return null;
                int j = s.IndexOf(']', i);
                if (j < 0) return null;
                var r = s.Substring(i + 1, j - i - 1);
                i = j + 1;
                return r;
            }

            Node CharAtom(char ch, Alpha font)
            {
                if (char.IsLetter(ch))
                {
                    var t = ch.ToString();
                    switch (font)
                    {
                        case Alpha.Cal: return new Sym(Cal.TryGetValue(ch, out var c) ? c : t, Face.Upright, Kind.Ord);
                        case Alpha.BB: return new Sym(BB.TryGetValue(ch, out var b) ? b : t, Face.Bold, Kind.Ord);
                        case Alpha.Rm: case Alpha.Tt: return new Sym(t, Face.Upright, Kind.Ord);
                        case Alpha.Sf: return new Sym(t, Face.Sans, Kind.Ord);
                        case Alpha.Bf: return new Sym(t, Face.Bold, Kind.Ord);
                        default: return new Sym(t, Face.Italic, Kind.Ord);
                    }
                }
                if (char.IsDigit(ch) || ch == '.')
                {
                    if (font == Alpha.BB && BB.TryGetValue(ch, out var bb)) return new Sym(bb, Face.Upright, Kind.Ord);
                    return new Sym(ch.ToString(), font == Alpha.Bf ? Face.Bold : Face.Upright, Kind.Ord);
                }
                switch (ch)
                {
                    case '+': return new Sym("+", Face.Upright, Kind.Bin);
                    case '-': return new Sym("−", Face.Upright, Kind.Bin);
                    case '*': return new Sym("∗", Face.Upright, Kind.Bin);
                    case '=': case '<': case '>': return new Sym(ch.ToString(), Face.Upright, Kind.Rel);
                    case ':': return new Sym(":", Face.Upright, Kind.Rel);
                    case ',': case ';': return new Sym(ch.ToString(), Face.Upright, Kind.Punct);
                    case '(': case '[': return new Sym(ch.ToString(), Face.Upright, Kind.Open);
                    case ')': case ']': return new Sym(ch.ToString(), Face.Upright, Kind.Close);
                    case '!': case '?': return new Sym(ch.ToString(), Face.Upright, Kind.Close);
                    case '~': return new Kern(0.33f);
                    case '|': return new Sym("|", Face.Upright, Kind.Ord);
                    case '/': return new Sym("/", Face.Upright, Kind.Ord);
                    case '{': { var l = ParseList(font); if (!End && s[i] == '}') i++; return l; }
                    default: return new Sym(ch.ToString(), Face.Upright, Kind.Ord);
                }
            }

            /// <summary>Text mode (\text{…}): words in the given face, spaces kept.</summary>
            Node TextNode(string raw, Face face)
            {
                var l = new Seq();
                var sb = new StringBuilder();
                void Flush() { if (sb.Length > 0) { l.Items.Add(new Sym(sb.ToString(), face, Kind.Ord)); sb.Clear(); } }
                for (int k = 0; k < raw.Length; k++)
                {
                    char ch = raw[k];
                    if (ch == ' ' || ch == '~') { Flush(); l.Items.Add(new Kern(0.32f)); continue; }
                    if (ch == '$')
                    {
                        int e = raw.IndexOf('$', k + 1);
                        if (e > k)
                        {
                            Flush();
                            var inner = new Parser(raw.Substring(k + 1, e - k - 1)).ParseList(Alpha.Math);
                            l.Items.Add(inner);
                            k = e;
                            continue;
                        }
                    }
                    if (ch == '\\' && k + 1 < raw.Length)
                    {
                        int j = k + 1;
                        if (char.IsLetter(raw[j])) { while (j < raw.Length && char.IsLetter(raw[j])) j++; }
                        else j++;
                        var cmd = raw.Substring(k + 1, j - k - 1);
                        if (Symbols.TryGetValue(cmd, out var sym)) sb.Append(sym.Item1);
                        else if (cmd == "," || cmd == ";" || cmd == " " || cmd == "quad") { Flush(); l.Items.Add(new Kern(cmd == "quad" ? 1 : 0.25f)); }
                        else if (cmd.Length == 1) sb.Append(cmd);
                        k = j - 1;
                        if (k + 1 < raw.Length && raw[k + 1] == ' ' && char.IsLetter(cmd.FirstOrDefault())) k++;
                        continue;
                    }
                    if (ch == '{' || ch == '}') continue;
                    sb.Append(ch);
                }
                Flush();
                return l;
            }

            Node Atom(Alpha font, Seq context)
            {
                char ch = s[i];
                if (ch != '\\') { i++; return CharAtom(ch, font); }
                var cmd = ReadCmd();
                if (cmd.Length == 0) return null;
                // spacing
                switch (cmd)
                {
                    case ",": return new Kern(3 / 18f);
                    case ":": case ">": return new Kern(4 / 18f);
                    case ";": return new Kern(5 / 18f);
                    case "!": return new Kern(-3 / 18f);
                    case " ": return new Kern(0.33f);
                    case "quad": return new Kern(1);
                    case "qquad": return new Kern(2);
                    case "enspace": return new Kern(0.5f);
                    case "thinspace": return new Kern(3 / 18f);
                    case "hspace": { var a = RawArg(); return new Kern(Length(a)); }
                }
                if (Symbols.TryGetValue(cmd, out var sym))
                {
                    var (txt, kind) = sym;
                    bool italic = kind == Kind.Ord && txt.Length == 1 && txt[0] >= 'α' && txt[0] <= 'ω' || txt == "ϵ" || txt == "ϑ" || txt == "ϕ" || txt == "ϱ" || txt == "ϖ";
                    return new Sym(txt, font == Alpha.Bf ? Face.Bold : italic ? Face.Italic : Face.Upright, kind);
                }
                if (BigOps.TryGetValue(cmd, out var op))
                    return Limited(new BigOp { Sym = op, Integral = op == "∫" || op == "∬" || op == "∭" || op == "∮" });
                if (LimitNames.Contains(cmd)) return Limited(new OpName { Name = cmd == "argmax" ? "arg max" : cmd == "argmin" ? "arg min" : cmd, CanLimit = true });
                if (FnNames.Contains(cmd)) return new OpName { Name = cmd };
                switch (cmd)
                {
                    case "frac": case "dfrac": case "tfrac": case "cfrac":
                    {
                        var n = Arg(font); var d = Arg(font);
                        return new Frac { Num = n, Den = d, Force = cmd == "tfrac" ? Style.Text : cmd == "dfrac" || cmd == "cfrac" ? Style.Display : (Style?)null };
                    }
                    case "binom": case "tbinom": case "dbinom":
                    {
                        var n = Arg(font); var d = Arg(font);
                        var lr = new LeftRight { L = "(", R = ")", Body = new Seq() };
                        lr.Body.Items.Add(new Frac { Num = n, Den = d, Bar = false, Force = cmd == "tbinom" ? Style.Text : (Style?)null });
                        return lr;
                    }
                    case "sqrt":
                    {
                        var idx = Optional();
                        return new Sqrt { Body = Arg(font), Index = idx != null ? new Parser(idx).ParseList(font) : null };
                    }
                    case "text": case "textrm": case "mbox": case "hbox": case "textnormal": case "textup":
                        return TextNode(RawArg(), Face.Upright);
                    case "textit": case "emph": return TextNode(RawArg(), Face.Italic);
                    case "textbf": return TextNode(RawArg(), Face.Bold);
                    case "texttt": case "textsf": return TextNode(RawArg(), Face.Sans);
                    case "operatorname": case "operatorname*":
                    {
                        var raw = RawArg();
                        bool star = i < s.Length && s[i] == '*';
                        return Limited(new OpName { Name = raw.Replace("\\", ""), CanLimit = star });
                    }
                    case "mathrm": case "rm": case "mathup": return FontArg(cmd, Alpha.Rm, font);
                    case "mathbf": case "bf": case "boldsymbol": case "bm": return FontArg(cmd, Alpha.Bf, font);
                    case "mathit": case "it": return FontArg(cmd, Alpha.It, font);
                    case "mathcal": case "mathscr": return FontArg(cmd, Alpha.Cal, font);
                    case "mathbb": return FontArg(cmd, Alpha.BB, font);
                    case "mathtt": return FontArg(cmd, Alpha.Tt, font);
                    case "mathsf": return FontArg(cmd, Alpha.Sf, font);
                    case "mathrel": { var a = Arg(font); a.Kind = Kind.Rel; return a; }
                    case "mathbin": { var a = Arg(font); a.Kind = Kind.Bin; return a; }
                    case "mathop": { var a = Arg(font); a.Kind = Kind.Op; return a; }
                    case "mathord": { var a = Arg(font); a.Kind = Kind.Ord; return a; }
                    case "hat": case "widehat": return new Accent { Body = Arg(font), What = "hat", Wide = cmd == "widehat" };
                    case "check": return new Accent { Body = Arg(font), What = "check" };
                    case "bar": return new Accent { Body = Arg(font), What = "bar" };
                    case "overline": return new Accent { Body = Arg(font), What = "overline", Wide = true };
                    case "underline": return new Accent { Body = Arg(font), What = "underline", Wide = true };
                    case "vec": return new Accent { Body = Arg(font), What = "vec" };
                    case "overrightarrow": return new Accent { Body = Arg(font), What = "vec", Wide = true };
                    case "tilde": case "widetilde": return new Accent { Body = Arg(font), What = "tilde", Wide = cmd == "widetilde" };
                    case "dot": return new Accent { Body = Arg(font), What = "dot" };
                    case "ddot": return new Accent { Body = Arg(font), What = "ddot" };
                    case "underbrace": return new HBrace { Body = Arg(font), Under = true };
                    case "overbrace": return new HBrace { Body = Arg(font), Under = false };
                    case "xrightarrow": case "xleftarrow":
                    {
                        var below = Optional();
                        return new Stack { Over = Arg(font), Under = below != null ? new Parser(below).ParseList(font) : null, Arrow = cmd == "xrightarrow" ? "→" : "←" };
                    }
                    case "overset": case "stackrel": { var o = Arg(font); var b = Arg(font); return new Stack { Over = o, Base = b, Kind = b.Kind == Kind.Ord ? Kind.Rel : b.Kind }; }
                    case "underset": { var u = Arg(font); var b = Arg(font); return new Stack { Under = u, Base = b, Kind = b.Kind == Kind.Ord ? Kind.Rel : b.Kind }; }
                    case "left":
                    {
                        var l = Delimiter();
                        var body = ParseList(font);
                        string r = ".";
                        if (PeekCmd() == "right") { ReadCmd(); r = Delimiter(); }
                        return new LeftRight { L = l, R = r, Body = body };
                    }
                    case "right": case "middle": Delimiter(); return null;
                    case "big": case "bigl": case "bigr": case "bigm": return BigDelim(cmd, 1.2f);
                    case "Big": case "Bigl": case "Bigr": case "Bigm": return BigDelim(cmd, 1.8f);
                    case "bigg": case "biggl": case "biggr": case "biggm": return BigDelim(cmd, 2.4f);
                    case "Bigg": case "Biggl": case "Biggr": case "Biggm": return BigDelim(cmd, 3.0f);
                    case "begin": return Env(RawArg(), font);
                    case "displaystyle": return StyleRest(Style.Display, font);
                    case "textstyle": return StyleRest(Style.Text, font);
                    case "scriptstyle": return StyleRest(Style.Script, font);
                    case "scriptscriptstyle": return StyleRest(Style.ScriptScript, font);
                    case "limits": case "nolimits":
                    {
                        var last = context.Items.Count > 0 ? context.Items[context.Items.Count - 1] : null;
                        if (last is BigOp b) b.ForceLimits = cmd == "limits";
                        if (last is OpName o) o.ForceLimits = cmd == "limits";
                        return null;
                    }
                    case "not":
                    {
                        SkipWs();
                        if (!End && s[i] == '=') { i++; return new Sym("≠", Face.Upright, Kind.Rel); }
                        var a = Arg(font);
                        if (a is Sym sy) return new Sym(sy.Text + "̸", sy.Face, sy.Kind);
                        return a;
                    }
                    case "pmod": { var a = Arg(font); var l = new Seq(); l.Items.Add(new Kern(1)); l.Items.Add(new Sym("(mod", Face.Upright, Kind.Ord)); l.Items.Add(new Kern(0.3f)); l.Items.Add(a); l.Items.Add(new Sym(")", Face.Upright, Kind.Close)); return l; }
                    case "bmod": return new Sym("mod", Face.Upright, Kind.Bin);
                    case "color": case "textcolor": { RawArg(); return cmd == "textcolor" ? Arg(font) : null; }
                    case "boxed": case "phantom": case "hphantom": case "vphantom": case "smash": case "mathstrut":
                        return cmd == "mathstrut" ? null : Arg(font);
                    case "label": case "tag": case "nonumber": case "notag": if (cmd == "label" || cmd == "tag") RawArg(); return null;
                    case "tiny": case "small": case "normalsize": case "large": case "Large": case "LARGE": case "huge": return null;
                }
                // Unknown: show the command so the problem is visible, not silently dropped.
                return new Sym("\\" + cmd, Face.Upright, Kind.Ord);
            }

            Node Limited(Node op)
            {
                SkipWs();
                var pc = PeekCmd();
                if (pc == "limits" || pc == "nolimits")
                {
                    ReadCmd();
                    if (op is BigOp b) b.ForceLimits = pc == "limits";
                    if (op is OpName o) o.ForceLimits = pc == "limits";
                }
                return op;
            }

            Node FontArg(string cmd, Alpha f, Alpha cur)
            {
                // \rm, \bf (old style) switch the rest of the group.
                if (cmd == "rm" || cmd == "bf" || cmd == "it") return ParseList(f);
                return Arg(f);
            }

            Node StyleRest(Style st, Alpha font) => new StyleSet { To = st, Body = ParseList(font) };

            Node BigDelim(string cmd, float size)
            {
                var d = Delimiter();
                char last = cmd[cmd.Length - 1];
                var k = last == 'l' ? Kind.Open : last == 'r' ? Kind.Close : last == 'm' ? Kind.Rel : Kind.Ord;
                if (k == Kind.Ord && (d == "(" || d == "[" || d == "{" || d == "⟨" || d == "⌊" || d == "⌈")) k = Kind.Open;
                if (k == Kind.Ord && (d == ")" || d == "]" || d == "}" || d == "⟩" || d == "⌋" || d == "⌉")) k = Kind.Close;
                return new Big { D = d, Size = size, Kind = k };
            }

            string Delimiter()
            {
                SkipWs();
                if (End) return ".";
                if (s[i] == '\\')
                {
                    var c = ReadCmd();
                    switch (c)
                    {
                        case "{": case "lbrace": return "{";
                        case "}": case "rbrace": return "}";
                        case "|": case "Vert": case "lVert": case "rVert": return "‖";
                        case "vert": case "lvert": case "rvert": case "mid": return "|";
                        case "langle": return "⟨";
                        case "rangle": return "⟩";
                        case "lfloor": return "⌊";
                        case "rfloor": return "⌋";
                        case "lceil": return "⌈";
                        case "rceil": return "⌉";
                        case "lbrack": return "[";
                        case "rbrack": return "]";
                        case "uparrow": return "↑";
                        case "downarrow": return "↓";
                        default: return Symbols.TryGetValue(c, out var sy) ? sy.Item1 : ".";
                    }
                }
                return s[i++].ToString();
            }

            Node Env(string name, Alpha font)
            {
                if (name == "array" || name == "alignat" || name == "alignat*") RawArg(); // column spec / count
                var m = new Matrix { Env = name.TrimEnd('*') == "align" ? "aligned" : name };
                var row = new List<Seq>();
                while (true)
                {
                    var cell = ParseList(font);
                    row.Add(cell);
                    SkipWs();
                    if (End) break;
                    if (s[i] == '&') { i++; continue; }
                    var pc = PeekCmd();
                    if (pc == "\\" || pc == "cr")
                    {
                        ReadCmd();
                        Optional(); // \\[2pt]
                        m.Rows.Add(row);
                        row = new List<Seq>();
                        continue;
                    }
                    if (pc == "end") { ReadCmd(); RawArg(); break; }
                    if (s[i] == '}') { i++; continue; } // stray brace: skip it
                    i++;
                }
                if (row.Count > 1 || row.Count == 1 && row[0].Items.Count > 0) m.Rows.Add(row);
                return m;
            }

            static float Length(string a)
            {
                a = a.Trim();
                float k = a.EndsWith("em") ? 1 : a.EndsWith("ex") ? 0.43f : a.EndsWith("pt") ? 0.1f : a.EndsWith("mu") ? 1 / 18f : 1;
                var num = new string(a.TakeWhile(c => char.IsDigit(c) || c == '.' || c == '-').ToArray());
                return float.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v * k : 0;
            }
        }

        // ── rendering ──

        public struct Result { public List<TextMeshPro> texts; public MeshRenderer rules; public float width, height; }

        /// <summary>Typeset LaTeX under `parent`; `em` is the font size in parent units.
        /// The box is centred vertically on the origin and aligned left/centre/right.</summary>
        public static Result Render(Transform parent, string src, float em, Color color, string align = "center")
        {
            var res = new Result { texts = new List<TextMeshPro>() };
            Box box;
            try
            {
                src = (src ?? "").Trim();
                if (src.StartsWith("$$") && src.EndsWith("$$") && src.Length >= 4) src = src.Substring(2, src.Length - 4);
                else if (src.StartsWith("$") && src.EndsWith("$") && src.Length >= 2) src = src.Substring(1, src.Length - 2);
                var tree = new Parser(src).ParseList(Alpha.Math);
                box = tree.Layout(Style.Display);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[vcl] tex: {e.Message}");
                box = Run(src, Face.Upright);
            }
            float x0 = align == "left" ? 0 : align == "right" ? -box.W : -box.W / 2;
            float y0 = -(box.H - box.D) / 2;
            res.width = box.W * em;
            res.height = (box.H + box.D) * em;

            // Glyph runs: one TextMeshPro each, baseline-placed.
            foreach (var g in box.Glyphs)
            {
                var go = Draw.Child(parent, "tex");
                var t = Setup(go.AddComponent<TextMeshPro>());
                t.font = FontOf(g.Face);
                t.fontSize = 10 * em * g.Scale;
                t.color = color;
                t.text = g.Text;
                t.renderer.sortingOrder = 3;
                t.renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var m = Measure(g.Text, g.Face);
                float k = em * g.Scale;
                go.transform.localPosition = new Vector3((x0 + g.X) * em, (y0 + g.Y) * em - m.Base * k * g.SY, -0.001f);
                go.transform.localScale = new Vector3(g.SX, g.SY, 1);
                res.texts.Add(t);
            }

            // Rules, radicals, braces, accents: one mesh.
            if (box.Strokes.Count > 0 || box.Fills.Count > 0)
            {
                var verts = new List<Vector3>();
                var tris = new List<int>();
                Vector3 P(Vector2 p) => new Vector3((x0 + p.x) * em, (y0 + p.y) * em, -0.001f);
                for (int si = 0; si < box.Strokes.Count; si++)
                {
                    var pts = box.Strokes[si];
                    float hw = box.Widths[si] / 2;
                    for (int j = 0; j + 1 < pts.Count; j++)
                    {
                        var a = pts[j]; var b = pts[j + 1];
                        var d = b - a;
                        if (d.sqrMagnitude < 1e-10f) continue;
                        d.Normalize();
                        var n = new Vector2(-d.y, d.x) * hw;
                        // Extend a little past joints so segments overlap without gaps.
                        var ext = d * hw * (j + 2 < pts.Count ? 0.9f : 0f);
                        var ext0 = d * hw * (j > 0 ? 0.9f : 0f);
                        int v0 = verts.Count;
                        verts.Add(P(a - ext0 + n)); verts.Add(P(a - ext0 - n)); verts.Add(P(b + ext - n)); verts.Add(P(b + ext + n));
                        tris.AddRange(new[] { v0, v0 + 1, v0 + 2, v0, v0 + 2, v0 + 3 });
                    }
                }
                foreach (var f in box.Fills)
                {
                    if (f.Count < 3) continue;
                    var mesh = Draw.Fill(f.Select(p => P(p)).ToList());
                    int v0 = verts.Count;
                    verts.AddRange(mesh.vertices);
                    foreach (var ti in mesh.triangles) tris.Add(v0 + ti);
                    Draw.Kill(mesh);
                }
                var all = new Mesh { name = "tex-rules" };
                if (verts.Count > 65000) all.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                all.SetVertices(verts);
                all.SetTriangles(tris, 0);
                all.RecalculateBounds();
                res.rules = Draw.MeshObject(parent, "tex-rules", all, Draw.Unlit(color, 3002), 3);
            }
            return res;
        }
    }
}
