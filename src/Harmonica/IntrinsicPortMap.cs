using CircuitRF.Core;
using CircuitRF.Core.Devices.External;

namespace CircuitRF.Harmonica;

/// <summary>
/// Which of the DUT's own ports the §4.5 intrinsic quantities are read at — and, when they cannot be
/// read at all, why.
///
/// <para><b>This exists because §4.5.5 refuses to guess.</b> For a native FET, an SDD or a diode the
/// device IS a two-port written in (Vgs, Vds), so port 0 is the gate and port 1 is the drain by
/// construction and there is nothing to decide. For an external model there is: every node is its own
/// ground-referenced port (<c>ExternalDeviceModel</c>'s own remark), the interesting ones are usually
/// INTERNAL, and nothing in the descriptor says which internal node is the intrinsic drain. So the
/// user names them (<see cref="DutSpec.IntrinsicMapping"/>) and this resolves the names to port
/// indices — or reports that it could not, which is what makes the panels draw EMPTY rather than
/// with a plausible-looking wrong answer.</para>
///
/// <para><b>The load side and the source side fail independently, deliberately.</b> §4.5.1's ratio at
/// the drain needs only the drain port. §4.5.3's <c>J′</c> route needs the GATE PORT'S OWN INCIDENCE
/// VECTOR, and an external model's gate port is gate-to-ground, not gate-to-source — so it is exact
/// only while the source pin actually sits at ground, which is §4.3's automatic grounding and stops
/// being true the moment the package states a source lead. Reporting an approximate source impedance
/// there would be exactly the silent wrong answer this type exists to prevent, so it is refused by
/// name instead.</para>
/// </summary>
/// <param name="GatePort">Port index the intrinsic gate is read at, or −1 when unavailable.</param>
/// <param name="DrainPort">Port index the intrinsic drain is read at, or −1 when unavailable.</param>
/// <param name="SourcePort">
/// Port index whose voltage every intrinsic port voltage is referenced to, or −1 for ground. Always
/// −1 for a two-port device, whose ports are already source-referenced.
/// </param>
/// <param name="LoadUnavailable">Why the load-side intrinsic plane cannot be read, or null.</param>
/// <param name="SourceUnavailable">Why the source-side intrinsic plane cannot be read, or null.</param>
public readonly record struct IntrinsicPortMap(
    int     GatePort,
    int     DrainPort,
    int     SourcePort,
    string? LoadUnavailable,
    string? SourceUnavailable)
{
    /// <summary>The two-port case: gate is port 0, drain is port 1, both already source-referenced.</summary>
    public static readonly IntrinsicPortMap TwoPort = new(0, 1, -1, null, null);

    public bool LoadAvailable   => LoadUnavailable   is null;
    public bool SourceAvailable => SourceUnavailable is null;

    /// <summary>Whichever reason there is to state, load side first — the one a user notices.</summary>
    public string? Reason => LoadUnavailable ?? SourceUnavailable;

    /// <summary>The message an external DUT carrying no mapping is refused with (R-h8-3).</summary>
    public const string NoMappingMessage =
        "This model's intrinsic plane has not been named. circuitRF cannot tell which of its internal " +
        "nodes is the intrinsic drain, so the intrinsic glyphs and the loadline are left empty rather " +
        "than drawn somewhere plausible and wrong. Name them in File ▸ Set DUT…";

    /// <summary>
    /// Resolves the map for one DUT. <paramref name="model"/> is the elaborated device — the only
    /// thing that carries an external model's own declared node labels.
    /// </summary>
    public static IntrinsicPortMap For(DutSpec dut, ComponentModel model, LumpedPackage package)
    {
        if (dut.Kind != DutKind.External) return TwoPort;

        if (model is not ExternalDeviceModel ext)
            return Unavailable(
                "This model does not report its own nodes, so the intrinsic plane cannot be located.");

        if (dut.IntrinsicMapping is not { } map) return Unavailable(NoMappingMessage);

        int g = IndexOfLabel(ext, map.GateNode);
        int d = IndexOfLabel(ext, map.DrainNode);
        int s = IndexOfLabel(ext, map.SourcePin);

        if (g < 0) return Unavailable(MissingNode("gate",   map.GateNode,   ext));
        if (d < 0) return Unavailable(MissingNode("drain",  map.DrainNode,  ext));
        if (s < 0) return Unavailable(MissingNode("source", map.SourcePin, ext));

        // §4.5.3's route reads the gate port's own incidence vector, and an external model's gate port
        // is gate-to-GROUND. That is the intrinsic gate-source port exactly while the source pin sits
        // at ground — §4.3's automatic grounding — and stops being it the moment a source lead lifts
        // the source terminal, which is precisely the case §4.5.3(a) exists for. Refused by name there.
        string? sourceReason = package.Rs != 0 || package.Ls != 0
            ? "The source-side intrinsic impedance is not available for an external model whose " +
              "package states a source lead: §4.5.3's route reads the model's own gate port, which " +
              "is referenced to ground rather than to the lifted source terminal. Remove Rs/Ls, or " +
              "read the load side only."
            : null;

        return new IntrinsicPortMap(g, d, s, null, sourceReason);
    }

    private static IntrinsicPortMap Unavailable(string why) => new(-1, -1, -1, why, why);

    /// <summary>
    /// Port index == node index for an external device: the elaborator lays its nodes out as
    /// <c>[n₀, 0, n₁, 0, …]</c>, so port k IS node k (<c>ExternalDeviceModel</c>'s own remark).
    /// </summary>
    private static int IndexOfLabel(ExternalDeviceModel ext, string label)
    {
        foreach (var n in ext.Descriptor.Nodes)
            if (string.Equals(n.Label, label, StringComparison.Ordinal))
                return n.Index;
        return -1;
    }

    private static string MissingNode(string role, string label, ExternalDeviceModel ext)
        => $"The intrinsic {role} was named '{label}', which this model does not declare. It declares: " +
           string.Join(", ", ext.Descriptor.Nodes
               .OrderBy(n => n.Index)
               .Select(n => string.IsNullOrWhiteSpace(n.Label) ? n.Index.ToString() : n.Label)) +
           ". Re-name it in File ▸ Set DUT…";
}
