// node --test viewer/test
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compile } from '../js/expr.js';
import { Timeline } from '../js/timeline.js';
import { existsSync, readFileSync } from 'node:fs';

const ev = (src, s = {}) => compile(src)({ x: 0, y: 0, t: 0, ...s });

test('expr: precedence, power, unary minus', () => {
  assert.equal(ev('1 + 2*3'), 7);
  assert.equal(ev('2^3^2'), 512);
  assert.equal(ev('-x^2', { x: 3 }), -9);
  assert.equal(ev('(1+2)*(3+4)'), 21);
  assert.equal(ev('2**3'), 8);
});

test('expr: implicit multiplication and functions', () => {
  assert.equal(ev('2x', { x: 4 }), 8);
  assert.equal(ev('3(x+1)', { x: 1 }), 6);
  assert.equal(ev('xy', { x: 2, y: 5 }), 10);
  assert.equal(ev('2sin(0) + cos(0)'), 1);
  assert.equal(ev('max(1, 4, 2)'), 4);
  assert.ok(Math.abs(ev('pi') - Math.PI) < 1e-12);
  assert.ok(Math.abs(ev('sin(x)/x', { x: 1e-9 }) - 1) < 1e-6);
});

test('expr: helpful errors', () => {
  assert.throws(() => compile('sin x'), /needs parentheses/);
  assert.throws(() => compile('foo(x)'), /unknown name 'foo'/);
  assert.throws(() => compile('(x + 1'), /expected '\)'/);
  assert.throws(() => compile('x $ 2'), /unexpected '\$'/);
  assert.throws(() => compile('fetch'), /unknown name/);
});

function run(cmds) {
  const tl = new Timeline((e) => { throw e; });
  const log = [];
  for (const [name, dur, par, now] of cmds) {
    tl.add({ dur, par, begin: () => log.push([name, 'begin']) }, now);
  }
  return { tl, starts: tl.queue.map((t) => t.start) };
}

test('timeline: sequential, parallel, catch-up', () => {
  const { starts } = run([
    ['a', 1, false, 0],
    ['b', 0.5, true, 0], // with a
    ['c', 1, false, 0], // after a (cursor = max(1, .5))
    ['d', 2, true, 0], // with c, ends at 3
    ['e', 1, false, 0], // after d
    ['f', 1, false, 10], // stream fell behind: starts now
  ]);
  assert.deepEqual(starts, [0, 0, 1, 1, 3, 10]);
});

test('timeline: tick runs begin/update/finish in order', () => {
  const tl = new Timeline((e) => { throw e; });
  const log = [];
  const mk = (n, dur, par) => ({
    dur, par,
    begin: () => log.push(`${n}:b`),
    update: (_, raw) => log.push(`${n}:${raw}`),
    finish: () => log.push(`${n}:f`),
  });
  tl.add(mk('a', 1), 0);
  tl.add(mk('b', 1, true), 0);
  tl.add(mk('c', 0), 0);
  tl.tick(0.5);
  tl.tick(1);
  assert.deepEqual(log, ['a:b', 'b:b', 'a:0.5', 'b:0.5', 'c:b', 'a:1', 'a:f', 'b:1', 'b:f', 'c:1', 'c:f']);
});

test('timeline: a failing begin skips only that task', () => {
  const errors = [];
  const tl = new Timeline((e) => errors.push(e.message));
  const log = [];
  tl.add({ dur: 0, begin: () => { throw new Error('boom'); }, update: () => log.push('bad') }, 0);
  tl.add({ dur: 0, begin: () => log.push('ok') }, 0);
  tl.tick(0);
  assert.deepEqual(errors, ['boom']);
  assert.deepEqual(log, ['ok']);
});

