// What a .cem holds (brief-L6-L7-em-ui.md D1, R-em-9/10/11).
//
// D1 — the EM setup lives in its OWN document type rather than inside the .clay. This CHANGES
// §10.8's R17a ("an EM setup is a property of the layout… persisted in the .clay"), and it serves
// R17a's own stated purpose better: the standing invariant "analyses attach to a TestBench, never
// to a Cell" is satisfied more cleanly by a standalone setup document than by embedding one in a
// cell view, and it buys three things embedding does not — several EM setups against one layout,
// editing a setup without dirtying the .clay, and a setup that is independently diffable.
//
// R-em-11 — everything the kernel takes is in here, and the panel hardcodes nothing (D3: nothing
// that affects the answer lives in a transient dialog, a canvas mode, or a hardcoded panel default).

using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em;

// L8e — EmAnalysisKind MOVED to src/Engine/Mom/EmKernelRegistry.cs.
//
// L8b defined it here because there was no registry and the only consumer was the .cem. D1 keys the
// registry on the analysis kind, and the registry lives in the engine (Ui → Engine), so the enum
// went with it. The member NAMES are unchanged, so every .cem round-trips byte-identically; the
// enum gained one member, Auto, which no pre-L8e file can contain.

// brief-em3d-3 R-em3d3-3 — which 3D solver a setup runs is an Em3dSolverChoice (Em3dSolverChoice.cs, brief-emi-1).

/// <summary>One face of a 3D setup's air box, as the <c>.cem</c> states it. Either half may be
/// omitted, and an omitted half takes the generator's default for that face.</summary>
/// <param name="PaddingUm">Distance from the outermost geometry to this face, micrometres.</param>
/// <param name="Boundary">What the face does to the field.</param>
/// <param name="PaddingPercent">3D editor round 1 — the padding as a percentage of the content's extent along
/// this face's axis (10 on an x face pads a tenth of the x-extent). A face states this or
/// <paramref name="PaddingUm"/>, never both.</param>
public sealed record EmAirBoxFace(double? PaddingUm, CircuitRF.Engine.Em3d.Em3dBoundaryKind? Boundary,
                                  double? PaddingPercent = null);

/// <summary>
/// A 3D setup's air box, per face (R-em3d3-6). A null face is the generator's default for it
/// (§6's rule: a fraction of the longest wavelength on five faces, and the floor on the lowest
/// ground plane where there is one).
/// </summary>
public sealed record EmAirBox(
    EmAirBoxFace? XMin = null, EmAirBoxFace? XMax = null,
    EmAirBoxFace? YMin = null, EmAirBoxFace? YMax = null,
    EmAirBoxFace? ZMin = null, EmAirBoxFace? ZMax = null);

/// <summary>
/// brief-em3d-22 R-em3d22-2a — one terminal of a static 3D solve, as the <c>.cem</c> states it.
/// </summary>
/// <param name="Name">The matrix row's label.</param>
/// <param name="Net">A net of the layout, or a <c>.wBond</c> wire array's name: every conductor on it
/// is in the terminal.</param>
/// <param name="Source">Magnetostatic only: the port (its number, or <c>port/N</c>) whose sheet
/// drives this terminal's current.</param>
/// <param name="Objects">brief-em3d-49 — the terminal's conductors BY NAME, an alternative to <paramref name="Net"/>:
/// a drawn object carries no net, so a 3D view's terminal names its objects (<c>U1/…</c> allowed). Stating both is
/// refused. Null or empty: the net decides.</param>
public sealed record EmTerminal3D(string Name, string Net, string? Source = null, IReadOnlyList<string>? Objects = null)
{
    /// <summary>True when the terminal names its conductors rather than a net.</summary>
    public bool ByObjects => Objects is { Count: > 0 };
}

/// <summary>
/// brief-dfr31-1 R-dfr31-1-5 — what an electrostatic solve does with a body of metal that is in no terminal and is not the
/// ground (<c>UnassignedConductors3D</c>). <b>Float is the default</b>: such a body carries no net charge, which is what
/// unconnected metal does, and it is solved exactly (<see cref="CircuitRF.Engine.Em3d.Em3dStaticResult.EliminateFloating"/>).
/// </summary>
public enum EmUnassignedConductors
{
    /// <summary>Each such body is solved as one more terminal and eliminated from the matrix.</summary>
    Float,
    /// <summary>Each such body is ground — plane copper and its stitching vias.</summary>
    Ground,
    /// <summary>The run is refused, naming the bodies.</summary>
    Refuse,
}

/// <summary>
/// brief-em3d-23 R-em3d23-2 — one port's 3D settings, as the <c>.cem</c> states them.
/// </summary>
/// <param name="Port">The port's number.</param>
/// <param name="Kind">Lumped (the default) or Wave.</param>
/// <param name="WidthFactor">A wave port's width in multiples of the line's width; null takes the rule's.</param>
/// <param name="HeightFactor">A wave port's height in multiples of the line's height above its return;
/// null takes the rule's.</param>
/// <param name="OffsetUm">A wave port's de-embedding distance, µm; null is 0.</param>
/// <param name="Return">brief-dfr28-4 R-dfr28-4-5 — a lumped port's return, a conductor layer's stackup name.
/// Null or empty is Automatic: the generator walks the artwork from the port to the plane it returns through.
/// <see cref="SameLayerReturn"/> is a coplanar return (brief-dfr34-1 R-dfr34-1-6).</param>
public sealed record EmPort3D(int Port, Em3dPortKind Kind = Em3dPortKind.Lumped, double? WidthFactor = null,
                              double? HeightFactor = null, double? OffsetUm = null, string? Return = null)
{
    /// <summary>
    /// brief-dfr34-1 R-dfr34-1-6 — the Return that names no plane: the port returns through ground metal on its own line's
    /// layer, across the gap beside the line's end (a coplanar launch). Spelled so no stackup conductor can be named it.
    /// </summary>
    public const string SameLayerReturn = "(same layer)";

    /// <summary>What a picker shows for <see cref="SameLayerReturn"/>.</summary>
    public const string SameLayerDisplay = "Same layer (coplanar)";

    /// <summary>States nothing a lumped port with no overrides would not: written as nothing.</summary>
    public bool IsDefault => Kind == Em3dPortKind.Lumped && WidthFactor is null && HeightFactor is null && OffsetUm is null
                             && string.IsNullOrEmpty(Return);
}

/// <summary>
/// brief-emb-1 R-emb1-2 — one component port a 3D setup asks for: a lumped gap port built on the pads of the part placed with
/// <paramref name="Refdes"/> on the 3D view's layout (<see cref="EmSetup.AllComponents"/> for every placed two-terminal part).
/// </summary>
/// <param name="Refdes">The part's reference designator, or <c>*</c>.</param>
/// <param name="Z0">The port's reference impedance, as a <c>.c3d</c> port writes one (<c>50</c>, <c>25+j10</c>); null is 50 Ω.</param>
/// <param name="Reference">R-emb1-6 — the port's negative end: one of the part's own pins (its ground pin), or the conductor a
/// pad lands on. Null: the second pad of a two-terminal part, the part's ground pad otherwise.</param>
/// <param name="WidthUm">The port's width across its gap, micrometres, centred where the pads put it. Null: the width the two
/// pads share. An entry beside <c>*</c> overrides that part's port without naming it twice.</param>
public sealed record EmComponentPort(string Refdes, string? Z0 = null, string? Reference = null, double? WidthUm = null)
{
    /// <summary>Overrides nothing: written as its refdes alone.</summary>
    public bool IsPlain => Z0 is null && Reference is null && WidthUm is null;

