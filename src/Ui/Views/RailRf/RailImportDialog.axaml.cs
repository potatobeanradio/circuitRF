using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.RailRf;

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

    private void OnImportClick(object? sender, RoutedEventArgs e)
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
              + "coordinate origin stated; pass --origin or set it here. railRF does not guess it — "
              + "three quarters of a millimetre on an 0402 is the difference between landing on the "
              + "part's own pad and landing on its neighbour's.",
                OriginCombo);
            return;
        }

        Close(options);
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
