//! vcl — command-line tool and live stream server.
//!
//!   vcl check [--mixed] [--design] FILE...  report diagnostics (exit 1 on errors); .md is
//!                                mixed; - is stdin; --design: lesson design advice (design.zig)
//!   vcl compile [--mixed] FILE   print the JSON IR, one command per line (- is stdin)
//!   vcl serve [--port N] [--host H] [--root DIR] [--assets DIR] [--lessons DIR]
//!             [--no-figs] [--unsafe-figs]
//!   vcl figs [--out DIR] [--unsafe-figs] FILE...   render the lessons' `fig`s
//!
//! The server:
//!   GET  /            the viewer (static files from --root, default "viewer")
//!   GET  /assets/…    files you provide (--assets DIR, default ROOT/assets):
//!                     SVGs, images, glTF/STL/OBJ models; /assets/index.json lists them
//!   GET  /library/…   a lesson library (--lessons DIR): its .vcl files and the .md
//!                     files with ```vcl fences; /library/index.json lists them
//!   GET  /ws          WebSocket: receives IR; may also send VCL text chunks
//!                     (?mode=mixed for prose+fences, ?listen=0 to only send)
//!   POST /vcl         body = VCL text (?mode=mixed, ?pace=MS between lines);
//!                     responds with plain-text diagnostics
//!   GET  /scene       current scene as {"cmds":[...]} (for late joiners/pollers)
//!   POST /ask         a learner question → broadcast {"op":"ask","text":…}
//!   POST /status      tutor state ("thinking"/"idle") → {"op":"status","text":…}
//!   POST /answer      a learner's answer to a question, a JSON object →
//!                     broadcast as {"op":"answer",…} (not replayed)
//!   GET  /figs/KEY.svg|png|json   a `fig` rendered from its code (fig.zig):
//!                     waits while it renders; else ROOT/figs/ (pre-rendered)
//!   GET  /quest/status, POST /quest/connect|start|disconnect
//!                     connect a Quest headset through adb (the Headset page,
//!                     /quest.html); only from this machine

const std = @import("std");
const builtin = @import("builtin");
const vcl = @import("vcl");
const quest = @import("quest.zig");
const fig = @import("fig.zig");
const design = @import("design.zig");
const Io = std.Io;
const Allocator = std.mem.Allocator;
const WebSocket = std.http.Server.WebSocket;

const usage =
    \\usage:
    \\  vcl check [--mixed] [--design] FILE...   report diagnostics (.md files are mixed; - reads stdin);
    \\                                 --design: also how well it teaches (docs/LESSON-DESIGN.md)
    \\  vcl compile [--mixed] FILE     print JSON IR (one command per line; - reads stdin)
    \\  vcl serve [--port 8080] [--host 127.0.0.1] [--root viewer] [--assets DIR] [--lessons DIR]
    \\            [--no-figs] [--unsafe-figs]
    \\  vcl figs [--out DIR] [--unsafe-figs] FILE...   render the figures (`fig`) of lessons
    \\
;

pub fn main(init: std.process.Init) !void {
    const io = init.io;
    const gpa = init.gpa;
    const args = try init.minimal.args.toSlice(init.arena.allocator());

    var out_buf: [8192]u8 = undefined;
    var out_w = Io.File.stdout().writer(io, &out_buf);
    const out = &out_w.interface;
    defer out.flush() catch {};

    if (args.len < 2) {
        try out.writeAll(usage);
        return;
    }
    const cmd = args[1];
    const rest = args[2..];
    if (std.mem.eql(u8, cmd, "check")) {
        const errors = try check(io, gpa, init.arena.allocator(), init.environ_map, rest, out);
        try out.flush();
        if (errors > 0) std.process.exit(1);
    } else if (std.mem.eql(u8, cmd, "compile")) {
        try compile(io, gpa, init.arena.allocator(), init.environ_map, rest, out);
    } else if (std.mem.eql(u8, cmd, "serve")) {
        try serve(io, gpa, init.arena.allocator(), rest, init.environ_map);
    } else if (std.mem.eql(u8, cmd, "figs")) {
        const failed = try figsCmd(io, gpa, init.arena.allocator(), rest, init.environ_map, out);
        try out.flush();
        if (failed > 0) std.process.exit(1);
    } else {
        try out.writeAll(usage);
        try out.flush();
        std.process.exit(2);
    }
}

// ───────────────────────────── check / compile ─────────────────────────────

const DiagJson = struct { level: []const u8, line: u32, msg: []const u8 };

/// Runs `data` commands' code (sandboxed; results cached) for a scene.
const Computer = struct {
    gpa: Allocator,
    io: Io,
    cfg: fig.Config,

    fn run(ctx: *anyopaque, lang: []const u8, code: []const u8, key: []const u8) vcl.Scene.Computed {
        const c: *Computer = @ptrCast(@alignCast(ctx));
        const r = fig.compute(c.gpa, c.io, c.cfg, lang, code, key, true) catch |err|
            return .{ .err = std.fmt.allocPrint(c.gpa, "couldn't run it: {s}", .{@errorName(err)}) catch return .missing };
        return switch (r) {
            .json => |j| .{ .json = j },
            .failed => |m| .{ .err = m },
            .missing => .missing,
        };
    }

    fn compute(c: *Computer) vcl.Scene.Compute {
        return .{ .ctx = c, .run = run };
    }
};

fn runSource(gpa: Allocator, src: []const u8, mode: vcl.Mode, computer: ?*Computer) ![]u8 {
    var scene = vcl.Scene.init(gpa);
    defer scene.deinit();
    if (computer) |c| scene.compute = c.compute();
    var st = vcl.Stream.init(mode);
    defer st.deinit(gpa);
    var aw: Io.Writer.Allocating = .init(gpa);
    errdefer aw.deinit();
    try st.feed(&scene, src, &aw.writer);
    try st.finish(&scene, &aw.writer);
    return aw.toOwnedSlice();
}

fn isDiag(line: []const u8) bool {
    return std.mem.startsWith(u8, line, "{\"op\":\"diag\"");
}

/// A file's contents, or standard input for "-".
fn readSource(io: Io, gpa: Allocator, path: []const u8) ![]u8 {
    const limit: Io.Limit = .limited(16 << 20);
    if (!std.mem.eql(u8, path, "-")) return Io.Dir.cwd().readFileAlloc(io, path, gpa, limit);
    var buf: [4096]u8 = undefined;
    var r = Io.File.stdin().reader(io, &buf);
    return r.interface.allocRemaining(gpa, limit);
}

