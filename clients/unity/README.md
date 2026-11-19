# VCL Unity client (Quest)

A second renderer for the VCL IR stream (`docs/IR.md`), built with Unity
2022.3.22f1 (the version VRChat uses). It connects to `vcl serve` over the same
WebSocket as the web viewer and draws the same lessons, as a standalone Quest
app. A flat desktop view comes up when no headset is active, which is what the
editor's play mode shows.

It is a C# port of the web viewer's runtime (`viewer/js/runtime.js`,
`timeline.js`, `board.js`, `avatar.js`): the same scheduler (including `par`
blocks and step mode), anchors, entrance effects and change ops. The server
samples `plot` and `surf` itself, so no client needs an expression evaluator.

Where the browser lends the web viewer a hand, this client does the work
itself:

* **Voice:** tts-lab, the same formant synthesizer as the web viewer's robot
  voice, built by Zig as a native library (`libtts.so` for the Quest's arm64
  and for the Linux editor; `nix develop -c zig build unity-tts`). Each `say`
  is synthesized as it arrives (a few ms) and lasts exactly as long as its
  audio, played from the teacher's head.
* **`tex`:** typeset by a small TeX-style layout engine (`Math.cs`) in DejaVu
  Serif: fractions, scripts, radicals, big operators with limits, `\left…\right`
  and `\big` delimiters, accents, braces, matrices and `cases`/`aligned`, with
  TeX's spacing between atoms. MathJax isn't available in Unity.
* **`svg`:** parsed and drawn as meshes (`Svg.cs`): paths (arcs included),
  basic shapes, text, fill/stroke/opacity, transforms and `style`. Gradients
  draw as a flat grey; holes in a fill are not cut.
* **`model`:** glTF/GLB, STL and OBJ are read by `Model.cs` (meshes, node tree,
  base colour and texture); no skins, animation or Draco compression.

## Build and install

You need the `unity` editor from the NixOS config (it pins 2022.3.22f1 with
Android support). The Android SDK, NDK r23b and JDK 11 come from this repo's
flake (`nix develop .#unity`), and the build script enters that shell itself:

```sh
clients/unity/tools/build-quest.sh      # → clients/unity/Builds/vcl-quest.apk
```

With the Quest plugged in by USB (developer mode on):

```sh
nix develop -c zig build                # the server, if not built yet
./zig-out/bin/vcl serve --port 8093 --lessons ~/path/to/lessons &   # --lessons is optional
clients/unity/tools/install-quest.sh    # installs, forwards port 8093 over USB, starts the app
```

To go wireless, run `vcl serve --host 0.0.0.0 --port 8093`, then give the app
your desktop's address: `tools/install-quest.sh ws://192.168.1.20:8093/ws`.
That writes it to the app's `server.txt`. Then load lessons from the web page
(`http://localhost:8093`) or let the tutor talk. Everything the server
broadcasts plays on every connected viewer.

The NixOS firewall blocks port 8093 from the LAN. Without opening it, adb over
Wi-Fi carries the port forward instead. The cable is needed once per headset boot.
Two ways to set that up:

* **Headset page:** open `http://localhost:8093/quest.html` (the **Headset** button
  in the viewer). It shows whether the headset is linked and has a **Connect
  headset** button. With **Keep connected** ticked, it relinks on its own while
  the page is open.
* **Terminal:** `tools/quest-wifi.sh [headset-ip]`. Run it again whenever the
  link drops. It remembers the last address.

**Controls:**

| | Headset | Desktop |
|---|---|---|
| Lesson menu | A or X | M (arrows, Enter, Esc) |
| Press a button (menu, question, transcript) | point, trigger | mouse |
| Pause / resume | B or Y | Space |
| Back / forward a step | thumbstick left / right | ← / → |
| Next step (step mode) | trigger | Space, Enter |
| Step mode on/off | menu | S |
| Recentre | thumbstick click | |
| Upright ↔ reclined | hold thumbstick click (0.8 s) | |

Going **back** restarts the current step if it has played for more than 3
seconds, else goes to the one before. **Forward** skips the rest of the step
(an open question counts as skipped).

The **transcript** beside the board lists the lesson's sentences and questions
(✓ right, ✗ wrong, – skipped), the current one marked. It has back, pause and
forward buttons, and pointing at a line and pulling the trigger jumps there.
It also shows when the lesson has **finished**, with the score if it asked
questions; the teacher's subtitle says so too.

