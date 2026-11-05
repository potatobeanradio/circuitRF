// brief-cosim-pattern-2 — an EM Field Data block's result record, as elaboration reads it.
//
// The block works on a result saved on disk and on nothing else: no .c3d, .cem or .clay is read to simulate it. So the factory has to
// reach the record from src/Core, below the project that writes it. The record's schema is EmResultRecord's in src/Design, a reference
// topic; this reads the part a run needs. The check that a result holds together lives here too, and EmResultRecord.Verify is this
// check, so there is one statement of it.
//
// FILE TIMES ARE A CACHE KEY HERE, NEVER A VERDICT. A sweep re-elaborates at every point and an optimizer at every evaluation, so a
// verdict is kept while no file's size or write time has moved. Whether a result is stale is only ever answered by its stamps.

using System.Collections.Concurrent;
using System.Text.Json;
using RfCore;
using RfCore.Export;

namespace CircuitRF.Core.Devices;

/// <summary>One file of a result that does not hold up.</summary>
public sealed record EmResultProblem(string What, string Path, string Sentence);

/// <summary>What each file of a result carries, and whether the files of one result hold together.</summary>
public static class EmResultIntegrity
{
    /// <summary>The <c>.npy</c>'s metadata cube holding the run's stamp.</summary>
    public const string StampCube = "__ResultStamp";

    /// <summary>The <c>.sNp</c> provenance line holding the run's stamp.</summary>
    public const string SnpStampPrefix = "circuitRF-EM stamp: ";

    /// <summary>A field set's manifest, by name in its folder.</summary>
    public const string ManifestFile = "fields.json";

