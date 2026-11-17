# VCL — Visual Communication Language, v0.1

VCL is a line-oriented, streamable command language for drawing and animating
explanatory visuals in a shared 3D space while a teacher (usually an LLM) talks.

Design goals, in priority order:

1. **An LLM can write it reliably, mid-conversation.** Few rules, no nesting,
   no arithmetic required, names instead of coordinates wherever possible.
2. **Streamable.** Every line is a complete command and runs the moment its
   newline arrives. Nothing ever waits for "the end of the program".
3. **Fail soft.** A bad line is skipped and reported; the rest of the lesson
   keeps playing. Errors are precise enough to feed back to the model.
4. **Renderer-agnostic.** Text compiles to a small JSON IR (see
   [IR.md](IR.md)). The WebXR viewer is one consumer; a Unity client is another.

## 1. Lexical structure

```
[&] [name:] op  positional...  key=value...   # comment
```

| Piece | Meaning |
|---|---|
| `&` | Run in **parallel** with the previous command instead of after it. |
| `name:` | Give the created entity a name so later lines can refer to it. Names are `[A-Za-z_][A-Za-z0-9_]*`. Reusing a name replaces the old entity. |
| `op` | The opcode (table below). |
| positional | Arguments in the order the op defines. |
| `key=value` | Optional named arguments, any order, after or between positionals. |
| `# ...` | Comment: `#` at the start of a line, or `#` followed by a space. (`#f80` is a colour.) |

Blank lines are ignored. A quoted string stays on one line (`\n` makes a
newline). For multi-line text, end the command with `<<TAG` (any name, usually
`END`): the following lines, up to a line containing only `TAG`, become the
command's last string argument, verbatim. For an op taking several strings
(`table`, `wave`) each non-empty line is one string. Inside a block nothing is
interpreted, not even ```` ``` ```` in mixed mode.

```
c: code 0,0 lang=c <<END
int main(void) {
    return 0;
}
END
```

### Value forms

| Form | Examples | Notes |
|---|---|---|
| number | `2`  `-0.5`  `1e-3` | |
| vector | `1,2`  `1,2,3` | no spaces; a missing z is 0 |
| point | `1,2` or `a` or `a.top` or `a.top+0,0.4` | a vector, or a reference to an entity, optionally an anchor and a `+offset` |
| string | `"Hello \"world\""` | escapes: `\"` `\\` `\n`; a closing quote must be followed by a space or the end of the line (`"a": "b"` is an error). UTF-8 is fine on the board (`"τ ≈ 4.7 µs"`); in speech (`say`, prose) write symbols as words, because the robot voice skips them |
| colour | `#f80`  `#ff8800`  `blue` | named: `red orange yellow green teal cyan blue purple pink white gray black brown` |
| range | `-3..3` | |
| bool | `true false on off yes no 1 0` | |

Anchors: `center top bottom left right front back start end`, plus **pins**:
named connection points of parts (`R1.a`, `U1.y`), block ports (`cpu.clk`),
code lines (`c.L3`), table rows (`t.R2`), timing-diagram signals (`w.clk`),
and the parts of `nn`, `grid`, `bits` and chart ops (§4.4, §4.8).
The parser knows every entity's pins and rejects unknown ones with a list of
the valid names.

| Pin | Where it is | Faces |
|---|---|---|
| part pin | the end of the pin's lead | outward along the lead |
| block port | on the box edge, at the port's tick | out of that side |
| code line `L3`, table row `R2` | right edge of the panel, level with the line/row | right |
| wave signal | right end of the signal's trace | right |

So `arrow note.left c.L3` points at line 3 from the right. A wave signal's pin
is its name with a trailing bus range dropped and other non-identifier
characters turned into `_`: `"clk"` → `w.clk`, `"d[7:0]"` → `w.d`,
`"FIFO write"` → `w.FIFO_write`, `"3v3"` → `w.s3v3`.
`start`/`end` are the endpoints of lines, arrows and paths (for anything else
they mean `left`/`right`). Anchors are resolved by the renderer at the moment
the command runs, so they follow entities that have moved.

