// Stroke/fill/arrowhead building blocks shared by entity types.
import * as THREE from 'three';
import { LineSegments2 } from 'three/addons/lines/LineSegments2.js';
import { LineSegmentsGeometry } from 'three/addons/lines/LineSegmentsGeometry.js';
import { LineMaterial } from 'three/addons/lines/LineMaterial.js';

/** Board units → metres. */
export const STAGE_SCALE = 0.15;

const lineMaterials = new Set();
export function setLineResolution(w, h) {
  for (const m of lineMaterials) m.resolution.set(w, h);
}

/** Thick polyline(s) with world-space width. Width is in board units. */
export class Stroke {
  constructor({ color = '#e8edf3', width = 0.06, dash = false } = {}) {
    this.dash = dash;
    this.material = new LineMaterial({
      color, linewidth: width * STAGE_SCALE, worldUnits: true, transparent: true, depthWrite: false,
      dashed: dash, dashSize: 0.22, gapSize: 0.14,
    });
    this.material.resolution.set(window.innerWidth, window.innerHeight);
    lineMaterials.add(this.material);
    this.obj = new LineSegments2(new LineSegmentsGeometry(), this.material);
    this.obj.renderOrder = 2;
    this.obj.frustumCulled = false;
  }

  /** polylines: array of arrays of Vector3. */
  set(polylines) {
    const arr = [];
    for (const pl of polylines)
      for (let i = 0; i + 1 < pl.length; i++) {
        const a = pl[i], b = pl[i + 1];
        arr.push(a.x, a.y, a.z, b.x, b.y, b.z);
      }
    // New geometry each time: LineSegmentsGeometry doesn't resize cleanly.
    this.obj.geometry.dispose();
    const g = new LineSegmentsGeometry();
    if (arr.length) g.setPositions(arr);
    this.obj.geometry = g;
    this.obj.visible = arr.length > 0;
    if (this.dash && arr.length) this.obj.computeLineDistances();
  }

  dispose() {
    lineMaterials.delete(this.material);
    this.obj.geometry.dispose();
    this.material.dispose();
  }
}

export function polylineLength(pl) {
  let L = 0;
  for (let i = 0; i + 1 < pl.length; i++) L += pl[i].distanceTo(pl[i + 1]);
  return L;
}

/** First fraction p (by arc length) of a list of polylines. */
export function partial(polylines, p) {
  if (p >= 1) return polylines;
  const total = polylines.reduce((s, pl) => s + polylineLength(pl), 0);
  let left = total * Math.max(0, p);
  const out = [];
  for (const pl of polylines) {
    if (left <= 0) break;
    const cur = [pl[0]];
    for (let i = 0; i + 1 < pl.length; i++) {
      const d = pl[i].distanceTo(pl[i + 1]);
      if (d >= left) {
        cur.push(pl[i].clone().lerp(pl[i + 1], d > 0 ? left / d : 0));
        left = 0;
        break;
      }
      cur.push(pl[i + 1]);
      left -= d;
    }
    out.push(cur);
  }
  return out;
}

/** End point and direction of a (possibly partial) polyline set. */
export function tipOf(polylines) {
  const pl = polylines[polylines.length - 1];
  if (!pl || pl.length < 2) return null;
  const a = pl[pl.length - 2], b = pl[pl.length - 1];
  const dir = b.clone().sub(a);
  if (dir.lengthSq() < 1e-12) return null;
  return { pos: b, dir: dir.normalize() };
}

const UP = new THREE.Vector3(0, 1, 0);

/** Cone arrowhead whose tip sits at a point. */
export class Head {
  constructor(color, size) {
    this.size = size;
    this.material = new THREE.MeshBasicMaterial({ color, transparent: true, depthWrite: false });
    const geo = new THREE.ConeGeometry(size * 0.42, size, 20);
    geo.translate(0, -size / 2, 0); // tip at origin
    this.obj = new THREE.Mesh(geo, this.material);
    this.obj.renderOrder = 2;
  }
  place(tip) {
    this.obj.visible = !!tip;
    if (!tip) return;
    this.obj.position.copy(tip.pos);
    this.obj.quaternion.setFromUnitVectors(UP, tip.dir);
  }
  dispose() {
    this.obj.geometry.dispose();
    this.material.dispose();
  }
}

/** Shorten the end of a polyline by `d` (so a head's tip lands on the end point). */
export function trimEnd(pl, d) {
  const out = pl.map((v) => v.clone());
  while (out.length >= 2 && d > 0) {
    const a = out[out.length - 2], b = out[out.length - 1];
    const L = a.distanceTo(b);
    if (L > d) { b.lerp(a, d / L); return out; }
    out.pop();
    d -= L;
  }
  return out;
}

