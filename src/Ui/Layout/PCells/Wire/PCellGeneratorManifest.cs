using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Ui.Layout.PCells.Wire;

/// <summary>
/// How a kit declares that its cells' layouts are GENERATED, and by what:
/// a <c>pcell-generators.json</c> beside the kit.
///
/// <para><b>Run-time data beside the kit, never knowledge compiled into circuitRF.</b> This is the
/// same rule <c>device-provider.json</c> already follows, and for the same reason — the alternative
/// is a list of kits inside the product, which is wrong the moment somebody ships one nobody told us
/// about. The two manifests are deliberately shaped alike (an entry point, an optional interpreter
/// override, paths resolved relative to the manifest's own folder) so a kit author who has met one
/// recognises the other.</para>
///
/// <para><b>It does NOT list the generators the kit offers.</b> <c>describe</c> is the only source
/// of that, and adding a second would be a cache that can silently disagree with the script — the
/// failure mode this codebase has already been bitten by (a recorded setting outliving the thing it
/// described). The cost is that listing a kit's cells means starting its interpreter once; the
/// benefit is that the list cannot be stale.</para>
/// </summary>
public sealed class PCellGeneratorManifest
{
    public const string FileName = "pcell-generators.json";

    /// <summary>The manifest's own schema version — separate again from the wire version, because a
    /// declaration format and a byte format have no reason to move together.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Relative path (from the manifest's own folder) to the script that calls
    /// <c>circuitrf_pcell.run()</c>. Required — a manifest naming nothing to run declares nothing.</summary>
    public string Entry { get; set; } = "";

    /// <summary>
    /// An explicit interpreter to use for this kit. Null means circuitRF finds one — which is the
    /// ordinary case and the one the zero-configuration path depends on.
    ///
    /// <para>This exists because a kit whose cells need third-party packages must be able to say
    /// which environment has them. circuitRF does not install packages on a user's behalf and does
    /// not bundle an interpreter; a kit that needs a particular environment declares it and
    /// circuitRF uses it.</para>
    /// </summary>
    public string? Interpreter { get; set; }

    /// <summary>Relative paths added to <c>PYTHONPATH</c> so a kit's own modules import without the
    /// user configuring anything.</summary>
    public List<string> PythonPath { get; set; } = [];

    /// <summary>
    /// What the generators are BUILT FROM, for the content hash that decides whether an already-
    /// generated cell can be reused. Empty means the entry script's own directory — the ordinary kit
    /// layout, and the answer that needs no configuration.
    ///
    /// <para><b>Deliberately not <see cref="PythonPath"/>.</b> That may point at a shared environment
    /// the kit does not own and did not author; hashing a virtual environment on every workspace open
    /// is a cost nobody would trace back to here. A kit whose generators genuinely depend on a library
    /// it ships names it here.</para>
    /// </summary>
    public List<string> Sources { get; set; } = [];

    /// <summary>
    /// Files the generated geometry depends on but which are not source — a table of pad sizes, a
    /// profile, a device list. Declaring one is the statement that changing it changes the artwork,
    /// which is exactly what the cache needs to be told: a generator that reads a file is fine
    /// PROVIDED that file's content is part of its key, and this is how it becomes part of it.
    /// </summary>
    public List<string> DataFiles { get; set; } = [];

    // ── Reading ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the manifest in <paramref name="directory"/>, or null when there is none — which is not
    /// an error: a kit with no generated artwork simply has no manifest, and reporting that on every
    /// import would be noise on nearly every kit.
    /// </summary>
    /// <param name="problem">Non-null when a manifest was present but unusable. Distinguished from
    /// absence deliberately: "there is none" and "there is one and it is broken" need different
    /// answers from the user.</param>
    public static PCellGeneratorManifest? TryRead(string directory, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(directory)) return null;

        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return null;

        PCellGeneratorManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<PCellGeneratorManifest>(File.ReadAllText(path), JsonOpts); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = $"'{path}' could not be read: {ex.Message}";
            return null;
        }

        if (manifest is null)
        {
            problem = $"'{path}' is empty.";
            return null;
        }

        if (manifest.SchemaVersion != CurrentSchemaVersion)
        {
            // Refused rather than read hopefully: a manifest from a newer circuitRF may mean
            // something different by the same field, and guessing would launch the wrong thing.
            problem = $"'{path}' declares schema version {manifest.SchemaVersion}; this build reads " +
                      $"version {CurrentSchemaVersion}.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(manifest.Entry))
        {
            problem = $"'{path}' names no entry script, so there is nothing to run.";
            return null;
        }

        return manifest;
    }

    /// <summary>
    /// The entry script's absolute path. Relative to the MANIFEST's own folder, matching
    /// <c>device-provider.json</c>'s own rule — so a kit can be moved or copied whole and still
    /// resolve, which is what makes an installed kit repairable rather than re-importable.
    /// </summary>
    public string ResolveEntry(string manifestDirectory)
        => Path.GetFullPath(Path.Combine(manifestDirectory, Entry));

    public IReadOnlyList<string> ResolvePythonPath(string manifestDirectory)
        => [.. PythonPath.Where(p => !string.IsNullOrWhiteSpace(p))
                         .Select(p => Path.GetFullPath(Path.Combine(manifestDirectory, p)))];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };
}