fn check(io: Io, gpa: Allocator, arena: Allocator, env: *const std.process.Environ.Map, args: []const [:0]const u8, out: *Io.Writer) !usize {
    var computer: Computer = .{ .gpa = gpa, .io = io, .cfg = try fig.configure(arena, io, env) };
    var errors: usize = 0;
    var force_mixed = false;
    var review = false;
    for (args) |a| {
        if (std.mem.eql(u8, a, "--mixed")) force_mixed = true;
        if (std.mem.eql(u8, a, "--design")) review = true;
    }
    for (args) |path| {
        if (std.mem.eql(u8, path, "--mixed") or std.mem.eql(u8, path, "--design")) continue;
        const mode: vcl.Mode = if (force_mixed or std.mem.endsWith(u8, path, ".md")) .mixed else .vcl;
        const src = readSource(io, gpa, path) catch |err| {
            try out.print("{s}: cannot read: {s}\n", .{ path, @errorName(err) });
            errors += 1;
            continue;
        };
        defer gpa.free(src);
        const ir = try runSource(gpa, src, mode, &computer);
        defer gpa.free(ir);
        var cmds: usize = 0;
        var it = std.mem.splitScalar(u8, ir, '\n');
        while (it.next()) |line| {
            if (line.len == 0) continue;
            if (!isDiag(line)) {
                cmds += 1;
                continue;
            }
            const d = try std.json.parseFromSlice(DiagJson, gpa, line, .{ .ignore_unknown_fields = true });
            defer d.deinit();
            if (std.mem.eql(u8, d.value.level, "error")) errors += 1;
            try out.print("{s}:{d}: {s}: {s}\n", .{ path, d.value.line, d.value.level, d.value.msg });
        }
        if (review) {
            // How well it teaches (docs/LESSON-DESIGN.md): advice, not errors.
            var arena_state = std.heap.ArenaAllocator.init(gpa);
            defer arena_state.deinit();
            for (try design.review(arena_state.allocator(), ir)) |f| {
                if (f.line) |l| try out.print("{s}:{d}: design: {s}\n", .{ path, l, f.msg }) else try out.print("{s}: design: {s}\n", .{ path, f.msg });
            }
        }
        try out.print("{s}: {d} commands\n", .{ path, cmds });
    }
    return errors;
}

fn compile(io: Io, gpa: Allocator, arena: Allocator, env: *const std.process.Environ.Map, args: []const [:0]const u8, out: *Io.Writer) !void {
    var computer: Computer = .{ .gpa = gpa, .io = io, .cfg = try fig.configure(arena, io, env) };
    var mode: vcl.Mode = .vcl;
    var path: ?[]const u8 = null;
    for (args) |a| {
        if (std.mem.eql(u8, a, "--mixed")) mode = .mixed else path = a;
    }
    const src = try readSource(io, gpa, path orelse return error.MissingFile);
    defer gpa.free(src);
    const ir = try runSource(gpa, src, mode, &computer);
    defer gpa.free(ir);
    try out.writeAll(ir);
}

// ───────────────────────────── server ─────────────────────────────

const Client = struct {
    ws: WebSocket,
    listen: bool,
    dead: bool = false,
};

