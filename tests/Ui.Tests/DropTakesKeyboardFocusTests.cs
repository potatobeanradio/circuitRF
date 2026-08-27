using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>A drop onto an editing canvas takes keyboard focus</b> (owner, 2026-08-26).
///
/// <para>Owner-reported: drag a component out of the Library palette onto the schematic, and it is
/// placed and SELECTED — but pressing <c>R</c> does nothing. The selection is real; the keyboard is
/// somewhere else. The drag began on a palette tile, so that is where focus still is, and every
/// editing key (R, M, arrows, Delete) is routed by the canvas control's own <c>KeyDown</c>. Nothing
/// errors and nothing looks wrong: the part sits there with selection handles on it, ignoring the
/// keyboard until the user clicks it.</para>
///
/// <para><c>OnPointerPressed</c> has always called <c>Focus()</c> on its very first line for exactly
/// this reason. A drop finishes the same gesture — the user has put something on the canvas and is
/// now working on it — so it owes the same, and the omission was in every drop handler on every
/// canvas, not just the one that got reported.</para>
///
/// <para><b>Why a source scan.</b> A <c>UserControl</c> cannot be constructed headlessly in this
/// project (the same constraint <c>SchematicMirrorContextMenuTests</c> works around by parsing the
/// real AXAML), so there is no way to raise a real <c>DragDrop.DropEvent</c> here. What CAN be held
/// shut is the rule itself: every drop handler on these canvases takes focus. That is the shape a
/// future drop target would get wrong — by being written from the neighbouring handler, which is how
/// this one spread to three files in the first place.</para>
/// </summary>
public class DropTakesKeyboardFocusTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    /// <summary>
    /// The file with comments and string literals removed. A doc comment on this very subject would
    /// otherwise satisfy the scan by talking about it — the trap every source-scan test in this repo
    /// has to step around.
    /// </summary>
    private static string CodeOf(string canvas)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Controls", canvas));
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"//[^\n]*", " ");
        text = Regex.Replace(text, "\"(?:[^\"\\\\\n]|\\\\.)*\"", "\"\"");
        return text;
    }

    /// <summary>The body of each <c>On…Drop</c> handler in the file, keyed by name.</summary>
    private static Dictionary<string, string> DropHandlers(string code)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in Regex.Matches(
                     code, @"private\s+(?:async\s+)?void\s+(On\w*Drop)\s*\([^)]*\)\s*\{"))
        {
            int i = m.Index + m.Length, depth = 1;
            while (i < code.Length && depth > 0)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}') depth--;
                i++;
            }
            found[m.Groups[1].Value] = code[(m.Index + m.Length)..(i - 1)];
        }
        return found;
    }

    [Theory]
    [InlineData("SchematicCanvas.cs",    3)]
    [InlineData("LayoutCanvas.cs",       3)]
    [InlineData("SymbolEditorCanvas.cs", 1)]
    public void EveryDropHandler_TakesKeyboardFocus(string canvas, int expectedHandlers)
    {
        var handlers = DropHandlers(CodeOf(canvas));

        Assert.Equal(expectedHandlers, handlers.Count);
        foreach (var (name, body) in handlers)
            Assert.True(body.Contains("TakeKeyboardFocus()", StringComparison.Ordinal),
                        $"{canvas}: {name} places something on the canvas but never takes the "
                      + "keyboard, so R/M/arrows/Delete go to whatever the drag started from.");
    }

    /// <summary>
    /// It has to be the FIRST thing the handler does, before any early return. Half of these
    /// handlers bail out when the payload does not parse or the drop is refused, and focus taken
    /// after that point is focus not taken on the paths that matter least — but also not taken on
    /// a refusal, where the user is left with a canvas that swallowed their drag AND their keys.
    /// </summary>
    [Theory]
    [InlineData("SchematicCanvas.cs")]
    [InlineData("LayoutCanvas.cs")]
    [InlineData("SymbolEditorCanvas.cs")]
    public void FocusIsTakenBeforeAnyEarlyReturn(string canvas)
    {
        foreach (var (name, body) in DropHandlers(CodeOf(canvas)))
        {
            int focus  = body.IndexOf("TakeKeyboardFocus()", StringComparison.Ordinal);
            int bailed = body.IndexOf("return", StringComparison.Ordinal);
            Assert.True(focus >= 0, $"{canvas}: {name} never takes the keyboard");
            Assert.True(bailed < 0 || focus < bailed,
                        $"{canvas}: {name} can return before it takes the keyboard");
        }
    }

    /// <summary>
    /// The helper really does focus the control. Without this the two scans above are satisfied by
    /// the NAME alone, which is the one thing a source scan can be fooled by.
    /// </summary>
    [Theory]
    [InlineData("SchematicCanvas.cs")]
    [InlineData("LayoutCanvas.cs")]
    [InlineData("SymbolEditorCanvas.cs")]
    public void TheHelperActuallyCallsFocus(string canvas)
        => Assert.Contains("private void TakeKeyboardFocus() => Focus();", CodeOf(canvas),
                           StringComparison.Ordinal);

    /// <summary>
    /// The schematic's cell drop is the one that awaits, and placing a cell with no symbol yet asks
    /// whether to generate one. That dialog takes focus on its way in, so the canvas has to take it
    /// back afterwards — the prompt otherwise hands it to nobody and the reported bug returns by a
    /// different route.
    /// </summary>
    [Fact]
    public void TheAwaitingCellDrop_TakesFocusAgainAfterTheDialog()
    {
        var body = DropHandlers(CodeOf("SchematicCanvas.cs"))["OnCellDrop"];
        int at    = body.IndexOf("CommitCellPlacementAsync", StringComparison.Ordinal);
        Assert.True(at >= 0);
        Assert.Contains("TakeKeyboardFocus()", body[at..], StringComparison.Ordinal);
    }
}
