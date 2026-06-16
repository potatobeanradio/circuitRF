// Single source of symbol geometry for all built-in symbol kinds.
// 2-terminal symbols (R/L/C/V/Tone/Term/GND) are VERTICAL: pins (0,∓200), leads on x=0.
// Box symbols (FET/ZPort/Sdd/Generic) stay HORIZONTAL: ports left/right.
// Geometry spec: docs/design/standard-library-symbols.md
//
// Framework-free — no Skia / Avalonia references.

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Single source of symbol geometry for all built-in symbol kinds.
/// Every consumer (renderer, ComputeGlyphBb, ghost preview) reads Primitives(kind).
/// </summary>
public static class BuiltInSymbols
{
    // ── Static caches — built once at first access ────────────────────────────

    private static readonly Symbol _resistor     = BuildResistor();
    private static readonly Symbol _inductor     = BuildInductor();
    private static readonly Symbol _capacitor    = BuildCapacitor();
    private static readonly Symbol _voltSrc      = BuildVoltageSource();
    private static readonly Symbol _toneSrc      = BuildToneSource();
    private static readonly Symbol _ground       = BuildGround();
    private static readonly Symbol _term         = BuildTerm();
    private static readonly Symbol _pin          = BuildPin();
    private static readonly Symbol _fetSdd       = BuildFetSdd();
    private static readonly Symbol _zport        = BuildZPort();
    private static readonly Symbol _sdd          = BuildSdd();
    private static readonly Symbol _var          = BuildVar();
    private static readonly Symbol _generic      = BuildGeneric();

    /// <summary>
    /// Returns the primitive list for a built-in symbol kind.
    /// Every geometric consumer must call this; the float[] path is gone.
    /// </summary>
    public static Symbol Primitives(SymbolKind kind) => kind switch
    {
        SymbolKind.Resistor      => _resistor,
        SymbolKind.Inductor      => _inductor,
        SymbolKind.Capacitor     => _capacitor,
        SymbolKind.VoltageSource => _voltSrc,
        SymbolKind.ToneSource    => _toneSrc,
        SymbolKind.Ground        => _ground,
        SymbolKind.Term          => _term,
        SymbolKind.Pin           => _pin,
        SymbolKind.FetSdd        => _fetSdd,
        SymbolKind.ZPort         => _zport,
        SymbolKind.Sdd           => _sdd,
        SymbolKind.Var           => _var,
        _                        => _generic,
    };

    // ── Line helpers ──────────────────────────────────────────────────────────

    private static LinePrimitive L(double x1, double y1, double x2, double y2,
                                   SymbolColorRole role = SymbolColorRole.SymbolLine)
        => new(role, SymbolStrokeTier.Normal, x1, y1, x2, y2);

    private static LinePrimitive P(double x1, double y1, double x2, double y2)
        => new(SymbolColorRole.SymbolPlus, SymbolStrokeTier.Normal, x1, y1, x2, y2);

    // ── Curve / shape helpers ─────────────────────────────────────────────────

    private static ArcPrimitive A(double cx, double cy, double r,
                                   double startDeg, double sweepDeg)
        => new() { ColorRole  = SymbolColorRole.SymbolLine,
                   StrokeTier = SymbolStrokeTier.Normal,
                   Cx = cx, Cy = cy, R = r,
                   StartDeg = startDeg, SweepDeg = sweepDeg };

    private static CirclePrimitive Circ(double cx, double cy, double r, bool filled = false)
        => new() { ColorRole  = SymbolColorRole.SymbolLine,
                   StrokeTier = SymbolStrokeTier.Normal,
                   Cx = cx, Cy = cy, R = r, Filled = filled };

    private static QuadCurvePrimitive QC(double p0x, double p0y,
                                          double ctrlX, double ctrlY,
                                          double p2x, double p2y)
        => new() { ColorRole  = SymbolColorRole.SymbolLine,
                   StrokeTier = SymbolStrokeTier.Normal,
                   P0X = p0x, P0Y = p0y, CtrlX = ctrlX, CtrlY = ctrlY,
                   P2X = p2x, P2Y = p2y };

    private static RoundedRectPrimitive RRect(double cx, double cy,
                                               double w, double h, double radius)
        => new() { ColorRole  = SymbolColorRole.SymbolLine,
                   StrokeTier = SymbolStrokeTier.Normal,
                   Cx = cx, Cy = cy, W = w, H = h, Radius = radius };

