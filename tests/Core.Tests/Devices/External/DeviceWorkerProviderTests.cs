using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// The provider that fronts an out-of-process device worker, driven against a worker that speaks
/// the real wire format (<see cref="FakeDeviceWorker"/>).
///
/// <para>What is worth testing here is not that a round trip happens — it is that the numbers come
/// back in the right slots and with the right signs. A transposed Jacobian, a charge block read as
/// a current block, or a sign flip applied twice all converge to a confident wrong answer, so each
/// has a test aimed at it directly.</para>
/// </summary>
public sealed class DeviceWorkerProviderTests
{
    private static (DeviceWorkerProvider Provider, FakeDeviceWorker Worker) NewProvider()
    {
        var worker = new FakeDeviceWorker();
        return (new DeviceWorkerProvider("test-worker", worker), worker);
    }

    private static IExternalDeviceInstance NewInstance(out FakeDeviceWorker worker)
    {
        var (provider, w) = NewProvider();
        worker = w;
        return provider.Create(FakeDeviceWorker.NonlinearType, new Dictionary<string, string>());
    }

    private static double[] Ramp(int n, double start = 0.25)
        => Enumerable.Range(0, n).Select(i => start + i).ToArray();

    // ── describe ──────────────────────────────────────────────────────────────

    [Fact]
    public void EveryTypeTheWorkerOffers_IsDescribed()
    {
        var (provider, _) = NewProvider();

        var types = provider.Describe();

        Assert.Equal(2, types.Count);
        Assert.Contains(types, t => t.TypeId == FakeDeviceWorker.NonlinearType);
        Assert.Contains(types, t => t.TypeId == FakeDeviceWorker.LinearOnlyType);
    }

    [Fact]
    public void ATypesShapeAndCapabilities_ComeFromTheWorker()
    {
        var (provider, _) = NewProvider();

        var t = provider.Describe().Single(d => d.TypeId == FakeDeviceWorker.NonlinearType);

        Assert.Equal(FakeDeviceWorker.ExternalPins,  t.ExternalPinCount);
        Assert.Equal(FakeDeviceWorker.InternalNodes, t.InternalNodeCount);
        Assert.Equal(FakeDeviceWorker.Nodes,         t.NodeCount);
        Assert.True(t.SupportsNonlinear);
        Assert.True(t.SupportsLinear);
    }

    [Fact]
    public void ALinearOnlyType_IsDescribedRatherThanHidden()
    {
        // Hiding it would misreport what the library contains; refusing to instantiate it is the
        // worker's job, and it does that with a reason.
        var (provider, _) = NewProvider();

        var t = provider.Describe().Single(d => d.TypeId == FakeDeviceWorker.LinearOnlyType);

        Assert.False(t.SupportsNonlinear);
        Assert.True(t.SupportsLinear);
    }

    [Fact]
    public void ParameterKinds_AreCarriedThrough_AndAnUnknownKindBecomesText()
    {
        var (provider, _) = NewProvider();

        var pars = provider.Describe().Single(d => d.TypeId == FakeDeviceWorker.NonlinearType).Parameters;

        Assert.Equal(ExternalParamKind.Double,   pars.Single(p => p.Name == "Scale").Kind);
        Assert.Equal(ExternalParamKind.Int,      pars.Single(p => p.Name == "Fingers").Kind);
        Assert.Equal(ExternalParamKind.FilePath, pars.Single(p => p.Name == "File").Kind);

        // An unrecognised kind must not be coerced — passing the text through verbatim is the only
        // handling that cannot corrupt a value circuitRF does not understand.
        Assert.Equal(ExternalParamKind.String,   pars.Single(p => p.Name == "Note").Kind);
    }

    [Fact]
    public void DescribeIsAskedOnceOnly_HoweverOftenItIsQueried()
    {
        var (provider, worker) = NewProvider();

        provider.Describe();
        provider.Describe();
        provider.Describe();

        Assert.Equal(1, worker.Commands.Count(c => c == "describe"));
    }

    // ── create ────────────────────────────────────────────────────────────────

