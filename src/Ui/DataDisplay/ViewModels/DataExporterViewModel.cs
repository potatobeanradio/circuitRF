// ================================================================
//  DataExporterViewModel.cs  —  State for the Data Exporter dialog
//
//  Enumerates results/<schematic>/run.npy sources, drives the
//  include/format/Touchstone slicing controls, and exposes the
//  pure export methods the code-behind calls after file picking.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using RfCore.Loadpull;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

// ── Local enums ───────────────────────────────────────────────────────────────

public enum ExportMode { Npy, Mat, Tsv, Touchstone, Spl, Lpcwave }

// ── Helper row types ──────────────────────────────────────────────────────────

public partial class IncludeRow(string groupName) : ObservableObject
{
    public string GroupName { get; } = groupName;
    [ObservableProperty] private bool _isChecked = true;
}

public partial class SweepSliceRow : ObservableObject
{
    public string AxisName  { get; }
    public string AxisUnit  { get; }

    public IReadOnlyList<string> Options { get; }

    [ObservableProperty] private int _selectedIndex;

    public SweepSliceRow(Axis ax)
    {
        AxisName = ax.Name;
        AxisUnit = ax.Unit;

        if (ax.Labels != null)
        {
            Options = ax.Labels.ToList().AsReadOnly();
        }
        else
        {
            var unit = string.IsNullOrEmpty(ax.Unit) ? string.Empty : $" {ax.Unit}";
            Options = ax.Values.Select(v => v.ToString("G6", CultureInfo.InvariantCulture) + unit)
                               .ToList().AsReadOnly();
        }
        _selectedIndex = 0;
    }
}

// ── DataExporterViewModel ─────────────────────────────────────────────────────

public partial class DataExporterViewModel : ObservableObject
{
    // ── Construction / data sources ──────────────────────────────────────────

    private readonly string? _resultsRoot;

    /// <summary>Displayable schematic names (subdirectories of resultsRoot with run.npy).</summary>
    public ObservableCollection<string> AvailableSchematicNames { get; } = new();

    [ObservableProperty] private string? _selectedSchematic;

    private DataSet? _loadedDataSet;

    // ── Format ───────────────────────────────────────────────────────────────

    [ObservableProperty] private ExportMode _exportMode = ExportMode.Npy;

    // ── Include rows (npy / mat / tsv mode) ──────────────────────────────────

    /// <summary>Analysis groups available for inclusion (all groups except measurements).</summary>
    public ObservableCollection<IncludeRow> IncludeRows { get; } = new();

    [ObservableProperty] private bool _includeMeasurements = true;

    /// <summary>Whether the loaded DataSet has a measurements group.</summary>
    public bool MeasurementsAvailable { get; private set; }

    // ── Touchstone options ───────────────────────────────────────────────────

    [ObservableProperty] private double _z0Ohms = 50.0;
    [ObservableProperty] private int    _digits = 12;
    [ObservableProperty] private char   _digitFormat = 'f';
    [ObservableProperty] private MatrixFormat _matrixFormat = MatrixFormat.MA;

    // ── Touchstone slicing ───────────────────────────────────────────────────

    /// <summary>Per-sweep-axis row for pinning/iteration in Touchstone mode.</summary>
    public ObservableCollection<SweepSliceRow> SweepSliceRows { get; } = new();

    [ObservableProperty] private bool _saveAllSweepFiles;

    /// <summary>The currently selected Touchstone group (single-select in Touchstone mode).</summary>
    public IncludeRow? SelectedTouchstoneGroup
        => IncludeRows.FirstOrDefault(r => r.IsChecked);

    // ── Loadpull (.spl / .lpcwave) export ──────────────────────────────────────

    /// <summary>True for the single-select loadpull export formats.</summary>
    public bool IsLoadpullMode => ExportMode is ExportMode.Spl or ExportMode.Lpcwave;

    /// <summary>True when the loaded DataSet has at least one loadpull-shaped group.</summary>
    public bool IsLoadpullAvailable { get; private set; }

    /// <summary>The currently selected loadpull group (single-select, mirrors Touchstone).</summary>
    public IncludeRow? SelectedLoadpullGroup
        => IncludeRows.FirstOrDefault(r => r.IsChecked);

