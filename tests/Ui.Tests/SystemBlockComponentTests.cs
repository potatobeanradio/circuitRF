using System.IO;
using CircuitRF.Core.Matching;
using CircuitRF.Design.Cells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The eleven system-level blocks on the schematic side (brief-sys-1): the artwork, the pins, the
/// ground-return extraction and the <c>System</c> palette category.
///
/// <para><b>None of them has an engine model yet</b>, and that is the point of the brief — the owner
/// approves the artwork before anything is built on it, because the artwork is the part users read
/// most. So what is asserted here is everything except behaviour: that every pin lands on the
/// connection grid and every lead reaches it, that a schematic of one of each survives a save and a
/// reload, that the four dynamic glyphs really are different pictures, and that the filter's glyph
/// is the match glyph <i>by construction</i> rather than by resemblance.</para>
/// </summary>
public class SystemBlockComponentTests
{
    /// <summary>The eleven tiles this brief adds. Every mechanical gate below runs over all of them.</summary>
    public static readonly SymbolKind[] SystemBlocks =
    [
        SymbolKind.Balun, SymbolKind.Circulator, SymbolKind.Switch, SymbolKind.SwitchD,
        SymbolKind.Amp, SymbolKind.Coupler, SymbolKind.Hybrid90, SymbolKind.Hybrid180,
        SymbolKind.Filter, SymbolKind.Atten, SymbolKind.Duplexer,
    ];

    public static TheoryData<SymbolKind> EachBlock()
    {
        var d = new TheoryData<SymbolKind>();
        foreach (var k in SystemBlocks) d.Add(k);
        return d;
    }

    // ══ Geometry, asserted mechanically over every kind ═══════════════════════
    //
    // By eye is exactly how a pin half a grid square off its lead survives review, so neither of the
    // two rules below is checked on a coordinate list — they are checked on the primitives that are
    // actually drawn, for every tile, with no per-kind exceptions to forget to add.

    [Theory]
    [MemberData(nameof(EachBlock))]
    public void EveryPinLandsOnTheConnectionGrid(SymbolKind kind)
    {
        foreach (var p in SymbolPortDefs.For(kind))
        {
            Assert.Equal(0f, p.LocalX % 100f);
            Assert.Equal(0f, p.LocalY % 100f);
        }
    }

    // The symbol's lead ENDS at the pin — the renderer draws the connection marker, so a lead that
    // stops short leaves a visible gap and a pin that reads as unconnected. This is the gate the
    // duplexer's antenna lead failed in review before the coordinates were corrected.
    [Theory]
    [MemberData(nameof(EachBlock))]
    public void EveryPinHasALeadEndingExactlyOnIt(SymbolKind kind)
    {
        var sym = BuiltInSymbols.Primitives(kind);
        foreach (var pin in sym.Pins)
        {
            bool reached = sym.Primitives.OfType<LinePrimitive>().Any(l =>
                (Near(l.X1, pin.LocalX) && Near(l.Y1, pin.LocalY)) ||
                (Near(l.X2, pin.LocalX) && Near(l.Y2, pin.LocalY)));
            Assert.True(reached,
                $"{kind} pin '{pin.Name}' at ({pin.LocalX}, {pin.LocalY}) has no lead ending on it. "
              + "A port whose lead stops short of its pin reads as unconnected.");
        }
    }

    private static bool Near(double a, double b) => System.Math.Abs(a - b) < 1e-6;

    // ── The duplexer's own two corrections, both owner findings off a rendered sheet ──

    [Fact]
    public void Duplexer_AntennaLeadRunsFromItsPinToTheBodyEdge()
    {
        var sym = BuiltInSymbols.Primitives(SymbolKind.Duplexer);
        var body = Assert.Single(sym.Primitives.OfType<RoundedRectPrimitive>());
        double leftEdge = body.Cx - body.W / 2;

        var ant = Assert.Single(sym.Primitives.OfType<LinePrimitive>()
            .Where(l => Near(l.X1, -300) && Near(l.Y1, 0)));
        Assert.Equal(leftEdge, ant.X2, 9);
        Assert.Equal(0.0, ant.Y2, 9);
    }

