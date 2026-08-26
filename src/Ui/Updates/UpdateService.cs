using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Updates;

/// <summary>Why a check ended. Only two of these are ever visible to the user.</summary>
public enum CheckOutcome
{
    /// <summary>Automatic updates are off, or an override forbids them. No socket was opened.</summary>
    Disabled,

    /// <summary>This install cannot write itself — the .msi and the .deb. Notify-only.</summary>
    NotifyOnly,

    /// <summary>Nothing newer on this channel. The overwhelmingly common outcome.</summary>
    UpToDate,

    /// <summary>A new version is staged and inert. One Message Panel line was posted.</summary>
    Staged,

    /// <summary>Not enough disk. One of only two conditions allowed to be visible (design §13.5).</summary>
    InsufficientSpace,

    /// <summary>Network, DNS, timeout, rate limit, verification failure. Silent.</summary>
    Failed,

    /// <summary>Skipped by the 24-hour throttle.</summary>
    Throttled,
}

/// <summary>What a check concluded, with the figures the two visible cases need.</summary>
public sealed record CheckResult(
    CheckOutcome Outcome,
    string? Version = null,
    long RequiredBytes = 0,
    long AvailableBytes = 0,
    string Detail = "");

/// <summary>
/// The whole update lifecycle, in one place: check, space, download, verify, stage, notify.
///
/// <para><b>The order is the design</b> (R-AU-23): reclaim, check space, download, verify, unpack,
/// rename into place. Everything expensive happens where abandoning costs nothing, and by the time
/// anything the running install depends on is touched, only renames remain. A disk that fills
/// mid-update therefore loses a download, never an installation.</para>
///
/// <para><b>Failure is silent.</b> No Message Panel entry, no dialog, no toast, for an unreachable
/// network, a timeout, a rate limit or a verification failure. An unreachable network is the NORMAL
/// state for a large fraction of this application's users — lab machines, air-gapped networks, hotel
/// wifi — and a recurring "couldn't check for updates" line would be a defect, not a feature.</para>
/// </summary>
public sealed class UpdateService
{
    /// <summary>At most one line per 30 days when a full disk is the sole reason updates are not happening.</summary>
    public static readonly TimeSpan SpaceNoticeInterval = TimeSpan.FromDays(30);

    /// <summary>One check per day, with jitter applied by the scheduler.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>Where a notify-only install is pointed. The one URL this subsystem shows a user.</summary>
    public const string ReleasesPageUrl = "https://github.com/potatobeanradio/circuitRF/releases";

    /// <summary>
    /// Why a WRITABLE install is still notify-only: the running binary carries no publisher identity,
    /// so R-AU-25's third step has nothing to compare a payload against.
    ///
    /// <para>It is a distinct sentence from the read-only one because the cause is different and the
    /// remedy belongs to a different person — an unsigned build is fixed by whoever built it, and
    /// telling the user their installation is in the wrong place would send them to re-install
    /// something that is exactly where it should be.</para>
    /// </summary>
    public const string UnsignedBuildReason =
        "This build is not signed, so it cannot verify that an update came from us and will not "
        + "install one.";

    /// <summary>Marks a check that found the newest version already downloaded. Not an error.</summary>
    public const string AlreadyStagedDetail = "already staged";

    private readonly Func<IUpdateFeed> _feedFactory;
    private readonly IFreeSpaceProbe _space;
    private readonly IMessageSink? _messages;
    private readonly Func<InstallSite> _site;

    public UpdateService(
        Func<IUpdateFeed> feedFactory,
        IFreeSpaceProbe space,
        IMessageSink? messages,
        Func<InstallSite>? site = null)
    {
        _feedFactory = feedFactory;
        _space       = space;
        _messages    = messages;
        _site        = site ?? UpdateInstallSite.Detect;
    }

    /// <summary>How many times the feed was constructed. The counter R-AU-44's gate reads.</summary>
    public int FeedsCreated { get; private set; }

