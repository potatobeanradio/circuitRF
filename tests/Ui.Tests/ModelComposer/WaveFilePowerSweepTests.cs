using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Modeling;
using RfCore.Data;
using RfCore.Loadpull;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// Brief LPW, gate 6 (R-lpw-9): a one-load <c>.cst</c> is a PowerSweep set. The file is the truth's own HB, written as the
/// peak waves at the device's planes — so the model the document composes from that truth must validate against it — and
/// the set states nothing but its measurement: frequency, bias, source and load terminations and the 2f0 load all come
/// from the file. Validation reads it through <see cref="LoadpullFiles.Read"/>, the call the Data Display makes.
/// </summary>
[Collection(CircuitRF.Ui.Tests.Lvs.LvsCliConsoleCollection.Name)]
public sealed class WaveFilePowerSweepTests(ITestOutputHelper output)
{
    [Fact]
    public void AOneLoadCst_ValidatesAsAPowerSweep_WithEveryConditionTakenFromTheFile()
    {
        string cmc = ExportFixture.Document();
        string dir = Path.GetDirectoryName(cmc)!;
        var zs = new Complex(25, 0); var zl = new Complex(40, 10);
        var cond = new LargeSignalConditions
        {
            Measurement = LargeSignalMeasurement.PowerSweep, FrequencyHz = 2e9, Vgs = -0.6, Vds = 6, SourceZ = zs, LoadZ = zl,
            LoadHarmonics = [Complex.Zero],
        };
        double[] pin = [-15, -10, -5, -2, 0, 2];
        var truth = CircuitEvaluation.Evaluate(
            PreparedCircuit.FromText(ValidationBench.PowerSweep("pwr", ExportFixture.Truth("gx", "dx"), 25, cond, pin, 5), dir, dir),
            new CircuitEvaluationRequest { Measurements = false });
        Assert.True(truth.Status == RunStatus.Success, truth.StatusMessage);
        File.WriteAllText(Path.Combine(dir, "one.cst"), Cst(truth.Results.First(r => r.Data.Contains("V")).Data, pin.Length, zs, zl));

        var doc = CmcFile.Read(cmc);
        CmcFile.Write(cmc, doc with
        {
            DataSets = [.. doc.DataSets, new CmcDataSet
            {
                Name = "wave", Kind = DataSetKind.LargeSignal, Sources = ["one.cst"],
                Conditions = new DataConditions { AmbientC = 25, LargeSignal = new LargeSignalConditions { Measurement = LargeSignalMeasurement.PowerSweep } },
                Import = new ImportSettings { Format = ImportSettings.Loadpull },
            }],
        });

        var result = ModelValidation.RunFile(cmc, settings: ValidationSettings.Default with { DataSets = ["wave"] });
        foreach (string line in result.Lines) output.WriteLine(line);
        var item = Assert.Single(result.Items);
        Assert.Equal(ValidationKind.PowerSweep, item.Kind);
        Assert.True(item.Passed, item.Line);
        // The drive the file states is the drive the truth was given: its available power, from the waves and its ΓS.
        Assert.All(item.Data["Pout.meas"].Axes[0].Values.Zip(pin), x => Assert.True(Math.Abs(x.First - x.Second) < 0.01, $"{x.First} vs {x.Second}"));
        var (meas, model) = (item.Data["Pout.meas"].RealValues, item.Data["Pout.model"].RealValues);
        Assert.All(meas.Zip(model), m => Assert.True(Math.Abs(m.First - m.Second) < 0.05, $"{m.First} vs {m.Second} dBm"));
    }

    /// <summary>
    /// One load's block of a <c>.cst</c> from an HB power sweep's terminal voltages and currents: peak waves at 50 Ω,
    /// a = (V + Z0·I)/(2√Z0) and b = (V − Z0·I)/(2√Z0) with I flowing into the device, f0 then 2f0 at each drive.
    /// </summary>
    private static string Cst(DataSet hb, int drives, Complex zs, Complex zl)
    {
        var v = hb["V"]; var i = hb["I"];
        int node = Array.FindIndex(v.Axes.ToArray(), a => a.Name == "node"), branch = Array.FindIndex(i.Axes.ToArray(), a => a.Name == "branch");
        Complex Cell(DataCube c, int ax, string label, int k, int h)
        {
            int nL = c.Axes[ax].Length, nH = c.Axes[ax + 1].Length, l = Array.IndexOf(c.Axes[ax].Labels!, label);
            return c.ComplexValues[k * nL * nH + l * nH + h];
        }
        double r0 = Math.Sqrt(50);
        string E(double x) => (x < 0 ? "-" : "+") + Math.Abs(x).ToString("0.000000E+00", CultureInfo.InvariantCulture);
        Complex G(Complex z) => (z - 50) / (z + 50);
        string Polar(Complex g) => $"{E(g.Magnitude)} {E(g.Phase * 180 / Math.PI)}";
        var sb = new StringBuilder();
        void L(string s) => sb.Append(s).Append("\r\n");
        L("#MEASUREMENT TYPE: Power Sweep");
        L("#FUNDAMENTAL FREQUENCY: 2.0 GHz");
        L("#HARMONICS: 1");
        L("#Z0SOURCE: 50.0");
        L("#Z0LOAD: 50.0");
        L($"#GAMMASOURCE: {Polar(G(zs))}");
        L($"#GAMMALOAD: {Polar(G(zl))}");
        L("#Frequency(Hz) Vin(V) Iin(A) Vout(V) Iout(A) Re[a1] Im[a1] Re[b1] Im[b1] Re[a2] Im[a2] Re[b2] Im[b2]");
        L("#BEGIN");
        for (int k = 0; k < drives; k++)
        {
            double vin = Cell(v, node, "gx", k, 0).Real, iin = Cell(i, branch, "IPG", k, 0).Real;
            double vout = Cell(v, node, "dx", k, 0).Real, iout = -Cell(i, branch, "IPD", k, 0).Real;
            for (int h = 1; h <= 2; h++)
            {
                Complex v1 = Cell(v, node, "gx", k, h), i1 = Cell(i, branch, "IPG", k, h);
                Complex v2 = Cell(v, node, "dx", k, h), i2 = -Cell(i, branch, "IPD", k, h);
                Complex a1 = (v1 + 50 * i1) / (2 * r0), b1 = (v1 - 50 * i1) / (2 * r0);
                Complex a2 = (v2 + 50 * i2) / (2 * r0), b2 = (v2 - 50 * i2) / (2 * r0);
                L(string.Join(" ", new[] { E(2e9 * h), E(vin), E(iin), E(vout), E(iout),
                    E(a1.Real), E(a1.Imaginary), E(b1.Real), E(b1.Imaginary), E(a2.Real), E(a2.Imaginary), E(b2.Real), E(b2.Imaginary) }));
            }
        }
        L("#END");
        return sb.ToString();
    }
}