    /// <summary>
    /// brief-lcp-3 R-lcp3-3 — <paramref name="list"/> with <paramref name="refdes"/>'s entry changed by <paramref name="change"/>:
    /// added beside <c>*</c> when the part had no entry. An entry already in the list is changed in place and never dropped, even
    /// when it comes to state nothing beside <c>*</c>: <c>*</c> takes two-pad parts only, so the entry may be what selects a part
    /// of more pads, and a plain refdes beside <c>*</c> is harmless. The one rule a 3D view's Properties and the setup panel's port
    /// rows both write a part's settings through.
    /// </summary>
    public static List<EmComponentPort> Override(IReadOnlyList<EmComponentPort> list, string refdes,
                                                 Func<EmComponentPort, EmComponentPort> change)
    {
        var result = list.ToList();
        int at = result.FindIndex(c => c.Refdes.Equals(refdes, StringComparison.OrdinalIgnoreCase));
        var entry = change(at >= 0 ? result[at] : new EmComponentPort(refdes));
        if (at >= 0) result[at] = entry;
        else if (!entry.IsPlain) result.Add(entry);
        return result;
    }

    /// <summary>Why <paramref name="z0"/> cannot be a component port's reference impedance, or null when it can (blank is the default).</summary>
    public static string? Z0Refusal(string? z0)
        => z0 is { } zt && zt.Trim().Length > 0 && !(CircuitRF.Design.ThreeD.C3dPorts.TryParseZ0(zt, out var z) && z.Real > 0)
            ? $"'{zt}' is not a reference impedance: a number of ohms with a positive real part, or a complex one such as 25+j10."
            : null;

    /// <summary>Why <paramref name="widthUm"/> cannot be a component port's width, or null when it can (null is the default).</summary>
    public static string? WidthRefusal(double? widthUm)
        => widthUm is <= 0 || widthUm is double.NaN
            ? "A port's width is a length greater than zero; leave it blank for the width the pads share."
            : null;
}

/// <summary>brief-em3d-23 R-em3d23-4a — an eigenmode solve's settings; a null field takes its default.</summary>
public sealed record EmEigenmode3D(int? Count = null, double? TargetGHz = null)
{
    /// <summary>How many modes when the setup does not say.</summary>
    public const int DefaultCount = 3;
}

/// <summary>
/// The mutable working model behind an open <c>.cem</c>. Framework-free — the editor view model
/// wraps this, the same split <c>TechEditorViewModel</c>/<c>Technology</c> already uses.
/// </summary>
public sealed class EmSetup
{
    /// <summary>
    /// D7 — which analysis this setup is.
    ///
    /// <para><b>The default moved from <c>CrossSection</c> to <c>Auto</c> at L8e</b>, when the
    /// registry that makes <c>Auto</c> mean something arrived. The move is safe in the one way that
    /// matters: auto-selection is CONSERVATIVE, so a geometry kernel A accepts still goes to kernel
    /// A and still produces the identical number. The only behaviour that changes for an existing
    /// <c>.cem</c> is that geometry which used to be REFUSED now runs on kernel B — which is what
    /// shipping the full-wave kernel means.</para>
    ///
    /// <para>Byte-identity is preserved because the omit-at-default rule moved with the default: a
    /// pre-L8b <c>.cem</c> has no field, loads as <c>Auto</c>, and re-serialises with no field.</para>
    /// </summary>
    public EmAnalysisKind AnalysisKind { get; set; } = EmAnalysisKind.Auto;

    /// <summary>The planar mesher's three controls (D3), used only when
    /// <see cref="AnalysisKind"/> is <see cref="EmAnalysisKind.Planar"/>. Separate from
    /// <see cref="Mesh"/> because the two meshers' settings mean genuinely different things — see
    /// <see cref="PlanarMeshSettings"/>'s own remarks on why mirroring kernel A's six controls would
    /// be wrong. <b>Defaults to <see cref="DefaultPlanarMesh"/>, not the engine's own default</b> —
    /// the two differ in one field, below.</summary>
    public PlanarMeshSettings PlanarMesh { get; set; } = DefaultPlanarMesh;

    /// <summary>
    /// <b>PM-3 — the PRODUCT's default planar mesh, stated once.</b> The engine's
    /// <see cref="PlanarMeshSettings.Default"/> keeps <see cref="PlanarMeshSettings.RooftopMerging"/>
    /// off, so every engine-side number and test stays reproducible; the product turns it on ("Mesh as
    /// Merged Shapes" in the EM Setup panel) — the same split <see cref="EdgePortReturn"/> makes, where
    /// the engine's <c>SeriesGap</c> is the product's <c>Ground</c>. This is the one place that
    /// decision lives: the model's own default, the <c>.cem</c>'s omit-at-default rule and the panel's
    /// initial state all read it, and <c>EmRunService</c>, <c>circuitrf check</c> and the panel's own
    /// mesh all take <see cref="PlanarMesh"/> as it stands, so none of them can disagree about it.
    /// </summary>
    public static readonly PlanarMeshSettings DefaultPlanarMesh =
        PlanarMeshSettings.Default with { RooftopMerging = true };

    /// <summary>Display name — the file stem by default.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// R-em-10: the layout this setup analyses, as a workspace-relative path to the <c>.clay</c>,
    /// using the convention <c>CellRef</c>/<c>WorkspaceRefs</c> already establish. <b>Never embedded
    /// geometry</b> — re-running after a layout edit must pick the edit up, and that is only true if
    /// the geometry is read at run time.
    /// </summary>
    public string LayoutRef { get; set; } = "";

    /// <summary>R-em-4b: which conductor stackup layer is the signal. Empty = infer, which is
    /// unambiguous whenever the drawn shapes land on exactly one.</summary>
    public string SignalStackupLayerName { get; set; } = "";

    /// <summary>
    /// <b>RP-1 — the conductor every port in this run returns through, named per run.</b>
    ///
    /// <para><b>Empty means R-em-4</b>, the inferred rule (the top surface of the highest
    /// ground-designated conductor below the lowest analysis level), and every document written
    /// before this field takes that path bit for bit. It is not "no ground": there is no spelling
    /// here for a run without a return plane.</para>
    ///
    /// <para>It exists because R-em-4 is the right rule stated in the wrong vocabulary for two real
    /// situations — a board with two designated planes below the structure where the trace is
    /// genuinely referenced to the LOWER one, and the routine "what if" of comparing the same
    /// structure against two references. The only other way to say either is to un-tick a plane's
    /// "Ground reference" in the TECHNOLOGY, which is shared by every design that uses it, and which
    /// also makes that plane a meshed signal conductor everywhere.</para>
    ///
    /// <para>The named conductor need NOT be ground-designated (the run says so in a note), but it
    /// must exist, must not also be an analysis level, and must lie below the lowest one — see
    /// <c>PlanarExtractor</c>'s RP-1 block for all four.</para>
    ///
    /// <para>Additive and <b>omitted from the file when empty</b>, so a <c>.cem</c> written before
    /// RP-1 round-trips byte-identically — the same rule <see cref="AnalysisLevelNames"/> and
    /// <c>PortZ0s</c> already follow, and no <c>FormatVersion</c> bump.</para>
    /// </summary>
    public string GroundStackupLayerName { get; set; } = "";

    /// <summary>
    /// <b>L9d/D5 — which conductor levels the planar analysis includes, by stackup entry name.</b>
    ///
    /// <para>Empty — the normal case, and what every pre-L9d <c>.cem</c> has — means "infer": every
    /// signal conductor entry that carries artwork. Naming them is how a user analyses two levels of
    /// a four-metal stack, and how they say which two when the answer is not obvious.</para>
    ///
    /// <para>Additive and <b>omitted from the file when empty</b>, so a <c>.cem</c> written before
    /// L9d round-trips byte-identically — the same rule <see cref="PortZ0s"/> already follows.</para>
    /// </summary>
    public List<string> AnalysisLevelNames { get; set; } = [];

