// Executes VCL IR commands (docs/IR.md) against a three.js scene.
//
// Coordinates: every entity's Object3D lives in "board units" under `stage`
// (or under an axes/group entity). Points in IR are either [x,y,z] in the
// parent's space (data space for entities `on` an axes) or references
// {ref, anchor, off} resolved against live entities when the command runs.
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { Timeline } from './timeline.js';
import { compile } from './expr.js';
import { TextPlane } from './text.js';
import * as D from './draw.js';

export const FG = '#e8edf3';
const PALETTE = ['#42a5f5', '#ffa726', '#66bb6a', '#f06292', '#ab47bc', '#26a69a', '#ffee58'];
const DEFAULT_FX = {
  pt: 'pop', text: 'fade', tex: 'fade', title: 'fade', node: 'pop', img: 'fade',
  box: 'grow', sphere: 'grow', cyl: 'grow', model: 'grow',
};
const DRAWABLE = new Set(['line', 'arrow', 'path', 'poly', 'rect', 'circle', 'angle', 'edge', 'axes', 'plot', 'surf']);

const V = (a) => new THREE.Vector3(a[0], a[1], a[2] ?? 0);
const isRef = (p) => p && !Array.isArray(p) && typeof p === 'object';
const DEG = Math.PI / 180;
const backOut = (p) => { const c = 1.70158; return 1 + (c + 1) * Math.pow(p - 1, 3) + c * Math.pow(p - 1, 2); };

// ───────────────────────────── Entity ─────────────────────────────

class Entity {
  constructor(rt, cmd, parentEnt) {
    this.rt = rt;
    this.id = cmd.id;
    this.op = cmd.op;
    this.cmd = cmd;
    this.parentEnt = parentEnt; // axes or group entity, or null
    this.axes = parentEnt?.axes && cmd.on ? parentEnt.axes : null;
    this.obj = new THREE.Group();
    this.obj.name = cmd.id;
    this.body = new THREE.Group();
    this.obj.add(this.body);
    (parentEnt ? parentEnt.obj : rt.stage).add(this.obj);
    this.mats = [];
    this.disposables = [];
    this.billboards = [];
    this.factor = { fill: 1, label: 1 };
    this.opacity = 1;
    this.targetOpacity = cmd.opacity ?? 1;
    this.p = 0; // entrance progress
    this.live = false;
    this.ends = null; // [start, end] in obj-local space
    this.spin = null;
    this.fx = cmd.fx ?? (DEFAULT_FX[cmd.op] || (DRAWABLE.has(cmd.op) ? 'draw' : 'fade'));
    if (this.fx === 'draw' && !DRAWABLE.has(cmd.op)) this.fx = 'fade';
  }

  get parentObj() { return this.obj.parent; }
  get in3D() { return !!(this.axes?.is3 || this.parentEnt?.axes?.is3); }

  track(mat, role) {
    mat.userData.base = mat.opacity ?? 1;
    mat.userData.role = role;
    mat.transparent = true;
    this.mats.push(mat);
    return mat;
  }
  own(d) { this.disposables.push(d); return d; }

  applyOpacity() {
    for (const m of this.mats) m.opacity = m.userData.base * this.opacity * (this.factor[m.userData.role] ?? 1);
    this.obj.visible = this.opacity > 0.002;
  }
  setOpacity(o) { this.opacity = o; this.applyOpacity(); }

  colorMats() { return this.mats.filter((m) => ['stroke', 'solid', 'text'].includes(m.userData.role)); }

  /** Entrance animation. */
  enter(eased, raw = eased) {
    this.p = eased;
    this.factor.label = Math.min(1, Math.max(0, (eased - 0.6) / 0.4));
    switch (this.fx) {
      case 'draw': this.render?.(eased); this.setOpacity(this.targetOpacity); break;
      case 'fade': this.render?.(1); this.setOpacity(eased * this.targetOpacity); break;
      case 'grow': this.render?.(1); this.obj.scale.setScalar(Math.max(eased, 1e-3)); this.setOpacity(this.targetOpacity); break;
      case 'pop': this.render?.(1); this.obj.scale.setScalar(Math.max(backOut(raw), 1e-3)); this.setOpacity(this.targetOpacity); break;
      default: this.render?.(1); this.factor.label = 1; this.setOpacity(this.targetOpacity);
    }
  }

  /** Point (IR) → parent space. */
  toParent(pt) {
    if (!isRef(pt)) return this.axes ? this.axes.map(V(pt)) : V(pt);
    const w = this.rt.refWorld(pt);
    return this.parentObj.worldToLocal(w);
  }
  /** Parent space → obj-local space. */
  parentToLocal(v) {
    this.obj.updateWorldMatrix(true, false);
    return this.obj.worldToLocal(this.parentObj.localToWorld(v.clone()));
  }

  addLabel(text, pos, align = 'center') {
    const tp = new TextPlane(text, { size: 0.36, color: FG, align });
    this.track(tp.material, 'label');
    this.own(tp);
    tp.mesh.position.add(pos);
    this.obj.add(tp.mesh);
    if (this.in3D) this.billboards.push(tp.mesh);
    this.label = tp;
    return tp;
  }

  dispose() {
    for (const d of this.disposables) d.dispose?.();
    this.obj.traverse((o) => {
      o.geometry?.dispose?.();
      const m = o.material;
      if (m) (Array.isArray(m) ? m : [m]).forEach((x) => { x.map?.dispose(); x.dispose(); });
    });
    this.obj.removeFromParent();
  }
}

// ───────────────────────────── Runtime ─────────────────────────────

export class Runtime {
  constructor({ stage, camera, avatar = null, voice = null, log = console.log }) {
    this.stage = stage;
    this.camera = camera;
    this.avatar = avatar;
    this.voice = voice;
    this.log = log;
    this.entities = new Map();
    this.timeline = new Timeline((e, t) => this.log('error', `line ${t.cmd?.line ?? '?'}: ${t.cmd?.op}: ${e.message}`));
    this.gltf = new GLTFLoader();
    this.texLoader = new THREE.TextureLoader();
    this.texLoader.setCrossOrigin('anonymous');
  }

