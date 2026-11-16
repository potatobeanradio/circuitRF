using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using Xunit;
using Xunit.Abstractions;
using static CircuitRF.Engine.Tests.Devices.LadderSParamTests;

namespace CircuitRF.Engine.Tests.Synthesis;

/// <summary>
/// The ladder realisers (docs/design/filter-synthesis.md §3): each one's MLAD, placed as it stores its parameters and
/// run through the engine, does what its textbook design says: the cutoff within 5 %, the band's centre within 2 %, the
/// notch at F. Each refusal is coded.
/// </summary>
public class LadderRealiserTests(ITestOutputHelper output)
{
    private const string Alumina = "H=0.635e-3 T=5e-6 Er=9.8 Sigma=4.1e7 TanD=0.0001";
    private static readonly MicrostripSubstrate AluminaS = new(0.635e-3, 5e-6, 9.8, 4.1e7, 0.0001);

    /// <summary>|S21| in dB of the realisation as one placed MLAD between two 50 Ω ports, its G pins on ground.</summary>
    private Func<double, double> Run(IFilterRealiser r, FilterSpec spec, MicrostripSubstrate? s = null, string sub = Sub)
    {
        var p = r.Realise(spec, s ?? Ro4350b).Parameters;
        Assert.Equal(p["Sections"], LadderPlan.Format(LadderPlan.Parse(p["Sections"])));
        output.WriteLine($"{r.Name}: Z0={p["Z0"]} F={p["F"]}\n{p["Sections"]}");
        string grounds = string.Concat(Enumerable.Repeat(" 0", LadderPlan.Grounds(p["Sections"])));
        return S21Db($"{Ports(50, 50)}\nMLAD:X1 p1 p2{grounds} Sections=\"{p["Sections"]}\" Z0={p["Z0"]} F={p["F"]} {sub}\n");
    }

    private static FilterSpec Lowpass(FilterResponse response, int n, double fc, IReadOnlyList<double>? zeros = null)
        => new(response, FilterBand.Lowpass, n, fc, fc, TransmissionZerosHz: zeros);

    private static FilterSpec Band(FilterBand band, FilterResponse response, int n, double f0, double fbw)
        => new(response, band, n, f0 * (1 - fbw / 2), f0 * (1 + fbw / 2));

    /// <summary>The stepped-impedance lowpass, N = 6 Butterworth at 2.5 GHz (Pozar's example), on the default
    /// impedances, the widest and narrowest lines 20 mil RO4350B gives: −3 dB at fc to 5 %.</summary>
    [Fact]
    public void SteppedImpedance_CutsOffAtFc()
    {
        const double fc = 2.5e9;
        var s21 = Run(new LadderSteppedImpedanceRealiser(), Lowpass(FilterResponse.Butterworth, 6, fc));
        double f3 = Crossing(s21, 0.5 * fc, 1.5 * fc, -3.0);
        output.WriteLine($"-3 dB at {f3 / 1e9:F4} GHz ({(f3 / fc - 1) * 100:F2} %)");
        Assert.InRange(f3 / fc, 0.95, 1.05);
    }

    /// <summary>The Richards-Kuroda open-stub lowpass at 2.4 GHz: N = 3 Butterworth (Pozar's form: stubs of 2·Z0, Z0/2 and
    /// 2·Z0, unit elements of 2·Z0) and N = 5, from the shunt-first form with Kuroda's identities worked further in:
    /// −3 dB at fc to 5 %.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void KurodaStubs_CutOffAtFc(int n)
    {
        const double fc = 2.4e9;
        var spec = Lowpass(FilterResponse.Butterworth, n, fc);
        double f3 = Crossing(Run(new LadderStubLowpassRealiser(), spec), 0.5 * fc, 1.5 * fc, -3.0);
        output.WriteLine($"-3 dB at {f3 / 1e9:F4} GHz ({(f3 / fc - 1) * 100:F2} %)");
        Assert.InRange(f3 / fc, 0.95, 1.05);
        // N = 5's end stubs are 181 Ω, a few microns on a laminate: a note says so.
        Assert.Equal(n == 5, new LadderStubLowpassRealiser().Realise(spec, Ro4350b).Notes.Any(x => x.Contains("H/4")));
    }

    /// <summary>The stub lowpass with a zero at 1.3·fc, N = 3 Butterworth at 2.4 GHz: the zero where its stub is a quarter
    /// wave, to 5 %, and the passband passes. MTEE's own junction moves the zero a few per cent up on this board.</summary>
    [Fact]
    public void StubZeros_PutTheZeroWhereAsked()
    {
        const double fc = 2.4e9, fInf = 1.3 * fc;
        var s21 = Run(new LadderStubZeroLowpassRealiser(), Lowpass(FilterResponse.Butterworth, 3, fc, [fInf]));
        double zero = Minimum(s21, 1.1 * fc, 1.6 * fc);
        output.WriteLine($"zero at {zero / 1e9:F4} GHz ({(zero / fInf - 1) * 100:F2} %), S21 {s21(zero):F1} dB; "
                       + $"at fc/2 {s21(fc / 2):F2} dB");
        Assert.InRange(zero / fInf, 0.95, 1.05);
        Assert.True(s21(zero) < -30.0);
        Assert.True(s21(fc / 2) > -1.0);
    }