    // ── Z0 notice ────────────────────────────────────────────────────────────

    public string Z0Notice     { get; private set; } = string.Empty;
    public bool   ShowZ0Notice { get; private set; }

    // ── Suggested filename ───────────────────────────────────────────────────

    public string SuggestedFileName { get; private set; } = string.Empty;

    // ── Validation ───────────────────────────────────────────────────────────

    public bool CanExport { get; private set; }

    // ── Constructor ──────────────────────────────────────────────────────────

    public DataExporterViewModel(string? resultsRoot, string? preselectSchematic = null)
    {
        _resultsRoot = resultsRoot;
        EnumerateSources();

        if (preselectSchematic != null
            && AvailableSchematicNames.Contains(preselectSchematic))
        {
            SelectedSchematic = preselectSchematic;
        }
        else if (AvailableSchematicNames.Count > 0)
        {
            SelectedSchematic = AvailableSchematicNames[0];
        }
    }

    // ── Partial property change callbacks ─────────────────────────────────────

    partial void OnSelectedSchematicChanged(string? value)
    {
        LoadDataSet(value);
        RebuildIncludeRows();
        RebuildSweepSliceRows();
        UpdateNoticesAndValidation();
        UpdateSuggestedFileName();
    }

    partial void OnExportModeChanged(ExportMode value)
    {
        RebuildIncludeRows();
        RebuildSweepSliceRows();
        UpdateNoticesAndValidation();
        UpdateSuggestedFileName();
    }

    partial void OnIncludeMeasurementsChanged(bool value)
        => UpdateNoticesAndValidation();

    partial void OnZ0OhmsChanged(double value)
        => UpdateNoticesAndValidation();

    partial void OnSaveAllSweepFilesChanged(bool value)
        => UpdateNoticesAndValidation();

    // ── Enumeration ──────────────────────────────────────────────────────────

    private void EnumerateSources()
    {
        AvailableSchematicNames.Clear();
        if (_resultsRoot is null || !Directory.Exists(_resultsRoot)) return;

        var items = new List<(string schematic, long ticks)>();
        foreach (var sub in Directory.EnumerateDirectories(_resultsRoot))
        {
            string runNpy = Path.Combine(sub, "run.npy");
            if (!File.Exists(runNpy)) continue;
            long ticks;
            try { ticks = new FileInfo(runNpy).LastWriteTimeUtc.Ticks; }
            catch { ticks = 0; }
            items.Add((Path.GetFileName(sub), ticks));
        }
        items.Sort((a, b) => b.ticks.CompareTo(a.ticks));
        foreach (var (s, _) in items) AvailableSchematicNames.Add(s);
    }

    // ── DataSet loading ───────────────────────────────────────────────────────

    private void LoadDataSet(string? schematic)
    {
        _loadedDataSet = null;
        if (schematic is null || _resultsRoot is null) return;

        string path = Path.Combine(_resultsRoot, schematic, "run.npy");
        if (!File.Exists(path)) return;

        try
        {
            var (ds, _) = DataSetImporter.Import(path);
            _loadedDataSet = ds;
        }
        catch { /* file unreadable — leave null, CanExport = false */ }

        // Loadpull (.spl/.lpcwave) export is offered only for loadpull-shaped results.
        _loadpullGroups = _loadedDataSet is null
            ? new List<string>()
            : LoadpullRecognition.FindLoadpullViews(_loadedDataSet)
                                  .Select(v => v.Group ?? DataSet.DefaultGroup)
                                  .ToList();
        IsLoadpullAvailable = _loadpullGroups.Count > 0;
        OnPropertyChanged(nameof(IsLoadpullAvailable));

        // If a loadpull format was selected for a now-non-loadpull source, fall back to .npy.
        if (IsLoadpullMode && !IsLoadpullAvailable)
            ExportMode = ExportMode.Npy;
    }

    private List<string> _loadpullGroups = new();

    // ── Include rows ─────────────────────────────────────────────────────────

