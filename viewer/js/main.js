// Viewer bootstrap: room, board, teacher, XR, UI, and the command source
// (live server over WebSocket, or the local wasm parser when offline).
//
// URL parameters:
//   ?server=ws://host:8080/ws   connect to a different stream server
//   ?offline=1                  never connect; parse locally
//   ?lesson=NAME                load lessons/NAME.vcl (or .md) and play it
//   ?skip=1                     jump straight to the end state (screenshots)
//   ?view=side                  desktop camera from the side (to see depth)
//   ?scene=aero|tech|demo|night the surroundings (also the Scene menu)
//
// A published static copy (tools/publish.sh) has <html data-static>: it never
// looks for a server and runs everything through the wasm parser.
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { Runtime } from './runtime.js';
import { Avatar } from './avatar.js';
import { STAGE_SCALE, setLineResolution } from './draw.js';
import { Board } from './board.js';
import { Transport } from './transport.js';
import { Voice } from './voice.js';
import { loadParser } from './vcl-wasm.js';
import { Scenery, SCENES } from './scenery.js';
import { Ambience } from './ambience.js';

const params = new URLSearchParams(location.search);
// Published copies load this module as main.js?v=<commit>; reuse that query
// for data files so a visitor never mixes versions.
const VQ = new URL(import.meta.url).search;
const $ = (s) => document.querySelector(s);

// ── renderer, scene, camera ──
const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: params.has('skip') });
renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
renderer.setSize(innerWidth, innerHeight);
renderer.xr.enabled = true;
renderer.xr.setReferenceSpaceType('local-floor');
// Text is the content here: keep fixed foveation light so the board's edges stay sharp.
renderer.xr.setFoveation(0.3);
document.body.prepend(renderer.domElement);

const BG = new THREE.Color('#0b0f16');
const scene = new THREE.Scene();
scene.background = BG;
scene.fog = new THREE.Fog(BG, 6, 16);

const camera = new THREE.PerspectiveCamera(55, innerWidth / innerHeight, 0.03, 60);
const BOARD = new THREE.Vector3(0, 1.55, -2.3);
camera.position.set(0, 1.6, -0.05);
if (params.get('view') === 'side') camera.position.set(2.2, 1.9, -0.4);
const controls = new OrbitControls(camera, renderer.domElement);
controls.target.copy(BOARD);
controls.enableDamping = true;
controls.update();

const hemi = new THREE.HemisphereLight('#c8d8ff', '#1a1f2a', 1.3);
scene.add(hemi);
const sun = new THREE.DirectionalLight('#ffffff', 1.8);
sun.position.set(1.5, 4, 2);
scene.add(sun);

// Everything the lesson owns sits in `classroom`, so it can be re-centred in VR.
const classroom = new THREE.Group();
scene.add(classroom);

const room = new THREE.Group();
const floor = new THREE.Mesh(new THREE.CircleGeometry(7, 64), new THREE.MeshStandardMaterial({ color: '#0e131b', roughness: 0.95 }));
floor.rotation.x = -Math.PI / 2;
room.add(floor);
const grid = new THREE.GridHelper(14, 28, '#1d2633', '#151c27');
grid.position.y = 0.001;
room.add(grid);
scene.add(room);

// The surroundings and their sound (Scene and Sound menus, remembered).
const scenery = new Scenery({ scene, room, hemi, sun, camera });
const ambience = new Ambience();

// The board: a dark slate panel; `stage` is its coordinate system (board units).
const stage = new THREE.Group();
stage.scale.setScalar(STAGE_SCALE);
classroom.add(stage);
const board = new Board(stage, BOARD);
const boardMesh = board.mesh;

const avatar = new Avatar({ subtitleAt: new THREE.Vector3(0, BOARD.y - 0.86, -2.15) });
classroom.add(avatar.root, avatar.beam, avatar.dot, avatar.subRoot);
// The teacher stands just left of the board, wherever its edge is.
const AVATAR_GAP = -1.55 + 8.5 * STAGE_SCALE;
// Subtitles hang under the board, or under the front edge of its stage.
const SUB_Z = avatar.subRoot.position.z;
board.onChange(([w, , d]) => {
  avatar.root.position.x = -(w / 2 + 0.5) * STAGE_SCALE + AVATAR_GAP;
  avatar.subRoot.position.z = SUB_Z + d * STAGE_SCALE;
});

