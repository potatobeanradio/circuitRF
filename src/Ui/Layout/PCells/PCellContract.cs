// The PCell contract (docs/design/pcell-contract.md). Framework-free — no Avalonia, no SkiaSharp.
// A PCell's layout is generated rather than stored: Generate(parameters, technology) -> {shapes, pins}.

namespace CircuitRF.Ui.Layout.PCells;

/// <summary>R8: the contract carries a version from day one — costs nothing with one version,
/// cannot be retrofitted once third-party cells exist. Bump this and record the reason in
/// pcell-contract.md whenever the Generate signature or its guarantees change.</summary>
public static class PCellContractVersion
{
    public const int Current = 1;
}

/// <summary>
/// R3: a pin carries name, location, layer, and width + outward direction — the last two because
/// a microstrip connection is an edge, not a point, and a bend needs to know which way its arm
/// faces. <see cref="OutwardDirectionDeg"/> is a continuous angle (0 = +X, 90 = +Y, ...) rather
/// than a 4-way enum, since layout's own angle mode supports any-angle geometry (an MBend's pin 2
/// direction is set by its own Angle parameter, not necessarily a multiple of 90°).
/// </summary>
public sealed record PCellPin(
    string Name,
    long X,
    long Y,
    LayerKey Layer,
    long WidthDbu,
    double OutwardDirectionDeg);

/// <summary>R3: shapes and pins, nothing else — a PCell describes one cell's contents in
/// cell-local coordinates (§6 of the design doc: no placement transform, that's the instance's
/// job). <paramref name="Diagnostics"/> (additive, brief-L5-followups-2.md §2.2 finding) carries
/// any human-readable warning text a generator wants surfaced (e.g. R-klp-10's curvature warning) —
/// a PURE generator (R5) has no message sink of its own to post to directly, so this is the ONE
/// channel; every caller that invokes a <see cref="PCellGenerator"/> is responsible for surfacing a
/// non-empty <see cref="Diagnostics"/> through its own <c>IMessageSink</c>. Null/empty = nothing to
/// report — the common case for every other generator, which needed no change.</summary>
public sealed record PCellResult(
    IReadOnlyList<LayoutShape> Shapes, IReadOnlyList<PCellPin> Pins, IReadOnlyList<string>? Diagnostics = null);

/// <summary>
/// R9: layer selection (Signal Layer + Ground Reference) is per-instance overridable and is not a
/// declared cell parameter (R2's "one list") — it travels alongside the resolved
/// <see cref="Technology"/> as a resolution detail (pcell-contract.md §5.2: "which technology
/// [and, by the same reasoning, which layer] is a resolution question, not a contract question").
/// Null override fields mean "default from the stackup" (<see cref="SubstrateResolver"/>).
/// </summary>
public sealed record PCellLayerSelection(string? SignalLayerNameOverride, string? GroundLayerNameOverride)
{
    public static readonly PCellLayerSelection Default = new(null, null);
}

/// <summary>
/// R1-R6: Generate(parameters, technology) -&gt; {shapes, pins}. Parameters are the resolved
/// values of the cell's own declared parameters (R2: the SAME list the symbol shows — e.g. MLIN's
/// W and L, in SI metres per R-pc-6). Technology supplies the layer table (used to pick the
/// drawing layer via <paramref name="layerSelection"/>); <c>null</c> when no technology resolved,
/// in which case the generator still produces geometry (on a fallback layer key) per §2 of
/// brief-L5a-pcell-contract-and-microstrip.md — only the ELECTRICAL stamp refuses without a
/// technology, and that is a separate code path (the microstrip <c>ComponentModel</c>s in
/// <c>src/Core/Devices/</c>), not this one.
///
/// R5 (purity): implementations MUST be pure functions of their three inputs — no file reads, no
/// clock, no ambient/global state, no randomness. This is not a style preference:
/// <see cref="PCellGeometryCache"/> keys on (generator id, parameter values, technology) and an
/// impure generator breaks that cache silently.
/// </summary>
public delegate PCellResult PCellGenerator(
    IReadOnlyDictionary<string, double> parameters,
    Technology? technology,
    PCellLayerSelection layerSelection);
