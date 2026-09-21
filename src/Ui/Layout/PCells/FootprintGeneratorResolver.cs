namespace CircuitRF.Ui.Layout.PCells;

/// <summary>
/// Answers <see cref="PCellRegistry"/> for every <c>smt:</c> generator id and for nothing else —
/// brief-footprint-1-land-pattern-generator.md R-fp1-6.
///
/// <para><b>Through the resolver seam, not by adding twenty entries to the built-in dictionary.</b>
/// That dictionary is closed by design, and 69 rows of it would be a second copy of
/// <see cref="ChipLandPatternGenerator.GeneratorIds"/> that could disagree with the first.</para>
///
/// <para><b>Registered ONCE, process-wide, and never removed</b> (R-fp1-6a). The case table is a
/// constant and does not belong to a workspace, so this is not the workspace-scoped arrangement
/// MW1's R-mw1-4 gave a kit's resolver: there is nothing to unregister on workspace close, and
/// unregistering it would make built-in footprints stop resolving in whichever window was not
/// looking.</para>
///
/// <para><b>It holds no generation logic.</b> Every land pattern comes out of
/// <see cref="ChipLandPatternGenerator"/> in <c>src/Design</c>, which is what lets
/// <c>src/Cli</c> — which cannot see this class or the registry — generate the same artwork with no
/// display attached (R-fp1-6b).</para>
/// </summary>
public sealed class FootprintGeneratorResolver : Wire.IPCellGeneratorResolver
{
    public PCellGenerator? Resolve(string generatorId)
        => FootprintRef.TryParse(generatorId, out var reference, out _)
            ? ChipLandPatternGenerator.GeneratorFor(reference!)
            : null;

    /// <summary>The CANONICAL ids — one per case per density. The density-less spelling
    /// <c>smt:0402</c> still RESOLVES (it is the nominal pattern), but it is not listed: listing
    /// both would offer the same artwork twice under two names.</summary>
    public IReadOnlyCollection<string> KnownGeneratorIds => ChipLandPatternGenerator.GeneratorIds;

    public string Describe() => "circuitRF's built-in SMT land patterns (smt:<case>[@<density>])";

    /// <summary>Empty for an id this resolver owns, which is what makes a built-in footprint
    /// PLACEABLE with no parameters — a land pattern's case and density are its identity, not its
    /// parameters. Null for anything else, which is "not mine".</summary>
    public IReadOnlyDictionary<string, PCellValue>? DeclaredDefaults(string generatorId)
        => Owns(generatorId) ? new Dictionary<string, PCellValue>() : null;

    public IReadOnlyList<PCellParameterInfo>? DeclaredParameters(string generatorId)
        => Owns(generatorId) ? [] : null;

    /// <summary>
    /// The land-pattern algorithm's own version, for the generated cell's content hash. Without it,
    /// changing the geometry leaves every cell it already produced on disk and in use — the failure
    /// the built-ins' hand-maintained version table exists to prevent.
    /// </summary>
    public string? ContentKeyFor(string generatorId)
        => Owns(generatorId) ? "smt" + ChipLandPatternGenerator.AlgorithmVersion : null;

    private static bool Owns(string generatorId)
        => FootprintRef.TryParse(generatorId, out _, out _);
}
