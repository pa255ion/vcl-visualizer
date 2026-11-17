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
./zig-out/bin/vcl serve --port 8093 &
clients/unity/tools/install-quest.sh    # installs, forwards port 8093 over USB, starts the app
```

To go wireless, run `vcl serve --host 0.0.0.0 --port 8093`, then give the app
your desktop's address: `tools/install-quest.sh ws://192.168.1.20:8093/ws`.
That writes it to the app's `server.txt`. Then load lessons from the web page
(`http://localhost:8093`) or let the tutor talk. Everything the server
broadcasts plays on every connected viewer.

**Controls:** trigger = next step (in step mode), B/Y = step mode on/off,
thumbstick click = recentre the classroom in front of you. On the desktop,
Space/→/Enter is next and S toggles step mode.

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

This renders a lesson in the editor, headless, after playing it to the end:

```sh
../../zig-out/bin/vcl compile ../../viewer/lessons/garden.vcl > /tmp/garden.jsonl
VCL_IR=/tmp/garden.jsonl VCL_SHOT=/tmp/garden.png \
  tools/unity.sh -batchmode -quit -executeMethod Vcl.EditorTools.VclBuild.Snapshot -logFile -
```

It needs a display for the GPU context (no `-nographics`).

## Layout

```
Assets/Vcl/Scripts/   Runtime.cs (entities, anchors, change ops) · Builders.cs (one per drawing op)
                      Timeline.cs · Board.cs · Avatar.cs · Draw.cs (strokes, fills, text) · Json.cs
                      Connection.cs (WebSocket) · VclApp.cs (scene, input, headset, status line)
Assets/Vcl/Resources/ VclUnlit.shader (stereo-instancing safe) · VclLit.shader
Assets/Vcl/Editor/    VclBuild.cs (Setup, Quest, Snapshot)
tools/                unity.sh · build-quest.sh · install-quest.sh
```

## Not drawn yet

The following ops play as a dimmed `[op]` placeholder, so a lesson still runs
through, and the status line notes each one: `img`, `svg`, `model`, `part`,
`wire`, `block`, `frame`, `code`, `table`, `wave`, and the charts and
diagrams `nn`, `grid`, `bits`, `bars`, `hist`, `scatter`, `errbar`, `band` and
`contour` (their data arrive parsed, see `docs/IR.md`). A change aimed at one
of their parts (`hl n.L1_3`) acts on the whole placeholder. Also missing:

- **`tex`:** shown as plain text with Unicode symbols (`\frac`, Greek letters,
  sub- and superscripts), not typeset.
- **`set … focus=`:** not supported.
- **Speech:** there is no voice yet; the subtitles carry the text.
- **`surf … wire=true`:** draws grid lines rather than a true wireframe.

Unity's OpenXR plugin can't target Linux desktops, so this client doesn't run
through WiVRn. A Godot client would be the way to do that.
