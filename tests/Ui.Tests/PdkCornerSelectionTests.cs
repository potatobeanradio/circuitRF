using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Choosing a corner, end to end: what the workspace offers, what a design records, what the panel
/// shows, and what reaches the run.
///
/// <para>Fixtures are synthetic but written in the SHAPE a kit uses — a corner file whose every
/// section binds a couple of process constants and then includes the same shared model file. That
/// shape is the whole reason a corner is a globals substitution rather than a different netlist.</para>
/// </summary>
public sealed class PdkCornerSelectionTests : IDisposable
{
    private readonly string _ws  = Path.Combine(Path.GetTempPath(), "crf-cs-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _kit;

    public PdkCornerSelectionTests()
    {
        _kit = Path.Combine(_ws, "kit");
        Directory.CreateDirectory(Path.Combine(_kit, "models"));

        File.WriteAllText(Path.Combine(_kit, "models", "caps_mod.lib"), """
            .subckt plate a b
            .param w=7u l=7u
            C1 a b c={carea*w*l}
            .ends plate
            """);

        File.WriteAllText(Path.Combine(_kit, "models", "capCorners.lib"), """
            * corners for the capacitor family
            .LIB cap_typ
            .param carea = 1.5E-15
            .param cpara = 1.0
            .include caps_mod.lib
            .ENDL cap_typ

            .LIB cap_wcs
            .param carea = 1.65E-15
            .param cpara = 1.1
            .include caps_mod.lib
            .ENDL cap_wcs
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { /* best effort */ }
    }

    private const string AxisId = "models/capCorners.lib";

    private List<CwsPdkRef> Refs() =>
    [
        new CwsPdkRef
        {
            Path     = "kit",
            Provider = "TestKit",
            Corners  =
            [
                new CwsCornerAxis
                {
                    AxisId      = AxisId,
                    DisplayName = "capCorners",
                    Options     = ["cap_typ", "cap_wcs"],
                },
            ],
        },
    ];

    private WorkspaceCornerAxis Axis() => WorkspaceCorners.From(_ws, Refs()).Single();

    // ── What the workspace offers ─────────────────────────────────────────────

    [Fact]
    public void S1_RecordedAxis_ResolvesToWhereTheKitActuallyIs()
    {
        var axis = Axis();

        Assert.Equal("TestKit", axis.Kit);
        Assert.Equal(AxisId, axis.AxisId);
        Assert.Equal("capCorners", axis.DisplayName);
        Assert.Equal(["cap_typ", "cap_wcs"], axis.Options);
        Assert.True(File.Exists(axis.AbsoluteFile));
    }

    [Fact]
    public void S2_AKitThatRecordedNoCorners_OffersNone()
    {
        // The overwhelmingly common case, and the one the Corners block must stay absent for.
        var refs = new List<CwsPdkRef> { new() { Path = "kit", Provider = "TestKit" } };

        Assert.Empty(WorkspaceCorners.From(_ws, refs));
    }

    [Fact]
    public void S3_AMovedKit_StillOffersItsAxis_SoTheSelectionIsRepairableRatherThanLost()
    {
        var refs = Refs();
        refs[0].Path = "somewhere-else";

        var axis = WorkspaceCorners.From(_ws, refs).Single();

        Assert.Equal(AxisId, axis.AxisId);
        Assert.False(File.Exists(axis.AbsoluteFile));
    }

    [Fact]
    public void S3b_TwoAxesSharingAName_AreQualifiedByWhatActuallyDiffers_AndAUniqueOneIsNot()
    {
        // THE SHAPE THAT OCCURS IN PRACTICE, and the reason the first attempt at this was wrong: it files its
        // corner files one directory per simulator flavour and then a `models` folder inside EACH,
        // so every path ends in the same leaf. Qualifying by the folder leaf produced two rows both
        // reading "models · capCorners" — a qualifier that does not qualify.
        var refs = Refs();
        refs[0].Corners!.Clear();
        refs[0].Corners!.Add(new CwsCornerAxis
        {
            AxisId      = "sim-a/models/capCorners.lib",
            DisplayName = "capCorners",
            Options     = ["cap_typ", "cap_wcs"],
        });
        refs[0].Corners!.Add(new CwsCornerAxis
        {
            AxisId      = "sim-b/models/capCorners.lib",
            DisplayName = "capCorners",
            Options     = ["cap_typ"],
        });
        refs[0].Corners!.Add(new CwsCornerAxis
        {
            AxisId      = "sim-a/models/resCorners.lib",
            DisplayName = "resCorners",
            Options     = ["res_typ"],
        });

        var axes = WorkspaceCorners.From(_ws, refs);

        var capLabels = axes.Where(a => a.DisplayName == "capCorners")
                            .Select(a => a.Label).OrderBy(s => s, StringComparer.Ordinal).ToList();

        Assert.Equal(["capCorners (sim-a)", "capCorners (sim-b)"], capLabels);

        // The property that actually matters, stated on its own so it cannot regress into a
        // qualifier that is merely PRESENT rather than distinguishing.
        Assert.Equal(2, capLabels.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // A name that is already unique is left alone — qualifying every row would make the common
        // case read worse to fix a case that is not there.
        Assert.Equal("resCorners", axes.Single(a => a.DisplayName == "resCorners").Label);
    }

    [Theory]
    // The kit: differs only in the middle segment.
    [InlineData("sim-a/models/capCorners.lib", "sim-b/models/capCorners.lib", "sim-a")]
    // Differs at the leaf.
    [InlineData("models/capCorners.lib", "other/capCorners.lib", "models")]
    // Differs at the root.
    [InlineData("a/models/capCorners.lib", "b/models/capCorners.lib", "a")]
    // More than one segment differs — both are needed to tell them apart.
    [InlineData("x/p/q/capCorners.lib", "x/r/s/capCorners.lib", "p/q")]
    // Nothing to say: same directory, so the path cannot separate them.
    [InlineData("models/capCorners.lib", "models/capCorners.lib", "")]
    public void S3c_TheQualifierIsTheSmallestPartOfThePathThatTellsThemApart(
        string mine, string other, string expected)
    {
        Assert.Equal(expected, WorkspaceCorners.DistinguishingSegments(mine, [mine, other]));
    }

    // ── What choosing one binds ───────────────────────────────────────────────

    [Fact]
    public void S4_ChoosingASection_BindsThatSectionsOwnValues()
    {
        var problems = new List<string>();
        var typ = WorkspaceCorners.BindingsFor(
            [Axis()], new Dictionary<string, string> { [Axis().Key] = "cap_typ" }, problems);
        var wcs = WorkspaceCorners.BindingsFor(
            [Axis()], new Dictionary<string, string> { [Axis().Key] = "cap_wcs" }, problems);

        Assert.Empty(problems);
        Assert.Equal("1.5E-15",  Value(typ, "carea"));
        Assert.Equal("1.65E-15", Value(wcs, "carea"));

        // A picker that binds the same thing whatever you choose looks exactly like success.
        Assert.NotEqual(Value(typ, "cpara"), Value(wcs, "cpara"));
    }

    [Fact]
    public void S5_NoSelection_BindsTheKitsNominalCorner_AndReportsNothing()
    {
        // THE REPORTED BUG. A kit states its process constants ONLY inside a corner section — this
        // fixture's `carea` is bound by cap_typ and cap_wcs and by nothing else, exactly as the real
        // kit's `cap_carea` is. So "nothing chosen" binding nothing left the model card referring to
        // a name no scope defined, and the design failed to elaborate. Left alone, an axis applies
        // the kit's own nominal corner: the section it lists first.
        var problems = new List<string>();

        var bound = WorkspaceCorners.BindingsFor([Axis()], new Dictionary<string, string>(), problems);

        Assert.Equal("1.5E-15", Value(bound, "carea"));   // cap_typ, the first-listed section
        Assert.Empty(problems);
    }

    [Fact]
    public void S6a_ASelectionOnAnAxisTheWorkspaceNoLongerOffers_IsReported()
    {
        // The kit was removed. The stale selection is named — but the axes that ARE offered still
        // bind their nominal corners, or removing one kit would stop every other one simulating.
        var problems = new List<string>();

        var bound = WorkspaceCorners.BindingsFor(
            [Axis()],
            new Dictionary<string, string> { ["SomeOtherKit|models/capCorners.lib"] = "cap_typ" },
            problems);

        Assert.NotEmpty(problems);
        Assert.Equal("1.5E-15", Value(bound, "carea"));
    }

    [Fact]
    public void S6b_ASectionTheKitNoLongerDeclares_IsRefused_NotQuietlyReplacedByTheNominalOne()
    {
        // A stale CHOICE is deliberately NOT healed. Substituting the nominal corner would leave the
        // design at a corner nobody chose with every number plausible — the one outcome this whole
        // mechanism exists to prevent. It is reported and nothing is bound for that axis, so the run
        // fails loudly rather than answering for a different corner.
        var problems = new List<string>();

        var bound = WorkspaceCorners.BindingsFor(
            [Axis()],
            new Dictionary<string, string> { [Axis().Key] = "cap_from_an_older_kit" },
            problems);

        Assert.Empty(bound);
        Assert.NotEmpty(problems);
    }

    [Fact]
    public void S7_AMovedKitsCornerIsReported_RatherThanQuietlyRunningAtTheDefault()
    {
        var refs = Refs();
        refs[0].Path = "somewhere-else";
        var axis = WorkspaceCorners.From(_ws, refs).Single();

        var problems = new List<string>();
        var bound = WorkspaceCorners.BindingsFor(
            [axis], new Dictionary<string, string> { [axis.Key] = "cap_typ" }, problems);

        Assert.Empty(bound);
        Assert.NotEmpty(problems);
    }

    // ── What the design records ───────────────────────────────────────────────

    [Fact]
    public void S8_ACornerSelection_RoundTripsThroughCsch()
    {
        var m = new SchematicEditModel();
        m.CornerSelections["TestKit|models/capCorners.lib"] = "cap_wcs";

        var reloaded = SchematicPersistence.Deserialize(SchematicPersistence.Serialize(m), null).model;

        Assert.Equal("cap_wcs", reloaded.CornerSelections["TestKit|models/capCorners.lib"]);
    }

    [Fact]
    public void S9_ADesignWithNoCornerSelected_SerializesWithNoCornerBlockAtAll()
    {
        // A design that never opened the Corners block must re-serialize byte-identically.
        var json = SchematicPersistence.Serialize(new SchematicEditModel());

        Assert.DoesNotContain("CornerSelections", json, StringComparison.Ordinal);
    }

    [Fact]
    public void S10_SettingACorner_IsOneUndoableEdit_AndClearingItRemovesTheEntry()
    {
        var schVm = new SchematicViewModel(new SchematicEditModel(), messageSink: null);
        string key = Axis().Key;

        schVm.Execute(new SetCornerSelectionCommand(schVm.EditModel, key, "cap_wcs"));
        Assert.Equal("cap_wcs", schVm.EditModel.CornerSelections[key]);

        schVm.UndoRedo.Undo();
        Assert.False(schVm.EditModel.CornerSelections.ContainsKey(key));

        schVm.UndoRedo.Redo();
        Assert.Equal("cap_wcs", schVm.EditModel.CornerSelections[key]);

        // Back to the kit's own defaults REMOVES the entry, so "no corner chosen" has exactly one
        // representation and the design writes no corner block.
        schVm.Execute(new SetCornerSelectionCommand(schVm.EditModel, key, null));
        Assert.Empty(schVm.EditModel.CornerSelections);
    }

    // ── What the panel shows ──────────────────────────────────────────────────

    [Fact]
    public void S11_NoAxes_MeansNoCornersBlockAtAll()
    {
        var vm = new AnalysesListViewModel();
        vm.SetActiveSchematic(new SchematicViewModel(new SchematicEditModel(), messageSink: null));

        Assert.False(vm.HasCorners);
        Assert.Empty(vm.CornerRows);
    }

    [Fact]
    public void S12_AxesButNoSchematic_StillMeansNoBlock()
    {
        var vm = new AnalysesListViewModel();
        vm.SetCornerAxes([Axis()]);

        Assert.False(vm.HasCorners);
    }

    [Fact]
    public void S13_PickingACorner_CommitsUndoably_AndUndoMovesTheComboBack()
    {
        var schVm = new SchematicViewModel(new SchematicEditModel(), messageSink: null);
        var vm    = new AnalysesListViewModel();
        vm.SetActiveSchematic(schVm);
        vm.SetCornerAxes([Axis()]);

        Assert.True(vm.HasCorners);
        var row = Assert.Single(vm.CornerRows);
        Assert.Null(row.SelectedOption!.Section);      // starts at the kit's own defaults

        row.SelectedOption = row.Options.Single(o => o.Section == "cap_wcs");
        Assert.Equal("cap_wcs", schVm.EditModel.CornerSelections[row.Axis.Key]);

        schVm.UndoRedo.Undo();
        Assert.Null(row.SelectedOption!.Section);      // the combo follows the model, not the click
        Assert.Empty(schVm.EditModel.CornerSelections);
    }

    [Fact]
    public void S14_ARecordedCornerTheKitNoLongerOffers_IsShown_NotSilentlyRevertedToTheDefault()
    {
        var m = new SchematicEditModel();
        m.CornerSelections[Axis().Key] = "cap_from_an_older_kit";

        var vm = new AnalysesListViewModel();
        vm.SetActiveSchematic(new SchematicViewModel(m, messageSink: null));
        vm.SetCornerAxes([Axis()]);

        var row = Assert.Single(vm.CornerRows);
        Assert.True(row.IsStale);
        Assert.Equal("cap_from_an_older_kit", row.SelectedOption!.Section);
    }

    [Fact]
    public void S17_TheCornersBlockStartsCollapsed_AndSaysSoWithoutBeingOpened()
    {
        // A kit declares twelve axes. Open by default, they push the analyses themselves off
        // the panel — which is exactly what was reported. Collapsed is the default; the header still
        // has to answer "is anything set here?" or collapsing it would just hide the answer.
        var vm = new AnalysesListViewModel();
        vm.SetActiveSchematic(new SchematicViewModel(new SchematicEditModel(), messageSink: null));
        vm.SetCornerAxes([Axis()]);

        Assert.False(vm.CornersExpanded);
        Assert.Equal("1 available · using kit defaults", vm.CornersSummaryText);
    }

    [Fact]
    public void S18_TheSummaryCountsWhatIsSet_AndNamesAStaleOne()
    {
        var schVm = new SchematicViewModel(new SchematicEditModel(), messageSink: null);
        var vm    = new AnalysesListViewModel();
        vm.SetActiveSchematic(schVm);
        vm.SetCornerAxes([Axis()]);

        vm.CornerRows[0].SelectedOption =
            vm.CornerRows[0].Options.Single(o => o.Section == "cap_wcs");

        Assert.Equal("1 available · 1 set", vm.CornersSummaryText);

        // Undo puts it back to the kit's own defaults, and the summary follows the model.
        schVm.UndoRedo.Undo();
        Assert.Equal("1 available · using kit defaults", vm.CornersSummaryText);

        // A corner the kit no longer offers is called out, not silently counted as "set".
        var stale = new SchematicEditModel();
        stale.CornerSelections[Axis().Key] = "cap_from_an_older_kit";
        var vm2 = new AnalysesListViewModel();
        vm2.SetActiveSchematic(new SchematicViewModel(stale, messageSink: null));
        vm2.SetCornerAxes([Axis()]);

        Assert.Equal("1 available · 1 set · 1 no longer offered", vm2.CornersSummaryText);
    }

    // ── What reaches the run ──────────────────────────────────────────────────

    [Fact]
    public void S15_CornerConstants_ReachTheTestbenchsGlobals()
    {
        var problems = new List<string>();
        var vars = WorkspaceCorners.BindingsFor(
            [Axis()], new Dictionary<string, string> { [Axis().Key] = "cap_wcs" }, problems);

        var result = NetExtractor.Extract(new SchematicEditModel(), "tb", cells: null,
                                          cornerVariables: vars);

        Assert.Equal("1.65E-15",
            result.TestBench.GlobalVariables.Single(v => v.Name == "carea").Expression);
    }

    [Fact]
    public void S16_ADesignsOwnVariableWins_AndTheCollisionIsReported()
    {
        // A corner constant is a kit's statement about its process; a variable the user wrote is a
        // statement about their design, and the design is the thing being simulated.
        var vars = new List<Variable> { new("carea", "9.9E-15") };

        var m = new SchematicEditModel();
        m.Components.Add(MakeVar("carea", "1.0E-15"));

        var result = NetExtractor.Extract(m, "tb", cells: null, cornerVariables: vars);

        Assert.Equal("1.0E-15",
            result.TestBench.GlobalVariables.Single(v => v.Name == "carea").Expression);
        Assert.Contains(result.Conflicts, c => c.Contains("carea", StringComparison.Ordinal));
    }

    private static EditableComponent MakeVar(string name, string expr)
    {
        var c = new EditableComponent
        {
            InstanceName = "VAR1",
            Symbol       = SymbolKind.Var,
            X            = 0,
            Y            = 0,
        };
        c.Parameters.Add(new EditableParameter { Name = name, Expression = expr });
        return c;
    }

    private static string Value(IEnumerable<Variable> vars, string name)
        => vars.Single(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Expression;
}
