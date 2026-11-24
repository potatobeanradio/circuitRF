// ================================================================
//  FreqLinkViewModel.cs  —  the link toggle beside a frequency picker
//  (brief-ddl-4 R-ddl4-3, R-ddl4-4)
//
//  A contour's frequency and an expression's frequency are fields of
//  their own, not slice axes, so they have no pin row. This is the pin
//  row's link half for one of them: a toggle that swaps the frequency
//  picker for a source picker, the reading of what the link resolved
//  to, and the chip for a source that is gone. The trace card owns the
//  model; this only shows it and reports the user's choice.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Render.DataDisplay;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public sealed partial class FreqLinkViewModel : ObservableObject
{
    private readonly Action<string?, string?> _commit;
    private readonly Action _refresh;
    private readonly Func<string?, string?> _findNamed;
    private bool _suppress, _settingSource;

    /// <param name="slot">Which frequency: <see cref="LinkGraph.ContourFreqSlot"/> or <see cref="LinkGraph.ExpressionFreqSlot"/>.</param>
    /// <param name="commit">Writes a link (id, name) — or none, for nulls — into the model and resolves it.</param>
    /// <param name="refresh">Fills the picker and the reading from the model.</param>
    /// <param name="findNamed">The id of the source of that name now, or null.</param>
    internal FreqLinkViewModel(int slot, Action<string?, string?> commit, Action refresh, Func<string?, string?> findNamed)
    {
        Slot = slot;
        _commit = commit;
        _refresh = refresh;
        _findNamed = findNamed;
    }

    internal int Slot { get; }

    /// <summary>The toggle: on, the frequency follows <see cref="LinkId"/> (or waits for a source to be picked).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowIndexPicker))]
    private bool _isLinked;

    /// <summary>The frequency picker, when the frequency is a fixed index.</summary>
    public bool ShowIndexPicker => !IsLinked;

    public string? LinkId   { get; private set; }
    public string? LinkName { get; private set; }

    [ObservableProperty] private IReadOnlyList<LinkSourceItem> _linkSources = [];
    [ObservableProperty] private LinkSourceItem? _selectedLinkSource;

    /// <summary><c>@m1 → 2.4 GHz</c>: the link and the frequency it resolved to.</summary>
    [ObservableProperty] private string _linkText = "";
    [ObservableProperty] private bool _isDangling;
    [ObservableProperty] private string _danglingText = "";
    [ObservableProperty] private bool _canUseNewSource;

    public string UseNewSourceText => $"Use the new {LinkName}";
    public string LinkPlaceholder  => IsDangling ? "Link to…" : "Choose a marker or control";

    partial void OnIsDanglingChanged(bool value) => OnPropertyChanged(nameof(LinkPlaceholder));

    partial void OnIsLinkedChanged(bool value)
    {
        if (_suppress) return;
        Gesture.Note("freq.link", value ? "link" : "index");
        if (value) { _refresh(); return; }     // a source is chosen in the picker
        LinkId = null; LinkName = null;
        _commit(null, null);
    }

    partial void OnSelectedLinkSourceChanged(LinkSourceItem? value)
    {
        if (_suppress || _settingSource || value is null) return;
        if (!value.IsEnabled) { SetSelectedSilent(LinkSources.FirstOrDefault(i => i.Id == LinkId)); return; }
        if (string.Equals(value.Id, LinkId, StringComparison.OrdinalIgnoreCase)) return;
        Gesture.Note("freq.link", $"@{value.Name}");
        LinkId = value.Id; LinkName = value.Name;
        _commit(LinkId, LinkName);
    }

    /// <summary>Follows the marker that now carries the dangling link's name.</summary>
    [RelayCommand]
    private void UseNewSource()
    {
        if (_findNamed(LinkName) is not { } id) return;
        LinkId = id;
        _commit(LinkId, LinkName);
    }

    /// <summary>Sets the toggle and link from the model without treating it as an edit.</summary>
    internal void Load(string? id, string? name)
    {
        _suppress = true;
        IsLinked = id is not null || name is not null;
        _suppress = false;
        LinkId = id; LinkName = name;
        OnPropertyChanged(nameof(UseNewSourceText));
    }

    /// <summary>Puts the picker's items, selection and texts in place.</summary>
    internal void Show(IReadOnlyList<LinkSourceItem> sources, string text, bool dangling, string danglingText, bool canUseNew)
    {
        LinkSources = sources;
        SetSelectedSilent(sources.FirstOrDefault(i => string.Equals(i.Id, LinkId, StringComparison.OrdinalIgnoreCase)));
        LinkText = text;
        IsDangling = dangling;
        DanglingText = danglingText;
        CanUseNewSource = canUseNew;
        OnPropertyChanged(nameof(UseNewSourceText));
    }

    private void SetSelectedSilent(LinkSourceItem? item)
    {
        _settingSource = true;
        SelectedLinkSource = item;
        _settingSource = false;
    }
}
