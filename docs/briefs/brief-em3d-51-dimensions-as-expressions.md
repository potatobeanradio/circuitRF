# Brief 51 — dimensions as expressions: VARs in the `.c3d`, and cell parameters passed down

**Series:** [3D EM, third series](brief-em3d-40-overview.md) · **Tag:** `R-em3d51-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §6.3 (*each dimension an expression … elaborate
first*); [`expressions.md`](../design/expressions.md) §8 (units), §9 (scope), §9.1 (the `=`/`≈` preview),
§10 (cycles); [`workspace-and-project-tree.md`](../design/workspace-and-project-tree.md) §2 (`.ccell`
parameters); [`pcell-parameter-handles.md`](../design/pcell-parameter-handles.md) §4 (driving a value
from a drag)
**Area:** `src/Design/ThreeD/` (persistence, scope, elaboration), `.ccell` parameters,
`src/Ui/ThreeD/` (the typed field of brief 45 §4, the Variables panel, Properties, the drag rule),
`src/Cli/{Check,Explain}.cs`
**Depends on:** 45 (its typed field), 47, 48 · **Blocks:** 52 (only the parameterised cavity)
**Owner decisions:** D8 (in this series), D12 (a same-name VAR is linked by default, and linked means the
cell parameter wins)

---

## 0. What this brief delivers

**Typing an expression as a dimension.** In brief 45's mid-gesture field, and anywhere a dimension is
typed (Properties, brief 46's Array panel), the user may type `w`, `2*w`, or `h_sub + t_met` instead of
a number. Brief 45's gate did not need this; it works from this brief on.

**An undefined name is defined on the spot.** If the expression names something that does not exist, the
field asks for it inline — a value, a unit, and whether it is a **VAR of this 3D view** or a **cell
parameter** — and the gesture completes. The new VAR is **stored in the `.c3d`**, as a VAR is stored in
a `.csch`.

**VARs and cell parameters are reconciled.** A `.c3d` sees its cell's parameters and its own VARs, in one
namespace. **A VAR with the same name as a cell parameter can be linked to it**, so the parameter's value
— an instance's override, or else the parameter's default — **passes down into the VAR**, and every
dimension that uses the VAR follows.

Also here, from the first version of this brief:
- instance overrides of a 3D child's parameters, evaluated in the parent's scope;
- cycle detection across all of it;
- the rule for **dragging** a dimension that holds an expression.

**Running a sweep over those names is not here** (overview §4).

---

## 1. `R-em3d51-0` — first, settle what the circuit side does with a same-name VAR

The circuit side already has VARs and cell parameters in one cell scope, and a `.c3d` should not invent a
different meaning for the same situation without saying so. **The code and a comment disagree:**
- `Elaborator.BuildCellScope` binds the parameter defaults, then the cell's variables, then the instance
  overrides, each `Bind` replacing the last. **Read that way**, a same-name VAR replaces the parameter's
  default, and an instance override still wins over both;
- `SubcircuitTranslation.CarryGlobals` says a same-name variable *"would bind over it and seal it
  shut"*, meaning the override would no longer reach it.

**Before anything else**, write one headless test: a cell with parameter `w` and a VAR `w`, instanced
twice, once overriding `w`. Record which is true in `src/Core/RESOLVED.md`. Change nothing on the
circuit side.

§3's rule for the `.c3d` is stated explicitly either way. If it differs from what the test finds, the
reference page says how and why, in one paragraph.

## 2. `R-em3d51-1` — the format

**`R-em3d51-1a` A dimension field holds a number or an expression.** Brief 41's integer DBU stays the
number form. The expression form is an object, and it **always carries its unit**:

```jsonc
"Size": [ { "Expr": "w", "Unit": "Mil" }, 1270000, { "Expr": "h_sub + t_met", "Unit": "Um" } ]
```

**Why the unit is stored, and not taken from the document.** The obvious shortcut is to use `DisplayUnit`
as every expression's site unit. It would make **changing the display unit change the geometry**, which
`layout-view.md` §1.3 forbids (*changing the display unit is free*). So the unit in force when the user
typed is written beside the expression, and nothing about the document's display ever changes what an
expression means.

**`R-em3d51-1b` Which fields may hold an expression:** every **named dimension**:
- `Min`, `Size` (box, rectangle);
- `Offset`, `Height`, `Shear` (prism, sheet, polyline);
- `Base`, `Length`, `Radius` (cylinder);
- `ThicknessUm`;
- `Placement.Origin` and each `Rotate` angle (degrees);
- `Array.Counts` (an integer ≥ 1 is required; a non-integer is refused, not rounded) and `Array.Pitch`;
- a port's `Rect`;
- a wire's `DiameterUm`.

**Point lists stay numbers:** `Outline`, `Holes`, a polyline's `Points`, a polyhedron's `Vertices`, a
wire's `Points`. A value typed for one of those is **evaluated once** and stored as a number, and the
field says so as the user types (*"outline points hold numbers — evaluated once"*). The same applies to a
wire's loop height, which is not stored (a wire's points are its only truth, brief 50), and to a Move's
displacement, which changes a placement and has no field of its own. Binding a point list is a later
brief.

**`R-em3d51-1c` VARs** — the `Variables` list brief 41 reserved — use the record a `.csch` VAR row holds:

```jsonc
"Variables": [
  { "Name": "w",     "Expression": "10",     "Unit": "Mil" },
  { "Name": "h_sub", "Expression": "10",     "Unit": "Mil", "Linked": true },   // §3
  { "Name": "gap",   "Expression": "w / 4" }                                    // unit-less: takes w's
]
```

Names are validated by the expression engine's rules: no built-in function name, and not `j`, `pi`, `e`
or `freq` (expressions.md §3, reserved names). They are also unique among the VARs.

**`R-em3d51-1d` Units** follow expressions.md §8, and there are **no units inside an expression** (`2*w +
5um` does not parse):
- a VAR's `Unit` scales its expression, as a `.csch` VAR's does;
- a field's `Unit` is its site unit;
- **var-unit-wins** applies (`Evaluator.Eval(expr, scope, unit)`): when an expression references a
  unit-bearing name, the site unit is skipped. That is the engine's rule, and it is not re-implemented
  here.

The length units must be the ones `Units` knows. If `mil` or `inch` is missing there, adding one is a
row in `_scales` **and** a row in `_baseUnitMap`. Forgetting the second is silent (expressions.md §8
says so).

**The trap, in bold on the reference page and in the typed field's preview:** in `2*w + 5` with `w`
unit-bearing, the site unit is skipped, so the literal `5` is **5 metres**. §4b's live preview shows the
resulting 5.000254 m as the user types. `check` warns on any dimension above 1 m, which is the mark a
unit slip leaves (the sweep-unit lesson: a run at 2 Hz looked entirely normal).

## 3. `R-em3d51-2` — scope: cell parameters, VARs, and passing a parameter down

**`R-em3d51-2a` What a `.c3d` sees**, in the cell's own scope (expressions.md §9):
- its **cell's parameters** (the `.ccell`'s — one interface for all of a cell's views);
- its own **VARs**.

**There are no globals.** A `.c3d` is not in a TestBench. A setup's own fields keep today's evaluation,
and letting a setup reference a `.c3d` VAR is a later brief. A **loose** `.c3d` (in no cell folder) sees
its VARs only.

**`R-em3d51-2b` A name that is both a cell parameter and a VAR** is the owner's case. The VAR carries
`Linked`:

| `Linked` | The VAR's value | The VAR's own `Expression` | Instance overrides of the name |
|---|---|---|---|
| **true** (the default when the names match, D12) | **the cell parameter's**: the instance's override, else the `.ccell` default | kept, shown greyed. It is used only when the document is elaborated with no cell (a loose copy), or after unlinking | **reach it**, and every dimension that uses it |
| false | its own expression | in force | **do not reach it**. `check` warns: *"VAR `w` hides cell parameter `w`; an instance's override of `w` does not change this 3D view. Link it, or rename one."* |

**Why linked means "the parameter wins, default included".** The alternative is the circuit side's order
as `BuildCellScope` reads (§1): the VAR replaces the default, and only overrides get through. That gives
a cell **two defaults** for one name: the `.ccell`'s, used by the schematic and symbol, and the VAR's,
used by the 3D view. An instance that overrides nothing would then be one size in the schematic and
another in 3D, with no warning. Linking to the parameter as a whole keeps **one** default, in the one
file that is the cell's interface.

**`R-em3d51-2c` Editing a linked VAR edits the parameter.** In the Variables panel (§5), a linked VAR's
row shows `w ← cell parameter = 10 mil`, and editing its value writes the **`.ccell` default**. There is
one number, so there is nothing to keep in step. The edit is one undo entry, even though it writes
another file.

**`R-em3d51-2d` Promote to Cell Parameter** (a VAR's context menu):
1. writes a `.ccell` parameter with the VAR's name, its expression as the default, and its unit;
2. sets `Linked`.

Values agree at the moment of promotion by construction. It is one undo entry across both files.
Adding a parameter is an interface change, and SL3's interface-change report handles it as it handles
one added from the parameter editor.

**`R-em3d51-2e` Instance overrides.** A `ThreeD` instance may carry `"Params": { "w": { "Expr": "2*a",
"Unit": "Mil" } }`:
- each name must be a **cell parameter** of the child. A name that is only a VAR there is refused:
  *"`w` is a VAR of `Lid`'s 3D view, not a parameter; promote it there to override it here"*;
- an override is evaluated in the **parent's** scope (override → parent scope; default → own scope,
  expressions.md §9);
- a `Layout` instance takes no overrides. A `.clay` has no parameters, and a PCell's belong to its
  generator. Refused, naming the view.

**`R-em3d51-2f` Cycles** (CLAUDE.md: *mandatory*) across `.ccell` defaults, VARs, links and overrides,
through the engine's own resolving stack (expressions.md §10). The chain is reported, `a → b → a`, before
any geometry is built.

## 4. `R-em3d51-3` — typing an expression, and defining a name on the spot

**`R-em3d51-3a` The typed field** (brief 45 §4, and every other place a dimension is typed) parses in this
order:
1. `number [unit]` → exact DBU through `LayoutUnits.TryParse`, stored as an integer, as before;
2. otherwise `expression [unit]` → the expression engine. A trailing length unit is the site unit, as on
   a `.cnl` line (`L=L1 nH`), and with no trailing unit the display unit is the site unit. It is stored as
   §2a's object form, with the unit written out.

**`R-em3d51-3b` A live preview** beside the field: the engine's own `=`/`≈` hint (expressions.md §9.1),
**in the display unit**, updated per keystroke. It is evaluated against a flat design-time mirror of the
cell's parameters and the document's VARs. A syntax error or an unknown name shows as
`unknown: <name>`, never as a blank. The rubber band follows the previewed value, so `2*w` draws at 2 ×
`w`'s current value before Enter.

**`R-em3d51-3c` Enter with an unknown name opens the Define strip, inline, beneath the field.** It is not
a modal dialog: the gesture is paused, not abandoned (§8.2 point 5). One row per unknown name:
- **value**, prefilled. When the expression is the bare name (`w`), it is prefilled with the dimension the
  rubber band currently shows, in the display unit, because that is what the user was about to type
  anyway. Otherwise it is blank;
- **unit**, defaulting to the display unit;
- **as:** *VAR in this 3D view* (the default) or *Cell parameter*. The second writes the `.ccell` and is
  offered only when the document is in a cell.

**Enter** defines every row and completes the step. The definitions and the new object are **one undo
entry**. **Esc** returns to the field with the text intact.

A name that is invalid (§2c) is refused inside its row. A name that already exists as a parameter is not
unknown, so it is never offered: parameters resolve directly, with no VAR needed.

**`R-em3d51-3d` Resolution happens before geometry** (§6), so what the user sees after Enter is the
elaborated result. It is never a guess that elaboration later disagrees with.

## 5. `R-em3d51-4` — the Variables panel and Properties

**`R-em3d51-4a` A Variables panel** beside the Setups panel. Each VAR row shows name, expression, unit,
resolved value (in the display unit), link state, and the number of fields that use it. The actions:
- **Add**, **Edit**, **Delete**, **Rename**;
- **Link** / **Unlink**, offered when a cell parameter of the same name exists;
- **Promote to Cell Parameter**.

Some actions have rules:
- **Delete** of a VAR still in use is refused, listing the fields that use it, with an *Inline its value*
  alternative that writes numbers into those fields;
- **Rename** rewrites every reference in the document **through the AST**, never by string substitution
  (CLAUDE.md, *never string substitution*). `ww` does not contain `w`. Rename is one undo entry;
- the cell's parameters are listed below the VARs, read-only here, with a link to the cell-parameter
  editor.

**`R-em3d51-4b` Properties** shows each dimension as **expression + resolved value**
(`h_sub + t_met = 0.254 mm`), editable as either. A resolution error shows **on the field**, red, with the
engine's message. The document keeps the text, and elaboration refuses with the same message.

## 6. `R-em3d51-5` — elaboration: expressions first, then geometry

**`R-em3d51-5a`** Every expression resolves **before** any geometry is built (CLAUDE.md: *the numeric
layer sees only fully-resolved values*; §6.3: *elaborate first*). The order:
1. the cell's parameter defaults, or the instance's overrides evaluated in the parent's scope;
2. then the VARs, linked ones taking the parameter's value (§3b);
3. then each field, at its own site unit.

A result is rounded to the document's DBU **once**. If that rounding moved it by more than 1e-9
relative, a note names the field, because two expressions meant to meet exactly may then not (overview
§1d).

**`R-em3d51-5b` Caching** (brief 42 §4) keys an object on its **resolved** fields. Changing `w`
re-elaborates exactly the objects whose resolved values changed. An instance's child is keyed on its
override **values**, so two instances with equal overrides share one child elaboration.

**`R-em3d51-5c` Headless:**
- VARs, links and overrides are authored by writing the `.c3d` (and the `.ccell`), and the reference page
  describes them, including §3b's table;
- `explain x.c3d --expr "2*w"` evaluates in the document's resolved scope, `--set` applied first, as
  `explain --expr` does for a circuit;
- `explain` reports, per name, whether it came from an override, a `.ccell` default, a linked VAR or an
  unlinked VAR;
- `check` reports undefined names (error), an unlinked shadowing VAR (warning), a dimension above 1 m
  (warning), and an unused VAR (info).

## 7. `R-em3d51-6` — dragging a dimension that holds an expression

A drag (briefs 46, 47) on a field that holds an expression cannot just write a number, or the expression is
silently lost. The rule, per field:

| The field is… | A drag… |
|---|---|
| a number | writes the number, as before |
| **a bare reference to an unlinked VAR** (`gap`) | writes the **VAR's** expression |
| **a bare reference to a cell parameter, or to a linked VAR** (`w`) | writes the **parameter**: its `.ccell` default, or, when pushed into an instance's context, that instance's override |
| **linear in exactly one name** (`2*w + gap`, with `gap` fixed), found by measurement as `PCellHandleSolver` measures sensitivity | solves for that name, and writes it as above |
| anything else | is **refused at the grab**, naming the field, with a one-click *Replace with number* |

**The preview shows everything the edit will move.** A drag that writes `w` moves every object that uses
`w`, and those objects preview too, through the kernel path (brief 47 §6). The counter gate becomes
*objects previewed = objects whose resolved fields change*. **Nothing moves on commit that did not move in
the preview.**

## 8. Gate

`tests/Ui.Tests/ThreeD/Expressions*`, plus the one Core test of §1.

0. **The circuit side's same-name behaviour** is pinned by a test and recorded (§1).
1. **Round trip** with expressions, VARs, links and overrides, byte for byte.
2. **The display unit is free.** Changing `DisplayUnit` from mil to µm changes no elaborated coordinate
   (1e-15 relative) in a document full of expressions.
3. **Linked vs unlinked.** With `w` a parameter (default 10 mil) and a VAR `w` (12 mil):
   - linked, with no override → 10 mil;
   - linked, with an override of 20 mil → 20 mil;
   - unlinked → 12 mil in both cases, and `check` warns.
4. **Promote** writes the `.ccell` parameter and links. Undo removes both.
5. **Define on the spot.** Typing `w` for a box width with `w` unknown, then Enter with the prefilled row,
   gives a VAR `w` equal to the rubber band's width and a box whose `Size.x` is `{Expr: "w"}`. One undo
   removes both.
6. **Units.** `2*w + 5` with a unit-bearing `w` resolves the literal as 5 m, the preview shows it, and
   `check` warns.
7. **Resolution order and scope.** An override referencing a parent VAR resolves in the parent's scope.
   An override of a VAR-only name is refused.
8. **Cycles** through a `.ccell` default, a linked VAR and an override are refused with the chain, before
   any geometry.
9. **Rename through the AST.** Renaming `w` to `width` leaves `ww` untouched.
10. **Cache.** Changing `w` re-elaborates exactly the objects that use it. Two instances with equal
    overrides share one child (counters).
11. **Drag rule.** Each row of §7's table, on the view model; a refusal leaves the document
    byte-identical. For a drag writing `w`, the previewed set equals the set that changed after commit.

## 9. Owner check (pixels not seen)

In the **Debug** build:
- draw a box and type `w` for its width; define `w` in the strip; draw a second box whose height is `w/2`;
- change `w` in the Variables panel and watch both move;
- promote `w`; place the cell twice in a parent with different overrides;
- make a second VAR with a parameter's name, unlink it, and read `check`'s warning;
- drag a face bound to `w` and watch the other `w` faces follow; try to drag `2*w*l` and see the refusal.

## 10. Scope

- No parametric sweep (overview §4).
- Expressions in point lists (§2b).
- Setup fields referencing `.c3d` VARs (§3a).
- No change to the circuit side's scope rule, whatever §1 finds.
