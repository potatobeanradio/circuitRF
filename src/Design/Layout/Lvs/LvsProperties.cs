// Values, tolerances, and where the tolerances came from — brief-lvs-10-properties.md,
// docs/design/lvs.md §6.4.
//
//   the correspondence brief 7 produced ──► one comparison per matched pair ──► Diagnostic[]
//
// ── AFTER TOPOLOGY, NEVER BEFORE (R-lvs10-1) ─────────────────────────────────────────────────
//
// Nothing in here matches anything. It reads `LvsComparison.Devices` — pairs brief 7 already
// concluded — and compares the values of devices that correspond. Comparing the parameters of
// devices that MAY correspond produces noise proportional to the size of the design, and the noise
// buries the topology finding that caused it. A device with no topology match therefore gets no
// property finding at all: one fault, one finding.
//
// It reads the REDUCED devices, because that is what corresponds. Brief 6 already did the value
// arithmetic — a parallel group's R is the group's R and a member's value nobody could compute
// makes the merged device claim none — so there is no summing here either.
//
// ── THE TOLERANCES WERE MEASURED, AND THE DERIVATION IS IN THE CODE (R-lvs10-6) ───────────────
//
// `LvsPropertyTolerances.Derivations` below is not documentation: it carries, per dimension, the
// observed spread of a CORRECT design and the smallest fault anyone would want caught, and
// `tests/Ui.Tests/Lvs/PropertyTests.cs` recomputes the first off `examples/LVS/Bias tee/` on every
// run. A later PCell change that widens the spread past its tolerance fails there, loudly, instead
// of silently making LVS pass a real error.
//
// And where there was no evidence, no number was invented: a dimension with no representative in
// that fixture is compared EXACTLY and says so (R-lvs10-6e). That is honest, and it is what makes
// the gap visible enough to close.
//
// ── WHERE THE LAYOUT'S OWN VALUES COME FROM, AND ONE RECONCILIATION ───────────────────────────
//
// R-lvs10-2a names two sources — a PCell's `PCellOrigin.Parameters` and brief 14's recognition —
// and rules out a land pattern, on the grounds that one land pattern is shared by every 0402 on the
// board so a value stored on it would be wrong for all but one. Brief 5 then built the board
// fixture the other way round on purpose: its parts are `R0402-294R` and `R0402-150R`, one CELL per
// value, because "a land pattern is the only place a board layout can state a value and F6 needs
// one to state" (R-lvs5-2's own departure note).
//
// Both rules are the same rule. What R-lvs10-2a forbids is a value that is not true of every
// placement of the cell carrying it; a cell whose whole identity is "a 294 Ω 0402" states something
// true of all of them. So the layout's claim is the cell's own — its `PCellOrigin.Parameters` where
// a generator drew it, and its `.ccell` declared parameter DEFAULTS where the cell declares any —
// and `LayoutRead` is where both are read. A generic `R0402` declaring no default claims nothing
// and gets R-lvs10-2b's one info line per device type, which is the case R-lvs10-2a was protecting.

using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>
/// How close two values of one <see cref="UnitDimension"/> have to be — <b>and whether anybody ever
/// measured it</b>.
/// </summary>
/// <param name="Dimension">Which dimension this is the answer for.</param>
/// <param name="Relative">
/// A fraction of the larger of the two values. A difference at or below it passes, so this is a
/// slack a correct design is ALLOWED to use.
/// </param>
/// <param name="Absolute">
/// An absolute difference in the dimension's SI base unit, and it is a RESOLUTION rather than a
/// slack: a difference must be strictly SMALLER than it to pass. One DBU on a length is the
/// database's own resolution and a sub-DBU difference cannot exist (R-lvs10-3e) — but a difference
/// of exactly one DBU is a real one, which is why this bound is strict where
/// <paramref name="Relative"/> is not.
/// </param>
/// <param name="Established">
/// False when no measurement stands behind this dimension (R-lvs10-6e). The comparison is then
/// EXACT and the run says so once, rather than a plausible number being invented.
/// </param>
public readonly record struct LvsTolerance(
    UnitDimension Dimension, double? Relative, double? Absolute, bool Established)
{
    /// <summary>Nothing but equality passes.</summary>
    public bool IsExact => Relative is null && Absolute is null;

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> are close enough.</summary>
    public bool Accepts(double a, double b)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b)) return a.Equals(b);
        double diff = Math.Abs(a - b);
        if (diff == 0.0) return true;
        if (Absolute is { } abs && diff < abs) return true;
        if (Relative is { } rel && diff <= rel * Math.Max(Math.Abs(a), Math.Abs(b))) return true;
        return false;
    }

    /// <summary>
    /// What the finding prints (R-lvs10-3c) — <b>always, on every line</b>, so a wrong default is
    /// visible rather than latent.
    /// </summary>
    public string Describe()
    {
        var parts = new List<string>(2);
        if (Relative is { } rel) parts.Add(LvsValueFormat.Percent(rel));
        if (Absolute is { } abs) parts.Add(LvsValueFormat.Of(abs, Dimension));

        if (parts.Count > 0) return string.Join(" or ", parts);
        return Established
            ? "exact"
            : $"exact (no tolerance has been measured for {Dimension})";
    }
}

