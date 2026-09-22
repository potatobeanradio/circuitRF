// ================================================================
//  PortReturnConductorTests.cs — RP-2b: the port's return is part of what the port IS.
//
//  RP-2a built the kernel's two-cut port and constructed every one of its ports directly. This is
//  how a USER says which return a port has — a property of the port LABEL, in the layout, because a
//  port is drawn there and its return is part of its identity — and how the run says what it did.
//
//  The order below is the brief's own and it matters. Gate 1 comes first: every port drawn before
//  RP-2b is a ground-referenced port, the whole L8/L9 acceptance set is made of them, and none of
//  the rest is worth having if a `.clay` or an extracted problem moved. Then the clone, the
//  layout-authored mixed run against RP-2a's directly-constructed equivalent, the refusals, the
//  note, and `explain`.
// ================================================================

using System.Diagnostics;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Mom;
using Avalonia.Input;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.ViewModels;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public class PortReturnConductorTests(ITestOutputHelper output) : IDisposable
{
    private readonly ITestOutputHelper _out = output;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-rp2b-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private static long Mm(double mm) => (long)Math.Round(mm * 1000 * Dbu);

    // ── Fixture: a signal line with one coplanar return strip beside it ────────────────────────
    //
    // The same shape RP-2a's own mixed-run fixture has (Fr4WithReturnStrip): two parallel
    // conductors on one level over the PCB starter's plane, so a ground-referenced port and a
    // conductor-referenced one are both meaningful on the same artwork.

    private const double LenMm = 8.0, WMm = 1.0, SMm = 0.4;

    /// <summary>Signal strip: y from s/2 to s/2 + w.</summary>
    private static RectShape Signal() => new()
    {
        Layer = TopCopper, X1 = 0, Y1 = Mm(0.5 * SMm), X2 = Mm(LenMm), Y2 = Mm(0.5 * SMm + WMm),
    };

    /// <summary>Return strip: y from −(s/2 + w) to −s/2.</summary>
    private static RectShape Return() => new()
    {
        Layer = TopCopper, X1 = 0, Y1 = Mm(-(0.5 * SMm + WMm)), X2 = Mm(LenMm), Y2 = Mm(-0.5 * SMm),
    };

    private static double YSignal => 0.5 * SMm + 0.5 * WMm;
    private static double YReturn => -YSignal;

    private static LabelShape Port(string text, double xMm, double yMm,
                                   LayoutRotation? direction = LayoutRotation.R0,
                                   LayoutPortReference? reference = null,
                                   (double X, double Y)? returnAt = null) => new()
    {
        Layer         = TopCopper,
        X             = Mm(xMm),
        Y             = Mm(yMm),
        Text          = text,
        Height        = Mm(0.3),
        IsPort        = true,
        PortDirection = direction,
        PortReference = reference,
        PortReturn    = returnAt is { } r ? new LayoutPortReturn(Mm(r.X), Mm(r.Y)) : null,
    };

    private static PlanarExtractionResult Extract(params LayoutShape[] shapes)
        => PlanarExtractor.Extract(shapes, StarterTechnologies.Pcb2Layer(), Dbu, 10e9);

    private static PlanarProblem Problem(params LayoutShape[] shapes)
    {
        var r = Extract(shapes);
        Assert.True(r.Ok, r.Refusal);
        return r.Problem!;
    }

    /// <summary>Every port is an internal delta gap — the only kind a conductor reference is legal
    /// on, and the kind that needs no de-embedding, so a whole solve stays cheap.</summary>
    private static EmSetup GapSetup(int ports) => new()
    {
        PortKinds = [.. Enumerable.Repeat(PlanarPortKind.InternalDeltaGap, ports)],
    };

    /// <summary>Extracts, with the setup's LEGACY port-kind list carried onto the labels first —
    /// which is exactly what <c>EmRunService</c> does for a <c>.cem</c> written before the type moved
    /// onto the drawing (2026-09-14). The fixtures above still state their types the old way, so this
    /// exercises that door as well as the extraction.</summary>
    private static EmPortExtractionResult Ports(EmSetup setup, params LayoutShape[] shapes)
    {
        EmPortKindMigration.ApplyInMemory(shapes, setup.PortKinds);
        return EmPortExtraction.Extract(shapes, Problem(shapes), Dbu, setup.ResolvePortZ0, LayoutUnit.Um);
    }

    private static PlanarMeshSettings MeshSettings() =>
        new(Auto: false, CellsPerWavelength: 12, EdgeMesh: false, MinCellsAcrossConductor: 4);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 1 — R-rp2b-4 + the brief's own "run this first": a ground-referenced port is untouched.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A port that names no return writes no new key</b>, so every <c>.clay</c> in existence is
    /// byte-identical after RP-2b. Asserted on the JSON text rather than on a re-read equality,
    /// because a field that round-trips through <c>null</c> would pass the latter while still adding
    /// <c>"PortReference": null</c> to every port label ever written.
    /// </summary>
    [Fact]
    public void Gate1_AGroundReferencedPortLabel_GainsNoKeyAtAll()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Signal());
        view.Shapes.Add(Port("P1", 0.25 * LenMm, YSignal));
        view.Shapes.Add(Port("P2", 0.75 * LenMm, YSignal));

        string json = LayoutPersistence.Serialize(view);
        _out.WriteLine(json);

        Assert.DoesNotContain("PortReference", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PortReturn", json, StringComparison.Ordinal);

        // …and the round trip is still a fixed point.
        Assert.Equal(json, LayoutPersistence.Serialize(LayoutPersistence.Deserialize(json)));
    }

    /// <summary>
    /// R-rp2b-4's other half: a <c>.clay</c> that DOES carry the fields round-trips them unchanged,
    /// through the file rather than through a clone.
    /// </summary>
    [Fact]
    public void Gate1_AClayCarryingAReturn_RoundTripsBothFields()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Signal());
        view.Shapes.Add(Return());
        view.Shapes.Add(Port("P1", 0.25 * LenMm, YSignal,
                             reference: LayoutPortReference.CoplanarGround,
                             returnAt: (0.25 * LenMm, YReturn)));

        string once  = LayoutPersistence.Serialize(view);
        var reloaded = LayoutPersistence.Deserialize(once);
        Assert.Equal(once, LayoutPersistence.Serialize(reloaded));

        var port = reloaded.Shapes.OfType<LabelShape>().Single(l => l.IsPort);
        Assert.Equal(LayoutPortReference.CoplanarGround, port.PortReference);
        Assert.Equal(new LayoutPortReturn(Mm(0.25 * LenMm), Mm(YReturn)), port.PortReturn);
    }

    /// <summary>
    /// <b>The extracted problem and the extracted PORTS of a ground-referenced layout are exactly
    /// what they were.</b> Every port comes out <c>GroundPlane</c> with no negative terminal, which
    /// is the property the whole L8/L9 acceptance set rests on.
    /// </summary>
    [Fact]
    public void Gate1_AGroundOnlyLayout_ExtractsPortsWithNoNegativeTerminal()
    {
        var shapes = new LayoutShape[]
        {
            Signal(), Return(),
            Port("P1", 0.25 * LenMm, YSignal), Port("P2", 0.75 * LenMm, YSignal),
        };

        var r = Ports(GapSetup(2), shapes);
        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(2, r.Ports.Count);
        foreach (var p in r.Ports)
        {
            Assert.Equal(PlanarPortReference.GroundPlane, p.Reference);
            Assert.Null(p.NegativeLocation);
            Assert.Null(p.NegativeLayerIndex);
            Assert.False(p.IsConductorReferenced);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 2 — R-rp2b-2. LayoutGeometry clones LabelShape field by field.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A property added to <see cref="LabelShape"/> and not to <c>LayoutGeometry</c>'s clone is
    /// dropped SILENTLY on copy, paste and every geometry transform — which for this pair means a
    /// pasted port quietly reverting to the ground plane, i.e. a different structure with no visible
    /// difference. Copy a port carrying a reference; assert it survived.
    /// </summary>
    [Fact]
    public void Gate2_CloningAPortCarryingAReturn_KeepsBothFields()
    {
        var port = Port("P1", 0.25 * LenMm, YSignal,
                        reference: LayoutPortReference.SecondConductor,
                        returnAt: (0.25 * LenMm, YReturn));

        var clone = Assert.IsType<LabelShape>(LayoutGeometry.Clone(port));

        Assert.NotSame(port, clone);
        Assert.Equal(LayoutPortReference.SecondConductor, clone.PortReference);
        Assert.Equal(port.PortReturn, clone.PortReturn);

        // The clone is independent: mutating one must not reach the other.
        clone.PortReturn = new LayoutPortReturn(0, 0);
        Assert.Equal(new LayoutPortReturn(Mm(0.25 * LenMm), Mm(YReturn)), port.PortReturn);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 3 — a layout-authored mixed run, against RP-2a's directly-constructed equivalent.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Two ports drawn in a layout, one plane-referenced and one conductor-referenced, extracted
    /// and solved end to end — and the answer is the one RP-2a gets by constructing the same two
    /// ports directly.</b>
    ///
    /// <para>That equality is the whole point of this brief: the layout half must be a way of SAYING
    /// what RP-2a already builds, not a second way of building it. The two s-matrices are compared
    /// exactly, because both sides run in one process on one mesh and one problem — anything but
    /// equality would mean the extraction handed the kernel a different port.</para>
    /// </summary>
    [Fact]
    public void Gate3_ALayoutAuthoredMixedRun_MatchesTheDirectlyConstructedEquivalent()
    {
        const double f = 5e9;
        var shapes = new LayoutShape[]
        {
            Signal(), Return(),
            Port("P1", 0.25 * LenMm, YSignal),
            Port("P2", 0.75 * LenMm, YSignal,
                 reference: LayoutPortReference.CoplanarGround,
                 returnAt: (0.75 * LenMm, YReturn)),
        };

        var problem = Problem(shapes);
        var drawn   = Ports(GapSetup(2), shapes);
        Assert.True(drawn.Ok, drawn.Refusal);

        // The run really is mixed, and it says so.
        Assert.False(drawn.Ports[0].IsConductorReferenced);
        Assert.True(drawn.Ports[1].IsConductorReferenced);
        Assert.Equal(PlanarPortReference.CoplanarGround, drawn.Ports[1].Reference);

        // RP-2a's own spelling of the same two ports: metres, constructed directly.
        double xM(double mm) => mm * 1e-3;
        var built = new PlanarPort[]
        {
            new(1, new EmPoint(xM(0.25 * LenMm), xM(YSignal)), PlanarPortSide.MinX, 50.0,
                Kind: PlanarPortKind.InternalDeltaGap),
            new(2, new EmPoint(xM(0.75 * LenMm), xM(YSignal)), PlanarPortSide.MinX, 50.0,
                Reference: PlanarPortReference.CoplanarGround,
                Kind: PlanarPortKind.InternalDeltaGap,
                NegativeLocation: new EmPoint(xM(0.75 * LenMm), xM(YReturn))),
        };

        var mesh = SurfaceMesher.Mesh(problem, MeshSettings()).Mesh;
        var a = PlanarPorts.ResolveAll(mesh, drawn.Ports);
        var b = PlanarPorts.ResolveAll(mesh, built);

        foreach (var r in a) _out.WriteLine(r.Describe());

        // The resolutions agree cut for cut before anything is solved — which is where a divergence
        // would actually be, and where it is legible.
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(b[i].Reference, a[i].Reference);
            Assert.Equal(b[i].LayerIndex, a[i].LayerIndex);
            Assert.Equal(b[i].ReferencePlaneM, a[i].ReferencePlaneM);
            Assert.Equal(b[i].BasisIndices, a[i].BasisIndices);
            Assert.Equal(b[i].Negative?.BasisIndices, a[i].Negative?.BasisIndices);
        }

        var sA = PlanarSolve.Run(problem, mesh, a, [f]).Points[0].S;
        var sB = PlanarSolve.Run(problem, mesh, b, [f]).Points[0].S;
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                Assert.Equal(sB[i, j], sA[i, j]);

        _out.WriteLine($"S = [{sA[0, 0]} {sA[0, 1]}; {sA[1, 0]} {sA[1, 1]}]");

        // …and the reference actually changed the answer: with both ports on the plane the same
        // artwork solves to something else. A change of zero would mean the second incidence block
        // never left the layout.
        var plane = new LayoutShape[]
        {
            Signal(), Return(),
            Port("P1", 0.25 * LenMm, YSignal), Port("P2", 0.75 * LenMm, YSignal),
        };
        var groundOnly = Ports(GapSetup(2), plane);
        Assert.True(groundOnly.Ok, groundOnly.Refusal);
        var sPlane = PlanarSolve.Run(problem, mesh,
            PlanarPorts.ResolveAll(mesh, groundOnly.Ports), [f]).Points[0].S;

        double worst = 0;
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                worst = Math.Max(worst, (sA[i, j] - sPlane[i, j]).Magnitude);
        _out.WriteLine($"worst |ΔS| against the all-plane run = {worst:E3}");
        Assert.True(worst > 1e-3,
            $"the drawn return moved the s-matrix by only {worst:E3} — the label's reference never " +
            "reached the incidence matrix");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 4 — R-rp2b-7. Each refusal fires and names its remedy. The SENTENCE is asserted.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Gate4_AReferenceWithNoReturnPoint_IsRefusedNamingWhatIsMissingAndWhereToSayIt()
    {
        var shapes = new LayoutShape[]
        {
            Signal(), Return(),
            Port("P1", 0.25 * LenMm, YSignal, reference: LayoutPortReference.CoplanarGround),
        };

        var r = Ports(GapSetup(1), shapes);
        Assert.False(r.Ok);
        _out.WriteLine(r.Refusal);

        Assert.Contains("does not say WHERE", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("a coplanar ground conductor", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("the return is never guessed", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("Properties inspector", r.Refusal!, StringComparison.Ordinal);

        // The port is still LISTED, with the problem on its own row — that is what the panel needs.
        var row = Assert.Single(r.Rows);
        Assert.False(row.Ok);
        Assert.Equal(r.Refusal, row.Problem);
    }

    [Fact]
    public void Gate4_AReturnPointOffTheMetal_IsRefusedSayingItIsTheRETURNTerminal()
    {
        var shapes = new LayoutShape[]
        {
            Signal(), Return(),
            Port("P1", 0.25 * LenMm, YSignal,
                 reference: LayoutPortReference.CoplanarGround,
                 returnAt: (0.25 * LenMm, YReturn - 5 * WMm)),   // well clear of both strips
        };

        var r = Ports(GapSetup(1), shapes);
        Assert.False(r.Ok);
        _out.WriteLine(r.Refusal);

        Assert.Contains("RETURN terminal", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("is not on any conductor", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("Move the return point onto the metal", r.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void Gate4_AReturnPointOnTheSameConductor_IsRefusedAsADeltaGapInDisguise()
    {
        var shapes = new LayoutShape[]
        {
            Signal(), Return(),
            Port("P1", 0.25 * LenMm, YSignal,
                 reference: LayoutPortReference.SecondConductor,
                 returnAt: (0.60 * LenMm, YSignal)),   // still the SIGNAL strip
        };

        var r = Ports(GapSetup(1), shapes);
        Assert.False(r.Ok);
        _out.WriteLine(r.Refusal);

        Assert.Contains("SAME piece of metal", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("ordinary internal delta gap", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("clear this port's reference", r.Refusal!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// R-rp2b-7's third refusal, and the one that exists specifically because <b>a user reads this
    /// message and not the kernel's</b>: RP-2a refuses a conductor-referenced EDGE port too, but its
    /// remedy is worded for someone constructing a <c>PlanarPort</c>. The layout's names where the
    /// port TYPE actually lives.
    /// </summary>
    [Fact]
    public void Gate4_AConductorReferencedEdgePort_IsRefusedWithTheLayoutSideRemedy()
    {
        var shapes = new LayoutShape[]
        {
            Signal(), Return(),
            Port("P1", 0.0, YSignal,
                 reference: LayoutPortReference.CoplanarGround,
                 returnAt: (0.0, YReturn)),
        };

        // No PortKinds at all — every port is an EDGE port, which is what every setup defaults to.
        var r = Ports(new EmSetup(), shapes);
        Assert.False(r.Ok);
        _out.WriteLine(r.Refusal);

        Assert.Contains("is an EDGE port that returns through drawn metal", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("referenced to nothing", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("'Internal delta gap' in the EM setup's port list", r.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void Gate4_AnInternalToGroundPortWithAReference_IsRefusedByWhatThePortIS()
    {
        var shapes = new LayoutShape[]
        {
            Signal(), Return(),
            Port("P1", 0.25 * LenMm, YSignal,
                 reference: LayoutPortReference.CoplanarGround,
                 returnAt: (0.25 * LenMm, YReturn)),
        };

        var setup = new EmSetup { PortKinds = [PlanarPortKind.Internal] };
        var r = Ports(setup, shapes);
        Assert.False(r.Ok);
        _out.WriteLine(r.Refusal);

        Assert.Contains("negative terminal is the ground plane by construction", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("'Internal delta gap' in the EM setup's port list", r.Refusal!, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 5 — R-rp2b-8. The run's "Every port returns through …" note.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static string ReturnNote(PlanarExtractionResult r) =>
        r.Notes.Single(n => n.StartsWith("Every port ", StringComparison.Ordinal));

    /// <summary>
    /// <b>A run where every port is ground-referenced reads exactly as it did.</b> Character for
    /// character, both spellings' worth — this note is the only place the return plane is visible at
    /// all, and its 2%-scale height is the number the whole R-em-4 trap is in.
    /// </summary>
    [Fact]
    public void Gate5_AGroundOnlyRunsNote_IsUnchanged()
    {
        var note = ReturnNote(Extract(
            Signal(), Return(),
            Port("P1", 0.25 * LenMm, YSignal), Port("P2", 0.75 * LenMm, YSignal)));
        _out.WriteLine(note);

        Assert.Contains(
            "That plane is the negative terminal of every port in this run and is not selectable " +
            "per port; it is modelled as laterally infinite.",
            note, StringComparison.Ordinal);
        Assert.StartsWith("Every port returns through", note, StringComparison.Ordinal);
        Assert.DoesNotContain("names its own return conductor instead", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A mixed run's note names the differing ports</b>, and stops asserting the sentence RP-2a
    /// made false. The plane is still named once with its height — it is still the medium's own
    /// boundary condition and still the return for every port that did not say otherwise.
    /// </summary>
    [Fact]
    public void Gate5_AMixedRunsNote_NamesTheDifferingPortsAndDropsTheFalseSentence()
    {
        var shapes = new LayoutShape[]
        {
            Signal(), Return(),
            Port("P1", 0.25 * LenMm, YSignal),
            Port("P2", 0.75 * LenMm, YSignal,
                 reference: LayoutPortReference.CoplanarGround,
                 returnAt: (0.75 * LenMm, YReturn)),
        };

        var note = ReturnNote(Extract(shapes));
        _out.WriteLine(note);

        Assert.DoesNotContain("is not selectable per port", note, StringComparison.Ordinal);
        Assert.StartsWith("Every port that does not name its own return conductor returns through",
                          note, StringComparison.Ordinal);
        Assert.Contains("'P2' names its own return conductor instead", note, StringComparison.Ordinal);
        Assert.DoesNotContain("'P1'", note, StringComparison.Ordinal);

        // …and the plane is still named, with its height.
        Assert.Contains("µm", note, StringComparison.Ordinal);

        // The CONDUCTOR each return landed on is in that port's own note, which is the only place it
        // is actually known — the extractor sees labels, not a resolved terminal.
        var ports = Ports(GapSetup(2), shapes);
        Assert.True(ports.Ok, ports.Refusal);
        string p2 = ports.Notes.Single(n => n.StartsWith("Port 2", StringComparison.Ordinal));
        _out.WriteLine(p2);
        Assert.Contains("It does NOT return through the ground plane", p2, StringComparison.Ordinal);
        Assert.Contains("a coplanar ground conductor", p2, StringComparison.Ordinal);
        Assert.Contains("'Top Copper (1 oz)'", p2, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 6 — R-rp2b-9. `circuitrf explain` agrees with the EXTRACTION.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Asserted against the extraction, never against a transcription of the rule</b> — RP-1's
    /// own gate 6, one level down. A rule restated in the CLI is a rule that can disagree with the
    /// run, and ruling that out is exactly what a caller asks this verb for.
    ///
    /// <para>Both halves of a mixed run are checked: the ground-referenced port reports the plane,
    /// and the conductor-referenced one reports the conductor its return terminal actually landed on
    /// — read off the resolved <see cref="PlanarPort"/> in process, not off an expected sentence.</para>
    /// </summary>
    [Fact]
    public void Gate6_ExplainReportsEachPortsOwnReturn_AsTheExtractionResolvedIt()
    {
        string cemPath = BuildMixedWorkspace();

        // What the RUN resolves, in process, through the same resolver and the same extraction.
        var setup = EmSetupPersistence.LoadFromFile(cemPath);
        var resolution = EmSetupResolver.Resolve(
            cemPath, setup.LayoutRef, Path.Combine(_root, ".cws"), new TechnologyCache());
        Assert.NotNull(resolution.Source?.Technology);

        var geometry = EmGeometry.Flatten(resolution.Source!.View, resolution.Source.AbsolutePath);
        var planar = PlanarExtractor.Extract(
            geometry.Shapes, resolution.Source.Technology!, resolution.Source.DbuPerMicron, 0,
            setup.ToExtractionSettings(setup.LayoutRef), geometry.GeneratorIds);
        Assert.True(planar.Ok, planar.Refusal);

        EmPortKindMigration.ApplyInMemory(resolution.Source.View.Shapes, setup.PortKinds);
        var ports = EmPortExtraction.Extract(
            resolution.Source.View.Shapes, planar.Problem!, resolution.Source.DbuPerMicron,
            setup.ResolvePortZ0, resolution.Source.View.DisplayUnit);
        Assert.True(ports.Ok, ports.Refusal);

        var conductorReferenced = Assert.Single(ports.Ports, p => p.IsConductorReferenced);
        var neg = conductorReferenced.NegativeLocation!.Value;
        int level = conductorReferenced.NegativeLayerIndex ?? 0;
        string conductor = planar.Problem!.Layers[level].Name;

        var (exit, stdout, stderr) = RunCli("explain", cemPath);
        _out.WriteLine(stdout);
        Assert.Equal(0, exit);

        // Every port has a row once one of them differs — half an answer about a mixed run is worse
        // than none.
        Assert.Contains("port 1 return", stdout, StringComparison.Ordinal);
        Assert.Contains("port 2 return", stdout, StringComparison.Ordinal);
        Assert.Contains("the run's return plane", stdout, StringComparison.Ordinal);

        // …and the conductor-referenced one names what the EXTRACTION resolved.
        Assert.Contains($"drawn metal on '{conductor}'", stdout, StringComparison.Ordinal);
        Assert.Contains($"({neg.X * 1e6:G6} µm, {neg.Y * 1e6:G6} µm)", stdout, StringComparison.Ordinal);
        Assert.Contains("coplanar ground conductor as its return", stdout, StringComparison.Ordinal);

        Assert.True(stderr.Length == 0 || !stderr.Contains("error", StringComparison.OrdinalIgnoreCase), stderr);
    }

    /// <summary>The ground-only case is deliberately silent — the plane step already answers it
    /// completely, and N rows all saying "the plane" would bury the one row that ever differs.</summary>
    [Fact]
    public void Gate6_AGroundOnlyCem_GainsNoPerPortRows()
    {
        string cemPath = BuildMixedWorkspace(mixed: false);

        var (exit, stdout, _) = RunCli("explain", cemPath);
        Assert.Equal(0, exit);
        _out.WriteLine(stdout);

        Assert.Contains("return plane", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("port 1 return", stdout, StringComparison.Ordinal);
    }

    private string BuildMixedWorkspace(bool mixed = true)
    {
        string cellLayoutDir = Path.Combine(_root, "Cps", "layout");
        Directory.CreateDirectory(cellLayoutDir);

        TechPersistence.SaveToFile(
            Path.Combine(_root, "pcb.ctech"), StarterTechnologies.Pcb2Layer());

        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Signal());
        view.Shapes.Add(Return());
        view.Shapes.Add(Port("P1", 0.25 * LenMm, YSignal));
        view.Shapes.Add(mixed
            ? Port("P2", 0.75 * LenMm, YSignal,
                   reference: LayoutPortReference.CoplanarGround,
                   returnAt: (0.75 * LenMm, YReturn))
            : Port("P2", 0.75 * LenMm, YSignal));
        LayoutPersistence.SaveToFile(Path.Combine(cellLayoutDir, "Cps.clay"), view);

        WorkspacePersistence.SaveToFile(
            Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "pcb.ctech" });

        string cemPath = Path.Combine(_root, "cps.cem");
        var setup = GapSetup(2);
        setup.Name      = "cps";
        setup.LayoutRef = Path.Combine("Cps", "layout", "Cps.clay");
        setup.Frequency = new FrequencySpec("5", "5", 1, SweepKind.Linear, "GHz", "GHz");
        EmSetupPersistence.SaveToFile(cemPath, setup);
        return cemPath;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(PortReturnConductorTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string dll = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(dll), $"the CLI was not built beside these tests: {dll}");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(dll);
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-rp2b-10 — the Port tool and the property panel
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Selects a port the way a user does — a click on its own anchor with the Select
    /// tool, which is what the panel's own liveness depends on.</summary>
    private static void SelectPort(LayoutEditorViewModel vm, LabelShape port)
    {
        vm.ActiveTool = LayoutEditorViewModel.Tool.Select;
        vm.OnPointerPressed(port.X, port.Y, default, 1, Mm(0.2));
        vm.OnPointerReleased(port.X, port.Y, default);
        Assert.Contains(vm.Model.Shapes.IndexOf(port), vm.SelectedIndices);
    }

    private static (LayoutEditorViewModel Vm, LayoutShapePropertiesViewModel Props, LayoutView View) Editor()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        view.Shapes.Add(Signal());
        view.Shapes.Add(Return());

        var vm = new LayoutEditorViewModel(view)
        {
            ActiveTool = LayoutEditorViewModel.Tool.Select,
            Technology = StarterTechnologies.Pcb2Layer(),
        };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        return (vm, props, view);
    }

    /// <summary>
    /// <b>A port the Port tool places returns through the plane and reads as it always did.</b> The
    /// tool seeds no reference and no return point — naming one is a deliberate act, and a
    /// nearest-conductor seed here would silently reference the answer to whichever loop happened to
    /// be closest.
    /// </summary>
    [Fact]
    public void ThePortTool_PlacesAGroundReferencedPort()
    {
        var (vm, _, view) = Editor();
        vm.ActiveTool = LayoutEditorViewModel.Tool.Port;
        vm.OnPointerPressed(0, Mm(YSignal), default, 1, 40, 1e-3, 0);

        var port = Assert.Single(view.Shapes.OfType<LabelShape>(), l => l.IsPort);
        Assert.Null(port.PortReference);
        Assert.Null(port.PortReturn);
    }

    /// <summary>
    /// The panel's combo commits the reference undoably, and <b>clearing it back to the plane drops
    /// the return point too</b> — a stored point nothing reads and nothing shows is exactly the kind
    /// of state that comes back to life on the next edit.
    /// </summary>
    [Fact]
    public void ThePanelsReferenceCombo_CommitsUndoably_AndClearingItDropsTheReturnPoint()
    {
        var (vm, props, view) = Editor();
        var port = Port("P1", 0.25 * LenMm, YSignal);
        view.Shapes.Add(port);
        SelectPort(vm, port);

        Assert.True(props.ShowPortDirection);
        Assert.Equal("Ground plane", props.PortReferenceValue);
        Assert.False(props.ShowPortReturn);

        props.PortReferenceValue = "Coplanar ground";
        Assert.Equal(LayoutPortReference.CoplanarGround, port.PortReference);
        Assert.True(props.ShowPortReturn);
        Assert.Contains("not picked", props.PortReturnText, StringComparison.Ordinal);

        // Pick the return conductor: a click on the OTHER strip.
        props.PickPortReturnCommand.Execute(null);
        Assert.True(vm.IsPickingPortReturn);
        vm.OnPointerPressed(Mm(0.25 * LenMm), Mm(YReturn), default, 1, 40, 1e-3, 0);
        Assert.False(vm.IsPickingPortReturn);
        Assert.Equal(new LayoutPortReturn(Mm(0.25 * LenMm), Mm(YReturn)), port.PortReturn);

        // Back to the plane: both fields go.
        props.PortReferenceValue = "Ground plane";
        Assert.Null(port.PortReference);
        Assert.Null(port.PortReturn);

        vm.UndoCommand.Execute(null);
        Assert.Equal(new LayoutPortReturn(Mm(0.25 * LenMm), Mm(YReturn)), port.PortReturn);
    }

    /// <summary>
    /// <b>A click off the metal is refused, and the pick stays armed.</b> The nearest conductor is
    /// deliberately not taken — it is very often not the return path, and referencing a port to the
    /// wrong one changes which loop the answer is about (R-rp2b-10).
    /// </summary>
    [Fact]
    public void PickingAReturn_OffTheMetal_IsRefusedRatherThanGuessed()
    {
        var (vm, props, view) = Editor();
        var port = Port("P1", 0.25 * LenMm, YSignal, reference: LayoutPortReference.CoplanarGround);
        view.Shapes.Add(port);
        SelectPort(vm, port);

        props.PickPortReturnCommand.Execute(null);
        vm.OnPointerPressed(Mm(0.25 * LenMm), Mm(YReturn - 5 * WMm), default, 1, 40, 1e-3, 0);

        Assert.Null(port.PortReturn);
        Assert.True(vm.IsPickingPortReturn);   // still armed — try again, or Escape

        vm.OnKeyDown(Key.Escape, default);
        Assert.False(vm.IsPickingPortReturn);
    }

    /// <summary>The same metal the port is on is refused at the CLICK, where the user is still
    /// holding the gesture that caused it, rather than at Simulate.</summary>
    [Fact]
    public void PickingAReturn_OnThePortsOwnConductor_IsRefusedAtTheClick()
    {
        var (vm, props, view) = Editor();
        var port = Port("P1", 0.25 * LenMm, YSignal, reference: LayoutPortReference.SecondConductor);
        view.Shapes.Add(port);
        SelectPort(vm, port);

        props.PickPortReturnCommand.Execute(null);
        vm.OnPointerPressed(Mm(0.60 * LenMm), Mm(YSignal), default, 1, 40, 1e-3, 0);

        Assert.Null(port.PortReturn);
        Assert.True(vm.IsPickingPortReturn);
    }
}
