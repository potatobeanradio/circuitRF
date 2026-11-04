// Export to Library's Compile netlist (brief vendorlib-16): the cell turned into one compiled Verilog-A module, published
// in place of the cell. OFF unless asked for — the dialog's checkbox starts unticked and headless it is
// --compile-netlist. The partition, the module and the proof live in Protect/; this is where Export calls them.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.VendorLibraries.ModelLibrary.Protect;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;

namespace CircuitRF.Design.VendorLibraries.ModelLibrary;

/// <summary>One platform's row of the Builds table (brief vendorlib-16 R-vl16-6a).</summary>
/// <param name="State">One of <see cref="ExportBuild.Checked"/>, <see cref="ExportBuild.NotRun"/>, <see cref="ExportBuild.NotBuilt"/>.</param>
/// <param name="Reason">For a platform not built, the compiler's own first error line.</param>
/// <param name="Fix">For a platform not built, what would build it — or that nothing known would.</param>
public sealed record ExportBuild(string Rid, string State, string Reason = "", string Fix = "")
{
    public const string Checked = "built and checked";
    public const string NotRun = "built, not run on this machine";
    public const string NotBuilt = "not built";

    public string Describe() => State == NotBuilt ? $"{Rid}: {State}. {Reason} {Fix}".TrimEnd() : $"{Rid}: {State}.";
}

