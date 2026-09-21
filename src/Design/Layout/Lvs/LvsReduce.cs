// The same collapse on both netlists — brief-lvs-6-reduction.md, docs/design/lvs.md §6.3.
//
//   LvsNetlist (layout)   ─┐
//                          ├─► LvsReduce.Apply ─► reduced pair ─► the comparison
//   LvsNetlist (schematic) ─┘        (one function, both sides, identical arguments)
//
// ── WHY THIS IS ON BY DEFAULT, WHICH REVERSES THE DESIGN NOTE'S FIRST DRAFT ────────────────────
//
// The note originally proposed no automatic reduction. The owner's answer was to use what is
// common and expected, and what every production LVS does is reduce by default: a power FET is one
// symbol on a drawing and eight identical fingers in the artwork, a 100 pF bypass is one symbol and
// two 47 pF parts side by side, a bias resistor is one symbol and two in series because the board
// only had two footprints of the right power rating. Compared object for object all three are
// mismatches and all three are correct designs. A designer whose eight-finger FET reports as seven
// extra devices stops running the tool.
//
// ── ONE FUNCTION, BOTH SIDES, AND IT CANNOT TELL THEM APART (R-lvs6-1a) ───────────────────────
//
// LvsNetlist carries no flag saying which document built it and LvsReduceOptions carries nothing
// side-specific — not even the measurement list, which is the TestBench's and is therefore the SAME
// SET OF NAMES on both sides (R-lvs6-3c). Never run on one side "to make it look like" the other:
// that is note R-lvs-2 applied to this pass, and a reducer that CAN tell the two apart will
// eventually treat them differently, which is a bug nobody can see because both inputs look right
// and only the answer is wrong.
//
// The document NAME is not here either, for the same reason — ReductionLog.Notes takes it at the
// point the report is written, so nothing the merge arithmetic can read knows what file it is in.
//
// ── NOTHING IS HIDDEN, IT IS ONLY COUNTED DIFFERENTLY (R-lvs6-5) ──────────────────────────────
//
// Every merged device keeps the Group of paths it stands for, so a finding names R7 rather than
// "the merged group at net 14" (R-lvs6-5b). That is what makes reduction safe to have on by
// default, and it is why LvsDevice.Group is never empty rather than being null for an unmerged
// device: a reader that must ask first is a reader that will forget to.

using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>Which collapse produced a group.</summary>
public enum ReductionKind
{
    /// <summary>Devices side by side on the same nets.</summary>
    Parallel,

    /// <summary>Devices end to end through a node nothing else reaches.</summary>
    Series,

    /// <summary>A part declared a shorting link (R-lvs6-5d) — a net merge, not a device merge.</summary>
    Jumper,
}

/// <summary>
/// One collapse that was found: what it was, what it produced, and <b>every device it stands
/// for</b> (R-lvs6-2b).
/// </summary>
/// <param name="Kind">Which collapse.</param>
/// <param name="Type">The device kind involved, for the counts-by-type summary (R-lvs6-5a).</param>
/// <param name="Path">The surviving device's path — the canonical first member's.</param>
/// <param name="Members">The individual device paths, in canonical order. What a finding
/// un-reduces to (R-lvs6-5b).</param>
/// <param name="Applied">Whether the collapse was actually performed. False only for a jumper the
/// caller did not ask to collapse (R-lvs6-5d): found, reported, and left as a device.</param>
public sealed record ReductionGroup(
    ReductionKind Kind,
    DeviceKind Type,
    string Path,
    IReadOnlyList<string> Members,
    bool Applied = true);

