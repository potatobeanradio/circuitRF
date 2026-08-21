namespace CircuitRF.Core.Devices.External;

/// <summary>
/// What kind of quantity a node carries. The solver treats every node identically — this exists
/// only for UI labelling, unit display, and default-connection policy. A thermal node is an
/// ordinary node whose voltage happens to be a temperature and whose current happens to be a power;
/// the MNA matrix is unit-agnostic and needs no new node type.
/// </summary>
public enum NodeQuantityKind
{
    Electrical,
    Thermal,
}

/// <summary>How a parameter value should be entered and transported.</summary>
public enum ExternalParamKind
{
    Double,
    Int,
    String,
    /// <summary>A path to a file the provider reads. The editor offers a file picker.</summary>
    FilePath,
}

/// <summary>
/// One parameter a device type declares. Names are opaque to circuitRF — they are rendered in the
/// parameter editor and passed back verbatim, never interpreted.
/// </summary>
/// <param name="Description">The model's own one-line description, or "" when it states none.
/// Additive and defaulted, so every existing construction site is unchanged — a compact model
/// declares hundreds of parameters and is unreadable without it.</param>
public sealed record ExternalParamDescriptor(
    string            Name,
    ExternalParamKind Kind,
    string?           DefaultText = null,
    string            Units       = "",
    string            Description = "");

/// <summary>
/// One node of a device type. <paramref name="External"/> nodes bind to nets the user names in the
/// netlist; internal nodes are allocated by the elaborator and are invisible in the design layer.
///
/// <para><b>SlavedTo</b> is the index of another node whose voltage this node follows, or null for
/// an ordinary free node. A provider reports this for a node that is not an independent unknown —
/// one whose row in the device's own Jacobian is identically zero, so solving for it would make the
/// system singular. circuitRF does not guess: a provider that reports a node as degenerate without
/// naming what it follows is a hard error at elaboration, because the alternative is a silently
/// wrong operating point.</para>
///
/// <para><b>Degenerate</b> is the provider's own measurement that this node has no equation: its row
/// in the device's Jacobian is zero, so the model writes nothing for it while other equations still
/// read it. It is reported SEPARATELY from <b>SlavedTo</b> because the two answer different
/// questions — "is this an independent unknown" and "if not, which node does it follow" — and a
/// provider can measure the first without knowing the second. Degenerate with nothing naming a
/// master is the hard error described above; the value cannot be guessed, and guessing wrong is not
/// a failure to converge but a converged answer that is wrong.</para>
///
/// <para><b>CollapsedToGround</b> is the other degenerate case: a node the provider reports as tied
/// to the ground reference rather than to another node of the same device. It is a separate field
/// and not <c>SlavedTo = 0</c>, because node 0 is an ordinary device node like any other — a device
/// whose first pin happens to be an interesting net would otherwise be read as grounding itself.
/// The two are mutually exclusive; a provider reporting both for one node is a hard error at
/// elaboration.</para>
/// </summary>
public sealed record ExternalNodeDescriptor(
    int              Index,
    bool             External,
    NodeQuantityKind QuantityKind      = NodeQuantityKind.Electrical,
    string           Label             = "",
    int?             SlavedTo          = null,
    bool             CollapsedToGround = false,
    bool             Degenerate        = false);

/// <summary>
/// Everything circuitRF knows about an externally-provided device type. All of it is learned at
/// runtime from the provider — circuitRF hardcodes no parameter name, no pin count, and no node
/// role. <paramref name="TypeId"/> and <paramref name="DisplayName"/> are opaque strings: rendered,
/// never interpreted.
/// </summary>
public sealed record ExternalDeviceDescriptor(
    string                                   TypeId,
    string                                   DisplayName,
    int                                      ExternalPinCount,
    int                                      InternalNodeCount,
    IReadOnlyList<ExternalParamDescriptor>   Parameters,
    IReadOnlyList<ExternalNodeDescriptor>    Nodes,
    bool                                     SupportsNonlinear = true,
    bool                                     SupportsLinear    = false)
{
    /// <summary>Total node count the device occupies in the global matrix.</summary>
    public int NodeCount => ExternalPinCount + InternalNodeCount;

    /// <summary>
    /// Nodes that are not free unknowns, paired with the node each one follows. Empty for a device
    /// whose nodes are all independent.
    /// </summary>
    public IEnumerable<(int Node, int SlavedTo)> SlavedNodes
        => Nodes.Where(n => n.SlavedTo is not null).Select(n => (n.Index, n.SlavedTo!.Value));

    /// <summary>Nodes the provider reports as tied to the ground reference. Empty for most devices.</summary>
    public IEnumerable<int> GroundedNodes
        => Nodes.Where(n => n.CollapsedToGround).Select(n => n.Index);
}
