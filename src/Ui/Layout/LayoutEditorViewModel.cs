using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// ViewModel for the Layout Editor. Deliberately thin in L0b — it grows enormously in L1 once
/// drawing tools, selection, and undo land. For now it owns the metadata bar and the save
/// commands, mirroring <c>SymbolEditorViewModel</c>'s save-command shape exactly.
/// </summary>
public sealed partial class LayoutEditorViewModel : ObservableObject
{
    /// <summary>The L0a container this document edits.</summary>
    public LayoutView Model { get; }

    [ObservableProperty] private bool _isDirty;

    /// <summary>
    /// Absolute on-disk path of the .clay file, or null for a not-yet-saved (scratch) document.
    /// Mirrors <c>SymbolEditorViewModel.CurrentSymbolPath</c> — the document reflects this.
    /// </summary>
    [ObservableProperty] private string? _currentLayoutPath;

    [ObservableProperty] private LayoutUnit _displayUnit;
    [ObservableProperty] private long _snapDbu;

    // ── Technology (L0c) ───────────────────────────────────────────────────────

    /// <summary>The resolved technology, or null when unresolved (missing/corrupt/no default) —
    /// the layout still opens and edits either way (§2.4 "never block on it").</summary>
    [ObservableProperty] private Technology? _technology;

    /// <summary>Absolute path of the .ctech <see cref="Technology"/> was resolved from, or null.
    /// Lets the workspace know which open documents to refresh when that file changes.</summary>
    internal string? ResolvedTechPath { get; private set; }

    public string TechNameText => Technology?.Name ?? "No technology";

    public string LayerCountText => Technology is null ? "fallback colors" : $"{Technology.Layers.Count} layers";

    /// <summary>Combined metadata-bar readout, e.g. "PCB 2-Layer · 8 layers" or
    /// "No technology · fallback colors".</summary>
    public string TechSummaryText => $"{TechNameText} · {LayerCountText}";

    partial void OnTechnologyChanged(Technology? value)
    {
        OnPropertyChanged(nameof(TechNameText));
        OnPropertyChanged(nameof(LayerCountText));
        OnPropertyChanged(nameof(TechSummaryText));
    }

    /// <summary>Applies a resolution from <see cref="TechnologyResolver"/> — called by the workspace
    /// after New Layout, after opening a .clay, and whenever the live-refresh seam fires. Does NOT
    /// touch DisplayUnit/SnapDbu: those are the document's own state once open, and silently
    /// re-seeding them from a changed technology would discard a user's choice.</summary>
    internal void ApplyTechResolution(TechResolution resolution)
    {
        ResolvedTechPath = resolution.ResolvedPath;
        Technology        = resolution.Tech;
    }

    // ── Metadata bar (read-only, derived) ─────────────────────────────────────

    public string ResolutionText => $"1 DBU = {LayoutUnits.Format(1, LayoutUnit.Nm, Model.DbuPerMicron)} nm";

    public string SnapText => $"{LayoutUnits.Format(SnapDbu, DisplayUnit, Model.DbuPerMicron)} {UnitSuffix(DisplayUnit)}";

    public string ShapeCountText => Model.Shapes.Count.ToString();

    public string InstanceCountText => Model.Instances.Count.ToString();

    /// <summary>Bbox of all shapes, unioned, formatted in the current display unit. "—" when empty.</summary>
    public string ExtentText
    {
        get
        {
            var bb = Bbox.Empty;
            foreach (var shape in Model.Shapes)
                bb = bb.Union(LayoutGeometry.BboxOf(shape));
            if (bb.IsEmpty) return "—";

            var w = LayoutUnits.Format(bb.MaxX - bb.MinX, DisplayUnit, Model.DbuPerMicron);
            var h = LayoutUnits.Format(bb.MaxY - bb.MinY, DisplayUnit, Model.DbuPerMicron);
            return $"{w} × {h} {UnitSuffix(DisplayUnit)}";
        }
    }

