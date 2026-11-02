# Sonnet Brief — Phase L4e: the Gerber reader

**Design:** `docs/design/layout-view.md` §8 (interchange). **Consumes L4a (GDSII), L4b (DXF),
L4c (Gerber + Excellon export), L4d (board import).**

**First of four phases that together add Gerber import.** L4e reads artwork; **L4f** reads drill files
and rebuilds vias; **L4g** orchestrates a whole file set into a cell and a technology; **L4h** adds the
menu entry and the round-trip gate. Build them in that order — each later phase assumes the earlier
one's output shape.

**Scope of THIS phase is one file at a time, text in and geometry out.** `GerberReader` touches bytes,
tokens and coordinates. It does not know what a `CellFolder`, a `Technology`, a `Messages` sink or a
dialog is — that split is `PcbReader`/`PcbImport`'s and `DxfReader`/`DxfImport`'s, and it is what makes
the reader headlessly testable against fixtures with no workspace anywhere.

**Write from the published format specification only.** Never read another simulator's or CAM tool's
source for this: §8's standing rule ("write from the public spec — never ingest GPL sources") applies to
this format exactly as it does to GDSII.

**Test loop** (root `CLAUDE.md` §"Layout/UI work") — two commands; this SDK rejects more than one
project path per invocation:
```
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

---

## 1. The one structural fact this whole phase follows from

**A Gerber file is a painted image, not a shape list.** Strokes, flashes and filled regions are laid
down in order by a small state machine, and each object is either *dark* (adds material) or *clear*
(erases it). Nothing in the file says "this is a pad" or "this is a trace"; the file says "select this
aperture, move here, expose".

That is why importing is a categorically bigger job than exporting, and it is the reason for almost
every requirement below. Export starts from typed shapes and paints them. Import has to **replay the
painting** and decide what typed shapes, if any, the result should become. §6 is where that decision
lives, and it is the most consequential paragraph in this brief.

**R-L4e-0. One more consumer of `InterchangeStructure` and the shared reconciliation.** Format-specific
code touches bytes and tokens, never editor state. The reader returns a neutral result; L4g maps it onto
layers, a technology and a cell. If this phase grows its own parallel neutral model, or its own layer
dialog, it has gone wrong.

## 2. Coordinate format and units

**R-L4e-1. Parse the declared format; never assume one.** `%FS<zero><notation>X<i><d>Y<i><d>*%` gives
zero omission (`L` leading omitted, `T` trailing omitted), notation (`A` absolute, `I` incremental) and
the integer/decimal digit counts per axis. `%MOMM*%` / `%MOIN*%` gives units, and the deprecated `G70`
(inch) / `G71` (mm) codes appear in real files and mean the same thing.

**Inch is at least as common as millimetre in circulation.** Do not treat the mm path as the normal one
and the inch path as an afterthought; our own writer emits mm, so the mm path will be exercised by the
round trip alone and the inch path will not be exercised at all unless it is tested deliberately.

**R-L4e-2. The unit conversion is exact more often than it looks, and where it is not, round and say
so.** At `LayoutUnits.DefaultDbuPerMicron` = 1000, 1 DBU = 1 nm:

| Declared format | One output unit | In DBU | Exact? |
|---|---|---|---|
| mm, 6 decimals | 1e-6 mm | 1 | yes |
| mm, ≤6 decimals | 1e-n mm | 10^(6-n) | yes |
| inch, 4 decimals | 1e-4 in | 2540 | yes |
| inch, 5 decimals | 1e-5 in | 254 | yes |
| inch, 6 decimals | 1e-6 in | 25.4 | **no** |

Where the mapping is exact, it is an integer multiply — no `double` anywhere on that path. Where it is
not, use `Math.Round` and report the worst-case error once, in the result, as a number. **Never a cast:**
`(long)(x * s)` truncates toward zero and is therefore wrong only for negative coordinates, which is
exactly the bug that survives a fixture drawn in the first quadrant (L4d's R-L4d-2, same trap, and
board coordinates commonly go negative because the origin sits at the board centre).

Export's rule is "refuse rather than silently round" (L4c R-L4c-1). **Import inverts it: round, but
report.** Refusing to read a real file the user already has is a worse outcome than refusing to write
one, because there is no alternative path for them.

**R-L4e-3. Incremental notation (`%FSI…`) is legal, rare, and silently catastrophic if misread.**
Support it or refuse the file by name. Do not read incremental coordinates as absolute.

## 3. The state machine, and the deprecated spellings that are still everywhere

**R-L4e-4. Modal state is not an optimization in this format — it is the syntax.** Track: current point,
current aperture, interpolation mode (`G01` linear / `G02` CW arc / `G03` CCW arc), quadrant mode
(`G74` single / `G75` multi), polarity (`%LPD%` / `%LPC%`), and region mode (`G36` / `G37`). Two
consequences that real files depend on heavily:

- **An omitted coordinate word inherits.** `Y1506D01*` keeps the current X.
- **An omitted operation code repeats.** A block that is *only* coordinates — `X1092501*` — repeats the
  last D-code. Files exist in which the great majority of blocks are of this form, so this is not an
  edge case to bolt on later.

**R-L4e-5. These deprecated forms must parse, because they are in files people actually hold:**

- `G54D10*` — the obsolete aperture-select prefix. `G54` is a no-op; the `D10` still selects.
- `G70` / `G71` (units), `G90` / `G91` (absolute / incremental).
- `D02*` with no coordinates at all — a move to the current point, i.e. a no-op that terminates a
  region contour.
- Zero-padded D-codes: `%ADD010C,0.001*%` is aperture 10.
- Empty blocks — a bare `*` — and `G04` comments, which may appear anywhere.
- `%IN…*%` (image name) and `%LN…*%` (layer name): **record them, act on neither.** The layer name is
  useful evidence for L4g's identity cascade; it is not authority.

**R-L4e-6. Report every unrecognized command by name, once, with a count** — never per occurrence,
never silently. This is L4d's R-L4d-1 verbatim, and for the same reason: a file full of things we skip
must not read as a file full of mysteries.

## 4. Apertures

**R-L4e-7. Standard aperture shapes: `C` (circle), `R` (rectangle), `O` (obround), `P` (regular
polygon).** All four, not just the two that the simplest files use — obround pads are ordinary in real
artwork and a reader that drops them drops pads.

Each may carry a **hole modifier** — a further `X<d>` (round hole) or `X<x>X<y>` (rectangular hole) —
which makes the flash a shape *with a hole in it*. A `CircleShape` cannot carry a hole, so a holed
circular aperture flashes as a `PolygonShape` (or `CurveShape`) with a hole ring. Count these; they are
the one case where R-L4e-9's shape-identity mapping cannot apply.

**R-L4e-8. Aperture macros (`%AM`).** A macro is a named template built from primitives, instantiated by
`%ADD<code><name>,<arg>X<arg>…*%`. Implement primitives **1** (circle), **4** (outline), **5** (regular
polygon), **7** (thermal), **20/2** (vector line), **21** (centre line) and **22** (lower-left line).
Primitive **6** (moiré) is a fiducial/annotation construct — skip it and count it, by name.

Two properties of the macro language that a naive implementation gets wrong:

- **The exposure modifier can be 0, meaning the primitive *erases* within the aperture.** That is how
  thermal reliefs and annular rings are drawn. A macro flash is therefore a shape that may have holes,
  and the compositing is *inside the aperture*, before the flash is placed.
- **Modifiers can be arithmetic expressions** over the macro's own `$1`, `$2`, … arguments, with `+`,
  `-`, `x` or `X` (multiply), `/` and parentheses, plus assignment to `$n` in a preceding block.

**Write a dedicated ~60-line evaluator for that arithmetic, and state why in its header.** Root
`CLAUDE.md` says circuitRF has one expression engine and that string substitution is never the answer —
that invariant is about the *circuit* expression language (variables, cell parameters, SDD equations,
measurements), and this is a foreign grammar that collides with it: `x` is a multiplication operator
here, there are no named variables, no functions, and no Real/Complex/Bool kinding. Routing Gerber macro
modifiers through the circuit engine would be a worse fit than a purpose-built evaluator, and the
reason belongs in the code rather than in this brief only.

**R-L4e-9. The aperture-to-shape mapping is load-bearing for the round trip. Do not "simplify" it.**

| Flash of… | becomes |
|---|---|
| circle aperture | `CircleShape` |
| rectangle aperture | `RectShape` |
| obround / polygon / macro / any holed aperture | `PolygonShape` (with holes where present) |

The first two rows exist because L4c's writer emits a circle flash for every `CircleShape` and
`ViaShape`. Turning a circle flash into a 64-sided polygon would still *render* correctly and would
quietly destroy the round trip — L4h's gate asserts this mapping directly for exactly that reason.

**Dedupe by (shape, modifiers), not by D-code.** Two files in a set may use different codes for the
same aperture.

## 5. Draws, arcs and regions

**R-L4e-10. A `D01` outside region mode is a stroke.** With a circular aperture it becomes a
`PathShape` with `End = PathEndStyle.Round` and `Width` = the aperture diameter — which is precisely
what L4c's writer emits for a round-capped path, so this closes that loop. Consecutive `D01`s with the
same aperture and no intervening `D02` are **one** path, not one path per segment.

Stroking with a **non-circular** aperture is deprecated but occurs. Sweep the aperture along the
centreline (Clipper2 Minkowski sum, via the existing `LayoutClipper` seam) into a region, and count it
by name — it is a real degradation and the summary must say it happened.

**R-L4e-11. Arcs, both quadrant modes.** `I`/`J` are the offsets from the start point to the arc centre.

- Under `G75` (multi-quadrant) they are **signed** and the centre is unambiguous.
- Under `G74` (single-quadrant) they are **unsigned magnitudes**: the centre is one of four candidates
  (±I, ±J), and the correct one is the candidate whose distance to *both* endpoints agrees to within a
  DBU or two **and** whose sweep stays within a single quadrant. Test both endpoints — a candidate that
  matches only the start is not eliminated by radius alone.

Both codes appear in real files, sometimes both in the same file, and a file may switch back to `G74`
after its arcs are drawn. **Convert to a bulge edge (`EdgeKind.Arc`), never to a polyline.** L4c's
writer emits arcs; the round trip depends on getting an arc back, and a flattened arc can never be
re-exported as one.

**R-L4e-12. Regions (`G36`/`G37`).** The contour is the `D01` sequence between them; a `D02` inside the
region starts a *new* contour. Outer contours are boundaries, inner contours cut holes — build
`PolygonShape`/`CurveShape` with `Holes`. Arcs inside a region boundary are legal and common.

Drop the duplicate closing vertex: a region contour ends where it began, and `LayoutShape` polygons are
implicitly closed. Leaving it in produces a zero-length edge that survives into every downstream
consumer, and it breaks byte-identity on re-export. Skip and count degenerate contours (fewer than three
distinct vertices).

## 6. Polarity — the decision that shapes the whole importer

`%LPC*%` makes subsequent objects **erase**. `LayoutShape` has no such concept: our model has polygons
with holes, and nothing else.

**R-L4e-13. Composite only the layers that actually need it.**

- **A layer that never uses `%LPC*%` imports primitive-for-primitive.** A stroke stays a `PathShape`, a
  circle flash stays a `CircleShape`, a region stays a `PolygonShape`. This is what makes the round trip
  exact, and it is what keeps an imported design *editable* rather than a single welded blob.
- **A layer that does use `%LPC*%` must be composited** — union the dark objects and subtract the clear
  ones, **in paint order**, through Clipper. There is no other correct reading of the file. The result
  is polygons, and the shape identities of that layer are gone.
- **Never composite a layer that does not need it.** Compositing is always *correct* and is therefore
  tempting as a uniform rule; it destroys shape identity, and shape identity is what L4h's gate is
  defined on. Decide per layer, from the file's own content, and report which layers were composited and
  why.

**R-L4e-14. Negative images and the deprecated transforms are refused by name, never ignored.**

- `%IPNEG*%` inverts the whole image, which requires a bounding frame to subtract from — a frame the
  file does not supply. Refuse the file with a sentence that names the command. An inside-out layer that
  looks plausible is worse than an import that did not happen.
- `%MI` (mirror), `%OF` (offset), `%SF` (scale), `%AS` (axis select): apply them if the implementation
  is trivial, otherwise refuse by name. **Silently ignoring `%MI` yields a mirrored board that looks
  entirely plausible** — the same failure mode L4d's R-L4d-3 exists to prevent.

## 7. Step-and-repeat and block apertures

**R-L4e-15. `%SR` repeats the enclosed block on a grid. Flatten it, and report the multiplication.**
Do **not** map it onto `LayoutInstance`. Step-and-repeat is panelization — it says "this board appears
six times on the manufacturing panel", not "this design has a sub-cell" — and mapping it to hierarchy
would oblige the writer to reproduce it, which L4c cannot do, breaking the round trip on the first
cycle. Report the factor so a user who imported a panel knows why the shape count is what it is.

`%AB` block apertures (X2) are the same decision: flatten, or refuse by name if not implemented.

## 8. X2 attributes — the round trip's spine

**R-L4e-16. Read the attribute set, and carry all of it on the neutral result.** A modern export
carries far more than L4c's writer emits, and every item below is load-bearing for a later phase:

| Attribute | Why it matters |
|---|---|
| `%TF.FileFunction` | What the file *is* — rung 1 of L4g's identity cascade, and it names the copper layer's **position in the stack** (`Copper,L1,Top` … `Copper,L4,Bot`) |
| `%TF.FilePolarity` | See R-L4e-17 — **not** an image inversion |
| `%TA.AperFunction` | What an aperture is **for**: `ViaPad`, `ComponentPad`, `SMDPad`, `Conductor`, `NonConductor`, `Profile`. Declares via-ness instead of leaving L4f to infer it |
| `%TO.N` | Net name → `LayoutShape.Net`, where L4c's writer took it from |
| `%TO.C`, `%TO.P` | Component reference and pad/pin number — see L4g's R-L4g-12 |

**R-L4e-17. `%TF.FilePolarity,Negative` does NOT mean "invert this image".** It declares what the
artwork *represents* — for a solder mask, openings rather than coverage — while the file itself is
painted positive, with `%LPD` and no `%IP` anywhere. Reading it as an inversion turns every solder mask
inside out, which renders plausibly and is completely wrong. It is a different command from `%IPNEG`
(R-L4e-14), which genuinely does invert and is genuinely refused.

**R-L4e-18. Attribute mechanics that a naive reader gets wrong:**

- **Object attributes are modal, and `%TD*%` with no name deletes them ALL** — not just one. A bare
  `%TD` is common (it is how a writer resets state between objects) and treating it as a no-op leaves
  stale nets and functions attached to every subsequent object.
- **Attribute values carry `\uXXXX` escapes** for characters that would otherwise terminate the block —
  `\u002A` is `*`. Unescape them, or a component reference containing one arrives mangled.
- **Match attribute values case-insensitively.** The same file set spells a file function
  `Soldermask` in the artwork and `SolderMask` in its own job file; both are the same thing.

Record unknown attributes by name, once, with a count (R-L4e-6). Do not fail on an attribute.

## 9. Scale

**R-L4e-19. Tens of thousands of draws in one file is ordinary, not pathological.** Older CAM output
paints a copper pour as thousands of parallel strokes ("vector fill") rather than as a region; a single
layer of a modest board can hold 75,000 draws. Read them all — that is well inside the envelope the
500k-shape layout performance work already covers.

Return the **per-layer stroke count** on the result so L4g can tell the user that a pour arrived as N
strokes and that the existing Merge action is the fix. A vector-filled pour is correct artwork and is
neither editable copper nor meshable, and the user cannot act on what they are not told.

**R-L4e-20. A ceiling refusal happens before allocation, with its number named** — L4d's R-L4d-20,
unchanged.

**R-L4e-21. Counters only. No wall-clock assertion anywhere in this phase's tests** (root `CLAUDE.md`,
and the standing repo rule against new timing tests). Assert *what* was read, never how fast.

## 10. Scope guardrails

- **Reader only.** No `CellFolder`, no `Technology`, no `Messages`, no dialog, no menu — those are L4g
  and L4h.
- **No drill files.** Excellon is L4f, including its own format-inference problem.
- No change to the L4c writer in this phase. If the round trip later needs one, that is L4h's finding
  to make with evidence.
- No new mesher or EM work: imported artwork is ordinary artwork.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 11. Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); documented gate green; no existing test regresses.
2. **Exact units (R-L4e-2)** — an inch file at 4 and at 5 decimals lands on exact DBU; a **negative**
   coordinate is the test. A 6-decimal inch file imports, rounds, and the reported worst-case error is
   asserted as a number.
3. **Modal coordinates and modal D-codes (R-L4e-4)** — a fixture whose blocks are mostly bare
   coordinates imports the same geometry as the same file written out longhand.
4. **Deprecated spellings (R-L4e-5)** — `G54D10`, `G70`, a bare `D02*`, a zero-padded `%ADD010`, and a
   stray `*` all parse; none is refused.
5. **Aperture shapes (R-L4e-7)** — C, R, O and P each flash at the right size and place; a holed
   aperture yields a shape with a hole.
6. **Macros (R-L4e-8)** — a macro with an exposure-0 primitive yields a flash with a hole; a macro whose
   modifier is an expression over `$1`/`$2` evaluates correctly, including `x` as multiply.
7. **Shape identity (R-L4e-9)** — a circle flash is a `CircleShape`, a rect flash a `RectShape`, a
   round-capped stroke a `PathShape` with the aperture's `Width`. Asserted on the types, directly.
8. **Arcs (R-L4e-11)** — a `G75` arc round-trips its bulge; a `G74` single-quadrant arc resolves to the
   correct one of four candidate centres, proven by a fixture whose wrong candidates are geometrically
   plausible.
9. **Regions (R-L4e-12)** — a region with an inner contour yields a polygon with a hole; the closing
   vertex is not duplicated; a degenerate contour is skipped and counted.
10. **Polarity (R-L4e-13)** — a layer with no `%LPC` keeps its primitives (asserted on shape types); a
    layer with `%LPC` composites to the correct geometry and is reported as composited.
11. **Refusals are named (R-L4e-14)** — `%IPNEG` and an unimplemented `%MI` each produce a refusal whose
    message names the command. Neither is silently ignored.
12. **Step-and-repeat (R-L4e-15)** — an `%SR` block yields the flattened repetitions and reports the
    factor; no `LayoutInstance` is created.
13. **X2 attributes (R-L4e-16/17/18)** — `%TF.FileFunction`, `%TA.AperFunction`, `%TO.N`, `%TO.C` and
    `%TO.P` survive onto the result; a **bare** `%TD` clears every object attribute, not one; a
    `\u002A` escape in an attribute value is unescaped; `%TF.FilePolarity,Negative` does **not** invert
    the image, asserted on a solder-mask fixture.
14. **Unknown commands** — a file carrying a command the reader does not know imports everything else
    and reports that command **once**, with a count.
15. **Vector fill (R-L4e-19)** — a fixture whose pour is painted as strokes imports every stroke and
    reports the count.
16. **Counters only (R-L4e-21)** — no wall-clock assertion anywhere.

**Fixtures.** Author them in this phase where possible; a fixture committed under `testdata/` that this
phase did not author must be one whose licence permits redistribution and whose content does not name a
vendor, tool or product in a way root `CLAUDE.md` §"Commercial Vendor References" forbids. A
hand-authored fixture is worth less as a dialect test but costs nothing to redistribute — prefer it, and
say in the completion note which dialect properties therefore went untested.

## 12. On completion

Write a **"Phase L4e — COMPLETE"** entry at the top of `src/Ui/RESOLVED.md` — **not** `CLAUDE.md`.
Call out:

1. **Which dialect features were exercised by a real fixture and which only by an authored one.** The
   gap is the honest measure of how much of the format is actually proven, and L4h's round trip cannot
   close it — a self-written file only proves we read what we wrote.
2. **The polarity decision as it actually landed** (R-L4e-13): how "composite only where needed" was
   determined per layer, and what a composited layer costs in lost shape identity.
3. **The macro evaluator**: what the grammar turned out to require, and whether anything about it argues
   for or against the dedicated-evaluator choice.
4. **The single-quadrant arc resolution** and how the four-candidate ambiguity was proven resolved.
5. **Stated limitations** — every command that refuses by name, and every construct skipped and counted.
6. **The largest file read and its shape count**, as one measured number, so L4g and L4h can size
   against it.