    /// <summary>Matthaei's shorted-stub bandpass, N = 3 Butterworth over Δ = 1 at 2.4 GHz: the −3 dB band's centre is F to
    /// 2 %.</summary>
    [Fact]
    public void ShortedStubBandpass_IsCentredOnF()
    {
        const double f0 = 2.4e9;
        var s21 = Run(new LadderShortedStubBandpassRealiser(), Band(FilterBand.Bandpass, FilterResponse.Butterworth, 3, f0, 1.0));
        double lo = Crossing(s21, 0.3 * f0, f0, -3.0), hi = Crossing(s21, f0, 1.7 * f0, -3.0);
        output.WriteLine($"-3 dB {lo / 1e9:F4} to {hi / 1e9:F4} GHz, centre {(lo + hi) / 2e9:F4} GHz");
        Assert.InRange((lo + hi) / 2.0 / f0, 0.98, 1.02);
    }

    /// <summary>The quarter-wave stub bandstop, N = 3 Butterworth over Δ = 0.5 at 2.4 GHz: the notch at F to 1 %, under
    /// −40 dB.</summary>
    [Fact]
    public void StubBandstop_NotchesAtF()
    {
        const double f0 = 2.4e9;
        var s21 = Run(new LadderStubBandstopRealiser(), Band(FilterBand.Bandstop, FilterResponse.Butterworth, 3, f0, 0.5));
        double notch = Minimum(s21, 0.8 * f0, 1.2 * f0);
        output.WriteLine($"notch {notch / 1e9:F4} GHz ({(notch / f0 - 1) * 100:F2} %), {s21(notch):F1} dB");
        Assert.InRange(notch / f0, 0.99, 1.01);
        Assert.True(s21(notch) < -40.0);
    }

    /// <summary>Cohn's end-coupled bandpass, N = 3 Butterworth over Δ = 0.03 at 10 GHz on 25 mil alumina, where gaps reach
    /// the outer couplings: the −3 dB band about the peak is centred on F to 2 %.</summary>
    [Fact]
    public void EndCoupled_IsCentredOnF()
    {
        const double f0 = 10e9;
        var s21 = Run(new LadderEndCoupledRealiser(), Band(FilterBand.Bandpass, FilterResponse.Butterworth, 3, f0, 0.03),
            AluminaS, Alumina);
        double peak = Minimum(x => -s21(x), 0.95 * f0, 1.05 * f0), top = s21(peak);
        double lo = Crossing(s21, 0.9 * f0, peak, top - 3.0), hi = Crossing(s21, peak, 1.1 * f0, top - 3.0);
        output.WriteLine($"peak {top:F2} dB; -3 dB {lo / 1e9:F4} to {hi / 1e9:F4} GHz, centre {(lo + hi) / 2e9:F4} GHz");
        Assert.InRange((lo + hi) / 2.0 / f0, 0.98, 1.02);
    }

    private const FilterResponse Bw = FilterResponse.Butterworth;

    public static TheoryData<string, IFilterRealiser, FilterSpec, bool> Refusals => new()
    {
        { "unsupported", new LadderSteppedImpedanceRealiser(), Lowpass(FilterResponse.Chebyshev, 4, 2.4e9), true },
        { "no-substrate", new LadderStubLowpassRealiser(), Lowpass(Bw, 3, 2.4e9), false },
        { "impedance-order", new LadderSteppedImpedanceRealiser { ZHigh = 40 }, Lowpass(Bw, 3, 2.4e9), true },
        { "zero-count", new LadderStubZeroLowpassRealiser(), Lowpass(Bw, 5, 2.4e9, [3e9, 3.5e9, 4e9]), true },
        { "zero-in-band", new LadderStubZeroLowpassRealiser(), Lowpass(Bw, 3, 2.4e9, [2e9]), true },
        { "width-unreachable", new LadderStubBandstopRealiser(), Band(FilterBand.Bandstop, Bw, 3, 2.4e9, 0.02), true },
        { "band-too-wide", new LadderEndCoupledRealiser(), Band(FilterBand.Bandpass, Bw, 3, 2.4e9, 0.8), true },
        { "gap-unreachable", new LadderEndCoupledRealiser(), Band(FilterBand.Bandpass, Bw, 3, 2.4e9, 0.1), true },
    };

    /// <summary>Each refusal is coded; the gap's names the capacitance it needs.</summary>
    [Theory]
    [MemberData(nameof(Refusals))]
    public void ARefusal_IsCoded(string code, IFilterRealiser r, FilterSpec spec, bool substrate)
    {
        var e = Assert.Throws<ParameterRefusalException>(() => r.Realise(spec, substrate ? Ro4350b : null));
        output.WriteLine(e.Message);
        Assert.Equal("synthesis.ladder." + code, e.Diagnostic?.Id);
        if (code == "gap-unreachable") Assert.Contains("fF", e.Message);
    }
}
