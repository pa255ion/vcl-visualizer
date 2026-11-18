//! Native build of tts-lab (github.com/pa255ion/tts-lab) for the Unity client:
//! libtts.so for the Quest (aarch64) and the Linux editor (x86_64). Same API as
//! the browser build (core/src/tts_wasm.zig); C# calls it through P/Invoke.
//!
//! Usage (see clients/unity/Assets/Vcl/Scripts/Voice.cs):
//!   p = tts_alloc(n); write UTF-8 text at p; tts_speak(p, n, engine, rate, pitch); tts_free(p, n)
//!   then read tts_samples()/tts_len() (mono f32) at tts_sample_rate(), lasting tts_seconds().
//! Engines: 0 classic formant, 1 refined formant, 2 diphone, 3 unit selection, 4 HMM.
//! The formant engines start instantly; the others build a voice on first use.

const std = @import("std");
const tts = @import("tts");

// Whole pages from the kernel, no thread-locals. Inside Unity's process on the
// Quest, a general-purpose heap here crashed writing just past a block (static
// test programs on the same headset ran clean, where the next page happened to
// be mapped); page-granular blocks leave the slack that tolerates it.
const gpa = std.heap.page_allocator;

// A fixed page size, so std never asks the kernel's auxiliary vector at run
// time: that goes through `getauxval`, which a libc-free library has nobody to
// provide (Android's loader won't find bionic's for it). The Quest's pages are
// 4 KiB, as are x86_64's.
pub const std_options: std.Options = .{ .page_size_min = 4096, .page_size_max = 4096 };

var synth: ?*tts.Synth = null;
var last: ?tts.Result = null;

export fn tts_alloc(n: usize) ?[*]u8 {
    const s = gpa.alloc(u8, @max(n, 1)) catch return null;
    return s.ptr;
}

export fn tts_free(p: [*]u8, n: usize) void {
    gpa.free(p[0..@max(n, 1)]);
}

/// Synthesize text; 0 on success, negative on failure. Replaces the previous result.
export fn tts_speak(p: [*]const u8, n: usize, engine: u32, rate: f32, pitch: f32) i32 {
    if (engine >= tts.Engine.count) return -3;
    if (synth == null) synth = tts.Synth.init(gpa) catch return -1;
    if (last) |r| r.deinit(gpa);
    last = null;
    last = synth.?.speak(gpa, p[0..n], .{
        .engine = @enumFromInt(engine),
        .prosody = .{ .rate = rate },
        .pitch_scale = pitch,
    }) catch return -2;
    return 0;
}

export fn tts_samples() [*]const f32 {
    return if (last) |r| r.samples.ptr else undefined;
}

export fn tts_len() usize {
    return if (last) |r| r.samples.len else 0;
}

export fn tts_seconds() f64 {
    return if (last) |r| r.audio_seconds else 0;
}

export fn tts_sample_rate() u32 {
    const r = last orelse return 0;
    if (r.audio_seconds <= 0) return 0;
    return @intFromFloat(@round(@as(f64, @floatFromInt(r.samples.len)) / r.audio_seconds));
}


// std still looks up the vDSO (for clocks) through an external `getauxval`.
// A libc-free library has nobody to provide it, and Android's loader won't
// bind it to bionic's, so read /proc/self/auxv. Returning 0 is safe: std then
// falls back to plain system calls.
var auxv: [64]usize = undefined;
var auxv_len: usize = 0;
var auxv_read = false;

comptime {
    // An executable gets std's own (from its startup code); only the library needs this.
    if (@import("builtin").output_mode == .Lib) @export(&getauxvalProc, .{ .name = "getauxval" });
}

fn getauxvalProc(index: usize) callconv(.c) usize {
    const linux = std.os.linux;
    if (!auxv_read) {
        auxv_read = true;
        const rc = linux.open("/proc/self/auxv", .{ .ACCMODE = .RDONLY }, 0);
        if (linux.errno(rc) == .SUCCESS) {
            const fd: i32 = @intCast(rc);
            const bytes = std.mem.sliceAsBytes(auxv[0..]);
            var got: usize = 0;
            while (got < bytes.len) {
                const n = linux.read(fd, bytes[got..].ptr, bytes.len - got);
                if (linux.errno(n) != .SUCCESS or n == 0) break;
                got += n;
            }
            auxv_len = got / @sizeOf(usize);
            _ = linux.close(fd);
        }
    }
    var i: usize = 0;
    while (i + 1 < auxv_len) : (i += 2) {
        if (auxv[i] == 0) break;
        if (auxv[i] == index) return auxv[i + 1];
    }
    return 0;
}
