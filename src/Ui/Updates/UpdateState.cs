using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Ui.Updates;

/// <summary>
/// The updater's own state — when it last checked, what it has staged, which versions it has given
/// up on, and the startup counter that rollback rests on.
///
/// <para><b>None of this belongs in <c>AppPreferences</c>, and <c>LastCheckUtc</c> least of all.</b>
/// It changes on every check; putting it in <c>preferences.json</c> would rewrite that whole file on
/// a 24-hour timer and race the settings dialog's own load-mutate-save. A preference is something the
/// user chose. This is bookkeeping.</para>
/// </summary>
public sealed class UpdateState
{
    /// <summary>When a check last SUCCEEDED. Null means never — which is what the settings line renders.</summary>
    [JsonPropertyName("last_check_utc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? LastCheckUtc { get; set; }

    /// <summary>The version sitting staged and inert, waiting for the next launch.</summary>
    [JsonPropertyName("staged_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StagedVersion { get; set; }

    /// <summary>True when the staged version came from a prerelease — so unchecking betas can drop
    /// exactly that one and leave a staged stable version alone.</summary>
    [JsonPropertyName("staged_is_prerelease")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? StagedIsPreRelease { get; set; }

    /// <summary>Where the staged payload is: an <c>app-&lt;ver&gt;</c> directory name, or a bundle path.</summary>
    [JsonPropertyName("staged_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StagedPath { get; set; }

    /// <summary>
    /// The version that has been SWAPPED IN and is waiting to prove it starts — which is not the same
    /// thing as <see cref="StagedVersion"/>, and conflating the two made rollback inert on every
    /// platform (found in review, 2026-08-25).
    ///
    /// <para><b>Why they must be separate.</b> On Windows and Linux the swap is a pointer flip
    /// performed by the OLD version, for the NEXT launch: the session that stages and flips runs to
    /// completion as the old version and its window proves nothing about the new one. A single field
    /// meant the counter was raised and then cleared by the version that was not being tested, so the
    /// new version's own first launch carried no counter at all and a crash before its first window
    /// could never reach <see cref="UpdateSwap.MaxFailedStartups"/>.</para>
    /// </summary>
    [JsonPropertyName("pending_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PendingVersion { get; set; }

    /// <summary>Where the pending version lives: an <c>app-&lt;ver&gt;</c> directory NAME, or a bundle path.</summary>
    [JsonPropertyName("pending_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PendingPath { get; set; }

    /// <summary>
    /// The staged bundle path of a macOS exchange that has STARTED but whose bookkeeping has not been
    /// written yet. Null the rest of the time, which is almost always.
    ///
    /// <para><b>It exists because the exchange and the record of it are two operations.</b> On macOS
    /// <c>SwapBundle</c> exchanges the installed bundle with the staged one and <c>RecordSwap</c>
    /// writes what happened only after it returns. A process killed between the two left this file
    /// still advertising the new version as STAGED while the disk already had it INSTALLED — so the
    /// next launch exchanged the pair straight back, <c>execv</c>ed the old version, and then released
    /// <c>updates/previous</c>, destroying the update. The user was silently downgraded and the
    /// download was thrown away, with nothing anywhere saying so.</para>
    ///
    /// <para>Written before the first thing that moves on disk and cleared by the record that
    /// supersedes it, so a launch that finds it set knows a swap was interrupted.
    /// <c>UpdateSwap.ResolveInterruptedSwap</c> is what reads it.</para>
    /// </summary>
    [JsonPropertyName("swap_in_progress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SwapInProgress { get; set; }

    /// <summary>The version that was running before the last swap, retained as rollback insurance.</summary>
    [JsonPropertyName("previous_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousVersion { get; set; }

    /// <summary>
    /// The previous version's <c>app-&lt;ver&gt;</c> directory name, in the versioned layout. Null on
    /// macOS, where the previous bundle is at <see cref="UpdatePaths.Previous"/> and needs no name.
    /// </summary>
    [JsonPropertyName("previous_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousPath { get; set; }

    /// <summary>
    /// Startup attempts by the PENDING version that have not yet reached a visible window. Raised by
    /// that version's own launch, cleared once its window appears; two failures revert.
    /// </summary>
    [JsonPropertyName("launch_attempts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LaunchAttempts { get; set; }

    /// <summary>
    /// One line to post at the next window that actually opens.
    ///
    /// <para><b>Persisted rather than held in memory</b>, because the only thing that ever writes it
    /// is a rollback — and a rollback happens precisely because the process keeps dying before it has
    /// a Message Panel to write to. An in-memory notice from a version that cannot start is a notice
    /// nobody ever reads.</para>
    /// </summary>
    [JsonPropertyName("pending_notice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PendingNotice { get; set; }

    /// <summary>Versions that failed verification or failed to start. Never retried, so a bad release
    /// cannot put the updater in a download loop.</summary>
    [JsonPropertyName("blacklist")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Blacklist { get; set; }

    /// <summary>A manifest's allow-listed <c>feedUrl</c>, once one has been seen. Design §15.4.</summary>
    [JsonPropertyName("feed_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FeedUrl { get; set; }

    /// <summary>When the "not enough disk space" line was last posted. At most one per 30 days.</summary>
    [JsonPropertyName("last_space_notice_utc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? LastSpaceNoticeUtc { get; set; }

    /// <summary>
    /// The version whose Release Notes have already been put in front of the user — the one field
    /// that makes that dialog show <b>once per version</b> rather than on every launch.
    ///
    /// <para><b>Bookkeeping, so it lives here and not in <c>preferences.json</c></b>, by this file's
    /// own rule: a preference is something the user chose, and this is a record of what they have
    /// been shown. The choice — whether to be shown anything at all — is
    /// <c>AppPreferences.ShowReleaseNotes</c>, and the two are deliberately not the same key.</para>
    ///
    /// <para><b>Null does not mean "show them".</b> It means nothing has been recorded yet, and what
    /// that implies depends on whether this installation existed before the launch that found it null
    /// — a distinction <see cref="ReleaseNotesGate"/> owns, because a clean install must never open
    /// with the notes for a version the user has never run anything else.</para>
    /// </summary>
    [JsonPropertyName("release_notes_shown_for")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReleaseNotesShownFor { get; set; }

    /// <summary>Versions already announced in the Message Panel, so a relaunch does not repeat a line.</summary>
    [JsonPropertyName("announced")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Announced { get; set; }

    /// <summary>
    /// How many entries the two append-only lists keep. Both would otherwise grow without bound in a
    /// file that is read on every launch, and both are caches of a decision rather than a record: a
    /// version old enough to have fallen off either list is a version no live release list still
    /// offers.
    /// </summary>
    public const int HistoryCap = 64;

    public bool IsBlacklisted(string version)
        => Blacklist is not null && Blacklist.Contains(version, StringComparer.Ordinal);

    public void Blacklist_Add(string version)
    {
        Blacklist ??= [];
        if (!Blacklist.Contains(version, StringComparer.Ordinal)) Blacklist.Add(version);
        Trim(Blacklist);
    }

    public void Announced_Add(string version)
    {
        Announced ??= [];
        if (!Announced.Contains(version, StringComparer.Ordinal)) Announced.Add(version);
        Trim(Announced);
    }

    private static void Trim(List<string> list)
    {
        if (list.Count > HistoryCap) list.RemoveRange(0, list.Count - HistoryCap);
    }
}

/// <summary>Reads and writes <see cref="UpdateState"/>, with the same never-throw shape as preferences.</summary>
public static class UpdateStateIo
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static UpdateState Load()
    {
        try
        {
            if (File.Exists(UpdatePaths.StateFile))
                return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(UpdatePaths.StateFile), Opts)
                       ?? new UpdateState();
        }
        catch { /* corrupt state — start fresh; it is all recoverable bookkeeping */ }
        return new UpdateState();
    }

    /// <summary>
    /// Writes via a temp file and a rename, for the same reason <c>current</c> is (design §13.2): a
    /// truncating write that fails with ENOSPC leaves a zero-byte file behind, and this one records
    /// which version is staged.
    /// </summary>
    public static void Save(UpdateState state)
    {
        try
        {
            Directory.CreateDirectory(UpdatePaths.Root);
            string tmp = UpdatePaths.StateFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Opts));
            AtomicFile.ReplaceOrMove(tmp, UpdatePaths.StateFile);
        }
        catch { /* non-critical */ }
    }

    /// <summary>Load, mutate, save — so a partial write cannot clobber the other fields.</summary>
    public static void Update(Action<UpdateState> mutate)
    {
        UpdateState s = Load();
        mutate(s);
        Save(s);
    }
}
