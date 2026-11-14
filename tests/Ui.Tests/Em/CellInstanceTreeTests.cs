// brief-icp-7 — the Component ports section's cell-instance tree and the cut drawn on the layout. A planar MMIC fixture: a line cut
// by X2 (Pin mode) and one by X3 (Cell mode, a keep-out box), and X1, a Pair of two Seg cells with no pins of its own, so it is
// refused. Nothing is solved.

using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Mom;
using CircuitRF.Render;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

public sealed class CellInstanceTreeTests(CellInstanceTreeTests.Fixture fx) : IClassFixture<CellInstanceTreeTests.Fixture>
{
    private const int Dbu = 1000;
    private static readonly LayerKey M1 = new(1, 0);
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    /// <summary>Gate 1 — the tree: parents before children, indented by depth, the by-cell row counting every Seg; and each
    /// instance's verdict from the build Refresh made.</summary>
    [Fact]
    public void TheTree_HasEachInstanceUnderItsParent_AndTheBuildsVerdicts()
    {
        var vm = fx.Open(fx.Entries());
        Assert.Equal(["X2", "X3", "X1", "X1/S1", "X1/S2"], vm.ComponentInstanceRows.Select(r => r.Path));
        Assert.Equal([0, 0, 0, 1, 1], vm.ComponentInstanceRows.Select(r => r.Item.Depth));
        Assert.Equal(["every Seg — 4 placed"], vm.ComponentCellRows.Select(r => r.Text));
        Assert.Empty(vm.ComponentPortRows);                       // no footprint part: the flat list is empty, the tree is not
        Row(vm, "X2").Verdict.ShouldStartWith("cut out; 2 ports");
        Row(vm, "X3").Verdict.ShouldStartWith("cut out; 2 ports");
        Assert.True(Row(vm, "X1").IsRefused);
        Row(vm, "X1").Verdict.ShouldStartWith("left out: X1 (Pair) has no layout pins");
        Assert.False(Row(vm, "X1/S1").IsEnabled);                  // under an included instance
    }

    /// <summary>Gate 1 — checking a parent disables the rows under it and takes their entries out, in one undo step.</summary>
    [Fact]
    public void CheckingAParent_DisablesItsChildren_AndTakesTheirEntriesOut()
    {
        var vm = fx.Open([]);
        Row(vm, "X1/S1").IsIncluded = true;
        Assert.Equal(["X1/S1"], vm.Working.ComponentPorts.Select(c => c.Refdes));
        int before = vm.UndoRedo.Entries.Count();
        Row(vm, "X1").IsIncluded = true;
        Assert.Equal(["X1"], vm.Working.ComponentPorts.Select(c => c.Refdes));
        Assert.False(Row(vm, "X1/S1").IsEnabled);
        Assert.False(Row(vm, "X1/S1").IsIncluded);
        Assert.Equal(before + 1, vm.UndoRedo.Entries.Count());
        vm.UndoRedo.Undo();
        Assert.Equal(["X1/S1"], vm.Working.ComponentPorts.Select(c => c.Refdes));
        Assert.True(Row(vm, "X1/S1").IsEnabled);
    }

    /// <summary>Gate 1 — each option writes the instance's entry and one undo step: Port at, Reference, Z0, Keep, the keep-out box,
    /// and a pin's reference, Port off and Ground. The by-cell row writes the Cell entry.</summary>
    [Fact]
    public void EachOption_WritesOneEntry_AndOneUndoStep()
    {
        var vm = fx.Open([new EmComponentPort("X2")]);
        var row = Row(vm, "X2");
        EmComponentPort Entry() => Assert.Single(vm.Working.ComponentPorts, c => c.Refdes == "X2");
        void Step(Action edit, Func<EmComponentPort, bool> wrote)
        {
            int before = vm.UndoRedo.Entries.Count();
            edit();
            Assert.Equal(before + 1, vm.UndoRedo.Entries.Count());
            Assert.True(wrote(Entry()), Entry().ToString());
            Assert.Single(vm.Working.ComponentPorts, c => c.Refdes == "X2");
        }
        Step(() => row.PortAt = row.PortAtChoices[1], e => e.PortAt == EmComponentPortAt.Cell);
        Step(() => row.Reference = row.ReferenceChoices.Single(c => c.Value == "A"), e => e.Reference == "A");
        Step(() => { row.Z0Text = "25"; row.CommitZ0(); }, e => e.Z0 == "25");
        Step(() => row.KeepChoices.First().IsKept = true, e => e.KeepNames.Count == 1);
        Step(() => { row.KeepOutX0 = "-10"; row.KeepOutY0 = "-50"; row.KeepOutX1 = "210"; row.KeepOutY1 = "50"; row.CommitKeepOut(); },
             e => e.KeepOutUm == new EmKeepOut(-10, -50, 210, 50));
        var pinB = row.PinRows.Single(p => p.Pin == "B");
        Step(() => pinB.Reference = pinB.ReferenceChoices.Single(c => c.Value == EmComponentPort.GroundReference),
             e => e.PinOptions["B"].Reference == EmComponentPort.GroundReference);
        Step(() => pinB.HasPort = false, e => e.PinOptions["B"].Port == false);
        Step(() => row.PinRows.Single(p => p.Pin == "A").IsGrounded = true, e => e.GroundNames.SequenceEqual(["A"]));

        int beforeCell = vm.UndoRedo.Entries.Count();
        vm.ComponentCellRows.Single().IsIncluded = true;
        Assert.Equal(beforeCell + 1, vm.UndoRedo.Entries.Count());
        Assert.Contains(vm.Working.ComponentPorts, c => c.Cell == "Seg" && c.Refdes.Length == 0);
    }