  now() { return performance.now() / 1000; }

  exec(cmd, { instant = false } = {}) {
    if (cmd.op === 'diag') return this.log(cmd.level, `line ${cmd.line}: ${cmd.msg}`);
    if (cmd.op === 'replay') {
      this.reset();
      for (const c of cmd.cmds) this.exec(c, { instant: true });
      return;
    }
    const h = this.handlers[cmd.op];
    if (!h) return this.log('warning', `viewer: unsupported op '${cmd.op}'`);
    // Real speech: synthesize now so the timeline knows exactly how long it lasts.
    if (cmd.op === 'say' && !instant) this.voice?.prepare(cmd);
    const task = h.call(this, cmd);
    task.cmd = cmd;
    task.dur = cmd.dur ?? 0;
    task.par = cmd.par;
    task.ease = cmd.ease;
    if (instant) {
      if (cmd.op === 'say' || cmd.op === 'point' || cmd.op === 'wait') return;
      this.timeline.runNow(task);
    } else this.timeline.add(task, this.now());
  }

  reset() {
    this.timeline.reset();
    for (const id of [...this.entities.keys()]) this.remove(id);
    this.avatar?.point(null);
  }

  update(dt) {
    const now = this.now();
    this.timeline.tick(now);
    const camQ = this.camera.getWorldQuaternion(new THREE.Quaternion());
    const q = new THREE.Quaternion();
    for (const e of this.entities.values()) {
      if (e.live && e.p > 0) e.refresh?.();
      if (e.spin) {
        e.obj.rotation.x += e.spin.x * dt;
        e.obj.rotation.y += e.spin.y * dt;
        e.obj.rotation.z += e.spin.z * dt;
      }
      for (const m of e.billboards) {
        m.parent.getWorldQuaternion(q);
        m.quaternion.copy(q.invert().multiply(camQ));
      }
    }
  }

  get(id) {
    const e = this.entities.get(id);
    if (!e) throw new Error(`unknown name '${id}'`);
    return e;
  }

  remove(id) {
    const e = this.entities.get(id);
    if (!e) return;
    for (const c of [...this.entities.values()]) if (c.parentEnt === e) this.remove(c.id);
    this.entities.delete(id);
    e.dispose();
  }

  // ── geometry queries ──

  /** World position of an entity anchor. */
  anchorWorld(e, anchor = 'center') {
    if ((anchor === 'start' || anchor === 'end') && e.ends) {
      e.obj.updateWorldMatrix(true, false);
      return e.obj.localToWorld(e.ends[anchor === 'start' ? 0 : 1].clone());
    }
    if (anchor === 'start') anchor = 'left';
    if (anchor === 'end') anchor = 'right';
    e.obj.updateWorldMatrix(true, true);
    if (e.op === 'pt' && anchor === 'center') return e.obj.getWorldPosition(new THREE.Vector3());
    const box = new THREE.Box3().setFromObject(e.body);
    if (box.isEmpty()) return e.obj.getWorldPosition(new THREE.Vector3());
    const a = this.stage.worldToLocal(box.min.clone());
    const b = this.stage.worldToLocal(box.max.clone());
    const min = a.clone().min(b), max = a.clone().max(b);
    const c = min.clone().add(max).multiplyScalar(0.5);
    const p = {
      center: c, top: new THREE.Vector3(c.x, max.y, c.z), bottom: new THREE.Vector3(c.x, min.y, c.z),
      left: new THREE.Vector3(min.x, c.y, c.z), right: new THREE.Vector3(max.x, c.y, c.z),
      front: new THREE.Vector3(c.x, c.y, max.z), back: new THREE.Vector3(c.x, c.y, min.z),
    }[anchor] ?? c;
    return this.stage.localToWorld(p);
  }

  /** World position of a {ref, anchor, off} point. Offsets are in board units. */
  refWorld(r) {
    const w = this.anchorWorld(this.get(r.ref), r.anchor);
    const s = this.stage.worldToLocal(w).add(V(r.off ?? [0, 0, 0]));
    return this.stage.localToWorld(s);
  }

  /** IR point → world (board space for plain vectors). */
  pointWorld(pt) {
    return isRef(pt) ? this.refWorld(pt) : this.stage.localToWorld(V(pt));
  }

  /** Point on an entity's border in direction `dir` (stage space). */
  borderPoint(e, dir) {
    const c = this.stage.worldToLocal(this.anchorWorld(e, 'center'));
    const d = dir.clone().normalize();
    const k = e.obj.getWorldScale(new THREE.Vector3()).x / this.stage.getWorldScale(new THREE.Vector3()).x;
    let hw, hh, circ = false;
    if (e.shape) {
      ({ hw, hh } = e.shape);
      hw *= k; hh *= k;
      circ = e.shape.kind === 'circle';
    } else {
      const box = new THREE.Box3().setFromObject(e.body);
      const s = this.stage.getWorldScale(new THREE.Vector3()).x;
      const size = box.getSize(new THREE.Vector3()).divideScalar(s);
      hw = size.x / 2; hh = size.y / 2;
      circ = e.op === 'pt' || e.op === 'circle' || e.op === 'sphere';
    }
    if (circ) return c.add(d.multiplyScalar(Math.max(hw, hh)));
    const t = Math.min(Math.abs(d.x) > 1e-6 ? hw / Math.abs(d.x) : Infinity, Math.abs(d.y) > 1e-6 ? hh / Math.abs(d.y) : Infinity);
    return c.add(d.multiplyScalar(Number.isFinite(t) ? t : 0));
  }

  // ── creation ──

