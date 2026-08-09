using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// The management surface for a workspace's PDK references: add, remove, reveal, validate.
///
/// <para><b>Why it is required rather than nice to have.</b> A kit's parts used to appear in the
/// Project Tree as ordinary cell folders. They are held in memory now, so they appear nowhere — and
/// without this dialog a workspace's dependency on a kit would be invisible until something failed to
/// resolve, with nowhere to go and repair it.</para>
///
/// <para>Every decision here lives in <see cref="PdkReferenceManager"/>, which is framework-free and
/// tested. This is presentation plus the two things that genuinely need a window: a folder picker and
/// the platform's file manager.</para>
/// </summary>
public partial class ManagePdksDialog : Window
{
    /// <summary>What the caller must supply — the dialog owns no workspace state of its own.</summary>
    public sealed record Context(
        string WorkspaceRootDir,
        List<CwsPdkRef> Refs,
        IReadOnlyList<string> PlacedPartRefs,
        Action Save,
        Action<string> Reveal,
        /// <summary>Re-read every referenced kit. Called after ANY change to the reference set.</summary>
        Action Loaded,
        Action<MessageLevel, string> Report,
        /// <summary>
        /// A kit was just added or repaired: its name and the folder it was read from.
        ///
        /// <para>Reported rather than acted on HERE because the follow-up a caller wants — offering to
        /// build a technology from the kit's process data, exactly as File ▸ Import ▸ PDK does — opens
        /// dialogs of its own, and this dialog is modal. The caller collects these and acts once this
        /// one has closed, so two modals are never stacked.</para>
        /// </summary>
        Action<string, string>? KitAdded = null);

    private Context? _ctx;
    private IReadOnlyList<PdkReferenceManager.RefStatus> _rows = [];

    public ManagePdksDialog() => InitializeComponent();

    public static async Task ShowAsync(Window? owner, Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (owner is null) return;

        var dlg = new ManagePdksDialog { _ctx = context };
        dlg.Refresh(selectFirst: true);
        await dlg.ShowDialog(owner);
    }

    /// <summary>
    /// Rebuilds the list from the reference set, preserving the selected kit where it can.
    /// </summary>
    /// <param name="selectFirst">
    /// Whether to fall back to the first kit when nothing ends up selected. True only on first open,
    /// where landing on a selection is helpful. On every later rebuild it is false, because "nothing
    /// selected" is then a state the USER chose and re-selecting for them would undo it — the same
    /// reason an editor does not re-select after you click away.
    /// </param>
    private void Refresh(bool selectFirst = false)
    {
        if (_ctx is null) return;

        string? wasSelected = SelectedStatus()?.Provider;

        _rows = PdkReferenceManager.Describe(_ctx.WorkspaceRootDir, _ctx.Refs);
        RefList.ItemsSource   = _rows.Select(Describe).ToList();
        EmptyMessage.IsVisible = _rows.Count == 0;

        int index = wasSelected is null
            ? (selectFirst && _rows.Count > 0 ? 0 : -1)
            : IndexOfProvider(wasSelected);

        // A kit that was selected and is no longer listed (it was just removed) leaves nothing
        // selected rather than jumping to a neighbour — a verdict must never appear to belong to a
        // kit the user did not pick.
        RefList.SelectedIndex = index;
        UpdateDetail();
    }