    /// <summary>The frequency sweep. Reuses <see cref="FrequencySpec"/> — and the panel reuses
    /// <c>FrequencySpecViewModel</c> — rather than growing a second frequency editor (R-em-11).</summary>
    public FrequencySpec Frequency { get; set; } = new("1", "20", 101, SweepKind.Linear, "GHz", "GHz");

    /// <summary>GitHub discussion 26 — further segments of the frequency plan after <see cref="Frequency"/>: a coarse
    /// broadband sweep plus a finer one around the centre, or single points (a segment of one point). Empty for the
    /// ordinary single sweep, and omitted from the file then, so a <c>.cem</c> written before it round-trips
    /// byte-identically. Read the plan through <see cref="ExpandFrequencies"/>, never <c>Frequency.Expand()</c>.</summary>
    public List<FrequencySpec> AdditionalFrequencies { get; set; } = [];

    /// <summary>Every segment of the plan, <see cref="Frequency"/> first.</summary>
    public IReadOnlyList<FrequencySpec> FrequencyPlan => [Frequency, .. AdditionalFrequencies];

    /// <summary>The frequencies the plan asks for: every segment's, sorted, each once — the union the circuit S-parameter
    /// analysis takes over its own segments.</summary>
    /// <summary>The plan as the 3D solvers take it, from <paramref name="positiveHz"/> — its frequencies above 0 Hz,
    /// sorted. One sweep stays the even sweep it always was; a plan carries its exact list.</summary>
    public Em3dFrequency ToEm3dFrequency(IReadOnlyList<double> positiveHz)
    {
        var even = new Em3dFrequency(positiveHz.Min(), positiveHz.Max(), positiveHz.Count,
            Frequency.Kind == SweepKind.Log ? Em3dSweepKind.Log : Em3dSweepKind.Linear);
        return AdditionalFrequencies.Count == 0 ? even : even with { Kind = Em3dSweepKind.Linear, ListHz = [.. positiveHz] };
    }

    public double[] ExpandFrequencies()
    {
        var all = new SortedSet<double>();
        foreach (var segment in FrequencyPlan)
            foreach (double f in segment.Expand()) all.Add(f);
        return [.. all];
    }

    /// <summary>Per-port reference impedance. Complex is permitted — <c>RFNetwork.ZToS</c> already
    /// handles it, and <c>EmPortZ0Tests</c> pins that it survives the whole path.
    ///
    /// <para>These two are the NEAR-end and FAR-end defaults, and they keep that meaning for any
    /// conductor count: under D3's numbering (port 2k−1 is conductor k's near end, 2k its far end)
    /// every odd port defaults to <see cref="Port1Z0"/> and every even port to
    /// <see cref="Port2Z0"/>. <see cref="PortZ0s"/> overrides an individual port.</para></summary>
    public Complex Port1Z0 { get; set; } = new(50, 0);

    /// <inheritdoc cref="Port1Z0"/>
    public Complex Port2Z0 { get; set; } = new(50, 0);

    /// <summary>
    /// R-cpl-6: per-port reference impedances, one per port in D3 order, overriding the
    /// near/far defaults above. <b>Empty is the normal case</b> — a 2-port or a coupled pair whose
    /// four ports all reference the same impedance stores nothing here, so every <c>.cem</c> written
    /// before L7b loads and re-serializes byte-identically.
    ///
    /// <para>Additive rather than replacing the pair, per R-cpl-6's own "keep every existing
    /// <c>.cem</c> loading unchanged": a list that replaced <see cref="Port1Z0"/>/<see cref="Port2Z0"/>
    /// would have to be synthesised on load for every existing file, and the near/far distinction —
    /// which is the one a user actually thinks in — would be lost.</para>
    /// </summary>
    public List<Complex> PortZ0s { get; set; } = [];

    /// <summary>
    /// brief-sdl-5 R-sdl5-2b: the reference impedance Update Layout from Schematic last wrote into <see cref="PortZ0s"/>,
    /// by port NUMBER — a provenance note, not a setting: nothing reads it to analyse anything. A re-run changes a port's
    /// <see cref="PortZ0s"/> entry to the schematic's new <c>Z</c> only while the entry still holds the value recorded
    /// here, so an impedance the user set in the setup is never overwritten. Empty for a setup the command never wrote
    /// to, which is every setup made before it; omitted from the file when empty.
    /// </summary>
    public SortedDictionary<int, Complex> SchematicPortZ0s { get; set; } = [];

    /// <summary>
    /// The reference impedance of the port in slot <paramref name="slot"/> — <c>portNumber - 1</c>,
    /// which is D3 order for the contiguous numbering every layout this tool creates has: the
    /// explicit override when one is stored, else the near/far default for that end.
    ///
    /// <para><b>The slot is the port NUMBER, not the port's position among the ports that happen to
    /// exist.</b> The per-port TYPE was the other such list until 2026-09-14, when it moved to the
    /// drawing — see <see cref="PortKinds"/>.</para>
    /// </summary>
    public Complex ResolvePortZ0(int slot)
        => slot >= 0 && slot < PortZ0s.Count
            ? PortZ0s[slot]
            : (slot % 2 == 0 ? Port1Z0 : Port2Z0);

    /// <summary>
    /// <b>LEGACY — where a port's TYPE used to live, kept only so an old <c>.cem</c> can still be
    /// read.</b> The type is <c>LabelShape.PortKind</c> now, on the drawing (2026-09-14); that field's
    /// own doc gives the argument, and <see cref="EmPortKindMigration"/> is the one door between the
    /// two. Nothing reads this to decide anything — it is a migration SOURCE and nothing else, and it
    /// is cleared the moment its values reach the labels.
    ///
    /// <para><b>Do not re-introduce a resolver over it.</b> The method that used to read it answered
    /// <see cref="PlanarPortKind.Edge"/> for any slot it had never been told about, which is every
    /// port in a setup whose list is empty — the normal case. That turned silence into an assertion,
    /// and it is the bug this whole move exists to end: a port drawn in the middle of a trace was
    /// redrawn as an edge port one frame after it was placed, and would have been DRIVEN from the
    /// conductor's end (owner report, 2026-09-14).</para>
    ///
    /// <para>Still serialised while a value remains, so a file that has not been opened since the
    /// move does not lose its types. Empty is the normal case and writes nothing.</para>
    /// </summary>
    public List<PlanarPortKind> PortKinds { get; set; } = [];

    /// <summary>All six <see cref="EmMeshSettings"/> fields, each defaulting to
    /// <see cref="EmMeshSettings.Default"/>. R18's 30-second target is reachable because the
    /// defaults are already right, not because the dialogs are fast.</summary>
    public EmMeshSettings Mesh { get; set; } = EmMeshSettings.Default;

    /// <summary>
    /// The Kirschning–Jansen dispersion correction — <b>ON by default</b>, and disabled with a
    /// stated reason when the cross-section is not a single microstrip. The panel asks
    /// <c>QuasiStaticKernel.TryMicrostripDispersion</c> whether it applies rather than re-deriving
    /// the condition, so turning it on costs nothing where it does not apply.
    ///
    /// <para><b>It defaults on because the default sweep runs to 20 GHz.</b> Kernel A holds C at its
    /// quasi-static value, and L8d measured the consequence directly on §10.7's own hero: ε_eff is
    /// +0.86% at 2 GHz, +9.8% at 10 GHz and +23.3% at 20 GHz against the static answer — while the
    /// full-wave kernel tracks Kirschning–Jansen to 0.89% out to 10 GHz. Leaving the correction off
    /// makes the most ordinary run there is (one microstrip, swept over a decade) report a number
    /// that is visibly wrong at the top of its own default band.</para>
    ///
    /// <para>A <c>.cem</c> written before this default flipped carries an explicit <c>false</c> —
    /// the field is non-nullable in the file — so no existing setup changes behaviour; only a newly
    /// created one picks the correction up.</para>
    /// </summary>
    public bool DispersionCorrection { get; set; } = true;

