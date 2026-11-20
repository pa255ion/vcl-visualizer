// Questions for the learner (docs/SPEC.md §4.10): quiz, pick, estimate, recall.
//
// A question is a card floating in front of the board (in board units, a
// child of the stage). The learner answers with the mouse or a controller ray:
// every pressable thing is a `Hit` ({ mesh, press(), hover(on), drag(local) }),
// which the viewer raycasts against. After the answer the card shows what was
// right and `explain=`, and Continue lets the lesson go on.

import * as THREE from 'three';
import { TextPlane } from './text.js';

const C = {
  card: '#101824', edge: '#3b4a5e', fg: '#e8edf3', muted: '#8796ab',
  button: '#1c2a3d', hover: '#2f6db5', chosen: '#2c6b80', ok: '#2e7d32', okFg: '#66bb6a', err: '#8e2b2b', errFg: '#ef5350',
};
const W = 9;          // card width, board units
const PAD = 0.45;
const Z = 1.5;        // in front of the board
const TEXT = 0.36;    // body line height

/** Where the learner has dragged cards to, from their usual place (board
 *  units; `pick` cards separately, as they start elsewhere). Remembered. */
const moved = (() => { try { return JSON.parse(localStorage.getItem('vcl.questionOffset')) ?? {}; } catch { return {}; } })();
const saveMoved = () => { try { localStorage.setItem('vcl.questionOffset', JSON.stringify(moved)); } catch {} };

/** Wrap text to lines of at most `n` characters. */
export function wrap(text, n) {
  const out = [];
  for (const para of String(text).split('\n')) {
    let cur = '';
    for (const w of para.split(/\s+/).filter(Boolean)) {
      if (cur && (cur + ' ' + w).length > n) { out.push(cur); cur = w; } else cur = cur ? `${cur} ${w}` : w;
    }
    out.push(cur);
  }
  return out.join('\n');
}
const charsFor = (width, size) => Math.max(8, Math.floor(width / (size * 0.5)));

/** A nice slider step for a range: 1, 2 or 5 × 10^k, about a hundredth of it. */
export function niceStep(lo, hi) {
  const raw = (hi - lo) / 100, k = Math.pow(10, Math.floor(Math.log10(raw)));
  return [1, 2, 5, 10].map((m) => m * k).find((s) => s >= raw);
}
const decimals = (step) => Math.max(0, -Math.floor(Math.log10(step) + 1e-9));

// Options that name other options by letter ("A and B", "both B and C",
// "only A") keep the whole list in the author's order; ones that point by
// position ("all of the above", "none of these") stay where they are while
// the rest are shuffled.
const BY_LETTER = /\b(?:both|either|neither|only|options?|answers?|choices?)\s+[A-H]\b|\b[A-H]\s*(?:,|&|\+|\band\b|\bor\b|\bnor\b)\s*[A-H]\b/;
const BY_PLACE = /\b(?:all|none|both|neither|either|any)\b.*\b(?:above|below|these|those|the others|options|answers|choices)\b/i;

/** The order to show quiz options in: a list of option numbers (1-based). */
export function optionOrder(options, rand = Math.random) {
  const order = options.map((_, i) => i + 1);
  if (options.some((o) => BY_LETTER.test(o))) return order;
  const free = order.filter((n) => !BY_PLACE.test(options[n - 1]));
  const shuffled = [...free];
  for (let i = shuffled.length - 1; i > 0; i--) {
    const j = Math.floor(rand() * (i + 1));
    [shuffled[i], shuffled[j]] = [shuffled[j], shuffled[i]];
  }
  let k = 0;
  return order.map((n) => (BY_PLACE.test(options[n - 1]) ? n : shuffled[k++]));
}

