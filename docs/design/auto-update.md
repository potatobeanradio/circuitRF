# circuitRF — Automatic Updates

**Status:** Design (rev 2 — security review folded in) · **Date:** 2026-08-25 · **Phase:** cross-cutting
(packaging + UI)

How circuitRF, harmonicaRF and wBond update themselves in the background — downloaded while the app
runs, swapped in at the next launch, with no dialog, no elevation prompt, no Gatekeeper warning and no
"Relaunch" button anywhere in the UI.

**Reads with:** `BUILDING.md` (how each installer is produced), `packaging/version.sh` + the repo-root
`VERSION` file (the single source of the version string), `docs/design/ui-architecture.md` (why none of
this may leak below `src/Ui`), `src/Ui/AppVersion.cs` (what the running app believes it is).

**Owner intent.** Match what VS Code and Claude.app do: the user never asks for an update, never
approves one, and never sees an installer. The only thing they ever see is one line in the Message
Panel telling them a relaunch will pick up the new version. And — non-negotiable — **an unreachable
network must be indistinguishable from a normal session.** circuitRF is used on lab machines, air-gapped
networks and hotel wifi. Nothing about this feature may ever make the application slower to start, slower
to simulate, or noisier when offline.

---

## 1. The rule that decides everything

**A silent background update requires the application to live somewhere the running user can write.**

That is not a design preference; it is the whole problem. Everything else in this document follows from
it. A per-machine install (`%ProgramFiles%`, `/opt`) can only be modified by a privileged process, and
the only ways to get one are a UAC prompt on every update or a permanently-installed root/SYSTEM service
that downloads and executes code from the internet. The second is a genuine privilege-escalation surface,
needs its own installer, and fights every corporate endpoint-protection product in existence. It is not
worth it and we are not doing it.

This is why VS Code ships a separate **User Installer** into `%LOCALAPPDATA%\Programs\Microsoft VS Code`,
and why Claude.app on Windows lives in `%LOCALAPPDATA%\AnthropicClaude`. Neither has ever silently
updated a Program Files install, on any version, ever.

Where circuitRF stood before this work:

| Installer | Installs to | User-writable | Silent update possible |
|---|---|---|---|
| `.dmg` (drag to Applications) | `/Applications/circuitRF.app` | yes for an admin user; **no** for a standard user | mostly |
| `.msi` (`Scope="perMachine"`) | `%ProgramFiles%\circuitRF` | **no** | no |
| `.deb` | `/opt/circuitrf` | **no** | no |

**The decision (owner, 2026-08-25): add a user-local install channel on Windows and Linux and make it
the default download.** The `.msi` and `.deb` remain, for machine-wide and centrally-managed installs;
those installs become **notify-only** — they check, and post a Message Panel line with a link, but never
write anything.

### 1.1 One runtime check, not three platform branches

The updater does not ask "what platform am I on?" It asks one question:

> Can this process write its own install tree, and is that tree laid out the way the updater expects?

`UpdateInstallSite.Detect()` answers it by probing for write access at the install root
(`AppContext.BaseDirectory` walked up to the root of the layout) and by checking for the marker file the
user-local installers leave behind. One check covers, uniformly and with no platform special-casing:

- macOS `/Applications` as an admin user → **writable**
- macOS `/Applications` as a standard user → **read-only**
- macOS `~/Applications` → **writable**
- Windows `%LOCALAPPDATA%\Programs\circuitRF` → **writable**
- Windows `%ProgramFiles%\circuitRF` (MSI) → **read-only**
- Linux `~/.local/share/circuitRF` → **writable**
- Linux `/opt/circuitrf` (deb) → **read-only**

Read-only ⇒ notify-only. That is the entire policy, it is one method, and it is testable against a
temp-directory fixture with no platform involvement at all.

---

## 2. Install layouts

The unifying trick on Windows and Linux is **versioned directories behind a stable launch path**, so an
update is a *pointer flip* and never an overwrite of a file that is currently in use. This is the
Squirrel / VS Code model and it exists because you cannot delete or overwrite a running `.exe` or a
loaded `.dll` on Windows — but you never need to, because the new version goes into a *new* directory.

### 2.1 Windows — `%LOCALAPPDATA%\Programs\circuitRF\`

```
circuitRF.exe              <- tiny stub launcher. NEVER changes. Shortcuts, file associations and
                              the Start Menu entry all point here, so an update re-registers nothing.
current                    <- one line of text: the directory name to run
app-1.0.0-beta.1\          <- a full publish tree
app-1.0.0-beta.2\          <- the staged next version
staging\                   <- partial downloads; never executed from
```

The stub reads `current` and starts `app-<ver>\circuitRF.exe`. Update = write a new `current`. The
previous `app-<ver>\` is retained until the new one has launched successfully once (§14). What it will
accept in `current` is a plain relative directory name and nothing else — no separator, no drive letter,
no `.` or `..` — because following anything else turns a corrupt file into an arbitrary program launch
(§9.2).

First install is either a per-user `.msi` — WiX v4 `Scope="perUser"` with `StandardDirectory
Id="LocalAppDataFolder"`, which raises no UAC prompt — or a plain zip plus a first-run bootstrap. Either
is fine; the *update* payload is a zip in both cases, and the updater never runs an installer.

### 2.2 Linux — `~/.local/share/circuitRF/`

Identical shape, with `current` as a symlink:

```
current -> app-1.0.0-beta.2      (symlink, re-pointed atomically via rename(2))
app-1.0.0-beta.1/
app-1.0.0-beta.2/
staging/
```

plus `~/.local/bin/circuitrf` and `~/.local/share/applications/circuitrf.desktop`, both pointing at the
stable `current/` path so nothing needs re-registering on update. Distributed as a `.tar.gz` with a small
`install.sh`. Replacing files under a running Linux process is safe in any case — the kernel keeps the
open inode alive — but the versioned layout means we never rely on that.

*(AppImage was considered as the Linux channel. It is a single file, trivially replaceable, and its
zsync-based updater would give delta downloads for free — which matters at our payload size, §12. It is
also an entirely new build path. Deferred, not rejected; §17.)*

### 2.3 macOS — the exception

A `.app` bundle **is** the launch path: LaunchServices, the Dock, and every file association resolve
`/Applications/circuitRF.app`, and there is no way to version-fold that behind a pointer. So macOS keeps
the bundle where it is and swaps the whole directory atomically:

- `renamex_np(old, new, RENAME_SWAP)` (macOS 10.12+) exchanges two directories in one atomic operation,
  or equivalently `NSFileManager.replaceItemAt`.
- .NET has no managed equivalent — `File.Move` will not atomically swap two directories — so this is a
  small P/Invoke to `libc`. The fallback (rename old aside, rename new into place) is two operations with
  a sub-millisecond window where nothing is at the path; acceptable, but prefer the atomic form.

This is exactly what Sparkle does, so the approach is well-trodden rather than novel.

---

## 3. The update lifecycle

```
   (background thread, >=60 s after launch, at most once per 24 h)
        |
   1. CHECK      GET the release list; pick the newest release for this channel whose version
        |        is greater than AppVersion.Display under SemVer 2.0 precedence. On a build
        |        carrying a release key, a release with no validly signed manifest is not a
        |        candidate at all (15.5).
        |
   1a. SPACE     reclaim our own debris, then require peak + 1 GB free, or stop here (§13)
        |
   2. DOWNLOAD   resumable HTTPS GET of the one asset matching this platform + architecture,
        |        into <AppData>/updates/staging/, re-checking free space as it goes
        |
   3. VERIFY     hash (if published) THEN code signature THEN publisher/Team-ID identity.
        |        macOS verifies the .dmg BEFORE mounting it; Windows verifies every PE in the
        |        staged tree, not just the apphost (§9). Failure: delete the staging directory,
        |        silently, and blacklist that version -- a TRANSIENT unpack failure does not.
        |
   4. STAGE      unpack as app-<ver>.partial\ (Win/Linux) or a .partial bundle (macOS), then
        |        rename to the real name once complete. Nothing incomplete ever holds a real
        |        name, and nothing is swapped yet. (§13.2)
        |
   5. NOTIFY     one Message Panel line, Info level (§10).
        |
   6. SWAP       at the NEXT launch, in Program.Main, BEFORE Avalonia initialises.
                 Win/Linux: write `current.tmp` and rename it over `current` — never a
                            truncating write (§13.2) — and the stub has not started the app
                            yet, so there is nothing to re-exec.
                 macOS:     atomic bundle swap, then execv() the new executable.