## 2. Space

All coordinates are in **board units**. The board is a virtual whiteboard in
front of the learner: `x` from −8 (left) to 8 (right), `y` from −4.5 (bottom)
to 4.5 (top), origin at the centre. `+z` comes out of the board toward the
learner, so 3D objects can float in front of it. One unit is about 15 cm.

`board W,H[,D]` resizes the board to W×H units (x −W/2…W/2, y −H/2…H/2; width
8–40, height 4.5–24). It grows sideways and upwards: the bottom edge stays put.
D > 0 (up to 24) adds a **stage**: a floor D units deep at the board's foot,
for 3D content that comes toward the learner (z from 0 to D, standing on
y = −H/2). Bare `board` goes back to 16,9 with no stage. `clear` keeps the
board's size; the off-board lint follows it. The desktop viewer moves its
camera so the whole board stays in view.

`on=AXES` on any drawing op means "my coordinates are data coordinates of that
axes", and the entity becomes a child of the axes (it moves with them).

## 3. Time

Every command has a duration (`dur=`, seconds). The renderer keeps a timeline
cursor:

* A normal command starts when the previous command finishes (or now, if the
  stream has fallen behind real time), and moves the cursor to its own end.
* A command prefixed with `&` starts at the same time as the previous one.
* Lines between `par` and `end` (each alone on its line) all start together,
  as if every command after the first had `&`. The command after `end` waits
  for the longest of them. Blocks don't nest; a block left open closes at the
  end of its ```` ```vcl ```` fence (with a warning).
* `wait N` moves the cursor N seconds forward.
* `say` lasts as long as it takes to speak (≈ 0.4 s per word) unless `dur=` is given.
  A renderer with real speech synthesis replaces that estimate with the audio's
  length (the WebXR viewer does).

So a lesson reads like a script: one beat after another, with `&` or
`par` … `end` to bundle things that should happen together.

```
par
c1: plot L1 "sin(3t)" "sin(2t)" t=0..6.2832 dur=4
c2: plot L2 "sin(5t)" "sin(4t)" t=0..6.2832 dur=4
text 0,-2 "two at once"
end
say "Both curves were drawn together."
```

Viewers may offer step-by-step playback: each `say` after the first of a burst
waits for the learner (Space, a button or the VR trigger), so a sentence and the
drawing that goes with it play as one step.

`ease=` controls animation curves: `linear in out inout` (default `inout`).

## 4. Opcodes

`P` point · `N` number · `V` vector · `S` string · `C` colour · `R` range ·
`ID` existing name · `…` repeatable.

### 4.1 Common keys

Every drawing op accepts: `color=C` `opacity=N` `fx=draw|fade|grow|pop|none`
(entrance effect) `label=S` `on=ID` `dur=N` `ease=`. Every other op accepts
`dur=` and `ease=`. Unknown keys produce a warning and are ignored.

### 4.2 Flow and talk

| Op | Arguments | Effect |
|---|---|---|
| `wait` | `N` | Pause the timeline. |
| `say` | `S` | The teacher says this (subtitle + speech bubble; TTS later). |
| `point` | `[P]` | The teacher looks and points at a point/entity; no argument stops pointing. |
| `title` | `S` | Heading across the top of the board. Replaces the previous title. |
| `clear` | | Fade out everything on the board (its size stays). |
| `board` | `[V]` | Resize the board to W,H[,D] units (§2); bare `board` restores 16,9. |
| `par` … `end` | | Not ops but block lines: everything between starts together (§3). |
| `del` | `ID…` | Remove entities. |

### 4.3 2D drawing

| Op | Arguments | Keys |
|---|---|---|
| `pt` | `P` | `size=N` |
| `line` | `P P` | `width=N` `dash=bool` |
| `arrow` | `P P` | `width=N` `dash=bool` `head=end|both` |
| `path` | `P P…` | `width=N` `closed=bool` `smooth=bool` `dash=bool` |
| `poly` | `P P P…` | `fill=C` `width=N` (filled polygon) |
| `rect` | `P V` | centre, size `w,h`; `fill=C` `width=N` `radius=N` |
| `circle` | `P N` | centre, radius; `fill=C` `width=N` |
| `angle` | `P P P` | marks the angle at the middle point between the other two; `radius=N` `right=bool` |
| `text` | `P S` | `size=N` (cap height, default 0.5) `align=left|center|right` |
| `tex` | `P S` | LaTeX; `size=N` |
| `node` | `P S` | labelled shape for diagrams; `shape=circle|box|pill|none` `fill=C` `size=N` |
| `edge` | `ID ID` | connects two nodes border-to-border; `dir=fwd|back|both|none` `bend=N` `width=N` `dash=bool` |
| `img` | `P S` | image URL; `size=N` (width) |

### 4.4 Graphs

| Op | Arguments | Keys |
|---|---|---|
| `axes` | `P` | centre; `x=R` `y=R` `z=R` (z makes them 3D) `size=V` (extent in board units) `grid=bool` `ticks=bool` `xticks=bool` `yticks=bool` `xlabel=S` `ylabel=S` `zlabel=S` |
| `plot` | `ID S [S [S]]` | on axes ID: one string `y=f(x)`; two strings `x(t), y(t)`; three strings `x(t), y(t), z(t)`. `x=R` or `t=R` domain; `width=N` `dash=bool` (e.g. a fit or estimate next to data) `step=bool` (a staircase) |
| `surf` | `ID S` | surface `z=f(x,y)` on 3D axes; `x=R` `y=R` `res=N` `fill=C` `wire=bool` |

| `contour` | `ID S` | contour lines of `z=f(x,y)` on 2D axes; `levels=N` (default 8, evenly between the lowest and highest value) `res=N` (grid, default 64) `x=R` `y=R` `width=N`; coloured low→high unless `color=` |
| `bars` | `ID S` | bar chart: `"y y y"` (at x = 1, 2, 3…) or `"x,y x,y"`; `x,y1/y2/y3` stacks; `width=N` (bar width in data units, default 0.6 of the spacing) `labels="a,b,c"` (category names under the bars; give the axes `xticks=false`) `series="p,q,r"` (legend for stacks). Parts/pins `B1…` |
| `hist` | `ID S` | histogram of the values in the string; `bins=N` (default Sturges) `range=R` (default min..max) `density=bool`. Parts/pins `B1…` |
| `scatter` | `ID S` | points `"x,y x,y …"` (`x,y,z` on 3D axes); `size=N` (dot radius, default 0.09). Pins `P1…` |
| `errbar` | `ID S` | intervals `"x,lo..hi"` or with an estimate `"x,y,lo..hi"`; `horiz=true` makes them horizontal (the first number is then the row's y, the estimate an x: a forest plot); `cap=N` `size=N` (dot) `width=N`. Pins `E1…` |
| `band` | `ID` | shaded band with dashed edges: `y=lo..hi` (horizontal), `x=lo..hi` (vertical) or both (a box); `fill=C`; `label` sits inside its top-left corner. For margins (equivalence bounds) and regions |

`plot … step=true` draws a staircase: flat runs and vertical risers at the
jumps, located exactly (`plot ax "round(4x)/4" step=true` for a quantiser).
`axes … xticks=false` (or `yticks=false`) hides the numbers along one axis only.

Data strings may be given as a `<<END` block instead (one or more items per
line). The parser checks them: wrong counts and bad numbers are errors.

Expressions use `x y t`, `+ - * / ^`, implicit multiplication (`2x`, `3(x+1)`),
`pi e`, and `sin cos tan asin acos atan atan2 sinh cosh tanh sqrt abs exp ln
log log2 floor ceil round min max pow sign mod`.

### 4.5 3D

| Op | Arguments | Keys |
|---|---|---|
| `box` | `P V` | centre, size `w,h,d` |
| `sphere` | `P N` | centre, radius |
| `cyl` | `P P N` | from, to, radius (rods, bonds, columns) |
| `model` | `P S` | glTF/GLB URL; `size=N` |

3D ops also take `wire=bool`.

### 4.6 Engineering diagrams

| Op | Arguments | Keys / pins |
|---|---|---|
| `part` | `P KIND` | `rot=N` (degrees, counter-clockwise) `flip=bool` `value=S` `size=N`. Kinds and pins: `resistor capacitor inductor vsource isource battery switch` (a, b) · `diode led` (a, k) · `ground vdd` (a) · `npn pnp` (b, c, e) · `nmos pmos` (g, d, s) · `opamp` (inp, inn, out) · `and or nand nor xor xnor` (a, b, y) · `not buf` (a, y) · `dff` (d, clk, q, qn) · `mux2` (a, b, s, y). Two-pin parts lie along x with `a` on the left. |
| `wire` | `P P…` | right-angle route through the points; pins are left along their direction. `route=auto\|hv\|vh\|direct` `dot=none\|start\|end\|both` (junction dots) `arrow=bool` `bus=N` (slash + width) `width` `dash` |
| `block` | `P V [S]` | box of size `w,h` with a title; `in=` `out=` `top=` `bottom=` comma-separated port names become pins on that side. `flip=true` puts inputs on the right and outputs on the left, for rows that flow right to left (the return path of a U-shaped pipeline). Warns when too small for its title and ports. |
| `frame` | `ID…` | dashed, titled boundary around entities that follows them; `title=S` `pad=N` `fill=C`. Frames can contain frames. |

### 4.7 Panels

| Op | Arguments | Keys |
|---|---|---|
| `code` | `P S` | syntax-highlighted source (`<<END` block); `lang=` (c cpp zig rust python js ts java go verilog vhdl asm sh…) `title=S` `lines=bool` `focus=R` (highlight lines, dim the rest; `set c focus=` moves it) `size=N` (line height, default 0.48). Pins `L1…` |
| `table` | `P S…` | one string per row, cells split by `\|`; `header=bool` (default true) `focus=R` (data rows) `size=N` (row height, default 0.65). Pins `R1…` |
| `wave` | `P S…` | timing diagram, one row per signal: `"name: pattern [\| labels]"` with `0 1` levels, `z` high-Z, `x` unknown, `.` repeat, `=` (or `2`–`9`) a bus value named by the next label, `p`/`n` one clock period. `step=N` `size=N`. Pins: signal names (see §1). |
| `svg` | `P S` | URL or inline markup (`<<END`), rasterized sharply; `size=N` (width) |

`model` loads glTF/GLB, STL (CAD, Z-up) and OBJ by file extension, scaled to
`size` (largest dimension). With `vcl serve --assets DIR` your own files are
at `assets/…`.

### 4.8 Diagrams with parts

| Op | Arguments | Keys / parts |
|---|---|---|
| `nn` | `P S` | neural network: layer sizes `"4,6,6,3"`, layers left to right, neighbouring layers fully connected. `size=w,h` (default 2.6 per layer gap × 4) `labels="x,h,y"` (under the layers) `edges=dense\|none` `max=N` (layers bigger than N, default 8, show their first 3 and last 2 neurons around ⋮, with the size in the label) `pairs=true` (each neuron gets a small error node beside it, for predictive coding) `feedback=true` (dashed edges back from each layer to the previous one's error nodes, or neurons). Parts: `n.L1` layer 1 (`L0` is the input), `n.L1_3` its neuron 3 (1-based; `L0_196` in a collapsed layer of 196), `n.L1_3e` that neuron's error node, `n.E1` the edges into layer 1, `n.F1` the feedback edges out of layer 1. Pins: those, plus `L1_top`/`L1_bottom` above and below a layer |
| `grid` | `P V S` | heatmap: `rows,cols` then rows×cols numbers (spaces, commas or new lines; `<<END` with one row per line reads well). `size=w,h` (default: cells up to 0.45, within 8×6) `cmap=gray\|div\|heat` (default `gray`, or `div` (blue-white-red around 0) when there are negatives) `range=R` (colour limits) `text=true` (print the numbers). Parts: `g.r2` row, `g.c3` column, `g.r2c3` cell (1-based). Pins: `r2` at a row's left end, `c3` at a column's top, `r2c3` at a cell's centre. `set g text="…"` gives it new values (same count) |
| `bits` | `P S` | bit fields: one character per bit, fields split by `\|` (`"s\|iiii\|fffffff"` is Q4.7 with a sign bit). `values="0\|0011\|0100000"` (0, 1 or x per bit; without it the pattern's letters show, dimmed) `labels="sign,integer,fraction"` (one per field, under braces) `size=N` (box width, default 0.55, within 14 overall) `index=bool` (bit numbers above, default true). Parts: `b.b0`… single bits (b0 is the least significant, on the right), `b.F1`… fields, and each label as a name (`b.fraction`). `set b text="1\|0011\|…"` changes the values |

`hl`, `color` and `fade` take parts as well as names: `hl n.L1_3`, `color n.E2
red`, `fade n.L0 0.3`, `color g.r2c3 yellow`, `hl b.sign`, `color bars1.B2
pink`. The parser lists the valid parts when one is misspelled.

### 4.9 Change things

| Op | Arguments | Keys / effect |
|---|---|---|
| `move` | `ID P` | animate position (centre) to P |
| `rot` | `ID V` | animate rotation to Euler degrees `x,y,z` |
| `scale` | `ID N` | animate uniform scale |
| `color` | `ID C` | animate colour (also of a part, §4.8) |
| `fade` | `ID N` | animate opacity (0 hides but keeps; also of a part) |
| `set` | `ID` | any of `pos=P rot=V scale=N color=C opacity=N text=S focus=R` |
| `hl` | `ID…` | brief highlight pulse; `color=C`; also parts (`hl n.L1_3`) |
| `spin` | `ID V` | continuous rotation, degrees/second per axis; `0,0,0` stops |
| `group` | `ID…` | needs a name; bundles entities so they move/rotate together |

## 5. Mixed mode (prose + visuals)

An LLM's normal reply can be streamed straight in when the server is told
`mode=mixed`:

````
Let's look at the slope of a line.
```vcl
ax: axes 0,0 x=-4..4 y=-3..3
plot ax "0.5x + 1" color=blue
```
The slope is how much y changes per step in x.
````

Text outside ```` ```vcl ```` fences becomes `say` commands, one per
sentence (a line is split after `.` `?` `!` followed by a space); light
markdown (`*` `_` `` ` `` and leading `#` `>` `-`) is stripped. Fences in
other languages are skipped.

Sending in the wrong mode is caught: a ```` ``` ```` line in plain VCL switches
that stream to mixed mode with a warning (prose lines before it fail with a
hint to use `mode=mixed`), and in mixed mode the first prose line that looks
like a VCL command gets a warning that it was spoken rather than drawn. The
viewer's Editor and `tools/stream.mjs` choose mixed mode by themselves when the
text contains a ```` ```vcl ```` line.

## 6. Errors

Each bad line produces one diagnostic and is skipped:

```
line 7: arrow: expected point for 'to', got "a.topp" (unknown anchor 'topp')
line 9: del: unknown name 'circel'
line 4: warning: rect: unknown key 'colour' (did you mean 'color'?)
```

Warnings are also given for layout the parser can predict: a `block` too
small for its title and ports, and `text`, `code`, `table`, `block`, `wave`,
`nn`, `grid` or `bits` placed at board coordinates that would run off the board
(sizes are estimated from character counts, so treat a near miss as a hint),
and any of those (or a
`node`) that lands on another one already on the board. Anything with `on=` is
in data coordinates and not checked, nor are labels, lines, shapes and `tex`;
an entity that was moved, scaled, faded or grouped is no longer compared.

Diagnostics go back to whoever sent the text, so an LLM harness can return
them to the model as tool output.

## 7. Example

```
title "Pythagoras"
A: pt -3,-2 label="A"
& B: pt 3,-2 label="B"
& C: pt 3,2 label="C"
ab: line A B
bc: line B C
ca: line C A color=yellow
& angle A B C right=true
say "Here's a right triangle."
hl ca
say "The long side, opposite the right angle, is the hypotenuse."
tex 0,-3.6 "a^2 + b^2 = c^2" size=0.7
```
