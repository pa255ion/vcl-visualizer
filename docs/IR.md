# VCL IR — the renderer contract

The server (or the wasm parser) turns VCL text into **IR**: one JSON object per
command. Renderers only ever see IR, so a new front end (Unity, Godot, a 2D web
canvas) never needs its own VCL parser. The parser has already validated
everything: names exist, types are right, and `plot`'s axes really are axes.

## Transport

`vcl serve` exposes:

| Endpoint | Direction | Payload |
|---|---|---|
| `GET /ws` (WebSocket) | server → client | one IR object per text message |
| `GET /ws` | client → server | VCL text chunks (lines run when their `\n` arrives). `?mode=mixed` for prose + fences, `?listen=0` for send-only producers |
| `POST /vcl?mode=&pace=` | client → server | VCL text; reply is plain-text diagnostics. `pace=MS` sleeps between lines to mimic streaming |
| `GET /scene` | server → client | `{"cmds":[…]}`: everything needed to rebuild the current scene |
| `POST /ask` | client → server | a learner question (plain text); broadcast as an `ask` event |
| `POST /status` | tutor → server | `thinking` or `idle`; broadcast as a `status` event |
| `GET /lessons/index.json` | server → client | built-in lessons: `[{"file","title"}]` (files at `/lessons/<file>`) |
| `GET /library/index.json` | server → client | the `--lessons DIR` library: `{"lessons":[{"file","title"}]}` (files at `/library/<file>`; 404 without one) |

On connect, a listening WebSocket client first gets
`{"op":"replay","cmds":[…]}`. Apply those commands **instantly** (no
animation, no timeline) after clearing your scene.

## Envelope

```json
{"op":"line","id":"a","line":3,"par":false,"dur":0.6,"ease":"out", ...fields}
```

| Field | Meaning |
|---|---|
| `op` | opcode (same names as VCL) |
| `id` | entity name; present on everything that creates something (`_N` if auto-assigned) |
| `line` | source line number (for error messages) |
| `par` | start together with the previous command (also set on every line after the first in a `par` … `end` block) |
| `dur` | seconds; always present |
| `ease` | `linear in out inout`; absent means `inout` |

All other fields are the op's arguments, named as in the spec tables
(`from`, `to`, `at`, `pts`, `r`, `size`, `text`, …), plus any keys given.

### Value encodings

| VCL | IR |
|---|---|
| number | number |
| vector / point literal | `[x, y, z]` (always 3) |
| point reference | `{"ref":"A","anchor":"top","off":[0,0.3,0]}`; `anchor` is a standard anchor or a pin name (`"b"`, `"clk"`, `"L3"`) |
| colour | `"#rrggbb"` |
| range | `[lo, hi]` |
| bool | `true` / `false` |
| repeatable args | arrays (`pts`, `ids`, `exprs`) |

### Conversation events

Not commands and never replayed. A renderer shows them on the teacher:

```json
{"op":"ask","text":"Why does the slope change?"}   // a learner question (POST /ask)
{"op":"status","text":"thinking"}                  // or "idle" (POST /status, from the tutor)
```

The tutor (`tools/tutor`) listens for `ask`, streams Claude's reply back in
mixed mode, and posts `status` around each turn.

A viewer posts each answer to a question (SPEC §4.10) to `POST /answer` as a
JSON object; the server broadcasts it with `"op":"answer"`:

```json
{"op":"answer","id":"q1","line":12,"kind":"quiz","q":"…","given":[1],"correct":false,"skipped":false,"options":["…","…"],"answer":[2]}
```

`given` is the chosen numbers (quiz, pick: 1-based), the value (estimate), or
true/false (recall: "I knew it"); `null` when skipped. A pick answer carries
`ids`, an estimate `answer` and `unit`. The tutor reacts to answers to the
questions it asked.

### Diagnostics

```json
{"op":"diag","level":"error","line":7,"msg":"arrow: unknown name 'Bb' (did you mean 'B'?)"}
```

`level` is `error` (line skipped) or `warning` (line ran, something ignored).

