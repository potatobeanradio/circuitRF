using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Matching;

namespace CircuitRF.Core.Devices;

/// <summary>
/// A synthesised bandpass matching network placed as one component (<c>docs/design/match.md</c> §8).
/// Two pins, ground the common return; the ladder itself is derived from the design the
/// <c>Design</c> parameter carries.
///
/// <h3>It does NOT contain the absorbed termination reactances</h3>
/// <para>Absorbing them is the entire premise of the feature: they belong to the external network,
/// and the component is the ladder <b>minus</b> them. If a design's end arm is
/// (L = 153.5169 pH, C = 10 pF) with the 10 pF absorbed, this component contains the inductor only.
/// A <c>CFano</c>/<c>LFano</c> surplus element (§4.5) and a <c>CDetune</c>/<c>LDetune</c> element
/// (§4.6) <b>are</b> ours and <b>are</b> stamped — which is why the rule is read off
/// <see cref="MatchElement.IsAbsorbed"/> and never off an element's name.</para>
///
/// <h3>It stamps ELEMENTS on minted internal nodes, not a lumped ABCD block</h3>
/// <para>Three reasons, in order (§8.3):</para>
/// <list type="number">
/// <item><b>DC.</b> A series arm contains a capacitor, so its ABCD entries diverge at omega = 0 — and
///   HB always includes the DC harmonic. Stamping elements inherits <see cref="InductorModel"/>'s
///   already-correct DC-open behaviour instead of re-deriving it.</item>
/// <item><b>HB.</b> An internal node voltage carries its own harmonic content. Eliminating it locally
///   is exact at DC and wrong in HB — the documented reason <see cref="DiodeModel"/>'s internal node
///   is not collapsed.</item>
/// <item><b>Flatten.</b> MN-5 writes the same elements as ordinary components and the two must agree;
///   with an ABCD block that equality would be an accident waiting to break.</item>
/// </list>
///
/// <h3>What one series arm is</h3>
/// <para>A maximal RUN of consecutive through-path elements — normally the arm's (L, C) pair, three
/// when §4.5's surplus element landed in it — is ONE branch-current unknown carrying their series
/// impedance, exactly as <c>InductorModel</c> does for <c>L=</c> plus <c>C=</c>. That is what makes
/// <see cref="InternalNodeCount"/> "one per series arm beyond the first" rather than one per element,
/// and it is why the count is derived from the finished ladder rather than from
/// <see cref="MatchDesign.Order"/>: a surplus or detune element changes the element list, and an
/// absorbed one is not in the stamped ladder at all.</para>
/// </summary>
public sealed class MatchModel : ComponentModel, IReportsWarnings
{
    /// <summary>One through-path run: a branch from <paramref name="FromSlot"/> to <paramref name="ToSlot"/>
    /// carrying Z = jw*L + 1/(jw*C), with 1/C accumulated as <paramref name="InvC"/> so several series
    /// capacitors combine the way series capacitors do.</summary>
    private readonly record struct SeriesArm(int FromSlot, int ToSlot, double L, double InvC, bool HasC);

    /// <summary>One shunt element hanging off node <paramref name="Slot"/>: an inductor branch to
    /// ground, or a capacitive admittance.</summary>
    private readonly record struct ShuntElement(int Slot, double Value, bool IsInductor);

    private readonly SeriesArm[] _series;
    private readonly ShuntElement[] _shunt;
    private readonly bool _throughShort;
    private readonly List<string> _notes;
    private List<(string Key, string Message)>? _pending;

    /// <param name="design">The design this instance carries. Kept for the Designer, the probe and
    /// MN-5's flatten; nothing in the stamp reads it.</param>
    /// <param name="network">The rebuilt ladder (<c>MatchRebuild.Rebuild</c>), absorbed flags intact.</param>
    /// <param name="notes">
    /// Non-fatal things the rebuild found — a dropped transform, a clamped N, a fingerprint mismatch.
    /// Reported through <see cref="IReportsWarnings"/> at the first <see cref="Stamp"/>, phrased with
    /// the instance path, exactly as <see cref="WBondModel"/> does. Empty for a design that rebuilds
    /// cleanly, which is why an ordinary run is unchanged by their existence.
    /// </param>
    public MatchModel(MatchDesign design, MatchNetwork network, IReadOnlyList<string>? notes = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(network);

        Design = design;
        Network = network;
        _notes = notes is null ? [] : [.. notes];

        var stamped = network.Elements.Where(e => !e.IsAbsorbed).ToList();
        StampedElements = stamped;

        foreach (var e in stamped)
        {
            // Zero is the one value that cannot be stamped at all — a zero series capacitor is an
            // infinite impedance and a zero shunt inductor is a dead short across the whole network —
            // and it arrives as silence rather than as an error anywhere upstream. A NEGATIVE value is
            // deliberately allowed through: MatchDesign.AllowNegativeComponents exists to produce one.
            if (e.Value == 0.0 || double.IsNaN(e.Value) || double.IsInfinity(e.Value))
                throw new InvalidOperationException(
                    $"Match: ladder element '{e.Name}' has the unusable value {e.Value}. " +
                    "Re-open the Match Designer and check the transforms applied to this design.");
        }

        int runs = CountSeriesRuns(stamped);
        InternalNodeCount = Math.Max(0, runs - 1);
        _throughShort = runs == 0;

        var series = new List<SeriesArm>(runs);
        var shunt = new List<ShuntElement>(stamped.Count);
        int current = 0;   // slot 0 = port 1
        int runIndex = 0;
        for (int i = 0; i < stamped.Count;)
        {
            if (stamped[i].IsShunt)
            {
                shunt.Add(new ShuntElement(current, stamped[i].Value, stamped[i].Type == ElementType.L));
                i++;
                continue;
            }

            double l = 0.0, invC = 0.0;
            bool hasC = false;
            while (i < stamped.Count && !stamped[i].IsShunt)
            {
                var e = stamped[i++];
                if (e.Type == ElementType.L) l += e.Value;
                else { invC += 1.0 / e.Value; hasC = true; }
            }

            // The LAST run lands on port 2; every earlier one lands on a minted internal node, in
            // ladder order — which is the order the elaborator mints them in.
            int to = runIndex == runs - 1 ? 1 : 2 + runIndex;
            series.Add(new SeriesArm(current, to, l, invC, hasC));
            current = to;
            runIndex++;
        }

        _series = [.. series];
        _shunt = [.. shunt];
    }

