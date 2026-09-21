// ================================================================
//  PartLibrarySeedingTests.cs — brief-authored-board-4-part-library-seeding.md §5
//
//  Seven claims, one test each. The library already knows which rows are missing; this is the
//  command that adds them, and the gates are about what a seeded row does NOT say.
//
//  The second one is the one that would be got wrong, and the brief says so: the obvious
//  implementation makes the coverage numbers LOOK better, because `PartLibrary.Coverage` counts a
//  part as known the moment a row for it exists.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class PartLibrarySeedingTests : IDisposable
{
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "crf-seed-" + Guid.NewGuid().ToString("N")[..8]);

    public PartLibrarySeedingTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ── the fixture: thirteen part numbers, four of them in the library ───────────────────────

    /// <summary>The design's own order, with two repeats — collapsing them is R-ab4-1a.</summary>
    private static readonly string[] Referenced =
    [
        "PN-01", "PN-02", "PN-03", "PN-04", "PN-05", "PN-02", "PN-06", "PN-07",
        "PN-08", "PN-09", "PN-05", "PN-10", "PN-11", "PN-12", "PN-13",
    ];

    /// <summary>The four the library already has — deliberately not the first four, so an
    /// implementation that seeded "everything after the fourth" would fail.</summary>
    private static readonly string[] Covered = ["PN-02", "PN-05", "PN-09", "PN-13"];

    private static readonly string[] Missing =
        ["PN-01", "PN-03", "PN-04", "PN-06", "PN-07", "PN-08", "PN-10", "PN-11", "PN-12"];

    /// <summary>A <c>.crail</c> naming <paramref name="libraryRef"/>, carrying <see cref="Referenced"/>
    /// on one rail. Returns its path.</summary>
    private string WriteDesign(string fileName = "Sensor board.crail", string? libraryRef = "decoupling.crlib")
    {
        var rail = new RailSpec { Name = "VDD" };
        for (int i = 0; i < Referenced.Length; i++)
            rail.Parts.Add(new RailPart { Refdes = $"C{i + 1}", PartNumber = Referenced[i] });

        var document = new RailDocument { PartLibraryRef = libraryRef };
        document.Rails.Add(rail);

        string path = Path.Combine(_tmp, fileName);
        RailDocumentIo.SaveToFile(path, document);
        return path;
    }

    /// <summary>A <c>.crlib</c> holding <see cref="Covered"/>, each with a real model on it so the
    /// four are distinguishable from the nine that arrive empty.</summary>
    private string WriteLibrary(string fileName = "decoupling.crlib")
    {
        var library = new PartLibrary { Name = "decoupling" };
        foreach (string part in Covered)
            library.Rows.Add(new PartLibraryRow
            {
                PartNumber              = part,
                CapacitanceFarads       = 1e-7,
                SelfResonantFrequencyHz = 28e6,
                DielectricClass         = "X7R",
                BiasCurve               = { new PartBiasPoint(0, 1e-7), new PartBiasPoint(3.3, 7.4e-8) },
            });

        string path = Path.Combine(_tmp, fileName);
        PartLibraryIo.SaveToFile(path, library);
        return path;
    }

    /// <summary>The editor over that library, with the coverage context the workspace would hand it.</summary>
    private static PartLibraryEditorViewModel Open(string crlib, string workspaceRoot, BomTable? bom = null)
    {
        var vm = new PartLibraryEditorViewModel(crlib, PartLibraryIo.LoadFromFile(crlib, validate: false));
        var context = PartLibraryCoverageContext.For(workspaceRoot, crlib);
        Assert.NotNull(context);
        vm.SetCoverageContext(context! with { Bom = bom });
        return vm;
    }

    // ── 1. Only the missing ones (R-ab4-1a, R-ab4-1b) ─────────────────────────────────────────

    /// <summary>
    /// <b>§5.1.</b> A library covering four of thirteen seeds NINE — in the design's own document
    /// order, repeats collapsed — and the button says which design and how many before it is pressed.
    /// </summary>
    [Fact]
    public void SeedingALibraryCoveringFourOfThirteen_AddsTheNineMissing_InDocumentOrder()
    {
        WriteDesign();
        var vm = Open(WriteLibrary(), _tmp);

        Assert.Equal(13, vm.Coverage!.Referenced);       // fifteen rows, two repeats collapsed
        Assert.Equal(9,  vm.MissingPartCount);
        Assert.Equal("Add the 9 parts Sensor board.crail asks for", vm.SeedMissingPartsText);

        vm.SeedMissingPartsCommand.Execute(null);

        Assert.Equal(13, vm.Rows.Count);
        Assert.Equal(Missing, vm.Rows.Skip(4).Select(r => r.PartNumber).ToArray());
        Assert.Empty(vm.Coverage!.Unknown);
    }

    // ── 2. A seeded row states the part number and nothing else (R-ab4-2a, R-ab4-2b) ──────────

    /// <summary>
    /// <b>§5.2.</b> Every field but the part number is null, and — the half that matters — the counts
    /// do not improve: the nine move out of <i>unknown</i> and into <i>no bias curve</i> and <i>no
    /// ESR basis</i>, which is what a row that names a part and models nothing honestly is.
    /// </summary>
    /// <remarks>
    /// <b>This is the one the obvious implementation gets wrong.</b> <c>PartLibrary.Coverage</c>
    /// counts a part as KNOWN the moment a row exists for it, so seeding always makes
    /// <c>Known</c> rise — and an implementation that filled a plausible capacitance in would make
    /// every other number rise with it while contributing a wrong value to the answer.
    /// </remarks>
    [Fact]
    public void ASeededRow_IsEmptyButForThePartNumber_AndStillCountsAsIncomplete()
    {
        WriteDesign();
        var vm = Open(WriteLibrary(), _tmp);

        var before = vm.Coverage!;
        Assert.Equal(4, before.Known);
        Assert.Equal(9, before.Unknown.Count);
        Assert.Empty(before.WithoutBiasCurve);          // the four that exist all carry one
        Assert.Empty(before.WithoutEsrBasis);

        vm.SeedMissingPartsCommand.Execute(null);

        var row = vm.Rows[4];
        Assert.Equal("PN-01", row.Model.PartNumber);
        Assert.Null(row.Model.Description);
        Assert.Null(row.Model.Footprint);
        Assert.Null(row.Model.DielectricClass);
        Assert.Null(row.Model.VoltageRatingV);
        Assert.Null(row.Model.CapacitanceFarads);
        Assert.Null(row.Model.SelfResonantFrequencyHz);
        Assert.Null(row.Model.StatedInductanceHenries);
        Assert.Null(row.Model.EsrOhms);
        Assert.Null(row.Model.ModelRef);
        Assert.Empty(row.Model.BiasCurve);

        // The row says so on its face, and the file-level counts say so too.
        Assert.True(row.IsIncomplete);
        Assert.True(row.HasNoEsrBasis);
        Assert.False(row.IsFlagged);                    // it is not a refusal: the library still saves

        var after = vm.Coverage!;
        Assert.Equal(13, after.Known);
        Assert.Empty(after.Unknown);
        Assert.Equal(9, after.WithoutBiasCurve.Count);
        Assert.Equal(9, after.WithoutEsrBasis.Count);
        Assert.Equal(before.WithBiasCurve, after.WithBiasCurve);   // nothing became derateable
        Assert.Contains("9 resolving no ESR at all", vm.CoverageText, StringComparison.Ordinal);
    }

    // ── 3. The dielectric class stays null (R-ab4-2c) ─────────────────────────────────────────

    /// <summary>
    /// <b>§5.3.</b> With no bill of materials the class is null and brief 11's ESR fallback reports
    /// no class — a part marked as having none, rather than one quietly given X7R's dissipation
    /// factor.
    /// </summary>
    [Fact]
    public void WithNoBom_TheDielectricClassIsNull_AndTheEsrFallbackReportsNoClass()
    {
        WriteDesign();
        var vm = Open(WriteLibrary(), _tmp);
        vm.SeedMissingPartsCommand.Execute(null);

        foreach (var row in vm.Rows.Skip(4))
        {
            Assert.Null(row.Model.DielectricClass);
            Assert.Null(RailEsrDefaults.CanonicalClass(row.Model.DielectricClass));
            Assert.Null(row.Model.EsrBasis);            // no file, no stated ESR, no class
        }
    }

    // ── 4. With a BOM, the fields arrive, with their provenance (R-ab4-3a, R-ab4-3b) ──────────

    /// <summary>
    /// <b>§5.4.</b> Where the design was imported with a bill of materials, a seeded row takes the
    /// description, the footprint and the value from it, plus what the description parse recognised —
    /// and the row reads as filled from a BOM, so a wrong parse is correctable rather than mysterious.
    /// </summary>
    /// <remarks>
    /// The last assertion is the one with teeth: <b>a bare value column is left null</b>. A BOM
    /// stating <c>100</c> means 100 nF on one board and 100 pF on another, and
    /// <c>RailValueFormat.TryParse</c> would read it through the ladder's own base unit as ONE
    /// HUNDRED FARADS — the "mark read without its scale" failure, in a column nobody would look at
    /// twice.
    /// </remarks>
    [Fact]
    public void WithABom_TheFieldsArriveWithTheirProvenance_AndABareValueIsLeftNull()
    {
        WriteDesign();
        var bom = Bom(
            ("R1",  "PN-01", "100 nF", "0402", "100nF 0402 X7R 16V"),
            ("R3",  "PN-03", "100",    "0603", "a description stating no class"),
            ("R6",  "PN-06", "600R",   null,   null));
        var vm = Open(WriteLibrary(), _tmp, bom);

        vm.SeedMissingPartsCommand.Execute(null);

        var first = vm.Rows.Single(r => r.PartNumber == "PN-01");
        Assert.Equal("100nF 0402 X7R 16V", first.Model.Description);
        Assert.Equal("0402", first.Model.Footprint);
        Assert.Equal("X7R",  first.Model.DielectricClass);
        // "16V" and not "16 V": BomFile.ParseDescription requires the unit letter ON the number,
        // because a bare token in free text is a quantity, a length or part of a code as often as it
        // is a rating. This fixture spells it the way a description that parses spells it.
        Assert.Equal(16.0,   first.Model.VoltageRatingV);
        Assert.Equal(1e-7,   first.Model.CapacitanceFarads!.Value, 12);
        Assert.Equal("pre-filled from the bill of materials", first.OriginText);

        // A bare number, and a unit from another dimension: both left null rather than guessed.
        var bare = vm.Rows.Single(r => r.PartNumber == "PN-03");
        Assert.Null(bare.Model.CapacitanceFarads);
        Assert.Null(bare.Model.DielectricClass);
        Assert.Equal("0603", bare.Model.Footprint);

        var ferrite = vm.Rows.Single(r => r.PartNumber == "PN-06");
        Assert.Null(ferrite.Model.CapacitanceFarads);
        Assert.Null(ferrite.Model.Footprint);
        Assert.Equal("", ferrite.OriginText);           // the BOM named it and said nothing usable

        // A part the BOM does not name is empty, and says nothing about a BOM (R-ab4-3c).
        var untouched = vm.Rows.Single(r => r.PartNumber == "PN-12");
        Assert.Null(untouched.Model.Description);
        Assert.Equal("", untouched.OriginText);
    }

    // ── 5. One undo removes all nine (R-ab4-4a) ───────────────────────────────────────────────

    /// <summary>
    /// <b>§5.5.</b> The whole batch is ONE undo entry. Nine rows that take nine undos to remove is
    /// the Match Designer's slider defect in a new place.
    /// </summary>
    [Fact]
    public void TheWholeBatch_IsOneUndoEntry()
    {
        WriteDesign();
        var vm = Open(WriteLibrary(), _tmp);

        vm.SeedMissingPartsCommand.Execute(null);
        Assert.Equal(13, vm.Rows.Count);
        Assert.True(vm.IsDirty);

        vm.UndoCommand.Execute(null);

        Assert.Equal(4, vm.Rows.Count);
        Assert.Equal(9, vm.MissingPartCount);           // the button is back, saying nine again
        Assert.False(vm.UndoRedo.CanUndo);
        Assert.False(vm.IsDirty);
    }

    // ── 6. Zero missing disables; no context removes (R-ab4-1c, R-ab4-1d) ─────────────────────

    /// <summary>
    /// <b>§5.6.</b> A library that covers its design leaves the command visible and DISABLED, with
    /// the reason in its tooltip — a control that vanishes on success reads as a control that broke.
    /// A library no design references offers the command at all, which is the ordinary state of a
    /// shared library being edited on its own.
    /// </summary>
    [Fact]
    public void ZeroMissingDisablesRatherThanHides_AndNoCoverageContextRemovesIt()
    {
        WriteDesign();
        var vm = Open(WriteLibrary(), _tmp);
        vm.SeedMissingPartsCommand.Execute(null);

        Assert.True(vm.HasSeedSource);                                  // still offered
        Assert.False(vm.SeedMissingPartsCommand.CanExecute(null));      // and disabled
        Assert.Contains("already in this library", vm.SeedMissingPartsTooltip, StringComparison.Ordinal);
        Assert.Contains("Sensor board.crail", vm.SeedMissingPartsTooltip, StringComparison.Ordinal);

        // A library in a directory no design points into. `For` answers null, and that null is what
        // takes the command off the toolbar.
        string orphan = Path.Combine(_tmp, "shared");
        Directory.CreateDirectory(orphan);
        string path = Path.Combine(orphan, "shared.crlib");
        PartLibraryIo.SaveToFile(path, new PartLibrary { Name = "shared" });

        var alone = new PartLibraryEditorViewModel(path, PartLibraryIo.LoadFromFile(path));
        alone.SetCoverageContext(PartLibraryCoverageContext.For(orphan, path));

        Assert.False(alone.HasSeedSource);
        Assert.False(alone.SeedMissingPartsCommand.CanExecute(null));
    }

    // ── 7. A .crail that names no library (R-ab4-4b) ──────────────────────────────────────────

    /// <summary>
    /// <b>§5.7.</b> A design naming no part library at all gets one created for it, seeded in the
    /// same act, landing where the New Cell dialog's own default would put it — and the
    /// <c>.crail</c> now resolves it.
    /// </summary>
    [Fact]
    public void ACrailNamingNoLibrary_GetsOneCreatedWhereNewCellWouldPutIt_AndResolvesIt()
    {
        string cws = Path.Combine(_tmp, "ws.cws");
        File.WriteAllText(cws, "{}");
        string crail = WriteDesign(libraryRef: null);

        var workspace = new WorkspaceViewModel { CurrentWorkspacePath = cws };
        string? created = workspace.CreatePartLibraryForRailDocument(crail, "decoupling", out string? error);

        Assert.Null(error);
        Assert.Equal(Path.Combine(_tmp, "decoupling.crlib"), created);   // the workspace root

        // The document names it, and the reference RESOLVES — which is the assertion, because the
        // reference is written document-relative and a path that reads back as text is not a path
        // that reads back as a library.
        var reopened = RailDocumentIo.LoadFromFile(crail);
        Assert.Equal("decoupling.crlib", reopened.PartLibraryRef);
        var resolved = RailArtwork.ResolvePartLibrary(reopened, crail, out _, out string? readError);
        Assert.Null(readError);
        Assert.NotNull(resolved);

        // Seeded in the same act: one row per DISTINCT part number, in document order, empty.
        Assert.Equal(13, resolved!.Rows.Count);
        Assert.Equal(Referenced.Distinct().ToArray(), resolved.Rows.Select(r => r.PartNumber).ToArray());
        Assert.All(resolved.Rows, r => Assert.Null(r.CapacitanceFarads));

        // A second attempt refuses by name rather than writing a second file.
        Assert.Null(workspace.CreatePartLibraryForRailDocument(crail, "other", out string? again));
        Assert.Contains("already names a part library", again!, StringComparison.Ordinal);
    }

    // ── plumbing ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A bill of materials, as the import would have produced one — each row's description
    /// run through <c>BomFile.ParseDescription</c>, which is what railRF itself does.</summary>
    private static BomTable Bom(
        params (string Refdes, string PartNumber, string? Value, string? Footprint, string? Description)[] rows)
    {
        var built = new List<BomRow>();
        foreach (var r in rows)
            built.Add(new BomRow(r.Refdes, r.PartNumber, r.Value, r.Footprint, r.Description)
            {
                Parsed = BomFile.ParseDescription(r.Description),
                Line   = built.Count + 2,
            });

        return new BomTable("board.bom.csv", null, ',', built, built.Count, 0, [], []);
    }
}
