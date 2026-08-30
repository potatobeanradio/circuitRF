namespace CircuitRF.Ui.Schematic;

// ---------------------------------------------------------------------------
//  Schematic read model 
//  World units: 100 = one grid square (standard EDA: 100 mils).
//  Component origin is the geometric center of its body.
// ---------------------------------------------------------------------------

public enum SymbolKind
{
    Resistor,
    Inductor,
    Capacitor,
    Vdc,
    ToneSource,
    Ground,
    Term,
    Pin,
    IProbe,
    Sdd,
    ZPort,
    Generic,
    Var,
    Meas,
    P1Tone,
    Snp,
    NonlinearC,
    Mutual,
    Tline,
    Tuner,

    /// <summary>Source-side Tuner. Same engine component as <see cref="Tuner"/> (EngineReference "Tuner",
    /// same parameters). Differs by glyph, instance prefix, and SOURCE-STYLE single-pin net ordering
    /// (pin = DUT-facing = Nodes[1]; the internal source net = Nodes[0], auto-generated, NOT ground).
    /// Pin on the RIGHT. Must be named SourceTuner= in the Loadpull analysis. Reference/source net as a
    /// pin is deferred.</summary>
    SourceTuner,

    /// <summary>Load-side Tuner. Same engine component as <see cref="Tuner"/>; LOAD-STYLE ordering
    /// (pin = DUT-facing = Nodes[0]; reference Nodes[1] hard-coded ground "0"). Pin on the LEFT. Must be
    /// named LoadTuner= in the analysis. Reference pin deferred.</summary>
    LoadTuner,

    /// <summary>Multi-tone RF power source (engine "PnTone"). A convenience clone of <see cref="P1Tone"/>
    /// for two-tone HB authoring: per-tone Freq[i]/Pavl[i]/Phase[i] fields, shared Z/Z[k]. Reuses
    /// P1Tone's symbol and 2-pin geometry. Not an S-param port (no Num).</summary>
    PnTone,

    /// <summary>Microstrip line (engine "MLIN"), brief-L5a-pcell-contract-and-microstrip.md.
    /// 2-port, W/L parameters. SymbolKind-registered like <see cref="Tline"/>, not an on-disk cell
    /// folder (see src/Ui/CLAUDE.md's L5a completion note for why).</summary>
    Mlin,

    /// <summary>Microstrip bend (engine "MBEND"). 2-port, W/Angle/Mitered parameters.</summary>
    MBend,

    /// <summary>Microstrip T-junction (engine "MTEE"). 3-port, W1/W2/W3 parameters.</summary>
    MTee,

    /// <summary>Microstrip cross-junction (engine "MCROSS"). 4-port, W1-W4 parameters.</summary>
    MCross,

    /// <summary>Linearly tapered microstrip line (engine "MTAPER"), brief-mtaper-mklopf.md §1.
    /// 2-port, W1/W2/L parameters.</summary>
    Mtaper,

    /// <summary>Klopfenstein-taper microstrip line (engine "MKLOPF"), brief-mtaper-mklopf.md §2-3.
    /// 2-port, Z1/Z2 (or W1/W2), GammaMax, L (or F3db), Offset, SmoothSteps parameters.</summary>
    Mklopf,

    /// <summary>Junction diode (engine "Diode"). Two pins, anode top / cathode bottom. `Rs` is a
    /// model parameter, not a separate placed resistor — when non-zero the elaborator mints the
    /// internal node itself, so the schematic shows one device either way.</summary>
    Diode,

    /// <summary>
    /// A compiled Verilog-A model the USER supplies (engine "VerilogA"). Points at a compiled model
    /// file and runs it — no kit, no manifest, nothing to install. Variadic: the model decides how
    /// many terminals it has, so `Pins` sets how many the symbol shows.
    /// </summary>
    VerilogA,

    // ── Built-in large-signal FET family ──────────────────────────────────────
    // Five SEPARATE kinds, one per published drain-current law, because they are NOT variants of
    // one another: each has its own parameter set, and several reuse a spelling for a different
    // quantity (the quadratic law's `Beta` is a transconductance parameter; the cubic law's is a
    // gate-voltage shift with drain bias). One kind with a "model" selector would present the union
    // of all five parameter sets and silently accept the wrong ones.
    //
    // All five SHARE one glyph and one 3-pin geometry (gate left, drain top, source bottom) — the
    // topology genuinely is the same, and the type label below the symbol names the law. The SOURCE
    // IS AN ORDINARY PIN: these are not hard-wired common-source.

