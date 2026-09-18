using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner report, 2026-09-17 — a restored workspace posted "Opened …/TxDirectConversion.csch" and no
/// such tab appeared.
///
/// <para>The document was in the dock, in tab order, realized in the visual tree and arranged at the
/// right x. What was wrong was the strip AROUND it: its scroll viewer was arranged at the 88px the
/// Welcome tab had occupied a moment earlier while its DesiredSize had already grown to the real
/// width, and a ScrollContentPresenter clips — so every tab past the first was cut away.
/// <c>FinishRestoredDockLayout</c> now invalidates those strips' ARRANGE.</para>
///
/// <para><b>Asserted by source scan, and that is not laziness — it is the boundary of what this
/// project can test.</b> The fact is a measured-vs-arranged disagreement inside Dock's own control
/// template, which needs a live windowing platform and a rendered frame to observe;
/// <c>Ui.Tests</c> has no Avalonia platform (see <c>DockWindowBehaviourTests</c>' own note, which
/// pins its window-behaviour facts the same way). It WAS verified on a real desktop: the window was
/// rendered to a bitmap before and after, three tabs where there had been one, on the first painted
/// frame.</para>
///
/// <para>Two things are pinned, because getting either wrong reproduces the bug in silence: the call
/// has to be on the RESTORE path, and it has to be an ARRANGE invalidation — invalidating measure
/// instead was tried against the running application and changed nothing, since re-measuring yields
/// the same DesiredSize and so asks for no new arrange.</para>
/// </summary>
public sealed class DocumentTabStripRestoreArrangeTests
{
    [Fact]
    public void TheRestoreReArrangesTheDocumentTabStrips()
    {
        string code = StripComments(File.ReadAllText(SourceFile("src/Ui/ViewModels/WorkspaceViewModel.Docking.cs")));

        var finish = Body(code, "private void FinishRestoredDockLayout");
        Assert.Contains("ReArrangeDocumentTabStrips();", finish);
    }

    [Fact]
    public void ItInvalidatesArrange_NotMeasure()
    {
        string code = StripComments(File.ReadAllText(SourceFile("src/Ui/ViewModels/WorkspaceViewModel.Docking.cs")));

        var method = Body(code, "private void ReArrangeDocumentTabStrips");
        Assert.Contains("OfType<DocumentTabStrip>()", method);
        Assert.Contains("InvalidateArrange()", method);
        Assert.DoesNotContain("InvalidateMeasure", method);
    }

    /// <summary>The source of a method, from its signature to the first line that is back at its own
    /// indentation — enough to scan one method without matching the rest of a 1,700-line partial.</summary>
    private static string Body(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' is gone — this test is about what it does, so rename it here too");

        int end = code.IndexOf("\n    }", at, StringComparison.Ordinal);
        Assert.True(end > at);
        return code.Substring(at, end - at);
    }

    private static string SourceFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(src, @"//[^\n]*", "");
    }
}
