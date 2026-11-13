//! vcl — command-line tool and live stream server.
//!
//!   vcl check [--mixed] FILE...  report diagnostics (exit 1 on errors); .md is mixed; - is stdin
//!   vcl compile [--mixed] FILE   print the JSON IR, one command per line (- is stdin)
//!   vcl serve [--port N] [--host H] [--root DIR] [--assets DIR]
//!
//! The server:
//!   GET  /            the viewer (static files from --root, default "viewer")
//!   GET  /assets/…    files you provide (--assets DIR, default ROOT/assets):
//!                     SVGs, images, glTF/STL/OBJ models; /assets/index.json lists them
//!   GET  /ws          WebSocket: receives IR; may also send VCL text chunks
//!                     (?mode=mixed for prose+fences, ?listen=0 to only send)
//!   POST /vcl         body = VCL text (?mode=mixed, ?pace=MS between lines);
//!                     responds with plain-text diagnostics
//!   GET  /scene       current scene as {"cmds":[...]} (for late joiners/pollers)
//!   POST /ask         a learner question → broadcast {"op":"ask","text":…}
//!   POST /status      tutor state ("thinking"/"idle") → {"op":"status","text":…}

const std = @import("std");
const builtin = @import("builtin");
const vcl = @import("vcl");
const Io = std.Io;
const Allocator = std.mem.Allocator;
const WebSocket = std.http.Server.WebSocket;

const usage =
    \\usage:
    \\  vcl check [--mixed] FILE...    report diagnostics (.md files are mixed; - reads stdin)
    \\  vcl compile [--mixed] FILE     print JSON IR (one command per line; - reads stdin)
    \\  vcl serve [--port 8080] [--host 127.0.0.1] [--root viewer] [--assets DIR]
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
        const errors = try check(io, gpa, rest, out);
        try out.flush();
        if (errors > 0) std.process.exit(1);
    } else if (std.mem.eql(u8, cmd, "compile")) {
        try compile(io, gpa, rest, out);
    } else if (std.mem.eql(u8, cmd, "serve")) {
        try serve(io, gpa, rest);
    } else {
        try out.writeAll(usage);
        try out.flush();
        std.process.exit(2);
    }
}

// ───────────────────────────── check / compile ─────────────────────────────

const DiagJson = struct { level: []const u8, line: u32, msg: []const u8 };

fn runSource(gpa: Allocator, src: []const u8, mode: vcl.Mode) ![]u8 {
    var scene = vcl.Scene.init(gpa);
    defer scene.deinit();
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

fn check(io: Io, gpa: Allocator, args: []const [:0]const u8, out: *Io.Writer) !usize {
    var errors: usize = 0;
    var force_mixed = false;
    for (args) |a| if (std.mem.eql(u8, a, "--mixed")) {
        force_mixed = true;
    };
    for (args) |path| {
        if (std.mem.eql(u8, path, "--mixed")) continue;
        const mode: vcl.Mode = if (force_mixed or std.mem.endsWith(u8, path, ".md")) .mixed else .vcl;
        const src = readSource(io, gpa, path) catch |err| {
            try out.print("{s}: cannot read: {s}\n", .{ path, @errorName(err) });
            errors += 1;
            continue;
        };
        defer gpa.free(src);
        const ir = try runSource(gpa, src, mode);
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
        try out.print("{s}: {d} commands\n", .{ path, cmds });
    }
    return errors;
}

fn compile(io: Io, gpa: Allocator, args: []const [:0]const u8, out: *Io.Writer) !void {
    var mode: vcl.Mode = .vcl;
    var path: ?[]const u8 = null;
    for (args) |a| {
        if (std.mem.eql(u8, a, "--mixed")) mode = .mixed else path = a;
    }
    const src = try readSource(io, gpa, path orelse return error.MissingFile);
    defer gpa.free(src);
    const ir = try runSource(gpa, src, mode);
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
    mutex: Io.Mutex = .init,
    scene: vcl.Scene,
    clients: std.ArrayList(*Client) = .empty,
    /// IR lines needed to rebuild the current scene (since the last `clear`).
    log: std.ArrayList([]u8) = .empty,
    /// The last `board` line: it outlives `clear`, so it starts every replay log.
    board: ?[]u8 = null,

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

fn serve(io: Io, gpa: Allocator, args: []const [:0]const u8) !void {
    var port: u16 = 8080;
    var host: []const u8 = "127.0.0.1";
    var root_path: []const u8 = "viewer";
    var assets_path: ?[]const u8 = null;
    var i: usize = 0;
    while (i < args.len) : (i += 1) {
        const a = args[i];
        if (i + 1 >= args.len) return error.MissingArgumentValue;
        if (std.mem.eql(u8, a, "--port")) {
            port = try std.fmt.parseInt(u16, args[i + 1], 10);
        } else if (std.mem.eql(u8, a, "--host")) {
            host = args[i + 1];
        } else if (std.mem.eql(u8, a, "--root")) {
            root_path = args[i + 1];
        } else if (std.mem.eql(u8, a, "--assets")) {
            assets_path = args[i + 1];
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

    var hub: Hub = .{ .io = io, .gpa = gpa, .root = root, .assets = assets, .scene = vcl.Scene.init(gpa) };
    defer hub.scene.deinit();

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

        const keep = route(hub, &req, conn, path, query) catch |err| {
            std.log.warn("{s} {s}: {s}", .{ @tagName(req.head.method), path, @errorName(err) });
            return;
        };
        if (!keep) return;
    }
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

fn route(hub: *Hub, req: *std.http.Server.Request, conn: *Conn, path: []const u8, query: []const u8) !bool {
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

    if (std.mem.eql(u8, path, "/assets/index.json")) {
        const listing = try assetIndex(hub);
        defer gpa.free(listing);
        try req.respond(listing, .{ .extra_headers = &.{ cors, .{ .name = "content-type", .value = "application/json" }, .{ .name = "cache-control", .value = "no-cache" } } });
        return true;
    }

    // Static files (and /assets/… from the assets directory).
    var dir = hub.root;
    var rel = if (std.mem.eql(u8, path, "/")) "index.html" else path[1..];
    if (std.mem.startsWith(u8, path, "/assets/")) if (hub.assets) |a| {
        dir = a;
        rel = path["/assets/".len..];
    };
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
