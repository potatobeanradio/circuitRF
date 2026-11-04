using System.Numerics;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Mom;
using Xunit;
using Severity = CircuitRF.Design.Layout.SchematicSync.SchematicLayoutPlanner.ReportSeverity;
using Kind = CircuitRF.Design.Layout.SchematicSync.SchematicLayoutPlanner.ReportKind;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// brief-sdl-5 §4: schematic ports become EM ports. P1-P5 over C2, F1, C3 and C1, plus the <c>.cem</c> step — the setup
/// <see cref="EmSetupCreate"/> writes, a setup the user edited left alone, and a dry run of the created setup. Placement
/// geometry itself (edge port, at the metal's face, facing in) is held for every design by <see cref="SdlChecker"/>'s
/// <c>port</c> lines; these assert what each scenario is about.
/// </summary>
public sealed class PortPlacementTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-sdl5-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>P1: C2's four ports are edge ports 1-4 on the four launches' open ends, each linked to its Port and
    /// recorded with its number; and the setup the option creates for it passes a dry run with nothing refused.</summary>
    [Fact]
    public void P1_C2_FourEdgePortsAtTheLaunchEnds_AndItsCreatedSetupDryRuns()
    {
        var (p, layout) = Placed("C2-branch-line");
        foreach (var (id, line) in new[] { ("P1", "TL1"), ("P2", "TL2"), ("P3", "TL3"), ("P4", "TL4") })
        {
            var label = Port(layout, id);
            Assert.Equal(id[1..], label.Text);
            Assert.Null(label.PortKind);   // inferred from the artwork, as a hand-placed port's is (D1)
            Assert.Equal(id[1..], layout.GeneratedPlacements[id].Text);
            Assert.Contains($"placed at {line} pin", Assert.Single(PlanOf(p).Lines, l => l.InstanceName == id).Text);
        }

        LayoutPersistence.SaveToFile(p.ClayPath, layout);
        var em = EmSetupFromSchematic.Run(p.Model, Cws(p), p.ClayPath, "C2-branch-line");
        string cem = Assert.Single(em.Written);
        Assert.Equal(Path.Combine(p.Workspace, "em", "C2-branch-line.cem"), cem);
        var setup = EmSetupPersistence.LoadFromFile(cem);
        Assert.Equal(EmAnalysisKind.Planar, setup.AnalysisKind);
        Assert.Equal(Enumerable.Repeat(new Complex(50, 0), 4), setup.PortZ0s);

        var source = EmSetupResolver.Resolve(cem, setup.LayoutRef, Cws(p), new TechnologyCache()).Source;
        var pre = EmRunService.Preflight(setup, source);
        Assert.Null(pre.Refusal);
        Assert.Equal(EmAnalysisKind.Planar, pre.Kind);
    }

    /// <summary>P2: F1's ports sit on its two lines' open ends; in the variant with no line before the capacitor, port 1
    /// sits on the pad at its outer edge (SDL-2's joint), facing into the pad.</summary>
    [Fact]
    public void P2_F1_PortsAtTheLineEnds_AndOnAPadAtItsOuterEdge()
    {
        var (p, layout) = Placed("F1-dc-block");
        Assert.Contains("placed at TL1 pin 1", Line(PlanOf(p), "P1").Text);
        Assert.Contains("placed at TL2 pin 2", Line(PlanOf(p), "P2").Text);
        Assert.Equal(["1", "2"], layout.Shapes.OfType<LabelShape>().Where(l => l.IsPort).Select(l => l.Text).Order());

        var (q, onPad) = Placed("F1b-port-on-pad");
        Assert.Contains("placed at C1 pin 1", Line(PlanOf(q), "P1").Text);
        var c1 = onPad.Instances.Single(i => i.SchematicId == "C1");
        var joint = PadJoint.Of(CellLayoutResolver.Resolve(c1, q.LayoutDir, q.Tech).View!, q.Tech).Points[0].Joint;
        var (x, y) = LayoutInstanceTransform.TransformPoint(joint.X, joint.Y, c1, 0, 0);
        var label = Port(onPad, "P1");
        Assert.Equal((x, y), (label.X, label.Y));
        Assert.Empty(SdlChecker.Check("F1b-port-on-pad", Path.Combine(_scratch, "check")).Problems);
    }

    /// <summary>P3: a Port on the net where a tee meets a line is reported for placing by hand, and no label is placed.</summary>
    [Fact]
    public void P3_APortOnAJunctionIsReportedAndNotPlaced()
    {
        var (p, layout) = Placed("C3b-port-on-junction");
        var line = Line(PlanOf(p), "P3");
        Assert.Equal(Severity.Warning, line.Severity);
        Assert.Equal(Kind.PortByHand, line.Kind);
        Assert.Contains("junction", line.Text);
        Assert.DoesNotContain(layout.Shapes.OfType<LabelShape>(), l => l.SchematicId == "P3");
    }

    /// <summary>P4: C2 with port 3 renumbered 5 in the schematic and re-run: the label reads 5, and the setup the first
    /// run created gains an impedance for port 5 and reports 3 as gone — keeping 3's.</summary>
    [Fact]
    public void P4_ARenumberedPortChangesItsLabel_AndTheSetupGainsTheNewNumber()
    {
        var (p, layout) = Placed("C2-branch-line");
        LayoutPersistence.SaveToFile(p.ClayPath, layout);
        string cem = Assert.Single(EmSetupFromSchematic.Run(p.Model, Cws(p), p.ClayPath, "C2-branch-line").Written);

        Set(p, "P3", "Num", "5");
        var (plan, after) = Rerun(p, layout);
        Assert.Equal("5", Port(after, "P3").Text);
        Assert.Equal("P3 — port 3 renumbered to 5", Line(plan, "P3").Text);
        Assert.Equal("5", after.GeneratedPlacements["P3"].Text);

        LayoutPersistence.SaveToFile(p.ClayPath, after);
        var em = EmSetupFromSchematic.Run(p.Model, Cws(p), p.ClayPath, "C2-branch-line");
        Assert.Equal([cem], em.Written);
        var setup = EmSetupPersistence.LoadFromFile(cem);
        Assert.Equal(5, setup.PortZ0s.Count);
        Assert.Equal(new Complex(50, 0), setup.PortZ0s[4]);
        Assert.Contains(em.Lines, l => l.Text.EndsWith("port 5 added at 50 Ω", StringComparison.Ordinal));
        Assert.Contains(em.Lines, l => l.Text.EndsWith("port 3 is no longer in the schematic; its settings are kept", StringComparison.Ordinal));
    }

    /// <summary>P5: C1's middle line lengthened by 1 mm: port 2's label, on TL3's far end, moves with it by exactly the
    /// difference; a label pulled off its pin by hand is not moved, and is reported.</summary>
    [Fact]
    public void P5_APortMovesWithItsLineEnd_AndOnePulledOffStays()
    {
        var (p, layout) = Placed("C1-through-line");
        var (was, p1) = (Port(layout, "P2"), Port(layout, "P1"));
        Set(p, "TL2", "L", "4");   // 3 mm → 4 mm
        var (_, after) = Rerun(p, layout);
        var now = Port(after, "P2");
        Assert.Equal(1_000_000, Math.Abs(now.X - was.X) + Math.Abs(now.Y - was.Y));
        Assert.Equal((p1.X, p1.Y), (Port(after, "P1").X, Port(after, "P1").Y));   // the anchored side does not move
        Assert.Empty(SdlChecker.Verify(p, after));

        // Pulled 0.5 mm off its pin by hand, then the line lengthened again: the label stays where it was put.
        Port(after, "P2").Y += 500_000;
        var pulled = (Port(after, "P2").X, Port(after, "P2").Y);
        Set(p, "TL2", "L", "5");
        var (plan2, after2) = Rerun(p, after);
        Assert.Equal(pulled, (Port(after2, "P2").X, Port(after2, "P2").Y));
        Assert.Contains("is off its pin", Line(plan2, "P2").Text);

        // Re-snap puts it back on its pin.
        var (_, snapped) = Rerun(p, after2, resnap: new HashSet<string> { "P2" });
        var tl3 = snapped.Instances.Single(i => i.SchematicId == "TL3");
        var pin = CellPins.Resolve(CellLayoutResolver.Resolve(tl3, p.LayoutDir, p.Tech).View!, p.Tech).Single(q => q.Name == "2");
        Assert.Equal(LayoutInstanceTransform.TransformPoint(pin.X, pin.Y, tl3, 0, 0), (Port(snapped, "P2").X, Port(snapped, "P2").Y));
    }

    /// <summary>A deleted port label is not put back, and Restore Removed Parts puts it back; the label's text edited
    /// in the layout is kept against an unchanged schematic (D-conflict).</summary>
    [Fact]
    public void ADeletedLabelStaysDeleted_AndAnEditedNumberIsKept()
    {
        var (p, layout) = Placed("C1-through-line");
        layout.Shapes.Remove(Port(layout, "P1"));
        Port(layout, "P2").Text = "7";

        var (plan, after) = Rerun(p, layout);
        Assert.DoesNotContain(after.Shapes.OfType<LabelShape>(), l => l.SchematicId == "P1");
        Assert.Contains("removed from the layout by hand", Line(plan, "P1").Text);
        Assert.Equal("7", Port(after, "P2").Text);
        Assert.Contains("The layout's value is kept", Line(plan, "P2").Text);

        var (_, restored) = Rerun(p, after, restoreRemoved: true);
        Assert.Equal("1", Port(restored, "P1").Text);
    }

    /// <summary>A setup with an impedance the user set is left alone when the schematic's Z changes; one still holding
    /// what the command wrote takes the new value.</summary>
    [Fact]
    public void ASetupTheUserEditedIsLeftAlone()
    {
        var (p, layout) = Placed("C1-through-line");
        LayoutPersistence.SaveToFile(p.ClayPath, layout);
        string cem = Assert.Single(EmSetupFromSchematic.Run(p.Model, Cws(p), p.ClayPath, "C1-through-line").Written);
        var setup = EmSetupPersistence.LoadFromFile(cem);
        setup.PortZ0s[0] = new Complex(75, 0);   // the user's own port-1 impedance
        EmSetupPersistence.SaveToFile(cem, setup);
        string edited = File.ReadAllText(cem);

        Set(p, "P1", "Z", "25");
        var em = EmSetupFromSchematic.Run(p.Model, Cws(p), p.ClayPath, "C1-through-line");
        Assert.Empty(em.Written);
        Assert.Equal(edited, File.ReadAllText(cem));
        Assert.Contains(em.Lines, l => l.Text.Contains("port 1 is 75 Ω in the setup; the schematic says 25 Ω. The setup's value is kept"));

        Set(p, "P2", "Z", "25");
        em = EmSetupFromSchematic.Run(p.Model, Cws(p), p.ClayPath, "C1-through-line");
        Assert.Equal([cem], em.Written);
        var now = EmSetupPersistence.LoadFromFile(cem);
        Assert.Equal([new Complex(75, 0), new Complex(25, 0)], now.PortZ0s);
        Assert.Equal(setup.Frequency.StartExpr, now.Frequency.StartExpr);
    }

    /// <summary>A setup made by hand holds its impedances as the EM Setup editor writes them — the odd/even port fields,
    /// no per-port list. The user's 25 Ω on port 1 is kept, not read as a port the setup has never heard of, and the
    /// port that already agrees is given no list entry that would override those fields.</summary>
    [Fact]
    public void ASetupMadeByHand_KeepsItsPortFieldImpedance()
    {
        var (p, layout) = Placed("C1-through-line");
        LayoutPersistence.SaveToFile(p.ClayPath, layout);
        string cem = EmSetupCreate.Create(p.Workspace, "ByHand", EmSetupCreate.LayoutRefOf(p.Workspace, p.ClayPath)).Path!;
        var setup = EmSetupPersistence.LoadFromFile(cem);
        setup.Port1Z0 = new Complex(25, 0);
        EmSetupPersistence.SaveToFile(cem, setup);

        var em = EmSetupFromSchematic.Run(p.Model, Cws(p), p.ClayPath, "C1-through-line");
        var now = EmSetupPersistence.LoadFromFile(cem);
        Assert.Equal(new Complex(25, 0), now.ResolvePortZ0(0));
        Assert.Empty(now.PortZ0s);
        Assert.Contains(em.Lines, l => l.Text.EndsWith("port 1 is 25 Ω in the setup; the schematic says 50 Ω. The setup's value is kept",
                                                       StringComparison.Ordinal));
    }

    /// <summary>A setup open in the EM Setup editor is not rewritten under it: the editor's copy would save over it.</summary>
    [Fact]
    public void ASetupOpenInTheEditor_IsNotRewritten()
    {
        var (p, layout) = Placed("C1-through-line");
        LayoutPersistence.SaveToFile(p.ClayPath, layout);
        string cem = Assert.Single(EmSetupFromSchematic.Run(p.Model, Cws(p), p.ClayPath, "C1-through-line").Written);
        string was = File.ReadAllText(cem);

        Set(p, "P1", "Z", "25");
        var em = EmSetupFromSchematic.Run(p.Model, Cws(p), p.ClayPath, "C1-through-line", isOpen: path => path == Path.GetFullPath(cem));
        Assert.Empty(em.Written);
        Assert.Equal(was, File.ReadAllText(cem));
        Assert.Contains(em.Lines, l => l.Text.Contains("is open in the EM Setup editor", StringComparison.Ordinal));
    }

    /// <summary>R-sdl5-2a: what <see cref="EmSetupCreate"/> writes is the setup File ▸ New ▸ EM Setup… has always written,
    /// byte for byte, and the view model keeps no second copy of the creation.</summary>
    [Fact]
    public void EmSetupCreate_WritesTheGuisSetup_AndTheViewModelKeepsNoCopy()
    {
        Directory.CreateDirectory(_scratch);
        var created = EmSetupCreate.Create(_scratch, "Amp", "Amp/layout/Amp.clay");
        Assert.Equal(Path.Combine(_scratch, "em", "Amp.cem"), created.Path);
        Assert.Equal(EmSetupPersistence.Serialize(EmSetup.CreateNew("Amp", "Amp/layout/Amp.clay")), File.ReadAllText(created.Path!));
        Assert.Equal("An EM setup named 'Amp' already exists.", EmSetupCreate.Create(_scratch, "Amp", "x.clay").Refusal);
        Assert.Equal("Amp2", EmSetupCreate.NextFreeName(_scratch, "Amp"));

        string code = Regex.Replace(Regex.Replace(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "ViewModels", "WorkspaceViewModel.cs")),
                                                  @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
        Assert.DoesNotContain("EmSetup.CreateNew(", code);
        Assert.DoesNotContain("EmSetupPersistence.SaveToFile(", code);
        Assert.Contains("EmSetupCreate.Create(", code);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private (SdlChecker.Prepared P, LayoutView Layout) Placed(string design)
    {
        var p = SdlChecker.Prepare(design, Path.Combine(_scratch, design));
        return (p, p.Applied(p.Plan()));
    }

    private static SchematicLayoutPlan PlanOf(SdlChecker.Prepared p) => p.Plan();

    private static (SchematicLayoutPlan Plan, LayoutView After) Rerun(SdlChecker.Prepared p, LayoutView layout,
                                                                     IReadOnlySet<string>? resnap = null, bool restoreRemoved = false)
    {
        var q = p with { Target = layout };
        var plan = q.Plan(resnap: resnap, restoreRemoved: restoreRemoved);
        return (plan, q.Applied(plan));
    }

    private static SchematicLayoutPlanner.ReportLine Line(SchematicLayoutPlan plan, string id) =>
        Assert.Single(plan.Lines, l => l.InstanceName == id);

    private static void Set(SdlChecker.Prepared p, string id, string param, string expression) =>
        p.Model.Components.Single(c => c.InstanceName == id).Parameters.Single(q => q.Name == param).Expression = expression;

    private static LabelShape Port(LayoutView layout, string id) =>
        layout.Shapes.OfType<LabelShape>().Single(l => l.IsPort && l.SchematicId == id);

    private static string Cws(SdlChecker.Prepared p) => Path.Combine(p.Workspace, ".cws");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
