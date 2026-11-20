# Designing VCL lessons that people remember (paste after PRIMER.md)

PRIMER.md tells you how to draw. This tells you how to teach so the learner
still knows it next month. The learner is one adult who will come back to these
lessons repeatedly; long-term retention matters more than how smooth one
viewing feels. Every rule below comes from well-replicated learning research
(retrieval practice, spacing, generation, worked examples, multimedia design).

## The five rules that matter most

1. **Ask before you tell.** Whenever the learner has already met something
   (an earlier lesson, an earlier beat), make them retrieve it with a question
   *before* you restate it. Retrieving strengthens memory; hearing it again
   barely does.
2. **Make them produce, not just recognise.** Prefer questions that require
   computing, predicting or stating something (`estimate`, `recall`, a `quiz`
   whose options are results of reasoning) over ones answerable by matching
   words on the board.
3. **Never leave the answer on the board.** Before a question, `fade` or `del`
   any text that states its answer. A `pick` next to a label that names the
   right entity tests reading, not memory.
4. **One idea per beat, and say it once.** Speech carries the explanation;
   the board carries the structure (diagrams, labels, key symbols). Don't put
   the sentence you are speaking on the board as text.
5. **Mix old and new.** Every lesson pulls at least two questions from earlier
   lessons, ideally from different units. Mark their source with `from="8.2"`
   (the card shows "Review from 8.2") so the learner knows it's review.

## Lesson structure

A lesson is 10–20 minutes, 4–8 sections separated by `clear`, and roughly one
question every 1–2 minutes of talk.

```
header comments   objectives, prerequisites, sources (keep these, see "Accuracy")
1. Retrieve       1–2 questions on the prerequisites, before any re-explanation
2. Hook           a prediction question about the new idea, before teaching it
3. Sections       explain → show → signal (hl/point) → check (one question)
4. Worked example then a near-identical problem for the learner
5. Interleave     1–2 questions from older lessons, unrelated to today
6. Learner recap  a recall card asking them to list today's ideas, BEFORE yours
7. Your recap     short, structural (a code/table panel is fine)
8. Final check    2–3 mixed questions: one recall, one application, one transfer
```

### 1. Retrieve prerequisites instead of restating them

Weak (tells):

````
Recall that about 36% of each cycle's energy is the clock tree.
```vcl
f1: node -4,1.4 "36% of each cycle's energy\nis the clock itself" shape=box
```
````

Strong (asks, then shows):

````
Before we start, a number from lesson eight point four.
```vcl
q_pre1: estimate "What fraction of each active cycle's energy went to the clock tree?" 0..100 answer=36 tol=6 step=1 unit="%" from="8.4" explain="About thirty-six percent: the clock ticks whether or not data changes."
f1: node -4,1.4 "36% of each cycle\nis the clock" shape=box size=0.45
```
````

### 2. Ask a prediction before teaching the idea

Questions asked before instruction improve learning of that material, even
when the learner gets them wrong. Make it clear that guessing is expected, and
have `explain=` promise the answer is coming rather than giving it all away.
Mark it `predict=true`: the card says "Guess first (not scored)", the score
leaves it out, and the voice reacts to a wrong guess as expected.

```
q_hook: quiz "Guess before we start: without a clock, how could a receiver know new data has arrived?" "The sender signals it on an extra wire" "The receiver samples it constantly" "Data wires are made faster than any logic" answer=1 predict=true explain="Good guess or not, the next five minutes are about exactly this signal."
```

For animated behaviour, ask the prediction *before* the animation runs, then
show it ("let's see if you were right"). This works especially well with
`wave`: draw the inputs, ask what the output does, then draw the output row.

### 3. Sections: explain, show, signal, check

- Speak one or two short sentences, then draw the 1–4 lines that show them.
- When you say "this", `hl` or `point` at it (signalling).
- End the section with **one** question that checks the section's core idea.
  Don't check every detail; check the idea the next section depends on.
- Keep each section under about 3 minutes of speech. Step mode lets the
  learner pace, but sections still need natural stopping points.

### 4. Worked example, then a matched problem

Show one fully worked example with every step spoken, then give an `estimate`
with different numbers and the same method. Later in the lesson (or a later
lesson), give a problem that needs the method in a slightly new situation
(transfer). `explain=` should show the working in one sentence.

