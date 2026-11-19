// The teacher: a small floating robot built from primitives (a stand-in until
// a rigged avatar exists), plus world-space subtitles for `say`.
import * as THREE from 'three';
import { TextPlane } from './text.js';

const SKIN = '#dfe7f1', SUIT = '#2b3a52', GLOW = '#7fe3ff';

export class Avatar {
  constructor({ position = new THREE.Vector3(-1.55, 0.25, -2.1), scale = 0.72, subtitleAt = new THREE.Vector3(0, 0.74, -2.2) } = {}) {
    this.root = new THREE.Group();
    this.root.position.copy(position);
    this.root.scale.setScalar(scale);
    this.baseY = position.y;
    /** Optional () => 0..1 | null: real voice loudness for the mouth. */
    this.level = null;

    const std = (color, extra = {}) => new THREE.MeshStandardMaterial({ color, roughness: 0.5, metalness: 0.1, ...extra });
    const glow = new THREE.MeshBasicMaterial({ color: GLOW });

    this.body = new THREE.Mesh(new THREE.CapsuleGeometry(0.15, 0.3, 8, 24), std(SUIT));
    this.body.position.y = 1.08;
    this.root.add(this.body);
    const chest = new THREE.Mesh(new THREE.CircleGeometry(0.035, 24), glow);
    chest.position.set(0, 1.17, 0.151);
    this.root.add(chest);

    this.head = new THREE.Group();
    this.head.position.y = 1.52;
    this.root.add(this.head);
    const skull = new THREE.Mesh(new THREE.SphereGeometry(0.17, 40, 28), std(SKIN));
    skull.scale.set(1.18, 1, 1);
    this.head.add(skull);
    // Dark face plate: a sphere patch centred on +z (phi = π/2 is +z in three.js).
    const visor = new THREE.Mesh(new THREE.SphereGeometry(0.168, 40, 28, Math.PI / 2 - 0.85, 1.7, 1.0, 1.05), new THREE.MeshStandardMaterial({ color: '#0d1522', roughness: 0.2 }));
    visor.scale.set(1.2, 1.02, 1.03);
    this.head.add(visor);
    const eyeGeo = new THREE.CapsuleGeometry(0.016, 0.028, 4, 12);
    this.eyes = [-0.055, 0.055].map((x) => {
      const m = new THREE.Mesh(eyeGeo, glow);
      m.position.set(x, 0.025, 0.168);
      this.head.add(m);
      return m;
    });
    this.mouth = new THREE.Mesh(new THREE.CapsuleGeometry(0.008, 0.05, 4, 12), glow);
    this.mouth.rotation.z = Math.PI / 2;
    this.mouth.position.set(0, -0.045, 0.17);
    this.head.add(this.mouth);
    const antenna = new THREE.Mesh(new THREE.CylinderGeometry(0.006, 0.006, 0.09), std(SKIN));
    antenna.position.y = 0.2;
    this.head.add(antenna);
    this.bulb = new THREE.Mesh(new THREE.SphereGeometry(0.022, 16, 12), glow);
    this.bulb.position.y = 0.255;
    this.head.add(this.bulb);

    this.hands = [-1, 1].map((side) => {
      const h = new THREE.Mesh(new THREE.SphereGeometry(0.05, 24, 16), std(SKIN));
      h.userData.rest = new THREE.Vector3(0.27 * side, 1.0, 0.05);
      h.position.copy(h.userData.rest);
      this.root.add(h);
      return h;
    });

    // Pointer beam + target dot (world space).
    this.beam = new THREE.Mesh(new THREE.CylinderGeometry(0.004, 0.004, 1, 8), new THREE.MeshBasicMaterial({ color: GLOW, transparent: true, opacity: 0.7 }));
    this.dot = new THREE.Mesh(new THREE.SphereGeometry(0.018, 16, 12), new THREE.MeshBasicMaterial({ color: GLOW }));
    this.beam.visible = this.dot.visible = false;

    // Subtitles.
    this.sub = new TextPlane(' ', { size: 0.07, color: '#f5f7fa', weight: 500 });
    this.subPanel = new THREE.Mesh(new THREE.PlaneGeometry(1, 1), new THREE.MeshBasicMaterial({ color: '#0b111a', transparent: true, opacity: 0.8, depthWrite: false }));
    this.subRoot = new THREE.Group();
    this.subRoot.position.copy(subtitleAt);
    this.subRoot.rotation.x = -0.25;
    this.subRoot.add(this.subPanel, this.sub.mesh);
    this.sub.mesh.position.z = 0.002;
    this.subRoot.visible = false;

    this.thinking = false;
    this.pointTarget = null;
    this.pointUntil = 0;
    this.speakUntil = 0;
    this.subUntil = 0;
    this.nextBlink = 2;
    this.t = 0;
  }

  addTo(scene) {
    scene.add(this.root, this.beam, this.dot, this.subRoot);
  }

  /** Show what the learner asked (or a status line) in the subtitle strip. */
  hear(text, { thinking = true, secs = 4 } = {}) {
    this.thinking = thinking;
    this.sub.material.color.set('#9fb4cc');
    this.#subtitle(text, secs);
  }

