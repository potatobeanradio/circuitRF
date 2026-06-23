# circuitRF — Net Extraction & Run (Phase 6e) Design

**Status:** Draft (rev 1) for review · **Date:** 2026-06-09 · **Phase:** 6e

Consolidates the **net-extraction + run** spec (previously scattered across `grid-and-connectivity.md` §3/§5,
`project-file-formats.md` "Port-index conventions" / "netlist.cnl on simulate", `ui-design.md` §5/§5.1) and
lays out the build order. This is the phase that makes a drawn schematic **actually simulate end-to-end**:
`.csch` → extract nets → emit `netlist.cnl` → the existing engine chain → `DataSet` → results.

**The one-line contract:** *extraction produces the same design model a hand-authored `.cnl` produces.* That
equality is the correctness oracle for the whole phase.

Companions: `grid-and-connectivity.md` (the on-`P` connectivity foundation extraction relies on),
`project-file-formats.md` (`.cnl` format, port-index conventions, `netlist.cnl`-on-simulate), `data-model.md`
(the engine Design model: Cell/Instance/TestBench/Variable/Analysis), `scratch-and-save-lifecycle.md` §1.3
(scratch sim writes `netlist.cnl` to the scratch working dir), `src/Ui/CLAUDE.md`, `src/Core/*/CLAUDE.md`.

---

## 1. The boundary (what exists, what 6e adds)

The engine side is **done** and unchanged by 6e:
```
  .cnl ──CnlReader──▶ Design model (Library/Cell/TestBench/Instance/Variable/Analysis)
        ──Elaborator──▶ ElaboratedNetlist (+ NodeMap)
        ──{SParameterEngine | NonlinearDcEngine | HbEngine | LoadpullEngine}──▶ DataSet
```
- `src/Core/Netlist/CnlReader.cs` parses `.cnl` text. Line grammar (the producer target):
  - `; comment`
  - `name = expr [unit]` — global variable
  - `Type:Inst  net1 net2 …  param=val [unit] …` — primitive component (nets **positional**, in terminal
    order; the engine infers terminal index from position)
  - `Cell:Inst  net1 net2 …  param=val …` — cell instance
  - `analysis …` / `measure …` — directives, stored (mostly) verbatim
- `src/Core/Elaboration/Elaborator.cs` + `NodeMap.cs` map net names → node indices (ground = node 0).
- `src/Cli/Program.cs` already runs `.cnl → DataSet` headless.

**6e adds exactly one new thing on the engine path: a net extractor** that turns a `SchematicEditModel` into
that `.cnl` text (or directly into the Design model). Everything downstream is reused.

**The connectivity geometry already exists** in `src/Ui/Schematic/EditableSchematic.cs`
(`ComputeConnectivityGeometry`): on-`P` vertex hashing, T-junction detection, dot-gated 4-way crossings,
port-position emission (built-in via `SymbolPortDefs`, cell-ref via resolved `.csym` pins). 6e **consumes**
this — it does not re-derive connectivity. The embedded "6e extraction note" comments in that file are the
normative spec for how the union must treat each case.

---

## 2. Net extraction — the algorithm

Extraction is a **headless, deterministic, framework-free** pass: `SchematicEditModel → Design model / .cnl`.

### 2.1 Build the node sets (union-find over on-`P` coincidence)
Every electrical connection point is on `P` exactly (`grid-and-connectivity.md` R1–R7), so connection is
**integer-cell equality**, not tolerance. Union into nets:
1. **Seed** a union-find with every distinct on-`P` connection point (component pins-in-world, wire vertices).
2. **Wires union their vertices:** every vertex of a wire is one node with the others on that wire's path
   (a wire is a single net along its polyline).
3. **Coincident points union:** points sharing a `P` cell are the same node (pins meeting pins, pins on wire
   vertices, wire-end on wire-end).
4. **T-junctions union (auto):** a wire endpoint on another wire's segment **interior** splits that wire and
   unions all three incident ends (the `ComputeConnectivityGeometry` auto-dot rule — a vertex + 3 incident
   ⇒ junction). This is automatic (no user dot needed).
5. **4-way crossings union ONLY with a user dot:** two wire interiors crossing with no vertex is ambiguous —
   it unions **iff** a user `EditableDot` sits there (the dot-gated `IsCrossingAtDot` rule). No dot ⇒ the
   wires pass over each other as two nets.