public static partial class ModelLibraryExport
{
    /// <summary>The compiler's target triple for each platform circuitRF runs on; null where it has none.</summary>
    public static readonly IReadOnlyDictionary<string, string?> TargetTriples = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["win-x64"] = "x86_64-pc-windows", ["win-arm64"] = "aarch64-pc-windows", ["win-x86"] = null,
        ["osx-arm64"] = "aarch64-apple-darwin", ["osx-x64"] = "x86_64-apple-darwin",
        ["linux-x64"] = "x86_64-unknown-linux", ["linux-arm64"] = "aarch64-unknown-linux",
    };

    /// <summary>The folder a protected part's generated source is written to, under the source workspace:
    /// <c>protected-source/&lt;Part&gt;/&lt;Part&gt;.va</c>.</summary>
    public const string ProtectedSourceFolder = "protected-source";

    /// <summary>The dialog's last line (R-vl16-2c).</summary>
    public const string SourceKeptLine = "The model's source is in protected-source/. It is not in the library. Keep it: every platform's build is made from it.";

    /// <summary><see cref="SourceKeptLine"/>, naming the folder when <c>--source-out</c> put the source elsewhere.</summary>
    public static string SourceKeptLineFor(string? sourcePath)
        => sourcePath is null || Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(sourcePath))) == ProtectedSourceFolder
            ? SourceKeptLine
            : $"The model's source is in {Path.GetDirectoryName(sourcePath)}. It is not in the library. Keep it: every platform's build is made from it.";

    /// <summary>The wrapper's instance of the compiled module.</summary>
    internal const string ProtectedInstance = "XPROT";

    /// <summary>What Compile netlist adds to the plan, or null when it is off.</summary>
    internal sealed record ProtectPlanned(ProtectPlan Partition, ProtectModule? Module, string? Hash, string SourcePath,
                                         IReadOnlyList<ProtectBias> Bias, bool Thermal);

    /// <summary>
    /// Plan's half: the partition and the module written from the SOURCE cell's netlist, so the dialog shows what is
    /// compiled and what stays outside before anything is written, and the hash that names the source is known. Every
    /// refusal joins the plan's.
    /// </summary>
    private static ProtectPlanned? PlanProtect(string dir, string part, ExportOptions options, string libraryDir, IReadOnlyList<string> pins,
                                               List<string> refusals)
    {
        if (!options.CompileNetlist) return null;
        if (CompilerMissingCached() is { } missing)
            refusals.Add($"Compile netlist is on, and no Verilog-A compiler is found: {missing}");

        string? text = ModelNetlist.FromCell(dir, out string? why);
        string sourcePath = SourcePathFor(dir, part, options);
        if (IsSameOrUnder(sourcePath, libraryDir))
            refusals.Add($"The generated source '{sourcePath}' would be inside the library; it must stay outside it, with the maker.");
        if (text is null) { refusals.Add(why ?? "The cell could not be extracted."); return null; }

        string? srcBase = SchematicCircuit.ReferenceBaseOfDirectory(Path.GetDirectoryName(PrimaryPath(dir, ViewType.Schematic) ?? dir));
        var partition = ProtectPartition.Partition(text, Path.GetFileName(dir), srcBase);
        if (partition.Inside.Count == 0 && partition.Refusals.Count == 0)
        {
            // A Verilog-A device is a model of its own and stays outside the generated module (brief vendorlib-16 D2), so
            // a cell that is one such device has nothing to fold in — but Compile Verilog-A models ships it without its
            // source, which is what the user is after. Say so, rather than leave them at a dead end.
            var models = partition.Outside.Where(e => e.Type.Equals("VerilogA", StringComparison.OrdinalIgnoreCase))
                                          .Select(e => e.Path).ToList();
            refusals.Add(models.Count == 0
                ? "Nothing in the cell can be compiled, so Compile netlist would protect nothing."
                : $"Nothing in the cell can be compiled into one module: {string.Join(", ", models)} "
                  + (models.Count == 1 ? "is a Verilog-A model, which stays a model of its own" : "are Verilog-A models, which stay models of their own")
                  + ". To ship it without its source, export with Compile Verilog-A models (--compile-models) instead.");
        }

        var bias = new List<ProtectBias>();
        foreach (string b in options.CheckBias)
        {
            if (ProtectBias.Parse(b, pins, out string? bad) is { } parsed) bias.Add(parsed);
            else refusals.Add($"The bias point '{b}' cannot be read: {bad}");
        }
        if (options.CheckBias.Count == 0) bias.AddRange(ProtectBias.Defaults(pins));

        ProtectModule? module = null;
        string? hash = null;
        try
        {
            module = NetlistVerilogA.Write(partition, ModuleNameFor(part));
            hash = "sha256:" + System.Convert.ToHexStringLower(SHA256.HashData(SourceBytes(module.Text)));
        }
        catch (ProtectRefusedException ex) { refusals.AddRange(ex.Refusals); }

        bool thermal = partition.Inside.Any(e => ProtectPartition.IsComposedFet(e.Type) && e.Nets.Count == 4 && pins.Contains(e.Nets[3]));
        return new ProtectPlanned(partition, module, hash, sourcePath, bias, thermal);
    }

    private static (string? Why, DateTime At) _compilerLookup = (null, DateTime.MinValue);
    private static readonly Lock _compilerGate = new();

    /// <summary><see cref="CompilerMissing"/>, remembered for ten seconds: the dialog plans on every keystroke, and finding
    /// a compiler launches it.</summary>
    private static string? CompilerMissingCached()
    {
        lock (_compilerGate)
        {
            if (DateTime.UtcNow - _compilerLookup.At < TimeSpan.FromSeconds(10)) return _compilerLookup.Why;
        }
        string? why = CompilerMissing();
        lock (_compilerGate) _compilerLookup = (why, DateTime.UtcNow);
        return why;
    }

    /// <summary>The module's name: the part number as a Verilog-A identifier.</summary>
    public static string ModuleNameFor(string part)
    {
        var sb = new StringBuilder();
        foreach (char c in part) sb.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        string name = sb.ToString();
        if (name.Length == 0 || !char.IsAsciiLetter(name[0])) name = "m_" + name;
        return CircuitRF.Design.Modeling.VerilogA.VaText.IsReserved(name) ? name + "_part" : name;
    }

    /// <summary>The source as bytes: UTF-8, no byte-order mark, LF line ends — what is written and what is hashed.</summary>
    private static byte[] SourceBytes(string text) => new UTF8Encoding(false).GetBytes(text.Replace("\r\n", "\n"));

    /// <summary>R-vl16-2c: <c>--source-out</c>, or <c>protected-source/&lt;Part&gt;/&lt;Part&gt;.va</c> beside the
    /// workspace the cell is in (the cell's parent folder when it is in none).</summary>
    public static string SourcePathFor(string cellDir, string part, ExportOptions options)
    {
        if (options.SourceOut is { Length: > 0 } given) return Path.GetFullPath(given);
        string root = WorkspaceRootFinder.WorkspaceDirOf(cellDir) ?? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(cellDir)) ?? cellDir;
        return Path.Combine(root, ProtectedSourceFolder, part, part + ".va");
    }

    /// <summary>The existing model a Compile-netlist export would add this platform's build to: same source hash, and
    /// this platform not yet built (R-vl16-2d). Null otherwise.</summary>
    private static ModelPartManifest? AddsPlatformTo(string modelDir, ProtectPlanned? protect)
    {
        if (protect?.Hash is null || !Directory.Exists(modelDir)) return null;
        var (existing, _) = ModelPartManifest.Read(Path.Combine(modelDir, ModelFolder.PartFileName));
        return existing is { CompiledFrom: { Length: > 0 } } ? existing : null;
    }

    /// <summary>
    /// Export's half, after the cell is copied into <paramref name="tmp"/> and its netlist extracted: the module compiled
    /// for every platform the compiler can reach, the source written to the maker, and the copy rewritten as a wrapper —
    /// the compiled module and the outside elements — with the original schematic, sub-cells and models removed.
    /// </summary>
    private static (IReadOnlyList<ExportBuild> Builds, string Neutral) BuildProtected(ProtectPlanned protect, string part, string tmp,
                                                                                      string copiedCnl, RunControl? control)
    {
        var module = protect.Module!;
        control?.BeginStage("Compiling the netlist");
        // The source goes to the maker first, so it is there to inspect whatever happens next (§4).
        Directory.CreateDirectory(Path.GetDirectoryName(protect.SourcePath)!);
        AtomicFile.WriteAllText(protect.SourcePath, Encoding.UTF8.GetString(SourceBytes(module.Text)));

        string models = Path.Combine(tmp, ModelFolder.ModelsFolder);
        string neutral = Path.Combine(models, part + OsdiBuilds.Extension);
        string here = OsdiBuilds.CurrentRid;
        VerilogASourceCompiler.Build(protect.SourcePath, OsdiBuilds.PlatformPath(neutral, here), target: null);

        // R-vl16-2g: every other platform is tried, in parallel; one that fails never fails the export.
        var others = OsdiBuilds.Rids.Where(r => r != here).ToList();
        var rows = new ExportBuild[others.Count];
        Parallel.For(0, others.Count, k =>
        {
            string rid = others[k];
            if (TargetTriples.GetValueOrDefault(rid) is not { } triple)
            { rows[k] = new ExportBuild(rid, ExportBuild.NotBuilt, "The compiler has no target for this platform.", FixFor(rid, "")); return; }
            string dst = OsdiBuilds.PlatformPath(neutral, rid);
            try
            {
                // Through circuitRF's link driver (brief vendorlib-18); this platform's own build above never is.
                VerilogASourceCompiler.Build(protect.SourcePath, dst, triple, rid: rid);
                var says = OsdiBuilds.PlatformsOf(dst);
                rows[k] = says.Count > 0 && !says.Contains(rid)
                    ? new ExportBuild(rid, ExportBuild.NotBuilt, $"The compiler wrote a build for {string.Join(", ", says)}.", "")
                    : new ExportBuild(rid, ExportBuild.NotRun);
            }
            catch (ExternalDeviceException ex)
            {
                string first = VerilogALinkDriver.DriverLine(ex.Message) ?? FirstErrorLine(ex.Message);
                rows[k] = new ExportBuild(rid, ExportBuild.NotBuilt, first, FixFor(rid, ex.Message));
            }
            if (rows[k].State == ExportBuild.NotBuilt)
                try { if (File.Exists(dst)) File.Delete(dst); TryDeleteEmpty(Path.GetDirectoryName(dst)!); } catch { }
        });
        var builds = new List<ExportBuild> { new(here, ExportBuild.Checked) };
        builds.AddRange(rows);

        // The wrapper, from the COPY's netlist so every outside file reference is already the model folder's own.
        // Read with no base folder, so each file reference stays as the copy wrote it: relative to the model folder.
        var copy = ProtectPartition.Partition(copiedCnl, part, baseDirectory: null);
        if (copy.Refusals.Count > 0) throw new ProtectRefusedException(copy.Refusals);
        if (!copy.Inside.Select(e => e.Path).SequenceEqual(protect.Partition.Inside.Select(e => e.Path), StringComparer.Ordinal))
            throw new ProtectRefusedException(["The copied cell does not partition as the cell did; nothing was written."]);
        WriteWrapper(copy, module, part, tmp, neutral);

        // A model left outside the module and compiled by Compile Verilog-A models is built for THIS platform only, so
        // the part runs only where that model has a build too (BuildsOfWrapper). A platform the module alone reached is
        // said to be not built, naming the model it lacks — "built" there would promise a part that cannot run.
        var runs = BuildsOfWrapper(tmp);
        string modelsDir = Path.Combine(tmp, ModelFolder.ModelsFolder);
        var outside = Directory.Exists(modelsDir)
            ? Directory.EnumerateFiles(modelsDir, "*" + OsdiBuilds.Extension, SearchOption.AllDirectories)
                       .Select(f => Path.Combine(modelsDir, Path.GetFileName(f)))
                       .Where(n => !Path.GetFileNameWithoutExtension(n).Equals(part, StringComparison.Ordinal))
                       .Distinct(DocumentFileRefs.PathComparer).ToList()
            : [];
        for (int k = 0; k < builds.Count; k++)
        {
            var b = builds[k];
            if (b.State == ExportBuild.NotBuilt || runs.Contains(b.Rid)) continue;
            var lacking = outside.Where(n => !File.Exists(OsdiBuilds.PlatformPath(n, b.Rid)))
                                 .Select(n => Path.GetFileNameWithoutExtension(n)).ToList();
            if (lacking.Count == 0) continue;
            File.Delete(OsdiBuilds.PlatformPath(neutral, b.Rid));
            TryDeleteEmpty(Path.GetDirectoryName(OsdiBuilds.PlatformPath(neutral, b.Rid))!);
            builds[k] = new ExportBuild(b.Rid, ExportBuild.NotBuilt,
                $"The netlist's module built, but {string.Join(", ", lacking)} (compiled outside it) has no build for this platform.",
                "Export again on that platform to add its build.");
        }
        return (builds, neutral);
    }

    /// <summary>The compiler's first line that says what went wrong, its colour codes removed.</summary>
    private static string FirstErrorLine(string message)
    {
        var lines = System.Text.RegularExpressions.Regex.Replace(message, @"\x1B\[[0-9;]*m", "")
                         .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return lines.Skip(1).FirstOrDefault(l => !l.StartsWith("Finished", StringComparison.Ordinal)) ?? lines.FirstOrDefault() ?? "";
    }

    /// <summary>The Builds row's sentence when zig is missing (brief vendorlib-18 §3).</summary>
    public const string InstallZig = "Install zig to build for other platforms, or name it in Settings ▸ Security & Permissions.";

    /// <summary>
    /// One sentence on what would build <paramref name="rid"/>, from what the attempt said (brief vendorlib-18 §3). Each
    /// cause is read from the message — the compiler's processor limit from its own crash, never from the platform pair,
    /// so a compiler that targets every processor is used as soon as one is installed (R-vl18-2a). An empty message is a
    /// platform this export did not try.
    /// </summary>
    internal static string FixFor(string rid, string message)
    {
        if (rid == "win-x86") return "The compiler has no 32-bit Windows target, so no machine can build this platform with it.";
        if (message.Length == 0) return "Export again on that platform to add its build.";
        if (VerilogASourceCompiler.SaysProcessorLimit(message))
        {
            string here = OsdiBuilds.CurrentRid[(OsdiBuilds.CurrentRid.IndexOf('-') + 1)..];
            return $"The Verilog-A compiler builds only for the processor it runs on ({here} here). A compiler build that targets every processor builds this platform too.";
        }
        if (VerilogALinkDriver.SaysZigMissing(message)) return InstallZig;
        if (VerilogALinkDriver.ZigRefusal(message) is { } zig) return zig;
        return "No known fix; the compiler's message above is all there is.";
    }

    private static void TryDeleteEmpty(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
    }

    /// <summary>
    /// R-vl16-2b: the model folder becomes the wrapper. The wrapper's <c>.csch</c> is drawn from its netlist by the
    /// netlist-to-schematic path, with the cell's pins added beside it, so the <c>.cnl</c> is still what the
    /// <c>.csch</c> extracts to (brief 6 D1). The original schematic, the sub-cell folders and every file the wrapper does
    /// not reference — a <c>.cmodel</c> above all — are removed.
    /// </summary>
    private static void WriteWrapper(ProtectPlan copy, ProtectModule module, string part, string tmp, string neutral)
    {
        var nets = new Dictionary<string, string>(StringComparer.Ordinal) { ["0"] = "0" };
        foreach (string p in copy.Pins) nets[p] = p;
        string Net(string flat)
        {
            if (nets.TryGetValue(flat, out string? n)) return n;
            string name = flat.Replace('.', '_');
            for (int k = 2; nets.ContainsValue(name); k++) name = $"{flat.Replace('.', '_')}_{k}";
            return nets[flat] = name;
        }
        var names = new HashSet<string>(StringComparer.Ordinal) { ProtectedInstance };
        string Name(string path)
        {
            string name = path.Replace('.', '_');
            for (int k = 2; !names.Add(name); k++) name = $"{path.Replace('.', '_')}_{k}";
            return name;
        }

        var tb = new TestBench(part);
        // An outside element at the top may name the cell's own variables (TLIN L=len). The wrapper's schematic is drawn
        // from this bench, whose variables become its VAR block — the cell's again once it is extracted.
        if (copy.Outside.Any(e => e.Level.IsTop)) tb.GlobalVariables.AddRange(copy.Top.Cell.Variables);
        var overrides = new List<ParameterAssignment>
        {
            new("File", "\"" + Path.GetRelativePath(tmp, neutral).Replace('\\', '/') + "\""),
            new("Pins", module.Nets.Count.ToString(CultureInfo.InvariantCulture)),
        };
        overrides.AddRange(module.Overrides);
        tb.Instances.Add(new Instance(ProtectedInstance, "VerilogA", module.Nets.Select(Net), overrides));
        var keep = new HashSet<string>(DocumentFileRefs.PathComparer);
        foreach (var e in copy.Outside)
        {
            tb.Instances.Add(new Instance(Name(e.Path), e.Instance.Reference, e.Nets.Select(Net), e.Instance.Overrides));
            foreach (var ov in e.Instance.Overrides)
            {
                string v = ov.Expression.Trim().Trim('"');
                if (v.Length == 0 || Path.IsPathRooted(v)) continue;
                // A compiled model outside the module (Compile Verilog-A models) is named by its platform-neutral path,
                // which is never a file: its builds are at models/<rid>/. Kept by that path, or they are deleted below.
                string full = Path.GetFullPath(Path.Combine(tmp, v));
                if (File.Exists(full) || (OsdiBuilds.IsCompiled(full) && OsdiBuilds.BuildsBeside(full).Count > 0)) keep.Add(full);
            }
        }

        string schDir = CellFolder.SubFolderPath(tmp, ViewType.Schematic);
        var drawn = NetlistSchematic.Build(new Library("wrapper"), tb, schematicDirectory: null);
        if (drawn.Schematic is not { } sch)
            throw new ProtectRefusedException([.. drawn.Refusals.Select(r => $"The wrapper could not be drawn: {r}")]);
        AddPins(sch, copy.Pins);
        foreach (string f in Directory.EnumerateFiles(schDir)) File.Delete(f);
        SchematicPersistence.SaveToFile(Path.Combine(schDir, part + CellFolder.ViewExtension(ViewType.Schematic)), sch, part);

        // What the wrapper does not reference leaves the folder.
        string cells = Path.Combine(tmp, ModelFolder.CellsFolder);
        if (Directory.Exists(cells)) Directory.Delete(cells, recursive: true);
        string models = Path.Combine(tmp, ModelFolder.ModelsFolder);
        if (Directory.Exists(models))
        {
            foreach (string f in Directory.EnumerateFiles(models, "*", SearchOption.AllDirectories).ToList())
            {
                bool build = Path.GetFileNameWithoutExtension(f) == part && OsdiBuilds.IsCompiled(f);
                bool otherBuild = OsdiBuilds.IsCompiled(f) && keep.Any(k => OsdiBuilds.BuildsBeside(k).Any(rid => DocumentFileRefs.PathComparer.Equals(OsdiBuilds.PlatformPath(k, rid), f)));
                if (!build && !otherBuild && !keep.Contains(Path.GetFullPath(f))) File.Delete(f);
            }
            foreach (string d in Directory.EnumerateDirectories(models, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                TryDeleteEmpty(d);
        }
    }

    /// <summary>The cell's pins, in a column to the left of the drawing, each joined to its net by a label on its pin.</summary>
    internal static void AddPins(SchematicEditModel sch, IReadOnlyList<string> pins)
    {
        double left = sch.Components.Count == 0 ? 0 : sch.Components.Min(c => c.X);
        double top = sch.Components.Count == 0 ? 0 : sch.Components.Min(c => c.Y);
        double x = Math.Floor((left - 1200) / 100) * 100;
        for (int k = 0; k < pins.Count; k++)
        {
            var pin = new EditableComponent { InstanceName = $"PIN{k + 1}", Symbol = SymbolKind.Pin, X = x, Y = top + k * 400 };
            pin.Parameters.Add(new EditableParameter { Name = "Num", Expression = (k + 1).ToString(CultureInfo.InvariantCulture) });
            pin.Parameters.Add(new EditableParameter { Name = "Name", Expression = pins[k] });
            sch.Components.Add(pin);
            var (px, py) = pin.GetPortWorldCoord(0);
            sch.NetLabels.Add(new EditableNetLabel { X = px, Y = py, Name = pins[k] });
        }
    }

    /// <summary>The proof's three frequencies, from the range the part states.</summary>
    private static double[] ProofFrequencies(ExportPlan plan, ExportOptions options) => ProtectCheck.Frequencies(FrequencyRange(plan, options));

    /// <summary>R-vl16-6b: the one summary line above the Builds table, or "" when every platform is built.</summary>
    public static string UnavailableLine(IReadOnlyList<ExportBuild> builds)
    {
        var missing = builds.Where(b => b.State == ExportBuild.NotBuilt).Select(b => b.Rid).ToList();
        return missing.Count == 0 ? "" : $"This part will be unavailable on: {string.Join(", ", missing)}.";
    }

    // ── Adding this platform to a model exported elsewhere (R-vl16-2d) ─────────────────────────

    /// <summary>
    /// The same Export with Compile netlist, run on another platform into the same library: when the source hash matches
    /// the model's <c>CompiledFrom</c>, this platform's build is added and proved here; when it differs, nothing is
    /// written. Works on a copy of the model folder and swaps it in only when the proof passes.
    /// </summary>
    private static ExportResult AddPlatform(ExportPlan plan, ExportOptions options, ProtectPlanned protect, ModelPartManifest existing,
                                            RunControl? control)
    {
        string modelDir = plan.ModelDir, parent = Path.GetDirectoryName(modelDir)!;
        string here = OsdiBuilds.CurrentRid;
        if (existing.CompiledFrom != protect.Hash)
        {
            string from = existing.Builds is { Count: > 0 } b ? string.Join(", ", b) : "other platforms'";
            return new ExportResult { Refusal = $"The cell changed since the {from} build. Rebuild every platform from one source." };
        }
        string id = Guid.NewGuid().ToString("N")[..8];
        string wrapper = Path.Combine(parent, $".{plan.PartNumber}.tmp-{id}");
        string tmp = Path.Combine(wrapper, plan.PartNumber);
        try
        {
            CopyDirectory(modelDir, tmp);
            control?.BeginStage("Compiling the netlist");
            Directory.CreateDirectory(Path.GetDirectoryName(protect.SourcePath)!);
            AtomicFile.WriteAllText(protect.SourcePath, Encoding.UTF8.GetString(SourceBytes(protect.Module!.Text)));
            string neutral = Path.Combine(tmp, ModelFolder.ModelsFolder, plan.PartNumber + OsdiBuilds.Extension);
            string build = OsdiBuilds.PlatformPath(neutral, here);
            if (File.Exists(build)) File.Delete(build);
            VerilogASourceCompiler.Build(protect.SourcePath, build, target: null);
            InvalidateResolution();

            control?.BeginStage("Proving the compiled part");
            var proof = Prove(plan, options, protect, tmp, existing);
            if (!proof.Passed) return Refuse(wrapper, proof.Refusal!);

            var builds = (existing.Builds ?? []).Append(here).Distinct().OrderBy(r => OsdiBuilds.Rids.ToList().IndexOf(r)).ToList();
            var proved = (existing.Proved ?? []).Append(here).Distinct().OrderBy(r => OsdiBuilds.Rids.ToList().IndexOf(r)).ToList();
            var manifest = existing with { Builds = builds, Proved = proved };
            string partPath = Path.Combine(tmp, ModelFolder.PartFileName);
            ModelPartManifest.Write(partPath, manifest);
            manifest = manifest with { PublishedHash = ModelPartHash.Compute(tmp) };
            ModelPartManifest.Write(partPath, manifest);

            string backup = Path.Combine(parent, $".{plan.PartNumber}.old-{id}");
            Directory.Move(modelDir, backup);
            Directory.Move(tmp, modelDir);
            TryDelete(wrapper);
            TryDelete(backup);
            InvalidateResolution();
            return new ExportResult
            {
                ModelDir = modelDir, Revision = manifest.Revision,
                Created = [Path.GetFullPath(OsdiBuilds.PlatformPath(Path.Combine(modelDir, ModelFolder.ModelsFolder, plan.PartNumber + OsdiBuilds.Extension), here))],
                Message = $"Added the {here} build of {plan.PartNumber}, checked here: {proof.Summary}",
                Builds = [.. OsdiBuilds.Rids.Select(r => proved.Contains(r) ? new ExportBuild(r, ExportBuild.Checked)
                                                     : builds.Contains(r) ? new ExportBuild(r, ExportBuild.NotRun)
                                                     : new ExportBuild(r, ExportBuild.NotBuilt, "", FixFor(r, "")))],
                SourcePath = protect.SourcePath, CompiledFrom = protect.Hash, Proof = proof.Summary,
            };
        }
        catch (ExternalDeviceException ex) { return Refuse(wrapper, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refuse(wrapper, $"The export could not be written: {ex.Message}");
        }
    }

    /// <summary>The proof (§4) of the folder at <paramref name="modelDir"/> against the source cell.</summary>
    private static ProtectCheckResult Prove(ExportPlan plan, ExportOptions options, ProtectPlanned protect, string modelDir,
                                            ModelPartManifest manifest)
    {
        string? srcText = ModelNetlist.FromCell(plan.CellDir, out string? why);
        if (srcText is null) return new ProtectCheckResult(double.NaN, "", 0, $"The proof could not extract the source cell: {why}");
        string srcSch = PrimaryPath(plan.CellDir, ViewType.Schematic) ?? plan.CellDir;
        string? srcBase = SchematicCircuit.ReferenceBaseOfDirectory(Path.GetDirectoryName(srcSch));
        string srcCell = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.CellDir)));
        string wrapperText = File.ReadAllText(ModelNetlist.PathOf(modelDir));
        double[] freqs = ProtectCheck.Frequencies(manifest.Frequency ?? FrequencyRange(plan, options));
        return ProtectCheck.Run(srcText, srcCell, srcBase, wrapperText, manifest.Cell, modelDir, manifest.Pins,
                                protect.Bias, freqs, protect.Thermal);
    }

    /// <summary>The platforms the wrapper runs on: those for which every compiled model in <c>models/</c> has a build.</summary>
    private static List<string> BuildsOfWrapper(string modelDir)
    {
        string models = Path.Combine(modelDir, ModelFolder.ModelsFolder);
        if (!Directory.Exists(models)) return [];
        var neutral = Directory.EnumerateFiles(models, "*" + OsdiBuilds.Extension, SearchOption.AllDirectories)
                               .Select(f => Path.Combine(models, Path.GetFileName(f))).Distinct(DocumentFileRefs.PathComparer).ToList();
        return [.. OsdiBuilds.Rids.Where(rid => neutral.Count > 0 && neutral.All(n => File.Exists(OsdiBuilds.PlatformPath(n, rid))))];
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
    }
}
