using System.Globalization;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The system Amplifier's noise refusals (brief NZ-3, <see cref="AmplifierNoiseSpec"/>), as coded diagnostics rather than
/// bare sentences (tests/Firewall.Tests/user-facing-text-allowlist.txt says why).
/// </summary>
public static class AmplifierNoiseDiagnostics
{
    private static string G6(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
    private static string Db(double? v) => v is { } x ? G6(x) : "";

    /// <summary>Rn or Gopt with no noise figure beside them.</summary>
    public static Diagnostic NoFigure() => Diagnostic.Create(
        "amp.noise.no-figure", DiagnosticSeverity.Error,
        "Amp: Rn and Gopt shape a noise figure, and this amplifier states none. Add NF (or NFopt), or remove them.");

    /// <summary>A noise figure below 0 dB.</summary>
    public static Diagnostic BelowZero(double? nfDb, double? nfOptDb) => Diagnostic.Create(
        "amp.noise.below-zero", DiagnosticSeverity.Error,
        "Amp: a noise figure below 0 dB is less noise than the source's own (NF={nf}, NFopt={nfopt}); " +
        "no two-port is quieter than a noiseless one.",
        ("nf", Db(nfDb)), ("nfopt", Db(nfOptDb)));

    /// <summary>NF below NFopt.</summary>
    public static Diagnostic BelowMinimum(double nfDb, double nfOptDb) => Diagnostic.Create(
        "amp.noise.below-minimum", DiagnosticSeverity.Error,
        "Amp: NF={nf} dB is below NFopt={nfopt} dB, but NFopt is the lowest noise figure any source gives.",
        ("nf", G6(nfDb)), ("nfopt", G6(nfOptDb)));

    /// <summary>NF and NFopt differ with the optimum at Zin itself, where the two are the same number.</summary>
    public static Diagnostic NoMinimumPlace(double nfDb, double nfOptDb) => Diagnostic.Create(
        "amp.noise.no-gopt", DiagnosticSeverity.Error,
        "Amp: NF={nf} dB and NFopt={nfopt} dB differ, but with no Gopt the noise match is Zin itself, where the noise figure " +
        "is NFopt. State Gopt (or Rn) to say where the minimum lies.",
        ("nf", G6(nfDb)), ("nfopt", G6(nfOptDb)));

    /// <summary>A set of noise parameters no two-port has.</summary>
    public static Diagnostic Unphysical(double nfMinDb, double rnOhm, string gammaOpt) => Diagnostic.Create(
        "amp.noise.unphysical", DiagnosticSeverity.Error,
        "Amp: the noise parameters NFmin = {nfmin} dB, Rn = {rn} Ohm and Gopt = {gopt} describe no physical two-port: " +
        "Fmin − 1 must not exceed 4·Rn·Re(Yopt). Raise Rn or move Gopt.",
        ("nfmin", G6(nfMinDb)), ("rn", G6(rnOhm)), ("gopt", gammaOpt));
}
