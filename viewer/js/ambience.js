// Ambience: generative music, a background sound bed, and soft sounds as
// things are drawn, all synthesized with Web Audio (no audio files). Each
// scene has its own key, tempo and instruments; the drawing sounds take their
// notes from the chord playing at that moment, so they always fit the music.
// The music dips while the teacher speaks.
//
//   const amb = new Ambience();
//   amb.setScene('aero'); amb.setMusic(0..1); amb.setEffects(0..1);
//   amb.draw(seconds)  amb.pop()  amb.fade()  amb.question()  amb.answer(correct)  amb.duck(seconds)

const mtof = (m) => 440 * Math.pow(2, (m - 69) / 12);

// Chords as MIDI notes (root position, spread for pads). Bars per chord: 2.
const THEMES = {
  night: {
    bpm: 58, music: 0.5,
    chords: [[50, 57, 62, 65, 69], [46, 53, 58, 62, 69], [41, 48, 57, 60, 64], [48, 55, 60, 64, 67]], // Dm9 B♭maj7 Fmaj7 C
    pad: { types: ['triangle', 'sine'], cutoff: 900, gain: 0.05, attack: 2.5, release: 3.5 },
    bell: { density: 0.08, octave: 12, gain: 0.022, ratio: 3.5 },
    bed: { kind: 'wind', gain: 0.025 },
  },
  aero: {
    bpm: 84, music: 0.55,
    chords: [[48, 55, 64, 67, 71, 74], [45, 52, 60, 64, 67, 71], [41, 48, 57, 60, 64, 67], [43, 50, 59, 62, 65, 69]], // Cmaj9 Am9 Fmaj9 G13
    pad: { types: ['sine', 'triangle'], cutoff: 2600, gain: 0.04, attack: 1.6, release: 2.5 },
    bell: { density: 0.22, octave: 24, gain: 0.018, ratio: 2.0 },
    bed: { kind: 'water', gain: 0.02 },
  },
  tech: {
    bpm: 100, music: 0.5,
    chords: [[45, 52, 57, 60, 64], [41, 48, 53, 57, 64], [48, 55, 60, 64, 67], [43, 50, 55, 59, 62]], // Am F C G
    pad: { types: ['sawtooth', 'sawtooth'], cutoff: 650, gain: 0.025, attack: 2, release: 3, lfo: 0.07 },
    arp: { type: 'sawtooth', every: 1, density: 0.55, gain: 0.03, cutoff: 1800, octave: 12, dur: 0.12 },
    pulse: { gain: 0.09 },
    bed: { kind: 'hum', gain: 0.02 },
  },
  demo: {
    bpm: 112, music: 0.45,
    chords: [[57, 60, 64], [53, 57, 60], [55, 59, 62], [52, 55, 59]], // Am F G Em
    pad: { types: ['square'], cutoff: 900, gain: 0.012, attack: 1, release: 2 },
    arp: { type: 'square', every: 1, density: 1, gain: 0.022, cutoff: 3500, octave: 12, dur: 0.1, tracker: true },
    bass: { type: 'triangle', gain: 0.08 },
    bed: null,
  },
};

export class Ambience {
  constructor() {
    this.ctx = null;
    this.scene = 'night';
    this.music = 0.6; // slider positions, 0–1 (0.6 and 0.7 are the designed levels)
    this.effects = 0.7;
    this.chord = 0;
    this.step = 0;
    this.padVoices = [];
    this.bedNode = null;
    // Browsers allow audio only after a gesture.
    const unlock = () => {
      this.start();
      if (this.ctx?.state === 'running') for (const e of ['pointerdown', 'keydown', 'touchstart']) removeEventListener(e, unlock, true);
    };
    for (const e of ['pointerdown', 'keydown', 'touchstart']) addEventListener(e, unlock, true);
  }

  get theme() { return THEMES[this.scene] ?? THEMES.night; }

