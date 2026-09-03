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

    // ── The MOS family ────────────────────────────────────────────────────────
    //
    // FOUR kinds for two laws in two channel types, which is the two rules above put together: a
    // LEVEL is a different set of equations (so it cannot be a mode parameter, exactly as the five
    // MESFET laws cannot), and a CHANNEL is a sign the reader has to be able to SEE (so it cannot be
    // one either, exactly as the BJT's polarity cannot).
    //
    // Unlike the MESFET family these have FOUR pins: drain, gate, source and BULK. The bulk is a
    // real terminal and not a convenience — tying it to the source internally would silently delete
    // the body effect, which is a defining part of the level-1 law and is worth hundreds of
    // millivolts of threshold. A part whose bulk really is tied to its source says so by wiring the
    // pin.
    //
    // The two levels share one glyph per channel, for the same reason the five MESFET laws share
    // theirs: the topology genuinely is identical and only the channel-current equation differs,
    // which the type label below the symbol already names. The two CHANNELS do not share, for the
    // same reason the two BJT polarities do not: the bulk arrow is the only thing on the drawing
    // that tells them apart, and it is the first thing a reader looks for.

    /// <summary>n-channel level-1 (Shichman-Hodges) MOSFET (engine "MOS1_N").</summary>
    Mos1N,

    /// <summary>p-channel level-1 (Shichman-Hodges) MOSFET (engine "MOS1_P").</summary>
    Mos1P,

    /// <summary>n-channel level-3 (semi-empirical short-channel) MOSFET (engine "MOS3_N").</summary>
    Mos3N,

    /// <summary>p-channel level-3 (semi-empirical short-channel) MOSFET (engine "MOS3_P").</summary>
    Mos3P,

    // ── The IGBTs ─────────────────────────────────────────────────────────────
    //
    // Three pins — collector, gate, emitter — and an internal base node the elaborator always mints,
    // because an equivalent-circuit IGBT IS an insulated-gate channel driving a bipolar and the node
    // between them is the model.
    //
    // The glyph is the MOS one with a BIPOLAR's emitter arrow on the output, which is exactly what
    // the standard symbol says the device is: an insulated gate on one side, a junction on the
    // other. It matters that the arrow is there — an IGBT does NOT conduct in reverse, and a reader
    // who mistakes one for a power MOSFET will expect a body diode that is not present.

    /// <summary>n-channel IGBT (engine "IGBT_N") — the ordinary one.</summary>
    IgbtN,

    /// <summary>p-channel IGBT (engine "IGBT_P").</summary>
    IgbtP,

    /// <summary>
    /// Ferrite bead (engine "Bead") — a two-terminal LINEAR element, <c>Rdc</c> in series with a
    /// parallel <c>L</c>/<c>Rp</c>/<c>Cp</c> tank.
    ///
    /// <para>Sits with the lumped elements rather than the devices, because that is what it is: a
    /// linear impedance. It is not an inductor and not an SRLC — a bead's whole purpose is that most
    /// of its impedance is RESISTIVE at the frequency a data sheet quotes, and that the loss RISES
    /// with frequency to a peak and falls again. Neither of those is expressible with a fixed R.</para>
    /// </summary>
    Bead,

    // ── The vertical power MOSFETs ────────────────────────────────────────────
    //
    // A SEPARATE component from the lateral MOS pair, not a setting of it. Three pins, not four:
    // the source-to-body short is inside the silicon, which is exactly what turns the substrate
    // junction into a source-to-drain BODY DIODE that carries load current. Its glyph draws that
    // diode, because it is a circuit element the user is going to reason about.

    /// <summary>n-channel vertical power MOSFET (engine "VDMOS_N").</summary>
    VdmosN,

    /// <summary>p-channel vertical power MOSFET (engine "VDMOS_P").</summary>
    VdmosP,

    // ── The p-channel MESFETs ─────────────────────────────────────────────────
    //
    // THREE, not five. p-channel is offered for exactly the laws that mirror unambiguously — the
    // ones whose gate dependence is anchored to a threshold and is even in it, so flipping every
    // voltage and current is the whole of the change. The Curtice-Ettenberg cubic and Angelov are
    // n-channel only: their gate dependence is a polynomial fitted directly against the gate
    // voltage, so a mirror would have to negate the odd-order coefficients and leave the even ones
    // alone, and no published convention says a p-channel card is written that way. See each of
    // those two models' own summary.
    //
    // All three share ONE glyph — the n-channel FET glyph with its gate arrow reversed — for the
    // same reason the five n-channel laws share theirs: the topology is identical and the type
    // label names the law. They do NOT share with the n-channel tiles, for the same reason the BJT
    // polarities do not: the arrow is the whole of the difference a reader can see.

    /// <summary>p-channel Curtice quadratic MESFET (engine "PFET_Curtice").</summary>
    PFetCurtice,

    /// <summary>p-channel Statz MESFET (engine "PFET_Statz").</summary>
    PFetStatz,

    /// <summary>p-channel Materka-Kacprzak MESFET (engine "PFET_Materka").</summary>
    PFetMaterka,

    // ── The JFET pair ─────────────────────────────────────────────────────────
    //
    // Two kinds over one law, arranged like the BJT's polarities rather than like the MESFET laws:
    // the names denote the same equations with one sign changed. Three pins, like the MESFET — the
    // JFET's gate IS the junction, so there is no fourth terminal to have.
    //
    // NOT the MESFET family with different coefficients, which is why these exist at all: the knee
    // is the square law's own boundary rather than a fitted tanh, and the gate is a real p-n
    // junction that conducts and stores depletion charge in BOTH directions where the MESFET's
    // Schottky gate is modelled as one forward diode.

    /// <summary>n-channel junction FET (engine "JFET_N"). Vto/Beta/Lambda + two gate junctions.</summary>
    JfetN,

    /// <summary>p-channel junction FET (engine "JFET_P"). Same equations, every sign reversed.</summary>
    JfetP,

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

    /// <summary>Ideal voltage-controlled voltage source (engine "VCVS"). Four pins in ± pair order —
    /// [0]=out+, [1]=out−, [2]=ctrl+, [3]=ctrl− — constraining V(out+) − V(out−) = E·(V(ctrl+) −
    /// V(ctrl−)). The control pair senses only and draws no current, which is what makes it ideal.
    /// Group 2 where the VCCS is Group 1: a controlled VOLTAGE source states a relation between node
    /// voltages, which no combination of admittances expresses, so it carries a branch-current
    /// unknown of its own.</summary>
    Vcvs,

    // ── Ideal mixer ───────────────────────────────────────────────────────────
    // TWO kinds over ONE engine component ("Mixer"), the TermG pattern rather than the BJT one:
    // nothing electrical differs between them, so there is no parallel model — only how many of the
    // six nets the schematic exposes as pins. The single-ended tile ties each port's − net to
    // ground at extraction, exactly as TermG does with Term's port 2, and gets the universal
    // circle-and-✕ glyph in exchange; the differential tile shows all six pins and can drive a
    // balanced IF, at the cost of looking like nothing in any textbook.

    /// <summary>Ideal three-port mixer, SINGLE-ENDED (engine "Mixer"). Three pins — RF left, LO
    /// bottom, IF right — with each port's − net tied to ground at extraction, so the engine still
    /// sees its six nets in ± pair order. Memoryless multiplier: v_if(open) = K·v_rf·v_lo, with the
    /// multiplier constant derived from `ConvGain` at the stated `Plo`. Both sidebands appear.
    /// Non-idealities: port impedances, three isolations, and an input-referred IIP3.</summary>
    Mixer,

    /// <summary>Ideal three-port mixer, DIFFERENTIAL (engine "Mixer"). The SAME component as
    /// <see cref="Mixer"/> — six pins in the repository's ± pair order:
    /// [0]=rf+, [1]=rf−, [2]=lo+, [3]=lo−, [4]=if+, [5]=if−. Use it when a port's
    /// return is not ground; otherwise the single-ended tile is the same circuit with three fewer
    /// wires to draw.</summary>
    MixerD,

    // ── System-level blocks (brief-sys-series.md) ─────────────────────────────
    // Ten tiles that let a user draw a SYSTEM block diagram — the level above a transistor, where a
    // signal path is a chain of named boxes. They share one drawing grammar with the mixer above: a
    // signal block reads LEFT TO RIGHT (inputs left, outputs right, a third port at the bottom), and
    // a block whose leads are not interchangeable LABELS them, because a reader who connects the
    // wrong one gets a circuit that solves and is wrong.
    //
    // SYS-1 adds the ARTWORK, the pins, the palette category and the ground-return extraction, and
    // nothing else: none of these ten has a model yet, so a placed tile simulates the way any
    // unimplemented primitive does. SYS-2 onwards makes each one real.

    /// <summary>Ideal balun (engine "Balun"). Three pins — UNB (−300,0) left, BAL+ (300,−100) and
    /// BAL− (300,+100) right — a single unbalanced end against a balanced pair, which is what the
    /// one-lead-against-two glyph says without spending text on it.</summary>
    Balun,

    /// <summary>Ideal circulator (engine "Circulator"). Three pins in port order — P1 (−300,0),
    /// P2 (300,0), P3 (0,+300). DYNAMIC on <c>Direction</c>: the arrow inside the body circulates
    /// 1→2→3→1 for <see cref="CirculatorDirection.CW"/> and reverses for
    /// <see cref="CirculatorDirection.CCW"/>, so which way it goes is read off the schematic rather
    /// than out of a parameter dialog.</summary>
    Circulator,

    /// <summary>Ideal SPST switch (engine "Switch"). Two pins, (−300,0) and (300,0), which are
    /// interchangeable and therefore unlabelled. DYNAMIC on <c>State</c>: the blade is drawn closed
    /// for <see cref="SwitchState.On"/> and lifted for <see cref="SwitchState.Off"/>.</summary>
    Switch,

    /// <summary>Ideal SPDT switch (engine "Switch" — the SAME component as <see cref="Switch"/>).
    /// Three pins — COM (−300,0), T1 (300,−100), T2 (300,+100). DYNAMIC on <c>State</c>: the blade
    /// points at the throw it is actually set to, so a <c>State</c> swept parametrically reads off
    /// the schematic.</summary>
    SwitchD,

    /// <summary>Ideal amplifier (engine "Amp"). Two pins, IN (−300,0) and OUT (300,0). Nothing is
    /// drawn inside the triangle: the gain shows as the parameter label under the symbol, which is
    /// where a reader looks for a number.</summary>
    Amp,

    /// <summary>Ideal directional coupler (engine "Coupler"). Four pins in port order — P1 IN
    /// (−300,−100), P2 THRU (300,−100), P3 CPL (300,+100), P4 ISO (−300,+100). The coupling arrow
    /// is not decoration: it is the whole of what separates the coupled port from the isolated one,
    /// and a coupler drawn without it is ambiguous in exactly the way that produces a silently wrong
    /// circuit.</summary>
    Coupler,

    /// <summary>Ideal 90° hybrid (engine "Coupler" — the SAME component as <see cref="Coupler"/> at
    /// 3.01 dB and 90°). Identical body, identical pins, identical arrow, plus the quadrature label
    /// inside the frame.</summary>
    Hybrid90,

    /// <summary>Ideal 180° hybrid — a rat race (engine "Coupler", the SAME component again, at
    /// 3.01 dB and 180°). Identical body, identical pins, identical arrow; only the phase written
    /// inside the frame differs, because only the phase differs. Owner decision, 2026-08-31: it
    /// ships beside <see cref="Hybrid90"/> because the geometry was already shared and an in-phase
    /// or anti-phase combiner is as ordinary a system block as a quadrature one.</summary>
    Hybrid180,

    /// <summary>Ideal filter (engine "Filter"). Two pins, (−200,0) and (200,0).
    ///
    /// <para><b>Its glyph IS <see cref="Match"/>'s glyph</b> — the same picture, built out of
    /// Match's own primitives, not a copy of them (owner decision, 2026-08-31). Impedance matching
    /// is a form of filtering, the two are built out of the same idea, and a library that draws them
    /// the same way says so. The two are told apart on the schematic by their type label and their
    /// instance name (<c>FLT1</c> against <c>MN1</c>) — the same way the five FET laws, which also
    /// share one glyph, are told apart today. The duplicate is deliberate; do not "fix" it.</para>
    ///
    /// <para>DYNAMIC on <c>Form</c>, through Match's own struck-line convention: a slash through
    /// every wave the network blocks.</para></summary>
    Filter,

    /// <summary>Ideal attenuator (engine "Atten"). Two pins, (−300,0) and (300,0), interchangeable
    /// and therefore unlabelled. The pinched bowtie reads as "signal made smaller" and collides with
    /// nothing else in the library; the loss shows as the parameter label.</summary>
    Atten,

    /// <summary>Ideal duplexer (engine "Duplexer"). Three pins — ANT (−300,0), TX (300,−100),
    /// RX (300,+100). One junction splitting into two filters is what a duplexer IS, and the glyph
    /// says so: the antenna lead runs from its pin to the body edge, the junction fans into two
    /// arms, and each arm carries its own passband stack with its name beside it.</summary>
    Duplexer,

    // ── The RLC pair ───────────────────────────────────────────────────────────────
    // Two kinds, not one with a series/parallel selector: the two DRAW differently, and the
    // topology is the whole of what tells them apart. A selector would leave the schematic showing
    // a series branch while the netlist carried a parallel one.
    //
    // Both are 2-pin at (0,−200)/(0,+200) — the SAME pin positions as R, L and C — so a designer can
    // swap a plain R, L or C for one of these without touching a single wire. That is a contract,
    // not a coincidence; SrlcPrlcPinCompatibilityTests holds it shut.

    /// <summary>Series RLC branch (engine "SRLC"). R, L and C in series on one branch — the shape a
    /// real ceramic capacitor takes, with the vendor's ESR and ESL entered as <c>R</c> and <c>L</c>.
    /// Its inductance lives on a Group-2 branch current, so a <see cref="Mutual"/> can couple to it
    /// exactly as it couples to a plain <see cref="Inductor"/>.</summary>
    Srlc,

    /// <summary>Parallel RLC branch (engine "PRLC"). R, L and C all across the same two nodes — a
    /// tank. The R and C stamp as admittances; the L takes its own Group-2 branch, both because an
    /// ideal inductor's admittance diverges at DC and because that branch is what a
    /// <see cref="Mutual"/> couples to.</summary>
    Prlc,

    /// <summary>
    /// A SPICE model placed as a component (no engine component of its own — the extractor
    /// resolves it to whatever the file describes).
    ///
    /// <para>Points at a file holding a <c>.model</c> card or a <c>.subckt</c> definition and runs
    /// THAT, with no cell folder anywhere: the import gesture (Copy to Workspace as Cell…) makes an
    /// editable cell out of the same file, and this is the other half of the same pair — a
    /// reference, not a copy. There is deliberately no pop-in: a SpiceModel has no <c>.csch</c> to
    /// push into, and the file it names is the authority.</para>
    ///
    /// <para><b>The symbol is GENERATED from the file</b> (<see cref="SpiceModelSymbolProvider"/>) —
    /// a supported <c>.model</c> card draws as the circuitRF device that implements it, a
    /// <c>.subckt</c> draws as an N-port box carrying the definition's own port names, and an
    /// unconfigured instance draws as a generic 2-port. Which one is a property of the FILE, so
    /// there is no pin count on this kind to keep in step.</para>
    /// </summary>
    SpiceModel,
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

    // ── Annotation symbols (VAR / MEAS) ──────────────────────────────────────
    // A VAR or a MEAS is all text: the glyph is a small ±80 x ±60 box and the rows beneath it are
    // the content. The shared -155/280 anchor was chosen for a two-terminal part whose leads run to
    // ±200, and on these it hangs the block down and to the LEFT of a box it has no lead to clear —
    // so the type, the instance name and every row sit adrift of the symbol they belong to. These
    // two constants pull the block back under the glyph: left edge flush with the box's own left
    // edge, and the first row just below its bottom.
    public const double AnnotationBodyHalfW = 80.0;  // VAR/MEAS box half-width  (BuiltInSymbols)
    public const double AnnotationBodyHalfH = 60.0;  // VAR/MEAS box half-height (BuiltInSymbols)
    public const double AnnotationLabelPadY = 22.0;  // clear air between the box bottom and the cap tops

    /// <summary>True for the port-less, text-carrying annotation symbols whose labels hug the glyph.</summary>
    public static bool IsAnnotationSymbol(SymbolKind symbol)
        => symbol is SymbolKind.Var or SymbolKind.Meas;

    /// <summary>
    /// Label anchor X (from component center) for this symbol. Annotation symbols left-justify their
    /// text to the glyph's own left edge; everything else keeps the shared
    /// <see cref="LabelBaseOffsetX"/>.
    /// </summary>
    public static double LabelBaseXFor(SymbolKind symbol)
        => IsAnnotationSymbol(symbol) ? -AnnotationBodyHalfW : LabelBaseOffsetX;

    /// <summary>
    /// The offset for label row <paramref name="i"/>, falling back to the LAST stored offset rather
    /// than to (0,0) when the row has none of its own. A parameter added AFTER the labels were moved
    /// has no saved offset, and reading (0,0) for it dropped that one row back at the un-moved
    /// default position — visibly detached from the block it belongs to. The rows below the last
    /// stored one belong directly under it.
    /// </summary>
    public static (double DX, double DY) LabelOffsetAt(
        IReadOnlyList<(double DX, double DY)> offsets, int i)
        => offsets.Count == 0 ? (0.0, 0.0) : offsets[Math.Min(i, offsets.Count - 1)];

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
        // VAR/MEAS: sit the first row just under the box rather than at the shared 280, which was
        // sized for a part with leads. Measured from the REAL glyph bottom so it stays right if the
        // box ever grows; LabelWorldHeight is added because the returned Y is a Skia BASELINE and
        // the padding is meant to be visible air above the cap tops.
        if (IsAnnotationSymbol(symbol))
        {
            double halfH = glyphHalfH ?? AnnotationBodyHalfH;
            return halfH + AnnotationLabelPadY + LabelWorldHeight;
        }
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
        double baselineX = cx + LabelBaseXFor(symbol) + oDx;
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

    /// <summary>
    /// The workspace alias a <c>ws://</c> cell reference carries, or null for every other component
    /// (MW2 R-mw2-13). Baked in here rather than derived in the renderer for the same reason
    /// <see cref="CellRefState"/> is: the render model is built once per change and read every frame.
    ///
    /// <para>Taken from the reference's own spelling, not from a resolution — the mark says "this
    /// cell is not this workspace's own", which is true whether or not it currently resolves, and
    /// which is exactly the fact a broken external reference most needs to state.</para>
    /// </summary>
    public string? ExternalAlias { get; init; }

    /// <summary>
    /// True when this instance's cell resolves and draws correctly, but its published interface no
    /// longer matches the one the instance was placed against (SL3 R-sl3-7).
    ///
    /// <para>Carried here so the renderer can mark the CHROME — R36 without exception: the geometry
    /// is the librarian's new symbol and it is the truth, so it renders exactly as drawn. Copied from
    /// <see cref="EditableComponent.InterfaceChanged"/>, which <c>CellInterfaceWatch</c> sets on open
    /// rather than per frame: the comparison reads the cell's <c>.ccell</c> from disk.</para>
    /// </summary>
    public bool InterfaceChanged { get; init; }
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
