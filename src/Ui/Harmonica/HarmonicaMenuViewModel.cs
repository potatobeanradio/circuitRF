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
/// One band's entry in Display ▸ Contour Harmonic — owner follow-up (2026-08-13): "the Contour
/// Harmonic menu does not update the harmonic order K. If I set K=5, then the menu should allow me to
/// loadpull or sourcepull on the 5f0 plane." The list used to be three hardcoded XAML items (f₀, 2f₀,
/// 3f₀) on each surface, so K &gt; 3 had no way to reach the bands it actually has. Carries its own
/// <c>Select</c> command (the SAME shape <see cref="HarmonicaBandMenuItem"/> already uses for its own
/// callback) so the in-window <c>ItemsSource</c> menu and the macOS <c>NativeMenu</c> code-behind
/// build (<c>HarmonicaMenuView.RebuildNativeBandMenus</c>) can share one item type and one trigger.
/// </summary>
public sealed partial class HarmonicaHarmonicMenuItem : ObservableObject
{
    public HarmonicaHarmonicMenuItem(int band, Action<int> select)
    {
        Band    = band;
        _select = select;
    }

    private readonly Action<int> _select;

    public int Band { get; }

    /// <summary>"f₀" for the fundamental, "{n}f₀" otherwise — the exact spelling the three hardcoded
    /// XAML items this replaces already used, kept identical rather than switched to
    /// <c>HarmonicaTitles.MxHeaderRow</c>'s own "1f₀" convention (a different row, styled for its own
    /// reasons — no need to move this menu's wording along with its data source).</summary>
    public string Header => Band == 1 ? "f₀" : $"{Band}f₀";

    [RelayCommand] private void Select() => _select(Band);
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
    public Action? RefreshDutHook      { get; set; }
    public Action? ImportGamHook       { get; set; }
    public Action? ExportGamHook       { get; set; }
    public Action? ExportDataHook      { get; set; }
    public Action? ExportTestbenchHook { get; set; }
    public Action? CloseDocumentHook   { get; set; }
    public Action? CopyPlotHook        { get; set; }
    public Action? CopyReadoutsHook    { get; set; }
    public Action? CopyTerminationsHook{ get; set; }
    public Action? HelpHook            { get; set; }
    public Action? AddTraceHook        { get; set; }
    public Action? PowerSweepHook      { get; set; }
    public Action? SetZ0Hook           { get; set; }

    /// <summary>
    /// brief-harmonicarf-r6a §2.2 — the ONE Settings… item, everywhere. Supersedes the separate
    /// <c>PreferencesHook</c> (the colour/appearance editor) and <c>AdvancedSettingsHook</c> (loadline
    /// pts / FFT× / charge / M / §3's contour-kernel controls) — both dialogs merged into one
    /// tabbed <c>HarmonicaSettingsDialog</c>, so there is only one item and one hook to open it.
    /// </summary>
    public Action? SettingsHook { get; set; }

    [RelayCommand] private void NewDocument()     => NewDocumentHook?.Invoke();
    [RelayCommand] private void OpenDocument()    => OpenDocumentHook?.Invoke();
    [RelayCommand] private void SaveDocument()    => SaveDocumentHook?.Invoke();
    [RelayCommand] private void SaveDocumentAs()  => SaveDocumentAsHook?.Invoke();
    [RelayCommand] private void SetDut()          => SetDutHook?.Invoke();
    [RelayCommand] private void RefreshDut()      => RefreshDutHook?.Invoke();
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
    [RelayCommand] private void Settings()         => SettingsHook?.Invoke();
    [RelayCommand] private void Help()             => HelpHook?.Invoke();

    // ── Markers (R-h7-2) ─────────────────────────────────────────────────────

    /// <summary>Every band on the source plane, checked when it carries a marker.</summary>
    public ObservableCollection<HarmonicaBandMenuItem> SourceBands { get; } = [];

    /// <summary>Every band on the load plane, checked when it carries a marker.</summary>
    public ObservableCollection<HarmonicaBandMenuItem> LoadBands { get; } = [];

    /// <summary>Display ▸ Contour Harmonic's own items, one per band 1..K — see
    /// <see cref="HarmonicaHarmonicMenuItem"/>'s own remark for the bug this replaces.</summary>
    public ObservableCollection<HarmonicaHarmonicMenuItem> ContourHarmonics { get; } = [];

    private bool _rebuilding;

