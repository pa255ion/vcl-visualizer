// Scenery: the world around the board. Each scene sets the sky, fog, light,
// floor and a few slow-moving things far enough away not to compete with the
// board. Everything is generated here (shaders, canvas textures, simple
// geometry): no downloads, nothing to license.
//
//   night  the plain dark room (the original look)
//   aero   bright sky, glossy platform, green hills, clouds and soap bubbles
//   tech   deep blue night, glowing floor grid, distant towers, rising data
//   demo   demoscene / synthwave: striped sun, stars, scrolling grid, vector shapes
//   math   dusk over graph paper: a golden-angle sunflower, drifting symbols, solids, a sine horizon
//   circuits  a circuit board: copper traces carrying pulses, chips and capacitors
//   space  a deck among the stars: ringed planet, nebulae, the galaxy's band, asteroids, meteors
//   lakeside  golden hour on a wooden dock: water, pines, mountain ridges, fireflies, birds
import * as THREE from 'three';

export const SCENES = {
  night: 'Night',
  aero: 'Aero',
  tech: 'Tech',
  demo: 'Demo',
  math: 'Math',
  circuits: 'Circuits',
  space: 'Space',
  lakeside: 'Lakeside',
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

// ── shared helpers for the newer scenes ──

/** Flat strips on the ground (y), one per segment [x0, z0, x1, z1], `w` wide; ends extended by w/2 so joints close. */
function strips(segs, w, y) {
  const pos = [], idx = [];
  for (const [x0, z0, x1, z1] of segs) {
    let dx = x1 - x0, dz = z1 - z0;
    const L = Math.hypot(dx, dz) || 1;
    dx /= L; dz /= L;
    const ax = x0 - dx * w / 2, az = z0 - dz * w / 2, bx = x1 + dx * w / 2, bz = z1 + dz * w / 2;
    const px = -dz * w / 2, pz = dx * w / 2, b = pos.length / 3;
    pos.push(ax + px, y, az + pz, ax - px, y, az - pz, bx - px, y, bz - pz, bx + px, y, bz + pz);
    idx.push(b, b + 2, b + 1, b, b + 3, b + 2);
  }
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  geo.setIndex(idx);
  geo.computeVertexNormals();
  return geo;
}

/** A glyph on a transparent canvas, softly glowing. */
function glyphTexture(text, glow = 'rgba(190,170,255,0.9)') {
  const c = document.createElement('canvas');
  c.width = c.height = 128;
  const g = c.getContext('2d');
  g.font = '86px "Cambria Math", "STIX Two Math", "Latin Modern Math", serif';
  g.textAlign = 'center'; g.textBaseline = 'middle';
  g.shadowColor = glow; g.shadowBlur = 14;
  g.fillStyle = '#ffffff';
  g.fillText(text, 64, 68);
  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  return tex;
}

const wrapAngle = (a) => Math.atan2(Math.sin(a), Math.cos(a));

// ── Math ──

function buildMath(g, anim) {
  const R = rng(13);
  // Graph paper to the horizon.
  const gm = gridMaterial({ color: '#9db8ff', base: '#141436', cell: 0.5, major: 10, fade: 55, baseAlpha: 0.95 });
  const floor = new THREE.Mesh(new THREE.PlaneGeometry(120, 120), gm);
  floor.rotation.x = -Math.PI / 2;
  floor.position.y = -0.002;
  g.add(floor);
  // The axes through the origin (where the learner stands), with ticks every metre.
  const axisSegs = (axis) => {
    const out = [axis === 'x' ? [-45, 0, 45, 0] : [0, -45, 0, 45]];
    for (let k = -40; k <= 40; k++) if (Math.abs(k) >= 7) out.push(axis === 'x' ? [k, -0.18, k, 0.18] : [-0.18, k, 0.18, k]);
    return out;
  };
  g.add(new THREE.Mesh(strips(axisSegs('x'), 0.05, 0.003), new THREE.MeshBasicMaterial({ color: '#ffb86b', transparent: true, opacity: 0.75 })));
  g.add(new THREE.Mesh(strips(axisSegs('z'), 0.05, 0.004), new THREE.MeshBasicMaterial({ color: '#6be3d4', transparent: true, opacity: 0.75 })));
  // A sunflower of golden-angle points around the classroom.
  const golden = Math.PI * (3 - Math.sqrt(5)), N = 700, sp = new Float32Array(N * 3);
  for (let n = 0; n < N; n++) {
    const r = 1.05 * Math.sqrt(n + 45), a = n * golden;
    sp.set([Math.cos(a) * r, 0.03, Math.sin(a) * r], n * 3);
  }
  const sg = new THREE.BufferGeometry();
  sg.setAttribute('position', new THREE.BufferAttribute(sp, 3));
  const flower = new THREE.Points(sg, new THREE.PointsMaterial({
    size: 0.26, color: '#ffcf6b', map: blobTexture([[0, 'rgba(255,255,255,1)'], [0.35, 'rgba(255,230,170,0.6)'], [1, 'rgba(255,200,100,0)']]),
    transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
  }));
  g.add(flower);
  // Symbols drifting in the air.
  const glyphs = ['π', '∑', '∫', '√', '∞', '∂', '∇', 'θ', 'φ', 'λ', 'Δ', 'e', 'i', '≈', '≠', '≤', '∈', 'ℝ', '∀', '∃', 'σ', 'μ', 'ω', '±', '×', 'ψ', 'ζ', 'Ω', 'ℕ', '⊂'];
  const symbols = glyphs.map((t) => {
    const m = new THREE.SpriteMaterial({ map: glyphTexture(t), transparent: true, depthWrite: false, fog: false, opacity: 0.6, color: '#e2dcff' });
    const s = new THREE.Sprite(m);
    let a = R() * Math.PI * 2;
    if (Math.abs(wrapAngle(a + Math.PI / 2)) < 0.35) a += 0.7; // not straight behind the board
    const r = 16 + R() * 30;
    s.userData = { a, r, y: 3 + R() * 13, ph: R() * 6.28 };
    s.scale.setScalar(1.3 + R() * 1.6);
    g.add(s);
    return s;
  });
  // Solids, turning slowly.
  const solids = [
    [new THREE.DodecahedronGeometry(2.4), '#ff9e7a', [-22, 9, -18]],
    [new THREE.IcosahedronGeometry(2.2), '#8fd3ff', [24, 11, -15]],
    [new THREE.TorusGeometry(2, 0.7, 16, 40), '#c9a6ff', [-26, 7, 12]],
    [new THREE.OctahedronGeometry(2.4), '#9dffc4', [21, 8, 19]],
    [new THREE.TetrahedronGeometry(2.6), '#ffe08a', [-3, 14, 30]],
  ].map(([geo, col, p], i) => {
    const o = new THREE.Group();
    o.add(new THREE.Mesh(geo, new THREE.MeshStandardMaterial({ color: col, roughness: 0.55, flatShading: true, transparent: true, opacity: 0.88 })));
    o.add(new THREE.LineSegments(new THREE.EdgesGeometry(geo), new THREE.LineBasicMaterial({ color: '#ffffff', transparent: true, opacity: 0.7 })));
    o.position.set(...p);
    o.userData = { y: p[1], ph: i * 1.3 };
    g.add(o);
    return o;
  });
  // The horizon is a function: two sums of sines all the way round, slowly shifting phase.
  const waves = [[60, '#ffcf6b', 7, 2.2, 5, 0.8, 13], [67, '#9db8ff', 10, 1.6, 3, 0.9, 8]].map(([r, col, y0, a1, f1, a2, f2]) => {
    const n = 360, pts = new Float32Array(n * 3);
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(pts, 3));
    const line = new THREE.LineLoop(geo, new THREE.LineBasicMaterial({ color: col, transparent: true, opacity: 0.75, fog: false }));
    g.add(line);
    return { pts, geo, n, r, y0, a1, f1, a2, f2 };
  });
  // A torus knot drawn as one long curve, far off to the side.
  const kp = [];
  for (let i = 0; i <= 600; i++) {
    const u = (i / 600) * Math.PI * 2 * 2, q = 3 / 2, rr = 9 + 3.5 * Math.cos(q * u);
    kp.push(new THREE.Vector3(rr * Math.cos(u), 3.5 * Math.sin(q * u), rr * Math.sin(u)));
  }
  const knot = new THREE.Line(new THREE.BufferGeometry().setFromPoints(kp), new THREE.LineBasicMaterial({ color: '#ffcf6b', fog: false, transparent: true, opacity: 0.8 }));
  knot.position.set(52, 18, 30);
  g.add(knot);
  anim.push((t, dt) => {
    flower.rotation.y = t * 0.01;
    for (const s of symbols) {
      const u = s.userData;
      u.a += dt * 0.006;
      s.position.set(Math.cos(u.a) * u.r, u.y + Math.sin(t * 0.35 + u.ph) * 0.5, Math.sin(u.a) * u.r);
    }
    for (const o of solids) {
      o.rotation.x = t * 0.13 + o.userData.ph;
      o.rotation.y = t * 0.19;
      o.position.y = o.userData.y + Math.sin(t * 0.4 + o.userData.ph) * 0.6;
    }
    for (const w of waves) {
      for (let i = 0; i < w.n; i++) {
        const a = (i / w.n) * Math.PI * 2;
        w.pts.set([Math.cos(a) * w.r, w.y0 + w.a1 * Math.sin(w.f1 * a + t * 0.35) + w.a2 * Math.sin(w.f2 * a - t * 0.6), Math.sin(a) * w.r], i * 3);
      }
      w.geo.attributes.position.needsUpdate = true;
    }
    knot.rotation.y = t * 0.05;
    knot.rotation.x = 0.5 + Math.sin(t * 0.1) * 0.2;
  });
  return {
    sky: { top: '#0d0b2a', horizon: '#6a5797', bottom: '#141436', sun: '#ffd7a0', sunDir: [-0.5, 0.1, -1], glow: 0.8 },
    fog: ['#2a2452', 30, 110], hemi: ['#e2dcff', '#141436', 1.3], sun: ['#ffe8c8', 1.5, [-3, 6, 2]],
  };
}

