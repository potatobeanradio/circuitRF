// brief-pch-9 R-pch9-7 — when the tool's array is an end of a Propagation Channel: the drop's rays on the (u,v) map (departure directions on
// a transmit array, arrival on a receive one), the codebook's beams, and the Beams list of ranked distinct pairs, all from the selection a run
// makes (BeamSelection.Score — nothing applied).
//
// WRITES, each one undo step on the schematic's own stack, as every edit the tool makes:
//   a pair clicked    BeamSelect=index, TxBeam/RxBeam the pair's codebook entries (each steered end's; BeamRank removed);
//   a ray clicked     the same with the ray's own directions, (θ,φ) in degrees;
//   Apply to array    every steered end's chosen direction into its steer() (PAA-9's write: the VARs, or the literals), and BeamSelect=manual,
//                     TxBeam/RxBeam/BeamRank removed — the run then uses the arrays' own weights, which now say the same beam.
// The drop slider changes nothing in the schematic: it scores another realization (Drop=) in memory.

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine.AntennaArray;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.PhasedArray;

/// <summary>One row of the Beams list: a distinct pair.</summary>
public sealed record BeamRow(int Rank, string Label, string Metric, int Tx, int Rx, bool Chosen)
{
    public string RankText => Rank.ToString(CultureInfo.InvariantCulture);
}

public sealed partial class PhasedArrayToolViewModel
{
    private bool _channelBusy, _channelAgain;
    private CancellationTokenSource? _channelCancel;

    /// <summary>True while the channel is being scored (off the UI thread); the tab's progress bar shows it.</summary>
    [ObservableProperty] private bool _scoringChannel;

    /// <summary>The scoring's progress, 0 to 1 within its current stage (0 while it cannot be counted).</summary>
    [ObservableProperty] private double _channelProgress;

    /// <summary>A RunProgress as a status line: the stage and its counter.</summary>
    internal static string Describe(CircuitRF.Engine.RunProgress p)
        => p.StageTotal > 0 ? $"{p.Stage} {p.StageCompleted} / {p.StageTotal}{(p.StageUnit.Length > 0 ? " " + p.StageUnit : "")}" : p.Stage;

    /// <summary>The channel this array ends, scored; null when it ends none.</summary>
    [ObservableProperty] private ChannelBeams? _channel;

    /// <summary>The drop the slider shows (in memory; the channel's own Drop is not written).</summary>
    [ObservableProperty] private double _dropValue = 1;

    /// <summary>What the Channel tab's status line says.</summary>
    [ObservableProperty] private string _channelStatus = "";

    /// <summary>The ranked distinct pairs, strongest first.</summary>
    public ObservableCollection<BeamRow> BeamRows { get; } = [];

    /// <summary>True when the array is an end of a channel.</summary>
    public bool HasChannel => _session?.ChannelEnd() is not null;

    private bool _dropSet, _showingDrop;

    partial void OnDropValueChanged(double value)
    {
        if (_showingDrop) return;
        _dropSet = true;
        RefreshChannel();
    }

