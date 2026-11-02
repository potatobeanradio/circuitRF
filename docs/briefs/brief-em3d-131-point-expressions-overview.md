# Brief 131 — Expressions in point lists: overview (wires and polylines)

**Series:** 131 (this overview) · [132 core](brief-em3d-132-point-expressions-core.md) ·
[133 wires](brief-em3d-133-wire-point-expressions.md) · [134 polylines](brief-em3d-134-polyline-point-expressions.md)
**Tag:** `R-em3d131-n` (shared rules), each brief its own `R-em3d13x-n`
**Status:** Written 2026-10-09, not started. Owner decisions D1–D4 all taken 2026-10-09.
**Depends on:** brief 51 (dimensions as expressions), brief 50 (bond wires), 3D editor round 4 (wire arrays, loop
height and span), commit deb6199f (wire point add/remove and `RenumberPointExpressions`).

---

## 0. The question

The owner wants geometry drawn from points to be driven by the document's variables, as a box's size or a cylinder's
radius already is. Examples: a wire's loop apex at `h_loop`; a foot at `x_pad + 50um` that follows the pad when
`x_pad` changes; a wire end whose z sits on a pad whose height is itself an expression; a polyline vertex placed by a
variable.

Brief 51 left every point list as numbers ("Binding a point list is a later brief"). This series binds two of them: a
**wire's `Points`** and a **polyline's `Points` / `Points3`**. Outlines, holes and polyhedron vertices stay numbers.

**Why a series rather than one brief.** The work divides into three parts, each of which lands and is tested on its own:
- **132, core** (`src/Design` only). The binding table holds only fixed-arity fields, so it needs a *list* spec, plus
  the JSON contract, evaluation, and the two pure functions both object kinds use: renumbering on insert and remove,
  and the offset writer of D2.
- **133, wires.** Their edits rewrite points in eight ways. Their ends sit on pads.
- **134, polylines.** They have no Inspector point rows today (only a count), they refuse vertex edits ("construction
  geometry"), and they have a 2D form and a 3D form.

Order: 132, then 133 and 134 in either order. Neither depends on the other.

## 1. Owner decisions

- **D1 — a wire end's z may be an expression** (2026-10-09). A design drawn with expressions can move a pad's top, and
  the end must stay on that surface. 133 R-em3d133-2 has the details.
- **D2 — a move writes its offset INTO the expression** (2026-10-09). Moving a point or an object whose points are
  bound keeps each expression and adds the offset as a `+` or `-` term, folding into a trailing literal when there is
  one: `x_pad + 50um` moved by +20 µm becomes `x_pad + 70um`, and `x_pad` becomes `x_pad + 20um`. The variable itself
  is never rewritten, because other geometry may use it. Rules: R-em3d131-2.
- **D3 — scope: wires and polylines** (2026-10-09). Polyhedron vertices are deferred.
- **D4 — Rotate and Mirror: exact where they can be, refused where they cannot** (2026-10-09). A quarter-turn about a
  principal axis, or a mirror, maps each component to a signed copy of another plus a constant, and that is written
  exactly: `x' = -(y_expr) + c`, with D2's folding on the constant. A half-turn is two quarter-turns. Any other angle
  refuses, naming the first bound field it would have to change. An unbound component is still a number and simply
  rotates. Rules: R-em3d131-5.

## 2. Shared rules

**R-em3d131-1 — the key.** Point *k* of list property `P` has its own expression-map entry, keyed **`P[k]`**, with one
slot per component: `Points[k]` (3 slots for a wire, 2 for a polyline's in-plane points) and `Points3[k]` (3). Its
field paths are `P[k][c]`. On screen they read `w1 point 2 z`, 1-based, as the Inspector labels rows. This is the
shape `RenumberPointExpressions` (deb6199f) already renumbers, and 132 moves that function into `src/Design` and
generalises it over the property name. **A file with no expression in a point list is byte-identical to today's.**

**R-em3d131-2 — the offset writer (D2).**
- The offset term is written in the document's display unit with its suffix. Its sign becomes the operator: `- 20um`,
  never `+ -20um`.
- **Folding.** A trailing `+`/`-` literal (a number with or without a unit) absorbs the offset. A fold that reaches
  zero drops the term. Repeated moves therefore never grow an expression. The fold works on the parsed AST, never on
  the string.
- **Precedence.** The term is added at the top level. A root other than an addition, product, function call or atom
  is parenthesised first.
- **A zero offset leaves that component's text byte-identical.**
- **An unbound component** is still a plain number and simply moves.

**R-em3d131-3 — everything that is not a translation keeps brief 51's drag rule**, except where a brief says otherwise.
For example, a wire's Re-seat writes an end's z rather than offsetting it. A component the edit changes that holds a
bare name writes the name; any other expression refuses the edit, naming the field. **A wire's Loop height is the
exception** (owner decision, 2026-10-09; 133 R-em3d133-4): it is refused on a wire with any point expression, unless
the only bound components are its two ends' z.

**R-em3d131-4 — inserting and removing points.** A new point is a plain number, the Catmull–Rom midpoint of its
neighbours (deb6199f). Later points' entries are renumbered, and a removed point's entries go with it. Both are one
undo entry each.

**R-em3d131-5 — the swap writer (D4).** For a quarter-turn or a mirror, the component that lands on axis *a* is
`s·(expr_b) + c`, with sign *s* = ±1 and constant *c* in DBU. A `+1` sign writes `expr_b` as it is. A `-1` sign
negates `expr_b`. Negation keeps a trailing `+`/`-` literal outside the parentheses with its sign flipped
(`-(t + 5um)` is written `-(t) - 5um`), drops the parentheses when what is left is an atom (a name or a literal),
and simplifies `-(-(e))` to `e`. Example: `ey` turned by +90° about z becomes `-(ey) + c`; the inverse turn negates
that to `ey - c` and adds `+ c`, which folds back to `ey`. `c` is added through R-em3d131-2's offset writer, so it folds and drops at zero in the
same way. A quarter-turn followed by its inverse therefore reads back as the original text. A component that stays on
its own axis with sign `+1` gets only the offset writer. The writer works on the AST, never on the string.

## 3. Series gates

1. Every shipped and fixture `.c3d` re-saves byte for byte (`TerminalWavePortTests.Gate5_EveryShippedAndFixtureC3d_ReSavesUnchanged`).
2. Each brief's own gates. Run the targeted classes only, never all of `Ui.Tests`.
3. At the end of the series: one example `.c3d` whose wire end z and a polyline vertex are bound to the same variables
   as the geometry they meet. Changing the variable keeps both attached.

## 4. Out of scope

- Polyhedron vertices, outlines and holes.
- A wire's loop height and span as stored fields. They stay derived from the points (brief 50).
- D2 applied to *placements*. A polyline's Move changes its `Placement`, which keeps brief 51's own rule (see 134).
