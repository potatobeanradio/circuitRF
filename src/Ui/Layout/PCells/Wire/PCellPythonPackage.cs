using System.IO;

namespace CircuitRF.Ui.Layout.PCells.Wire;

/// <summary>
/// Where circuitRF's own Python package (<c>circuitrf_pcell</c> and <c>cni</c>) lives, so a kit's
/// manifest never has to say.
///
/// <para><b>Why circuitRF supplies this rather than the kit.</b> A manifest cannot know where
/// circuitRF was installed, and asking a kit author to write an absolute path into a file they ship
/// makes the kit machine-specific — the exact opposite of "reference it where it lies". So the
/// resolver puts this directory on the interpreter's path itself, and a kit's own
/// <c>pythonPath</c> names only the kit.</para>
///
/// <para><b>It goes FIRST, ahead of anything the manifest names, and that is deliberate.</b> The
/// package and the host are versioned together — the wire version is a constant in both — so a kit
/// shipping its own older copy must not shadow the one that matches this build. A mismatch would
/// otherwise surface as a refusal naming two version numbers, with nothing pointing at the stale
/// copy that caused it.</para>
/// </summary>
public static class PCellPythonPackage
{
    private const string PackageMarker = "circuitrf_pcell";

    /// <summary>
    /// The directory to put on the interpreter's path, or null when this build cannot find its own
    /// package — reported by the caller, never thrown: a missing package costs a kit's generated
    /// artwork, and every other generator, built-in and the design itself are untouched.
    /// </summary>
    public static string? RootDirectory { get; } = Locate();

    private static string? Locate()
    {
        // Deployed beside the application: the package is copied next to the executable, so a shipped
        // build needs no source tree at all.
        string beside = Path.Combine(System.AppContext.BaseDirectory, "pcell-python");
        if (IsPackageRoot(beside)) return beside;

        // A development tree: walk up from the build output to the repository's own tools folder.
        // Bounded rather than unbounded — a walk that never stops is a hang on a machine where the
        // layout is not what this expects.
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        for (int depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tools", "pcell-python");
            if (IsPackageRoot(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>Checked by the package's own presence, never by the folder's name — a folder called
    /// <c>pcell-python</c> holding something else must not be adopted silently.</summary>
    private static bool IsPackageRoot(string directory)
        => File.Exists(Path.Combine(directory, PackageMarker, "__init__.py"));

    /// <summary>
    /// A content hash of this package, for a generated cell's identity.
    ///
    /// <para><b>Why a kit's own scripts are not the whole of what built its cells.</b> This package
    /// is the other half: it is what turns a vendor cell into geometry, and it decides things that
    /// end up recorded IN the cell — which parameters the run treated as outputs, and what it derived
    /// them to. Change that and an on-disk cell built by the previous version is stale in exactly the
    /// way <see cref="PCellGeneratorContentHash"/> exists to prevent, with none of the kit's own
    /// files having moved. A derived value that appeared for a newly-placed part and not for the
    /// identical part already in the design is how this was found.</para>
    ///
    /// <para>Empty when the package cannot be located or read — the caller then contributes nothing,
    /// which is the behaviour every generated cell already had.</para>
    /// </summary>
    public static string ContentKey { get; } = ComputeContentKey();

    private static string ComputeContentKey()
    {
        if (RootDirectory is not { } root) return "";
        try
        {
            var files = Directory.EnumerateFiles(root, "*.py", SearchOption.AllDirectories)
                                 .Where(f => !f.Contains("__pycache__", StringComparison.Ordinal))
                                 .Select(f => (Relative: Path.GetRelativePath(root, f).Replace('\\', '/'), Absolute: f))
                                 .OrderBy(e => e.Relative, StringComparer.Ordinal)
                                 .ToList();
            if (files.Count == 0) return "";

            using var sha = System.Security.Cryptography.SHA256.Create();
            var buffer = new byte[64 * 1024];
            foreach (var (relative, absolute) in files)
            {
                byte[] name = System.Text.Encoding.UTF8.GetBytes(relative + "\n");
                sha.TransformBlock(name, 0, name.Length, null, 0);
                using var stream = File.OpenRead(absolute);
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    sha.TransformBlock(buffer, 0, read, null, 0);
            }
            sha.TransformFinalBlock([], 0, 0);
            return Convert.ToHexString(sha.Hash!)[..12].ToLowerInvariant();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
