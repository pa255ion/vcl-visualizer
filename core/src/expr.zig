//! Math expressions for `plot` and `surf`, evaluated by the server so that
//! renderers receive sampled points and never need an expression evaluator.
//!
//! Same language as viewer/js/expr.js: numbers, + - * / ^ (or **), unary
//! minus, parentheses, functions, constants (pi, π, e, tau) and the variables
//! the caller allows. Implicit multiplication: "2x", "3(x+1)", "xy" (when every
//! letter of a name is a variable).

const std = @import("std");
const Allocator = std.mem.Allocator;

pub const Vars = struct { x: f64 = 0, y: f64 = 0, t: f64 = 0 };
const Var = enum { x, y, t };

const Func = enum {
    sin, cos, tan, asin, acos, atan, atan2, sinh, cosh, tanh, sqrt, cbrt, abs, exp, ln, log,
    log10, log2, floor, ceil, round, min, max, pow, sign, mod, sec, csc, cot,

    fn arity(f: Func) struct { u8, u8 } {
        return switch (f) {
            .atan2, .pow, .mod => .{ 2, 2 },
            .min, .max => .{ 1, 255 },
            else => .{ 1, 1 },
        };
    }
};

const Node = union(enum) {
    num: f64,
    variable: Var,
    neg: *const Node,
    bin: struct { op: u8, a: *const Node, b: *const Node },
    call: struct { f: Func, args: []const *const Node },
    /// "xy": the product of single-letter variables.
    product: []const Var,
};

pub const Expr = struct {
    root: *const Node,

    pub fn eval(e: Expr, v: Vars) f64 {
        return evalNode(e.root, v);
    }
};

fn evalNode(n: *const Node, v: Vars) f64 {
    return switch (n.*) {
        .num => |x| x,
        .variable => |x| get(v, x),
        .neg => |a| -evalNode(a, v),
        .bin => |b| {
            const x = evalNode(b.a, v);
            const y = evalNode(b.b, v);
            return switch (b.op) {
                '+' => x + y,
                '-' => x - y,
                '*' => x * y,
                '/' => x / y,
                '^' => jsPow(x, y),
                else => unreachable,
            };
        },
        .call => |c| {
            var buf: [8]f64 = undefined;
            const n_args = @min(c.args.len, buf.len);
            for (c.args[0..n_args], 0..) |a, i| buf[i] = evalNode(a, v);
            const args = buf[0..n_args];
            const x = args[0];
            return switch (c.f) {
                .sin => @sin(x),
                .cos => @cos(x),
                .tan => @tan(x),
                .asin => std.math.asin(x),
                .acos => std.math.acos(x),
                .atan => std.math.atan(x),
                .atan2 => std.math.atan2(x, args[1]),
                .sinh => std.math.sinh(x),
                .cosh => std.math.cosh(x),
                .tanh => std.math.tanh(x),
                .sqrt => if (x < 0) std.math.nan(f64) else @sqrt(x),
                .cbrt => std.math.cbrt(x),
                .abs => @abs(x),
                .exp => @exp(x),
                .ln, .log => if (x < 0) std.math.nan(f64) else @log(x),
                .log10 => if (x < 0) std.math.nan(f64) else @log10(x),
                .log2 => if (x < 0) std.math.nan(f64) else @log2(x),
                .floor => @floor(x),
                .ceil => @ceil(x),
                .round => @floor(x + 0.5), // JS Math.round
                .min => blk: {
                    var m = x;
                    for (args[1..]) |a| m = if (std.math.isNan(a) or std.math.isNan(m)) std.math.nan(f64) else @min(m, a);
                    break :blk m;
                },
                .max => blk: {
                    var m = x;
                    for (args[1..]) |a| m = if (std.math.isNan(a) or std.math.isNan(m)) std.math.nan(f64) else @max(m, a);
                    break :blk m;
                },
                .pow => jsPow(x, args[1]),
                .sign => if (std.math.isNan(x)) x else if (x > 0) 1 else if (x < 0) -1 else x,
                .mod => @rem(@rem(x, args[1]) + args[1], args[1]), // JS ((a % b) + b) % b
                .sec => 1 / @cos(x),
                .csc => 1 / @sin(x),
                .cot => 1 / @tan(x),
            };
        },
        .product => |vs| blk: {
            var p: f64 = 1;
            for (vs) |x| p *= get(v, x);
            break :blk p;
        },
    };
}

