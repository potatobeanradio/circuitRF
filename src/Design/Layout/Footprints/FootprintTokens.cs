// A BOM's footprint column, matched against the case table — brief-footprint-4 R-fp4-3.
//
// `BomFile` has recognised `footprint`, `package`, `pattern`, `land pattern`, `fp`, `decal`, `pkg`,
// `case` and `footprint name` since brief-railrf-2, and `PartLibraryRow.Footprint` has existed
// beside it. Neither fed anything. This file is what they feed — and it feeds a REPORT, never an
// assignment (R-fp4-3d): a BOM column that silently set artwork would be the same class of error as
// the ambiguity below.
//
// THE NORMALISATION IS SMALL AND EXPLICIT (R-fp4-3a), and that is the requirement rather than an
// implementation note. A real bill of materials writes `SM/C_0402`, `C0402`, `CAP-0402-X7R` and
// `0402` for one part, so the reduction has to be stated as a rule with a table of examples as its
// test — not as a similarity score. A token that matches nothing is REPORTED with the token shown
// and is never turned into the nearest code (R-fp4-3b): the whole point of the column is that it
// tells you something, and a fuzzy match tells you what the matcher believed.
//
// AND THE COLLISION IS REAL AND SILENT (R-fp4-3c / overview §1e). `0201` imperial is 0.60 x 0.30 mm;
// `0201` METRIC is 0.25 x 0.125 mm, which is imperial `008004` — a factor of 2.4 either way, with
// nothing to notice: it places, it renders, it exports, and the first sign of trouble is a board.
// So a BARE four-digit token in the colliding set is reported as AMBIGUOUS with both readings
// named. Same shape as `convert`'s Excellon suppression refusal, for the same reason: leading and
// trailing suppression differ by four orders of magnitude on identical text, so it prints the
// inference and the thing that would answer it rather than picking.

namespace CircuitRF.Design.Layout.Footprints;

/// <summary>What a footprint token from a bill of materials or a part library turned out to be.</summary>
public enum FootprintTokenOutcome
{
    /// <summary>One case size, unambiguously.</summary>
    Matched,

    /// <summary>A bare numeric token that names a real case in BOTH the imperial and the metric
    /// scheme. Reported as ambiguous with both readings; never resolved by picking one.</summary>
    Ambiguous,

    /// <summary>Nothing in the case table. Shown as written, never approximated.</summary>
    Unmatched,
}

/// <param name="Token">The token exactly as the file wrote it — what the report shows.</param>
/// <param name="Reduced">What the normalisation reduced it to, or the token itself where nothing
/// reduced. Reported because the reduction is the step a reader has to be able to check.</param>
/// <param name="Reference">The built-in reference this resolves to (<c>smt:0402@N</c>), or null.
/// <b>Offered, never applied</b> — R-fp4-3d.</param>
public sealed record FootprintTokenMatch(
    string Token,
    string Reduced,
    FootprintTokenOutcome Outcome,
    SmtCase? Case,
    DensityLevel Density,
    string? Reference,
    string Report);

/// <summary>
/// The one normalisation from a BOM/part-library footprint spelling to a case size.
/// </summary>
public static class FootprintTokens
{
    /// <summary>
    /// The eight bare tokens that name a real case in both schemes, each with the IMPERIAL code its
    /// METRIC reading means.
    /// </summary>
    /// <remarks>
    /// <b>Data, and deliberately not derived.</b> Three of the eight (<c>0201</c>, <c>0402</c>,
    /// <c>0603</c>) are themselves imperial codes in <see cref="SmtCaseTable"/> and could be found
    /// by intersecting the codes with the twins; the other five are not — imperial <c>1005</c>,
    /// <c>1608</c>, <c>2012</c>, <c>3216</c> and <c>3225</c> are real EIA chip codes that circuitRF
    /// does not generate a pattern for, and a derivation over our own table would therefore call
    /// them unambiguous and read them as metric with nothing said. Naming the eight is the honest
    /// form; the reading each one has IS looked up in the table, so the millimetres in a report
    /// cannot drift from the case data.
    /// </remarks>
    private static readonly (string Token, string MetricMeansImperial)[] _colliding =
    [
        ("0201", "008004"),
        ("0402", "01005"),
        ("0603", "0201"),
        ("1005", "0402"),
        ("1608", "0603"),
        ("2012", "0805"),
        ("3216", "1206"),
        ("3225", "1210"),
    ];

