// The derived-data cache beside a .cmc (brief-modelcomposer-3 R-mc3-4, R-mc3-6; overview D11).
//
//   <name>.cmc-cache/
//     index.json      per data set: its .npy, the importer settings it was read with, and per source the
//                     path, size, modification time and SHA-256
//     <k>.npy         the data set's NormalisedData, through circuitRF's own .npy writer
//
// OPEN READS NO SOURCE when every source's size and modification time match the index — that is the whole
// point of the cache, and CmcCache.SourceReads is the counter that holds it. A source whose size or time
// moved is hashed: the same SHA-256 refreshes the index and keeps the cached data; a different one
// re-imports the set, with one status line. A MISSING source with a cache keeps working, with one warning;
// a missing source with no cache is a refusal that names it.
//
// ATOMIC. A rebuild writes a whole new folder beside the old one and swaps it in by rename; a failure or a
// cancellation before the swap deletes the new folder and leaves the old cache exactly as it was. The cache
// holds nothing a re-import cannot rebuild — exclusions are the user's and live in the .cmc.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CircuitRF.Engine;
using RfCore.Export;

namespace CircuitRF.Design.Modeling;

/// <summary>What opening a document's data produced: each set's data by name, and what to tell the user.</summary>
public sealed record CmcOpenResult(
    IReadOnlyDictionary<string, NormalisedData> Data,
    IReadOnlyList<string> Status,
    IReadOnlyList<string> Warnings);

public sealed class CmcCache
{
    public const string FolderSuffix = ".cmc-cache";
    public const string IndexFile = "index.json";
    private const int IndexVersion = 1;

    private readonly string _cmcPath;
    private readonly string _dir;
    private long _sourceReads;

    public CmcCache(string cmcPath)
    {
        _cmcPath = Path.GetFullPath(cmcPath);
        _dir = Path.GetDirectoryName(_cmcPath)!;
        Folder = FolderFor(_cmcPath);
    }

