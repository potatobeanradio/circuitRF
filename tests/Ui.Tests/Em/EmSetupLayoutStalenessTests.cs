// Owner report, 2026-08-25: "placed 3 ports in my .clay drawing, but only 2 ports show up in
// the .cem for the file."
//
// The extraction was never wrong — `EmPortExtraction.Extract` resolved all three, and a Simulate
// would have RUN all three, because `EmRunService` re-extracts from the live `LayoutView`. The panel
// was stale: nothing re-ran `EmSetupEditorViewModel.Refresh` after the `.cem` was opened, so its
// port list, mesh summary and blocking reason were a snapshot from open time, refreshed only when a
// setting inside the panel was committed or Mesh was pressed.

using System.IO;
using System.Text.RegularExpressions;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class EmSetupLayoutStalenessTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private static long Mm(double mm) => (long)Math.Round(mm * 1000 * Dbu);

    private static LabelShape Port(string text, double xMm, double yMm, LayoutRotation dir) =>
        new()
        {
            Layer = TopCopper, X = Mm(xMm), Y = Mm(yMm), Text = text,
            Height = Mm(0.5), IsPort = true, PortDirection = dir,
        };

    /// <summary>A tee, so the cross-section extractor refuses it and Auto lands on the planar
    /// kernel — the only kernel whose ports come from the layout's own labels at all.</summary>
    private static LayoutView TeeWithTwoPorts()
    {
        var view = new LayoutView
        {
            DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um,
            SnapDbu = 1000, AngleMode = AngleMode.AnyAngle,
        };
        view.Shapes.Add(new PolygonShape
        {
            Layer = TopCopper,
            Xy =
            [
                Mm(9), Mm(0),  Mm(20), Mm(0),  Mm(20), Mm(2.9),  Mm(0), Mm(2.9),
                Mm(0), Mm(0),  Mm(6),  Mm(0),  Mm(6),  Mm(-12),  Mm(9), Mm(-12),
            ],
        });
        view.Shapes.Add(Port("P1", 0,  1.45, LayoutRotation.R0));
        view.Shapes.Add(Port("P2", 20, 1.45, LayoutRotation.R180));
        return view;
    }

    private static EmSetupEditorViewModel PanelOver(LayoutView view, string clayPath)
    {
        var setup = new EmSetup { LayoutRef = clayPath, AnalysisKind = EmAnalysisKind.Auto };
        var vm = new EmSetupEditorViewModel(clayPath + ".cem", setup)
        {
            ResolveLayout = _ => new EmLayoutSource(clayPath, view, StarterTechnologies.Pcb2Layer(), Dbu),
        };
        vm.Refresh();
        return vm;
    }

    // ── The mechanism the workspace's wiring relies on ────────────────────────────────────────

    /// <summary>
    /// <b>Refresh re-reads the LIVE LayoutView, so a port added after the panel opened appears.</b>
    /// This is the half that was already correct and is what makes the one-line subscription in
    /// <c>WorkspaceViewModel</c> sufficient — had Refresh cached the geometry, that subscription
    /// would have fixed nothing.
    /// </summary>
    [Fact]
    public void APortAddedAfterThePanelOpened_AppearsOnTheNextRefresh()
    {
        const string clay = "/tmp/staleness-test.clay";
        var view = TeeWithTwoPorts();
        var vm   = PanelOver(view, clay);

        Assert.Equal(EmAnalysisKind.Planar, vm.SelectedKernel);
        Assert.Null(vm.PortRefusal);
        Assert.Equal(2, vm.PortRows.Count);

        // The third port, on the stub — exactly the owner's gesture.
        view.Shapes.Add(Port("P3", 7.5, -12, LayoutRotation.R90));

        // Still two: the panel does not poll, which is the whole reason the subscription exists.
        Assert.Equal(2, vm.PortRows.Count);

        vm.Refresh();

        Assert.Null(vm.PortRefusal);
        Assert.Equal(3, vm.PlanarPorts.Count);
        Assert.Equal(3, vm.PortRows.Count);
        Assert.Equal([1, 2, 3], vm.PortRows.Select(r => r.PortNumber));
    }

    /// <summary>A refresh over an edited layout must also drop the mesh report that described the
    /// artwork as it was — R-em-17, the half <see cref="EmSetupEditorViewModel.InvalidateMesh"/>
    /// already documented as the workspace's job and that nothing was calling.</summary>
    [Fact]
    public void InvalidateMesh_DropsAReportDescribingArtworkThatHasSinceChanged()
    {
        const string clay = "/tmp/staleness-mesh-test.clay";
        var view = TeeWithTwoPorts();
        var vm   = PanelOver(view, clay);

        var problem = vm.PreparePlanarMesh();
        Assert.NotNull(problem);
        vm.AdoptPlanarMeshReport(vm.ComputePlanarMesh(problem!, null));
        Assert.NotNull(vm.PlanarMeshReport);

        vm.InvalidateMesh();

        Assert.Null(vm.PlanarMeshReport);
        Assert.Null(vm.CurrentDensity);
    }

    // ── One bad port must not erase the others ────────────────────────────────────────────────

    /// <summary>A tee with three ports, the third an internal delta gap in the .cem.</summary>
    private static (LayoutView View, EmSetupEditorViewModel Vm) ThreePortPanel(string clay)
    {
        var view = TeeWithTwoPorts();
        view.Shapes.Add(Port("P3", 7.5, -12, LayoutRotation.R90));

        var setup = new EmSetup { LayoutRef = clay, AnalysisKind = EmAnalysisKind.Auto };
        setup.PortKinds.AddRange([PlanarPortKind.Edge, PlanarPortKind.Edge, PlanarPortKind.InternalDeltaGap]);

        var vm = new EmSetupEditorViewModel(clay + ".cem", setup)
        {
            ResolveLayout = _ => new EmLayoutSource(clay, view, StarterTechnologies.Pcb2Layer(), Dbu),
        };
        vm.Refresh();
        return (view, vm);
    }

    /// <summary>
    /// <b>Every port the user drew stays listed, and the bad one says why.</b> Owner request,
    /// 2026-08-25: "if any ports aren't touching metal, the .cem editor will not list the ports. I'd
    /// like to still see them listed, even if they are not on a conductor (and the .cem gives a
    /// warning)." The panel built its rows from <c>EmPortExtractionResult.Ports</c>, which is empty on
    /// any refusal — so one bad label emptied the list at exactly the moment the user was trying to
    /// find the bad label.
    /// </summary>
    [Fact]
    public void APortOffTheMetal_LeavesEveryOtherPortListed_AndNamesItsOwnProblem()
    {
        const string clay = "/tmp/bad-port-listing.clay";
        var (view, vm) = ThreePortPanel(clay);
        Assert.Equal(3, vm.PortRows.Count);

        view.Shapes.OfType<LabelShape>().First(l => l.Text == "P2").X = Mm(60);   // well off the end
        vm.Refresh();

        Assert.Equal(3, vm.PortRows.Count);
        Assert.Equal([1, 2, 3], vm.PortRows.Select(r => r.PortNumber));

        var bad = vm.PortRows[1];
        Assert.NotNull(bad.Problem);
        Assert.Contains("is not on any conductor", bad.Problem);
        Assert.True(bad.HasProblem);

        // And only that one is flagged — a shared refusal banner would say nothing about WHICH port.
        Assert.Null(vm.PortRows[0].Problem);
        Assert.Null(vm.PortRows[2].Problem);
    }

    /// <summary>
    /// <b>Listing it does NOT mean running it.</b> The solver's view of the port set stays
    /// all-or-nothing: a port that is not on metal has no location the mesher could honestly place it
    /// at, and a solve over the ports that happened to resolve would be a complete, plausible answer
    /// for a structure nobody drew. This is the assertion that keeps the new list from becoming a way
    /// to run a half-resolved setup.
    /// </summary>
    [Fact]
    public void APortOffTheMetal_StillEmptiesTheSolversPortSet_AndBlocksTheRun()
    {
        const string clay = "/tmp/bad-port-blocks.clay";
        var (view, vm) = ThreePortPanel(clay);

        view.Shapes.OfType<LabelShape>().First(l => l.Text == "P2").X = Mm(60);
        vm.Refresh();

        Assert.Empty(vm.PlanarPorts);
        Assert.False(vm.CanRun);
        Assert.NotNull(vm.PortRefusal);
        Assert.Equal(vm.PortRefusal, vm.BlockingReason);
    }

    /// <summary>
    /// <b>An internal port keeps its mark while a DIFFERENT port is broken.</b> Owner report,
    /// 2026-08-25: "P3 renders as edge port (even though it is a gap port) when P2 is not on a
    /// conductor." The layout's marks were published from the resolved port list, so a refusal
    /// anywhere silently retyped every internal port in the drawing back to an edge port — a picture
    /// contradicting the .cem that produced it.
    /// </summary>
    [Fact]
    public void AnInternalGapPortKeepsItsMark_WhenSomeOtherPortIsOffTheMetal()
    {
        const string clay = "/tmp/bad-port-marks.clay";
        var (view, vm) = ThreePortPanel(clay);

        var before = Assert.Single(vm.InternalPortMarkAnchors);
        Assert.Equal(PlanarPortKind.InternalDeltaGap, before.Kind);

        view.Shapes.OfType<LabelShape>().First(l => l.Text == "P2").X = Mm(60);
        vm.Refresh();

        var after = Assert.Single(vm.InternalPortMarkAnchors);
        Assert.Equal(PlanarPortKind.InternalDeltaGap, after.Kind);
        Assert.Equal(before.X, after.X);
        Assert.Equal(before.Y, after.Y);

        // And the row still offers the type it is, so the user can retype it while the other port is
        // still broken.
        Assert.Equal(PlanarPortKind.InternalDeltaGap, vm.PortRows[2].Kind);
    }

    /// <summary>An unresolved row names no SIDE — the side is inferred from a conductor, and there
    /// isn't one. Captioning it "low-x end" would be the panel asserting the very thing the refusal
    /// denies.</summary>
    [Fact]
    public void AnUnresolvedRowDoesNotClaimAnEnd()
    {
        const string clay = "/tmp/bad-port-caption.clay";
        var (view, vm) = ThreePortPanel(clay);

        view.Shapes.OfType<LabelShape>().First(l => l.Text == "P2").X = Mm(60);
        vm.Refresh();

        Assert.DoesNotContain("end", vm.PortRows[1].Label);
        Assert.Contains("Port 2", vm.PortRows[1].Label);
        // The rows that DID resolve still name theirs.
        Assert.Contains("end", vm.PortRows[0].Label);
    }

    /// <summary>The extractor's own diagnostic view: one row per numbered label, in port order,
    /// resolved or not — and index-aligned with the .cem's per-port lists, which is what makes
    /// <c>ResolvePortZ0(i)</c> / <c>ResolvePortKind(i)</c> address the right port on a refused
    /// set.</summary>
    [Fact]
    public void ExtractReportsEveryNumberedLabel_InPortOrder_ResolvedOrNot()
    {
        var view = TeeWithTwoPorts();
        view.Shapes.Add(Port("P3", 7.5, -12, LayoutRotation.R90));
        view.Shapes.OfType<LabelShape>().First(l => l.Text == "P2").X = Mm(60);

        var planar = PlanarExtractor.Extract(view.Shapes, StarterTechnologies.Pcb2Layer(), Dbu, 10e9);
        Assert.True(planar.Ok, planar.Refusal);

        var r = EmPortExtraction.Extract(view.Shapes, planar.Problem!, Dbu);

        Assert.Equal(3, r.Rows.Count);
        Assert.Equal([1, 2, 3], r.Rows.Select(x => x.Number));
        Assert.True(r.Rows[0].Ok);
        Assert.False(r.Rows[1].Ok);
        Assert.True(r.Rows[2].Ok);
        Assert.NotNull(r.Rows[1].Problem);

        // The solver's view is still empty, and the refusal is the FIRST problem found — unchanged
        // wording, so every existing refusal gate still reads the same sentence.
        Assert.Empty(r.Ports);
        Assert.Equal(r.Rows[1].Problem, r.Refusal);
    }

    // ── One place ports are listed, not two ───────────────────────────────────────────────────

    /// <summary>
    /// <b>The captioned "Port 1 Z₀ / Port 2 Z₀" pair and the per-port list are never both on
    /// screen.</b> Owner report, 2026-08-25: "I don't see a Port 3 Z₀ option in the .cem editor." The
    /// planar panel drew both — a grid captioned <i>Port 1</i> and <i>Port 2</i>, and beneath it a
    /// row per port. The captioned pair reads as the port list, stops at two, and gives a reader no
    /// reason to take the rows below it as the same thing continued.
    /// </summary>
    [Fact]
    public void ThePlanarPanelShowsThePerPortList_AndNotTheCaptionedNearFarPair()
    {
        const string clay = "/tmp/nearfar-planar-test.clay";
        var vm = PanelOver(TeeWithTwoPorts(), clay);

        Assert.Equal(EmAnalysisKind.Planar, vm.SelectedKernel);
        Assert.True(vm.ShowPortList);
        Assert.False(vm.ShowNearFarPortZ0);
    }

    /// <summary>And the cross-section kernel's single line keeps the pair, which fully describes its
    /// two ports — the case ShowPortList's own note says the list must not duplicate.</summary>
    [Fact]
    public void ASingleLineCrossSectionSetup_KeepsTheCaptionedPair()
    {
        const string clay = "/tmp/nearfar-xsection-test.clay";
        var view = new LayoutView
        {
            DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um,
            SnapDbu = 1000, AngleMode = AngleMode.AnyAngle,
        };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) });
        var vm = PanelOver(view, clay);

        Assert.Equal(EmAnalysisKind.CrossSection, vm.SelectedKernel);
        Assert.False(vm.ShowPortList);
        Assert.True(vm.ShowNearFarPortZ0);
    }

    /// <summary>They are exact opposites by construction, at every port count — two independently
    /// computed visibilities could both be true, which is the state being fixed.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheTwoPortControlsAreNeverBothVisible(bool planar)
    {
        var view = new LayoutView
        {
            DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um,
            SnapDbu = 1000, AngleMode = AngleMode.AnyAngle,
        };
        if (planar)
        {
            view.Shapes.Add(TeeWithTwoPorts().Shapes[0]);
            view.Shapes.Add(Port("P1", 0,  1.45, LayoutRotation.R0));
            view.Shapes.Add(Port("P2", 20, 1.45, LayoutRotation.R180));
        }
        else
        {
            view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) });
        }

        var vm = PanelOver(view, $"/tmp/nearfar-both-{planar}.clay");
        Assert.NotEqual(vm.ShowPortList, vm.ShowNearFarPortZ0);
    }

    // ── The wiring itself ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>WorkspaceViewModel</c> cannot be constructed headlessly (see <c>src/Ui/CLAUDE.md</c>'s
    /// testing notes), so the subscription that makes the two behaviours above reach a user is pinned
    /// by source scan — this codebase's established fallback. Comments are stripped first: the bug
    /// report and its explanation are written in them, and a scan that matched prose would pass on a
    /// file where the call had been deleted.
    /// </summary>
    [Fact]
    public void EveryPathBackedLayoutSession_NotifiesOpenEmSetupsWhenItsModelChanges()
    {
        string src = StripComments(ReadSource("src/Ui/ViewModels/WorkspaceViewModel.cs"));

        Assert.Contains("vm.Model.Changed += (_, _) => NotifyEmSetupsLayoutChanged(vm.CurrentLayoutPath);", src);
        Assert.Contains("private void NotifyEmSetupsLayoutChanged(string? absClayPath)", src);

        // Both halves, and posted rather than run inside LayoutModel.NotifyChanged's RenderLock.
        var body = src[src.IndexOf("private void NotifyEmSetupsLayoutChanged", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Contains("Dispatcher.UIThread.Post", body);
        Assert.Contains("vm.InvalidateMesh();", body);
        Assert.Contains("vm.Refresh();", body);
    }

    // ── A port dragged OFF the metal must say so, wherever it went ────────────────────────────

    /// <summary>
    /// <b>The concave notch of a tee is not "on the metal", and it used to be — at zero distance.</b>
    /// Owner report, 2026-08-25: "if I move Port 1 or Port 3 off the metal, I get no live update for
    /// bad port (but I do get a warning for port 2)."
    ///
    /// <para><c>NearestPolygon</c>'s off-metal test measured the distance to the polygon's BOUNDING
    /// BOX. A tee's box spans its own empty notch, so a port dragged sideways into the notch measured
    /// exactly zero and was accepted however far it was from any copper — which is why the report is
    /// asymmetric: port 2 sits at the far end, where moving it leaves the box at once, while ports 1
    /// and 3 flank the notch and could never leave it.</para>
    /// </summary>
    [Theory]
    // The notch: inside the tee's bounding box, nowhere near its metal.
    [InlineData("P3", 15.0, -6.0)]
    [InlineData("P3", 13.0, -8.0)]
    [InlineData("P1", -3.0, -6.0)]
    // And straight out past the end, which the bounding-box test did already catch.
    [InlineData("P2", 30.0,  1.45)]
    public void APortMovedOffTheMetal_IsRefusedByName_WhicheverWayItWent(string which, double xMm, double yMm)
    {
        const string clay = "/tmp/offmetal-test.clay";
        var view = TeeWithTwoPorts();
        view.Shapes.Add(Port("P3", 7.5, -12, LayoutRotation.R90));
        var vm = PanelOver(view, clay);
        Assert.Null(vm.PortRefusal);
        Assert.Equal(3, vm.PortRows.Count);

        var l = view.Shapes.OfType<LabelShape>().First(s => s.Text == which);
        l.X = Mm(xMm); l.Y = Mm(yMm);
        vm.Refresh();

        Assert.NotNull(vm.PortRefusal);
        Assert.Contains($"Port {which[1]}", vm.PortRefusal);
        Assert.Contains("is not on any conductor", vm.PortRefusal);
        // And it BLOCKS the run — a refusal the panel prints but does not act on is decoration.
        Assert.Equal(vm.PortRefusal, vm.BlockingReason);
    }

    /// <summary>A label a hair off the metal still resolves, which is what the tolerance is for — the
    /// fix tightened WHAT is measured, and it must not have turned into "on the metal exactly".</summary>
    [Fact]
    public void ALabelJustOffTheEdge_StillResolves()
    {
        const string clay = "/tmp/offmetal-slop-test.clay";
        var view = TeeWithTwoPorts();
        var vm   = PanelOver(view, clay);

        var p1 = view.Shapes.OfType<LabelShape>().First(s => s.Text == "P1");
        p1.X -= Mm(0.05);                 // 50 um off the left end face
        vm.Refresh();

        Assert.Null(vm.PortRefusal);
        Assert.Equal(2, vm.PortRows.Count);
    }

    /// <summary>
    /// <b>The reach follows the CONDUCTOR, not the drawing.</b> Metal nowhere near a port must not
    /// change whether that port counts as on the metal — and under the bounding-box rule it did:
    /// the reach was half the smaller side of the whole polygon's box, so lengthening the tee's stub
    /// (at the far end of the bar from port 1) grew the tolerance at port 1 from 7.45 mm to 10 mm,
    /// and a port 8 mm off the left end face flipped from refused to accepted.
    /// </summary>
    [Theory]
    [InlineData(12.0)]
    [InlineData(40.0)]
    public void TheOffMetalReach_DoesNotFollowMetalAtTheOtherEndOfTheStructure(double stubLengthMm)
    {
        var view = new LayoutView
        {
            DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um,
            SnapDbu = 1000, AngleMode = AngleMode.AnyAngle,
        };
        view.Shapes.Add(new PolygonShape
        {
            Layer = TopCopper,
            Xy =
            [
                Mm(9), Mm(0),  Mm(20), Mm(0),  Mm(20), Mm(2.9),  Mm(0), Mm(2.9),
                Mm(0), Mm(0),  Mm(6),  Mm(0),  Mm(6),  Mm(-stubLengthMm),  Mm(9), Mm(-stubLengthMm),
            ],
        });
        // Port 1 is 8 mm off the bar's LEFT end face — nowhere near the stub, at either length.
        view.Shapes.Add(Port("P1", -8, 1.45, LayoutRotation.R0));
        view.Shapes.Add(Port("P2", 20, 1.45, LayoutRotation.R180));

        var vm = PanelOver(view, $"/tmp/offmetal-reach-{stubLengthMm}.clay");

        Assert.NotNull(vm.PortRefusal);
        Assert.Contains("Port 1", vm.PortRefusal);
    }

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadSource(string relative)
        => File.ReadAllText(Path.Combine(SourceRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(src, @"//[^\n]*", "");
    }
}