// ── Circuits ──

function buildCircuits(g, anim) {
  const R = rng(17);
  // Solder mask, with a faint silkscreen grid (0.1 inch, scaled up).
  const pcb = new THREE.Mesh(new THREE.PlaneGeometry(200, 200), new THREE.MeshStandardMaterial({ color: '#0c3d2c', roughness: 0.45, metalness: 0.1 }));
  pcb.rotation.x = -Math.PI / 2;
  pcb.position.y = -0.004;
  g.add(pcb);
  const silk = new THREE.Mesh(new THREE.PlaneGeometry(120, 120), gridMaterial({ color: '#2f8a63', base: '#000000', cell: 2.54, major: 4, fade: 50, baseAlpha: 0 }));
  silk.rotation.x = -Math.PI / 2;
  silk.position.y = -0.002;
  g.add(silk);
  // Buses of parallel traces routed outward at 45° steps from the classroom's edge.
  const paths = [];
  for (let i = 0; i < 26; i++) {
    const a = R() * Math.PI * 2, r0 = 7 + R() * 0.8;
    let x = Math.cos(a) * r0, z = Math.sin(a) * r0, h = Math.round(a / (Math.PI / 4)) * (Math.PI / 4);
    const mid = [[x, z]];
    while (Math.hypot(x, z) < 50 && mid.length < 12) {
      const L = 3 + R() * 7;
      x += Math.cos(h) * L; z += Math.sin(h) * L;
      mid.push([x, z]);
      const k = R();
      if (k < 0.3) h += Math.PI / 4; else if (k < 0.6) h -= Math.PI / 4;
      const out = Math.atan2(z, x);
      if (Math.abs(wrapAngle(h - out)) > 1.2) h = Math.round(out / (Math.PI / 4)) * (Math.PI / 4);
    }
    // Offset copies of the centre line (mitred at the bends), each stopping at its own length.
    const n = 2 + Math.floor(R() * 6), gap = 0.32;
    for (let j = 0; j < n; j++) {
      const d = (j - (n - 1) / 2) * gap, pts = [];
      for (let k = 0; k < mid.length; k++) {
        const p = mid[k], a0 = mid[Math.max(0, k - 1)], a1 = mid[Math.min(mid.length - 1, k + 1)];
        const t0 = k > 0 ? Math.atan2(p[1] - a0[1], p[0] - a0[0]) : Math.atan2(a1[1] - p[1], a1[0] - p[0]);
        const t1 = k < mid.length - 1 ? Math.atan2(a1[1] - p[1], a1[0] - p[0]) : t0;
        const tm = t0 + wrapAngle(t1 - t0) / 2, m = d / Math.cos(wrapAngle(t1 - t0) / 2);
        pts.push([p[0] - Math.sin(tm) * m, p[1] + Math.cos(tm) * m]);
      }
      const keep = Math.max(2, mid.length - Math.floor(R() * 3));
      paths.push(pts.slice(0, keep));
    }
  }
  const segs = [], pads = [], vias = [];
  for (const pts of paths) {
    for (let i = 1; i < pts.length; i++) segs.push([pts[i - 1][0], pts[i - 1][1], pts[i][0], pts[i][1]]);
    const [ex, ez] = pts[pts.length - 1];
    pads.push([ex - 0.24, ez, ex + 0.24, ez]);
    for (let i = 1; i < pts.length - 1; i++) if (R() < 0.18) vias.push(pts[i]);
  }
  const copper = new THREE.MeshStandardMaterial({ color: '#d39a4c', metalness: 0.85, roughness: 0.3, emissive: '#3a2408' });
  g.add(new THREE.Mesh(strips(segs, 0.12, 0.004), copper));
  g.add(new THREE.Mesh(strips(pads, 0.48, 0.005), copper));
  const viaMesh = new THREE.InstancedMesh(new THREE.RingGeometry(0.07, 0.17, 16).rotateX(-Math.PI / 2), new THREE.MeshStandardMaterial({ color: '#d8dde3', metalness: 0.9, roughness: 0.25 }), vias.length);
  vias.forEach(([x, z], i) => viaMesh.setMatrixAt(i, new THREE.Matrix4().makeTranslation(x, 0.006, z)));
  g.add(viaMesh);
  // Chips (with their pins) and capacitors, all well beyond the classroom.
  const body = new THREE.MeshStandardMaterial({ color: '#15171c', roughness: 0.55 });
  const pinMat = new THREE.MeshStandardMaterial({ color: '#c9ced6', metalness: 0.9, roughness: 0.3 });
  const pinList = [];
  for (let i = 0; i < 18; i++) {
    let a = R() * Math.PI * 2;
    if (Math.abs(wrapAngle(a + Math.PI / 2)) < 0.3) a += 0.6;
    const r = 14 + R() * 26, w = 2 + R() * 4, d = 1.6 + R() * 3, h = 0.45 + R() * 0.5;
    const chip = new THREE.Group();
    const box = new THREE.Mesh(new THREE.BoxGeometry(w, h, d), body);
    box.position.y = h / 2 + 0.05;
    chip.add(box);
    const dot = new THREE.Mesh(new THREE.CircleGeometry(0.12, 16), new THREE.MeshBasicMaterial({ color: '#cfd6df' }));
    dot.rotation.x = -Math.PI / 2;
    dot.position.set(-w / 2 + 0.35, h + 0.06, -d / 2 + 0.35);
    chip.add(dot);
    chip.position.set(Math.cos(a) * r, 0, Math.sin(a) * r);
    chip.rotation.y = Math.round(R() * 4) * (Math.PI / 4);
    chip.updateMatrixWorld();
    const n = Math.max(2, Math.floor(w / 0.55));
    for (let k = 0; k < n; k++) for (const side of [-1, 1]) {
      const p = new THREE.Vector3(-w / 2 + (k + 0.5) * (w / n), 0.12, side * (d / 2 + 0.15)).applyMatrix4(chip.matrixWorld);
      pinList.push([p, chip.rotation.y]);
    }
    g.add(chip);
  }
  const pins = new THREE.InstancedMesh(new THREE.BoxGeometry(0.14, 0.1, 0.34), pinMat, pinList.length);
  const q = new THREE.Quaternion(), one = new THREE.Vector3(1, 1, 1);
  pinList.forEach(([p, ry], i) => pins.setMatrixAt(i, new THREE.Matrix4().compose(p, q.setFromAxisAngle(new THREE.Vector3(0, 1, 0), ry), one)));
  g.add(pins);
  const capBody = new THREE.MeshStandardMaterial({ color: '#1d3478', roughness: 0.35 });
  const capTop = new THREE.MeshStandardMaterial({ color: '#c8ccd4', metalness: 0.85, roughness: 0.3 });
  const stripe = new THREE.MeshStandardMaterial({ color: '#8f98b0', roughness: 0.6 });
  for (let i = 0; i < 12; i++) {
    let a = R() * Math.PI * 2;
    if (Math.abs(wrapAngle(a + Math.PI / 2)) < 0.3) a -= 0.6;
    const r = 20 + R() * 28, cr = 0.8 + R() * 1.0, h = 2 + R() * 3.5;
    const x = Math.cos(a) * r, z = Math.sin(a) * r;
    const c = new THREE.Mesh(new THREE.CylinderGeometry(cr, cr, h, 28), capBody);
    c.position.set(x, h / 2, z);
    const top = new THREE.Mesh(new THREE.CylinderGeometry(cr * 0.97, cr * 0.97, 0.05, 28), capTop);
    top.position.set(x, h + 0.02, z);
    const st = new THREE.Mesh(new THREE.CylinderGeometry(cr * 1.01, cr * 1.01, h * 0.96, 28, 1, true, a + Math.PI - 0.35, 0.7), stripe);
    st.position.set(x, h / 2, z);
    g.add(c, top, st);
  }
  // The classroom's copper ring.
  const ring = new THREE.Mesh(new THREE.TorusGeometry(6.5, 0.04, 8, 160), copper);
  ring.rotation.x = Math.PI / 2;
  ring.position.y = 0.02;
  g.add(ring);
  // Signals: pulses running out along the traces.
  const lens = paths.map((pts) => {
    const c = [0];
    for (let i = 1; i < pts.length; i++) c.push(c[i - 1] + Math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]));
    return c;
  });
  const pulseTex = blobTexture([[0, 'rgba(255,255,255,1)'], [0.25, 'rgba(140,255,200,0.8)'], [1, 'rgba(0,255,140,0)']]);
  const pulses = [];
  for (let i = 0; i < 45; i++) {
    const s = new THREE.Sprite(new THREE.SpriteMaterial({ map: pulseTex, transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, color: R() < 0.25 ? '#ffd27a' : '#8dffc8' }));
    s.scale.setScalar(0.7);
    s.userData = { p: Math.floor(R() * paths.length), d: R() * 30, v: 4 + R() * 5 };
    g.add(s);
    pulses.push(s);
  }
  anim.push((t, dt) => {
    for (const s of pulses) {
      const u = s.userData;
      u.d += u.v * dt;
      const c = lens[u.p], pts = paths[u.p];
      if (u.d >= c[c.length - 1]) { u.p = Math.floor(R() * paths.length); u.d = 0; continue; }
      let k = 1;
      while (c[k] < u.d) k++;
      const f = (u.d - c[k - 1]) / (c[k] - c[k - 1]);
      s.position.set(pts[k - 1][0] + (pts[k][0] - pts[k - 1][0]) * f, 0.08, pts[k - 1][1] + (pts[k][1] - pts[k - 1][1]) * f);
    }
  });
  return {
    sky: { top: '#010604', horizon: '#1d5a40', bottom: '#03100b', sun: '#ffb45c', sunDir: [0.3, 0.04, -1], glow: 0.9 },
    fog: ['#0a2a1e', 24, 85], hemi: ['#c8ffe6', '#0c3d2c', 1.1], sun: ['#ffe2b8', 1.6, [3, 7, 4]],
  };
}

