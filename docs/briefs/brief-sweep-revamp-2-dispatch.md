# Sonnet Brief — Sweep revamp, Stage 2: dispatcher Enabled semantics (engine)

> Second of three (see `docs/design/parametric-sweep-ux.md`; Stage 1 landed). **No UX change.**
> Goal: make `Enabled` mean what the user expects end-to-end:
> - Disable the **base** analysis (e.g. DC) → nothing runs for that chain.
> - Disable a **sweep** → that axis collapses out of the result (its inner runs in its place); the sweep's
>   Start/Stop/Step is kept, the result just loses that dimension.
> - Disable **all** sweeps → just the base analysis runs (a single operating point).
> Build 0W/0E (`TreatWarningsAsErrors=true`); tests green.

## Why a shared resolver
The chain is by name: `ParametricSweepAnalysis.InnerAnalysisName` points down to a base analysis. Two places
walk it — `SchematicRunService.RunNetlist` (top-level dispatch) and `ParametricSweepEngine.Run` (per-point
recursion). Both must skip disabled sweeps identically, so the skip logic goes in one pure Core helper they
both call. `InnerAnalysisName` is immutable, so we resolve through it at run time rather than rewriting the chain.

## Part A — new resolver: `src/Core/Design/AnalysisChain.cs`
Pure, framework-free (Core only — no engine/UI deps; firewall-safe). Operates on `Analysis` + `TestBench`.
```csharp
using System.Linq;

namespace CircuitRF.Core.Design;

/// <summary>
/// Resolves parametric-sweep chains honoring <see cref="Analysis.Enabled"/>.
/// A disabled sweep "collapses": its axis is dropped and its own inner is adopted in its place.
/// A disabled base analysis makes the whole chain inert.
/// The chain is linked by <see cref="ParametricSweepAnalysis.InnerAnalysisName"/>.
/// </summary>
public static class AnalysisChain
{
    private const int MaxDepth = 64;   // cycle guard

    private static Analysis? Find(string name, TestBench tb)
        => tb.Analyses.FirstOrDefault(x => x.Name == name);

    /// <summary>
    /// The next analysis to actually run when descending into <paramref name="innerName"/>, skipping
    /// disabled parametric sweeps. Returns the first ENABLED sweep or ANY base analysis reached, or null
    /// if the name resolves to nothing.
    /// </summary>
    public static Analysis? ResolveEffectiveInner(string innerName, TestBench tb)
    {
        Analysis? a = Find(innerName, tb);
        int guard = 0;
        while (a is ParametricSweepAnalysis ps && !a.Enabled && guard++ < MaxDepth)
            a = Find(ps.InnerAnalysisName, tb);
        return a;
    }

    /// <summary>
    /// From a chain root, descend past disabled OUTER sweeps to the outermost analysis that runs
    /// (an enabled sweep, or a base). Null if it runs off the end.
    /// </summary>
    public static Analysis? ResolveEffectiveTop(Analysis root, TestBench tb)
    {
        Analysis? a = root;
        int guard = 0;
        while (a is ParametricSweepAnalysis ps && !a.Enabled && guard++ < MaxDepth)
            a = Find(ps.InnerAnalysisName, tb);
        return a;
    }

    /// <summary>
    /// True when <paramref name="top"/> bottoms out at an ENABLED base analysis after skipping disabled
    /// sweeps. A disabled base ⇒ the whole chain is inert ⇒ false.
    /// </summary>
    public static bool IsChainRunnable(Analysis top, TestBench tb)
    {
        Analysis? a = top;
        int guard = 0;
        while (a is ParametricSweepAnalysis ps && guard++ < MaxDepth)
            a = ResolveEffectiveInner(ps.InnerAnalysisName, tb);
        return a is { Enabled: true };
    }
}
```

## Part B — engine: collapse disabled inner sweeps
File: `src/Engine/ParametricSweepEngine.cs`, top of `Run`. Replace the raw inner lookup:
```csharp
        // Locate the inner analysis by name.
        var inner = tb.Analyses.FirstOrDefault(a => a.Name == sweep.InnerAnalysisName)
            ?? throw new InvalidOperationException(
                $"Parametric sweep '{sweep.Name}': inner analysis " +
                $"'{sweep.InnerAnalysisName}' not found in TestBench.");
```
with the collapse-aware resolve:
```csharp
        // Locate the inner analysis, skipping disabled sweeps (collapse): a disabled inner sweep is
        // transparent — its dimension is dropped and ITS inner runs here instead.
        var inner = AnalysisChain.ResolveEffectiveInner(sweep.InnerAnalysisName, tb)
            ?? throw new InvalidOperationException(
                $"Parametric sweep '{sweep.Name}': inner analysis " +
                $"'{sweep.InnerAnalysisName}' not found (or its chain is disabled).");
```
Nothing else in the engine changes — `RunInner`'s recursive `ParametricSweepAnalysis` case already calls
`Run`, which re-resolves at each level. The dispatcher (Part C) guarantees the base is enabled before any
dispatch, so this resolve never legitimately returns a disabled base.

