using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Design.Cells;

namespace CircuitRF.Design.VendorLibraries.ModelLibrary;

/// <summary>
/// <c>library.clib</c> — what a model library says about itself (brief vendorlib-6 §3a, R-vl6-1a).
/// Every key is optional except <see cref="Format"/> and <see cref="FormatVersion"/>; a library
/// without the file takes its folder's name.
/// </summary>
public sealed record ModelLibraryManifest
{
    public const string FormatName     = "circuitrf-model-library";
    public const int    CurrentVersion = 1;

    public string Format         { get; init; } = FormatName;
    public int    FormatVersion  { get; init; } = CurrentVersion;
    public string Name           { get; init; } = "";
    public string Publisher      { get; init; } = "";
    public string LibraryVersion { get; init; } = "";
    public string License        { get; init; } = "";
    public string Url            { get; init; } = "";
    /// <summary>A publisher saying "copy it to change it". It greys out Edit Vendor Model and nothing else.</summary>
    public bool   ReadOnly       { get; init; }

    /// <summary>Keys this build does not know, kept so a round trip loses nothing a newer writer put there.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; init; }

    internal static readonly string[] Required = [nameof(Format), nameof(FormatVersion)];

    /// <summary>The record, or the sentence saying why it was refused.</summary>
    public static (ModelLibraryManifest? Manifest, string? Refusal) Read(string path)
    {
        var (m, refusal) = ModelManifestIo.Read<ModelLibraryManifest>(path, FormatName, Required);
        return (m, refusal);
    }

    public static void Write(string path, ModelLibraryManifest manifest) => ModelManifestIo.Write(path, manifest);
}

/// <summary>One cell parameter as the index lists it: its name and its default, as written in the <c>.cnl</c>.</summary>
public sealed record ModelPartParameter
{
    public string Name    { get; init; } = "";
    public string Default { get; init; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; init; }
}

/// <summary>The frequency range a model is valid over, as quantities with units (<c>"20 GHz"</c>).</summary>
public sealed record ModelPartFrequency
{
    public string Low  { get; init; } = "";
    public string High { get; init; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; init; }
}

/// <summary>A part Export replaced by an S-parameter file (brief 11). Provenance only — nothing reads it to run.</summary>
public sealed record ModelPartConversion
{
    public string Instance { get; init; } = "";
    /// <summary>The cell whose schematic placed it: the model's own, or a sub-cell's folder name.</summary>
    public string Cell     { get; init; } = "";
    public string Type     { get; init; } = "";
    public string File     { get; init; } = "";
    /// <summary>Its parameters as they were written (brief vendorlib-11 R-vl11-2c).</summary>
    public string Parameters { get; init; } = "";
    /// <summary>The technology it was sampled on, by name — never its path.</summary>
    public string Technology { get; init; } = "";
    /// <summary>The frequency plan, as the <c>sparam</c> directive spells it.</summary>
    public string Frequency  { get; init; } = "";
    /// <summary>The circuitRF version that converted it.</summary>
    public string Version    { get; init; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; init; }
}

/// <summary>
/// <c>part.cpart</c> — what finds and describes a model (brief vendorlib-6 §3b, R-vl6-1a). Its presence
/// is what makes a folder a model folder (<see cref="ModelFolder.Is"/>).
///
/// <para><b>Nothing here changes a result</b> (D7). What runs is the <c>.cnl</c>; where the index needs a
/// fact the <c>.cnl</c> also states (pins, parameters) both are written from one source and
/// <see cref="ModelFolderCheck"/> proves they agree.</para>
/// </summary>
public sealed record ModelPartManifest
{
    public const string FormatName     = "circuitrf-model";
    public const int    CurrentVersion = 1;

    public const string KindActive  = "Active";
    public const string KindPassive = "Passive";

    /// <summary>The glyphs a part may ask for; empty means the model's own symbol.</summary>
    public static readonly IReadOnlyList<string> Glyphs = ["", "R", "C", "L", "FET"];

