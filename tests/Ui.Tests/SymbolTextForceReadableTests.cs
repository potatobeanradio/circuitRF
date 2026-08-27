using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner-reported, 2026-08-26: an SnP rotated in the schematic rendered its port numbers UPSIDE DOWN.
/// Every label circuitRF draws on a built-in symbol is a word to be read — a port number, "Ref",
/// "VAR" — never a mark whose orientation carries meaning, so all of them are
/// <see cref="TextPrimitive.ForceReadable"/> now. SDD, ZPort and the generic device box had the same
/// defect and share the same helper.
/// </summary>
public class SymbolTextForceReadableTests : IDisposable
{
    public SymbolTextForceReadableTests()
        // Assets/Fonts needs a live Avalonia host; SKTypeface.Default is the documented headless seam.
        => SkiaFonts.TestOverrideTypeface = SKTypeface.Default;

    public void Dispose() => SkiaFonts.TestOverrideTypeface = null;

    // ── T1: every built-in symbol's text is force-readable ────────────────────

    public static IEnumerable<object[]> AllKinds() =>
        Enum.GetValues<SymbolKind>().Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void EveryBuiltInSymbolText_IsForceReadable(SymbolKind kind)
    {
        // Port counts that exercise the variadic bodies (SDD/ZPort/SnP/VerilogA) as well as the fixed ones.
        foreach (int n in new[] { 1, 2, 3, 5 })
        {
            Symbol sym;
            try { sym = BuiltInSymbols.Primitives(kind, n); }
            catch (ArgumentException)      { continue; }   // kind does not accept this port count
            catch (NotSupportedException)  { continue; }

            foreach (var t in sym.Primitives.OfType<TextPrimitive>())
                Assert.True(t.ForceReadable,
                    $"{kind} (n={n}) text '{t.Content}' would render upside down on a rotated instance.");
        }
    }

    // ── T2: the three the owner named, through the parameterised SnP builder ──

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void SnpPortLabels_AreForceReadable_InEveryPinConfiguration(int n)
    {
        foreach (bool refNode in new[] { false, true })
        foreach (var cfg in Enum.GetValues<SnpPinConfig>())
        foreach (var pitch in Enum.GetValues<SnpPitch>())
        {
            var sym = BuiltInSymbols.PrimitivesForSnp(n, refNode, cfg, pitch);
            var texts = sym.Primitives.OfType<TextPrimitive>().ToList();

            Assert.NotEmpty(texts);
            Assert.All(texts, t => Assert.True(t.ForceReadable));
        }
    }

    // ── T3: the flag actually reaches the pixels ──────────────────────────────

    /// <summary>
    /// The oracle is the INK CENTROID, not a pixel diff: a glyph rasterised upright and the same
    /// glyph rasterised at 180 degrees do not agree pixel-for-pixel even when they are geometrically
    /// each other's rotation (hinting and antialiasing differ), so an exact comparison measures the
    /// rasteriser rather than the flag. The centroid does not care.
    ///
    /// <para>The text sits at the origin with Center/Middle anchoring, so its box centre IS the canvas
    /// centre and the whole question is where the ink lands relative to it. Rigid text rotates with the
    /// instance and its centroid REFLECTS through that point; force-readable text is drawn upright in
    /// the same place both times, so its centroid does not move at all. Asserting both is what proves
    /// the measurement discriminates.</para>
    /// </summary>
    [Fact]
    public void ForceReadableText_KeepsItsInkUpright_WhileRigidTextReflects()
    {
        // A capital A at this size puts its ink well above the box centre — several pixels of offset
        // to measure, rather than a fraction of one.
        static TextPrimitive Glyph(bool readable) => new()
        {
            Content = "A", AnchorX = 0, AnchorY = 0, FontSize = 200,
            Align = SymbolTextAlign.Center, VAlign = SymbolTextVAlign.Middle,
            ForceReadable = readable,
        };

        var upright  = Centroid([Glyph(true)],  SymbolRotation.R0);
        var readable = Centroid([Glyph(true)],  SymbolRotation.R180);
        var rigid    = Centroid([Glyph(false)], SymbolRotation.R180);

        // The measurement has something to see: the ink is genuinely off-centre, so the reflected and
        // unreflected answers are ~2*|Y| apart — an order of magnitude outside the tolerance below.
        Assert.True(Math.Abs(upright.Y) > 8.0,
            $"The probe glyph's ink is only {upright.Y:F2} px off its box centre — nothing to measure.");

        // 3 px, not 0: Skia hints an upright glyph to the pixel grid and does not hint a rotated one,
        // so the two rasterisations of one outline are a pixel or two apart however exact the geometry.
        // Measured at ~1.8 px here, against a ~22 px signal.
        const double Tol = 3.0;

        Assert.True(Distance(readable, upright) < Tol,
            $"Force-readable text moved when the instance rotated: {upright} -> {readable}.");

        Assert.True(Distance(rigid, (-upright.X, -upright.Y)) < Tol,
            $"Control failed — rigid text should reflect through the box centre, went to {rigid}.");
    }

