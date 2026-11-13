// Text and LaTeX as textured planes. Text is drawn white and tinted by the
// material colour, so recolouring never re-rasterises.
import * as THREE from 'three';

const FONT = '"Inter", "Segoe UI", system-ui, -apple-system, sans-serif';
const PX = 96; // raster size of one line of text

function rasterText(text, { weight = 600, align = 'center' } = {}) {
  const lines = String(text).split('\n');
  const canvas = document.createElement('canvas');
  const g = canvas.getContext('2d');
  let px = PX;
  g.font = `${weight} ${px}px ${FONT}`;
  let maxW = Math.max(1, ...lines.map((l) => g.measureText(l).width));
  if (maxW > 3800) { px = Math.floor((PX * 3800) / maxW); maxW = 3800; }
  const pad = px * 0.2, lh = px * 1.2;
  canvas.width = Math.ceil(maxW + pad * 2);
  canvas.height = Math.ceil(lh * lines.length + pad * 2);
  g.font = `${weight} ${px}px ${FONT}`;
  g.fillStyle = '#fff';
  g.textBaseline = 'middle';
  g.textAlign = align;
  const x = align === 'left' ? pad : align === 'right' ? canvas.width - pad : canvas.width / 2;
  lines.forEach((l, i) => g.fillText(l, x, pad + lh * (i + 0.5)));
  // Height of the whole plane in "line sizes".
  return { canvas, aspect: canvas.width / canvas.height, heightLines: canvas.height / px };
}

function makeTexture(canvas) {
  const tex = new THREE.CanvasTexture(canvas);
  tex.colorSpace = THREE.SRGBColorSpace;
  tex.anisotropy = 8;
  tex.generateMipmaps = true;
  tex.minFilter = THREE.LinearMipmapLinearFilter;
  return tex;
}

/** A plane showing text. `size` is the line height in board units. */
export class TextPlane {
  constructor(text, { size = 0.5, color = '#e8edf3', align = 'center', weight = 600 } = {}) {
    this.size = size;
    this.align = align;
    this.weight = weight;
    this.material = new THREE.MeshBasicMaterial({ color, transparent: true, depthWrite: false, side: THREE.DoubleSide });
    this.mesh = new THREE.Mesh(new THREE.PlaneGeometry(1, 1), this.material);
    this.mesh.renderOrder = 3;
    this.setText(text);
  }

  setText(text) {
    this.text = String(text);
    const r = rasterText(this.text, { weight: this.weight, align: this.align });
    this.#apply(r.canvas, r.heightLines * this.size, r.aspect);
  }

  /** Width/height in board units. */
  get width() { return this.mesh.scale.x; }
  get height() { return this.mesh.scale.y; }

  #apply(canvas, h, aspect) {
    this.material.map?.dispose();
    this.material.map = makeTexture(canvas);
    this.material.needsUpdate = true;
    const ax = this.align === 'left' ? (h * aspect) / 2 : this.align === 'right' ? -(h * aspect) / 2 : 0;
    this.mesh.position.x += ax - (this.alignX ?? 0); // keep wherever it was placed
    this.alignX = ax;
    this.mesh.scale.set(h * aspect, h, 1);
  }

  /** Put the text's anchor point (its left/centre/right edge, per `align`) at `v`. */
  placeAt(v) {
    this.mesh.position.copy(v);
    this.mesh.position.x += this.alignX;
    return this;
  }

  /** Show an arbitrary canvas, `height` board units tall (code, tables, SVG). */
  setCanvas(canvas, height) {
    this.text = null;
    this.#apply(canvas, height, canvas.width / canvas.height);
  }

  /** Replace the raster with LaTeX (async). */
  async setTex(tex) {
    this.text = tex;
    const r = await rasterTex(tex);
    if (this.text !== tex) return; // superseded
    this.#apply(r.canvas, r.heightLines * this.size, r.aspect);
  }

  dispose() {
    this.material.map?.dispose();
    this.material.dispose();
    this.mesh.geometry.dispose();
  }
}

// ───────────── LaTeX via MathJax (SVG → canvas), loaded on first use ─────────────

let mathjax;
function loadMathJax() {
  if (mathjax) return mathjax;
  mathjax = new Promise((resolve, reject) => {
    window.MathJax = { svg: { fontCache: 'none' }, startup: { typeset: false } };
    const s = document.createElement('script');
    s.src = 'https://cdn.jsdelivr.net/npm/mathjax@3.2.2/es5/tex-svg.js';
    s.async = true;
    s.onload = () => window.MathJax.startup.promise.then(resolve, reject);
    s.onerror = () => reject(new Error('could not load MathJax'));
    document.head.appendChild(s);
  });
  return mathjax;
}

const EX = 48; // px per ex when rasterising

async function rasterTex(tex) {
  await loadMathJax();
  const node = window.MathJax.tex2svg(tex, { display: true });
  const svg = node.querySelector('svg');
  const wEx = parseFloat(svg.getAttribute('width'));
  const hEx = parseFloat(svg.getAttribute('height'));
  svg.setAttribute('width', `${wEx * EX}px`);
  svg.setAttribute('height', `${hEx * EX}px`);
  svg.setAttribute('color', '#ffffff');
  svg.style.color = '#ffffff';
  const xml = new XMLSerializer().serializeToString(svg);
  const img = new Image();
  const url = URL.createObjectURL(new Blob([xml], { type: 'image/svg+xml' }));
  try {
    img.src = url;
    await img.decode();
  } finally {
    URL.revokeObjectURL(url);
  }
  const pad = EX * 0.4;
  const canvas = document.createElement('canvas');
  canvas.width = Math.ceil(wEx * EX + pad * 2);
  canvas.height = Math.ceil(hEx * EX + pad * 2);
  canvas.getContext('2d').drawImage(img, pad, pad, wEx * EX, hEx * EX);
  // A line of ordinary math is ~2.2ex tall: map that to one "size".
  return { canvas, aspect: canvas.width / canvas.height, heightLines: canvas.height / (EX * 2.2) };
}
