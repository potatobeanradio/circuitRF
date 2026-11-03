using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Design.Cells;

/// <summary>
/// A cell's corners between the <c>.ccell</c> that stores them and the <c>define</c> every extraction writes them into
/// (brief vendorlib-13 R-vl13-2b). The one conversion, so a run, an Export and a Copy to Workspace cannot disagree
/// about what a stored corner means.
/// </summary>
public static class CellCorners
{
    /// <summary>
    /// The <c>.ccell</c>'s corners as the design layer holds them: each non-blank value split into its expression and
    /// a trailing unit (<c>0.9 pF</c>), a value with no unit taking the parameter's own — as a row typed without one
    /// does. A value naming a parameter the cell does not declare is kept, so the reader names it rather than this
    /// dropping it silently.
    /// </summary>
    public static List<CellCorner> FromCcell(CcellFile? ccell)
    {
        var list = new List<CellCorner>();
        if (ccell?.Corners is not { } corners) return list;
        foreach (var c in corners)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) continue;
            var corner = new CellCorner(c.Name.Trim());
            foreach (var (name, text) in c.Values)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                var declared = ccell.Parameters.FirstOrDefault(p => p.Name == name);
                var (expr, unit) = Split(text, declared?.Unit);
                corner.Bindings.Add(new ParameterAssignment(name, expr, unit));
            }
            list.Add(corner);
        }
        return list;
    }

    /// <summary>The corners in the <c>.ccell</c> at <paramref name="cellDir"/>; empty when it has none or does not read.</summary>
    public static List<CellCorner> Of(string cellDir)
    {
        string path = Path.Combine(cellDir, CellFolder.CcellFileName);
        if (!File.Exists(path)) return [];
        try { return FromCcell(CellPersistence.LoadFromFile(path)); }
        catch { return []; }
    }

    /// <summary><c>0.9 pF</c> → (<c>0.9</c>, <c>pF</c>); <c>0.9</c> → (<c>0.9</c>, the parameter's unit).</summary>
    public static (string Expression, string? Unit) Split(string text, string? parameterUnit)
    {
        string t = text.Trim();
        int space = t.LastIndexOf(' ');
        if (space > 0)
        {
            string tail = UnitNormalizer.ToEngineUnit(t[(space + 1)..]);
            if (Units.IsRecognizedUnit(tail)) return (t[..space].Trim(), tail);
        }
        string u = UnitNormalizer.ToEngineUnit(parameterUnit ?? "");
        return (t, u.Length > 0 ? u : null);
    }
}
