using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// An imported kit's parts live in memory; the workspace records only a reference to the kit
/// (docs/design/pdk-import.md). These cover the reference form and the registry that holds them.
///
/// <para>Fixtures name no vendor and no part (R-pdk-1) — a kit name and a part id are strings that
/// arrived at run time, so a synthetic one exercises the same code a kit does.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class PdkKitRegistryTests : IDisposable
{
    public PdkKitRegistryTests() => PdkKitRegistry.Clear();
    public void Dispose()        => PdkKitRegistry.Clear();

    private static PdkKitPart Part(string id, int pins = 2)
    {
        var sym = new Symbol(
            primitives: [],
            pins:       [.. Enumerable.Range(0, pins).Select(i => new SymbolPin(0, i * 100, i, $"p{i}"))],
            portCount:  pins);
        return new PdkKitPart(id, sym, new CcellFile { NumPorts = pins }, IconPath: null);
    }

    // ── The reference form (R-pdk-5) ──────────────────────────────────────────

    [Fact]
    public void AKitReference_RoundTripsThroughItsOwnParser()
    {
        string r = PdkKitRegistry.RefFor("SampleKit", "PART_A");

        Assert.True(PdkKitRegistry.IsKitRef(r));
        Assert.True(PdkKitRegistry.TryParse(r, out string kit, out string part));
        Assert.Equal("SampleKit", kit);
        Assert.Equal("PART_A",    part);
    }

    /// <summary>
    /// The whole reason the reference is virtual rather than a path: a missing kit and a mistyped
    /// path have to be distinguishable, so an ordinary relative path must never read as a kit ref.
    /// </summary>
    [Theory]
    [InlineData("../../pdk/SampleKit/PART_A")]
    [InlineData("SomeCell")]
    [InlineData("")]
    [InlineData(null)]
    public void AnOrdinaryCellReference_IsNotAKitReference(string? cellRef)
        => Assert.False(PdkKitRegistry.IsKitRef(cellRef));

    /// <summary>A kit names its own parts; circuitRF does not get to constrain them.</summary>
    [Fact]
    public void APartIdContainingASeparator_SurvivesTheRoundTrip()
    {
        string r = PdkKitRegistry.RefFor("SampleKit", "GROUP/PART_A");

        Assert.True(PdkKitRegistry.TryParse(r, out string kit, out string part));
        Assert.Equal("SampleKit",     kit);
        Assert.Equal("GROUP/PART_A",  part);
    }

    [Theory]
    [InlineData("pdk://")]
    [InlineData("pdk://SampleKit")]
    [InlineData("pdk:///PART_A")]
    [InlineData("pdk://SampleKit/")]
    public void AMalformedKitReference_IsRefusedRatherThanHalfParsed(string bad)
        => Assert.False(PdkKitRegistry.TryParse(bad, out _, out _));

    // ── Contents ──────────────────────────────────────────────────────────────

    [Fact]
    public void APartIsFoundByItsReference_AndNotByAnother()
    {
        PdkKitRegistry.SetKit("SampleKit", [Part("PART_A"), Part("PART_B")]);

        Assert.NotNull(PdkKitRegistry.Find(PdkKitRegistry.RefFor("SampleKit", "PART_A")));
        Assert.NotNull(PdkKitRegistry.Find(PdkKitRegistry.RefFor("SampleKit", "PART_B")));
        Assert.Null(PdkKitRegistry.Find(PdkKitRegistry.RefFor("SampleKit", "PART_C")));
        Assert.Null(PdkKitRegistry.Find(PdkKitRegistry.RefFor("OtherKit",  "PART_A")));
    }

    /// <summary>
    /// Replacing rather than merging: a re-import or a repaired reference must produce the kit as it
    /// is NOW, not the union of every version of it seen this session.
    /// </summary>
    [Fact]
    public void ReloadingAKit_ReplacesItsParts_RatherThanMergingThem()
    {
        PdkKitRegistry.SetKit("SampleKit", [Part("PART_A"), Part("PART_B")]);
        PdkKitRegistry.SetKit("SampleKit", [Part("PART_B"), Part("PART_C")]);

        Assert.Null(PdkKitRegistry.Find(PdkKitRegistry.RefFor("SampleKit", "PART_A")));
        Assert.NotNull(PdkKitRegistry.Find(PdkKitRegistry.RefFor("SampleKit", "PART_C")));
        Assert.Single(PdkKitRegistry.LoadedKits);
        Assert.Equal(2, PdkKitRegistry.PartsOf("SampleKit").Count);
    }

    [Fact]
    public void RemovingOneKit_LeavesTheOthers()
    {
        PdkKitRegistry.SetKit("KitOne", [Part("PART_A")]);
        PdkKitRegistry.SetKit("KitTwo", [Part("PART_A")]);

        PdkKitRegistry.RemoveKit("KitOne");

        Assert.False(PdkKitRegistry.HasKit("KitOne"));
        Assert.True(PdkKitRegistry.HasKit("KitTwo"));
        Assert.Null(PdkKitRegistry.Find(PdkKitRegistry.RefFor("KitOne", "PART_A")));
        Assert.NotNull(PdkKitRegistry.Find(PdkKitRegistry.RefFor("KitTwo", "PART_A")));
    }

    // ── Resolution (R-pdk-6) ──────────────────────────────────────────────────

    /// <summary>
    /// The headline: a kit part resolves to its symbol out of memory, with no directory anywhere in
    /// the call. The base directory is deliberately garbage — if it were consulted this would fail.
    /// </summary>
    [Fact]
    public void AKitPart_ResolvesToItsSymbol_WithoutTouchingTheFilesystem()
    {
        PdkKitRegistry.SetKit("SampleKit", [Part("PART_A", pins: 3)]);

        var res = CellSymbolResolver.Resolve(
            PdkKitRegistry.RefFor("SampleKit", "PART_A"), "/no/such/directory/anywhere");

        Assert.Equal(CellSymbolState.Resolved, res.State);
        Assert.Equal(3, res.Symbol!.Pins.Count);
    }

    /// <summary>
    /// An unloaded kit is NotFound — the reported, repairable state that draws the placeholder. It
    /// must NOT fall through to the path branch, which would report a directory nobody expected.
    /// </summary>
    [Fact]
    public void AKitThatIsNotLoaded_ResolvesToNotFound()
    {
        var res = CellSymbolResolver.Resolve(
            PdkKitRegistry.RefFor("SampleKit", "PART_A"), "/no/such/directory/anywhere");

        Assert.Equal(CellSymbolState.NotFound, res.State);
        Assert.Null(res.Symbol);
    }

    [Fact]
    public void AKitPartsPublishedInterface_ResolvesFromMemoryToo()
    {
        PdkKitRegistry.SetKit("SampleKit", [Part("PART_A", pins: 4)]);

        var cell = CellSymbolResolver.ResolveCcell(
            PdkKitRegistry.RefFor("SampleKit", "PART_A"), baseDir: "");

        Assert.NotNull(cell);
        Assert.Equal(4, cell!.NumPorts);
    }

    [Fact]
    public void AnUnloadedKitsInterface_IsNull_NotAnException()
        => Assert.Null(CellSymbolResolver.ResolveCcell(
               PdkKitRegistry.RefFor("SampleKit", "PART_A"), baseDir: ""));
}