/** Where an estimate's slider starts: random, but not near the answer (or the middle). */
export function startValue([lo, hi], answer, rand = Math.random) {
  const span = hi - lo;
  for (let i = 0; i < 20; i++) {
    const v = lo + rand() * span;
    if (Math.abs(v - answer) > span * 0.2 && Math.abs(v - (lo + hi) / 2) > span * 0.05) return v;
  }
  return Math.abs(lo - answer) > Math.abs(hi - answer) ? lo : hi;
}

/** Is this answer right? `given`: chosen numbers (quiz, pick), a value (estimate), or true/false (recall). */
export function grade(cmd, given) {
  if (cmd.op === 'estimate') {
    const tol = cmd.tol ?? 0.05 * (cmd.range[1] - cmd.range[0]);
    return Math.abs(given - cmd.answer) <= tol + 1e-9;
  }
  if (cmd.op === 'recall') return !!given;
  const want = [...cmd.answer].sort((a, b) => a - b), got = [...given].sort((a, b) => a - b);
  return want.length === got.length && want.every((v, i) => v === got[i]);
}

export class QuestionCard {
  /**
   * rt: the Runtime (stage, board, pointWorld…). done(result): the learner
   * answered or skipped; onContinue(): they pressed Continue.
   */
  constructor(rt, cmd, { done, onContinue }) {
    this.rt = rt;
    this.cmd = cmd;
    this.done = done;
    this.onContinue = onContinue;
    this.multi = (cmd.op === 'quiz' || cmd.op === 'pick') && cmd.answer.length > 1;
    this.chosen = new Set();
    this.order = cmd.op === 'quiz' ? optionOrder(cmd.options) : null; // shown order; answers keep the author's numbers
    this.step = cmd.op === 'estimate' ? cmd.step ?? niceStep(...cmd.range) : null;
    // The slider starts somewhere random (away from the answer), not in the middle
    // where a guess would sit, and must be moved before Check.
    this.value = cmd.op === 'estimate' ? this.snap(startValue(cmd.range, cmd.answer)) : null;
    this.moved = false;
    this.confidence = null; // 'guess' | 'fairly' | 'sure', if the learner says
    this.revealed = false; // recall: answer shown
    this.result = null;
    this.hits = [];
    this.root = new THREE.Group();
    this.root.name = 'question';
    rt.stage.add(this.root);
    this.badges = cmd.op === 'pick' ? new THREE.Group() : null;
    if (this.badges) rt.stage.add(this.badges);
    this.fade = 0;
    this.render();
  }

  get answered() { return !!this.result; }

  snap(v) {
    const [lo, hi] = this.cmd.range;
    return Math.min(hi, Math.max(lo, lo + Math.round((v - lo) / this.step) * this.step));
  }

  format(v) {
    const s = v.toFixed(decimals(this.step));
    return this.cmd.unit ? `${s} ${this.cmd.unit}` : s;
  }

