//! Connecting a Quest headset to `vcl serve` through adb, for the Headset page
//! (viewer/quest.html) and its /quest/… endpoints.
//!
//! The Quest app reaches the server at 127.0.0.1:PORT on the headset, which
//! `adb reverse` forwards to this machine. adb can run over USB or Wi-Fi; over
//! Wi-Fi the forward survives unplugging the cable. Switching adb to Wi-Fi
//! (`adb tcpip 5555`) needs the cable once per headset boot; after that,
//! `adb connect IP:5555` brings it back without one.

const std = @import("std");
const Io = std.Io;
const Allocator = std.mem.Allocator;

pub const app_package = "io.github.pa255ion.vcl";
pub const wifi_port = 5555;

pub const Transport = struct {
    /// adb's name for it: a USB serial, or IP:PORT over Wi-Fi.
    serial: []const u8,
    /// "device" (ready), "unauthorized", "offline", …
    state: []const u8,
    wifi: bool,
    /// Details, filled in only for ready transports.
    ip: ?[]const u8 = null,
    forwarded: bool = false,
    app_running: bool = false,

    fn ready(t: Transport) bool {
        return std.mem.eql(u8, t.state, "device");
    }
};

pub const Quest = struct {
    io: Io,
    /// Freed all at once: one Quest per request.
    arena: Allocator,
    /// The server's port, forwarded to the same port on the headset.
    port: u16,
    log: Io.Writer.Allocating,

    pub fn init(io: Io, arena: Allocator, port: u16) Quest {
        return .{ .io = io, .arena = arena, .port = port, .log = .init(arena) };
    }

    const Result = struct { ok: bool, out: []const u8 };

    /// Run adb with these arguments. A missing adb or a timeout is an
    /// error; adb's own failures come back as ok = false.
    fn adb(q: *Quest, args: []const []const u8) !Result {
        const argv = try q.arena.alloc([]const u8, args.len + 1);
        argv[0] = "adb";
        @memcpy(argv[1..], args);
        const r = std.process.run(q.arena, q.io, .{
            .argv = argv,
            .stdout_limit = .limited(1 << 20),
            .stderr_limit = .limited(1 << 20),
            .timeout = .{ .duration = .{ .raw = .fromSeconds(20), .clock = .awake } },
        }) catch |err| switch (err) {
            error.FileNotFound => return error.NoAdb,
            else => return err,
        };
        const ok = r.term == .exited and r.term.exited == 0;
        const out = std.mem.trim(u8, if (r.stdout.len > 0) r.stdout else r.stderr, " \t\r\n");
        return .{ .ok = ok, .out = out };
    }

    fn say(q: *Quest, comptime fmt: []const u8, args: anytype) void {
        q.log.writer.print(fmt ++ "\n", args) catch {};
    }

    /// Every transport adb knows, with details for the ready ones.
    pub fn transports(q: *Quest) ![]Transport {
        const r = try q.adb(&.{"devices"});
        var list: std.ArrayList(Transport) = .empty;
        var lines = std.mem.splitScalar(u8, r.out, '\n');
        while (lines.next()) |raw| {
            const line = std.mem.trim(u8, raw, " \t\r");
            if (line.len == 0 or std.mem.startsWith(u8, line, "List of") or line[0] == '*') continue;
            var f = std.mem.tokenizeAny(u8, line, " \t");
            const serial = f.next() orelse continue;
            const state = f.next() orelse continue;
            var t: Transport = .{ .serial = serial, .state = state, .wifi = std.mem.indexOfScalar(u8, serial, ':') != null };
            if (t.ready()) try q.details(&t);
            try list.append(q.arena, t);
        }
        return list.items;
    }

    fn details(q: *Quest, t: *Transport) !void {
        const sh = try q.adb(&.{ "-s", t.serial, "shell", "ip -4 addr show wlan0; echo @@; pidof " ++ app_package });
        if (sh.ok or sh.out.len > 0) {
            const at = std.mem.indexOf(u8, sh.out, "@@") orelse sh.out.len;
            if (std.mem.indexOf(u8, sh.out[0..at], "inet ")) |i| {
                const rest = sh.out[i + 5 .. at];
                const end = std.mem.indexOfAny(u8, rest, "/ \n") orelse rest.len;
                t.ip = rest[0..end];
            }
            if (at < sh.out.len) t.app_running = std.mem.trim(u8, sh.out[at + 2 ..], " \t\r\n").len > 0;
        }
        const rv = try q.adb(&.{ "-s", t.serial, "reverse", "--list" });
        const want = try std.fmt.allocPrint(q.arena, "tcp:{d} tcp:{d}", .{ q.port, q.port });
        t.forwarded = rv.ok and std.mem.indexOf(u8, rv.out, want) != null;
    }

    /// The transport to use: Wi-Fi over USB, so the forward outlives the cable.
    fn best(list: []const Transport) ?Transport {
        var pick: ?Transport = null;
        for (list) |t| if (t.ready() and (pick == null or (t.wifi and !pick.?.wifi))) {
            pick = t;
        };
        return pick;
    }

    /// Do whatever is needed so the headset reaches the server, without
    /// the cable if possible: switch a USB-connected headset to Wi-Fi, or
    /// reconnect to `known_ip` (its last address), then forward the port.
    /// Returns the headset's IP when known.
    pub fn connect(q: *Quest, known_ip: ?[]const u8) !?[]const u8 {
        var list = try q.transports();
        var ip: ?[]const u8 = known_ip;
        var wifi_ready = false;
        for (list) |t| if (t.ready() and t.wifi) {
            wifi_ready = true;
            ip = t.ip orelse ip;
        };

        if (!wifi_ready) {
            for (list) |t| {
                if (!t.ready() or t.wifi) continue;
                const usb_ip = t.ip orelse {
                    q.say("The headset is on USB but has no Wi-Fi address. Is its Wi-Fi on?", .{});
                    continue;
                };
                ip = usb_ip;
                q.say("Switching adb on the headset to Wi-Fi ({s})…", .{usb_ip});
                const r = try q.adb(&.{ "-s", t.serial, "tcpip", std.fmt.comptimePrint("{d}", .{wifi_port}) });
                if (!r.ok) q.say("  adb tcpip: {s}", .{r.out});
                try q.io.sleep(.fromSeconds(2), .awake);
                break;
            }
            if (ip) |addr| {
                const target = try std.fmt.allocPrint(q.arena, "{s}:{d}", .{ addr, wifi_port });
                var tries: u8 = 0;
                while (tries < 3) : (tries += 1) {
                    const r = try q.adb(&.{ "connect", target });
                    q.say("adb connect {s}: {s}", .{ target, r.out });
                    if (r.ok and std.mem.indexOf(u8, r.out, "connected") != null and
                        std.mem.indexOf(u8, r.out, "cannot") == null and std.mem.indexOf(u8, r.out, "failed") == null) break;
                    try q.io.sleep(.fromSeconds(2), .awake);
                }
            } else if (best(list) == null) {
                q.say("No headset found. Plug it in by USB once (needed after each headset restart), " ++
                    "or enter its Wi-Fi address.", .{});
            }
            list = try q.transports();
        }

        for (list) |t| if (std.mem.eql(u8, t.state, "unauthorized"))
            q.say("{s}: put on the headset and allow USB debugging for this computer.", .{t.serial});

        const t = best(list) orelse return ip;
        if (!t.forwarded) try q.forward(t);
        return t.ip orelse ip;
    }

    fn forward(q: *Quest, t: Transport) !void {
        const spec = try std.fmt.allocPrint(q.arena, "tcp:{d}", .{q.port});
        const r = try q.adb(&.{ "-s", t.serial, "reverse", spec, spec });
        if (r.ok) q.say("Forwarded port {d} over {s}.", .{ q.port, if (t.wifi) "Wi-Fi" else "USB" }) else q.say("adb reverse: {s}", .{r.out});
    }

    /// Start (or bring to the front) the app on the headset.
    pub fn startApp(q: *Quest) !void {
        const t = best(try q.transports()) orelse {
            q.say("No headset connected.", .{});
            return;
        };
        const r = try q.adb(&.{ "-s", t.serial, "shell", "am", "start", "-n", app_package ++ "/com.unity3d.player.UnityPlayerActivity" });
        q.say("{s}", .{if (r.ok) "Started the app on the headset." else r.out});
    }

    /// Drop the Wi-Fi connection (the headset stays in Wi-Fi mode until it restarts).
    pub fn disconnect(q: *Quest) !void {
        for (try q.transports()) |t| if (t.wifi) {
            const r = try q.adb(&.{ "disconnect", t.serial });
            q.say("{s}", .{r.out});
        };
    }

    /// {"adb":true,"port":8093,"transports":[{"serial":…,"state":…,"wifi":…,"ip":…,"forwarded":…,"app":…}]}
    pub fn statusJson(q: *Quest, w: *Io.Writer) !void {
        const list = q.transports() catch |err| {
            try w.print("{{\"adb\":false,\"error\":\"{s}\",\"port\":{d},\"transports\":[]}}", .{ @errorName(err), q.port });
            return;
        };
        try w.print("{{\"adb\":true,\"port\":{d},\"transports\":[", .{q.port});
        for (list, 0..) |t, i| {
            if (i > 0) try w.writeAll(",");
            try w.writeAll("{\"serial\":");
            try std.json.Stringify.encodeJsonString(t.serial, .{}, w);
            try w.writeAll(",\"state\":");
            try std.json.Stringify.encodeJsonString(t.state, .{}, w);
            try w.print(",\"wifi\":{},\"forwarded\":{},\"app\":{},\"ip\":", .{ t.wifi, t.forwarded, t.app_running });
            if (t.ip) |ip| try std.json.Stringify.encodeJsonString(ip, .{}, w) else try w.writeAll("null");
            try w.writeAll("}");
        }
        try w.writeAll("]}");
    }
};

/// An IPv4 address, nothing else: it ends up in an adb argument.
pub fn validIp(s: []const u8) bool {
    if (s.len == 0 or s.len > 15) return false;
    var parts: u8 = 0;
    var it = std.mem.splitScalar(u8, s, '.');
    while (it.next()) |p| {
        parts += 1;
        _ = std.fmt.parseInt(u8, p, 10) catch return false;
    }
    return parts == 4;
}

test validIp {
    try std.testing.expect(validIp("192.168.12.228"));
    try std.testing.expect(!validIp("192.168.12"));
    try std.testing.expect(!validIp("1.2.3.4;rm"));
    try std.testing.expect(!validIp("256.1.1.1"));
}
