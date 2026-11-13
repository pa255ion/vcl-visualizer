// The teacher's voice.
//
//   robot    tts-lab (viewer/tts.wasm, formant synthesis) played from the
//            avatar's head as positional audio; `say` lasts exactly as long
//            as the audio, so speech and visuals stay in step
//   browser  the browser's speechSynthesis (timing stays an estimate)
//   off      subtitles only
//
// Speech is synthesized when a `say` arrives (a few ms per sentence) and
// played when it starts. Browsers only allow audio after a user gesture, so
// until the first click/key/Enter VR the robot voice is silent.
import * as THREE from 'three';

export const VOICE_MODES = ['robot', 'browser', 'off'];
const ENGINE_REFINED_FORMANT = 1;

export class Voice {
  constructor({ camera, speaker, url = 'tts.wasm', log = () => {} }) {
    this.log = log;
    this.url = url;
    this.mode = 'robot';
    try { const m = localStorage.getItem('vcl.voice'); if (VOICE_MODES.includes(m)) this.mode = m; } catch {}
    this.engine = ENGINE_REFINED_FORMANT;
    this.rate = 1.0;
    this.pitch = 1.0;
    this.tts = null; // wasm exports, once loaded
    this.loading = null;

    this.listener = new THREE.AudioListener();
    camera.add(this.listener);
    this.audio = new THREE.PositionalAudio(this.listener);
    this.audio.setRefDistance(1.5);
    speaker.add(this.audio);
    this.analyser = new THREE.AudioAnalyser(this.audio, 64);

    // Unlock audio on the first gesture (autoplay policy).
    const unlock = () => {
      const ctx = this.listener.context;
      if (ctx.state !== 'running') ctx.resume();
    };
    for (const ev of ['pointerdown', 'keydown', 'touchstart']) addEventListener(ev, unlock, { passive: true });
    this.unlock = unlock;

    if (this.mode === 'robot') this.load();
  }

  setMode(m) {
    this.mode = m;
    try { localStorage.setItem('vcl.voice', m); } catch {}
    if (m === 'robot') this.load();
    if (m !== 'browser' && 'speechSynthesis' in window) speechSynthesis.cancel();
  }

  load() {
    this.loading ??= (async () => {
      const res = await fetch(this.url);
      if (!res.ok) throw new Error(`${this.url}: ${res.status}`);
      const { instance } = await WebAssembly.instantiate(await res.arrayBuffer(), {});
      this.tts = instance.exports;
    })().catch((e) => {
      this.log('warning', `robot voice unavailable (${e.message}); using the browser's voice`);
      this.mode = 'browser';
    });
    return this.loading;
  }

  /** Synthesize a say command's audio and set its duration to match (runs on arrival). */
  prepare(cmd) {
    if (this.mode !== 'robot' || !this.tts || !cmd.text) return;
    const x = this.tts;
    const bytes = new TextEncoder().encode(cmd.text);
    const p = x.tts_alloc(bytes.length);
    new Uint8Array(x.memory.buffer, p, bytes.length).set(bytes);
    const rc = x.tts_speak(p, bytes.length, this.engine, this.rate, this.pitch);
    x.tts_free(p, bytes.length);
    if (rc !== 0) { this.log('warning', `voice: synthesis failed (${rc})`); return; }
    const samples = new Float32Array(x.memory.buffer, x.tts_samples(), x.tts_len()).slice();
    cmd.audio = { samples, rate: x.tts_sample_rate() };
    cmd.dur = x.tts_seconds() + 0.25; // a breath between sentences
  }

  /** Start speaking a say command (called when it starts on the timeline). */
  play(cmd) {
    if (this.mode === 'robot' && cmd.audio) {
      const ctx = this.listener.context;
      if (ctx.state !== 'running') return; // no gesture yet
      const buf = ctx.createBuffer(1, cmd.audio.samples.length, cmd.audio.rate);
      buf.copyToChannel(cmd.audio.samples, 0);
      if (this.audio.isPlaying) this.audio.stop();
      this.audio.setBuffer(buf);
      this.audio.play();
    } else if (this.mode === 'browser' && 'speechSynthesis' in window) {
      speechSynthesis.cancel();
      const u = new SpeechSynthesisUtterance(cmd.text);
      u.rate = 1.05;
      speechSynthesis.speak(u);
    }
  }

  stop() {
    if (this.audio.isPlaying) this.audio.stop();
    if ('speechSynthesis' in window) speechSynthesis.cancel();
  }

  /** 0..1 loudness of the robot voice right now (drives the mouth), or null. */
  level() {
    if (this.mode !== 'robot' || !this.audio.isPlaying) return null;
    return Math.min(1, this.analyser.getAverageFrequency() / 90);
  }
}