  /** Rebuild the card for its current state. */
  render() {
    this.clear(this.root);
    this.hits = [];
    const c = this.cmd, body = new THREE.Group();
    let y = -PAD;
    const x0 = -W / 2 + PAD, inner = W - 2 * PAD;
    const text = (s, size, color, x = x0, width = inner) => {
      const tp = new TextPlane(wrap(s, charsFor(width, size)), { size, color, align: 'left' });
      tp.mesh.position.set(x + tp.width / 2, y - tp.height / 2, 0.02);
      body.add(tp.mesh);
      y -= tp.height;
      return tp;
    };

    let kind = { quiz: this.multi ? 'Choose every right answer' : 'Question', pick: this.multi ? 'Point at every right one on the board' : 'Point at it on the board', estimate: 'Estimate', recall: 'Recall' }[c.op];
    if (c.predict) kind = `Guess first (not scored) · ${kind}`;
    if (c.from) kind = `Review from ${c.from} · ${kind}`;
    const pick = c.op === 'pick';
    text(kind.toUpperCase(), 0.24, c.predict ? '#ffe066' : c.from ? '#b58cff' : C.muted, x0, inner - 2.6); // clear of the drag grip
    y -= 0.12;
    text(c.q, 0.44, C.fg, x0, pick && !this.result ? inner - 2.2 : inner);
    y -= pick ? 0.15 : 0.3;

    const res = this.result;
    if (c.op === 'quiz') {
      this.order.forEach((n, i) => {
        const opt = c.options[n - 1], right = c.answer.includes(n), picked = res ? res.given.includes(n) : this.chosen.has(n);
        const mark = res ? (right ? '✓' : picked ? '✗' : ' ') : this.multi ? (picked ? '☑' : '☐') : String.fromCharCode(65 + i);
        const label = wrap(`${mark}   ${opt}`, charsFor(inner - 0.5, TEXT));
        const lines = label.split('\n').length;
        const h = lines * TEXT * 1.25 + 0.3;
        const bg = res ? (right ? C.ok : picked ? C.err : C.button) : picked ? C.chosen : C.button;
        this.button(body, x0, y, inner, h, label, res ? null : () => this.choose(n), { bg, align: 'left' });
        y -= h + 0.15;
      });
    } else if (c.op === 'pick') {
      this.renderBadges();
    } else if (c.op === 'estimate') {
      y -= 0.5;
      this.slider(body, x0 + 0.3, y, inner - 0.6);
      y -= 1.0;
    } else if (c.op === 'recall') {
      if (!this.revealed) {
        text('Say the answer out loud, then reveal it.', 0.3, C.muted);
        y -= 0.2;
        this.button(body, x0, y, 3.4, 0.75, 'Show answer', () => { this.revealed = true; this.render(); });
        y -= 0.9;
      } else {
        const tp = text(c.answer, 0.4, '#ffe066');
        tp.mesh.position.x += 0.1;
        y -= 0.3;
      }
    }

    // Feedback, or the actions.
    y -= 0.1;
    if (res) {
      const head = res.skipped ? 'Skipped'
        : c.predict ? (res.correct ? '✓ Good guess' : 'Not this time: now let\'s see why')
        : res.correct ? (c.op === 'recall' ? '✓ Good' : res.confidence === 'guess' ? '✓ Correct (a lucky guess? it will come back)' : '✓ Correct')
        : c.op === 'recall' ? 'Worth another look' : res.confidence === 'sure' ? '✗ Not quite, though you were sure: worth remembering why' : '✗ Not quite';
      text(head, 0.42, res.skipped ? C.muted : res.correct ? C.okFg : C.errFg);
      if (c.op === 'estimate' && !res.skipped) text(`You said ${this.format(res.given)}; the answer is ${this.format(c.answer)}.`, 0.32, C.fg);
      if (c.explain) { y -= 0.1; text(c.explain, 0.34, C.fg); }
      y -= 0.25;
      this.button(body, W / 2 - PAD - 3, y, 3, 0.75, 'Continue ▸', () => this.onContinue(), { bg: C.chosen });
      y -= 0.75;
    } else {
      const canCheck = this.multi || c.op === 'estimate';
      if (pick && !canCheck) {
        // Short card: Skip sits beside the question.
        this.button(body, W / 2 - PAD - 1.9, -PAD - 0.4, 1.9, 0.7, 'Skip', () => this.skip(), { size: 0.3 });
        y -= PAD;
        return this.finishCard(body, y);
      }
      if (c.op === 'recall' && this.revealed) {
        this.button(body, x0, y, 3.4, 0.75, 'I knew it', () => this.finish(true), { bg: C.ok });
        this.button(body, x0 + 3.6, y, 3.4, 0.75, 'Not yet', () => this.finish(false), { bg: C.err });
      } else if (canCheck) {
        if (this.hint) { text(this.hint, 0.28, '#ffe066'); y -= 0.15; }
        // How sure? (optional): a sure mistake is the one most worth correcting.
        if (!c.predict) {
          text('How sure are you?', 0.26, C.muted);
          y -= 0.08;
          let cx = x0;
          for (const [k, label] of [['guess', 'Guessing'], ['fairly', 'Fairly'], ['sure', 'Sure']]) {
            const on = this.confidence === k;
            this.button(body, cx, y, 2.1, 0.6, label, () => { this.confidence = on ? null : k; this.render(); }, { bg: on ? C.chosen : C.button, size: 0.28 });
            cx += 2.25;
          }
          y -= 0.8;
        }
        const ready = c.op === 'estimate' || this.chosen.size > 0;
        const check = () => {
          if (c.op === 'estimate' && !this.moved) { this.hint = 'Drag the slider to your estimate first.'; return this.render(); }
          this.finish(c.op === 'estimate' ? this.value : [...this.chosen]);
        };
        this.button(body, x0, y, 2.6, 0.75, 'Check', ready ? check : null, { bg: ready ? C.chosen : C.button });
      }
      this.button(body, W / 2 - PAD - 1.9, y, 1.9, 0.75, 'Skip', () => this.skip(), { size: 0.3 });
      y -= 0.75;
    }
    y -= PAD;
    this.finishCard(body, y);
  }