6. **Net labels union by name (named connectivity).** A net label names the net of the wire it sits on, **and
   all nets whose labels share the same name are the SAME net** — they union, even across disjoint wires with
   no physical connection. This is *connectivity by name*: dropping the label `vdd` on two separate wires
   makes them one net `vdd` (the standard EDA "net label / global net" behavior). So labels participate in
   the union-find: after the geometric unions (steps 2–5), **union all nets that carry a label, grouped by
   (case-sensitive) label name.** A net may carry multiple labels of the **same** name (redundant, fine);
   two **different** label names on the same physical net is a conflict — surface it to Messages (§2.2) rather
   than silently picking one.

Reuse `ComputeConnectivityGeometry` for steps 2–5 (it already computes the vertex hash, auto-dots, and the
crossing predicate as the single source). Extraction's union-find consumes those outputs, then applies the
label-name union (step 6) on top.

### 2.2 Name the nets
- **Ground** (any `Ground` component pin's net) → net **0** / `gnd` (the engine's reference node).
- **Net-label-named nets** → the label text. All nets sharing a label name were already unioned into one net
  (§2.1.6), so this is one name for one net. If a single physical net carries **two different** label names
  (a naming conflict), surface it to Messages and pick deterministically (e.g. first by stable order) so the
  netlist is still emitted — don't fail the extraction, but don't hide the conflict.
- **Port-named nets** (a `Port`/`Term` component) → the port's net name as the design expects.
- **All other nets** → a deterministic auto-name (e.g. `n1`, `n2`, … assigned in a **stable order** — by
  lowest component/pin encountered — so re-extraction of an unchanged schematic yields identical names; this
  matters for the oracle and for clean `netlist.cnl` diffs).

### 2.3 Emit components in terminal order (the bug-prone seam)
For each placed component, emit one `.cnl` line: `Type:Inst  <nets in terminal order>  <params>`.
- **Walk terminals in the symbol's defined order** (1-based user-facing) and emit each terminal's net **in
  that order** — the engine infers terminal index from **position**, so order *is* the contract
  (`project-file-formats.md` "Port-index conventions"). **Do not** shift a 1-based symbol terminal into a
  0-based slot; **do not** transpose terminals. A 3-terminal FET (d/g/s) is where a transposition silently
  makes a wrong-but-plausible netlist — the oracle (§4) catches it.
- **Type** = the engine component type (from `ComponentTypeRegistry` / the cell's model); **Inst** = the
  instance name; **params** = the instance's parameter expressions (strings, as authored — the engine
  resolves them) with units.
- **Disabled components** (`DisableState.Open`/`Short`, §7.2) emit per their disable semantics (Open = omit /
  open the terminals; Short = short the terminals into one net) — honor the existing disable model.
- **Variables** (design `Var`s) → `name = expr` lines; **analyses/measurements** → `analysis …`/`measure …`
  directives. (Where these live in the schematic — directive components vs. testbench metadata — follow the
  data-model; for v1 a TestBench cell's analyses/measurements come from its schematic's directive content.)

### 2.4 Output
Two equivalent outputs (the extractor can produce either; both feed the engine):
- **`.cnl` text** written to disk (`netlist.cnl` — §3), human-inspectable + CLI-runnable; OR
- the **Design model** directly (skip the text round-trip for the in-app run).
For v1, **write `netlist.cnl` and feed the engine from the same extraction** (the text is the inspectable
artifact and the oracle target). The extractor's Design-model output and `CnlReader`-of-its-own-text must
agree (a cheap internal consistency check).

---

## 3. `netlist.cnl` on simulate (where it lands)

