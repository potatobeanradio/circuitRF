using System.Globalization;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Systems;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Synthesis.Filters;

/// <summary>
/// A four-pole quasi-elliptic bandpass filter as one MCRC, Hong and Lancaster's cross-coupled square of open loops
/// (docs/design/filter-synthesis.md §4.1; J.-S. Hong and M. J. Lancaster, IEEE Trans. MTT 44(11), 1996,
/// <see href="https://doi.org/10.1109/22.543968">10.1109/22.543968</see>): an equiripple passband with one transmission
/// zero either side, at mirror frequencies. The folded 4–2 matrix of mirror zeros (<see cref="CouplingMatrix"/>) holds
/// k12 = k34, k23 and k14, nothing else, and the loop k12·k23·k34·k14 is negative; the square draws exactly that, k14
/// electric across the openings of loops 1 and 4 (<c>Sx</c>) against k23 magnetic between the backs of 2 and 3 (S3).
/// The gaps and the tap are solved on the part's own circuit model (<see cref="CoupledResonatorDesign.FromCouplings"/>).
/// </summary>
public sealed class CrossCoupledLoopRealiser : IFilterRealiser
{
    public string Name => "Open-loop bandpass, cross-coupled (MCRC)";

    public bool Supports(FilterSpec spec, out string? why)
    {
        why = Check(spec, out _)?.Message;
        return why is null;
    }

    public FilterRealisation Realise(FilterSpec spec, MicrostripSubstrate? substrate) => Realise(spec, substrate, default);

    public FilterRealisation Realise(FilterSpec spec, MicrostripSubstrate? substrate, CancellationToken cancel)
    {
        if (Check(spec, out double wz) is { } refusal) throw refusal;
        var s = substrate ?? throw Refusal("substrate", "an MCRC is microstrip and needs its substrate.");
        double f0 = spec.CentreHz, fbw = spec.FractionalBandwidth;
        double returnLoss = -10.0 * Math.Log10(1.0 - Math.Pow(10.0, -spec.RippleDb / 10.0));
        var p = CouplingMatrix.ToPhysical(CouplingMatrix.Synthesise(4, returnLoss, [-wz, wz]), f0, fbw);
        // The square's four couplings; what else the folded matrix holds (M13, M24, the diagonal) is rounding.
        var k = new double[4, 4];
        foreach (var (i, j) in new[] { (0, 1), (1, 2), (2, 3), (0, 3) }) k[i, j] = k[j, i] = p.K[i, j];
        double w = CoupledResonatorDesign.Width(spec.Z0, f0, s);
        var form = new Dictionary<string, object>
        {
            ["W"] = w, ["Gap"] = w, ["Shape"] = nameof(ResonatorShape.OpenLoop), ["Feed"] = nameof(ResonatorFeed.Tapped),
            ["Arrangement"] = nameof(LoopArrangement.Cross), ["Profile"] = nameof(ResonatorProfile.Uniform),
            ["Form"] = nameof(ResonatorForm.Bandpass), ["Coupling"] = nameof(ResonatorCoupling.Pairs),
        };
        var v = CoupledResonatorDesign.FromCouplings(form, k, p.QeSource, f0, s, cancel: cancel);
        double half = wz * fbw / 2.0, root = Math.Sqrt(1.0 + half * half);
        string G3(double x) => x.ToString("G3", CultureInfo.InvariantCulture);
        string note = $"Folded 4–2 matrix at {G3(returnLoss)} dB return loss, zeros at ω = ±{G3(wz)} ({G3(f0 * (root - half) / 1e9)} "
                    + $"and {G3(f0 * (root + half) / 1e9)} GHz): k12 = k34 = {G3(Math.Abs(k[0, 1]))}, k23 = {G3(Math.Abs(k[1, 2]))}, "
                    + $"k14 = {G3(Math.Abs(k[0, 3]))} of the opposite sign, Qe = {G3(p.QeSource)}; each gap and the tap solved on the "
                    + "circuit model (MCLIN pairs; the square's diagonal and the corners' coupling left out).";
        return new FilterRealisation(ComponentModelFactory.CoupledResonatorTypeName, CoupledResonatorRealiser.Stored(v), [], [note]);
    }

    /// <summary>Why the square cannot build <paramref name="spec"/>, or null; <paramref name="wz"/> is then its zeros'
    /// normalised frequency, ω = (f/f0 − f0/f)/FBW, the mean of the two asked for.</summary>
    private static ParameterRefusalException? Check(FilterSpec spec, out double wz)
    {
        wz = 0.0;
        double fbw = spec.FractionalBandwidth;
        if (spec.Band != FilterBand.Bandpass)
            return Refusal("band", "the cross-coupled square is a bandpass filter; a {band} filter is another family's.", ("band", spec.Band));
        if (spec.Response != FilterResponse.Chebyshev)
            return Refusal("response", "the square's passband is equiripple, a Chebyshev response with the zeros asked for; "
                + "{response} is not.", ("response", spec.Response));
        if (spec.Order != 4)
            return Refusal("order", "the square is four resonators; order {n} is not.", ("n", spec.Order));
        if (!(fbw > 0.0 && fbw <= CoupledResonatorRealiser.MaxBandwidth))
            return Refusal("bandwidth", "a fractional bandwidth of {fbw} is outside these narrow-band designs' (0, {max}].",
                ("fbw", fbw), ("max", CoupledResonatorRealiser.MaxBandwidth));
        double f0 = spec.CentreHz;
        double[] w = [.. (spec.TransmissionZerosHz ?? []).Select(f => (f / f0 - f0 / f) / fbw).Order()];
        if (w is not [< -1.0 and var lo, > 1.0 and var hi] || Math.Abs(hi + lo) > 0.05 * (hi - lo) / 2.0)
            return Refusal("zeros", "the square places two transmission zeros, one either side of the band at mirror "
                + "frequencies (f1·f2 = f0² to 5 %); {zeros} Hz are not.", ("zeros", string.Join(", ", spec.TransmissionZerosHz ?? [])));
        wz = (hi - lo) / 2.0;
        return null;
    }

    private static ParameterRefusalException Refusal(string code, string message, params (string, object?)[] args)
        => new(Diagnostic.Create("synthesis.xc." + code, DiagnosticSeverity.Error, "Cross-coupled synthesis: " + message, args));
}
