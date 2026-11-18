# VCL visualizer

A compact, streamable **visual communication language** (VCL) that a teacher
(an LLM) writes while it talks, and a **WebXR viewer** that animates it on a
board in VR/AR, next to a teacher avatar, in real time.

```
LLM reply ──text chunks──▶ vcl serve (Zig) ──JSON IR over WebSocket──▶ viewers (WebXR, Unity/Quest)
   prose + ```vcl fences        parse · validate · diagnostics             schedule · animate · render
                                └──── diagnostics back to the producer (to feed to the model)
```

* **Language:** [docs/SPEC.md](docs/SPEC.md). For a system prompt: [docs/PRIMER.md](docs/PRIMER.md).
* **Renderer contract** (for new front ends): [docs/IR.md](docs/IR.md).
* **Unity client** (a native Quest app, same stream): [clients/unity/README.md](clients/unity/README.md).

## What it can show

| Area | Ops |
|---|---|
| Geometry and annotation | points, lines, arrows, paths, polygons, rectangles, circles, angle marks, text, LaTeX |
| Math | 2D and 3D axes, function and parametric plots, step plots, surfaces `z = f(x, y)`, contour lines |
| Charts and data | `bars` (stacked, with categories and a legend), `hist`, `scatter`, `errbar` (incl. forest plots), shaded `band`s; heatmaps (`grid`) |
| Structured diagrams | neural networks (`nn`, with error nodes and feedback edges for predictive coding), bit fields (`bits`, for fixed-point formats); `hl n.L1_3`, `color n.E2 red` act on their parts |
| Diagrams | labelled nodes and edges; hierarchical `block`s with named ports, `frame`s around subsystems |
| Circuits | 26 schematic `part`s (passives, sources, diodes, BJTs, MOSFETs, op-amp, logic gates, D flip-flop, mux) with named pins, and right-angle `wire`s with junction dots and bus widths |
| Code and data | syntax-highlighted `code` with line focus, `table`s (truth tables) with row focus, timing diagrams (`wave`) |
| Your files | `svg` (URL or inline), `img`, 3D `model`s from glTF/GLB, STL and OBJ |
| 3D | boxes, spheres, cylinders, models; rotate, spin, and walk around them in VR |
| Teaching | `say`, `point` (the robot points), `hl` pulses, `set … focus=` steps through code/tables, timed and parallel beats |

Pins (`R1.a`, `cpu.clk`, `c.L3`, `t.R2`, `w.clk`) work anywhere a point does,
so wires and arrows attach to the right spot without the model computing
coordinates, and wires drawn between pins follow the parts when they move.

Not there yet:
- Automatic layout: the model chooses coordinates (on a board it can enlarge with `board`). Lint warnings catch blocks
  that are too small, and text, panels, blocks, waves and nodes that run off
  the board or onto each other; labels, lines, shapes and LaTeX aren't checked.
- Smart wire routing: the routes are simple right-angle shapes. A wire doubling
  back around a part needs corner points.
- Analog waveforms on a time axis (use `plot`), state machines (use
  `node`/`edge`), and text-diff views.

## Quick start

```sh
nix develop                 # zig 0.16, node, adb
zig build                   # zig-out/bin/vcl + viewer/vcl.wasm
zig build run -- serve      # http://127.0.0.1:8080
```

Open http://127.0.0.1:8080, click **Editor**, pick a lesson, press **Run** (Ctrl+Enter).

Files you want to show (SVG drawings, CAD exports as STL/OBJ, glTF models)
go in `viewer/assets/`, or anywhere with `vcl serve --assets DIR`. They're then
at `assets/<name>`, and the tutor tells Claude which ones exist.

A folder of lessons elsewhere (`.vcl` files, and `.md` files with ```` ```vcl ````
fences) can be served alongside with `vcl serve --lessons DIR`: they appear in
the web page's lesson list and the Quest app's menu, grouped by subfolder
(`GET /library/index.json` lists them, `/library/<path>` serves each).

Feed it from anywhere:

```sh
curl --data-binary @viewer/lessons/bfs.vcl localhost:8080/vcl            # whole file
curl --data-binary @viewer/lessons/bfs.vcl 'localhost:8080/vcl?pace=400' # one line every 400 ms
node tools/stream.mjs viewer/lessons/mixed-reply.md                     # token-sized chunks, like an LLM
zig-out/bin/vcl check viewer/lessons/*.vcl                              # lint
zig-out/bin/vcl compile viewer/lessons/tour.vcl                         # print IR
```

Every connected viewer animates the same scene; one that joins late is sent a
replay of the current scene. The viewer also works without the server: it
falls back to the same parser compiled to wasm (`?offline=1` forces this).

Viewer URL options: `?lesson=derivative` autoplays a lesson, `?skip=1` jumps
to its end state, `?view=side` puts the desktop camera off to the side, and
`?server=ws://host:8080/ws` connects to another server.

## Talking to the teacher (Claude)