const Hub = struct {
    io: Io,
    gpa: Allocator,
    root: Io.Dir,
    /// Where /assets/… comes from (null: no assets directory).
    assets: ?Io.Dir,
    /// Where /library/… comes from (null: no lesson library).
    library: ?Io.Dir = null,
    mutex: Io.Mutex = .init,
    scene: vcl.Scene,
    clients: std.ArrayList(*Client) = .empty,
    /// IR lines needed to rebuild the current scene (since the last `clear`).
    log: std.ArrayList([]u8) = .empty,
    /// The last `board` line: it outlives `clear`, so it starts every replay log.
    board: ?[]u8 = null,
    /// The port we serve on, which adb forwards to the headset.
    port: u16 = 0,
    /// One adb conversation at a time (the Headset page polls).
    quest_mutex: Io.Mutex = .init,
    /// The headset's last Wi-Fi address, to reconnect without the cable.
    quest_ip: [15]u8 = undefined,
    quest_ip_len: usize = 0,
    /// Figures from code (null: --no-figs). Renders run in `fig_group`;
    /// `fig_pending` holds the keys being rendered (under `fig_mutex`).
    figs: ?fig.Config = null,
    fig_mutex: Io.Mutex = .init,
    fig_pending: std.StringHashMapUnmanaged(void) = .empty,
    fig_group: Io.Group = .init,

    fn lock(h: *Hub) void {
        h.mutex.lockUncancelable(h.io);
    }
    fn unlock(h: *Hub) void {
        h.mutex.unlock(h.io);
    }

    /// Feed a chunk from one producer. Commands go to every listening client;
    /// diagnostics also go to `sender` (if it is not listening already) and
    /// are appended as text to `report` (for HTTP callers). Caller holds the lock.
    fn ingest(h: *Hub, st: *vcl.Stream, chunk: []const u8, finish: bool, sender: ?*Client, report: ?*Report) !void {
        var aw: Io.Writer.Allocating = .init(h.gpa);
        defer aw.deinit();
        try st.feed(&h.scene, chunk, &aw.writer);
        if (finish) try st.finish(&h.scene, &aw.writer);

        var it = std.mem.splitScalar(u8, aw.written(), '\n');
        while (it.next()) |line| {
            if (line.len == 0) continue;
            if (isDiag(line)) {
                if (report) |r| try r.addDiag(h.gpa, line);
                if (sender) |s| if (!s.listen) send(s, line);
            } else {
                if (report) |r| r.cmds += 1;
                try h.record(line);
                if (std.mem.eql(u8, opName(line), "fig")) h.startFig(line);
            }
            h.broadcast(line);
        }
    }

    fn record(h: *Hub, line: []const u8) !void {
        const name = opName(line);
        if (std.mem.eql(u8, name, "clear")) {
            for (h.log.items) |l| h.gpa.free(l);
            h.log.clearRetainingCapacity();
            if (h.board) |b| try h.log.append(h.gpa, try h.gpa.dupe(u8, b));
            return;
        }
        if (std.mem.eql(u8, name, "board")) {
            if (h.board) |b| h.gpa.free(b);
            h.board = try h.gpa.dupe(u8, line);
        }
        const op = vcl.findOp(name) orelse return;
        if (!op.replay) return;
        try h.log.append(h.gpa, try h.gpa.dupe(u8, line));
    }

    /// Start rendering a `fig` in the background (unless it is cached or
    /// already rendering). A failure is reported as a diagnostic on its line,
    /// so the tutor sees it. Caller holds the lock.
    fn startFig(h: *Hub, line: []const u8) void {
        const cfg = h.figs orelse return;
        const f = parseFig(h.gpa, line) orelse return;
        if (fig.cached(h.io, cfg, f.value.key)) {
            defer f.deinit();
            if (fig.cachedError(h.gpa, h.io, cfg, f.value.key)) |msg| {
                defer h.gpa.free(msg);
                h.figDiag(f.value.line, msg);
            }
            return;
        }
        h.fig_mutex.lockUncancelable(h.io);
        const busy = h.fig_pending.contains(f.value.key);
        if (!busy) h.fig_pending.put(h.gpa, h.gpa.dupe(u8, f.value.key) catch {
            h.fig_mutex.unlock(h.io);
            f.deinit();
            return;
        }, {}) catch {};
        h.fig_mutex.unlock(h.io);
        if (busy) {
            f.deinit();
            return;
        }
        h.fig_group.concurrent(h.io, figJob, .{ h, f }) catch {
            h.figDone(f.value.key);
            f.deinit();
        };
    }

    fn figJob(h: *Hub, f: std.json.Parsed(FigLine)) Io.Cancelable!void {
        defer f.deinit();
        defer h.figDone(f.value.key);
        const outcome = fig.render(h.gpa, h.io, h.figs.?, f.value.lang, f.value.code, f.value.key) catch |err| {
            std.log.warn("fig line {d}: {s}", .{ f.value.line, @errorName(err) });
            return;
        };
        switch (outcome) {
            .ok => std.log.info("fig line {d}: rendered {s}", .{ f.value.line, f.value.key }),
            .failed => |e| {
                defer h.gpa.free(e.msg);
                std.log.warn("fig line {d}: {s}", .{ f.value.line, e.msg });
                h.lock();
                defer h.unlock();
                h.figDiag(f.value.line, e.msg);
            },
        }
    }

    fn figDone(h: *Hub, key: []const u8) void {
        h.fig_mutex.lockUncancelable(h.io);
        defer h.fig_mutex.unlock(h.io);
        if (h.fig_pending.fetchRemove(key)) |kv| h.gpa.free(kv.key);
    }

    fn figPending(h: *Hub, key: []const u8) bool {
        h.fig_mutex.lockUncancelable(h.io);
        defer h.fig_mutex.unlock(h.io);
        return h.fig_pending.contains(key);
    }

    /// `fig: <msg>` as an error on the figure's line, to everyone. Caller holds the lock.
    fn figDiag(h: *Hub, line_no: u32, msg: []const u8) void {
        var aw: Io.Writer.Allocating = .init(h.gpa);
        defer aw.deinit();
        const w = &aw.writer;
        w.print("{{\"op\":\"diag\",\"level\":\"error\",\"line\":{d},\"msg\":", .{line_no}) catch return;
        const text = std.fmt.allocPrint(h.gpa, "fig: {s}", .{msg}) catch return;
        defer h.gpa.free(text);
        vcl.writeJsonString(w, text) catch return;
        w.writeAll("}") catch return;
        h.broadcast(aw.written());
    }

    fn broadcast(h: *Hub, line: []const u8) void {
        for (h.clients.items) |c| if (c.listen) send(c, line);
    }

    fn snapshot(h: *Hub, gpa: Allocator, prefix: []const u8) ![]u8 {
        var aw: Io.Writer.Allocating = .init(gpa);
        errdefer aw.deinit();
        const w = &aw.writer;
        try w.writeAll(prefix);
        for (h.log.items, 0..) |l, i| {
            if (i > 0) try w.writeByte(',');
            try w.writeAll(l);
        }
        try w.writeAll("]}");
        return aw.toOwnedSlice();
    }
};

fn send(c: *Client, msg: []const u8) void {
    if (c.dead) return;
    c.ws.writeMessage(msg, .text) catch {
        c.dead = true;
    };
}

fn opName(line: []const u8) []const u8 {
    const prefix = "{\"op\":\"";
    if (!std.mem.startsWith(u8, line, prefix)) return "";
    const rest = line[prefix.len..];
    const end = std.mem.indexOfScalar(u8, rest, '"') orelse return "";
    return rest[0..end];
}

const Report = struct {
    text: std.ArrayList(u8) = .empty,
    cmds: usize = 0,
    errors: usize = 0,
    warnings: usize = 0,

    fn addDiag(r: *Report, gpa: Allocator, line: []const u8) !void {
        const d = try std.json.parseFromSlice(DiagJson, gpa, line, .{ .ignore_unknown_fields = true });
        defer d.deinit();
        if (std.mem.eql(u8, d.value.level, "error")) r.errors += 1 else r.warnings += 1;
        var aw: Io.Writer.Allocating = .fromArrayList(gpa, &r.text);
        defer r.text = aw.toArrayList();
        try aw.writer.print("line {d}: {s}: {s}\n", .{ d.value.line, d.value.level, d.value.msg });
    }
};

/// `vcl figs`: render every `fig` of some lessons (for a static copy, or to warm the cache).
fn figsCmd(io: Io, gpa: Allocator, arena: Allocator, args: []const [:0]const u8, env: *const std.process.Environ.Map, out: *Io.Writer) !usize {
    var cfg = try fig.configure(arena, io, env);
    var files: std.ArrayList([]const u8) = .empty;
    defer files.deinit(gpa);
    var i: usize = 0;
    while (i < args.len) : (i += 1) {
        const a = args[i];
        if (std.mem.eql(u8, a, "--out")) {
            i += 1;
            if (i >= args.len) return error.MissingArgumentValue;
            try Io.Dir.cwd().createDirPath(io, args[i]);
            cfg.cache = try Io.Dir.cwd().realPathFileAlloc(io, args[i], arena);
        } else if (std.mem.eql(u8, a, "--unsafe-figs")) {
            cfg.unsafe = true;
        } else try files.append(gpa, a);
    }
    var failed: usize = 0;
    var count: usize = 0;
    for (files.items) |path| {
        const src = try readSource(io, gpa, path);
        defer gpa.free(src);
        const mode: vcl.Mode = if (std.mem.endsWith(u8, path, ".md")) .mixed else .vcl;
        var computer: Computer = .{ .gpa = gpa, .io = io, .cfg = cfg };
        const ir = try runSource(gpa, src, mode, &computer);
        defer gpa.free(ir);
        var it = std.mem.splitScalar(u8, ir, '\n');
        while (it.next()) |line| {
            // `data` ran while parsing (its results went into the same directory); report failures.
            if (isDiag(line)) {
                const d = std.json.parseFromSlice(DiagJson, gpa, line, .{ .ignore_unknown_fields = true }) catch continue;
                defer d.deinit();
                if (std.mem.startsWith(u8, d.value.msg, "data:")) {
                    failed += 1;
                    try out.print("{s}:{d}: {s}\n", .{ path, d.value.line, d.value.msg });
                }
                continue;
            }
            if (!std.mem.eql(u8, opName(line), "fig")) continue;
            const f = parseFig(gpa, line) orelse continue;
            defer f.deinit();
            count += 1;
            switch (try fig.render(gpa, io, cfg, f.value.lang, f.value.code, f.value.key)) {
                .ok => try out.print("{s}:{d}: {s} ok\n", .{ path, f.value.line, f.value.key }),
                .failed => |e| {
                    defer gpa.free(e.msg);
                    failed += 1;
                    try out.print("{s}:{d}: fig: {s}\n", .{ path, f.value.line, e.msg });
                },
            }
        }
    }
    try out.print("{d} figure(s), {d} failed, in {s}\n", .{ count, failed, cfg.cache });
    return failed;
}

