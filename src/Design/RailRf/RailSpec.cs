using CircuitRF.Design.Layout;

namespace CircuitRF.Design.RailRf;

/// <summary>
/// What the reference conductor is taken to BE (railrf.md §2.2's own table, verbatim).
///
/// <para><b>The choice is carried on every result and stamped on every plot, every table and every
/// export</b> — that is brief 5's and brief 12's job, but the reason lives here: the second and third
/// are optimistic and a reader who does not know which was used cannot tell.</para>
/// </summary>
public enum RailReferenceExtent
{
    /// <summary>The actual copper on that layer. Honest; a fragmented reference shows as one.</summary>
    AsImported,

    /// <summary>That layer taken as solid within the board outline. Removes return constrictions the
    /// real board may have — OPTIMISTIC.</summary>
    FilledToOutline,

    /// <summary>The layer taken as unbounded at its own z. Removes edge effects too; an upper bound,
    /// and the only way to compare two different outlines on equal terms.</summary>
    Infinite,
}

/// <summary>
/// The frequency band a rail's Z(f) is answered over (§2.2, "The band"). Base SI: hertz.
/// </summary>
/// <param name="StartHz">The bottom of the band.</param>
/// <param name="StopHz">The top. A transient target derives a top of its own
/// (<see cref="RailTransientSpec.BandTopHz"/>); which one brief 12 sweeps to is its call, and both
/// are on the document so it can say which it used.</param>
/// <param name="Points">How many points before adaptive refinement around resonances adds more.</param>
/// <param name="Logarithmic">Log spacing, which is what a PDN band wants — 10 kHz to 200 MHz is four
/// decades and a linear sweep spends almost every point in the top one.</param>
public readonly record struct RailBand(double StartHz, double StopHz, int Points, bool Logarithmic)
{
    /// <summary>§11.3's own sketch: 10 kHz … 200 MHz.</summary>
    public static RailBand Default => new(1e4, 2e8, 201, true);

    public string? Refusal(string where)
    {
        if (!(StartHz > 0))            return $"{where}'s band starts at {StartHz} Hz. A log-spaced band starts above zero.";
        if (!(StopHz > StartHz))       return $"{where}'s band stops at {StopHz} Hz, at or below its start of {StartHz} Hz.";
        if (Points < 2)                return $"{where}'s band has {Points} point(s). A sweep needs at least two.";
        return null;
    }
}

/// <summary>
/// One rail: its net, its reference, the branches on it, what it is judged against, and what excites
/// it (railrf.md §2.2).
///
/// <para><b>A board has more than one, and one is ANALYSED at a time.</b> Review's boards run a
/// primary cell into a converter or an LDO that makes a second voltage, so a document that could
/// describe one net could not describe one of these boards — that is why
/// <see cref="RailDocument.Rails"/> is a set. There is still no simultaneous multi-rail solve, ever:
/// the rails are solved in the dependency order <see cref="RailOrder"/> computes, one at a
/// time.</para>
/// </summary>
public sealed class RailSpec
{
    /// <summary>
    /// What this rail is CALLED — what the rail selector shows, what <c>--rail</c> names, and what a
    /// refusal from <see cref="RailOrder"/> says. Usually the net name and not required to be: two
    /// rails may not share one.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>The power net, as the board netlist or the <c>.kicad_pcb</c> names it. Null on the
    /// assisted Gerber path before the pour has been picked (§2.2) — geometry alone has no net in
    /// it, and railRF will not guess a net name.</summary>
    public string? NetName { get; set; }

    /// <summary>
    /// The reference return's drawing layer.
    ///
    /// <para><b>Asked for, never inferred</b> (Q-8). There is deliberately no "infer the reference"
    /// path in this model at all: a null one is a document that is not yet ready to solve. Brief 7's
    /// window PROPOSES a layer and says why; brief 10's verb refuses and names the flag. Neither of
    /// them guesses, and the model does not offer a way to.</para></summary>
    public LayerKey? ReferenceLayer { get; set; }

    /// <summary>What the reference conductor is taken to be. <see cref="RailReferenceExtent.AsImported"/>
    /// is the default, and the only one that is not optimistic.</summary>
    public RailReferenceExtent ReferenceExtent { get; set; } = RailReferenceExtent.AsImported;

    /// <summary>Every branch feeding this rail, in declaration order.</summary>
    public List<RailSource> Sources { get; } = [];

    /// <summary>Every branch drawing from it, in declaration order. A row with no current is an
    /// observation port (<see cref="RailLoad.DcCurrentA"/>).</summary>
    public List<RailLoad> Loads { get; } = [];