    /// <summary>
    /// One complete check.
    ///
    /// <para><paramref name="manual"/> is Help ▸ Check for Updates…: it ignores the 24-hour throttle
    /// and is the one place a network failure may be reported, because the user explicitly asked.</para>
    /// </summary>
    public async Task<CheckResult> CheckAsync(bool manual, CancellationToken ct)
    {
        UpdatePolicyState policy = UpdatePolicy.Current;

        // R-AU-44: "never checks" is LITERAL. The preference is read BEFORE an HttpClient is
        // constructed, not consulted afterwards to decide whether to act on the result. With
        // automatic updates off, circuitRF opens no socket for any reason.
        if (!policy.AutomaticUpdates)
            return new CheckResult(CheckOutcome.Disabled, Detail: policy.Reason);

        InstallSite site = _site();

        // WHY THIS IS NOT A RETURN. R-AU-1 and design §1.1 both say a read-only install is
        // NOTIFY-ONLY — "they check, and post a Message Panel line with a link, but never write".
        // Returning here skipped the CHECK as well as the write, so every .msi, .deb and
        // standard-user macOS install was silently never told a new version existed (found in a
        // second review, 2026-08-25). The half that must not happen is the WRITING; the check is
        // the point of the mode.
        string? notifyOnlyReason = null;
        if (!site.CanSelfUpdate)
        {
            notifyOnlyReason = $"This {UpdateApp.Name} installation is in a location this account "
                             + "cannot write, so it does not update itself.";
        }
        else if (!await PayloadVerifier.RunningBuildCanAcceptUpdatesAsync(site, ct).ConfigureAwait(false))
        {
            // R-AU-25's publisher check compares the staged payload against the RUNNING application,
            // so a running build with no publisher identity of its own can never accept anything — an
            // ad-hoc macOS bundle or an unsigned Windows publish. Asking that BEFORE the feed rather
            // than after the unpack stops a developer build fetching and discarding the full ~160 MB
            // payload on a timer, and stops the refusal being recorded in the blacklist — which is
            // shared with the real installation on the same machine, since AppDataRoot is one
            // directory, and would have permanently withheld a perfectly good release from it.
            //
            // It is a NOTIFY-ONLY reason and not a silent stop, because the cause is fixable by
            // whoever built the binary and invisible to whoever is running it.
            notifyOnlyReason = UnsignedBuildReason;
        }

        UpdateState state = UpdateStateIo.Load();

        if (!manual && state.LastCheckUtc is DateTime last &&
            DateTime.UtcNow - last < CheckInterval)
            return new CheckResult(CheckOutcome.Throttled);

        if (!SemanticVersion.TryParse(AppVersion.Display, out SemanticVersion? running) || running is null)
            return new CheckResult(CheckOutcome.Failed, Detail: "the running version is not a version");

        UpdatePlatform? platform = UpdateAssetNames.CurrentPlatform();
        if (platform is null) return new CheckResult(CheckOutcome.Failed, Detail: "unsupported platform");

        // ── 1. reclaim our own debris, BEFORE measuring space ────────────────────────────────
        var reclaimer = new UpdateReclaimer(
            UpdatePaths.Root,
            site.Shape == InstallShape.VersionedPointer ? site.Root : null,
            previousVersionDirectoryName: state.PreviousPath);

        // Not on the notify-only path: that mode writes nothing, and it has no debris of its own
        // because it never staged anything.
        if (notifyOnlyReason is null)
            try { reclaimer.ReclaimDebris(); } catch { /* the next launch tries again */ }

        // ── 2. the feed ──────────────────────────────────────────────────────────────────────
        UpdateCandidate? candidate;
        try
        {
            FeedsCreated++;
            IUpdateFeed feed = _feedFactory();

            candidate = await UpdateSelector.SelectAsync(
                feed, await feed.ListReleasesAsync(ct).ConfigureAwait(false),
                running, policy.IncludeBetas, UpdateApp.Name, platform.Value,
                RuntimeInformation.ProcessArchitecture, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new CheckResult(CheckOutcome.Failed, Detail: "cancelled"); }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException)
        {
            return new CheckResult(CheckOutcome.Failed, Detail: e.Message);
        }

        // A successful CHECK updates the timestamp even when nothing came of it — otherwise a
        // machine with no updates available would re-ask every time the app started.
        UpdateStateIo.Update(s =>
        {
            s.LastCheckUtc = DateTime.UtcNow;
            if (candidate?.FeedUrl is not null) s.FeedUrl = candidate.FeedUrl;
        });

        if (candidate is null)
            return new CheckResult(CheckOutcome.UpToDate, Detail: notifyOnlyReason ?? "");

        string version = candidate.Release.VersionText;

        // ── 2b. notify-only: one line with a link, and not one byte written ─────────────────
        if (notifyOnlyReason is not null)
        {
            AnnounceNotifyOnly(version, notifyOnlyReason, manual);
            return new CheckResult(CheckOutcome.NotifyOnly, version, Detail: notifyOnlyReason);
        }

        if (state.IsBlacklisted(version))
            return new CheckResult(CheckOutcome.UpToDate, Detail: "blacklisted");

        if (string.Equals(state.StagedVersion, version, StringComparison.Ordinal))
            return new CheckResult(CheckOutcome.Staged, version, Detail: AlreadyStagedDetail);

        // A version that has ALREADY been swapped in and is waiting to prove it starts is not a
        // version to fetch again — and re-fetching it was destructive, not merely wasteful (found in
        // a second review, 2026-08-25). `RecordSwap` clears StagedVersion when it flips the pointer,
        // so only PendingVersion records it; without this guard Help ▸ Check for Updates… (which
        // ignores the throttle) re-ran the whole fetch with destinationDir = <root>/app-<version>,
        // and UpdateStager.Promote deletes an existing destination before renaming into it — i.e. it
        // deleted the directory `current` names. A failure or a crash between those two steps leaves
        // the stub with a pointer to a directory that is not there and an application that will not
        // start. UpdateStager.Promote now refuses that delete as well; this is the cheap half.
        if (string.Equals(state.PendingVersion, version, StringComparison.Ordinal))
            return new CheckResult(CheckOutcome.Staged, version, Detail: AlreadyStagedDetail);

        // ── 3. space, against PEAK, before a byte is transferred ─────────────────────────────
        long download = candidate.Asset.Size > 0 ? candidate.Asset.Size : 0;
        long expanded = UpdateSpace.EstimateExpandedBytes(download);
        long required = UpdateSpace.RequiredFreeSpace(download, expanded);

        Directory.CreateDirectory(UpdatePaths.Staging);

        bool Enough() => _space.AvailableFreeSpace(UpdatePaths.Staging) >= required;

        if (!Enough())
        {
            reclaimer.ReclaimUntil(Enough, previousVersionReleasable: (state.LaunchAttempts ?? 0) == 0);

            if (!Enough())
            {
                long available = _space.AvailableFreeSpace(UpdatePaths.Staging);
                ReportSpace(manual, required, available);
                return new CheckResult(CheckOutcome.InsufficientSpace, version, required, available);
            }
        }

        // ── 4-6. download, verify, stage ────────────────────────────────────────────────────
        return await FetchVerifyStageAsync(candidate, site, required, running.ToString(), manual, ct)
                     .ConfigureAwait(false);
    }

