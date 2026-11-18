// PWL-4 — the wall port in the setup panel.
//
// R-pwl4-6b: a 3D view's own setup lists the view's ports on the air box's faces and chooses, for this setup only, whether each
// face runs as drawn, as wave ports or as wall ports — the setup's Ports3D rows (brief 3), never the drawing. Z0 and the offset
// stay on the port itself and are shown read-only, so each value is edited in exactly one place. A choice takes the whole face,
// since one face carries one kind.
//
// R-pwl4-7: Calibrate wall ports, the setup's CalibrateWallPorts switch, under the port table in both containers; greyed when
// the setup has no wall port, never hidden, so nothing below it moves.

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Engine.Em3d;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>A port of a 3D view drawn on a face of the air box, as the setup panel lists it: a terminal of a wave port is its own
/// row. <paramref name="Face"/> is the box face (<c>xmin</c> …); <paramref name="Offset"/> the reference plane's move, as shown.</summary>
public sealed record EmFacePort(int Number, string Name, Em3dPortKind Drawn, string Face, string Z0, string Offset);

/// <summary>One row of a 3D view's face-port table.</summary>
public sealed partial class EmFacePortRow : ObservableObject
{
    public const string WaveChoice = "Wave", WallChoice = "Wall";

    public EmFacePortRow(EmFacePort port, string choice)
    {
        Port = port;
        Choices = [AsDrawn(port.Drawn), WaveChoice, WallChoice];
        _choice = choice;
    }

    public EmFacePort Port { get; }
    public string Number => Port.Number.ToString(CultureInfo.InvariantCulture);
    public string Name => Port.Name;
    public string Z0 => Port.Z0;
    public string Offset => Port.Offset;

    /// <summary>The row's own stable list: "As drawn (Wave)" or "As drawn (Wall)", then Wave and Wall.</summary>
    public IReadOnlyList<string> Choices { get; }

    public static string AsDrawn(Em3dPortKind drawn) => $"As drawn ({drawn})";

    [ObservableProperty] private string _choice;

    internal Action<EmFacePortRow>? ChoiceChanged { get; set; }

    partial void OnChoiceChanged(string value) => ChoiceChanged?.Invoke(this);

    /// <summary>The kind this setup runs the port as.</summary>
    public Em3dPortKind Runs => Choice == WaveChoice ? Em3dPortKind.Wave : Choice == WallChoice ? Em3dPortKind.Wall : Port.Drawn;
}

public sealed partial class EmSetupEditorViewModel
{
    public const string FacePortTableTip =
        "How this setup runs the 3D view's ports on the air box's faces. As drawn: as the port states. Wave or Wall: that kind for " +
        "this setup only (its Ports3D), the drawing unchanged. A face's ports switch together: one face carries one kind.";

    public const string FacePortValueTip = "Set in the port's Properties.";

    /// <summary>R-pwl4-6b — the 3D view's ports on the air box's faces; set by the view that hosts an embedded setup.</summary>
    public Func<IReadOnlyList<EmFacePort>>? FacePortsHost
    {
        get => _facePortsHost;
        set { _facePortsHost = value; RebuildFacePortRows(); }
    }
    private Func<IReadOnlyList<EmFacePort>>? _facePortsHost;

    public ObservableCollection<EmFacePortRow> FacePortRows { get; } = [];

    /// <summary>The face-port table is shown: an embedded driven or eigenmode 3D setup (the .cem's own table is the other).</summary>
    public bool IsFacePortSetup => Is3DSetup && !IsStaticSetup && IsEmbedded;

    /// <summary>Greyed header text when the view has no port on a face of the air box, so the panel keeps its layout.</summary>
    public bool HasFacePorts => FacePortRows.Count > 0;

    /// <summary>Re-reads the view's face ports: the view calls it when its ports change.</summary>
    public void RefreshFacePorts() => RebuildFacePortRows();

