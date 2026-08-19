using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// Starting a worker is announced BEFORE the process is created, once per process.
///
/// <para><b>Owner report: the first time a worker is launched there is no feedback at all.</b> It is
/// the one step in evaluating an external model that a user waits on and cannot see — the model
/// library is loaded and its device types read, and on a Mac that happens inside a virtual machine
/// which has to boot first. Until it finishes, a run proceeding normally is indistinguishable from
/// one that has hung, and the next thing printed is whatever the run says next, which never mentions
/// the worker.</para>
///
/// <para><b>Once is the whole requirement, so the placement is what matters.</b> The event is raised
/// where a process is actually created, not where a provider is asked for — the registry keeps what
/// it resolved, so every device after the first uses the worker already running and nothing further
/// is said.</para>
/// </summary>
[Collection(ExternalProviderRegistryCollection.Name)]
public sealed class DeviceWorkerStartNotificationTests : IDisposable
{
    private readonly List<DeviceWorkerStart> _seen = [];
    private readonly Action<DeviceWorkerStart> _handler;

    public DeviceWorkerStartNotificationTests()
    {
        _handler = s => _seen.Add(s);
        ProcessDeviceWorkerTransport.Starting += _handler;
    }

    public void Dispose()
    {
        ProcessDeviceWorkerTransport.Starting -= _handler;
        ExternalDeviceRegistry.Clear();
    }

    /// <summary>A path that is certainly not an executable, so the start fails immediately — this
    /// file is about what is ANNOUNCED, and announcing happens before the process exists.</summary>
    private static string NotAnExecutable =>
        Path.Combine(Path.GetTempPath(), "crf-not-a-worker-" + Guid.NewGuid().ToString("N")[..8]);

    // ── The gate ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Announced before the process is created, and therefore announced even when creating it fails.
    /// That ordering is the point: a message that waited for a successful start would arrive after
    /// the wait it exists to explain, and would be missing entirely from the case a user most needs
    /// it in.
    /// </summary>
    [Fact]
    public void TheStartIsAnnouncedBeforeTheProcessIsCreated()
    {
        string worker = NotAnExecutable;

        Assert.Throws<ExternalDeviceException>(
            () => ProcessDeviceWorkerTransport.Start(worker, ["lib.so"], forProvider: "AcmeKit"));

        var announced = Assert.Single(_seen);
        Assert.Equal("AcmeKit", announced.Provider);
        Assert.Equal(worker,    announced.Command);
    }

    /// <summary>One announcement per process, not one per subscriber call site — two starts are two
    /// events because two workers really did start.</summary>
    [Fact]
    public void EachStartedProcessIsAnnouncedExactlyOnce()
    {
        foreach (string kit in new[] { "KitA", "KitB" })
            Assert.Throws<ExternalDeviceException>(
                () => ProcessDeviceWorkerTransport.Start(NotAnExecutable, forProvider: kit));

        Assert.Equal(["KitA", "KitB"], _seen.Select(s => s.Provider));
    }

    /// <summary>
    /// A caller with no provider name still announces. The message can be written either way; it just
    /// says less. Leaving the event unraised would make the no-name case — a compiled model the user
    /// placed themselves — the one with no feedback at all.
    /// </summary>
    [Fact]
    public void ACallerWithNoProviderNameStillAnnounces()
    {
        Assert.Throws<ExternalDeviceException>(() => ProcessDeviceWorkerTransport.Start(NotAnExecutable));

        Assert.Equal("", Assert.Single(_seen).Provider);
    }

    /// <summary>
    /// A subscriber that throws is ignored. A host's own reporting must never be the reason a worker
    /// fails to start — the failure would be attributed to the kit, and the kit would be fine.
    /// </summary>
    [Fact]
    public void ASubscriberThatThrowsDoesNotStopTheWorker()
    {
        void Explode(DeviceWorkerStart _) => throw new InvalidOperationException("reporting is broken");
        ProcessDeviceWorkerTransport.Starting += Explode;
        try
        {
            // Still the ordinary "could not be started" failure, not the subscriber's exception.
            var ex = Assert.Throws<ExternalDeviceException>(
                () => ProcessDeviceWorkerTransport.Start(NotAnExecutable, forProvider: "AcmeKit"));
            Assert.DoesNotContain("reporting is broken", ex.Message, StringComparison.Ordinal);

            // …and the well-behaved subscriber beside it still heard about it.
            Assert.Single(_seen);
        }
        finally { ProcessDeviceWorkerTransport.Starting -= Explode; }
    }

    /// <summary>
    /// The registry keeps a provider it resolved, so a second lookup of the same name starts nothing
    /// and says nothing. This is what makes "once per worker" true for a design placing many devices
    /// from one kit — the property the owner actually asked for.
    /// </summary>
    [Fact]
    public void ASecondLookupOfTheSameProviderStartsNothingAndSaysNothing()
    {
        int launches = 0;
        var resolver = new StubResolver(name =>
        {
            launches++;
            // Goes through the REAL start, so the announcement comes from the code under test rather
            // than from this fixture. Whether the process then exists is beside the point here.
            try { ProcessDeviceWorkerTransport.Start(NotAnExecutable, forProvider: name); }
            catch (ExternalDeviceException) { /* expected: there is no such executable */ }
            return new DeviceWorkerProvider(name, new FakeDeviceWorker());
        });

        ExternalDeviceRegistry.AddResolver(resolver);

        Assert.NotNull(ExternalDeviceRegistry.Find("AcmeKit"));
        Assert.NotNull(ExternalDeviceRegistry.Find("AcmeKit"));
        Assert.NotNull(ExternalDeviceRegistry.Find("AcmeKit"));

        Assert.Equal(1, launches);
        Assert.Single(_seen);
    }

    private sealed class StubResolver(Func<string, IExternalDeviceProvider> make) : IExternalProviderResolver
    {
        public string Describe => "a stub";
        public IExternalDeviceProvider? Resolve(string name) => make(name);
    }
}
