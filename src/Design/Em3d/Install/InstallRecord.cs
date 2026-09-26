using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Em3d.Install;

/// <summary>One upstream source as the install used it: where from, and whether its checksum was checked.</summary>
/// <param name="Url">The upstream URL.</param>
/// <param name="FetchedBy">Who fetched it: <c>circuitRF</c> for an archive this installer downloaded, or the
/// step that did (<c>git</c>, <c>Spack</c>, the upstream build script).</param>
/// <param name="Sha256">The checksum upstream publishes, or null when it publishes none.</param>
/// <param name="Verified">True only when THIS installer computed the file's SHA-256 and it matched.</param>
/// <param name="Note">Why it was not verified, or how it was pinned instead (a git tag checked against its
/// commit).</param>
public sealed record InstallSource(string Url, string FetchedBy, string? Sha256, bool Verified, string? Note);

/// <summary>
/// <c>install.json</c> — what the assistant put where (brief-em3d-24 R-em3d24-2f). Brief 25 removes a
/// solver BY this record and by nothing else, which is why it names the home, every root it wrote into,
/// and what it measured: F0 found a live package inside a dead Spack tree, so "remove the directory that
/// looks unused" is not a rule an uninstaller may follow (<c>em-3d-f0-findings.md</c> Q12).
/// </summary>
public sealed record InstallRecord
{
    /// <summary>The record format. Bumped only by a change an older reader would misread.</summary>
    public int Format { get; init; } = 1;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SolverTool Tool { get; init; }

    /// <summary>The validated release installed, e.g. <c>0.18.1</c>.</summary>
    public string Version { get; init; } = "";

    /// <summary>The recipe's id, e.g. <c>palace-0.18.1-macos-arm64</c>.</summary>
    public string Recipe { get; init; } = "";

    /// <summary>The home: everything the install wrote is under it.</summary>
    public string Home { get; init; } = "";

    /// <summary>The program discovery starts, absolute.</summary>
    public string Program { get; init; } = "";

    /// <summary>Palace only: the Spack install tree inside the home, whose database names the MPI Palace
    /// was linked against (<see cref="SpackInstalls.MpiLauncherFor"/>).</summary>
    public string? SpackInstallTree { get; init; }

    /// <summary>What discovery reported for the installed program when the install was checked.</summary>
    public string Identified { get; init; } = "";

    public DateTimeOffset InstalledAt { get; init; }

    /// <summary>Bytes under <see cref="Home"/> when the install was published, measured.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Wall clock from the first step to publication, measured.</summary>
    public double ElapsedSeconds { get; init; }

    public IReadOnlyList<InstallSource> Sources { get; init; } = [];

    /// <summary>brief-em3d-26 — the Linux subsystem distribution the home is in, or null for one on this
    /// machine. When set, <see cref="Home"/>, <see cref="Program"/> and <see cref="SpackInstallTree"/> are
    /// Linux paths inside it. Omitted from a native record, so no existing record changes.</summary>
    public string? Distribution { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented          = true,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The record at <paramref name="path"/>, or null when there is none or it does not read —
    /// an unreadable record makes a home unpublished, never a crash.</summary>
    public static InstallRecord? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var r = JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(path), Json);
            return r is { Program.Length: > 0, Home.Length: > 0 } ? r : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Writes the record atomically — the write that publishes an in-place home.</summary>
    public void Write(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, Json));
}