    private async Task<CheckResult> FetchVerifyStageAsync(
        UpdateCandidate candidate, InstallSite site, long required, string runningVersion,
        bool manual, CancellationToken ct)
    {
        string version = candidate.Release.VersionText;

        // A SEPARATE client from the feed's: HttpClient.Timeout bounds the whole operation including
        // the response body, so the feed's 30 s would be 30 s to move 160 MB. Progress is policed by
        // an idle timeout inside the read loop instead.
        using HttpClient http = UpdateDownloader.CreateDownloadHttpClient();
        var downloader = new UpdateDownloader(http, _space);

        DownloadResult dl = await downloader
            .DownloadAsync(candidate.Asset, UpdatePaths.Staging, required, null, ct).ConfigureAwait(false);

        if (dl.Outcome == DownloadOutcome.OutOfSpace)
        {
            long available = _space.AvailableFreeSpace(UpdatePaths.Staging);
            ReportSpace(manual, required, available);
            return new CheckResult(CheckOutcome.InsufficientSpace, version, required, available);
        }

        if (dl.Outcome != DownloadOutcome.Completed || dl.Path is null)
            return new CheckResult(CheckOutcome.Failed, version, Detail: dl.Outcome.ToString());

        // A SIGNED manifest's SHA-256 is not best-effort — it is the whole of the integrity guarantee
        // on Linux and for the Windows Python payload, because it is the only thing carrying the
        // signature's proof through to the bytes (design §15.5). UpdateSelector already refuses a
        // signed manifest that names no hash; this is the assertion at the point of use, so that no
        // future path can arrive here having skipped it.
        if (candidate.ManifestSigned && !UpdateManifest.IsSha256Hex(candidate.Asset.Sha256))
            return Reject(version, dl.Path, null, "a signed manifest named no digest for this asset",
                          verificationFailure: true);

        // Hash first — cheap, and it catches a truncated or substituted transfer before an unpack.
        VerifyResult hash = await PayloadVerifier
            .VerifyHashAsync(dl.Path, candidate.Asset.Sha256, ct).ConfigureAwait(false);

        if (!hash.Ok) return Reject(version, dl.Path, null, hash.Detail, verificationFailure: true);

        var stager = new UpdateStager(_space);
        StageResult staged;

        // The signature and the identity run against the .partial tree, BEFORE the rename that gives
        // it a real name (R-AU-27). Verifying afterwards left an unverified app-<ver> holding a real
        // name in the live install root whenever the process died in between.
        Task<VerifyResult> Verify(string partialPath) => VerifyStagedAsync(site, partialPath, ct);

        if (site.Shape == InstallShape.MacOsBundle)
        {
            // The CONTAINER, before hdiutil is handed it. Mounting is the one step that gives
            // attacker-supplied bytes to a kernel filesystem parser, so it does not happen until the
            // image itself carries our Developer ID (security review, 2026-08-25).
            VerifyResult image = await PayloadVerifier
                .VerifyMacImageAsync(dl.Path, site.Root, ct).ConfigureAwait(false);

            if (!image.Ok) return Reject(version, dl.Path, null, image.Detail, verificationFailure: true);

            string bundleName = UpdateApp.Name + ".app";
            Directory.CreateDirectory(UpdatePaths.Staged);

            staged = await stager.StageMacBundleAsync(
                dl.Path, bundleName,
                Path.Combine(UpdatePaths.Staged, version, bundleName), ct, Verify).ConfigureAwait(false);
        }
        else
        {
            string exe = OperatingSystem.IsWindows() ? UpdateApp.Name + ".exe" : UpdateApp.Name;
            staged = await stager.StageArchiveAsync(
                dl.Path, Path.Combine(site.Root, UpdateInstallSite.VersionDirPrefix + version),
                exe, ct, Verify).ConfigureAwait(false);
        }

        if (!staged.Ok || staged.StagedPath is null)
            return Reject(version, dl.Path, staged.StagedPath, staged.Detail,
                          verificationFailure: staged.Outcome == StageOutcome.VerificationFailed,
                          installRoot: site.Root);

        // The download can go the moment unpacking succeeded — which drops the requirement from peak
        // to transient partway through, even though the CHECK was made against peak.
        try { File.Delete(dl.Path); } catch { /* the reclaim takes it */ }

        UpdateStateIo.Update(s =>
        {
            s.StagedVersion      = version;
            s.StagedIsPreRelease = candidate.Release.IsPreRelease;
            s.StagedPath = site.Shape == InstallShape.MacOsBundle
                ? staged.StagedPath
                : UpdateInstallSite.VersionDirPrefix + version;
        });

        Announce(runningVersion, version);
        return new CheckResult(CheckOutcome.Staged, version);
    }

