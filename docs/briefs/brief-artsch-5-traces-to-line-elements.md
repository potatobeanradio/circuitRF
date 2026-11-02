# Brief AS-5 — Traces to line elements

**Series:** `brief-artsch-0-overview.md` (D3, D13, D14, D16, D19) · **Tag:** `R-as5-<m>`
**Depends on:** AS-1 (CPWG, SLIN), AS-2 (TLIN physical form), AS-3 (the board graph)
**Area:** `src/Design/Layout/Recognition/` (`LineRecognition.cs`, `LineSegmentation.cs`, `LineJunctions.cs`,
`LineElement.cs`, `LineTypeChoice.cs`); `src/Design/Layout/Em/TraceImpedanceAnalysis*.cs` (additions to its
output records only — see R-as5-1); reads `TraceCrossSection`, `MicrostripSubstrateInjection` and AS-1's
injection

---

## 1. Goal

Every signal island's copper between parts, ports and vias becomes a chain of **line elements** — MLIN with its
bends, tees, crosses and tapers where the line is microstrip; CPWG or SLIN where it is grounded coplanar or
stripline; TLIN in its physical form for anything else — with physical widths and lengths read from the artwork,
connected node to node to the parts and ports AS-3 and AS-4 found.

## 2. Requirements

**R-as5-1 — The trace review is the reader.** One call to `TraceImpedanceAnalysis.Analyze` over the scope's layers
supplies pieces, chains, end kinds, stations (W, Z0, εeff, gaps, `Configuration`, references). Its target and
tolerance do not matter here and its findings are ignored. Where AS-5 needs something the analysis computes but
does not return — a **junction's member traces and its centre**, a chain's **bend corners** (the gaps between
pieces, already described in `TraceRun.Pieces`' comment as "bends, jogs or steps") — it is **added to the output
records** (`TraceRun`, a new `TraceJunction`), not recomputed by walking the copper again. The shared-cut solve
cache is what keeps this affordable; a counter gate holds it (R-as5-9).

**R-as5-2 — Line type per segment (D13), with the coplanar threshold as an option.** Each trace is split where its
stations' `Configuration` changes, ignoring changes shorter than max(2·W, 0.5 mm) (absorbed into the surrounding
type). Each segment's type is its stations' length-weighted majority, then `LineTypeChoice` maps it per D13.
**Coplanar reading is a choice the user controls** (the owner's field observation: an encroaching top-side ground
can turn what the designer meant as microstrip into GCPW):
- `RecognitionOptions.Coplanar = Auto | Microstrip | Gcpw`. `Auto` reads a segment as GCPW only when **both** side
  gaps are at most `CoplanarGapFactor × H` (default **3**, the classifier's own threshold; the option re-applies it
  per segment from the stations' `GapLeft`/`GapRight`, so it can be tightened without touching the review).
  `Microstrip` reads every GCPW segment as MLIN; `Gcpw` reads every microstrip segment with a measured gap on both
  sides as CPWG.
- Whatever is chosen, **every MLIN and CPWG element records the measured gaps** in its artwork metadata (D12,
  D19), so AS-11's swap can turn one into the other with the board's own gap.

**R-as5-3 — Straight runs.** Collinear pieces of one width class (`MergeToleranceMicrons`) merge into one element.
A piece shorter than max(W, 100 µm) is absorbed into its longer neighbour (its length added, its width the
neighbour's). Width is the class's nominal; length is centre-line length between **reference planes** (R-as5-5).

**R-as5-4 — Discontinuities (D14).**
- **MLIN regions.** A bend corner between two pieces → **MBEND** with its measured angle; a chamfered outer
  corner → `Miter` from the cut length against `MicrostripDiscontinuities.MiterCutLength` (nearest of the model's
  miter options); a 90° corner made as two 45° chamfered corners → two 45° MBENDs with the short diagonal between
  them as an MLIN. A three-trace junction → **MTEE** (the two most nearly collinear arms are the through arms, the
  third the branch, in the model's own W1/W2/W3 convention); four → **MCROSS**; more than four → a plain node,
  reported. A width change along a straight run where the edges are not parallel over more than 2·W → **MTAPER**
  (W1, W2, L); shorter, an abutting step. MKLOPF is not recognised.
- **CPWG / SLIN / TLIN regions.** A bend is centre-line length; a junction is a plain node; a step abuts. Reported
  once per class.

**R-as5-5 — Reference planes.** Lengths are measured so that elements abut without double-counting copper:
an MBEND owns its corner square (each adjoining line loses W/2 from the corner point); an MTEE/MCROSS owns its
junction (each arm loses half the width of the crossing arm, the models' own convention — read it from the
models and cite it); a part's pad owns its pad (the line stops at the pad edge); a via owns its land (the line stops
at the land's edge). A line whose remaining length is ≤ 0 after this is absorbed into the element it ran into,
and counted.

**R-as5-6 — Ends.** From the trace review's end kinds: `pad` → the AS-4 part's node; `via` → the AS-3 via
element's node; `junction` → the junction element's node; `open end` → an open-ended line (no open-end model,
noted); a port's location → the port's node. An end that lands on nothing AS-3/AS-4 know is an open end and is
listed.

**R-as5-7 — Element parameters.**
- **MLIN / MBEND / MTEE / MCROSS / MTAPER:** widths and lengths only; the substrate is **not** written — it is
  injected from the technology at extraction, as for a hand-placed MLIN. The signal layer is the trace's layer.
- **CPWG:** `W`, `L`, `G` = the segment's length-weighted mean gap; substrate injected (AS-1).
- **SLIN:** `W`, `L`; `H1`/`H2` and substrate injected (AS-1).
- **TLIN (physical form, AS-2):** `L`; `Z` and `Eeff` the segment's length-weighted mean of the solved stations;
  `F` = the top of the analysis range (D15); `Ad` from the filling factor
  ((π·f/c₀)·(Er/√Eeff)·((Eeff−1)/(Er−1))·tanδ, in dB/m); `Ac` from the strip estimate Rs/(Z0·W) in dB/m —
  **stated as an estimate** in the design note. Each TLIN records its fallback reason (D13's table) for the report.
- A segment with no solved station is TLIN at the neighbouring segment's Z and εeff, reported as unsolved.

**R-as5-8 — Coupled pairs (D14).** Two segments on one layer, parallel within 2°, edge-to-edge gap ≤ 3× their mean
width, overlapping for more than λ/20 at the top frequency (λ from εeff) → one *coupled, modelled uncoupled*
finding with both anchors. Nothing changes in the circuit.

**R-as5-9 — Report classes.** Elements by type; TLIN fallbacks by reason; segments read as GCPW and as MLIN under
`Auto` (so a user sees the coplanar reading's effect at once); junctions with more than four arms; open ends;
absorbed slivers; coupled pairs; unsolved segments.

## 3. Not in this phase
Emitting the netlist and drawing (AS-6); the swap command (AS-11); step, open-end and gap models (D18).

## 4. Gates (minimal tests, run only these classes)
Synthetic boards built in memory on a two-layer and a four-layer synthetic technology:
- `LineSegmentationTests` — a straight 10 mm 50 Ω line between two 0402 pads → one MLIN whose L is the
  pad-edge-to-pad-edge distance and whose W is the drawn width; a line with a 90° corner → MLIN–MBEND–MLIN whose
  lengths sum, with the corner square, to the centre-line length; a chamfered corner reads `Miter`; a 1 mm linear
  ramp from 0.5 to 1.5 mm → MTAPER; a 50 µm sliver is absorbed, never dropped.
- `LineJunctionTests` — a T of three lines → MTEE with the branch identified; a + → MCROSS; arm lengths follow
  R-as5-5.
- `LineTypeChoiceTests` — a line with side ground at 1·H both sides → CPWG with that `G` under `Auto`, MLIN with
  the gap recorded under `Microstrip`; the same line with ground at 5·H → MLIN under `Auto`, gap recorded;
  `CoplanarGapFactor = 6` turns it to CPWG; an inner-layer line between two planes → SLIN; a stripline with
  coplanar ground → TLIN whose Z/Eeff equal the stations' mean and whose reason is recorded.
- `LineRecognitionCountersTests` — a 40 mm line of one width solves **one** cut (the review's shared-cut counter),
  and `Analyze` is called once per recognition.
- `LineRecognitionFieldTests` — `FixtureFact` on `testdata/artwork-boards/`: each board's element counts by type
  equal its `expected.json` within the ranges written there (a field board's expected counts are ranges, not
  exact numbers).