    /// <summary>Curtice quadratic FET (engine "FET_Curtice"). Vto/Beta/Alpha/Lambda.</summary>
    FetCurtice,

    /// <summary>Curtice–Ettenberg cubic FET (engine "FET_CurticeCubic"). A0–A3/Gamma/Beta/Vds0.</summary>
    FetCurticeCubic,

    /// <summary>Statz FET (engine "FET_Statz"). Vto/Beta/B/Alpha/Lambda.</summary>
    FetStatz,

    /// <summary>Materka–Kacprzak FET (engine "FET_Materka"). Idss/Vp0/Gamma/Alpha.</summary>
    FetMaterka,

    /// <summary>Angelov (Chalmers) FET (engine "FET_Angelov"). Ipk/Vpk/P1–P3/Alpha/Lambda.</summary>
    FetAngelov,

    /// <summary>Term with port 2 permanently grounded, presenting as a 1-port
    /// (brief-housekeeping-tearoff-palette-repo.md §4). A packaging convenience, not a parallel
    /// model: reuses <see cref="Term"/>'s own engine reference ("Port") and glyph exactly, with
    /// <see cref="Ground"/>'s existing glyph placed at Term's own port-2 location — never redrawn,
    /// never resized. The remaining port keeps Term's port-1 identity ("+", (0,-200)), so a
    /// schematic that swaps Term+GND for TermG is electrically identical.</summary>
    TermG,

    /// <summary>
    /// A wirebond design placed as a component (engine "wBond", wbond.md §5). Its symbol is
    /// GENERATED from the <c>.wBond</c> file its <c>File</c> parameter names — two pins per wire
    /// array plus a <c>REF</c> pin — so both the pin count and the pin names are properties of that
    /// file rather than of this kind. See <see cref="WBondSymbolProvider"/> for why that needed a
    /// fourth symbol mechanism, and why no copy of the symbol is ever written to disk.
    /// </summary>
    WBond,

    /// <summary>Sentinel for a component type this build of circuitRF does not recognize
    /// (brief-housekeeping-tearoff-palette-repo.md R-hk-19a) — e.g. a `.csch` saved by a newer
    /// version, or one referencing a since-removed type such as the hard-removed library FET
    /// (§7A). Never placed by the user; only produced by <c>SchematicPersistence</c> on load.
    /// The original, unrecognized type string is preserved on
    /// <see cref="EditableComponent.UnknownSymbolRawName"/> so it can be reported by name rather
    /// than silently dropped. Renders as a generic placeholder glyph (the existing "unknown kind"
    /// fallback every switch over <see cref="SymbolKind"/> already has); the rest of the schematic
    /// still loads and simulates normally around it.</summary>
    Unknown,

    /// <summary>
    /// A synthesised bandpass matching network placed as one component (engine "Match",
    /// <c>docs/design/match.md</c> §8). Two pins, ground the common return; its whole design rides in
    /// a hidden base64 <c>Design</c> parameter, exactly as <see cref="WBond"/>'s wires do.
    ///
    /// <para>The component contains the ladder <b>minus</b> whatever the two external terminations
    /// supply — absorbing those reactances is the entire premise — so what it stamps is a property of
    /// the design rather than of this kind. Unlike <see cref="WBond"/> the pin COUNT is fixed at two,
    /// so the built-in symbol and geometry serve every design.</para>
    /// </summary>
    Match,

    // ── Built-in bipolar transistor ───────────────────────────────────────────
    // TWO kinds over ONE set of equations, which is the opposite of the FET family above: there the
    // five names denote five different drain-current laws with five different parameter sets, while
    // here the parameter list is identical and only a sign differs. Polarity is still two kinds
    // rather than one with a selector, because the two DRAW differently — the emitter arrow is the
    // whole of what a reader uses to tell them apart — and a selector would leave the schematic
    // showing an n-p-n while the netlist carried a p-n-p.
    //
    // Both are 3-pin: collector TOP, base LEFT, emitter BOTTOM. Rb/Re/Rc are MODEL parameters, not
    // separately placed resistors — a non-zero one moves the junctions onto an internal node the
    // elaborator mints, so the schematic shows one device either way.