    private static async Task<VerifyResult> VerifyStagedAsync(InstallSite site, string stagedPath, CancellationToken ct)
    {
        if (site.Shape == InstallShape.MacOsBundle)
            return await PayloadVerifier.VerifyMacBundleAsync(stagedPath, site.Root, ct).ConfigureAwait(false);

        if (OperatingSystem.IsWindows())
        {
            string exe = UpdateApp.Name + ".exe";
            return PayloadVerifier.VerifyWindowsTree(
                stagedPath, Path.Combine(AppContext.BaseDirectory, exe), exe);
        }

        // Linux has no signing infrastructure to check against; TLS and the hash are what there is.
        return new VerifyResult(VerifyOutcome.NotApplicable, "no platform signature to check");
    }

    /// <summary>
    /// Discard and say nothing — and <b>blacklist only when verification is what failed</b>.
    ///
    /// <para>The blacklist is permanent, and it is shared: <c>AppDataRoot</c> is one directory for
    /// all three applications and every build of them, so an entry withholds that release from the
    /// real installation on the machine too. That is the right price for a payload that was not
    /// signed by us — it is not a thing to retry — and the wrong price for a <c>tar</c> that was not
    /// on the box, a file another process had open, or any of the other transient reasons an unpack
    /// fails. Those used to blacklist as well, which stranded a user on their current version
    /// permanently and silently, with the next check's log line the only evidence anywhere (security
    /// review, 2026-08-25). A transient failure now simply retries at the next check.</para>
    /// </summary>
    private static CheckResult Reject(
        string version, string? download, string? staged, string detail, bool verificationFailure,
        string? installRoot = null)
    {
        UpdateStager.Discard(staged, installRoot);
        try { if (download is not null) File.Delete(download); } catch { /* the reclaim takes it */ }

        if (verificationFailure) UpdateStateIo.Update(s => s.Blacklist_Add(version));
        return new CheckResult(CheckOutcome.Failed, version, Detail: detail);
    }