// ── Space ──

/** A gas giant's bands, with a storm. */
function planetTexture() {
  const W = 512, H = 256, c = document.createElement('canvas');
  c.width = W; c.height = H;
  const g = c.getContext('2d'), img = g.createImageData(W, H);
  const pal = [[232, 201, 160], [201, 143, 90], [243, 226, 196], [168, 105, 61], [220, 180, 134]].map((v) => new THREE.Color(v[0] / 255, v[1] / 255, v[2] / 255));
  const col = new THREE.Color();
  for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
    const u = x / W * Math.PI * 2;
    const yy = y + 4 * Math.sin(u * 3 + y * 0.05) + 2 * Math.sin(u * 7 - y * 0.11);
    const b = Math.min(1, Math.max(0, (Math.sin(yy * 0.09) + Math.sin(yy * 0.23 + 1.3) * 0.6 + Math.sin(yy * 0.51) * 0.25) / 3.7 + 0.5));
    const i = Math.min(pal.length - 2, Math.floor(b * (pal.length - 1)));
    col.copy(pal[i]).lerp(pal[i + 1], b * (pal.length - 1) - i);
    const dx = (x - W * 0.62) / 26, dy = (y - H * 0.62) / 12, st = Math.exp(-(dx * dx + dy * dy));
    col.lerp(new THREE.Color('#c0543a'), st * 0.7);
    const o = (y * W + x) * 4;
    img.data[o] = col.r * 255; img.data[o + 1] = col.g * 255; img.data[o + 2] = col.b * 255; img.data[o + 3] = 255;
  }
  g.putImageData(img, 0, 0);
  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  return tex;
}

