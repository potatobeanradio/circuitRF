# Sonnet Brief — Phase L4d: `.kicad_pcb` board import

**Design:** `docs/design/layout-view.md` §8 (interchange), §2.4 (technology and stackup), §3.1a (holes),
§10 (EM). **Consumes L4a (GDSII), L4b (DXF), L4c (Gerber + Excellon), and the via/stackup work.**

**Depends on L3d (arbitrary-angle instances).** Placements in this format carry an arbitrary angle; on a
four-way `LayoutInstance.Rot` every rotated placement would have to be flattened to shapes on import,
losing hierarchy and multiplying the shape count. Do not start this phase first.

**Scope is import only.** No writer of this format, in this phase or as a stretch goal — see §1.

**Test loop** (root `CLAUDE.md` §"Layout/UI work") — two commands; this SDK rejects more than one
project path per invocation:
```
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

---

## 1. Why this direction, and why only this direction

**The value is not the geometry — it is the stackup and the nets.** A `.kicad_pcb` carries, per layer,
a thickness, a relative permittivity and a loss tangent, plus a net table every track, via, pad and
copper pour references. That is most of a `.ctech` and most of an EM setup, arriving for free. An
importer that read geometry and dropped the stackup would not be worth building; §4 is the phase's
centre of gravity, not an accessory to §5.

**A writer is deliberately excluded.** Emitting a board file means authoring board-setup and design-rule
state circuitRF has no opinion about — inventing values on the user's behalf, in a file that is then
theirs to fabricate from. The outward handoff is already served: L4b writes DXF with exact layer names
and colours, and the graphics-import path of every board tool reads it. If an outward path is wanted
later it should be a *footprint*-scoped writer, which is geometry plus pads and nothing invented; that
is a separate brief and is not in scope here.

**R-L4d-0. This is one more consumer of `InterchangeStructure` and the shared layer-mapping dialog**
(§8 R15, unchanged): format-specific code touches bytes and tokens, never editor state. If this phase
grows its own parallel neutral model, it has gone wrong.

## 2. The format, and the one rule that survives its versions

S-expressions. Tokens are lowercase and unquoted; strings are double-quoted UTF-8; numbers are decimal
millimetres with **no exponent notation**; booleans are `yes`/`no`. The header is
`(kicad_pcb (version YYYYMMDD) (generator "...") ...)` — **the version is a date stamp that changes with
each release epoch of the originating tool**, and files from every epoch are in circulation.

**R-L4d-1. Dispatch on the tokens actually present. Never branch on the version stamp, and never refuse
a file for its version.** This is L4b's own proven principle, quoted from §8: the reader "dispatches
purely on the group codes it understands and reports what it does not... No file is ever refused for its
version." Concretely, the same entity must be accepted in more than one spelling:

- a graphic's stroke width as `(width W)` **or** `(stroke (width W) (type ...))`;
- an arc as `(start)(mid)(end)` **or** the older centre-plus-`(angle A)` form — distinguish by whether
  `mid` or `angle` is present, never by the file's version;
- a cubic as `gr_curve` **or** `bezier`.

Report every unrecognized token **by name, once, with a count** — never per occurrence, never silently.
A file full of tokens we skip must not read as a file full of mysteries.

**R-L4d-2. Units are exact, and the trap is on the negative side.** Coordinates are decimal millimetres;
`LayoutUnits.DefaultDbuPerMicron` is 1000, so **1 DBU = 1 nm** and 1 mm = 1,000,000 DBU exactly — this
format's internal grid and ours are the same grid, which is a fidelity no other importer here enjoys.
Convert with `Math.Round`, never a cast: `(long)(x * 1e6)` truncates toward zero and is therefore wrong
only for negative coordinates, which is exactly the bug that survives a fixture drawn in the first
quadrant.

**R-L4d-3. Y is DOWN in the source and UP in `.clay`. Flip once, at entry, and never again.** A sign
error here yields a mirrored board that looks entirely plausible. Pin it with a fixture that is
asymmetric on **both** axes — the WB-C lesson: a bridge between two coordinate conventions can only be
tested by geometry that is off both axes.

## 3. Layers

`(layers (0 "F.Cu" signal) (31 "B.Cu" signal) (36 "B.SilkS" user "B.Silkscreen") ...)` — ordinal,
canonical name, type, optional user-facing name.

**R-L4d-4. Add a `PcbLayerName` alias to `InterchangeMapping`** — additive and nullable, exactly like
the existing `DxfLayerName`, so every existing `.ctech` loads unchanged.

**Reconciliation reuses the shared dialog**, following `DxfLayerReconciliation`'s pattern, with "Add to
technology" pre-selected and pre-filled from the file's own canonical name. L4b already justified that
divergence from the cross-technology-paste default: a board file's layer names are the author's
deliberate intent, not an accident of a paste. Do not write a second reconciliation.

## 4. Stackup → technology — the reason to build this at all

```
(setup (stackup
  (layer "F.Cu"        (type "copper")  (thickness 0.035))
  (layer "dielectric 1"(type "core")    (thickness 1.51) (material "...")
                       (epsilon_r 4.5)  (loss_tangent 0.02))
  ...))
