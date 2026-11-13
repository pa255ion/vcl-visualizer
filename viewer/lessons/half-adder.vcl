# A half adder: gates, truth table, and timing.
title "Half adder"
X1: part -2,2.4 xor label="U1"
& A1: part -2,0.6 and label="U2"
A: pt -6,2.65 label="A" size=0.1
& B: pt -6,2.15 label="B" size=0.1
wire A X1.a
& wire B X1.b
wire -4.6,2.65 A1.a dot=start
& wire -4,2.15 A1.b dot=start
wire X1.y 0.8,2.4 label="S"
& wire A1.y 0.8,0.6 label="C"
say "Two gates add two bits: XOR gives the sum, AND gives the carry."
t: table 4.8,1.6 "A|B|S|C" "0|0|0|0" "0|1|1|0" "1|0|1|0" "1|1|0|1"
say "Only when both inputs are one does the carry appear."
set t focus=4
& hl A1
arrow 6.9,0 t.R4+0.1,0 color=yellow
w: wave -0.5,-2.45 "A: 0.0.1.1." "B: 0.1.0.1." "S: 0.1.1.0." "C: 0.0.0.1." step=0.75 size=0.42
say "In time, the outputs simply follow the inputs, after a short gate delay we're ignoring here."
point w.C
