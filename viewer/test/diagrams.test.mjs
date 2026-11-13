// Pure parts of the engineering-diagram support: symbols, routing, code
// highlighting, timing-diagram parsing.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { symbol, route, PART_KINDS } from '../js/parts.js';
import { highlight, parseWave, cells, wavePin } from '../js/panels.js';

const vclBin = fileURLToPath(new URL('../../zig-out/bin/vcl', import.meta.url));

test('parts.js pins match the parser (core/src/vcl.zig part_pins)', () => {
  const zig = readFileSync(new URL('../../core/src/vcl.zig', import.meta.url), 'utf8');
  const table = zig.slice(zig.indexOf('pub const part_pins'), zig.indexOf('const part_kind_names'));
  const pairs = [...table.matchAll(/\.\{ "(\w+)", "([\w,]+)" \}/g)].map((m) => [m[1], m[2]]);
  assert.ok(pairs.length >= 20, 'parsed the Zig table');
  assert.deepEqual(PART_KINDS.slice().sort(), pairs.map((p) => p[0]).sort());
  for (const [kind, pins] of pairs) {
    assert.deepEqual(Object.keys(symbol(kind).pins).sort(), pins.split(',').sort(), kind);
  }
});

test('every symbol has geometry and outward unit pin directions', () => {
  for (const k of PART_KINDS) {
    const s = symbol(k);
    assert.ok(s.lines.length > 0, k);
    for (const [n, p] of Object.entries(s.pins)) assert.equal(Math.hypot(...p.dir), 1, `${k}.${n}`);
  }
});

const P = (x, y, dir) => ({ x, y, z: 0, dir: dir && { x: dir[0], y: dir[1] } });
const xy = (pts) => pts.map((p) => [p.x, p.y]);

test('route: pin-to-pin wires are orthogonal Z / L shapes', () => {
  // right-facing output to left-facing input: Z through the midpoint
  assert.deepEqual(xy(route([P(0, 0, [1, 0]), P(4, 2, [-1, 0])])), [[0, 0], [2, 0], [2, 2], [4, 2]]);
  // horizontal pin to a downward-facing pin: one elbow
  assert.deepEqual(xy(route([P(0, 0, [1, 0]), P(3, 3, [0, -1])])), [[0, 0], [3, 0], [3, 3]]);
  // aligned pins collapse to a straight segment
  assert.deepEqual(xy(route([P(0, 1, [1, 0]), P(5, 1, [-1, 0])])), [[0, 1], [5, 1]]);
  // explicit modes and corner points
  assert.deepEqual(xy(route([P(0, 0), P(2, 3)], 'vh')), [[0, 0], [0, 3], [2, 3]]);
  assert.deepEqual(xy(route([P(0, 0), P(1, 1), P(3, 1)], 'hv')), [[0, 0], [1, 0], [1, 1], [3, 1]]);
  assert.deepEqual(xy(route([P(0, 0), P(2, 3)], 'direct')), [[0, 0], [2, 3]]);
  // every segment is axis-aligned
  const r = route([P(0, 0, [1, 0]), P(-3, 5, [-1, 0]), P(2, -1, [0, 1])]);
  for (let i = 1; i < r.length; i++) assert.ok(r[i].x === r[i - 1].x || r[i].y === r[i - 1].y);
});

test('highlight: keywords, strings, comments, numbers per language', () => {
  const kinds = (src, lang) => highlight(src, lang)[0].filter((t) => t.text.trim()).map((t) => `${t.type}:${t.text.trim()}`);
  assert.deepEqual(kinds('int x = 0x1F; // hi', 'c'), ['kw:int', 'plain:x =', 'num:0x1F', 'plain:;', 'com:// hi']);
  assert.deepEqual(kinds('const s = "a\\"b";', 'zig'), ['kw:const', 'plain:s =', 'str:"a\\"b"', 'plain:;']);
  assert.ok(kinds("assign y = 8'hFF & a;", 'verilog').includes("num:8'hFF"));
  assert.ok(kinds('def f(x):  # c', 'python').includes('com:# c'));
  assert.deepEqual(kinds('  addi x1, x2, 4  ; inc', 'asm'), ['kw:addi', 'type:x1', 'plain:,', 'type:x2', 'plain:,', 'num:4', 'com:; inc']);
  // block comments span lines
  const b = highlight('a /* one\ntwo */ b', 'c');
  assert.equal(b[1][0].type, 'com');
  assert.equal(b[1].at(-1).text.trim(), 'b');
});

test('parseWave: levels, repeats, bus values with labels, clocks', () => {
  const [clk, bus] = parseWave(['clk: p..', 'd[7:0]: x=.=| A5 3C']);
  assert.equal(clk.name, 'clk');
  assert.deepEqual(clk.steps.map((s) => s.v), ['p', 'p', 'p']);
  assert.deepEqual(bus.steps.map((s) => s.v), ['x', '=', '=', '=']);
  assert.deepEqual(bus.steps.filter((s) => s.label !== undefined).map((s) => s.label), ['A5', '3C']);
  assert.throws(() => parseWave(['q: 01q']), /unknown symbol 'q'/);
  assert.deepEqual(cells(['A | B', 'x|y']), [['A', 'B'], ['x', 'y']]);
});

test('wavePin: signal names become the same pins as in the parser', { skip: !existsSync(vclBin) && 'run `zig build` first' }, () => {
  const names = ['clk', 'FIFO write', 'd[7:0]', 'data_in [31:0]', '3v3', 'a-b.c', 'TVALID'];
  assert.deepEqual(names.map(wavePin), ['clk', 'FIFO_write', 'd', 'data_in', 's3v3', 'a_b_c', 'TVALID']);
  assert.equal(wavePin('[7:0]'), null);
  // The parser lists a wave's pins when a point names one it doesn't have.
  const src = `w: wave 0,0 ${names.map((n) => `"${n}: 01"`).join(' ')}\npoint w.nope\n`;
  let out; // exits 1 because of the deliberate error; the diagnostics are on stdout
  try { out = execFileSync(vclBin, ['compile', '-'], { input: src, encoding: 'utf8' }); } catch (e) { out = e.stdout; }
  const msg = out.split('\n').filter(Boolean).map((l) => JSON.parse(l)).find((m) => m.op === 'diag').msg;
  assert.deepEqual(msg.split('its pins are ')[1].split(', '), names.map(wavePin));
});
