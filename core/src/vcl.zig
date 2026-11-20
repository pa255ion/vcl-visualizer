//! VCL: parser, validator and IR (JSON) emitter.
//!
//! Pure library: no I/O, no globals. Used natively by the stream server and
//! compiled to wasm32 for the browser viewer.
//!
//! Two pieces of state:
//!   * `Scene`  — names that currently exist (shared by every producer).
//!   * `Stream` — per-producer line buffering and prose/fence mode.
//!
//! Output is newline-delimited JSON written to a `std.Io.Writer`; one object
//! per command or diagnostic. See docs/IR.md.

const std = @import("std");
const Allocator = std.mem.Allocator;
const Writer = std.Io.Writer;
const expr = @import("expr.zig");

test {
    _ = expr;
}

pub const version = "0.1";

/// Bumped whenever figure rendering changes (the style sheet, the output), so
/// cached figures from before are not reused: it seeds every figure's key.
pub const fig_version: u64 = 1;

/// A `fig`'s key: a hash of its language and code. The rendered figure is
/// `figs/<key>.svg` (and `.png` for clients that can't draw every SVG), both
/// from `vcl serve` and in a published static copy.
pub fn figKey(lang: []const u8, code: []const u8) [16]u8 {
    return codeKey("", lang, code);
}

/// A `data` command's key (its results are `figs/<key>.data.json`).
pub fn dataKey(lang: []const u8, code: []const u8) [16]u8 {
    return codeKey("data:", lang, code);
}

fn codeKey(kind: []const u8, lang: []const u8, code: []const u8) [16]u8 {
    var h = std.hash.Wyhash.init(fig_version);
    h.update(kind);
    h.update(lang);
    h.update("\x00");
    h.update(code);
    var out: [16]u8 = undefined;
    _ = std.fmt.bufPrint(&out, "{x:0>16}", .{h.final()}) catch unreachable;
    return out;
}

// ───────────────────────────── schema ─────────────────────────────

pub const Ty = enum { num, vec, point, str, color, range, boolean, id, word, nums };

pub const Param = struct {
    name: []const u8,
    ty: Ty,
    /// Positional only: may be omitted.
    opt: bool = false,
    /// Positional only: consumes the remaining positionals (min..max).
    many: bool = false,
    min: u8 = 1,
    max: u8 = 255,
    /// For `.word`: the accepted values (empty: any plain word, e.g. a language name).
    words: []const []const u8 = &.{},
    /// For `.id`: the op that created the referenced entity must be this.
    of_op: ?[]const u8 = null,
    /// For `.str`: keep backslashes (LaTeX); only \" is unescaped.
    raw: bool = false,
    /// For `.id`: `name.pin` is accepted for diagrams with parts (`part_ops`).
    part: bool = false,
    /// For `.id` with `part`: any pin will do (`t.R2`, `c.L3`, `R1.a`), not only parts.
    any_pin: bool = false,
    /// For `.boolean`: a quoted list of numbers is accepted too (`xticks="1,2,4"`).
    nums: bool = false,
};

/// Ops whose pins are also parts that `hl`, `color` and `fade` can target.
pub const part_ops = [_][]const u8{ "nn", "grid", "bits", "bars", "hist" };

pub const Kind = enum { draw, change, flow };

pub const Op = struct {
    name: []const u8,
    kind: Kind,
    pos: []const Param = &.{},
    keys: []const Param = &.{},
    dur: f64,
    needs_name: bool = false,
    /// Kept in the replay log (late joiners rebuild the scene from it).
    replay: bool = true,
    /// A question for the learner (§4.10): may be named, waits for an answer.
    question: bool = false,
};

fn P(name: []const u8, ty: Ty) Param {
    return .{ .name = name, .ty = ty };
}
fn W(name: []const u8, words: []const []const u8) Param {
    return .{ .name = name, .ty = .word, .words = words };
}

const ease_words = &[_][]const u8{ "linear", "in", "out", "inout" };
const fx_words = &[_][]const u8{ "draw", "fade", "grow", "pop", "none" };
const timing_keys = [_]Param{ P("dur", .num), W("ease", ease_words) };
const draw_keys = [_]Param{
    P("color", .color), P("opacity", .num), W("fx", fx_words), P("label", .str),
    .{ .name = "on", .ty = .id, .of_op = "axes" },
};

const width = P("width", .num);
const dash = P("dash", .boolean);
const fill = P("fill", .color);
const size = P("size", .num);
const wire = P("wire", .boolean);
const on_axes: Param = .{ .name = "axes", .ty = .id, .of_op = "axes" };
/// A change target that may also be one part of a diagram (`n.L1_3`, `g.r2c3`).
const part_target: Param = .{ .name = "target", .ty = .id, .part = true };

/// Schematic symbols and their pins (the renderer draws them; see viewer/js/parts.js).
pub const part_pins = [_]struct { []const u8, []const u8 }{
    .{ "resistor", "a,b" },  .{ "capacitor", "a,b" }, .{ "inductor", "a,b" },   .{ "vsource", "a,b" },
    .{ "isource", "a,b" },   .{ "battery", "a,b" },   .{ "switch", "a,b" },     .{ "diode", "a,k" },
    .{ "led", "a,k" },       .{ "ground", "a" },      .{ "vdd", "a" },          .{ "npn", "b,c,e" },
    .{ "pnp", "b,c,e" },     .{ "nmos", "g,d,s" },    .{ "pmos", "g,d,s" },     .{ "opamp", "inp,inn,out" },
    .{ "and", "a,b,y" },     .{ "or", "a,b,y" },      .{ "nand", "a,b,y" },     .{ "nor", "a,b,y" },
    .{ "xor", "a,b,y" },     .{ "xnor", "a,b,y" },    .{ "not", "a,y" },        .{ "buf", "a,y" },
    .{ "dff", "d,clk,q,qn" }, .{ "mux2", "a,b,s,y" },
};
const part_kind_names = blk: {
    var n: [part_pins.len][]const u8 = undefined;
    for (part_pins, 0..) |pp, i| n[i] = pp[0];
    break :blk n;
};

pub const ops = [_]Op{
    // flow & talk
    .{ .name = "wait", .kind = .flow, .pos = &.{P("secs", .num)}, .dur = 0, .replay = false },
    .{ .name = "say", .kind = .flow, .pos = &.{P("text", .str)}, .dur = 0, .replay = false },
    .{ .name = "point", .kind = .flow, .pos = &.{.{ .name = "at", .ty = .point, .opt = true }}, .dur = 0.4, .replay = false },
    .{ .name = "title", .kind = .flow, .pos = &.{P("text", .str)}, .dur = 0.5 },
    .{ .name = "clear", .kind = .flow, .dur = 0.5 },
    .{ .name = "board", .kind = .flow, .pos = &.{.{ .name = "size", .ty = .vec, .opt = true }}, .dur = 0.8 },
    .{ .name = "del", .kind = .flow, .pos = &.{.{ .name = "ids", .ty = .id, .many = true }}, .dur = 0.3 },
    // questions for the learner: playback waits for the answer
    .{ .name = "quiz", .kind = .flow, .pos = &.{ P("q", .str), .{ .name = "options", .ty = .str, .many = true, .min = 2, .max = 8 } }, .keys = &.{ P("answer", .nums), P("explain", .str) }, .dur = 0, .replay = false, .question = true },
    .{ .name = "pick", .kind = .flow, .pos = &.{ P("q", .str), .{ .name = "ids", .ty = .id, .many = true, .part = true, .any_pin = true, .min = 2, .max = 12 } }, .keys = &.{ P("answer", .nums), P("explain", .str) }, .dur = 0, .replay = false, .question = true },
    .{ .name = "estimate", .kind = .flow, .pos = &.{ P("q", .str), P("range", .range) }, .keys = &.{ P("answer", .num), P("tol", .num), P("step", .num), P("unit", .str), P("explain", .str) }, .dur = 0, .replay = false, .question = true },
    .{ .name = "recall", .kind = .flow, .pos = &.{ P("q", .str), P("answer", .str) }, .keys = &.{P("explain", .str)}, .dur = 0, .replay = false, .question = true },
    // 2D
    .{ .name = "pt", .kind = .draw, .pos = &.{P("at", .point)}, .keys = &.{size}, .dur = 0.3 },
    .{ .name = "line", .kind = .draw, .pos = &.{ P("from", .point), P("to", .point) }, .keys = &.{ width, dash }, .dur = 0.6 },
    .{ .name = "arrow", .kind = .draw, .pos = &.{ P("from", .point), P("to", .point) }, .keys = &.{ width, dash, W("head", &.{ "end", "both" }) }, .dur = 0.6 },
    .{ .name = "path", .kind = .draw, .pos = &.{.{ .name = "pts", .ty = .point, .many = true, .min = 2 }}, .keys = &.{ width, dash, P("closed", .boolean), P("smooth", .boolean) }, .dur = 0.9 },
    .{ .name = "poly", .kind = .draw, .pos = &.{.{ .name = "pts", .ty = .point, .many = true, .min = 3 }}, .keys = &.{ fill, width }, .dur = 0.6 },
    .{ .name = "rect", .kind = .draw, .pos = &.{ P("at", .point), P("size", .vec) }, .keys = &.{ fill, width, P("radius", .num) }, .dur = 0.6 },
    .{ .name = "circle", .kind = .draw, .pos = &.{ P("at", .point), P("r", .num) }, .keys = &.{ fill, width }, .dur = 0.6 },
    .{ .name = "angle", .kind = .draw, .pos = &.{ P("a", .point), P("vertex", .point), P("b", .point) }, .keys = &.{ P("radius", .num), P("right", .boolean), width }, .dur = 0.5 },
    .{ .name = "text", .kind = .draw, .pos = &.{ P("at", .point), P("text", .str) }, .keys = &.{ size, W("align", &.{ "left", "center", "right" }) }, .dur = 0.5 },
    .{ .name = "tex", .kind = .draw, .pos = &.{ P("at", .point), .{ .name = "text", .ty = .str, .raw = true } }, .keys = &.{size}, .dur = 0.6 },
    .{ .name = "node", .kind = .draw, .pos = &.{ P("at", .point), P("text", .str) }, .keys = &.{ W("shape", &.{ "circle", "box", "pill", "none" }), fill, size }, .dur = 0.5 },
    .{ .name = "edge", .kind = .draw, .pos = &.{ P("from", .id), P("to", .id) }, .keys = &.{ W("dir", &.{ "fwd", "back", "both", "none" }), P("bend", .num), width, dash }, .dur = 0.5 },
    .{ .name = "img", .kind = .draw, .pos = &.{ P("at", .point), P("url", .str) }, .keys = &.{size}, .dur = 0.6 },
    .{ .name = "svg", .kind = .draw, .pos = &.{ P("at", .point), P("src", .str) }, .keys = &.{size}, .dur = 0.6 },
    // a figure drawn by code (matplotlib, Octave) on the server: see `figKey`
    .{ .name = "data", .kind = .flow, .pos = &.{ W("lang", &.{ "python", "octave" }), P("code", .str) }, .dur = 0, .replay = false, .needs_name = true },
    .{ .name = "fig", .kind = .draw, .pos = &.{ P("at", .point), W("lang", &.{ "python", "octave" }), P("code", .str) }, .keys = &.{size}, .dur = 0.8 },
    // engineering diagrams
    .{ .name = "part", .kind = .draw, .pos = &.{ P("at", .point), W("kind", &part_kind_names) }, .keys = &.{ P("rot", .num), P("flip", .boolean), P("value", .str), size }, .dur = 0.5 },
    .{ .name = "wire", .kind = .draw, .pos = &.{.{ .name = "pts", .ty = .point, .many = true, .min = 2 }}, .keys = &.{
        W("route", &.{ "auto", "hv", "vh", "direct" }), W("dot", &.{ "none", "start", "end", "both" }),
        P("arrow", .boolean), P("bus", .num), width, dash,
    }, .dur = 0.5 },
    .{ .name = "block", .kind = .draw, .pos = &.{ P("at", .point), P("size", .vec), .{ .name = "text", .ty = .str, .opt = true } }, .keys = &.{
        P("in", .str), P("out", .str), P("top", .str), P("bottom", .str), fill, P("flip", .boolean),
    }, .dur = 0.6 },
    .{ .name = "frame", .kind = .draw, .pos = &.{.{ .name = "ids", .ty = .id, .many = true }}, .keys = &.{ P("title", .str), P("pad", .num), dash, fill }, .dur = 0.5 },
    .{ .name = "code", .kind = .draw, .pos = &.{ P("at", .point), P("text", .str) }, .keys = &.{
        .{ .name = "lang", .ty = .word }, size, P("lines", .boolean), P("focus", .range), P("title", .str),
    }, .dur = 0.6 },
    .{ .name = "table", .kind = .draw, .pos = &.{ P("at", .point), .{ .name = "rows", .ty = .str, .many = true } }, .keys = &.{ size, P("header", .boolean), P("focus", .range) }, .dur = 0.6 },
    .{ .name = "wave", .kind = .draw, .pos = &.{ P("at", .point), .{ .name = "rows", .ty = .str, .many = true } }, .keys = &.{ P("step", .num), size }, .dur = 1.0 },
    // graphs
    .{ .name = "axes", .kind = .draw, .pos = &.{P("at", .point)}, .keys = &.{
        P("x", .range),      P("y", .range),      P("z", .range),       P("size", .vec),
        P("grid", .boolean), P("ticks", .boolean), .{ .name = "xticks", .ty = .boolean, .nums = true },
        .{ .name = "yticks", .ty = .boolean, .nums = true }, P("xlabel", .str), P("ylabel", .str), P("zlabel", .str),
        P("xlog", .boolean), P("ylog", .boolean),
    }, .dur = 1.0 },
    .{ .name = "plot", .kind = .draw, .pos = &.{ on_axes, .{ .name = "exprs", .ty = .str, .many = true, .max = 3 } }, .keys = &.{ P("x", .range), P("t", .range), width, dash, P("step", .boolean) }, .dur = 1.2 },
    .{ .name = "surf", .kind = .draw, .pos = &.{ on_axes, P("expr", .str) }, .keys = &.{ P("x", .range), P("y", .range), P("res", .num), fill, wire }, .dur = 1.2 },
    .{ .name = "contour", .kind = .draw, .pos = &.{ on_axes, P("expr", .str) }, .keys = &.{ P("x", .range), P("y", .range), P("res", .num), P("levels", .num), width }, .dur = 1.2 },
    .{ .name = "bars", .kind = .draw, .pos = &.{ on_axes, P("data", .str) }, .keys = &.{ width, P("labels", .str), P("series", .str) }, .dur = 0.8 },
    .{ .name = "hist", .kind = .draw, .pos = &.{ on_axes, P("data", .str) }, .keys = &.{ P("bins", .num), P("range", .range), P("density", .boolean) }, .dur = 0.8 },
    .{ .name = "scatter", .kind = .draw, .pos = &.{ on_axes, P("data", .str) }, .keys = &.{size}, .dur = 0.6 },
    .{ .name = "errbar", .kind = .draw, .pos = &.{ on_axes, P("data", .str) }, .keys = &.{ P("horiz", .boolean), P("cap", .num), width, size }, .dur = 0.6 },
    .{ .name = "band", .kind = .draw, .pos = &.{on_axes}, .keys = &.{ P("x", .range), P("y", .range), fill }, .dur = 0.5 },
    // structured diagrams
    .{ .name = "nn", .kind = .draw, .pos = &.{ P("at", .point), P("layers", .str) }, .keys = &.{
        P("size", .vec), P("labels", .str), W("edges", &.{ "dense", "none" }), P("pairs", .boolean), P("feedback", .boolean), P("max", .num),
    }, .dur = 1.2 },
    .{ .name = "grid", .kind = .draw, .pos = &.{ P("at", .point), P("dims", .vec), P("values", .str) }, .keys = &.{
        P("size", .vec), W("cmap", &.{ "gray", "div", "heat" }), P("text", .boolean), P("range", .range),
    }, .dur = 0.8 },
    .{ .name = "bits", .kind = .draw, .pos = &.{ P("at", .point), P("pattern", .str) }, .keys = &.{ P("labels", .str), P("values", .str), size, P("index", .boolean) }, .dur = 0.8 },
    // 3D
    .{ .name = "box", .kind = .draw, .pos = &.{ P("at", .point), P("size", .vec) }, .keys = &.{wire}, .dur = 0.6 },
    .{ .name = "sphere", .kind = .draw, .pos = &.{ P("at", .point), P("r", .num) }, .keys = &.{wire}, .dur = 0.6 },
    .{ .name = "cyl", .kind = .draw, .pos = &.{ P("from", .point), P("to", .point), P("r", .num) }, .keys = &.{wire}, .dur = 0.6 },
    .{ .name = "model", .kind = .draw, .pos = &.{ P("at", .point), P("url", .str) }, .keys = &.{size}, .dur = 0.8 },
    // change
    .{ .name = "move", .kind = .change, .pos = &.{ P("target", .id), P("to", .point) }, .dur = 0.6 },
    .{ .name = "rot", .kind = .change, .pos = &.{ P("target", .id), P("rot", .vec) }, .dur = 0.6 },
    .{ .name = "scale", .kind = .change, .pos = &.{ P("target", .id), P("scale", .num) }, .dur = 0.5 },
    .{ .name = "color", .kind = .change, .pos = &.{ part_target, P("color", .color) }, .dur = 0.4 },
    .{ .name = "fade", .kind = .change, .pos = &.{ part_target, P("opacity", .num) }, .dur = 0.4 },
    .{ .name = "set", .kind = .change, .pos = &.{P("target", .id)}, .keys = &.{
        P("pos", .point), P("rot", .vec), P("scale", .num), P("color", .color), P("opacity", .num), P("text", .str),
        P("focus", .range),
    }, .dur = 0.5 },
    .{ .name = "hl", .kind = .change, .pos = &.{.{ .name = "ids", .ty = .id, .many = true, .part = true }}, .keys = &.{P("color", .color)}, .dur = 0.8, .replay = false },
    .{ .name = "spin", .kind = .change, .pos = &.{ P("target", .id), P("rate", .vec) }, .dur = 0 },
    .{ .name = "group", .kind = .flow, .pos = &.{.{ .name = "ids", .ty = .id, .many = true }}, .dur = 0, .needs_name = true },
};

pub fn findOp(name: []const u8) ?*const Op {
    for (&ops) |*op| if (std.mem.eql(u8, op.name, name)) return op;
    return null;
}

pub const default_board: Vec3 = .{ 16, 9, 0 };
pub const board_min: Vec3 = .{ 8, 4.5, 0 };
pub const board_max: Vec3 = .{ 40, 24, 24 };

pub const Anchor = enum { center, top, bottom, left, right, front, back, start, end };

pub const named_colors = [_]struct { []const u8, u24 }{
    .{ "red", 0xef5350 },    .{ "orange", 0xffa726 }, .{ "yellow", 0xffee58 },
    .{ "green", 0x66bb6a },  .{ "teal", 0x26a69a },   .{ "cyan", 0x4dd0e1 },
    .{ "blue", 0x42a5f5 },   .{ "purple", 0xab47bc }, .{ "pink", 0xf06292 },
    .{ "white", 0xf5f5f5 },  .{ "gray", 0x9e9e9e },   .{ "grey", 0x9e9e9e },
    .{ "black", 0x212121 },  .{ "brown", 0x8d6e63 },
};

// ───────────────────────────── values ─────────────────────────────

const Vec3 = [3]f64;

const Point = union(enum) {
    abs: Vec3,
    ref: struct { id: []const u8, anchor: []const u8, off: Vec3 },
};

const Value = union(enum) {
    num: f64,
    vec: Vec3,
    point: Point,
    str: []const u8,
    color: u24,
    range: [2]f64,
    boolean: bool,
    id: []const u8,
    word: []const u8,
    list: []Value,
};

// ───────────────────────────── scene ─────────────────────────────

const Entity = struct {
    op: []const u8, // static string from `ops`
    parent: ?[]const u8, // owned by the names map (key of the parent)
    /// Pin names, comma-separated (owned; "" if none): `R1.a`, `cpu.clk`.
    ports: []const u8 = "",
    /// Estimated board-space extent (x0, y0, x1, y1) of text, panels, blocks,
    /// waves and nodes placed at board coordinates; null when unknown or moved.
    box: ?[4]f64 = null,
    /// For axes: their data ranges (plots are sampled against them).
    axes: ?AxesRanges = null,

    fn hasPort(e: Entity, name: []const u8) bool {
        var it = std.mem.splitScalar(u8, e.ports, ',');
        while (it.next()) |p| if (p.len > 0 and std.mem.eql(u8, p, name)) return true;
        return false;
    }
};

const AxesRanges = struct { is3: bool, x: [2]f64, y: [2]f64, z: [2]f64, xlog: bool = false, ylog: bool = false };

