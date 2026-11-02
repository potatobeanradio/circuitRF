# Brief IMF-3 — What is wire: plates, boxes, annotation arrows, dashes

**Series:** `brief-imf-0-overview.md` (F3, F4, F5, L6) · **Tag:** `R-imf3-<m>`
**Depends on:** IMF-2
**Area:** `src/Design/Schematic/Recognition/` (`WireGraph.cs`, `SchematicImageReading.cs` — the curved-lines
refusal, `SchematicImageReport.cs`), design note §19

---

## 1. Goal

What the wire graph takes for wire is wire and nothing else, whatever the pen. Today, on datasheets: hairline
capacitor plates with a wide gap become two wires (ds-a, ds-h), a double-headed `Vref` dimension arrow becomes a wire
that **shorts two nets** (ds-d), and a dashed fixture box's sides become wire (ds-e). On textbook pictures a heavy
plate becomes a wire with a junction on it (b, f), IEC box sides become wire (c), a zig-zag refuses the whole picture
(a), and a dashed continuation becomes a row of parts (e, f).

## 2. Requirements

**R-imf3-1 — Plates by shape, at any pen weight.** A straight run is a **plate** when both its ends stop in bare paper
and exactly one wire end meets it (at its interior, from the perpendicular) — whatever its length — **or** when it
lies parallel to another such run with the strip between them clear of ink. The pair's gap may be anything from 1 `w`
(a textbook plate pair) to 1.5 `s` (a datasheet's hairline plates, set far apart). A **curved plate** (an arc within
45° of the other plate's direction, its chord meeting the same rule; ds-d's `Ci`, `Co`, `CADJ`) is a plate. A plate
is never a wire candidate. The length limit `MaxSymbolStroke` remains only for a run that meets no wire, now in `s`.

**R-imf3-2 — Boxes at any pen weight.** The cycle rule (§9.3) finds a box; it fails on heavy pens because the corners
fill in and the sides' skeleton runs stop short of each other. Close the corners first: two perpendicular runs whose
ends lie within 1.5 `w` of a common point are joined there before the cycle search. A box is a symbol outline whatever
its size; a hairline datasheet IC box with a gradient inside is a box (its fill is IMF-2's).

**R-imf3-3 — Annotation arrows are never wire (F4).** Before the graph is built, every **arrow** is found — a straight
shaft with a filled or open head (a triangle ≤ 2 `s`, its apex on the shaft's line) — and classified:
- **dimension arrow** — heads at both ends, or a head at one end and the other end on a wire node it does not join
  in line with (ds-d's `Vref`): an annotation; its shaft and heads are removed from the ink, the word beside it is a
  comment;
- **current arrow** — one head, the shaft beside a wire within 2 `s` and parallel to it, or **on** a wire with the
  head inline (ds-b's `ID`, ds-d's `IAdj`): an annotation; an inline head is removed and the wire under it kept;
- **voltage arrow** — one head, no wire at either end, spanning two terminals (b): an annotation, kept as evidence
  for IMF-6's port pairing;
- **arrow into a terminal** — a wire ending in a head pointing into a terminal dot or circle (c): the wire continues
  into the terminal (one net); the head is removed;
- **supply arrow** — a head at the free end of a wire, pointing away along it, with a supply word beside it (ds-a's
  `VCC`): a supply mark (IMF-8);
- **arrow on a symbol** — a head crossing a resistor or capacitor at an angle (a variable part): left in the region
  for IMF-5.
Every class is counted in the report and drawn in the overlay's annotation class.

**R-imf3-4 — Dashed lines and dashed boxes.** Three or more collinear runs of similar length (± 30 %) at similar gaps,
each under 3 `s`, form a **dashed line**. A dashed line that closes into a rectangle, or whose ends meet no wire in
line, is an **annotation** (a fixture boundary, ds-e) and is removed from the ink. A dashed line in line with a wire at
one or both ends is a **wire** flagged `Dashed` — it abbreviates a connection (e, f) — drawn as an ordinary wire and
named once in the report ("a dashed line was read as a wire; it usually stands for repeated sections left out").

**R-imf3-5 — A zig-zag never refuses a picture (F5).** The curved-lines refusal (§13.4) excludes every slanted piece
of a **zig-zag chain** — three or more slanted runs alternating in direction, each 1–6 `s` long, between two collinear
orthogonal leads — before it counts. A Smith chart still has no such chains and is still refused.

**R-imf3-6 — Junction marks of any shape.** A junction is a filled blob 2–6 `w` across at a node of degree ≥ 3 —
round, square or diamond (e) — and **at datasheet scale** a dot up to 4 `w` on a 1 px hairline, where today's 6 `w`
upper bound and IMF-2's resampling must agree.

**R-imf3-7 — The report** counts plates (pairs, curved), boxes, arrows by class, dashed lines by role, zig-zag chains
excluded from the curvature count, and junction marks by shape.

## 3. Not in this phase
Which part a stroke belongs to (IMF-4); what a box or a triangle is (IMF-5, IMF-8).

## 4. Gates (minimal tests, run only these classes)
- `ImageDatasheetStyleTests` — `ds-regulator`'s `Vref` arrow is no wire and the two nets it spans stay two; its
  curved plates are plates; `ds-rf-amp`'s hairline plates are plates at 1× and 2×; `ds-test-circuit`'s dashed box makes
  no wire. Turn those on.
- `ImageWebStyleTests` — `textbook-zigzag` is not refused; `heavy-iec`'s plates and box sides are not wire;
  `ladder-dashed` reads each rail as one net. Turn those on.
- `WireGraphStrokeTests` (new, in-memory): a hairline plate pair 1.2 `s` apart; a double-headed arrow between two
  wires; a current arrow on a wire; a dashed rectangle; a filled diamond at a T.
- `ImageNothingHereTests` — the Smith chart is still refused.
