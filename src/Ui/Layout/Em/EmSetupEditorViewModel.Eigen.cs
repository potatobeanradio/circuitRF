// brief-em3d-23 — the panel's port-kind table, an eigenmode setup's count and target, and the modes a run
// found.
//
// NOTHING THAT AFFECTS THE ANSWER LIVES ONLY HERE (R-em-11): the table writes Ports3D and the two boxes
// write Eigenmode, each through the same undoable CommitEdit every other control uses. Both are staged
// text, written when a box loses focus (or the kind picker changes); a row whose port is blank is not
// written, and a row that states nothing but Lumped is written as nothing (EmPort3D.IsDefault).

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Engine.Em3d;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfCore.Data;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>One row of the port-kind table, as typed.</summary>
public sealed partial class Em3dPortRow : ObservableObject
{
    /// <summary>Called when the kind picker changes: a picker has no focus-loss to commit on.</summary>
    internal Action? KindChanged { get; set; }

    [ObservableProperty] private string _port = "";
    [ObservableProperty] private Em3dPortKind _kind = Em3dPortKind.Lumped;
    [ObservableProperty] private string _width = "";
    [ObservableProperty] private string _height = "";
    [ObservableProperty] private string _offsetUm = "";

    public bool IsWave => Kind == Em3dPortKind.Wave;

    partial void OnKindChanged(Em3dPortKind value)
    {
        OnPropertyChanged(nameof(IsWave));
        KindChanged?.Invoke();
    }
}

/// <summary>One mode a run found, as the panel's table shows it.</summary>
public sealed record Em3dModeRow(string Mode, string FrequencyGHz, string Q, string QUnloaded, string Where);

public sealed partial class EmSetupEditorViewModel
{
    /// <summary>The kinds a 3D port can be.</summary>
    public static IReadOnlyList<Em3dPortKind> Port3DKindChoices { get; } = [Em3dPortKind.Lumped, Em3dPortKind.Wave];

    /// <summary>The port-kind table's rows.</summary>
    public ObservableCollection<Em3dPortRow> Port3DRows { get; } = [];

    [ObservableProperty] private string _eigenCountText = "";
    [ObservableProperty] private string _eigenTargetText = "";
    [ObservableProperty] private string? _eigenFieldError;

    /// <summary>The modes of the last eigenmode run of this setup, in this session.</summary>
    public ObservableCollection<Em3dModeRow> EigenmodeRows { get; } = [];

    /// <summary>True once a run has filled the mode table.</summary>
    public bool HasEigenmodeRows => EigenmodeRows.Count > 0;

    /// <summary>True when the port-kind table is shown: a 3D setup that is driven or eigenmode.</summary>
    public bool IsPortKindSetup => Is3DSetup && !IsStaticSetup;

    /// <summary>True when the eigenmode boxes are shown.</summary>
    public bool IsEigenmodeSetup => Is3DSetup && Problem3DChoice.Value == Em3dProblemType.Eigenmode;

    /// <summary>The placeholder of the count box: the default.</summary>
    public static string EigenDefaultCount { get; } = EmEigenmode3D.DefaultCount.ToString(CultureInfo.InvariantCulture);

    public const string Port3DKindTip =
        "Lumped: a sheet between the line and its return with the port's Z0 across it. Wave: a region of the air " +
        "box's face, fed by the line's own mode — no series parasitic, but the line must run to the layout's " +
        "edge, and only Palace builds one. Kinds may be mixed.";

    private void RaiseEigenVisibility()
    {
        OnPropertyChanged(nameof(IsPortKindSetup));
        OnPropertyChanged(nameof(IsEigenmodeSetup));
    }

    private void SyncEigenFields()
    {
        foreach (var r in Port3DRows) r.KindChanged = null;
        Port3DRows.Clear();
        foreach (var p in Working.Ports3D) Port3DRows.Add(Row(p));
        EigenCountText  = Working.Eigenmode?.Count?.ToString(CultureInfo.InvariantCulture) ?? "";
        EigenTargetText = Working.Eigenmode?.TargetGHz?.ToString("R", CultureInfo.InvariantCulture) ?? "";
        EigenFieldError = null;
        RaiseEigenVisibility();
    }

    private Em3dPortRow Row(EmPort3D p) => new()
    {
        Port = p.Port.ToString(CultureInfo.InvariantCulture),
        Kind = p.Kind,
        Width = p.WidthFactor?.ToString("R", CultureInfo.InvariantCulture) ?? "",
        Height = p.HeightFactor?.ToString("R", CultureInfo.InvariantCulture) ?? "",
        OffsetUm = p.OffsetUm?.ToString("R", CultureInfo.InvariantCulture) ?? "",
        KindChanged = () => CommitPorts3D(),
    };