    /// <summary>Scores the channel again (off the UI thread), at the slider's drop once it has been moved, else the channel's own.</summary>
    public void RefreshChannel()
    {
        OnPropertyChanged(nameof(HasChannel));
        if (_session is not { } session || Frame is not { } frame || session.ChannelEnd() is null)
        {
            if (Channel is not null) { Channel = null; BeamRows.Clear(); ChannelStatus = ""; }
            return;
        }
        // a newer request cancels the one running (a drop slider dragged across, an edit), which then starts the newest
        if (_channelBusy) { _channelAgain = true; _channelCancel?.Cancel(); return; }
        _channelBusy = true;
        var cts = new CancellationTokenSource();
        _channelCancel = cts;
        long? drop = _dropSet ? (long)Math.Round(DropValue) : null;
        double hz = frame.State.Hz;
        ScoringChannel = true;
        ChannelProgress = 0;
        ChannelStatus = $"Scoring the beams{(drop is { } d ? $" at drop {d}" : "")}…";
        var control = new CircuitRF.Engine.RunControl
        {
            Token = cts.Token,
            MinReportIntervalMs = 100,
            Progress = new PostedProgress(_host, p =>
            {
                if (_disposed || cts.IsCancellationRequested) return;
                ChannelProgress = p.StageTotal > 0 ? (double)p.StageCompleted / p.StageTotal : 0;
                ChannelStatus = Describe(p);
            }),
        };
        _host.Run(() =>
        {
            ChannelBeams? beams = null;
            bool cancelled = false;
            try { beams = session.ChannelAt(drop, hz, control); }
            catch (OperationCanceledException) { cancelled = true; }
            _host.Post(() =>
            {
                _channelBusy = false;
                ScoringChannel = false;
                if (_disposed) return;
                if (!cancelled) ShowChannel(beams);
                if (_channelAgain) { _channelAgain = false; RefreshChannel(); }
            });
        });
    }

