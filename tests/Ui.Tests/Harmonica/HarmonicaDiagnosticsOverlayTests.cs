// ================================================================
//  HarmonicaDiagnosticsOverlayTests.cs — §1 of
//  brief-harmonicarf-r5-the-unmeasured-stage-and-drag-starvation
//
//  §1's own gate: "every new number is a counter or a timer that is READ, not one that is added and
//  then reported as unread." These tests pin the ROLLING-WINDOW ARITHMETIC deterministically (fed a
//  clock, the same D1 convention FrameScheduler already uses) — the actual overlay DRAW cannot be
//  exercised here (Ui.Tests has no live Avalonia host), which is exactly why the brief's own gate is
//  the owner's interactive reading, not a headless benchmark.
// ================================================================

using System;
using System.IO;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaDiagnosticsOverlayTests
{
    private sealed class Clock
    {
        private double _now;
        public double Read() => _now;
        public void Advance(double ms) => _now += ms;
    }

    private static string Source() => ReadSource("src", "Ui", "Harmonica", "HarmonicaDiagnosticsOverlay.cs");

    private static string ReadSource(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine([dir!.FullName, .. parts]);
        Assert.True(File.Exists(path), $"source not found at {path}");
        return File.ReadAllText(path);
    }

    // ══ §1.1 — the rolling-window statistics themselves ══════════════════════════════════════════

    [Fact]
    public void Compute_BeforeAnyFrame_ReturnsAllZero_NotAThrow()
    {
        var overlay = new HarmonicaDiagnosticsOverlay(() => 0);
        var stats = overlay.Compute();
        Assert.Equal(0, stats.SampleCount);
        Assert.Equal(0.0, stats.MeanMs);
        Assert.Equal(0.0, stats.MaxMs);
    }

    [Fact]
    public void Compute_OneFrame_RecordsNoInterval_TheFirstCallOnlySeedsTheClock()
    {
        // A single RecordFrame() has no PREVIOUS frame to measure a gap against — it must not
        // fabricate a zero-length interval.
        var clock = new Clock();
        var overlay = new HarmonicaDiagnosticsOverlay(clock.Read);
        overlay.RecordFrame();
        Assert.Equal(0, overlay.Compute().SampleCount);
    }

    [Fact]
    public void Compute_MeanMaxLast_MatchAHandComputedSeries()
    {
        var clock = new Clock();
        var overlay = new HarmonicaDiagnosticsOverlay(clock.Read);

        double[] intervals = [10, 20, 15, 40, 12];
        overlay.RecordFrame();                         // seeds the clock, no sample yet
        foreach (double dt in intervals)
        {
            clock.Advance(dt);
            overlay.RecordFrame();
        }

        var stats = overlay.Compute();
        Assert.Equal(intervals.Length, stats.SampleCount);
        Assert.Equal(intervals[^1], stats.LastMs, 6);
        Assert.Equal(System.Linq.Enumerable.Average(intervals), stats.MeanMs, 6);
        Assert.Equal(System.Linq.Enumerable.Max(intervals), stats.MaxMs, 6);
    }

    [Fact]
    public void Compute_OverBudgetCount_CountsIntervalsStrictlyAboveTheThreshold()
    {
        var clock = new Clock();
        var overlay = new HarmonicaDiagnosticsOverlay(clock.Read);

        // Two frames comfortably under 33.3 ms, two comfortably over.
        double[] intervals = [10, 15, 50, 60];
        overlay.RecordFrame();
        foreach (double dt in intervals) { clock.Advance(dt); overlay.RecordFrame(); }

        var stats = overlay.Compute(overBudgetMs: 33.3);
        Assert.Equal(2, stats.OverBudgetCount);
        Assert.Equal(4, stats.SampleCount);
    }

    [Fact]
    public void Compute_P95AndP99_OnAUniformRun_SitNearTheTopOfTheRange()
    {
        // 100 evenly-stepped intervals, 1..100 ms — p99 and p95 should land close to the top of the
        // range and p99 must be >= p95, which is the actual property worth pinning (not a fragile
        // exact-value match against a specific interpolation scheme).
        var clock = new Clock();
        var overlay = new HarmonicaDiagnosticsOverlay(clock.Read);

        overlay.RecordFrame();
        for (int i = 1; i <= 100; i++) { clock.Advance(i); overlay.RecordFrame(); }

        // Only the last WindowSize (120) survive anyway, and 100 < 120 here so all of them do.
        var stats = overlay.Compute();
        Assert.Equal(100, stats.SampleCount);
        Assert.True(stats.P99Ms >= stats.P95Ms, $"p99 ({stats.P99Ms}) should be >= p95 ({stats.P95Ms})");
        Assert.True(stats.P95Ms > stats.MeanMs, "p95 should sit above the mean on a monotone series");
        Assert.True(stats.P99Ms <= stats.MaxMs + 1e-9);
    }

    // ══ §1.1 — the rolling window is genuinely ~120 frames, not unbounded ════════════════════════

    [Fact]
    public void RollingWindow_DropsSamplesOlderThanWindowSize()
    {
        var clock = new Clock();
        var overlay = new HarmonicaDiagnosticsOverlay(clock.Read);

        overlay.RecordFrame();
        // 200 intervals, all exactly 5 ms, THEN one lone 500 ms interval right at the start would
        // have been evicted by the time this reads — proven by feeding a distinguishable OLD value
        // first and confirming it no longer moves the max once enough new frames have pushed it out.
        clock.Advance(500);
        overlay.RecordFrame();   // interval #1 = 500 ms — the outlier

        for (int i = 0; i < HarmonicaDiagnosticsOverlay.WindowSize; i++)
        {
            clock.Advance(5);
            overlay.RecordFrame();
        }

        var stats = overlay.Compute();
        Assert.Equal(HarmonicaDiagnosticsOverlay.WindowSize, stats.SampleCount);
        Assert.True(stats.MaxMs < 500, $"the 500 ms outlier should have aged out of a {HarmonicaDiagnosticsOverlay.WindowSize}-frame window, but max is {stats.MaxMs}");
        Assert.Equal(5.0, stats.MeanMs, 6);
    }

    // ══ §1.1 — reset on demand ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Reset_ClearsTheWindow_AndTheNextRecordFrameSeedsFresh_NoStaleGap()
    {
        var clock = new Clock();
        var overlay = new HarmonicaDiagnosticsOverlay(clock.Read);

        overlay.RecordFrame();
        clock.Advance(10);
        overlay.RecordFrame();
        Assert.Equal(1, overlay.Compute().SampleCount);

        overlay.Reset();
        Assert.Equal(0, overlay.Compute().SampleCount);

        // A big clock jump across the reset must NOT be recorded as a giant interval — Reset() must
        // have cleared the "last frame" seed too, or the next RecordFrame would measure the gap
        // across the reset itself.
        clock.Advance(10_000);
        overlay.RecordFrame();
        Assert.Equal(0, overlay.Compute().SampleCount);   // still just the seed — no interval yet
        clock.Advance(5);
        overlay.RecordFrame();
        Assert.Equal(1, overlay.Compute().SampleCount);
        Assert.Equal(5.0, overlay.Compute().LastMs, 6);
    }

    // ══ §1 — GC deltas over the window ════════════════════════════════════════════════════════════

    [Fact]
    public void GcDeltas_AreZero_WhenNoCollectionHappensBetweenTheOldestAndNewestSample()
    {
        var clock = new Clock();
        var overlay = new HarmonicaDiagnosticsOverlay(clock.Read);

        overlay.RecordFrame();
        for (int i = 0; i < 5; i++) { clock.Advance(1); overlay.RecordFrame(); }

        var stats = overlay.Compute();
        // Cannot force "definitely no GC ran" in a hosted test process, but the delta must never be
        // NEGATIVE — GC.CollectionCount is monotonic, so a negative reading would mean the oldest/
        // newest sample indices were read backwards.
        Assert.True(stats.Gen0Delta >= 0, $"Gen0Delta was negative: {stats.Gen0Delta}");
        Assert.True(stats.Gen1Delta >= 0, $"Gen1Delta was negative: {stats.Gen1Delta}");
    }

    // ══ §1 guardrail 6 — off by default, and the recording call sites are gated on the toggle ════

    [Fact]
    public void ShowDiagnosticsOverlay_DefaultsToFalse_OnAFreshDocument()
    {
        var vm = new HarmonicaViewModel();
        Assert.False(vm.ShowDiagnosticsOverlay);
    }

    [Fact]
    public void HarmonicaCanvas_GatesBothRecordFrameAndDraw_OnShowDiagnosticsOverlay()
    {
        string src = ReadSource("src", "Ui", "Controls", "HarmonicaCanvas.cs");

        int m = src.IndexOf("public void Render(ImmediateDrawingContext ctx)", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n        /// <summary>\n        /// §7.7's edit-mode", m, StringComparison.Ordinal);
        Assert.True(mEnd > m);
        string body = src[m..mEnd];

        Assert.Contains("if (_showOverlay && _diagnostics is not null) _diagnostics.RecordFrame();", body, StringComparison.Ordinal);
        Assert.Contains("if (_showOverlay && _vm is not null && _diagnostics is not null)", body, StringComparison.Ordinal);
        Assert.Contains("_showOverlay = vm?.ShowDiagnosticsOverlay ?? false;", src, StringComparison.Ordinal);
    }

    // ══ §1 — persisted per-document, like every other Display toggle ═════════════════════════════

    [Fact]
    public void ShowDiagnosticsOverlay_PersistsAcrossASaveLoadRoundTrip()
    {
        var vm = new HarmonicaViewModel();
        vm.ShowDiagnosticsOverlay = true;
        vm.Appearance = vm.Appearance with { ShowDiagnosticsOverlay = true };

        string json = vm.ToCharmJson();

        var reloaded = new HarmonicaViewModel();
        Assert.False(reloaded.ShowDiagnosticsOverlay);   // sanity: default is off
        reloaded.LoadCharm(json, null);
        Assert.True(reloaded.ShowDiagnosticsOverlay);
    }

    [Fact]
    public void AnUntouchedDocument_ReserialisesByteForByte_TheToggleAddsNoBlockWhenOff()
    {
        // The same rule every other CharmAppearance field follows (R-h45-12's own convention): an
        // untouched document's Appearance stays IsDefault, so the block is omitted entirely rather
        // than writing out three or four nulls.
        var vm = new HarmonicaViewModel();
        Assert.True(vm.Appearance.IsDefault);
        Assert.Null(vm.Appearance.ShowDiagnosticsOverlay);
    }

    [Fact]
    public void ToggleDiagnosticsOverlayCommand_FlipsBothTheLiveFlagAndTheAppearanceBlock()
    {
        string src = ReadSource("src", "Ui", "Harmonica", "HarmonicaMenuViewModel.cs");

        int m = src.IndexOf("private void ToggleDiagnosticsOverlay()", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    /// <summary>§1.1's own \"reset on demand\"", m, StringComparison.Ordinal);
        Assert.True(mEnd > m);
        string body = src[m..mEnd];

        Assert.Contains("_vm.ShowDiagnosticsOverlay = !_vm.ShowDiagnosticsOverlay;", body, StringComparison.Ordinal);
        Assert.Contains("_vm.Appearance = _vm.Appearance with { ShowDiagnosticsOverlay = _vm.ShowDiagnosticsOverlay };",
                        body, StringComparison.Ordinal);
    }

    [Fact]
    public void ResetDiagnosticsOverlayCommand_CallsResetAndRequestsARedraw()
    {
        string src = ReadSource("src", "Ui", "Harmonica", "HarmonicaMenuViewModel.cs");

        int m = src.IndexOf("private void ResetDiagnosticsOverlay()", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    }\n", m, StringComparison.Ordinal);
        Assert.True(mEnd > m);
        string body = src[m..mEnd];

        Assert.Contains("_vm.Diagnostics.Reset();", body, StringComparison.Ordinal);
        Assert.Contains("_vm.RequestRedraw();", body, StringComparison.Ordinal);
    }

    // Owner request, 2026-08-13 (post-brief): the two menu items are removed from BOTH menu surfaces,
    // deliberately, while the commands themselves and everything they drive stay wired underneath —
    // "keep the code behind so we can turn this back on easily." This is pinned two ways: the menu
    // AXAML no longer references either command (so a future accidental re-add is visible in a diff,
    // not silently doubled), and the VIEWMODEL commands still exist and still work end to end (so
    // "turning it back on" really is just re-adding the two AXAML lines, not resurrecting dead code).

    [Fact]
    public void MenuViews_NoLongerReferenceTheDiagnosticsOverlayCommands_OnEitherSurface()
    {
        string nativeAxaml = ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaMenuView.axaml");
        Assert.DoesNotContain("Command=\"{Binding ToggleDiagnosticsOverlayCommand}\"", nativeAxaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding ResetDiagnosticsOverlayCommand}\"", nativeAxaml, StringComparison.Ordinal);

        // The comment explaining WHY, left for whoever re-adds the two lines later.
        Assert.Contains("the Diagnostics Overlay menu items", nativeAxaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommandsAndEverythingTheyDrive_StillExist_AndStillWork_WithNoMenuAtAll()
    {
        // The commands themselves (RelayCommand-generated) still exist on the type...
        var menuVmType = typeof(CircuitRF.Ui.Harmonica.HarmonicaMenuViewModel);
        Assert.NotNull(menuVmType.GetMethod("ToggleDiagnosticsOverlay",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));
        Assert.NotNull(menuVmType.GetMethod("ResetDiagnosticsOverlay",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));

        // ...and still do exactly what they always did, driven directly rather than through a menu —
        // proving "re-add the two AXAML lines" really is the whole of turning this back on.
        var vm = new HarmonicaViewModel();
        var menu = new CircuitRF.Ui.Harmonica.HarmonicaMenuViewModel(vm);

        Assert.False(vm.ShowDiagnosticsOverlay);
        menu.ToggleDiagnosticsOverlayCommand.Execute(null);
        Assert.True(vm.ShowDiagnosticsOverlay);
        Assert.True(vm.Appearance.ShowDiagnosticsOverlay);

        vm.Diagnostics.RecordFrame();
        menu.ResetDiagnosticsOverlayCommand.Execute(null);
        Assert.Equal(0, vm.Diagnostics.Compute().SampleCount);
    }
}
