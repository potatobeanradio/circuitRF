// ================================================================
//  AssemblyTests.cs — the gate for brief-lvs-13-assemblies.md §7.
//
//  ── THE FIXTURE, ONCE ─────────────────────────────────────────────────────────────────────────
//
//  A package with two leads, one die on it, and bond wires joining the two:
//
//      lead IN  ──wire G1──►  die pad A ─(inside the die)─ die pad B  ──wire G2──►  lead OUT
//
//  The die is a cell of its own with its OWN technology, so this is brief 9 with a wBond in it
//  (R-lvs13-1b) — and every claim below is about the wBond, the boundary, or both.
//
//  ── TWO THINGS THE FIXTURE IS BUILT TO DEFEAT ─────────────────────────────────────────────────
//
//  1. **It is not at 1000 DBU/µm.** At the shipped resolution 1 DBU = 1 nm, so a wire foot's
//     nanometre coordinate and its DBU coordinate are the same integer and a missing conversion is
//     invisible (R-lvs13-3b, the WB-C scar). This board is 400 DBU/µm and every foot is off BOTH
//     axes at a fraction of a micron, so reading a foot raw lands it thousands of microns away.
//  2. **The die's metal is on the SAME layer key as the board's.** Two technologies, two stackups,
//     one integer — which is the coincidence R-lvs13-2a says must never join copper. Only the
//     hierarchy keeps the two apart, and a fixture on a different layer number would pass by
//     accident.
//
//  Fixture paths are anonymized to the SHAPE of a path — a temp folder and invented cell names.
// ================================================================

using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Theming;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using CircuitRF.WBond;
using Symbol = CircuitRF.Design.Symbol.Symbol;

namespace CircuitRF.Ui.Tests.Lvs;