    /// <summary>
    /// The same measurement on the labels an SnP actually carries, so the fix is pinned on the shipped
    /// symbol rather than only on a probe glyph.
    /// </summary>
    [Fact]
    public void AnSnpsOwnPortLabels_RenderDifferentlyRotated_ThanTheyWouldRigidly()
    {
        var labels = BuiltInSymbols.PrimitivesForSnp(2, refNode: false, SnpPinConfig.Standard, SnpPitch.Loose)
                                   .Primitives.OfType<TextPrimitive>().Cast<SymbolPrimitive>().ToList();
        Assert.NotEmpty(labels);

        var rigidLabels = labels.Cast<TextPrimitive>().Select(Clear).Cast<SymbolPrimitive>().ToList();

        var readable = Centroid(labels,      SymbolRotation.R180);
        var rigid    = Centroid(rigidLabels, SymbolRotation.R180);

        Assert.True(Distance(readable, rigid) > 2.0,
            $"The SnP's port labels rotate rigidly: readable {readable} vs rigid {rigid}.");
    }

    // ── T4: a label still belongs to ITS OWN pin, at every orientation ────────

    /// <summary>
    /// The label is what a user reads to decide which lead to wire, so keeping it upright is only half
    /// the requirement — it also has to stay ATTACHED to the pin it names (owner, 2026-08-26). This
    /// renders each port label on its own, takes its ink centroid, and asserts the LEAD it sits closest
    /// to is the one running to the pin whose name it carries — over every rotation, both mirror states
    /// and, for SnP, every pin configuration and pitch.
    ///
    /// <para><b>The metric is distance to the LEAD, not to the pin tip, and that is not a detail.</b>
    /// These symbols put each label inside the body on its own lead's row, which is exactly how a user
    /// reads one — and by straight-line distance to a PIN, a 4-port SnP's "3" is 125 units from pin 3
    /// and 125 units from the Ref pin diagonally below it, a dead tie decided by a pixel of glyph
    /// offset. The tie is in the symbol's geometry, not in the rendering: it sits there at every
    /// rotation, flag or no flag. Measuring against the lead the label lies on gives 15 units versus
    /// 90 and matches what is actually on screen.</para>
    ///
    /// <para>It holds because the readability flip is 180 degrees about the text's OWN box centre, and
    /// that centre goes through the instance transform like any other point: a rotation is an isometry,
    /// so every label-to-lead distance is carried over unchanged. The only thing the flip moves is the
    /// ink WITHIN the box — at most half a glyph — against tens of units of clearance. That margin is
    /// what this measures, rather than assumes.</para>
    /// </summary>
    [Theory]
    [InlineData(SymbolKind.Snp,      1)]
    [InlineData(SymbolKind.Snp,      2)]
    [InlineData(SymbolKind.Snp,      3)]
    [InlineData(SymbolKind.Snp,      4)]
    [InlineData(SymbolKind.Sdd,      2)]
    [InlineData(SymbolKind.Sdd,      3)]
    [InlineData(SymbolKind.ZPort,    2)]
    [InlineData(SymbolKind.ZPort,    4)]
    [InlineData(SymbolKind.VerilogA, 3)]
    public void EveryPortLabel_StaysOnTheLeadOfThePinItNames(SymbolKind kind, int n)
    {
        foreach (var sym in Variants(kind, n))
        foreach (var rotation in Enum.GetValues<SymbolRotation>())
        foreach (bool mirrorX in new[] { false, true })
        {
            var leads = LeadsByPin(sym);
            Assert.NotEmpty(leads);

            foreach (var label in sym.Primitives.OfType<TextPrimitive>())
            {
                // Only labels that NAME a pin are under test; a body annotation names nothing.
                if (!leads.ContainsKey(label.Content)) continue;

                var ink = Centroid([label], rotation, mirrorX);

                var nearest = leads
                    .Select(kv => (kv.Key, D: DistanceToLead(ink, kv.Value, rotation, mirrorX)))
                    .OrderBy(t => t.D)
                    .ToList();

                Assert.True(nearest[0].Key == label.Content,
                    $"{kind} n={n} {rotation}{(mirrorX ? " mirrored" : "")}: the label '{label.Content}' " +
                    $"sits on pin '{nearest[0].Key}'s lead ({nearest[0].D:F1} px, against " +
                    $"{nearest.First(t => t.Key == label.Content).D:F1} px to its own) — a user wiring " +
                    "by that label wires the wrong lead.");

                // Won, not tied. Without this the test would pass on a coin flip — which is exactly
                // the state a pin-tip metric was in, and the reason it is not the metric used here.
                if (nearest.Count > 1)
                    Assert.True(nearest[1].D - nearest[0].D > 10.0,
                        $"{kind} n={n} {rotation}{(mirrorX ? " mirrored" : "")}: the label " +
                        $"'{label.Content}' is {nearest[0].D:F1} px from its own lead and " +
                        $"{nearest[1].D:F1} px from '{nearest[1].Key}'s — too close to call.");
            }
        }
    }

