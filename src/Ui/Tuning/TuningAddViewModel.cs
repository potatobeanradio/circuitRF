// ================================================================
//  TuningAddViewModel.cs  —  the Add… search popup (brief-tuneopt-4 R-to4-3.1)
//
//  Lists the catalog: key, location, current value, read-only mark.
//  Typing filters live; "Include sub-cells" widens the list past the
//  top level, as the Instances panel's check does. Enter adds the
//  selected rows — or the first one shown when none is selected — in
//  one undo step and closes; a double-click adds that row with any
//  selected and stays open, so several can be added in a row. The ×
//  beside the search box, or a click away, closes it.
//
//  The Optimizer panel's ＋ is the same popup over the same catalog;
//  only the flag it sets differs (ITunableAddHost).
//
//  An SnP block's S-parameter entries (brief-sens-2; the Yield panel
//  only — Tuning and the Optimizer do not offer them) are one collapsed
//  row per instance — a 12-port file offers 156 keys and must not flood
//  the list. A double-click on that row opens or closes it; adding it
//  (Enter, or selected with others) adds every entry it holds. A search
//  that matches entries shows them open.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Optimization;

namespace CircuitRF.Ui.Tuning;

/// <summary>What the Add… popup lists from and what adding sets: <c>tune</c> for the Tuning panel,
/// <c>opt</c> for the Optimizer's (brief-tuneopt-10 R-to10-2).</summary>
public interface ITunableAddHost
{
    TunableCatalog? Catalog { get; }

    /// <summary>The key already carries this panel's flag, so the popup does not offer it.</summary>
    bool IsActive(string key);

    /// <summary>Sets this panel's flag on every key, as one undo step.</summary>
    void Activate(IReadOnlyCollection<string> keys);

    /// <summary>Whether this panel offers <paramref name="t"/> at all — the Yield panel never offers a vendor part,
    /// which is chosen and never toleranced (brief vendorlib-4 R-vl4-3d).</summary>
    // A vendor part's value is drawn, never tuned or optimized (brief vendorlib-5 R-vl5-3a): only Yield offers it. So is an
    // SnP block's S-parameter offset, which is one number at every frequency (brief-sens-2).
    bool Offers(Tunable t) => !t.IsPartValue && t.SnpEntry is null;
}

public sealed partial class TuningAddViewModel(ITunableAddHost panel) : ObservableObject
{
    [ObservableProperty] private bool   _isOpen;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool   _includeSubCells;

    /// <summary>The tunables shown, not yet tuned, matching the search.</summary>
    public ObservableCollection<TuningAddRow> Results { get; } = [];

    partial void OnSearchTextChanged(string value)       => Refresh();
    partial void OnIncludeSubCellsChanged(bool value)    => Refresh();
    partial void OnIsOpenChanged(bool value)             { if (value) Refresh(); }

    /// <summary>The SnP instances whose entries are shown open (brief-sens-2).</summary>
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    /// <summary>Rebuilds <see cref="Results"/>. Cheap: the catalog is already in hand.</summary>
    public void Refresh()
    {
        Results.Clear();
        if (panel.Catalog is not { } catalog) return;

        string q = SearchText.Trim();
        var groups = new Dictionary<string, TuningAddRow>(StringComparer.Ordinal);
        foreach (var t in catalog.Tunables)
        {
            if (t.Cell is not null && !IncludeSubCells) continue;
            if (panel.IsActive(t.Key) || !panel.Offers(t)) continue;
            if (q.Length > 0 && !Matches(t, q)) continue;
            if (t.SnpEntry is { } se)
            {
                // One collapsed row per SnP instance; its entries follow it only when it is open.
                if (!groups.TryGetValue(se.Instance, out var group))
                {
                    group = new TuningAddRow(se.Instance, t.Location, _expanded.Contains(se.Instance) || q.Length > 0);
                    groups[se.Instance] = group;
                    Results.Add(group);
                }
                group.Members.Add(t.Key);
                if (group.IsExpanded) Results.Add(new TuningAddRow(t));
                continue;
            }
            Results.Add(new TuningAddRow(t));
        }
        foreach (var g in groups.Values) g.RefreshDisplay();
    }