    private void RebuildIncludeRows()
    {
        IncludeRows.Clear();
        if (_loadedDataSet is null) return;

        bool isTs = ExportMode == ExportMode.Touchstone;
        bool isLp = IsLoadpullMode;
        bool singleSelect = isTs || isLp;

        foreach (var group in _loadedDataSet.Groups)
        {
            if (group == DataSet.MeasurementsGroup) continue;

            if (isTs)
            {
                // Touchstone: only S-bearing groups
                if (!_loadedDataSet.CubesIn(group).ContainsKey("S")) continue;
            }
            else if (isLp)
            {
                // Loadpull export: only loadpull-shaped groups (recognized by cube signature).
                if (!_loadpullGroups.Contains(group)) continue;
            }

            var row = new IncludeRow(group) { IsChecked = true };
            row.PropertyChanged += (_, _) =>
            {
                if (singleSelect) EnforceSingleSelect(row);
                RebuildSweepSliceRows();
                UpdateNoticesAndValidation();
                OnPropertyChanged(nameof(SelectedTouchstoneGroup));
                OnPropertyChanged(nameof(SelectedLoadpullGroup));
            };
            IncludeRows.Add(row);
        }

        // Single-select modes: keep only the first checked.
        if (singleSelect && IncludeRows.Count > 1)
        {
            for (int i = 1; i < IncludeRows.Count; i++)
                IncludeRows[i].IsChecked = false;
        }

        MeasurementsAvailable = _loadedDataSet.ContainsGroup(DataSet.MeasurementsGroup);
        OnPropertyChanged(nameof(MeasurementsAvailable));
        OnPropertyChanged(nameof(SelectedTouchstoneGroup));
        OnPropertyChanged(nameof(SelectedLoadpullGroup));
    }

    private void EnforceSingleSelect(IncludeRow checkedRow)
    {
        if (!checkedRow.IsChecked) return;
        foreach (var r in IncludeRows)
            if (r != checkedRow) r.IsChecked = false;
    }

    // ── Sweep slice rows ─────────────────────────────────────────────────────

    private void RebuildSweepSliceRows()
    {
        SweepSliceRows.Clear();
        if (ExportMode != ExportMode.Touchstone) return;

        var group = SelectedTouchstoneGroup?.GroupName;
        if (group is null || _loadedDataSet is null) return;

        var result = TouchstoneExporter.Inspect(_loadedDataSet, group);
        foreach (var ax in result.SweepAxes)
            SweepSliceRows.Add(new SweepSliceRow(ax));

        OnPropertyChanged(nameof(SweepSliceRows));
    }

    // ── Z0 notice + validation ────────────────────────────────────────────────

    private void UpdateNoticesAndValidation()
    {
        ShowZ0Notice = false;
        Z0Notice     = string.Empty;

        if (ExportMode == ExportMode.Touchstone)
        {
            var group = SelectedTouchstoneGroup?.GroupName;
            if (group != null && _loadedDataSet != null)
            {
                var insp = TouchstoneExporter.Inspect(_loadedDataSet, group);
                if (insp.SourceZ0Kind != Z0Kind.UniformReal)
                {
                    ShowZ0Notice = true;
                    Z0Notice = $"This analysis uses per-port or complex reference impedances. " +
                               $"Touchstone supports only a single real reference impedance, " +
                               $"so the data will be renormalized to {Z0Ohms} Ω on export.";
                }
            }
        }

        OnPropertyChanged(nameof(Z0Notice));
        OnPropertyChanged(nameof(ShowZ0Notice));

        // Compute CanExport
        bool canExport = _loadedDataSet != null;
        if (canExport)
        {
            canExport = ExportMode switch
            {
                ExportMode.Npy or ExportMode.Mat or ExportMode.Tsv =>
                    IncludeRows.Any(r => r.IsChecked) || (MeasurementsAvailable && IncludeMeasurements),
                ExportMode.Touchstone =>
                    SelectedTouchstoneGroup != null,
                ExportMode.Spl or ExportMode.Lpcwave =>
                    IsLoadpullAvailable && SelectedLoadpullGroup != null,
                _ => false
            };
        }
        CanExport = canExport;
        OnPropertyChanged(nameof(CanExport));
    }