fn get(v: Vars, x: Var) f64 {
    return switch (x) {
        .x => v.x,
        .y => v.y,
        .t => v.t,
    };
}

/// Math.pow: a negative base with a fractional exponent is NaN.
fn jsPow(a: f64, b: f64) f64 {
    if (a < 0 and b != @trunc(b)) return std.math.nan(f64);
    return std.math.pow(f64, a, b);
}

// ───────────────────────────── parser ─────────────────────────────

const Tok = union(enum) { num: f64, id: []const u8, op: u8 };

pub const Error = error{ Bad, OutOfMemory };

pub const Parser = struct {
    arena: Allocator,
    toks: []const Tok = &.{},
    i: usize = 0,
    vars: []const Var = &.{},
    /// Set when compile fails; fits the longest message below.
    msg_buf: [160]u8 = undefined,
    msg: []const u8 = "",

    fn fail(p: *Parser, comptime fmt: []const u8, args: anytype) Error {
        p.msg = std.fmt.bufPrint(&p.msg_buf, fmt, args) catch "invalid expression";
        return error.Bad;
    }

    fn tokenize(p: *Parser, src: []const u8) Error!void {
        var list: std.ArrayList(Tok) = .empty;
        var i: usize = 0;
        while (i < src.len) {
            const c = src[i];
            if (c == ' ' or c == '\t') {
                i += 1;
                continue;
            }
            if (std.ascii.isDigit(c) or (c == '.' and i + 1 < src.len and std.ascii.isDigit(src[i + 1]))) {
                const s = i;
                while (i < src.len and std.ascii.isDigit(src[i])) i += 1;
                if (i + 1 < src.len and src[i] == '.' and std.ascii.isDigit(src[i + 1])) {
                    i += 1;
                    while (i < src.len and std.ascii.isDigit(src[i])) i += 1;
                }
                if (i < src.len and (src[i] == 'e' or src[i] == 'E')) {
                    var j = i + 1;
                    if (j < src.len and (src[j] == '+' or src[j] == '-')) j += 1;
                    if (j < src.len and std.ascii.isDigit(src[j])) {
                        while (j < src.len and std.ascii.isDigit(src[j])) j += 1;
                        i = j;
                    }
                }
                const v = std.fmt.parseFloat(f64, src[s..i]) catch return p.fail("bad number '{s}'", .{src[s..i]});
                try list.append(p.arena, .{ .num = v });
                continue;
            }
            if (std.ascii.isAlphabetic(c) or c == '_') {
                const s = i;
                while (i < src.len and (std.ascii.isAlphanumeric(src[i]) or src[i] == '_')) i += 1;
                try list.append(p.arena, .{ .id = src[s..i] });
                continue;
            }
            if (std.mem.startsWith(u8, src[i..], "π")) {
                i += "π".len;
                try list.append(p.arena, .{ .id = "pi" });
                continue;
            }
            if (std.mem.startsWith(u8, src[i..], "**")) {
                i += 2;
                try list.append(p.arena, .{ .op = '^' });
                continue;
            }
            if (std.mem.indexOfScalar(u8, "+-*/^(),", c) != null) {
                i += 1;
                try list.append(p.arena, .{ .op = c });
                continue;
            }
            const n = std.unicode.utf8ByteSequenceLength(c) catch 1;
            return p.fail("unexpected '{s}'", .{src[i..@min(src.len, i + n)]});
        }
        p.toks = list.items;
    }

    fn peekOp(p: *Parser, c: u8) bool {
        if (p.i >= p.toks.len) return false;
        return switch (p.toks[p.i]) {
            .op => |o| o == c,
            else => false,
        };
    }

    fn expect(p: *Parser, c: u8) Error!void {
        if (p.peekOp(c)) {
            p.i += 1;
            return;
        }
        if (p.i >= p.toks.len) return p.fail("expected '{c}' at end", .{c});
        return p.fail("expected '{c}' near '{s}'", .{ c, p.tokText(p.toks[p.i]) });
    }

    fn tokText(p: *Parser, t: Tok) []const u8 {
        return switch (t) {
            .num => |v| std.fmt.allocPrint(p.arena, "{d}", .{v}) catch "?",
            .id => |s| s,
            .op => |o| std.fmt.allocPrint(p.arena, "{c}", .{o}) catch "?",
        };
    }

    fn node(p: *Parser, n: Node) Error!*const Node {
        const ptr = try p.arena.create(Node);
        ptr.* = n;
        return ptr;
    }

    fn expr(p: *Parser) Error!*const Node {
        var a = try p.term();
        while (p.peekOp('+') or p.peekOp('-')) {
            const op = p.toks[p.i].op;
            p.i += 1;
            a = try p.node(.{ .bin = .{ .op = op, .a = a, .b = try p.term() } });
        }
        return a;
    }

    fn startsPrimary(p: *Parser) bool {
        if (p.i >= p.toks.len) return false;
        return switch (p.toks[p.i]) {
            .num, .id => true,
            .op => |o| o == '(',
        };
    }

    fn term(p: *Parser) Error!*const Node {
        var a = try p.unary();
        while (true) {
            if (p.peekOp('*') or p.peekOp('/')) {
                const op = p.toks[p.i].op;
                p.i += 1;
                a = try p.node(.{ .bin = .{ .op = op, .a = a, .b = try p.unary() } });
            } else if (p.startsPrimary()) {
                a = try p.node(.{ .bin = .{ .op = '*', .a = a, .b = try p.power() } });
            } else return a;
        }
    }

    fn unary(p: *Parser) Error!*const Node {
        if (p.peekOp('-')) {
            p.i += 1;
            return p.node(.{ .neg = try p.unary() });
        }
        if (p.peekOp('+')) {
            p.i += 1;
            return p.unary();
        }
        return p.power();
    }

    fn power(p: *Parser) Error!*const Node {
        const base = try p.primary();
        if (p.peekOp('^')) {
            p.i += 1;
            return p.node(.{ .bin = .{ .op = '^', .a = base, .b = try p.unary() } });
        }
        return base;
    }

    fn primary(p: *Parser) Error!*const Node {
        if (p.i >= p.toks.len) return p.fail("unexpected end of expression", .{});
        const t = p.toks[p.i];
        p.i += 1;
        switch (t) {
            .num => |v| return p.node(.{ .num = v }),
            .op => |o| {
                if (o == '(') {
                    const a = try p.expr();
                    try p.expect(')');
                    return a;
                }
                return p.fail("unexpected '{c}'", .{o});
            },
            .id => |name| {
                if (std.meta.stringToEnum(Func, name)) |f| {
                    if (!p.peekOp('(')) return p.fail("{s} needs parentheses, e.g. {s}(x)", .{ name, name });
                    p.i += 1;
                    var args: std.ArrayList(*const Node) = .empty;
                    try args.append(p.arena, try p.expr());
                    while (p.peekOp(',')) {
                        p.i += 1;
                        try args.append(p.arena, try p.expr());
                    }
                    try p.expect(')');
                    const lo, const hi = f.arity();
                    if (args.items.len < lo or args.items.len > hi or args.items.len > 8) {
                        if (lo == hi) return p.fail("{s} takes {d} argument{s}, got {d}", .{ name, lo, if (lo == 1) "" else "s", args.items.len });
                        return p.fail("{s} takes 1 to 8 arguments, got {d}", .{ name, args.items.len });
                    }
                    return p.node(.{ .call = .{ .f = f, .args = args.items } });
                }
                if (std.mem.eql(u8, name, "pi")) return p.node(.{ .num = std.math.pi });
                if (std.mem.eql(u8, name, "e")) return p.node(.{ .num = std.math.e });
                if (std.mem.eql(u8, name, "tau")) return p.node(.{ .num = 2 * std.math.pi });
                if (p.varOf(name)) |v| return p.node(.{ .variable = v });
                // "xy" → x*y when every letter is a variable.
                const prod = try p.arena.alloc(Var, name.len);
                for (name, 0..) |c, k| prod[k] = p.varOf(&.{c}) orelse {
                    var list: [16]u8 = undefined;
                    var n: usize = 0;
                    for (p.vars, 0..) |v, j| {
                        if (j > 0) {
                            @memcpy(list[n..][0..2], ", ");
                            n += 2;
                        }
                        list[n] = @tagName(v)[0];
                        n += 1;
                    }
                    return p.fail("unknown name '{s}' (variables: {s})", .{ name, list[0..n] });
                };
                return p.node(.{ .product = prod });
            },
        }
    }

    fn varOf(p: *Parser, name: []const u8) ?Var {
        const v = std.meta.stringToEnum(Var, name) orelse return null;
        return if (std.mem.indexOfScalar(Var, p.vars, v) != null) v else null;
    }
};