    /// <summary>Gate 3 and R-icp7-3 — the marks: a hatched outline for the Pin-mode cut, a box for the Cell-mode one, a port at each
    /// face, X1's pins left out; a build copied before an edit is discarded whole at the layout.</summary>
    [Fact]
    public void TheMarks_DrawEachCut_AndAStaleBuildIsDiscarded()
    {
        var (layout, cem) = fx.OpenWithLayout(fx.Entries());
        Assert.True(layout.ShowComponentPortMarks("t.cem", cem.ComponentPortMarks, cem.ComponentPortMarksEditCount, cem.ComponentCutMarks, cem.SolverGeometry));
        var cuts = layout.ComponentCutMarks.ToDictionary(c => c.Path);
        Assert.False(cuts["X2"].Box);
        Assert.True(cuts["X3"].Box);
        Assert.Equal(new Bbox(Um(470), Um(520), Um(730), Um(680)), cuts["X3"].Region);
        Assert.Equal(new Bbox(Um(500), Um(-36), Um(700), Um(36)), cuts["X2"].Region);
        Assert.All(cuts["X1"].Pins, p => Assert.Equal(LayoutCutPinKind.LeftOut, p.Kind));
        Assert.Equal(4, layout.ComponentPortMarks.Count(m => m.Cut));
        Assert.Contains("X2.A: routed", cuts["X2"].Pins.Single(p => p.X == Um(500)).Tip, StringComparison.Ordinal);

        var stale = (cem.ComponentPortMarks, cem.ComponentCutMarks, cem.ComponentPortMarksEditCount);
        layout.Model.Instances[0].Y += Um(10);
        layout.Model.NotifyChanged();
        Assert.Empty(layout.ComponentCutMarks);                   // the edit clears them at once
        Assert.False(layout.ShowComponentPortMarks("t.cem", stale.ComponentPortMarks, stale.ComponentPortMarksEditCount, stale.ComponentCutMarks));
        Assert.Empty(layout.ComponentCutMarks);
        Assert.Empty(layout.ComponentPortMarks);
    }

    /// <summary>Gate 4 — the solver-geometry preview is EmGeometry.ForSetup's shapes, shape for shape: X2's own metal gone, X3's box
    /// cut out of the line.</summary>
    [Fact]
    public void TheSolverGeometryPreview_IsWhatForSetupReturns()
    {
        var (layout, cem) = fx.OpenWithLayout(fx.Entries(), geometry: true);
        Assert.True(layout.ShowComponentPortMarks("t.cem", cem.ComponentPortMarks, cem.ComponentPortMarksEditCount, cem.ComponentCutMarks, cem.SolverGeometry));
        var expected = EmGeometry.ForSetup(cem.Working, new EmLayoutSource(fx.ClayPath, layout.Model, fx.Tech, Dbu)).Shapes;
        Assert.NotNull(layout.SolverGeometry);
        Assert.Equal(expected.Select(Key), layout.SolverGeometry!.Select(Key));
        Assert.DoesNotContain(layout.SolverGeometry!, s => LayoutGeometry.BboxOf(s) is var b && b.MinX >= Um(500) && b.MaxX <= Um(700) && b.MinY >= Um(-36) && b.MaxY <= Um(36));

        static string Key(LayoutShape s) => $"{s.GetType().Name}|{s.Layer}|{LayoutGeometry.BboxOf(s)}";
    }

    private static EmComponentInstanceRow Row(EmSetupEditorViewModel vm, string path) => vm.ComponentInstanceRows.Single(r => r.Path == path);

    public sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("icp7-").FullName;
        public Technology Tech { get; } = ShippedTechnologies.Load("mmic-GaAs_2LM_100um");
        public string Workspace => _root;
        public string ClayPath { get; }
        public LayoutView View { get; }