    /// <summary>A double-click on an SnP instance's row opens or closes it.</summary>
    public void Toggle(TuningAddRow group)
    {
        if (!group.IsGroup) return;
        if (!_expanded.Add(group.Key)) _expanded.Remove(group.Key);
        Refresh();
    }

    /// <summary>Name, instance, parameter, cell or an SnP entry's label (<c>S31</c>) — case-insensitive.</summary>
    private static bool Matches(Tunable t, string q)
        => new[] { t.Key, t.Owner, t.Parameter, t.Cell, t.SnpEntry?.Label }
            .Any(s => s?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>Adds the selected rows, or the first one shown when none is selected (Enter), and closes.</summary>
    [RelayCommand]
    private void AddSelected()
    {
        if (!Add(null)) return;
        IsOpen = false;
        SearchText = "";
    }

    /// <summary>
    /// Double-click: adds this row, along with any rows already selected, and STAYS OPEN with the search
    /// as typed — so several values are added without reopening the popup each time (owner request,
    /// 2026-10-25). The added rows leave the list: the panel refreshes it while the popup is open.
    /// </summary>
    public void AddWith(TuningAddRow row)
    {
        if (row.IsGroup) { Toggle(row); return; }
        if (Add(row)) Refresh();
    }

    /// <summary>The × beside the search box.</summary>
    [RelayCommand]
    private void Close() => IsOpen = false;

    private bool Add(TuningAddRow? also)
    {
        // An SnP instance's row stands for every entry it holds.
        static IEnumerable<string> KeysOf(TuningAddRow r) => r.IsGroup ? r.Members : [r.Key];
        var keys = Results.Where(r => r.IsSelected || ReferenceEquals(r, also)).SelectMany(KeysOf).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0 && Results.Count > 0) keys.AddRange(KeysOf(Results[0]));
        if (keys.Count == 0) return false;
        panel.Activate(keys);
        return true;
    }
}

/// <summary>One row of the Add… list: a tunable, or an SnP instance standing for its entries (brief-sens-2).</summary>
public sealed partial class TuningAddRow : ObservableObject
{
    public TuningAddRow(Tunable tunable)
    {
        Tunable  = tunable;
        Key      = tunable.Key;
        Location = tunable.Location;
        // An SnP entry reads as its label (SNP3 · S31 dB), indented under its instance's row.
        Display  = tunable.SnpEntry is { } se ? "    " + se.Label : tunable.Key;
    }

    /// <summary>An SnP instance's row: <paramref name="instance"/> is the owner prefix (<c>SNP3</c>).</summary>
    public TuningAddRow(string instance, string location, bool expanded)
    {
        Key = instance;
        Location = location;
        IsExpanded = expanded;
        Display = instance;
    }

    /// <summary>The tunable; null on an SnP instance's row.</summary>
    public Tunable? Tunable { get; }
    public string  Key       { get; }
    public string  Location  { get; }
    public string  ValueText => Tunable?.ValueText ?? "";
    public bool    IsReadOnly => Tunable?.ReadOnlyReason is not null;
    public string? ReadOnlyReason => Tunable?.ReadOnlyReason;

    /// <summary>What the row shows.</summary>
    public string Display { get; private set; }

    /// <summary>An SnP instance's row, standing for <see cref="Members"/>.</summary>
    public bool IsGroup => Tunable is null;

    /// <summary>Its entries are listed under it.</summary>
    public bool IsExpanded { get; }

    /// <summary>An SnP instance's row: the keys it stands for.</summary>
    public List<string> Members { get; } = [];

    internal void RefreshDisplay()
    {
        if (IsGroup) Display = $"{(IsExpanded ? "▾" : "▸")} {Key} · {Members.Count} S-parameter entries";
    }

    [ObservableProperty] private bool _isSelected;
}
