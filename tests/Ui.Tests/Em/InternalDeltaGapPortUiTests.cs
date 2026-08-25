// The internal delta-gap port, Ui side — the SECOND port type reaches the EM Setup panel.
//
// The engine half (where the cut lands, what it drives, that de-embedding is a true no-op for it) is
// tests/Engine.Tests/Mom/InternalDeltaGapPortTests.cs. What lives here is everything the engine
// cannot see: the `.cem` round trip, Clone, the provenance hash (which MUST move, unlike the solver
// flags beside it), the panel row, and that the run service actually hands the type to the extractor.
//
// The wiring tests are the load-bearing ones, for the reason this area has already paid for once:
// `PlanarExtractor.AnalyticAlternativeFor` was built, tested and unreachable for months because no
// caller in src/ passed its optional parameter. `kindFor` is another optional parameter carrying a
// whole capability, so both of its call sites are pinned.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class InternalDeltaGapPortUiTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private static long Mm(double mm) => (long)Math.Round(mm * 1000 * Dbu);

    private static RectShape Line() =>
        new() { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) };

    private static LabelShape Port(string text, double xMm, double yMm, LayoutRotation? dir = null) =>
        new()
        {
            Layer = TopCopper, X = Mm(xMm), Y = Mm(yMm), Text = text, Height = Mm(0.5),
            IsPort = true, PortDirection = dir,
        };

    private static PlanarProblem Problem(params LayoutShape[] shapes)
    {
        var r = PlanarExtractor.Extract(shapes, StarterTechnologies.Pcb2Layer(), Dbu, 10e9);
        Assert.True(r.Ok, r.Refusal);
        return r.Problem!;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The .cem round trip — omit at default, exactly like PortZ0s beside it
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ACemWhosePortsAreAllEdgePorts_GainsNoByte()
    {
        var setup = new EmSetup { Name = "hero", LayoutRef = "Amp/layout/Amp.clay" };
        string before = EmSetupPersistence.Serialize(setup);

        Assert.DoesNotContain("PortKinds", before, StringComparison.Ordinal);
        Assert.Equal(PlanarPortKind.Edge, setup.ResolvePortKind(0));
        Assert.Equal(PlanarPortKind.Edge, setup.ResolvePortKind(7));

        var reloaded = EmSetupPersistence.Deserialize(before);
        Assert.Equal(before, EmSetupPersistence.Serialize(reloaded));
    }

    [Fact]
    public void APanelThatMaterialisedEveryRowAtItsDEFAULT_StillGainsNoByte()
    {
        // The half that is easy to get wrong: the panel writes one entry per port whether or not the
        // user changed anything, so a naive serializer would put ["Edge","Edge"] into every .cem
        // that has ever been opened — which is precisely the byte-identity the omit-at-default rule
        // exists to protect.
        var setup = new EmSetup
        {
            Name = "hero", LayoutRef = "a.clay",
            PortKinds = [PlanarPortKind.Edge, PlanarPortKind.Edge],
        };

        Assert.DoesNotContain("PortKinds", EmSetupPersistence.Serialize(setup), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingOnePortInternal_RoundTrips_AndSurvivesClone()
    {
        var setup = new EmSetup
        {
            Name = "planar", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
            PortKinds = [PlanarPortKind.Edge, PlanarPortKind.InternalDeltaGap],
        };

        string json = EmSetupPersistence.Serialize(setup);
        Assert.Contains("InternalDeltaGap", json, StringComparison.Ordinal);

        var back = EmSetupPersistence.Deserialize(json);
        Assert.Equal(PlanarPortKind.Edge,             back.ResolvePortKind(0));
        Assert.Equal(PlanarPortKind.InternalDeltaGap, back.ResolvePortKind(1));

        // Clone drives the editor's undo snapshots; a field missing from it is silently lost on the
        // next unrelated edit.
        Assert.Equal(PlanarPortKind.InternalDeltaGap, setup.Clone().ResolvePortKind(1));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // It DOES move the provenance hash — the opposite of the solver flags beside it
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ChangingAPortsTypeMovesThePortHash_ButAnAllEdgeSetIsUNCHANGED()
    {
        var at = new EmPoint(0.010, 0.00145);

        PlanarPort[] edge     = [new(1, at, PlanarPortSide.MinX, 50.0)];
        PlanarPort[] withKind = [new(1, at, PlanarPortSide.MinX, 50.0,
                                     Kind: PlanarPortKind.Edge)];
        PlanarPort[] gap      = [new(1, at, PlanarPortSide.MinX, 50.0,
                                     Kind: PlanarPortKind.InternalDeltaGap)];

        // Changing an edge port into a gap moves the excitation and turns de-embedding off for it,
        // so an .snp written under one type is NOT current for the other. Leaving the type out of
        // the hash would be exactly the staleness failure R-em-20 exists to prevent.
        Assert.NotEqual(EmSnpProvenance.PortHash(edge), EmSnpProvenance.PortHash(gap));

        // And an all-edge port set hashes as it always did, so no .snp this application has ever
        // written reports a one-time false staleness.
        Assert.Equal(EmSnpProvenance.PortHash(edge), EmSnpProvenance.PortHash(withKind));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Extraction: the type comes from the .cem, and a gap with no direction is refused
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ThePortTypeComesFromTheCem_NotFromTheLabel()
    {
        LayoutShape[] shapes =
        [
            Line(),
            Port("1", 0,  1.45),
            Port("2", 20, 1.45),
            Port("3", 10, 1.45, LayoutRotation.R0),
        ];

        var setup = new EmSetup
        {
            Name = "p", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
            PortKinds = [PlanarPortKind.Edge, PlanarPortKind.Edge, PlanarPortKind.InternalDeltaGap],
        };

        var r = EmPortExtraction.Extract(shapes, Problem(shapes), Dbu, setup.ResolvePortZ0,
                                         LayoutUnit.Um, setup.ResolvePortKind);

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(PlanarPortKind.Edge,             r.Ports[0].Kind);
        Assert.Equal(PlanarPortKind.Edge,             r.Ports[1].Kind);
        Assert.Equal(PlanarPortKind.InternalDeltaGap, r.Ports[2].Kind);

        // Nothing was written to the layout — the label is an ordinary label either way (R-res-4).
        Assert.All(shapes.OfType<LabelShape>(), l => Assert.True(l.IsPort));
        Assert.Contains(r.Notes, n => n.Contains("INTERNAL delta gap"));
    }

    [Fact]
    public void AnInternalPortWithNoDirection_IsRefusedByName_RatherThanInferringOne()
    {
        // A label in the MIDDLE of a conductor is roughly equidistant from all four edges, so the
        // nearest-boundary inference an edge port uses measures nothing about it. Guessing would
        // reverse the sign of everything through this port; the edge port's own corner-ambiguity
        // refusal would fire here too, but for the wrong reason and with the wrong remedy.
        LayoutShape[] shapes = [Line(), Port("1", 10, 1.45)];   // no PortDirection

        var r = EmPortExtraction.Extract(
            shapes, Problem(shapes), Dbu, null, LayoutUnit.Um,
            _ => PlanarPortKind.InternalDeltaGap);

        Assert.False(r.Ok);
        Assert.Contains("internal delta-gap port with no direction", r.Refusal!);
        Assert.Contains("Rotate the port", r.Refusal!);
    }

    [Fact]
    public void AnEdgePortInTheSameSpotStillInfersItsSide()
    {
        // The complement of the test above: the inference is unchanged for the type it was built
        // for, so nothing about the default path moved.
        LayoutShape[] shapes = [Line(), Port("1", 0, 1.45)];

        var r = EmPortExtraction.Extract(shapes, Problem(shapes), Dbu);
        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(PlanarPortSide.MinX,   r.Ports[0].Side);
        Assert.Equal(PlanarPortKind.Edge,   r.Ports[0].Kind);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The panel
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "crf-gap-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static LayoutView PortedLine()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Line());
        view.Shapes.Add(Port("1", 0,  1.45));
        view.Shapes.Add(Port("2", 20, 1.45));
        return view;
    }

    private static EmSetupEditorViewModel Editor(string dir)
    {
        string path  = Path.Combine(dir, "panel.cem");
        var    setup = new EmSetup
        {
            Name = "panel", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
        };
        EmSetupPersistence.SaveToFile(path, setup);
        var vm = new EmSetupEditorViewModel(path, setup)
        {
            ResolveLayout = _ => new EmLayoutSource(
                Path.Combine(dir, "a.clay"), PortedLine(), StarterTechnologies.Pcb2Layer(), Dbu),
        };
        vm.Refresh();
        return vm;
    }

    [Fact]
    public void ThePlanarPortListIsPopulatedAtAll()
    {
        // A REGRESSION TEST FOR A BUG THIS WORK UNCOVERED, not for the port type.
        //
        // `RaiseState`'s stale-row guard asked only whether the CROSS-SECTION problem was null, and
        // a planar refresh leaves that null by construction — so the list was filled by
        // `RebuildPlanarPortRows` and emptied one line later, every time. The per-port reference
        // impedance has therefore never appeared for a full-wave setup, and no test caught it
        // because every other PortRows test drives the cross-section kernel.
        var vm = Editor(TempDir());

        Assert.True(vm.ShowPortList);
        Assert.Equal([1, 2], vm.PortRows.Select(r => r.PortNumber));
        Assert.Equal("50", vm.PortRows[0].Text);

        // And the list is a live editor, not a readback: committing a row still writes through.
        vm.PortRows[1].Text = "75";
        vm.CommitPortRow(1);
        Assert.Equal(new Complex(75, 0), vm.Working.ResolvePortZ0(1));
    }

    [Fact]
    public void EveryPlanarRowOffersTheType_SourcedFromTheEnum()
    {
        var vm = Editor(TempDir());

        Assert.Equal(2, vm.PortRows.Count);
        Assert.All(vm.PortRows, r => Assert.True(r.ShowKind));
        Assert.All(vm.PortRows, r => Assert.Equal(PlanarPortKind.Edge, r.Kind));

        // From the enum rather than a hand-written list, so a third port type cannot silently fail
        // to appear in the panel — the rule BoundaryCellsChoices already follows.
        Assert.Equal(Enum.GetValues<PlanarPortKind>(), EmPortZ0Row.KindChoices);
    }

    [Fact]
    public void ChangingATypeCommitsOneUndoEntry_AndINVALIDATESTheMesh()
    {
        var vm = Editor(TempDir());

        while (vm.UndoRedo.CanUndo) vm.UndoRedo.Undo();
        while (vm.UndoRedo.CanRedo) vm.UndoRedo.Redo();

        vm.BuildPlanarMesh();
        Assert.NotNull(vm.PlanarMeshReport);

        vm.PortRows[1].Kind = PlanarPortKind.InternalDeltaGap;

        Assert.Equal(PlanarPortKind.InternalDeltaGap, vm.Working.ResolvePortKind(1));
        Assert.Equal(PlanarPortKind.Edge,             vm.Working.ResolvePortKind(0));

        // Unlike the reference impedance beside it: Z0 is a renormalisation applied to the answer,
        // while the type decides WHERE the excitation is cut and therefore which rooftops are
        // driven. A report computed under the other type is about a different excitation.
        Assert.Null(vm.PlanarMeshReport);

        Assert.True(vm.UndoRedo.CanUndo);
        vm.UndoRedo.Undo();
        Assert.Equal(PlanarPortKind.Edge, vm.Working.ResolvePortKind(1));
    }

    [Fact]
    public void SelectingTheTypeItAlreadyHas_PushesNoUndoEntry()
    {
        var vm = Editor(TempDir());
        while (vm.UndoRedo.CanUndo) vm.UndoRedo.Undo();
        while (vm.UndoRedo.CanRedo) vm.UndoRedo.Redo();

        bool couldUndo = vm.UndoRedo.CanUndo;
        vm.PortRows[0].Kind = PlanarPortKind.Edge;
        Assert.Equal(couldUndo, vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void TheCrossSectionKernelsRowsOfferNoType()
    {
        // Its ports are the two ends of a uniform line by construction, so there is nothing an
        // internal gap could mean there and offering the choice would be offering a dead setting.
        string dir = TempDir();
        string path = Path.Combine(dir, "xs.cem");
        var setup = new EmSetup
        {
            Name = "xs", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.CrossSection,
        };
        EmSetupPersistence.SaveToFile(path, setup);
        var vm = new EmSetupEditorViewModel(path, setup)
        {
            ResolveLayout = _ => new EmLayoutSource(
                Path.Combine(dir, "a.clay"), PortedLine(), StarterTechnologies.Pcb2Layer(), Dbu),
        };
        vm.Refresh();

        Assert.All(vm.PortRows, r => Assert.False(r.ShowKind));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // A gap port on the uniform-line kernel is refused, not silently dropped
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AGapPortOnTheCrossSectionKernel_BlocksTheRunByName()
    {
        // The trap this guards: a uniform line carrying an interior gap is STILL a uniform
        // cross-section, so kernel A accepts it and Auto prefers A whenever A accepts. Kernel A never
        // meshes the plane — its ports are the ends of the extracted line by construction — so the
        // gap would simply not be there, and the run would publish a complete, plausible answer for
        // the line without it. Nothing else on screen connects "I set port 3 to Internal delta gap"
        // to "the result has two ports".
        string dir = TempDir();
        string path = Path.Combine(dir, "xs.cem");
        var setup = new EmSetup
        {
            Name = "xs", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.CrossSection,
            PortKinds = [PlanarPortKind.Edge, PlanarPortKind.InternalDeltaGap],
        };
        EmSetupPersistence.SaveToFile(path, setup);
        var vm = new EmSetupEditorViewModel(path, setup)
        {
            ResolveLayout = _ => new EmLayoutSource(
                Path.Combine(dir, "a.clay"), PortedLine(), StarterTechnologies.Pcb2Layer(), Dbu),
        };
        vm.Refresh();

        Assert.NotNull(vm.InternalGapOnTheWrongKernel);
        Assert.Contains("internal delta-gap port", vm.BlockingReason!);
        Assert.Contains("full-wave planar", vm.BlockingReason!);
        Assert.False(vm.CanRun);
    }

    [Fact]
    public void TheSameSetupOnTheFullWaveKernel_IsNotBlockedByIt()
    {
        // The complement, so the guard cannot pass by refusing everything.
        var setup = new EmSetup
        {
            Name = "p", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
            PortKinds = [PlanarPortKind.Edge, PlanarPortKind.InternalDeltaGap],
        };
        string dir = TempDir(), path = Path.Combine(dir, "p.cem");
        EmSetupPersistence.SaveToFile(path, setup);
        var vm = new EmSetupEditorViewModel(path, setup)
        {
            ResolveLayout = _ => new EmLayoutSource(
                Path.Combine(dir, "a.clay"), PortedLine(), StarterTechnologies.Pcb2Layer(), Dbu),
        };
        vm.Refresh();

        Assert.Null(vm.InternalGapOnTheWrongKernel);
    }

    [Fact]
    public void AnAllEdgeSetupOnTheCrossSectionKernel_IsUntouched()
    {
        var setup = new EmSetup
        {
            Name = "xs", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.CrossSection,
        };
        string dir = TempDir(), path = Path.Combine(dir, "xs.cem");
        EmSetupPersistence.SaveToFile(path, setup);
        var vm = new EmSetupEditorViewModel(path, setup)
        {
            ResolveLayout = _ => new EmLayoutSource(
                Path.Combine(dir, "a.clay"), PortedLine(), StarterTechnologies.Pcb2Layer(), Dbu),
        };
        vm.Refresh();

        Assert.Null(vm.InternalGapOnTheWrongKernel);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Two setups, one layout: the layout names whose interpretation it is drawing
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheLayoutRecordsWhichSetupsPortTypesItIsShowing_AndAnEditClearsIt()
    {
        // A layout can be analysed by more than one .cem, and two of them may legitimately disagree
        // about a port — which is the whole reason the type is an analysis setting rather than a
        // property of the drawing. There is only ONE layout on screen, so it can draw only one of
        // the two answers, and it has to be able to say which.
        var vm = new LayoutEditorViewModel(PortedLine());

        Assert.Equal("", vm.InternalGapPortsOwner);

        vm.InternalGapPorts      = [(Mm(10), Mm(1.45))];
        vm.InternalGapPortsOwner = "lna_gap";
        Assert.Single(vm.InternalGapPorts);

        // R-em-17's rule, applied to this overlay too: an edited layout drops what an EM setup told
        // it about itself rather than going on drawing marks against moved artwork.
        vm.Model.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(1), Y2 = Mm(1) });
        vm.Model.NotifyChanged();

        Assert.Empty(vm.InternalGapPorts);
        Assert.Equal("", vm.InternalGapPortsOwner);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The wiring — an optional parameter carrying a capability is a capability with no caller
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BothCallersOfTheExtractor_PassThePortKinds()
    {
        foreach (string file in new[]
                 {
                     Path.Combine("..", "..", "..", "..", "..", "src", "Ui", "Layout", "Em", "EmRunService.cs"),
                     Path.Combine("..", "..", "..", "..", "..", "src", "Ui", "Layout", "Em", "EmSetupEditorViewModel.cs"),
                 })
        {
            string src = File.ReadAllText(file);
            int at = src.IndexOf("EmPortExtraction.Extract(", StringComparison.Ordinal);
            Assert.True(at >= 0, $"{file} no longer calls EmPortExtraction.Extract");
            Assert.Contains("ResolvePortKind", src.Substring(at, Math.Min(400, src.Length - at)),
                            StringComparison.Ordinal);
        }
    }
}