/** Ring bands: a 1D strip of alpha across the ring's width. */
function ringTexture() {
  const c = document.createElement('canvas');
  c.width = 512; c.height = 4;
  const g = c.getContext('2d');
  for (let x = 0; x < 512; x++) {
    const u = x / 511;
    const a = (0.45 + 0.3 * Math.sin(u * 70) + 0.25 * Math.sin(u * 23 + 1)) * Math.min(1, u * 8, (1 - u) * 6) * (u > 0.62 && u < 0.67 ? 0.1 : 1);
    g.fillStyle = `rgba(240,225,195,${Math.max(0, Math.min(1, a)).toFixed(3)})`;
    g.fillRect(x, 0, 1, 4);
  }
  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  return tex;
}

function buildSpace(g, anim) {
  const R = rng(23);
  // The deck: dark metal with light rings and running lights.
  const deck = new THREE.Mesh(new THREE.CylinderGeometry(6.5, 6.7, 0.15, 96), new THREE.MeshStandardMaterial({ color: '#1b2230', roughness: 0.85, metalness: 0.2 }));
  deck.position.y = -0.076;
  g.add(deck);
  for (const [r, w, c] of [[6.55, 0.05, '#5fd3ff'], [4.2, 0.012, '#2a3a55'], [2.2, 0.012, '#2a3a55']]) {
    const m = new THREE.Mesh(new THREE.TorusGeometry(r, w, 8, 160), new THREE.MeshBasicMaterial({ color: c }));
    m.rotation.x = Math.PI / 2;
    m.position.y = 0.005;
    g.add(m);
  }
  const NL = 48, lights = new THREE.InstancedMesh(new THREE.SphereGeometry(0.06, 10, 8), new THREE.MeshBasicMaterial({ color: '#ffffff' }), NL);
  for (let i = 0; i < NL; i++) {
    const a = (i / NL) * Math.PI * 2;
    lights.setMatrixAt(i, new THREE.Matrix4().makeTranslation(Math.cos(a) * 6.62, 0.02, Math.sin(a) * 6.62));
    lights.setColorAt(i, new THREE.Color('#5fd3ff'));
  }
  g.add(lights);
  // Stars all round (below too), and the galaxy's band.
  const blob = blobTexture([[0, 'rgba(255,255,255,1)'], [0.3, 'rgba(255,255,255,0.5)'], [1, 'rgba(255,255,255,0)']]);
  const tints = ['#ffffff', '#cfe0ff', '#fff4d6', '#ffd2a1', '#aac8ff'].map((c) => new THREE.Color(c));
  const band = new THREE.Vector3(0.25, 1, 0.35).normalize(); // the band's pole
  const e1 = new THREE.Vector3(1, 0, 0).cross(band).normalize(), e2 = band.clone().cross(e1);
  const starLayer = (n, size, inBand) => {
    const pos = new Float32Array(n * 3), col = new Float32Array(n * 3), v = new THREE.Vector3();
    for (let i = 0; i < n; i++) {
      if (inBand) {
        const a = R() * Math.PI * 2, off = (R() + R() + R() - 1.5) * 0.18;
        v.copy(e1).multiplyScalar(Math.cos(a)).addScaledVector(e2, Math.sin(a)).addScaledVector(band, off).normalize();
      } else {
        const u = R() * 2 - 1, a = R() * Math.PI * 2, s = Math.sqrt(1 - u * u);
        v.set(Math.cos(a) * s, u, Math.sin(a) * s);
      }
      pos.set([v.x * 100, v.y * 100, v.z * 100], i * 3);
      const c = tints[Math.floor(R() * tints.length)].clone().multiplyScalar(inBand ? 0.35 + R() * 0.4 : 0.5 + R() * 0.5);
      col.set([c.r, c.g, c.b], i * 3);
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    geo.setAttribute('color', new THREE.BufferAttribute(col, 3));
    return new THREE.Points(geo, new THREE.PointsMaterial({ size, vertexColors: true, map: blob, transparent: true, depthWrite: false, fog: false, blending: THREE.AdditiveBlending }));
  };
  g.add(starLayer(2600, 0.45, false), starLayer(300, 0.8, false), starLayer(3000, 0.3, true));
  // Nebulae along the band.
  const nebTex = (c) => blobTexture([[0, c + '0.55)'], [0.45, c + '0.2)'], [1, c + '0)']]);
  const nebColors = ['rgba(122,60,255,', 'rgba(255,63,168,', 'rgba(47,214,200,', 'rgba(80,120,255,'].map(nebTex);
  for (let i = 0; i < 16; i++) {
    const a = R() * Math.PI * 2;
    const v = e1.clone().multiplyScalar(Math.cos(a)).addScaledVector(e2, Math.sin(a)).addScaledVector(band, (R() - 0.5) * 0.25).normalize();
    const s = new THREE.Sprite(new THREE.SpriteMaterial({ map: nebColors[i % nebColors.length], transparent: true, depthWrite: false, fog: false, blending: THREE.AdditiveBlending, opacity: 0.35 + R() * 0.35 }));
    s.position.copy(v.multiplyScalar(95));
    s.scale.setScalar(28 + R() * 30);
    g.add(s);
  }
  // A ringed gas giant and its moon, off to the right of the board.
  const planet = new THREE.Group();
  planet.position.set(62, 22, -72);
  planet.rotation.z = 0.35;
  const globe = new THREE.Mesh(new THREE.SphereGeometry(13, 64, 32), new THREE.MeshStandardMaterial({ map: planetTexture(), roughness: 1, fog: false }));
  planet.add(globe);
  const rg = new THREE.RingGeometry(17, 28, 160, 1), rp = rg.attributes.position, ruv = rg.attributes.uv;
  for (let i = 0; i < rp.count; i++) ruv.setXY(i, (Math.hypot(rp.getX(i), rp.getY(i)) - 17) / 11, 0.5);
  const rings = new THREE.Mesh(rg, new THREE.MeshBasicMaterial({ map: ringTexture(), transparent: true, side: THREE.DoubleSide, depthWrite: false, fog: false, color: '#d9c9a8' }));
  rings.rotation.x = Math.PI / 2 - 0.25;
  planet.add(rings);
  g.add(planet);
  const moon = new THREE.Mesh(new THREE.SphereGeometry(2.4, 32, 16), new THREE.MeshStandardMaterial({ color: '#9a9ca3', roughness: 1, fog: false }));
  g.add(moon);
  // Tumbling asteroids (vertices displaced by position, so shared corners stay shut).
  const rock = new THREE.IcosahedronGeometry(1, 1), rpos = rock.attributes.position;
  for (let i = 0; i < rpos.count; i++) {
    const x = rpos.getX(i), y = rpos.getY(i), z = rpos.getZ(i);
    const k = 0.75 + 0.5 * Math.abs(Math.sin(x * 12.9 + y * 78.2 + z * 37.7) * 0.5 + Math.sin(x * 4.1 - z * 3.3) * 0.5);
    rpos.setXYZ(i, x * k, y * k, z * k);
  }
  rock.computeVertexNormals();
  const rockMat = new THREE.MeshStandardMaterial({ color: '#7d7468', roughness: 1, flatShading: true });
  const asteroids = [];
  for (let i = 0; i < 24; i++) {
    let a = R() * Math.PI * 2;
    if (Math.abs(wrapAngle(a + Math.PI / 2)) < 0.6) a += 1.2; // the board's side stays clear
    const m = new THREE.Mesh(rock, rockMat);
    m.scale.set(0.4 + R() * 1.6, 0.4 + R() * 1.2, 0.4 + R() * 1.4);
    m.userData = { a, r: 26 + R() * 26, y: -14 + R() * 22, s: (R() - 0.5) * 0.6, w: 0.002 + R() * 0.003 };
    g.add(m);
    asteroids.push(m);
  }
  // Now and then, a meteor.
  const metGeo = new THREE.BufferGeometry();
  metGeo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(6), 3));
  const metMat = new THREE.LineBasicMaterial({ color: '#ffffff', transparent: true, opacity: 0, fog: false });
  g.add(new THREE.Line(metGeo, metMat));
  const met = { next: 3, age: 0, p: new THREE.Vector3(), v: new THREE.Vector3() };
  anim.push((t, dt) => {
    globe.rotation.y = t * 0.01;
    const ma = t * 0.02;
    moon.position.set(62 + Math.cos(ma) * 26, 22 + Math.sin(ma) * 5, -72 + Math.sin(ma) * 26);
    for (const m of asteroids) {
      const u = m.userData;
      u.a += u.w * dt;
      m.position.set(Math.cos(u.a) * u.r, u.y, Math.sin(u.a) * u.r);
      m.rotation.x += u.s * dt;
      m.rotation.y += u.s * 0.7 * dt;
    }
    for (let i = 0; i < NL; i++) {
      const k = Math.pow(Math.max(0, Math.cos((i / NL) * Math.PI * 2 * 3 - t * 1.5)), 8);
      lights.setColorAt(i, new THREE.Color('#1d5f7a').lerp(new THREE.Color('#bff2ff'), k));
    }
    lights.instanceColor.needsUpdate = true;
    met.next -= dt;
    if (met.next <= 0) {
      met.next = 4 + R() * 6;
      met.age = 0;
      const a = R() * Math.PI * 2;
      met.p.set(Math.cos(a) * 80, 25 + R() * 35, Math.sin(a) * 80);
      met.v.set(-Math.sin(a), -0.35, Math.cos(a)).multiplyScalar(45 * (R() < 0.5 ? 1 : -1)).setY(-15);
    }
    met.age += dt;
    if (met.age < 1.2) {
      met.p.addScaledVector(met.v, dt);
      const tail = met.p.clone().addScaledVector(met.v, -0.12);
      metGeo.attributes.position.array.set([met.p.x, met.p.y, met.p.z, tail.x, tail.y, tail.z]);
      metGeo.attributes.position.needsUpdate = true;
      metMat.opacity = Math.sin((met.age / 1.2) * Math.PI) * 0.9;
    } else metMat.opacity = 0;
  });
  return {
    sky: { top: '#000004', horizon: '#05051a', bottom: '#000004', sun: '#fff1d6', sunDir: [-0.6, 0.22, -0.75], sunSize: 0.012, glow: 0.7 },
    fog: ['#02020a', 60, 160], hemi: ['#8a9ad0', '#05050f', 0.8], sun: ['#fff4e0', 1.6, [-6, 2.2, -7.5]],
  };
}