```

**Why the swap happens at launch and not at quit.** Quitting looks tempting but needs a detached helper
process to act after the app is gone, and it loses the race against a force-quit or a crash. Doing it in
`Main` before any framework is initialised means no helper, no race, and the app tree is *provably* not in
use. On Windows and Linux the stub model removes even the re-exec: the swap is one text file or one
symlink, flipped before the real process starts.

**Never swap mid-session.** A self-contained .NET app does not load every assembly eagerly and Avalonia
resolves some resources lazily; replacing the tree underneath a running process is a class of bug that
reproduces on someone else's machine, once, six weeks later. The staged version sits inert until the next
launch, which is also what makes the Message Panel wording honest.

---

## 4. macOS: why there is no Gatekeeper prompt

Two independent mechanisms, and the feature needs both. This is the part of the design most likely to be
got wrong by accident, because both failure modes are invisible until a real user on a real signed build
hits them.

### 4.1 Quarantine — why the first-launch dialog does not appear

The "downloaded from the internet, are you sure" dialog is triggered by the `com.apple.quarantine`
extended attribute. That attribute is **set by the downloading application**, which must opt in through
LaunchServices — browsers, Mail, Messages do; `curl` does not; and **a .NET `HttpClient` writing to a
`FileStream` does not.**

That is the entire mechanism. Because our updater fetches its own payload, the staged bundle carries no
quarantine attribute, Gatekeeper's first-launch assessment never runs, and the swapped-in application
starts with no dialog. It is the same reason a `curl`-downloaded app has never prompted.

### 4.2 App Management (macOS 13+) — why the app must be Developer ID signed

Since Ventura, a process that modifies **another** application's bundle requires the user to grant "App
Management" permission in System Settings — precisely the prompt this feature must not produce. The
exemption is that the modifying process is **signed with the same Team ID as the bundle it is modifying.**

Self-update satisfies that by construction — but only if there *is* a Team ID. An ad-hoc signature has no
identity behind it, so **an ad-hoc build cannot silently self-update, even in principle.** This raises
signing from a nice-to-have to a hard prerequisite:

> **Releases must be Developer ID signed and notarized. Automatic updates on macOS do not work on an
> ad-hoc build, and the failure mode is a TCC prompt, not an error.**

`packaging/macos/build-macos.sh` already resolves a `Developer ID Application` identity, refuses to be
fooled by an `Apple Development` certificate, and notarizes through `notarytool` — so the prerequisite is
already met by the existing pipeline. Staple as well (`xcrun stapler staple` on the `.app`, *then* zip;
you cannot staple an archive): stapling costs nothing and it is what lets Gatekeeper validate a manually
downloaded build with no network.

### 4.3 The trap that turns a silent update into a Gatekeeper refusal

**Do not unpack a macOS payload with `System.IO.Compression.ZipFile`.** It drops Unix mode bits and
symlinks. A bundle missing its executable bit and its `Frameworks` symlinks has a *broken code signature*,
which means the swapped-in app is refused at launch — the exact outcome this feature exists to avoid,
arriving by the least obvious route, and only on a real signed build so no test catches it.

Shell out to `ditto`, which is the only macOS-correct answer, in both directions:

```
ditto -c -k --keepParent --sequesterRsrc circuitRF.app circuitRF-<ver>-arm64.zip   # producing
ditto -x -k circuitRF-<ver>-arm64.zip <staging>                                    # consuming
```

The same reasoning rules out a naive recursive `File.Copy` for the bundle swap. Move directories; never
walk and copy them.

---

## 5. Windows: SmartScreen has the same structure

Windows' analogue of quarantine is the **Mark of the Web**, an NTFS alternate data stream named
`Zone.Identifier`, and it too is written by the *downloading* application — browsers set it, `HttpClient`
does not. So the same free pass applies: a payload fetched by the updater carries no MOTW, and SmartScreen
never engages.

Authenticode signing is still wanted for the manually-downloaded first install (a fresh publisher accrues
SmartScreen reputation slowly), and it is what the verification step in §9 checks. But it is not what
suppresses a prompt during a background update; the absence of MOTW is.

---

## 6. Linux

There is no Gatekeeper, no SmartScreen, no quarantine and no notarization. There is also no way to write
`/opt` without root, which is why a `.deb` install is notify-only and the user-local tarball is the channel
that actually auto-updates.

---

## 7. Payload format: never download an installer

The updater must never run a `.msi`, `.dmg` or `.deb`. Those are *user-facing installers* — they want a
window, and two of them want elevation. What the updater wants is a plain archive of the application
payload:

| Platform | Update payload | New asset needed? |
|---|---|---|
| macOS | the existing **`.dmg`** — `hdiutil attach -nobrowse`, `ditto` the `.app` out, `hdiutil detach` | **no** |
| Windows | `.zip` of the publish tree | yes — but the per-user channel needs it anyway |
| Linux | `.tar.gz` of the publish tree | yes — but the per-user channel needs it anyway |

macOS reusing the disk image is worth the small amount of `hdiutil` plumbing: it means the mac release
gains **zero** additional assets and there is no second artifact that can drift out of step with the one
users download by hand.

---

## 8. The feed: GitHub Releases, and nothing else to upload

Releases live at `https://github.com/potatobeanradio/circuitRF/releases`. The requirement is that cutting
a release stays "build the installers, upload them, done" — no second server, no manual manifest editing.

### 8.1 The decision: name-convention matching, with an optional manifest that wins if present

Two designs were on the table. The chosen design is both, layered, because the second one costs nothing
until the day it is needed and is worth a great deal on that day.

**The working path — the GitHub REST API, no extra files.**

```
GET https://api.github.com/repos/potatobeanradio/circuitRF/releases
```

returns, per release, `tag_name`, `prerelease`, `draft`, `body`, and every asset's `name`, `size` and
`browser_download_url`. The updater filters by channel (§8.3), picks the newest release whose version
exceeds the running one, and selects the asset whose **name** matches its own platform and architecture:

```
circuitRF-<version>-<arch>.dmg          arch in {arm64, x64}
circuitRF-<version>-win-<arch>.zip      arch in {x64, arm64, x86}
circuitRF-<version>-linux-<arch>.tar.gz arch in {x64, arm64}
```