  finishCard(body, y) {
    const h = -y;
    const bg = new THREE.Mesh(roundedRect(W, h, 0.3), new THREE.MeshBasicMaterial({ color: C.card, transparent: true, opacity: 0.97 }));
    bg.position.set(0, -h / 2, 0);
    const edge = new THREE.LineLoop(roundedOutline(W, h, 0.3), new THREE.LineBasicMaterial({ color: C.edge, transparent: true }));
    edge.position.copy(bg.position);
    this.root.add(bg, edge, body);
    this.height = h;
    // A drag handle in the corner; the whole card (not its buttons) moves it.
    const grip = new TextPlane('✥ drag to move', { size: 0.22, color: C.muted });
    grip.mesh.position.set(W / 2 - PAD - grip.width / 2, -PAD - 0.11, 0.02);
    this.root.add(grip.mesh);
    let grab = null;
    this.hits.push({
      mesh: bg,
      cursor: 'move',
      press: (world) => { if (world) grab = this.rt.stage.worldToLocal(world.clone()).sub(this.root.position); },
      drag: (world) => {
        if (!grab) return;
        const p = this.rt.stage.worldToLocal(world.clone()).sub(grab);
        const [bw, bh] = this.rt.board?.size ?? [16, 9];
        p.x = Math.max(-bw / 2 - 6, Math.min(bw / 2 + 6, p.x));
        p.y = Math.max(-bh / 2 - 3, Math.min(bh / 2 + 6, p.y));
        this.root.position.x = p.x;
        this.root.position.y = p.y;
        const home = this.home();
        moved[this.slot] = { x: p.x - home.x, y: p.y - home.y };
        saveMoved();
      },
      hover: () => {},
    });
    this.place();
    this.setOpacity(this.fade);
  }

  /** Where the card goes: over the lower board, clear of the subtitles (pick:
   *  a short card over the title, leaving the things to pick in view). */
  place() {
    const home = this.home(), off = moved[this.slot] ?? { x: 0, y: 0 };
    this.root.position.set(home.x + off.x, home.y + off.y, Z);
    this.root.rotation.x = this.cmd.op === 'pick' ? 0.06 : -0.06;
  }

  get slot() { return this.cmd.op === 'pick' ? 'pick' : 'card'; }

  /** The card's usual top-centre, before any dragging. */
  home() {
    const bh = this.rt.board?.size[1] ?? 9;
    return { x: 0, y: this.cmd.op === 'pick' ? bh / 2 - 0.1 : Math.min(bh / 2 - 0.3, this.height - bh / 2 + 0.9) };
  }