  start() {
    if (this.ctx) { if (this.ctx.state !== 'running') this.ctx.resume(); return; }
    let ctx;
    try { ctx = this.ctx = new AudioContext(); } catch { return; }
    this.master = ctx.createGain();
    this.master.gain.value = 0.9;
    const comp = ctx.createDynamicsCompressor();
    comp.threshold.value = -18; comp.ratio.value = 3;
    this.master.connect(comp).connect(ctx.destination);
    // A soft hall: generated impulse response.
    this.reverb = ctx.createConvolver();
    this.reverb.buffer = this.impulse(2.8);
    const wet = ctx.createGain(); wet.gain.value = 0.5;
    this.reverb.connect(wet).connect(this.master);
    // Echo for the arpeggios and bells.
    this.delay = ctx.createDelay(1);
    const fb = ctx.createGain(); fb.gain.value = 0.32;
    const dwet = ctx.createGain(); dwet.gain.value = 0.35;
    this.delay.connect(fb).connect(this.delay);
    this.delay.connect(dwet).connect(this.master);
    // Buses: music (ducked under speech), bed, effects.
    this.musicBus = ctx.createGain(); this.musicBus.connect(this.master); this.musicBus.connect(this.reverb);
    this.bedBus = ctx.createGain(); this.bedBus.connect(this.master);
    this.fx = ctx.createGain(); this.fx.connect(this.master); this.fx.connect(this.reverb);
    this.duckGain = ctx.createGain(); // between the music voices and the music bus
    this.duckGain.connect(this.musicBus);
    this.noise = this.noiseBuffer();
    this.applyLevels();
    this.applyScene(true);
    this.nextTime = ctx.currentTime + 0.1;
    this.timer = setInterval(() => this.schedule(), 50);
    if (ctx.state !== 'running') ctx.resume();
  }

  impulse(secs) {
    const ctx = this.ctx, n = Math.floor(ctx.sampleRate * secs), b = ctx.createBuffer(2, n, ctx.sampleRate);
    for (let ch = 0; ch < 2; ch++) {
      const d = b.getChannelData(ch);
      for (let i = 0; i < n; i++) d[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / n, 3);
    }
    return b;
  }

  noiseBuffer() {
    const ctx = this.ctx, n = ctx.sampleRate * 2, b = ctx.createBuffer(1, n, ctx.sampleRate), d = b.getChannelData(0);
    let last = 0;
    for (let i = 0; i < n; i++) { const w = Math.random() * 2 - 1; last = (last + 0.02 * w) / 1.02; d[i] = w * 0.5 + last * 3; } // between white and brown
    return b;
  }

  setScene(name) {
    if (!THEMES[name]) name = 'night';
    if (name === this.scene && this.ctx) return;
    this.scene = name;
    if (this.ctx) this.applyScene(false);
  }

  /** Music (and the background bed) volume, 0–1 as on the slider. */
  setMusic(v) {
    this.music = v;
    if (this.ctx) this.applyLevels();
  }

  /** Effects volume, 0–1. */
  setEffects(v) {
    this.effects = v;
    if (this.ctx) this.applyLevels();
  }

  get musicOn() { return this.music > 0.005; }

  applyLevels() {
    // Square law (sliders feel even), 1 at the designed level.
    const t = this.ctx.currentTime, m = (this.music / 0.6) ** 2, f = (this.effects / 0.7) ** 2;
    this.musicBus.gain.setTargetAtTime(this.theme.music * m, t, 0.1);
    this.bedBus.gain.setTargetAtTime(m, t, 0.1);
    this.fx.gain.setTargetAtTime(f, t, 0.05);
  }

  applyScene(first) {
    const ctx = this.ctx, t = ctx.currentTime, th = this.theme;
    for (const v of this.padVoices) v.release(t);
    this.padVoices = [];
    if (this.bedNode) { this.bedNode.stop(t + 2); this.bedNode = null; }
    this.chord = 0;
    this.step = 0;
    this.nextTime = t + (first ? 0.1 : 0.6);
    this.delay.delayTime.setValueAtTime((60 / th.bpm) * 0.75, t);
    this.applyLevels();
    if (th.bed) this.bedNode = this.bed(th.bed);
  }

  // ── the generative music ──

  schedule() {
    const ctx = this.ctx;
    if (!ctx || ctx.state !== 'running') return;
    const th = this.theme, sixteenth = 60 / th.bpm / 4;
    while (this.nextTime < ctx.currentTime + 0.25) {
      this.tick(this.step, this.nextTime, sixteenth);
      this.nextTime += sixteenth;
      this.step++;
    }
  }

