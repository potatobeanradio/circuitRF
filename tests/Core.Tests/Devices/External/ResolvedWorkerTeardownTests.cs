using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// Ending the workers the registry started itself.
///
/// <para><b>Why this is worth pinning.</b> A worker is a child process, and a child does not die with
/// its parent. Nothing was ending them when circuitRF exited — <c>ResetResolved</c> was wired only to
/// a workspace switch — so quitting left one running per kit the design had used.</para>
///
/// <para>On macOS that is not the stray-process nuisance it sounds like: a kit's worker runs inside a
/// VM, macOS allows only a few at once, and a leaked one holds its slot indefinitely — it waits for a
/// request that can no longer arrive, because closing the pipe tells the guest nothing (a virtio
/// console has no end-of-stream to deliver). The next run then cannot start its VM and is killed by
/// the system before it can explain itself.</para>
///
/// <para>These run a REAL worker process, because the thing being checked is that teardown reaches
/// it at all. That the process itself then really dies is <c>DeviceWorkerProcessTests</c>' job — it
/// watches a live transport's own liveness, which is the only place that can be observed without the
/// disposal under test having already released the handle.</para>
/// </summary>
[Collection(ExternalProviderRegistryCollection.Name)]
public sealed class ResolvedWorkerTeardownTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-kit-" + Guid.NewGuid().ToString("N")[..8]);

    public ResolvedWorkerTeardownTests()
    {
        ExternalDeviceRegistry.Clear();

        string kit = Path.Combine(_root, "SampleKit");
        Directory.CreateDirectory(kit);
        File.WriteAllText(Path.Combine(kit, DeviceWorkerManifest.FileName),
            $$"""
              { "workers": [ { "platform": "any", "command": "{{WorkerPath.Replace("\\", "\\\\")}}" } ] }
              """);
    }

    public void Dispose()
    {
        ExternalDeviceRegistry.Clear();
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>The reference worker, located the same way the process tests locate it.</summary>
    private static string WorkerPath
    {
        get
        {
            string? dir = typeof(ResolvedWorkerTeardownTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "DeviceWorkerExampleDir")?.Value;

            Assert.False(string.IsNullOrWhiteSpace(dir), "the build did not record the worker's location");

            string exe = Path.Combine(Path.GetFullPath(dir!),
                "DeviceWorkerExample" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : ""));

            Assert.True(File.Exists(exe), $"the reference worker was not built at '{exe}'");
            return exe;
        }
    }

    /// <summary>
    /// Resolves the kit the way a design does, through the registry — and keeps hold of the
    /// transport the resolver built, so teardown can be observed on the very worker that was
    /// started.
    ///
    /// <para>Identifying it by watching the process table instead was tried and is wrong: other test
    /// classes start the same reference worker concurrently, so the new-process diff is whoever
    /// happened to launch at that moment. It flaked in a full-solution run and passed alone.</para>
    /// </summary>
    private Spy ResolveAndKeepTransport()
    {
        Spy? spy = null;

        ExternalDeviceRegistry.AddResolver(new DeviceWorkerProviderResolver([_root],
            (name, command, arguments) =>
            {
                spy = new Spy(ProcessDeviceWorkerTransport.Start(command, arguments));
                return new DeviceWorkerProvider(name, spy);
            }));

        var provider = ExternalDeviceRegistry.Require("SampleKit");
        Assert.NotEmpty(provider.Describe());       // it really started, and really answered

        Assert.NotNull(spy);
        Assert.True(spy!.IsAlive, "the worker was not running, so teardown would prove nothing");
        return spy;
    }

    [Fact]
    public void ResetResolved_TearsDownTheWorkerItStarted()
    {
        var worker = ResolveAndKeepTransport();

        ExternalDeviceRegistry.ResetResolved();

        Assert.True(worker.Disposed, "the worker was left running — it would outlive circuitRF");
    }

    [Fact]
    public void Clear_TearsItDownToo()
    {
        // Two entry points, one guarantee. A host tearing down through either must not be the one
        // that leaks.
        var worker = ResolveAndKeepTransport();

        ExternalDeviceRegistry.Clear();

        Assert.True(worker.Disposed, "the worker was left running");
    }

    [Fact]
    public void AProviderTheHostRegisteredItself_IsNotEndedByTheRegistry()
    {
        // The host owns what it created. Ending it here would dispose something the application is
        // still using — the same bug in the opposite direction.
        var host = new CountingProvider();
        ExternalDeviceRegistry.Register(host);

        ExternalDeviceRegistry.ResetResolved();

        Assert.Equal(0, host.Disposals);
    }

    /// <summary>A real transport, watched. Everything is forwarded; only the disposal is recorded.</summary>
    private sealed class Spy(IDeviceWorkerTransport inner) : IDeviceWorkerTransport
    {
        public bool Disposed { get; private set; }

        public Stream Requests           => inner.Requests;
        public Stream Replies            => inner.Replies;
        public string Origin             => inner.Origin;
        public bool   IsAlive            => inner.IsAlive;
        public string RecentErrorOutput  => inner.RecentErrorOutput;

        public void Dispose() { Disposed = true; inner.Dispose(); }
    }

    private sealed class CountingProvider : IExternalDeviceProvider, IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;

        public string Name => "HostOwned";

        public IReadOnlyList<ExternalDeviceDescriptor> Describe() => [];
        public IExternalDeviceInstance Create(string typeId, IReadOnlyDictionary<string, string> parameters)
            => throw new NotSupportedException();
    }
}