/// Compile `src` with the variables named in `vars` (e.g. "xt"). On error,
/// returns error.Bad with the message in `p.msg`. Nodes live in `p.arena`.
pub fn compile(p: *Parser, src: []const u8, vars: []const u8) Error!Expr {
    const vs = try p.arena.alloc(Var, vars.len);
    for (vars, 0..) |c, k| vs[k] = std.meta.stringToEnum(Var, &.{c}).?;
    p.vars = vs;
    p.i = 0;
    try p.tokenize(src);
    const root = try p.expr();
    if (p.i < p.toks.len) return p.fail("unexpected '{s}'", .{p.tokText(p.toks[p.i])});
    return .{ .root = root };
}

// ───────────────────────────── tests ─────────────────────────────

const testing = std.testing;

fn evalStr(src: []const u8, vars: []const u8, v: Vars) !f64 {
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    var p: Parser = .{ .arena = arena.allocator() };
    const e = try compile(&p, src, vars);
    return e.eval(v);
}

fn compileErr(src: []const u8, vars: []const u8) ![]const u8 {
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    var p: Parser = .{ .arena = arena.allocator() };
    if (compile(&p, src, vars)) |_| return error.TestUnexpectedResult else |err| {
        try testing.expectEqual(error.Bad, err);
        return try testing.allocator.dupe(u8, p.msg);
    }
}