    /// <summary>ComboBox item source for the display-unit picker.</summary>
    public static IReadOnlyList<LayoutUnit> AllUnits { get; } = Enum.GetValues<LayoutUnit>();

    private static string UnitSuffix(LayoutUnit unit) => LayoutUnits.Suffix(unit);

    // ── Display unit / snap grid — document preferences, not geometry (§1.3/§1.5) ────
    // They dirty the document (persisted in .clay) but never touch an undo stack: a unit
    // change "needs no undo entry beyond a view-preference change", and a snap change never
    // touches existing geometry.

    partial void OnDisplayUnitChanged(LayoutUnit value)
    {
        Model.DisplayUnit = value;
        IsDirty = true;
        OnPropertyChanged(nameof(SnapText));
        OnPropertyChanged(nameof(ExtentText));
    }

    partial void OnSnapDbuChanged(long value)
    {
        Model.SnapDbu = value;
        IsDirty = true;
        OnPropertyChanged(nameof(SnapText));
    }

    // ── Construction ───────────────────────────────────────────────────────────

    public LayoutEditorViewModel(LayoutView model, string? currentLayoutPath = null)
    {
        Model = model;

        // Seed backing fields directly — bypassing the property setters so construction
        // never marks the document dirty or double-writes the model it was built from.
        _displayUnit       = model.DisplayUnit;
        _snapDbu           = model.SnapDbu;
        _currentLayoutPath = currentLayoutPath;

        SaveLayoutCommand   = new AsyncRelayCommand<Window?>(SaveLayoutAsync);
        SaveLayoutAsCommand = new AsyncRelayCommand<Window?>(SaveLayoutAsAsync);
    }

    // ── Save / load ────────────────────────────────────────────────────────────

    public IAsyncRelayCommand<Window?> SaveLayoutCommand   { get; }
    public IAsyncRelayCommand<Window?> SaveLayoutAsCommand { get; }

    /// <summary>Fired after each successful save with the absolute path of the saved .clay file.</summary>
    public event Action<string>? LayoutSaved;

    /// <summary>Raised when a save fails (e.g. a read-only / unwritable location). The workspace
    /// routes it to the Messages pane. A failed save must surface an error, never crash the app.</summary>
    public event Action<string>? SaveError;

    private async Task SaveLayoutAsync(Window? owner)
    {
        if (CurrentLayoutPath is not null)
            PerformSave(CurrentLayoutPath);
        else
            await SaveLayoutAsAsync(owner);
    }

    private async Task SaveLayoutAsAsync(Window? owner)
    {
        if (owner is null) return;

        IStorageFolder? startFolder = null;
        if (CurrentLayoutPath is { Length: > 0 } p)
        {
            string? dir = Path.GetDirectoryName(p);
            if (dir is not null)
                try { startFolder = await owner.StorageProvider.TryGetFolderFromPathAsync(dir); }
                catch { }
        }

        var result = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title                  = "Save Layout",
            DefaultExtension       = "clay",
            SuggestedFileName      = Path.GetFileNameWithoutExtension(CurrentLayoutPath ?? "layout"),
            SuggestedStartLocation = startFolder,
            FileTypeChoices        =
            [
                new FilePickerFileType("circuitRF Layout") { Patterns = ["*.clay"] },
            ],
        });
        if (result is null) return;
        PerformSave(result.Path.LocalPath);
    }

    internal void PerformSave(string path)   // internal for a future save-error regression test
    {
        try
        {
            LayoutPersistence.SaveToFile(path, Model);
        }
        catch (Exception ex)
        {
            // Do NOT mark the document saved or raise LayoutSaved — the file was not written.
            SaveError?.Invoke($"Couldn't save layout to '{path}': {ex.Message}");
            return;
        }
        CurrentLayoutPath = path;
        IsDirty = false;
        LayoutSaved?.Invoke(path);
    }
}
