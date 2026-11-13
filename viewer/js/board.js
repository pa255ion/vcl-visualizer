// The board the teacher draws on, resizable with `board W,H[,D]`.
//
// W×H is the usable area in board units (x -W/2..W/2, y -H/2..H/2); the slate
// is half a unit larger all round. The board grows sideways and upwards: its
// bottom edge stays at the same height, so the subtitles below never move.
// D > 0 adds a stage in front of the board: a floor plate D units deep at the
// board's foot, for 3D content that comes toward the learner.
import * as THREE from 'three';
import { STAGE_SCALE, roundedRectPoints, Stroke } from './draw.js';

export const DEFAULT_BOARD = [16, 9, 0];
const MARGIN = [0.5, 0.4];

export class Board {
  /** `stage` is the board's coordinate system; `center` its centre (metres) at the default size. */
  constructor(stage, center) {
    this.stage = stage;
    this.home = center.clone();
    this.size = [...DEFAULT_BOARD];
    this.listeners = [];

    this.mesh = new THREE.Mesh(
      new THREE.BufferGeometry(),
      new THREE.MeshBasicMaterial({ color: '#121924', transparent: true, opacity: 0.96, depthWrite: false }),
    );
    this.mesh.position.z = -0.05;
    this.mesh.renderOrder = 0;
    this.edge = new Stroke({ color: '#2a3647', width: 0.05 });

    // Stage (depth > 0): floor plate, grid and rim.
    this.floor = new THREE.Mesh(
      new THREE.PlaneGeometry(1, 1),
      new THREE.MeshBasicMaterial({ color: '#16202e', transparent: true, opacity: 0.85, depthWrite: false, side: THREE.DoubleSide }),
    );
    this.floor.rotation.x = -Math.PI / 2;
    this.floor.renderOrder = 0;
    this.floorGrid = new THREE.GridHelper(1, 1, '#2a3647', '#1d2735');
    this.floorGrid.material.transparent = true;
    this.floorGrid.material.opacity = 0.7;
    this.volume = new Stroke({ color: '#2a3647', width: 0.05 });
    stage.add(this.mesh, this.edge.obj, this.floor, this.floorGrid, this.volume.obj);
    this.apply(DEFAULT_BOARD);
  }

  get width() { return this.size[0]; }
  get height() { return this.size[1]; }
  get depth() { return this.size[2]; }

  /** Call `fn(size)` whenever the size changes (camera fit, avatar, title). */
  onChange(fn) { this.listeners.push(fn); }

  /** Set the size now (no animation). */
  apply([w, h, d]) {
    this.size = [w, h, d];
    const ow = w + 2 * MARGIN[0], oh = h + 2 * MARGIN[1];
    const outline = roundedRectPoints(ow, oh, 0.35);
    this.mesh.geometry.dispose();
    this.mesh.geometry = new THREE.ShapeGeometry(new THREE.Shape(outline.map((p) => new THREE.Vector2(p.x, p.y))));
    this.edge.set([outline.map((p) => p.setZ(-0.04))]);

    // Grow upwards: keep the bottom edge where the default board has it.
    const lift = (h - DEFAULT_BOARD[1]) / 2 * STAGE_SCALE;
    this.stage.position.set(this.home.x, this.home.y + lift, this.home.z);

    const show = d > 0.01;
    this.floor.visible = this.floorGrid.visible = this.volume.obj.visible = show;
    if (show) {
      const y = -oh / 2;
      this.floor.scale.set(ow, d, 1);
      this.floor.position.set(0, y, d / 2 - 0.05);
      // One-unit cells, rebuilt only when the count changes.
      const cells = Math.max(1, Math.round(Math.max(ow, d)));
      if (cells !== this.cells) {
        this.cells = cells;
        const tmp = new THREE.GridHelper(1, cells, '#2a3647', '#1d2735');
        this.floorGrid.geometry.dispose();
        this.floorGrid.geometry = tmp.geometry;
        tmp.material.dispose();
      }
      this.floorGrid.scale.set(ow, 1, d);
      this.floorGrid.position.set(0, y + 0.01, d / 2 - 0.05);
      const x0 = -ow / 2, x1 = ow / 2, z = d - 0.05;
      const P = (x, zz) => new THREE.Vector3(x, y + 0.02, zz);
      this.volume.set([[P(x0, -0.05), P(x0, z), P(x1, z), P(x1, -0.05)]]);
    }
    for (const fn of this.listeners) fn(this.size);
  }

  /** A timeline task that animates to `size` (null: the default). */
  tween(size) {
    let from;
    const to = size ?? DEFAULT_BOARD;
    const lerp = (p) => from.map((a, i) => a + (to[i] - a) * p);
    return {
      begin: () => { from = [...this.size]; },
      update: (p) => this.apply(lerp(p)),
      finish: () => this.apply([...to]),
    };
  }

  /** Board-space box (metres, world) that should be in view: the slate, the stage, and `extra` (avatar). */
  worldBounds() {
    const [w, h, d] = this.size;
    const box = new THREE.Box3();
    const hw = w / 2 + MARGIN[0], hh = h / 2 + MARGIN[1];
    for (const v of [[-hw, -hh, 0], [hw, hh, 0], [-hw, -hh, d], [hw, -hh, d]])
      box.expandByPoint(this.stage.localToWorld(new THREE.Vector3(...v)));
    return box;
  }
}
