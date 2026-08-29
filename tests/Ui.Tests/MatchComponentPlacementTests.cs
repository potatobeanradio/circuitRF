using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Matching;
using CircuitRF.Engine;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// MN-2's UI-side gates (match.md §8.4): the symbol, the registry entry, the new Matching category,
/// and the one claim that ties them together — <b>a freshly placed Match simulates with no edits</b>.
/// </summary>
public class MatchComponentPlacementTests(ITestOutputHelper output)
{
    // ── placement ─────────────────────────────────────────────────────────────

    /// <summary>Exactly what the placement path produces: a component seeded from the registry's
    /// default parameters and nothing else.</summary>
    private static EditableComponent PlaceFresh(string name, double x, double y)
    {
        var comp = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Match, X = x, Y = y };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name,
                Expression = dp.Expression,
                Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic,
            });
        return comp;
    }

    private static EditableComponent Term(int num, double x, double y, double z = 50.0)
    {
        var comp = new EditableComponent { InstanceName = $"T{num}", Symbol = SymbolKind.Term, X = x, Y = y };
        comp.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
        comp.Parameters.Add(new EditableParameter
        {
            Name = "Z",
            Expression = z.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        return comp;
    }

    /// <summary>
    /// The headline claim of §2: drop a <c>Match</c>, wire it up, Run. No Designer, no edits, no
    /// hand-written <c>Design</c> — which matters because until MN-3 there is no way to give one.
    /// </summary>
    [Fact]
    public void AFreshlyPlacedMatch_RunsAOnePortSParameterSweep()
    {
        // Match at the origin: pins (-200,0) and (+200,0). Term "+" is at (0,-200) local, so a Term
        // centred at (-200,200) lands its + pin exactly on the Match's left pin; the Match's right
        // pin is grounded, making this a 1-port.
        var model = new SchematicEditModel();
        model.Components.Add(PlaceFresh("MN1", 0, 0));
        model.Components.Add(Term(1, -200, 200));
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Ground, X = -200, Y = 400 });
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Ground, X = 200, Y = 0 });

        var testBench = NetExtractor.Extract(model).TestBench;
        var netlist = new Elaborator().Elaborate(testBench);

        var placed = netlist.Components.Single(c => c.InstancePath == "MN1");
        var mm = Assert.IsType<MatchModel>(placed.Model);
        Assert.Equal(1.8e9, mm.Design.F1);
        Assert.Equal(2.2e9, mm.Design.F2);

        double[] freqs = [1.0e9, 1.8e9, 2.0e9, 2.2e9, 3.0e9];
        var s = SParameterEngine.Run(netlist, freqs)["S"];

        for (int f = 0; f < freqs.Length; f++)
        {
            double mag = ((Complex)s[f, 0, 0]).Magnitude;
            output.WriteLine($"{freqs[f] / 1e9:F1} GHz  |S11| = {mag:F9}");

            // Every element is lossless and port 2 is a short, so the 1-port reflects everything.
            // A stamp with a sign error on a reactance shows up here as |S11| > 1.
            Assert.Equal(1.0, mag, 1e-9);
        }
    }

    /// <summary>
    /// The same component between two Terms is the bandpass its design describes — flat in
    /// 1.8-2.2 GHz at the 0.1 dB design ripple, well down an octave either side. This is what makes
    /// the default a REAL design rather than merely a decodable one.
    /// </summary>
    /// <remarks>
    /// <b>Port 2 is terminated in 10 Ω, not 50</b>: the shipped default transforms 50 Ω down to 10 Ω
    /// (2026-08-19), which is the whole point of it being a matching network rather than a filter.
    /// Measuring it into 50 Ω would measure the mismatch this component exists to remove.
    /// </remarks>
    [Fact]
    public void AFreshlyPlacedMatch_IsTheBandpassItsDefaultDesignDescribes()
    {
        var model = new SchematicEditModel();
        model.Components.Add(PlaceFresh("MN1", 0, 0));
        model.Components.Add(Term(1, -200, 200));
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Ground, X = -200, Y = 400 });
        model.Components.Add(Term(2, 200, 200, z: 10.0));
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Ground, X = 200, Y = 400 });

        var netlist = new Elaborator().Elaborate(NetExtractor.Extract(model).TestBench);
        double[] freqs = [1.0e9, 1.8e9, 2.0e9, 2.2e9, 4.0e9];
        var s = SParameterEngine.Run(netlist, freqs)["S"];

        double Db(int f) => 20.0 * Math.Log10(((Complex)s[f, 1, 0]).Magnitude);
        for (int f = 0; f < freqs.Length; f++)
            output.WriteLine($"{freqs[f] / 1e9:F1} GHz  |S21| = {Db(f):F3} dB");

        Assert.True(Db(1) > -0.11, $"1.8 GHz is the lower band edge: {Db(1):F3} dB");
        Assert.True(Db(2) > -0.11, $"2.0 GHz is mid-band: {Db(2):F3} dB");
        Assert.True(Db(3) > -0.11, $"2.2 GHz is the upper band edge: {Db(3):F3} dB");
        Assert.True(Db(0) < -20.0, $"1.0 GHz is well below the band: {Db(0):F3} dB");
        Assert.True(Db(4) < -20.0, $"4.0 GHz is well above the band: {Db(4):F3} dB");
    }

    /// <summary>
    /// The design survives the netlist FILE — the path File ▸ Export Netlist takes, and the one the
    /// in-memory tests above cannot see.
    ///
    /// <para><c>CnlWriter</c> writes <c>Design=&lt;base64&gt;</c> unquoted, and <c>CnlReader</c>'s
    /// spaced-assignment merge treats a token ENDING in <c>=</c> as an empty assignment and glues the
    /// next token on as its value. A padded payload followed by any other parameter on the same
    /// instance line therefore arrives as one run-on string and decodes to nothing — which is exactly
    /// why <c>MatchEmbedding.Encode</c> strips the padding, and why a placed Match writes its echo
    /// parameters after <c>Design</c> on that same line.</para>
    /// </summary>
    [Fact]
    public void TheDesignSurvivesAnExportedNetlist()
    {
        var model = new SchematicEditModel();
        model.Components.Add(PlaceFresh("MN1", 0, 0));
        model.Components.Add(Term(1, -200, 200));
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Ground, X = -200, Y = 400 });
        model.Components.Add(Term(2, 200, 200));
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Ground, X = 200, Y = 400 });

        var extracted = NetExtractor.Extract(model);
        string cnl = CircuitRF.Core.Netlist.CnlWriter.Write(extracted.TestBench, extracted.Library, "test");
        output.WriteLine(cnl);

        // The instance line carries the payload AND the echoes, in that order — the arrangement the
        // merge rule would break on a padded payload.
        string line = cnl.Split('\n').Single(l => l.TrimStart().StartsWith("Match:", StringComparison.Ordinal));
        Assert.Contains("Design=", line, StringComparison.Ordinal);
        Assert.Contains("F1=", line, StringComparison.Ordinal);

        var (lib, tb) = new CircuitRF.Core.Netlist.CnlReader().Read(cnl);
        var netlist = new Elaborator(lib).Elaborate(tb);
        var mm = Assert.IsType<MatchModel>(netlist.Components.Single(c => c.InstancePath == "MN1").Model);
        Assert.Equal(1.8e9, mm.Design.F1);
        Assert.Equal(9, mm.StampedElements.Count);
    }

    // ── §4: registry, category, palette ───────────────────────────────────────

    /// <summary>The prefix is <c>MN</c> and NOT <c>M</c> — <c>M</c> is <see cref="SymbolKind.Mutual"/>,
    /// and two kinds sharing a prefix hand out colliding instance names.</summary>
    [Fact]
    public void ThePrefixIsMn_AndDoesNotCollideWithMutual()
    {
        Assert.Equal("MN", ComponentTypeRegistry.InstancePrefix(SymbolKind.Match));
        Assert.Equal("M", ComponentTypeRegistry.InstancePrefix(SymbolKind.Mutual));
        Assert.Equal("Match", ComponentTypeRegistry.EngineReference(SymbolKind.Match));
    }

    /// <summary>Auto-naming counts up from <c>MN1</c>, and is not confused by a <c>Mutual</c> beside
    /// it — the concrete reason the prefix is two letters.</summary>
    [Fact]
    public void AutoNaming_GivesMn1ThenMn2()
    {
        var existing = new System.Collections.Generic.List<EditableComponent>();
        string first = SchematicEditModel.NextAvailableName(existing, SymbolKind.Match);
        Assert.Equal("MN1", first);

        existing.Add(new EditableComponent { InstanceName = first, Symbol = SymbolKind.Match });
        existing.Add(new EditableComponent { InstanceName = "M1", Symbol = SymbolKind.Mutual });
        Assert.Equal("MN2", SchematicEditModel.NextAvailableName(existing, SymbolKind.Match));
        Assert.Equal("M2", SchematicEditModel.NextAvailableName(existing, SymbolKind.Mutual));
    }

    /// <summary>It is in the palette, under the new Matching category, and findable by what it
    /// SOLVES as well as by how it works.</summary>
    [Fact]
    public void MatchIsInThePalette_UnderTheMatchingCategory()
    {
        var item = Assert.Single(LibraryCatalog.AllItems, i => i.Kind == SymbolKind.Match);
        Assert.Equal("Match", item.DisplayName);
        Assert.Equal(ComponentCategory.Matching, item.Category);
        Assert.True(item.IsCommon);

        Assert.Contains(LibraryCatalog.ByCategory(ComponentCategory.Matching),
                        i => i.Kind == SymbolKind.Match);
        Assert.Contains(LibraryCatalog.AllItemsPinnedOrder(), i => i.Kind == SymbolKind.Match);

        foreach (string query in new[] { "matching", "Chebyshev", "Fano", "interstage", "Cgs", "bandpass" })
            Assert.Contains(LibraryCatalog.Search(query), i => i.Kind == SymbolKind.Match);
    }

    /// <summary>
    /// A new <see cref="ComponentCategory"/> is only real if every one of the four places that knows
    /// about categories was updated — the enum, the sort key, the catalog and the picker. Missing the
    /// picker leaves a category nothing can be filtered to; missing the sort key files it under the
    /// catch-all with <c>Other</c>.
    /// </summary>
    [Fact]
    public void TheMatchingCategory_ReachesThePicker_BetweenMicrostripAndDataFiles()
    {
        var tool = new PaletteTool();
        var names = tool.Categories.Select(c => c.DisplayName).ToList();
        output.WriteLine(string.Join(" | ", names));

        // "Matching" is one word, so PaletteTool.RealDisplayName needs no entry and the name falls
        // through to ToString() — asserted here rather than assumed.
        Assert.Contains("Matching", names);
        Assert.True(names.IndexOf("Microstrip") < names.IndexOf("Matching"));
        Assert.True(names.IndexOf("Matching") < names.IndexOf("Data Files"));

        // AllItems is sorted by CategorySortKey, so the same order has to hold there — an unmapped
        // category would fall to the catch-all rank and sort with Other instead.
        var order = LibraryCatalog.AllItems.Select(i => i.Category).ToList();
        Assert.True(order.LastIndexOf(ComponentCategory.Microstrip) < order.IndexOf(ComponentCategory.Matching));
        Assert.True(order.LastIndexOf(ComponentCategory.Matching) < order.IndexOf(ComponentCategory.DataFiles));
    }

    /// <summary><c>Design</c> is base64 of the whole design and must never render as a text row; the
    /// echo parameters must, because they are the only description of the network a user has.</summary>
    /// <summary>
    /// <b>A Match offers no generic parameter rows at all</b> (owner, 2026-08-28) — the payload
    /// because nobody can read it, the echoes because nothing reads them back and the compact panel
    /// already states every one of them in words. A name the registry does not declare is not the
    /// Match's own and still gets a row.
    /// </summary>
    [Fact]
    public void NoParameterOfAMatch_IsAGenericParameterRow()
    {
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            Assert.True(ParameterEditorViewModel.IsMatchPanelParameter(dp.Name), dp.Name);

        Assert.False(ParameterEditorViewModel.IsMatchPanelParameter("Temp"));
    }

    /// <summary>
    /// The other half of the same rule: none of them is drawn beside the symbol either — not on a
    /// freshly placed instance (the registry's own defaults), and not on one placed before the change
    /// whose file still says <c>ShowOnSchematic = true</c>.
    /// </summary>
    [Fact]
    public void AMatchDrawsNoParameterLabels()
    {
        var comp = PlaceFresh("MN1", 0, 0);
        Assert.All(comp.Parameters, p => Assert.False(p.ShowOnSchematic, p.Name));
        Assert.Empty(comp.LabelParameters());

        // The legacy instance: three of them were true when the file was written.
        foreach (string name in new[] { "F1", "F2", "Order" })
            comp.Parameters.Single(p => p.Name == name).ShowOnSchematic = true;
        Assert.Empty(comp.LabelParameters());

        // Type and instance name still label it — only the PARAMETERS are gone.
        var rendered = comp.ToRenderComponent();
        Assert.Equal(new[] { "Match", "MN1" }, rendered.Labels);
    }

    // ── §3: the symbol ────────────────────────────────────────────────────────

    /// <summary>Three stacked full-cycle sines, two slashes, a body and two pins.</summary>
    [Fact]
    public void TheSymbolIsTheStandardBandpassGlyph()
    {
        var sym = BuiltInSymbols.Primitives(SymbolKind.Match);

        var sines = sym.Primitives.OfType<SinePrimitive>().OrderBy(s => s.Cy).ToList();
        Assert.Equal(3, sines.Count);
        Assert.All(sines, s => Assert.Equal(1.0, s.Cycles));
        Assert.All(sines, s => Assert.Equal(SineAxis.Horizontal, s.Axis));
        Assert.All(sines, s => Assert.Equal(sines[0].Length, s.Length));

        // Stacked and not touching: the gap between neighbouring centres must exceed the peak-to-peak
        // amplitude, or the three waves read as one smear.
        Assert.True(sines[1].Cy - sines[0].Cy > 2 * sines[0].Amp);
        Assert.True(sines[2].Cy - sines[1].Cy > 2 * sines[1].Amp);

        var body = Assert.Single(sym.Primitives.OfType<RoundedRectPrimitive>());
        Assert.False(body.Filled);

        Assert.Equal(2, sym.Pins.Count);
        Assert.Equal((-200.0, 0.0), (sym.Pins[0].LocalX, sym.Pins[0].LocalY));
        Assert.Equal((200.0, 0.0), (sym.Pins[1].LocalX, sym.Pins[1].LocalY));
        Assert.All(sym.Pins, p => Assert.Equal(0.0, p.LocalX % 100));
    }

    /// <summary>
    /// <b>The strikethroughs actually strike, at every orientation.</b> The waves are
    /// <c>SinePrimitive</c>s, which know how to rotate; the slashes are plain lines, and a slash
    /// drawn so that it only LOOKS like a strikethrough at 0° is the specific mistake worth a test.
    /// Checked by intersecting the real geometry — sampled wave against slash segment — after running
    /// both through the same <see cref="SchematicGeometry.LocalToWorld"/> the renderer uses.
    /// </summary>
    [Theory]
    [InlineData(SymbolRotation.R0, false)]
    [InlineData(SymbolRotation.R90, false)]
    [InlineData(SymbolRotation.R180, false)]
    [InlineData(SymbolRotation.R270, false)]
    [InlineData(SymbolRotation.R0, true)]
    [InlineData(SymbolRotation.R90, true)]
    [InlineData(SymbolRotation.R180, true)]
    [InlineData(SymbolRotation.R270, true)]
    public void TheSlashesCrossTheOuterWaves_AndNotTheMiddleOne(SymbolRotation rotation, bool mirrored)
    {
        var sym = BuiltInSymbols.Primitives(SymbolKind.Match);
        var sines = sym.Primitives.OfType<SinePrimitive>().OrderBy(s => s.Cy).ToList();
        var slashes = Slashes(sym);
        Assert.Equal(2, slashes.Count);

        Assert.True(slashes.Any(s => Crosses(sines[0], s, rotation, mirrored)),
            "the top wave must be struck through");
        Assert.True(slashes.Any(s => Crosses(sines[2], s, rotation, mirrored)),
            "the bottom wave must be struck through");
        Assert.False(slashes.Any(s => Crosses(sines[1], s, rotation, mirrored)),
            "the passband wave must be left unstruck — a slash across it inverts the glyph's meaning");
    }

    // ── the form- and band-dependent glyph (match.md §8.4) ────────────────────

    /// <summary>
    /// <b>Which waves carry a slash is the whole content of the glyph.</b> The three waves read as a
    /// frequency axis with the highest at the top, so a bandpass strikes the outer two, a lowpass the
    /// top two and a highpass the bottom two — and the check is geometric, on the real primitives,
    /// because a slash that is merely NEAR a wave is not a strikethrough.
    /// </summary>
    [Theory]
    [InlineData(NetworkForm.Bandpass, true,  false, true)]
    [InlineData(NetworkForm.Lowpass,  true,  true,  false)]
    [InlineData(NetworkForm.Highpass, false, true,  true)]
    public void TheStruckWavesFollowTheNetworkForm(NetworkForm form, bool top, bool middle, bool bottom)
    {
        var sym = BuiltInSymbols.PrimitivesForMatch(form, 1);
        var sines = sym.Primitives.OfType<SinePrimitive>().OrderBy(s => s.Cy).ToList();
        Assert.Equal(3, sines.Count);

        var slashes = Slashes(sym);
        Assert.Equal(2, slashes.Count);

        foreach (var (wave, expected, which) in new[]
                 {
                     (sines[0], top,    "top"),
                     (sines[1], middle, "middle"),
                     (sines[2], bottom, "bottom"),
                 })
        {
            bool struck = slashes.Any(sl => Crosses(wave, sl, SymbolRotation.R0, mirrored: false));
            Assert.True(struck == expected,
                $"{form}: the {which} wave should {(expected ? "" : "NOT ")}be struck through");
        }
    }

    /// <summary>The two forms that are not bandpass must not merely re-use its glyph.</summary>
    [Fact]
    public void TheThreeFormsAreThreeDifferentGlyphs()
    {
        var forms = new[] { NetworkForm.Bandpass, NetworkForm.Lowpass, NetworkForm.Highpass };
        var keys = forms.Select(f => string.Join("|", Slashes(BuiltInSymbols.PrimitivesForMatch(f, 1))
                                                      .Select(l => $"{l.Y1:F3},{l.Y2:F3}")
                                                      .OrderBy(t => t, StringComparer.Ordinal)))
                        .ToList();
        Assert.Equal(3, keys.Distinct().Count());
    }

    /// <summary>
    /// A multiband design is bandpass in EVERY band, so it is drawn as one smaller bandpass stack per
    /// band — two side by side, three as two-below-one — rather than as one stack that cannot say how
    /// many passbands there are. Every stack is a real bandpass stack (three waves, outer two struck),
    /// every one is smaller than the single-band stack, and none of them leaves the body.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void AMultibandMatchDrawsOneSmallerBandpassStackPerBand(int bands)
    {
        var single = BuiltInSymbols.PrimitivesForMatch(NetworkForm.Bandpass, 1);
        var sym    = BuiltInSymbols.PrimitivesForMatch(NetworkForm.Bandpass, bands);

        var sines = sym.Primitives.OfType<SinePrimitive>().ToList();
        Assert.Equal(3 * bands, sines.Count);
        Assert.Equal(2 * bands, Slashes(sym).Count);

        double full = single.Primitives.OfType<SinePrimitive>().First().Length;
        Assert.All(sines, w => Assert.True(w.Length < full, "a multiband wave must be smaller"));

        // Group the waves by their own stack, and check each stack in isolation.
        foreach (var group in sines.GroupBy(w => (Math.Round(w.Cx, 6), Math.Round(RowOf(w), 6))))
        {
            var stack = group.OrderBy(w => w.Cy).ToList();
            Assert.Equal(3, stack.Count);
            var near = Slashes(sym)
                       .Where(l => Math.Abs((l.X1 + l.X2) / 2 - group.Key.Item1) < 1e-6
                                && (l.Y1 + l.Y2) / 2 > stack[0].Cy - 30
                                && (l.Y1 + l.Y2) / 2 < stack[2].Cy + 30)
                       .ToList();
            Assert.Equal(2, near.Count);
            Assert.True(near.Any(l => Crosses(stack[0], l, SymbolRotation.R0, false)), "top wave struck");
            Assert.False(near.Any(l => Crosses(stack[1], l, SymbolRotation.R0, false)), "middle wave clear");
            Assert.True(near.Any(l => Crosses(stack[2], l, SymbolRotation.R0, false)), "bottom wave struck");
        }

        // The body is 220 x 220 about the origin; everything drawn inside it must stay inside it.
        var body = Assert.Single(sym.Primitives.OfType<RoundedRectPrimitive>());
        Assert.All(sines, w =>
        {
            Assert.True(Math.Abs(w.Cx) + w.Length / 2 < body.W / 2, "a wave runs through the body wall");
            Assert.True(Math.Abs(w.Cy) + w.Amp < body.H / 2, "a wave runs through the body wall");
        });

        // Dual-band is side by side: two stacks, one row. Tri-band is two below one: three x centres
        // and two rows, the lone stack above the pair.
        Assert.Equal(bands, sines.Select(w => Math.Round(w.Cx, 6)).Distinct().Count());
        var rows = sines.Select(w => Math.Round(RowOf(w), 6)).Distinct().OrderBy(v => v).ToList();
        Assert.Equal(bands == 3 ? 2 : 1, rows.Count);
        if (bands == 3) Assert.True(rows[0] < rows[1]);

        // The centre of a wave's own stack: the middle wave of the three it belongs to.
        double RowOf(SinePrimitive w)
            => sines.Where(o => Math.Abs(o.Cx - w.Cx) < 1e-6)
                    .Select(o => o.Cy)
                    .OrderBy(y => Math.Abs(y - w.Cy))
                    .Take(3)
                    .Average();
    }

    /// <summary>
    /// The instance draws itself from its own <c>Form</c> and <c>Bands</c> echoes — the glyph that
    /// reaches the renderer, not the type's default one.
    /// </summary>
    [Theory]
    [InlineData("Bandpass", "1", 3)]
    [InlineData("Lowpass",  "1", 3)]
    [InlineData("Bandpass", "2", 6)]
    [InlineData("Bandpass", "3", 9)]
    public void TheInstanceGlyphFollowsTheEchoParameters(string form, string bands, int waves)
    {
        var comp = PlaceFresh("MN1", 0, 0);
        comp.Parameters.Single(p => p.Name == "Form").Expression  = form;
        comp.Parameters.Single(p => p.Name == "Bands").Expression = bands;

        var rendered = comp.ToRenderComponent();
        Assert.NotNull(rendered.InstanceSymbol);
        Assert.Equal(waves, rendered.InstanceSymbol!.Primitives.OfType<SinePrimitive>().Count());

        // Same two pins in the same places, whatever the glyph — the wires must not move.
        Assert.Equal(2, rendered.InstanceSymbol.Pins.Count);
        Assert.Equal((-200.0, 0.0), (rendered.InstanceSymbol.Pins[0].LocalX, rendered.InstanceSymbol.Pins[0].LocalY));
        Assert.Equal((200.0, 0.0), (rendered.InstanceSymbol.Pins[1].LocalX, rendered.InstanceSymbol.Pins[1].LocalY));
    }

    /// <summary>A malformed or absent echo falls back to the single-band bandpass glyph, never crashes.</summary>
    [Theory]
    [InlineData("Elliptic", "0")]
    [InlineData("", "nonsense")]
    public void AnUnreadableEchoFallsBackToTheDefaultGlyph(string form, string bands)
    {
        var comp = PlaceFresh("MN1", 0, 0);
        comp.Parameters.Single(p => p.Name == "Form").Expression  = form;
        comp.Parameters.Single(p => p.Name == "Bands").Expression = bands;

        var rendered = comp.ToRenderComponent();
        Assert.Equal(3, rendered.InstanceSymbol!.Primitives.OfType<SinePrimitive>().Count());
        Assert.Equal(2, Slashes(rendered.InstanceSymbol).Count);
    }

    private static List<LinePrimitive> Slashes(Symbol sym)
        => sym.Primitives.OfType<LinePrimitive>()
              .Where(l => l.X1 != l.X2 && l.Y1 != l.Y2)   // the leads are axis-aligned
              .ToList();

    /// <summary>True when the sampled wave and the slash segment genuinely intersect.</summary>
    private static bool Crosses(SinePrimitive wave, LinePrimitive slash,
                                SymbolRotation rotation, bool mirrored)
    {
        (double X, double Y) W(double lx, double ly)
            => SchematicGeometry.LocalToWorld((float)lx, (float)ly, 0, 0, rotation, mirrored);

        var (ax, ay) = W(slash.X1, slash.Y1);
        var (bx, by) = W(slash.X2, slash.Y2);

        const int samples = 400;
        (double X, double Y)? previous = null;
        for (int i = 0; i <= samples; i++)
        {
            double t = i / (double)samples;
            double lx = wave.Cx - wave.Length / 2 + t * wave.Length;
            double ly = wave.Cy + wave.Amp * Math.Sin(2 * Math.PI * wave.Cycles * t);
            var here = W(lx, ly);
            if (previous is { } p && SegmentsCross(p.X, p.Y, here.X, here.Y, ax, ay, bx, by))
                return true;
            previous = here;
        }
        return false;
    }

    private static bool SegmentsCross(double ax, double ay, double bx, double by,
                                      double cx, double cy, double dx, double dy)
    {
        static double Cross(double ox, double oy, double px, double py, double qx, double qy)
            => (px - ox) * (qy - oy) - (py - oy) * (qx - ox);

        double d1 = Cross(cx, cy, dx, dy, ax, ay);
        double d2 = Cross(cx, cy, dx, dy, bx, by);
        double d3 = Cross(ax, ay, bx, by, cx, cy);
        double d4 = Cross(ax, ay, bx, by, dx, dy);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
            && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }
}
