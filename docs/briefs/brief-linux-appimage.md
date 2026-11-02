# Brief APPIMG — Linux AppImage, with a Download button on its update line

**Source:** GitHub issue #29 asked for Linux releases as AppImages, with a working proof-of-concept script.
**Tag:** `R-appimg-<n>` · **Size:** medium (one packaging change, one updater change, docs)
**Findings:** `packaging/RESOLVED.md` (items 1-3), `src/Ui/RESOLVED.md` (items 4-6). Never `CLAUDE.md`.
**Tests:** one per claim, in the existing classes named below. Run only those classes, never the full suite or all of
`Ui.Tests`.

---

## Why, and what this is not

An AppImage is one executable file holding the application in a compressed read-only image; running it mounts the
image and starts the app from the mount. No install step, no root, no distribution dependency. For Linux users it
means one download that runs as-is and can be kept anywhere (a USB stick, a lab machine, several versions side by
side).

`docs/design/auto-update.md` §2.2 considered an AppImage as the Linux channel and deferred it (§17), because it is a
new build path and because it cannot update itself the way the tarball does: it runs from a read-only mount, so the
versioned `app-<ver>/` + `current` swap has nowhere to write.

This brief therefore **adds** an AppImage; it replaces nothing:

- The **tarball stays** the self-updating Linux install and the update payload. `UpdateAssetNames` still matches it
  for the tarball install, and its name does not change.
- The **`.deb` stays**, notify-only, for machine-wide installs.
- An AppImage install is **not silently updated**. Its update line carries a **Download** button. A click downloads
  the new AppImage next to the running one, verifies it, and marks it executable. Nothing is replaced, deleted or
  relaunched; the user starts the new file when they choose.

A full Linux release goes from 4 files to 6.

## Do not merge the issue's script as written

It proves the idea and is credited for it in the commit message (by issue number). It is not the shape to ship:

- It is a second standalone script that repeats the publish and both kernel builds. The repo rule is exactly one
  packaging script per platform, whose no-argument form builds everything that platform ships (see `CLAUDE.md`,
  "Package"; 1.0.0-beta.2 shipped 7 of its 15 artifacts when this was not so).
- It builds x64 only, and its squashfs `-Xbcj x86` filter is wrong for arm64.
- It fetches `appimagetool` from the old repository's moving `continuous` build: unpinned, unverified, and possibly
  the older runtime that needs `libfuse2`, which current Ubuntu does not install by default.
- It writes its own `.desktop` file (no MIME types) instead of `packaging/linux/circuitrf.desktop`.
- It skips CliSmoke and `PackagingScriptTests`, and it ends with a stray `EOF` line.

---

## 1. A third kind in `build-linux.sh` (R-appimg-1)

Add `appimage` beside `deb` and `tarball`: `[x64|arm64|both] [deb|tarball|appimage|all]`, where `both`/`all`
for the kind means all three. **The no-argument form builds all six files.** Update the header comment's file
list, the usage lines, and the end-of-run count (`expected 4` becomes `expected 6`).

It reuses the per-architecture `PUBLISH` tree, which already holds the geometry kernel, the gdstk worker, the
device and OSDI workers, and has passed CliSmoke. Nothing is published or built a second time. The AppDir:

```
AppDir/
  AppRun                       # exec "$HERE/usr/lib/circuitrf/circuitRF" "$@"
  circuitrf.desktop            # generated from packaging/linux/circuitrf.desktop (below)
  circuitrf.png                # packaging/linux/icons/circuitrf.png
  .DirIcon -> circuitrf.png
  usr/lib/circuitrf/           # cp -a "${PUBLISH}/." here, chmod +x on the same five files the tarball sets
  usr/share/mime/packages/circuitrf.xml
  usr/share/icons/hicolor/512x512/apps/circuitrf.png
```

- **The desktop file is derived, not duplicated:** copy `circuitrf.desktop`, rewriting only its `Exec=` line to
  `Exec=circuitRF %F`, so a change to the name, categories or MIME list reaches all three kinds. Tools that
  integrate AppImages into the menu read it from the image.
- **`AppRun` passes every argument through.** The installed executable is the CLI (`CLAUDE.md`, AUT-13), so
  `./circuitRF-<ver>-linux-x64.AppImage sparam pad.cnl` must work as it does from the tarball.