test "expressions evaluate like viewer/js/expr.js" {
    const eq = testing.expectApproxEqAbs;
    try eq(@as(f64, 9), try evalStr("x^2", "xt", .{ .x = 3 }), 1e-12);
    try eq(@as(f64, -9), try evalStr("-x^2", "xt", .{ .x = 3 }), 1e-12);
    try eq(@as(f64, 18), try evalStr("2x^2", "xt", .{ .x = 3 }), 1e-12);
    try eq(@as(f64, 12), try evalStr("3(x+1)", "xt", .{ .x = 3 }), 1e-12);
    try eq(@as(f64, 6), try evalStr("xy", "xy", .{ .x = 2, .y = 3 }), 1e-12);
    try eq(@as(f64, 512), try evalStr("2^3^2", "x", .{}), 1e-12);
    try eq(@as(f64, 8), try evalStr("2**3", "x", .{}), 1e-12);
    try eq(@as(f64, 1), try evalStr("sin(pi/2)", "x", .{}), 1e-12);
    try eq(@as(f64, std.math.pi), try evalStr("π", "x", .{}), 1e-12);
    try eq(@as(f64, 2), try evalStr("mod(-1, 3)", "x", .{}), 1e-12);
    try eq(@as(f64, 5), try evalStr("max(1, 5, 2)", "x", .{}), 1e-12);
    try eq(@as(f64, -2), try evalStr("round(-2.5)", "x", .{}), 1e-12);
    try eq(@as(f64, 0.25), try evalStr(".5e-0 * 0.5", "x", .{}), 1e-12);
    try eq(@as(f64, 3), try evalStr("t", "xt", .{ .t = 3 }), 1e-12);
    try testing.expect(std.math.isNan(try evalStr("sqrt(-1)", "x", .{})));
    try testing.expect(std.math.isNan(try evalStr("(-8)^(1/3)", "x", .{})));
    try testing.expect(std.math.isInf(try evalStr("1/x", "x", .{})));
}

test "expression errors" {
    const cases = [_]struct { []const u8, []const u8, []const u8 }{
        .{ "x^", "xt", "unexpected end of expression" },
        .{ "sin x", "xt", "sin needs parentheses, e.g. sin(x)" },
        .{ "(x+1", "xt", "expected ')' at end" },
        .{ "x + q", "xt", "unknown name 'q' (variables: x, t)" },
        .{ "x $ 2", "xt", "unexpected '$'" },
        .{ "atan2(x)", "xt", "atan2 takes 2 arguments, got 1" },
        .{ "x)", "xt", "unexpected ')'" },
    };
    for (cases) |c| {
        const msg = try compileErr(c[0], c[1]);
        defer testing.allocator.free(msg);
        try testing.expectEqualStrings(c[2], msg);
    }
}
