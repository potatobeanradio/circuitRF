using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace RfCore.Tests.Loadpull;

/// <summary>
/// Brief LPW (R-lpw-10): a small analytic device evaluated at known loads, and the loadpull files it would produce, written
/// in each format's exact layout — <c>.cst</c> (CRLF, a block per load, interleaved harmonic rows, ragged), <c>.lpcwave</c>
/// and <c>.satwave</c> (<c>!</c> headers, RMS waves). Every metric therefore has an exact expected value
/// (<see cref="Expected"/>), worked from the brief's own definitions rather than from the reader's code.
/// <para>
/// The committed fixtures under <c>testdata/lpw</c> are this class's output (<see cref="WriteAll"/>); a test holds them
/// byte for byte to it, so the two cannot drift.
/// </para>
/// </summary>
public static class LpwSyntheticDevice
{
    public const double F0 = 10e9;
    public const double Z0 = 50;
    public static readonly Complex GammaS = Complex.FromPolarCoordinates(0.3, 150 * Math.PI / 180);
    public static readonly Complex[] Loads =
    [
        Complex.FromPolarCoordinates(0.2, 30 * Math.PI / 180),
        Complex.FromPolarCoordinates(0.5, 120 * Math.PI / 180),
        Complex.FromPolarCoordinates(0.4, -60 * Math.PI / 180),
    ];
    /// <summary>Drives per load: the second load is short, so its block is ragged.</summary>
    public static readonly int[] Drives = [8, 6, 8];
    /// <summary>The harmonic loads at 2f0 and 3f0, the same at every f0 load.</summary>
    public static readonly Complex[] HarmonicLoads =
    [
        Complex.FromPolarCoordinates(0.6, 170 * Math.PI / 180),
        Complex.FromPolarCoordinates(0.5, -20 * Math.PI / 180),
    ];
    public const double Vout = 28, Vin = -2;

    /// <summary>The available power at drive <paramref name="p"/>, dBm — every load is driven up the same ladder.</summary>
    public static double PavlDbm(int p) => 10 + 2 * p;

    /// <summary>Γin moves with drive, so transducer and power gain compress at different drives (gate 4).</summary>
    public static Complex GammaIn(int p) => Complex.FromPolarCoordinates(0.05 + 0.05 * p, (-100 + 10 * p) * Math.PI / 180);

    /// <summary>The PEAK waves {a1, b1, a2, b2} of load <paramref name="g"/> at drive <paramref name="p"/> and harmonic
    /// <paramref name="h"/> (1 = f0).</summary>
    public static Complex[] Waves(int g, int p, int h)
    {
        double pavlW = Math.Pow(10, (PavlDbm(p) - 30) / 10);
        double bg = Math.Sqrt(2 * pavlW * (1 - GammaS.Magnitude * GammaS.Magnitude));   // P(bg)/(1−|ΓS|²) = Pavl, peak
        var gin = GammaIn(p);
        var a1 = bg / (1 - GammaS * gin);
        var b1 = gin * a1;
        // Gain 4 (12 dB) into a load-dependent factor, compressing softly toward a ceiling, with an AM/PM rotation.
        double sat = 0.15;
        double comp = 1 / Math.Sqrt(1 + Math.Pow(a1.Magnitude / sat, 2));
        // A gain expansion over the first drives puts the gain's peak after the small-signal drive, so a compression
        // from the maximum (Gmax) and one from the small-signal gain (Gss) are different drives.
        double x = Math.Pow(a1.Magnitude / 0.25, 4), expand = 1 + 1.5 * x / (1 + x);
        var b2 = 4.0 * (1 + 0.3 * Loads[g].Real) * a1 * comp * expand * Complex.FromPolarCoordinates(1, 0.4 * a1.Magnitude * a1.Magnitude);
        var a2 = Loads[g] * b2;
        if (h == 1) return [a1, b1, a2, b2];

        // Harmonics: what the device sends toward the load grows as it compresses; at the first drive it is next to
        // nothing and the reflection is noise (|Γ| = 3) — the weighting must make that drive count for nothing.
        var hb2 = 0.1 / h * b2 * (1 - comp) * (p == 0 ? 0.05 : 1.0);
        var ha2 = p == 0 ? 3.0 * Complex.FromPolarCoordinates(hb2.Magnitude, 1.0) : HarmonicLoads[h - 2] * hb2;
        var ha1 = 0.01 * (p + 1) / h * Complex.One;
        var hb1 = 0.5 * ha1 * Complex.FromPolarCoordinates(1, 0.3);
        return [ha1, hb1, ha2, hb2];
    }

    public static double Iout(int g, int p) => 0.01 + 0.08 * Waves(g, p, 1)[3].Magnitude;
    public static double Iin(int p) => -1e-4 * p;

    // ── Expected metrics (the brief's definitions, peak waves: P = ½|x|²) ─────

    public sealed record Metrics(double PavlDbm, double PinIncDbm, double PinDelDbm, double PoutDbm, double PoutW, double GtDb,
                                 double GpDb, double PdcW, double Efficiency, double Pae, Complex Zin, double IrlDb, double AmpmDeg,
                                 Complex GammaLoadMeas);