- **File name:** `circuitRF-<CRF_VERSION>-linux-<arch>.AppImage`, built through the same `CRF_VERSION` as the
  tarball (`packaging/version.sh`, never a literal).

## 2. Pinned, verified tools; both architectures (R-appimg-2)

- Pin `appimagetool` and the **static type-2 runtime** (the one that does not need `libfuse2`) for both
  architectures, by URL **and SHA-256**, in a new `packaging/linux/appimage.env` (the same `KEY=value` shape as
  `tools/geometry-worker/occt/recipe.env`). Cache the downloads per user beside the existing kernel caches; verify
  the hash on every use and refuse on a mismatch.
- Run the host's `appimagetool` and pass the **target's** runtime with `--runtime-file`. That is what cross-builds
  the arm64 image on x64 and the reverse, matching the kernels' cross-build story.
- Pass `--appimage-extract-and-run` or `APPIMAGE_EXTRACT_AND_RUN=1` to the tool, so a build machine without FUSE
  (a container, a CI runner) can still pack.
- **Stop and ask the owner** if upstream offers only a moving `continuous` URL for either file, with nothing that
  names a fixed build. Do not pin a hash to a URL whose content is expected to change.
- **Compression:** measure zstd and xz once on the x64 image (file size, and time from launch to the `--version`
  output, cold and warm). Use zstd unless xz is clearly smaller for no visible start-up cost. Record both figures
  and the choice in `packaging/RESOLVED.md`. This is a one-off measurement, not a test.

## 3. Smoke-test the AppImage itself (R-appimg-3)

After packing, run CliSmoke against the **`.AppImage` file** with the same arguments the publish tree got
(`--version`, `reference --json`, `serve`, kernel and gdstk presence). Use `APPIMAGE_EXTRACT_AND_RUN=1`, so the
check does not depend on FUSE on the build machine. This is what proves `AppRun` and the argument pass-through. The
existing rule applies unchanged: the other architecture runs only through qemu binfmt, and not smoke-tested counts
as not passed unless `CRF_ALLOW_UNSMOKED=1`.

**Tests**, in `tests/Ui.Tests/PackagingScriptTests.cs`:
- `build-linux.sh` builds the AppImage name through `CRF_VERSION` in exactly the spelling
  `UpdateAssetNames.ExpectedAppImage` produces (the same guard the tarball has).
- The no-argument kinds include `appimage`, and the full-run count is 6.
- `appimage.env` carries a SHA-256 for each pinned download, and the script never names a `continuous` URL.
- The AppImage `Exec=` comes from `circuitrf.desktop` and is not a second hand-written desktop file.

## 4. The updater recognises an AppImage install (R-appimg-4)

The AppImage runtime sets `APPIMAGE` (the absolute path of the `.AppImage` file) and `APPDIR` (the mount point).
**Both are inherited by every child process**, so a tarball install launched from a terminal that an AppImage
started would see them too. Detection must therefore rest on where this process actually runs, not on the
variables alone:

- `UpdateInstallSite.DetectFrom` gains a fourth shape, `InstallShape.AppImage`. It applies only when `APPDIR` is set
  and `AppContext.BaseDirectory` lies inside it, **and** `APPIMAGE` names an existing regular file. Then `Root` is
  the file's directory, and `IsWritable` is a probe of that directory (probed, never inferred, as for the other
  shapes).
- `CanSelfUpdate` stays **false** for this shape. Nothing about an AppImage is updated silently.
- Keep the testable form: take the two values as parameters (a `DetectFrom` overload), and keep the environment
  read inside `Detect()` only.
- `UpdateAssetNames` gains `ExpectedAppImage(app, version, archToken)` →
  `{app}-{version}-linux-{archToken}.AppImage`, and the selector picks **that** asset when the site is an
  AppImage. A release with no AppImage for this architecture offers nothing, the same rule as today.
- The signed manifest already covers it: `packaging/sign-release.sh` signs everything in `dist/`, so each AppImage
  gets a SHA-256 under the release key with no change there. Confirm this in the RESOLVED entry rather than
  assuming it.

**Tests** in `UpdateInstallSiteTests` and `UpdateAssetNameTests`:
- A base directory inside `APPDIR` with `APPIMAGE` naming a file gives `AppImage` with that file's directory as
  `Root`.
