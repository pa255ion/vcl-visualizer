// Scenery: the world around the board. Each scene sets the sky, fog, light,
// floor and a few slow-moving things far enough away not to compete with the
// board. Everything is generated here (shaders, canvas textures, simple
// geometry): no downloads, nothing to license.
//
//   night  the plain dark room (the original look)
//   aero   bright sky, glossy platform, green hills, clouds and soap bubbles
//   tech   deep blue night, glowing floor grid, distant towers, rising data
//   demo   demoscene / synthwave: striped sun, stars, scrolling grid, vector shapes
import * as THREE from 'three';

export const SCENES = {
  night: 'Night',
  aero: 'Aero',
  tech: 'Tech',
  demo: 'Demo',
};

// ── shared shaders ──

/** Sky dome: a vertical gradient with a sun (optionally striped, synthwave-style). */
function skyMaterial(o) {
  return new THREE.ShaderMaterial({
    side: THREE.BackSide, depthWrite: false, fog: false,
    uniforms: {
      uTop: { value: new THREE.Color(o.top) }, uHorizon: { value: new THREE.Color(o.horizon) },
      uBottom: { value: new THREE.Color(o.bottom) }, uSun: { value: new THREE.Color(o.sun ?? '#ffffff') },
      uSun2: { value: new THREE.Color(o.sun2 ?? o.sun ?? '#ffffff') },
      uSunDir: { value: new THREE.Vector3(...(o.sunDir ?? [0, 0.3, -1])).normalize() },
      uSunSize: { value: o.sunSize ?? 0 }, uGlow: { value: o.glow ?? 0 }, uStripes: { value: o.stripes ? 1 : 0 },
      uBand: { value: o.band ?? 0.25 }, uTime: { value: 0 },
    },
    vertexShader: `varying vec3 vDir;
      void main() { vDir = normalize(position); gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`,
    fragmentShader: `uniform vec3 uTop, uHorizon, uBottom, uSun, uSun2, uSunDir; uniform float uSunSize, uGlow, uStripes, uBand, uTime;
      varying vec3 vDir;
      void main() {
        vec3 d = normalize(vDir);
        float h = d.y;
        vec3 c = h > 0.0 ? mix(uHorizon, uTop, pow(smoothstep(0.0, 1.0, h / uBand * 0.25 + h), 0.7)) : mix(uHorizon, uBottom, smoothstep(0.0, 0.15, -h));
        float a = acos(clamp(dot(d, uSunDir), -1.0, 1.0));
        c += uSun * uGlow * exp(-a * 6.0) * 0.6;
        if (uSunSize > 0.0 && a < uSunSize) {
          float v = (d.y - (uSunDir.y - uSunSize)) / (2.0 * uSunSize); // 0 at the bottom of the disc, 1 at the top
          vec3 s = mix(uSun2, uSun, clamp(v, 0.0, 1.0));
          float edge = smoothstep(uSunSize, uSunSize * 0.97, a);
          if (uStripes > 0.5 && v < 0.55) {
            float band = fract(v * 14.0 - uTime * 0.15);
            edge *= step(mix(0.08, 0.55, 1.0 - v / 0.55), band);
          }
          c = mix(c, s, edge);
        }
        gl_FragColor = vec4(c, 1.0);
      }`,
  });
}

