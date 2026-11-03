using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using CircuitRF.Design.VendorLibraries;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// The management surface for a workspace's library references — PDKs and vendor part libraries
/// (brief vendorlib-1 R-vl1-4b): add, remove, reveal, validate. Shown as <b>Manage Libraries…</b>;
/// the type keeps its older name, which no reader sees.
///
/// <para><b>Why it is required rather than nice to have.</b> A kit's parts used to appear in the
/// Project Tree as ordinary cell folders. They are held in memory now, so they appear nowhere — and
/// without this dialog a workspace's dependency on a kit would be invisible until something failed to
/// resolve, with nowhere to go and repair it.</para>
///
/// <para>Every decision here lives in <see cref="PdkReferenceManager"/> and
/// <see cref="VendorLibraryRefs"/>, which are framework-free and tested. This is presentation plus the two things that genuinely need a window: a folder picker and
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
        Action<string, string>? KitAdded = null,
        /// <summary>The workspace's vendor-library references. Saved by <see cref="Save"/> with the kits.</summary>
        List<CwsVendorLibraryRef>? VendorRefs = null,
        /// <summary>The File/Name of every SpiceModel placed in an OPEN schematic.</summary>
        IReadOnlyList<(string File, string Name)>? PlacedVendorParts = null,
        /// <summary>
        /// Adds a vendor library from a picked folder into <see cref="VendorRefs"/> — the same confirm
        /// step and report File ▸ Add Vendor Library… uses. Null when the user cancelled.
        /// </summary>
        Func<Window, string, VendorLibraryBuild, Task<string?>>? AddVendor = null);

    /// <summary>One row: a PDK or a vendor library.</summary>
    internal sealed class LibraryRow
    {
        public PdkReferenceManager.RefStatus? Pdk    { get; init; }
        public VendorLibraryRefs.Status?      Vendor { get; init; }

        public bool    IsVendor     => Vendor is not null;
        public string  Name         => Vendor?.Name ?? Pdk!.Provider;
        public string  Kind         => IsVendor ? "Vendor library" : "PDK";
        public string? ResolvedPath => IsVendor ? Vendor!.ResolvedPath : Pdk!.ResolvedPath;
        public bool    IsExternal   => IsVendor ? Vendor!.IsExternal : Pdk!.IsExternal;

        /// <summary>The state in WORDS — a parts count when healthy, the problem otherwise.</summary>
        public string State => IsVendor
            ? !Vendor!.Exists
                ? "Missing"
                : Vendor.Parts is { } n
                    ? PdkPartInstaller.Plural(n, "part", "parts")
                    : "Indexing…"
            : Pdk!.State switch
            {
                PdkReferenceManager.RefState.Missing => "Missing",
                PdkReferenceManager.RefState.Drifted => "Needs attention",
                _                                    => PdkPartInstaller.Plural(Pdk.PartsLoaded, "part", "parts"),
            };

        public string Line1 => $"{Name}   ·   {(IsExternal ? "outside this workspace" : "inside this workspace")}";
        public string Line2 => ResolvedPath ?? Vendor?.StoredPath ?? "";

        /// <summary>Whether File ▸ New Workspace references this library — the per-user list.</summary>
        public bool UseInNewWorkspaces
        {
            get => IsVendor && (UserVendorLibraries.UseInNewWorkspaces(Name) ?? false);
            set
            {
                if (!IsVendor) return;
                if (value && UserVendorLibraries.UseInNewWorkspaces(Name) is null && ResolvedPath is { } p)
                    UserVendorLibraries.Remember(Name, p);
                UserVendorLibraries.SetUseInNewWorkspaces(Name, value);
            }
        }
    }

    private Context? _ctx;
    private IReadOnlyList<LibraryRow> _rows = [];

    /// <summary>Set only by <see cref="Body"/>, for a documentation figure. See its own note.</summary>
    private IReadOnlyList<PdkReferenceManager.RefStatus>? _fixedRows;

    public ManagePdksDialog()
    {
        InitializeComponent();
        RefList.ItemTemplate = RowTemplate();

        // A vendor library's index is built in the background; the row says "Indexing…" until it
        // lands, and is redrawn then rather than polled.
        VendorLibraryIndex.Built += OnIndexBuilt;
        Closed += (_, _) => VendorLibraryIndex.Built -= OnIndexBuilt;
    }

    private void OnIndexBuilt(string root) => Dispatcher.UIThread.Post(() => Refresh());

    /// <summary>Kind · name and path · state/parts · (vendor rows) Use in new workspaces.</summary>
    private static FuncDataTemplate<LibraryRow> RowTemplate() => new((row, _) =>
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*,Auto,Auto") };
        grid.Children.Add(new TextBlock
        {
            Text = row.Kind, FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Top,
        });
        var name = new StackPanel { Spacing = 1 };
        name.Children.Add(new TextBlock { Text = row.Line1, FontSize = 12, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
        name.Children.Add(new TextBlock { Text = row.Line2, FontSize = 10, Opacity = 0.65, TextTrimming = Avalonia.Media.TextTrimming.PrefixCharacterEllipsis });
        // brief vendorlib-15 §5c: a signed library's row says who signed it, and asks about a key not yet trusted.
        if (row.IsVendor && row.ResolvedPath is { } root && VendorLibraryIndex.Latest(root)?.Signature is { } signature
            && Controls.LibrarySignatureLine.For(signature) is { } signed)
            name.Children.Add(signed);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var state = new TextBlock { Text = row.State, FontSize = 11, Margin = new Avalonia.Thickness(10, 0, 0, 0) };
        Grid.SetColumn(state, 2);
        grid.Children.Add(state);
        if (row.IsVendor)
        {
            var use = new CheckBox
            {
                Content   = "New workspaces",
                FontSize  = 11,
                IsChecked = row.UseInNewWorkspaces,
                Margin    = new Avalonia.Thickness(12, -4, 0, 0),
            };
            ToolTip.SetTip(use, "Reference this library from every new workspace you create.");
            use.IsCheckedChanged += (_, _) => row.UseInNewWorkspaces = use.IsChecked == true;
            Grid.SetColumn(use, 3);
            grid.Children.Add(use);
        }
        return grid;
    });

    /// <summary>
    /// Build the dialog against <paramref name="context"/> and hand back its CONTENT, populated.
    /// </summary>
    /// <remarks>
    /// For the user-docs factory. A Window cannot be hosted inside another Window, so the figure
    /// takes the content out — the same lift the Setup Analyses figure makes, and for the same
    /// reason: a fixture that rebuilt this layout would be photographing a second implementation that
    /// drifts from the real one the moment either changes.
    /// </remarks>
    /// <param name="rows">
    /// Pre-computed reference statuses, for a figure. Supplying them skips
    /// <see cref="PdkReferenceManager.Describe"/> — which reads the FILESYSTEM, and would put the
    /// generating machine's temp path in a committed figure and change it on every run. Everything
    /// downstream (the row formatting, the detail note, the buttons, the layout) is still the
    /// dialog's own. Null takes the ordinary path.
    /// </param>
    internal static Control Body(Context context,
                                 IReadOnlyList<PdkReferenceManager.RefStatus>? rows = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var dlg = new ManagePdksDialog { _ctx = context, _fixedRows = rows };
        dlg.Refresh(selectFirst: true);
        var body = (Control)dlg.Content!;
        dlg.Content = null;
        return body;
    }

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

        var was = SelectedStatus();

        var pdkRows = (_fixedRows ?? PdkReferenceManager.Describe(_ctx.WorkspaceRootDir, _ctx.Refs))
            .Select(p => new LibraryRow { Pdk = p });
        var vendorRows = VendorLibraryRefs.List(_ctx.WorkspaceRootDir, _ctx.VendorRefs ?? [])
            .Select(v => new LibraryRow { Vendor = v });
        _rows = [.. pdkRows, .. vendorRows];

        // A library with no finished index yet gets one started; its row is redrawn when it lands.
        foreach (var r in _rows.Where(r => r.IsVendor && r.Vendor!.Exists && r.Vendor.Parts is null))
            _ = VendorLibraryIndex.StartBuild(r.ResolvedPath!);

        RefList.ItemsSource   = _rows;
        EmptyMessage.IsVisible = _rows.Count == 0;

        int index = was is null
            ? (selectFirst && _rows.Count > 0 ? 0 : -1)
            : IndexOfProvider(was.Name, was.IsVendor);

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

    private int IndexOfProvider(string name, bool vendor = false)
    {
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].IsVendor == vendor
                && string.Equals(_rows[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private LibraryRow? SelectedStatus()
    {
        int i = RefList.SelectedIndex;
        return i >= 0 && i < _rows.Count ? _rows[i] : null;
    }

    private CwsPdkRef? SelectedRef()
    {
        if (_ctx is null || SelectedStatus() is not { IsVendor: false } s) return null;
        return _ctx.Refs.FirstOrDefault(
            r => string.Equals(r.Provider, s.Name, StringComparison.OrdinalIgnoreCase));
    }

    private CwsVendorLibraryRef? SelectedVendorRef()
    {
        if (_ctx?.VendorRefs is null || SelectedStatus() is not { IsVendor: true } s) return null;
        return _ctx.VendorRefs.FirstOrDefault(
            r => string.Equals(r.Name, s.Name, StringComparison.OrdinalIgnoreCase));
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
        RevealButton.IsEnabled   = any && s!.ResolvedPath is { } p && Directory.Exists(p);
        ValidateButton.IsEnabled = any;

        // Said per row, because it is only true per row: a kit inside the workspace tree travels with
        // it, one outside does not. The positive case is stated too — "will this survive being shared"
        // is the question this dialog exists to answer, and silence is not an answer.
        DetailText.Text = s is null
            ? ""
            : s.IsVendor
                ? VendorDetail(s)
            : s.Pdk!.Detail.Length > 0
                ? s.Pdk.Detail
                : s.IsExternal
                    ? "This kit lives outside the workspace, so sharing the workspace does not carry " +
                      "it — a colleague will need their own copy and will repair the reference here."
                    : "This kit lives inside the workspace, so sharing the workspace carries it.";
    }

    /// <summary>The detail line for a vendor library: what is wrong, or whether sharing carries it.</summary>
    private static string VendorDetail(LibraryRow s)
    {
        if (!s.Vendor!.Exists)
            return "The library folder is not there. Add the folder again under the same name to repair the " +
                   "reference — every placed part resolves again — or remove it.";
        string share = s.IsExternal
            ? "This library lives outside the workspace, so sharing the workspace does not carry it — a " +
              "colleague will need their own copy and will repair the reference here."
            : "This library lives inside the workspace, so sharing the workspace carries it.";
        return VendorLibraryIndex.Latest(s.ResolvedPath!)?.UnreadableSentence is { } unreadable
            ? $"{unreadable} {share}"
            : share;
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    private async void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (_ctx is null) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = "Choose the kit's or the vendor library's folder",
            AllowMultiple = false,
        });

        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { Length: > 0 } path) return;

        // Classified by CONTENT, never asked (R-vl1-4b): readable .subckt parts and no kit structure
        // is a vendor library. Off the UI thread — a library is indexed to find out.
        if (_ctx.AddVendor is not null)
        {
            ShowBusy($"Reading '{Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}'…");
            (VendorLibraryRefs.FolderKind Kind, VendorLibraryBuild? Build) kind;
            try   { kind = await Task.Run(() => VendorLibraryRefs.Classify(path)); }
            finally { HideBusy(); }

            if (kind.Kind == VendorLibraryRefs.FolderKind.UnreadableLibrary)
            {
                ShowResult(kind.Build!.Refusal ?? "That folder holds no readable part.", [], clean: false);
                return;
            }
            if (kind.Kind == VendorLibraryRefs.FolderKind.VendorLibrary)
            {
                ResultPanel.IsVisible = false;
                if (await _ctx.AddVendor(this, path, kind.Build!) is not { } added) return;

                _ctx.Save();
                Refresh();
                RefList.SelectedIndex = IndexOfProvider(added, vendor: true);
                ShowResult($"Added '{added}' — {kind.Build!.SummaryLines()[0]}", [.. kind.Build.SummaryLines().Skip(1)]);
                return;
            }
        }

        // Off the UI thread: adding a kit READS it, which on a real one is a few hundred
        // milliseconds of file enumeration and netlist parsing, and this dialog is what the user is
        // looking at while it happens. Awaited, so nothing else here touches _ctx.Refs meanwhile.
        ShowBusy($"Reading '{Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}'…");
        (PdkPartInstaller.InstallOutcome? outcome, string? problem) result;
        try
        {
            result = await Task.Run(() =>
            {
                var o = PdkReferenceManager.AddOrRepair(
                    _ctx.WorkspaceRootDir, _ctx.Refs, path, out string? p);
                return (o, p);
            });
        }
        finally { HideBusy(); }

        var (outcome, problem) = result;

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
            $"Manage Libraries — added '{outcome.KitName}': " +
            $"{PdkPartInstaller.Plural(outcome.Items.Count, "placeable part", "placeable parts")}, " +
            $"{PdkPartInstaller.Plural(outcome.SymbolsInstalled, "symbol", "symbols")} read, " +
            $"{PdkPartInstaller.Plural(outcome.IconsFound, "icon", "icons")}.");

        foreach (var n in outcome.Notes ?? []) _ctx.Report(MessageLevel.Info,    $"Manage Libraries — {n}");
        foreach (var d in outcome.Diagnostics) _ctx.Report(MessageLevel.Warning, $"Manage Libraries — {d}");

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
        if (_ctx is null || SelectedStatus() is not { } row) return;
        if (row.IsVendor) { await RemoveVendorAsync(row); return; }
        var s = row.Pdk!;

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

        PdkReferenceManager.Remove(_ctx.WorkspaceRootDir, _ctx.Refs, s.Provider, _ctx.PlacedPartRefs);
        _ctx.Save();
        _ctx.Loaded();

        _ctx.Report(placed > 0 ? MessageLevel.Warning : MessageLevel.Info,
            placed > 0
                ? $"Manage Libraries — removed '{s.Provider}'. " +
                  $"{PdkPartInstaller.Plural(placed, "placed part is", "placed parts are")} now " +
                  $"unresolved; add the kit back to resolve them again."
                : $"Manage Libraries — removed '{s.Provider}'.");

        Refresh();
    }

    /// <summary>Remove for a vendor library: warned with the count of placed parts in OPEN schematics.</summary>
    private async Task RemoveVendorAsync(LibraryRow row)
    {
        if (_ctx?.VendorRefs is null) return;
        var files  = (_ctx.PlacedVendorParts ?? []).Select(p => p.File).ToList();
        int placed = files.Count(f => VendorLibraryRefs.TryParse(f, out string n, out _)
                                   && string.Equals(n, row.Name, StringComparison.OrdinalIgnoreCase));

        string message = placed > 0
            ? $"Remove the reference to '{row.Name}'?\n\n" +
              $"{PdkPartInstaller.Plural(placed, "placed part", "placed parts")} will show as " +
              $"unresolved until it is added back. Nothing is deleted from any schematic."
            : $"Remove the reference to '{row.Name}'?";

        var choice = await new SaveChangesDialog(message, saveLabel: "Remove", dontSaveLabel: null)
            .ShowDialog<SaveChangesResult>(this);
        if (choice != SaveChangesResult.Save) return;

        VendorLibraryRefs.Remove(_ctx.VendorRefs, row.Name, files);
        _ctx.Save();

        _ctx.Report(placed > 0 ? MessageLevel.Warning : MessageLevel.Info,
            placed > 0
                ? $"Manage Libraries — removed '{row.Name}'. " +
                  $"{PdkPartInstaller.Plural(placed, "placed part is", "placed parts are")} now " +
                  $"unresolved; add the library back to resolve them again."
                : $"Manage Libraries — removed '{row.Name}'.");

        Refresh();
    }

    private void OnRevealClick(object? sender, RoutedEventArgs e)
    {
        if (_ctx is null || SelectedStatus()?.ResolvedPath is not { } path) return;
        _ctx.Reveal(path);
    }

    private async void OnValidateClick(object? sender, RoutedEventArgs e)
    {
        if (_ctx is null) return;
        if (SelectedVendorRef() is { } v) { await ValidateVendorAsync(v); return; }
        if (SelectedRef() is not { } r) return;

        // Same reason as Add: validating re-reads the whole kit from disk. The read is a pure
        // function of what is on disk, so it belongs on a worker thread; everything below, which
        // writes into this dialog, is back on the UI thread after the await.
        var roots  = PdkReferenceManager.LibraryRootsIn(_ctx.WorkspaceRootDir, _ctx.Refs);
        var placed = _ctx.PlacedPartRefs;
        var root   = _ctx.WorkspaceRootDir;

        ShowBusy($"Validating '{r.Provider}'…");
        PdkReferenceManager.ValidationResult result;
        try   { result = await Task.Run(() => PdkReferenceManager.Validate(root, r, placed, roots)); }
        finally { HideBusy(); }

        // The summary says what was CHECKED, not merely whether it passed — a bare "no problems found"
        // cannot be told apart from a check that did nothing.
        ShowResult(result.Summary, [.. result.Notes.Concat(result.Problems)], result.IsClean);

        _ctx.Report(result.IsClean ? MessageLevel.Success : MessageLevel.Warning,
                    $"Validate PDK — {result.Summary}");
        foreach (string line in result.Problems)
            _ctx.Report(MessageLevel.Warning, $"Validate PDK — {r.Provider}: {line}");
    }

    private async Task ValidateVendorAsync(CwsVendorLibraryRef v)
    {
        var placed = _ctx!.PlacedVendorParts ?? [];
        var root   = _ctx.WorkspaceRootDir;

        ShowBusy($"Validating '{v.Name}'…");
        VendorLibraryRefs.ValidationResult result;
        try   { result = await Task.Run(() => VendorLibraryRefs.Validate(root, v, placed)); }
        finally { HideBusy(); }

        ShowResult(result.Summary, [.. result.Notes.Concat(result.Problems)], result.IsClean);
        _ctx.Report(result.IsClean ? MessageLevel.Success : MessageLevel.Warning,
                    $"Validate library — {result.Summary}");
        foreach (string line in result.Problems)
            _ctx.Report(MessageLevel.Warning, $"Validate library — {v.Name}: {line}");
        Refresh();
    }

    /// <summary>
    /// Says an action is running, in the panel its result will land in. Reuses that panel rather than
    /// adding a second surface, so the row the user is already watching becomes the answer.
    /// </summary>
    private void ShowBusy(string headline)
    {
        ResultHeadline.Text = headline;
        ResultHeadline.Classes.Set("problem", false);
        ResultLines.IsVisible = false;
        ResultLines.ItemsSource = null;
        BusyBar.IsVisible = true;
        ResultPanel.IsVisible = true;
    }

    private void HideBusy() => BusyBar.IsVisible = false;

    /// <summary>
    /// Shows the outcome of an action in the dialog: a headline plus whatever detail it produced,
    /// tinted only as a SECOND cue behind the words — the state is always readable without colour.
    /// </summary>
    private void ShowResult(string headline, IReadOnlyList<string> lines, bool clean = true)
    {
        BusyBar.IsVisible        = false;
        ResultHeadline.Text      = headline;
        ResultHeadline.Classes.Set("problem", !clean);

        ResultLines.ItemsSource = lines.Count > 0 ? lines : null;
        ResultLines.IsVisible   = lines.Count > 0;
        ResultPanel.IsVisible   = true;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
