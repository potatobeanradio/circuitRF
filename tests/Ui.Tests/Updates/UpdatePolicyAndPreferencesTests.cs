using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Ui;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Updates;
using Xunit;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// R-AU-37 / R-AU-39 / R-AU-44 / R-AU-45 / R-AU-46 — the settings, their defaults, the override
/// precedence, and what changing one actually does.
///
/// <para>Serialized as a collection because <see cref="AppDataRoot"/> and the
/// <c>CRF_NO_UPDATE_CHECK</c> environment variable are both process-global: a parallel test
/// redirecting the state directory underneath another one would make every assertion here
/// nondeterministic.</para>
/// </summary>
[Collection(AppDataRootCollection.Name)]
public sealed class UpdatePolicyAndPreferencesTests : IDisposable
{
    private readonly string _root;
    private readonly string _install;

    public UpdatePolicyAndPreferencesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-update-prefs-" + Guid.NewGuid().ToString("N")[..8]);
        _install = Path.Combine(_root, "install");
        Directory.CreateDirectory(_install);
        AppDataRoot.RedirectTo(_root);
        Environment.SetEnvironmentVariable(UpdatePolicy.EnvironmentVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(UpdatePolicy.EnvironmentVariable, null);
        AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private string PrefsPath => Path.Combine(_root, "preferences.json");

    private InstallSite Site() => new(_install, InstallShape.VersionedPointer, true, _install);

    // ── the two defaults ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-AU-37. With NO preferences.json present at all — the fresh-install case — automatic updates
    /// resolve ON and betas OFF. Absence IS the default; a seeded file would pass a weaker test.
    /// </summary>
    [Fact]
    public void FreshInstall_ResolvesUpdatesOn_AndBetasOff_WithNoFileAtAll()
    {
        Assert.False(File.Exists(PrefsPath));

        UpdatePolicyState p = UpdatePolicy.Resolve(_install, AppPreferencesIo.Load());

        Assert.True(p.AutomaticUpdates);
        Assert.False(p.IncludeBetas);
        Assert.False(p.IsOverridden);

        // ...and reading a preference did not create the file.
        Assert.False(File.Exists(PrefsPath));
    }

    [Fact]
    public void TheTwoDefaultsDiffer_AndBothComeFromTheNullableIdiom()
    {
        var empty = new AppPreferences();
        Assert.Null(empty.AutomaticUpdates);
        Assert.Null(empty.IncludeBetaUpdates);

        Assert.True(empty.AutomaticUpdates ?? true);
        Assert.False(empty.IncludeBetaUpdates ?? false);
    }

    [Fact]
    public void NeitherKeyIsEverWrittenUnlessTheUserSetsIt()
    {
        AppPreferencesIo.Save(new AppPreferences { ActiveThemeName = "Default" });
        string json = File.ReadAllText(PrefsPath);

        Assert.DoesNotContain("automatic_updates", json);
        Assert.DoesNotContain("include_beta_updates", json);
    }

    [Fact]
    public void TheKeysRoundTripWhenTheUserDoesSetThem()
    {
        AppPreferencesIo.Update(p => { p.AutomaticUpdates = false; p.IncludeBetaUpdates = true; });

        string json = File.ReadAllText(PrefsPath);
        Assert.Contains("\"automatic_updates\": false", json);
        Assert.Contains("\"include_beta_updates\": true", json);

        AppPreferences back = AppPreferencesIo.Load();
        Assert.False(back.AutomaticUpdates);
        Assert.True(back.IncludeBetaUpdates);
    }

    /// <summary>
    /// R-AU-37's closing instruction: Migrate exists for RENAMES, and these are new keys with no
    /// retired spelling. Adding a no-op line to it would misrepresent what it is for.
    /// </summary>
    [Fact]
    public void MigrateHasNoEntryForTheNewKeys()
    {
        string src = UpdateInstallSiteTests.SourceFile("src/Ui/Theming/AppPreferences.cs");
        int i = src.IndexOf("public static AppPreferences Migrate", StringComparison.Ordinal);
        Assert.True(i > 0);

        string body = UpdateInstallSiteTests.StripComments(src[i..src.IndexOf("public static void Save", i, StringComparison.Ordinal)]);

        Assert.DoesNotContain("AutomaticUpdates", body);
        Assert.DoesNotContain("IncludeBetaUpdates", body);
    }

    // ── override precedence ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePolicyFileBeatsTheEnvironmentBeatsThePreference()
    {
        var on = new AppPreferences { AutomaticUpdates = true, IncludeBetaUpdates = true };

        // 3 — the preference, with nothing above it.
        Assert.True(UpdatePolicy.Resolve(_install, on).AutomaticUpdates);

        // 2 — the environment beats the preference.
        Environment.SetEnvironmentVariable(UpdatePolicy.EnvironmentVariable, "1");
        UpdatePolicyState env = UpdatePolicy.Resolve(_install, on);
        Assert.False(env.AutomaticUpdates);
        Assert.Equal(UpdateOverride.Environment, env.Override);

        // 1 — the policy file beats the environment, and therefore everything.
        File.WriteAllText(Path.Combine(_install, UpdatePolicy.PolicyFileName), "");
        UpdatePolicyState file = UpdatePolicy.Resolve(_install, on);
        Assert.False(file.AutomaticUpdates);
        Assert.Equal(UpdateOverride.PolicyFile, file.Override);
    }

    /// <summary>
    /// Under either override the checkbox must render DISABLED, WITH THE REASON — a checkbox the
    /// user can tick that changes nothing is worse than one they cannot. The reason is what the
    /// dialog shows, so it has to say something.
    /// </summary>
    [Fact]
    public void AnOverrideCarriesAUserFacingReason()
    {
        Environment.SetEnvironmentVariable(UpdatePolicy.EnvironmentVariable, "1");
        UpdatePolicyState env = UpdatePolicy.Resolve(_install, new AppPreferences());
        Assert.True(env.IsOverridden);
        Assert.Contains(UpdatePolicy.EnvironmentVariable, env.Reason);

        Environment.SetEnvironmentVariable(UpdatePolicy.EnvironmentVariable, null);
        File.WriteAllText(Path.Combine(_install, UpdatePolicy.PolicyFileName), "");
        UpdatePolicyState file = UpdatePolicy.Resolve(_install, new AppPreferences());
        Assert.True(file.IsOverridden);
        Assert.Contains("administrator", file.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// On macOS the install root is the .app itself, and an administrator drops the policy file NEXT
    /// TO the bundle — putting it inside would break the bundle's code signature, which is a
    /// spectacular way to disable an application while meaning to disable its updater.
    /// </summary>
    [Fact]
    public void ThePolicyFileIsHonouredBesideAMacBundleAsWellAsInsideAnInstallRoot()
    {
        string bundle = Path.Combine(_root, "Applications", "circuitRF.app");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents"));

        Assert.False(UpdatePolicy.PolicyFilePresent(bundle));

        File.WriteAllText(Path.Combine(_root, "Applications", UpdatePolicy.PolicyFileName), "");
        Assert.True(UpdatePolicy.PolicyFilePresent(bundle));
    }

    /// <summary>
    /// R-AU-45's closing rule, asserted as a source property: NOTHING else reads
    /// AppPreferences.AutomaticUpdates directly. One accessor, or the override precedence will be
    /// right in one place and absent in another — and the place it is absent is the one that opens a
    /// socket on a machine whose administrator forbade it.
    /// </summary>
    [Fact]
    public void OnlyUpdatePolicyAndTheSettingsControlReadThePreferenceDirectly()
    {
        string[] allowed =
        [
            Path.Combine("src", "Ui", "Updates", "UpdatePolicy.cs"),
            Path.Combine("src", "Ui", "Theming", "AppPreferences.cs"),
            Path.Combine("src", "Ui", "Views", "Dialogs", "UpdateSettingsView.axaml.cs"),
        ];

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "circuitRF.slnx"))) root = root.Parent;
        Assert.NotNull(root);

        foreach (string file in Directory.EnumerateFiles(Path.Combine(root!.FullName, "src"), "*.cs",
                                                         SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root.FullName, file);
            if (Array.Exists(allowed, a => rel.EndsWith(a, StringComparison.Ordinal))) continue;

            // UpdatePolicyState carries a property of the same name, and reading THAT is exactly
            // what this rule asks for — so the resolved form is removed before the scan.
            string code = UpdateInstallSiteTests.StripComments(File.ReadAllText(file))
                .Replace("UpdatePolicy.Current.AutomaticUpdates", "", StringComparison.Ordinal)
                .Replace("policy.AutomaticUpdates", "", StringComparison.Ordinal);

            Assert.False(code.Contains(".AutomaticUpdates", StringComparison.Ordinal),
                         $"{rel} reads AppPreferences.AutomaticUpdates directly; go through UpdatePolicy.");
        }
    }

