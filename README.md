# VCL visualizer

A compact, streamable **visual communication language** (VCL) that a teacher
(an LLM) writes while it talks, and a **WebXR viewer** that animates it on a
board in VR/AR, next to a teacher avatar, in real time.

```
LLM reply ──text chunks──▶ vcl serve (Zig) ──JSON IR over WebSocket──▶ viewers (WebXR / later Unity)
   prose + ```vcl fences        parse · validate · diagnostics             schedule · animate · render
                                └──── diagnostics back to the producer (to feed to the model)
```

* **Language:** [docs/SPEC.md](docs/SPEC.md). For a system prompt: [docs/PRIMER.md](docs/PRIMER.md).
* **Renderer contract** (for new front ends such as Unity): [docs/IR.md](docs/IR.md).

## Quick start

```sh
nix develop                 # zig 0.16, node, adb
zig build                   # zig-out/bin/vcl + viewer/vcl.wasm
zig build run -- serve      # http://127.0.0.1:8080
```

Open http://127.0.0.1:8080, click **Editor**, pick a lesson, press **Run** (Ctrl+Enter).

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
* **Wi-Fi:** run `vcl serve --host 0.0.0.0`, then either put it behind HTTPS
  (for example `tailscale serve 8080` with Tailscale on the headset) or add
  `http://<desktop-ip>:8080` under *Insecure origins treated as secure* in
  the Quest browser's `chrome://flags`.

In VR, squeeze a grip button to bring the board in front of you.

## Layout

```
core/src/vcl.zig     parser · validator · IR emitter (pure; tests inside)
core/src/main.zig    CLI + HTTP/WebSocket stream server
core/src/wasm.zig    browser build of the parser → viewer/vcl.wasm
viewer/              static WebXR viewer (three.js from CDN, no build step)
  js/runtime.js        IR → three.js entities (one builder per op)
  js/timeline.js       scheduler (reference for other renderers)
  js/expr.js           safe expression compiler for plot/surf
  js/avatar.js         teacher stand-in + subtitles
  lessons/             example lessons (.vcl, and .md for mixed prose)
tools/stream.mjs     simulate an LLM streaming into the server
docs/                SPEC, IR, PRIMER
```

## Tests

```sh
zig build test                        # parser
node --test viewer/test/*.test.mjs    # expression compiler, scheduler
```

## Not done yet

* Real LLM bridge: stream a model's reply into `/ws?mode=mixed`, then return
  diagnostics to it as tool output.
* TTS with real timing. `say` uses an estimate, and the "voice" checkbox uses
  the browser's speech synthesis.
* A rigged avatar, interacting with entities in VR (grab or ask about a thing),
  `morph` between shapes, tables, a binary wire format, and the Unity client.