    // ── the only two things the user ever sees ───────────────────────────────────────────────

    /// <summary>
    /// R-AU-47. One Info line per staged version, named after the RUNNING application and worded
    /// from the installed version to the newly staged one — so if several versions stage before the
    /// user relaunches, the last line on screen is always the true end state.
    ///
    /// <para><b>There is no "Relaunch" button, anywhere, in any form.</b> The app can be holding
    /// unsaved workspaces; a one-click relaunch invites data loss to save a keystroke.</para>
    /// </summary>
    private void Announce(string from, string to)
    {
        UpdateState s = UpdateStateIo.Load();
        if (s.Announced is not null && s.Announced.Contains(to)) return;

        _messages?.Info(
            $"{UpdateApp.Name} updated from {from} to {to} in the background. "
            + $"Relaunch {UpdateApp.Name} to start using the version. "
            + "Automatic updates can be disabled in Settings, under Security & Permissions.");

        UpdateStateIo.Update(st => st.Announced_Add(to));
    }

    /// <summary>
    /// R-AU-1's other half. A read-only install — the per-machine <c>.msi</c>, the <c>.deb</c>,
    /// <c>/Applications</c> as a standard user — <b>checks, and says so once</b>. It is the only
    /// thing those users ever get from this subsystem, and skipping it made the whole mode
    /// indistinguishable from the feature being off.
    ///
    /// <para>Once per version in the BACKGROUND, through the same <c>Announced</c> list the staging
    /// line uses, so a machine that checks daily for a month does not post the same line thirty
    /// times — and <b>every time on a manual check</b>, because a user who has just picked
    /// Help ▸ Check for Updates… and gets silence has been told nothing. Same asymmetry as
    /// <see cref="ReportSpace"/>, for the same reason.</para>
    ///
    /// <para>And no button: the user goes to the releases page in their own browser, in their own
    /// time (R-AU-48).</para>
    /// </summary>
    private void AnnounceNotifyOnly(string version, string reason, bool manual)
    {
        UpdateState s = UpdateStateIo.Load();
        if (!manual && s.Announced is not null && s.Announced.Contains(version)) return;

        _messages?.Info(
            $"{UpdateApp.Name} {version} is available. {reason} "
            + $"You can download it from {ReleasesPageUrl}");

        UpdateStateIo.Update(st => st.Announced_Add(version));
    }

    /// <summary>
    /// The one exception to silence, and it is narrow. Being offline is often permanent and often
    /// intentional; a full disk is an accident the user wants to know about and can act on. So: the
    /// manual check reports it with figures, and the background check posts at most ONE line per
    /// 30 days — information, not nagging.
    /// </summary>
    private void ReportSpace(bool manual, long required, long available)
    {
        string line = $"{UpdateApp.Name} needs about {UpdateSpace.FormatBytes(required)} of free disk "
                    + $"space to install the update and there is {UpdateSpace.FormatBytes(available)} "
                    + "available. The update was not downloaded.";

        if (manual) { _messages?.Warning(line); return; }

        UpdateState s = UpdateStateIo.Load();
        if (s.LastSpaceNoticeUtc is DateTime when && DateTime.UtcNow - when < SpaceNoticeInterval) return;

        _messages?.Warning(line);
        UpdateStateIo.Update(st => st.LastSpaceNoticeUtc = DateTime.UtcNow);
    }
}
