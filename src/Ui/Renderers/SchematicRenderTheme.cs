using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

/// <summary>
/// Colour tokens for the schematic renderer.
/// Two static presets (Light / Dark); the canvas selects based on ActualThemeVariant.
/// </summary>
public sealed class SchematicRenderTheme
{
    public SKColor Background    { get; init; }
    public SKColor Grid          { get; init; }
    public SKColor Wire          { get; init; }
    public SKColor ComponentBody { get; init; }
    public SKColor Label         { get; init; }
    public SKColor ConnectionDot { get; init; }
    public SKColor UnconnectedPort { get; init; }
    public SKColor LodRect       { get; init; }

    public static readonly SchematicRenderTheme Light = new()
    {
        Background     = new SKColor(250, 250, 250),
        Grid           = new SKColor(170, 170, 170, 70),
        Wire           = new SKColor(25,  25,  25),
        ComponentBody  = new SKColor(30,  30,  30),
        Label          = new SKColor(30,  30,  30),
        ConnectionDot  = new SKColor(20,  20,  20),
        UnconnectedPort = new SKColor(200, 30,  30, 200),
        LodRect        = new SKColor(70,  90,  180, 180),
    };

    public static readonly SchematicRenderTheme Dark = new()
    {
        Background     = new SKColor(28,  28,  28),
        Grid           = new SKColor(80,  80,  80, 70),
        Wire           = new SKColor(200, 200, 200),
        ComponentBody  = new SKColor(190, 190, 190),
        Label          = new SKColor(190, 190, 190),
        ConnectionDot  = new SKColor(210, 210, 210),
        UnconnectedPort = new SKColor(220, 60,  60, 200),
        LodRect        = new SKColor(100, 120, 220, 180),
    };
}
