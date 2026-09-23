// The one netlist type — brief-lvs-3-layout-netlist.md R-lvs3-1, docs/design/lvs.md §4.
//
// ── ONE TYPE FOR BOTH SIDES, AND THE COMPARATOR MUST NOT BE ABLE TO TELL THEM APART ────────────
//
// R-lvs3-1a. Brief 3 builds one of these from a `.clay` and brief 4 builds one from a `.csch`, and
// there is no flag anywhere in here saying which. A comparator that CAN tell will eventually treat
// them differently — an anchor honoured on one side and not the other, a tolerance applied one way
// — and the asymmetry is a bug nobody can see, because both inputs look right and the answer is
// merely wrong.
//
// ── PROVENANCE IS FOR THE REPORT, AND FOR NOTHING ELSE ────────────────────────────────────────
//
// R-lvs3-1b. The document, the instance path and the source coordinate are what a finding needs in
// order to put a marker somewhere and name a part. The COMPARISON reads none of it, with one
// stated exception that is not a matching decision at all: brief 7's deterministic tie-break,
// which needs SOME total order on otherwise-indistinguishable candidates and takes this one so the
// same design gives the same answer twice.

using CircuitRF.Design.Cells;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>Where a device came from — <b>for the report</b> (R-lvs3-1b).</summary>
/// <param name="Document">The file it was read from: a <c>.clay</c> here, a <c>.csch</c> on the
/// schematic side.</param>
/// <param name="InstancePath">The placement, as <c>LayoutDesignFlatten.PathOf</c> spells it.</param>
/// <param name="X">Where to put a marker, in the document's own units. DBU for a layout.</param>
/// <param name="Y">DBU.</param>
public sealed record LvsProvenance(string Document, string InstancePath, long X, long Y);

/// <summary>One terminal of one device, and the net it is on.</summary>
/// <param name="Port">1-based, the number both sides already use — <c>SymbolPin.PortIndex</c> on
/// one side and the terminal map's <c>Port</c> on the other.</param>
/// <param name="Name">The terminal's own name, for reports. May be empty.</param>
/// <param name="NetIndex">An index into <see cref="LvsNetlist.Nets"/>. <b>Always a real net</b>: a
/// terminal that landed on no copper gets a net of its own, with one pin and no label, because an
/// open is a topological fact and a sentinel would have to be special-cased by every reader.</param>
public sealed record LvsTerminal(int Port, string Name, int NetIndex);

/// <summary>
/// What a device's own side says ABOUT one of its parameters, beyond the value — <b>everything
/// brief 10 needs and nothing it does not</b> (R-lvs10-8's "no new parameter model").
/// </summary>
/// <remarks>
/// <b>It has exactly the same shape on both sides, and that is the point.</b>
/// <see cref="LvsNetlist"/>'s own rule is that the comparator must not be able to tell the two
/// netlists apart; a schematic simply fills <see cref="Computed"/> and <see cref="Unread"/> false,
/// because a drawing derives nothing from its own geometry.
///
/// <para><b>A name appears here exactly when it appears in <see cref="LvsDevice.Parameters"/>.</b>
/// There is deliberately no "declared but unvalued" entry: a parameter with no value is a
/// parameter the side claims nothing about, and R-lvs10-2b already says what to do about that.</para>
/// </remarks>
/// <param name="Dimension">The physical dimension the side declares — <c>CcellParameter.Dimension</c>
/// on one side and <c>EditableParameter.Dimension</c> on the other, which are the same enum.
/// <see cref="UnitDimension.None"/> is an ordinary answer: a count, a ratio, a model name.</param>
/// <param name="Computed">
/// <c>PCellOrigin.IsComputed</c> — the generator DERIVED this from its own geometry rather than
/// reading it (R-lvs10-4a). Such a parameter cannot disagree with the artwork; it can disagree with
/// what the schematic ASKED for, and that is a different sentence pointing at a different file.
/// </param>
/// <param name="Unread">
/// <c>PCellOrigin.IsUnread</c> — the run that drew this artwork never read the parameter, so
/// nothing about the geometry depends on it (R-lvs10-4c). A difference there is information, not a
/// fault.
/// </param>
public readonly record struct LvsParameterFact(
    UnitDimension Dimension, bool Computed = false, bool Unread = false);

