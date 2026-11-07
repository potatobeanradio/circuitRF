using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout.Em;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

// brief-lcp-3 — the Component ports section on a layout's .cem: the list, Check, the per-part rows in the Ports list, a part
// that left the layout, and where the section shows. LCP-1's board (two two-pad parts across a broken line); nothing is solved.
public sealed class CemComponentPortsPanelTests(PlanarComponentPortTests.Board board) : IClassFixture<PlanarComponentPortTests.Board>
{
    private readonly List<(string Text, bool Warning)> _posted = [];

    private EmSetupEditorViewModel Open(EmSetup setup, string? path = null)
    {
        var vm = new EmSetupEditorViewModel(path ?? Path.Combine(board.Workspace, "lcp3.cem"), setup)
        {
            ResolveLayout = _ => board.Source(),
            PostMessage = (text, warning) => _posted.Add((text, warning)),
        };
        vm.Refresh();
        vm.ComponentPartsListing.GetAwaiter().GetResult();
        return vm;
    }

    private static string Names(EmSetup s) => string.Join(",", s.ComponentPorts.Select(c => c.Refdes + (c.Z0 is { } z ? "=" + z : "")));

    /// <summary>R-lcp3-1 — ticking a part writes the list, one undo entry, and undo restores it with the box.</summary>
    [Fact]
    public void Toggling_WritesTheList_AndUndoRestoresIt()
    {
        var vm = Open(PlanarComponentPortTests.Setup([]));
        Assert.Equal(["C1", "C2"], vm.ComponentPortRows.Select(r => r.Refdes));
        vm.ComponentPortRows.Single(r => r.Refdes == "C1").IsIncluded = true;
        Assert.Equal("C1", Names(vm.Working));
        vm.UndoRedo.Undo();
        Assert.Empty(vm.Working.ComponentPorts);
        Assert.False(vm.ComponentPortRows.Single(r => r.Refdes == "C1").IsIncluded);
    }

    /// <summary>Every two-terminal part (<c>*</c>) keeps a part's own settings beside it.</summary>
    [Fact]
    public void EveryPart_KeepsAPartsOverride()
    {
        var vm = Open(PlanarComponentPortTests.Setup(["C1", "C2"]));     // C2 states Z0 = 25
        vm.AllComponentParts = true;
        Assert.Equal("*,C2=25", Names(vm.Working));
        Assert.All(vm.ComponentPortRows, r => Assert.True(r.IsIncluded));
    }

    /// <summary>R-lcp3-3 — a built component port has a row in the Ports list named by its part, with no type; its Z0 and width
    /// write the part's entry (never PortZ0s), one undo entry each.</summary>
    [Fact]
    public void AComponentPortRow_WritesItsPartsEntry()
    {
        var vm = Open(PlanarComponentPortTests.Setup(["C1", "C2"]));
        EmPortZ0Row C1() => vm.PortRows.Single(r => r.ComponentRefdes == "C1");
        Assert.Equal("Port 3 · C1", C1().Label);
        Assert.False(C1().ShowKind);
        Assert.Equal("", C1().Text);                                    // blank: the setup's Z0, shown as the placeholder
        Assert.Equal("50", C1().Z0Placeholder);
        Assert.DoesNotContain(vm.Notes, n => n.Contains("delta gap", StringComparison.OrdinalIgnoreCase));   // R-lcp3-6
        Assert.Contains(vm.Notes, n => n.StartsWith("C1: component port 3", StringComparison.Ordinal));

        C1().Text = "30";
        vm.CommitPortRow(vm.PortRows.IndexOf(C1()));
        Assert.Equal("30", vm.Working.ComponentPorts.Single(c => c.Refdes == "C1").Z0);
        Assert.Empty(vm.Working.PortZ0s);

        C1().WidthText = "500";
        vm.CommitComponentPortWidth(C1());
        Assert.Equal(500, vm.Working.ComponentPorts.Single(c => c.Refdes == "C1").WidthUm);
        Assert.Equal("500", C1().WidthText);

        vm.UndoRedo.Undo();
        var c1 = vm.Working.ComponentPorts.Single(c => c.Refdes == "C1");
        Assert.Null(c1.WidthUm);
        Assert.Equal("30", c1.Z0);
    }

