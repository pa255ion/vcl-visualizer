#!/usr/bin/env node
// The tutor: connects Claude to a running `vcl serve`.
//
//   learner question (viewer Ask box, voice, or this terminal) ──POST /ask──▶ server
//   server ──{"op":"ask"}──▶ tutor ──▶ Claude (streaming) ──text deltas──▶ server (mixed mode)
//   server ──diagnostics──▶ tutor ──▶ one automatic repair turn for lines that failed
//
//   node tools/tutor/tutor.mjs [--server http://127.0.0.1:8080] [--model claude-opus-5-5]
//                              [--effort low|medium|high|xhigh|max]
//
// Credentials come from the environment (ANTHROPIC_API_KEY, or an `ant auth login`
// profile). Type a question at the prompt, or use the viewer's Ask box.
import Anthropic from '@anthropic-ai/sdk';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import readline from 'node:readline';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const argv = process.argv.slice(2);
const opt = (name, def) => { const i = argv.indexOf(name); return i >= 0 ? argv[i + 1] : def; };
const SERVER = opt('--server', 'http://127.0.0.1:8080').replace(/\/$/, '');
const MODEL = opt('--model', 'claude-opus-5-5');
const EFFORT = opt('--effort', 'medium');
const INTERACTIVE = process.stdin.isTTY && !argv.includes('--no-repl');
const WS = SERVER.replace(/^http/, 'ws') + '/ws';

const PERSONA = `You are a patient, friendly teacher. The learner sees you as a small robot standing beside a board in a shared 3D space (often in VR). Everything you write outside \`\`\`vcl fences is spoken aloud and shown as subtitles; everything inside fences is drawn on the board as it arrives.

How to teach here:
- Speak in plain sentences. No markdown, lists, headings, emoji, or LaTeX outside fences: subtitles show them literally. Put formulas on the board with \`tex\`.
- One or two short sentences, then show what you just said; alternate prose and fences. Use \`point\` and \`hl\` when you say "this" or "here".
- This is a conversation, not a lecture: cover one idea per reply, keep it brief, and when it helps, end by checking understanding or offering where to go next.
- Build on what is already on the board. \`clear\` when you change topic.
- A user message may begin with a [board] line listing the names currently on the board. Only those exist.
- An [assets] line lists files you can show with \`svg\` or \`model\` (use the paths as given).
- A [viewer] message lists VCL lines that failed. Reply with only a \`\`\`vcl fence that redoes them correctly, with no prose.`;

const PRIMER = readFileSync(path.join(here, '../../docs/PRIMER.md'), 'utf8');
const SYSTEM = `${PERSONA}\n\n${PRIMER}`;

const client = new Anthropic();
const messages = []; // append-only conversation history (Anthropic.Beta.BetaMessageParam[])

const log = (...a) => console.error('[tutor]', ...a);

// ───────────── server links ─────────────

/** A WebSocket that reconnects forever and queues sends while down. */
function link(url, { onMessage = () => {}, onOpen = () => {} } = {}) {
  let ws = null, queue = [], delay = 500;
  const connect = () => {
    ws = new WebSocket(url);
    ws.onopen = () => { delay = 500; onOpen(); for (const m of queue) ws.send(m); queue = []; };
    ws.onmessage = (e) => {
      for (const line of String(e.data).split('\n')) if (line.trim()) {
        try { onMessage(JSON.parse(line)); } catch { /* not JSON */ }
      }
    };
    ws.onclose = () => { setTimeout(connect, delay); delay = Math.min(delay * 2, 8000); };
    ws.onerror = () => {};
  };
  connect();
  return { send: (m) => (ws?.readyState === WebSocket.OPEN ? ws.send(m) : queue.push(m)) };
}

async function post(route, body) {
  try { await fetch(SERVER + route, { method: 'POST', body }); } catch (e) { log(`POST ${route} failed: ${e.message}`); }
}

/** Files the teacher can show (the server's assets directory). */
async function assetFiles() {
  try { return (await (await fetch(SERVER + '/assets/index.json')).json()).files ?? []; } catch { return []; }
}

/** Names that exist on the board now (from the server's replay log). */
async function boardNames() {
  try {
    const { cmds } = await (await fetch(SERVER + '/scene')).json();
    const names = new Set();
    for (const c of cmds) {
      if (c.op === 'del') c.ids.forEach((id) => names.delete(id));
      else if (c.id && !c.id.startsWith('_')) names.add(c.id);
    }
    return [...names];
  } catch { return null; }
}

// Producer link: our text goes in here (mixed mode); diagnostics for our lines come back.
// The server numbers lines per connection, so we keep our own copy to quote them.
let lineNo = 0, partial = '', inFence = false, blockTag = null;
const sentLines = new Map();
let turnErrors = [];
const producer = link(`${WS}?listen=0&mode=mixed`, {
  onOpen: () => { lineNo = 0; partial = ''; inFence = false; blockTag = null; sentLines.clear(); },
  onMessage: (m) => {
    if (m.op !== 'diag') return;
    const text = sentLines.get(m.line) ?? '';
    log(`${m.level}: ${m.msg}  ← ${text}`);
    if (m.level === 'error') turnErrors.push({ text, msg: m.msg });
  },
});

