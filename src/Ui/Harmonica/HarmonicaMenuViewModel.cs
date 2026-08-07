// ================================================================
//  HarmonicaMenuViewModel.cs  —  M1 of brief-harmonicarf-h7
//
//  R-h7-1  harmonicaRF's menus are its OWN, and there is NO Simulate menu — it is always simulating,
//          so a Simulate menu would be a lie about what the tool does.
//  R-h7-2  the Markers menu is the first thing that can create a marker, and it must create the
//          marker AND its TerminationSet entry through one call.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CircuitRF.Harmonica;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Harmonica;

/// <summary>One band's entry in the Markers menu.</summary>
public sealed partial class HarmonicaBandMenuItem : ObservableObject
{
    public HarmonicaBandMenuItem(TerminationSideKind side, int band, bool present, bool canRemove,
                                 Action<TerminationSideKind, int, bool> toggle)
    {
        Side       = side;
        Band       = band;
        _isPresent = present;
        CanRemove  = canRemove;
        _toggle    = toggle;
    }

    private readonly Action<TerminationSideKind, int, bool> _toggle;
    private bool _suppress;

    public TerminationSideKind Side { get; }
    public int  Band      { get; }
    public bool CanRemove { get; }

    /// <summary>"S2" / "L3" — §4.2's naming, the same the marker itself uses.</summary>
    public string Header => (Side == TerminationSideKind.Source ? "S" : "L") + Band;

    public string Tooltip => Band == 1
        ? "The fundamental termination is always present and cannot be removed (§4.2)."
        : $"Show a marker on band {Band} of the {(Side == TerminationSideKind.Source ? "source" : "load")} " +
          "plane. Removing it leaves the band unmarked — the absence of a marker, not a marker with a " +
          "default value.";

    [ObservableProperty] private bool _isPresent;

    partial void OnIsPresentChanged(bool value)
    {
        if (_suppress) return;
        _toggle(Side, Band, value);
    }

    /// <summary>Writes the checkbox WITHOUT re-entering the toggle — used when the menu is rebuilt
    /// from the model rather than clicked.</summary>
    internal void SetPresentQuietly(bool value)
    {
        _suppress = true;
        try { IsPresent = value; }
        finally { _suppress = false; }
    }
}

/// <summary>
/// The commands and state behind harmonicaRF's own menu set (§7.6).
///
/// <para><b>There is no Simulate menu and there is not going to be one</b> (R-h7-1). harmonicaRF is
/// always simulating; a menu offering to start it would describe a different tool.</para>
///
/// <para><b>Document-scoped actions arrive as hooks, not as a workspace reference.</b> harmonicaRF
/// opens with no workspace (§1.2) and ships standalone (§3.1), so New / Open / Save / Close are
/// <see cref="Func{TResult}"/>s the host wires. A null hook leaves its menu item disabled rather than
/// present-and-broken.</para>
/// </summary>
public sealed partial class HarmonicaMenuViewModel : ObservableObject
{
    private readonly HarmonicaViewModel _vm;

    public HarmonicaMenuViewModel(HarmonicaViewModel viewModel)
    {
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        RebuildBandMenus();
        _vm.Markers.CollectionChanged += OnMarkersChanged;
    }

