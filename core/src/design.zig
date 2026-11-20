//! `vcl check --design`: does a lesson follow docs/LESSON-DESIGN.md? Reads a
//! lesson's IR and reports on its shape rather than its syntax: questions
//! named (their names carry the learner's answer history), retrieval or a
//! prediction early, a question every 1–2 minutes of talk, review questions
//! from earlier lessons, a learner recap near the end, board text that only
//! repeats the speech, and sentences too long to follow by ear.
const std = @import("std");
const Allocator = std.mem.Allocator;

pub const Finding = struct {
    line: ?u32, // null: about the whole lesson
    msg: []const u8,
};

const Question = struct { line: u32, op: []const u8, id: ?[]const u8, predict: bool, review: bool, at: f64 };
const Say = struct { line: u32, text: []const u8, at: f64 };
const Text = struct { line: u32, text: []const u8, says_before: usize };

/// Findings about the lesson whose IR (JSON lines) is `ir`. Strings live in `a`.
pub fn review(a: Allocator, ir: []const u8) ![]Finding {
    var out: std.ArrayList(Finding) = .empty;
    var questions: std.ArrayList(Question) = .empty;
    var says: std.ArrayList(Say) = .empty;
    var texts: std.ArrayList(Text) = .empty;
    var talk: f64 = 0; // seconds of speech so far

    var it = std.mem.splitScalar(u8, ir, '\n');
    while (it.next()) |raw| {
        if (raw.len == 0) continue;
        const v = std.json.parseFromSliceLeaky(std.json.Value, a, raw, .{}) catch continue;
        if (v != .object) continue;
        const o = v.object;
        const op = str(o, "op") orelse continue;
        const line: u32 = if (o.get("line")) |l| (if (l == .integer) @intCast(l.integer) else 0) else 0;
        if (std.mem.eql(u8, op, "say")) {
            const t = str(o, "text") orelse "";
            try says.append(a, .{ .line = line, .text = t, .at = talk });
            talk += num(o, "dur");
            if (longestSentence(t) > 30)
                try out.append(a, .{ .line = line, .msg = try std.fmt.allocPrint(a, "a {d}-word sentence; a synthetic voice is easier to follow in short ones", .{longestSentence(t)}) });
        } else if (isQuestion(op)) {
            try questions.append(a, .{
                .line = line, .op = op, .id = str(o, "id"), .at = talk,
                .predict = if (o.get("predict")) |p| p == .bool and p.bool else false,
                .review = o.get("from") != null,
            });
        } else if (std.mem.eql(u8, op, "text") or std.mem.eql(u8, op, "node")) {
            if (str(o, "text")) |t| try texts.append(a, .{ .line = line, .text = t, .says_before = says.items.len });
        }
    }

    // Names: present and unique (they key the learner's answers).
    for (questions.items, 0..) |q, i| {
        const id = q.id orelse {
            try out.append(a, .{ .line = q.line, .msg = try std.fmt.allocPrint(a, "name this {s} (q<lesson>_<n>): the name ties the learner's answers to it, across versions of the lesson", .{q.op}) });
            continue;
        };
        for (questions.items[0..i]) |p| if (p.id != null and std.mem.eql(u8, p.id.?, id)) {
            try out.append(a, .{ .line = q.line, .msg = try std.fmt.allocPrint(a, "question name '{s}' is used twice (line {d} too); each question needs its own", .{ id, p.line }) });
            break;
        };
    }

    // Pacing: something to answer early, and every couple of minutes.
    const minutes = talk / 60;
    if (questions.items.len == 0) {
        if (talk > 60) try out.append(a, .{ .line = null, .msg = try std.fmt.allocPrint(a, "no questions in about {d:.0} minutes of talk; retrieval is what makes it stick", .{minutes}) });
    } else {
        const first = questions.items[0];
        if (first.at > 120)
            try out.append(a, .{ .line = first.line, .msg = try std.fmt.allocPrint(a, "about {d:.0} minutes of talk before the first question; open with retrieval of what came before, or a prediction", .{first.at / 60}) });
        var last_at: f64 = first.at;
        var last_line: u32 = first.line;
        for (questions.items[1..]) |q| {
            if (q.at - last_at > 180)
                try out.append(a, .{ .line = q.line, .msg = try std.fmt.allocPrint(a, "about {d:.0} minutes of talk since the last question (line {d}); check the idea every 1–2 minutes", .{ (q.at - last_at) / 60, last_line }) });
            last_at = q.at;
            last_line = q.line;
        }
        if (talk - last_at > 180)
            try out.append(a, .{ .line = null, .msg = try std.fmt.allocPrint(a, "about {d:.0} minutes of talk after the last question (line {d}); end with a final check", .{ (talk - last_at) / 60, last_line }) });
        if (talk > 240 and @as(f64, @floatFromInt(questions.items.len)) * 120 < talk)
            try out.append(a, .{ .line = null, .msg = try std.fmt.allocPrint(a, "{d} questions in about {d:.0} minutes of talk; aim for one every 1–2 minutes", .{ questions.items.len, minutes }) });
        var predicts: usize = 0;
        var reviews: usize = 0;
        var late_recall = false;
        for (questions.items) |q| {
            if (q.predict) predicts += 1;
            if (q.review) reviews += 1;
            if (std.mem.eql(u8, q.op, "recall") and q.at >= talk * 0.7) late_recall = true;
        }
        if (predicts == 0 and talk > 120)
            try out.append(a, .{ .line = null, .msg = "no prediction question (predict=true) before new material: a guess first helps, even a wrong one" });
        if (reviews < 2 and talk > 240)
            try out.append(a, .{ .line = null, .msg = try std.fmt.allocPrint(a, "{d} review question(s) from earlier lessons (from=\"…\"); mix in at least two, from unrelated ones", .{reviews}) });
        if (!late_recall and talk > 240)
            try out.append(a, .{ .line = null, .msg = "no recall card near the end: let the learner recap today's ideas before you do" });
    }

    // Board text that says what the voice says: the board is for structure.
    for (texts.items) |t| {
        if (wordCount(t.text) < 5) continue;
        const lo = t.says_before -| 2;
        const hi = @min(says.items.len, t.says_before + 2);
        for (says.items[lo..hi]) |s| if (sameWords(a, t.text, s.text)) {
            try out.append(a, .{ .line = t.line, .msg = try std.fmt.allocPrint(a, "this board text repeats what is said (line {d}); keep the board to labels and symbols", .{s.line}) });
            break;
        };
    }
    std.mem.sort(Finding, out.items, {}, struct {
        fn lt(_: void, x: Finding, y: Finding) bool {
            return (x.line orelse std.math.maxInt(u32)) < (y.line orelse std.math.maxInt(u32));
        }
    }.lt);
    return out.items;
}