    /// <summary>n-p-n bipolar transistor (engine "BJT_NPN"). Emitter arrow points OUT of the base.</summary>
    BjtNpn,

    /// <summary>p-n-p bipolar transistor (engine "BJT_PNP"). Emitter arrow points INTO the base.</summary>
    BjtPnp,

    // ── Current sources ───────────────────────────────────────────────────────
    // Both carry an ARROW as their only direction cue, for the same reason the BJT pair does: the
    // arrow is the first thing a reader looks for, and it is the whole of what says which way the
    // current goes. The two point OPPOSITE ways, and each is right for what it is — an INDEPENDENT
    // source delivers into its first pin (the engine's "J injects into its first node" convention,
    // src/Engine/CLAUDE.md), while a CONTROLLED transconductance sinks from its output-plus pin, the
    // way a small-signal gm source is drawn in every device model. Read each glyph's own arrow.

    /// <summary>Single- or multi-tone ideal CURRENT source (engine "I_1Tone"/"I_nTone"), the dual of
    /// <see cref="ToneSource"/>. Two pins, (0,−200) top and (0,+200) bottom; `I` is the tone
    /// amplitude, `Freq` its frequency, `Idc` a DC offset. Positive `I` sources current INTO the top
    /// pin — the direction the glyph's arrowhead points.</summary>
    CurrentToneSource,

    /// <summary>Ideal voltage-controlled current source (engine "VCCS"). Four pins in ± pair order —
    /// [0]=out+, [1]=out−, [2]=ctrl+, [3]=ctrl− — carrying I = G·(V(ctrl+) − V(ctrl−)). The control
    /// pair senses only and draws no current, which is what makes it ideal; positive G draws current
    /// IN at out+ and OUT at out−, the downward direction the diamond's arrowhead points — so the
    /// stage is inverting across a grounded load.</summary>
    Vccs,
}

public enum PortConnectionState { Unconnected, Connected }

public enum SymbolRotation { R0 = 0, R90 = 90, R180 = 180, R270 = 270 }

/// <summary>Port descriptor in component-LOCAL coordinates (before rotation/translation).</summary>
public sealed record SchematicPortDef(string Name, float LocalX, float LocalY, PortConnectionState State);

/// <summary>A placed component instance with pre-computed world bounding boxes.</summary>
public sealed class SchematicComponent
{
    // ── Label layout constants (world units, relative to component center) ────
    // Single source of truth shared by BuildRenderModel (which stores FullBb) and
    // the renderer (which reads it). Having them here prevents the two callsites
    // drifting to different values — that drift was what caused the LabelOffsets
    // cull blind spot fixed in this commit.
    public const double LabelBaseOffsetX   = -155.0; // label anchor X from center
    public const double LabelBaseY         =  280.0; // first-row Skia baseline Y from center
    public const double LabelWorldHeight   =   70.0; // font cap-height in world units
    public const double LabelWorldStep     =   72.0; // line-to-line spacing
    public const double LabelWidthEstimate =  500.0; // floor text-width estimate (short labels)
    public const double LabelCharWidth     =   50.0; // per-character world width — long labels (VAR var
                                                     // rows, MEAS formulas) need this so the cull BB covers
                                                     // their full width and they don't vanish at the edge.

    /// <summary>Generous world width of a label string for bounding-box culling: a per-character estimate
    /// (over-estimating is safe — it only widens the cull BB), floored at <see cref="LabelWidthEstimate"/>.</summary>
    public static double LabelWidthFor(string label) =>
        Math.Max(LabelWidthEstimate, (label?.Length ?? 0) * LabelCharWidth);