/// Names that currently exist. Shared by every producer feeding one scene.
pub const Scene = struct {
    gpa: Allocator,
    names: std.StringArrayHashMapUnmanaged(Entity) = .empty,
    auto_id: u32 = 0,
    /// Usable board area (x -w/2..w/2, y -h/2..h/2) and the depth of the stage in
    /// front of it; set by `board`, kept by `clear`.
    board: Vec3 = default_board,
    /// Set by `Stream` for the lines of a `par` … `end` block after the first.
    force_par: bool = false,
    /// Sample `plot` and `surf` into points (IR fields `polys` and `zgrid`).
    /// Off only in tests that check the rest of the output.
    sample: bool = true,
    /// Values computed by `data` commands, as text: "d.x" → "1 2 3". Later
    /// lines (and prose) use them as {d.x}. They outlive `clear`.
    vars: std.StringHashMapUnmanaged([]u8) = .empty,
    /// What runs a `data` command's code (the server, `vcl figs`; in the
    /// browser, a table of results computed ahead of time). Null: none.
    compute: ?Compute = null,

    pub const Compute = struct {
        ctx: *anyopaque,
        run: *const fn (ctx: *anyopaque, lang: []const u8, code: []const u8, key: []const u8) Computed,
    };
    /// JSON of the code's variables, or why there is none (both owned by the scene's gpa).
    pub const Computed = union(enum) { json: []u8, err: []u8, missing };

    pub fn init(gpa: Allocator) Scene {
        return .{ .gpa = gpa };
    }

    pub fn deinit(s: *Scene) void {
        s.freeAll();
        s.names.deinit(s.gpa);
        var it = s.vars.iterator();
        while (it.next()) |kv| {
            s.gpa.free(kv.key_ptr.*);
            s.gpa.free(kv.value_ptr.*);
        }
        s.vars.deinit(s.gpa);
    }

    fn setVar(s: *Scene, name: []const u8, text: []const u8) !void {
        const v = try s.gpa.dupe(u8, text);
        errdefer s.gpa.free(v);
        if (s.vars.getPtr(name)) |old| {
            s.gpa.free(old.*);
            old.* = v;
            return;
        }
        const k = try s.gpa.dupe(u8, name);
        errdefer s.gpa.free(k);
        try s.vars.put(s.gpa, k, v);
    }

    /// `text` with every {name.field} of a `data` result replaced by its value
    /// (other braces, as in LaTeX, stay). Allocated in `a` (or `text` itself).
    pub fn expand(s: *const Scene, a: Allocator, text: []const u8) Allocator.Error![]const u8 {
        if (s.vars.count() == 0 or std.mem.indexOfScalar(u8, text, '{') == null) return text;
        var out: std.ArrayList(u8) = .empty;
        var i: usize = 0;
        while (i < text.len) {
            if (text[i] == '{') if (std.mem.indexOfScalarPos(u8, text, i, '}')) |j| {
                if (s.vars.get(text[i + 1 .. j])) |v| {
                    try out.appendSlice(a, v);
                    i = j + 1;
                    continue;
                }
            };
            try out.append(a, text[i]);
            i += 1;
        }
        return out.items;
    }

    pub fn reset(s: *Scene) void {
        s.freeAll();
        s.names.clearRetainingCapacity();
    }

    fn freeAll(s: *Scene) void {
        for (s.names.keys(), s.names.values()) |k, e| {
            s.gpa.free(k);
            s.gpa.free(e.ports);
        }
    }

    fn add(s: *Scene, name: []const u8, op: []const u8, parent: ?[]const u8, ports: []const u8) !void {
        if (s.names.contains(name)) s.remove(name);
        const key = try s.gpa.dupe(u8, name);
        errdefer s.gpa.free(key);
        const owned_ports = try s.gpa.dupe(u8, ports);
        errdefer s.gpa.free(owned_ports);
        const parent_key: ?[]const u8 = if (parent) |p| (s.names.getKey(p) orelse null) else null;
        try s.names.put(s.gpa, key, .{ .op = op, .parent = parent_key, .ports = owned_ports });
    }

    /// Remove `name` and everything parented (transitively) to it.
    fn remove(s: *Scene, name: []const u8) void {
        const key = s.names.getKey(name) orelse return;
        // Children first, since they point at `key`.
        var i: usize = 0;
        while (i < s.names.count()) {
            const e = s.names.values()[i];
            if (e.parent) |p| if (p.ptr == key.ptr) {
                s.remove(s.names.keys()[i]);
                i = 0;
                continue;
            };
            i += 1;
        }
        const ports = s.names.get(key).?.ports;
        _ = s.names.orderedRemove(key);
        s.gpa.free(key);
        s.gpa.free(ports);
    }

    fn setParent(s: *Scene, child: []const u8, parent: []const u8) void {
        const pk = s.names.getKey(parent) orelse return;
        if (s.names.getPtr(child)) |e| e.parent = pk;
    }

    /// Parse and execute one complete line, writing IR/diagnostic JSON lines.
    pub fn command(s: *Scene, text: []const u8, line_no: u32, out: *Writer) Writer.Error!void {
        return s.commandBody(text, null, line_no, out);
    }

    /// A command whose last string argument is a `<<TAG` … `TAG` block.
    pub fn commandBody(s: *Scene, text: []const u8, body: ?[]const u8, line_no: u32, out: *Writer) Writer.Error!void {
        var arena_state = std.heap.ArenaAllocator.init(s.gpa);
        defer arena_state.deinit();
        var p: LineParser = .{ .scene = s, .arena = arena_state.allocator(), .line_no = line_no, .out = out, .body = body };
        p.run(text) catch |err| switch (err) {
            error.Bad => try p.emitDiag("error", p.err_msg),
            error.OutOfMemory => try p.emitDiag("error", "out of memory"),
            error.WriteFailed => return error.WriteFailed,
        };
    }

    /// Emit a `say` (used by mixed mode for prose).
    pub fn say(s: *Scene, raw: []const u8, line_no: u32, out: *Writer) Writer.Error!void {
        var arena_state = std.heap.ArenaAllocator.init(s.gpa);
        defer arena_state.deinit();
        const text = s.expand(arena_state.allocator(), raw) catch raw;
        try out.print("{{\"op\":\"say\",\"line\":{d},\"par\":false,\"dur\":", .{line_no});
        try writeNum(out, speechDuration(text));
        try out.writeAll(",\"text\":");
        try writeJsonString(out, text);
        try out.writeAll("}\n");
    }
};

/// A `data` value as VCL text: a number (6 significant digits), a string as
/// it is, a list as "a b c", a list of rows as "x,y x,y". False: not usable.
fn dataText(a: Allocator, out: *std.ArrayList(u8), v: std.json.Value, depth: u8) Allocator.Error!bool {
    switch (v) {
        .integer => |i| try out.print(a, "{d}", .{i}),
        .float => |f| try dataNum(a, out, f),
        .number_string => |s| try out.appendSlice(a, s),
        .string => |s| try out.appendSlice(a, s),
        .bool => |b| try out.appendSlice(a, if (b) "true" else "false"),
        .array => |arr| {
            if (depth > 1) return false;
            for (arr.items, 0..) |item, i| {
                if (i > 0) try out.append(a, if (depth == 0) ' ' else ',');
                if (!try dataText(a, out, item, depth + 1)) return false;
            }
        },
        else => return false,
    }
    return true;
}

fn dataNum(a: Allocator, out: *std.ArrayList(u8), f: f64) Allocator.Error!void {
    if (!std.math.isFinite(f)) return out.appendSlice(a, "0");
    if (f == @round(f) and @abs(f) < 1e15) return out.print(a, "{d}", .{@as(i64, @intFromFloat(f))});
    const mag = std.math.log10(@abs(f));
    const decimals: usize = @intFromFloat(std.math.clamp(5 - @floor(mag), 0, 12));
    var buf: [64]u8 = undefined;
    var s: []const u8 = std.fmt.bufPrint(&buf, "{d:.[1]}", .{ f, decimals }) catch return out.appendSlice(a, "0");
    if (std.mem.indexOfScalar(u8, s, '.') != null) {
        s = std.mem.trimEnd(u8, s, "0");
        s = std.mem.trimEnd(u8, s, ".");
    }
    try out.appendSlice(a, s);
}

fn speechDuration(text: []const u8) f64 {
    var words: f64 = 0;
    var it = std.mem.tokenizeAny(u8, text, " \t\n");
    while (it.next()) |_| words += 1;
    return @max(1.2, words * 0.38 + 0.4);
}

// ───────────────────────────── line parser ─────────────────────────────

const Token = struct { text: []const u8, col: usize };