## Scheduling (every renderer must do this)

Keep a cursor `T` and the previous start `S`. When command `c` arrives at time `now`:

```
start = max(c.par ? S : T, now)
T     = c.par ? max(T, start + c.dur) : start + c.dur
S     = start
```

Run the command at `start`, animate over `dur`, finish at `start + dur`.
A renderer that synthesizes speech may replace a `say`'s `dur` with the audio's
length **when the command arrives**, before scheduling it.
Resolve point references **when the command starts**, not when it arrives.
`viewer/js/timeline.js` is the reference implementation (about 80 lines).

Step-by-step playback (optional): hold a non-`par` `say` once its start time
comes, until the learner asks for the next step, then shift it and everything
queued behind it by the time spent waiting. Commands that arrive meanwhile are
scheduled from `T` as usual, not from `now`. The WebXR viewer holds every `say`
after the first of a burst (a burst ends when the timeline runs empty).

**Questions** (`quiz`, `pick`, `estimate`, `recall`) hold the timeline the same
way, from their start until the learner answers and continues; only that
releases them, not "next step". Their `dur` is 0.

**Steps, pause, back and forward** (both viewers): number the steps as
commands arrive (a step starts at each non-`par` `say` and each question) and
keep every command with its step. Pausing stops the timeline's clock (and the
speech). Forward finishes the current step's commands at once (talk, waits and
questions are dropped, not run) and starts the next step now. Back resets the
scene and re-runs every kept command: those before the target step instantly,
the rest scheduled as usual. `viewer/js/runtime.js` (`forward`, `back`,
`seek`) is the reference.

## Sampled plots and surfaces

The parser evaluates `plot` and `surf` expressions itself (an expression error
is a parse error), so renderers never need an expression evaluator. Both keep
their source fields (`exprs`, `expr`) and add, in the axes' **data**
coordinates:

| Op | Field | Meaning |
|---|---|---|
| `plot` | `polys` | `[[[x,y,z], …], …]`: polylines (a curve breaks at poles and where it leaves the y range; z is 0 except for 3-expression curves) |
| `surf` | `zgrid` | `{"n":N,"z":[…]}`: (N+1)² heights, row-major, rows along y from `y[0]`, columns along x from `x[0]` (the `x`/`y` keys, else the axes' ranges); clamped to the axes' z range |

Map them through the axes like any `on=` data point. Numbers are rounded to
about 1/20000 of the axis range (on a log axis, to about 1/2000 of its low end;
a plot on a log x axis is sampled evenly in log x).

`contour` adds `contours`: `[{"z":level,"polys":[[[x,y], …], …]}, …]`, one
entry per level from low to high; each curve is one polyline (a closed loop
repeats its first point at the end). `plot … step=true` comes as `polys` that
already have their flat runs and vertical risers.

## Parsed data

The data strings of charts and diagrams are parsed by the parser too (a bad
value is a parse error). The source string stays; these fields are added:

| Op | Field | Meaning |
|---|---|---|
| `bars` | `bars` | `[[x, [y1, y2, …]], …]`: one entry per bar; several ys stack from the baseline (0, or the nearest end of the y range) |
| `hist` | `bars`, `bw` | bins as bars (`[[centre, [count]], …]`, or densities with `density`), and the bin width |
| `scatter` | `pts` | `[[x,y,z], …]` |
| `errbar` | `bars` | `[[x, y or null, lo, hi], …]`; with `horiz`, x is the row's y and the interval runs along x |
| `nn` | `sizes`, `shown` | neurons per layer, and per layer the 1-based neurons drawn (a big layer: its first and last few, with an ellipsis between) |
| `grid` | `cells` | rows×cols numbers, row-major from the top-left |
| `bits` | `widths` | bits per field (`pattern` split at `\|`), most significant first |

`band` has no data: its `x`/`y` ranges (either may be missing: the whole axis).

## Figures from code

`fig` carries its `lang` (`python` or `octave`), its `code` and a `key`: a hash
of the two (with `vcl.fig_version`). The rendered figure is
`figs/<key>.svg` (vector), `figs/<key>.png` (2× raster, for clients that draw
only part of SVG) and `figs/<key>.json`, relative to the server (`GET /figs/…`
waits while it renders) or to a published static copy (pre-rendered by `vcl
figs`). `size` is the width in board units (default 8); the height follows the
image. The JSON gives each axes' box in the figure (fractions from the bottom
left), its `x`/`y` limits and log flags. A figure whose code fails still gets
an image (the error), and the server sends a diagnostic for the line.

