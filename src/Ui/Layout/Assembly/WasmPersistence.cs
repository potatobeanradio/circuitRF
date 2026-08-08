using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout.Assembly;

// ──────────────────────────────────────────────────────────────────────────────
//  .wasm file format — rev 1 (alpha, no back-compat per policy).
//  Clones TechPersistence's conventions exactly: System.Text.Json, WriteIndented,
//  JsonStringEnumConverter, PascalCase with no naming policy, a format_version that is REJECTED when
//  newer rather than migrated, nothing derived persisted, and every write through AtomicFile.
//
//  One practical note, raised in wbond.md §8.1 and adopted as specified: `.wasm` is also
//  WebAssembly's extension, so OS file associations, editors and MIME sniffing may claim it and web
//  results for "wasm file" will be about something else. It is unambiguous inside circuitRF, where
//  documents are resolved by ROLE (a workspace's assembly rule reference) rather than by extension.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>The on-disk shape. Kept separate from <see cref="WasmFile"/> for the same reason
/// <c>CtechFile</c> is separate from <see cref="Technology"/>: the file carries a version the model
/// has no business holding.</summary>
public sealed class WasmDocumentFile
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public List<WasmRule> Machine { get; set; } = [];
    public List<WasmRule> Process { get; set; } = [];
    public List<WasmRule> Material { get; set; } = [];
    public List<long> AllowedDiametersNm { get; set; } = [];
    public List<string> AllowedMetals { get; set; } = [];
    public List<WasmEnvelope> Envelopes { get; set; } = [];
}

/// <summary>Reads and writes `.wasm` assembly rule files. Framework-free (no Avalonia / Skia).</summary>
public static class WasmPersistence
{
    /// <summary>The conventional extension. One constant, so the picker, the scanner and the
    /// resolver cannot disagree about what a `.wasm` is.</summary>
    public const string Extension = ".wasm";

    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    // ── Write ─────────────────────────────────────────────────────────────────

    public static string Serialize(WasmFile wasm)
        => JsonSerializer.Serialize(ToFileModel(wasm), JsonOpts);

    public static void SaveToFile(string path, WasmFile wasm)
        => AtomicFile.WriteAllText(path, Serialize(wasm));

    // ── Read ──────────────────────────────────────────────────────────────────

    public static WasmFile Deserialize(string json)
    {
        var file = JsonSerializer.Deserialize<WasmDocumentFile>(json, JsonOpts)
            ?? throw new InvalidDataException("Failed to deserialize .wasm file.");

        // Refused BY NAME and by number, never migrated. A newer file may state rules in a
        // vocabulary this build cannot evaluate, and quietly reading the parts it recognises would
        // produce a check that looks complete and is not — the one failure mode a rule checker must
        // never have.
        if (file.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $".wasm format_version {file.FormatVersion} is newer than " +
                $"expected {CurrentFormatVersion}. Update the application.");

        return FromFileModel(file);
    }

    public static WasmFile LoadFromFile(string path)
        => Deserialize(GzipTextFile.ReadAllTextAutoGzip(path));

    // ── Convert WasmFile <-> WasmDocumentFile ─────────────────────────────────

    private static WasmDocumentFile ToFileModel(WasmFile w) => new()
    {
        FormatVersion      = CurrentFormatVersion,
        Name               = w.Name,
        Machine            = [.. w.Machine],
        Process            = [.. w.Process],
        Material           = [.. w.Material],
        AllowedDiametersNm = [.. w.AllowedDiametersNm],
        AllowedMetals      = [.. w.AllowedMetals],
        Envelopes          = [.. w.Envelopes],
    };

    private static WasmFile FromFileModel(WasmDocumentFile f) => new()
    {
        Name               = f.Name,
        Machine            = [.. f.Machine],
        Process            = [.. f.Process],
        Material           = [.. f.Material],
        AllowedDiametersNm = [.. f.AllowedDiametersNm],
        AllowedMetals      = [.. f.AllowedMetals],
        Envelopes          = [.. f.Envelopes],
    };
}
