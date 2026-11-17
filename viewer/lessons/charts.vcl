# Charts and diagrams with parts: nn, bits, grid, bars, hist, errbar, band, contour.
title "A network, and its numbers"
n: nn -3.2,0.5 "196,32,10" labels="pixels,hidden,digits" size=7,4.2
say "A small network: 196 pixels in, 32 hidden units, 10 digit scores out."
hl n.L0
say "Each hidden unit sums all the inputs, weighted."
color n.E1 orange
hl n.L1_2
g: grid 5,0.6 14,14 size=4.2,4.2 <<END
0 0 0 0 0 0 0 0 0 0 0 0 0 0
0 0 0 0 0 1 1 1 1 0 0 0 0 0
0 0 0 0 1 1 0 0 1 1 0 0 0 0
0 0 0 0 0 0 0 0 0 1 0 0 0 0
0 0 0 0 0 0 0 0 0 1 0 0 0 0
0 0 0 0 0 0 0 0 1 1 0 0 0 0
0 0 0 0 0 0 0 1 1 0 0 0 0 0
0 0 0 0 0 0 1 1 0 0 0 0 0 0
0 0 0 0 0 1 1 0 0 0 0 0 0 0
0 0 0 0 1 1 0 0 0 0 0 0 0 0
0 0 0 1 1 0 0 0 0 0 0 0 0 0
0 0 0 1 1 1 1 1 1 1 1 0 0 0
0 0 0 0 0 0 0 0 0 0 0 0 0 0
0 0 0 0 0 0 0 0 0 0 0 0 0 0
END
& text 5,-2 "the input: 14×14" size=0.35
point g
say "The input is this picture, one pixel per input unit."
b: bits -1,-3.6 "s|iiii|fffffff" labels="sign,integer,fraction" values="0|0011|0100000" size=0.42
say "On the chip, every weight is a twelve bit fixed point number."
hl b.fraction
clear
title "Energy and accuracy"
board 24,13
ax: axes -6,1 x=0.4..3.6 y=0..6 size=8,6 xticks=false ylabel="energy (µJ)"
e: bars ax "1,3/1.5/0.8 2,2/1/0.6 3,1.2/0.7/0.5" labels="BP,PC lean,PC full" series="clock tree,logic,BRAM" width=0.55
say "Most of the energy goes into the clock tree."
hl e.B1
ax2: axes 6,1 x=-1.5..1.5 y=0..4 size=8,6 yticks=false xlabel="accuracy difference (%)" ylabel=""
band ax2 x=-0.5..0.5 label="equivalence margin"
errbar ax2 "3,0.1,-0.3..0.5 2,-0.2,-0.7..0.3 1,0.4,-0.5..1.3" horiz=true color=yellow
say "Two confidence intervals sit inside the margin; the third does not."
hl e
clear
title "Loss landscape and rounding"
board
ax: axes -4,-0.3 x=-2..2 y=-2..2 size=6,6
contour ax "x^2 + 3y^2 + x*y" levels=7
path -1.8,1.6 -1.1,0.5 -0.5,0.1 -0.15,0.02 0,0 on=ax color=white width=0.05
say "Gradient descent walks down the bowl."
q: axes 4,-0.3 x=-1..1 y=-1..1 size=6,6
plot q "x" dash=true color=gray
plot q "round(4x)/4" step=true color=orange
say "Rounding to quarter steps turns the line into a staircase."