  tick(step, t, s16) {
    const th = this.theme, bar = 16, perChord = bar * 2;
    if (step % perChord === 0) {
      this.chord = Math.floor(step / perChord) % th.chords.length;
      if (this.musicOn) this.padChord(t, th.chords[this.chord], perChord * s16);
    }
    if (!this.musicOn) return;
    const ch = th.chords[this.chord];
    const pos = step % perChord;
    if (th.arp && pos % th.arp.every === 0 && Math.random() < th.arp.density) {
      const a = th.arp;
      const idx = a.tracker ? step % ch.length : Math.floor(Math.random() * ch.length);
      const up = a.tracker ? (Math.floor(step / ch.length) % 2) * 12 : (Math.random() < 0.3 ? 12 : 0);
      this.tone(t, ch[idx] + a.octave + up, { type: a.type, dur: a.dur, gain: a.gain, cutoff: a.cutoff, attack: 0.005, release: 0.08, dest: this.duckGain, delay: true });
    }
    if (th.bell && pos % 2 === 0 && Math.random() < th.bell.density / 4) {
      const n = ch[1 + Math.floor(Math.random() * (ch.length - 1))] + th.bell.octave;
      this.bell(t, n, th.bell.gain, th.bell.ratio, this.duckGain);
    }
    if (th.pulse && pos % 4 === 0) this.kick(t, th.pulse.gain);
    if (th.bass && pos % 4 === 0) {
      const n = ch[0] - 24 + (pos % 8 === 4 ? 12 : 0);
      this.tone(t, n, { type: th.bass.type, dur: s16 * 3, gain: th.bass.gain, cutoff: 1200, attack: 0.005, release: 0.08, dest: this.duckGain });
    }
  }

  padChord(t, notes, dur) {
    const p = this.theme.pad;
    for (const v of this.padVoices) v.release(t + 0.2);
    this.padVoices = notes.map((n, i) => this.voice(t, n, { ...p, gain: p.gain / (1 + i * 0.15), dest: this.duckGain, hold: dur }));
  }

  /** A sustained pad voice (detuned oscillators through a low-pass); release() fades it. */
  voice(t, midi, { types, cutoff, gain, attack, release, dest, hold, lfo }) {
    const ctx = this.ctx;
    const env = ctx.createGain();
    env.gain.setValueAtTime(0, t);
    env.gain.linearRampToValueAtTime(gain, t + attack);
    const f = ctx.createBiquadFilter();
    f.type = 'lowpass'; f.frequency.value = cutoff; f.Q.value = 0.4;
    f.connect(env).connect(dest);
    const oscs = types.map((type, i) => {
      const o = ctx.createOscillator();
      o.type = type;
      o.frequency.value = mtof(midi);
      o.detune.value = (i % 2 ? 1 : -1) * (5 + i * 3);
      o.connect(f);
      o.start(t);
      return o;
    });
    if (lfo) {
      const l = ctx.createOscillator(), lg = ctx.createGain();
      l.frequency.value = lfo; lg.gain.value = cutoff * 0.6;
      l.connect(lg).connect(f.frequency);
      l.start(t);
      oscs.push(l);
    }
    let done = false;
    const rel = (at) => {
      if (done) return;
      done = true;
      at = Math.max(at, ctx.currentTime);
      env.gain.cancelScheduledValues(at);
      env.gain.setValueAtTime(env.gain.value, at);
      env.gain.setTargetAtTime(0, at, release / 3);
      for (const o of oscs) o.stop(at + release + 0.5);
    };
    if (hold) for (const o of oscs) o.stop(t + hold + release + 2); // safety: never leak
    return { release: rel };
  }

  /** A short note. */
  tone(t, midi, { type = 'sine', dur = 0.2, gain = 0.05, cutoff = 4000, attack = 0.01, release = 0.2, dest, delay = false, glide = 0 }) {
    const ctx = this.ctx;
    const o = ctx.createOscillator(), env = ctx.createGain(), f = ctx.createBiquadFilter();
    o.type = type;
    o.frequency.setValueAtTime(mtof(midi + glide), t);
    if (glide) o.frequency.exponentialRampToValueAtTime(mtof(midi), t + Math.min(0.08, dur));
    f.type = 'lowpass'; f.frequency.value = cutoff;
    env.gain.setValueAtTime(0, t);
    env.gain.linearRampToValueAtTime(gain, t + attack);
    env.gain.setTargetAtTime(0, t + attack + dur, release / 3);
    o.connect(f).connect(env).connect(dest ?? this.fx);
    if (delay) env.connect(this.delay);
    o.start(t);
    o.stop(t + attack + dur + release * 2 + 0.1);
  }

