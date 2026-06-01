using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Two-terminal resistor. Group 1: AddAdmittance(a, b, G) where G = 1/R.
///
/// Non-physical inputs (warn-and-continue — circuitRF research-tool philosophy):
///   R &lt; 0 : stamps 1/R with its sign (models an active/negative-resistance element). Warns once.
///   R = 0 : stamps Gmax (near-short conductance). Warns once.
/// </summary>
public sealed class ResistorModel : ComponentModel
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    /// <summary>
    /// Conductance ceiling for R=0 near-short substitution.
    /// Dual of AnalysisSettings.Gmin. Matches AnalysisSettings.Default.Gmax.
    /// </summary>
    public const double DefaultGmax = 1e12; // S

    // Deduplication: warn once per component instance, not once per frequency sweep.
    private bool _warned;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        double r = c.Parameters["R"].AsReal();

        double g;
        if (r == 0.0)
        {
            if (!_warned)
            {
                Console.Error.WriteLine(
                    $"[circuitRF] R:{c.InstancePath}: R=0 Ω — stamping Gmax={DefaultGmax:G4} S " +
                    "as a near-short; proceeding. (Set R to a small positive value to suppress.)");
                _warned = true;
            }
            g = DefaultGmax;
        }
        else if (r < 0.0)
        {
            if (!_warned)
            {
                Console.Error.WriteLine(
                    $"[circuitRF] R:{c.InstancePath}: R={r:G4} Ω < 0 — non-physical/active element; " +
                    "stamping 1/R with its sign and proceeding.");
                _warned = true;
            }
            g = 1.0 / r;   // negative conductance — intentional
        }
        else
        {
            g = 1.0 / r;
        }

        mna.AddAdmittance(c.Nodes[0], c.Nodes[1], new Complex(g, 0));
    }
}