    // ── "never checks" is literal ────────────────────────────────────────────────────────────

    /// <summary>
    /// R-AU-44 / gate 23. With automatic updates off the feed is NEVER touched — asserted with a fake
    /// that fails the test if it is called, not with a round trip of the setting. The preference is
    /// read BEFORE an HttpClient is constructed, not consulted afterwards to decide whether to act on
    /// the result.
    /// </summary>
    [Fact]
    public async Task WithAutomaticUpdatesOff_TheFeedIsNeverTouched()
    {
        AppPreferencesIo.Update(p => p.AutomaticUpdates = false);

        var service = new UpdateService(
            () => new ForbiddenUpdateFeed(),         // throws if constructed AND called
            new FakeFreeSpaceProbe(long.MaxValue),
            messages: null,
            site: () => new InstallSite(_install, InstallShape.VersionedPointer, true, _install));

        CheckResult r = await service.CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal(CheckOutcome.Disabled, r.Outcome);
        Assert.Equal(0, service.FeedsCreated);       // not merely unused — never even built
    }

    [Fact]
    public async Task UnderAnOverride_TheFeedIsNeverTouchedEither()
    {
        AppPreferencesIo.Update(p => p.AutomaticUpdates = true);
        Environment.SetEnvironmentVariable(UpdatePolicy.EnvironmentVariable, "1");

        var service = new UpdateService(
            () => new ForbiddenUpdateFeed(),
            new FakeFreeSpaceProbe(long.MaxValue),
            messages: null,
            site: () => new InstallSite(_install, InstallShape.VersionedPointer, true, _install));

        Assert.Equal(CheckOutcome.Disabled,
                     (await service.CheckAsync(manual: true, CancellationToken.None)).Outcome);
        Assert.Equal(0, service.FeedsCreated);
    }

