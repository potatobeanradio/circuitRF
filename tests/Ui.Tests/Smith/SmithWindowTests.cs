using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Smith;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests.Smith;

/// <summary>
/// The Smith Chart DOCUMENT and its window (brief-smith-4-document-window.md §5). <b>One test per
/// claim</b>, and the claims are about a document behaving like a document — the chart pane and the
/// network pane are briefs 5 and 6.
///
/// <para><b>Four of the eight are source scans, and that is not a shortcut.</b>
/// <c>WorkspaceViewModel</c> cannot be constructed headlessly — its constructor builds the Dock
/// layout and posts to the Dispatcher — which is the fallback this codebase already uses for every
/// other <c>WorkspaceViewModel</c>-only rule (<c>ActivateOpenDocumentTests</c> says so in its own
/// header). What CAN be driven directly is driven directly: the document, its view model, its edit
/// history and its save route all work with no display, which is itself part of
/// <c>R-smith4-1</c>'s claim.</para>
/// </summary>
public sealed class SmithWindowTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return dir!;
    }

    private static string ReadRepoFile(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

    /// <summary>The <c>AuthoringCliVerbTests</c> stripper, verbatim — a rule stated in a comment is
    /// not a rule the code follows.</summary>
    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    /// <summary>XML comments are <c>//</c>-prefixed and go with the rest; XAML's are their own
    /// shape.</summary>
    private static string StripXamlComments(string markup)
        => Regex.Replace(markup, @"<!--.*?-->", "", RegexOptions.Singleline);

    private static string TempDir([CallerMemberName] string name = "")
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-smith-window", name, Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A two-point one-port file whose S₁₁ is real, so the impedance it imports to is one
    /// anybody can check by hand: Z = Z₀(1+S)/(1−S).</summary>
    private static string WriteS1p(string dir, string name = "gen.s1p")
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path,
            "! a generator impedance\n" +
            "# GHZ S MA R 50\n" +
            "1.0 0.5 0.0\n" +
            "2.0 0.5 0.0\n");
        return path;
    }

    private static SmithChartDocument Scratch()
        => new("Untitled-Smith-1", new SmithChartViewModel());

    // ── 1. it round-trips through the shell (gate 1) ─────────────────────────

    /// <summary>
    /// Open, edit, dirty mark, Save, reopen — and the reopened document is the one that was saved.
    /// </summary>
    /// <remarks>
    /// <b>The dirty mark is asserted on the TAB TITLE and not on a boolean</b>, because the bullet is
    /// what the user actually sees and it is the half that has gone wrong before: the document mirrors
    /// the view model (the view model is the source of truth, never the reverse), so a document that
    /// reported <c>IsDirty</c> correctly and forgot to re-title would pass a boolean assertion and
    /// still show a clean tab over unsaved work.
    /// </remarks>
    [Fact]
    public void ADocumentEditedAndSaved_ShowsTheBulletThenLosesIt_AndReopensAsWhatWasSaved()
    {
        string dir  = TempDir();
        string path = Path.Combine(dir, "lna_input.csmith");

        var doc = Scratch();
        Assert.False(doc.IsDirty);
        Assert.Equal("Untitled-Smith-1", doc.Title);

        // One committed edit: Conjugate, whose whole visible effect is the sign of every X.
        doc.ViewModel.Design.Generator.Rows[0].ReactanceOhm = -12.5;
        doc.ViewModel.MarkSaved();                       // baseline: the row as typed
        doc.ViewModel.ConjugateCommand.Execute(null);

        Assert.True(doc.IsDirty);
        Assert.Equal("• Untitled-Smith-1", doc.Title);

        Assert.Null(SmithChartDocumentSave.Write(doc, path, workspace: null));
        Assert.True(File.Exists(path));
        Assert.False(doc.IsDirty);
        Assert.Equal("lna_input", doc.Title);   // the tab follows the file, bullet gone
        Assert.Equal(path, doc.FilePath);

        var reopened = SmithChartDocument.Open(path);
        Assert.False(reopened.IsDirty);
        Assert.Equal(12.5, reopened.ViewModel.Design.Generator.Rows[0].ReactanceOhm, 12);
        Assert.Equal(doc.ViewModel.Design.DesignFrequencyHz,
                     reopened.ViewModel.Design.DesignFrequencyHz, 12);
    }

    /// <summary>
    /// The close-time prompt. <b>The document cannot ask for itself</b> — the Save / Don't Save /
    /// Cancel dialog belongs to the shell, which is the whole of <c>R-smith4-1</c> — so what is
    /// checkable here is that the shell's own hook has a branch for this kind, with the rule every
    /// other kind's branch carries: a cancelled picker leaves the document dirty and must cancel the
    /// close, or "Save" would quietly behave as "Don't Save".
    /// </summary>
    [Fact]
    public void TheShellsCloseHook_PromptsForADirtySmithChart_AndACancelledSaveCancelsTheClose()
    {
        string code = StripComments(ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs"));

        Assert.Contains("dockable is SmithChartDocument smithCloseDoc && smithCloseDoc.IsDirty",
                        code, StringComparison.Ordinal);
        Assert.Contains("await SaveSmithChartDoc(smithCloseDoc, window);\n                    return !smithCloseDoc.IsDirty;",
                        code.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    // ── 2. a second open focuses the first (gate 2) ──────────────────────────

    /// <summary>
    /// <b>The single-instance rule every document type has.</b> A second open of a path already open
    /// activates that document rather than opening a second view of one file — and through
    /// <c>ActivateOpenDocument</c> rather than a bare <c>SetActiveDockable</c>, because a document
    /// torn off into its own window has to be RAISED and not merely selected behind the shell. That
    /// distinction is the reported Data Display bug <c>ActivateOpenDocumentTests</c> exists for; this
    /// asserts the Smith route joined it rather than growing a fifth copy.
    /// </summary>
    [Fact]
    public void OpenSmithPath_ActivatesAnAlreadyOpenDocument_RatherThanOpeningASecond()
    {
        string code  = StripComments(ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs"));
        int    start = code.IndexOf("public void OpenSmithPath(", StringComparison.Ordinal);
        Assert.True(start > 0, "OpenSmithPath is gone — the .csmith double-click route with it.");

        string body = code[start..Math.Min(code.Length, start + 1800)];

        Assert.Contains("_openDocsByPath.TryGetValue(full, out var already)", body, StringComparison.Ordinal);
        Assert.Contains("ActivateOpenDocument(already)", body, StringComparison.Ordinal);

        // And the open path itself: the document is opened and REGISTERED by path, or the next
        // double-click would open a second one anyway.
        Assert.Contains("SmithChartDocument.Open(full)", body, StringComparison.Ordinal);
        Assert.Contains("_openDocsByPath[full] = doc", body, StringComparison.Ordinal);

        // R-smith4-2: a read refusal is reported, never thrown out of a double-click.
        Assert.Contains("Messages.Error($\"Could not open {name}: {ex.Message}\")", body, StringComparison.Ordinal);
    }

    // ── 3. a scratch document needs no workspace (gate 3) ────────────────────

    /// <summary>
    /// <b>A scratch <c>.csmith</c> opens with nothing else loaded, and Save As gives it a home.</b>
    /// Nothing in this test names a workspace — the save route takes <c>null</c> for one, which is
    /// the same thing harmonicaRF's does and the same thing the standalone case needs.
    /// </summary>
    /// <remarks>
    /// The scratch document opens on a real generator rather than an empty one, for the reason
    /// <see cref="SmithChartViewModel.NewScratchDesign"/> gives: an empty generator table is a
    /// refusal, so a blank document would open showing its own error message. Asserted here because
    /// it is the state Tools ▸ Smith Chart and the On Launch row both produce.
    /// </remarks>
    [Fact]
    public void AScratchDocumentOpensWithNoWorkspace_AndSaveAsGivesItAPath()
    {
        var doc = Scratch();

        Assert.True(doc.IsScratch);
        Assert.Null(doc.FilePath);
        Assert.Null(doc.ViewModel.DocumentDirectory);

        // It opens on something well formed, not on a refusal.
        Assert.Null(doc.ViewModel.Design.Refusal());
        Assert.Null(doc.ViewModel.Refusal);
        Assert.Single(doc.ViewModel.GeneratorRows);
        Assert.Contains("VSWR", doc.ViewModel.StatusLine, StringComparison.Ordinal);

        string dir  = TempDir();
        string path = Path.Combine(dir, "scratch.csmith");
        Assert.Null(SmithChartDocumentSave.Write(doc, path, workspace: null));

        Assert.False(doc.IsScratch);
        Assert.Equal(path, doc.FilePath);
        Assert.Equal(dir.TrimEnd(Path.DirectorySeparatorChar),
                     doc.ViewModel.DocumentDirectory?.TrimEnd(Path.DirectorySeparatorChar));
    }

    // ── 4. Undo routing (gate 4) ─────────────────────────────────────────────

    /// <summary>
    /// <b>The shell's Undo must be able to REACH this document.</b>
    /// </summary>
    /// <remarks>
    /// The regression is the one <see cref="IEditHistoryDocument"/> exists for: with a FLOATING Data
    /// Display in focus, Cmd+Z undid an edit in a schematic that was not in focus. On macOS the menu
    /// bar is app-global — the same <c>NativeMenu</c> is attached to every torn-off window — so Edit ▸
    /// Undo fires the SHELL's Undo command from whichever window is key, and that command had no way
    /// to reach a document it was not typed to. A document that cannot be reached that way has the
    /// same bug.
    ///
    /// <para>This document takes <c>IUndoableDocument</c>, which IS an <c>IEditHistoryDocument</c> —
    /// the documented shortcut for a history that is an <c>UndoRedoStack</c>. Both halves are
    /// asserted: the interface the shell's dispatch is typed to, and that Undo actually reverses the
    /// edit rather than merely being callable.</para>
    /// </remarks>
    [Fact]
    public void TheShellsUndo_ReachesThisDocumentThroughIEditHistoryDocument_AndReversesTheEdit()
    {
        var doc = Scratch();

        var history = Assert.IsAssignableFrom<IEditHistoryDocument>(doc);
        Assert.False(history.CanUndoLast);

        doc.ViewModel.ConjugateCommand.Execute(null);
        Assert.True(history.CanUndoLast);

        // The description is a real one, which is what taking IUndoableDocument over the bare
        // interface buys: the menu item says what it will undo instead of a naked verb.
        Assert.Contains("Conjugate", history.UndoLastDescription, StringComparison.Ordinal);

        history.UndoLast();
        Assert.False(history.CanUndoLast);
        Assert.True(history.CanRedoLast);

        // And the shell's own dispatch is typed to the interface, not to a list of document types.
        string code = StripComments(ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs"));
        Assert.Contains("SetActiveUndoTarget(activeDockable as IEditHistoryDocument)", code, StringComparison.Ordinal);
    }

    // ── 5. the LaunchAction ordinal contract (gate 5, R-smith4-10) ───────────

    /// <summary>
    /// <b>The test that stops the next person getting it wrong quietly.</b>
    /// </summary>
    /// <remarks>
    /// <see cref="LaunchAction"/> is serialized as an ORDINAL, and
    /// <c>SettingsView.LoadGeneralPrefs</c> fills the combobox from a bare <c>string[]</c> whose index
    /// is cast straight back to the enum. Reordering or inserting into EITHER silently changes what
    /// every already-saved <c>preferences.json</c> means: no error, no migration, and a user's chosen
    /// launch action becomes a different one. So the array's LENGTH and its ORDER are both asserted
    /// against the enum, and <c>NewSmithChart</c> is asserted to be LAST — appended, never inserted.
    /// </remarks>
    [Fact]
    public void TheLaunchActionComboboxArray_MatchesTheEnumInLengthAndOrder()
    {
        string source = ReadRepoFile("src/Ui/Views/Dialogs/SettingsView.axaml.cs");
        int    at     = source.IndexOf("LaunchActionCombo.ItemsSource", StringComparison.Ordinal);
        Assert.True(at > 0, "LaunchActionCombo.ItemsSource is gone — this contract has no other home.");

        int open  = source.IndexOf('{', at);
        int close = source.IndexOf("};", open, StringComparison.Ordinal);
        Assert.True(close > open, "Could not read the launch-action array literal.");

        var labels = Regex.Matches(source[open..close], "\"([^\"]*)\"")
                          .Select(m => m.Groups[1].Value)
                          .ToArray();

        var members = Enum.GetNames<LaunchAction>();

        Assert.Equal(members.Length, labels.Length);

        // Order: the LAST member is the one this brief appended, and it is last in both. A member
        // inserted anywhere else would leave the lengths equal and the meanings shifted, which is
        // exactly the silent failure — so the pairing is checked position by position.
        Assert.Equal(LaunchAction.NewSmithChart, Enum.GetValues<LaunchAction>()[^1]);
        Assert.Equal("Smith Chart", labels[^1]);
        Assert.Equal("Welcome",     labels[0]);
        Assert.Equal("harmonicaRF", labels[(int)LaunchAction.NewHarmonica]);
        Assert.Equal("New Layout",  labels[(int)LaunchAction.NewLayout]);

        // And both switches that act on the preference carry the new case; one without the other is
        // a launch action that does nothing on one of the two paths and nothing reports it.
        string vm = StripComments(ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs"));
        Assert.Equal(2, Regex.Matches(vm, @"case LaunchAction\.NewSmithChart:").Count);
    }

    // ── 6. both Tools menus carry the entry (gate 6, R-smith4-9) ─────────────

    /// <summary>
    /// <b>The macOS <c>NativeMenu</c> and the in-window menu are hand-maintained and must not
    /// drift</b> — the file already says so, and this is that comment made into a check. An entry on
    /// one surface only is invisible on whichever platform reads the other.
    /// </summary>
    [Fact]
    public void BothToolsMenus_CarryTheSmithChartEntry()
    {
        string markup = StripXamlComments(ReadRepoFile("src/Ui/Views/WorkspaceWindow.axaml"));

        Assert.Contains("<NativeMenuItem Header=\"Smith Chart\" Command=\"{Binding NewSmithChartCommand}\"/>",
                        markup, StringComparison.Ordinal);
        Assert.Contains("Header=\"_Smith Chart\"", markup, StringComparison.Ordinal);

        // Twice and no more: once per surface. A third would be a stray entry in some other menu.
        Assert.Equal(2, Regex.Matches(markup, @"NewSmithChartCommand").Count);

        // Beside harmonicaRF, Match Designer and railRF on both — the row this one was asked to join.
        foreach (string neighbour in new[] { "NewHarmonicaCommand", "NewMatchDesignerCommand", "NewRailRfCommand" })
            Assert.Equal(2, Regex.Matches(markup, neighbour).Count);
    }

    // ── 7. every editable value is an InlineEditText (gate 7, R-smith4-5) ────

    /// <summary>
    /// <b>Owner instruction, and the word is <i>every</i>.</b> No bare <c>TextBox</c> in
    /// <c>src/Ui/Views/Smith/</c> carries a two-way binding to a design value.
    /// </summary>
    /// <remarks>
    /// The reason is the owner's: <c>InlineEditText</c> already carries the Match Designer's
    /// specification pane, railRF's source rows and harmonicaRF's readout strip, so the user has
    /// already learned it and we have already debugged it — the three-key contract (Return commits and
    /// handles, LostFocus commits, Escape reverts), double-click rather than single, the unit left
    /// unselected in the text, and the alignment honoured by the resting text and the open box
    /// together.
    ///
    /// <para><b>The scan is for a two-way <c>TextBox</c> and not for the word <c>TextBox</c></b>,
    /// because a read-only one would be legitimate and this rule is about EDITABLE values. Comments
    /// are stripped first, on <c>AuthoringCliVerbTests</c>' own precedent: a rule stated in a comment
    /// is not a rule the code follows.</para>
    ///
    /// <para>It also asserts there is no fourth inline editor here — the trap <c>R-smith4-5</c> exists
    /// to close — and no <c>Window</c> subclass, which is <c>R-smith4-1</c>'s own tell.</para>
    /// </remarks>
    [Fact]
    public void NoBareTextBoxInTheSmithViews_CarriesATwoWayBindingToADesignValue()
    {
        string viewDir = Path.Combine(RepoRoot(), "src", "Ui", "Views", "Smith");
        var    files   = Directory.GetFiles(viewDir, "*.axaml", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        bool sawInlineEditor = false;

        foreach (string file in files)
        {
            string markup = StripXamlComments(File.ReadAllText(file));

            foreach (System.Text.RegularExpressions.Match element in Regex.Matches(markup, @"<TextBox\b[^>]*>", RegexOptions.Singleline))
                Assert.False(element.Value.Contains("Mode=TwoWay", StringComparison.Ordinal),
                    $"{Path.GetFileName(file)}: a bare TextBox is two-way bound to a design value. "
                  + "R-smith4-5 — every editable value in this window is an InlineEditText, and the "
                  + "word is every.");

            sawInlineEditor |= markup.Contains("InlineEditText", StringComparison.Ordinal);
        }

        Assert.True(sawInlineEditor,
            "No InlineEditText anywhere under src/Ui/Views/Smith — the scan is guarding nothing.");

        // R-smith4-1's own tell: a Window subclass here would mean the Smith Chart had become an
        // application again. Checked over the code-behind, comments stripped.
        foreach (string file in Directory.GetFiles(viewDir, "*.cs", SearchOption.AllDirectories))
        {
            string code = StripComments(File.ReadAllText(file));
            Assert.DoesNotMatch(new Regex(@"class\s+\w+\s*:\s*Window\b"), code);
        }
    }

    // ── the splitters (R-smith4-3) ───────────────────────────────────────────

    /// <summary>
    /// <b>The splitter positions ARE the document's own View block</b>, and the view applies them in
    /// code rather than by binding.
    /// </summary>
    /// <remarks>
    /// Both halves are worth one test because both fail silently. A view model that kept its own copy
    /// of the fractions would save the document's stale ones; and a <c>RowDefinition</c> is not in the
    /// logical tree, so a binding on its <c>Height</c> inherits no DataContext, resolves against
    /// nothing and simply never applies — the splitter moves, the document remembers nothing, and no
    /// error is raised anywhere. Nothing else in this application binds a definition's size.
    /// </remarks>
    [Fact]
    public void TheSplitterFractionsLiveInTheDocumentsViewBlock_AndAreAppliedInCode()
    {
        var vm = new SmithChartViewModel();

        vm.SplitterSide = 0.3;
        vm.SplitterMain = 0.8;
        Assert.Equal(0.3, vm.Design.View.SplitterSide, 9);
        Assert.Equal(0.8, vm.Design.View.SplitterMain, 9);

        // Clamped, so a stored zero cannot reopen the document with a region collapsed to nothing.
        vm.SplitterSide = 0.0;
        Assert.True(vm.SplitterSide > 0.0);

        var back = SmithDesignIo.Deserialize(SmithDesignIo.Serialize(vm.Design));
        Assert.Equal(0.8, back.View.SplitterMain, 9);

        string markup = StripXamlComments(ReadRepoFile("src/Ui/Views/Smith/SmithChartView.axaml"));
        Assert.DoesNotMatch(new Regex(@"<(Row|Column)Definition[^>]*(Height|Width)=""\{Binding"), markup);

        string code = StripComments(ReadRepoFile("src/Ui/Views/Smith/SmithChartView.axaml.cs"));
        Assert.Contains("RowDefinitions[0].Height", code, StringComparison.Ordinal);
        Assert.Contains("ColumnDefinitions[0].Width", code, StringComparison.Ordinal);
    }

    // ── 8. import, re-import and conjugate (gate 8, R-smith4-7) ──────────────

    /// <summary>
    /// <b>The import REPLACES the table and records the path; Conjugate is ONE undo entry.</b>
    /// </summary>
    /// <remarks>
    /// The three claims are tested together because they are one gesture sequence on one table, and
    /// the interesting failure is in how they interact: an import that merged would leave rows from
    /// the typed table mixed with the file's, and a Conjugate that pushed one entry per row would take
    /// as many Undos as the file had frequencies.
    ///
    /// <para><b>The path is provenance and nothing resolves it at load</b>, which is what Re-import
    /// depends on and what makes a document portable. Asserted by re-importing from the recorded path
    /// after the table has been changed underneath it.</para>
    /// </remarks>
    [Fact]
    public void Import_ReplacesTheTableAndRecordsThePath_AndConjugateIsOneUndoEntry()
    {
        string dir  = TempDir();
        string s1p  = WriteS1p(dir);
        var    doc  = Scratch();
        var    vm   = doc.ViewModel;

        // Something to be replaced: a typed table of three rows that is not the file's two.
        vm.AddGeneratorRowCommand.Execute(null);
        vm.AddGeneratorRowCommand.Execute(null);
        Assert.Equal(3, vm.GeneratorRows.Count);

        Assert.Null(vm.ImportGeneratorFrom(s1p));

        // REPLACED, not merged — and the numbers are the file's own: Z = 50(1+0.5)/(1−0.5) = 150 Ω.
        Assert.Equal(2, vm.Design.Generator.Rows.Count);
        Assert.Equal(2, vm.GeneratorRows.Count);
        Assert.Equal(1e9, vm.Design.Generator.Rows[0].FrequencyHz, 3);
        Assert.Equal(150.0, vm.Design.Generator.Rows[0].ResistanceOhm, 6);
        Assert.Equal(0.0,   vm.Design.Generator.Rows[0].ReactanceOhm,  6);

        Assert.Equal(s1p, vm.Design.Generator.SourcePath);
        Assert.True(vm.HasSourcePath);
        Assert.True(vm.ReimportGeneratorCommand.CanExecute(null));

        // RE-IMPORT repeats the read from the recorded path. Change the table first, so a re-import
        // that did nothing would be visible.
        vm.Edit("test", () => vm.Design.Generator.Rows.Clear());
        Assert.Empty(vm.Design.Generator.Rows);
        vm.ReimportGeneratorCommand.Execute(null);
        Assert.Equal(2, vm.Design.Generator.Rows.Count);
        Assert.Equal(150.0, vm.Design.Generator.Rows[0].ResistanceOhm, 6);

        // CONJUGATE: one entry, whatever the table's length, and a single Undo unwinds it completely.
        //
        // The two reactances are seeded THROUGH Edit rather than written straight onto the design,
        // and that is not ceremony: an un-snapshotted mutation is invisible to the history, so
        // CountUndoEntries' drain-and-replay would silently discard it and the conjugate would be
        // measured against zeros. Every mutation in this window goes through Edit for the same
        // reason the production code does.
        vm.Edit("Seed the table", () =>
        {
            vm.Design.Generator.Rows[0].ReactanceOhm = -20.0;
            vm.Design.Generator.Rows[1].ReactanceOhm = -30.0;
        });

        int before = CountUndoEntries(vm);
        vm.ConjugateCommand.Execute(null);
        Assert.Equal(before + 1, CountUndoEntries(vm));

        Assert.Equal(20.0, vm.Design.Generator.Rows[0].ReactanceOhm, 9);
        Assert.Equal(30.0, vm.Design.Generator.Rows[1].ReactanceOhm, 9);

        vm.UndoRedo.Undo();
        Assert.Equal(-20.0, vm.Design.Generator.Rows[0].ReactanceOhm, 9);
        Assert.Equal(-30.0, vm.Design.Generator.Rows[1].ReactanceOhm, 9);
    }

    /// <summary>
    /// How many entries the undo stack holds, counted by draining it and putting it back.
    /// </summary>
    /// <remarks>
    /// <see cref="UndoRedoStack"/> exposes <c>CanUndo</c> and not a count, deliberately — nothing in
    /// the application needs one. Counting by walking is what lets the one-gesture-one-entry rule be
    /// asserted as a NUMBER rather than as "at least one", which is the only form that catches a
    /// Conjugate that pushed an entry per row.
    /// </remarks>
    private static int CountUndoEntries(SmithChartViewModel vm)
    {
        int n = 0;
        while (vm.UndoRedo.CanUndo) { vm.UndoRedo.Undo(); n++; }
        for (int i = 0; i < n; i++) vm.UndoRedo.Redo();
        return n;
    }
}
