using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.Layout.Assembly;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.Views.Dialogs;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Views.WBond;

/// <summary>
/// The standalone binary's shell: one plain <see cref="Window"/> around one
/// <see cref="WBondEditorView"/>, with no Dock and no workspace (wbond.md §11, R-wbe-3, R-wbe-4).
///
/// <para><b>One window per document.</b> Several <c>.wBond</c> files open as several windows rather
/// than as tabs — tabs would need the document shell this binary exists to do without, and with one
/// window per document the OS window list IS the document list.</para>
///
/// <para><b>The editor itself needed nothing from <c>WorkspaceViewModel</c>, which is the interesting
/// difference from harmonicaRF.</b> <c>WBondEditorView</c>, <c>WBondViewModel</c>, the overlay and
/// the two canvases reference the workspace shell nowhere at all — every workspace-shaped concern
/// (where a scratch layout lives, where embedded geometry is unpacked, which <c>.wasm</c> resolves)
/// was already a parameter rather than a lookup. So this shell supplies those four answers and hosts
/// the unmodified view.</para>
/// </summary>
public partial class WBondShellWindow : Window
{
    /// <summary>
    /// Assembly-rule cache for the whole process. Shared so two windows opening designs that name the
    /// same <c>.wasm</c> read it once — the same bargain <c>WorkspaceViewModel</c>'s own cache makes.
    /// </summary>
    private static readonly WasmCache RuleCache = new();

    /// <summary>
    /// This process's scratch area. One per run, under the OS temp directory: a decoded copy of what
    /// is already inside a <c>.wBond</c> is not project state, and writing it beside the user's file
    /// would leave litter they never asked for.
    /// </summary>
    private static readonly string SessionDir = Path.Combine(
        Path.GetTempPath(), "circuitRF-wbond", Guid.NewGuid().ToString("N")[..8]);

    private readonly WBondMenuViewModel _menus = new();
    private readonly DrcTool _drc = new();

    public WBondShellWindow() : this(new WBondDocument()) { }

    public WBondShellWindow(WBondDocument document)
    {
        InitializeComponent();

        MenuBar.DataContext  = _menus;
        DrcPanel.DataContext = _drc;

        WireMenus();
        Adopt(document);
    }

    /// <summary>The one document this window shows.</summary>
    public WBondDocument Document => (WBondDocument)DataContext!;

    // ── Document lifetime ─────────────────────────────────────────────────────