function emit(chunk) {
  producer.send(chunk);
  partial += chunk;
  let nl;
  while ((nl = partial.indexOf('\n')) >= 0) {
    const line = partial.slice(0, nl);
    partial = partial.slice(nl + 1);
    sentLines.set(++lineNo, line);
    if (sentLines.size > 2000) sentLines.delete(lineNo - 2000);
    // Mirror the server's parser: inside a <<TAG block nothing is a fence.
    if (blockTag) { if (line.trim() === blockTag) blockTag = null; continue; }
    const open = inFence && /\s<<([A-Za-z_]\w*)\s*$/.exec(line);
    if (open) blockTag = open[1];
    else if (line.trim().startsWith('```')) inFence = !inFence;
  }
}

/** Finish the turn's text cleanly: flush the last line, close a dangling block and fence. */
function endText() {
  if (partial) emit('\n');
  if (blockTag) emit(`${blockTag}\n`);
  if (inFence) emit('```\n');
}

// Listener link: questions from any viewer (or from our own terminal via POST /ask).
link(WS, { onMessage: (m) => { if (m.op === 'ask') enqueue(m.text); } });

// ───────────── turns ─────────────

const queue = [];
let busy = false;
function enqueue(text) {
  queue.push(text);
  if (!busy) pump();
}
async function pump() {
  busy = true;
  while (queue.length) {
    const q = queue.shift();
    if (INTERACTIVE) process.stdout.write(`\n\x1b[2mlearner:\x1b[0m ${q}\n`);
    const names = await boardNames();
    const content = [];
    if (names) content.push({ type: 'text', text: `[board] ${names.length ? names.join(', ') : '(empty)'}` });
    const files = await assetFiles();
    if (files.length) content.push({ type: 'text', text: `[assets] ${files.map((f) => `assets/${f}`).join(', ')}` });
    content.push({ type: 'text', text: q });
    const errors = await turn(content);
    if (errors.length) {
      log(`repairing ${errors.length} line(s)`);
      const list = errors.map((e) => `${e.text}\n  → ${e.msg}`).join('\n');
      await turn(`[viewer] These lines failed and were skipped:\n${list}\nReply with only a \`\`\`vcl fence that redoes them correctly.`);
    }
  }
  busy = false;
  if (INTERACTIVE) rl?.prompt();
}

/** Assistant content as it may be sent back: after a mid-output fallback, drop the
 *  declined attempt's non-text blocks that precede the last fallback marker. */
function echoable(content) {
  const last = content.findLastIndex((b) => b.type === 'fallback');
  if (last < 0) return content;
  return content.filter((b, i) => i > last || b.type === 'text');
}

async function turn(userContent) {
  messages.push({ role: 'user', content: userContent });
  turnErrors = [];
  await post('/status', 'thinking');
  try {
    const stream = client.beta.messages.stream({
      model: MODEL,
      max_tokens: 32000,
      betas: ['server-side-fallback-2026-07-01'],
      fallbacks: 'default', // on a safety-classifier decline, retry on Anthropic's recommended model
      cache_control: { type: 'ephemeral' },
      system: SYSTEM,
      output_config: { effort: EFFORT },
      messages,
    });
    stream.on('text', (delta) => {
      emit(delta);
      if (INTERACTIVE) process.stdout.write(delta);
    });
    const msg = await stream.finalMessage();
    endText();
    if (msg.content.length) messages.push({ role: 'assistant', content: echoable(msg.content) });
    // (An empty refusal leaves the user turn unanswered; the next one merges with it.)
    if (msg.stop_reason === 'refusal') {
      log(`declined (${msg.stop_details?.category ?? 'no category'})`);
      emit("I'm sorry, I can't help with that one. Could we try a different question?\n");
    } else if (msg.stop_reason === 'max_tokens') {
      log('reply hit max_tokens and was cut off');
    }
    const u = msg.usage;
    log(`${msg.model}: ${u.input_tokens} in (+${u.cache_read_input_tokens ?? 0} cached), ${u.output_tokens} out`);
  } catch (err) {
    endText();
    if (err instanceof Anthropic.AuthenticationError) {
      log('no valid API credentials: run `ant auth login` or set ANTHROPIC_API_KEY');
    } else if (err instanceof Anthropic.RateLimitError) {
      log('rate limited; try again shortly');
    } else if (err instanceof Anthropic.APIError) {
      log(`API error ${err.status}: ${err.message}`);
    } else {
      log(`error: ${err.message}`);
    }
    emit("Sorry, I lost my train of thought. Could you ask that again?\n");
    messages.pop(); // nothing was answered; keep the history valid for the next turn
  }
  await new Promise((r) => setTimeout(r, 250)); // let the last diagnostics arrive
  await post('/status', 'idle');
  return turnErrors;
}

// ───────────── terminal ─────────────

let rl = null;
log(`model ${MODEL} (effort ${EFFORT}), server ${SERVER}`);
if (INTERACTIVE) {
  rl = readline.createInterface({ input: process.stdin, output: process.stdout, prompt: '\nask> ' });
  rl.prompt();
  rl.on('line', (line) => {
    const q = line.trim();
    if (q) post('/ask', q); else rl.prompt();
  });
  rl.on('close', () => process.exit(0));
}