// ── desktop camera: keep the board (and the teacher) in the part of the window
// the editor panel doesn't cover ──
function fitCamera() {
  if (renderer.xr.isPresenting) return;
  const W = innerWidth, Hh = innerHeight;
  const panel = document.body.classList.contains('editing') ? $('#panel').getBoundingClientRect() : null;
  const visW = panel && panel.left > W * 0.35 ? panel.left - 12 : W;
  if (visW < W) camera.setViewOffset(W, Hh, (W - visW) / 2, 0, W, Hh);
  else camera.clearViewOffset();
  camera.updateProjectionMatrix();
  // Everything that should be in view: the board and stage, the teacher, the subtitles.
  const box = board.worldBounds();
  const sub = avatar.subRoot.position;
  const pts = [];
  for (const x of [box.min.x, box.max.x]) for (const y of [box.min.y, box.max.y]) for (const z of [box.min.z, box.max.z]) pts.push(new THREE.Vector3(x, y, z));
  pts.push(new THREE.Vector3(avatar.root.position.x - 0.32, sub.y - 0.12, avatar.root.position.z),
    new THREE.Vector3(avatar.root.position.x - 0.32, BOARD.y + 0.2, avatar.root.position.z),
    new THREE.Vector3(sub.x, sub.y - 0.12, sub.z));
  const lo = new THREE.Vector3().copy(pts[0]), hi = lo.clone();
  for (const v of pts) { lo.min(v); hi.max(v); }
  const center = new THREE.Vector3(stage.position.x, (lo.y + hi.y) / 2, (lo.z + hi.z) / 2);
  const dir = camera.position.clone().sub(controls.target);
  if (dir.lengthSq() < 1e-6) dir.set(0, 0, 1);
  dir.normalize();
  // Smallest distance at which every point lands inside the uncovered part of the window.
  const xMax = -1 + 2 * visW / W;
  const fits = (dist) => {
    camera.position.copy(center).addScaledVector(dir, dist);
    camera.lookAt(center);
    camera.updateMatrixWorld();
    return pts.every((v) => {
      const p = v.clone().project(camera);
      return p.z < 1 && p.x > -0.96 && p.x < xMax - 0.04 && Math.abs(p.y) < 0.94;
    });
  };
  let a = 0.5, b = 30;
  for (let i = 0; i < 30; i++) { const m = (a + b) / 2; if (fits(m)) b = m; else a = m; }
  controls.target.copy(center);
  camera.position.copy(center).addScaledVector(dir, b);
  controls.update();
}
board.onChange(() => fitCamera());

// ── UI log ──
const logEl = $('#log');
function log(level, msg) {
  const li = document.createElement('li');
  li.className = level;
  li.textContent = msg;
  logEl.prepend(li);
  // A toast: it fades after a while (errors stay longer); the editor shows them all.
  setTimeout(() => {
    li.classList.add('old');
    setTimeout(() => li.classList.add('gone'), 700);
  }, level === 'error' ? 15000 : level === 'warning' ? 10000 : 6000);
  while (logEl.children.length > 200) logEl.lastChild.remove();
  if (level === 'error' || level === 'warning') console.warn(msg);
  else console.log(msg);
}

const voice = new Voice({ camera, speaker: avatar.head, url: new URL(`../tts.wasm${VQ}`, import.meta.url).href, log });
avatar.level = () => voice.level();
const runtime = new Runtime({ stage, board, camera, avatar, voice, log });

// ── command sources ──
const status = $('#status');
function setStatus(s, label) {
  status.dataset.state = s;
  status.textContent = label ?? s;
}

let parser = null;
let fedLines = 0; // lines the local parser has numbered so far (its line numbers keep counting)
async function localParser() {
  parser ??= await loadParser(new URL(`../vcl.wasm${VQ}`, import.meta.url).href);
  return parser;
}
// Commands that come from the editor carry `editorLine`, so playback can mark it.
let tagEditor = false;

/** Conversation events (from the tutor, tools/tutor) go to the teacher; the rest is IR. */
function onCmd(cmd) {
  if (cmd.op === 'ask') {
    log('info', `you: ${cmd.text}`);
    avatar.hear(`You: ${cmd.text}`);
  } else if (cmd.op === 'status') {
    avatar.thinking = cmd.text === 'thinking';
  } else if (cmd.op === 'answer') {
    // An answer (ours or another viewer's), for the tutor.
  } else {
    if (tagEditor && cmd.line) cmd.editorLine = cmd.line;
    runtime.exec(cmd, { instant: cmd.op === 'replay' });
  }
}

let transport = null;
const serverUrl = params.get('server') ?? (location.protocol.startsWith('http') ? `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws` : null);
const isStatic = document.documentElement.hasAttribute('data-static');
if (serverUrl && !params.has('offline') && !isStatic) {
  runtime.figBase = `${serverUrl.replace(/^ws/, 'http').replace(/\/ws$/, '')}/figs/`;
  transport = new Transport({
    url: serverUrl,
    onCmd,
    onStatus: (s) => {
      setStatus(s, s === 'live' ? 'live' : s === 'connecting' ? 'connecting…' : 'offline · local');
      $('#ask-form').hidden = s !== 'live';
    },
  });
  transport.connect();
} else setStatus('offline', 'offline · local');

/** Run VCL (or mixed prose) text: through the server if connected, else locally.
 *  `fromEditor`: the text is the editor's, so its lines are marked as they play. */
