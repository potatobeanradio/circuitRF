using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.RailRf;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Views.RailRf;

/// <summary>
/// §2.3 step 1: the dialog that gets a board into a railRF document.
/// </summary>
/// <remarks>
/// <b>It asks exactly the two things that must not be guessed</b> (R-rail7-7) — the placement origin
/// here, and the Excellon coordinate format through <see cref="Dialogs.GerberDrillFormatPromptDialog"/>
/// during the import itself, which is the dialog <c>convert</c>'s own rule already routes through.
/// Reusing that dialog rather than writing a second sentence for the same refusal is what stops the
/// two surfaces drifting.
///
/// <para>Returns a <see cref="RailImportOptions"/> via <c>ShowDialog&lt;RailImportOptions?&gt;</c>, or
/// null on Cancel, which aborts the whole import and leaves nothing behind.</para>
/// </remarks>
public partial class RailImportDialog : Window
{
    /// <summary>The three origins, in the order the dialog lists them, with the spellings
    /// <see cref="PlacementTable.Describe"/> already uses — so the dialog and every report name the
    /// same thing the same way.</summary>
    private static readonly (PlacementOrigin Origin, string Label)[] Origins =
    [
        (PlacementOrigin.SymbolOrigin, "the footprint's symbol origin"),
        (PlacementOrigin.BodyCentre,   "the part body's centre"),
        (PlacementOrigin.PinOne,       "pin 1"),
    ];

    /// <summary>
    /// The artwork file picker's filter.
    /// </summary>
    /// <remarks>
    /// <b>A CONVENIENCE, never a decision</b> (R-rail22-1c): what a file IS is settled by CONTENT
    /// through the import's own classifier, so nothing here can admit or exclude a file. The list is
    /// the Gerber import's own, because it is the same import. It lives on the dialog now rather
    /// than in <c>RailRfWindow.Import.cs</c> because the dialog is what opens the picker.
    /// </remarks>
    private static readonly string[] ArtworkPatterns =
    [
        "*.gbr", "*.gbrjob", "*.gdo", "*.gtl", "*.gbl", "*.gts", "*.gbs",
        "*.gto", "*.gbo", "*.gtp", "*.gbp", "*.gko", "*.gm1",
        "*.drl", "*.ncd", "*.xln", "*.txt", "*.kicad_pcb",
    ];

    public RailImportDialog() => InitializeComponent();

    /// <param name="workspaceDir">The open workspace's folder, or null. <b>Null is an OFFER to create
    /// one</b>, not a reason to fall back to the throwaway path.</param>
    /// <remarks>
    /// <b>No artwork argument any more</b> (R-rail22-1a). This dialog is now the FIRST thing Import
    /// Board opens, and the artwork is one of its rows — so there is nothing to be told and the box
    /// starts empty, which is what "nothing is pre-selected" means here.
    /// </remarks>
    public RailImportDialog(string? workspaceDir) : this()
    {
        _workspaceDir = workspaceDir;

        NoWorkspaceText.IsVisible = workspaceDir is null;

        // ITEMSSOURCE FIRST, AND NOTHING SELECTS ANYTHING AFTER IT. That ordering is the wBond
        // round-6 rule; the stronger invariant here is that SelectedIndex stays -1 until the user
        // acts, because a pre-selected origin is exactly the guess Q-14 closed.
        OriginCombo.ItemsSource = Origins.Select(o => o.Label).ToList();

        ArtworkFilePick.Click   += async (_, _) => await PickArtworkFile();
        ArtworkFolderPick.Click += async (_, _) => await PickArtworkFolder();
        PlacementPick.Click     += async (_, _) => await PickInto(PlacementBox, "Placement file");
        BomPick.Click           += async (_, _) => await PickInto(BomBox, "Bill of materials");
        NetlistPick.Click       += async (_, _) => await PickInto(NetlistBox, "Board netlist");
        PartLibraryPick.Click   += async (_, _) => await PickInto(PartLibraryBox, "Part library");
    }

    private string? _workspaceDir;