/// <summary>
/// The shipped tolerance table and one technology's overrides of it — <b>one table, in one
/// place</b> (R-lvs10-3a), keyed on <see cref="UnitDimension"/> and never on a parameter name.
/// </summary>
/// <remarks>
/// <b>The numbers are provisional and this says so</b> (R-lvs10-6d) — <i>"I don't have default
/// property tolerances, you'll have to create a design to test it. We can tweak it later"</i>
/// (owner, 2026-09-21). Tweaking is a one-line change to <see cref="Derivations"/> with a test that
/// says what the change costs, and every report prints the number it applied.
/// </remarks>
public sealed class LvsPropertyTolerances
{
    /// <summary>
    /// Why each measured default is the number it is — <b>the derivation, in the code</b>
    /// (R-lvs10-6c). A tolerance whose derivation is not written down is a magic number by the next
    /// release, and nobody will dare change it because nobody will know what it was protecting.
    /// </summary>
    /// <param name="Dimension">Which dimension.</param>
    /// <param name="Chosen">The shipped relative default.</param>
    /// <param name="ObservedSpread">
    /// <b>The lower bound</b>: the largest gap between what the schematic asked for and what the
    /// artwork of a CORRECT design resolved to, measured off <c>examples/LVS/Bias tee/</c>. A
    /// tolerance tighter than this rejects good artwork.
    /// </param>
    /// <param name="SmallestFault">
    /// <b>The upper bound</b>: the smallest difference anyone would want caught. For a value picked
    /// from a standard component series that is one step of it, so a tolerance looser than this
    /// would accept the neighbouring part.
    /// </param>
    /// <param name="Evidence">Where both bounds came from.</param>
    public sealed record LvsToleranceDerivation(
        UnitDimension Dimension, double Chosen, double ObservedSpread, double SmallestFault,
        string Evidence);

    /// <summary>
    /// One step of the E96 series — <c>10^(1/96) - 1</c>, 2.42 % — <b>the upper bound every
    /// measured default is held below</b>. A value is chosen from a series, and the smallest wrong
    /// part is its neighbour in that series; a tolerance at or above this would accept it.
    /// </summary>
    public static readonly double E96Step = Math.Pow(10.0, 1.0 / 96.0) - 1.0;

    /// <summary>
    /// R-lvs10-6a's procedure, run once and written down.
    /// </summary>
    /// <remarks>
    /// The observed spreads are the quantisation of a correct design: every part in
    /// <c>examples/LVS/Bias tee/</c> is drawn on a 0.25 µm grid, so the value its geometry resolves
    /// to is not exactly the value the schematic asked for. That gap is the floor under any
    /// tolerance. The chosen number — <b>1 %</b> for all three — is a round one comfortably above
    /// the widest of them (0.202 %) and comfortably below one E96 step (2.42 %).
    ///
    /// <para><b>Three dimensions, because the fixture has three.</b> Everything else is exact and
    /// says so (R-lvs10-6e); adding a fourth row means measuring a fourth part, not guessing.</para>
    /// </remarks>
    public static readonly IReadOnlyList<LvsToleranceDerivation> Derivations =
    [
        // The spreads are rounded UP from the measurement, to three figures, so the bound is one a
        // later run cannot fail by a rounding digit while still failing on a real widening.
        new(UnitDimension.Resistance,  0.01, 0.00202,   E96Step,
            "TFR-62R: 62 Ω asked, 61.875 Ω drawn — 0.2016 %."),
        new(UnitDimension.Capacitance, 0.01, 0.00196,   E96Step,
            "MIM-0P8P: 0.8 pF asked, 0.798440 pF drawn — 0.1950 %; MIM-4P0P is 0.0346 %."),
        new(UnitDimension.Inductance,  0.01, 0.000126,  E96Step,
            "SPIRAL-1N2: 1.2 nH asked, 1.199850 nH drawn — 0.01251 %."),
    ];