    [Fact]
    public void ParameterValues_ReachTheWorkerTypedAsTheTypeDeclaredThem()
    {
        var (provider, worker) = NewProvider();

        using var _ = provider.Create(FakeDeviceWorker.NonlinearType, new Dictionary<string, string>
        {
            ["Scale"]   = "2.5",
            ["Fingers"] = "194",
            ["File"]    = @"data/model file.dat",
        });

        var given = worker.Instances.Values.Single();
        Assert.Equal("2.5",                  given["Scale"]);
        Assert.Equal("194",                  given["Fingers"]);
        Assert.Equal(@"data/model file.dat", given["File"]);   // a space must survive as one value
    }

    [Fact]
    public void AParameterLeftBlank_IsNotSent_SoTheModelKeepsItsOwnDefault()
    {
        var (provider, worker) = NewProvider();

        using var _ = provider.Create(FakeDeviceWorker.NonlinearType, new Dictionary<string, string>
        {
            ["Scale"]   = "2.5",
            ["Fingers"] = "",
        });

        var given = worker.Instances.Values.Single();
        Assert.True(given.ContainsKey("Scale"));
        Assert.False(given.ContainsKey("Fingers"));
    }

    [Fact]
    public void AMisspelledParameter_IsRefusedByName_NotSilentlyDropped()
    {
        // The worker matches by keyword and ignores what it does not know, so a typo would
        // otherwise present as a device quietly running on a default value.
        var (provider, _) = NewProvider();

        var ex = Assert.Throws<ExternalDeviceException>(() =>
            provider.Create(FakeDeviceWorker.NonlinearType, new Dictionary<string, string> { ["Scaale"] = "2.5" }));

        Assert.Contains("Scaale", ex.Message);
        Assert.Contains("Scale",  ex.Message);      // and says what it should have been
    }

    [Fact]
    public void ANumericParameterGivenText_IsRefusedBeforeItReachesTheModel()
    {
        var (provider, _) = NewProvider();

        var ex = Assert.Throws<ExternalDeviceException>(() =>
            provider.Create(FakeDeviceWorker.NonlinearType, new Dictionary<string, string> { ["Scale"] = "wide" }));

        Assert.Contains("Scale", ex.Message);
        Assert.Contains("wide",  ex.Message);
    }

    [Fact]
    public void AnUnknownTypeName_IsRefusedWithTheListOfRealOnes()
    {
        var (provider, _) = NewProvider();

        var ex = Assert.Throws<ExternalDeviceException>(() =>
            provider.Create("not_a_type", new Dictionary<string, string>()));

        Assert.Contains("not_a_type", ex.Message);
        Assert.Contains(FakeDeviceWorker.NonlinearType, ex.Message);
    }

    [Fact]
    public void AWorkersOwnRefusal_ReachesTheUserVerbatim()
    {
        var (provider, _) = NewProvider();

        var ex = Assert.Throws<ExternalDeviceException>(() =>
            provider.Create(FakeDeviceWorker.LinearOnlyType, new Dictionary<string, string>()));

        Assert.Contains("nonlinear analyze entry point", ex.Message);
        Assert.Contains("fake device worker", ex.Message);      // and says which worker said it
    }

    [Fact]
    public void DelayPairs_AreSurfacedForTheAnalysisThatWillNeedThem()
    {
        using var instance = NewInstance(out _);

        var pair = Assert.Single(((DeviceWorkerInstance)instance).DelayPairs);

        Assert.Equal(5, pair.FromNode);
        Assert.Equal(4, pair.ToNode);
        Assert.Equal(7.15e-12, pair.Tau, 15);
    }

    // ── measured node roles ───────────────────────────────────────────────────

    [Fact]
    public void ANodeThatIsNotAFreeUnknown_ReportsWhatItFollows()
    {
        using var instance = NewInstance(out _);

        var slaved = Assert.Single(instance.Descriptor.SlavedNodes);

        Assert.Equal((5, 4), slaved);
    }

    [Fact]
    public void AThermalPin_IsClassifiedFromMeasurement_NotDeclaration()
    {
        using var instance = NewInstance(out _);

        var nodes = instance.Descriptor.Nodes;

        Assert.Equal(NodeQuantityKind.Thermal,    nodes.Single(n => n.Index == 3).QuantityKind);
        Assert.Equal(NodeQuantityKind.Electrical, nodes.Single(n => n.Index == 1).QuantityKind);
    }

