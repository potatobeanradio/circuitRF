using System.Text;

namespace CircuitRF.Design.Em3d.Install;

/// <summary>One home a removal would delete, measured when the plan was made.</summary>
public sealed record PlannedRemoval(InstallRecord Record, long Bytes);

/// <summary>
/// What a removal would do, before it does it (brief-em3d-25 R-em3d25-1b): the homes, their size measured
/// NOW rather than read from the record, and the confirmation a person reads — or the refusal that
/// stops it before anything is asked.
/// </summary>
/// <param name="Leftovers">Each <c>*.removing</c> directory an interrupted removal left, with its size: no
/// longer a solver anyone can run, but still disk the user was told they would get back (R-em3d25-1d).</param>
/// <param name="Refusal">Why nothing can be removed, or null.</param>
public sealed record SolverRemovalPlan(IReadOnlyList<PlannedRemoval> Homes, IReadOnlyList<(string Path, long Bytes)> Leftovers,
                                       string? Refusal, string Confirmation)
{
    public long TotalBytes => Homes.Sum(h => h.Bytes) + Leftovers.Sum(l => l.Bytes);
    public bool CanProceed => Refusal is null && (Homes.Count > 0 || Leftovers.Count > 0);
}

/// <summary>How a removal ended.</summary>
public enum RemovalStatus
{
    /// <summary>Every planned home is gone.</summary>
    Removed,

    /// <summary>Nothing was removed: in use, an install running, or nothing of circuitRF's to remove.</summary>
    Refused,

    /// <summary>Every home was taken out of discovery's sight, but some files could not be deleted; they
    /// are listed, and the next removal finishes them.</summary>
    Incomplete,
}

/// <summary>What a removal did.</summary>
/// <param name="NotRemoved">Each file or directory that could not be deleted, by path.</param>
public sealed record RemovalOutcome(RemovalStatus Status, string Report, IReadOnlyList<string> Removed, IReadOnlyList<string> NotRemoved);

/// <summary>
/// Removes what the install assistant installed (brief-em3d-25, em-3d.md §7.2 "What the assistant
/// installs, it can uninstall"). <b>The one implementation</b> — the Settings rows, <i>Remove all 3D
/// solvers</i>, <i>Uninstall circuitRF…</i> and <c>circuitrf solver remove</c> all call it.
///
/// <para><b>By the record, and by nothing else</b> (R-em3d25-1a). A home is removable because its
/// <c>install.json</c> says circuitRF put it there; removal is that directory, because every version's
/// home is self-contained. F0 found a live package inside a dead Spack tree, so "delete what looks
/// unused" is not a rule this class may follow. A program found any other way — Settings, an environment
/// variable, <c>PATH</c>, Spack, conda — has no removal at all, and asking for one is a refusal naming
/// where it was found.</para>
///
/// <para><b>All or nothing</b> (R-em3d25-2). Every home is checked — no install running on its tool, no
/// run holding it (<see cref="SolverInUse"/>) — before any is touched, so <i>Remove all</i> never
/// removes some and leaves others.</para>
///
/// <para><b>Rename, then delete</b> (R-em3d25-1d). A home becomes <c>&lt;home&gt;.removing</c> first,
/// which discovery never reads, so a delete that fails part way (a file held open on Windows) leaves
/// nothing a run would find; the next removal finishes it and the report names what was left.</para>
///
/// <para><b>Never called by an upgrade</b> (R-em3d25-5a). The updater's version swap and the macOS bundle
/// exchange do not reference this type, which <c>SolverUninstallTests</c> holds by a source scan.</para>
/// </summary>
public sealed class SolverUninstaller
{
    public SolverUninstaller(IReadOnlyList<string>? roots = null) => Roots = roots ?? SolverHomes.DefaultRoots;

    /// <summary>Every root the install assistant may have written into.</summary>
    public IReadOnlyList<string> Roots { get; }

    /// <summary>The discovery a refusal asks where a tool NOT installed by circuitRF was found. A test
    /// hands in one of its own.</summary>
    public Func<SolverTool, SolverDiscovery> Discovery { get; init; } = SolverDiscovery.For;

    /// <summary>Deletes one file. A seam, so a test can make a delete fail part way on any platform.</summary>
    internal Action<string> DeleteFile { get; init; } = File.Delete;

    /// <summary>Every circuitRF-installed home of <paramref name="tool"/>, newest first.</summary>
    public IReadOnlyList<InstallRecord> Installed(SolverTool tool) => SolverHomes.Published(tool, Roots);

    /// <summary>Every circuitRF-installed home of every tool.</summary>
    public IReadOnlyList<InstallRecord> InstalledAll()
        => Enum.GetValues<SolverTool>().SelectMany(Installed).ToList();

