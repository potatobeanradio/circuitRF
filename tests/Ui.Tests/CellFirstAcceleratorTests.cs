using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gates for brief-cell-first-and-ui-fixes.md §2 (R-cc-2): Cmd+Shift+N moves from New Schematic to
/// New Cell in every one of this app's three hand-mirrored File-menu surfaces (the in-window Menu,
/// the macOS NativeMenu, and the torn-off-window File menu — see TornOffFileMenuView.axaml's own doc
/// comment on why there are three). New Schematic keeps no replacement accelerator. WorkspaceWindow
/// is a real Window subclass and cannot be constructed headlessly (the standing constraint this
/// codebase's own menu-structure tests already work around) — so, per that established precedent,
/// these gates read the real .axaml source directly rather than driving a live control.
/// </summary>
public class CellFirstAcceleratorTests
{
    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    // Grabs the <MenuItem .../>...</MenuItem> or self-closed <NativeMenuItem .../> element whose own
    // Header attribute matches, without walking into a nested submenu that might also mention the
    // same accelerator elsewhere — a simple brace/depth-free approach that works because none of
    // these specific New-submenu items nest another MenuItem inside themselves.
    private static string ExtractElement(string src, string headerValue)
    {
        var m = Regex.Match(src, $@"<(MenuItem|NativeMenuItem)\s+Header=""{Regex.Escape(headerValue)}""[^>]*?(/>|>.*?</\1>)",
            RegexOptions.Singleline);
        Assert.True(m.Success, $"Could not find an element with Header=\"{headerValue}\" in the given source.");
        return m.Value;
    }

    [Theory]
    [InlineData("src/Ui/Views/WorkspaceWindow.axaml")]
    [InlineData("src/Ui/Views/Shared/TornOffFileMenuView.axaml")]
    public void NewCell_CarriesTheCtrlShiftNAccelerator_NewSchematicDoesNot(string relativePath)
    {
        var src = ReadRepoFile(relativePath.Replace('/', Path.DirectorySeparatorChar));

        var newCell = ExtractElement(src, "New _Cell…");
        Assert.Contains("InputGesture=\"Ctrl+Shift+N\"", newCell);

        var newSchematic = ExtractElement(src, "New _Schematic");
        Assert.DoesNotContain("InputGesture=", newSchematic);
    }

    [Fact]
    public void NativeMenu_NewCell_CarriesTheMetaShiftNGesture_NewSchematicDoesNot()
    {
        var src = ReadRepoFile(Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml"));

        var newCell = ExtractElement(src, "New Cell…");
        Assert.Contains("Gesture=\"Meta+Shift+N\"", newCell);

        var newSchematic = ExtractElement(src, "New Schematic");
        Assert.DoesNotContain("Gesture=", newSchematic);
    }

    [Fact]
    public void WindowKeyBindings_CtrlShiftN_AndMetaShiftN_BothTargetNewCellInWorkspaceCommand()
    {
        var src = ReadRepoFile(Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml"));

        Assert.Contains(
            "<KeyBinding Gesture=\"Ctrl+Shift+N\"  Command=\"{Binding NewCellInWorkspaceCommand}\"/>", src);
        Assert.Contains(
            "<KeyBinding Gesture=\"Meta+Shift+N\"  Command=\"{Binding NewCellInWorkspaceCommand}\"/>", src);

        // Never bound to the scratch-schematic command anywhere in the Window.KeyBindings block.
        var keyBindingsBlock = src[src.IndexOf("<Window.KeyBindings>")..src.IndexOf("</Window.KeyBindings>")];
        Assert.DoesNotContain("NewScratchSchematicCommand", keyBindingsBlock);
    }
}