### 5. Interleave older material

At least two questions per lesson should come from lessons two or more back,
on topics unrelated to today. Unrelated is the point: the learner has to
recall which idea applies, which is what real problems demand.

### 6. Learner recap before yours

```
q_recap: recall "Before my recap: list the main ideas from this lesson, in order, out loud." "Clock costs; handshakes (4-phase, 2-phase); bundled data and matched delays; C-element; click element; desynchronization vs elastic; why the per-step floor makes self-timed worth trying."
```

Then give your own recap. The learner compares, which shows them what they
missed while it is still fresh.

## Writing questions

**How sure?** Quiz and estimate cards let the learner say how sure they were
(guessing, fairly, sure). It is optional and comes back with the answer: a
wrong answer given as *sure* is the one most worth correcting, and a right
answer given as a *guess* should come back later.

**Choose the type by what you want to strengthen:**

| Type | Best for | Watch out for |
|---|---|---|
| `recall` | facts, definitions, one-line explanations | answers must be short (below) |
| `estimate` | applying a formula, sense of magnitude | make the range wide enough that the answer isn't guessable from its middle |
| `quiz` | misconceptions, choosing between ideas | distractors must be plausible |
| `pick` | locating a part in a diagram | the answer must not be labelled on the board |

**Recall cards: one fact per card.** A card whose answer has three clauses is
three cards. The learner self-rates `recall`, and a long answer invites "I
mostly knew it" ratings that hide what was missed. Aim for answers under about
20 words. If the idea is an explanation, ask for its key step:

Weak:
```
recall "Why would a self-timed engine help where clock gating doesn't?" "Gating saves only when a whole block is idle; the floor is paid on busy steps whose values barely change. A self-timed stage fires only on a new token, so unchanged work costs nothing (though handshakes cost energy too)."
```
Strong (two cards):
```
q_a: recall "When does clock gating save energy?" "Only when a whole block is idle."
q_b: recall "In a self-timed stage, what makes the registers clock?" "A new token arriving; no new token, no fire pulse."
```

**Distractors are misconceptions.** Each wrong option should be something a
learner could reasonably believe: a confusion with a neighbouring concept, an
off-by-one, a sign error, the intuitive-but-wrong answer. Never pad with
absurd options. Options are shuffled, so don't reference positions, and keep
options similar in length (the longest option is often guessed as right).

**Write `explain=` for the wrong answer.** Assume the learner chose the most
tempting distractor and explain in one or two sentences why it fails and what
is true instead.

**Stable, unique names.** Name every question with its lesson and number
(`q10_2_5`). Don't rename or renumber questions when editing a lesson: the
names identify the learner's answer history. If you change a question's
meaning, give it a new name.

**Hide answers first.** Before a question, check the board:

```vcl
fade lbl4 0
& fade lbl2 0
q10_2_3: pick "Which protocol returns request and acknowledge to zero after every transfer?" w4 w2 answer=1
fade lbl4 1
& fade lbl2 1
```

Leaving a formula or truth table visible is fine when the question asks the
learner to *apply* it; it is not fine when the question asks them to remember
it.

## The board and the voice

- **Board text is labels and symbols, not sentences.** If a line of board text
  repeats what you are saying, cut it or reduce it to a keyword. Exception: a
  one-line takeaway at the end of a section, shown after you've said it.
- **Limit new numbers.** Each section should introduce at most two or three
  numbers the learner is meant to keep. Mark context-only numbers as such
  ("about", "for scale") and don't quiz them. Quiz the numbers that matter
  more than once across lessons.
- **Round for speech.** "About one hundred fourteen megahertz", not "one
  hundred thirteen point six".
- **Keep spoken sentences short.** The voice is synthetic; long sentences with
  nested clauses are hard to follow by ear. Prefer two short sentences.
- **Use space consistently.** If the sender is on the left in one section,
  keep it on the left in the next. Consistent layout becomes a memory cue.

## Accuracy

A learner will diligently memorise anything you say, including mistakes.