/// <summary>
/// What one call to <see cref="LvsReduce.Apply"/> did — <b>the whole of it, always reported</b>
/// (R-lvs6-5a/c).
/// </summary>
/// <param name="Enabled">Whether reduction ran at all. <c>false</c> is <c>--no-reduce</c>, and it
/// is on the face of every report either way (R-lvs6-5c).</param>
/// <param name="Passes">How many times the fixed point was walked (R-lvs6-1b). A series merge can
/// expose a parallel one and the reverse, so this is 2 or 3 on a real ladder and 1 on a design with
/// nothing to collapse.</param>
/// <param name="DevicesBefore">The device count going in.</param>
/// <param name="DevicesAfter">The device count coming out.</param>
/// <param name="Groups">Every collapse, in the order it was made.</param>
public sealed record ReductionLog(
    bool Enabled,
    int Passes,
    int DevicesBefore,
    int DevicesAfter,
    IReadOnlyList<ReductionGroup> Groups)
{
    /// <summary>The collapses of one kind.</summary>
    public IReadOnlyList<ReductionGroup> Of(ReductionKind kind)
        => [.. Groups.Where(g => g.Kind == kind)];

    /// <summary>
    /// The run summary's lines for <paramref name="document"/> — <b>including the mode line, which
    /// is emitted in both modes</b> (R-lvs6-5c).
    /// </summary>
    /// <remarks>
    /// <b>The document name arrives here and nowhere else.</b> Reduction itself is handed nothing
    /// that names a side or a file (R-lvs6-1a); the report is written afterwards, from both logs,
    /// and it is the caller that puts the two lines beside each other — which is the whole value of
    /// them, because an asymmetry in these counts is often the first clue to what is actually
    /// wrong.
    /// </remarks>
    public IReadOnlyList<Diagnostic> Notes(string document)
    {
        var notes = new List<Diagnostic> { LvsDiagnostics.ReductionMode(document, Enabled) };

        AddCounts(notes, ReductionKind.Parallel, LvsDiagnostics.ReduceParallel);
        AddCounts(notes, ReductionKind.Series,   LvsDiagnostics.ReduceSeries);

        foreach (var jumper in Of(ReductionKind.Jumper))
            notes.Add(LvsDiagnostics.ReduceJumper(document, jumper.Path, jumper.Applied));

        return notes;

        void AddCounts(List<Diagnostic> into, ReductionKind kind,
                       Func<string, int, int, int, string, Diagnostic> make)
        {
            var groups = Of(kind);
            if (groups.Count == 0) return;

            int from = groups.Sum(g => g.Members.Count);
            string byType = string.Join(", ", groups
                .GroupBy(g => g.Type)
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Key} {g.Sum(x => x.Members.Count)} to {g.Count()}"));

            into.Add(make(document, groups.Count, from, groups.Count, byType));
        }
    }
}

/// <summary>
/// What reduction was asked for. <b>Identical on both sides, every time</b> (R-lvs6-1a).
/// </summary>
public sealed record LvsReduceOptions
{
    // Declared BEFORE Default, and it has to be: a static field initializer runs in declaration
    // order, so Default built above this line would be built with a null MeasuredNames.
    private static readonly IReadOnlySet<string> EmptyNames =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Reduction on, nothing measured, jumpers reported and not collapsed.</summary>
    public static readonly LvsReduceOptions Default = new();

    /// <summary><c>--no-reduce</c> (R-lvs6-5c, note R-lvs-40).</summary>
    public static readonly LvsReduceOptions NoReduce = new() { Enabled = false };

    /// <summary>
    /// <c>--no-reduce</c> turns the whole pass off (R-lvs6-5c). <b>All or nothing</b> — there is no
    /// per-collapse switch, because the table is the rule and every knob on it is a way to make a
    /// wrong design pass.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Every name the <c>measure</c> lines mention — <b>the TestBench's own list, given unchanged
    /// to both sides</b> (R-lvs6-3c).
    /// </summary>
    /// <remarks>
    /// <b>This is the clause that will be forgotten.</b> Reducing away a node a measurement
    /// references produces a circuit that still parses, still runs, and silently answers a different
    /// question. Both sides refuse the same collapse because both are handed the same names — which
    /// is also why this is a set of NAMES rather than of net indices, the two sides having no reason
    /// to number their nets alike.
    ///
    /// <para>It is resolved against <see cref="LvsNet.Label"/>, which today carries only the names
    /// something anchored — ground, a user's net label, a cell port, a stamped piece of copper.
    /// That makes this clause overlap R-lvs6-3a's label clause on a flat netlist, and the two are
    /// still written separately, for R-lvs6-3d's reason: each is a distinct way the node could be
    /// reachable after all, each has a different fix, and brief 9's hierarchy stitching carries
    /// names the label clause does not.</para>
    /// </remarks>
    public IReadOnlySet<string> MeasuredNames { get; init; } = EmptyNames;