    /// <summary>The cache folder of <paramref name="cmcPath"/>: <c>&lt;name&gt;.cmc-cache</c> beside it.</summary>
    public static string FolderFor(string cmcPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cmcPath))!, Path.GetFileNameWithoutExtension(cmcPath) + FolderSuffix);

    public string Folder { get; }

    /// <summary>Source files opened by this cache — hashed or imported. Zero after an open that the cache answered.</summary>
    public long SourceReads => Interlocked.Read(ref _sourceReads);

    private sealed record SourceStamp(string Path, long Size, long ModifiedTicks, string Sha256);

    private sealed record SetEntry(string Name, string File, string Settings, IReadOnlyList<SourceStamp> Sources);

    /// <summary>
    /// Every data set's normalised data, from the cache where it is current and from its sources where it is
    /// not; exclusions from <paramref name="doc"/> applied. Throws <see cref="DataImportException"/> for a
    /// missing source with no cache, and <see cref="OperationCanceledException"/> when cancelled — in both
    /// cases the cache on disk is unchanged.
    /// </summary>
    public CmcOpenResult Open(CmcDocument doc, RunControl? rc = null)
    {
        var index = ReadIndex();
        var status = new List<string>();
        var warnings = new List<string>();
        var data = new Dictionary<string, NormalisedData>(StringComparer.Ordinal);
        var entries = new List<SetEntry>();
        var fresh = new Dictionary<string, NormalisedData>(StringComparer.Ordinal);   // file → data to write
        // A large-signal set is validation only (MC-11): the validation reads it from its sources, so it is never cached.
        bool dirty = index is null || index.Count != doc.DataSets.Count(CmcStamps.FeedsFits);

        for (int i = 0; i < doc.DataSets.Count; i++)
        {
            rc?.ThrowIfCancellationRequested();
            var set = doc.DataSets[i];
            if (!CmcStamps.FeedsFits(set)) continue;
            string settings = SettingsKey(set);
            var old = index?.FirstOrDefault(e => e.Name == set.Name && e.Settings == settings);
            bool cached = old is not null && File.Exists(Path.Combine(Folder, old.File));
            string file = $"{i}.npy";
            if (old?.File != file) dirty = true;

            string? reason = old is null ? null : "";
            var stamps = new List<SourceStamp>();
            var missing = new List<string>();
            if (cached)
            {
                foreach (string src in set.Sources)
                {
                    string path = DataImport.ResolveSource(_dir, src);
                    var was = old!.Sources.FirstOrDefault(x => x.Path == src);
                    if (!File.Exists(path))
                    {
                        missing.Add(src);
                        if (was is null) { reason = null; break; }
                        stamps.Add(was);
                        continue;
                    }
                    var info = new FileInfo(path);
                    if (was is not null && was.Size == info.Length && was.ModifiedTicks == info.LastWriteTimeUtc.Ticks)
                    {
                        stamps.Add(was);
                        continue;
                    }
                    string sha = Hash(path);
                    if (was is not null && was.Sha256 == sha)
                    {
                        stamps.Add(was with { Size = info.Length, ModifiedTicks = info.LastWriteTimeUtc.Ticks });
                        dirty = true;
                        continue;
                    }
                    reason = $"{set.Name}: {src} changed; re-imported";
                    break;
                }
            }

            if (cached && reason == "")
            {
                foreach (string m in missing) warnings.Add($"{set.Name}: {m} is missing; using the cached data.");
                var nd = NormalisedData.FromDataSet(DataSetImporter.Import(Path.Combine(Folder, old!.File)).DataSet);
                data[set.Name] = nd.WithExclusions(set.Exclusions);
                entries.Add(new SetEntry(set.Name, file, settings, stamps));
                if (old.File != file) fresh[file] = nd;
                continue;
            }

            // Import from the sources. A missing one with no usable cache is a refusal naming it.
            foreach (string src in set.Sources)
                if (!File.Exists(DataImport.ResolveSource(_dir, src)))
                    throw new DataImportException(src, $"it is missing, and {set.Name} has no cached data to use instead.");
            Interlocked.Add(ref _sourceReads, set.Sources.Count);
            var result = DataImport.Import(set, _dir, rc);
            stamps = [.. set.Sources.Select(src => Stamp(src))];
            status.Add(reason is { Length: > 0 } ? $"{reason}: {result.Summary}" : $"{set.Name}: {result.Summary}");
            warnings.AddRange(result.Notes.Select(n => $"{set.Name}: {n}"));
            data[set.Name] = result.Data.WithExclusions(set.Exclusions);
            entries.Add(new SetEntry(set.Name, file, settings, stamps));
            fresh[file] = result.Data;
            dirty = true;
        }

        if (dirty)
        {
            rc?.ThrowIfCancellationRequested();
            WriteAtomically(entries, fresh, index);
        }
        return new CmcOpenResult(data, status, warnings);
    }

    /// <summary>Each cached source's SHA-256 as the index recorded it, by (data set, source path); empty with no index.</summary>
    public IReadOnlyDictionary<(string Set, string Source), string> SourceHashes()
    {
        var map = new Dictionary<(string, string), string>();
        foreach (var e in ReadIndex() ?? [])
            foreach (var s in e.Sources) map[(e.Name, s.Path)] = s.Sha256;
        return map;
    }

    private SourceStamp Stamp(string src)
    {
        string path = DataImport.ResolveSource(_dir, src);
        var info = new FileInfo(path);
        return new SourceStamp(src, info.Length, info.LastWriteTimeUtc.Ticks, Hash(path));
    }

    private string Hash(string path)
    {
        Interlocked.Increment(ref _sourceReads);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    /// <summary>What a cached entry was read with: the kind, the sources and the importer settings, as the .cmc writes them.</summary>
    private static string SettingsKey(CmcDataSet set)
    {
        var probe = new CmcDocument
        {
            Kind = set.Kind == DataSetKind.PassiveSParams ? CmcKind.Passive : CmcKind.Active,
            // The plane stays: a capacitance table is valid at the intrinsic plane only (R-mc20-2). The key reads no condition.
            DataSets = [set with { Name = "_", Conditions = DataConditions.None with { ReferencePlane = set.Conditions.ReferencePlane }, Exclusions = [] }],
        };
        using var doc = JsonDocument.Parse(CmcFile.ToBytes(probe));
        var e = doc.RootElement.GetProperty("DataSets")[0];
        return $"{e.GetProperty("Kind").GetString()} {e.GetProperty("Sources").GetRawText()} {e.GetProperty("Import").GetRawText()}"
            .Replace("\n", "").Replace("  ", "");
    }

    // ── index.json ────────────────────────────────────────────────────────────

    private List<SetEntry>? ReadIndex()
    {
        string path = Path.Combine(Folder, IndexFile);
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.GetProperty("FormatVersion").GetInt32() != IndexVersion) return null;
            var list = new List<SetEntry>();
            foreach (var e in root.GetProperty("DataSets").EnumerateArray())
                list.Add(new SetEntry(
                    e.GetProperty("Name").GetString()!, e.GetProperty("File").GetString()!, e.GetProperty("Settings").GetString()!,
                    [.. e.GetProperty("Sources").EnumerateArray().Select(s => new SourceStamp(
                        s.GetProperty("Path").GetString()!, s.GetProperty("Size").GetInt64(),
                        s.GetProperty("ModifiedTicks").GetInt64(), s.GetProperty("Sha256").GetString()!))]));
            return list;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;    // a damaged index is a cache that is not there; it is derived and is rebuilt
        }
    }

    private static byte[] IndexBytes(IReadOnlyList<SetEntry> entries)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();
            w.WriteNumber("FormatVersion", IndexVersion);
            w.WriteStartArray("DataSets");
            foreach (var e in entries)
            {
                w.WriteStartObject();
                w.WriteString("Name", e.Name);
                w.WriteString("File", e.File);
                w.WriteString("Settings", e.Settings);
                w.WriteStartArray("Sources");
                foreach (var s in e.Sources)
                {
                    w.WriteStartObject();
                    w.WriteString("Path", s.Path);
                    w.WriteNumber("Size", s.Size);
                    w.WriteNumber("ModifiedTicks", s.ModifiedTicks);
                    w.WriteString("Sha256", s.Sha256);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>Writes the new cache into a sibling folder, then swaps it in. Nothing is changed until the swap.</summary>
    private void WriteAtomically(IReadOnlyList<SetEntry> entries, IReadOnlyDictionary<string, NormalisedData> fresh,
                                 IReadOnlyList<SetEntry>? oldIndex)
    {
        string tmp = Folder + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        string? retired = null;
        var carried = new List<string>();
        try
        {
            Directory.CreateDirectory(tmp);
            foreach (var e in entries)
            {
                string target = Path.Combine(tmp, e.File);
                if (fresh.TryGetValue(e.File, out var nd))
                {
                    using var fs = File.Create(target);
                    DataSetExporter.ExportNpy(nd.ToDataSet(), fs);
                }
                else File.Copy(Path.Combine(Folder, oldIndex!.First(o => o.Name == e.Name).File), target);
            }
            File.WriteAllBytes(Path.Combine(tmp, IndexFile), IndexBytes(entries));

            if (Directory.Exists(Folder))
            {
                // Every subfolder is carried across: the intrinsic results (keyed by the data they came from, so they stay
                // valid where it did not change and are never read where it did), and the fitted arrays of the current
                // and charge steps, which nothing can rebuild but a fit. A source whose timestamp moved with its bytes
                // unchanged — a copy, a checkout — rewrites the index, and that once deleted every fit.
                foreach (string sub in Directory.GetDirectories(Folder))
                {
                    string name = Path.GetFileName(sub);
                    Directory.Move(sub, Path.Combine(tmp, name));
                    carried.Add(name);
                }
                retired = Folder + ".old-" + Guid.NewGuid().ToString("N")[..8];
                Directory.Move(Folder, retired);
            }
            Directory.Move(tmp, Folder);
        }
        catch
        {
            // A carried subfolder's only copy is in tmp now, so a swap that failed part-way (a file held open on Windows)
            // puts the old cache back before tmp goes; tmp is kept if anything could not be moved back.
            bool restored = true;
            try
            {
                if (retired is not null && Directory.Exists(retired) && !Directory.Exists(Folder)) Directory.Move(retired, Folder);
                foreach (string name in carried)
                {
                    string from = Path.Combine(tmp, name), to = Path.Combine(Folder, name);
                    if (Directory.Exists(from) && Directory.Exists(Folder) && !Directory.Exists(to)) Directory.Move(from, to);
                    restored &= !Directory.Exists(from);
                }
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { restored = false; }
            if (restored) try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch (IOException) { }
            throw;
        }
        if (retired is not null)
            try { Directory.Delete(retired, recursive: true); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
    }
}
