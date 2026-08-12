using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using CircuitRF.Core.Devices.External;
using CircuitRF.Engine;
using CircuitRF.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// M2's gate: R-hrf-5 (the structural / value boundary), R-hrf-4 (<c>Zin</c> from the TRUE delivered
/// current), R-hrf-10 (the published <c>DataSet</c>), R-hrf-11 (<c>.charm</c>) and R-hrf-12
/// (Touchstone coverage).
/// </summary>
public sealed class ContextAndPersistenceTests(ITestOutputHelper output)
{
    private static AnalysisSettings Settings => new()
    {
        InductanceRegularization  = RegularizationMode.Always,
        ConductanceRegularization = RegularizationMode.Never,
    };

    private static CircuitModel Model(LumpedPackage? package = null) => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = "_v1/300",
                ["I[2,0]"] = "0.08*(_v1+3)*(_v1+3)*tanh(0.4*_v2)",
                ["Q[1]"]   = "2e-12*_v1",
            },
        },
        Embedding = new EmbeddingStack { Package = package ?? LumpedPackage.None },
        Bias      = new BiasSpec { Vgs = -1.5, Vds = 10 },
        Settings  = new HarmonicaSettings
        {
            HarmonicCount = 3, FrequencyHz = 2e9,
            BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9, Tol = 1e-9,
        },
    };

    private static TerminationSet Terms(int k, Complex zs, Complex zl)
    {
        var t = new TerminationSet(k);
        for (int h = 1; h <= k; h++) { t.Set(TerminationSide.Source, h, zs); t.Set(TerminationSide.Load, h, zl); }
        return t;
    }

    // ── R-hrf-5 — the boundary ────────────────────────────────────────────────

    [Fact]
    public void R5_AValueChangeDoesNotRebuildAndAStructuralOneDoes()
    {
        var model = Model();
        var ctx = HarmonicaContext.Create(model, Settings);
        int builds = ctx.RebuildCount;

        // Drive is not even in the model's identity — it is an argument to Solve.
        Assert.False(ctx.Apply(model with { PavlDbm = 12 }));
        Assert.Equal(builds, ctx.RebuildCount);

        // Bias is a value change: it mutates the supply model in place and re-extracts, but the
        // netlist is not re-elaborated.
        Assert.False(ctx.Apply(model with { Bias = model.Bias with { Vgs = -1.2 } }));
        Assert.Equal(builds, ctx.RebuildCount);

        // The harmonic count IS structural — the interface network is per-harmonic.
        Assert.True(ctx.Apply(model with
        {
            Settings = model.Settings with { HarmonicCount = 5 },
        }));
        Assert.Equal(builds + 1, ctx.RebuildCount);

        // So is the DUT.
        Assert.True(ctx.Apply(model with
        {
            Settings = model.Settings with { HarmonicCount = 5 },
            Dut = model.Dut with { Multiplicity = 2.0 },
        }));
        Assert.Equal(builds + 2, ctx.RebuildCount);
    }

    [Fact]
    public void R5_MovingTheBiasActuallyMovesTheOperatingPoint()
    {
        // The boundary is only worth holding if the in-place mutation WORKS. A supply that silently
        // kept its elaborated value would leave the netlist un-rebuilt (passing the test above) and
        // the physics frozen — the failure mode this check exists for.
        var model = Model();
        var ctx = HarmonicaContext.Create(model, Settings);
        var terms = Terms(3, new Complex(25, 0), new Complex(40, 0));

        double IdcAt(double vgs)
        {
            ctx.Apply(model with { Bias = model.Bias with { Vgs = vgs } });
            var pt = ctx.Solve(terms, -20);
            Assert.True(pt.Converged);
            int drain = ctx.InterfaceIndex(HarmonicaNetlist.LoadPlane);
            return pt.INl[drain, 0].Real;
        }

        double cold = IdcAt(-2.5), warm = IdcAt(-1.0);
        output.WriteLine($"Idq at Vgs = −2.5 V: {cold * 1e3:F3} mA;  at −1.0 V: {warm * 1e3:F3} mA");
        Assert.True(warm > cold * 1.5,
            $"the bias mutation did not reach the device: {cold * 1e3:F3} mA → {warm * 1e3:F3} mA");
    }

    // ── R-hrf-4 — Zin from the TRUE delivered current ─────────────────────────

    [Fact]
    public void R4_ZinIsTheDeliveredCurrentNotTheDevicesOwn()
    {
        // The fixture is exactly the shape that broke loadpull's Zin: a real passive network between
        // the termination plane and the device, so the current the SOURCE delivers is not the
        // current the DEVICE takes. Reporting the latter is the 5000-Ω-instead-of-192-Ω bug.
        var package = new LumpedPackage { Rg = 6.0, Lg = 0.5e-9 };
        var model = Model(package);
        var ctx = HarmonicaContext.Create(model, Settings);
        var terms = Terms(3, new Complex(25, 0), new Complex(40, 0));

        var pt = ctx.Solve(terms, 0);
        Assert.True(pt.Converged, $"‖F‖ = {pt.Residual:E3}");

        var (planeV, planeI) = ctx.Interface.PlaneState(
            terms, HarmonicaContext.DriveVolts(terms, pt.PavlDbm), pt.INlTotal, model.Settings.DcBlockFarads);

        int src = (int)TerminationSide.Source;
        Complex zin = planeV[src, 1] / planeI[src, 1];

        // The device's own intrinsic gate current, which is what must NOT be used.
        var spectra = IntrinsicPlane.Evaluate(
            ctx.DutComponent, pt.V, ctx.Interface.DeviceNodes, 3,
            CircuitRF.Engine.HarmonicBalance.HbFft.GridSize(3, 1), model.Settings.FrequencyHz);
        int gate = ctx.InterfaceIndex(HarmonicaNetlist.GateTerminal);
        Complex zWrong = pt.V[gate, 1] / spectra.portCurrents[0, 1];

        output.WriteLine($"Zin from the DELIVERED current   = {zin:G8}");
        output.WriteLine($"Zin from the device's own current = {zWrong:G8}   ← the shipped bug's answer");

        // The delivered current obeys KCL at the plane: it is the sum of everything that node feeds,
        // which here is the bias choke plus the series gate lead into the device. That is a closed
        // form, so this is a real check and not a comparison of two engine paths.
        double omega = 2.0 * Math.PI * model.Settings.FrequencyHz;
        Complex zChoke = new(0, omega * model.Settings.BiasChokeHenries);
        Complex zLead  = package.Rg + new Complex(0, omega * package.Lg);
        // The device's TERMINAL current at the gate — conduction plus displacement. Using the
        // conduction half here would be the very substitution this test exists to forbid, one level
        // down in the oracle.
        Complex iTerminal = spectra.portCurrents[0, 1] + spectra.portChargeCurrents[0, 1];
        Complex zDevice = pt.V[gate, 1] / iTerminal;
        Complex hand = Complex.One / (Complex.One / zChoke + Complex.One / (zLead + zDevice));

        output.WriteLine($"hand: choke ∥ (Rg + jωLg + Z_device) = {hand:G8}");
        Assert.True((zin - hand).Magnitude / hand.Magnitude < 1e-6,
            $"Zin should be the node's whole load; got {zin:G8} against {hand:G8}");

        Assert.True((zin - zWrong).Magnitude / zWrong.Magnitude > 0.05,
            "the fixture must actually separate the two currents, or this proves nothing");
    }

    // ── R-hrf-10 — the published DataSet ──────────────────────────────────────

    [Fact]
    public void R10_ThePublishedDataSetCarriesTheContractH4ToH7IsWrittenAgainst()
    {
        var model = Model(new LumpedPackage { Rg = 2.0, Ls = 0.3e-9 });
        var ctx = HarmonicaContext.Create(model, Settings);
        var terms = Terms(3, new Complex(25, 0), new Complex(40, 15));

        var pt = ctx.Solve(terms, 0);
        Assert.True(pt.Converged, $"‖F‖ = {pt.Residual:E3}");

        var ds = HarmonicaDataSet.Build(ctx, pt, terms);

        // Names and shapes are part of the deliverable, so they are asserted rather than eyeballed.
        foreach (string name in new[]
                 {
                     "V", "INl", "V_intr", "I_intr", "Idisp_intr", "Vds_intr_t", "Ids_intr_t",
                     "Z_ext", "Gamma_ext", "Z_intr", "Gamma_intr", "Zs_conv",
                     "V_ext", "Iin", "Zin", "Converged", "Residual", "Pavl_dBm",
                 })
            Assert.True(ds.Contains(name), $"the published DataSet is missing '{name}'");

        var zsConv = ds["Zs_conv"];
        Assert.Equal(2, zsConv.Axes.Count);
        Assert.Equal("harmonic",    zsConv.Axes[0].Name);
        Assert.Equal("harmonic_in", zsConv.Axes[1].Name);
        Assert.Equal(model.Settings.HarmonicCount + 1, zsConv.Axes[0].Values.Length);

        var zIntr = ds["Z_intr"];
        Assert.Equal("side", zIntr.Axes[0].Name);
        Assert.Equal(["source", "load"], zIntr.Axes[0].Labels!);

        // The harmonic axis carries integer ORDERS, matching every other HB result in the repo.
        Assert.Equal([0.0, 1.0, 2.0, 3.0], ds["V"].Axes[1].Values);

        output.WriteLine($"published cubes: {string.Join(", ", ds.Cubes.Keys)}");
    }

    // ── R-hrf-11 — .charm ─────────────────────────────────────────────────────

    [Fact]
    public void R11_CharmRoundTripsSetupAndOnlySetup()
    {
        var model = Model(new LumpedPackage { Rg = 1.5, Lg = 0.4e-9, Ls = 0.2e-9, Cpd = 0.1e-12 })
            with { PavlDbm = 7.5 };
        var terms = Terms(model.Settings.HarmonicCount, new Complex(25, -8), new Complex(40, 15));
        terms.Remove(TerminationSide.Load, 3);      // an UNMARKED band must come back unmarked

        string json = CharmIo.Write(model, terms);
        var (back, backTerms) = CharmIo.Read(json, null, out var unresolved, withMarkers: true);

        Assert.Empty(unresolved);
        Assert.Equal(model.StructuralKey, back.StructuralKey);
        Assert.Equal(model.Bias, back.Bias);
        Assert.Equal(model.Settings, back.Settings);
        Assert.Equal(model.PavlDbm, back.PavlDbm);

        for (int side = 0; side < 2; side++)
            for (int h = 1; h <= model.Settings.HarmonicCount; h++)
            {
                Assert.Equal(terms.IsMarked((TerminationSide)side, h),
                             backTerms.IsMarked((TerminationSide)side, h));
                Assert.Equal(terms.Z((TerminationSide)side, h),
                             backTerms.Z((TerminationSide)side, h));
            }

        // No results. The file is re-solved on open, which is what removes a whole class of
        // stale-data bug — so a results-shaped key appearing here is a defect, not a feature.
        foreach (string forbidden in new[] { "\"V\"", "Zs_conv", "Gamma_intr", "Residual" })
            Assert.DoesNotContain(forbidden, json, StringComparison.Ordinal);
    }

    [Fact]
    public void R11_AnAbsentFieldTakesItsBuiltInDefault()
    {
        // The DataDisplayConfig forward-compatibility rule: an old file must still open after new
        // fields are added, so every field is nullable and absence means "the default".
        var (model, terms) = CharmIo.Read("""{ "FormatVersion": 1 }""", null, out var unresolved,
                                          withMarkers: true);

        Assert.Empty(unresolved);
        Assert.Equal(new HarmonicaSettings(), model.Settings);
        Assert.Equal(5, model.Settings.HarmonicCount);          // D8's default
        Assert.Equal(TerminationSet.UnmarkedBandOhms, terms.Z(TerminationSide.Load, 3).Real);
    }

    [Fact]
    public void R11_AMissingReferencedModelIsNamedRatherThanSubstituted()
    {
        var model = Model() with
        {
            Dut = new DutSpec
            {
                Kind = DutKind.External, TypeName = "some_fet",
                // Provider must carry the "VerilogA|<path>" form CharmIo/VerilogAFileResolver expect
                // for a user-named model file — a bare path is not a recognized provider shape and
                // CharmIo.Read's FindUnresolved never even looks at it (ModelFileIn returns null).
                Provider = VerilogAFileResolver.ProviderNameFor("/no/such/place/vendor-model.osdi"),
            },
        };

        string json = CharmIo.Write(model);
        var back = CharmIo.Read(json, null, out var unresolved);

        var missing = Assert.Single(unresolved);
        Assert.Equal("model", missing.Kind);
        Assert.Contains("vendor-model.osdi", missing.Message, StringComparison.Ordinal);
        output.WriteLine(missing.Message);

        // The document still opens, so the user can re-point it rather than losing the work — and
        // the DUT is not quietly replaced by something that would run.
        Assert.Equal(DutKind.External, back.Dut.Kind);
        Assert.Equal("some_fet", back.Dut.TypeName);
    }

    [Fact]
    public void R11_EmbeddingFilesAreStoredByBareNameAndResolvedBesideTheCharm()
    {
        string dir = Path.Combine(Path.GetTempPath(), "charm-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string snp = Path.Combine(dir, "package.s2p");
            File.WriteAllText(snp, "# HZ S RI R 50\n1e9 0 0 1 0 1 0 0 0\n");

            var model = Model() with
            {
                Embedding = new EmbeddingStack { S2pInFile = Path.Combine("/elsewhere", "package.s2p") },
            };

            string json = CharmIo.Write(model);
            Assert.Contains("\"package.s2p\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("elsewhere", json, StringComparison.Ordinal);

            var back = CharmIo.Read(json, dir, out var unresolved);
            Assert.Empty(unresolved);
            Assert.Equal("package.s2p", back.Embedding.S2pInFile);

            // And from somewhere else, it is reported missing by name rather than silently ignored.
            CharmIo.Read(json, Path.GetTempPath(), out var elsewhere);
            Assert.Single(elsewhere);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ── R-hrf-12 — Touchstone coverage ────────────────────────────────────────

    [Fact]
    public void R12_AnEmbeddingThatDoesNotReachKf0IsRefusedByName()
    {
        string dir = Path.Combine(Path.GetTempPath(), "snp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // 1–4 GHz, against a fixture that needs 3 × 2 GHz = 6 GHz.
            string file = Path.Combine(dir, "short.s2p");
            File.WriteAllText(file,
                "# GHZ S RI R 50\n" +
                "1 0 0 1 0 1 0 0 0\n" +
                "4 0 0 1 0 1 0 0 0\n");

            var model = Model() with { Embedding = new EmbeddingStack { S2pInFile = file } };

            var refusals = TouchstoneCoverage.CheckAll(model);
            var r = Assert.Single(refusals);
            output.WriteLine(r.Refusal);

            // All three facts a user needs to act: the file, the frequency it misses, and its range.
            Assert.Contains("short.s2p", r.Refusal, StringComparison.Ordinal);
            Assert.Contains("6", r.Refusal, StringComparison.Ordinal);
            Assert.Contains("4", r.Refusal, StringComparison.Ordinal);
            Assert.DoesNotContain("extrapolat", r.Refusal.Replace(
                "harmonicaRF will not extrapolate", "", StringComparison.Ordinal),
                StringComparison.OrdinalIgnoreCase);

            // A file that DOES reach it is silent.
            string wide = Path.Combine(dir, "wide.s2p");
            File.WriteAllText(wide, "# GHZ S RI R 50\n1 0 0 1 0 1 0 0 0\n20 0 0 1 0 1 0 0 0\n");
            Assert.Empty(TouchstoneCoverage.CheckAll(
                model with { Embedding = new EmbeddingStack { S2pInFile = wide } }));

            // And the explicit opt-in suppresses the refusal — nothing else.
            Assert.Empty(TouchstoneCoverage.CheckAll(model, allowHoldLastValue: true));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }
}