**The escape hatch — an optional `update-manifest.json` asset.** If the newest release contains an asset
by that exact name, the updater downloads it (a few hundred bytes) and *prefers it over name matching*.
It may carry per-asset SHA-256, release notes, `minimumUpgradableFrom`, and — the field that matters most
in the long run — a `feedUrl` pointing somewhere other than GitHub (§15).

**Today we publish no manifest.** Cutting a release is exactly "upload the installers." But every shipped
client already knows how to obey one, which means the migration in §15 does not strand the installed base.
That capability is roughly twenty lines and it is only ever cheap *before* it is needed.

A manifest may also carry a **detached signature**, in a sibling `update-manifest.json.sig` asset. That is
§15.5, and it changes what the manifest *is*: an unsigned one is a convenience the allow-list constrains,
a signed one is the integrity guarantee itself and may name a payload anywhere. Which of the two applies
is decided by whether this build carries a release key, not by the release.

### 8.2 What the API costs and what it constrains

- **60 requests/hour per IP, unauthenticated.** At one check per machine per day this is irrelevant, and
  it stays irrelevant unless a large number of users sit behind a single corporate NAT. There is no
  authenticated option worth having — shipping a token in a desktop binary is not a secret.
- **`GET /releases/latest` excludes prereleases and drafts.** We do not use it. We fetch the list and
  filter, which is required for the beta channel anyway (§8.3).
- **The repository must be public** for unauthenticated access. If circuitRF's source is ever made
  private, the standard move is a separate public *releases-only* repository: public releases, private
  source, one URL constant changed.
- **A `digest` field on assets** has begun appearing in GitHub's API responses. Use it when present, but
  treat it as best-effort — the code-signature check in §9 is the guarantee, not the hash.

### 8.3 Channels — stable and beta

The GitHub **prerelease flag is the channel switch.** Nothing else is needed and nothing else is
maintained.

- **`Include Betas` off (default):** only releases with `prerelease == false` are considered.
- **`Include Betas` on:** every non-draft release is considered.

Two consequences worth stating because they look like bugs and are not:

- A user running `1.0.0-beta.3` with betas **off** is offered nothing until `1.0.0` ships — at which point
  they are offered it, because `1.0.0 > 1.0.0-beta.3` under SemVer precedence. Correct.
- If the newest *stable* release is older than the running beta (say `0.9.0` vs `1.0.0-beta.3`), no update
  is offered. Also correct — this is what stops the beta channel from silently downgrading people.

Turning `Include Betas` **off** discards any staged beta, for the same reason that turning updates off
does (§10).

### 8.4 Version comparison is SemVer 2.0, not string comparison and not `System.Version`

`System.Version` cannot parse `1.0.0-beta.1` at all, and a lexicographic comparison gets prerelease
ordering exactly backwards. The required precedence is:

```
0.9.0  <  1.0.0-beta.1  <  1.0.0-beta.2  <  1.0.0-beta.10  <  1.0.0-rc.1  <  1.0.0
```

Note `beta.2 < beta.10`: dot-separated numeric identifiers compare **numerically**, not as text. This is
the same trap `packaging/version.sh` already documents for dpkg (where `0.9.0~beta.1` sorts before
`0.9.0` and `0.9.0-beta.1` sorts after it) — the second appearance of one problem, so it gets one
implementation, `SemanticVersion`, with the ordering pinned by a table-driven test.

The running version comes from `AppVersion.Display`, which reads `InformationalVersion`, which
`Directory.Build.props` reads from the repo-root `VERSION` file. **No version string is introduced
anywhere by this feature** — the root `CLAUDE.md` rule stands unchanged.

---

## 9. Verification, and what is actually trusted

Three checks, in order, before anything is staged. Each one is cheap; the last one is the one that matters.

1. **Transport.** HTTPS with normal certificate validation. No pinning — pinning a public CDN's
   certificate is a way to break your own updater on a Tuesday. The payload URL must be `https` on a
   host in `FeedUrlAllowList`; a `http://` URL, or one on any other host, is refused without a request
   being made (§9.2).
2. **Hash.** Compare SHA-256 against the manifest or the API's `digest` field, when either is available.
3. **Code signature and identity.** On macOS: `codesign --verify --strict` **on the downloaded `.dmg`
   before it is mounted**, and again on the extracted bundle, asserting both times that the Team ID
   equals the running application's. On Windows: Authenticode validity and a publisher match on
   **every PE in the staged tree**, not on the first one.

Step 3 is the real security boundary. Steps 1 and 2 establish that the bytes are the bytes GitHub served;
only step 3 establishes that **we** produced them. It is what survives a mis-issued certificate, a
compromised release, or a mistake in the naming convention that points the updater at the wrong file.

A useful corollary, which §15 depends on: because identity is proved by the code signature, **the host is
trusted only for availability, never for integrity.** GitHub is a bucket. That is why moving off it later
is a small change rather than a security re-analysis.

**Verification failure — and only verification failure — blacklists.** Discard the staging directory,
record the version in a small local blacklist so it is not retried on a loop, and say nothing. The
blacklist is permanent *and* shared: `AppDataRoot` is one directory for all three applications and every
build of them, so an entry withholds that release from every installation on the machine. A payload that
is not signed by us has earned that. A `tar` that was not on the box, a file another process had open,
or any of the other transient reasons an unpack fails has not — and used to earn it anyway, which
stranded a user on their current version permanently and silently. A transient failure retries at the
next check.

### 9.1 Where the identity check stops, and what closes the gap

Stating this precisely matters more than the check itself, because the boundary is not the same on the
three platforms and it is easy to read §9 as though it were.

| Platform | What the identity check covers | What it does not |
|---|---|---|
| **macOS** | Everything. The bundle's seal covers every file inside it, `pcell-python/**` included, and the disk image carries the same Developer ID. | — |
| **Windows** | Every PE in the payload. Today `PublishSingleFile` makes that the whole application in one file. | **Non-PE content Authenticode cannot sign** — chiefly `pcell-python/**/*.py`, which circuitRF executes. |
| **Linux** | Nothing. There is no platform signing infrastructure to ask. | **The entire payload.** TLS to the feed host is the whole of the trust chain. |

So the honest statement of the threat model is: **an attacker who can publish a release to our GitHub
repository gets code execution on Linux, and on Windows through the Python payload — but not on macOS,
where they would also need the Developer ID key.** Everything below the release — a compromised CDN, an
on-path attacker, a mis-issued certificate — is covered on all three by TLS plus §9.2's constraints.

**§15.5's signed manifest is what closes both**, and it is the only thing that does: a public key
compiled into the client, a per-asset SHA-256 inside a manifest signed by the matching private key, and
the host reduced to serving bytes on every platform equally. **It is now built** — but it is inert until
a key exists, because a client demanding a signature nobody is producing yet is a client that never
updates again. So the table above is what this build actually does, and it stops being true for the
release after `ReleaseKeys.PublicKeySpkiBase64` is filled in. §15.5 has the three steps.

Two things follow for as long as no key is compiled in, and they are the cheap half of the same
protection:

- **The GitHub release must be treated as a signing key.** Two-factor on the account, no long-lived
  release tokens, and the same care over who can push a tag as over who holds the Developer ID
  certificate. On two of three platforms it is exactly as powerful. Generating a release key (§15.5)
  is what demotes it back to a bucket.