/** A floor grid that glows and fades with distance; `scroll` slides it toward the viewer. */
function gridMaterial(o) {
  return new THREE.ShaderMaterial({
    transparent: true, depthWrite: false, fog: false,
    uniforms: {
      uColor: { value: new THREE.Color(o.color) }, uBase: { value: new THREE.Color(o.base ?? '#000000') },
      uCell: { value: o.cell ?? 1 }, uMajor: { value: o.major ?? 5 }, uFade: { value: o.fade ?? 30 },
      uScroll: { value: 0 }, uPulse: { value: o.pulse ? 1 : 0 }, uTime: { value: 0 }, uBaseAlpha: { value: o.baseAlpha ?? 1 },
    },
    vertexShader: `varying vec3 vPos;
      void main() { vec4 w = modelMatrix * vec4(position, 1.0); vPos = w.xyz; gl_Position = projectionMatrix * viewMatrix * w; }`,
    fragmentShader: `uniform vec3 uColor, uBase; uniform float uCell, uMajor, uFade, uScroll, uPulse, uTime, uBaseAlpha;
      varying vec3 vPos;
      float lines(vec2 p, float w) {
        vec2 g = abs(fract(p - 0.5) - 0.5) / fwidth(p);
        return 1.0 - min(min(g.x, g.y) / w, 1.0);
      }
      void main() {
        vec2 p = vPos.xz / uCell + vec2(0.0, uScroll);
        float r = length(vPos.xz);
        float l = max(lines(p, 1.2) * 0.45, lines(p / uMajor, 1.6));
        float fade = 1.0 - smoothstep(uFade * 0.25, uFade, r);
        float pulse = 0.0;
        if (uPulse > 0.5) { float ring = mod(uTime * 4.0, 40.0); pulse = exp(-abs(r - ring) * 1.5) * 0.8 * (1.0 - ring / 40.0); }
        vec3 c = mix(uBase, uColor, clamp(l + pulse * 0.6, 0.0, 1.0)) + uColor * pulse * 0.3;
        float a = max(uBaseAlpha, l) * fade;
        gl_FragColor = vec4(c, a);
      }`,
  });
}

/** Soft round sprite texture (clouds, glows, stars). */
function blobTexture(stops) {
  const c = document.createElement('canvas');
  c.width = c.height = 128;
  const g = c.getContext('2d');
  const grad = g.createRadialGradient(64, 64, 0, 64, 64, 64);
  for (const [t, col] of stops) grad.addColorStop(t, col);
  g.fillStyle = grad;
  g.fillRect(0, 0, 128, 128);
  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  return tex;
}

/** A puffy cloud: several overlapping soft blobs on one canvas. */
function cloudTexture(seed) {
  const c = document.createElement('canvas');
  c.width = 256; c.height = 128;
  const g = c.getContext('2d');
  let s = seed;
  const rnd = () => ((s = (s * 16807) % 2147483647) / 2147483647);
  for (let i = 0; i < 9; i++) {
    const x = 50 + rnd() * 156, y = 60 + rnd() * 25, r = 26 + rnd() * 30;
    const grad = g.createRadialGradient(x, y - r * 0.3, 0, x, y, r);
    grad.addColorStop(0, 'rgba(255,255,255,0.95)');
    grad.addColorStop(0.6, 'rgba(245,250,255,0.6)');
    grad.addColorStop(1, 'rgba(230,242,255,0)');
    g.fillStyle = grad;
    g.beginPath(); g.arc(x, y, r, 0, Math.PI * 2); g.fill();
  }
  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  return tex;
}

/** Glossy soap bubble: a bright rim, a faint rainbow sheen and a highlight. */
function bubbleMaterial() {
  return new THREE.ShaderMaterial({
    transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
    uniforms: { uTime: { value: 0 } },
    vertexShader: `varying vec3 vN, vV;
      void main() { vec4 mv = modelViewMatrix * vec4(position, 1.0); vN = normalize(normalMatrix * normal); vV = normalize(-mv.xyz); gl_Position = projectionMatrix * mv; }`,
    fragmentShader: `uniform float uTime; varying vec3 vN, vV;
      void main() {
        float f = 1.0 - abs(dot(vN, vV));
        vec3 sheen = 0.5 + 0.5 * cos(6.2831 * (f * 1.3 + vec3(0.0, 0.33, 0.67) + uTime * 0.05));
        float spec = pow(max(dot(reflect(-vV, vN), normalize(vec3(-0.4, 0.8, 0.5))), 0.0), 40.0);
        vec3 c = mix(vec3(0.75, 0.9, 1.0), sheen, 0.45) * pow(f, 2.5) * 0.9 + spec * 0.9;
        gl_FragColor = vec4(c, 1.0);
      }`,
  });
}

