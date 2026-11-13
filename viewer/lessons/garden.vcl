# Art demo: the board grows and gains a stage in front, and par … end draws
# several things at once. Tick "step" to pause after each sentence (Space = next).
title "Lissajous garden"
say "Let's make the board bigger, and give it a stage to stand things on."
board 26,13,7
say "Three frames, laid out at the same time."
par
L1: axes -8.5,1.6 x=-1.1..1.1 y=-1.1..1.1 size=6,6 ticks=false color=#3a4a64
L2: axes 0,1.6 x=-1.1..1.1 y=-1.1..1.1 size=6,6 ticks=false color=#3a4a64
L3: axes 8.5,1.6 x=-1.1..1.1 y=-1.1..1.1 size=6,6 ticks=false color=#3a4a64
end
say "Now three curves, drawn together, each made from two sine waves."
par
c1: plot L1 "sin(3t)" "sin(2t)" t=0..6.2832 color=cyan width=0.08 dur=4
c2: plot L2 "sin(5t+0.5)" "sin(4t)" t=0..6.2832 color=pink width=0.08 dur=4
c3: plot L3 "sin(7t)" "sin(6t+0.3)" t=0..6.2832 color=yellow width=0.08 dur=4
text -8.5,-1.9 "3 : 2" size=0.5 color=cyan
text 0,-1.9 "5 : 4" size=0.5 color=pink
text 8.5,-1.9 "7 : 6" size=0.5 color=yellow
tex 0,-3.3 "x=\sin(a\,t+\varphi),\quad y=\sin(b\,t)" size=0.6
end
say "The ratio of the two frequencies decides the shape."
say "On the stage, a small orrery turns while we talk."
par
sun: sphere 0,-5.9,3.5 0.8 color=orange
p1: sphere 2.6,-5.9,3.5 0.3 color=cyan
p2: sphere -2.6,-5.9,3.5 0.3 color=pink
p3: sphere 0,-5.9,0.5 0.4 color=teal
p4: sphere 0,-5.9,6.5 0.4 color=purple
end
sys: group sun p1 p2 p3 p4
spin sys 0,35,0
say "Many things can change at once: the curves trade colours."
par
color c1 pink dur=1.5
color c2 yellow dur=1.5
color c3 cyan dur=1.5
hl sun color=yellow
end
say "And the frames turn, all three in step."
par
spin L1 0,60,0
spin L2 0,-60,0
spin L3 0,60,0
end
wait 3
par
rot L1 0,0,0 dur=1.2
rot L2 0,0,0 dur=1.2
rot L3 0,0,0 dur=1.2
end
say "That's the garden. Every sentence was paired with what it describes."
