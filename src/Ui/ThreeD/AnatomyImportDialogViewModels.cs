// brief-sar-11 — File ▸ Import ▸ Voxel Model… and Surface Meshes…: the dialogs' state. Every rule is AnatomyImport's
// (src/Design), the functions `circuitrf import voxels|surfaces` call; these hold what the user chooses where the file does
// not say — a raw volume's layout, a label list, a tetrahedral mesh's voxel size, each label's or surface's material — and the
// size report (R-sar11-5), re-read as the choices change. Nothing is written until Import, which hands the plan to the editor.

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Import;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One label of the voxel import's table: its count, its tissue name and its material (or air).</summary>
public sealed partial class VoxelImportRowViewModel : ObservableObject
{
    private readonly VoxelImportDialogViewModel _owner;
    private readonly bool _loading;

    internal VoxelImportRowViewModel(VoxelImportDialogViewModel owner, VoxelImportRow row)
    {
        _owner = owner;
        Row = row;
        _loading = true;
        Name = row.Name;
        Material = row.Air ? VoxelImportDialogViewModel.AirChoice : row.Material ?? VoxelImportDialogViewModel.NoMaterial;
        _loading = false;
    }

    public VoxelImportRow Row { get; }
    public string LabelText => Row.Label.ToString(CultureInfo.InvariantCulture);
    public string VoxelsText => Row.Voxels.ToString("N0", CultureInfo.InvariantCulture);
    public string MatchText => Row.Match;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _material = VoxelImportDialogViewModel.NoMaterial;

    partial void OnNameChanged(string value) { Row.Name = value.Trim(); if (!_loading) _owner.RowsChanged(); }

    partial void OnMaterialChanged(string value)
    {
        if (_loading || value is null) return;
        Row.Air = value == VoxelImportDialogViewModel.AirChoice;
        Row.Material = Row.Air || value == VoxelImportDialogViewModel.NoMaterial ? null : value;
        Row.Match = Row.Material is null ? "" : "chosen";
        OnPropertyChanged(nameof(MatchText));
        _owner.RowsChanged();
    }
}

public sealed partial class VoxelImportDialogViewModel : ObservableObject
{
    public const string NoMaterial = "(no material)";
    public const string AirChoice = "(air)";

    private readonly Func<VoxelImportOptions, VoxelImportPlan> _plan;
    private readonly int _dbuPerMicron;

    /// <param name="plan">Reads and plans the import with the dialog's options (AnatomyImport.PlanVoxels on the editor's document).</param>
    public VoxelImportDialogViewModel(string sourcePath, IEnumerable<string> materials, int dbuPerMicron, Func<VoxelImportOptions, VoxelImportPlan> plan)
    {
        SourcePath = sourcePath;
        _dbuPerMicron = dbuPerMicron;
        _plan = plan;
        foreach (var m in materials) Materials.Add(m);
        IsRaw = !TetMesh.IsTetMesh(sourcePath) && C3dVoxelBodies.FormatOf(sourcePath) == "raw";
        IsTet = TetMesh.IsTetMesh(sourcePath);
    }

    public string SourcePath { get; }
    public string Title => $"Import Voxel Model — {Path.GetFileName(SourcePath)}";
    public bool IsRaw { get; }
    public bool IsTet { get; }
    public bool NeedsLayout => IsRaw || IsTet;

    public ObservableCollection<string> Materials { get; } = [NoMaterial, AirChoice];
    public ObservableCollection<VoxelImportRowViewModel> Rows { get; } = [];
    public IReadOnlyList<string> RawTypes { get; } = ["uint8", "int8", "uint16", "int16", "uint32", "int32"];
    public IReadOnlyList<string> Units { get; } = ["mm", "um", "cm", "m", "in", "mil"];

    public VoxelImportPlan? Plan { get; private set; }

    [ObservableProperty] private string _labelsCsv = "";
    [ObservableProperty] private string _dims = "";
    [ObservableProperty] private string _rawType = "uint8";
    [ObservableProperty] private string _voxelMm = "";
    [ObservableProperty] private bool _bigEndian;
    [ObservableProperty] private string _headerBytes = "0";
    [ObservableProperty] private string _meshUnit = "mm";
    [ObservableProperty] private string _gridStepMm = "0";
    [ObservableProperty] private string _objectName = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _report = "";
    [ObservableProperty] private string _notes = "";