const LineParser = struct {
    scene: *Scene,
    arena: Allocator,
    line_no: u32,
    out: *Writer,
    err_msg: []const u8 = "",
    op: *const Op = undefined,
    /// Text of a `<<TAG` block; stands in as the last positional argument.
    body: ?[]const u8 = null,

    const Error = error{ Bad, OutOfMemory, WriteFailed };

    fn fail(p: *LineParser, comptime fmt: []const u8, args: anytype) Error {
        p.err_msg = std.fmt.allocPrint(p.arena, fmt, args) catch "error";
        return error.Bad;
    }

    fn emitDiag(p: *LineParser, level: []const u8, msg: []const u8) Writer.Error!void {
        try p.out.print("{{\"op\":\"diag\",\"level\":\"{s}\",\"line\":{d},\"msg\":", .{ level, p.line_no });
        try writeJsonString(p.out, msg);
        try p.out.writeAll("}\n");
    }

    fn warn(p: *LineParser, comptime fmt: []const u8, args: anytype) Error!void {
        const msg = try std.fmt.allocPrint(p.arena, fmt, args);
        try p.emitDiag("warning", msg);
    }

    fn tokenize(p: *LineParser, line: []const u8) Error![]Token {
        var toks: std.ArrayList(Token) = .empty;
        var i: usize = 0;
        while (i < line.len) {
            const c = line[i];
            if (c == ' ' or c == '\t' or c == '\r') {
                i += 1;
                continue;
            }
            // Comment: '#' at line start or followed by whitespace/end.
            if (c == '#' and (toks.items.len == 0 or i + 1 == line.len or line[i + 1] == ' ' or line[i + 1] == '\t')) break;
            const start = i;
            var in_str = false;
            while (i < line.len) : (i += 1) {
                const ch = line[i];
                if (in_str) {
                    if (ch == '\\' and i + 1 < line.len) {
                        i += 1;
                    } else if (ch == '"') {
                        in_str = false;
                        // A string ends the token: `"a": "b"` is a typo, not one value.
                        if (i + 1 < line.len and !(line[i + 1] == ' ' or line[i + 1] == '\t' or line[i + 1] == '\r'))
                            return p.fail("unexpected '{c}' right after the closing quote at column {d} (a quoted string must be followed by a space)", .{ line[i + 1], i + 1 });
                    }
                } else if (ch == '"') {
                    in_str = true;
                } else if (ch == ' ' or ch == '\t' or ch == '\r') break;
            }
            if (in_str) return p.fail("unterminated string starting at column {d}", .{start + 1});
            try toks.append(p.arena, .{ .text = line[start..i], .col = start + 1 });
        }
        return toks.items;
    }

    fn run(p: *LineParser, src_line: []const u8) Error!void {
        // {d.x} from `data` results, except in code that runs (fig/data: f-strings use braces).
        var line = src_line;
        if (p.scene.vars.count() > 0) {
            const first = try p.tokenize(src_line);
            const runs_code = for (first) |t| {
                const op_text = if (std.mem.indexOfScalar(u8, t.text, ':')) |c| t.text[c + 1 ..] else t.text;
                if (op_text.len == 0 or std.mem.eql(u8, t.text, "&")) continue;
                break std.mem.eql(u8, op_text, "fig") or std.mem.eql(u8, op_text, "data") or std.mem.eql(u8, op_text, "&fig") or std.mem.eql(u8, op_text, "&data");
            } else false;
            if (!runs_code) {
                line = try p.scene.expand(p.arena, src_line);
                if (p.body) |b| p.body = try p.scene.expand(p.arena, b);
            }
        }
        var toks = try p.tokenize(line);
        if (toks.len == 0) return;

        var par = p.scene.force_par;
        var name: ?[]const u8 = null;

        // '&' prefix, alone or glued to the next token.
        if (std.mem.eql(u8, toks[0].text, "&")) {
            par = true;
            toks = toks[1..];
        } else if (toks[0].text[0] == '&') {
            par = true;
            toks[0].text = toks[0].text[1..];
        }
        if (toks.len == 0) return p.fail("'&' must be followed by a command", .{});

        // 'name:' or 'name:op'.
        if (std.mem.indexOfScalar(u8, toks[0].text, ':')) |colon| {
            const n = toks[0].text[0..colon];
            if (!isIdent(n)) return p.fail("invalid name '{s}' (names are letters, digits and _, not starting with a digit)", .{n});
            name = n;
            const rest = toks[0].text[colon + 1 ..];
            if (rest.len == 0) {
                toks = toks[1..];
            } else toks[0].text = rest;
            if (toks.len == 0) return p.fail("'{s}:' must be followed by a command", .{n});
        }

        const op_name = toks[0].text;
        p.op = findOp(op_name) orelse {
            var names: [ops.len][]const u8 = undefined;
            for (ops, 0..) |o, i| names[i] = o.name;
            if (looksLikeProse(toks))
                return p.fail("unknown command '{s}': this line looks like prose; text that mixes prose and ```vcl fences must be sent with mode=mixed", .{op_name});
            if (suggest(op_name, &names)) |s|
                return p.fail("unknown command '{s}' (did you mean '{s}'?)", .{ op_name, s });
            return p.fail("unknown command '{s}'", .{op_name});
        };
        const op = p.op;
        if (name) |n| {
            if (op.kind != .draw and !op.needs_name and !op.question)
                return p.fail("{s}: does not create anything, so it can't be named '{s}'", .{ op.name, n });
        }
        if (op.needs_name and name == null)
            return p.fail("{s}: needs a name, e.g. 'g: {s} a b'", .{ op.name, op.name });

        // Split positionals and key=value.
        var positional: std.ArrayList(Token) = .empty;
        var keyed: std.ArrayList(struct { key: []const u8, val: Token }) = .empty;
        for (toks[1..]) |t| {
            if (t.text[0] != '"') if (std.mem.indexOfScalar(u8, t.text, '=')) |eq| {
                const k = t.text[0..eq];
                if (isIdent(k)) {
                    if (eq + 1 == t.text.len) return p.fail("{s}: '{s}=' needs a value", .{ op.name, k });
                    try keyed.append(p.arena, .{ .key = k, .val = .{ .text = t.text[eq + 1 ..], .col = t.col + eq + 1 } });
                    continue;
                }
            };
            try positional.append(p.arena, t);
        }
        if (p.body != null) try positional.append(p.arena, .{ .text = heredoc_marker, .col = 0 });

        // Positionals.
        const Field = struct { name: []const u8, val: Value };
        var fields: std.ArrayList(Field) = .empty;
        var pi: usize = 0;
        for (op.pos) |param| {
            if (param.many) {
                const rest = positional.items[pi..];
                if (rest.len < param.min and p.body == null) return p.fail("{s}: expected at least {d} {s} for '{s}', got {d}{s}", .{
                    op.name, param.min, tyName(param.ty), param.name, rest.len, usageHint(op),
                });
                if (rest.len > param.max) return p.fail("{s}: at most {d} values for '{s}', got {d}", .{ op.name, param.max, param.name, rest.len });
                var list: std.ArrayList(Value) = .empty;
                for (rest) |t| {
                    if (t.text.ptr == heredoc_marker.ptr and param.ty == .str) {
                        var lines = std.mem.splitScalar(u8, p.body.?, '\n');
                        while (lines.next()) |l| if (std.mem.trim(u8, l, " \t\r").len > 0)
                            try list.append(p.arena, .{ .str = std.mem.trimEnd(u8, l, "\r") });
                    } else try list.append(p.arena, try p.value(param, t.text));
                }
                try fields.append(p.arena, .{ .name = param.name, .val = .{ .list = list.items } });
                pi = positional.items.len;
                break;
            }
            if (pi >= positional.items.len) {
                if (param.opt) break;
                return p.fail("{s}: missing '{s}' ({s}){s}", .{ op.name, param.name, tyName(param.ty), usageHint(op) });
            }
            try fields.append(p.arena, .{ .name = param.name, .val = try p.value(param, positional.items[pi].text) });
            pi += 1;
        }
        if (pi < positional.items.len) {
            const extra = positional.items[pi];
            if (extra.text.ptr == heredoc_marker.ptr)
                return p.fail("{s}: has no text argument for a <<TAG block{s}", .{ op.name, usageHint(op) });
            if (isIdent(extra.text))
                return p.fail("{s}: unexpected argument '{s}' (optional settings are written key=value){s}", .{ op.name, extra.text, usageHint(op) });
            return p.fail("{s}: unexpected argument '{s}'{s}", .{ op.name, extra.text, usageHint(op) });
        }

        // Keys.
        var dur: f64 = op.dur;
        var ease: ?[]const u8 = null;
        var on: ?[]const u8 = null;
        key_loop: for (keyed.items) |kv| {
            const k = kv.key;
            if (std.mem.eql(u8, k, "op") or std.mem.eql(u8, k, "id") or std.mem.eql(u8, k, "par") or std.mem.eql(u8, k, "line"))
                return p.fail("{s}: '{s}' is reserved", .{ op.name, k });
            const param = blk: {
                for (op.keys) |kp| if (std.mem.eql(u8, kp.name, k)) break :blk kp;
                for (timing_keys) |kp| if (std.mem.eql(u8, kp.name, k)) break :blk kp;
                if (op.kind == .draw) for (draw_keys) |kp| if (std.mem.eql(u8, kp.name, k)) break :blk kp;
                // Unknown: warn and skip.
                var cands: std.ArrayList([]const u8) = .empty;
                for (op.keys) |kp| try cands.append(p.arena, kp.name);
                for (timing_keys) |kp| try cands.append(p.arena, kp.name);
                if (op.kind == .draw) for (draw_keys) |kp| try cands.append(p.arena, kp.name);
                if (suggest(k, cands.items)) |s|
                    try p.warn("{s}: unknown key '{s}' ignored (did you mean '{s}'?)", .{ op.name, k, s })
                else
                    try p.warn("{s}: unknown key '{s}' ignored", .{ op.name, k });
                continue :key_loop;
            };
            for (fields.items) |f| if (std.mem.eql(u8, f.name, k))
                return p.fail("{s}: '{s}' given twice", .{ op.name, k });
            const v = try p.value(param, kv.val.text);
            if (std.mem.eql(u8, k, "dur")) {
                if (v.num < 0) return p.fail("{s}: dur must be >= 0", .{op.name});
                dur = v.num;
                continue;
            }
            if (std.mem.eql(u8, k, "ease")) {
                ease = v.word;
                continue;
            }
            if (std.mem.eql(u8, k, "on")) on = v.id;
            try fields.append(p.arena, .{ .name = k, .val = v });
        }

        // Ops drawn on axes (plot, bars, …) belong to them: deleting the axes deletes them.
        if (op.pos.len > 0 and op.pos[0].of_op != null and std.mem.eql(u8, op.pos[0].name, "axes"))
            on = fields.items[0].val.id;

        // Op-specific checks.
        if (std.mem.eql(u8, op.name, "wait")) {
            if (fields.items[0].val.num < 0) return p.fail("wait: seconds must be >= 0", .{});
            dur = fields.items[0].val.num;
        }
        if (std.mem.eql(u8, op.name, "axes")) try p.checkAxes(fields.items);
        if (op.question) try p.checkQuestion(fields.items);
        if (std.mem.eql(u8, op.name, "data")) return p.runData(name.?, fieldStr(fields.items, "lang").?, fieldStr(fields.items, "code").?);
        if (std.mem.eql(u8, op.name, "fig") and std.mem.trim(u8, fieldStr(fields.items, "code").?, " \t\r\n").len == 0)
            return p.fail("fig: the code is empty; put it in a <<END block: f: fig 0,0 python <<END … END", .{});
        if (std.mem.eql(u8, op.name, "say") and !hasKey(keyed.items, "dur")) dur = speechDuration(fields.items[0].val.str);
        if (std.mem.eql(u8, op.name, "board")) {
            var b = default_board;
            if (fields.items.len > 0) {
                b = fields.items[0].val.vec;
                for (0..3) |k| if (b[k] < board_min[k] or b[k] > board_max[k]) {
                    const axis = [_][]const u8{ "width", "height", "depth" };
                    return p.fail("board: {s} {d} is out of range ({d}..{d}); the default is 16,9 (and depth 0)", .{ axis[k], b[k], board_min[k], board_max[k] });
                };
                fields.items[0].val.vec = b;
            }
            p.scene.board = b;
        }
        const box: ?[4]f64 = if (on == null) try p.lintBounds(fields.items) else null;
        var samples: ?[]const u8 = null;
        if (std.mem.eql(u8, op.name, "plot") or std.mem.eql(u8, op.name, "surf") or std.mem.eql(u8, op.name, "contour"))
            samples = try p.sample(fields.items);
        const derived: ?Derived = try p.derive(fields.items);
        if (std.mem.eql(u8, op.name, "group")) {
            for (fields.items[0].val.list) |v| if (std.mem.eql(u8, v.id, name.?))
                return p.fail("group: '{s}' can't contain itself", .{v.id});
        }

        // Effects on the scene.
        var id_buf: [16]u8 = undefined;
        const id: ?[]const u8 = if (op.kind == .draw or op.needs_name) name orelse blk: {
            p.scene.auto_id += 1;
            break :blk std.fmt.bufPrint(&id_buf, "_{d}", .{p.scene.auto_id}) catch unreachable;
        } else if (op.question) name else null;

        if (op.question) {
            // A question's name only labels its answer; it isn't on the board.
        } else if (std.mem.eql(u8, op.name, "clear")) {
            p.scene.reset();
        } else if (op.kind == .change and !std.mem.eql(u8, op.name, "color") and !std.mem.eql(u8, op.name, "hl")) {
            // Moved, turned, scaled or faded: its old extent no longer holds.
            if (p.scene.names.getPtr(fields.items[0].val.id)) |e| e.box = null;
        } else if (std.mem.eql(u8, op.name, "del")) {
            for (fields.items[0].val.list) |v| p.scene.remove(v.id);
        } else if (id) |i| {
            var ports: []const u8 = "";
            if (std.mem.eql(u8, op.name, "part")) {
                const kind = fieldStr(fields.items, "kind").?;
                for (part_pins) |pp| if (std.mem.eql(u8, pp[0], kind)) {
                    ports = pp[1];
                };
            } else if (std.mem.eql(u8, op.name, "block")) {
                var all: std.ArrayList(u8) = .empty;
                for ([_][]const u8{ "in", "out", "top", "bottom" }) |side| {
                    const list = fieldStr(fields.items, side) orelse continue;
                    var it = std.mem.splitScalar(u8, list, ',');
                    while (it.next()) |raw| {
                        const port = std.mem.trim(u8, raw, " ");
                        if (port.len == 0) continue;
                        if (!isIdent(port)) return p.fail("block: port '{s}' must be a plain name (letters, digits, _)", .{port});
                        if (std.meta.stringToEnum(Anchor, port) != null) return p.fail("block: port '{s}' clashes with the anchor of the same name; pick another", .{port});
                        var dup = std.mem.splitScalar(u8, all.items, ',');
                        while (dup.next()) |d| if (std.mem.eql(u8, d, port)) return p.fail("block: port '{s}' listed twice", .{port});
                        if (all.items.len > 0) try all.append(p.arena, ',');
                        try all.appendSlice(p.arena, port);
                    }
                }
                ports = all.items;
                try p.lintBlock(fields.items);
            } else if (std.mem.eql(u8, op.name, "code") or std.mem.eql(u8, op.name, "table")) {
                // Numbered rows: code lines L1…, table data rows R1… (after the header).
                const is_code = op.name[0] == 'c';
                var n: usize = 0;
                if (is_code) {
                    n = std.mem.count(u8, fieldStr(fields.items, "text").?, "\n") + 1;
                } else {
                    for (fields.items) |f| if (std.mem.eql(u8, f.name, "rows")) {
                        n = f.val.list.len;
                    };
                    const header = for (fields.items) |f| {
                        if (std.mem.eql(u8, f.name, "header")) break f.val.boolean;
                    } else true;
                    if (header and n > 0) n -= 1;
                }
                var all: std.ArrayList(u8) = .empty;
                for (1..@min(n, 999) + 1) |k| {
                    if (all.items.len > 0) try all.append(p.arena, ',');
                    try all.print(p.arena, "{c}{d}", .{ @as(u8, if (is_code) 'L' else 'R'), k });
                }
                ports = all.items;
            } else if (std.mem.eql(u8, op.name, "wave")) {
                // Signals are pins under their identifier form: "FIFO write" → w.FIFO_write, d[7:0] → w.d.
                var all: std.ArrayList(u8) = .empty;
                var owners: std.ArrayList(struct { []const u8, []const u8 }) = .empty; // pin, signal name
                for (fields.items) |f| if (std.mem.eql(u8, f.name, "rows")) for (f.val.list) |row| {
                    const head = std.mem.sliceTo(row.str, '|');
                    const colon = std.mem.lastIndexOfScalar(u8, head, ':');
                    const pat = if (colon) |c| head[c + 1 ..] else head;
                    for (pat) |ch| if (std.mem.indexOfScalar(u8, "01zxpn=.23456789 \t", ch) == null)
                        return p.fail("wave: unknown symbol '{c}' in \"{s}\" (use 0 1 z x . = p n, after 'name:')", .{ ch, row.str });
                    const nm = std.mem.trim(u8, head[0 .. colon orelse 0], " ");
                    if (nm.len == 0) continue;
                    const pin = try wavePin(p.arena, nm);
                    if (pin.len == 0 or std.meta.stringToEnum(Anchor, pin) != null) {
                        try p.warn("wave: signal '{s}' gets no pin (point at it with an offset from the wave instead, or rename it)", .{nm});
                        continue;
                    }
                    const earlier = for (owners.items) |o| {
                        if (std.mem.eql(u8, o[0], pin)) break o[1];
                    } else null;
                    if (earlier) |e| {
                        try p.warn("wave: signals '{s}' and '{s}' both become the pin '{s}'; only '{s}' gets it", .{ e, nm, pin, e });
                        continue;
                    }
                    try owners.append(p.arena, .{ pin, nm });
                    if (all.items.len > 0) try all.append(p.arena, ',');
                    try all.appendSlice(p.arena, pin);
                };
                ports = all.items;
            }
            if (derived) |d| ports = d.ports;
            if (box) |b| try p.lintOverlap(i, b);
            try p.scene.add(i, op.name, on, ports);
            if (std.mem.eql(u8, op.name, "axes")) p.scene.names.getPtr(i).?.axes = axesRanges(fields.items);
            if (box) |b| p.scene.names.getPtr(i).?.box = b;
            if (std.mem.eql(u8, op.name, "group"))
                for (fields.items[0].val.list) |v| {
                    p.scene.setParent(v.id, i);
                    if (p.scene.names.getPtr(v.id)) |e| e.box = null; // the group may move them
                };
        }

        // Emit.
        const o = p.out;
        try o.print("{{\"op\":\"{s}\"", .{op.name});
        if (id) |i| {
            try o.writeAll(",\"id\":");
            try writeJsonString(o, i);
        }
        try o.print(",\"line\":{d},\"par\":{},\"dur\":", .{ p.line_no, par });
        try writeNum(o, dur);
        if (ease) |e| try o.print(",\"ease\":\"{s}\"", .{e});
        for (fields.items) |f| {
            try o.writeAll(",");
            try writeJsonString(o, f.name);
            try o.writeAll(":");
            try writeValue(o, f.val);
        }
        if (samples) |sm| if (p.scene.sample) try o.writeAll(sm);
        if (std.mem.eql(u8, op.name, "fig")) {
            const key = figKey(fieldStr(fields.items, "lang").?, fieldStr(fields.items, "code").?);
            try o.print(",\"key\":\"{s}\"", .{&key});
        }
        if (derived) |d| try o.writeAll(d.json);
        try o.writeAll("}\n");
    }

    /// `name: data LANG <<END … END`: run the code (through the scene's
    /// `compute`) and keep its variables as {name.var}. No IR of its own.
    fn runData(p: *LineParser, name: []const u8, lang: []const u8, code: []const u8) Error!void {
        if (std.mem.trim(u8, code, " \t\r\n").len == 0) return p.fail("data: the code is empty; put it in a <<END block: d: data python <<END … END", .{});
        const key = dataKey(lang, code);
        const c = p.scene.compute orelse return p.fail("data: nothing here runs code (it needs vcl serve, or results from vcl figs)", .{});
        const result = c.run(c.ctx, lang, code, &key);
        const json = switch (result) {
            .json => |j| j,
            .err => |e| {
                defer p.scene.gpa.free(e);
                return p.fail("data: {s}", .{e});
            },
            .missing => return p.fail("data: not computed (it needs vcl serve; a published copy has only its own lessons' results)", .{}),
        };
        defer p.scene.gpa.free(json);
        const parsed = std.json.parseFromSliceLeaky(std.json.Value, p.arena, json, .{}) catch return p.fail("data: unreadable results", .{});
        if (parsed != .object) return p.fail("data: unreadable results", .{});
        var it = parsed.object.iterator();
        var n: usize = 0;
        while (it.next()) |kv| {
            var buf: std.ArrayList(u8) = .empty;
            if (!try dataText(p.arena, &buf, kv.value_ptr.*, 0)) continue;
            const full = try std.fmt.allocPrint(p.arena, "{s}.{s}", .{ name, kv.key_ptr.* });
            try p.scene.setVar(full, buf.items);
            n += 1;
        }
        if (n == 0) try p.warn("data: the code set no numbers, lists or strings to use as {{{s}.…}}", .{name});
    }

    /// quiz/pick need answer=N (1-based, several for quiz/pick: 1,3) among
    /// their choices; estimate's answer must lie in its range.
    fn checkQuestion(p: *LineParser, fields: anytype) Error!void {
        const op = p.op.name;
        const choices: ?usize = for (fields) |f| {
            if (std.mem.eql(u8, f.name, "options") or std.mem.eql(u8, f.name, "ids")) break f.val.list.len;
        } else null;
        if (choices) |n| {
            if (n < 2) return p.fail("{s}: needs at least 2 choices, got {d}", .{ op, n });
            if (n > (if (op[0] == 'q') @as(usize, 8) else 12)) return p.fail("{s}: at most {d} choices, got {d}", .{ op, @as(usize, if (op[0] == 'q') 8 else 12), n });
            const answer = for (fields) |f| {
                if (std.mem.eql(u8, f.name, "answer")) break f.val.list;
            } else return p.fail("{s}: needs answer=N, the number of the right choice (1 is the first; answer=1,3 if several are right)", .{op});
            for (answer, 0..) |a, i| {
                const k = a.num;
                if (k != @floor(k) or k < 1 or k > @as(f64, @floatFromInt(n)))
                    return p.fail("{s}: answer {d} isn't one of the choices (1..{d})", .{ op, k, n });
                for (answer[0..i]) |b| if (b.num == k) return p.fail("{s}: answer {d} listed twice", .{ op, k });
            }
        }
        if (std.mem.eql(u8, op, "estimate")) {
            const r = fields[1].val.range;
            const answer = for (fields) |f| {
                if (std.mem.eql(u8, f.name, "answer")) break f.val.num;
            } else return p.fail("estimate: needs answer=N, the right value", .{});
            if (answer < r[0] or answer > r[1]) return p.fail("estimate: answer {d} is outside the slider's range {d}..{d}", .{ answer, r[0], r[1] });
            for (fields) |f| if (std.mem.eql(u8, f.name, "tol") and f.val.num < 0) return p.fail("estimate: tol must be >= 0", .{});
            for (fields) |f| if (std.mem.eql(u8, f.name, "step") and f.val.num <= 0) return p.fail("estimate: step must be > 0", .{});
        }
    }

    fn value(p: *LineParser, param: Param, text: []const u8) Error!Value {
        const op = p.op.name;
        if (text.ptr == heredoc_marker.ptr) {
            if (param.ty != .str) return p.fail("{s}: a <<END block can only fill a string argument, not '{s}'", .{ op, param.name });
            return .{ .str = p.body.? };
        }
        switch (param.ty) {
            .num => return .{ .num = parseNum(text) orelse
                return p.fail("{s}: expected a number for '{s}', got '{s}'", .{ op, param.name, text }) },
            .nums => {
                var vals: std.ArrayList(Value) = .empty;
                var it = std.mem.splitScalar(u8, std.mem.trim(u8, text, "\""), ',');
                while (it.next()) |t| try vals.append(p.arena, .{ .num = parseNum(std.mem.trim(u8, t, " ")) orelse
                    return p.fail("{s}: expected a number or numbers like 1,3 for '{s}', got '{s}'", .{ op, param.name, text }) });
                return .{ .list = vals.items };
            },
            .vec => return .{ .vec = parseVec(text) orelse
                return p.fail("{s}: expected a vector like 1,2 or 1,2,3 for '{s}', got '{s}'", .{ op, param.name, text }) },
            .range => {
                const lines_ok = std.mem.eql(u8, param.name, "focus"); // line/row numbers: 3 or 3..5
                if (lines_ok) if (parseNum(text)) |n| return .{ .range = .{ n, n } };
                const dots = std.mem.indexOf(u8, text, "..") orelse
                    return p.fail("{s}: expected a range like -3..3 for '{s}', got '{s}'", .{ op, param.name, text });
                const a = parseNum(text[0..dots]);
                const b = parseNum(text[dots + 2 ..]);
                if (a == null or b == null) return p.fail("{s}: expected a range like -3..3 for '{s}', got '{s}'", .{ op, param.name, text });
                if (!(a.? < b.?) and !(lines_ok and a.? == b.?)) return p.fail("{s}: range '{s}' must go from low to high", .{ op, text });
                return .{ .range = .{ a.?, b.? } };
            },
            .str => {
                if (text[0] != '"') return p.fail("{s}: '{s}' must be a quoted string, e.g. \"{s}\"", .{ op, param.name, text });
                return .{ .str = try unescape(p.arena, text[1 .. text.len - 1], param.raw) };
            },
            .boolean => {
                if (param.nums and text[0] == '"') {
                    var vals: std.ArrayList(Value) = .empty;
                    var it = std.mem.tokenizeAny(u8, text[1 .. text.len - 1], ", \t");
                    while (it.next()) |t| try vals.append(p.arena, .{ .num = parseNum(t) orelse
                        return p.fail("{s}: '{s}' takes true/false or a list of numbers like \"1,2,5\"; got '{s}'", .{ op, param.name, t }) });
                    if (vals.items.len == 0) return p.fail("{s}: '{s}' is an empty list (use {s}=false for no ticks)", .{ op, param.name, param.name });
                    return .{ .list = vals.items };
                }
                const t = [_][]const u8{ "true", "on", "yes", "1" };
                const f = [_][]const u8{ "false", "off", "no", "0" };
                for (t) |w| if (std.ascii.eqlIgnoreCase(text, w)) return .{ .boolean = true };
                for (f) |w| if (std.ascii.eqlIgnoreCase(text, w)) return .{ .boolean = false };
                return p.fail("{s}: expected true/false for '{s}', got '{s}'", .{ op, param.name, text });
            },
            .word => {
                if (param.words.len == 0) {
                    for (text) |c| if (!(std.ascii.isAlphanumeric(c) or c == '_' or c == '+' or c == '#' or c == '-' or c == '.'))
                        return p.fail("{s}: '{s}' should be a plain word, got '{s}'", .{ op, param.name, text });
                    return .{ .word = text };
                }
                for (param.words) |w| if (std.mem.eql(u8, w, text)) return .{ .word = w };
                const list = try std.mem.join(p.arena, "|", param.words);
                return p.fail("{s}: '{s}' must be one of {s}, got '{s}'", .{ op, param.name, list, text });
            },
            .color => {
                if (text[0] == '#') {
                    const hex = text[1..];
                    const v = std.fmt.parseInt(u24, hex, 16) catch null;
                    if (v) |n| switch (hex.len) {
                        6 => return .{ .color = n },
                        3 => {
                            const r: u24 = (n >> 8) & 0xf;
                            const g: u24 = (n >> 4) & 0xf;
                            const b: u24 = n & 0xf;
                            return .{ .color = (r * 17) << 16 | (g * 17) << 8 | b * 17 };
                        },
                        else => {},
                    };
                    return p.fail("{s}: bad colour '{s}' (use #rgb or #rrggbb)", .{ op, text });
                }
                for (named_colors) |nc| if (std.ascii.eqlIgnoreCase(nc[0], text)) return .{ .color = nc[1] };
                var names: [named_colors.len][]const u8 = undefined;
                for (named_colors, 0..) |nc, i| names[i] = nc[0];
                if (suggest(text, &names)) |s| return p.fail("{s}: unknown colour '{s}' (did you mean '{s}'?)", .{ op, text, s });
                return p.fail("{s}: unknown colour '{s}' (use a name like blue or #rrggbb)", .{ op, text });
            },
            .id => {
                if (param.part) if (std.mem.indexOfScalar(u8, text, '.')) |dot| {
                    const n = text[0..dot];
                    const pin = text[dot + 1 ..];
                    if (!isIdent(n)) return p.fail("{s}: expected a name or name.part for '{s}', got '{s}'", .{ op, param.name, text });
                    const e = try p.lookup(n);
                    const ok = for (part_ops) |po| {
                        if (std.mem.eql(u8, po, e.op)) break true;
                    } else false;
                    if (!ok and !param.any_pin) return p.fail("{s}: '{s}' is a {s}; only parts of nn, grid, bits, bars and hist can be targeted (name.part)", .{ op, n, e.op });
                    if (!e.hasPort(pin)) {
                        var cands: std.ArrayList([]const u8) = .empty;
                        var it = std.mem.splitScalar(u8, e.ports, ',');
                        while (it.next()) |pn| if (pn.len > 0) try cands.append(p.arena, pn);
                        const hint = if (suggest(pin, cands.items)) |sg| try std.fmt.allocPrint(p.arena, " (did you mean '{s}'?)", .{sg}) else "";
                        if (param.any_pin and e.ports.len == 0) return p.fail("{s}: '{s}' (a {s}) has no pins; pick it whole", .{ op, n, e.op });
                        return p.fail("{s}: '{s}' has no {s} '{s}'{s}", .{ op, n, if (param.any_pin) "pin" else "part", pin, hint });
                    }
                    return .{ .id = text };
                };
                if (!isIdent(text)) return p.fail("{s}: expected a name for '{s}', got '{s}'", .{ op, param.name, text });
                const e = try p.lookup(text);
                if (param.of_op) |want| if (!std.mem.eql(u8, e.op, want))
                    return p.fail("{s}: '{s}' is a {s}, not {s}", .{ op, text, e.op, want });
                return .{ .id = text };
            },
            .point => {
                const c = text[0];
                if (std.ascii.isDigit(c) or c == '-' or c == '+' or c == '.') {
                    return .{ .point = .{ .abs = parseVec(text) orelse
                        return p.fail("{s}: expected a point like 1,2 or a name for '{s}', got '{s}'", .{ op, param.name, text }) } };
                }
                var rest = text;
                var off: Vec3 = .{ 0, 0, 0 };
                if (std.mem.indexOfScalar(u8, rest, '+')) |plus| {
                    off = parseVec(rest[plus + 1 ..]) orelse
                        return p.fail("{s}: bad offset in '{s}' (write name.anchor+dx,dy)", .{ op, text });
                    rest = rest[0..plus];
                }
                var anchor: []const u8 = "center";
                var name = rest;
                if (std.mem.indexOfScalar(u8, rest, '.')) |dot| {
                    anchor = rest[dot + 1 ..];
                    name = rest[0..dot];
                }
                if (!isIdent(name)) return p.fail("{s}: expected a point like 1,2 or a name for '{s}', got '{s}'", .{ op, param.name, text });
                const e = try p.lookup(name);
                if (std.meta.stringToEnum(Anchor, anchor) == null and !e.hasPort(anchor)) {
                    // Suggest among this entity's pins and the standard anchors.
                    var cands: std.ArrayList([]const u8) = .empty;
                    var it = std.mem.splitScalar(u8, e.ports, ',');
                    while (it.next()) |pin| if (pin.len > 0) try cands.append(p.arena, pin);
                    const pins = try std.mem.join(p.arena, ", ", cands.items);
                    for (std.meta.fieldNames(Anchor)) |f| try cands.append(p.arena, f);
                    const hint = if (suggest(anchor, cands.items)) |sg| try std.fmt.allocPrint(p.arena, " (did you mean '{s}'?)", .{sg}) else "";
                    if (pins.len > 0)
                        return p.fail("{s}: '{s}' has no pin '{s}'{s}; its pins are {s}", .{ op, name, anchor, hint, pins });
                    return p.fail("{s}: unknown anchor '{s}' in '{s}'{s}", .{ op, anchor, text, hint });
                }
                return .{ .point = .{ .ref = .{ .id = name, .anchor = anchor, .off = off } } };
            },
        }
    }

    /// Warn when a block is too small for its title and port names (the
    /// renderer can't move things the model placed, so say so up front).
    /// Estimates: title glyphs ~0.23 wide, port names ~0.15, as the viewer draws them.
    /// Log axes need a positive 2D range; explicit ticks should fall inside it.
    fn checkAxes(p: *LineParser, fields: anytype) Error!void {
        const A = axesRanges(fields);
        inline for (.{ "x", "y" }) |ax| {
            const log = @field(A, ax ++ "log");
            const r = @field(A, ax);
            if (log and A.is3) return p.fail("axes: " ++ ax ++ "log works on 2D axes only", .{});
            if (log and !(r[0] > 0)) return p.fail("axes: " ++ ax ++ "log=true needs a positive " ++ ax ++ " range, like " ++ ax ++ "=0.1..100; got {d}..{d}", .{ r[0], r[1] });
            if (fieldVal(fields, ax ++ "ticks")) |v| if (v == .list) {
                var out: usize = 0;
                for (v.list) |t| if (t.num < r[0] or t.num > r[1]) {
                    out += 1;
                };
                if (out > 0) try p.warn("axes: {d} of the " ++ ax ++ "ticks fall outside " ++ ax ++ "={d}..{d} and won't show", .{ out, r[0], r[1] });
            };
        }
    }

    /// Evaluate a `plot`/`surf` and return its sample fields as a JSON fragment
    /// (",\"polys\":…" or ",\"zgrid\":…"), in the axes' data coordinates.
    /// Mirrors what viewer/js/runtime.js used to sample client-side.
    fn sample(p: *LineParser, fields: anytype) Error![]const u8 {
        const op = p.op.name;
        const ax_name = fieldVal(fields, "axes").?.id;
        const A = (p.scene.names.get(ax_name) orelse return p.fail("{s}: unknown name '{s}'", .{ op, ax_name })).axes orelse
            return p.fail("{s}: '{s}' is not axes", .{ op, ax_name });
        var ep: expr.Parser = .{ .arena = p.arena };
        var buf: std.Io.Writer.Allocating = .init(p.arena);
        const w = &buf.writer;
        const decimals = struct {
            fn of(r: [2]f64) u8 {
                const q = @abs(r[1] - r[0]) / 20000;
                if (!(q > 0)) return 4;
                return @intFromFloat(std.math.clamp(@ceil(-@log10(q)), 0, 8));
            }
        };
        // On a log axis precision follows the low end (0.001 needs more digits than 1000).
        const dx = if (A.xlog) decimals.of(.{ 0, A.x[0] * 10 }) else decimals.of(A.x);
        const dy = if (A.ylog) decimals.of(.{ 0, A.y[0] * 10 }) else decimals.of(A.y);
        const dz = decimals.of(if (A.is3) A.z else A.y);

        if (std.mem.eql(u8, op, "surf")) {
            if (!A.is3) return p.fail("surf: needs 3D axes (give '{s}' a z=range)", .{ax_name});
            const src = fieldVal(fields, "expr").?.str;
            const f = expr.compile(&ep, src, "xy") catch |e| return p.exprFail(e, src, &ep);
            const xr = if (fieldVal(fields, "x")) |v| v.range else A.x;
            const yr = if (fieldVal(fields, "y")) |v| v.range else A.y;
            const res = if (fieldVal(fields, "res")) |v| v.num else 56;
            const n: usize = @intFromFloat(std.math.clamp(@floor(res + 0.5), 4, 160));
            const nf: f64 = @floatFromInt(n);
            w.print(",\"zgrid\":{{\"n\":{d},\"z\":[", .{n}) catch return error.OutOfMemory;
            for (0..n + 1) |j| for (0..n + 1) |i| {
                const x = xr[0] + (xr[1] - xr[0]) * @as(f64, @floatFromInt(i)) / nf;
                const y = yr[0] + (yr[1] - yr[0]) * @as(f64, @floatFromInt(j)) / nf;
                const z = f.eval(.{ .x = x, .y = y });
                if (i + j > 0) w.writeByte(',') catch return error.OutOfMemory;
                writeFixed(w, if (std.math.isFinite(z)) std.math.clamp(z, A.z[0], A.z[1]) else 0, dz) catch return error.OutOfMemory;
            };
            w.writeAll("]}") catch return error.OutOfMemory;
            return buf.written();
        }

        if (std.mem.eql(u8, op, "contour")) {
            if (A.is3) return p.fail("contour: needs 2D axes (for a height field on 3D axes use surf)", .{});
            if (A.xlog or A.ylog) return p.fail("contour: log axes aren't supported; plot log(z) on linear axes instead", .{});
            const src = fieldVal(fields, "expr").?.str;
            const f = expr.compile(&ep, src, "xy") catch |e| return p.exprFail(e, src, &ep);
            const xr = if (fieldVal(fields, "x")) |v| v.range else A.x;
            const yr = if (fieldVal(fields, "y")) |v| v.range else A.y;
            const res = if (fieldVal(fields, "res")) |v| v.num else 64;
            const n: usize = @intFromFloat(std.math.clamp(@floor(res + 0.5), 8, 200));
            const nf: f64 = @floatFromInt(n);
            const zs = try p.arena.alloc(f64, (n + 1) * (n + 1));
            var lo = std.math.inf(f64);
            var hi = -std.math.inf(f64);
            for (0..n + 1) |j| for (0..n + 1) |i| {
                const z = f.eval(.{
                    .x = xr[0] + (xr[1] - xr[0]) * @as(f64, @floatFromInt(i)) / nf,
                    .y = yr[0] + (yr[1] - yr[0]) * @as(f64, @floatFromInt(j)) / nf,
                });
                zs[j * (n + 1) + i] = z;
                if (std.math.isFinite(z)) {
                    lo = @min(lo, z);
                    hi = @max(hi, z);
                }
            };
            if (!(lo < hi)) return p.fail("contour: \"{s}\" is constant or undefined over this range: no levels to draw", .{src});
            const nl: usize = @intFromFloat(std.math.clamp(@round(if (fieldVal(fields, "levels")) |v| v.num else 8), 1, 40));
            w.writeAll(",\"contours\":[") catch return error.OutOfMemory;
            for (0..nl) |k| {
                const lv = lo + (hi - lo) * @as(f64, @floatFromInt(k + 1)) / @as(f64, @floatFromInt(nl + 1));
                if (k > 0) try w.writeByte(',');
                try w.writeAll("{\"z\":");
                try writeFixed(w, lv, 6);
                try w.writeAll(",\"polys\":");
                try contourLevel(p.arena, w, zs, n, xr, yr, lv, .{ dx, dy });
                try w.writeByte('}');
            }
            try w.writeByte(']');
            return buf.written();
        }

        const srcs = fieldVal(fields, "exprs").?.list;
        var fns: [3]expr.Expr = undefined;
        for (srcs, 0..) |v, k| fns[k] = expr.compile(&ep, v.str, "xt") catch |e| return p.exprFail(e, v.str, &ep);
        var polys: usize = 0;
        var cur: usize = 0; // points in the current polyline
        w.writeAll(",\"polys\":[") catch return error.OutOfMemory;
        const S = struct {
            fn brk(wr: *Writer, n_polys: *usize, n_cur: *usize) !void {
                if (n_cur.* > 0) {
                    try wr.writeAll("]");
                    n_polys.* += 1;
                }
                n_cur.* = 0;
            }
            fn push(wr: *Writer, n_polys: usize, n_cur: *usize, v: Vec3, d: [3]u8) !void {
                if (n_cur.* == 0) try wr.writeAll(if (n_polys > 0) ",[" else "[");
                if (n_cur.* > 0) try wr.writeAll(",");
                try wr.writeAll("[");
                try writeFixed(wr, v[0], d[0]);
                try wr.writeAll(",");
                try writeFixed(wr, v[1], d[1]);
                try wr.writeAll(",");
                try writeFixed(wr, v[2], d[2]);
                try wr.writeAll("]");
                n_cur.* += 1;
            }
        };
        const d3: [3]u8 = .{ dx, dy, dz };
        // A one-point polyline draws nothing: drop it (the writer can't unwrite,
        // so points are buffered until a second one arrives).
        var pending: ?Vec3 = null;
        const step = struct {
            fn ok(wr: *Writer, n_polys: *usize, n_cur: *usize, pend: *?Vec3, v: Vec3, d: [3]u8) !void {
                if (n_cur.* == 0) {
                    if (pend.*) |first| {
                        try S.push(wr, n_polys.*, n_cur, first, d);
                        pend.* = null;
                    } else {
                        pend.* = v;
                        return;
                    }
                }
                try S.push(wr, n_polys.*, n_cur, v, d);
            }
            fn brk(wr: *Writer, n_polys: *usize, n_cur: *usize, pend: *?Vec3) !void {
                pend.* = null;
                try S.brk(wr, n_polys, n_cur);
            }
        };
        if (srcs.len == 1) {
            const xr = if (fieldVal(fields, "x")) |v| v.range else A.x;
            // Work in screen units: log10 on a log axis (so sampling is even across decades).
            const toS = struct {
                fn f(y: f64, log: bool) f64 {
                    return if (!log) y else if (y > 0) @log10(y) else std.math.nan(f64);
                }
            }.f;
            const ylo = toS(A.y[0], A.ylog);
            const yhi = toS(A.y[1], A.ylog);
            const yspan = yhi - ylo;
            if (A.xlog and !(xr[0] > 0)) return p.fail("plot: x={d}..{d} must be positive on a log x axis", .{ xr[0], xr[1] });
            const N = 600;
            const stairs = if (fieldVal(fields, "step")) |v| v.boolean else false;
            var prev: ?f64 = null;
            var prev_x: f64 = xr[0];
            for (0..N + 1) |i| {
                const u = @as(f64, @floatFromInt(i)) / N;
                const x = if (A.xlog) xr[0] * std.math.pow(f64, xr[1] / xr[0], u) else xr[0] + (xr[1] - xr[0]) * u;
                const y = fns[0].eval(.{ .x = x, .t = x });
                const yv = toS(y, A.ylog);
                const in = std.math.isFinite(yv) and (A.is3 or (yv >= ylo - 0.05 * yspan and yv <= yhi + 0.05 * yspan));
                if (stairs and in and prev != null and y != prev.?) {
                    // A staircase: find where the value jumps (bisection), then run flat to it and rise.
                    var a = prev_x;
                    var b = x;
                    for (0..40) |_| {
                        const m = (a + b) / 2;
                        const ym = fns[0].eval(.{ .x = m, .t = m });
                        if (@abs(ym - prev.?) <= @abs(ym - y)) a = m else b = m;
                    }
                    step.ok(w, &polys, &cur, &pending, .{ b, prev.?, 0 }, d3) catch return error.OutOfMemory;
                    step.ok(w, &polys, &cur, &pending, .{ b, y, 0 }, d3) catch return error.OutOfMemory;
                    if (x - b < 1e-9 * @abs(xr[1] - xr[0])) { // the riser is at this sample: done with it
                        prev_x = x;
                        prev = y;
                        continue;
                    }
                } else if (!in or (prev != null and !A.is3 and @abs(yv - toS(prev.?, A.ylog)) > yspan))
                    step.brk(w, &polys, &cur, &pending) catch return error.OutOfMemory;
                prev_x = x;
                if (stairs and in and prev != null and y == prev.? and i != N) continue; // flat: only the corners matter
                if (in) step.ok(w, &polys, &cur, &pending, .{ x, y, 0 }, d3) catch return error.OutOfMemory;
                prev = if (in) y else null;
            }
        } else {
            const tr = if (fieldVal(fields, "t")) |v| v.range else [2]f64{ 0, 2 * std.math.pi };
            const N = 800;
            for (0..N + 1) |i| {
                const t = tr[0] + (tr[1] - tr[0]) * @as(f64, @floatFromInt(i)) / N;
                var v: Vec3 = .{ 0, 0, 0 };
                var finite = true;
                for (srcs, 0..) |_, k| {
                    v[k] = fns[k].eval(.{ .x = t, .t = t });
                    finite = finite and std.math.isFinite(v[k]);
                }
                if (!finite) {
                    step.brk(w, &polys, &cur, &pending) catch return error.OutOfMemory;
                    continue;
                }
                step.ok(w, &polys, &cur, &pending, v, d3) catch return error.OutOfMemory;
            }
        }
        step.brk(w, &polys, &cur, &pending) catch return error.OutOfMemory;
        w.writeAll("]") catch return error.OutOfMemory;
        if (polys == 0) {
            if (srcs.len == 1) return p.fail("plot: nothing to draw: \"{s}\" has no values inside the axes' y range {d}..{d} for x in this range", .{ srcs[0].str, A.y[0], A.y[1] });
            return p.fail("plot: nothing to draw: the curve has no finite points for t in this range", .{});
        }
        return buf.written();
    }

    const Derived = struct { json: []const u8, ports: []const u8 };
    const PinList = struct {
        arena: Allocator,
        list: std.ArrayList(u8) = .empty,
        fn add(l: *PinList, comptime fmt: []const u8, args: anytype) Allocator.Error!void {
            if (l.list.items.len > 0) try l.list.append(l.arena, ',');
            try l.list.print(l.arena, fmt, args);
        }
        fn has(l: *PinList, name: []const u8) bool {
            var it = std.mem.splitScalar(u8, l.list.items, ',');
            while (it.next()) |x| if (std.mem.eql(u8, x, name)) return true;
            return false;
        }
    };

    /// The data strings of nn, grid, bits, bars, hist, scatter and errbar,
    /// parsed into JSON arrays (renderers never parse them), and their pins.
    fn derive(p: *LineParser, fields: anytype) Error!?Derived {
        const op = p.op.name;
        var buf: std.Io.Writer.Allocating = .init(p.arena);
        const w = &buf.writer;
        var pins: PinList = .{ .arena = p.arena };
        if (std.mem.eql(u8, op, "nn")) {
            try p.deriveNn(fields, w, &pins);
        } else if (std.mem.eql(u8, op, "grid")) {
            try p.deriveGrid(fields, w, &pins);
        } else if (std.mem.eql(u8, op, "bits")) {
            try p.deriveBits(fields, w, &pins);
        } else if (std.mem.eql(u8, op, "bars")) {
            try p.deriveBars(fields, w, &pins);
        } else if (std.mem.eql(u8, op, "hist")) {
            try p.deriveHist(fields, w, &pins);
        } else if (std.mem.eql(u8, op, "scatter")) {
            const A = try p.axesOf(fields);
            try w.writeAll(",\"pts\":[");
            var it = std.mem.tokenizeAny(u8, fieldStr(fields, "data").?, " \t\r\n;");
            var n: usize = 0;
            while (it.next()) |item| : (n += 1) {
                const v = parseVec(item) orelse return p.fail("scatter: expected points like \"1,2 3,4\", got '{s}'", .{item});
                if (std.mem.count(u8, item, ",") == 2 and !A.is3) return p.fail("scatter: '{s}' has a z, but the axes are 2D", .{item});
                if (n > 0) try w.writeByte(',');
                try writeVec(w, v);
                try pins.add("P{d}", .{n + 1});
            }
            if (n == 0) return p.fail("scatter: no points; write them as \"x,y x,y …\"", .{});
            try w.writeByte(']');
        } else if (std.mem.eql(u8, op, "errbar")) {
            try w.writeAll(",\"bars\":[");
            var it = std.mem.tokenizeAny(u8, fieldStr(fields, "data").?, " \t\r\n;");
            var n: usize = 0;
            while (it.next()) |item| : (n += 1) {
                const bad = "errbar: expected \"x,lo..hi\" or \"x,y,lo..hi\" items (y,lo..hi with horiz=true), got '{s}'";
                const last = std.mem.lastIndexOfScalar(u8, item, ',') orelse return p.fail(bad, .{item});
                const dots = std.mem.indexOf(u8, item[last + 1 ..], "..") orelse return p.fail(bad, .{item});
                const lo = parseNum(item[last + 1 ..][0..dots]) orelse return p.fail(bad, .{item});
                const hi = parseNum(item[last + 1 ..][dots + 2 ..]) orelse return p.fail(bad, .{item});
                if (lo > hi) return p.fail("errbar: interval '{s}' must go from low to high", .{item[last + 1 ..]});
                var head = std.mem.splitScalar(u8, item[0..last], ',');
                const x = parseNum(head.next().?) orelse return p.fail(bad, .{item});
                const y: ?f64 = if (head.next()) |t| parseNum(t) orelse return p.fail(bad, .{item}) else null;
                if (head.next() != null) return p.fail(bad, .{item});
                if (n > 0) try w.writeByte(',');
                try w.writeByte('[');
                try writeNum(w, x);
                try w.writeByte(',');
                if (y) |v| try writeNum(w, v) else try w.writeAll("null");
                try w.writeByte(',');
                try writeNum(w, lo);
                try w.writeByte(',');
                try writeNum(w, hi);
                try w.writeByte(']');
                try pins.add("E{d}", .{n + 1});
            }
            if (n == 0) return p.fail("errbar: no intervals; write them as \"x,lo..hi …\"", .{});
            try w.writeByte(']');
        } else if (std.mem.eql(u8, op, "band")) {
            if (fieldVal(fields, "x") == null and fieldVal(fields, "y") == null)
                return p.fail("band: give x=lo..hi (a vertical strip), y=lo..hi (a horizontal one) or both", .{});
            return null;
        } else return null;
        return .{ .json = buf.written(), .ports = pins.list.items };
    }

    fn axesOf(p: *LineParser, fields: anytype) Error!AxesRanges {
        const name = fieldVal(fields, "axes").?.id;
        const e = p.scene.names.get(name) orelse return p.fail("{s}: unknown name '{s}'", .{ p.op.name, name });
        return e.axes orelse p.fail("{s}: '{s}' is not axes", .{ p.op.name, name });
    }

    fn deriveNn(p: *LineParser, fields: anytype, w: *Writer, pins: *PinList) Error!void {
        var counts: std.ArrayList(u32) = .empty;
        var it = std.mem.tokenizeAny(u8, fieldStr(fields, "layers").?, ", \t");
        while (it.next()) |t| {
            const n = std.fmt.parseInt(u32, t, 10) catch return p.fail("nn: layer sizes are whole numbers, like \"4,6,6,3\"; got '{s}'", .{t});
            if (n < 1 or n > 1_000_000) return p.fail("nn: layer size {d} is out of range (1..1000000)", .{n});
            try counts.append(p.arena, n);
        }
        if (counts.items.len == 0 or counts.items.len > 16) return p.fail("nn: give 1 to 16 layer sizes, like \"4,6,6,3\"", .{});
        const max_shown: u32 = @intFromFloat(std.math.clamp(@round(if (fieldVal(fields, "max")) |v| v.num else 8), 3, 30));
        const pairs = if (fieldVal(fields, "pairs")) |v| v.boolean else false;
        const feedback = if (fieldVal(fields, "feedback")) |v| v.boolean else false;
        const dense = !std.mem.eql(u8, fieldStr(fields, "edges") orelse "dense", "none");
        if (fieldStr(fields, "labels")) |l| {
            const n = std.mem.count(u8, l, ",") + 1;
            if (n != counts.items.len) try p.warn("nn: {d} labels for {d} layers", .{ n, counts.items.len });
        }
        try w.writeAll(",\"sizes\":[");
        for (counts.items, 0..) |n, i| {
            if (i > 0) try w.writeByte(',');
            try w.print("{d}", .{n});
        }
        try w.writeAll("],\"shown\":[");
        for (counts.items, 0..) |n, i| {
            if (i > 0) try w.writeByte(',');
            try w.writeByte('[');
            // A big layer shows its first and last few neurons around an ellipsis.
            const c: u32 = @max(3, (max_shown * 6 + 5) / 10);
            const top = if (n <= max_shown) n else (c + 1) / 2;
            const bottom = if (n <= max_shown) 0 else c - top;
            var first = true;
            var j: u32 = 1;
            while (j <= n) : (j += 1) {
                if (j > top and j <= n - bottom) {
                    j = n - bottom;
                    continue;
                }
                if (!first) try w.writeByte(',');
                first = false;
                try w.print("{d}", .{j});
                try pins.add("L{d}_{d}", .{ i, j });
                if (pairs) try pins.add("L{d}_{d}e", .{ i, j });
            }
            try w.writeByte(']');
            try pins.add("L{d},L{d}_top,L{d}_bottom", .{ i, i, i });
            if (i > 0 and dense) try pins.add("E{d}", .{i});
            if (i > 0 and feedback) try pins.add("F{d}", .{i});
        }
        try w.writeByte(']');
    }

    fn deriveGrid(p: *LineParser, fields: anytype, w: *Writer, pins: *PinList) Error!void {
        const d = fieldVal(fields, "dims").?.vec;
        if (d[0] != @round(d[0]) or d[1] != @round(d[1]) or d[0] < 1 or d[1] < 1 or d[0] > 256 or d[1] > 256)
            return p.fail("grid: dims are whole numbers rows,cols (1..256 each), got {d},{d}", .{ d[0], d[1] });
        const rows: usize = @intFromFloat(d[0]);
        const cols: usize = @intFromFloat(d[1]);
        try w.writeAll(",\"cells\":[");
        var it = std.mem.tokenizeAny(u8, fieldStr(fields, "values").?, " \t\r\n,;");
        var n: usize = 0;
        while (it.next()) |t| : (n += 1) {
            const v = parseNum(t) orelse return p.fail("grid: '{s}' is not a number (values are separated by spaces, commas or new lines)", .{t});
            if (n > 0) try w.writeByte(',');
            if (n < rows * cols) try writeNum(w, v);
        }
        if (n != rows * cols) return p.fail("grid: {d}x{d} needs {d} values, got {d}", .{ rows, cols, rows * cols, n });
        try w.writeByte(']');
        for (1..rows + 1) |r| try pins.add("r{d}", .{r});
        for (1..cols + 1) |c| try pins.add("c{d}", .{c});
        if (rows * cols <= 4096) for (1..rows + 1) |r| for (1..cols + 1) |c| try pins.add("r{d}c{d}", .{ r, c });
    }

    fn deriveBits(p: *LineParser, fields: anytype, w: *Writer, pins: *PinList) Error!void {
        const pattern = fieldStr(fields, "pattern").?;
        var widths: std.ArrayList(usize) = .empty;
        var total: usize = 0;
        var it = std.mem.splitScalar(u8, pattern, '|');
        while (it.next()) |g| {
            var n: usize = 0;
            for (g) |ch| if (ch != ' ') {
                n += 1;
            };
            if (n == 0) return p.fail("bits: empty field in \"{s}\" (fields are separated by |, one character per bit)", .{pattern});
            try widths.append(p.arena, n);
            total += n;
        }
        if (total > 64) return p.fail("bits: {d} bits; at most 64", .{total});
        if (fieldStr(fields, "values")) |vals| {
            var vi = std.mem.splitScalar(u8, vals, '|');
            var k: usize = 0;
            while (vi.next()) |g| : (k += 1) {
                var n: usize = 0;
                for (g) |ch| {
                    if (ch == ' ') continue;
                    if (std.mem.indexOfScalar(u8, "01x?-", ch) == null)
                        return p.fail("bits: values are 0, 1 or x per bit, got '{c}' in \"{s}\"", .{ ch, vals });
                    n += 1;
                }
                if (k >= widths.items.len or n != widths.items[k])
                    return p.fail("bits: values \"{s}\" must match the fields of \"{s}\" (same | groups, one digit per bit)", .{ vals, pattern });
            }
            if (k != widths.items.len) return p.fail("bits: values \"{s}\" must match the fields of \"{s}\" (same | groups, one digit per bit)", .{ vals, pattern });
        }
        try w.writeAll(",\"widths\":[");
        for (widths.items, 0..) |n, i| {
            if (i > 0) try w.writeByte(',');
            try w.print("{d}", .{n});
        }
        try w.writeByte(']');
        for (0..total) |b| try pins.add("b{d}", .{b});
        for (1..widths.items.len + 1) |f| try pins.add("F{d}", .{f});
        if (fieldStr(fields, "labels")) |labels| {
            const n = std.mem.count(u8, labels, ",") + 1;
            if (n != widths.items.len) return p.fail("bits: {d} labels for {d} fields", .{ n, widths.items.len });
            var li = std.mem.splitScalar(u8, labels, ',');
            while (li.next()) |l| {
                const pin = try wavePin(p.arena, l);
                if (pin.len == 0 or std.meta.stringToEnum(Anchor, pin) != null or pins.has(pin)) continue;
                try pins.add("{s}", .{pin});
            }
        }
    }

    fn deriveBars(p: *LineParser, fields: anytype, w: *Writer, pins: *PinList) Error!void {
        try w.writeAll(",\"bars\":[");
        var it = std.mem.tokenizeAny(u8, fieldStr(fields, "data").?, " \t\r\n;");
        var n: usize = 0;
        var depth: usize = 0;
        while (it.next()) |item| : (n += 1) {
            const comma = std.mem.indexOfScalar(u8, item, ',');
            const x: f64 = if (comma) |c| parseNum(item[0..c]) orelse
                return p.fail("bars: bad x in '{s}' (items are y, x,y or x,y1/y2/y3 for stacks)", .{item}) else @floatFromInt(n + 1);
            if (n > 0) try w.writeByte(',');
            try w.writeByte('[');
            try writeNum(w, x);
            try w.writeAll(",[");
            var ys = std.mem.splitScalar(u8, item[if (comma) |c| c + 1 else 0..], '/');
            var k: usize = 0;
            while (ys.next()) |t| : (k += 1) {
                const y = parseNum(t) orelse return p.fail("bars: bad value '{s}' in '{s}' (items are y, x,y or x,y1/y2/y3 for stacks)", .{ t, item });
                if (k > 0) try w.writeByte(',');
                try writeNum(w, y);
            }
            depth = @max(depth, k);
            try w.writeAll("]]");
            try pins.add("B{d}", .{n + 1});
        }
        if (n == 0) return p.fail("bars: no bars; write them as \"y y y\" or \"x,y x,y\"", .{});
        try w.writeByte(']');
        if (fieldStr(fields, "labels")) |l| {
            const k = std.mem.count(u8, l, ",") + 1;
            if (k != n) return p.fail("bars: {d} labels for {d} bars", .{ k, n });
        }
        if (fieldStr(fields, "series")) |l| {
            const k = std.mem.count(u8, l, ",") + 1;
            if (k != depth) try p.warn("bars: {d} series names for stacks of {d}", .{ k, depth });
        }
    }

    fn deriveHist(p: *LineParser, fields: anytype, w: *Writer, pins: *PinList) Error!void {
        var vals: std.ArrayList(f64) = .empty;
        var it = std.mem.tokenizeAny(u8, fieldStr(fields, "data").?, " \t\r\n,;");
        while (it.next()) |t| try vals.append(p.arena, parseNum(t) orelse return p.fail("hist: '{s}' is not a number", .{t}));
        if (vals.items.len == 0) return p.fail("hist: no values", .{});
        var lo = std.math.inf(f64);
        var hi = -std.math.inf(f64);
        for (vals.items) |v| {
            lo = @min(lo, v);
            hi = @max(hi, v);
        }
        if (fieldVal(fields, "range")) |r| {
            lo = r.range[0];
            hi = r.range[1];
        } else if (lo == hi) {
            lo -= 0.5;
            hi += 0.5;
        }
        const nf: f64 = @floatFromInt(vals.items.len);
        const auto_bins = @ceil(@log2(nf) + 1); // Sturges
        const bins: usize = @intFromFloat(std.math.clamp(@round(if (fieldVal(fields, "bins")) |v| v.num else auto_bins), 1, 200));
        const counts = try p.arena.alloc(f64, bins);
        @memset(counts, 0);
        const bw = (hi - lo) / @as(f64, @floatFromInt(bins));
        var outside: usize = 0;
        for (vals.items) |v| {
            if (v < lo or v > hi) {
                outside += 1;
                continue;
            }
            const k: usize = @min(bins - 1, @as(usize, @intFromFloat(@floor((v - lo) / bw))));
            counts[k] += 1;
        }
        if (outside > 0) try p.warn("hist: {d} of {d} values fall outside the range {d}..{d}", .{ outside, vals.items.len, lo, hi });
        const density = if (fieldVal(fields, "density")) |v| v.boolean else false;
        try w.writeAll(",\"bars\":[");
        for (counts, 0..) |c, k| {
            if (k > 0) try w.writeByte(',');
            try w.writeByte('[');
            try writeFixed(w, lo + (@as(f64, @floatFromInt(k)) + 0.5) * bw, 8);
            try w.writeAll(",[");
            try writeFixed(w, if (density) c / (nf * bw) else c, 6);
            try w.writeAll("]]");
            try pins.add("B{d}", .{k + 1});
        }
        try w.writeAll("],\"bw\":");
        try writeFixed(w, bw, 8);
    }

    fn exprFail(p: *LineParser, e: expr.Error, src: []const u8, ep: *expr.Parser) Error {
        return switch (e) {
            error.OutOfMemory => error.OutOfMemory,
            error.Bad => p.fail("{s}: \"{s}\": {s}", .{ p.op.name, src, ep.msg }),
        };
    }

    fn lintBlock(p: *LineParser, fields: anytype) Error!void {
        var w: f64 = 0;
        var h: f64 = 0;
        for (fields) |f| if (std.mem.eql(u8, f.name, "size")) {
            w = f.val.vec[0];
            h = f.val.vec[1];
        };
        const longest = struct {
            fn names(list: ?[]const u8) struct { len: usize, n: usize } {
                var it = std.mem.splitScalar(u8, list orelse "", ',');
                var m: usize = 0;
                var n: usize = 0;
                while (it.next()) |x| {
                    const t = std.mem.trim(u8, x, " ");
                    if (t.len == 0) continue;
                    m = @max(m, t.len);
                    n += 1;
                }
                return .{ .len = m, .n = n };
            }
        };
        const in = longest.names(fieldStr(fields, "in"));
        const out = longest.names(fieldStr(fields, "out"));
        const top = longest.names(fieldStr(fields, "top"));
        const bottom = longest.names(fieldStr(fields, "bottom"));
        var title_len: usize = 0;
        var title_lines: usize = 0;
        var tl = std.mem.splitScalar(u8, fieldStr(fields, "text") orelse "", '\n');
        while (tl.next()) |l| {
            title_len = @max(title_len, l.len);
            if (l.len > 0) title_lines += 1;
        }
        const f = struct {
            fn of(x: usize) f64 {
                return @floatFromInt(x);
            }
        }.of;
        const sides = in.n + out.n > 0;
        var need_w = f(title_len) * 0.23 + 0.4;
        if (sides) need_w = @max(need_w, f(in.len + out.len) * 0.15 + 0.6);
        need_w = @max(need_w, f(@max(top.n, bottom.n)) * 0.6);
        var need_h = f(title_lines) * 0.5 + 0.3;
        if (sides) need_h = @max(need_h, f(title_lines) * 0.5 + 0.2 + f(@max(in.n, out.n)) * 0.42);
        if (top.n > 0) need_h += 0.3;
        if (bottom.n > 0) need_h += 0.3;
        if (w + 0.05 < need_w or h + 0.05 < need_h)
            try p.warn("block: {d:.1}x{d:.1} is too small for its title and ports (they will overlap); try at least {d:.1}x{d:.1}", .{ w, h, @max(w, need_w), @max(h, need_h) });
    }

    /// Warn when text, a panel, a block or a wave placed at board coordinates
    /// would run off the board (x −8…8, y −4.5…4.5). Sizes are estimates of
    /// what the viewer draws (proportional glyphs ~0.45 em, monospace ~0.6 em),
    /// checked against the renderer in Chromium.
    fn lintBounds(p: *LineParser, fields: anytype) Error!?[4]f64 {
        const op = p.op.name;
        const at = for (fields) |f| {
            if (std.mem.eql(u8, f.name, "at")) switch (f.val) {
                .point => |pt| switch (pt) {
                    .abs => |v| break v,
                    else => return null,
                },
                else => return null,
            };
        } else return null;
        const num = struct {
            fn get(fs: anytype, name: []const u8) ?f64 {
                for (fs) |f| if (std.mem.eql(u8, f.name, name)) return switch (f.val) {
                    .num => |n| n,
                    else => null,
                };
                return null;
            }
            fn of(x: usize) f64 {
                return @floatFromInt(x);
            }
            /// Widest line in characters (UTF-8 code points) and the number of lines.
            fn lines(text: []const u8) struct { w: usize, n: usize } {
                var it = std.mem.splitScalar(u8, text, '\n');
                var w: usize = 0;
                var n: usize = 0;
                while (it.next()) |l| {
                    w = @max(w, std.unicode.utf8CountCodepoints(l) catch l.len);
                    n += 1;
                }
                return .{ .w = w, .n = n };
            }
        };
        const f = num.of;
        var w: f64 = 0;
        var h: f64 = 0;
        var x0: f64 = undefined; // left edge
        if (std.mem.eql(u8, op, "text")) {
            const s = num.get(fields, "size") orelse 0.45;
            const l = num.lines(fieldStr(fields, "text") orelse "");
            w = f(l.w) * 0.45 * s + 0.4 * s;
            h = f(l.n) * 1.2 * s + 0.4 * s;
            const al = fieldStr(fields, "align") orelse "center";
            x0 = if (al[0] == 'l') at[0] else if (al[0] == 'r') at[0] - w else at[0] - w / 2;
        } else if (std.mem.eql(u8, op, "node")) {
            const s = num.get(fields, "size") orelse 0.45;
            const l = num.lines(fieldStr(fields, "text") orelse "");
            const tw = f(l.w) * 0.45 * s + 0.4 * s;
            const th = f(l.n) * 1.2 * s + 0.4 * s;
            const shape = fieldStr(fields, "shape") orelse (if (l.w <= 3 and l.n == 1) "circle" else "box");
            if (std.mem.eql(u8, shape, "circle")) {
                w = @max(tw, th) + 0.7 * s;
                h = w;
            } else if (std.mem.eql(u8, shape, "none")) {
                w = tw;
                h = th;
            } else {
                w = tw + 0.7 * s;
                h = th + 0.5 * s;
            }
            x0 = at[0] - w / 2;
        } else if (std.mem.eql(u8, op, "code")) {
            const s = num.get(fields, "size") orelse 0.48;
            const l = num.lines(fieldStr(fields, "text") orelse "");
            const gutter: usize = std.math.log10_int(@max(l.n, 1)) + 3; // digits + 2
            w = f(l.w + gutter + 2) * 0.43 * s;
            h = (f(l.n) + 0.86 + (if (fieldStr(fields, "title") != null) @as(f64, 1.2) else 0)) * s;
            x0 = at[0] - w / 2;
        } else if (std.mem.eql(u8, op, "table")) {
            const s = num.get(fields, "size") orelse 0.65;
            var cols: [32]usize = @splat(0);
            var ncol: usize = 0;
            var nrow: usize = 0;
            for (fields) |fd| if (std.mem.eql(u8, fd.name, "rows")) for (fd.val.list) |row| {
                nrow += 1;
                var it = std.mem.splitScalar(u8, row.str, '|');
                var c: usize = 0;
                while (it.next()) |cell| : (c += 1) if (c < cols.len) {
                    const t = std.mem.trim(u8, cell, " ");
                    cols[c] = @max(cols[c], std.unicode.utf8CountCodepoints(t) catch t.len);
                    ncol = @max(ncol, c + 1);
                };
            };
            for (cols[0..ncol]) |cw| w += (f(cw) * 0.30 + 0.875) * s;
            h = f(nrow) * s;
            x0 = at[0] - w / 2;
        } else if (std.mem.eql(u8, op, "block")) {
            for (fields) |fd| if (std.mem.eql(u8, fd.name, "size")) {
                w = fd.val.vec[0];
                h = fd.val.vec[1];
            };
            x0 = at[0] - w / 2;
        } else if (std.mem.eql(u8, op, "nn") or std.mem.eql(u8, op, "grid") or std.mem.eql(u8, op, "bits")) {
            const sz = diagramSize(op, fields) orelse return null;
            w = sz[0];
            h = sz[1];
            x0 = at[0] - w / 2;
        } else if (std.mem.eql(u8, op, "wave")) {
            const step = num.get(fields, "step") orelse 0.5;
            const rh = num.get(fields, "size") orelse 0.45;
            var steps: usize = 0;
            var name_len: usize = 0;
            var nrow: usize = 0;
            for (fields) |fd| if (std.mem.eql(u8, fd.name, "rows")) for (fd.val.list) |row| {
                nrow += 1;
                const head = std.mem.sliceTo(row.str, '|');
                const colon = std.mem.lastIndexOfScalar(u8, head, ':');
                const pat = if (colon) |c| head[c + 1 ..] else head;
                var k: usize = 0;
                for (pat) |c| if (c != ' ' and c != '\t') {
                    k += 1;
                };
                steps = @max(steps, k);
                if (colon) |c| name_len = @max(name_len, std.mem.trim(u8, head[0..c], " ").len);
            };
            w = f(steps) * step + f(name_len) * @max(0.32, rh * 0.62) * 0.55 + 0.3;
            h = f(nrow) * rh * 1.9;
            x0 = at[0] - w / 2;
        } else return null;
        const tol = 0.05;
        const bx = p.scene.board[0] / 2;
        const by = p.scene.board[1] / 2;
        const x1 = x0 + w;
        const y0 = at[1] - h / 2;
        const y1 = at[1] + h / 2;
        var edges: [4][]const u8 = undefined;
        var ne: usize = 0;
        if (x0 < -bx - tol) {
            edges[ne] = "left";
            ne += 1;
        }
        if (x1 > bx + tol) {
            edges[ne] = "right";
            ne += 1;
        }
        if (y1 > by + tol) {
            edges[ne] = "top";
            ne += 1;
        }
        if (y0 < -by - tol) {
            edges[ne] = "bottom";
            ne += 1;
        }
        const box: [4]f64 = .{ x0, y0, x1, y1 };
        if (ne == 0) return box;
        const which = try std.mem.join(p.arena, " and ", edges[0..ne]);
        const fix = if (w > 2 * bx or h > 2 * by) "make it smaller (size=), split it, or enlarge the board (board W,H)" else "move it inward, shorten it or reduce size=";
        try p.warn("{s}: about {d:.1}x{d:.1}, spanning x {d:.1}..{d:.1}, y {d:.1}..{d:.1}: it runs off the {s} of the board (x {d}..{d}, y {d}..{d}); {s}", .{ op, w, h, x0, x1, y0, y1, which, -bx, bx, -by, by, fix });
        return box;
    }

    /// Warn when a new box lands on boxes already on the board (one warning per line,
    /// naming up to two of them). Small touches are ignored.
    fn lintOverlap(p: *LineParser, name: []const u8, b: [4]f64) Error!void {
        var hits: std.ArrayList(u8) = .empty;
        var n: usize = 0;
        var ix: [4]f64 = undefined;
        for (p.scene.names.keys(), p.scene.names.values()) |k, e| {
            const o = e.box orelse continue;
            if (std.mem.eql(u8, k, name)) continue;
            const x0 = @max(b[0], o[0]);
            const y0 = @max(b[1], o[1]);
            const x1 = @min(b[2], o[2]);
            const y1 = @min(b[3], o[3]);
            if (x1 <= x0 or y1 <= y0) continue;
            if (x1 - x0 < 0.1 or y1 - y0 < 0.1) continue; // touching edges is fine
            if (n == 0) ix = .{ x0, y0, x1, y1 };
            n += 1;
            if (n <= 2) {
                if (hits.items.len > 0) try hits.appendSlice(p.arena, ", ");
                try hits.print(p.arena, "'{s}' ({s})", .{ if (k[0] == '_') "unnamed" else k, e.op });
            }
        }
        if (n == 0) return;
        const more = if (n > 2) try std.fmt.allocPrint(p.arena, " and {d} more", .{n - 2}) else "";
        try p.warn("{s}: overlaps {s}{s} around x {d:.1}..{d:.1}, y {d:.1}..{d:.1}; move one of them", .{ p.op.name, hits.items, more, ix[0], ix[2], ix[1], ix[3] });
    }

    fn lookup(p: *LineParser, name: []const u8) Error!Entity {
        if (p.scene.names.get(name)) |e| return e;
        if (suggest(name, p.scene.names.keys())) |s|
            return p.fail("{s}: unknown name '{s}' (did you mean '{s}'?)", .{ p.op.name, name, s });
        return p.fail("{s}: unknown name '{s}'", .{ p.op.name, name });
    }
};