    /// <summary>
    /// <b>Adaptive frequency sampling (planar kernel only) — ON by default.</b> Solve a subset of
    /// the requested frequencies and model the rest, refining until a solved midpoint agrees with
    /// the model to <c>PlanarAdaptiveSettings.Default.Tolerance</c> (1e-3 in |ΔS|).
    ///
    /// <para><b>This is what makes the default sweep usable at all.</b> A de-embedded full-wave
    /// point costs 48 s on one level and 71.9 s on two (L8d/L9d, measured alone), so the default
    /// 101-point sweep is 80 minutes to nearly three hours solved point by point. Adaptive sampling
    /// costs nothing in accuracy at that tolerance — L9e measured the realised worst |ΔS| against
    /// the fully-solved answer at <b>2.5e-5</b>, orders below the kernel's own de-embedding
    /// residual — and the published grid is always exactly the grid that was asked for, with every
    /// solved point carrying the solver's own matrix byte for byte.</para>
    ///
    /// <para><b>The two ends of the sweep and a DC point are never modelled</b> (owner report,
    /// 2026-09-16). Refinement SEEDS on both endpoints and only ever bisects between solved points,
    /// so the first and last frequencies asked for are always solved; and 0 Hz is not part of the
    /// sampling at all — LF1 takes it off the grid before any of this runs and <c>PlanarDcSolve</c>
    /// answers it as a conduction network, which is what makes the bias point a circuit run reads
    /// off that row trustworthy. The points below the fit's floor are the same solve at the user's
    /// own frequency (LF2). All of them are flagged SOLVED in <c>planar.PointSolved</c>; they used
    /// not to be, which is how this reached an owner as "adaptive did not simulate my DC point".</para>
    ///
    /// <para>Turn it off to solve every requested point. Nothing about kernel A is affected: a
    /// cross-section solve is a closed form per frequency and 101 of them are sub-second.</para>
    /// </summary>
    public bool AdaptiveSampling { get; set; } = true;

    /// <summary>
    /// <b>ANT-9 — the resonance search, OFF by default, and the only setting in circuitRF that lets
    /// a sweep publish a frequency you did not ask for.</b>
    ///
    /// <para>Adaptive sampling never adds a point: it bisects the grid it was GIVEN, so a resonance
    /// narrower than your frequency step is invisible to it however hard it refines. That is a
    /// deliberate property and it is what makes every published point yours. It is also, on a
    /// high-Q structure, the reason a sweep can solve 86 % of its grid and still miss its tolerance
    /// by a factor of twenty — measured on a patch board at 10 MHz spacing.</para>
    ///
    /// <para>With this on, the sampler may INSERT frequencies where Im(Z_in) crosses zero, bracket
    /// each one by bisection, and publish f₀, Q and the bandwidth as a diagnostic. <b>Every inserted
    /// frequency is flagged</b>, and every point of the requested grid is still published exactly as
    /// it was, so nothing you asked for moves. It costs real solves — a de-embedded full-wave point
    /// is 48-72 s — which is why it is opt-in and capped rather than simply always on.</para>
    ///
    /// <para><b>It needs adaptive sampling</b>, because it seeds itself from the interpolant that
    /// refinement builds; with adaptive sampling off it is left off and the run SAYS so rather than
    /// silently doing nothing. Nullable + omitted at its default in the <c>.cem</c>, so a file
    /// written before it existed loads and re-serialises byte-identically.</para>
    /// </summary>
    public bool ResonanceSearch { get; set; }

    /// <summary>
    /// <b>M2 (brief-gazz-accuracy-ceiling) — take the ẑẑ Green's function from direct Sommerfeld
    /// integration instead of from the DCIM fit.</b> Planar kernel only, and only meaningful when the
    /// layout carries vias: it is the one block G_A^zz is ever evaluated in.
    ///
    /// <para>Off by default because it is <b>15–45% more per frequency point per via span</b>
    /// (measured). It exists because the fit's own validated range is ρ/λ ≤ 0.1 between via
    /// footprints, and a board with vias further apart than that is refused — this is the way past
    /// that refusal, and the refusal names it.</para>
    /// </summary>
    public bool DirectVerticalKernel { get; set; }

    /// <summary>
    /// <b>M5 — solve the planar system with the AIM accelerator instead of a dense LU.</b> Planar
    /// kernel only, single-level meshes only (a via's ẑ current needs a different grid kernel and the
    /// accelerator refuses it by name).
    ///
    /// <para><b>Off by default, and this is the first user-reachable switch it has ever had.</b> M5
    /// built the accelerator, gated its accuracy and shipped it disabled with no way to enable it
    /// short of editing <c>PlanarSolveSettings</c> in code — so a capability that exists has been
    /// unreachable from the application since it landed. Exposed now on the owner's instruction
    /// (2026-08-14), with its measured trade stated where the user is standing: <b>the win is memory,
    /// not time</b> (~4× less working set past N ≈ 900), and the time crossover is much later, around
    /// N ≈ 3,700.</para>
    ///
    /// <para><b>It DOES move the ceiling, on a single-level mesh — to 12,000, from 5,000</b>
    /// (<c>SurfaceMesher.AcceleratedUnknownCeiling</c>, <c>docs/sonnet-briefs/brief-em-aim-ceiling.md</c>,
    /// 2026-08-14). A multi-level or via-bearing mesh is refused by name regardless of this flag, so
    /// the effective ceiling there is still 5,000. The refusal names turning this on as the first
    /// remedy whenever doing so would let a mesh run — but a de-embedded run's calibration-standard
    /// capacitance step is a separate, always-dense computation this flag does not reach, and can
    /// still refuse a wide-port DUT past 5,000 even with this on; see that brief's own HISTORY.md
    /// closing subsection.</para>
    /// </summary>
    public bool AcceleratedSolve { get; set; }

    /// <summary>
    /// <b>Model the conductor's real thickness in a PLANAR run.</b> The planar kernel's metal is a
    /// zero-thickness sheet, so it has no sidewalls: the fringing capacitance a thick strip adds is
    /// missing and Z0 reads high (measured: a 25 µm copper 50 Ω line read 54.4 Ω where a 3D solve and
    /// the closed-form models read 49.7-50.0 Ω). On, every conductor outline on a level whose copper is
    /// thick is grown by the width at which a sheet has the SAME Z0 as the real thick strip, found from
    /// the 2-D cross-section kernel on this technology's own stackup; port edges stay where they are
    /// drawn. See <c>PlanarThicknessCorrection</c>. <b>Off for a setup that does not say</b> — a
    /// <c>.cem</c> written before the key existed, or one written by hand — so every existing result is
    /// bit-identical; <b>on in a setup the application CREATES</b> (<see cref="CreateNew"/>), so a new
    /// design starts from the Z0 a thick strip really has. A cross-section (kernel A) or 3D run models
    /// real thickness already and ignores this.
    /// </summary>
    public bool ConductorThicknessCorrection { get; set; }

    /// <summary>
    /// A setup as File ▸ New ▸ EM Setup and the layout editor's EM button create it: the model's own
    /// defaults, except that <see cref="ConductorThicknessCorrection"/> is ON. The property itself stays
    /// off when absent, because that is what an existing file means; a NEW setup has no prior result
    /// to keep bit-identical, so it takes the more accurate answer.
    /// </summary>
    public static EmSetup CreateNew(string name, string layoutRef) => new EmSetup
    {
        Name                         = name,
        LayoutRef                    = layoutRef,
        Frequency                    = NewSetupFrequency(),
        ConductorThicknessCorrection = true,
        RobustMeshing                = true,
    }.WithNewSetupSolverDefaults();