    // Containment is not the gate — a label that merely FITS is exactly what a coordinate list hides
    // and a reader sees immediately. So this measures the gap to the frame AND to the wave stacks.
    [Fact]
    public void Duplexer_LabelsSitInsideTheBody_WithRealClearance()
    {
        var sym  = BuiltInSymbols.Primitives(SymbolKind.Duplexer);
        var body = Assert.Single(sym.Primitives.OfType<RoundedRectPrimitive>());
        double right = body.Cx + body.W / 2, bottom = body.Cy + body.H / 2;
        double top   = body.Cy - body.H / 2;

        // A generous box for a 2-character label at MixerPortFontSize, centred on its anchor.
        const double halfW = 2 * BuiltInSymbols.MixerPortFontSize * 0.6 / 2;
        const double halfH = BuiltInSymbols.MixerPortFontSize * 0.75 / 2;
        const double minGap = 15;

        var waves = sym.Primitives.OfType<SinePrimitive>().ToList();
        Assert.Equal(6, waves.Count);                       // two stacks of three
        double waveRight = waves.Max(w => w.Cx + w.Length / 2);

        foreach (var label in sym.Primitives.OfType<TextPrimitive>()
                                 .Where(t => t.Content is "TX" or "RX"))
        {
            Assert.True(label.AnchorX + halfW < right - minGap,
                $"'{label.Content}' comes within {right - (label.AnchorX + halfW):0.#} of the frame");
            Assert.True(label.AnchorY - halfH > top + minGap && label.AnchorY + halfH < bottom - minGap,
                $"'{label.Content}' is not clear of the frame vertically");
            Assert.True(label.AnchorX - halfW > waveRight + minGap,
                $"'{label.Content}' comes within {(label.AnchorX - halfW) - waveRight:0.#} of its wave stack");
        }
    }

    // The 90° label was specified at the body's exact centre, which the coupling arrow passes
    // straight through — a struck-through label. It is drawn clear of the arrow instead, and this is
    // the arithmetic that says so, for the WIDER of the two hybrids as well as the narrower.
    [Theory]
    [InlineData(SymbolKind.Hybrid90,  "90°")]
    [InlineData(SymbolKind.Hybrid180, "180°")]
    public void Hybrid_PhaseLabelClearsBothTheCouplingArrowAndTheFrame(SymbolKind kind, string expected)
    {
        var sym   = BuiltInSymbols.Primitives(kind);
        var body  = Assert.Single(sym.Primitives.OfType<RoundedRectPrimitive>());
        var label = Assert.Single(sym.Primitives.OfType<TextPrimitive>()
                                     .Where(t => t.Content == expected));

        double halfW = expected.Length * label.FontSize * 0.6 / 2;
        double halfH = label.FontSize * 0.75 / 2;
        double left  = body.Cx - body.W / 2;

        Assert.True(label.AnchorX - halfW > left + 10,
            $"'{expected}' overruns the frame by {left - (label.AnchorX - halfW):0.#}");

        // The arrow's shaft, at the label's own vertical extent. At the centre of the body it is at
        // x = 0, which is why a centred label cannot survive.
        var shaft = Assert.Single(sym.Primitives.OfType<LinePrimitive>()
            .Where(l => l.X1 != l.X2 && l.Y1 != l.Y2));
        double t  = (label.AnchorY + halfH - shaft.Y1) / (shaft.Y2 - shaft.Y1);
        double xAtLabel = shaft.X1 + t * (shaft.X2 - shaft.X1);
        Assert.True(label.AnchorX + halfW < xAtLabel - 10,
            $"'{expected}' is struck through by the coupling arrow at x = {xAtLabel:0.#}");
    }

    // ── The glyphs say the things the brief says they must ────────────────────

    [Fact]
    public void Amp_IsAnEmptyTriangle_BecauseTheGainIsALabel()
    {
        var sym = BuiltInSymbols.Primitives(SymbolKind.Amp);
        var tri = Assert.Single(sym.Primitives.OfType<PolygonPrimitive>());
        Assert.Equal(3, tri.Points.Count);
        Assert.False(tri.Filled);
        Assert.Empty(sym.Primitives.OfType<TextPrimitive>());
    }

