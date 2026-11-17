// Canvas-drawn panels: source code (syntax highlighted) and tables.
// The tokenizer is pure (no DOM) so it can be tested in Node.

const KW = {
  c: 'auto break case char const continue default do double else enum extern float for goto if inline int long register restrict return short signed sizeof static struct switch typedef union unsigned void volatile while bool true false NULL uint8_t uint16_t uint32_t uint64_t int8_t int16_t int32_t int64_t size_t',
  cpp: 'class public private protected template typename namespace using new delete virtual override final auto nullptr constexpr noexcept operator this throw try catch const_cast static_cast dynamic_cast reinterpret_cast std',
  zig: 'const var fn pub return if else while for switch break continue struct enum union error try catch defer errdefer comptime inline export extern packed and or orelse unreachable null undefined true false test anytype noreturn asm volatile threadlocal',
  rust: 'fn let mut pub struct enum impl trait for while loop if else match return use mod crate self Self super where as in ref move unsafe async await dyn const static type true false break continue',
  python: 'def class return if elif else for while in not and or is None True False import from as with try except finally raise lambda yield pass break continue global nonlocal async await assert del',
  js: 'function const let var return if else for while do switch case break continue class new this extends import export from default async await yield try catch finally throw typeof instanceof in of null undefined true false static get set',
  ts: 'interface type enum implements public private protected readonly namespace declare abstract as keyof',
  java: 'class interface extends implements public private protected static final void int long short byte char boolean float double new return if else for while do switch case break continue try catch finally throw throws this super null true false package import',
  go: 'func package import var const type struct interface map chan go defer return if else for range switch case default break continue select fallthrough nil true false',
  verilog: 'module endmodule input output inout wire reg logic always always_ff always_comb always_latch assign begin end if else case casez endcase posedge negedge parameter localparam integer initial for generate endgenerate genvar function endfunction task endtask default typedef enum struct packed',
  vhdl: 'entity architecture is begin end port in out inout signal process if then else elsif case when others library use all component generic map variable constant type of to downto rising_edge falling_edge and or not xor',
  asm: '',
  haskell: 'module where import qualified as hiding data type newtype class instance deriving via stock anyclass let in case of if then else do mdo rec forall family default infix infixl infixr foreign',
  sh: 'if then else elif fi for do done while until case esac function return export local in echo exit',
};
const TYPES = {
  zig: 'u8 u16 u32 u64 u128 i8 i16 i32 i64 i128 usize isize f16 f32 f64 bool void anyerror type comptime_int',
  rust: 'u8 u16 u32 u64 usize i8 i16 i32 i64 isize f32 f64 bool char str String Vec Option Result Box',
  vhdl: 'std_logic std_logic_vector unsigned signed integer natural boolean',
  go: 'int int8 int16 int32 int64 uint uint8 uint16 uint32 uint64 float32 float64 string bool byte rune error',
};
const ALIASES = { 'c++': 'cpp', h: 'c', hpp: 'cpp', py: 'python', javascript: 'js', typescript: 'ts', rs: 'rust', sv: 'verilog', systemverilog: 'verilog', v: 'verilog', vhd: 'vhdl', s: 'asm', assembly: 'asm', bash: 'sh', shell: 'sh', hs: 'haskell', clash: 'haskell', lhs: 'haskell' };

function langOf(name) {
  const l = String(name || '').toLowerCase();
  return ALIASES[l] ?? l;
}

function spec(lang) {
  const l = langOf(lang);
  const words = new Set([...(KW[l] ?? '').split(' '), ...(l === 'cpp' ? KW.c.split(' ') : []), ...(l === 'ts' ? KW.js.split(' ') : [])].filter(Boolean));
  const types = new Set((TYPES[l] ?? '').split(' ').filter(Boolean));
  const line = l === 'python' || l === 'sh' ? ['#'] : l === 'vhdl' || l === 'haskell' ? ['--'] : l === 'asm' ? [';', '#', '//'] : ['//'];
  const block = ['python', 'sh', 'vhdl', 'asm'].includes(l) ? null : l === 'haskell' ? ['{-', '-}'] : ['/*', '*/'];
  return { l, words, types, line, block, ci: l === 'vhdl' };
}

/**
 * Tokenize code into lines of {text, type}; type ∈ plain kw type str num com fn.
 * Block comments carry across lines.
 */
