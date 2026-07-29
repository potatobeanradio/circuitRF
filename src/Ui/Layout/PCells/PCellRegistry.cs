namespace CircuitRF.Ui.Layout.PCells;

/// <summary>
/// Maps a generator id to its <see cref="PCellGenerator"/>. §0/guardrails of
/// brief-L5a-pcell-contract-and-microstrip.md: "no scripting host, no PDK loading, no third-party
/// PCell mechanism" — this is a small closed, built-in dispatcher (mirrors
/// <c>ComponentModelFactory</c>'s registry shape), not an extensibility point. The generator id is
/// the same string used as the microstrip <c>ComponentModel</c>'s type name in
/// <c>ComponentModelFactory</c> ("MLIN", "MBEND", "MTEE", "MCROSS"), so the artwork and electrical
/// sides of one component are keyed identically.
/// </summary>
public static class PCellRegistry
{
    private static readonly Dictionary<string, PCellGenerator> _generators =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "MLIN",   MlinPCell.Generate   },
            { "MBEND",  MBendPCell.Generate  },
            { "MTEE",   MTeePCell.Generate   },
            { "MCROSS", MCrossPCell.Generate },
            { "MTAPER", MTaperPCell.Generate },
            { "MKLOPF", MKlopfPCell.Generate },
        };

    public static bool TryGet(string generatorId, out PCellGenerator generator)
        => _generators.TryGetValue(generatorId, out generator!);

    public static IReadOnlyCollection<string> KnownGeneratorIds => _generators.Keys;
}
