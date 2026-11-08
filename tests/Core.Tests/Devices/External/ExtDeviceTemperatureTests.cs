using System;
using System.Linq;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// An <c>ExtDevice</c> served by a worker is set up at the design's ambient (<c>temp</c>), as a <c>VerilogA</c> is.
/// It used to be sent no temperature, so the worker set every compiled model up at its own 300 K whatever the
/// design stated.
///
/// <para>The device is <c>tools/fake-osdi-model</c>'s <c>crf_rc</c>: <c>g(T) = g0·(1 + tc·(T − tnom))</c>, T in
/// kelvin, so with <c>tc = 0.01</c> and <c>tnom = 300</c> it conducts exactly twice as much at 400 K.</para>
/// </summary>
[Collection(ExternalProviderRegistryCollection.Name)]
public sealed class ExtDeviceTemperatureTests : IDisposable
{
    private const string WorkerRel = "tools/osdi-worker/osdi-worker";
    private const string ModelRel  = "tools/fake-osdi-model/fake_osdi.osdi";
    private const string HowTo     = "run tools/osdi-worker/build.sh (needs a C compiler)";

    public ExtDeviceTemperatureTests()
        => ExternalDeviceRegistry.Register(DeviceWorkerProvider.Launch("osdi",
               FixturePaths.Require(WorkerRel), [FixturePaths.Require(ModelRel)]));

    public void Dispose() => ExternalDeviceRegistry.Clear();

    private static double ConductanceAt(string globals)
    {
        var (lib, tb) = new CnlReader().Read($"""
            {globals}
            ExtDevice:X1  a 0  Provider=osdi Type=crf_rc g0=0.002 c=1e-12 tc=0.01 tnom=300
            """);
        var ec = new Elaborator(lib).Elaborate(tb).Components.Single(c => c.InstancePath == "X1");
        return ec.Evaluate(new PortVoltages([1.0, 0.0])).I[0];
    }

    [FixtureFact(WorkerRel, HowTo)]
    public void TheDesignsAmbient_ReachesTheWorker()
    {
        Assert.Equal(0.002, ConductanceAt(""), 12);                 // no `temp`: 26.85 °C, exactly 300 K
        Assert.Equal(0.004, ConductanceAt("temp = 126.85"), 12);    // 400 K
    }
}
