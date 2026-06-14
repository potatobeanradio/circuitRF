// ================================================================
//  AppSettingsViewModel.cs  —  Observable settings wrapper (singleton)
//
//  AppSettingsViewModel.Instance is the app-wide singleton.  Any
//  component that needs to read a setting accesses it directly via
//  this static property — no constructor injection required.
//
//  Every observable property auto-saves to disk on change.
//
//  Real-time visual refresh:
//    DataDisplayViewModel subscribes to PropertyChanged and triggers
//    the appropriate refresh paths for display-affecting settings:
//      AlwaysDisplayDataSourcePrefix  → rebuild label strips + redraw
//      MarkerBoxTransparentBackground → redraw all info boxes
//
//  ──────────────────────────────────────────────────────────────
//  Adding a new setting (complete checklist — see AppSettings.cs):
//    2. Add [ObservableProperty] _fieldName below (step 2 of 5).
//    3. Initialise it in the private constructor from _model.
//    4. Add partial void OnXxxChanged to sync _model + call Save().
//    5. If it needs an immediate screen refresh, add it to
//       DataDisplayViewModel.OnSettingsPropertyChanged().
// ================================================================

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class AppSettingsViewModel : ViewModelBase
{
    // ── Singleton ────────────────────────────────────────────────────────
    public static AppSettingsViewModel Instance { get; } = new();

    // ── Backing model ────────────────────────────────────────────────────
    private readonly AppSettings _model;

    // ── Observable properties ────────────────────────────────────────────

    // Export & Copy
    [ObservableProperty] private ExportThemeMode _exportTheme;
    [ObservableProperty] private bool            _exportTransparentBackground;

    // Display
    [ObservableProperty] private bool _markerBoxTransparentBackground;
    [ObservableProperty] private bool _alwaysDisplayDataSourcePrefix;

    // New Marker Defaults
    [ObservableProperty] private int             _markerMaxFractionDigits;
    [ObservableProperty] private PrecisionFormat  _markerPrecisionFormat;

    // Rect Plot
    [ObservableProperty] private double _rectAspectRatio;

    // ── Constructor ──────────────────────────────────────────────────────
    private AppSettingsViewModel()
    {
        _model = AppSettings.Load();

        _exportTheme                    = _model.ExportTheme;
        _exportTransparentBackground    = _model.ExportTransparentBackground;
        _markerBoxTransparentBackground = _model.MarkerBoxTransparentBackground;
        _alwaysDisplayDataSourcePrefix  = _model.AlwaysDisplayDataSourcePrefix;
        _markerMaxFractionDigits        = _model.MarkerMaxFractionDigits;
        _markerPrecisionFormat          = _model.MarkerPrecisionFormat;
        _rectAspectRatio                = _model.RectAspectRatio;
    }

    // ── Save-on-change partial handlers ──────────────────────────────────

    partial void OnExportThemeChanged(ExportThemeMode value)
    {
        _model.ExportTheme = value;
        _model.Save();
        OnPropertyChanged(nameof(ExportThemeIndex));   // keep combo in sync
    }

    partial void OnExportTransparentBackgroundChanged(bool value)
        { _model.ExportTransparentBackground = value; _model.Save(); }

    partial void OnMarkerBoxTransparentBackgroundChanged(bool value)
        { _model.MarkerBoxTransparentBackground = value; _model.Save(); }

    partial void OnAlwaysDisplayDataSourcePrefixChanged(bool value)
        { _model.AlwaysDisplayDataSourcePrefix = value; _model.Save(); }

    partial void OnMarkerMaxFractionDigitsChanged(int value)
    {
        _model.MarkerMaxFractionDigits = value;
        _model.Save();
        OnPropertyChanged(nameof(MarkerDigitsDecimal));   // keep NumericUpDown in sync
    }

    partial void OnMarkerPrecisionFormatChanged(PrecisionFormat value)
    {
        _model.MarkerPrecisionFormat = value;
        _model.Save();
        OnPropertyChanged(nameof(MarkerPrecisionFormatIndex));   // keep combo in sync
    }

    partial void OnRectAspectRatioChanged(double value)
    {
        _model.RectAspectRatio = value;
        _model.Save();
        OnPropertyChanged(nameof(RectAspectRatioDecimal));   // keep NumericUpDown in sync
    }

    // ── NumericUpDown adapter properties ─────────────────────────────────
    // NumericUpDown.Value is decimal?, so these bridge the type gap cleanly.
    // The underlying [ObservableProperty] fields stay int / double throughout.

    public decimal MarkerDigitsDecimal
    {
        get => MarkerMaxFractionDigits;
        set
        {
            int i = (int)Math.Round(Math.Clamp((double)value, 1, 10));
            if (i != MarkerMaxFractionDigits) MarkerMaxFractionDigits = i;
        }
    }

    // decimal? matches NumericUpDown.Value exactly — no binding coercion needed.
    public decimal? RectAspectRatioDecimal
    {
        get => (decimal)RectAspectRatio;
        set
        {
            // Null means the user cleared the field or typed something non-numeric.
            // Silently restore the golden ratio instead of letting the setting go invalid.
            double d = value.HasValue
                ? (double)value.Value
                : AppSettings.GoldenAspectRatio;

            if (Math.Abs(d - RectAspectRatio) > 1e-6)
            {
                RectAspectRatio = d;   // OnRectAspectRatioChanged → save + notify decimal
            }
            else
            {
                // Setting didn't change numerically (e.g. user cleared then re-entered same
                // value, or we just restored the golden ratio that was already set).
                // Push the canonical value back to the NumericUpDown to clear any stale text.
                OnPropertyChanged(nameof(RectAspectRatioDecimal));
            }
        }
    }

    // ── ComboBox index ↔ enum helpers ────────────────────────────────────
    // SelectedIndex binding avoids needing a converter in XAML.

    public static IReadOnlyList<string> ExportThemeModeLabels { get; } =
        ["Follow System Theme", "Force Light Theme", "Force Dark Theme"];

    public int ExportThemeIndex
    {
        get => (int)ExportTheme;
        set { if ((int)ExportTheme != value) ExportTheme = (ExportThemeMode)value; }
    }

    public static IReadOnlyList<string> PrecisionFormatLabels { get; } =
        ["Auto  (G)", "Fixed (F)", "Scientific (E)"];

    public int MarkerPrecisionFormatIndex
    {
        get => (int)MarkerPrecisionFormat;
        set { if ((int)MarkerPrecisionFormat != value) MarkerPrecisionFormat = (PrecisionFormat)value; }
    }

    // ── Public helpers called by renderers / exporters ───────────────────

    /// <summary>
    /// Returns the <see cref="RenderTheme"/> to use for export / copy,
    /// applying the <see cref="ExportTheme"/> override against the live system theme.
    /// </summary>
    public RenderTheme GetExportRenderTheme(RenderTheme systemTheme) => ExportTheme switch
    {
        ExportThemeMode.ForceLightTheme => RenderTheme.Light,
        ExportThemeMode.ForceDarkTheme  => RenderTheme.Dark,
        _                               => systemTheme
    };

    /// <summary>
    /// Returns the effective show-file-prefix flag.
    /// Always true when <see cref="AlwaysDisplayDataSourcePrefix"/> is set;
    /// otherwise mirrors the library-count heuristic supplied by the caller.
    /// </summary>
    public bool EffectiveShowFilePrefix(bool libraryHasMultiple) =>
        AlwaysDisplayDataSourcePrefix || libraryHasMultiple;
}
