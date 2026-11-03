# Brief SDL-6 — Simulating a schematic by EM of its own layout

**Series:** [Schematic-driven layout](brief-sdl-0-overview-and-groundwork.md) · **Tag:** `R-sdl6-n`
**Area:** new `src/Core/Devices/External/IParametricModelProvider.cs` (seam), `src/Core/Elaboration/`
(the call, and the instance-binding rule of D7), `src/Core/Netlist/AnalysisDirectiveSchema*.cs` (the
analysis option), new `src/Design/Layout/Em/ParametricEm/` (`EmEvaluation.cs`, `EmCellCache.cs`),
`src/Design/Layout/Em/EmRunService.cs`, `src/Design/Layout/Em/EmCoSimNetlist.cs`, the run hosts
(`src/Ui` Simulate, `src/Cli` run verbs, MCP `run`), `src/Cli` `explain`/`check`, `docs/design/expressions.md` §9
**Depends on:** SDL-4 and SDL-5.

---

## 0. What this adds

A designer can EM-simulate a layout and use its `.sNp` in a schematic, but only at the geometry that was
drawn. When a width, length or gap has to change, the loop is manual: edit the layout, re-run EM, put the
new file in a schematic, tune again.

This brief lets **the schematic the designer drew be simulated by EM of its own layout**, at whatever
values its variables have. The schematic keeps its ports, its analyses, its goals and its VAR block; the
only change is a choice on the analysis: simulate by circuit models, or by EM. Sweeps, tuning and
optimisation (SDL-7) then run in that same schematic. No test bench, no symbol, no second cell.

A second path covers reuse: the same cell placed as one part inside a larger schematic, simulated by EM
at the values that schematic gives it (§3).

## 1. Decisions common to both paths