const FigLine = struct { key: []const u8, lang: []const u8, code: []const u8, line: u32 = 0 };
fn parseFig(gpa: Allocator, line: []const u8) ?std.json.Parsed(FigLine) {
    return std.json.parseFromSlice(FigLine, gpa, line, .{ .ignore_unknown_fields = true, .allocate = .alloc_always }) catch null;
}

fn serve(io: Io, gpa: Allocator, arena: Allocator, args: []const [:0]const u8, env: *const std.process.Environ.Map) !void {
    var port: u16 = 8080;
    var host: []const u8 = "127.0.0.1";
    var root_path: []const u8 = "viewer";
    var assets_path: ?[]const u8 = null;
    var lessons_path: ?[]const u8 = null;
    var figs_on = true;
    var unsafe_figs = false;
    var i: usize = 0;
    while (i < args.len) : (i += 1) {
        const a = args[i];
        if (std.mem.eql(u8, a, "--no-figs")) {
            figs_on = false;
            continue;
        }
        if (std.mem.eql(u8, a, "--unsafe-figs")) {
            unsafe_figs = true;
            continue;
        }
        if (i + 1 >= args.len) return error.MissingArgumentValue;
        if (std.mem.eql(u8, a, "--port")) {
            port = try std.fmt.parseInt(u16, args[i + 1], 10);
        } else if (std.mem.eql(u8, a, "--host")) {
            host = args[i + 1];
        } else if (std.mem.eql(u8, a, "--root")) {
            root_path = args[i + 1];
        } else if (std.mem.eql(u8, a, "--assets")) {
            assets_path = args[i + 1];
        } else if (std.mem.eql(u8, a, "--lessons")) {
            lessons_path = args[i + 1];
        } else return error.UnknownOption;
        i += 1;
    }

    var root = try Io.Dir.cwd().openDir(io, root_path, .{});
    defer root.close(io);

    var assets: ?Io.Dir = if (assets_path) |ap|
        try Io.Dir.cwd().openDir(io, ap, .{ .iterate = true })
    else
        root.openDir(io, "assets", .{ .iterate = true }) catch null;
    defer if (assets) |*d| d.close(io);
    if (assets_path) |ap| std.log.info("assets: {s}/ at /assets/", .{ap});

    var library: ?Io.Dir = if (lessons_path) |lp| try Io.Dir.cwd().openDir(io, lp, .{ .iterate = true }) else null;
    defer if (library) |*d| d.close(io);
    if (lessons_path) |lp| std.log.info("lessons: {s}/ at /library/", .{lp});

    var figs: ?fig.Config = null;
    if (figs_on) {
        var cfg = try fig.configure(arena, io, env);
        cfg.unsafe = unsafe_figs;
        std.log.info("figures: python {s}, octave {s}, sandbox {s}; cache {s}", .{
            cfg.python orelse "(none)", cfg.octave orelse "(none)",
            if (cfg.unsafe) "OFF (--unsafe-figs)" else cfg.bwrap orelse "(no bwrap: figures won't render)", cfg.cache,
        });
        figs = cfg;
    }
    var hub: Hub = .{ .io = io, .gpa = gpa, .root = root, .assets = assets, .library = library, .scene = vcl.Scene.init(gpa), .port = port, .figs = figs };
    defer hub.scene.deinit();
    // `data` commands run while their line is parsed (under the hub's lock: the next lines need them).
    var computer: Computer = undefined;
    if (figs) |cfg| {
        computer = .{ .gpa = gpa, .io = io, .cfg = cfg };
        hub.scene.compute = computer.compute();
    }
    defer hub.fig_group.cancel(io);

    const addr = try Io.net.IpAddress.parse(host, port);
    var server = listenExclusive(io, addr) catch |err| {
        if (err == error.AddressInUse) {
            std.log.err("port {d} is already in use. Is another `vcl serve` running? " ++
                "A suspended one (Ctrl+Z) still holds the port: check `jobs` and `fg` it, or kill it.", .{port});
            std.process.exit(1);
        }
        return err;
    };
    defer server.deinit(io);
    std.log.info("vcl {s}: serving {s}/ on http://{s}:{d}  (POST /vcl, WS /ws)", .{ vcl.version, root_path, host, port });

    var group: Io.Group = .init;
    defer group.cancel(io);
    while (true) {
        const stream = server.accept(io) catch |err| {
            std.log.warn("accept: {s}", .{@errorName(err)});
            continue;
        };
        group.concurrent(io, handleConn, .{ &hub, stream }) catch {
            stream.close(io);
        };
    }
}

