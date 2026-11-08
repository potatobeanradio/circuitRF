using System;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// An <c>ExtDevice</c>'s <c>m</c> is circuitRF's multiplier, as a <c>VerilogA</c>'s is: the elaborator scales the
/// whole device and the model never sees the name. It used to be forwarded, so a kit cell passing <c>m=m</c> into its
/// compiled device was refused (<c>has no parameter named 'm'</c>).
///
/// <para>The device is <c>tools/fake-osdi-model</c>'s <c>crf_rc</c> at its own <c>tnom</c>, so <c>I = g0·v</c> and
/// m copies carry m times that.</para>
/// </summary>
[Collection(ExternalProviderRegistryCollection.Name)]
public sealed class ExtDeviceMultiplierTests : IDisposable
{
    private const string WorkerRel = "tools/osdi-worker/osdi-worker";
    private const string ModelRel  = "tools/fake-osdi-model/fake_osdi.osdi";
    private const string HowTo     = "run tools/osdi-worker/build.sh (needs a C compiler)";
    private const double G0        = 0.002;

    public ExtDeviceMultiplierTests()
        => ExternalDeviceRegistry.Register(DeviceWorkerProvider.Launch("osdi",
               FixturePaths.Require(WorkerRel), [FixturePaths.Require(ModelRel)]));

    public void Dispose() => ExternalDeviceRegistry.Clear();

    private static double CurrentAtOneVolt(double m)
    {
        var (lib, tb) = new CnlReader().Read($"""
            ExtDevice:X1  a 0  Provider=osdi Type=crf_rc g0={G0.ToString("R", CultureInfo.InvariantCulture)} c=1e-12 m={m.ToString("R", CultureInfo.InvariantCulture)}
            """);
        var ec = new Elaborator(lib).Elaborate(tb).Components.Single(c => c.InstancePath == "X1");

        Assert.Equal(m, ec.Multiplicity);
        return ec.Evaluate(new PortVoltages([1.0, 0.0])).I[0];
    }

    [FixtureFact(WorkerRel, HowTo)]
    public void M_ScalesTheDevice_AndIsNotSentToTheModel()
    {
        Assert.Equal(1 * G0, CurrentAtOneVolt(1), 12);
        Assert.Equal(3 * G0, CurrentAtOneVolt(3), 12);
    }
}
