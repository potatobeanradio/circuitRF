namespace CircuitRF.Ui.Theming;

/// <summary>
/// Framework-free color theme: semantic roles → RGBA for light and dark variants.
/// Placed in src/Ui because it is presentation data consumed only by src/Ui today;
/// it carries no Avalonia/SkiaSharp types and could migrate to src/Core if another
/// assembly ever needs it.
///
/// Three-layer separation:
///   L1 (this class) — framework-free data model; the .ccolor file holds this.
///   L2 (SchematicRenderTheme.FromTheme) — projects roles → SKColor for the renderer.
///   L3 (AppPreferences + WorkspacePersistence) — active-theme selection and persistence.
/// </summary>
public sealed class ColorTheme
{
    public string Name { get; }

    private readonly IReadOnlyDictionary<string, Rgba> _light;
    private readonly IReadOnlyDictionary<string, Rgba> _dark;

    public ColorTheme(
        string name,
        IReadOnlyDictionary<string, Rgba> light,
        IReadOnlyDictionary<string, Rgba> dark)
    {
        Name   = name;
        _light = light;
        _dark  = dark;
    }

    /// <summary>
    /// Returns the RGBA for <paramref name="role"/> in the given variant,
    /// falling back to <see cref="BuiltIn"/> for any role absent from this theme
    /// (so partial or old .ccolor files load without hard-failing on missing roles).
    /// </summary>
    public Rgba Resolve(string role, ColorVariant variant)
    {
        var dict = variant == ColorVariant.Dark ? _dark : _light;
        if (dict.TryGetValue(role, out var color)) return color;
        if (!ReferenceEquals(this, BuiltIn)) return BuiltIn.Resolve(role, variant);
        return new Rgba(128, 128, 128);   // guard: built-in should be complete
    }

    /// <summary>Exposes the raw role maps so L2 and ColorThemeIo can iterate them.</summary>
    public (IReadOnlyDictionary<string, Rgba> Light, IReadOnlyDictionary<string, Rgba> Dark) GetRoleMaps()
        => (_light, _dark);

    // ── Built-in default — single source of truth for missing-role fallback ──────────────

    /// <summary>
    /// The built-in default palette (from the color-themes.md table).
    /// Shipped as Default.ccolor in /Assets/Color and as this in-code fallback so the app
    /// always has valid colors even if no files are found.
    /// </summary>
    public static readonly ColorTheme BuiltIn = new("Default",
        new Dictionary<string, Rgba>
        {
            [ColorRole.SchematicBackground]        = new(250, 250, 250),
            [ColorRole.SchematicGrid]              = new(170, 170, 170,  70),
            [ColorRole.SchematicWire]              = new(164,  63, 129),
            [ColorRole.SchematicNodeLabelText]     = new(164,  63, 129),
            [ColorRole.SchematicInstanceNameText]  = new( 59,  28, 243),
            [ColorRole.SchematicParameterNameText] = new( 24,   8, 122),
            [ColorRole.SchematicComponentNameText] = new(106, 142, 246),
            [ColorRole.SchematicConnectedPin]      = new( 94, 105, 216),
            [ColorRole.SchematicWireJunctionDot]   = new( 59,  28, 243),
            [ColorRole.SchematicSymbolLine]        = new( 45,  20, 195),
            [ColorRole.SchematicSymbolPlus]        = new(210,  99,  40),
            [ColorRole.SystemWarning]              = new(206,  74,  36),
        },
        new Dictionary<string, Rgba>
        {
            [ColorRole.SchematicBackground]        = new( 28,  28,  30),
            [ColorRole.SchematicGrid]              = new( 70,  70,  80,  70),
            [ColorRole.SchematicWire]              = new(214, 122, 178),
            [ColorRole.SchematicNodeLabelText]     = new(214, 122, 178),
            [ColorRole.SchematicInstanceNameText]  = new(138, 120, 255),
            [ColorRole.SchematicParameterNameText] = new(120, 104, 230),
            [ColorRole.SchematicComponentNameText] = new(140, 174, 255),
            [ColorRole.SchematicConnectedPin]      = new(130, 145, 240),
            [ColorRole.SchematicWireJunctionDot]   = new(138, 120, 255),
            [ColorRole.SchematicSymbolLine]        = new(150, 132, 250),
            [ColorRole.SchematicSymbolPlus]        = new(245, 140,  75),
            [ColorRole.SystemWarning]              = new(240, 120,  70),
        });
}
