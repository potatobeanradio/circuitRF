using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;

namespace CircuitRF.Design.VendorLibraries.ModelLibrary;

/// <summary>What <see cref="ModelLibraryExport.Refresh"/> did.</summary>
public sealed record RefreshResult
{
    /// <summary>Why the model was not refreshed; null when it was. The edited schematic stays saved either way.</summary>
    public string? Refusal { get; init; }
    /// <summary>The one Messages line: what changed, or <c>&lt;part&gt; still runs its previous netlist: …</c>.</summary>
    public string Message { get; init; } = "";
    /// <summary>The model's own documents Refresh rewrote (a newly referenced file re-pointed, a converted part), absolute.
    /// An editor holding one of them open has to read it again.</summary>
    public IReadOnlyList<string> Rewritten { get; init; } = [];
    /// <summary>Every file written, absolute, in ordinal order.</summary>
    public IReadOnlyList<string> Written { get; init; } = [];
    /// <summary>The <c>.cnl</c> now holds something other than it did.</summary>
    public bool NetlistChanged { get; init; }
    public bool Succeeded => Refusal is null;
}

// Brief vendorlib-12 R-vl12-2d: a model folder's schematic was edited in place — by Edit Vendor Model's save, or by an
// agent writing the .csch — and the folder is brought back into step with it. The same walk, copier, converter and
// rewrites Export uses, applied to the folder itself rather than to a copy of a workspace cell.
public static partial class ModelLibraryExport
{
    /// <summary>
    /// Refresh (R-vl12-2d): Export minus the dialog, run on a model folder whose schematic was edited where it lives.
    /// Any file the model's documents newly reference from outside the folder is copied into <c>models/</c> and the
    /// reference rewritten model-relative; a newly placed cell from outside comes into <c>cells/</c>; a newly added
    /// technology-bound part is converted on the plan <c>part.cpart</c>'s <c>Converted</c> entries record (the default
    /// plan when there are none); the <c>.cnl</c> is extracted again; and <c>Pins</c> and <c>Parameters</c> are
    /// updated. <c>PublishedHash</c> and <c>Revision</c> are never touched, so check reports the folder as edited
    /// since it was exported.
    ///
    /// <para><b>A refusal writes nothing</b> (R-vl12-2e): the caller has already saved the schematic, the <c>.cnl</c>
    /// is left as it was, and the message says the part still runs its previous netlist and why.</para>
    /// </summary>
    public static RefreshResult Refresh(string modelDir, RunControl? control = null)
    {
        string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelDir));
        string partPath = Path.Combine(dir, ModelFolder.PartFileName);
        var (manifest, manifestRefusal) = File.Exists(partPath) ? ModelPartManifest.Read(partPath) : (null, null);
        string part = manifest?.Part ?? Path.GetFileName(dir);
        RefreshResult Refused(string why) => new() { Refusal = why, Message = $"{part} still runs its previous netlist: {why}" };

        if (manifest is null)
            return Refused(manifestRefusal ?? $"'{dir}' is not a model folder; it holds no {ModelFolder.PartFileName}.");
        InvalidateResolution();
        if (PrimaryPath(dir, ViewType.Schematic) is not { } topSch)
            return Refused($"the model folder has no primary schematic to extract.");

        // ── What changed, and every refusal, before anything is written ──
        var (walk, refusals) = Survey(dir, topSch);

        var newFiles    = walk.Copies.Where(c => c.Kind == ExportCopyKind.File && !IsUnder(c.Source, dir)).ToList();
        var newSubCells = walk.Copies.Where(c => c.Kind == ExportCopyKind.SubCell && !IsSameOrUnder(c.Source, dir)).ToList();

        IReadOnlyList<FrequencySpec> sweep = DefaultSweep;
        double[] freqs = [];
        if (walk.Conversions.Count > 0)
        {
            // An encapsulated model records the plan its block was sampled over (brief vendorlib-19 R-vl19-2b).
            if ((manifest.Converted.FirstOrDefault(c => c.Frequency.Trim().Length > 0)?.Frequency ?? manifest.Plan) is { Length: > 0 } recorded)
            {
                if (ParsePlan(recorded) is { } plan) sweep = plan;
                else refusals.Add($"the frequency plan part.cpart records for its converted parts ('{recorded}') cannot be read.");
            }
            freqs = PlanFrequencies(sweep, out string? planRefusal) ?? [];
            if (planRefusal is not null) refusals.Add(planRefusal);
        }

        if (refusals.Count > 0) return Refused(string.Join(" ", refusals.Distinct(StringComparer.Ordinal)));

        // ── The writes ──
        var written = new List<string>();
        var rewritten = new List<string>();
        var plan0 = new ExportPlan
        {
            CellDir = dir, PartNumber = part, ModelDir = dir, Pins = manifest.Pins,
            Conversions = walk.Conversions, Frequencies = freqs,
        };
        var options = new ExportOptions { LibraryDir = Path.GetDirectoryName(dir)!, Sweep = sweep };
        var files = new FileCopier(dir);
        var newMap = new Dictionary<string, string>(DocumentFileRefs.PathComparer);
        var restore = new List<(string Path, string Text)>();

        // R-vl12-2e: a refusal reached after the first write takes every write back — the SnPs Convert wrote, the cells
        // copied in, the schematics rewritten (to the bytes the user's save left) — so the folder is as the save left it.
        RefreshResult Undo(string why)
        {
            foreach (string f in files.Written) try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            foreach (string d in newMap.Values) try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            foreach (var (path, text) in restore) try { AtomicFile.WriteAllText(path, text); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            InvalidateResolution();
            return Refused(why);
        }

        try
        {
            Converted converted;
            try { converted = Convert(plan0, options, files, control); }
            catch (ConversionRefused refused) { return Undo(refused.Message); }
            catch (OperationCanceledException) { return Undo("the refresh was cancelled."); }

            // A cell newly placed from outside the model comes into cells/, under its leaf name made unique.
            foreach (var c in newSubCells)
            {
                string leaf = Path.GetFileName(c.Source), name = leaf;
                for (int n = 2; Directory.Exists(Path.Combine(dir, ModelFolder.CellsFolder, name)) || newMap.Values.Any(v => Path.GetFileName(v) == name); n++)
                    name = $"{leaf}_{n}";
                newMap[c.Source] = Path.Combine(dir, ModelFolder.CellsFolder, name);
            }
            var warnings = new List<string>();
            foreach (var (src, dst) in newMap)
                CopyCell(src, dst, Path.GetFileName(dst), top: false, plan0, options, newMap, files, converted, warnings);

            // The model's own documents: only what is new is rewritten, so an edit that brought nothing in leaves
            // the saved schematic's bytes exactly as the editor wrote them.
            foreach (var (sch, cellDir) in ModelSchematics(dir))
            {
                string before = DocumentFileRefs.ReadText(sch);
                string text = before;
                string schDir = Path.GetDirectoryName(sch)!;
                if (newFiles.Count > 0 || newMap.Count > 0)
                    text = DocumentFileRefs.Rewrite(sch, OwningRoot.Of(schDir), (abs, refBase) =>
                    {
                        if (IsUnder(abs, dir)) return null;
                        string target = MappedIntoCell(abs, newMap) ?? files.Copy(abs);
                        return refBase == RefBase.Document
                            ? Path.GetRelativePath(schDir, target).Replace('\\', '/')
                            : Path.GetRelativePath(dir, target).Replace('\\', '/');
                    }) ?? before;

                string key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cellDir));
                bool converts = converted.Files.Keys.Any(k => DocumentFileRefs.PathComparer.Equals(k.Cell, key));
                if (converts || newMap.Count > 0)
                {
                    string name = DocumentFileRefs.PathComparer.Equals(key, dir) ? part : Path.GetFileName(cellDir);
                    text = RewriteSchematic(text, sch, sch, name, files, newMap, options, cellDir, converted, NoFets);
                }
                if (!string.Equals(text, before, StringComparison.Ordinal))
                {
                    restore.Add((sch, before));
                    AtomicFile.WriteAllText(sch, text);
                    rewritten.Add(sch);
                }
            }
            written.AddRange(files.Written);
            foreach (var dst in newMap.Values)
                written.AddRange(Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories).Select(Path.GetFullPath));
            written.AddRange(rewritten);

            // The netlist, from the folder as it now stands.
            InvalidateResolution();
            string? cnl = ModelNetlist.FromCell(dir, out string? why);
            if (cnl is null) return Undo(why ?? "the edited schematic could not be extracted.");
            if (ModelFolderCheck.TopLevelStatements(cnl).FirstOrDefault() is { Statement: { } stray })
                return Undo($"the extracted netlist holds '{stray}' outside every define block.");
            var (lib, _) = new CnlReader().Read(cnl, "model", sourceDirectory: null);
            var top = lib.Find(manifest.Cell) ?? lib.Find(part);
            if (top is null)
                return Undo($"the extracted netlist has no define named {manifest.Cell}.");

            string cnlPath = ModelNetlist.PathOf(dir);
            string? oldCnl = File.Exists(cnlPath) ? File.ReadAllText(cnlPath) : null;
            bool netlistChanged = !string.Equals(oldCnl, cnl, StringComparison.Ordinal);
            if (netlistChanged) { AtomicFile.WriteAllText(cnlPath, cnl); written.Add(cnlPath); }

            // Pins and Parameters from the netlist; Converted gains the new conversions. PublishedHash and Revision stay.
            var updated = manifest with
            {
                Pins = [.. top.Ports],
                Parameters = [.. top.Parameters.Where(p => !p.Hidden).Select(p => new ModelPartParameter
                {
                    Name = p.Name, Default = Spell(p.DefaultExpression, p.Unit),
                })],
                Converted = [.. manifest.Converted, .. converted.Records],
            };
            if (!ManifestEquals(manifest, updated)) { ModelPartManifest.Write(partPath, updated); written.Add(partPath); }
            InvalidateResolution();

            var said = new List<string>();
            if (newFiles.Count > 0) said.Add($"{newFiles.Count} file{(newFiles.Count == 1 ? "" : "s")} copied into models/");
            if (newMap.Count > 0) said.Add($"{newMap.Count} cell{(newMap.Count == 1 ? "" : "s")} copied into cells/");
            if (converted.Records.Count > 0)
                said.Add($"{converted.Records.Count} part{(converted.Records.Count == 1 ? "" : "s")} converted to S-parameters, {freqs.Length} points");
            string what = netlistChanged ? "netlist extracted again" : "netlist unchanged";
            return new RefreshResult
            {
                Message = $"Refreshed {part}: {what}" + (said.Count > 0 ? "; " + string.Join("; ", said) : "") + ".",
                Rewritten = rewritten, Written = Sorted(written), NetlistChanged = netlistChanged,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Undo($"the model folder could not be written ({ex.Message}).");
        }
    }

    /// <summary>The walk over a model folder's schematics, and every reason its schematic cannot become its netlist.</summary>
    private static (Walk Walk, List<string> Refusals) Survey(string dir, string topSch)
    {
        var refusals = new List<string>();
        var walk = new Walk();
        walk.Visit(topSch, dir, top: true, refusals);
        foreach (var v in walk.Copies.Where(c => c.Kind is ExportCopyKind.VendorSpicePart or ExportCopyKind.VendorModel))
            refusals.Add($"{v.Part} comes from the vendor library '{v.Library}'. A model carries its parts with it, and only "
                         + "Export to Library copies a vendor library's part in, with that library's terms; export the cell again to include it.");
        return (walk, refusals);
    }

    /// <summary>
    /// Why the model folder's schematic cannot be what it runs — a kit part, a file that does not resolve, a vendor part
    /// not copied in — or null when nothing stands in the way. Refresh refuses on exactly these (R-vl12-2e), and
    /// <see cref="ModelFolderCheck"/> reports them as the <c>.cnl</c>/<c>.csch</c> mismatch they are, since an
    /// extraction drops a kit part it cannot resolve and would otherwise compare equal.
    /// </summary>
    public static string? SchematicRefusal(string modelDir)
    {
        string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelDir));
        if (PrimaryPath(dir, ViewType.Schematic) is not { } topSch) return null;
        var (_, refusals) = Survey(dir, topSch);
        return refusals.Count == 0 ? null : string.Join(" ", refusals.Distinct(StringComparer.Ordinal));
    }

    /// <summary>The model's own schematics: the top cell's primary and each cell under <c>cells/</c>'s, with its cell folder.</summary>
    private static IEnumerable<(string Schematic, string CellDir)> ModelSchematics(string modelDir)
    {
        if (PrimaryPath(modelDir, ViewType.Schematic) is { } top) yield return (top, modelDir);
        string cells = Path.Combine(modelDir, ModelFolder.CellsFolder);
        if (!Directory.Exists(cells)) yield break;
        foreach (string sub in Directory.GetDirectories(cells).Order(StringComparer.Ordinal))
            if (PrimaryPath(sub, ViewType.Schematic) is { } sch) yield return (sch, sub);
    }

    private static List<string> Sorted(List<string> files)
        => [.. files.Select(Path.GetFullPath).Distinct(DocumentFileRefs.PathComparer).Order(StringComparer.Ordinal)];

    private static bool ManifestEquals(ModelPartManifest a, ModelPartManifest b)
        => System.Text.Json.JsonSerializer.Serialize(a) == System.Text.Json.JsonSerializer.Serialize(b);

    /// <summary>
    /// A frequency plan as <see cref="DescribePlan"/> wrote it into a <c>Converted</c> entry — segments separated by
    /// <c>;</c>, each <c>start=… &lt;unit&gt; stop=… &lt;unit&gt; npts=…</c> or <c>step=… &lt;unit&gt;</c>, with
    /// <c>log</c> optional and the closing "DC always included" — read back. Null when it does not read.
    /// </summary>
    public static IReadOnlyList<FrequencySpec>? ParsePlan(string text)
    {
        var plan = new List<FrequencySpec>();
        foreach (string raw in text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith("DC ", StringComparison.OrdinalIgnoreCase)) continue;
            var words = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var values = new Dictionary<string, (string Value, string Unit)>(StringComparer.OrdinalIgnoreCase);
            bool log = false;
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];
                if (w.Equals("log", StringComparison.OrdinalIgnoreCase)) { log = true; continue; }
                int eq = w.IndexOf('=');
                if (eq <= 0) return null;
                // A unit, when written, is the next word: one with no '=' that is not the log flag.
                bool unitFollows = i + 1 < words.Length && !words[i + 1].Contains('=')
                                   && !words[i + 1].Equals("log", StringComparison.OrdinalIgnoreCase);
                values[w[..eq]] = (w[(eq + 1)..], unitFollows ? words[++i] : "");
            }
            if (!values.TryGetValue("start", out var start) || !values.TryGetValue("stop", out var stop)) return null;
            var kind = log ? SweepKind.Log : SweepKind.Linear;
            string Unit(string u) => u.Length == 0 ? "Hz" : u;
            try
            {
                if (values.TryGetValue("npts", out var n) && int.TryParse(n.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                    plan.Add(new FrequencySpec(start.Value, stop.Value, count, kind, Unit(start.Unit), Unit(stop.Unit)));
                else if (values.TryGetValue("step", out var step))
                    plan.Add(new FrequencySpec(start.Value, stop.Value, step.Value, kind, Unit(start.Unit), Unit(stop.Unit), Unit(step.Unit)));
                else return null;
            }
            catch (ArgumentException) { return null; }
        }
        return plan.Count == 0 ? null : plan;
    }
}
