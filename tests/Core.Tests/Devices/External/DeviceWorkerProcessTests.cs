using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using CircuitRF.Core.Devices.External;
using System.Threading;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// The provider driven against a worker running as a real child process.
///
/// <para>Every other test here speaks the protocol over in-memory streams, which cannot exercise the
/// part that actually runs in production: pipes. A pipe returns short reads, buffers writes until
/// something flushes, deadlocks if nobody drains the error stream, and ends abruptly when the child
/// exits. None of that is reachable with a <c>MemoryStream</c>, and all of it is where process
/// plumbing goes wrong.</para>
///
/// <para>The worker is the reference implementation in <c>tools/DeviceWorkerExample</c>, which
/// shares no code with circuitRF — so agreement here is two independent implementations of the wire
/// format agreeing, not one implementation agreeing with itself.</para>
/// </summary>
public sealed class DeviceWorkerProcessTests
{
    private const string TypeId = "example_fet_v1";

    /// <summary>
    /// A worker's own log reaches the host as it arrives, when the host asks for it.
    ///
    /// <para><b>The gap this closes.</b> A worker MEASURES how a model's nodes behave — which are
    /// free unknowns, which carry a temperature — and those measurements decide how the device is
    /// stamped. A measurement that comes out differently on two machines throws on neither: the
    /// device stamps cleanly, every number stays finite, and the only symptom is that one of them
    /// does not converge. <c>RecentErrorOutput</c> holds the same lines but is read only where
    /// something threw, so the one account of what happened was unreachable in exactly that case.</para>
    /// </summary>
    [Fact]
    public void AWorkersOwnLog_ReachesTheHost_WhenItIsAskedFor()
    {
        var seen = new List<string>();
        void Collect(DeviceWorkerLogLine l) { lock (seen) seen.Add(l.Line); }

        bool was = ProcessDeviceWorkerTransport.MirrorErrorOutput;
        ProcessDeviceWorkerTransport.MirrorErrorOutput = true;
        ProcessDeviceWorkerTransport.Logged += Collect;
        try
        {
            // The reference worker writes to its error stream when it is told to fail, which is the
            // one thing every worker's stream is guaranteed to carry.
            using var transport = ProcessDeviceWorkerTransport.Start(
                WorkerPath, ["--fail-with", "measured node 6 as undriven"], forProvider: "AcmeKit");

            for (int i = 0; i < 100; i++)
            {
                lock (seen) if (seen.Count > 0) break;
                Thread.Sleep(20);
            }

            lock (seen) Assert.Contains("measured node 6 as undriven", seen);
        }
        finally
        {
            ProcessDeviceWorkerTransport.Logged -= Collect;
            ProcessDeviceWorkerTransport.MirrorErrorOutput = was;
        }
    }