    // Two interchangeable pins get no names; three that are not interchangeable get all three.
    // A name on a symmetric pin is noise that reads as meaning, and a missing one on an asymmetric
    // pin produces a circuit that solves and is wrong.
    [Theory]
    [InlineData(SymbolKind.Switch)]
    [InlineData(SymbolKind.Atten)]
    public void ASymmetricTwoPortCarriesNoPortNames(SymbolKind kind)
        => Assert.Empty(BuiltInSymbols.Primitives(kind).Primitives.OfType<TextPrimitive>());

    [Fact]
    public void Coupler_NumbersAllFourPorts_SoTheCoupledPortIsNotGuessedAt()
    {
        var text = BuiltInSymbols.Primitives(SymbolKind.Coupler).Primitives
                                 .OfType<TextPrimitive>().Select(t => t.Content).ToList();
        Assert.Equal(["1", "2", "3", "4"], text.Order().ToList());

        // …and the arrow that separates the coupled port from the isolated one is a FILLED head on a
        // shaft that runs from the main arm to the coupled arm. Without it the symbol is ambiguous.
        var head = Assert.Single(BuiltInSymbols.Primitives(SymbolKind.Coupler)
                                               .Primitives.OfType<PolygonPrimitive>());
        Assert.True(head.Filled);
        Assert.True(head.Points.Max(p => p[1]) > 0, "the arrow must point at the COUPLED arm");
    }

    [Fact]
    public void Balun_ShowsOneUnbalancedLeadAgainstAPolarisedPair()
    {
        var sym = BuiltInSymbols.Primitives(SymbolKind.Balun);
        Assert.Equal(6, sym.Primitives.OfType<ArcPrimitive>().Count());     // two 3-arc coils
        var marks = sym.Primitives.OfType<TextPrimitive>().ToList();
        Assert.Equal(2, marks.Count);
        Assert.Equal(SymbolColorRole.SymbolPlus, Assert.Single(marks, m => m.Content == "+").ColorRole);
        Assert.Contains(marks, m => m.Content == "−");
    }

    // ══ The four dynamic glyphs ═══════════════════════════════════════════════
    //
    // Two properties, and both matter. A DIFFERENT list per variant, or the parameter does nothing
    // and a switch drawn closed is a switch that might be open. The SAME instance for the same
    // variant, or every render allocates a new primitive list — which is what the per-variant cache
    // exists to prevent, and the only thing that makes a glyph cheap enough to build on demand.

    [Fact]
    public void Circulator_DrawsADifferentArrowPerDirection_AndCachesEach()
    {
        var cw  = BuiltInSymbols.PrimitivesForCirculator(CirculatorDirection.CW);
        var ccw = BuiltInSymbols.PrimitivesForCirculator(CirculatorDirection.CCW);

        Assert.NotEqual(ArcKeys(cw), ArcKeys(ccw));
        Assert.Same(cw,  BuiltInSymbols.PrimitivesForCirculator(CirculatorDirection.CW));
        Assert.Same(ccw, BuiltInSymbols.PrimitivesForCirculator(CirculatorDirection.CCW));

        // The two turn OPPOSITE ways, which is the whole content of the symbol.
        var a = Assert.Single(cw.Primitives.OfType<ArcPrimitive>().Where(x => x.R < 150));
        var b = Assert.Single(ccw.Primitives.OfType<ArcPrimitive>().Where(x => x.R < 150));
        Assert.NotEqual(System.Math.Sign(a.SweepDeg), System.Math.Sign(b.SweepDeg));
    }

    private static string ArcKeys(Symbol s)
        => string.Join("|", s.Primitives.OfType<ArcPrimitive>()
                             .Select(a => $"{a.Cx},{a.Cy},{a.R},{a.StartDeg},{a.SweepDeg}"));