async function run(text, mode = 'vcl', fromEditor = false) {
  if (!text.endsWith('\n')) text += '\n';
  if (mode === 'vcl' && /^[ \t]*```vcl[ \t]*$/m.test(text)) { // prose + fences pasted as plain VCL
    mode = 'mixed';
    $('#mode').value = 'mixed';
    log('info', 'this text has ```vcl fences, so it runs in mixed mode (prose is spoken)');
  }
  if (transport?.connected) {
    const base = serverUrl.replace(/^ws/, 'http').replace(/\/ws$/, '');
    // Each POST is numbered from line 1; its commands arrive over the WebSocket meanwhile.
    tagEditor = fromEditor;
    try {
      const res = await fetch(`${base}/vcl?mode=${mode}`, { method: 'POST', body: text });
      if (!res.ok) log('error', `server: ${res.status}`);
    } finally { setTimeout(() => { tagEditor = false; }, 300); }
    return; // commands and diagnostics come back over the WebSocket
  }
  const p = await localParser();
  if (p.mode !== mode) { p.reset(mode); fedLines = 0; }
  // `data` results can't be computed here: fetch the ones computed ahead of time.
  await Promise.all(p.dataKeys(text, mode).map(async (key) => {
    try {
      const res = await fetch(`${runtime.figBase}${key}.data.json`);
      if (res.ok) p.putData(key, await res.text());
    } catch {}
  }));
  const base = fedLines;
  fedLines += (text.match(/\n/g) || []).length;
  for (const cmd of p.feed(text)) {
    if (fromEditor && cmd.line) cmd.editorLine = cmd.line - base;
    runtime.exec(cmd);
  }
}

/** Stop playback and wipe the board now (not after what is queued). */
async function stopAndClear() {
  runtime.reset();
  voice.stop();
  board.apply([16, 9, 0]);
  avatar.subRoot.visible = false;
  markLine(0);
  wasBusy = false; // stopped, not finished
  play.hidden = true;
  play.dataset.state = '';
  if (transport?.connected) await run('clear\nboard\n', 'vcl');
  else if (parser) { parser.reset(modeSel.value); fedLines = 0; }
}

// ── answers to questions: logged, and sent to the server for the tutor ──
runtime.onAnswer = (a) => {
  const what = a.skipped ? 'skipped' : a.correct ? 'right' : 'wrong';
  log('info', `answer (${a.kind}${a.id ? ` ${a.id}` : ''}): ${what}`);
  if (!a.skipped) ambience.answer(a.correct);
  if (!transport?.connected) return;
  const base = serverUrl.replace(/^ws/, 'http').replace(/\/ws$/, '');
  fetch(`${base}/answer`, { method: 'POST', body: JSON.stringify(a) }).catch((e) => log('warning', `answer: ${e.message}`));
};

// ── asking the teacher (needs the server and a running tutor) ──
async function ask(text) {
  text = text.trim();
  if (!text || !transport?.connected) return;
  const base = serverUrl.replace(/^ws/, 'http').replace(/\/ws$/, '');
  await fetch(`${base}/ask`, { method: 'POST', body: text }).catch((e) => log('error', `ask: ${e.message}`));
}
$('#ask-form').onsubmit = (e) => {
  e.preventDefault();
  ask($('#ask').value);
  $('#ask').value = '';
};

// Voice input, where the browser has speech recognition (feature-detected).
const Recognition = window.SpeechRecognition || window.webkitSpeechRecognition;
let listening = null;
function startListening() {
  if (!Recognition || listening) return;
  const r = new Recognition();
  r.lang = navigator.language || 'en-US';
  r.interimResults = false;
  r.onresult = (e) => ask(e.results[0][0].transcript);
  r.onerror = (e) => log('warning', `voice: ${e.error}`);
  r.onend = () => { listening = null; $('#mic').classList.remove('listening'); };
  r.start();
  listening = r;
  $('#mic').classList.add('listening');
  avatar.hear('listening…', { thinking: false });
}
function stopListening() { listening?.stop(); }
if (Recognition) {
  $('#mic').hidden = false;
  $('#mic').onclick = () => (listening ? stopListening() : startListening());
}