    private static double P(Complex x) => 0.5 * (x.Real * x.Real + x.Imaginary * x.Imaginary);
    private static double Dbm(double w) => 10 * Math.Log10(w * 1000);

    public static Metrics Expected(int g, int p)
    {
        var w = Waves(g, p, 1);
        var (a1, b1, a2, b2) = (w[0], w[1], w[2], w[3]);
        double pavl = P(a1 - GammaS * b1) / (1 - GammaS.Magnitude * GammaS.Magnitude);
        double pdel = P(a1) - P(b1), pout = P(b2) - P(a2);
        double pdc = Vin * Iin(p) + Vout * Iout(g, p);
        var gin = b1 / a1;
        double ph(int q) { var x = Waves(g, q, 1); return (x[3] / x[0]).Phase * 180 / Math.PI; }
        double ampm = ph(p) - ph(0);
        ampm -= 360 * Math.Round(ampm / 360);   // the device's AM/PM stays well inside ±180°
        return new Metrics(Dbm(pavl), Dbm(P(a1)), Dbm(pdel), Dbm(pout), pout, 10 * Math.Log10(pout / pavl), 10 * Math.Log10(pout / pdel),
                           pdc, 100 * pout / pdc, 100 * (pout - pdel) / pdc, Z0 * (1 + gin) / (1 - gin), 20 * Math.Log10(gin.Magnitude), ampm,
                           a2 / b2);
    }

    /// <summary>The harmonic metrics at load <paramref name="g"/>, drive <paramref name="p"/>, harmonic <paramref name="h"/> ≥ 2.</summary>
    public static (double PoutDbm, double PinDelDbm) ExpectedHarmonic(int g, int p, int h)
    {
        var w = Waves(g, p, h);
        return (Dbm(P(w[3]) - P(w[2])), Dbm(P(w[0]) - P(w[1])));
    }

    // ── Writers ─────────────────────────────────────────────────────────────

    private static string E(double v) => (v < 0 ? "-" : "+") + Math.Abs(v).ToString("0.00000E+00", CultureInfo.InvariantCulture);
    private static string F(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);

    /// <summary>A <c>.cst</c> holding <paramref name="harmonics"/> harmonics above f0, CRLF, a block per load.</summary>
    public static string Cst(int harmonics, int loads = 3)
    {
        var sb = new StringBuilder();
        void L(string s) => sb.Append(s).Append("\r\n");
        L("#VERSION: 1.0.0.0");
        L("#TRANSISTOR NAME: Synthetic FET");
        L("#TRANSISTOR PERIPHERY [mm]: 1.0");
        L("#");
        for (int g = 0; g < loads; g++)
        {
            L("#MEASUREMENT TYPE: Power Sweep");
            L("#TC: 25.0");
            L($"#FUNDAMENTAL FREQUENCY: {F(F0 / 1e9, "0.0")} GHz");
            L($"#HARMONICS: {harmonics}");
            L("#SWEPT HARMONIC: 0");
            L("#Z0SOURCE: 50.0");
            L("#Z0LOAD: 50.0");
            L($"#GAMMASOURCE: {E(GammaS.Magnitude)} {E(GammaS.Phase * 180 / Math.PI)}");
            L($"#GAMMALOAD: {E(Loads[g].Magnitude)} {E(Loads[g].Phase * 180 / Math.PI)}");
            L("#OPERATING CONDITIONS: Mode Continuous");
            L("#BIAS PATH(S): 2");
            L($"#PULSE COMMAND: Vin {E(Vin)} Vout {E(Vout)}");
            L("#");
            L("#Frequency(Hz) Vin(V) Iin(A) Vout(V) Iout(A) Re[a1] Im[a1] Re[b1] Im[b1] Re[a2] Im[a2] Re[b2] Im[b2]");
            L("#BEGIN");
            for (int p = 0; p < Drives[g]; p++)
                for (int h = 1; h <= harmonics + 1; h++)
                {
                    var w = Waves(g, p, h);
                    L(string.Join(" ", new[] { E(F0 * h), E(Vin), E(Iin(p)), E(Vout), E(Iout(g, p)),
                        E(w[0].Real), E(w[0].Imaginary), E(w[1].Real), E(w[1].Imaginary),
                        E(w[2].Real), E(w[2].Imaginary), E(w[3].Real), E(w[3].Imaginary) }));
                }
            L("#END");
            L("#");
        }
        return sb.ToString();
    }

    private static readonly string[] WaveTitles =
        ["Psource[dBm]", "PinWaves[dBm]", "PinDelWaves[dBm]", "PoutWaves[dBm]", "GainWavesTrd[dB]", "GainWavesPwr[dB]",
         "PAEffWaves[%]", "OutEffWaves[%]", "|GinWaves@F0|", "PhiinWaves@F0[deg]",
         "A1reF0", "A1imF0", "B1reF0", "B1imF0", "A2reF0", "A2imF0", "B2reF0", "B2imF0",
         "V1[V]", "V2[V]", "I1[uA]", "I2[mA]", "PowerIndex"];

