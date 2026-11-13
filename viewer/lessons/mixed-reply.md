Here's how a vector can be split into two parts.
```vcl
title "Vector components"
ax: axes 0,-0.3 x=-1..5 y=-1..4 size=9,6
v: arrow 0,0 4,3 on=ax color=yellow label="v"
```
The vector v points 4 units right and 3 units up.
```vcl
vx: arrow 0,0 4,0 on=ax color=blue label="4"
& vy: arrow 4,0 4,3 on=ax color=green label="3"
```
Those two pieces are its x and y components.
Its length comes from Pythagoras: the square root of 16 plus 9, which is 5.
```vcl
tex 5.5,2.8 "|v| = \sqrt{4^2 + 3^2} = 5" size=0.6
hl v
```