    private readonly Dictionary<UnitDimension, LvsTolerance> _table;

    private LvsPropertyTolerances(Dictionary<UnitDimension, LvsTolerance> table) => _table = table;

    /// <summary>
    /// The shipped table, with <paramref name="tech"/>'s own rows replacing the ones they name
    /// (R-lvs10-3b) — a PCB 1 % part and an MMIC thin-film resistor are not held to the same number.
    /// </summary>
    /// <param name="tech">The resolved technology, or null.</param>
    /// <param name="dbuPerMicron">
    /// The layout's database resolution, which is what a GEOMETRIC tolerance is (R-lvs10-3e). Zero
    /// or less means no layout stated one, and a length is then compared exactly.
    /// </param>
    public static LvsPropertyTolerances For(Technology? tech, long dbuPerMicron)
    {
        var table = new Dictionary<UnitDimension, LvsTolerance>();

        foreach (var d in Derivations)
            table[d.Dimension] = new LvsTolerance(d.Dimension, d.Chosen, null, Established: true);

        // R-lvs10-3e. Absolute in DBU, not relative: a metre is not an extensive quantity whose
        // error scales with it, and a sub-DBU difference cannot exist because the database cannot
        // represent one. Established by that argument rather than by a measurement — it is the
        // artwork's own resolution, not a slack anybody chose.
        table[UnitDimension.Length] = dbuPerMicron > 0
            ? new LvsTolerance(UnitDimension.Length, null, 1e-6 / dbuPerMicron, Established: true)
            : new LvsTolerance(UnitDimension.Length, null, null, Established: true);

        // R-lvs10-3d. There is no tolerance on a count, a ratio or a model name, and a near-miss
        // there is a different model — exact BY RULE, so it is established and says nothing.
        table[UnitDimension.None] = new LvsTolerance(UnitDimension.None, null, null, Established: true);

        foreach (var rule in tech?.LvsTolerances ?? [])
            table[rule.Dimension] = new LvsTolerance(
                rule.Dimension, Positive(rule.Relative), Positive(rule.Absolute), Established: true);

        return new LvsPropertyTolerances(table);

        static double? Positive(double? v) => v is { } x && double.IsFinite(x) && x > 0 ? x : null;
    }

    /// <summary>What applies to <paramref name="dimension"/> — never null, and exact where nothing
    /// was measured.</summary>
    public LvsTolerance Of(UnitDimension dimension)
        => _table.TryGetValue(dimension, out var t)
            ? t
            : new LvsTolerance(dimension, null, null, Established: false);
}

/// <summary>The property pass — R-lvs10-1's "after topology, never before".</summary>
internal static class LvsProperties
{
    /// <summary>
    /// The parameter names that mean "how many of these are in parallel" — R-lvs10-5's own
    /// vocabulary, and the reason they are excluded from the ordinary value loop: multiplicity has
    /// its own finding and comparing it twice would be one fault with two lines.
    /// </summary>
    private static readonly string[] MultiplicityNames = ["Nf", "M"];

