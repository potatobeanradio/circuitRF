using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Cells;
using CircuitRF.Core.Netlist;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.VendorLibraries.ModelLibrary;

/// <summary>
/// What makes a model folder sound beyond being a sound cell folder (brief vendorlib-6 R-vl6-2b), and
/// what makes a library of them sound (R-vl6-2c).
///
/// <para><b>Here and not in <c>circuitrf check</c></b>, because <c>check</c> writes no rule of its own:
/// Validate in Manage Libraries (brief 9) and Export's self-check (brief 10) call these same functions,
/// so a model that passes headlessly passes when it is added. The cell-folder half of a model folder
/// is <c>check</c>'s existing cell walk and is not repeated here.</para>
///
/// <para>Diagnostics are RETURNED, never posted (<c>src/Design/CLAUDE.md</c>).</para>
/// </summary>
public static class ModelFolderCheck
{
    /// <summary>The findings for the model folder at <paramref name="modelDir"/>, every one naming the file it is about.</summary>
    public static IReadOnlyList<Diagnostic> Validate(string modelDir)
    {
        string dir      = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelDir));
        string folder   = Path.GetFileName(dir);
        string partPath = Path.Combine(dir, ModelFolder.PartFileName);
        var found = new List<Diagnostic>();

        var (part, refusal) = ModelPartManifest.Read(partPath);
        if (part is null)
        {
            found.Add(Diagnostic.Create("check.model.part-unreadable", DiagnosticSeverity.Error,
                "{path}: {reason}", ("path", partPath), ("reason", refusal)));
            return found;     // without the manifest there is no Part, Cell or Pins to hold anything to
        }

        if (!string.Equals(part.Part, folder, StringComparison.Ordinal))
            found.Add(Diagnostic.Create("check.model.part-not-folder", DiagnosticSeverity.Error,
                "{path}: \"Part\" is \"{part}\" but the model folder is named \"{folder}\"; a model folder is named after its part number.",
                ("path", partPath), ("part", part.Part), ("folder", folder)));

        // The netlist is named after the FOLDER when the two disagree — the row above already said so,
        // and naming it after Part would report a second, derived error for the same mistake.
        string cnlPath = ModelFolder.NetlistPath(dir, folder);
        CheckNetlist(cnlPath, dir, part, found);

        if (part.PublishedHash.Length > 0)
        {
            string now;
            try { now = ModelPartHash.Compute(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { now = ""; }
            if (!string.Equals(now, part.PublishedHash, StringComparison.OrdinalIgnoreCase))
                found.Add(Diagnostic.Create("check.model.edited-since-exported", DiagnosticSeverity.Warning,
                    "{path}: edited since exported — the folder no longer matches its \"PublishedHash\".",
                    ("path", partPath), ("published", part.PublishedHash), ("now", now)));
        }

        return found;
    }

    private static void CheckNetlist(string cnlPath, string dir, ModelPartManifest part, List<Diagnostic> found)
    {
        if (!File.Exists(cnlPath))
        {
            found.Add(Diagnostic.Create("check.model.netlist-missing", DiagnosticSeverity.Error,
                "{path}: the model's netlist is missing; a model runs from {name}.",
                ("path", cnlPath), ("name", Path.GetFileName(cnlPath))));
            return;
        }

        string text;
        try { text = File.ReadAllText(cnlPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            found.Add(Diagnostic.Create("check.model.netlist-unreadable", DiagnosticSeverity.Error,
                "{path}: could not be read ({message}).", ("path", cnlPath), ("message", ex.Message)));
            return;
        }

        foreach (var (line, statement) in TopLevelStatements(text))
            found.Add(Diagnostic.Create("check.model.netlist-top-level", DiagnosticSeverity.Error,
                "{path}: line {line} is outside every define block ('{statement}'); a model's netlist holds define … end blocks and comments only.",
                ("path", cnlPath), ("line", line), ("statement", statement.Length > 60 ? statement[..60] + "…" : statement)));

        Library lib;
        TestBench tb;
        // No source directory: the reader would rebase every File= to an absolute path, and the rule
        // below is about what the model WROTE.
        try { (lib, tb) = new CnlReader().Read(text, "model", sourceDirectory: null); }
        catch (Exception ex)
        {
            found.Add(Diagnostic.Create("check.model.netlist-unreadable", DiagnosticSeverity.Error,
                "{path}: could not be read ({message}).", ("path", cnlPath), ("message", ex.Message)));
            return;
        }
        foreach (string w in tb.ReadWarnings)
            found.Add(Diagnostic.Create("check.model.netlist-warning", DiagnosticSeverity.Warning,
                "{path}: {message}", ("path", cnlPath), ("message", w)));

        CheckTopCell(cnlPath, lib, part, found);
        CheckAgainstSchematic(cnlPath, text, dir, part, found);

        var compiled = new List<string>();
        foreach (var cell in lib.Cells)
            foreach (var inst in cell.Instances)
                foreach (var ov in inst.Overrides)
                foreach (string value in FileValues(inst.Reference, ov))
                {
                    if (LeavesFolder(dir, value))
                        found.Add(Diagnostic.Create("check.model.reference-leaves-folder", DiagnosticSeverity.Error,
                            "{path}: {instance} in {cell} names \"{file}\", which is not inside the model folder; every file a model uses travels with it.",
                            ("path", cnlPath), ("cell", cell.Name), ("instance", inst.InstanceName), ("file", value)));
                    else if (OsdiBuilds.IsCompiled(value))
                    {
                        string abs = Path.GetFullPath(Path.Combine(dir, value.Replace('\\', '/')));
                        if (!compiled.Contains(abs, StringComparer.Ordinal)) compiled.Add(abs);
                    }
                }
        CheckBuilds(dir, part, compiled, found);
    }

    /// <summary>
    /// Brief vendorlib-15 R-vl15-1b/1c: a model's compiled devices against its platform folders and its
    /// <c>Builds</c> list. A compiled device with no source of the same name anywhere in the folder is
    /// compiled-only — recognised from the files, never declared — and for one of those <c>Builds</c> must say
    /// exactly which <c>models/&lt;rid&gt;/</c> folders hold a build, and each build must be for the platform its
    /// folder names.
    /// </summary>
    private static void CheckBuilds(string dir, ModelPartManifest part, List<string> compiled, List<Diagnostic> found)
    {
        string partPath = Path.Combine(dir, ModelFolder.PartFileName);
        var listed = part.Builds ?? [];
        foreach (string rid in listed.Where(r => !OsdiBuilds.IsKnownRid(r)).Distinct(StringComparer.Ordinal))
            found.Add(Diagnostic.Create("check.model.build-unknown-platform", DiagnosticSeverity.Error,
                "{path}: \"Builds\" lists \"{rid}\", which is not a platform circuitRF runs on; it is one of {rids}.",
                ("path", partPath), ("rid", rid), ("rids", string.Join(", ", OsdiBuilds.Rids))));
        if (compiled.Count == 0)
        {
            if (listed.Count > 0)
                found.Add(Diagnostic.Create("check.model.builds-without-compiled", DiagnosticSeverity.Error,
                    "{path}: \"Builds\" lists {rids}, and the netlist places no compiled model (.osdi) to have builds of.",
                    ("path", partPath), ("rids", string.Join(", ", listed))));
            return;
        }

        var sources = SourceStems(dir);
        foreach (string abs in compiled)
        {
            string rel = Path.GetRelativePath(dir, abs).Replace('\\', '/');
            var present = OsdiBuilds.BuildsBeside(abs);
            bool compiledOnly = !sources.Contains(Path.GetFileNameWithoutExtension(abs));

            foreach (string rid in present)
            {
                string build = OsdiBuilds.PlatformPath(abs, rid);
                var says = OsdiBuilds.PlatformsOf(build);
                if (says.Count > 0 && !says.Contains(rid))
                    found.Add(Diagnostic.Create("check.model.build-wrong-platform", DiagnosticSeverity.Error,
                        "{path}: {build} is in the {rid} folder and is a build for {says}.",
                        ("path", build), ("build", Path.GetRelativePath(dir, build).Replace('\\', '/')), ("rid", rid),
                        ("says", string.Join(", ", says))));
            }
            if (!compiledOnly) continue;

            if (listed.Count == 0)
            {
                found.Add(Diagnostic.Create("check.model.builds-unlisted", DiagnosticSeverity.Warning,
                    "{path}: {file} is compiled with no source, and \"Builds\" lists no platforms, so a palette cannot say where it runs" +
                    (present.Count > 0 ? "; the folder holds builds for {present}." : "."),
                    ("path", partPath), ("file", rel), ("present", string.Join(", ", present))));
                continue;
            }
            foreach (string rid in listed.Where(OsdiBuilds.IsKnownRid).Where(r => !present.Contains(r)))
                found.Add(Diagnostic.Create("check.model.build-missing", DiagnosticSeverity.Error,
                    "{path}: \"Builds\" lists {rid}, and {build} is not there.",
                    ("path", partPath), ("rid", rid),
                    ("build", Path.GetRelativePath(dir, OsdiBuilds.PlatformPath(abs, rid)).Replace('\\', '/'))));
            foreach (string rid in present.Where(r => !listed.Contains(r)))
                found.Add(Diagnostic.Create("check.model.build-not-listed", DiagnosticSeverity.Warning,
                    "{path}: the folder holds a {rid} build of {file}, and \"Builds\" does not list {rid}.",
                    ("path", partPath), ("rid", rid), ("file", rel)));
            if (!present.Contains(OsdiBuilds.CurrentRid) && !File.Exists(abs))
                found.Add(Diagnostic.Create("check.model.no-build-here", DiagnosticSeverity.Info,
                    "{path}: there is no build of {file} for {rid}, this machine's platform; the publisher ships it for {present}.",
                    ("path", partPath), ("file", rel), ("rid", OsdiBuilds.CurrentRid),
                    ("present", present.Count == 0 ? "no platform" : string.Join(", ", present))));
        }
    }

    /// <summary>The file stems of every Verilog-A source anywhere in the model folder.</summary>
    private static HashSet<string> SourceStems(string dir)
    {
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                if (VerilogASourceCompiler.IsSourceFile(f)) stems.Add(Path.GetFileNameWithoutExtension(f));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return stems;
    }

    /// <summary>
    /// D1's gate (brief vendorlib-9 R-vl9-4a): when the model ships its schematic, the <c>.cnl</c> is exactly what
    /// that schematic extracts to (<see cref="ModelNetlist.FromCell"/>), judged by meaning rather than layout. A
    /// model written by hand or by script with no schematic has nothing to be held to, and is not.
    /// </summary>
    private static void CheckAgainstSchematic(string cnlPath, string text, string dir, ModelPartManifest part, List<Diagnostic> found)
    {
        PrimaryResolution primary;
        try { primary = CellFolder.ResolvePrimary(dir, ViewType.Schematic); }
        catch (Exception) { return; }
        if (primary.State == PrimaryState.NoView) return;

        string? extracted = ModelNetlist.FromCell(dir, out string? why);
        bool? same = extracted is null ? null : ModelNetlist.SameDefinitions(text, extracted);
        // Brief vendorlib-12 R-vl12-2e: what Refresh refuses on is a mismatch too — an extraction drops a kit part it
        // cannot resolve, so the two would otherwise compare equal while the schematic holds a part the netlist lacks.
        string? refusal = same == true ? ModelLibraryExport.SchematicRefusal(dir) : null;
        if (same == true && refusal is null) return;
        if (refusal is not null)
        {
            found.Add(Diagnostic.Create("check.model.netlist-not-schematic", DiagnosticSeverity.Error,
                "{part}: its schematic cannot become its netlist ({reason}); the part runs its previous netlist.",
                ("path", cnlPath), ("part", part.Part), ("reason", refusal)));
            return;
        }
        found.Add(Diagnostic.Create("check.model.netlist-not-schematic", DiagnosticSeverity.Error,
            "{part}: its netlist is not what its schematic extracts to; the part runs its netlist. Export it again, or Edit Vendor Model and save.",
            ("path", cnlPath), ("part", part.Part), ("reason", why ?? "")));
    }

    private static void CheckTopCell(string cnlPath, Library lib, ModelPartManifest part, List<Diagnostic> found)
    {
        var placed = lib.Cells.SelectMany(c => c.Instances).Select(i => i.Reference).ToHashSet(StringComparer.Ordinal);
        var tops   = lib.Cells.Where(c => !placed.Contains(c.Name)).ToList();
        var cell   = lib.Cells.FirstOrDefault(c => c.Name == part.Cell);

        if (cell is null || tops.Count != 1 || tops[0] != cell)
        {
            string topNames = tops.Count == 0 ? "none" : string.Join(", ", tops.Select(c => c.Name));
            found.Add(Diagnostic.Create("check.model.top-cell", DiagnosticSeverity.Error,
                "{path}: part.cpart says the top define is \"{cell}\", and the netlist's top define is {tops}; a model has one top define, named by \"Cell\".",
                ("path", cnlPath), ("cell", part.Cell), ("tops", tops.Count == 1 ? $"\"{topNames}\"" : $"not one ({topNames})")));
            if (cell is null) return;
        }

        if (!cell.Ports.SequenceEqual(part.Pins, StringComparer.Ordinal))
            found.Add(Diagnostic.Create("check.model.pins", DiagnosticSeverity.Error,
                "{path}: define {cell} has pins ({netlist}) and part.cpart lists ({manifest}); they are the same pins in the same order.",
                ("path", cnlPath), ("cell", cell.Name),
                ("netlist", string.Join(" ", cell.Ports)), ("manifest", string.Join(" ", part.Pins))));

        // D7: the index's parameter list is the .cnl's, written from one source. The manifest's
        // defaults are read back through the SAME `parameters` reader, so two spellings the reader
        // takes as one value agree (`3 V` and `3  V`; a unit written glued or apart is the reader's call).
        var declared = cell.Parameters.Where(p => !p.Hidden)
                           .Select(p => (p.Name, Default: Spell(p.DefaultExpression, p.Unit))).ToList();
        var listed   = AsDeclared(part.Parameters);
        if (listed is null || !declared.SequenceEqual(listed))
            found.Add(Diagnostic.Create("check.model.parameters", DiagnosticSeverity.Error,
                "{path}: define {cell} declares parameters ({netlist}) and part.cpart lists ({manifest}); they are the same names and defaults in the same order.",
                ("path", cnlPath), ("cell", cell.Name),
                ("netlist", string.Join(", ", declared.Select(p => $"{p.Name}={p.Default}"))),
                ("manifest", string.Join(", ", part.Parameters.Select(p => $"{p.Name}={p.Default}")))));

        static List<(string Name, string Default)>? AsDeclared(List<ModelPartParameter> parameters)
        {
            var text = new System.Text.StringBuilder("define __manifest ()\n");
            foreach (var p in parameters) text.Append("  parameters ").Append(p.Name).Append('=').Append(p.Default).Append('\n');
            text.Append("end\n");
            try
            {
                var (lib, _) = new CnlReader().Read(text.ToString(), "manifest", sourceDirectory: null);
                return [.. lib.Cells.Single().Parameters.Select(p => (p.Name, Spell(p.DefaultExpression, p.Unit)))];
            }
            catch (Exception) { return null; }
        }

        static string Spell(string expr, string? unit)
            => string.Join(' ', ((expr ?? "") + (string.IsNullOrEmpty(unit) ? "" : " " + unit))
                                  .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Whether a file-valued parameter reaches outside <paramref name="modelDir"/>: an absolute path, a
    /// reference into another library or kit (anything with a scheme), or a relative path that climbs out.
    /// </summary>
    /// <summary>
    /// Each file an override names: a <c>File</c> on any part, and every parameter <see cref="FileReferences.KindOf"/>
    /// says the elaborator resolves as a file (a wBond's <c>MaterialLibrary</c>, a modulated source carrier's
    /// <c>File=</c> field) — the same list the run rebases against the model folder, so nothing it reads escapes this check.
    /// </summary>
    private static IEnumerable<string> FileValues(string reference, CircuitRF.Core.Design.ParameterAssignment ov)
    {
        var kind = FileReferences.KindOf(reference, ov.Name);
        if (kind == FileValueKind.None && !ov.Name.Equals("File", StringComparison.OrdinalIgnoreCase)) yield break;
        string value = (ov.Expression ?? "").Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1].Trim();
        if (value.Length == 0) yield break;
        if (kind != FileValueKind.CarrierSpec) { yield return value; yield break; }
        var named = new List<string>();
        global::RfCore.Signals.SignalSpec.MapFilePath(value, f => { if (f.Trim().Length > 0) named.Add(f.Trim()); return f; });
        foreach (string f in named) yield return f;
    }

    public static bool LeavesFolder(string modelDir, string value)
    {
        if (value.Contains("://", StringComparison.Ordinal)) return true;
        if (Path.IsPathRooted(value)) return true;
        // A drive or UNC path written on Windows is not rooted to macOS or Linux, which would combine it under the folder.
        if (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':') return true;
        if (value.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelDir));
        string full = Path.GetFullPath(Path.Combine(root, value.Replace('\\', '/')));
        return !full.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsLinux()
            ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every statement outside a <c>define … end</c> block, with its 1-based line number. Comments
    /// (<c>;</c>) and blank lines are not statements; a line continued with a trailing <c>\</c> belongs
    /// to the statement it continues.
    /// </summary>
    public static IEnumerable<(int Line, string Statement)> TopLevelStatements(string text)
    {
        bool inDefine = false, continued = false;
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].TrimEnd('\r').Trim();
            bool wasContinued = continued;
            continued = t.EndsWith('\\');
            if (wasContinued || t.Length == 0 || t.StartsWith(';')) continue;

            if (inDefine)
            {
                if (t.Equals("end", StringComparison.OrdinalIgnoreCase) || t.StartsWith("end ", StringComparison.OrdinalIgnoreCase))
                    inDefine = false;
                continue;
            }
            if (t.StartsWith("define ", StringComparison.Ordinal)) { inDefine = true; continue; }
            yield return (i + 1, t);
        }
    }
}