/// A wave signal's pin name: drop a trailing bus range ([7:0]), turn every run
/// of other characters into '_', trim '_' at the ends, and prefix a leading
/// digit with 's'. Empty means no pin. Mirrors wavePin in viewer/js/panels.js.
pub fn wavePin(a: Allocator, name: []const u8) Allocator.Error![]const u8 {
    var n = std.mem.trim(u8, name, " \t");
    if (n.len > 0 and n[n.len - 1] == ']') if (std.mem.lastIndexOfScalar(u8, n, '[')) |b| {
        n = std.mem.trimEnd(u8, n[0..b], " \t");
    };
    var out: std.ArrayList(u8) = .empty;
    var gap = false;
    for (n) |c| {
        if (std.ascii.isAlphanumeric(c) or c == '_') {
            if (gap and out.items.len > 0) try out.append(a, '_');
            gap = false;
            try out.append(a, c);
        } else gap = true;
    }
    var r = std.mem.trim(u8, out.items, "_");
    if (r.len > 0 and std.ascii.isDigit(r[0])) r = try std.mem.concat(a, u8, &.{ "s", r });
    return r;
}

/// Stands in for a `<<TAG` block among the positional tokens (compared by address).
const heredoc_marker: []const u8 = "<<heredoc>>";

/// One contour level of a grid of samples (marching squares), written as a JSON
/// array of polylines in data coordinates. Segments are chained through the
/// cell edges they share, so each curve comes out as one polyline.
fn contourLevel(arena: Allocator, w: *Writer, zs: []const f64, n: usize, xr: [2]f64, yr: [2]f64, lv: f64, dec: [2]u8) (Writer.Error || Allocator.Error)!void {
    const H = n * (n + 1); // horizontal edges: (i,j)-(i+1,j), id j*n+i
    const E = H + (n + 1) * n; // vertical edges: (i,j)-(i,j+1), id H + j*(n+1)+i
    const none = std.math.maxInt(u32);
    const Seg = struct { a: u32, b: u32 };
    var segs: std.ArrayList(Seg) = .empty;
    const z = struct {
        fn at(v: []const f64, nn: usize, i: usize, j: usize) f64 {
            return v[j * (nn + 1) + i];
        }
    }.at;
    for (0..n) |j| for (0..n) |i| {
        const bl = z(zs, n, i, j);
        const br = z(zs, n, i + 1, j);
        const tr = z(zs, n, i + 1, j + 1);
        const tl = z(zs, n, i, j + 1);
        if (!(std.math.isFinite(bl) and std.math.isFinite(br) and std.math.isFinite(tr) and std.math.isFinite(tl))) continue;
        const bottom: u32 = @intCast(j * n + i);
        const top: u32 = @intCast((j + 1) * n + i);
        const left: u32 = @intCast(H + j * (n + 1) + i);
        const right: u32 = left + 1;
        var c: u4 = 0;
        if (bl >= lv) c |= 1;
        if (br >= lv) c |= 2;
        if (tr >= lv) c |= 4;
        if (tl >= lv) c |= 8;
        const centre_up = (bl + br + tr + tl) / 4 >= lv;
        const pairs: []const [2]u32 = switch (c) {
            0, 15 => &.{},
            1, 14 => &.{.{ left, bottom }},
            2, 13 => &.{.{ bottom, right }},
            3, 12 => &.{.{ left, right }},
            4, 11 => &.{.{ right, top }},
            6, 9 => &.{.{ bottom, top }},
            7, 8 => &.{.{ left, top }},
            5 => if (centre_up) &.{ .{ bottom, right }, .{ left, top } } else &.{ .{ left, bottom }, .{ right, top } },
            10 => if (centre_up) &.{ .{ left, bottom }, .{ right, top } } else &.{ .{ bottom, right }, .{ left, top } },
        };
        for (pairs) |pr| try segs.append(arena, .{ .a = pr[0], .b = pr[1] });
    };
    // Each edge is shared by at most two segments.
    const link = try arena.alloc([2]u32, E);
    @memset(link, .{ none, none });
    for (segs.items, 0..) |sg, k| for ([_]u32{ sg.a, sg.b }) |e| {
        if (link[e][0] == none) link[e][0] = @intCast(k) else link[e][1] = @intCast(k);
    };
    const seen = try arena.alloc(bool, segs.items.len);
    @memset(seen, false);
    const pt = struct {
        fn of(v: []const f64, nn: usize, hh: usize, x: [2]f64, y: [2]f64, l: f64, e: u32) [2]f64 {
            var i_a: usize = undefined;
            var j_a: usize = undefined;
            var i_b: usize = undefined;
            var j_b: usize = undefined;
            if (e < hh) {
                j_a = e / nn;
                i_a = e % nn;
                i_b = i_a + 1;
                j_b = j_a;
            } else {
                const k = e - hh;
                j_a = k / (nn + 1);
                i_a = k % (nn + 1);
                i_b = i_a;
                j_b = j_a + 1;
            }
            const va = v[j_a * (nn + 1) + i_a];
            const vb = v[j_b * (nn + 1) + i_b];
            const t = if (vb != va) std.math.clamp((l - va) / (vb - va), 0, 1) else 0.5;
            const fi = @as(f64, @floatFromInt(i_a)) + t * @as(f64, @floatFromInt(i_b - i_a));
            const fj = @as(f64, @floatFromInt(j_a)) + t * @as(f64, @floatFromInt(j_b - j_a));
            const nf: f64 = @floatFromInt(nn);
            return .{ x[0] + (x[1] - x[0]) * fi / nf, y[0] + (y[1] - y[0]) * fj / nf };
        }
    }.of;
    try w.writeByte('[');
    var first_poly = true;
    // Open curves first (start at an edge with one segment), then closed loops.
    for (0..2) |pass| for (segs.items, 0..) |sg, k0| {
        if (seen[k0]) continue;
        var start = sg.a;
        if (pass == 0) {
            if (link[sg.a][1] == none) start = sg.a else if (link[sg.b][1] == none) start = sg.b else continue;
        }
        if (!first_poly) try w.writeByte(',');
        first_poly = false;
        try w.writeByte('[');
        var at = start;
        var cur: u32 = @intCast(k0);
        var p0 = pt(zs, n, H, xr, yr, lv, at);
        try w.writeByte('[');
        try writeFixed(w, p0[0], dec[0]);
        try w.writeByte(',');
        try writeFixed(w, p0[1], dec[1]);
        try w.writeByte(']');
        while (true) {
            seen[cur] = true;
            const s = segs.items[cur];
            const other = if (s.a == at) s.b else s.a;
            p0 = pt(zs, n, H, xr, yr, lv, other);
            try w.writeAll(",[");
            try writeFixed(w, p0[0], dec[0]);
            try w.writeByte(',');
            try writeFixed(w, p0[1], dec[1]);
            try w.writeByte(']');
            const l = link[other];
            const next = if (l[0] != cur and l[0] != none and !seen[l[0]]) l[0] else if (l[1] != cur and l[1] != none and !seen[l[1]]) l[1] else none;
            if (next == none) break;
            cur = next;
            at = other;
        }
        try w.writeByte(']');
    };
    try w.writeByte(']');
}