/// Listen with SO_REUSEADDR but *not* SO_REUSEPORT. Zig's `reuse_address`
/// sets both, which lets a second server bind the same port and silently
/// share connections with the first (including a suspended one, whose share
/// then hangs). REUSEADDR alone still allows a quick restart over TIME_WAIT.
fn listenExclusive(io: Io, addr: Io.net.IpAddress) !Io.net.Server {
    if (builtin.os.tag == .linux and addr == .ip4) {
        const linux = std.os.linux;
        const rc = linux.socket(linux.AF.INET, linux.SOCK.STREAM | linux.SOCK.CLOEXEC, 0);
        if (linux.errno(rc) != .SUCCESS) return error.SocketFailed;
        const fd: i32 = @intCast(rc);
        errdefer _ = linux.close(fd);
        const one: c_int = 1;
        _ = linux.setsockopt(fd, linux.SOL.SOCKET, linux.SO.REUSEADDR, std.mem.asBytes(&one), @sizeOf(c_int));
        var sa: linux.sockaddr.in = .{ .port = std.mem.nativeToBig(u16, addr.ip4.port), .addr = @bitCast(addr.ip4.bytes) };
        switch (linux.errno(linux.bind(fd, @ptrCast(&sa), @sizeOf(linux.sockaddr.in)))) {
            .SUCCESS => {},
            .ADDRINUSE => return error.AddressInUse,
            else => return error.BindFailed,
        }
        if (linux.errno(linux.listen(fd, 128)) != .SUCCESS) return error.ListenFailed;
        return .{ .socket = .{ .handle = fd, .address = addr }, .options = {} };
    }
    return addr.listen(io, .{});
}

const Conn = struct {
    in_buf: [256 * 1024]u8,
    out_buf: [64 * 1024]u8,
    body_buf: [8 * 1024]u8,
};

fn handleConn(hub: *Hub, stream: Io.net.Stream) Io.Cancelable!void {
    const io = hub.io;
    defer stream.close(io);
    const conn = hub.gpa.create(Conn) catch return;
    defer hub.gpa.destroy(conn);
    var sr = stream.reader(io, &conn.in_buf);
    var sw = stream.writer(io, &conn.out_buf);
    var http = std.http.Server.init(&sr.interface, &sw.interface);

    while (true) {
        var req = http.receiveHead() catch return;
        // A POST with neither Content-Length nor chunking has an empty body
        // (RFC 9112 §6.3); std.http asserts one of them is there.
        if (req.head.method.requestHasBody() and req.head.transfer_encoding == .none and req.head.content_length == null)
            req.head.content_length = 0;
        const target = req.head.target;
        const path = target[0 .. std.mem.indexOfScalar(u8, target, '?') orelse target.len];
        const query = if (std.mem.indexOfScalar(u8, target, '?')) |q| target[q + 1 ..] else "";

        switch (req.upgradeRequested()) {
            .websocket => |key| if (std.mem.eql(u8, path, "/ws")) {
                handleWs(hub, &req, key orelse return, query) catch |err|
                    std.log.debug("ws closed: {s}", .{@errorName(err)});
                return;
            },
            else => {},
        }

        const keep = route(hub, &req, conn, path, query, isLocal(stream.socket.address)) catch |err| {
            std.log.warn("{s} {s}: {s}", .{ @tagName(req.head.method), path, @errorName(err) });
            return;
        };
        if (!keep) return;
    }
}

fn isLocal(peer: Io.net.IpAddress) bool {
    return switch (peer) {
        .ip4 => |a| a.bytes[0] == 127,
        .ip6 => |a| std.mem.eql(u8, &a.bytes, &(.{0} ** 15 ++ .{1})) or
            (std.mem.eql(u8, a.bytes[0..12], &(.{0} ** 10 ++ .{ 0xff, 0xff })) and a.bytes[12] == 127),
    };
}

fn queryParam(query: []const u8, name: []const u8) ?[]const u8 {
    var it = std.mem.splitScalar(u8, query, '&');
    while (it.next()) |kv| {
        const eq = std.mem.indexOfScalar(u8, kv, '=') orelse continue;
        if (std.mem.eql(u8, kv[0..eq], name)) return kv[eq + 1 ..];
    }
    return null;
}

fn modeFrom(query: []const u8) vcl.Mode {
    const m = queryParam(query, "mode") orelse return .vcl;
    return if (std.mem.eql(u8, m, "mixed")) .mixed else .vcl;
}

const cors: std.http.Header = .{ .name = "access-control-allow-origin", .value = "*" };