    /// <summary>The stamp a <c>.sNp</c>'s header carries; null when it carries none.</summary>
    public static string? SnpStamp(string snpPath)
    {
        try
        {
            foreach (string raw in File.ReadLines(snpPath))
            {
                string line = raw.TrimStart();
                if (line.StartsWith('#')) break;
                if (!line.StartsWith('!')) continue;
                line = line[1..].Trim();
                if (line.StartsWith(SnpStampPrefix, StringComparison.Ordinal)) return line[SnpStampPrefix.Length..].Trim();
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The stamp a <c>.npy</c> carries, read from its metadata without its arrays; null when it carries none.</summary>
    public static string? NpyStamp(string npyPath)
    {
        try
        {
            using var meta = JsonDocument.Parse(DataSetImporter.ReadMetaJson(npyPath));
            foreach (var field in meta.RootElement.EnumerateObject())
            {
                if (field.Value.ValueKind != JsonValueKind.Object || !field.Value.TryGetProperty("cube", out var cube) ||
                    cube.GetString() != StampCube) continue;
                return field.Value.GetProperty("axes")[0].GetProperty("labels")[0].GetString();
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException
                                     or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or EndOfStreamException)
        {
            return null;
        }
    }

    private static JsonDocument? Manifest(string folder)
    {
        string path = Path.Combine(folder, ManifestFile);
        try { return File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// Every file of a result that is missing, truncated or carries another run's stamp, in publish order; empty when the result holds
    /// together. An adopted record's files were written before stamps, so only whether they are whole can be asked about.
    /// </summary>
    /// <param name="sweepPoints">The points the record says the Touchstone holds; a Touchstone cut short holds fewer.</param>
    public static IReadOnlyList<EmResultProblem> Check(string recordPath, string stamp, bool adopted, string snp, string npy, string? fields, int sweepPoints)
    {
        var found = new List<EmResultProblem>();
        string dir = Path.GetDirectoryName(Path.GetFullPath(recordPath))!;
        string Full(string relative) => Path.GetFullPath(Path.Combine(dir, relative));

        void Stamped(string what, string relative, string path, string? carried)
        {
            if (adopted || carried == stamp) return;
            found.Add(new(what, path, $"its {what} '{relative}' {(carried is null ? "carries no stamp" : $"is from another run (stamp {carried})")}, and the record's stamp is {stamp}"));
        }

        if (fields is { Length: > 0 })
        {
            const string what = "field set";
            string folder = Full(fields);
            if (!Directory.Exists(folder)) found.Add(new(what, folder, $"its {what} '{fields}' is missing"));
            else if (Manifest(folder) is not { } manifest) found.Add(new(what, folder, $"its {what} '{fields}' has no readable {ManifestFile}"));
            else
                using (manifest)
                {
                    Stamped(what, fields, folder, manifest.RootElement.TryGetProperty("Stamp", out var s) ? s.GetString() : null);
                    foreach (var (name, bytes) in ManifestFiles(manifest.RootElement))
                    {
                        string file = Path.Combine(folder, name);
                        if (!File.Exists(file)) found.Add(new(what, file, $"its {what} '{fields}' has lost '{name}'"));
                        else if (new FileInfo(file).Length is var size && size != bytes)
                            found.Add(new(what, file, $"its {what} '{fields}' holds '{name}' truncated: {size:N0} bytes where its run wrote {bytes:N0}"));
                    }
                }
        }

        string npyPath = Full(npy);
        if (!File.Exists(npyPath)) found.Add(new(".npy", npyPath, $"its .npy '{npy}' is missing"));
        else if (DataSetImporter.IsTruncated(npyPath)) found.Add(new(".npy", npyPath, $"its .npy '{npy}' is truncated"));
        else Stamped(".npy", npy, npyPath, NpyStamp(npyPath));

        string snpPath = Full(snp);
        if (!File.Exists(snpPath)) found.Add(new(".sNp", snpPath, $"its .sNp '{snp}' is missing"));
        else if (TouchstoneProblem(snpPath, sweepPoints) is { } problem) found.Add(new(".sNp", snpPath, $"its .sNp '{snp}' {problem}"));
        else Stamped(".sNp", snp, snpPath, SnpStamp(snpPath));
        return found;
    }

    private static string? TouchstoneProblem(string path, int sweepPoints)
    {
        try
        {
            int held = TouchstoneCache.Get(path).Frequencies.Length;
            return held < sweepPoints ? $"is truncated: it holds {held} of the run's {sweepPoints} frequencies" : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            return $"could not be read ({e.Message})";
        }
    }

    private static IEnumerable<(string Name, long Bytes)> ManifestFiles(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("Frequencies", out var freqs) || freqs.ValueKind != JsonValueKind.Array) yield break;
        foreach (var f in freqs.EnumerateArray())
        {
            if (!f.TryGetProperty("Files", out var files) || files.ValueKind != JsonValueKind.Array) continue;
            foreach (var file in files.EnumerateArray())
                if (file.TryGetProperty("Name", out var n) && n.GetString() is { Length: > 0 } name && file.TryGetProperty("Bytes", out var b) && b.TryGetInt64(out long bytes))
                    yield return (name, bytes);
        }
    }

    /// <summary>The files whose size and write time decide whether a kept verdict still stands.</summary>
    internal static List<string> FilesOf(string recordPath, string snp, string npy, string? fields)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(recordPath))!;
        var list = new List<string> { recordPath, Path.GetFullPath(Path.Combine(dir, snp)), Path.GetFullPath(Path.Combine(dir, npy)) };
        if (fields is not { Length: > 0 }) return list;
        string folder = Path.GetFullPath(Path.Combine(dir, fields));
        list.Add(Path.Combine(folder, ManifestFile));
        using var manifest = Manifest(folder);
        if (manifest is not null) list.AddRange(ManifestFiles(manifest.RootElement).Select(f => Path.Combine(folder, f.Name)));
        return list;
    }
}

/// <summary>
/// R-cp2-3 — what an EM Field Data block needs from its <c>.cemr</c>: the Touchstone it stamps, its ports, and what the result holds.
/// </summary>
public sealed class EmFieldDataRecord
{
    /// <summary>The block's <c>.cnl</c> spelling.</summary>
    public const string TypeName = "EmFieldData";

    /// <summary>A result record's extension.</summary>
    public const string Extension = ".cemr";

    /// <summary>What the block is called to a person.</summary>
    public const string DisplayName = "EM Field Data";

    public required string RecordPath { get; init; }
    public required string SnpPath { get; init; }
    public required IReadOnlyList<string> PortNames { get; init; }
    public required int FarFieldPoints { get; init; }
    public required int FieldSetPoints { get; init; }

    /// <summary>The per-port field set's folder, absolute; null when the run kept none.</summary>
    public string? FieldsFolder { get; init; }

    public int PortCount => PortNames.Count;

    /// <summary>"EM Field Data 'X1.EM1'": the elaborated instance path, verbatim.</summary>
    public static string Describe(string instancePath)
        => string.IsNullOrWhiteSpace(instancePath) ? DisplayName : $"{DisplayName} '{instancePath}'";

    private sealed class Dto
    {
        public int FormatVersion { get; set; }
        public string Stamp { get; set; } = "";
        public bool Adopted { get; set; }
        public FilesDto? Files { get; set; }
        public List<PortDto>? Ports { get; set; }
        public FrequenciesDto? Frequencies { get; set; }
    }

    private sealed class FilesDto
    {
        public string Snp { get; set; } = "";
        public string Npy { get; set; } = "";
        public string? Fields { get; set; }
    }

    private sealed class PortDto
    {
        public int Number { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class FrequenciesDto
    {
        public List<double>? SweepHz { get; set; }
        public List<double>? FarFieldHz { get; set; }
        public List<double>? FieldSetHz { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record Kept(long Ticks, long Length, EmFieldDataRecord? Record, string? Refusal, string[] Files, long[] Fingerprint);

    private static readonly ConcurrentDictionary<string, Kept> _kept = new(StringComparer.Ordinal);

    /// <summary>
    /// R-cp2-5 — the record at <paramref name="path"/>, or the sentence refusing it, which names the file and is written to follow the
    /// block's name: there is no such record, it holds nothing a plain SnP does not, a file it names is missing or truncated, or
    /// the files are from different runs.
    /// </summary>
    public static (EmFieldDataRecord? Record, string? Refusal) Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (null, "no result record is chosen. Open its parameters and pick the .cemr a 3D run wrote.");
        string name = Path.GetFileName(path);
        if (Regexes.IsTouchstone(name))
            return (null, $"'{name}' is a Touchstone file, not a result record. Pick the .cemr beside it; for a run older than records, " +
                          $"`circuitrf em --adopt` writes one, and so does this block's file picker.");
        var info = new FileInfo(path);
        if (Directory.Exists(path)) return (null, $"its File names a folder, not a result record: '{path}'.");
        if (!info.Exists) return (null, $"there is no result record '{path}'. Run its EM setup, or pick another record.");

        string key = Path.GetFullPath(path);
        if (_kept.TryGetValue(key, out var kept) && kept.Ticks == info.LastWriteTimeUtc.Ticks && kept.Length == info.Length
            && Fingerprint(kept.Files).AsSpan().SequenceEqual(kept.Fingerprint))
            return (kept.Record, kept.Refusal);

        var (record, refusal, files) = Read(key, name);
        _kept[key] = new Kept(info.LastWriteTimeUtc.Ticks, info.Length, record, refusal, files, Fingerprint(files));
        return (record, refusal);
    }

    private static (EmFieldDataRecord? Record, string? Refusal, string[] Files) Read(string path, string name)
    {
        Dto? dto;
        try { dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path), Json); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, $"'{name}' could not be read: {e.Message}", []);
        }
        if (dto is null || dto.Stamp.Length == 0 || dto.Files is not { Snp.Length: > 0 } files)
            return (null, $"'{name}' is not a result record: it has no Stamp or no Files.", []);
        if (dto.FormatVersion != 1)
            return (null, $"'{name}' is a result record of format {dto.FormatVersion}, and this build reads format 1.", []);

        var watched = EmResultIntegrity.FilesOf(path, files.Snp, files.Npy, files.Fields).ToArray();
        int farField = dto.Frequencies?.FarFieldHz?.Count ?? 0, fieldSet = dto.Frequencies?.FieldSetHz?.Count ?? 0;
        string snp = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, files.Snp));
        if (farField == 0 && fieldSet == 0)
            return (null, $"'{name}' holds neither a far field nor a field set, so it has nothing a plain SnP does not: place " +
                          $"'{Path.GetFileName(snp)}' in an SnP for its S-parameters. To keep fields, turn on the setup's Radiation pattern or " +
                          "Keep per-port fields and run it again.", watched);

        var problems = EmResultIntegrity.Check(path, dto.Stamp, dto.Adopted, files.Snp, files.Npy, files.Fields, dto.Frequencies?.SweepHz?.Count ?? 0);
        if (problems.Count > 0)
            return (null, $"'{name}' does not hold together: {string.Join("; ", problems.Select(p => p.Sentence))}. Run its EM setup again.", watched);

        var ports = (dto.Ports ?? []).OrderBy(p => p.Number).Select(p => p.Name).ToList();
        if (ports.Count == 0) return (null, $"'{name}' lists no ports.", watched);
        return (new EmFieldDataRecord
        {
            RecordPath = path, SnpPath = snp, PortNames = ports, FarFieldPoints = farField, FieldSetPoints = fieldSet,
            FieldsFolder = files.Fields is { Length: > 0 } f ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, f)) : null,
        }, null, watched);
    }

    /// <summary>The port names a record states, for drawing its pins; null when it cannot be read at all. A result that does not hold
    /// together still has the names its record lists: the refusal belongs to the run.</summary>
    public static IReadOnlyList<string>? ReadPortNames(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        // A render asks for every block on every frame: a stat each, and the file only when it changes.
        string key = info.FullName;
        if (_names.TryGetValue(key, out var kept) && kept.Ticks == info.LastWriteTimeUtc.Ticks && kept.Length == info.Length) return kept.Names;
        IReadOnlyList<string>? names = null;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path), Json);
            names = dto?.Ports is { Count: > 0 } ports ? [.. ports.OrderBy(p => p.Number).Select(p => p.Name)] : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        _names[key] = (info.LastWriteTimeUtc.Ticks, info.Length, names);
        return names;
    }

    private static readonly ConcurrentDictionary<string, (long Ticks, long Length, IReadOnlyList<string>? Names)> _names = new(StringComparer.Ordinal);

    private static long[] Fingerprint(string[] files)
    {
        var f = new long[2 * files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            var info = new FileInfo(files[i]);
            if (!info.Exists) { f[2 * i] = -1; continue; }
            f[2 * i] = info.Length;
            f[2 * i + 1] = info.LastWriteTimeUtc.Ticks;
        }
        return f;
    }

    private static class Regexes
    {
        public static bool IsTouchstone(string name)
        {
            string e = Path.GetExtension(name);
            return e.Length >= 4 && (e[1] is 's' or 'S') && (e[^1] is 'p' or 'P') && e[2..^1].All(char.IsDigit);
        }
    }
}