**Questions** (`quiz`, `pick`, `estimate`, `recall`) appear on a card in front
of the board: point and pull the trigger to answer (for `pick`, at the
numbered markers on the board; for `estimate`, hold the trigger on the slider
and move). The card shows what was right, the teacher says why, and Continue
goes on. Answers go to the server for the tutor.

The menu also has a **Voice** switch (robot voice or subtitles only) and a
**Transcript** switch, both remembered.

The **menu** lists the server's built-in lessons and its `--lessons` library,
one folder per subfolder; point with either controller and pull the trigger.
It also has Stop, step mode, Upright/Recline, Voice and Transcript. Picking a lesson clears the
board and posts the lesson to the server, so every connected viewer plays it.

**Reclined** (for lying down): recentring aligns the whole room with your view,
pitch and roll included, so the board sits straight ahead of your eyes
whichever way you lie. Upright recentring turns the room about the vertical
only. The choice is remembered.

## Working on it

`tools/unity.sh` runs the pinned editor on this project, with or without
arguments. It first copies the editor's built-in packages into
`Library/PackageCache` with write permission. On NixOS they sit in the
read-only nix store, and Unity's Package Manager otherwise fails with `EACCES`
while copying them.

```sh
tools/unity.sh                                       # open the editor
tools/unity.sh -batchmode -quit -executeMethod Vcl.EditorTools.VclBuild.Setup
```

`Setup` (also menu **VCL → Set up project**) is idempotent: it unpacks
TextMesh Pro's resources, sets the player options (package
`io.github.pa255ion.vcl`, IL2CPP/ARM64, GLES3, linear colour), turns on
OpenXR with Meta Quest support and the Touch controller profile for Android,
and writes `Assets/Vcl/Main.unity`. The Quest build runs it first.

### Snapshot test (no headset)

This renders a lesson in the editor, headless, after playing it to the end
(`VCL_IR` may list several files, `a.jsonl:b.jsonl`; with `VCL_SHOTS=DIR`
every board becomes `DIR/NAME-K.png`, and `VCL_MENU=1` adds the menu):

```sh
../../zig-out/bin/vcl compile ../../viewer/lessons/garden.vcl > /tmp/garden.jsonl
VCL_IR=/tmp/garden.jsonl VCL_SHOT=/tmp/garden.png \
  tools/unity.sh -batchmode -quit -executeMethod Vcl.EditorTools.VclBuild.Snapshot -logFile -
```

It needs a display for the GPU context (no `-nographics`). Files a lesson
names (`svg`, `model`, `img`) are read from `viewer/` (or `VCL_ASSETS=DIR`),
and the log says whether the speech library loaded (`[vcl] voice: …`).

## Layout

```
Assets/Vcl/Scripts/   Runtime.cs (entities, pins, parts, change ops) · Builders.cs (shapes, text, axes, solids)
                      Math.cs (tex typesetting) · Svg.cs · Model.cs (glTF, STL, OBJ) · Voice.cs (tts-lab speech)
                      Diagrams.cs (part, wire, block, frame, code, table, wave, img) · Charts.cs (nn, grid,
                      bits, bars, hist, scatter, errbar, band, contour) · Parts.cs (symbols, wire routing)
                      Panels.cs (syntax highlighting, wave parsing) · Timeline.cs · Board.cs · Avatar.cs
                      Draw.cs (strokes, fills, text, fonts) · Json.cs · Connection.cs (WebSocket)
                      Menu.cs (lesson menu) · VclApp.cs (scene, input, headset, lessons over HTTP)
Assets/Vcl/Resources/ VclUnlit.shader (stereo-instancing safe) · VclLit.shader
                      Fonts/ DejaVu Sans (+ Mono, Serif): fallback glyphs, code panels, maths
Assets/Plugins/       libtts.so (arm64-v8a for the Quest, x86_64 for the editor), from `zig build unity-tts`
Assets/Vcl/Editor/    VclBuild.cs (Setup, Quest, Snapshot)
tools/                unity.sh · build-quest.sh · install-quest.sh
```

## Differences from the web viewer

- **Voice:** the robot voice only; there is no system speech engine on the
  Quest to match the web viewer's "browser" voice.
- **Lip-sync** follows loudness, as on the web.
- **`tex`** covers what lessons use; an unknown command shows as `\name` so it
  is noticed rather than silently dropped.

Unity's OpenXR plugin can't target Linux desktops, so this client doesn't run
through WiVRn. A Godot client would be the way to do that.