// ── editor / lessons ──
const editor = $('#editor');
const modeSel = $('#mode');
$('#run').onclick = () => run(editor.value, modeSel.value, true).catch((e) => log('error', e.message));
$('#clear').onclick = () => stopAndClear().catch((e) => log('error', e.message));
$('#skip').onclick = () => runtime.timeline.flush();
editor.addEventListener('keydown', (e) => {
  if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); $('#run').click(); }
});
// The Headset page runs adb on this machine, so offer it only when the page
// comes from this machine's vcl serve.
if (/^(localhost|127\.0\.0\.1|\[::1\])$/.test(location.hostname) && location.port) $('#headset').hidden = false;
$('#toggle-editor').onclick = () => { document.body.classList.toggle('editing'); fitCamera(); };
// Playback speed, remembered.
try { const s = parseFloat(localStorage.getItem('vcl.speed')); if (s >= 0.5 && s <= 2) runtime.setSpeed(s); } catch {}
$('#speed').value = String(runtime.speed);
$('#speed').onchange = (e) => {
  runtime.setSpeed(parseFloat(e.target.value));
  try { localStorage.setItem('vcl.speed', String(runtime.speed)); } catch {}
};
$('#voice').value = voice.mode;
$('#voice').onchange = (e) => voice.setMode(e.target.value);
const browserOption = $('#voice option[value="browser"]');
voice.onVoices = (n) => {
  browserOption.textContent = n ? 'browser voice' : 'browser voice (none available)';
  browserOption.title = voice.browserProblem() ?? '';
};
voice.onVoices(voice.browserVoices.length);
voice.loading?.then(() => { $('#voice').value = voice.mode; }); // robot may fall back to browser

// Lessons: viewer/lessons/NAME, or library/PATH from `vcl serve --lessons DIR`.
async function loadLesson(name) {
  const res = await fetch(name.startsWith('library/') ? name : `lessons/${name}${VQ}`);
  if (!res.ok) throw new Error(`no lesson ${name}`);
  editor.value = await res.text();
  modeSel.value = name.endsWith('.md') ? 'mixed' : 'vcl';
}
const lessonSel = $('#lessons');

