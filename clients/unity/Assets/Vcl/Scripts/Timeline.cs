// Timeline scheduler: a port of viewer/js/timeline.js (docs/IR.md, "Scheduling").
//
// Sequential tasks start when the previous finishes (or now, if the stream
// fell behind); `par` tasks start with the previous task. A `gate` task waits,
// once its time comes, until Release() (step-by-step playback): everything
// queued behind it moves later by the time spent waiting.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Vcl
{
    public class Task
    {
        public double Dur;
        public bool Par;
        public string Ease;
        public bool Gate;
        public Cmd Cmd;
        public Action Begin;
        public Action<float, float> Update; // (eased, raw)
        public Action Finish;

        internal double Start, End;
        internal bool Ok;
    }

    public class Timeline
    {
        readonly Action<Exception, Task> onError;
        public Action<Task> OnBegin;

        double cursor, lastStart;
        readonly List<Task> queue = new List<Task>();
        List<Task> active = new List<Task>();
        public Task Waiting { get; private set; }

        public Timeline(Action<Exception, Task> onError)
        {
            this.onError = onError;
            Reset();
        }

        public void Reset()
        {
            cursor = lastStart = double.NegativeInfinity;
            queue.Clear();
            active.Clear();
            Waiting = null;
        }

        public bool Busy => queue.Count > 0 || active.Count > 0;

        public bool Release(double now)
        {
            var g = Waiting;
            if (g == null) return false;
            double shift = Math.Max(0, now - g.Start);
            foreach (var t in queue) { t.Start += shift; t.End += shift; }
            cursor += shift;
            lastStart += shift;
            g.Gate = false;
            Waiting = null;
            return true;
        }

        public void Add(Task task, double now)
        {
            double dur = Math.Max(0, task.Dur);
            double floor = Waiting != null ? double.NegativeInfinity : now;
            double start = Math.Max(task.Par ? lastStart : cursor, floor);
            task.Start = start;
            task.End = start + dur;
            cursor = task.Par ? Math.Max(cursor, task.End) : task.End;
            lastStart = start;
            queue.Add(task);
        }

        public void RunNow(Task t)
        {
            if (Call(t, () => t.Begin?.Invoke()) && Call(t, () => t.Update?.Invoke(1, 1))) Call(t, () => t.Finish?.Invoke());
        }

        public void Tick(double now)
        {
            while (queue.Count > 0 && queue[0].Start <= now)
            {
                if (queue[0].Gate)
                {
                    if (active.Count == 0) Waiting = queue[0];
                    break;
                }
                var t = queue[0];
                queue.RemoveAt(0);
                t.Ok = Call(t, () => t.Begin?.Invoke());
                if (t.Ok) OnBegin?.Invoke(t);
                active.Add(t);
            }
            if (active.Count == 0) return;
            var keep = new List<Task>(active.Count);
            foreach (var t in active)
            {
                if (!t.Ok) continue;
                float raw = t.End > t.Start ? (float)Math.Min(1, (now - t.Start) / (t.End - t.Start)) : 1;
                float eased = Eases.Apply(t.Ease, raw);
                if (!Call(t, () => t.Update?.Invoke(eased, raw))) continue;
                if (raw >= 1) { Call(t, () => t.Finish?.Invoke()); continue; }
                keep.Add(t);
            }
            active = keep;
        }

        public void Flush()
        {
            foreach (var t in active) if (t.Ok && Call(t, () => t.Update?.Invoke(1, 1))) Call(t, () => t.Finish?.Invoke());
            active.Clear();
            var q = new List<Task>(queue);
            queue.Clear();
            foreach (var t in q) RunNow(t);
            cursor = lastStart = double.NegativeInfinity;
            Waiting = null;
        }

        bool Call(Task t, Action f)
        {
            try { f(); return true; }
            catch (Exception e) { onError(e, t); return false; }
        }
    }

    public static class Eases
    {
        public static float Apply(string ease, float p)
        {
            switch (ease)
            {
                case "linear": return p;
                case "in": return p * p * p;
                case "out": return 1 - Mathf.Pow(1 - p, 3);
                default: return p < 0.5f ? 4 * p * p * p : 1 - Mathf.Pow(-2 * p + 2, 3) / 2;
            }
        }

        public static float BackOut(float p)
        {
            const float c = 1.70158f;
            return 1 + (c + 1) * Mathf.Pow(p - 1, 3) + c * Mathf.Pow(p - 1, 2);
        }
    }
}
