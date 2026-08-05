using System.Security.Cryptography;
using System.Text;

namespace CircuitRF.Ui.Layout.PCells.Wire;

/// <summary>
/// A content hash over everything a kit's generators are built from — its scripts and whatever data
/// files it declares.
///
/// <para><b>This is not a hypothetical failure mode; it has already happened here once.</b>
/// <see cref="GeneratedCellStore"/> originally keyed a generated cell on
/// <c>(generator, parameters, technology, layers)</c> alone, so fixing a generator's own geometry bug
/// never invalidated the on-disk cells built by the buggy version — the fix landed, the tests passed,
/// and the artwork did not change. That was solved for built-ins by a hand-maintained version number
/// somebody has to remember to bump. **A generator that is a FILE THE USER EDITS cannot have a
/// hand-maintained version**, so the number becomes a hash of the thing itself.</para>
///
/// <para><b>What is hashed is what the kit DECLARES</b>, defaulting to the entry script's own
/// directory. A shared environment on <c>PYTHONPATH</c> is deliberately NOT hashed: the kit does not
/// own it, it can be enormous, and hashing a virtual environment on every workspace open would be
/// the sort of cost nobody traces back to here. A kit that wants its own library included says so.</para>
/// </summary>
public static class PCellGeneratorContentHash
{
    /// <summary>
    /// Beyond this, the declared source set is not a kit's scripts — it is a directory somebody
    /// pointed at by accident. See <see cref="Compute"/> for what happens then.
    /// </summary>
    public const int MaxFiles = 2000;

    /// <summary>Total bytes read before the same conclusion is drawn.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>Extensions taken when a declared source is a DIRECTORY. An explicit file is taken
    /// whatever it is — that is what declaring it means.</summary>
    private static readonly string[] SourceExtensions = [".py"];

    /// <summary>
    /// Hashes <paramref name="manifest"/>'s declared sources and data files.
    ///
    /// <para>The manifest itself is included: changing the entry point or the declared data is a
    /// change to what the generators are, even when no script was touched.</para>
    /// </summary>
    /// <param name="problem">
    /// Non-null when the declared set was too large to hash. The caller must then treat the
    /// generators as having NO stable content key — see <see cref="PCellWorkerResolver"/>, which
    /// substitutes a per-session one so cells regenerate rather than being wrongly reused. A partial
    /// hash presented as a complete one is the worst available answer: it looks stable and is not.
    /// </param>
    public static string Compute(string manifestDirectory, PCellGeneratorManifest manifest, out string? problem)
    {
        problem = null;

        var files = new List<string>();
        if (!CollectInto(files, manifestDirectory, manifest, out problem))
            return "";

        // Sorted by the path each file is named RELATIVE to the manifest, so the hash does not
        // change when the kit is moved or copied — which it routinely is, and which must not
        // regenerate every cell in the workspace.
        var entries = files
            .Select(f => (Relative: Relative(manifestDirectory, f), Absolute: f))
            .OrderBy(e => e.Relative, StringComparer.Ordinal)
            .ToList();

        using var sha = SHA256.Create();
        var buffer = new byte[64 * 1024];
        long total = 0;

        foreach (var (relative, absolute) in entries)
        {
            // The NAME is hashed as well as the bytes, so renaming a file — which can change which
            // module a script imports — is a change.
            byte[] name = Encoding.UTF8.GetBytes(relative + "\n");
            sha.TransformBlock(name, 0, name.Length, null, 0);

            try
            {
                using var stream = File.OpenRead(absolute);
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > MaxBytes)
                    {
                        problem = $"The PCell generators in '{manifestDirectory}' declare more than " +
                                  $"{MaxBytes / (1024 * 1024)} MB of source; their cells will be " +
                                  "regenerated every session rather than cached.";
                        return "";
                    }
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be read cannot be proven unchanged. Refusing to produce a key is
                // the safe direction — the alternative silently reuses a cell built from a version of
                // a file nobody can now see.
                problem = $"'{absolute}' could not be read, so the PCell generators in " +
                          $"'{manifestDirectory}' have no stable content key: {ex.Message}";
                return "";
            }
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!)[..12].ToLowerInvariant();
    }

    private static bool CollectInto(
        List<string> files, string manifestDirectory, PCellGeneratorManifest manifest, out string? problem)
    {
        problem = null;

        void Add(string path)
        {
            if (File.Exists(path)) files.Add(Path.GetFullPath(path));
        }

        // The manifest itself: changing the entry point or the declared data is a change to what the
        // generators are, even with every script untouched.
        Add(Path.Combine(manifestDirectory, PCellGeneratorManifest.FileName));

        // Declared sources, defaulting to the entry script's own directory — the ordinary kit layout,
        // and the answer that needs no configuration.
        var roots = manifest.Sources.Count > 0
            ? manifest.Sources.Select(s => Path.GetFullPath(Path.Combine(manifestDirectory, s))).ToList()
            : [Path.GetDirectoryName(manifest.ResolveEntry(manifestDirectory)) ?? manifestDirectory];

        foreach (string root in roots)
        {
            if (File.Exists(root)) { Add(root); continue; }
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> found;
            try { found = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
            catch (Exception ex) { problem = $"'{root}' could not be listed: {ex.Message}"; return false; }

            foreach (string file in found)
            {
                if (!SourceExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;
                files.Add(Path.GetFullPath(file));
                if (files.Count > MaxFiles)
                {
                    problem = $"The PCell generators in '{manifestDirectory}' declare more than " +
                              $"{MaxFiles} source files, which is a directory pointed at by accident " +
                              "rather than a kit; their cells will be regenerated every session " +
                              "rather than cached.";
                    return false;
                }
            }
        }

        // Declared data files, taken whatever they are — declaring one is the statement that the
        // geometry depends on it.
        foreach (string declared in manifest.DataFiles)
        {
            string path = Path.GetFullPath(Path.Combine(manifestDirectory, declared));
            if (File.Exists(path)) { files.Add(path); continue; }
            if (!Directory.Exists(path)) continue;

            try { files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)); }
            catch (Exception ex) { problem = $"'{path}' could not be listed: {ex.Message}"; return false; }

            if (files.Count > MaxFiles)
            {
                problem = $"The PCell generators in '{manifestDirectory}' declare more than {MaxFiles} " +
                          "files; their cells will be regenerated every session rather than cached.";
                return false;
            }
        }

        return true;
    }

    private static string Relative(string root, string path)
    {
        try { return Path.GetRelativePath(root, path).Replace('\\', '/'); }
        catch { return Path.GetFileName(path); }
    }
}