    [Fact]
    public void Switch_DrawsTheBladeInThePositionItIsSetTo_AndCachesEach()
    {
        var on  = BuiltInSymbols.PrimitivesForSwitch(SwitchState.On);
        var off = BuiltInSymbols.PrimitivesForSwitch(SwitchState.Off);

        Assert.NotEqual(LineKeys(on), LineKeys(off));
        Assert.Same(on,  BuiltInSymbols.PrimitivesForSwitch(SwitchState.On));
        Assert.Same(off, BuiltInSymbols.PrimitivesForSwitch(SwitchState.Off));

        // Closed: every line is horizontal, so the two contacts are joined along the signal path.
        // Open: the blade is lifted clear of it, which is the only thing that says "off".
        Assert.All(on.Primitives.OfType<LinePrimitive>(), l => Assert.Equal(l.Y1, l.Y2));
        Assert.Contains(off.Primitives.OfType<LinePrimitive>(), l => l.Y1 != l.Y2);
    }

    [Fact]
    public void SwitchD_PointsTheBladeAtTheThrowItIsSetTo_AndCachesEach()
    {
        var t1 = BuiltInSymbols.PrimitivesForSwitchD(SwitchThrow.T1);
        var t2 = BuiltInSymbols.PrimitivesForSwitchD(SwitchThrow.T2);

        Assert.NotEqual(LineKeys(t1), LineKeys(t2));
        Assert.Same(t1, BuiltInSymbols.PrimitivesForSwitchD(SwitchThrow.T1));
        Assert.Same(t2, BuiltInSymbols.PrimitivesForSwitchD(SwitchThrow.T2));

        // The blade starts at the common contact and rises to throw 1 or falls to throw 2.
        var b1 = Assert.Single(t1.Primitives.OfType<LinePrimitive>().Where(l => l.Y1 != l.Y2));
        var b2 = Assert.Single(t2.Primitives.OfType<LinePrimitive>().Where(l => l.Y1 != l.Y2));
        Assert.Equal(-100.0, b1.Y2);
        Assert.Equal( 100.0, b2.Y2);
    }

    private static string LineKeys(Symbol s)
        => string.Join("|", s.Primitives.OfType<LinePrimitive>()
                             .Select(l => $"{l.X1},{l.Y1},{l.X2},{l.Y2}"));

    // ── The SPDT's State is written "1" and "2", and that is a trap worth pinning ──
    //
    // Enum.TryParse resolves a bare numeral against the UNDERLYING value, so an enum numbered from
    // zero would silently read "1" as the SECOND throw: a switch drawn in the wrong position, with
    // nothing to see and nothing to fail. SwitchThrow is numbered from one for exactly this reason.

    [Theory]
    [InlineData("1",  SwitchThrow.T1)]
    [InlineData("2",  SwitchThrow.T2)]
    [InlineData("T1", SwitchThrow.T1)]
    [InlineData("T2", SwitchThrow.T2)]
    public void SwitchD_StateParsesTheNumeralsTheGlyphItselfLabels(string text, SwitchThrow expected)
    {
        Assert.True(System.Enum.TryParse<SwitchThrow>(text, ignoreCase: true, out var got));
        Assert.Equal(expected, got);
    }

    // ══ The filter IS the match, by construction ══════════════════════════════
    //
    // Owner decision, 2026-08-31: the same picture, not a related one. It is built by reusing
    // Match's own primitive list rather than by copying its geometry, so the two are identical by
    // construction and cannot drift apart when either is next touched. All three forms are asserted,
    // because a copy that happens to agree on the default form is the failure this catches.

    [Theory]
    [InlineData(NetworkForm.Lowpass)]
    [InlineData(NetworkForm.Bandpass)]
    [InlineData(NetworkForm.Highpass)]
    public void Filter_PrimitivesAreElementForElementMatchsOwn(NetworkForm form)
    {
        var filter = BuiltInSymbols.PrimitivesForFilter(form);
        var match  = BuiltInSymbols.PrimitivesForMatch(form, 1);

        Assert.Equal(match.Primitives.Count, filter.Primitives.Count);
        for (int i = 0; i < match.Primitives.Count; i++)
            Assert.Same(match.Primitives[i], filter.Primitives[i]);

        // Same picture, its OWN pins: a Filter is a Filter, and Sym re-derives the pin list per kind.
        Assert.Equal(match.Pins.Select(p => (p.LocalX, p.LocalY)),
                     filter.Pins.Select(p => (p.LocalX, p.LocalY)));
    }

