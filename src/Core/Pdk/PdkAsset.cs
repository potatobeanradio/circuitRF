namespace CircuitRF.Core.Pdk;

/// <summary>What role a discovered file plays in a process design kit.</summary>
public enum PdkAssetKind
{
    /// <summary>A hierarchical netlist describing cells and their connectivity.</summary>
    Netlist,
    /// <summary>Model data a device provider reads (measured tables, fitted coefficients, …).</summary>
    ModelData,
    /// <summary>Frequency-domain network data (Touchstone and friends).</summary>
    NetworkData,
    /// <summary>Schematic symbol artwork — the drawn appearance of a part.</summary>
    SymbolArtwork,
    /// <summary>Physical layout artwork — drawn geometry on process layers.</summary>
    LayoutArtwork,
    /// <summary>A small raster image intended as a palette/browser icon.</summary>
    PaletteIcon,
    /// <summary>Layer definitions: names, purposes, stream numbers, display style.</summary>
    LayerTechnology,
    /// <summary>Documentation, licence text, release notes.</summary>
    Documentation,
    /// <summary>Recognised as belonging to the kit, but of no known role.</summary>
    Other,
}

/// <summary>
/// How far circuitRF can get with an asset.
///
/// <para>The three states are deliberately distinct. "I do not know what this is" and "I know
/// exactly what this is and cannot read it yet" are completely different messages to a user, and
/// only the second one tells them (and us) what to build next.</para>
/// </summary>
public enum PdkAssetSupport
{
    /// <summary>circuitRF can read it now.</summary>
    Supported,
    /// <summary>The format is identified, but no reader exists yet. Name it and say so.</summary>
    RecognizedNotSupported,
    /// <summary>Not identified at all.</summary>
    Unrecognized,
}

/// <summary>
/// One file found while importing a kit, together with what circuitRF made of it.
/// </summary>
/// <param name="RelativePath">Path relative to the kit root, always with '/' separators.</param>
/// <param name="Kind">The role this file plays.</param>
/// <param name="Support">How far circuitRF can get with it.</param>
/// <param name="FormatName">
/// Human-readable format name, shown to the user. Describes the FORMAT, never the supplier — the
/// same import path has to serve every kit, and circuitRF holds no knowledge of any particular one.
/// </param>
/// <param name="Detail">Optional extra context, e.g. what a reader would need.</param>
public sealed record PdkAsset(
    string          RelativePath,
    PdkAssetKind    Kind,
    PdkAssetSupport Support,
    string          FormatName,
    string          Detail = "")
{
    public string FileName => RelativePath.Contains('/')
        ? RelativePath[(RelativePath.LastIndexOf('/') + 1)..]
        : RelativePath;
}

/// <summary>Severity of a finding in an import report.</summary>
public enum PdkFindingSeverity { Info, Warning, Blocker }

/// <summary>
/// Something the user should know about an import.
///
/// <para><paramref name="SuggestedAction"/> is not decoration. A kit that cannot be imported is
/// only a useful message if it says what would make it importable — otherwise the user is left with
/// "unsupported" and nowhere to go.</para>
/// </summary>
public sealed record PdkFinding(
    PdkFindingSeverity Severity,
    string             Summary,
    string             SuggestedAction = "");