    /// <summary>
    /// Every property divergence between corresponding devices.
    /// </summary>
    /// <param name="schematic">The schematic netlist as compared — reduced.</param>
    /// <param name="layout">The layout netlist as compared — reduced.</param>
    /// <param name="comparison">What brief 7 concluded. <b>Only its pairs are read</b>.</param>
    /// <param name="tolerances">The table, already resolved against the technology and the layout's
    /// own database resolution.</param>
    public static IReadOnlyList<Diagnostic> Compare(
        LvsNetlist schematic, LvsNetlist layout, LvsComparison comparison,
        LvsPropertyTolerances tolerances)
    {
        var found = new List<Diagnostic>();

        // R-lvs10-2b's one line per device TYPE: what the artwork calls the silent cell, how many
        // devices of it there were, and what the schematic had wanted compared.
        var silent = new Dictionary<string, (int Devices, SortedSet<string> Wanted)>(StringComparer.Ordinal);

        // R-lvs10-6e's one line per dimension, and only for a dimension something actually
        // compared — a gap nothing ran into is not a gap worth a line.
        var unestablished = new Dictionary<UnitDimension, int>();

        foreach (var pair in comparison.Devices.OrderBy(p => p.Schematic))
        {
            if (pair.Kind != LvsObjectKind.Device) continue;
            if (pair.Schematic < 0 || pair.Schematic >= schematic.Devices.Count) continue;
            if (pair.Layout    < 0 || pair.Layout    >= layout.Devices.Count) continue;

            // R-lvs10-1a, on the one pairing it does not name. `LvsPairedBy.Symmetry` is a
            // TIE-BREAK between interchangeable candidates and is arbitrary by its own definition
            // (R-lvs7-4c): a finding naming this object may mean any other in its group. Judging
            // values on it would report a coin toss — and worse, it would report it on exactly the
            // designs where the values could have told the two apart, because R-lvs7-3a deliberately
            // keeps them out of the matching. The run already says `lvs.match.by-symmetry` with a
            // count, so the silence is stated rather than assumed.
            if (pair.By == LvsPairedBy.Symmetry) continue;

            var s = schematic.Devices[pair.Schematic];
            var l = layout.Devices[pair.Layout];

            Multiplicity(s, l, found);

            var wanted = s.Parameters.Keys
                .Where(n => !IsMultiplicity(n))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            if (wanted.Count == 0) continue;

            // R-lvs10-2b. The artwork states nothing at all about this part — a land pattern shared
            // by every 0402 on the board. Not a finding, and said once per type however many there
            // are, because four hundred lines saying the obvious is a report nobody reads.
            if (l.Parameters.Count == 0)
            {
                string type = l.Type.Name is { Length: > 0 } n ? n : l.Type.Kind.ToString();
                if (!silent.TryGetValue(type, out var tally))
                    tally = (0, new SortedSet<string>(StringComparer.Ordinal));
                foreach (string name in wanted) tally.Wanted.Add(name);
                silent[type] = (tally.Devices + 1, tally.Wanted);
                continue;
            }

            foreach (string name in wanted)
            {
                object? asked = s.Parameters[name];

                // R-lvs10-2d. The artwork DOES state values and this one is not among them, which
                // is not the same thing as stating none: the two are not the same generator.
                if (!TryValue(l, name, out object? drawn, out _))
                {
                    found.Add(LvsDiagnostics.PropertyMissing(
                        s.Path, l.Path, name, LvsValueFormat.Of(asked, Dimension(s, l, name)),
                        string.Join(", ", l.Parameters.Keys.OrderBy(k => k, StringComparer.Ordinal))));
                    continue;
                }

                var dimension = Dimension(s, l, name);
                var tolerance = tolerances.Of(dimension);
                if (!tolerance.Established)
                    unestablished[dimension] = unestablished.GetValueOrDefault(dimension) + 1;

                if (Agrees(asked, drawn, tolerance)) continue;

                string sText = LvsValueFormat.Of(asked, dimension);
                string lText = LvsValueFormat.Of(drawn, dimension);
                string tText = tolerance.Describe();
                var fact = Fact(l, name);

                // R-lvs10-4. Which SENTENCE, on the layout's own account of the parameter: a value
                // the generator derived from what it drew cannot disagree with the artwork, and one
                // it never read means the artwork is not wrong at all.
                found.Add(
                    fact.Computed ? LvsDiagnostics.PropertyDerivedDiffers(s.Path, l.Path, name, sText, lText, tText)
                  : fact.Unread   ? LvsDiagnostics.PropertyUnreadDiffers(s.Path, l.Path, name, sText, lText, tText)
                  :                 LvsDiagnostics.PropertyMismatch(s.Path, l.Path, name, sText, lText, tText));
            }
        }

        foreach (var (type, tally) in silent.OrderBy(e => e.Key, StringComparer.Ordinal))
            found.Add(LvsDiagnostics.PropertyLayoutSilent(
                type, tally.Devices, string.Join(", ", tally.Wanted)));

        foreach (var (dimension, count) in unestablished.OrderBy(e => e.Key))
            found.Add(LvsDiagnostics.ToleranceUnestablished(dimension.ToString(), count));

        return found;
    }

