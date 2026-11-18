// The teacher's voice: tts-lab (github.com/pa255ion/tts-lab, formant
// synthesis) built as a native library (libtts.so, `zig build unity-tts`) and
// played from the avatar's head. A port of viewer/js/voice.js's robot mode:
// speech is synthesized when a `say` arrives (a few ms per sentence) and the
// say then lasts exactly as long as its audio, so speech and visuals stay in
// step. Modes: robot, off (subtitles only), kept in PlayerPrefs "vcl.voice".
using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Vcl
{
    public class Voice
    {
        const string Lib = "tts";
        [DllImport(Lib)] static extern int tts_speak(byte[] text, UIntPtr n, uint engine, float rate, float pitch);
        [DllImport(Lib)] static extern IntPtr tts_samples();
        [DllImport(Lib)] static extern UIntPtr tts_len();
        [DllImport(Lib)] static extern double tts_seconds();
        [DllImport(Lib)] static extern uint tts_sample_rate();

        const uint RefinedFormant = 1;
        public bool On { get; private set; }
        public float Rate = 1, Pitch = 1;
        public string Error; // why there is no voice, if there isn't

        readonly AudioSource source;
        float level; // smoothed loudness, for the mouth

        public Voice(Transform speaker)
        {
            source = speaker.gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 1;           // positional, from the head
            source.minDistance = 1.5f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.dopplerLevel = 0;
            On = PlayerPrefs.GetString("vcl.voice", "robot") == "robot";
        }

        public void SetOn(bool on)
        {
            On = on;
            PlayerPrefs.SetString("vcl.voice", on ? "robot" : "off");
            PlayerPrefs.Save();
            if (!on) Stop();
        }

        /// <summary>Synthesize a say's text. Returns the clip and how long the say
        /// should last (audio plus a breath), or null when silent.</summary>
        public (AudioClip clip, double dur)? Prepare(string text)
        {
            if (!On || string.IsNullOrWhiteSpace(text) || Error != null) return null;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(Speakable(text));
                if (tts_speak(bytes, (UIntPtr)bytes.Length, RefinedFormant, Rate, Pitch) != 0) return null;
                int n = (int)tts_len();
                uint rate = tts_sample_rate();
                if (n == 0 || rate == 0) return null;
                var samples = new float[n];
                Marshal.Copy(tts_samples(), samples, 0, n);
                var clip = AudioClip.Create("say", n, 1, (int)rate, false);
                clip.SetData(samples, 0);
                return (clip, tts_seconds() + 0.25);
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                Error = $"no speech library ({e.GetType().Name})";
                Debug.LogWarning($"[vcl] voice: {e.Message}");
                return null;
            }
        }

        /// <summary>Say text aloud as the lesson reads it: drop markup the
        /// synthesizer would spell out.</summary>
        static string Speakable(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var ch in text)
                sb.Append(ch == '*' || ch == '_' || ch == '`' || ch == '$' || ch == '\\' ? ' ' : ch);
            return sb.ToString();
        }

        public void Play(AudioClip clip)
        {
            if (!clip || !On) return;
            source.Stop();
            if (source.clip && source.clip != clip) UnityEngine.Object.Destroy(source.clip);
            source.clip = clip;
            source.Play();
        }

        public void Stop()
        {
            if (source.isPlaying) source.Stop();
        }

        public bool Speaking => source.isPlaying;

        /// <summary>How loud the voice is right now, 0..1, to move the mouth.</summary>
        public float Level()
        {
            if (!source.isPlaying || !source.clip) { level = 0; return 0; }
            var clip = source.clip;
            int pos = source.timeSamples, n = Mathf.Min(512, clip.samples - pos);
            if (n <= 0) return level;
            var buf = new float[n];
            clip.GetData(buf, pos);
            float s = 0;
            foreach (var v in buf) s += v * v;
            float rms = Mathf.Sqrt(s / n);
            level = Mathf.Lerp(level, Mathf.Clamp01(rms * 6), 0.5f);
            return level;
        }
    }
}