/// Default extents of nn, grid and bits (as viewer/js/runtime.js draws them),
/// including their labels. Null when the data doesn't parse (the line fails later).
fn diagramSize(op: []const u8, fields: anytype) ?[2]f64 {
    const given: ?Vec3 = if (fieldVal(fields, "size")) |v| switch (v) {
        .vec => |x| x,
        else => null,
    } else null;
    if (std.mem.eql(u8, op, "nn")) {
        const layers = std.mem.count(u8, fieldStr(fields, "layers") orelse "", ",") + 1;
        const lf: f64 = @floatFromInt(layers);
        const s = given orelse Vec3{ @max(2, 2.6 * (lf - 1)), 4, 0 };
        return .{ s[0] + 1.2, s[1] + 1.1 }; // neurons overhang; labels below
    }
    if (std.mem.eql(u8, op, "grid")) {
        if (given) |s| return .{ s[0], s[1] };
        const d = (fieldVal(fields, "dims") orelse return null).vec;
        const cell = gridCell(d[0], d[1]);
        return .{ d[1] * cell, d[0] * cell };
    }
    var n: f64 = 0;
    for (fieldStr(fields, "pattern") orelse "") |c| if (c != '|' and c != ' ') {
        n += 1;
    };
    const cell = if (fieldVal(fields, "size")) |v| v.num else bitsCell(n);
    return .{ n * cell, cell * 3.4 };
}
/// Cell size of a grid without size=: at most 0.45, and the whole within 8x6.
pub fn gridCell(rows: f64, cols: f64) f64 {
    return @min(0.45, @min(8 / cols, 6 / rows));
}
/// Box size of a bits diagram without size=: at most 0.55, and the whole within 14.
pub fn bitsCell(n: f64) f64 {
    return @min(0.55, 14 / @max(n, 1));
}