/// <summary>One device.</summary>
/// <param name="Path">
/// <b>The user-facing identity, and it is stable across runs</b> (R-lvs3-1c) — not a GUID and not
/// an index into anything. <c>R1</c>, <c>U3/M1</c>, <c>R1[0,2]</c>: the report's own spelling, so a
/// finding names what the designer called the part.
/// </param>
/// <param name="Designator">May be empty. <b>Not the identity</b> — every element of an array
/// carries the same one (R-lvs3-4b), which is correct and is exactly why <paramref name="Path"/>
/// exists.</param>
/// <param name="Type">What kind of device, canonically. Two devices of different type never match.</param>
/// <param name="Terminals">In port order. <b>Empty is representable and is not a dropped device</b>
/// (R-lvs3-5a): a cell with no derivable terminal map is unmatchable and must still appear in the
/// count, or the two sides disagree about how many parts there are for a reason the report never
/// gave.</param>
/// <param name="Parameters">Resolved SI values, empty where nothing claims one. Brief 10 compares
/// them; this brief only carries what the artwork already states.</param>
/// <param name="Provenance">For the report (R-lvs3-1b).</param>
public sealed record LvsDevice(
    string Path,
    string Designator,
    DeviceType Type,
    IReadOnlyList<LvsTerminal> Terminals,
    IReadOnlyDictionary<string, object?> Parameters,
    LvsProvenance Provenance)
{
    /// <summary>
    /// Every device this one stands for, in canonical order — <b>what a finding un-reduces to</b>
    /// (R-lvs6-2b, R-lvs6-5b).
    /// </summary>
    /// <remarks>
    /// <b>Never empty, and it holds <see cref="Path"/> alone until something merges.</b> A report
    /// that could only say "the merged group at net 14" is one a user cannot act on, so every
    /// finding names the individuals — and it does so by reading this list unconditionally rather
    /// than by asking first whether the device was merged, which is the branch that gets forgotten.
    /// </remarks>
    public IReadOnlyList<string> Group { get; init; } = [Path];

    /// <summary>
    /// What this device says its counterpart on the OTHER side is called — brief 7's tier-0
    /// anchor (R-lvs7-2a), which on the layout side is <c>LayoutInstance.SchematicId</c>.
    /// </summary>
    /// <remarks>
    /// <b>It is a claim, not an identity, and the comparison verifies every one of them</b>
    /// (R-lvs7-2b). Empty is the ordinary case: a hand-drawn board claims nothing and is compared
    /// on its structure.
    ///
    /// <para><b>Read symmetrically.</b> An anchor exists where one side's <c>AnchorId</c> equals
    /// the other side's <see cref="Path"/>, whichever side stated it — the comparator never asks
    /// which document it is holding, for <see cref="LvsNetlist"/>'s own reason. Today only the
    /// layout side fills it, because only the layout side has a field for it; the schematic's own
    /// <see cref="Path"/> IS the thing that field names.</para>
    /// </remarks>
    public string AnchorId { get; init; } = "";

    /// <summary>
    /// What each of <see cref="Parameters"/> IS — its dimension, and whether the artwork derived it
    /// or never read it. Empty where the side says nothing about any of them.
    /// </summary>
    /// <remarks>
    /// A second dictionary rather than a richer value type on <see cref="Parameters"/> because the
    /// VALUES are what brief 6 merges, sums and drops; these are declarations about the parameter
    /// and survive that arithmetic unchanged. Keys are a subset of <see cref="Parameters"/>' —
    /// a fact about a value nothing carries is a fact about nothing.
    /// </remarks>
    public IReadOnlyDictionary<string, LvsParameterFact> ParameterFacts { get; init; } = NoFacts;

    private static readonly IReadOnlyDictionary<string, LvsParameterFact> NoFacts =
        new Dictionary<string, LvsParameterFact>(StringComparer.Ordinal);

    /// <summary>
    /// How many devices are in parallel here — <b>carried, not discarded</b> (R-lvs6-2c). A merge
    /// of four fingers is 4, and brief 10 compares it against the schematic's own <c>Nf</c>/<c>M</c>.
    /// 1 for everything that has not been merged in parallel.
    /// </summary>
    public int Multiplicity { get; init; } = 1;

    /// <summary>
    /// <b>Which of the parts this device stands for are placed end for end</b> — their terminal 1
    /// on the copper their counterpart calls terminal 2 — as <c>TurnedParts.Read</c> read them
    /// (brief LVS 16 R-lvs16-1b). Empty for everything else, which includes every schematic device.
    /// </summary>
    /// <remarks>
    /// <b>An input to the comparison, never a decision of it.</b> The reading is railRF's, called
    /// once in <c>LayoutRead</c>, so the two windows cannot disagree about which parts it turned;
    /// the comparison only says which of them its own correspondence BEARS OUT (a renamed part is
    /// read as turned against the wrong counterpart, and a short can put the other net's names on a
    /// part's copper). <b>A list of paths rather than a flag</b> because reduction merges ten
    /// decoupling capacitors into one device, and the turned one among them is still one placed part
    /// the report has to name. It names no side: a netlist that set it on a schematic device would
    /// be read exactly the same way, which is <see cref="LvsNetlist"/>'s own rule.
    /// </remarks>
    public IReadOnlyList<string> CrossedMembers { get; init; } = [];

    /// <summary>
    /// <b>This part's two terminals are interchangeable</b>, because its counterpart says it is a
    /// resistor, a capacitor or an inductor (brief LVS 16) — where its own <see cref="Type"/> cannot.
    /// </summary>
    /// <remarks>
    /// A placement Update Layout wrote carries a <c>SchematicId</c> and no <c>PartKind</c>, so its own
    /// kind is <c>Cell</c> and says nothing about symmetry. Reduction's parallel key reads this, so
    /// a land pattern standing for a capacitor merges with its neighbours whichever way round it is
    /// placed — as the schematic's capacitors already do — rather than standing alone as an
    /// "antiparallel" part and arriving as an unmatched device.
    /// </remarks>
    public bool Interchangeable { get; init; }
}

