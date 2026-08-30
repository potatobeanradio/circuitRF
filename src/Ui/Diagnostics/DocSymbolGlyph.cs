using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Diagnostics;

/// <summary>
/// One symbol on its own, drawn the way <see cref="SymbolArtworkGenerator"/> draws the emitted
/// <c>assets/symbols/*.svg</c> — glyph, port leads and the <b>unconnected port markers</b>.
///
/// <para><b>Not <c>PaletteGlyphControl</c>.</b> That one is documented as "glyph only (no pins, no
/// labels)", which is right for a palette tile and wrong here: without the little squares a reader
/// cannot see that a Pin's line ENDS in a connection point, which is the entire thing the figure is
/// explaining (owner, 2026-08-24). Both go through <c>SchematicRenderer</c>; this one goes through
/// the same call the symbol figures do, so the slide and the documentation page cannot disagree.</para>
/// </summary>
public sealed class DocSymbolGlyph : Control
{
    public static readonly StyledProperty<SymbolKind> KindProperty =
        AvaloniaProperty.Register<DocSymbolGlyph, SymbolKind>(nameof(Kind));

    public static readonly StyledProperty<int> PortCountProperty =
        AvaloniaProperty.Register<DocSymbolGlyph, int>(nameof(PortCount), 2);

    /// <summary>
    /// Which <c>Match</c> glyph to draw. Ignored for every other <see cref="Kind"/>.
    ///
    /// <para>A <c>Match</c> is the one built-in whose glyph is a function of its DESIGN rather than of
    /// its kind — the waves say bandpass, lowpass or highpass and how many bands
    /// (<c>BuiltInSymbols.PrimitivesForMatch</c>) — so documenting it needs a way to ask for a
    /// variant. These two properties are that, and they go through the same call the schematic makes,
    /// so a figure of the dual-band glyph cannot show a symbol the canvas would not draw.</para>
    /// </summary>
    public static readonly StyledProperty<NetworkForm> MatchFormProperty =
        AvaloniaProperty.Register<DocSymbolGlyph, NetworkForm>(nameof(MatchForm), NetworkForm.Bandpass);

    /// <summary>How many bands the <c>Match</c> glyph depicts, 1-3. See <see cref="MatchForm"/>.</summary>
    public static readonly StyledProperty<int> MatchBandsProperty =
        AvaloniaProperty.Register<DocSymbolGlyph, int>(nameof(MatchBands), 1);

    public NetworkForm MatchForm
    {
        get => GetValue(MatchFormProperty);
        set => SetValue(MatchFormProperty, value);
    }

    public int MatchBands
    {
        get => GetValue(MatchBandsProperty);
        set => SetValue(MatchBandsProperty, value);
    }

    public SymbolKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public int PortCount
    {
        get => GetValue(PortCountProperty);
        set => SetValue(PortCountProperty, value);
    }

    static DocSymbolGlyph()
        => AffectsRender<DocSymbolGlyph>(KindProperty, PortCountProperty,
                                         MatchFormProperty, MatchBandsProperty);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 2 || Bounds.Height < 2) return;
        context.Custom(new Op(new Rect(Bounds.Size), Kind, PortCount, MatchForm, MatchBands,
                              SchematicRenderTheme.FromTheme(ColorTheme.BuiltIn, ThemeService.CurrentVariant)));
    }

    private sealed class Op(Rect bounds, SymbolKind kind, int ports,
                           NetworkForm matchForm, int matchBands, SchematicRenderTheme theme)
        : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>()?.Lease();
            if (lease is null) return;
            using (lease)
            {
                var symbol = kind == SymbolKind.Match
                    ? BuiltInSymbols.PrimitivesForMatch(matchForm, matchBands)
                    : SymbolArtworkGenerator.SymbolFor(kind, ports);
                SymbolArtworkGenerator.DrawFitted(
                    lease.SkCanvas, kind, symbol,
                    (float)bounds.Width, (float)bounds.Height,
                    pad: (float)Math.Min(bounds.Width, bounds.Height) * 0.14f, theme);
            }
        }
    }
}