    /// <summary>The design this component carries. Authoritative and complete (§7.2).</summary>
    public MatchDesign Design { get; }

    /// <summary>The rebuilt ladder, absorbed elements included — what the Designer and MN-5 read.</summary>
    public MatchNetwork Network { get; }

    /// <summary>The elements this component actually stamps: the ladder minus the absorbed ones.</summary>
    public IReadOnlyList<MatchElement> StampedElements { get; }

    /// <summary>
    /// How many nets beyond the two pins this instance needs — one per series arm past the first.
    /// Read by the elaborator, which mints them keyed on the instance path.
    /// </summary>
    public int InternalNodeCount { get; }

    /// <inheritdoc/>
    public override int PortCount => 2;

    /// <inheritdoc/>
    public override ModelKind Kind => ModelKind.Linear;

    /// <summary>
    /// <c>Nodes[0]</c> = port-1 signal (Term1 side), <c>Nodes[1]</c> = port-2 signal, ground the
    /// common return — the <see cref="TLineModel"/> convention. <c>Nodes[2…]</c> are the minted
    /// internal nodes, in ladder order along the through path.
    /// </summary>
    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        ArgumentNullException.ThrowIfNull(mna);
        ArgumentNullException.ThrowIfNull(c);

        QueueNotes(c);

        int Node(int slot) => slot < c.Nodes.Length ? c.Nodes[slot] : 0;

        // A ladder with no through-path element at all (a single shunt arm) leaves the two pins the
        // same electrical node. Unreachable for orders 2..6, whose arms alternate, but a ladder is a
        // list and the alternative to saying so here is a silently open port 2.
        if (_throughShort)
        {
            int br = mna.AddBranch();
            mna.AddBranchCurrent(br, Node(0), Node(1));
            mna.AddConstraint(br, Node(0), +Complex.One);
            mna.AddConstraint(br, Node(1), -Complex.One);
        }

        foreach (var arm in _series)
        {
            int br = mna.AddBranch();
            mna.AddBranchCurrent(br, Node(arm.FromSlot), Node(arm.ToSlot));

            if (arm.HasC && omega == 0.0)
            {
                // DC with a series capacitor: the branch is an open circuit. Same stamp
                // InductorModel uses for L= plus C= — constraint -i = 0, node voltages unconstrained.
                mna.AddBranchConstraint(br, br, new Complex(-1.0, 0.0));
                continue;
            }

            // V_a - V_b - Z*i = 0, Z = jw*L + 1/(jw*C) = j*(w*L - InvC/w).
            mna.AddConstraint(br, Node(arm.FromSlot), +Complex.One);
            mna.AddConstraint(br, Node(arm.ToSlot), -Complex.One);
            double imag = -omega * arm.L;
            if (arm.HasC && omega != 0.0) imag += arm.InvC / omega;
            var diag = new Complex(0.0, imag);
            if (diag != Complex.Zero) mna.AddBranchConstraint(br, br, diag);
        }

        foreach (var el in _shunt)
        {
            int node = Node(el.Slot);
            if (!el.IsInductor)
            {
                // jwC = 0 at DC — an exact open, exactly as CapacitorModel stamps it.
                mna.AddAdmittance(node, 0, new Complex(0.0, omega * el.Value));
                continue;
            }

            // A bare inductor to ground: a branch carrying jwL, an exact short at DC.
            int br = mna.AddBranch();
            mna.AddBranchCurrent(br, node, 0);
            mna.AddConstraint(br, node, +Complex.One);
            mna.AddConstraint(br, 0, -Complex.One);
            var diag = new Complex(0.0, -omega * el.Value);
            if (diag != Complex.Zero) mna.AddBranchConstraint(br, br, diag);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<(string Key, string Message)> DrainWarnings()
    {
        if (_pending is not { Count: > 0 }) return [];

        var drained = _pending;
        _pending = [];
        return drained;
    }

    private void QueueNotes(ElaboratedComponent c)
    {
        if (_notes.Count == 0 || _pending is not null) return;

        _pending = [];
        for (int i = 0; i < _notes.Count; i++)
            _pending.Add(($"match:{c.InstancePath}:{i}", $"Match '{c.InstancePath}': {_notes[i]}"));
    }

    private static int CountSeriesRuns(IReadOnlyList<MatchElement> elements)
    {
        int runs = 0;
        bool inRun = false;
        foreach (var e in elements)
        {
            if (e.IsShunt) { inRun = false; continue; }
            if (!inRun) { runs++; inRun = true; }
        }
        return runs;
    }
}
