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

// One heap for the synth, no thread-locals: a dlopen'd library with a TLS
// segment and no libc is asking for trouble on Android's loader. Unity calls
// from its main thread only.
var heap: std.heap.DebugAllocator(.{ .thread_safe = false, .safety = false }) = .init;
const gpa = heap.allocator();

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
