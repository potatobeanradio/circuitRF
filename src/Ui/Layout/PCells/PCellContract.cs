// The PCell contract (docs/design/pcell-contract.md). Framework-free — no Avalonia, no SkiaSharp.
// A PCell's layout is generated rather than stored: Generate(parameters, technology) -> {shapes, pins}.

namespace CircuitRF.Ui.Layout.PCells;

/// <summary>R8: the contract carries a version from day one — costs nothing with one version,
/// cannot be retrofitted once third-party cells exist. Bump this and record the reason in
/// pcell-contract.md whenever the Generate signature or its guarantees change.</summary>
public static class PCellContractVersion
{
    /// <summary>
    /// 2 (2026-08-03): parameters carry KINDED values rather than bare doubles
    /// (<see cref="PCellValue"/>), and R5's purity rule is restated in terms of determinism rather
    /// than of file access. Both had to land before any third party writes a cell — a script host
    /// reads its own modules by nature, so the old wording was unsatisfiable for one, and a cell
    /// that needs a model name cannot express it through a double. Neither is retrofittable once
    /// cells exist in the wild, which is what this version field is for.
    /// </summary>
    public const int Current = 2;
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

/// <summary>
/// How a <see cref="PCellHandle"/>'s grip travels (pcell-parameter-handles.md §2.1).
/// </summary>
public enum PCellHandleKind
{
    /// <summary>The grip travels along a straight line through the anchor in direction
    /// <see cref="PCellHandle.AxisDeg"/>. A drag's projection onto that line is the scalar the
    /// parameter follows.</summary>
    Linear,

    /// <summary>
    /// The grip swings about the anchor. The angle of (cursor − anchor), measured counter-clockwise
    /// from <see cref="PCellHandle.AxisDeg"/>, is the scalar the parameter follows — so
    /// <see cref="PCellHandle.AxisDeg"/> is a REFERENCE direction here, not a direction of travel.
    ///
    /// <para>Everything else is unchanged: the sensitivity is still measured rather than declared,
    /// the projection is still one scalar, and the solver never learns which kind it is holding. The
    /// only differences are the projection formula and that the scalar is in DEGREES, which is why
    /// the probe and convergence thresholds have their own angular values.</para>
    /// </summary>
    Angular,
}

/// <summary>
/// How eagerly a drag on this cell should redraw its artwork (pcell-parameter-handles.md R-pch-10).
/// </summary>
public enum PCellPreviewMode
{
    /// <summary>The host times the first regeneration of a gesture and decides. The default, and
    /// right for every cell whose cost it cannot know in advance.</summary>
    Auto,

    /// <summary>
    /// Always defer: the pre-drag artwork stands, the grip and readout follow the cursor, and the
    /// cell regenerates once on release.
    ///
    /// <para><b>For a generator that already knows it is too expensive to redraw per frame</b> — a
    /// cell with hundreds of shapes, or one that issues many boolean round trips per generate. Auto
    /// reaches the same conclusion, but only after spending one full regeneration to find out;
    /// declaring it skips that. An author who is wrong about their own cell costs the user a live
    /// preview they could have had, never correctness — the committed value is identical either
    /// way.</para>
    /// </summary>
    Deferred,
}

/// <summary>
/// What KIND of physical quantity a handle's parameter is — the one thing the host genuinely cannot
/// measure for itself, and the only reason it needs to know.
///
/// <para><b>This does NOT reintroduce the declared <c>scale</c> R-pch-2 rejected.</b> That rule is
/// about SENSITIVITY (how much the parameter changes per unit of travel), which is still measured by
/// regenerating and is still unit-free. This says only what the parameter IS, which the host needs for
/// two things it cannot do otherwise: printing a drag readout a human can read (a raw
/// <c>0.0118872</c> is not a width), and honouring the layout's own snap grid (a user who set 1 mil
/// snapping expects the committed width to land on a whole mil, not 468.00006 of one).</para>
///
/// <para><see cref="Unspecified"/> is the default and is not a defect: the readout falls back to the
/// raw value and the grid is not applied, which is exactly how every handle behaved before this
/// existed. A script-supplied handle that says nothing therefore keeps working unchanged.</para>
/// </summary>
public enum PCellHandleQuantity
{
    /// <summary>Nothing declared — raw readout, no grid snapping.</summary>
    Unspecified,

    /// <summary>A length. In-process that is SI metres (R-pc-6); the host converts for display and
    /// quantizes to the layout's own snap step.</summary>
    Length,

