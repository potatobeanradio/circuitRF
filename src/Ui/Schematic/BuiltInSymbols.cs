// Single source of symbol geometry for all built-in symbol kinds.
// 2-terminal symbols (R/L/C/V/Tone/Term/GND) are VERTICAL: pins (0,∓200), leads on x=0.
// Box symbols (FET/ZPort/Sdd/Generic) stay HORIZONTAL: ports left/right.
// Geometry spec: docs/design/standard-library-symbols.md
//
// Framework-free — no Skia / Avalonia references.

using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Single source of symbol geometry for all built-in symbol kinds.
/// Every consumer (renderer, ComputeGlyphBb, ghost preview) reads Primitives(kind[, portCount]).
/// SDD and ZPort use an N-aware body that grows with port count — pass portCount for correct geometry.
/// </summary>
public static class BuiltInSymbols
{
    // ── Static caches — built once at first access ────────────────────────────

    private static readonly Symbol _resistor     = BuildResistor();
    private static readonly Symbol _inductor     = BuildInductor();
    private static readonly Symbol _capacitor    = BuildCapacitor();
    private static readonly Symbol _vdcSrc       = BuildVdc();
    private static readonly Symbol _toneSrc      = BuildToneSource();
    private static readonly Symbol _ground       = BuildGround();
    private static readonly Symbol _term         = BuildTerm();
    private static readonly Symbol _pin          = BuildPin();
    private static readonly Symbol _fetSdd       = BuildFetSdd();
    private static readonly Symbol _var          = BuildVar();
    private static readonly Symbol _generic      = BuildGeneric();
    private static readonly Symbol _p1Tone       = BuildP1Tone();

    // Per-N cache for variadic box symbols (SDD and ZPort share body geometry).
    private static readonly Dictionary<int, Symbol> _sddCache   = new();
    private static readonly Dictionary<int, Symbol> _zportCache = new();

    /// <summary>
    /// Returns the primitive list for a built-in symbol kind.
    /// For SDD/ZPort: uses the default portCount=2. Prefer the portCount overload.
    /// </summary>
    public static Symbol Primitives(SymbolKind kind) => Primitives(kind, 2);

    /// <summary>
    /// Returns the primitive list for a built-in symbol kind.
    /// For SDD/ZPort: builds an N-aware rounded-rect body sized to the pin span.
    /// portCount is ignored for other kinds.
    /// </summary>
    public static Symbol Primitives(SymbolKind kind, int portCount)
    {
        switch (kind)
        {
            case SymbolKind.ZPort:
            {
                int n = portCount > 0 ? portCount : 2;
                if (!_zportCache.TryGetValue(n, out var sym))
                    _zportCache[n] = sym = BuildSddVariadicSymbol(SymbolKind.ZPort, n);
                return sym;
            }
            case SymbolKind.Sdd:
            {
                int n = portCount > 0 ? portCount : 2;
                if (!_sddCache.TryGetValue(n, out var sym))
                    _sddCache[n] = sym = BuildSddVariadicSymbol(SymbolKind.Sdd, n);
                return sym;
            }
            case SymbolKind.Resistor:   return _resistor;
            case SymbolKind.Inductor:   return _inductor;
            case SymbolKind.Capacitor:  return _capacitor;
            case SymbolKind.Vdc:        return _vdcSrc;
            case SymbolKind.ToneSource: return _toneSrc;
            case SymbolKind.Ground:     return _ground;
            case SymbolKind.Term:       return _term;
            case SymbolKind.Pin:        return _pin;
            case SymbolKind.FetSdd:     return _fetSdd;
            case SymbolKind.Var:        return _var;
            case SymbolKind.P1Tone:     return _p1Tone;
            default:                    return _generic;
        }
    }

    // ── Line helpers ──────────────────────────────────────────────────────────

    private static LinePrimitive L(double x1, double y1, double x2, double y2,
                                   SymbolColorRole role = SymbolColorRole.SymbolLine)
        => new(role, SymbolStrokeTier.Normal, x1, y1, x2, y2);

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

    private static TextPrimitive Txt(string content, double ax, double ay,
                                      double fontSize = 12,
                                      SymbolTextAlign align = SymbolTextAlign.Center,
                                      SymbolTextVAlign vAlign = SymbolTextVAlign.Middle)
        => new() { Content = content, AnchorX = ax, AnchorY = ay,
                   FontSize = fontSize, Align = align, VAlign = vAlign };

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

    // ── Vdc — battery symbol: two unequal parallel bars + leads + +/− markers ─
    // Pins: (0,-200) + top / (0,+200) − bottom.