/** Deterministic pseudo-random numbers, so a scene looks the same each visit. */
function rng(seed) {
  let s = seed;
  return () => ((s = (s * 16807) % 2147483647) / 2147483647);
}

// ── the scenes ──

function buildAero(g, anim) {
  const R = rng(7);
  // Glossy white platform with an aqua rim, the classroom floor.
  const plat = new THREE.Mesh(new THREE.CylinderGeometry(6.5, 6.7, 0.12, 96),
    new THREE.MeshStandardMaterial({ color: '#c9dbe8', roughness: 0.22, metalness: 0.15 }));
  plat.position.y = -0.061;
  g.add(plat);
  const rim = new THREE.Mesh(new THREE.TorusGeometry(6.62, 0.05, 12, 128),
    new THREE.MeshBasicMaterial({ color: '#5fd3ff' }));
  rim.rotation.x = Math.PI / 2;
  g.add(rim);
  // Rolling hills: flat near the platform, rising toward the horizon.
  const hills = new THREE.PlaneGeometry(240, 240, 120, 120);
  hills.rotateX(-Math.PI / 2);
  const pos = hills.attributes.position, colors = [];
  const lo = new THREE.Color('#3f9e45'), hi = new THREE.Color('#b6e86a'), tmp = new THREE.Color();
  for (let i = 0; i < pos.count; i++) {
    const x = pos.getX(i), z = pos.getZ(i), r = Math.hypot(x, z);
    const n = Math.sin(x * 0.045) * Math.cos(z * 0.05) * 3.2 + Math.sin(x * 0.11 + z * 0.07) * 1.3 + Math.cos(z * 0.13 - x * 0.03) * 0.9;
    const k = THREE.MathUtils.smoothstep(r, 9, 45);
    const y = -0.15 + (n + 3) * k * 1.4 + k * 2;
    pos.setY(i, y);
    tmp.copy(lo).lerp(hi, THREE.MathUtils.clamp((n + 4) / 9, 0, 1));
    colors.push(tmp.r, tmp.g, tmp.b);
  }
  hills.setAttribute('color', new THREE.Float32BufferAttribute(colors, 3));
  hills.computeVertexNormals();
  g.add(new THREE.Mesh(hills, new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.75 })));
  // Lake: a glossy blue disc in a dip to the side.
  const lake = new THREE.Mesh(new THREE.CircleGeometry(14, 64),
    new THREE.MeshStandardMaterial({ color: '#2aa6e6', roughness: 0.05, metalness: 0.3, transparent: true, opacity: 0.85 }));
  lake.rotation.x = -Math.PI / 2;
  lake.position.set(-26, 0.6, -14);
  g.add(lake);
  // Round, glossy trees.
  const leaf = new THREE.MeshStandardMaterial({ color: '#5cc84a', roughness: 0.3 });
  const trunk = new THREE.MeshStandardMaterial({ color: '#8a6a4a', roughness: 0.8 });
  for (let i = 0; i < 16; i++) {
    const a = R() * Math.PI * 2, r = 12 + R() * 16, s = 0.7 + R() * 0.9;
    if (Math.abs(Math.atan2(Math.sin(a), Math.cos(a)) + Math.PI / 2) < 0.5) continue; // keep the view behind the board clear
    const x = Math.cos(a) * r, z = Math.sin(a) * r;
    const k = THREE.MathUtils.smoothstep(r, 9, 45);
    const base = -0.15 + k * 2;
    const t = new THREE.Mesh(new THREE.CylinderGeometry(0.12 * s, 0.16 * s, 1.4 * s, 8), trunk);
    t.position.set(x, base + 0.7 * s, z);
    const c = new THREE.Mesh(new THREE.SphereGeometry(0.9 * s, 20, 14), leaf);
    c.position.set(x, base + 1.7 * s, z);
    g.add(t, c);
  }
  // Clouds drifting slowly across the sky.
  const clouds = [];
  for (let i = 0; i < 16; i++) {
    const m = new THREE.SpriteMaterial({ map: cloudTexture(11 + i * 31), transparent: true, depthWrite: false, fog: false, opacity: 0.92 });
    const s = new THREE.Sprite(m);
    const a = R() * Math.PI * 2, r = 55 + R() * 30;
    s.position.set(Math.cos(a) * r, 14 + R() * 18, Math.sin(a) * r);
    s.scale.set(26 + R() * 18, 12 + R() * 6, 1);
    s.userData.a = a; s.userData.r = r;
    g.add(s);
    clouds.push(s);
  }
  // Soap bubbles rising around the learner.
  const bm = bubbleMaterial();
  const bubbles = [];
  const bg = new THREE.SphereGeometry(1, 24, 16);
  for (let i = 0; i < 26; i++) {
    const b = new THREE.Mesh(bg, bm);
    const a = R() * Math.PI * 2, r = 3.5 + R() * 6;
    b.position.set(Math.cos(a) * r, R() * 6, Math.sin(a) * r);
    b.scale.setScalar(0.06 + R() * 0.2);
    b.userData.v = 0.08 + R() * 0.15;
    b.userData.ph = R() * 6.28;
    g.add(b);
    bubbles.push(b);
  }
  anim.push((t, dt) => {
    bm.uniforms.uTime.value = t;
    for (const c of clouds) {
      c.userData.a += dt * 0.004;
      c.position.x = Math.cos(c.userData.a) * c.userData.r;
      c.position.z = Math.sin(c.userData.a) * c.userData.r;
    }
    for (const b of bubbles) {
      b.position.y += b.userData.v * dt;
      b.position.x += Math.sin(t * 0.6 + b.userData.ph) * dt * 0.08;
      if (b.position.y > 7) b.position.y = -0.2;
    }
  });
  return {
    sky: { top: '#2f8fe8', horizon: '#d8f1ff', bottom: '#bfe6ff', sun: '#fffbe8', sunDir: [0.5, 0.55, -0.7], sunSize: 0.05, glow: 1.2 },
    fog: ['#d8f1ff', 35, 140], hemi: ['#e6f4ff', '#6fae58', 1.6], sun: ['#fff6e0', 2.2, [4, 8, 3]],
  };
}