    /// <summary>Writes the table as it now reads: one undoable edit, or none when nothing changed. A row
    /// whose port is not a whole number, or whose number is not a positive number, is left in the table
    /// with the message, and nothing is written.</summary>
    public void CommitPorts3D()
    {
        if (_suppressCommit) return;
        var list = new List<EmPort3D>();
        foreach (var r in Port3DRows)
        {
            if (r.Port.Trim().Length == 0) continue;
            if (!int.TryParse(r.Port.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
            {
                EigenFieldError = $"'{r.Port}' is not a port number.";
                return;
            }
            double? width = Num(r.Width), height = Num(r.Height), offset = Num(r.OffsetUm);
            if (width is double.NaN || height is double.NaN || offset is double.NaN)
            {
                EigenFieldError = $"Port {n}'s width, height and offset must be numbers, or blank for the default.";
                return;
            }
            list.Add(new EmPort3D(n, r.Kind, width, height, offset));
        }
        EigenFieldError = null;
        var before = SnapshotJson();
        Working.Ports3D = list;
        if (SnapshotJson() == before) return;
        CommitEdit(before, "Change 3D port kinds");
    }

    /// <summary>Writes the eigenmode count and target; blank is the default.</summary>
    public void CommitEigenmode()
    {
        if (_suppressCommit) return;
        int? count = null;
        if (EigenCountText.Trim().Length > 0)
        {
            if (!int.TryParse(EigenCountText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) || c < 1)
            {
                EigenFieldError = "The mode count is a whole number, at least 1.";
                return;
            }
            count = c;
        }
        double? target = Num(EigenTargetText);
        if (target is double.NaN || target <= 0)
        {
            EigenFieldError = "The target is a positive frequency in GHz, or blank for the sweep's start.";
            return;
        }
        EigenFieldError = null;
        var before = SnapshotJson();
        Working.Eigenmode = count is null && target is null ? null : new EmEigenmode3D(count, target);
        if (SnapshotJson() == before) return;
        CommitEdit(before, "Change eigenmode settings");
    }

    [RelayCommand]
    private void AddPort3D()
    {
        int next = Port3DRows.Select(r => int.TryParse(r.Port, out int n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
        var row = Row(new EmPort3D(next, Em3dPortKind.Wave));
        Port3DRows.Add(row);
        CommitPorts3D();
    }

    [RelayCommand]
    private void RemovePort3D(Em3dPortRow? row)
    {
        if (row is null || !Port3DRows.Remove(row)) return;
        row.KindChanged = null;
        CommitPorts3D();
    }

    /// <summary>
    /// brief-em3d-23 R-em3d23-4c — the modes of a finished eigenmode run, for the panel's table: mode, f,
    /// Q, Q with the ports' loading removed, and the region holding most of the mode's electric energy.
    /// </summary>
    public void AdoptEigenmodes(DataSet data)
    {
        EigenmodeRows.Clear();
        OnPropertyChanged(nameof(HasEigenmodeRows));
        if (!data.Cubes.TryGetValue(Em3dEigenResult.FrequencyCube, out var f) ||
            !data.Cubes.TryGetValue(Em3dEigenResult.QCube, out var q)) return;
        data.Cubes.TryGetValue(Em3dEigenResult.UnloadedQCube, out var qu);
        data.Cubes.TryGetValue(Em3dEigenResult.ParticipationCube, out var part);
        var inv = CultureInfo.InvariantCulture;
        string Q(double v) => double.IsPositiveInfinity(v) ? "lossless" : double.IsNaN(v) ? "—" : v.ToString("0.0", inv);
        for (int i = 0; i < f.Axes[0].Length; i++)
        {
            string where = "";
            if (part?.Axes[1].Labels is { } names)
            {
                int n = names.Length, best = 0;
                for (int k = 1; k < n; k++) if (part.RealValues[i * n + k] > part.RealValues[i * n + best]) best = k;
                where = $"{names[best]} ({(100 * part.RealValues[i * n + best]).ToString("0", inv)} %)";
            }
            EigenmodeRows.Add(new Em3dModeRow(((int)f.Axes[0].Values[i]).ToString(inv),
                (f.RealValues[i] / 1e9).ToString("0.000000", inv), Q(q.RealValues[i]),
                qu is null ? "" : Q(qu.RealValues[i]), where));
        }
        OnPropertyChanged(nameof(HasEigenmodeRows));
    }

    /// <summary>Blank is null (the default); a number is itself; anything else NaN.</summary>
    private static double? Num(string text)
        => text.Trim().Length == 0 ? null
         : double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
}
