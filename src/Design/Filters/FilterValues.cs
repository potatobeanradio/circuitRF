using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Matching;
using CircuitRF.Design.Statistics;
using CircuitRF.Design.VendorLibraries;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Filters;

/// <summary>Which preferred-number ladder a value is snapped onto. Exact leaves it alone.</summary>
public enum FilterSeriesKind
{
    /// <summary>The theoretical value.</summary>
    Exact,
    /// <summary>IEC 60063, six values a decade.</summary>
    E6,
    /// <summary>IEC 60063, twelve values a decade. The ladder the Smith chart ships.</summary>
    E12,
    /// <summary>IEC 60063, twenty-four values a decade.</summary>
    E24,
    /// <summary>IEC 60063, ninety-six values a decade.</summary>
    E96,
}

/// <summary>One part a library series offers, in base SI, with its stated tolerance when known.</summary>
public sealed record FilterPart(string Name, double Value, string Tolerance = "")
{
    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>
/// One series of a vendor library: the parts of one file that are inductors, or the parts that are
/// capacitors. A series is that file, which is how a placed part's own "same kind" list is cut.
/// </summary>
public sealed record FilterLibrarySeries(
    string Library,
    string File,
    string Label,
    bool Inductors,
    IReadOnlyList<FilterPart> Parts)
{
    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>
/// The three value columns. Exact is the ladder. An E-series is <see cref="PreferredValues"/>, the
/// same snap the Smith chart and the optimizer use, nearest by ratio. A library series is the
/// nearest stated value in a file of real parts.
/// </summary>
public static class FilterValues
{
    /// <summary>The value <paramref name="exact"/> snaps to, or itself for <see cref="FilterSeriesKind.Exact"/>.</summary>
    public static double Snap(bool inductor, double exact, FilterSeriesKind kind)
    {
        if (kind == FilterSeriesKind.Exact) return exact;
        var mantissas = kind switch
        {
            FilterSeriesKind.E6  => PreferredValues.E6,
            FilterSeriesKind.E12 => PreferredValues.E12,
            FilterSeriesKind.E24 => PreferredValues.E24,
            FilterSeriesKind.E96 => PreferredValues.E96,
            _ => PreferredValues.E12,
        };
        // The same spans the shipped ladders use: 0.1 nH…100 µH, 0.1 pF…100 nF.
        var ladder = PreferredValues.Series(mantissas, inductor ? -10 : -13, inductor ? -5 : -8);
        return PreferredValues.Snap(exact, ladder);
    }

    /// <summary>
    /// The part whose stated value is nearest <paramref name="exact"/>, nearest by ratio. Null when
    /// the series has no positive value.
    /// </summary>
    public static FilterPart? Nearest(double exact, IReadOnlyList<FilterPart> parts)
    {
        if (!(exact > 0) || parts.Count == 0) return null;
        FilterPart? chosen = null;
        double bestRatio = double.PositiveInfinity;
        foreach (var p in parts)
        {
            if (!(p.Value > 0)) continue;
            double ratio = Math.Abs(Math.Log(p.Value / exact));
            if (ratio < bestRatio)
            {
                bestRatio = ratio;
                chosen = p;
            }
        }
        return chosen;
    }

    /// <summary>
    /// The inductor series and the capacitor series in the vendor libraries a workspace names.
    /// A library that does not index is skipped and named in <paramref name="notes"/>.
    /// </summary>
    public static IReadOnlyList<FilterLibrarySeries> LibrarySeries(string? workspaceRoot, List<string> notes)
    {
        var found = new List<FilterLibrarySeries>();
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return found;

        string rootDir = Directory.Exists(workspaceRoot)
            ? workspaceRoot
            : Path.GetDirectoryName(workspaceRoot) ?? "";
        if (rootDir.Length == 0) return found;

        string? cws = Directory.EnumerateFiles(rootDir, "*.cws").FirstOrDefault();
        if (cws is null) return found;

        CwsFile ws;
        try { ws = WorkspacePersistence.LoadFromFile(cws); }
        catch (Exception e)
        {
            notes.Add($"The workspace could not be read: {e.Message}");
            return found;
        }

        foreach (var lib in ws.VendorLibraries ?? [])
        {
            string? libRoot = VendorLibraryRefs.ResolveLibraryRoot(rootDir, lib.Name, out string? why);
            if (libRoot is null)
            {
                notes.Add(why ?? $"Vendor library '{lib.Name}' could not be resolved.");
                continue;
            }
            var build = VendorLibraryIndex.Build(libRoot);
            if (!build.IsUsable)
            {
                notes.Add($"Vendor library '{lib.Name}' was not indexed" +
                          (build.Refusal is { } r ? $": {r}" : "."));
                continue;
            }
            foreach (var group in build.Entries.GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase))
            {
                Add(lib.Name, group.Key, group, VendorPartQuantity.Inductance, inductors: true);
                Add(lib.Name, group.Key, group, VendorPartQuantity.Capacitance, inductors: false);
            }

            void Add(string library, string file, IEnumerable<VendorLibraryEntry> entries,
                     VendorPartQuantity quantity, bool inductors)
            {
                var parts = entries
                    .Where(e => e.Header.Quantity == quantity && e.Header.Value is > 0)
                    .Select(e => new FilterPart(e.Part, e.Header.Value!.Value,
                                                ToleranceOf(e.Header, e.Part)))
                    .OrderBy(p => p.Value)
                    .ToList();
                if (parts.Count == 0) return;
                // Series name only — the library is already chosen by which series list this is in.
                string stem = Path.GetFileNameWithoutExtension(file.Replace('\\', '/').Split('/').Last());
                found.Add(new FilterLibrarySeries(library, file, stem, inductors, parts));
            }
        }
        return found;
    }

    /// <summary>
    /// The IEC series' conventional tolerance, as Yield's compact editor spells it
    /// (<see cref="ToleranceText.Format"/>). Empty for Exact.
    /// </summary>
    public static string ToleranceOf(FilterSeriesKind kind)
    {
        string? tol = kind switch
        {
            FilterSeriesKind.E6  => "20%",
            FilterSeriesKind.E12 => "10%",
            FilterSeriesKind.E24 => "5%",
            FilterSeriesKind.E96 => "1%",
            _ => null,
        };
        return FormatTolerancePercent(tol);
    }

    /// <summary>
    /// The tolerance a vendor part states. Order: a <c>Tolerance</c>/<c>Tol</c> header field; then
    /// each Description slash-field (a bare EIA letter J/K/M/… or a <c>±N%</c> token); then
    /// <see cref="BomFile.ParseDescription"/> on the whole Description; then the EIA letter that
    /// follows the part's value code in its name (static SPICE editions rarely put ±% in the
    /// header). Empty when none is stated.
    /// </summary>
    public static string ToleranceOf(VendorLibraryHeader header, string? partName = null)
    {
        if (!string.IsNullOrWhiteSpace(header.Tolerance)
            && TryToleranceShown(header.Tolerance, out string fromTol))
            return fromTol;

        foreach (string field in header.DescriptionFields)
        {
            if (TryToleranceShown(field, out string shown)) return shown;
        }

        if (header.Description is { } d)
        {
            double? pct = BomFile.ParseDescription(d).TolerancePercent;
            if (pct is { } p && p > 0)
                return FormatTolerancePercent(p.ToString("0.###", CultureInfo.InvariantCulture) + "%");

            foreach (Match m in PercentTol.Matches(d))
            {
                if (TryToleranceShown(m.Value, out string shown)) return shown;
            }
        }

        if (partName is not null && header.Value is { } v
            && TryToleranceFromPartName(partName, v, header.Quantity, out string fromPn))
            return fromPn;

        return "";
    }

    private static readonly Regex PercentTol =
        new(@"[±+\-/]*\s*\d+(?:\.\d+)?\s*%", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// A single Description field or Tolerance value: EIA letter alone, or a percent token.
    /// EIA letters are only accepted when the whole field is that letter — the same silent-guess
    /// ban BomFile applies inside free text does not apply to a dedicated slash field.
    /// </summary>
    private static bool TryToleranceShown(string raw, out string shown)
    {
        shown = "";
        string t = raw.Trim().Trim(',', ';');
        if (t.Length == 0) return false;

        if (t.Length == 1 && TryEiaLetter(t[0], valueSi: null, quantity: VendorPartQuantity.None,
                                          out shown))
            return true;

        double? pct = BomFile.ParseDescription(t).TolerancePercent;
        if (pct is { } p && p > 0)
        {
            shown = FormatTolerancePercent(p.ToString("0.###", CultureInfo.InvariantCulture) + "%");
            return true;
        }
        return false;
    }

    /// <summary>
    /// The EIA letter that follows an engineering-notation value code in <paramref name="part"/>
    /// (e.g. <c>3R0J</c>, <c>4N3D</c>). The code must match <paramref name="valueSi"/> so a letter
    /// elsewhere in the name is not treated as a tolerance.
    /// </summary>
    private static bool TryToleranceFromPartName(
        string part, double valueSi, VendorPartQuantity quantity, out string shown)
    {
        shown = "";
        string u = part.ToUpperInvariant();
        foreach (string enc in ValueEncodings(valueSi, quantity))
        {
            int i = 0;
            while ((i = u.IndexOf(enc, i, StringComparison.Ordinal)) >= 0)
            {
                int j = i + enc.Length;
                if (j < u.Length && TryEiaLetter(u[j], valueSi, quantity, out shown))
                    return true;
                i++;
            }
        }
        return false;
    }

    /// <summary>Engineering spellings of <paramref name="si"/> that chip part numbers use
    /// (<c>3R0</c>, <c>4N3</c>, <c>100</c> for 10 pF).</summary>
    private static IEnumerable<string> ValueEncodings(double si, VendorPartQuantity quantity)
    {
        if (!(si > 0) || !double.IsFinite(si)) yield break;

        (double Scale, char Dot)[] steps = quantity switch
        {
            VendorPartQuantity.Capacitance =>
                [(1e12, 'R'), (1e9, 'N'), (1e6, 'U')],
            VendorPartQuantity.Inductance =>
                [(1e9, 'N'), (1e6, 'U'), (1e3, 'R')],
            _ =>
                [(1e12, 'R'), (1e9, 'N'), (1e6, 'U')],
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (scale, dot) in steps)
        {
            double v = si * scale;
            if (v < 0.05 || v >= 100000) continue;

            // One-decimal engineering: 3.0 → 3R0, 4.3 → 4N3.
            string one = v.ToString("0.0", CultureInfo.InvariantCulture);
            int dotAt = one.IndexOf('.');
            if (dotAt > 0)
            {
                string eng = one[..dotAt] + dot + one[(dotAt + 1)..];
                if (seen.Add(eng)) yield return eng;
            }

            // EIA two-significant-digit code: 10 → 100, 4.7 → 475 (×10^n). Only for ≥ 1.
            if (v >= 1)
            {
                int exp = (int)Math.Floor(Math.Log10(v)) - 1;
                if (exp < 0) exp = 0;
                int mant = (int)Math.Round(v / Math.Pow(10, exp));
                if (mant >= 10 && mant <= 99)
                {
                    string eia = mant.ToString(CultureInfo.InvariantCulture)
                                 + exp.ToString(CultureInfo.InvariantCulture);
                    if (seen.Add(eia)) yield return eia;
                }
            }
        }
    }

    /// <summary>
    /// EIA tolerance letter → Yield spelling. B/C/D are absolute on small L/C (pF / nH); the rest
    /// are percent. Without a value, every letter is read as percent (slash-field case).
    /// </summary>
    private static bool TryEiaLetter(
        char letter, double? valueSi, VendorPartQuantity quantity, out string shown)
    {
        shown = "";
        char L = char.ToUpperInvariant(letter);
        bool absolute = valueSi is { } v && quantity switch
        {
            VendorPartQuantity.Capacitance => v <= 10e-12 && L is 'B' or 'C' or 'D' or 'W',
            VendorPartQuantity.Inductance  => v <= 10e-9  && L is 'B' or 'C' or 'D' or 'W',
            _ => false,
        };

        if (absolute)
        {
            string? abs = L switch
            {
                'B' => quantity == VendorPartQuantity.Inductance ? "0.1nH" : "0.1pF",
                'C' => quantity == VendorPartQuantity.Inductance ? "0.2nH" : "0.25pF",
                'D' => quantity == VendorPartQuantity.Inductance ? "0.5nH" : "0.5pF",
                'W' => quantity == VendorPartQuantity.Inductance ? "0.05nH" : "0.05pF",
                _ => null,
            };
            if (abs is null) return false;
            shown = FormatTolerancePercent(abs);
            return shown.Length > 0;
        }

        double? pct = L switch
        {
            'B' => 0.1, 'C' => 0.25, 'D' => 0.5, 'F' => 1, 'G' => 2,
            'H' => 3, 'J' => 5, 'K' => 10, 'M' => 20, 'N' => 30,
            'W' => 0.05,
            _ => null,
        };
        if (pct is not { } p) return false;
        shown = FormatTolerancePercent(p.ToString("0.###", CultureInfo.InvariantCulture) + "%");
        return shown.Length > 0;
    }

    /// <summary>Parts of <paramref name="series"/> whose value matches <paramref name="target"/> (ratio).</summary>
    public static IReadOnlyList<FilterPart> PartsAtValue(FilterLibrarySeries series, double target, double ratioTol = 0.02)
    {
        if (!(target > 0)) return series.Parts;
        double lim = Math.Abs(Math.Log(1.0 + Math.Max(0.001, ratioTol)));
        var hit = series.Parts
            .Where(p => p.Value > 0 && Math.Abs(Math.Log(p.Value / target)) <= lim)
            .ToList();
        if (hit.Count > 0) return hit;
        return Nearest(target, series.Parts) is { } one ? [one] : [];
    }

    private static string FormatTolerancePercent(string? tol) =>
        string.IsNullOrEmpty(tol)
            ? ""
            : ToleranceText.Format(StatDistribution.Gauss,
                new StatSpread { Tol = tol, Sigmas = ToleranceText.DefaultSigmas });
}