    /// <summary>
    /// Owner request, 2026-11-08 — what a NEW setup's 3D solver sections start with: the Draft preset (Palace and EMerge)
    /// and no saved fields (every solver), so a first run is quick and writes S-parameters only. Fields are hundreds of MB
    /// per frequency per driven port; ticking Save fields brings back the sweep's centre. Only new setups take it: an
    /// omitted section still means Standard and the sweep's centre, which is what every setup written before this says.
    /// </summary>
    public EmSetup WithNewSetupSolverDefaults()
    {
        Palace  = new CemPalace  { Quality = PalaceQuality.Draft, SaveFieldsGHz = [] };
        Emerge  = new CemEmerge  { Quality = PalaceQuality.Draft, SaveFieldsGHz = [] };
        OpenEms = new CemOpenEms { SaveFieldsGHz = [] };
        return this;
    }

    /// <summary>
    /// Designer round 32 — the sweep a NEW setup starts with: 0.5 to 8 GHz in 50 MHz steps. The ISM, cellular and
    /// short-range bands a board is usually simulated for sit inside it, where a 1-20 GHz default spent most of its points
    /// (and most of a 3D mesh's size, which follows the top frequency) above them. Only new setups take it; the model's own
    /// default stays 1-20 GHz, which is what a .cem written without a Frequency block has always meant.
    /// </summary>
    public static FrequencySpec NewSetupFrequency() => new("0.5", "8", "0.05", SweepKind.Linear, "GHz", "GHz", "GHz");

    /// <summary>Whether <paramref name="f"/> is still exactly <see cref="NewSetupFrequency"/> — the sweep nobody has chosen.</summary>
    public static bool IsNewSetupFrequency(FrequencySpec f)
    {
        var n = NewSetupFrequency();
        return f.Mode == n.Mode && f.Kind == n.Kind && f.StartExpr == n.StartExpr && f.StopExpr == n.StopExpr &&
               f.StepExpr == n.StepExpr && f.StartUnit == n.StartUnit && f.StopUnit == n.StopUnit && f.StepUnit == n.StepUnit;
    }

    /// <summary>
    /// brief-dfr34-2 R-dfr34-2-2 — the sweep an antenna is offered round the frequency its pattern is read at: half to one and
    /// a half times <paramref name="ghz"/>, in a round step of about a hundredth of it (1, 2 or 5 × a power of ten), with
    /// <paramref name="ghz"/> itself on the grid. 2.45 GHz gives 1.23 to 3.67 GHz in 20 MHz steps. The 0.5-8 GHz new-setup
    /// sweep starts a decade below a 2.4 GHz antenna, which is what made its default air box λ/4 at 0.5 GHz.
    /// </summary>
    public static FrequencySpec SweepAround(double ghz)
    {
        if (!(ghz > 0) || !double.IsFinite(ghz)) throw new ArgumentOutOfRangeException(nameof(ghz));
        double raw = ghz / 100, decade = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double m = raw / decade;
        double step = (m >= 5 ? 5 : m >= 2 ? 2 : 1) * decade;
        double k = Math.Round(ghz / 2 / step, MidpointRounding.AwayFromZero);
        static string G(double v) => Math.Round(v, 9).ToString("G9", System.Globalization.CultureInfo.InvariantCulture);
        return new FrequencySpec(G(ghz - k * step), G(ghz + k * step), G(step), SweepKind.Linear, "GHz", "GHz", "GHz");
    }

    /// <summary>
    /// <b>LFE — what a planar EDGE port's source returns through.</b> <c>Ground</c> (the default) is
    /// the true edge port: the source sits between the ground plane and the strip's end, so the port
    /// discontinuity stays a small series inductance and shunt capacitance at every frequency and the
    /// de-embedding stays well conditioned down to the bottom of the band. <c>SeriesGap</c> is the
    /// pre-LFE port — a delta gap against the floating outermost cell, whose series capacitance made
    /// the de-embedding error grow as 1/f (measured on a 5 mm 50 Ω laminate line: Zc 0.9 Ω and
    /// εeff 1e-4 at 100 MHz where the field solve itself reads 51.7 Ω / 2.46) and worsen as the mesh
    /// was refined. Kept so a pre-LFE result can be reproduced.
    /// </summary>
    public PlanarEdgePortReturn EdgePortReturn { get; set; } = DefaultEdgePortReturn;

    /// <summary>The default <see cref="EdgePortReturn"/>, stated once so the model, the
    /// <c>.cem</c> omit-at-default rule and the provenance hash cannot disagree.</summary>
    public const PlanarEdgePortReturn DefaultEdgePortReturn = PlanarEdgePortReturn.Ground;

    /// <summary>
    /// <b>ANT-12 — the radiation pattern, OFF by default, and the first user-reachable switch the far
    /// field has ever had.</b> Planar kernel only.
    ///
    /// <para>ANT-4 through ANT-11 built the pattern, the metrics, the polarization and the plots, and
    /// every one of those phases recorded that "nothing reaches the CLI or the GUI" because the one
    /// before it did not either. So the whole antenna capability was unreachable from the application
    /// and from <c>circuitrf em</c>: <c>PlanarSolveSettings.FarField</c> could only be set by editing
    /// C#. This field is what closes that, and it closes it for the CLI at the same time — the
    /// <c>em</c> verb takes everything from the <c>.cem</c> and needs no flag of its own.</para>
    ///
    /// <para><b>A pattern is produced at every SOLVED point</b>, which with adaptive sampling on is a
    /// subset of the requested grid: a pattern needs the basis currents, and an interpolated
    /// s-parameter has none behind it. The cubes land in the <c>"farfield"</c> group of the same
    /// <c>DataSet</c> the s-parameters do.</para>
    ///
    /// <para><b>It changes no s-parameter</b> — the pattern is a post-process of currents the solve
    /// already paid for — so it is deliberately NOT in <c>EmSnpProvenance</c>'s hash: an <c>.snp</c>
    /// written with this on is byte-identical to one written with it off, and marking every existing
    /// file stale would be a lie about the network.</para>
    ///
    /// <para>Nullable + omitted at its default in the <c>.cem</c>, so a file written before it
    /// existed loads and re-serialises byte-identically.</para>
    /// </summary>
    public bool RadiationPattern { get; set; }

    /// <summary>
    /// <b>brief-dfr34-2 R-dfr34-2-1 — the frequency the radiation pattern is read at, GHz; null when none is stated.</b> A
    /// driven 3D run that asks for a pattern pads its air box by a quarter wavelength HERE rather than at the sweep's lowest
    /// frequency, which on a default 0.5 GHz start put 150 mm of air round a 1.5 mm board
    /// (<c>Em3dGenerator.DefaultPadding</c>). Read only when <see cref="RadiationPattern"/> is on. It sizes the box only:
    /// each solver still produces the pattern at the points it always has. Omitted from the <c>.cem</c> when null, so a file
    /// written before it existed loads and re-serialises byte-identically.
    /// </summary>
    public double? PatternFrequencyGHz { get; set; }

    /// <summary>
    /// The frequency a driven 3D run sizes its mesh at, GHz; null is the sweep's top. A power amplifier's S-parameters matter most
    /// at the fundamental and a fifth harmonic only has to be reasonable, so sizing the elements for the top of a 5×f sweep costs
    /// about 5³ the elements for accuracy where it matters least. Palace and EMerge read it; openEMS's grid has to carry the sweep's
    /// top to stay stable and does not. The solve still covers the whole sweep. Planar's own is
    /// <see cref="CircuitRF.Engine.Mom.PlanarMeshSettings.MeshFrequencyHz"/>. Omitted from the <c>.cem</c> and the <c>.c3d</c> when null,
    /// so a file written before it existed loads and re-serialises byte-identically.
    /// </summary>
    public double? MeshFrequencyGHz { get; set; }

