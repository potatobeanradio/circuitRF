# Brief MTR-3 — the card, the round trip, the docs

**Series:** MTR-0 … MTR-3 · **Tag:** `R-mtr3-<n>` · **Depends on:** MTR-2
**Area:** `AnalysisDirectiveSchema`, `CnlReader`/`CnlWriter`, `AnalysisSerialization`, `DuplicateAnalysisCommand`,
`src/Cli/Tran.cs`, `src/Cli/Check.cs`, `TranBodyViewModel`/`TranBodyView`, `docs/user/src/reference/transient.md`,
`docs/user/src/reference/components.md`.

## The gap

`Store`, `StoreHarm` and `MeasureFrom` exist in the engine (MTR-2) but nowhere a user writes them. The two keys on the
source (`RecordStart`, `Ramp`, MTR-1) are undocumented.

## Requirements

**R-mtr3-1 — The keys survive everything.**
- The three tran keys are in the directive schema with their defaults.
- They survive `.cnl` write and read, `.csch` save and load, Duplicate, and the CLI's override of a tran directive.
- The clone-parity test covers them.

**R-mtr3-2 — The card.**
- The card offers Store (`auto`, `baseband`, `rf`) as a picker with its one-line description. `StoreHarm` and
  `MeasureFrom` sit under More.
- When the store would be RF and the schematic has a `MOD_Source`, a one-line warning reads: "RF rate stores every
  carrier cycle: about N points per net", where N is Stop × 20 × the highest `Fc`.
- The row's controls never move when the warning shows or hides.
- A UiSnapshot of the card in both states, looked at.

**R-mtr3-3 — `check`.** `check.tran.keys` reports a malformed `Store`, `StoreHarm` or `MeasureFrom` through the run's own
resolution.

**R-mtr3-4 — The pages.**
- The Transient page gains a "Modulated sources" section. It covers when to use it instead of envelope, the window, the
  three keys, what baseband results are, and the cost of RF storage.
- The Components page's `MOD_Source` entry gains `RecordStart` and `Ramp`.

## Gates

1. Round trip: a tran directive with every new key set survives `.cnl` write and read, `.csch` save and load, and
   Duplicate, unchanged.
2. The card's PNGs, with and without the warning.