        public Fixture()
        {
            TechPersistence.SaveToFile(Path.Combine(_root, "tech.ctech"), Tech);
            WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
            Cell("Seg", c => { Pin(c, "A", 0, 0, 180, 72); Pin(c, "B", 200, 0, 0, 72); c.Shapes.Add(R(0, -36, 200, 36)); });
            Cell("Pair", c => { c.Instances.Add(Place("Seg", 0, 0, "S1")); c.Instances.Add(Place("Seg", 0, 200, "S2")); });
            var v = new LayoutView { DbuPerMicron = Dbu };
            foreach (double y in new[] { 0.0, 600.0 })
            {
                v.Shapes.Add(R(0, y - 36, 500, y + 36));
                v.Shapes.Add(R(700, y - 36, 1200, y + 36));
            }
            v.Shapes.Add(Edge(0, 0, LayoutRotation.R0, "1"));
            v.Shapes.Add(Edge(1200, 0, LayoutRotation.R180, "2"));
            v.Shapes.Add(Edge(0, 600, LayoutRotation.R0, "3"));
            v.Shapes.Add(Edge(1200, 600, LayoutRotation.R180, "4"));
            v.Instances.Add(Place("Seg", 500, 0, "X2"));
            v.Instances.Add(Place("Seg", 500, 600, "X3"));
            v.Instances.Add(Place("Pair", 1600, 0, "X1"));
            string dir = CellFolder.CreateCellFolder(_root, "Top");
            CellCreate.WriteLayoutView(dir, "Top", v);
            ClayPath = Directory.GetFiles(dir, "*.clay", SearchOption.AllDirectories).Single();
            View = LayoutPersistence.LoadFromFile(ClayPath);
        }

        /// <summary>X2 in Pin mode, X3 in Cell mode with a keep-out box, X1 referenced to Common (refused).</summary>
        public IReadOnlyList<EmComponentPort> Entries() =>
        [
            new("X2"),
            new("X3") { PortAt = EmComponentPortAt.Cell, KeepOutUm = new EmKeepOut(-30, -80, 230, 80) },
            new("X1", Reference: EmComponentPort.CommonReference),
        ];

        public EmSetup Setup(IReadOnlyList<EmComponentPort> entries) => new()
        {
            Name = "t", LayoutRef = Path.GetRelativePath(_root, ClayPath).Replace('\\', '/'), AnalysisKind = EmAnalysisKind.Planar,
            PlanarMesh = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 10, EdgeMesh: false),
            Frequency = new FrequencySpec("10", "10", 1, SweepKind.Linear, "GHz", "GHz"),
            AdaptiveSampling = false, ComponentPorts = [.. entries],
        };

        public EmSetupEditorViewModel Open(IReadOnlyList<EmComponentPort> entries)
        {
            var source = new EmLayoutSource(ClayPath, LayoutPersistence.LoadFromFile(ClayPath), Tech, Dbu);
            var vm = new EmSetupEditorViewModel(Path.Combine(_root, "t.cem"), Setup(entries)) { ResolveLayout = _ => source };
            vm.Refresh();
            vm.WhenRefreshed.GetAwaiter().GetResult();
            vm.ComponentPartsListing.GetAwaiter().GetResult();
            return vm;
        }

        public (LayoutEditorViewModel Layout, EmSetupEditorViewModel Cem) OpenWithLayout(IReadOnlyList<EmComponentPort> entries, bool geometry = false)
        {
            var layout = new LayoutEditorViewModel(LayoutPersistence.LoadFromFile(ClayPath), ClayPath);
            var cem = new EmSetupEditorViewModel(Path.Combine(_root, "t.cem"), Setup(entries))
            {
                ResolveLayout = _ => new EmLayoutSource(ClayPath, layout.Model, Tech, Dbu),
                ComponentPortMarksSettle = TimeSpan.Zero,
            };
            if (geometry) cem.SolverGeometryWanted = true;
            cem.Refresh();
            cem.WhenRefreshed.GetAwaiter().GetResult();
            cem.ComponentPortMarksBuilding.GetAwaiter().GetResult();
            return (layout, cem);
        }

        private void Cell(string name, Action<LayoutView> draw)
        {
            string dir = CellFolder.CreateCellFolder(_root, name);
            var v = new LayoutView { DbuPerMicron = Dbu };
            draw(v);
            CellCreate.WriteLayoutView(dir, name, v);
        }

        private static void Pin(LayoutView v, string name, double x, double y, double outward, double width)
            => v.Pins.Add(new LayoutPin { Name = name, X = Um(x), Y = Um(y), WidthDbu = Um(width), OutwardDeg = outward, Layer = M1 });

        private static LayoutInstance Place(string cell, double x, double y, string id)
            => new() { CellRef = "../../" + cell, X = Um(x), Y = Um(y), Mag = 1.0, SchematicId = id };

        private static RectShape R(double x0, double y0, double x1, double y1) => new() { Layer = M1, X1 = Um(x0), Y1 = Um(y0), X2 = Um(x1), Y2 = Um(y1) };

        private static LabelShape Edge(double x, double y, LayoutRotation dir, string text) => new()
        {
            Layer = M1, PortLayer = M1, X = Um(x), Y = Um(y), Text = text, Height = Um(36), IsPort = true,
            PortKind = PlanarPortKind.Edge, PortDirection = dir, PortUseEdgeWidth = true, PortUncalibrated = true,
        };

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}

internal static class CutTreeAsserts
{
    public static void ShouldStartWith(this string actual, string prefix) => Assert.StartsWith(prefix, actual, StringComparison.Ordinal);
}