    /// <summary>
    /// <b>brief-dfr28-3 — Robust meshing: a 3D run removes each layout outline point that lies within a fraction of a
    /// micrometre of the line through its neighbours</b> (<see cref="CircuitRF.Design.Layout.Em3d.Em3dOutlineCleanup"/>),
    /// so the mesher is not handed a sub-micrometre edge or a near-straight run of points. Imported artwork carries both —
    /// arcs flattened into a few-µm segments that end in a 1.4 µm stub, 0.1 µm edges left by merging draws — and on the
    /// TP149 board one such run made Gmsh build a flat tetrahedron whose NaN turned every frequency's S into −I. An outline
    /// moves by at most the tolerance. 3D only: the planar solvers do not read it.
    ///
    /// <para>brief-emb-2 R-emb2-3 — ON unless a setup says otherwise: a file that does not state it reads as on, which is
    /// what every setup the GUI made already wrote, and only <c>false</c> is written. Part of the geometry, so a change to it
    /// makes an earlier result stale.</para>
    /// </summary>
    public bool RobustMeshing { get; set; } = true;

    /// <summary>brief-em3d-139 — Thin Metal Correction: a conductor under <see cref="Em3d.Em3dThinMetalLoss.SkinDepths"/> skin depths thick
    /// has that thickness on its faces on Palace and EMerge, so its resistance levels off at the DC value. On unless a setup says otherwise.</summary>
    public bool ThinMetalCorrection { get; set; } = true;

    /// <summary>
    /// EM-TFR — what a 3D setup makes of every conductor band: <see cref="Em3dConductorMode.Auto"/> takes the
    /// sheet rule, <see cref="Em3dConductorMode.Sheets"/> is the planar model's own idealisation and
    /// <see cref="Em3dConductorMode.Solids"/> gives every band its real thickness. A resistive film stays a
    /// sheet in all three. Part of the geometry, so a change makes an earlier result stale.
    /// </summary>
    public Em3dConductorMode Conductors { get; set; }

    /// <summary>
    /// <b>The conducted power TRP and peak EIRP are referenced to, in dBm</b> (owner request,
    /// 2026-09-11). Read only when <see cref="RadiationPattern"/> is on; 0 dBm by default, which is
    /// what an over-the-air report is written against.
    ///
    /// <para>It is a SETTING rather than a derived number because those two metrics are the only
    /// absolute powers in the registry and this analysis drives a 1 V delta gap — see
    /// <c>PlanarMetricSettings.ReferenceInputPowerDbm</c> for the whole of why. It changes no
    /// s-parameter and is not in <c>EmSnpProvenance</c>'s hash, for the same reason
    /// <see cref="RadiationPattern"/> is not.</para>
    ///
    /// <para>Omitted from the <c>.cem</c> at its default, so a file written before it existed loads
    /// and re-serialises byte-identically.</para>
    /// </summary>
    public double ReferenceInputPowerDbm { get; set; }

    /// <summary>
    /// <b>Set by the loader when a legacy <c>.cem</c> asked for de-embedding to be turned off.
    /// Never serialised, never editable — the switch it came from has been REMOVED.</b>
    ///
    /// <para>PCAL2/R-pcal2-3 added that switch for one reason: the mesh-ceiling refusal recommended
    /// "turn de-embedding off and read the raw solve" in prose, and a refusal naming an unreachable
    /// remedy is not a remedy. The remedy itself was the defect. For an EDGE port the raw solve is
    /// not a degraded answer, it is an OPEN CIRCUIT: the port's cut sits one cell inside the drawn
    /// metal, so the source's outer terminal is an isolated sliver and the port drives nothing but
    /// that sliver's fringing capacitance. Measured on a plain 3.8 mm × 254 µm microstrip — S₁₁ and
    /// S₂₂ = +1, S₂₁ = −107 dB, and DOUBLING the line's length moved S₁₁ in the fourth decimal,
    /// because the raw answer carries no information about the structure at all.</para>
    ///
    /// <para>And for every OTHER port kind the switch was already inert: de-embedding only ever
    /// applies to <c>PlanarPortKind.Edge</c> (<c>PlanarPortResolution.IsDeembeddable</c>), so on a
    /// design of internal delta gaps turning it off changed nothing. A switch whose two settings are
    /// "no effect" and "an open circuit" has no third case to preserve.</para>
    ///
    /// <para>The engine's own <c>PlanarSolveSettings.Deembed</c> is untouched and stays available to
    /// callers who want the raw current distribution rather than port s-parameters — the far-field
    /// and resonance-search paths use it exactly that way. What is removed is the ability for a
    /// <c>.cem</c> to publish that path's s-parameters as an answer.</para>
    ///
    /// <para>A file carrying <c>"Deembed": false</c> still LOADS — refusing it would leave the user
    /// with a document they cannot open to fix — and runs de-embedded, saying so in a warning. It
    /// re-serialises WITHOUT the field, which is the one deliberate exception to the byte-identical
    /// round-trip rule the rest of this file follows: the field no longer has a meaning to preserve.</para>
    /// </summary>
    public bool LegacyRawSolveRequested { get; set; }

    /// <summary>
    /// <b>PCAL2/R-pcal2-2 — publish a de-embedded answer even where the two-line calibration is not
    /// valid, instead of refusing. OFF by default, which is the refusal.</b> Planar kernel only.
    ///
    /// <para>A port whose feed has another conductor inside the run of line the calibration standard
    /// reproduces is de-embedded against a structure that is not the one being corrected, and the
    /// peel divides that mismatch by a₂₁² (~10⁴ at 1 GHz). Measured on a coupled pair: 22 dB of
    /// error in S₂₁ at the bottom of the band, non-passive at 48 of 51 points. Until PCAL2 that
    /// shipped as a note attached to a file that carries no notes.</para>
    ///
    /// <para><b>Named for what it does, not for the check it suppresses</b>, because the check is
    /// not the thing being turned off — the arithmetic still runs outside its validity and the
    /// answer is still wrong in the way the refusal describes. There are real reasons to want it
    /// anyway (comparing against a previous run, debugging, or knowing the port region is not where
    /// your answer lives), and none for it to be quiet: <b>the Touchstone gains a provenance line
    /// recording that the de-embedding was applied outside its validity</b>, which is the half that
    /// survives the file being opened somewhere else.</para>
    ///
    /// <para>Nullable + omitted at its default in the <c>.cem</c>, so a file written before it
    /// existed loads and re-serialises byte-identically.</para>
    /// </summary>
    public bool DeembedOutsideCalibrationValidity { get; set; }

    /// <summary>Workspace-relative override for the written <c>.snp</c>. Empty = the predictable
    /// path <c>EmRunService</c> derives from the layout and setup names (R-em-19).</summary>
    public string SnpOutputPathOverride { get; set; } = "";

    /// <summary>brief-dfr31-4 R-dfr31-4-1 — once a driven run has written its Touchstone, make the cell
    /// <c>&lt;setup&gt;_sim</c>: an SnP of the run with a TermG on every port, an S-parameter analysis and a Data Display
    /// (<see cref="EmSnpSchematic"/>). An existing cell of that name is never overwritten. Off by default; omitted from
    /// the <c>.cem</c> when off. It changes no answer, so it enters no provenance hash.</summary>
    public bool CreateSnpSchematic { get; set; }

    /// <summary>
    /// <b>The part of the layout this setup solves — null solves all of it</b>, which is what every
    /// <c>.cem</c> written before the region existed means. Applied by
    /// <see cref="EmGeometry.ForSetup"/> before either extractor sees the geometry; see
    /// <see cref="EmSolveRegion"/> for why it lives here rather than in the layout.
    /// </summary>
    public EmSolveRegion? SolveRegion { get; set; }

