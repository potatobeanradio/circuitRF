// The Yield panel's Library parts group — brief vendorlib-13 R-vl13-1c.
//
// One row per placed library part whose model holds a distribution, its spreads shown read-only in the panel's own
// notation, with a tick box. Unticked, every draw inside that part's cells evaluates to its nominal: one statistics key
// (nominalparts=) lists the parts held. The draws are mismatch draws by the scope rule, so the Mismatch switch holds
// them too. What a part holds is LibraryParts' answer in src/Design; nothing here reads a model.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CircuitRF.Design.Statistics;
using CircuitRF.Ui.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Yield;

/// <summary>One placed library part with distributions in its model.</summary>
public sealed partial class LibraryPartRowViewModel : ObservableObject
{
    private readonly Action<LibraryPartRowViewModel, bool> _changed;
    private bool _syncing;

    internal LibraryPartRowViewModel(PlacedLibraryPart part, bool drawn, Action<LibraryPartRowViewModel, bool> changed)
    {
        _changed = changed;
        Part = part.Part;
        InstancesText = part.Instances.Count == 1 ? part.Instances[0] : $"{part.Instances.Count} placed";
        Spreads = [.. part.Distributions.Select(d => $"{d.Site}  {d.Spread}")];
        _syncing = true;
        IsDrawn = drawn;
        _syncing = false;
    }

    public string Part { get; }
    public string InstancesText { get; }

    /// <summary>Each distribution in the model: where it is written and its spread (<c>Cg  ± 5 % at 3σ</c>).</summary>
    public IReadOnlyList<string> Spreads { get; }

    public string SpreadsText => string.Join("   ", Spreads);

    [ObservableProperty] private bool _isDrawn;

    partial void OnIsDrawnChanged(bool value)
    {
        if (!_syncing) _changed(this, value);
    }

    internal void Sync(bool drawn)
    {
        _syncing = true;
        IsDrawn = drawn;
        _syncing = false;
    }
}

public sealed partial class YieldPanelViewModel
{
    /// <summary>The placed library parts of a schematic as extracted (brief vendorlib-13); null when it does not extract.
    /// Asked when the Kit statistics are — on focus and after a run, never on every edit.</summary>
    public Func<SchematicViewModel, IReadOnlyList<PlacedLibraryPart>?>? LibraryPartsFor { get; set; }

    /// <summary>The Library parts group: one row per placed part whose model holds a distribution.</summary>
    public ObservableCollection<LibraryPartRowViewModel> LibraryPartRows { get; } = [];

    public bool HasLibraryParts => LibraryPartRows.Count > 0;

    private IReadOnlyList<PlacedLibraryPart>? SafeLibraryParts(SchematicViewModel tuned)
    {
        try { return LibraryPartsFor?.Invoke(tuned); }
        catch { return null; }
    }

    private void RefreshLibraryParts()
    {
        var held = Settings.NominalPartList.ToHashSet(StringComparer.Ordinal);
        var parts = _tuned is null ? [] : (SafeLibraryParts(_tuned) ?? []).Where(p => p.Distributions.Count > 0).ToList();
        LibraryPartRows.Clear();
        foreach (var p in parts) LibraryPartRows.Add(new LibraryPartRowViewModel(p, !held.Contains(p.Part), SetPartDrawn));
        OnPropertyChanged(nameof(HasLibraryParts));
    }

    /// <summary>Ticks re-read from the statistics line, after an undo or another edit changed it.</summary>
    private void SyncLibraryPartTicks()
    {
        var held = Settings.NominalPartList.ToHashSet(StringComparer.Ordinal);
        foreach (var row in LibraryPartRows) row.Sync(!held.Contains(row.Part));
    }

    private void SetPartDrawn(LibraryPartRowViewModel row, bool drawn)
    {
        var list = Settings.NominalPartList.Where(p => p != row.Part).ToList();
        if (!drawn) list.Add(row.Part);
        EditSettings(s => s.NominalParts = list.Count == 0 ? null : string.Join(',', list),
                     drawn ? $"Draw {row.Part}'s own tolerances" : $"Hold {row.Part}'s own tolerances at nominal");
        SyncLibraryPartTicks();
    }
}