  create(cmd) {
    let e;
    return {
      begin: () => {
        this.remove(cmd.id);
        let parentEnt = null;
        if (cmd.on) parentEnt = this.get(cmd.on);
        e = new Entity(this, cmd, parentEnt);
        try {
          BUILD[cmd.op].call(this, e, cmd);
        } catch (err) {
          e.dispose();
          throw err;
        }
        this.entities.set(cmd.id, e);
        e.enter(0, 0);
      },
      update: (eased, raw) => e.enter(eased, raw),
      finish: () => e.enter(1, 1),
    };
  }

  /** Tween helper for change ops. */
  change(cmd, setup) {
    let u;
    return {
      begin: () => { u = setup(this.get(cmd.target)); },
      update: (p, raw) => u?.(p, raw),
    };
  }
}

// ───────────────────────────── handlers ─────────────────────────────

const H = {};
Runtime.prototype.handlers = H;

H.wait = () => ({});
H.say = function (c) {
  return { begin: () => { this.avatar?.say(c.text, c.dur); this.voice?.play(c); } };
};
H.point = function (c) { return { begin: () => this.avatar?.point(c.at ? this.pointWorld(c.at) : null, c.dur) }; };
H.title = function (c) { return this.create({ ...c, id: '__title', op: 'title' }); };

function fadeOutAndRemove(rt, pick) {
  let list = [], o0 = [];
  return {
    begin: () => { list = pick(); o0 = list.map((e) => e.opacity); },
    update: (p) => list.forEach((e, i) => e.setOpacity(o0[i] * (1 - p))),
    finish: () => list.forEach((e) => { if (rt.entities.get(e.id) === e) rt.remove(e.id); }),
  };
}
H.clear = function () {
  return fadeOutAndRemove(this, () => { this.avatar?.point(null); return [...this.entities.values()]; });
};
H.del = function (c) { return fadeOutAndRemove(this, () => c.ids.map((id) => this.get(id))); };

H.group = function (c) {
  return {
    begin: () => {
      const members = c.ids.map((id) => this.get(id));
      this.remove(c.id);
      const g = new Entity(this, { id: c.id, op: 'group' }, null);
      const box = new THREE.Box3();
      for (const m of members) box.expandByObject(m.obj);
      if (!box.isEmpty()) g.obj.position.copy(this.stage.worldToLocal(box.getCenter(new THREE.Vector3())));
      g.obj.updateWorldMatrix(true, false);
      for (const m of members) { g.obj.attach(m.obj); m.parentEnt = g; }
      g.p = 1;
      this.entities.set(c.id, g);
    },
  };
};

H.move = function (c) {
  return this.change(c, (e) => {
    const dest = e.toParent(c.to);
    const center = e.parentObj.worldToLocal(this.anchorWorld(e, 'center'));
    const from = e.obj.position.clone();
    const to = from.clone().add(dest.sub(center));
    return (p) => e.obj.position.lerpVectors(from, to, p);
  });
};
H.rot = function (c) {
  return this.change(c, (e) => {
    e.spin = null;
    const from = e.obj.quaternion.clone();
    const to = new THREE.Quaternion().setFromEuler(new THREE.Euler(c.rot[0] * DEG, c.rot[1] * DEG, c.rot[2] * DEG));
    return (p) => e.obj.quaternion.slerpQuaternions(from, to, p);
  });
};
H.scale = function (c) {
  return this.change(c, (e) => {
    const from = e.obj.scale.x;
    return (p) => e.obj.scale.setScalar(Math.max(1e-3, from + (c.scale - from) * p));
  });
};
H.fade = function (c) {
  return this.change(c, (e) => {
    const from = e.opacity;
    return (p) => e.setOpacity(from + (c.opacity - from) * p);
  });
};
function colorTween(e, target) {
  const mats = e.colorMats();
  const from = mats.map((m) => m.color.clone());
  const to = new THREE.Color(target);
  return (p) => mats.forEach((m, i) => m.color.lerpColors(from[i], to, p));
}
H.color = function (c) { return this.change(c, (e) => colorTween(e, c.color)); };
H.set = function (c) {
  return this.change(c, (e) => {
    const parts = [];
    if (c.text !== undefined) e.setText?.(c.text);
    if (c.pos) parts.push(H.move.call(this, { target: c.target, to: c.pos }));
    if (c.rot) parts.push(H.rot.call(this, { target: c.target, rot: c.rot }));
    if (c.scale !== undefined) parts.push(H.scale.call(this, c));
    if (c.opacity !== undefined) parts.push(H.fade.call(this, c));
    if (c.color) parts.push(H.color.call(this, c));
    parts.forEach((t) => t.begin());
    return (p, raw) => parts.forEach((t) => t.update(p, raw));
  });
};
H.hl = function (c) {
  let us = [];
  return {
    begin: () => {
      us = c.ids.map((id) => {
        const e = this.get(id);
        const mats = e.colorMats();
        const from = mats.map((m) => m.color.clone());
        const hl = new THREE.Color(c.color ?? '#ffe066');
        const s0 = e.obj.scale.x;
        return (raw) => {
          const k = Math.sin(Math.PI * raw);
          mats.forEach((m, i) => m.color.lerpColors(from[i], hl, k * 0.85));
          e.obj.scale.setScalar(s0 * (1 + 0.12 * k));
        };
      });
    },
    update: (_, raw) => us.forEach((u) => u(raw)),
  };
};
H.spin = function (c) {
  return this.change(c, (e) => {
    const r = V(c.rate).multiplyScalar(DEG);
    e.spin = r.lengthSq() > 0 ? r : null;
    return () => {};
  });
};

for (const op of ['pt', 'line', 'arrow', 'path', 'poly', 'rect', 'circle', 'angle', 'text', 'tex', 'node', 'edge', 'img',
  'axes', 'plot', 'surf', 'box', 'sphere', 'cyl', 'model'])
  H[op] = function (c) { return this.create(c); };

// ───────────────────────────── builders ─────────────────────────────

const BUILD = {};

