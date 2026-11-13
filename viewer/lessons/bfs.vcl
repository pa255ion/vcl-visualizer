# Breadth-first search on a small graph, with the queue shown as text.
title "Breadth-first search"
A: node -6,0.5 "A"
& B: node -3.5,2.3 "B"
& C: node -3.5,-1.3 "C"
& D: node -0.8,2.3 "D"
& E: node -0.8,-1.3 "E"
& F: node 1.9,0.5 "F"
& G: node -0.8,-3.4 "G"
edge A B dir=none
& edge A C dir=none
& edge B D dir=none
& edge C E dir=none
& edge C G dir=none
& edge D F dir=none
& edge E F dir=none
say "We'll explore this graph level by level, starting from A."
q: text 6,2.4 "queue: A" size=0.5 align=center
& v: text 6,1.4 "visited: " size=0.4 color=gray
color A green
& hl A color=green
say "Take A off the queue and add its neighbours, B and C."
set q text="queue: B C"
& set v text="visited: A"
color B orange
& color C orange
say "Next B: its new neighbour is D."
color B green
& set q text="queue: C D"
& set v text="visited: A B"
& color D orange
say "Then C, which adds E and G."
color C green
& set q text="queue: D E G"
& set v text="visited: A B C"
& color E orange
& color G orange
say "D adds F. Everything after that is already seen."
color D green
& set q text="queue: E G F"
& color F orange
color E green
& color G green
& color F green
& set q text="queue: (empty)"
& set v text="visited: A B C D E G F"
say "Each node was reached by the fewest possible edges from A."