function buildTech(g, anim) {
  const R = rng(3);
  const gm = gridMaterial({ color: '#29d3ff', base: '#04101c', cell: 1, major: 5, fade: 45, pulse: true, baseAlpha: 0.92 });
  const floor = new THREE.Mesh(new THREE.PlaneGeometry(100, 100), gm);
  floor.rotation.x = -Math.PI / 2;
  floor.position.y = -0.002;
  g.add(floor);
  // Distant towers with lit window bands.
  const towerMat = new THREE.ShaderMaterial({
    fog: true,
    uniforms: { ...THREE.UniformsLib.fog, uTime: { value: 0 } },
    vertexShader: `#include <fog_pars_vertex>
      varying vec3 vPos; varying vec3 vN; varying float vSeed;
      void main() {
        vec4 w = modelMatrix * instanceMatrix * vec4(position, 1.0);
        vPos = w.xyz; vN = normal; vSeed = instanceMatrix[3].x * 0.37 + instanceMatrix[3].z * 0.71;
        vec4 mvPosition = viewMatrix * w;
        gl_Position = projectionMatrix * mvPosition;
        #include <fog_vertex>
      }`,
    fragmentShader: `#include <fog_pars_fragment>
      uniform float uTime; varying vec3 vPos; varying vec3 vN; varying float vSeed;
      float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
      void main() {
        vec3 c = vec3(0.02, 0.05, 0.09);
        if (abs(vN.y) < 0.5) {
          vec2 cell = floor(vec2(vPos.x + vPos.z, vPos.y) * vec2(1.6, 1.2));
          float on = step(0.62, hash(cell + vSeed + floor(uTime * 0.2 + hash(cell) * 5.0) * 0.01));
          vec2 f = fract(vec2(vPos.x + vPos.z, vPos.y) * vec2(1.6, 1.2));
          float win = step(0.2, f.x) * step(f.x, 0.8) * step(0.3, f.y) * step(f.y, 0.75);
          c += win * on * mix(vec3(0.1, 0.75, 1.0), vec3(0.6, 0.4, 1.0), hash(cell * 1.3)) * 0.9;
        } else c += vec3(0.1, 0.6, 0.9) * 0.15;
        gl_FragColor = vec4(c, 1.0);
        #include <fog_fragment>
      }`,
  });
  const n = 70;
  const towers = new THREE.InstancedMesh(new THREE.BoxGeometry(1, 1, 1), towerMat, n);
  const m = new THREE.Matrix4();
  for (let i = 0; i < n; i++) {
    const a = R() * Math.PI * 2, r = 28 + R() * 30, w = 2 + R() * 4, h = 6 + Math.pow(R(), 2) * 34;
    m.compose(new THREE.Vector3(Math.cos(a) * r, h / 2, Math.sin(a) * r), new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0, 1, 0), -a),
      new THREE.Vector3(w, h, w * (0.6 + R())));
    towers.setMatrixAt(i, m);
  }
  g.add(towers);
  // Data motes rising slowly.
  const N = 500, pts = new Float32Array(N * 3), speed = new Float32Array(N);
  for (let i = 0; i < N; i++) {
    const a = R() * Math.PI * 2, r = 3 + R() * 22;
    pts.set([Math.cos(a) * r, R() * 12, Math.sin(a) * r], i * 3);
    speed[i] = 0.15 + R() * 0.4;
  }
  const pg = new THREE.BufferGeometry();
  pg.setAttribute('position', new THREE.BufferAttribute(pts, 3));
  const motes = new THREE.Points(pg, new THREE.PointsMaterial({
    size: 0.09, map: blobTexture([[0, 'rgba(160,240,255,1)'], [0.4, 'rgba(60,200,255,0.6)'], [1, 'rgba(0,120,255,0)']]),
    transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
  }));
  g.add(motes);
  // A thin glowing ring around the classroom.
  const ring = new THREE.Mesh(new THREE.TorusGeometry(6.5, 0.025, 8, 160), new THREE.MeshBasicMaterial({ color: '#29d3ff' }));
  ring.rotation.x = Math.PI / 2;
  ring.position.y = 0.01;
  g.add(ring);
  anim.push((t, dt) => {
    gm.uniforms.uTime.value = t;
    towerMat.uniforms.uTime.value = t;
    for (let i = 0; i < N; i++) {
      let y = pts[i * 3 + 1] + speed[i] * dt;
      if (y > 12) y = 0;
      pts[i * 3 + 1] = y;
    }
    pg.attributes.position.needsUpdate = true;
  });
  return {
    sky: { top: '#01030a', horizon: '#0b2a44', bottom: '#04101c', sun: '#29d3ff', sunDir: [0, 0.02, -1], glow: 0.35 },
    fog: ['#061626', 20, 75], hemi: ['#9fd8ff', '#06101c', 1.1], sun: ['#bfe9ff', 1.4, [1.5, 4, 2]],
  };
}

