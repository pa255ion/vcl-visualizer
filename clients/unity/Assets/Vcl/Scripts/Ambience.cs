// Ambience: generative music, a background sound bed and soft sounds as
// things are drawn, synthesized on the audio thread (OnAudioFilterRead). The
// twin of viewer/js/ambience.js: same keys, tempos and instruments per scene,
// drawing sounds taken from the chord playing at that moment, and the music
// dips while the teacher speaks.
//
// The main thread only posts requests (scene, volumes, sounds); the audio thread
// owns every voice, the sequencer and the effects.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Vcl
{
    [RequireComponent(typeof(AudioSource))]
    public class Ambience : MonoBehaviour
    {
        // ── themes ──

        enum Wave { Sine, Triangle, Saw, Square }
        enum Bus { Music, Fx, Bed }

        class Theme
        {
            public float Bpm, Music;
            public int[][] Chords;
            public Wave[] PadWaves; public float PadCutoff, PadGain, PadAttack, PadRelease, PadLfo;
            public bool Bell; public float BellDensity, BellGain, BellRatio; public int BellOctave;
            public bool Arp; public Wave ArpWave; public float ArpDensity, ArpGain, ArpCutoff, ArpDur; public int ArpOctave; public bool Tracker;
            public float Pulse; // kick gain (0: none)
            public float Bass;  // bass gain (0: none)
            public string Bed; public float BedGain;
        }

        static readonly Dictionary<string, Theme> Themes = new Dictionary<string, Theme>
        {
            ["night"] = new Theme
            {
                Bpm = 58, Music = 0.5f,
                Chords = new[] { new[] { 50, 57, 62, 65, 69 }, new[] { 46, 53, 58, 62, 69 }, new[] { 41, 48, 57, 60, 64 }, new[] { 48, 55, 60, 64, 67 } },
                PadWaves = new[] { Wave.Triangle, Wave.Sine }, PadCutoff = 900, PadGain = 0.05f, PadAttack = 2.5f, PadRelease = 3.5f,
                Bell = true, BellDensity = 0.08f, BellOctave = 12, BellGain = 0.022f, BellRatio = 3.5f,
                Bed = "wind", BedGain = 0.025f,
            },
            ["aero"] = new Theme
            {
                Bpm = 84, Music = 0.55f,
                Chords = new[] { new[] { 48, 55, 64, 67, 71, 74 }, new[] { 45, 52, 60, 64, 67, 71 }, new[] { 41, 48, 57, 60, 64, 67 }, new[] { 43, 50, 59, 62, 65, 69 } },
                PadWaves = new[] { Wave.Sine, Wave.Triangle }, PadCutoff = 2600, PadGain = 0.04f, PadAttack = 1.6f, PadRelease = 2.5f,
                Bell = true, BellDensity = 0.22f, BellOctave = 24, BellGain = 0.018f, BellRatio = 2f,
                Bed = "water", BedGain = 0.02f,
            },
            ["tech"] = new Theme
            {
                Bpm = 100, Music = 0.5f,
                Chords = new[] { new[] { 45, 52, 57, 60, 64 }, new[] { 41, 48, 53, 57, 64 }, new[] { 48, 55, 60, 64, 67 }, new[] { 43, 50, 55, 59, 62 } },
                PadWaves = new[] { Wave.Saw, Wave.Saw }, PadCutoff = 650, PadGain = 0.025f, PadAttack = 2, PadRelease = 3, PadLfo = 0.07f,
                Arp = true, ArpWave = Wave.Saw, ArpDensity = 0.55f, ArpGain = 0.03f, ArpCutoff = 1800, ArpOctave = 12, ArpDur = 0.12f,
                Pulse = 0.09f, Bed = "hum", BedGain = 0.02f,
            },
            ["demo"] = new Theme
            {
                Bpm = 112, Music = 0.45f,
                Chords = new[] { new[] { 57, 60, 64 }, new[] { 53, 57, 60 }, new[] { 55, 59, 62 }, new[] { 52, 55, 59 } },
                PadWaves = new[] { Wave.Square }, PadCutoff = 900, PadGain = 0.012f, PadAttack = 1, PadRelease = 2,
                Arp = true, ArpWave = Wave.Square, ArpDensity = 1, ArpGain = 0.022f, ArpCutoff = 3500, ArpOctave = 12, ArpDur = 0.1f, Tracker = true,
                Bass = 0.08f,
            },
        };


        // ── main thread API ──

        public string Scene { get; private set; } = "night";
        /// <summary>Slider positions, 0–1; 0.6 and 0.7 are the designed levels.</summary>
        public float MusicVolume { get; private set; } = 0.6f;
        public float EffectsVolume { get; private set; } = 0.7f;
        readonly object gate = new object();
        List<Action> pending = new List<Action>(), swap = new List<Action>();
        void Post(Action a) { lock (gate) pending.Add(a); }

        public void SetScene(string s) { if (!Themes.ContainsKey(s)) s = "night"; Scene = s; Post(() => ApplyScene(s)); }
        public void SetMusicVolume(float v) { MusicVolume = Mathf.Clamp01(v); Post(() => musicVol = MusicVolume); }
        public void SetEffectsVolume(float v) { EffectsVolume = Mathf.Clamp01(v); Post(() => fxVol = EffectsVolume); }
        public void DrawSound(float secs) => Post(() => DoDraw(secs));
        public void Pop() => Post(DoPop);
        public void Fade() => Post(() => { if (fxVol > 0.005f) BellVoice(ChordNote(2), 0.016f, 3.5f, Bus.Fx); });
        public void Question() => Post(DoQuestion);
        public void Answer(bool correct) => Post(() => DoAnswer(correct));
        public void Duck(float secs) => Post(() => duckUntil = time + Math.Max(0.3, secs));

        int rate = 48000;
        AudioSource source;

        void Awake()
        {
            rate = AudioSettings.outputSampleRate;
            source = GetComponent<AudioSource>();
            source.spatialBlend = 0;
            source.playOnAwake = false;
            source.loop = true;
            source.clip = AudioClip.Create("ambience", rate, 1, rate, false); // silent: the sound is made in OnAudioFilterRead
            reverb = new Reverb(rate);
            delayBuf = new float[rate];
            ApplyScene(Scene);
            source.Play();
        }

        // ── audio thread state ──

        float musicVol = 0.6f, fxVol = 0.7f;
        bool MusicOn => musicVol > 0.005f;
        Theme th = Themes["night"];
        double time, nextStep, duckUntil = -1;
        int step, chord;
        readonly List<Voice> voices = new List<Voice>();
        readonly System.Random rnd = new System.Random();
        float musicLvl, bedLvl, fxLvl = 1, duck = 1;
        Reverb reverb;
        float[] delayBuf; int delayPos; float delayTime = 0.5f;
        uint noiseSeed = 22222;
        double nextDrop;

        float R() => (float)rnd.NextDouble();
        static float Mtof(float m) => 440f * Mathf.Pow(2, (m - 69) / 12f);

        void ApplyScene(string s)
        {
            th = Themes[s];
            foreach (var v in voices) if (v.Bus != Bus.Fx) v.ReleaseAt(time);
            step = 0; chord = 0;
            nextStep = time + 0.6;
            delayTime = 60f / th.Bpm * 0.75f;
            if (th.Bed != null) voices.Add(new BedVoice(this, th.Bed, th.BedGain));
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            lock (gate) { var t = pending; pending = swap; swap = t; }
            foreach (var a in swap) { try { a(); } catch (Exception e) { Debug.LogException(e); } }
            swap.Clear();

            double dt = 1.0 / rate;
            int frames = data.Length / channels;
            float sixteenth = 60f / th.Bpm / 4f;
            float kLvl = 1 - Mathf.Exp(-1f / (0.4f * rate)), kDuckDown = 1 - Mathf.Exp(-1f / (0.15f * rate)), kDuckUp = 1 - Mathf.Exp(-1f / (0.6f * rate));
            int dlen = Mathf.Clamp((int)(delayTime * rate), 1, delayBuf.Length - 1);
            for (int f = 0; f < frames; f++)
            {
                while (nextStep <= time) { Tick(step, nextStep, sixteenth); nextStep += sixteenth; step++; }
                // Square law (the sliders feel even), 1 at the designed level.
                float m = (musicVol / 0.6f) * (musicVol / 0.6f), fxT = (fxVol / 0.7f) * (fxVol / 0.7f);
                float musicT = th.Music * m, bedT = m;
                musicLvl += (musicT - musicLvl) * kLvl; bedLvl += (bedT - bedLvl) * kLvl; fxLvl += (fxT - fxLvl) * kLvl * 4;
                float duckT = time < duckUntil ? 0.45f : 1;
                duck += (duckT - duck) * (duckT < duck ? kDuckDown : kDuckUp);

                float music = 0, fx = 0, bed = 0, dsend = 0;
                for (int i = voices.Count - 1; i >= 0; i--)
                {
                    var v = voices[i];
                    float s = v.Next(dt);
                    if (v.Done) { voices.RemoveAt(i); continue; }
                    if (v.Bus == Bus.Music) music += s; else if (v.Bus == Bus.Fx) fx += s; else bed += s;
                    if (v.Delay) dsend += s;
                }
                music *= musicLvl * duck; fx *= fxLvl; bed *= bedLvl;
                // Echo (arpeggios and bells), then the hall.
                float echoed = delayBuf[(delayPos - dlen + delayBuf.Length) % delayBuf.Length];
                delayBuf[delayPos] = dsend * Mathf.Max(musicLvl / Mathf.Max(th.Music, 0.01f), fxLvl) * 0.8f + echoed * 0.32f;
                delayPos = (delayPos + 1) % delayBuf.Length;
                float dry = music + fx + bed + echoed * 0.35f;
                reverb.Process((music + fx) * 0.5f, out float wl, out float wr);
                float l = Soft((dry + wl) * 0.9f), r = Soft((dry + wr) * 0.9f);
                if (channels >= 2) { data[f * channels] += l; data[f * channels + 1] += r; for (int c = 2; c < channels; c++) data[f * channels + c] += (l + r) * 0.5f; }
                else data[f] += (l + r) * 0.5f;
                time += dt;
            }
        }

        static float Soft(float x) => x > 1.5f ? 1 : x < -1.5f ? -1 : x - x * x * x / 6.75f;

        // ── the generative music ──

        void Tick(int st, double t, float s16)
        {
            const int bar = 16, perChord = bar * 2;
            if (st % perChord == 0)
            {
                chord = st / perChord % th.Chords.Length;
                if (MusicOn) PadChord(t, th.Chords[chord], perChord * s16);
            }
            if (!MusicOn) return;
            var ch = th.Chords[chord];
            int pos = st % perChord;
            if (th.Arp && R() < th.ArpDensity)
            {
                int idx = th.Tracker ? st % ch.Length : rnd.Next(ch.Length);
                int up = th.Tracker ? (st / ch.Length % 2) * 12 : (R() < 0.3f ? 12 : 0);
                voices.Add(new Tone(this, ch[idx] + th.ArpOctave + up, th.ArpWave, th.ArpDur, th.ArpGain, th.ArpCutoff, 0.005f, 0.08f, Bus.Music) { Delay = true });
            }
            if (th.Bell && pos % 2 == 0 && R() < th.BellDensity / 4)
                BellVoice(ch[1 + rnd.Next(ch.Length - 1)] + th.BellOctave, th.BellGain, th.BellRatio, Bus.Music);
            if (th.Pulse > 0 && pos % 4 == 0) voices.Add(new Kick(this, th.Pulse));
            if (th.Bass > 0 && pos % 4 == 0)
                voices.Add(new Tone(this, ch[0] - 24 + (pos % 8 == 4 ? 12 : 0), Wave.Triangle, s16 * 3, th.Bass, 1200, 0.005f, 0.08f, Bus.Music));
        }

        void PadChord(double t, int[] notes, float dur)
        {
            foreach (var v in voices) if (v is Pad) v.ReleaseAt(time + 0.2);
            for (int i = 0; i < notes.Length; i++)
                voices.Add(new Pad(this, notes[i], th.PadWaves, th.PadCutoff, th.PadGain / (1 + i * 0.15f), th.PadAttack, th.PadRelease, th.PadLfo, dur + 0.2f));
        }

        void BellVoice(float midi, float gain, float ratio, Bus bus) => voices.Add(new Bell(this, midi, gain, ratio, bus) { Delay = true });

        int ChordNote(int up, int pick = -1)
        {
            var ch = th.Chords[chord];
            return ch[pick >= 0 ? pick : 1 + rnd.Next(ch.Length - 1)] + 12 * up;
        }

        // ── sounds for the board ──

        void DoDraw(float secs)
        {
            if (fxVol <= 0.005f) return;
            float d = Mathf.Clamp(secs, 0.25f, 2.5f);
            float lo = Scene == "demo" ? 2000 : Scene == "tech" ? 1500 : 900;
            voices.Add(new NoiseBurst(this, d, lo, lo * 2.2f, 3, Scene == "aero" ? 0.022f : 0.018f));
            var w = Scene == "demo" ? Wave.Square : Scene == "tech" ? Wave.Saw : Wave.Triangle;
            voices.Add(new Tone(this, ChordNote(1), w, 0.15f, 0.04f, 2200, 0.005f, 0.6f, Bus.Fx) { Delay = Scene != "night" });
            if (d > 1) voices.Add(new Tone(this, ChordNote(2), Wave.Sine, 0.1f, 0.025f, 4000, 0.01f, 0.8f, Bus.Fx) { StartAt = time + d - 0.1 });
        }

        void DoPop()
        {
            if (fxVol <= 0.005f) return;
            voices.Add(new Tone(this, ChordNote(1), Wave.Sine, 0.06f, 0.05f, 4000, 0.003f, 0.25f, Bus.Fx) { Glide = -12 });
        }

        void DoQuestion()
        {
            if (fxVol <= 0.005f) return;
            var ch = th.Chords[chord];
            voices.Add(new Bell(this, ch[0] + 24, 0.03f, 2, Bus.Fx));
            voices.Add(new Bell(this, ch[2] + 24, 0.03f, 2, Bus.Fx) { StartAt = time + 0.16 });
        }

        void DoAnswer(bool correct)
        {
            if (fxVol <= 0.005f) return;
            if (correct) { int[] s = { 0, 4, 7, 12 }; for (int i = 0; i < 4; i++) voices.Add(new Tone(this, 72 + s[i], Wave.Triangle, 0.1f, 0.045f, 4000, 0.01f, 0.5f, Bus.Fx) { StartAt = time + i * 0.09 }); }
            else { voices.Add(new Tone(this, 62, Wave.Sine, 0.18f, 0.04f, 4000, 0.01f, 0.5f, Bus.Fx)); voices.Add(new Tone(this, 59, Wave.Sine, 0.18f, 0.04f, 4000, 0.01f, 0.5f, Bus.Fx) { StartAt = time + 0.18 }); }
        }

        float Noise()
        {
            noiseSeed ^= noiseSeed << 13; noiseSeed ^= noiseSeed >> 17; noiseSeed ^= noiseSeed << 5;
            return noiseSeed / (float)uint.MaxValue * 2 - 1;
        }

        // ── voices ──

        abstract class Voice
        {
            protected readonly Ambience a;
            public Bus Bus;
            public bool Delay, Done;
            public double StartAt = -1;
            protected double age;
            protected Voice(Ambience a, Bus bus) { this.a = a; Bus = bus; }
            public float Next(double dt)
            {
                if (StartAt > a.time) return 0;
                float s = Sample(dt);
                age += dt;
                return s;
            }
            protected abstract float Sample(double dt);
            public virtual void ReleaseAt(double t) { }
        }

        /// <summary>Topology-preserving state-variable filter (Zavalishin).</summary>
        struct Svf
        {
            float ic1, ic2, g, k, a1, a2, a3;
            public void Set(float fc, float q, int rate)
            {
                fc = Mathf.Clamp(fc, 20, rate * 0.45f);
                g = Mathf.Tan(Mathf.PI * fc / rate); k = 1 / Mathf.Max(q, 0.05f);
                a1 = 1 / (1 + g * (g + k)); a2 = g * a1; a3 = g * a2;
            }
            public float Low(float v0) { Run(v0, out var lo, out _); return lo; }
            public float Band(float v0) { Run(v0, out _, out var bp); return bp; }
            void Run(float v0, out float lo, out float bp)
            {
                float v3 = v0 - ic2, v1 = a1 * ic1 + a2 * v3, v2 = ic2 + a2 * ic1 + a3 * v3;
                ic1 = 2 * v1 - ic1; ic2 = 2 * v2 - ic2;
                lo = v2; bp = v1;
            }
        }

        static float Blep(double t, double dt)
        {
            if (t < dt) { t /= dt; return (float)(t + t - t * t - 1); }
            if (t > 1 - dt) { t = (t - 1) / dt; return (float)(t * t + t + t + 1); }
            return 0;
        }

        static float Osc(Wave w, ref double ph, double inc)
        {
            ph += inc; if (ph >= 1) ph -= 1;
            switch (w)
            {
                case Wave.Sine: return Mathf.Sin((float)(ph * 2 * Math.PI));
                case Wave.Triangle: return (float)(4 * Math.Abs(ph - 0.5) - 1);
                case Wave.Saw: return (float)(2 * ph - 1) - Blep(ph, inc);
                default: { float s = ph < 0.5 ? 1 : -1; s += Blep(ph, inc); s -= Blep((ph + 0.5) % 1, inc); return s; }
            }
        }

        /// <summary>A short note: oscillator → low-pass → attack, hold, exponential release.</summary>
        class Tone : Voice
        {
            readonly Wave w; readonly float freq, dur, gain, attack, tau; double ph; Svf f; public float Glide;
            public Tone(Ambience a, float midi, Wave w, float dur, float gain, float cutoff, float attack, float release, Bus bus) : base(a, bus)
            {
                this.w = w; freq = Mtof(midi); this.dur = dur; this.gain = gain; this.attack = attack; tau = release / 3;
                f.Set(cutoff, 0.7f, a.rate);
            }
            protected override float Sample(double dt)
            {
                float fr = Glide != 0 ? freq * Mathf.Pow(2, Glide / 12f * Mathf.Clamp01(1 - (float)age / Mathf.Min(0.08f, dur))) : freq;
                float s = f.Low(Osc(w, ref ph, fr * dt));
                float env = age < attack ? (float)age / attack : age < attack + dur ? 1 : Mathf.Exp(-(float)(age - attack - dur) / tau);
                if (age > attack + dur + tau * 9) Done = true;
                return s * env * gain;
            }
        }

        /// <summary>A sustained pad note: detuned oscillators through a (slowly swept) low-pass.</summary>
        class Pad : Voice
        {
            readonly Wave[] ws; readonly double[] ph, inc; readonly float gain, attack, tau, cutoff, lfo; Svf f; double releaseAt; float env, relFrom; int n;
            public Pad(Ambience a, float midi, Wave[] ws, float cutoff, float gain, float attack, float release, float lfo, float hold) : base(a, Bus.Music)
            {
                this.ws = ws; this.gain = gain; this.attack = attack; tau = release / 3; this.cutoff = cutoff; this.lfo = lfo;
                ph = new double[ws.Length]; inc = new double[ws.Length];
                for (int i = 0; i < ws.Length; i++) { inc[i] = Mtof(midi + (i % 2 == 1 ? 1 : -1) * (5 + i * 3) / 100f) / a.rate; ph[i] = a.R(); }
                releaseAt = a.time + hold;
                f.Set(cutoff, 0.4f, a.rate);
            }
            public override void ReleaseAt(double t) { if (t < releaseAt) releaseAt = t; }
            protected override float Sample(double dt)
            {
                if (lfo > 0 && (n++ & 31) == 0) f.Set(cutoff * (1 + 0.6f * Mathf.Sin((float)(a.time * lfo * 2 * Math.PI))), 0.4f, a.rate);
                float s = 0;
                for (int i = 0; i < ws.Length; i++) s += Osc(ws[i], ref ph[i], inc[i]);
                s = f.Low(s);
                if (a.time < releaseAt) { env = Mathf.Min(1, (float)age / attack); relFrom = env; }
                else { env = relFrom * Mathf.Exp(-(float)(a.time - releaseAt) / tau); if (env < 1e-4f) Done = true; }
                return s * env * gain;
            }
        }

        /// <summary>A glassy FM bell.</summary>
        class Bell : Voice
        {
            readonly float fr, gain, ratio; double pc, pm;
            public Bell(Ambience a, float midi, float gain, float ratio, Bus bus) : base(a, bus) { fr = Mtof(midi); this.gain = gain; this.ratio = ratio; }
            protected override float Sample(double dt)
            {
                float t = (float)age;
                float index = 2.5f * Mathf.Exp(-t * 3.26f); // 2.5 → 0.05 over 1.2 s
                pm += fr * ratio * dt; if (pm >= 1) pm -= 1;
                float mod = Mathf.Sin((float)(pm * 2 * Math.PI)) * index;
                pc += fr * dt; if (pc >= 1) pc -= 1;
                float env = t < 0.005f ? t / 0.005f : Mathf.Exp(-(t - 0.005f) / 0.27f);
                if (t > 2.6f) Done = true;
                return Mathf.Sin((float)(pc * 2 * Math.PI) + mod) * env * gain;
            }
        }

        class Kick : Voice
        {
            readonly float gain; double ph;
            public Kick(Ambience a, float gain) : base(a, Bus.Music) { this.gain = gain; }
            protected override float Sample(double dt)
            {
                float t = (float)age;
                float fr = 110 * Mathf.Pow(42f / 110f, Mathf.Clamp01(t / 0.18f));
                ph += fr * dt; if (ph >= 1) ph -= 1;
                if (t > 0.4f) Done = true;
                return Mathf.Sin((float)(ph * 2 * Math.PI)) * gain * Mathf.Exp(-t / 0.038f);
            }
        }

        /// <summary>Band-passed noise with a rising sweep (a pen on the board).</summary>
        class NoiseBurst : Voice
        {
            readonly float dur, from, to, q, gain; Svf f; int n;
            public NoiseBurst(Ambience a, float dur, float from, float to, float q, float gain) : base(a, Bus.Fx)
            { this.dur = dur; this.from = from; this.to = to; this.q = q; this.gain = gain; f.Set(from, q, a.rate); }
            protected override float Sample(double dt)
            {
                float t = (float)age;
                if ((n++ & 31) == 0) f.Set(from * Mathf.Pow(to / from, Mathf.Clamp01(t / dur)), q, a.rate);
                float rise = Mathf.Min(0.08f, dur / 3);
                float env = t < rise ? t / rise : t < dur - 0.15f ? 1 : Mathf.Clamp01((dur - t) / 0.15f);
                if (t >= dur) Done = true;
                return f.Band(a.Noise()) * env * gain * 2;
            }
        }

        /// <summary>The background: wind, a stream with droplets, or a low hum. Fades in, and out when released.</summary>
        class BedVoice : Voice
        {
            readonly string kind; readonly float gain; Svf f; float brown; double p1, p2, p3; int n; double releaseAt = double.MaxValue;
            public BedVoice(Ambience a, string kind, float gain) : base(a, Bus.Bed)
            {
                this.kind = kind; this.gain = gain;
                if (kind == "wind") f.Set(420, 1.5f, a.rate); else if (kind == "water") f.Set(1400, 0.6f, a.rate); else f.Set(180, 0.7f, a.rate);
            }
            public override void ReleaseAt(double t) { if (t < releaseAt) releaseAt = t; }
            protected override float Sample(double dt)
            {
                float w = a.Noise();
                brown = (brown + 0.02f * w) / 1.02f;
                float nz = w * 0.5f + brown * 3;
                float s;
                if (kind == "wind")
                {
                    if ((n++ & 63) == 0) f.Set(420 + 220 * Mathf.Sin((float)(a.time * 0.07 * 2 * Math.PI)), 1.5f, a.rate);
                    s = f.Low(nz);
                }
                else if (kind == "water")
                {
                    s = f.Band(nz);
                    // Droplets now and then.
                    if (a.time >= a.nextDrop)
                    {
                        a.nextDrop = a.time + 0.35 + a.R() * 0.4;
                        if (a.R() < 0.5f) a.voices.Add(new Droplet(a, 900 + a.R() * 1400, gain * 1.2f));
                    }
                }
                else
                {
                    s = f.Low(nz);
                    p1 += 55 * dt; p2 += 110 * dt; p3 += 165.5 * dt;
                    s += Mathf.Sin((float)(p1 % 1 * 2 * Math.PI)) * 0.5f + Mathf.Sin((float)(p2 % 1 * 2 * Math.PI)) * 0.2f + Mathf.Sin((float)(p3 % 1 * 2 * Math.PI)) * 0.08f;
                }
                float env = Mathf.Min(1, (float)age / 3);
                if (a.time > releaseAt) { env *= Mathf.Exp(-(float)(a.time - releaseAt) / 0.5f); if (a.time - releaseAt > 3) Done = true; }
                return s * env * gain;
            }
        }

        class Droplet : Voice
        {
            readonly float f0, gain; double ph;
            public Droplet(Ambience a, float f0, float gain) : base(a, Bus.Bed) { this.f0 = f0; this.gain = gain; }
            protected override float Sample(double dt)
            {
                float t = (float)age;
                ph += f0 * Mathf.Pow(1.8f, Mathf.Clamp01(t / 0.05f)) * dt; if (ph >= 1) ph -= 1;
                if (t > 0.1f) Done = true;
                float env = t < 0.005f ? t / 0.005f : Mathf.Exp(-(t - 0.005f) / 0.0092f);
                return Mathf.Sin((float)(ph * 2 * Math.PI)) * env * gain;
            }
        }

        /// <summary>A small Freeverb: four combs and two all-passes per side.</summary>
        class Reverb
        {
            readonly float[][] comb = new float[8][]; readonly int[] cpos = new int[8]; readonly float[] cstore = new float[8];
            readonly float[][] ap = new float[4][]; readonly int[] apos = new int[4];
            public Reverb(int rate)
            {
                float k = rate / 44100f;
                int[] cl = { 1116, 1188, 1277, 1356 }, al = { 556, 441 };
                for (int i = 0; i < 4; i++) { comb[i] = new float[(int)(cl[i] * k)]; comb[i + 4] = new float[(int)((cl[i] + 23) * k)]; }
                for (int i = 0; i < 2; i++) { ap[i] = new float[(int)(al[i] * k)]; ap[i + 2] = new float[(int)((al[i] + 23) * k)]; }
            }
            public void Process(float x, out float l, out float r)
            {
                x *= 0.03f;
                l = r = 0;
                for (int i = 0; i < 8; i++)
                {
                    var b = comb[i]; float y = b[cpos[i]];
                    cstore[i] = y * 0.8f + cstore[i] * 0.2f;
                    b[cpos[i]] = x + cstore[i] * 0.84f;
                    cpos[i] = (cpos[i] + 1) % b.Length;
                    if (i < 4) l += y; else r += y;
                }
                for (int i = 0; i < 4; i++)
                {
                    var b = ap[i]; float bo = b[apos[i]];
                    float inp = i < 2 ? l : r;
                    float o = -inp + bo;
                    b[apos[i]] = inp + bo * 0.5f;
                    apos[i] = (apos[i] + 1) % b.Length;
                    if (i < 2) l = o; else r = o;
                }
            }
        }
    }
}
