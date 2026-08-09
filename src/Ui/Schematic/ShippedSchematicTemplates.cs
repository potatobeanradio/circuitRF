using System.Linq;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// The schematic templates circuitRF ships out of the box — real, authored <c>.csch</c> files (see
/// <c>src/Ui/resources/schematic-templates/</c>), embedded into the assembly as plain .NET
/// <c>EmbeddedResource</c> assets and parsed at runtime through the SAME
/// <see cref="SchematicPersistence.Deserialize"/> reader a user's own <c>.csch</c> goes through.
/// Never transcribed into C#: a second representation of the same authored content would drift from
/// it, and a template that the schematic editor could not itself open would be worse than no
/// template at all. This mirrors <c>CircuitRF.Ui.Layout.ShippedTechnologies</c> exactly — read that
/// class first if this one needs changing.
///
/// Embedding (rather than a folder beside the executable) is also what makes templates present in a
/// compiled/published build: a loose content folder does not survive <c>dotnet publish</c> without
/// its own copy rule, and would then be missing from exactly the build a user actually runs.
///
/// Deliberately plain .NET <c>EmbeddedResource</c>, not Avalonia's <c>AvaloniaResource</c>/
/// <c>AssetLoader</c> — this file is framework-free (no Avalonia reference) and
/// <c>AssetLoader.Open</c> throws with no live Avalonia platform, which is the same constraint
/// <c>SkiaFonts.cs</c> already records for embedded fonts.
/// </summary>
public static class ShippedSchematicTemplates
{
    private const string ResourceSuffix = ".csch";

    private static readonly Lazy<IReadOnlyList<ShippedSchematicTemplate>> _entries = new(Discover);

    /// <summary>Every shipped template, sorted by <see cref="ShippedSchematicTemplate.Id"/> for a
    /// stable, deterministic order — never enumeration order, which .NET does not guarantee across
    /// runtimes or platforms.</summary>
    public static IReadOnlyList<ShippedSchematicTemplate> All => _entries.Value;

    /// <summary>The template's raw authored JSON.</summary>
    public static string LoadRawJson(ShippedSchematicTemplate entry)
    {
        var asm = typeof(ShippedSchematicTemplates).Assembly;
        using var stream = asm.GetManifestResourceStream(entry.ResourceName)
            ?? throw new InvalidOperationException($"Embedded schematic template \"{entry.ResourceName}\" not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Parses one template into a fresh <see cref="SchematicEditModel"/> through the ordinary
    /// <c>.csch</c> reader. <paramref name="schematicDirectory"/> is the directory the resulting
    /// schematic will be SAVED into — it is what any relative <c>CellRef</c> resolves against, so a
    /// caller that already knows the destination should pass it rather than leaving the model
    /// unable to resolve until its first save.
    /// </summary>
    public static SchematicEditModel Load(ShippedSchematicTemplate entry, string? schematicDirectory = null)
    {
        var (model, _, _) = SchematicPersistence.Deserialize(LoadRawJson(entry), schematicDirectory);
        return model;
    }

    /// <summary>Convenience overload keyed by <see cref="ShippedSchematicTemplate.Id"/>.</summary>
    public static SchematicEditModel Load(string id, string? schematicDirectory = null)
    {
        var entry = All.FirstOrDefault(e => e.Id == id)
            ?? throw new InvalidOperationException($"No shipped schematic template named \"{id}\".");
        return Load(entry, schematicDirectory);
    }

    private static IReadOnlyList<ShippedSchematicTemplate> Discover()
    {
        var asm = typeof(ShippedSchematicTemplates).Assembly;
        var list = new List<ShippedSchematicTemplate>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.EndsWith(ResourceSuffix, StringComparison.Ordinal)) continue;

            // MSBuild's default embedded-resource name is <RootNamespace>.<folder.path>.<FileName>:
            // folder separators become dots, but the filename itself is never split further. None of
            // the shipped template filenames contain a '.' beyond the ".csch" extension, so the
            // segment between the LAST remaining dot and the extension is exactly the file stem,
            // however many namespace/folder segments precede it.
            string withoutExt = name[..^ResourceSuffix.Length];
            int lastDot = withoutExt.LastIndexOf('.');
            string id = lastDot >= 0 ? withoutExt[(lastDot + 1)..] : withoutExt;

            list.Add(new ShippedSchematicTemplate(id, name, DisplayNameFor(id)));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return list;
    }

    /// <summary>
    /// The template's own file stem, made readable: underscores are word separators in a filesystem
    /// name and spaces everywhere else. Nothing else is rewritten — hyphens and capitalisation are
    /// the author's own ("FET_S-Parameters" reads as "FET S-Parameters"), so adding a template is
    /// dropping a well-named file in the folder and nothing more.
    /// </summary>
    internal static string DisplayNameFor(string id) => id.Replace('_', ' ');
}

/// <summary>
/// One schematic template shipped inside the assembly. <see cref="Id"/> is the file stem — stable
/// and filesystem-safe; <see cref="DisplayName"/> is what the New Cell / New Schematic picker shows;
/// <see cref="ResourceName"/> is the raw embedded-resource manifest name, internal to loading.
/// </summary>
public sealed record ShippedSchematicTemplate(string Id, string ResourceName, string DisplayName);
