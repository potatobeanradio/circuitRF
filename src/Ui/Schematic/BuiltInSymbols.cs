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
    /// <summary>
    /// Font size for the port-number labels every dynamic symbol carries — SDD/ZPort's "1+"/"2−",
    /// SnP's "1".."N" and "Ref". In symbol-local units, where one schematic grid square is 100 and
    /// an SDD body is 180 tall, so the previous 10 rendered at roughly a twentieth of the body:
    /// legible only when zoomed well in, on the ONE label that says which lead is which. Raised on
    /// owner request. The bodies are 180–200 wide and the labels are inset 15–20 from the edge, so
    /// even a three-character "10+" clears the opposite edge comfortably; the tightest vertical
    /// case is SnP at Tight pitch, whose pins are a full grid square (100) apart.
    /// </summary>
    public const double SddPortLabelFontSize = 18.0;
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
    private static readonly Symbol _var          = BuildVar();
    private static readonly Symbol _meas         = BuildMeas();
    private static readonly Symbol _generic      = BuildGeneric();
    private static readonly Symbol _p1Tone       = BuildP1Tone();
    private static readonly Symbol _nonlinearC   = BuildNonlinearC();
    private static readonly Symbol _mutual        = BuildMutual();
    private static readonly Symbol _tline         = BuildTline();
    private static readonly Symbol _tuner         = BuildTuner();
    private static readonly Symbol _sourceTuner   = BuildSourceTuner();
    private static readonly Symbol _loadTuner     = BuildLoadTuner();
    private static readonly Symbol _mlin          = BuildMlin();
    private static readonly Symbol _mbend         = BuildMBend();
    private static readonly Symbol _mtee          = BuildMTee();
    private static readonly Symbol _mcross        = BuildMCross();
    private static readonly Symbol _mtaper        = BuildMtaper();
    private static readonly Symbol _mklopf        = BuildMklopf();
    private static readonly Symbol _termG         = BuildTermG();
    private static readonly Symbol _diode         = BuildDiode();
    private static readonly Symbol _fet           = BuildFet();

    // Per-N cache for variadic box symbols (SDD and ZPort share body geometry).
    private static readonly Dictionary<int, Symbol> _sddCache   = new();
    private static readonly Dictionary<int, Symbol> _zportCache = new();
    // SnP cache key: (n, refNode, cfg, pitch)
    private static readonly Dictionary<(int, bool, SnpPinConfig, SnpPitch), Symbol> _snpCache = new();
    private static readonly Dictionary<int, Symbol> _verilogACache = new();
    // Tuner-family cache key: (kind, showBias) — per-instance bias-branch variant.
    private static readonly Dictionary<(SymbolKind, bool), Symbol> _tunerCache = new();

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
            case SymbolKind.VerilogA:
            {
                int n = portCount > 0 ? portCount : 2;
                if (!_verilogACache.TryGetValue(n, out var sym))
                    _verilogACache[n] = sym = BuildVerilogASymbol(n);
                return sym;
            }
            case SymbolKind.Resistor:   return _resistor;
            case SymbolKind.Inductor:   return _inductor;
            case SymbolKind.Capacitor:  return _capacitor;
            case SymbolKind.NonlinearC: return _nonlinearC;
            case SymbolKind.Mutual:     return _mutual;
            case SymbolKind.Tline:      return _tline;
            case SymbolKind.Mlin:       return _mlin;
            case SymbolKind.MBend:      return _mbend;
            case SymbolKind.MTee:       return _mtee;
            case SymbolKind.MCross:     return _mcross;
            case SymbolKind.Mtaper:     return _mtaper;
            case SymbolKind.Mklopf:     return _mklopf;
            case SymbolKind.Vdc:        return _vdcSrc;
            case SymbolKind.ToneSource: return _toneSrc;
            case SymbolKind.Ground:     return _ground;
            case SymbolKind.Term:       return _term;
            case SymbolKind.Pin:        return _pin;
            case SymbolKind.IProbe:     return _iprobe;
            case SymbolKind.Var:        return _var;
            case SymbolKind.Meas:       return _meas;
            case SymbolKind.P1Tone:     return _p1Tone;
            case SymbolKind.PnTone:     return _p1Tone;   // shares P1Tone's glyph (multi-tone variant)
            case SymbolKind.Tuner:       return _tuner;
            case SymbolKind.SourceTuner: return _sourceTuner;
            case SymbolKind.LoadTuner:   return _loadTuner;
            case SymbolKind.TermG:       return _termG;
            case SymbolKind.Diode:       return _diode;
            // All five FET laws share ONE glyph: the topology really is identical and only the
            // drain-current equation differs, which the type label below the symbol already names.
            // Drawing five near-identical triangles-and-bars would imply a difference the schematic
            // cannot show. Same reasoning as PnTone reusing P1Tone's glyph.
            case SymbolKind.FetCurtice:
            case SymbolKind.FetCurticeCubic:
            case SymbolKind.FetStatz:
            case SymbolKind.FetMaterka:
            case SymbolKind.FetAngelov:  return _fet;
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

    // ── NonlinearC — capacitor glyph + three diagonal "nonlinear" slashes ──────
    // Identical plates/leads to the linear capacitor; three parallel diagonal strokes
    // are the standard nonlinear-element annotation. Pins: (0,-200)/(0,+200).

    // ── Diode — filled triangle + cathode bar, vertical like the lumped elements ──
    // Pins: (0,-200) anode top / (0,+200) cathode bottom. Current flows top→bottom, which is the
    // direction the triangle points, so the glyph reads the same way the model's sign convention does.

    private static Symbol BuildDiode() => Sym([
        L(   0, -200,   0,  -55),                  // anode lead
        Poly(true, -60, -55,  60, -55,   0,  45),  // body triangle, apex at the bar
        L( -60,   45,  60,   45),                  // cathode bar
        L(   0,   45,   0,  200),                  // cathode lead
    ], SymbolKind.Diode);

    // ── FET — Schottky/insulated gate bar, channel bar, drain and source arms ──
    // Pins: gate (−200,0) LEFT, drain (0,−200) TOP, source (0,+200) BOTTOM. The arrow on the gate
    // lead marks the n-channel polarity and points INTO the channel, the usual convention.
    //
    // Shared by all five built-in FET laws — see the dispatch above for why.

    private static Symbol BuildFet() => Sym([
        L(-200,    0, -135,    0),                    // gate lead (up to the arrow base)
        Poly(true, -135, -32, -135,  32,  -80,   0),  // n-channel arrow, tip on the gate bar
        L( -80, -110,  -80,  110),                    // gate bar
        L( -30, -110,  -30,  110),                    // channel bar
        L( -30,  -80,    0,  -80),                    // drain arm off the channel
        L(   0,  -80,    0, -200),                    // drain lead
        L( -30,   80,    0,   80),                    // source arm off the channel
        L(   0,   80,    0,  200),                    // source lead
    ], SymbolKind.FetCurtice);

    private static Symbol BuildNonlinearC() => Sym([
        L(   0, -200,   0,  -12),            // top lead
        L( -50,  -12,  50,  -12),            // flat top plate
        QC( -50,   22,   0,    2,  50,  22), // curved bottom plate
        L(   0,   12,   0,  200),            // bottom lead
        // nonlinear annotation: two end-ticks joined by a diagonal (−y is up)
        L( -50, -32, -50, -62),              // Line 1: left tick, above plate, upward
        L(  50,   28,  50,  58),              // Line 2: right tick, below plate, downward
        L( -50, -32,  50,   28),              // Line 3: diagonal joining the closest ends
    ], SymbolKind.NonlinearC);

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

    // ── TermG — Term with port 2 permanently grounded (brief-housekeeping-tearoff-palette-repo.md
    // §4) ────────────────────────────────────────────────────────────────────────────────────
    // R-hk-7/R-hk-8: reuses Term's OWN primitives verbatim (no redraw, no resize) plus Ground's
    // OWN primitives verbatim, translated to sit exactly at Term's port-2 location (0,+200) — the
    // same point a separately-wired GND would occupy. The combined bounding box is therefore
    // identical to Term + GND placed separately (§8 gate 7): Term's leads/box/text are untouched,
    // and Ground's stem+bars simply continue from y=+200 to y=+270 exactly as they would if Ground
    // itself were placed with its own pin (local origin) at that world point.

    private static Symbol BuildTermG() => Sym(
        [.. _term.Primitives, .. TranslateLines(_ground.Primitives, 0, 200)],
        SymbolKind.TermG);

    /// <summary>Translates a primitive list by (dx,dy) — only <see cref="LinePrimitive"/> is needed
    /// here since <see cref="BuildGround"/> draws Ground entirely out of lines.</summary>
    private static IEnumerable<SymbolPrimitive> TranslateLines(IReadOnlyList<SymbolPrimitive> prims, double dx, double dy)
        => prims.Select(p => p switch
        {
            LinePrimitive l => new LinePrimitive(l.ColorRole, l.StrokeTier, l.X1 + dx, l.Y1 + dy, l.X2 + dx, l.Y2 + dy),
            _ => throw new NotSupportedException(
                $"TranslateLines only supports {nameof(LinePrimitive)} (Ground glyph is line-only); got {p.GetType().Name}."),
        });

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

    /// <summary>
    /// A plain box with a lead to each terminal. Deliberately generic: circuitRF does not know what
    /// the user's model IS — it could be a transistor, a diode, a whole subcircuit — so drawing a
    /// transistor glyph would assert something untrue on the schematic. The terminal NUMBERS are
    /// drawn instead, because with a generic body they are the only thing telling a user which lead
    /// is which.
    /// </summary>
    private static Symbol BuildVerilogASymbol(int n)
    {
        var ports = SymbolPortDefs.GenerateGenericDevicePorts(n);

        float top = ports.Min(p => p.LocalY), bottom = ports.Max(p => p.LocalY);
        double cy = (top + bottom) * 0.5;
        double halfH = Math.Max((bottom - top) * 0.5 + 60, 100);
        const double halfW = 110;

        var prims = new List<SymbolPrimitive> { RRect(0, cy, halfW * 2, halfH * 2, 12) };

        foreach (var (name, lx, ly) in ports)
        {
            bool onLeft = lx < 0;
            prims.Add(L(onLeft ? -halfW : halfW, ly, lx, ly));

            // The terminal numbers this symbol's own contract promises: with a deliberately generic
            // body they are the ONLY thing telling a user which lead is which. They were described
            // and never drawn.
            prims.Add(Txt(name, onLeft ? -halfW + 15 : halfW - 15, ly,
                fontSize: SddPortLabelFontSize,
                align: onLeft ? SymbolTextAlign.Left : SymbolTextAlign.Right,
                vAlign: SymbolTextVAlign.Middle));
        }

        return Sym(prims, SymbolKind.VerilogA, n);
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

    // ── MEAS — port-less box with "=" motif (measurement equations) ──────────

    private static Symbol BuildMeas() => Sym([
        L(-80, -60,  80, -60),   // top
        L( 80, -60,  80,  60),   // right
        L( 80,  60, -80,  60),   // bottom
        L(-80,  60, -80, -60),   // left
        L(-40, -15,  40, -15),   // upper equals bar
        L(-40,  15,  40,  15),   // lower equals bar
    ], SymbolKind.Meas);

    // ── Mutual — 0-port coupling annotation: letter M + two outward-curved arrows ──
    // No electrical pins. Two arcs (center=(0,110), R=110) flank the M glyph.
    // Arc 1: 230°→255° (left side).  Arc 2: 285°→310° (right side).
    // Arrowheads computed so each triangle base is exactly orthogonal to its arc
    // at the outer endpoint (230° and 310°).  CW-tangent at θ = (−sin θ, cos θ).
    //   Left  tip=(−75,30); base corners=(−66,9) and (−54,25).
    //   Right tip=( 75,30); base corners=( 66,9) and ( 54,25).
    // RoundedRect frames the full content (M + arcs + arrowheads) with ~15 u margin.

    private static Symbol BuildMutual() => Sym([
        Txt("M", 0, 0, fontSize: 36, align: SymbolTextAlign.Center, vAlign: SymbolTextVAlign.Middle),
        RRect(0, 5, 200, 80, 8),
        A(0, 110, 110, 230, 25),   // left arc
        A(0, 110, 110, 285, 25),   // right arc
        Poly(true, -75,30, -66,9, -54,25),   // left arrowhead
        Poly(true,  75,30,  66,9,  54,25),   // right arrowhead
    ], SymbolKind.Mutual);

    // ── TLIN — ideal lossless transmission line: horizontal 2-port box with leads ──
    // Pins: (−200,0) left / (+200,0) right (horizontal, ground-referenced).
    // A rounded-rect body (read as a line segment) with two horizontal leads. A thin
    // centre line through the body evokes the conductor; the box distinguishes it from
    // the generic 2-port and the lumped elements.
    private static Symbol BuildTline() => Sym([
        L(-200,   0,  -90,   0),            // left lead
        L(  90,   0,  200,   0),            // right lead
        RRect( 0,  0,  180,  90,  18),      // body (rounded rect, x∈[−90,90], y∈[−45,45])
        L( -60,   0,   60,   0),            // centre conductor line through the body
    ], SymbolKind.Tline);

    // ── Microstrip built-ins (brief-L5a-pcell-contract-and-microstrip.md) ──────────
    // Every body element below is an UNFILLED RoundedRectPrimitive (RRect never sets Filled, which
    // defaults false) — the same "give it thickness, no fill" convention BuildTline already uses,
    // applied consistently across all four so they read as one family rather than TLIN-plus-three-
    // odd-ones-out.

    private static Symbol BuildMlin() => Sym([
        L(-200,   0,  -90,   0),
        L(  90,   0,  200,   0),
        RRect(0, 0, 180, 60, 12),   // trace body with thickness, unfilled
    ], SymbolKind.Mlin);

    // A real right-angle bend — pin 1 left (input arm, R-pc-3's own origin/+X convention), pin 2
    // DOWN so wiring to it is a natural vertical run. The body is ONE unfilled outline polygon (the
    // union of the horizontal and vertical arms, mitered at the corner) rather than two overlapping
    // RoundedRects — two independently-stroked rects sharing a corner region drew crossing/
    // overlapping lines there; a single traced outline reads as one continuous bent trace.
    private static Symbol BuildMBend() => Sym([
        L(-200,   0, -180,   0),
        Poly(false, -180,-25,  25,-25,  25,180,  -25,180,  -25,25,  -180,25),   // L-shaped body outline
        L(   0, 180,    0, 200),
    ], SymbolKind.MBend);

    // Through line (pins 1/2, left/right) + branch (pin 3, DOWN — +Y is down in this codebase) —
    // R-pc-3's own "pin 1 origin, through +X to pin 2, branch +Y to pin 3" convention. The body is
    // ONE unfilled outline polygon (the union of the through-line and branch arms) — no overlapping
    // RoundedRects, no filled junction dot; the T-shaped outline itself is unambiguous.
    private static Symbol BuildMTee() => Sym([
        L(-200,    0, -180,    0),
        L( 180,    0,  200,    0),
        L(   0,  180,    0,  200),
        Poly(false, -180,-25,  180,-25,  180,25,  25,25,  25,180,  -25,180,  -25,25,  -180,25),   // T-shaped body outline
    ], SymbolKind.MTee);

    // Four arms (right/up/left/down), traced as ONE unfilled cross-shaped outline polygon — no
    // overlapping RoundedRects, no filled junction dot; the plus-sign outline is unambiguous.
    private static Symbol BuildMCross() => Sym([
        L( 180,    0,  200,    0),
        L(-200,    0, -180,    0),
        L(   0, -200,    0, -180),
        L(   0,  180,    0,  200),
        Poly(false, -25,-180,  25,-180,  25,-25,  180,-25,  180,25,  25,25,  25,180,  -25,180,  -25,25,  -180,25,  -180,-25,  -25,-25),   // cross-shaped body outline
    ], SymbolKind.MCross);

    // MTaper — a trapezoid body (thicker at pin 1/W1, tapering to pin 2/W2), unfilled outline —
    // the glyph's own visual taper is symbolic (not to the instance's actual W1/W2 ratio).
    private static Symbol BuildMtaper() => Sym([
        L(-200,   0,  -90,   0),
        L(  90,   0,  200,   0),
        Poly(false, -90,-40,  90,-20,  90,20,  -90,40),   // trapezoid body outline, wide->narrow
    ], SymbolKind.Mtaper);

    // MKlopf — the Klopfenstein taper's own body outline is bowed (S-shaped), not a straight
    // trapezoid, distinguishing it from MTaper's linear glyph — symbolic only, not the instance's
    // actual profile (which the real physics computes per brief-mtaper-mklopf.md §2).
    private static Symbol BuildMklopf() => Sym([
        L(-200,   0,  -90,   0),
        L(  90,   0,  200,   0),
        QC(-90,-40,   0,-25,   90,-20),
        L(90,-20,  90,20),
        QC( 90, 20,   0, 15,  -90, 40),
        L(-90,40,  -90,-40),
    ], SymbolKind.Mklopf);

    // ── Tuner — compact almost-square termination, single left pin ────────────
    // 220 × 200 box (edges ±110 / ±100) — nearly square. Advanced users want a small footprint,
    // not a detailed pictorial; keep the interior mark minimal. The tuning dial is centered in the
    // box. Pin "1" (DUT-facing) at (−300,0); the reference net is implicit ground (hard-coded "0"
    // at extraction — deferred to expose).
    private static Symbol BuildTuner() => Sym([
        L(-300,   0, -110,   0),          // left lead to box edge (DUT-facing pin)
        RRect(0,  0,  220,  200,  20),    // almost-square body, 220 × 200
        Circ(0,   0,   40),               // tuning dial (a Smith-ish circle), centered
        L(0, 0, 28, -28),                 // short "slug" needle
    ], SymbolKind.Tuner);

    // ── Source Tuner — wider box + P1Tone-style source-drive circle; single RIGHT pin ──
    // 400 × 200. The drive circle + 1-cycle sine (borrowed from P1Tone) marks that a source
    // tuner OWNS its internal RF drive (loadpull.md §1.1). The drive source sits on the PIN side
    // (right, nearest the DUT); the tunable-Γ dial is on the far (left) side. Pin "1" at (+300,0).
    private static Symbol BuildSourceTuner() => Sym([
        L( 200,   0,  300,   0),           // right lead → DUT-facing pin
        RRect(0,  0,  400,  200,  20),     // wider body
        Circ( 90,  0,  48),                // source-drive circle (P1Tone motif) — pin side (right)
        Sine( 90,  0,  20,   1,   90, SineAxis.Horizontal),
        Circ(-90,  0,  40),                // tunable-Γ mark — far side (left)
        L(-90, 0, -62, -28),               // slug needle
    ], SymbolKind.SourceTuner);

    // ── Load Tuner — wider box, passive (no drive circle); single LEFT pin ────
    // 400 × 200. Passive termination → NO drive circle. The termination zigzag (passive load) sits
    // on the PIN side (left, nearest the DUT); the tunable-Γ dial is on the far (right) side.
    // Pin "1" (DUT-facing) at (−300,0).
    private static Symbol BuildLoadTuner() => Sym([
        L(-300,   0, -200,   0),           // left lead → DUT-facing pin
        RRect(0,  0,  400,  200,  20),     // wider body
        PLine(-90,-44, -90,-28, -70,-14, -110,12, -70,36, -90,50, -90,55),  // termination zigzag — pin side (left)
        Circ( 90,  0,  40),                // tunable-Γ mark — far side (right)
        L(90, 0, 118, -28),                // slug needle
    ], SymbolKind.LoadTuner);

    /// <summary>
    /// Per-instance Tuner-family symbol. <paramref name="showBias"/> appends the embedded bias-tee +
    /// DC-supply annotation (loadpull.md §1.1) beneath the box. DISPLAY-ONLY — never changes the
    /// extracted netlist; the bias-tee hardware is identical across the three kinds, so the same add-on
    /// is appended to whichever base glyph (general / Source / Load) the kind selects. Cached per
    /// (kind, showBias), mirroring the SnP per-instance symbol path.
    /// </summary>
    public static Symbol PrimitivesForTuner(SymbolKind kind, bool showBias)
    {
        var key = (kind, showBias);
        if (!_tunerCache.TryGetValue(key, out var sym))
            _tunerCache[key] = sym = BuildTunerVariant(kind, showBias);
        return sym;
    }

    private static Symbol BuildTunerVariant(SymbolKind kind, bool showBias)
    {
        var baseSym = kind switch
        {
            SymbolKind.SourceTuner => _sourceTuner,
            SymbolKind.LoadTuner   => _loadTuner,
            _                      => _tuner,
        };
        if (!showBias) return baseSym;
        var prims = new List<SymbolPrimitive>(baseSym.Primitives);
        prims.AddRange(BiasTeeAddOn());
        return new Symbol(prims, baseSym.Pins);
    }

    // ── Shared bias-tee add-on — RF choke (2 coils) + Vdc two-bar supply ───────
    // Drawn beneath the box (bottom edge at y=+100), dropping straight down from box-bottom center.
    // Annotates that the tuner carries its own bias supply; identical for all three tuner kinds.
    private static IReadOnlyList<SymbolPrimitive> BiasTeeAddOn() =>
    [
        L(0, 100,  0, 124),                 // tee stub from box bottom
        A(0, 136, 12, -90, 180),            // choke coil 1
        A(0, 160, 12, -90, 180),            // choke coil 2
        L(0, 172,  0, 196),                 // lead to DC supply
        L(-22, 196, 22, 196),               // long bar (+)
        L(-12, 212, 12, 212),               // short bar (−)
        L(0, 212,  0, 236),                 // bottom tail
    ];

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