fn route(hub: *Hub, req: *std.http.Server.Request, conn: *Conn, path: []const u8, query: []const u8, local: bool) !bool {
    const gpa = hub.gpa;
    const method = req.head.method;

    if (method == .OPTIONS) {
        try req.respond("", .{ .status = .no_content, .extra_headers = &.{
            cors,
            .{ .name = "access-control-allow-methods", .value = "GET, POST, OPTIONS" },
            .{ .name = "access-control-allow-headers", .value = "content-type" },
        } });
        return true;
    }

    if (std.mem.startsWith(u8, path, "/quest/")) return questRoute(hub, req, path["/quest/".len..], query, local);

    if (method == .POST and std.mem.eql(u8, path, "/vcl")) {
        const mode = modeFrom(query);
        const pace_ms: i64 = if (queryParam(query, "pace")) |p| std.fmt.parseInt(i64, p, 10) catch 0 else 0;
        const reader = try req.readerExpectContinue(&conn.body_buf);
        const body = try reader.allocRemaining(gpa, .limited(4 << 20));
        defer gpa.free(body);

        var st = vcl.Stream.init(mode);
        defer st.deinit(gpa);
        var report: Report = .{};
        defer report.text.deinit(gpa);

        if (pace_ms <= 0) {
            hub.lock();
            defer hub.unlock();
            try hub.ingest(&st, body, true, null, &report);
        } else {
            var it = std.mem.splitScalar(u8, body, '\n');
            while (it.next()) |line| {
                {
                    hub.lock();
                    defer hub.unlock();
                    try hub.ingest(&st, line, false, null, &report);
                    try hub.ingest(&st, "\n", false, null, &report);
                }
                if (std.mem.trim(u8, line, " \t\r").len > 0)
                    try hub.io.sleep(.fromMilliseconds(pace_ms), .awake);
            }
        }
        var aw: Io.Writer.Allocating = .fromArrayList(gpa, &report.text);
        try aw.writer.print("ok: {d} commands, {d} errors, {d} warnings\n", .{ report.cmds, report.errors, report.warnings });
        report.text = aw.toArrayList();
        try req.respond(report.text.items, .{ .extra_headers = &.{
            cors, .{ .name = "content-type", .value = "text/plain; charset=utf-8" },
        } });
        return true;
    }

    // Conversation events: a learner's question (/ask) or the tutor's state
    // (/status: "thinking" | "idle"). Broadcast as {"op":"ask"|"status","text":…},
    // not recorded for replay.
    if (method == .POST and (std.mem.eql(u8, path, "/ask") or std.mem.eql(u8, path, "/status"))) {
        const reader = try req.readerExpectContinue(&conn.body_buf);
        const body = try reader.allocRemaining(gpa, .limited(64 << 10));
        defer gpa.free(body);
        const text = std.mem.trim(u8, body, " \t\r\n");
        if (text.len == 0) {
            try req.respond("empty\n", .{ .status = .bad_request, .extra_headers = &.{cors} });
            return true;
        }
        var aw: Io.Writer.Allocating = .init(gpa);
        defer aw.deinit();
        try aw.writer.print("{{\"op\":\"{s}\",\"text\":", .{path[1..]});
        try vcl.writeJsonString(&aw.writer, text);
        try aw.writer.writeAll("}");
        {
            hub.lock();
            defer hub.unlock();
            hub.broadcast(aw.written());
        }
        try req.respond("ok\n", .{ .extra_headers = &.{cors} });
        return true;
    }

    if (method == .POST and std.mem.eql(u8, path, "/answer")) {
        const reader = try req.readerExpectContinue(&conn.body_buf);
        const body = try reader.allocRemaining(gpa, .limited(64 << 10));
        defer gpa.free(body);
        const event = answerEvent(gpa, body) catch {
            try req.respond("expected a JSON object\n", .{ .status = .bad_request, .extra_headers = &.{cors} });
            return true;
        };
        defer gpa.free(event);
        {
            hub.lock();
            defer hub.unlock();
            hub.broadcast(event);
        }
        try req.respond("ok\n", .{ .extra_headers = &.{cors} });
        return true;
    }

    if (method != .GET and method != .HEAD) {
        try req.respond("method not allowed\n", .{ .status = .method_not_allowed });
        return true;
    }

    if (std.mem.eql(u8, path, "/scene")) {
        const snap = blk: {
            hub.lock();
            defer hub.unlock();
            break :blk try hub.snapshot(gpa, "{\"cmds\":[");
        };
        defer gpa.free(snap);
        try req.respond(snap, .{ .extra_headers = &.{ cors, .{ .name = "content-type", .value = "application/json" } } });
        return true;
    }

    if (std.mem.startsWith(u8, path, "/figs/")) return figRoute(hub, req, path["/figs/".len..]);

    if (std.mem.eql(u8, path, "/assets/index.json")) {
        const listing = try assetIndex(hub);
        defer gpa.free(listing);
        try req.respond(listing, .{ .extra_headers = &.{ cors, .{ .name = "content-type", .value = "application/json" }, .{ .name = "cache-control", .value = "no-cache" } } });
        return true;
    }

    if (std.mem.eql(u8, path, "/library/index.json")) {
        const listing = try libraryIndex(hub);
        defer gpa.free(listing);
        try req.respond(listing, .{ .extra_headers = &.{ cors, .{ .name = "content-type", .value = "application/json" }, .{ .name = "cache-control", .value = "no-cache" } } });
        return true;
    }

    // Static files (and /assets/…, /library/… from their directories).
    var dir = hub.root;
    var rel = if (std.mem.eql(u8, path, "/")) "index.html" else path[1..];
    if (std.mem.startsWith(u8, path, "/assets/")) if (hub.assets) |a| {
        dir = a;
        rel = path["/assets/".len..];
    };
    if (std.mem.startsWith(u8, path, "/library/")) {
        dir = hub.library orelse {
            try req.respond("no lesson library (start the server with --lessons DIR)\n", .{ .status = .not_found, .extra_headers = &.{cors} });
            return true;
        };
        rel = path["/library/".len..];
    }
    if (rel.len == 0 or std.mem.indexOf(u8, rel, "..") != null or rel[0] == '/') {
        try req.respond("bad path\n", .{ .status = .bad_request });
        return true;
    }
    var index_buf: [1024]u8 = undefined;
    if (std.mem.endsWith(u8, rel, "/")) rel = std.fmt.bufPrint(&index_buf, "{s}index.html", .{rel}) catch rel;
    const data = dir.readFileAlloc(hub.io, rel, gpa, .limited(64 << 20)) catch |err| {
        const status: std.http.Status = if (err == error.FileNotFound) .not_found else .internal_server_error;
        try req.respond(@errorName(err), .{ .status = status });
        return true;
    };
    defer gpa.free(data);
    try req.respond(data, .{ .extra_headers = &.{
        .{ .name = "content-type", .value = mimeType(rel) },
        .{ .name = "cache-control", .value = "no-cache" },
    } });
    return true;
}

/// GET /figs/KEY.svg|png|json: from the render cache (waiting while it
/// renders), else pre-rendered under ROOT/figs/.
fn figRoute(hub: *Hub, req: *std.http.Server.Request, name: []const u8) !bool {
    const gpa = hub.gpa;
    const is_data = std.mem.endsWith(u8, name, ".data.json"); // a `data` command's results
    const dot = if (is_data) name.len - ".data.json".len else std.mem.lastIndexOfScalar(u8, name, '.') orelse name.len;
    const key = name[0..dot];
    const ext = name[dot..];
    const known = is_data or std.mem.eql(u8, ext, ".svg") or std.mem.eql(u8, ext, ".png") or std.mem.eql(u8, ext, ".json");
    if (!fig.isKey(key) or !known) {
        try req.respond("bad figure name\n", .{ .status = .bad_request });
        return true;
    }
    const headers: []const std.http.Header = &.{
        .{ .name = "content-type", .value = mimeType(name) },
        .{ .name = "cache-control", .value = "max-age=86400" },
        .{ .name = "access-control-allow-origin", .value = "*" },
    };
    var waited: u32 = 0;
    while (true) : (waited += 1) {
        if (hub.figs) |cfg| {
            var buf: [std.fs.max_path_bytes]u8 = undefined;
            const p = try std.fmt.bufPrint(&buf, "{s}/{s}", .{ cfg.cache, name });
            if (Io.Dir.cwd().readFileAlloc(hub.io, p, gpa, .limited(64 << 20))) |data| {
                defer gpa.free(data);
                try req.respond(data, .{ .extra_headers = headers });
                return true;
            } else |_| {}
            // Still rendering: wait (up to a minute and a half).
            if (waited < 600 and hub.figPending(key)) {
                hub.io.sleep(.fromMilliseconds(150), .awake) catch return false;
                continue;
            }
        }
        break;
    }
    var rel_buf: [64]u8 = undefined;
    const rel = try std.fmt.bufPrint(&rel_buf, "figs/{s}", .{name});
    const data = hub.root.readFileAlloc(hub.io, rel, gpa, .limited(64 << 20)) catch {
        try req.respond("figure not rendered (see the diagnostics for its line)\n", .{ .status = .not_found, .extra_headers = &.{.{ .name = "access-control-allow-origin", .value = "*" }} });
        return true;
    };
    defer gpa.free(data);
    try req.respond(data, .{ .extra_headers = headers });
    return true;
}

