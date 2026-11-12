using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CircuitRF.Ui.DataDisplay;

/// <summary>
/// The Data Display templates circuitRF ships — real, authored <c>.cddt</c> files in
/// <c>src/Ui/resources/cdd-templates/</c>, embedded as plain .NET <c>EmbeddedResource</c>s for the reasons
/// <see cref="Schematic.ShippedSchematicTemplates"/> gives (present in a published build, readable with no
/// Avalonia platform). Read through the same <c>.cdd</c> reader as a user's own template.
/// </summary>
public static class ShippedDataDisplayTemplates
{
    private const string Prefix = "CircuitRF.Ui.CddTemplates.";
    private const string Suffix = ".cddt";

    private static readonly Lazy<IReadOnlyList<string>> _stems = new(() =>
        typeof(ShippedDataDisplayTemplates).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(Suffix, StringComparison.Ordinal))
            .Select(n => n[Prefix.Length..^Suffix.Length])
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList());

    /// <summary>Every shipped template's file stem, in a stable order.</summary>
    public static IReadOnlyList<string> Stems => _stems.Value;

    /// <summary>The shipped template <paramref name="stem"/>'s JSON.</summary>
    /// <exception cref="InvalidOperationException">No such template is embedded.</exception>
    public static string LoadJson(string stem)
    {
        using var stream = typeof(ShippedDataDisplayTemplates).Assembly.GetManifestResourceStream(Prefix + stem + Suffix)
            ?? throw new InvalidOperationException($"This build ships no Data Display template called '{stem}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