  /** A glassy FM bell. */
  bell(t, midi, gain, ratio, dest) {
    const ctx = this.ctx, fr = mtof(midi);
    const car = ctx.createOscillator(), mod = ctx.createOscillator(), mg = ctx.createGain(), env = ctx.createGain();
    car.frequency.value = fr;
    mod.frequency.value = fr * ratio;
    mg.gain.setValueAtTime(fr * 2.5, t);
    mg.gain.exponentialRampToValueAtTime(fr * 0.05, t + 1.2);
    mod.connect(mg).connect(car.frequency);
    env.gain.setValueAtTime(0, t);
    env.gain.linearRampToValueAtTime(gain, t + 0.005);
    env.gain.exponentialRampToValueAtTime(0.0001, t + 2.5);
    car.connect(env).connect(dest ?? this.fx);
    env.connect(this.delay);
    car.start(t); mod.start(t);
    car.stop(t + 2.6); mod.stop(t + 2.6);
  }

  kick(t, gain) {
    const ctx = this.ctx, o = ctx.createOscillator(), env = ctx.createGain();
    o.frequency.setValueAtTime(110, t);
    o.frequency.exponentialRampToValueAtTime(42, t + 0.18);
    env.gain.setValueAtTime(gain, t);
    env.gain.exponentialRampToValueAtTime(0.0001, t + 0.35);
    o.connect(env).connect(this.duckGain);
    o.start(t); o.stop(t + 0.4);
  }

  /** Filtered noise, shaped. */
  noiseBurst(t, dur, { from, to, q = 2, gain = 0.04, type = 'bandpass', dest }) {
    const ctx = this.ctx, src = ctx.createBufferSource(), f = ctx.createBiquadFilter(), env = ctx.createGain();
    src.buffer = this.noise;
    src.loop = true;
    f.type = type; f.Q.value = q;
    f.frequency.setValueAtTime(from, t);
    f.frequency.exponentialRampToValueAtTime(to, t + dur);
    env.gain.setValueAtTime(0, t);
    env.gain.linearRampToValueAtTime(gain, t + Math.min(0.08, dur / 3));
    env.gain.setValueAtTime(gain, t + Math.max(0.08, dur - 0.15));
    env.gain.linearRampToValueAtTime(0, t + dur);
    src.connect(f).connect(env).connect(dest ?? this.fx);
    src.start(t, Math.random() * 1.5);
    src.stop(t + dur + 0.05);
  }

  /** The background bed: wind, a stream with droplets, or a low hum. Returns {stop(at)}. */
  bed({ kind, gain }) {
    const ctx = this.ctx, t = ctx.currentTime;
    const out = ctx.createGain();
    out.gain.setValueAtTime(0, t);
    out.gain.linearRampToValueAtTime(gain, t + 3);
    out.connect(this.bedBus);
    const nodes = [];
    const src = ctx.createBufferSource();
    src.buffer = this.noise; src.loop = true;
    const f = ctx.createBiquadFilter();
    nodes.push(src);
    if (kind === 'wind') {
      f.type = 'lowpass'; f.frequency.value = 420; f.Q.value = 1.5;
      const l = ctx.createOscillator(), lg = ctx.createGain();
      l.frequency.value = 0.07; lg.gain.value = 220;
      l.connect(lg).connect(f.frequency); l.start(t); nodes.push(l);
    } else if (kind === 'water') {
      f.type = 'bandpass'; f.frequency.value = 1400; f.Q.value = 0.6;
    } else {
      f.type = 'lowpass'; f.frequency.value = 180;
      for (const [fr, g] of [[55, 0.5], [110, 0.2], [165.5, 0.08]]) {
        const o = ctx.createOscillator(), og = ctx.createGain();
        o.frequency.value = fr; og.gain.value = g;
        o.connect(og).connect(out); o.start(t); nodes.push(o);
      }
    }
    src.connect(f).connect(out);
    src.start(t);
    // Water: droplets now and then.
    let drops = null;
    if (kind === 'water') {
      drops = setInterval(() => {
        if (ctx.state !== 'running' || Math.random() > 0.5) return;
        const at = ctx.currentTime + Math.random() * 0.4;
        const o = ctx.createOscillator(), e = ctx.createGain();
        const f0 = 900 + Math.random() * 1400;
        o.frequency.setValueAtTime(f0, at);
        o.frequency.exponentialRampToValueAtTime(f0 * 1.8, at + 0.05);
        e.gain.setValueAtTime(0, at);
        e.gain.linearRampToValueAtTime(gain * 1.2, at + 0.005);
        e.gain.exponentialRampToValueAtTime(0.0001, at + 0.09);
        o.connect(e).connect(out);
        o.start(at); o.stop(at + 0.1);
      }, 350);
    }
    return {
      stop: (at) => {
        out.gain.cancelScheduledValues(ctx.currentTime);
        out.gain.setTargetAtTime(0, ctx.currentTime, 0.5);
        for (const n of nodes) n.stop(at);
        if (drops) clearInterval(drops);
      },
    };
  }

