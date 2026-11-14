// brief-cosim-pattern-1 — how a result was made, kept with the result.
//
// A .sNp header carries hashes, which answer whether the document changed but not what it said. So a finished driven run writes
// <name>.cemr beside its .sNp: the settings it used, its ports and sweep, and which files hold its numbers. The record is the only
// pointer to those files, and every path in it is relative to the record, so a result folder moved elsewhere still resolves.
//
// ONE STAMP PER RUN. The record, the .sNp (a provenance line), the .npy (a __ cube) and the field set (its manifest) all carry it.
// Results are published by renames that are not atomic as a group (EmResultStaging), so a process that dies among them leaves
// stamps that disagree, and Verify says which file differs. File times are never compared: a git checkout or a copy sets them
// arbitrarily.

using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CircuitRF.Core.Devices;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Results;
using CircuitRF.Design.Thermal;
using RfCore;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Design.Layout.Em;

/// <summary>
/// R-cp1-7 — the one name a result goes by. The setup's Output name decides every file: <c>&lt;name&gt;.sNp</c>,
/// <c>&lt;name&gt;_em.npy</c>, <c>&lt;name&gt;.fields/</c> and <c>&lt;name&gt;.cemr</c>.
/// </summary>
public static partial class EmResultNames
{
    /// <summary>A result record's extension.</summary>
    public const string RecordExtension = ".cemr";

