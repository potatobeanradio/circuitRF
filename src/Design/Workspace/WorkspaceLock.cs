using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Design.Workspace;

// ──────────────────────────────────────────────────────────────────────────────
//  SL4 §1 — two people, one workspace, and an ADVISORY notice about it.
//  brief-shared-library-4-concurrency-and-latency.md R-sl4-1 … R-sl4-5.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>What a lock file says: who has this workspace open, where, and since when.</summary>
/// <param name="User">The account name, as the other person's machine reports it.</param>
/// <param name="Host">Their machine's name — the half that makes the notice actionable.</param>
/// <param name="ProcessId">Their circuitRF's process id. Meaningful only on the SAME host.</param>
/// <param name="TakenUtc">When the workspace was opened.</param>
public sealed record WorkspaceLockInfo(
    [property: JsonPropertyName("user")]      string   User,
    [property: JsonPropertyName("host")]      string   Host,
    [property: JsonPropertyName("processId")] int      ProcessId,
    [property: JsonPropertyName("takenUtc")]  DateTime TakenUtc);

/// <summary>
/// The advisory record that a workspace is open somewhere — a small JSON file beside the
/// <c>.cws</c>, written when the workspace is opened and removed when it is closed.
///
/// <para><b>Why this exists (§1).</b> <c>WorkspaceViewModel.SwitchToWorkspace</c> already refuses to
/// open one workspace twice, and its own comment gives the reason exactly: two view models over one
/// <c>.cws</c> means two independent edit-session registries over the same files — two undo stacks,
/// two dirty flags, last-save-wins. That reasoning is entirely correct and entirely PROCESS-LOCAL:
/// the check is <c>App.WindowShowing</c>, which enumerates this process's windows. Every consequence
/// it names is equally true of two people on two machines, and none of it was detected there. The
/// concrete loss is not the documents — a clobbered <c>.csch</c> is at least visible — it is the
/// <c>.cws</c>: dock layout, open-document list, kit settings and <b>the alias table</b>, last writer
/// wins, silently, and an alias that vanished from someone else's file is not a symptom anyone
/// attributes correctly.</para>
///
/// <para><b>It is ADVISORY and the wording says so (R-sl4-2, and R-sl-8 before it).</b> circuitRF
/// cannot lock a network share reliably across three platforms and must not pretend to. Both answers
/// — open read-only, or open anyway — are available, always. <b>A lock this product treated as
/// authoritative would become a stale file that locks out a team</b>, which is a worse failure than
/// the one being prevented and is unfixable by anyone who does not know the file exists.</para>
///
/// <para><b>No open file handle (R-sl4-4).</b> <c>CrashReporter</c> holds a handle with
/// <c>FileShare.Read</c> so that an exclusive open by a probe proves ownership, and <c>Program</c>
/// uses the same idiom for single-instance detection. That is the right mechanism LOCALLY and its
/// guarantees do not survive SMB, NFS or a dropped connection — a handle-based lock over a share
/// fails in the direction that produces a confident false statement about another person, which is
/// the one direction this feature must not fail in. This writes the file and closes it.</para>
///
/// <para><b>Nothing merges (R-sl4-5).</b> Detect, report, let the user choose. Reconciling two
/// <c>.cws</c> files or two <c>.csch</c> files is a different product.</para>
///
/// <para>Framework-free and in <c>src/Design</c> beside <see cref="WorkspaceWritability"/>, which
/// answers the question this one asks first: a workspace nobody can write takes no lock and needs
/// none.</para>
/// </summary>
public static class WorkspaceLock
{
    /// <summary>
    /// The lock file's name, in the workspace root beside the <c>.cws</c>. One fixed name per
    /// workspace, because the whole question is "is anyone else in here" and a per-session name would
    /// make that a directory listing rather than a read.
    ///
    /// <para><c>WorkspaceScanner.IsHiddenTreeFile</c> hides it — the project tree hides only
    /// <c>.DS_Store</c> and <c>*.source</c>, <b>not dotfiles in general</b>, so without that it would
    /// render as a loose file at the workspace root and travel into an archive. That is the same trap
    /// SL2's write probe fell into, recorded there.</para>
    /// </summary>
    public const string FileName = ".crf-open.json";

