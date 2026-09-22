using System;
using System.IO;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Field report, 2026-09-22: the schematic's zoom was lost going schematic -> layout -> schematic.
/// The schematic canvas re-fitted on every fresh bind — the defect the layout canvas had until
/// 2026-09-04 (<c>LayoutCanvasViewportPersistenceTests</c>) — so a view that was re-realised came back
/// framed rather than where the user left it. The memory now lives on the session's view model, and
/// the canvas half is scanned on the same terms as the layout's, since this project stands up no
/// headless Avalonia app.
/// </summary>
public sealed class SchematicCanvasViewportPersistenceTests
{
    [Fact]
    public void AFreshSession_RemembersNothing_SoItStillGetsTheInitialFit()
        => Assert.Null(new SchematicViewModel(new SchematicEditModel(), messageSink: null).LastViewport);

    [Fact]
    public void TheCanvasRecordsEveryViewportChange_AndRestoresItOnAFreshBind()
    {
        string src = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Controls", "SchematicCanvas.cs")));

        // One funnel for every pan/zoom path, and it records.
        Assert.Single(Regex.Matches(src, @"ViewportChanged\?\.Invoke\("));
        Assert.Matches(@"(?s)private void RaiseViewportChanged\(\)\s*\{\s*RememberViewport\(\);", src);
        Assert.Matches(@"_editContext\.LastViewport = \(_panX, _panY, _zoom\);", src);

        // A canvas that has not yet established a view takes the remembered one instead of fitting.
        Assert.Matches(@"(?s)if \(!_viewportEstablished && _editContext\.LastViewport is \{ \} last\)\s*\{\s*"
                     + @"\(_panX, _panY, _zoom\) = last;\s*MarkViewportEstablished\(\);", src);
    }

    /// <summary>Comments describe the rule; only code can break it.</summary>
    private static string StripComments(string src) =>
        Regex.Replace(Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