    /// <summary>
    /// First-row label baseline Y (from component center) for this symbol and port count.
    /// For fixed-geometry symbols this is the constant LabelBaseY. For the variadic SDD/ZPort
    /// symbols whose body grows with port count, the base-Y is pushed just below the glyph's
    /// bottom edge so the label never overlaps the symbol body.
    /// <paramref name="glyphHalfH"/> — when provided, overrides the SNP body half-height lookup
    /// with the component's real glyph extent, avoiding config-mismatch for n ≥ 4 ports.
    /// </summary>
    public static double LabelBaseYFor(SymbolKind symbol, int portCount, double? glyphHalfH = null)
    {
        if (symbol is SymbolKind.Sdd or SymbolKind.ZPort)
        {
            double halfH = SymbolPortDefs.SddBodyRect(portCount).HalfH;
            return Math.Max(LabelBaseY, halfH + LabelWorldStep);
        }
        if (symbol is SymbolKind.Snp)
        {
            double halfH = glyphHalfH
                ?? SymbolPortDefs.SnpBodyRect(portCount, SnpPinConfig.Standard, SnpPitch.Loose).HalfH;
            return Math.Max(LabelBaseY, halfH + LabelWorldStep);
        }
        // Tuner family: the box is only 200 tall (±100) but ShowBias adds a bias branch beneath it,
        // so the label must clear the actual glyph extent (glyphHalfH = glyph bottom), like SDD/ZPort/SnP.
        if (symbol is SymbolKind.Tuner or SymbolKind.SourceTuner or SymbolKind.LoadTuner)
        {
            double halfH = glyphHalfH ?? 100.0;   // box half-height when the real extent isn't supplied
            return Math.Max(LabelBaseY, halfH + LabelWorldStep);
        }

        // Any symbol whose real glyph runs deeper than the default offset must have its labels
        // pushed clear of it, or they render INSIDE the body. That is not a special case for one
        // kind — a cell reference resolved to a large kit symbol hits it hardest — so the clearance
        // rule applies whenever the caller knows the true glyph extent.
        return glyphHalfH is { } gh ? Math.Max(LabelBaseY, gh + LabelWorldStep) : LabelBaseY;
    }

    /// <summary>
    /// Canonical world geometry for label row <paramref name="i"/>, given the per-label offset
    /// (LabelOffsets[i] plus any live drag delta). Single source of truth shared by the renderer
    /// (DrawLabels) and the hit-test (TestComponentLabels) so the clickable zone always tracks the
    /// rendered text. Returns the left-aligned text anchor (BaselineX, BaselineY) and the vertical
    /// hit band [BandTopY, BandBotY] centered on the visual row.
    /// <paramref name="glyphHalfH"/> is forwarded to <see cref="LabelBaseYFor"/> for SNP accuracy.
    /// </summary>
    public static (double BaselineX, double BaselineY, double BandTopY, double BandBotY)
        LabelRowGeometry(double cx, double cy, int i, double oDx, double oDy,
                         SymbolKind symbol, int portCount, double? glyphHalfH = null)
    {
        double baseY     = LabelBaseYFor(symbol, portCount, glyphHalfH);
        double baselineX = cx + LabelBaseOffsetX + oDx;
        double baselineY = cy + baseY + oDy + i * LabelWorldStep;
        const double comfort = 6.0;
        double bandTopY  = baselineY - LabelWorldHeight - comfort;
        double bandBotY  = baselineY + LabelWorldHeight * 0.28 + comfort;
        return (baselineX, baselineY, bandTopY, bandBotY);
    }

    /// <summary>Stable ID carried from EditableComponent.Id — used by overlay for selection lookup.</summary>
    public string Id            { get; init; } = "";
    public string InstanceName  { get; init; } = "";
    public SymbolKind Symbol    { get; init; }
    public double X             { get; init; }
    public double Y             { get; init; }
    public SymbolRotation Rotation { get; init; }
    public bool MirrorX        { get; init; }
    public DisableState DisableState { get; init; }
    public IReadOnlyList<SchematicPortDef> Ports { get; init; } = [];

    /// <summary>
    /// On-schematic labels in display order: [0] = type, [1] = instance name, [2+] = parameters
    /// flagged ShowOnSchematic. Rendered left-aligned below the glyph.
    /// </summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>
    /// Per-label world-position offsets (DX, DY) from the default auto-position.
    /// Parallel to Labels; missing entries imply (0,0).
    /// </summary>
    public IReadOnlyList<(double DX, double DY)> LabelOffsets { get; init; } = [];

    // Glyph-only bounding box (±200 around center, used for zoom-to-fit and hit-test).
    public double BbMinX { get; init; }
    public double BbMinY { get; init; }
    public double BbMaxX { get; init; }
    public double BbMaxY { get; init; }

    // Symbol-glyph-only bounding box (no text area). Used for hit-testing and selection highlight.
    public double GlyphBbMinX { get; init; }
    public double GlyphBbMinY { get; init; }
    public double GlyphBbMaxX { get; init; }
    public double GlyphBbMaxY { get; init; }

