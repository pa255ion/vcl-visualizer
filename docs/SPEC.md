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
| string | `"Hello \"world\""` | escapes: `\"` `\\` `\n` |
| colour | `#f80`  `#ff8800`  `blue` | named: `red orange yellow green teal cyan blue purple pink white gray black brown` |
| range | `-3..3` | |
| bool | `true false on off yes no 1 0` | |

Anchors: `center top bottom left right front back start end`, plus **pins**:
named connection points of parts (`R1.a`, `U1.y`), block ports (`cpu.clk`),
code lines (`c.L3`), table rows (`t.R2`) and timing-diagram signals (`w.clk`).
The parser knows every entity's pins and rejects unknown ones with a list of
the valid names.
`start`/`end` are the endpoints of lines, arrows and paths (for anything else
they mean `left`/`right`). Anchors are resolved by the renderer at the moment
the command runs, so they follow entities that have moved.

## 2. Space

All coordinates are in **board units**. The board is a virtual whiteboard in
front of the learner: `x` from −8 (left) to 8 (right), `y` from −4.5 (bottom)
to 4.5 (top), origin at the centre. `+z` comes out of the board toward the
learner, so 3D objects can float in front of it. One unit is about 15 cm.

`on=AXES` on any drawing op means "my coordinates are data coordinates of that
axes", and the entity becomes a child of the axes (it moves with them).

## 3. Time

Every command has a duration (`dur=`, seconds). The renderer keeps a timeline
cursor:

* A normal command starts when the previous command finishes (or now, if the
  stream has fallen behind real time), and moves the cursor to its own end.
* A command prefixed with `&` starts at the same time as the previous one.
* `wait N` moves the cursor N seconds forward.
* `say` lasts as long as it takes to speak (≈ 0.4 s per word) unless `dur=` is given.
  A renderer with real speech synthesis replaces that estimate with the audio's
  length (the WebXR viewer does).

So a lesson reads like a script: one beat after another, with `&` to bundle
things that should happen together.

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
| `clear` | | Fade out everything on the board. |
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
| `axes` | `P` | centre; `x=R` `y=R` `z=R` (z makes them 3D) `size=V` (extent in board units) `grid=bool` `ticks=bool` `xlabel=S` `ylabel=S` `zlabel=S` |
| `plot` | `ID S [S [S]]` | on axes ID: one string `y=f(x)`; two strings `x(t), y(t)`; three strings `x(t), y(t), z(t)`. `x=R` or `t=R` domain; `width=N` |
| `surf` | `ID S` | surface `z=f(x,y)` on 3D axes; `x=R` `y=R` `res=N` `fill=C` `wire=bool` |

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
| `block` | `P V [S]` | box of size `w,h` with a title; `in=` `out=` `top=` `bottom=` comma-separated port names become pins on that side. Warns when too small for its title and ports. |
| `frame` | `ID…` | dashed, titled boundary around entities that follows them; `title=S` `pad=N` `fill=C`. Frames can contain frames. |

### 4.7 Panels

| Op | Arguments | Keys |
|---|---|---|
| `code` | `P S` | syntax-highlighted source (`<<END` block); `lang=` (c cpp zig rust python js ts java go verilog vhdl asm sh…) `title=S` `lines=bool` `focus=R` (highlight lines, dim the rest; `set c focus=` moves it) `size=N` (line height). Pins `L1…` |
| `table` | `P S…` | one string per row, cells split by `\|`; `header=bool` (default true) `focus=R` (data rows) `size=N`. Pins `R1…` |
| `wave` | `P S…` | timing diagram, one row per signal: `"name: pattern [\| labels]"` with `0 1` levels, `z` high-Z, `x` unknown, `.` repeat, `=` (or `2`–`9`) a bus value named by the next label, `p`/`n` one clock period. `step=N` `size=N`. Pins: signal names. |
| `svg` | `P S` | URL or inline markup (`<<END`), rasterized sharply; `size=N` (width) |

`model` loads glTF/GLB, STL (CAD, Z-up) and OBJ by file extension, scaled to
`size` (largest dimension). With `vcl serve --assets DIR` your own files are
at `assets/…`.

### 4.8 Change things

| Op | Arguments | Keys / effect |
|---|---|---|
| `move` | `ID P` | animate position (centre) to P |
| `rot` | `ID V` | animate rotation to Euler degrees `x,y,z` |
| `scale` | `ID N` | animate uniform scale |
| `color` | `ID C` | animate colour |
| `fade` | `ID N` | animate opacity (0 hides but keeps) |
| `set` | `ID` | any of `pos=P rot=V scale=N color=C opacity=N text=S focus=R` |
| `hl` | `ID…` | brief highlight pulse; `color=C` |
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

Text outside ```` ```vcl ```` fences becomes `say` commands, one per line.

## 6. Errors

Each bad line produces one diagnostic and is skipped:

```
line 7: arrow: expected point for 'to', got "a.topp" (unknown anchor 'topp')
line 9: del: unknown name 'circel'
line 4: warning: rect: unknown key 'colour' (did you mean 'color'?)
```

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
