using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// <c>Provider</c> and <c>Type</c> are circuitRF's own selectors on an <c>ExtDevice</c>, and a
/// compiled model may declare a parameter whose name differs from one only in CASE.
///
/// <para><b>Why that collision is worth its own file.</b> A real MOS compact model declares
/// <c>TYPE</c> for its channel polarity — <c>+1</c> for n-channel, <c>-1</c> for p-channel — and its
/// parameter card states it. Read case-blind, that card entry is taken for circuitRF's device-type
/// selector: it is dropped from what reaches the model, the model falls back to its own default, and
/// a p-channel device silently becomes an n-channel one that converges perfectly.</para>
/// </summary>
public sealed class ReservedSelectorNameTests : IDisposable
{
    public void Dispose() => ExternalDeviceRegistry.Clear();

    // ── the rule itself ───────────────────────────────────────────────────────

    [Fact]
    public void TheExactSpellingWins_WhenBothArePresent()
        => Assert.Equal("Type", ComponentModelFactory.ReservedKey(["TYPE", "Type", "Scale"], "Type"));

    /// <summary>
    /// With no exact spelling, a case-insensitive match still selects — so a hand-written netlist
    /// saying <c>type=</c> goes on working exactly as it did. Such a design has no other parameter of
    /// that name for it to be confused with, which is why the ambiguity only arises when both appear.
    /// </summary>
    [Fact]
    public void WithNoExactSpelling_ACaseInsensitiveMatchStillSelects()
        => Assert.Equal("type", ComponentModelFactory.ReservedKey(["type", "Scale"], "Type"));

    [Fact]
    public void NothingSpellingIt_IsNull()
        => Assert.Null(ComponentModelFactory.ReservedKey(["Scale", "Fingers"], "Type"));

    // ── it reaches the model ──────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, Value> Resolve(params (string Name, string Value)[] overrides)
    {
        // Elaboration builds the model as well as resolving its parameters, so the provider has to
        // be there — this is the ordinary path, not a shortcut around it.
        ExternalDeviceRegistry.Register(new DeviceWorkerProvider("AcmeKit", new FakeDeviceWorker()));

        var inst = new Instance("X1", "ExtDevice", ["a", "b", "c", "d"],
                                [.. overrides.Select(o => new ParameterAssignment(o.Name, o.Value))]);

        var tb = new TestBench("tb");
        tb.Instances.Add(inst);

        var netlist = new Elaborator(new Library("lib")).Elaborate(tb);
        return netlist.Components.Single(c => c.InstancePath == "X1").Parameters;
    }

    /// <summary>
    /// The model's own <c>TYPE</c> is EVALUATED as the number it is, while <c>Type</c> stays the
    /// verbatim selector. Treated as a selector it would be stored raw instead — which happens to
    /// survive a bare literal and quietly fails the moment a card writes an expression.
    /// </summary>
    [Fact]
    public void AModelsOwnTYPE_IsResolvedAsAValue_WhileTypeStaysTheSelector()
    {
        var resolved = Resolve(
            ("Provider", "AcmeKit"),
            ("Type",     FakeDeviceWorker.NonlinearType),
            ("TYPE",     "-1"));

        Assert.Equal(ValueKind.String, resolved["Type"].Kind);
        Assert.Equal(FakeDeviceWorker.NonlinearType, resolved["Type"].AsString());

        Assert.Equal(ValueKind.Real, resolved["TYPE"].Kind);
        Assert.Equal(-1.0, resolved["TYPE"].AsReal());
    }

    /// <summary>
    /// The gate that matters: what the PROVIDER was actually handed. Everything above can be right
    /// and the value still be dropped one layer later, where the forwarding decides what is a
    /// selector — and being dropped is invisible, because the model simply uses its own default.
    /// </summary>
    [Fact]
    public void AModelsOwnTYPE_IsForwardedToTheProvider_NotEatenAsTheSelector()
    {
        var worker   = new FakeDeviceWorker();
        var provider = new DeviceWorkerProvider("AcmeKit", worker);
        ExternalDeviceRegistry.Register(provider);

        var model = ComponentModelFactory.TryCreate("ExtDevice", new Dictionary<string, Value>(StringComparer.Ordinal)
        {
            ["Provider"] = new Value("AcmeKit"),
            ["Type"]     = new Value(FakeDeviceWorker.NonlinearType),
            ["TYPE"]     = new Value(-1.0),
            ["Scale"]    = new Value(2.0),
        });

        Assert.NotNull(model);

        var given = Assert.Single(worker.Instances).Value;
        Assert.Equal("-1", given["TYPE"]);
        Assert.Equal("2",  given["Scale"]);

        // …and the selector itself is still not offered to the model, which never declared it.
        Assert.DoesNotContain("Type", given.Keys);
    }
}