    /// <summary>
    /// Clicking the empty space below the list clears the selection — the standard way to mean "none
    /// of these". Clicking an already-selected row deliberately does NOT deselect: that makes a
    /// double-click on a row toggle it off, which reads as the list losing your selection.
    /// </summary>
    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Control c && c.FindAncestorOfType<ListBoxItem>() is null)
            RefList.SelectedIndex = -1;
    }

    private int IndexOfProvider(string provider)
    {
        for (int i = 0; i < _rows.Count; i++)
            if (string.Equals(_rows[i].Provider, provider, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>Name · state · where it is. The state is a word, never colour alone.</summary>
    private static string Describe(PdkReferenceManager.RefStatus s)
    {
        string state = s.State switch
        {
            PdkReferenceManager.RefState.Missing => "Missing",
            PdkReferenceManager.RefState.Drifted => "Needs attention",
            _                                    => PdkPartInstaller.Plural(s.PartsLoaded, "part", "parts"),
        };
        string where = s.IsExternal ? "outside this workspace" : "inside this workspace";
        return $"{s.Provider}   —   {state}   ·   {where}\n{s.ResolvedPath}";
    }

    private PdkReferenceManager.RefStatus? SelectedStatus()
    {
        int i = RefList.SelectedIndex;
        return i >= 0 && i < _rows.Count ? _rows[i] : null;
    }

    private CwsPdkRef? SelectedRef()
    {
        if (_ctx is null || SelectedStatus() is not { } s) return null;
        return _ctx.Refs.FirstOrDefault(
            r => string.Equals(r.Provider, s.Provider, StringComparison.OrdinalIgnoreCase));
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // A result belongs to the kit it was produced for, so selecting another one clears it rather
        // than leaving a verdict sitting under a different kit's name.
        ResultPanel.IsVisible = false;
        UpdateDetail();
    }

    private void UpdateDetail()
    {
        var s = SelectedStatus();
        bool any = s is not null;

        RemoveButton.IsEnabled   = any;
        RevealButton.IsEnabled   = any && Directory.Exists(s!.ResolvedPath);
        ValidateButton.IsEnabled = any;

        // Said per row, because it is only true per row: a kit inside the workspace tree travels with
        // it, one outside does not. The positive case is stated too — "will this survive being shared"
        // is the question this dialog exists to answer, and silence is not an answer.
        DetailText.Text = s is null
            ? ""
            : s.Detail.Length > 0
                ? s.Detail
                : s.IsExternal
                    ? "This kit lives outside the workspace, so sharing the workspace does not carry " +
                      "it — a colleague will need their own copy and will repair the reference here."
                    : "This kit lives inside the workspace, so sharing the workspace carries it.";
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    private async void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (_ctx is null) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = "Choose the kit's folder",
            AllowMultiple = false,
        });

        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { Length: > 0 } path) return;

        var outcome = PdkReferenceManager.AddOrRepair(
            _ctx.WorkspaceRootDir, _ctx.Refs, path, out string? problem);

        if (outcome is null)
        {
            // Shown as an action RESULT, not as the row detail: the row detail belongs to whatever is
            // selected and is overwritten by the next selection change, which would take the reason
            // the add failed with it.
            ShowResult(problem ?? "That folder could not be added.", [], clean: false);
            return;
        }

        _ctx.Save();
        _ctx.Loaded();

        // Reported to Messages as well as shown here, exactly like File ▸ Import ▸ PDK: this dialog
        // is dismissed and the record of what was added has to outlive it.
        _ctx.Report(MessageLevel.Info,
            $"Manage PDKs — added '{outcome.KitName}': " +
            $"{PdkPartInstaller.Plural(outcome.Items.Count, "placeable part", "placeable parts")}, " +
            $"{PdkPartInstaller.Plural(outcome.SymbolsInstalled, "symbol", "symbols")} read, " +
            $"{PdkPartInstaller.Plural(outcome.IconsFound, "icon", "icons")}.");

        foreach (var n in outcome.Notes ?? []) _ctx.Report(MessageLevel.Info,    $"Manage PDKs — {n}");
        foreach (var d in outcome.Diagnostics) _ctx.Report(MessageLevel.Warning, $"Manage PDKs — {d}");

        // Select the new kit BEFORE showing the result: changing the selection clears the result
        // panel (a verdict belongs to the row it was produced for), so showing it first would have it
        // wiped a line later.
        Refresh();
        RefList.SelectedIndex = IndexOfProvider(outcome.KitName);

        ShowResult(
            $"Added '{outcome.KitName}' — " +
            $"{PdkPartInstaller.Plural(outcome.Items.Count, "placeable part", "placeable parts")}.",
            [.. (outcome.Notes ?? []).Concat(outcome.Diagnostics)]);

        // The kit's SECOND half. Adding a reference here and importing through File ▸ Import ▸ PDK
        // put exactly the same kit into the workspace, so they must offer exactly the same things —
        // and this door offered only the parts, leaving a user who repaired or added a kit here with
        // no technology and no indication one was available.
        _ctx.KitAdded?.Invoke(outcome.KitName, path);
    }

    private async void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (_ctx is null || SelectedStatus() is not { } s) return;

        int placed = _ctx.PlacedPartRefs.Count(
            cr => PdkKitRegistry.TryParse(cr, out string kit, out _)
               && string.Equals(kit, s.Provider, StringComparison.OrdinalIgnoreCase));

        // Warned, and reversible: nothing is deleted from any schematic. Adding the kit back
        // resolves those parts again, which is why this needs no undo of its own.
        string message = placed > 0
            ? $"Remove the reference to '{s.Provider}'?\n\n" +
              $"{PdkPartInstaller.Plural(placed, "placed part", "placed parts")} will show as " +
              $"unresolved until it is added back. Nothing is deleted from any schematic."
            : $"Remove the reference to '{s.Provider}'?";

        var choice = await new SaveChangesDialog(message, saveLabel: "Remove", dontSaveLabel: null)
            .ShowDialog<SaveChangesResult>(this);
        if (choice != SaveChangesResult.Save) return;

        PdkReferenceManager.Remove(_ctx.Refs, s.Provider, _ctx.PlacedPartRefs);
        _ctx.Save();
        _ctx.Loaded();

        _ctx.Report(placed > 0 ? MessageLevel.Warning : MessageLevel.Info,
            placed > 0
                ? $"Manage PDKs — removed '{s.Provider}'. " +
                  $"{PdkPartInstaller.Plural(placed, "placed part is", "placed parts are")} now " +
                  $"unresolved; add the kit back to resolve them again."
                : $"Manage PDKs — removed '{s.Provider}'.");

        Refresh();
    }

    private void OnRevealClick(object? sender, RoutedEventArgs e)
    {
        if (_ctx is null || SelectedStatus() is not { } s) return;
        _ctx.Reveal(s.ResolvedPath);
    }

    private void OnValidateClick(object? sender, RoutedEventArgs e)
    {
        if (_ctx is null || SelectedRef() is not { } r) return;

        var result = PdkReferenceManager.Validate(
            _ctx.WorkspaceRootDir, r, _ctx.PlacedPartRefs,
            PdkReferenceManager.LibraryRootsIn(_ctx.WorkspaceRootDir, _ctx.Refs));

        // The summary says what was CHECKED, not merely whether it passed — a bare "no problems found"
        // cannot be told apart from a check that did nothing.
        ShowResult(result.Summary, [.. result.Notes.Concat(result.Problems)], result.IsClean);

        _ctx.Report(result.IsClean ? MessageLevel.Success : MessageLevel.Warning,
                    $"Validate PDK — {result.Summary}");
        foreach (string line in result.Problems)
            _ctx.Report(MessageLevel.Warning, $"Validate PDK — {r.Provider}: {line}");
    }

    /// <summary>
    /// Shows the outcome of an action in the dialog: a headline plus whatever detail it produced,
    /// tinted only as a SECOND cue behind the words — the state is always readable without colour.
    /// </summary>
    private void ShowResult(string headline, IReadOnlyList<string> lines, bool clean = true)
    {
        ResultHeadline.Text      = headline;
        ResultHeadline.Classes.Set("problem", !clean);

        ResultLines.ItemsSource = lines.Count > 0 ? lines : null;
        ResultLines.IsVisible   = lines.Count > 0;
        ResultPanel.IsVisible   = true;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