    private void RebuildFacePortRows()
    {
        foreach (var r in FacePortRows) r.ChoiceChanged = null;
        FacePortRows.Clear();
        foreach (var p in _facePortsHost?.Invoke() ?? [])
        {
            var row = Working.Ports3D.LastOrDefault(x => x.Port == p.Number && x.Kind is Em3dPortKind.Wave or Em3dPortKind.Wall);
            string choice = row is null ? EmFacePortRow.AsDrawn(p.Drawn) : row.Kind == Em3dPortKind.Wave ? EmFacePortRow.WaveChoice : EmFacePortRow.WallChoice;
            FacePortRows.Add(new EmFacePortRow(p, choice) { ChoiceChanged = FacePortChoiceChanged });
        }
        OnPropertyChanged(nameof(HasFacePorts));
        RaiseWallSwitch();
    }

    /// <summary>A row's choice takes its whole face: "As drawn" removes the face's Ports3D rows, Wave or Wall writes one per port.
    /// One undo entry; the note under the table names the ports that came along.</summary>
    private void FacePortChoiceChanged(EmFacePortRow changed)
    {
        if (_suppressCommit) return;
        var face = FacePortRows.Where(r => r.Port.Face == changed.Port.Face).ToList();
        _suppressCommit = true;
        try { foreach (var r in face.Where(r => r != changed)) r.Choice = changed.Choice == EmFacePortRow.WaveChoice || changed.Choice == EmFacePortRow.WallChoice
                                                                        ? changed.Choice : EmFacePortRow.AsDrawn(r.Port.Drawn); }
        finally { _suppressCommit = false; }
        var numbers = face.Select(r => r.Port.Number).ToHashSet();
        var kept = Working.Ports3D.Where(x => !numbers.Contains(x.Port)).ToList();
        if (changed.Choice is EmFacePortRow.WaveChoice or EmFacePortRow.WallChoice)
        {
            var kind = changed.Choice == EmFacePortRow.WaveChoice ? Em3dPortKind.Wave : Em3dPortKind.Wall;
            kept.AddRange(face.Select(r => new EmPort3D(r.Port.Number, kind)));
        }
        var before = SnapshotJson();
        Working.Ports3D = [.. kept.OrderBy(x => x.Port)];
        Port3DKindNote = face.Count > 1
            ? $"The {changed.Port.Face} face's ports ({string.Join(", ", face.Select(r => r.Name))}) run {(changed.Choice.StartsWith("As drawn", StringComparison.Ordinal) ? "as drawn" : "as " + changed.Choice.ToLowerInvariant() + " ports")} in this setup: one face carries one kind."
            : "";
        RaiseWallSwitch();
        if (SnapshotJson() == before) return;
        CommitEdit(before, $"Run the {changed.Port.Face} face's ports {changed.Choice}");
    }

    // ── R-pwl4-7 — Calibrate wall ports ─────────────────────────────────────────────────────

    /// <summary>The setup runs at least one wall port: drawn as one, chosen here, or a .cem row of kind Wall.</summary>
    public bool HasWallPorts => IsEmbedded ? FacePortRows.Any(r => r.Runs == Em3dPortKind.Wall)
                                           : Port3DRows.Any(r => r.Kind == Em3dPortKind.Wall);

    public string CalibrateWallPortsTip => HasWallPorts
        ? "On: line standards solved before the structure calibrate each wall port's gap and the wall out, to the reference plane at " +
          "the wall. Off: the raw S at each gap, in the port's Z0, for comparison."
        : "No wall ports in this setup.";

    /// <summary>The setup's <c>CalibrateWallPorts</c>; off is written as <c>false</c>, on is the default and writes nothing.</summary>
    public bool CalibrateWallPorts
    {
        get => Working.CalibrateWallPorts;
        set
        {
            if (value == Working.CalibrateWallPorts || _suppressCommit) return;
            var before = SnapshotJson();
            Working.CalibrateWallPorts = value;
            OnPropertyChanged();
            if (SnapshotJson() != before) CommitEdit(before, value ? "Calibrate wall ports" : "Do not calibrate wall ports");
        }
    }

    private void RaiseWallSwitch()
    {
        OnPropertyChanged(nameof(HasWallPorts));
        OnPropertyChanged(nameof(CalibrateWallPortsTip));
        OnPropertyChanged(nameof(CalibrateWallPorts));
        OnPropertyChanged(nameof(IsFacePortSetup));
    }
}
