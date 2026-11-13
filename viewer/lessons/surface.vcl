# A surface z = f(x, y) and a helix on 3D axes. Lean in and look around.
title "Functions of two variables"
ax: axes 0,-0.6 x=-3..3 y=-3..3 z=-1.5..1.5 size=6,6,3
say "These axes are three-dimensional: x, y, and z pointing up."
s: surf ax "sin(x)*cos(y)" res=60
say "This surface is z equals sine x times cosine y."
spin ax 0,18,0
say "Peaks where both factors agree in sign, valleys where they differ."
hx: plot ax "2.2cos(t)" "2.2sin(t)" "t/5" t=-7..7 color=pink width=0.07
say "A curve can live in the same space: here, a helix winding upward."
wait 2
fade s 0.35
say "Fade the surface to see the helix through it."
