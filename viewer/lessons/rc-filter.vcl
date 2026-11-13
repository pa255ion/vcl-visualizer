# An RC low-pass filter: schematic, cutoff frequency, and its magnitude response.
title "RC low-pass filter"
V1: part -6,0 vsource rot=-90 label="V1" value="1 V AC"
& R1: part -3.5,2 resistor label="R1" value="1 kΩ"
& C1: part -1,0 capacitor rot=-90 label="C1" value="100 nF"
& G: part -3.5,-2.6 ground
say "Here's the simplest filter there is: a resistor feeding a capacitor."
wire V1.a R1.a
wire R1.b 0.8,2
& out: pt 0.8,2 label="Vout" size=0.1
wire C1.a -1,2 dot=end
wire V1.b G.a
& wire C1.b G.a
say "The output is taken across the capacitor."
point C1
tex 4.6,2.9 "f_c = \frac{1}{2\pi R C} \approx 1.6\,\mathrm{kHz}" size=0.6
say "Below the cutoff frequency the capacitor barely loads the signal. Above it, it shorts more and more of it to ground."
ax: axes 4.6,-0.9 x=1..5 y=0..1.1 size=5.5,3.2 xlabel="log f" ylabel="|H|"
plot ax "1/sqrt(1 + 10^(2x - 2*3.2))" color=yellow
hl R1 C1