- **Do not turn `PublishSingleFile` off on Windows** without revisiting this. It is what makes "every PE"
  and "the whole application" the same set. If it is ever turned off, the third-party assemblies in the
  tree are unsigned by us, `VerifyWindowsTree` starts refusing every payload, and updates stop —
  loudly in the log rather than silently, which is the right direction, but it is a decision to make
  deliberately.

### 9.2 What the updater refuses to believe

Everything in this list arrives from outside — a release tag, an asset name, a URL, an archive member, a
line in the updater's own state file — and every entry is a place where one of those becomes a path the
updater writes to, or a program it runs. None was reachable end to end when it was written down; each
existed because the guard that happened to stand in the way was a single one somewhere else in the
pipeline, and a pipeline whose safety rests on one guard per class is a pipeline that is one refactor
from being wrong.

| Input | Rule | Why |
|---|---|---|
| **A release tag** | SemVer 2.0 identifiers only — `[0-9A-Za-z-]`, at most 128 characters | `ReleaseInfo.VersionText` is the tag's own spelling and becomes a path segment: `<install root>/app-<ver>` and `updates/staged/<ver>/`. `1.0.0+../../evil` used to parse. |
| **An asset name** | No separator, no drive letter, no `.`/`..`, no invalid file-name character | It is combined with `staging/` to make a path. |
| **An asset URL** | Absolute `https` on a `FeedUrlAllowList` host — the feed's own asset URLs as well as a manifest's | A plain `http` URL is exactly what an on-path attacker would substitute; an arbitrary host is the whole trust chain on Linux. |
| **The feed URL, and a manifest's** | `FeedUrlAllowList.IsAcceptable`, checked in `ListReleasesAsync` and `GetAssetBytesAsync` themselves | A manifest can re-point the feed *and* name the payload's URL, so where it is fetched from matters at least as much as where the payload is. |
| **A manifest's signature** | ECDSA P-256 over the manifest's bytes as served, at most 1 KB of base64, curve pinned in the verifier | It is the trust anchor once a key exists (§15.5), and it is checked on attacker-supplied bytes on a background thread — so every failure is `false`, never an exception. |
| **A signed manifest's digest** | Exactly 64 lower-case hex characters, and *mandatory* | A signed manifest that names no digest is a signature over nothing that matters: only the hash carries the signature's proof through to the payload's bytes. |
| **An advertised size** | `0 <= size <= 2 GB` | It feeds `EstimateExpandedBytes`, which multiplies it. |
| **A transfer** | Stops at the advertised size, or at 2 GB when the feed publishes none | With no size the read loop had no stop condition, so a server that never closes writes until the volume is down to the 1 GB reserve — §13's own failure, arriving from the network rather than the arithmetic. |
| **A feed document** | 8 MB, enforced by the client | Both the release list and a manifest are read with `ReadAsStringAsync`, which buffers the whole body. |
| **An archive member** | No symlink whose target leaves the extraction tree | `tar` refuses a member *named* with `..`, but a link whose *target* escapes is an ordinary valid member — and the tree is about to be renamed into the live install root and executed from. |
| **A disk image** | Verified and Team-ID matched *before* `hdiutil attach`; mounted `-nobrowse -readonly -noautoopen -owners off` | Mounting hands attacker-supplied bytes to a kernel filesystem parser. `build-macos.sh` already signs the image, so this costs nothing. |
| **`state.json`'s directory names** | `app-` plus version characters, and nothing else — checked at `WriteCurrent`, not only at its callers | Those strings become path components and the contents of the launch pointer. The state file is ordinary JSON in the user's application-data directory. |
| **`current`, read by the stub** | No separator, no drive letter, no `.`/`..` | Following one would turn a corrupt file into an arbitrary program launch. The stub's own comment claimed `..` was rejected; it was not. |
| **`current`, read by `install.sh`** | `app-` plus version characters | It is interpolated into an `rm -rf`. A `current` holding `../..` deletes `~/.local`. |
| **A helper tool** | Started from an absolute path (`/usr/bin/tar`, `/usr/bin/codesign`, …), never a bare name | A bare name resolves through `PATH`, and the Linux user-local install puts its own launcher in `~/.local/bin` — ahead of `/usr/bin` on most distributions. A `codesign` dropped there *is* the verification step. |

**The rule these share:** the check lives at the line that consumes the value, not at the line that
produced it. `UpdateAssetNames.IsSafeAssetFileName` is called in `UpdateManifest.Select` *and* in
`UpdateDownloader.DownloadAsync`; `IsSafeVersionDirectoryName` is called in `FlipPointer`, in `Revert`
*and* inside `WriteCurrent`. Duplicated deliberately, so that no future caller can route around one by
constructing a path itself — the same reasoning as `UpdateStager.Promote`'s live-pointer refusal and
`UpdateReclaimer.Remove`'s running-directory refusal.

## 10. Settings and the Message Panel

### 10.1 Settings ▸ **Security & Permissions** → "Updates" section

**Not the General tab** (owner, 2026-08-25). The Permissions tab was renamed *Security & Permissions*
and is now where everything that decides what circuitRF is allowed to **run** or to **fetch** lives —
the external-PDK generator trust store, the external-device-worker consent
(`Security.ExternalWorkerPolicy`, added the same day and deliberately shaped like `UpdatePolicy`: same
precedence, an environment kill switch and a policy file beside the install), and these two checkboxes
together. A checkbox that governs
whether the application downloads and installs code from the internet is a security setting first and a
convenience second, and someone auditing what this binary is permitted to do should find all of it in
one place rather than in the tab each feature happened to arrive in.

*(harmonicaRF's own dialog is unaffected: it does not use `SettingsView` at all and hosts the same
`UpdateSettingsView` control as its own tab. One implementation, two hosts — §10.1's controls are written
once.)*

Two checkboxes, stored in `AppPreferences` using the existing nullable-with-default idiom
(`bool? AutomaticUpdates`, `bool? IncludeBetaUpdates`) so an absent key means "default":

- **Automatic updates** — default **on**. Downloads new versions in the background and installs them the
  next time circuitRF is relaunched. One `preferences.json` serves all three applications, so this
  governs circuitRF, harmonicaRF and wBond together, and the help text says so.
- **Include beta releases** — default **off**. Sub-item, disabled while automatic updates are off.

Plus one greyed informational line, *Last checked: <time>* or *Last checked: never*. It costs nothing and
it is the first thing anyone looks at when wondering whether the feature is working at all.

**"Never checks" is literal.** The preference is read *before* an `HttpClient` is constructed, not after.
When automatic updates are off, circuitRF opens no socket for any reason.

**Turning either setting off discards a staged update.** A user who unchecks the box and then gets a new
version on the next relaunch has been lied to by the checkbox. So unchecking deletes the staging
directory and reverts `current`/the pending swap.

### 10.2 The Message Panel entry

Info level, posted once per staged version, at the moment staging completes:

> circuitRF updated from 1.0.0-beta.1 to 1.0.0-beta.2 in the background. Relaunch circuitRF to start
> using the version. Automatic updates can be disabled in Settings, under Security & Permissions.

The application name is the running application's, so harmonicaRF and wBond say their own names.

**There is no "Relaunch circuitRF" button, anywhere.** The app can be holding unsaved workspaces; offering
a one-click relaunch invites data loss for the sake of saving a keystroke.