    /// <summary>One drive's row in the <c>.lpcwave</c>/<c>.satwave</c> columns: RMS waves, and the stated metrics worked
    /// from them to the dialect's own rounding (the instrument's source setting sits 24 dB below the available power).</summary>
    private static string WaveRow(int g, int p)
    {
        var m = Expected(g, p);
        var w = Waves(g, p, 1).Select(x => x / Math.Sqrt(2)).ToArray();
        var gin = (m.Zin - Z0) / (m.Zin + Z0);
        string[] v =
        [
            F(m.PavlDbm - 24, "0.000"), F(m.PavlDbm, "0.000"), F(m.PinDelDbm, "0.000"), F(m.PoutDbm, "0.000"), F(m.GtDb, "0.000"),
            F(m.GpDb, "0.000"), F(m.Pae, "0.000"), F(m.Efficiency, "0.000"), F(gin.Magnitude, "0.00000"), F(gin.Phase * 180 / Math.PI, "0.000"),
            .. w.SelectMany(x => new[] { F(x.Real, "0.0000000000"), F(x.Imaginary, "0.0000000000") }),
            F(Vin, "0.000"), F(Vout, "0.000"), F(Iin(p) * 1e6, "0.000"), F(Iout(g, p) * 1e3, "0.000"), (p + 1).ToString(CultureInfo.InvariantCulture),
        ];
        return "  " + string.Join("  ", v);
    }

    private static string Polar(Complex g) => $"{F(g.Magnitude, "0.000")}<{F(g.Phase * 180 / Math.PI, "0.0")}(deg)";

    /// <summary>A <c>.lpcwave</c> of every load, RMS waves.</summary>
    public static string Lpcwave()
    {
        var sb = new StringBuilder();
        void L(string s) => sb.Append(s).Append("\r\n");
        L("! Power Sweep Load Pull Measurement Data");
        L("!--------------------------------------------------------");
        L($"! Frequency = {F(F0 / 1e9, "0")} GHz  ");
        L("! Char.Impedances = Source: 50 + 0j Ohm   Load: 50 + 0j Ohm");
        var zs = Z0 * (1 + GammaS) / (1 - GammaS);
        L($"! Source Impedance = {F(zs.Real, "0.00")} {(zs.Imaginary < 0 ? "-" : "+")}j {F(Math.Abs(zs.Imaginary), "0.00")} Ohm ");
        L($"! Source Impedance (Polar) = {F(GammaS.Magnitude, "0.000")}, {F(GammaS.Phase * 180 / Math.PI, "0.00")}(deg) ");
        L("!--------------------------------------------------------");
        L("Point  Gamma  Phase[deg]  " + string.Join("  ", WaveTitles));
        L("!--------------------------------------------------------");
        for (int g = 0; g < Loads.Length; g++)
        {
            L($"# {g + 1:000}  {F(Loads[g].Magnitude, "0.000")}  {F(Loads[g].Phase * 180 / Math.PI, "0.0")}");
            for (int p = 0; p < Drives[g]; p++) L(WaveRow(g, p));
        }
        return sb.ToString();
    }

    /// <summary>A <c>.satwave</c>: load <paramref name="g"/>'s drive-up alone.</summary>
    public static string Satwave(int g = 0)
    {
        var sb = new StringBuilder();
        void L(string s) => sb.Append(s).Append("\r\n");
        L($"! Frequency: {F(F0 / 1e9, "0")} GHz");
        L("! Char.Impedances = Source: 50 + 0j Ohm   Load: 50 + 0j Ohm");
        L($"! Load Frequencies = F0: {F(F0 / 1e9, "0")} GHz  ");
        L($"! Load Impedances (Polar) = F0: {Polar(Loads[g])} ");
        L($"! Source Impedances (Polar) = F0: {Polar(GammaS)} ");
        L("! --------------------------------------------------------");
        L("! TITLES:  " + string.Join("  ", WaveTitles));
        L("! --------------------------------------------------------");
        for (int p = 0; p < Drives[g]; p++) L(WaveRow(g, p));
        return sb.ToString();
    }

    /// <summary>Every committed fixture: file name → text.</summary>
    public static IReadOnlyDictionary<string, string> All() => new Dictionary<string, string>
    {
        ["synthetic_f0.cst"] = Cst(0),
        ["synthetic_2f0.cst"] = Cst(1),
        ["synthetic_3f0.cst"] = Cst(2),
        ["synthetic_one_load.cst"] = Cst(1, loads: 1),
        ["synthetic.lpcwave"] = Lpcwave(),
        ["synthetic.satwave"] = Satwave(),
    };

    /// <summary>Writes <see cref="All"/> into <paramref name="dir"/> (how the committed fixtures were made).</summary>
    public static void WriteAll(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var (name, text) in All()) File.WriteAllText(Path.Combine(dir, name), text, new UTF8Encoding(false));
    }

    /// <summary>The repository's <c>testdata/lpw</c>.</summary>
    public static string FixtureDir()
    {
        for (var d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "circuitRF.slnx"))) return Path.Combine(d, "testdata", "lpw");
        throw new DirectoryNotFoundException("the repository root (circuitRF.slnx) was not found");
    }
}