`data` has no IR: the parser runs its code (through the server, or results
computed ahead of time as `figs/<key>.data.json` for a static copy, where the
key hashes `data:`, the language and the code) and fills `{name.var}` in the
lines after it, so clients only see the resulting commands.

## Parts

`hl`, `color` and `fade` may target one part of a diagram instead of a whole
entity: `"ids":["n.L1_3"]`, `"target":"g.r2c3"`. The parser only allows the
parts listed below (they are the same names as the pins). A renderer recolours
or dims only that part; `hl` pulses its colour (and the WebXR viewer swells
neurons and bit boxes).

| Op | Parts (and pins) |
|---|---|
| `nn` | `L0`… whole layers (also `L0_top`, `L0_bottom` as pins above and below), `L1_3` neuron 3 of layer 1, `L1_3e` its error node (`pairs`), `E1`… the edges into layer 1, `F1`… feedback edges from layer 1 (`feedback`) |
| `grid` | `r2` row, `c3` column, `r2c3` cell (1-based; cells only up to 4096 of them) |
| `bits` | `b0`… single bits (b0 the least significant), `F1`… fields, and each field's label as a name (`sign`, `integer`) |
| `bars`, `hist` | `B1`… bars (pins at their tops) |

`scatter` has pins `P1`… at its points and `errbar` `E1`… at its intervals'
centres, but no parts.

## Pins

Parts, blocks, code, tables, timing diagrams and the diagrams above expose named pins. The parser
has already checked that a referenced pin exists; the renderer must provide
its position (and, for wires, its outward direction). Part pin positions are
defined in `viewer/js/parts.js`, whose names must match `part_pins` in
`core/src/vcl.zig` (a test checks this).

## Coordinates

Board units: x −8…8, y −4.5…4.5 by default, +z toward the viewer. The WebXR
viewer uses 1 unit = 0.15 m with the default board's centre 2.3 m ahead at
1.55 m height.

`{"op":"board","size":[W,H,D]}` resizes the board to W×H (x −W/2…W/2, y
−H/2…H/2) with a stage D deep in front of it (D = 0: none); without `size`,
back to [16,9,0]. Animate over `dur`. The WebXR viewer keeps the board's bottom
edge in place (it grows upwards) and the title at y = H/2 − 0.65. A `board` is
replayed after `clear`, so the server's replay log starts with the last one;
a `replay` resets the board to the default before running its commands. Entities
with `on` are children of that axes entity and their literal points are axes
data coordinates; on 3D axes, data (x, y, z) maps to board (x, z-up, −y).
On 2D axes with `xlog`/`ylog` true, that axis places log10(v) instead (the
range is positive; values ≤ 0 sit at its low end). `xticks`/`yticks` are either
booleans or a list of numbers to tick, label and grid at.

## Notes for a Unity / VRChat client

* `clients/unity` is one: it uses the WebSocket directly
  (`System.Net.WebSockets`) and mirrors `runtime.js` with an entity registry,
  the scheduler above and one builder per op. Unity's z axis points away from
  the viewer, so it flips IR z (and mirrors rotations) at the boundary.
* VRChat worlds can't open sockets. Udon can only fetch strings
  (`VRCStringDownloader`), about once every 5 seconds, from allow-listed hosts.
  So VRChat would poll `GET /scene` (plus a cursor parameter to add) rather
  than stream. Keep ops simple enough to interpret in UdonSharp; anything that
  would need an evaluator client-side is sampled server-side instead, as
  `plot`/`surf` are.