`tools/tutor` connects Claude to a running server. Questions come from the
viewer's **Ask** box, its **Talk** button (where the browser has speech
recognition), push-to-talk in VR, or the tutor's own terminal prompt. The reply
streams straight into the scene: prose becomes speech and subtitles, and
```` ```vcl ```` blocks draw as they arrive.

```sh
nix develop
(cd tools/tutor && npm install)        # official Anthropic SDK
ant auth login                         # or: export ANTHROPIC_API_KEY=…
zig-out/bin/vcl serve &                # if it isn't already running
node tools/tutor/tutor.mjs             # --effort low for snappier replies; --model …
```

* **Model:** Claude Opus 5.5 at `medium` effort. It runs with server-side refusal
  fallbacks (`fallbacks: "default"`), so if a safety classifier declines a turn
  it's retried on Anthropic's recommended model instead of going silent.
* **Self-repair:** lines that fail to parse are sent back to Claude once, with
  the error, and it redraws just those.
* **Context:** each question carries the names currently on the board, so the
  teacher can build on what's there (including what you drew yourself).
* **Test:** `cd tools/tutor && npm test` runs the whole loop against a mock
  API: no credentials, no cost.

## The teacher's voice

By default the robot speaks with [tts-lab](https://github.com/pa255ion/tts-lab)'s
refined formant synthesizer, compiled into the viewer as `tts.wasm` (177 KB,
pinned in `build.zig.zon`). Each `say` is synthesized in a few milliseconds
when it arrives and then lasts exactly as long as its audio, so speech and
drawing stay in step. The voice comes from the robot's head (positional audio
in VR) and its mouth follows the loudness. This works offline and on the
static site.

The header's voice menu switches to the browser's own speech (its timing stays
an estimate) or turns the voice off. Browsers only allow sound after you click
or press a key, so a lesson that autoplays stays silent until then.

## As a static site

The viewer works on any static host. The parser ships as wasm, so lessons
play without the server, and each lesson works as a replayable transcript.
Link straight to one with `…/?lesson=derivative`.

```sh
nix develop -c tools/publish.sh ../neocities-pa255ion   # copies a build into SITE/vcl/
```

The copy is about 220 KB. It's marked static, so it never looks for
`vcl serve`, and the commit hash is stamped into every module import and data
URL, so caches never mix versions. Then commit and push the site and upload it
with the site's `./deploy.sh`.

On Neocities, `.wasm` and `.vcl` need a supporter account. Its
Content-Security-Policy allows the CDN-hosted three.js and MathJax. It also
serves over HTTPS, so **Enter VR** works when the page is opened directly in
the Quest browser, with no cable or adb.

## On the Quest 3S

WebXR needs a secure origin, which means `localhost` or HTTPS.

* **USB (simplest):** enable developer mode, plug in, run
  `adb reverse tcp:8080 tcp:8080`, then open `http://localhost:8080` in the
  Quest browser and press **Enter VR** or **Enter AR** (passthrough).
* **adb over Wi-Fi (no cable after setup):** open `http://localhost:8080/quest.html`
  on the desktop (the **Headset** button) and press **Connect headset**, or
  run `tools/quest-wifi.sh "" 8080`. The page and script switch adb to Wi-Fi
  and forward the port. They need the cable once after each headset restart.
* **Wi-Fi:** run `vcl serve --host 0.0.0.0`, then either put it behind HTTPS
  (for example `tailscale serve 8080` with Tailscale on the headset) or add
  `http://<desktop-ip>:8080` under *Insecure origins treated as secure* in
  the Quest browser's `chrome://flags`.

In VR:

| Control | Does |
|---|---|
| Grip (either hand) | bring the board in front of you |
| Right stick forward / back | move the board farther / closer |
| Left stick forward / back | raise / lower the board |
| A or X, hold | push-to-talk, if the Quest browser offers speech recognition |

Distance and height are remembered on each device.

## Layout

```
core/src/vcl.zig     parser · validator · IR emitter (pure; tests inside)
core/src/main.zig    CLI + HTTP/WebSocket stream server
core/src/wasm.zig    browser build of the parser → viewer/vcl.wasm
viewer/              static WebXR viewer (three.js from CDN, no build step)
  js/runtime.js        IR → three.js entities (one builder per op)
  js/timeline.js       scheduler (reference for other renderers)
  js/expr.js           expression compiler (fallback; the parser samples plot/surf)
  js/avatar.js         teacher stand-in + subtitles
  lessons/             example lessons (.vcl, and .md for mixed prose)
clients/unity/       Unity 2022.3 client: a native Quest app (C# port of the viewer runtime)
tools/stream.mjs     simulate an LLM streaming into the server
docs/                SPEC, IR, PRIMER
```

## Tests

```sh
zig build test                        # parser
node --test viewer/test/*.test.mjs    # expression compiler, scheduler
(cd tools/tutor && npm test)          # tutor loop against a mock API (after zig build)
```

## Not done yet

* Lip-sync from tts-lab's phone timings (currently loudness only), and running
  synthesis in a worker for long bursts of speech.
* A rigged avatar, interacting with entities in VR (grab or ask about a thing),
  `morph` between shapes, and a binary wire format.

## Credits

`tts.wasm` is built from [tts-lab](https://github.com/pa255ion/tts-lab) (MIT),
whose built-in pronunciations come from the CMU Pronouncing Dictionary
(© 1993–2015 Carnegie Mellon University, BSD-style licence). Both licence texts
ship with the viewer in `viewer/licenses/`.
