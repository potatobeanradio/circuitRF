using System;
using System.Globalization;
using System.IO;
using System.Linq;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Match;

/// <summary>
/// MN-3 §1: where the Designer opens from, and the compact panel that offers it.
///
/// <para>The routing itself is view code-behind, which this project cannot exercise headlessly — so
/// it is a SOURCE scan, the same technique <c>SchematicEditParametersMenuTests</c> uses for the same
/// reason. What it gates is the thing that would actually regress: a <c>Match</c> falling back to the
/// 420 px generic dialog, silently, because the branch was removed or reordered.</para>
/// </summary>
public sealed class MatchDesignerHostingTests(ITestOutputHelper output)
{
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

    private static string StripComments(string src)
    {
        var sb = new System.Text.StringBuilder();
        foreach (string line in src.Split('\n'))
        {
            string t = line.TrimStart();
            if (t.StartsWith("//", StringComparison.Ordinal)) continue;
            int slashes = line.IndexOf("//", StringComparison.Ordinal);
            sb.AppendLine(slashes >= 0 ? line[..slashes] : line);
        }
        return sb.ToString();
    }

    [Fact]
    public void DoubleClickingAMatch_OpensTheDesigner_BeforeTheGenericDialogIsEverConstructed()
    {
        string src = StripComments(ReadSource("src", "Ui", "Views", "Content", "SchematicView.axaml.cs"));
        int opener = src.IndexOf("private void OpenParameterEditorFor(", StringComparison.Ordinal);
        Assert.True(opener >= 0);

        int branch = src.IndexOf("SymbolKind.Match", opener, StringComparison.Ordinal);
        int show = src.IndexOf("MatchDesignerWindow.Show", opener, StringComparison.Ordinal);
        int generic = src.IndexOf("new ParameterEditorDialog", opener, StringComparison.Ordinal);

        Assert.True(branch > 0, "OpenParameterEditorFor does not branch on SymbolKind.Match");
        Assert.True(show > branch, "the Match branch does not open the Designer");
        Assert.True(show < generic,
            "the Match branch runs AFTER the generic dialog is built — a Match would get both");
    }

    [Fact]
    public void TheDesignerWindow_IsNonModalResizableAndSizedAsTheDesignSays()
    {
        string xaml = ReadSource("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml");

        // The opening size is the GOLDEN RATIO (owner, 2026-08-20: "have the Match Designer window
        // size open at the Golden Ratio"). Asserted as the RATIO rather than as two literals, so the
        // claim survives a future resize and a future resize cannot quietly break the claim.
        var size = System.Text.RegularExpressions.Regex.Match(
            xaml, @"Width=""(\d+)""\s+Height=""(\d+)""");
        Assert.True(size.Success, "the window declares no Width/Height pair");
        double w = double.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
        double h = double.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);
        Assert.Equal(1.618, w / h, 2);
        Assert.Contains("MinWidth=\"1000\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"700\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CanResize=\"True\"", xaml, StringComparison.Ordinal);
        // No WindowStartupLocation in the AXAML: the Designer is shown UNOWNED (below), and
        // CenterOwner needs an Owner. ShowUnowned sets the mode and the position together.
        Assert.DoesNotContain("WindowStartupLocation=", xaml, StringComparison.Ordinal);

        // Undo/redo bind to the view-model's commands, which delegate to the SCHEMATIC's stack.
        Assert.Contains("Command=\"{Binding UndoCommand}\"", xaml, StringComparison.Ordinal);

        // ...and THIS WINDOW is opened with Show(), never ShowDialog(), and never with an OWNER.
        //
        // Non-modal was never enough on its own (owner-reported, 2026-08-20: "the Match Designer
        // window is always in front. I can't get back to the workspace with the designer window
        // open"). An owned window is kept above its owner in the z-order by every platform for as
        // long as it exists, so Show(owner) is exactly as unreachable-behind as a dialog. The
        // Designer is therefore an independent top-level, placed over the workspace by ShowUnowned
        // and closed with it. A dialog the Designer itself opens — MN-5's Flatten to Cell name prompt
        // — is still modal and rightly so, which is why the claim is asserted against the call that
        // opens the DESIGNER.
        string cs = StripComments(ReadSource("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml.cs"));
        Assert.Contains("ShowUnowned(window, owner)", cs, StringComparison.Ordinal);
        Assert.DoesNotContain("window.Show(owner", cs, StringComparison.Ordinal);
        Assert.DoesNotContain("window.ShowDialog", cs, StringComparison.Ordinal);