    // Reuse must not disturb what it reuses. Match's own list is the cached one it always was, and
    // building every Filter variant does not add to it, reorder it or replace it.
    [Fact]
    public void BuildingTheFilterLeavesMatchsOwnPrimitivesUntouched()
    {
        var before = BuiltInSymbols.PrimitivesForMatch(NetworkForm.Bandpass, 1);
        var snapshot = before.Primitives.ToList();

        foreach (var f in new[] { NetworkForm.Lowpass, NetworkForm.Bandpass, NetworkForm.Highpass })
            _ = BuiltInSymbols.PrimitivesForFilter(f);

        var after = BuiltInSymbols.PrimitivesForMatch(NetworkForm.Bandpass, 1);
        Assert.Same(before, after);
        Assert.Equal(snapshot, after.Primitives);

        // And the shared LIST is not shared — a Filter holds its own, so a future in-place edit of
        // one cannot silently rewrite the other.
        Assert.NotSame(after.Primitives,
                       BuiltInSymbols.PrimitivesForFilter(NetworkForm.Bandpass).Primitives);
    }

    [Fact]
    public void Filter_DrawsADifferentStackPerForm_AndCachesEach()
    {
        var lp = BuiltInSymbols.PrimitivesForFilter(NetworkForm.Lowpass);
        var bp = BuiltInSymbols.PrimitivesForFilter(NetworkForm.Bandpass);
        var hp = BuiltInSymbols.PrimitivesForFilter(NetworkForm.Highpass);

        Assert.NotEqual(LineKeys(lp), LineKeys(bp));
        Assert.NotEqual(LineKeys(bp), LineKeys(hp));
        Assert.NotEqual(LineKeys(lp), LineKeys(hp));
        Assert.Same(lp, BuiltInSymbols.PrimitivesForFilter(NetworkForm.Lowpass));
    }

    // ══ Registry ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(SymbolKind.Balun,      "Balun")]
    [InlineData(SymbolKind.Circulator, "Circulator")]
    [InlineData(SymbolKind.Switch,     "Switch")]
    [InlineData(SymbolKind.SwitchD,    "Switch")]
    [InlineData(SymbolKind.Amp,        "Amp")]
    [InlineData(SymbolKind.Coupler,    "Coupler")]
    [InlineData(SymbolKind.Hybrid90,   "Coupler")]
    [InlineData(SymbolKind.Hybrid180,  "Coupler")]
    [InlineData(SymbolKind.Filter,     "Filter")]
    [InlineData(SymbolKind.Atten,      "Atten")]
    [InlineData(SymbolKind.Duplexer,   "Duplexer")]
    public void EachTileNamesItsEngineComponent(SymbolKind kind, string expected)
        => Assert.Equal(expected, ComponentTypeRegistry.EngineReference(kind));

    // Swapping SPST for SPDT, or a coupler for either hybrid, must not renumber a schematic — so the
    // tiles that share an engine component share an instance prefix too, the mixer's own reason.
    [Fact]
    public void TilesOverOneEngineComponentShareOneInstancePrefix()
    {
        Assert.Equal("SW", ComponentTypeRegistry.Get(SymbolKind.Switch).InstancePrefix);
        Assert.Equal("SW", ComponentTypeRegistry.Get(SymbolKind.SwitchD).InstancePrefix);
        Assert.Equal("HYB", ComponentTypeRegistry.Get(SymbolKind.Hybrid90).InstancePrefix);
        Assert.Equal("HYB", ComponentTypeRegistry.Get(SymbolKind.Hybrid180).InstancePrefix);
    }

