const std = @import("std");

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});

    const vcl = b.createModule(.{ .root_source_file = b.path("core/src/vcl.zig"), .target = target, .optimize = optimize });

    // CLI + stream server
    const exe = b.addExecutable(.{
        .name = "vcl",
        .root_module = b.createModule(.{
            .root_source_file = b.path("core/src/main.zig"),
            .target = target,
            .optimize = optimize,
            .imports = &.{.{ .name = "vcl", .module = vcl }},
        }),
    });
    b.installArtifact(exe);

    const run = b.addRunArtifact(exe);
    run.step.dependOn(b.getInstallStep());
    if (b.args) |args| run.addArgs(args);
    b.step("run", "Run the vcl CLI (e.g. zig build run -- serve)").dependOn(&run.step);

    // Browser parser: viewer/vcl.wasm
    const wasm = b.addExecutable(.{
        .name = "vcl",
        .root_module = b.createModule(.{
            .root_source_file = b.path("core/src/wasm.zig"),
            .target = b.resolveTargetQuery(.{ .cpu_arch = .wasm32, .os_tag = .freestanding }),
            .optimize = .ReleaseSmall,
            .imports = &.{.{ .name = "vcl", .module = b.createModule(.{
                .root_source_file = b.path("core/src/vcl.zig"),
                .target = b.resolveTargetQuery(.{ .cpu_arch = .wasm32, .os_tag = .freestanding }),
                .optimize = .ReleaseSmall,
            }) }},
        }),
    });
    wasm.entry = .disabled;
    wasm.rdynamic = true;
    // Speech: tts-lab (github.com/pa255ion/tts-lab) for the browser → viewer/tts.wasm
    const wasm_target = b.resolveTargetQuery(.{ .cpu_arch = .wasm32, .os_tag = .freestanding });
    const tts_dep = b.dependency("tts_lab", .{});
    const tts_wasm = b.addExecutable(.{
        .name = "tts",
        .root_module = b.createModule(.{
            .root_source_file = b.path("core/src/tts_wasm.zig"),
            .target = wasm_target,
            .optimize = .ReleaseSmall,
            .imports = &.{.{ .name = "tts", .module = b.createModule(.{
                .root_source_file = tts_dep.path("src/tts.zig"),
                .target = wasm_target,
                .optimize = .ReleaseSmall,
            }) }},
        }),
    });
    tts_wasm.entry = .disabled;
    tts_wasm.rdynamic = true;

    const copy = b.addUpdateSourceFiles();
    copy.addCopyFileToSource(wasm.getEmittedBin(), "viewer/vcl.wasm");
    copy.addCopyFileToSource(tts_wasm.getEmittedBin(), "viewer/tts.wasm");
    b.step("wasm", "Build viewer/vcl.wasm and viewer/tts.wasm").dependOn(&copy.step);
    b.getInstallStep().dependOn(&copy.step);

    // Tests
    const tests = b.addTest(.{ .root_module = vcl });
    b.step("test", "Run parser tests").dependOn(&b.addRunArtifact(tests).step);
}