    public bool CanAccept => Plan is not null && Error is null && Rows.All(r => r.Row.Air || r.Row.Material is not null);
    partial void OnErrorChanged(string? value) => Notify();

    private void Notify()
    {
        OnPropertyChanged(nameof(CanAccept));
        AcceptCommand.NotifyCanExecuteChanged();
    }

    public event Action<bool>? CloseRequested;

    /// <summary>The options as typed, or the sentence saying which field does not parse.</summary>
    private (VoxelImportOptions? Options, string? Why) Options(int dbuPerMicron)
    {
        double? Mm(string text) => NumericText.TryParseDouble(text, out double v) ? v * 1e-3 : null;
        RawVolumeLayout? raw = null;
        if (IsRaw)
        {
            var d = Dims.Split(',', 'x', '×', ' ').Where(t => t.Length > 0).Select(t => int.TryParse(t, out int n) ? n : -1).ToArray();
            if (d.Length != 3 || d.Any(n => n < 1)) return (null, "Dimensions: three voxel counts, nx, ny, nz.");
            var vox = VoxelMm.Split(',').Select(t => Mm(t.Trim())).ToArray();
            if (vox.Length is not (1 or 3) || vox.Any(v => v is not > 0)) return (null, "Voxel: a size in mm, or three (x, y, z).");
            double[] size = vox.Length == 1 ? [vox[0]!.Value, vox[0]!.Value, vox[0]!.Value] : [.. vox.Select(v => v!.Value)];
            if (!long.TryParse(HeaderBytes, out long header) || header < 0) return (null, "Header: a byte count.");
            raw = new RawVolumeLayout(d[0], d[1], d[2], RawVolumeLayout.ParseType(RawType)!.Value, size[0], size[1], size[2], BigEndian, header);
        }
        double tet = 0;
        if (IsTet)
        {
            if (Mm(VoxelMm) is not { } size || !(size > 0)) return (null, "Voxel: the size the mesh is voxelised at, in mm.");
            tet = size;
        }
        if (Mm(GridStepMm) is not { } step || step < 0) return (null, "Grid step: a length in mm (0 puts a line on every voxel boundary).");
        return (new VoxelImportOptions
        {
            LabelsCsv = LabelsCsv.Trim() is { Length: > 0 } csv ? csv : null, Raw = raw, TetVoxelM = tet,
            TetUnitM = C3dMeshes.UnitMetres(MeshUnit) ?? 1e-3, Name = ObjectName.Trim() is { Length: > 0 } n ? n : null,
            GridStep = (long)Math.Round(step * 1e6 * dbuPerMicron), AllowUnmapped = true,
        }, null);
    }

    /// <summary>Reads the file with the options as they stand and fills the table and the report. A refusal is shown.</summary>
    [RelayCommand]
    public async Task ReadAsync()
    {
        Error = null;
        var (options, why) = Options(_dbuPerMicron);
        if (options is null) { Error = why; return; }
        try
        {
            var plan = await Task.Run(() => _plan(options));
            Plan = plan;
            if (ObjectName.Length == 0) ObjectName = plan.Name;
            Rows.Clear();
            foreach (var r in plan.Rows) Rows.Add(new VoxelImportRowViewModel(this, r));
            Notes = string.Join(Environment.NewLine, plan.Notes);
            RowsChanged();
        }
        catch (Exception e) when (e is LabelVolumeException or RfCore.Hdf5.OpenEmsHdf5Exception or IOException or UnauthorizedAccessException)
        {
            Plan = null;
            Rows.Clear();
            Report = "";
            Error = e.Message;
        }
    }

    internal void RowsChanged()
    {
        if (Plan is { } plan)
        {
            plan.Name = ObjectName.Trim();
            Report = string.Join(Environment.NewLine, plan.Report.Lines(plan.Rows));
        }
        Notify();
    }

    partial void OnObjectNameChanged(string value) { if (Plan is { } p) p.Name = value.Trim(); }

