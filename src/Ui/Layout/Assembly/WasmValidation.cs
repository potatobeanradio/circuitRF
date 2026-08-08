using CircuitRF.Ui.Layout.Drc;

namespace CircuitRF.Ui.Layout.Assembly;

/// <summary>
/// Structural checks on a `.wasm`, mirroring <see cref="TechValidation"/>: it never throws, it never
/// mutates, and every finding is a sentence a user can act on.
///
/// <para><b>A rule set with problems still loads and still runs.</b> One malformed envelope or one
/// unparseable expression costs that one rule, never the file — the same rule
/// <see cref="TechnologyResolver"/> follows for a technology that fails validation. Refusing the
/// whole file would leave the user with no assembly checking at all because of a typo in a rule they
/// were not relying on.</para>
/// </summary>
public static class WasmValidation
{
    public static IReadOnlyList<string> Validate(WasmFile wasm)
    {
        ArgumentNullException.ThrowIfNull(wasm);
        var problems = new List<string>();

        ValidateEnvelopes(wasm, problems);
        ValidateRules(wasm, problems);
        ValidateMaterials(wasm, problems);

        return problems;
    }

    private static void ValidateEnvelopes(WasmFile wasm, List<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var env in wasm.Envelopes)
        {
            string label = string.IsNullOrWhiteSpace(env.Name) ? "(unnamed)" : env.Name;

            if (string.IsNullOrWhiteSpace(env.Name))
                problems.Add("An envelope table has no name, so no rule can look it up.");
            else if (!seen.Add(env.Name))
                problems.Add($"Two envelope tables are both named \"{env.Name}\"; only the first is reachable.");

            if (env.Points.Count == 0)
            {
                problems.Add($"Envelope \"{label}\" has no points, so it states no limit.");
                continue;
            }

            // A one-point table is LEGAL and is a constant (§5 open question 3, adopted): a house
            // that states one number means one number, and demanding a second point to say so would
            // be the format arguing with the document it is transcribing.
            for (int i = 1; i < env.Points.Count; i++)
            {
                long prev = env.Points[i - 1].X;
                long cur  = env.Points[i].X;

                // Refused, never sorted-and-continued. Interpolating between points whose order is
                // unknown produces a limit curve nobody stated, and quietly reordering someone's
                // table hides a transcription error that is much better surfaced.
                if (cur == prev)
                    problems.Add($"Envelope \"{label}\" states span {cur} twice; a table cannot give two " +
                                 "limits at one span.");
                else if (cur < prev)
                    problems.Add($"Envelope \"{label}\" is not in increasing span order ({prev} then {cur}); " +
                                 "sort the table rather than relying on the checker to guess.");
            }
        }
    }

    private static void ValidateRules(WasmFile wasm, List<string> problems)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var envelopes = wasm.Envelopes
            .Where(e => !string.IsNullOrWhiteSpace(e.Name))
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (section, rule) in wasm.AllRules())
        {
            string label = string.IsNullOrWhiteSpace(rule.Name) ? "(unnamed)" : rule.Name;

            if (string.IsNullOrWhiteSpace(rule.Name))
                problems.Add($"A rule in the {section} section has no name; a violation has nothing to " +
                             "trace back to.");
            else if (!names.Add(rule.Name))
                problems.Add($"Two rules are both named \"{rule.Name}\"; a violation could not say which.");

            if (!DrcPredicateParser.TryParse(rule.Expression, out var predicate, out string? error) ||
                predicate is null)
            {
                problems.Add($"Rule \"{label}\" ({section}) will not parse and cannot be checked: {error}");
                continue;
            }

            foreach (string table in predicate.ReferencedEnvelopes())
                if (!envelopes.Contains(table))
                    problems.Add($"Rule \"{label}\" looks up envelope \"{table}\", which this file does " +
                                 "not declare.");

            // Two DIFFERENT pairings in one predicate have no single candidate to be evaluated
            // against — `wire_spacing(G1,G2) >= 4mil && foot_pitch(G3,G4) >= 6mil` is two rules
            // wearing one name. Refused here rather than half-evaluated later.
            var pairs = predicate.PairCalls();
            if (pairs.Count > 1)
            {
                var first = pairs[0];
                foreach (var other in pairs.Skip(1))
                    if (!SamePairing(first, other))
                    {
                        problems.Add($"Rule \"{label}\" compares two different wire pairings " +
                                     $"({first.SetA}/{first.SetB} and {other.SetA}/{other.SetB}); " +
                                     "split it into one rule per pairing.");
                        break;
                    }
            }

            // In pair domain a per-wire function has to name one of the pair's own sets, or there is
            // no candidate wire for it to measure — see DrcPredicateExpr's header.
            if (predicate.Domain == WasmDomain.Pair && predicate.PairSets() is { } sets)
            {
                foreach (string set in predicate.ReferencedSets())
                    if (!Same(set, sets.A) && !Same(set, sets.B))
                        problems.Add($"Rule \"{label}\" measures wire set \"{set}\", which is not one of " +
                                     $"the two sets its pairing draws from ({sets.A}, {sets.B}).");
            }
        }
    }

    private static void ValidateMaterials(WasmFile wasm, List<string> problems)
    {
        foreach (long d in wasm.AllowedDiametersNm)
            if (d <= 0)
                problems.Add($"The material section lists a non-positive wire diameter ({d} nm).");

        if (wasm.AllowedMetals.Any(string.IsNullOrWhiteSpace))
            problems.Add("The material section lists a blank wire metal.");
    }

    private static bool SamePairing(WasmValue.PairCall a, WasmValue.PairCall b) =>
        (Same(a.SetA, b.SetA) && Same(a.SetB, b.SetB)) ||
        (Same(a.SetA, b.SetB) && Same(a.SetB, b.SetA));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
