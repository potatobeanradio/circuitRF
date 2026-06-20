# Phase 6e — Step 4: write `netlist.cnl` on simulate (Claude Code / Sonnet)

Wire the extraction output to disk: on simulate, **extract → write one `netlist.cnl`** to the right directory
(workspace root for a materialized workspace; the scratch-session dir for a scratch schematic), overwritten
each run, with a provenance header. Builds on step 1 (`NetExtractor`), step 2 (`CnlWriter`), step 3
(`UnitNormalizer`). **This brief is step 4** — the file write + destination logic + provenance. **No in-app
Run/engine wiring** (that's step 5) — step 4 produces the netlist file; step 5 runs it. Read
`net-extraction-and-run.md` §3 first. Sub-gated; report between layers. Firewall green.

> Read first: `docs/design/net-extraction-and-run.md` §3 (`netlist.cnl` destination, overwrite, provenance),
> `scratch-and-save-lifecycle.md` §1.3 (scratch sim writes to the scratch working dir = recovery session dir).
> Context: `src/Ui/Schematic/NetExtractor.cs` (`Extract(model) → ExtractionResult`), `src/Core/Netlist/
> CnlWriter.cs` (`Write(tb, header)`), `src/Ui/Schematic/UnitNormalizer.cs` (step 3 — already applied at
> emit), `src/Ui/Schematic/RecoveryManager.cs` (the scratch-session recovery dir — the scratch destination),
> `src/Ui/ViewModels/WorkspaceViewModel.cs` (`CurrentWorkspacePath`, the active document, `RunAnalysis` stub,
> `_scratchDocs`, `Messages`), `src/Ui/Schematic/SchematicDocument.cs` (`IsScratch`/`FilePath`/`ViewModel.
> EditModel`). Design docs win on any conflict.

## The spine
- **One `netlist.cnl`, overwritten each run** (§3) — not one per testbench; the latest run's netlist.
- **Destination:** **workspace root** when a workspace is open; the **scratch-session dir** (RecoveryManager's
  session dir) for a scratch schematic with no workspace (scratch sim is a first-class run, §1.3).
- **Provenance header:** `; netlist.cnl — generated from TestBench "<name>" at <ISO-8601 UTC>` (passed to
  `CnlWriter.Write`).
- **Generated scratch artifact** — never part of the saved project; `.csch` is the source of truth.
- **Scope fence (step 4):** extract + write the file. NO engine run, NO results, NO Run-command UI beyond
  what's needed to produce the file (step 5 does the run).

---

## LAYER 1 — destination resolver + write helper

A small helper (in `WorkspaceViewModel` or a `NetlistWriter` helper):
1. **Resolve the destination dir:** workspace open (`CurrentWorkspacePath != null`) → its directory
   (`Path.GetDirectoryName`); else (scratch) → the **RecoveryManager scratch-session dir**. Target =
   `<dir>/netlist.cnl`.
2. **`WriteNetlist(SchematicEditModel model, string testBenchName)`:** `NetExtractor.Extract(model)` →
   `CnlWriter.Write(tb, header)` with the provenance header (testBenchName + `DateTime.UtcNow` ISO-8601) →
   write to the resolved path (overwrite). Return the path written (+ any extraction `Conflicts` to surface).
3. Atomic write (temp + rename) for robustness, consistent with the `.cws` policy.

**Layer 1 gate:** `WriteNetlist` on a small model writes `netlist.cnl` to the workspace root (workspace open)
or the scratch dir (no workspace), with the provenance header line; the file round-trips through `CnlReader`.
Report.

---

## LAYER 2 — hook it to the simulate entry (produce-the-file only)

1. Wire the helper so that invoking simulate on the **active TestBench schematic** extracts + writes
   `netlist.cnl`. For step 4 this may be a temporary command/button or the existing `RunAnalysis` stub doing
   **only the extract+write** (no engine yet) — state which. The testBenchName comes from the active
   document's cell/title.
2. **Report** via Messages: success with the **full `netlist.cnl` path as a clickable link**, plus any
   extraction `Conflicts` (e.g. two-different-labels-on-one-net from step 1) as warnings.
3. **Scratch run** writes to the scratch dir and reports that path (no workspace required — consistent with
   "scratch sim is first-class").

**Layer 2 gate:** triggering simulate on a drawn schematic writes `netlist.cnl` (workspace or scratch dir),
posts the path as a clickable Message, and surfaces any conflicts; opening the file shows the provenance header
+ the extracted netlist. Report.

## Acceptance (step 4)
1. Simulate extracts the active schematic and writes one `netlist.cnl` (overwritten each run) to the workspace
   root, or the scratch-session dir when no workspace is open; atomic write; provenance header.
2. The path is reported (clickable) via Messages with extraction conflicts surfaced; the file round-trips
   through `CnlReader`.
3. `dotnet build`/`dotnet test` green; firewall green; **no engine run / results / step-5 Run wiring**;
   nothing else regresses.

## Guardrails
- **One `netlist.cnl`, overwritten; workspace-root or scratch-dir destination; provenance header.**
- **Generated artifact** — not saved-project state.
- Reuse `NetExtractor`/`CnlWriter`/`RecoveryManager`; don't duplicate.
- **Scope fence:** produce the file only — no engine run (step 5).
- Sub-gate the two layers; report between each.
- Update `net-extraction-and-run.md` §6 status (step 4 done) and `src/Ui/CLAUDE.md`.

*Exit: simulate writes an inspectable `netlist.cnl` to the workspace (or scratch dir) with provenance — the
artifact the in-app Run (step 5) feeds to the engine.*
