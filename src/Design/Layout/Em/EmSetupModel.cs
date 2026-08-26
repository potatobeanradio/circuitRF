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
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em;

// L8e — EmAnalysisKind MOVED to src/Engine/Mom/EmKernelRegistry.cs.
//
// L8b defined it here because there was no registry and the only consumer was the .cem. D1 keys the
// registry on the analysis kind, and the registry lives in the engine (Ui → Engine), so the enum
// went with it. The member NAMES are unchanged, so every .cem round-trips byte-identically; the
// enum gained one member, Auto, which no pre-L8e file can contain.

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
    /// be wrong.</summary>
    public PlanarMeshSettings PlanarMesh { get; set; } = PlanarMeshSettings.Default;

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
    /// The reference impedance of port <paramref name="index"/> (0-based, D3 order): the explicit
    /// override when one is stored, else the near/far default for that end.
    /// </summary>
    public Complex ResolvePortZ0(int index)
        => index >= 0 && index < PortZ0s.Count
            ? PortZ0s[index]
            : (index % 2 == 0 ? Port1Z0 : Port2Z0);

    /// <summary>
    /// <b>Per-port TYPE, for the full-wave planar kernel: an edge port, an internal delta gap, or
    /// an internal port to the ground plane.</b>
    /// One entry per port in the extractor's own port order, and <b>empty is the normal case</b> —
    /// every port is an edge port unless something here says otherwise, so a <c>.cem</c> written
    /// before this existed loads and re-serialises byte-identically, exactly as
    /// <see cref="PortZ0s"/> and <see cref="AnalysisLevelNames"/> do.
    ///
    /// <para>It is here rather than on the port LABEL for the same reason the reference impedance is:
    /// a layout is geometry. The same artwork can be analysed with a gap in the middle of a trace in
    /// one setup and driven from its ends in another, and neither should edit the drawing.</para>
    ///
    /// <para>The cross-section (quasi-static) kernel has no use for it — its ports are the ends of a
    /// uniform line by construction — so the panel offers it only for a planar analysis.</para>
    /// </summary>
    public List<PlanarPortKind> PortKinds { get; set; } = [];

    /// <summary>
    /// The type of port <paramref name="index"/> (0-based, in extractor order): the stored value
    /// when there is one, else <see cref="PlanarPortKind.Edge"/>.
    /// </summary>
    public PlanarPortKind ResolvePortKind(int index)
        => index >= 0 && index < PortKinds.Count ? PortKinds[index] : PlanarPortKind.Edge;

    /// <summary>
    /// Does this setup declare any port the uniform-line kernel cannot represent — an internal delta
    /// gap or an internal port?
    ///
    /// <para><b>The one question that has to be asked BEFORE the kernel is chosen.</b> A uniform line
    /// with an internal port on it is, geometrically, still a uniform cross-section — so the
    /// cross-section extractor accepts it and <c>Auto</c> prefers that kernel, which has no interior
    /// cut, no via, no mesh to put either on, and no way to say so. The port would simply not be
    /// there, and the run would return a complete, plausible answer for a structure without it.</para>
    /// </summary>
    public bool DeclaresInternalPort()
    {
        foreach (var k in PortKinds) if (k != PlanarPortKind.Edge) return true;
        return false;
    }

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
    /// <para>Turn it off to solve every requested point. Nothing about kernel A is affected: a
    /// cross-section solve is a closed form per frequency and 101 of them are sub-second.</para>
    /// </summary>
    public bool AdaptiveSampling { get; set; } = true;

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

    /// <summary>Workspace-relative override for the written <c>.snp</c>. Empty = the predictable
    /// path <c>EmRunService</c> derives from the layout and setup names (R-em-19).</summary>
    public string SnpOutputPathOverride { get; set; } = "";

    public EmSetup Clone() => new()
    {
        AnalysisKind           = AnalysisKind,
        PlanarMesh             = PlanarMesh,     // record, immutable
        Name                   = Name,
        LayoutRef              = LayoutRef,
        SignalStackupLayerName = SignalStackupLayerName,
        AnalysisLevelNames     = [.. AnalysisLevelNames],
        Frequency              = Frequency,      // immutable
        Port1Z0                = Port1Z0,
        Port2Z0                = Port2Z0,
        PortZ0s                = [.. PortZ0s],   // a fresh list: Complex is immutable, the list is not
        PortKinds              = [.. PortKinds], // likewise: the enum is a value, the list is not
        Mesh                   = Mesh,           // record, immutable
        DispersionCorrection   = DispersionCorrection,
        AdaptiveSampling       = AdaptiveSampling,
        DirectVerticalKernel   = DirectVerticalKernel,
        AcceleratedSolve       = AcceleratedSolve,
        SnpOutputPathOverride  = SnpOutputPathOverride,
    };

    /// <summary>The extraction settings this setup implies — the one place the two are married,
    /// so the panel and the run service cannot disagree about them.</summary>
    public EmExtractionSettings ToExtractionSettings(string? subjectDescription = null)
        => new(SignalStackupLayerName is { Length: > 0 } s ? s : null,
               Port1Z0, Port2Z0,
               subjectDescription ?? (LayoutRef is { Length: > 0 } l ? l : null),
               PortZ0s.Count > 0 ? [.. PortZ0s] : null,
               AnalysisLevelNames.Count > 0 ? [.. AnalysisLevelNames] : null);
}