    // ── Multiplicity (R-lvs10-5) ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Four fingers in the artwork against a schematic that says three — <b>a property finding, not
    /// a topology one</b> (R-lvs10-5a). The merge is what made the comparison possible; judging it
    /// is here.
    /// </summary>
    private static void Multiplicity(LvsDevice s, LvsDevice l, List<Diagnostic> found)
    {
        if (Declared(s) is var (name, declared))
        {
            // R-lvs10-5c. An integer, and therefore exact.
            if (declared != l.Multiplicity)
                found.Add(LvsDiagnostics.PropertyMultiplicity(s.Path, l.Path, name, declared, l.Multiplicity));
            return;
        }

        // R-lvs10-5b. The schematic declares no multiplicity parameter AT ALL and the artwork has
        // several in parallel where the drawing has one. That is a real and common
        // under-specification, and the finding names every one of them.
        if (l.Multiplicity != s.Multiplicity)
            found.Add(LvsDiagnostics.MultiplicityUnstated(
                s.Path, l.Path, l.Multiplicity, string.Join(", ", l.Group)));
    }

    /// <summary>The schematic's own multiplicity parameter and its integer value, or null where it
    /// declares none.</summary>
    private static (string Name, int Value)? Declared(LvsDevice device)
    {
        foreach (string name in MultiplicityNames)
        {
            if (!TryValue(device, name, out object? raw, out string? actual)) continue;
            if (Number(raw) is not { } value || !double.IsFinite(value)) continue;
            if (Math.Abs(value - Math.Round(value)) > 1e-9) continue;
            return (actual!, (int)Math.Round(value));
        }
        return null;
    }

    private static bool IsMultiplicity(string name)
        => MultiplicityNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    // ── Reading one side's answer about one parameter ────────────────────────────────────────