If several versions are staged before the user relaunches, each new staging posts its own line, worded from
the **installed** version to the **newly staged** one — so the last line the user sees is always the true
end state.

### 10.3 Help ▸ Check for Updates…

A menu item that runs the same check immediately, ignoring the 24-hour throttle, and reports through the
**Message Panel** rather than a dialog — up to date, downloading, or could not reach the update server.
This is the one place a network failure is allowed to be visible, because here the user explicitly asked.

The item is present but **disabled** when automatic updates are off (a manual check is still a network
call, and "never checks for updates" has to mean what it says) and when the install site is read-only
(§1.1), where it is replaced by the notify-only path.

---

## 11. Offline, privacy, and the kill switches

circuitRF has never made an outbound network call — there is no `HttpClient` anywhere in `src/` today.
This feature introduces the first one, and that deserves to be deliberate rather than incidental.

**Rules that make it safe to ship:**

- The check runs on a background thread, **at least 60 seconds after launch**, so it never competes with
  startup and never appears in a cold-start measurement.
- ~10 second timeout, one retry, then give up until the next scheduled check.
- **Failure is completely silent.** No Message Panel entry, no dialog, no toast. An unreachable network is
  the *normal* state for a large fraction of this application's users, and a recurring "couldn't check for
  updates" line would be a defect, not a feature. Failures go to the application log only. A full
  disk is the one condition with a narrow exception, for reasons given in §13.5.
- `LastCheckUtc` is persisted; a check is skipped if one succeeded within ~24 hours, **with jitter**, so a
  lab of machines imaged from one disk does not arrive at the API in lockstep.
- Every path is wrapped so that no exception from the update subsystem can reach the UI thread or affect
  shutdown. The updater is not permitted to be the reason anything else fails.
- Downloads are low-priority and **resumable** (HTTP range requests), so a dropped connection resumes
  rather than restarting a 160 MB transfer (§12).
- Staging lives under `AppDataRoot.SubDir("updates")` — already redirectable, so `tools/DocGen` and the
  test suite are isolated from the network **by construction** rather than by remembering to disable
  something.

**What leaves the machine.** One HTTPS GET to `api.github.com` and, if an update exists, one asset
download. The request carries the client IP (unavoidable for any HTTP request) and a User-Agent, which the
GitHub API requires. Keep the User-Agent minimal — `circuitRF/<version>` — and document that no identifier,
telemetry, workspace content or usage data is transmitted, because some of this application's users work
under export-controlled or otherwise restricted network policies and will be asked by their IT department
exactly what the binary contacts.

**Kill switches beyond the checkbox**, for environments where a per-user preference is not sufficient:

- `CRF_NO_UPDATE_CHECK=1` in the environment disables the subsystem entirely.
- A machine-wide policy file next to the install (`no-auto-update`) does the same and **overrides the
  preference**, so an administrator can deploy an installation that cannot be re-enabled by the user.

---

## 12. Payload size — the honest problem

Measured, 2026-08-23 (`dist/`, version 1.0.0-beta.1):

| Artifact | Size |
|---|---|
| `circuitRF-1.0.0-beta.1-arm64.dmg` | 160 MB |
| `circuitRF-1.0.0-beta.1-x64.dmg` | 112 MB |
| `circuitRF-1.0.0-beta.1-*.msi` | 50–53 MB |
| `circuitRF-1.0.0-beta.1-*.deb` | 58–60 MB |
| expanded macOS `.app` | ~333 MB |

The macOS figure is large because the bundle carries a self-contained .NET runtime **plus** the Linux VM
kernel and initramfs that compiled device models are run inside. Downloading that per release, per
application — circuitRF, harmonicaRF and wBond are three separate installs — on a hotel connection is
genuinely rude.

**v1 answer:** at most one check per day, resumable low-priority download, and betas off by default so
the users on the fastest-moving channel are the ones who opted into it.

These figures are also what §13 has to survive: at peak an update needs the download *and* its expanded
copy at once, so the macOS payload above implies roughly half a gigabyte of headroom, not 160 MB of it.

**The highest-leverage improvement, when it is worth doing:** version the Linux VM image *separately* from
the application. It changes almost never, so most updates would drop to the .NET payload alone. That is a
packaging change, not an updater change, and the updater's asset list is already general enough to fetch
two files instead of one.

Real binary deltas (zsync, bsdiff, Squirrel-style delta packages) would do better still and are a
considerably bigger project. Deferred; see §17.

---

## 13. Running out of disk space

**The rule this section exists to guarantee: the updater must never be the reason a user cannot save
their work.** A user with a workspace open and 400 MB free must not have circuitRF quietly consume 495 MB
in the background and then fail to write a `.cws`. Nobody would ever connect the two, and the thing lost
is the user's afternoon.

The structural answer, from which everything below follows: **all space consumption happens in a phase
where abandoning costs nothing, and every operation after that phase is a rename.** Download, unpack and
verify all happen off to the side; by the time anything the running install depends on is touched, the
only remaining operations are directory and pointer renames, which consume no space and cannot half-fail.
A disk that fills mid-update therefore loses a download, never an installation.

### 13.1 A 160 MB download needs ~500 MB of headroom

The naive check — "is there room for the payload?" — is wrong by roughly a factor of three, because at
peak the compressed download and its expanded copy exist at the same time, on top of the installation
already present. Measured from `dist/` and the release publish trees, 2026-08-23:

| Platform | download | expanded | **peak new space** | transient after swap | steady state |
|---|---|---|---|---|---|
| macOS arm64 | 160 MB `.dmg` | 335 MB | **495 MB** | 335 MB | 0 |
| macOS x64 | 112 MB `.dmg` | 184 MB | **296 MB** | 184 MB | 0 |
| Windows x64 | ~50 MB `.zip` | 131 MB | **181 MB** | 131 MB | 0 |
| Linux x64 | ~55 MB `.tar.gz` | 126 MB | **181 MB** | 126 MB | 0 |

*(Windows and Linux download figures are projected from the `.msi` and `.deb`, which compress the same
publish tree; macOS figures are measured artifacts.)*

Reading the columns:

- **Peak** is what must be free before starting. It is `download + expanded`, and it is the number the
  pre-flight check uses.
- **Transient** is what remains held after the swap: the *previous* version, retained as the rollback
  insurance of §14. It is released as soon as the new version clears its startup counter — so it is a
  one-generation cost, not an accumulating one.
- **Steady state is zero.** The updater holds no permanent disk footprint. Exactly one previous version is
  ever retained, never a history of them.

The download can be deleted the moment unpacking succeeds, which drops the requirement from peak to
transient partway through. The check is still made against peak, because a check that has to be right
about *when* it is measured is a check that will eventually be measured at the wrong moment.

**Required free space = peak + reserve, with a 1 GB reserve.** The reserve is deliberately generous and it
is not a payload figure — it is headroom for the user's own work: a workspace save, an EM result set, the
recovery snapshots, the crash reports, and the several percent of free space macOS and Windows both want
in order to behave. The asymmetry justifies the generosity: being too cautious costs one skipped update,
which is invisible and self-correcting on the next check, while being too optimistic costs a user their
unsaved work.

So on an Apple Silicon Mac, circuitRF declines to update below roughly 1.5 GB free. That is the intended
behaviour, not a bug to tune away later.

### 13.2 Where a full disk actually bites

The four points of exposure are not equally dangerous, and the design is shaped by the fact that only one
of them can do lasting harm.

