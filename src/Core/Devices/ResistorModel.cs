using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Two-terminal resistor. Group 1: AddAdmittance(a, b, G) where G = 1/R.
///
/// Non-physical inputs (warn-and-continue — circuitRF research-tool philosophy):
///   R &lt; 0 : stamps 1/R with its sign (models an active/negative-resistance element). Warns once.
///   R = 0 : stamps Gmax (near-short conductance). Warns once.
///
/// <para><b>Temperature is a MULTIPLIER resolved at construction, not a parameter read at stamp
/// time.</b> The stated resistance is what the value carries; the temperature factor
/// <c>1 + TC1·ΔT + TC2·ΔT²</c> is folded in once, because the ambient a device is evaluated at is
/// known at elaboration and nowhere else — a resistor reading <c>c.Parameters</c> during
/// <see cref="Stamp"/> has no way to see it. A sweep over the ambient re-elaborates every point, so
/// resolving here loses nothing.</para>
///
/// <para><b>A polynomial, not a junction relation.</b> A resistor's temperature dependence is a
/// fitted curve, which is a different shape from the exponential physics a junction obeys — see
/// <see cref="Temperature.PolynomialScale"/>. Reaching for the junction relations here would be
/// borrowing device physics for something that has none.</para>
/// </summary>
public sealed class ResistorModel : ComponentModel, IReportsWarnings
{
    private readonly double _temperatureFactor;

    /// <param name="temperatureFactor">
    /// What the stated resistance is multiplied by. Defaults to exactly 1, so a resistor built
    /// without one is bit-identical to a resistor built before this existed.
    /// </param>
    public ResistorModel(double temperatureFactor = 1.0)
        => _temperatureFactor = temperatureFactor;

    /// <summary>brief TRN-6 — what the stated resistance is multiplied by (TC1/TC2 at the resistor's temperature).</summary>
    public double TemperatureFactor => _temperatureFactor;

    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    /// <summary>
    /// Conductance ceiling for R=0 near-short substitution.
    /// Dual of AnalysisSettings.Gmin. Matches AnalysisSettings.Default.Gmax.
    /// </summary>
    public const double DefaultGmax = 1e12; // S

    // Deduplication: warn once per component instance, not once per frequency sweep. The message is
    // QUEUED, not printed: a model has no netlist to reach, so the engine drains it after the stamp
    // into ElaboratedNetlist.AddWarningOnce, keyed per instance path. That key is what makes the
    // frequency-parallel path's separately elaborated copies (SP-P3) report the notice once rather
    // than once per copy — a direct Console.Error write from here printed it three times on a
    // 251-point sweep (src/Engine/RESOLVED.md, WSP-1).
    private bool _warned;
    private readonly List<(string Key, string Message)> _pending = [];

    public IReadOnlyList<(string Key, string Message)> DrainWarnings()
    {
        if (_pending.Count == 0) return [];
        var drained = _pending.ToArray();
        _pending.Clear();
        return drained;
    }

    /// <summary>
    /// <see cref="Core.Activity.ActiveExact"/>, unconditionally at the TYPE level, because a
    /// resistor with <c>R &lt; 0</c> is exactly the "negative resistance … rendered passive" of
    /// the reference document's §8 (p. 111) and the class cannot know its own value here.
    ///
    /// <para>The EFFECT is conditional and costs nothing: for an ordinary positive resistor
    /// <see cref="StampPassive"/> stamps the same conductance <see cref="Stamp"/> does, so the
    /// active and passive assemblies differ in no entry and the resistor contributes no column to
    /// the determinant ratio at all. <see cref="ActivityFor"/> says so for the PLACED instance,
    /// which is where the value is known.</para>
    /// </summary>
    public override Activity Activity => Activity.ActiveExact;

    /// <summary>
    /// The placed instance's answer: <see cref="Core.Activity.Passive"/> when its resolved
    /// <c>R ≥ 0</c>, because then <see cref="StampPassive"/> and <see cref="Stamp"/> are the same
    /// stamp and the resistor is not carrying activity to passivate at all. Only a NEGATIVE
    /// resistor is the document's §8 negative resistance.
    ///
    /// <para>This is the hook's own purpose — "upgrade its conservative type-level answer to
    /// Passive once it has measured itself" (<see cref="ComponentModel.ActivityFor"/>) — and
    /// without it every resistor in a design was reported as active by
    /// <c>explain --analysis</c>, whose listing exists to skip exactly that
    /// ("a hundred resistors saying passive is noise") and whose "N carrying activity to
    /// passivate" count was therefore the resistor count plus the active devices. Nothing
    /// numerical moves: the passive assembly is built from <see cref="StampPassive"/> either way,
    /// the per-frequency passivity guard has nothing to check on a positive resistor, and the
    /// determinant lemma's column list is a PREFERENCE that already drops columns which do not
    /// differ.</para>
    /// </summary>
    public override Activity ActivityFor(ElaboratedComponent c, IReadOnlyList<double> freqsHz)
        => c.Parameters.TryGetValue("R", out var r)
        && r.Kind == Expressions.ValueKind.Real
        && r.AsReal() * _temperatureFactor >= 0.0     // the same product Stamp branches on
            ? Activity.Passive
            : Activity.ActiveExact;

    /// <inheritdoc/>
    public override string? PassivationNote =>
        "R → |R|, which is its ordinary stamp unless R < 0";

    /// <summary><c>R → |R|</c> (Eq. 181's <c>Y_passive</c>; §8 p. 111). Identical to
    /// <see cref="Stamp"/> for every resistor that was already passive.</summary>
    public override void StampPassive(IMnaContext mna, ElaboratedComponent c, double omega)
        => StampConductance(mna, c, passive: true);

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
        => StampConductance(mna, c, passive: false);

    private void StampConductance(IMnaContext mna, ElaboratedComponent c, bool passive)
    {
        double r = c.Parameters["R"].AsReal() * _temperatureFactor;

        double g;
        if (r == 0.0)
        {
            if (!_warned && !passive)
            {
                _pending.Add(($"resistor.short:{c.InstancePath}",
                    $"R:{c.InstancePath}: R=0 Ω — stamping Gmax={DefaultGmax:G4} S " +
                    "as a near-short; proceeding. (Set R to a small positive value to suppress.)"));
                _warned = true;
            }
            g = DefaultGmax;
        }
        else if (r < 0.0)
        {
            if (!_warned && !passive)
            {
                _pending.Add(($"resistor.negative:{c.InstancePath}",
                    $"R:{c.InstancePath}: R={r:G4} Ω < 0 — non-physical/active element."));
                _warned = true;
            }
            // The passive assembly takes |R| — the document's "negative resistances … rendered
            // passive" (§8, p. 111). Everywhere else the sign is stamped as written, deliberately.
            g = passive ? 1.0 / -r : 1.0 / r;   // negative conductance — intentional
        }
        else
        {
            g = 1.0 / r;
        }

        mna.AddAdmittance(c.Nodes[0], c.Nodes[1], new Complex(g, 0));
    }
}