    /// <summary>The shapes one kind actually takes on a schematic; SnP's pin layout is parameterised.</summary>
    private static IEnumerable<Symbol> Variants(SymbolKind kind, int n)
    {
        if (kind != SymbolKind.Snp) { yield return BuiltInSymbols.Primitives(kind, n); yield break; }

        foreach (bool refNode in new[] { false, true })
        foreach (var cfg in Enum.GetValues<SnpPinConfig>())
        foreach (var pitch in Enum.GetValues<SnpPitch>())
            yield return BuiltInSymbols.PrimitivesForSnp(n, refNode, cfg, pitch);
    }

    /// <summary>Each named pin's lead — the line primitive with an endpoint ON that pin.</summary>
    private static Dictionary<string, LinePrimitive> LeadsByPin(Symbol sym)
    {
        var lines = sym.Primitives.OfType<LinePrimitive>().ToList();
        var map   = new Dictionary<string, LinePrimitive>(StringComparer.Ordinal);

        foreach (var pin in sym.Pins)
        {
            if (string.IsNullOrEmpty(pin.Name)) continue;
            var lead = lines.FirstOrDefault(l => Touches(l, pin));
            if (lead is not null) map[pin.Name!] = lead;
        }
        return map;

        static bool Touches(LinePrimitive l, SymbolPin p) =>
            (Math.Abs(l.X1 - p.LocalX) < 0.5 && Math.Abs(l.Y1 - p.LocalY) < 0.5) ||
            (Math.Abs(l.X2 - p.LocalX) < 0.5 && Math.Abs(l.Y2 - p.LocalY) < 0.5);
    }

    /// <summary>Pixel distance from an ink centroid to a lead, both under the instance transform.</summary>
    private static double DistanceToLead(
        (double X, double Y) ink, LinePrimitive lead, SymbolRotation rotation, bool mirrorX)
    {
        var (ax, ay) = Pixel(lead.X1, lead.Y1, rotation, mirrorX);
        var (bx, by) = Pixel(lead.X2, lead.Y2, rotation, mirrorX);

        double dx = bx - ax, dy = by - ay;
        double len2 = dx * dx + dy * dy;
        double t = len2 <= 0 ? 0 : Math.Clamp(((ink.X - ax) * dx + (ink.Y - ay) * dy) / len2, 0, 1);
        return Distance(ink, (ax + t * dx, ay + t * dy));
    }

    private static (double X, double Y) Pixel(double lx, double ly, SymbolRotation rot, bool mirrorX)
    {
        var (px, py) = SchematicRenderer.LocalToPixel(
            (float)lx, (float)ly, 0, 0, rot, mirrorX, -Origin, -Origin, 1.0);
        return (px - Origin, py - Origin);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TextPrimitive Clear(TextPrimitive t) => new()
    {
        Content = t.Content, AnchorX = t.AnchorX, AnchorY = t.AnchorY, FontSize = t.FontSize,
        FontStyle = t.FontStyle, Align = t.Align, VAlign = t.VAlign, Rotation = t.Rotation,
        ColorRole = t.ColorRole, ForceReadable = false,
    };

    private static double Distance((double X, double Y) a, (double X, double Y) b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private const int Side   = 1200;
    private const double Origin = (Side - 1) / 2.0;

    /// <summary>Alpha-weighted ink centroid, in pixels relative to the component origin.</summary>
    private static (double X, double Y) Centroid(
        IReadOnlyList<SymbolPrimitive> prims, SymbolRotation rotation, bool mirrorX = false)
    {
        var theme = SchematicRenderTheme.FromTheme(ColorTheme.BuiltIn, ColorVariant.Dark);
        var info  = new SKImageInfo(Side, Side, SKColorType.Rgba8888, SKAlphaType.Premul);

        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Transparent);

        // Pan is SUBTRACTED before zoom (pixel = (world - pan) * zoom), so it is negated here to put
        // the component origin at the centre of the pixel GRID — the point every claim above is
        // measured against.
        SchematicRenderer.DrawSymbol(surface.Canvas, prims,
            compX: 0, compY: 0, rotation, mirrorX,
            panX: -Origin, panY: -Origin, zoom: 1.0, theme,
            applyForceReadable: true);

        using var image  = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        var px = bitmap.GetPixelSpan();          // Rgba8888: alpha is the 4th byte of each pixel
        double sum = 0, sx = 0, sy = 0;
        for (int y = 0, i = 3; y < Side; y++)
        for (int x = 0; x < Side; x++, i += 4)
        {
            double a = px[i];
            if (a <= 8) continue;
            sum += a; sx += a * x; sy += a * y;
        }

        Assert.True(sum > 0, "Nothing was drawn — the render harness is broken, not the flag.");
        return (sx / sum - Origin, sy / sum - Origin);
    }
}
