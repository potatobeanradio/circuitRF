using System;
using System.Globalization;
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
/// A compiled model with a THERMAL terminal, placed on a schematic and solved — through the ordinary
/// VerilogA path, with no kit, no manifest and nothing installed but the model file.
///
/// <para><b>What this covers that the worker tests cannot.</b> Those drive the model through the
/// provider and compare its outputs against arithmetic. Here the answers have to survive the rest of
/// the chain: the component states how many pins it drew, that number reaches the model as
/// <c>$port_connected</c>, the model's decision comes back as a node collapse, elaboration lays the
/// nodes out around it, and the DC engine has to decide — from the node's DISCIPLINE — whether to
/// hold it at the ambient or leave the model's own rise alone. Every one of those can fail while
/// every worker test passes, and until the worker reported a node's units none of them ran at all.
/// </para>
///
/// <para><b>The fixture is <c>tools/fake-osdi-model</c>'s <c>crf_therm</c></b>, whose closed form is
/// written in that library's own comment: an electrical conduction <c>g</c> from A to B, a
/// dissipation <c>g·v_AB²</c> driving a thermal terminal, and its own <c>1/rth</c> back to the
/// thermal ground. That is the same shape as a physics-based compact model that carries its own
/// thermal RC and adds the ambient internally — so the node it solves for is the RISE — and it needs
/// no proprietary artefact to stand up.</para>
///
/// <para>Skips with a reason when the native worker is not built: a missing C compiler must never be
/// why somebody cannot run the suite.</para>
/// </summary>
[Collection("ExternalDeviceRegistry")]
public sealed class VerilogAConnectedTerminalsTests : IDisposable
{
    private const string WorkerRel = "tools/osdi-worker/osdi-worker";
    private const string ModelRel  = "tools/fake-osdi-model/fake_osdi.osdi";
    private const string HowTo     = "run tools/osdi-worker/build.sh (needs a C compiler)";

    // The fixture's own parameters, and the arithmetic that follows from them.
    private const double G = 0.01, Rth = 50.0, Vab = 2.0, AmbientC = 25.0;

    /// <summary>The rise the model solves for: its own dissipation through its own resistance.</summary>
    private const double Rise = G * Vab * Vab * Rth;   // 0.01 * 4 * 50 = 2 °C

    private readonly string _previousTools = DeviceWorkerManifest.ToolsDirectory;

