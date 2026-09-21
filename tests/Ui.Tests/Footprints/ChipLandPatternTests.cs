using System.Globalization;
using CircuitRF.Ui.Layout.PCells;

namespace CircuitRF.Ui.Tests.Footprints;

/// <summary>
/// brief-footprint-1-land-pattern-generator.md §7 — the gate for the land-pattern generator.
///
/// <para><b>Where this file lives.</b> The brief asks for <c>tests/Design.Tests/</c>. There is no such
/// project: every <c>src/Design</c> feature is tested from here, including the two that crossed the
/// firewall for the same reason this one is below it (the DRC engine, the interchange readers), and
/// gate 7 needs <c>PCellRegistry</c>, which is in <c>src/Ui</c>. A new test project that referenced
/// <c>src/Ui</c> in order to test <c>src/Design</c> would say the opposite of what the layering
/// is.</para>
/// </summary>
public sealed class ChipLandPatternTests
{
    // ══ 1. The table is self-consistent ═════════════════════════════════════════════════════════
    //
    // A hand-maintained twin column is a column that goes wrong once and is never looked at again
    // (R-fp1-1a). A metric code IS the body in tenths of a millimetre, so the column is derivable
    // and therefore checkable; the imperial code is checkable only from 0402 up, and only to the 5%
    // the industry's own rounding allows (0201 imperial is 0.6 mm, not the 0.508 mm its code
    // literally says) — still tight enough to catch a transposed digit.

    [Fact]
    public void EveryRowsMetricTwinFollowsFromItsOwnDimensions()
    {
        foreach (var c in SmtCaseTable.All)
        {
            if (c.CodeIsMetric)
            {
                // R-fp1-1b: a tantalum code is ALREADY metric, so its twin column reads the LETTER.
                // Its own code carries the dimensions instead, height included.
                Assert.True(c.MetricTwin is "A" or "B" or "C" or "D" or "X",
                    $"{c.Code}: a moulded tantalum's twin column is the EIA letter, not '{c.MetricTwin}'.");
                Assert.Equal($"{Tenths(c.BodyLengthMm):00}{Tenths(c.BodyWidthMm):00}-{Tenths(c.BodyHeightMm ?? 0m):00}", c.Code);
                continue;
            }

            Assert.Equal($"{Tenths(c.BodyLengthMm):00}{Tenths(c.BodyWidthMm):00}", c.MetricTwin);

            // The imperial code is a dimension only from 0402 up. Below that the industry rounds its
            // own codes UP and by a lot — 01005 literally says 0.010 in = 0.254 mm for a part that is
            // 0.40 mm long — so asserting the conversion on those three rows would assert the
            // nickname, not the part. Above the threshold the code means what it says to within the
            // 5% its own rounding allows, which is still tight enough to catch a transposed digit.
            var (inchL, inchW) = ImperialCode(c.Code);
            if (inchL < 0.04m) continue;
            AssertWithin(inchL * 25.4m, c.BodyLengthMm, $"{c.Code} length");
            AssertWithin(inchW * 25.4m, c.BodyWidthMm, $"{c.Code} width");
        }

        // The collision §1e of the overview is about: two real, shipped case sizes spelled 0201 and
        // differing by 2.4x. Every mention of a case reads its twin and its millimetres for exactly
        // this reason, so the display string is part of the contract, not decoration.
        Assert.Equal("0201 (metric 0603)   0.60 x 0.30 mm", SmtCaseTable.Find("0201")!.Display);
        Assert.Equal("008004 (metric 0201)   0.25 x 0.125 mm", SmtCaseTable.Find("008004")!.Display);
    }

    private static int Tenths(decimal mm) => (int)(mm * 10m);

    private static (decimal L, decimal W) ImperialCode(string code)
    {
        int half = code.Length / 2;
        decimal Parse(string s) => decimal.Parse(s, CultureInfo.InvariantCulture) / Pow10(s.Length);
        return (Parse(code[..half]), Parse(code[half..]));
    }