    // ── brief-em3d-3 — the 3D setup's fields (R-em3d3-3) ─────────────────────────────────────────
    //
    // All additive, nullable and omitted at default: a planar .cem gains no byte. A setup switched
    // to 3D keeps every planar field (and check notes them, at info); switched back, they are where
    // they were. That is em-3d.md §4.2's "both sections kept", applied to planar vs 3D.

    /// <summary>R-em3d3-3a — which 3D solver(s) this setup runs. <see cref="Em3dSolverChoice.None"/> is a
    /// planar setup.</summary>
    public Em3dSolverChoice Solver3D { get; set; } = Em3dSolverChoice.None;

    /// <summary>True when this setup is a 3D one.</summary>
    public bool Is3D => !Solver3D.IsNone;

    /// <summary>
    /// R-em3d3-4 — the temperature every solid's conductivity is evaluated at, °C. Null is
    /// <see cref="DefaultOperatingTempC"/>. Read by the 3D generator only; the planar solvers take
    /// each stackup entry's σ as the technology states it.
    /// </summary>
    public double? OperatingTempC { get; set; }

    /// <summary>R-em3d3-4a / owner decision D3: a 3D result at 20 °C is comparable with a planar
    /// one as shipped, since a stackup entry naming a material resolves to its σ at 20 °C.</summary>
    public const double DefaultOperatingTempC = 20.0;

    /// <summary>R-em3d3-6 — the air box, per face. Null is the generator's default on every face.</summary>
    public EmAirBox? AirBox { get; set; }

    /// <summary>Palace's own section (em-3d.md §4.2; brief-em3d-7 R-em3d7-6a) — see <see cref="PalaceSettings"/> for the defaults.
    /// Null takes circuitRF's defaults.</summary>
    public CemPalace? Palace { get; set; }

    /// <summary>
    /// brief-em3d-22 R-em3d22-1a — what the 3D solve asks: S over the sweep, or a capacitance or
    /// inductance matrix. <see cref="Em3dProblemType.Driven"/> is every setup written before static
    /// solves existed, and is omitted from the file.
    /// </summary>
    public Em3dProblemType Problem3D { get; set; }

    /// <summary>True for an electrostatic or magnetostatic setup.</summary>
    public bool IsStatic3D => Problem3D is Em3dProblemType.Electrostatic or Em3dProblemType.Magnetostatic;

    /// <summary>brief-em3d-73 R-em3d73-5a — true for a thermal setup (<c>Problem3D: Thermal</c>): solved by circuitRF's own
    /// thermal solver, never by Palace or openEMS, and embedded in a <c>.c3d</c> only (D1).</summary>
    public bool IsThermal => Problem3D == Em3dProblemType.Thermal;

    /// <summary>brief-em3d-73 R-em3d73-5b — a thermal setup's section; null when the setup states none.</summary>
    public CemThermal? Thermal { get; set; }

    /// <summary>brief-sar-6 R-sar6-1 — the setup's Sar group; null when it asks for no SAR.</summary>
    public CemSar? Sar { get; set; }

    /// <summary>brief-sar-6 — true when the setup asks for SAR (a driven 3D setup with a Sar group).</summary>
    public bool RequestsSar => Sar is not null && Is3D && Problem3D == Em3dProblemType.Driven;

    /// <summary>R-em3d22-2a — a static solve's terminals, in matrix order.</summary>
    public List<EmTerminal3D> Terminals3D { get; set; } = [];

    /// <summary>R-em3d22-2a — the net that is the matrix's reference. Empty is the ground the
    /// generator already uses: the ground-reference conductors, and the PEC floor when there is one.</summary>
    public string Ground3D { get; set; } = "";

    /// <summary>brief-dfr31-1 R-dfr31-1-5 — what an electrostatic solve does with metal in no terminal and not ground.</summary>
    public EmUnassignedConductors UnassignedConductors3D { get; set; } = EmUnassignedConductors.Float;

    /// <summary>brief-em3d-23 R-em3d23-2a — per-port 3D settings. A port with no entry is lumped.</summary>
    public List<EmPort3D> Ports3D { get; set; } = [];

    /// <summary>brief-emb-1 R-emb1-2 — the component ports this setup builds on the parts placed on its 3D view's layout, numbered
    /// after the document's own ports in list order. On a planar <c>.cem</c> each is a strip across the part's gap cut by a delta
    /// gap, in the solved problem only (brief-lcp-1, <see cref="EmPlanarComponentPorts"/>); a 3D <c>.cem</c> on a layout builds
    /// them in an in-memory 3D view of it, as a <c>.c3d</c> does (brief-lcp-5, <c>Em3dSetupProblem</c>).</summary>
    public List<EmComponentPort> ComponentPorts { get; set; } = [];

    /// <summary>The <see cref="EmComponentPort.Refdes"/> that names every placed two-terminal part.</summary>
    public const string AllComponents = "*";

    /// <summary>brief-em3d-23 R-em3d23-4a — an eigenmode solve's count and target. Null takes both
    /// defaults: <see cref="EmEigenmode3D.DefaultCount"/> modes above the sweep's start.</summary>
    public EmEigenmode3D? Eigenmode { get; set; }

    /// <summary>The kind the setup states for port <paramref name="number"/>.</summary>
    public Em3dPortKind PortKind3D(int number)
        => Ports3D.LastOrDefault(p => p.Port == number)?.Kind ?? Em3dPortKind.Lumped;

    /// <summary>brief-dfr28-4 R-dfr28-4-5 — the return layer the setup states for port <paramref name="number"/>,
    /// or null for Automatic.</summary>
    public string? PortReturn3D(int number)
        => Ports3D.LastOrDefault(p => p.Port == number)?.Return is { Length: > 0 } r ? r : null;

    /// <summary>True when any port is stated as a wave port.</summary>
    public bool HasWavePorts3D => Ports3D.Any(p => p.Kind == Em3dPortKind.Wave);

    /// <summary>
    /// R-em3d22-1c — what a static setup keeps but does not read, by <c>.cem</c> key: the sweep when it
    /// is not the default, and the port impedances.
    /// </summary>
    public IReadOnlyList<string> DrivenOnlyFieldsSet()
    {
        var set = new List<string>();
        var d = new EmSetup().Frequency;
        var f = Frequency;
        if ((f.StartExpr, f.StopExpr, f.StepExpr, f.NumPoints, f.Mode, f.Kind, f.StartUnit, f.StopUnit, f.StepUnit) !=
            (d.StartExpr, d.StopExpr, d.StepExpr, d.NumPoints, d.Mode, d.Kind, d.StartUnit, d.StopUnit, d.StepUnit))
            set.Add("Frequency");
        if (AdditionalFrequencies.Count > 0) set.Add("AdditionalFrequencies");
        if (PortZ0s.Count > 0 || Port1Z0 != new EmSetup().Port1Z0 || Port2Z0 != new EmSetup().Port2Z0) set.Add("PortZ0s");
        if (CreateSnpSchematic) set.Add("CreateSnpSchematic");
        return set;
    }

    /// <summary>openEMS's own section (em-3d.md §4.2): the grid fields of brief-em3d-8 — see
    /// <see cref="CemOpenEms.ResolveGrid"/> for the defaults. Null takes circuitRF's defaults.</summary>
    public CemOpenEms? OpenEms { get; set; }

    /// <summary>brief-emi-4 R-emi4-1 — EMerge's own section — see <see cref="EmergeSettings"/> for the defaults. Null takes
    /// circuitRF's defaults.</summary>
    public CemEmerge? Emerge { get; set; }

