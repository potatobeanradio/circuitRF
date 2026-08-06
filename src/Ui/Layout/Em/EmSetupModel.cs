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

namespace CircuitRF.Ui.Layout.Em;

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

    /// <summary>All six <see cref="EmMeshSettings"/> fields, each defaulting to
    /// <see cref="EmMeshSettings.Default"/>. R18's 30-second target is reachable because the
    /// defaults are already right, not because the dialogs are fast.</summary>
    public EmMeshSettings Mesh { get; set; } = EmMeshSettings.Default;

    /// <summary>The Kirschning–Jansen dispersion correction — <b>off by default</b>, and disabled
    /// with a stated reason when the cross-section is not a single microstrip. The panel asks
    /// <c>QuasiStaticKernel.TryMicrostripDispersion</c> whether it applies rather than re-deriving
    /// the condition.</summary>
    public bool DispersionCorrection { get; set; }

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
        Mesh                   = Mesh,           // record, immutable
        DispersionCorrection   = DispersionCorrection,
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