    private static decimal Pow10(int n) { decimal d = 1m; for (int i = 0; i < n; i++) d *= 10m; return d; }

    private static void AssertWithin(decimal fromCode, decimal body, string what)
    {
        decimal ratio = body / fromCode;
        Assert.True(ratio is > 0.90m and < 1.10m,
            $"{what}: the code says {fromCode:0.000} mm, the table says {body:0.000} mm — a 10% " +
            "discrepancy is past the industry's own rounding and reads like a transposed digit.");
    }

    // ══ 2. Three densities, in the right direction ══════════════════════════════════════════════

    [Fact]
    public void ThreeDensitiesDifferInTheRightDirectionAndStayCentred()
    {
        var c = SmtCaseTable.Find("0402")!;
        var m = LandPattern.For(c, DensityLevel.Most, LayoutUnits.DefaultDbuPerMicron);
        var n = LandPattern.For(c, DensityLevel.Nominal, LayoutUnits.DefaultDbuPerMicron);
        var l = LandPattern.For(c, DensityLevel.Least, LayoutUnits.DefaultDbuPerMicron);

        static long Area(LandPattern p) => p.PadWidthDbu * p.PadHeightDbu;
        Assert.True(Area(m) > Area(n), $"Most {Area(m)} must exceed Nominal {Area(n)}.");
        Assert.True(Area(n) > Area(l), $"Nominal {Area(n)} must exceed Least {Area(l)}.");

        foreach (var p in new[] { m, n, l })
            Assert.Equal(-p.Pad1CentreXDbu, p.Pad2CentreXDbu);

        // Nowhere in the table may the two lands merge: a negative gap is a short, not a dense
        // pattern, and the clamp that prevents it must never have to fire.
        foreach (var kase in SmtCaseTable.All)
            foreach (var d in new[] { DensityLevel.Most, DensityLevel.Nominal, DensityLevel.Least })
            {
                var p = LandPattern.For(kase, d, LayoutUnits.DefaultDbuPerMicron);
                Assert.False(p.GapWasClamped, $"{kase.Code}@{FootprintRef.CodeOf(d)} leaves no gap between its lands.");
                Assert.True(p.PadWidthDbu > 0 && p.PadHeightDbu > 0, $"{kase.Code}@{FootprintRef.CodeOf(d)} has an empty land.");
            }
    }

    // ══ 3. Layers resolve by ROLE on three technologies ═════════════════════════════════════════
    //
    // This is the test that would have caught a shipped .clay: the three technologies put silkscreen
    // on (5,0), on (7,0), and nowhere at all, and a stored land pattern carrying an absolute layer
    // key would have painted silk into the second one's soldermask with nothing said.
    //
    // THE THIRD ONE IS A FIXTURE AND NOT A SHIPPED TECHNOLOGY, from brief-footprint-5 R-fp5-1c. The
    // Power Rail example's technology WAS the no-silkscreen case until that brief gave it the three
    // drawing layers a board carries; what is kept here is the five-layer table it had before, so
    // the case of a PCB technology with copper and nothing else stays covered. It is real — an
    // imported Gerber set with no silkscreen in it mints exactly this — and the omission path is
    // what R-fp1-3a is about.

