using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// brief-sdl-9: MGAP, the end-to-end gap. The closed form is gated against
/// <c>testdata/microstrip-gap/gap-capacitance.txt</c> — gap capacitances from the planar engine's electrostatic
/// solve on a meshed gap — as the gap's own |S21| and phase at 50 Ω. The tolerances are the departures that
/// file's README records, by region; they are not fitted to make the model pass.
/// </summary>
public class MicrostripGapTests
{
    private const double H = 508e-6, Er = 3.66, Z0 = 50.0;
    private static readonly double[] Frequencies = [2e9, 6e9, 10e9];

    private static Complex S21(double cg, double cp, double f)
    {
        double w = 2 * Math.PI * f;
        Complex y11 = new(0, w * (cg + cp)), y12 = new(0, -w * cg);
        Complex a = 1 + Z0 * y11;
        return -2 * Z0 * y12 / (a * a - Z0 * Z0 * y12 * y12);
    }

    /// <summary>The model's S21, read back from what it STAMPS rather than from the formula it calls.</summary>
    private static Complex ModelS21(MicrostripGapModel gap, double f)
    {
        var mna = new CapturingMnaContext();
        gap.Stamp(mna, new ElaboratedComponent("MGAP", "G1", [1, 2], new Dictionary<string, Value>(), gap), 2 * Math.PI * f);
        Complex y11 = mna.Entries[(1, 1)], y12 = mna.Entries[(1, 2)], y21 = mna.Entries[(2, 1)], y22 = mna.Entries[(2, 2)];
        return -2 * Z0 * y21 / ((1 + Z0 * y11) * (1 + Z0 * y22) - Z0 * Z0 * y12 * y21);
    }

    [Fact]
    public void AgainstTheEmReference_SitsWithinTheRecordedDepartureEverywhere()
    {
        var failures = new List<string>();
        foreach (var (w, s, cg, cp) in Reference())
        {
            // The README's table: W/h ≤ 1 within 0.6 dB / 0.6°; W/h = 2 within 1.4 dB / 2°, except its S/W = 1
            // corner, where the series capacitance reads 43 % low and |S21| 4.9 dB low — held to that, not hidden.
            bool wide = w / H > 1.5, corner = wide && s / w > 0.9;
            double dbTol = corner ? 5.5 : wide ? 1.4 : 0.6, degTol = wide ? 2.0 : 0.6;

            var gap = new MicrostripGapModel(w, s, H, Er, "MGAP:G1");
            foreach (double f in Frequencies)
            {
                Complex reference = S21(cg, cp, f), model = ModelS21(gap, f);
                double dDb = 20 * Math.Log10(model.Magnitude / reference.Magnitude);
                double dDeg = (model / reference).Phase * 180 / Math.PI;
                if (Math.Abs(dDb) > dbTol || Math.Abs(dDeg) > degTol)
                    failures.Add($"W={w * 1e6:F0} µm S={s * 1e6:F1} µm at {f / 1e9:G3} GHz: {dDb:+0.00;-0.00} dB, {dDeg:+0.00;-0.00}°");
            }
        }
        Assert.True(failures.Count == 0, "Outside the recorded departure:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void ACapacitivePi_IsReciprocalLossless_AndAnOpenAtDc()
    {
        var gap = new MicrostripGapModel(508e-6, 100e-6, H, Er, "MGAP:G1");
        var mna = new CapturingMnaContext();
        gap.Stamp(mna, new ElaboratedComponent("MGAP", "G1", [1, 2], new Dictionary<string, Value>(), gap), 2 * Math.PI * 5e9);
        Assert.Equal(mna.Entries[(1, 2)], mna.Entries[(2, 1)]);
        Assert.All(mna.Entries.Values, y => Assert.Equal(0.0, y.Real));

        var dc = new CapturingMnaContext();
        gap.Stamp(dc, new ElaboratedComponent("MGAP", "G1", [1, 2], new Dictionary<string, Value>(), gap), 0.0);
        Assert.Empty(dc.Entries);
    }

    [Fact]
    public void OutsideTheStatedRange_WarnsOncePerRatio()
    {
        var gap = new MicrostripGapModel(4e-3, 8e-3, H, Er, "MGAP:G1");   // W/h ≈ 7.9, S/W = 2
        var warnings = gap.DrainWarnings();
        Assert.Contains(warnings, w => w.Message.Contains("W/h", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Message.Contains("S/W", StringComparison.Ordinal));
        Assert.True(gap.SeriesCapacitance >= 0);
        Assert.Empty(new MicrostripGapModel(508e-6, 100e-6, H, Er, "MGAP:G2").DrainWarnings());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.1")]
    public void AGapOfZeroOrLess_IsRefusedAtElaboration_NamingTheInstance(string s)
    {
        string path = Path.Combine(Path.GetTempPath(), "crf-mgap-" + Guid.NewGuid().ToString("N")[..8] + ".cnl");
        File.WriteAllText(path,
            "Port:P1 a 0 Num=1 Z=50 Ohm\n" +
            $"MGAP:G1 a b W=0.5 mm S={s} mm\n" +
            "Port:P2 b 0 Num=2 Z=50 Ohm\n" +
            "analysis SP1 type=sparam start=1 stop=2 npts=2 Unit=GHz\n");
        try
        {
            var (lib, tb) = CnlReader.ReadFile(path);
            var ex = Assert.Throws<ParameterRefusalException>(() => new Elaborator(lib).Elaborate(tb));
            Assert.Contains("G1", ex.Message, StringComparison.Ordinal);
            Assert.Contains("S must be greater than zero", ex.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    private static IEnumerable<(double W, double S, double Cg, double Cp)> Reference()
    {
        var dir = AppContext.BaseDirectory;
        string? file = null;
        while (dir is not null && file is null)
        {
            var candidate = Path.Combine(dir, "testdata", "microstrip-gap", "gap-capacitance.txt");
            if (File.Exists(candidate)) file = candidate;
            dir = Path.GetDirectoryName(dir);
        }
        if (file is null) throw new FileNotFoundException("testdata/microstrip-gap/gap-capacitance.txt");

        var rows = File.ReadAllLines(file).Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                          .Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray())
            .Select(v => (v[0] * 1e-6, v[1] * 1e-6, v[2] * 1e-15, v[3] * 1e-15)).ToList();
        Assert.Equal(15, rows.Count);
        return rows;
    }
}
