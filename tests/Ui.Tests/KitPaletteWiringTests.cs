using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// What the importer worked out about a part has to REACH the palette tile.
///
/// <para><b>Why this file exists, stated plainly: both of the things it checks were silently broken
/// and the whole 5,600-test suite stayed green.</b> Every other test in this area checks one side or
/// the other — the reader produces a drawing, <see cref="KitTemplateSymbol"/> turns one into
/// primitives, the palette groups by a category string — and nothing checked that the installer
/// carries either fact ACROSS. Both failures are invisible to a compiler and to every existing test:
/// a part with its drawing dropped still places and still connects, it just looks like every other
/// part; a reference built positionally still compiles, its grouping just defaults to empty and the
/// kit's own sub-headings quietly vanish from the filter.</para>
///
/// <para>Synthetic throughout. The repository commits no third-party kit data.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class KitPaletteWiringTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "crf-kitwire-" + Guid.NewGuid().ToString("N")[..8]);

    public KitPaletteWiringTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static IReadOnlyList<KitSymbolPin> Pins() =>
    [
        new("a", -400, 0),
        new("b",  400, 0),
    ];

    /// <summary>A drawing that is plainly not the box a pin-only part falls back to.</summary>
    private static IReadOnlyList<KitSymbolShape> Drawing() =>
    [
        new KitSymbolLine(-300, -200,  300, -200),
        new KitSymbolLine( 300, -200,  300,  200),
        new KitSymbolLine( 300,  200, -300,  200),
        new KitSymbolLine(-300,  200, -300, -200),
        new KitSymbolLine(-300,    0,  300,    0),
        new KitSymbolLine(   0, -200,    0,  200),
        new KitSymbolLine(-300, -200,  300,  200),
        new KitSymbolLine(-300,  200,  300, -200),
        new KitSymbolLine(-150, -200, -150,  200),
    ];

    private PdkImportReport ReportWith(params PdkPart[] parts)
    {
        var report = new PdkImportReport { RootPath = _root, KitName = "SampleKit" };
        report.Parts.AddRange(parts);
        return report;
    }

    // ── the drawn body ────────────────────────────────────────────────────────

    [Fact]
    public void W1_APartCarryingADrawingGetsThatDrawing_NotTheFallbackBox()
    {
        var drawn = new PdkPart(Id: "DRAWN", DisplayName: "DRAWN",
                                PinCount: 2, Pins: Pins(), Body: Drawing());

        // Null body — not an empty one. Null means "came from a symbol library, which states no
        // artwork"; empty means "came from a drawing that drew nothing". Only the first falls back.
        var plain = new PdkPart(Id: "PLAIN", DisplayName: "PLAIN",
                                PinCount: 2, Pins: Pins(), Body: null);

        var outcome = PdkPartInstaller.Install(ReportWith(drawn, plain));
        var built   = outcome.Parts ?? [];

        Assert.Equal(2, built.Count);

        int drawnPrims = built.Single(p => p.PartId == "DRAWN").Symbol.Primitives.Count;
        int plainPrims = built.Single(p => p.PartId == "PLAIN").Symbol.Primitives.Count;

        // Nine drawn segments against whatever the fallback box is. The exact counts are not the
        // point; that the two DIFFER is — identical counts is precisely what "every kit symbol shows
        // as a generic block" looks like from here. Nine deliberately, because a four-sided box plus
        // its pin stubs lands on six and the first version of this test collided with it by accident.
        Assert.Equal(9, drawnPrims);
        Assert.NotEqual(plainPrims, drawnPrims);
    }

    [Fact]
    public void W2_EveryPartLookingIdenticalIsWhatARegressionHereLooksLike()
    {
        // The shape of the assertion that would have caught it on a kit: several parts drawn
        // differently must not collapse to one primitive count.
        var a = new PdkPart(Id: "A", DisplayName: "A", PinCount: 2, Pins: Pins(), Body: Drawing());
        var b = new PdkPart(Id: "B", DisplayName: "B", PinCount: 2, Pins: Pins(),
                            Body: [new KitSymbolLine(-300, 0, 300, 0)]);

        var built = PdkPartInstaller.Install(ReportWith(a, b)).Parts ?? [];

        Assert.Equal(2, built.Select(p => p.Symbol.Primitives.Count).Distinct().Count());
    }

    // ── what the tile carries ─────────────────────────────────────────────────

    [Fact]
    public void W3_TheKitsOwnGroupingReachesTheTile_Verbatim()
    {
        // The palette lists a kit's own groupings indented beneath it, reading this field. Defaulted
        // to empty — which is what a positionally-built reference does — every part of the kit files
        // under the kit heading alone and the sub-headings disappear with nothing failing.
        var part = new PdkPart(Id: "P", DisplayName: "P", Category: "capacitor",
                               PinCount: 2, Pins: Pins(), Body: Drawing());

        var item = Assert.Single(PdkPartInstaller.Install(ReportWith(part)).Items);

        Assert.NotNull(item.Pdk);
        Assert.Equal("capacitor", item.Pdk!.Category);
    }

    [Fact]
    public void W4_TheDeclaredModelNameReachesTheTile()
    {
        // The one identity a kit's schematic part and its layout cell reliably share, and the third
        // rule KitPaletteMerge falls back to when neither id rule can match.
        var part = new PdkPart(
            Id: "P", DisplayName: "P", PinCount: 2, Pins: Pins(), Body: Drawing(),
            Parameters: [new PdkPartParameter("model", "mim_core"),
                         new PdkPartParameter("w", "7u")]);

        var item = Assert.Single(PdkPartInstaller.Install(ReportWith(part)).Items);

        Assert.Equal("mim_core", item.Pdk!.ModelName);
    }

    [Fact]
    public void W5_APartDeclaringNoModelCarriesNoModelName_RatherThanSomethingElsesDefault()
    {
        var part = new PdkPart(Id: "P", DisplayName: "P", PinCount: 2, Pins: Pins(), Body: Drawing(),
                               Parameters: [new PdkPartParameter("w", "7u")]);

        Assert.Equal("", Assert.Single(PdkPartInstaller.Install(ReportWith(part)).Items).Pdk!.ModelName);
    }

    // ── the kit's own number notation ─────────────────────────────────────────

    /// <summary>
    /// <b>A kit spells a number the way its own simulator reads one, and circuitRF reads no
    /// engineering suffixes</b> — a value's unit is a FIELD on the row, not a letter on the number
    /// (measured: <c>0.72u</c> is <i>Parse error at position 2</i>; <c>0.72</c> with the unit µm
    /// resolves). A default left in the kit's spelling therefore reaches the schematic as something
    /// nothing downstream can evaluate: the artwork happens to survive, because the kit's own cell
    /// parses its own spelling, and then Run fails a long way away with a message about a token.
    ///
    /// <para>The kit measured is not even consistent with itself — its own symbol templates write
    /// <c>7.0e-6</c> for one part and <c>0.72u</c> for the next — so there is nothing to detect and no
    /// assumption to make. Every default is read in the dialect it was written in.</para>
    /// </summary>
    [Theory]
    [InlineData("0.72u", "7.2E-07")]
    [InlineData("600n",  "6E-07")]
    [InlineData("1.5p",  "1.5E-12")]
    [InlineData("1n",    "1E-09")]
    public void AKitsOwnSpellingBecomesSomethingCircuitRfCanEvaluate(string kitWrote, string expected)
        => Assert.Equal(expected, PdkPartInstaller.InCircuitRfsOwnNotation(kitWrote));

    /// <summary>
    /// <b>The trap this shares a parser with the netlist reader to avoid.</b> That dialect is
    /// case-insensitive and spells milli <c>M</c>, with mega as <c>MEG</c>; circuitRF's own unit table
    /// is SI and case-SENSITIVE, where <c>M</c> is mega. Reading a kit's suffix through the SI table
    /// turns one millifarad into one megafarad — a factor of 10⁹ in a value that still parses, still
    /// stamps and still converges. Pinned here because "just use the unit table" is the obvious
    /// simplification and it is silently wrong.
    /// </summary>
    [Theory]
    [InlineData("1M",   "0.001")]
    [InlineData("1m",   "0.001")]
    [InlineData("1MEG", "1000000")]
    [InlineData("1meg", "1000000")]
    public void MilliAndMegaFollowTheKitsDialect_NotCircuitRfsUnitTable(string kitWrote, string expected)
        => Assert.Equal(expected, PdkPartInstaller.InCircuitRfsOwnNotation(kitWrote));

    /// <summary>
    /// Anything circuitRF can already read is left EXACTLY as the kit wrote it. Rewriting a value that
    /// was already fine replaces the kit's own spelling with a formatting of it, for no gain — and the
    /// measured kit writes plenty of them.
    /// </summary>
    [Theory]
    [InlineData("7.0e-6")]
    [InlineData("10e-6")]
    [InlineData("1")]
    [InlineData("0.5e-6")]
    public void AValueCircuitRfCanAlreadyRead_IsLeftVerbatim(string kitWrote)
        => Assert.Equal(kitWrote, PdkPartInstaller.InCircuitRfsOwnNotation(kitWrote));

    /// <summary>A word-valued default is not a number and must not be turned into one.</summary>
    [Theory]
    [InlineData("cap_mim")]
    [InlineData("Selected")]
    [InlineData("w&l")]
    [InlineData("")]
    public void AWordValuedDefault_PassesStraightThrough(string kitWrote)
        => Assert.Equal(kitWrote, PdkPartInstaller.InCircuitRfsOwnNotation(kitWrote));

    /// <summary>And it reaches the built part, which is the point of the whole thing.</summary>
    [Fact]
    public void TheNormalisedDefaultIsWhatAPlacedInstanceSeedsFrom()
    {
        var part = new PdkPart(
            Id: "P", DisplayName: "P", PinCount: 2, Pins: Pins(), Body: Drawing(),
            Parameters: [new PdkPartParameter("l", "0.72u"), new PdkPartParameter("w", "1.0e-6")]);

        var built = Assert.Single(PdkPartInstaller.Install(ReportWith(part)).Parts ?? []);

        Assert.Equal("7.2E-07", built.Ccell.Parameters.Single(p => p.Name == "l").DefaultExpression);
        Assert.Equal("1.0e-6",  built.Ccell.Parameters.Single(p => p.Name == "w").DefaultExpression);
    }
}