    [Fact]
    public void LayersResolveByRoleOnEachTechnologyAndAMissingRoleIsReportedNotRelocated()
    {
        var two  = ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz");
        var four = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        var copperOnly = CopperOnlyTechnology();

        var reference = Parse("smt:0402@N");

        var onTwo  = ChipLandPatternGenerator.Generate(reference, two,  PCellLayerSelection.Default);
        var onFour = ChipLandPatternGenerator.Generate(reference, four, PCellLayerSelection.Default);
        var onCopperOnly = ChipLandPatternGenerator.Generate(reference, copperOnly, PCellLayerSelection.Default);

        Assert.Equal(new LayerKey(1, 0), CopperOf(onTwo, two));
        Assert.Equal(new LayerKey(1, 0), CopperOf(onFour, four));
        Assert.Equal(new LayerKey(1, 0), CopperOf(onCopperOnly, copperOnly));

        // Silk lands where each technology actually keeps it — and the keys differ, which is the point.
        Assert.Contains(onTwo.Shapes,  s => s.Layer == new LayerKey(5, 0));
        Assert.Contains(onFour.Shapes, s => s.Layer == new LayerKey(7, 0));

        // The third has no silkscreen at all: nothing is drawn, and the omission is NAMED.
        Assert.All(onCopperOnly.Shapes, s => Assert.Equal(new LayerKey(1, 0), s.Layer));
        Assert.Contains(onCopperOnly.Diagnostics ?? [], d =>
            d.Contains("silkscreen", StringComparison.OrdinalIgnoreCase) &&
            d.Contains(copperOnly.Name, StringComparison.Ordinal));

        // And the technology that fixture was taken from now has all three roles, which is the
        // other half of R-fp5-1: the shipped example generates its own footprints.
        var rail = PowerRailTechnology();
        var onRail = ChipLandPatternGenerator.Generate(reference, rail, PCellLayerSelection.Default);
        Assert.Equal(new LayerKey(1, 0), CopperOf(onRail, rail));
        Assert.Contains(onRail.Shapes, s => s.Layer == new LayerKey(5, 0));
        Assert.Contains(onRail.Shapes, s => s.Layer == new LayerKey(6, 0));
    }

    // ══ 4. A MMIC technology is refused, by name ════════════════════════════════════════════════

    [Fact]
    public void AMmicTechnologyIsRefusedByNameWithNoPattern()
    {
        var mmic = ShippedTechnologies.Load("mmic-GaAs_2LM_100um");
        var result = ChipLandPatternGenerator.Generate(Parse("smt:0402@N"), mmic, PCellLayerSelection.Default);

        Assert.Empty(result.Shapes);
        Assert.Empty(result.Pins);
        string sentence = Assert.Single(result.Diagnostics!);
        Assert.Contains(mmic.Name, sentence, StringComparison.Ordinal);
        Assert.Contains("front-copper", sentence, StringComparison.OrdinalIgnoreCase);

        // And with no technology at all, for the same reason and in the same shape.
        var nothing = ChipLandPatternGenerator.Generate(Parse("smt:0402@N"), null, PCellLayerSelection.Default);
        Assert.Empty(nothing.Shapes);
        Assert.Single(nothing.Diagnostics!);
    }

    // ══ 5. No courtyard on Edge.Cuts ════════════════════════════════════════════════════════════
    //
    // R-fp1-3c: that layer is the BOARD OUTLINE, and a courtyard rectangle on it is a routed slot.

    [Fact]
    public void NothingIsEverDrawnOnTheBoardOutlineLayer()
    {
        foreach (var entry in ShippedTechnologies.All)
        {
            var tech = ShippedTechnologies.Load(entry);
            var outline = tech.Layers.FirstOrDefault(l =>
                string.Equals(l.Interchange?.PcbLayerName, "Edge.Cuts", StringComparison.OrdinalIgnoreCase));
            if (outline is null) continue;

            foreach (var c in SmtCaseTable.All)
            {
                var result = ChipLandPatternGenerator.Generate(
                    FootprintRef.For(c), tech, PCellLayerSelection.Default);
                Assert.DoesNotContain(result.Shapes, s => s.Layer == outline.Key);
            }
        }
    }

    // ══ 6. The parser is total ══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("smt:0402",   "smt:0402@N")]
    [InlineData("smt:0402@M", "smt:0402@M")]
    public void AWellFormedReferenceParsesToItsCanonicalSpelling(string text, string canonical)
    {
        Assert.True(FootprintRef.TryParse(text, out var r, out string? why), why);
        Assert.Equal(canonical, r!.ToString());
        Assert.Null(why);
    }