    private static PolygonPrimitive Poly(bool filled, params double[] xyPairs)
    {
        var pg = new PolygonPrimitive
        {
            ColorRole  = SymbolColorRole.SymbolLine,
            StrokeTier = SymbolStrokeTier.Normal,
            Filled     = filled,
        };
        for (int i = 0; i + 1 < xyPairs.Length; i += 2)
            pg.Points.Add([xyPairs[i], xyPairs[i + 1]]);
        return pg;
    }

    private static PolylinePrimitive PLine(params double[] xyPairs)
    {
        var pl = new PolylinePrimitive
        {
            ColorRole  = SymbolColorRole.SymbolLine,
            StrokeTier = SymbolStrokeTier.Normal,
        };
        for (int i = 0; i + 1 < xyPairs.Length; i += 2)
            pl.Points.Add([xyPairs[i], xyPairs[i + 1]]);
        return pl;
    }

    private static SinePrimitive Sine(double cx, double cy,
                                       double amp, double cycles, double length,
                                       SineAxis axis)
        => new() { ColorRole  = SymbolColorRole.SymbolLine,
                   StrokeTier = SymbolStrokeTier.Normal,
                   Cx = cx, Cy = cy, Amp = amp, Cycles = cycles,
                   Length = length, Axis = axis };

    // ── Sym helper ────────────────────────────────────────────────────────────

    private static Symbol Sym(IReadOnlyList<SymbolPrimitive> prims, SymbolKind kind, int portCount = 0)
    {
        var portDefs = portCount > 0
            ? SymbolPortDefs.For(kind, portCount)
            : SymbolPortDefs.For(kind);
        var pins = portDefs
            .Select((d, i) => new SymbolPin(d.LocalX, d.LocalY, i, d.Name))
            .ToList();
        return new Symbol(prims, pins);
    }

    // ── Resistor — vertical 6-zig polyline (ANSI/US) ─────────────────────────
    // Pins: (0,-200) top / (0,+200) bottom.
    // One polyline = top lead + 6-zig body (y∈[-90,+90], amp ±30) + bottom lead.

    private static Symbol BuildResistor() => Sym([
        PLine( 0,-200,  0,-90,  30,-75, -30,-45,  30,-15,
              -30,  15, 30, 45, -30, 75,   0, 90,   0,200),
    ], SymbolKind.Resistor);

    // ── Inductor — 4 vertical coils bulging +x + polarity dot ────────────────
    // Pins: (0,-200) top / (0,+200) bottom.
    // 4 semicircle arcs centre=(0,cy) r=25, start=-90° sweep=180° (bulge right).

    private static Symbol BuildInductor() => Sym([
        L(  0, -200,   0, -100),            // top lead
        A(  0,  -75,  25, -90, 180),        // coil 1
        A(  0,  -25,  25, -90, 180),        // coil 2
        A(  0,   25,  25, -90, 180),        // coil 3
        A(  0,   75,  25, -90, 180),        // coil 4
        L(  0,  100,   0,  200),            // bottom lead
        Circ(40, -95,   6, filled: true),   // polarity dot (near top terminal)
    ], SymbolKind.Inductor);

    // ── Capacitor — flat plate + curved plate (Core Graphics style) ───────────
    // Pins: (0,-200) top / (0,+200) bottom.
    // Curved plate is a QuadCurve bowing toward the flat plate.
    // Bottom lead origin (0,+12) is the curve apex (computed midpoint of the quadratic).

    private static Symbol BuildCapacitor() => Sym([
        L(   0, -200,   0,  -12),           // top lead
        L( -50,  -12,  50,  -12),           // flat top plate
        QC( -50,   22,   0,    2,  50,  22),// curved bottom plate (bows toward gap)
        L(   0,   12,   0,  200),           // bottom lead (from curve apex)
    ], SymbolKind.Capacitor);

    // ── Voltage Source — circle + leads + ± marks ─────────────────────────────
    // Pins: (0,-200) + top / (0,+200) − bottom.

    private static Symbol BuildVoltageSource() => Sym([
        L(  0, -200,   0,  -60),            // top lead
        Circ( 0,    0,  60),                 // body circle (stroked)
        L(  0,   60,   0,  200),            // bottom lead
        P(-12,  -30,  12,  -30),            // + horizontal
        P(  0,  -42,   0,  -18),            // + vertical
        P(-12,   30,  12,   30),            // − bar
    ], SymbolKind.VoltageSource);

    // ── Tone / AC Source — circle + sine mark + leads ─────────────────────────
    // Pins: (0,-200) top / (0,+200) bottom.

    private static Symbol BuildToneSource() => Sym([
        L(   0, -200,   0,  -60),           // top lead
        Circ(  0,    0,  60),                // body circle (stroked)
        L(   0,   60,   0,  200),           // bottom lead
        Sine(0,    0,  22,    1,   70, SineAxis.Horizontal),  // AC mark
    ], SymbolKind.ToneSource);

