# Brief — what an agent still cannot do: the design features behind headless authoring

**Tag:** `R-aa-n` · **Phases:** AA-1 … AA-6, independent of one another, each with its own go/no-go
**Precedent:** `serve` and the automation series (`docs/design/cli.md` §11), the reference topics
(`src/Cli/Reference.cs`, `src/Cli/DocumentSchema.cs`), the MIM series (`tests/Ui.Tests/Em/MimCapacitorTests.cs`),
the kit PCells in `examples/PDK PCells/pcell-kit`, LVS (`src/Design/Layout/Lvs`, `cli.md` §19)
**Holds:** nothing here is started until the owner has answered that phase's decisions (§each, "Owner decides").

---

## 0. Why this brief exists

On 2026-10-06 an agent was given a filter specification and the GaAs technology that ships with circuitRF, and
**nothing but the circuitrf MCP tools** — no shell, no web, no source tree. It produced every deliverable in
13 minutes (a synthesised prototype, a realisable version with tolerance corners, a `.csch`, a DRC-clean layout, a
checked `.cem`, GDSII and a report) and kept a friction log of 16 entries. Its report on the run is the evidence for
this brief.

The friction items that were DEFECTS or MISSING REFERENCE were fixed directly in the same session: the `.cnl`
technology binding, skipped-line warnings, the `.csch` validation and reference topic, the shipped
technologies/materials topics, the component index, band reductions in measurements, `plot y=db`, DRC locations,
the `.cem` Auto override, the mesh budget in `--summary`, and `serve --print-config`. What is left is below: each is a
FEATURE, each needs a design decision only the owner can make, and each is what stood between the agent's design and
one a person would tape out.

What the agent computed BY HAND because circuitRF could not, in its own words (paraphrased): MIM capacitance per area
from the stackup; prototype g-values to L and C; microstrip Z0 and εeff to size lines; transmission-zero arm lengths;
every rectangle's coordinates in DBU; worst-case spec values read off cubes; each tuning iteration, by editing a
variable and re-running. The die came out 7.8 mm long because straight high-impedance lines were the only inductor
circuitRF could simulate on that process.

---

## AA-1 — MMIC passives as built-in components and PCells

**Evidence.** The shipped GaAs technology states a MIM module (MIM Metal, MIM Dielectric, MIM Via) and a Resistor
layer, and the planar extractor already handles MIM structures (MIM-2…MIM-7). But there is **no circuit component**
for a MIM capacitor, a spiral or loop inductor, a thin-film resistor or an airbridge crossover, and no BUILT-IN PCell
that draws one. The spiral and MIM PCells that exist are a kit example (`examples/PDK PCells/pcell-kit`), which an
agent working in its own folder never sees.