    /// <summary>
    /// The circuitRF-installed homes of <paramref name="tool"/> other than <paramref name="current"/>'s
    /// version, measured — what R-em3d25-3 lists after a newer version is installed.
    /// </summary>
    public IReadOnlyList<PlannedRemoval> Superseded(SolverTool tool, string current)
        => Installed(tool).Where(r => r.Version != current).Select(r => new PlannedRemoval(r, SolverInstaller.MeasureBytes(r.Home))).ToList();

    // ── plans ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The plan to remove one version of <paramref name="tool"/>. <paramref name="version"/> null means
    /// the only one installed; with several installed that is a refusal listing them, because removing a
    /// guessed one of two is not something to do to a day-long build.
    /// </summary>
    public SolverRemovalPlan PlanOne(SolverTool tool, string? version = null)
    {
        string name = Discovery(tool).Name;
        var homes = Installed(tool);
        var leftovers = Leftovers([tool]);
        if (homes.Count == 0)
            return leftovers.Count > 0 && version is null
                ? new([], leftovers, null, Confirm([], leftovers, all: false))
                : Refuse(NotOurs(tool, name));

        InstallRecord? chosen;
        if (version is not null)
        {
            chosen = homes.FirstOrDefault(r => string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase));
            if (chosen is null)
                return Refuse($"circuitRF has not installed {name} {version}. It installed {Versions(homes)}.");
        }
        else if (homes.Count > 1)
            return Refuse($"circuitRF has installed {homes.Count} versions of {name}: {Versions(homes)}. Name the one to remove.");
        else chosen = homes[0];