const SUN_AZ = Math.atan2(-1, 0.62); // the demo sun's direction around the horizon

function buildDemo(g, anim) {
  const R = rng(5);
  const gm = gridMaterial({ color: '#ff3fb4', base: '#0d0220', cell: 1.5, major: 1, fade: 55, baseAlpha: 0.95 });
  const floor = new THREE.Mesh(new THREE.PlaneGeometry(120, 120), gm);
  floor.rotation.x = -Math.PI / 2;
  floor.position.y = -0.002;
  g.add(floor);
  // Stars.
  const N = 900, pts = new Float32Array(N * 3);
  for (let i = 0; i < N; i++) {
    const u = R() * 2 - 1, a = R() * Math.PI * 2, y = Math.abs(u) * 0.95 + 0.05;
    const r = Math.sqrt(1 - y * y);
    pts.set([Math.cos(a) * r * 90, y * 90, Math.sin(a) * r * 90], i * 3);
  }
  const sg = new THREE.BufferGeometry();
  sg.setAttribute('position', new THREE.BufferAttribute(pts, 3));
  const starMat = new THREE.PointsMaterial({ size: 0.5, color: '#ffffff', map: blobTexture([[0, 'rgba(255,255,255,1)'], [0.3, 'rgba(255,255,255,0.5)'], [1, 'rgba(255,255,255,0)']]),
    transparent: true, depthWrite: false, fog: false, blending: THREE.AdditiveBlending });
  g.add(new THREE.Points(sg, starMat));
  // Wireframe mountain ridges around the horizon.
  const ridge = [];
  const ring = 256;
  for (let i = 0; i < ring; i++) {
    const a = (i / ring) * Math.PI * 2;
    const h = (Math.abs(Math.sin(a * 3.1)) * 9 + Math.abs(Math.sin(a * 7.3 + 1)) * 5 + Math.abs(Math.sin(a * 17.7)) * 2.5) *
      (1 - 0.85 * Math.exp(-Math.pow(Math.atan2(Math.sin(a - SUN_AZ), Math.cos(a - SUN_AZ)), 2) * 6)); // low under the sun
    ridge.push([Math.cos(a), h, Math.sin(a)]);
  }
  const lines = [];
  for (const r of [70, 60]) {
    for (let i = 0; i < ring; i++) {
      const p = ridge[i], q = ridge[(i + 1) % ring], k = r / 70;
      lines.push(p[0] * r, p[1] * k, p[2] * r, q[0] * r, q[1] * k, q[2] * r);
      if (i % 4 === 0) lines.push(p[0] * r, 0, p[2] * r, p[0] * r, p[1] * k, p[2] * r);
    }
  }
  const lg = new THREE.BufferGeometry();
  lg.setAttribute('position', new THREE.Float32BufferAttribute(lines, 3));
  g.add(new THREE.LineSegments(lg, new THREE.LineBasicMaterial({ color: '#b23cff', fog: false, transparent: true, opacity: 0.85 })));
  // Classic rotating vector objects, cycling colour.
  const shapes = [
    [new THREE.IcosahedronGeometry(2.2, 0), [-14, 7, -22]],
    [new THREE.TorusKnotGeometry(1.6, 0.45, 80, 8), [15, 8, -20]],
    [new THREE.OctahedronGeometry(2, 0), [-20, 9, 8]],
    [new THREE.TorusGeometry(2, 0.6, 10, 24), [20, 6, 10]],
  ].map(([geo, p], i) => {
    const mat = new THREE.LineBasicMaterial({ color: '#ffffff', fog: false });
    const o = new THREE.LineSegments(new THREE.WireframeGeometry(geo), mat);
    o.position.set(...p);
    o.userData.h = i * 0.25;
    g.add(o);
    return o;
  });
  const sky = skyMaterial({ top: '#000000', horizon: '#ff2a6d', bottom: '#12051f', sun: '#ffe066', sun2: '#ff2a8a',
    sunDir: [0.62, 0.16, -1], sunSize: 0.17, glow: 1.4, stripes: true, band: 0.12 });
  anim.push((t) => {
    gm.uniforms.uScroll.value = -t * 1.2;
    sky.uniforms.uTime.value = t;
    for (const o of shapes) {
      o.rotation.x = t * 0.4 + o.userData.h * 4;
      o.rotation.y = t * 0.55;
      o.material.color.setHSL((t * 0.05 + o.userData.h) % 1, 0.9, 0.65);
    }
  });
  return {
    skyMaterial: sky,
    fog: ['#14041f', 25, 90], hemi: ['#ffb3e6', '#14041f', 1.2], sun: ['#ffd1f0', 1.5, [1.5, 4, 2]],
  };
}