    /// <summary>
    /// Whether a declared shorting link collapses to a net merge (R-lvs6-5d).
    /// </summary>
    /// <remarks>
    /// <b>Off unless the caller knows BOTH documents have one.</b> A jumper is collapsed on both
    /// sides or on neither: collapsing one side only compares a circuit neither document draws.
    /// Found jumpers are reported at info either way, because a jumper present in one document and
    /// absent from the other is exactly the thing a designer wants told — and this flag being off
    /// is how that gets said instead of silently absorbed.
    /// </remarks>
    public bool CollapseJumpers { get; init; }

    /// <summary>
    /// Every identifier <paramref name="measureExpressions"/> mentions, whole dotted paths and
    /// their segments alike.
    /// </summary>
    /// <remarks>
    /// <b>A token scan, deliberately, and deliberately over-inclusive.</b> Naming one net too many
    /// costs a collapse that would have been safe; naming one too few silently changes what a
    /// measurement measures. Only one of those is recoverable, so this errs the recoverable way and
    /// does not try to parse <c>V(...)</c> apart from <c>I(...)</c>.
    /// </remarks>
    public static IReadOnlySet<string> NamesIn(IEnumerable<string> measureExpressions)
    {
        ArgumentNullException.ThrowIfNull(measureExpressions);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string expression in measureExpressions)
        {
            if (expression is not { Length: > 0 }) continue;
            foreach (Match m in Identifier.Matches(expression))
            {
                names.Add(m.Value);
                foreach (string segment in m.Value.Split('.')) names.Add(segment);
            }
        }
        return names;
    }

    private static readonly Regex Identifier =
        new(@"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*", RegexOptions.Compiled);
}

/// <summary>
/// Collapses parallel and series groups, on either netlist, through one function (R-lvs6-1a).
/// </summary>
public static class LvsReduce
{
    // ── R-lvs6-4e: the exclusion is by DeviceKind, listed once ────────────────────────────────
    //
    // ALLOW-lists, not deny-lists, and that is the whole requirement: a DeviceKind added later is
    // excluded because it is not written here, so somebody has to think about it. A deny-list would
    // make a new kind reducible by default, which is how a kind becomes reducible because nobody
    // thought about it.
    //
    // What is missing from the parallel list is note R-lvs-36's exclusion, exactly: the microstrip
    // family is TransmissionLine; SnP, SDD, Match, the wBond and the composite RLCs are Unknown
    // (DeviceTypes.Of leaves them so deliberately, because they are BOXES whose contents the file
    // names); Port, Term and the tuner family are Fixture. Two series MLINs are one line only if
    // their Z0s agree and a comparison tool must not do that arithmetic on the user's behalf;
    // collapsing an SnP is not even definable.

    /// <summary>
    /// The kinds a PARALLEL group may be made of. A <see cref="DeviceKind.Cell"/> qualifies because
    /// the fingered FET and the paralleled die part are cells in the artwork, and two placements of
    /// the SAME resolved cell folder with every terminal port for port on the same net ARE in
    /// parallel whatever the cell contains.
    /// </summary>
    private static readonly HashSet<DeviceKind> ParallelReducible =
    [
        DeviceKind.Resistor, DeviceKind.Capacitor, DeviceKind.Inductor,
        DeviceKind.Transistor, DeviceKind.Diode, DeviceKind.Cell,
    ];

