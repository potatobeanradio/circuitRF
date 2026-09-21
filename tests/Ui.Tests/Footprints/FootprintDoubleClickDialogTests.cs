using CircuitRF.Design;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Footprints;

/// <summary>
/// Owner report, 2026-09-20: double-clicking a capacitor's land pattern in the Layout Editor opened
/// a dialog with no parameters in it, where the Properties Inspector beside it was showing the
/// footprint picker, the designator and the placement.
///
/// <para>The routing was right — a built-in land pattern IS a PCell-generated cell, so the
/// double-click correctly went to the parameter dialog rather than to push-in. What was missing is
/// that a land pattern declares NO parameters at all (its case and its density are its identity,
/// R-fp1-4c), so the one control that dialog hosted had nothing to show. Two claims are gated here:
/// the empty-parameter condition itself, and that the dialog now hosts the instance surface as
/// well, which is what it has to show instead.</para>
/// </summary>
public sealed class FootprintDoubleClickDialogTests : IDisposable
{
    private readonly string _root;

    public FootprintDoubleClickDialogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-fp-dblclick-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>The condition the bug report describes: the instance routes to the PCell dialog
    /// (<c>IsPCellInstance</c>) and yet has nothing for the parameter list to draw — so a window
    /// hosting only that list is an empty window.</summary>
    [Fact]
    public void ALandPatternIsAPCellInstanceWithNoParametersToShow()
    {
        var vm = new LayoutEditorViewModel(
            new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 },
            Path.Combine(_root, "Board", "layout", "main.clay"))
            { ActiveTool = LayoutEditorViewModel.Tool.Select };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);

        var reference = FootprintRef.For(SmtCaseTable.All[0]);
        var tech = ShippedTechnologies.Load(ShippedTechnologies.All[0]);
        string cellDir = GeneratedCellStore.GetOrCreate(
            _root, reference.ToString(), new Dictionary<string, PCellValue>(),
            tech, ShippedTechnologies.All[0].Id, PCellLayerSelection.Default);

        var inst = new LayoutInstance
        {
            CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir),
            X = 0, Y = 0, Mag = 1.0,
        };
        vm.Model.Instances.Add(inst);
        vm.SelectInstance(0);

        // Double-click dispatches on this predicate, so the dialog is what opens.
        Assert.True(LayoutHierarchyResolver.IsPCellInstance(inst, vm));

        // ...and this is why it opened on nothing.
        Assert.False(props.ShowPCellParameterList);
        Assert.True(props.PCellParamRows is null || props.PCellParamRows.Count == 0);

        // The instance surface, which the dialog now hosts too, does have something to show:
        // the footprint picker opens on this placement's own case.
        Assert.True(props.IsSingleInstanceSelected);
        Assert.NotEmpty(props.InstanceFootprintOptions);
        Assert.Equal(reference.Case.Code, vm.SelectedInstanceFootprint?.Case.Code);
    }

    /// <summary>The fix, held where it can actually be held without a display: the dialog hosts the
    /// SAME two controls the docked Properties panel embeds, and defines neither itself. A dialog
    /// that grew its own copy of either would drift from the panel silently, which is the rule
    /// <c>PCellParameterListView</c>'s own extraction already set.</summary>
    [Fact]
    public void TheDialogHostsTheInstanceSurfaceAndOwnsNoCopyOfIt()
    {
        string src = Path.Combine(RepoRoot(), "src", "Ui", "Views", "Dialogs", "LayoutPCellParameterDialog.axaml");
        string xaml = File.ReadAllText(src);

        Assert.Contains("<props:LayoutInstancePropertiesView", xaml);
        Assert.Contains("<props:PCellParameterListView", xaml);

        // Neither surface is re-declared here: the dialog's only fields are the two hosted controls
        // and its Close button.
        Assert.DoesNotContain("InstanceFootprintOptions", xaml);
        Assert.DoesNotContain("PCellParamRows", xaml);

        // And the panel hosts the same extracted control rather than a second copy of the section.
        string panel = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Ui", "Views", "Properties", "LayoutShapePropertiesView.axaml"));
        Assert.Contains("<props:LayoutInstancePropertiesView", panel);
        Assert.DoesNotContain("InstanceFootprintOptions", panel);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