// ── Lakeside ──

const LAKE_SUN = [-0.35, 0.07, -1];

/** Water: small waves, the sky's colours by the Fresnel term, and the sun's glitter path. */
function waterMaterial(o) {
  return new THREE.ShaderMaterial({
    fog: true,
    uniforms: {
      ...THREE.UniformsLib.fog, uTime: { value: 0 },
      uDeep: { value: new THREE.Color(o.deep) }, uHorizon: { value: new THREE.Color(o.horizon) }, uTop: { value: new THREE.Color(o.top) },
      uSun: { value: new THREE.Color(o.sun) }, uSunDir: { value: new THREE.Vector3(...o.sunDir).normalize() },
    },
    vertexShader: `#include <fog_pars_vertex>
      varying vec3 vPos;
      void main() {
        vec4 w = modelMatrix * vec4(position, 1.0);
        vPos = w.xyz;
        vec4 mvPosition = viewMatrix * w;
        gl_Position = projectionMatrix * mvPosition;
        #include <fog_vertex>
      }`,
    fragmentShader: `#include <fog_pars_fragment>
      uniform float uTime; uniform vec3 uDeep, uHorizon, uTop, uSun, uSunDir;
      varying vec3 vPos;
      void wave(vec2 p, vec2 k, float a, float s, inout vec2 grad) {
        float ph = dot(k, p) + uTime * s;
        grad += a * k * cos(ph);
      }
      void main() {
        vec2 p = vPos.xz, grad = vec2(0.0);
        float dist = length(cameraPosition - vPos);
        float damp = 1.0 / (1.0 + dist * 0.12);
        wave(p, vec2(0.9, 0.35), 0.018, 1.1, grad);
        wave(p, vec2(-0.5, 1.2), 0.014, 1.4, grad);
        wave(p, vec2(2.1, -1.3), 0.008, 2.3, grad);
        wave(p, vec2(-3.3, -2.2), 0.005, 3.1, grad);
        wave(p, vec2(4.7, 3.1), 0.003, 4.2, grad);
        grad *= damp;
        vec3 n = normalize(vec3(-grad.x, 1.0, -grad.y));
        vec3 v = normalize(cameraPosition - vPos);
        float fres = 0.08 + 0.9 * pow(1.0 - max(dot(n, v), 0.0), 4.0);
        vec3 r = reflect(-v, n);
        vec3 sky = mix(uHorizon, uTop, clamp(r.y * 2.5, 0.0, 1.0));
        vec3 c = mix(uDeep, sky, fres);
        float sd = max(dot(r, uSunDir), 0.0);
        c += uSun * (pow(sd, 400.0) * 4.0 + pow(sd, 40.0) * 0.35);
        gl_FragColor = vec4(c, 1.0);
        #include <fog_fragment>
      }`,
  });
}