    // ── Term — resistor-in-box, "+" (signal) and "−" (reference) pins ───────────
    // Pins: (0,-200) top "+" signal, (0,+200) bottom "−" reference.
    // Single-ended: wire "−" to Ground. Differential: wire across two DUT nodes.

    private static Symbol BuildTerm() => Sym([
        L(    0, -200,    0, -110),         // "+" lead into box top
        RRect(0,    0,  110,  240,   12),   // frame box (y∈[-120,+120])
        PLine(  0,-110,   0, -80,           // internal zigzag (termination R)
               25, -65, -25, -35,
               25,  -5, -25,  25,
               25,  55, -25,  80,
                0,  95,   0, 110),
        L(    0, +120,    0, +200),         // "−" lead from box bottom
    ], SymbolKind.Term);

    // ── Pin — interface terminal: short lead + open flag square ─────────────────
    // Pin at (0,-200) — the schematic connection point (lead tip).
    // A short vertical lead descends into a small open-square "flag" body.
    // The Num label (shown via parameters) identifies which cell interface port this is.

    private static Symbol BuildPin() => Sym([
        L(0, -200,  0, -100),          // lead from pin to flag body
        RRect(0, -50, 100, 100, 10),   // open square flag: center (0,-50), 100×100
    ], SymbolKind.Pin);

    // ── Ground — stem + filled downward triangle (Core Graphics style) ────────
    // Pins: (0,0) — the connection point at the top of the symbol.

    private static Symbol BuildGround() => Sym([
        L(  0,   0,   0,  40),             // stem
        Poly(filled: true, -45,40, 45,40, 0,90),  // downward triangle
    ], SymbolKind.Ground);

    // ── FET/SDD — box with gate, drain, source (horizontal, unchanged) ────────

    private static Symbol BuildFetSdd() => Sym([
        L(-200,   0, -80,   0),   // gate lead (tip at -200)
        L( -80,-100,  80,-100),   // box top
        L(  80,-100,  80, 100),   // box right
        L(  80, 100, -80, 100),   // box bottom
        L( -80, 100, -80,-100),   // box left
        L( -80,   0, -30,   0),   // gate horizontal bar
        L( -30, -70, -30,  70),   // channel vertical
        L( -30, -50,  80, -50),   // drain horizontal
        L(  80, -50, 200,-100),   // drain diagonal (tip at 200,-100)
        L( -30,  50,  80,  50),   // source horizontal
        L(  80,  50, 200, 100),   // source diagonal (tip at 200,100)
        L( -30, -50, -20, -40),   // arrow notch 1
        L( -30, -50, -20, -60),   // arrow notch 2
    ], SymbolKind.FetSdd);

    // ── ZPort — box + Z-mark (horizontal, static body only) ──────────────────
    // Port lead stubs drawn dynamically by the renderer per port.

    private static Symbol BuildZPort() => Sym([
        L(-70,-50,  70,-50),   // top
        L( 70,-50,  70, 50),   // right
        L( 70, 50, -70, 50),   // bottom
        L(-70, 50, -70,-50),   // left
        L(-40,-30,  40,-30),   // Z top
        L( 40,-30, -40, 30),   // Z diagonal
        L(-40, 30,  40, 30),   // Z bottom
    ], SymbolKind.ZPort);

    // ── Sdd — box only (horizontal) ──────────────────────────────────────────

    private static Symbol BuildSdd() => Sym([
        L(-80,-50,  80,-50),   // top
        L( 80,-50,  80, 50),   // right
        L( 80, 50, -80, 50),   // bottom
        L(-80, 50, -80,-50),   // left
    ], SymbolKind.Sdd);

    // ── VAR — port-less box (no leads) ───────────────────────────────────────

    private static Symbol BuildVar() => Sym([
        L(-80, -60,  80, -60),   // top
        L( 80, -60,  80,  60),   // right
        L( 80,  60, -80,  60),   // bottom
        L(-80,  60, -80, -60),   // left
    ], SymbolKind.Var);

    // ── Generic — 2-port box with leads (horizontal fallback) ─────────────────

    private static Symbol BuildGeneric() => Sym([
        L(-200,  0, -80,  0),   // left lead
        L(  80,  0, 200,  0),   // right lead
        L( -80,-50,  80,-50),   // top
        L(  80,-50,  80, 50),   // right
        L(  80, 50, -80, 50),   // bottom
        L( -80, 50, -80,-50),   // left
    ], SymbolKind.Generic);
}
