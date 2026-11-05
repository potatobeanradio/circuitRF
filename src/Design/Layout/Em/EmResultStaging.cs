// brief-cosim-pattern-1 R-cp1-3 — a run that does not finish changes no result.
//
// Every result file is written into a staging folder beside where it lands (<dir>/.staging-<name>/new/), under its final file name,
// so nothing inside a file can name the staging folder. Then Publish renames them into place in the order field set, .npy, .sNp,
// record. A disk that fills, a write error or a cancel before the renames leaves the previous result as it was. A rename that fails
// puts back what the earlier renames replaced. A process killed among the renames leaves stamps that disagree, which
// EmResultRecord.Verify reports, and its staging folder is removed by the next run of that setup.
//
// Same folder as the target, so the renames stay on one volume.

using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Design.Layout.Em;

/// <summary>A run's mesh as the run reports it.</summary>
public sealed record EmResultMesh(long? Size, string? Unit, long? Unknowns);

/// <summary>What <see cref="EmResultStaging.PublishRun"/> put in place; a null path was not published.</summary>
public sealed record EmPublished(string? Npy, string? Snp, string? Record)
{
    public static readonly EmPublished None = new(null, null, null);
}

/// <summary>One finished driven run's results, ready to be published.</summary>
/// <param name="WriteSnp">Writes the Touchstone at the staged base it is given, with the stamp in its header: null, or why not.</param>
internal sealed record EmPublishRequest(
    EmResultPaths Paths, int PortCount, DataSet Data, EmSetup Setup, EmRunTarget? Target,
    string Solver, string SolverVersion, string? Device, EmResultMesh? Mesh, IReadOnlyList<EmResultPort> Ports,
    Func<string, string, string?> WriteSnp)
{
    /// <summary>The field set staged by this run, when it kept one.</summary>
    public CircuitRF.Design.Em3d.PortFieldManifest? Fields { get; init; }

    /// <summary>The staging already holding this run's field set; null to begin one.</summary>
    public EmResultStaging? Staging { get; init; }

    public string? ThreeDViewText { get; init; }
}

public sealed class EmResultStaging : IDisposable
{
    private const string Prefix = ".staging-";

    /// <summary>Test seam: called before each rename with its target, so a test can make one fail.</summary>
    internal static Action<string>? BeforeRename;

    private readonly EmResultPaths _paths;
    private readonly string _name;

    private EmResultStaging(EmResultPaths paths, string stamp)
    {
        _paths = paths;
        _name = Prefix + Path.GetFileName(paths.Base);
        Stamp = stamp;
    }

    /// <summary>The run's stamp, which every one of its files carries.</summary>
    public string Stamp { get; }

    /// <summary>Starts staging one run's result, removing what a killed run of this setup left.</summary>
    public static EmResultStaging Begin(EmResultPaths paths)
    {
        RemoveLeftovers(paths);
        return new EmResultStaging(paths, CircuitRF.Design.Em3d.PortFieldSet.NewStamp());
    }

    /// <summary>Removes the staging folders of this result's name, including any a killed process left.</summary>
    public static void RemoveLeftovers(EmResultPaths paths)
    {
        string name = Prefix + Path.GetFileName(paths.Base);
        foreach (string dir in Folders(paths)) Remove(Path.Combine(dir, name));
    }

    private static IEnumerable<string> Folders(EmResultPaths paths)
        => new[] { paths.Base, paths.NpyPath, paths.SnpBase }
           .Select(p => Path.GetDirectoryName(Path.GetFullPath(p))!).Distinct(StringComparer.Ordinal);

