// ================================================================
//  RailRfFieldReport6Tests.cs — the sixth outside designer report (2026-09-24)
//
//  1. A series part hanging off the rail (a 0 Ω link upstream of the source) was refused as "no pad
//     of that reference is on this board" — the pad was on the board, and the designer read the
//     sentence as a question about the part's orientation.
//  2. The resonance note spoke an antenna's language (|S|, a -10 dB match, radiation) on a rail.
//  3. The |Z| plot showed on the DC tab as empty axes.
//  4. "400n H" was refused where "400nH" was read, and "1.2u" — the series editor's own suggested
//     spelling — was refused too.
//  5. A model file picked in the part library editor was stored and not shown until a reopen.
//  6. Update Layout from Schematic on a schematic saved at a workspace's root wrote its layout
//     OUTSIDE the workspace, and the reverse command made a stray schematic/ folder.
// ================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Archive;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.RailRf;
using CircuitRF.Ui.Views.Dialogs;
using CircuitRF.Ui.WBond;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class RailRfFieldReport6Tests(ITestOutputHelper output)
{
    private const int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(3, 0);

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * DbuPerMicron);
    private static long Um(double v) => (long)Math.Round(v * DbuPerMicron);

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }

    // ── 1. A series part that hangs off the rail ───────────────────────────────────────────────

    private static Technology TwoLayerBoard()
    {
        var tech = new Technology { Name = "hang-off board" };
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Top],
            },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "CORE", ThicknessDbu = Mm(0.4), Epsr = 4.3 },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT",
                ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Bot], IsGroundReference = true,
            },
        ];
        return tech;
    }

    private static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2) =>
        new() { Layer = layer, X1 = Mm(x1), Y1 = Mm(y1), X2 = Mm(x2), Y2 = Mm(y2) };

    private static PlacedPin Pad(string refdes, string pin, string net, double xMm) =>
        new PlacedPin(refdes, pin, net, Mm(xMm), Mm(0.15), PinSource.BoardNetlist) { Layer = Top };

    /// <summary>
    /// <b>Claim 1.</b> L1's first pad is on the rail's trace and its second on a land of its own that
    /// nothing else joins — the shape of a link between a switcher's inductor and the node the source
    /// is anchored on. The refusal names the part as hanging off the rail and says what answers it; it
    /// does not say the pad is missing from the board.
    /// </summary>
    [Fact]
    public void ASeriesPartWithOneEndOffTheRail_IsSaidToHangOffIt_NotToBeMissing()
    {
        var rail = new RailSpec { Name = "vsmps", ReferenceLayer = Bot };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" }, OpenCircuitVoltageV = 1.4,
        });
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" }, DcCurrentA = 0.03 });
        rail.Parts.Add(new RailPart
        {
            Refdes = "L1", Connection = RailPartConnection.Series,
            TerminalA = new RailPortAnchor { Refdes = "L1", Pin = "1" },
            TerminalB = new RailPortAnchor { Refdes = "L1", Pin = "2" },
        });
        var doc = new RailDocument { Name = "hang-off" };
        doc.Rails.Add(rail);

        var run = RailDcRun.Run(new RailDcRequest
        {
            Document = doc,
            Technology = TwoLayerBoard(),
            DbuPerMicron = DbuPerMicron,
            Shapes = [Rect(Top, 0, 0, 10, 0.3), Rect(Top, 12, 0, 13, 0.3), Rect(Bot, -1, -3, 15, 3)],
            Pads =
            [
                Pad("BT1", "1", "vsmps", 0.5),
                Pad("U1", "VDD", "vsmps", 9.5),
                Pad("L1", "1", "vsmps", 5.0),
                Pad("L1", "2", "n9", 12.5),
            ],
        });

        output.WriteLine(run.Refusal ?? "solved");
        Assert.NotNull(run.Refusal);
        Assert.Contains("L1 hangs off the rail", run.Refusal, StringComparison.Ordinal);
        Assert.Contains("L1.1 is on the rail's copper", run.Refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("no pad of that reference", run.Refusal, StringComparison.Ordinal);
    }

    // ── 2. The resonance note is a rail's ──────────────────────────────────────────────────────

    /// <summary>
    /// <b>Claim 2.</b> The engine's numbers, in a rail's terms: where |Z| dips and peaks and by how
    /// much, in MHz — and none of the antenna vocabulary (|S|, a matched bandwidth, radiation).
    /// </summary>
    [Fact]
    public void TheResonanceNote_SpeaksOfZ_NotOfAMatch()
    {
        PlanarResonance R(double f, PlanarResonanceKind kind, double ohm) =>
            new(f, 1e3, kind, 39.9, ohm, 0, 2e5, -0.0, double.NaN, double.NaN, double.NaN, "no match", 3);

        var outcome = new PlanarResonanceOutcome(
            [R(8.946e6, PlanarResonanceKind.Series, 1.544e-4), R(10.87e6, PlanarResonanceKind.Parallel, 0.01362)],
            new double[24], 0, CapBound: true, Ran: true, Note: "Resonance search: … the MATCH that is not there.");

        string note = PdnAdaptiveSweep.Describe(outcome, PlanarResonanceSettings.Default, 1e4, 2e8, ports: 1);
        output.WriteLine(note);

        Assert.Contains("8.946 MHz, a series resonance — |Z| dips to 154.4 µΩ", note, StringComparison.Ordinal);
        Assert.Contains("10.87 MHz, an anti-resonance — |Z| peaks at 13.62 mΩ", note, StringComparison.Ordinal);
        Assert.Contains("cap of 24", note, StringComparison.Ordinal);
        foreach (string antenna in new[] { "|S|", "MATCH", "match", "radiation", "GHz" })
            Assert.DoesNotContain(antenna, note, StringComparison.Ordinal);
    }

    // ── 3. The |Z| plot is the frequency tab's ─────────────────────────────────────────────────

    /// <summary>
    /// <b>Claim 3.</b> The plot and the marker boxes that point into it follow the tab, and the window
    /// binds both — a property bound to nothing is the state R-rail18-6 was in, one layer along.
    /// </summary>
    [Fact]
    public void TheImpedancePlot_IsOnTheFrequencyTabOnly()
    {
        using var vm = new RailRfViewModel();

        vm.SelectedResultsTab = RailResultsTab.Dc;
        Assert.False(vm.ShowImpedancePlot);
        vm.SelectedResultsTab = RailResultsTab.Frequency;
        Assert.True(vm.ShowImpedancePlot);

        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Views", "RailRf", "RailRfWindow.axaml"));
        Assert.Matches(@"Name=""ImpedancePlotHost""[^>]*IsVisible=""\{Binding ShowImpedancePlot\}""", xaml);
        Assert.Matches(@"Name=""MarkerInfoBoxLayer""[^>]*IsVisible=""\{Binding ShowImpedancePlot\}""", xaml);
    }

    // ── 4. Units ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Claim 4.</b> A space inside the unit is not part of it, and a bare prefix is the field's own
    /// base unit — exact case, so a frequency's "10m" is still refused rather than read as MHz.
    /// </summary>
    [Theory]
    [InlineData("400n H", RailQuantity.Inductance, 400e-9)]
    [InlineData("400n",   RailQuantity.Inductance, 400e-9)]
    [InlineData("1.2u",   RailQuantity.Inductance, 1.2e-6)]
    [InlineData("10 m ohm", RailQuantity.Resistance, 10e-3)]
    [InlineData("3.3v",   RailQuantity.Voltage, 3.3)]
    [InlineData("10m",    RailQuantity.Frequency, double.NaN)]
    public void AUnitMayCarryASpace_AndABarePrefixIsTheFieldsOwnUnit(string text, RailQuantity q, double expected)
    {
        bool ok = RailValueFormat.TryParse(text, q, out double v);
        if (double.IsNaN(expected)) { Assert.False(ok); return; }
        Assert.True(ok);
        Assert.Equal(expected, v, expected * 1e-12);
    }

    // ── 5. A picked model file shows ───────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Claim 5.</b> Picking a file raises the model cell's own change — the one the keystroke path
    /// deliberately leaves out, because there a TextBox already shows what was typed.
    /// </summary>
    [Fact]
    public void APickedModelFile_IsShownAtOnce()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-fr6-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string crlib = Path.Combine(dir, "parts.crlib");
            var library = new PartLibrary { Name = "parts" };
            library.Rows.Add(new PartLibraryRow { PartNumber = "PN-L12N-0603" });

            var editor = new PartLibraryEditorViewModel(crlib, library);
            var row = editor.Rows.Single();
            var raised = new List<string?>();
            ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            row.SetModelFile(Path.Combine(dir, "models", "L12N-0603_series.s2p"));

            Assert.Equal("models/L12N-0603_series.s2p", row.ModelRef);
            Assert.Contains(nameof(row.ModelRef), raised);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    // ── 6. A loose schematic's layout is the one beside it ─────────────────────────────────────

    /// <summary>
    /// <b>Claim 6a.</b> In a cell folder the other view is in its own sub-folder; LOOSE — here at a
    /// workspace's root — it is beside the document, never in a folder made next to its parent.
    /// </summary>
    [Fact]
    public void TheOtherView_OfALooseDocument_IsBesideIt()
    {
        string ws = Path.Combine(Path.GetTempPath(), "ws");

        var loose = CellFolder.SiblingView(Path.Combine(ws, "PDN1.csch"), ViewType.Schematic, ViewType.Layout);
        Assert.Equal(Path.Combine(ws, "PDN1.clay"), loose.TargetPath);
        Assert.Null(loose.CellDir);

        var back = CellFolder.SiblingView(Path.Combine(ws, "PDN1.clay"), ViewType.Layout, ViewType.Schematic);
        Assert.Equal(Path.Combine(ws, "PDN1.csch"), back.TargetPath);

        string cell = Path.Combine(ws, "lib", "amp");
        var inCell = CellFolder.SiblingView(Path.Combine(cell, "schematic", "amp.csch"), ViewType.Schematic, ViewType.Layout);
        Assert.Equal(Path.Combine(cell, "layout", "amp.clay"), inCell.TargetPath);
        Assert.Equal(cell, inCell.CellDir);
    }

    /// <summary>
    /// <b>Claim 6b.</b> Both commands resolve the other view through that one rule — the grandparent
    /// derivation is gone from both — and a loose layout's wires are seeded beside it.
    /// </summary>
    [Fact]
    public void BothSyncCommands_UseTheSiblingRule_AndALooseLayoutsWiresAreBesideIt()
    {
        string src = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Ui", "ViewModels", "WorkspaceViewModel.SchematicToLayout.cs"));
        Assert.Equal(2, Regex.Matches(src, @"CellFolder\.SiblingView\(").Count);
        Assert.DoesNotMatch(@"cellDir\s*=\s*Path\.GetDirectoryName\((schematicDir|layoutDir)\)", src);

        string ws = Path.Combine(Path.GetTempPath(), "crf-fr6-ws-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(ws);
        try
        {
            var model = new SchematicEditModel();
            model.Components.Add(WBondPlacement.BuildCarrying(null, "W1"));

            var seeded = WBondCellSeeding.Seed(model, ws, "PDN1", looseDir: ws);

            Assert.Equal(Path.Combine(ws, "PDN1.wBond"), seeded.Path);
            Assert.Empty(Directory.GetDirectories(ws));
        }
        finally { try { Directory.Delete(ws, recursive: true); } catch { /* best effort */ } }
    }

    // ══ The owner's decisions on the same report (2026-09-24) ══════════════════════════════════

    // ── 7. A shared part library: referenced by default, archived with its models ──────────────

    /// <summary>
    /// <b>Claim 7a.</b> Referencing is the default answer whether or not the design has a library
    /// already — a team keeping one <c>.crlib</c> for every project is the expected use — and the
    /// question says what answers the dependency: the archive offers it.
    /// </summary>
    [Fact]
    public void UseExistingLibrary_DefaultsToReferencingIt()
    {
        Assert.Equal(RailLibraryUse.Reference, RailUseLibraryDialog.Choices(null).First);
        Assert.Equal(RailLibraryUse.Reference, RailUseLibraryDialog.Choices("board.crlib").First);
        Assert.Equal(RailLibraryUse.Merge, RailUseLibraryDialog.Choices("board.crlib").Second);

        string q = RailUseLibraryDialog.Question("/team/parts.crlib", null, pickedIsOutside: true);
        Assert.Contains("SHARED", q, StringComparison.Ordinal);
        Assert.Contains("Archive Workspace offers to include it, with its model files", q, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Claim 7b.</b> A library the design references from OUTSIDE the workspace is offered by
    /// Archive Workspace as one row, ticked by default, carrying the model files it names relative to
    /// itself — the library alone would arrive with every Touchstone broken.
    /// </summary>
    [Fact]
    public void AReferencedSharedLibrary_IsArchivedWithItsModelFiles_TickedByDefault()
    {
        string root = Path.Combine(Path.GetTempPath(), "crf-fr6-arch-" + Guid.NewGuid().ToString("N")[..8]);
        string ws = Path.Combine(root, "board"), shared = Path.Combine(root, "team");
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(Path.Combine(shared, "models"));
        try
        {
            string model = Path.Combine(shared, "models", "L12N-0603.s2p");
            File.WriteAllText(model, "# Hz S RI R 50\n1e6 0 0 1 0 1 0 0 0\n");

            var library = new PartLibrary { Name = "team" };
            library.Rows.Add(new PartLibraryRow
            {
                PartNumber = "PN-L12N", DielectricClass = PartLibraryRow.OtherClass,
                ModelRef = "models/L12N-0603.s2p",
            });
            PartLibraryIo.SaveToFile(Path.Combine(shared, "team.crlib"), library, validate: false);

            var doc = new RailDocument { Name = "board", PartLibraryRef = "../team/team.crlib" };
            RailDocumentIo.SaveToFile(Path.Combine(ws, "board.crail"), doc);

            var plan = WorkspaceArchiveScanner.Scan(ws);
            var row = Assert.Single(plan.Options, o => o.Kind == ArchiveOptionKind.ExternalFile);

            Assert.True(row.Selected);
            Assert.StartsWith("external/libraries/", row.ArchivePath, StringComparison.Ordinal);
            Assert.Contains(row.Members, m => m.RelativePath == "team.crlib");
            Assert.Contains(row.Members, m => m.RelativePath == "models/L12N-0603.s2p");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best effort */ } }
    }

    // ── 8. Save to library ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Claim 8.</b> A series row's file becomes its part number's model file, stored relative to
    /// the library, as ONE undoable edit — and a part number the library lacks gets a row classed
    /// Other, the class a series row takes its file from.
    /// </summary>
    [Fact]
    public void SaveToLibrary_MakesTheFileThePartsModel_AsOneUndoableEdit()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-fr6-lib-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var editor = new PartLibraryEditorViewModel(Path.Combine(dir, "parts.crlib"), new PartLibrary { Name = "parts" });

            var row = editor.SetPartModel("PN-L12N", Path.Combine(dir, "external", "L12N-0603_series.s2p"));

            Assert.Equal("external/L12N-0603_series.s2p", row.ModelRef);
            Assert.False(row.IsCapacitor);
            Assert.Single(editor.Rows);
            Assert.True(editor.IsDirty);

            editor.UndoCommand.Execute(null);
            Assert.Empty(editor.Rows);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    // ── 9. Top or bottom ───────────────────────────────────────────────────────────────────────

    private static readonly LayerKey Gnd = new(2, 0);

    /// <summary>TOP, then the reference plane, then BOT — the rail runs on BOT only.</summary>
    private static Technology ThreeLayerBoard()
    {
        var tech = TwoLayerBoard();
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Top],
            },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "PP", ThicknessDbu = Mm(0.2), Epsr = 4.3 },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "GND",
                ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Gnd], IsGroundReference = true,
            },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "CORE", ThicknessDbu = Mm(0.4), Epsr = 4.3 },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT",
                ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Bot],
            },
        ];
        return tech;
    }

    /// <summary>
    /// <b>Claim 9a.</b> A series resistor soldered to the bottom, whose footprint draws its pins on
    /// the TOP: read where the footprint says, the rail does not run through it and is refused;
    /// stated Bottom, its pads are on the bottom copper and every amp of the load crosses its DCR.
    /// </summary>
    [Fact]
    public void ABottomSidePart_IsReadOnTheBottomCopper_OnceItsRowSaysSo()
    {
        const double Dcr = 0.35, Load = 0.35;

        RailDcRunResult Run(RailBoardSide? side)
        {
            var rail = new RailSpec { Name = "VDD", ReferenceLayer = Gnd };
            rail.Sources.Add(new RailSource { Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" }, OpenCircuitVoltageV = 3.3 });
            rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" }, DcCurrentA = Load });
            rail.Parts.Add(new RailPart
            {
                Refdes = "R1", Connection = RailPartConnection.Series, DcResistanceOhms = Dcr, BoardSide = side,
                TerminalA = new RailPortAnchor { Refdes = "R1", Pin = "1" },
                TerminalB = new RailPortAnchor { Refdes = "R1", Pin = "2" },
            });
            var doc = new RailDocument { Name = "bottom" };
            doc.Rails.Add(rail);

            PlacedPin P(string refdes, string pin, double x, LayerKey land) =>
                new PlacedPin(refdes, pin, "VDD", Mm(x), Mm(0.15), PinSource.BoardNetlist) { Layer = land };
            IReadOnlyList<PlacedPin> pads =
            [
                P("BT1", "1", 0.5, Bot), P("U1", "VDD", 13.5, Bot),
                P("R1", "1", 5.5, Top),  P("R1", "2", 8.5, Top),     // the footprint's own land
            ];
            var tech = ThreeLayerBoard();
            var (sided, points) = RailPartSides.Apply(pads, [], doc, tech);

            return RailDcRun.Run(new RailDcRequest
            {
                Document = doc, Technology = tech, DbuPerMicron = DbuPerMicron,
                Shapes = [Rect(Bot, 0, 0, 6, 0.3), Rect(Bot, 8, 0, 14, 0.3), Rect(Gnd, -1, -3, 15, 3)],
                Pads = sided, NetPoints = points,
            });
        }

        Assert.NotNull(Run(side: null).Refusal);

        var run = Run(RailBoardSide.Bottom);
        output.WriteLine(run.Refusal ?? "solved");
        Assert.Null(run.Refusal);
        var r1 = Assert.Single(run.Rails[0].Breakdown, r => r.Label.Contains("R1", StringComparison.Ordinal));
        Assert.Equal(Dcr * Load, r1.DropV, 1e-6);
    }

    /// <summary>
    /// <b>Claim 9b.</b> A side is a fact about the part on the board: set on one rail's row, it is set
    /// on every rail naming that part, so two rails cannot put one capacitor on two sides.
    /// </summary>
    [Fact]
    public void APartsSide_IsSetOnEveryRailThatNamesIt()
    {
        var doc = new RailDocument { Name = "two rails" };
        foreach (string name in new[] { "A", "B" })
        {
            var rail = new RailSpec { Name = name };
            rail.Parts.Add(new RailPart { Refdes = "C5" });
            doc.Rails.Add(rail);
        }
        using var vm = new RailRfViewModel(doc, null);

        Assert.Equal(2, vm.SetPartsBoardSide(["C5"], RailBoardSide.Bottom));
        Assert.All(doc.Rails, r => Assert.Equal(RailBoardSide.Bottom, r.Parts[0].BoardSide));
        Assert.Empty(RailPartSides.Disagreements(doc));
        Assert.False(vm.ShowPartSides);     // no board, so no bottom to offer
    }

    // ── 10. The designer's board, opened here ──────────────────────────────────────────────────

    /// <summary>
    /// <b>Claim 10.</b> A layout written on Windows names its generated cells with BACKSLASHES. When
    /// they are regenerated here under a new name, every instance is repointed — and keeps its
    /// author's separators. Path.GetFileName on macOS/Linux does not split on '\\', so none was
    /// repointed and the designer's board opened with every footprint, and so every pad, missing.
    /// </summary>
    [Fact]
    public void AWindowsWrittenCellRef_IsRepointedWhenItsCellIsRegenerated()
    {
        string root = Path.Combine(Path.GetTempPath(), "crf-fr6-regen-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            const string stale = "smt-0402@N_000000000000";
            var view = new LayoutView { DbuPerMicron = DbuPerMicron };
            view.PCellSnapshots[stale] = new PCellSnapshot(
                "smt:0402@N", new Dictionary<string, CircuitRF.Design.Layout.PCells.PCellValue>(), null, null, null);
            view.Instances.Add(new LayoutInstance { CellRef = @"..\..\..\.generated-cells\" + stale, Mag = 1 });

            int moved = GeneratedCellsLifecycle.Regenerate(root, view, _ => null);

            Assert.Equal(1, moved);
            string cellRef = view.Instances[0].CellRef!;
            Assert.StartsWith(@"..\..\..\.generated-cells\smt-0402@N_", cellRef, StringComparison.Ordinal);
            Assert.DoesNotContain(stale, cellRef, StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(root, ".generated-cells", cellRef[(cellRef.LastIndexOf('\\') + 1)..])));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best effort */ } }
    }

    /// <summary>
    /// <b>Claim 11.</b> The side combo fits its column: the column leaves room for "bottom" beside the
    /// theme's 32 px chevron, and the theme's 64 px floor is lowered on the combo itself — a floor
    /// wider than its cell is drawn centred with both sides outside it.
    /// </summary>
    [Fact]
    public void TheSideCombo_FitsItsColumn()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Views", "RailRf", "RailRfWindow.axaml"));
        Assert.Equal(2, Regex.Matches(xaml, @"ColumnDefinitions=""20,52,130,84,100,62,64,96,110,170,84,\*""").Count);
        Assert.Matches(@"Name=""PartSideBox""[\s\S]{0,1500}?<x:Double x:Key=""ComboBoxThemeMinWidth"">0</x:Double>", xaml);
    }

    // ── 12. Edit Model Source… ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Claim 12.</b> Selecting a series row opens nothing — an editor that followed the selection
    /// pushed the parts table up and down as a user clicked through it. The editor opens only when
    /// asked, and only for a series row; the window no longer carries the in-pane one, and the row's
    /// model-source cell takes the double-click that asks.
    /// </summary>
    [Fact]
    public void TheSeriesEditor_OpensWhenAskedFor_NotOnSelection()
    {
        var rail = new RailSpec { Name = "VDD" };
        rail.Parts.Add(new RailPart { Refdes = "FB1", Connection = RailPartConnection.Series });
        rail.Parts.Add(new RailPart { Refdes = "C1" });
        var doc = new RailDocument { Name = "series" };
        doc.Rails.Add(rail);
        using var vm = new RailRfViewModel(doc, null);

        vm.SelectedPart = vm.Parts.Single(p => p.Refdes == "FB1");
        Assert.Null(vm.SeriesEditor);

        Assert.Null(vm.BeginSeriesEdit("C1"));
        var editor = vm.BeginSeriesEdit("FB1");
        Assert.NotNull(editor);
        Assert.Same(editor, vm.SeriesEditor);
        vm.EndSeriesEdit();
        Assert.Null(vm.SeriesEditor);

        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Views", "RailRf", "RailRfWindow.axaml"));
        Assert.DoesNotContain("HasSeriesEditor", xaml, StringComparison.Ordinal);
        Assert.Matches(@"Text=""\{Binding ModelSourceText\}""[\s\S]{0,200}?DoubleTapped=""OnPartModelSourceDoubleTapped""", xaml);
    }
}