    /// <summary>
    /// R-sl4-3's second staleness rule: a lock older than this is treated as stale regardless of
    /// where it came from. <b>Hours, not minutes</b> — an engineer leaves a workspace open over
    /// lunch, and a threshold short enough to catch a crash promptly is short enough to declare a
    /// colleague's live session dead. Eight hours is a working day; anything still holding a lock
    /// after one is far more likely to be a crash or a killed VPN than a person.
    ///
    /// <para>Both rules are heuristics and both may be wrong. That is acceptable precisely because
    /// R-sl4-2 makes the answer overridable either way — being wrong here costs a notice, never
    /// access.</para>
    /// </summary>
    public static TimeSpan StaleAfter { get; set; } = TimeSpan.FromHours(8);

    // ── Seams (§4 item 6: a real second machine is not testable and is not needed) ─

    /// <summary>The clock. Null is <see cref="DateTime.UtcNow"/>.</summary>
    public static Func<DateTime>? Clock { get; set; }

    /// <summary>This machine's name. Null is <see cref="Environment.MachineName"/>.</summary>
    public static Func<string>? HostName { get; set; }

    /// <summary>This user's account name. Null is <see cref="Environment.UserName"/>.</summary>
    public static Func<string>? UserName { get; set; }

    /// <summary>This process's id. Null is the real one.</summary>
    public static Func<int>? ProcessId { get; set; }

    /// <summary>
    /// Whether a process id is running ON THIS HOST. Null asks the OS. Never consulted for a lock
    /// from another host, where a process id means nothing and colliding with a live one is certain.
    /// </summary>
    public static Func<int, bool>? ProcessIsRunning { get; set; }

    private static DateTime Now      => Clock    is { } c ? c() : DateTime.UtcNow;
    private static string   ThisHost => HostName is { } h ? h() : Environment.MachineName;
    private static string   ThisUser => UserName is { } u ? u() : Environment.UserName;
    private static int      ThisPid  => ProcessId is { } p ? p() : Environment.ProcessId;