    [Fact]
    public void AWorkerThatCannotProbe_StillYieldsAUsableInstance()
    {
        // Probing refines a descriptor; it does not gate one. A worker too old to probe must still
        // give a working instance, falling back on the shape the type declared.
        var worker = new FakeDeviceWorker { RefuseProbe = true };
        var provider = new DeviceWorkerProvider("test-worker", worker);

        using var instance = provider.Create(FakeDeviceWorker.NonlinearType, new Dictionary<string, string>());

        Assert.Equal(FakeDeviceWorker.Nodes, instance.Descriptor.NodeCount);
        Assert.Equal((5, 4), Assert.Single(instance.Descriptor.SlavedNodes));   // still known from describe
        Assert.Equal(6.0, instance.Evaluate(Ramp(FakeDeviceWorker.Nodes)).Conductance[5, 5], 12);
    }

    // ── evaluation ────────────────────────────────────────────────────────────

    [Fact]
    public void CurrentAndCharge_AreReadFromTheirOwnBlocks()
    {
        using var instance = NewInstance(out _);
        double[] v = Ramp(FakeDeviceWorker.Nodes);

        var r = instance.Evaluate(v);

        // I[k] = (k+1)*v[k], plus 3*v[1] at node 0; Q[k] = v[k]/2. Reading one block as the other
        // would still produce plausible numbers, which is exactly why both are checked.
        Assert.Equal(1 * v[0] + 3 * v[1], r.Current[0], 12);
        Assert.Equal(2 * v[1],            r.Current[1], 12);
        Assert.Equal(6 * v[5],            r.Current[5], 12);
        Assert.Equal(0.5 * v[3],          r.Charge[3],  12);
    }

    [Fact]
    public void TheJacobianIsNotTransposed()
    {
        // The model's one asymmetric term: node 0's current depends on node 1's voltage, and not
        // the other way about. A transposed decode passes every symmetric check and fails this one.
        using var instance = NewInstance(out _);

        var r = instance.Evaluate(Ramp(FakeDeviceWorker.Nodes));

        Assert.Equal(3.0, r.Conductance[0, 1], 12);
        Assert.Equal(0.0, r.Conductance[1, 0], 12);
    }

    [Fact]
    public void ConductanceAndCapacitance_AreNotEachOther()
    {
        using var instance = NewInstance(out _);

        var r = instance.Evaluate(Ramp(FakeDeviceWorker.Nodes));

        Assert.Equal(4.0, r.Conductance[3, 3], 12);
        Assert.Equal(0.5, r.Capacitance[3, 3], 12);
    }

    [Fact]
    public void CurrentSignIsPassedThroughUnflipped()
    {
        // The worker already reports current positive INTO the device, which is circuitRF's own
        // convention. A defensive second flip would invert every operating point and still converge.
        using var instance = NewInstance(out _);

        var r = instance.Evaluate([1.0, 0.0, 0.0, 0.0, 0.0, 0.0]);

        Assert.Equal(+1.0, r.Current[0], 12);
    }

    [Fact]
    public void AWholeBatch_IsEvaluatedInOneRoundTrip()
    {
        using var instance = NewInstance(out var worker);
        int before = worker.Commands.Count(c => c == "eval");

        var points = Enumerable.Range(0, 64)
            .Select(k => (IReadOnlyList<double>)Ramp(FakeDeviceWorker.Nodes, k))
            .ToArray();

        var results = instance.EvaluateBatch(points);

        Assert.Equal(64, results.Count);
        Assert.Equal(1, worker.Commands.Count(c => c == "eval") - before);
    }

    [Fact]
    public void EveryPointInABatch_KeepsItsOwnAnswer()
    {
        // An off-by-one in the per-point stride shows up as neighbouring points sharing results.
        using var instance = NewInstance(out _);
        var points = Enumerable.Range(0, 8)
            .Select(k => (IReadOnlyList<double>)Ramp(FakeDeviceWorker.Nodes, k))
            .ToArray();

        var results = instance.EvaluateBatch(points);

        for (int k = 0; k < points.Length; k++)
            Assert.Equal(2 * points[k][1], results[k].Current[1], 12);
    }