export function trimStart(pl, d) {
  return trimEnd([...pl].reverse(), d).reverse();
}

/** Filled flat shape from 2D points (z taken from the first point). */
export function fillMesh(points, color, opacity) {
  const shape = new THREE.Shape(points.map((p) => new THREE.Vector2(p.x, p.y)));
  const geo = new THREE.ShapeGeometry(shape, 24);
  if (points.length) geo.translate(0, 0, points[0].z);
  const material = new THREE.MeshBasicMaterial({ color, transparent: true, opacity, depthWrite: false, side: THREE.DoubleSide });
  const mesh = new THREE.Mesh(geo, material);
  mesh.renderOrder = 1;
  return mesh;
}

export function circlePoints(r, n = 96, z = 0) {
  const pts = [];
  for (let i = 0; i <= n; i++) {
    const a = (i / n) * Math.PI * 2;
    pts.push(new THREE.Vector3(Math.cos(a) * r, Math.sin(a) * r, z));
  }
  return pts;
}

export function roundedRectPoints(w, h, r) {
  r = Math.max(0, Math.min(r, w / 2, h / 2));
  const s = new THREE.Shape();
  const x = -w / 2, y = -h / 2;
  s.moveTo(x + r, y);
  s.lineTo(x + w - r, y);
  if (r) s.quadraticCurveTo(x + w, y, x + w, y + r);
  s.lineTo(x + w, y + h - r);
  if (r) s.quadraticCurveTo(x + w, y + h, x + w - r, y + h);
  s.lineTo(x + r, y + h);
  if (r) s.quadraticCurveTo(x, y + h, x, y + h - r);
  s.lineTo(x, y + r);
  if (r) s.quadraticCurveTo(x, y, x + r, y);
  const pts = s.getPoints(10).map((p) => new THREE.Vector3(p.x, p.y, 0));
  pts.push(pts[0].clone());
  return pts;
}

/** "Nice" tick step for a range (1, 2, 5 × 10^k). */
export function niceStep(span, target = 6) {
  const raw = span / target;
  const mag = Math.pow(10, Math.floor(Math.log10(raw)));
  const n = raw / mag;
  return (n < 1.5 ? 1 : n < 3.5 ? 2 : n < 7.5 ? 5 : 10) * mag;
}

export function fmtTick(v) {
  // Very large or small magnitudes (log axes) as powers of ten: 10⁻⁵, 3×10⁶.
  if (v !== 0 && (Math.abs(v) >= 1e5 || Math.abs(v) < 1e-3)) {
    const k = Math.floor(Math.log10(Math.abs(v)) + 1e-9);
    const m = Math.round((v / 10 ** k) * 100) / 100;
    const sup = String(k).replace(/[-0-9]/g, (d) => '⁻⁰¹²³⁴⁵⁶⁷⁸⁹'['-0123456789'.indexOf(d)]);
    return (m === 1 ? '' : m === -1 ? '-' : `${m}×`) + `10${sup}`;
  }
  const r = Math.round(v * 1000) / 1000;
  return Object.is(r, -0) ? '0' : String(r);
}

/**
 * Tick values for a log axis over r (r[0] > 0): powers of ten when the range
 * spans several decades (thinned to about 7), else 1-2-5 or 1…9 multiples.
 */
export function logTicks(r) {
  const k0 = Math.floor(Math.log10(r[0]) + 1e-9), k1 = Math.ceil(Math.log10(r[1]) - 1e-9);
  const decades = Math.log10(r[1] / r[0]);
  const mults = decades >= 3 ? [1] : decades >= 1 ? [1, 2, 5] : [1, 2, 3, 4, 5, 6, 7, 8, 9];
  const step = Math.max(1, Math.ceil(decades / 7));
  const out = [];
  for (let k = k0; k <= k1; k++) {
    if (mults.length === 1 && (k - k0) % step) continue;
    for (const m of mults) {
      const v = Number((m * 10 ** k).toPrecision(12));
      if (v >= r[0] * (1 - 1e-9) && v <= r[1] * (1 + 1e-9)) out.push(v);
    }
  }
  // Too narrow for any 1…9 multiple to land twice: fall back to linear steps.
  if (out.length < 2) {
    const st = niceStep(r[1] - r[0], 4);
    out.length = 0;
    for (let v = Math.ceil(r[0] / st - 1e-9) * st; v <= r[1] + 1e-9; v += st) out.push(Number(v.toPrecision(12)));
  }
  return out;
}
