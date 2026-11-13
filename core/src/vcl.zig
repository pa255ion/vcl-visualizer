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
    /// For `.word`: the accepted values.
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
    // graphs
    .{ .name = "axes", .kind = .draw, .pos = &.{P("at", .point)}, .keys = &.{
        P("x", .range),      P("y", .range),      P("z", .range),       P("size", .vec),
        P("grid", .boolean), P("ticks", .boolean), P("xlabel", .str),  P("ylabel", .str),
        P("zlabel", .str),
    }, .dur = 1.0 },
    .{ .name = "plot", .kind = .draw, .pos = &.{ .{ .name = "axes", .ty = .id, .of_op = "axes" }, .{ .name = "exprs", .ty = .str, .many = true, .max = 3 } }, .keys = &.{ P("x", .range), P("t", .range), width }, .dur = 1.2 },
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
    ref: struct { id: []const u8, anchor: Anchor, off: Vec3 },
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
        for (s.names.keys()) |k| s.gpa.free(k);
        s.names.deinit(s.gpa);
    }

    pub fn reset(s: *Scene) void {
        for (s.names.keys()) |k| s.gpa.free(k);
        s.names.clearRetainingCapacity();
    }

    fn add(s: *Scene, name: []const u8, op: []const u8, parent: ?[]const u8) !void {
        if (s.names.contains(name)) s.remove(name);
        const key = try s.gpa.dupe(u8, name);
        errdefer s.gpa.free(key);
        const parent_key: ?[]const u8 = if (parent) |p| (s.names.getKey(p) orelse null) else null;
        try s.names.put(s.gpa, key, .{ .op = op, .parent = parent_key });
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
        _ = s.names.orderedRemove(key);
        s.gpa.free(key);
    }

    fn setParent(s: *Scene, child: []const u8, parent: []const u8) void {
        const pk = s.names.getKey(parent) orelse return;
        if (s.names.getPtr(child)) |e| e.parent = pk;
    }

    /// Parse and execute one complete line, writing IR/diagnostic JSON lines.
    pub fn command(s: *Scene, text: []const u8, line_no: u32, out: *Writer) Writer.Error!void {
        var arena_state = std.heap.ArenaAllocator.init(s.gpa);
        defer arena_state.deinit();
        var p: LineParser = .{ .scene = s, .arena = arena_state.allocator(), .line_no = line_no, .out = out };
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
                    } else if (ch == '"') in_str = false;
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

        // Positionals.
        const Field = struct { name: []const u8, val: Value };
        var fields: std.ArrayList(Field) = .empty;
        var pi: usize = 0;
        for (op.pos) |param| {
            if (param.many) {
                const rest = positional.items[pi..];
                if (rest.len < param.min) return p.fail("{s}: expected at least {d} {s} for '{s}', got {d}{s}", .{
                    op.name, param.min, tyName(param.ty), param.name, rest.len, usageHint(op),
                });
                if (rest.len > param.max) return p.fail("{s}: at most {d} values for '{s}', got {d}", .{ op.name, param.max, param.name, rest.len });
                const list = try p.arena.alloc(Value, rest.len);
                for (rest, 0..) |t, i| list[i] = try p.value(param, t.text);
                try fields.append(p.arena, .{ .name = param.name, .val = .{ .list = list } });
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
            try p.scene.add(i, op.name, on);
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
        switch (param.ty) {
            .num => return .{ .num = parseNum(text) orelse
                return p.fail("{s}: expected a number for '{s}', got '{s}'", .{ op, param.name, text }) },
            .vec => return .{ .vec = parseVec(text) orelse
                return p.fail("{s}: expected a vector like 1,2 or 1,2,3 for '{s}', got '{s}'", .{ op, param.name, text }) },
            .range => {
                const dots = std.mem.indexOf(u8, text, "..") orelse
                    return p.fail("{s}: expected a range like -3..3 for '{s}', got '{s}'", .{ op, param.name, text });
                const a = parseNum(text[0..dots]);
                const b = parseNum(text[dots + 2 ..]);
                if (a == null or b == null) return p.fail("{s}: expected a range like -3..3 for '{s}', got '{s}'", .{ op, param.name, text });
                if (!(a.? < b.?)) return p.fail("{s}: range '{s}' must go from low to high", .{ op, text });
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
                var anchor: Anchor = .center;
                if (std.mem.indexOfScalar(u8, rest, '.')) |dot| {
                    const a = rest[dot + 1 ..];
                    anchor = std.meta.stringToEnum(Anchor, a) orelse {
                        const names = comptime std.meta.fieldNames(Anchor);
                        if (suggest(a, names)) |s| return p.fail("{s}: unknown anchor '{s}' in '{s}' (did you mean '{s}'?)", .{ op, a, text, s });
                        return p.fail("{s}: unknown anchor '{s}' in '{s}'", .{ op, a, text });
                    };
                    rest = rest[0..dot];
                }
                if (!isIdent(rest)) return p.fail("{s}: expected a point like 1,2 or a name for '{s}', got '{s}'", .{ op, param.name, text });
                _ = try p.lookup(rest);
                return .{ .point = .{ .ref = .{ .id = rest, .anchor = anchor, .off = off } } };
            },
        }
    }

    fn lookup(p: *LineParser, name: []const u8) Error!Entity {
        if (p.scene.names.get(name)) |e| return e;
        if (suggest(name, p.scene.names.keys())) |s|
            return p.fail("{s}: unknown name '{s}' (did you mean '{s}'?)", .{ p.op.name, name, s });
        return p.fail("{s}: unknown name '{s}'", .{ p.op.name, name });
    }
};

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

    pub fn init(mode: Mode) Stream {
        return .{ .mode = mode };
    }

    pub fn deinit(st: *Stream, gpa: Allocator) void {
        st.pending.deinit(gpa);
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

    /// Execute any final unterminated line.
    pub fn finish(st: *Stream, scene: *Scene, out: *Writer) (Writer.Error || Allocator.Error)!void {
        if (st.pending.items.len == 0) return;
        const full = try scene.gpa.dupe(u8, st.pending.items);
        defer scene.gpa.free(full);
        st.pending.clearRetainingCapacity();
        try st.line(scene, full, out);
    }

    pub fn line(st: *Stream, scene: *Scene, raw: []const u8, out: *Writer) Writer.Error!void {
        st.line_no += 1;
        const text = std.mem.trimEnd(u8, raw, "\r");
        if (st.mode == .vcl) return scene.command(text, st.line_no, out);

        const t = std.mem.trim(u8, text, " \t");
        if (std.mem.startsWith(u8, t, "```")) {
            const lang = std.mem.trim(u8, t[3..], " \t");
            st.fence = switch (st.fence) {
                .none => if (std.mem.eql(u8, lang, "vcl")) .vcl else .other,
                else => .none,
            };
            return;
        }
        switch (st.fence) {
            .vcl => return scene.command(text, st.line_no, out),
            .other => return,
            .none => {},
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
};

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
                try w.print(",\"anchor\":\"{s}\",\"off\":", .{@tagName(r.anchor)});
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
