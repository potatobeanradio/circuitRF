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

    public RailImportDialog() => InitializeComponent();

    /// <param name="artworkPath">What the user pointed at — a Gerber set's folder, one Gerber file,
    /// or a <c>.kicad_pcb</c>.</param>
    /// <param name="workspaceDir">The open workspace's folder, or null. <b>Null is an OFFER to create
    /// one</b>, not a reason to fall back to the throwaway path.</param>
    public RailImportDialog(string artworkPath, string? workspaceDir) : this()
    {
        _artworkPath = artworkPath;
        _workspaceDir = workspaceDir;

        ArtworkText.Text =
            $"Importing \u201c{System.IO.Path.GetFileName(artworkPath.TrimEnd('/', '\\'))}\u201d.";

        NoWorkspaceText.IsVisible = workspaceDir is null;

        // ITEMSSOURCE FIRST, AND NOTHING SELECTS ANYTHING AFTER IT. That ordering is the wBond
        // round-6 rule; the stronger invariant here is that SelectedIndex stays -1 until the user
        // acts, because a pre-selected origin is exactly the guess Q-14 closed.
        OriginCombo.ItemsSource = Origins.Select(o => o.Label).ToList();

        PlacementPick.Click   += async (_, _) => await PickInto(PlacementBox, "Placement file");
        BomPick.Click         += async (_, _) => await PickInto(BomBox, "Bill of materials");
        NetlistPick.Click     += async (_, _) => await PickInto(NetlistBox, "Board netlist");
        PartLibraryPick.Click += async (_, _) => await PickInto(PartLibraryBox, "Part library");
    }

    private string _artworkPath = "";
    private string? _workspaceDir;

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

        // R-rail7-7: the origin is a REFUSAL, stated here with the flag that answers it headless, so
        // the dialog and `circuitrf rail --origin` say the same thing. The dialog does not close on
        // it — the answer is one control away.
        if (options.NeedsPlacementOrigin)
        {
            RefusalText.Text =
                $"\u201c{System.IO.Path.GetFileName(options.PlacementPath!)}\u201d needs its "
              + "coordinate origin stated; pass --origin or set it here. railRF does not guess it — "
              + "three quarters of a millimetre on an 0402 is the difference between landing on the "
              + "part's own pad and landing on its neighbour's.";
            RefusalText.IsVisible = true;
            OriginCombo.Focus();
            return;
        }

        Close(options);
    }

    /// <summary>What the dialog currently states. Exposed so the shape of the result is testable
    /// without driving the controls through a window.</summary>
    internal RailImportOptions Build() => new()
    {
        ArtworkPath      = _artworkPath,
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
