namespace CircuitRF.Core.Matching;

/// <summary>One ladder element as flatten writes it: the element, and the two nets it joins.</summary>
/// <param name="Element">The element itself — its own name is the component's instance name.</param>
/// <param name="NetA">The net at the element's first terminal.</param>
/// <param name="NetB">The net at its second terminal; <c>"0"</c> (ground) for a shunt element.</param>
public sealed record FlattenedElement(MatchElement Element, string NetA, string NetB)
{
    /// <summary>
    /// True when this is match.md §22's DC-blocking capacitor rather than a ladder element of its own.
    /// </summary>
    /// <remarks>
    /// <b>It shares its inductor's column in the drawing</b> — it is the lower half of one shunt arm,
    /// not a second arm — so the Ui has to be able to tell without re-deriving it from the name. The
    /// entry immediately before it in <see cref="MatchFlattenPlan.Elements"/> is always the inductor it
    /// blocks, and <see cref="FlattenedElement.NetA"/> is the node between the two.
    /// </remarks>
    public bool IsDcBlock { get; init; }
}

/// <summary>
/// One end's termination as flatten writes it — <b>always disabled</b> (match.md §11.3). The
/// resistance and the absorbed reactance are recorded separately because they are two components.
/// </summary>
/// <param name="End">1 or 2.</param>
/// <param name="R">The termination resistance, ohms. Written as a <c>Term</c>'s <c>Z</c>.</param>
/// <param name="PortNet">The ladder net this end attaches to — the cell's own interface pin.</param>
/// <param name="Absorbed">The reactance the external termination supplies, or null for a resistive end.</param>
/// <param name="AbsorbedNetA">The absorbed element's first net, or null when there is none.</param>
/// <param name="AbsorbedNetB">Its second net, or null.</param>
/// <param name="TermHighNet">
/// The net the <c>Term</c>'s "+" terminal sits on. Equal to <paramref name="PortNet"/> for a
/// parallel end; for a SERIES end it is the node on the far side of the absorbed element, which is
/// what puts the reference resistance behind the reactance exactly as the synthesis assumes.
/// </param>
public sealed record FlattenedTermination(
    int End,
    double R,
    string PortNet,
    MatchElement? Absorbed,
    string? AbsorbedNetA,
    string? AbsorbedNetB,
    string TermHighNet);

/// <summary>
/// What <b>Flatten to Cell</b> writes, as pure topology (match.md §11.1). Framework-free on purpose:
/// the Ui turns this into placed components and wires, and <c>Engine.Tests</c> turns the same object
/// into a netlist, so "the flattened cell is the component" is a property of one shared walk rather
/// than of two implementations that happen to agree.
/// </summary>
/// <remarks>
/// <b>The nets are derived from the STAMPED ladder, not from the whole one.</b> An absorbed element
/// is not in the cell's live netlist — it belongs to the external network, which is the entire
/// premise (match.md §8.2) — so walking the full element list would mint a node for an end series
/// arm that is not there and shift every net after it. Filtering first is what makes
/// <see cref="Port1Net"/> and <see cref="Port2Net"/> the same two nodes <c>MatchModel</c> stamps as
/// <c>Nodes[0]</c> and <c>Nodes[1]</c>.
/// </remarks>
public sealed class MatchFlattenPlan
{
    /// <summary>Ground.</summary>
    public const string GroundNet = "0";

    private MatchFlattenPlan(
        string port1Net, string port2Net,
        IReadOnlyList<FlattenedElement> elements,
        IReadOnlyList<FlattenedTermination> terminations)
    {
        Port1Net = port1Net;
        Port2Net = port2Net;
        Elements = elements;
        Terminations = terminations;
    }

    /// <summary>The Term1-side interface net.</summary>
    public string Port1Net { get; }

    /// <summary>The Term2-side interface net.</summary>
    public string Port2Net { get; }

    /// <summary>The ladder the cell actually contains, left to right — the absorbed elements removed.</summary>
    public IReadOnlyList<FlattenedElement> Elements { get; }

    /// <summary>The two ends' records, end 1 first. Everything they name is written disabled.</summary>
    public IReadOnlyList<FlattenedTermination> Terminations { get; }

    /// <summary>Builds the plan for a rebuilt ladder.</summary>
    public static MatchFlattenPlan Build(MatchNetwork network)
    {
        ArgumentNullException.ThrowIfNull(network);

        // The stamped subset, in ladder order — MatchModel.StampedElements' own rule, read off the
        // flag and never off a name.
        var stamped = new MatchNetwork
        {
            R1 = network.R1,
            R2 = network.R2,
            Elements = [.. network.Elements.Where(e => !e.IsAbsorbed)],
        };

        var nets = stamped.AssignNets();
        var elements = new List<FlattenedElement>(stamped.Elements.Count);
        int minted = 0;
        for (int i = 0; i < stamped.Elements.Count; i++)
        {
            var e = stamped.Elements[i];

            // ── A DC block is TWO instances and one minted node (match.md §22.2) ──
            //
            // The network model cannot spell a series pair to ground — AssignNets derives every net
            // from the flat list and a shunt element is node-to-ground, full stop — so the block
            // travels as a property on the inductor. A CELL has no such constraint: it is real
            // components on real nets, so the branch is written out the way it is built, an L from the
            // node to a minted node and a C from there to ground. The flattened cell's S-parameters
            // therefore equal MatchResponse.At's, which is the gate.
            if (e.IsShunt && e.DcBlock > 0.0)
            {
                string mid = $"b{(++minted).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                elements.Add(new FlattenedElement(e, nets[i].A, mid));
                elements.Add(new FlattenedElement(
                    new MatchElement
                    {
                        Name = MatchDcBlock.BlockName(e.Name),
                        Type = ElementType.C,
                        IsShunt = true,
                        Value = e.DcBlock,
                        ArmIndex = e.ArmIndex,
                    },
                    mid, GroundNet)
                { IsDcBlock = true });
                continue;
            }

            elements.Add(new FlattenedElement(e, nets[i].A, nets[i].B));
        }

        string port1 = "p1";
        string port2 = stamped.Elements.Count == 0 ? port1 : stamped.RightPortNet();

        var terminations = new List<FlattenedTermination>(2)
        {
            End(network, 1, port1, network.R1),
            End(network, 2, port2, network.R2),
        };

        return new MatchFlattenPlan(port1, port2, elements, terminations);
    }

    private static FlattenedTermination End(MatchNetwork network, int end, string portNet, double r)
    {
        var absorbed = network.Elements.FirstOrDefault(e => e.AbsorbedEnd == end);
        if (absorbed is null)
            return new FlattenedTermination(end, r, portNet, null, null, null, portNet);

        // A shunt absorbed element hangs off the interface net, in parallel with the reference
        // resistance; a SERIES one sits between the interface net and it. The orientation is read
        // off the element rather than off Termination.Topology so a design whose topology and
        // ladder ever disagreed would write the LADDER, which is what the response was computed from.
        if (absorbed.IsShunt)
            return new FlattenedTermination(end, r, portNet, absorbed, portNet, GroundNet, portNet);

        string inner = $"t{end}";
        return new FlattenedTermination(end, r, portNet, absorbed, portNet, inner, inner);
    }
}