    private void ShowChannel(ChannelBeams? beams)
    {
        Channel = beams;
        BeamRows.Clear();
        if (beams is null) { ChannelStatus = ""; Redraw?.Invoke(this, EventArgs.Empty); return; }
        if (!_dropSet) { _showingDrop = true; DropValue = beams.WrittenDrop; _showingDrop = false; }
        if (beams.Table is not { } t)
        {
            ChannelStatus = $"{beams.Channel}: {beams.Refusal}";
            Redraw?.Invoke(this, EventArgs.Empty);
            return;
        }
        static string End(string side, BeamEntry e, bool steered) => !steered ? $"{side} {e.Label}"
            : string.Create(CultureInfo.InvariantCulture, $"{side} {e.Index} ({e.ThetaDeg:0.#}°, {e.PhiDeg:0.#}°)");
        foreach (var p in t.Pairs.Where(p => p.Rank is not null).Take(40))
            BeamRows.Add(new BeamRow(p.Rank!.Value, $"{End("tx", t.TxBeams[p.Tx], t.TxSteered)}   {End("rx", t.RxBeams[p.Rx], t.RxSteered)}",
                                     $"{p.MetricDb:0.00} dB", p.Tx, p.Rx, p.Tx == t.Chosen.Tx && p.Rx == t.Chosen.Rx));
        ChannelStatus = $"{beams.Channel}, {(beams.IsTx ? "transmit" : "receive")} end · drop {beams.Drop} · {t.Why} · bound {t.BoundDb:0.00} dB, " +
                        $"codebook loss {t.CodebookLossDb:0.00} dB · {t.PairsScored} of {t.PairsTotal} pairs ({t.Search})";
        Redraw?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The weakest ray the map draws, dB below the strongest.</summary>
    public const double RayFloorDb = 30;

    /// <summary>The rays the (u,v) map draws: this end's direction of each ray in the front half-space within <see cref="RayFloorDb"/> of
    /// the strongest, with its index in the drop's list and its size (0 at the floor, 1 the strongest).</summary>
    public IReadOnlyList<(int Ray, double U, double V, double Size)> MapRays()
    {
        if (Channel is not { Table: not null } c) return [];
        var list = new List<(int, double, double, double)>();
        int k = 0;
        foreach (var (th, ph, db) in c.Rays)
        {
            if (double.IsFinite(th) && th <= 90 && db >= -RayFloorDb)
            {
                double t = th * Math.PI / 180, p = ph * Math.PI / 180;
                list.Add((k, Math.Sin(t) * Math.Cos(p), Math.Sin(t) * Math.Sin(p), 1 + db / RayFloorDb));
            }
            k++;
        }
        return list;
    }

    /// <summary>The channel instance in the schematic.</summary>
    private EditableComponent? ChannelComponent()
        => Channel is { } c ? Schematic.EditModel.Components.FirstOrDefault(x => x.InstanceName == c.Channel) : null;

    /// <summary>A pair clicked: BeamSelect=index with the pair's codebook entries, one undo step.</summary>
    public void SelectPair(BeamRow row)
    {
        if (Channel?.Table is not { } t) return;
        WriteIndex(t.TxSteered ? (row.Tx + 1).ToString(CultureInfo.InvariantCulture) : null,
                   t.RxSteered ? (row.Rx + 1).ToString(CultureInfo.InvariantCulture) : null,
                   $"Select beam pair {row.Rank} on {Channel.Channel}");
    }

    /// <summary>A ray clicked: BeamSelect=index with the ray's own directions, one undo step.</summary>
    public void SelectRay(int k)
    {
        if (Channel?.Table is not { } t || k < 0 || k >= t.Rays.Count) return;
        var r = t.Rays[k];
        static string Dir(double th, double ph) => string.Create(CultureInfo.InvariantCulture, $"({Math.Round(th, 2):0.##},{Math.Round(ph, 2):0.##})");
        WriteIndex(t.TxSteered ? Dir(r.TxThetaDeg, r.TxPhiDeg) : null, t.RxSteered ? Dir(r.RxThetaDeg, r.RxPhiDeg) : null,
                   $"Select the beams of ray {k + 1} on {Channel.Channel}");
    }

    private void WriteIndex(string? tx, string? rx, string description)
    {
        if (ChannelComponent() is not { } ch) return;
        IUiCommand? cmd = AndThen(null, WriteOn(ch, "BeamSelect", "index"));
        cmd = tx is null ? AndThen(cmd, RemoveOn(ch, "TxBeam")) : AndThen(cmd, WriteOn(ch, "TxBeam", tx));
        cmd = rx is null ? AndThen(cmd, RemoveOn(ch, "RxBeam")) : AndThen(cmd, WriteOn(ch, "RxBeam", rx));
        cmd = AndThen(cmd, RemoveOn(ch, "BeamRank"));
        Schematic.Execute(new NamedCommand(description, cmd!));
    }

    /// <summary>True when the array ends a channel whose BeamSelect is not manual, so the run, not the array's steer(), sets its beam.</summary>
    private bool ChannelPicksTheBeams()
    {
        var ch = ChannelComponent() ?? (_session?.ChannelEnd() is var (inst, _) ? Schematic.EditModel.Components.FirstOrDefault(x => x.InstanceName == inst.InstanceName) : null);
        if (ch is null) return false;
        string word = ch.Parameters.FirstOrDefault(p => p.Name == "BeamSelect")?.Expression.Trim().Trim('"') ?? "";
        return !word.Equals("manual", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A drag's steering made to stick on a channel end: the OTHER end's chosen direction into its steer() (when the channel was scored),
    /// BeamSelect=manual and the index keys removed, after <paramref name="dragged"/> — Apply to array, with the dragged end's direction the
    /// user's own.
    /// </summary>
    private IUiCommand? SteerByHand(IUiCommand? dragged)
    {
        var ch = ChannelComponent() ?? (_session?.ChannelEnd() is var (inst, _) ? Schematic.EditModel.Components.FirstOrDefault(x => x.InstanceName == inst.InstanceName) : null);
        if (ch is null) return dragged;
        var model = Schematic.EditModel;
        IUiCommand? cmd = dragged;
        if (Channel?.Table is { } t)
            foreach (var (steered, array, beam) in new[] { (t.TxSteered, t.TxArray, t.TxBeams[t.Chosen.Tx]), (t.RxSteered, t.RxArray, t.RxBeams[t.Chosen.Rx]) })
            {
                if (!steered || array is null || array == Instance.InstanceName
                    || model.Components.FirstOrDefault(c => c.InstanceName == array) is not { } comp) continue;
                var steering = PhasedArraySteering.Of(comp, model.Components);
                if (!steering.Draggable) continue;
                double th = Math.Round(beam.ThetaDeg, 2), ph = Math.Round(beam.PhiDeg, 2);
                foreach (var (row, expr, unit) in steering.VarWrites(model.Components, th, ph))
                    cmd = AndThen(cmd, new EditParameterCommand(model, row, expr, unit));
                if (steering.Theta is { IsVar: false } || steering.Phi is { IsVar: false })
                    cmd = AndThen(cmd, WriteOn(comp, steering.Field, steering.Rewritten(comp.Parameters, th, ph)));
            }
        cmd = AndThen(cmd, WriteOn(ch, "BeamSelect", "manual"));
        foreach (string key in new[] { "TxBeam", "RxBeam", "BeamRank" }) cmd = AndThen(cmd, RemoveOn(ch, key));
        return cmd;
    }

    /// <summary>
    /// Apply to array: each steered end's chosen direction into its array's steer() (the VARs, or the literals, as a beam drag writes them)
    /// and BeamSelect=manual on the channel, its index keys removed — one undo step.
    /// </summary>
    [RelayCommand]
    public void ApplyBeam()
    {
        if (Channel?.Table is not { } t || ChannelComponent() is not { } ch) return;
        var model = Schematic.EditModel;
        IUiCommand? cmd = null;
        foreach (var (steered, array, beam) in new[] { (t.TxSteered, t.TxArray, t.TxBeams[t.Chosen.Tx]), (t.RxSteered, t.RxArray, t.RxBeams[t.Chosen.Rx]) })
        {
            if (!steered || array is null || model.Components.FirstOrDefault(c => c.InstanceName == array) is not { } comp) continue;
            var steering = PhasedArraySteering.Of(comp, model.Components);
            if (!steering.Draggable) continue;
            double th = Math.Round(beam.ThetaDeg, 2), ph = Math.Round(beam.PhiDeg, 2);
            foreach (var (row, expr, unit) in steering.VarWrites(model.Components, th, ph))
                cmd = AndThen(cmd, new EditParameterCommand(model, row, expr, unit));
            if (steering.Theta is { IsVar: false } || steering.Phi is { IsVar: false })
                cmd = AndThen(cmd, WriteOn(comp, steering.Field, steering.Rewritten(comp.Parameters, th, ph)));
        }
        cmd = AndThen(cmd, WriteOn(ch, "BeamSelect", "manual"));
        foreach (string key in new[] { "TxBeam", "RxBeam", "BeamRank" }) cmd = AndThen(cmd, RemoveOn(ch, key));
        Schematic.Execute(new NamedCommand($"Apply {Channel.Channel}'s beam to the array", cmd!));
    }

    private IUiCommand WriteOn(EditableComponent comp, string name, string expression)
    {
        var model = Schematic.EditModel;
        if (comp.Parameters.FirstOrDefault(p => p.Name == name) is { } row) return new EditParameterCommand(model, row, expression, row.Unit);
        return new AddParameterCommand(model, comp, new EditableParameter { Name = name, Expression = expression, ShowOnSchematic = false });
    }

    /// <summary>The command removing <paramref name="name"/> from <paramref name="comp"/>, or null when it has none.</summary>
    private IUiCommand? RemoveOn(EditableComponent comp, string name)
        => comp.Parameters.FirstOrDefault(p => p.Name == name) is { } row ? new RemoveParameterCommand(Schematic.EditModel, comp, row) : null;

    private static IUiCommand? AndThen(IUiCommand? first, IUiCommand? next) => next is null ? first : first is null ? next : new CompositeCommand(first, next);
}

/// <summary>Progress observed on a worker, delivered on the UI thread through the tool's host.</summary>
internal sealed class PostedProgress(IPhasedArrayHost host, Action<CircuitRF.Engine.RunProgress> show) : IProgress<CircuitRF.Engine.RunProgress>
{
    public void Report(CircuitRF.Engine.RunProgress value) => host.Post(() => show(value));
}