    /// <summary>What the DC answer is judged against — millivolts. Null where nobody has stated
    /// one, which brief 5 reports rather than substituting a number.</summary>
    public RailTarget? DropBudget { get; set; }

    /// <summary>What the frequency answer is judged against, where it is one number for the whole
    /// rail: a flat Z, or a transient spec the flat Z is DERIVED from. A per-port limit is a mask and
    /// lives on the load row instead (<see cref="RailLoad.Mask"/>).
    ///
    /// <para>This and <see cref="DropBudget"/> are not alternatives — §2.2's own rule.</para></summary>
    public RailTarget? ImpedanceTarget { get; set; }

    /// <summary>The band Z(f) is answered over.</summary>
    public RailBand Band { get; set; } = RailBand.Default;

    /// <summary>What on this board excites this rail (§2.2's last paragraph).</summary>
    public List<RailAggressor> Aggressors { get; } = [];

    /// <summary>
    /// Every part on this rail — the decoupling bank and the bulk (§2.2, "The parts").
    ///
    /// <para><b>Not a second bill of materials.</b> What it carries that a BOM cannot is the
    /// per-instance mounting loop (<see cref="RailPart.MountingInductanceHenries"/>), which is a
    /// property of where the part was PLACED rather than of what was bought — typed in P1, computed
    /// from the via geometry in P2a, and overridable either way. Rows pre-filled from a BOM say so
    /// (<see cref="RailPart.Origin"/>).</para>
    ///
    /// <para>Nothing here models anything: <see cref="RailPartResolver"/> turns these rows plus the
    /// part library into <see cref="RailPartModel"/>s.</para></summary>
    public List<RailPart> Parts { get; } = [];

    /// <summary>
    /// The voltage this rail nominally sits at, in VOLTS — the highest open-circuit voltage any
    /// source on it states, or null where none does.
    ///
    /// <para><b>What Q-12's derating is applied at where no DC answer exists yet</b>, and the same
    /// quantity <see cref="RailDcResult.SourceVoltageV"/> reports after a solve. The DC answer
    /// supersedes it: a part at the far end of a rail sits below the source by exactly the drop the
    /// DC solve computes, and derating reads the bias curve at the voltage the part actually
    /// sees.</para></summary>
    public double? NominalVoltageV =>
        Sources.Select(s => s.OpenCircuitVoltageV)
               .Where(v => v is not null)
               .DefaultIfEmpty(null)
               .Max();

    /// <summary>Every observation port on this rail — every load, since a load with a current is
    /// observed as well as drawn from. The DC report lists an observed port AS observed; this is the
    /// set it lists.</summary>
    public IEnumerable<RailLoad> ObservationPorts => Loads;

    /// <summary>The loads that contribute a current injection to the DC solve — the ones that state
    /// one. Brief 1's own property test: three loads, one currentless, solves with two injections and
    /// reports three ports.</summary>
    public IEnumerable<RailLoad> DcInjections => Loads.Where(l => l.DcCurrentA is not null);

    /// <summary>Null when this rail is well formed, or the first refusal sentence.</summary>
    public string? Refusal()
    {
        string rail = $"Rail '{(Name.Length > 0 ? Name : "(unnamed)")}'";

        if (string.IsNullOrWhiteSpace(Name))
            return "A rail has no name. Every rail is named — the rail selector shows it, --rail " +
                   "names it, and a refusal about the solve order has to say which two rails it is " +
                   "about.";

        for (int i = 0; i < Sources.Count; i++)
            if (Sources[i].Refusal($"{rail}'s source {i + 1} ({Sources[i].Anchor.Describe()})") is { } s)
                return s;

        for (int i = 0; i < Loads.Count; i++)
            if (Loads[i].Refusal($"{rail}'s load {i + 1} ({Loads[i].Anchor.Describe()})") is { } l)
                return l;

        if (DropBudget?.Refusal($"{rail}'s drop budget", RailTargetKind.DropBudget) is { } d) return d;
        if (ImpedanceTarget?.Refusal($"{rail}'s impedance target",
                                     RailTargetKind.FlatImpedance, RailTargetKind.Transient) is { } z) return z;
        if (Band.Refusal(rail) is { } b) return b;

        foreach (var a in Aggressors)
            if (a.Refusal(rail) is { } ag) return ag;

        var seenParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Parts)
        {
            if (p.Refusal(rail) is { } pr) return pr;
            if (!seenParts.Add(p.Refdes))
                return $"{rail} lists part '{p.Refdes}' twice. A refdes is one part on the board, " +
                       "and two rows for it would be two mounting loops for one pad.";
        }

        return null;
    }
}