    /// <summary>An angle in degrees. Displayed with a degree sign; never quantized to a length
    /// grid.</summary>
    Angle,
}

/// <summary>
/// The parameter a <see cref="PCellHandle"/> drives when dragged PERPENDICULAR to its own axis
/// (pcell-parameter-handles.md R-pch-4a). Null on an ordinary one-degree-of-freedom grip, which is
/// the common case.
///
/// <para><b>This is not the two-parameter apportionment R-pch-4 rules out.</b> That rule exists
/// because splitting one drag between two parameters needs a tie-break and every tie-break is
/// arbitrary. An orthogonal decomposition needs none: travel along the axis and travel across it are
/// independent scalars, each with exactly one parameter, and the split is unique. What R-pch-4
/// forbids is guessing; this does not guess.</para>
/// </summary>
public sealed record PCellHandleCrossAxis(
    string Parameter,
    string? Label = null,
    double? Min = null,
    double? Max = null,
    PCellHandleQuantity Quantity = PCellHandleQuantity.Unspecified);

/// <summary>
/// One draggable grip on generated artwork — pcell-parameter-handles.md R-pch-1/R-pch-4.
///
/// <para><b>Coordinates are cell-local DBU</b>, exactly the frame <see cref="PCellPin"/> already
/// uses, so the instance transform applies to both identically and neither needs its own
/// convention.</para>
///
/// <para><b>The generator never states how much the parameter changes per unit of travel</b>
/// (R-pch-2). It states only which parameter, where the grip is, what it measures from, and which
/// way it moves; the host measures the sensitivity by regenerating with a perturbation
/// (<see cref="PCellHandleSolver"/>). Declaring an affine <c>scale</c> instead was rejected because
/// it would differ between an in-process generator (lengths in SI metres) and a script one (lengths
/// already in DBU) for the same cell, and because it cannot describe a non-linear relationship at
/// all.</para>
///
/// <para><b><see cref="Min"/>/<see cref="Max"/> are in the parameter's own units</b> — SI metres for
/// a length in-process, DBU for the same length on the wire, matching what the parameter values
/// themselves already are on each side. They are a convenience that lets the editor stop the grip at
/// the bound and name it; a generator that clamps internally needs neither, because regeneration is
/// authoritative (R-pch-3).</para>
/// </summary>
public sealed record PCellHandle(
    string Parameter,
    long AnchorX,
    long AnchorY,
    long X,
    long Y,
    double AxisDeg,
    PCellHandleKind Kind = PCellHandleKind.Linear,
    string? Label = null,
    double? Min = null,
    double? Max = null,
    PCellHandleCrossAxis? Cross = null,
    bool KeepAnchorFixed = false,
    PCellHandleQuantity Quantity = PCellHandleQuantity.Unspecified)
{
    /// <summary>What the readout calls it — the generator's own <see cref="Label"/>, else the
    /// parameter name.</summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Parameter : Label!;

    // KeepAnchorFixed (R-pch-4b): "hold my ANCHOR still in world space while I am dragged."
    //
    // A generator cannot move its own origin — R4 pins pin 1 at (0,0) — so without this, dragging a
    // cell's LEFT edge grows it to the right, which is the opposite of what the gesture means. The
    // host instead translates the INSTANCE so that the anchor keeps the world position it had, which
    // is the one thing a generator structurally cannot do for itself.
    //
    // It is expressed on the anchor rather than as a free "fixed point" because the anchor is
    // already what the grip measures FROM, and it is already re-emitted on every generate — so the
    // host can read where it moved to instead of being told a rule for predicting it. Declaring the
    // OPPOSITE edge as the anchor and setting this flag is the whole of "drag this end, keep the
    // other end still".
    //
    // A no-op when the anchor does not move (an anchor at the cell origin, say), so it is safe and
    // self-documenting to set on every edge grip of a set rather than only the ones that need it.

    /// <summary>The perpendicular axis's own label, on a two-axis grip.</summary>
    public string CrossDisplayLabel =>
        Cross is null ? "" : string.IsNullOrWhiteSpace(Cross.Label) ? Cross.Parameter : Cross.Label!;

    /// <summary>
    /// The scalar this handle measures ACROSS its own axis — travel perpendicular to
    /// <see cref="AxisDeg"/>, positive 90° counter-clockwise from it. The other half of an orthogonal
    /// decomposition, and meaningful only when <see cref="Cross"/> is set.
    /// </summary>
    public double ProjectCross(double px, double py)
    {
        double dx = px - AnchorX, dy = py - AnchorY;
        double rad = AxisDeg * (Math.PI / 180.0);
        return -dx * Math.Sin(rad) + dy * Math.Cos(rad);
    }

    /// <summary>Where this grip currently sits across its own axis.</summary>
    public double ProjectedCrossPosition => ProjectCross(X, Y);

    /// <summary>
    /// This grip seen as its own perpendicular one-degree-of-freedom handle — the same anchor and
    /// position, the axis turned 90°, and the cross parameter promoted to the primary.
    ///
    /// <para>Used by the solver so a two-axis grip needs no second code path anywhere: each axis is
    /// solved by the ordinary machinery, and the only new thing is that a drag does it twice.</para>
    /// </summary>
    /// <para><b><see cref="KeepAnchorFixed"/> is carried across, and that is load-bearing.</b> A
    /// pinned grip's CELL coordinates are the thing that stays still while its ANCHOR moves — the
    /// solver's own R-pch-4b inversion (measure from the REGENERATED anchor) is what makes such a grip
    /// measurable at all. Dropping the flag here made the cross axis of every pinned two-axis grip read
    /// as dead and be silently dropped, while the primary axis kept working.</para>
    public PCellHandle AsCrossHandle() => Cross is null
        ? this
        : new PCellHandle(Cross.Parameter, AnchorX, AnchorY, X, Y, AxisDeg + 90.0, Kind,
                          Cross.Label, Cross.Min, Cross.Max,
                          Cross: null, KeepAnchorFixed: KeepAnchorFixed, Quantity: Cross.Quantity);

    /// <summary>
    /// The scalar this handle measures at an arbitrary point, in the handle's own projection: DBU
    /// along the axis for <see cref="PCellHandleKind.Linear"/>, degrees about the anchor for
    /// <see cref="PCellHandleKind.Angular"/>.
    ///
    /// <para>One implementation, shared by the solver (which projects a regenerated grip position)
    /// and the editor (which projects the cursor). Two copies would be two chances to disagree about
    /// a sign.</para>
    /// </summary>
    public double Project(double px, double py)
    {
        double dx = px - AnchorX, dy = py - AnchorY;
        if (Kind == PCellHandleKind.Angular)
        {
            double deg = Math.Atan2(dy, dx) * (180.0 / Math.PI) - AxisDeg;
            // Normalised to (-180, 180] so a grip either side of the reference reads with the sign
            // the user expects rather than jumping by a full turn.
            while (deg <= -180.0) deg += 360.0;
            while (deg > 180.0) deg -= 360.0;
            return deg;
        }
        double rad = AxisDeg * (Math.PI / 180.0);
        return dx * Math.Cos(rad) + dy * Math.Sin(rad);
    }

    /// <summary>Where this grip currently sits, in its own projection — the value
    /// <see cref="Project"/> returns for the declared position.</summary>
    public double ProjectedPosition => Project(X, Y);
}

/// <summary>R3: shapes and pins, nothing else — a PCell describes one cell's contents in
/// cell-local coordinates (§6 of the design doc: no placement transform, that's the instance's
/// job). <paramref name="Diagnostics"/> (additive, brief-L5-followups-2.md §2.2 finding) carries
/// any human-readable warning text a generator wants surfaced (e.g. R-klp-10's curvature warning) —
/// a PURE generator (R5) has no message sink of its own to post to directly, so this is the ONE
/// channel; every caller that invokes a <see cref="PCellGenerator"/> is responsible for surfacing a
/// non-empty <see cref="Diagnostics"/> through its own <c>IMessageSink</c>. Null/empty = nothing to
/// report — the common case for every other generator, which needed no change.
///
/// <para><paramref name="Handles"/> (additive, pcell-parameter-handles.md) is the OPTIONAL list of
/// draggable parameter grips. <b>Null means "not draggable", which is why the feature costs every
/// existing generator nothing</b> — a trailing defaulted parameter leaves every construction site
/// compiling untouched, and a cell that declares none behaves exactly as it did before this
/// existed. Handles are returned per-generate rather than declared once, because their positions are
/// functions of the parameter values; they are never persisted in <c>.clay</c>, so the generator is
/// the single source and there is no second copy to go stale.</para></summary>
/// <para><paramref name="Preview"/> lets a generator that already knows it is expensive skip
/// straight to deferred drag preview instead of paying one full regeneration for the host to work
/// that out (R-pch-10). Defaulted to <see cref="PCellPreviewMode.Auto"/>, so it costs nothing to
/// ignore.</para></summary>
/// <para><paramref name="ComputedParameters"/> and <paramref name="ComputedValues"/> (additive) are
/// how a generator says "I DERIVE this one, I never read it" — a MIM cap's capacitance, a resistor's
/// resistance. Naming a parameter is what makes circuitRF stop offering an edit box for something
/// typing into cannot change; supplying its value is what makes the parameter list track the
/// artwork, because a derived value is by definition not the one stored with the design. Both null
/// — every generator written before this — means "everything I declare is an input", which is what
/// was assumed unconditionally before.</summary>
/// <para><paramref name="UnreadParameters"/> (additive) is a MEASUREMENT and not a classification:
/// the parameters this run never looked at, and therefore the ones that cannot have shaped the
/// geometry it produced. It changes no editor and locks nothing — "unread" and "not the user's to
/// set" are different things, and a kit's model name, multiplier and initial condition are all
/// unread. It exists so a field whose edits do nothing can say so instead of looking broken.</summary>
public sealed record PCellResult(
    IReadOnlyList<LayoutShape> Shapes,
    IReadOnlyList<PCellPin> Pins,
    IReadOnlyList<string>? Diagnostics = null,
    IReadOnlyList<PCellHandle>? Handles = null,
    PCellPreviewMode Preview = PCellPreviewMode.Auto,
    IReadOnlyList<string>? ComputedParameters = null,
    IReadOnlyDictionary<string, PCellValue>? ComputedValues = null,
    IReadOnlyList<string>? UnreadParameters = null);

/// <summary>
/// What a generator says about ONE of its parameters, beyond its name and its value — everything
/// the parameter editor needs to put the right control on screen instead of the same free-text box
/// for all of them.
///
/// <para><b>Declared by the generator, never guessed by the host.</b> circuitRF has no way to look
/// at a string parameter and know whether it is a model name, a yes/no flag spelled "Yes", or a
/// capacitance the cell derives and never reads. A vendor kit states all three, in its own
/// declaration, and the answer to "can we infer it somehow" is that there is nothing to infer:
/// it was already said, and the job is to carry it.</para>
///
/// <para>Every field past <paramref name="Kind"/> is optional and null means "nothing stated",
/// which is what a generator written before any of this existed says — and it renders exactly as it
/// always did.</para>
/// </summary>
public sealed record PCellParameterInfo(
    string Name,
    PCellValueKind Kind,
    PCellValue? Default = null,
    string? Label = null,
    IReadOnlyList<PCellValue>? Choices = null,
    double? Minimum = null,
    double? Maximum = null,
    bool Computed = false)
{
    /// <summary>What to show beside the field — the generator's own label when it gave one that says
    /// more than the name already does, else the name.</summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Name : Label!;

    /// <summary>
    /// True when this parameter's two choices read as a yes/no pair, so a CHECKBOX says the same
    /// thing as a two-item dropdown in a fraction of the space and with no click to discover it.
    ///
    /// <para>Matched against a small vocabulary rather than assumed from the count: two choices can
    /// perfectly well be "octagon"/"square", and a checkbox for that is a control whose unchecked
    /// state has no name. The pairs recognised are the ones kits actually write — Yes/No, true/false,
    /// on/off, and the t/nil some kits inherit from their scripting language.</para>
    /// </summary>
    public bool IsYesNoPair =>
        Kind != PCellValueKind.Bool && Choices is { Count: 2 } &&
        TruthOf(Choices[0]) is { } a && TruthOf(Choices[1]) is { } b && a != b;

    /// <summary>The choice that means "checked" for a <see cref="IsYesNoPair"/> parameter.</summary>
    public PCellValue? TrueChoice =>
        IsYesNoPair ? (TruthOf(Choices![0]) == true ? Choices[0] : Choices[1]) : (PCellValue?)null;

    /// <summary>The choice that means "unchecked" for a <see cref="IsYesNoPair"/> parameter.</summary>
    public PCellValue? FalseChoice =>
        IsYesNoPair ? (TruthOf(Choices![0]) == true ? Choices[1] : Choices[0]) : (PCellValue?)null;

    /// <summary>Which side of a yes/no pair a choice is, or null when it is neither and the pair is
    /// therefore not one.</summary>
    internal static bool? TruthOf(PCellValue value)
    {
        if (value.Kind == PCellValueKind.Bool) return value.AsBool();
        string t = value.AsText().Trim().ToLowerInvariant();
        if (t is "yes" or "true" or "t" or "on" or "1") return true;
        if (t is "no" or "false" or "f" or "nil" or "off" or "0") return false;
        return null;
    }
}

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
/// R5 (DETERMINISM — restated at contract version 2): the same inputs must always produce the same
/// output. <see cref="PCellGeometryCache"/> keys on (generator id, parameter values, technology),
/// so a generator that answers differently for one key breaks that cache silently — the second
/// caller gets the first one's geometry and nothing says so.
///
/// <para><b>The rule used to read "no file reads", and that was the wrong way to say it.</b> A
/// script host reads its own modules to exist at all, so the literal prohibition is unsatisfiable
/// for the very extension this contract is being widened for. What actually matters is that
/// everything the output depends on is DECLARED: no clock, no ambient or global state, no
/// randomness, no set-iteration order, no address-derived hashing, and no accumulation whose order
/// varies between runs. A generator that reads a file must have that file's content in its cache
/// key — the obligation the generator content hash exists to carry.</para>
///
/// <para>Determinism is stated with force because its failure is silent and cache-poisoning: two
/// users on different machines must get identical geometry, and when they do not, what they see is
/// a design that changed by itself.</para>
/// </summary>
public delegate PCellResult PCellGenerator(
    IReadOnlyDictionary<string, PCellValue> parameters,
    Technology? technology,
    PCellLayerSelection layerSelection);