        var planned = new[] { new PlannedRemoval(chosen, SolverInstaller.MeasureBytes(chosen.Home)) };
        return new(planned, leftovers, null, Confirm(planned, leftovers, all: false));
    }

    /// <summary>The plan for <i>Remove all 3D solvers</i>: every circuitRF-installed home, one confirmation.</summary>
    public SolverRemovalPlan PlanAll()
    {
        var homes = InstalledAll();
        var leftovers = Leftovers(Enum.GetValues<SolverTool>());
        if (homes.Count == 0 && leftovers.Count == 0)
            return Refuse("circuitRF has installed no 3D solvers for this account, so there is nothing to remove. " +
                          "A solver you installed yourself is yours to remove.");
        var planned = homes.Select(r => new PlannedRemoval(r, SolverInstaller.MeasureBytes(r.Home))).ToList();
        return new(planned, leftovers, null, Confirm(planned, leftovers, all: true));
    }

    private static SolverRemovalPlan Refuse(string why) => new([], [], why, "");

    /// <summary>Every <c>*.removing</c> directory under <paramref name="tools"/>' directories, measured.</summary>
    private List<(string Path, long Bytes)> Leftovers(IEnumerable<SolverTool> tools)
    {
        var found = new List<(string, long)>();
        foreach (var tool in tools)
            foreach (string root in Roots.Distinct(StringComparer.Ordinal))
            {
                string dir = SolverHomes.ToolDirectory(root, tool);
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (string d in Directory.EnumerateDirectories(dir, "*" + SolverHomes.RemovingSuffix))
                        found.Add((d, SolverInstaller.MeasureBytes(d)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        return found;
    }

    /// <summary>The refusal for a tool circuitRF did not install: where it WAS found, if anywhere.</summary>
    private string NotOurs(SolverTool tool, string name)
    {
        SolverInstallation? found = null;
        try { found = Discovery(tool).Find(out _); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        return found is null
            ? $"circuitRF has not installed {name} for this account, so there is nothing to remove."
            : $"The {name} a run uses is at {found.Path}, {found.HowFoundText}. circuitRF did not install it, so it " +
              "has no Uninstall and circuitRF will not remove it; remove it the way it was installed.";
    }

    private static string Versions(IEnumerable<InstallRecord> homes) => string.Join(", ", homes.Select(r => r.Version));

    /// <summary>
    /// R-em3d25-1b — the space, measured; that removal is permanent; what getting it back costs, from the
    /// install's own measured time; and that documents are not touched.
    /// </summary>
    private string Confirm(IReadOnlyList<PlannedRemoval> planned, IReadOnlyList<(string Path, long Bytes)> leftovers, bool all)
    {
        var sb = new StringBuilder();
        if (all && planned.Count > 0) sb.AppendLine("This removes every 3D solver circuitRF installed for this account:");
        foreach (var p in planned)
            sb.AppendLine($"{(all ? "  • " : "Remove ")}{Discovery(p.Record.Tool).Name} {p.Record.Version} — {Size(p.Bytes)}, at {p.Record.Home}");
        if (leftovers.Count > 0)
        {
            sb.AppendLine((planned.Count > 0 ? "It also finishes" : "This finishes") + " an earlier removal that stopped part way:");
            foreach (var l in leftovers) sb.AppendLine($"  • {l.Path} — {Size(l.Bytes)}");
        }
        long total = planned.Sum(p => p.Bytes) + leftovers.Sum(l => l.Bytes);
        sb.AppendLine(planned.Count + leftovers.Count > 1 ? $"Together they free {Size(total)}." : $"This frees {Size(total)}.");
        if (planned.Count == 0) return sb.ToString().TrimEnd();
        sb.AppendLine();
        sb.AppendLine("Removal is permanent. Getting a solver back is a reinstall:");
        foreach (var p in planned) sb.AppendLine($"  {Discovery(p.Record.Tool).Name}: {ReinstallCost(p.Record)}");
        sb.AppendLine();
        sb.Append("Your documents are not touched: setups, meshes, results and run directories live in your workspaces, " +
                  "and every one of them stays. Solvers you installed yourself stay too. Afterwards a 3D run offers Install … again.");
        return sb.ToString();
    }

    /// <summary>The install's own measured time on this computer, or the recipe's measurement when the
    /// record has none.</summary>
    internal static string ReinstallCost(InstallRecord record)
    {
        if (record.ElapsedSeconds >= 1)
            return $"installing it took {Minutes(record.ElapsedSeconds)} on this computer ({record.InstalledAt:yyyy-MM-dd}).";
        if (SolverRecipes.All.FirstOrDefault(r => r.Id == record.Recipe) is { Measured.Minutes: { } m } recipe)
            return $"about {Minutes(m * 60)} ({recipe.Measured.Source}).";
        return "the same install again; its time was not measured.";
    }

    private static string Minutes(double seconds)
        => seconds >= 5400 ? $"about {seconds / 3600:0.#} hours"
         : seconds >= 60   ? $"{seconds / 60:0} minutes"
         : $"{seconds:0} seconds";

    /// <summary>Bytes as a person reads them.</summary>
    public static string Size(long bytes)
        => bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.00} GB"
         : bytes >= 1_000_000     ? $"{bytes / 1e6:0.0} MB"
         : $"{Math.Max(bytes, 0) / 1e3:0} kB";

    // ── the removal ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Carries out <paramref name="plan"/>: refuses as a whole when any home's tool has an install
    /// running or any home is in use; otherwise renames each home out of discovery's sight and deletes it,
    /// finishing any removal an earlier attempt left.
    /// </summary>
    public RemovalOutcome Remove(SolverRemovalPlan plan)
    {
        if (plan.Refusal is { } why) return new(RemovalStatus.Refused, why, [], []);
        if (!plan.CanProceed) return new(RemovalStatus.Refused, "Nothing to remove.", [], []);

        // An install holds its tool's lock for its whole length, in this process or another, so taking
        // that lock is what refuses a removal while one runs — and keeps one from starting mid-delete.
        var gates = new List<FileStream>();
        try
        {
            foreach (var tool in plan.Homes.Select(h => h.Record.Tool).Distinct())
            {
                string name = Discovery(tool).Name;
                foreach (string toolDir in plan.Homes.Where(h => h.Record.Tool == tool)
                                                     .Select(h => Path.GetDirectoryName(h.Record.Home)!).Distinct())
                {
                    try
                    {
                        gates.Add(new FileStream(Path.Combine(toolDir, SolverHomes.InstallLockFile), FileMode.OpenOrCreate,
                                                 FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
                    }
                    catch (IOException)
                    {
                        return new(RemovalStatus.Refused,
                                   $"Nothing was removed: an install of {name} is running. Wait for it to finish, or cancel it, and try again.", [], []);
                    }
                    catch (UnauthorizedAccessException e)
                    {
                        return new(RemovalStatus.Refused, $"Nothing was removed: {toolDir} cannot be changed ({e.Message}).", [], []);
                    }
                }
            }

            var busy = plan.Homes.Select(h => (h, Holders: SolverInUse.HoldersOf(h.Record.Home)))
                                 .Where(x => x.Holders.Count > 0).ToList();
            if (busy.Count > 0)
            {
                var sb = new StringBuilder("Nothing was removed: ");
                sb.Append(string.Join("; ", busy.Select(b =>
                    $"{Discovery(b.h.Record.Tool).Name} {b.h.Record.Version} is in use by {string.Join(" and ", b.Holders)}")));
                sb.Append(". Wait for it to finish, or cancel it, and try again.");
                return new(RemovalStatus.Refused, sb.ToString(), [], []);
            }

            var removed = new List<string>();
            var left    = new List<string>();
            foreach (var h in plan.Homes)
            {
                string home = h.Record.Home;
                if (!Directory.Exists(home)) { removed.Add(home); continue; }
                string removing = home + SolverHomes.RemovingSuffix;
                try
                {
                    DeleteQuietly(removing, left);   // an earlier attempt's remains, under the same name
                    Directory.Move(home, removing);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Not renamed means still published: the record goes first, which is what unpublishes it.
                    try { DeleteFile(Path.Combine(home, SolverHomes.RecordFile)); }
                    catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException)
                    {
                        left.Add(home);
                        continue;
                    }
                    removing = home;
                }
                DeleteQuietly(removing, left);
                removed.Add(home);
            }
            foreach (string toolDir in plan.Homes.Select(h => Path.GetDirectoryName(h.Record.Home)!)
                                                 .Concat(plan.Leftovers.Select(l => Path.GetDirectoryName(l.Path)!)).Distinct())
                FinishInterrupted(toolDir, left);
            removed.AddRange(plan.Leftovers.Select(l => l.Path).Where(p => !Directory.Exists(p)));

            return new(left.Count == 0 ? RemovalStatus.Removed : RemovalStatus.Incomplete, Report(plan, left), removed, left);
        }
        finally
        {
            foreach (var g in gates) g.Dispose();
        }
    }

    /// <summary>Deletes every <c>*.removing</c> directory an interrupted removal left in <paramref name="toolDir"/>.</summary>
    private void FinishInterrupted(string toolDir, List<string> left)
    {
        IEnumerable<string> debris;
        try { debris = Directory.Exists(toolDir) ? Directory.EnumerateDirectories(toolDir, "*" + SolverHomes.RemovingSuffix).ToList() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        foreach (string d in debris) DeleteQuietly(d, left);
    }

    private string Report(SolverRemovalPlan plan, IReadOnlyList<string> left)
    {
        var sb = new StringBuilder();
        var names = plan.Homes.Select(h => $"{Discovery(h.Record.Tool).Name} {h.Record.Version}")
                              .Concat(plan.Leftovers.Count > 0 ? ["what an earlier removal left"] : []).ToList();
        if (left.Count == 0)
        {
            sb.Append($"Removed {string.Join(", ", names)}, freeing {Size(plan.TotalBytes)}. Your documents were not touched. ");
            sb.Append("A 3D run that needs it now offers Install … again.");
            return sb.ToString();
        }
        sb.AppendLine($"Removed {string.Join(", ", names)} from circuitRF's sight — a run no longer finds " +
                      (names.Count == 1 ? "it" : "them") + " — but these could not be deleted:");
        foreach (string p in left.Take(20)) sb.AppendLine("    " + p);
        if (left.Count > 20) sb.AppendLine($"    … and {left.Count - 20} more.");
        sb.Append("A program still running from there holds them. Close it, then remove again: the next removal finishes the job.");
        return sb.ToString();
    }

    /// <summary>
    /// Deletes <paramref name="dir"/> bottom up, never following a link, making read-only entries writable
    /// on the way, and adding each entry it could not delete to <paramref name="left"/> by path instead of
    /// stopping at the first.
    /// </summary>
    private void DeleteQuietly(string dir, List<string> left)
    {
        var info = new DirectoryInfo(dir);
        if (!info.Exists && info.LinkTarget is null) return;
        if (info.LinkTarget is not null)
        {
            try { info.Delete(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add(dir); }
            return;
        }
        bool clean = DeleteChildren(info, left);
        if (!clean) return;
        try { MakeWritable(info); info.Delete(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add(dir); }
    }

    private bool DeleteChildren(DirectoryInfo dir, List<string> left)
    {
        MakeWritable(dir);
        IEnumerable<FileSystemInfo> entries;
        try { entries = dir.EnumerateFileSystemInfos().ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add(dir.FullName); return false; }

        bool clean = true;
        foreach (var e in entries)
        {
            try
            {
                if (e is DirectoryInfo d && d.LinkTarget is null)
                {
                    if (!DeleteChildren(d, left)) { clean = false; continue; }
                    d.Delete();
                }
                else if (e is DirectoryInfo link) link.Delete();   // a link is removed, never followed
                else
                {
                    if (OperatingSystem.IsWindows() && e.Attributes.HasFlag(FileAttributes.ReadOnly))
                        e.Attributes &= ~FileAttributes.ReadOnly;
                    DeleteFile(e.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left.Add(e.FullName);
                clean = false;
            }
        }
        return clean;
    }

    private static void MakeWritable(DirectoryInfo d)
    {
        try
        {
            if (OperatingSystem.IsWindows()) { if (d.Attributes.HasFlag(FileAttributes.ReadOnly)) d.Attributes &= ~FileAttributes.ReadOnly; }
            else d.UnixFileMode |= UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
