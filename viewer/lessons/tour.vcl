# A tour of the primitives.
title "VCL primitives"
text -6,2.6 "2D" size=0.5 color=gray
p1: pt -7,1.5 label="pt"
& line -6,1.5 -4,2 label="line"
& arrow -3.5,1.2 -1.5,2.2 color=orange label="arrow"
path -7,-0.3 -6,0.6 -5,-0.3 -4,0.6 -3,-0.3 smooth=true color=cyan
& r: rect -1,0 1.6,1 radius=0.2 fill=#1d4a5a color=teal
c: circle -6,-2 0.8 color=pink
& poly -4.5,-2.8 -3,-1.2 -2.5,-2.8 color=purple
& tex -1,-2 "\int_0^1 x^2\,dx = \tfrac13" size=0.5
text 3.5,2.6 "3D" size=0.5 color=gray
bx: box 2,1 1.2,1.2,1.2 color=blue
& sp: sphere 4,1 0.7 color=orange
& cy: cyl 5.6,0.3 6.6,1.7 0.25 color=green
spin bx 20,30,0
& spin sp 0,40,0
n1: node 2.5,-2 "input"
& n2: node 6,-2 "output" shape=pill
edge n1 n2 label="f" bend=0.4
say "Points, lines, paths, shapes, LaTeX, 3D solids and diagrams."
point bx
say "Anything with a name can be moved, recoloured, highlighted or spun."
move n2 6,-3.2
& hl n2
point
