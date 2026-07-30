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

    /// <summary>
    /// Content-addressing version for a generator's OWN geometry algorithm (independent of the
    /// pcell-contract.md "PCellContractVersion" and of the parameters/technology already in the
    /// hash key) — see <see cref="GeneratedCellStore.GetOrCreate"/>'s doc comment for why this
    /// exists: without it, fixing a generator's algorithm (e.g. a coordinate-sign bug) never
    /// invalidates an already-written on-disk generated cell that was created with the OLD, buggy
    /// output, because <c>(GeneratorId, parameters, tech, layers)</c> alone is unchanged. Bump the
    /// entry for a generator whenever its geometry OUTPUT changes for some existing parameter set
    /// (not for a change that only affects NEW parameters/behavior). Defaults to 1 for any
    /// generator not listed here.
    /// </summary>
    private static readonly Dictionary<string, int> _generatorVersions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // R-L5f-5 (brief-L5-followups.md §2): branch direction flipped from +Y to -Y.
            { "MTEE", 2 },
        };

    public static int GeneratorVersion(string generatorId)
        => _generatorVersions.TryGetValue(generatorId, out var v) ? v : 1;
}