fn fieldVal(fields: anytype, name: []const u8) ?Value {
    for (fields) |f| if (std.mem.eql(u8, f.name, name)) return f.val;
    return null;
}

/// The data ranges of an `axes` command (defaults as in the viewer).
fn axesRanges(fields: anytype) AxesRanges {
    const z = fieldVal(fields, "z");
    const is3 = z != null;
    return .{
        .is3 = is3,
        .x = if (fieldVal(fields, "x")) |v| v.range else .{ -5, 5 },
        .y = if (fieldVal(fields, "y")) |v| v.range else if (is3) .{ -5, 5 } else .{ -3, 3 },
        .z = if (z) |v| v.range else .{ 0, 0 },
        .xlog = if (fieldVal(fields, "xlog")) |v| v.boolean else false,
        .ylog = if (fieldVal(fields, "ylog")) |v| v.boolean else false,
    };
}

/// `v` with at most `decimals` digits after the point, trailing zeros dropped.
fn writeFixed(w: *Writer, v: f64, decimals: u8) Writer.Error!void {
    var b: [64]u8 = undefined;
    const s = switch (decimals) {
        inline 0...8 => |d| std.fmt.bufPrint(&b, std.fmt.comptimePrint("{{d:.{d}}}", .{d}), .{v}) catch return writeNum(w, v),
        else => unreachable,
    };
    var t: []const u8 = s;
    if (std.mem.indexOfScalar(u8, t, '.') != null) {
        t = std.mem.trimEnd(u8, t, "0");
        t = std.mem.trimEnd(u8, t, ".");
    }
    if (std.mem.eql(u8, t, "-0")) t = "0";
    try w.writeAll(t);
}

fn fieldStr(fields: anytype, name: []const u8) ?[]const u8 {
    for (fields) |f| if (std.mem.eql(u8, f.name, name)) return switch (f.val) {
        .str => |x| x,
        .word => |x| x,
        else => null,
    };
    return null;
}

fn hasKey(items: anytype, k: []const u8) bool {
    for (items) |kv| if (std.mem.eql(u8, kv.key, k)) return true;
    return false;
}

fn tyName(t: Ty) []const u8 {
    return switch (t) {
        .num => "number",
        .vec => "vector",
        .point => "point",
        .str => "string",
        .color => "colour",
        .range => "range",
        .boolean => "true/false",
        .id => "name",
        .word => "word",
        .nums => "numbers",
    };
}

/// " — usage: line FROM TO" style hint, comptime-generated per op.
fn usageHint(op: *const Op) []const u8 {
    inline for (ops) |o| {
        if (std.mem.eql(u8, o.name, op.name)) {
            const s = comptime blk: {
                var u: []const u8 = " — usage: " ++ o.name;
                for (o.pos) |prm| {
                    const tn = switch (prm.ty) {
                        .num => "N",
                        .vec => "V",
                        .point => "P",
                        .str => "\"S\"",
                        .color => "C",
                        .range => "R",
                        .boolean => "B",
                        .id => "NAME",
                        .word => "W",
                        .nums => "N,N",
                    };
                    u = u ++ " " ++ (if (prm.opt) "[" else "") ++ prm.name ++ ":" ++ tn ++ (if (prm.many) "…" else "") ++ (if (prm.opt) "]" else "");
                }
                break :blk u;
            };
            return s;
        }
    }
    return "";
}

// ───────────────────────────── stream (line buffering, mixed mode) ─────────────────────────────

pub const Mode = enum { vcl, mixed };

pub const Stream = struct {
    mode: Mode,
    line_no: u32 = 0,
    fence: enum { none, vcl, other } = .none,
    pending: std.ArrayList(u8) = .empty,
    // An open `<<TAG` block: the command line, its tag, and the lines so far.
    here_tag: std.ArrayList(u8) = .empty,
    here_head: std.ArrayList(u8) = .empty,
    here_body: std.ArrayList(u8) = .empty,
    here_line: u32 = 0,
    warned_cmd: bool = false, // mixed mode: told once that a command sat outside a fence
    // `par` … `end`: every command in the block starts together.
    par: enum { off, first, rest } = .off,
    par_line: u32 = 0,
    here_par: bool = false, // the open <<TAG block's command is inside a par block (after its first)

    const max_block = 256 * 1024;

    pub fn init(mode: Mode) Stream {
        return .{ .mode = mode };
    }

    pub fn deinit(st: *Stream, gpa: Allocator) void {
        st.pending.deinit(gpa);
        st.here_tag.deinit(gpa);
        st.here_head.deinit(gpa);
        st.here_body.deinit(gpa);
    }

    fn closeBlock(st: *Stream) void {
        st.here_tag.clearRetainingCapacity();
        st.here_head.clearRetainingCapacity();
        st.here_body.clearRetainingCapacity();
    }

    /// Feed an arbitrary chunk of text; every completed line is executed.
    pub fn feed(st: *Stream, scene: *Scene, chunk: []const u8, out: *Writer) (Writer.Error || Allocator.Error)!void {
        var rest = chunk;
        while (std.mem.indexOfScalar(u8, rest, '\n')) |nl| {
            if (st.pending.items.len > 0) {
                try st.pending.appendSlice(scene.gpa, rest[0..nl]);
                const full = try scene.gpa.dupe(u8, st.pending.items);
                defer scene.gpa.free(full);
                st.pending.clearRetainingCapacity();
                try st.line(scene, full, out);
            } else try st.line(scene, rest[0..nl], out);
            rest = rest[nl + 1 ..];
        }
        try st.pending.appendSlice(scene.gpa, rest);
    }

    /// Execute any final unterminated line; report a block left open.
    pub fn finish(st: *Stream, scene: *Scene, out: *Writer) (Writer.Error || Allocator.Error)!void {
        if (st.pending.items.len > 0) {
            const full = try scene.gpa.dupe(u8, st.pending.items);
            defer scene.gpa.free(full);
            st.pending.clearRetainingCapacity();
            try st.line(scene, full, out);
        }
        if (st.par != .off) {
            try diagLevel(out, "warning", st.par_line, "par block never closed: end it with a line containing only end");
            st.par = .off;
        }
        if (st.here_tag.items.len > 0) {
            var buf: [160]u8 = undefined;
            const msg = std.fmt.bufPrint(&buf, "<<{s} block never closed: end it with a line containing only {s}", .{ st.here_tag.items, st.here_tag.items }) catch "<< block never closed";
            try diag(out, st.here_line, msg);
            st.closeBlock();
        }
    }

    pub fn line(st: *Stream, scene: *Scene, raw: []const u8, out: *Writer) (Writer.Error || Allocator.Error)!void {
        st.line_no += 1;
        const text = std.mem.trimEnd(u8, raw, "\r");

        // Inside a <<TAG block everything is content (even ``` lines) until TAG.
        if (st.here_tag.items.len > 0) {
            if (std.mem.eql(u8, std.mem.trim(u8, text, " \t"), st.here_tag.items)) {
                const body = std.mem.trimEnd(u8, st.here_body.items, "\n");
                scene.force_par = st.here_par;
                defer scene.force_par = false;
                try scene.commandBody(st.here_head.items, body, st.here_line, out);
                st.closeBlock();
            } else if (st.here_body.items.len + text.len > max_block) {
                try diag(out, st.here_line, "<< block too long (over 256 KB); it was dropped");
                st.closeBlock();
            } else {
                try st.here_body.appendSlice(scene.gpa, text);
                try st.here_body.append(scene.gpa, '\n');
            }
            return;
        }
        const t = std.mem.trim(u8, text, " \t");
        if (st.mode == .vcl) {
            if (!std.mem.startsWith(u8, t, "```")) return st.vclLine(scene, text, out);
            // A fence in plain VCL: this is mixed text sent in the wrong mode.
            st.mode = .mixed;
            try diagLevel(out, "warning", st.line_no, "this is mixed text (prose with ```vcl fences) sent as plain VCL, so the prose above failed as commands; switched to mixed mode from here. Send it with mode=mixed");
        }
        if (std.mem.startsWith(u8, t, "```")) {
            const lang = std.mem.trim(u8, t[3..], " \t");
            st.fence = switch (st.fence) {
                .none => if (std.mem.eql(u8, lang, "vcl")) .vcl else .other,
                else => .none,
            };
            if (st.fence == .none and st.par != .off) {
                try diagLevel(out, "warning", st.par_line, "par block still open at the end of the ```vcl fence; closed it there (end it with a line containing only end)");
                st.par = .off;
            }
            return;
        }
        switch (st.fence) {
            .vcl => return st.vclLine(scene, text, out),
            .other => return,
            .none => {},
        }
        if (!st.warned_cmd and looksLikeCommand(t)) {
            st.warned_cmd = true;
            try diagLevel(out, "warning", st.line_no, "this looks like a VCL command outside a ```vcl fence, so it was spoken, not drawn. Put commands inside ```vcl fences, or send plain VCL without mode=mixed");
        }
        // Prose: strip light markdown, split into sentences.
        var prose = std.mem.trimStart(u8, t, "#>-* \t");
        if (prose.len == 0) return;
        var buf: [4096]u8 = undefined;
        var n: usize = 0;
        for (prose) |c| {
            if (c == '*' or c == '`' or c == '_') continue;
            if (n == buf.len) break;
            buf[n] = c;
            n += 1;
        }
        prose = buf[0..n];
        var start: usize = 0;
        var i: usize = 0;
        while (i < prose.len) : (i += 1) {
            const c = prose[i];
            if ((c == '.' or c == '?' or c == '!') and (i + 1 == prose.len or prose[i + 1] == ' ')) {
                const sent = std.mem.trim(u8, prose[start .. i + 1], " ");
                if (sent.len > 0) try scene.say(sent, st.line_no, out);
                start = i + 1;
            }
        }
        const tail = std.mem.trim(u8, prose[start..], " ");
        if (tail.len > 0) try scene.say(tail, st.line_no, out);
    }

    fn vclLine(st: *Stream, scene: *Scene, text: []const u8, out: *Writer) (Writer.Error || Allocator.Error)!void {
        const word = bareWord(text);
        if (std.mem.eql(u8, word, "par")) {
            if (st.par != .off) {
                var buf: [96]u8 = undefined;
                return diag(out, st.line_no, std.fmt.bufPrint(&buf, "par: a par block is already open (since line {d}); par blocks don't nest", .{st.par_line}) catch "par: already open");
            }
            st.par = .first;
            st.par_line = st.line_no;
            return;
        }
        if (std.mem.eql(u8, word, "end")) {
            if (st.par == .off) return diag(out, st.line_no, "end: no par block is open");
            st.par = .off;
            return;
        }
        const is_cmd = word.len > 0 or blk: {
            const t = std.mem.trim(u8, text, " \t");
            break :blk t.len > 0 and t[0] != '#';
        };
        const together = st.par == .rest;
        if (is_cmd and st.par == .first) st.par = .rest;
        if (blockStart(text)) |b| {
            try st.here_tag.appendSlice(scene.gpa, b.tag);
            try st.here_head.appendSlice(scene.gpa, b.head);
            st.here_line = st.line_no;
            st.here_par = together;
            return;
        }
        scene.force_par = together;
        defer scene.force_par = false;
        return scene.command(text, st.line_no, out);
    }
};

/// The line's only word (ignoring spaces and a trailing # comment), or "" if it has none or several.
fn bareWord(text: []const u8) []const u8 {
    const code = std.mem.sliceTo(text, '#');
    const t = std.mem.trim(u8, code, " \t\r");
    if (std.mem.indexOfAny(u8, t, " \t") != null) return "";
    return t;
}

/// Words that read like a sentence: 4+ plain tokens ending in punctuation,
/// or a first word with an apostrophe (Let's, It's).
fn looksLikeProse(toks: []const Token) bool {
    if (std.mem.indexOfScalar(u8, toks[0].text, '\'') != null or std.mem.indexOf(u8, toks[0].text, "’") != null) return true;
    if (toks.len < 4) return false;
    for (toks) |t| if (std.mem.indexOfAny(u8, t.text, "=\"") != null) return false;
    const last = toks[toks.len - 1].text;
    return std.mem.indexOfScalar(u8, ".?!:,", last[last.len - 1]) != null;
}

/// A prose line that is really a command: `[&] [name:] op …` with a known op and
/// command-like arguments (key=value, a quoted string, x,y numbers, or nothing),
/// not ending like a sentence.
fn looksLikeCommand(t: []const u8) bool {
    if (t.len == 0 or std.mem.endsWith(u8, t, ".") or std.mem.endsWith(u8, t, "?") or std.mem.endsWith(u8, t, "!")) return false;
    var it = std.mem.tokenizeAny(u8, std.mem.trimStart(u8, t, "& "), " \t");
    var first = it.next() orelse return false;
    if (std.mem.indexOfScalar(u8, first, ':')) |c| {
        if (!isIdent(first[0..c])) return false;
        first = if (c + 1 < first.len) first[c + 1 ..] else it.next() orelse return false;
    }
    if (findOp(first) == null) return false;
    const rest = it.rest();
    if (rest.len == 0) return true; // `clear`, `point`
    if (std.mem.indexOfScalar(u8, rest, '=') != null or std.mem.indexOfScalar(u8, rest, '"') != null) return true;
    for (rest, 0..) |c, i| if (c == ',' and i > 0 and i + 1 < rest.len and
        (std.ascii.isDigit(rest[i - 1]) or rest[i - 1] == '.') and
        (std.ascii.isDigit(rest[i + 1]) or rest[i + 1] == '-' or rest[i + 1] == '.')) return true;
    return false;
}

/// `cmd args… <<TAG` (outside any string) opens a block that ends at a line `TAG`.
pub fn blockStart(text: []const u8) ?struct { head: []const u8, tag: []const u8 } {
    const t = std.mem.trimEnd(u8, text, " \t");
    const sp = std.mem.lastIndexOfAny(u8, t, " \t") orelse return null;
    const last = t[sp + 1 ..];
    if (!std.mem.startsWith(u8, last, "<<") or !isIdent(last[2..])) return null;
    const head = t[0..sp];
    var quotes: usize = 0;
    var i: usize = 0;
    while (i < head.len) : (i += 1) {
        if (head[i] == '\\') {
            i += 1;
        } else if (head[i] == '"') quotes += 1;
    }
    if (quotes % 2 != 0) return null;
    if (std.mem.indexOf(u8, head, " # ") != null) return null; // inside a comment
    return .{ .head = head, .tag = last[2..] };
}

fn diag(out: *Writer, line_no: u32, msg: []const u8) Writer.Error!void {
    return diagLevel(out, "error", line_no, msg);
}

fn diagLevel(out: *Writer, level: []const u8, line_no: u32, msg: []const u8) Writer.Error!void {
    try out.print("{{\"op\":\"diag\",\"level\":\"{s}\",\"line\":{d},\"msg\":", .{ level, line_no });
    try writeJsonString(out, msg);
    try out.writeAll("}\n");
}

// ───────────────────────────── helpers ─────────────────────────────

pub fn isIdent(s: []const u8) bool {
    if (s.len == 0) return false;
    if (!(std.ascii.isAlphabetic(s[0]) or s[0] == '_')) return false;
    for (s[1..]) |c| if (!(std.ascii.isAlphanumeric(c) or c == '_')) return false;
    return true;
}

fn parseNum(s: []const u8) ?f64 {
    if (s.len == 0) return null;
    const v = std.fmt.parseFloat(f64, s) catch return null;
    if (!std.math.isFinite(v)) return null;
    // Reject things parseFloat accepts that aren't plain numbers (e.g. "0x10", "inf").
    for (s) |c| if (!(std.ascii.isDigit(c) or c == '.' or c == '-' or c == '+' or c == 'e' or c == 'E')) return null;
    return v;
}

fn parseVec(s: []const u8) ?Vec3 {
    var v: Vec3 = .{ 0, 0, 0 };
    var it = std.mem.splitScalar(u8, s, ',');
    var n: usize = 0;
    while (it.next()) |part| : (n += 1) {
        if (n == 3) return null;
        v[n] = parseNum(part) orelse return null;
    }
    if (n < 2) return null;
    return v;
}

/// `\"` → `"` always. Unless `raw`: `\\` → `\`, `\n` → newline, `\t` → tab.
/// Any other backslash is kept, so stray LaTeX in a plain string survives.
fn unescape(a: Allocator, s: []const u8, raw: bool) Allocator.Error![]const u8 {
    if (std.mem.indexOfScalar(u8, s, '\\') == null) return s;
    var out = try a.alloc(u8, s.len);
    var n: usize = 0;
    var i: usize = 0;
    while (i < s.len) : (i += 1) {
        if (s[i] == '\\' and i + 1 < s.len) {
            const c = s[i + 1];
            const rep: ?u8 = if (c == '"') '"' else if (raw) null else switch (c) {
                '\\' => '\\',
                'n' => '\n',
                't' => '\t',
                else => null,
            };
            if (rep) |r| {
                out[n] = r;
                n += 1;
                i += 1;
                continue;
            }
        }
        out[n] = s[i];
        n += 1;
    }
    return out[0..n];
}

/// Optimal-string-alignment distance (Levenshtein + adjacent transposition).
fn editDistance(a: []const u8, b: []const u8) usize {
    if (a.len > 32 or b.len > 32) return 99;
    var d: [33][33]usize = undefined;
    for (0..a.len + 1) |i| d[i][0] = i;
    for (0..b.len + 1) |j| d[0][j] = j;
    for (1..a.len + 1) |i| for (1..b.len + 1) |j| {
        const ca = std.ascii.toLower(a[i - 1]);
        const cb = std.ascii.toLower(b[j - 1]);
        const cost: usize = if (ca == cb) 0 else 1;
        d[i][j] = @min(@min(d[i - 1][j] + 1, d[i][j - 1] + 1), d[i - 1][j - 1] + cost);
        if (i > 1 and j > 1 and ca == std.ascii.toLower(b[j - 2]) and std.ascii.toLower(a[i - 2]) == cb)
            d[i][j] = @min(d[i][j], d[i - 2][j - 2] + 1);
    };
    return d[a.len][b.len];
}

fn suggest(word: []const u8, cands: []const []const u8) ?[]const u8 {
    var best: ?[]const u8 = null;
    var best_d: usize = 99;
    for (cands) |c| {
        const d = editDistance(word, c);
        if (d < best_d) {
            best_d = d;
            best = c;
        }
    }
    if (word.len <= 2) return null;
    const limit: usize = if (word.len <= 4) 1 else 2;
    return if (best_d <= limit) best else null;
}

fn writeNum(w: *Writer, v: f64) Writer.Error!void {
    if (v == @trunc(v) and @abs(v) < 1e15) {
        try w.print("{d}", .{@as(i64, @intFromFloat(v))});
    } else try w.print("{d}", .{v});
}

