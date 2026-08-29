using System;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.External;

/// <summary>
/// A device an external provider supplies has to be GIVEN BACK, and a parametric sweep is where that
/// stops being a nicety.
///
/// <para><b>The failure this pins, measured.</b> A provider's instance lives in the
/// WORKER's process — memory no garbage collector here can reach, in a table that is finite. A sweep
/// re-elaborates once per point on purpose, because that is how a swept variable reaches the circuit
/// at all, so every point asks for a fresh device. A 201 × 101 DC sweep over a compiled compact model
/// therefore asked a worker holding 4,096 instances for 20,502, and the run died part-way through
/// with a message about the 4,097th. Nothing about that failure names a sweep, a leak, or a
/// lifetime.</para>
/// </summary>
// Serialises this class against every other one that mutates the process-wide static
// ExternalDeviceRegistry. Six sibling classes in this directory already carry this attribute; this
// one did not, and that is the whole of the intermittent "External device provider '...' is not
// available: no providers are registered" seen under full-suite load on 2026-08-29 — xUnit runs
// test classes in parallel, so a sibling's Clear() landed between this class's Register and its
// use. Both failing methods passed in isolation, which is the signature. There is deliberately no
// [CollectionDefinition] for this name: a bare [Collection] still groups the classes, and two
// collections never run in parallel with one another.
[Collection("ExternalDeviceRegistry")]
public sealed class ExternalDeviceLifetimeTests : IDisposable
{
    public void Dispose() => ExternalDeviceRegistry.Clear();

    private const string ProviderName = "lifetime-kit";

    /// <summary>
    /// A common-source stage whose gate bias is a swept global — the ordinary shape, and the one that
    /// forces re-elaboration per point. The fourth net is the thermal terminal, left to float.
    /// </summary>
    private const string Netlist = """
        VG = 2
        Vdc:VGS  g 0   Vdc=VG
        Vdc:VD   dd 0  Vdc=5
        R:RD     dd d  R=100
        ExtDevice:X1  g d 0 tj  Provider=lifetime-kit Type=SquareLawFet
        """;

    private static (SquareLawFetProvider Provider, TestBench Bench, Library Library) Bench()
    {
        var provider = new SquareLawFetProvider(ProviderName);
        ExternalDeviceRegistry.Register(provider);

        var (lib, tb) = new CnlReader().Read(Netlist);
        return (provider, tb, lib);
    }

    /// <summary>
    /// The unit of the rule: disposing a netlist gives back every device it made, and nothing else
    /// has to know a provider was involved.
    /// </summary>
    [Fact]
    public void DisposingANetlist_GivesBackTheDevicesItMade()
    {
        var (provider, tb, lib) = Bench();

        var netlist = new Elaborator(lib).Elaborate(tb);
        Assert.Equal(1, provider.Created);
        Assert.Equal(1, provider.Live);

        netlist.Dispose();
        Assert.Equal(0, provider.Live);

        netlist.Dispose();               // idempotent: clean-up must not depend on being called once
        Assert.Equal(0, provider.Live);
    }

    /// <summary>
    /// THE ONE THAT MATTERS. Every point of a sweep asks for its own device, and none of them may
    /// still be held when the sweep returns.
    ///
    /// <para>Asserted on what is LIVE rather than on what was created: creating one per point is
    /// correct and is not the defect — re-elaboration is how the sweep works. The defect is keeping
    /// them. So the count of creations is asserted to actually grow with the axis, which is what
    /// stops this passing for the wrong reason if re-elaboration were ever optimised away.</para>
    /// </summary>
    [Fact]
    public void ASweep_HoldsNoMoreDevicesWhenItFinishesThanWhenItStarted()
    {
        var (provider, tb, lib) = Bench();

        tb.Analyses.Add(new DcAnalysis("DC1"));
        var sweep = new ParametricSweepAnalysis(
            "sweepVG", "VG", [.. Enumerable.Range(0, 25).Select(i => 1.0 + 0.1 * i)], "DC1");
        tb.Analyses.Add(sweep);

        var data = ParametricSweepEngine.Run(sweep, lib, tb, null, null);

        Assert.NotEmpty(data.Cubes);
        Assert.True(provider.Created >= 25,
            $"the sweep must really re-elaborate per point, or this proves nothing (created {provider.Created})");
        Assert.Equal(0, provider.Live);
    }

    /// <summary>
    /// A NESTED sweep is where the count runs away fastest, because the outer axis elaborates a
    /// netlist per outer point that its own inner sweep then never uses. 5 × 5 asks for 30 devices.
    /// </summary>
    [Fact]
    public void ANestedSweep_ReleasesTheOuterPointsToo()
    {
        var (provider, tb, lib) = Bench();

        tb.GlobalVariables.Add(new Variable("VD2", "5"));

        tb.Analyses.Add(new DcAnalysis("DC1"));
        var inner = new ParametricSweepAnalysis("sweepVG", "VG", [1.0, 1.5, 2.0, 2.5, 3.0], "DC1");
        tb.Analyses.Add(inner);
        var outer = new ParametricSweepAnalysis(
            "sweepVD2", "VD2", [4.0, 4.5, 5.0, 5.5, 6.0], "sweepVG");
        tb.Analyses.Add(outer);

        ParametricSweepEngine.Run(outer, lib, tb, null, null);

        Assert.True(provider.Created >= 30,
            $"both axes must really re-elaborate (created {provider.Created})");
        Assert.Equal(0, provider.Live);
    }
}