        // ShowUnowned's two obligations, both of which the owner had been discharging for it.
        Assert.Contains("WindowStartupLocation.Manual", cs, StringComparison.Ordinal);
        Assert.Contains("CascadedFrom(owner)", cs, StringComparison.Ordinal);
        Assert.Contains("window.Position = at;", cs, StringComparison.Ordinal);
        Assert.Contains("owner.Closed += CloseWithOwner", cs, StringComparison.Ordinal);
    }

    [Fact]
    public void OneWindowPerInstance_ReInvokingRaisesTheExistingOne()
    {
        string cs = StripComments(ReadSource("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml.cs"));
        Assert.Contains("TryGetValue(comp, out var existing)", cs, StringComparison.Ordinal);
        Assert.Contains("existing.Activate()", cs, StringComparison.Ordinal);
        Assert.Contains("Open.Remove(comp)", cs, StringComparison.Ordinal);
    }

    // ── The Properties-region panel ───────────────────────────────────────────

    private static (SchematicViewModel Vm, EditableComponent Comp, ParameterEditorViewModel Editor)
        SelectAMatch(MatchDesign design)
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        comp.Parameters.First(p => p.Name == "Design").Expression = MatchEmbedding.Encode(design);
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);
        return (vm, comp, editor);
    }

    private static MatchDesign Golden() => new()
    {
        F1 = 3.3e9, F2 = 5.0e9, Order = 4, Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(200.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
        Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 10e-12),
    };

    [Fact]
    public void ThePropertiesPanel_StatesTheBandOrderResponseBothTerminationsAndTheWorstReturnLoss()
    {
        var (_, _, editor) = SelectAMatch(Golden());

        Assert.True(editor.IsMatch);
        output.WriteLine(editor.MatchBandSummary);
        output.WriteLine(editor.MatchOrderSummary);
        output.WriteLine(editor.MatchTerm1Summary);
        output.WriteLine(editor.MatchTerm2Summary);
        output.WriteLine(editor.MatchReturnLossSummary);

        Assert.Equal("3.3 – 5 GHz", editor.MatchBandSummary);
        Assert.Contains("order 4", editor.MatchOrderSummary);
        // match.md §6.9: the family is named by its OUTCOME now, and §16.7 puts the form on the line.
        Assert.Contains("single-match", editor.MatchOrderSummary, StringComparison.Ordinal);
        Assert.Contains("bandpass", editor.MatchOrderSummary, StringComparison.Ordinal);
        Assert.Contains("200", editor.MatchTerm1Summary);
        Assert.Contains("parallel", editor.MatchTerm1Summary);
        Assert.Contains("series", editor.MatchTerm2Summary);
        // 16.66 dB is match.md §4.9's own golden number, reached here through the panel.
        Assert.Contains("worst in-band RL 16.66 dB", editor.MatchReturnLossSummary);
        // ...and the design as stored carries no transforms yet, so the far end is NOT reached —
        // which the panel says, rather than reporting a match it does not have.
        Assert.Contains("Π N² not reached", editor.MatchReturnLossSummary);
        Assert.True(editor.MatchNeedsAttention);
        Assert.Empty(editor.MatchPayloadError);

        editor.Dispose();
    }

    /// <summary>The panel is a readout of the design, so it follows an edit made anywhere.</summary>
    [Fact]
    public void ThePropertiesPanel_FollowsADesignerEdit()
    {
        var (vm, comp, editor) = SelectAMatch(Golden());
        Assert.Equal("3.3 – 5 GHz", editor.MatchBandSummary);

        var designer = new MatchDesignerViewModel();
        designer.SetTarget(vm, comp);
        designer.F2 = 6e9;

        Assert.Equal("3.3 – 6 GHz", editor.MatchBandSummary);

        designer.Dispose();
        editor.Dispose();
    }

    /// <summary>A refused design says so here too, rather than showing a blank where a number goes.</summary>
    [Fact]
    public void ThePropertiesPanel_SaysWhyWhenTheDesignRefuses()
    {
        var design = Golden();
        design.Response = ResponseShape.Bessel;
        var (_, _, editor) = SelectAMatch(design);

        output.WriteLine(editor.MatchReturnLossSummary);
        Assert.True(editor.MatchNeedsAttention);
        Assert.Contains("Bessel", editor.MatchReturnLossSummary, StringComparison.Ordinal);

        editor.Dispose();
    }

    /// <summary>Only <c>Design</c> is hidden from the generic rows; the six echoes stay readable.</summary>
    [Fact]
    public void OnlyTheDesignBlob_IsHiddenFromTheGenericRows()
    {
        var (_, _, editor) = SelectAMatch(Golden());
        var names = editor.Rows.Select(r => r.Name).ToList();
        output.WriteLine(string.Join(", ", names));

        Assert.DoesNotContain("Design", names);
        foreach (string echo in new[] { "F1", "F2", "Order", "Response", "R1", "R2" })
            Assert.Contains(echo, names);

        editor.Dispose();
    }

    // ── The theme roles ───────────────────────────────────────────────────────

    /// <summary>
    /// A colour nobody can reach is a colour that does not exist: a role has to be in
    /// <see cref="ColorRole.All"/> and to resolve in BOTH variants.
    /// </summary>
    [Fact]
    public void TheMatchColourRoles_AreReachableAndResolveInBothVariants()
    {
        foreach (string role in new[] { ColorRole.MatchAbsorbed, ColorRole.MatchNegative, ColorRole.MatchBracket })
        {
            Assert.Contains(role, ColorRole.All);
            var light = ColorTheme.BuiltIn.Resolve(role, ColorVariant.Light);
            var dark = ColorTheme.BuiltIn.Resolve(role, ColorVariant.Dark);
            output.WriteLine($"{role}: light {light.R},{light.G},{light.B},{light.A}  " +
                             $"dark {dark.R},{dark.G},{dark.B},{dark.A}");
            Assert.NotEqual(light, dark);
        }
        Assert.Equal(ColorRole.All.Count, ColorRole.All.Distinct(StringComparer.Ordinal).Count());
    }
}