fn writeVec(w: *Writer, v: Vec3) Writer.Error!void {
    try w.writeAll("[");
    try writeNum(w, v[0]);
    try w.writeAll(",");
    try writeNum(w, v[1]);
    try w.writeAll(",");
    try writeNum(w, v[2]);
    try w.writeAll("]");
}

pub fn writeJsonString(w: *Writer, s: []const u8) Writer.Error!void {
    try w.writeByte('"');
    for (s) |c| switch (c) {
        '"' => try w.writeAll("\\\""),
        '\\' => try w.writeAll("\\\\"),
        '\n' => try w.writeAll("\\n"),
        '\r' => try w.writeAll("\\r"),
        '\t' => try w.writeAll("\\t"),
        0...8, 11, 12, 14...0x1f => try w.print("\\u{x:0>4}", .{c}),
        else => try w.writeByte(c),
    };
    try w.writeByte('"');
}

fn writeValue(w: *Writer, v: Value) Writer.Error!void {
    switch (v) {
        .num => |n| try writeNum(w, n),
        .vec => |x| try writeVec(w, x),
        .point => |pt| switch (pt) {
            .abs => |x| try writeVec(w, x),
            .ref => |r| {
                try w.writeAll("{\"ref\":");
                try writeJsonString(w, r.id);
                try w.writeAll(",\"anchor\":");
                try writeJsonString(w, r.anchor);
                try w.writeAll(",\"off\":");
                try writeVec(w, r.off);
                try w.writeAll("}");
            },
        },
        .str => |s| try writeJsonString(w, s),
        .color => |c| try w.print("\"#{x:0>6}\"", .{c}),
        .range => |r| {
            try w.writeAll("[");
            try writeNum(w, r[0]);
            try w.writeAll(",");
            try writeNum(w, r[1]);
            try w.writeAll("]");
        },
        .boolean => |b| try w.writeAll(if (b) "true" else "false"),
        .id => |s| try writeJsonString(w, s),
        .word => |s| try writeJsonString(w, s),
        .list => |l| {
            try w.writeAll("[");
            for (l, 0..) |x, i| {
                if (i > 0) try w.writeAll(",");
                try writeValue(w, x);
            }
            try w.writeAll("]");
        },
    }
}

// ───────────────────────────── tests ─────────────────────────────

const testing = std.testing;

fn runLines(src: []const u8, mode: Mode) ![]u8 {
    var scene = Scene.init(testing.allocator);
    defer scene.deinit();
    scene.sample = false; // tests/"plot and surf are sampled" checks the samples
    var st = Stream.init(mode);
    defer st.deinit(testing.allocator);
    var aw: Writer.Allocating = .init(testing.allocator);
    errdefer aw.deinit();
    try st.feed(&scene, src, &aw.writer);
    try st.finish(&scene, &aw.writer);
    return aw.toOwnedSlice();
}

fn expectOut(src: []const u8, expected: []const u8) !void {
    const got = try runLines(src, .vcl);
    defer testing.allocator.free(got);
    try testing.expectEqualStrings(expected, got);
}

test "basic draw command" {
    try expectOut("a: line 0,0 1,2,3 color=#f80 width=0.1\n",
        \\{"op":"line","id":"a","line":1,"par":false,"dur":0.6,"from":[0,0,0],"to":[1,2,3],"color":"#ff8800","width":0.1}
        \\
    );
}

test "auto ids, parallel, refs with anchor and offset" {
    try expectOut(
        \\A: pt -3,-2
        \\& pt 1,1
        \\&arrow A.top+0,0.5 _2 dur=1 ease=out
    ,
        \\{"op":"pt","id":"A","line":1,"par":false,"dur":0.3,"at":[-3,-2,0]}
        \\{"op":"pt","id":"_1","line":2,"par":true,"dur":0.3,"at":[1,1,0]}
        \\{"op":"diag","level":"error","line":3,"msg":"arrow: unknown name '_2'"}
        \\
    );
}

test "comments and colours" {
    try expectOut(
        \\# a comment
        \\c: circle 0,0 1 fill=#abc color=blue  # trailing comment
    ,
        \\{"op":"circle","id":"c","line":2,"par":false,"dur":0.6,"at":[0,0,0],"r":1,"fill":"#aabbcc","color":"#42a5f5"}
        \\
    );
}

test "errors are precise and skip the line" {
    try expectOut(
        \\lien 0,0 1,1
        \\line 0,0
        \\circle 0,0 1 colour=red
        \\del nope
        \\text 0,0 hello
    ,
        \\{"op":"diag","level":"error","line":1,"msg":"unknown command 'lien' (did you mean 'line'?)"}
        \\{"op":"diag","level":"error","line":2,"msg":"line: missing 'to' (point) — usage: line from:P to:P"}
        \\{"op":"diag","level":"warning","line":3,"msg":"circle: unknown key 'colour' ignored (did you mean 'color'?)"}
        \\{"op":"circle","id":"_1","line":3,"par":false,"dur":0.6,"at":[0,0,0],"r":1}
        \\{"op":"diag","level":"error","line":4,"msg":"del: unknown name 'nope'"}
        \\{"op":"diag","level":"error","line":5,"msg":"text: 'text' must be a quoted string, e.g. \"hello\""}
        \\
    );
}

test "strings with escapes and spaces" {
    try expectOut("say \"He said \\\"hi\\\" twice\"\n",
        \\{"op":"say","line":1,"par":false,"dur":1.92,"text":"He said \"hi\" twice"}
        \\
    );
}

test "tex strings keep backslashes; text strings unescape" {
    try expectOut(
        \\tex 0,0 "\frac{a}{b} \neq \nabla \\ x"
        \\text 0,0 "one\ntwo \alpha \"q\""
    ,
        \\{"op":"tex","id":"_1","line":1,"par":false,"dur":0.6,"at":[0,0,0],"text":"\\frac{a}{b} \\neq \\nabla \\\\ x"}
        \\{"op":"text","id":"_2","line":2,"par":false,"dur":0.5,"at":[0,0,0],"text":"one\ntwo \\alpha \"q\""}
        \\
    );
}

test "plot needs axes; del cascades to children" {
    try expectOut(
        \\c: circle 0,0 1
        \\plot c "x^2"
        \\ax: axes 0,0 x=-3..3 y=-1..9
        \\f: plot ax "x^2" color=red
        \\p: pt 1,1 on=ax
        \\del ax
        \\move p 0,0
    ,
        \\{"op":"circle","id":"c","line":1,"par":false,"dur":0.6,"at":[0,0,0],"r":1}
        \\{"op":"diag","level":"error","line":2,"msg":"plot: 'c' is a circle, not axes"}
        \\{"op":"axes","id":"ax","line":3,"par":false,"dur":1,"at":[0,0,0],"x":[-3,3],"y":[-1,9]}
        \\{"op":"plot","id":"f","line":4,"par":false,"dur":1.2,"axes":"ax","exprs":["x^2"],"color":"#ef5350"}
        \\{"op":"pt","id":"p","line":5,"par":false,"dur":0.3,"at":[1,1,0],"on":"ax"}
        \\{"op":"del","line":6,"par":false,"dur":0.3,"ids":["ax"]}
        \\{"op":"diag","level":"error","line":7,"msg":"move: unknown name 'p'"}
        \\
    );
}

test "plot and surf are sampled" {
    var scene = Scene.init(testing.allocator);
    defer scene.deinit();
    var st = Stream.init(.vcl);
    defer st.deinit(testing.allocator);
    var aw: Writer.Allocating = .init(testing.allocator);
    defer aw.deinit();
    try st.feed(&scene,
        \\ax: axes 0,0 x=-2..2 y=-1..1
        \\f: plot ax "1/x" x=-1..1
        \\g: plot ax "cos(t)" "sin(t)" t=0..3.1415927
        \\plot ax "x^"
        \\plot ax "x+5"
        \\s: surf ax "x+y"
        \\a3: axes 0,0 z=0..1
        \\s3: surf a3 "x+y" res=4
        \\
    , &aw.writer);
    const out = aw.written();
    var lines = std.mem.splitScalar(u8, out, '\n');
    _ = lines.next(); // axes
    const f = lines.next().?;
    // 1/x splits at the pole into two runs, clipped to y -1..1 (+5%).
    try testing.expect(std.mem.indexOf(u8, f, "\"polys\":[[[-1,-1,0],") != null);
    try testing.expectEqual(@as(usize, 2), std.mem.count(u8, f, "]],[[") + 1);
    const g = lines.next().?;
    try testing.expect(std.mem.indexOf(u8, g, "\"polys\":[[[1,0,0],") != null);
    try testing.expect(std.mem.endsWith(u8, g, "[-1,0,0]]]}"));
    try testing.expectEqualStrings(
        \\{"op":"diag","level":"error","line":4,"msg":"plot: \"x^\": unexpected end of expression"}
    , lines.next().?);
    try testing.expectEqualStrings(
        \\{"op":"diag","level":"error","line":5,"msg":"plot: nothing to draw: \"x+5\" has no values inside the axes' y range -1..1 for x in this range"}
    , lines.next().?);
    try testing.expectEqualStrings(
        \\{"op":"diag","level":"error","line":6,"msg":"surf: needs 3D axes (give 'ax' a z=range)"}
    , lines.next().?);
    _ = lines.next(); // a3
    // 5×5 grid over x,y -5..5, clamped to z 0..1.
    try testing.expect(std.mem.endsWith(u8, lines.next().?,
        \\"res":4,"zgrid":{"n":4,"z":[0,0,0,0,0,0,0,0,0,1,0,0,0,1,1,0,0,1,1,1,0,1,1,1,1]}}
    ));
}

test "streaming in arbitrary chunks equals whole input" {
    const src = "a: pt 0,0\nb: pt 1,1\nline a b\n";
    const whole = try runLines(src, .vcl);
    defer testing.allocator.free(whole);

    var scene = Scene.init(testing.allocator);
    defer scene.deinit();
    var st = Stream.init(.vcl);
    defer st.deinit(testing.allocator);
    var aw: Writer.Allocating = .init(testing.allocator);
    defer aw.deinit();
    var i: usize = 0;
    while (i < src.len) : (i += 3) try st.feed(&scene, src[i..@min(i + 3, src.len)], &aw.writer);
    try st.finish(&scene, &aw.writer);
    try testing.expectEqualStrings(whole, aw.written());
}

test "mixed mode: prose becomes say, vcl fences run, other fences skipped" {
    const got = try runLines(
        \\Here is **a line**. Watch it.
        \\```vcl
        \\l: line 0,0 1,1
        \\```
        \\```python
        \\print("x")
        \\```
        \\Done
    , .mixed);
    defer testing.allocator.free(got);
    try testing.expectEqualStrings(
        \\{"op":"say","line":1,"par":false,"dur":1.92,"text":"Here is a line."}
        \\{"op":"say","line":1,"par":false,"dur":1.2,"text":"Watch it."}
        \\{"op":"line","id":"l","line":3,"par":false,"dur":0.6,"from":[0,0,0],"to":[1,1,0]}
        \\{"op":"say","line":8,"par":false,"dur":1.2,"text":"Done"}
        \\
    , got);
}

test "wrong mode: fences in plain VCL switch to mixed; commands in prose are flagged once" {
    const plain = try runLines(
        \\Let's draw a line, shall we?
        \\```vcl
        \\l: line 0,0 1,1
        \\```
        \\Done.
    , .vcl);
    defer testing.allocator.free(plain);
    try testing.expectEqualStrings(
        \\{"op":"diag","level":"error","line":1,"msg":"unknown command 'Let's': this line looks like prose; text that mixes prose and ```vcl fences must be sent with mode=mixed"}
        \\{"op":"diag","level":"warning","line":2,"msg":"this is mixed text (prose with ```vcl fences) sent as plain VCL, so the prose above failed as commands; switched to mixed mode from here. Send it with mode=mixed"}
        \\{"op":"line","id":"l","line":3,"par":false,"dur":0.6,"from":[0,0,0],"to":[1,1,0]}
        \\{"op":"say","line":5,"par":false,"dur":1.2,"text":"Done."}
        \\
    , plain);
    const mixed = try runLines(
        \\title "Ohm's law"
        \\l: line 0,0 1,1
        \\Point at the line, then clear the board.
    , .mixed);
    defer testing.allocator.free(mixed);
    try testing.expectEqualStrings(
        \\{"op":"diag","level":"warning","line":1,"msg":"this looks like a VCL command outside a ```vcl fence, so it was spoken, not drawn. Put commands inside ```vcl fences, or send plain VCL without mode=mixed"}
        \\{"op":"say","line":1,"par":false,"dur":1.54,"text":"title \"Ohm's law\""}
        \\{"op":"say","line":2,"par":false,"dur":1.92,"text":"l: line 0,0 1,1"}
        \\{"op":"say","line":3,"par":false,"dur":3.44,"text":"Point at the line, then clear the board."}
        \\
    , mixed);
}

test "overlap lint: boxes that land on each other; moved things are forgotten" {
    try expectOut(
        \\c: code -2,0 "int x = 1;"
        \\n: text -1,0 "note"
        \\move c -2,3
        \\m: text -2.6,0 "ok"
    ,
        \\{"op":"code","id":"c","line":1,"par":false,"dur":0.6,"at":[-2,0,0],"text":"int x = 1;"}
        \\{"op":"diag","level":"warning","line":2,"msg":"text: overlaps 'c' (code) around x -1.5..-0.5, y -0.4..0.4; move one of them"}
        \\{"op":"text","id":"n","line":2,"par":false,"dur":0.5,"at":[-1,0,0],"text":"note"}
        \\{"op":"move","line":3,"par":false,"dur":0.6,"target":"c","to":[-2,3,0]}
        \\{"op":"text","id":"m","line":4,"par":false,"dur":0.5,"at":[-2.6,0,0],"text":"ok"}
        \\
    );
}

test "group needs name and reparents" {
    try expectOut(
        \\a: pt 0,0
        \\b: pt 1,0
        \\group a b
        \\g: group a b
        \\del g
        \\del a
    ,
        \\{"op":"pt","id":"a","line":1,"par":false,"dur":0.3,"at":[0,0,0]}
        \\{"op":"pt","id":"b","line":2,"par":false,"dur":0.3,"at":[1,0,0]}
        \\{"op":"diag","level":"error","line":3,"msg":"group: needs a name, e.g. 'g: group a b'"}
        \\{"op":"group","id":"g","line":4,"par":false,"dur":0,"ids":["a","b"]}
        \\{"op":"del","line":5,"par":false,"dur":0.3,"ids":["g"]}
        \\{"op":"diag","level":"error","line":6,"msg":"del: unknown name 'a'"}
        \\
    );
}

test "every example lesson parses without errors" {
    // Kept in sync with lessons/*.vcl by the CLI `check` command; here we
    // only make sure the spec example parses.
    const got = try runLines(
        \\title "Pythagoras"
        \\A: pt -3,-2 label="A"
        \\& B: pt 3,-2 label="B"
        \\& C: pt 3,2 label="C"
        \\ab: line A B
        \\bc: line B C
        \\ca: line C A color=yellow
        \\& angle A B C right=true
        \\say "Here's a right triangle."
        \\hl ca
        \\tex 0,-3.6 "a^2 + b^2 = c^2" size=0.7
    , .vcl);
    defer testing.allocator.free(got);
    try testing.expect(std.mem.indexOf(u8, got, "\"diag\"") == null);
}

test "pins: parts and block ports are checked, with suggestions" {
    try expectOut(
        \\R1: part 0,0 resistor value="10k"
        \\U1: part 3,0 nand
        \\wire R1.b U1.a dot=end
        \\wire R1.c U1.y
        \\cpu: block 0,3 3,2 "CPU" in="clk,rst" out="addr"
        \\wire cpu.addr cpu.clk+0,-1
        \\wire cpu.adr R1.a
        \\b2: block 0,0 1,1 out="left"
    ,
        \\{"op":"part","id":"R1","line":1,"par":false,"dur":0.5,"at":[0,0,0],"kind":"resistor","value":"10k"}
        \\{"op":"part","id":"U1","line":2,"par":false,"dur":0.5,"at":[3,0,0],"kind":"nand"}
        \\{"op":"wire","id":"_1","line":3,"par":false,"dur":0.5,"pts":[{"ref":"R1","anchor":"b","off":[0,0,0]},{"ref":"U1","anchor":"a","off":[0,0,0]}],"dot":"end"}
        \\{"op":"diag","level":"error","line":4,"msg":"wire: 'R1' has no pin 'c'; its pins are a, b"}
        \\{"op":"block","id":"cpu","line":5,"par":false,"dur":0.6,"at":[0,3,0],"size":[3,2,0],"text":"CPU","in":"clk,rst","out":"addr"}
        \\{"op":"wire","id":"_2","line":6,"par":false,"dur":0.5,"pts":[{"ref":"cpu","anchor":"addr","off":[0,0,0]},{"ref":"cpu","anchor":"clk","off":[0,-1,0]}]}
        \\{"op":"diag","level":"error","line":7,"msg":"wire: 'cpu' has no pin 'adr' (did you mean 'addr'?); its pins are clk, rst, addr"}
        \\{"op":"diag","level":"error","line":8,"msg":"block: port 'left' clashes with the anchor of the same name; pick another"}
        \\
    );
}

test "<<TAG blocks: multi-line strings, even ``` inside mixed mode" {
    const got = try runLines(
        \\Here is the loop.
        \\```vcl
        \\c: code 0,0 lang=c focus=2 <<END
        \\for (int i = 0; i < n; i++) {
        \\    sum += a[i];   // "quoted" \n stays
        \\```
        \\}
        \\END
        \\t: table 6,0 <<ROWS
        \\A|B|Y
        \\0|0|1
        \\
        \\1|1|0
        \\ROWS
        \\```
        \\Done.
    , .mixed);
    defer testing.allocator.free(got);
    try testing.expectEqualStrings(
        \\{"op":"say","line":1,"par":false,"dur":1.92,"text":"Here is the loop."}
        \\{"op":"code","id":"c","line":3,"par":false,"dur":0.6,"at":[0,0,0],"text":"for (int i = 0; i < n; i++) {\n    sum += a[i];   // \"quoted\" \\n stays\n```\n}","lang":"c","focus":[2,2]}
        \\{"op":"table","id":"t","line":9,"par":false,"dur":0.6,"at":[6,0,0],"rows":["A|B|Y","0|0|1","1|1|0"]}
        \\{"op":"say","line":16,"par":false,"dur":1.2,"text":"Done."}
        \\
    , got);
}

test "fig: code in a <<END block, keyed by its hash" {
    const src =
        \\f: fig 0,1 python size=8 <<END
        \\x = np.linspace(0, 1)
        \\plt.plot(x, x**2)
        \\END
        \\fig 0,0 matlab "plot(1:3)"
        \\fig 0,0 octave ""
    ;
    const key = figKey("python", "x = np.linspace(0, 1)\nplt.plot(x, x**2)");
    var buf: [600]u8 = undefined;
    const want = try std.fmt.bufPrint(&buf,
        \\{{"op":"fig","id":"f","line":1,"par":false,"dur":0.8,"at":[0,1,0],"lang":"python","code":"x = np.linspace(0, 1)\nplt.plot(x, x**2)","size":8,"key":"{s}"}}
        \\{{"op":"diag","level":"error","line":5,"msg":"fig: 'lang' must be one of python|octave, got 'matlab'"}}
        \\{{"op":"diag","level":"error","line":6,"msg":"fig: the code is empty; put it in a <<END block: f: fig 0,0 python <<END … END"}}
        \\
    , .{&key});
    try expectOut(src, want);
    try std.testing.expect(!std.mem.eql(u8, &key, &figKey("octave", "x = np.linspace(0, 1)\nplt.plot(x, x**2)")));
}

test "data: computed values fill {d.x} in later lines and prose" {
    const Fake = struct {
        fn run(_: *anyopaque, lang: []const u8, code: []const u8, key: []const u8) Scene.Computed {
            _ = lang;
            _ = key;
            if (std.mem.indexOf(u8, code, "boom") != null) return .{ .err = testing.allocator.dupe(u8, "ZeroDivisionError: division by zero (code line 1)") catch unreachable };
            return .{ .json = testing.allocator.dupe(u8,
                \\{"peak": 3.14159265, "n": 4, "ys": [1, 2.5, 4], "pts": [[0, 1], [1, 0.5]], "label": "decay", "mod": null}
            ) catch unreachable };
        }
    };
    var scene = Scene.init(testing.allocator);
    defer scene.deinit();
    var dummy: u8 = 0;
    scene.compute = .{ .ctx = &dummy, .run = Fake.run };
    var st = Stream.init(.mixed);
    defer st.deinit(testing.allocator);
    var aw: Writer.Allocating = .init(testing.allocator);
    defer aw.deinit();
    try st.feed(&scene,
        \\```vcl
        \\d: data python <<END
        \\peak = np.pi
        \\END
        \\ax: axes 0,0 x=0..4 y=0..4
        \\s: scatter ax "{d.pts}"
        \\bars ax "{d.ys}" labels="{d.label},b,c"
        \\tex 0,0 "\\frac{a}{d.nope}"
        \\e: data python "boom"
        \\```
        \\The peak is {d.peak}, over {d.n} points.
        \\
    , &aw.writer);
    try st.finish(&scene, &aw.writer);
    const got = aw.written();
    try testing.expect(std.mem.indexOf(u8, got, "\"data\"\":\"0,1 1,0.5\"") != null or std.mem.indexOf(u8, got, "\"data\":\"0,1 1,0.5\"") != null);
    try testing.expect(std.mem.indexOf(u8, got, "\"data\":\"1 2.5 4\"") != null);
    try testing.expect(std.mem.indexOf(u8, got, "\"labels\":\"decay,b,c\"") != null);
    try testing.expect(std.mem.indexOf(u8, got, "\\\\frac{a}{d.nope}") != null);
    try testing.expect(std.mem.indexOf(u8, got, "data: ZeroDivisionError: division by zero (code line 1)") != null);
    try testing.expect(std.mem.indexOf(u8, got, "The peak is 3.14159, over 4 points.") != null);
    try testing.expect(std.mem.indexOf(u8, got, "\"op\":\"data\"") == null);
}

