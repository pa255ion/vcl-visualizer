// Schematic symbols as plain geometry (board units, pins along ±x unless
// noted). Each symbol is
//   { lines: [[x,y]…][]  open polylines,
//     fills: [[x,y]…][]  filled polygons (arrowheads, dots),
//     texts: {text,x,y,size}[],
//     pins:  {name: {at:[x,y], dir:[dx,dy]}} }   dir points away from the part
// Pin names must match `part_pins` in core/src/vcl.zig.

const arc = (cx, cy, r, a0, a1, n = 24) => {
  const pts = [];
  for (let i = 0; i <= n; i++) {
    const a = a0 + ((a1 - a0) * i) / n;
    pts.push([cx + r * Math.cos(a), cy + r * Math.sin(a)]);
  }
  return pts;
};
const circle = (cx, cy, r, n = 40) => arc(cx, cy, r, 0, 2 * Math.PI, n);
const quad = (a, c, b, n = 16) => {
  const pts = [];
  for (let i = 0; i <= n; i++) {
    const t = i / n, u = 1 - t;
    pts.push([u * u * a[0] + 2 * u * t * c[0] + t * t * b[0], u * u * a[1] + 2 * u * t * c[1] + t * t * b[1]]);
  }
  return pts;
};
/** Small filled arrowhead with its tip at `tip`, pointing along (dx,dy). */
const head = (tip, dx, dy, s = 0.16) => {
  const l = Math.hypot(dx, dy), ux = dx / l, uy = dy / l;
  const bx = tip[0] - ux * s, by = tip[1] - uy * s;
  return [tip, [bx - uy * s * 0.5, by + ux * s * 0.5], [bx + uy * s * 0.5, by - ux * s * 0.5]];
};
const pin = (x, y, dx, dy) => ({ at: [x, y], dir: [dx, dy] });
const twoPin = { a: pin(-1, 0, -1, 0), b: pin(1, 0, 1, 0) };

function gate(kind) {
  const inv = /^(nand|nor|xnor|not)$/.test(kind);
  const base = kind.replace(/^n(?=and|or)/, '').replace(/^xn/, 'x');
  const lines = [], fills = [], texts = [];
  let backX = -0.6; // where input leads end
  if (base === 'and') {
    lines.push([[-0.6, 0.5], [0.1, 0.5], ...arc(0.1, 0, 0.5, Math.PI / 2, -Math.PI / 2).slice(1), [-0.6, -0.5], [-0.6, 0.5]]);
  } else if (base === 'or' || base === 'xor') {
    lines.push(quad([-0.6, 0.5], [-0.35, 0], [-0.6, -0.5]));
    lines.push([...quad([-0.6, 0.5], [0.15, 0.5], [0.6, 0]), ...quad([0.6, 0], [0.15, -0.5], [-0.6, -0.5]).slice(1)]);
    backX = -0.5;
    if (base === 'xor') { lines.push(quad([-0.75, 0.5], [-0.5, 0], [-0.75, -0.5])); backX = -0.65; }
  } else { // buf / not
    lines.push([[-0.5, 0.45], [0.5, 0], [-0.5, -0.45], [-0.5, 0.45]]);
    backX = -0.5;
  }
  let outX = base === 'buf' || base === 'not' ? 0.5 : 0.6;
  if (inv) { lines.push(circle(outX + 0.09, 0, 0.09, 20)); outX += 0.18; }
  lines.push([[outX, 0], [1.2, 0]]);
  const pins = { y: pin(1.2, 0, 1, 0) };
  if (base === 'buf' || base === 'not') {
    lines.push([[-1.2, 0], [backX, 0]]);
    pins.a = pin(-1.2, 0, -1, 0);
  } else {
    lines.push([[-1.2, 0.25], [backX, 0.25]], [[-1.2, -0.25], [backX, -0.25]]);
    pins.a = pin(-1.2, 0.25, -1, 0);
    pins.b = pin(-1.2, -0.25, -1, 0);
  }
  return { lines, fills, texts, pins };
}