    private void OnMarkersChanged(object? sender,
                                  System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RebuildBandMenus();

    /// <summary>Detaches from the view model. The view calls this when its DataContext changes —
    /// without it a replaced menu view model stays alive on the marker list's event and keeps
    /// rebuilding submenus nothing is showing.</summary>
    public void Detach() => _vm.Markers.CollectionChanged -= OnMarkersChanged;

    public HarmonicaViewModel Harmonica => _vm;

    // ── File ─────────────────────────────────────────────────────────────────

    /// <summary>Host hooks. Each is optional; an unwired one disables its item.</summary>
    public Action? NewDocumentHook     { get; set; }
    public Action? OpenDocumentHook    { get; set; }
    public Action? SaveDocumentHook    { get; set; }
    public Action? SaveDocumentAsHook  { get; set; }
    public Action? SetDutHook          { get; set; }
    public Action? ImportGamHook       { get; set; }
    public Action? ExportGamHook       { get; set; }
    public Action? ExportDataHook      { get; set; }
    public Action? ExportTestbenchHook { get; set; }
    public Action? CloseDocumentHook   { get; set; }
    public Action? CopyPlotHook        { get; set; }
    public Action? CopyReadoutsHook    { get; set; }
    public Action? CopyTerminationsHook{ get; set; }
    public Action? PreferencesHook     { get; set; }
    public Action? HelpHook            { get; set; }
    public Action? AddTraceHook        { get; set; }

    [RelayCommand] private void NewDocument()     => NewDocumentHook?.Invoke();
    [RelayCommand] private void OpenDocument()    => OpenDocumentHook?.Invoke();
    [RelayCommand] private void SaveDocument()    => SaveDocumentHook?.Invoke();
    [RelayCommand] private void SaveDocumentAs()  => SaveDocumentAsHook?.Invoke();
    [RelayCommand] private void SetDut()          => SetDutHook?.Invoke();
    [RelayCommand] private void ImportGam()       => ImportGamHook?.Invoke();
    [RelayCommand] private void ExportGam()       => ExportGamHook?.Invoke();
    [RelayCommand] private void ExportData()      => ExportDataHook?.Invoke();
    [RelayCommand] private void ExportTestbench() => ExportTestbenchHook?.Invoke();
    [RelayCommand] private void CloseDocument()   => CloseDocumentHook?.Invoke();

    // ── Edit ─────────────────────────────────────────────────────────────────

    [RelayCommand] private void Undo() => _vm.EditDisplay.Undo.Undo();
    [RelayCommand] private void Redo() => _vm.EditDisplay.Undo.Redo();
    [RelayCommand] private void CopyPlot()         => CopyPlotHook?.Invoke();
    [RelayCommand] private void CopyReadouts()     => CopyReadoutsHook?.Invoke();
    [RelayCommand] private void CopyTerminations() => CopyTerminationsHook?.Invoke();
    [RelayCommand] private void Preferences()      => PreferencesHook?.Invoke();
    [RelayCommand] private void Help()             => HelpHook?.Invoke();

    // ── Markers (R-h7-2) ─────────────────────────────────────────────────────

    /// <summary>Every band on the source plane, checked when it carries a marker.</summary>
    public ObservableCollection<HarmonicaBandMenuItem> SourceBands { get; } = [];

    /// <summary>Every band on the load plane, checked when it carries a marker.</summary>
    public ObservableCollection<HarmonicaBandMenuItem> LoadBands { get; } = [];

    private bool _rebuilding;

    /// <summary>
    /// Rebuilds both band submenus from the model. Called on construction and whenever the marker
    /// list changes — including from a <c>.charm</c> load, so the menu can never claim a band is
    /// marked that the file did not mark.
    /// </summary>
    public void RebuildBandMenus()
    {
        if (_rebuilding) return;
        _rebuilding = true;
        try
        {
            Sync(SourceBands, TerminationSideKind.Source);
            Sync(LoadBands,   TerminationSideKind.Load);
        }
        finally { _rebuilding = false; }

        void Sync(ObservableCollection<HarmonicaBandMenuItem> into, TerminationSideKind side)
        {
            int k = _vm.Terminations.HarmonicCount;

            // The band COUNT only changes when K does, which is a structural edit — rebuild the
            // whole list then, and only update the checkmarks otherwise, so a menu the user has
            // open does not reset its scroll position on every marker toggle.
            if (into.Count != k)
            {
                into.Clear();
                for (int band = 1; band <= k; band++)
                    into.Add(new HarmonicaBandMenuItem(side, band, Present(side, band),
                                                       canRemove: band != 1, ToggleBand));
                return;
            }

            foreach (var item in into) item.SetPresentQuietly(Present(side, item.Band));
        }

        bool Present(TerminationSideKind side, int band)
            => _vm.Markers.Any(m => m.Side == side && m.Band == band);
    }

    private void ToggleBand(TerminationSideKind side, int band, bool wanted)
    {
        if (_rebuilding) return;

        if (wanted) _vm.AddMarkerBand(side, band);
        else if (!_vm.RemoveMarkerBand(side, band))
        {
            // Band 1 refuses. Put the checkmark back rather than leaving the menu asserting
            // something the model does not agree with.
            RebuildBandMenus();
            return;
        }

        _vm.RequestScheduledFrame(dragging: false);
    }

    [RelayCommand]
    private void ResetMarkers()
    {
        _vm.ResetMarkers();
        RebuildBandMenus();
        _vm.RequestScheduledFrame(dragging: false);
    }

    // ── Display ──────────────────────────────────────────────────────────────

    [RelayCommand] private void ToggleEditDisplay() => _vm.EditDisplay.Unlocked = !_vm.EditDisplay.Unlocked;

    /// <summary>§7.7's trace picker. Unlocks the layout as it opens: a trace with nowhere to be put
    /// is not much use, and the new panel lands over the existing ones.</summary>
    [RelayCommand]
    private void AddTrace()
    {
        _vm.EditDisplay.Unlocked = true;
        AddTraceHook?.Invoke();
    }

    /// <summary>Removes every picked trace, and its panel with it.</summary>
    [RelayCommand]
    private void RemoveAllTraces()
    {
        foreach (var t in _vm.PickedTraces.ToArray()) _vm.RemovePickedTrace(t);
    }

    [RelayCommand]
    private void ToggleIsoLineLabels()
    {
        _vm.ShowIsoLineLabels = !_vm.ShowIsoLineLabels;
        _vm.Appearance = _vm.Appearance with { ShowIsoLineLabels = _vm.ShowIsoLineLabels };
    }

    [RelayCommand] private void ToggleLoadlinePlane() => _vm.IntrinsicPlane = !_vm.IntrinsicPlane;

    [RelayCommand]
    private void SetEfficiencyMetric(string? metric)
    {
        _vm.EfficiencyMetric = string.Equals(metric, "PAE", StringComparison.OrdinalIgnoreCase)
            ? GridMetric.Pae : GridMetric.DrainEfficiency;
        _vm.RequestScheduledFrame(dragging: false);
    }

    /// <summary>§6.5's plane selector. Clears any custom grid — a scatter imported for the load plane
    /// says nothing about the source plane.</summary>
    [RelayCommand]
    private void SetGridSide(string? side)
    {
        var want = string.Equals(side, "Source", StringComparison.OrdinalIgnoreCase)
            ? TerminationSide.Source : TerminationSide.Load;
        if (_vm.GridSide == want) return;

        _vm.GridSide   = want;
        _vm.CustomGrid = null;
        _vm.ResetSchedule();
        _vm.RequestScheduledFrame(dragging: false);
    }

    /// <summary>§6.5's harmonic selector — which band the contour grid sweeps.</summary>
    [RelayCommand]
    private void SetGridHarmonic(string? band)
    {
        if (!int.TryParse(band, out int k) || k < 1 || k > _vm.Terminations.HarmonicCount) return;
        if (_vm.GridHarmonic == k) return;

        _vm.GridHarmonic = k;
        _vm.CustomGrid   = null;
        _vm.ResetSchedule();
        _vm.RequestScheduledFrame(dragging: false);
    }

    [RelayCommand]
    private void SetContourLevels(string? levels)
    {
        if (int.TryParse(levels, out int n) && n is >= 2 and <= 40)
        {
            _vm.ContourLevels = n;
            _vm.RequestScheduledFrame(dragging: false);
        }
    }

    // ── Grid ─────────────────────────────────────────────────────────────────

    /// <summary>§6.8's ring presets, as "rings×spokes". Coarse is the scheduler's own tier-B default.</summary>
    public static readonly IReadOnlyList<string> GridPresets = ["3×12", "5×12", "7×16"];

    [RelayCommand]
    private void SetGridPreset(string? preset)
    {
        var parts = (preset ?? "").Split('×', 'x');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int rings)
                              || !int.TryParse(parts[1], out int spokes)) return;

        _vm.SetGridPreset(rings, spokes);
    }

    [RelayCommand] private void ResetGrid() => _vm.ResetGrid();
}