    [Theory]
    [InlineData("smt:9999")]    // an unknown case
    [InlineData("smt:0402@Q")]  // an unknown density
    [InlineData("smt:")]        // no case at all
    [InlineData("")]            // nothing at all
    [InlineData("0402")]        // no scheme — a path, per R-fp1-5b, and not this parser's business
    public void AMalformedReferenceIsRefusedAndNeverThrows(string text)
    {
        Assert.False(FootprintRef.TryParse(text, out var r, out string? why));
        Assert.Null(r);
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    [Fact]
    public void AnUnknownCaseRefusalListsTheRealCodes()
    {
        Assert.False(FootprintRef.TryParse("smt:9999", out _, out string? why));
        foreach (string code in SmtCaseTable.Codes)
            Assert.Contains(code, why!, StringComparison.Ordinal);
    }

    // ══ 7. The CLI path and the registry path agree, byte for byte ══════════════════════════════
    //
    // R-fp1-6b. src/Cli cannot see PCellRegistry, so it calls the generator in src/Design directly;
    // src/Ui reaches the same generator through the resolver seam. Both must be the same artwork, or
    // a headless render and the window disagree about a board with nothing to say which is right.

    [Fact]
    public void TheRegistryPathAndTheDirectPathProduceTheSameLayoutView()
    {
        var tech = ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz");

        foreach (string id in new[] { "smt:0402@N", "smt:0603@M", "smt:7343-31@L" })
        {
            Assert.True(PCellRegistry.TryGet(id, out var viaRegistry), $"the registry does not answer for '{id}'.");
            var registryResult = viaRegistry(new Dictionary<string, PCellValue>(), tech, PCellLayerSelection.Default);

            var direct = ChipLandPatternGenerator.Generate(Parse(id), tech, PCellLayerSelection.Default);

            Assert.Equal(ViewJson(registryResult, tech), ViewJson(direct, tech));
        }

        // And the resolver answers for the built-in ids and for nothing else.
        Assert.Contains("smt:0402@N", PCellRegistry.AllKnownGeneratorIds());
        Assert.False(PCellRegistry.TryGet("smt:9999@N", out _));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static FootprintRef Parse(string text)
    {
        Assert.True(FootprintRef.TryParse(text, out var r, out string? why), why);
        return r!;
    }

    /// <summary>One assembly of a <see cref="PCellResult"/> into a document, used for BOTH paths —
    /// so any difference the comparison finds is the generator's and not the harness's.</summary>
    private static string ViewJson(PCellResult result, Technology tech)
    {
        var view = new LayoutView
        {
            DbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
            DisplayUnit  = tech.DefaultDisplayUnit,
            SnapDbu      = tech.DefaultSnapDbu,
        };
        view.Shapes.AddRange(result.Shapes);
        foreach (var p in result.Pins)
            view.Pins.Add(new LayoutPin
            {
                Name = p.Name, X = p.X, Y = p.Y,
                WidthDbu = p.WidthDbu, OutwardDeg = p.OutwardDirectionDeg, Layer = p.Layer,
            });
        return LayoutPersistence.Serialize(view);
    }

    private static LayerKey CopperOf(PCellResult result, Technology tech)
    {
        Assert.NotEmpty(result.Pins);
        var key = result.Pins[0].Layer;
        Assert.Contains(tech.Layers, l => l.Key == key);
        return key;
    }

    /// <summary>R-fp5-1c's fixture: the Power Rail example's five-layer table, as it was before the
    /// three drawing layers were added — a PCB technology with copper and nothing else.</summary>
    private static Technology CopperOnlyTechnology()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        string path = Path.Combine(dir, "tests", "Ui.Tests", "Footprints", "Fixtures",
                                   "pcb-4layer-copper-only.ctech");
        Assert.True(File.Exists(path), $"the copper-only technology fixture is not at '{path}'.");
        return TechPersistence.Deserialize(File.ReadAllText(path));
    }

    private static Technology PowerRailTechnology()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        string path = Path.Combine(dir, "examples", "Power Rail", "tech", "pcb-4layer-1p6mm.ctech");
        Assert.True(File.Exists(path), $"the Power Rail example's technology is not at '{path}'.");
        return TechPersistence.Deserialize(File.ReadAllText(path));
    }
}