test "data: without anything to run the code, an error" {
    try expectOut("d: data python \"x = 1\"\n",
        \\{"op":"diag","level":"error","line":1,"msg":"data: nothing here runs code (it needs vcl serve, or results from vcl figs)"}
        \\
    );
}

test "<<TAG blocks: unclosed and misplaced" {
    try expectOut(
        \\pt 0,0 <<X
        \\stuff
        \\X
        \\code 0,0 <<END
        \\never closed
    ,
        \\{"op":"diag","level":"error","line":1,"msg":"pt: has no text argument for a <<TAG block — usage: pt at:P"}
        \\{"op":"diag","level":"error","line":4,"msg":"<<END block never closed: end it with a line containing only END"}
        \\
    );
}

test "row pins: code lines, table rows, wave signals" {
    try expectOut(
        \\c: code 0,0 "a\nb\nc"
        \\point c.L3
        \\point c.L4
        \\t: table 0,2.5 "A|B" "0|1"
        \\point t.R1
        \\w: wave 0,-2.5 "clk: p.p." "data[7:0]: =.= | A B"
        \\point w.clk
    ,
        \\{"op":"code","id":"c","line":1,"par":false,"dur":0.6,"at":[0,0,0],"text":"a\nb\nc"}
        \\{"op":"point","line":2,"par":false,"dur":0.4,"at":{"ref":"c","anchor":"L3","off":[0,0,0]}}
        \\{"op":"diag","level":"error","line":3,"msg":"point: 'c' has no pin 'L4'; its pins are L1, L2, L3"}
        \\{"op":"table","id":"t","line":4,"par":false,"dur":0.6,"at":[0,2.5,0],"rows":["A|B","0|1"]}
        \\{"op":"point","line":5,"par":false,"dur":0.4,"at":{"ref":"t","anchor":"R1","off":[0,0,0]}}
        \\{"op":"wave","id":"w","line":6,"par":false,"dur":1,"at":[0,-2.5,0],"rows":["clk: p.p.","data[7:0]: =.= | A B"]}
        \\{"op":"point","line":7,"par":false,"dur":0.4,"at":{"ref":"w","anchor":"clk","off":[0,0,0]}}
        \\
    );
}

test "a string glued to the next character is an error, not a silent typo" {
    try expectOut(
        \\w: wave 0,0 "clk: p...": "en: 01" step=1
        \\text 0,0 "a"b"
    ,
        \\{"op":"diag","level":"error","line":1,"msg":"unexpected ':' right after the closing quote at column 23 (a quoted string must be followed by a space)"}
        \\{"op":"diag","level":"error","line":2,"msg":"unexpected 'b' right after the closing quote at column 12 (a quoted string must be followed by a space)"}
        \\
    );
}

test "wave: any signal name becomes a pin; bad symbols are caught" {
    try expectOut(
        \\w: wave 0,0 "FIFO write: 0.1" "d[7:0]: =.= | A B" "top: 1.." "FIFO_write: 0"
        \\point w.FIFO_write
        \\point w.d
        \\wave 0,0 "a: 01q"
    ,
        \\{"op":"diag","level":"warning","line":1,"msg":"wave: signal 'top' gets no pin (point at it with an offset from the wave instead, or rename it)"}
        \\{"op":"diag","level":"warning","line":1,"msg":"wave: signals 'FIFO write' and 'FIFO_write' both become the pin 'FIFO_write'; only 'FIFO write' gets it"}
        \\{"op":"wave","id":"w","line":1,"par":false,"dur":1,"at":[0,0,0],"rows":["FIFO write: 0.1","d[7:0]: =.= | A B","top: 1..","FIFO_write: 0"]}
        \\{"op":"point","line":2,"par":false,"dur":0.4,"at":{"ref":"w","anchor":"FIFO_write","off":[0,0,0]}}
        \\{"op":"point","line":3,"par":false,"dur":0.4,"at":{"ref":"w","anchor":"d","off":[0,0,0]}}
        \\{"op":"diag","level":"error","line":4,"msg":"wave: unknown symbol 'q' in \"a: 01q\" (use 0 1 z x . = p n, after 'name:')"}
        \\
    );
}

test "bounds lint: text and panels that run off the board" {
    try expectOut(
        \\text 7,0 "far too long for this spot"
        \\text -7.9,0 "ok" align=left
        \\a: axes 0,0 x=0..10 y=0..1
        \\text 9,0 "data coordinates are not checked" on=a
    ,
        \\{"op":"diag","level":"warning","line":1,"msg":"text: about 5.4x0.7, spanning x 4.3..9.7, y -0.4..0.4: it runs off the right of the board (x -8..8, y -4.5..4.5); move it inward, shorten it or reduce size="}
        \\{"op":"text","id":"_1","line":1,"par":false,"dur":0.5,"at":[7,0,0],"text":"far too long for this spot"}
        \\{"op":"text","id":"_2","line":2,"par":false,"dur":0.5,"at":[-7.9,0,0],"text":"ok","align":"left"}
        \\{"op":"axes","id":"a","line":3,"par":false,"dur":1,"at":[0,0,0],"x":[0,10],"y":[0,1]}
        \\{"op":"text","id":"_3","line":4,"par":false,"dur":0.5,"at":[9,0,0],"text":"data coordinates are not checked","on":"a"}
        \\
    );
}

test "block size lint warns before things overlap" {
    try expectOut(
        \\pc: block 0,0 1.3,1.2 "PC" in="next" out="addr"
        \\ok: block 0,2 2.4,1.2 "PC" in="next" out="addr"
    ,
        \\{"op":"diag","level":"warning","line":1,"msg":"block: 1.3x1.2 is too small for its title and ports (they will overlap); try at least 1.8x1.2"}
        \\{"op":"block","id":"pc","line":1,"par":false,"dur":0.6,"at":[0,0,0],"size":[1.3,1.2,0],"text":"PC","in":"next","out":"addr"}
        \\{"op":"block","id":"ok","line":2,"par":false,"dur":0.6,"at":[0,2,0],"size":[2.4,1.2,0],"text":"PC","in":"next","out":"addr"}
        \\
    );
}

test "board: resizing widens the lint bounds and survives clear" {
    try expectOut(
        \\board 24,13.5,6
        \\clear
        \\text 10,5.5 "fits now"
        \\board
        \\text 10,-5.5 "too far"
        \\board 50,9
    ,
        \\{"op":"board","line":1,"par":false,"dur":0.8,"size":[24,13.5,6]}
        \\{"op":"clear","line":2,"par":false,"dur":0.5}
        \\{"op":"text","id":"_1","line":3,"par":false,"dur":0.5,"at":[10,5.5,0],"text":"fits now"}
        \\{"op":"board","line":4,"par":false,"dur":0.8}
        \\{"op":"diag","level":"warning","line":5,"msg":"text: about 1.6x0.7, spanning x 9.2..10.8, y -5.9..-5.1: it runs off the right and bottom of the board (x -8..8, y -4.5..4.5); move it inward, shorten it or reduce size="}
        \\{"op":"text","id":"_2","line":5,"par":false,"dur":0.5,"at":[10,-5.5,0],"text":"too far"}
        \\{"op":"diag","level":"error","line":6,"msg":"board: width 50 is out of range (8..40); the default is 16,9 (and depth 0)"}
        \\
    );
}

test "par blocks: every command after the first starts with it" {
    try expectOut(
        \\a: pt 0,0
        \\par  # all at once
        \\move a 1,1
        \\# a comment doesn't count as the first
        \\b: pt 2,2
        \\c: code 0,-3 <<END
        \\x = 1
        \\END
        \\end
        \\pt 3,3
        \\end
        \\par
        \\par
    ,
        \\{"op":"pt","id":"a","line":1,"par":false,"dur":0.3,"at":[0,0,0]}
        \\{"op":"move","line":3,"par":false,"dur":0.6,"target":"a","to":[1,1,0]}
        \\{"op":"pt","id":"b","line":5,"par":true,"dur":0.3,"at":[2,2,0]}
        \\{"op":"code","id":"c","line":6,"par":true,"dur":0.6,"at":[0,-3,0],"text":"x = 1"}
        \\{"op":"pt","id":"_1","line":10,"par":false,"dur":0.3,"at":[3,3,0]}
        \\{"op":"diag","level":"error","line":11,"msg":"end: no par block is open"}
        \\{"op":"diag","level":"error","line":13,"msg":"par: a par block is already open (since line 12); par blocks don't nest"}
        \\{"op":"diag","level":"warning","line":12,"msg":"par block never closed: end it with a line containing only end"}
        \\
    );
}

test "par blocks close at the end of a mixed-mode fence" {
    const got = try runLines(
        \\Look.
        \\```vcl
        \\par
        \\a: pt 0,0
        \\b: pt 1,1
        \\```
        \\Done.
        \\
    , .mixed);
    defer testing.allocator.free(got);
    try testing.expect(std.mem.indexOf(u8, got, "\"id\":\"b\",\"line\":5,\"par\":true") != null);
    try testing.expect(std.mem.indexOf(u8, got, "par block still open at the end of the ```vcl fence") != null);
}

test "charts and diagrams: data strings become arrays, pins and parts" {
    try expectOut(
        \\n: nn 0,0 "196,4" max=6 pairs=true
        \\hl n.L0_196 n.L1_2e
        \\color n.E1 red
        \\fade n.L1_9 0.2
        \\hl n
        \\c: circle 5,3 0.5
        \\color c.top red
        \\g: grid 6,-3 2,2 "1 2 3 4"
        \\grid 6,-3 2,2 "1 2 3"
        \\set g text="4 3 2 1"
        \\b: bits -5,-3 "s|ii|ff" labels="sign,int,frac" values="1|01|10"
        \\hl b.int b.b0 b.F3
        \\bits 0,0 "s|ii" values="1|0"
    ,
        \\{"op":"nn","id":"n","line":1,"par":false,"dur":1.2,"at":[0,0,0],"layers":"196,4","max":6,"pairs":true,"sizes":[196,4],"shown":[[1,2,195,196],[1,2,3,4]]}
        \\{"op":"hl","line":2,"par":false,"dur":0.8,"ids":["n.L0_196","n.L1_2e"]}
        \\{"op":"color","line":3,"par":false,"dur":0.4,"target":"n.E1","color":"#ef5350"}
        \\{"op":"diag","level":"error","line":4,"msg":"fade: 'n' has no part 'L1_9' (did you mean 'L1_1'?)"}
        \\{"op":"hl","line":5,"par":false,"dur":0.8,"ids":["n"]}
        \\{"op":"circle","id":"c","line":6,"par":false,"dur":0.6,"at":[5,3,0],"r":0.5}
        \\{"op":"diag","level":"error","line":7,"msg":"color: 'c' is a circle; only parts of nn, grid, bits, bars and hist can be targeted (name.part)"}
        \\{"op":"grid","id":"g","line":8,"par":false,"dur":0.8,"at":[6,-3,0],"dims":[2,2,0],"values":"1 2 3 4","cells":[1,2,3,4]}
        \\{"op":"diag","level":"error","line":9,"msg":"grid: 2x2 needs 4 values, got 3"}
        \\{"op":"set","line":10,"par":false,"dur":0.5,"target":"g","text":"4 3 2 1"}
        \\{"op":"bits","id":"b","line":11,"par":false,"dur":0.8,"at":[-5,-3,0],"pattern":"s|ii|ff","labels":"sign,int,frac","values":"1|01|10","widths":[1,2,2]}
        \\{"op":"hl","line":12,"par":false,"dur":0.8,"ids":["b.int","b.b0","b.F3"]}
        \\{"op":"diag","level":"error","line":13,"msg":"bits: values \"1|0\" must match the fields of \"s|ii\" (same | groups, one digit per bit)"}
        \\
    );
    try expectOut(
        \\ax: axes 0,0 x=0..4 y=0..5
        \\b: bars ax "3 4,2/1" labels="a,b"
        \\bars ax "1 2" labels="a"
        \\h: hist ax "1 1 2 9" bins=2 range=0..4
        \\scatter ax "1,2 3,4"
        \\errbar ax "1,0.5..1.5 2,2,1.5..2.5"
        \\errbar ax "1,2..1"
        \\band ax
        \\band ax y=1..2
        \\hl b.B2 h.B1
        \\del ax
        \\hl b
    ,
        \\{"op":"axes","id":"ax","line":1,"par":false,"dur":1,"at":[0,0,0],"x":[0,4],"y":[0,5]}
        \\{"op":"bars","id":"b","line":2,"par":false,"dur":0.8,"axes":"ax","data":"3 4,2/1","labels":"a,b","bars":[[1,[3]],[4,[2,1]]]}
        \\{"op":"diag","level":"error","line":3,"msg":"bars: 1 labels for 2 bars"}
        \\{"op":"diag","level":"warning","line":4,"msg":"hist: 1 of 4 values fall outside the range 0..4"}
        \\{"op":"hist","id":"h","line":4,"par":false,"dur":0.8,"axes":"ax","data":"1 1 2 9","bins":2,"range":[0,4],"bars":[[1,[2]],[3,[1]]],"bw":2}
        \\{"op":"scatter","id":"_1","line":5,"par":false,"dur":0.6,"axes":"ax","data":"1,2 3,4","pts":[[1,2,0],[3,4,0]]}
        \\{"op":"errbar","id":"_2","line":6,"par":false,"dur":0.6,"axes":"ax","data":"1,0.5..1.5 2,2,1.5..2.5","bars":[[1,null,0.5,1.5],[2,2,1.5,2.5]]}
        \\{"op":"diag","level":"error","line":7,"msg":"errbar: interval '2..1' must go from low to high"}
        \\{"op":"diag","level":"error","line":8,"msg":"band: give x=lo..hi (a vertical strip), y=lo..hi (a horizontal one) or both"}
        \\{"op":"band","id":"_3","line":9,"par":false,"dur":0.5,"axes":"ax","y":[1,2]}
        \\{"op":"hl","line":10,"par":false,"dur":0.8,"ids":["b.B2","h.B1"]}
        \\{"op":"del","line":11,"par":false,"dur":0.3,"ids":["ax"]}
        \\{"op":"diag","level":"error","line":12,"msg":"hl: unknown name 'b'"}
        \\
    );
}

test "log axes and listed ticks" {
    var scene = Scene.init(testing.allocator);
    defer scene.deinit();
    var st = Stream.init(.vcl);
    defer st.deinit(testing.allocator);
    var aw: Writer.Allocating = .init(testing.allocator);
    defer aw.deinit();
    try st.feed(&scene,
        \\ax: axes 0,0 x=0.01..100 y=0.1..10 xlog=true ylog=true xticks="0.5,1,2,1000"
        \\plot ax "sqrt(x)"
        \\b: axes 0,0 x=0..1 ylog=true
        \\contour ax "x*y"
        \\c: axes 0,0 yticks="1,a"
        \\
    , &aw.writer);
    const out = aw.written();
    try testing.expect(std.mem.indexOf(u8, out, "\"xlog\":true,\"ylog\":true,\"xticks\":[0.5,1,2,1000]") != null);
    try testing.expect(std.mem.indexOf(u8, out, "1 of the xticks fall outside x=0.01..100") != null);
    try testing.expect(std.mem.indexOf(u8, out, "ylog=true needs a positive y range") != null);
    try testing.expect(std.mem.indexOf(u8, out, "contour: log axes aren't supported") != null);
    try testing.expect(std.mem.indexOf(u8, out, "'yticks' takes true/false or a list of numbers") != null);
    // Sampled evenly in log x: the first steps are tiny (0.01 → 0.010155), and y is kept to 0.1..10.
    var lines = std.mem.splitScalar(u8, out, '\n');
    const plot = while (lines.next()) |l| {
        if (std.mem.startsWith(u8, l, "{\"op\":\"plot\"")) break l;
    } else return error.TestExpectedEqual;
    try testing.expect(std.mem.indexOf(u8, plot, "\"polys\":[[[0.01,0.1,0],[0.010155,0.10077,0]") != null);
}

test "contour and step plots are sampled" {
    var scene = Scene.init(testing.allocator);
    defer scene.deinit();
    var st = Stream.init(.vcl);
    defer st.deinit(testing.allocator);
    var aw: Writer.Allocating = .init(testing.allocator);
    defer aw.deinit();
    try st.feed(&scene,
        \\ax: axes 0,0 x=-2..2 y=-2..2
        \\contour ax "x^2+y^2" levels=1 res=8
        \\plot ax "floor(x)" step=true x=-1..1
        \\contour ax "1"
        \\
    , &aw.writer);
    var lines = std.mem.splitScalar(u8, aw.written(), '\n');
    _ = lines.next();
    // One level halfway (z=4): a single closed loop (first point repeated at the end).
    const c = lines.next().?;
    const parsed = try std.json.parseFromSlice(std.json.Value, testing.allocator, c, .{});
    defer parsed.deinit();
    const levels = parsed.value.object.get("contours").?.array.items;
    try testing.expectEqual(@as(usize, 1), levels.len);
    try testing.expectEqual(@as(i64, 4), levels[0].object.get("z").?.integer);
    const polys = levels[0].object.get("polys").?.array.items;
    try testing.expectEqual(@as(usize, 1), polys.len);
    const pts = polys[0].array.items;
    try testing.expect(pts.len > 8);
    const num = struct {
        fn of(v: std.json.Value) f64 {
            return switch (v) {
                .integer => |i| @floatFromInt(i),
                .float => |f| f,
                else => std.math.nan(f64),
            };
        }
    }.of;
    for (0..2) |k| try testing.expectEqual(num(pts[0].array.items[k]), num(pts[pts.len - 1].array.items[k]));
    // floor(x) on -1..1: flat at -1, a riser at 0, flat at 0, the jump to 1 at the end (corners only).
    try testing.expect(std.mem.indexOf(u8, lines.next().?, "\"polys\":[[[-1,-1,0],[0,-1,0],[0,0,0],[1,0,0],[1,1,0]]]") != null);
    try testing.expectEqualStrings(
        \\{"op":"diag","level":"error","line":4,"msg":"contour: \"1\" is constant or undefined over this range: no levels to draw"}
    , lines.next().?);
}

test "questions: quiz, pick, estimate, recall; answers are checked" {
    try expectOut(
        \\n: nn 0,0 "3,2" edges=none
        \\q1: quiz "Slope?" "Area" "Slope" answer=2 explain="Rate."
        \\quiz "Primes" answer=1,3 <<END
        \\2
        \\4
        \\5
        \\END
        \\pick "Output layer?" n.L0 n.L1 answer=2
        \\estimate "Neurons?" 0..20 answer=5 tol=1 unit="n"
        \\recall "Overfitting?" "Fitting noise."
        \\quiz "Q" "A" "B" answer=3
        \\quiz "Q" "A" "B"
        \\estimate "Q" 0..1 answer=2
        \\hl q1
    ,
        \\{"op":"nn","id":"n","line":1,"par":false,"dur":1.2,"at":[0,0,0],"layers":"3,2","edges":"none","sizes":[3,2],"shown":[[1,2,3],[1,2]]}
        \\{"op":"quiz","id":"q1","line":2,"par":false,"dur":0,"q":"Slope?","options":["Area","Slope"],"answer":[2],"explain":"Rate."}
        \\{"op":"quiz","line":3,"par":false,"dur":0,"q":"Primes","options":["2","4","5"],"answer":[1,3]}
        \\{"op":"pick","line":8,"par":false,"dur":0,"q":"Output layer?","ids":["n.L0","n.L1"],"answer":[2]}
        \\{"op":"estimate","line":9,"par":false,"dur":0,"q":"Neurons?","range":[0,20],"answer":5,"tol":1,"unit":"n"}
        \\{"op":"recall","line":10,"par":false,"dur":0,"q":"Overfitting?","answer":"Fitting noise."}
        \\{"op":"diag","level":"error","line":11,"msg":"quiz: answer 3 isn't one of the choices (1..2)"}
        \\{"op":"diag","level":"error","line":12,"msg":"quiz: needs answer=N, the number of the right choice (1 is the first; answer=1,3 if several are right)"}
        \\{"op":"diag","level":"error","line":13,"msg":"estimate: answer 2 is outside the slider's range 0..1"}
        \\{"op":"diag","level":"error","line":14,"msg":"hl: unknown name 'q1'"}
        \\
    );
}

test "pick takes any pin: table rows, code lines, scatter points, part pins" {
    try expectOut(
        \\t: table 0,0 "a|b" "1|2" "3|4"
        \\R1: part 4,0 resistor
        \\pick "Which row?" t.R1 t.R2 R1.a answer=2
        \\pick "Q" t.R9 t.R1 answer=1
        \\pick "Q" R1.q t.R1 answer=1
        \\pick "Q" t.R1 nope.a answer=1
    ,
        \\{"op":"table","id":"t","line":1,"par":false,"dur":0.6,"at":[0,0,0],"rows":["a|b","1|2","3|4"]}
        \\{"op":"part","id":"R1","line":2,"par":false,"dur":0.5,"at":[4,0,0],"kind":"resistor"}
        \\{"op":"pick","line":3,"par":false,"dur":0,"q":"Which row?","ids":["t.R1","t.R2","R1.a"],"answer":[2]}
        \\{"op":"diag","level":"error","line":4,"msg":"pick: 't' has no pin 'R9'"}
        \\{"op":"diag","level":"error","line":5,"msg":"pick: 'R1' has no pin 'q'"}
        \\{"op":"diag","level":"error","line":6,"msg":"pick: unknown name 'nope'"}
        \\
    );
}
