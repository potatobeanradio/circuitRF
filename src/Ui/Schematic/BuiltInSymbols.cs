// Single source of symbol geometry for all built-in symbol kinds.
// 2-terminal symbols (R/L/C/V/Tone/Term/GND) are VERTICAL: pins (0,∓200), leads on x=0.
// Box symbols (FET/ZPort/Sdd/Generic) stay HORIZONTAL: ports left/right.
// Geometry spec: docs/design/standard-library-symbols.md
//
// Framework-free — no Skia / Avalonia references.

using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Matching;

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
    /// <summary>
    /// Font size for the mixer's RF/LO/IF port names. Larger than
    /// <see cref="SddPortLabelFontSize"/> because these sit OUTSIDE the body, on the leads, where
    /// there is room — and because they carry more weight than a port number does: the mixer's
    /// three leads are not interchangeable, and a reader who connects the wrong one gets a circuit
    /// that solves and is wrong.
    /// </summary>
    public const double MixerPortFontSize = 30.0;
    /// <summary>Font size for the "VAR" body label.</summary>
    public const double VarLabelFontSize = 48.0;

    // ── Static caches — built once at first access ────────────────────────────

    private static readonly Symbol _resistor     = BuildResistor();
    private static readonly Symbol _inductor     = BuildInductor();
    private static readonly Symbol _capacitor    = BuildCapacitor();
    private static readonly Symbol _srlc         = BuildSrlc();
    private static readonly Symbol _prlc         = BuildPrlc();
    private static readonly Symbol _vdcSrc       = BuildVdc();
    private static readonly Symbol _toneSrc      = BuildToneSource();
    private static readonly Symbol _iToneSrc     = BuildCurrentToneSource();
    private static readonly Symbol _vccs         = BuildVccs();
    private static readonly Symbol _vcvs         = BuildVcvs();
    private static readonly Symbol _mixer        = BuildMixer();
    private static readonly Symbol _mixerD       = BuildMixerD();
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
    private static readonly Symbol _match         = BuildMatch();
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
    private static readonly Symbol _fet           = BuildFet(nChannel: true);
    private static readonly Symbol _pfet          = BuildFet(nChannel: false);
    private static readonly Symbol _jfetN         = BuildJfet(nChannel: true);
    private static readonly Symbol _jfetP         = BuildJfet(nChannel: false);
    private static readonly Symbol _igbtN         = BuildIgbt(nChannel: true);
    private static readonly Symbol _igbtP         = BuildIgbt(nChannel: false);
    private static readonly Symbol _bead          = BuildBead();
    private static readonly Symbol _vdmosN        = BuildVdmos(nChannel: true);
    private static readonly Symbol _vdmosP        = BuildVdmos(nChannel: false);
    private static readonly Symbol _mosN          = BuildMos(nChannel: true);
    private static readonly Symbol _mosP          = BuildMos(nChannel: false);
    private static readonly Symbol _bjtNpn        = BuildBjt(npn: true);
    private static readonly Symbol _bjtPnp        = BuildBjt(npn: false);

    // ── System blocks (brief-sys-1) ───────────────────────────────────────────
    // Six are fixed and cached once here; the other four vary per instance and are cached per
    // variant beside the Match cache below.
    private static readonly Symbol _balun         = BuildBalun();
    private static readonly Symbol _amp           = BuildAmp();
    private static readonly Symbol _coupler       = BuildCoupler(SymbolKind.Coupler);
    private static readonly Symbol _hybrid90      = BuildCoupler(SymbolKind.Hybrid90);
    private static readonly Symbol _hybrid180     = BuildCoupler(SymbolKind.Hybrid180);
    private static readonly Symbol _atten         = BuildAtten();
    private static readonly Symbol _duplexer      = BuildDuplexer();

    // Per-N cache for variadic box symbols (SDD and ZPort share body geometry).
    private static readonly Dictionary<int, Symbol> _sddCache   = new();
    private static readonly Dictionary<int, Symbol> _zportCache = new();
    // SnP cache key: (n, refNode, cfg, pitch)
    private static readonly Dictionary<(int, bool, SnpPinConfig, SnpPitch), Symbol> _snpCache = new();
    private static readonly Dictionary<int, Symbol> _verilogACache = new();
    // Tuner-family cache key: (kind, showBias) — per-instance bias-branch variant.
    private static readonly Dictionary<(SymbolKind, bool), Symbol> _tunerCache = new();
    // Match cache key: (form, bandCount) — per-instance glyph variant (match.md §8.4).
    private static readonly Dictionary<(NetworkForm, int), Symbol> _matchCache = new();
    // The four DYNAMIC system glyphs, cached per variant exactly as Match is: a circulator shows
    // which way it turns, a switch shows the position it is set to, and a filter shows what it
    // passes. Each is small and closed, so the whole variant set is built at most once.
    private static readonly Dictionary<CirculatorDirection, Symbol> _circulatorCache = new();
    private static readonly Dictionary<SwitchState, Symbol>        _switchCache      = new();
    private static readonly Dictionary<SwitchThrow, Symbol>        _switchDCache     = new();
    private static readonly Dictionary<NetworkForm, Symbol>        _filterCache      = new();

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
            // SpiceModel answers here with the UNCONFIGURED glyph, the same contract the four
            // dynamic system blocks keep: a caller with no instance to ask — the palette tile, the
            // placement ghost, a documentation figure — still gets a real drawing. A PLACED instance
            // never reaches this: its symbol comes from the file, through
            // SpiceModelSymbolProvider, by way of the ordinary external-symbol-reference path.
            case SymbolKind.SpiceModel:
                return SpiceModelSymbolProvider.UnconfiguredSymbol(SnpPinConfig.Standard, SnpPitch.Loose);
            case SymbolKind.Resistor:   return _resistor;
            case SymbolKind.Inductor:   return _inductor;
            case SymbolKind.Capacitor:  return _capacitor;
            case SymbolKind.Srlc:       return _srlc;
            case SymbolKind.Prlc:       return _prlc;
            case SymbolKind.NonlinearC: return _nonlinearC;
            case SymbolKind.Mutual:     return _mutual;
            case SymbolKind.Tline:      return _tline;
            case SymbolKind.Match:      return _match;
            case SymbolKind.Mlin:       return _mlin;
            case SymbolKind.MBend:      return _mbend;
            case SymbolKind.MTee:       return _mtee;
            case SymbolKind.MCross:     return _mcross;
            case SymbolKind.Mtaper:     return _mtaper;
            case SymbolKind.Mklopf:     return _mklopf;
            case SymbolKind.Vdc:        return _vdcSrc;
            case SymbolKind.ToneSource: return _toneSrc;
            case SymbolKind.CurrentToneSource: return _iToneSrc;
            case SymbolKind.Vccs:       return _vccs;
            case SymbolKind.Vcvs:       return _vcvs;
            case SymbolKind.Mixer:      return _mixer;
            case SymbolKind.MixerD:     return _mixerD;
            case SymbolKind.Balun:      return _balun;
            case SymbolKind.Amp:        return _amp;
            case SymbolKind.Coupler:    return _coupler;
            case SymbolKind.Hybrid90:   return _hybrid90;
            case SymbolKind.Hybrid180:  return _hybrid180;
            case SymbolKind.Atten:      return _atten;
            case SymbolKind.Duplexer:   return _duplexer;
            // The four dynamic ones answer here with their DEFAULT variant — the same contract
            // Match keeps, so a caller that has no instance to ask (the palette tile, the ghost
            // preview, the documentation figure) still gets a real drawing.
            case SymbolKind.Circulator: return PrimitivesForCirculator(CirculatorDirection.CW);
            case SymbolKind.Switch:     return PrimitivesForSwitch(SwitchState.On);
            case SymbolKind.SwitchD:    return PrimitivesForSwitchD(SwitchThrow.T1);
            case SymbolKind.Filter:     return PrimitivesForFilter(NetworkForm.Bandpass);
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
            // The p-channel laws share one glyph with each other and none with the n-channel ones:
            // the gate arrow is the only thing on the drawing that says which channel this is.
            case SymbolKind.PFetCurtice:
            case SymbolKind.PFetStatz:
            case SymbolKind.PFetMaterka: return _pfet;
            // The two BJT polarities do NOT share a glyph, unlike the five FET laws above. The
            // emitter arrow is the only thing that distinguishes an n-p-n from a p-n-p on a
            // schematic, and it is the first thing a reader looks for.
            // The two MOS levels share one glyph per CHANNEL, and the two channels do not share
            // with each other. Same split, same reasons, as the families above: the topology is
            // identical across levels and the type label names the law, while the bulk arrow is the
            // only thing on the drawing that says which channel this is.
            // The two JFET channels do not share a glyph either, and for the same reason: the
            // gate arrow is the only thing on the drawing that says which one this is.
            case SymbolKind.JfetN:       return _jfetN;
            case SymbolKind.JfetP:       return _jfetP;
            case SymbolKind.IgbtN:       return _igbtN;
            case SymbolKind.IgbtP:       return _igbtP;
            case SymbolKind.Bead:        return _bead;
            case SymbolKind.VdmosN:      return _vdmosN;
            case SymbolKind.VdmosP:      return _vdmosP;
            case SymbolKind.Mos1N:
            case SymbolKind.Mos3N:       return _mosN;
            case SymbolKind.Mos1P:
            case SymbolKind.Mos3P:       return _mosP;
            case SymbolKind.BjtNpn:      return _bjtNpn;
            case SymbolKind.BjtPnp:      return _bjtPnp;
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

    /// <summary>
    /// A built-in symbol's text. <b><c>ForceReadable</c> is ON by default here</b> — every built-in
    /// label is a WORD to be read ("1", "2", "Ref", "VAR", "d+"), never a mark whose orientation
    /// carries meaning, so a rotated instance must keep it right side up rather than spinning it
    /// rigidly (owner, 2026-08-26 — an SnP rotated 180 degrees rendered its port numbers upside
    /// down; SDD, ZPort and the generic device box all had it too, since all four share this helper).
    ///
    /// <para>The flip is 180 degrees about the text's OWN box centre, so nothing moves: a label
    /// stays at the body edge it was authored against, which after a 180 degree instance rotation is
    /// the edge its port has moved to. The <c>+</c>/<c>−</c> polarity marks pass through it
    /// unchanged, both being symmetric under that flip.</para>
    ///
    /// <para>Authored <c>.csym</c> text is NOT touched — <c>ForceReadable</c> stays per-primitive and
    /// off by default there, because a symbol's author may have meant the rotation. The symbol editor
    /// shows the literal authored orientation either way.</para>
    /// </summary>
    private static TextPrimitive Txt(string content, double ax, double ay,
                                      double fontSize = 12,
                                      SymbolTextAlign align = SymbolTextAlign.Center,
                                      SymbolTextVAlign vAlign = SymbolTextVAlign.Middle,
                                      SymbolColorRole colorRole = SymbolColorRole.SymbolLine,
                                      bool forceReadable = true)
        => new() { Content = content, AnchorX = ax, AnchorY = ay,
                   FontSize = fontSize, Align = align, VAlign = vAlign, ColorRole = colorRole,
                   ForceReadable = forceReadable };

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

    // ── SRLC — R over L over C, in series, on one branch ──────────────────────
    // Pins: (0,-200) top / (0,+200) bottom — IDENTICAL to R, L and C, so a designer can swap a
    // plain element for this one without moving a wire. That is the whole point of the kind.
    //
    // The three glyphs are the SAME pictures the standalone R, L and C draw, shrunk to share one
    // 400-unit span: the resistor loses two of its six zigs (4 remain, amplitude +/-25 against 30),
    // the inductor loses a coil and 5 units of radius (3 at r=20 against 4 at r=25), and the
    // capacitor keeps its flat-plate-plus-curve exactly, at 60 wide against 100. Nothing is a new
    // picture — a reader who knows the library recognises all three at a glance, stacked in the
    // order the netlist stamps them.
    //
    // The polarity dot is the inductor's own, carried over: this kind's L can be one end of a
    // Mutual, and the dot is what says which end.

    private static Symbol BuildSrlc() => Sym([
        L(   0, -200,   0, -160),                             // top lead
        PLine(0, -160,  25, -150, -25, -130,  25, -110,
                       -25,  -90,   0,  -80),                 // R — 4 zigs, amp +/-25
        L(   0,  -80,   0,  -60),                             // R -> L link
        A(   0,  -40,  20, -90, 180),                         // coil 1
        A(   0,    0,  20, -90, 180),                         // coil 2
        A(   0,   40,  20, -90, 180),                         // coil 3
        Circ(30,  -55,   5, filled: true),                    // polarity dot (L's own convention)
        L(   0,   60,   0,  110),                             // L -> C link
        L( -30,  110,  30,  110),                             // flat top plate
        QC(-30,  133,   0,  119,  30,  133),                  // curved bottom plate (apex y=126)
        L(   0,  126,   0,  200),                             // bottom lead (from curve apex)
    ], SymbolKind.Srlc);

    // ── PRLC — R, L and C side by side, in parallel between two rails ─────────
    // Pins: (0,-200) top / (0,+200) bottom — again identical to R, L and C.
    //
    // Same three borrowed pictures, laid out left-to-right instead of top-to-bottom, hung between a
    // top and a bottom rail at y = -/+150. The resistor keeps its full +/-30 zig amplitude here (it
    // has the room sideways) but only 4 zigs; the branch spacing of 80 and the capacitor's 60-wide
    // plates put the glyph's extent at exactly +/-110, symmetric about the lead.

    private static Symbol BuildPrlc() => Sym([
        L(   0, -200,   0, -150),                             // top lead
        L( -80, -150,  80, -150),                             // top rail
        L( -80,  150,  80,  150),                             // bottom rail
        L(   0,  150,   0,  200),                             // bottom lead

        // R — leftmost branch
        L( -80, -150, -80,  -60),
        PLine(-80, -60, -50, -45, -110, -15, -50,  15,
                       -110,  45, -80,  60),                  // 4 zigs, amp +/-30
        L( -80,   60, -80,  150),

        // L — middle branch, on the lead's own axis
        L(   0, -150,   0,  -60),
        A(   0,  -40,  20, -90, 180),                         // coil 1
        A(   0,    0,  20, -90, 180),                         // coil 2
        A(   0,   40,  20, -90, 180),                         // coil 3
        Circ(30,  -55,   5, filled: true),                    // polarity dot
        L(   0,   60,   0,  150),

        // C — rightmost branch
        L(  80, -150,  80,  -12),
        L(  50,  -12, 110,  -12),                             // flat top plate
        QC( 50,   22,  80,    2, 110,   22),                  // curved bottom plate (apex y=12)
        L(  80,   12,  80,  150),
    ], SymbolKind.Prlc);

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

    private static Symbol BuildFet(bool nChannel) => Sym([
        L(-200,    0, -135,    0),                    // gate lead (up to the arrow base)
        nChannel ? Poly(true, -135, -32, -135,  32,  -80,   0)   // n-channel: tip on the gate bar
                 : Poly(true,  -80, -32,  -80,  32, -135,   0),  // p-channel: tip toward the gate
        L( -80, -110,  -80,  110),                    // gate bar
        L( -30, -110,  -30,  110),                    // channel bar
        L( -30,  -80,    0,  -80),                    // drain arm off the channel
        L(   0,  -80,    0, -200),                    // drain lead
        L( -30,   80,    0,   80),                    // source arm off the channel
        L(   0,   80,    0,  200),                    // source lead
    ], nChannel ? SymbolKind.FetCurtice : SymbolKind.PFetCurtice);

    // ── BJT — base bar, collector and emitter arms, arrow on the emitter ──────
    // Pins: base (-200,0) LEFT, collector (0,-200) TOP, emitter (0,+200) BOTTOM. Same envelope and
    // the same lead lengths as the FET glyph, so the two families sit at one scale in the palette.
    //
    // The ARROW IS THE POLARITY and there is no other cue: it sits on the emitter arm and points
    // away from the base for an n-p-n, into it for a p-n-p — conventional current, out of the
    // emitter of an n-p-n. The arm runs from (-80,55) to (0,110); the arrowhead is built along that
    // direction rather than axis-aligned, so it lies ON the lead instead of beside it.

    private static Symbol BuildBjt(bool npn) => Sym([
        L(-200,    0,  -80,    0),                    // base lead
        L( -80, -110,  -80,  110),                    // base bar
        L( -80,  -55,    0, -110),                    // collector arm
        L(   0, -110,    0, -200),                    // collector lead
        L( -80,   55,    0,  110),                    // emitter arm
        L(   0,  110,    0,  200),                    // emitter lead
        npn ? Poly(true, -54,  95, -34,  65, -16,  99)    // n-p-n: tip toward the emitter
            : Poly(true, -46, 100, -26,  70, -64,  66),   // p-n-p: tip toward the base
    ], npn ? SymbolKind.BjtNpn : SymbolKind.BjtPnp);

    // ── JFET — gate arrow straight onto an UNBROKEN channel bar ───────────────
    // Pins: drain (0,−200) TOP, gate (−200,0) LEFT, source (0,+200) BOTTOM. Same envelope and the
    // same lead lengths as the FET, MOS and BJT glyphs, so the four transistor families sit at one
    // scale in the palette.
    //
    // What distinguishes it from the MESFET glyph beside it is what distinguishes the devices: the
    // MESFET has TWO vertical bars (a gate bar standing off an insulated channel) and this has ONE,
    // because a JFET's gate is a junction made directly onto the channel. The arrowhead sits at the
    // end of the gate lead, ON the channel bar, which is where the junction is.
    //
    // The channel bar is UNBROKEN — a depletion device, conducting at zero gate bias, which is the
    // opposite of the MOS glyph's three segments. The arrow IS the channel polarity and there is no
    // other cue: it points INTO the channel for n-channel, out of it for p-channel.

    private static Symbol BuildJfet(bool nChannel) => Sym([
        L(-200,    0,  -30,    0),                    // gate lead, right onto the channel
        L( -30, -110,  -30,  110),                    // channel bar, unbroken
        L( -30,  -85,    0,  -85),                    // drain arm
        L(   0,  -85,    0, -200),                    // drain lead
        L( -30,   85,    0,   85),                    // source arm
        L(   0,   85,    0,  200),                    // source lead
        nChannel ? Poly(true, -90,  -20, -90,  20, -34,   0)    // n-channel: tip on the channel bar
                 : Poly(true, -34,  -20, -34,  20, -90,   0),   // p-channel: tip toward the gate
    ], nChannel ? SymbolKind.JfetN : SymbolKind.JfetP);

    // ── IGBT — an insulated gate on one side, a bipolar's arrow on the other ──
    // Pins: collector (0,−200) TOP, gate (−200,0) LEFT, emitter (0,+200) BOTTOM.
    //
    // The glyph says what the device is, which is the point of it: the input side is the MOS one —
    // a gate bar standing off a broken (enhancement) channel bar — and the output side carries the
    // BIPOLAR's emitter arrow. That arrow is not decoration. An IGBT does NOT conduct in reverse,
    // and a reader who takes it for a power MOSFET will expect a body diode that is not there; the
    // arrow is what stops that reading.

    private static Symbol BuildIgbt(bool nChannel) => Sym([
        L(-200,    0,  -80,    0),                    // gate lead
        L( -80, -110,  -80,  110),                    // gate bar
        L( -30, -110,  -30,  -60),                    // channel: collector segment
        L( -30,  -25,  -30,   25),                    // channel: body segment
        L( -30,   60,  -30,  110),                    // channel: emitter segment
        L( -30,  -85,    0,  -85),                    // collector arm
        L(   0,  -85,    0, -200),                    // collector lead
        L( -30,   85,    0,   85),                    // emitter arm
        L(   0,   85,    0,  200),                    // emitter lead
        // The emitter arrow, built along the arm rather than axis-aligned so it lies ON the lead —
        // the same construction the BJT glyph uses, and pointing the same way for the same reason.
        nChannel ? Poly(true, -18,  75,   2,  75, -10,  99)     // conventional current OUT of the emitter
                 : Poly(true, -30,  73, -10,  73, -22,  97),
    ], nChannel ? SymbolKind.IgbtN : SymbolKind.IgbtP);

    // ── Ferrite bead — a wire passing through a core ──────────────────────────
    // Pins: (0,−200) and (0,+200), the same two every lumped element uses — it falls through
    // SymbolPortDefs' default arm exactly as SRLC and PRLC do, so there is no second copy of the
    // coordinates to drift.
    //
    // The wire runs UNBROKEN from pin to pin, which is the whole of what the glyph has to say: a
    // bead is a conductor threaded through a core, not an element in series with one. At DC it is a
    // piece of wire with the winding resistance of a piece of wire, and drawing it as a body with
    // leads would suggest otherwise. The two hatch strokes are the core.

    private static Symbol BuildBead() => Sym([
        L(   0, -200,   0,  200),           // the wire, straight through
        RRect(0, 0, 90, 150, 30),           // the core around it
        L( -45,  -25,  45,  -55),           // core hatching
        L( -45,   55,  45,   25),
    ], SymbolKind.Bead);

    // ── VDMOS — the MOSFET glyph with its body tied to the source, and the body DIODE drawn ──
    // Pins: drain (0,−200) TOP, gate (−200,0) LEFT, source (0,+200) BOTTOM. No bulk pin.
    //
    // Two things say "power MOSFET" rather than "MOSFET" and both are drawn because both are facts
    // about the circuit:
    //   * the bulk arm turns and joins the SOURCE lead instead of leaving as a fourth pin. That is
    //     the source-to-body short, inside the silicon, and it is why there is no body effect.
    //   * the body DIODE is drawn explicitly, on the right, between the drain and source leads. It
    //     is not decoration: it is the freewheeling path of every half-bridge and it carries the
    //     full load current during dead time. Its arrow is the channel polarity read the usual way
    //     — for n-channel it conducts from source to drain, so it points UP toward the drain.
    //
    // The channel bar is drawn in three segments, the enhancement mark, exactly as the lateral MOS
    // glyph draws it.

    private static Symbol BuildVdmos(bool nChannel) => Sym([
        L(-200,    0,  -80,    0),                    // gate lead
        L( -80, -110,  -80,  110),                    // gate bar
        L( -30, -110,  -30,  -60),                    // channel: drain segment
        L( -30,  -25,  -30,   25),                    // channel: body segment
        L( -30,   60,  -30,  110),                    // channel: source segment
        L( -30,  -85,    0,  -85),                    // drain arm
        L(   0,  -85,    0, -200),                    // drain lead
        L( -30,   85,    0,   85),                    // source arm
        L(   0,   85,    0,  200),                    // source lead
        L( -30,    0,   45,    0),                    // body tie, out and then down to the source
        L(  45,    0,   45,   85),
        L(  45,   85,    0,   85),
        nChannel ? Poly(true,  25,  -20,  25,  20, -25,   0)     // n-channel: tip toward the channel
                 : Poly(true, -25,  -20, -25,  20,  25,   0),    // p-channel: tip toward the body
        // The body diode, tapped off the two leads.
        L(   0, -150,  130, -150),
        L( 130, -150,  130,  -55),
        L( 130,   10,  130,  150),
        L(   0,  150,  130,  150),
        nChannel ? L(100, -55, 160, -55) : L(100,  10, 160,  10),          // cathode bar
        nChannel ? Poly(true, 100,  10, 160,  10, 130, -55)                // conducts source→drain
                 : Poly(true, 100, -55, 160, -55, 130,  10),               // …and the other way
    ], nChannel ? SymbolKind.VdmosN : SymbolKind.VdmosP);

    // ── MOSFET — insulated gate, a BROKEN channel bar, and a bulk arm on the right ──
    // Pins: drain (0,−200) TOP, gate (−200,0) LEFT, source (0,+200) BOTTOM, bulk (+200,0) RIGHT.
    // Same envelope and the same lead lengths as the FET and BJT glyphs, so the three transistor
    // families sit at one scale in the palette.
    //
    // TWO things distinguish this from the MESFET glyph beside it, and both are load-bearing:
    //   * the channel bar is drawn in THREE SEGMENTS rather than one. That is the standard mark for
    //     an ENHANCEMENT device — no channel until the gate makes one — and it is what says this
    //     part is off at zero gate bias, which the MESFET is not.
    //   * there is a FOURTH lead, out to the right, for the bulk. The arrow on it is the channel
    //     polarity and there is no other cue: it points INTO the channel for n-channel, out of it
    //     for p-channel, which is the junction it stands for read the usual way.
    //
    // The bulk arm meets the channel at its midpoint, between the two outer segments, because that
    // is where the substrate contacts the body — drawing it onto the drain or source end would
    // suggest a connection to one of them, which is the thing the separate pin exists to deny.

    private static Symbol BuildMos(bool nChannel) => Sym([
        L(-200,    0,  -80,    0),                    // gate lead
        L( -80, -110,  -80,  110),                    // gate bar (the insulator gap is the space)
        L( -30, -110,  -30,  -60),                    // channel: drain segment
        L( -30,  -25,  -30,   25),                    // channel: body segment
        L( -30,   60,  -30,  110),                    // channel: source segment
        L( -30,  -85,    0,  -85),                    // drain arm
        L(   0,  -85,    0, -200),                    // drain lead
        L( -30,   85,    0,   85),                    // source arm
        L(   0,   85,    0,  200),                    // source lead
        L( -30,    0,  200,    0),                    // bulk arm and lead, out to the right
        nChannel ? Poly(true,  30,  -20,  30,  20,  -20,   0)    // n-channel: tip toward the channel
                 : Poly(true, -20,  -20, -20,  20,   30,   0),   // p-channel: tip toward the bulk
    ], nChannel ? SymbolKind.Mos1N : SymbolKind.Mos1P);

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

    // ── ITone / current tone source — the SAME circle-and-sine body as VTone, so the two read as
    // one family, with the polarity marks replaced by an ARROW on the top lead.
    // Pins: (0,-200) top / (0,+200) bottom.
    //
    // The arrow is the whole of the direction cue, and it is the BJT's arrowhead, at the BJT's size
    // and drawn the same way (a filled three-point Poly lying ON the lead rather than beside it).
    // It points at pin 1 because that is where a positive I is DELIVERED — the engine's
    // "a current source injects into its first node" convention, drawn.
    //
    // Deliberately NOT the textbook circle-with-an-arrow-inside: the body is 120 across and already
    // carries the sine that says "tone", and an arrowhead inside it would either collide with the
    // sine or shrink to something unreadable at palette size. On the lead it is legible at every
    // zoom and cannot be mistaken for the AC mark.

    private static Symbol BuildCurrentToneSource() => Sym([
        L(   0, -200,   0,  -60),           // top lead
        Circ(  0,    0,  60),                // body circle (stroked)
        L(   0,   60,   0,  200),           // bottom lead
        Sine(0,    0,  22,    1,   70, SineAxis.Horizontal),  // AC mark — identical to VTone's
        Poly(true, 0, -160, -20, -114, 20, -114),   // current-direction arrowhead, tip toward pin 1
    ], SymbolKind.CurrentToneSource);

    // ── VCCS — ideal voltage-controlled current source ────────────────────────
    // Pins: [0] out+ (0,-200), [1] out- (0,+200), [2] ctrl+ (-300,-100), [3] ctrl- (-300,+100).
    //
    // The DIAMOND is the universal "dependent source" body — the one mark that separates a
    // controlled source from an independent one at a glance — and the arrow inside it is the BJT's
    // arrowhead again, pointing DOWN at out−: a positive G·Vc flows IN at out+ and OUT at out−, the
    // way a small-signal transconductance is drawn in every device model (and the SPICE G element's
    // own direction). It therefore points the OPPOSITE way to ITone's, which is an independent
    // source and delivers into its first pin — read each glyph's own arrow, do not carry one over.
    //
    // The control leads STOP SHORT of the diamond (they end at x=-170, the diamond's left vertex is
    // at x=-90) and are marked + and −. That gap is the drawing: the control pair senses voltage and
    // carries no current, so a lead touching the body would draw exactly the connection this device
    // does not have.

    private static Symbol BuildVccs() => Sym([
        L(   0, -200,    0,  -90),          // output lead, top (out+)
        L(   0,   90,    0,  200),          // output lead, bottom (out-)
        Poly(false, 0, -90,  90, 0,  0, 90,  -90, 0),   // diamond body (stroked, closed)
        L(   0,  -58,    0,   22),          // arrow shaft, pointing down toward out−
        Poly(true, 0,  62, -18,  20, 18,  20),          // arrowhead, tip toward out−
        L(-300, -100, -170, -100),          // control lead, upper (ctrl+)
        L(-300,  100, -170,  100),          // control lead, lower (ctrl-)
        Txt("+", -150, -100, fontSize: PolarityFontSize, colorRole: SymbolColorRole.SymbolPlus),
        Txt("−", -150, +100, fontSize: PolarityFontSize),
    ], SymbolKind.Vccs);

    // ── VCVS — the same diamond, carrying a voltage instead of a current ──────
    //
    // The body, the leads and the stopped-short control pair are the VCCS's exactly, because the two
    // differ in one thing only and a reader who knows one should recognise the other at a glance.
    // What differs is what sits INSIDE the diamond: a controlled voltage source is drawn with a ±
    // pair down its axis, not an arrow, because there is no current direction to point at — the
    // element states a potential difference and its current is whatever the rest of the circuit
    // draws through it.

    private static Symbol BuildVcvs() => Sym([
        L(   0, -200,    0,  -90),          // output lead, top (out+)
        L(   0,   90,    0,  200),          // output lead, bottom (out-)
        Poly(false, 0, -90,  90, 0,  0, 90,  -90, 0),   // diamond body (stroked, closed)
        Txt("+",   0,  -40, fontSize: PolarityFontSize, colorRole: SymbolColorRole.SymbolPlus),
        Txt("−", 0,  40, fontSize: PolarityFontSize),
        L(-300, -100, -170, -100),          // control lead, upper (ctrl+)
        L(-300,  100, -170,  100),          // control lead, lower (ctrl-)
        Txt("+", -150, -100, fontSize: PolarityFontSize, colorRole: SymbolColorRole.SymbolPlus),
        Txt("−", -150, +100, fontSize: PolarityFontSize),
    ], SymbolKind.Vcvs);

    // ── Mixer — the universal circle-and-✕ ────────────────────────────────────
    // Pins: RF (-300,0) left · LO (0,+300) bottom · IF (+300,0) right.
    //
    // This glyph is not a choice. A circle with a multiplication sign in it has meant "mixer" in
    // every RF block diagram for sixty years, and it says the one thing about the device that a
    // reader needs: what comes out is the PRODUCT of what goes in. Drawing anything else here would
    // be inventing a private notation for the most standardised symbol in the discipline.
    //
    // The three leads are NOT interchangeable — RF·LO lands on IF and no other assignment of the
    // three does — so each carries its name. That is a departure from the rest of this library,
    // where a 2-terminal part's pins are symmetric or a polarity mark suffices; here a reader who
    // guesses wrong gets a circuit that solves and is wrong, which is the failure worth three
    // small words. LO enters from BELOW, as it does in every block diagram, so a downconverter
    // reads left to right with its local oscillator underneath.

    private static Symbol BuildMixer() => Sym([
        Circ(  0,    0,  120),                  // body circle (stroked)
        L(  -85,  -85,   85,   85),             // ✕, first stroke  (r·√2/2 ≈ 85)
        L(  -85,   85,   85,  -85),             // ✕, second stroke
        L( -300,    0, -120,    0),             // RF lead (left)
        L(  120,    0,  300,    0),             // IF lead (right)
        L(    0,  300,    0,  120),             // LO lead (bottom)
        Txt("RF", -215,  -55, fontSize: MixerPortFontSize),
        Txt("IF",  215,  -55, fontSize: MixerPortFontSize),
        Txt("LO",   75,  235, fontSize: MixerPortFontSize),
    ], SymbolKind.Mixer);

    // ── MixerD — the same device with all six nets exposed ────────────────────
    // Pins: rf+ (-300,-100) rf− (-300,+100) · lo+ (-100,+300) lo− (+100,+300) ·
    //       if+ (+300,-100) if− (+300,+100).
    //
    // A BOX rather than the circle, because six leads cannot land on a circle's edge on the
    // connection grid — they would meet it at irrational offsets and the symbol would stop being
    // drawable in the editor's own coordinates. The ✕ is kept and is the whole of the family
    // resemblance: it is the mark that says "multiplier", and it is why this reads as the same
    // device as the tile beside it rather than as a generic three-port block.
    //
    // Port names go INSIDE, beside their own pair, and the polarity marks are the same "+"/"−"
    // pair in the same SymbolPlus role the VCCS uses — a reader who has learned one ± convention
    // in this library has learned them all.

    private static Symbol BuildMixerD() => Sym([
        RRect( 0,    0,  240,  240,   12),      // body box
        L(  -85,  -85,   85,   85),             // ✕, first stroke
        L(  -85,   85,   85,  -85),             // ✕, second stroke
        L( -300, -100, -120, -100),             // rf+ lead
        L( -300,  100, -120,  100),             // rf− lead
        L(  120, -100,  300, -100),             // if+ lead
        L(  120,  100,  300,  100),             // if− lead
        L( -100,  300, -100,  120),             // lo+ lead
        L(  100,  300,  100,  120),             // lo− lead
        Txt("+", -215, -155, fontSize: PolarityFontSize, colorRole: SymbolColorRole.SymbolPlus),
        Txt("−", -215,  155, fontSize: PolarityFontSize),
        Txt("+",  215, -155, fontSize: PolarityFontSize, colorRole: SymbolColorRole.SymbolPlus),
        Txt("−",  215,  155, fontSize: PolarityFontSize),
        Txt("+", -165,  235, fontSize: PolarityFontSize, colorRole: SymbolColorRole.SymbolPlus),
        Txt("−",  165,  235, fontSize: PolarityFontSize),
        Txt("RF", -215,  -20, fontSize: MixerPortFontSize),
        Txt("IF",  215,  -20, fontSize: MixerPortFontSize),
        Txt("LO",    0,  235, fontSize: MixerPortFontSize),
    ], SymbolKind.MixerD);

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

            // The LEAD from the body edge out to the pin tip. The body is 180 wide and the pins sit
            // at +/-200, so without this there is a 110-unit gap between the box and the point a wire
            // attaches to — the wire ends in mid-air and the symbol reads as broken. Every other
            // box-with-terminals glyph here already draws it (BuildVerilogASymbol, BuildSnpSymbol);
            // the SDD and ZPort were the two that did not (owner, 2026-08-20, from the symbol-editor
            // figure, where a symbol with nothing else in it makes the gap unmissable).
            prims.Add(L(isLeft ? -w * 0.5 : w * 0.5, ly, lx, ly));

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
    /// transistor glyph would assert something untrue on the schematic.
    ///
    /// <para><b>The leads are named by the MODEL where the model has said what they are</b>, and
    /// numbered otherwise. On a five-terminal part — drain, gate, source, bulk, thermal — numbers
    /// are the largest single source of mis-wiring, and the model has already declared each
    /// terminal's own name; drawing <c>1..5</c> over it withholds something circuitRF was told.</para>
    ///
    /// <para><b>This changes what the leads are CALLED, not what the part claims to be.</b> The body
    /// stays the generic box for exactly the reason above — naming a terminal <c>g</c> repeats the
    /// model's own word for it, where drawing a transistor would be circuitRF asserting a device
    /// class nobody stated.</para>
    /// </summary>
    /// <param name="labels">The model's own terminal names, or null/short/blank entries where it
    /// named none — each lead falls back to its number independently, so a model that names three of
    /// five terminals draws three names and two numbers rather than five numbers.</param>
    private static Symbol BuildVerilogASymbol(int n, IReadOnlyList<string>? labels = null)
    {
        var ports = SymbolPortDefs.GenerateGenericDevicePorts(n);

        float top = ports.Min(p => p.LocalY), bottom = ports.Max(p => p.LocalY);
        double cy = (top + bottom) * 0.5;
        double halfH = Math.Max((bottom - top) * 0.5 + 60, 100);
        const double halfW = 110;

        var prims = new List<SymbolPrimitive> { RRect(0, cy, halfW * 2, halfH * 2, 12) };

        for (int i = 0; i < ports.Length; i++)
        {
            var (name, lx, ly) = ports[i];
            bool onLeft = lx < 0;
            prims.Add(L(onLeft ? -halfW : halfW, ly, lx, ly));

            // With a deliberately generic body this text is the ONLY thing telling a user which lead
            // is which — the model's own word for the terminal where it gave one, its number where
            // it did not.
            string text = labels is not null && i < labels.Count
                          && !string.IsNullOrWhiteSpace(labels[i])
                        ? labels[i].Trim()
                        : name;

            prims.Add(Txt(text, onLeft ? -halfW + 15 : halfW - 15, ly,
                fontSize: SddPortLabelFontSize,
                align: onLeft ? SymbolTextAlign.Left : SymbolTextAlign.Right,
                vAlign: SymbolTextVAlign.Middle));
        }

        return Sym(prims, SymbolKind.VerilogA, n);
    }

    /// <summary>
    /// The VerilogA box drawn with a model's own terminal names.
    ///
    /// <para><b>Not cached here, unlike the numbered form.</b> That one is keyed on the terminal
    /// count alone and there are a handful of counts; this varies with the label list too, and the
    /// caller — <c>EditableSchematic.InstanceGlyph</c> — already rebuilds a glyph only when a
    /// parameter changes. GEOMETRY IS IDENTICAL to the numbered form's: same pin coordinates, same
    /// body, so nothing downstream that positions a wire or hit-tests a lead can disagree with it.</para>
    /// </summary>
    public static Symbol PrimitivesForVerilogA(int portCount, IReadOnlyList<string>? labels)
    {
        int n = portCount > 0 ? portCount : 2;

        // Nothing named: hand back the cached numbered symbol rather than building a second identical
        // one, which is the state every component placed before a file was chosen is in.
        if (labels is null || labels.Count == 0 || labels.All(string.IsNullOrWhiteSpace))
            return Primitives(SymbolKind.VerilogA, n);

        return BuildVerilogASymbol(n, labels);
    }

    /// <summary>
    /// The SnP box, drawn around port names the CALLER supplies rather than port numbers —
    /// what a <see cref="SymbolKind.SpiceModel"/> pointed at a <c>.subckt</c> shows
    /// (<see cref="SpiceModelSymbolProvider"/>).
    ///
    /// <para><b>Geometry is SnP's own, not a second copy of it.</b> Pin positions, body height and
    /// body centre all come from the same <see cref="SymbolPortDefs"/> calls
    /// <see cref="BuildSnpSymbol"/> makes, so the Pins/Pitch options mean on a subcircuit exactly
    /// what they mean on a Touchstone file — which is the whole reason those two controls are
    /// offered on both. Only the WIDTH differs: a subcircuit's ports are named (<c>vdd</c>,
    /// <c>bulk</c>) where a Touchstone's are numbered, and SnP's 200-wide body would print two
    /// four-letter names over each other.</para>
    ///
    /// <para>Not cached here: the caller keys its own cache on the file's mtime, which is the thing
    /// that can actually change. Caching by name list underneath that would hold a second copy of
    /// every symbol for no additional hit.</para>
    /// </summary>
    public static Symbol PrimitivesForNamedPortBox(
        IReadOnlyList<string> portNames, SnpPinConfig cfg, SnpPitch pitch)
    {
        ArgumentNullException.ThrowIfNull(portNames);
        int n = Math.Max(portNames.Count, 1);

        var geometry = SymbolPortDefs.GenerateSnpPorts(n, refNode: false, cfg, pitch);
        var ports = new (string Name, float LocalX, float LocalY)[geometry.Length];
        for (int i = 0; i < geometry.Length; i++)
            ports[i] = (i < portNames.Count && !string.IsNullOrWhiteSpace(portNames[i])
                            ? portNames[i]
                            : geometry[i].Name,
                        geometry[i].LocalX, geometry[i].LocalY);

        // Wide enough for the two longest names that can face each other across the body, and never
        // wider than the pins it has to stay inside of (they sit at ±200, and a lead of at least 30
        // has to remain visible or the box reads as touching the wire).
        int longest = ports.Max(q => q.Name.Length);
        double halfW = Math.Clamp(60 + longest * 9.0, 100, 170);

        return BuildPortBox(ports, n, cfg, pitch, halfW * 2);
    }

    private static Symbol BuildSnpSymbol(int n, bool refNode, SnpPinConfig cfg, SnpPitch pitch)
    {
        var ports = SymbolPortDefs.GenerateSnpPorts(n, refNode, cfg, pitch);
        return BuildPortBox(ports, n, cfg, pitch, SymbolPortDefs.SnpBodyRect(n, cfg, pitch).W);
    }

    /// <summary>
    /// The shared body-and-leads drawing for every box-shaped N-port glyph: SnP's numbered ports and
    /// a SpiceModel subcircuit's named ones. <paramref name="ports"/> supplies the pins as placed;
    /// <paramref name="n"/> is the SIGNAL port count (a Ref pin is not one of them) and is what the
    /// height and centre are measured from.
    /// </summary>
    private static Symbol BuildPortBox(
        (string Name, float LocalX, float LocalY)[] ports,
        int n, SnpPinConfig cfg, SnpPitch pitch, double w)
    {
        float halfH = SymbolPortDefs.SnpBodyRect(n, cfg, pitch).HalfH;
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
    // No electrical pins. Two gentle curves flank the M glyph, each a QuadCurve tracing what used
    // to be a 25°-wide slice of a huge (center=(0,110), R=110) Arc — same endpoints, same ~2.6-unit
    // sagitta, visually identical, but NOT an ArcPrimitive any more: SymbolGeometry.ComputeBb bounds
    // an Arc by its full CONTAINING CIRCLE (conservative, correct for an arc that is a real fraction
    // of its circle), and for a 25° sliver of an R=110 circle that circle's own box (y: 0→220) swamps
    // the glyph's real content (y: −35→45), pulling every ComputeBb-driven centering — the Palette
    // tile, the Symbol Editor canvas, the schematic's own selection box — down so the visible M+arcs
    // rendered pinned to the top (owner report, 2026-08-16). QuadCurve bounds on its own small local
    // control polygon instead, which is what fixes it.
    // Arrowheads computed so each triangle base is exactly orthogonal to its curve's original arc
    // at the outer endpoint (230° and 310°).  CW-tangent at θ = (−sin θ, cos θ).
    //   Left  tip=(−75,30); base corners=(−66,9) and (−54,25).
    //   Right tip=( 75,30); base corners=( 66,9) and ( 54,25).
    // RoundedRect frames the full content (M + curves + arrowheads) with ~15 u margin.

    private static Symbol BuildMutual() => Sym([
        Txt("M", 0, 0, fontSize: 36, align: SymbolTextAlign.Center, vAlign: SymbolTextVAlign.Middle),
        RRect(0, 5, 200, 80, 8),
        QC(-71, 26, -52, 10, -28, 4),   // left curve  (was: A(0, 110, 110, 230, 25))
        QC( 71, 26,  52, 10,  28, 4),   // right curve (was: A(0, 110, 110, 285, 25))
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

    // ── Match — the standard filter glyph: three stacked sine waves in a square body, with a ─────
    // slash struck through every wave the network BLOCKS (match.md §8.4).
    // Pins: (−200,0) left = port 1 = Termination 1 side / (+200,0) right, matching TLIN's own
    // horizontal 2-port convention.
    //
    // **The three waves read as a frequency axis, high at the top.** Which two carry a slash is
    // therefore the whole content of the glyph, and it follows the network's form (owner, 2026-08-28):
    //   Bandpass — top and bottom struck, the middle wave passes.
    //   Lowpass  — the top TWO struck; the lowest frequency passes.
    //   Highpass — the bottom TWO struck; the highest frequency passes.
    // A multiband design (match.md §18) is bandpass in every band, so it is drawn as two or three
    // SMALLER bandpass glyphs instead — side by side for dual-band, and two-below-one for tri-band,
    // which says "several passbands" in the one place a single stack of three waves cannot.
    //
    // The slashes are PLAIN LINES while the waves are SinePrimitives, and the primitive is the
    // only one of the two that knows anything about the glyph — so the strikethrough reads as a
    // strikethrough only if the lines are drawn to cross the waves geometrically, at every rotation
    // and mirrored. They are therefore centred on their own wave and just long enough (±14 in x,
    // against the waves' ±60) to overhang the wave's own local amplitude at both ends — owner,
    // 2026-08-19: "much shorter", then "make them half as long" (±28 x 50 -> ±14 x 26, half the
    // length, same slope and same centre). Every slash runs the same way, DOWNWARD from left to
    // right, so a pair reads as one annotation rather than as a cross.
    //
    // The 4-unit offset off the wave's own centre is load-bearing, not cosmetic: a slash centred
    // exactly on its wave's own centre passes THROUGH the point the wave itself passes through, and
    // two segments meeting at a shared point do not strictly cross — which
    // MatchComponentPlacementTests' strike-through check, quite correctly, reads as "not struck
    // through". Outer waves are nudged toward the middle and the middle wave downward, which is the
    // only choice that has to be made and the one that keeps a lowpass's two slashes parallel.

    /// <summary>Centre-to-centre spacing of the three stacked waves, symbol-local units.</summary>
    private const double MatchWaveGap    = 55;
    private const double MatchWaveAmp    = 16;
    private const double MatchWaveLength = 120;
    private const double MatchSlashHalfX = 14;
    private const double MatchSlashHalfY = 13;
    private const double MatchSlashNudge = 4;

    private static Symbol BuildMatch() => BuildMatchVariant(NetworkForm.Bandpass, 1);

    /// <summary>
    /// Per-instance <c>Match</c> symbol: the wave stack follows <paramref name="form"/>, and a
    /// <paramref name="bandCount"/> above 1 replaces it with that many smaller bandpass stacks
    /// (match.md §8.4, §18). Cached per (form, bandCount), mirroring the Tuner and SnP paths.
    ///
    /// <para>DISPLAY-ONLY, like <c>ShowBias</c>: the engine reads the <c>Design</c> payload and
    /// nothing else, so the glyph is a second RENDERING of the design, never a second input to it.
    /// It is driven by the <c>Form</c> and <c>Bands</c> echo parameters the Designer rewrites on
    /// every commit — the same echoes that used to be drawn as text beside the symbol.</para>
    /// </summary>
    public static Symbol PrimitivesForMatch(NetworkForm form, int bandCount)
    {
        int bands = bandCount < 1 ? 1 : bandCount > 3 ? 3 : bandCount;
        if (bands == 1 && form == NetworkForm.Bandpass) return _match;   // the cached default
        var key = (form, bands);
        if (!_matchCache.TryGetValue(key, out var sym))
            _matchCache[key] = sym = BuildMatchVariant(form, bands);
        return sym;
    }

    private static Symbol BuildMatchVariant(NetworkForm form, int bandCount)
    {
        var prims = new List<SymbolPrimitive>
        {
            L(-200,   0, -110,   0),                 // left lead
            L( 110,   0,  200,   0),                 // right lead
            RRect( 0,  0,  220, 220,  18),           // square body
        };

        // Scale and placement of each wave stack inside the 220 × 220 body. A stack is
        // ±(gap + amp) tall and ±length/2 wide before scaling, so at these three scales every group
        // clears the body wall and, for the multiband cases, its neighbours.
        switch (bandCount)
        {
            case 2:
                MatchWaveStack(prims, NetworkForm.Bandpass, 0.5,  -55, 0);
                MatchWaveStack(prims, NetworkForm.Bandpass, 0.5,   55, 0);
                break;
            case 3:
                MatchWaveStack(prims, NetworkForm.Bandpass, 0.45,   0, -45);
                MatchWaveStack(prims, NetworkForm.Bandpass, 0.45, -52,  45);
                MatchWaveStack(prims, NetworkForm.Bandpass, 0.45,  52,  45);
                break;
            default:
                MatchWaveStack(prims, form, 1.0, 0, 0);
                break;
        }

        return Sym(prims, SymbolKind.Match);
    }

    /// <summary>
    /// One stack of three waves centred at (cx, cy) and scaled by <paramref name="s"/>, with the
    /// blocked waves of <paramref name="form"/> struck through.
    /// </summary>
    private static void MatchWaveStack(List<SymbolPrimitive> prims, NetworkForm form,
                                       double s, double cx, double cy)
    {
        // Index 0 = top = the highest frequency the glyph depicts, 2 = bottom = the lowest.
        bool[] struck = form switch
        {
            NetworkForm.Lowpass  => [true,  true,  false],
            NetworkForm.Highpass => [false, true,  true ],
            _                    => [true,  false, true ],
        };

        for (int i = 0; i < 3; i++)
        {
            double wy = cy + (i - 1) * MatchWaveGap * s;
            prims.Add(Sine(cx, wy, MatchWaveAmp * s, 1, MatchWaveLength * s, SineAxis.Horizontal));
            if (!struck[i]) continue;
            // Toward the middle for the outer waves; downward for the middle one.
            double nudge = (i == 2 ? -MatchSlashNudge : MatchSlashNudge) * s;
            prims.Add(L(cx - MatchSlashHalfX * s, wy + nudge - MatchSlashHalfY * s,
                        cx + MatchSlashHalfX * s, wy + nudge + MatchSlashHalfY * s));
        }
    }

    // ══ System-level blocks (brief-sys-1-symbols-and-palette.md) ══════════════════════════════
    //
    // Ten glyphs for the level ABOVE a transistor, where a signal path is a chain of named boxes.
    // Two conventions are inherited from the mixer and are not redesigned here: a signal block reads
    // LEFT TO RIGHT (inputs left, outputs right, a third port at the bottom), and a block whose
    // leads are NOT interchangeable labels them — a reader who connects the wrong one gets a circuit
    // that solves and is wrong. A block whose leads ARE interchangeable (the SPST switch, the
    // attenuator) is left unlabelled, because a name on a symmetric pin is noise that reads as
    // meaning.

    /// <summary>
    /// A straight arrow from (x1,y1) to (x2,y2): the shaft, shortened by the head's own length, and
    /// a filled triangular head whose TIP lands exactly on (x2,y2).
    ///
    /// <para>The shaft is shortened rather than drawn full length because a stroked line running the
    /// whole way into a filled head thickens its spine at the join — visible at the zoom a schematic
    /// is actually read at, and the sort of thing that reads as a drawing mistake.</para>
    /// </summary>
    private static void ArrowTo(List<SymbolPrimitive> prims,
                                double x1, double y1, double x2, double y2,
                                double headLen = 30, double headHalfW = 14)
    {
        double dx = x2 - x1, dy = y2 - y1;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return;
        double ux = dx / len, uy = dy / len;      // along the arrow
        double nx = -uy,      ny = ux;            // across it
        double bx = x2 - headLen * ux, by = y2 - headLen * uy;   // the head's base centre
        prims.Add(L(x1, y1, bx, by));
        prims.Add(Poly(true, x2, y2,
                             bx + headHalfW * nx, by + headHalfW * ny,
                             bx - headHalfW * nx, by - headHalfW * ny));
    }

    /// <summary>
    /// A filled arrowhead sitting ON a circular arc at angle <paramref name="endDeg"/>, pointing
    /// along the direction of travel — clockwise when <paramref name="cw"/>, otherwise anticlockwise.
    /// Degrees are Skia's: 0 is +x and a POSITIVE sweep turns clockwise on screen, because +y is down.
    /// </summary>
    private static PolygonPrimitive ArcArrowhead(double cx, double cy, double r, double endDeg, bool cw,
                                                 double headLen = 34, double headHalfW = 15)
    {
        double a  = endDeg * Math.PI / 180.0;
        double px = cx + r * Math.Cos(a), py = cy + r * Math.Sin(a);
        // The tangent at that point, in the direction the arc was drawn.
        double tx = -Math.Sin(a), ty = Math.Cos(a);
        if (!cw) { tx = -tx; ty = -ty; }
        double nx = -ty, ny = tx;
        double bx = px - headLen * tx * 0.5, by = py - headLen * ty * 0.5;
        double sx = px + headLen * tx * 0.5, sy = py + headLen * ty * 0.5;
        return Poly(true, sx, sy, bx + headHalfW * nx, by + headHalfW * ny,
                                  bx - headHalfW * nx, by - headHalfW * ny);
    }

    // ── Balun — a transformer inside a system-block frame ─────────────────────
    // Pins: UNB (-300,0) left · BAL+ (300,-100) · BAL- (300,+100) right.
    //
    // The box keeps it in the same family as its neighbours; the coils say what it is; and the
    // single left lead against a ± pair on the right says which end is unbalanced without spending
    // text on it. The polarity marks are the same "+"/"-" pair in the same SymbolPlus role the VCCS
    // and MixerD use — a reader who has learned one ± convention in this library has learned them all.

    private static Symbol BuildBalun() => Sym([
        RRect( 0,    0,  240,  300,   12),      // body, x in [-120,120]  y in [-150,150]
        A(  -45,  -60,   30,   90,  180),       // primary coil, three arcs bulging -x
        A(  -45,    0,   30,   90,  180),
        A(  -45,   60,   30,   90,  180),
        A(   45,  -60,   30,  -90,  180),       // secondary coil, three arcs bulging +x
        A(   45,    0,   30,  -90,  180),
        A(   45,   60,   30,  -90,  180),
        L(   -8,  -80,   -8,   80),             // transformer core, two lines
        L(    8,  -80,    8,   80),
        L( -300,    0, -120,    0),             // UNB lead
        L(  120, -100,  300, -100),             // BAL+ lead
        L(  120,  100,  300,  100),             // BAL- lead
        Txt("+", 215, -155, fontSize: PolarityFontSize, colorRole: SymbolColorRole.SymbolPlus),
        Txt("−", 215,  155, fontSize: PolarityFontSize),
    ], SymbolKind.Balun);

    // ── Circulator — the universal circle with a rotation arrow ───────────────
    // Pins: 1 (-300,0) · 2 (+300,0) · 3 (0,+300).
    //
    // The arrow is the whole content of the symbol, and it is DYNAMIC: `Direction = CW` circulates
    // 1 -> 2 -> 3 -> 1 and `CCW` reverses it. A circulator drawn without saying which way it turns
    // is a component whose entire behaviour is unstated, so the glyph states it.
    //
    // The arc's ~100 degree GAP sits at the top of the circle in both directions, which is the one
    // place no port lead arrives, so the gap never reads as a break in a connection.

    /// <summary>
    /// Per-instance <c>Circulator</c> symbol: the rotation arrow follows <paramref name="dir"/>.
    /// Cached per direction, mirroring the Match and Tuner paths.
    /// </summary>
    public static Symbol PrimitivesForCirculator(CirculatorDirection dir)
    {
        if (!_circulatorCache.TryGetValue(dir, out var sym))
            _circulatorCache[dir] = sym = BuildCirculator(dir);
        return sym;
    }

    private static Symbol BuildCirculator(CirculatorDirection dir)
    {
        bool cw = dir == CirculatorDirection.CW;
        // CW  sweeps +260 from -60, ending at 200 (upper left).
        // CCW sweeps -260 from 240, ending at -20 (upper right).
        double start = cw ? -60 : 240;
        double sweep = cw ? 260 : -260;
        return Sym([
            Circ(0, 0, 150),                                  // body circle
            A(0, 0, 80, start, sweep),                         // the rotation arrow's arc
            ArcArrowhead(0, 0, 80, start + sweep, cw),         // its head, at the arc's end
            L(-300,   0, -150,   0),
            L( 150,   0,  300,   0),
            L(   0, 150,    0, 300),
            Txt("1", -215, -30, fontSize: MixerPortFontSize),
            Txt("2",  215, -30, fontSize: MixerPortFontSize),
            Txt("3",   60, 235, fontSize: MixerPortFontSize),
        ], SymbolKind.Circulator);
    }

    // ── Switch (SPST) and SwitchD (SPDT) — drawn in the position they are SET to ──
    // SPST pins: (-300,0) · (+300,0), interchangeable and therefore unlabelled.
    // SPDT pins: COM (-300,0) · T1 (+300,-100) · T2 (+300,+100), which are NOT.
    //
    // Both are DYNAMIC on `State`, and that is the whole point: a switch drawn in the position it is
    // actually set to is readable at a glance, and a `State` swept parametrically then reads off the
    // schematic rather than out of the sweep definition.

    /// <summary>Per-instance SPST <c>Switch</c> symbol — the blade closed or lifted.</summary>
    public static Symbol PrimitivesForSwitch(SwitchState state)
    {
        if (!_switchCache.TryGetValue(state, out var sym))
            _switchCache[state] = sym = BuildSwitch(state);
        return sym;
    }

    private static Symbol BuildSwitch(SwitchState state) => Sym([
        L(-300, 0, -100, 0),                    // leads
        L( 100, 0,  300, 0),
        state == SwitchState.On
            ? L(-100, 0, 100,   0)              // the blade, closed
            : L(-100, 0,  80, -90),             // the blade, lifted
        Circ(-100, 0, 12, filled: true),        // the two contacts, drawn OVER the blade so the
        Circ( 100, 0, 12, filled: true),        // pivot reads as a pivot rather than as a crossing
    ], SymbolKind.Switch);

    /// <summary>Per-instance SPDT <c>SwitchD</c> symbol — the blade on the throw it is set to.</summary>
    public static Symbol PrimitivesForSwitchD(SwitchThrow thrown)
    {
        if (!_switchDCache.TryGetValue(thrown, out var sym))
            _switchDCache[thrown] = sym = BuildSwitchD(thrown);
        return sym;
    }

    private static Symbol BuildSwitchD(SwitchThrow thrown) => Sym([
        L(-300,    0, -100,    0),              // COM lead
        L( 100, -100,  300, -100),              // T1 lead
        L( 100,  100,  300,  100),              // T2 lead
        thrown == SwitchThrow.T1
            ? L(-100, 0, 100, -100)             // blade to throw 1
            : L(-100, 0, 100,  100),            // blade to throw 2
        Circ(-100,    0, 12, filled: true),
        Circ( 100, -100, 12, filled: true),
        Circ( 100,  100, 12, filled: true),
        Txt("1", 165, -155, fontSize: MixerPortFontSize),
        Txt("2", 165,  155, fontSize: MixerPortFontSize),
    ], SymbolKind.SwitchD);

    // ── Amp — the amplifier triangle ──────────────────────────────────────────
    // Pins: IN (-300,0) · OUT (+300,0).
    //
    // Nothing inside it. The gain shows as the parameter label under the symbol, which is where a
    // reader looks for a number — and a triangle with a number written inside it is the one
    // amplifier drawing that stops being readable the moment the number has three digits.

    private static Symbol BuildAmp() => Sym([
        Poly(false, -140, -150, -140, 150, 160, 0),   // the amplifier triangle, stroked
        L(-300, 0, -140, 0),
        L( 160, 0,  300, 0),
    ], SymbolKind.Amp);

    // ── Coupler and the two hybrids — one body, one arrow, one pin layout ─────
    // Pins: 1 IN (-300,-100) · 2 THRU (+300,-100) · 3 CPL (+300,+100) · 4 ISO (-300,+100).
    //
    // THREE tiles over ONE engine component and one drawing: the 90° hybrid is that component at
    // 3.01 dB and quadrature, the 180° hybrid the same at anti-phase, and the only thing that
    // differs on the page is the phase written inside the frame — because the only thing that
    // differs is the phase.
    //
    // The two arms run straight THROUGH the body, leads and all, because that is what a coupler is:
    // two transmission lines that happen to be close to each other. The arrow does the real work —
    // it is what separates the coupled port from the isolated one, and a coupler drawn without it is
    // ambiguous in exactly the way that produces a silently wrong circuit.
    //
    // A hybrid's phase label is placed to the LEFT of centre, NOT at it. The arrow crosses the
    // body's exact centre on its way from the main arm to the coupled one, so a label centred there
    // is struck through by it — precisely what a coordinate list hides and a reader sees
    // immediately. One x for both hybrids, chosen so the WIDER of the two ("180°") still clears the
    // frame: at -85 the 4-character label clears the frame by ~26 and the arrow by ~36, and the
    // 3-character one has more room again.

    /// <summary>Symbol-local x of a hybrid's phase label. See the note above for why it is not 0.</summary>
    private const double HybridPhaseLabelX = -85;

    private static Symbol BuildCoupler(SymbolKind kind)
    {
        var prims = new List<SymbolPrimitive>
        {
            RRect(0, 0, 320, 300, 12),          // body, x in [-160,160]  y in [-150,150]
            L(-300, -100, 300, -100),           // the main arm, lead to lead
            L(-300,  100, 300,  100),           // the coupled arm
        };
        ArrowTo(prims, -40, -100, 40, 100);     // coupling: main arm -> coupled arm
        string phase = kind switch
        {
            SymbolKind.Hybrid90  => "90°",
            SymbolKind.Hybrid180 => "180°",
            _                    => "",         // a plain coupler states its coupling as a parameter
        };
        if (phase.Length > 0) prims.Add(Txt(phase, HybridPhaseLabelX, 0, fontSize: 44));
        prims.Add(Txt("1", -215, -140, fontSize: MixerPortFontSize));
        prims.Add(Txt("2",  215, -140, fontSize: MixerPortFontSize));
        prims.Add(Txt("3",  215,  145, fontSize: MixerPortFontSize));
        prims.Add(Txt("4", -215,  145, fontSize: MixerPortFontSize));
        return Sym(prims, kind);
    }

    // ── Filter — Match's glyph, by construction ───────────────────────────────
    // Pins: (-200,0) · (+200,0) — Match's own pins, at Match's own spacing.
    //
    // THE FILTER GLYPH IS THE MATCH GLYPH: the same picture, not a related one (owner decision,
    // 2026-08-31). Impedance matching is a form of filtering, the two are built out of the same
    // idea, and a library that draws them the same way says so. It is built by reusing Match's own
    // primitive list rather than by copying its geometry — the TermG pattern — so the two are
    // identical BY CONSTRUCTION and cannot drift apart when either is next touched.
    //
    // The duplicate is deliberate. The two are told apart on the schematic by their type label and
    // their instance name (FLT1 against MN1), which is the same way the five FET laws — which also
    // share one glyph — are told apart today.
    //
    // bandCount is always 1: a filter has ONE band. A multi-band MATCH is a different statement
    // about a different component, and drawing a filter as several stacks would claim a passband
    // count it has no parameter for.

    /// <summary>Per-instance <c>Filter</c> symbol — Match's own primitives for the same form.</summary>
    public static Symbol PrimitivesForFilter(NetworkForm form)
    {
        if (!_filterCache.TryGetValue(form, out var sym))
            _filterCache[form] = sym = Sym([.. PrimitivesForMatch(form, 1).Primitives], SymbolKind.Filter);
        return sym;
    }

    // ── Atten — the pinched bowtie ────────────────────────────────────────────
    // Pins: (-300,0) · (+300,0), interchangeable and therefore unlabelled.
    //
    // Two filled triangles meeting at a point read as "signal made smaller", and the shape collides
    // with nothing else in the library. The loss shows as the parameter label.

    private static Symbol BuildAtten() => Sym([
        RRect(0, 0, 240, 160, 12),                    // body, x in [-120,120]  y in [-80,80]
        Poly(true, -80, -60, -80, 60, 0, 0),          // the bowtie, left half
        Poly(true,  80, -60,  80, 60, 0, 0),          // and right half
        L(-300, 0, -120, 0),
        L( 120, 0,  300, 0),
    ], SymbolKind.Atten);

    // ── Duplexer — a junction that splits into two filters ────────────────────
    // Pins: ANT (-300,0) · TX (+300,-100) · RX (+300,+100).
    //
    // One junction splitting into two filters is what a duplexer IS, and the glyph says so: the two
    // branches carry MATCH's own passband stack, at 0.45 scale, so the block reads as "two filters"
    // in the same visual language the filter tile uses.
    //
    // Two details are owner corrections, 2026-08-31, and both are the kind a rendered figure makes
    // obvious and a coordinate list hides. The ANT lead runs from its PIN all the way to the body
    // edge — a port whose lead stops short of the frame reads as unconnected. And the TX and RX
    // labels sit INSIDE the body, beside their own stack rather than above it, because that is where
    // the room is: at 0.45 scale a stack reaches |y| = 122 including its strike lines, leaving 38
    // units to the frame at |y| = 160, and a 30-point label cannot fit in that with clearance at
    // both ends. Beside, the stack spans x in [28,82], so a label centred at x = 130 clears the
    // waves by 31 and the frame by 24 — and it names the arm it is level with.

    private static Symbol BuildDuplexer()
    {
        var prims = new List<SymbolPrimitive>
        {
            RRect(0, 0, 340, 320, 14),          // body, x in [-170,170]  y in [-160,160]
            L(-300,   0, -170,   0),            // ANT lead, pin to body edge
            L(-170,   0,  -90,   0),            // the common line, into the junction
            L( -90,   0,  -30, -90),            // and its two arms
            L( -90,   0,  -30,  90),
        };
        MatchWaveStack(prims, NetworkForm.Bandpass, 0.45, 55, -90);   // TX passband
        MatchWaveStack(prims, NetworkForm.Bandpass, 0.45, 55,  90);   // RX passband
        prims.Add(L(170, -100, 300, -100));
        prims.Add(L(170,  100, 300,  100));
        prims.Add(Txt("TX", 130, -90, fontSize: MixerPortFontSize));
        prims.Add(Txt("RX", 130,  90, fontSize: MixerPortFontSize));
        return Sym(prims, SymbolKind.Duplexer);
    }

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