    /// <summary>
    /// Rebuilds the band submenus AND Contour Harmonic from the model. Called on construction and
    /// whenever the marker list changes — including from a <c>.charm</c> load, so the menu can never
    /// claim a band is marked (or a harmonic is reachable) that the file did not actually have. K only
    /// ever moves through <c>RetargetTerminations</c>/<c>RebuildMarkersFromTerminations</c>
    /// (structural — no direct "K changed" event exists), both of which always touch
    /// <see cref="HarmonicaViewModel.Markers"/>, so the SAME trigger this method already had for the
    /// band checkboxes is exactly the right one for Contour Harmonic too — one signal, three lists.
    /// </summary>
    public void RebuildBandMenus()
    {
        if (_rebuilding) return;
        _rebuilding = true;
        try
        {
            Sync(SourceBands, TerminationSideKind.Source);
            Sync(LoadBands,   TerminationSideKind.Load);
            SyncContourHarmonics();
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

    /// <summary>Same "rebuild only when the COUNT moved" discipline <c>Sync</c> uses for the band
    /// checkboxes — K is the only thing that ever changes this list's length.</summary>
    private void SyncContourHarmonics()
    {
        int k = _vm.Terminations.HarmonicCount;
        if (ContourHarmonics.Count == k) return;

        ContourHarmonics.Clear();
        for (int band = 1; band <= k; band++)
            ContourHarmonics.Add(new HarmonicaHarmonicMenuItem(band, SelectGridHarmonic));
    }

    /// <summary>A Contour Harmonic item was picked — routes through the SAME
    /// <see cref="SetGridHarmonic"/> the (now dynamic) menu items always used, never a second write.</summary>
    private void SelectGridHarmonic(int band)
        => SetGridHarmonic(band.ToString(System.Globalization.CultureInfo.InvariantCulture));

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

    /// <summary>R-h9r2-18 — Display ▸ Power Sweep…, the explicit Start/Stop/Step ladder, the tickle
    /// and ExactCompressionSolve.</summary>
    [RelayCommand] private void PowerSweep() => PowerSweepHook?.Invoke();

    /// <summary>R-h9r2-20 — Display ▸ Set Z0…, a second surface onto the SAME write the §7.5 input
    /// row already makes.</summary>
    [RelayCommand] private void SetZ0() => SetZ0Hook?.Invoke();

    [RelayCommand]
    private void ToggleIsoLineLabels()
    {
        _vm.ShowIsoLineLabels = !_vm.ShowIsoLineLabels;
        _vm.Appearance = _vm.Appearance with { ShowIsoLineLabels = _vm.ShowIsoLineLabels };
    }

    /// <summary>R-h9b-7 — mirrors <see cref="ToggleIsoLineLabels"/> exactly.</summary>
    [RelayCommand]
    private void ToggleShowGridPoints()
    {
        _vm.ShowGridPoints = !_vm.ShowGridPoints;
        _vm.Appearance = _vm.Appearance with { ShowGridPoints = _vm.ShowGridPoints };
    }

    /// <summary>brief-harmonicarf-r5 §1 — mirrors <see cref="ToggleShowGridPoints"/> exactly. Turning
    /// it OFF does not clear the rolling window (so flipping it on/off to compare two drags keeps both
    /// readings); use <see cref="ResetDiagnosticsOverlay"/> for that.</summary>
    [RelayCommand]
    private void ToggleDiagnosticsOverlay()
    {
        _vm.ShowDiagnosticsOverlay = !_vm.ShowDiagnosticsOverlay;
        _vm.Appearance = _vm.Appearance with { ShowDiagnosticsOverlay = _vm.ShowDiagnosticsOverlay };
    }

    /// <summary>§1.1's own "reset on demand" — clears the rolling window so the owner can do one
    /// representative drag and read a clean set, unpolluted by whatever ran before. Session-only, like
    /// the window itself: never marks the document dirty.</summary>
    [RelayCommand]
    private void ResetDiagnosticsOverlay()
    {
        _vm.Diagnostics.Reset();
        _vm.RequestRedraw();
    }

    /// <summary>brief-harmonicarf-r6d §4 — the power-sweep panel's title fly menu, same parametrized
    /// shape as <see cref="SetGridSide"/>'s Load/Source pair. The panel TITLE tracks this too (it is
    /// read from <see cref="HarmonicaViewModel.ShowPowerSweepTimeDomain"/> at draw time, in
    /// <c>HarmonicaPanelRenderer.BuildPowerSweepPlot</c>/<c>BuildTimeDomainPlot</c>), so it always
    /// names what is actually drawn.</summary>
    [RelayCommand]
    private void SetPowerSweepMode(string? mode)
    {
        bool timeDomain = string.Equals(mode, "TimeDomain", StringComparison.OrdinalIgnoreCase);
        if (_vm.ShowPowerSweepTimeDomain == timeDomain) return;

        _vm.ShowPowerSweepTimeDomain = timeDomain;
        _vm.Appearance = _vm.Appearance with { ShowPowerSweepTimeDomain = timeDomain };
    }

    [RelayCommand] private void ToggleLoadlinePlane() => _vm.IntrinsicPlane = !_vm.IntrinsicPlane;

    /// <summary>§1 (R1C) — the removed toolbar's cursor-snap button. §7.6.</summary>
    [RelayCommand] private void ToggleCursorSnap() => _vm.ToggleCursorSnap();

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

    /// <summary>§1 (R1C) — the removed toolbar's "Solve" button: a forced full-quality re-solve.</summary>
    [RelayCommand] private void SolveNow() => _vm.SolveFullGrid();
}