    /// <summary>
    /// A device's value for <paramref name="name"/> as a number, and the name it was actually
    /// stored under — null when the side claims nothing about it.
    /// </summary>
    /// <remarks>
    /// <b>Ordinal first, then case-insensitively.</b> One kit spells a parameter <c>W</c> and
    /// another <c>w</c>; treating those as different parameters would report a generator mismatch
    /// on a design where nothing is wrong.
    /// </remarks>
    private static bool TryValue(LvsDevice device, string name, out object? value, out string? actual)
    {
        if (device.Parameters.TryGetValue(name, out value)) { actual = name; return true; }

        actual = device.Parameters.Keys.FirstOrDefault(
            k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (actual is null) { value = null; return false; }

        value = device.Parameters[actual];
        return true;
    }

    /// <summary>
    /// The dimension to compare in: what the SCHEMATIC declares, else what the layout does.
    /// </summary>
    /// <remarks>
    /// <b>The schematic first because it is the side that always has one.</b> A drawing's parameter
    /// carries <c>EditableParameter.Dimension</c> from the component that declared it; a PCell's
    /// resolved snapshot carries values and nothing about what they mean, so on the artwork side a
    /// dimension exists only where the cell's <c>.ccell</c> declared the parameter.
    /// </remarks>
    private static UnitDimension Dimension(LvsDevice s, LvsDevice l, string name)
    {
        var declared = Fact(s, name).Dimension;
        return declared != UnitDimension.None ? declared : Fact(l, name).Dimension;
    }

    private static LvsParameterFact Fact(LvsDevice device, string name)
    {
        if (device.ParameterFacts.TryGetValue(name, out var fact)) return fact;
        string? key = device.ParameterFacts.Keys.FirstOrDefault(
            k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        return key is null ? default : device.ParameterFacts[key];
    }

    // ── Do two values agree (R-lvs10-3d/3e) ──────────────────────────────────────────────────

    /// <summary>
    /// <b>Exact for everything that is not a measured quantity.</b> Integers, booleans, enumerations
    /// and model names carry no tolerance — a model name differing by one character is a different
    /// model, not a near miss.
    /// </summary>
    private static bool Agrees(object? asked, object? drawn, LvsTolerance tolerance)
    {
        if (asked is null || drawn is null) return asked is null && drawn is null;

        // A model name is a string on both sides or the two sides mean different things by the
        // parameter, and either way only equality can be meant.
        if (asked is string || drawn is string)
            return asked is string a && drawn is string b && a.Equals(b, StringComparison.Ordinal);

        if (asked is bool || drawn is bool)
            return Number(asked) is { } x && Number(drawn) is { } y && x != 0.0 == (y != 0.0);

        if (Number(asked) is not { } left || Number(drawn) is not { } right) return false;

        // R-lvs10-3d/5c. A count is exact, and a side that stored it as an integer is the side that
        // says so — the other may perfectly well carry the same count as a resolved real.
        if (IsIntegral(asked) || IsIntegral(drawn)) return left.Equals(right);

        return tolerance.Accepts(left, right);
    }

    private static bool IsIntegral(object? value) => value is int or long or short or byte;

    /// <summary>A stored parameter as a number, or null where it is not one.</summary>
    private static double? Number(object? value) => value switch
    {
        double d  => d,
        float f   => f,
        int i     => i,
        long l    => l,
        short s   => s,
        byte b    => b,
        decimal m => (double)m,
        bool flag => flag ? 1.0 : 0.0,
        // A complex parameter with no imaginary part IS a real one; with one, only equality can be
        // meant, and comparing magnitudes would call j·1 and 1 the same value.
        Complex c => c.Imaginary == 0.0 ? c.Real : double.NaN,
        _         => null,
    };
}

/// <summary>
/// One spelling for a value on a property line — <b>the number the designer typed, with the unit
/// they typed it in</b>.
/// </summary>
/// <remarks>
/// <b>It knows no multiplier of its own.</b> The ladders come from
/// <c>ComponentTypeRegistry.UnitOptions</c> and the scale factors from
/// <c>CircuitRF.Core.Expressions.Units</c> — the same two tables every parameter row in the
/// application already reads, which is why "0.8 pF" here means what it means there.
/// </remarks>
internal static class LvsValueFormat
{
    /// <summary>An SI-base value in the unit its dimension would show it in.</summary>
    public static string Of(object? value, UnitDimension dimension) => value switch
    {
        null      => "(none)",
        string s  => $"'{s}'",
        bool b    => b ? "true" : "false",
        int or long or short or byte
                  => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        double d  => Of(d, dimension),
        float f   => Of((double)f, dimension),
        Complex c => c.Imaginary == 0.0
                       ? Of(c.Real, dimension)
                       : $"{Significant(c.Real)}{(c.Imaginary < 0 ? "-" : "+")}j{Significant(Math.Abs(c.Imaginary))}",
        _         => value.ToString() ?? "",
    };

    /// <summary>An SI-base number in the unit its dimension would show it in.</summary>
    public static string Of(double value, UnitDimension dimension)
    {
        if (!double.IsFinite(value)) return value.ToString(CultureInfo.InvariantCulture);

        string? unit = Ladder(dimension, value);
        if (unit is null) return Significant(value);

        double scaled = value / (ScaleOf(unit) ?? 1.0);
        return $"{Significant(scaled)} {unit}";
    }

    /// <summary>A fraction as the percentage a tolerance is written as — <c>0.01</c> is "1 %".</summary>
    public static string Percent(double fraction) => $"{Significant(fraction * 100.0)} %";

    /// <summary>
    /// The largest unit of the dimension's own ladder that still leaves the value at or above 1,
    /// or null where the dimension has no ladder — a count, a ratio.
    /// </summary>
    private static string? Ladder(UnitDimension dimension, double value)
    {
        if (dimension == UnitDimension.None) return null;

        string? chosen = null;
        double magnitude = Math.Abs(value);
        foreach (string candidate in ComponentTypeRegistry.UnitOptions(dimension))
        {
            if (ScaleOf(candidate) is not { } scale) continue;   // "None", and "dBm" on the power ladder
            chosen ??= candidate;
            if (magnitude == 0.0 || magnitude / scale < 1.0) break;
            chosen = candidate;
        }
        return chosen;
    }

    /// <summary>
    /// A display unit's linear scale. <c>V</c>, <c>A</c> and <c>W</c> are the base symbols of their
    /// dimensions and <see cref="Units"/> deliberately keeps them out of its scale table (the
    /// spelling collides with a microstrip WIDTH), so they answer 1 here rather than being skipped
    /// and leaving the ladder with a hole where its base unit should be.
    /// </summary>
    private static double? ScaleOf(string displayUnit)
    {
        string engine = UnitNormalizer.ToEngineUnit(displayUnit);
        if (engine.Length == 0) return null;
        return Units.Scale(engine) ?? (engine is "V" or "A" or "W" ? 1.0 : null);
    }

    /// <summary>Six significant digits with the trailing zeros taken off — enough to see a
    /// quantisation gap and not so much that a line becomes unreadable.</summary>
    private static string Significant(double value)
        => value.ToString("G6", CultureInfo.InvariantCulture);
}
