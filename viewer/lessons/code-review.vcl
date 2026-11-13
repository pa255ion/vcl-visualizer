# Walking through code: focus moves line by line, notes point at lines.
title "Pushing into a ring buffer"
c: code -2.6,0.2 lang=zig size=0.4 title="ring.zig" <<END
pub fn push(self: *Ring, x: u8) error{Full}!void {
    if (self.len == self.buf.len) return error.Full;
    const tail = (self.head + self.len) % self.buf.len;
    self.buf[tail] = x;
    self.len += 1;
}
END
say "Let's read push one line at a time."
set c focus=2
& n1: text 5,1.6 "full? refuse,\ndon't overwrite" size=0.4 color=orange
& arrow n1.left c.L2 color=orange
say "First, a full buffer refuses the write instead of overwriting old data."
set c focus=3
& n2: text 5,0.1 "wrap around\nwith modulo" size=0.4 color=cyan
& arrow n2.left c.L3 color=cyan
say "The tail index wraps around with a modulo."
set c focus=4..5
say "Then we store the byte and grow the length."
s: code -2.6,-3.2 lang=c size=0.3 <<END
/* the same in C */
if (r->len == CAP) return -1;   // full
r->buf[(r->head + r->len++) % CAP] = x;
END