    /// <summary>
    /// R-em3d3-3b — the planar-only fields this setup sets away from their defaults, by their
    /// <c>.cem</c> key. A 3D setup ignores every one of them; <c>check</c> names them at info so a
    /// setting that is kept but not read is never silent.
    /// </summary>
    public IReadOnlyList<string> PlanarOnlyFieldsSet()
    {
        var set = new List<string>();
        if (AnalysisKind != EmAnalysisKind.Auto)                 set.Add("AnalysisKind");
        if (SignalStackupLayerName.Length > 0)                   set.Add("SignalStackupLayerName");
        if (AnalysisLevelNames.Count > 0)                        set.Add("AnalysisLevelNames");
        if (PlanarMesh != DefaultPlanarMesh)                     set.Add("PlanarMesh");
        if (Mesh != EmMeshSettings.Default)                      set.Add("Mesh");
        if (!AdaptiveSampling)                                   set.Add("AdaptiveSampling");
        if (ResonanceSearch)                                     set.Add("ResonanceSearch");
        if (DirectVerticalKernel)                                set.Add("DirectVerticalKernel");
        if (AcceleratedSolve)                                    set.Add("AcceleratedSolve");
        if (ConductorThicknessCorrection)                        set.Add("ConductorThicknessCorrection");
        if (EdgePortReturn != DefaultEdgePortReturn)             set.Add("EdgePortReturn");
        // brief-em3d-31 — RadiationPattern is read by a driven 3D run too, so it is not planar-only;
        // a static or eigenmode 3D setup says so through Em3dRunService.RadiationPatternIgnored.
        if (DeembedOutsideCalibrationValidity)                   set.Add("DeembedOutsideCalibrationValidity");
        return set;
    }

    /// <summary>
    /// The planar-mesh fields this setup states away from their defaults that <c>Auto</c> DISCARDS,
    /// as <c>(.cem key, stated value, value used)</c>. <see cref="PlanarMeshSettings.Resolved"/> is the
    /// contract: with <c>Auto</c> on, cells per wavelength and edge cells are chosen for the user and every
    /// other field survives (the edge mesh too, since EMB-4). A stated value that is then not read is invisible in a
    /// hand-written file — the mesh notes report the value USED, and nothing says why it differs — so
    /// <c>check</c> names each one and the switch that makes it take effect.
    /// </summary>
    public IReadOnlyList<(string Key, string Stated, string Used)> AutoOverriddenMeshFields()
    {
        var m = PlanarMesh;
        if (!m.Auto) return [];
        var used = m.Resolved;
        var list = new List<(string, string, string)>();
        if (m.CellsPerWavelength != used.CellsPerWavelength)
            list.Add(("CellsPerWavelength", Inv(m.CellsPerWavelength), Inv(used.CellsPerWavelength)));
        if (m.EdgeMesh != used.EdgeMesh)
            list.Add(("EdgeMesh", m.EdgeMesh ? "true" : "false", used.EdgeMesh ? "true" : "false"));
        if (m.EdgeCells != used.EdgeCells)
            list.Add(("EdgeCells", Inv(m.EdgeCells), Inv(used.EdgeCells)));
        return list;

        static string Inv(int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// EMB-4 R-emb4-7 — the planar-mesh fields this setup STATES away from their defaults while <c>Auto</c> is on and
    /// that win over it (<see cref="PlanarMeshSettings.Resolved"/>: the edge mesh, the cells across the narrowest
    /// conductor, the detail floor), as <c>(.cem key, stated value)</c>. <c>check</c> notes them, so a reader of the file
    /// knows Auto chose only the rest.
    /// </summary>
    public IReadOnlyList<(string Key, string Stated)> AutoKeptMeshFields()
    {
        var m = PlanarMesh;
        if (!m.Auto) return [];
        var list = new List<(string, string)>();
        if (m.EdgeMesh != PlanarMeshSettings.DefaultEdgeMesh)
            list.Add(("EdgeMesh", m.EdgeMesh ? "true" : "false"));
        if (m.MinCellsAcrossConductor != PlanarMeshSettings.DefaultMinCellsAcrossConductor)
            list.Add(("MinCellsAcrossConductor", Inv(m.MinCellsAcrossConductor)));
        if (m.DetailFloorDivisor != PlanarMeshSettings.DefaultDetailFloorDivisor)
            list.Add(("DetailFloorDivisor", Inv(m.DetailFloorDivisor)));
        return list;

        static string Inv(int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public EmSetup Clone() => new()
    {
        AnalysisKind           = AnalysisKind,
        PlanarMesh             = PlanarMesh,     // record, immutable
        Name                   = Name,
        LayoutRef              = LayoutRef,
        SignalStackupLayerName = SignalStackupLayerName,
        GroundStackupLayerName = GroundStackupLayerName,
        AnalysisLevelNames     = [.. AnalysisLevelNames],
        Frequency              = Frequency,      // immutable
        AdditionalFrequencies  = [.. AdditionalFrequencies],
        Port1Z0                = Port1Z0,
        Port2Z0                = Port2Z0,
        PortZ0s                = [.. PortZ0s],   // a fresh list: Complex is immutable, the list is not
        SchematicPortZ0s       = new(SchematicPortZ0s),
        PortKinds              = [.. PortKinds], // likewise: the enum is a value, the list is not
        Mesh                   = Mesh,           // record, immutable
        DispersionCorrection   = DispersionCorrection,
        AdaptiveSampling       = AdaptiveSampling,
        ResonanceSearch        = ResonanceSearch,
        DirectVerticalKernel   = DirectVerticalKernel,
        AcceleratedSolve       = AcceleratedSolve,
        ConductorThicknessCorrection = ConductorThicknessCorrection,
        EdgePortReturn         = EdgePortReturn,
        LegacyRawSolveRequested = LegacyRawSolveRequested,
        DeembedOutsideCalibrationValidity = DeembedOutsideCalibrationValidity,
        RadiationPattern       = RadiationPattern,
        PatternFrequencyGHz    = PatternFrequencyGHz,
        MeshFrequencyGHz       = MeshFrequencyGHz,
        RobustMeshing          = RobustMeshing,
        ThinMetalCorrection    = ThinMetalCorrection,
        Conductors             = Conductors,
        ReferenceInputPowerDbm = ReferenceInputPowerDbm,
        SnpOutputPathOverride  = SnpOutputPathOverride,
        CreateSnpSchematic     = CreateSnpSchematic,
        SolveRegion            = SolveRegion,    // record, immutable
        Solver3D               = Solver3D,
        OperatingTempC         = OperatingTempC,
        AirBox                 = AirBox,         // record, immutable
        Palace                 = Palace?.Clone(),
        OpenEms                = OpenEms?.Clone(),
        Emerge                 = Emerge?.Clone(),
        Problem3D              = Problem3D,
        Terminals3D            = [.. Terminals3D],   // records, immutable
        Ground3D               = Ground3D,
        UnassignedConductors3D = UnassignedConductors3D,
        Ports3D                = [.. Ports3D],       // records, immutable
        ComponentPorts         = [.. ComponentPorts], // records, immutable
        Eigenmode              = Eigenmode,
        Thermal                = Thermal?.Clone(),
        Sar                    = Sar?.Clone(),
    };

    /// <summary>The extraction settings this setup implies — the one place the two are married,
    /// so the panel and the run service cannot disagree about them.</summary>
    public EmExtractionSettings ToExtractionSettings(string? subjectDescription = null)
        => new(SignalStackupLayerName is { Length: > 0 } s ? s : null,
               Port1Z0, Port2Z0,
               subjectDescription ?? (LayoutRef is { Length: > 0 } l ? l : null),
               PortZ0s.Count > 0 ? [.. PortZ0s] : null,
               AnalysisLevelNames.Count > 0 ? [.. AnalysisLevelNames] : null,
               GroundStackupLayerName is { Length: > 0 } g ? g : null,
               ConductorThicknessCorrection);
}