fn isQuestion(op: []const u8) bool {
    for ([_][]const u8{ "quiz", "pick", "estimate", "recall" }) |q| if (std.mem.eql(u8, op, q)) return true;
    return false;
}

fn str(o: std.json.ObjectMap, k: []const u8) ?[]const u8 {
    const v = o.get(k) orelse return null;
    return if (v == .string) v.string else null;
}

fn num(o: std.json.ObjectMap, k: []const u8) f64 {
    const v = o.get(k) orelse return 0;
    return switch (v) {
        .float => |f| f,
        .integer => |i| @floatFromInt(i),
        else => 0,
    };
}

fn wordCount(t: []const u8) usize {
    var n: usize = 0;
    var it = std.mem.tokenizeAny(u8, t, " \t\n");
    while (it.next()) |_| n += 1;
    return n;
}

/// Words in the longest sentence (split at . ! ? and ;).
fn longestSentence(t: []const u8) usize {
    var best: usize = 0;
    var it = std.mem.tokenizeAny(u8, t, ".!?;");
    while (it.next()) |s| best = @max(best, wordCount(s));
    return best;
}

/// Is the board text, word for word (ignoring case and punctuation), inside the sentence?
fn sameWords(a: Allocator, board: []const u8, spoken: []const u8) bool {
    const b = normalize(a, board) catch return false;
    const s = normalize(a, spoken) catch return false;
    return b.len > 0 and std.mem.indexOf(u8, s, b) != null;
}

fn normalize(a: Allocator, t: []const u8) ![]const u8 {
    var out: std.ArrayList(u8) = .empty;
    var space = true;
    for (t) |c| {
        if (std.ascii.isAlphanumeric(c)) {
            try out.append(a, std.ascii.toLower(c));
            space = false;
        } else if (!space) {
            try out.append(a, ' ');
            space = true;
        }
    }
    return std.mem.trim(u8, out.items, " ");
}

test review {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const ir =
        \\{"op":"say","line":1,"dur":130,"text":"A long introduction. The clock costs energy every cycle."}
        \\{"op":"text","id":"t","line":2,"text":"The clock costs energy every cycle"}
        \\{"op":"quiz","line":3,"q":"?","options":["a","b"],"answer":[1]}
        \\{"op":"recall","id":"q1","line":4,"q":"?","answer":"x"}
        \\{"op":"say","line":5,"dur":200,"text":"More."}
        \\{"op":"estimate","id":"q1","line":6,"q":"?","range":[0,1],"answer":0.3,"from":"8.2"}
    ;
    const found = try review(arena.allocator(), ir);
    var msgs: std.ArrayList(u8) = .empty;
    for (found) |x| try msgs.print(arena.allocator(), "{?d}: {s}\n", .{ x.line, x.msg });
    const m = msgs.items;
    errdefer std.debug.print("{s}", .{m});
    const has = struct {
        fn in(hay: []const u8, needle: []const u8) bool {
            return std.mem.indexOf(u8, hay, needle) != null;
        }
    }.in;
    try std.testing.expect(has(m, "2: this board text repeats what is said (line 1)"));
    try std.testing.expect(has(m, "3: about 2 minutes of talk before the first question"));
    try std.testing.expect(has(m, "3: name this quiz"));
    try std.testing.expect(has(m, "6: question name 'q1' is used twice (line 4 too)"));
    try std.testing.expect(has(m, "6: about 3 minutes of talk since the last question (line 4)"));
    try std.testing.expect(has(m, "no prediction question"));
    try std.testing.expect(has(m, "1 review question(s)"));
    try std.testing.expect(has(m, "no recall card near the end"));
}
