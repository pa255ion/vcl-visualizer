// Viewer bootstrap: room, board, teacher, XR, UI, and the command source
// (live server over WebSocket, or the local wasm parser when offline).
//
// URL parameters:
//   ?server=ws://host:8080/ws   connect to a different stream server
//   ?offline=1                  never connect; parse locally
//   ?lesson=NAME                load lessons/NAME.vcl (or .md) and play it
//   ?skip=1                     jump straight to the end state (screenshots)
//   ?view=side                  desktop camera from the side (to see depth)
//
// A published static copy (tools/publish.sh) has <html data-static>: it never
// looks for a server and runs everything through the wasm parser.
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { Runtime } from './runtime.js';
import { Avatar } from './avatar.js';
import { STAGE_SCALE, setLineResolution, roundedRectPoints, Stroke } from './draw.js';
import { Transport } from './transport.js';
import { loadParser } from './vcl-wasm.js';

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

scene.add(new THREE.HemisphereLight('#c8d8ff', '#1a1f2a', 1.3));
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

// The board: a dark slate panel; `stage` is its coordinate system (board units).
const stage = new THREE.Group();
stage.position.copy(BOARD);
stage.scale.setScalar(STAGE_SCALE);
classroom.add(stage);
const boardOutline = roundedRectPoints(17, 9.8, 0.35);
const boardMesh = new THREE.Mesh(
  new THREE.ShapeGeometry(new THREE.Shape(boardOutline.map((p) => new THREE.Vector2(p.x, p.y)))),
  new THREE.MeshBasicMaterial({ color: '#121924', transparent: true, opacity: 0.96, depthWrite: false }),
);
boardMesh.position.z = -0.05;
boardMesh.renderOrder = 0;
stage.add(boardMesh);
const boardEdge = new Stroke({ color: '#2a3647', width: 0.05 });
boardEdge.set([boardOutline.map((p) => p.setZ(-0.04))]);
stage.add(boardEdge.obj);

const avatar = new Avatar({ subtitleAt: new THREE.Vector3(0, BOARD.y - 0.86, -2.15) });
classroom.add(avatar.root, avatar.beam, avatar.dot, avatar.subRoot);

// ── UI log ──
const logEl = $('#log');
function log(level, msg) {
  const li = document.createElement('li');
  li.className = level;
  li.textContent = msg;
  logEl.prepend(li);
  while (logEl.children.length > 200) logEl.lastChild.remove();
  if (level === 'error' || level === 'warning') console.warn(msg);
  else console.log(msg);
}

const runtime = new Runtime({ stage, camera, avatar, log });

// ── command sources ──
const status = $('#status');
function setStatus(s, label) {
  status.dataset.state = s;
  status.textContent = label ?? s;
}

let parser = null;
async function localParser() {
  parser ??= await loadParser(new URL(`../vcl.wasm${VQ}`, import.meta.url).href);
  return parser;
}

/** Conversation events (from the tutor, tools/tutor) go to the teacher; the rest is IR. */
function onCmd(cmd) {
  if (cmd.op === 'ask') {
    log('info', `you: ${cmd.text}`);
    avatar.hear(`You: ${cmd.text}`);
  } else if (cmd.op === 'status') {
    avatar.thinking = cmd.text === 'thinking';
  } else runtime.exec(cmd, { instant: cmd.op === 'replay' });
}

let transport = null;
const serverUrl = params.get('server') ?? (location.protocol.startsWith('http') ? `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws` : null);
const isStatic = document.documentElement.hasAttribute('data-static');
if (serverUrl && !params.has('offline') && !isStatic) {
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

/** Run VCL (or mixed prose) text: through the server if connected, else locally. */
async function run(text, mode = 'vcl') {
  if (!text.endsWith('\n')) text += '\n';
  if (transport?.connected) {
    const base = serverUrl.replace(/^ws/, 'http').replace(/\/ws$/, '');
    const res = await fetch(`${base}/vcl?mode=${mode}`, { method: 'POST', body: text });
    if (!res.ok) log('error', `server: ${res.status}`);
    return; // commands and diagnostics come back over the WebSocket
  }
  const p = await localParser();
  if (p.mode !== mode) p.reset(mode);
  for (const cmd of p.feed(text)) runtime.exec(cmd);
}

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
$('#run').onclick = () => run(editor.value, modeSel.value).catch((e) => log('error', e.message));
$('#clear').onclick = () => run('clear', 'vcl');
$('#skip').onclick = () => runtime.timeline.flush();
editor.addEventListener('keydown', (e) => {
  if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); $('#run').click(); }
});
$('#toggle-editor').onclick = () => document.body.classList.toggle('editing');
$('#voice').onchange = (e) => { avatar.voice = e.target.checked; };