    // A tile's DISPLAY NAME is the component's own word, not its abbreviation (owner, 2026-08-31).
    // The abbreviation is what a user TYPES — the instance prefix and the short type code, both
    // unchanged — and the display name is what they READ, on the palette tile and under the symbol.
    // Pinned here because it is the sort of thing a later "tidy" shortens back.
    [Theory]
    [InlineData(SymbolKind.Balun,      "Balun")]
    [InlineData(SymbolKind.Circulator, "Circulator")]
    [InlineData(SymbolKind.Switch,     "Switch")]
    [InlineData(SymbolKind.SwitchD,    "SwitchD")]
    [InlineData(SymbolKind.Amp,        "Amp")]
    [InlineData(SymbolKind.Coupler,    "Directional Coupler")]
    [InlineData(SymbolKind.Hybrid90,   "Hybrid90")]
    [InlineData(SymbolKind.Hybrid180,  "Hybrid180")]
    [InlineData(SymbolKind.Filter,     "Filter")]
    [InlineData(SymbolKind.Atten,      "Attenuator")]
    [InlineData(SymbolKind.Duplexer,   "Duplexer")]
    public void EachTileIsNamedForWhatItIs_NotForItsAbbreviation(SymbolKind kind, string expected)
        => Assert.Equal(expected, ComponentTypeRegistry.DisplayName(kind));

    [Theory]
    [InlineData("BALUN",  SymbolKind.Balun)]
    [InlineData("circ",   SymbolKind.Circulator)]
    [InlineData("SW",     SymbolKind.Switch)]
    [InlineData("swd",    SymbolKind.SwitchD)]
    [InlineData("AMP",    SymbolKind.Amp)]
    [InlineData("cpl",    SymbolKind.Coupler)]
    [InlineData("HYB",    SymbolKind.Hybrid90)]
    [InlineData("hyb90",  SymbolKind.Hybrid90)]
    [InlineData("HYB180", SymbolKind.Hybrid180)]
    [InlineData("flt",    SymbolKind.Filter)]
    [InlineData("ATT",    SymbolKind.Atten)]
    [InlineData("dpx",    SymbolKind.Duplexer)]
    // …and each one also answers to the name on its own tile, because that is the other spelling a
    // user has actually seen. Making someone learn that "Attenuator" is typed "ATT" buys nothing.
    [InlineData("Circulator", SymbolKind.Circulator)]
    [InlineData("switch",     SymbolKind.Switch)]
    [InlineData("SwitchD",    SymbolKind.SwitchD)]
    [InlineData("Coupler",    SymbolKind.Coupler)]
    [InlineData("directional coupler", SymbolKind.Coupler)]
    [InlineData("Hybrid90",   SymbolKind.Hybrid90)]
    [InlineData("Hybrid180",  SymbolKind.Hybrid180)]
    [InlineData("Filter",     SymbolKind.Filter)]
    [InlineData("attenuator", SymbolKind.Atten)]
    [InlineData("Duplexer",   SymbolKind.Duplexer)]
    public void TryParseCode_ResolvesEveryShortCode(string code, SymbolKind expected)
    {
        Assert.True(ComponentTypeRegistry.TryParseCode(code, out var kind, out _));
        Assert.Equal(expected, kind);
    }

    // Most of these ARE read by an engine now (brief-sys-2, brief-sys-3); the rest are still the
    // handful the GLYPH reads, plus the number the amplifier's artwork was specified around. Every
    // one of them must carry a meaning either way, or its row in the generated parameter table
    // would be blank.
    [Theory]
    [MemberData(nameof(EachBlock))]
    public void EveryDeclaredParameterCarriesAMeaning(SymbolKind kind)
    {
        foreach (var p in ComponentTypeRegistry.DefaultParameters(kind, 0))
            Assert.False(string.IsNullOrWhiteSpace(ComponentTypeRegistry.ParameterDescription(kind, p.Name)),
                $"{kind}.{p.Name} has no description, so its row in the generated table would be blank");
    }

    [Fact]
    public void TheGlyphSelectorsAreHiddenFromTheSchematic_BecauseTheGlyphAlreadySaysThem()
    {
        foreach (var (kind, name) in new[]
                 {
                     (SymbolKind.Circulator, "Direction"), (SymbolKind.Switch, "State"),
                     (SymbolKind.SwitchD, "State"), (SymbolKind.Filter, "Form"),
                 })
        {
            var p = Assert.Single(ComponentTypeRegistry.DefaultParameters(kind, 0), q => q.Name == name);
            Assert.False(p.ShowOnSchematic,
                $"{kind}.{name} is drawn INTO the symbol; captioning it as well says the same thing twice");
        }
    }