**Scope.**
- Circuit components, each bound to the technology the way MLIN is (`MicrostripSubstrateInjection`, and now
  `CnlTechnologyBinding` for a `.cnl`): `MIMCAP` (W, L → C from the MIM dielectric's εr and thickness, plus plate
  fringing; ESR from the plate metals), `SPIRAL` (turns, width, spacing, inner diameter → L, R, C to ground),
  `TFR` (W, L → R from the Resistor layer's sheet resistance), `AIRBRIDGE` (span → series L and C to the line under it).
- Built-in PCells in `src/Design/Layout/PCells` that draw each one from the same parameters, so schematic → layout
  generation works on an MMIC as it does on a board.
- Catalogue entries (`reference components`) with the validity range of each model stated.

**Owner decides.**
1. **The spiral model.** A closed-form estimate (modified Wheeler / current-sheet for L, with stated validity) is
   cheap and approximate. A table extracted from planar EM per technology is accurate and costs an EM run per
   technology. A hybrid (closed form, flagged as an estimate, plus an "extract this spiral" command that writes an SnP)
   is the recommendation.
2. **Component tokens.** `MIMCAP`, `SPIRAL`, `TFR`, `AIRBRIDGE` are proposed; they must not reuse any vendor's token.
3. **Whether the PDK-PCells kit example's generators are promoted** to built-ins, or written fresh in C# beside MLIN.

**Gate.** Each model against an independent reference: the parallel-plate-plus-fringe formula for MIM, a planar EM
extraction of the same spiral on the shipped stack for SPIRAL (within a stated tolerance), sheet × squares for TFR.

---

## AA-2 — LVS that recognises MMIC artwork

**Evidence.** `lvs` on the agent's design reported 13 errors, all "no counterpart in the layout": "0 devices, 0 nets".
The shipped GaAs technology has no device-recognition deck, so plain artwork (an MLIN rectangle, a MIM plate stack, a
backside via) is copper with no device in it.

**Scope.** A recognition deck in the shipped GaAs technology for MLIN, MIMCAP, TFR, VIAGND and (after AA-1) SPIRAL,
with terminal maps; and documentation of what the `Component`/`Pin` fields on a drawn shape mean, which the agent
found but could not interpret.

**Owner decides.** Whether recognition is by layer combination only (MIM Metal over MIM Dielectric over Metal1 is a
capacitor) or also by the PCell origin a generated shape records. Recognition by layers works on imported GDSII too and
is the recommendation.

**Gate.** The agent's layout (rebuilt in code on the shipped technology, as `MimCapacitorTests` builds its fixtures)
passes `lvs` against its schematic, and swapping two nets fails it naming both.

---

## AA-3 — A line calculator

**Evidence.** The agent sized every line from Hammerstad by hand, and its circuit model and the drawn line then
disagreed by 6% (67.8 Ω from the model, 64.0 Ω from `impedance`). `impedance --survey` exists but needs a line that is
already drawn.

**Scope.** Synthesis and analysis on a technology layer with nothing drawn: W ↔ Z0, and εeff, loss per length and
guided wavelength at a frequency, for microstrip (and coplanar where the technology has a coplanar ground). Two
answers side by side: the circuit model's (what a run will use) and the quasi-static cross-section's (what
`impedance` would report), so a disagreement is seen before anything is drawn.

**Owner decides.** The spelling: a mode of `impedance` (`impedance --tech <ctech> --layer Metal1 --z0 50`) or of
`explain`. A mode of `impedance` is the recommendation, since that verb already owns the cross-section.

**Gate.** The model answer equals what an `MLIN` of that width elaborates to on the same layer, bit for bit; the
cross-section answer equals `impedance` on a drawn line of that width.

---

## AA-4 — Tuning and optimisation

> **Superseded (2026-10-07)** by the tuning and optimization series, `brief-tuneopt-0-overview.md`. Both owner decisions
> below are answered there: optimization is in scope (PRD v3.1 §5.1), and the headless optimizer reports values and
> writes nothing back to the design's values (a named preset is the one opt-in write). This section is kept as the
> evidence that motivated it; build from the new series, not from here.

**Evidence.** `set` now works on every circuit analysis, so a sweep of what-ifs is scriptable, but the agent still ran each
iteration and read the worst value itself. With the band reductions now in measurements, a goal can be STATED; nothing
yet searches for the values that meet it.

**Scope.** An `optimize` directive on the TestBench naming variables with ranges and goals written as measurements
(`goal IL_worst <= 1`), a derivative-free method to start (Nelder–Mead, or a bounded pattern search), and a result that
reports the best values, every goal's margin, and the trajectory. The optimiser writes NOTHING back to the design: it
reports the values, and the caller (or the GUI, later) applies them. A headless verb and the MCP `run` tool reach it as
`analysis=optimize`.

**Owner decides.**
1. Whether this is in scope for v1 at all (`docs/PRD.md`'s non-goals should be checked first).
2. Writing back: report-only (recommended — an agent applies values by writing the file, which is the contract) or an
   opt-in write.

**Gate.** A two-variable problem with a known optimum (a matched L-section) converges to it within tolerance from a
stated poor start, and an infeasible goal is reported as infeasible rather than as the nearest miss.

---

## AA-5 — EM runs an agent can afford: cost before, and jobs during

**Evidence.** The outside agent's final 3D run took 6 h 58 min for 3.5 M unknowns. A blocking MCP tool call for that
long is fragile (one dropped connection loses it), and the only cost information before launching is the planar
unknown count in `check`'s notes.

**Scope.**
- **Cost before.** `explain` on a `.cem` (planar and 3D) reports the expected unknowns, memory and a time estimate
  from a stated, measured model, with the estimate's basis named. A 3D estimate comes from a mesh-size pass, not a
  solve.
- **Jobs during.** `run` gains a detached mode: it returns a job id at once; `jobs status <id>` reports progress and
  elapsed time; `jobs cancel <id>` stops it; the result lands where an attached run would write it. The MCP server
  exposes the same three.
- **Convergence as one operation.** A mesh-convergence study (the same setup at two refinements, reporting the
  largest |ΔS| and where) as a single command, because the outside agent built that comparison itself and it is the
  question every EM result needs answered.

**Owner decides.** Where a detached job lives when the server process exits (a per-workspace job folder that a later
server can re-attach to is the recommendation), and whether a job may outlive the client that started it.

**Gate.** The estimate is within a stated factor of the measured cost on the shipped 3D examples; a detached run
cancelled midway writes nothing; a re-attached job reports the same status as the original.

---

## AA-6 — A drawn schematic from a netlist

**Evidence.** The agent wrote its `.csch` by trial and error, measuring pin offsets from rendered pixels. The new
`schematic` reference topic and the pin offsets in `reference components` make that possible, but a person reviewing an
agent's design still wants a readable drawing, and placing symbols and routing wires is not what an agent is good at.

**Scope.** `netlist --to-schematic` (spelling to decide): a `.cnl` in, a `.csch` out, with symbols placed left to right
along the signal path, shunt elements dropped below, ground symbols, wires routed orthogonally and net labels where a
wire would cross. The output must extract back to the same netlist — that round trip is the gate.

**Owner decides.** Whether auto-placement is worth its cost for v1, or whether the reference topic is enough for now.

**Gate.** For every committed example `.cnl`, the generated `.csch` extracts to the same instances, nets and values.