/** Shared machinery for anything made of strokes (+ optional heads/fill). */
function strokeEntity(e, spec, { width = 0.06, color = FG, dash = false, fillColor = null, fillOpacity = 0.2, heads = {} } = {}) {
  const stroke = e.own(new D.Stroke({ color, width, dash }));
  e.track(stroke.material, 'stroke');
  e.body.add(stroke.obj);
  const headSize = Math.max(0.2, width * 4.2);
  const mkHead = () => {
    const h = e.own(new D.Head(color, headSize));
    e.track(h.material, 'stroke');
    e.body.add(h.obj);
    return h;
  };
  const endHead = heads.end ? mkHead() : null;
  const startHead = heads.start ? mkHead() : null;
  let fill = null, fillMat = null;
  if (fillColor) fillMat = e.track(new THREE.MeshBasicMaterial({ color: fillColor, opacity: fillOpacity, depthWrite: false, side: THREE.DoubleSide }), 'fill');

  // Initial resolve in parent space → pivot at the centre.
  let s = spec();
  const all = s.polys.flat().concat(s.fill ?? []);
  if (!s.fixedOrigin && all.length) e.obj.position.copy(new THREE.Box3().setFromPoints(all).getCenter(new THREE.Vector3()));
  e.live = !!s.live;

  let local, key = '';
  const compute = () => {
    const toL = (v) => e.parentToLocal(v);
    local = {
      polys: s.polys.map((pl) => pl.map(toL)),
      fill: s.fill ? s.fill.map(toL) : null,
      ends: s.ends ? s.ends.map(toL) : null,
      label: s.label ? toL(s.label) : null,
    };
    e.ends = local.ends;
    if (fillMat) {
      if (fill) { e.body.remove(fill); fill.geometry.dispose(); }
      fill = null;
      if (local.fill && local.fill.length >= 3) {
        fill = D.fillMesh(local.fill, fillColor, 1);
        fill.material.dispose();
        fill.material = fillMat;
        e.body.add(fill);
      }
    }
    if (e.label && local.label) e.label.mesh.position.copy(local.label).add(e.labelShift ?? new THREE.Vector3());
  };
  compute();

  e.render = (p) => {
    let polys = D.partial(local.polys, p);
    if (endHead) {
      const tip = D.tipOf(polys);
      endHead.place(p > 0 ? tip : null);
      if (tip) polys = polys.map((pl, i) => (i === polys.length - 1 ? D.trimEnd(pl, headSize * 0.8) : pl));
    }
    if (startHead) {
      const first = polys[0];
      const tip = first && first.length >= 2 ? D.tipOf([[first[1], first[0]]]) : null;
      startHead.place(p > 0 && tip ? { pos: first[0], dir: tip.dir } : null);
      if (tip) polys = [D.trimStart(first, headSize * 0.8), ...polys.slice(1)];
    }
    stroke.set(polys);
    e.factor.fill = p;
    e.applyOpacity();
  };

  e.refresh = () => {
    let next;
    try { next = spec(); } catch { return; } // a referenced entity vanished mid-fade
    const k = next.polys.flat().map((v) => `${v.x.toFixed(4)},${v.y.toFixed(4)},${v.z.toFixed(4)}`).join(';');
    if (k === key) return;
    key = k;
    s = next;
    compute();
    e.render(e.p);
  };
  return local;
}

function labelFor(e, cmd, pos, align = 'center') {
  if (cmd.label !== undefined) e.addLabel(cmd.label, pos, align);
}

BUILD.pt = function (e, c) {
  const r = c.size ?? 0.13;
  const mat = e.track(new THREE.MeshBasicMaterial({ color: c.color ?? FG }), 'stroke');
  const m = new THREE.Mesh(new THREE.SphereGeometry(r, 24, 16), mat);
  m.renderOrder = 2;
  e.body.add(m);
  e.obj.position.copy(e.toParent(c.at));
  labelFor(e, c, new THREE.Vector3(r + 0.08, r + 0.12, 0), 'left');
};

function lineLabel(e, c, a, b) {
  if (c.label === undefined) return null;
  const mid = a.clone().add(b).multiplyScalar(0.5);
  const d = b.clone().sub(a);
  const n = new THREE.Vector3(-d.y, d.x, 0).normalize().multiplyScalar(0.32);
  if (n.y < 0 || (Math.abs(n.y) < 1e-6 && n.x > 0)) n.negate(); // prefer above/left
  e.addLabel(c.label, new THREE.Vector3());
  e.labelShift = n;
  return mid;
}

BUILD.line = BUILD.arrow = function (e, c) {
  const spec = () => {
    const a = e.toParent(c.from), b = e.toParent(c.to);
    return { polys: [[a, b]], ends: [a, b], label: a.clone().add(b).multiplyScalar(0.5), live: isRef(c.from) || isRef(c.to) };
  };
  const s0 = spec();
  lineLabel(e, c, s0.ends[0], s0.ends[1]);
  strokeEntity(e, spec, {
    width: c.width ?? 0.06, color: c.color ?? FG, dash: c.dash,
    heads: c.op === 'arrow' ? { end: true, start: c.head === 'both' } : {},
  });
};

BUILD.path = function (e, c) {
  const spec = () => {
    let pts = c.pts.map((p) => e.toParent(p));
    if (c.smooth && pts.length > 2) {
      const curve = new THREE.CatmullRomCurve3(pts, !!c.closed, 'centripetal');
      pts = curve.getPoints(48 * pts.length);
      if (c.closed) pts.push(pts[0].clone());
    } else if (c.closed) pts.push(pts[0].clone());
    return { polys: [pts], ends: [pts[0], pts[pts.length - 1]], label: pts[0], live: c.pts.some(isRef) };
  };
  if (c.label !== undefined) { e.addLabel(c.label, new THREE.Vector3()); e.labelShift = new THREE.Vector3(0, 0.35, 0); }
  strokeEntity(e, spec, { width: c.width ?? 0.06, color: c.color ?? FG, dash: c.dash });
};

