using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// The whole path a worker-backed device travels: registry → provider → instance →
/// <see cref="ExternalDeviceModel"/> → the <see cref="NonlinearResult"/> the engine stamps.
///
/// <para>The per-layer tests each check their own layer's contract. What they cannot show is that
/// the layers agree — that the matrix reaching the solver holds the numbers the model produced, in
/// the orientation the solver assumes. That is what these check.</para>
/// </summary>
[Collection(ExternalProviderRegistryCollection.Name)]
public sealed class DeviceWorkerEndToEndTests
{
    private static ExternalDeviceModel NewModel(out FakeDeviceWorker worker)
    {
        worker = new FakeDeviceWorker();
        var provider = new DeviceWorkerProvider("test-worker", worker);
        var instance = provider.Create(FakeDeviceWorker.NonlinearType, new Dictionary<string, string>());
        return new ExternalDeviceModel(instance, provider.Name, "X1");
    }

    private static double[] Ramp(int n) => Enumerable.Range(0, n).Select(i => 0.25 + i).ToArray();

    [Fact]
    public void AWorkerBackedDevice_PresentsOnePortPerNode()
    {
        var model = NewModel(out _);

        Assert.Equal(FakeDeviceWorker.Nodes,         model.PortCount);
        Assert.Equal(FakeDeviceWorker.ExternalPins,  model.ExternalPinCount);
        Assert.Equal(FakeDeviceWorker.InternalNodes, model.InternalNodeCount);
        Assert.Equal(ModelKind.Nonlinear,            model.Kind);
    }

    [Fact]
    public void TheStampReachingTheEngine_HoldsWhatTheModelProduced()
    {
        var model = NewModel(out _);
        double[] v = Ramp(FakeDeviceWorker.Nodes);

        NonlinearResult r = model.Evaluate(new PortVoltages(v));

        Assert.Equal(1 * v[0] + 3 * v[1], r.I[0],     12);
        Assert.Equal(0.5 * v[2],          r.Q[2],     12);
        Assert.Equal(3.0,                 r.Dg[0, 1], 12);   // not transposed, across every layer
        Assert.Equal(0.0,                 r.Dg[1, 0], 12);
        Assert.Equal(0.5,                 r.Dc[4, 4], 12);
    }

    [Fact]
    public void AProviderRegisteredByName_IsReachableAsAnyOtherIs()
    {
        var worker = new FakeDeviceWorker();
        var provider = new DeviceWorkerProvider("registered-worker", worker);

        try
        {
            ExternalDeviceRegistry.Register(provider);

            var found = ExternalDeviceRegistry.Require("registered-worker");

            Assert.Same(provider, found);
            Assert.Contains(found.Describe(), t => t.TypeId == FakeDeviceWorker.NonlinearType);
        }
        finally
        {
            ExternalDeviceRegistry.Unregister("registered-worker");
            provider.Dispose();
        }
    }

    [Fact]
    public void AFailureInsideTheWorker_ArrivesNamingTheInstanceAndTheProvider()
    {
        // An error from a device deep in a sweep is useless without saying which device it was.
        var model = NewModel(out var worker);
        worker.FailPoints.Add(0);

        var ex = Assert.Throws<ExternalDeviceException>(
            () => model.Evaluate(new PortVoltages(Ramp(FakeDeviceWorker.Nodes))));

        Assert.Contains(FakeDeviceWorker.NonlinearType, ex.Message);
    }

    [Fact]
    public void ThermalNodesAreVisibleToTheEngineAsOrdinaryNodes()
    {
        // A thermal node is an ordinary unknown whose voltage happens to be a temperature. The
        // classification is for labelling and connection policy — it must not shrink the stamp.
        var model = NewModel(out _);

        Assert.Equal(NodeQuantityKind.Thermal,
                     model.Descriptor.Nodes.Single(n => n.Index == 3).QuantityKind);
        Assert.Equal(FakeDeviceWorker.Nodes, model.PortCount);
        Assert.Equal(FakeDeviceWorker.Nodes,
                     model.Evaluate(new PortVoltages(Ramp(FakeDeviceWorker.Nodes))).I.Length);
    }
}