    [GeneratedRegex(@"\.s(\d+|n)p$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TouchstoneSuffix();

    /// <summary>A typed or browsed name without the <c>.sNp</c>, <c>.npy</c> or <c>.cemr</c> it arrived with: the suffixes are the
    /// run's to add. A browsed <c>x_em.npy</c> is the result named <c>x</c>.</summary>
    public static string Clean(string name)
    {
        if (name.EndsWith(RecordExtension, StringComparison.OrdinalIgnoreCase)) return name[..^RecordExtension.Length];
        if (name.EndsWith(".npy", StringComparison.OrdinalIgnoreCase))
        {
            string stem = name[..^4];
            return stem.EndsWith(EmRunService.NpyKeySuffix, StringComparison.Ordinal) ? stem[..^EmRunService.NpyKeySuffix.Length] : stem;
        }
        return TouchstoneSuffix().Replace(name, "");
    }

    /// <summary>The stem a blank Output name resolves to, with no folder and no suffix: the setup's key, and in 3D the solver's
    /// token. A comparison writes one result per solver and its own <c>.compare</c>, so that is the name it shows.</summary>
    public static string BlankStem(EmSetup setup)
    {
        if (setup.IsThermal) return ThermalRunService.ResultKey(setup);
        if (!setup.Is3D) return EmRunService.ResolveResultKey(setup);
        return setup.Solver3D.IsCompare
            ? EmRunService.ResolveResultKey(setup) + "." + Em3dRunService.CompareToken
            : Em3dRunService.ResultKey(setup, setup.Solver3D.Reference);
    }

    /// <summary>What a result's companions (<c>_sim</c> cell, <c>_cosim.cnl</c>) are named after: the Output name's own file name
    /// when there is one, so each result has its own, otherwise the setup's key.</summary>
    public static string CompanionKey(EmSetup setup)
        => setup.SnpOutputPathOverride is { Length: > 0 } o
           && ResultsWriter.SanitizeFileNameComponent(Path.GetFileName(Clean(o).TrimEnd('/', '\\'))) is { Length: > 0 } name
            ? name
            : EmRunService.ResolveResultKey(setup);

    /// <summary>The folder a run writes under when no Output name says otherwise: the workspace's flat <c>results/</c>.</summary>
    internal static string ResultsDirectory(string resultsRoot)
        => ResultsWriter.ResultsDirectory(Path.GetDirectoryName(resultsRoot.TrimEnd(Path.DirectorySeparatorChar)) ?? resultsRoot);
}

/// <summary>Where one result's files land.</summary>
/// <param name="Base">The result's name as a path with no suffix.</param>
/// <param name="NpyPath">The <c>.npy</c>.</param>
/// <param name="SnpBase">The Touchstone with no <c>.sNp</c>: <paramref name="Base"/>, unless the run was given a separate Touchstone
/// destination (<see cref="EmRunTarget.TouchstoneDestination"/>).</param>
public sealed record EmResultPaths(string Base, string NpyPath, string SnpBase)
{
    public string FieldsFolder => PortFieldSet.FolderForBase(Base);
    public string RecordPath => Base + EmResultNames.RecordExtension;
    public string SnpPath(int ports) => SnpBase + $".s{ports}p";

    /// <summary>A planar or cross-section result's files.</summary>
    public static EmResultPaths Planar(string resultsRoot, EmSetup setup, string? touchstone = null)
    {
        string b = EmRunService.ResolveSnpBasePath(resultsRoot, setup);
        string npy = setup.SnpOutputPathOverride is { Length: > 0 }
            ? b + EmRunService.NpyKeySuffix + ".npy"
            : Path.Combine(EmResultNames.ResultsDirectory(resultsRoot), EmRunService.ResolveNpyKey(setup) + ".npy");
        return new EmResultPaths(b, npy, touchstone is { Length: > 0 } t ? EmResultNames.Clean(t) : b);
    }
}

/// <summary>What one run is asked beyond the setup. Neither field is part of the setup: nothing here is saved with it.</summary>
public sealed record EmRunTarget
{
    /// <summary><c>em -o</c>: where this run's Touchstone goes, and nothing else. The setup's Output name still decides the
    /// <c>.npy</c>, the field set and the record.</summary>
    public string? TouchstoneDestination { get; init; }

    /// <summary>False for a run whose Touchstone is renamed by its caller (the EM cache): a record would name a file that is about
    /// to move.</summary>
    public bool KeepRecord { get; init; } = true;

    /// <summary>brief-icp-8 — false for a run whose component ports are closed by its caller (an EM cell's evaluation puts the
    /// schematic's own parts back on them in memory): it writes no <c>_cosim</c> netlist beside its Touchstone.</summary>
    public bool WriteCoSim { get; init; } = true;

    /// <summary>The <c>.cem</c> or <c>.c3d</c> the setup was read from, so a record can compare it later; null when it has none.</summary>
    public string? SetupPath { get; init; }

    /// <summary>The name of the setup embedded in <see cref="SetupPath"/>; null for a <c>.cem</c>.</summary>
    public string? EmbeddedSetup { get; init; }
}

/// <summary>Which solver wrote a result.</summary>
public sealed class EmResultSolver
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary><c>cpu</c>, <c>gpu</c> or <c>auto</c>; null on an adopted result, which does not say.</summary>
    public string? Device { get; set; }
}

/// <summary>The result's files, each relative to the record.</summary>
public sealed class EmResultFiles
{
    public string Snp { get; set; } = "";
    public string Npy { get; set; } = "";
    /// <summary>The per-port field set's folder; null when the run kept none.</summary>
    public string? Fields { get; set; }
}

/// <summary>One port of a result.</summary>
public sealed class EmResultPort
{
    public const string Gap = "Gap", Plane = "Plane", Wave = "Wave";

    public int Number { get; set; }
    public string Name { get; set; } = "";
    public double Z0Re { get; set; }
    public double Z0Im { get; set; }

    /// <summary><see cref="Gap"/>: its negative end is a pad. <see cref="Plane"/>: it returns to a real reference.
    /// <see cref="Wave"/>: a wave port. Null on an adopted result whose file does not say.</summary>
    public string? Kind { get; set; }

    /// <summary>A component port's part; null otherwise.</summary>
    public string? Part { get; set; }

    /// <summary>A component port's pins: the signal pin, then its reference pin when it has one.</summary>
    public List<string>? Pins { get; set; }

    /// <summary>brief-icp-2 R-icp2-5 — a cut instance's cell (its generator for a generated cell); <see cref="Part"/> is then the
    /// instance's path. Absent from the record otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cell { get; set; }