BUILD.poly = function (e, c) {
  const spec = () => {
    const pts = c.pts.map((p) => e.toParent(p));
    const center = new THREE.Box3().setFromPoints(pts).getCenter(new THREE.Vector3());
    return { polys: [[...pts, pts[0]]], fill: pts, label: center, live: c.pts.some(isRef) };
  };
  if (c.label !== undefined) e.addLabel(c.label, new THREE.Vector3());
  const color = c.color ?? FG;
  strokeEntity(e, spec, { width: c.width ?? 0.05, color, fillColor: c.fill ?? color, fillOpacity: c.fill ? 0.55 : 0.18 });
};

BUILD.rect = function (e, c) {
  const [w, h] = c.size;
  e.obj.position.copy(e.toParent(c.at));
  const pts = D.roundedRectPoints(w, h, c.radius ?? 0);
  const spec = () => ({ polys: [pts.map((p) => p.clone().add(e.obj.position))], fill: c.fill ? pts.map((p) => p.clone().add(e.obj.position)) : null, fixedOrigin: true });
  strokeEntity(e, spec, { width: c.width ?? 0.05, color: c.color ?? FG, fillColor: c.fill ?? null, fillOpacity: 0.55 });
  labelFor(e, c, new THREE.Vector3(0, h / 2 + 0.3, 0));
};

BUILD.circle = function (e, c) {
  e.obj.position.copy(e.toParent(c.at));
  const pts = D.circlePoints(c.r).map((p) => new THREE.Vector3(p.y, p.x, 0)); // start at the top, clockwise
  const spec = () => ({ polys: [pts.map((p) => p.clone().add(e.obj.position))], fill: c.fill ? pts.map((p) => p.clone().add(e.obj.position)) : null, fixedOrigin: true });
  strokeEntity(e, spec, { width: c.width ?? 0.05, color: c.color ?? FG, fillColor: c.fill ?? null, fillOpacity: 0.55 });
  labelFor(e, c, new THREE.Vector3(0, c.r + 0.3, 0));
};

BUILD.angle = function (e, c) {
  const r = c.radius ?? 0.6;
  const spec = () => {
    const A = e.toParent(c.a), O = e.toParent(c.vertex), B = e.toParent(c.b);
    const u = A.clone().sub(O).normalize(), w = B.clone().sub(O).normalize();
    let arc;
    if (c.right) {
      const s = r * 0.6;
      arc = [O.clone().addScaledVector(u, s), O.clone().addScaledVector(u, s).addScaledVector(w, s), O.clone().addScaledVector(w, s)];
    } else {
      const th = Math.acos(Math.max(-1, Math.min(1, u.dot(w))));
      const n = 32;
      arc = [];
      for (let i = 0; i <= n; i++) {
        const t = i / n;
        const k1 = th > 1e-6 ? Math.sin((1 - t) * th) / Math.sin(th) : 1 - t;
        const k2 = th > 1e-6 ? Math.sin(t * th) / Math.sin(th) : t;
        arc.push(O.clone().addScaledVector(u, k1 * r).addScaledVector(w, k2 * r));
      }
    }
    const bis = u.clone().add(w).normalize();
    return { polys: [arc], fill: [O, ...arc], label: O.clone().addScaledVector(bis, r + 0.35), live: [c.a, c.vertex, c.b].some(isRef) };
  };
  if (c.label !== undefined) e.addLabel(c.label, new THREE.Vector3());
  const color = c.color ?? '#ffcc66';
  strokeEntity(e, spec, { width: c.width ?? 0.045, color, fillColor: color, fillOpacity: c.right ? 0 : 0.18 });
};

function textEntity(e, c, { size = 0.5, weight = 600, tex = false } = {}) {
  const tp = new TextPlane(c.text, { size: c.size ?? size, color: c.color ?? FG, align: c.align ?? 'center', weight });
  e.own(tp);
  e.track(tp.material, 'text');
  e.body.add(tp.mesh);
  e.obj.position.copy(e.toParent(c.at));
  if (e.in3D) e.billboards.push(tp.mesh);
  const setTex = (t) => tp.setTex(t).catch((err) => e.rt.log('warning', `line ${c.line}: tex: ${err.message} (showing plain text)`));
  if (tex) setTex(c.text);
  e.setText = (t) => (tex ? (tp.setText(t), setTex(t)) : tp.setText(t));
  labelFor(e, c, new THREE.Vector3(0, -(c.size ?? size) * 0.9, 0));
}
BUILD.text = function (e, c) { textEntity(e, c); };
BUILD.tex = function (e, c) { textEntity(e, c, { tex: true }); };
BUILD.title = function (e, c) { textEntity(e, { ...c, at: [0, 3.85, 0], size: 0.62 }, { weight: 700 }); };

BUILD.node = function (e, c) {
  e.obj.position.copy(e.toParent(c.at));
  const size = c.size ?? 0.45;
  const tp = e.own(new TextPlane(c.text, { size, color: c.color ?? FG }));
  e.track(tp.material, 'text');
  tp.mesh.position.z = 0.01;
  let parts = [];
  const strokeColor = c.color ?? '#7fa7d9';
  const fillMat = e.track(new THREE.MeshBasicMaterial({ color: c.fill ?? '#1b2635', opacity: 0.95, depthWrite: false, side: THREE.DoubleSide }), 'fill');
  const stroke = e.own(new D.Stroke({ color: strokeColor, width: 0.05 }));
  e.track(stroke.material, 'stroke');
  const build = () => {
    parts.forEach((p) => { e.body.remove(p); if (p.geometry) p.geometry.dispose(); });
    const w = tp.width, h = tp.height;
    const kind = c.shape ?? (String(tp.text).length <= 3 ? 'circle' : 'box');
    let outline;
    if (kind === 'circle') {
      const r = Math.max(w, h) / 2 + size * 0.35;
      outline = D.circlePoints(r, 64);
      e.shape = { kind, hw: r, hh: r };
    } else {
      const bw = w + size * 0.7, bh = h + size * 0.5;
      outline = D.roundedRectPoints(bw, bh, kind === 'pill' ? bh / 2 : kind === 'box' ? size * 0.25 : 0);
      e.shape = { kind, hw: bw / 2, hh: bh / 2 };
    }
    parts = [tp.mesh];
    if (kind !== 'none') {
      const fill = D.fillMesh(outline, '#000', 1);
      fill.material.dispose();
      fill.material = fillMat;
      stroke.set([outline]);
      parts.push(fill, stroke.obj);
    }
    parts.forEach((p) => e.body.add(p));
  };
  build();
  e.setText = (t) => { tp.setText(t); build(); };
};

