//! Browser build of the VCL parser (viewer/vcl.wasm).
//!
//! JS usage (see viewer/js/vcl-wasm.js):
//!   p = vcl_alloc(n); write UTF-8 into memory at p; vcl_feed(p, n); vcl_free(p, n)
//!   read vcl_out_ptr()/vcl_out_len() → newline-delimited IR JSON
//! One global scene + stream; vcl_reset(mode) starts over (0 = vcl, 1 = mixed).
//! `data` commands can't run here: their results, computed ahead of time
//! (figs/<key>.data.json), are handed in with vcl_put_data; vcl_data_keys
//! lists the keys a text needs.

const std = @import("std");
const vcl = @import("vcl");

const gpa = std.heap.wasm_allocator;

var scene: vcl.Scene = .{ .gpa = gpa, .compute = .{ .ctx = &unused, .run = lookup } };
var stream: vcl.Stream = .{ .mode = .vcl };
var out: std.Io.Writer.Allocating = .init(gpa);

/// `data` results handed in by the page: key → JSON.
var results: std.StringHashMapUnmanaged([]u8) = .empty;
var unused: u8 = 0;

fn lookup(_: *anyopaque, _: []const u8, _: []const u8, key: []const u8) vcl.Scene.Computed {
    const j = results.get(key) orelse return .missing;
    return .{ .json = gpa.dupe(u8, j) catch return .missing };
}

/// While listing keys: note each one (nothing is run).
var wanted: std.ArrayList(u8) = .empty;
fn note(_: *anyopaque, _: []const u8, _: []const u8, key: []const u8) vcl.Scene.Computed {
    if (std.mem.indexOf(u8, wanted.items, key) == null) {
        wanted.appendSlice(gpa, key) catch {};
        wanted.append(gpa, '\n') catch {};
    }
    return .missing;
}

/// A `data` command's results (JSON) for its key.
export fn vcl_put_data(kp: [*]const u8, kn: usize, jp: [*]const u8, jn: usize) void {
    const j = gpa.dupe(u8, jp[0..jn]) catch return;
    if (results.getPtr(kp[0..kn])) |old| {
        gpa.free(old.*);
        old.* = j;
        return;
    }
    const k = gpa.dupe(u8, kp[0..kn]) catch return;
    results.put(gpa, k, j) catch {};
}

/// The keys of the `data` commands in a text (newline-separated, in the output buffer).
export fn vcl_data_keys(p: [*]const u8, n: usize, mode: u32) u32 {
    out.clearRetainingCapacity();
    wanted.clearRetainingCapacity();
    var dry: vcl.Scene = .{ .gpa = gpa, .compute = .{ .ctx = &unused, .run = note } };
    defer dry.deinit();
    var st: vcl.Stream = .{ .mode = if (mode == 1) .mixed else .vcl };
    defer st.deinit(gpa);
    var sink: std.Io.Writer.Allocating = .init(gpa);
    defer sink.deinit();
    st.feed(&dry, p[0..n], &sink.writer) catch return 1;
    st.finish(&dry, &sink.writer) catch return 1;
    out.writer.writeAll(wanted.items) catch return 1;
    return 0;
}

export fn vcl_alloc(n: usize) ?[*]u8 {
    const s = gpa.alloc(u8, n) catch return null;
    return s.ptr;
}

export fn vcl_free(p: [*]u8, n: usize) void {
    gpa.free(p[0..n]);
}

export fn vcl_reset(mode: u32) void {
    scene.reset();
    scene.board = vcl.default_board;
    stream.deinit(gpa);
    stream = .{ .mode = if (mode == 1) .mixed else .vcl };
}

/// Feed a chunk. Returns 0 on success. Output replaces the previous output.
export fn vcl_feed(p: [*]const u8, n: usize, finish: bool) u32 {
    out.clearRetainingCapacity();
    stream.feed(&scene, p[0..n], &out.writer) catch return 1;
    if (finish) stream.finish(&scene, &out.writer) catch return 1;
    return 0;
}

export fn vcl_out_ptr() [*]const u8 {
    return out.written().ptr;
}

export fn vcl_out_len() usize {
    return out.written().len;
}