    public string Format        { get; init; } = FormatName;
    public int    FormatVersion { get; init; } = CurrentVersion;
    /// <summary>The part number. Equals the model folder's name.</summary>
    public string Part          { get; init; } = "";
    /// <summary>The top <c>define</c> in the <c>.cnl</c>.</summary>
    public string Cell          { get; init; } = "";
    public string Kind          { get; init; } = KindActive;
    public string Description   { get; init; } = "";
    public List<string> Pins    { get; init; } = [];
    public List<ModelPartParameter> Parameters { get; init; } = [];
    public string Glyph         { get; init; } = "";
    public string Value         { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelPartFrequency? Frequency { get; init; }
    public string Conditions    { get; init; } = "";
    public string Footprint     { get; init; } = "";
    public string CaseMetric    { get; init; } = "";
    public string CaseImperial  { get; init; } = "";
    public int    Revision      { get; init; }
    /// <summary>Written by Export only: <see cref="ModelPartHash.Compute"/> of the folder as exported.</summary>
    public string PublishedHash { get; init; } = "";
    public List<ModelPartConversion> Converted { get; init; } = [];
    /// <summary>The platforms a compiled-only model has a build for, as runtime identifiers (brief vendorlib-15
    /// R-vl15-1b). A describing fact only (D7): the palette reads it to grey a part this platform has no build of,
    /// and check proves it matches <c>models/&lt;rid&gt;/</c>. Absent for a model with source.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Builds { get; init; }
    /// <summary>Compile netlist (brief vendorlib-16 R-vl16-2d): <c>sha256:&lt;hex&gt;</c> of the generated source every
    /// platform's build was compiled from. A second platform's build is added only when its source hashes the same.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompiledFrom { get; init; }
    /// <summary>The platforms whose build the proof ran on (R-vl16-2i); a build listed in <see cref="Builds"/> and not
    /// here was compiled elsewhere and never run by its publisher.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Proved { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; init; }

    internal static readonly string[] Required =
        [nameof(Format), nameof(FormatVersion), nameof(Part), nameof(Cell), nameof(Kind), nameof(Pins)];

    /// <summary>The record, or the sentence saying why it was refused.</summary>
    public static (ModelPartManifest? Manifest, string? Refusal) Read(string path)
    {
        var (m, refusal) = ModelManifestIo.Read<ModelPartManifest>(path, FormatName, Required);
        if (m is null) return (null, refusal);

        if (m.Part.Trim().Length == 0) return (null, $"{ModelFolder.PartFileName}: \"Part\" is empty.");
        if (m.Cell.Trim().Length == 0) return (null, $"{ModelFolder.PartFileName}: \"Cell\" is empty.");
        if (m.Kind is not (KindActive or KindPassive))
            return (null, $"{ModelFolder.PartFileName}: \"Kind\" is \"{m.Kind}\"; it is \"{KindActive}\" or \"{KindPassive}\".");
        if (!Glyphs.Contains(m.Glyph, StringComparer.Ordinal))
            return (null, $"{ModelFolder.PartFileName}: \"Glyph\" is \"{m.Glyph}\"; it is one of R, C, L, FET, or empty for the model's own symbol.");
        return (m, null);
    }

    public static void Write(string path, ModelPartManifest manifest) => ModelManifestIo.Write(path, manifest);
}

/// <summary>The one read and write both manifests go through, so their refusals read alike.</summary>
internal static class ModelManifestIo
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // `Unknown` must not swallow a misspelt KNOWN key silently: matching is exact, so `"part"`
        // lands in Unknown and the required-key test below names "Part" as missing.
        PropertyNameCaseInsensitive = false,
    };

    public static (T? Record, string? Refusal) Read<T>(string path, string formatName, string[] required) where T : class
    {
        string file = Path.GetFileName(path);
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return (null, $"{file} could not be read ({ex.Message})."); }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { return (null, $"{file} is not valid JSON ({ex.Message})."); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, $"{file} is not a JSON object.");

            var missing = required.Where(k => !doc.RootElement.TryGetProperty(k, out _)).ToList();
            if (missing.Count > 0)
                return (null, $"{file} has no {string.Join(", ", missing.Select(k => $"\"{k}\""))}.");

            var format = doc.RootElement.GetProperty("Format");
            if (format.ValueKind != JsonValueKind.String || format.GetString() != formatName)
                return (null, $"{file}: \"Format\" is {format.GetRawText()}; it is \"{formatName}\".");

            var version = doc.RootElement.GetProperty("FormatVersion");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int v) || v < 1)
                return (null, $"{file}: \"FormatVersion\" is {version.GetRawText()}; it is a whole number from 1.");
        }

        T? record;
        try { record = JsonSerializer.Deserialize<T>(text, ReadOptions); }
        catch (JsonException ex) { return (null, $"{file}: {ex.Message}"); }
        if (record is null) return (null, $"{file} is empty.");

        int known = record switch
        {
            ModelLibraryManifest l => l.FormatVersion,
            ModelPartManifest p    => p.FormatVersion,
            _                      => 1,
        };
        int current = record is ModelLibraryManifest ? ModelLibraryManifest.CurrentVersion : ModelPartManifest.CurrentVersion;
        if (known > current)
            return (null, $"{file} is format version {known}, and this build of circuitRF reads version {current}. Update circuitRF to read it.");

        return (record, null);
    }

    private static readonly JsonSerializerOptions ReadOptions = new(Options)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static void Write<T>(string path, T record)
        => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(record, Options) + "\n");
}
