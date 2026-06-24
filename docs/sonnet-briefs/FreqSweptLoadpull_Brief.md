> **STATUS — Layers A–G COMPLETE + follow-ups closed (2026-06-23).** Engine dispatch + `"freq"`(Hz)
> axis (A/B), `.gam` multi-block input (C) and freq-tagged output (D), pursuit per-freq dispatch + trends
> + follow-on (E), UI un-gate + persistence (F), and display recognition (G) all landed and green
> (Core 386, Engine 452+1skip, Ui 1469; 0W/0E). Layer G fix: `LoadpullRecognition` tolerates a leading
> `freq` axis so the summary table + contour-frequency picker light up from `LoadpullSurface.Frequencies`.
> **Both noted follow-ups now resolved:** (1) the pursuit follow-on is run through
> `LoadpullPostProcessor.Enrich` in `RunFollowOnLoadpull`, so `LP_Pout_dBm`/`LP_Efficiency`/`LP_Zin_*`/…
> appear (not just raw `LP_Pout`); (2) `.gam` OutputGrid writes are coordinated by a single
> `OutputWriteState` threaded through nested `Run`s — the first pursuit write truncates, the rest append,
> so nested sweeps accumulate every block instead of re-truncating per outer point.

# Frequency-Swept Loadpull & Loadpull-Pursuit (Claude Code / Sonnet)

Enable **parametric-sweep chains over a frequency VAR** for `Loadpull` and `Loadpull-Pursuit`. The user
wants to declare `VAR: RFfreq = 2 GHz` (or unit-less `RFfreq = 2`) and run a Loadpull / Loadpull-Pursuit
**swept over frequency**, producing a freq-stacked result the existing multi-frequency `LoadpullSurface`
already understands (summary table per freq, contours per freq). Plus a **freq-aware `.gam` grid format**
(input *and* pursuit output), because optimum terminations are a function of frequency — while keeping the
flexibility that a `.gam` with **no** freq info is usable at **any** frequency.

> **Scope fence.** This brief is the design/scope only — it intentionally lists *what* to build and the
> *invariants*, not finished code. Sub-gate the implementation and **report + stop between layers** (A→G).
> Each layer is independently testable. Firewall stays green (no Avalonia in `src/Core`/`src/Engine`/`RfCore`).

> **Read first:** `docs/design/loadpull.md` (Tuner + the `.gam` surface, §1.3/§2.2), `docs/design/
> loadpull_pursuit.md` (MXP/MXE + recommended terminations §5/§6), `docs/design/loadpull-postprocessor.md`
> (derived metrics, `__Freq` carrier, canonical names). Code: `src/Engine/ParametricSweepEngine.cs`
> (the generic sweep — `RunInner` throws for loadpull today), `src/Engine/Loadpull/LoadpullEngine.cs`
> (`Resolve` + `BuildLoadpullDataSet` + `__Freq`), `src/Engine/Loadpull/LoadpullPursuitEngine.cs`
> (`Resolve` + `RunFollowOnLoadpull` + `RecommendedTerminations`), `src/Engine/Loadpull/GamReader.cs` /
> `GamWriter.cs` (the `.gam` I/O), `../RfCore/src/Loadpull/LoadpullSurface.cs` (`BuildFreqSlices` — freq
> detection by axis name), `src/Ui/Schematic/SchematicRunService.cs` (dispatch + chain), `src/Core/Design/
> AnalysisChain.cs` (enabled-chain resolution), `src/Ui/ViewModels/AnalysisEditorViewModel.cs`
> (`ShowSweeps` gate). Design docs win on conflict.

---

## The spine (what's already free, and the one real gap)