    /// <summary>
    /// A read-only install writes NOTHING — gate item 8's "assert it directly".
    ///
    /// <para><b>It still CHECKS.</b> R-AU-1 and design §1.1 are explicit that notify-only means "they
    /// check, and post a Message Panel line with a link, but never write" — and the first version of
    /// this test asserted the opposite, with a <c>ForbiddenUpdateFeed</c> that failed if the feed was
    /// touched at all. That pinned the implementation's model rather than the requirement, and the
    /// implementation returned before the feed, so every .msi, .deb and standard-user macOS install
    /// was silently never told a new version existed (second review, 2026-08-25).</para>
    /// </summary>
    [Fact]
    public async Task AReadOnlyInstall_ChecksAndSaysSo_ButWritesNothing()
    {
        AppPreferencesIo.Update(p => p.AutomaticUpdates = true);

        string flat = Path.Combine(_root, "opt");
        Directory.CreateDirectory(flat);

        var messages = new RecordingSink();
        var service = new UpdateService(
            () => new FakeUpdateFeed([CannedReleases.Release("v99.0.0", false, false, "circuitRF-99.0.0-arm64.dmg")]),
            new FakeFreeSpaceProbe(long.MaxValue),
            messages,
            site: () => new InstallSite(flat, InstallShape.Flat, false, flat),
            trust: new ReleaseTrust(""));   // the canned release carries no signed manifest

        CheckResult r = await service.CheckAsync(manual: true, CancellationToken.None);

        Assert.Equal(CheckOutcome.NotifyOnly, r.Outcome);
        Assert.Equal("99.0.0", r.Version);

        // The user is told, once, with somewhere to go and no button to press.
        string line = Assert.Single(messages.Posted).Text;
        Assert.Contains("99.0.0", line);
        Assert.Contains(UpdateService.ReleasesPageUrl, line);
        Assert.DoesNotContain("Relaunch", line, StringComparison.OrdinalIgnoreCase);

        // ...and not one byte reached the installation or the staging tree.
        Assert.Empty(Directory.GetFileSystemEntries(flat));
        Assert.False(Directory.Exists(UpdatePaths.Staging));
    }