| Phase | Consequence of ENOSPC | Severity |
|---|---|---|
| **Download** | partial file in `staging/`; delete and retry later | benign |
| **Unpack / stage** | partial tree — *benign only if it cannot be mistaken for a complete one* | benign **by construction**, see below |
| **The swap** | **can brick the installation** — the one that matters | mitigated to impossible, see below |
| **First run of the new version** | the new version starts but cannot write preferences, logs or recovery | not the updater's fault, but the updater made it likelier |

**Unpack** is made safe by naming discipline. The tree is written as `app-<ver>.partial\` and renamed to
`app-<ver>\` only once it is complete and verified. A rename within one filesystem is atomic and needs no
space. Nothing ever executes from, or is counted as, a `.partial` directory.

**The swap** is where a careless implementation loses the user's application. On Windows and Linux the swap
is writing a one-line `current` pointer — and **the classic disaster is truncate-then-write**: the file is
opened for truncation, the write fails with ENOSPC, and `current` is now *empty*. The stub launcher no
longer knows what to run, and circuitRF will not start at all — a full disk has become an uninstallation.

The mitigation is absolute and costs nothing:

> **`current` is never written in place.** It is written to `current.tmp` and `rename()`d over the
> original. If the temp write fails there is nothing to clean up and `current` was never touched. On
> macOS and Linux the symlink form is the same pattern: `symlink()` to a temp name, then `rename()`.

macOS's bundle swap is already immune — `renamex_np(..., RENAME_SWAP)` is a directory exchange that
allocates nothing.

### 13.3 The rules

1. **Check before starting.** `peak + 1 GB` must be free, or the check ends with no download attempted.
2. **Re-check while downloading**, every ~16 MB. Free space is not a constant: another process can fill the
   volume during a 160 MB transfer, and a check made only at the start is a check made at the one moment it
   is least likely to still be true.
3. **Every write lands by rename, never by truncation.** `staging/*.partial` → the real name;
   `current.tmp` → `current`. This is what makes every failure mode recoverable rather than destructive.
4. **Nothing incomplete is ever named as though it were complete.** A `.partial` suffix is the whole
   mechanism, and it is what lets the next launch tell wreckage from a staged update.
5. **Reclaim our own footprint before giving up**, in this fixed order, retrying the space check after each
   step: (a) `staging/` — always safe, it holds only partial downloads; (b) any `.partial` trees;
   (c) abandoned or blacklisted staged versions; (d) the retained previous version, **but only if the
   running version has already cleared its startup counter**, since at that point the rollback it insures
   against can no longer be triggered (§14).
6. **Never delete anything outside `<AppData>/updates/` and the install root's own `app-*` directories.**
   The updater does not have opinions about the user's disk. It does not clear caches, it does not touch
   workspaces, and it does not "helpfully" find space anywhere it did not itself consume.
7. **Handle ENOSPC at every write anyway.** The pre-flight check is an optimization that avoids wasting
   150 MB of someone's bandwidth; it is not a correctness guarantee, because it cannot be one. Correctness
   comes from rules 3 and 4.

### 13.4 Measuring free space — and the APFS trap

.NET's `DriveInfo.AvailableFreeSpace` reports the raw `statfs` value. On APFS that number can be
dramatically *lower* than what the volume can actually provide, because macOS counts local Time Machine
snapshots and other evictable content as used: a Mac showing "20 GB available" in Finder can report ~2 GB
through `statfs`, with the difference being **purgeable** space that the system would reclaim on demand.
Apple exposes the optimistic figure separately, as `volumeAvailableCapacityForImportantUsageKey`.

**Use the raw, pessimistic number, and accept being over-cautious.** The two errors are not symmetric,
exactly as in §13.1: an over-cautious check skips an update on a machine that had room, which nobody
notices and the next check may well fix; an over-optimistic check starts a 495 MB write against space that
only exists if the OS cooperates promptly, on a volume that is already nearly full.

This is recorded here because it looks like a bug and invites a fix. Someone will eventually notice that
circuitRF refuses to update on a Mac whose Finder window says there is plenty of room, and will reach for
the important-usage key. **That would be a regression**, and the reason is written down so that it is a
decision rather than a rediscovery.

Windows' `GetDiskFreeSpaceEx` and Linux's `statvfs` have no equivalent wrinkle; the raw figure is the real
one on both.

### 13.5 What the user sees

Background updates stay **silent**, as §11 requires. A failed check is a failed check whatever caused it,
and a user whose disk is full has louder problems than a missed update.

Two deliberate exceptions, because a full disk differs from an unreachable network in one important way —
being offline is often permanent and often intentional, whereas a full disk is an accident the user wants
to know about and can act on:

- **Help ▸ Check for Updates…** reports it explicitly, naming the figure: *"circuitRF needs about 1.5 GB
  of free disk space to install the update and there is 380 MB available."* The user asked, so the answer
  is specific enough to act on.
- **At most one Message Panel line per 30 days**, and only when insufficient space is the *sole* reason
  updates are not happening — so it is information, not nagging. A user who ignores it is told again in a
  month, not at every check.

Everything else — every skipped check, every reclaim, every retry — goes to the application log only.

### 13.6 Self-healing across restarts

A process killed mid-update, or one that filled the disk badly enough that it could not even clean up
after itself, leaves `staging/` and `app-<ver>.partial\` behind. Both are reclaimed at the **next launch**,
before the update subsystem does anything else, and both are unconditionally safe to delete because rule 4
guarantees that nothing incomplete has ever been given a real name.

The practical consequence is that a disk-full event is self-limiting: the wasted space is returned the next
time circuitRF starts, whether or not the update ever completes, and no sequence of failures accumulates
debris. A user who fills their disk during an update and never updates again ends up with exactly the
footprint they started with.

### 13.7 Testing it

Disk-full is famous for being tested only in production. Most of it does not have to be:

- **The arithmetic is a pure function.** `RequiredFreeSpace(downloadBytes, expandedBytes, reserve)` is
  table-tested, with the peak-vs-transient distinction of §13.1 pinned so that a future change which stops
  deleting the download, or starts retaining two previous versions, fails a test instead of a user's disk.
- **The policy is testable behind an injected probe.** `IFreeSpaceProbe` is faked, so the skip decision,
  the reclaim order of rule 5, and the "never delete outside our own directories" guarantee are all
  ordinary unit tests against a temp-directory fixture. No real full disk is involved.
- **The atomicity properties need no full disk at all.** Abort partway through an unpack and assert that
  the installation still launches and that `current` is untouched; abort partway through a `current.tmp`
  write and assert the same. These are the tests that actually protect against §13.2's bricking case, and
  they are cheap.
- **Real ENOSPC gets a small filesystem**, and it is a manual or opt-in step rather than a routine one —
  mounting filesystems inside the default `dotnet test` gate is not worth what it costs on CI. A 600 MB
  image is enough to drive every path: macOS `hdiutil create -size 600m`, Linux a loopback file or a
  size-capped `tmpfs`, Windows a VHD. Run it when the staging or swap logic changes.

---

## 14. Rollback and failure modes

The worst outcome this feature can produce is a user whose working application was silently replaced by a
broken one, with no way back. Two cheap mitigations, both mandatory:

1. **Do not delete the previous version until the new one has launched successfully at least once.**
   On Windows/Linux that means keeping the previous `app-<ver>\` directory; on macOS it means keeping the
   replaced bundle under `<AppData>/updates/previous/`. This costs one release's worth of disk and it is
   the single best piece of insurance in the design.
2. **Automatic revert on repeated startup failure.** A launch counter is written before the first window
   appears and cleared once it does. If a swapped-in version fails to clear it *N* times (N = 2), the
   pointer is reverted to the previous version and a Message Panel line explains what happened. The crash
   reporting landed in `8532bd3` gives this a place to send the detail.

Other failure modes and their handling:

| Failure | Handling |
|---|---|
| Network unreachable, DNS fails, timeout | silent; retry at the next scheduled check |
| API rate-limited (HTTP 403 + rate-limit headers) | silent; back off to the next day |
| Download interrupted | resume via range request; three attempts, then discard |
| Hash or signature mismatch | discard, blacklist that version locally, silent |
| Unpack failed for any other reason (no `tar`, a locked file, an odd archive) | discard, **no blacklist**, retry at the next check — a transient failure must not strand a user permanently (§9) |
| A payload URL off the allow-list, an absurd advertised size, an escaping symlink in the archive | refused before it is used; silent, no blacklist (§9.2) |
| Insufficient disk space | reclaim own debris, then skip and retry next check; ENOSPC mid-write is recoverable by construction — §13 |
| Install root not writable | notify-only (§1.1) |
| Staged version deleted by the user or antivirus | detected at swap; fall through and re-stage later |
| Two circuitRF processes running | swap is at launch and pointer-based; last writer wins, both end up on the same version |

---

## 15. Moving the payload off GitHub entirely

Nothing in this design is committed to GitHub. This section records what a move would cost, and — more
usefully — which decisions to make *now* so it stays cheap.

### 15.1 What is actually being depended on

GitHub supplies exactly two things:

1. **A version query** — some cheap request that answers "what is newest?" without transferring the payload.
2. **A large-file HTTPS download** that supports range requests.

It is explicitly **not** trusted for integrity. Per §9, authenticity comes from the Apple/Authenticode code
signature and a Team-ID/publisher identity check that only we can produce. A replacement host is a bucket
of bytes; substituting it changes availability characteristics and nothing about the security model.

### 15.2 What a replacement must provide

- HTTPS with a publicly-valid certificate chain.
- **HTTP range requests**, or resumable downloads stop working at 160 MB.
- A stable, cacheable URL for the version query, ideally with sane cache headers — a manifest that is
  cached for six hours is fine and reduces load; one cached for six days is not.
- Availability comparable to GitHub's, since an unreachable feed is silently degraded (which is safe, but
  means updates simply stop happening and nobody finds out).
- A bandwidth bill you are willing to pay. This is the real constraint: at ~160 MB per macOS user per
  release, 1,000 users × 12 releases/year is ~2 TB/year for that platform alone.

### 15.3 Candidates

| Option | Egress cost | Notes |
|---|---|---|
| **Cloudflare R2** + a Worker or plain public bucket | **zero** | S3-compatible, range requests, no egress fee at all. Storage is pennies per month at this size. The standout choice, and the one that makes the bandwidth arithmetic above disappear. |
| Backblaze B2 (+ Cloudflare via the Bandwidth Alliance) | ~zero through Cloudflare | Similar economics, one more moving part. |
| AWS S3 + CloudFront | ~$0.085/GB | Works perfectly; the 2 TB/year above becomes a real, recurring bill. |
| Azure Blob / Google Cloud Storage | similar to AWS | Same shape, same trade-off. |
| GitHub Pages | free, **100 GB/month soft limit** | Not an escape from GitHub, and the soft limit is well below one release's traffic. Not viable for payloads; fine for a manifest. |
| Own VPS + nginx | metered by the provider | Full control; you now own TLS renewal, uptime, and a bandwidth ceiling that a popular release day can hit. |

**Recommendation if the move ever happens: Cloudflare R2**, with the manifest and the payloads in the same
bucket. Zero egress is decisive for a 160 MB artifact, and the S3-compatible API means the packaging
scripts change from `gh release upload` to an `aws s3 cp` / `rclone copy` line — three lines per script.

### 15.4 The one hard part: the installed base

Code changes are trivial. `IUpdateFeed` has one implementation today (`GitHubReleasesFeed`); a second one
is a single file, and the URL is one constant. The packaging scripts gain one upload line each.

**The hard part is that already-installed copies of circuitRF ask the feed they were compiled against.**
Turn GitHub off and every client older than the migration is stranded on its last known version, silently,
forever — the worst possible failure because nobody notices.

**This is why §8.1's optional `update-manifest.json` exists and why it is worth shipping the client-side
support now, before it is needed.** The migration then looks like this:

1. Stand up the new host. Publish payloads and a manifest there.
2. Publish an `update-manifest.json` **to GitHub** whose `feedUrl` field points at the new host. Every
   client that has ever shipped reads it, persists the new feed URL, and never asks GitHub again.
3. Keep publishing that one small GitHub asset for a deprecation window — a year, say. It is a few hundred
   bytes per release.
4. Stop.

The alternative, with no manifest support in the client, is that a GitHub asset URL cannot be made to point
anywhere else, so the only migration path is "tell users to reinstall by hand," which reaches the users who
read release notes and no one else.

**`feedUrl` is a security-relevant field** and must not be a blind redirect. Constrain it: honour it only
from an allow-list of hostnames compiled into the client, or — better, if a signed manifest is ever
introduced — only from a manifest carrying a valid signature from a key embedded in the application.

### 15.5 The signed manifest — **built, and inert until a key exists**

This was the deferred long-term shape. It is now implemented, because §9.1's table made the cost of
deferring it specific rather than general: without it, whoever can publish a release to the host gets
code execution on Linux and, through `pcell-python/**`, on Windows. It is the only mechanism that closes
either, and neither the platform code signature nor the host's TLS can be made to.

**What it is.** A public key compiled into the client (`src/Ui/Updates/ReleaseKeys.cs`), a per-asset
SHA-256 inside an `update-manifest.json`, and a detached signature over that manifest's exact bytes in a
sibling asset `update-manifest.json.sig`. The signature proves the manifest; the SHA-256 carries that
proof through to the payload. The host then serves bytes and nothing else, on every platform equally.

- **ECDSA P-256 / SHA-256, not Ed25519.** The earlier note said "minisign-style EdDSA", which is what
  Sparkle uses. .NET has no managed Ed25519, and pulling in a native dependency for one would trip the
  root `CLAUDE.md`'s **ask before** — for a change of signature algorithm, not of security level. P-256
  is in the BCL, is the same 128-bit level, and needs nothing that is not already on every machine the
  application runs on. The curve is pinned in the verifier rather than read out of the key, so a key
  naming a weaker one is refused even though it is the key we compiled in.
- **The signature is detached, and the manifest's own reserved `signature` field is not used.** A
  signature carried inside the document it signs needs a canonicalisation rule, and a canonicalisation
  rule is a second specification that two programs written years apart both have to get exactly right.
  Signing the bytes as served has no such rule. The field stays *parsed* so a manifest written against
  the earlier note is not rejected as malformed; nothing reads it.

**The switch is the key's presence, and the demand is unconditional.**

| `ReleaseKeys.PublicKeySpkiBase64` | Behaviour |
|---|---|
| empty (**as shipped**) | Exactly as before. Manifests are honoured if present and constrained to the allow-list; platform code signing is the only integrity check. Nothing changes for anyone. |
| set | A release is **not a candidate at all** unless it carries `update-manifest.json`, a valid `update-manifest.json.sig`, and a well-formed SHA-256 for this platform's asset. |

It has to be unconditional. "Verify the signature if one is present" is a downgrade attack with extra
steps: an attacker who can publish a release can publish one with no manifest, and an updater that reads
the absence of a signature as *nothing to check* has learned nothing from checking.

**What the key relaxes, on purpose.** A keyed build accepts a `feedUrl` and a payload URL on **any**
`https` host, in place of `FeedUrlAllowList`. That is not a weakening — it is what the signature was
for. The allow-list exists because, with nothing but TLS behind a manifest, the host *is* the trust
anchor; a keyed build's anchor is a key that is on no host. Constraining the hostname then stops adding
anything and starts preventing the two things §15.4 and this section exist to enable: moving the payload
off GitHub, and mirroring it. `https` is still required — TLS no longer carries integrity here, but it
still carries confidentiality, and which version of which application a machine is fetching is not
something to put on the wire in the clear. One predicate, `FeedUrlAllowList.IsAcceptable`, states the
whole rule and every fetch site calls it.

**Turning it on is a forward migration, and it is one-way.** Clients older than the keyed build have no
key and update normally; the keyed build and everything after it require a signature. So:

1. `dotnet run --project tools/ReleaseSigner -- keygen release-key.pem` — keep the private key **off this
   repository and off the build machine if you can**, and back it up. Losing it strands every installed
   copy until they reinstall by hand.
2. Paste the printed public key into `ReleaseKeys.PublicKeySpkiBase64` and commit it. A *public* key
   belongs in version control: it is the thing being trusted, so it should be reviewable, diffable and
   attributable to the commit that introduced it.
3. From that release onward, every release carries `update-manifest.json` and `update-manifest.json.sig`.
   `ReleaseSigner manifest` builds the first from `dist/` (computing each SHA-256) and `ReleaseSigner
   sign` writes the second. `BUILDING.md`'s release checklist has the commands.

**`tools/ReleaseSigner` references nothing else in this repository**, per the root `CLAUDE.md`'s rule for
`tools/`: it implements the format the client reads rather than sharing code with it, so the two agreeing
is evidence rather than a tautology. `tests/Ui.Tests/Updates/SignedManifestTests.cs` verifies a fixture
the *tool* produced, which is the half a self-signing test could never prove.

**What it still does not cover.** A compromise of the signing key itself. On macOS and Windows the
platform code signature is a second, independent anchor and both checks still run — so the key alone is
not enough there. On Linux it is, which is the residual risk and the reason the private key's handling is
a release-process matter rather than a build-script one.

### 15.6 Mirroring, once a key exists

Not built, and now cheap enough to be a decision rather than a project: publish the identical signed
manifest to two or three hosts and have the client try them in order. The client already accepts a
`feedUrl` on any host from a signed manifest, so what is missing is a list rather than a mechanism.
Worth doing at the same time as any move off GitHub (§15.3's recommendation is Cloudflare R2), because
a self-hosted bucket introduces exactly the single point of failure that mirroring removes.

## 16. Testing

The UI firewall applies unchanged: none of this may create a dependency from `src/Core`, `src/Engine`,
`src/Cli` or `RfCore` on anything new. The updater lives in `src/Ui`.

**Pure functions, ordinary unit tests, no network:**

- `SemanticVersion` precedence, including prerelease ordering and the `beta.2 < beta.10` numeric case (§8.4).
- Channel filtering: which release is selected for each combination of running version, `IncludeBetas`, and
  a release list containing prereleases and drafts.
- Asset selection: platform × architecture → asset name, across every artifact the three packaging scripts
  produce.
- `UpdateInstallSite.Detect()` against temp-directory fixtures for each layout, writable and read-only.
- The swap and the rollback, against a fake install tree in a temp directory.
- Free-space arithmetic and the reclaim policy, behind a faked `IFreeSpaceProbe` — §13.7, which also
  covers the abort-midway atomicity tests and the small-filesystem ENOSPC run.

**The test that prevents silent, permanent failure:** the naming convention in §8.1 is parsed by the client
and produced by `build-macos.sh`, `build-windows.ps1` and `build-linux.sh`. Rename an artifact and updates stop —
with no error, no log line, and no user report, because a user who is not being offered an update has
nothing to report. So `tests/Ui.Tests/` gains an assertion that the file names those scripts construct match
the exact patterns the updater matches. This is the same class of guard as the pure-ASCII `.ps1` rule and
the single-source `VERSION` test, and it exists for the same reason: the failure is silent.

**Network is faked, always.** `IUpdateFeed` is behind an interface; tests use canned JSON. No test in this
repository may make a network call. The download tests drive an `HttpListener` bound to the loopback
interface inside the test process, which is why `UpdateDownloader.UrlIsAllowed` and `MaxTransferBytes`
exist as `init` seams — the shipping allow-list refuses `http://127.0.0.1`, exactly as intended, and the
2 GB cap cannot be crossed by a fixture. Both defaults are asserted against the real values by
`UpdateSecurityHardeningTests`, so the seams cannot quietly become the shipping configuration.

**Every refusal in §9.2 has a test**, in `tests/Ui.Tests/Updates/UpdateSecurityHardeningTests.cs`. Three
of them are source scans rather than behaviour, because the behaviour needs a signed artifact and a real
platform: that the disk image is verified before it is mounted, that it is mounted inert, and that the
Windows check is against the tree. A source scan is the weakest form of test in this repository and is
used only where the alternative is no test at all.

**What tests cannot cover, and must be checked by hand once per platform before shipping:**

- macOS: that an updated build launches with **no** Gatekeeper dialog and **no** App Management prompt.
  This is the requirement the feature exists to satisfy and §4.2's Team-ID exemption is the mechanism it
  depends on — verify it empirically on a real Developer ID signed, notarized build downloaded onto a clean
  machine, rather than trusting this document.
- Windows: that a per-user install updates with no UAC prompt and no SmartScreen warning.
- Linux: that the `~/.local` install updates and that the `.desktop` entry still launches afterwards.

This manual step is irreducible. It should be a checklist item in `BUILDING.md` at every release that
changes signing, packaging layout, or the updater.

---

## 17. Not in v1

Recorded so that they stay visible rather than quietly becoming permanent omissions:

- **Delta updates.** Full payloads every time. §12 explains why splitting the Linux VM image out is the
  cheaper first move, and zsync/bsdiff the more expensive second one.
- **AppImage as the Linux channel**, with its zsync-based delta updates (§2.2).
- ~~**A signed manifest**~~ — **built** (§15.5), and shipping inert: the client half is complete and
  every release from the one that carries a key onward must be signed. What is left is the key ceremony,
  which is the owner's to perform and is deliberately not something a build script can do.
- **Multi-host mirroring** (§15.6). Now a list rather than a mechanism, since a signed manifest may name
  any host. Worth doing at the same time as any move off GitHub.
- **Updating the three applications in one operation.** circuitRF, harmonicaRF and wBond are separate
  installs and each updates itself independently, which costs a user with all three a threefold download.
- **A "What's new in <version>" entry on the first launch after an update.** Cheap — compare `AppVersion`
  to a persisted `LastRunVersion` — and a natural companion to §10.2, but not asked for.
