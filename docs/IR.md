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
| `par` | start together with the previous command |
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

## Pins

Parts, blocks, code, tables and timing diagrams expose named pins. The parser
has already checked that a referenced pin exists; the renderer must provide
its position (and, for wires, its outward direction). Part pin positions are
defined in `viewer/js/parts.js`, whose names must match `part_pins` in
`core/src/vcl.zig` (a test checks this).

## Coordinates

Board units: x −8…8, y −4.5…4.5, +z toward the viewer. The WebXR viewer uses
1 unit = 0.15 m with the board centre 2.3 m ahead at 1.55 m height. Entities
with `on` are children of that axes entity and their literal points are axes
data coordinates; on 3D axes, data (x, y, z) maps to board (x, z-up, −y).

## Notes for a Unity / VRChat client

* A Unity client can use the WebSocket directly (e.g. NativeWebSocket) and
  mirror `runtime.js`: an entity registry, the scheduler above, and one
  builder per op.
* VRChat worlds can't open sockets. Udon can only fetch strings
  (`VRCStringDownloader`), about once every 5 seconds, from allow-listed hosts.
  So VRChat would poll `GET /scene` (plus a cursor parameter to add) rather
  than stream. Keep ops simple enough to interpret in UdonSharp; avoid adding
  anything that needs a full expression evaluator *client-side* only (sampling
  `plot`/`surf` server-side into point arrays would be the VRChat path).