export function highlight(src, lang) {
  const s = spec(lang);
  const out = [];
  let inBlock = false;
  for (const raw of String(src).replace(/\t/g, '    ').split('\n')) {
    const toks = [];
    const push = (text, type) => {
      if (!text) return;
      const last = toks[toks.length - 1];
      if (last && last.type === type) last.text += text; else toks.push({ text, type });
    };
    let i = 0;
    let firstWord = true;
    while (i < raw.length) {
      const rest = raw.slice(i);
      if (inBlock) {
        const end = rest.indexOf(s.block[1]);
        if (end < 0) { push(rest, 'com'); i = raw.length; break; }
        push(rest.slice(0, end + 2), 'com'); i += end + 2; inBlock = false; continue;
      }
      if (s.block && rest.startsWith(s.block[0])) { inBlock = true; continue; }
      // Haskell: `--` starts a comment unless it is part of an operator (-->, --|).
      if (s.l === 'haskell' ? /^--+(?![!#$%&*+./<=>?@\\^|~:])/.test(rest) : s.line.some((c) => rest.startsWith(c))) { push(rest, 'com'); break; }
      const q = rest[0];
      if (q === '"' || q === "'" || (q === '`' && (s.l === 'js' || s.l === 'ts' || s.l === 'go'))) {
        // Verilog sized literals (8'hFF) are numbers, not strings.
        if (q === "'" && s.l === 'verilog' && /^'[sS]?[bodhBODH]/.test(rest)) {
          const m = /^'[sS]?[bodhBODH][0-9a-fA-FxXzZ_?]+/.exec(rest);
          push(m[0], 'num'); i += m[0].length; continue;
        }
        if (q === "'" && (s.l === 'rust' || s.l === 'vhdl' || s.l === 'haskell') && !/^'(\\.|[^'\\])'/.test(rest)) { push(q, 'plain'); i++; continue; }
        let j = 1;
        while (j < rest.length && rest[j] !== q) j += rest[j] === '\\' ? 2 : 1;
        push(rest.slice(0, j + 1), 'str'); i += j + 1; continue;
      }
      let m;
      if ((m = /^(0[xX][0-9a-fA-F_]+|0[bB][01_]+|\d[\d_]*(\.\d+)?([eE][+-]?\d+)?[uUlLfF]*)/.exec(rest)) && !/\w/.test(raw[i - 1] ?? '')) {
        push(m[0], 'num'); i += m[0].length; firstWord = false; continue;
      }
      if ((m = (s.l === 'haskell' ? /^[A-Za-z_][\w']*/ : /^[A-Za-z_$@][\w$]*/).exec(rest))) {
        const w = m[0], key = s.ci ? w.toLowerCase() : w;
        let type = 'plain';
        if (s.words.has(key)) type = 'kw';
        else if (s.types.has(key)) type = 'type';
        else if (s.l === 'haskell' && /^[A-Z]/.test(w)) type = 'type'; // types and constructors
        else if (s.l === 'haskell' && /^\s*::/.test(raw.slice(i + w.length)) && !raw.slice(0, i).trim()) type = 'fn'; // a signature
        else if (s.l === 'asm' && firstWord) type = 'kw';
        else if (s.l === 'asm' && /^(%?[re]?[a-d]x|%?r\d+[dwb]?|x\d+|w\d+|[re]?[sd]i|[re]?[sb]p|sp|lr|pc|zero|ra|[atsv]\d+)$/i.test(w)) type = 'type';
        else if (raw[i + w.length] === '(') type = 'fn';
        push(w, type); i += w.length; firstWord = false; continue;
      }
      if (s.l === 'asm' && rest[0] === '%' && (m = /^%\w+/.exec(rest))) { push(m[0], 'type'); i += m[0].length; continue; }
      push(rest[0], 'plain'); i++;
      if (!/\s/.test(rest[0]) && rest[0] !== ':') firstWord = false;
    }
    out.push(toks);
  }
  return out;
}

const THEME = { bg: '#0d1117', border: '#30363d', gutter: '#6e7681', focus: '#1f3a5f', title: '#161b22',
  plain: '#e6edf3', kw: '#ff7b72', type: '#79c0ff', str: '#a5d6ff', num: '#79c0ff', com: '#8b949e', fn: '#d2a8ff' };
const MONO = '"JetBrains Mono", "Fira Code", "DejaVu Sans Mono", ui-monospace, monospace';
const SANS = '"Inter", "Segoe UI", system-ui, sans-serif';

/** Draw code to a canvas. Returns {canvas, lineHeightPx}. */
export function drawCode(src, { lang, lines = true, focus = null, title = null } = {}) {
  const rows = highlight(src, lang);
  const canvas = document.createElement('canvas');
  const g = canvas.getContext('2d');
  let px = 40;
  g.font = `${px}px ${MONO}`;
  const cw = g.measureText('M').width;
  const maxChars = Math.max(1, ...rows.map((r) => r.reduce((n, t) => n + t.text.length, 0)));
  const gutterChars = lines ? String(rows.length).length + 2 : 1;
  let width = (maxChars + gutterChars + 2) * cw;
  if (width > 4000) { px = Math.floor((px * 4000) / width); width = 4000; }
  const lh = Math.round(px * 1.4), pad = px * 0.6, titleH = title ? Math.round(lh * 1.2) : 0;
  canvas.width = Math.ceil(width);
  canvas.height = Math.ceil(rows.length * lh + pad * 2 + titleH);
  g.font = `${px}px ${MONO}`;
  const cwx = g.measureText('M').width;
  // Panel.
  g.fillStyle = THEME.bg;
  roundRect(g, 1, 1, canvas.width - 2, canvas.height - 2, px * 0.4);
  g.fill();
  g.strokeStyle = THEME.border;
  g.lineWidth = 2;
  g.stroke();
  if (title) {
    g.fillStyle = THEME.title;
    g.fillRect(2, 2, canvas.width - 4, titleH - 2);
    g.fillStyle = THEME.gutter;
    g.font = `${Math.round(px * 0.8)}px ${SANS}`;
    g.textBaseline = 'middle';
    g.fillText(title, pad, titleH / 2);
    g.font = `${px}px ${MONO}`;
  }
  const inFocus = (n) => !focus || (n >= focus[0] && n <= focus[1]);
  g.textBaseline = 'middle';
  rows.forEach((toks, i) => {
    const n = i + 1, y = titleH + pad + i * lh + lh / 2;
    if (focus && inFocus(n)) {
      g.fillStyle = THEME.focus;
      g.fillRect(2, y - lh / 2, canvas.width - 4, lh);
    }
    g.globalAlpha = inFocus(n) ? 1 : 0.4;
    let x = pad * 0.5;
    if (lines) {
      g.fillStyle = THEME.gutter;
      const num = String(n).padStart(gutterChars - 2, ' ');
      g.fillText(num, x, y);
      x += gutterChars * cwx;
    } else x = pad;
    for (const t of toks) {
      g.fillStyle = THEME[t.type] ?? THEME.plain;
      g.fillText(t.text, x, y);
      x += t.text.length * cwx;
    }
    g.globalAlpha = 1;
  });
  return { canvas, lineHeightPx: lh, firstRowPx: titleH + pad };
}

/** Split table rows ("a|b|c") into trimmed cells. */
export function cells(rows) {
  return rows.map((r) => String(r).split('|').map((c) => c.trim()));
}

/** Draw a table to a canvas. Returns {canvas, lineHeightPx}. */
export function drawTable(rows, { header = true, focus = null } = {}) {
  const grid = cells(rows);
  const ncol = Math.max(...grid.map((r) => r.length));
  const canvas = document.createElement('canvas');
  const g = canvas.getContext('2d');
  const px = 40, lh = Math.round(px * 1.6), padX = px * 0.7;
  const font = (bold) => `${bold ? 700 : 500} ${px}px ${SANS}`;
  const widths = Array.from({ length: ncol }, (_, c) => Math.max(...grid.map((r, i) => {
    g.font = font(header && i === 0);
    return g.measureText(r[c] ?? '').width;
  })) + padX * 2);
  canvas.width = Math.ceil(widths.reduce((a, b) => a + b, 0) + 4);
  canvas.height = Math.ceil(grid.length * lh + 4);
  g.fillStyle = '#111823';
  roundRect(g, 1, 1, canvas.width - 2, canvas.height - 2, px * 0.3);
  g.fill();
  g.textBaseline = 'middle';
  g.textAlign = 'center';
  grid.forEach((r, i) => {
    const y = 2 + i * lh;
    const dataRow = header ? i : i + 1; // focus counts data rows from 1
    if (header && i === 0) { g.fillStyle = '#1c2a3d'; g.fillRect(2, y, canvas.width - 4, lh); }
    else if (focus && dataRow >= focus[0] && dataRow <= focus[1]) { g.fillStyle = '#1f3a5f'; g.fillRect(2, y, canvas.width - 4, lh); }
    else if (i % 2 === 0) { g.fillStyle = '#0f151e'; g.fillRect(2, y, canvas.width - 4, lh); }
    let x = 2;
    for (let c = 0; c < ncol; c++) {
      g.font = font(header && i === 0);
      g.globalAlpha = focus && !(header && i === 0) && !(dataRow >= focus[0] && dataRow <= focus[1]) ? 0.45 : 1;
      g.fillStyle = header && i === 0 ? '#ffffff' : '#e6edf3';
      g.fillText(r[c] ?? '', x + widths[c] / 2, y + lh / 2);
      g.globalAlpha = 1;
      x += widths[c];
    }
  });
  // Rules: under the header, and between columns.
  g.strokeStyle = '#3b4a5e';
  g.lineWidth = 2;
  if (header) { g.beginPath(); g.moveTo(2, 2 + lh); g.lineTo(canvas.width - 2, 2 + lh); g.stroke(); }
  let x = 2;
  for (let c = 0; c + 1 < ncol; c++) {
    x += widths[c];
    g.beginPath(); g.moveTo(x, 4); g.lineTo(x, canvas.height - 4); g.stroke();
  }
  g.strokeStyle = '#30363d';
  roundRect(g, 1, 1, canvas.width - 2, canvas.height - 2, px * 0.3);
  g.stroke();
  return { canvas, lineHeightPx: lh };
}

function roundRect(g, x, y, w, h, r) {
  g.beginPath();
  g.moveTo(x + r, y);
  g.arcTo(x + w, y, x + w, y + h, r);
  g.arcTo(x + w, y + h, x, y + h, r);
  g.arcTo(x, y + h, x, y, r);
  g.arcTo(x, y, x + w, y, r);
  g.closePath();
}

/**
 * Parse timing-diagram rows: "name: pattern [| label label …]".
 * Pattern chars: 0 1 (levels), z (high-Z), x (unknown), . (repeat previous),
 * = or 2–9 (a bus value, named by the next label), p/n (one clock period
 * starting rising/falling). Returns [{name, steps:[{v, label?}]}].
 */
/** A wave signal's pin name (mirrors wavePin in core/src/vcl.zig):
 *  "FIFO write" → FIFO_write, "d[7:0]" → d, "3v3" → s3v3, "" → null. */
export function wavePin(name) {
  let n = String(name).trim();
  if (n.endsWith(']') && n.lastIndexOf('[') >= 0) n = n.slice(0, n.lastIndexOf('[')).trim();
  let r = n.replace(/[^A-Za-z0-9_]+/g, '_').replace(/^_+|_+$/g, '');
  if (/^[0-9]/.test(r)) r = 's' + r;
  return r || null;
}

export function parseWave(rows) {
  return rows.map((row) => {
    const [head, labelPart] = String(row).split('|');
    const colon = head.lastIndexOf(':'); // names like d[7:0] contain colons; patterns never do
    const name = colon >= 0 ? head.slice(0, colon).trim() : '';
    const pat = (colon >= 0 ? head.slice(colon + 1) : head).replace(/\s+/g, '');
    const labels = (labelPart ?? '').trim().split(/\s+/).filter(Boolean);
    const steps = [];
    let prev = 'x', li = 0;
    for (const ch of pat) {
      let v = ch === '.' ? prev : ch;
      if (/[2-9=]/.test(v)) v = '=';
      if (!/[01zxpn=]/.test(v)) throw new Error(`wave: unknown symbol '${ch}' in "${row}" (use 0 1 z x . = p n)`);
      const step = { v };
      if (ch !== '.' && v === '=') step.label = labels[li++] ?? '';
      if (ch !== '.' && v === '=') step.start = true;
      steps.push(step);
      prev = v;
    }
    return { name, steps };
  });
}
