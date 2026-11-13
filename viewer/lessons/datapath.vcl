# A hierarchical block diagram: a single-cycle CPU datapath inside an SoC.
title "Single-cycle datapath"
pc: block -6.3,0.4 1.6,1.3 "PC" in="nxt" out="pc"
& im: block -3.9,0.4 2.5,1.7 "Instr Mem" in="addr" out="ins"
& rf: block -0.6,0.4 2.6,2.2 "Reg File" in="ra,rb,wd" out="a,b"
& alu: block 2.5,0.4 1.9,1.9 "ALU" in="x,y" out="r"
& dm: block 5.75,0.4 2.3,1.9 "Data Mem" in="addr,wd" out="rd"
say "An instruction flows left to right through five blocks."
wire pc.pc im.addr bus=32
wire im.ins rf.ra bus=5
wire rf.a alu.x bus=32
& wire rf.b alu.y
wire alu.r dm.addr bus=32
say "The ALU's result is also the next address when we branch."
wire alu.r 3.9,-2 -7.6,-2 -7.6,0.4 pc.nxt color=orange dash=true arrow=true
core: frame pc im rf alu title="Core"
soc: frame core dm title="SoC" pad=0.45
uart: block 5.7,-3.2 2,1.1 "UART" top="bus"
wire dm.rd 7.3,0.4 7.3,-2.2 uart.bus
say "The core sits inside the SoC, next to memory and peripherals like this UART."
hl core