function transistor(kind) {
  const lines = [circle(0.2, 0, 0.62)], fills = [], texts = [];
  if (kind === 'npn' || kind === 'pnp') {
    lines.push([[-1, 0], [-0.15, 0]], [[-0.15, -0.4], [-0.15, 0.4]]);
    lines.push([[-0.15, 0.18], [0.5, 0.55], [0.5, 1]], [[-0.15, -0.18], [0.5, -0.55], [0.5, -1]]);
    const e0 = [-0.15, -0.18], e1 = [0.5, -0.55];
    if (kind === 'npn') fills.push(head([0.38, -0.48], e1[0] - e0[0], e1[1] - e0[1]));
    else fills.push(head([-0.02, -0.255], e0[0] - e1[0], e0[1] - e1[1]));
    return { lines, fills, texts, pins: { b: pin(-1, 0, -1, 0), c: pin(0.5, 1, 0, 1), e: pin(0.5, -1, 0, -1) } };
  }
  // MOSFETs: gate g on the left, drain d up, source s down.
  const p = kind === 'pmos';
  lines.push([[-1, 0], [p ? -0.55 : -0.35, 0]], [[-0.35, -0.45], [-0.35, 0.45]], [[-0.2, -0.5], [-0.2, 0.5]]);
  if (p) lines.push(circle(-0.45, 0, 0.1, 20));
  lines.push([[-0.2, 0.35], [0.5, 0.35], [0.5, 1]], [[-0.2, -0.35], [0.5, -0.35], [0.5, -1]]);
  fills.push(p ? head([-0.05, -0.35], -1, 0) : head([0.3, -0.35], 1, 0));
  return { lines, fills, texts, pins: { g: pin(-1, 0, -1, 0), d: pin(0.5, 1, 0, 1), s: pin(0.5, -1, 0, -1) } };
}