- **D1 — The schematic is both models.** It is the source of the geometry (SDL-0's planner) and the
  circuit model. Nothing is duplicated and no hand-made SnP cell is needed.
- **D2 — Evaluation never writes the user's files.** At variable values *p*: take the SAVED layout in
  memory, run the planner with *p* applied to the schematic's VARs and parameters (SDL-4's rules apply,
  so the user's hand edits are in every evaluated geometry), then run EM on that in-memory layout through
  `EmRunService.Run` with an `EmLayoutSource` built from it. The `.csch`, `.clay` and `.cem` are not
  touched. Writing values back is SDL-7's Apply, done once, on request.
- **D3 — The cache is keyed on geometry, not on values.** Key = hash of: the flattened shapes on every
  layer the stackup solves, the port labels, the setup's solve-affecting settings, the technology's
  stackup, and the solver id and version. Two values that round to the same DBU share one result; a
  generator change that alters the copper misses the cache by construction. Files:
  `<results>/em-cache/<cell>/<key>.s<N>p` plus an `EmSnpProvenance` sidecar listing the *p* that first
  produced it. Plain files; **Layout ▸ Clear EM Cache**; no automatic eviction in this brief.
- **D4 — A variable that does not reach the copper is reported.** When a Re-parameterised part (SDL-4)
  holds a value set in the layout, a VAR or parameter that would have driven it has no effect on that
  part. The run, `check` and `explain` say so: *"w50 does not reach ML3.W, which is set in the layout."*
  SDL-7 depends on this; an optimiser varying a variable with no effect is the failure it prevents.
- **D5 — Solves are counted before they start.** A run first resolves every EM evaluation at every point
  it will run, then reports: solves needed, already cached, estimated time and memory (the planar dry-run
  figures `EmRunService` already produces). The GUI asks once per run when more than one uncached solve
  is needed. CLI and MCP run without asking, but stop at `--em-max-solves` (default 20) with the count,
  before solving anything.
- **D6 — Frequencies come from the setup.** The EM result is solved on the `.cem`'s frequency plan and
  read as any SnP is. An analysis range outside the plan is a warning, with today's SnP extrapolation
  behaviour unchanged.
- **What makes a schematic EM-capable:** a layout linked to it (SDL-0's records present) and exactly one
  `.cem` whose layout is that layout, or the one named on the analysis or instance when there are
  several. Its EM ports are its schematic Ports (SDL-5). Anything missing is a refusal at `check` time
  naming what is missing; EM never falls back to circuit models silently. SDL-8 extends this to a `.c3d`
  with an embedded setup (3D solvers).

## 2. Path A — simulate this schematic by EM (the main path)

- **`R-sdl6-1` The choice lives on the analysis.** Every analysis gains `model=circuit|em` (default
  `circuit`) and, when there are several setups, `emSetup=`. The Add/Edit Analysis dialog shows it as
  *Simulate by: Circuit models / EM of this schematic's layout*. A run-wide override exists for checking
  a whole design: CLI `--em on|off`, MCP `em`. The schematic's VARs are ordinary globals here, so they
  are already tunables and already overridable by `--set`; nothing about them changes.
- **`R-sdl6-2` What the analysis runs on.** With `model=em`, the circuit the analysis sees is built from
  the EM result:
  - **Every part is in the copper** (the discussion's coupler: MLINs and MTEEs only): the EM N-port, with
    the schematic's Ports as its terminals. An S-parameter analysis then reports exactly the EM
    S-parameters, renormalised to the Ports' `Z` as any SnP is.
  - **Some parts are not copper** (footprinted capacitors, a transistor, a bias source): the EM N-port of
    the copper, with component ports at those parts' pads (`EmComponentPorts`), and each such part's own
    model connected at its pads. `EmCoSimNetlist` already builds this netlist for a board; establish
    whether it serves as is, and stop and report if it needs more than wiring. Any analysis then runs on
    that netlist, harmonic balance included.
  - **A part with no layout at all** (an ideal R between two line ends) cannot be connected to the EM
    result. A refusal listing such parts, never a silent omission.
- **`R-sdl6-3` The results keep their names.** The analysis's result carries its own name (`SP1`), so
  measures, goals and Data Display traces work unchanged whichever model ran. The `DataSet` records
  which model produced it and the cache key, and a Data Display trace from an EM run is labelled so.

## 3. Path B — the cell as one part inside another schematic

The discussion's Idea 3 also asks for the EM'd block to be placed in a larger schematic, with
terminations or other circuitry around it, and optimised there. This section makes a placed component
simulate by EM at its instance's values; SDL-10 completes the use case (the kinds of component, the
parent's layout per instance, several instances, where optimised values are written).

- **`R-sdl6-4` Instance choice.** An instance of an EM-capable cell carries **Model = Circuit | EM**,
  default Circuit, in its properties and in the `.csch`/`.cnl` (`Model=EM`, `EmSetup=`, reserved instance
  parameter names). An analysis's `model=em` and `--em on` also switch every EM-capable instance below it.
- **D7 — A cell's VARs can be set from the instance; the cell's design does not change.** Only declared
  cell parameters can be overridden today, and a cell parameterised through a VAR block should not have
  to be restructured. The binding rule of `expressions.md` §9 extends by one line: for each of the cell's
  own variables that the instance overrides, the override expression is evaluated in the parent's scope
  and replaces that variable's expression in this instance's scope only, exactly as a parameter override
  does. A cell already evaluates its variables per instance, so this needs no new scope; VARs depending
  on an overridden one (`L90-w50/2`) follow through ordinary evaluation.
  - Applies to every hierarchical instance, not only EM cells.
  - The instance's properties list the cell's VARs in a *Variables* group beside its parameters.
  - An overridden cell VAR is a tunable keyed in the existing nested form (`X1:w50`, beside
    `DUT:R3.R`); confirm the spelling cannot be confused with a component parameter key.
  - Overridable VAR names join the cell's interface hash (`CellInterfaceHash`), so a rename fires the
    existing interface-changed warning; an override naming nothing is an error, never ignored.
  - A name that is both a parameter and a VAR of one cell: establish what the elaborator does today,
    state it in `expressions.md`, and make an override of such a name an error naming both.
  - Additive: every existing design elaborates byte-identically (gate: the Hero references unchanged).
- **D8 — A cell with no symbol gets one.** `AutoSymbolGenerator`'s symbol (odd ports left, even right),
  pins numbered by the schematic Ports' `Num`, so EM ports, symbol pins and the parent's connections agree
  by number with no step from the user.
- **D9 — The seam.** Core gains `IParametricModelProvider`, in the shape of
  `IExternalProviderResolver`: given the instance's cell reference and its resolved values, return an SnP
  path or a failure with a sentence. The elaborator calls it where the instance's values are resolved,
  once per sweep point. Core never references Design; each host registers the Design implementation.
  Path A uses the same provider with the schematic itself as the cell. Progress and cancellation ride
  `RunControl` like `em`'s.

## 4. Work

- **`R-sdl6-5`** `EmEvaluation` (D2) and `EmCellCache` (D3), shared by both paths.
- **`R-sdl6-6`** The counting pass and its prompts (D5), in GUI, CLI and MCP.
- **`R-sdl6-7`** `check` (EM-capability, D4) and `explain --analysis` (per analysis and per EM instance:
  model, setup, cached or not at each point) report through the existing validators and walks; no logic
  of their own (CLAUDE.md's rule for both verbs).

## 5. Tests

- **Path A identity:** C2 with `SP1 model=em` gives the same S-parameters as running `em` on C2's layout
  and reading the SnP. The SnP bytes match except the provenance write-timestamp (the `EmCliVerbTests`
  rule).
- **Path A, mixed:** F1 with `model=em`: the capacitor's model sits between its pads' component ports;
  an ideal R with no layout is refused by name.
- **Path A, sweep:** a `parametric_sweep` over C2's `L90` with `model=em` solves each distinct geometry
  once; the `.csch`, `.clay` and `.cem` bytes are unchanged afterwards.
- **Path B identity:** a schematic holding the C2 cell with Model = EM equals Path A on C2 at the same
  values.
- **D7:** two instances of one cell with different `w50` give different results; an unoverridden
  instance equals the cell's own VAR value; an override naming nothing is an error; a renamed VAR fires
  the interface-changed warning; every Hero reference elaborates unchanged.
- **Cache, counting, D4, refusals:** as their decisions state, one test each.
- Most tests use a stub provider; at most one real solve, on a single short MLIN at a coarse mesh.
  Measure it; if it is at or above ~5 s, tag it `Benchmark`. No new timing assertions.

## 6. Docs

New user page `docs/user/src/reference/em-cells.md`: simulating a schematic by EM of its own layout
(first), reusing it as a part (second), the cache, what the solve count means. `docs/design/cli.md`:
`model=`, `--em`, `--em-max-solves`, the `explain` additions. `expressions.md` §9: D7.
`layout-view.md` §9.1: D2-D3.

## 7. Gate

`dotnet test tests/Ui.Tests --no-build --filter "FullyQualifiedName~SchematicSync|FullyQualifiedName~Em"`,
`dotnet test tests/Core.Tests --no-build` (the seam and D7), `tests/Firewall.Tests`. UI: the analysis
dialog's Simulate-by choice, the instance property and the solve-count prompt rendered with
`tools/UiSnapshot`, looked at, PNGs listed. Findings in `src/Design/RESOLVED.md`.
