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
    // ── Font sizes (compile-time consts — safe to reference from field initializers) ──
    /// <summary>Font size for the +/− polarity marks on 2-terminal sources/terminations.</summary>
    public const double PolarityFontSize = 36.0;
    /// <summary>Font size for SDD/ZPort port-number labels ("1+", "2−", …).</summary>
    public const double SddPortLabelFontSize = 10.0;
    /// <summary>Font size for the "VAR" body label.</summary>
    public const double VarLabelFontSize = 48.0;

    // ── Static caches — built once at first access ────────────────────────────

    private static readonly Symbol _resistor     = BuildResistor();
    private static readonly Symbol _inductor     = BuildInductor();
    private static readonly Symbol _capacitor    = BuildCapacitor();
    private static readonly Symbol _vdcSrc       = BuildVdc();
    private static readonly Symbol _toneSrc      = BuildToneSource();
    private static readonly Symbol _ground       = BuildGround();
    private static readonly Symbol _term         = BuildTerm();
    private static readonly Symbol _pin          = BuildPin();
    private static readonly Symbol _iprobe       = BuildIProbe();
    private static readonly Symbol _fetSdd       = BuildFetSdd();
    private static readonly Symbol _var          = BuildVar();
    private static readonly Symbol _generic      = BuildGeneric();
    private static readonly Symbol _p1Tone       = BuildP1Tone();

    // Per-N cache for variadic box symbols (SDD and ZPort share body geometry).
    private static readonly Dictionary<int, Symbol> _sddCache   = new();
    private static readonly Dictionary<int, Symbol> _zportCache = new();
    // SnP cache key: (n, refNode, cfg, pitch)
    private static readonly Dictionary<(int, bool, SnpPinConfig, SnpPitch), Symbol> _snpCache = new();

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
            case SymbolKind.Snp:
            {
                int n = portCount > 0 ? portCount : 2;
                return PrimitivesForSnp(n, refNode: false, cfg: SnpPinConfig.Standard, pitch: SnpPitch.Loose);
            }
            case SymbolKind.Resistor:   return _resistor;
            case SymbolKind.Inductor:   return _inductor;
            case SymbolKind.Capacitor:  return _capacitor;
            case SymbolKind.Vdc:        return _vdcSrc;
            case SymbolKind.ToneSource: return _toneSrc;
            case SymbolKind.Ground:     return _ground;
            case SymbolKind.Term:       return _term;
            case SymbolKind.Pin:        return _pin;
            case SymbolKind.IProbe:     return _iprobe;
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
                                      SymbolTextVAlign vAlign = SymbolTextVAlign.Middle,
                                      SymbolColorRole colorRole = SymbolColorRole.SymbolLine)
        => new() { Content = content, AnchorX = ax, AnchorY = ay,
                   FontSize = fontSize, Align = align, VAlign = vAlign, ColorRole = colorRole };

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
        Txt("+", -25, -100, fontSize: PolarityFontSize, colorRole: SymbolColorRole.SymbolPlus),   // + polarity marker near top lead
        Txt("−", -25, +100, fontSize: PolarityFontSize),                                           // − polarity marker near bottom lead
    ], SymbolKind.Vdc);

    // ── Tone / AC Source — circle + sine mark + leads + +/− markers ──────────
    // Pins: (0,-200) top / (0,+200) bottom.

    private static Symbol BuildToneSource() => Sym([
        L(   0, -200,   0,  -60),           // top lead
        Circ(  0,    0,  60),                // body circle (stroked)
        L(   0,   60,   0,  200),           // bottom lead
        Sine(0,    0,  22,    1,   70, SineAxis.Horizontal),  // AC mark
        Txt("+", -25, -130, fontSize: PolarityFontSize, colorRole: SymbolColorRole.SymbolPlus),   // + polarity marker near top lead
        Txt("−", -25, +130, fontSize: PolarityFontSize),                                           // − polarity marker near bottom lead
    ], SymbolKind.ToneSource);

    // ── P1Tone — RF power source: Term-sized box, top-half zigzag resistor,
    // bottom-half voltage-source circle with 1-cycle sine inside.
    // Pins: (0,-200) top (RF) / (0,+200) bottom (reference).
    // Same frame dimensions as Term so P1Tone reads as the same family.

    private static Symbol BuildP1Tone() => Sym([
        L(    0, -200,    0, -110),          // top lead into box (same as Term)
        RRect(0,    0,  110,  240,   12),    // frame box, SAME size as Term (y∈[-120,+120])
        // Top half: small zigzag resistor (smaller than Term's), spanning y∈[-100,-10]
        PLine(  0, -100,   0, -85,
               18, -73, -18, -55,
               18, -37, -18, -19,
                0,  -7,   0,   0),           // resistor body ends at circle top
        // Bottom half: voltage-source circle centered at (0,+55)
        Circ(  0,   55,  45),
        // Sine inside the circle: 1 cycle, fills the circle width (length = 2·r = 90)
        Sine(  0,   55,  20,    1,   90, SineAxis.Horizontal),
        L(    0,  120,    0,  200),          // bottom lead from box (same as Term)
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
                0,  95,   0, 120),
        L(    0, +120,    0, +200),         // "−" lead from box bottom
        Txt("+", -70, -165, fontSize: PolarityFontSize, colorRole: SymbolColorRole.SymbolPlus),   // + polarity marker near top lead
        Txt("−", -70, +165, fontSize: PolarityFontSize),                                           // − polarity marker near bottom lead
    ], SymbolKind.Term);

    // ── Pin — interface terminal: horizontal hexagon + stem, tip on the right ──
    // Port at (100,0) — on grid (multiple of 100). Total x-span −100..100 = 200.
    // Stem length 50: from hex right vertex (50,0) to port tip (100,0).
    // Hexagon: left vertex (-100,0), right vertex (50,0), height ±50.
    // Flat-top/bottom edges at x=-40 and x=10 (round numbers, aspect ratio free).
    // Num label (shown via parameters) identifies the cell interface port.

    private static Symbol BuildPin() => Sym([
        Poly(false, -55,-50,  10,-50,  50,0,  10,50,  -55,50,  -100,0),  // hexagon body
        L(50, 0,  100, 0),               // stem: hex right vertex (50,0) → port tip (100,0)
    ], SymbolKind.Pin);

    // ── IProbe — current probe / ammeter ─────────────────────────────────────
    // 2-terminal series ammeter. Pins at the BOTTOM (0,100)/(100,100), 100 apart.
    // Stems rise to a horizontal connector at y=0 carrying a right-pointing current
    // arrow (pin1 left → pin2 right). Above the connector: an ammeter window
    // (curved top/bottom via quad curves, top edge wider → angled sides). A rounded
    // rect encloses the window + arrow; the connector leads exit it to the stems.
    private static Symbol BuildIProbe() => Sym([
        L(  0, 100,   0,   0),                 // left stem  (pin1 → connector)
        L(100, 100, 100,   0),                 // right stem (pin2 → connector)
        L(  0,   0, 100,   0),                 // horizontal connector
        Poly(true, 40, -10, 60, 0, 40, 10),    // current arrow (filled), points right
        QC(35, -22, 50, -16, 65, -22),         // window bottom edge (shorter, bows down)
        QC(25, -52, 50, -58, 75, -52),         // window top edge (longer, bows up)
        L(35, -22, 25, -52),                   // window left side (angled out toward top)
        L(65, -22, 75, -52),                   // window right side (angled out toward top)
        RRect(50, -24, 80, 84, 10),            // enclosing rounded rect (window + arrow)
    ], SymbolKind.IProbe);

    // ── Ground — stem + filled downward triangle (Core Graphics style) ────────
    // Pins: (0,0) — the connection point at the top of the symbol.

    private static Symbol BuildGround() => Sym([
        L(  0,   0,   0,  40),             // stem
        L(  -45,   40,   45,  40),         // first line
        L(  -30,   55,   30,  55),         // second line
        L(  -15,   70,   15,  70),         // third
    ], SymbolKind.Ground);

    // ── FET/SDD — clean FET: gate bar, channel bar, straight horizontal leads ──
    // No box, no arrows. Drain/source leads are perfectly straight horizontal
    // lines from the channel bar to the on-grid pin tips at (200,∓100).
    // Channel spans y∈[−100,100] so each lead leaves it horizontally.

    private static Symbol BuildFetSdd() => Sym([
        L(-200,    0,  -60,    0),   // gate lead (tip at -200,0)
        L( -60, -100,  -60,  100),   // gate vertical bar
        L( -40, -100,  -40,  100),   // channel vertical bar (parallel to gate)
        L( -40, -100,  200, -100),   // drain: PERFECTLY STRAIGHT horizontal lead to pin tip (200,-100)
        L( -40,  100,  200,  100),   // source: PERFECTLY STRAIGHT horizontal lead to pin tip (200,100)
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
            // "+" terminal labels render in the SymbolPlus color; "−"/others stay regular.
            var role = name.EndsWith("+", StringComparison.Ordinal)
                ? SymbolColorRole.SymbolPlus
                : SymbolColorRole.SymbolLine;
            prims.Add(Txt(name, ax, ly, fontSize: SddPortLabelFontSize,
                align: isLeft ? SymbolTextAlign.Left : SymbolTextAlign.Right,
                vAlign: SymbolTextVAlign.Middle,
                colorRole: role));
        }

        return Sym(prims, kind, n);
    }

    // ── SnP — N-port Touchstone-file-backed network ──────────────────────────

    /// <summary>
    /// Returns (possibly cached) symbol primitives for an SnP component with the given params.
    /// Renderer/glyph-BB/ghost must call this instead of Primitives(Snp, n) when they know
    /// the component's actual RefNode/PinConfig/Pitch values.
    /// </summary>
    public static Symbol PrimitivesForSnp(int n, bool refNode, SnpPinConfig cfg, SnpPitch pitch)
    {
        var key = (n, refNode, cfg, pitch);
        if (!_snpCache.TryGetValue(key, out var sym))
            _snpCache[key] = sym = BuildSnpSymbol(n, refNode, cfg, pitch);
        return sym;
    }

    private static Symbol BuildSnpSymbol(int n, bool refNode, SnpPinConfig cfg, SnpPitch pitch)
    {
        var ports = SymbolPortDefs.GenerateSnpPorts(n, refNode, cfg, pitch);
        var (w, halfH) = SymbolPortDefs.SnpBodyRect(n, cfg, pitch);
        float cy = SymbolPortDefs.SnpBodyCenterYPublic(n, cfg, pitch);

        double bodyTop  = cy - halfH, bodyBot = cy + halfH;
        double bodyLeft = -w * 0.5,   bodyRight = +w * 0.5;

        var prims = new List<SymbolPrimitive> { RRect(0, cy, w, halfH * 2, 12) };

        foreach (var (name, lx, ly) in ports)
        {
            // Lead from the nearest body edge to the pin tip.
            if      (lx < 0)       prims.Add(L(bodyLeft,  ly, lx, ly));
            else if (lx > 0)       prims.Add(L(bodyRight, ly, lx, ly));
            else if (ly < bodyTop) prims.Add(L(0, bodyTop, 0, ly));   // top pin (3-port port 3)
            else                   prims.Add(L(0, bodyBot, 0, ly));   // bottom / Ref

            // Label inside the body; Ref gets "Ref" text, signal pins get port number.
            if (lx < 0 || (lx == 0 && n == 1))
                prims.Add(Txt(name, bodyLeft + 20, ClampInsideBody(ly, bodyTop, bodyBot),
                    SddPortLabelFontSize, SymbolTextAlign.Left, SymbolTextVAlign.Middle));
            else if (lx > 0)
                prims.Add(Txt(name, bodyRight - 20, ClampInsideBody(ly, bodyTop, bodyBot),
                    SddPortLabelFontSize, SymbolTextAlign.Right, SymbolTextVAlign.Middle));
            else if (name == "Ref")
                prims.Add(Txt("Ref", 0, bodyBot - 22, SddPortLabelFontSize,
                    SymbolTextAlign.Center, SymbolTextVAlign.Middle));
            else   // top-center pin (3-port port 3)
                prims.Add(Txt(name, 0, bodyTop + 22, SddPortLabelFontSize,
                    SymbolTextAlign.Center, SymbolTextVAlign.Middle));
        }

        var pins = ports.Select((d, i) => new SymbolPin(d.LocalX, d.LocalY, i, d.Name)).ToList();
        return new Symbol(prims, pins);

        static double ClampInsideBody(double y, double top, double bot)
            => Math.Min(bot - 22, Math.Max(top + 22, y));
    }

    // ── VAR — port-less box with "VAR" label ─────────────────────────────────

    private static Symbol BuildVar() => Sym([
        L(-80, -60,  80, -60),   // top
        L( 80, -60,  80,  60),   // right
        L( 80,  60, -80,  60),   // bottom
        L(-80,  60, -80, -60),   // left
        Txt("VAR", 0, 0, fontSize: VarLabelFontSize, align: SymbolTextAlign.Center, vAlign: SymbolTextVAlign.Middle),
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
