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

pub const version = "0.1";

// ───────────────────────────── schema ─────────────────────────────

pub const Ty = enum { num, vec, point, str, color, range, boolean, id, word };

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
};

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
    .{ .name = "del", .kind = .flow, .pos = &.{.{ .name = "ids", .ty = .id, .many = true }}, .dur = 0.3 },
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
        P("grid", .boolean), P("ticks", .boolean), P("xlabel", .str),  P("ylabel", .str),
        P("zlabel", .str),
    }, .dur = 1.0 },
    .{ .name = "plot", .kind = .draw, .pos = &.{ .{ .name = "axes", .ty = .id, .of_op = "axes" }, .{ .name = "exprs", .ty = .str, .many = true, .max = 3 } }, .keys = &.{ P("x", .range), P("t", .range), width, dash }, .dur = 1.2 },
    .{ .name = "surf", .kind = .draw, .pos = &.{ .{ .name = "axes", .ty = .id, .of_op = "axes" }, P("expr", .str) }, .keys = &.{ P("x", .range), P("y", .range), P("res", .num), fill, wire }, .dur = 1.2 },
    // 3D
    .{ .name = "box", .kind = .draw, .pos = &.{ P("at", .point), P("size", .vec) }, .keys = &.{wire}, .dur = 0.6 },
    .{ .name = "sphere", .kind = .draw, .pos = &.{ P("at", .point), P("r", .num) }, .keys = &.{wire}, .dur = 0.6 },
    .{ .name = "cyl", .kind = .draw, .pos = &.{ P("from", .point), P("to", .point), P("r", .num) }, .keys = &.{wire}, .dur = 0.6 },
    .{ .name = "model", .kind = .draw, .pos = &.{ P("at", .point), P("url", .str) }, .keys = &.{size}, .dur = 0.8 },
    // change
    .{ .name = "move", .kind = .change, .pos = &.{ P("target", .id), P("to", .point) }, .dur = 0.6 },
    .{ .name = "rot", .kind = .change, .pos = &.{ P("target", .id), P("rot", .vec) }, .dur = 0.6 },
    .{ .name = "scale", .kind = .change, .pos = &.{ P("target", .id), P("scale", .num) }, .dur = 0.5 },
    .{ .name = "color", .kind = .change, .pos = &.{ P("target", .id), P("color", .color) }, .dur = 0.4 },
    .{ .name = "fade", .kind = .change, .pos = &.{ P("target", .id), P("opacity", .num) }, .dur = 0.4 },
    .{ .name = "set", .kind = .change, .pos = &.{P("target", .id)}, .keys = &.{
        P("pos", .point), P("rot", .vec), P("scale", .num), P("color", .color), P("opacity", .num), P("text", .str),
        P("focus", .range),
    }, .dur = 0.5 },
    .{ .name = "hl", .kind = .change, .pos = &.{.{ .name = "ids", .ty = .id, .many = true }}, .keys = &.{P("color", .color)}, .dur = 0.8, .replay = false },
    .{ .name = "spin", .kind = .change, .pos = &.{ P("target", .id), P("rate", .vec) }, .dur = 0 },
    .{ .name = "group", .kind = .flow, .pos = &.{.{ .name = "ids", .ty = .id, .many = true }}, .dur = 0, .needs_name = true },
};

pub fn findOp(name: []const u8) ?*const Op {
    for (&ops) |*op| if (std.mem.eql(u8, op.name, name)) return op;
    return null;
}

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

    fn hasPort(e: Entity, name: []const u8) bool {
        var it = std.mem.splitScalar(u8, e.ports, ',');
        while (it.next()) |p| if (p.len > 0 and std.mem.eql(u8, p, name)) return true;
        return false;
    }
};