    /// <summary>
    /// A worker that is simply not on this machine says whose program it is and where it was looked
    /// for, rather than passing on the operating system's own account.
    ///
    /// <para>That account names the file and the WORKING DIRECTORY and stops. The working directory
    /// is a red herring — a bare name was never going to be looked for there — and what is left
    /// reads as a kit that failed to ship something, when the missing program is circuitRF's own
    /// optional component: it is built beside the application, and a build made where no C compiler
    /// was present skips it, warns, and succeeds.</para>
    /// </summary>
    [Fact]
    public void AWorkerThatIsNotOnThisMachine_SaysWhoseProgramItIsAndWhereItWasLookedFor()
    {
        var ex = Assert.Throws<ExternalDeviceException>(
            () => ProcessDeviceWorkerTransport.Start("crf-no-such-worker-9d2f1a"));

        Assert.Contains("tools folder", ex.Message, StringComparison.Ordinal);
        Assert.Contains("built alongside the application", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// ...and a failure that got FURTHER than "no such file" is left alone. A program that is there
    /// but refused says nothing about where circuitRF looks, and advice to go and build one would
    /// send the reader off to fix the wrong thing.
    /// </summary>
    [Fact]
    public void AWorkerThatExistsButCannotRun_IsNotBlamedOnAMissingBuild()
    {
        string notAProgram = Path.Combine(Path.GetTempPath(), "crf-not-a-program-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(notAProgram, "this is not an executable");

        try
        {
            var ex = Assert.Throws<ExternalDeviceException>(
                () => ProcessDeviceWorkerTransport.Start(notAProgram));

            Assert.DoesNotContain("built alongside the application", ex.Message, StringComparison.Ordinal);
        }
        finally { try { File.Delete(notAProgram); } catch { /* best effort */ } }
    }

    /// <summary>
    /// Path to the reference worker, recorded by the build. Missing means it was not built, which
    /// is a broken test setup rather than a skip — silently passing would hide the loss of the only
    /// coverage of the process transport.
    /// </summary>
    private static string WorkerDirectory
    {
        get
        {
            string? dir = typeof(DeviceWorkerProcessTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "DeviceWorkerExampleDir")?.Value;

            Assert.False(string.IsNullOrWhiteSpace(dir), "the build did not record the worker's location");
            return Path.GetFullPath(dir!);
        }
    }

    private static string WorkerPath
    {
        get
        {
            string exe = Path.Combine(WorkerDirectory,
                "DeviceWorkerExample" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : ""));

            Assert.True(File.Exists(exe), $"the reference worker was not built at '{exe}'");
            return exe;
        }
    }

    private static DeviceWorkerProvider Launch() => DeviceWorkerProvider.Launch("example", WorkerPath);

    private static IExternalDeviceInstance Create(
        DeviceWorkerProvider provider, params (string Name, string Value)[] parameters)
        => provider.Create(TypeId, parameters.ToDictionary(p => p.Name, p => p.Value));

    // ── the device, in closed form ────────────────────────────────────────────
    //
    //  Duplicated here on purpose. Asking the worker what it thinks and comparing that to itself
    //  proves nothing; these are the equations the worker is SUPPOSED to implement, written
    //  independently, so a disagreement means one of the two is wrong.

    private const double Beta = 0.02, Vth = 0.7, Lambda = 0.01, Ggs = 1e-9;

    private static double DrainCurrent(double vg, double vd, double vs)
    {
        double over = vg - vs - Vth, vds = vd - vs;
        return over > 0 && vds > 0 ? Beta * over * over * (1 + Lambda * vds) : 0.0;
    }

    // ── it starts, and it answers ─────────────────────────────────────────────

    [Fact]
    public void AWorkerStartedAsAProcess_DescribesWhatItServes()
    {
        using var provider = Launch();

        var type = Assert.Single(provider.Describe());

        Assert.Equal(TypeId, type.TypeId);
        Assert.Equal(3, type.ExternalPinCount);
        Assert.Equal(0, type.InternalNodeCount);
        Assert.True(type.SupportsNonlinear);
    }

    [Fact]
    public void ParametersDeclaredByTheWorker_ArriveWithTheirKinds()
    {
        using var provider = Launch();

        var names = provider.Describe().Single().Parameters.Select(p => p.Name).ToArray();

        Assert.Equal(["Beta", "Vth", "Lambda", "Cgs", "Ggs"], names);
    }

    [Fact]
    public void TheOperatingPointComesBackAcrossThePipe_MatchingTheDeviceItModels()
    {
        using var provider = Launch();
        using var device   = Create(provider);

        var r = device.Evaluate([2.0, 5.0, 0.0]);   // gate 2 V, drain 5 V, source at 0

        Assert.Equal(DrainCurrent(2.0, 5.0, 0.0), r.Current[1], 12);
        Assert.Equal(Ggs * 2.0,                   r.Current[0], 15);
    }

    [Fact]
    public void TheThreeCurrentsSumToZero_SoWhatCrossedThePipeIsStillPhysical()
    {
        // The strongest single check available without duplicating the whole model: a decode that
        // lost, reordered or rescaled any entry breaks Kirchhoff's law.
        using var provider = Launch();
        using var device   = Create(provider);

        foreach (double vg in new[] { 0.0, 1.0, 2.5, 4.0 })
        {
            var r = device.Evaluate([vg, 5.0, 0.0]);
            Assert.Equal(0.0, r.Current.Sum(), 12);
        }
    }

    [Fact]
    public void TheJacobianArrivesInTheRightOrientation()
    {
        // ∂I[drain]/∂V[gate] is large and ∂I[gate]/∂V[drain] is zero, so a transposed decode is
        // unmistakable — and would otherwise be a silently wrong Newton step, not a failure.
        using var provider = Launch();
        using var device   = Create(provider);

        var r = device.Evaluate([2.0, 5.0, 0.0]);

        double expected = 2 * Beta * (2.0 - Vth) * (1 + Lambda * 5.0);
        Assert.Equal(expected, r.Conductance[1, 0], 12);
        Assert.Equal(0.0,      r.Conductance[0, 1], 15);
    }

    [Fact]
    public void TheJacobianAgreesWithTheCurrentsItIsTheDerivativeOf()
    {
        // Finite-differencing across the pipe: the two are computed by different code paths in the
        // worker, so this catches a model whose derivative does not match its own current.
        using var provider = Launch();
        using var device   = Create(provider);

        const double h = 1e-6;
        var at    = device.Evaluate([2.0, 5.0, 0.0]);
        var moved = device.Evaluate([2.0 + h, 5.0, 0.0]);

        double numeric = (moved.Current[1] - at.Current[1]) / h;

        Assert.Equal(numeric, at.Conductance[1, 0], 5);
    }

    [Fact]
    public void ParametersSetAtCreation_ChangeWhatTheWorkerReturns()
    {
        using var provider = Launch();
        using var stock    = Create(provider);
        using var stronger = Create(provider, ("Beta", "0.08"));

        double a = stock.Evaluate([2.0, 5.0, 0.0]).Current[1];
        double b = stronger.Evaluate([2.0, 5.0, 0.0]).Current[1];

        Assert.Equal(4 * a, b, 12);
    }

    [Fact]
    public void TwoInstancesOfOneWorker_KeepTheirOwnParameters()
    {
        // One worker serves many devices in a design. Instances sharing state would show up as a
        // circuit whose transistors mysteriously track each other.
        using var provider = Launch();
        using var low      = Create(provider, ("Vth", "0.7"));
        using var high     = Create(provider, ("Vth", "3.0"));

        Assert.True(low.Evaluate([2.0, 5.0, 0.0]).Current[1] > 0);
        Assert.Equal(0.0, high.Evaluate([2.0, 5.0, 0.0]).Current[1], 15);
    }

    // ── the pipe under load ───────────────────────────────────────────────────

    [Fact]
    public void ALargeBatchSurvivesThePipeIntact()
    {
        // ~72k doubles in one reply, far past any single pipe buffer, so this only passes if partial
        // reads are looped on both sides. It is the test that would fail if either end took a short
        // read for the end of the stream.
        using var provider = Launch();
        using var device   = Create(provider);

        var points = Enumerable.Range(0, 2000)
            .Select(k => (IReadOnlyList<double>)new[] { 0.9 + k * 0.001, 5.0, 0.0 })
            .ToArray();

        var results = device.EvaluateBatch(points);

        Assert.Equal(points.Length, results.Count);

        for (int k = 0; k < points.Length; k += 137)
            Assert.Equal(DrainCurrent(points[k][0], 5.0, 0.0), results[k].Current[1], 12);
    }

    [Fact]
    public void ManyRoundTripsInSequence_StayInStep()
    {
        // A framing error that leaves one stray byte behind is invisible on the first call and
        // corrupts every call after it.
        using var provider = Launch();
        using var device   = Create(provider);

        for (int k = 0; k < 200; k++)
        {
            double vg = 1.0 + (k % 30) * 0.1;
            Assert.Equal(DrainCurrent(vg, 5.0, 0.0), device.Evaluate([vg, 5.0, 0.0]).Current[1], 12);
        }
    }

    // ── measured structure ────────────────────────────────────────────────────

    [Fact]
    public void NodeRolesComeBackFromTheWorkersOwnMeasurement()
    {
        using var provider = Launch();
        using var device   = Create(provider);

        var nodes = device.Descriptor.Nodes;

        Assert.Equal(3, nodes.Count);
        Assert.All(nodes, n => Assert.True(n.External));
        Assert.Empty(device.Descriptor.SlavedNodes);
    }

    // ── failures ──────────────────────────────────────────────────────────────

    [Fact]
    public void AWorkerThatRefusesSomething_SaysWhyAndKeepsServing()
    {
        // A rejected parameter must not cost the worker: the next device still has to work.
        using var provider = Launch();

        var ex = Assert.Throws<ExternalDeviceException>(() => Create(provider, ("NotAParameter", "1")));
        Assert.Contains("NotAParameter", ex.Message);

        using var device = Create(provider);
        Assert.True(device.Evaluate([2.0, 5.0, 0.0]).Current[1] > 0);
    }

    [Fact]
    public void AProgramThatDoesNotExist_IsReportedAsFailingToStart()
    {
        var ex = Assert.Throws<ExternalDeviceException>(
            () => DeviceWorkerProvider.Launch("missing", Path.Combine(Path.GetTempPath(), "no-such-worker-xyz")));

        Assert.Contains("no-such-worker-xyz", ex.Message);
    }

    [Fact]
    public void AWorkerThatStoppedAnswering_IsReportedRatherThanWaitedOnForever()
    {
        // The failure a user actually hits when a model dies mid-solve: the process is gone but
        // nothing told the host. The read must end as a named error, not as a wait with no end.
        // Driven through a real process that has genuinely exited, since an EOF on a live pipe is
        // the thing being tested and no in-memory stream produces one.
        using var transport = ProcessDeviceWorkerTransport.Start(WorkerPath);
        using var channel   = new DeviceWorkerChannel(transport);

        channel.Send(w => w.WriteString("cmd", "shutdown")).Dispose();

        // Wait for the process to have actually gone before asking again. Without this the test is a
        // race against the OS reaping it: the pipe may still be open, in which case the write lands
        // and the failure arrives by a different route with a different message. Waiting makes the
        // condition under test — a read that ends as a named error rather than a wait with no end —
        // the only thing being measured, under load as well as idle.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (transport.IsAlive && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.False(transport.IsAlive, "the worker did not exit after being told to shut down");

        var ex = Assert.Throws<ExternalDeviceException>(
            () => channel.Send(w => w.WriteString("cmd", "describe")));

        // Whichever way it surfaces — the write hitting a broken pipe or the read hitting EOF — it
        // must be a named failure that says the worker is gone and which worker it was.
        Assert.Contains("exited", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(WorkerPath, ex.Message);      // and says which worker
        Assert.DoesNotContain(") the ", ex.Message);  // reads as a sentence, not two glued together
    }

    [Fact]
    public void AWorkerThatDiesImmediately_StillReportsWhatItSaidOnTheWayOut()
    {
        // A worker that cannot start says why on its error stream and exits at once, and that
        // message is the ONLY description the user gets. This pins that the report carries it with
        // no grace period at all — a request sent the instant the provider is resolved.
        //
        // ONLY REPRODUCES UNDER LOAD, which is the whole reason it is written this way. Error lines
        // arrive on a background reader, and it loses the race only when the machine is busy enough
        // to delay it: 5 failures in 12 full-solution runs without the fix, and 0 in 40 runs of this
        // test on its own. Twelve isolated passes were once taken as proof the race did not exist —
        // it was the wrong experiment, and the conclusion was wrong with it.
        //
        // So: if this ever fails, it is not flaky. Do not add a wait for the process to be reaped
        // either — polling for that IS the grace period, and it hides exactly what is being tested.
        const string reason = "cannot open model data file: no such file or directory";

        using var transport = ProcessDeviceWorkerTransport.Start(WorkerPath, ["--fail-with", reason]);
        using var channel   = new DeviceWorkerChannel(transport);

        // Deliberately NO wait for the process to be reaped. Polling for that is what gives the
        // background reader time to catch up, and it is the absence of that grace — a request sent
        // the instant the provider is resolved — that produced the empty report in the field.
        var ex = Assert.Throws<ExternalDeviceException>(
            () => channel.Send(w => w.WriteString("cmd", "describe")));

        Assert.Contains(reason, ex.Message);
        Assert.Contains("Worker output:", ex.Message);
    }

    [Fact]
    public void UsingADeviceAfterItsProviderHasEnded_SaysThatPlainly()
    {
        // Distinct from the worker dying on its own: here the application ended it, so this is a
        // mistake in the calling code and is reported as one rather than as a transport failure.
        var provider = Launch();
        var device   = Create(provider);

        provider.Dispose();

        Assert.Throws<ObjectDisposedException>(() => device.Evaluate([2.0, 5.0, 0.0]));
    }

    // ── the shipped example manifest ──────────────────────────────────────────

    [Fact]
    public void TheExampleManifestIsValid_AndCoversThisMachine()
    {
        // It is the template a kit author copies. A broken example is worse than none, and nothing
        // else would catch it — no product code reads this file.
        string path = Path.Combine(
            Path.GetFullPath(Path.Combine(WorkerDirectory, "..", "..", "..")),
            DeviceWorkerManifest.FileName);

        Assert.True(File.Exists(path), $"the example manifest is missing from '{path}'");

        var manifest = DeviceWorkerManifest.TryRead(path, out string? problem);

        Assert.Null(problem);
        Assert.Equal("ExampleKit", manifest!.ProviderName);
        Assert.NotNull(manifest.LaunchForThisMachine());
        Assert.Contains(manifest.Launches, l => l.Platform == "win");
    }

    [Fact]
    public void EndingTheProviderStopsTheProcess()
    {
        var provider = Launch();
        using (var device = Create(provider))
            Assert.True(device.Evaluate([2.0, 5.0, 0.0]).Current[1] > 0);

        provider.Dispose();
        provider.Dispose();   // ending an already-ended worker is not an error
    }
}
