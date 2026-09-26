using System.Diagnostics;
using System.Text;

namespace CircuitRF.Design.Em3d.Install;

/// <summary>
/// Which circuitRF-installed solver homes something is using right now (brief-em3d-25 R-em3d25-1c), so
/// <see cref="SolverUninstaller"/> never removes a program from under a run.
///
/// <para><b>Two halves.</b> In this process, a count of holders per home, each with the sentence a
/// refusal names it by ("the 3D EM run of 'filter'"). For every OTHER process, a lock file in the home,
/// <c>in-use.&lt;pid&gt;</c>, holding the same sentences — so a CLI run in a terminal refuses a removal
/// made from the window, and the other way round. A lock whose process is gone is stale: it is ignored
/// and deleted, because a run that crashed must not make its solver unremovable for ever.</para>
///
/// <para><b>Only published homes are held.</b> A program found on <c>PATH</c> or named in Settings is not
/// circuitRF's to remove, so there is nothing to protect and nothing is written next to it.</para>
/// </summary>
public static class SolverInUse
{
    /// <summary>The lock file's name prefix; the rest is the holding process's id.</summary>
    public const string LockPrefix = "in-use.";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, List<string>> Holders = new(PathComparer);

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Holds the published home of each of <paramref name="programs"/> for as long as the result is not
    /// disposed. A program under no published home holds nothing.
    /// </summary>
    public static IDisposable HoldPrograms(IEnumerable<string?> programs, string holder)
    {
        var homes = programs.Where(p => p is { Length: > 0 }).Select(p => SolverHomes.HomeOf(p!))
                            .OfType<string>().Distinct(PathComparer).ToList();
        return new Hold(homes.Select(h => Take(h, holder)).ToList(), holder);
    }

    /// <summary>Holds <paramref name="home"/> itself.</summary>
    public static IDisposable HoldHome(string home, string holder) => new Hold([Take(home, holder)], holder);

    /// <summary>
    /// Everything holding <paramref name="home"/>: this process's holders by name, then each other live
    /// process's lock, by what that process wrote in it. Stale locks are deleted on the way.
    /// </summary>
    public static IReadOnlyList<string> HoldersOf(string home)
    {
        var found = new List<string>();
        string key = Normalize(home);
        lock (Gate)
            if (Holders.TryGetValue(key, out var mine)) found.AddRange(mine);
        bool heldHere = found.Count > 0;

        IEnumerable<string> locks;
        try { locks = Directory.Exists(home) ? Directory.EnumerateFiles(home, LockPrefix + "*").ToList() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return found; }
        foreach (string file in locks)
        {
            if (!int.TryParse(Path.GetFileName(file)[LockPrefix.Length..], out int pid)) continue;
            // Our own pid with no holder here is a lock a previous process with this id left behind.
            bool live = pid == Environment.ProcessId ? heldHere : IsRunning(pid);
            if (!live)
            {
                try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                continue;
            }
            if (pid == Environment.ProcessId) continue;
            string text;
            try { text = File.ReadAllText(file).Trim(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { text = ""; }
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            found.AddRange(lines.Length > 0
                ? lines.Select(l => $"{l} (circuitRF process {pid})")
                : [$"circuitRF process {pid}"]);
        }
        return found;
    }

    private static string Take(string home, string holder)
    {
        string key = Normalize(home);
        lock (Gate)
        {
            if (!Holders.TryGetValue(key, out var list)) Holders[key] = list = [];
            list.Add(holder);
            WriteLock(key, list);
        }
        return key;
    }

    private static void Release(string key, string holder)
    {
        lock (Gate)
        {
            if (!Holders.TryGetValue(key, out var list)) return;
            list.Remove(holder);
            if (list.Count == 0) Holders.Remove(key);
            WriteLock(key, list);
        }
    }

    /// <summary>Rewrites this process's lock in <paramref name="home"/>, or deletes it when nothing holds
    /// the home. A home that cannot be written (read-only media) still gets the in-process half.</summary>
    private static void WriteLock(string home, List<string> holders)
    {
        string file = Path.Combine(home, LockPrefix + Environment.ProcessId);
        try
        {
            if (holders.Count == 0) File.Delete(file);
            else if (Directory.Exists(home)) File.WriteAllText(file, string.Join('\n', holders) + "\n", new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }

    private static string Normalize(string home) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));

    private sealed class Hold(List<string> keys, string holder) : IDisposable
    {
        private List<string>? _keys = keys;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _keys, null) is not { } held) return;
            foreach (string key in held) Release(key, holder);
        }
    }
}
