# The Pythagorean theorem, shown with squares on the sides of a 3-4-5 triangle.
title "The Pythagorean theorem"
say "Let's build a right triangle and look at the squares on its sides."
A: pt -4,-1.2 label="A"
& B: pt -2.2,-1.2 label="B"
& C: pt -2.2,1.2 label="C"
b: line A B color=green label="3"
a: line B C color=blue label="4"
c: line C A color=yellow label="5"
& rt: angle A B C right=true
point rt
say "The corner at B is a right angle."
say "The longest side, opposite that corner, is called the hypotenuse."
hl c
sb: poly A B -2.2,-3 -4,-3 color=green
& tex -3.1,-2.1 "9" size=0.6 color=green
sa: poly B C 0.2,1.2 0.2,-1.2 color=blue
& tex -1,0 "16" size=0.6 color=blue
say "A square on each short side: three squared is 9, four squared is 16."
sc: poly A C -4.6,3 -6.4,0.6 color=yellow
& tex -4.3,0.9 "25" size=0.6 color=yellow
say "And the square on the hypotenuse has area 25."
point
tex 4.4,1 "a^2 + b^2 = c^2" size=0.9
say "9 plus 16 is 25. That holds for every right triangle."
& tex 4.4,-0.6 "16 + 9 = 25" size=0.7 color=gray
hl sa sb
hl sc
