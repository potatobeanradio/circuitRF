using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Ui.Theming;

/// <summary>Reads and writes .ccolor theme files (System.Text.Json, human-diffable, stable key ordering).</summary>
public static class ColorThemeIo
{
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented              = true,
        PropertyNameCaseInsensitive= true,
    };

    // ── Write ─────────────────────────────────────────────────────────────────

    public static string Save(ColorTheme theme)
    {
        var (light, dark) = theme.GetRoleMaps();
        var dto = new ColorThemeDto
        {
            FormatVersion = CurrentFormatVersion,
            Name  = theme.Name,
            // Sort keys alphabetically for stable, human-diffable output.
            Light = ToSortedDict(light),
            Dark  = ToSortedDict(dark),
        };
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    public static void SaveFile(string path, ColorTheme theme)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Save(theme));
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public static ColorTheme Load(string json)
    {
        var dto = JsonSerializer.Deserialize<ColorThemeDto>(json, JsonOptions)
            ?? throw new ColorThemeFormatException("Failed to parse .ccolor: null result.");

        if (dto.FormatVersion != CurrentFormatVersion)
            throw new ColorThemeFormatException(
                $".ccolor format_version {dto.FormatVersion} is not supported (expected {CurrentFormatVersion}). " +
                "Regenerate the file or update circuitRF.");

        return new ColorTheme(
            dto.Name ?? "Custom",
            dto.Light ?? new Dictionary<string, Rgba>(),
            dto.Dark  ?? new Dictionary<string, Rgba>());
    }

    public static ColorTheme LoadFile(string path) => Load(File.ReadAllText(path));

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Dictionary<string, Rgba> ToSortedDict(IReadOnlyDictionary<string, Rgba> src)
        => src.OrderBy(kv => kv.Key, StringComparer.Ordinal)
              .ToDictionary(kv => kv.Key, kv => kv.Value);

    // ── DTO (private, serialization only) ─────────────────────────────────────

    private sealed class ColorThemeDto
    {
        [JsonPropertyName("format_version")] [JsonPropertyOrder(0)] public int FormatVersion { get; set; }
        [JsonPropertyName("name")]           [JsonPropertyOrder(1)] public string?                   Name  { get; set; }
        [JsonPropertyName("light")]          [JsonPropertyOrder(2)] public Dictionary<string, Rgba>? Light { get; set; }
        [JsonPropertyName("dark")]           [JsonPropertyOrder(3)] public Dictionary<string, Rgba>? Dark  { get; set; }
    }
}

/// <summary>Thrown when a .ccolor file has an unsupported format_version or cannot be parsed.</summary>
public sealed class ColorThemeFormatException : Exception
{
    public ColorThemeFormatException(string message) : base(message) { }
}