```

**R-L4d-5. Map onto `Stackup`/`StackupLayer` directly:** `copper` → `StackupKind.Conductor`;
`core`/`prepreg` → `Dielectric`; `thickness` → `ThicknessDbu`; `epsilon_r` → `Epsr`; `loss_tangent` →
`TanD`. The file's order is top-to-bottom and must stay top-to-bottom — a reversed stackup simulates
cleanly and answers the wrong question.

**R-L4d-6. The stackup section is OPTIONAL, and its absence must not be papered over.** A board whose
author never opened the stackup page has only an overall board thickness. When the section is missing:
import the geometry, leave the technology's stackup empty, and say so in one Messages line naming what
the EM path will need. **Do not fabricate a plausible default substrate** — an invented stackup is worse
than none, because nothing downstream will ever question it and it *will* be simulated.

**R-L4d-7. Two quantities the format does not carry at all: conductor conductivity (`SigmaSm`) and
`Mur`.** Default conductivity to copper, leave `Mur` at 1, and report both as defaults in the same note.
**Do not infer either from the `material` string** — that is a lookup table of laminate trade names, it
is out of scope, and it would put third-party product names into this repo (root `CLAUDE.md`
§"Commercial Vendor References").

**R-L4d-8.** `BoundaryCondition` (the stackup's `Top`/`Bottom` open-or-ground) has no counterpart in the
format. Leave the technology's own defaults and say so in the same note. One honest paragraph about what
was and was not recovered beats three separate silent assumptions.

## 5. Geometry

Two coordinate spaces only: board-level items are already in board coordinates; a footprint's items are
in the footprint's frame and compose through §7's placement.

| Source | Becomes |
|---|---|
| `gr_line` (start, end, width) | `PathShape`, two points, `End = Round` |
| `gr_rect` **filled** | `RectShape` |
| `gr_rect` **unfilled** | a `PathShape` tracing the four edges at the stroke width |
| `gr_circle` **filled** (centre, end) | `CircleShape`, radius = distance from `centre` to `end` |
| `gr_circle` **unfilled** | `CurveShape` annulus — outer and inner rings at ± half the stroke width |
| `gr_arc` | `PathShape` with a single `Arc` edge; bulge = `tan(sweep/4)`, signed |
| `gr_poly` filled / unfilled | `PolygonShape` / outline `PathShape` |
| `gr_curve` \| `bezier` | `CurveShape` or `PathShape` with a `Cubic` edge |
| `gr_text` | `LabelShape`, **never** `IsPort` |
| `segment` (start, end, width, layer, net) | `PathShape`, `End = Round`, `Net` set |
| `arc` (start, mid, end, width, net) | `PathShape` with an `Arc` edge |
| `via` (at, size, drill, layers, net) | `ViaShape` — see R-L4d-10 |
| images, dimensions, groups | skipped, reported by type with a count |

For the three-point arc form, derive the centre from the circumcircle of `(start, mid, end)` and take
the sweep's sign from the cross product of `(mid − start)` and `(end − mid)`. Bulge from sweep is the
existing convention (`LayoutEdge.Bulge` = `tan(sweep/4)`, signed); reuse `LayoutArcPromotion` rather
than deriving a second time.

**R-L4d-9. An unfilled outline must never become a filled region.** This is the highest-consequence
silent error in the phase: a `gr_rect` with `(fill no)` imported as a `RectShape` is an entire copper
pour that does not exist on the board, and it will be meshed and simulated as one. Assert it directly.

**R-L4d-10. On a via, `Layer` is the BARREL and `LandingLayer` is the PAD.** `ViaShape`'s own doc
comment states the consequence in as many words — getting it backwards "produces a GDSII/DXF export that
looks plausible and puts copper where the hole should be." Read that comment before writing either
field. `(layers "F.Cu" "B.Cu")` gives the span; the model carries one landing layer, so map a through
via faithfully and **report blind and buried vias by count, naming how they were placed**, rather than
pretending the model expressed them.

## 6. Zones — take the fill, not the outline

**R-L4d-11. Import `filled_polygon`, not `polygon`.** The outline is the author's *request*; the filled
polygons are the copper that exists. For EM only the copper exists.

**R-L4d-12. A zone with no `filled_polygon` was never filled — skip it, and do not fall back to the
outline.** The outline includes every area the fill would have cleared around pads and neighbouring
nets; importing it as copper shorts the board. Report the count of unfilled zones by net and layer in
one line, because that number is the user's cue to go fill the board and re-import.

**R-L4d-13. `keepout` zones are not copper.** Skipped, counted, reported.

**R-L4d-14. How a fill stores its islands and its holes is the one thing the published documentation
does not fully pin down — determine it from real files and write down what you found.** `island`
markers and island-removal modes are documented; the representation of a *hole* inside a filled area is
not. `PolygonShape.Holes` can express whatever it turns out to be (and `LayoutClipper.EnsureValidHoles`
normalizes any construction path that is not Clipper2 output, per §3.1a R10b). This is the primary
subject of §10's spike; do not design §6 before that spike has run.

## 7. Footprints

**R-L4d-15. A footprint becomes an instance of a generated cell, content-addressed.** Every placement of
the same library part shares one cell — this is `GeneratedCellStore`'s existing keying (L5's R-L5-1), and
it is what keeps a board with 400 identical parts at 400 instances rather than 400 copies of geometry.

**Placement is `(at X Y [ANGLE])` with an arbitrary angle, and a footprint on the back layer is a mirror
combined with that angle.** Do the mirror-versus-rotation-order algebra against
`LayoutInstanceTransform`'s own convention **before** writing the reader — `GdsiiTransformCodec`'s header
documents exactly this class of trap (its `STRANS` reflects about a different axis than our `MirrorX`,
which is why the reflect-then-rotate-180 correction exists). Pin it with an all-combinations comparison,
as `LayoutGdsiiTransformTests` does.

**R-L4d-16. Pads.**

| Pad shape | Becomes |
|---|---|
| `circle` | `CircleShape` |
| `rect` | `RectShape`, or `PolygonShape` when the pad carries its own angle |
| `oval` | `PathShape` with `End = Round` — width = the smaller dimension, length = the difference |
| `roundrect` | `RoundedRectShape`, `CornerRadius = roundrect_rratio × min(size.x, size.y)` |
| `trapezoid` | `PolygonShape` |
| `custom` | its `(primitives ...)`, which are the same graphic tokens as §5 — **reuse that code path; do not write a second graphics reader** |

- A pad's own `(at x y ANGLE)` is relative to the footprint and composes with the footprint's placement.
- A `thru_hole` or `np_thru_hole` pad with `(drill ...)` also produces a `ViaShape` (pad plus drill).
  **Oval drills and drill offsets are not expressible** — report them by count rather than silently
  rounding them to a round drill at the pad centre.
- A pad's `layers` list may use wildcards (`*.Cu`, `F&B.Cu`); expand them against the board's own layer
  table, not against a hard-coded list.

**R-L4d-17. Pads populate the generated cell's `LayoutPin` list.** `Name` = the pad number, `Layer` = the
copper layer, `WidthDbu` = the pad's extent across the direction it faces. This is what makes an imported
board's connection points selectable as EM ports, and it is the reason the type exists —
`LayoutModel.cs` says so directly: "a pin list on the view is what lets artwork that was merely IMPORTED
carry connectivity too."

## 8. Nets

**R-L4d-18.** The top-level `(net N "NAME")` entries are the ordinal → name table; every track, via, zone
and pad carries `(net N)`. Set `LayoutShape.Net` to the **name**, never the ordinal — the field is a
string and a design carrying `"7"` where it should carry `"VDD"` is unusable for port setup and for DRC's
`NetScope`. **Net 0 is the unassigned net: leave `Net` null, not `""`.**

## 9. Scale — import all of it, and say what comes next

A real board is tens of thousands of entities across four to eight layers, and as a whole it is not a
MoM problem.

**R-L4d-19. Import the whole board. Do not filter, and do not make the user choose a region during the
import.** Cropping is an *edit*, and the editor already has one — selection plus `LayoutClipper`. Making
import do it would put a second, worse selection UI inside a file reader. Say it plainly in the
post-import summary: what came in, and that the EM path expects a cropped region.

**R-L4d-20. Refuse before allocating, not during.** Count entities in a first pass and apply the same
class of ceiling `LayoutFlatten`'s `FlattenAllLevelsHardCeiling` already establishes, **naming the number
in the refusal**. A reader that dies partway through a large board leaves the user with a half-imported
layout and no explanation.

**R-L4d-21. Assert counters, never wall clock** — entities read, shapes produced, generated cells
created, spatial-index population. Root `CLAUDE.md`'s benchmark rule and the standing "no new timing
tests" instruction both apply: a timing assertion here would measure the machine.

## 10. The spike, before the phase

**R-L4d-22. One day, before any of §5–§8 is designed.** Parse one real, filled, multi-layer board with a
throwaway reader and **dump what it actually contains**:

1. the fill representation — islands, and how a hole inside a filled area is stored (§6, R-L4d-14);
2. whether the `setup`/`stackup` section is populated, and with which fields;
3. which arc and stroke spellings appear (R-L4d-1);
4. the spread of footprint angles and which pad shapes are present;
5. whether blind or buried vias, oval drills or drill offsets appear.

Write the findings into the completion note. **If the fill representation needs per-polygon hole
matching, say so and re-size §6 before building it** rather than discovering it mid-phase.

**Fixtures must be files this phase did not author**, and a fixture committed under `testdata/` must be
one whose licence permits redistribution and whose content does not name a vendor or product in a way
root `CLAUDE.md` §"Commercial Vendor References" forbids. When in doubt, author a board in the
originating tool and commit that instead — an authored fixture is worth less as a dialect test but costs
nothing to redistribute. Do not read the originating tool's source for any purpose: it is GPL, and §8's
standing rule ("write from the public spec — never ingest GPL sources") applies to this format exactly as
it does to GDSII.

## 11. Scope guardrails

- **Import only.** No writer of this format, not even a partial one.
- **No schematic side.** Netlist interchange is `SpiceNetlistReader`'s own question and is not in scope.
- No footprint libraries, no 3D models, no design rules, no net classes, no groups — read, report, and
  do not apply.
- No new mesher or EM work: an imported board is ordinary artwork the existing path already handles.
- No change to the GDSII, DXF or Gerber paths beyond `InterchangeMapping`'s additive field.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 12. Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); documented gate green; no existing test regresses.
2. **Handedness (R-L4d-3)** — a fixture asymmetric on both axes imports at the correct handedness; a
   mirrored import fails the test.
3. **Exact units (R-L4d-2)** — `−12.3456 mm` lands on exactly `−12345600` DBU. The negative case is the
   test.
4. **Stackup present** — `Epsr`, `TanD` and thickness match the file, top-to-bottom order preserved.
5. **Stackup absent (R-L4d-6)** — geometry imports, the stackup stays empty, one Messages line says so,
   and **no substrate is fabricated**.
6. **Defaults reported (R-L4d-7)** — conductivity and `Mur` are named as defaults, and nothing is
   inferred from the material string.
7. **Unfilled stays unfilled (R-L4d-9)** — a `(fill no)` rect does not become a copper pour.
8. **Zones (R-L4d-11/12/13)** — a filled zone imports its fill; an unfilled zone is skipped and counted;
   a keepout is skipped and counted; the outline is never imported as copper.
9. **Via orientation (R-L4d-10)** — barrel on `Layer`, pad on `LandingLayer`, proven by rendering or
   exporting and comparing, **not** by reading the two fields back.
10. **Footprints (R-L4d-15)** — the same part placed four times at 0°, 90°, 37.5° and back-side-180°
    yields **one** generated cell and four instances, each rendering identically to that footprint
    flattened in place.
11. **Pads → pins (R-L4d-17)** — a two-pad footprint yields two `LayoutPin`s with names and widths, and
    the EM port picker can select one.
12. **Nets (R-L4d-18)** — a track's `Net` is the name; net 0 leaves it null.
13. **Version tolerance (R-L4d-1)** — two fixtures from different format epochs both import, neither is
    refused, and both token-level differences (stroke spelling, arc parameterization) are handled.
14. **Unknown tokens** — a file carrying a token the reader does not know imports everything else and
    reports that token **once**, with a count.
15. **Ceiling (R-L4d-20)** — an oversized file is refused before allocation, with its number named.
16. **Counters only (R-L4d-21)** — entity and shape counts asserted; **no wall-clock assertion anywhere**.

## 13. On completion

Write a **"Phase L4d — COMPLETE"** entry at the top of `src/Ui/RESOLVED.md` — **not** `CLAUDE.md`.
Call out:

1. **The spike's findings as measurements** (R-L4d-22): the fill/hole representation as actually
   observed, which arc and stroke spellings the fixtures held, and the footprint-angle spread.
2. **The via barrel/pad orientation proof** and how it was demonstrated.
3. **The stackup-absent behaviour**, and what an imported board still needs before it can be simulated.
4. **The stated limitations**: blind/buried vias, oval drills and drill offsets, `BoundaryCondition`,
   conductivity and `Mur`.
5. **What a cropped region of an imported board actually costs to mesh and solve** — one measured number,
   so the next phase can size against it.
6. Whether a footprint-scoped writer is now worth a follow-up brief, and on what evidence.
