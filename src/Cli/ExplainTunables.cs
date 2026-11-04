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
    public static (ExplainTunablesJson? Report, int Exit) Collect(string path, DocumentKind kind, IReadOnlyList<string>? snpEntries = null)
    {
        TunableCatalog catalog;
        Core.Design.TuningSetup? setup;
        IReadOnlyList<string>? threeDOnly = null;
        IReadOnlyList<string>? emComponents = null;
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
                                                      cws is null ? null : Path.GetDirectoryName(cws),
                                                      SchematicCircuit.ReferenceBaseOf(csch));
                    setup = model.Tuning;
                    threeDOnly = ThreeDOnly(csch, model);
                    emComponents = EmComponents(csch, model);
                    break;
                }
                case DocumentKind.Netlist:
                {
                    var (lib, tb) = CnlTechnologyBinding.ReadFile(path, out var parts);
                    catalog = TunableCatalog.FromNetlist(tb, lib, parts: parts);
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

        // EMB-5 R-emb5-9: an SnP block offers 2·N² offset rows (a 15-port board, 450), which the per-block line below
        // already summarises — so they are folded to it unless the block is named (or all). A result that grows with
        // its input says how much it left out and how to ask for it.
        bool all = snpEntries?.Any(n => n.Equals("all", StringComparison.OrdinalIgnoreCase)) ?? false;
        bool Listed(string instance) => all || (snpEntries?.Contains(instance, StringComparer.OrdinalIgnoreCase) ?? false);
        var folded = catalog.SnpInstances.Select(s => s.Instance).Where(i => !Listed(i)).ToHashSet(StringComparer.Ordinal);
        bool Fold(Tunable t) => t.Kind == TunableKind.SnpEntry && folded.Any(i => t.Key.StartsWith(i + ".", StringComparison.Ordinal));

        var rows = catalog.Tunables.Where(t => !Fold(t)).Select(t =>
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
                    : null,
                t.IsPartValue
                    ? new ExplainPartValueJson(t.PartEntry?.PartNumber ?? t.Owner, true,
                                               t.PartTolerance?.SpreadText, t.PartToleranceNote)
                    : null,
                t.SnpEntry is null ? null : t.Label);
        }).ToList();

        var snp = catalog.SnpInstances.Select(s => new ExplainSnpJson(
            s.Instance, s.Location, s.Ports, s.Tied is { } tied ? (tied ? "tied" : "untied") : null, s.TieReason, s.Entries, s.LoadError,
            folded.Contains(s.Instance) && s.Entries > 0 ? true : null)).ToList();
        return (new ExplainTunablesJson(rows, catalog.UnresolvedKeys, snp.Count > 0 ? snp : null) { ThreeDOnly = threeDOnly, EmComponents = emComponents }, 0);
    }

    /// <summary>brief-sdl-10 D1 — every placed cell that is an EM component, with the line its kind is stated by
    /// (<see cref="Design.Layout.Em.ParametricEm.EmComponentKinds"/>, the same the instance's properties show). Null for none.</summary>
    private static IReadOnlyList<string>? EmComponents(string csch, Design.Schematic.SchematicEditModel model)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(csch))!;
        var lines = new List<string>();
        foreach (var c in model.Components.Where(c => c.CellRef is { Length: > 0 } && c.InstanceName.Length > 0))
        {
            if (Design.Schematic.PdkKitRegistry.IsKitRef(c.CellRef!)) continue;
            string? cellDir;
            try { cellDir = Design.Workspace.ExternalCellRef.ResolveCellDir(c.CellRef, dir); }
            catch (Exception) { continue; }
            if (cellDir is null || !Directory.Exists(cellDir)) continue;
            var kind = Design.Layout.Em.ParametricEm.EmComponentKinds.Of(cellDir);
            if (kind.Kind != Design.Layout.Em.ParametricEm.EmComponentKind.None) lines.Add($"{c.InstanceName}: {kind.Sentence}");
        }
        return lines.Count > 0 ? lines : null;
    }

    /// <summary>
    /// brief-sdl-8 D3 — the VARs of the cell's 3D view that neither the schematic nor the cell's parameters define: they stay
    /// the view's own and are not tunable from the schematic. Null when the schematic is in no cell, the cell has no 3D view,
    /// or every VAR is linked.
    /// </summary>
    private static IReadOnlyList<string>? ThreeDOnly(string csch, Design.Schematic.SchematicEditModel model)
    {
        try
        {
            if (Design.Layout.Em.ParametricEm.EmCapability.CellDirOfSchematic(csch) is not { } cellDir) return null;
            var primary = CellFolder.ResolvePrimary(cellDir, ViewType.ThreeD);
            if (primary.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent) || primary.ResolvedName is null) return null;
            string c3d = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.ThreeD), primary.ResolvedName);
            var doc = Design.ThreeD.C3dPersistence.LoadFromFile(c3d);
            model.SchematicDirectory ??= Path.GetDirectoryName(Path.GetFullPath(csch));
            var names = Design.Schematic.CellVariables.Values(model).Keys.ToHashSet(StringComparer.Ordinal);
            foreach (var p in Design.ThreeD.C3dCell.OfFolder(cellDir).Parameters) names.Add(p.Name);
            var own = doc.Variables.Where(v => !names.Contains(v.Name)).Select(v =>
                $"VAR '{v.Name}' of the 3D view {Path.GetFileName(c3d)} is the view's own and is not tunable from the schematic; " +
                $"add a VAR named {v.Name} to the schematic to link it.").ToList();
            return own.Count > 0 ? own : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
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
            if (t.IsDefault) flags.Add(t.Kind == "snpEntry" ? "default 0" : "cell default");
            if (t.Label is { } label) flags.Add(label);
            // An SnP offset is one number at every frequency: toleranced and ranked, never tuned (brief-sens-2).
            if (t.Kind == "snpEntry") flags.Add("sensitivity and yield only");
            if (t.RangeGuessed) flags.Add("range guessed");
            if (t.Whole is { } w) flags.Add("part of " + w);
            if (t.Disabled is { } d) flags.Add(d);
            if (t.ReadOnly is { } r && t.PartValue is null) flags.Add("read-only: " + r);
            // A vendor part's value is drawn, never tuned, and tol=part is its own tolerance (R-vl5-5b).
            if (t.PartValue is { } pv)
            {
                flags.Add("statistical only");
                flags.Add(pv.Tolerance is { } tol ? $"tol=part is {tol}" : $"tol=part not known: {pv.ToleranceNote}");
            }

            // A vendor part reads as a part — its number, value and Description — then its candidates (R-vl4-6a).
            // A vendor part's value has no range: it is drawn, never tuned.
            string line = t.Part is { } part
                ? $"  {t.Key.PadRight(keyWidth)}  {t.Location.PadRight(locWidth)}  {part.Placed}"
                : t.PartValue is not null || t.Kind == "snpEntry"
                    ? $"  {t.Key.PadRight(keyWidth)}  {t.Location.PadRight(locWidth)}  {t.Value,-14}"
                    : $"  {t.Key.PadRight(keyWidth)}  {t.Location.PadRight(locWidth)}  {t.Value,-14}  {t.Min} .. {t.Max}";
            if (flags.Count > 0) line += "  [" + string.Join("; ", flags) + "]";
            Console.WriteLine(line);
            if (t.Part is { } p)
                Console.WriteLine($"  {"".PadRight(keyWidth)}  {"".PadRight(locWidth)}  {p.Selection}: {p.Summary}");
            if (t.Stat is { } stat) Console.WriteLine($"  {"".PadRight(keyWidth)}  {"".PadRight(locWidth)}  {stat.Text}");
        }

        // brief-sens-2 R-sens2-4: each SnP block's state — `SNP3: 4 ports, tied (auto: …), 20 entries`.
        if (report.Snp is { Count: > 0 } blocks)
        {
            Console.WriteLine();
            Console.WriteLine("SnP blocks (each S-parameter a dB and a phase offset):");
            foreach (var b in blocks)
                Console.WriteLine(b.LoadError is { } why
                    ? $"  {b.Instance}: not offered — {why}"
                    : $"  {b.Instance}: {b.Ports} ports, {b.Tie} ({b.Reason}), {b.Entries} entries"
                      + (b.Folded == true ? $" (folded: --snp-entries {b.Instance} lists them)" : ""));
        }

        if (report.EmComponents is { Count: > 0 } em)
        {
            Console.WriteLine();
            Console.WriteLine("EM components placed here:");
            foreach (var line in em) Console.WriteLine("  " + line);
        }
        if (report.ThreeDOnly is { Count: > 0 } own)
        {
            Console.WriteLine();
            Console.WriteLine("3D view VARs the schematic does not define (not tunable here):");
            foreach (var line in own) Console.WriteLine("  " + line);
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
        TunableKind.PartValue     => "partValue",
        TunableKind.SnpEntry      => "snpEntry",
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