const BUILDERS = { aero: buildAero, tech: buildTech, demo: buildDemo };

export class Scenery {
  /**
   * scene: the THREE.Scene; room: the plain floor and grid (shown for `night`);
   * hemi, sun: the scene's lights, recoloured per scene.
   */
  constructor({ scene, room, hemi, sun, camera }) {
    Object.assign(this, { scene, room, hemi, sun, camera });
    this.root = new THREE.Group();
    this.root.name = 'scenery';
    scene.add(this.root);
    this.defaults = {
      background: scene.background.clone(), fog: [scene.fog.color.clone(), scene.fog.near, scene.fog.far],
      hemi: [hemi.color.clone(), hemi.groundColor.clone(), hemi.intensity], sun: [sun.color.clone(), sun.intensity, sun.position.clone()],
      far: camera.far,
    };
    this.anim = [];
    this.t = 0;
    this.name = 'night';
    this.ar = false;
  }

  set(name) {
    if (!SCENES[name]) name = 'night';
    this.name = name;
    this.dispose();
    for (const c of [...this.root.children]) this.root.remove(c);
    this.anim = [];
    const d = this.defaults;
    this.scene.fog.color.copy(d.fog[0]); this.scene.fog.near = d.fog[1]; this.scene.fog.far = d.fog[2];
    this.hemi.color.copy(d.hemi[0]); this.hemi.groundColor.copy(d.hemi[1]); this.hemi.intensity = d.hemi[2];
    this.sun.color.copy(d.sun[0]); this.sun.intensity = d.sun[1]; this.sun.position.copy(d.sun[2]);
    this.camera.far = d.far;
    this.room.visible = name === 'night' && !this.ar;
    this.background = d.background;
    if (name !== 'night') {
      const g = new THREE.Group();
      const o = BUILDERS[name](g, this.anim);
      const skyMat = o.skyMaterial ?? skyMaterial(o.sky);
      const sky = new THREE.Mesh(new THREE.SphereGeometry(110, 48, 24), skyMat);
      sky.renderOrder = -10;
      sky.frustumCulled = false;
      g.add(sky);
      if (skyMat.uniforms?.uTime) this.anim.push((t) => { skyMat.uniforms.uTime.value = t; });
      this.scene.fog.color.set(o.fog[0]); this.scene.fog.near = o.fog[1]; this.scene.fog.far = o.fog[2];
      this.hemi.color.set(o.hemi[0]); this.hemi.groundColor.set(o.hemi[1]); this.hemi.intensity = o.hemi[2];
      this.sun.color.set(o.sun[0]); this.sun.intensity = o.sun[1]; this.sun.position.set(...o.sun[2]);
      this.camera.far = 150;
      this.background = new THREE.Color(o.fog[0]);
      // Before the board and the lesson (whose transparent panels don't write depth).
      g.traverse((o) => { if (o !== sky) o.renderOrder = -5; });
      this.root.add(g);
    }
    this.camera.updateProjectionMatrix();
    this.root.visible = !this.ar;
    this.scene.background = this.ar ? null : this.background;
  }

  /** AR shows the real room: no sky, floor or scenery. */
  setAR(on) {
    this.ar = on;
    this.root.visible = !on;
    this.room.visible = this.name === 'night' && !on;
    this.scene.background = on ? null : this.background;
  }

  update(dt) {
    this.t += dt;
    for (const f of this.anim) f(this.t, dt);
  }

  dispose() {
    this.root.traverse((o) => {
      o.geometry?.dispose?.();
      const ms = Array.isArray(o.material) ? o.material : o.material ? [o.material] : [];
      for (const m of ms) { m.map?.dispose?.(); m.dispose?.(); }
    });
  }
}
