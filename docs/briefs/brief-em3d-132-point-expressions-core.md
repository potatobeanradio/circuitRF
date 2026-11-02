# Brief 132 — Point-list expressions: the core

**Series:** [131 overview](brief-em3d-131-point-expressions-overview.md) · **Tag:** `R-em3d132-n`
**Status:** Written 2026-10-09. Done 2026-10-09; findings in `src/Design/RESOLVED.md`.
**Area:** `src/Design/ThreeD/` only (`C3dBindings`, the `.c3d` JSON contract, the elaborator's field resolution,
`C3dHierarchy`), the `3d-view` reference topic, `check` / `explain`. No editor UI. Findings in `src/Design/RESOLVED.md`.
**Depends on:** brief 51.

---

## 0. What this brief delivers

The machinery that 133 (wires) and 134 (polylines) use. After this brief, a `.c3d` may carry an expression in a wire's
or a polyline's point. That expression evaluates, is checked and is explained. Nothing in the editor offers it yet,
apart from what already reads `Exprs` generically.

## 1. Requirements

**R-em3d132-1 — a list spec in the binding table.** `C3dBindings.Fields` holds fixed-arity specs (`Size`, 3). It gains
a spec for a **list of fixed-arity elements**: owner type, property, element arity and kind. The entries are
`C3dWire.Points` (3, Length), `C3dPolyline.Points` (2, Length, in the polyline's plane) and `C3dPolyline.Points3` (3,
Length). `FieldsOf` enumerates a list spec from the owner's current count, with paths `P[k][c]` and map keys `P[k]`
(overview R-em3d131-1). Every caller of `Fields` / `FieldsOf` that assumed a fixed arity must be found and checked
(grep both), not assumed to be unaffected.

**R-em3d132-2 — the JSON contract.** A point is still `[a, b(, c)]`, and any component may be an expression string, as
`Size`'s may. Reading fills `Exprs[P[k]]`; writing puts the expression back in place. **A file with no point
expression is byte-identical** (the series gate). A polyline that has `Points3` ignores `Points` today. An expression
under the ignored one is a read refusal naming it, never silently dropped.

**R-em3d132-3 — evaluation.** Point components resolve where every other bound field resolves, before anything reads
the points: `C3dWires.Resolve` and its pad lookup, the wire array, the polyline's consumers and the solver. Downstream
of elaboration, nothing sees an expression. An unresolvable component is a refusal naming the object, the point
(1-based) and the component, in the words every other field's refusal uses.

**R-em3d132-4 — the two pure functions both kinds share.**
- `RenumberPointExpressions(owner, property, from, removed)`. Move it from `C3dEditorViewModel` (deb6199f) into
  `src/Design/ThreeD`, generalised over the property name (`Points`, `Points3`). It keeps the refusal for a key shape it
  cannot renumber. Its test moves with it.
- `OffsetExpression(expr, offsetDbu, unit)`. This is the D2 writer, with overview R-em3d131-2's folding, sign,
  precedence and zero rules, operating on the expression engine's AST and printing through its printer. Unit tests:
  plain append, fold, fold to zero, negative offset, parenthesised root, a zero offset that leaves the text byte-identical.
- `SwapExpression(expr, sign, offsetDbu, unit)`, the D4 writer (overview R-em3d131-5): `s·(expr) + c`, with the
  trailing-literal rule, the parenthesis rule, double-negation simplification and the offset writer's folding. Unit tests: `+1` with a zero
  constant leaves the text byte-identical; `-1` on an atom; `-1` on a compound; `-1` on `t + 5um` gives `-(t) - 5um`; `-(-(e))`; a quarter-turn and its
  inverse read back as the original text.

**R-em3d132-5 — operations that copy objects.**
- `C3dHierarchy.Scale` (a child at a finer DBU) multiplies point values. Find out what it does today with brief 51's
  bound fields and do exactly the same for point expressions. An expression with units is DBU-independent; a bare
  number in an expression may not be. Record the finding.
- Flatten bakes an instance's placement into a wire's points (`C3dWires.BakePlacement`). It writes through
  `OffsetExpression` for the translation and `SwapExpression` for a quarter-turn or mirror (D4). It refuses any
  other rotation of a bound point, naming it. Before writing this, check what Flatten does with the child's own variables
  in brief 51's fields, and do the same.

**R-em3d132-6 — check, explain, reference, MCP.** `check` reports an unresolvable point as it reports any field (exit
1). `explain --tunables` lists bound point components by path. A `tune` line on one works through the existing
machinery: verify it with a test, do not assume. The `3d-view` reference topic documents the point-expression form
with one wire example and one polyline example.

## 2. Gates

1. The series gate: every shipped and fixture `.c3d` re-saves byte for byte.
2. Round trip: a wire and a polyline (2D and 3D) with expressions in some components read, write and read back
   identical, `Exprs` included.
3. Evaluation: a wire's apex z bound to `h_loop`, elaborated at two values of `h_loop`, gives two loop heights
   (`C3dWires.Resolve`'s report). A polyline vertex bound to `x_v` moves with it.
4. The `OffsetExpression` and `SwapExpression` unit tests listed in R-em3d132-4. `RenumberPointExpressions`' test, moved.
5. `check` exits 1 on an unresolvable point, naming object, point and component.

Targeted classes only.
