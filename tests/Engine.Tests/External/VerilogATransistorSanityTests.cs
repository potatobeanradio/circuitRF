using System;
using System.IO;
using System.Linq;
using CircuitRF.Core;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.External;

/// <summary>
/// A real compiled transistor from a real process kit, placed as an ordinary component and solved by
/// circuitRF's own DC engine.
///
/// <para><b>Why this is the check that matters, and why it is not the same as the worker tests.</b>
/// Those drive the model through the provider and compare its own outputs against each other. This
/// one puts it in a CIRCUIT: elaboration has to expand a four-terminal device, resolve the seven
/// nodes the model collapses, mint the rest, and Newton has to converge on a device whose
/// derivatives arrived over a pipe. Every one of those can fail while every worker test passes.</para>
///
/// <para><b>The assertions are what a transistor DOES</b>, not stored numbers: it turns on above
/// threshold and stays off below, the drain current saturates with drain voltage, and the
/// transconductance is positive. None of that needs a reference simulator, and a device that is
/// silently disconnected — the exact bug this phase found — fails every one of them.</para>
///
/// <para>Skips with a reason when no compiled model is present. See
/// <c>CompiledModelValidationTests</c> for the licensing posture: the compiler is the user's, the
/// sources are the kit's, and the artifact is never committed.</para>
/// </summary>
// Serialises this class against every other one that mutates the process-wide static
// ExternalDeviceRegistry. Six sibling classes in this directory already carry this attribute and
// two did not, which is the whole of the intermittent "External device provider '...' is not
// available: no providers are registered" ExternalDeviceLifetimeTests hit under full-suite load on
// 2026-08-29 — xUnit runs test classes in parallel, so a sibling's Clear() landed between that
// class's Register and its use. THIS class has not been seen to fail; it mutates the same static
// (ResetResolved) from outside the collection, so it has the identical exposure and has only been
// lucky. Added at the same time for that reason. There is deliberately no
// [CollectionDefinition] for this name: a bare [Collection] still groups the classes, and two
// collections never run in parallel with one another.
[Collection("ExternalDeviceRegistry")]
public sealed class VerilogATransistorSanityTests : IDisposable
{
    private const string ModelDirVariable = "CIRCUITRF_OSDI_MODELS";

    /// <summary>
    /// The surface-potential MOSFET a BiCMOS kit supplies for its CMOS devices. Named by FILE, not
    /// by kit: any compiled model of that name works, and nothing here depends on a supplier.
    /// </summary>
    private const string ModelFile = "psp103.osdi";

    private readonly string _previousTools = DeviceWorkerManifest.ToolsDirectory;

    /// <summary>
    /// Points circuitRF's tools folder at the built worker. The application copies it beside itself
    /// at build time; a test assembly has no such copy, so the lookup is aimed at the repository's
    /// own build output instead — the same thing, one directory over.
    /// </summary>
    public VerilogATransistorSanityTests()
    {
        if (FindUpwards("tools/osdi-worker") is { } dir) DeviceWorkerManifest.ToolsDirectory = dir;
    }

    public void Dispose()
    {
        ExternalDeviceRegistry.ResetResolved();
        DeviceWorkerManifest.ToolsDirectory = _previousTools;
    }

