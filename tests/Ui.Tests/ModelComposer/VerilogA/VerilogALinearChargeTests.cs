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
/// brief-modelcomposer-27 R-mc27-5: the linear-charge switches at the bias point D18 has the user state. The point
/// circuitRF's own freeze settled on (<see cref="ComposedCoreModel.FrozenAt"/>) is passed as <c>VgsQ</c>, <c>VdsQ</c> and
/// <c>TjQ</c>; with it the module holds the same charge — S at the bias point, and an HB sweep to compression. Unset, it
/// refuses with its own sentence.
/// </summary>
[Collection(ExternalDeviceRegistryCollection.Name)]
public sealed class VerilogALinearChargeTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _previousTools = DeviceWorkerManifest.ToolsDirectory;

    public VerilogALinearChargeTests(ITestOutputHelper output)
    {
        _output = output;
        if (CompilerAvailability.WorkerDirectory() is { } dir) DeviceWorkerManifest.ToolsDirectory = dir;
    }

    public void Dispose()
    {
        ExternalDeviceRegistry.ResetResolved();
        DeviceWorkerManifest.ToolsDirectory = _previousTools;
    }

    /// <summary>A self-heating device whose gate charge moves with temperature, so <c>TjQ</c> matters; an all-ANN device,
    /// whose charge is continued outside its domain under drive; and drift charges continued beyond their boxes.</summary>
    private static readonly Dictionary<string, ComposedModel> Models = new()
    {
        ["SelfHeating"] = VaStateModels.SelfHeating(ModelTemperature.Range(0, 150), ModelThermal.Single(50, 1e-9)),
        ["Ann"] = VaTestModels.AnnModel(AnnActivation.Tanh),
        ["Drift"] = VaTestModels.Drift(),
    };

    [CompilerTheory]
    [InlineData("SelfHeating", "LinearC=1")]
    [InlineData("SelfHeating", "LinearCgs=1")]
    [InlineData("SelfHeating", "LinearCdg=1")]
    [InlineData("SelfHeating", "LinearCds=1")]
    [InlineData("SelfHeating", "LinearCm=1")]
    [InlineData("SelfHeating", "LinearCgs=1 LinearCds=1")]
    [InlineData("Ann", "LinearC=1")]
    [InlineData("Ann", "LinearCgs=1 LinearCds=1")]
    [InlineData("Drift", "LinearC=1")]
    [InlineData("Drift", "LinearCdg=1 LinearCm=1")]
    public void AtTheFrozenPoint_SAndHbToCompressionAgree(string model, string switches)
    {
        var (cmodel, va) = Files("Linear" + model, Models[model]);
        string what = $"{model} {switches}";

        // S at the bias point: the point is the S bench's own DC solution, as circuitRF's freeze finds it.
        string cf = ComposedFet(cmodel, switches);
        string sBench = SBench(-0.6, 6);
        string point = FrozenPoint(sBench + cf, Tight);
        double worst = SAgrees(what, sBench, cf, VaBench.VerilogA(va, $"{switches} {point}"), [1e9, 5e9, 20e9]);

        double first = double.NaN, last = double.NaN;
        for (double vs = 0.05; vs < 8; vs *= 1.5)
        {
            string hbPoint = FrozenPoint(Drive(vs) + cf);
            var a = HbAgrees($"{what} [{hbPoint}]", vs, cf, VaBench.VerilogA(va, $"{switches} {hbPoint}"));
            if (double.IsNaN(first)) first = a.Gain;
            last = a.Gain;
            if (last < first - 3) break;
        }
        Assert.True(last < first - 1, $"{what}: the gain fell only from {first:F2} to {last:F2} dB");
        _output.WriteLine($"{what}: worst |ΔS| {worst:E2}; gain {first:F3} -> {last:F3} dB");
    }

    /// <summary>A linear-charge switch with the bias point unset is a refusal, never a guess: the module stops at the first
    /// step with its own sentence, naming the switch and both parameters.</summary>
    [CompilerFact]
    public void AnUnsetBiasPoint_IsRefusedWithTheSentence()
    {
        var (_, va) = Files("LinearSelfHeating", Models["SelfHeating"]);
        var nl = Elaborate(Bench(-0.6, 6) + VaBench.VerilogA(va, "LinearCgs=1 VdsQ=6"));
        // Refused at every point, so a refusal rather than an unconverged operating point (brief vendorlib-15 R-vl15-2).
        string report = Assert.ThrowsAny<CircuitRF.Core.Devices.External.ExternalDeviceException>(
            () => NonlinearDcEngine.Run(nl, Tight)).Message;
        Assert.True(report.Contains("model fatal: LinearSelfHeating.cmodel: LinearCgs=1 needs the bias point, and VgsQ or VdsQ is not set; " +
                                    "set VgsQ and VdsQ to the intrinsic gate-source and drain-source voltages at the bias point, in V.",
                                    StringComparison.Ordinal), report);
    }

    /// <summary>A self-heating device with <c>TjQ</c> unset is allowed, and says once that the charge is frozen at the
    /// ambient rather than at the junction temperature.</summary>
    [CompilerFact]
    public void AnUnsetJunctionTemperature_WarnsOnce()
    {
        var (_, va) = Files("LinearSelfHeating", Models["SelfHeating"]);
        var nl = Elaborate(Bench(-0.6, 6) + VaBench.VerilogA(va, "LinearC=1 VgsQ=-0.6 VdsQ=6"));
        Assert.True(NonlinearDcEngine.Run(nl, Tight).Converged);
        string log = WorkerOutput(nl);
        const string warning = "model warning: LinearSelfHeating.cmodel: TjQ is not set, so the charge is frozen at the ambient";
        Assert.Equal(1, log.Split(warning).Length - 1);
    }
}