    private static void Remove(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private string StageRoot(string target) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target))!, _name);

    /// <summary>Where <paramref name="target"/> is staged. Its folder exists; the file or folder does not have to.</summary>
    private string Staged(string target)
    {
        string folder = Path.Combine(StageRoot(target), "new");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, Path.GetFileName(target));
    }

    /// <summary>The field set's staged folder, for <c>PortFieldSetWriter.Write</c>.</summary>
    public string StagedFields => Staged(_paths.FieldsFolder);

    /// <summary>Publishes only a <c>.npy</c>: a static or eigenmode problem's result, which has no record and shares no other file.</summary>
    /// <returns>The published path, or null with the sentence why not.</returns>
    public static (string? Path, string? Error) PublishNpy(string npyPath, DataSet data)
    {
        if (data.Groups.Count == 0) return (null, null);
        var paths = new EmResultPaths(npyPath, npyPath, npyPath);
        using var staging = Begin(paths);
        try
        {
            DataSetExporter.Export(data, staging.Staged(npyPath), ExportFormat.Npy);
            return staging.Rename([(npyPath, staging.Staged(npyPath))]) is { } error ? (null, error) : (Path.GetFullPath(npyPath), null);
        }
        catch (Exception e) when (IsWriteFailure(e)) { return (null, e.Message); }
    }

    private static bool IsWriteFailure(Exception e) => e is not OperationCanceledException;

    /// <summary>
    /// R-cp1-3 — one finished run's result: everything staged, then renamed into place. A write that fails publishes nothing, adds
    /// the sentence to <paramref name="errors"/> and leaves the previous result as it was.
    /// </summary>
    internal static EmPublished PublishRun(EmPublishRequest r, ICollection<string> errors)
    {
        var staging = r.Staging ?? Begin(r.Paths);
        using (staging)
        {
            bool keepRecord = r.Target?.KeepRecord != false;
            string npy = r.Paths.NpyPath, snp = r.Paths.SnpPath(r.PortCount);
            string? stagedNpy = null;
            bool failed = false;

            EmResultRecord.StampData(r.Data, staging.Stamp);
            try
            {
                if (r.Data.Groups.Count > 0)
                {
                    stagedNpy = staging.Staged(npy);
                    DataSetExporter.Export(r.Data, stagedNpy, ExportFormat.Npy);
                }
            }
            catch (Exception e) when (IsWriteFailure(e))
            {
                errors.Add($"The EM result could not be written to results/: {e.Message}");
                failed = true;
            }

            string stagedSnp = "";
            try
            {
                stagedSnp = staging.Staged(snp);
                string stagedBase = stagedSnp[..^Path.GetExtension(stagedSnp).Length];
                if (r.WriteSnp(stagedBase, staging.Stamp) is { } why)
                {
                    errors.Add($"The .snp could not be written to '{snp}': {why}");
                    failed = true;
                }
            }
            catch (Exception e) when (IsWriteFailure(e))
            {
                errors.Add($"The .snp could not be written to '{snp}': {e.Message}");
                failed = true;
            }

            string? stagedRecord = null;
            if (!failed && keepRecord)
            {
                try
                {
                    stagedRecord = staging.Staged(r.Paths.RecordPath);
                    File.WriteAllText(stagedRecord, EmResultRecord.Build(r, staging.Stamp, stagedSnp).Serialize());
                }
                catch (Exception e) when (IsWriteFailure(e))
                {
                    errors.Add($"The run's record could not be written to '{r.Paths.RecordPath}': {e.Message}");
                    failed = true;
                }
            }
            if (failed)
            {
                errors.Add("No result file was replaced: the previous result, if there is one, is as it was.");
                return EmPublished.None;
            }

            var steps = new List<(string Target, string? New)>();
            if (keepRecord) steps.Add((r.Paths.FieldsFolder, r.Fields is null ? null : staging.StagedFields));
            if (stagedNpy is not null) steps.Add((npy, stagedNpy));
            steps.Add((snp, stagedSnp));
            if (keepRecord)
            {
                if (ReplacedTouchstone(r.Paths, snp) is { } other) steps.Add((other, null));
                steps.Add((r.Paths.RecordPath, stagedRecord));
            }
            if (staging.Rename(steps) is { } error)
            {
                errors.Add($"The run's results could not be put in place, so the previous result was kept: {error}");
                return EmPublished.None;
            }
            return new EmPublished(stagedNpy is null ? null : Path.GetFullPath(npy), snp, keepRecord ? Path.GetFullPath(r.Paths.RecordPath) : null);
        }
    }

    /// <summary>The previous result's Touchstone when this run replaces it with another port count: the same stem beside it, with the
    /// suffix the record it is replacing named. One name holds one result.</summary>
    private static string? ReplacedTouchstone(EmResultPaths paths, string snp)
    {
        if (!File.Exists(paths.RecordPath)) return null;
        var (old, _) = EmResultRecord.Load(paths.RecordPath);
        if (old is null) return null;
        string previous = EmResultRecord.Resolve(paths.RecordPath, old.Files.Snp);
        return !string.Equals(previous, Path.GetFullPath(snp), StringComparison.Ordinal) && File.Exists(previous)
               && string.Equals(Path.GetDirectoryName(previous), Path.GetDirectoryName(Path.GetFullPath(snp)), StringComparison.Ordinal)
               && string.Equals(EmResultNames.Clean(previous), Path.GetFullPath(paths.SnpBase), StringComparison.Ordinal)
            ? previous : null;
    }

    /// <summary>
    /// Puts each staged file at its target, in order, moving what was there aside first. A failure puts everything back. A step with no
    /// new file removes the target. Null on success, otherwise the sentence why.
    /// </summary>
    private string? Rename(IReadOnlyList<(string Target, string? New)> steps)
    {
        var done = new List<(string Target, string? Old, bool Placed)>();
        try
        {
            foreach (var (target, staged) in steps)
            {
                if (staged is null && !Exists(target)) continue;
                BeforeRename?.Invoke(target);
                string? old = null;
                if (Exists(target))
                {
                    string aside = Path.Combine(StageRoot(target), "old");
                    Directory.CreateDirectory(aside);
                    old = Path.Combine(aside, Path.GetFileName(target));
                    Move(target, old);
                }
                done.Add((target, old, false));
                if (staged is not null)
                {
                    Move(staged, target);
                    done[^1] = (target, old, true);
                }
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var why = e.Message;
            for (int k = done.Count - 1; k >= 0; k--)
            {
                var (target, old, placed) = done[k];
                try
                {
                    if (placed) Delete(target);
                    if (old is not null) Move(old, target);
                }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
                {
                    why += $" '{target}' could not be put back ({undo.Message}).";
                }
            }
            return why;
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void Move(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    private static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else File.Delete(path);
    }

    /// <summary>Removes this run's staging folders. Whatever was published is already out of them.</summary>
    public void Dispose()
    {
        foreach (string dir in Folders(_paths)) Remove(Path.Combine(dir, _name));
    }
}