  say(text, dur) {
    this.thinking = false;
    this.sub.material.color.set('#f5f7fa');
    const now = this.t;
    this.speakUntil = now + dur;
    this.#subtitle(text, dur + 0.8);
    this.onSay?.(text);
  }

  #subtitle(text, secs) {
    this.subUntil = this.t + secs;
    // Wrap long lines for the subtitle strip.
    const words = String(text).split(/\s+/);
    const lines = [];
    let cur = '';
    for (const w of words) {
      if ((cur + ' ' + w).trim().length > 64) { lines.push(cur); cur = w; } else cur = (cur + ' ' + w).trim();
    }
    if (cur) lines.push(cur);
    this.sub.setText(lines.join('\n'));
    this.subPanel.scale.set(this.sub.width + 0.06, this.sub.height + 0.02, 1);
    this.subRoot.visible = true;
  }

  /** Point at a world position (or stop with null). */
  point(world, dur = 0) {
    this.pointTarget = world ? world.clone() : null;
    this.pointUntil = this.t + Math.max(3.5, dur);
  }

  /** Stop talking now (the step was skipped). */
  silence() {
    this.speakUntil = this.t;
    this.subUntil = Math.min(this.subUntil ?? 0, this.t + 0.3);
  }

  update(dt, camera) {
    // Paused: the subtitles and the mouth wait too.
    if (this.paused) for (const k of ['speakUntil', 'subUntil', 'pointUntil']) if (this[k] > this.t) this[k] += dt;
    this.t += dt;
    const t = this.t;
    this.root.position.y = this.baseY + Math.sin(t * 1.6) * 0.012;

    if (this.pointTarget && t > this.pointUntil) this.pointTarget = null;
    const camPos = camera.getWorldPosition(new THREE.Vector3());
    // Thinking: glance up and to the side.
    const look = this.pointTarget ?? (this.thinking ? this.head.getWorldPosition(new THREE.Vector3()).add(new THREE.Vector3(0.4, 0.5, 1)) : camPos);

    // Head turns toward the look target (smoothly).
    const headWorld = this.head.getWorldPosition(new THREE.Vector3());
    const m = new THREE.Matrix4().lookAt(look, headWorld, new THREE.Vector3(0, 1, 0));
    const want = new THREE.Quaternion().setFromRotationMatrix(m);
    const parentQ = this.root.getWorldQuaternion(new THREE.Quaternion()).invert();
    want.premultiply(parentQ);
    this.head.quaternion.slerp(want, Math.min(1, dt * 5));
    this.body.rotation.y += ((this.pointTarget ? 0.35 : 0) - this.body.rotation.y) * Math.min(1, dt * 3);

    // Right hand reaches toward the target.
    const hand = this.hands[1];
    let goal = hand.userData.rest.clone();
    goal.y += Math.sin(t * 2.1) * 0.01;
    if (this.pointTarget) {
      const shoulder = this.root.localToWorld(new THREE.Vector3(0.15, 1.25, 0));
      const dir = this.pointTarget.clone().sub(shoulder).normalize();
      goal = this.root.worldToLocal(shoulder.addScaledVector(dir, 0.32));
    }
    hand.position.lerp(goal, Math.min(1, dt * 6));
    const left = this.hands[0];
    left.position.y = left.userData.rest.y + Math.sin(t * 2.1 + 1) * 0.01;

    // Beam from hand to target.
    const showBeam = !!this.pointTarget && hand.position.distanceTo(goal) < 0.08;
    this.beam.visible = this.dot.visible = showBeam;
    if (showBeam) {
      // Beam and dot live in the avatar's parent space (the movable classroom).
      const par = this.beam.parent;
      const a = par.worldToLocal(hand.getWorldPosition(new THREE.Vector3()));
      const b = par.worldToLocal(this.pointTarget.clone());
      const d = b.clone().sub(a);
      this.beam.position.copy(a).addScaledVector(d, 0.5);
      this.beam.scale.set(1, d.length(), 1);
      this.beam.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), d.normalize());
      this.dot.position.copy(b);
      this.dot.scale.setScalar(1 + 0.25 * Math.sin(t * 8));
    }

    // Speaking: mouth and antenna.
    const speaking = t < this.speakUntil && !this.paused;
    const heard = this.level?.();
    const talk = heard ?? (speaking ? 0.5 + 0.5 * Math.abs(Math.sin(t * 13) * Math.sin(t * 4.7)) : 0);
    this.mouth.scale.set(1, 1 + talk * 0.4, 1 + talk * 3);
    this.bulb.scale.setScalar(1 + (speaking ? 0.3 * Math.abs(Math.sin(t * 6)) : this.thinking ? 0.45 * (0.5 + 0.5 * Math.sin(t * 5)) : 0));

    // Blink.
    if (t > this.nextBlink) this.nextBlink = t + 2.5 + Math.random() * 3;
    const blink = this.nextBlink - t < 0.12 ? 0.15 : 1;
    this.eyes.forEach((e) => (e.scale.y = blink));

    // Subtitles: face the viewer a little, fade after speaking.
    if (this.subRoot.visible) {
      const left2 = this.subUntil - t;
      this.sub.material.opacity = Math.min(1, Math.max(0, left2 / 0.5));
      this.subPanel.material.opacity = 0.8 * this.sub.material.opacity;
      if (left2 <= 0) this.subRoot.visible = false;
    }
  }
}
