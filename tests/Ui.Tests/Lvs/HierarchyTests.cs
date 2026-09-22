// ================================================================
//  HierarchyTests.cs — the gate for brief-lvs-9-hierarchy.md §7.
//
//  ── WHAT IS BEING PINNED ──────────────────────────────────────────────────────────────────────
//
//  Each distinct cell is extracted ONCE, in its own frame; a placement of it is one device whose
//  terminals are its boundary pins, stitched into the parent by transform and point-in-piece; and
//  a cell whose copper reaches the parent anywhere else is REPORTED rather than absorbed.
//
//  The gate on the whole brief is gate 1: a flat run and a hierarchical run over the same
//  documents produce the same findings, in the same order. Everything else here is one claim per
//  test — the counters, the transform, the contact, the ceiling, the key, and the write that must
//  not happen.
//
//  ── ONE NARROWING OF R-lvs9-4b, AND IT IS STATED RATHER THAN HIDDEN ───────────────────────────
//
//  The brief calls the flat reading the ORACLE the hierarchical one is gated against. Brief 3's
//  flat reading is root-only by construction — "the root's own placements and no deeper"
//  (R-ab1-1a), which `LayoutDesignFlatten`'s own note repeats — so it cannot see a device inside a
//  module at all, and it therefore cannot be an oracle for what is inside one. What the two runs
//  are gated on is what they CAN both answer: the parent's own findings, in order. That is the
//  substantial claim either way, because the hierarchical run builds its partition with every
//  module's internals REMOVED, and the gate is that removing them changed nothing.
//
//  src/Design/RESOLVED.md records the reasoning.
//
//  Fixture paths are anonymized to the SHAPE of a path — a temp folder and invented cell names.
// ================================================================

using System.Linq;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.Theming;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using Symbol = CircuitRF.Design.Symbol.Symbol;

namespace CircuitRF.Ui.Tests.Lvs;

