using System.Threading.Tasks;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// SL4 §1 (brief-shared-library-4-concurrency-and-latency.md): two people, one workspace, and an
/// ADVISORY notice about it.
///
/// <para><c>SwitchToWorkspace</c> has always refused to open one workspace twice, and its own comment
/// gives the reason exactly — two view models over one <c>.cws</c> means two independent edit-session
/// registries over the same files, two undo stacks, two dirty flags, last-save-wins. That reasoning
/// is entirely correct and entirely <b>process-local</b>: the check is <c>App.WindowShowing</c>, which
/// enumerates this process's windows. Every consequence it names is equally true of two people on two
/// machines, and none of it was detected there.</para>
///
/// <para>The mechanism is <see cref="WorkspaceLock"/>, in <c>src/Design</c>. This file is the two
/// places the product touches it: the question asked before an open, and the release on the way
/// out.</para>
/// </summary>
public partial class WorkspaceViewModel
{
    /// <summary>
    /// R-sl4-1/-2/-3: the advisory check, run before a workspace is opened by a user gesture. Returns
    /// false only when the user cancelled; every other path returns true, because <b>this feature
    /// never denies access</b> (R-sl-8).
    ///
    /// <para>The three outcomes:</para>
    /// <list type="bullet">
    /// <item><b>No lock, or our own</b> — nothing is said and nothing is asked.</item>
    /// <item><b>A lock the two staleness rules call abandoned</b> (R-sl4-3) — one Info line saying
    /// which rule fired, and the open proceeds. A stale lock must not cost anybody a dialog: that is
    /// how "advisory" turns back into "a file that locks out a team".</item>
    /// <item><b>A live-looking lock</b> — the notice, and the user's choice of read-only, anyway, or
    /// cancel.</item>
    /// </list>
    ///
    /// <para>The read-only answer routes through <see cref="WorkspaceWritability.OpenReadOnlyThisSession"/>
    /// rather than a flag of its own, so it inherits every behaviour SL2 already built and tested for
    /// a locked-down share — Save disabled with a reason, the <c>.cws</c> write choke point skipping
    /// silently, the provenance band, Save As on quit. "A workspace we have chosen not to write" and
    /// "a workspace we cannot write" want identical behaviour from all of them.</para>
    /// </summary>
    private async Task<bool> ConfirmConcurrentOpenAsync(string cwsPath)
    {
        string? dir = Path.GetDirectoryName(cwsPath);
        if (dir is null) return true;

        string name = Path.GetFileName(dir) ?? cwsPath;

        // The answer is asked afresh on every open — a choice made about a lock that has since gone
        // must not keep a workspace read-only for the rest of the session.
        WorkspaceWritability.ClearSessionReadOnly(dir);

        // A workspace the filesystem will not let us write cannot be a party to last-writer-wins, and
        // that is the ONLY thing this notice exists to bound. The shared library is exactly this case
        // — read-only to everyone but the librarian — so warning every engineer who opens it that the
        // librarian is in there would put a modal in front of the workflow the whole series was
        // written to support, about a hazard they cannot cause. SL2's open-time line already tells
        // them the fact that actually applies to them.
        if (WorkspaceWritability.IsReadOnly(dir)) return true;

        if (WorkspaceLock.Read(dir) is not { } held) return true;
        if (WorkspaceLock.IsOurs(held)) return true;

        if (WorkspaceLock.IsStale(held))
        {
            Messages.Info(WorkspaceLock.StaleNoticeFor(held, name));
            return true;
        }

        // No window to ask in — a headless or test host, or an open driven from a background route.
        // Proceeding is the honest default: refusing would be this feature denying access, which is
        // the one thing R-sl4-2 forbids it from doing. The notice is still recorded.
        if (ResolveOwner(null) is not { } owner)
        {
            Messages.Warning(WorkspaceLock.NoticeFor(held, name));
            return true;
        }

        var choice = await Views.Dialogs.WorkspaceInUseDialog.AskAsync(
            owner, name, WorkspaceLock.NoticeFor(held, name));

        switch (choice)
        {
            case Views.Dialogs.WorkspaceInUseDialog.Choice.ReadOnly:
                WorkspaceWritability.OpenReadOnlyThisSession(dir);
                return true;

            case Views.Dialogs.WorkspaceInUseDialog.Choice.OpenAnyway:
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// R-sl4-1: takes the lock on the workspace just opened, and drops the one on the workspace being
    /// left. Called from <c>SwitchToWorkspace</c> once the switch has committed.
    ///
    /// <para>A workspace opened READ-ONLY — whether because the filesystem says so or because the
    /// user chose it at the notice — takes no lock, and needs none: nothing it does can lose anyone
    /// else's work, so there is nothing for the next person to be warned about. That check lives
    /// inside <see cref="WorkspaceLock.Take"/>, which asks SL2's memoised probe.</para>
    /// </summary>
    private void MoveWorkspaceLock(string? leavingCwsPath, string enteringCwsPath)
    {
        if (leavingCwsPath is { Length: > 0 })
        {
            WorkspaceLock.Release(Path.GetDirectoryName(leavingCwsPath));
            WorkspaceWritability.ClearSessionReadOnly(Path.GetDirectoryName(leavingCwsPath));
        }
        WorkspaceLock.Take(Path.GetDirectoryName(enteringCwsPath));
    }

    /// <summary>R-sl4-1: the workspace is closing — with the window, with the app, or because the
    /// shell was reset. Releases only a lock this process took (see
    /// <see cref="WorkspaceLock.Release"/>).</summary>
    private void ReleaseWorkspaceLock()
    {
        if (CurrentWorkspacePath is not { } cws) return;
        string? dir = Path.GetDirectoryName(cws);
        WorkspaceLock.Release(dir);
        WorkspaceWritability.ClearSessionReadOnly(dir);
    }
}