const SYMBOLS = {
  resistor: () => ({
    lines: [[[-1, 0], [-0.6, 0], [-0.5, 0.18], [-0.3, -0.18], [-0.1, 0.18], [0.1, -0.18], [0.3, 0.18], [0.5, -0.18], [0.6, 0], [1, 0]]],
    pins: twoPin,
  }),
  capacitor: () => ({
    lines: [[[-1, 0], [-0.12, 0]], [[-0.12, -0.4], [-0.12, 0.4]], [[0.12, -0.4], [0.12, 0.4]], [[0.12, 0], [1, 0]]],
    pins: twoPin,
  }),
  inductor: () => ({
    lines: [[[-1, 0], [-0.6, 0], ...[-0.45, -0.15, 0.15, 0.45].flatMap((cx) => arc(cx, 0, 0.15, Math.PI, 0, 12).slice(1)), [1, 0]]],
    pins: twoPin,
  }),
  vsource: () => ({
    lines: [[[-1, 0], [-0.45, 0]], circle(0, 0, 0.45), [[0.45, 0], [1, 0]]],
    texts: [{ text: '+', x: -0.2, y: 0, size: 0.34 }, { text: '−', x: 0.2, y: 0, size: 0.34 }],
    pins: twoPin,
  }),
  isource: () => ({
    lines: [[[-1, 0], [-0.45, 0]], circle(0, 0, 0.45), [[0.45, 0], [1, 0]], [[-0.25, 0], [0.12, 0]]],
    fills: [head([0.27, 0], 1, 0)],
    pins: twoPin,
  }),
  battery: () => ({
    lines: [[[-1, 0], [-0.1, 0]], [[-0.1, -0.35], [-0.1, 0.35]], [[0.1, -0.18], [0.1, 0.18]], [[0.1, 0], [1, 0]]],
    texts: [{ text: '+', x: -0.32, y: 0.32, size: 0.26 }],
    pins: twoPin,
  }),
  switch: () => ({
    lines: [[[-1, 0], [-0.4, 0]], [[-0.4, 0], [0.35, 0.32]], [[0.4, 0], [1, 0]]],
    fills: [circle(-0.4, 0, 0.06, 16), circle(0.4, 0, 0.06, 16)],
    pins: twoPin,
  }),
  diode: () => ({
    lines: [[[-1, 0], [-0.3, 0]], [[-0.3, 0.3], [0.3, 0], [-0.3, -0.3], [-0.3, 0.3]], [[0.3, -0.3], [0.3, 0.3]], [[0.3, 0], [1, 0]]],
    pins: { a: pin(-1, 0, -1, 0), k: pin(1, 0, 1, 0) },
  }),
  led: () => ({
    lines: [[[-1, 0], [-0.3, 0]], [[-0.3, 0.3], [0.3, 0], [-0.3, -0.3], [-0.3, 0.3]], [[0.3, -0.3], [0.3, 0.3]], [[0.3, 0], [1, 0]],
      [[0, 0.38], [0.24, 0.62]], [[0.22, 0.32], [0.46, 0.56]]],
    fills: [head([0.32, 0.7], 1, 1, 0.13), head([0.54, 0.64], 1, 1, 0.13)],
    pins: { a: pin(-1, 0, -1, 0), k: pin(1, 0, 1, 0) },
  }),
  ground: () => ({
    lines: [[[0, 0], [0, -0.3]], [[-0.35, -0.3], [0.35, -0.3]], [[-0.22, -0.42], [0.22, -0.42]], [[-0.09, -0.54], [0.09, -0.54]]],
    pins: { a: pin(0, 0, 0, 1) },
  }),
  vdd: () => ({
    lines: [[[0, 0], [0, 0.3]], [[-0.3, 0.3], [0.3, 0.3]]],
    texts: [{ text: 'VDD', x: 0, y: 0.56, size: 0.28, value: true }],
    pins: { a: pin(0, 0, 0, -1) },
  }),
  npn: () => transistor('npn'),
  pnp: () => transistor('pnp'),
  nmos: () => transistor('nmos'),
  pmos: () => transistor('pmos'),
  opamp: () => ({
    lines: [[[-0.8, 0.9], [0.9, 0], [-0.8, -0.9], [-0.8, 0.9]], [[-1.4, 0.45], [-0.8, 0.45]], [[-1.4, -0.45], [-0.8, -0.45]], [[0.9, 0], [1.4, 0]]],
    texts: [{ text: '−', x: -0.6, y: 0.45, size: 0.34 }, { text: '+', x: -0.6, y: -0.45, size: 0.34 }],
    pins: { inn: pin(-1.4, 0.45, -1, 0), inp: pin(-1.4, -0.45, -1, 0), out: pin(1.4, 0, 1, 0) },
  }),
  and: () => gate('and'), or: () => gate('or'), nand: () => gate('nand'), nor: () => gate('nor'),
  xor: () => gate('xor'), xnor: () => gate('xnor'), not: () => gate('not'), buf: () => gate('buf'),
  dff: () => ({
    lines: [[[-0.8, -1], [0.8, -1], [0.8, 1], [-0.8, 1], [-0.8, -1]], [[-0.8, -0.32], [-0.58, -0.5], [-0.8, -0.68]],
      [[-1.3, 0.5], [-0.8, 0.5]], [[-1.3, -0.5], [-0.8, -0.5]], [[0.8, 0.5], [1.3, 0.5]], [[0.8, -0.5], [1.3, -0.5]]],
    texts: [{ text: 'D', x: -0.55, y: 0.5, size: 0.3 }, { text: 'Q', x: 0.55, y: 0.5, size: 0.3 }, { text: 'Q̄', x: 0.55, y: -0.5, size: 0.3 }],
    pins: { d: pin(-1.3, 0.5, -1, 0), clk: pin(-1.3, -0.5, -1, 0), q: pin(1.3, 0.5, 1, 0), qn: pin(1.3, -0.5, 1, 0) },
  }),
  mux2: () => ({
    lines: [[[-0.5, 1], [0.5, 0.6], [0.5, -0.6], [-0.5, -1], [-0.5, 1]], [[-1.1, 0.5], [-0.5, 0.5]], [[-1.1, -0.5], [-0.5, -0.5]],
      [[0.5, 0], [1.1, 0]], [[0, -1.3], [0, -0.8]]],
    texts: [{ text: '0', x: -0.3, y: 0.5, size: 0.28 }, { text: '1', x: -0.3, y: -0.5, size: 0.28 }],
    pins: { a: pin(-1.1, 0.5, -1, 0), b: pin(-1.1, -0.5, -1, 0), s: pin(0, -1.3, 0, -1), y: pin(1.1, 0, 1, 0) },
  }),
};

