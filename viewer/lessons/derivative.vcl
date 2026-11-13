# The derivative as the limit of secant slopes.
title "The derivative"
ax: axes -0.5,-0.5 x=-1..4 y=-1..9 size=10,6.4 xlabel="x" ylabel="y"
f: plot ax "x^2" color=blue label="f(x) = x²"
say "Here is f of x equals x squared."
P: pt 1,1 on=ax label="P" color=yellow
& Q: pt 3,9 on=ax label="Q" color=orange
s: line P Q color=orange width=0.05
say "The line through P and Q is a secant. Its slope is rise over run."
m: tex 5.4,2.6 "m = \frac{f(1+h) - f(1)}{h}" size=0.6
& h: text 5.4,1.3 "h = 2   m = 4" size=0.45 color=orange
say "Now let h shrink, sliding Q down the curve toward P."
move Q 2,4 dur=1.4
& set h text="h = 1   m = 3"
move Q 1.5,2.25 dur=1.2
& set h text="h = 0.5   m = 2.5"
move Q 1.1,1.21 dur=1.2
& set h text="h = 0.1   m = 2.1"
say "The slopes approach 2. The secant becomes the tangent line."
t: line 0,-1 3.5,6 on=ax color=green width=0.07 dash=true
& set h text="h → 0   m → 2" color=green
hl t
tex 5.4,-0.4 "f'(1) = 2" size=0.75 color=green
