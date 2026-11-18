using System.Text.RegularExpressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Circuit;

/// <summary>
/// What a run adds when a file it reads is missing: which document in the workspace writes it, or that none does.
/// <see cref="DocumentPath"/> is that document, for a window to offer opening; null when nothing writes the file.
/// </summary>
public sealed record MissingFileWriter(string Sentence, string? DocumentPath);

/// <summary>
/// The writer of a file a run needed and did not find. Asked only on that failure; a run that finds its files never scans.
///
/// <para><b>Nothing is matched by name.</b> Each candidate is asked where it writes, through the function its own run writes
/// with (<see cref="EmRunService.ResolveSnpBasePath"/>, <see cref="Em3dRunService.SnpBasePath"/>, a TRL analysis's fixture
/// paths), and that path is compared with the missing one. A Touchstone file that merely looks like a result, a vendor's
/// <c>.s2p</c> for instance, is attributed to nothing.</para>
///
/// <para>A Touchstone writer names its file by its port count, which only its geometry knows. So a match on everything but
/// the <c>.sNp</c> is checked against the results folder: a file of the same stem and another port count there is a result
/// this block cannot read, and the sentence says so instead of asking for a run that would write the same file again.</para>
/// </summary>
public static class MissingFileWriters
{
    private static readonly Regex Touchstone = new(@"\.s(\d+)p$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The sentence for a Touchstone file nothing in the workspace writes.</summary>
    public const string NoWriter = "Nothing in this workspace writes this file.";

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// The writer of <paramref name="missingFile"/> among the workspace's TRL benches, EM setups and 3D views' setups, or for a
    /// Touchstone file that none writes, <see cref="NoWriter"/>. Null when there is no workspace to look in, or the file is
    /// not a Touchstone file and no TRL analysis writes it.
    /// </summary>
    public static MissingFileWriter? Explain(string missingFile, string? workspaceRoot)
    {
        if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot)) return null;

        if (TrlRun.FindWriter(missingFile, workspaceRoot) is { } trl) return new MissingFileWriter(trl.Sentence, trl.Bench);

        string want = Path.GetFullPath(missingFile);
        var m = Touchstone.Match(want);
        if (!m.Success) return null;
        string wantStem = want[..^m.Length];

        foreach (var (stem, document, label) in EmWriters(workspaceRoot))
        {
            if (!string.Equals(Path.GetFullPath(stem), wantStem, PathComparison)) continue;
            string name = Path.GetFileName(want);
            if (OtherPortCount(stem, m.Groups[1].Value) is { } other)
                return new MissingFileWriter(Capitalized(
                    $"{label} wrote {Path.GetFileName(other.Path)}, a {other.Ports}-port result, and this block reads {name}, a " +
                    $"{m.Groups[1].Value}-port one."), document);
            return new MissingFileWriter($"{name} is written by {label}: run that first.", document);
        }
        return new MissingFileWriter(NoWriter, null);
    }

    /// <summary>Each EM result stem the workspace's documents write, with the document and how a sentence names it.</summary>
    private static IEnumerable<(string Stem, string Document, string Label)> EmWriters(string workspaceRoot)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (string cem in Directory.EnumerateFiles(workspaceRoot, "*.cem", options))
        {
            EmSetup setup;
            try { setup = EmSetupPersistence.LoadFromFile(cem); }
            catch (Exception ex) when (Unreadable(ex)) { continue; }
            foreach (var w in Stems(setup, ResultsRootOf(cem), cem, Relative(workspaceRoot, cem)))
                yield return w;
        }
        foreach (string c3d in Directory.EnumerateFiles(workspaceRoot, "*.c3d", options))
        {
            IReadOnlyList<C3dEmbeddedSetup> setups;
            try { setups = C3dSetups.Read(C3dPersistence.LoadFromFile(c3d)); }
            catch (Exception ex) when (Unreadable(ex)) { continue; }
            foreach (var embedded in setups)
            {
                if (embedded.Setup is null) continue;
                var run = C3dSetups.ForRun(embedded.Setup, c3d);
                foreach (var w in Stems(run, ResultsRootOf(c3d), c3d, $"setup '{embedded.Name}' in {Relative(workspaceRoot, c3d)}"))
                    yield return w;
            }
        }
    }

    /// <summary>The stems a run of <paramref name="setup"/> writes: one for a planar setup, one per solver (and its comparison
    /// spelling) for a 3D one.</summary>
    private static IEnumerable<(string, string, string)> Stems(EmSetup setup, string resultsRoot, string document, string label)
    {
        if (!setup.Is3D)
        {
            yield return (EmRunService.ResolveSnpBasePath(resultsRoot, setup), document, label);
            yield break;
        }
        foreach (var solver in setup.Solver3D.Solvers)
        {
            string run = $"the {Em3dSolvers.Get(solver).DisplayName} run of {label}";
            yield return (Em3dRunService.SnpBasePath(resultsRoot, setup, solver), document, run);
            if (setup.Solver3D.IsCompare)
                yield return (Em3dRunService.BothSnpBasePath(resultsRoot, setup, solver), document, run);
        }
    }

    /// <summary>A result of <paramref name="stem"/> with another port count than <paramref name="ports"/>, newest first.</summary>
    private static (string Path, string Ports)? OtherPortCount(string stem, string ports)
    {
        string? dir = Path.GetDirectoryName(stem);
        if (dir is null || !Directory.Exists(dir)) return null;
        string name = Path.GetFileName(stem);
        return Directory.EnumerateFiles(dir, name + ".s*p")
            .Select(f => (Path: f, Match: Touchstone.Match(f)))
            .Where(f => f.Match.Success
                        && string.Equals(Path.GetFileName(f.Path)[..^f.Match.Length], name, PathComparison)
                        && f.Match.Groups[1].Value != ports)
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f.Path))
            .Select(f => ((string Path, string Ports)?)(f.Path, f.Match.Groups[1].Value))
            .FirstOrDefault();
    }

    /// <summary>Where a run of the document writes: <c>results/</c> of the workspace above it, or beside it when there is none —
    /// the results root the GUI's run and the CLI's <c>em</c> both use.</summary>
    private static string ResultsRootOf(string document)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(document))!;
        string root = WorkspaceRootFinder.FindAncestorCws(dir) is { } cws ? Path.GetDirectoryName(cws)! : dir;
        return Path.Combine(root, "results");
    }

    private static string Capitalized(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Relative(string workspaceRoot, string path)
        => Path.GetRelativePath(workspaceRoot, path).Replace('\\', '/');

    private static bool Unreadable(Exception ex)
        => ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException
               or ArgumentException or InvalidOperationException or NotSupportedException or FormatException;
}