    /// <summary>
    /// Opens a <c>.wBond</c> into THIS window — the double-click route, and the route File ▸ Open
    /// takes. Both go through <see cref="WBondDocument.Open"/>, which is what makes M5's structural
    /// half true: circuitRF's tab and this window open one file by one code path.
    /// </summary>
    public void OpenWBond(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string scratch = Path.Combine(
                SessionDir, "embedded",
                Math.Abs(full.GetHashCode()).ToString(System.Globalization.CultureInfo.InvariantCulture));

            Adopt(WBondDocument.Open(full, scratch));
        }
        catch (Exception ex)
        {
            // WB35: report, never fail silently and never substitute. The window keeps whatever it
            // was already showing rather than being blanked by a file that could not be read.
            Editor.ShowShellStatus($"Could not open {Path.GetFileName(path)}: {ex.Message}", isWarning: true);
        }
    }

    /// <summary>Opens a <c>.wBond</c> in a NEW window (R-wbe-4) and shows it.</summary>
    public static WBondShellWindow OpenInNewWindow(string path)
    {
        var window = new WBondShellWindow();
        window.Show();
        window.OpenWBond(path);
        return window;
    }

    /// <summary>Installs a document into this window and re-establishes everything derived from it.</summary>
    private void Adopt(WBondDocument document)
    {
        DataContext = document;

        // A blank editor's layout view is where cells are dragged in as references (§6.6). Without a
        // real (if empty) layout there is nothing to drop INTO and the existing drag path silently
        // does nothing, which reads as drag-and-drop being broken rather than as there being no
        // layout yet. The directory is real because a dropped cell's CellRef resolves against it.
        document.ViewModel.EnsureReferenceLayout(
            Path.Combine(SessionDir, "reference", Guid.NewGuid().ToString("N")[..8]));

        // R-wbe-6's resolution order, minus the half that needs a workspace: the document's OWN
        // AssemblyRef, then nothing. Finding none is a normal state and the DRC panel says so.
        foreach (string diagnostic in document.ResolveAssemblyRules(null, null, RuleCache).Diagnostics)
            Editor.ShowShellStatus(diagnostic, isWarning: true);

        _drc.SetActiveLayout(document.ViewModel.ReferenceLayout);

        document.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WBondDocumentViewModel.IsDirty)) UpdateTitle();
        };

        // The editor's own Save / Save As buttons (owner, 2026-08-16) land on THIS window's picker —
        // the same method File ▸ Save already uses, never a second way to write a .wBond.
        document.SaveRequested += saveAs => _ = SaveAsync(saveAs);

        UpdateTitle();
        ReportUnresolvedReferences();
    }

    private void UpdateTitle()
    {
        string name = Document.FilePath is { } p ? Path.GetFileNameWithoutExtension(p) : "Untitled";
        Title = (Document.ViewModel.IsDirty ? "• " : "") + name + " — wBond";
    }

    // ── Closing a window closes a document, so it asks ────────────────────────

    private bool _closeConfirmed;

    /// <summary>
    /// With one window per document, closing a window IS closing a document — so an unsaved one is
    /// asked about here, exactly as circuitRF's own tab close asks.
    ///
    /// <para>The close is cancelled and re-issued rather than blocked, because the answer arrives
    /// from a modal that cannot be awaited inside the synchronous <c>OnClosing</c> — the same shape
    /// <c>WorkspaceWindow</c> already uses for its own quit prompt.</para>
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_closeConfirmed || !Document.ViewModel.IsDirty)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        _ = ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        string name = Document.FilePath is { } p ? Path.GetFileName(p) : "this design";

        var answer = await new SaveChangesDialog(
            $"Save changes to {name} before closing?",
            saveLabel:     "Save",
            dontSaveLabel: "Don't Save",
            cancelLabel:   "Cancel",
            title:         "Unsaved Changes").ShowDialog<SaveChangesResult>(this);

        if (answer == SaveChangesResult.Cancel) return;

        if (answer == SaveChangesResult.Save)
        {
            await SaveAsync(saveAs: false);
            // A cancelled save picker leaves the document dirty, and that must cancel the close too —
            // otherwise "Save" would silently behave as "Don't Save".
            if (Document.ViewModel.IsDirty) return;
        }

        _closeConfirmed = true;
        Close();
    }

    // ── R-wbe-6 — references that resolve to nothing are REPORTED, never silent ──

    /// <summary>
    /// Names the cell references this design could not resolve and offers to re-point them.
    ///
    /// <para><b>This is not a failure and is not presented as one.</b> With no workspace, the designs
    /// that open completely are one carrying embedded geometry and one carrying none; anything else
    /// resolves nothing, which WB35 says to report and offer to repair rather than refuse or
    /// substitute. The design is already open and fully editable either way.</para>
    /// </summary>
    private void ReportUnresolvedReferences()
    {
        if (Document.ViewModel.ReferenceLayout is not { } layout) return;

        var missing = WBondReferenceGeometry.Unresolved(layout.Model, layout.InstanceBaseDir);
        if (missing.Count == 0) return;

        Editor.ShowShellStatus(
            $"{missing.Count} cell reference(s) could not be resolved: {Describe(missing)} — " +
            "File ▸ Open ▸ Locate Cells… to re-point them.",
            isWarning: true);

        _ = OfferRepointAsync(missing);
    }

    private static string Describe(IReadOnlyList<string> names) =>
        names.Count <= 3
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(3)) + $" (+{names.Count - 3} more)";

    private async Task OfferRepointAsync(IReadOnlyList<string> missing)
    {
        var answer = await new SaveChangesDialog(
            $"This design references {missing.Count} cell(s) that are not inside it:\n\n" +
            Describe(missing) + "\n\n" +
            "Geometry embedded in the file opens on its own; a reference needs the cells it names. " +
            "Point wBond at the folder those cells live in, or carry on without them — the wires are " +
            "unaffected either way.",
            saveLabel:     "Locate Cells…",
            dontSaveLabel: null,
            cancelLabel:   "Not Now",
            title:         "Missing Reference Geometry").ShowDialog<SaveChangesResult>(this);

        if (answer != SaveChangesResult.Save) return;

        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Locate reference cells",
            AllowMultiple = false,
        });

        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } folder) return;
        if (Document.ViewModel.ReferenceLayout is not { } layout) return;

        int moved = WBondReferenceGeometry.Repoint(layout.Model, layout.InstanceBaseDir!, folder);
        var still = WBondReferenceGeometry.Unresolved(layout.Model, layout.InstanceBaseDir);

        Editor.ShowShellStatus(
            still.Count == 0
                ? $"Re-pointed {moved} reference(s) — all reference geometry now resolves."
                : $"Re-pointed {moved} reference(s); {still.Count} still unresolved: {Describe(still)}.",
            isWarning: still.Count > 0);
    }

    // ── Menu wiring ───────────────────────────────────────────────────────────

    private void WireMenus()
    {
        // File. New and Open produce WINDOWS rather than tabs — R-wbe-4, and the reason a standalone
        // wBond can hold several designs at once with no document shell.
        _menus.NewDocumentHook    = () => new WBondShellWindow().Show();
        _menus.OpenDocumentHook   = () => _ = OpenAsync();
        _menus.SaveDocumentHook   = () => _ = SaveAsync(saveAs: false);
        _menus.SaveDocumentAsHook = () => _ = SaveAsync(saveAs: true);
        _menus.CloseWindowHook    = Close;

        _menus.ImportWireTableHook  = () => _ = ImportWireTableAsync();
        _menus.ImportWiresDxfHook   = () => _ = Editor.ImportWiresAsync();
        _menus.ExportDxfHook        = () => _ = Editor.ExportDxfAsync();
        _menus.ExportTouchstoneHook = () => _ = Editor.ExportTouchstoneAsync();

        // Edit. Every one of these already exists on the editor view — the standalone binds the same
        // methods the docked tab's own keyboard gestures do, never a second implementation.
        _menus.UndoHook        = () => Editor.UndoFromShell();
        _menus.RedoHook        = () => Editor.RedoFromShell();
        _menus.CopyHook        = () => _ = Editor.CopyAsync();
        _menus.CopyGraphicHook = () => _ = Editor.CopyGraphicAsync();
        _menus.PasteHook       = () => _ = Editor.PasteAsync();
        _menus.PreferencesHook = () => _ = ShowPreferencesAsync();

        _menus.SelectAllWiresHook   = () => Editor.SelectAllIncludingWires();
        _menus.CheckDesignRulesHook = RunAssemblyCheck;
        _menus.CompareDistributedModelHook = () => _ = Editor.CompareDistributedModelAsync();
        _menus.HelpHook             = () => DocLauncher.Open("index.html");
    }

    private async Task OpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open wBond",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("wBond design") { Patterns = ["*.wBond", "*.wbond"] }],
        });

        bool first = true;
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is not { } path) continue;

            // The first file replaces THIS window's untouched document only when there is nothing to
            // lose; everything else opens its own window, so opening several files never silently
            // discards one of them.
            if (first && Document.IsScratch && !Document.IsDirty) OpenWBond(path);
            else OpenInNewWindow(path);

            first = false;
        }
    }

    private async Task SaveAsync(bool saveAs)
    {
        string? target = saveAs ? null : Document.FilePath;

        if (target is null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save wBond",
                SuggestedFileName = (Document.FilePath is { } p
                    ? Path.GetFileNameWithoutExtension(p)
                    : "wirebonds") + ".wBond",
                DefaultExtension = "wBond",
                FileTypeChoices = [new FilePickerFileType("wBond design") { Patterns = ["*.wBond"] }],
            });

            if (file?.TryGetLocalPath() is not { } chosen) return;
            target = chosen;
        }

        bool embed = false;

        // Asked only when the layout actually HOLDS something (owner, 2026-08-16) — a design with no
        // reference geometry has nothing on either side of the choice. Same rule, same helper, as the
        // workspace's own SaveWBondDoc.
        if (Document.ViewModel.ReferenceLayout is { } layout &&
            WBondGeometryEmbedding.HasGeometryToEmbed(layout.Model))
        {
            // WB33 — what a save costs is stated BEFORE it happens. The same plan dialog circuitRF
            // shows, for the same reason: a file that quietly lost parametricity on a vendor PCell is
            // discovered by whoever receives it.
            var plan = WBondGeometryEmbedding.Analyze(layout.Model, layout.InstanceBaseDir);
            var choice = await WBondSaveGeometryDialog.ShowAsync(this, plan);

            if (choice == WBondSaveGeometryDialog.Choice.Cancel) return;
            embed = choice == WBondSaveGeometryDialog.Choice.Embed;
        }

        try
        {
            Document.Save(target, embed);
            UpdateTitle();
            Editor.ShowShellStatus($"Saved {Path.GetFileName(target)}");
        }
        catch (Exception ex)
        {
            Editor.ShowShellStatus($"Could not save {Path.GetFileName(target)}: {ex.Message}", isWarning: true);
        }
    }

    /// <summary>
    /// WB36 / §9.3 — a packaging flow's own bond list becomes a NEW document, in its own window.
    /// Merging into whatever happens to be open would need rules the table does not state.
    /// </summary>
    private async Task ImportWireTableAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Wirebond Table",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Wirebond table (CSV)") { Patterns = ["*.csv"] }],
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

        try
        {
            var design = WireTableCsv.ReadFile(path);
            var window = new WBondShellWindow(new WBondDocument(new WBondViewModel(design)));
            window.Show();
            window.Editor.ShowShellStatus(
                $"Imported {design.WireCount} wire(s) in {design.Arrays.Count} array(s) from {Path.GetFileName(path)}.");
        }
        catch (Exception ex)
        {
            // The reader names the offending line; passing that through is the whole value of it.
            Editor.ShowShellStatus($"Could not import {Path.GetFileName(path)}: {ex.Message}", isWarning: true);
        }
    }

    private async Task ShowPreferencesAsync()
    {
        // The SAME settings window circuitRF opens, with no workspace directory — its General tab is
        // where WBondDefaults (points, diameter, material) and the saved colour theme both live, so
        // there is nothing standalone-specific to build.
        await new SettingsView(workspaceDirPath: null).ShowDialog(this);
    }

    /// <summary>
    /// Runs the assembly check and reveals the panel. The check itself is the LAYOUT's own DRC run —
    /// §8.1's "a new rule vocabulary over an existing DRC, not a second DRC" — so nothing here
    /// evaluates a rule.
    /// </summary>
    private void RunAssemblyCheck()
    {
        if (Document.ViewModel.ReferenceLayout is not { } layout) return;

        _drc.SetActiveLayout(layout);
        DrcHost.IsVisible = true;
        layout.RunDrc();
    }
}
