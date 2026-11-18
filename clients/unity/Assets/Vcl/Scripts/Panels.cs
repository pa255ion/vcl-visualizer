// Pure text helpers shared by the code, table and wave builders: a port of
// the tokenizer and parsers in viewer/js/panels.js (keep them in step).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vcl
{
    public struct Tok
    {
        public string Text, Type; // plain kw type str num com fn
    }

    public class WaveStep
    {
        public char V;        // 0 1 z x p n =
        public string Label;  // for '=' runs
        public bool Start;    // a new '=' value begins here
    }

    public class WaveRow
    {
        public string Name;
        public List<WaveStep> Steps = new List<WaveStep>();
    }

    public static class Panels
    {
        static readonly Dictionary<string, string> KW = new Dictionary<string, string>
        {
            ["c"] = "auto break case char const continue default do double else enum extern float for goto if inline int long register restrict return short signed sizeof static struct switch typedef union unsigned void volatile while bool true false NULL uint8_t uint16_t uint32_t uint64_t int8_t int16_t int32_t int64_t size_t",
            ["cpp"] = "class public private protected template typename namespace using new delete virtual override final auto nullptr constexpr noexcept operator this throw try catch const_cast static_cast dynamic_cast reinterpret_cast std",
            ["zig"] = "const var fn pub return if else while for switch break continue struct enum union error try catch defer errdefer comptime inline export extern packed and or orelse unreachable null undefined true false test anytype noreturn asm volatile threadlocal",
            ["rust"] = "fn let mut pub struct enum impl trait for while loop if else match return use mod crate self Self super where as in ref move unsafe async await dyn const static type true false break continue",
            ["python"] = "def class return if elif else for while in not and or is None True False import from as with try except finally raise lambda yield pass break continue global nonlocal async await assert del",
            ["js"] = "function const let var return if else for while do switch case break continue class new this extends import export from default async await yield try catch finally throw typeof instanceof in of null undefined true false static get set",
            ["ts"] = "interface type enum implements public private protected readonly namespace declare abstract as keyof",
            ["java"] = "class interface extends implements public private protected static final void int long short byte char boolean float double new return if else for while do switch case break continue try catch finally throw throws this super null true false package import",
            ["go"] = "func package import var const type struct interface map chan go defer return if else for range switch case default break continue select fallthrough nil true false",
            ["verilog"] = "module endmodule input output inout wire reg logic always always_ff always_comb always_latch assign begin end if else case casez endcase posedge negedge parameter localparam integer initial for generate endgenerate genvar function endfunction task endtask default typedef enum struct packed",
            ["vhdl"] = "entity architecture is begin end port in out inout signal process if then else elsif case when others library use all component generic map variable constant type of to downto rising_edge falling_edge and or not xor",
            ["asm"] = "",
            ["haskell"] = "module where import qualified as hiding data type newtype class instance deriving via stock anyclass let in case of if then else do mdo rec forall family default infix infixl infixr foreign",
            ["sh"] = "if then else elif fi for do done while until case esac function return export local in echo exit",
        };
        static readonly Dictionary<string, string> TYPES = new Dictionary<string, string>
        {
            ["zig"] = "u8 u16 u32 u64 u128 i8 i16 i32 i64 i128 usize isize f16 f32 f64 bool void anyerror type comptime_int",
            ["rust"] = "u8 u16 u32 u64 usize i8 i16 i32 i64 isize f32 f64 bool char str String Vec Option Result Box",
            ["vhdl"] = "std_logic std_logic_vector unsigned signed integer natural boolean",
            ["go"] = "int int8 int16 int32 int64 uint uint8 uint16 uint32 uint64 float32 float64 string bool byte rune error",
        };
        static readonly Dictionary<string, string> ALIASES = new Dictionary<string, string>
        {
            ["c++"] = "cpp", ["h"] = "c", ["hpp"] = "cpp", ["py"] = "python", ["javascript"] = "js", ["typescript"] = "ts",
            ["rs"] = "rust", ["sv"] = "verilog", ["systemverilog"] = "verilog", ["v"] = "verilog", ["vhd"] = "vhdl",
            ["s"] = "asm", ["assembly"] = "asm", ["bash"] = "sh", ["shell"] = "sh", ["hs"] = "haskell", ["clash"] = "haskell", ["lhs"] = "haskell",
        };

        static readonly Regex NumRe = new Regex(@"^(0[xX][0-9a-fA-F_]+|0[bB][01_]+|\d[\d_]*(\.\d+)?([eE][+-]?\d+)?[uUlLfF]*)");
        static readonly Regex IdRe = new Regex(@"^[A-Za-z_$@][\w$]*");
        static readonly Regex HsIdRe = new Regex(@"^[A-Za-z_][\w']*");
        static readonly Regex HsComment = new Regex(@"^--+(?![!#$%&*+./<=>?@\\^|~:])");
        static readonly Regex CharLit = new Regex(@"^'(\\.|[^'\\])'");
        static readonly Regex VerilogSized = new Regex(@"^'[sS]?[bodhBODH][0-9a-fA-FxXzZ_?]+");
        static readonly Regex AsmReg = new Regex(@"^(%?[re]?[a-d]x|%?r\d+[dwb]?|x\d+|w\d+|[re]?[sd]i|[re]?[sb]p|sp|lr|pc|zero|ra|[atsv]\d+)$", RegexOptions.IgnoreCase);

        static HashSet<string> Words(string l) => new HashSet<string>(((KW.TryGetValue(l, out var k) ? k : "")
            + (l == "cpp" ? " " + KW["c"] : "") + (l == "ts" ? " " + KW["js"] : "")).Split(' ').Where(w => w.Length > 0));

        /// <summary>Tokenize code into lines of tokens; block comments carry across lines.</summary>
        public static List<List<Tok>> Highlight(string src, string lang)
        {
            var l = (lang ?? "").ToLowerInvariant();
            if (ALIASES.TryGetValue(l, out var al)) l = al;
            var words = Words(l);
            var types = new HashSet<string>((TYPES.TryGetValue(l, out var ty) ? ty : "").Split(' ').Where(w => w.Length > 0));
            bool hs = l == "haskell", ci = l == "vhdl";
            string[] line = l == "python" || l == "sh" ? new[] { "#" } : l == "vhdl" || hs ? new[] { "--" } : l == "asm" ? new[] { ";", "#", "//" } : new[] { "//" };
            string[] block = new[] { "python", "sh", "vhdl", "asm" }.Contains(l) ? null : hs ? new[] { "{-", "-}" } : new[] { "/*", "*/" };
            var outp = new List<List<Tok>>();
            bool inBlock = false;
            foreach (var raw in (src ?? "").Replace("\t", "    ").Split('\n'))
            {
                var toks = new List<Tok>();
                void Push(string text, string type)
                {
                    if (string.IsNullOrEmpty(text)) return;
                    if (toks.Count > 0 && toks[toks.Count - 1].Type == type) { var t = toks[toks.Count - 1]; t.Text += text; toks[toks.Count - 1] = t; }
                    else toks.Add(new Tok { Text = text, Type = type });
                }
                int i = 0;
                bool firstWord = true;
                while (i < raw.Length)
                {
                    var rest = raw.Substring(i);
                    if (inBlock)
                    {
                        int end = rest.IndexOf(block[1], StringComparison.Ordinal);
                        if (end < 0) { Push(rest, "com"); i = raw.Length; break; }
                        Push(rest.Substring(0, end + 2), "com"); i += end + 2; inBlock = false; continue;
                    }
                    if (block != null && rest.StartsWith(block[0], StringComparison.Ordinal)) { inBlock = true; continue; }
                    if (hs ? HsComment.IsMatch(rest) : line.Any(c => rest.StartsWith(c, StringComparison.Ordinal))) { Push(rest, "com"); break; }
                    char q = rest[0];
                    if (q == '"' || q == '\'' || (q == '`' && (l == "js" || l == "ts" || l == "go")))
                    {
                        if (q == '\'' && l == "verilog" && Regex.IsMatch(rest, @"^'[sS]?[bodhBODH]"))
                        {
                            var vm = VerilogSized.Match(rest);
                            if (vm.Success) { Push(vm.Value, "num"); i += vm.Length; continue; }
                        }
                        if (q == '\'' && (l == "rust" || l == "vhdl" || hs) && !CharLit.IsMatch(rest)) { Push("'", "plain"); i++; continue; }
                        int j = 1;
                        while (j < rest.Length && rest[j] != q) j += rest[j] == '\\' ? 2 : 1;
                        int len = Math.Min(rest.Length, j + 1);
                        Push(rest.Substring(0, len), "str"); i += len; continue;
                    }
                    Match m;
                    if ((m = NumRe.Match(rest)).Success && !(i > 0 && (char.IsLetterOrDigit(raw[i - 1]) || raw[i - 1] == '_')))
                    {
                        Push(m.Value, "num"); i += m.Length; firstWord = false; continue;
                    }
                    if ((m = (hs ? HsIdRe : IdRe).Match(rest)).Success)
                    {
                        var w = m.Value;
                        var key = ci ? w.ToLowerInvariant() : w;
                        string type = "plain";
                        if (words.Contains(key)) type = "kw";
                        else if (types.Contains(key)) type = "type";
                        else if (hs && char.IsUpper(w[0])) type = "type";
                        else if (hs && Regex.IsMatch(raw.Substring(i + w.Length), @"^\s*::") && raw.Substring(0, i).Trim().Length == 0) type = "fn";
                        else if (l == "asm" && firstWord) type = "kw";
                        else if (l == "asm" && AsmReg.IsMatch(w)) type = "type";
                        else if (i + w.Length < raw.Length && raw[i + w.Length] == '(') type = "fn";
                        Push(w, type); i += w.Length; firstWord = false; continue;
                    }
                    if (l == "asm" && rest[0] == '%' && (m = Regex.Match(rest, @"^%\w+")).Success) { Push(m.Value, "type"); i += m.Length; continue; }
                    Push(rest[0].ToString(), "plain"); i++;
                    if (!char.IsWhiteSpace(rest[0]) && rest[0] != ':') firstWord = false;
                }
                outp.Add(toks);
            }
            return outp;
        }

        public static readonly Dictionary<string, string> Theme = new Dictionary<string, string>
        {
            ["bg"] = "#0d1117", ["border"] = "#30363d", ["gutter"] = "#6e7681", ["focus"] = "#1f3a5f", ["title"] = "#161b22",
            ["plain"] = "#e6edf3", ["kw"] = "#ff7b72", ["type"] = "#79c0ff", ["str"] = "#a5d6ff", ["num"] = "#79c0ff", ["com"] = "#8b949e", ["fn"] = "#d2a8ff",
        };

        /// <summary>Split table rows ("a|b|c") into trimmed cells.</summary>
        public static List<string[]> Cells(IEnumerable<string> rows) => rows.Select(r => r.Split('|').Select(c => c.Trim()).ToArray()).ToList();

        /// <summary>A wave signal's pin name (mirrors wavePin in core/src/vcl.zig).</summary>
        public static string WavePin(string name)
        {
            var n = (name ?? "").Trim();
            if (n.EndsWith("]") && n.LastIndexOf('[') >= 0) n = n.Substring(0, n.LastIndexOf('[')).Trim();
            var r = Regex.Replace(n, "[^A-Za-z0-9_]+", "_").Trim('_');
            if (r.Length > 0 && char.IsDigit(r[0])) r = "s" + r;
            return r.Length > 0 ? r : null;
        }

        /// <summary>Timing-diagram rows: "name: pattern [| label label …]" (see panels.js parseWave).</summary>
        public static List<WaveRow> ParseWave(IEnumerable<string> rows)
        {
            var outp = new List<WaveRow>();
            foreach (var row in rows)
            {
                int bar = row.IndexOf('|');
                string head = bar >= 0 ? row.Substring(0, bar) : row, labelPart = bar >= 0 ? row.Substring(bar + 1) : "";
                int colon = head.LastIndexOf(':');
                var wr = new WaveRow { Name = colon >= 0 ? head.Substring(0, colon).Trim() : "" };
                var pat = Regex.Replace(colon >= 0 ? head.Substring(colon + 1) : head, @"\s+", "");
                var labels = labelPart.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                char prev = 'x';
                int li = 0;
                foreach (char ch in pat)
                {
                    char v = ch == '.' ? prev : ch;
                    if ((v >= '2' && v <= '9') || v == '=') v = '=';
                    if ("01zxpn=".IndexOf(v) < 0) throw new Exception($"wave: unknown symbol '{ch}' in \"{row}\" (use 0 1 z x . = p n)");
                    var st = new WaveStep { V = v };
                    if (ch != '.' && v == '=') { st.Label = li < labels.Length ? labels[li] : ""; li++; st.Start = true; }
                    wr.Steps.Add(st);
                    prev = v;
                }
                outp.Add(wr);
            }
            return outp;
        }
    }
}