- Put a `# source:` comment above every non-obvious claim, figure or number.
- Distinguish, in speech, the three kinds of claim: established background
  ("textbook background"), the learner's own sources ("the thesis measured"),
  and your own reasoning ("my back-of-envelope, not a result").
- Do arithmetic in a comment (`# python3: 6.0/0.122 = 49.2`) or in a `data`
  block, never in your head.
- If you aren't sure of a fact, leave it out. Don't quiz anything you
  wouldn't stake the learner's exam on.

## Offline lessons (no tutor, replayed many times)

Most lessons are played back later without a Claude session, often more than
once. Write for that:

- **`explain=` is the only feedback the learner gets.** Make it address the
  most tempting wrong answer, since no tutor will follow up.
- **Assume rewatching.** A second viewing that only re-listens is weak
  practice; the questions are what make a replay worthwhile. Put the
  lesson's key ideas into questions, not just into speech, so a replay that
  jumps between questions still covers them.
- **Make questions survive memorisation.** After a few replays the learner
  may remember "the second-longest option" rather than the idea. Prefer
  `estimate` and `recall` for the most important ideas, and for key
  methods, ask the same method twice with different numbers in different
  sections or lessons.
- **Prompt saying the answer aloud.** Phrase `recall` prompts so the learner
  states the answer before revealing it ("Say it out loud, then reveal").

## Reacting to answers (live tutor sessions only)

When you receive `[answer] …`:

- **Correct, high confidence:** move on; don't re-explain.
- **Wrong:** explain once, briefly, then ask a *different* question on the
  same idea a few minutes later (a variant with new numbers, or a different
  type). Don't re-ask the identical question immediately; that tests short-term
  memory.
- **Skipped:** treat as wrong, without comment on the skip.
- **Several wrong in one section:** the section was too dense. Go back to its
  core idea with a simpler example before continuing.

## Practice replays

The viewer's **Practice** button (and the Quest transcript's) replays a
lesson's questions only: each with the board as it stands at that point, one
after another, then the score. It is the lesson's retrieval practice without
the talk, which is why the key ideas need to be in questions.

## Checking a lesson

`vcl check` already warns about the commonest question problems: an answer
readable on the board when the question is asked (fade it first; formulas
match however they are spaced, and a recall answer whose key words are mostly
on one board text counts too; a one- or two-digit estimate among a table's
numbers doesn't), a `pick`
whose right choice is labelled with a word from the question, a recall answer
over about 20 words (except a recap card that asks to list or recap), a right option much longer than the others, an estimate
answer in the middle of its range, and unrounded numbers in speech.
`vcl check --design FILE…` also reviews the lesson as a whole against this
guide: unnamed or repeated question names, the minutes of talk before the
first question and between questions, a prediction question, review questions
(`from=`), a recall card near the end and before your recap board (text
headed "Recap", "Summary", "Key ideas"…), board text that repeats the speech,
and sentences too long to follow by ear (a number spoken as words counts as
one word).

The check matches words, not meaning: a paraphrase of the answer on the
board, or a box labelled with its meaning, still needs a reader's eye.

When a finding is deliberate, say so in a comment and it stops printing:
`# design-ok: why` on the line of the finding or the line above (in a `.md`,
`<!-- design-ok: why -->` works too) waves through that line's warnings and
design notes, never its errors. A lesson-wide note is waved through by a
`design-ok` comment anywhere whose reason names its topic: "review" or
"from=", "predict", "recap" or "recall", "pace", "minutes" or "questions".

```vcl
# design-ok: review — this is the first lesson, there is nothing earlier
# design-ok: the central question, read aloud word for word on purpose
q: text 0,2 "Can early stopping let predictive coding escape its cost bound?"
```

## Checklist before you finish a lesson

- [ ] Opens with retrieval of prerequisites, not restatement
- [ ] At least one prediction question before new material
- [ ] One check question per section; one every 1–2 minutes overall
- [ ] At least two interleaved questions from older, unrelated lessons
- [ ] No question has its answer visible on the board
- [ ] Every recall card is one fact, answer under about 20 words
- [ ] Every distractor is a plausible misconception
- [ ] Learner recaps before you do
- [ ] Every number has a source comment; your own estimates are labelled
- [ ] Question names follow `q<lesson>_<n>` and are unchanged from earlier versions
