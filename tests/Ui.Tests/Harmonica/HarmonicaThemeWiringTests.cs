using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

/// <summary>
/// R-h9a-8/R-h9a-9 — <c>HarmonicaView</c> is a <c>UserControl</c> and cannot be constructed headlessly
/// in this project (see this repo's own "Testing without the Avalonia runtime" note); this pins the
/// two theme-change wiring seams by reading the real source, the same fallback
/// <c>HarmonicaDockedFocusWiringTests</c> already uses for view-level wiring that needs a live app host.
///
/// <para><b>R-h9a-8 — an OS light/dark switch must reach an ALREADY-OPEN document.</b> Before this,
/// <c>ApplyVariant()</c> ran only once, at attach time; a later <c>ActualThemeVariantChanged</c> never
/// reached a document that was already open when the switch happened.</para>
///
/// <para><b>R-h9a-9 — a circuitRF Settings-dialog colour edit (which replaces <c>ThemeService.Active</c>
/// and fires <c>ThemeService.ThemeChanged</c>) must reach an already-open document too</b> — the same
/// class of gap, on the app-wide-theme axis rather than the OS-variant axis, and fixed the same way:
/// the VIEW owns the subscription (not the view model, which has no <c>IDisposable</c>/teardown to
/// unsubscribe from a static, process-wide event), attached in <c>OnDataContextChanged</c>'s attach
/// block and detached in its detach block — mirroring <c>ActualThemeVariantChanged</c>'s own pair
/// exactly.</para>
/// </summary>
public class HarmonicaThemeWiringTests
{
    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    private static string HarmonicaViewSource() =>
        ReadRepoFile("src/Ui/Views/Harmonica/HarmonicaView.axaml.cs");

    private static string HarmonicaViewModelSource() =>
        ReadRepoFile("src/Ui/Harmonica/HarmonicaViewModel.cs");

    // ── R-h9a-9 — RenderTheme reads ThemeService.Active as its base theme ────────────────────────

    [Fact]
    public void RenderTheme_PassesThemeServiceActive_AsTheBaseTheme_NotTheBuiltInDefault()
    {
        string src = HarmonicaViewModelSource();
        Assert.Contains(
            "HarmonicaAppearanceBridge.ToRenderTheme(Appearance, Variant, ThemeService.Active);",
            src, System.StringComparison.Ordinal);
    }

    // ── R-h9a-8 — ActualThemeVariantChanged: subscribed at attach, unsubscribed at detach ────────

    [Fact]
    public void ActualThemeVariantChanged_IsSubscribedAtAttach_AndUnsubscribedAtDetach()
    {
        string src = HarmonicaViewSource();
        Assert.Contains(
            "appAttach.ActualThemeVariantChanged += OnActualThemeVariantChanged;",
            src, System.StringComparison.Ordinal);
        Assert.Contains(
            "appDetach.ActualThemeVariantChanged -= OnActualThemeVariantChanged;",
            src, System.StringComparison.Ordinal);

        // Detach must run BEFORE attach in source order, so a re-bind (e.g. tab torn off and
        // re-docked) can never end up double-subscribed.
        int detachIdx = src.IndexOf(
            "appDetach.ActualThemeVariantChanged -= OnActualThemeVariantChanged;",
            System.StringComparison.Ordinal);
        int attachIdx = src.IndexOf(
            "appAttach.ActualThemeVariantChanged += OnActualThemeVariantChanged;",
            System.StringComparison.Ordinal);
        Assert.True(detachIdx >= 0 && attachIdx >= 0 && detachIdx < attachIdx,
            "detach must precede attach in OnDataContextChanged's source order");
    }

    [Fact]
    public void OnActualThemeVariantChanged_ReRendersOnTheUiThread_ViaApplyVariantAndRefresh()
    {
        string src = HarmonicaViewSource();
        Assert.Contains(
            "private void OnActualThemeVariantChanged(object? sender, EventArgs e)",
            src, System.StringComparison.Ordinal);
        Assert.Contains(
            "Dispatcher.UIThread.Post(() => { ApplyVariant(); Refresh(); }, DispatcherPriority.Background);",
            src, System.StringComparison.Ordinal);
    }

    // ── R-h9a-9 — ThemeService.ThemeChanged: subscribed at attach, unsubscribed at detach ────────

    [Fact]
    public void ThemeServiceThemeChanged_IsSubscribedAtAttach_AndUnsubscribedAtDetach()
    {
        string src = HarmonicaViewSource();
        Assert.Contains(
            "ThemeService.ThemeChanged += OnThemeServiceChanged;",
            src, System.StringComparison.Ordinal);
        Assert.Contains(
            "ThemeService.ThemeChanged -= OnThemeServiceChanged;",
            src, System.StringComparison.Ordinal);

        int detachIdx = src.IndexOf(
            "ThemeService.ThemeChanged -= OnThemeServiceChanged;", System.StringComparison.Ordinal);
        int attachIdx = src.IndexOf(
            "ThemeService.ThemeChanged += OnThemeServiceChanged;", System.StringComparison.Ordinal);
        Assert.True(detachIdx >= 0 && attachIdx >= 0 && detachIdx < attachIdx,
            "detach must precede attach in OnDataContextChanged's source order");
    }

    [Fact]
    public void OnThemeServiceChanged_ReRendersOnTheUiThread_ViaRefreshAlone()
    {
        string src = HarmonicaViewSource();
        Assert.Contains(
            "private void OnThemeServiceChanged(object? sender, EventArgs e)",
            src, System.StringComparison.Ordinal);
        Assert.Contains(
            "Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);",
            src, System.StringComparison.Ordinal);
    }
}
