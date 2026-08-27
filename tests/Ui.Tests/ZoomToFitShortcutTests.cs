using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>F is Zoom to Fit in every editor, and every Zoom to Fit button says so</b> (owner, 2026-08-26).
///
/// <para>Owner-reported: F did nothing in the Layout Editor, and its Zoom to Fit button — unlike the
/// Schematic Editor's, the Symbol Editor's and the wBond profile's — did not advertise the key. The
/// two halves are one bug: a shortcut nobody is told about is a shortcut nobody presses, and a
/// tooltip that promises one that does not work is worse.</para>
///
/// <para><b>The guard that matters</b> is not the key binding but its exclusion: a layout label is
/// typed straight into the editor, so a bare F handler would zoom the view the moment the user typed
/// an 'f' in a label. <c>SymbolEditorCanvas</c> already gates its own F on exactly this, and that
/// precedent is what the Layout Editor's now follows.</para>
///
/// <para>Source-scanned rather than exercised: no <c>UserControl</c> can be constructed headlessly in
/// this project, so a real <c>KeyEventArgs</c> cannot be raised (the constraint
/// <c>SchematicMirrorContextMenuTests</c> works around by parsing the AXAML).</para>
/// </summary>
public class ZoomToFitShortcutTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    /// <summary>Source with comments stripped — a comment mentioning <c>Key.F</c> must not pass for
    /// a binding, which is the one thing a scan like this can be fooled by.</summary>
    private static string CodeOf(params string[] parts)
    {
        var text = Read(parts);
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(text, @"//[^\n]*", " ");
    }

    /// <summary>
    /// The Layout Editor binds F to its canvas's own Zoom to Fit — the reported gap. Matched on the
    /// binding and the call together so a handler that claims the key without acting cannot pass.
    /// </summary>
    [Fact]
    public void TheLayoutEditor_BindsFToZoomToFit()
    {
        var code = CodeOf("src", "Ui", "Controls", "LayoutCanvas.cs");

        var at = Regex.Match(code, @"e\.Key == Key\.F\b[^\n]*\)\s*\{\s*ZoomToFit\(\);");
        Assert.True(at.Success,
                    "LayoutCanvas should zoom to fit on F — the key the other editors already use.");
    }

    /// <summary>
    /// …and does not steal it while a label is being typed. Without this, typing a label containing
    /// an 'f' jumps the view — the failure mode that made the Symbol Editor guard its own F.
    /// </summary>
    [Fact]
    public void TheLayoutEditorsF_IsSuppressedWhileTypingALabel()
    {
        var code = CodeOf("src", "Ui", "Controls", "LayoutCanvas.cs");

        int key = code.IndexOf("e.Key == Key.F", StringComparison.Ordinal);
        Assert.True(key >= 0);
        int at  = code.LastIndexOf("if (", key, StringComparison.Ordinal);   // the whole condition
        Assert.True(at >= 0);
        var condition = code[at..code.IndexOf('{', key)];

        Assert.Contains("IsTypingLabel", condition, StringComparison.Ordinal);
        Assert.Contains("!ctrl", condition, StringComparison.Ordinal);   // Ctrl/⌘+F stays free
    }

    /// <summary>
    /// Every Zoom to Fit button advertises the key, in one spelling. The Layout Editor's was the odd
    /// one out; the wBond EDITOR's is deliberately excluded — its button fits two canvases at once
    /// (profile and layout), so it is a different action wearing the same name.
    /// </summary>
    [Theory]
    [InlineData("Content", "SchematicView.axaml")]
    [InlineData("Content", "SymbolEditorView.axaml")]
    [InlineData("Layout",  "LayoutEditorView.axaml")]
    [InlineData("WBond",   "WBondProfileView.axaml")]
    public void EveryZoomToFitButton_AdvertisesTheKey(string folder, string view)
    {
        var xaml = Read("src", "Ui", "Views", folder, view);

        Assert.Contains("Zoom to Fit", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTip.Tip=\"Zoom to Fit\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"Zoom to Fit  (F)\"", xaml, StringComparison.Ordinal);
    }
}