/// <summary>One net.</summary>
/// <param name="Index">Its own position in <see cref="LvsNetlist.Nets"/>.</param>
/// <param name="Label">What it is CALLED where anything called it anything — <c>"0"</c> for
/// ground, a stamped name on the copper, a schematic net name. <b>Null is ordinary</b>: most
/// artwork names nothing, and a name is an anchor for the comparison rather than a requirement of
/// it.</param>
/// <param name="Pins">Every terminal on it, as (device index, terminal index) into
/// <see cref="LvsNetlist.Devices"/>.</param>
public sealed record LvsNet(int Index, string? Label, IReadOnlyList<(int Device, int Terminal)> Pins);

/// <summary>
/// A flat netlist of devices, terminals and nets — the thing the comparison compares, from either
/// side (R-lvs3-1a).
/// </summary>
/// <param name="Devices">In the order the document places them.</param>
/// <param name="Nets">Ground first where there is one, so <c>"0"</c> is index 0 whenever it
/// exists — a small determinism that costs nothing and makes a report's net numbers readable.</param>
/// <param name="BoundaryNets">The cell's own ports, IN PORT ORDER — a layout boundary pin on one
/// side and a <c>Port Num=</c> on the other. Empty for a design that is not being read as a cell.</param>
/// <param name="Notes">Everything the extraction had to say. <b>Returned, never posted</b> — this
/// project's own rule.</param>
public sealed record LvsNetlist(
    IReadOnlyList<LvsDevice> Devices,
    IReadOnlyList<LvsNet> Nets,
    IReadOnlyList<int> BoundaryNets,
    IReadOnlyList<Diagnostic> Notes)
{
    /// <summary>Nothing extracted, and <paramref name="notes"/> says why — the over-the-ceiling
    /// answer (R-lvs3-2c), which is deliberately not a confident netlist over geometry the run
    /// never saw.</summary>
    public static LvsNetlist Nothing(IReadOnlyList<Diagnostic> notes) => new([], [], [], notes);
}