    /// <summary>brief-hzn-11 R-hzn11-4 — a wave port whose S is normalised to its mode (its <see cref="Z0Re"/> of 1 is then not an
    /// impedance). Absent from the record when false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ModeNormalised { get; set; }

    /// <summary>brief-icp-2a — an Uncalibrated Port: driven at its cut and not de-embedded, its launch in the result. Absent
    /// from the record when false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Uncalibrated { get; set; }

    [JsonIgnore] public Complex Z0 => new(Z0Re, Z0Im);
}

/// <summary>The frequencies a result holds, in hertz.</summary>
public sealed class EmResultFrequencies
{
    public List<double> SweepHz { get; set; } = [];
    public List<double> FarFieldHz { get; set; } = [];
    public List<double> FieldSetHz { get; set; } = [];
}

/// <summary>What the run was handed and what it solved on.</summary>
public sealed class EmResultGeometry
{
    public string? GeometryHash { get; set; }
    public string? MeshHash { get; set; }
    public string? PortHash { get; set; }
    public string? ModelRevision { get; set; }
    public long? MeshSize { get; set; }
    /// <summary>What <see cref="MeshSize"/> counts: <c>elements</c>, <c>tetrahedra</c>, <c>cells</c>.</summary>
    public string? MeshUnit { get; set; }
    public long? Unknowns { get; set; }
}

/// <summary>Where the setup behind a result was read from.</summary>
public sealed class EmResultSetupSource
{
    /// <summary>The <c>.cem</c> or <c>.c3d</c>, relative to the record.</summary>
    public string File { get; set; } = "";
    /// <summary>The setup's name inside a <c>.c3d</c>; null for a <c>.cem</c>.</summary>
    public string? Embedded { get; set; }
}

/// <summary>One file of a record that does not hold up: missing, or carrying another run's stamp.</summary>
public sealed record EmResultDisagreement(string What, string Path, string Sentence);

/// <summary>R-cp1-1 — a <c>.cemr</c>: how one finished driven run was made and where its numbers are.</summary>
public sealed partial class EmResultRecord
{
    /// <summary>The <c>.npy</c>'s metadata cube holding the stamp, on the group that holds S.</summary>
    public const string StampCube = EmResultIntegrity.StampCube;

    public int FormatVersion { get; set; } = 1;
    public string Stamp { get; set; } = "";
    public DateTimeOffset Written { get; set; }
    /// <summary>The circuitRF version that wrote the record.</summary>
    public string Product { get; set; } = "";
    public EmResultSolver Solver { get; set; } = new();
    public EmResultFiles Files { get; set; } = new();
    public List<EmResultPort> Ports { get; set; } = [];
    public EmResultFrequencies Frequencies { get; set; } = new();

    /// <summary>What every other port was doing in the stored pattern (<see cref="EmFarFieldExcitation"/>); null when the result holds no far field.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FarFieldExcitation { get; set; }

    /// <summary>The whole setup, as a <c>.cem</c> holds it. Null on an adopted result: its settings were not recorded.</summary>
    public JsonElement? Setup { get; set; }

    /// <summary>Where the setup was read from; null when it was not read from a file.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmResultSetupSource? SetupSource { get; set; }

    /// <summary>The 3D view's document as text, when the run came from one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreeDView { get; set; }

    public EmResultGeometry? Geometry { get; set; }

