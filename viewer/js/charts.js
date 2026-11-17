// Charts on axes (bars, hist, scatter, errbar, band, contour) and structured
// diagrams (nn, grid, bits). The parser has already turned their data strings
// into arrays and listed their pins (docs/IR.md); this draws them.
//
// Diagrams expose `e.parts`: named pieces that `hl`, `color` and `fade` can
// target (`hl n.L1_3`), each { mats (with .color), objs (scaled by hl), apply() }.
import * as THREE from 'three';
import { TextPlane } from './text.js';
import { wavePin } from './panels.js';
import * as D from './draw.js';

const V3 = (x, y, z = 0) => new THREE.Vector3(x, y, z);
const clamp01 = (v) => Math.min(1, Math.max(0, v));

const CMAPS = {
  gray: ['#101418', '#f5f5f5'],
  heat: ['#440154', '#3b528b', '#21918c', '#5ec962', '#fde725'], // viridis
  div: ['#2166ac', '#f7f7f7', '#b2182b'],
  levels: ['#3949ab', '#26a69a', '#ffee58'], // as surf
};
function cmap(name, t, out = new THREE.Color()) {
  const stops = CMAPS[name];
  const x = clamp01(t) * (stops.length - 1);
  const i = Math.min(stops.length - 2, Math.floor(x));
  return out.set(stops[i]).lerp(new THREE.Color(stops[i + 1]), x - i);
}

/** Short number for a cell or a bar: 3, 0.25, 12.5, 1.2e+3. */
function fmt(v) {
  if (Number.isInteger(v)) return String(v);
  const a = Math.abs(v);
  if (a >= 1e4 || (a > 0 && a < 1e-3)) return v.toExponential(1);
  return String(Number(v.toFixed(a >= 100 ? 0 : a >= 10 ? 1 : 2)));
}