    [RelayCommand(CanExecute = nameof(CanAccept))]
    private void Accept()
    {
        if (Plan is { Unmapped.Count: > 0 } p) { Error = C3dVoxelBodies.UnmappedRefusal(p.Name, p.Unmapped); return; }
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}

/// <summary>One surface of the surface import: its name, its material, and what the check found.</summary>
public sealed partial class SurfaceImportRowViewModel : ObservableObject
{
    private readonly bool _loading;

    internal SurfaceImportRowViewModel(SurfaceImportRow row)
    {
        Row = row;
        _loading = true;
        Name = row.Name;
        Material = row.Material ?? SurfaceImportDialogViewModel.NoMaterial;
        _loading = false;
    }

    public SurfaceImportRow Row { get; }
    public string File => Path.GetFileName(Row.SourcePath);
    public string CheckText => Row.Fault is { } f ? f : $"closed, {Row.Check.Triangles:N0} triangles" + (Row.Flip ? ", turned round" : "");
    public bool Faulty => Row.Fault is not null;
    public string DepthText => Row.Depth == 0 ? "outermost" : $"inside {Row.Depth}";

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _material = SurfaceImportDialogViewModel.NoMaterial;

    partial void OnNameChanged(string value) { if (!_loading) Row.Name = value.Trim(); }

    partial void OnMaterialChanged(string value)
    {
        if (_loading || value is null) return;
        Row.Material = value == SurfaceImportDialogViewModel.NoMaterial ? null : value;
        Row.Match = Row.Material is null ? "" : "chosen";
    }
}

public sealed partial class SurfaceImportDialogViewModel : ObservableObject
{
    public const string NoMaterial = "(no material)";

    private readonly Func<string, SurfaceImportPlan> _plan;

    public SurfaceImportDialogViewModel(IReadOnlyList<string> files, IEnumerable<string> materials, Func<string, SurfaceImportPlan> plan)
    {
        Files = files;
        _plan = plan;
        foreach (var m in materials) Materials.Add(m);
    }

    public IReadOnlyList<string> Files { get; }
    public string Title => $"Import Surface Meshes — {Files.Count} file{(Files.Count == 1 ? "" : "s")}";
    public ObservableCollection<string> Materials { get; } = [NoMaterial];
    public ObservableCollection<SurfaceImportRowViewModel> Rows { get; } = [];
    public IReadOnlyList<string> Units { get; } = ["mm", "um", "cm", "m", "in", "mil"];

    public SurfaceImportPlan? Plan { get; private set; }

    [ObservableProperty] private string _unit = "mm";
    [ObservableProperty] private string _groupName = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _notes = "";

    public bool CanAccept => Plan is not null && Error is null && Rows.All(r => !r.Faulty);
    partial void OnErrorChanged(string? value) { OnPropertyChanged(nameof(CanAccept)); AcceptCommand.NotifyCanExecuteChanged(); }
    partial void OnUnitChanged(string value) => _ = ReadAsync();
    partial void OnGroupNameChanged(string value) { if (Plan is { } p) p.Group = value.Trim(); }

    public event Action<bool>? CloseRequested;

    /// <summary>Reads and checks every surface, ordered outermost first.</summary>
    public async Task ReadAsync()
    {
        Error = null;
        string unit = Unit;
        try
        {
            var plan = await Task.Run(() => _plan(unit));
            Plan = plan;
            Rows.Clear();
            foreach (var r in plan.Rows) Rows.Add(new SurfaceImportRowViewModel(r));
            if (GroupName.Length == 0) GroupName = plan.Group; else plan.Group = GroupName.Trim();
            Notes = string.Join(Environment.NewLine, plan.Notes);
            if (plan.Rows.FirstOrDefault(r => r.Fault is not null) is { } bad)
                Error = $"'{Path.GetFileName(bad.SourcePath)}' {bad.Fault}. A surface that is not closed cannot be a tissue; nothing will be imported.";
        }
        catch (Exception e) when (e is LabelVolumeException or IOException or UnauthorizedAccessException)
        {
            Plan = null;
            Rows.Clear();
            Error = e.Message;
        }
        OnPropertyChanged(nameof(CanAccept));
        AcceptCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAccept))]
    private void Accept() => CloseRequested?.Invoke(true);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