    [Fact]
    public void AnEmptyBatch_AsksTheWorkerNothing()
    {
        using var instance = NewInstance(out var worker);
        int before = worker.Commands.Count;

        Assert.Empty(instance.EvaluateBatch([]));
        Assert.Equal(before, worker.Commands.Count);
    }

    [Fact]
    public void AVoltageVectorOfTheWrongLength_IsRefusedWithBothCounts()
    {
        using var instance = NewInstance(out _);

        var ex = Assert.Throws<ExternalDeviceException>(() =>
            instance.Evaluate([1.0, 2.0]));

        Assert.Contains("6", ex.Message);
        Assert.Contains("2", ex.Message);
    }

    [Fact]
    public void APointTheModelCouldNotEvaluate_IsRaisedRatherThanReturnedAsNonsense()
    {
        // Returning it would put a non-finite value into the matrix and surface much later as an
        // unexplained failure to converge.
        using var instance = NewInstance(out var worker);
        worker.FailPoints.Add(3);

        var points = Enumerable.Range(0, 8)
            .Select(k => (IReadOnlyList<double>)Ramp(FakeDeviceWorker.Nodes, k))
            .ToArray();

        var ex = Assert.Throws<ExternalDeviceException>(() => instance.EvaluateBatch(points));

        Assert.Contains("point 3", ex.Message);
        Assert.Contains("finite", ex.Message);
    }

    [Fact]
    public void AFailedPoint_DoesNotClaimACauseTheWorkerNeverReported()
    {
        // A worker marks a point failed the same way whether the model refused it, crashed inside
        // it, or produced a value that was not finite — senior_worker.c sets status 0 for all three,
        // and logs "eval: SIGSEGV caught" for the middle one. Naming only the last of the three sent
        // a real diagnosis chasing the bias when the model had in fact been unable to open a data
        // file it needed.
        using var instance = NewInstance(out var worker);
        worker.FailPoints.Add(0);

        var ex = Assert.Throws<ExternalDeviceException>(
            () => instance.Evaluate(Ramp(FakeDeviceWorker.Nodes, 0)));

        Assert.Contains("refused",  ex.Message);
        Assert.Contains("crashed",  ex.Message);
        Assert.Contains("could not open", ex.Message);
    }

    [Fact]
    public void AWorkerThatAnswersADifferentShape_IsReportedAsAMismatch()
    {
        // A worker built against a different protocol revision must be caught here, not decoded
        // into a plausible-looking wrong answer.
        using var instance = NewInstance(out var worker);
        worker.LieAboutShape = true;

        var ex = Assert.Throws<ExternalDeviceException>(() =>
            instance.Evaluate(Ramp(FakeDeviceWorker.Nodes)));

        Assert.Contains("answered with", ex.Message);
    }

    // ── lifecycle ─────────────────────────────────────────────────────────────

    [Fact]
    public void DisposingAnInstance_ReleasesItInTheWorker()
    {
        var instance = NewInstance(out var worker);
        Assert.Single(worker.Instances);

        instance.Dispose();

        Assert.Empty(worker.Instances);
    }

    [Fact]
    public void DisposingTwice_AsksTheWorkerOnce()
    {
        var instance = NewInstance(out var worker);

        instance.Dispose();
        instance.Dispose();

        Assert.Equal(1, worker.Commands.Count(c => c == "destroy"));
    }

    [Fact]
    public void DisposingAnInstanceOfADeadWorker_DoesNotThrowOverTheRealFailure()
    {
        // Disposal runs on the way out of an error. A second exception there replaces the first.
        var instance = NewInstance(out var worker);
        worker.RefuseEverythingBecause = "the worker is gone";

        instance.Dispose();
    }

    [Fact]
    public void DisposingTheProvider_EndsTheWorker()
    {
        var (provider, worker) = NewProvider();

        provider.Dispose();

        Assert.True(worker.Disposed);
    }

    [Fact]
    public void UsingAnInstanceAfterDisposal_SaysSo()
    {
        var instance = NewInstance(out _);
        instance.Dispose();

        Assert.Throws<ObjectDisposedException>(() => instance.Evaluate(Ramp(FakeDeviceWorker.Nodes)));
    }
}