    // Full visual bounding box: glyph BB unioned with every label at its actual offset position
    // (including LabelOffsets). Read by SchematicSpatialIndex (build) and the renderer in-loop
    // cull — both reference the same value so they stay in sync automatically.
    public double FullBbMinX { get; init; }
    public double FullBbMinY { get; init; }
    public double FullBbMaxX { get; init; }
    public double FullBbMaxY { get; init; }

    // ── Cell-reference rendering state ────────────────────────────────────────
    // Non-null only for cell-reference components (EditableComponent.CellRef != null).
    // Drives which render path the renderer takes:
    //   Resolved       → draw CellRefPrimitives via DrawSymbol
    //   NotFound       → "Not Found" warning glyph
    //   PrimaryMissing → plain-rectangle stand-in
    // Null = built-in component, BuiltInSymbols path unchanged.

    /// <summary>
    /// Three-state resolution result for cell-reference components; null for built-ins.
    /// </summary>
    public CellSymbolState? CellRefState      { get; init; }

    /// <summary>Non-null when CellRefState == Resolved — the primary .csym primitives to draw.</summary>
    public IReadOnlyList<SymbolPrimitive>? CellRefPrimitives { get; init; }

    /// <summary>
    /// Non-null for components whose glyph depends on per-instance params: SnP (RefNode/PinConfig/
    /// Pitch) and the Tuner family (ShowBias). The precomputed Symbol (primitives + pins). The
    /// renderer and glyph-BB computation use this instead of the generic BuiltInSymbols.Primitives
    /// fallback. Null for components whose glyph is a pure function of SymbolKind + port count.
    /// </summary>
    public Symbol? InstanceSymbol { get; init; }
}

/// <summary>A wire segment (orthogonal polyline) with pre-computed world bounding box.</summary>
public sealed class SchematicWire
{
    /// <summary>Stable ID carried from EditableWire.Id — used by overlay for selection lookup.</summary>
    public string Id            { get; init; } = "";
    public IReadOnlyList<(double X, double Y)> Points { get; init; } = [];
    public double BbMinX { get; init; }
    public double BbMinY { get; init; }
    public double BbMaxX { get; init; }
    public double BbMaxY { get; init; }
    /// <summary>Whether the first endpoint connects to another wire or component port.</summary>
    public bool StartConnected { get; init; }
    /// <summary>Whether the last endpoint connects to another wire or component port.</summary>
    public bool EndConnected   { get; init; }
}

/// <summary>
/// A junction dot (§4.3 dark square). circuitRF maintains a hard invariant (§5.1): a dot exists
/// only where it marks a genuine connection — a user dot on a real 4-way wire crossing, or a
/// derived auto-dot at a T-junction. Inert dots never reach the render model, so every dot here
/// is an unambiguous "these wires are connected" mark (load-bearing for 6e net extraction).
/// </summary>
public sealed class SchematicDot(double x, double y)
{
    public double X { get; } = x;
    public double Y { get; } = y;
}

/// <summary>A user-placed net (node) label displayed on the canvas.</summary>
public sealed class SchematicNetLabel
{
    public string Id   { get; init; } = "";
    public double X    { get; init; }
    public double Y    { get; init; }
    public string Name { get; init; } = "";
}

/// <summary>A user-placed bitmap canvas object in the schematic (read model).</summary>
public sealed record SchematicBitmap(
    string Id,
    string ImagePath,
    double X, double Y,        // top-left in world coords
    double Width, double Height,
    double Opacity);           // 0 = transparent, 1 = opaque

/// <summary>
/// The complete schematic read model consumed by SchematicRenderer.
/// Immutable after construction — 6c is read-only.
/// </summary>
public sealed class SchematicModel
{
    public IReadOnlyList<SchematicComponent>  Components   { get; init; } = [];
    public IReadOnlyList<SchematicWire>       Wires        { get; init; } = [];
    public IReadOnlyList<SchematicDot>        ConnectionDots { get; init; } = [];
    public IReadOnlyList<SchematicNetLabel>   NetLabels    { get; init; } = [];
    public IReadOnlyList<SchematicBitmap>     Bitmaps      { get; init; } = [];
    public double GridSize  { get; init; } = 100.0;
    // Overall bounding box of all elements (used for zoom-to-fit).
    public double BbMinX   { get; init; }
    public double BbMinY   { get; init; }
    public double BbMaxX   { get; init; }
    public double BbMaxY   { get; init; }
}