    /// <summary>Every token that is ambiguous when written bare — what a test asserts the set of,
    /// and what a caller can show a user.</summary>
    public static IReadOnlyList<string> AmbiguousTokens { get; } = [.. _colliding.Select(c => c.Token)];

    private static readonly char[] _separators = ['/', '\\', '_', '-', ' ', '.', ':', ',', '|'];

    /// <summary>
    /// Matches one footprint token against the case table.
    /// </summary>
    /// <remarks>
    /// <b>The rule, in order.</b>
    /// <list type="number">
    ///   <item>A canonical <c>smt:&lt;case&gt;[@&lt;density&gt;]</c> reference, parsed by
    ///   <see cref="FootprintRef"/> itself. It is the spelling this file's own ambiguity report asks
    ///   for, so it has to be one this file can read back.</item>
    ///   <item>The WHOLE trimmed token, against the table. This comes first because a tantalum code
    ///   contains the separator the next step splits on — <c>7343-31</c> would otherwise be split
    ///   into two halves that name nothing.</item>
    ///   <item>Each separator-delimited segment, in order, against the table.</item>
    ///   <item>Each segment with a leading letter run stripped (<c>C0402</c> -&gt; <c>0402</c>), then
    ///   with a trailing IPC density letter stripped (<c>0402M</c> -&gt; <c>0402</c> at density M —
    ///   brief 1's own <c>@M</c>/<c>@N</c>/<c>@L</c> vocabulary, not a scheme marker).</item>
    ///   <item>Whatever matched first wins, and NOTHING ELSE is tried. There is no scoring and no
    ///   nearest code.</item>
    /// </list>
    ///
    /// <para><b>Bareness is what decides ambiguity</b> (R-fp4-3c's own words: "a bare numeric
    /// token"). <c>0402</c> alone states no scheme and is ambiguous. <c>C0402</c>, <c>SM/C_0402</c>
    /// and <c>CAP-0402-X7R</c> are decal names in the imperial convention those names are written
    /// in, and — crucially — the report prints the reading in full, with the metric twin and the
    /// millimetres beside it, so a wrong convention is visible rather than silent.</para>
    /// </remarks>
    public static FootprintTokenMatch Match(string? token)
    {
        string raw = (token ?? "").Trim();
        if (raw.Length == 0)
            return new FootprintTokenMatch("", "", FootprintTokenOutcome.Unmatched, null,
                DensityLevel.Nominal, null, "The bill of materials states no footprint for this part.");

        string s = raw.ToUpperInvariant();

        // 0a — A CANONICAL BUILT-IN REFERENCE READS AS ITSELF, density and all.
        //
        // `smt:` is `FootprintRef`'s spelling and it arrives here from three real places: the
        // ambiguity report below TELLS a reader to write `smt:0402`, the part library editor's
        // footprint picker STORES exactly that (brief-railrf-24 R-rail24-4a), and a footprint
        // parameter copied out of a schematic is the fully canonical `smt:0402@N`. The reduction
        // below can read none of them, and the two failures are silent ones:
        //
        //   `smt:0402@N`  — `@` is not a separator, so the segment is `0402@N`, which is not a code,
        //                   and stripping the trailing `N` leaves `0402@`, which is not one either.
        //   `smt:3216-18` — `-` IS a separator, so a tantalum code is split into `3216` and `18`,
        //                   neither of which is a code. (Bare `3216-18` works, because step 1 tries
        //                   the whole token first; prefixing the scheme is what breaks it.)
        //
        // So the scheme is parsed by the type that owns it, first, and a MALFORMED one is reported
        // with that type's own refusal rather than falling through to a reduction that can only
        // reach a wrong answer or the generic sentence.
        if (FootprintRef.IsBuiltInReference(s))
            return FootprintRef.TryParse(s, out var canonical, out string? refusal)
                ? Matched(raw, canonical!.Case.Code, canonical.Case, canonical.Density)
                : new FootprintTokenMatch(
                      raw, raw, FootprintTokenOutcome.Unmatched, null, DensityLevel.Nominal, null,
                      $"'{raw}' claims to be a built-in footprint reference and is not one: {refusal}");

        // 0b — THE AMBIGUITY, BEFORE ANY LOOKUP. A bare token is the whole token, so this cannot be
        // reached by reduction and does not need to be re-asked below. It comes first because five
        // of the eight (1005, 1608, 2012, 3216, 3225) are NOT codes in SmtCaseTable at all — they
        // are real EIA chip codes circuitRF generates no pattern for — so a lookup-first order
        // would call them unmatched and lose the fact that their metric reading is a case we know
        // perfectly well. Unmatched is the wrong answer there: it hides a 2.4x collision behind a
        // word that means "nothing to see".
        if (IsBare(s) && _colliding.FirstOrDefault(c =>
                string.Equals(c.Token, s, StringComparison.OrdinalIgnoreCase)) is { Token: not null } hit)
            return new FootprintTokenMatch(
                raw, s, FootprintTokenOutcome.Ambiguous, null, DensityLevel.Nominal, null,
                AmbiguityReport(raw, hit.Token, hit.MetricMeansImperial));

        // 1 — the whole token.
        if (SmtCaseTable.Find(s) is { } whole)
            return Matched(raw, s, whole, DensityLevel.Nominal);

        // 2 and 3 — the segments, plainly and then reduced.
        foreach (string segment in s.Split(_separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (SmtCaseTable.Find(segment) is { } plain)
                return Matched(raw, segment, plain, DensityLevel.Nominal);

            string stripped = StripLeadingLetters(segment);
            if (stripped.Length == 0) continue;

            if (SmtCaseTable.Find(stripped) is { } afterPrefix)
                return Matched(raw, stripped, afterPrefix, DensityLevel.Nominal);

            var (body, density) = SplitTrailingDensity(stripped);
            if (body.Length > 0 && body != stripped && SmtCaseTable.Find(body) is { } afterDensity)
                return Matched(raw, body, afterDensity, density);
        }

        return new FootprintTokenMatch(
            raw, raw, FootprintTokenOutcome.Unmatched, null, DensityLevel.Nominal, null,
            $"'{raw}' is not a case size circuitRF knows. It is shown as written and was not matched " +
            "to the nearest code — the case sizes circuitRF generates are: " +
            string.Join(", ", SmtCaseTable.Codes) + ".");
    }

    private static FootprintTokenMatch Matched(
        string raw, string reduced, SmtCase matched, DensityLevel density)
    {
        var reference = FootprintRef.For(matched, density);
        return new FootprintTokenMatch(
            raw, reduced, FootprintTokenOutcome.Matched, matched, density, reference.ToString(),
            $"'{raw}' reads as {reference.Display}.");
    }

    /// <summary>Both readings, named, and the thing that would answer it. The same shape
    /// <c>convert</c>'s Excellon suppression refusal takes.</summary>
    private static string AmbiguityReport(string raw, string token, string metricMeansImperial)
    {
        string imperial = SmtCaseTable.Find(token) is { } own
            ? own.Display
            : $"{token} — a case circuitRF generates no pattern for";

        string metric = SmtCaseTable.Find(metricMeansImperial) is { } twin
            ? twin.Display
            : metricMeansImperial;

        return $"'{raw}' is ambiguous and was not matched. Read as an imperial code it is " +
               $"{imperial}; read as a metric code it is imperial {metric}. State the scheme — " +
               $"'smt:{token}' is the imperial reading and 'smt:{metricMeansImperial}' the metric one.";
    }

    /// <summary>A token is BARE when every character of it is a digit — the only shape that states
    /// no scheme at all. <c>C0402</c> and <c>0402M</c> both say something about themselves.</summary>
    private static bool IsBare(string token) => token.All(char.IsAsciiDigit);

    private static string StripLeadingLetters(string segment)
    {
        int i = 0;
        while (i < segment.Length && char.IsAsciiLetter(segment[i])) i++;
        return segment[i..];
    }

    private static (string Body, DensityLevel Density) SplitTrailingDensity(string segment)
    {
        if (segment.Length < 2) return (segment, DensityLevel.Nominal);
        char last = segment[^1];
        return last switch
        {
            'M' => (segment[..^1], DensityLevel.Most),
            'N' => (segment[..^1], DensityLevel.Nominal),
            'L' => (segment[..^1], DensityLevel.Least),
            _   => (segment, DensityLevel.Nominal),
        };
    }
}