    /// <summary>
    /// The other half, which the old test conflated with it: a WRITABLE install whose binary carries
    /// no publisher identity is notify-only too, and the reason it gives is the signing, not the
    /// location. Telling that user their installation is in the wrong place would send them to
    /// re-install something that is exactly where it should be.
    /// </summary>
    [Fact]
    public void TheTwoNotifyOnlyReasonsAreDifferentSentences()
    {
        Assert.DoesNotContain("cannot write", UpdateService.UnsignedBuildReason);
        Assert.Contains("not signed", UpdateService.UnsignedBuildReason);
    }

    // ── LastCheckUtc is not a preference ─────────────────────────────────────────────────────

    /// <summary>
    /// R-AU-39 / gate 22. It changes on every check; putting it in preferences.json would rewrite the
    /// whole file on a 24-hour timer and race the settings dialog's own load-mutate-save.
    /// </summary>
    [Fact]
    public void RecordingACheck_DoesNotModifyPreferencesJson()
    {
        AppPreferencesIo.Update(p => p.ActiveThemeName = "Default");
        byte[] before = File.ReadAllBytes(PrefsPath);

        UpdateStateIo.Update(s => s.LastCheckUtc = DateTime.UtcNow);

        Assert.Equal(before, File.ReadAllBytes(PrefsPath));
        Assert.True(File.Exists(UpdatePaths.StateFile));
        Assert.DoesNotContain("last_check", File.ReadAllText(PrefsPath));
    }

    [Fact]
    public void TheStateFileLivesUnderTheRedirectableUpdatesDirectory()
    {
        // Staging is isolated from a developer's real installation BY CONSTRUCTION, rather than by
        // remembering to disable something.
        Assert.StartsWith(_root, UpdatePaths.Root, StringComparison.Ordinal);
        Assert.StartsWith(UpdatePaths.Root, UpdatePaths.Staging, StringComparison.Ordinal);
    }

    // ── changing a setting has side effects ──────────────────────────────────────────────────

    private void StageFake(string version, bool prerelease)
    {
        string dir = Path.Combine(_install, UpdateInstallSite.VersionDirPrefix + version);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(UpdatePaths.Staging);
        File.WriteAllText(Path.Combine(UpdatePaths.Staging, "leftover.bin"), "x");

        UpdateStateIo.Update(s =>
        {
            s.StagedVersion      = version;
            s.StagedPath         = UpdateInstallSite.VersionDirPrefix + version;
            s.StagedIsPreRelease = prerelease;
        });
    }

    /// <summary>
    /// R-AU-46 / gate 25. A user who unchecks the box and is then moved to a new version on the next
    /// relaunch has been lied to by the checkbox — which is the whole reason this exists separately
    /// from writing the preference.
    /// </summary>
    [Fact]
    public void TurningAutomaticUpdatesOff_DiscardsTheStagedUpdate()
    {
        StageFake("2.0.0", prerelease: false);

        UpdatePreferenceChange.Apply(false, false, Site);

        Assert.Null(UpdateStateIo.Load().StagedVersion);
        Assert.False(Directory.Exists(Path.Combine(_install, "app-2.0.0")));
        Assert.False(Directory.Exists(UpdatePaths.Staging));
    }

    [Fact]
    public void TurningBetasOff_DiscardsAStagedPrerelease()
    {
        StageFake("2.0.0-beta.1", prerelease: true);

        UpdatePreferenceChange.Apply(true, false, Site);

        Assert.Null(UpdateStateIo.Load().StagedVersion);
        Assert.False(Directory.Exists(Path.Combine(_install, "app-2.0.0-beta.1")));
    }

    [Fact]
    public void TurningBetasOff_LeavesAStagedSTABLEVersionAlone()
    {
        StageFake("2.0.0", prerelease: false);

        UpdatePreferenceChange.Apply(true, false, Site);

        Assert.Equal("2.0.0", UpdateStateIo.Load().StagedVersion);
        Assert.True(Directory.Exists(Path.Combine(_install, "app-2.0.0")));
    }

    // ── the retained previous version ────────────────────────────────────────────────────────

