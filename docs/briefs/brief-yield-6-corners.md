# Brief YA-6 — Corner analysis, Monte Carlo per corner, statistical corners

**Series:** `brief-yield-0-overview.md` (D9, D10, D12) · **Tag:** `R-ya6-<m>`
**Depends on:** YA-4, YA-5 (YA-3 for kit-axis corners and mismatch-per-corner)
**Area:** `src/Design/Statistics/` (`CornerRun`, `CornerGenerator`, `StatisticalCorner`),
`src/Ui/Schematic/WorkspaceCorners.cs` (read-only use; its binding function must be callable from `src/Design` —
move the framework-free part below the firewall if it is not already reachable, as the `src/Design` extractions
did), `src/Design/Schematic/NetExtractor.cs`, `src/Cli/Yield.cs`, `docs/design/yield.md`

---

## 1. Goal

A designer (or an agent) checks a design at every corner that matters — kit process corners, temperature, supply —
in one run with one table of results; runs mismatch Monte Carlo at each; and turns a Monte Carlo run's worst trials
into named corners that later tuning and optimization can be held to.

## 2. Requirements

**R-ya6-1 — A corner run.** `CornerRun` evaluates the nominal design once per **enabled** corner (D10): each
corner's kit axis selections resolved through the existing `WorkspaceCorners.BindingsFor` path (the one Simulate
uses for `CornerSelections` — a corner's selection **replaces** the schematic's own for that axis and leaves its other
axes as the schematic has them), its `temp` bound to the elaborator's ambient global (`Temperature.AmbientGlobalName`),
its VAR and tunable values applied with `TunableOverrides.Apply` (so `--set` interacts with them exactly as with
tuned values: the same key set both ways is a refusal naming both). Corners are evaluated through YA-2's batch door,
in parallel. A **statistical** corner applies its replayed z-vector (R-ya6-4).

**R-ya6-2 — Results.** One DataSet with an outer **`corner`** axis whose labels are the corner names
(`DataCube.Axis.Labels`), the nominal as corner `nominal` first; per corner per goal: pass and margin; per corner:
status. The report is a **corner × goal table** of margins with failures marked, the worst corner per goal, and
every corner that did not evaluate with its reason. Written to `<schematic>.corners.npy` (Simulate's `run.npy` and
the Monte Carlo file are untouched).

**R-ya6-3 — Monte Carlo per corner.** `statistics corners=all|<names>` (YA-1) runs a YA-4 Monte Carlo/yield **at
each listed corner**: the corner's bindings fixed, the stat entries and kit **mismatch** drawing per trial, and kit
**process** draws off (a process corner and a process draw are two answers to one question; holding both would
double-count — `process=1` with corners is a `check` warning saying so). The DataSet has `corner` outside `trial`;
yield and its interval are reported per corner and for the worst corner.

**R-ya6-4 — Statistical corners.** `corner <Name> trial=<n> seed=<s> sampling=<m> trials=<N>` replays trial n's
z-vector (YA-2 R-ya2-4) against the **current** nominal: percent spreads follow it, absolute ones do not, correlated
entries keep their correlation. Streams that no longer exist (a renamed instance, a deleted entry) replay with a
warning naming each; new stat entries since the run take their nominal. `StatisticalCorner.FromRun(result, goal,
k)` proposes the k worst trials per goal as corner definitions — the panel's and the CLI's "save as corner" use it.

**R-ya6-5 — The generator.** `CornerGenerator.CrossProduct(axes × options, temps, VAR values)` returns explicit
`CornerDefinition`s named from their parts (`tt_25`, `ss_85_Vdd3.0`), deduplicated, capped at 256 with a refusal
above that naming the count. It writes lines; it is not a grammar (D10).

**R-ya6-6 — CLI.** `circuitRF yield corners <path>` (YA-5's verb): runs every enabled corner, prints the corner ×
goal margin table, exits 3 if any goal fails at any corner (D13). `--corners a,b` narrows; `--mc` runs R-ya6-3;
`--generate "axis=opt1,opt2;temp=-40,25,85;Vdd=3.0,3.6"` prints the generated corner lines (it writes nothing — an
agent pastes them into the file, the format being the contract) and `--write` appends them to a `.csch` after a
history checkpoint. `yield estimate --save-corner <name> --trial n` (YA-5) is the statistical-corner write. MCP:
`run analysis=corners`, and `generate` as an option returning the lines.

**R-ya6-7 — explain.** `explain --analysis` lists each corner with its resolved bindings in base SI with unit, and
says for each kit axis whether the corner sets it or inherits the schematic's selection.

## 3. Not in this phase
Optimizing or tuning against corners (YA-7). The panel (YA-10).

## 4. Gates (minimal tests, run only these classes)
- `CornerRunTests` — a divider with `temp`-dependent resistors (a tempco parameter): corner margins equal three
  Simulate runs with the same values typed; a corner overriding one kit axis leaves the schematic's other axis
  selection in force (on a minimal synthetic kit fixture with two section files, committed as test data — synthetic,
  not a vendor's).
- `CornerMonteCarloTests` — per-corner yields are each identical to a YA-4 run with that corner's bindings typed.
- `StatisticalCornerTests` — replaying trial n reproduces trial n's values exactly; after moving a nominal, a
  percent-spread entry moves with it and an absolute one does not; a renamed instance warns.
- `CornerGeneratorTests` — 2 options × 3 temps × 2 VAR values = 12 named, unique corners; 300 is refused.
- `CornerCliTests` — the verb exits 3 naming the failing corner and goal; `--generate` writes nothing.