export function addCharts(BUILD, { FG, PALETTE, axesOf, labelFor }) {
  // ── helpers ──
  function text(e, parent, str, pos, { size = 0.3, color = FG, align = 'center', role = 'label', opacity = 1 } = {}) {
    const tp = e.own(new TextPlane(str, { size, color, align }));
    tp.material.opacity = opacity;
    e.track(tp.material, role);
    tp.placeAt(pos);
    parent.add(tp.mesh);
    if (e.in3D) e.billboards.push(tp.mesh);
    return tp;
  }
  function basic(e, color, role, opacity = 1) {
    return e.track(new THREE.MeshBasicMaterial({ color, opacity, depthWrite: false, side: THREE.DoubleSide }), role);
  }
  function stroke(e, parent, color, width, { dash = false, role = 'stroke', opacity = 1 } = {}) {
    const s = e.own(new D.Stroke({ color, width, dash }));
    s.material.opacity = opacity;
    e.track(s.material, role);
    parent.add(s.obj);
    return s;
  }
  const unitQuad = new THREE.PlaneGeometry(1, 1);
  /** A mesh showing the rectangle a..b (opposite corners), for bars. */
  function setQuad(m, a, b) {
    m.position.set((a.x + b.x) / 2, (a.y + b.y) / 2, (a.z + b.z) / 2);
    m.scale.set(Math.max(1e-4, Math.abs(b.x - a.x)), Math.max(1e-4, Math.abs(b.y - a.y)), 1);
  }
  function quad(mat) {
    const m = new THREE.Mesh(unitQuad.clone(), mat);
    m.renderOrder = 1;
    return m;
  }

  // ── nn: layers of neurons, fully connected ──
  BUILD.nn = function (e, c) {
    const sizes = c.sizes, shown = c.shown, L = sizes.length;
    const [W, H] = c.size ?? [Math.max(2, 2.6 * (L - 1)), 4];
    e.obj.position.copy(e.toParent(c.at));
    const collapsed = sizes.map((n, i) => shown[i].length < n);
    const slots = shown.map((s, i) => s.length + (collapsed[i] ? 1 : 0));
    const maxSlots = Math.max(...slots);
    const d = maxSlots > 1 ? Math.min(0.95, H / (maxSlots - 1)) : 1;
    const r = Math.min(0.3, d * 0.34);
    const color = c.color ?? '#7fa7d9', errColor = '#ef9a9a', edgeColor = '#6b7a8f';
    const labels = c.labels ? c.labels.split(',').map((s) => s.trim()) : [];
    const xOf = (i) => (L > 1 ? -W / 2 + (i * W) / (L - 1) : 0);
    const disc = new THREE.CircleGeometry(1, 40), ring = new THREE.RingGeometry(0.8, 1, 40);
    e.parts = {};
    e.pins = {};

    const neuron = (x, y, rad, col) => {
      const g = new THREE.Group();
      g.position.set(x, y, 0.01);
      const fm = basic(e, '#1b2635', 'fill', 0.95), rm = basic(e, col, 'stroke');
      const f = new THREE.Mesh(disc, fm), rr = new THREE.Mesh(ring, rm);
      f.scale.setScalar(rad);
      rr.scale.setScalar(rad);
      f.renderOrder = 2;
      rr.renderOrder = 3;
      g.add(f, rr);
      e.body.add(g);
      return { obj: g, mats: [rm, fm], at: V3(x, y) };
    };

    const layers = [];
    const yLow = -((maxSlots - 1) * d) / 2 - r;
    for (let i = 0; i < L; i++) {
      const x = xOf(i), s = shown[i];
      const y0 = ((slots[i] - 1) * d) / 2;
      let top = 0;
      while (top < s.length && s[top] === top + 1) top++; // shown above the ellipsis
      const lay = { vals: [], errs: [], objs: [] };
      let slot = 0;
      s.forEach((j, k) => {
        if (collapsed[i] && k === top) {
          const dots = new THREE.Group(); // ⋮
          dots.position.set(x, y0 - slot * d, 0.01);
          const dm = basic(e, '#aab6c5', 'stroke');
          for (const dy of [-1, 0, 1]) {
            const m = new THREE.Mesh(disc, dm);
            m.scale.setScalar(Math.max(0.025, r * 0.13));
            m.position.y = dy * Math.min(d * 0.28, 0.2);
            dots.add(m);
          }
          e.body.add(dots);
          lay.objs.push(dots);
          slot++;
        }
        const y = y0 - slot * d;
        slot++;
        const v = neuron(x, y, r, color);
        lay.vals.push(v);
        lay.objs.push(v.obj);
        e.parts[`L${i}_${j}`] = { mats: v.mats, objs: [v.obj] };
        e.pins[`L${i}_${j}`] = { at: v.at, dir: V3(1, 0) };
        if (c.pairs) { // an error node beside each value node
          const er = neuron(x + r * 1.45, y - r * 0.75, r * 0.48, errColor);
          lay.errs.push(er);
          lay.objs.push(er.obj);
          e.parts[`L${i}_${j}e`] = { mats: er.mats, objs: [er.obj] };
          e.pins[`L${i}_${j}e`] = { at: er.at, dir: V3(1, 0) };
        }
      });
      const all = [...lay.vals, ...lay.errs];
      const part = { mats: all.flatMap((n) => n.mats), objs: all.map((n) => n.obj) };
      e.parts[`L${i}`] = e.parts[`L${i}_top`] = e.parts[`L${i}_bottom`] = part;
      e.pins[`L${i}`] = { at: V3(x, 0), dir: V3(0, 1) };
      e.pins[`L${i}_top`] = { at: V3(x, y0 + r), dir: V3(0, 1) };
      e.pins[`L${i}_bottom`] = { at: V3(x, -y0 - r), dir: V3(0, -1) };
      let lab = labels[i] ?? '';
      if (collapsed[i]) lab = lab ? `${lab} (${sizes[i]})` : String(sizes[i]);
      if (lab) text(e, e.obj, lab, V3(x, yLow - 0.38), { size: 0.32 });
      layers.push(lay);
    }

    // Edges between consecutive layers, circle border to circle border.
    const link = (a, b, ra, rb) => {
      const dir = b.at.clone().sub(a.at).normalize();
      return [a.at.clone().addScaledVector(dir, ra), b.at.clone().addScaledVector(dir, -rb)];
    };
    const forward = [], feedback = [];
    for (let i = 1; i < L; i++) {
      if (c.edges !== 'none') {
        const s = stroke(e, e.body, edgeColor, 0.022, { opacity: 0.75 });
        s.obj.renderOrder = 1;
        const polys = [];
        for (const a of layers[i - 1].vals) for (const b of layers[i].vals) polys.push(link(a, b, r, r));
        forward.push({ s, polys, i });
        e.parts[`E${i}`] = { mats: [s.material] };
        e.pins[`E${i}`] = { at: V3((xOf(i - 1) + xOf(i)) / 2, 0), dir: V3(0, 1) };
      }
      if (c.feedback) { // predictions back down: value nodes of layer i → error (or value) nodes of i-1
        const s = stroke(e, e.body, errColor, 0.02, { dash: true, opacity: 0.5 });
        s.obj.renderOrder = 1;
        const targets = c.pairs ? layers[i - 1].errs : layers[i - 1].vals;
        const rt = c.pairs ? r * 0.48 : r;
        const polys = [];
        for (const a of layers[i].vals) for (const b of targets) {
          const [p0, p1] = link(a, b, r, rt);
          const mid = p0.clone().lerp(p1, 0.5).add(V3(0, -0.12 * p0.distanceTo(p1)));
          polys.push(new THREE.QuadraticBezierCurve3(p0, mid, p1).getPoints(12));
        }
        feedback.push({ s, polys });
        e.parts[`F${i}`] = { mats: [s.material] };
        e.pins[`F${i}`] = { at: V3((xOf(i - 1) + xOf(i)) / 2, -0.3), dir: V3(0, -1) };
      }
    }

    e.render = (p) => {
      const k = p * L;
      layers.forEach((lay, i) => {
        const a = clamp01((k - i) * 1.6);
        lay.objs.forEach((o) => o.scale.setScalar(Math.max(1e-3, a)));
      });
      for (const { s, polys, i } of forward) {
        const f = clamp01(k - i + 0.4);
        s.set(f > 0 ? polys.map(([a, b]) => [a, a.clone().lerp(b, f)]) : []);
      }
      const fb = clamp01((p - 0.7) / 0.3);
      for (const { s, polys } of feedback) s.set(fb > 0 ? polys.map((pl) => D.partial([pl], fb)[0]).filter(Boolean) : []);
    };
    labelFor(e, c, V3(0, ((maxSlots - 1) * d) / 2 + r + 0.4, 0));
  };

  // ── grid: a heatmap of values (canvas texture; cells are parts) ──
  BUILD.grid = function (e, c) {
    const R = Math.round(c.dims[0]), C = Math.round(c.dims[1]);
    const cell0 = Math.min(0.45, 8 / C, 6 / R);
    const [W, H] = c.size ?? [C * cell0, R * cell0];
    e.obj.position.copy(e.toParent(c.at));
    const pxc = Math.max(12, Math.min(96, Math.floor(2048 / Math.max(R, C))));
    const canvas = document.createElement('canvas');
    canvas.width = C * pxc;
    canvas.height = R * pxc;
    const g = canvas.getContext('2d');
    const tex = new THREE.CanvasTexture(canvas);
    tex.colorSpace = THREE.SRGBColorSpace;
    tex.anisotropy = 8;
    const mat = e.track(new THREE.MeshBasicMaterial({ map: tex, depthWrite: false, side: THREE.DoubleSide }), 'fill');
    const mesh = new THREE.Mesh(new THREE.PlaneGeometry(W, H), mat);
    mesh.renderOrder = 1;
    e.body.add(mesh);
    stroke(e, e.body, '#5d6b7e', 0.025).set([[V3(-W / 2, -H / 2), V3(W / 2, -H / 2), V3(W / 2, H / 2), V3(-W / 2, H / 2), V3(-W / 2, -H / 2)]]);

    let vals = c.cells.slice();
    const cells = vals.map(() => ({ color: new THREE.Color(), userData: {} }));
    const recolor = () => {
      let lo = Math.min(...vals), hi = Math.max(...vals);
      const name = c.cmap ?? (lo < 0 ? 'div' : 'gray');
      if (c.range) [lo, hi] = c.range;
      else if (name === 'div') { const m = Math.max(Math.abs(lo), Math.abs(hi)) || 1; lo = -m; hi = m; }
      const span = hi - lo || 1;
      vals.forEach((v, k) => cmap(name, (v - lo) / span, cells[k].color));
    };
    const gap = pxc >= 24 ? 1 : 0;
    const draw = () => {
      g.clearRect(0, 0, canvas.width, canvas.height);
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.font = `600 ${Math.round(pxc * 0.36)}px "Inter", system-ui, sans-serif`;
      vals.forEach((v, k) => {
        const col = cells[k].color, x = (k % C) * pxc, y = Math.floor(k / C) * pxc;
        g.globalAlpha = cells[k].userData.dim ?? 1;
        g.fillStyle = col.getStyle();
        g.fillRect(x + gap, y + gap, pxc - 2 * gap, pxc - 2 * gap);
        if (c.text) {
          const lum = 0.2126 * col.r + 0.7152 * col.g + 0.0722 * col.b; // linear
          g.fillStyle = lum > 0.22 ? '#111' : '#f5f5f5';
          g.fillText(fmt(v), x + pxc / 2, y + pxc / 2 + 1);
        }
      });
      g.globalAlpha = 1;
      tex.needsUpdate = true;
    };
    recolor();
    draw();

    const cw = W / C, ch = H / R;
    e.parts = {};
    e.pins = {};
    for (let i = 1; i <= R; i++) {
      e.parts[`r${i}`] = { mats: cells.slice((i - 1) * C, i * C), apply: draw };
      e.pins[`r${i}`] = { at: V3(-W / 2, H / 2 - (i - 0.5) * ch), dir: V3(-1, 0) };
    }
    for (let j = 1; j <= C; j++) {
      e.parts[`c${j}`] = { mats: cells.filter((_, k) => k % C === j - 1), apply: draw };
      e.pins[`c${j}`] = { at: V3(-W / 2 + (j - 0.5) * cw, H / 2), dir: V3(0, 1) };
    }
    if (R * C <= 4096) for (let i = 1; i <= R; i++) for (let j = 1; j <= C; j++) {
      e.parts[`r${i}c${j}`] = { mats: [cells[(i - 1) * C + j - 1]], apply: draw };
      e.pins[`r${i}c${j}`] = { at: V3(-W / 2 + (j - 0.5) * cw, H / 2 - (i - 0.5) * ch), dir: V3(0, 1) };
    }
    // set g text="…": new values, same shape (weights that change, a new digit).
    e.setText = (t) => {
      const next = String(t).split(/[\s,;]+/).filter(Boolean).map(Number);
      if (next.length !== vals.length || next.some((v) => !Number.isFinite(v)))
        return e.rt.log('warning', `grid '${e.id}': set text= needs ${vals.length} numbers`);
      vals = next;
      recolor();
      draw();
    };
    labelFor(e, c, V3(0, H / 2 + 0.3, 0));
  };

  // ── bits: a register / fixed-point format, one box per bit ──
  BUILD.bits = function (e, c) {
    const groups = c.pattern.split('|').map((s) => s.replace(/ /g, ''));
    const widths = c.widths ?? groups.map((s) => s.length);
    const n = widths.reduce((a, b) => a + b, 0);
    const cell = c.size ?? Math.min(0.55, 14 / Math.max(n, 1));
    e.obj.position.copy(e.toParent(c.at));
    const W = n * cell, x0 = -W / 2;
    const yb = -0.3 * cell, yt = yb + cell; // box bottom and top
    const labels = c.labels ? c.labels.split(',').map((s) => s.trim()) : [];
    const pattern = groups.join('');
    const lw = Math.min(0.035, cell * 0.07);
    e.parts = {};
    e.pins = {};
    const digits = [];
    let i = 0;
    widths.forEach((w, f) => {
      const col = c.color ?? PALETTE[f % PALETTE.length];
      const fieldMats = [];
      const fx0 = x0 + i * cell;
      for (let k = 0; k < w; k++, i++) {
        const bit = n - 1 - i;
        const cx = x0 + (i + 0.5) * cell, cy = (yb + yt) / 2;
        const grp = new THREE.Group();
        grp.position.set(cx, cy, 0);
        e.body.add(grp);
        const fm = basic(e, col, 'fill', 0.22);
        const q = quad(fm);
        setQuad(q, V3(-cell / 2, -cell / 2), V3(cell / 2, cell / 2));
        grp.add(q);
        const s = stroke(e, grp, col, lw);
        const h = cell / 2;
        s.set([[V3(-h, -h), V3(h, -h), V3(h, h), V3(-h, h), V3(-h, -h)]]);
        const dg = text(e, grp, ' ', V3(0, 0, 0.01), { size: cell * 0.62, role: 'text' });
        digits.push(dg);
        if (c.index ?? true) text(e, e.body, String(bit), V3(cx, yt + cell * 0.32), { size: Math.max(0.16, cell * 0.36), color: '#8a98ab' });
        const mats = [s.material, fm];
        fieldMats.push(...mats);
        e.parts[`b${bit}`] = { mats, objs: [grp] };
        e.pins[`b${bit}`] = { at: V3(cx, yt + cell * 0.6), dir: V3(0, 1) };
      }
      // A brace under the field, and its label.
      const fx1 = x0 + i * cell, xm = (fx0 + fx1) / 2;
      const by = yb - 0.08 * cell, bd = Math.max(0.12, 0.3 * cell);
      const br = stroke(e, e.body, col, lw * 0.9, { role: 'label' });
      const inset = Math.min(0.06, (fx1 - fx0) / 6);
      br.set([[V3(fx0 + inset, by), V3(fx0 + inset, by - bd * 0.45), V3(xm - bd * 0.4, by - bd * 0.45), V3(xm, by - bd),
        V3(xm + bd * 0.4, by - bd * 0.45), V3(fx1 - inset, by - bd * 0.45), V3(fx1 - inset, by)]]);
      const ly = by - bd - Math.max(0.2, cell * 0.42);
      if (labels[f]) text(e, e.body, labels[f], V3(xm, ly), { size: Math.max(0.26, cell * 0.5), color: col });
      const part = { mats: [...fieldMats, br.material] };
      const pin = { at: V3(xm, ly - Math.max(0.2, cell * 0.4)), dir: V3(0, -1) };
      e.parts[`F${f + 1}`] = part;
      e.pins[`F${f + 1}`] = pin;
      const named = labels[f] ? wavePin(labels[f]) : '';
      if (named && !(named in e.pins)) { e.parts[named] = part; e.pins[named] = pin; }
    });
    // Values in the boxes; without them, the pattern's letters, dimmed.
    e.setText = (t) => {
      const v = String(t ?? '').replace(/[|\s]/g, '');
      digits.forEach((dg, k) => {
        dg.setText(v[k] ?? pattern[k] ?? ' ');
        dg.material.userData.base = v[k] ? 1 : 0.45;
      });
      e.applyOpacity();
    };
    e.setText(c.values ?? '');
    labelFor(e, c, V3(0, yt + cell * 0.95, 0));
  };

  // ── bars and histograms on axes ──
  function barsEntity(e, c, barW) {
    const A = axesOf(e, c);
    const xs = [...new Set(c.bars.map((b) => b[0]))].sort((a, b) => a - b);
    let step = Infinity;
    for (let i = 1; i < xs.length; i++) step = Math.min(step, xs[i] - xs[i - 1]);
    if (!Number.isFinite(step)) step = 1;
    const w = barW ?? c.width ?? 0.6 * step;
    const depth = Math.max(...c.bars.map((b) => b[1].length));
    const single = c.color ?? PALETTE[A.plots++ % PALETTE.length];
    const colors = Array.from({ length: depth }, (_, k) => (depth > 1 ? PALETTE[k % PALETTE.length] : single));
    const base = Math.min(Math.max(0, A.y[0]), A.y[1]);
    const segs = [];
    e.parts = {};
    e.pins = {};
    c.bars.forEach(([x, ys], i) => {
      let cum = base;
      const mats = [];
      ys.forEach((y, k) => {
        const m = basic(e, colors[k], 'solid', 0.82);
        const q = quad(m);
        e.body.add(q);
        segs.push({ q, x, lo: cum, hi: cum + y });
        cum += y;
        mats.push(m);
      });
      e.parts[`B${i + 1}`] = { mats };
      e.pins[`B${i + 1}`] = { at: A.map(V3(x, cum)).add(V3(0, cum >= base ? 0.08 : -0.08)), dir: V3(0, cum >= base ? 1 : -1) };
    });
    e.render = (p) => {
      for (const s of segs) {
        const lo = base + (s.lo - base) * p, hi = base + (s.hi - base) * p;
        setQuad(s.q, A.map(V3(s.x - w / 2, lo)), A.map(V3(s.x + w / 2, hi)));
      }
    };
    if (c.labels) c.labels.split(',').forEach((t, i) => {
      if (c.bars[i]) text(e, e.obj, t.trim(), A.map(V3(c.bars[i][0], A.y[0])).add(V3(0, -0.34)), { size: 0.28 });
    });
    if (c.series) {
      const corner = A.map(V3(A.x[1], A.y[1]));
      c.series.split(',').forEach((t, k) => {
        const y = corner.y - 0.15 - k * 0.42, x = corner.x + 0.45;
        const q = quad(basic(e, colors[k % colors.length], 'label', 0.82));
        setQuad(q, V3(x, y - 0.12), V3(x + 0.24, y + 0.12));
        e.obj.add(q);
        text(e, e.obj, t.trim(), V3(x + 0.36, y), { size: 0.28, align: 'left' });
      });
    }
  }
  BUILD.bars = function (e, c) { barsEntity(e, c); };
  BUILD.hist = function (e, c) { barsEntity(e, c, c.bw * 0.96); };

  // ── scatter, error bars, bands ──
  BUILD.scatter = function (e, c) {
    const A = axesOf(e, c);
    const color = c.color ?? PALETTE[A.plots++ % PALETTE.length];
    const r = c.size ?? 0.09;
    const mat = basic(e, color, 'stroke');
    const geo = A.is3 ? new THREE.SphereGeometry(r, 16, 12) : new THREE.CircleGeometry(r, 20);
    e.pins = {};
    const dots = c.pts.map((pt, i) => {
      const m = new THREE.Mesh(geo, mat);
      m.position.copy(A.map(V3(pt[0], pt[1], pt[2] ?? 0)));
      if (!A.is3) m.position.z += 0.01;
      m.renderOrder = 2;
      e.body.add(m);
      e.pins[`P${i + 1}`] = { at: m.position.clone(), dir: V3(0, 1) };
      return m;
    });
    e.render = (p) => dots.forEach((m, i) => m.scale.setScalar(Math.max(1e-3, clamp01(p * dots.length * 1.5 - i))));
    if (c.label !== undefined) { e.addLabel(c.label, V3(), 'left'); e.label.placeAt(dots.at(-1).position.clone().add(V3(0.2, 0.2))); }
  };

  BUILD.errbar = function (e, c) {
    const A = axesOf(e, c);
    const color = c.color ?? PALETTE[A.plots++ % PALETTE.length];
    const cap = (c.cap ?? 0.18) / 2, r = c.size ?? 0.08;
    const s = stroke(e, e.body, color, c.width ?? 0.04);
    const dotMat = basic(e, color, 'stroke');
    const dotGeo = new THREE.CircleGeometry(r, 20);
    const items = [];
    e.pins = {};
    c.bars.forEach(([x, y, lo, hi], i) => {
      // horiz: x is the row's y, the interval runs along x, and y is the estimate's x.
      const at = (u, v) => A.map(c.horiz ? V3(v, u) : V3(u, v));
      const a = at(x, lo), b = at(x, hi);
      const t = c.horiz ? V3(0, cap) : V3(cap, 0);
      const mid = y == null ? a.clone().lerp(b, 0.5) : at(x, y);
      const polys = [[a, b], [a.clone().sub(t), a.clone().add(t)], [b.clone().sub(t), b.clone().add(t)]];
      let dot = null;
      if (y != null) {
        dot = new THREE.Mesh(dotGeo, dotMat);
        dot.position.copy(mid).add(V3(0, 0, 0.01));
        dot.renderOrder = 2;
        e.body.add(dot);
      }
      items.push({ polys, mid, dot });
      e.pins[`E${i + 1}`] = { at: mid.clone(), dir: c.horiz ? V3(0, 1) : V3(1, 0) };
    });
    e.render = (p) => {
      s.set(items.flatMap(({ polys, mid }) => polys.map((pl) => pl.map((v) => mid.clone().lerp(v, p)))));
      for (const it of items) it.dot?.scale.setScalar(Math.max(1e-3, p));
    };
  };

  BUILD.band = function (e, c) {
    const A = axesOf(e, c);
    const [x0, x1] = c.x ?? A.x, [y0, y1] = c.y ?? A.y;
    const color = c.fill ?? c.color ?? '#90a4ae';
    const q = quad(basic(e, color, 'fill', 0.2));
    const a = A.map(V3(x0, y0)), b = A.map(V3(x1, y1));
    a.z = b.z = -0.01;
    setQuad(q, a, b);
    q.renderOrder = 0;
    e.body.add(q);
    const edges = [];
    if (c.y) edges.push([A.map(V3(x0, y0)), A.map(V3(x1, y0))], [A.map(V3(x0, y1)), A.map(V3(x1, y1))]);
    if (c.x) edges.push([A.map(V3(x0, y0)), A.map(V3(x0, y1))], [A.map(V3(x1, y0)), A.map(V3(x1, y1))]);
    stroke(e, e.body, color, 0.03, { dash: true, opacity: 0.85 }).set(edges);
    if (c.label !== undefined) {
      e.addLabel(c.label, V3(), 'left');
      e.label.placeAt(A.map(V3(x0, y1)).add(V3(0.15, -0.25, 0.01)));
    }
  };

  // ── contour lines (sampled by the parser) ──
  BUILD.contour = function (e, c) {
    const A = axesOf(e, c);
    if (!c.contours) throw new Error('no samples in the IR (the server is older than this viewer)');
    const n = c.contours.length;
    const levels = c.contours.map((lv, i) => {
      const color = c.color ?? `#${cmap('levels', n > 1 ? i / (n - 1) : 0.5).getHexString()}`;
      const s = stroke(e, e.body, color, c.width ?? 0.04);
      return { s, polys: lv.polys.map((pl) => pl.map(([x, y]) => A.map(V3(x, y)))) };
    });
    e.render = (p) => levels.forEach(({ s, polys }) => s.set(D.partial(polys, p)));
  };
}
