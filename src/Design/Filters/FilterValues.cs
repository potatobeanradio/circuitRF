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
    /// <summary>
    /// Real parts from the workspace's vendor libraries: one inductor series and one capacitor series,
    /// each element the nearest stated value (or a hand-picked part). An element whose kind has no
    /// series chosen keeps its exact value. Appended, so a stored <c>Values</c> keeps its meaning.
    /// </summary>
    VendorLibrary,
}

/// <summary>
/// Whether the LC Filter Designer sizes the order to the mask or takes a typed one. The Filter block
/// stores it (<c>OrderMode</c>) so the Designer reopens the way it was left; the block's own
/// response reads <c>Order</c> either way.
/// </summary>
public enum FilterOrderMode
{
    /// <summary>The closed-form minimum for the family and the mask edges.</summary>
    Minimum,

    /// <summary>The order in the Order box.</summary>
    Specify,
}

/// <summary>One part a library series offers, in base SI, with its stated tolerance when known.</summary>
/// <param name="Vendor">The library part itself, which is what a placed ladder and a simulated Filter use;
/// null for a part built by hand (a test).</param>
public sealed record FilterPart(string Name, double Value, string Tolerance = "", VendorPartRef? Vendor = null)
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

    /// <summary>
    /// What a Filter block's <c>LSeries</c>/<c>CSeries</c> parameter stores to name this series:
    /// the vendor library, then the series. Compared, never parsed.
    /// </summary>
    public string Key => Library + ": " + Label;
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
        if (kind is FilterSeriesKind.Exact or FilterSeriesKind.VendorLibrary) return exact;
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
    /// The value one element is built with, and the library part behind it when there is one: the
    /// exact value, the IEC series' nearest preferred value, or for <see cref="FilterSeriesKind.VendorLibrary"/>
    /// the hand-picked part in <paramref name="overrides"/>, else the nearest part of the element's own
    /// series, else the exact value. The one rule the Designer's table, a placed ladder and a simulated
    /// Filter block all build with.
    /// </summary>
    public static (double Value, FilterLibrarySeries? Series, FilterPart? Part) Choose(
        FilterElement element, FilterSeriesKind kind,
        FilterLibrarySeries? inductors, FilterLibrarySeries? capacitors,
        IReadOnlyDictionary<string, (FilterLibrarySeries Series, FilterPart Part)>? overrides)
    {
        if (kind == FilterSeriesKind.Exact)
            return (element.Value, null, null);
        if (kind != FilterSeriesKind.VendorLibrary)
        {
            // A value the search for a passing design picked: another value of the same series.
            if (overrides is not null && overrides.TryGetValue(element.Name, out var pick)
                && pick.Series.Library == IecLibrary && pick.Series.Inductors == element.Inductor
                && pick.Series.Key == IecSeries(kind, element.Inductor).Key)
                return (pick.Part.Value, null, null);
            return (Snap(element.Inductor, element.Value, kind), null, null);
        }
        if (overrides is not null && overrides.TryGetValue(element.Name, out var ov) && ov.Series.Inductors == element.Inductor)
            return (ov.Part.Value, ov.Series, ov.Part);
        var series = element.Inductor ? inductors : capacitors;
        if (series is not null && Nearest(element.Value, series.Parts) is { } near)
            return (near.Value, series, near);
        return (element.Value, series, null);
    }

    /// <summary>The library name an IEC series goes by when its values are handled as parts.</summary>
    public const string IecLibrary = "IEC 60063";

    private static readonly Dictionary<(FilterSeriesKind, bool), FilterLibrarySeries> IecCache = [];

    /// <summary>
    /// An IEC series as a list of parts, over the span <see cref="Snap"/> uses: what the search for a
    /// passing design picks other values from, and what a block's <c>Parts</c> names them by.
    /// </summary>
    public static FilterLibrarySeries IecSeries(FilterSeriesKind kind, bool inductors)
    {
        lock (IecCache)
        {
            if (IecCache.TryGetValue((kind, inductors), out var cached)) return cached;
            var mantissas = kind switch
            {
                FilterSeriesKind.E6  => PreferredValues.E6,
                FilterSeriesKind.E24 => PreferredValues.E24,
                FilterSeriesKind.E96 => PreferredValues.E96,
                _ => PreferredValues.E12,
            };
            var values = PreferredValues.Series(mantissas, inductors ? -10 : -13, inductors ? -5 : -8);
            var parts = values.Select(v => new FilterPart(ValueName(v, inductors ? "H" : "F"), v, ToleranceOf(kind)))
                              .ToList();
            var series = new FilterLibrarySeries(IecLibrary, "", $"{kind} {(inductors ? "inductors" : "capacitors")}",
                                                 inductors, parts);
            IecCache[(kind, inductors)] = series;
            return series;
        }
    }

    /// <summary>A value as a part name: three significant figures and an SI prefix, e.g. <c>4.7nH</c>.</summary>
    private static string ValueName(double v, string unit)
    {
        string[] prefixes = ["f", "p", "n", "u", "m", ""];
        int i = 0;
        double scaled = v * 1e15;
        while (i < prefixes.Length - 1 && Math.Abs(scaled) >= 1000) { scaled /= 1000; i++; }
        return scaled.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + prefixes[i] + unit;
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
                                                ToleranceOf(e.Header, e.Part), VendorPartRef.For(library, e)))
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

            // Engineering: 3.0 → 3R0 and 3R; 4.3 → 4N3; 15 → 15N0 and 15N;
            // below 1 → R80 / R8 for 0.80 (leading letter, no integer digit).
            if (v < 1)
            {
                int tenths = (int)Math.Round(v * 10);
                if (tenths is >= 1 and <= 9)
                {
                    if (seen.Add(dot + tenths.ToString(CultureInfo.InvariantCulture) + "0"))
                        yield return dot + tenths.ToString(CultureInfo.InvariantCulture) + "0";
                    if (seen.Add(dot + tenths.ToString(CultureInfo.InvariantCulture)))
                        yield return dot + tenths.ToString(CultureInfo.InvariantCulture);
                }
                int hundredths = (int)Math.Round(v * 100);
                if (hundredths is >= 10 and <= 99
                    && seen.Add(dot + hundredths.ToString(CultureInfo.InvariantCulture)))
                    yield return dot + hundredths.ToString(CultureInfo.InvariantCulture);
                continue;
            }

            string one = v.ToString("0.0", CultureInfo.InvariantCulture);
            int dotAt = one.IndexOf('.');
            if (dotAt > 0)
            {
                string eng = one[..dotAt] + dot + one[(dotAt + 1)..];
                if (seen.Add(eng)) yield return eng;
                if (one.EndsWith(".0", StringComparison.Ordinal)
                    && seen.Add(one[..dotAt] + dot))
                    yield return one[..dotAt] + dot;
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
