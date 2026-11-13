#!/usr/bin/env node
// Stream a lesson to `vcl serve` in small chunks, the way an LLM's tokens
// would arrive. Diagnostics for this producer are printed as they come back.
//
//   node tools/stream.mjs FILE [--mixed] [--cps 80] [--url ws://127.0.0.1:8080/ws]
//
// .md files default to --mixed (prose + ```vcl fences).
import { readFileSync } from 'node:fs';

const args = process.argv.slice(2);
const file = args.find((a) => !a.startsWith('--') && !/^\d+$/.test(a) && !a.startsWith('ws'));
if (!file) { console.error('usage: stream.mjs FILE [--mixed] [--cps N] [--url WS]'); process.exit(2); }
const opt = (name, def) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : def; };
const text = readFileSync(file, 'utf8');
const mixed = args.includes('--mixed') || file.endsWith('.md') || /^[ \t]*```vcl[ \t]*$/m.test(text);
const cps = Number(opt('--cps', 80)); // characters per second (~20 tokens/s)
const url = `${opt('--url', 'ws://127.0.0.1:8080/ws')}?listen=0&mode=${mixed ? 'mixed' : 'vcl'}`;

const ws = new WebSocket(url);
let opened = false;
ws.onerror = () => { if (!opened) { console.error(`could not connect to ${url}`); process.exit(1); } };
ws.onmessage = (e) => {
  for (const l of String(e.data).split('\n').filter(Boolean)) {
    const d = JSON.parse(l);
    if (d.op === 'diag') console.error(`${file}:${d.line}: ${d.level}: ${d.msg}`);
  }
};
ws.onopen = async () => {
  opened = true;
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  let i = 0;
  while (i < text.length) {
    const n = 3 + Math.floor(Math.random() * 4); // token-ish chunk
    ws.send(text.slice(i, i + n));
    i += n;
    await sleep((n / cps) * 1000);
  }
  ws.send('\n');
  await sleep(300);
  ws.close();
};
