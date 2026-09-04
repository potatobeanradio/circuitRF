using System;
using System.Collections.Generic;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// A provider backed by a separate PROCESS stops working the moment that process exits. The registry
/// caches providers, so it has to notice.
///
/// <para><b>The report this exists for.</b> A user placed a VerilogA component, worked for ten
/// minutes, placed a second one pointing at a different model, and was told
/// <i>"The device worker … the connection failed (Pipe is broken.) The worker process has exited."</i>
/// — a plumbing failure, shown for something entirely recoverable, that stopped them simulating. A
/// dead worker is not a condition to report; it is a condition to fix, by starting another.</para>
/// </summary>
[Collection(ExternalProviderRegistryCollection.Name)]
public sealed class DeadProviderRestartTests : IDisposable
{
    private const string Name = "restartable-provider";

    public DeadProviderRestartTests() => ExternalDeviceRegistry.Clear();
    public void Dispose() => ExternalDeviceRegistry.Clear();

    /// <summary>A provider that can be killed, counts its own disposal, and is numbered so two
    /// generations of it can be told apart.</summary>
    private sealed class MortalProvider(string name, int generation) : IExternalDeviceProvider, IDisposable
    {
        public string Name { get; } = name;
        public int    Generation { get; } = generation;
        public bool   Disposed { get; private set; }
        public bool   Alive { get; set; } = true;

        public bool IsUsable => Alive && !Disposed;

        public IReadOnlyList<ExternalDeviceDescriptor> Describe() => [];

        public IExternalDeviceInstance Create(string typeId, IReadOnlyDictionary<string, string> parameters)
            => throw new NotSupportedException();

        public void Dispose() => Disposed = true;
    }

    private sealed class CountingResolver(string name) : IExternalProviderResolver
    {
        public int Calls { get; private set; }
        public readonly List<MortalProvider> Made = [];

        public string Describe => "mortal-provider stub";

        public IExternalDeviceProvider? Resolve(string requested)
        {
            if (!string.Equals(requested, name, StringComparison.OrdinalIgnoreCase)) return null;
            Calls++;
            var p = new MortalProvider(name, Calls);
            Made.Add(p);
            return p;
        }
    }

    [Fact]
    public void ALiveProviderIsReused_AndTheResolverIsNotAskedTwice()
    {
        // The behaviour that must not regress: resolving is expensive (it starts a process), so a
        // healthy provider is still handed straight back.
        var resolver = new CountingResolver(Name);
        ExternalDeviceRegistry.AddResolver(resolver);

        var first  = ExternalDeviceRegistry.Find(Name);
        var second = ExternalDeviceRegistry.Find(Name);

        Assert.Same(first, second);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public void ADeadProviderIsReplaced_NotHandedOutAgain()
    {
        var resolver = new CountingResolver(Name);
        ExternalDeviceRegistry.AddResolver(resolver);

        var first = (MortalProvider)ExternalDeviceRegistry.Find(Name)!;
        first.Alive = false;                       // the worker process exits

        var second = (MortalProvider)ExternalDeviceRegistry.Find(Name)!;

        Assert.NotSame(first, second);
        Assert.Equal(2, second.Generation);
        Assert.True(second.IsUsable);
        Assert.Equal(2, resolver.Calls);
    }

    [Fact]
    public void TheDeadProviderIsDisposed_SoNothingIsLeftHalfOpen()
    {
        var resolver = new CountingResolver(Name);
        ExternalDeviceRegistry.AddResolver(resolver);

        var first = (MortalProvider)ExternalDeviceRegistry.Find(Name)!;
        first.Alive = false;
        ExternalDeviceRegistry.Find(Name);

        Assert.True(first.Disposed);
    }

    [Fact]
    public void RequireAlsoGetsALiveProvider_RatherThanThrowingOnTheCorpse()
    {
        // Require is the path a RUN takes. Before this, a dead worker meant the whole simulation
        // failed with a broken pipe.
        var resolver = new CountingResolver(Name);
        ExternalDeviceRegistry.AddResolver(resolver);

        var first = (MortalProvider)ExternalDeviceRegistry.Find(Name)!;
        first.Alive = false;

        var got = (MortalProvider)ExternalDeviceRegistry.Require(Name);

        Assert.True(got.IsUsable);
        Assert.NotSame(first, got);
    }

    [Fact]
    public void AHostRegisteredProviderIsNeverReplaced_EvenWhenItReportsItselfUnusable()
    {
        // The host owns what it registered: the registry knows no resolver that could rebuild it, so
        // silently dropping it would turn "unusable" into "not registered at all" — a worse report,
        // and one the host cannot act on.
        var mine = new MortalProvider(Name, generation: 1);
        ExternalDeviceRegistry.Register(mine);
        mine.Alive = false;

        var got = ExternalDeviceRegistry.Find(Name);

        Assert.Same(mine, got);
        Assert.False(mine.Disposed);
    }

    [Fact]
    public void ReplacementSurvivesRepeatedDeaths_RatherThanWedgingAfterOne()
    {
        var resolver = new CountingResolver(Name);
        ExternalDeviceRegistry.AddResolver(resolver);

        for (int generation = 1; generation <= 3; generation++)
        {
            var p = (MortalProvider)ExternalDeviceRegistry.Find(Name)!;
            Assert.Equal(generation, p.Generation);
            p.Alive = false;
        }

        Assert.Equal(3, resolver.Calls);
    }
}
