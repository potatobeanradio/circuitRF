using System.Globalization;
using CircuitRF.Diagnostics;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>Harmonic-balance noise's messages (brief NZ-5, <see cref="HbNoise"/>), as coded diagnostics.</summary>
public static class HbNoiseDiagnostics
{
    private static string Ghz(double hz) => (hz / 1e9).ToString("G6", CultureInfo.InvariantCulture);

    /// <summary>Noise=yes with no small-signal sweep to read it at.</summary>
    public static Diagnostic NoSsSweep(string analysis) => Diagnostic.Create(
        "hb.noise.no-ss-sweep", DiagnosticSeverity.Error,
        "HB '{analysis}' has Noise=yes but no small-signal sweep. The noise is read at the small-signal frequencies, which " +
        "are the OUTPUT frequencies: add SSStart, SSStop and SSNpts (or SSStep).",
        ("analysis", analysis));

    /// <summary>NoiseIn= or NoiseOut= that is not a port number.</summary>
    public static Diagnostic BadPort(string analysis, string key, string expr) => Diagnostic.Create(
        "hb.noise.bad-port", DiagnosticSeverity.Error,
        "HB '{analysis}': {key}={expr} is not a port number (the Num of a top-level Port, Term or P1Tone).",
        ("analysis", analysis), ("key", key), ("expr", expr));

    /// <summary>NoiseSideband= that is not one integer per tone.</summary>
    public static Diagnostic BadSideband(string analysis, string expr, int tones) => Diagnostic.Create(
        "hb.noise.bad-sideband", DiagnosticSeverity.Error,
        "HB '{analysis}': NoiseSideband={expr} is not {what}",
        ("analysis", analysis), ("expr", expr),
        ("what", tones == 1 ? "an integer k (the input frequency is f_out + k·f0)."
                            : $"{tones} comma-separated integers, one per tone (the input frequency is f_out + Σ kᵢ·fᵢ)."));

    /// <summary>The key the unconverged-solve noise warning is reported once under.</summary>
    public const string UnconvergedKey = "hb-noise-unconverged";

    /// <summary>Noise read on a harmonic-balance solve that did not converge.</summary>
    public static Diagnostic Unconverged() => Diagnostic.Create(
        "hb.noise.unconverged", DiagnosticSeverity.Warning,
        "HB noise was read on a harmonic-balance solve that did not converge: its NF and the other noise results describe " +
        "that unconverged waveform, not the circuit's.");

    /// <summary>
    /// Refuses, naming the key, an HB or loadpull noise request whose NoiseIn or NoiseOut names no port or Tuner of
    /// <paramref name="nl"/> — what <c>check</c> asks before anything is solved.
    /// </summary>
    public static void ValidateEnds(CircuitRF.Core.Elaboration.ElaboratedNetlist nl, HbNoiseRequest request)
        => HbNoise.ValidateEnds(nl, request);

    /// <summary>A port number no port carries.</summary>
    public static Diagnostic NoPort(string key, int num, string role) => Diagnostic.Create(
        "hb.noise.no-port", DiagnosticSeverity.Error,
        "{key}={num} names the {role} port, but no top-level Port, Term or P1Tone has Num={num}.",
        ("key", key), ("num", num), ("role", role));

    /// <summary>A loadpull's NoiseIn/NoiseOut naming no Tuner (brief NZ-6).</summary>
    public static Diagnostic NoTuner(string key, string name) => Diagnostic.Create(
        "hb.noise.no-tuner", DiagnosticSeverity.Error,
        "{key}={name} names no Tuner in this design.",
        ("key", key), ("name", name));

    /// <summary>The signal sideband is not among those the conversion matrix keeps.</summary>
    public static Diagnostic SidebandNotRetained(string sideband) => Diagnostic.Create(
        "hb.noise.sideband-not-retained", DiagnosticSeverity.Error,
        "NoiseSideband={sideband} is outside the sidebands the conversion matrix retains. Raise SSMaxHarm (or MaxHarm / " +
        "MaxMixOrder) to reach it.",
        ("sideband", sideband));

    /// <summary>Three or more tones.</summary>
    public static Diagnostic TonesUnsupported(int tones) => Diagnostic.Create(
        "hb.noise.tones-unsupported", DiagnosticSeverity.Error,
        "HB noise supports one and two tones, as the small-signal sweep it runs on does; this analysis declares {tones}.",
        ("tones", tones));

    /// <summary>The older two-tone grid path, reached only by a setting.</summary>
    public static Diagnostic TwoToneGridPath() => Diagnostic.Create(
        "hb.noise.two-tone-grid-path", DiagnosticSeverity.Error,
        "HB noise runs on the mixing-lattice two-tone path, which is the default; this run was sent to the older grid " +
        "path by a setting. Clear HbTwoToneOnLattice=false to run it.");

    /// <summary>The key a degenerate output frequency is reported once under.</summary>
    public const string DegenerateKey = "hb.noise.degenerate-frequency";

    /// <summary>An output frequency where a sideband and the negative of another coincide.</summary>
    public static Diagnostic Degenerate(double hz, string what) => Diagnostic.Create(
        DegenerateKey, DiagnosticSeverity.Warning,
        "HB noise: the output frequency {f} GHz is commensurate with {what} at order 2, so a sideband and the negative of " +
        "another are the same frequency and the noise there cannot be split between them; it is NaN rather than a " +
        "plausible wrong number. Offset the SS grid so no point lands on a half-multiple of a tone.",
        ("f", Ghz(hz)), ("what", what));

    /// <summary>The key a singular conversion matrix is reported once under.</summary>
    public const string SingularKey = "hb.noise.singular-conversion-matrix";

    /// <summary>A singular conversion matrix at an output frequency.</summary>
    public static Diagnostic Singular(double hz) => Diagnostic.Create(
        SingularKey, DiagnosticSeverity.Warning,
        "HB noise: the conversion matrix is singular at {f} GHz, so the noise there is NaN — the linearised periodic " +
        "system has a pole at that frequency.",
        ("f", Ghz(hz)));

    /// <summary>A Mixer's SSB NF below the 3 dB a noiseless DSB mixer has.</summary>
    public static Diagnostic MixerNfBelowFloor(string instance, double nfDb) => Diagnostic.Create(
        "hb.noise.mixer-nf-below-floor", DiagnosticSeverity.Warning,
        "Mixer:{instance}: NF={nf} dB SSB is below the 3 dB a noiseless double-sideband mixer has, so it adds no noise of " +
        "its own here. State NFType=DSB if the figure is a double-sideband one.",
        ("instance", instance), ("nf", nfDb.ToString("G6", CultureInfo.InvariantCulture)));
}
