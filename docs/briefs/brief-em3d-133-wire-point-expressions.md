# Brief 133 — Expressions in a wire's points: the editor

**Series:** [131 overview](brief-em3d-131-point-expressions-overview.md) · **Tag:** `R-em3d133-n`
**Status:** Written 2026-10-09. Done 2026-10-09; findings in `src/Ui/ThreeD/RESOLVED.md`.
**Area:** `src/Ui/ThreeD/` (the Inspector's wire rows, `C3dEditorViewModel.Wires.cs` / `.WireShape.cs`, the
move/rotate/mirror/align paths, the drag rule in `.Expressions.cs`), `src/Design/ThreeD/C3dWires.cs` (seating).
Findings in `src/Ui/ThreeD/RESOLVED.md`.
**Depends on:** 132.

---

## 0. What this brief delivers

A wire's point components can be typed as expressions in the Inspector. Every edit that rewrites a wire's points meets
a bound component by a stated rule (overview D2 for translations, brief 51's drag rule otherwise). An end whose z is
bound stays on its pad.

## 1. Requirements

**R-em3d133-1 — the Inspector rows.** Each point row's x, y and z become `C3dDimensionField`s: number or expression,
with the same parse, error text, Esc revert and bound styling as every other dimension field. The +▲ / +▼ / − buttons
(deb6199f) stay, and work through 132's `RenumberPointExpressions`.

**R-em3d133-2 — an end's z may be bound (D1).** A design drawn with expressions can have a pad whose top moves with a
variable (`t_sub + t_die`). An end bound to the same expression stays on that surface.
- **Elaboration is the judge.** The evaluated z is checked by the existing pad lookup (`C3dWires.PadAt`, with the same
  tolerance as an unbound end). A miss is the ordinary "end is no longer on a pad" refusal, plus the expression text.
  A miss is never silently corrected.
- **A bound z is never re-seated silently.** When the expression already lands on the top, seating writes nothing and
  pushes no undo entry. This is the normal case. Otherwise the seat's write goes through the drag rule (overview
  R-em3d131-3).
- `SeatEditedWire`'s refusal of an end moved off every pad applies unchanged, against the evaluated value.
- **Arrays.** Each element's end is offset by `Pitch`, exactly as today. A pitch with a z component needs pads stepping
  by the same amount, and an element that misses is refused by name (`w1[2]`).
- `SetWirePoint`'s "an end's z follows its pad" note applies only to an unbound z typed to a value the seat puts back.

**R-em3d133-3 — the edits, and the rule each one uses.**

| Edit | Components it changes | Rule |
|---|---|---|
| Inspector typed point | that point's three | the typed text replaces the field |
| Vertex-mode Move (G) of a point | that point's three | D2, offset |
| Move of a wire | every component of every point | D2, offset |
| Span | the end's x and y (and its seated z) | D2, offset; the z as R-em3d133-2 |
| Align to Face (a translation) | every component of every point | D2, offset |
| Rotate / Mirror of a wire | every component of every point | D4: a quarter-turn or mirror swaps exactly; any other angle refuses |
| Flatten of an instance holding a wire | every component of every point | 132 R-em3d132-5 |
| Loop height | the z of every interior point | refused on a bound wire, except one whose only expressions are its ends' z (R-em3d133-4) |
| Re-seat Wire Ends | the ends' z (nothing, when a bound z already lands) | drag rule |
| Add / remove point | none; later points' entries renumber | overview R-em3d131-4 |

**R-em3d133-4 — Loop height on a bound wire (owner decision, 2026-10-09).** Loop height scales every interior point's
rise above the foot-to-foot chord. It keeps every x and y, and it never moves an end. It is **refused** on a wire that
holds any point expression, with one exception: a wire whose only bound components are its start's z and its end's z.
That wire's Loop height runs exactly as an unbound wire's does. Nothing it changes is bound, and the ends stay on their
pads (D1). The refusal names the first bound component, as `'w1' point 2 z holds h_loop`, and tells the user to replace
it with a number or to edit the expression. The drag rule is **not** used here. It would rewrite a variable that other
geometry may use, which D2 rules out for a move.
- Known consequence, not a defect: on a wire whose ends' z are bound and whose interior is numeric, raising a pad
  through its variable leaves the interior points where they are, so the measured loop height shrinks. Loop height
  stays a one-shot edit of numbers, as in brief 50.

A vertex Move preview already goes through `PreviewThroughNames` (`FaceToolChanged`). D2 changes what it writes for a
translation: the offset term instead of the name. The preview must show the same expression text the commit will write.

## 2. Gates

1. A wire with `h_loop` on its apex z: changing `h_loop` changes the elaborated loop height (the `WireGateTests` Gate 2
   pattern).
2. A foot x bound to `x_pad + 50um`: changing `x_pad` moves both pad and foot, and the wire is not refused.
3. D2 through the editor. `x_pad` moved by +20 µm reads `x_pad + 20um`. Moved −20 µm then +20 µm, `x_pad + 50um`
   reads `x_pad + 50um` again. The variable is unchanged, and an unmoved axis's text is byte-identical. One test per
   translation row of the table, not one per component.
4. D4. A wire with `x_pad` on a point's x, rotated 90° about z: that point's y reads `x_pad` (or `-(x_pad)`, as the
   turn's sign gives) plus its folded constant, and rotating back reads the original text in every component. Mirror
   works the same way. A 30° rotation of a bound wire is refused, naming the first bound field; the same rotation of an
   unbound wire succeeds.
5. Loop height (R-em3d133-4). A wire with `h_loop` on an interior z is refused, naming `point k z`, and its document
   and undo count are unchanged. A wire whose only expressions are its two ends' z sets the typed loop height, and both
   end expressions are byte-identical afterwards. The drag rule for Re-seat: a bare name is rewritten; a compound
   expression is refused, naming the field.
6. D1. An end z bound to the pad's own expression follows `t_die`, and a re-seat writes nothing (undo count unchanged).
   An expression that misses gives the "no longer on a pad" refusal carrying the expression text.
7. Insert and remove on a bound wire carry later points' expressions, and survive undo and redo.

Targeted classes only (`EditorRound3PropertiesTests`, `WireGateTests`, `WireArrayTests`, the brief-51 classes).