    private void UpdateSuggestedFileName()
    {
        string schematic = SelectedSchematic ?? "export";
        string ext = ExportMode switch
        {
            ExportMode.Npy        => ".npy",
            ExportMode.Mat        => ".mat",
            ExportMode.Tsv        => ".txt",
            ExportMode.Touchstone => TouchstoneExt(),
            ExportMode.Spl        => ".spl",
            ExportMode.Lpcwave    => ".lpcwave",
            _                     => ".npy",
        };
        SuggestedFileName = schematic + ext;
        OnPropertyChanged(nameof(SuggestedFileName));
    }

    private string TouchstoneExt()
    {
        var group = SelectedTouchstoneGroup?.GroupName;
        if (group is null || _loadedDataSet is null) return ".s2p";
        if (!_loadedDataSet.CubesIn(group).TryGetValue("S", out var sCube)) return ".s2p";

        int iIdx = sCube.Axes.ToList().FindIndex(a => a.Name == "i");
        if (iIdx < 0) return ".s2p";
        int nPorts = sCube.Axes[iIdx].Length;
        return $".s{nPorts}p";
    }

    // ── Export methods ────────────────────────────────────────────────────────

    /// <summary>
    /// Export to npy / mat / tsv.  The caller supplies the full path including extension.
    /// </summary>
    public void ExportDataSet(string path)
    {
        if (_loadedDataSet is null) throw new InvalidOperationException("No DataSet loaded.");

        var groups = IncludeRows.Where(r => r.IsChecked).Select(r => r.GroupName).ToList();
        if (MeasurementsAvailable && IncludeMeasurements)
            groups.Add(DataSet.MeasurementsGroup);

        var subset = DataSetSubset.SelectGroups(_loadedDataSet, groups);
        var fmt = ExportMode switch
        {
            ExportMode.Npy => ExportFormat.Npy,
            ExportMode.Mat => ExportFormat.Mat,
            ExportMode.Tsv => ExportFormat.Tsv,
            _              => ExportFormat.Npy,
        };
        DataSetExporter.Export(subset, path, fmt);
    }

    /// <summary>
    /// Export to Touchstone.  The caller supplies the base path (no extension, no suffix).
    /// Returns the result so the code-behind can report collisions / paths.
    /// </summary>
    public TouchstoneExportResult ExportTouchstone(string baseFilePathNoSuffix)
    {
        if (_loadedDataSet is null) throw new InvalidOperationException("No DataSet loaded.");

        var group = SelectedTouchstoneGroup?.GroupName
            ?? throw new InvalidOperationException("No analysis group selected.");

        var opts = new TouchstoneExportOptions(Z0Ohms, Digits, DigitFormat, MatrixFormat);

        var pinnedByAxis = new Dictionary<string, int>();
        foreach (var row in SweepSliceRows)
            pinnedByAxis[row.AxisName] = row.SelectedIndex;

        return TouchstoneExporter.Export(
            _loadedDataSet, group, opts,
            pinnedByAxis,
            allSweepFiles: SaveAllSweepFiles,
            baseFilePathNoSuffix);
    }

    /// <summary>
    /// Export the selected loadpull group to a measured-style <c>.spl</c> or <c>.lpcwave</c> file
    /// (Phase 2 of the loadpull post-processor). The caller supplies the full path including extension.
    /// Multi-frequency loadpull results are written as one block per frequency.
    /// </summary>
    public void ExportLoadpull(string path)
    {
        if (_loadedDataSet is null) throw new InvalidOperationException("No DataSet loaded.");

        var group = SelectedLoadpullGroup?.GroupName
            ?? throw new InvalidOperationException("No loadpull group selected.");

        switch (ExportMode)
        {
            case ExportMode.Spl:     SplWriter.WriteSpl(_loadedDataSet, path, group); break;
            case ExportMode.Lpcwave: LpcwaveWriter.WriteLpcwave(_loadedDataSet, path, group); break;
            default: throw new InvalidOperationException($"ExportLoadpull called in {ExportMode} mode.");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Schematic name backing the currently selected datasource (for pre-population).</summary>
    public string? SelectedSchematicName => SelectedSchematic;
}