test('timeline: a gate holds the rest until release, then everything moves later', () => {
  const tl = new Timeline((e) => { throw e; });
  const log = [];
  const mk = (n, dur, extra = {}) => ({ dur, ...extra, begin: () => log.push(`${n}@${tl.now}`) });
  tl.add(mk('a', 1), 0);
  tl.add(mk('say', 1, { gate: true }), 0); // due at 1
  tl.add(mk('draw', 0.5, { par: true }), 0); // with the say
  tl.add(mk('next', 0), 0); // due at 2
  const at = (t) => { tl.now = t; tl.tick(t); };
  at(0); at(1); at(5);
  assert.deepEqual(log, ['a@0']);
  assert.ok(tl.waiting && tl.busy);
  tl.add(mk('late', 0), 5); // queued while held: still behind the gate, not "now"
  assert.ok(tl.release(5));
  at(5); at(5.9);
  assert.deepEqual(log, ['a@0', 'say@5', 'draw@5']);
  at(6);
  assert.deepEqual(log, ['a@0', 'say@5', 'draw@5', 'next@6', 'late@6']);
  assert.ok(!tl.busy && !tl.release(6));
});

test('timeline: finishThrough skips one step and starts the next now', () => {
  const tl = new Timeline((e) => { throw e; });
  const log = [];
  const mk = (n, step, dur, extra = {}) => ({ dur, step, ...extra, begin: () => log.push(n), finish: () => log.push(`/${n}`) });
  tl.add(mk('say1', 1, 3), 0);
  tl.add(mk('draw1', 1, 1), 0);       // 3..4
  tl.add(mk('say2', 2, 2, { gate: true }), 0); // 4..6, held in step mode
  tl.add(mk('draw2', 2, 1), 0);
  tl.tick(1);
  assert.deepEqual(log, ['say1']);
  // Skip step 1: say1 finishes, draw1 runs at once, say1's speech isn't replayed.
  tl.finishThrough(1, 1, (t) => t === undefined);
  assert.deepEqual(log, ['say1', '/say1', 'draw1', '/draw1']);
  tl.tick(1); // step 2 starts now, without waiting for its gate
  assert.deepEqual(log.slice(4), ['say2']);
  tl.tick(3.5);
  assert.deepEqual(log.slice(4), ['say2', 'draw2', '/say2']);
});

test('timeline: a question gate opens only with force', () => {
  const tl = new Timeline((e) => { throw e; });
  const log = [];
  tl.add({ dur: 0, begin: () => log.push('card') }, 0);
  tl.add({ dur: 0, gate: true, question: true }, 0);
  tl.add({ dur: 0, begin: () => log.push('after') }, 0);
  tl.tick(0); tl.tick(1);
  assert.ok(tl.waiting?.question);
  assert.ok(!tl.release(2)); // the trigger's "next" doesn't answer it
  assert.ok(tl.release(2, true));
  tl.tick(2);
  assert.deepEqual(log, ['card', 'after']);
});

const ttsWasm = new URL('../tts.wasm', import.meta.url);
test('tts.wasm: speech with an exact duration', { skip: !existsSync(ttsWasm) && 'run `zig build` first' }, async () => {
  const { instance } = await WebAssembly.instantiate(readFileSync(ttsWasm), {});
  const x = instance.exports;
  const say = (text, engine = 1, rate = 1) => {
    const b = new TextEncoder().encode(text);
    const p = x.tts_alloc(b.length);
    new Uint8Array(x.memory.buffer, p, b.length).set(b);
    const rc = x.tts_speak(p, b.length, engine, rate, 1);
    x.tts_free(p, b.length);
    return rc;
  };
  assert.equal(say('y equals two x plus b.'), 0);
  assert.equal(x.tts_sample_rate(), 16000);
  const secs = x.tts_seconds();
  assert.ok(secs > 1 && secs < 4, `duration ${secs}`);
  assert.equal(x.tts_len(), Math.round(secs * 16000));
  const s = new Float32Array(x.memory.buffer, x.tts_samples(), x.tts_len());
  assert.ok(s.some((v) => Math.abs(v) > 0.05), 'audible');
  assert.equal(say('y equals two x plus b.', 1, 1.5), 0);
  assert.ok(x.tts_seconds() < secs, 'faster rate is shorter');
  assert.equal(say('hello', 9), -3, 'unknown engine rejected');
});