    private static Symbol BuildVdc() => Sym([
        L(  0, -200,   0,  -30),            // top lead
        L(-40,  -30,  40,  -30),            // long bar (+ terminal)
        L(-20,  -10,  20,   -10),            // short bar
        L(-40,  10,  40,  10),              // long bar
        L(-20,   30,  20,   30),            // short bar (− terminal)
        L(  0,   30,   0,  200),            // bottom lead
        Txt("+", -25, -100),                // + polarity marker near top lead
        Txt("−", -25, +100),                // − polarity marker near bottom lead
    ], SymbolKind.Vdc);

    // ── Tone / AC Source — circle + sine mark + leads + +/− markers ──────────
    // Pins: (0,-200) top / (0,+200) bottom.

    private static Symbol BuildToneSource() => Sym([
        L(   0, -200,   0,  -60),           // top lead
        Circ(  0,    0,  60),                // body circle (stroked)
        L(   0,   60,   0,  200),           // bottom lead
        Sine(0,    0,  22,    1,   70, SineAxis.Horizontal),  // AC mark
        Txt("+", -25, -130),                // + polarity marker near top lead
        Txt("−", -25, +130),                // − polarity marker near bottom lead
    ], SymbolKind.ToneSource);

    // ── P1Tone — power source: circle + sine mark + power-arrow chevron + +/− ─
    // Pins: (0,-200) top (RF) / (0,+200) bottom (reference).
    // Visually distinct from ToneSource by the upward-pointing chevron (↑) inside the circle.

    private static Symbol BuildP1Tone() => Sym([
        L(   0, -200,   0,  -60),           // top lead
        Circ(  0,    0,  60),                // body circle (stroked)
        L(   0,   60,   0,  200),           // bottom lead
        Sine(0,   15,  18,    1,   55, SineAxis.Horizontal),  // AC mark (shifted down slightly)
        L( -20,  -22,   0,  -38),           // chevron left arm  (↑)
        L(  20,  -22,   0,  -38),           // chevron right arm (↑)
        Txt("+", -25, -130),                // + polarity marker near top lead
        Txt("−", -25, +130),                // − polarity marker near bottom lead
    ], SymbolKind.P1Tone);

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
        Txt("+", -70, -165),                // + polarity marker near top lead
        Txt("−", -70, +165),                // − polarity marker near bottom lead
    ], SymbolKind.Term);

    // ── Pin — interface terminal: horizontal hexagon + stem, tip on the right ──
    // Pin connection point at (200,0) — the lead tip on the right.
    // Hexagon body centered near origin; stem from hex right vertex (80,0) to tip (200,0).
    // Num label (shown via parameters) identifies the cell interface port.

    private static Symbol BuildPin() => Sym([
        Poly(false, -40,-50,  40,-50,  80,0,  40,50,  -40,50,  -80,0),  // hexagon body
        L(80, 0,  200, 0),               // stem from hex right vertex to pin tip
    ], SymbolKind.Pin);

    // ── Ground — stem + filled downward triangle (Core Graphics style) ────────
    // Pins: (0,0) — the connection point at the top of the symbol.

    private static Symbol BuildGround() => Sym([
        L(  0,   0,   0,  40),             // stem
        L(  -45,   40,   45,  40),         // first line
        L(  -30,   55,   30,  55),         // second line
        L(  -15,   70,   15,  70),         // third
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

    // ── SDD / ZPort — N-aware rounded-rect body ───────────────────────────────
    // Body edges at ±90; port lead stubs drawn dynamically by the renderer.
    // Port-number/polarity TextPrimitives are part of the per-N symbol.
    // ZPort no longer carries the "Z" mark; identification is via the type label.

    private static Symbol BuildSddVariadicSymbol(SymbolKind kind, int n)
    {
        var ports = SymbolPortDefs.For(kind, n);
        var (w, halfH) = SymbolPortDefs.SddBodyRect(n);

        var prims = new List<SymbolPrimitive>
        {
            RRect(0, 0, w, halfH * 2, 12),
        };

        foreach (var (name, lx, ly) in ports)
        {
            bool isLeft = lx < 0;
            double ax    = isLeft ? -75.0 : 75.0;
            prims.Add(Txt(name, ax, ly, fontSize: 10,
                align: isLeft ? SymbolTextAlign.Left : SymbolTextAlign.Right,
                vAlign: SymbolTextVAlign.Middle));
        }

        return Sym(prims, kind, n);
    }

    // ── VAR — port-less box with "VAR" label ─────────────────────────────────

    private static Symbol BuildVar() => Sym([
        L(-80, -60,  80, -60),   // top
        L( 80, -60,  80,  60),   // right
        L( 80,  60, -80,  60),   // bottom
        L(-80,  60, -80, -60),   // left
        Txt("VAR", 0, 0, fontSize: 24, align: SymbolTextAlign.Center, vAlign: SymbolTextVAlign.Middle),
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