  button(parent, x, y, w, h, label, onPress, { bg = C.button, align = 'center', size = TEXT } = {}) {
    const mat = new THREE.MeshBasicMaterial({ color: bg, transparent: true });
    const m = new THREE.Mesh(roundedRect(w, h, 0.15), mat);
    m.position.set(x + w / 2, y - h / 2, 0.01);
    parent.add(m);
    const tp = new TextPlane(label, { size, color: onPress || bg !== C.button ? C.fg : C.muted, align });
    tp.mesh.position.set(align === 'left' ? x + 0.25 + tp.width / 2 : x + w / 2, y - h / 2, 0.03);
    parent.add(tp.mesh);
    if (onPress) {
      const base = new THREE.Color(bg);
      this.hits.push({
        mesh: m,
        press: onPress,
        hover: (on) => mat.color.copy(base).lerp(new THREE.Color(C.hover), on ? 0.6 : 0),
      });
    }
    return m;
  }

  slider(parent, x, y, w) {
    const [lo, hi] = this.cmd.range, res = this.result;
    const at = (v) => x + ((v - lo) / (hi - lo)) * w;
    const track = new THREE.Mesh(new THREE.PlaneGeometry(w, 0.12), new THREE.MeshBasicMaterial({ color: '#3b4a5e', transparent: true }));
    track.position.set(x + w / 2, y, 0.01);
    parent.add(track);
    const label = (s, px, py, size = 0.28, color = C.muted) => {
      const tp = new TextPlane(s, { size, color });
      tp.mesh.position.set(px, py, 0.03);
      parent.add(tp.mesh);
      return tp;
    };
    label(this.format(lo), x, y - 0.4);
    label(this.format(hi), x + w, y - 0.4);
    if (res && !res.skipped) {
      const right = new THREE.Mesh(new THREE.PlaneGeometry(0.08, 0.6), new THREE.MeshBasicMaterial({ color: C.okFg, transparent: true }));
      right.position.set(at(this.cmd.answer), y, 0.02);
      parent.add(right);
    }
    const knob = new THREE.Mesh(new THREE.CircleGeometry(0.22, 24), new THREE.MeshBasicMaterial({ color: res ? (res.correct ? C.okFg : C.errFg) : '#7fe3ff', transparent: true }));
    knob.position.set(at(this.value), y, 0.03);
    parent.add(knob);
    const value = label(this.format(this.value), at(this.value), y + 0.45, 0.34, C.fg);
    if (res) return;
    // A tall invisible strip around the track takes presses and drags.
    const grab = new THREE.Mesh(new THREE.PlaneGeometry(w + 0.6, 1.0), new THREE.MeshBasicMaterial({ visible: false }));
    grab.position.copy(track.position);
    parent.add(grab);
    const set = (local) => {
      this.moved = true;
      this.hint = null;
      this.value = this.snap(lo + ((local.x - x) / w) * (hi - lo));
      knob.position.x = at(this.value);
      value.setText(this.format(this.value));
      value.mesh.position.x = at(this.value);
    };
    this.hits.push({
      mesh: grab,
      press: () => {},
      drag: (world) => set(parent.worldToLocal(world.clone())),
      hover: (on) => knob.scale.setScalar(on ? 1.25 : 1),
    });
  }

  /** Numbered markers on the things to pick from. */
  renderBadges() {
    this.clear(this.badges);
    const res = this.result;
    this.cmd.ids.forEach((id, i) => {
      const n = i + 1, pos = this.targetPos(id);
      if (!pos) return;
      const right = this.cmd.answer.includes(n), picked = res ? res.given.includes(n) : this.chosen.has(n);
      const color = res ? (right ? C.ok : picked ? C.err : '#33415a') : picked ? C.chosen : '#1f5f99';
      const mat = new THREE.MeshBasicMaterial({ color, transparent: true });
      const disc = new THREE.Mesh(new THREE.CircleGeometry(0.34, 28), mat);
      disc.renderOrder = 20;
      disc.position.copy(pos);
      const ring = new THREE.Mesh(new THREE.RingGeometry(0.34, 0.4, 28), new THREE.MeshBasicMaterial({ color: "#e8edf3", transparent: true }));
      ring.renderOrder = 20;
      ring.position.copy(pos);
      const tp = new TextPlane(res ? (right ? '✓' : picked ? '✗' : String(n)) : String(n), { size: 0.4, color: '#ffffff' });
      tp.mesh.renderOrder = 21;
      tp.mesh.position.copy(pos).add(new THREE.Vector3(0, 0, 0.01));
      this.badges.add(disc, ring, tp.mesh);
      if (!res) {
        const base = new THREE.Color(color);
        this.hits.push({
          mesh: disc,
          press: () => this.choose(n),
          hover: (on) => { mat.color.copy(base).lerp(new THREE.Color(C.hover), on ? 0.6 : 0); ring.scale.setScalar(on ? 1.15 : 1); },
        });
      }
    });
  }