    /// <summary>R-lcp3-4/-5 — an entry naming a part that is not placed keeps a marked row and is not dropped from the file;
    /// Check names it; a layout edit then marks the verdicts stale without rebuilding.</summary>
    [Fact]
    public void APartNotPlaced_KeepsAMarkedRow_AndCheckNamesIt()
    {
        var setup = PlanarComponentPortTests.Setup(["C1"]);
        setup.ComponentPorts = [.. setup.ComponentPorts, new EmComponentPort("C7", Z0: "40")];
        var vm = Open(setup);
        var c7 = vm.ComponentPortRows.Single(r => r.Refdes == "C7");
        Assert.True(c7.IsMissing);
        Assert.True(c7.IsIncluded);
        Assert.True(c7.IsMarked);

        vm.CheckComponentPortsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.Contains(_posted, m => m.Warning && m.Text.Contains("C7 is no part placed", StringComparison.Ordinal));
        Assert.Contains(_posted, m => !m.Warning && m.Text.StartsWith("C1: component port 3", StringComparison.Ordinal));
        Assert.DoesNotContain(_posted, m => m.Text.Contains("delta gap", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("1 built, 1 left out; see Messages.", vm.ComponentPortsStatus);

        vm.MarkComponentPortsStale();
        Assert.All(vm.ComponentPortRows, r => Assert.False(r.IsRefused));
        Assert.Equal("The layout has changed since the last Check.", vm.ComponentPortsStatus);

        c7.IsIncluded = false;                                           // unticked, it goes; the row stays until the next read
        Assert.Equal("C1", Names(vm.Working));
        Assert.Contains(vm.ComponentPortRows, r => r.Refdes == "C7");
    }

    /// <summary>The .cem round-trips byte for byte when nothing was changed.</summary>
    [Fact]
    public void NothingChanged_SavesTheSameBytes()
    {
        string path = Path.Combine(board.Workspace, "lcp3-roundtrip.cem");
        var setup = PlanarComponentPortTests.Setup(["C1", "C2"]);
        setup.ComponentPorts = [new EmComponentPort(EmSetup.AllComponents), new EmComponentPort("C2", "25", null, 600), new EmComponentPort("C9", Reference: "2")];
        EmSetupPersistence.SaveToFile(path, setup);
        byte[] before = File.ReadAllBytes(path);
        var vm = Open(EmSetupPersistence.LoadFromFile(path), path);
        vm.SaveCommand.Execute(null);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    /// <summary>R-lcp3-2 — shown on Planar and a driven 3D .cem, hidden on the uniform-line kernel and an electrostatic setup;
    /// a 3D .cem's Check reports the refusal until LCP-5.</summary>
    [Fact]
    public void WhereTheSectionShows()
    {
        Assert.True(Open(PlanarComponentPortTests.Setup(["C1"])).ShowComponentPorts);

        var line = PlanarComponentPortTests.Setup(["C1"]);
        line.AnalysisKind = EmAnalysisKind.CrossSection;
        Assert.False(Open(line).ShowComponentPorts);

        var statics = PlanarComponentPortTests.Setup(["C1"]);
        statics.Solver3D = Em3dSolverId.Palace;
        statics.Problem3D = Em3dProblemType.Electrostatic;
        Assert.False(Open(statics).ShowComponentPorts);

        var driven = PlanarComponentPortTests.Setup(["C1"]);
        driven.Solver3D = Em3dSolverId.Palace;
        var vm = Open(driven);
        Assert.True(vm.ShowComponentPorts);
        Assert.Equal(["C1", "C2"], vm.ComponentPortRows.Select(r => r.Refdes));
        Assert.Equal(EmSetupEditorViewModel.ComponentPortsTip3D, vm.ComponentPortsTip);
        vm.CheckComponentPortsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.Contains(_posted, m => m.Warning && m.Text == Em3dGenerator.ComponentPortsOnLayoutRefusal);
        Assert.Equal("Not built; see Messages.", vm.ComponentPortsStatus);
    }
}