public sealed class HierarchyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-lvs9-" + Guid.NewGuid().ToString("N")[..12]);

    public HierarchyTests()
    {
        Directory.CreateDirectory(_root);
        WriteTechnology();
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);
    private static readonly LayerKey ViaL = new(9, 0);

    // The module's own boundary pins, OFF BOTH AXES — the WB-C trap in its second form. A mirror
    // about a symmetric pair is a no-op and a test of it proves nothing.
    private static readonly (string Pin, long X, long Y)[] ModulePins =
        [("A", -Um(1500), Um(400)), ("B", Um(1500), -Um(600))];

    // ══ 1 — THE BRIEF: flat and hierarchical agree ═══════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 1.</b> The same documents, read flat and read hierarchically, produce the same
    /// findings — same ids, same objects, same order (R-lvs9-4b).
    /// </summary>
    /// <remarks>
    /// <b>This is what makes the hierarchy safe to turn on.</b> A hierarchical run takes every
    /// module's internal copper OUT of the parent's partition (R-lvs9-2d) and stitches the boundary
    /// back through the pins alone; if that were wrong, nets would move and this test would show it
    /// as a different report. A run that merely agreed on the COUNT would not.
    /// </remarks>
    [Fact]
    public void AFlatRunAndAHierarchicalRunReportTheSameThingInTheSameOrder()
    {
        string board = Board("Agree", 0, mirror: false, contact: false);

        var hierarchical = LvsRun.Run(board);
        var flat = LvsRun.Run(board, new LvsRunOptions { Flat = true });

        Assert.Equal(Signature(flat), Signature(hierarchical));

        // And the hierarchy was actually engaged, so the equality above is not the equality of two
        // identical readings.
        Assert.Equal(2, hierarchical.Hierarchy.Extractions);
        Assert.Equal(1, flat.Hierarchy.Extractions);
    }

    // ══ 2 — one extraction per distinct cell ════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 2.</b> Forty placements of one cell extract it <b>once</b> (R-lvs9-5a), and the
    /// other thirty-nine are cache hits.
    /// </summary>
    /// <remarks>
    /// The root counts as an extraction of its own, so the number is two — one board, one cell.
    /// This is the counter that catches the O(n) reading the hierarchy exists to replace, and it
    /// measures no clock (R-lvs9-5b/5c).
    /// </remarks>
    [Fact]
    public void FortyPlacementsOfOneCellExtractItOnce()
    {
        var result = LvsRun.Run(Scale("Forty", 40));

        Assert.Equal(2, result.Hierarchy.Extractions);
        Assert.Equal(39, result.Hierarchy.CacheHits);
        Assert.Equal(1, result.Cells.Count);
        Assert.Equal(40, result.Cells[0].Placements.Count);
    }

    // ══ 3 — a PCell is its parameters ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 3.</b> Two resolved parameter sets of one generated cell are two cache entries
    /// (R-lvs9-1d) — and two identical ones are one.
    /// </summary>
    [Fact]
    public void TwoParameterSetsOfOnePCellAreTwoExtractions()
    {
        string cell = Module("KeyedByParameters", contact: false);
        var tech = TwoLayerTech();

        var w10 = Generated(10);
        var w20 = Generated(20);

        Assert.NotEqual(LvsCellKey.Of(cell, w10, tech), LvsCellKey.Of(cell, w20, tech));
        Assert.Equal(LvsCellKey.Of(cell, w10, tech), LvsCellKey.Of(cell, Generated(10), tech));
    }

    // ══ 4 — the stitch, under every transform ═══════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 4.</b> A module at 90°, at 217° and mirrored: its boundary pins land on the parent
    /// nets hand arithmetic says they do (R-lvs9-2a).
    /// </summary>
    /// <remarks>
    /// <b>The whole board is transformed together</b>, so the answer is required to be invariant
    /// and the fixture states the arithmetic rather than a test restating it. The module's pins are
    /// off BOTH axes, so no reflection and no cardinal rotation maps either onto the other or onto
    /// itself; 217° sends every rectangle through <c>LayoutRotationPromotion</c> on the way out of
    /// the flatten.
    ///
    /// <para>Both terminals must be on parent copper, which is the half that matters: the module's
    /// own internals are NOT in this partition, so a pin that failed to land would read as an open
    /// rather than quietly finding the module's own trace.</para>
    /// </remarks>
    [Theory]
    [InlineData(0.0, false)]
    [InlineData(90.0, false)]
    [InlineData(217.0, false)]
    [InlineData(0.0, true)]
    public void BoundaryPinsLandOnTheParentNetsUnderEveryTransform(double deg, bool mirror)
    {
        var result = LvsRun.Run(Board("Xf" + deg + mirror, deg, mirror, contact: false));

        var module = Assert.Single(result.Layout.Devices, d => d.Path == "U1");
        Assert.Equal(2, module.Terminals.Count);
        Assert.Equal(2, module.Terminals.Select(t => t.NetIndex).Distinct().Count());
        Assert.DoesNotContain(result.Findings, f => f.Id == "lvs.pin.no-copper");

        // Each pin landed on a piece of copper, and the two are different pieces.
        var pads = result.Geometry.PadsOfDevice(0).Select(i => result.Geometry.Pads[i]).ToArray();
        Assert.Equal(2, pads.Length);
        Assert.Equal(2, pads.Select(p => p.Piece).Distinct().Count());
        Assert.All(pads, p => Assert.True(p.Piece >= 0));

        // And that piece is the PARENT's trace rather than the module's own pad: the trace is
        // 1.02 mm long and the pad is 250 µm square, so no rotation of the pad alone reaches
        // 500 µm. Without this the test would pass on a module wired to nothing.
        Assert.All(pads, p =>
        {
            var box = result.Geometry.BoundsOfPiece(p.Piece);
            Assert.True(Math.Max(box.MaxX - box.MinX, box.MaxY - box.MinY) > Um(500));
        });
    }

    // ══ 5 — undeclared contact ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 5.</b> A cell whose copper meets the parent's away from every pin it declares is
    /// reported — naming the cell, the placement, the layer and the coordinate, with a marker
    /// (R-lvs9-3b) — and the same design under <c>--flatten-cell</c> reports the flatten at info
    /// and no contact (R-lvs9-3d).
    /// </summary>
    /// <remarks>
    /// <b>Absorbing it would be the defect this tool exists to find, committed by the tool.</b> The
    /// hierarchy would say the module reaches the design through two pins while the copper says it
    /// reaches it through a third place nobody declared, and every net either side of that place
    /// would be wrong with nothing to look at.
    /// </remarks>
    [Fact]
    public void UndeclaredContactIsReportedAndFlattenCellIsTheEscapeHatch()
    {
        string board = Board("Contact", 0, mirror: false, contact: true);

        var reported = LvsRun.Run(board);
        var contact = Assert.Single(reported.Findings, f => f.Id == "lvs.hierarchy.undeclared-contact");

        Assert.Equal(DiagnosticSeverity.Error, contact.Severity);
        Assert.Equal("U1", contact.Diagnostic.Arguments["path"]);
        Assert.Equal("Contact-Mod", contact.Diagnostic.Arguments["cellName"]);
        Assert.Equal("Top", contact.Diagnostic.Arguments["layerName"]);
        Assert.True(contact.HasMarker);

        // The coordinate is inside the slab the fixture drew — (4.0, 5.9) mm to (5.0, 6.1) mm.
        long x = Assert.IsType<long>(contact.Diagnostic.Arguments["x"]);
        long y = Assert.IsType<long>(contact.Diagnostic.Arguments["y"]);
        Assert.InRange(x, Um(4000), Um(5000));
        Assert.InRange(y, Um(5900), Um(6100));

        // R-lvs9-3d: named for flattening, the contact is not a finding — and the flatten IS one.
        var flattened = LvsRun.Run(board, new LvsRunOptions
        {
            FlattenCells = new HashSet<string>(["Contact-Mod"], StringComparer.OrdinalIgnoreCase),
        });

        Assert.DoesNotContain(flattened.Findings, f => f.Id == "lvs.hierarchy.undeclared-contact");
        var info = Assert.Single(flattened.Findings, f => f.Id == "lvs.hierarchy.flattened");
        Assert.Equal(DiagnosticSeverity.Info, info.Severity);
        Assert.Equal("Contact-Mod", info.Diagnostic.Arguments["cellName"]);
    }

    // ══ 6 — the two asymmetric states ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 6.</b> A layout module the drawing has no cell instance for is flattened and
    /// reported at info (R-lvs9-6b); a schematic cell instance the artwork does not have is an
    /// ordinary unmatched device, not a hierarchy finding (R-lvs9-6c).
    /// </summary>
    /// <remarks>
    /// <b>Both are legitimate and common states</b>, and refusing either would make hierarchy
    /// useless on a real board. What must not happen is silence about the first: a module read flat
    /// is a module whose inside was never compared.
    /// </remarks>
    [Fact]
    public void ALayoutOnlyModuleFlattensAndASchematicOnlyInstanceIsAnUnmatchedDevice()
    {
        var result = LvsRun.Run(Board("Asymmetric", 0, mirror: false, contact: false, layoutOnlyModule: true));

        var info = Assert.Single(result.Findings, f => f.Id == "lvs.hierarchy.flattened");
        Assert.Equal("Asymmetric-Orphan", info.Diagnostic.Arguments["cellName"]);
        Assert.Contains("no cell instance", info.Render(), StringComparison.Ordinal);

        // R-lvs9-6c: a part that is drawn and not built is an ordinary DEVICE finding. A hierarchy
        // id here would say the artwork is missing a CELL, which is a different repair entirely.
        var unmatched = result.Findings
            .Where(f => f.Id == "lvs.device.unmatched-schematic").ToArray();
        Assert.NotEmpty(unmatched);

        var named = unmatched.SelectMany(f => f.Objects).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(result.Findings, f =>
            f.Id.StartsWith("lvs.hierarchy.", StringComparison.Ordinal) && f.Objects.Any(named.Contains));
    }

    // ══ 7 — the counters, on a generated scale fixture ══════════════════════════════════════════

    /// <summary>
    /// <b>Gate 7.</b> One point-in-piece query per pin, and one layer union per extraction
    /// (R-lvs9-5a, R-lvs9-5a2) — on a fixture built in the test so it can be scaled without adding
    /// megabytes of artwork to the repository.
    /// </summary>
    /// <remarks>
    /// <b>The arithmetic is the fixture's, stated here.</b> Twenty placements of a two-pin module
    /// is forty pads at board level; the module's own extraction adds its resistor's two pads and
    /// its own two boundary pins. A reading that probed per SHAPE rather than per pin would not
    /// come out at 44, and neither would one that re-extracted the cell per placement.
    /// </remarks>
    [Fact]
    public void QueriesPerPinIsOneAndLayerUnionsEqualDistinctCells()
    {
        var result = LvsRun.Run(Scale("Counted", 20));

        Assert.Equal(20 * 2 + 4, result.Hierarchy.PinQueries);
        Assert.Equal(result.Hierarchy.Extractions, result.Hierarchy.LayerUnions);
        Assert.Equal(20, result.Hierarchy.ContactChecks);
    }

    // ══ 8 — the ceiling ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 8.</b> Above the device ceiling the run refuses and says the number (R-lvs9-5d) —
    /// <c>DrcEngine</c>'s bargain, so a pathological design costs a message rather than a hang.
    /// </summary>
    [Fact]
    public void OverTheDeviceCeilingIsARefusalNamingTheNumber()
    {
        var result = LvsRun.Run(Scale("Ceiling", 6), new LvsRunOptions { MaxDevices = 3 });

        var refusal = Assert.Single(result.Findings, f => f.Id == "lvs.layout.over-device-ceiling");
        Assert.Equal(DiagnosticSeverity.Error, refusal.Severity);
        Assert.Equal(6, refusal.Diagnostic.Arguments["devices"]);
        Assert.Equal(3L, refusal.Diagnostic.Arguments["ceiling"]);
        Assert.Empty(result.Layout.Devices);
    }

    // ══ 9 — the cache key ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 9.</b> The key changes when the artwork's file changes, and when the technology
    /// changes (R-lvs9-1b) — the parameter half is gate 3.
    /// </summary>
    /// <remarks>
    /// <b>A cache key that differs from the one the rest of the application uses is a cache that is
    /// stale in exactly the cases the others are not</b>, which is why the inputs are
    /// <c>GeneratedCellStore.BuildCellName</c>'s and <c>CellLayoutResolver</c>'s put together. The
    /// technology half is the one that is easy to leave out: the same <c>.clay</c> read against a
    /// different stackup is a different partition, and circuitRF ships the editor that edits one.
    /// </remarks>
    [Fact]
    public void TouchingTheClayOrTheTechnologyInvalidatesTheKey()
    {
        string cell = Module("Keyed", contact: false);
        var tech = TwoLayerTech();

        string before = LvsCellKey.Of(cell, null, tech);

        string clay = Path.Combine(CellFolder.SubFolderPath(cell, ViewType.Layout), "Keyed.clay");
        File.SetLastWriteTimeUtc(clay, File.GetLastWriteTimeUtc(clay).AddMinutes(1));
        Assert.NotEqual(before, LvsCellKey.Of(cell, null, tech));

        var other = TwoLayerTech();
        other.Layers[0].Name = "TopCopper";
        Assert.NotEqual(before, LvsCellKey.Of(cell, null, other));
    }

    // ══ 10 — it writes nothing ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 10.</b> A hierarchical run writes nothing at all (R-lvs9-1c, R-aut4-6) — so it runs
    /// on a read-only tree and on a workspace another process has open.
    /// </summary>
    /// <remarks>
    /// <b>Asserted as the tree's own before-and-after</b> rather than by revoking write permission,
    /// which is what the claim actually is and which says the same thing on every platform. The
    /// cache is per run and in memory; a cache FILE would be the one part of this feature that
    /// could not make this promise.
    /// </remarks>
    [Fact]
    public void AHierarchicalRunWritesNothing()
    {
        string board = Board("ReadOnly", 0, mirror: false, contact: true);

        var before = Snapshot();
        LvsRun.Run(board);
        Assert.Equal(before, Snapshot());
    }

    // ══ 11 — a cell's own sign-off survives the board's waiver list ═════════════════════════════

    /// <summary>
    /// A finding hoisted out of a sub-cell keeps the waiver that cell's own <c>.clay</c> granted
    /// it, even once the BOARD waives something of its own.
    /// </summary>
    /// <remarks>
    /// <b>The two halves of the claim only bite together.</b> <c>LvsWaivers.Apply</c> clears every
    /// finding no key matches — which is what makes un-waiving in the panel work — and a hoisted
    /// finding's key names the PLACEMENT, so the cell's own key can never match it. Before the
    /// inherited waiver was carried, adding one board-level waiver silently un-waived every
    /// finding signed off inside every cell, and the only symptom was the error count.
    /// </remarks>
    [Fact]
    public void ACellsOwnWaiverSurvivesTheBoardsWaiverList()
    {
        var inCell = LvsMarker.Of(LvsDiagnostics.NetShort("A, B", 2, "net 3"), ["A", "B"], Bbox.Empty);
        var signedOff = inCell with { Waived = true, WaiverReason = "known, and deliberate" };
        var hoisted = LvsHierarchy.Within("U1", signedOff);

        var ownFinding = LvsMarker.Of(
            LvsDiagnostics.NetShort("C, D", 2, "net 7"), ["C", "D"], Bbox.Empty);
        var boardWaiver = new LvsWaiver { Key = LvsWaiverKey.For(ownFinding), Reason = "board level" };

        var applied = LvsWaivers.Apply([hoisted, ownFinding], [boardWaiver]);

        Assert.True(applied[0].Waived);
        Assert.Equal("known, and deliberate", applied[0].WaiverReason);
        Assert.True(applied[1].Waived);

        // And the board can still un-waive its own, which is the behaviour the clearing exists for.
        Assert.False(LvsWaivers.Apply([hoisted, ownFinding], [])[1].Waived);
    }

    // ── Fixture machinery ───────────────────────────────────────────────────────────────────────

    /// <summary>Every file under the fixture root, with its length and last write — what "wrote
    /// nothing" means, stated as data.</summary>
    private IReadOnlyList<string> Snapshot() =>
        [.. Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
             .Select(f => $"{Path.GetRelativePath(_root, f)}|{new FileInfo(f).Length}|"
                        + File.GetLastWriteTimeUtc(f).Ticks)
             .Order(StringComparer.Ordinal)];

    /// <summary>A run's report as the gate compares two of them: id and objects, in order.</summary>
    private static IReadOnlyList<string> Signature(LvsRunResult result) =>
        [.. result.Findings.Select(f => $"{f.Id}|{string.Join(",", f.Objects)}")];

    /// <summary>A layout carrying a generated cell's provenance, which is what R-lvs9-1d keys
    /// on.</summary>
    private static LayoutView Generated(double width)
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        var parameters = new Dictionary<string, PCellValue> { ["W"] = PCellValue.Real(width) };
        view.PCellOrigin = new PCellOrigin("MLIN", parameters);
        return view;
    }

    // ── The parent ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A board placing one module at <paramref name="deg"/>/<paramref name="mirror"/>, with a
    /// parent trace under each of the module's two declared pins — and, optionally, a slab of
    /// parent copper under the module's own undeclared strip.
    /// </summary>
    private string Board(
        string name, double deg, bool mirror, bool contact, bool layoutOnlyModule = false)
    {
        string module = Module(name + "-Mod", contact);
        string orphan = layoutOnlyModule ? Module(name + "-Orphan", contact: false, ports: 0) : "";

        string cellDir = CellFolder.CreateCellFolder(_root, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);

        var view = NewLayout();
        var place = Place(module, layoutDir, Um(5000), Um(5000), deg, mirror, "U1");
        view.Instances.Add(place);

        // One trace under each declared pin, in the module's own frame offset by the placement, and
        // then taken through the SAME transform the placement is — so the arithmetic is the
        // fixture's and the answer is required to be invariant.
        foreach (var (_, px, py) in ModulePins)
            view.Shapes.Add(TracePolygon(
                Um(5000) + px - Um(900), Um(5000) + py - Um(80),
                Um(5000) + px + Um(120), Um(5000) + py + Um(80), deg, mirror));

        // R-lvs9-3: parent copper under the module's undeclared strip, which sits at y ≈ +1000 µm
        // in the module's own frame.
        if (contact)
            view.Shapes.Add(TracePolygon(
                Um(4000), Um(5800), Um(5000), Um(6200), deg, mirror));

        if (layoutOnlyModule)
            view.Instances.Add(Place(orphan, layoutDir, Um(12000), Um(12000), 0, false, null));

        SaveLayout(view, Path.Combine(layoutDir, name + ".clay"));

        // The drawing: two ports, a resistor, and a capacitor to ground that the artwork does not
        // have at all — R-lvs9-6c's schematic-only part, which must read as an ordinary unmatched
        // DEVICE. It is a capacitor rather than a second resistor so the series collapse cannot
        // quietly merge the two and hand the comparison a device count that happens to match.
        var model = new SchematicEditModel();
        model.Components.Add(Resistor("R9", 0, 200));           // pins (0,0) and (0,400)
        model.Components.Add(Port("P1", -100, 0, 1));
        model.Components.Add(Port("P2", -100, 400, 2));
        model.Components.Add(Capacitor("C1", 800, 200));        // pins (800,0) and (800,400)
        model.Components.Add(new EditableComponent
        {
            InstanceName = "GND1", Symbol = SymbolKind.Ground, X = 800, Y = 400,
        });
        model.Wires.Add(Wire((0, 0), (800, 0)));
        WriteSchematic(cellDir, name, model);

        return cellDir;
    }

    /// <summary>
    /// <paramref name="count"/> placements of one module, on bare board with no parent copper —
    /// the generated scale fixture of gate 7, which is built here rather than committed.
    /// </summary>
    private string Scale(string name, int count)
    {
        string module = Module(name + "-Mod", contact: false);

        string cellDir = CellFolder.CreateCellFolder(_root, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);

        var view = NewLayout();
        for (int i = 0; i < count; i++)
            view.Instances.Add(Place(
                module, layoutDir, Um(10000) * (i + 1), Um(10000), 0, false, "U" + (i + 1)));

        SaveLayout(view, Path.Combine(layoutDir, name + ".clay"));
        WriteSchematic(cellDir, name, new SchematicEditModel());
        return cellDir;
    }

    // ── The module ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A cell that is a design of its own: two boundary pins, one resistor between them, and a
    /// schematic beside the layout — which is what makes it a cell the comparison descends into.
    /// </summary>
    /// <param name="contact">Draw a strip of copper the cell declares no pin for — R-lvs9-3's
    /// undeclared contact, which is only a finding when the parent has copper under it.</param>
    /// <param name="ports">Zero declares no ports, which makes the placement not a device: the
    /// drawing has no cell instance for it, and R-lvs9-6b flattens it.</param>
    private string Module(string name, bool contact, int ports = 2)
    {
        string leaf = Footprint(name + "-R", ("A", -Um(300), 0), ("B", Um(300), 0));

        string cellDir = CellFolder.CreateCellFolder(_root, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);

        var view = NewLayout();

        // The boundary, and a pad under each — the pad IS how the pin lands on the parent's copper.
        foreach (var (pin, x, y) in ModulePins)
        {
            view.Pins.Add(new LayoutPin { Name = pin, X = x, Y = y, WidthDbu = Um(250), Layer = Top });
            view.Shapes.Add(Rect(Top, x - Um(125), y - Um(125), x + Um(125), y + Um(125)));
        }

        // The inside: one resistor, and a trace from each boundary pad to one of its pads. NONE of
        // this is in the parent's partition once the cell is read hierarchically (R-lvs9-2d).
        view.Instances.Add(Place(leaf, layoutDir, 0, 0, 0, false, "R1", schematicId: "R1"));
        view.Shapes.Add(Rect(Top, ModulePins[0].X, ModulePins[0].Y - Um(60), -Um(175), Um(60)));
        view.Shapes.Add(Rect(Top, Um(175), -Um(60), ModulePins[1].X, ModulePins[1].Y + Um(60)));

        // R-lvs9-3: metal the cell declares no pin for.
        if (contact) view.Shapes.Add(Rect(Top, -Um(1500), Um(900), Um(1500), Um(1100)));

        SaveLayout(view, Path.Combine(layoutDir, name + ".clay"));

        var model = new SchematicEditModel();
        model.Components.Add(Resistor("R1", 0, 200));
        model.Components.Add(Port("P1", -100, 0, 1));
        model.Components.Add(Port("P2", -100, 400, 2));
        WriteSchematic(cellDir, name, model);

        // The symbol is what joins port to layout pin BY NAME — brief 1's terminal map, derived
        // rather than declared, which is the ordinary state of a hand-drawn cell.
        string[] names = [.. ModulePins.Select(p => p.Pin)];
        SymbolPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), name + ".csym"),
            new Symbol([], [.. names.Select((n, i) => new SymbolPin(0, i * 100, i + 1, n))], names.Length));

        SetPorts(cellDir, ports);
        return cellDir;
    }

    /// <summary>A leaf land pattern: one named pin per entry, a pad under each, and a symbol pin
    /// per name. No schematic — which is what keeps it a leaf.</summary>
    private string Footprint(string name, params (string Pin, long X, long Y)[] pins)
    {
        string cellDir = CellFolder.CreateCellFolder(_root, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        if (File.Exists(Path.Combine(layoutDir, name + ".clay"))) return cellDir;

        SymbolPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), name + ".csym"),
            new Symbol([], [.. pins.Select((p, i) => new SymbolPin(0, i * 100, i + 1, p.Pin))], pins.Length));

        var view = new LayoutView { DbuPerMicron = Dbu };
        foreach (var (pin, x, y) in pins)
        {
            view.Pins.Add(new LayoutPin { Name = pin, X = x, Y = y, WidthDbu = Um(250), Layer = Top });
            var pad = Rect(Top, x - Um(125), y - Um(125), x + Um(125), y + Um(125));
            pad.Pin = pin;      // what a land pattern's own pads carry, and all they may carry
            view.Shapes.Add(pad);
        }
        SaveLayout(view, Path.Combine(layoutDir, name + ".clay"));

        SetPorts(cellDir, pins.Length);
        return cellDir;
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────────

    private LayoutView NewLayout() => new()
    {
        DbuPerMicron = Dbu,
        TechRef = Path.Combine("..", "..", "tech", "Board.ctech"),
    };

    private static void SaveLayout(LayoutView view, string path) => LayoutPersistence.SaveToFile(path, view);

    private static void WriteSchematic(string cellDir, string name, SchematicEditModel model)
    {
        string dir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".csch"), SchematicPersistence.Serialize(model));
    }

    private static void SetPorts(string cellDir, int ports)
    {
        string path = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(path);
        ccell.NumPorts = ports;
        CellPersistence.SaveToFile(path, ccell);
    }

    private static EditableComponent Resistor(string name, double x, double y)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Resistor, X = x, Y = y };
        c.Parameters.Add(new EditableParameter { Name = "R", Expression = "50" });
        return c;
    }

    private static EditableComponent Capacitor(string name, double x, double y)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Capacitor, X = x, Y = y };
        c.Parameters.Add(new EditableParameter { Name = "C", Expression = "1p" });
        return c;
    }

    private static EditableWire Wire(params (double X, double Y)[] points)
    {
        var w = new EditableWire();
        w.Points.AddRange(points);
        return w;
    }

    /// <summary>A cell port. Its own terminal is at (+100, 0) from its body, so the body goes 100
    /// to the LEFT of the pin it names.</summary>
    private static EditableComponent Port(string name, double x, double y, int num)
    {
        var p = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Pin, X = x, Y = y };
        p.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
        return p;
    }

    private static LayoutInstance Place(
        string cellDir, string layoutDir, long x, long y, double deg, bool mirror,
        string? refdes, string? schematicId = null)
    {
        var (px, py) = Xf(x, y, deg, mirror);
        return new LayoutInstance
        {
            CellRef = Path.GetRelativePath(layoutDir, cellDir),
            X = px, Y = py, Mag = 1.0,
            RotationDegrees = deg, MirrorX = mirror,
            RefDes = refdes, SchematicId = schematicId,
        };
    }

    /// <summary>The whole-board transform: mirror X first, then rotate — exactly the order
    /// <c>LayoutInstanceTransform.TransformPoint</c> applies.</summary>
    private static (long X, long Y) Xf(long x, long y, double deg, bool mirror)
    {
        double mx = mirror ? -x : x;
        var (c, s) = LayoutAngle.CosSin(deg);
        return ((long)Math.Round(mx * c - y * s), (long)Math.Round(mx * s + y * c));
    }

    private static RectShape Rect(LayerKey layer, long x1, long y1, long x2, long y2) =>
        new() { Layer = layer, X1 = Math.Min(x1, x2), Y1 = Math.Min(y1, y2),
                X2 = Math.Max(x1, x2), Y2 = Math.Max(y1, y2) };

    /// <summary>An axis-aligned box taken through the board transform — a polygon, because a
    /// rotated rectangle is not a rectangle.</summary>
    private static PolygonShape TracePolygon(
        long x1, long y1, long x2, long y2, double deg, bool mirror)
    {
        var xy = new List<long>();
        foreach (var (x, y) in new[] { (x1, y1), (x2, y1), (x2, y2), (x1, y2) })
        {
            var (px, py) = Xf(x, y, deg, mirror);
            xy.Add(px); xy.Add(py);
        }
        return new PolygonShape { Layer = Top, Xy = [.. xy] };
    }

    private void WriteTechnology()
    {
        string techDir = Path.Combine(_root, "tech");
        Directory.CreateDirectory(techDir);
        TechPersistence.SaveToFile(Path.Combine(techDir, "Board.ctech"), TwoLayerTech());
        WorkspacePersistence.SaveToFile(
            Path.Combine(_root, ".cws"),
            new CwsFile { DefaultTechRef = Path.Combine("tech", "Board.ctech") });
    }

    private static Technology TwoLayerTech()
    {
        var tech = new Technology { Name = "TwoLayer" };
        tech.Layers =
        [
            new LayerDef { Key = Top,  Name = "Top",    ZOrder = 0, Color = new Rgba(200, 80, 40, 255) },
            new LayerDef { Key = Bot,  Name = "Bottom", ZOrder = 1, Color = new Rgba(40, 90, 200, 255) },
            new LayerDef { Key = ViaL, Name = "Via",    ZOrder = 2, Color = new Rgba(120, 120, 120, 255) },
        ];
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = Um(35), SigmaSm = 5.8e7,
                DrawingLayers = [Top],
            },
            new StackupLayer
            {
                Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [ViaL],
                SpanFromLayer = "Top", SpanToLayer = "Bottom",
            },
            new StackupLayer
            {
                Kind = StackupKind.Dielectric, Name = "PP", ThicknessDbu = Um(200), Epsr = 4.3, TanD = 0.02,
            },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "Bottom", ThicknessDbu = Um(35), SigmaSm = 5.8e7,
                DrawingLayers = [Bot], IsGroundReference = true,
            },
        ];
        return tech;
    }
}
