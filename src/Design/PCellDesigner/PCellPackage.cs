// Owner: PCD-5 (brief-pcd-0 §5, seam 6; written by PCD-1's seams commit). Framework-free.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.PCellDesigner;

/// <summary>brief-pcd-0 §4.4: the <c>manifest.json</c> of a <c>.cpcell</c>.</summary>
public sealed record PCellPackageManifest
{
    public string Format { get; init; } = PCellDesign.PackageFormat;
    public int    FormatVersion { get; init; } = PCellDesign.PackageFormatVersion;
    public string Name { get; init; } = "";
    public string GeneratorId { get; init; } = "";
    public string ContentKey { get; init; } = "";
    public IReadOnlyList<string> Generators { get; init; } = [];
    public IReadOnlyList<string> Layers { get; init; } = [];
    public string WrittenBy { get; init; } = "";
}

/// <summary>Path: the package written (Export) or the cell folder imported (Import); null when refused, the refusal an
/// Error in Findings.</summary>
public sealed record PCellPackageOutcome(string? Path, PCellPackageManifest? Manifest,
                                         IReadOnlyList<CircuitRF.Diagnostics.Diagnostic> Findings);

/// <summary>brief-pcd-0 §4.4: export and import of a designed cell as a <c>.cpcell</c>.</summary>
/// <remarks>brief-pcd-5 R-pcd5-3, D1 and D2: the package is the cell folder renamed to its cell, written deterministically
/// (entries sorted ordinally, deflate, every timestamp 1980-01-01), and its content key is one function of the folder.</remarks>
public static class PCellPackage
{
    private static readonly DateTimeOffset Epoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    /// <summary>Runs <see cref="PCellDesignCheck"/> first.</summary>
    public static PCellPackageOutcome Export(string cellDir, string packagePath)
    {
        cellDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cellDir));
        string name = Path.GetFileName(cellDir);
        var findings = new List<Diagnostic>(PCellDesignCheck.Run(cellDir));
        if (findings.Any(f => f.Severity == DiagnosticSeverity.Error))
            return Refused(findings, "pcell.export", $"{name} was not exported: the check above reports errors.");

        try
        {
            var ccell = CellFolder.ReadCcell(cellDir)!;
            string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
            string cschPath = Path.Combine(schematicDir, CellFolder.ResolvePrimary(cellDir, ViewType.Schematic).ResolvedName!);
            byte[] csch = File.ReadAllBytes(cschPath);
            var model = SchematicPersistence.LoadFromFile(cschPath).model;
            var ports = PCellDesign.PortsOf(model);

            // The symbol the Library tile draws: the cell's own when it has one with pins, else the generated box.
            byte[] csym;
            if (CellFolder.ResolvePrimary(cellDir, ViewType.Symbol).ResolvedName is { } symName
                && Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), symName) is var symPath
                && SymbolPersistence.LoadFromFile(symPath).Pins.Count > 0)
                csym = File.ReadAllBytes(symPath);
            else
                csym = Encoding.UTF8.GetBytes(SymbolPersistence.Serialize(AutoSymbolGenerator.Generate(name, ports.Count)));

            // Each view is the sole file of its folder, so the packaged cell names no primary (D1).
            var packaged = CellPersistence.Deserialize(CellPersistence.Serialize(ccell));
            packaged.PrimarySchematic = packaged.PrimarySymbol = packaged.PrimaryLayout = packaged.Primary3D = null;
            packaged.LayoutGenerator = PCellDesign.GeneratorIdFor(name);
            packaged.EmCopper = true;
            packaged.Terminals = PCellDesign.TerminalsOf(model);
            packaged.NumPorts = ports.Count;

            var manifest = new PCellPackageManifest
            {
                Name = name,
                GeneratorId = PCellDesign.GeneratorIdFor(name),
                ContentKey = ContentKeyOf(cellDir)!,
                Generators = GeneratorsOf(model, schematicDir),
                Layers = LayersOf(model),
                WrittenBy = $"circuitRF {CircuitRF.Design.Modeling.CmodelExport.Version}".TrimEnd(),
            };

            var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [PCellDesign.ManifestFileName] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, ManifestJson)),
                [$"{name}/{CellFolder.CcellFileName}"] = Encoding.UTF8.GetBytes(CellPersistence.Serialize(packaged)),
                [$"{name}/{CellFolder.SubFolderName(ViewType.Schematic)}/{name}{CellFolder.ViewExtension(ViewType.Schematic)}"] = csch,
                [$"{name}/{CellFolder.SubFolderName(ViewType.Symbol)}/{name}{CellFolder.ViewExtension(ViewType.Symbol)}"] = csym,
            };

            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (path, bytes) in entries)
                {
                    var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                    entry.LastWriteTime = Epoch;
                    using var s = entry.Open();
                    s.Write(bytes);
                }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(packagePath))!);
            File.WriteAllBytes(packagePath, buffer.ToArray());
            findings.Add(Diagnostic.Create("pcell.exported", DiagnosticSeverity.Info, "{name} exported to {path}.",
                                           ("name", name), ("path", packagePath)));
            return new PCellPackageOutcome(packagePath, manifest, findings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return Refused(findings, "pcell.export", $"{name} was not exported: {e.Message}");
        }
    }

    public static PCellPackageOutcome Import(string packagePath, string libraryDir)
    {
        var findings = new List<Diagnostic>();
        if (ReadManifest(packagePath, out string? refusal) is not { } manifest)
            return Refused(findings, "pcell.import", refusal!);
        string name = manifest.Name;
        if (!Directory.Exists(libraryDir))
            return Refused(findings, "pcell.import", $"{name} was not imported: the folder {libraryDir} does not exist.");
        libraryDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryDir));

        // Every layer a part names must be a conductor of the technology it lands on (as `import part` asks).
        if (manifest.Layers.Count > 0)
        {
            var tech = WorkspaceRootFinder.FindAncestorCws(libraryDir) is { } cws
                ? TechnologyResolver.ResolveForDocument(null, Path.Combine(libraryDir, name, "layout", name + ".clay"), cws, new TechnologyCache()).Resolution.Tech
                : null;
            var conductors = tech?.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = manifest.Layers.Where(l => conductors is null || !conductors.Contains(l)).ToList();
            if (missing.Count > 0)
                return Refused(findings, "pcell.import-layers",
                    $"{name} was not imported: its parts are drawn on {string.Join(", ", missing)}, which " +
                    (tech is null ? "no technology resolves here to check" : $"the technology {tech.Name} does not have") + ".");
        }

        string target = Path.Combine(libraryDir, name);
        string staging = Path.Combine(libraryDir, $".{name}.cpcell-import-{Guid.NewGuid():N}");
        try
        {
            using (var zip = ZipFile.OpenRead(packagePath))
                foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(name + "/", StringComparison.Ordinal) && e.Name.Length > 0))
                {
                    string dest = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest);
                }
            string unpacked = Path.Combine(staging, name);
            string? key = ContentKeyOf(unpacked);

            // D2: an existing cell is compared with the package as unpacked, by the same function.
            if (Directory.Exists(target))
            {
                if (key is not null && ContentKeyOf(target) == key)
                {
                    findings.Add(Diagnostic.Create("pcell.already-imported", DiagnosticSeverity.Info,
                        "{name} is already imported in {dir}; nothing was written.", ("name", name), ("dir", libraryDir)));
                    return new PCellPackageOutcome(target, manifest, findings);
                }
                return Refused(findings, "pcell.import-exists",
                    $"{name} was not imported: {target} already holds another cell of that name; it is never overwritten. " +
                    "Rename or remove it first.");
            }
            Directory.Move(unpacked, target);
            findings.Add(Diagnostic.Create("pcell.imported", DiagnosticSeverity.Info, "{name} imported into {dir}.",
                                           ("name", name), ("dir", libraryDir)));
            return new PCellPackageOutcome(target, manifest, findings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Refused(findings, "pcell.import", $"{name} was not imported: {e.Message}");
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public static PCellPackageManifest? ReadManifest(string packagePath, out string? refusal)
    {
        refusal = null;
        try
        {
            using var zip = ZipFile.OpenRead(packagePath);
            if (zip.GetEntry(PCellDesign.ManifestFileName) is not { } entry)
                return Fail($"{Path.GetFileName(packagePath)} is not a PCell package: it has no {PCellDesign.ManifestFileName}.", out refusal);
            PCellPackageManifest? manifest;
            using (var s = entry.Open()) manifest = JsonSerializer.Deserialize<PCellPackageManifest>(s);
            if (manifest is null || manifest.Format != PCellDesign.PackageFormat)
                return Fail($"{Path.GetFileName(packagePath)} is not a PCell package (format \"{manifest?.Format}\").", out refusal);
            if (manifest.FormatVersion > PCellDesign.PackageFormatVersion)
                return Fail($"{Path.GetFileName(packagePath)} is package format {manifest.FormatVersion}; this circuitRF reads up to " +
                            $"{PCellDesign.PackageFormatVersion}. Update circuitRF to import it.", out refusal);
            if (NameValidator.Validate(manifest.Name) is { } bad)
                return Fail($"{Path.GetFileName(packagePath)} names its cell \"{manifest.Name}\": {bad}.", out refusal);

            // Only the manifest and the one cell folder, with no entry escaping it.
            foreach (var e in zip.Entries)
                if (e.FullName != PCellDesign.ManifestFileName && !InsideCell(e.FullName, manifest.Name))
                    return Fail($"{Path.GetFileName(packagePath)} holds \"{e.FullName}\", outside its cell folder {manifest.Name}.", out refusal);
            if (zip.GetEntry($"{manifest.Name}/{CellFolder.CcellFileName}") is null)
                return Fail($"{Path.GetFileName(packagePath)} has no {manifest.Name}/{CellFolder.CcellFileName}.", out refusal);
            return manifest;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return Fail($"{Path.GetFileName(packagePath)} could not be read as a PCell package: {e.Message}", out refusal);
        }

        static PCellPackageManifest? Fail(string why, out string? refusal) { refusal = why; return null; }
        static bool InsideCell(string entry, string cell) =>
            entry.StartsWith(cell + "/", StringComparison.Ordinal) && !entry.Contains('\\') && !Path.IsPathRooted(entry)
            && !entry.Split('/').Contains("..");
    }

    // ── The content key (D2) ──────────────────────────────────────────────────

    [ThreadStatic] private static HashSet<string>? _keying;

    /// <summary>
    /// PCD-0 §4.4: <c>sha256:</c> over the primary <c>.csch</c> bytes, the <c>.ccell</c>'s <c>Parameters</c> as serialised,
    /// and the sorted <see cref="PCellRegistry.GeneratorContentKey"/> of every generator the schematic places. Null when the
    /// cell has no readable primary schematic. A designed cell reached again while its own key is being made contributes
    /// <c>cycle</c> (D4), so a cell that places itself still has a key.
    /// </summary>
    internal static string? ContentKeyOf(string cellDir)
    {
        string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cellDir));
        var keying = _keying ??= new HashSet<string>(StringComparer.Ordinal);
        if (!keying.Add(dir)) return "cycle";
        try
        {
            if (CellFolder.ReadCcell(dir) is not { } ccell
                || CellFolder.ResolvePrimary(dir, ViewType.Schematic).ResolvedName is not { } csch) return null;
            string schematicDir = CellFolder.SubFolderPath(dir, ViewType.Schematic);
            string path = Path.Combine(schematicDir, csch);
            byte[] bytes = File.ReadAllBytes(path);
            var model = SchematicPersistence.Deserialize(Encoding.UTF8.GetString(bytes), schematicDir).model;

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(bytes);
            sha.AppendData(Encoding.UTF8.GetBytes(CellPersistence.Serialize(new CcellFile { Parameters = ccell.Parameters })));
            foreach (string g in GeneratorsOf(model, schematicDir))
                sha.AppendData(Encoding.UTF8.GetBytes($"\n{g}={PCellRegistry.GeneratorContentKey(g)}"));
            return "sha256:" + Convert.ToHexStringLower(sha.GetHashAndReset());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return null; }
        finally { keying.Remove(dir); }
    }

    /// <summary>The generators the schematic places, sorted: each part's own, and a cell's that its generator draws.</summary>
    internal static IReadOnlyList<string> GeneratorsOf(SchematicEditModel model, string schematicDir)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var c in model.Components.Where(c => c.Disable is not (DisableState.Open or DisableState.Short)))
        {
            if (c.CellRef is { Length: > 0 } cellRef)
            {
                if (CellFolder.LayoutGeneratorOf(ExternalCellRef.ResolveCellDir(cellRef, schematicDir)) is { } drawnBy) ids.Add(drawnBy);
            }
            else if (SchematicLayoutPlanner.HasPCellGenerator(c.Symbol, c.PortCount, out string id)) ids.Add(id);
        }
        return [.. ids];
    }

    /// <summary>Every layer a part states explicitly, sorted.</summary>
    internal static IReadOnlyList<string> LayersOf(SchematicEditModel model) =>
        [.. model.Components.SelectMany(c => c.Parameters)
            .Where(p => p.Name is DrawnMetal.SignalLayer or DrawnMetal.GroundReference && !string.IsNullOrWhiteSpace(p.Expression))
            .Select(p => p.Expression.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)];

    private static PCellPackageOutcome Refused(List<Diagnostic> findings, string id, string why)
    {
        findings.Add(new Diagnostic(id, DiagnosticSeverity.Error, why));
        return new PCellPackageOutcome(null, null, findings);
    }
}
