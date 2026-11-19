// Timeline scheduler (see docs/SPEC.md §3).
//
// A task is { dur, par, ease, gate, step, begin(), update(eased, raw), finish() }.
// Sequential tasks start when the previous finishes (or now, if the stream
// fell behind); `par` tasks start with the previous task. A `gate` task waits,
// once its time comes, until release() (step-by-step playback, questions):
// everything queued behind it moves later by the time spent waiting. `step`
// numbers the lesson's steps (a sentence or question and what goes with it),
// so finishThrough() can skip one.

export const EASES = {
  linear: (p) => p,
  in: (p) => p * p * p,
  out: (p) => 1 - Math.pow(1 - p, 3),
  inout: (p) => (p < 0.5 ? 4 * p * p * p : 1 - Math.pow(-2 * p + 2, 3) / 2),
};

export class Timeline {
  constructor(onError = (e) => console.error(e)) {
    this.onError = onError;
    this.onBegin = null; // (task) => void, as each task starts
    this.reset();
  }

  reset() {
    this.cursor = -Infinity;
    this.lastStart = -Infinity;
    this.queue = [];
    this.active = [];
    this.waiting = null; // the gate task being held, if any
  }

  /** Anything playing or still to come. */
  get busy() { return this.queue.length > 0 || this.active.length > 0; }

  /** Let a held gate (and everything after it) go. A question's gate
   *  only opens with `force` (it was answered or skipped). */
  release(now, force = false) {
    const g = this.waiting;
    if (!g || (g.question && !force)) return false;
    const shift = Math.max(0, now - g.start);
    for (const t of this.queue) { t.start += shift; t.end += shift; }
    this.cursor += shift;
    this.lastStart += shift;
    g.gate = false;
    this.waiting = null;
    return true;
  }

  /** Seconds of scheduled work not yet started. */
  backlog(now) {
    return Math.max(0, this.cursor - now);
  }

  add(task, now) {
    const dur = Math.max(0, task.dur || 0);
    // While a gate is held, keep queuing relative to it (release() shifts them all).
    const start = Math.max(task.par ? this.lastStart : this.cursor, this.waiting ? -Infinity : now);
    task.start = start;
    task.end = start + dur;
    this.cursor = task.par ? Math.max(this.cursor, task.end) : task.end;
    this.lastStart = start;
    this.queue.push(task);
  }

  /** Run a task to completion immediately (replays). */
  runNow(task) {
    if (this.#call(task, 'begin')) this.#call(task, 'update', 1, 1) && this.#call(task, 'finish');
  }

  tick(now) {
    while (this.queue.length && this.queue[0].start <= now) {
      if (this.queue[0].gate) {
        // Hold until release(), once whatever is still playing has finished.
        if (!this.active.length) this.waiting = this.queue[0];
        break;
      }
      const t = this.queue.shift();
      t.ok = this.#call(t, 'begin');
      if (t.ok) this.onBegin?.(t);
      this.active.push(t);
    }
    if (!this.active.length) return;
    const ease = (t, p) => (EASES[t.ease] || EASES.inout)(p);
    this.active = this.active.filter((t) => {
      if (!t.ok) return false;
      const raw = t.end > t.start ? Math.min(1, (now - t.start) / (t.end - t.start)) : 1;
      if (!this.#call(t, 'update', ease(t, raw), raw)) return false;
      if (raw >= 1) { this.#call(t, 'finish'); return false; }
      return true;
    });
  }

  /**
   * Skip ahead one step: finish what is playing and everything queued up to
   * and including step `step` now (tasks where `quiet(t)` holds, like speech,
   * are dropped instead), then start what follows now, without its gate.
   */
  finishThrough(step, now, quiet = () => false) {
    for (const t of this.active) if (t.ok) this.#call(t, 'update', 1, 1) && this.#call(t, 'finish');
    this.active = [];
    this.waiting = null;
    while (this.queue.length && (this.queue[0].step ?? 0) <= step) {
      const t = this.queue.shift();
      if (!quiet(t)) this.runNow(t);
    }
    if (!this.queue.length) { this.cursor = this.lastStart = -Infinity; return; }
    const shift = this.queue[0].start - now;
    for (const t of this.queue) { t.start -= shift; t.end -= shift; }
    this.cursor -= shift;
    this.lastStart -= shift;
    if (!this.queue[0].question) this.queue[0].gate = false;
  }

  /** Finish everything immediately (e.g. "skip ahead"). */
  flush() {
    for (const t of this.active) if (t.ok) this.#call(t, 'update', 1, 1) && this.#call(t, 'finish');
    this.active = [];
    for (const t of this.queue) this.runNow(t);
    this.queue = [];
    this.cursor = this.lastStart = -Infinity;
    this.waiting = null;
  }

  #call(t, fn, ...args) {
    try {
      t[fn]?.(...args);
      return true;
    } catch (e) {
      this.onError(e, t);
      return false;
    }
  }
}
