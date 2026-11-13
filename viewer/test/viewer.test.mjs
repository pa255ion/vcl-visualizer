// node --test viewer/test
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compile } from '../js/expr.js';
import { Timeline } from '../js/timeline.js';

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