    private static string? FindUpwards(string relative)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// The compiled model, or null — in which case every test here returns early. Also null when the
    /// native worker was not built, which is the standing rule for it: a missing C compiler must
    /// never be why somebody cannot run the suite.
    /// </summary>
    private static string? Model()
    {
        string? dir = Environment.GetEnvironmentVariable(ModelDirVariable);
        if (string.IsNullOrWhiteSpace(dir)) return null;

        string worker = Path.Combine(DeviceWorkerManifest.ToolsDirectory, "osdi-worker");
        if (!File.Exists(worker) && !File.Exists(worker + ".exe")) return null;

        string path = Path.Combine(dir, ModelFile);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// A common-source stage: the model's four terminals wired D-G-S-B, a drain resistor to a
    /// supply, and a gate bias. `Pins=4` because the symbol has to know before anything reads the
    /// file; the model's own terminal order decides what each one is.
    /// </summary>
    private static ElaboratedNetlist Stage(string model, double vgs, double vdd, double rd = 1e3)
    {
        string cnl =
            $"Vdc:VDD  vdd 0  Vdc={vdd.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}\n" +
            $"Vdc:VG   g   0  Vdc={vgs.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}\n" +
            $"R:RD     vdd d  R={rd.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}\n" +
            $"VerilogA:M1  d g 0 0  File=\"{model}\" Pins=4 W=10u L=0.13u\n";

        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    /// <summary>Drain current, from the drop across the drain resistor — the circuit's own answer.</summary>
    private static double DrainCurrent(double vgs, double vdd, double rd = 1e3)
    {
        var n = Stage(Model()!, vgs, vdd, rd);
        var r = NonlinearDcEngine.Run(n);
        Assert.True(r.Converged, $"the operating point did not converge at Vgs={vgs}, Vdd={vdd}");

        int d = n.Nodes.IndexOf("d");
        double vd = d == 0 ? 0.0 : r.NodeVoltages[d - 1];
        return (vdd - vd) / rd;
    }

    // ── T1 — it converges at all, and the device is actually in the circuit ───

    [Fact]
    public void T1_AComparedModelTransistorSolvesInACircuit()
    {
        if (Model() is not { } model) return;

        var n = Stage(model, vgs: 1.2, vdd: 1.2);
        var r = NonlinearDcEngine.Run(n);

        Assert.True(r.Converged, "a real compact model must reach an operating point in this circuit");

        // The drain must be pulled DOWN from the supply. A device that elaborated but ended up
        // disconnected — the failure this phase found — leaves the drain sitting at the rail, and
        // every other check here would still pass.
        int d = n.Nodes.IndexOf("d");
        double vd = r.NodeVoltages[d - 1];
        Assert.True(vd < 1.2 - 1e-3, $"the transistor is not conducting into the circuit: Vd = {vd}");
        Assert.True(vd > 0.0, $"Vd = {vd} is below ground, which is not a bias this circuit can reach");
    }

    // ── T2 — it turns on ─────────────────────────────────────────────────────

    /// <summary>
    /// The defining behaviour. Below threshold the device is off and the drain sits near the supply;
    /// above it, current rises steeply and monotonically. Asserted as a RATIO across the sweep, so it
    /// does not depend on the model's own threshold voltage being any particular value.
    /// </summary>
    [Fact]
    public void T2_DrainCurrentRisesMonotonicallyWithGateVoltage()
    {
        if (Model() is null) return;

        double[] vgs = [0.0, 0.3, 0.6, 0.9, 1.2];
        var id = vgs.Select(v => DrainCurrent(v, vdd: 1.2)).ToArray();

        for (int k = 1; k < id.Length; k++)
            Assert.True(id[k] >= id[k - 1] - 1e-12,
                $"Id fell as Vgs rose: Id({vgs[k - 1]}) = {id[k - 1]:G6}, Id({vgs[k]}) = {id[k]:G6}");

        // Off at zero gate bias, on at full — several orders of magnitude apart, which is what makes
        // this a transistor rather than a resistor.
        Assert.True(id[^1] > 1e4 * Math.Max(id[0], 1e-15),
            $"the device barely turned on: Id(0) = {id[0]:G6}, Id({vgs[^1]}) = {id[^1]:G6}");
    }

    // ── T3 — it saturates ────────────────────────────────────────────────────

    /// <summary>
    /// The other defining behaviour, and the one that separates a transistor from a resistor: above
    /// the knee, drain current is nearly independent of drain voltage. Checked with a small drain
    /// resistor so the device — not the load — sets the current.
    /// </summary>
    [Fact]
    public void T3_DrainCurrentSaturatesWithDrainVoltage()
    {
        if (Model() is null) return;

        const double rd = 10.0;
        double idLow  = DrainCurrent(vgs: 1.2, vdd: 0.8, rd: rd);
        double idHigh = DrainCurrent(vgs: 1.2, vdd: 1.2, rd: rd);

        Assert.True(idHigh > 0, "no drain current at all in saturation");

        // Rising, but far less than proportionally: a resistor would give 1.5x here.
        Assert.True(idHigh >= idLow, $"Id fell as Vdd rose: {idLow:G6} -> {idHigh:G6}");
        Assert.True(idHigh < 1.35 * idLow,
            $"Id is not saturating: {idLow:G6} -> {idHigh:G6} for a 50% rise in supply");
    }

    // ── T4 — transconductance has the right sign and size ────────────────────

    /// <summary>
    /// gm = dId/dVgs, taken from the circuit's own answers. Positive by definition for an n-channel
    /// device; a sign error anywhere between the model and the matrix inverts it.
    /// </summary>
    [Fact]
    public void T4_TransconductanceIsPositive()
    {
        if (Model() is null) return;

        const double h = 0.02;
        double gm = (DrainCurrent(1.0 + h, 1.2) - DrainCurrent(1.0 - h, 1.2)) / (2.0 * h);

        Assert.True(gm > 0, $"transconductance came out {gm:G6} S, which is the wrong sign");
        Assert.True(double.IsFinite(gm), "transconductance is not finite");
    }

    // ── T5 — the collapsed terminals stayed connected ────────────────────────

    /// <summary>
    /// The direct regression for this phase's worst bug. This model collapses its drain terminal
    /// onto an internal node, and its bulk terminal plus three internal bulk nodes onto one master.
    /// Giving the TERMINAL the internal node's index drops the user's net — the device then solves
    /// perfectly, disconnected, and every current is zero.
    /// </summary>
    [Fact]
    public void T5_CollapsedTerminalsStillCarryTheUsersNets()
    {
        if (Model() is not { } model) return;

        var n = Stage(model, vgs: 1.2, vdd: 1.2);
        var ec = n.Components.Single(c => c.Model is ExternalDeviceModel);
        var d  = ((ExternalDeviceModel)ec.Model).Descriptor;

        Assert.NotEmpty(d.SlavedNodes);          // the model really does collapse nodes

        // Ground-referenced pairs: node k occupies Nodes[2k]. Every EXTERNAL pin must still sit on
        // the net the netlist gave it.
        int netD = n.Nodes.IndexOf("d"), netG = n.Nodes.IndexOf("g");
        Assert.Equal(netD, ec.Nodes[0]);         // drain
        Assert.Equal(netG, ec.Nodes[2]);         // gate
        Assert.Equal(0,    ec.Nodes[4]);         // source, wired to ground
        Assert.Equal(0,    ec.Nodes[6]);         // bulk, wired to ground
    }
}
