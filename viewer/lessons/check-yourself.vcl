# Questions for the learner: quiz, pick, estimate, recall (docs/SPEC.md §4.10),
# laid out as docs/LESSON-DESIGN.md suggests: a guess first, a check per idea,
# a review question from an earlier lesson, the learner's recap at the end.
# The Practice button replays just the questions.
title "Check yourself: slopes"
ax: axes -3,-0.5 x=-2..2 y=-1..4 size=7,5
f: plot ax "x^2" color=blue
say "Here is the curve y equals x squared."

q_cy_1: quiz "Guess before we start: a line that touches the curve at one point. What does its slope tell you?" "How fast the curve is rising there" "The area under the curve up to there" "Where the curve crosses zero" answer=1 predict=true explain="We'll see in a moment."

t: line ax.center+-1.75,-1.3 ax.center+1.75,2.2 color=orange
& p: pt ax.center+0,0 size=0.25 color=yellow
say "This line touches the curve at one point. It is the tangent line."
say "Its slope is how fast the curve is rising, right there."

q_cy_2: quiz "Where is the curve y = x² rising?" answer=1,3 explain="Its slope is 2x, which is positive whenever x is positive; at minus one it is falling." <<END
At x = 1
At x = -1
At x = 2
At x = 0
END

q_cy_3: estimate "What is the slope of y = x² at x = 1.5?" 0..8 answer=3 tol=0.2 explain="The slope is 2x, and 2 times 1.5 is 3. Not 2.25: that's the height, x squared."

n: nn 4.4,0.2 "3,4,2" size=5,3.6 labels="in,hidden,out"
say "Now something from an earlier lesson: a small neural network."
q_cy_4: pick "Which layer has the most neurons?" n.L0 n.L1 n.L2 answer=2 from="neurons and layers" explain="The middle one, with four. The input has three, the output two."

q_cy_5: recall "Before my recap: what is a derivative, in one sentence?" "The rate a function changes: the slope of its tangent line."
say "That's it: the slope of the tangent is the derivative. Press Practice to try the questions again."