    [Fact]
    public void TheAmplifiersGainAndTheAttenuatorsLossShowOnTheSchematic()
    {
        // The amplifier carries its electrical parameters now too (brief-sys-5), and TWO of them
        // show: the triangle is drawn empty, and gain and intercept are the pair a system diagram is
        // read for. Neither is in the picture, so neither can be left to the properties panel.
        foreach (var p in ComponentTypeRegistry.DefaultParameters(SymbolKind.Amp, 0))
            Assert.Equal(p.Name is "Gain" or "IP3", p.ShowOnSchematic);

        // The attenuator carries its electrical parameters as well now (brief-sys-2), so this is
        // the one that shows rather than the only one there is — and it stays the only one that
        // shows, because the bowtie is drawn empty around it.
        foreach (var p in ComponentTypeRegistry.DefaultParameters(SymbolKind.Atten, 0))
            Assert.Equal(p.Name == "Loss", p.ShowOnSchematic);
    }

    // ══ Extraction ════════════════════════════════════════════════════════════
    //
    // Every tile shows N pins for a model declaring N ground-referenced PORTS — 2N nets, each port's
    // − tied to "0". One rule, one branch, one set of kinds; MixerD is the deliberate non-member,
    // and asserting that here is what stops a later edit folding it in by accident.

    [Theory]
    [MemberData(nameof(EachBlock))]
    public void EachTileExtractsTwoNetsPerPin_WithEveryReturnTiedToGround(SymbolKind kind)
    {
        var inst = ExtractSingle(kind);
        int pins = SymbolPortDefs.For(kind).Length;

        Assert.Equal(2 * pins, inst.NetBindings.Count);
        for (int i = 0; i < pins; i++)
        {
            Assert.Equal("0", inst.NetBindings[2 * i + 1]);
            Assert.NotEqual("0", inst.NetBindings[2 * i]);
        }
        // The signal nets are distinct, or the block would be shorted onto itself.
        var signal = Enumerable.Range(0, pins).Select(i => inst.NetBindings[2 * i]).ToList();
        Assert.Equal(pins, signal.Distinct().Count());
    }

    [Fact]
    public void MixerD_IsStillNotAGroundReferencedBlock()
    {
        var inst = ExtractSingle(SymbolKind.MixerD);
        Assert.DoesNotContain("0", inst.NetBindings);
    }

    // ══ Round trip ════════════════════════════════════════════════════════════

    // One of each, placed on a grid, saved and reloaded. Geometry AND parameters: a SymbolKind is
    // persisted ORDINALLY by .csch, so an enum member inserted anywhere but the end silently
    // renames every kind after it — this is the test that would catch it.
    [Fact]
    public void OneOfEachTile_SavesAndReloadsWithIdenticalGeometryAndParameters()
    {
        var model = new SchematicEditModel();
        for (int i = 0; i < SystemBlocks.Length; i++)
            model.Components.Add(Place(SystemBlocks[i], $"X{i + 1}", i * 1000, 0));

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csch");
        try
        {
            SchematicPersistence.SaveToFile(path, model);
            var (loaded, _, _) = SchematicPersistence.LoadFromFile(path);

            Assert.Equal(SystemBlocks, loaded.Components.Select(c => c.Symbol));
            Assert.DoesNotContain(SymbolKind.Unknown, loaded.Components.Select(c => c.Symbol));

            for (int i = 0; i < SystemBlocks.Length; i++)
            {
                var (a, b) = (model.Components[i], loaded.Components[i]);
                Assert.Equal(a.InstanceName, b.InstanceName);
                Assert.Equal((a.X, a.Y), (b.X, b.Y));
                Assert.Equal(a.Parameters.Select(p => (p.Name, p.Expression, p.Unit, p.ShowOnSchematic)),
                             b.Parameters.Select(p => (p.Name, p.Expression, p.Unit, p.ShowOnSchematic)));
                Assert.Equal(SymbolPortDefs.For(a.Symbol), SymbolPortDefs.For(b.Symbol));
            }
        }
        finally { File.Delete(path); }
    }

