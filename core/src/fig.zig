//! Figures from code: render a `fig` (Python/matplotlib or Octave) to
//! `<cache>/<key>.svg`, `.png` and `.json` (where its axes are, for pointing).
//!
//! The code comes from a lesson, which usually comes from a language model,
//! so it runs in a sandbox (bubblewrap): no network, no other processes, the
//! file system read-only except a scratch directory, nothing of $HOME, a time
//! limit here and CPU/memory limits in the render script. Only with
//! `--unsafe-figs` (no bwrap available, and you trust the lessons) does it run
//! directly.
//!
//! Interpreters: $VCL_PYTHON (a Python with numpy and matplotlib; the dev shell
//! sets one with scipy, sympy and networkx too) or python3 on PATH; $VCL_OCTAVE
//! or octave-cli on PATH, with gnuplot and rsvg-convert ($VCL_GNUPLOT,
//! $VCL_RSVG: their bin directories).
const std = @import("std");
const Io = std.Io;
const Allocator = std.mem.Allocator;

const render_py = @embedFile("fig/render.py");
const render_m = @embedFile("fig/render.m");

pub const Config = struct {
    /// Absolute paths (null: not found).
    python: ?[]const u8 = null,
    octave: ?[]const u8 = null,
    /// gnuplot's and rsvg-convert's bin directories, put on Octave's PATH.
    gnuplot_bin: ?[]const u8 = null,
    rsvg_bin: ?[]const u8 = null,
    bwrap: ?[]const u8 = null,
    /// Run without the sandbox (only when asked for).
    unsafe: bool = false,
    /// Where rendered figures are kept (absolute).
    cache: []const u8,
    timeout_s: i64 = 60,
};

/// Find what figures need, from the environment (see the top of this file).
pub fn configure(gpa: Allocator, io: Io, env: *const std.process.Environ.Map) !Config {
    const cache = if (env.get("VCL_FIG_CACHE")) |c|
        try gpa.dupe(u8, c)
    else if (env.get("XDG_CACHE_HOME")) |x|
        try std.fs.path.join(gpa, &.{ x, "vcl", "figs" })
    else
        try std.fs.path.join(gpa, &.{ env.get("HOME") orelse "/tmp", ".cache", "vcl", "figs" });
    return .{
        .python = try exe(gpa, io, env, "VCL_PYTHON", "python3"),
        .octave = try exe(gpa, io, env, "VCL_OCTAVE", "octave-cli"),
        .gnuplot_bin = if (env.get("VCL_GNUPLOT")) |g| try gpa.dupe(u8, g) else if (try which(gpa, io, env, "gnuplot")) |g| std.fs.path.dirname(g) else null,
        .rsvg_bin = if (env.get("VCL_RSVG")) |g| try gpa.dupe(u8, g) else if (try which(gpa, io, env, "rsvg-convert")) |g| std.fs.path.dirname(g) else null,
        .bwrap = try exe(gpa, io, env, "VCL_BWRAP", "bwrap"),
        .cache = cache,
    };
}

fn exe(gpa: Allocator, io: Io, env: *const std.process.Environ.Map, var_name: []const u8, name: []const u8) !?[]const u8 {
    if (env.get(var_name)) |p| return try realOr(gpa, io, p);
    return which(gpa, io, env, name);
}

/// The real path of an executable on PATH (symlinks resolved, so it can be found inside the sandbox).
fn which(gpa: Allocator, io: Io, env: *const std.process.Environ.Map, name: []const u8) !?[]const u8 {
    var it = std.mem.splitScalar(u8, env.get("PATH") orelse return null, ':');
    while (it.next()) |dir| {
        if (dir.len == 0) continue;
        const cand = try std.fs.path.join(gpa, &.{ dir, name });
        defer gpa.free(cand);
        Io.Dir.accessAbsolute(io, cand, .{ .execute = true }) catch continue;
        return try realOr(gpa, io, cand);
    }
    return null;
}

fn realOr(gpa: Allocator, io: Io, p: []const u8) ![]const u8 {
    if (!std.fs.path.isAbsolute(p)) return try gpa.dupe(u8, p);
    return Io.Dir.realPathFileAbsoluteAlloc(io, p, gpa) catch try gpa.dupe(u8, p);
}

pub const Outcome = union(enum) {
    ok,
    /// The message (owned by the caller's allocator). `drawn`: an error figure
    /// was still written (the code failed, not the machinery).
    failed: struct { msg: []u8, drawn: bool },
};

