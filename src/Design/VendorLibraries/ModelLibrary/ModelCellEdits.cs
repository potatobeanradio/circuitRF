using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Statistics;

namespace CircuitRF.Design.VendorLibraries.ModelLibrary;

/// <summary>One parameter as Export to Library's Parameters table shows it (brief vendorlib-13 R-vl13-1b).</summary>
/// <param name="Nominal">The default with any distribution taken off: what the spread is drawn around.</param>
/// <param name="Distribution">Gauss or Unif when the default is a distribution the panel's grammar can show; None otherwise.</param>
/// <param name="Spread">That distribution's spread in the panel's own notation (<c>± 5 % at 3σ</c>); empty for none.</param>
/// <param name="Unreadable">The default when it is a distribution the grammar cannot show (a <c>limit</c>, an expression
/// spread) — left as it is unless the user replaces it.</param>
public sealed record ModelParameterRow(string Name, string Nominal, string Unit, StatDistribution Distribution, string Spread,
                                       string? Unreadable);

/// <summary>
/// The two edits Export to Library makes to the SOURCE cell's <c>.ccell</c> (brief vendorlib-13 R-vl13-1b, R-vl13-2c): a
/// parameter's tolerance, written as the distribution function on its default, and the cell's corners. Written to the
/// source cell so the next export keeps them — never only into an exported copy. The dialog is a shell over these.
/// </summary>
public static class ModelCellEdits
{
    /// <summary>The cell's parameters with their tolerances read back; empty when it has no <c>.ccell</c>.</summary>
    public static IReadOnlyList<ModelParameterRow> Parameters(string cellDir)
    {
        if (Load(cellDir) is not { } ccell) return [];
        return [.. ccell.Parameters.Select(p =>
        {
            string expr = p.DefaultExpression;
            if (ModelTolerance.FromFunction(expr, p.Unit) is { } t)
                return new ModelParameterRow(p.Name, t.Nominal, p.Unit, t.Distribution, ToleranceText.Format(t.Distribution, t.Spread), null);
            bool drawn = CircuitRF.Core.Expressions.Evaluator.ContainsStatisticalCall(expr);
            return new ModelParameterRow(p.Name, drawn ? ModelTolerance.NominalOf(expr) : expr, p.Unit, StatDistribution.None, "",
                                         drawn ? expr : null);
        })];
    }

    /// <summary>
    /// Writes <paramref name="parameter"/>'s tolerance: <paramref name="spread"/> typed in the Yield panel's grammar,
    /// read by its own parser, mapped by <see cref="ModelTolerance.ToFunction"/>. <see cref="StatDistribution.None"/> or
    /// an empty spread writes the nominal back. Null on success; otherwise why, and nothing is written.
    /// </summary>
    public static string? SetTolerance(string cellDir, string parameter, StatDistribution distribution, string spread)
    {
        if (Load(cellDir) is not { } ccell) return "The cell has no .ccell to write.";
        var p = ccell.Parameters.FirstOrDefault(x => x.Name == parameter);
        if (p is null) return $"The cell declares no parameter {parameter}.";
        string nominal = ModelTolerance.NominalOf(p.DefaultExpression);
        string next;
        if (distribution == StatDistribution.None || spread.Trim().Length == 0) next = nominal;
        else
        {
            if (ToleranceText.Parse(spread, distribution) is not { } sp)
                return $"'{spread}' is not a spread — write ± 5 % at 3σ, σ 1 Ω or 45 … 55 Ω.";
            if (ModelTolerance.ToFunction(nominal, p.Unit, distribution, sp, out string? why) is not { } fn) return why;
            next = fn;
        }
        if (next == p.DefaultExpression) return null;
        p.DefaultExpression = next;
        return Save(cellDir, ccell);
    }

    /// <summary>The cell's corners, as stored.</summary>
    public static IReadOnlyList<CcellCorner> Corners(string cellDir)
        => Load(cellDir)?.Corners is { } c ? [.. c.Select(x => new CcellCorner { Name = x.Name, Values = new(x.Values, StringComparer.Ordinal) })] : [];

    /// <summary>Why <paramref name="corners"/> cannot be stored — the reader's own refusals, said before they reach a
    /// <c>.cnl</c> — or null.</summary>
    public static string? CornerProblem(IReadOnlyList<CcellCorner> corners, IEnumerable<string> parameters)
    {
        var declared = parameters.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in corners)
        {
            string n = c.Name.Trim();
            if (n.Length == 0) return "Every corner needs a name.";
            if (!(char.IsLetter(n[0]) || n[0] == '_') || !n.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                return $"'{n}' is not a corner name; a corner's name is one word.";
            if (n.Equals(CellCorner.Nominal, StringComparison.OrdinalIgnoreCase))
                return "'nominal' is reserved: it is the cell's own defaults. Name the corner something else.";
            if (!seen.Add(n)) return $"Two corners are named '{n}'.";
            foreach (var k in c.Values.Keys)
                if (!declared.Contains(k)) return $"Corner '{n}' sets {k}, which is not a parameter of the cell.";
        }
        return null;
    }

    /// <summary>Stores <paramref name="corners"/> in the cell's <c>.ccell</c>, blank values dropped (a blank is nominal).
    /// Null on success; otherwise why, and nothing is written.</summary>
    public static string? SetCorners(string cellDir, IReadOnlyList<CcellCorner> corners)
    {
        if (Load(cellDir) is not { } ccell) return "The cell has no .ccell to write.";
        if (CornerProblem(corners, ccell.Parameters.Select(p => p.Name)) is { } why) return why;
        var stored = corners.Select(c => new CcellCorner
        {
            Name = c.Name.Trim(),
            Values = c.Values.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                             .ToDictionary(kv => kv.Key, kv => kv.Value.Trim(), StringComparer.Ordinal),
        }).ToList();
        ccell.Corners = stored.Count == 0 ? null : stored;
        return Save(cellDir, ccell);
    }

    private static CcellFile? Load(string cellDir)
    {
        string path = Path.Combine(cellDir, CellFolder.CcellFileName);
        if (!File.Exists(path)) return null;
        try { return CellPersistence.LoadFromFile(path); }
        catch { return null; }
    }

    private static string? Save(string cellDir, CcellFile ccell)
    {
        try { CellPersistence.SaveToFile(Path.Combine(cellDir, CellFolder.CcellFileName), ccell); return null; }
        catch (Exception ex) { return $"The cell's .ccell could not be written: {ex.Message}"; }
    }
}
