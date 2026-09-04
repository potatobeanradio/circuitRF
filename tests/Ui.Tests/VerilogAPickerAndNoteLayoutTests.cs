using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Three shapes of the VerilogA component's parameter dialog that a user meets before anything is
/// simulated, and that a headless test cannot reach any other way: a file picker and a top-level
/// AXAML layout are both un-constructible in this project, so they are pinned by SOURCE SCAN — the
/// same fallback this codebase already uses for menu structure and AXAML wiring.
/// </summary>
public class VerilogAPickerAndNoteLayoutTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    private const string View     = "src/Ui/Views/ParameterEditor/ParameterEditorView.axaml";
    private const string CodeBehind = "src/Ui/Views/ParameterEditor/ParameterEditorView.axaml.cs";

    // ── The picker's DEFAULT filter ───────────────────────────────────────────

    [Fact]
    public void TheModelFilePicker_OffersSourceAndCompiledInItsFirstFilter()
    {
        // A picker opens on its FIRST filter. With `.osdi` alone there, a user who had just
        // downloaded a model family opened the dialog onto their own `.va` and saw it greyed out —
        // which reads as "circuitRF cannot take this file" rather than "switch the dropdown".
        string src = Read(CodeBehind);

        int start = src.IndexOf("Task<string?> PickModelFileAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0, "PickModelFileAsync should still exist.");

        int filters = src.IndexOf("FileTypeFilter", start, StringComparison.Ordinal);
        Assert.True(filters >= 0, "PickModelFileAsync should still declare a FileTypeFilter.");

        // The first Patterns list after FileTypeFilter is the default filter.
        var first = Regex.Match(src[filters..], @"Patterns\s*=\s*\[(?<pats>[^\]]*)\]");
        Assert.True(first.Success, "The first filter should declare a Patterns list.");

        string patterns = first.Groups["pats"].Value;
        Assert.Contains("*.va", patterns, StringComparison.Ordinal);
        Assert.Contains("*.osdi", patterns, StringComparison.Ordinal);
    }

    [Fact]
    public void TheModelFilePicker_StillAcceptsVamsAndTheLibraryExtensions()
    {
        // Widening the default must not have dropped anything the dialog took before.
        string src = Read(CodeBehind);
        int start = src.IndexOf("Task<string?> PickModelFileAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0, "PickModelFileAsync should still exist.");
        string body = src[start..];

        foreach (string ext in new[] { "*.vams", "*.so", "*.dll", "*.dylib" })
            Assert.Contains(ext, body, StringComparison.Ordinal);
    }

    // ── Where the compile note goes ───────────────────────────────────────────

    [Fact]
    public void TheCompileNote_IsNotRenderedInTheParameterDialog()
    {
        // It belongs in the Messages panel: it is ordinary progress rather than a problem with the
        // component, and it runs to several lines naming a compiler path and an artefact path —
        // which the dialog has no room reserved for.
        string xaml = Read(View);

        Assert.DoesNotContain("VerilogACompileNote", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("HasVerilogACompileNote", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCompileNote_IsPostedThroughTheOwningSchematicsMessageSink()
    {
        // Through the SCHEMATIC's sink, never a process-global one: a static sink posts into
        // whichever window registered last, which is the multi-window defect MW1 exists to have fixed.
        string vm = Read("src/Ui/ViewModels/ParameterEditorViewModel.cs");

        int at = vm.IndexOf("LastCompileNote", StringComparison.Ordinal);
        Assert.True(at >= 0, "The view model should still read the compile note.");

        string around = vm.Substring(at, Math.Min(400, vm.Length - at));
        Assert.Contains("_schematicVm.MessageSink", around, StringComparison.Ordinal);
    }

    // ── The notes do not paint over one another ───────────────────────────────

    [Fact]
    public void TheVerilogANotes_ShareOneGridRowThroughAStackPanel_NotAsSiblings()
    {
        // The reported bug: four TextBlocks each carrying Grid.Row="3" were laid out at the SAME
        // origin and painted over each other whenever more than one was visible — which is the
        // common case, since a file note and a thermal note both arise from choosing a file.
        // A Grid row stacks nothing by itself.
        string xaml = Read(View);

        // Direct children of the OUTER grid only — matched by their indentation, since inner grids
        // inside the scroller carry a Grid.Row="3" of their own that means something else entirely.
        int row3 = Regex.Matches(xaml, @"^        <[A-Za-z]+ Grid\.Row=""3""", RegexOptions.Multiline).Count;
        Assert.True(
            row3 == 1,
            $"Row 3 should have exactly one direct child (the notes' StackPanel); found {row3}.");

        Assert.Contains(@"<StackPanel Grid.Row=""3""", xaml, StringComparison.Ordinal);

        // And the notes it holds are still bound.
        foreach (string note in new[] { "VerilogAFileNote", "VerilogAUnknownParamsNote", "VerilogAThermalNote" })
            Assert.Contains(note, xaml, StringComparison.Ordinal);
    }
}
