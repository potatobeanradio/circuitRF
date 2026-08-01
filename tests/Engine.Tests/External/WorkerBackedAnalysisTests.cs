using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.External;

/// <summary>
/// The last claim in the chain, and the only one that matters to a user: <b>press Run and get an
/// answer</b> — from a netlist that merely names a kit, through a device model evaluated by a
/// separate process, to a converged operating point.
///
/// <para>Every layer below this is tested on its own. What none of those can show is that they line
/// up: that elaboration resolves a provider nobody registered, that the engine's Newton iteration
/// survives a device whose derivatives arrive over a pipe, and that the result is right rather than
/// merely converged. A device that silently conducts nothing converges beautifully.</para>
///
/// <para>The worker is <c>tools/DeviceWorkerExample</c>, which shares no code with circuitRF, and
/// every expected number here is computed from the transistor's equations directly — never by
/// asking the worker and comparing it to itself.</para>
/// </summary>
[Collection("ExternalDeviceRegistry")]
public sealed class WorkerBackedAnalysisTests : IDisposable
{
    private const string ProviderName = "ExampleKit";
    private const string TypeId       = "example_fet_v1";

    // The worker's own defaults, restated here rather than queried.
    private const double Beta = 0.02, Vth = 0.7, Lambda = 0.01, Ggs = 1e-9;

    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "crf-run-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// Stands a kit up the way an import would: a folder named for the kit, holding a manifest that
    /// points at the worker. Nothing is registered — resolution has to find it.
    /// </summary>
    public WorkerBackedAnalysisTests()
    {
        ExternalDeviceRegistry.Clear();

        string kitDir = Path.Combine(_workspace, "pdk", ProviderName);
        Directory.CreateDirectory(kitDir);

        File.WriteAllText(Path.Combine(kitDir, DeviceWorkerManifest.FileName), $$"""
            { "provider": "{{ProviderName}}",
              "workers": [ { "platform": "any", "command": {{ToJson(WorkerPath)}} } ] }
            """);

        ExternalDeviceRegistry.AddResolver(
            new DeviceWorkerProviderResolver([Path.Combine(_workspace, "pdk")]));
    }