    /// <summary>
    /// The kinds a SERIES group may be made of — <b>lumped R, C and L, and nothing else</b>
    /// (R-lvs6-4b). Two FETs in series are a cascode, not a bigger FET.
    /// </summary>
    private static readonly HashSet<DeviceKind> SeriesReducible =
        [DeviceKind.Resistor, DeviceKind.Capacitor, DeviceKind.Inductor];

    /// <summary>
    /// Reduces <paramref name="netlist"/> to a fixed point (R-lvs6-1b).
    /// </summary>
    /// <param name="netlist">Either side's netlist. Unchanged; a new one is returned.</param>
    /// <param name="options">What was asked for. <b>The same object on both sides</b> (R-lvs6-1a).</param>
    /// <returns>The reduced netlist and everything that was done to it.</returns>
    /// <exception cref="InvalidOperationException">A pass reported a change without strictly
    /// reducing the device count. There is no iteration cap and none is needed — every collapse
    /// removes a device — so this is the assertion that makes a would-be infinite loop a failure
    /// rather than a hang (R-lvs6-1b).</exception>
    public static (LvsNetlist Reduced, ReductionLog Log) Apply(
        LvsNetlist netlist, LvsReduceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(netlist);
        options ??= LvsReduceOptions.Default;

        int before = netlist.Devices.Count;
        if (!options.Enabled)
            return (netlist, new ReductionLog(false, 0, before, before, []));

        var state = new State(netlist, options);
        var groups = new List<ReductionGroup>();
        int passes = 0;

        // Said once, before anything moves, and NOT inside the loop — a jumper the caller did not
        // ask to collapse changes nothing, so counting it as a change would make a pass that
        // reported without shrinking, which is the very thing the assertion below refuses.
        if (!options.CollapseJumpers) state.NoteJumpers(groups);

        while (true)
        {
            int count = state.Devices.Count;
            int made = groups.Count;

            if (options.CollapseJumpers) state.CollapseJumpers(groups);
            state.MergeParallel(groups);
            state.MergeSeries(groups);

            if (groups.Count == made) break;
            passes++;

            // R-lvs6-1b. Every collapse removes at least one device, so a pass that reported one
            // and did not shrink is a pass that would run again forever.
            if (state.Devices.Count >= count)
                throw new InvalidOperationException(
                    $"LVS reduction made {groups.Count - made} collapse(s) on pass {passes} without "
                    + $"reducing the device count ({count}). That is a non-terminating pass.");
        }

        return (state.Build(), new ReductionLog(true, passes, before, state.Devices.Count, groups));
    }

    // ── The working state ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The netlist while it is being reduced: devices whose terminals name nets by their ORIGINAL
    /// index, plus a net table that remembers which of those have been merged into which
    /// (a jumper) and which have ceased to exist (a series node).
    /// </summary>
    /// <remarks>
    /// <b>Nets are renumbered exactly once, at the end.</b> Renumbering on every collapse would
    /// mean remapping <see cref="LvsNetlist.BoundaryNets"/>, every terminal and the observable set
    /// each time, and the third of those is the one that would be forgotten.
    /// </remarks>
    private sealed class State
    {
        private readonly LvsNetlist _source;
        private readonly LvsReduceOptions _options;
        private readonly int[] _alias;
        private readonly bool[] _eliminated;
        private readonly string?[] _labels;
        private readonly Dictionary<string, int> _order = new(StringComparer.Ordinal);

        public List<LvsDevice> Devices { get; private set; }

        public State(LvsNetlist source, LvsReduceOptions options)
        {
            _source  = source;
            _options = options;
            _alias   = [.. Enumerable.Range(0, source.Nets.Count)];
            _eliminated = new bool[source.Nets.Count];
            _labels  = [.. source.Nets.Select(n => n.Label)];

            Devices = [.. source.Devices];
            for (int i = 0; i < Devices.Count; i++) _order[Devices[i].Path] = i;
        }