public sealed class AssemblyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-lvs13-" + Guid.NewGuid().ToString("N")[..12]);

    public AssemblyTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ── Units ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>NOT <see cref="LayoutUnits.DefaultDbuPerMicron"/> — see the header.</summary>
    private const int Dbu = 400;

    /// <summary>Microns to this board's DBU.</summary>
    private static long D(double um) => (long)Math.Round(um * Dbu);

    /// <summary>Microns to nanometres — what a <see cref="Wire"/>'s points are in.</summary>
    private static long N(double um) => (long)Math.Round(um * 1000.0);

    private static readonly LayerKey BoardTop = new(1, 0);
    private static readonly LayerKey BoardVia = new(9, 0);

    /// <summary>
    /// <b>The same integer as the board's.</b> R-lvs13-2a is about exactly this: an MMIC's layer 1
    /// and a board's layer 1 are two different metals whose NUMBERS coincide, and the whole class of
    /// defect is copper joined because of it. A fixture on a different layer number would pass by
    /// accident.
    /// </summary>
    private static readonly LayerKey DieMetal = BoardTop;

    // The four feet, in MICRONS of the board's own frame. Every one is off both axes and none is a
    // round number of microns: read as DBU rather than converted, each lands ~2.5x further out.
    private static readonly (double X, double Y) LeadInFoot  = (-2499.75,  62.5);
    private static readonly (double X, double Y) DieAFoot    = (-1300.5,   62.5);
    private static readonly (double X, double Y) DieBFoot    = ( 1300.5,  -62.5);
    private static readonly (double X, double Y) LeadOutFoot = ( 2499.75, -62.5);

    /// <summary>Somewhere with no copper under it at all — R-lvs13-3c.</summary>
    private static readonly (double X, double Y) Nowhere = (0.0, 4000.5);

    // ══ 1 — the brief: one die, two technologies, wires to the package ══════════════════════════

    /// <summary>
    /// <b>Gate 1.</b> A die on a board, bonded to the package leads, compares CLEAN — the die
    /// extracted once on its own terms and stitched at its pads.
    /// </summary>
    /// <remarks>
    /// This is the whole brief in one assertion. The two technologies are reconciled at the
    /// boundary, the die's internals stay inside it, and the only things joining the die to the
    /// package are two bond wires whose feet were located by point-in-piece after a nm → DBU
    /// conversion.
    /// </remarks>
    [Fact]
    public void ADieBondedToItsPackageComparesClean()
    {
        var result = LvsRun.Run(Assembly("Clean"));

        Assert.DoesNotContain(result.Findings, f => f.Severity == DiagnosticSeverity.Error);
        Assert.Equal(2, result.Hierarchy.Extractions);       // the board, and the die once
        Assert.Equal(2, result.Layout.Devices.Count);         // the die placement, and the wBond

        // Clean because both parts were PAIRED, not because there was nothing to pair.
        Assert.Equal(2, result.Comparison.Devices.Count);
    }

    // ══ 2 — the nm ⇄ DBU bridge, at a resolution where it is visible ════════════════════════════

    /// <summary>
    /// <b>Gate 2, written first.</b> A foot's net is right at a NON-DEFAULT resolution and at
    /// coordinates that are not round microns (R-lvs13-3b).
    /// </summary>
    /// <remarks>
    /// <b>The second assertion is the one that makes the first mean anything.</b> Landing on the
    /// right net could be luck; the raw nanometre coordinate landing on NO copper is what says a
    /// conversion happened and that the fixture would have caught its absence. At 1000 DBU/µm both
    /// assertions pass with the conversion deleted, which is the whole trap.
    /// </remarks>
    [Fact]
    public void AFootsNetIsCorrectAtANonDefaultResolution()
    {
        var result = LvsRun.Run(Assembly("Bridge"));

        // The wire into the die reaches the die's own pad A: the same net that pad puts the die
        // placement's terminal A on (R-lvs13-3d).
        Assert.Equal(NetOf(result, "U1", "A"), NetOf(result, "W1", "G1.o"));

        // …and it took the conversion to get there.
        Assert.Equal(D(DieAFoot.X), FootX(result, DieAFoot));
        Assert.True(result.Geometry.Pieces.IndexAt(N(DieAFoot.X), N(DieAFoot.Y), null) < 0,
                    "the un-converted nanometre coordinate must land on nothing");
    }

    // ══ 3 — a foot on nothing ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 3.</b> A foot that lands on no copper is an error naming the array, the wire and the
    /// coordinate (R-lvs13-3c).
    /// </summary>
    [Fact]
    public void AFootOnNothingIsReportedWithItsArrayWireAndCoordinate()
    {
        var result = LvsRun.Run(Assembly("Unbonded", strayFoot: true));

        var finding = Assert.Single(result.Findings, f => f.Id == "lvs.wbond.foot-on-nothing");
        Assert.Equal(DiagnosticSeverity.Error, finding.Severity);
        Assert.Equal("G2", finding.Diagnostic.Arguments["array"]);
        Assert.Equal(1, finding.Diagnostic.Arguments["wire"]);
        Assert.Equal(D(Nowhere.X), finding.Diagnostic.Arguments["x"]);
        Assert.Equal(D(Nowhere.Y), finding.Diagnostic.Arguments["y"]);
    }

    // ══ 4 — a foot on a die's pad reaches the die ═══════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 4.</b> A foot landing on a die's bond pad resolves through the boundary, because a
    /// bond pad IS a boundary pin (R-lvs13-3d).
    /// </summary>
    /// <remarks>
    /// The two wires land on the die's two pads, and those pads are on two DIFFERENT board nets —
    /// which is the hierarchical reading doing its job. The trace joining them inside the die is
    /// out of the board's partition (R-lvs9-2d), so if it were still in, this test would read one
    /// net and fail.
    /// </remarks>
    [Fact]
    public void AFootOnADiesPadReachesThatPadsNetAndNotTheOther()
    {
        var result = LvsRun.Run(Assembly("Stitched"));

        Assert.Equal(NetOf(result, "U1", "A"), NetOf(result, "W1", "G1.o"));
        Assert.Equal(NetOf(result, "U1", "B"), NetOf(result, "W1", "G2.i"));
        Assert.NotEqual(NetOf(result, "U1", "A"), NetOf(result, "U1", "B"));
    }

    // ══ 5 — arrays match by NAME ════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 5.</b> A reordered array list is caught by name BEFORE any net is compared, and it
    /// costs ONE finding rather than a cascade (R-lvs13-4b/4c).
    /// </summary>
    /// <remarks>
    /// <b>The count is the claim.</b> A wBond's pin order IS its array order, so a reorder
    /// re-points every pin while the drawn wiring stays put — comparing its nets would produce 2M
    /// findings that are individually true and collectively about the wrong thing. So the instance
    /// leaves BOTH netlists and the drift is the only thing said about it.
    /// </remarks>
    [Fact]
    public void AReorderedArrayListIsOneFindingAndTheWBondIsNotCompared()
    {
        var result = LvsRun.Run(Assembly("Drifted", reorderArrays: true));

        var drift = Assert.Single(result.Findings, f => f.Id.StartsWith("lvs.wbond.", StringComparison.Ordinal));
        Assert.Equal("lvs.wbond.array-drift", drift.Id);
        Assert.Equal(DiagnosticSeverity.Error, drift.Severity);

        Assert.DoesNotContain(result.Layout.Devices, d => d.Path == "W1");
        Assert.DoesNotContain(result.Schematic.Devices, d => d.Path == "W1");
    }

    // ══ 6 — Carried or Linked, and the report says which ════════════════════════════════════════

    /// <summary>
    /// <b>Gate 6.</b> The two wire sources give different answers on a drifted instance; LVS reads
    /// the one the ENGINE would read, and names it (R-lvs13-5b/5c).
    /// </summary>
    /// <remarks>
    /// The <c>.wBond</c> beside the artwork has one foot hanging in space and the carried payload
    /// does not. Carried therefore reads clean and warns that the two have parted; Linked reads the
    /// unbonded wire. Verifying the wrong one verifies a design nobody runs.
    /// </remarks>
    [Fact]
    public void CarriedAndLinkedReadDifferentWiresAndTheReportNamesWhich()
    {
        var carried = LvsRun.Run(Assembly("Carried", sidecarWithStrayFoot: true));
        var linked  = LvsRun.Run(Assembly("Linked", sidecarWithStrayFoot: true, linkToSidecar: true));

        Assert.Equal("Carried", Read(carried).Diagnostic.Arguments["source"]);
        Assert.DoesNotContain(carried.Findings, f => f.Id == "lvs.wbond.foot-on-nothing");
        var stale = Assert.Single(carried.Findings, f => f.Id == "lvs.wbond.payload-drift");
        Assert.Equal(DiagnosticSeverity.Warning, stale.Severity);

        Assert.Equal("Linked", Read(linked).Diagnostic.Arguments["source"]);
        Assert.Contains(linked.Findings, f => f.Id == "lvs.wbond.foot-on-nothing");
        Assert.DoesNotContain(linked.Findings, f => f.Id == "lvs.wbond.payload-drift");
    }

    // ══ 7 — the stamped capacitors are not devices ══════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 7.</b> A wBond's shunt capacitors are part of its own model and appear at no symbol,
    /// so neither side counts them (R-lvs13-5d).
    /// </summary>
    /// <remarks>
    /// Stated because it is exactly the kind of thing that looks like an omission: with capacitance
    /// ON — which is the shipped default and is asserted here so the claim is not vacuous — the
    /// component stamps three per array, and emitting them would make every wBond report M extra
    /// unmatched capacitors.
    /// </remarks>
    [Fact]
    public void AWBondsStampedCapacitorsAreNotDevices()
    {
        Assert.True(Wires().IncludeCapacitance);

        var result = LvsRun.Run(Assembly("Counted"));

        Assert.Equal(result.Schematic.Devices.Count, result.Layout.Devices.Count);
        Assert.DoesNotContain(
            result.Layout.Devices, d => d.Type.Kind == DeviceKind.Capacitor);
    }

    // ══ 8 — no connection by layer coincidence ══════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 8.</b> A die whose metal OVERLAPS the board's own, on the same layer key, with no
    /// pin, no via and no wire between them: two nets (R-lvs13-2a).
    /// </summary>
    /// <remarks>
    /// <b>This is the test that catches the whole class.</b> The die's strip runs out of its pad B
    /// and under a board trace that reaches lead OUT — so if copper crossed the boundary by
    /// coincidence, the wBond's G2 array would have both its terminals on one net. The
    /// <c>--flat</c> run is the control: there the two ARE one net, which is what a flat reading
    /// correctly says about one flat graph, and it is the reason this test can fail.
    /// </remarks>
    [Fact]
    public void OverlappingCopperAcrossATechnologyBoundaryIsTwoNets()
    {
        string board = Assembly("Coincide", dieOverlapsBoard: true);

        var hierarchical = LvsRun.Run(board);
        Assert.NotEqual(NetOf(hierarchical, "W1", "G2.i"), NetOf(hierarchical, "W1", "G2.o"));

        // And it is reported rather than absorbed — R-lvs9-3, which is what makes the separation a
        // decision the report states rather than a silence.
        Assert.Contains(hierarchical.Findings, f => f.Id == "lvs.hierarchy.undeclared-contact");

        var flat = LvsRun.Run(board, new LvsRunOptions { Flat = true });
        Assert.Equal(NetOf(flat, "W1", "G2.i"), NetOf(flat, "W1", "G2.o"));
    }

    // ══ 9 — a pending reconciliation refuses that sub-cell ══════════════════════════════════════

    /// <summary>
    /// <b>Gate 9.</b> A layer the two technologies cannot confidently reconcile makes that die
    /// unextractable, reported and never guessed at (R-lvs13-2c).
    /// </summary>
    [Fact]
    public void APendingLayerReconciliationRefusesThatSubCell()
    {
        var result = LvsRun.Run(Assembly("Pending", unmappableDieLayer: true));

        var refusal = Assert.Single(result.Findings, f => f.Id == "lvs.layout.pending-layer-mapping");
        Assert.Equal(DiagnosticSeverity.Error, refusal.Severity);
    }

    // ══ 10 — a die with no ground reference of its own ══════════════════════════════════════════

    /// <summary>
    /// <b>Gate 10.</b> A die whose stackup flags no ground reference warns and still compares
    /// correctly, because its ground arrives through a wire (R-lvs13-6b, R-lvs3-6d).
    /// </summary>
    /// <remarks>
    /// <b>This is the case that rule was written for.</b> The reference conductor of this assembly
    /// lives in the PACKAGE — the board's undrawn backside, reached by a stitching via — and the
    /// wBond's own REF terminal resolves to it (R-lvs13-6a) while the die, correctly, has nothing of
    /// the kind. A design that refused here would refuse every MMIC.
    /// </remarks>
    [Fact]
    public void ADieWithNoGroundReferenceWarnsAndStillCompares()
    {
        var result = LvsRun.Run(Assembly("Grounded", referencePin: true));

        var die = Assert.Single(result.Cells);
        Assert.Contains(die.Result.Findings, f => f.Id == "lvs.ground.no-reference-conductor"
                                                  && f.Severity == DiagnosticSeverity.Warning);

        // The package's reference is what the wBond's REF terminal found: net "0".
        Assert.Equal("0", result.Layout.Nets[NetOf(result, "W1", "REF")].Label);
        Assert.DoesNotContain(result.Findings, f => f.Severity == DiagnosticSeverity.Error);
    }

    // ══ 11 — an array with no wires ═════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 11, and the one place this brief's own text is contradicted by the application.</b>
    /// The EXTRACTION reads an array with no wires as two opens and says nothing else about it
    /// (R-lvs13-4d) — but the design does not elaborate, because an empty array is refused.
    /// </summary>
    /// <remarks>
    /// <b>R-lvs13-4d calls an empty array "an ordinary mid-design state". It is not one, and the
    /// refusal predates this brief by a long way.</b> <c>WBondDesign.Validate</c> rejects it —
    /// an empty array makes the mapping matrix rank-deficient and the array-basis inductance
    /// singular — and the schematic's own array editor deliberately cannot create one, adding every
    /// new array with a wire already in it for exactly this reason. So the state is reachable only
    /// by hand-editing, and a design in it cannot be simulated.
    ///
    /// <para>What brief 13 can honestly promise is the half it owns: the READING does not fall
    /// over, does not invent a net and does not report a foot that is not there. The elaboration
    /// refusal is the engine's own sentence, carried unmodified, and it is asserted here rather
    /// than filtered out so that nobody reads this test as evidence the state is supported.</para>
    /// </remarks>
    [Fact]
    public void AnArrayWithNoWiresIsTwoOpensAndNotAWBondFinding()
    {
        var result = LvsRun.Run(Assembly("Empty", emptyThirdArray: true));

        int i = NetOf(result, "W1", "G3.i"), o = NetOf(result, "W1", "G3.o");
        Assert.NotEqual(i, o);
        Assert.Single(result.Layout.Nets[i].Pins);
        Assert.Single(result.Layout.Nets[o].Pins);
        Assert.DoesNotContain(result.Findings, f => f.Id.StartsWith("lvs.wbond.", StringComparison.Ordinal)
                                                    && f.Severity == DiagnosticSeverity.Error);

        // …and the refusal that is genuinely there, named rather than hidden.
        var refused = Assert.Single(result.Findings, f => f.Id == "lvs.schematic.elaboration-failed");
        Assert.Contains("has no wires", refused.Render(), StringComparison.Ordinal);
    }

    // ── Reading a result ────────────────────────────────────────────────────────────────────────

    /// <summary>The layout-side net one named terminal of one named device is on.</summary>
    private static int NetOf(LvsRunResult result, string device, string terminal)
    {
        var d = Assert.Single(result.Layout.Devices, x => x.Path == device);
        return Assert.Single(d.Terminals, t => t.Name == terminal).NetIndex;
    }

    /// <summary>Where the pad recorded for one foot landed, in DBU.</summary>
    private static long FootX(LvsRunResult result, (double X, double Y) foot)
    {
        int device = result.Layout.Devices.ToList().FindIndex(d => d.Path == "W1");
        return Assert.Single(
            result.Geometry.PadsOfDevice(device)
                  .Select(i => result.Geometry.Pads[i]), p => p.Y == D(foot.Y) && p.X == D(foot.X)).X;
    }

    /// <summary>The line that says which wires were read — R-lvs13-5b.</summary>
    private static LvsFinding Read(LvsRunResult result)
        => Assert.Single(result.Findings, f => f.Id == "lvs.wbond.wires-read");

    // ── The wires ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The assembly's two bond wires, in the board's own frame and in NANOMETRES.</summary>
    private static WBondDesign Wires(
        bool strayFoot = false, bool emptyThirdArray = false, bool reorder = false)
    {
        var g1 = Array("G1", LeadInFoot, DieAFoot);
        var g2 = Array("G2", DieBFoot, strayFoot ? Nowhere : LeadOutFoot);

        var design = new WBondDesign();
        design.Arrays.AddRange(reorder ? [g2, g1] : [g1, g2]);
        if (emptyThirdArray) design.Arrays.Add(new WireArray { Name = "G3" });
        return design;
    }

    private static WireArray Array(string name, (double X, double Y) from, (double X, double Y) to)
    {
        long z = WBondUnits.ToNm(WBondEmbedding.DefaultWire.FootZMils, WBondUnit.Mil);
        long apex = WBondUnits.ToNm(WBondEmbedding.DefaultWire.LoopHeightMils, WBondUnit.Mil);
        var wire = new Wire();
        wire.Points.Add(new Point3(N(from.X), N(from.Y), z));
        wire.Points.Add(new Point3((N(from.X) + N(to.X)) / 2, (N(from.Y) + N(to.Y)) / 2, apex));
        wire.Points.Add(new Point3(N(to.X), N(to.Y), z));
        return new WireArray { Name = name, Wires = { wire } };
    }

    // ── The assembly ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The package, the die on it, the wires between them and the drawing of all three.
    /// </summary>
    /// <param name="strayFoot">Move array G2's far foot off every piece of copper.</param>
    /// <param name="reorderArrays">Record an array list the instance's wiring was NOT drawn
    /// against — R-lvs13-4c's drift.</param>
    /// <param name="sidecarWithStrayFoot">Write a <c>.wBond</c> beside the artwork that differs
    /// from the carried payload, which is R-lvs13-5c's recoverable state.</param>
    /// <param name="linkToSidecar">Point the instance at that file and set <c>Source=Linked</c>.</param>
    /// <param name="dieOverlapsBoard">Run the die's own metal under a board trace, with nothing
    /// declared between them — R-lvs13-2a.</param>
    /// <param name="unmappableDieLayer">Give the die a layer the board's technology cannot be
    /// matched to — R-lvs13-2c.</param>
    /// <param name="referencePin">Expose the wBond's REF terminal and wire it to ground.</param>
    /// <param name="emptyThirdArray">Declare an array and draw no wires in it — R-lvs13-4d.</param>
    private string Assembly(
        string name, bool strayFoot = false, bool reorderArrays = false,
        bool sidecarWithStrayFoot = false, bool linkToSidecar = false,
        bool dieOverlapsBoard = false, bool unmappableDieLayer = false,
        bool referencePin = false, bool emptyThirdArray = false)
    {
        WriteTechnologies(name, unmappableDieLayer);
        string die = Die(name, dieOverlapsBoard);

        string cellDir = CellFolder.CreateCellFolder(_root, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);

        // ── The package ────────────────────────────────────────────────────────────────────────
        var view = new LayoutView
        {
            DbuPerMicron = Dbu,
            TechRef = Path.Combine("..", "..", "tech", name + "-Board.ctech"),
        };

        view.Shapes.Add(Rect(BoardTop, D(-2600), D(-100), D(-2400), D(100)));    // lead IN
        view.Shapes.Add(Rect(BoardTop, D(2400), D(-100), D(2600), D(100)));      // lead OUT
        view.Pins.Add(Pin("P1", D(-2500), 0, BoardTop));
        view.Pins.Add(Pin("P2", D(2500), 0, BoardTop));

        // The package's own reference: a pad on top and a stitching barrel down to the backside
        // conductor the stackup flags and the artwork does not draw.
        view.Shapes.Add(Rect(BoardTop, D(-200), D(-1200), D(200), D(-800)));
        view.Shapes.Add(Rect(BoardVia, D(-100), D(-1100), D(100), D(-900)));

        // R-lvs13-2a: board copper reaching from lead OUT to under the die's own strip.
        if (dieOverlapsBoard) view.Shapes.Add(Rect(BoardTop, D(1900), D(-40), D(2450), D(40)));

        view.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(layoutDir, die),
            X = 0, Y = 0, Mag = 1.0, RefDes = "U1", SchematicId = "U1",
        });

        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, name + ".clay"), view);

        var wires = Wires(strayFoot, emptyThirdArray);
        if (sidecarWithStrayFoot)
            WBondIo.WriteFile(Path.Combine(layoutDir, name + ".wBond"), Wires(strayFoot: true));

        // ── The drawing ────────────────────────────────────────────────────────────────────────
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        Directory.CreateDirectory(schematicDir);

        var model = new SchematicEditModel { SchematicDirectory = schematicDir };

        var wb = WBondPlacement.BuildCarrying(wires, "W1");
        SetParameter(wb, "RefPin", referencePin ? "true" : "false");
        if (linkToSidecar)
            WBondPlacement.LinkTo(wb, Path.Combine(layoutDir, name + ".wBond"), schematicDir);
        if (reorderArrays)
            SetParameter(wb, WBondPlacement.ArraysParameter,
                         WBondSymbolProvider.ArraysKeyOf(Wires(reorder: true)));
        model.Components.Add(wb);

        // ── The drawing's topology, built by COINCIDENCE rather than by routing ────────────────
        //
        // A schematic port landing exactly on another is a connection, so placing each part so its
        // pin sits on the wBond's needs no wire and cannot accidentally run one over a third pin —
        // which is the failure mode of hand-routed fixture geometry and is invisible when it
        // happens: the extraction is right and the drawing is not what the test author meant.
        var u1 = new EditableComponent
        {
            InstanceName = "U1", Symbol = SymbolKind.Generic,
            CellRef = Path.GetRelativePath(schematicDir, die),
        };
        model.Components.Add(u1);

        var p1 = PortPin("P1", 1);
        var p2 = PortPin("P2", 2);
        model.Components.Add(p1);
        model.Components.Add(p2);

        PlacePortAt(model, p1, 0, PortAt(model, wb, 0));     // P1 on G1.i
        PlacePortAt(model, u1, 0, PortAt(model, wb, 1));     // U1.A on G1.o
        PlacePortAt(model, p2, 0, PortAt(model, wb, 3));     // P2 on G2.o

        // The one connection the geometry cannot make by touching: the die's far pad back to the
        // wBond's other input, routed well clear of every pin on both sides.
        var (bx, by) = PortAt(model, u1, 1);
        var (g2x, g2y) = PortAt(model, wb, 2);
        model.Wires.Add(Wire(
            (bx, by), (bx, -2400), (g2x - 600, -2400), (g2x - 600, g2y), (g2x, g2y)));

        // R-lvs13-4d. Declared and never drawn — so its two pins are wired to nothing on the
        // drawing either, which is what makes both sides agree that the array is open.
        if (referencePin)
        {
            var gnd = new EditableComponent { InstanceName = "GND1", Symbol = SymbolKind.Ground };
            model.Components.Add(gnd);
            PlacePortAt(model, gnd, 0, PortAt(model, wb, model.PortDefsOf(wb).Count - 1));
        }

        File.WriteAllText(
            Path.Combine(schematicDir, name + ".csch"), SchematicPersistence.Serialize(model));

        // The board's own terminal map: symbol port ↔ layout pin, BY NAME.
        SymbolPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), name + ".csym"),
            new Symbol([], [new SymbolPin(0, 0, 1, "P1"), new SymbolPin(0, 100, 2, "P2")], 2));
        SetPorts(cellDir, 2);

        return cellDir;
    }

    // ── The die ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A cell of its own, with its OWN technology: two bond pads and the metal joining them.
    /// </summary>
    /// <remarks>
    /// <b>The joining trace covers NEITHER declared pin</b>, which is what keeps it out of the
    /// board's partition when the die is read as a cell (R-lvs9-2d) — and therefore what makes the
    /// die's two pads two different board nets for the wires to land on.
    /// </remarks>
    private string Die(string name, bool overlapsBoard)
    {
        string cellDir = CellFolder.CreateCellFolder(_root, name + "-Die");
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);

        var view = new LayoutView
        {
            DbuPerMicron = Dbu,
            TechRef = Path.Combine("..", "..", "tech", name + "-Die.ctech"),
        };

        view.Pins.Add(Pin("A", D(-1300.5), 0, DieMetal));
        view.Pins.Add(Pin("B", D(1300.5), 0, DieMetal));
        view.Shapes.Add(Rect(DieMetal, D(-1400), D(-125), D(-1000), D(125)));
        view.Shapes.Add(Rect(DieMetal, D(1000), D(-125), D(1400), D(125)));
        view.Shapes.Add(Rect(DieMetal, D(-1100), D(-40), D(1100), D(40)));

        // R-lvs13-2a: the die's own metal, running out under the package's.
        if (overlapsBoard) view.Shapes.Add(Rect(DieMetal, D(1350), D(-40), D(2000), D(40)));

        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, name + "-Die.clay"), view);

        var model = new SchematicEditModel();
        var a = PortPin("A", 1);
        var b = PortPin("B", 2);
        a.X = -400; b.X = 400;
        model.Components.Add(a);
        model.Components.Add(b);
        model.Wires.Add(Wire(a.GetPortWorldCoord(0), b.GetPortWorldCoord(0)));

        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        Directory.CreateDirectory(schematicDir);
        File.WriteAllText(Path.Combine(schematicDir, name + "-Die.csch"),
                          SchematicPersistence.Serialize(model));

        SymbolPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), name + "-Die.csym"),
            new Symbol([], [new SymbolPin(-200, 0, 1, "A"), new SymbolPin(200, 0, 2, "B")], 2));
        SetPorts(cellDir, 2);

        return cellDir;
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────────

    private static (double X, double Y) PortAt(SchematicEditModel model, EditableComponent comp, int index)
    {
        var defs = model.PortDefsOf(comp);
        Assert.True(index < defs.Count, $"'{comp.InstanceName}' has {defs.Count} ports, not {index + 1}");
        return model.PortWorldOf(comp, defs[index]);
    }

    private static EditableWire Wire(params (double X, double Y)[] points)
    {
        var w = new EditableWire();
        w.Points.AddRange(points);
        return w;
    }

    private static EditableComponent PortPin(string name, int num)
    {
        var p = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Pin };
        p.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
        return p;
    }

    /// <summary>Moves <paramref name="comp"/> so one of its ports sits exactly on
    /// <paramref name="target"/> — which is a connection, with no wire to route.</summary>
    private static void PlacePortAt(
        SchematicEditModel model, EditableComponent comp, int portIndex, (double X, double Y) target)
    {
        comp.X = 0; comp.Y = 0;
        var (px, py) = PortAt(model, comp, portIndex);
        comp.X = target.X - px;
        comp.Y = target.Y - py;
    }

    private static void SetParameter(EditableComponent comp, string name, string value)
    {
        var p = comp.Parameters.FirstOrDefault(q => q.Name == name);
        if (p is null) comp.Parameters.Add(new EditableParameter { Name = name, Expression = value });
        else p.Expression = value;
    }

    private static LayoutPin Pin(string name, long x, long y, LayerKey layer) =>
        new() { Name = name, X = x, Y = y, WidthDbu = D(150), Layer = layer };

    private static RectShape Rect(LayerKey layer, long x1, long y1, long x2, long y2) =>
        new() { Layer = layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    private static void SetPorts(string cellDir, int ports)
    {
        string path = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(path);
        ccell.NumPorts = ports;
        CellPersistence.SaveToFile(path, ccell);
    }

    // ── The two technologies ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The package's stackup and the die's — <b>two of them, which is what makes this an
    /// assembly</b>.
    /// </summary>
    /// <remarks>
    /// The die numbers its metal 1 and calls it "Top", exactly as the package does — so the flatten
    /// reconciles them as the same layer and nothing has to be confirmed. That is the coincidence
    /// R-lvs13-2a is about, and the artwork is then indistinguishable by layer alone.
    /// <paramref name="unmappable"/> renames the die's layer instead: same key, different name,
    /// which is the Drill→Substrate trap nothing can settle and which therefore refuses.
    /// </remarks>
    private void WriteTechnologies(string name, bool unmappable)
    {
        string techDir = Path.Combine(_root, "tech");
        Directory.CreateDirectory(techDir);

        var board = new Technology { Name = "Package" };
        board.Layers =
        [
            new LayerDef { Key = BoardTop, Name = "Top", ZOrder = 0, Color = new Rgba(200, 80, 40, 255) },
            new LayerDef { Key = BoardVia, Name = "Via", ZOrder = 1, Color = new Rgba(120, 120, 120, 255) },
        ];
        board.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = D(35), SigmaSm = 5.8e7,
                DrawingLayers = [BoardTop],
            },
            new StackupLayer
            {
                Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [BoardVia],
                SpanFromLayer = "Top", SpanToLayer = "Paddle",
            },
            new StackupLayer
            {
                Kind = StackupKind.Dielectric, Name = "Mold", ThicknessDbu = D(200), Epsr = 3.6, TanD = 0.01,
            },
            // The reference conductor DRAWS NOTHING — a package paddle, which is what makes the
            // ground reading an inference rather than a piece of artwork (R-lvs3-6).
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "Paddle", ThicknessDbu = D(35), SigmaSm = 5.8e7,
                IsGroundReference = true,
            },
        ];
        TechPersistence.SaveToFile(Path.Combine(techDir, name + "-Board.ctech"), board);

        var die = new Technology { Name = "Die" };
        die.Layers =
        [
            new LayerDef
            {
                Key = DieMetal, Name = unmappable ? "DieMetal" : "Top",
                ZOrder = 0, Color = new Rgba(220, 200, 60, 255),
            },
        ];
        die.Stackup.Layers =
        [
            // No IsGroundReference anywhere — R-lvs13-6b's die, grounded through the package.
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "Metal", ThicknessDbu = D(3), SigmaSm = 4.1e7,
                DrawingLayers = [DieMetal],
            },
        ];
        TechPersistence.SaveToFile(Path.Combine(techDir, name + "-Die.ctech"), die);

        WorkspacePersistence.SaveToFile(
            Path.Combine(_root, ".cws"),
            new CwsFile { DefaultTechRef = Path.Combine("tech", name + "-Board.ctech") });
    }
}