pub fn isKey(s: []const u8) bool {
    if (s.len != 16) return false;
    for (s) |c| if (!std.ascii.isHex(c)) return false;
    return true;
}

/// Already rendered (successfully or as an error figure)?
pub fn cached(io: Io, cfg: Config, key: []const u8) bool {
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const p = std.fmt.bufPrint(&buf, "{s}/{s}.png", .{ cfg.cache, key }) catch return false;
    Io.Dir.accessAbsolute(io, p, .{}) catch return false;
    return true;
}

/// The cached error message of a figure whose code failed, if any.
pub fn cachedError(gpa: Allocator, io: Io, cfg: Config, key: []const u8) ?[]u8 {
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const p = std.fmt.bufPrint(&buf, "{s}/{s}.err", .{ cfg.cache, key }) catch return null;
    return Io.Dir.cwd().readFileAlloc(io, p, gpa, .limited(4096)) catch null;
}

/// Render one figure into the cache (a no-op when it is there already).
pub fn render(gpa: Allocator, io: Io, cfg: Config, lang: []const u8, code: []const u8, key: []const u8) !Outcome {
    if (!isKey(key)) return fail(gpa, "bad figure key", .{});
    if (cached(io, cfg, key)) {
        if (cachedError(gpa, io, cfg, key)) |m| return .{ .failed = .{ .msg = m, .drawn = true } };
        return .ok;
    }
    const python = std.mem.eql(u8, lang, "python");
    const interp = (if (python) cfg.python else cfg.octave) orelse return if (python)
        fail(gpa, "no Python for figures: run `vcl serve` inside `nix develop` (it has matplotlib), or set VCL_PYTHON to a python3 with numpy and matplotlib", .{})
    else
        fail(gpa, "no Octave for figures: run `vcl serve` inside `nix develop`, or set VCL_OCTAVE to octave-cli", .{});
    if (cfg.bwrap == null and !cfg.unsafe)
        return fail(gpa, "figures run in a sandbox, which needs bubblewrap (bwrap) on PATH; or start vcl serve with --unsafe-figs if you trust the lessons", .{});

    var cache = try Io.Dir.cwd().createDirPathOpen(io, cfg.cache, .{});
    defer cache.close(io);
    var name_buf: [64]u8 = undefined;
    const now: u64 = @truncate(@as(u128, @bitCast(@as(i128, Io.Clock.real.now(io).nanoseconds))));
    const work_name = try std.fmt.bufPrint(&name_buf, ".work-{s}-{x}", .{ key, now });
    var work = try cache.createDirPathOpen(io, work_name, .{});
    defer {
        work.close(io);
        cache.deleteTree(io, work_name) catch {};
    }
    try work.writeFile(io, .{ .sub_path = if (python) "code.py" else "code.m", .data = code });
    try work.writeFile(io, .{ .sub_path = if (python) "render.py" else "render.m", .data = if (python) render_py else render_m });
    const work_abs = try std.fs.path.join(gpa, &.{ cfg.cache, work_name });
    defer gpa.free(work_abs);

    // The command: the interpreter on the render script, in the sandbox.
    var argv: std.ArrayList([]const u8) = .empty;
    defer argv.deinit(gpa);
    const path_env = try std.fmt.allocPrint(gpa, "{s}{s}{s}{s}{s}:/bin:/usr/bin", .{
        std.fs.path.dirname(interp) orelse "/usr/bin",
        if (!python and cfg.gnuplot_bin != null) ":" else "",
        if (!python) cfg.gnuplot_bin orelse "" else "",
        if (!python and cfg.rsvg_bin != null) ":" else "",
        if (!python) cfg.rsvg_bin orelse "" else "",
    });
    defer gpa.free(path_env);
    const cwd_in: []const u8 = if (cfg.bwrap != null and !cfg.unsafe) "/work" else work_abs;
    if (cfg.bwrap != null and !cfg.unsafe) {
        try argv.appendSlice(gpa, &.{
            cfg.bwrap.?,          "--unshare-all",   "--die-with-parent", "--new-session", "--clearenv",
            "--ro-bind",          "/nix/store",      "/nix/store",
            // Elsewhere than NixOS the interpreter lives in the usual places.
            "--ro-bind-try",      "/usr",            "/usr",
            "--ro-bind-try",      "/lib",            "/lib",
            "--ro-bind-try",      "/lib64",          "/lib64",
            "--ro-bind-try",      "/bin",            "/bin",
            "--ro-bind-try",      "/etc/fonts",      "/etc/fonts",
            "--ro-bind-try",      "/etc/ld.so.cache", "/etc/ld.so.cache",
            "--bind",             work_abs,          "/work",
            "--proc",             "/proc",           "--dev",
            "/dev",               "--tmpfs",         "/tmp",
            "--chdir",            "/work",           "--setenv",
            "HOME",               "/work",           "--setenv",
            "MPLCONFIGDIR",       "/work",           "--setenv",
            "OPENBLAS_NUM_THREADS", "1",             "--setenv",
            "OMP_NUM_THREADS",    "1",               "--setenv",
            "PATH",               path_env,          "--",
        });
    }
    if (python) {
        try argv.appendSlice(gpa, &.{ interp, "-I", "render.py" });
    } else {
        try argv.appendSlice(gpa, &.{ interp, "--norc", "--quiet", "--no-history", "--no-window-system", "render.m" });
    }

    var env_map: std.process.Environ.Map = .init(gpa);
    defer env_map.deinit();
    try env_map.put("HOME", work_abs);
    try env_map.put("MPLCONFIGDIR", work_abs);
    try env_map.put("PATH", path_env);
    try env_map.put("OPENBLAS_NUM_THREADS", "1");

    const r = std.process.run(gpa, io, .{
        .argv = argv.items,
        .cwd = if (cfg.bwrap != null and !cfg.unsafe) .inherit else .{ .path = work_abs },
        .environ_map = &env_map,
        .stdout_limit = .limited(1 << 20),
        .stderr_limit = .limited(1 << 20),
        .timeout = .{ .duration = .{ .raw = .fromSeconds(cfg.timeout_s), .clock = .awake } },
    }) catch |err| return switch (err) {
        error.Timeout => fail(gpa, "the figure took longer than {d} s and was stopped", .{cfg.timeout_s}),
        else => fail(gpa, "couldn't run {s}: {s}", .{ cwd_in, @errorName(err) }),
    };
    defer gpa.free(r.stdout);
    defer gpa.free(r.stderr);

    const drew = if (work.access(io, "out.png", .{})) true else |_| false;
    if (!drew) {
        const tail = lastLines(r.stderr, 4);
        return fail(gpa, "{s} failed: {s}", .{ if (python) "python" else "octave", if (tail.len > 0) tail else "no output (killed?)" });
    }
    // Into the cache: the images, where the axes are, and the error if the code failed.
    var nb: [64]u8 = undefined;
    for ([_][2][]const u8{ .{ "out.svg", ".svg" }, .{ "meta.json", ".json" }, .{ "err.txt", ".err" }, .{ "out.png", ".png" } }) |pair| {
        const dest = try std.fmt.bufPrint(&nb, "{s}{s}", .{ key, pair[1] });
        work.rename(pair[0], cache, dest, io) catch {};
    }
    if (cache.readFileAlloc(io, (try std.fmt.bufPrint(&nb, "{s}.err", .{key})), gpa, .limited(4096))) |m|
        return .{ .failed = .{ .msg = m, .drawn = true } }
    else |_| {}
    return .ok;
}

fn fail(gpa: Allocator, comptime fmt: []const u8, args: anytype) !Outcome {
    return .{ .failed = .{ .msg = try std.fmt.allocPrint(gpa, fmt, args), .drawn = false } };
}

/// The last `n` non-empty lines of some output, joined with " | ".
fn lastLines(text: []const u8, n: usize) []const u8 {
    const t = std.mem.trimEnd(u8, text, " \n\r\t");
    var start = t.len;
    var count: usize = 0;
    while (start > 0 and count < n) {
        start -= 1;
        if (t[start] == '\n') count += 1;
    }
    if (start < t.len and t[start] == '\n') start += 1;
    return t[start..];
}

test lastLines {
    try std.testing.expectEqualStrings("b\nc", lastLines("a\nb\nc\n", 2));
    try std.testing.expectEqualStrings("a\nb", lastLines("a\nb", 5));
}

test isKey {
    try std.testing.expect(isKey("0123456789abcdef"));
    try std.testing.expect(!isKey("../../etc/passwd"));
}