/// A learner's answer (a JSON object from a viewer) as one broadcast line,
/// with "op":"answer" first and any "op" of its own dropped.
fn answerEvent(gpa: Allocator, body: []const u8) ![]u8 {
    const parsed = try std.json.parseFromSlice(std.json.Value, gpa, body, .{});
    defer parsed.deinit();
    if (parsed.value != .object) return error.NotAnObject;
    var aw: Io.Writer.Allocating = .init(gpa);
    errdefer aw.deinit();
    try aw.writer.writeAll("{\"op\":\"answer\"");
    var it = parsed.value.object.iterator();
    while (it.next()) |kv| {
        if (std.mem.eql(u8, kv.key_ptr.*, "op")) continue;
        try aw.writer.writeAll(",");
        try std.json.Stringify.encodeJsonString(kv.key_ptr.*, .{}, &aw.writer);
        try aw.writer.writeAll(":");
        try std.json.Stringify.value(kv.value_ptr.*, .{}, &aw.writer);
    }
    try aw.writer.writeAll("}");
    return aw.toOwnedSlice();
}

/// The Headset page's endpoints: they run adb, so only this machine may call
/// them, and actions need an `x-vcl: quest` header, which a cross-site page
/// can't send without a preflight we don't allow.
fn questRoute(hub: *Hub, req: *std.http.Server.Request, action: []const u8, query: []const u8, local: bool) !bool {
    const json: std.http.Header = .{ .name = "content-type", .value = "application/json" };
    if (!local) {
        try req.respond("the headset controls only work from the machine running vcl serve\n", .{ .status = .forbidden });
        return true;
    }
    const is_status = std.mem.eql(u8, action, "status");
    if (is_status != (req.head.method == .GET)) {
        try req.respond("method not allowed\n", .{ .status = .method_not_allowed });
        return true;
    }
    if (!is_status) {
        var marked = false;
        var it = req.iterateHeaders();
        while (it.next()) |h| if (std.ascii.eqlIgnoreCase(h.name, "x-vcl") and std.mem.eql(u8, h.value, "quest")) {
            marked = true;
        };
        if (!marked) {
            try req.respond("missing x-vcl: quest header\n", .{ .status = .forbidden });
            return true;
        }
    }

    var arena_state: std.heap.ArenaAllocator = .init(hub.gpa);
    defer arena_state.deinit();
    const arena = arena_state.allocator();
    var q: quest.Quest = .init(hub.io, arena, hub.port);
    var out: Io.Writer.Allocating = .init(arena);

    hub.quest_mutex.lockUncancelable(hub.io);
    defer hub.quest_mutex.unlock(hub.io);
    const known: ?[]const u8 = if (queryParam(query, "ip")) |ip| (if (quest.validIp(ip)) ip else null) else if (hub.quest_ip_len > 0) hub.quest_ip[0..hub.quest_ip_len] else null;

    if (is_status) {
        try q.statusJson(&out.writer);
        // Splice in what only the server knows: connected viewers, the remembered address.
        const viewers = blk: {
            hub.lock();
            defer hub.unlock();
            break :blk hub.clients.items.len;
        };
        const body = out.written();
        var full: Io.Writer.Allocating = .init(arena);
        try full.writer.print("{s},\"viewers\":{d},\"known_ip\":", .{ body[0 .. body.len - 1], viewers });
        if (known) |k| try std.json.Stringify.encodeJsonString(k, .{}, &full.writer) else try full.writer.writeAll("null");
        try full.writer.writeAll("}");
        try req.respond(full.written(), .{ .extra_headers = &.{ json, .{ .name = "cache-control", .value = "no-store" } } });
        return true;
    }

    var ip: ?[]const u8 = null;
    const ran: anyerror!void = if (std.mem.eql(u8, action, "connect"))
        (if (q.connect(known)) |got| {
            ip = got;
        } else |err| err)
    else if (std.mem.eql(u8, action, "start"))
        q.startApp()
    else if (std.mem.eql(u8, action, "disconnect"))
        q.disconnect()
    else {
        try req.respond("unknown action\n", .{ .status = .not_found });
        return true;
    };
    ran catch |err| q.log.writer.print("{s}\n", .{if (err == error.NoAdb) "adb isn't installed (or not on PATH)." else @errorName(err)}) catch {};
    if (ip) |addr| if (quest.validIp(addr)) {
        @memcpy(hub.quest_ip[0..addr.len], addr);
        hub.quest_ip_len = addr.len;
    };
    try out.writer.writeAll("{\"log\":");
    try std.json.Stringify.encodeJsonString(q.log.written(), .{}, &out.writer);
    try out.writer.writeAll(",\"ip\":");
    if (ip) |addr| try std.json.Stringify.encodeJsonString(addr, .{}, &out.writer) else try out.writer.writeAll("null");
    try out.writer.writeAll("}");
    try req.respond(out.written(), .{ .extra_headers = &.{json} });
    return true;
}

