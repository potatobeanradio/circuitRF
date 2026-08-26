# Sonnet Brief — Automatic Updates (AU)

**Design:** `docs/design/auto-update.md` — read all of it before starting. This brief implements that
document and does not restate its reasoning; where the two disagree, the design note is right and this
brief is stale.

**Also reads with:** `BUILDING.md` (what each packaging script does today), `packaging/version.sh` + the
repo-root `VERSION` file (the single source of the version string), root `CLAUDE.md`
§"Build / test / run" and §"Commercial Vendor References".

**Milestone order is not negotiable.** M0 is a spike whose result can invalidate M4; M1 is pure code that
everything else consumes; M2 is the packaging change without which M4 cannot be tested at all on two of
the three platforms. Do not start in the middle.

**Test loop** (root `CLAUDE.md` §"Layout/UI work" — the same two projects apply here; this SDK rejects
more than one project path per invocation):
```
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

---

## 1. What is being built, and the one rule underneath it

circuitRF, harmonicaRF and wBond check GitHub for a newer release, download it in the background, and
swap it in at the next launch. The user sees exactly one Message Panel line and never a dialog, an
installer, an elevation prompt or a Gatekeeper warning.

**The rule everything follows from (design §1): a silent update requires an install location the running
user can write.** Two of the three current installers put the application somewhere they cannot
(`%ProgramFiles%`, `/opt`), which is why M2 exists and why it is packaging work rather than updater work.

**R-AU-1. One runtime check, not three platform branches.** `UpdateInstallSite.Detect()` answers "can this
process write its own install tree, laid out the way the updater expects?" That single predicate covers
macOS-as-standard-user, the per-machine MSI and the `.deb` identically. Read-only ⇒ **notify-only**: check,
post one Message Panel line with a link, write nothing. If this grows a `switch` on
`RuntimeInformation.OSPlatform` to decide *policy*, it has gone wrong — platform branches belong only in
the primitives that move bytes (§5, §6).

**R-AU-2. The CLI never updates itself, and neither does anything below `src/Ui`.** The whole subsystem
lives in `src/Ui/Updates/`. `src/Cli` is a headless driver that runs in build pipelines; a binary that
silently replaces itself mid-CI is a defect. The UI firewall is unchanged and `tests/Firewall.Tests` must
stay green.

**R-AU-3. No version string is introduced anywhere.** The running version is `AppVersion.Display`, which
reads `InformationalVersion`, which `Directory.Build.props` reads from `VERSION`. Root `CLAUDE.md`'s
single-source rule and `tests/Ui.Tests/VersionSingleSourceTests.cs` both stand. If you find yourself
writing a version literal, stop.

---

## 2. Milestones

| | What | Gates |
|---|---|---|
| **M0** | Spike: prove the macOS premise on a real signed build | §3 |
| **M1** | Core — versions, feed, asset selection, install site, space policy. Pure, no network | §4 |
| **M2** | Per-user install channels on Windows and Linux (packaging) | §5 |
| **M3** | Fetch, verify, stage | §6 |
| **M4** | Swap, rollback, self-heal | §7 |
| **M5** | UI — settings, Message Panel, Help item | §8 |
| **M6** | Signing prerequisite, release process, manual acceptance matrix | §9 |

M1 and M2 are independent of each other and both independent of M0's *result*; M3 and M4 are not — do not
build them before M0 reports.

---

## 3. M0 — the spike, before anything that depends on it

The macOS design rests on two claims that this repository cannot test and that the design note asserts
from documentation rather than measurement. **Both are load-bearing, and if either is false the macOS half
of M3/M4 is designed wrong.** Measure them first, on a real Developer ID signed and notarized build, on a
Mac that is not the build machine.

**R-AU-4. Prove, and write down what was observed:**

1. A `.dmg` fetched by `HttpClient` into a file carries **no** `com.apple.quarantine` attribute —
   `xattr -p com.apple.quarantine <file>` finds nothing. (Design §4.1.)
2. A bundle extracted with `ditto -x -k` from that image passes `codesign --verify --deep --strict` and
   `spctl -a -vv -t exec`.
3. The running application can replace `/Applications/circuitRF.app` **with no App Management prompt**,
   because the replacing process shares the target's Team ID. (Design §4.2 — this is the claim most worth
   distrusting.)
4. The replaced application launches with **no Gatekeeper dialog of any kind**.
5. For contrast, and to pin the requirement in M6: repeat (3) with an **ad-hoc** signed build and record
   what the user sees. The expectation is a TCC prompt; confirm it, because it is the reason signing
   becomes mandatory rather than advisable.

**R-AU-5. If (3) or (4) fails, stop and report before building M3 or M4.** Do not work around it by
launching a helper, shelling out to `open`, or asking the user to approve anything — those are the
outcomes this feature exists to avoid, and a workaround that produces one prompt instead of another has
not solved the problem. The fallback is notify-only on macOS, which is a different and much smaller phase.

---

## 4. M1 — the core

All of M1 is pure or temp-directory-bound. **No network, no real install, no platform APIs.** Every
requirement here is covered by ordinary unit tests in `tests/Ui.Tests`.

### 4.1 Versions

**R-AU-6. `SemanticVersion` implements SemVer 2.0 precedence.** Not string comparison, and **not
`System.Version`, which cannot parse `1.0.0-beta.1` at all.** Pin the ordering with a table-driven test:

```
0.9.0  <  1.0.0-beta.1  <  1.0.0-beta.2  <  1.0.0-beta.10  <  1.0.0-rc.1  <  1.0.0
```

`beta.2 < beta.10` is the case a naive implementation gets wrong: dot-separated numeric identifiers
compare **numerically**, not lexically. This is the second appearance of a trap `packaging/version.sh`
already documents for dpkg's `~`; it gets one implementation, not two.

**R-AU-7. Never offer a version that is not strictly greater than the running one.** No equal, no lower,
no "reinstall". A user on `1.0.0-beta.3` whose channel's newest stable is `0.9.0` is offered nothing, and
that is correct — it is what stops the beta channel from silently downgrading people.

### 4.2 The feed

**R-AU-8. `IUpdateFeed` is an interface with one shipping implementation, `GitHubReleasesFeed`.** Tests
use canned JSON. **No test in this repository may make a network call** — not a "just this one", not a
`[Trait]`-gated one.

**R-AU-9. Use `GET /repos/potatobeanradio/circuitRF/releases` and filter. Never `/releases/latest`,**
which excludes prereleases and drafts and would make the beta channel silently empty. Drafts are always
excluded; prereleases are the channel switch (R-AU-11).

**R-AU-10. Asset selection is by name, per application, platform and architecture:**

```
circuitRF-<version>-<arch>.dmg            arch in {arm64, x64}
circuitRF-<version>-win-<arch>.zip        arch in {x64, arm64, x86}
circuitRF-<version>-linux-<arch>.tar.gz   arch in {x64, arm64}
```

and the same with `harmonicaRF-` and `wBond-`. Three applications, each updating itself, each matching
its own name — a shared updater must not offer circuitRF's payload to wBond.

Architecture comes from `RuntimeInformation.ProcessArchitecture`, and **an x64 build running under Rosetta
on Apple Silicon stays on x64.** It correctly reports X64 and must be left there; silently migrating a
user across architectures is not an update.

**R-AU-11. Channels are the GitHub prerelease flag and nothing else.** `IncludeBetas` off (default) ⇒
only `prerelease == false`. On ⇒ every non-draft release. No second list, no naming convention, no
maintained channel file.

**R-AU-12. Implement the optional-manifest hook now, even though we publish no manifest.** If the selected
release contains an asset named exactly `update-manifest.json`, fetch it and **prefer it over name
matching**. Honour `assets[]` (name, url, sha256), `minimumUpgradableFrom`, and `feedUrl`. Absent, fall
back to R-AU-10 silently — that is the normal case today.

This is ~20 lines and it is the entire migration path off GitHub (design §15.4). It is only ever cheap
before it is needed, and a shipped client that does not understand it can never be told to look elsewhere.

**R-AU-13. `feedUrl` is not a blind redirect.** Honour it only for hostnames on an allow-list compiled
into the binary. A field that lets a release point the updater at an arbitrary host is a field that lets a
compromised release point it at an arbitrary host.

### 4.3 Install site

**R-AU-14. `UpdateInstallSite.Detect()` returns the layout root, whether it is writable, and which
platform shape it is** — probed, by attempting a write, not inferred from the path. `/Applications` is
writable for an admin user and not for a standard one, and no amount of path inspection reveals which.

Tested against temp-directory fixtures for every layout in design §2, writable and read-only. No real
installation is involved.

### 4.4 Free space (design §13)

**R-AU-15. `RequiredFreeSpace(downloadBytes, expandedBytes, reserve)` = `download + expanded + reserve`,
with `reserve` = 1 GB.** The naive `download`-only check is wrong by roughly 3× because the compressed
payload and its expanded copy exist simultaneously. Table-test it against design §13.1's measured figures
so that a future change which stops deleting the download, or starts retaining two previous versions,
fails a test rather than someone's disk.

**R-AU-16. `IFreeSpaceProbe` reports the raw, pessimistic figure** — `DriveInfo.AvailableFreeSpace`. **Do
not use macOS's `volumeAvailableCapacityForImportantUsageKey`.** It reports purgeable space as available
and can differ from the raw figure by many gigabytes on APFS. This looks like a bug from the outside and
someone will eventually try to "fix" it; design §13.4 records why it is not one, and the reasoning is that
over-caution costs a skipped update while over-optimism costs a user their unsaved work.

**R-AU-17. The reclaim order is fixed, and it never leaves our own directories:** `staging/`, then
`.partial` trees, then abandoned or blacklisted staged versions, then the retained previous version **but
only once the running version has cleared its startup counter** (§7). The updater has no opinions about
the user's disk — it does not clear caches, touch workspaces, or find space anywhere it did not itself
consume. Assert that last property directly.

---

## 5. M2 — per-user install channels (packaging)

Without this, Windows and Linux cannot update at all. It is packaging work and it changes what users
download.

**R-AU-18. Windows: `%LOCALAPPDATA%\Programs\circuitRF\` with versioned directories behind a stub.**

```
circuitRF.exe          <- stub launcher; NEVER changes; shortcuts and file associations point here
current                <- one line of text naming the directory to run
app-<version>\         <- a full publish tree
staging\
```

The stub is a small native launcher that reads `current` and `CreateProcess`es
`app-<ver>\circuitRF.exe`. **`tools/senior-worker` already builds a Windows launcher stub for the same
class of reason** — follow it rather than inventing a second pattern, and do not make the stub a .NET
program (it would need a runtime that lives in the directory it is choosing between).

First install is a per-user MSI: WiX v4 `Scope="perUser"` with `StandardDirectory Id="LocalAppDataFolder"`.
`packaging/windows/circuitRF.wxs` is `Scope="perMachine"` today; the per-user variant is a parameter, not
a second file.

*Considered and rejected: silently re-running a per-user `.msi` with `msiexec /qn`, as VS Code does with
its Inno installer.* It is less new machinery, but it abandons the rename-only swap that design §13's
disk-space safety rests on, and a half-applied MSI on a full disk is a much larger thing to reason about
than a pointer file that was never truncated. Recorded here so it is a decision rather than an oversight;
if the owner prefers it, design §13.2 must be re-argued first.

**R-AU-19. Linux: `~/.local/share/circuitRF/` with the same shape**, `current` as a symlink, plus
`~/.local/bin/circuitrf` and `~/.local/share/applications/circuitrf.desktop` pointing at the stable
`current/` path so an update re-registers nothing. Shipped as `.tar.gz` + `install.sh`.

**R-AU-20. The `.msi` and `.deb` survive unchanged and become notify-only.** They are the managed and
machine-wide story and nothing about them regresses.

**R-AU-21. Add the naming-convention test — this is the one that prevents silent, permanent failure.**
`build-dmg.sh`, `build-msi.ps1` and `build-deb.sh` construct the names R-AU-10 parses. Rename an artifact
and updates stop with **no error anywhere and no user report**, because a user who is not being offered an
update has nothing to notice. `tests/Ui.Tests/PackagingScriptTests.cs` is the existing home for exactly
this class of guard (its two rules exist for the same reason); add the assertion there.

**R-AU-22. Update `README.md` and `BUILDING.md` together.** The download table in `README.md` gains the
per-user channel and states which installs auto-update. Root `CLAUDE.md` already requires the two files to
stay in step.

---

## 6. M3 — fetch, verify, stage

**R-AU-23. Order of operations is: reclaim → check space → download → verify → unpack → rename into
place.** Everything expensive happens where abandoning costs nothing. By the time anything the running
install depends on is touched, only renames remain (§7).

**R-AU-24. Downloads are resumable** (HTTP range requests) and re-check free space every ~16 MB. A 160 MB
transfer that restarts from zero on a dropped connection is not acceptable at this payload size, and a
space check made only at the start is made at the one moment it is least likely to still be true.

**R-AU-25. Verify in this order: hash (when published), then code signature, then publisher identity.**
macOS: `codesign --verify --strict` **and** the staged bundle's Team ID equals the running application's.
Windows: Authenticode validity **and** publisher match. Step three is the actual security boundary — the
first two prove the bytes are what GitHub served, only the third proves we produced them.

Any failure: delete the staging directory, record the version in a local blacklist so it is not retried in
a loop, and say nothing.

**R-AU-26. macOS: `ditto`, never `System.IO.Compression.ZipFile`.** `ZipFile` drops Unix mode bits and
symlinks; a bundle missing its executable bit and its `Frameworks` links has a **broken code signature**
and is refused at launch — the exact failure this feature exists to prevent, arriving by the least obvious
route, and only on a real signed build so no unit test catches it. The same reasoning forbids a recursive
`File.Copy` for the bundle: move directories, never walk and copy them.

The macOS payload is the existing `.dmg` — `hdiutil attach -nobrowse`, `ditto` the `.app` out,
`hdiutil detach`. **No new macOS release asset.** Detach in a `finally`; a leaked mount is a leaked disk.

**R-AU-27. Nothing incomplete ever holds a real name.** Unpack to `app-<ver>.partial\` (or a `.partial`
bundle) and rename to the real name only when complete and verified. Nothing executes from, or counts as,
a `.partial` path. This is the whole mechanism that makes an interrupted update harmless.

**R-AU-28. Do not add a shell-out to `curl`, `open`, `Invoke-WebRequest` or a browser anywhere in the
download path.** The absence of a quarantine attribute (macOS) and of Mark-of-the-Web (Windows) is what
suppresses the Gatekeeper and SmartScreen prompts, and it holds precisely because `HttpClient` writes the
file itself. A helpful-looking refactor to a shell downloader would reintroduce both prompts, silently,
and only on a real user's machine.

---

## 7. M4 — swap, rollback, self-heal

**R-AU-29. The swap happens at the next launch, in `Program.Main`, before Avalonia initialises** — not at
quit. No detached helper, no race against a force-quit, and the app tree is provably not in use.
Windows/Linux: rewrite the pointer, before the stub has started anything. macOS: atomic bundle swap, then
`execv` the new executable.

**R-AU-30. Never swap mid-session.** A self-contained .NET app does not load every assembly eagerly and
Avalonia resolves some resources lazily. The staged version sits inert until the next launch, which is
also what makes the Message Panel wording honest.

**R-AU-31. `current` is never written in place.** Write `current.tmp`, then rename over the original
(`MoveFileEx` with `MOVEFILE_REPLACE_EXISTING` on Windows; `rename(2)` elsewhere; the symlink form is
`symlink()` to a temp name then `rename()`). **A truncating write that fails with ENOSPC leaves `current`
empty, the stub with nothing to run, and the user with an application that will not start** — a full disk
turned into an uninstallation. This is the single most destructive failure in the design and it costs
nothing to make impossible.

**R-AU-32. macOS uses an atomic directory exchange** — `renamex_np(..., RENAME_SWAP)` via P/Invoke, or
`NSFileManager.replaceItemAt`. `File.Move` will not atomically swap two directories. The two-rename
fallback is acceptable but is a fallback.

**R-AU-33. Keep exactly one previous version, and delete it once the new one has launched successfully
once.** A launch counter is written before the first window appears and cleared once it does. Steady-state
disk footprint is zero: one previous version, never a history.

**R-AU-34. Revert automatically after N = 2 failed startups**, and post one Message Panel line explaining
what happened. The crash reporting from `8532bd3` is where the detail goes. This is the only insurance a
user has against a bad release replacing a working application, and it is cheap.

**R-AU-35. Reclaim debris at every launch, before anything else** — `staging/` and any `.partial` trees,
unconditionally, because R-AU-27 guarantees nothing incomplete was ever given a real name. This is what
makes a disk-full event self-limiting rather than cumulative.

**R-AU-36. Test the atomicity properties without a full disk.** Abort partway through an unpack and assert
the installation still launches and `current` is untouched; abort partway through a `current.tmp` write
and assert the same. These are the tests that actually protect against R-AU-31, and they are cheap. Real
ENOSPC gets a 600 MB scratch filesystem and is a **manual or opt-in** step, not part of the default
`dotnet test` gate — mounting filesystems on CI is not worth what it costs.

---

## 8. M5 — the UI and the settings wiring

### 8.1 The two preferences

**R-AU-37. Two nullable properties on `AppPreferences` (`src/Ui/Theming/AppPreferences.cs`), following
that file's existing shape exactly** — `[JsonPropertyName]`, `[JsonIgnore(WhenWritingNull)]`, and a
comment saying what null means and *why* the default is what it is:

```csharp
[JsonPropertyName("automatic_updates")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public bool? AutomaticUpdates { get; set; }          // null ⇒ ON

[JsonPropertyName("include_beta_updates")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public bool? IncludeBetaUpdates { get; set; }        // null ⇒ OFF
```

**The two defaults differ, and the nullable idiom is what delivers them**: a machine with no
`preferences.json` at all — the fresh-install case — reads `AutomaticUpdates ?? true` and
`IncludeBetaUpdates ?? false`, which is the owner's specification without a single line of first-run
seeding. Never write a default value into the file; absence *is* the default.

**No `AppPreferencesIo.Migrate` entry.** These are new keys with no retired spelling. `Migrate` exists for
renames (`launch_pane` → `window_layout`) and adding a no-op line to it would misrepresent that.

**R-AU-38. One `preferences.json` serves all three applications**, because `AppDataRoot.Dir` is a single
`LocalApplicationData/circuitRF` directory. So the toggle set in circuitRF governs harmonicaRF and wBond
as well. That is the intended behaviour — "should this machine update itself" is a property of the user,
not of which binary they happened to open — but it is a consequence worth knowing before R-AU-42 makes
sense, and it must be stated in the settings help text so the scope of the checkbox is not a surprise.

**R-AU-39. `LastCheckUtc` is NOT a preference and does not go in `AppPreferences`.** It is updater state:
it changes on every check, and putting it there would rewrite the entire `preferences.json` on a 24-hour
timer and race the settings dialog's own load→mutate→save. It lives in the updater's small state file
under `AppDataRoot.SubDir("updates")`, alongside the staged-version record and the failed-version
blacklist. The settings dialog *reads* it to render the greyed line and never writes it.

### 8.2 The dialog

**R-AU-40. Follow `SettingsView.axaml`'s existing General-tab section idiom, and do not invent a second
preferences mechanism.** An "Updates" section header (`FontSize="11" FontWeight="SemiBold"
Opacity="0.55"`) over a `Grid`, matching the "Design Rules" and "Messages" sections directly above it.
The code-behind pattern is already established in `SettingsView.axaml.cs` and is three parts:

1. **Populate inside the guard.** In `LoadGeneralPrefs()`, within the existing
   `_updatingGeneral = true` / `finally` block:
   ```csharp
   AutoUpdateCheck.IsChecked    = prefs.AutomaticUpdates   ?? true;
   IncludeBetasCheck.IsChecked  = prefs.IncludeBetaUpdates ?? false;
   ```
   The guard is not optional. Setting `IsChecked` raises `IsCheckedChanged`, and without it the dialog
   writes the preference it just read on every open.
2. **Persist through `AppPreferencesIo.Update`**, never `Load`-then-`Save`, so a partial write cannot
   clobber the other fields:
   ```csharp
   private void OnAutoUpdateChanged(object? sender, RoutedEventArgs e)
   {
       if (_updatingGeneral) return;
       AppPreferencesIo.Update(p => p.AutomaticUpdates = AutoUpdateCheck.IsChecked);
       ApplyUpdatePreferenceChange();      // R-AU-41, R-AU-46
   }
   ```
3. **A greyed *Last checked: …* line**, read from R-AU-39's state file, showing *never* when absent.

**R-AU-41. The beta checkbox is a sub-item and is disabled while automatic updates are off** — set in
**both** places, or it desynchronises: inside `LoadGeneralPrefs()` (within the guard) and in the parent's
changed handler. `IncludeBetasCheck.IsEnabled = AutoUpdateCheck.IsChecked == true`. A disabled child whose
state still reads "on" is the kind of detail that survives review and confuses users.

**R-AU-42. harmonicaRF needs the same two controls added to its own dialog.** `SettingsView` is shared by
circuitRF **and** wBond — `WBondShellWindow.axaml.cs:394` opens the very same window — so both are covered
by R-AU-40 for free. harmonicaRF is not: `HarmonicaSettingsDialog.axaml` is a separate window with its own
Appearance and Advanced tabs and does not use `SettingsView` at all. Without this, a user who has only
harmonicaRF installed can never turn automatic updates off, which contradicts R-AU-44. Both dialogs read
and write the same two `AppPreferences` properties; neither owns them.

**R-AU-43. The check is scheduled from each application's own startup path** — `App.axaml.cs`,
`HarmonicaApp.axaml.cs`, `WBondApp.axaml.cs` — on a background thread, at least 60 seconds after the main
window opens, so it never competes with launch and never appears in a cold-start measurement. One shared
scheduler in `src/Ui/Updates/`, three call sites, no copy.

### 8.3 The reader side, and what takes precedence

**R-AU-44. "Never checks" is literal: the preference is read BEFORE an `HttpClient` is constructed**, not
consulted afterwards to decide whether to act on the result. With automatic updates off, circuitRF opens
no socket for any reason. **Gate this with a test that the feed is never touched** — a fake `IUpdateFeed`
that fails the test if called — not with a test that the setting round-trips.

**R-AU-45. One accessor, `UpdatePolicy.Current`, resolves the effective setting, in this precedence
order:**

| | Source | Wins over |
|---|---|---|
| 1 | the `no-auto-update` policy file beside the install | everything |
| 2 | `CRF_NO_UPDATE_CHECK=1` in the environment | the preference |
| 3 | `AppPreferences.AutomaticUpdates` | the default |
| 4 | default (on) | — |

The policy file overriding the preference is the point of it (design §11): an administrator deploys an
installation the user cannot re-enable. **When either override is in force the checkbox must render
disabled, with the reason** — a checkbox the user can tick that changes nothing is worse than one they
cannot.

Nothing else may read `AppPreferences.AutomaticUpdates` directly. One accessor, or the override precedence
will be right in one place and absent in another.

### 8.4 Changing a setting has side effects

**R-AU-46. Turning either setting off discards the matching staged update**, via
`ApplyUpdatePreferenceChange()`: automatic updates off ⇒ delete the staging directory and cancel any
pending swap; betas off while a staged version is a prerelease ⇒ discard that one specifically, and leave
a staged *stable* version alone. A user who unchecks the box and is then moved to a new version on the
next relaunch has been lied to by the checkbox — which is the whole reason this requirement exists
separately from R-AU-37.

The handler must therefore do more than write a preference. A settings change that only mutates JSON is
incomplete.

### 8.5 What the user sees

**R-AU-47. One Message Panel line, Info level, per staged version**, posted when staging completes and
named after the *running* application:

> circuitRF updated from 1.0.0-beta.1 to 1.0.0-beta.2 in the background. Relaunch circuitRF to start using
> the version. Automatic updates can be disabled in Settings.

If several versions stage before the user relaunches, each posts its own line, worded from the
**installed** version to the **newly staged** one, so the last line on screen is always the true end state.

**R-AU-48. There is no "Relaunch circuitRF" button, anywhere, in any form.** Owner instruction. The app
can be holding unsaved workspaces; a one-click relaunch invites data loss to save a keystroke. Do not add
one to the message, the settings page, a toast, the title bar or a menu.

**R-AU-49. Help ▸ Check for Updates… reports through the Message Panel, never a dialog.** It ignores the
24-hour throttle. Disabled when automatic updates are off (a manual check is still a network call) and
when the install site is read-only, where the notify-only path serves instead.

**R-AU-50. Background failure is silent** — no Message Panel entry, no dialog, no toast, for an
unreachable network, a timeout, a rate limit or a verification failure. An offline machine is the normal
state for a large fraction of these users. Two deliberate exceptions, both from design §13.5, both about
disk space and nothing else: the manual check reports it with figures, and **at most one Message Panel
line per 30 days** when insufficient space is the sole reason updates are not happening.

---

## 9. M6 — signing, release process, manual acceptance

**R-AU-51. Developer ID signing and notarization become a release prerequisite on macOS**, because an
ad-hoc build has no Team ID and therefore cannot satisfy design §4.2's App Management exemption — it fails
as a TCC prompt, not an error. `packaging/macos/build-dmg.sh` already does the work; M6 makes it required
rather than optional for a release, and M0(5) is the evidence.

Staple as well: `xcrun stapler staple` on the `.app`, **then** zip. An archive cannot be stapled.

**R-AU-52. Add the manual acceptance matrix to `BUILDING.md`**, to be run at every release that changes
signing, packaging layout or the updater. No test can cover these:

| | Check |
|---|---|
| macOS | An updated build launches with **no** Gatekeeper dialog and **no** App Management prompt |
| Windows | A per-user install updates with **no** UAC prompt and **no** SmartScreen warning |
| Linux | A `~/.local` install updates and the `.desktop` entry still launches afterwards |
| All | A read-only install (`.msi` / `.deb` / standard-user macOS) is notify-only and writes nothing |

---

## 10. Scope guardrails

- **`src/Ui/Updates/` only.** Don't touch `src/Core`, `src/Engine`, `RfCore`. `tests/Firewall.Tests` stays
  green and no new assembly reference crosses the boundary.
- **No network in any test, ever.** `IUpdateFeed` and `IFreeSpaceProbe` are faked.
- **No new timing or wall-clock assertions.** Standing owner instruction: assert counters and structural
  properties. Nothing in this phase should be measuring the machine.
- **Staging lives under `AppDataRoot.SubDir("updates")`**, which is already redirectable — so `tools/DocGen`
  and the test suite are isolated by construction rather than by remembering to disable something.
- No delta updates, no AppImage, no signed manifest, no multi-host mirroring, no cross-application shared
  payload, no "what's new" screen. All are recorded in design §17 and all are out of scope here.
- **No vendor or PDK names** anywhere in code, comments, test data or documentation (root `CLAUDE.md`
  §"Commercial Vendor References"). The hosting discussion in design §15.3 names infrastructure providers
  in prose only; do not carry any of them into an identifier, a constant or a fixture.

---

## 11. Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); documented gate green; no existing test regresses;
   `tests/Firewall.Tests` green.
2. **M0 reported (R-AU-4/5)** — the five observations written down as measurements, including the ad-hoc
   contrast case. If (3) or (4) failed, the phase stopped here and said so.
3. **SemVer precedence (R-AU-6)** — the ordering table passes, `beta.2 < beta.10` included.
4. **No downgrade (R-AU-7)** — a running beta newer than the newest stable is offered nothing.
5. **Channels (R-AU-11)** — betas off sees only non-prereleases; betas on sees all; drafts never appear.
6. **Asset selection (R-AU-10)** — every artifact the three packaging scripts produce maps to exactly one
   application × platform × architecture, and wBond is never offered circuitRF's payload.
7. **Manifest hook (R-AU-12/13)** — a canned release carrying `update-manifest.json` uses it and ignores
   name matching; the same release without it falls back silently; a `feedUrl` off the allow-list is
   refused.
8. **Install site (R-AU-14)** — writable and read-only fixtures for all four layouts; read-only is
   notify-only and writes nothing. Assert the "writes nothing" directly.
9. **Space arithmetic (R-AU-15)** — required space equals `download + expanded + 1 GB` against design
   §13.1's measured figures. A test that only knows the download size fails.
10. **Pessimistic probe (R-AU-16)** — the raw figure is what the policy consumes.
11. **Reclaim (R-AU-17)** — the fixed order holds, and **nothing outside our own directories is ever
    deleted**, asserted directly.
12. **Naming convention (R-AU-21)** — the packaging scripts' constructed names match the updater's
    patterns. Change a name in a script and this test goes red.
13. **`.partial` discipline (R-AU-27)** — an incomplete tree is never executed from and never counted as
    staged.
14. **The bricking case (R-AU-31/36)** — an aborted `current` write leaves the previous `current` intact
    and the installation launching. This is the gate item that matters most.
15. **Atomic swap (R-AU-32)** — the bundle exchange is atomic, or the fallback is documented as taken.
16. **Rollback (R-AU-33/34)** — two failed startups revert to the previous version and post one line; the
    previous version is deleted after one successful launch and only then.
17. **Self-heal (R-AU-35)** — debris from a killed update is reclaimed at the next launch.
18. **Fresh-install defaults (R-AU-37)** — with **no** `preferences.json` present, automatic updates
    resolve ON and betas OFF, and opening and closing Settings without touching anything writes
    **neither** key. The absence-is-the-default property is the test; a seeded file would pass a weaker
    one.
19. **Guarded populate (R-AU-40)** — opening the Settings dialog does not write the preference it just
    read. Assert the file is byte-unchanged across an open/close.
20. **Sub-checkbox enablement (R-AU-41)** — the beta checkbox is disabled whenever automatic updates are
    off, on **load** as well as on change.
21. **Both dialogs (R-AU-42)** — the two controls are reachable from `SettingsView` *and* from
    `HarmonicaSettingsDialog`, and both read and write the same `AppPreferences` properties.
22. **`LastCheckUtc` is not a preference (R-AU-39)** — a check does not modify `preferences.json`.
23. **Never checks (R-AU-44)** — with automatic updates off, the feed is never touched. A fake
    `IUpdateFeed` fails the test if called; a round-trip test does not satisfy this item.
24. **Override precedence (R-AU-45)** — the policy file beats the environment variable beats the
    preference, and under either override the checkbox renders **disabled with a reason** rather than
    tickable-but-inert.
25. **Discard on disable (R-AU-46)** — unchecking automatic updates removes the staged update; unchecking
    betas removes a staged *prerelease* and leaves a staged stable version alone.
26. **No relaunch button (R-AU-48)** — a source scan finds no such control. Strip comments before
    scanning; the H8 lesson applies.
27. **Silence (R-AU-50)** — an unreachable feed, a timeout, a 403 and a verification failure each produce
    no user-visible output; the two disk-space exceptions produce exactly what §13.5 specifies.
28. **Manual matrix (R-AU-52)** — run once per platform, recorded in the completion note.

---

## 12. On completion

Write an **"Automatic Updates — COMPLETE"** entry at the top of `src/Ui/RESOLVED.md` — **not**
`CLAUDE.md`, and not the design note. Call out:

1. **M0's five observations as measurements**, especially whether the App Management exemption behaved as
   design §4.2 claims, and what the ad-hoc build actually showed. If either differed from the design note,
   say so plainly and mark the note stale.
2. **The measured peak disk requirement per platform**, checked against design §13.1's table — the figures
   there are from `dist/` on 2026-08-23 and will have moved.
3. **Whether the Windows stub was needed as designed**, or whether something simpler survived contact.
   Record the reason either way, since R-AU-18 already rejects one alternative on paper.
4. **What the manual acceptance matrix cost** in wall-clock, per platform, since it recurs at every
   release that touches signing or packaging.
5. **Any place a platform branch leaked into policy** rather than staying in the byte-moving primitives
   (R-AU-1), and whether it was removed or is now a known exception.
6. Whether anything learned here changes the migration story in design §15 — particularly whether the
   manifest hook (R-AU-12) is carrying its weight or should have been the primary path from the start.
