// brief-em3d-83 §9 — field plots are document objects (pixels were not seen; every gate reads the records, the tree, the
// banner and the geometry the viewer built):
//
//   1  two plots (a ClipPlane cut, a Faces plot with a side) save, reopen, and draw the same triangles and colour range
//   2  adding, editing, hiding and renaming a plot leaves the run current: no stale banner, and the same geometry script
//   3  each inspector edit is one undo entry, and undo restores what is drawn
//   4  a frequency the run did not save is reported naming the saved ones, draws nothing, and a run that saves it clears it
//   5  a plot names its solution by value: a re-run that saves one more step below it leaves it on its frequency
//   6  the tree lists the plots in both groupings, and Show all / Hide all include them
//   7  the toolbar's Fields strip is retired (held by a source scan, not remembered)
//
// Gate 8 (no geometry on a phase step) is FieldFaceTests.Gate6, which now draws through a plot.
// The driven runs of gates 4 and 5 are the committed cavity fixture's steps re-listed under a driven collection — its data,
// not new data.

using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.Viewer3D;
using CircuitRF.Ui.ThreeD;
using static CircuitRF.Ui.ThreeD.C3dEditorViewModel;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(Viewer3DCollection.Name)]
public sealed class FieldPlotTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-em83-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static string Cavity => Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "fields", "cavity");

    // ── 1. round trip ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_TwoPlots_SaveAndReopen_DrawTheSameTrianglesAndRange()
    {
        var vm = Open(Eigenmode());
        Run(vm);
        vm.NewFieldPlot();                                                       // a cut on z through the middle, mode 1, |E|
        var cavity = vm.Viewer.Scene.Objects.Single(o => o.Name == "cavity");
        int ymin = Enumerable.Range(0, cavity.FaceNames.Count).Single(i => cavity.FaceName(i) == "ymin");
        Assert.Null(vm.PlotFieldOnFace(cavity.Id, ymin, side: 1));               // a new Faces plot, with a side
        Assert.Equal(["Field1", "Field2"], vm.Document.FieldPlots.Select(p => p.Name));
        Assert.Equal(C3dFieldSide.Top, vm.Document.FieldPlots[1].Faces.Single().Side);
        var before = new[] { Drawn(vm, "Field1"), Drawn(vm, "Field2") };

        string text = C3dPersistence.Serialize(vm.Document);
        Assert.Equal(text, C3dPersistence.Serialize(C3dPersistence.Deserialize(text)));        // byte for byte
        Assert.Contains("\"FieldPlots\"", text);
        C3dPersistence.SaveToFile(vm.FilePath, vm.Document);

        var again = Open(vm.FilePath);
        Run(again);
        var after = new[] { Drawn(again, "Field1"), Drawn(again, "Field2") };
        output.WriteLine(string.Join("; ", before.Select(d => $"{d.Vertices} vertices, {d.Lo:G6}..{d.Hi:G6}")));
        Assert.All(before, d => Assert.True(d.Vertices > 0));
        Assert.Equal(before, after);
    }

    // ── 2. not stale ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_PlotEdits_LeaveTheRunCurrent_AndTheGeometryScriptUnchanged()
    {
        var vm = Open(Eigenmode());
        string geo = GeometryScript(vm);
        Run(vm);
        Assert.Null(vm.FieldsStaleText);

        vm.NewFieldPlot();
        Assert.Null(vm.SetFieldPlot("Field1", "edit", p => p.Percentile = 95));
        vm.SetPlotShown("Field1", false);
        Assert.Null(vm.RenameFieldPlot("Field1", "Cut"));
        Assert.Equal(4, vm.UndoEntries);
        Assert.Null(vm.FieldsStaleText);                                         // the banner never came up
        Assert.Equal(geo, GeometryScript(vm));                                    // Gmsh's input: the mesh is reused

        // The control: a model edit IS stale, through the same comparison.
        vm.ChangeObjects("move", [0], o => ((C3dBox)o).Min = new C3dPoint3(1000, 0, 0));
        Assert.NotNull(vm.FieldsStaleText);
    }

    // ── 3. undo ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_EachInspectorEdit_IsOneUndoEntry_AndUndoRestoresTheDrawing()
    {
        var vm = Open(Eigenmode());
        Run(vm);
        vm.NewFieldPlot();
        var z = Drawn(vm, "Field1");
        Assert.Same(vm.SelectedFieldPlot, vm.FieldPlot("Field1"));
        Assert.True(vm.Properties.IsFieldPlot);

        int entries = vm.UndoEntries;
        long builds = vm.Viewer.FieldGeometryBuilds;
        vm.Properties.PlotAxis = C3dAxis.X;                                      // cut across x instead
        Assert.Equal(entries + 1, vm.UndoEntries);
        Until(() => vm.Viewer.FieldGeometryBuilds > builds, "the x cut was never drawn");
        var x = Settled(vm);
        Assert.NotEqual(z.Vertices, x.Vertices);

        builds = vm.Viewer.FieldGeometryBuilds;
        vm.Properties.PlotDb = true;
        Assert.Equal(entries + 2, vm.UndoEntries);

        vm.UndoRedo.Undo();
        vm.UndoRedo.Undo();
        Assert.Equal(C3dAxis.Z, vm.FieldPlot("Field1")!.Axis);
        Until(() => vm.Viewer.FieldGeometryBuilds > builds + 0 && Settled(vm).Vertices == z.Vertices, "undo never drew the z cut again");
        Assert.Equal(z, Settled(vm));

        // brief-cxp-5 — ticking Smooth redraws the plot at once, as one undo entry, with nothing else done
        entries = vm.UndoEntries;
        builds = vm.Viewer.FieldGeometryBuilds;
        vm.Properties.PlotSmooth = true;
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.True(vm.FieldPlot("Field1")!.Smooth);
        Until(() => vm.Viewer.FieldGeometryBuilds > builds, "ticking Smooth never redrew the plot");
        Assert.Equal(z.Vertices, Settled(vm).Vertices);                          // the same triangles: only their values move
    }

    // ── 4. missing data ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_AFrequencyTheRunDidNotSave_IsReported_UntilARunSavesIt()
    {
        var vm = Open(Driven());
        string dir = DrivenRun(vm, 2, 10);
        vm.NewFieldPlot();
        Assert.Null(vm.SetFieldPlot("Field1", "6 GHz", p => p.Solution = new C3dFieldSolution { GHz = 6 }));
        Until(() => vm.Viewer.FieldPlotProblem is not null, "the missing frequency was never reported");
        string why = vm.Viewer.FieldPlotProblem!;
        output.WriteLine(why);
        Assert.StartsWith("The run saved 2, 10 GHz; this plot shows 6 GHz.", why);
        Assert.Equal(why, vm.Tree.Single(g => g.Role == C3dTreeGroupRole.FieldPlots).Items.Single().Refusal);
        Assert.Empty(vm.Viewer.FieldGeometry.Vertices);
        Assert.Equal(6, vm.FieldPlot("Field1")!.Solution!.GHz);                  // never re-pointed to the nearest

        // Other frequency…: the setup saves it too — the centre it saved by default kept — and the plot waits for the run.
        Assert.Null(vm.PlotOtherFrequency("Field1", 6));
        Assert.Equal([6.0, 10.0], C3dSetups.Read(vm.Document).Single().Setup!.Palace!.SaveFieldsGHz!);
        DrivenRun(vm, 2, 6, 10, into: dir);
        Until(() => vm.Viewer.FieldPlotProblem is null && vm.Viewer.FieldGeometry.Vertices.Length > 0, "the saved frequency was never drawn");
        Assert.Null(vm.Tree.Single(g => g.Role == C3dTreeGroupRole.FieldPlots).Items.Single().Refusal);
    }

    // ── 5. by value ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_ARunThatSavesMore_LeavesThePlotOnItsFrequency()
    {
        var vm = Open(Driven());
        string dir = DrivenRun(vm, 2, 10);
        vm.NewFieldPlot();
        Assert.Null(vm.SetFieldPlot("Field1", "10 GHz", p => p.Solution = new C3dFieldSolution { GHz = 10 }));
        Until(() => vm.Viewer.SelectedFieldSolution?.Label == "10 GHz" && vm.Viewer.FieldGeometry.Vertices.Length > 0, "10 GHz was never drawn");
        Assert.Equal(1, vm.Viewer.FieldSolutions.IndexOf(vm.Viewer.SelectedFieldSolution!));

        DrivenRun(vm, 2, 6, 10, into: dir);                                       // index 1 is 6 GHz now
        Until(() => vm.Viewer.FieldSolutions.Count == 3 && vm.Viewer.FieldGeometry.Vertices.Length > 0, "the new run was never read");
        Assert.Equal("10 GHz", vm.Viewer.SelectedFieldSolution!.Label);
        Assert.Equal(10, vm.FieldPlot("Field1")!.Solution!.GHz);
    }

    // ── the Inspector's pickers and offset slider ────────────────────────────────────────────

    /// <summary>A pick commits and the commit reloads the Inspector from inside the ComboBox's own selection change; refilling
    /// that ComboBox's rows there left it showing a blank box over the right selection. A reload keeps rows that did not change.</summary>
    [Fact]
    public void APick_LeavesEveryPickersRowsInPlace()
    {
        var vm = Open(Eigenmode());
        Run(vm);
        vm.NewFieldPlot();
        var pr = vm.Properties;
        int resets = 0;
        void Count(object? _, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => resets++;
        pr.PlotSetups.CollectionChanged += Count;
        pr.PlotSolutions.CollectionChanged += Count;
        pr.PlotQuantities.CollectionChanged += Count;
        pr.PlotTargets.CollectionChanged += Count;

        int entries = vm.UndoEntries;
        pr.PlotQuantity = pr.PlotQuantities.First(q => q != pr.PlotQuantity);
        pr.PlotTarget = pr.PlotTargets[1];
        pr.PlotSolution = pr.PlotSolutions.First(s => !s.IsOther && s != pr.PlotSolution);
        Assert.Equal(entries + 3, vm.UndoEntries);
        Assert.Equal(0, resets);
    }

    /// <summary>A re-run whose step offers more than the last one's (brief-emi-11: an EMerge run gaining J_s on its surfaces) lists
    /// the new quantities in the Inspector of the plot already selected: the read after a run refreshes the pickers, not only the
    /// plot's problem sentence, so nobody has to select another row and come back.</summary>
    [Fact]
    public void AReRun_ThatOffersMore_RefreshesTheSelectedPlotsQuantityPicker()
    {
        var vm = Open(Eigenmode());
        string dir = Em3dRunService.RunDirectory(Path.Combine(_root, "results"), vm.ActiveRunSetup!, Em3dSolverId.Palace);
        CopyDirectory(Cavity, dir);
        Directory.Delete(Path.Combine(dir, "postpro", "paraview", "eigenmode_boundary"), true);
        Run(vm);
        vm.NewFieldPlot();
        Assert.DoesNotContain(vm.Properties.PlotQuantities, q => q.Quantity == "J_s");

        Directory.Delete(dir, true);
        CopyDirectory(Cavity, dir);
        foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(1));
        Run(vm);
        Until(() => vm.Properties.PlotQuantities.Any(q => q.Quantity == "J_s"), "the selected plot's Quantity picker never offered J_s");
    }

    /// <summary>The offset slider draws the plane where it is dragged, writes nothing until released, then writes one entry; the
    /// toolbar's section is not the plot's and does not move.</summary>
    [Fact]
    public void TheOffsetSlider_PreviewsWhileDragged_AndCommitsOneEntryOnRelease()
    {
        var vm = Open(Eigenmode());
        Run(vm);
        vm.NewFieldPlot();
        var pr = vm.Properties;
        Assert.True(pr.PlotIsClip && pr.PlotHasOffsetRange);
        Drawn(vm, "Field1");
        long before = vm.FieldPlot("Field1")!.Offset!.Value;
        int entries = vm.UndoEntries;
        var section = (vm.Viewer.ClipEnabled, vm.Viewer.ClipAxis, vm.Viewer.ClipPosition);

        // Two ticks with no time between them: the first tick's build is drawn, not cancelled by the second, and the second
        // plane is built after it — a drag faster than the builds still draws slices as it goes.
        long builds = vm.Viewer.FieldGeometryBuilds;
        pr.PlotOffsetSlider = pr.PlotOffsetMin + 0.2 * (pr.PlotOffsetMax - pr.PlotOffsetMin);
        double dragged = pr.PlotOffsetMin + 0.25 * (pr.PlotOffsetMax - pr.PlotOffsetMin);
        pr.PlotOffsetSlider = dragged;
        Until(() => vm.Viewer.FieldGeometryBuilds >= builds + 2, "a drag's slices were not both drawn");
        Assert.Equal(section, (vm.Viewer.ClipEnabled, vm.Viewer.ClipAxis, vm.Viewer.ClipPosition));   // independent of the plot
        Assert.Equal(before, vm.FieldPlot("Field1")!.Offset);                     // the document does not
        Assert.Equal(entries, vm.UndoEntries);

        pr.CommitPlotOffsetSlider();
        Assert.Equal((long)Math.Round(dragged), vm.FieldPlot("Field1")!.Offset);
        Assert.Equal(entries + 1, vm.UndoEntries);
        vm.UndoRedo.Undo();
        Assert.Equal(before, vm.FieldPlot("Field1")!.Offset);
        Assert.Equal(before, pr.PlotOffsetSlider);
    }

    /// <summary>The legend is anchored to the right edge and sized to its widest line, whose range text changes length as the
    /// slider moves: its width is held (only grows) while one plot's one quantity is drawn, and starts again on another.</summary>
    [Fact]
    public void TheLegendsWidth_IsHeldThroughADrag_AndStartsAgainForAnotherQuantity()
    {
        var vm = Open(Eigenmode());
        Run(vm);
        vm.NewFieldPlot();
        Drawn(vm, "Field1");
        Assert.Equal(200, vm.Viewer.HeldLegendWidth(200));
        Assert.Equal(200, vm.Viewer.HeldLegendWidth(170));                       // a shorter range line: the box stays put

        long builds = vm.Viewer.FieldGeometryBuilds;
        vm.Properties.PlotQuantity = vm.Properties.PlotQuantities.First(q => q != vm.Properties.PlotQuantity);
        Until(() => vm.Viewer.FieldGeometryBuilds > builds, "the other quantity was never drawn");
        Assert.Equal(170, vm.Viewer.HeldLegendWidth(170));
    }

    /// <summary>A ClipPlane plot's slice is on the plot's own plane: the view's section plane (the toolbar's) does not cut it
    /// (the shader's field block, <c>fmode.w</c>). A Surfaces plot's paint is on the model's faces and is cut with them.</summary>
    [Fact]
    public void AClipPlanePlot_IsNotCutByTheViewsSection_ASurfacesPlotIs()
    {
        var vm = Open(Eigenmode());
        Run(vm);
        vm.NewFieldPlot();
        Until(() => vm.Viewer.FieldGeometryBuilds > 0 && vm.Viewer.FieldScale is not null, "the slice was never drawn");
        Assert.Equal(1f, vm.Viewer.View.Field[7]);

        long builds = vm.Viewer.FieldGeometryBuilds;
        Assert.Null(vm.SetFieldPlot("Field1", "on surfaces", p => p.On = CircuitRF.Design.ThreeD.C3dFieldPlotOn.Surfaces));
        Until(() => vm.Viewer.FieldGeometryBuilds > builds, "the surfaces were never drawn");
        Assert.Equal(0f, vm.Viewer.View.Field[7]);
    }

    // ── 6. the tree ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_TheTree_ListsPlots_InBothGroupings_AndShowHideAllIncludeThem()
    {
        var vm = Open(Eigenmode());
        Run(vm);
        vm.NewFieldPlot();
        vm.NewFieldPlot();                                                       // brief-em3d-96: both drawn (up to four at once)
        Assert.Equal([false, false], vm.Document.FieldPlots.Select(p => p.Hidden));
        foreach (var grouping in new[] { C3dTreeGrouping.Material, C3dTreeGrouping.Primitive })
        {
            vm.TreeGrouping = grouping;
            var group = vm.Tree.Single(g => g.Role == C3dTreeGroupRole.FieldPlots);
            Assert.Equal(["Field1", "Field2"], group.Items.Select(i => i.Name));
            Assert.Equal("|E| · mode 1 · clip Z", group.Items[0].Detail);
            Assert.True(group.CanAdd);
        }
        vm.HideAllTreeObjectsCommand.Execute(null);
        Assert.All(vm.Document.FieldPlots, p => Assert.True(p.Hidden));
        Assert.Null(vm.Viewer.Plot);
        vm.ShowAllTreeObjectsCommand.Execute(null);
        Assert.All(vm.Document.FieldPlots, p => Assert.False(p.Hidden));
        Assert.NotNull(vm.Viewer.Plot);
    }

    /// <summary>The tree keeps its selection by row name: a renamed plot (and its undo and redo) stays selected under its new
    /// name, with the Inspector on it.</summary>
    [Fact]
    public void ARenamedPlot_StaysSelected_ThroughUndoAndRedo()
    {
        var vm = Open(Eigenmode());
        vm.NewFieldPlot();
        Assert.Equal("Field1", vm.SelectedTreeItem?.Name);
        Assert.Null(vm.RenameFieldPlot("Field1", "Cut"));
        Assert.Equal(("Cut", FieldPlotKind), (vm.SelectedTreeItem?.Name, vm.SelectedTreeItem?.Kind));
        Assert.Equal("Field plot Cut", vm.Properties.Heading);
        vm.UndoRedo.Undo();
        Assert.Equal("Field1", vm.SelectedTreeItem?.Name);
        vm.UndoRedo.Redo();
        Assert.Equal("Cut", vm.SelectedTreeItem?.Name);
    }

    // ── 7. the strip is gone ────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_TheEditorView_BindsNoneOfTheRetiredStripsState()
    {
        string axaml = File.ReadAllText(Path.Combine(PalaceBackendTests.RepoRoot(), "src", "Ui", "Views", "ThreeD", "C3dEditorView.axaml"));
        axaml = System.Text.RegularExpressions.Regex.Replace(axaml, "<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        foreach (string gone in (string[])["ShowField", "SelectedFieldSolution", "SelectedFieldQuantity", "FieldOnClipPlane", "FieldOnSurfaces",
                                           "FieldDb", "FieldPercentile", "TemperatureAllFaces", "TemperatureOnClip", "TemperatureStep"])
            Assert.DoesNotContain("Viewer." + gone, axaml);
    }

    // ── the fixture and the waits ───────────────────────────────────────────────────────────

    /// <summary>Plot <paramref name="name"/> shown, and what its layer draws once nothing more is on its way (brief-em3d-96: the
    /// plots drawn beside it stay drawn, and the range is its group's).</summary>
    private (int Vertices, double Lo, double Hi) Drawn(C3dEditorViewModel vm, string name)
    {
        vm.SetPlotShown(name, true);
        Until(() => vm.Viewer.LayerNamed(name) is { Builds: > 0, Scale: not null, Building: false } l && l.Name == name, $"{name} was never drawn");
        Settled(vm);
        var layer = vm.Viewer.LayerNamed(name)!;
        return (layer.Geometry.Vertices.Length, layer.Scale!.Lo, layer.Scale.Hi);
    }

    /// <summary>What is drawn once nothing more is on its way.</summary>
    private (int Vertices, double Lo, double Hi) Settled(C3dEditorViewModel vm)
    {
        Thread.Sleep(150);
        while (_posted.TryDequeue(out var a)) a();
        var s = vm.Viewer.FieldScale;
        return (vm.Viewer.FieldGeometry.Vertices.Length, s?.Lo ?? double.NaN, s?.Hi ?? double.NaN);
    }

    private static EmSetup Eigenmode()
    {
        var none = new EmAirBoxFace(0, null);
        return new EmSetup
        {
            Name = "modes", Solver3D = Em3dSolverId.Palace, Problem3D = Em3dProblemType.Eigenmode,
            AirBox = new EmAirBox(none, none, none, none, none, none), Eigenmode = new EmEigenmode3D(1, 5),
            Frequency = new CircuitRF.Core.Design.FrequencySpec("5", "15", 3, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz"),
        };
    }

    /// <summary>A driven setup sweeping 2–18 GHz: its default saved frequency is the centre, 10 GHz — the owner's report.</summary>
    private static EmSetup Driven()
    {
        var none = new EmAirBoxFace(0, null);
        return new EmSetup
        {
            Name = "drive", Solver3D = Em3dSolverId.Palace, Problem3D = Em3dProblemType.Driven,
            AirBox = new EmAirBox(none, none, none, none, none, none),
            Frequency = new CircuitRF.Core.Design.FrequencySpec("2", "18", 9, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz"),
        };
    }

    private C3dEditorViewModel Open(EmSetup setup)
    {
        const long um = 1000;
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Air", Epsr = 1 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cavity", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cavity.c3d");
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            Objects = [new C3dBox { Name = "cavity", Material = "Air", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(22860 * um, 10160 * um, 25000 * um) }],
            Setups = [EmSetupPersistence.ToEmbedded(setup)],
        });
        return Open(path);
    }

    private C3dEditorViewModel Open(string path)
    {
        string ws = Path.Combine(_root, "ws");
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue)
        {
            ResultsRootProvider = () => Path.Combine(_root, "results"),
        };
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Start();
        Until(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested && vm.Viewer.Scene.Objects.Length > 0, "the scene never settled");
        return vm;
    }

    /// <summary>The cavity fixture as the active setup's finished run.</summary>
    private void Run(C3dEditorViewModel vm)
    {
        var run = vm.ActiveRunSetup!;
        string dir = Em3dRunService.RunDirectory(Path.Combine(_root, "results"), run, Em3dSolverId.Palace);
        if (!Directory.Exists(dir)) CopyDirectory(Cavity, dir);
        C3dRunInputs.Take(vm.Document, vm.TopFilePath, []).KeepIn(dir);   // what the run service keeps (brief-em3d-87)
        vm.RunFinished();
        Until(() => vm.Viewer.FieldsAvailable && vm.Viewer.FieldSolutions.Count > 0, "the run's fields were never read");
    }

    /// <summary>A driven run saving <paramref name="ghz"/>: the cavity's first steps re-listed as those frequencies.</summary>
    private string DrivenRun(C3dEditorViewModel vm, params double[] ghz) => DrivenRun(vm, ghz, null);

    private string DrivenRun(C3dEditorViewModel vm, double a, double b, double c, string into) => DrivenRun(vm, [a, b, c], into);

    private string DrivenRun(C3dEditorViewModel vm, double[] ghz, string? into)
    {
        var run = vm.ActiveRunSetup!;
        string dir = into ?? Em3dRunService.RunDirectory(Path.Combine(_root, "results"), run, Em3dSolverId.Palace);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        CopyDirectory(Cavity, dir);
        string pv = Path.Combine(dir, "postpro", "paraview");
        foreach (string part in (string[])["", "_boundary"])
        {
            string from = Path.Combine(pv, "eigenmode" + part), to = Path.Combine(pv, "driven" + part);
            Directory.Move(from, to);
            File.Delete(Path.Combine(to, "eigenmode" + part + ".pvd"));
            File.WriteAllText(Path.Combine(to, "driven" + part + ".pvd"),
                "<?xml version=\"1.0\"?>\n<VTKFile type=\"Collection\" version=\"2.2\" byte_order=\"LittleEndian\">\n<Collection>\n" +
                string.Concat(ghz.Select((g, i) => $"<DataSet timestep=\"{g.ToString(System.Globalization.CultureInfo.InvariantCulture)}\" group=\"\" part=\"0\" " +
                                                   $"file=\"Cycle00000{i + 1}/data.pvtu\" name=\"mesh\"/>\n")) +
                "</Collection>\n</VTKFile>\n");
        }
        C3dRunInputs.Take(vm.Document, vm.TopFilePath, []).KeepIn(dir);
        vm.RunFinished();
        Until(() => vm.Viewer.FieldSolutions.Count == ghz.Length, "the driven run was never read");
        return dir;
    }

    /// <summary>What Gmsh would be given for the active setup — the mesh-reuse comparison's input.</summary>
    private string GeometryScript(C3dEditorViewModel vm)
    {
        var g = C3dProblemAssembly.Assemble(C3dSetups.Read(vm.Document).Single().Setup!, vm.RunDocument(), vm.FilePath,
                                            Path.Combine(_root, "ws", ".cws"));
        Assert.True(g.Problem is not null, g.Refusal);
        return GmshGeoWriter.Write(g.Problem!, PalaceSettings.Resolve(null)).Geo!;
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string dst = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }
    }

    private void Until(Func<bool> done, string what)
        => Assert.True(SpinWait.SpinUntil(() => { while (_posted.TryDequeue(out var a)) a(); return done(); }, TimeSpan.FromSeconds(30)), what);
}