  // ── sounds for what happens on the board ──

  ready() { return this.ctx?.state === 'running' && this.effects > 0.005; }

  /** A note from the current chord, `up` octaves above its middle. */
  chordNote(up = 1, pick) {
    const ch = this.theme.chords[this.chord];
    const n = ch[pick ?? 1 + Math.floor(Math.random() * (ch.length - 1))];
    return n + 12 * up;
  }

  /** Something being drawn for `secs`: a soft pen sweep, and a pluck from the chord. */
  draw(secs) {
    if (!this.ready()) return;
    const t = this.ctx.currentTime, d = Math.min(Math.max(secs, 0.25), 2.5);
    const scene = this.scene;
    const lo = scene === 'demo' ? 2000 : scene === 'tech' ? 1500 : 900, hi = lo * 2.2;
    this.noiseBurst(t, d, { from: lo, to: hi, q: 3, gain: scene === 'aero' ? 0.022 : 0.018 });
    const type = scene === 'demo' ? 'square' : scene === 'tech' ? 'sawtooth' : 'triangle';
    this.tone(t, this.chordNote(1), { type, dur: 0.15, gain: 0.04, cutoff: 2200, attack: 0.005, release: 0.6, delay: scene !== 'night' });
    if (d > 1) this.tone(t + d - 0.1, this.chordNote(2), { type: 'sine', dur: 0.1, gain: 0.025, release: 0.8 });
  }

  /** Something popping into place. */
  pop() {
    if (!this.ready()) return;
    const t = this.ctx.currentTime;
    this.tone(t, this.chordNote(1), { type: 'sine', dur: 0.06, gain: 0.05, attack: 0.003, release: 0.25, glide: -12 });
  }

  /** Something fading in: a quiet bell. */
  fade() {
    if (!this.ready()) return;
    this.bell(this.ctx.currentTime, this.chordNote(2), 0.016, 3.5, this.fx);
  }

  /** A question appears. */
  question() {
    if (!this.ready()) return;
    const t = this.ctx.currentTime, ch = this.theme.chords[this.chord];
    this.bell(t, ch[0] + 24, 0.03, 2, this.fx);
    this.bell(t + 0.16, ch[2] + 24, 0.03, 2, this.fx);
  }

  answer(correct) {
    if (!this.ready()) return;
    const t = this.ctx.currentTime;
    if (correct) [0, 4, 7, 12].forEach((s, i) => this.tone(t + i * 0.09, 72 + s, { type: 'triangle', dur: 0.1, gain: 0.045, release: 0.5 }));
    else [0, -3].forEach((s, i) => this.tone(t + i * 0.18, 62 + s, { type: 'sine', dur: 0.18, gain: 0.04, release: 0.5 }));
  }

  /** Lower the music while the teacher speaks for `secs`. */
  duck(secs) {
    if (!this.ctx) return;
    const t = this.ctx.currentTime, g = this.duckGain.gain;
    g.cancelScheduledValues(t);
    g.setTargetAtTime(0.45, t, 0.15);
    g.setTargetAtTime(1, t + Math.max(0.3, secs), 0.6);
  }
}