    /// <summary>Both artwork buttons write the same box, because the box is the answer and the two
    /// buttons are only two ways of reaching it — including typing a path into it.</summary>
    private async Task PickArtworkFile()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "railRF — Import Board",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Board artwork") { Patterns = ArtworkPatterns },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] },
            ],
        });
        if (files.Count > 0) ArtworkBox.Text = files[0].Path.LocalPath;
    }

    private async Task PickArtworkFolder()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "railRF — Board Folder",
            AllowMultiple = false,
        });
        if (folders.Count > 0) ArtworkBox.Text = folders[0].Path.LocalPath;
    }

    private async Task PickInto(TextBox box, string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"railRF — {title}",
            AllowMultiple = false,
        });
        if (files.Count > 0) box.Text = files[0].Path.LocalPath;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        var options = Build();

        // R-rail22-1a. Nothing is pre-selected, so nothing chosen is the state the dialog OPENS in
        // and it has to be answerable rather than merely wrong — both buttons are named.
        if (options.NeedsArtwork)
        {
            Refuse(RailImportOptions.ArtworkRefusal, ArtworkBox);
            return;
        }

        // R-rail22-3a. A PDF bill of materials is a picture of a table, and the user almost always
        // has the table itself beside it — so the refusal names that file rather than the format.
        if (RailImportOptions.BomRefusal(options.BomPath) is { } bomRefusal)
        {
            Refuse(bomRefusal, BomBox);
            return;
        }

        // R-rail7-7: the origin is a REFUSAL, stated here with the flag that answers it headless, so
        // the dialog and `circuitrf rail --origin` say the same thing. The dialog does not close on
        // it — the answer is one control away.
        if (options.NeedsPlacementOrigin)
        {
            Refuse(
                $"\u201c{System.IO.Path.GetFileName(options.PlacementPath!)}\u201d needs its "
              + "coordinate origin stated; set it here. railRF does not guess it — three quarters "
              + "of a millimetre on an 0402 is the difference between landing on the part's own pad "
              + "and landing on its neighbour's.",
                OriginCombo);
            return;
        }

        // ── THE TWO COMPANIONS ARE TESTED *HERE*, WHERE THE ANSWER IS (field report, 2026-09-22) ─
        //
        // Both files were read only after the whole import had run, so a designer who pointed a row
        // at the wrong file learned about it from a log line with the dialog long closed — and in
        // one case the sentence he got named a command-line flag. The dialog has refused an
        // unreadable BOM and an unstated origin since it was written, at the moment they can be
        // fixed; there was no reason these two were different.
        //
        // NEITHER IS A HARD GATE. A board with no netlist and no placement table is the ordinary
        // assisted-Gerber path and solves perfectly well, so each refusal names CLEARING THE BOX as
        // an exit. What must not happen is that a file was named and nothing said what became of it.
        // THE NETLIST FIRST, because it is the cheap one. Both are refusals the dialog stays open
        // on, and the placement route can open a second dialog — so checking it first would make a
        // user who then hits the netlist refusal walk through the column mapping again on their
        // next press.
        if (!CheckNetlist(options)) return;

        // Re-read from the file on every press: Build() constructs a fresh options record, so a
        // mapping given for one file can never be carried onto whatever the row names next.
        if (await ResolvePlacementAsync(options) is not { } withPlacement) return;

        Close(withPlacement);
    }

    /// <summary>
    /// Reads the placement file the dialog names, and — where it has no header row — opens the
    /// column-naming dialog rather than refusing with a flag nobody can reach.
    /// </summary>
    /// <remarks>
    /// <b><see cref="PlacementFile"/>'s refusal is right and stays</b>: column order is not a
    /// standard, and a positional reading puts the rotation in the Y column silently. What was
    /// missing is the GUI half of the answer — the sentence said "Name them with --columns" to a
    /// user in a window.
    ///
    /// <para>Returns the options to import with, or null where the user answered neither — in which
    /// case the dialog stays open, which is <see cref="Refuse"/>'s own rule.</para>
    /// </remarks>
    private async Task<RailImportOptions?> ResolvePlacementAsync(RailImportOptions options)
    {
        if (options.PlacementPath is not { Length: > 0 } path) return options;

        string text;
        try { text = System.IO.File.ReadAllText(path); }
        catch (Exception ex)
        {
            Refuse($"\u201c{System.IO.Path.GetFileName(path)}\u201d could not be read: {ex.Message} "
                 + "Point the row at another file, or clear the box to import without a placement "
                 + "table — the parts table's Position column is then empty and nothing else changes.",
                PlacementBox);
            return null;
        }

        var read = PlacementFile.Read(
            path, text, CircuitRF.Design.Layout.LayoutUnits.DefaultDbuPerMicron, options.PlacementOrigin);

        if (read.Refusal is not { Length: > 0 } refusal) return options;

        // A header this reader could not find is the one refusal with a control that answers it.
        // Every other one — an ambiguous header, a missing X column, a file that is not a table at
        // all — is about the FILE, and the answer is a different file.
        if (!PlacementFile.HasNoHeader(read))
        {
            Refuse($"\u201c{System.IO.Path.GetFileName(path)}\u201d {refusal} Point the row at "
                 + "another file, or clear the box to import without a placement table.",
                PlacementBox);
            return null;
        }

        var chosen = await new RailPlacementColumnsDialog(
            System.IO.Path.GetFileName(path),
            DelimitedTables.Parse(text)).ShowDialog<RailPlacementColumnChoice?>(this);

        // "Skip this file" is an ANSWER, not a cancel: the artwork imports and the placement row is
        // cleared, which is exactly the state a board that shipped no placement table is in.
        if (chosen is null)
            return options with { PlacementPath = null, PlacementOrigin = null };

        return options with
        {
            PlacementColumns = chosen.Columns,
            PlacementUnits   = chosen.Units,
        };
    }

    /// <summary>
    /// Reads the board netlist the dialog names, so a file that is not one is reported HERE.
    /// </summary>
    /// <remarks>
    /// The sentence is <c>RailImportReport</c>'s, unchanged — one refusal, one wording, on every
    /// surface. What is added is the exit, because this row is genuinely optional: a board with no
    /// netlist anchors every port by coordinate and solves.
    /// </remarks>
    private bool CheckNetlist(RailImportOptions options)
    {
        if (options.BoardNetlistPath is not { Length: > 0 } path) return true;

        var read = BoardNetlistFile.ReadFile(
            path, CircuitRF.Design.Layout.LayoutUnits.DefaultDbuPerMicron);

        if (read is null)
        {
            Refuse($"\u201c{System.IO.Path.GetFileName(path)}\u201d could not be read. Point the row "
                 + "at another file, or clear the box to import without one.", NetlistBox);
            return false;
        }

        if (read.Refusal is not { Length: > 0 } refusal) return true;

        Refuse($"\u201c{read.FileName}\u201d {RailImportReport.RefusalTail(refusal)} Point the row "
             + "at the right file, or clear the box to import without one — every port is then "
             + "anchored by coordinate instead of by reference designator, which is the ordinary "
             + "assisted-Gerber path.", NetlistBox);
        return false;
    }

    /// <summary>Says the sentence and puts the caret on the control that answers it. <b>The dialog
    /// does not close on a refusal</b> — the answer is one control away, which is the rule the
    /// origin refusal already followed and the reason all three go through one place.</summary>
    private void Refuse(string sentence, Control answersIt)
    {
        RefusalText.Text = sentence;
        RefusalText.IsVisible = true;
        answersIt.Focus();
    }

    /// <summary>What the dialog currently states. Exposed so the shape of the result is testable
    /// without driving the controls through a window.</summary>
    internal RailImportOptions Build() => new()
    {
        ArtworkPath      = Text(ArtworkBox) ?? "",
        LandInWorkspace  = LandInWorkspaceBox.IsChecked == true,
        PlacementPath    = Text(PlacementBox),
        PlacementOrigin  = OriginCombo.SelectedIndex >= 0 ? Origins[OriginCombo.SelectedIndex].Origin : null,
        BomPath          = Text(BomBox),
        BoardNetlistPath = Text(NetlistBox),
        PartLibraryPath  = Text(PartLibraryBox),
        WorkspaceDir     = _workspaceDir,
    };

    private static string? Text(TextBox box) =>
        box.Text is { Length: > 0 } t ? t.Trim() : null;
}
