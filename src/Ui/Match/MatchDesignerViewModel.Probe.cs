using System;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Core.Matching;
using CircuitRF.Engine;
using CircuitRF.Engine.Matching;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Matching;

/// <summary>
/// MN-4 — the Probe button: looking outward from a pin into the circuit the <c>Match</c> is placed in
/// (match.md §10).
/// </summary>
/// <remarks>
/// <b>Off the UI thread, with cancel.</b> A probe is a real S-parameter sweep, and on the case this
/// feature exists for — a biased transistor — it is a real DC solve first. It runs on a worker with
/// the engine's own <see cref="RunControl"/> carrying progress and a cancellation token, exactly as an
/// ordinary Run does.
///
/// <para><b>Nothing here decides an impedance.</b> The measurement, the four fits and the ranking are
/// <see cref="TerminationProbe"/>'s, in <c>src/Engine</c>. This class decides when to run one, what to
/// say while it runs, and what to write back when it finishes.</para>
/// </remarks>
public sealed partial class MatchDesignerViewModel
{
    private CancellationTokenSource? _probeCts;

    /// <summary>True while a probe is running; both buttons are held down for the duration.</summary>
    [ObservableProperty] private bool _isProbing;

    /// <summary>What the probe is doing, for the inline progress row.</summary>
    [ObservableProperty] private string _probeStatus = "";

    /// <summary>Probe progress, 0..1, or -1 when there is no honest denominator.</summary>
    [ObservableProperty] private double _probeProgress;

    /// <summary>Cancels a running probe. The engine answers at the next frequency point.</summary>
    public IRelayCommand CancelProbeCommand => _cancelProbeCommand ??=
        new RelayCommand(() => _probeCts?.Cancel(), () => IsProbing);
    private IRelayCommand? _cancelProbeCommand;

    partial void OnIsProbingChanged(bool value)
    {
        CancelProbeCommand.NotifyCanExecuteChanged();
        Term1.RefreshProbeState();
        Term2.RefreshProbeState();
    }

    // ── Availability (match.md §10.4) ─────────────────────────────────────────

    /// <summary>
    /// Re-answers §10.4 for both ends. Called whenever the schematic could have changed underneath —
    /// never on a slider drag, which cannot change what is wired to a pin.
    /// </summary>
    internal void RefreshProbeAvailability()
    {
        Term1.Availability = Evaluate(0);
        Term2.Availability = Evaluate(1);
    }

    private MatchProbeAvailability Evaluate(int pinIndex) =>
        MatchProbeAvailability.Evaluate(
            _schematicVm?.EditModel, _schematicVm?.CellResolver, InstanceName, pinIndex);

    // ── The run ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs one probe and, when it succeeds, applies the winning fit to that termination.
    /// </summary>
    /// <param name="end">1 or 2.</param>
    internal async Task ProbeAsync(int end)
    {
        if (IsProbing) return;

        var term = end == 1 ? Term1 : Term2;
        var availability = Evaluate(end - 1);
        term.Availability = availability;
        if (!availability.CanProbe || availability.Bench is null)
        {
            term.ProbeError = availability.Reason;
            return;
        }

        int points = Math.Clamp(_design.PlotPoints, 2, 20001);
        double f1 = _design.F1, f2 = _design.F2;
        bool conjugate = end == 1 ? _design.Term1Conjugate : _design.Term2Conjugate;
        double warn = Settings.ProbeResidualWarning;
        // The same base directory a Run uses, so a file-backed model (SnP, a kit) resolves the
        // same way here as it does there.
        string? baseDir = _schematicVm?.WorkspaceRoot;
        var bench = availability.Bench;
        var lib = availability.Library;
        string instance = InstanceName;
        int pinIndex = end - 1;

        term.ProbeError = "";
        IsProbing = true;
        ProbeStatus = $"Probing {instance} pin {end}…";
        ProbeProgress = 0;

        _probeCts?.Dispose();
        _probeCts = new CancellationTokenSource();
        // Progress<T> captures the UI context here, so the engine's own worker-thread reports arrive
        // back on the thread that owns these properties.
        var progress = new Progress<RunProgress>(p =>
        {
            ProbeProgress = p.Total > 0 ? (double)p.Completed / p.Total : -1;
            if (p.Stage.Length > 0) ProbeStatus = p.Stage;
        });
        var control = new RunControl
        {
            Token = _probeCts.Token,
            Progress = progress,
            Total = points,
        };

        TerminationProbe.ProbeResult? result = null;
        string? failure = null;
        try
        {
            result = await Task.Run(() => TerminationProbe.Probe(
                bench, instance, pinIndex, f1, f2, points, conjugate,
                lib, baseDir, warn, control: control), _probeCts.Token);
        }
        catch (OperationCanceledException)
        {
            failure = "Probe cancelled. Nothing was changed.";
        }
        catch (Exception ex)
        {
            // A probe is a measurement, not a render pass: a failure is reported where the user asked
            // for the number, and the design it would have overwritten is left exactly as it was.
            failure = $"The probe failed: {ex.Message}";
        }
        finally
        {
            IsProbing = false;
            ProbeStatus = "";
            ProbeProgress = 0;
        }

        if (failure is not null) { term.ProbeError = failure; return; }
        term.ShowProbeResult(result!);
    }

    /// <summary>Sets one end's conjugate toggle and commits it with the rest of the design.</summary>
    internal void SetConjugate(int end, bool conjugate)
    {
        if (end == 1) _design.Term1Conjugate = conjugate; else _design.Term2Conjugate = conjugate;
        Term1.Refresh();
        Term2.Refresh();
        Commit();
    }

    /// <summary>
    /// Writes one probed termination back. Separate from <see cref="SetTermination"/> so a probe can
    /// SET the provenance while every other path clears it — match.md §10.5's "the user's override
    /// always wins".
    /// </summary>
    internal void ApplyProbedTermination(int end, Termination probed)
    {
        ArgumentNullException.ThrowIfNull(probed);
        SetTermination(end, probed, fromProbe: true);
    }
}