    // A dynamic glyph is chosen from an instance PARAMETER, so it has to survive the round trip too
    // — a reloaded switch that draws itself closed when the file says Off is a lie the file cannot
    // correct.
    [Fact]
    public void ADynamicGlyphsSelectorSurvivesTheRoundTrip()
    {
        var model = new SchematicEditModel();
        var sw = Place(SymbolKind.Switch, "SW1", 0, 0);
        sw.Parameters.Single(p => p.Name == "State").Expression = "Off";
        var circ = Place(SymbolKind.Circulator, "CIRC1", 1000, 0);
        circ.Parameters.Single(p => p.Name == "Direction").Expression = "CCW";
        model.Components.Add(sw);
        model.Components.Add(circ);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csch");
        try
        {
            SchematicPersistence.SaveToFile(path, model);
            var (loaded, _, _) = SchematicPersistence.LoadFromFile(path);

            Assert.Equal("Off", loaded.Components[0].Parameters.Single(p => p.Name == "State").Expression);
            Assert.Equal("CCW", loaded.Components[1].Parameters.Single(p => p.Name == "Direction").Expression);

            // …and the reloaded instance draws the variant the file names, not the default.
            Assert.Same(BuiltInSymbols.PrimitivesForSwitch(SwitchState.Off),
                        loaded.Components[0].ToRenderComponent().InstanceSymbol);
            Assert.Same(BuiltInSymbols.PrimitivesForCirculator(CirculatorDirection.CCW),
                        loaded.Components[1].ToRenderComponent().InstanceSymbol);
        }
        finally { File.Delete(path); }
    }

    // ══ Palette ═══════════════════════════════════════════════════════════════

    [Fact]
    public void SystemListsExactlyTheElevenBlocksPlusTheTwoMixerTiles()
    {
        var system = LibraryCatalog.ByCategory(ComponentCategory.System);

        Assert.Equal(SystemBlocks.Length + 2, system.Count);
        foreach (var kind in SystemBlocks)
            Assert.Single(system, i => i.Kind == kind);

        // The two mixer tiles join as an EXTRA membership and keep Devices as their primary: a mixer
        // is a device you put in the signal path, and it is also a block you draw a system out of.
        foreach (var kind in new[] { SymbolKind.Mixer, SymbolKind.MixerD })
        {
            Assert.Single(system, i => i.Kind == kind);
            Assert.Contains(LibraryCatalog.ByCategory(ComponentCategory.Devices), i => i.Kind == kind);
            Assert.Equal(ComponentCategory.Devices, ComponentTypeRegistry.Get(kind).Category);
        }
    }

    [Theory]
    [MemberData(nameof(EachBlock))]
    public void EachBlockAppearsInTheFullListExactlyOnce_UnderSystem(SymbolKind kind)
    {
        var item = Assert.Single(LibraryCatalog.AllItems, i => i.Kind == kind);
        Assert.Equal(ComponentCategory.System, item.Category);
    }

    [Fact]
    public void SystemIsOfferedByThePalette_DirectlyAfterDevices()
    {
        var names = new CircuitRF.Ui.ViewModels.Dock.PaletteTool()
            .Categories.Select(c => c.DisplayName).ToList();
        int devices = names.IndexOf("Devices");
        Assert.True(devices >= 0);
        Assert.Equal("System", names[devices + 1]);
    }

    [Theory]
    [InlineData("balun",      SymbolKind.Balun)]
    [InlineData("isolator",   SymbolKind.Circulator)]
    [InlineData("SPDT",       SymbolKind.SwitchD)]
    [InlineData("attenuator", SymbolKind.Atten)]
    [InlineData("rat race",   SymbolKind.Hybrid180)]
    [InlineData("duplexer",   SymbolKind.Duplexer)]
    public void EachBlockIsFoundByTheWordAUserWouldSearchFor(string query, SymbolKind expected)
        => Assert.Contains(LibraryCatalog.Search(query), i => i.Kind == expected);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EditableComponent Place(SymbolKind kind, string name, double x, double y)
    {
        var comp = new EditableComponent { InstanceName = name, Symbol = kind, X = x, Y = y };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        return comp;
    }

    private static CircuitRF.Core.Design.Instance ExtractSingle(SymbolKind kind)
    {
        var model = new SchematicEditModel();
        model.Components.Add(Place(kind, "X1", 0, 0));
        return Assert.Single(NetExtractor.Extract(model).TestBench.Instances);
    }
}