        /// <summary>The net <paramref name="net"/> has been merged into, or itself.</summary>
        public int Find(int net)
        {
            while (_alias[net] != net) net = _alias[net] = _alias[_alias[net]];
            return net;
        }

        /// <summary>
        /// Merges two nets into one, <b>the lower index winning</b> — which keeps ground at index 0
        /// where the two readers put it, and keeps the whole thing deterministic.
        /// </summary>
        private void Union(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a == b) return;

            (int keep, int drop) = a < b ? (a, b) : (b, a);
            _alias[drop] = keep;
            _labels[keep] ??= _labels[drop];
            if (_labels[drop] == "0") _labels[keep] = "0";
        }

        // ── R-lvs6-3: the degree-2 test is the whole safety argument ─────────────────────────

        /// <summary>
        /// Whether a node may be reduced across — <b>all five clauses, each written out</b>
        /// (R-lvs6-3a).
        /// </summary>
        /// <remarks>
        /// If nothing else reaches the node, the two parts are electrically indistinguishable from
        /// one. That is the entire justification (R-lvs6-3b), and every clause here is a way the
        /// node could be reachable after all. They overlap on purpose: net <c>"0"</c> is already
        /// excluded by having a label, and excluding it by name as well costs nothing and removes a
        /// class of degenerate fixture (R-lvs6-3d).
        /// </remarks>
        private bool NodeIsCollapsible(int root, int degree, HashSet<int> boundary)
        {
            if (degree != 2) return false;                                  // exactly two terminals
            if (boundary.Contains(root)) return false;                      // a cell boundary net
            if (_labels[root] == "0") return false;                         // R-lvs6-3d
            if (_labels[root] is { } named
                && _options.MeasuredNames.Contains(named)) return false;    // R-lvs6-3c
            if (_labels[root] is not null) return false;                    // it carries a label
            return true;
        }

        // ── R-lvs6-5d: a declared shorting link is a NET merge ───────────────────────────────

        /// <summary>
        /// Finds every jumper, reports each, and collapses them only when asked.
        /// </summary>
        /// <remarks>
        /// <b>What declares a jumper is a zero-ohm value on a two-terminal part</b> — note
        /// R-lvs-37's own spelling. No <see cref="CircuitRF.Design.Schematic.SymbolKind"/> names a
        /// shorting link today, so <c>PartKind</c> has nothing to say one with; when one is added,
        /// it is one more clause here and nowhere else.
        /// </remarks>
        public void NoteJumpers(List<ReductionGroup> groups)
        {
            foreach (var jumper in Jumpers())
                groups.Add(new ReductionGroup(
                    ReductionKind.Jumper, jumper.Type.Kind, jumper.Path, jumper.Group, Applied: false));
        }

        /// <summary>Merges each jumper's two nets and drops the device.</summary>
        public void CollapseJumpers(List<ReductionGroup> groups)
        {
            var found = Jumpers();
            if (found.Count == 0) return;

            var removed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var jumper in found)
            {
                Union(jumper.Terminals[0].NetIndex, jumper.Terminals[1].NetIndex);
                removed.Add(jumper.Path);
                groups.Add(new ReductionGroup(
                    ReductionKind.Jumper, jumper.Type.Kind, jumper.Path, jumper.Group));
            }

