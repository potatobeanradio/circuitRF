# Phase 4b-2 Enhancement — `LoadpullPursuitResult` + optional follow-on `LoadpullResult` (Claude Code / Sonnet)

**Goal:** make `loadpull_pursuit` an end-to-end "search → recommend → focused loadpull, unattended" analysis.
Two additions:
1. The analysis **always** produces a **`LoadpullPursuitResult`** (merge/extend the existing `PursuitRunResult`)
   carrying inputs + recommended terminations (`GamBuilderResult`) + per-optimum Zsource + the optional
   follow-on result.
2. The analysis **optionally** runs a follow-on standard loadpull over the recommended terminations,
   producing a generic, role-agnostic **`LoadpullResult`**, gated by `CreateLoadpullResult` (default on).

> Read first: `docs/design/loadpull_pursuit.md` (§0.1 the headline workflow, §6.5 the result types and
> follow-on loadpull, §3 the new directive keys, §10 summary). Files: `LoadpullPursuitEngine.cs`
> (`PursuitRunResult`, `Run`, `Resolve`), `LoadpullResult.cs`, `GamWriter.cs` (`GamBuilderResult`),
> `LoadpullEngine.cs` (the standard loadpull this reuses). Design note wins over the brief.

## Context (already implemented — reuse, don't rebuild)
- `PursuitRunResult` (LoadpullPursuitEngine.cs) already holds MXP/MXE optima, the cache, unscorable, warnings.
- `GamBuilderResult` + `GamWriter.Build`/`WriteFile` (GamWriter.cs) already build the recommended `.gam` points.
- `LoadpullResult` (LoadpullResult.cs) is the generic 2-D loadpull dataset a plain `loadpull` produces.
- `LoadpullEngine` already runs a standard loadpull over a `GamGrid`.

## Scope

### STEP 1 — `LoadpullPursuitResult` (merge/extend `PursuitRunResult`, always produced)
Rename/extend `PursuitRunResult` → **`LoadpullPursuitResult`** (it's convenient to merge; do it). Add fields so
the result is self-documenting (loadpull_pursuit.md §6.5.1):
- **`Params`** — the resolved `PursuitParams` (the inputs: all directive settings).
- Keep existing: `MXP`, `MXE` (each `PursuitOptimum` with Z, value, sweep, Zsource), `Cache`, `UnscorableZ`,
  `Warnings`.
- **`RecommendedTerminations`** — the `GamBuilderResult` (the focused+broad `.gam` point set). **Always
  computed in memory** by calling `GamWriter.Build(...)` during the run, regardless of whether `OutputGrid`
  is set. (Currently the `.gam` is likely only built when `OutputGrid` is present — change it to always
  build in memory; `OutputGrid` only controls the additional `GamWriter.WriteFile(...)` call.)
- **`LoadpullData`** — the follow-on `LoadpullResult` from STEP 3, or **null** if `CreateLoadpullResult=false`.
- (The inner per-search `PursuitResult` type inside `PursuitEngine` is a DIFFERENT, internal type — leave it.)

### STEP 2 — new directive keys (loadpull_pursuit.md §3)
Add to the `loadpull_pursuit` directive resolution (`LoadpullPursuitEngine.Resolve` + `PursuitParams`):
- **`CreateLoadpullResult`** (bool, default **on/true**) — whether to run the follow-on loadpull (STEP 3).
- **`LoadpullResultZsource`** (enum **`MXE`** default / `MXP` / `None`) — which recommended Zsource the
  follow-on loadpull uses for the source match; `None` = use the Source Tuner's own declared Z1 (no override).
- (`SearchMethod` from the prior brief is already being added; keep it.)
All resolve through the expression engine / directive resolution like the other keys.

### STEP 3 — the optional follow-on `LoadpullResult` (loadpull_pursuit.md §6.5.2)
After the search + recommendation, if `CreateLoadpullResult` is true:
- Build a `GamGrid` from `RecommendedTerminations.Points` (the in-memory recommended terminations — NOT
  necessarily from a file; they exist whether or not `OutputGrid` wrote them).
- Determine the source match per `LoadpullResultZsource`:
  - `MXE` → set the Source Tuner's fundamental termination to the MXE optimum's recommended `Zsource`.
  - `MXP` → the MXP optimum's recommended `Zsource`.
  - `None` → do NOT override; leave the Source Tuner's declared Z1 as-is.
- Run a **standard `LoadpullEngine` loadpull** over that grid with that source match → a generic
  **`LoadpullResult`**.
- **The `LoadpullResult` stays role-agnostic** — it does not record that a pursuit created it. Store it in
  `LoadpullPursuitResult.LoadpullData`. The user correlates the two via the `LoadpullPursuitResult`.
- If `CreateLoadpullResult=false`: skip this entirely; `LoadpullData = null`. Search + recommendations are
  still produced.

### STEP 4 — orthogonality (verify)
Confirm the three outputs are independently controlled (loadpull_pursuit.md §6.5.2):
- Recommended terminations (`GamBuilderResult`): **always** built in memory.
- `.gam` file: written **iff** `OutputGrid` is set.
- Follow-on `LoadpullResult`: produced **iff** `CreateLoadpullResult` is true.
A run with `OutputGrid` unset but `CreateLoadpullResult=true` must still run the follow-on loadpull (from the
in-memory terminations). A run with `OutputGrid` set but `CreateLoadpullResult=false` must write the file but
produce `LoadpullData=null`.

## Acceptance
1. `loadpull_pursuit` always returns a `LoadpullPursuitResult` with: resolved params, MXP/MXE (+Zsource),
   cache, unscorable, warnings, the in-memory `GamBuilderResult`, and `LoadpullData` (or null).
2. `CreateLoadpullResult` (default on) and `LoadpullResultZsource` (default MXE) directive keys resolved.
3. Follow-on `LoadpullResult` produced over the recommended terminations with the chosen source match;
   it is the generic `LoadpullResult` type (role-agnostic — no pursuit-specific fields).
4. Orthogonality verified: `OutputGrid` controls the file only; `CreateLoadpullResult` controls the
   simulation only; recommended terminations always in memory. `LoadpullData=null` iff `CreateLoadpullResult=false`.
5. Hero 3B: a single `loadpull_pursuit` run with `CreateLoadpullResult=on` yields MXP/MXE + a focused
   `LoadpullResult` concentrated around the optima, using the MXE Zsource. Add/extend a test confirming the
   follow-on `LoadpullResult` exists, its grid matches the recommended terminations, and it is null when
   `CreateLoadpullResult=false`.
6. `dotnet build`/`dotnet test` green; Phases 1–4b still pass (existing pursuit behavior unchanged except the
   result-type rename + always-build-recommendations).

## Guardrails
- Reuse `GamWriter.Build`, `LoadpullEngine`, `LoadpullResult` — do not duplicate loadpull or grid-building logic.
- `LoadpullResult` stays generic/role-agnostic — do NOT add pursuit-aware fields to it.
- The follow-on loadpull uses the in-memory recommended terminations, not a re-read of the `.gam` file
  (decouple from `OutputGrid`).
- `LoadpullResultZsource=None` must leave the Source Tuner's declared Z1 untouched (no override).
- Diagnostics over grinding; flag design questions.
- Update `src/Engine/Loadpull/CLAUDE.md` with `LoadpullPursuitResult`, the follow-on loadpull, and the new keys.
