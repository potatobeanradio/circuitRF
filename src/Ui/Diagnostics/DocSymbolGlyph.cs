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

    /// <summary>
    /// Which variant of a DYNAMIC system block to draw (brief-sys-1). Each is ignored by every kind
    /// but its own, exactly as <see cref="MatchForm"/> is.
    ///
    /// <para>Four built-ins besides <c>Match</c> draw a different picture per instance, and every one
    /// of them draws the thing the prose is about: a circulator that turns the other way, a switch
    /// in its other position, a filter that passes the other end of the band. Documenting them with
    /// only the default variant would illustrate the sentence next to it and none of the others.
    /// <see cref="SymbolKind.Filter"/> reuses <see cref="MatchForm"/> rather than adding a fifth
    /// property, because it is the same parameter choosing the same picture.</para>
    /// </summary>
    public static readonly StyledProperty<CirculatorDirection> CirculatorDirProperty =
        AvaloniaProperty.Register<DocSymbolGlyph, CirculatorDirection>(nameof(CirculatorDir));

    /// <summary>The SPST switch's position. See <see cref="CirculatorDir"/>.
    /// The default is stated because <see cref="SwitchState"/>'s members are numbered to match the
    /// engine's <c>State</c> parameter, so <c>default(SwitchState)</c> is Off, not On.</summary>
    public static readonly StyledProperty<SwitchState> SwitchPosProperty =
        AvaloniaProperty.Register<DocSymbolGlyph, SwitchState>(nameof(SwitchPos), SwitchState.On);

    /// <summary>The throw an SPDT switch points at. See <see cref="CirculatorDir"/>.</summary>
    public static readonly StyledProperty<SwitchThrow> SwitchThrownProperty =
        AvaloniaProperty.Register<DocSymbolGlyph, SwitchThrow>(nameof(SwitchThrown), SwitchThrow.T1);

    public CirculatorDirection CirculatorDir
    {
        get => GetValue(CirculatorDirProperty);
        set => SetValue(CirculatorDirProperty, value);
    }

    public SwitchState SwitchPos
    {
        get => GetValue(SwitchPosProperty);
        set => SetValue(SwitchPosProperty, value);
    }

    public SwitchThrow SwitchThrown
    {
        get => GetValue(SwitchThrownProperty);
        set => SetValue(SwitchThrownProperty, value);
    }

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
                                         MatchFormProperty, MatchBandsProperty,
                                         CirculatorDirProperty, SwitchPosProperty,
                                         SwitchThrownProperty);

    /// <summary>
    /// The symbol a set of variant selectors names — the ONE place that knows which built-ins draw
    /// themselves differently per instance, so a figure of a variant is drawn by the same call the
    /// canvas makes and cannot show a symbol the schematic would not.
    /// </summary>
    internal static Symbol GlyphFor(SymbolKind kind, int ports, NetworkForm matchForm, int matchBands,
                                    CirculatorDirection dir, SwitchState pos, SwitchThrow thrown)
        => kind switch
        {
            SymbolKind.Match      => BuiltInSymbols.PrimitivesForMatch(matchForm, matchBands),
            SymbolKind.Filter     => BuiltInSymbols.PrimitivesForFilter(matchForm),
            SymbolKind.Circulator => BuiltInSymbols.PrimitivesForCirculator(dir),
            SymbolKind.Switch     => BuiltInSymbols.PrimitivesForSwitch(pos),
            SymbolKind.SwitchD    => BuiltInSymbols.PrimitivesForSwitchD(thrown),
            _                     => SymbolArtworkGenerator.SymbolFor(kind, ports),
        };

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 2 || Bounds.Height < 2) return;
        context.Custom(new Op(new Rect(Bounds.Size), Kind, PortCount, MatchForm, MatchBands,
                              CirculatorDir, SwitchPos, SwitchThrown,
                              SchematicRenderTheme.FromTheme(ColorTheme.BuiltIn, ThemeService.CurrentVariant)));
    }

    private sealed class Op(Rect bounds, SymbolKind kind, int ports,
                           NetworkForm matchForm, int matchBands,
                           CirculatorDirection dir, SwitchState pos, SwitchThrow thrown,
                           SchematicRenderTheme theme)
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
                var symbol = GlyphFor(kind, ports, matchForm, matchBands, dir, pos, thrown);
                SymbolArtworkGenerator.DrawFitted(
                    lease.SkCanvas, kind, symbol,
                    (float)bounds.Width, (float)bounds.Height,
                    pad: (float)Math.Min(bounds.Width, bounds.Height) * 0.14f, theme);
            }
        }
    }
}