// Lesson files from this computer: the Open… button, or dropped on the editor.
const LESSON_FILE = /\.(vcl|md|markdown|txt)$/i;
async function openFile(file) {
  if (!file) return;
  if (!LESSON_FILE.test(file.name) && !/^text\//.test(file.type)) throw new Error(`${file.name}: not a lesson (open a .vcl, .md or .txt file)`);
  if (file.size > 2e6) throw new Error(`${file.name}: too big for a lesson (${(file.size / 1e6).toFixed(1)} MB)`);
  const text = await file.text();
  if (text.includes('\0')) throw new Error(`${file.name}: not a text file`);
  if ($('#autoclear').checked) await stopAndClear();
  editor.value = text;
  editor.scrollTop = 0;
  markLine(0);
  // Markdown, or any text with ```vcl fences, runs as mixed prose.
  modeSel.value = /\.(md|markdown)$/i.test(file.name) || /^```vcl\b/m.test(text) ? 'mixed' : 'vcl';
  lessonSel.value = '';
  log('info', `opened ${file.name} (${modeSel.value}); press Run to play it`);
}
const fileInput = $('#open-file');
$('#open').onclick = () => fileInput.click();
fileInput.onchange = () => {
  openFile(fileInput.files[0]).catch((e) => log('error', e.message)).finally(() => { fileInput.value = ''; });
};
editor.addEventListener('dragover', (e) => {
  if (![...(e.dataTransfer?.types ?? [])].includes('Files')) return;
  e.preventDefault();
  editor.classList.add('dropping');
});
editor.addEventListener('dragleave', () => editor.classList.remove('dropping'));
editor.addEventListener('drop', (e) => {
  editor.classList.remove('dropping');
  const file = e.dataTransfer?.files?.[0];
  if (!file) return; // dropped text: the textarea inserts it as usual
  e.preventDefault();
  openFile(file).catch((err) => log('error', err.message));
});
fetch(`lessons/index.json${VQ}`).then((r) => r.json()).then((list) => {
  for (const l of list) lessonSel.add(new Option(l.title, l.file));
  // The server's lesson library, one group per folder.
  fetch('library/index.json').then((r) => (r.ok ? r.json() : null)).then((lib) => {
    const groups = new Map();
    for (const l of lib?.lessons ?? []) {
      const dir = l.file.includes('/') ? l.file.slice(0, l.file.lastIndexOf('/')) : 'library';
      if (!groups.has(dir)) groups.set(dir, lessonSel.appendChild(Object.assign(document.createElement('optgroup'), { label: dir })));
      groups.get(dir).appendChild(new Option(l.title, `library/${l.file}`));
    }
  }).catch(() => {});
  lessonSel.onchange = async () => {
    if (!lessonSel.value) return;
    try {
      if ($('#autoclear').checked) await stopAndClear();
      await loadLesson(lessonSel.value);
    } catch (e) { log('error', e.message); }
  };
}).catch(() => {});

if (params.has('lesson')) {
  const n = params.get('lesson');
  const file = /\.(vcl|md)$/.test(n) ? n : `${n}.vcl`;
  (async () => {
    await loadLesson(file);
    await voice.loading; // so the first lines are already voiced
    if (transport) await new Promise((r) => { const t = setInterval(() => transport.connected && (clearInterval(t), r()), 50); setTimeout(r, 2000); });
    await run('clear\nboard\n', 'vcl');
    await run(editor.value, modeSel.value, true);
    // Skip to the end. In mixed mode `say` beats are queued as their speech is
    // ready, after this point, so keep flushing rather than once.
    // Questions are skipped, so screenshots show the board, not a card.
    if (params.has('skip')) setInterval(() => { runtime.question?.skip(); if (runtime.timeline.busy) runtime.timeline.flush(); }, 150);
  })().catch((e) => log('error', e.message));
}

// ── playback: the line being played, running/finished, step-by-step ──
const curLine = $('#cur-line');
let markedLine = 0;
/** Mark editor line n (1-based; 0 hides the mark) and keep it in view. */
function markLine(n) {
  markedLine = n;
  curLine.hidden = !n;
  if (!n) return;
  let lh = parseFloat(getComputedStyle(editor).lineHeight) || 19.5;
  const pad = parseFloat(getComputedStyle(editor).paddingTop) || 10;
  let y = pad + (n - 1) * lh;
  if (editor.classList.contains('wrap')) ({ y, h: lh } = wrappedLine(n));
  if (document.body.classList.contains('editing') && (y < editor.scrollTop || y + lh > editor.scrollTop + editor.clientHeight))
    editor.scrollTop = Math.max(0, y - editor.clientHeight / 3);
  curLine.style.height = `${lh}px`;
  curLine.style.transform = `translateY(${y - editor.scrollTop}px)`;
}
/** Where line n sits when lines wrap: lay the text out in a hidden copy of the editor. */
const mirror = $('#editor-mirror');
function wrappedLine(n) {
  const lines = editor.value.split('\n');
  mirror.style.width = `${editor.clientWidth + 2}px`; // + the border: the same text width
  const mark = document.createElement('span');
  mark.textContent = lines[n - 1] || ' ';
  mirror.replaceChildren(lines.slice(0, n - 1).map((l) => l + '\n').join(''), mark);
  const rects = mark.getClientRects(), top = mirror.getBoundingClientRect().top;
  if (!rects.length) return { y: 0, h: 19.5 };
  const y = rects[0].top - top, h = rects[rects.length - 1].bottom - top - y;
  mirror.replaceChildren();
  return { y, h };
}
editor.addEventListener('scroll', () => markedLine && markLine(markedLine));
editor.addEventListener('input', () => markLine(0)); // edited: the numbers no longer match
runtime.onBegin = (t) => {
  if (t.cmd?.editorLine) markLine(t.cmd.editorLine);
  soundFor(t);
};

// ── scene and sound ──
const POPS = new Set(['pt', 'node', 'box', 'sphere', 'cyl', 'model']);
const FADES = new Set(['text', 'tex', 'title', 'img', 'svg', 'fig', 'code', 'table', 'grid', 'bits', 'band']);
const DRAWS = new Set(['line', 'arrow', 'path', 'poly', 'rect', 'circle', 'angle', 'edge', 'axes', 'plot', 'surf',
  'part', 'wire', 'block', 'frame', 'wave', 'nn', 'bars', 'hist', 'scatter', 'errbar', 'contour']);
let lastSound = 0;
/** A soft sound for each thing that appears (not too many at once), and the music dips under speech. */
function soundFor(t) {
  const op = t.cmd?.op, secs = (t.dur ?? 0) / runtime.speed;
  if (!op || secs < 0.05) return; // instant: replaying or skipping
  if (op === 'say') { ambience.duck(secs); return; }
  if (op === 'quiz' || op === 'pick' || op === 'estimate' || op === 'recall') { ambience.question(); return; }
  const now = performance.now();
  if (now - lastSound < 90) return;
  if (DRAWS.has(op)) ambience.draw(secs);
  else if (POPS.has(op)) ambience.pop();
  else if (FADES.has(op)) ambience.fade();
  else return;
  lastSound = now;
}
// Scene tiles in the settings panel.
const scenesEl = $('#scenes');
for (const [k, label] of Object.entries(SCENES)) {
  const b = document.createElement('button');
  b.className = `swatch ${k}`;
  b.dataset.scene = k;
  b.title = label;
  b.innerHTML = `<i></i><span>${label}</span>`;
  b.onclick = () => {
    setScene(k);
    try { localStorage.setItem('vcl.scene', k); } catch {}
  };
  scenesEl.append(b);
}
let sceneName = params.get('scene');
try { sceneName ??= localStorage.getItem('vcl.scene'); } catch {}
sceneName = SCENES[sceneName] ? sceneName : 'night';
function setScene(name) {
  scenery.set(name);
  ambience.setScene(name);
  for (const b of scenesEl.children) b.setAttribute('aria-pressed', String(b.dataset.scene === name));
}
setScene(sceneName);

// Volumes (0–100, remembered). An older "sound" setting maps onto them once.
function volume(id, key, def, apply) {
  const el = $(id), out = el.parentElement.querySelector('output');
  let v = def;
  try {
    const old = localStorage.getItem('vcl.sound'), saved = localStorage.getItem(key);
    if (saved !== null) v = +saved;
    else if (old === 'off' || (old === 'fx' && key === 'vcl.musicVol')) v = 0;
  } catch {}
  const show = () => {
    el.style.setProperty('--fill', `${el.value}%`);
    out.textContent = +el.value ? `${el.value}` : 'off';
    apply(el.value / 100);
  };
  el.value = String(v);
  show();
  el.oninput = () => { show(); ambience.start(); };
  el.onchange = () => { try { localStorage.setItem(key, el.value); } catch {} };
}
volume('#music-vol', 'vcl.musicVol', 60, (v) => ambience.setMusic(v));
volume('#fx-vol', 'vcl.fxVol', 70, (v) => ambience.setEffects(v));

// The settings panel: the gear opens it; a click elsewhere or Escape closes it.
const settingsEl = $('#settings'), settingsBtn = $('#settings-btn');
function showSettings(on) {
  settingsEl.hidden = !on;
  settingsBtn.setAttribute('aria-expanded', String(on));
}
settingsBtn.onclick = (e) => { e.stopPropagation(); showSettings(settingsEl.hidden); };
addEventListener('pointerdown', (e) => {
  if (!settingsEl.hidden && !settingsEl.contains(e.target) && !settingsBtn.contains(e.target)) showSettings(false);
});
addEventListener('keydown', (e) => { if (e.key === 'Escape') showSettings(false); });

function setting(id, key, def, onChange = () => {}) {
  const el = $(id);
  try { const v = localStorage.getItem(key); el.checked = v === null ? def : v === '1'; } catch { el.checked = def; }
  el.onchange = () => { try { localStorage.setItem(key, el.checked ? '1' : '0'); } catch {} onChange(el.checked); };
  onChange(el.checked);
}
setting('#autoclear', 'vcl.autoclear', true);
setting('#wrap', 'vcl.wrap', false, (on) => {
  editor.classList.toggle('wrap', on);
  editor.wrap = on ? 'soft' : 'off';
  if (markedLine) markLine(markedLine);
});
new ResizeObserver(() => markedLine && editor.classList.contains('wrap') && markLine(markedLine)).observe(editor);
setting('#step', 'vcl.step', false, (on) => {
  runtime.stepping = on;
  $('#next').hidden = !on;
  if (!on) runtime.next(); // don't leave a sentence waiting
});
$('#next').onclick = () => runtime.next();
$('#back').onclick = () => runtime.back();
$('#fwd').onclick = () => runtime.forward();
$('#pause').onclick = () => runtime.togglePause();
$('#practice').onclick = () => {
  if (runtime.practicing) return runtime.endPractice();
  if (!runtime.practice()) log('info', 'this lesson has no questions to practise');
};
runtime.onPractice = ({ right, total }) => {
  const msg = total ? `✓ Practice done · ${right} of ${total} right` : '✓ Practice done';
  log('info', msg);
  avatar.hear(msg, { thinking: false, secs: 8 });
};
// Space: next step (step mode) or pause/resume; → skips to the next step, ← goes back one.
addEventListener('keydown', (e) => {
  if (e.target.closest?.('textarea, input, select')) return;
  const act = {
    ' ': () => (runtime.waiting ? runtime.next() : runtime.togglePause()),
    Enter: () => runtime.waiting && runtime.next(),
    ArrowRight: () => runtime.forward(), PageDown: () => runtime.forward(),
    ArrowLeft: () => runtime.back(), PageUp: () => runtime.back(),
  }[e.key];
  if (!act) return;
  e.preventDefault();
  act();
});

const play = $('#play');
let wasBusy = false;
/** The playback pill and buttons; when a lesson (or a reply) ends, say so. */
function showPlayState() {
  const rs = runtime.state, busy = rs !== 'idle';
  const state = busy ? rs : wasBusy ? 'done' : play.dataset.state;
  const finished = !busy && wasBusy;
  wasBusy = busy;
  $('#transport').hidden = !runtime.steps;
  const steps = runtime.steps, at = Math.min(runtime.step, steps);
  $('#progress').hidden = $('#progress-label').hidden = !steps;
  if (steps) {
    $('#progress-fill').style.width = `${(state === 'done' ? 1 : at / steps) * 100}%`;
    $('#progress-label').textContent = `${state === 'done' ? steps : Math.max(at, 1)} / ${steps}`;
  }
  $('#dock').hidden = !steps && !state && $('#next').hidden;
  const pr = $('#practice');
  pr.hidden = !runtime.practicing && !runtime.questionSteps.length;
  pr.textContent = runtime.practicing ? `End practice (${runtime.practicing.i + 1}/${runtime.practicing.steps.length})` : 'Practice';
  pr.classList.toggle('on', !!runtime.practicing);
  $('#pause').textContent = rs === 'paused' ? '▶' : '❚❚';
  $('#pause').title = rs === 'paused' ? 'Resume (Space)' : 'Pause (Space)';
  if (finished && runtime.practiceEnded) runtime.practiceEnded = false; // its own message is showing
  else if (finished) {
    const { right, total } = runtime.score;
    const score = total ? ` · ${right} of ${total} right` : '';
    play.dataset.score = score;
    avatar.hear(`✓ Finished${score}`, { thinking: false, secs: 8 });
  }
  if (state === play.dataset.state) return;
  play.dataset.state = state;
  play.hidden = !state;
  play.textContent = {
    playing: 'playing', paused: 'paused · Space to resume', waiting: 'step mode · Space for next',
    question: 'your turn: answer the question', done: `finished${play.dataset.score ?? ''}`,
  }[state] ?? '';
  $('#next').classList.toggle('ready', state === 'waiting');
  if (state === 'done') markLine(0);
}

// ── XR ──
async function xrButton(mode, el) {
  if (!navigator.xr || !(await navigator.xr.isSessionSupported(mode).catch(() => false))) { el.hidden = true; return; }
  el.hidden = false;
  el.onclick = async () => {
    if (renderer.xr.isPresenting) { renderer.xr.getSession().end(); return; }
    const session = await navigator.xr.requestSession(mode, { optionalFeatures: ['local-floor', 'hand-tracking'] });
    const ar = mode === 'immersive-ar';
    scenery.setAR(ar);
    boardMesh.material.opacity = ar ? 0.8 : 0.96;
    session.addEventListener('end', () => { scenery.setAR(false); boardMesh.material.opacity = 0.96; });
    await renderer.xr.setSession(session);
  };
}
xrButton('immersive-vr', $('#enter-vr'));
xrButton('immersive-ar', $('#enter-ar'));

// ── VR comfort: where the classroom sits ──
// Grip re-centres in front of you; right stick forward/back moves the board
// farther/closer, left stick raises/lowers it. Remembered per device.
const placement = { dist: 0, height: 0, yaw: 0, x: 0, y: 0, z: 0 };
// Only distance and height persist: the headset's floor origin (and so any
// re-centred position) changes every session.
try {
  const { dist = 0, height = 0 } = JSON.parse(localStorage.getItem('vcl.placement') || '{}');
  Object.assign(placement, { dist: +dist || 0, height: +height || 0 });
} catch {}
function savePlacement() {
  try { localStorage.setItem('vcl.placement', JSON.stringify({ dist: placement.dist, height: placement.height })); } catch {}
}
function applyPlacement() {
  classroom.rotation.y = placement.yaw;
  const off = new THREE.Vector3(0, placement.height, placement.dist).applyAxisAngle(new THREE.Vector3(0, 1, 0), placement.yaw);
  classroom.position.set(placement.x, placement.y, placement.z).add(off);
}
applyPlacement();

// ── pressing things on the board (question cards): mouse, or a controller's ray ──
// Each pointer (0, 1: controllers; 'mouse') hovers one hit; pressing it calls
// press(), and while held, drag() follows the pointer over that hit's plane.
const controllers = [];
const pointer = {
  ray: new THREE.Raycaster(),
  hover: new Map(), // pointer → hit
  held: new Map(),  // pointer → hit being dragged
  /** Aim pointer `id` along a ray; returns the hit it is over. */
  aim(id, origin, dir) {
    this.ray.set(origin, dir);
    const hits = runtime.hits;
    const meshes = hits.map((h) => h.mesh);
    const top = meshes.length ? this.ray.intersectObjects(meshes, false)[0] : null;
    const hit = top ? hits[meshes.indexOf(top.object)] : null;
    const prev = this.hover.get(id);
    if (prev !== hit) { prev?.hover(false); hit?.hover(true); this.hover.set(id, hit); }
    const held = this.held.get(id);
    if (held?.drag) {
      // Follow the pointer over the dragged thing's plane.
      const n = new THREE.Vector3(0, 0, 1).transformDirection(held.mesh.matrixWorld);
      const plane = new THREE.Plane().setFromNormalAndCoplanarPoint(n, held.mesh.getWorldPosition(new THREE.Vector3()));
      const at = this.ray.ray.intersectPlane(plane, new THREE.Vector3());
      if (at) held.drag(at);
    }
    return hit;
  },
  press(id) {
    const hit = this.hover.get(id);
    if (!hit || !runtime.hits.includes(hit)) return false;
    this.held.set(id, hit);
    const at = this.ray.ray.intersectPlane(new THREE.Plane().setFromNormalAndCoplanarPoint(
      new THREE.Vector3(0, 0, 1).transformDirection(hit.mesh.matrixWorld), hit.mesh.getWorldPosition(new THREE.Vector3())), new THREE.Vector3());
    hit.press(at);
    if (at) hit.drag?.(at);
    return true;
  },
  release(id) { this.held.delete(id); },
  /** Controllers: aim each one's ray every frame. */
  update() {
    for (let i = 0; i < controllers.length; i++) {
      const c = controllers[i];
      if (!renderer.xr.isPresenting) continue;
      const o = c.getWorldPosition(new THREE.Vector3());
      const d = new THREE.Vector3(0, 0, -1).applyQuaternion(c.getWorldQuaternion(new THREE.Quaternion()));
      this.aim(i, o, d);
    }
  },
};
// Mouse: hover and press question buttons; a press on one doesn't turn the view.
const mouse = new THREE.Vector2();
function aimMouse(e) {
  const r = renderer.domElement.getBoundingClientRect();
  mouse.set(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1);
  pointer.ray.setFromCamera(mouse, camera);
  return pointer.aim('mouse', pointer.ray.ray.origin.clone(), pointer.ray.ray.direction.clone());
}
renderer.domElement.addEventListener('pointerdown', (e) => {
  if (renderer.xr.isPresenting) return;
  aimMouse(e);
  if (pointer.press('mouse')) { e.stopImmediatePropagation(); controls.enabled = false; }
}, { capture: true });
renderer.domElement.addEventListener('pointermove', (e) => {
  if (renderer.xr.isPresenting) return;
  const hit = aimMouse(e);
  const h = pointer.held.get('mouse') ?? hit;
  renderer.domElement.style.cursor = h ? h.cursor ?? 'pointer' : '';
});
addEventListener('pointerup', () => { pointer.release('mouse'); controls.enabled = true; });

// Controllers: a short ray each; squeeze (grip) re-centres the classroom in front of you.
for (let i = 0; i < 2; i++) {
  const c = renderer.xr.getController(i);
  const ray = new THREE.Mesh(new THREE.CylinderGeometry(0.002, 0.002, 0.6, 6), new THREE.MeshBasicMaterial({ color: '#7fe3ff', transparent: true, opacity: 0.5 }));
  ray.rotation.x = -Math.PI / 2;
  ray.position.z = -0.3;
  c.add(ray);
  c.addEventListener('squeezestart', recenter);
  // Trigger: press what the ray points at (a question's buttons), else next step (step mode).
  c.addEventListener('selectstart', () => { if (!pointer.press(i)) runtime.next(); });
  c.addEventListener('selectend', () => pointer.release(i));
  scene.add(c);
  controllers.push(c);
}
function recenter() {
  const head = renderer.xr.getCamera();
  const pos = head.getWorldPosition(new THREE.Vector3());
  const dir = head.getWorldDirection(new THREE.Vector3());
  Object.assign(placement, { yaw: Math.atan2(-dir.x, -dir.z), x: pos.x, y: pos.y - 1.6, z: pos.z });
  applyPlacement();
  savePlacement();
}

/** Thumbsticks move the board (left/right: next or previous step); A/X is push-to-talk; B/Y pauses. */
let sticksMoved = false, talkHeld = false, pauseHeld = false, flick = 0;
function readControllers(dt) {
  const session = renderer.xr.getSession();
  if (!session) return;
  let moved = false, talk = false, pause = false, side = 0;
  for (const src of session.inputSources) {
    const gp = src.gamepad;
    if (!gp) continue;
    const y = gp.axes[3] ?? 0;
    if (Math.abs(y) > 0.25) {
      moved = true;
      if (src.handedness === 'right') placement.dist = THREE.MathUtils.clamp(placement.dist + y * dt * 0.9, -2.5, 1.6);
      if (src.handedness === 'left') placement.height = THREE.MathUtils.clamp(placement.height - y * dt * 0.5, -0.9, 0.9);
    }
    if (gp.buttons[4]?.pressed) talk = true;
    if (gp.buttons[5]?.pressed) pause = true;
    const x = gp.axes[2] ?? 0;
    if (Math.abs(x) > 0.7 && Math.abs(y) < 0.5) side = x > 0 ? 1 : -1;
  }
  if (pause && !pauseHeld) runtime.togglePause();
  pauseHeld = pause;
  if (side !== flick) { if (side > 0) runtime.forward(); else if (side < 0) runtime.back(); }
  flick = side;
  if (moved) applyPlacement();
  if (sticksMoved && !moved) savePlacement();
  sticksMoved = moved;
  if (talk && !talkHeld) startListening();
  if (!talk && talkHeld) stopListening();
  talkHeld = talk;
}

// ── loop ──
addEventListener('resize', () => {
  camera.aspect = innerWidth / innerHeight;
  camera.updateProjectionMatrix();
  renderer.setSize(innerWidth, innerHeight);
  setLineResolution(innerWidth, innerHeight);
  fitCamera();
});
fitCamera();

const clock = new THREE.Clock();
renderer.setAnimationLoop(() => {
  const dt = Math.min(clock.getDelta(), 0.1);
  if (!renderer.xr.isPresenting) controls.update();
  else readControllers(dt);
  pointer.update();
  runtime.update(dt);
  scenery.update(dt);
  showPlayState();
  avatar.update(dt, renderer.xr.isPresenting ? renderer.xr.getCamera() : camera);
  renderer.render(scene, camera);
});

// Exposed for debugging from the console.
Object.assign(window, { runtime, run, scene, camera, board, THREE, scenery, ambience });