    private static bool IsRunning(int pid)
    {
        if (ProcessIsRunning is { } probe) return probe(pid);
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (ArgumentException)
        {
            return false;   // no such process — the one answer that actually means "gone"
        }
        catch
        {
            // Anything else means we could not tell, and "could not tell" reads as ALIVE:
            // declaring a live session dead is the direction that produces a confident false
            // statement about another person, which is the one direction this must not fail in. The
            // age rule still catches a genuinely abandoned lock a few hours later.
            return true;
        }
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The lock on the workspace rooted at <paramref name="workspaceDir"/>, or null when there is
    /// none — including when the file is unreadable or malformed. A lock file we cannot understand is
    /// no evidence about another person, and refusing to open a workspace over one would be exactly
    /// the stale-file failure R-sl4-2 forbids.
    /// </summary>
    public static WorkspaceLockInfo? Read(string? workspaceDir)
    {
        if (string.IsNullOrWhiteSpace(workspaceDir)) return null;
        string path = Path.Combine(workspaceDir, FileName);
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<WorkspaceLockInfo>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when the lock was taken by THIS process on THIS host — our own, not a report
    /// about anybody else.</summary>
    public static bool IsOurs(WorkspaceLockInfo info) =>
        info.ProcessId == ThisPid && string.Equals(info.Host, ThisHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// R-sl4-3 — stale by either of two independent rules:
    /// <list type="number">
    /// <item>it names THIS host and a process id that is not running (a crash, or a kill);</item>
    /// <item>it is older than <see cref="StaleAfter"/>, wherever it came from.</item>
    /// </list>
    /// A lock from another host can only ever be stale by the second rule: that host's process ids
    /// say nothing here, and half of them are live on any busy machine.
    /// </summary>
    public static bool IsStale(WorkspaceLockInfo info)
    {
        if (string.Equals(info.Host, ThisHost, StringComparison.OrdinalIgnoreCase)
            && !IsRunning(info.ProcessId))
            return true;

        return Now - info.TakenUtc > StaleAfter;
    }

    // ── The notice (R-sl4-2) ──────────────────────────────────────────────────

    /// <summary>
    /// The sentence shown when another session holds the lock. It names WHO and WHERE, because
    /// "someone has this open" is not something a user can act on and "[a colleague] on
    /// <c>lab-07</c>" is — the next step is to go and ask them.
    ///
    /// <para>It never says the workspace is locked, blocked or unavailable, because it is none of
    /// those: both answers follow it. The wording is the feature (R-sl4-2).</para>
    /// </summary>
    public static string NoticeFor(WorkspaceLockInfo info, string workspaceName)
    {
        string ago = Describe(Now - info.TakenUtc);
        return $"'{workspaceName}' was opened by {info.User} on {info.Host} {ago}. " +
               "circuitRF cannot tell whether they still have it open, so this is a notice rather " +
               "than a refusal. If you both save, the last one wins — including the workspace's own " +
               "dock layout, kit settings and referenced-workspace aliases.";
    }

    /// <summary>The same sentence for a lock the two staleness rules say is abandoned. It says which
    /// rule fired, because "probably a crash on your own machine" and "nobody has touched this since
    /// Tuesday" call for different amounts of caution from the reader.</summary>
    public static string StaleNoticeFor(WorkspaceLockInfo info, string workspaceName)
    {
        bool sameHostDeadProcess =
            string.Equals(info.Host, ThisHost, StringComparison.OrdinalIgnoreCase)
            && !IsRunning(info.ProcessId);

        return sameHostDeadProcess
            ? $"'{workspaceName}' was left marked as open by a circuitRF on this machine that is no " +
              "longer running — most likely a crash. Opening it normally."
            : $"'{workspaceName}' has been marked as open by {info.User} on {info.Host} since " +
              $"{Describe(Now - info.TakenUtc)}, which is long enough that the mark is probably " +
              "left over. Opening it normally.";
    }

    private static string Describe(TimeSpan age)
    {
        if (age < TimeSpan.Zero)             return "just now";     // a clock skew across machines
        if (age.TotalMinutes < 2)            return "a moment ago";
        if (age.TotalMinutes < 90)           return $"about {(int)age.TotalMinutes} minutes ago";
        if (age.TotalHours   < 36)           return $"about {(int)age.TotalHours} hours ago";
        return $"about {(int)age.TotalDays} days ago";
    }

    // ── Take / release ────────────────────────────────────────────────────────

    /// <summary>
    /// R-sl4-1: records that this process has the workspace open, and returns whether the file was
    /// written.
    ///
    /// <para><b>A read-only workspace takes no lock and needs none</b> — nobody can write it, so
    /// there is nothing to lose to a last-writer-wins race, and the one thing worse than not taking a
    /// lock on a shared library would be an error box saying we could not. The writability question
    /// is SL2's probe, memoised, so this costs nothing on a workspace already open. The same test
    /// suppresses the READING side: a session that cannot write is not a party to the race, so it is
    /// never shown the notice either — see <c>WorkspaceViewModel.ConfirmConcurrentOpenAsync</c>.</para>
    ///
    /// <para>Failure to write is silent for the same reason: this is a courtesy to the next person to
    /// open the workspace, not a step in opening it.</para>
    ///
    /// <para><b>It OVERWRITES a lock somebody else holds</b>, which is reached only after the user has
    /// answered "open anyway" to the notice. Considered and kept: the alternative — leaving theirs in
    /// place — means our own close leaves a record of a session that has ended, and the next person is
    /// warned about nobody. Overwriting keeps the file describing a session that is actually running,
    /// which is what a third opener needs to be told. The other session loses its mark and, because
    /// <see cref="Release"/> only removes its own, leaves ours alone.</para>
    /// </summary>
    public static bool Take(string? workspaceDir)
    {
        if (string.IsNullOrWhiteSpace(workspaceDir)) return false;
        if (WorkspaceWritability.IsReadOnly(workspaceDir)) return false;

        try
        {
            var info = new WorkspaceLockInfo(ThisUser, ThisHost, ThisPid, Now);
            File.WriteAllText(
                Path.Combine(workspaceDir, FileName),
                JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// R-sl4-1: removes the lock on close.
    ///
    /// <para><b>Only if it is ours.</b> A lock naming another host, or another process on this one,
    /// belongs to a session that is still running; deleting it because we happened to close a window
    /// would silently disarm the notice for the person who is actually in there. A lock we cannot
    /// read is also left alone — we have no evidence it is ours.</para>
    /// </summary>
    public static void Release(string? workspaceDir)
    {
        if (string.IsNullOrWhiteSpace(workspaceDir)) return;
        if (Read(workspaceDir) is not { } info || !IsOurs(info)) return;
        try { File.Delete(Path.Combine(workspaceDir, FileName)); } catch { /* a courtesy, never a step */ }
    }
}
