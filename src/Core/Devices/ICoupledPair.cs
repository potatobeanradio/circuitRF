using System.Numerics;

namespace CircuitRF.Core.Devices;

/// <summary>
/// A symmetric coupled pair stamped by its even and odd modes (<see cref="CLineModel.StampModalPair"/>), as the four numbers
/// that define it at one frequency: each mode's per-line characteristic impedance (Ω, real) and its γ·l over the pair's length.
/// Read where the pair is written some other way than its nodal stamp (the SPICE deck writer's coupled lossy line, brief TRN-6
/// R-trn6-9); the model's own <c>Stamp</c> is unchanged. Nets: A near, A far, B near, B far.
/// </summary>
public interface ICoupledPair
{
    (double Ze, Complex GammaLengthEven, double Zo, Complex GammaLengthOdd) PairAt(double freqHz);
}
