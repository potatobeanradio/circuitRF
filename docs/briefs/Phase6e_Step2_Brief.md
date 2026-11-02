# Phase 6e — Step 2: `.cnl` emission + the extraction oracle (Claude Code / Sonnet)

Make extraction's output **inspectable and provably correct**: emit a `TestBench` (from step 1's
`NetExtractor`) as **`.cnl` text** that `CnlReader` round-trips, and build the **extraction oracle** — a
hero `.csch` drawn to match an authored hero `.cnl` must extract to an equivalent design + identical engine
result. **This brief is step 2.** No `netlist.cnl`-on-simulate file path, no in-app Run wiring, no Units
normalization, no UI — those are steps 3+. Read `net-extraction-and-run.md` §2.3/§2.4/§4 first. Sub-gated;
**report and stop between every layer.** Firewall green.

> Read first: `docs/design/net-extraction-and-run.md` §2.3 (terminal-order emission), §2.4 (output), §4 (the
> oracle — topology + terminal-order + DataSet equivalence). Context code: `src/Ui/Schematic/NetExtractor.cs`
> (step 1 — `Extract(model) → ExtractionResult(TestBench, Conflicts)`; the source), `src/Core/Design/
> TestBench.cs` + `Instance.cs` (`Instances`, `NetBindings` (terminal order), `Overrides`, `RefNetBinding`,
> `GlobalVariables`), `src/Core/Netlist/CnlReader.cs` (the **round-trip consumer** — emit exactly the line
> grammar it parses: `Type:Inst nets params`, `name = expr`, `analysis …`/`measure …`; honor the N-or-N+1
> SnP ref-node rule), `src/Core/Elaboration/Elaborator.cs` (elaborates the Design model — for the end-to-end
> oracle), `src/Engine/SParameterEngine.cs` (the engine for the oracle's DataSet compare), `src/Cli/
> Program.cs` (how `.cnl → CnlReader → Elaborator → engine → DataSet` is wired — mirror for the oracle),
> existing hero fixtures (Hero2 `.cnl` + golden — find under `testdata/`/test fixtures). Design docs win on
> any conflict.

## The spine (do not violate)
- **Emit exactly what `CnlReader` parses.** The emitter is the inverse of `CnlReader`; its output must
  round-trip through `CnlReader` to an equivalent `TestBench`. Same line grammar, same terminal-order =
  net-node order, same ref-node (N-or-N+1) convention.
- **Terminal order is the contract** (§2.3) — emit each instance's nets in `NetBindings` order (which step 1
  already put in symbol terminal order); position *is* the terminal index. No transpose, no off-by-one.
- **The oracle is the correctness gate** (§4) — extraction equals authored. Compare **topology** (same
  instances, params, net-node order per instance; nets equal up to auto-name renaming = same terminal sets),
  AND **end-to-end** (extracted netlist through the engine matches the authored netlist's DataSet for ≥1 hero).
- **Emitter framework-free** (it consumes the Design model + writes text; no Avalonia/Skia).
- **Scope fence (step 2):** `.cnl` text emission + round-trip + oracle. NO `netlist.cnl` workspace/scratch
  path, NO Run command, NO Units glyph normalization, NO UI.

---

## LAYER 1 — `.cnl` emitter (`TestBench` → text)

A framework-free `CnlWriter` (mirror `CnlReader`'s grammar, inverse direction; e.g. `src/Core/Netlist/
CnlWriter.cs` so reader+writer sit together):
1. **Header comment:** a `; …` provenance line (the writer accepts a caller-supplied comment; the
   `netlist.cnl` provenance string is supplied in step 4 — here just support an optional header).
2. **Global variables:** `name = expr [unit]` lines from `TestBench.GlobalVariables`.
3. **Instances:** one `Type:Inst  net1 net2 …  param=val …` line per `Instance` — `Reference` as the type,
   `InstanceName`, then **`NetBindings` in order**, then `Overrides` as `param=val` (units as carried).
   Honor `RefNetBinding` for N-port (emit the extra ref net per the N-or-N+1 rule `CnlReader` expects).
4. **Directives:** emit `TestBench.Analyses`/`Measurements`/`RawDirectives` as `analysis …`/`measure …` lines
   (verbatim for `RawDirective`; typed analyses formatted to the grammar `CnlReader` parses back). For step 2
   the heroes drive what must round-trip.
5. Deterministic ordering (stable instance + line order) so output diffs are clean.

**Layer 1 gate:** a hand-built `TestBench` → `CnlWriter` → text → `CnlReader` → a `TestBench` **equivalent** to
the original (same instances, references, net-bindings-in-order, params, ref-nodes, variables). A round-trip
unit test asserts this. Report.

---

## LAYER 2 — the extraction oracle (topology equivalence)

Build the oracle test (`ui-design.md` §5.1 / §4):
1. Pick a hero with a clean authored `.cnl` (Hero2). **Construct a `SchematicEditModel`** that matches it —
   place the same components with the same params, wire the same nets (build it programmatically in the test,
   or load a committed hero `.csch` if one exists; programmatic is fine and explicit).
2. `NetExtractor.Extract` it → `TestBench_extracted`. `CnlReader` the authored hero `.cnl` →
   `TestBench_authored`.
3. **Assert topological equivalence:** same set of instances (by reference+params), and for each instance the
   **net-node order matches** (terminal order), with nets compared **up to auto-name renaming** — i.e. build
   the partition "which (instance,terminal) endpoints share a net" for both and assert the partitions are
   equal. (Don't compare auto-name strings; compare the connectivity they encode.)
4. This is the test that catches a transposed FET terminal or an off-by-one base shift (the partition or the
   per-instance order would differ).

**Layer 2 gate:** the oracle passes for Hero2 (extracted topology ≡ authored); deliberately transposing two
terminals in the test schematic makes it **fail** (proving the test has teeth). Report.

---

## LAYER 3 — end-to-end oracle (DataSet equivalence)

Close the loop through the engine:
1. Run **both** netlists — `TestBench_extracted` and `TestBench_authored` — through `Elaborator` + the
   appropriate engine (e.g. `SParameterEngine` for an S-param hero), exactly as `Program.cs` does.
2. **Assert the DataSets match** (within numerical tolerance) — same S-parameters vs. frequency. This proves
   extraction is correct not just structurally but in the answer the engine computes.
3. Use an existing hero golden if available; else compare extracted-vs-authored directly (both engine-run,
   same result). Pick ≥1 hero (Hero2 S-params is the natural choice).

**Layer 3 gate:** extracted and authored produce matching DataSets for the hero; the end-to-end oracle is a
permanent test. Report.

## Acceptance (step 2)
1. A framework-free `CnlWriter` emits a `TestBench` as `.cnl` text that `CnlReader` round-trips to an
   equivalent design (instances, terminal-order net-bindings, params, ref-nodes, variables, directives).
2. The extraction oracle proves a hero `.csch`/model extracts to a design **topologically equivalent** to the
   authored hero `.cnl` (net-partition + per-instance terminal order), and **fails** on a deliberate
   terminal transposition.
3. The end-to-end oracle proves extracted vs. authored produce matching engine DataSets for ≥1 hero.
4. `dotnet build`/`dotnet test` green; firewall green (emitter/oracle framework-free); **no netlist.cnl path,
   no Run wiring, no Units normalization, no UI** (steps 3+); nothing else regresses.

## Guardrails
- **Emit exactly what `CnlReader` parses** — the writer is the reader's inverse; round-trip is the unit test.
- **Terminal order = net-node order**, positional; no transpose/off-by-one (the oracle guards it — make sure
  it has teeth, i.e. a transposition fails it).
- **Oracle compares topology up to auto-name renaming** (net partitions), not name strings; plus DataSet
  equivalence end-to-end.
- **Framework-free** emitter + oracle (the oracle is a Core/engine test, not a UI test).
- **Scope fence:** emission + round-trip + oracle only — no netlist.cnl path, Run, Units, or UI.
- Sub-gate the three layers; report and stop between each; don't run the full suite into the output limit.
- Update `net-extraction-and-run.md` §6 status (step 2 done) and `src/Core/*/CLAUDE.md` / `src/Ui/CLAUDE.md`
  (the `CnlWriter` round-trips `CnlReader`; the extraction oracle is the permanent correctness gate).

*Exit: extraction emits inspectable `.cnl` that round-trips the reader, and a permanent oracle proves a drawn
schematic extracts to the same design — and the same engine answer — as the authored hero netlist; the
correctness gate the in-app Run (steps 3–5) sits behind.*
