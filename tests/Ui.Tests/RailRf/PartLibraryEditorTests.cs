// ================================================================
//  PartLibraryEditorTests.cs — brief-railrf-24-part-library-editor.md §5
//
//  The part library is a document, and documents can be opened. Nine claims, one test each —
//  RailSaveTests' own shape: view-model driven, no window, no app host, because everything the
//  brief adds is either framework-free (the editor) or a routing decision on the workspace that
//  a bare WorkspaceViewModel answers headlessly.
//
//  Gate 1 FAILED AT HEAD for the reason the brief gives: `.crlib` classified as NodeKind.OtherFile,
//  so the project tree's double-click reached the default no-op and nothing happened.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class PartLibraryEditorTests : IDisposable
{
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "crf-crlib-" + Guid.NewGuid().ToString("N")[..8]);

    public PartLibraryEditorTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>The workspace the brief names — read-only; every test that writes copies it first.</summary>
    private static string ShippedExample() => Path.Combine(RepoRoot(), "examples", "Power Rail");

    private static string ShippedLibrary() =>
        Path.Combine(ShippedExample(), "parts", "decoupling.crlib");

    /// <summary>The shipped library, copied where a test may edit it.</summary>
    private string CopyShippedLibrary()
    {
        string dest = Path.Combine(_tmp, "decoupling.crlib");
        File.Copy(ShippedLibrary(), dest);
        return dest;
    }

    private static PartLibraryEditorViewModel Open(string path) =>
        new(path, PartLibraryIo.LoadFromFile(path, validate: false));

    // ── 1. It opens ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>§5.1.</b> Double-clicking the shipped library in the project tree opens a Part Library
    /// document — which needs BOTH halves: the tree has to classify the extension as something with
    /// an editor, and the workspace's by-path route has to dispatch it.
    /// </summary>
    /// <remarks>
    /// <b>Failed at HEAD on the first assertion.</b> `.crlib` fell through
    /// <c>WorkspaceScanner.ClassifyFile</c> to <see cref="NodeKind.OtherFile"/>, so
    /// <c>OpenNode</c> reached its default no-op. That is the report this brief came from, exactly:
    /// nothing happened, and nothing said why.
    /// </remarks>
    [Fact]
    public void TheProjectTreeClassifiesIt_AndTheWorkspaceOpensItAsADocument()
    {
        Assert.Equal(NodeKind.PartLibraryFile, WorkspaceScanner.ClassifyFile(ShippedLibrary()));

        var vm = new WorkspaceViewModel();
        Assert.True(vm.OpenDocumentByPath(ShippedLibrary()));

        var doc = Assert.IsType<PartLibraryDocument>(vm.FindOpenDocument(ShippedLibrary()));
        Assert.Equal(4, doc.ViewModel.Rows.Count);
        Assert.False(doc.IsDirty);
    }

    // ── 2. Round trip ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>§5.2.</b> Opened and saved with no edit, the file is BYTE-IDENTICAL — opening a library
    /// does not rewrite it — and one changed field survives a reload.
    /// </summary>
    [Fact]
    public void SavingAnUneditedLibrary_ChangesNoByte_AndAnEditSurvivesAReload()
    {
        string path = CopyShippedLibrary();
        byte[] before = File.ReadAllBytes(path);

        var untouched = Open(path);
        untouched.SaveCommand.Execute(null);
        Assert.Equal(before, File.ReadAllBytes(path));

        var vm = Open(path);
        vm.Rows[0].Description = "100 nF 0402 X7R 16 V (second source)";
        vm.SaveCommand.Execute(null);

        var reloaded = PartLibraryIo.LoadFromFile(path);
        Assert.Equal("100 nF 0402 X7R 16 V (second source)", reloaded.Rows[0].Description);
        Assert.Equal(4, reloaded.Rows.Count);
    }

    // ── 3. One session per path ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>§5.3.</b> Opening the same file twice activates the one document. Two edit sessions over
    /// one file means two undo stacks and two dirty flags, and whichever saves last wins.
    /// </summary>
    [Fact]
    public void OpeningTwice_ActivatesTheSameDocument()
    {
        var vm = new WorkspaceViewModel();
        Assert.True(vm.OpenDocumentByPath(ShippedLibrary()));
        var first = vm.FindOpenDocument(ShippedLibrary());

        Assert.True(vm.OpenDocumentByPath(ShippedLibrary()));
        Assert.Same(first, vm.FindOpenDocument(ShippedLibrary()));
    }

    // ── 4. Dirty and undo ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>§5.4.</b> An edit marks the document dirty, undo backs it out and the mark clears — which
    /// is what routing through the ordinary open path was FOR (R-rail24-1a). A save clears it too.
    /// </summary>
    [Fact]
    public void AnEditIsDirtyAndUndoable_AndTheTabMarkFollowsIt()
    {
        string path = CopyShippedLibrary();
        var vm  = Open(path);
        var doc = new PartLibraryDocument("decoupling.crlib", vm, path);

        Assert.False(doc.IsDirty);

        vm.Rows[1].DielectricClass = "X7R";
        Assert.True(doc.IsDirty);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoCommand.Execute(null);
        Assert.Equal("X5R", vm.Rows[1].DielectricClass);
        Assert.False(doc.IsDirty);

        vm.Rows[1].DielectricClass = "X7R";
        vm.SaveCommand.Execute(null);
        Assert.False(doc.IsDirty);
        Assert.Equal("X7R", PartLibraryIo.LoadFromFile(path).Rows[1].DielectricClass);
    }

    /// <summary>
    /// <b>Owner report, 2026-09-20: an edit did not light the dirty indicators up.</b> It could not:
    /// the cells committed on LostFocus, and clicking the tab strip or the project tree does not
    /// always take focus out of a text box — so the document was dirty in the user's hands and clean
    /// in its own. They commit per KEYSTROKE now, and a run of keystrokes in one field is still ONE
    /// undo entry.
    /// </summary>
    [Fact]
    public void TheFirstKeystrokeMarksTheDocumentDirty_AndTheWholeRunIsOneUndo()
    {
        string path = CopyShippedLibrary();
        var vm  = Open(path);
        var doc = new PartLibraryDocument("decoupling.crlib", vm, path);
        string original = vm.Rows[0].CapacitanceEntry;

        vm.Rows[0].CapacitanceEntry = "2";
        Assert.True(doc.IsDirty);
        Assert.StartsWith("•", doc.Title, StringComparison.Ordinal);

        vm.Rows[0].CapacitanceEntry = "22";
        vm.Rows[0].CapacitanceEntry = "220 nF";
        Assert.Equal(2.2e-7, vm.Rows[0].Model.CapacitanceFarads!.Value, 12);

        vm.UndoCommand.Execute(null);
        Assert.Equal(original, vm.Rows[0].CapacitanceEntry);
        Assert.False(doc.IsDirty);
        Assert.False(vm.UndoRedo.CanUndo);

        // A DIFFERENT field is a different gesture, and a save ends the run — otherwise the next
        // keystroke would extend an entry the stack is now treating as the clean baseline.
        vm.Rows[0].Description = "a";
        vm.SaveCommand.Execute(null);
        Assert.False(doc.IsDirty);
        vm.Rows[0].Description = "ab";
        Assert.True(doc.IsDirty);
    }

    /// <summary>
    /// <b>Owner request, 2026-09-20: Escape puts the row down and the bias curve away.</b> The key
    /// handling is the view's; what it drives is this — clearing the selection empties the curve
    /// editor, and <c>HasSelectedRow</c> is what the panel's visibility is bound to.
    /// </summary>
    [Fact]
    public void ClearingTheSelection_EmptiesTheBiasCurveEditor()
    {
        var vm = Open(ShippedLibrary());
        vm.SelectedRow = vm.Rows[0];
        Assert.True(vm.HasSelectedRow);
        Assert.NotEmpty(vm.BiasPoints);

        vm.SelectedRow = null;
        Assert.False(vm.HasSelectedRow);
        Assert.Empty(vm.BiasPoints);
        Assert.Empty(vm.SelectedBiasCurve);
    }

    // ── 5. The disagreement shows (R-rail24-2a) ───────────────────────────────────────────────

    /// <summary>
    /// <b>§5.5.</b> A row whose stated inductance is further from the derived one than the 5 %
    /// tolerance is flagged; one inside it is not. <b>The derived value is used either way</b> — the
    /// stated one only ever gets compared.
    /// </summary>
    [Fact]
    public void AStatedInductanceOutsideTheTolerance_IsFlagged_AndOneInsideItIsNot()
    {
        var library = new PartLibrary();
        // 100 nF at 28 MHz derives 323.1 pH.
        var inside  = new PartLibraryRow { PartNumber = "A", CapacitanceFarads = 1e-7,
                                           SelfResonantFrequencyHz = 28e6 };
        var outside = new PartLibraryRow { PartNumber = "B", CapacitanceFarads = 1e-7,
                                           SelfResonantFrequencyHz = 28e6 };
        double derived = inside.DerivedInductanceHenries!.Value;
        inside.StatedInductanceHenries  = derived * 1.02;   // 2 %  — inside PartLibrary.InductanceTolerance
        outside.StatedInductanceHenries = derived * 1.40;   // 40 % — a mounting loop, not the part
        library.Rows.Add(inside);
        library.Rows.Add(outside);

        var vm = new PartLibraryEditorViewModel(Path.Combine(_tmp, "l.crlib"), library);

        Assert.False(vm.Rows[0].IsInductanceDisagreement);
        Assert.True(vm.Rows[1].IsInductanceDisagreement);
        Assert.NotNull(vm.Rows[1].DisagreementSentence);
        Assert.Single(vm.Disagreements);
        Assert.Contains("'B'", vm.Disagreements[0]);

        // …and the derived value is the one the row reports as the inductance in play.
        Assert.Equal(derived, library.Rows[1].InductanceHenries!.Value, 15);
    }

    // ── 6. Indicative ESR is marked (R-rail24-2b) ─────────────────────────────────────────────

    /// <summary>
    /// <b>§5.6.</b> A row with no stated ESR and a dielectric class reads as a class default and is
    /// marked indicative; one that states an ESR does not; one with neither resolves to nothing and
    /// says so. That is the difference between a number somebody measured and a number railRF
    /// assumed, and it is the thing a reader most needs to know.
    /// </summary>
    [Fact]
    public void AnEsrFromADielectricClass_ReadsAsIndicative_AndAStatedOneDoesNot()
    {
        var library = new PartLibrary();
        library.Rows.Add(new PartLibraryRow { PartNumber = "STATED", EsrOhms = 0.032,
                                              DielectricClass = "X7R" });
        library.Rows.Add(new PartLibraryRow { PartNumber = "CLASS",  DielectricClass = "X7R" });
        library.Rows.Add(new PartLibraryRow { PartNumber = "NEITHER" });

        var vm = new PartLibraryEditorViewModel(Path.Combine(_tmp, "l.crlib"), library);

        Assert.False(vm.Rows[0].IsIndicative);
        Assert.Equal("stated", vm.Rows[0].EsrBasisText);

        Assert.True(vm.Rows[1].IsIndicative);
        Assert.Contains("indicative", vm.Rows[1].EsrBasisText, StringComparison.Ordinal);

        Assert.True(vm.Rows[2].HasNoEsrBasis);
        Assert.Equal(1, vm.IndicativeCount);
    }

    // ── 7. Coverage (R-rail24-2c) ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>§5.7.</b> Opened from the workspace that references it, the editor's counts are
    /// <see cref="PartLibrary.Coverage"/>'s own — nothing here counts anything.
    /// </summary>
    /// <remarks>
    /// Driven against the SHIPPED example, because the property under test is that the walk finds
    /// the design through a document-relative reference (<c>../parts/decoupling.crlib</c>), and a
    /// fixture written flat here would not have one to resolve.
    /// </remarks>
    [Fact]
    public void CoverageAgainstTheShippedDesign_MatchesPartLibraryCoveragesOwn()
    {
        var context = PartLibraryCoverageContext.For(ShippedExample(), ShippedLibrary());
        Assert.NotNull(context);
        Assert.Equal("Sensor board.crail", context!.Subject);

        var library  = PartLibraryIo.LoadFromFile(ShippedLibrary());
        var expected = library.Coverage(context.PartNumbers);

        var vm = Open(ShippedLibrary());
        vm.SetCoverageContext(context.Subject, context.PartNumbers);

        Assert.NotNull(vm.Coverage);
        Assert.Equal(expected.Referenced,     vm.Coverage!.Referenced);
        Assert.Equal(expected.Known,          vm.Coverage.Known);
        Assert.Equal(expected.WithBiasCurve,  vm.Coverage.WithBiasCurve);
        Assert.Contains($"{expected.Referenced} referenced", vm.CoverageText, StringComparison.Ordinal);

        // The bulk part is the one with no curve, so the two counts are not the same number — a
        // coverage panel where they always agree would prove nothing.
        Assert.NotEqual(expected.Known, expected.WithBiasCurve);
    }

    // ── 8. A refused row saves (R-rail24-3a) ──────────────────────────────────────────────────

    /// <summary>
    /// <b>§5.8.</b> A row with no part number is a refusal — and it SAVES anyway, because an editor
    /// that will not let you save work in progress is an editor people work around. The refusal
    /// travels with the file: a run against it reports the same sentence.
    /// </summary>
    [Fact]
    public void ALibraryWithARefusedRow_StillSaves_AndARunAgainstItReportsTheSameSentence()
    {
        string path = CopyShippedLibrary();
        var vm = Open(path);

        vm.AddRowCommand.Execute(null);          // an empty row: no part number yet
        Assert.True(vm.IsRefused);
        Assert.True(vm.Rows[^1].IsFlagged);
        string stated = vm.Refusal!;

        vm.SaveCommand.Execute(null);
        Assert.True(File.Exists(path));

        // What a RUN says. RailArtwork is the one door every rail run reads a library through, and it
        // reads validated — so the sentence the editor showed is the sentence the run reports.
        var document = new RailDocument { PartLibraryRef = Path.GetFileName(path) };
        string crail = Path.Combine(_tmp, "board.crail");
        RailDocumentIo.SaveToFile(crail, document);

        var loaded = RailArtwork.ResolvePartLibrary(document, crail, out _, out string? error);
        Assert.Null(loaded);
        Assert.Equal(stated, error);

        // …and the editor can still OPEN what it was allowed to write, which is the half that makes
        // the refusal fixable rather than terminal.
        Assert.Equal(5, Open(path).Rows.Count);
    }

    // ── 9. The bias curve edits and plots (R-rail24-1c) ───────────────────────────────────────

    /// <summary>
    /// <b>§5.9.</b> Adding a curve point changes the capacitance the resolver derates to — which is
    /// the whole reason the curve is editable at all.
    /// </summary>
    /// <remarks>
    /// Measured through <see cref="RailDerating.Apply"/>, the function the rail solve itself calls;
    /// asserting the editor's own arithmetic would be asserting that a list has one more element in
    /// it.
    /// </remarks>
    [Fact]
    public void AddingABiasCurvePoint_ChangesTheDeratedCapacitanceTheResolverComputes()
    {
        string path = CopyShippedLibrary();
        var vm = Open(path);

        // The bulk part, which ships with NO curve — so it derates to its marked value.
        var row = vm.Rows.Single(r => r.PartNumber == "CAP-BULK-100U-POLY-10V");
        vm.SelectedRow = row;
        Assert.Empty(vm.SelectedBiasCurve);

        const double railVolts = 3.3;
        var beforeDerating = RailDerating.Apply(row.Model, railVolts);
        Assert.Equal(RailCapacitanceBasis.Marked, beforeDerating.Basis);

        vm.AddBiasPointCommand.Execute(null);                  // point at 0 V, the marked value
        vm.SelectedBiasPointIndex = 0;
        vm.AddBiasPointCommand.Execute(null);                  // a second point to interpolate between
        vm.BiasPoints[1].BiasEntry        = "10 V";
        vm.BiasPoints[1].CapacitanceEntry = "40 µF";

        Assert.Equal(2, vm.SelectedBiasCurve.Count);
        Assert.True(vm.Rows.Single(r => r.PartNumber == "CAP-BULK-100U-POLY-10V").HasBiasCurve);

        var afterDerating = RailDerating.Apply(row.Model, railVolts);
        Assert.Equal(RailCapacitanceBasis.Derated, afterDerating.Basis);
        Assert.True(afterDerating.UsedFarads < beforeDerating.UsedFarads,
            $"derating a 100 µF part at {railVolts} V against the new curve produced "
            + $"{afterDerating.UsedFarads} F, which is not below the marked "
            + $"{beforeDerating.UsedFarads} F.");

        // …and it survives the file, which is what makes the edit worth making.
        vm.SaveCommand.Execute(null);
        Assert.Equal(2, PartLibraryIo.LoadFromFile(path)
                                     .Part("CAP-BULK-100U-POLY-10V")!.BiasCurve.Count);
    }

    // ── 10. The footprint column is a picker (R-rail24-4a) ───────────────────────────

    /// <summary>
    /// The picker offers the case table, and a chosen row stores a token the parts table can read
    /// back — which is the only property of the stored spelling that matters.
    /// </summary>
    /// <remarks>
    /// <b>The catalogue's other sections are deliberately absent.</b> A workspace cell's layout view
    /// and Custom… are ARTWORK references resolved against a schematic's directory;
    /// <c>PartLibraryRow.Footprint</c> is the package a part is BOUGHT in and nothing resolves it to
    /// artwork, so a path stored here would read as unmatched for ever. The assertion below is that
    /// round trip, not the spelling: every offered row must survive
    /// <see cref="FootprintTokens.Match"/> as the case it names.
    /// </remarks>
    [Fact]
    public void EveryOfferedFootprintStoresATokenTheCaseTableReadsBack()
    {
        var offered = PartLibraryRowViewModel.Footprints;

        // The (none) row is first and stores nothing; every other row is one case, in table order.
        Assert.Equal("", offered[0].Token);
        Assert.Equal(SmtCaseTable.Codes.Select(c => "smt:" + c), offered.Skip(1).Select(o => o.Token));

        foreach (var option in offered.Skip(1))
        {
            var m = FootprintTokens.Match(option.Token);
            Assert.Equal(FootprintTokenOutcome.Matched, m.Outcome);

            // No density: an IPC-7351B level is a property of a land pattern, not of a package a
            // part is bought in, and no land pattern is generated from this file.
            Assert.DoesNotContain('@', option.Token);
        }

        // ToString is the TOKEN, because that is what an editable combo box puts in its text box and
        // therefore what lands in the file. The long form is the dropdown's, through the template.
        Assert.Equal(offered[1].Token, offered[1].ToString());
        Assert.Contains("metric", offered[1].Display, StringComparison.Ordinal);
        Assert.Contains("mm",     offered[1].Display, StringComparison.Ordinal);
    }

    /// <summary>
    /// Picking a row writes it, dirties the document and undoes — and picking the row a part already
    /// carries does none of the three.
    /// </summary>
    /// <remarks>
    /// The second half is not a nicety. An editable combo box raises <c>SelectionChanged</c> when its
    /// text first matches an item, which happens while the document is being bound: without the
    /// no-op guard, opening this library — whose every row names a case size — would push an undo
    /// entry per row and the document would open dirty against a file it had not changed a byte of.
    /// </remarks>
    [Fact]
    public void PickingAFootprintIsOneUndoableEdit_AndPickingTheStoredOneIsNoEditAtAll()
    {
        string path = CopyShippedLibrary();
        var vm  = Open(path);
        var row = vm.Rows.Single(r => r.PartNumber == "CAP-0402-100N-X7R-16V");

        // What the file already says — re-picking it is not an edit.
        Assert.Equal("smt:0402", row.Footprint);
        row.SelectFootprint(PartLibraryRowViewModel.Footprints.Single(o => o.Token == "smt:0402"));
        Assert.False(vm.IsDirty);
        Assert.False(vm.UndoRedo.CanUndo);

        row.SelectFootprint(PartLibraryRowViewModel.Footprints.Single(o => o.Token == "smt:1206"));
        Assert.Equal("smt:1206", row.Footprint);
        Assert.True(vm.IsDirty);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoCommand.Execute(null);
        Assert.Equal("smt:0402",
                     vm.Rows.Single(r => r.PartNumber == "CAP-0402-100N-X7R-16V").Footprint);

        // And it survives the file, read back by the same matcher the parts table uses.
        vm.RedoCommand.Execute(null);
        vm.SaveCommand.Execute(null);
        string stored = PartLibraryIo.LoadFromFile(path, validate: false)
                                     .Part("CAP-0402-100N-X7R-16V")!.Footprint ?? "";
        Assert.Equal("1206", FootprintTokens.Match(stored).Case?.Code);
    }

    /// <summary>
    /// A typed token is still accepted and still shown as written — and a bare one that names a real
    /// case in BOTH schemes is the one state the cell flags.
    /// </summary>
    /// <remarks>
    /// R-fp4-3b: an unmatched token is ordinary. A vendor decal name is a legitimate thing for a
    /// maintained table to carry, and turning it into the nearest code would tell the reader what the
    /// matcher believed. An ambiguous one is different: <c>0402</c> read the wrong way is 2.4x out, it
    /// places, it renders, it exports, and the first sign of trouble is a board.
    /// </remarks>
    [Fact]
    public void ATypedTokenSurvivesAsWritten_AndOnlyAnAmbiguousOneIsFlagged()
    {
        var vm = Open(CopyShippedLibrary());
        var row = vm.Rows[0];

        row.Footprint = "SM/C_0402";
        Assert.Equal("SM/C_0402", row.Footprint);
        Assert.False(row.IsFootprintAmbiguous);
        Assert.Contains("0402", row.FootprintReport, StringComparison.Ordinal);

        row.Footprint = "WIDGET-77";
        Assert.Equal("WIDGET-77", row.Footprint);
        Assert.False(row.IsFootprintAmbiguous);
        Assert.Contains("WIDGET-77", row.FootprintReport, StringComparison.Ordinal);

        row.Footprint = "0402";
        Assert.True(row.IsFootprintAmbiguous);
        Assert.Contains("imperial", row.FootprintReport, StringComparison.Ordinal);
        Assert.Contains("metric",   row.FootprintReport, StringComparison.Ordinal);

        // …and the remedy the sentence names is one the picker offers and the matcher reads.
        Assert.Contains("smt:0402", row.FootprintReport, StringComparison.Ordinal);
        row.Footprint = "smt:0402";
        Assert.False(row.IsFootprintAmbiguous);
    }
}