/// Names that currently exist. Shared by every producer feeding one scene.
pub const Scene = struct {
    gpa: Allocator,
    names: std.StringArrayHashMapUnmanaged(Entity) = .empty,
    auto_id: u32 = 0,

    pub fn init(gpa: Allocator) Scene {
        return .{ .gpa = gpa };
    }

    pub fn deinit(s: *Scene) void {
        s.freeAll();
        s.names.deinit(s.gpa);
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
    pub fn say(s: *Scene, text: []const u8, line_no: u32, out: *Writer) Writer.Error!void {
        _ = s;
        try out.print("{{\"op\":\"say\",\"line\":{d},\"par\":false,\"dur\":", .{line_no});
        try writeNum(out, speechDuration(text));
        try out.writeAll(",\"text\":");
        try writeJsonString(out, text);
        try out.writeAll("}\n");
    }
};

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

    fn run(p: *LineParser, line: []const u8) Error!void {
        var toks = try p.tokenize(line);
        if (toks.len == 0) return;

        var par = false;
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
            if (op.kind != .draw and !op.needs_name)
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

        // Op-specific checks.
        if (std.mem.eql(u8, op.name, "wait")) {
            if (fields.items[0].val.num < 0) return p.fail("wait: seconds must be >= 0", .{});
            dur = fields.items[0].val.num;
        }
        if (std.mem.eql(u8, op.name, "say") and !hasKey(keyed.items, "dur")) dur = speechDuration(fields.items[0].val.str);
        if (on == null) try p.lintBounds(fields.items);
        if (std.mem.eql(u8, op.name, "group")) {
            for (fields.items[0].val.list) |v| if (std.mem.eql(u8, v.id, name.?))
                return p.fail("group: '{s}' can't contain itself", .{v.id});
        }

        // Effects on the scene.
        var id_buf: [16]u8 = undefined;
        const id: ?[]const u8 = if (op.kind == .draw or op.needs_name) name orelse blk: {
            p.scene.auto_id += 1;
            break :blk std.fmt.bufPrint(&id_buf, "_{d}", .{p.scene.auto_id}) catch unreachable;
        } else null;

        if (std.mem.eql(u8, op.name, "clear")) {
            p.scene.reset();
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
                    var dup = std.mem.splitScalar(u8, all.items, ',');
                    const seen = while (dup.next()) |d| {
                        if (std.mem.eql(u8, d, pin)) break true;
                    } else false;
                    if (seen) {
                        try p.warn("wave: signals '{s}' and an earlier one share the pin '{s}'; only the first gets it", .{ nm, pin });
                        continue;
                    }
                    if (all.items.len > 0) try all.append(p.arena, ',');
                    try all.appendSlice(p.arena, pin);
                };
                ports = all.items;
            }
            try p.scene.add(i, op.name, on, ports);
            if (std.mem.eql(u8, op.name, "group"))
                for (fields.items[0].val.list) |v| p.scene.setParent(v.id, i);
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
        try o.writeAll("}\n");
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
    fn lintBounds(p: *LineParser, fields: anytype) Error!void {
        const op = p.op.name;
        const at = for (fields) |f| {
            if (std.mem.eql(u8, f.name, "at")) switch (f.val) {
                .point => |pt| switch (pt) {
                    .abs => |v| break v,
                    else => return,
                },
                else => return,
            };
        } else return;
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
        } else if (std.mem.eql(u8, op, "code")) {
            const s = num.get(fields, "size") orelse 0.36;
            const l = num.lines(fieldStr(fields, "text") orelse "");
            const gutter: usize = std.math.log10_int(@max(l.n, 1)) + 3; // digits + 2
            w = f(l.w + gutter + 2) * 0.43 * s;
            h = (f(l.n) + 0.86 + (if (fieldStr(fields, "title") != null) @as(f64, 1.2) else 0)) * s;
            x0 = at[0] - w / 2;
        } else if (std.mem.eql(u8, op, "table")) {
            const s = num.get(fields, "size") orelse 0.5;
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
        } else return;
        const tol = 0.05;
        const x1 = x0 + w;
        const y0 = at[1] - h / 2;
        const y1 = at[1] + h / 2;
        var edges: [4][]const u8 = undefined;
        var ne: usize = 0;
        if (x0 < -8 - tol) {
            edges[ne] = "left";
            ne += 1;
        }
        if (x1 > 8 + tol) {
            edges[ne] = "right";
            ne += 1;
        }
        if (y1 > 4.5 + tol) {
            edges[ne] = "top";
            ne += 1;
        }
        if (y0 < -4.5 - tol) {
            edges[ne] = "bottom";
            ne += 1;
        }
        if (ne == 0) return;
        const which = try std.mem.join(p.arena, " and ", edges[0..ne]);
        const fix = if (w > 16 or h > 9) "make it smaller (size=) or split it" else "move it inward, shorten it or reduce size=";
        try p.warn("{s}: about {d:.1}x{d:.1}, spanning x {d:.1}..{d:.1}, y {d:.1}..{d:.1}: it runs off the {s} of the board (x -8..8, y -4.5..4.5); {s}", .{ op, w, h, x0, x1, y0, y1, which, fix });
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
        if (blockStart(text)) |b| {
            try st.here_tag.appendSlice(scene.gpa, b.tag);
            try st.here_head.appendSlice(scene.gpa, b.head);
            st.here_line = st.line_no;
            return;
        }
        return scene.command(text, st.line_no, out);
    }
};

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
        \\t: table 3,0 <<ROWS
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
        \\{"op":"table","id":"t","line":9,"par":false,"dur":0.6,"at":[3,0,0],"rows":["A|B|Y","0|0|1","1|1|0"]}
        \\{"op":"say","line":16,"par":false,"dur":1.2,"text":"Done."}
        \\
    , got);
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
        \\t: table 0,0 "A|B" "0|1"
        \\point t.R1
        \\w: wave 0,0 "clk: p.p." "data[7:0]: =.= | A B"
        \\point w.clk
    ,
        \\{"op":"code","id":"c","line":1,"par":false,"dur":0.6,"at":[0,0,0],"text":"a\nb\nc"}
        \\{"op":"point","line":2,"par":false,"dur":0.4,"at":{"ref":"c","anchor":"L3","off":[0,0,0]}}
        \\{"op":"diag","level":"error","line":3,"msg":"point: 'c' has no pin 'L4'; its pins are L1, L2, L3"}
        \\{"op":"table","id":"t","line":4,"par":false,"dur":0.6,"at":[0,0,0],"rows":["A|B","0|1"]}
        \\{"op":"point","line":5,"par":false,"dur":0.4,"at":{"ref":"t","anchor":"R1","off":[0,0,0]}}
        \\{"op":"wave","id":"w","line":6,"par":false,"dur":1,"at":[0,0,0],"rows":["clk: p.p.","data[7:0]: =.= | A B"]}
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
        \\w: wave 0,0 "FIFO write: 0.1" "d[7:0]: =.= | A B" "top: 1.."
        \\point w.FIFO_write
        \\point w.d
        \\wave 0,0 "a: 01q"
    ,
        \\{"op":"diag","level":"warning","line":1,"msg":"wave: signal 'top' gets no pin (point at it with an offset from the wave instead, or rename it)"}
        \\{"op":"wave","id":"w","line":1,"par":false,"dur":1,"at":[0,0,0],"rows":["FIFO write: 0.1","d[7:0]: =.= | A B","top: 1.."]}
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
        \\ok: block 0,0 2.4,1.2 "PC" in="next" out="addr"
    ,
        \\{"op":"diag","level":"warning","line":1,"msg":"block: 1.3x1.2 is too small for its title and ports (they will overlap); try at least 1.8x1.2"}
        \\{"op":"block","id":"pc","line":1,"par":false,"dur":0.6,"at":[0,0,0],"size":[1.3,1.2,0],"text":"PC","in":"next","out":"addr"}
        \\{"op":"block","id":"ok","line":2,"par":false,"dur":0.6,"at":[0,0,0],"size":[2.4,1.2,0],"text":"PC","in":"next","out":"addr"}
        \\
    );
}