- The same variables with a base directory **outside** `APPDIR` give the shape the directory itself has
  (`VersionedPointer` for a tarball fixture).
- `ExpectedAppImage` produces the documented spelling, and `Select` on an AppImage site picks it over the tarball.

## 5. The update line gets a Download button (R-appimg-5)

In `UpdateService`, an `AppImage` site takes the notify-only path: it checks and writes nothing in the background.
Its line is different:

- **Text:** `circuitRF <ver> is available` (`UpdateApp.Name`, never a literal). Not the read-only sentence, because
  "a location this account cannot write" is untrue for an AppImage in `~/Applications` and would send the user to
  move a file that is where it should be.
- **Button:** `Download`, through `IMessageSink.PostAction`, the mechanism the Relaunch line already uses.
  R-AU-48 forbids a relaunch button; this button closes nothing and starts nothing, so R-AU-48 does not apply.
  Say so in the code comment, and correct `AnnounceNotifyOnly`'s comment, which cites R-AU-48 for a rule it does
  not state.
- **Once per version** in the background (the `Announced` state, as now); a manual Help ▸ Check for Updates… posts
  it again.

**On a click:**
1. **Writable `Root`:** check free space (`UpdateSpace`); then `UpdateDownloader` fetches the asset to
   `<Root>/<asset name>.partial` under the same URL allow-list, size cap and stall timeout. The download shows
   progress on the line's own row, the way the background download does.
2. Verify the SHA-256 against the **signed manifest** (`PayloadVerifier.VerifyHashAsync` with the manifest's
   digest). This build carries a release key, so a missing or unsigned digest is a refusal, not a pass.
3. Set mode `0755` (`File.SetUnixFileMode`), then rename `.partial` to the final name. The asset name has already
   passed `UpdateAssetNames.IsSafeAssetFileName`. If the final name already exists with the right hash, skip the
   download and report it as done.
4. Complete the row: `circuitRF <ver> downloaded to <full path>` with an **Open Folder** button
   (`FileReveal.Reveal`; do not write a second one). No relaunch, no deletion of the running file, no auto-start.

- **`Root` not writable**, for example an AppImage run from a read-only share: the button opens the asset's
  release URL in the browser, and the line says the file must be made executable before it runs. Nothing is
  written.
- **A failure after the click** (network, hash, disk) is reported on the row in one sentence, and `.partial` is
  deleted. R-AU-50's background silence does not apply to something the user just asked for, the same
  asymmetry R-AU-49 already makes for a manual check. A hash failure also goes in the blacklist, as for the
  tarball.

**Tests** in `UpdateServiceBehaviourTests` (fake feed and fake sink, as the existing ones use):
- An AppImage site posts one action line labelled `Download` and writes nothing before the click.
- The click writes the final file, executable, and leaves no `.partial`.
- A hash mismatch leaves no file, reports on the row, and blacklists the version.
- An unwritable `Root` opens the URL and writes nothing.

## 6. A read-only install tree (R-appimg-6, audit)

Everything under `usr/lib/circuitrf/` is read-only at run time. Find anything that writes beside the executable
(`grep` for `AppContext.BaseDirectory` and `Environment.ProcessPath` joined to a write). Workers, PCell Python
bytecode caches and any first-run extraction are the likely places. Redirect each one to the per-user state
directory, or show that it already fails harmlessly. List what was found in `src/Ui/RESOLVED.md`, including
"nothing" if that is the result.

## 7. Documentation

- `README.md` Download table: two AppImage rows beside the tarball rows, plus one sentence: download, make
  executable, run; updates are offered with a Download button rather than installed automatically, so use the
  tarball to update automatically.
- `BUILDING.md` Linux section: the third kind, `appimage.env`, and FUSE not being needed to build.
- `docs/design/auto-update.md`: §2.2's AppImage paragraph and §17's entry describe what ships now, as a current
  fact (no "was deferred" history). Add the AppImage row to the §1 table.

## Owner check (cannot be done from this shell)

On a Linux machine: run each AppImage from a non-root account, then open an example workspace, run an S-parameter
analysis, and open a `.c3d` that uses a boolean (geometry kernel). Then, on a build one release behind the latest
published one, check that a manual update check posts the Download line and that the downloaded file runs.