  /** A pick target's position in stage space, a little in front of it. */
  targetPos(id) {
    const rt = this.rt, dot = id.indexOf('.');
    const ref = dot < 0 ? { ref: id, anchor: 'center' } : { ref: id.slice(0, dot), anchor: id.slice(dot + 1) };
    let world = null;
    try {
      const e = rt.get(ref.ref);
      if (dot < 0 || e.pins?.[ref.anchor]) world = rt.anchorWorld(e, ref.anchor);
      else {
        const part = rt.target(id), box = new THREE.Box3();
        for (const o of part.objs ?? []) box.expandByObject(o);
        world = box.isEmpty() ? rt.anchorWorld(e, 'center') : box.getCenter(new THREE.Vector3());
      }
    } catch (e) {
      rt.log('warning', `pick: ${e.message}`);
      return null;
    }
    return rt.stage.worldToLocal(world).add(new THREE.Vector3(0, 0, 0.3));
  }

  choose(n) {
    if (this.result) return;
    if (!this.multi) return this.finish([n]);
    if (this.chosen.has(n)) this.chosen.delete(n); else this.chosen.add(n);
    this.render();
  }

  finish(given) {
    if (this.result) return;
    this.result = { given, correct: grade(this.cmd, given), skipped: false, confidence: this.confidence };
    this.render();
    this.done(this.result);
  }

  skip() {
    if (this.result) return this.onContinue();
    this.result = { given: null, correct: false, skipped: true };
    this.done(this.result);
    this.onContinue();
  }

  update(dt) {
    if (this.fade < 1) {
      this.fade = Math.min(1, this.fade + dt / 0.3);
      this.setOpacity(this.fade);
    }
  }

  setOpacity(a) {
    for (const g of [this.root, this.badges]) g?.traverse((o) => {
      const m = o.material;
      if (!m || m.visible === false) return;
      m.userData.base ??= m.opacity;
      m.opacity = m.userData.base * a;
    });
  }

  clear(g) {
    if (!g) return;
    for (const o of [...g.children]) {
      o.traverse((x) => { x.geometry?.dispose(); x.material?.map?.dispose(); x.material?.dispose(); });
      g.remove(o);
    }
  }

  dispose() {
    this.clear(this.root);
    this.clear(this.badges);
    this.root.removeFromParent();
    this.badges?.removeFromParent();
    this.hits = [];
  }
}

function roundedShape(w, h, r) {
  const s = new THREE.Shape(), x = -w / 2, y = -h / 2;
  r = Math.min(r, w / 2, h / 2);
  s.moveTo(x + r, y);
  s.lineTo(x + w - r, y); s.quadraticCurveTo(x + w, y, x + w, y + r);
  s.lineTo(x + w, y + h - r); s.quadraticCurveTo(x + w, y + h, x + w - r, y + h);
  s.lineTo(x + r, y + h); s.quadraticCurveTo(x, y + h, x, y + h - r);
  s.lineTo(x, y + r); s.quadraticCurveTo(x, y, x + r, y);
  return s;
}
const roundedRect = (w, h, r) => new THREE.ShapeGeometry(roundedShape(w, h, r), 4);
const roundedOutline = (w, h, r) => new THREE.BufferGeometry().setFromPoints(roundedShape(w, h, r).getPoints(4));