function buildLakeside(g, anim) {
  const R = rng(31);
  const sunDir = new THREE.Vector3(...LAKE_SUN).normalize(), sunAz = Math.atan2(sunDir.z, sunDir.x);
  const water = new THREE.Mesh(new THREE.PlaneGeometry(260, 260), waterMaterial({ deep: '#1d4258', horizon: '#ffc995', top: '#4a78b4', sun: '#fff0c8', sunDir: LAKE_SUN }));
  water.rotation.x = -Math.PI / 2;
  water.position.y = -0.4;
  g.add(water);
  // The dock: planks across, posts down into the water.
  const woods = ['#8b6a4a', '#7a5c3f', '#94734f', '#6f5339'].map((c) => new THREE.Color(c));
  const zs = [];
  for (let z = -7.2; z < 6.8; z += 0.3) zs.push(z);
  const planks = new THREE.InstancedMesh(new THREE.BoxGeometry(1, 1, 1), new THREE.MeshStandardMaterial({ roughness: 0.85 }), zs.length);
  zs.forEach((z, i) => {
    planks.setMatrixAt(i, new THREE.Matrix4().compose(new THREE.Vector3((R() - 0.5) * 0.15, -0.045 + (R() - 0.5) * 0.008, z + 0.14), new THREE.Quaternion(), new THREE.Vector3(13 + (R() - 0.5) * 0.3, 0.08, 0.27)));
    planks.setColorAt(i, woods[Math.floor(R() * woods.length)]);
  });
  g.add(planks);
  const postMat = new THREE.MeshStandardMaterial({ color: '#5a4430', roughness: 0.9 });
  for (const x of [-6.4, 6.4]) for (let z = -7; z <= 6.7; z += 2.75) {
    const p = new THREE.Mesh(new THREE.CylinderGeometry(0.13, 0.15, 1.3, 12), postMat);
    p.position.set(x, -0.45, z);
    g.add(p);
  }
  // Far shore: land with a ragged edge, and pines (none in front of the sun).
  const land = new THREE.RingGeometry(36, 140, 160, 3), lp = land.attributes.position;
  for (let i = 0; i < lp.count; i++) {
    const x = lp.getX(i), y = lp.getY(i), r = Math.hypot(x, y);
    if (r < 40) {
      const a = Math.atan2(y, x), k = (36 + 6 * Math.sin(a * 3 + 1) + 3 * Math.sin(a * 7) + 2 * Math.sin(a * 13 + 2)) / r;
      lp.setXY(i, x * k, y * k);
    }
  }
  const landMesh = new THREE.Mesh(land, new THREE.MeshStandardMaterial({ color: '#2b4430', roughness: 1 }));
  landMesh.rotation.x = -Math.PI / 2;
  landMesh.position.y = -0.3;
  g.add(landMesh);
  const pines = new THREE.InstancedMesh(new THREE.ConeGeometry(1, 1, 7), new THREE.MeshStandardMaterial({ roughness: 1, flatShading: true }), 260);
  const greens = ['#1d3526', '#24402d', '#2a4a33', '#1a2f22'].map((c) => new THREE.Color(c));
  let np = 0;
  for (let i = 0; i < 400 && np < 260; i++) {
    const a = R() * Math.PI * 2;
    if (Math.abs(wrapAngle(a - sunAz)) < 0.22) continue;
    const edge = 36 + 6 * Math.sin(a * 3 + 1) + 3 * Math.sin(a * 7) + 2 * Math.sin(a * 13 + 2);
    const r = edge + 3 + R() * 30, h = 3.5 + R() * 5, w = h * (0.22 + R() * 0.08);
    pines.setMatrixAt(np, new THREE.Matrix4().compose(new THREE.Vector3(Math.cos(a) * r, -0.3 + h / 2, Math.sin(a) * r), new THREE.Quaternion(), new THREE.Vector3(w, h, w)));
    pines.setColorAt(np, greens[Math.floor(R() * greens.length)]);
    np++;
  }
  pines.count = np;
  g.add(pines);
  // Mountain ridges, hazier with distance, low where the sun goes down.
  [[82, '#53607c', 1.0, 0.3], [94, '#7a7c9e', 0.8, 1.7], [106, '#a596b4', 0.65, 3.1]].reverse().forEach(([r, col, k, s]) => {
    const n = 256, pos = [], idx = [];
    for (let i = 0; i <= n; i++) {
      const a = (i / n) * Math.PI * 2, da = wrapAngle(a - sunAz);
      const h = (Math.abs(Math.sin(a * 2 + s)) * 13 + Math.abs(Math.sin(a * 5 + s * 2)) * 6 + Math.abs(Math.sin(a * 11 + s)) * 2) * k * (1 - 0.75 * Math.exp(-da * da * 10));
      pos.push(Math.cos(a) * r, -0.4, Math.sin(a) * r, Math.cos(a) * r, h + 2, Math.sin(a) * r);
      if (i < n) idx.push(i * 2, i * 2 + 1, i * 2 + 2, i * 2 + 1, i * 2 + 3, i * 2 + 2);
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    geo.setIndex(idx);
    g.add(new THREE.Mesh(geo, new THREE.MeshBasicMaterial({ color: col, fog: false, side: THREE.DoubleSide })));
  });
  // Reeds by the dock, swaying.
  const reedMat = new THREE.MeshStandardMaterial({ color: '#5d6b3a', roughness: 0.9 }), headMat = new THREE.MeshStandardMaterial({ color: '#5a3b22', roughness: 1 });
  const clumps = [[-7.3, -6.4], [7.2, 4.8], [-7.1, 2.6], [7.0, -3.9], [-2.5, 7.4], [3.4, 7.3]].map(([x, z]) => {
    const c = new THREE.Group();
    c.position.set(x, -0.4, z);
    for (let i = 0; i < 8; i++) {
      const h = 1.1 + R() * 0.8, ox = (R() - 0.5) * 0.7, oz = (R() - 0.5) * 0.7;
      const st = new THREE.Mesh(new THREE.CylinderGeometry(0.012, 0.02, h, 5), reedMat);
      st.position.set(ox, h / 2, oz);
      st.rotation.set((R() - 0.5) * 0.15, 0, (R() - 0.5) * 0.15);
      c.add(st);
      if (R() < 0.6) {
        const hd = new THREE.Mesh(new THREE.CylinderGeometry(0.045, 0.045, 0.24, 8), headMat);
        hd.position.set(ox, h - 0.2, oz);
        c.add(hd);
      }
    }
    c.userData.ph = R() * 6.28;
    g.add(c);
    return c;
  });
  // Fireflies over the water near the dock.
  const flyTex = blobTexture([[0, 'rgba(255,255,220,1)'], [0.3, 'rgba(255,226,122,0.7)'], [1, 'rgba(255,200,60,0)']]);
  const flies = [];
  for (let i = 0; i < 60; i++) {
    const s = new THREE.Sprite(new THREE.SpriteMaterial({ map: flyTex, transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, opacity: 0 }));
    let a = R() * Math.PI * 2;
    const r = 7.5 + R() * 9;
    s.userData = { x: Math.cos(a) * r, z: Math.sin(a) * r, y: 0.2 + R() * 2.2, ph: R() * 6.28, f: 0.4 + R() * 0.7 };
    s.scale.setScalar(0.16);
    g.add(s);
    flies.push(s);
  }
  // Birds circling far off, wings beating.
  const NB = 6, birdGeo = new THREE.BufferGeometry(), bpos = new Float32Array(NB * 12);
  birdGeo.setAttribute('position', new THREE.BufferAttribute(bpos, 3));
  g.add(new THREE.LineSegments(birdGeo, new THREE.LineBasicMaterial({ color: '#2a2230', fog: false })));
  const birds = Array.from({ length: NB }, (_, i) => ({ a: R() * 6.28, r: 42 + R() * 18, y: 15 + R() * 9, v: 0.03 + R() * 0.02, ph: R() * 6.28, s: 0.7 + R() * 0.4 }));
  // A few warm clouds, low and far.
  const clouds = [];
  for (let i = 0; i < 9; i++) {
    const s = new THREE.Sprite(new THREE.SpriteMaterial({ map: cloudTexture(41 + i * 17), transparent: true, depthWrite: false, fog: false, opacity: 0.75, color: '#ffd2b8' }));
    const a = R() * Math.PI * 2, r = 70 + R() * 20;
    s.position.set(Math.cos(a) * r, 18 + R() * 14, Math.sin(a) * r);
    s.scale.set(30 + R() * 20, 9 + R() * 5, 1);
    s.userData = { a, r };
    g.add(s);
    clouds.push(s);
  }
  const wm = water.material;
  anim.push((t, dt) => {
    wm.uniforms.uTime.value = t;
    for (const c of clumps) c.rotation.z = Math.sin(t * 0.8 + c.userData.ph) * 0.035;
    for (const s of flies) {
      const u = s.userData;
      s.position.set(u.x + Math.sin(t * 0.3 * u.f + u.ph) * 0.8, u.y + Math.sin(t * 0.5 * u.f + u.ph * 2) * 0.3, u.z + Math.cos(t * 0.27 * u.f + u.ph) * 0.8);
      s.material.opacity = Math.pow(Math.max(0, Math.sin(t * u.f + u.ph)), 6);
    }
    birds.forEach((b, i) => {
      b.a += b.v * dt;
      const cx = Math.cos(b.a) * b.r, cz = Math.sin(b.a) * b.r, fx = -Math.sin(b.a), fz = Math.cos(b.a); // heading
      const sx = -fz, sz = fx, flap = Math.sin(t * 5 + b.ph) * 0.5 * b.s, span = 1.1 * b.s;
      bpos.set([cx + sx * span, b.y + flap, cz + sz * span, cx, b.y, cz, cx, b.y, cz, cx - sx * span, b.y + flap, cz - sz * span], i * 12);
    });
    birdGeo.attributes.position.needsUpdate = true;
    for (const c of clouds) {
      c.userData.a += dt * 0.003;
      c.position.x = Math.cos(c.userData.a) * c.userData.r;
      c.position.z = Math.sin(c.userData.a) * c.userData.r;
    }
  });
  return {
    sky: { top: '#3e6fae', horizon: '#ffc58f', bottom: '#f6c9a2', sun: '#fff1c8', sunDir: LAKE_SUN, sunSize: 0.035, glow: 1.8 },
    fog: ['#f1c39c', 45, 150], hemi: ['#ffe0bd', '#4a5e48', 1.4], sun: ['#ffd49a', 2.0, [-3.5, 1.2, -10]],
  };
}

const BUILDERS = { aero: buildAero, tech: buildTech, demo: buildDemo, math: buildMath, circuits: buildCircuits, space: buildSpace, lakeside: buildLakeside };

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
