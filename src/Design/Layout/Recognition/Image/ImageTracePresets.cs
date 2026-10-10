// Saved layer maps — brief-img-3-trace-layout-image.md R-im3-8 (D7).
//
// Pictures from one source always use the same colours, so a layer map and the trace options can be saved under a name
// and offered again when a picture of the same colours is dropped: a preset matches when every one of its colours has a
// cluster within ΔE 8 and the picture has no colour it does not name. Per-user state in
// <UserStateDirectory>/image-presets/ — never in a workspace, because a preset describes a source of pictures, not a
// design. The directory is an argument (a preference is an argument, src/Design/CLAUDE.md); null is the user's own.

using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Design.Imaging;

namespace CircuitRF.Design.Layout.Recognition.Image;

/// <summary>A named layer map and the options it was made with.</summary>
public sealed record ImageTracePreset(string Name, IReadOnlyList<ImageTracePresetRow> Rows, ImageTraceOptions Options);

/// <summary>One colour of a preset and what it is read as.</summary>
public sealed record ImageTracePresetRow(string Colour, ImageLayerRole Role, IReadOnlyList<string> Layers);

public static class ImageTracePresets
{
    /// <summary>The user's own preset directory.</summary>
    public static string DefaultDirectory => UserStateDirectory.SubDir("image-presets");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>A preset of <paramref name="map"/> under <paramref name="name"/>.</summary>
    public static ImageTracePreset From(string name, ImageLayerMap map, ImageTraceOptions options) =>
        new(name, [.. map.Rows.Select(r => new ImageTracePresetRow(r.Hex, r.Role, r.Layers))], options);

    /// <summary>Writes <paramref name="preset"/> as <c>&lt;name&gt;.json</c>, replacing one of the same name.</summary>
    public static string Save(ImageTracePreset preset, string? directory = null)
    {
        string dir = directory ?? DefaultDirectory;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, FileName(preset.Name));
        File.WriteAllText(path, JsonSerializer.Serialize(preset, Json));
        return path;
    }

    /// <summary>Every readable preset, by name. An unreadable file is skipped.</summary>
    public static IReadOnlyList<ImageTracePreset> Load(string? directory = null)
    {
        string dir = directory ?? DefaultDirectory;
        if (!Directory.Exists(dir)) return [];
        var list = new List<ImageTracePreset>();
        foreach (string file in Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                if (JsonSerializer.Deserialize<ImageTracePreset>(File.ReadAllText(file), Json) is { Name.Length: > 0 } p) list.Add(p);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
        }
        return list;
    }

    /// <summary>The presets that fit <paramref name="clusters"/>: every preset colour within ΔE 8 of a cluster, and every
    /// cluster within ΔE 8 of a preset colour.</summary>
    public static IReadOnlyList<ImageTracePreset> Matching(ColourClusterSet clusters, string? directory = null) =>
        [.. Load(directory).Where(p => Matches(p, clusters))];

    public static bool Matches(ImageTracePreset preset, ColourClusterSet clusters)
    {
        double lim = ImageLayerMap.MatchDeltaE;
        var labs = preset.Rows.Select(r => TryParse(r.Colour, out int rgb) ? CieLab.FromRgb(rgb) : (Lab?)null).ToList();
        if (labs.Count == 0 || labs.Any(l => l is null)) return false;
        return labs.All(l => clusters.Clusters.Any(c => c.Lab.DeltaE(l!.Value) <= lim))
               && clusters.Clusters.All(c => labs.Any(l => c.Lab.DeltaE(l!.Value) <= lim));
    }

    /// <summary><paramref name="preset"/>'s map laid over <paramref name="clusters"/> — an edited map, so a re-run keeps it.</summary>
    public static ImageLayerMap Apply(ImageTracePreset preset, ColourClusterSet clusters)
    {
        var rows = preset.Rows.Select(r => new ImageLayerRow(-1, TryParse(r.Colour, out int rgb) ? rgb : 0, 0, r.Role, r.Layers)).ToList();
        return new ImageLayerMap(rows, Edited: true).Rebind(clusters);
    }

    private static bool TryParse(string hex, out int rgb) =>
        int.TryParse(hex.TrimStart('#'), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out rgb);

    private static string FileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new([.. name.Trim().Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c)]);
        return (safe.Length == 0 ? "preset" : safe) + ".json";
    }
}
