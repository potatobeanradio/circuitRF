// brief-railrf-22-import.md §5 — the import dialog's own gate.
//
// ── SOURCE SCANS WHERE THE DECISION IS IN THE MARKUP, FRAMEWORK-FREE CALLS EVERYWHERE ELSE ───────
//
// RailWindowTests' shape, for its reason: this project references no Avalonia runtime, so a control
// that exists and a handler that has no silent return are read out of the file that declares them,
// and everything that decides what the dialog MEANS — which door an artwork path takes, what a .pdf
// BOM is refused with, what a bill of materials gave up — is on the framework-free records and is
// called directly.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public class RailImportDialogTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "crf-rail22-" + Guid.NewGuid().ToString("N")[..8]);

    public RailImportDialogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ══ Gate 1 — the dialog opens FIRST, and it offers both (R-rail22-1a) ════════════════════════

    /// <summary>
    /// Import Board raises the dialog before it raises any picker, and the dialog carries an artwork
    /// row with a File button and a Folder button, neither pre-selected.
    /// </summary>
    /// <remarks>
    /// <b>Failed at HEAD before this brief</b>: <c>ImportBoardAsync</c> opened
    /// <c>OpenFilePickerAsync</c> as its first act and the dialog had no artwork row at all, so the
    /// first thing a user did was pick one of twelve files that belong together — and the question
    /// that mattered, whether the enclosing FOLDER was the intent, arrived later from inside the
    /// import.
    /// </remarks>
    [Fact]
    public void Gate1_TheDialogComesFirst_AndOffersAFileButtonAndAFolderButton_NothingPreSelected()
    {
        string import = Read("src/Ui/Views/RailRf/RailRfWindow.Import.cs");
        string xaml   = Read("src/Ui/Views/RailRf/RailImportDialog.axaml");
        string dialog = Read("src/Ui/Views/RailRf/RailImportDialog.axaml.cs");

        // The window opens no file picker of its own any more — the artwork picker moved onto the
        // dialog, which is what makes "the dialog is first" structural rather than an ordering that
        // a later edit could quietly undo.
        Assert.DoesNotContain("OpenFilePickerAsync", Strip(import));

        int dlg    = Strip(import).IndexOf("new RailImportDialog(", StringComparison.Ordinal);
        int folder = Strip(import).IndexOf("OpenFolderPickerAsync", StringComparison.Ordinal);
        Assert.True(dlg >= 0, "Import Board no longer constructs the dialog.");
        Assert.True(folder < 0 || dlg < folder,
            "A folder picker is opened before the import dialog — the dialog is meant to be first.");

        foreach (string name in new[] { "ArtworkBox", "ArtworkFilePick", "ArtworkFolderPick" })
            Assert.Contains($"x:Name=\"{name}\"", xaml);

        Assert.Contains("Content=\"File…\"", xaml);
        Assert.Contains("Content=\"Folder…\"", xaml);

        // NOTHING PRE-SELECTED: the box declares no Text, and nothing writes one at construction.
        string artworkBox = Between(xaml, "x:Name=\"ArtworkBox\"", "/>");
        Assert.DoesNotContain("Text=", artworkBox);
        Assert.DoesNotContain("ArtworkBox.Text =", Strip(dialog).Split("PickArtworkFile")[0]);

        // Both buttons write the same box, so the box is the answer rather than the button.
        Assert.Contains("ArtworkBox.Text = files[0]", dialog);
        Assert.Contains("ArtworkBox.Text = folders[0]", dialog);
    }

    // ══ Gate 2 — a folder named up front is not asked about again (R-rail22-1b) ══════════════════

    [Fact]
    public void Gate2_AFolderNamedUpFront_NeverReachesThePickFolderPrompt()
    {
        string set = Path.Combine(_root, "board-set");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "top.gtl"), Artwork());
        File.WriteAllText(Path.Combine(set, "bottom.gbl"), Artwork(xMm: 2.0));

        int prompts = 0, picks = 0;
        var result = RailArtworkEntry.Import(
            set, Path.Combine(_root, "out-folder"), LayoutUnits.DefaultDbuPerMicron,
            promptForScope: _ => { prompts++; return GerberImportScope.EnclosingFolder; },
            pickFolder:     () => { picks++; return null; });

        Assert.Equal(0, picks);
        Assert.Equal(0, prompts);
        Assert.False(result.Cancelled);
        Assert.NotNull(result.CellDir);
    }

    // ══ Gate 3 — and pickFolder still exists, and is still reached on the path that needs it ═════

    /// <summary>
    /// <b>The later prompt is not removed</b> — it is the funnel File ▸ Import ▸ Gerber goes through
    /// and the import may still need it for a set the classifier resolves differently. Removing it
    /// would be a second import path, which the Import file's own header forbids in its first line.
    /// </summary>
    [Fact]
    public void Gate3_OnTheFileRoute_PickFolderIsStillReachable()
    {
        string set = Path.Combine(_root, "one-of-many");
        Directory.CreateDirectory(set);
        string chosen = Path.Combine(set, "top.gtl");
        File.WriteAllText(chosen, Artwork());
        File.WriteAllText(Path.Combine(set, "bottom.gbl"), Artwork(xMm: 2.0));

        int picks = 0;
        var result = RailArtworkEntry.Import(
            chosen, Path.Combine(_root, "out-file"), LayoutUnits.DefaultDbuPerMicron,
            promptForScope: _ => GerberImportScope.AnotherFolder,
            pickFolder:     () => { picks++; return null; });

        Assert.Equal(1, picks);
        Assert.True(result.Cancelled);   // a dismissed picker creates nothing, as it always did
    }

    // ══ Gate 4 — no silent return from a visible control (R-rail22-2a) ══════════════════════════

    /// <summary>
    /// With no workspace resolvable, Edit Technology says a sentence rather than nothing.
    /// </summary>
    /// <remarks>
    /// The handler's two bare <c>return</c>s are read out of the file, because the sentences
    /// themselves are framework-free and the branch that chooses between them is three lines of a
    /// window. The second branch is the reachable one: railRF is an unowned window that outlives the
    /// workspace behind it.
    /// </remarks>
    [Fact]
    public void Gate4_EditTechnology_HasNoSilentReturn_AndEachRefusalNamesWhatToDo()
    {
        string body = Strip(Read("src/Ui/Views/RailRf/RailRfWindow.axaml.cs"));
        int at = body.IndexOf("private void OnBoardEditTechnology", StringComparison.Ordinal);
        Assert.True(at >= 0, "OnBoardEditTechnology is gone — this gate is about that handler.");
        string handler = body.Substring(at, body.IndexOf("\n    }", at, StringComparison.Ordinal) - at);

        Assert.DoesNotContain("WorkspaceLocator.Any() is not { } workspace) return", handler);
        Assert.Contains("RailTechnologyEdit.NoWorkspaceRefusal", handler);
        Assert.Contains("RailTechnologyEdit.TemporaryRefusal", handler);
        Assert.Contains("RailTechnologyEdit.NoTechnologyRefusal", handler);

        // NOT under the temp directory — _root is, so it cannot stand in for a workspace here.
        string tech = Path.Combine(Path.DirectorySeparatorChar + "workspaces", "demo", "tech", "board.ctech");
        Assert.Contains("no workspace open", RailTechnologyEdit.NoWorkspaceRefusal(tech),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("board.ctech", RailTechnologyEdit.NoWorkspaceRefusal(tech));
        Assert.Contains("Open the workspace", RailTechnologyEdit.NoWorkspaceRefusal(tech));

        // The second route to the same silent button: the throwaway import mints its .ctech into a
        // temp directory, which no workspace owns.
        string temp = Path.Combine(Path.GetTempPath(), "circuitrf-rail-abcd1234", "board.ctech");
        Assert.True(RailTechnologyEdit.IsTemporary(temp));
        Assert.False(RailTechnologyEdit.IsTemporary(tech));
        Assert.Contains("temporary", RailTechnologyEdit.TemporaryRefusal(temp));
        Assert.Contains("Keep the artwork in this workspace", RailTechnologyEdit.TemporaryRefusal(temp));
    }

    // ══ Gate 5 — the throwaway path names its consequence up front (R-rail22-2b) ════════════════

    [Fact]
    public void Gate5_TheCheckboxText_SaysTheTechnologyIsTemporaryAndCannotBeEdited()
    {
        string xaml = Read("src/Ui/Views/RailRf/RailImportDialog.axaml");
        int box = xaml.IndexOf("LandInWorkspaceBox", StringComparison.Ordinal);
        Assert.True(box >= 0);

        string note = xaml.Substring(box, 1600);
        Assert.Contains("temporary", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be edited", note, StringComparison.OrdinalIgnoreCase);

        // And the default is still ON — this is the UNCOMMON path, which is exactly why its
        // consequence has to be stated rather than discovered when something does nothing.
        Assert.Contains("IsChecked=\"True\"", xaml);
        Assert.True(new RailImportOptions().LandInWorkspace);
    }

    // ══ Gate 6 — a .pdf BOM is refused BY NAME, and the sentence names CSV (R-rail22-3a) ════════

    /// <summary>
    /// <b>No PDF reader, and the refusal names the file the user already has.</b> A PDF bill of
    /// materials is a rendering of a table rather than a table, and the failure mode is quiet and
    /// plausible — the same class as the Excellon suppression question, which <c>convert</c> refuses
    /// outright rather than guessing.
    /// </summary>
    [Fact]
    public void Gate6_APdfBomIsRefused_AndTheSentenceNamesTheCsv()
    {
        string? refusal = RailImportOptions.BomRefusal("/boards/demo/Bill of Materials.pdf");
        Assert.NotNull(refusal);
        Assert.Contains("Bill of Materials.pdf", refusal);
        Assert.Contains("PDF", refusal);
        Assert.Contains("CSV", refusal);
        Assert.True(new RailImportOptions { BomPath = "x.PDF" }.NeedsReadableBom);

        // Everything the reader can actually read is not refused — including the tab-separated
        // export the sentence offers as the alternative.
        Assert.Null(RailImportOptions.BomRefusal("bom.csv"));
        Assert.Null(RailImportOptions.BomRefusal("bom.txt"));
        Assert.Null(RailImportOptions.BomRefusal(null));

        // XLSX is deliberately NOT offered: this reader parses delimited text and nothing in
        // circuitRF opens a workbook, so naming it would be a second wrong file to try.
        Assert.DoesNotContain("XLSX", refusal, StringComparison.OrdinalIgnoreCase);

        // And the dialog says it rather than letting the import discover it.
        Assert.Contains("RailImportOptions.BomRefusal",
                        Strip(Read("src/Ui/Views/RailRf/RailImportDialog.axaml.cs")));
    }

    // ══ Gate 7 — the designer's CSV shape reads, and the import says what it read (R-rail22-3b) ══

    /// <summary>
    /// A grouped BOM with a quantity column expands to the right member count, and the three counts
    /// reach the window.
    /// </summary>
    /// <remarks>
    /// <b>Synthesised, not the designer's file</b> — this fixture is the SHAPE (grouped cells, a
    /// quantity column, one fragment that cannot be expanded), and carries no vendor part numbers.
    /// </remarks>
    [Fact]
    public void Gate7_AGroupedBomExpands_AndTheImportReportsTheThreeCounts()
    {
        const string csv =
            "Refdes,Qty,Value,Description\n" +
            "\"C3, C5, C7, C10, C30\",5,100nF,X7R 16V ceramic\n" +
            "C11-C19,9,1uF,X5R 10V ceramic\n" +
            "R1,1,10k,thick film\n" +
            "\"U1, U2\",2,,linear regulator\n" +
            "C100-,1,10uF,tantalum\n";

        string path = Path.Combine(_root, "bom.csv");
        File.WriteAllText(path, csv);

        var bom = BomFile.ReadFile(path);
        Assert.NotNull(bom);
        Assert.Null(bom!.Refusal);

        // 5 source rows in; 5 + 9 + 1 + 2 + 1 = 18 references out.
        Assert.Equal(5, bom.SourceRowCount);
        Assert.Equal(18, bom.Rows.Count);
        Assert.Single(bom.RowsFor("C7"));
        Assert.Single(bom.RowsFor("C19"));
        Assert.Single(bom.RowsFor("U2"));

        // "C100-" looks like a range and is not one, so it is taken as written and REPORTED —
        // the number that was computed all along and shown nowhere.
        Assert.Single(bom.UnexpandedCells);

        string summary = RailImportReport.BomSummary(bom);
        Assert.Contains("5 row(s) read", summary);
        Assert.Contains("18 reference(s)", summary);
        Assert.Contains("1 reference cell(s) could NOT be expanded", summary);

        // No BOM is no line at all, rather than a line of zeroes.
        Assert.Equal("", RailImportReport.BomSummary(null));

        // And the window has somewhere to put it that is not the refusal channel — which gates Run.
        string xaml = Read("src/Ui/Views/RailRf/RailRfWindow.axaml");
        Assert.Contains("{Binding ImportSummary}", xaml);
        Assert.Contains("{Binding HasImportSummary}", xaml);
        Assert.Contains("ImportSummary = RailImportReport.BomSummary(bom)",
                        Read("src/Ui/RailRf/RailRfViewModel.Import.cs"));
    }

    // ── Fixtures and helpers ─────────────────────────────────────────────────

    private const string MmHeader = "%FSLAX46Y46*%\n%MOMM*%\n";

    private static string Artwork(double xMm = 1.0, double yMm = 1.0)
    {
        long x = (long)Math.Round(xMm * 1_000_000);
        long y = (long)Math.Round(yMm * 1_000_000);
        return MmHeader + "%ADD10C,0.400*%\nD10*\n" + $"X{x}Y{y}D03*\n" + "M02*\n";
    }

    private static string Read(string repoRelative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), repoRelative));

    /// <summary>Comments removed, so a rule stated in prose does not satisfy a scan for the code
    /// that enforces it — the source-scan rule H8 set and every scan here follows.</summary>
    private static string Strip(string source)
    {
        var lines = source.Split('\n')
            .Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? "" : l);
        return string.Join("\n", lines);
    }

    private static string Between(string text, string from, string to)
    {
        int a = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, $"'{from}' is not in the file.");
        int b = text.IndexOf(to, a, StringComparison.Ordinal);
        return text.Substring(a, (b < 0 ? text.Length : b) - a);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