    /// <summary>Written by <see cref="Adopt"/> for a run from before records existed: its data files carry no stamp.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Adopted { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <summary>The record in <paramref name="path"/>, or the sentence why not.</summary>
    public static (EmResultRecord? Record, string? Refusal) Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return (null, $"'{path}' does not exist.");
            var r = JsonSerializer.Deserialize<EmResultRecord>(File.ReadAllText(path), Json);
            if (r is null || r.Stamp.Length == 0) return (null, $"'{Path.GetFileName(path)}' is not a result record: it has no Stamp.");
            if (r.FormatVersion != 1)
                return (null, $"'{Path.GetFileName(path)}' is a result record of format {r.FormatVersion}, and this build reads format 1.");
            return (r, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, $"'{Path.GetFileName(path)}' could not be read: {e.Message}");
        }
    }

    /// <summary>A path from the record's folder as it is stored: relative, with forward slashes.</summary>
    internal static string Relative(string recordPath, string target)
        => Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(recordPath))!, Path.GetFullPath(target)).Replace('\\', '/');

    /// <summary>A path the record stores, as a full path, given where the record is now.</summary>
    public static string Resolve(string recordPath, string relative)
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(recordPath))!, relative));

    // ── the stamp on each data file ───────────────────────────────────────────────────────────────

    /// <summary>R-cp1-2 — the stamp as a <c>__</c> cube on the group holding S. Replaces one already there.</summary>
    internal static void StampData(DataSet data, string stamp)
    {
        if (data.Groups.FirstOrDefault(g => data.CubesIn(g).ContainsKey("S")) is not { } group) return;
        data.AddToGroup(group, StampCube, new DataCube([new Axis("stamp", [0.0], "", [stamp])], new double[1]));
    }

    /// <summary>The stamp a <c>.npy</c> carries, read from its metadata without its arrays; null when it carries none.</summary>
    public static string? ReadNpyStamp(string npyPath) => EmResultIntegrity.NpyStamp(npyPath);

    /// <summary>The stamp a <c>.sNp</c>'s header carries; null when it carries none.</summary>
    public static string? ReadSnpStamp(string snpPath) => EmResultIntegrity.SnpStamp(snpPath);

    /// <summary>
    /// Every file the record names that is missing, truncated or carries another run's stamp, in publish order. Empty when the result
    /// holds together. An adopted record's files were written before stamps, so only whether they are whole can be asked about.
    /// The check itself is <see cref="EmResultIntegrity.Check"/>, the one an EM Field Data block is refused by.
    /// </summary>
    public IReadOnlyList<EmResultDisagreement> Verify(string recordPath)
        => [.. EmResultIntegrity.Check(recordPath, Stamp, Adopted, Files.Snp, Files.Npy, Files.Fields, Frequencies.SweepHz.Count)
                                .Select(p => new EmResultDisagreement(p.What, p.Path, p.Sentence))];

    // ── a finished run's record ───────────────────────────────────────────────────────────────────

    /// <summary>The record of a run whose files are staged. The hashes are read back from the Touchstone just written, so they are
    /// the file's own and no second copy of how they are formed exists.</summary>
    internal static EmResultRecord Build(EmPublishRequest r, string stamp, string stagedSnp)
    {
        var paths = r.Paths;
        string snp = paths.SnpPath(r.PortCount);
        var provenance = EmSnpProvenance.TryRead(stagedSnp);
        var record = new EmResultRecord
        {
            Stamp = stamp,
            Written = DateTimeOffset.Now,
            Product = ProductVersion(),
            Solver = new EmResultSolver { Name = r.Solver, Version = r.SolverVersion, Device = r.Device },
            Files = new EmResultFiles
            {
                Snp = Relative(paths.RecordPath, snp),
                Npy = Relative(paths.RecordPath, paths.NpyPath),
                Fields = r.Fields is null ? null : Relative(paths.RecordPath, paths.FieldsFolder),
            },
            Ports = [.. r.Ports],
            Frequencies = new EmResultFrequencies
            {
                SweepHz = [.. SweepOf(r.Data)],
                FarFieldHz = [.. FarFieldOf(r.Data)],
                FieldSetHz = r.Fields is null ? [] : [.. r.Fields.Frequencies.Select(f => f.Hz)],
            },
            FarFieldExcitation = ExcitationOf(r.Data),
            Setup = EmSetupPersistence.ToEmbedded(r.Setup),
            ThreeDView = r.ThreeDViewText,
            Geometry = new EmResultGeometry
            {
                GeometryHash = provenance?.GeometryHash, MeshHash = provenance?.MeshHash, PortHash = provenance?.PortHash,
                ModelRevision = provenance?.ModelRevision,
                MeshSize = r.Mesh?.Size, MeshUnit = r.Mesh?.Unit, Unknowns = r.Mesh?.Unknowns,
            },
        };
        if (r.Target?.SetupPath is { Length: > 0 } setupPath)
            record.SetupSource = new EmResultSetupSource { File = Relative(paths.RecordPath, setupPath), Embedded = r.Target.EmbeddedSetup };
        return record;
    }

    private static string ProductVersion()
        => typeof(EmResultRecord).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } v
            ? v.Split('+')[0] : "";

    /// <summary>A far field beside the planar kernel's diagnostics is that kernel's. Asked of the data, so a result that names no solver answers too.</summary>
    private static string? ExcitationOf(DataSet data)
        => !data.Groups.Contains(CircuitRF.Engine.Mom.PlanarFarField.Group) ? null
         : data.Groups.Contains(CircuitRF.Engine.Mom.PlanarKernel.DiagnosticsGroup) ? EmFarFieldExcitation.ShortedOthers
         : EmFarFieldExcitation.TerminatedZ0;

    private static double[] SweepOf(DataSet data)
    {
        if (data.Groups.FirstOrDefault(g => data.CubesIn(g).ContainsKey("S")) is not { } group) return [];
        return AxisValues(data.CubesIn(group)["S"]);
    }

    private static double[] FarFieldOf(DataSet data)
    {
        if (!data.Groups.Contains("farfield")) return [];
        foreach (var cube in data.CubesIn("farfield").Values)
            if (cube.Axes.Any(a => a.Name == "freq")) return AxisValues(cube);
        return [];
    }

    private static double[] AxisValues(DataCube cube)
        => (cube.Axes.FirstOrDefault(a => a.Name is "f" or "freq" or "Frequency") ?? cube.Axes[0]).Values;

    // ── an older result ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-cp1-6 — a record for a run from before records existed, written beside its <c>.sNp</c> from what its files hold. Setup is
    /// null ("settings not recorded"). Refuses when no <c>.npy</c> matches the <c>.sNp</c>, and when a record is already there. It
    /// never touches the data files.
    /// </summary>
    public static (string? RecordPath, string? Refusal) Adopt(string snpPath)
    {
        var (record, recordPath, refusal) = Adoption(snpPath);
        if (record is null) return (null, refusal);
        try { File.WriteAllText(recordPath!, record.Serialize()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, $"'{Path.GetFileName(recordPath)}' could not be written: {e.Message}");
        }
        return (recordPath, null);
    }

    /// <summary>The sentence <see cref="Adopt"/> would refuse <paramref name="snpPath"/> with, or null when it would write a record. Writes nothing.</summary>
    public static string? AdoptionRefusal(string snpPath) => Adoption(snpPath).Refusal;

    private static (EmResultRecord? Record, string? RecordPath, string? Refusal) Adoption(string snpPath)
    {
        string full = Path.GetFullPath(snpPath);
        string name = Path.GetFileName(full);
        if (!File.Exists(full)) return (null, null, $"'{snpPath}' does not exist.");
        string stem = Path.GetFileNameWithoutExtension(full);
        string dir = Path.GetDirectoryName(full)!;
        string recordPath = Path.Combine(dir, stem + EmResultNames.RecordExtension);
        if (File.Exists(recordPath)) return (null, null, $"'{name}' already has a result record: '{Path.GetFileName(recordPath)}'.");

        SNP snp;
        try { snp = TouchstoneIO.ReadFile(full, readComments: false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or InvalidDataException or ArgumentException)
        {
            return (null, null, $"'{name}' could not be read: {e.Message}");
        }

        var header = ReadHeader(full);
        var candidates = AdoptionCandidates(dir, stem, header);
        DataSet? data = null;
        string? npy = null;
        foreach (string candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var (ds, _) = DataSetImporter.Import(candidate);
                if (!Matches(ds, snp)) continue;
                (data, npy) = (ds, candidate);
                break;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { }
        }
        if (data is null || npy is null)
            return (null, null, $"No .npy matches '{name}', so it cannot be adopted: looked for {string.Join(", ", candidates.Select(c => $"'{Path.GetFileName(c)}'"))} " +
                          "and found none with the same ports, frequencies and S-parameters. Run the EM setup again to get a whole result.");

        var provenance = EmSnpProvenance.TryRead(full);
        string fieldsFolder = PortFieldSet.FolderFor(full);
        var manifest = PortFieldSet.ReadManifest(fieldsFolder);
        // An older run's Touchstone says which field set is its own. One beside it from another run is not this result's.
        if (manifest is not null && header.FieldsStamp is { } own && manifest.Stamp != own) manifest = null;
        var record = new EmResultRecord
        {
            Stamp = PortFieldSet.NewStamp(),
            Adopted = true,
            Written = header.Written ?? new DateTimeOffset(File.GetLastWriteTimeUtc(full), TimeSpan.Zero),
            Product = ProductVersion(),
            Solver = new EmResultSolver { Name = header.Solver ?? "", Version = provenance?.ModelRevision ?? "" },
            Files = new EmResultFiles
            {
                Snp = Relative(recordPath, full),
                Npy = Relative(recordPath, npy),
                Fields = manifest is null ? null : Relative(recordPath, fieldsFolder),
            },
            Ports = [.. Enumerable.Range(1, snp.Ports).Select(k => new EmResultPort
            {
                Number = k,
                Name = header.PortNames.GetValueOrDefault(k, $"P{k}"),
                Z0Re = (snp.Z0PerPort is { } each && k <= each.Length ? each[k - 1] : snp.Z0).Real,
                Z0Im = (snp.Z0PerPort is { } each2 && k <= each2.Length ? each2[k - 1] : snp.Z0).Imaginary,
                Kind = header.PortKinds.GetValueOrDefault(k),
            })],
            Frequencies = new EmResultFrequencies
            {
                SweepHz = [.. SweepOf(data)],
                FarFieldHz = [.. FarFieldOf(data)],
                FieldSetHz = manifest is null ? [] : [.. manifest.Frequencies.Select(f => f.Hz)],
            },
            FarFieldExcitation = ExcitationOf(data),
            Geometry = new EmResultGeometry
            {
                GeometryHash = provenance?.GeometryHash, MeshHash = provenance?.MeshHash, PortHash = provenance?.PortHash,
                ModelRevision = provenance?.ModelRevision,
            },
        };
        return (record, recordPath, null);
    }

    private sealed record SnpHeader(string? Setup, string? Solver, DateTimeOffset? Written, Dictionary<int, string> PortNames, Dictionary<int, string> PortKinds, string? FieldsStamp);

    private static SnpHeader ReadHeader(string snpPath)
    {
        string? setup = null, solver = null;
        DateTimeOffset? written = null;
        string? fieldsStamp = null;
        var names = new Dictionary<int, string>();
        var kinds = new Dictionary<int, string>();
        foreach (string raw in File.ReadLines(snpPath))
        {
            string line = raw.TrimStart();
            if (line.StartsWith('#')) break;
            if (!line.StartsWith('!')) continue;
            line = line[1..].Trim();
            if (line.StartsWith("Generated by circuitRF - EM: ", StringComparison.Ordinal)) solver = line["Generated by circuitRF - EM: ".Length..].Trim();
            else if (line.StartsWith("circuitRF-EM setup: ", StringComparison.Ordinal)) setup = line["circuitRF-EM setup: ".Length..].Trim();
            else if (line.StartsWith("circuitRF-EM written: ", StringComparison.Ordinal) &&
                     DateTimeOffset.TryParse(line["circuitRF-EM written: ".Length..].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var w)) written = w;
            else if (OlderFieldsLine().Match(line) is { Success: true } older) fieldsStamp = older.Groups[1].Value;
            else if (PortLine().Match(line) is { Success: true } m)
            {
                int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                names[n] = m.Groups[2].Value;
                string rest = m.Groups[3].Value;
                if (rest.Contains(", wave", StringComparison.Ordinal)) kinds[n] = EmResultPort.Wave;
                else if (rest.Contains("delta gap", StringComparison.Ordinal)) kinds[n] = EmResultPort.Gap;
                else if (rest.Contains("edge,", StringComparison.Ordinal) || rest.Contains("to the ground plane", StringComparison.Ordinal)) kinds[n] = EmResultPort.Plane;
            }
        }
        return new SnpHeader(setup, solver, written, names, kinds, fieldsStamp);
    }

    [GeneratedRegex(@"^circuitRF-EM port (\d+): '([^']*)'(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex PortLine();

    /// <summary>The line a run from before records wrote to tie its Touchstone to its field set. Only adoption reads it.</summary>
    [GeneratedRegex(@"port fields:\s*'[^']+'\s+stamp\s+(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex OlderFieldsLine();

    /// <summary>Where an older run's <c>.npy</c> can be: named after the Touchstone, or, under an older Output name, which moved only
    /// the Touchstone, named after the setup in the workspace's <c>results/</c>.</summary>
    private static List<string> AdoptionCandidates(string dir, string stem, SnpHeader header)
    {
        var list = new List<string> { Path.Combine(dir, stem + EmRunService.NpyKeySuffix + ".npy") };
        if (header.Setup is not { Length: > 0 } setup) return list;
        string key = ResultsWriter.SanitizeFileNameComponent(setup);
        var folders = new List<string> { dir };
        if (Workspace.WorkspaceRootFinder.FindAncestorCws(dir) is { } cws)
            folders.Add(Path.Combine(Path.GetDirectoryName(cws)!, "results"));
        string? token = header.Solver is { } s
            ? Em3dSolvers.All.FirstOrDefault(d => d.DisplayName.Equals(s, StringComparison.OrdinalIgnoreCase))?.ResultToken : null;
        foreach (string folder in folders)
        {
            if (token is not null) list.Add(Path.Combine(folder, $"{key}.{token}{EmRunService.NpyKeySuffix}.npy"));
            list.Add(Path.Combine(folder, key + EmRunService.NpyKeySuffix + ".npy"));
        }
        return [.. list.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Does this DataSet hold this Touchstone's numbers: the same ports and frequencies, and the same S at the first and last
    /// points, to the digits a Touchstone holds?</summary>
    private static bool Matches(DataSet data, SNP snp)
    {
        if (data.Groups.FirstOrDefault(g => data.CubesIn(g).ContainsKey("S")) is not { } group) return false;
        var s = data.CubesIn(group)["S"];
        if (s.Axes.Count != 3) return false;
        double[] f = AxisValues(s);
        if (f.Length != snp.Frequencies.Length || s.Axes[1].Length != snp.Ports || s.Axes[2].Length != snp.Ports) return false;
        for (int k = 0; k < f.Length; k++)
            if (Math.Abs(f[k] - snp.Frequencies[k]) > 1e-6 * Math.Max(Math.Abs(f[k]), 1)) return false;
        if (f.Length == 0 || s.DataKind != DataKind.Complex) return false;
        var values = s.ComplexValues;
        int n = snp.Ports;
        foreach (int k in new[] { 0, f.Length - 1 })
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    if (Complex.Abs(values[(k * n + i) * n + j] - snp.Matrices[k][i, j]) > 1e-6) return false;
        return true;
    }

    // ── reading it back ───────────────────────────────────────────────────────────────────────────

    /// <summary>A setup's settings in its own words: each field of the <c>.cem</c> with its value, in file order.</summary>
    public static IReadOnlyList<(string Field, string Value)> Settings(JsonElement setup)
    {
        var list = new List<(string, string)>();
        Flatten(setup, "", list);
        return list;
    }

    /// <summary>Each field whose value differs between two flattened documents, with both values (null where it is absent).</summary>
    public static IReadOnlyList<(string Field, string? Then, string? Now)> Differences(
        IReadOnlyList<(string Field, string Value)> then, IReadOnlyList<(string Field, string Value)> now)
    {
        var a = then.ToDictionary(x => x.Field, x => x.Value, StringComparer.Ordinal);
        var b = now.ToDictionary(x => x.Field, x => x.Value, StringComparer.Ordinal);
        var found = new List<(string, string?, string?)>();
        foreach (var (field, value) in then)
            if (!b.TryGetValue(field, out var other)) found.Add((field, value, null));
            else if (other != value) found.Add((field, value, other));
        foreach (var (field, value) in now)
            if (!a.ContainsKey(field)) found.Add((field, null, value));
        return found;
    }

    /// <summary>The setup as it is on disk now, when the record's <see cref="SetupSource"/> can still be read; otherwise the sentence why.</summary>
    public (EmSetup? Setup, string? WhyNot, string? Path) CurrentSetup(string recordPath)
    {
        if (SetupSource is not { } source) return (null, "the run was not started from a saved setup", null);
        string path = Resolve(recordPath, source.File);
        if (!File.Exists(path)) return (null, $"'{source.File}' is no longer there", path);
        try
        {
            if (source.Embedded is not { } embedded) return (EmSetupPersistence.LoadFromFile(path), null, path);
            var doc = ThreeD.C3dPersistence.LoadFromFile(path);
            var read = ThreeD.C3dSetups.Read(doc).FirstOrDefault(s => s.Name == embedded);
            return read?.Setup is { } setup
                ? (ThreeD.C3dSetups.ForRun(setup, path), null, path)
                : (null, $"'{source.File}' no longer holds a setup named '{embedded}'", path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
                                     or ArgumentException or InvalidDataException or ThreeD.C3dReadException)
        {
            return (null, $"'{source.File}' could not be read: {e.Message}", path);
        }
    }

    /// <summary>
    /// What differs between the settings this run used and the setup on disk now. Empty with a null reason when they agree; a reason
    /// says why they could not be compared: settings not recorded, or a setup that cannot be read. A difference never blocks anything.
    /// </summary>
    public (IReadOnlyList<(string Field, string? Then, string? Now)> Differences, string? WhyNot) DifferencesFromCurrent(string recordPath)
    {
        if (Setup is not { } setup) return ([], SettingsNotRecorded);
        var (current, whyNot, _) = CurrentSetup(recordPath);
        if (current is null) return ([], whyNot);
        return (Differences(Settings(setup), Settings(EmSetupPersistence.ToEmbedded(current))), null);
    }

    /// <summary>What a record with no setup says about its settings.</summary>
    public const string SettingsNotRecorded = "settings not recorded";

    /// <summary>R-cp2-6 — one line of what this result is: when, which solver and quality, the sweep and the port count.</summary>
    public string Summary()
    {
        var sweep = Frequencies.SweepHz;
        string band = sweep.Count switch
        {
            0 => "no sweep",
            1 => MaterialDispersion.Hz(sweep[0]),
            _ => $"{MaterialDispersion.Hz(sweep[0])} to {MaterialDispersion.Hz(sweep[^1])}, {sweep.Count} points",
        };
        string solver = Solver.Name.Length > 0 ? Solver.Name : "solver not recorded";
        string quality = Setup is { } s && s.ValueKind == JsonValueKind.Object && QualityOf(s) is { } q ? $" {q}" : "";
        return $"{Written.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}, {solver}{quality}, {band}, {Ports.Count} port{(Ports.Count == 1 ? "" : "s")}";
    }

    /// <summary>The quality the solver's own section of the setup states; null when it states none, as a planar setup does.</summary>
    private string? QualityOf(JsonElement setup)
    {
        foreach (var section in setup.EnumerateObject())
            if (section.Value.ValueKind == JsonValueKind.Object && section.Name.Equals(Solver.Name, StringComparison.OrdinalIgnoreCase))
                foreach (var p in section.Value.EnumerateObject())
                    if (p.Name.Equals("Quality", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
        return null;
    }

    private static void Flatten(JsonElement e, string prefix, List<(string, string)> into)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) Flatten(p.Value, prefix.Length == 0 ? p.Name : $"{prefix}.{p.Name}", into);
                break;
            case JsonValueKind.Array:
                int k = 0;
                foreach (var item in e.EnumerateArray()) Flatten(item, $"{prefix}[{k++}]", into);
                break;
            case JsonValueKind.String:
                into.Add((prefix, e.GetString() ?? ""));
                break;
            default:
                into.Add((prefix, e.GetRawText()));
                break;
        }
    }
}
