# Sonnet Brief — Uniform engine-diagnostics channel → Messages pane

**Goal.** Surface engine/elaboration **run-time warnings** (S-param regularization with floating-node detail,
HB convergence failures, elaboration lints) in the **Messages pane** — once per run, full detail, at Warning
level. Today they go only to `Console.Error` and never reach the UI.

**Firewall constraint (must respect).** `CircuitRF.Engine` and `CircuitRF.Core` must NOT reference the UI;
`IMessageSink` is UI-only and its doc says *the engine never calls it directly — the UI layer reads the result
and posts.* So diagnostics cross via a **Core-level channel** that the UI run service drains and posts.

**Root gap (confirmed by reading the chain).** `SParameterEngine.Run`/`HbEngine.Run` emit diagnostics via
`Console.Error.WriteLine`. `ElaboratedNetlist.AddWarning` exists (a `List<string> Warnings` + stderr echo) but
is `internal`, so engines (separate assembly) can't call it, and `SchematicRunService.RunNetlist` **never reads
`nl.Warnings`** — it only assembles its own `notes`/`errors`. `WorkspaceViewModel.RunAnalysis` posts only
`RunResult.StatusMessage` (one `Success`/`Error`/`Info`). Net: no elaboration OR engine warning is ever shown.

## Design — one channel, three small layers

### 1. Core: make the warning channel engine-writable + structured (`ElaboratedNetlist.cs`)
- Make `AddWarning` **public** (it's already the Core diagnostic sink; engines need to append run-time warnings).
- Keep the existing `Warnings` list + stderr echo (headless/CLI still benefit).
- Add a lightweight **dedup guard** so repeated identical warnings collapse to one entry:
  `AddWarningOnce(string key, string message)` — no-op if `key` already seen this run; appends otherwise.
  (S-param regularization is topology-invariant across frequencies → one entry, not 101.)
- Update the XML doc: `Warnings` now carries **elaboration AND engine run-time** warnings.

### 2. Engines: emit through the channel instead of (or alongside) stderr
- **`SParameterEngine.Run`** — when regularization engages (the `catch (SingularMatrixException) when (canRetry)`
  block), call `netlist.AddWarningOnce("sparam-regularization", fullDetail)` with the **full** message —
  i.e. the complete `ex.Message` (which already contains the `FindZeroRows`/`FindZeroCols` node names + the
  "touched by: …" component lists from the namers), **not** the current `ex.Message.Split('\n')[0]` first-line
  truncation. Prefix with a one-line summary, e.g. `"S-parameter matrix singular — regularization (gmin) applied.
  Likely floating node(s):"` then the detail. Keep or drop the per-frequency stderr line as you like, but the
  **pane entry is emitted once**. (The engine already has `netlist` in hand — no signature change.)
- **`HbEngine`** — route the existing stderr diagnostics through `AddWarningOnce`/`AddWarning` too:
  - DC operating-point non-convergence (`"[HB] Warning: DC operating point did not converge…"`,
    `[HB2D]` variant) → `AddWarningOnce("hb-dc-nonconverge", …)`.
  - Newton non-convergence per sweep point (`"[HB] Non-convergence at …"`, `[HB2D]` variant) → these CAN differ
    per point, so summarize: accumulate count + worst residual and emit **one** warning at end of `Run`
    (e.g. `"HB did not converge at 3 of 21 sweep points (worst ‖F‖=…); stored best-available results."`), rather
    than one per point. Keep the detailed per-point `[HB-DC]`/`[HB2D-DC]` prints on stderr only (they're a debug
    trace, not user-facing). Commensurability errors already throw → surface as `EngineError` (unchanged).
- **DC** — `RunTypedAnalysis`'s `DcAnalysis` case is still the "not wired in-app" stub returning null; there is
  **no DC engine run**, so there is nothing to emit yet. No DC work in this brief beyond the channel being
  ready. (When DC dispatch is added later — tied to the `ParametricSweepEngine` DC-dispatch gap noted in
  `data-display.md` §7.3 — it uses the same `AddWarning` channel for free.)
- **Loadpull/ParametricSweep** — out of scope here; they can adopt the same channel later. Don't refactor them.

### 3. Run service: drain `nl.Warnings` into the result (`SchematicRunService.cs`)
- After dispatch completes (typed + raw), read `nl.Warnings` and carry them on `RunResult` as a **separate**
  list, distinct from the status summary:
  - Add `IReadOnlyList<string> Warnings` to `RunResult` (default empty).
  - Populate it from `nl.Warnings` (the netlist is in scope as `nl`). This also captures **elaboration** lints
    (`LintTopLevelTerms`, buried-Term, duplicate/gap Num) that currently never surface.
- Do **not** fold warnings into `StatusMessage` (keep the Info summary clean and separate from Warning entries).

### 4. Caller: post warnings at Warning level (`WorkspaceViewModel.RunAnalysis`)
- In Step 3 (surface the result), **before/after** the status post, iterate `result.Warnings` and call
  `Messages.Warning(w)` for each. Post these for `Success` **and** `EngineError` (a run can both error and have
  warnings). Avoid double-posting: warnings come only from `result.Warnings`, the summary only from
  `StatusMessage`.
- Long multi-line regularization detail is fine in one `Messages.Warning(...)` entry (the pane renders it);
  no need to split per line.

## Out of scope
- Loadpull / ParametricSweep diagnostic routing (future, same channel).
- DC engine diagnostics (no in-app DC run exists yet).
- Changing `RunResult.StatusMessage` content or the existing notes/errors behavior.
- Any per-frequency or per-sweep-point spam — emit **once** (dedup) or **summarized**.

## Tests
- **Engine (`CircuitRF.Engine.Tests`):** an S-param run on a netlist with a floating node →
  `nl.Warnings` contains exactly **one** regularization entry, and that entry contains the floating node's
  **name** (assert the node name substring is present — proves no first-line truncation and dedup-to-one).
- **Engine:** an HB run forced to non-converge (tiny MaxIter or a known-divergent stub) → `nl.Warnings` contains
  one summarized convergence warning with the failing-point count.
- **Run service (`Ui.Tests`):** `RunNetlist` on a netlist that elaborates with a lint warning (e.g. buried Term)
  → `RunResult.Warnings` is non-empty and includes the lint text (proves the drain surfaces **elaboration**
  warnings too, closing the pre-existing gap).
- **Run service:** a clean netlist → `RunResult.Warnings` empty; `Status == Success`.
- Build 0W/0E; full suite green.

## On completion
Note in `src/Engine/CLAUDE.md` and `src/Ui/CLAUDE.md`: engines surface run-time warnings via
`ElaboratedNetlist.AddWarning`/`AddWarningOnce` (Core-level, firewall-safe); `SchematicRunService` drains
`nl.Warnings` into `RunResult.Warnings`; `WorkspaceViewModel.RunAnalysis` posts them to the Messages pane at
Warning level. The engine still never touches `IMessageSink` directly.
