using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.External;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;
using static CircuitRF.Ui.Tests.ModelComposer.VerilogA.VaBench;

namespace CircuitRF.Ui.Tests.ModelComposer.VerilogA;

/// <summary>
/// brief-modelcomposer-27 R-mc27-3: self-heating as the module writes it — the thermal network on the internal node
/// <c>ti</c>, one pole or a Foster ladder with its slope, the instance's <c>Rth</c>, <c>Cth</c> and <c>Temp</c>, and the
/// thermal pin left unconnected or wired — against the same <c>.cmodel</c> placed as a <c>ComposedFET</c>.
/// </summary>
[Collection(ExternalDeviceRegistryCollection.Name)]
public sealed class VerilogAThermalTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _previousTools = DeviceWorkerManifest.ToolsDirectory;

    public VerilogAThermalTests(ITestOutputHelper output)
    {
        _output = output;
        if (CompilerAvailability.WorkerDirectory() is { } dir) DeviceWorkerManifest.ToolsDirectory = dir;
    }

    public void Dispose()
    {
        ExternalDeviceRegistry.ResetResolved();
        DeviceWorkerManifest.ToolsDirectory = _previousTools;
    }

    private static readonly ModelTemperature Range = ModelTemperature.Range(0, 150);

    private static readonly Dictionary<string, (ComposedModel Model, string Instance)> Cases = new()
    {
        ["OnePole"] = (VaStateModels.SelfHeating(Range, ModelThermal.Single(50, 1e-9)), ""),
        // Three sections whose resistance moves with the ambient, at an ambient away from Tref.
        ["Ladder"] = (VaStateModels.SelfHeating(Range, new ModelThermal([new(20, 1e-10), new(20, 1e-9), new(10, 1e-8)]) { RthSlope = 0.2, Tref = 25 }),
                      "Temp=60"),
        ["Instance"] = (VaStateModels.SelfHeating(Range, ModelThermal.Single(50, 1e-9)), "N=6 Rth=80 Cth=2e-9 Temp=70"),
        ["LadderInstance"] = (VaStateModels.SelfHeating(Range, new ModelThermal([new(20, 1e-10), new(20, 1e-9), new(10, 1e-8)])),
                              "Rth=30 Cth=5e-9"),
    };

    public static IEnumerable<object[]> Names() => Cases.Keys.Select(k => new object[] { k });

    /// <summary>The pin unconnected — a three-pin placement — still self-heats: the drain current and the thermal node's
    /// rise agree at a dissipating bias, and the rise is real. A network written on the terminal would sit at a 0 K rise
    /// here, and this is the gate that catches it. S across the thermal pole reads the thermal capacitance.</summary>
    [CompilerTheory]
    [MemberData(nameof(Names))]
    public void UnconnectedPin_SelfHeats_AndAgrees(string name)
    {
        var (model, instance) = Cases[name];
        var (cmodel, va) = Files("Thermal" + name, model);
        string bench = Bench(0, 8);
        DcAgrees(name, bench, ComposedFet(cmodel, instance), VaBench.VerilogA(va, instance), ("X1.t", "X1:ti"));

        double rise = Dc(bench + ComposedFet(cmodel, instance), name).V["X1.t"];
        double heated = Dc(bench + VaBench.VerilogA(va, instance), name).Id;
        double isothermal = Dc(bench + VaBench.VerilogA(va, instance + " SelfHeating=0"), name).Id;
        Assert.True(rise > 5, $"{name}: the channel rose only {rise:G3} K");
        Assert.True(Math.Abs(heated - isothermal) > 0.01 * Math.Abs(isothermal), $"{name}: heating moved Id from {isothermal:R} to {heated:R}");

        double worst = SAgrees(name, SBench(0, 8), ComposedFet(cmodel, instance), VaBench.VerilogA(va, instance), [1e5, 3e6, 1e8, 5e9]);
        _output.WriteLine($"{name}: rise {rise:F3} K, worst |ΔS| {worst:E2}");

        // The pin was left off on purpose and the module grounds it itself: circuitRF holds nothing at the ambient there,
        // and reads the internal node, which carries the rise above the module's own ambient, as the device's own.
        var nl = Elaborate(bench + VaBench.VerilogA(va, instance));
        Assert.True(NonlinearDcEngine.Run(nl, Tight).Converged);
        Assert.DoesNotContain(nl.Components, c => c.InstancePath.Contains("__ambient__", StringComparison.Ordinal));
        Assert.DoesNotContain(nl.Warnings, w => w.Contains("thermal", StringComparison.Ordinal));
    }

    /// <summary>Issue 67: the pin DRAWN on a net of its own that reaches nothing else. The model collapses it onto its
    /// internal node <c>ti</c>, where its own <c>Rth</c> sits, so it is referenced and nothing is held: it reads what an
    /// open-ended 1e12 Ω to ground reads, and what the <c>ComposedFET</c> reads. Held at the ambient, as it was, the
    /// rise-type node ran 25 K hot.</summary>
    [CompilerFact]
    public void DrawnButUnwiredPin_IsNotHeld_AndReadsItsOwnRise()
    {
        var (cmodel, va) = Files("ThermalOnePole", Cases["OnePole"].Model);
        string bench = Bench(0, 8);
        var nl = Elaborate(bench + VaBench.VerilogA(va, pin: "tn"));
        Assert.DoesNotContain(nl.Components, c => c.InstancePath.Contains("__ambient__", StringComparison.Ordinal));
        Assert.DoesNotContain(nl.Warnings, w => w.Contains("thermal", StringComparison.Ordinal));

        var drawn = Dc(bench + VaBench.VerilogA(va, pin: "tn"), "drawn");
        var open = Dc(bench + "R:RT tn 0 R=1e12\n" + VaBench.VerilogA(va, pin: "tn"), "open-ended");
        Assert.Equal(open.Id, drawn.Id, Math.Abs(open.Id) * 1e-6);
        Assert.Equal(open.V["tn"], drawn.V["tn"], Math.Abs(open.V["tn"]) * 1e-6);
        Assert.True(drawn.V["tn"] > 5, $"the pin rose only {drawn.V["tn"]:G3} K");
        DcAgrees("drawn", bench, ComposedFet(cmodel, pin: "tn"), VaBench.VerilogA(va, pin: "tn"), ("tn", "tn"));
    }

    /// <summary>The pin wired to an external thermal resistance, which sits in parallel with the model's own network, agrees
    /// with the <c>ComposedFET</c> given the same pin-4 network — Id, and the pin's temperature.</summary>
    [CompilerFact]
    public void WiredPin_AgreesWithTheSamePinNetwork()
    {
        var (cmodel, va) = Files("ThermalOnePole", Cases["OnePole"].Model);
        string bench = Bench(0, 8) + "R:RT tn 0 R=100\n";
        DcAgrees("wired", bench, ComposedFet(cmodel, pin: "tn"), VaBench.VerilogA(va, pin: "tn"), ("tn", "tn"));
        double wired = Dc(bench + ComposedFet(cmodel, pin: "tn"), "wired").V["tn"];
        double alone = Dc(Bench(0, 8) + ComposedFet(cmodel), "alone").V["X1.t"];
        Assert.True(wired < 0.8 * alone, $"the external 100 K/W should lower the rise: {wired:G4} K wired, {alone:G4} K alone");

        // A wired pin with self-heating off sits at a 0 K rise.
        var off = Dc(bench + VaBench.VerilogA(va, "SelfHeating=0", pin: "tn"), "off");
        Assert.True(Math.Abs(off.V["tn"]) < 1e-9, $"SelfHeating=0 left the pin at {off.V["tn"]:G3} K");
    }
}