BUILD.edge = function (e, c) {
  const A = e.rt.get(c.from), B = e.rt.get(c.to);
  const stage = e.rt.stage;
  const spec = () => {
    const ca = stage.worldToLocal(e.rt.anchorWorld(A)), cb = stage.worldToLocal(e.rt.anchorWorld(B));
    const d = cb.clone().sub(ca);
    const bend = c.bend ?? 0;
    const n = new THREE.Vector3(-d.y, d.x, 0).normalize();
    // Leave each node toward the curve's control point so bent edges look right.
    const ctrl = ca.clone().add(cb).multiplyScalar(0.5).addScaledVector(n, bend * d.length() * 0.5);
    const pa = e.rt.borderPoint(A, ctrl.clone().sub(ca)), pb = e.rt.borderPoint(B, ctrl.clone().sub(cb));
    const pts = bend ? new THREE.QuadraticBezierCurve3(pa, ctrl, pb).getPoints(40) : [pa, pb];
    const toP = (v) => e.parentObj.worldToLocal(stage.localToWorld(v.clone()));
    const P = pts.map(toP);
    const mid = toP(bend ? new THREE.QuadraticBezierCurve3(pa, ctrl, pb).getPoint(0.5) : pa.clone().add(pb).multiplyScalar(0.5));
    return { polys: [P], ends: [P[0], P[P.length - 1]], label: mid, live: true };
  };
  if (c.label !== undefined) {
    const s0 = spec();
    lineLabel(e, c, s0.ends[0], s0.ends[1]);
  }
  const dir = c.dir ?? 'fwd';
  strokeEntity(e, spec, {
    width: c.width ?? 0.05, color: c.color ?? '#9fb4cc', dash: c.dash,
    heads: { end: dir === 'fwd' || dir === 'both', start: dir === 'back' || dir === 'both' },
  });
};

BUILD.img = function (e, c) {
  e.obj.position.copy(e.toParent(c.at));
  const w = c.size ?? 4;
  const mat = e.track(new THREE.MeshBasicMaterial({ color: '#ffffff', side: THREE.DoubleSide }), 'solid');
  const mesh = new THREE.Mesh(new THREE.PlaneGeometry(1, 1), mat);
  mesh.scale.set(w, w * 0.6, 1);
  e.body.add(mesh);
  e.rt.texLoader.load(c.url, (tex) => {
    tex.colorSpace = THREE.SRGBColorSpace;
    mat.map = tex;
    mat.needsUpdate = true;
    mesh.scale.set(w, (w * tex.image.height) / tex.image.width, 1);
  }, undefined, () => e.rt.log('warning', `line ${c.line}: img: could not load ${c.url}`));
};

BUILD.model = function (e, c) {
  e.obj.position.copy(e.toParent(c.at));
  e.rt.gltf.load(c.url, (g) => {
    const root = g.scene;
    const box = new THREE.Box3().setFromObject(root);
    const size = box.getSize(new THREE.Vector3());
    const k = (c.size ?? 3) / Math.max(size.x, size.y, size.z, 1e-6);
    root.scale.setScalar(k);
    root.position.copy(box.getCenter(new THREE.Vector3()).multiplyScalar(-k));
    root.traverse((o) => { if (o.material) e.track(o.material, 'solid'); });
    e.body.add(root);
    e.applyOpacity();
  }, undefined, () => e.rt.log('warning', `line ${c.line}: model: could not load ${c.url}`));
};

function solid(e, c, geo) {
  const mat = e.track(new THREE.MeshStandardMaterial({ color: c.color ?? PALETTE[0], roughness: 0.45, metalness: 0.05, wireframe: !!c.wire }), 'solid');
  const m = new THREE.Mesh(geo, mat);
  e.body.add(m);
  return m;
}
BUILD.box = function (e, c) {
  e.obj.position.copy(e.toParent(c.at));
  solid(e, c, new THREE.BoxGeometry(c.size[0], c.size[1], c.size[2] || Math.min(c.size[0], c.size[1])));
  labelFor(e, c, new THREE.Vector3(0, c.size[1] / 2 + 0.3, 0));
};
BUILD.sphere = function (e, c) {
  e.obj.position.copy(e.toParent(c.at));
  solid(e, c, new THREE.SphereGeometry(c.r, 48, 32));
  labelFor(e, c, new THREE.Vector3(0, c.r + 0.3, 0));
};
BUILD.cyl = function (e, c) {
  const a = e.toParent(c.from), b = e.toParent(c.to);
  e.obj.position.copy(a.clone().add(b).multiplyScalar(0.5));
  const d = b.clone().sub(a);
  const m = solid(e, c, new THREE.CylinderGeometry(c.r, c.r, d.length(), 32));
  m.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), d.clone().normalize());
  e.ends = [a.clone().sub(e.obj.position), b.clone().sub(e.obj.position)];
  labelFor(e, c, new THREE.Vector3(0, c.r + 0.3, 0));
};

// ── axes, plot, surf ──

