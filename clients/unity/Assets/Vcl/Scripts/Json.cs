// Minimal JSON reader for VCL IR: objects become Dictionary<string, object>,
// arrays List<object>, numbers double, plus string, bool and null.
// (JsonUtility can't read IR, whose fields depend on the op.)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Vcl
{
    public static class Json
    {
        /// <summary>The reverse: write what Parse makes (and ints, floats, any IList/IDictionary).</summary>
        public static string Write(object v)
        {
            var sb = new StringBuilder();
            WriteTo(sb, v);
            return sb.ToString();
        }

        static void WriteTo(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string s:
                    sb.Append('"');
                    foreach (var c in s)
                    {
                        if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                        else if (c == '\n') sb.Append("\\n");
                        else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                    }
                    sb.Append('"');
                    break;
                case double d: sb.Append(double.IsFinite(d) ? d.ToString("R", CultureInfo.InvariantCulture) : "null"); break;
                case float f: WriteTo(sb, (double)f); break;
                case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case System.Collections.IDictionary dict:
                    sb.Append('{');
                    bool first = true;
                    foreach (System.Collections.DictionaryEntry kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteTo(sb, kv.Key.ToString());
                        sb.Append(':');
                        WriteTo(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
                case System.Collections.IEnumerable list:
                    sb.Append('[');
                    bool firstItem = true;
                    foreach (var x in list) { if (!firstItem) sb.Append(','); firstItem = false; WriteTo(sb, x); }
                    sb.Append(']');
                    break;
                default: WriteTo(sb, v.ToString()); break;
            }
        }

        public static object Parse(string s)
        {
            int i = 0;
            var v = Value(s, ref i);
            Ws(s, ref i);
            if (i != s.Length) throw new FormatException($"JSON: trailing text at {i}");
            return v;
        }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON: unexpected end");
            char c = s[i];
            switch (c)
            {
                case '{':
                {
                    i++;
                    var o = new Dictionary<string, object>();
                    Ws(s, ref i);
                    if (s[i] == '}') { i++; return o; }
                    while (true)
                    {
                        Ws(s, ref i);
                        var k = Str(s, ref i);
                        Ws(s, ref i);
                        if (s[i++] != ':') throw new FormatException("JSON: expected ':'");
                        o[k] = Value(s, ref i);
                        Ws(s, ref i);
                        if (s[i] == ',') { i++; continue; }
                        if (s[i] == '}') { i++; return o; }
                        throw new FormatException($"JSON: expected ',' or '}}' at {i}");
                    }
                }
                case '[':
                {
                    i++;
                    var a = new List<object>();
                    Ws(s, ref i);
                    if (s[i] == ']') { i++; return a; }
                    while (true)
                    {
                        a.Add(Value(s, ref i));
                        Ws(s, ref i);
                        if (s[i] == ',') { i++; continue; }
                        if (s[i] == ']') { i++; return a; }
                        throw new FormatException($"JSON: expected ',' or ']' at {i}");
                    }
                }
                case '"': return Str(s, ref i);
                case 't': i += 4; return true;
                case 'f': i += 5; return false;
                case 'n': i += 4; return null;
                default:
                {
                    int st = i;
                    while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                    return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
                }
            }
        }

        static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException($"JSON: expected string at {i}");
            i++;
            var sb = new StringBuilder();
            while (s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            i++;
            return sb.ToString();
        }
    }

    /// <summary>One IR command (docs/IR.md) with typed accessors.</summary>
    public class Cmd
    {
        public readonly Dictionary<string, object> F;
        public Cmd(Dictionary<string, object> f) { F = f; }

        /// <summary>A say's synthesized speech, kept so replaying it (going back) needn't redo it.</summary>
        public (UnityEngine.AudioClip clip, double dur)? Spoken;
        public float SpokenRate; // the speed it was synthesized at

        public string Op => Str("op");
        public string Id => Str("id");
        public int Line => (int)Num("line", 0);
        public bool Par => Bool("par");
        public double Dur => Num("dur", 0);
        public string Ease => Str("ease");

        public bool Has(string k) => F.TryGetValue(k, out var v) && v != null;
        public object this[string k] => F.TryGetValue(k, out var v) ? v : null;
        public string Str(string k, string d = null) => F.TryGetValue(k, out var v) && v is string s ? s : d;
        public double Num(string k, double d) => F.TryGetValue(k, out var v) && v is double x ? x : d;
        public float Flt(string k, float d) => (float)Num(k, d);
        public bool Bool(string k, bool d = false) => F.TryGetValue(k, out var v) && v is bool b ? b : d;
        public List<object> List(string k) => F.TryGetValue(k, out var v) ? v as List<object> : null;

        /// <summary>[lo, hi] range, or null.</summary>
        public double[] Range(string k)
        {
            var l = List(k);
            return l == null ? null : new[] { (double)l[0], (double)l[1] };
        }

        public Cmd With(string k, object v)
        {
            var d = new Dictionary<string, object>(F) { [k] = v };
            return new Cmd(d);
        }
    }
}