export const PART_KINDS = Object.keys(SYMBOLS);

/** Geometry for a part, with defaults filled in. */
export function symbol(kind) {
  const make = SYMBOLS[kind];
  if (!make) throw new Error(`unknown part '${kind}'`);
  const s = make();
  return { lines: s.lines ?? [], fills: s.fills ?? [], texts: s.texts ?? [], pins: s.pins };
}

/**
 * Orthogonal ("Manhattan") route through points, schematic style.
 * pts: [{x,y,z, dir?:{x,y}}]; mode: auto | hv | vh | direct. Returns [{x,y,z}].
 * auto: leave each pin along its direction; horizontal→horizontal pins get a
 * Z-shaped route through the midpoint, mixed pins a single elbow.
 */
export function route(pts, mode = 'auto') {
  const out = [{ ...pts[0] }];
  const push = (x, y, z) => {
    const last = out[out.length - 1];
    if (Math.abs(last.x - x) < 1e-6 && Math.abs(last.y - y) < 1e-6) return;
    out.push({ x, y, z });
  };
  const horiz = (d) => d && Math.abs(d.x) >= Math.abs(d.y);
  for (let i = 0; i + 1 < pts.length; i++) {
    const a = pts[i], b = pts[i + 1], z = b.z;
    let m = mode;
    if (m === 'auto') {
      const ha = a.dir ? horiz(a.dir) : null, hb = b.dir ? horiz(b.dir) : null;
      if (ha === null && hb === null) m = 'hv';
      else if (ha !== null && hb !== null) m = ha && hb ? 'zh' : !ha && !hb ? 'zv' : ha ? 'hv' : 'vh';
      else m = (ha ?? hb) ? (ha !== null ? 'hv' : 'vh') : (ha !== null ? 'vh' : 'hv');
    }
    if (m === 'direct') push(b.x, b.y, z);
    else if (m === 'hv') { push(b.x, a.y, z); push(b.x, b.y, z); }
    else if (m === 'vh') { push(a.x, b.y, z); push(b.x, b.y, z); }
    else if (m === 'zh') {
      let xm = (a.x + b.x) / 2;
      // Leave each pin outward by at least a stub when the midpoint is behind it.
      if (a.dir && (xm - a.x) * a.dir.x < 0.3 && b.dir && (xm - b.x) * b.dir.x < 0.3) xm = a.x + 0.3 * Math.sign(a.dir.x || 1);
      push(xm, a.y, z); push(xm, b.y, z); push(b.x, b.y, z);
    } else { // zv
      const ym = (a.y + b.y) / 2;
      push(a.x, ym, z); push(b.x, ym, z); push(b.x, b.y, z);
    }
  }
  // Merge collinear runs.
  const merged = [out[0]];
  for (let i = 1; i < out.length; i++) {
    const p = merged[merged.length - 1], q = out[i], n = out[i + 1];
    if (n && ((Math.abs(p.x - q.x) < 1e-6 && Math.abs(q.x - n.x) < 1e-6) || (Math.abs(p.y - q.y) < 1e-6 && Math.abs(q.y - n.y) < 1e-6))) continue;
    merged.push(q);
  }
  return merged;
}
