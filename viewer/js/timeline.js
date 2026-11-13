// Timeline scheduler (see docs/SPEC.md §3).
//
// A task is { dur, par, ease, begin(), update(eased, raw), finish() }.
// Sequential tasks start when the previous finishes (or now, if the stream
// fell behind); `par` tasks start with the previous task.

export const EASES = {
  linear: (p) => p,
  in: (p) => p * p * p,
  out: (p) => 1 - Math.pow(1 - p, 3),
  inout: (p) => (p < 0.5 ? 4 * p * p * p : 1 - Math.pow(-2 * p + 2, 3) / 2),
};

export class Timeline {
  constructor(onError = (e) => console.error(e)) {
    this.onError = onError;
    this.reset();
  }

  reset() {
    this.cursor = -Infinity;
    this.lastStart = -Infinity;
    this.queue = [];
    this.active = [];
  }

  /** Seconds of scheduled work not yet started. */
  backlog(now) {
    return Math.max(0, this.cursor - now);
  }

  add(task, now) {
    const dur = Math.max(0, task.dur || 0);
    const start = Math.max(task.par ? this.lastStart : this.cursor, now);
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
      const t = this.queue.shift();
      t.ok = this.#call(t, 'begin');
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

  /** Finish everything immediately (e.g. "skip ahead"). */
  flush() {
    for (const t of this.active) if (t.ok) this.#call(t, 'update', 1, 1) && this.#call(t, 'finish');
    this.active = [];
    for (const t of this.queue) this.runNow(t);
    this.queue = [];
    this.cursor = this.lastStart = -Infinity;
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
