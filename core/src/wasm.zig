//! Browser build of the VCL parser (viewer/vcl.wasm).
//!
//! JS usage (see viewer/js/vcl-wasm.js):
//!   p = vcl_alloc(n); write UTF-8 into memory at p; vcl_feed(p, n); vcl_free(p, n)
//!   read vcl_out_ptr()/vcl_out_len() → newline-delimited IR JSON
//! One global scene + stream; vcl_reset(mode) starts over (0 = vcl, 1 = mixed).

const std = @import("std");
const vcl = @import("vcl");

const gpa = std.heap.wasm_allocator;

var scene: vcl.Scene = .{ .gpa = gpa };
var stream: vcl.Stream = .{ .mode = .vcl };
var out: std.Io.Writer.Allocating = .init(gpa);

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