            Devices = [.. Devices.Where(d => !removed.Contains(d.Path))];
        }

        private List<LvsDevice> Jumpers()
            => [.. Devices.Where(IsShortingLink).OrderBy(d => d.Path, StringComparer.Ordinal)];

        /// <summary>A two-terminal part declaring zero ohms.</summary>
        private static bool IsShortingLink(LvsDevice device)
            => device.Terminals.Count == 2
               && TryReal(device.Parameters, "R", out double r)
               && r == 0.0;

        // ── R-lvs6-2: parallel ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Merges every set of devices that sit on the same nets, port for port.
        /// </summary>
        /// <remarks>
        /// <b>Port for port, not set-wise</b> (R-lvs6-2a). Two FETs whose drain and source are
        /// swapped relative to each other are not parallel — they are antiparallel, and that is a
        /// different circuit. Comparing terminal SETS would merge them.
        ///
        /// <para>The one relaxation is the note's own two-terminal row: an <c>R</c>, a <c>C</c> and
        /// an <c>L</c> are symmetric parts, so for those three the net PAIR is unordered. It is not
        /// extended to a two-terminal diode, whose reversal is exactly the antiparallel case, nor
        /// to a two-terminal cell, whose symmetry nothing here knows.</para>
        /// </remarks>
        public void MergeParallel(List<ReductionGroup> groups)
        {
            var byKey = new Dictionary<string, List<LvsDevice>>(StringComparer.Ordinal);
            foreach (var device in Canonical(Devices))
            {
                if (!ParallelReducible.Contains(device.Type.Kind)) continue;
                if (device.Terminals.Count < 2) continue;   // R-lvs3-5a's unmatchable device

                string key = ParallelKey(device);
                if (!byKey.TryGetValue(key, out var bucket)) byKey[key] = bucket = [];
                bucket.Add(device);
            }

            var merged = new Dictionary<string, LvsDevice>(StringComparer.Ordinal);
            var consumed = new HashSet<string>(StringComparer.Ordinal);

            foreach (var bucket in byKey.Values.Where(b => b.Count > 1)
                                               .OrderBy(b => _order[b[0].Path]))
            {
                var representative = bucket[0];
                var device = representative with
                {
                    Terminals    = [.. representative.Terminals.Select(t =>
                                      t with { NetIndex = Find(t.NetIndex) })],
                    Group        = [.. bucket.SelectMany(d => d.Group)],
                    Multiplicity = bucket.Sum(d => d.Multiplicity),
                    Parameters   = MergedParameters(bucket, representative.Type.Kind, parallel: true),
                };

                merged[representative.Path] = device;
                foreach (var member in bucket.Skip(1)) consumed.Add(member.Path);
                groups.Add(new ReductionGroup(
                    ReductionKind.Parallel, device.Type.Kind, device.Path, device.Group));
            }

            if (merged.Count == 0) return;
            Devices = [.. Devices.Where(d => !consumed.Contains(d.Path))
                                 .Select(d => merged.GetValueOrDefault(d.Path, d))];
        }

        /// <summary>
        /// What makes two devices the same parallel group: the type, the terminal count and the
        /// nets, port for port.
        /// </summary>
        private string ParallelKey(LvsDevice device)
        {
            bool symmetric = device.Terminals.Count == 2
                             && device.Type.Kind is DeviceKind.Resistor
                                                 or DeviceKind.Capacitor
                                                 or DeviceKind.Inductor;

            var nets = device.Terminals.OrderBy(t => t.Port).Select(t => Find(t.NetIndex));
            if (symmetric) nets = nets.Order();

            return string.Join('|',
                device.Type.Kind, device.Type.CellDir ?? "", device.Terminals.Count,
                string.Join(',', nets));
        }

        // ── R-lvs6-2 / R-lvs6-3: series ──────────────────────────────────────────────────────

        /// <summary>
        /// Merges pairs of lumped parts that meet end to end at a node nothing else reaches.
        /// </summary>
        /// <remarks>
        /// <b>One merge per device per pass.</b> A three-part ladder collapses in two passes rather
        /// than in one, which costs nothing — the fixed point is walked anyway — and keeps the
        /// merge order from depending on which node the walk reached first.
        /// </remarks>
        public void MergeSeries(List<ReductionGroup> groups)
        {
            var boundary = new HashSet<int>(_source.BoundaryNets.Select(Find));
            var pins = new Dictionary<int, List<(LvsDevice Device, int Terminal)>>();

            foreach (var device in Devices)
                for (int t = 0; t < device.Terminals.Count; t++)
                {
                    int root = Find(device.Terminals[t].NetIndex);
                    if (!pins.TryGetValue(root, out var list)) pins[root] = list = [];
                    list.Add((device, t));
                }

            var merged = new Dictionary<string, LvsDevice>(StringComparer.Ordinal);
            var consumed = new HashSet<string>(StringComparer.Ordinal);

            foreach (int root in pins.Keys.Order())
            {
                var here = pins[root];
                if (!NodeIsCollapsible(root, here.Count, boundary)) continue;

                var (first, firstTerminal)   = here[0];
                var (second, secondTerminal) = here[1];

                if (ReferenceEquals(first, second)) continue;                        // a self-loop
                if (consumed.Contains(first.Path) || consumed.Contains(second.Path)) continue;
                if (merged.ContainsKey(first.Path) || merged.ContainsKey(second.Path)) continue;

                if (first.Terminals.Count != 2 || second.Terminals.Count != 2) continue;
                if (!SeriesReducible.Contains(first.Type.Kind)) continue;

                // R-lvs6-4d. An R in series with an L stays two devices: the merged value would
                // have no dimension. The NAME is deliberately not part of this — it is what to call
                // the device in a report and is never matched on.
                if (first.Type.Kind != second.Type.Kind) continue;
                if (!string.Equals(first.Type.CellDir ?? "", second.Type.CellDir ?? "",
                                   StringComparison.Ordinal)) continue;

                var pair = Canonical([first, second]).ToList();
                bool firstIsRepresentative = ReferenceEquals(pair[0], first);
                var (a, aTerminal) = firstIsRepresentative
                    ? (first, firstTerminal) : (second, secondTerminal);
                var (b, bTerminal) = firstIsRepresentative
                    ? (second, secondTerminal) : (first, firstTerminal);

                var aFree = a.Terminals[1 - aTerminal];
                var bFree = b.Terminals[1 - bTerminal];

                var device = a with
                {
                    Terminals = [
                        new LvsTerminal(1, aFree.Name, Find(aFree.NetIndex)),
                        new LvsTerminal(2, bFree.Name, Find(bFree.NetIndex)),
                    ],
                    Group        = [.. a.Group, .. b.Group],
                    // A series merge is not a multiplicity: two four-finger groups end to end are
                    // still four in parallel, so this is the MAX rather than the sum.
                    Multiplicity = Math.Max(a.Multiplicity, b.Multiplicity),
                    Parameters   = MergedParameters([a, b], a.Type.Kind, parallel: false),
                };

                merged[a.Path] = device;
                consumed.Add(b.Path);
                _eliminated[root] = true;
                groups.Add(new ReductionGroup(
                    ReductionKind.Series, device.Type.Kind, device.Path, device.Group));
            }

            if (merged.Count == 0) return;
            Devices = [.. Devices.Where(d => !consumed.Contains(d.Path))
                                 .Select(d => merged.GetValueOrDefault(d.Path, d))];
        }

        // ── R-lvs6-1c: one canonical order, everywhere ───────────────────────────────────────

        /// <summary>
        /// Devices in (path, ordinal) order — <b>what decides which member of a group is the one
        /// that survives</b>, and therefore what makes the same input give the same merged
        /// identities on every run and on every platform.
        /// </summary>
        private IEnumerable<LvsDevice> Canonical(IEnumerable<LvsDevice> devices)
            => devices.OrderBy(d => d.Path, StringComparer.Ordinal).ThenBy(d => _order[d.Path]);

        // ── Coming out ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The reduced netlist, with nets renumbered once.
        /// </summary>
        /// <remarks>
        /// A net survives unless it was merged into another (a jumper) or ceased to exist (a series
        /// node). A net left with no pins is NOT dropped on that ground alone: a boundary net that
        /// reaches no device is an open, which is a topological fact the comparison must still see.
        /// </remarks>
        public LvsNetlist Build()
        {
            var map = new int[_source.Nets.Count];
            Array.Fill(map, -1);

            int next = 0;
            for (int i = 0; i < map.Length; i++)
                if (!_eliminated[i] && Find(i) == i) map[i] = next++;

            int At(int net) => map[Find(net)];

            var pins = new List<(int Device, int Terminal)>[next];
            for (int i = 0; i < next; i++) pins[i] = [];

            var devices = new List<LvsDevice>(Devices.Count);
            for (int d = 0; d < Devices.Count; d++)
            {
                var device = Devices[d];
                var terminals = new List<LvsTerminal>(device.Terminals.Count);
                for (int t = 0; t < device.Terminals.Count; t++)
                {
                    int net = At(device.Terminals[t].NetIndex);
                    terminals.Add(device.Terminals[t] with { NetIndex = net });
                    pins[net].Add((d, t));
                }
                devices.Add(device with { Terminals = terminals });
            }

            var nets = new List<LvsNet>(next);
            for (int i = 0; i < map.Length; i++)
                if (map[i] >= 0) nets.Add(new LvsNet(map[i], _labels[i], pins[map[i]]));

            return new LvsNetlist(devices, nets, [.. _source.BoundaryNets.Select(At)], _source.Notes);
        }
    }

    // ── The value arithmetic (R-lvs6-2's third column) ───────────────────────────────────────

    /// <summary>
    /// The merged device's parameters: the group's value computed as the physics does, and
    /// everything else carried only where the members agree about it.
    /// </summary>
    /// <remarks>
    /// <b>A parameter the members disagree about is dropped, not picked.</b> Carrying the first
    /// member's would state something about the group that is not true of it, and brief 10 would
    /// then compare the schematic against a number one finger claimed. Where the group's own value
    /// cannot be computed — because some member does not claim one — the merged device claims none
    /// either, which is the "compared only where BOTH sides claim a value" rule arriving one step
    /// early.
    /// </remarks>
    private static IReadOnlyDictionary<string, object?> MergedParameters(
        IReadOnlyList<LvsDevice> members, DeviceKind kind, bool parallel)
    {
        string? value = ValueParameter(kind);
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (name, first) in members[0].Parameters)
        {
            if (name == value) continue;
            if (members.All(m => m.Parameters.TryGetValue(name, out var other) && Equals(other, first)))
                merged[name] = first;
        }

        if (value is null) return merged;

        var values = new List<double>(members.Count);
        foreach (var member in members)
        {
            if (!TryReal(member.Parameters, value, out double v)) return merged;
            values.Add(v);
        }

        // C sums in parallel and adds as 1/C in series; R and L do the reverse. A zero in a
        // reciprocal sum is a short across the group, which is the answer rather than a divide.
        bool reciprocal = parallel
            ? kind is DeviceKind.Resistor or DeviceKind.Inductor
            : kind is DeviceKind.Capacitor;

        if (!reciprocal) merged[value] = values.Sum();
        else if (values.Any(v => v == 0.0)) merged[value] = 0.0;
        else
        {
            double inverse = values.Sum(v => 1.0 / v);
            merged[value] = inverse == 0.0 ? double.PositiveInfinity : 1.0 / inverse;
        }

        return merged;
    }

    /// <summary>Which parameter carries the value of a lumped part, or null where there is no
    /// single one to carry.</summary>
    private static string? ValueParameter(DeviceKind kind) => kind switch
    {
        DeviceKind.Resistor  => "R",
        DeviceKind.Capacitor => "C",
        DeviceKind.Inductor  => "L",
        _                    => null,
    };

    /// <summary>A resolved parameter as a real number. The two readers box what the document
    /// stated, so this accepts the numeric types either of them can produce and refuses the
    /// rest.</summary>
    private static bool TryReal(
        IReadOnlyDictionary<string, object?> parameters, string name, out double value)
    {
        value = 0.0;
        if (!parameters.TryGetValue(name, out object? boxed)) return false;

        switch (boxed)
        {
            case double d:  value = d;         return !double.IsNaN(d);
            case float f:   value = f;         return !float.IsNaN(f);
            case int i:     value = i;         return true;
            case long l:    value = l;         return true;
            case decimal m: value = (double)m; return true;
            default:                           return false;
        }
    }
}