/// <summary>What a library of model folders must hold together (brief vendorlib-6 R-vl6-2c).</summary>
public static class ModelLibraryCheck
{
    /// <summary>
    /// Library-level findings for the folder at <paramref name="libraryDir"/>: one part number in two
    /// model folders. A part number is compared without regard to case, because the folders that hold
    /// two spellings of one are a single folder on a case-insensitive disk.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Validate(string libraryDir)
    {
        var found = new List<Diagnostic>();
        var byPart = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in ModelFolder.Under(libraryDir))
        {
            var (part, _) = ModelPartManifest.Read(Path.Combine(dir, ModelFolder.PartFileName));
            string number = part?.Part is { Length: > 0 } p ? p : Path.GetFileName(dir);
            if (!byPart.TryGetValue(number, out var list)) byPart[number] = list = [];
            list.Add(dir);
        }

        string root = Path.GetFullPath(libraryDir);
        foreach (var (number, dirs) in byPart.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (dirs.Count < 2) continue;
            found.Add(Diagnostic.Create("check.model.duplicate-part", DiagnosticSeverity.Error,
                "{path}: part number {part} is in {count} model folders ({folders}); a part number names one model.",
                ("path", libraryDir), ("part", number), ("count", dirs.Count),
                ("folders", string.Join(", ", dirs.Select(d => Path.GetRelativePath(root, d).Replace('\\', '/'))))));
        }
        return found;
    }
}