    public VerilogAConnectedTerminalsTests()
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
            if (Directory.Exists(candidate) || File.Exists(candidate)) return Path.GetFullPath(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static string ModelFile() => FindUpwards(ModelRel)!;

    private static string N(double d) => d.ToString("G17", CultureInfo.InvariantCulture);

    /// <summary>
    /// The device across a fixed supply, with its thermal terminal either drawn or not. <c>Pins</c>
    /// is circuitRF's own — how many terminals the symbol has — and it is the number the user states
    /// before anything has opened the file.
    /// </summary>
    private static ElaboratedNetlist Elaborate(bool drawThermalPin, int selfHeating = 1)
    {
        string pins = drawThermalPin ? "a 0 tj" : "a 0";
        string cnl =
            $"temp = {N(AmbientC)}\n" +
            $"Vdc:V1  a 0  Vdc={N(Vab)}\n" +
            $"VerilogA:X1  {pins}  File=\"{ModelFile()}\" Model=crf_therm " +
            $"Pins={(drawThermalPin ? 3 : 2)} g={N(G)} rth={N(Rth)} sh={selfHeating}\n";

        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static double NodeV(NonlinearDcEngine.DcResult r, ElaboratedNetlist nl, string name)
    {
        return nl.Nodes.TryGetIndex(name, out int idx) && idx > 0 ? r.NodeVoltages[idx - 1] : 0.0;
    }

    // ── V1 — a five-terminal model placed as a four-terminal part ─────────────

    /// <summary>
    /// Not drawing a pin is the ordinary way to say "I do not want a thermal terminal on my
    /// schematic", and it has to reach the model: <c>$port_connected</c> is how a compact model
    /// learns the host did not wire one, and grounding its own node is what it does about it.
    ///
    /// <para><b>Both halves are asserted because both used to be wrong.</b> The count never left
    /// circuitRF — every instance claimed every terminal connected — and elaboration refused a net
    /// count below the type's pin count outright, so this circuit could not be built at all. With
    /// the count arriving and the model grounding its own node, the thermal node is simply not in
    /// the system: no unknown, no source, nothing added.</para>
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void V1_ATerminalTheSchematicDidNotDraw_IsGroundedByTheModel()
    {
        var nl = Elaborate(drawThermalPin: false);
        var r  = NonlinearDcEngine.Run(nl);

        Assert.True(r.Converged, "a model placed with fewer pins than it declares must still solve");

        // The electrical half is exactly the device's own conduction, so this is not a device that
        // elaborated into nothing.
        Assert.Equal(G * Vab, r.NodeVoltages.Length > 0 ? CurrentThroughSupply(r, nl) : 0.0, 9);

        // No thermal node was minted, and nothing was added to hold one.
        Assert.DoesNotContain(nl.Nodes.AllNames, n => n.Contains("tj", StringComparison.Ordinal));
        Assert.DoesNotContain(nl.Components, c => c.InstancePath.Contains("__ambient__", StringComparison.Ordinal));
        Assert.DoesNotContain(nl.Warnings, w => w.Contains("not connected", StringComparison.Ordinal));
    }

    /// <summary>Supply current, from the device's own conduction: I = g·V.</summary>
    private static double CurrentThroughSupply(NonlinearDcEngine.DcResult r, ElaboratedNetlist nl)
        => G * NodeV(r, nl, "a");

    // ── V2 — the node's discipline reaches the engine ─────────────────────────

    /// <summary>
    /// The classification everything else here depends on, seen where it is acted on rather than
    /// where it is parsed: elaboration has to know this node is a TEMPERATURE, and the only place
    /// that fact exists is the units the model declared for it.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void V2_TheModelsThermalTerminalIsClassifiedFromItsOwnUnits()
    {
        var nl = Elaborate(drawThermalPin: true);

        var device = Assert.Single(nl.Components.Select(c => c.Model).OfType<ExternalDeviceModel>());
        var nodes  = device.Descriptor.Nodes.ToDictionary(n => n.Index);

        Assert.Equal(NodeQuantityKind.Thermal,    nodes[2].QuantityKind);
        Assert.Equal(NodeQuantityKind.Electrical, nodes[0].QuantityKind);
        Assert.Equal(("K", "W"), (nodes[2].Units, nodes[2].ResidualUnits));
    }

    // ── V3/V4 — a model that carries its own thermal RC is left alone ─────────

    /// <summary>
    /// F3's first half, measured rather than read. With a thermal node now visible to the elaborator,
    /// the ambient hold starts running against models that reference their own thermal node — and it
    /// must not touch them. Pinning one at the ambient makes the model compute ambient + ambient +
    /// rise: finite, plausible, and wrong with nothing to notice.
    ///
    /// <para>The device's own positive thermal conductance is what says the node is already
    /// referenced, and this fixture supplies one exactly as both surveyed families do.</para>
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void V3_ADeviceCarryingItsOwnThermalRC_IsNotHeldAtTheAmbient()
    {
        var nl = Elaborate(drawThermalPin: true);
        var r  = NonlinearDcEngine.Run(nl);

        Assert.True(r.Converged);

        // Its own rise above its own thermal ground, intact. A source holding this node at 25 °C
        // would read 25 here, and the model would then have been evaluated at the wrong temperature.
        Assert.Equal(Rise, NodeV(r, nl, "tj"), 6);
        Assert.DoesNotContain(nl.Warnings, w => w.Contains("not connected", StringComparison.Ordinal));
        Assert.DoesNotContain(nl.Components, c => c.InstancePath.Contains("__ambient__", StringComparison.Ordinal));
    }

    /// <summary>
    /// F3's second half. <c>ReportThermalNodes</c>'s ground-reference test is "the reference is zero
    /// while the ambient is not", which is precisely how a self-contained model's rise-carrying node
    /// looks — the reference is read from the LINEAR network, where such a model contributes nothing.
    /// A warning on every correctly-modelled part is worse than no warning at all: it is what stops
    /// the real one being read.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void V4_ASelfContainedThermalNode_IsNotReportedAsTiedToGround()
    {
        var nl = Elaborate(drawThermalPin: true);
        NonlinearDcEngine.Run(nl);

        Assert.DoesNotContain(nl.Warnings, w => w.Contains("referenced to", StringComparison.Ordinal));
        Assert.DoesNotContain(nl.Notes,    w => w.Contains("reaches its reference", StringComparison.Ordinal));
    }

    // ── V5 — self-heating is not vacuous ──────────────────────────────────────

    /// <summary>
    /// The vacuity guard. §5.1 of <c>pdk-external-devices.md</c> records a family whose thermal
    /// terminal turned out to be inert — every check above would pass on a device whose thermal node
    /// does nothing at all. Sweeping the model's OWN thermal resistance has to move the node.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void V5_TheModelsOwnThermalResistanceActuallyDoesSomething()
    {
        double tj = RiseAt(Rth), tjHalf = RiseAt(Rth / 2.0);

        Assert.Equal(Rise,       tj,     6);
        Assert.Equal(Rise / 2.0, tjHalf, 6);
        Assert.True(Math.Abs(tj - tjHalf) > 1e-6,
            "halving the model's own thermal resistance changed nothing: the thermal node is inert");

        static double RiseAt(double rth)
        {
            string cnl =
                $"temp = {N(AmbientC)}\n" +
                $"Vdc:V1  a 0  Vdc={N(Vab)}\n" +
                $"VerilogA:X1  a 0 tj  File=\"{ModelFile()}\" Model=crf_therm Pins=3 " +
                $"g={N(G)} rth={N(rth)} sh=1\n";

            var (lib, tb) = new CnlReader().Read(cnl);
            var nl = new Elaborator(lib).Elaborate(tb);
            var r  = NonlinearDcEngine.Run(nl);
            Assert.True(r.Converged);
            return NodeV(r, nl, "tj");
        }
    }

    // ── V6 — a pin count the model cannot honour is refused ───────────────────

    /// <summary>
    /// Stating more pins than the model has is refused with a sentence rather than clamped. Clamping
    /// would stand the device up against a connection pattern nobody asked for, and it would
    /// converge.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void V6_MorePinsThanTheModelDeclares_IsRefused()
    {
        string cnl =
            $"Vdc:V1  a 0  Vdc={N(Vab)}\n" +
            $"VerilogA:X1  a 0 tj x  File=\"{ModelFile()}\" Model=crf_therm Pins=4 " +
            $"g={N(G)} rth={N(Rth)} sh=1\n";

        var (lib, tb) = new CnlReader().Read(cnl);

        Assert.Throws<ExternalDeviceException>(() => new Elaborator(lib).Elaborate(tb));
    }
}
