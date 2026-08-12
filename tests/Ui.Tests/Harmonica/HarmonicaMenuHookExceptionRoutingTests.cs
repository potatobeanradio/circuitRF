using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

/// <summary>
/// R-h9a-13 — every menu hook that starts async work in <c>HarmonicaView.WireMenuHooks</c> used to be
/// wired as <c>() => _ = SomeAsyncMethod();</c>: an `async Task` method's compiler-generated state
/// machine always captures a thrown exception into the returned Task rather than letting it escape
/// synchronously, so a discarded/unobserved Task loses that exception silently and irrecoverably,
/// regardless of where in the method body the throw happens. <c>RunHook</c> is the fix — it awaits the
/// operation and routes any exception into <see cref="CircuitRF.Ui.Harmonica.HarmonicaViewModel.SolveError"/>
/// (the same field every OTHER async handler in that file — Open/Save/Import/Export — already used its
/// own local try/catch to populate), so a failing menu item now reports why instead of doing nothing.
///
/// <c>HarmonicaView</c> is a `UserControl` and cannot be constructed headlessly in this suite (no
/// Avalonia runtime), so this is a source-scan test — the same fallback this file's siblings
/// (<c>HarmonicaThemeWiringTests</c>, <c>HarmonicaCopyPlotTransparencyTests</c>) already use for
/// view-level wiring that cannot be driven directly.
/// </summary>
public class HarmonicaMenuHookExceptionRoutingTests
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

    [Fact]
    public void RunHook_Exists_AwaitsTheOperation_AndRoutesAnyExceptionToSolveErrorThenRefreshes()
    {
        string src = HarmonicaViewSource();
        Assert.Contains("private async void RunHook(Func<System.Threading.Tasks.Task> op)", src, System.StringComparison.Ordinal);
        Assert.Contains("try { await op(); }", src, System.StringComparison.Ordinal);
        Assert.Contains("if (Vm is { } h) { h.SolveError = ex.Message; Refresh(); }", src, System.StringComparison.Ordinal);
    }

    /// <summary>The exact discarded-Task pattern this brief exists to remove must be gone from
    /// <c>WireMenuHooks</c> entirely — confirming the fix was applied, not merely that a helper was
    /// added alongside the old, still-broken wiring.</summary>
    [Fact]
    public void WireMenuHooks_NoLongerDiscardsAnyHookTask()
    {
        string src = WireMenuHooksBody();
        Assert.DoesNotContain("_ = ", src, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("menus.OpenDocumentHook    = () => RunHook(OpenCharmAsync);")]
    [InlineData("menus.SaveDocumentHook    = () => RunHook(() => SaveCharmAsync(saveAs: false));")]
    [InlineData("menus.SaveDocumentAsHook  = () => RunHook(() => SaveCharmAsync(saveAs: true));")]
    [InlineData("menus.ImportGamHook       = () => RunHook(ImportGamAsync);")]
    [InlineData("menus.ExportGamHook       = () => RunHook(ExportGamAsync);")]
    [InlineData("menus.ExportTestbenchHook = () => RunHook(ExportTestbenchAsync);")]
    [InlineData("menus.CopyTerminationsHook= () => RunHook(CopyTerminationsAsync);")]
    [InlineData("menus.CopyReadoutsHook    = () => RunHook(CopyReadoutsAsync);")]
    [InlineData("menus.PreferencesHook     = () => RunHook(ShowPreferencesAsync);")]
    [InlineData("menus.AddTraceHook        = () => RunHook(ShowTracePickerAsync);")]
    [InlineData("menus.SetDutHook          = () => RunHook(ShowSetDutAsync);")]
    [InlineData("menus.ExportDataHook      = () => RunHook(ExportDataAsync);")]
    [InlineData("menus.CopyPlotHook        = () => RunHook(CopyPlotAsync);")]
    public void EveryAsyncMenuHook_RoutesThroughRunHook(string expectedLine)
    {
        string src = HarmonicaViewSource();
        Assert.Contains(expectedLine, src, System.StringComparison.Ordinal);
    }

    /// <summary>The three hooks with no Task to lose (plain method-group assignments) are untouched —
    /// wrapping them in RunHook would be pointless, since a synchronous method's exception already
    /// propagates to the caller normally.</summary>
    [Theory]
    [InlineData("menus.NewDocumentHook     = NewDocument;")]
    [InlineData("menus.CloseDocumentHook   = CloseDocument;")]
    [InlineData("menus.HelpHook            = ShowHelp;")]
    public void SynchronousHooks_StayAsPlainMethodGroups(string expectedLine)
    {
        string src = HarmonicaViewSource();
        Assert.Contains(expectedLine, src, System.StringComparison.Ordinal);
    }

    private static string WireMenuHooksBody()
    {
        string src = HarmonicaViewSource();
        int start = src.IndexOf("private void WireMenuHooks(", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "WireMenuHooks method not found");
        // The method's own closing brace, at the method's own 4-space indent — stops short of
        // RunHook's preceding doc comment, which itself contains the literal discarded-Task pattern
        // as a WORDS-ABOUT-THE-BUG example, not as live wiring, and would otherwise false-positive.
        int end = src.IndexOf("\n    }\n", start, System.StringComparison.Ordinal);
        Assert.True(end >= 0, "WireMenuHooks closing brace not found");
        return src[start..end];
    }
}
