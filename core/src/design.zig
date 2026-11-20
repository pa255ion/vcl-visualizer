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
const Text = struct { line: u32, text: []const u8, says_before: usize, at: f64 };

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
            if (str(o, "text")) |t| try texts.append(a, .{ .line = line, .text = t, .says_before = says.items.len, .at = talk });
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
        // The teacher's recap board: the first board text near the end that says so.
        var recap: ?Text = null;
        for (texts.items) |t| if (t.at >= talk * 0.6 and isRecap(t.text)) {
            recap = t;
            break;
        };
        var late_recall = false;
        var recall_after: ?u32 = null; // a late recall that comes only after the recap board
        for (questions.items) |q| {
            if (q.predict) predicts += 1;
            if (q.review) reviews += 1;
            if (std.mem.eql(u8, q.op, "recall") and q.at >= talk * 0.6) {
                if (recap != null and q.line > recap.?.line) {
                    recall_after = q.line;
                } else late_recall = true;
            }
        }
        if (!late_recall and recap != null and talk > 240) {
            try out.append(a, .{ .line = recap.?.line, .msg = if (recall_after) |l|
                try std.fmt.allocPrint(a, "your recap board comes before the learner's (the recall on line {d}); ask them to list the ideas first", .{l})
            else
                "a recap board, but no recall card before it: let the learner recap today's ideas before you do" });
            late_recall = true;
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

/// Words in the longest sentence (split at . ! ? and ;). A number spoken
/// as words ("one point eight five five") counts as one word.
fn longestSentence(t: []const u8) usize {
    var best: usize = 0;
    var start: usize = 0;
    var i: usize = 0;
    while (i <= t.len) : (i += 1) {
        // A sentence ends at ! ? ; or a full stop that isn't inside a number (1.855).
        const end = i == t.len or switch (t[i]) {
            '!', '?', ';' => true,
            '.' => i + 1 == t.len or !std.ascii.isDigit(t[i + 1]),
            else => false,
        };
        if (!end) continue;
        const s = t[start..i];
        start = i + 1;
        var n: usize = 0;
        var in_number = false;
        var w = std.mem.tokenizeAny(u8, s, " \t\n,");
        while (w.next()) |word| {
            const num_word = isNumberWord(word);
            if (!(num_word and in_number)) n += 1;
            in_number = num_word;
        }
        best = @max(best, n);
    }
    return best;
}

fn isNumberWord(w: []const u8) bool {
    if (w.len > 0 and (std.ascii.isDigit(w[0]) or w[0] == '-')) return std.fmt.parseFloat(f64, w) catch null != null;
    const words = [_][]const u8{ "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety", "hundred", "thousand", "million", "billion", "point", "minus", "negative" };
    for (words) |x| if (std.ascii.eqlIgnoreCase(w, x)) return true;
    // twenty-one, thirty-five
    if (std.mem.indexOfScalar(u8, w, '-')) |i| return isNumberWord(w[0..i]) and isNumberWord(w[i + 1 ..]);
    return false;
}

/// Board text that heads the teacher's own recap ("Recap", "Summary", "Today's ideas"…).
fn isRecap(t: []const u8) bool {
    const heads = [_][]const u8{ "recap", "summary", "in summary", "takeaways", "key ideas", "today's ideas", "what we covered" };
    const head = std.mem.trim(u8, t[0 .. std.mem.indexOfAny(u8, t, ":\n") orelse t.len], " \t");
    for (heads) |h| if (std.ascii.startsWithIgnoreCase(head, h)) return true;
    return false;
}

/// Is a finding on line `line` (1-based) waved through by a `design-ok`
/// comment on that line or the line above (`# design-ok: why`, or
/// `<!-- design-ok -->` in a .md)? Applies to warnings and design notes.
pub fn waived(src: []const u8, line: ?u32) bool {
    const l = line orelse return false;
    return waivedLine(src, l);
}

/// A lesson-wide note (no line) is waved through by a `design-ok` comment
/// anywhere whose reason names its topic: "review"/"from=", "predict",
/// "recap"/"recall", or "pace"/"minutes"/"questions".
pub fn waivedLesson(src: []const u8, msg: []const u8) bool {
    const topics = [_]struct { msg: []const u8, words: []const []const u8 }{
        .{ .msg = "review question", .words = &.{ "review", "from=" } },
        .{ .msg = "prediction", .words = &.{"predict"} },
        .{ .msg = "recap", .words = &.{ "recap", "recall" } },
        .{ .msg = "minutes of talk", .words = &.{ "pace", "minutes", "questions" } },
    };
    var it = std.mem.splitScalar(u8, src, '\n');
    while (it.next()) |l| {
        const at = std.mem.indexOf(u8, l, "design-ok") orelse continue;
        const why = l[at..];
        for (topics) |t| {
            if (std.mem.indexOf(u8, msg, t.msg) == null) continue;
            for (t.words) |w| if (std.ascii.indexOfIgnoreCase(why, w) != null) return true;
        }
    }
    return false;
}

fn waivedLine(src: []const u8, line: u32) bool {
    if (line == 0) return false;
    var it = std.mem.splitScalar(u8, src, '\n');
    var n: u32 = 1;
    while (it.next()) |l| : (n += 1) {
        if (n + 1 < line) continue;
        if (n > line) break;
        if (std.mem.indexOf(u8, l, "design-ok") != null) return true;
    }
    return false;
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

test "spoken numbers count as one word; recap order; design-ok" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    // "one point eight five five" counts once, like "1.855": 25 words either way.
    const n = longestSentence("The delay is one point eight five five nanoseconds at twenty-one degrees, which is the figure we measured on the board last week in the lab with the scope");
    try std.testing.expectEqual(@as(usize, 25), n);
    try std.testing.expectEqual(@as(usize, 25), longestSentence("The delay is 1.855 nanoseconds at 21 degrees, which is the figure we measured on the board last week in the lab with the scope"));

    const ir =
        \\{"op":"say","line":1,"dur":200,"text":"Talk."}
        \\{"op":"quiz","id":"q1","line":2,"q":"?","options":["a","b"],"answer":[1],"predict":true}
        \\{"op":"say","line":3,"dur":100,"text":"Talk."}
        \\{"op":"text","id":"r","line":4,"text":"Recap: clocks, handshakes"}
        \\{"op":"recall","id":"q2","line":5,"q":"List today's ideas.","answer":"x"}
        \\{"op":"say","line":6,"dur":10,"text":"Done."}
    ;
    const found = try review(arena.allocator(), ir);
    var hit = false;
    for (found) |f| {
        if (std.mem.indexOf(u8, f.msg, "no recall card near the end") != null) return error.TestUnexpectedResult;
        if (f.line == 4 and std.mem.indexOf(u8, f.msg, "before the learner's (the recall on line 5)") != null) hit = true;
    }
    try std.testing.expect(hit);

    const src = "a\n# design-ok: read aloud on purpose\nt: text 0,0 \"x\"\nq: recall \"?\" \"x\"\n";
    try std.testing.expect(waived(src, 3));
    try std.testing.expect(waived(src, 2));
    try std.testing.expect(!waived(src, 4));
    try std.testing.expect(!waived(src, 1));
    try std.testing.expect(waivedLesson("# design-ok: no from= review questions; the first lesson\n", "0 review question(s) from earlier lessons"));
    try std.testing.expect(!waivedLesson("# design-ok: no from= review questions\n", "no prediction question"));
}
