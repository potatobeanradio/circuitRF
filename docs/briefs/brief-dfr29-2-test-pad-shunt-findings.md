# Brief DFR29-2 — The impedance check flags a test pad that loads the line

**Series:** designer feedback round 29 (2026-10-27). · **Tag:** `R-dfr29-2-<n>`
**Area:** `src/Design/Layout/Em/TraceImpedanceAnalysis.Pads.cs` (`PadShunts`, `PadShuntAt`), `TraceImpedanceAnalysis.cs`
(`TraceIssueKind`, `TraceRun.Issues`, the via-transition skip, ~l. 81–100 and ~l. 820), the impedance panel and canvas
markers in `src/Ui/Layout/` (find them from `TraceImpedanceReport` consumers), `src/Cli/ImpedanceLine.cs`;
`docs/user/src/reference/` (the impedance-check page). Findings go in `src/Design/RESOLVED.md`.
**Tests:** `tests/Ui.Tests/Layout/TestPadRound26Tests.cs` (the pad-shunt tests today) and the impedance-check test classes
beside it. The formulas are not touched: `TestPadGeometry` (`src/Core/Devices/TestPadFormulas.cs`) stays the one
implementation.

---

## 0. Why

The designer asked that the impedance check catch the via-plus-test-pad arrangement that cost the round-26 board its
6 dB: a test point read as a capacitor to the next ground layer, from the pad's diameter and the known stackup, in
series with the via's inductance — an LC — with no EM run. He notes it matters on the 10-layer board, where the next
plane is close, and not on the 4-layer one.

**Most of this exists (DFR26-7 R-dfr26-7-5, pushed).** `PadShuntAt` finds a round pad at a trace's END — directly, or
through a via to another layer — resolves the planes it faces with the TESTPAD's own stackup walk, and reports the pad's
capacitance (Kirchhoff's disc over a plane), the via's inductance, the barrel's and unused pads' capacitance, and the LC
resonance. But it is only a **note** on the trace: it never warns, never fails, has no marker on the canvas, and so is
easy to miss in a long report. It also sees only a `CircleShape` at a trace's end.

## 1. What to do

### R-dfr29-2-1 — A pad shunt is a finding when it matters at the frequency asked

Add `TraceIssueKind.PadShunt`. Judge each `TracePadShunt` at the check's `MaxFrequencyHz` against the trace's own Z0:

- the shunt's return loss on the line, `|S11| = ωCZ0 / √(4 + (ωCZ0)²)` with C the total (pad + barrel + unused pads) and the
  via's L in series when there is one (use the series LC's admittance, not C alone);
- the LC resonance, when there is a via.

**Owner decision D1 (thresholds):** recommended — **warning** when |S11| at fmax is above −20 dB or the resonance is below
2 × fmax; **fail** when |S11| is above −10 dB or the resonance falls inside the band (below fmax). The finding's sentence
is the note's, plus the verdict: *"… 0.92 pF to 'GND2', the via 0.41 nH, resonating at 8.2 GHz: |S11| −1.1 dB at 6 GHz."*
With no `MaxFrequencyHz` set, the shunt stays a note (there is no frequency to judge it at), and the note says so.
Acceptances (`TraceImpedanceAcceptance`) cover it as they cover any finding.

### R-dfr29-2-2 — See it on the canvas

Each pad shunt gets a marker at the pad (the issue's `X0..Y1` box = the pad's bounds), coloured by severity like the
other findings, and is listed in the panel's findings with the rest. Clicking it selects the pad.

### R-dfr29-2-3 — Find the pads that are missed today

Measure first on the round-26 TP149 workspace and on two small fixtures, and report what is missed before adding any:

- a pad that is not a `CircleShape`: a rectangle or rounded rectangle wider than `PadOverLand` × the trace, or a polygon a
  circle fits (`ImageTrace.CircleOf`-style fit), at the trace's end;
- a TESTPAD instance's pad (its PCell geometry, as flattened) — it carries its own model, so report its shunt from the
  instance's own parameters and say which;
- a pad ON the line rather than at an end: the trace runs through a disc much wider than itself;
- a pad at the end of a short stub teed off the line: the stub is its own trace and is already covered — confirm with a
  fixture, add nothing if it is.

Keep `Isolated`'s rule (no other copper joined to the pad); it is what keeps a component's land from reading as a test
point.

### R-dfr29-2-4 — The via keep-out does not hide it

`ViaTransitionMicrons` (400 µm by default) stops Z0 being judged next to a via. The pad shunt is judged at the pad, not
along the trace, so it must be reported even when the whole stub lies inside that distance. Add a fixture: a 300 µm stub
from the line to a via and a 1 mm pad on the far side.

### R-dfr29-2-5 — CLI and MCP

`circuitrf impedance` prints the shunt findings with the others and carries them in `--json` (`kind: "pad-shunt"`, C, L,
resonance, |S11| at fmax, severity). The MCP `impedance` tool returns the same.

## 2. Gates

1. A 10-layer fixture (the shipped `pcb-10layer_FR-4_1.7mm_1oz`) with a 1 mm test pad through a via on the top layer,
   GND on L2: the finding fails at 6 GHz. The same pad on the 4-layer FR-4 62 mil technology, plane further away: a
   warning or nothing, per D1.
2. No `MaxFrequencyHz`: a note, no finding, and the note says why.
3. R-dfr29-2-4's stub inside the via keep-out is reported.
4. Each new pad shape R-dfr29-2-3 adds has its fixture; a component land joined to other copper never reads as a pad.
5. TP149: every test point the round-26 report named is flagged, at the C the report gave (± the formula's own figure —
   the numbers must not move, only their severity).
6. CLI `--json` and the MCP result carry the finding.

## 3. Not in this brief

- New formulas or an EM-backed figure — the TESTPAD's estimate stays the one estimate.
- Checking pads on lines with no Z0 target (a DC net): the check only runs on what it already analyses.
