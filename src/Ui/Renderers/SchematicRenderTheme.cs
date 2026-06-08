using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

/// <summary>
/// SKColor token bundle consumed by the schematic renderer.
/// Built from a ColorTheme via FromTheme (Layer 2 projection) — never hardcoded.
/// Overlay/selection/accent colors are not yet user-themed and keep variant-specific defaults.
/// </summary>
public sealed class SchematicRenderTheme
{
    // ── Themed roles ──────────────────────────────────────────────────────────

    public SKColor Background        { get; init; }
    public SKColor Grid              { get; init; }
    public SKColor Wire              { get; init; }
    public SKColor SymbolLine        { get; init; }   // component body lines
    public SKColor SymbolPlus        { get; init; }   // +/− polarity marks (VoltageSource)
    public SKColor ComponentNameText { get; init; }   // label row 0: type / component name
    public SKColor InstanceNameText  { get; init; }   // label row 1: instance name
    public SKColor ParameterNameText { get; init; }   // label row 2+: parameters
    public SKColor ConnectionDot     { get; init; }   // wire-to-wire junction dot
    public SKColor UnconnectedPort   { get; init; }   // unconnected pin marker (= System.Warning)
    public SKColor Warning           { get; init; }   // unconnected wire endpoint (= System.Warning)
    public SKColor ConnectedPin      { get; init; }   // filled dot on a connected port
    public SKColor NetLabelText      { get; init; }   // wire node-name label

    // ── Not yet themed (overlay, interaction, LOD) ────────────────────────────

    public SKColor LodRect           { get; init; }
    public SKColor SelectionBox      { get; init; }
    public SKColor SelectionFill     { get; init; }
    public SKColor RubberBandStroke  { get; init; }
    public SKColor RubberBandFill    { get; init; }
    public SKColor GhostBody         { get; init; }
    public SKColor WirePreview       { get; init; }
    public SKColor DisabledGlyph     { get; init; }

    // ── Projection factory (L2) ───────────────────────────────────────────────

    /// <summary>
    /// Builds a SchematicRenderTheme by projecting the given ColorTheme's roles to SKColor.
    /// Overlay / interaction colors that are not yet user-themed keep variant-specific defaults.
    /// </summary>
    public static SchematicRenderTheme FromTheme(ColorTheme theme, ColorVariant variant)
    {
        SKColor SK(string role)
        {
            var c = theme.Resolve(role, variant);
            return new SKColor(c.R, c.G, c.B, c.A);
        }

        bool isLight = variant == ColorVariant.Light;
        return new SchematicRenderTheme
        {
            // ── Themed roles ──────────────────────────────────────────────────
            Background        = SK(ColorRole.SchematicBackground),
            Grid              = SK(ColorRole.SchematicGrid),
            Wire              = SK(ColorRole.SchematicWire),
            SymbolLine        = SK(ColorRole.SchematicSymbolLine),
            SymbolPlus        = SK(ColorRole.SchematicSymbolPlus),
            ComponentNameText = SK(ColorRole.SchematicComponentNameText),
            InstanceNameText  = SK(ColorRole.SchematicInstanceNameText),
            ParameterNameText = SK(ColorRole.SchematicParameterNameText),
            ConnectionDot     = SK(ColorRole.SchematicWireJunctionDot),
            UnconnectedPort   = SK(ColorRole.SystemWarning),
            Warning           = SK(ColorRole.SystemWarning),
            ConnectedPin      = SK(ColorRole.SchematicConnectedPin),
            NetLabelText      = SK(ColorRole.SchematicNodeLabelText),

            // ── Not yet themed — vary by variant ──────────────────────────────
            LodRect          = isLight ? new SKColor( 70,  90, 180, 180) : new SKColor(100, 120, 220, 180),
            SelectionBox     = isLight ? new SKColor(  0, 120, 215, 255) : new SKColor( 70, 160, 255, 255),
            SelectionFill    = isLight ? new SKColor(  0, 120, 215,  40) : new SKColor( 70, 160, 255,  50),
            RubberBandStroke = isLight ? new SKColor(  0, 120, 215, 200) : new SKColor( 70, 160, 255, 200),
            RubberBandFill   = isLight ? new SKColor(  0, 120, 215,  28) : new SKColor( 70, 160, 255,  35),
            GhostBody        = isLight ? new SKColor(  0, 100, 180, 120) : new SKColor( 80, 180, 255, 130),
            WirePreview      = isLight ? new SKColor(  0, 140,  60, 200) : new SKColor( 60, 200, 100, 220),
            DisabledGlyph    = isLight ? new SKColor(200,  60,  60, 160) : new SKColor(240,  80,  80, 160),
        };
    }

    // ── Convenience statics (derived from BuiltIn — one source of truth) ──────

    /// <summary>Default light-mode token set, derived from ColorTheme.BuiltIn.</summary>
    public static readonly SchematicRenderTheme Light = FromTheme(ColorTheme.BuiltIn, ColorVariant.Light);

    /// <summary>Default dark-mode token set, derived from ColorTheme.BuiltIn.</summary>
    public static readonly SchematicRenderTheme Dark  = FromTheme(ColorTheme.BuiltIn, ColorVariant.Dark);

    // ── Accent override ───────────────────────────────────────────────────────

    /// <summary>Returns a copy with rubber-band colors replaced by the system accent color.</summary>
    public SchematicRenderTheme WithAccent(SKColor accent) => new()
    {
        Background        = Background,
        Grid              = Grid,
        Wire              = Wire,
        SymbolLine        = SymbolLine,
        SymbolPlus        = SymbolPlus,
        ComponentNameText = ComponentNameText,
        InstanceNameText  = InstanceNameText,
        ParameterNameText = ParameterNameText,
        ConnectionDot     = ConnectionDot,
        UnconnectedPort   = UnconnectedPort,
        Warning           = Warning,
        ConnectedPin      = ConnectedPin,
        NetLabelText      = NetLabelText,
        LodRect           = LodRect,
        SelectionBox      = SelectionBox,
        SelectionFill     = SelectionFill,
        RubberBandStroke  = accent.WithAlpha(200),
        RubberBandFill    = accent.WithAlpha(35),
        GhostBody         = GhostBody,
        WirePreview       = WirePreview,
        DisabledGlyph     = DisabledGlyph,
    };
}
