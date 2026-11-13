// Safe math-expression compiler for plot/surf. No eval: tokenize → parse →
// closure tree. Supports implicit multiplication ("2x", "3(x+1)", "xy").

const FUNCS = {
  sin: Math.sin, cos: Math.cos, tan: Math.tan, asin: Math.asin, acos: Math.acos,
  atan: Math.atan, atan2: Math.atan2, sinh: Math.sinh, cosh: Math.cosh, tanh: Math.tanh,
  sqrt: Math.sqrt, cbrt: Math.cbrt, abs: Math.abs, exp: Math.exp, ln: Math.log, log: Math.log,
  log10: Math.log10, log2: Math.log2, floor: Math.floor, ceil: Math.ceil, round: Math.round,
  min: Math.min, max: Math.max, pow: Math.pow, sign: Math.sign, mod: (a, b) => ((a % b) + b) % b,
  sec: (x) => 1 / Math.cos(x), csc: (x) => 1 / Math.sin(x), cot: (x) => 1 / Math.tan(x),
};
const CONSTS = { pi: Math.PI, e: Math.E, tau: 2 * Math.PI };

function tokenize(src) {
  const toks = [];
  const re = /\s*(?:(\d*\.\d+(?:[eE][+-]?\d+)?|\d+(?:[eE][+-]?\d+)?)|([A-Za-z_][A-Za-z_0-9]*)|(\*\*|[-+*/^(),])|(π)|(\S))/y;
  let m;
  re.lastIndex = 0;
  while (re.lastIndex < src.length && (m = re.exec(src))) {
    if (m[1] !== undefined) toks.push({ t: 'num', v: parseFloat(m[1]) });
    else if (m[2] !== undefined) toks.push({ t: 'id', v: m[2] });
    else if (m[3] !== undefined) toks.push({ t: 'op', v: m[3] === '**' ? '^' : m[3] });
    else if (m[4] !== undefined) toks.push({ t: 'id', v: 'pi' });
    else if (m[5] !== undefined) throw new Error(`unexpected '${m[5]}'`);
    if (/^\s*$/.test(src.slice(re.lastIndex))) break;
  }
  return toks;
}

export function compile(src, vars = ['x', 'y', 't']) {
  const toks = tokenize(String(src));
  let i = 0;
  const peek = () => toks[i];
  const isOp = (v) => peek() && peek().t === 'op' && peek().v === v;
  const expect = (v) => {
    if (!isOp(v)) throw new Error(`expected '${v}'${peek() ? ` near '${peek().v}'` : ' at end'}`);
    i++;
  };

  function expr() {
    let a = term();
    while (isOp('+') || isOp('-')) {
      const op = toks[i++].v, b = term(), l = a;
      a = op === '+' ? (s) => l(s) + b(s) : (s) => l(s) - b(s);
    }
    return a;
  }
  function startsPrimary() {
    const t = peek();
    return t && (t.t === 'num' || t.t === 'id' || (t.t === 'op' && t.v === '('));
  }
  function term() {
    let a = unary();
    for (;;) {
      if (isOp('*') || isOp('/')) {
        const op = toks[i++].v, b = unary(), l = a;
        a = op === '*' ? (s) => l(s) * b(s) : (s) => l(s) / b(s);
      } else if (startsPrimary()) { // implicit multiplication
        const b = power(), l = a;
        a = (s) => l(s) * b(s);
      } else return a;
    }
  }
  function unary() {
    if (isOp('-')) { i++; const a = unary(); return (s) => -a(s); }
    if (isOp('+')) { i++; return unary(); }
    return power();
  }
  function power() {
    const base = primary();
    if (isOp('^')) { i++; const ex = unary(); return (s) => Math.pow(base(s), ex(s)); }
    return base;
  }
  function primary() {
    const t = toks[i++];
    if (!t) throw new Error('unexpected end of expression');
    if (t.t === 'num') { const v = t.v; return () => v; }
    if (t.t === 'op' && t.v === '(') { const a = expr(); expect(')'); return a; }
    if (t.t === 'id') {
      const name = t.v;
      if (FUNCS[name]) {
        if (!isOp('(')) throw new Error(`${name} needs parentheses, e.g. ${name}(x)`);
        i++;
        const args = [expr()];
        while (isOp(',')) { i++; args.push(expr()); }
        expect(')');
        const f = FUNCS[name];
        if (args.length === 1) { const a = args[0]; return (s) => f(a(s)); }
        if (args.length === 2) { const [a, b] = args; return (s) => f(a(s), b(s)); }
        return (s) => f(...args.map((a) => a(s)));
      }
      if (name in CONSTS) { const v = CONSTS[name]; return () => v; }
      if (vars.includes(name)) return (s) => s[name];
      // "xy" → x*y when every letter is a variable.
      if ([...name].every((c) => vars.includes(c))) {
        const parts = [...name];
        return (s) => parts.reduce((acc, c) => acc * s[c], 1);
      }
      throw new Error(`unknown name '${name}' (variables: ${vars.join(', ')})`);
    }
    throw new Error(`unexpected '${t.v}'`);
  }

  const fn = expr();
  if (i < toks.length) throw new Error(`unexpected '${toks[i].v}'`);
  return fn;
}
