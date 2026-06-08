using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

/// <summary>
/// Colour tokens for the schematic renderer.
/// Two static presets (Light / Dark); the canvas selects based on ActualThemeVariant.
/// </summary>
public sealed class SchematicRenderTheme
{
    public SKColor Background      { get; init; }
    public SKColor Grid            { get; init; }
    public SKColor Wire            { get; init; }
    public SKColor ComponentBody   { get; init; }
    public SKColor Label           { get; init; }
    public SKColor ConnectionDot   { get; init; }
    public SKColor UnconnectedPort { get; init; }
    public SKColor LodRect         { get; init; }

    // 6d overlay colors
    public SKColor SelectionBox    { get; init; }   // selection highlight stroke
    public SKColor SelectionFill   { get; init; }   // selection highlight fill
    public SKColor RubberBandStroke{ get; init; }   // rubber-band rect stroke (overridden by accent)
    public SKColor RubberBandFill  { get; init; }   // rubber-band rect fill   (overridden by accent)
    public SKColor GhostBody       { get; init; }   // placement ghost
    public SKColor WirePreview     { get; init; }   // live wire preview
    public SKColor DisabledGlyph   { get; init; }   // disabled component overlay (legacy; Open/Short now use Warning)

    // 6d rendering additions
    public SKColor Warning         { get; init; }   // DisableState / unconnected port / unconnected wire endpoint
    public SKColor ConnectedPin    { get; init; }   // filled dot on a connected port
    public SKColor NetLabelText    { get; init; }   // wire node-name label text

    public static readonly SchematicRenderTheme Light = new()
    {
        Background      = new SKColor(250, 250, 250),
        Grid            = new SKColor(170, 170, 170, 70),
        Wire            = new SKColor(25,  25,  25),
        ComponentBody   = new SKColor(30,  30,  30),
        Label           = new SKColor(30,  30,  30),
        ConnectionDot   = new SKColor(20,  20,  20),
        UnconnectedPort = new SKColor(200, 30,  30, 200),
        LodRect         = new SKColor(70,  90,  180, 180),
        SelectionBox    = new SKColor(0,   120, 215, 255),
        SelectionFill   = new SKColor(0,   120, 215,  40),
        RubberBandStroke= new SKColor(0,   120, 215, 200),
        RubberBandFill  = new SKColor(0,   120, 215,  28),
        GhostBody       = new SKColor(0,   100, 180, 120),
        WirePreview     = new SKColor(0,   140, 60,  200),
        DisabledGlyph   = new SKColor(200, 60,  60,  160),
        Warning         = new SKColor(220, 100, 0,   220),
        ConnectedPin    = new SKColor(0,   150, 80,  220),
        NetLabelText    = new SKColor(0,   80,  160, 220),
    };

    public static readonly SchematicRenderTheme Dark = new()
    {
        Background      = new SKColor(28,  28,  28),
        Grid            = new SKColor(80,  80,  80, 70),
        Wire            = new SKColor(200, 200, 200),
        ComponentBody   = new SKColor(190, 190, 190),
        Label           = new SKColor(190, 190, 190),
        ConnectionDot   = new SKColor(210, 210, 210),
        UnconnectedPort = new SKColor(220, 60,  60, 200),
        LodRect         = new SKColor(100, 120, 220, 180),
        SelectionBox    = new SKColor(70,  160, 255, 255),
        SelectionFill   = new SKColor(70,  160, 255,  50),
        RubberBandStroke= new SKColor(70,  160, 255, 200),
        RubberBandFill  = new SKColor(70,  160, 255,  35),
        GhostBody       = new SKColor(80,  180, 255, 130),
        WirePreview     = new SKColor(60,  200, 100, 220),
        DisabledGlyph   = new SKColor(240, 80,  80,  160),
        Warning         = new SKColor(255, 140, 0,   230),
        ConnectedPin    = new SKColor(0,   200, 100, 220),
        NetLabelText    = new SKColor(80,  160, 255, 220),
    };

    /// <summary>Returns a copy of this theme with rubber-band colors overridden by the system accent.</summary>
    public SchematicRenderTheme WithAccent(SKColor accent) => new()
    {
        Background      = Background,
        Grid            = Grid,
        Wire            = Wire,
        ComponentBody   = ComponentBody,
        Label           = Label,
        ConnectionDot   = ConnectionDot,
        UnconnectedPort = UnconnectedPort,
        LodRect         = LodRect,
        SelectionBox    = SelectionBox,
        SelectionFill   = SelectionFill,
        RubberBandStroke= accent.WithAlpha(200),
        RubberBandFill  = accent.WithAlpha(35),
        GhostBody       = GhostBody,
        WirePreview     = WirePreview,
        DisabledGlyph   = DisabledGlyph,
        Warning         = Warning,
        ConnectedPin    = ConnectedPin,
        NetLabelText    = NetLabelText,
    };
}
