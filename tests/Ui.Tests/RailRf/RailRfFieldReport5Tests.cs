// ================================================================
//  RailRfFieldReport5Tests.cs
//
//  A fifth round of outside railRF use, 2026-09-23. One test per CLAIM.
//
//  ── WHAT HE SAID, AND WHAT IS ASSERTED ────────────────────────────────────────────────────────
//
//   1. The GND plane could not be picked as the reference. The combo lists it disabled and sends
//      the user to the technology editor, whose Attach button did the repair — and railRF went on
//      showing the row disabled, because the list was rebuilt only on a new board. Asserted: the
//      list follows an adopted stackup, and the same repair is offered beside the combo.
//
//   2. A ferrite bead was solved as a 1 µF capacitor ("unresolved → 1 µF"). The library editor
//      seeded a bias point at 1 µF on a row with no capacitance, and derating then returned that
//      point. Asserted: no point can be added to such a row, and a curve on one is ignored. The
//      class is a drop-down now (his suggestion) whose `Other` says a row is not a capacitor, so a
//      bead is in neither bias-curve count; and C, f0 and L take a unit (he asked which unit f0 was in).
//
//   3. After deleting the part library, a new one could not be created: the .crail still named the
//      old file and creation refused. Asserted: a dangling reference is replaced, and said to be.
//
//   4. He had every part's C, self-resonance and ESL in a spreadsheet and no way into the library.
//      Asserted: a .csv reads with the units its headers state, a unitless column is not read, a
//      Value/C disagreement stops the C, and a trimmed part number matches one row and is named.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Layout;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class RailRfFieldReport5Tests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-fr5-" + Guid.NewGuid().ToString("N")[..12]);

    public RailRfFieldReport5Tests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ── 1 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AttachingThePlaneInTheStackupMakesItPickable_AndTheRepairIsOfferedBesideTheCombo()
    {
        var doc = new RailDocument { Name = "board" };
        doc.Rails.Add(new RailSpec { Name = "3V3", NetName = "3V3" });
        var vm = new RailRfViewModel(doc, null)
        {
            PostToUi     = a => a(),
            RunOffThread = (work, _) => Task.FromResult(work()),
        };
        vm.Board = new RailBoardInputs { Shapes = [], Technology = PlaneUnattached() };

        var gnd = vm.ReferenceLayerOptions.Single(o => o.Name == "GND");
        Assert.False(gnd.IsSelectable);
        Assert.Equal("Attach 'gnd' to GND", vm.ReferenceFixLabel);

        // What the technology editor's Attach button produces, arriving through the live seam.
        var attached = PlaneUnattached();
        attached.Stackup.Layers.Single(l => l.Name == "GND").DrawingLayers.Add(new LayerKey(3, 0));
        vm.AdoptTechnology(attached);

        var picked = vm.ReferenceLayerOptions.Single(o => o.Name == "gnd");
        Assert.True(picked.IsSelectable);
        Assert.Equal(picked.Key, vm.ReferenceProposal?.Key);   // the marked plane is now proposed
        Assert.False(vm.HasReferenceFix);
    }

    /// <summary>Top, an inner GND marked as the reference with no drawing layer, Bottom — and a
    /// drawing layer `gnd` nothing claims, which is the imported set's shape.</summary>
    private static Technology PlaneUnattached()
    {
        var tech = new Technology { Name = "board" };
        tech.Layers.Add(new LayerDef { Key = new LayerKey(1, 0), Name = "Top Copper" });
        tech.Layers.Add(new LayerDef { Key = new LayerKey(2, 0), Name = "Bottom Copper" });
        tech.Layers.Add(new LayerDef { Key = new LayerKey(3, 0), Name = "gnd" });
        tech.Stackup.Layers.Add(new StackupLayer
            { Kind = StackupKind.Conductor, Name = "Top Copper", DrawingLayers = [new LayerKey(1, 0)] });
        tech.Stackup.Layers.Add(new StackupLayer
            { Kind = StackupKind.Conductor, Name = "GND", IsGroundReference = true });
        tech.Stackup.Layers.Add(new StackupLayer
            { Kind = StackupKind.Conductor, Name = "Bottom Copper", DrawingLayers = [new LayerKey(2, 0)] });
        return tech;
    }

    // ── 2 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AFerriteRow_TakesNoBiasPoint_AndACurveOnItDoesNotMakeItACapacitor()
    {
        var bead = new PartLibraryRow { PartNumber = "FB-220R-0603", EsrOhms = 0.05 };
        var library = new PartLibrary { Name = "l" };
        library.Rows.Add(bead);

        var vm = new PartLibraryEditorViewModel(Path.Combine(_root, "l.crlib"), library);
        vm.SelectedRow = vm.Rows[0];
        Assert.False(vm.AddBiasPointCommand.CanExecute(null));

        // A library written by the editor before this fix carries exactly this row.
        bead.BiasCurve.Add(new PartBiasPoint(0, 1e-6));
        var derated = RailDerating.Apply(bead, 3.3);
        Assert.Null(derated.DeratedFarads);
        Assert.False(derated.UsedFarads > 0);
        Assert.Contains("NOT modelled as a derated capacitor", derated.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowClassedOther_IsInNeitherBiasCurveCount_AndASeededRowStillIs()
    {
        var library = new PartLibrary { Name = "l" };
        library.Rows.Add(new PartLibraryRow
            { PartNumber = "FB-220R-0603", DielectricClass = PartLibraryRow.OtherClass, EsrOhms = 0.05 });
        library.Rows.Add(new PartLibraryRow { PartNumber = "SEEDED" });   // a capacitor nobody filled in

        var coverage = library.Coverage(["FB-220R-0603", "SEEDED"]);

        Assert.Equal(["FB-220R-0603"], coverage.NotCapacitors);
        Assert.Equal(["SEEDED"], coverage.WithoutBiasCurve);
        Assert.Equal(0, coverage.WithBiasCurve);
        Assert.Contains(PartLibraryRow.OtherClass, PartLibraryRowViewModel.ClassOptions);
    }

    /// <summary>f0's unit was unclear — a bare number is not taken as hertz.</summary>
    [Fact]
    public void TheSelfResonanceTakesAUnit_AndABareNumberIsRefused()
    {
        var library = new PartLibrary { Name = "l" };
        library.Rows.Add(new PartLibraryRow { PartNumber = "CAP-100N" });
        var row = new PartLibraryEditorViewModel(Path.Combine(_root, "l.crlib"), library).Rows[0];

        row.SelfResonantFrequencyEntry = "28.89";
        Assert.Null(row.Model.SelfResonantFrequencyHz);

        row.SelfResonantFrequencyEntry = "28.89 MHz";
        Assert.Equal(28.89e6, row.Model.SelfResonantFrequencyHz!.Value, 1e-3);
    }

    // ── 4. Importing his parts table (.csv, owner 2026-09-23) ─────────────────────────────────

    /// <summary>His table's shape: C, the resonance and ESL with units in the header, a second
    /// unitless "L" in henries, and one row whose Value and C columns differ tenfold.</summary>
    private const string Table =
        "Reference,Value,Note,Package,Manufacturer,Part Number,C (pF),resonance (MHz),L,L (nH)\n" +
        "C9,100nF,Decoupling cap,SM/C_0402,Maker,CAP100N0402X7RA88D,100000,28.89,3.03491E-10,0.303491\n" +
        "\"C2,C22\",150nF,Filter,SM/C_0402,Maker,CAP150N0402X5RE19,1500000.00,394,1.08782E-13,0.000109\n" +
        "C19,12pF,load cap,SM/C_0402,Maker,CAP12P0402C0GZ01,12,5140,7.98974E-12,0.00799\n";

    [Fact]
    public void ATable_IsReadWithTheUnitsItsHeadersState_AndAValueThatDisagreesStopsTheC()
    {
        var library = new PartLibrary { Name = "l" };
        var report = PartLibraryTableImport.Apply(library, Table, "parts.csv");

        Assert.Equal(3, report.Added.Count);
        var c9 = library.Part("CAP100N0402X7RA88D")!;
        Assert.Equal(100e-9, c9.CapacitanceFarads!.Value, 1e-15);
        Assert.Equal(28.89e6, c9.SelfResonantFrequencyHz!.Value, 1e-3);
        Assert.Equal(0.303491e-9, c9.StatedInductanceHenries!.Value, 1e-18);   // from "L (nH)"
        Assert.Equal("Decoupling cap", c9.Description);
        Assert.Contains(report.Notes, n => n.Contains("Column \"L\" was not read", StringComparison.Ordinal));

        // 150 nF in Value, 1.5 µF in "C (pF)": neither is taken, and the row is named.
        Assert.Null(library.Part("CAP150N0402X5RE19")!.CapacitanceFarads);
        Assert.Contains(report.Notes, n => n.Contains("CAP150N0402X5RE19's value says 150 nF", StringComparison.Ordinal));
    }

    /// <summary>A supplier tool finds a part only once its trailing code is deleted, so the table
    /// may name a trimmed number. One library row it is the start of is a match, and said to be;
    /// two is not.</summary>
    [Fact]
    public void ATrimmedPartNumber_MatchesTheOneRowItStarts_AndIsNamed()
    {
        var library = new PartLibrary { Name = "l" };
        library.Rows.Add(new PartLibraryRow { PartNumber = "CAP100N0402X7RA88D" });
        library.Rows.Add(new PartLibraryRow { PartNumber = "CAP12P0402C0GZ01A" });
        library.Rows.Add(new PartLibraryRow { PartNumber = "CAP12P0402C0GZ01B" });

        string trimmed = Table.Replace("CAP100N0402X7RA88D", "CAP100N0402X7RA88");
        var report = PartLibraryTableImport.Apply(library, trimmed, "parts.csv");

        Assert.Equal(100e-9, library.Part("CAP100N0402X7RA88D")!.CapacitanceFarads!.Value, 1e-15);
        Assert.Contains(report.Notes, n => n.Contains("taken as the library's CAP100N0402X7RA88D", StringComparison.Ordinal));
        Assert.Null(library.Part("CAP12P0402C0GZ01"));                        // not added either
        Assert.Contains(report.Notes, n => n.Contains("start of 2 library part numbers", StringComparison.Ordinal));
    }

    // ── 3 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ACrailNamingADeletedLibrary_GetsANewOne_AndTheDanglingReferenceIsReplaced()
    {
        string cws = Path.Combine(_root, "ws.cws");
        File.WriteAllText(cws, "{}");

        var doc = new RailDocument { Name = "board", PartLibraryRef = "deleted.crlib" };
        var rail = new RailSpec { Name = "3V3", NetName = "3V3" };
        rail.Parts.Add(new RailPart { Refdes = "C1", PartNumber = "CAP-100N" });
        doc.Rails.Add(rail);
        string crail = Path.Combine(_root, "board.crail");
        RailDocumentIo.SaveToFile(crail, doc);

        var workspace = new WorkspaceViewModel { CurrentWorkspacePath = cws };
        string? created = workspace.CreatePartLibraryForRailDocument(crail, "fresh", out string? note);

        Assert.Equal(Path.Combine(_root, "fresh.crlib"), created);
        Assert.Contains("'deleted.crlib', which no longer exists", note, StringComparison.Ordinal);
        Assert.Equal("fresh.crlib", RailDocumentIo.LoadFromFile(crail).PartLibraryRef);
    }
}