## Part C — dispatcher: run effective chain roots only
File: `src/Ui/Schematic/SchematicRunService.cs`, in `RunNetlist` step 4. Replace the `innerOfSweep` set and
the dispatch loop:
```csharp
        // Names that are wrapped as the inner of a parametric sweep — run only via their sweep.
        var innerOfSweep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in tb.Analyses)
            if (a is ParametricSweepAnalysis ps && !string.IsNullOrEmpty(ps.InnerAnalysisName))
                innerOfSweep.Add(ps.InnerAnalysisName);

        foreach (var analysis in tb.Analyses)
        {
            if (!analysis.Enabled) continue;              // disabled — in tb for chain lookup only
            if (innerOfSweep.Contains(analysis.Name)) continue; // runs only via its wrapping sweep

            try
            {
                var ds = RunTypedAnalysis(analysis, nl, tb, lib, notes);
                if (ds is not null)
                {
                    var resultName = analysis is ParametricSweepAnalysis psa
                        ? RootInnerName(psa, tb)
                        : analysis.Name;
                    results.Add(new AnalysisResult(DeduplicateName(resultName, usedNames), ds));
                }
            }
            catch (Exception ex)
            {
                errors.Add($"'{analysis.Name}': {ex.Message}");
            }
        }
```
with a chain-root walk that honors Enabled (collapse + dead-chain skip):
```csharp
        // A chain "root" is an analysis that no sweep references as its inner (the outermost level).
        // We dispatch exactly one effective top per chain; everything below runs via the engine.
        var referencedAsInner = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in tb.Analyses)
            if (a is ParametricSweepAnalysis ps && !string.IsNullOrEmpty(ps.InnerAnalysisName))
                referencedAsInner.Add(ps.InnerAnalysisName);

        foreach (var root in tb.Analyses)
        {
            if (referencedAsInner.Contains(root.Name)) continue;     // not a root — runs via its outer

            // Skip disabled OUTER sweeps to find the outermost thing that actually runs.
            var top = AnalysisChain.ResolveEffectiveTop(root, tb);
            if (top is null || !top.Enabled) continue;               // whole chain disabled
            if (!AnalysisChain.IsChainRunnable(top, tb)) continue;   // base analysis disabled → nothing runs

            try
            {
                var ds = RunTypedAnalysis(top, nl, tb, lib, notes);
                if (ds is not null)
                {
                    var resultName = top is ParametricSweepAnalysis psa
                        ? RootInnerName(psa, tb)
                        : top.Name;
                    results.Add(new AnalysisResult(DeduplicateName(resultName, usedNames), ds));
                }
            }
            catch (Exception ex)
            {
                errors.Add($"'{top.Name}': {ex.Message}");
            }
        }
```
Also update `RootInnerName` (names the result after the analysis that actually runs) to skip disabled sweeps:
```csharp
    private static string RootInnerName(ParametricSweepAnalysis sweep, TestBench tb)
    {
        Analysis? cur = sweep;
        var guard = 0;
        while (cur is ParametricSweepAnalysis ps && guard++ < 64)
            cur = AnalysisChain.ResolveEffectiveInner(ps.InnerAnalysisName, tb);
        return cur?.Name ?? sweep.Name;
    }
```
Add `using CircuitRF.Core.Design;` if not already imported (it is — `ParametricSweepAnalysis` is used).

## Tests
**Core.Tests — `AnalysisChain` (pure, no engine needed).** Build synthetic TestBenches with
DC1 / SW_Vds(Inner=DC1) / SW_Vgs(Inner=SW_Vds) and toggle `Enabled`:
- all enabled → `ResolveEffectiveTop(SW_Vgs)`=SW_Vgs; `IsChainRunnable`=true; `ResolveEffectiveInner("SW_Vds")`=SW_Vds.
- SW_Vds disabled → `ResolveEffectiveInner("SW_Vds")`=DC1 (collapse); `IsChainRunnable(SW_Vgs)`=true.
- SW_Vgs disabled (SW_Vds enabled) → `ResolveEffectiveTop(SW_Vgs)`=SW_Vds.
- DC1 disabled → `IsChainRunnable(SW_Vgs)`=false.
- both sweeps disabled → `ResolveEffectiveTop(SW_Vgs)`=DC1; `IsChainRunnable`=DC1.Enabled.

**Engine.Tests (integration, small circuit that yields a node-voltage cube).** Through `ParametricSweepEngine`
/ `SchematicRunService` (whichever the existing sweep tests use):
- both sweeps enabled → result cube has both sweep axes (`Vgs` and `Vds`).
- inner (Vds) disabled → result cube has only the `Vgs` axis (dimension dropped); values equal the all-enabled
  slice at Vds's current global value.
- both disabled → result is the base DC cube (no sweep axes).
- DC disabled → no result for that chain (NoAnalysis / empty results).

**Firewall.Tests** already enforce Core has no Avalonia — `AnalysisChain` is Core-only, so it stays green.

## Gate (manual)
In the editor: disable the inner Vds sweep → run → the plot/table shows a Vgs-only sweep (no Vds dimension),
and the sweep's Start/Stop/Step is untouched when you reopen it. Disable the DC → run → nothing runs. Disable
both sweeps → run → a single DC operating point. Re-enable → full 2-D sweep returns.

## On completion
Note in the nearest CLAUDE.md: `Enabled` is now honored end-to-end via `AnalysisChain` (Core) — a disabled
sweep collapses (drops its axis, keeps its Spec), a disabled base makes the chain inert. Stage 3 (unified
editor UX with per-axis Enabled + reorder) follows.
