# Brief IM-8 — Symbols: what each region is, which way it faces, where its pins are

**Series:** `brief-img-0-overview.md` (D1, D9) · **Tag:** `R-im8-<m>`
**Depends on:** IM-7
**Area:** `src/Design/Schematic/Recognition/` (`SymbolTemplates.cs`, `SymbolMatch.cs`, `IImageSymbolClassifier.cs`,
`RecognisedSymbol.cs`), `src/Design/resources/image-symbol-templates/` (new, the alternate drawings), design note §9

---

## 1. Goal

Each symbol region becomes a component kind, an orientation and an ordered set of pins — or, honestly, an unknown.

## 2. Requirements

**R-im8-1 — Templates from circuitRF's own symbols.** `SymbolTemplates.BuiltIn()` draws each relevant
`BuiltInSymbols` entry (resistor, inductor, capacitor, ground, port/term, pin, transmission lines — the TLIN and
microstrip family's symbols, short and open stubs, diode, the FET and BJT symbols) with **the same renderer the
application draws with** (`src/Render`), at unit pin span, as centre-line strokes plus pin positions. One drawing, not
a second copy of the symbols' geometry: if a symbol's look changes, its template follows.

**R-im8-2 — Templates for the drawings other tools use.** Pictures are rarely drawn by circuitRF. A small set of
alternate drawings, stored as stroke files in `src/Design/resources/image-symbol-templates/` (written in-house, each with its kind,
pin positions and a one-line description of the convention it follows — never a tool's name): the IEC resistor (a
rectangle), the IEC inductor (a filled or hollow rectangle), the four-hump and three-hump coils, the curved-plate and
polarised capacitors, the triangle and chassis grounds, the terminal circle, the coaxial-cylinder transmission line,
the box-with-line transmission line, the amplifier triangle, the generic IC box. Adding a drawing is adding a file.

**R-im8-3 — Matching.** For a region with *n* attachments, every template with *n* pins is tried in the 8 orientations
(4 rotations × mirror) whose pin directions agree with the attachments' incoming directions (± 20°); the template is
scaled so its pin span equals the attachments' span. The score is the **modified-Hausdorff distance** between the
region's skeleton and the template's strokes — the AS-10 measure and its implementation (`StrokeGlyphs`' matcher,
generalised to a stroke set if needed, not copied), in units of `w`. Accepted within a threshold and clear of the
runner-up of a *different kind* by a margin; same-kind variants do not compete. The winning orientation gives the pin
order.

**R-im8-4 — Structure checks.** Cheap rules that settle what a distance cannot: a capacitor has two parallel bars
perpendicular to its axis with a gap; a resistor's zig-zag has ≥ 3 alternating vertices; a coil has ≥ 3 humps on one
side of its axis; a ground has one attachment and bars shrinking away from it. A template match that fails its kind's
structure check is demoted to its runner-up.

**R-im8-5 — Multi-pin and unknown (D9).** A region with ≥ 3 attachments that is not a recognised three-terminal device
— or is one (a transistor, an amplifier) — is **cut out**: each attachment becomes a port in IM-10, exactly as AS D9 cuts
out a multi-pin part. A two-attachment region matching nothing becomes kind `?` with its two pins; a one-attachment
region matching nothing is reported as an unread terminal and left open.

**R-im8-6 — The seam (D1).** `IImageSymbolClassifier` is what the reader calls, with the template matcher as the one
implementation; a later classifier is added by implementing it. Nothing else in the pipeline knows which classifier
answered.

**R-im8-7 — The record.** `RecognisedSymbol`: region, kind (or `?`, or *cut out*), template (and so its drawing
convention), orientation, pins in order with their pixel positions and their wire nodes, score, runner-up, and
confidence (the parts table's dot).

**R-im8-8 — The report.** Symbols by kind, unknowns, cut-out devices with their pin counts, demotions by a structure
check, unread terminals.

## 3. Not in this phase
Values and designators (IM-9); the circuit (IM-10).

## 4. Gates (minimal tests, run only these classes)
In-memory pictures, each symbol drawn by the test (not by `src/Render`, so the gate does not compare the renderer with
itself) in the convention named:
- `SymbolMatchTests` — one `InlineData` per kind and convention: US and IEC resistor, coil and IEC inductor, flat and
  curved capacitor, ground, terminal, box transmission line, diode — each read with the right kind and orientation
  at 0° and 90°; a mirrored diode reads its pins in the mirrored order.
- `SymbolStructureTests` — two parallel bars with no gap (a drawn rectangle's sides) are not a capacitor.
- `SymbolCutOutTests` — a FET with three wires is cut out with three pins; an unknown blob between two wires is `?`.
- `SymbolTemplatesTests` — every built-in template has the pin count of its `BuiltInSymbols` entry.
