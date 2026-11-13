// End-to-end test of the tutor against a mock Messages API (no credentials,
// no cost): real `vcl serve`, real tutor process, scripted streamed replies.
//   cd tools/tutor && npm test      (needs `zig build` first)
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '../../..');
const vcl = path.join(root, 'zig-out/bin/vcl');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** Mock /v1/messages: streams each scripted reply in small text deltas. */
function mockApi(replies) {
  const requests = [];
  const server = http.createServer((req, res) => {
    let body = '';
    req.on('data', (d) => (body += d));
    req.on('end', () => {
      requests.push({ headers: req.headers, body: JSON.parse(body) });
      const text = replies[requests.length - 1] ?? 'OK.';
      res.writeHead(200, { 'content-type': 'text/event-stream' });
      const ev = (type, data) => res.write(`event: ${type}\ndata: ${JSON.stringify({ type, ...data })}\n\n`);
      ev('message_start', { message: { id: `msg_${requests.length}`, type: 'message', role: 'assistant', model: 'claude-opus-5-5', content: [], stop_reason: null, stop_sequence: null, usage: { input_tokens: 10, output_tokens: 0 } } });
      ev('content_block_start', { index: 0, content_block: { type: 'text', text: '' } });
      for (let i = 0; i < text.length; i += 7) ev('content_block_delta', { index: 0, delta: { type: 'text_delta', text: text.slice(i, i + 7) } });
      ev('content_block_stop', { index: 0 });
      ev('message_delta', { delta: { stop_reason: 'end_turn', stop_sequence: null }, usage: { output_tokens: 42 } });
      ev('message_stop', {});
      res.end();
    });
  });
  return new Promise((r) => server.listen(0, '127.0.0.1', () => r({ server, requests, port: server.address().port })));
}

async function until(pred, ms = 8000) {
  const t0 = Date.now();
  while (Date.now() - t0 < ms) { if (await pred()) return true; await sleep(50); }
  return false;
}

test('ask → streamed lesson → failed line repaired', { skip: !existsSync(vcl) && 'run `zig build` first' }, async (t) => {
  const port = 18000 + Math.floor(Math.random() * 1000);
  const base = `http://127.0.0.1:${port}`;
  const server = spawn(vcl, ['serve', '--port', String(port)], { cwd: root, stdio: 'ignore' });
  t.after(() => server.kill());
  assert.ok(await until(async () => (await fetch(base + '/scene').catch(() => null))?.ok), 'server up');

  const api = await mockApi([
    'A right triangle has one square corner.\n```vcl\nA: pt 0,0\nB: pt 3,0\nlien A B\n```\nSee the corner?\n',
    '```vcl\nab: line A B\n```\n',
  ]);
  t.after(() => api.server.close());

  // Watch what a viewer would see.
  const seen = [];
  const viewer = new WebSocket(base.replace('http', 'ws') + '/ws');
  viewer.onmessage = (e) => seen.push(JSON.parse(e.data));
  t.after(() => viewer.close());

  const tutor = spawn(process.execPath, [path.join(here, '../tutor.mjs'), '--server', base, '--no-repl'], {
    env: { ...process.env, ANTHROPIC_BASE_URL: `http://127.0.0.1:${api.port}`, ANTHROPIC_API_KEY: 'test-key' },
    stdio: ['ignore', 'ignore', 'pipe'],
  });
  let stderr = '';
  tutor.stderr.on('data', (d) => (stderr += d));
  t.after(() => tutor.kill());
  await sleep(600); // tutor connects its two links

  await fetch(base + '/ask', { method: 'POST', body: 'What is a right triangle?' });

  const names = async () => (await (await fetch(base + '/scene')).json()).cmds.map((c) => c.id);
  assert.ok(await until(async () => (await names()).includes('ab')), `repaired line reached the scene\n${stderr}`);

  // First request: the right model, fallbacks, caching, effort, and a [board] note.
  const [first, second] = api.requests;
  assert.equal(first.body.model, 'claude-opus-5-5');
  assert.equal(first.body.fallbacks, 'default');
  assert.match(first.headers['anthropic-beta'], /server-side-fallback-2026-07-01/);
  assert.deepEqual(first.body.cache_control, { type: 'ephemeral' });
  assert.equal(first.body.output_config.effort, 'medium');
  assert.match(first.body.system, /VCL primer/);
  assert.deepEqual(first.body.messages[0].content.map((b) => b.text), ['[board] (empty)', 'What is a right triangle?']);

  // Second request: append-only history, then the repair note quoting the bad line.
  assert.equal(second.body.messages.length, 3);
  assert.equal(second.body.messages[1].role, 'assistant');
  const note = second.body.messages[2].content;
  assert.match(note, /^\[viewer\]/);
  assert.match(note, /lien A B/);
  assert.match(note, /did you mean 'line'/);

  // The viewer saw the question, thinking/idle status, speech, and drawing.
  const ops = seen.map((m) => m.op);
  assert.ok(seen.some((m) => m.op === 'ask' && m.text === 'What is a right triangle?'));
  assert.ok(seen.some((m) => m.op === 'status' && m.text === 'thinking'));
  assert.ok(seen.some((m) => m.op === 'say' && m.text === 'A right triangle has one square corner.'));
  assert.ok(ops.includes('pt') && ops.includes('line'));
  assert.ok(await until(() => seen.some((m) => m.op === 'status' && m.text === 'idle')));
  assert.deepEqual((await names()).filter((n) => !n.startsWith('_')), ['A', 'B', 'ab']);
});
