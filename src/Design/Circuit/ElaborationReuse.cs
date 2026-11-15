// brief-paa-8b R-paa8b-2 / R-paa8b-3 — the elaborated netlist a circuit keeps so that an evaluation whose tuned values reach only a
// Feed-mode array's weights re-weighs it (Elaborator.Reweigh) instead of elaborating the bench again.
//
// ONE EVALUATION AT A TIME. A re-weighed netlist shares every component but the beamformers with the kept one, and a model writes state
// while it stamps, so two runs on them at once would race: an evaluation holds the reuse for its whole run (TryHold … Release), and a
// second evaluation that finds it held elaborates on its own, as before. Kept only after a full elaboration whose request set nothing
// but tunables (no --set, no statistics), whose plan solves no EM and whose parts release nothing when disposed — the kept and the run
// netlists share parts, and the run's is disposed when it ends.

using System.Diagnostics.CodeAnalysis;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Design.Circuit;

/// <summary>R-paa8b-2 — the kept netlist of a circuit's last full elaboration, and the tuned values it was made at.</summary>
internal sealed class ElaborationReuse
{
    private int _held;
    private ElaboratedNetlist? _kept;
    private IReadOnlyDictionary<string, string> _keptAt = new Dictionary<string, string>();
    private bool? _keptEm;
    private long _reweighs;

    /// <summary>Evaluations that re-weighed instead of elaborating.</summary>
    public long Reweighs => Interlocked.Read(ref _reweighs);

    /// <summary>Takes the reuse for one evaluation; false when another evaluation holds it.</summary>
    public bool TryHold() => Interlocked.CompareExchange(ref _held, 1, 0) == 0;

    public void Release() => Volatile.Write(ref _held, 0);

    /// <summary>The re-weighed netlist for <paramref name="tb"/> at <paramref name="tunables"/>, or null (and why) when the bench must be
    /// elaborated: nothing kept, another request shape, or a changed value that reaches more than the weights.</summary>
    public bool TryReweigh(CircuitEvaluationRequest request, IReadOnlyDictionary<string, string> tunables, TestBench tb, Library lib,
                           string? baseDirectory, [NotNullWhen(true)] out ElaboratedNetlist? nl)
    {
        nl = null;
        if (_kept is null || request.Sets.Count > 0 || request.Statistics is not null || request.Em != _keptEm) return false;
        var changed = tunables.Keys.Concat(_keptAt.Keys).Distinct(StringComparer.Ordinal)
                              .Where(k => !string.Equals(tunables.GetValueOrDefault(k), _keptAt.GetValueOrDefault(k), StringComparison.Ordinal))
                              .ToList();
        if (changed.Count == 0 || PhasedArrayReweigh.Refusal(tb, lib, changed) is not null) return false;
        try { nl = new Elaborator(lib) { BaseDirectory = baseDirectory }.Reweigh(_kept, tb); }
        catch (Exception) { return false; }   // the elaboration then says whatever is wrong, as it always has
        Interlocked.Increment(ref _reweighs);
        return true;
    }

    /// <summary>Keeps <paramref name="nl"/>, just elaborated at <paramref name="tunables"/>, when a re-weigh could start from it.</summary>
    public void Keep(ElaboratedNetlist nl, CircuitEvaluationRequest request, IReadOnlyDictionary<string, string> tunables, bool hasEm)
    {
        bool eligible = !hasEm && request.Sets.Count == 0 && request.Statistics is null
                        && nl.Components.Any(c => c.Model is PhasedArrayModel { Feed: not null } && !c.InstancePath.Contains('.'))
                        && !nl.Components.Any(c => c.Model is IDisposable);
        _kept = eligible ? nl.Copy() : null;
        _keptAt = new Dictionary<string, string>(tunables, StringComparer.Ordinal);
        _keptEm = request.Em;
    }
}
