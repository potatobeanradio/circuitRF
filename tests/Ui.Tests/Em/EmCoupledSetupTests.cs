using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

/// <summary>
/// <b>L7b Tier C4 — the <c>.cem</c>'s per-port reference impedances (R-cpl-6).</b>
///
/// <para>The whole point of making this additive rather than replacing
/// <c>Port1Z0</c>/<c>Port2Z0</c> is that <b>every existing <c>.cem</c> keeps loading</b> — and that
/// is a claim, so it is tested against a hand-authored pre-L7b file rather than against one this
/// build wrote.</para>
/// </summary>
public class EmCoupledSetupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-cem-l7b").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }
    private string Path_(string n) => Path.Combine(_dir, n);

    // ── the near/far default, which is what a pre-L7b .cem means ──────────────────────────────

    /// <summary>
    /// D3 numbering makes port 2k−1 a near end and 2k a far end, so the pre-L7b pair keeps its exact
    /// meaning for any conductor count: odd ports take Port1Z0, even ports Port2Z0.
    /// </summary>
    [Fact]
    public void WithNoOverrides_OddPortsTakePort1Z0_AndEvenPortsTakePort2Z0()
    {
        var s = new EmSetup { Port1Z0 = new Complex(50, 0), Port2Z0 = new Complex(75, 0) };

        Assert.Equal(new Complex(50, 0), s.ResolvePortZ0(0));   // port 1 — conductor A, near
        Assert.Equal(new Complex(75, 0), s.ResolvePortZ0(1));   // port 2 — conductor A, far
        Assert.Equal(new Complex(50, 0), s.ResolvePortZ0(2));   // port 3 — conductor B, near
        Assert.Equal(new Complex(75, 0), s.ResolvePortZ0(3));   // port 4 — conductor B, far
    }

    [Fact]
    public void AnExplicitOverride_WinsForThatPortOnly()
    {
        var s = new EmSetup { Port1Z0 = new Complex(50, 0), Port2Z0 = new Complex(50, 0) };
        s.PortZ0s.AddRange([new(50, 0), new(50, 0), new(100, -5)]);

        Assert.Equal(new Complex(100, -5), s.ResolvePortZ0(2));
        Assert.Equal(new Complex(50, 0),   s.ResolvePortZ0(3));   // past the list — back to the default
    }

    // ── persistence: additive, so nothing that existed changes ────────────────────────────────

    /// <summary>
    /// <b>The C4 gate: every existing <c>.cem</c> still loads.</b> Hand-authored, with no
    /// <c>PortZ0s</c> field at all — which is exactly what every file written before L7b looks like.
    /// </summary>
    [Fact]
    public void AHandAuthoredPreL7bCem_StillLoads()
    {
        string path = Path_("legacy.cem");
        File.WriteAllText(path, """
        {
          "FormatVersion": 1,
          "Name": "legacy",
          "LayoutRef": "a.clay",
          "Frequency": { "StartExpr": "1", "StopExpr": "20", "NumPoints": 101,
                         "Mode": "PointCount", "Kind": "Linear",
                         "StartUnit": "GHz", "StopUnit": "GHz", "StepUnit": "GHz" },
          "Port1Z0Real": 50, "Port1Z0Imag": 0,
          "Port2Z0Real": 75, "Port2Z0Imag": 0,
          "Mesh": { "MinCellsAcrossWidth": 6, "EdgeCells": 3, "EdgeFractionOfWidth": 0.03,
                    "EdgeGrowthRatio": 1.7, "TruncationHeights": 20.0, "TruncationTailCells": 12 },
          "DispersionCorrection": false
        }
        """);

        var s = EmSetupPersistence.LoadFromFile(path);

        Assert.Equal("legacy", s.Name);
        Assert.Equal(new Complex(50, 0), s.Port1Z0);
        Assert.Equal(new Complex(75, 0), s.Port2Z0);
        Assert.Empty(s.PortZ0s);
        Assert.Equal(new Complex(75, 0), s.ResolvePortZ0(3));   // and the defaults still govern
    }

    /// <summary>
    /// A setup that overrides nothing must write NO <c>PortZ0s</c> field — otherwise every existing
    /// setup's file would change the first time it was opened and saved, which is the kind of
    /// gratuitous diff that makes a format feel unstable.
    /// </summary>
    [Fact]
    public void ASetupWithNoOverrides_WritesNoPortZ0sFieldAtAll()
    {
        string json = EmSetupPersistence.Serialize(new EmSetup { Name = "x" });
        Assert.DoesNotContain("PortZ0s", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PerPortOverrides_RoundTripThroughTheFile()
    {
        var s = new EmSetup { Name = "coupled" };
        s.PortZ0s.AddRange([new(50, 0), new(50, 0), new(75, 10), new(75, -10)]);

        string path = Path_("coupled.cem");
        EmSetupPersistence.SaveToFile(path, s);
        var back = EmSetupPersistence.LoadFromFile(path);

        Assert.Equal(4, back.PortZ0s.Count);
        Assert.Equal(new Complex(75, 10),  back.PortZ0s[2]);
        Assert.Equal(new Complex(75, -10), back.PortZ0s[3]);
    }

    /// <summary>A hand-edited file with a dangling half-pair must degrade to the defaults, not
    /// refuse to open — a <c>.cem</c> is text a user may edit.</summary>
    [Fact]
    public void AnOddLengthPortZ0sList_DropsTheDanglingHalfPairRatherThanThrowing()
    {
        string path = Path_("odd.cem");
        File.WriteAllText(path, """
        {
          "FormatVersion": 1, "Name": "odd", "LayoutRef": "a.clay",
          "Frequency": { "StartExpr": "1", "StopExpr": "20", "NumPoints": 101,
                         "Mode": "PointCount", "Kind": "Linear",
                         "StartUnit": "GHz", "StopUnit": "GHz", "StepUnit": "GHz" },
          "Port1Z0Real": 50, "Port1Z0Imag": 0, "Port2Z0Real": 50, "Port2Z0Imag": 0,
          "PortZ0s": [ 60, 0, 70 ],
          "Mesh": { "MinCellsAcrossWidth": 6, "EdgeCells": 3, "EdgeFractionOfWidth": 0.03,
                    "EdgeGrowthRatio": 1.7, "TruncationHeights": 20.0, "TruncationTailCells": 12 },
          "DispersionCorrection": false
        }
        """);

        var s = EmSetupPersistence.LoadFromFile(path);
        Assert.Single(s.PortZ0s);
        Assert.Equal(new Complex(60, 0), s.PortZ0s[0]);
    }

    [Fact]
    public void CloneCopiesThePortListRatherThanSharingIt()
    {
        var s = new EmSetup();
        s.PortZ0s.Add(new Complex(60, 0));

        var c = s.Clone();
        c.PortZ0s.Add(new Complex(70, 0));

        Assert.Single(s.PortZ0s);   // the original is untouched — Clone feeds the undo snapshot
        Assert.Equal(2, c.PortZ0s.Count);
    }

    // ── the panel's port list ─────────────────────────────────────────────────────────────────

    private static LayoutView CoupledPair()
    {
        var v = new LayoutView { DbuPerMicron = LayoutUnits.DefaultDbuPerMicron };
        v.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0,         X2 = 20_000_000, Y2 = 1_000_000 });
        v.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 1_500_000, X2 = 20_000_000, Y2 = 2_500_000 });
        return v;
    }

    private static LayoutView SingleLine()
    {
        var v = new LayoutView { DbuPerMicron = LayoutUnits.DefaultDbuPerMicron };
        v.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = 20_000_000, Y2 = 2_900_000 });
        return v;
    }

    private EmSetupEditorViewModel Editor(LayoutView view)
    {
        string path = Path_("panel.cem");
        var setup = new EmSetup { Name = "panel", LayoutRef = "a.clay" };
        EmSetupPersistence.SaveToFile(path, setup);
        var vm = new EmSetupEditorViewModel(path, setup)
        {
            ResolveLayout = _ => new EmLayoutSource(
                "/x/a.clay", view, StarterTechnologies.Pcb2Layer(), LayoutUnits.DefaultDbuPerMicron),
        };
        vm.Refresh();
        return vm;
    }

    /// <summary>The port COUNT is a property of the geometry (2N), never something the user types.</summary>
    [Fact]
    public void ACoupledPair_BuildsFourPortRows_LabelledInD3Order()
    {
        var vm = Editor(CoupledPair());

        Assert.Equal(4, vm.PortRows.Count);
        Assert.True(vm.ShowPortList);

        Assert.Equal([1, 2, 3, 4], vm.PortRows.Select(r => r.PortNumber));
        Assert.Contains("near end", vm.PortRows[0].Label, StringComparison.Ordinal);
        Assert.Contains("far end",  vm.PortRows[1].Label, StringComparison.Ordinal);
        Assert.Contains("near end", vm.PortRows[2].Label, StringComparison.Ordinal);
        Assert.Contains("far end",  vm.PortRows[3].Label, StringComparison.Ordinal);

        // Ports 1,2 name one conductor and 3,4 the other — the pairing D3 fixes.
        Assert.Equal(vm.Problem!.Ports[0].Conductor, vm.Problem.Ports[1].Conductor);
        Assert.Equal(vm.Problem.Ports[2].Conductor,  vm.Problem.Ports[3].Conductor);
        Assert.NotEqual(vm.Problem.Ports[0].Conductor, vm.Problem.Ports[2].Conductor);
    }

    /// <summary>A single line's two ports are fully described by the near/far pair, so the list is
    /// hidden rather than duplicating those same two fields.</summary>
    [Fact]
    public void ASingleLine_HidesThePortList()
    {
        var vm = Editor(SingleLine());
        Assert.Equal(2, vm.PortRows.Count);
        Assert.False(vm.ShowPortList);
    }

    [Fact]
    public void EditingAPortRow_CommitsUndoablyAndPadsFromTheDefaults()
    {
        var vm = Editor(CoupledPair());

        vm.PortRows[3].Text = "75";
        vm.CommitPortRow(3);

        Assert.True(vm.IsDirty);
        Assert.Equal(4, vm.Working.PortZ0s.Count);            // padded, so ports 1-3 keep their value
        Assert.Equal(new Complex(50, 0),  vm.Working.ResolvePortZ0(0));
        Assert.Equal(new Complex(75, 0),  vm.Working.ResolvePortZ0(3));

        vm.UndoCommand.Execute(null);
        Assert.Equal(new Complex(50, 0), vm.Working.ResolvePortZ0(3));
    }

    [Fact]
    public void AnUnparseablePortRow_ShowsAnErrorAndLeavesTheModelAlone()
    {
        var vm = Editor(CoupledPair());

        vm.PortRows[0].Text = "not a number";
        vm.CommitPortRow(0);

        Assert.NotNull(vm.PortRows[0].Error);
        Assert.True(vm.PortRows[0].HasError);
        Assert.Empty(vm.Working.PortZ0s);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void CommittingTheSameValue_PushesNoUndoEntry()
    {
        var vm = Editor(CoupledPair());
        vm.PortRows[2].Text = "50";
        vm.CommitPortRow(2);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    /// <summary>A per-port override must survive all the way into the built <see cref="EmProblem"/>,
    /// or the panel would be editing a value the solver never sees.</summary>
    [Fact]
    public void APerPortOverride_ReachesTheExtractedProblem()
    {
        var vm = Editor(CoupledPair());
        vm.PortRows[3].Text = "75+10j";
        vm.CommitPortRow(3);

        var ports = vm.Problem!.Ports.OrderBy(p => p.Number).ToList();
        Assert.Equal(new Complex(50, 0),  ports[0].Z0);
        Assert.Equal(new Complex(75, 10), ports[3].Z0);
    }
}