/// {"lessons":[{"file":"u01/02-x.md","title":"…"},…]}: the --lessons library,
/// sorted by path. A lesson is a .vcl file or a .md file with a ```vcl fence;
/// its title is the first `# ` heading (.md) or `title "…"` command (.vcl),
/// else the file name.
fn libraryIndex(hub: *Hub) ![]u8 {
    const gpa = hub.gpa;
    const Lesson = struct { file: []u8, title: []u8 };
    var list: std.ArrayList(Lesson) = .empty;
    defer {
        for (list.items) |l| {
            gpa.free(l.file);
            gpa.free(l.title);
        }
        list.deinit(gpa);
    }
    if (hub.library) |lib| {
        var walker = try lib.walk(gpa);
        defer walker.deinit();
        while (try walker.next(hub.io)) |e| {
            if (e.kind != .file or list.items.len >= 2000) continue;
            if (std.mem.indexOf(u8, e.path, "/.") != null or e.path[0] == '.') continue;
            const md = std.mem.endsWith(u8, e.path, ".md");
            if (!md and !std.mem.endsWith(u8, e.path, ".vcl")) continue;
            const text = lib.readFileAlloc(hub.io, e.path, gpa, .limited(4 << 20)) catch continue;
            defer gpa.free(text);
            if (md and !hasFence(text)) continue; // notes, not a lesson
            var title: []const u8 = std.fs.path.basename(e.path);
            var lines = std.mem.splitScalar(u8, text, '\n');
            while (lines.next()) |raw| {
                const l = std.mem.trim(u8, raw, " \t\r");
                if (md and std.mem.startsWith(u8, l, "# ")) {
                    title = std.mem.trim(u8, l[2..], " ");
                    break;
                }
                if (!md and std.mem.startsWith(u8, l, "title ")) {
                    const q = std.mem.trim(u8, l[6..], " ");
                    if (q.len >= 2 and q[0] == '"') if (std.mem.lastIndexOfScalar(u8, q, '"')) |end| if (end > 0) {
                        title = q[1..end];
                        break;
                    };
                }
            }
            const file = try gpa.dupe(u8, e.path);
            errdefer gpa.free(file);
            try list.append(gpa, .{ .file = file, .title = try gpa.dupe(u8, title) });
        }
    }
    std.mem.sort(Lesson, list.items, {}, struct {
        fn lt(_: void, x: Lesson, y: Lesson) bool {
            return std.mem.lessThan(u8, x.file, y.file);
        }
    }.lt);
    var aw: Io.Writer.Allocating = .init(gpa);
    errdefer aw.deinit();
    try aw.writer.writeAll("{\"lessons\":[");
    for (list.items, 0..) |l, i| {
        if (i > 0) try aw.writer.writeByte(',');
        try aw.writer.writeAll("{\"file\":");
        try vcl.writeJsonString(&aw.writer, l.file);
        try aw.writer.writeAll(",\"title\":");
        try vcl.writeJsonString(&aw.writer, l.title);
        try aw.writer.writeByte('}');
    }
    try aw.writer.writeAll("]}");
    return aw.toOwnedSlice();
}

/// A line that is just ```vcl (a board fence, not a mention of one).
fn hasFence(text: []const u8) bool {
    var lines = std.mem.splitScalar(u8, text, '\n');
    while (lines.next()) |l| if (std.mem.eql(u8, std.mem.trim(u8, l, " \t\r"), "```vcl")) return true;
    return false;
}

/// {"files":["chip.svg","models/arm.stl",…]}: what the teacher can show.
fn assetIndex(hub: *Hub) ![]u8 {
    const gpa = hub.gpa;
    var names: std.ArrayList([]u8) = .empty;
    defer {
        for (names.items) |n| gpa.free(n);
        names.deinit(gpa);
    }
    if (hub.assets) |a| {
        var walker = try a.walk(gpa);
        defer walker.deinit();
        while (try walker.next(hub.io)) |e| {
            if (e.kind != .file or names.items.len >= 500) continue;
            if (std.mem.indexOf(u8, e.path, "/.") != null or e.path[0] == '.') continue;
            try names.append(gpa, try gpa.dupe(u8, e.path));
        }
    }
    std.mem.sort([]u8, names.items, {}, struct {
        fn lt(_: void, x: []u8, y: []u8) bool {
            return std.mem.lessThan(u8, x, y);
        }
    }.lt);
    var aw: Io.Writer.Allocating = .init(gpa);
    errdefer aw.deinit();
    try aw.writer.writeAll("{\"files\":[");
    for (names.items, 0..) |n, i| {
        if (i > 0) try aw.writer.writeByte(',');
        try vcl.writeJsonString(&aw.writer, n);
    }
    try aw.writer.writeAll("]}");
    return aw.toOwnedSlice();
}

fn mimeType(path: []const u8) []const u8 {
    const table = [_]struct { []const u8, []const u8 }{
        .{ ".html", "text/html; charset=utf-8" },     .{ ".js", "text/javascript; charset=utf-8" },
        .{ ".mjs", "text/javascript; charset=utf-8" }, .{ ".css", "text/css; charset=utf-8" },
        .{ ".wasm", "application/wasm" },              .{ ".json", "application/json" },
        .{ ".png", "image/png" },                      .{ ".jpg", "image/jpeg" },
        .{ ".svg", "image/svg+xml" },                  .{ ".glb", "model/gltf-binary" },
        .{ ".gltf", "model/gltf+json" },               .{ ".vcl", "text/plain; charset=utf-8" },
        .{ ".md", "text/plain; charset=utf-8" },       .{ ".stl", "model/stl" },
        .{ ".obj", "model/obj" },                      .{ ".mtl", "text/plain; charset=utf-8" },
        .{ ".bin", "application/octet-stream" },       .{ ".webp", "image/webp" },
        .{ ".gif", "image/gif" },                      .{ ".txt", "text/plain; charset=utf-8" },
    };
    for (table) |e| if (std.mem.endsWith(u8, path, e[0])) return e[1];
    return "application/octet-stream";
}

fn handleWs(hub: *Hub, req: *std.http.Server.Request, key: []const u8, query: []const u8) !void {
    const gpa = hub.gpa;
    const listen = !std.mem.eql(u8, queryParam(query, "listen") orelse "1", "0");
    var st = vcl.Stream.init(modeFrom(query));
    defer st.deinit(gpa);

    const client = try gpa.create(Client);
    defer gpa.destroy(client);
    client.* = .{ .ws = try req.respondWebSocket(.{ .key = key }), .listen = listen };

    {
        hub.lock();
        defer hub.unlock();
        try client.ws.flush();
        if (listen) {
            const snap = try hub.snapshot(gpa, "{\"op\":\"replay\",\"cmds\":[");
            defer gpa.free(snap);
            send(client, snap);
        }
        try hub.clients.append(gpa, client);
    }
    defer {
        hub.lock();
        defer hub.unlock();
        for (hub.clients.items, 0..) |c, i| if (c == client) {
            _ = hub.clients.swapRemove(i);
            break;
        };
    }
    std.log.info("ws client connected ({d} total)", .{hub.clients.items.len});

    while (true) {
        const msg = client.ws.readSmallMessage() catch |err| {
            if (err == error.ConnectionClose) {
                // Echo the close frame so clients see a clean shutdown.
                hub.lock();
                defer hub.unlock();
                client.ws.writeMessage("", .connection_close) catch {};
            }
            return err;
        };
        switch (msg.opcode) {
            .text => {
                hub.lock();
                defer hub.unlock();
                try hub.ingest(&st, msg.data, false, client, null);
            },
            .ping => {
                hub.lock();
                defer hub.unlock();
                client.ws.writeMessage(msg.data, .pong) catch return;
            },
            else => {},
        }
        if (client.dead) return;
    }
}