async function loadLesson(name) {
  const res = await fetch(`lessons/${name}${VQ}`);
  if (!res.ok) throw new Error(`no lesson ${name}`);
  editor.value = await res.text();
  modeSel.value = name.endsWith('.md') ? 'mixed' : 'vcl';
}
const lessonSel = $('#lessons');
fetch(`lessons/index.json${VQ}`).then((r) => r.json()).then((list) => {
  for (const l of list) lessonSel.add(new Option(l.title, l.file));
  lessonSel.onchange = () => lessonSel.value && loadLesson(lessonSel.value).catch((e) => log('error', e.message));
}).catch(() => {});

if (params.has('lesson')) {
  const n = params.get('lesson');
  const file = /\.(vcl|md)$/.test(n) ? n : `${n}.vcl`;
  (async () => {
    await loadLesson(file);
    if (transport) await new Promise((r) => { const t = setInterval(() => transport.connected && (clearInterval(t), r()), 50); setTimeout(r, 2000); });
    await run('clear\n', 'vcl');
    await run(editor.value, modeSel.value);
    if (params.has('skip')) setTimeout(() => runtime.timeline.flush(), 300);
  })().catch((e) => log('error', e.message));
}

// ── XR ──
async function xrButton(mode, el) {
  if (!navigator.xr || !(await navigator.xr.isSessionSupported(mode).catch(() => false))) { el.hidden = true; return; }
  el.hidden = false;
  el.onclick = async () => {
    if (renderer.xr.isPresenting) { renderer.xr.getSession().end(); return; }
    const session = await navigator.xr.requestSession(mode, { optionalFeatures: ['local-floor', 'hand-tracking'] });
    const ar = mode === 'immersive-ar';
    scene.background = ar ? null : BG;
    room.visible = !ar;
    boardMesh.material.opacity = ar ? 0.8 : 0.96;
    session.addEventListener('end', () => { scene.background = BG; room.visible = true; boardMesh.material.opacity = 0.96; });
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

// Controllers: a short ray each; squeeze (grip) re-centres the classroom in front of you.
for (let i = 0; i < 2; i++) {
  const c = renderer.xr.getController(i);
  const ray = new THREE.Mesh(new THREE.CylinderGeometry(0.002, 0.002, 0.6, 6), new THREE.MeshBasicMaterial({ color: '#7fe3ff', transparent: true, opacity: 0.5 }));
  ray.rotation.x = -Math.PI / 2;
  ray.position.z = -0.3;
  c.add(ray);
  c.addEventListener('squeezestart', recenter);
  scene.add(c);
}
function recenter() {
  const head = renderer.xr.getCamera();
  const pos = head.getWorldPosition(new THREE.Vector3());
  const dir = head.getWorldDirection(new THREE.Vector3());
  Object.assign(placement, { yaw: Math.atan2(-dir.x, -dir.z), x: pos.x, y: pos.y - 1.6, z: pos.z });
  applyPlacement();
  savePlacement();
}

/** Thumbsticks move the board; A (right) / X (left) is push-to-talk. */
let sticksMoved = false, talkHeld = false;
function readControllers(dt) {
  const session = renderer.xr.getSession();
  if (!session) return;
  let moved = false, talk = false;
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
  }
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
});

const clock = new THREE.Clock();
renderer.setAnimationLoop(() => {
  const dt = Math.min(clock.getDelta(), 0.1);
  if (!renderer.xr.isPresenting) controls.update();
  else readControllers(dt);
  runtime.update(dt);
  avatar.update(dt, renderer.xr.isPresenting ? renderer.xr.getCamera() : camera);
  renderer.render(scene, camera);
});

// Exposed for debugging from the console.
Object.assign(window, { runtime, run, scene, camera, THREE });