    public void Dispose()
    {
        ExternalDeviceRegistry.Clear();      // also ends the worker it started
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best effort */ }
    }

    private static string ToJson(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private static string WorkerPath
    {
        get
        {
            string? dir = typeof(WorkerBackedAnalysisTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "DeviceWorkerExampleDir")?.Value;

            Assert.False(string.IsNullOrWhiteSpace(dir), "the build did not record the worker's location");

            string exe = Path.Combine(Path.GetFullPath(dir!),
                "DeviceWorkerExample" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : ""));

            Assert.True(File.Exists(exe), $"the reference worker was not built at '{exe}'");
            return exe;
        }
    }

    private static string N(double v) => v.ToString("G17", CultureInfo.InvariantCulture);

    // ── the device, derived independently ─────────────────────────────────────

    private static double DrainCurrent(double vgs, double vds)
    {
        double over = vgs - Vth;
        return over > 0 && vds > 0 ? Beta * over * over * (1 + Lambda * vds) : 0.0;
    }

    /// <summary>
    /// Operating point with a source-degeneration resistor, solved by scalar fixed-point iteration —
    /// no matrices and no engine code. Source current is the channel current plus gate leakage,
    /// since the gate's leakage returns through the source.
    /// </summary>
    private static double SolveDegeneratedOracle(double vg, double vdd, double rs)
    {
        double vs = 0.0;
        for (int k = 0; k < 1_000_000; k++)
        {
            double id  = DrainCurrent(vg - vs, vdd - vs);
            double igs = Ggs * (vg - vs);
            double next = (id + igs) * rs;
            double step = 0.05 * (next - vs);
            vs += step;
            if (Math.Abs(step) <= 1e-16 * Math.Max(1.0, Math.Abs(vs))) return vs;
        }

        Assert.Fail("the oracle did not converge — fix the oracle, not the engine");
        return 0;
    }

    private static (NonlinearDcEngine.DcResult Result, ElaboratedNetlist Netlist) Run(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return (NonlinearDcEngine.Run(nl), nl);
    }

    private static double NodeVoltage(NonlinearDcEngine.DcResult r, ElaboratedNetlist nl, string net)
    {
        int index = nl.Nodes.GetOrAssign(net);
        return index == 0 ? 0.0 : r.NodeVoltages[index - 1];
    }

    /// <summary>
    /// Drain current, measured by a probe in the drain lead. Positive flows into the drain, so this
    /// is the device current under the same convention the provider reports.
    /// </summary>
    private static double DrainProbe(NonlinearDcEngine.DcResult r) => r.ProbeCurrents["IPd"];

    /// <summary>The drain supply and its probe, shared by every fixture below.</summary>
    private static string DrainSupply(double vdd) => $"""
        Vdc:VD     dd 0  Vdc={N(vdd)}
        IProbe:IPd dd d
        """;

    // ── press Run ─────────────────────────────────────────────────────────────

    [Fact]
    public void ANetlistThatMerelyNamesAKit_SolvesThroughAWorkerProcess()
    {
        // Nothing registered a provider. Elaboration asks for one by name, resolution finds the
        // kit's manifest, and a worker starts — all of it inside this one call.
        const double vg = 2.0, vdd = 5.0;

        var (result, netlist) = Run($"""
            Vdc:VG   g 0  Vdc={N(vg)}
            {DrainSupply(vdd)}
            ExtDevice:X1  g d 0  Provider={ProviderName} Type={TypeId}
            """);

        Assert.True(result.Converged);

        // With the source grounded and both other nodes driven, the operating point is closed-form.
        double expected = DrainCurrent(vg, vdd);
        double drainCurrent = DrainProbe(result);

        Assert.Equal(expected, drainCurrent, 9);
        Assert.True(expected > 1e-3, "the fixture must actually conduct, or this proves nothing");
    }

    [Fact]
    public void TheDeviceIsOffBelowThreshold_RatherThanConvergingToAnythingItLikes()
    {
        // The companion to the test above. A device that conducts nothing converges perfectly, so
        // "it converged" is only evidence when both the on and off cases land where they should.
        var (result, _) = Run($"""
            Vdc:VG   g 0  Vdc={N(0.3)}
            {DrainSupply(5.0)}
            ExtDevice:X1  g d 0  Provider={ProviderName} Type={TypeId}
            """);

        Assert.True(result.Converged);

        // Not exactly zero, and it should not be: the engine adds gmin (1e-12 S) to every voltage
        // node for continuity, so 5 V leaks exactly 5 pA regardless of the device. Asserting zero
        // would be asserting against the solver's own regularisation. What matters is that the
        // channel is off — nine orders below the ~34 mA it passes when on.
        double drainCurrent = DrainProbe(result);

        Assert.True(Math.Abs(drainCurrent) < 1e-9,
            $"the device should be off below threshold, but passes {drainCurrent:G6} A");
    }

    [Fact]
    public void FeedbackThroughTheCircuit_IsSolvedByNewtonAgainstTheWorkersOwnDerivatives()
    {
        // The real test of the Jacobian crossing the pipe: with source degeneration the operating
        // point depends on itself, so a wrong derivative either fails to converge or lands somewhere
        // else. Both would pass a test that only drove the device open-loop.
        const double vg = 3.0, vdd = 5.0, rs = 10.0;

        var (result, netlist) = Run($"""
            Vdc:VG   g 0  Vdc={N(vg)}
            {DrainSupply(vdd)}
            R:RS     s 0  R={N(rs)}
            ExtDevice:X1  g d s  Provider={ProviderName} Type={TypeId}
            """);

        Assert.True(result.Converged);

        double expectedVs = SolveDegeneratedOracle(vg, vdd, rs);

        Assert.True(expectedVs > 0.05,
            "the fixture must actually degenerate, or this is the open-loop test again");
        Assert.Equal(expectedVs, NodeVoltage(result, netlist, "s"), 6);
    }

    [Fact]
    public void ParametersOnTheNetlistLine_ReachTheModelInsideTheWorker()
    {
        // The last mile of the parameter path: netlist text -> elaboration -> provider -> process.
        const double vg = 2.0, vdd = 5.0, beta = 0.05;

        var (result, _) = Run($"""
            Vdc:VG   g 0  Vdc={N(vg)}
            {DrainSupply(vdd)}
            ExtDevice:X1  g d 0  Provider={ProviderName} Type={TypeId} Beta={N(beta)}
            """);

        Assert.True(result.Converged);

        double expected = beta * Math.Pow(vg - Vth, 2) * (1 + Lambda * vdd);
        double drainCurrent = DrainProbe(result);

        Assert.Equal(expected, drainCurrent, 9);
    }

    [Fact]
    public void SeveralDevicesInOneDesign_ShareASingleWorker()
    {
        // A worker per device would be a process per transistor. They must also stay independent:
        // different gate drives have to give different currents.
        var (result, _) = Run($"""
            Vdc:VG1  g1 0  Vdc={N(2.0)}
            Vdc:VG2  g2 0  Vdc={N(3.0)}
            {DrainSupply(5.0)}
            ExtDevice:X1  g1 d 0  Provider={ProviderName} Type={TypeId}
            ExtDevice:X2  g2 d 0  Provider={ProviderName} Type={TypeId}
            """);

        Assert.True(result.Converged);

        double expected = DrainCurrent(2.0, 5.0) + DrainCurrent(3.0, 5.0);
        double drainCurrent = DrainProbe(result);

        Assert.Equal(expected, drainCurrent, 9);
        Assert.Single(ExternalDeviceRegistry.ProviderNames);
    }

    [Fact]
    public void ASweepReusesTheWorkerItAlreadyStarted()
    {
        // Restarting a process per bias point would dominate any sweep. The provider is resolved
        // once and cached, so the second solve costs no process launch.
        string Cnl(double vg) => $"""
            Vdc:VG   g 0  Vdc={N(vg)}
            {DrainSupply(5.0)}
            ExtDevice:X1  g d 0  Provider={ProviderName} Type={TypeId}
            """;

        Run(Cnl(1.5));
        var resolved = ExternalDeviceRegistry.Find(ProviderName);

        for (double vg = 1.6; vg <= 3.0; vg += 0.1)
        {
            var (result, _) = Run(Cnl(vg));
            Assert.True(result.Converged);

            double expected = DrainCurrent(vg, 5.0);
            Assert.Equal(expected, DrainProbe(result), 9);
        }

        Assert.Same(resolved, ExternalDeviceRegistry.Find(ProviderName));
    }

    // ── when it cannot work ───────────────────────────────────────────────────

    [Fact]
    public void ANetlistNamingAKitThatIsNotInstalled_SaysWhereItLooked()
    {
        // The message a user gets when a design is opened without its kit. It has to name the place
        // to put one, or there is nothing to act on.
        var ex = Assert.Throws<ExternalDeviceException>(() => Run($"""
            {DrainSupply(5.0)}
            ExtDevice:X1  g d 0  Provider=NotInstalledKit Type={TypeId}
            """));

        Assert.Contains("NotInstalledKit", ex.Message);
        Assert.Contains(Path.Combine(_workspace, "pdk"), ex.Message);
    }

    [Fact]
    public void ANetlistNamingATypeTheKitDoesNotServe_ListsWhatItDoes()
    {
        var ex = Assert.Throws<ExternalDeviceException>(() => Run($"""
            {DrainSupply(5.0)}
            ExtDevice:X1  g d 0  Provider={ProviderName} Type=no_such_device
            """));

        Assert.Contains("no_such_device", ex.Message);
        Assert.Contains(TypeId, ex.Message);
    }
}
