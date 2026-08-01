using CircuitRF.Core.Design;

namespace CircuitRF.Core.Netlist;

/// <summary>
/// One part's formulations, found in a kit's own netlist rather than declared anywhere.
/// </summary>
/// <param name="Stem">The name they share — <c>X</c> for <c>X_MET</c> and <c>X_ROOT</c>.</param>
/// <param name="Choices">The trailing tokens that tell them apart, in the order the file listed them.</param>
/// <param name="Buildable">
/// The subset circuitRF can actually build. The first of these is what a placed part starts on, which
/// is the whole reason a user gets an answer on the first Run instead of an explanation.
/// </param>
public sealed record KitVariantFamily(string Stem, IReadOnlyList<string> Choices, IReadOnlyList<string> Buildable)
{
    /// <summary>The cell name for one choice — the pattern a part records and substitutes into.</summary>
    public string CellNameFor(string choice) => $"{Stem}_{choice}";

    /// <summary>Choices offered but not buildable. Still offered: a user asking for one is told it is
    /// not implemented, which is information; leaving it out looks like the kit is missing something.</summary>
    public IReadOnlyList<string> Unsupported
        => Choices.Where(c => !Buildable.Contains(c, StringComparer.Ordinal)).ToList();
}

/// <summary>
/// Works out, from a kit's netlist alone, which of its subcircuits are formulations of one part and
/// which of those circuitRF can build.
///
/// <para><b>Why this is discovery rather than declaration.</b> Both facts are in the file: names
/// sharing a stem and differing only by a trailing token are one part in several forms, and a form is
/// buildable exactly when everything it instantiates resolves. Declaring either of them means someone
/// writes a file and puts it somewhere, which is the thing importing a read-only kit must not
/// require.</para>
/// </summary>
public static class KitVariantDiscovery
{
    /// <summary>
    /// Every family in the library. A name with no sibling is not a family — one formulation is not a
    /// choice, and offering a picker with a single entry is noise.
    /// </summary>
    public static IReadOnlyList<KitVariantFamily> Find(Library library, IReadOnlySet<string> incompleteCells)
    {
        var byStem = new Dictionary<string, List<(string Choice, Cell Cell)>>(StringComparer.Ordinal);

        foreach (var cell in library.Cells)
        {
            int underscore = cell.Name.LastIndexOf('_');
            if (underscore <= 0 || underscore == cell.Name.Length - 1) continue;

            string stem   = cell.Name[..underscore];
            string choice = cell.Name[(underscore + 1)..];
            if (stem.Length == 0) continue;

            if (!byStem.TryGetValue(stem, out var list)) byStem[stem] = list = [];
            list.Add((choice, cell));
        }

        var result = new List<KitVariantFamily>();
        foreach (var (stem, members) in byStem)
        {
            if (members.Count < 2) continue;

            var buildable = members.Where(m => IsBuildable(m.Cell, library, incompleteCells, []))
                                   .Select(m => m.Choice)
                                   .ToList();

            result.Add(new KitVariantFamily(stem, members.Select(m => m.Choice).ToList(), buildable));
        }

        return result;
    }

    /// <summary>
    /// The family belonging to a part, or null. Chosen by how much of the part's own name the stem
    /// shares — a kit names a part and its formulations from the same words, but rarely identically
    /// (<c>…_MODEL</c> against <c>…_SPmodel_MET</c>), so a prefix test finds nothing and the most
    /// distinctive overlap is what actually identifies it.
    ///
    /// <para>A tie is no answer: two families fitting a part equally well means the name does not
    /// identify one, and guessing would attach a formulation choice to the wrong part.</para>
    /// </summary>
    public static KitVariantFamily? ForPart(string partId, IReadOnlyList<KitVariantFamily> families)
    {
        var partTokens = new HashSet<string>(Tokens(partId), StringComparer.OrdinalIgnoreCase);
        if (partTokens.Count == 0) return null;

        KitVariantFamily? best = null;
        int bestScore = 0, tiedAt = 0;

        foreach (var family in families)
        {
            int score = Tokens(family.Stem).Distinct(StringComparer.OrdinalIgnoreCase)
                                           .Count(partTokens.Contains);
            if (score > bestScore) { bestScore = score; best = family; tiedAt = 1; }
            else if (score == bestScore && score > 0) tiedAt++;
        }

        return bestScore >= 2 && tiedAt == 1 ? best : null;
    }

    /// <summary>
    /// Whether circuitRF can build this cell — it and everything it instantiates, all the way down,
    /// were read completely.
    ///
    /// <para><b>An unfamiliar type is NOT the test, and getting that backwards inverts the answer.</b>
    /// A type circuitRF does not recognise is very often a device a provider supplies, which is the
    /// normal case for the formulation a kit expects you to use; while the formulation that CANNOT be
    /// built is typically the one written in a form the reader could not take. Testing for unfamiliar
    /// types marks the working formulation broken and the broken one working — measured,
    /// which is how this rule was found.</para>
    ///
    /// <para>Recursive, because a cell reads cleanly while the cell it instantiates does not, and it
    /// is the whole chain that has to hold. The visiting set makes a cyclic file terminate rather than
    /// recurse forever.</para>
    /// </summary>
    private static bool IsBuildable(Cell cell, Library library, IReadOnlySet<string> incomplete,
                                    HashSet<string> visiting)
    {
        if (incomplete.Contains(cell.Name)) return false;
        if (!visiting.Add(cell.Name)) return true;   // already on this chain — not a second reason to fail

        foreach (var instance in cell.Instances)
        {
            var sub = library.Cells.FirstOrDefault(c => c.Name.Equals(instance.Reference, StringComparison.Ordinal));
            if (sub is null) continue;               // not a cell here — a primitive, or a provider's device
            if (!IsBuildable(sub, library, incomplete, visiting)) return false;
        }

        visiting.Remove(cell.Name);
        return true;
    }

    /// <summary>Splits a kit name into its words, on separators and case changes.</summary>
    private static IEnumerable<string> Tokens(string name)
    {
        var current = new System.Text.StringBuilder();
        foreach (char c in name)
        {
            if (c is '_' or '-' or '.' or ' ')
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}