BUILD.axes = function (e, c) {
  const is3 = !!c.z;
  const xr = c.x ?? [-5, 5], yr = c.y ?? (is3 ? [-5, 5] : [-3, 3]), zr = c.z ?? [0, 0];
  const size = c.size ?? (is3 ? [5, 5, 5] : [8, 5, 0]);
  const W = size[0], Hh = size[1], Dd = size[2] || size[0];
  const sx = W / (xr[1] - xr[0]), sy = Hh / (yr[1] - yr[0]), sz = is3 ? Dd / (zr[1] - zr[0]) : 1;
  const mx = (xr[0] + xr[1]) / 2, my = (yr[0] + yr[1]) / 2, mz = (zr[0] + zr[1]) / 2;
  const map = is3
    ? (v) => new THREE.Vector3((v.x - mx) * sx, (v.z - mz) * sz, -(v.y - my) * sy)
    : (v) => new THREE.Vector3((v.x - mx) * sx, (v.y - my) * sy, v.z * Math.min(sx, sy));
  e.obj.position.copy(e.toParent(c.at)); // before e.axes is set: `at` is in board space
  e.axes = { is3, x: xr, y: yr, z: zr, map, plots: 0 };
  if (is3) e.obj.rotation.set(0.35, -0.55, 0);

  const clamp = (v, r) => Math.min(Math.max(v, r[0]), r[1]);
  const o = { x: clamp(0, xr), y: clamp(0, yr), z: clamp(0, zr) };
  const P = (x, y, z = o.z) => map(new THREE.Vector3(x, y, z));
  const color = c.color ?? '#8a98ab';
  const axisPolys = [[P(xr[0], o.y), P(xr[1], o.y)], [P(o.x, yr[0]), P(o.x, yr[1])]];
  if (is3) axisPolys.push([P(o.x, o.y, zr[0]), P(o.x, o.y, zr[1])]);

  // Axis lines (drawn) with heads at the positive ends.
  const strokes = axisPolys.map(() => {
    const s = e.own(new D.Stroke({ color, width: 0.045 }));
    e.track(s.material, 'stroke');
    e.body.add(s.obj);
    const h = e.own(new D.Head(color, 0.22));
    e.track(h.material, 'stroke');
    e.body.add(h.obj);
    return { s, h };
  });

  // Ticks, grid and labels (fade in with the 'label' factor).
  const deco = new THREE.Group();
  e.obj.add(deco);
  const tickStroke = e.own(new D.Stroke({ color, width: 0.03 }));
  e.track(tickStroke.material, 'label');
  deco.add(tickStroke.obj);
  const gridStroke = e.own(new D.Stroke({ color: FG, width: 0.018 }));
  gridStroke.material.opacity = 0.12;
  e.track(gridStroke.material, 'label');
  deco.add(gridStroke.obj);
  const ticks = [], grid = [];
  const tl = 0.1;
  const label = (text, pos, align = 'center', sizeL = 0.26, col = '#aab6c5') => {
    const tp = e.own(new TextPlane(text, { size: sizeL, color: col, align }));
    e.track(tp.material, 'label');
    tp.mesh.position.add(pos);
    deco.add(tp.mesh);
    if (is3) e.billboards.push(tp.mesh);
  };
  const showTicks = c.ticks ?? true, showGrid = c.grid ?? true;
  const each = (r, f) => {
    const st = D.niceStep(r[1] - r[0]);
    for (let v = Math.ceil(r[0] / st - 1e-9) * st; v <= r[1] + 1e-9; v += st) f(v);
  };
  each(xr, (v) => {
    if (showGrid) grid.push(is3 ? [P(v, yr[0]), P(v, yr[1])] : [P(v, yr[0]), P(v, yr[1])]);
    if (!showTicks || Math.abs(v - o.x) < 1e-9) return;
    const p = P(v, o.y);
    ticks.push([p.clone().add(new THREE.Vector3(0, -tl, 0)), p.clone().add(new THREE.Vector3(0, tl, 0))]);
    label(D.fmtTick(v), p.clone().add(new THREE.Vector3(0, -0.32, 0)));
  });
  each(yr, (v) => {
    if (showGrid) grid.push([P(xr[0], v), P(xr[1], v)]);
    if (!showTicks || Math.abs(v - o.y) < 1e-9) return;
    const p = P(o.x, v);
    ticks.push(is3 ? [p.clone().add(new THREE.Vector3(-tl, 0, 0)), p.clone().add(new THREE.Vector3(tl, 0, 0))]
      : [p.clone().add(new THREE.Vector3(-tl, 0, 0)), p.clone().add(new THREE.Vector3(tl, 0, 0))]);
    label(D.fmtTick(v), p.clone().add(new THREE.Vector3(-0.2, 0, 0)), 'right');
  });
  if (is3) each(zr, (v) => {
    if (!showTicks || Math.abs(v - o.z) < 1e-9) return;
    const p = P(o.x, o.y, v);
    ticks.push([p.clone().add(new THREE.Vector3(-tl, 0, 0)), p.clone().add(new THREE.Vector3(tl, 0, 0))]);
    label(D.fmtTick(v), p.clone().add(new THREE.Vector3(-0.2, 0, 0)), 'right');
  });
  tickStroke.set(ticks);
  gridStroke.set(grid);
  const lx = c.xlabel ?? 'x', ly = c.ylabel ?? 'y', lz = c.zlabel ?? 'z';
  label(lx, P(xr[1], o.y).add(new THREE.Vector3(0.35, 0, 0)), 'left', 0.34, FG);
  label(ly, P(o.x, yr[1]).add(is3 ? new THREE.Vector3(0, 0, -0.35) : new THREE.Vector3(0, 0.35, 0)), 'center', 0.34, FG);
  if (is3) label(lz, P(o.x, o.y, zr[1]).add(new THREE.Vector3(0, 0.35, 0)), 'center', 0.34, FG);

  e.render = (p) => {
    axisPolys.forEach((pl, i) => {
      const part = D.partial([pl], p);
      const { s, h } = strokes[i];
      const tip = D.tipOf(part);
      h.place(p > 0 ? tip : null);
      s.set(tip ? [D.trimEnd(part[0], 0.18)] : part);
    });
  };
};