Per `project-file-formats.md`:
- On simulate, extraction writes **one** `netlist.cnl`, **overwritten each run** (latest run's netlist).
- **Destination:** the **workspace root** for a materialized workspace; the **scratch working dir** (the
  recovery-cache session dir, `scratch-and-save-lifecycle.md` §1.3) for a **scratch** schematic with no
  workspace. (Scratch sim is a first-class run — it produces a netlist like any other; it just lands in the
  scratch dir.)
- **Header comment** records provenance: `; netlist.cnl — generated from TestBench "<name>" at <ISO-8601>`.
- It is a **generated scratch artifact**, never part of the saved project (the `.csch` is the source of
  truth).

### 3.1 File-path resolution base — `Grid` / `OutputGrid` / SnP `File` (Loadpull UI 07)

Relative file paths in the emitted `netlist.cnl` (the loadpull `Grid=` / pursuit `OutputGrid=`, and the
SnP `File=`) **resolve against the directory that `netlist.cnl` is written to** — the workspace root
(materialized) or the scratch session dir. `CnlReader.ReadFile` sets `_sourceDirectory` to the netlist's
own directory, and the loadpull/pursuit/SnP parsers resolve relative paths against it (`Path.Combine` →
absolute); absolute paths pass through verbatim.

**Convention (one base, no surprises):** the GUI file pickers store paths **relative to the workspace
root** — the SAME base — via `SnpPathPolicy.ToStored(absolutePath, workspaceRoot)`. The Loadpull/Pursuit
authoring bodies (`Lp/LppBodyViewModel`) take the workspace root (threaded from `SchematicViewModel.
WorkspaceRoot`) and use it as the `ToStored` base, exactly like the SnP `File` picker
(`ParameterEditorViewModel`). Because the picker base = the netlist directory = the reader's resolution
base, a picked `.gam` round-trips correctly **regardless of where the schematic itself lives** (a
cell-homed schematic sits under `<ws>/<cell>/schematic/`, but its `.gam` paths are still relative to the
workspace root). Storing relative to the schematic dir instead would lose directory levels at run time —
the bug fixed in the Loadpull UI 06 follow-up. When no workspace is open (scratch), the picker keeps the
absolute path, which also resolves unchanged.

---

## 4. The extraction oracle (the correctness test)

The phase's correctness hinges on one independent test (`ui-design.md` §5.1):
- Take a **hero netlist** (an authored `.cnl` from Phases 1–5 — e.g. Hero2). **Draw a `.csch`** that matches
  it (place the same components, wire the same nets). **Extract** that `.csch`.
- **Assert the extracted design model is equivalent** to the authored `.cnl`'s model: same components, same
  parameter expressions, same **net-node order per component** (the terminal-order contract), same nets up to
  auto-name renaming (compare topology, not auto-name strings — two nets are "the same" if they connect the
  same terminal set).
- This catches terminal-order/base-offset errors (the emitted net-node order would differ) and connectivity
  errors (a missed T-junction or an un-dotted crossing would merge/split the wrong terminals).
- Run the extracted netlist through the engine and confirm the **DataSet matches** the authored netlist's
  DataSet (end-to-end equivalence) for at least one hero (e.g. Hero2 S-parameters).

This oracle is a **permanent test**, not a one-time check — it guards every future extraction change.

---

## 5. Run wiring (in-app)

- **Run command** (the existing `RunAnalysis` stub in `WorkspaceViewModel`): on the active TestBench
  schematic → extract → write `netlist.cnl` → `CnlReader`/Design-model → `Elaborator` → the analysis engine(s)
  the testbench declares → `DataSet`.
- **Engine reuse:** no new engine code — route to `SParameterEngine`/`NonlinearDcEngine`/`HbEngine`/
  `LoadpullEngine` exactly as the CLI does.
- **Results:** v1 surfaces the run via Messages (success/convergence/warnings, with the `netlist.cnl` path as
  a clickable link) and holds the `DataSet`; **visualizing** results is **Phase 7** (the data display). 6e's
  job is "the run happens and produces a DataSet," not "plot it."
- **Errors** (unconnected required pins, no analysis, singular matrix, non-convergence) → clear Messages
  pointing at the cause; never a silent failure.
- **The Units ASCII↔glyph seam** (the long-flagged gotcha): the engine `Units` table is ASCII-keyed (`Ohm`,
  `uH`, `uF`) while the editor unit ComboBox uses glyphs (`Ω`, `µH`, `µF`). Extraction emits parameter units —
  so **normalize glyphs → ASCII at extraction** (or teach `Units` the glyph spellings). This MUST be resolved
  in 6e or the first real run throws on `ApplyUnit`. (Flagged in `src/Ui/CLAUDE.md`.)

---

## 6. Implementation order (smallest correct first)

1. **The extractor core (headless):** `SchematicEditModel → Design model`, reusing
   `ComputeConnectivityGeometry` for union-find; net naming (ground/label/port/auto-stable); terminal-order
   emission. Framework-free, unit-testable with hand-built edit models. **No file IO, no engine, no UI yet.**
   **DONE (Phase 6e Step 1, 2026-06-09)** — `src/Ui/Schematic/NetExtractor.cs`;
   19 gate tests in `tests/Ui.Tests/NetExtractorLayer{1,2,3}Tests.cs`; all 829 tests green.
2. **`.cnl` emission + the oracle:** emit `.cnl` text; build the **extraction oracle** (hero `.csch` →
   extract → equivalent to authored `.cnl`; topology + terminal-order + DataSet equivalence). This is the
   correctness gate — land it early.
   **DONE (Phase 6e Step 2, 2026-06-09)** — `src/Core/Netlist/CnlWriter.cs` (TestBench → .cnl, inverse
   of CnlReader; round-trips all instance types, typed analyses, measurements, raw directives); 7 L1
   round-trip tests in `tests/Core.Tests/Netlist/CnlWriterTests.cs`; 3 oracle tests in
   `tests/Ui.Tests/ExtractionOracleTests.cs` (L2 topology equivalence + transposition failure + L3
   DataSet equivalence); all 839 tests green.
3. **The Units glyph↔ASCII normalization** (§5) — small but mandatory before a real run.
   **DONE (Phase 6e Step 3, 2026-06-09)** — `src/Core/Expressions/UnitNormalizer.cs`
   (`ToEngineUnit`: Ω→Ohm, µ/μ→u, composed with prefixes; ASCII units unchanged; None/empty→empty;
   table-uncovered units emit as-is without crash); applied at the single extraction emit point in
   `NetExtractor.EmitInstance`; 30 gate tests in `tests/Core.Tests/Expressions/UnitNormalizerTests.cs`;
   all 880 tests green.
4. **`netlist.cnl` write** (§3): workspace root / scratch dir, overwrite, provenance header.
   **DONE (Phase 6e Step 4, 2026-06-09)** — `WorkspaceViewModel.WriteNetlist` (private helper):
   resolves destination (workspace root when `CurrentWorkspacePath` set, else
   `RecoveryManager.SessionDir`); calls `NetExtractor.Extract` → `CnlWriter.Write` with provenance
   header (`; netlist.cnl — generated from TestBench "<name>" at <ISO-8601 UTC>`); atomic write
   (temp + `File.Move` overwrite). `RunAnalysis` command wired: extracts the active
   `SchematicDocument`, calls `WriteNetlist`, posts the path as a clickable `Messages.Success`,
   surfaces any extraction conflicts as `Messages.Warning`. No engine run (step 5). All 880 tests green.
5. **Run wiring** (§5): the Run command → extract → engine → DataSet; Messages reporting; error surfacing.
   (Scratch run writes to the scratch dir.)
   **DONE (Phase 6e Step 5, 2026-06-09)** — `src/Ui/Schematic/SchematicRunService.cs` (headless
   `RunNetlist(path) → RunResult`; dispatches typed analyses: HB/Loadpull/LoadpullPursuit/ParametricSweep/
   SParameterAnalysis; raw S-param directives parsed from `RawDirectives`; DC noted but deferred;
   engine exceptions captured as EngineError, never thrown). `WorkspaceViewModel.RunAnalysis` is now
   `async Task`: writes netlist → posts clickable path → runs service on background thread → posts
   Success/NoAnalysis/EngineError message → holds DataSets in `_lastRunDataSets` for Phase 7.
   `StopAnalysis` stays informational (no CancellationToken in the engines — runs to completion).
   4 gate tests in `tests/Ui.Tests/SchematicRunServiceTests.cs`; all 884 tests green.
6. **Disable-state + directives polish:** Open/Short emission; analysis/measurement directive emission from
   the schematic; multi-analysis testbenches.

Steps 1–2 are the heart (extraction + oracle); 3–5 make a real in-app run work; 6 covers the fuller surface.
Results **visualization is Phase 7**, not here.

---

## 7. Open / deferred
- **Results visualization** (plots/tables/contours) → Phase 7 (data display, `.cdd`).
- **Net-label cross-wire semantics** — **DECIDED (owner):** same-name labels **union** into one net, including
  across physically-disjoint wires (named connectivity / global nets), §2.1.6. (Not deferred.) The only open
  edge is the *conflict* case — one physical net carrying two different label names — which surfaces to
  Messages and resolves deterministically (§2.2).
- **Cell-instance (hierarchical) extraction** — a schematic instancing another cell: v1 may flatten or defer
  hierarchy depth; confirm scope at step 1 (the engine Design model supports cell instances, so emission is
  `Cell:Inst …` — but deep hierarchy extraction can be staged).
- **Incremental / live extraction** — v1 extracts on Run (not continuously); live netlist preview deferred.
