using System.Globalization;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>explain --tunables</c> — the tunable catalog (<see cref="TunableCatalog"/>) of a schematic, a
/// cell's primary schematic, or a <c>.cnl</c>. It owns no discovery: a schematic's catalog is the one
/// the Tuning window lists, through the resolver a run descends with.
/// </summary>
internal static class ExplainTunables
{
    public static (ExplainTunablesJson? Report, int Exit) Collect(string path, DocumentKind kind)
    {
        TunableCatalog catalog;
        Core.Design.TuningSetup? setup;
        try
        {
            switch (kind)
            {
                case DocumentKind.Schematic:
                case DocumentKind.Cell:
                {
                    string csch = kind == DocumentKind.Schematic ? path : PrimarySchematic(path) ?? "";
                    if (csch.Length == 0)
                        return (null, JsonRun.Fail(CliDiagnostics.ExplainNotApplicable("--tunables", DocumentKinds.Name(kind))));
                    var (model, _, _) = CircuitSource.LoadSchematic(csch);
                    string? cws = DocumentKinds.AncestorCws(Path.GetFullPath(csch));
                    catalog = TunableCatalog.Discover(model, DiskCellResolver.Instance,
                                                      cws is null ? null : Path.GetDirectoryName(cws));
                    setup = model.Tuning;
                    break;
                }
                case DocumentKind.Netlist:
                {
                    var (lib, tb) = CnlTechnologyBinding.ReadFile(path);
                    catalog = TunableCatalog.FromNetlist(tb, lib);
                    setup   = tb.Tuning;
                    break;
                }
                default:
                    return (null, JsonRun.Fail(CliDiagnostics.ExplainNotApplicable("--tunables", DocumentKinds.Name(kind))));
            }
        }
        catch (Exception ex)
        {
            return (null, JsonRun.Fail(CliDiagnostics.ExplainUnreadable(path, ex.Message)));
        }

        var rows = catalog.Tunables.Select(t =>
        {
            var entry = setup?.Variables.FirstOrDefault(e => e.Key == t.Key);
            return new ExplainTunableJson(
                t.Key, t.Location, t.InstanceCount, KindWord(t.Kind), t.ValueText, t.Value, t.Unit, t.IsInteger,
                t.IsDefault, t.ReadOnlyReason, t.DisabledReason,
                entry?.Min ?? t.DefaultMin, entry?.Max ?? t.DefaultMax, entry is null && t.RangeGuessed,
                entry?.Tune ?? false, entry?.Opt ?? false,
                t.Part is null ? null : Design.Optimization.ComplexValue.Format(t.Whole, t.WholeUnit, t.Form),
                t.IsVendorPart ? null : ExplainStatistics.Stat(entry, t),
                t.VendorPart is { } vp
                    ? new ExplainPartJson(vp.PlacedText, vp.Library, vp.Selection, vp.Candidates.Count,
                                          vp.SpanText, vp.Series, vp.Summary)
                    : null);
        }).ToList();

        return (new ExplainTunablesJson(rows, catalog.UnresolvedKeys), 0);
    }

    public static void Print(ExplainTunablesJson report)
    {
        Console.WriteLine();
        Console.WriteLine($"Tunables ({report.Tunables.Count}):");
        if (report.Tunables.Count == 0)
            Console.WriteLine("  none — no value is a plain number or a complex literal");

        int keyWidth = Math.Max(8, report.Tunables.Select(t => t.Key.Length).DefaultIfEmpty(0).Max());
        int locWidth = Math.Max(4, report.Tunables.Select(t => t.Location.Length).DefaultIfEmpty(0).Max());
        foreach (var t in report.Tunables)
        {
            var flags = new List<string>();
            if (t.Tune)      flags.Add("tune");
            if (t.Opt)       flags.Add("opt");
            if (t.Integer)   flags.Add("integer");
            if (t.IsDefault) flags.Add("cell default");
            if (t.RangeGuessed) flags.Add("range guessed");
            if (t.Whole is { } w) flags.Add("part of " + w);
            if (t.Disabled is { } d) flags.Add(d);
            if (t.ReadOnly is { } r) flags.Add("read-only: " + r);

            // A vendor part reads as a part — its number, value and Description — then its candidates (R-vl4-6a).
            string line = t.Part is { } part
                ? $"  {t.Key.PadRight(keyWidth)}  {t.Location.PadRight(locWidth)}  {part.Placed}"
                : $"  {t.Key.PadRight(keyWidth)}  {t.Location.PadRight(locWidth)}  {t.Value,-14}  {t.Min} .. {t.Max}";
            if (flags.Count > 0) line += "  [" + string.Join("; ", flags) + "]";
            Console.WriteLine(line);
            if (t.Part is { } p)
                Console.WriteLine($"  {"".PadRight(keyWidth)}  {"".PadRight(locWidth)}  {p.Selection}: {p.Summary}");
            if (t.Stat is { } stat) Console.WriteLine($"  {"".PadRight(keyWidth)}  {"".PadRight(locWidth)}  {stat.Text}");
        }

        if (report.Unresolved.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Keys the tuning setup names that resolve to nothing (ignored):");
            foreach (var k in report.Unresolved) Console.WriteLine("  " + k);
        }
    }

    private static string KindWord(TunableKind k) => k switch
    {
        TunableKind.Variable      => "variable",
        TunableKind.CellParameter => "cellParameter",
        TunableKind.Part          => "part",
        _                         => "parameter",
    };

    private static string? PrimarySchematic(string cellDir)
    {
        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
        return primary.ResolvedName is { } file
            ? Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), file)
            : null;
    }
}
