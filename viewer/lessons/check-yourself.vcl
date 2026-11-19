# Questions for the learner: quiz, pick, estimate, recall (docs/SPEC.md §4.10).
title "Check yourself: slopes"
ax: axes -3,-0.5 x=-2..2 y=-1..4 size=7,5
f: plot ax "x^2" color=blue
say "Here is the curve y equals x squared."
t: line ax.center+-1.75,-1.3 ax.center+1.75,2.2 color=orange
& p: pt ax.center+0,0 size=0.25 color=yellow
say "And a line touching it at one point. We call that a tangent line."

q1: quiz "What does the slope of a tangent line tell you?" "The area under the curve" "How fast the curve is rising at that point" "Where the curve crosses zero" answer=2 explain="The tangent's slope is the derivative: the rate of change at that point."

n: nn 4.4,0.2 "3,4,2" size=5,3.6 labels="in,hidden,out"
say "Now something different: a small neural network."
pick "Which layer has the most neurons?" n.L0 n.L1 n.L2 answer=2 explain="The hidden layer has four; the input three, the output two."

quiz "Where is the curve y = x² rising?" answer=1,3 explain="Its slope is 2x, which is positive whenever x is positive." <<END
At x = 1
At x = -1
At x = 2
At x = 0
END

estimate "What is the slope of y = x² at x = 1.5?" 0..5 answer=3 tol=0.2 explain="The slope is 2x, and 2 times 1.5 is 3."

recall "In one sentence: what is a derivative?" "The rate at which a function changes: the slope of its tangent line."
say "That's the end of this check. Well done."