    /// <summary>
    /// R-AU-33 / gate 16's second half: exactly one previous version is kept, and it is deleted
    /// ONLY once the new one has launched successfully — which is what
    /// <see cref="UpdateStartup.NoteFirstWindowShown"/> is called to establish. Steady-state disk
    /// footprint is zero: one previous version, never a history.
    /// </summary>
    [Fact]
    public void ThePreviousVersionSurvivesUntilTheFirstWindowActuallyAppears()
    {
        string previous = Path.Combine(UpdatePaths.Root, "previous");
        Directory.CreateDirectory(previous);
        File.WriteAllText(Path.Combine(previous, "circuitRF"), "the version that worked");

        UpdateStateIo.Update(s =>
        {
            s.PendingVersion  = "2.0.0";
            s.PendingPath     = "app-2.0.0";
            s.PreviousVersion = "1.0.0";
            s.LaunchAttempts  = 1;          // started, but no window yet
        });

        // Nothing has confirmed the launch, so the reclaim refuses to touch it.
        new UpdateReclaimer(UpdatePaths.Root).ReclaimUntil(() => false, previousVersionReleasable: false);
        Assert.True(Directory.Exists(previous));

        // The window appears: the counter clears and the one retained generation is given back. This
        // test runs outside a versioned layout, so the launch belongs to the pending version by the
        // bundle rule and the confirmation is unambiguous.
        UpdateStartup.NoteFirstWindowShown();

        Assert.False(Directory.Exists(previous));
        UpdateState after = UpdateStateIo.Load();
        Assert.Null(after.LaunchAttempts);
        Assert.Null(after.PendingVersion);
    }

    /// <summary>
    /// A rollback notice is PERSISTED, not held in memory — because the only thing that ever writes
    /// one is a version crashing before it has a Message Panel to write to. An in-memory notice from
    /// a build that cannot start is a notice nobody ever reads.
    /// </summary>
    [Fact]
    public void ARollbackNotice_SurvivesToTheNextWindowAndIsPostedOnlyOnce()
    {
        UpdateStateIo.Update(s => s.PendingNotice = "circuitRF could not start after updating.");

        Assert.Equal("circuitRF could not start after updating.", UpdateStartup.NoteFirstWindowShown());
        Assert.Null(UpdateStateIo.Load().PendingNotice);
        Assert.Null(UpdateStartup.NoteFirstWindowShown());
    }

    /// <summary>
    /// R-AU-33's own bookkeeping: the two append-only lists are caches of a decision, not a record,
    /// and they live in a file read on every launch. A version old enough to fall off either is a
    /// version no live release list still offers.
    /// </summary>
    [Fact]
    public void TheBlacklistAndAnnouncedListsAreBounded()
    {
        var state = new UpdateState();
        for (int i = 0; i < UpdateState.HistoryCap * 3; i++)
        {
            state.Blacklist_Add($"9.{i}.0");
            state.Announced_Add($"9.{i}.0");
        }

        Assert.Equal(UpdateState.HistoryCap, state.Blacklist!.Count);
        Assert.Equal(UpdateState.HistoryCap, state.Announced!.Count);

        // It is the OLDEST that fall off, so the most recent refusals still hold.
        Assert.Contains($"9.{UpdateState.HistoryCap * 3 - 1}.0", state.Blacklist);
        Assert.DoesNotContain("9.0.0", state.Blacklist);
    }

    [Fact]
    public void ANormalLaunchWithNothingOutstanding_ChangesNothing()
    {
        UpdateStateIo.Update(s => s.LastCheckUtc = new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc));
        byte[] before = File.ReadAllBytes(UpdatePaths.StateFile);

        UpdateStartup.NoteFirstWindowShown();

        Assert.Equal(before, File.ReadAllBytes(UpdatePaths.StateFile));
    }

    [Fact]
    public void WithNothingStaged_ChangingASettingDoesNothingDestructive()
    {
        Directory.CreateDirectory(UpdatePaths.Staging);
        File.WriteAllText(Path.Combine(UpdatePaths.Staging, "in-flight.partial"), "x");

        UpdatePreferenceChange.Apply(true, true, Site);

        Assert.True(Directory.Exists(UpdatePaths.Staging));
    }
}


