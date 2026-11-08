# DE-4 — The `trl` test-bench analysis

Read `brief-de-0-overview.md` (D8, D9) and `brief-de-1-network-math.md` first.

**What.** On a test bench holding three SnP blocks with the user's measured Thru, Reflect and Line files:

```
analysis CAL type=trl thru=THRU reflect=REFLECT line=LINE reflectType=short fixtureA="fixtures/fixture_A.s2p" fixtureB="fixtures/fixture_B.s2p"
```

Simulate (or `circuitrf trl`, or MCP `run analysis=trl`) writes the two fixture files and returns a DataSet to plot. The
files go straight into two Deembed blocks.

## The directive

| Key | Required | Meaning |
|---|---|---|
| `thru`, `reflect`, `line` | yes | Instance names of SnP blocks on this bench. A name that is no SnP, or an SnP whose file is not a 2-port, is refused by name |
| `reflectType` | yes | `open` or `short`: which side of the Smith chart the reflect is on. Its exact value comes out of the solve |
| `fixtureA`, `fixtureB` | no | Output paths, resolved exactly as an SnP's `File` (D8). Default `<analysis name>_A.s2p` and `<analysis name>_B.s2p` in the bench's schematic folder |

It reads the SnP blocks' **resolved** `File` parameters from the elaborated bench, so a variable or a `--set` reaches it as it
would reach a run. It needs no circuit solve. The three blocks are usually between port pairs, so an `sp` analysis on the same
bench shows the raw standards, but that is not required.

## The run

- `TrlCalibration.Solve` (DE-1) on the three files. Its refusals become the analysis's: grid mismatch, non-invertible
  points, a file that is not a 2-port.
- Write A and B as Touchstone 1.1 `.s2p`, RI, in GHz, at the Thru's frequencies, referenced to the Thru file's Z0. Add a
  `!` header line naming the three source files by their stored (relative) spelling, and saying the reference impedance is
  the Line's. Create the folder if it is missing. Through the existing Touchstone writer.
- **DataSet**: `SA` and `SB` (complex, `freq × i × j`, 1-based ports as an S cube is), `LineDeg` (real, unwrapped), `Gamma`
  (complex) and `Usable` (real 0/1). Measurements on the bench can read them as they read any cube, and the Data Display
  plots them.
- **Warning**, once per run: the count and the frequency span of the points whose `LineDeg mod 180` falls outside
  [20°, 160°], with what to do about it: another line length, or narrow the band. It is a warning, not a refusal, because the
  user may only care about part of the band.
- Notes: the two paths written, the reference plane at the Thru's middle, and the reference impedance.

## Everywhere an analysis type is threaded through

Follow `FieldsAnalysis`, the most recent type, through: `Analysis.cs` (`TrlAnalysis`), `AnalysisDirectiveSchema`,
`CnlReader`/`CnlWriter`, `AnalysisSerialization` (`.csch` tag `"trl"`), `CircuitEvaluation` (describe and dispatch),
`DuplicateAnalysisCommand`, `AnalysisRowViewModel` (tag `TRL`; summary "THRU / REFLECT / LINE → fixture_A, fixture_B"),
`AnalysisEditorViewModel` plus a body view model and view, `Explain`'s analysis-to-verb table, `ToolCatalog`, and
`AnalysisClonePropertyParityTests`, which will fail until `TrlAnalysis` is cloned.

- **Editor body**: three combo boxes listing the bench's SnP instances, the reflect type, and the two output paths with a
  Browse… (save) button each. UiSnapshot PNG, looked at.
- **CLI**: a `trl` verb on the five-step anatomy in `docs/design/cli.md` (read it first). `-o` is not meaningful here,
  because the outputs are two files the directive names. `--fixture-a`/`--fixture-b` override them, by replacing the
  directive (the CLAUDE.md rule for overrides). Exit 0 on success, even with the usable-band warning, and 1 on a refusal.
  Add it to `CliEntry`, `cli.md` and the verb list in CLAUDE.md's CLI paragraph.
- **MCP**: `run analysis=trl`, and the `reference analyses` text comes from the schema.

## Tests

- `TrlAnalysisTests` (`Ui.Tests` or wherever `CircuitEvaluation` is exercised): a bench whose three SnPs hold synthetic
  standards written by the test (DE-1's construction). Then the GUI's run path writes `fixture_A.s2p`/`fixture_B.s2p`, and a
  second bench with `Deembed(A) — SnP(measured) — Deembed(B)` returns the DUT to ≤ 1e-9. Touchstone text precision is the
  floor: write enough digits, and measure the floor rather than guess it.
- The CLI verb as a process, byte for byte against the in-process run, on the files written.
- `.csch` round trip of a `TrlAnalysis` and a `.cnl` round trip of the directive.
- Refusals: `thru=` naming a resistor, and a 4-port file.

## Docs

A `trl` section in the analyses reference (generated from the schema, plus prose): what TRL needs from the standards, the
usable band, the reference plane and impedance, the fixture orientation (D9), and the two-step workflow (calibrate, then
Deembed). `cli.md` gets the verb.
