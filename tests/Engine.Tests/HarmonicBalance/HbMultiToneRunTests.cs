using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// The T ≥ 3 path end to end through <see cref="HbEngine.Run"/> — the full pass a user gets:
/// directive resolution, the ceiling, commensurability, per-product linear extraction, the Newton
/// solve, the linear-interior back-solve, and the result cubes.
///
/// <para>The fixture (<c>testdata/Hero5/hero5_3tone.cnl</c>) is the Hero-5 GaN PA with three
/// EQUALLY SPACED carriers, kept at MaxMixOrder=3 (32 products, 128 unknowns) so this stays a
/// routine-tier test rather than a performance measurement.</para>
/// </summary>
public class HbMultiToneRunTests(ITestOutputHelper output)
{
    private static string Hero5Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero5");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero5 not found");
    }

    private static DataSet RunThreeTone()
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(Hero5Dir(), "hero5_3tone.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var hba       = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p         = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
        Assert.Equal(3, p.ToneFreqsHz.Length);
        return new HbEngine(netlist, tb).Run(p);
    }

    private static int MixIdx(DataSet ds, string tag)
    {
        var axis = ds["V"].Axes.First(a => a.Name == "mixIndex");
        int i = Array.IndexOf(axis.Labels!, tag);
        Assert.True(i >= 0, $"mixIndex label {tag} not present");
        return i;
    }

    private static Complex V(DataSet ds, string node, string tag)
    {
        var cube = ds["V"];
        int n = Array.IndexOf(cube.Axes.First(a => a.Name == "node").Labels!, node);
        Assert.True(n >= 0, $"node {node} not in the V cube");
        return (Complex)cube[n, MixIdx(ds, tag)];
    }

    [Fact]
    public void ThreeTone_Converges_PreservesBias_AndMixesAllThreeTones()
    {
        var ds = RunThreeTone();

        Assert.Equal(1.0, ds["Converged"].RealValues[0]);
        output.WriteLine($"residual {ds["Residual"].RealValues[0]:E3}");

        // Bias is held by the self-consistent DC index of the lattice.
        Assert.Equal(-3.05, V(ds, "n_gate",  "(0,0,0)").Real, 2);
        Assert.Equal(48.0,  V(ds, "n_drain", "(0,0,0)").Real, 2);

        // All three carriers develop, and comparably — equal drive into a near-equal load.
        double c1 = V(ds, "n_drain", "(1,0,0)").Magnitude;
        double c2 = V(ds, "n_drain", "(0,1,0)").Magnitude;
        double c3 = V(ds, "n_drain", "(0,0,1)").Magnitude;
        output.WriteLine($"carriers at n_drain: {c1:E4}  {c2:E4}  {c3:E4} V");
        Assert.True(c1 > 0.1 && c2 > 0.1 && c3 > 0.1, "a carrier failed to develop");
        Assert.True(Math.Abs(c1 - c3) / c1 < 0.05, "outer carriers are implausibly asymmetric");

        // THE point of three tones: a product that mixes ALL THREE and is driven by none of them.
        // (1,1,-1) cannot arise from any two-tone subset of this drive, so its presence is proof
        // the lattice is genuinely three-dimensional and not two tones with a spectator.
        double triple = V(ds, "n_drain", "(1,1,-1)").Magnitude;
        output.WriteLine($"three-way product (1,1,-1) at n_drain: {triple:E4} V");
        Assert.True(triple > 1e-6,
            $"the three-tone mixing product (1,1,-1) is absent (|V|={triple:E3}) — the third tone is not mixing.");
    }

    [Fact]
    public void EquallySpacedTones_KeepFrequencyDegenerateProductsIndependent()
    {
        // With equally spaced carriers, (1,-1,0) and (0,1,-1) are DIFFERENT mixing products that
        // sit at the SAME physical frequency (−10 MHz). They must remain separate unknowns: each
        // tone owns its own phase axis, so the torus basis functions stay orthogonal no matter
        // what the frequencies do. If the engine ever collapsed them the solve would be singular
        // or the answer arbitrary, and neither failure announces itself.
        var ds = RunThreeTone();
        var axis = ds["V"].Axes.First(a => a.Name == "mixIndex");

        int a = MixIdx(ds, "(1,-1,0)");
        int b = MixIdx(ds, "(0,1,-1)");
        Assert.NotEqual(a, b);
        Assert.Equal(axis.Values[a], axis.Values[b], 3);            // same frequency…
        Assert.Equal(-10e6, axis.Values[a], 3);

        var va = V(ds, "n_drain", "(1,-1,0)");
        var vb = V(ds, "n_drain", "(0,1,-1)");
        output.WriteLine($"degenerate pair at −10 MHz: (1,-1,0)={va.Magnitude:E4}∠{va.Phase * 180 / Math.PI:F1}°  " +
                         $"(0,1,-1)={vb.Magnitude:E4}∠{vb.Phase * 180 / Math.PI:F1}°");

        Assert.True(va.Magnitude > 1e-6 && vb.Magnitude > 1e-6, "a degenerate baseband product is missing");
        Assert.True((va - vb).Magnitude > 1e-9, "…but they are NOT the same unknown");
    }

    [Fact]
    public void ResultCubes_HaveTheSameShapeAsTwoTone_WithWidenedTags()
    {
        // The data display reads the axis NAME to decide it is a spectrum, the axis VALUES to
        // position stems, and the axis LABEL verbatim for readouts. Keeping all three identical
        // in kind to the two-tone result is what lets a three-tone spectrum render through the
        // frozen two-tone path with no data-display change at all.
        var ds = RunThreeTone();

        foreach (string cube in new[] { "V", "INl", "I" })
            Assert.True(ds.Contains(cube), $"missing cube {cube}");
        Assert.True(ds.Contains("ToneFreqs") && ds.Contains("MetaMixOrder"));

        var axis = ds["V"].Axes.First(a => a.Name == "mixIndex");
        Assert.Equal("Hz", axis.Unit);
        Assert.Equal(MixingLattice.CountFor(3, 3), axis.Length);
        Assert.Equal(3.0, ds["MetaMixOrder"].RealValues[0]);

        // Tags are 3-tuples, DC first, and the VALUE of each is its signed physical frequency.
        Assert.Equal("(0,0,0)", axis.Labels![0]);
        double[] tones = ds["ToneFreqs"].RealValues;
        Assert.Equal(3, tones.Length);

        for (int m = 0; m < axis.Length; m++)
        {
            var parts = axis.Labels[m].Trim('(', ')').Split(',');
            Assert.Equal(3, parts.Length);
            int[] k = parts.Select(int.Parse).ToArray();
            Assert.Equal(k[0] * tones[0] + k[1] * tones[1] + k[2] * tones[2], axis.Values[m], 3);
        }

        // Negative-frequency reps are retained and stored signed, exactly as at two tones.
        Assert.True(axis.Values.Any(v => v < 0), "no negative-frequency product retained");
    }

    [Fact]
    public void MeasurementSelectors_AddressProductsByToneVector()
    {
        // The int[] overloads on TwoToneMeasurements are how a caller reaches a T-tone product.
        var ds = RunThreeTone();

        double pCarrier = TwoToneMeasurements.PoutDbm(ds, 0, "n_drain", [1, 0, 0]);
        double pTriple  = TwoToneMeasurements.PoutDbm(ds, 0, "n_drain", [1, 1, -1]);
        double dbc      = TwoToneMeasurements.ImDbc(ds, 0, "n_drain", [1, 1, -1], [1, 0, 0]);
        output.WriteLine($"carrier {pCarrier:F2} dBm, (1,1,-1) {pTriple:F2} dBm, {dbc:F2} dBc");

        Assert.True(pCarrier > pTriple, "an intermod product exceeds the carrier");
        Assert.Equal(pTriple - pCarrier, dbc, 6);

        // Frequencies come back signed and summed over the tone vector.
        Assert.Equal(-10e6, TwoToneMeasurements.FrequencyOf(ds, [1, -1, 0]), 3);

        // A non-retained rep resolves through its conjugate partner, as at two tones.
        var direct = TwoToneMeasurements.Tone(ds, 0, "n_drain", [1, -1, 0]);
        var conj   = TwoToneMeasurements.Tone(ds, 0, "n_drain", [-1, 1, 0]);
        Assert.Equal(direct.Real,       conj.Real,      12);
        Assert.Equal(direct.Imaginary, -conj.Imaginary, 12);
    }
}