function axesOf(e, c) {
  const ax = e.rt.get(c.axes);
  if (!ax.axes) throw new Error(`'${c.axes}' is not axes`);
  // Plots always live in their axes' space.
  if (e.parentEnt !== ax) { ax.obj.add(e.obj); e.parentEnt = ax; }
  e.axes = ax.axes;
  return ax.axes;
}

BUILD.plot = function (e, c) {
  const A = axesOf(e, c);
  const fns = c.exprs.map((s) => {
    try { return compile(s, ['x', 't']); } catch (err) { throw new Error(`"${s}": ${err.message}`); }
  });
  const color = c.color ?? PALETTE[A.plots++ % PALETTE.length];
  const polys = [];
  let cur = [];
  const push = (v) => cur.push(A.map(v));
  const brk = () => { if (cur.length > 1) polys.push(cur); cur = []; };
  const yr = A.is3 ? null : A.y, yspan = A.is3 ? 0 : yr[1] - yr[0];
  if (fns.length === 1) {
    const [x0, x1] = c.x ?? A.x;
    const N = 600;
    let prev = null;
    for (let i = 0; i <= N; i++) {
      const x = x0 + ((x1 - x0) * i) / N;
      const y = fns[0]({ x, t: x });
      const ok = Number.isFinite(y) && (A.is3 || (y >= yr[0] - 0.05 * yspan && y <= yr[1] + 0.05 * yspan));
      if (!ok || (prev !== null && !A.is3 && Math.abs(y - prev) > yspan)) brk();
      if (ok) push(new THREE.Vector3(x, y, 0));
      prev = ok ? y : null;
    }
  } else {
    const [t0, t1] = c.t ?? [0, 2 * Math.PI];
    const N = 800;
    for (let i = 0; i <= N; i++) {
      const t = t0 + ((t1 - t0) * i) / N;
      const v = fns.map((f) => f({ t, x: t }));
      if (!v.every(Number.isFinite)) { brk(); continue; }
      // On 3D axes: x(t), y(t), z(t). On 2D: x(t), y(t).
      push(new THREE.Vector3(v[0], v[1], v[2] ?? 0));
    }
  }
  brk();
  if (!polys.length) throw new Error('nothing to draw in this range');
  strokeEntity(e, () => ({ polys, ends: [polys[0][0], polys.at(-1).at(-1)], label: polys.at(-1).at(-1), fixedOrigin: true }),
    { width: c.width ?? 0.06, color });
  if (c.label !== undefined) { e.addLabel(c.label, new THREE.Vector3(), 'left'); e.label.mesh.position.copy(e.ends[1]).add(new THREE.Vector3(0.2, 0.2, 0)); }
};

BUILD.surf = function (e, c) {
  const A = axesOf(e, c);
  if (!A.is3) throw new Error('surf needs 3D axes (give the axes a z=range)');
  let f;
  try { f = compile(c.expr, ['x', 'y']); } catch (err) { throw new Error(`"${c.expr}": ${err.message}`); }
  const [x0, x1] = c.x ?? A.x, [y0, y1] = c.y ?? A.y;
  const n = Math.max(4, Math.min(160, Math.round(c.res ?? 56)));
  const zs = new Float32Array((n + 1) * (n + 1));
  const [zlo, zhi] = A.z;
  for (let j = 0; j <= n; j++) for (let i = 0; i <= n; i++) {
    const z = f({ x: x0 + ((x1 - x0) * i) / n, y: y0 + ((y1 - y0) * j) / n });
    zs[j * (n + 1) + i] = Number.isFinite(z) ? Math.min(zhi, Math.max(zlo, z)) : 0;
  }
  const geo = new THREE.PlaneGeometry(1, 1, n, n);
  const pos = geo.attributes.position;
  const colors = new Float32Array(pos.count * 3);
  const stops = [new THREE.Color('#3949ab'), new THREE.Color('#26a69a'), new THREE.Color('#ffee58')];
  const tmp = new THREE.Color();
  const z0 = Math.min(zhi, Math.max(zlo, 0));
  geo.setAttribute('color', new THREE.BufferAttribute(colors, 3));
  const mat = e.track(new THREE.MeshStandardMaterial({ vertexColors: true, side: THREE.DoubleSide, roughness: 0.6, opacity: 0.92 }), 'fill');
  const mesh = new THREE.Mesh(geo, mat);
  e.body.add(mesh);
  if (c.wire) {
    const wm = e.track(new THREE.MeshBasicMaterial({ color: '#0b0f16', wireframe: true, opacity: 0.35 }), 'stroke');
    e.body.add(new THREE.Mesh(geo, wm));
  }
  // PlaneGeometry vertex order: rows from top (v=1) to bottom; columns left→right.
  e.render = (p) => {
    for (let j = 0; j <= n; j++) for (let i = 0; i <= n; i++) {
      const k = j * (n + 1) + i;
      const x = x0 + ((x1 - x0) * i) / n, y = y1 - ((y1 - y0) * j) / n;
      const zz = zs[(n - j) * (n + 1) + i];
      const v = A.map(new THREE.Vector3(x, y, z0 + (zz - z0) * p));
      pos.setXYZ(k, v.x, v.y, v.z);
    }
    pos.needsUpdate = true;
    geo.computeVertexNormals();
    geo.computeBoundingBox();
    geo.computeBoundingSphere();
    e.factor.fill = 1;
  };
  // Colours were computed in zs order; remap to vertex order.
  for (let j = 0; j <= n; j++) for (let i = 0; i <= n; i++) {
    const k = j * (n + 1) + i, src = (n - j) * (n + 1) + i;
    const t = (zs[src] - zlo) / (zhi - zlo || 1);
    if (t < 0.5) tmp.lerpColors(stops[0], stops[1], t * 2); else tmp.lerpColors(stops[1], stops[2], (t - 0.5) * 2);
    if (c.fill) tmp.set(c.fill);
    colors.set([tmp.r, tmp.g, tmp.b], k * 3);
  }
};
