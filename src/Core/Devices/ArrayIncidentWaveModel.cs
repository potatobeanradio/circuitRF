// brief-paa-7 R-paa7-1 — one plane wave arriving at a Phased Antenna Array, as a tone source on the array's element sources.
//
// Each element's wave equation is b = S·a + c with c the voltage on that element's source node (a 1 S conductance the array stamps), so a current
// c_n injected there puts the received wave c_n on element n (ArrayReceive.Waves, note eq. (4)). It is a ToneSourceModelBase, one tone at the
// wave's frequency, so every engine treats it as it treats an I_nTone: on the harmonic-balance grid and checked for being on it, zeroed for a
// linear extraction and in a small-signal run, scaled by a drive ramp, noiseless, and a carrier in an envelope run. A signal and an interferer are
// two of these, and pass through the channels' amplifiers together.

using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>brief-paa-7 R-paa7-1 — an incident wave on a Phased Antenna Array's elements: its nodes are the elements' source nodes.</summary>
public sealed class ArrayIncidentWaveModel : ToneSourceModelBase
{
    private readonly Complex[] _waves;

    /// <param name="hz">The wave's frequency.</param>
    /// <param name="waves">The wave it puts on each element (peak, √W), in element order.</param>
    public ArrayIncidentWaveModel(double hz, Complex[] waves)
        : base([new ToneEntry(hz, Complex.One, 0, null, null, 1, 1, new Dictionary<string, Value>())], 0, null, null)
    {
        _waves = waves;
        FrequencyHz = hz;
    }

    public double FrequencyHz { get; }

    /// <summary>The wave on each element.</summary>
    public IReadOnlyList<Complex> Waves => _waves;

    public override int PortCount => _waves.Length;

    protected override string DcParamName => "RxPd";

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        Complex j = ExcitationAt(omega);
        if (j == Complex.Zero) return;
        for (int e = 0; e < _waves.Length && e < c.Nodes.Length; e++)
            if (c.Nodes[e] > 0 && _waves[e] != Complex.Zero) mna.AddCurrentInjection(c.Nodes[e], j * _waves[e]);
    }
}