**Already free — the tone sweep "just works" per point.** Both `LoadpullEngine.Resolve` and
`LoadpullPursuitEngine.Resolve` resolve the tone with **var-unit-wins** via
`FreqUnit.ResolveHz(ToneExpr, ToneUnit, globals, globalsWithUnit)`. `ParametricSweepEngine.Run` already
injects each swept override into `tb.GlobalVariables` (with `baseUnit`) and re-elaborates per point, so a
re-`Resolve` after injection yields `p.ToneHz` = the swept frequency with **no new tone plumbing**. The
chain dispatch is also already wired: `SchematicRunService` routes a `ParametricSweepAnalysis`-wrapped
loadpull to `ParametricSweepEngine.Run`, and `RootInnerName`/`AnalysisChain` resolve the loadpull as the
chain's base. **The only engine blocker is that `ParametricSweepEngine.RunInner` throws
`NotSupportedException` for `LoadpullAnalysis`/`LoadpullPursuitAnalysis`** (Sweep-Fix-2 deliberately
excluded engine-owning analyses).

**The one real integration gap — the frequency axis.** `LoadpullSurface.BuildFreqSlices` recognizes the
frequency dimension **only by an axis named `"freq"` (Hz)**, falling back to the single-frequency `__Freq`
carrier cube. But the generic sweep prepends an axis named after the **swept variable** (`"RFfreq"`) carrying
the **raw swept values** (which for a unit-less `RFfreq = 2` are *not* Hz). And `__Freq` is `__`-prefixed, so
`DataSet.StackSweepAxis` passes it through **unstacked** (only the first point's value survives). So a naive
freq sweep yields a `"RFfreq"` axis the surface ignores → it reports a single (wrong) frequency. **The
result must carry a `"freq"` axis in Hz built from the per-point *resolved* `p.ToneHz`.**

**Design rule for the axis (the load-bearing invariant):** a Loadpull/Pursuit swept over a variable that
changes the resolved tone is a *frequency sweep* → stack with axis **`"freq"`, unit `"Hz"`, values = the
per-point resolved `ToneHz`** (not the raw swept values — this makes `RFfreq = 2 GHz` and unit-less
`RFfreq = 2` both correct). A loadpull swept over a variable that does **not** change the tone (e.g. a bias)
is *not* a frequency sweep → keep the var-named axis; the surface stays single-frequency (correct: the tone
is fixed). Robust detection: collect the per-point `ToneHz`; if they vary → it's a freq sweep.

---

## LAYER A — Engine dispatch: run Loadpull/Pursuit per swept point

Add the missing dispatch so a swept loadpull/pursuit actually runs per point. **Recommended approach: a
dedicated freq-sweep path** (rather than naively reusing the generic `StackSweepAxis`), because the freq
semantics — building the `"freq"` axis from resolved `ToneHz`, selecting per-freq `.gam` blocks, and
collecting per-freq recommended terminations — are loadpull-specific and don't belong in the generic engine.

Two viable shapes (pick one; recommend the first):

1. **Dedicated `LoadpullSweepEngine` (recommended).** Reuses the override-injection / re-elaboration loop
   from `ParametricSweepEngine.Run` (factor it out or mirror it), but for each swept value: inject override →
   re-elaborate → `LoadpullEngine.Resolve` (→ `p`, with `p.ToneHz`) → `Run(p)` → **enrich** → collect
   `(DataSet, p.ToneHz)`. Then stack per the axis rule above. Keep the generic engine untouched.
2. **Generic `RunInner` cases + post-stack fix-up.** Add `LoadpullAnalysis`/`LoadpullPursuitAnalysis` cases
   to `RunInner`; after `StackSweepAxis`, if the inner is a loadpull and the per-point `ToneHz` (readable
   from each point's `__Freq`) varies, rename the prepended axis to `"freq"` and overwrite its values with
   the resolved `ToneHz`. More coupling in the generic engine; only choose if reusing its loop verbatim wins.

**Enrich per point, before stacking.** Today `SchematicRunService` enriches at dispatch
(`LoadpullPostProcessor.Enrich(new LoadpullEngine(...).Run(p))`). Factor that "run + enrich" into one helper
and call it **per swept point** so every freq slice carries the canonical names (`Pout_dBm`, `Efficiency`,
`Zin_real`, …). The post-processor is idempotent (`__lpEnriched` sentinel) and gated on `__SrcNodeIdx`, so
per-point enrich is safe. The derived FOM cubes `{gridPoint,pinStep}` then stack to `{freq,gridPoint,pinStep}`
and `LoadpullSurface.metricNames` already lists them.

**`SchematicRunService`:** the non-swept dispatch (`case LoadpullAnalysis`/`LoadpullPursuitAnalysis`) stays;
the swept path is reached via the existing `ParametricSweepAnalysis` top dispatch — route it to the new
loadpull-sweep handler (detect: chain base is loadpull/pursuit). `RootInnerName` already names the result
file after the loadpull.

**Stacking notes (`DataSet.StackSweepAxis`, generic):** handles complex `GammaLoad`/`ZLoad {gridPoint}`,
rank-4 `V`/`INl`, real FOMs. `__`-prefixed cubes (`__SrcNodeIdx`, `__LoadNodeIdx`, `__lpEnriched`, `__Freq`)
pass **unstacked** (first point only) — fine for the node-index/sentinel; `__Freq` becomes stale but is
ignored once the real `"freq"` axis exists (consider dropping `__Freq` emission on swept runs, or leave it).
**Constraint:** stacking requires identical cube shapes across points → every freq must run the **same number
of grid points** (trivially true when one freq-less `.gam` is reused; see Layer C for freq-dependent grids).

**Gate:** a freq-swept Hero-3 loadpull (e.g. `RFfreq` over {1.8, 2.0, 2.2} GHz) runs, produces cubes with a
leading `"freq"` axis in Hz = the resolved tones, and `new LoadpullSurface(ds).Frequencies` returns all three.

---

## LAYER B — Frequency axis = resolved ToneHz in Hz (the surface contract)

Make the swept result satisfy `LoadpullSurface`'s freq contract:

- Stacked axis **name `"freq"`, unit `"Hz"`, values = per-point resolved `p.ToneHz`**.
- Verify `LoadpullSurface.BuildFreqSlices` then sees `hasFreq == true`, builds one `FreqSlice` per freq, and
  `Frequencies` is populated (it already supports multi-freq from `.spl`). No RfCore change should be needed
  if the axis is named `"freq"`; if you instead go with a var-named axis, the alternative is to teach
  `BuildFreqSlices` to also recognize a frequency axis by **unit `"Hz"`** — but that does **not** fix the
  unit-less `RFfreq = 2` case (raw values aren't Hz), so prefer building the axis from resolved `ToneHz`.
- **Unit handling:** `RFfreq = 2 GHz` → swept values `[2e9,…]`, `ToneHz = 2e9`. `RFfreq = 2` (no unit) +
  analysis `ToneUnit=GHz` → swept values `[2,…]` but `ResolveHz` applies `ToneUnit` → `ToneHz = 2e9`. Building
  the axis from `ToneHz` makes both correct and Hz-valued. (Document that the analysis `ToneUnit` still
  governs a unit-less VAR, exactly as in HB.)

**Gate:** summary table shows the right `Freq (GHz)` per row for both `2 GHz` and unit-less `2` (+ `ToneUnit=GHz`).

---

## LAYER C — `.gam` INPUT: optional per-frequency blocks (keep "any-frequency" flexibility)

`GamReader.GamGrid = (Points, Z0)` is a single block. Extend to optional **per-frequency blocks**, preserving
full backward compatibility.

- **Format — use a non-comment directive line, NOT a `#` line.** In `GamReader.Parse`, `;` is a pure
  comment and `#` is *also* effectively a comment: only the **first** `#` line is read (as the file header
  for form/`Z0`/format); **every subsequent `#` line is skipped** (`Parse` lines ~79–80). So a per-block
  `# Freq=…` marker would be silently dropped for all blocks after the first. Instead, delimit blocks with a
  **bare directive line** whose first token is `Freq=<value><unit>` (or `freq <value> <unit>`), e.g.:

  ```
  # impedance Z0=50 re+j*imag      ← file header (unchanged; form/Z0/format defaults)
  freq=1.8GHz                      ← block delimiter (no '#', not a comment)
  80+j*10
  90+j*5
  freq=2.0GHz
  85+j*8
  95+j*3
  ```

  This is unambiguous: data lines start with a digit / `+` / `-` / `.`, so a line whose first token begins
  with a letter (`freq`) is a directive, never a data point. `;` and `#` stay pure comments (users can still
  annotate). Per-block form/`Z0`/format overrides on the `freq` line are optional/future — v1 inherits the
  file header. The parser needs a new branch in `Parse`: a non-comment line starting with `freq`
  (case-insensitive, `=` or whitespace separated) closes the current block and opens a new one at that freq
  (resolve `<value><unit>` to Hz via the same unit table the tone uses).
- **No `freq` line anywhere → one block, "any frequency"** (today's behavior, unchanged). A file MAY mix one
  leading freq-less block (the fallback) with `freq`-tagged blocks.
- **Model:** `GamGrid` → `{ IReadOnlyList<GamFreqBlock> Blocks, double Z0 }` with
  `GamFreqBlock(double? FreqHz, IReadOnlyList<GamPoint> Points)` (`FreqHz == null` = any-freq). Keep a
  back-compat accessor so existing single-block callers still compile (`Points` → the sole/any block).
- **Selection (`LoadpullEngine.Resolve`, given `p.ToneHz`):** exact freq block (within tolerance, e.g.
  0.1 %); else the `null`-freq block; else a clear error ("no `.gam` block for f = … and no any-frequency
  block"). **No interpolation between freq blocks in v1** (exact/nearest only — note as a future option).
- **Equal point counts** across freq-tagged blocks used in one sweep (stacking constraint, Layer A). Validate
  and error clearly if ragged.

**Gate:** a 3-freq `.gam` drives a 3-freq sweep selecting the right block per freq; a legacy freq-less `.gam`
still runs at any swept frequency.

---

## LAYER D — `.gam` OUTPUT (pursuit): freq-tagged recommended terminations

The optimum (MXP/MXE → recommended terminations) is **per frequency**. `GamWriter.WriteFile` writes a single
block today; extend to **per-freq sections**.

- A freq-swept pursuit collects one `GamWriter.GamBuilderResult` **per frequency** →
  `IReadOnlyList<(double FreqHz, GamBuilderResult)>`.
- `GamWriter.WriteFile` emits the file `#` header once, then a **`freq=<f>` directive line** + points per
  frequency (the non-comment block delimiter from Layer C, round-trippable by the Layer-C reader). A
  single-frequency pursuit still writes one block (a leading `freq=` line is optional) — keep the current
  freq-less output readable.
- `LoadpullPursuitResult.RecommendedTerminations` becomes per-freq (or gains a per-freq map); the in-memory
  result is the source of truth (file write still gated on `OutputGrid`).

**Gate:** freq-swept pursuit writes a `.gam` with one block per freq; round-trips through the Layer-C reader.

---

## LAYER E — Pursuit follow-on Loadpull, per frequency

`RunFollowOnLoadpull` runs a focused Loadpull over the **in-memory recommended terminations**. For a
freq-swept pursuit it must run **freq-swept**, using **each frequency's** recommended grid (Layer D), with the
source `Z[1]` set per freq from that freq's MXE/MXP `Zsource` (the existing `LoadpullResultZsource` logic,
now per freq). The follow-on `LoadpullData` becomes a freq-stacked `LoadpullResult` (same `"freq"` axis
convention as Layer B).

**Gate:** `CreateLoadpullResult=true` on a freq-swept pursuit yields a freq-stacked `LoadpullData` whose
per-freq grid-point count matches that freq's recommended terminations.

---

## LAYER F — UI: enable the sweep card for Loadpull/Pursuit + persistence

- **Un-gate the sweep UI:** `AnalysisEditorViewModel.ShowSweeps => !IsLp && !IsLpp` currently hides the
  parametric-sweep card for loadpull/pursuit. Allow it for LP/LPP. The sweep-axis row
  (`SweepAxisRowViewModel`) and `BuildAnalyses` (chain construction) are generic and should already produce a
  `ParametricSweepAnalysis` wrapping the loadpull once shown — verify and wire.
- **Variable picker:** the sweep var combo should offer `RFfreq` (any VAR). No special-casing — frequency is
  just a swept VAR; the engine's resolved-`ToneHz` axis rule (Layer B) handles the freq semantics.
- **`.cnl` round-trip:** the parametric-sweep + loadpull `.cnl` forms already exist (`analysis … type=
  loadpull … Grid=…` linked by a `parametric` sweep via `InnerAnalysisName`, with `enabled=false` on inner
  members and `Spec`/`Values` persistence). Confirm a swept-loadpull chain authored in the UI round-trips
  through `CnlWriter`/`CnlReader` and `.csch`/`.canl`.
- **Grid `.gam` picker** (LpBody/LppBody) is unchanged; the freq-block selection is an engine concern. The
  `.gam` output path picker (LppBody) is unchanged; freq blocks are written transparently.

**Gate:** author a freq-swept Loadpull and a freq-swept Pursuit in the editor; run; round-trip the `.cnl`/`.csch`.

---

## LAYER G — Display: multi-freq summary + per-freq contours (mostly free)

Once the result carries a real `"freq"` axis (Layers A/B), the existing multi-frequency machinery should
light up with little or no change:

- **Summary table:** `LoadpullSurface.Frequencies` drives the per-freq rows (already multi-freq for `.spl`).
- **Contours:** the contour trace card's frequency picker (`ShowContourFreqPicker` = `AvailableFrequencies
  .Count > 1`) already exists; verify it populates from the swept `Frequencies` and that
  `RebuildContour`/`Fit(freqIdx, …)` slices the right frequency.
- **Post-processor:** per-point enrich (Layer A) means each freq slice already has `Pout_dBm`/`Efficiency`/
  `Zin_*`/`AMPM_deg`/`IRL_dB`. `MetricAtCoord`/`RecommendedMxx` operate per `freqIdx` — no change expected.

**Gate:** a freq-swept loadpull renders a summary table with one row per frequency and lets the user switch
the contour frequency; values match a per-frequency single-run.

---

## Open decisions (call out; pick sensible defaults, note for owner)

1. **Freq match tolerance** for `.gam` block selection (default 0.1 % of `ToneHz`).
2. **No interpolation** between `.gam` freq blocks in v1 (exact/nearest). Interpolation = future.
3. **Ragged grids** (different point counts per freq): RESOLVED 2026-06-23 — `ParametricSweepEngine`
   `PadRaggedGridsToCommon`/`PadCubeTo` pad every per-freq loadpull cube up to the across-freq maximum on
   each axis (gridPoint AND pinStep) with NaN before stacking; `LoadpullSurface` drops NaN scatter points,
   so each freq's fit sees only its real terminations. This is the real case for a `.gam` generated by a
   swept pursuit (e.g. a reactive output cap makes more terminations unscorable at some freqs). Test:
   `FreqSweptLoadpullTests.FreqSweptLoadpull_RaggedPerFreqGrid_PadsAndStacks`.
4. **Per-block vs file-level `Z0`/form/format** in the `.gam` (recommend inherit-with-override).
5. **Drop stale `__Freq`** on swept runs vs leave it (harmless; the `"freq"` axis wins).
6. **Non-tone variable sweeps of loadpull** (e.g. sweep a bias): supported by the same dispatch, but the axis
   stays var-named and the surface treats it single-frequency (correct). Confirm this falls out naturally.

## Testing summary (engine-first, then UI)

- **Engine:** freq-swept Hero-3 loadpull → 3-freq `"freq"` axis (Hz) = resolved tones; `LoadpullSurface
  .Frequencies` = 3; per-freq slice equals a standalone run at that freq (within tolerance). Unit-less
  `RFfreq = 2` + `ToneUnit=GHz` axis == `2e9`. `.gam` per-freq read + write round-trip; freq-less `.gam`
  reused at all freqs. Freq-swept pursuit → freq-tagged output `.gam` + per-freq follow-on `LoadpullData`.
- **UI:** sweep card visible for LP/LPP; authored chain runs; `.cnl`/`.csch`/`.canl` round-trip; summary
  shows per-freq rows; contour freq picker switches frequency.
- Keep both repos green; no UI references leak into `RfCore`/`src/Engine`/`src/Core`.
