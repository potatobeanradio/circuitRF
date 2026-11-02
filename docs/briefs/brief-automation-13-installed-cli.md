# Sonnet Brief — AUT-13: the CLI and the MCP server ship in every installer

**Read `brief-automation-7-mcp-hardening.md` first**, then `docs/design/cli.md` §11 (`serve`) and the
header of `packaging/windows/stub/circuitrf-stub.c`. Touches packaging on all three platforms, so it
must be finished on all three: a release is cut on each platform by its own script (CLAUDE.md
§Package), and a step that only one script got is the defect this brief exists to remove.

**Scope: an installed circuitRF answers `circuitrf <verb> …` and `circuitrf serve --root <dir>`,
exactly as `dotnet run --project src/Cli --` does from a checkout — and the installers grow by about
a megabyte, not by another copy of the application.**

---

## 0. What is wrong, and why nothing caught it

Every release to date — 1.0.0-beta.1 through beta.32 — ships the GUI and **no command-line driver at
all**. All three packaging scripts publish `src/Ui` only; `src/Ui` does not reference `src/Cli`; and
`Program.Main` handles no verb, so `circuitRF serve` opens a window. An agent told to "install
circuitRF from the release and drive it over MCP" has nothing to drive. The user guide's `serve`
section tells a client to launch "the circuitRF executable" with `serve --root <dir>`; no installed
executable does that.

It is worse than absent on Linux: the `.deb`'s `postinst` links `/usr/bin/circuitrf` to the GUI binary
and `install.sh` puts `~/.local/bin/circuitrf` on the PATH, so the documented
`circuitrf sparam amp.cnl` **starts the GUI**, which treats `sparam` as a file to open — and, with an
instance already running, forwards it there through the single-instance socket.

**Why five weeks of releases did not notice, which is what §5's gate is for:**
- Every CLI and `serve` gate launches `src/Cli/bin/…/CircuitRF.Cli.dll` — the build tree, never an
  installed artifact.
- `tests/Ui.Tests/PackagingScriptTests.cs` reads the packaging scripts' TEXT. Nothing ever opens a
  built `.dmg`, `.msi`, `.deb` or `.tar.gz` and runs what is inside.
- The user docs say "from a source checkout there is no `circuitrf` on your path yet", which reads as
  though an installed one exists. It was never checked against an install.

---

## R-aut13-1. The `circuitRF` executable IS the CLI — no second executable

**Decided by the owner: the installed CLI adds no meaningful size, and it is called `circuitRF`.**
Both requirements point at one design: the application executable dispatches to the CLI when its first
argument is a verb, and starts the GUI otherwise.

**Why not a second executable.** `circuitRF`, `harmonicaRF` and `wBond` are each a self-contained
single-file publish (`PublishSingleFile`, `SelfContained`): 137–141 MB apiece, each carrying its own
.NET runtime, Avalonia and SkiaSharp. A separate self-contained `circuitrf` would be another copy of
the runtime and of Skia — tens of megabytes per package, ×15 packages — for a program whose code is
`CircuitRF.Cli.dll`, **0.64 MB** (Release). Folded into `circuitRF`, the bundle grows by that DLL and
nothing else, because everything it references (Core, Engine, Design, Render, RfCore, WBond) is
already inside.

**Why a second executable could not be called `circuitRF` anyway.** It would sit in the same folder as
the GUI's `circuitRF.exe` on Windows, and inside the same `Contents/MacOS/` on macOS — and both file
systems are case-insensitive by default, so `circuitrf` and `circuitRF` are one file there.

**What to build:**
- `src/Ui` references `src/Cli`. The direction is legal — the firewall forbids `src/Cli` referencing
  a UI framework, not the reverse — and `tests/Firewall.Tests` must still pass unchanged.
  **Known trap:** a self-contained executable referencing a framework-dependent one fails with
  NETSDK1150/1151. Either split `src/Cli` into a library holding every verb plus a thin `Program.cs`
  project the tests launch, or set `ValidateExecutableReferencesMatchSelfContained=false` on the
  reference. Measure which one builds and publishes cleanly on all three platforms; prefer the split
  if both do, because it states the relationship instead of silencing a check.
- `CliEntry` exposes one public question — `IsVerb(string)` — built from the SAME switch `Run`
  dispatches on, not from a copied list. `src/Ui` never names a verb.
- **The dispatch is the FIRST statement of `Program.Main`** (`src/Ui/Program.cs`, the circuitRF entry
  point only — not `ProgramHarmonica`/`ProgramWBond`), before `CrashReporter.Install`, before
  `AppRelaunch`, before `ReleaseNotesGate.CaptureAtStartup`, before `UpdateStartup.RunBeforeUi`, and
  before any single-instance mutex, lock or socket. Each of those is wrong for a CLI call, and each
  would fail silently:
  - `UpdateStartup.RunBeforeUi` applies a staged update and HANDS THIS PROCESS OVER — by `execv` on
    Linux — so a `circuitrf check` could turn into a GUI launch of the new version.
  - The Windows mutex / Linux lock would make a CLI call made while the GUI is open take the "not
    first" branch and forward its arguments to the window.
  - `CrashReporter` writes the GUI's session file, and a CLI exit is not a GUI session.
  - `ReleaseNotesGate` would record a CLI call as a launch of the application.
  - `ExternalWorkerPolicy.Install` is left out as well: the installed CLI behaves exactly like
    `src/Cli`'s own executable, which installs no consent hook and runs workers (its stated default).
- If `args[0]` is a verb: `Environment.Exit(CliEntry.Run(args))`. **Avalonia is never initialised**
  on that path — no `AppBuilder`, no `NSApplication` on macOS, so no Dock icon and no window.
- No arguments, or a file path, starts the GUI exactly as today. A document opened by double-click
  arrives as a full path (Windows, Linux) or an Apple Event (macOS), never as a bare verb word.
- Add `--version` to the CLI (it has none today): print the application version from the one
  `VERSION` source (`src/Ui/AppVersion.cs`'s route, or the assembly's `InformationalVersion` it
  reads), exit 0. `--version` is a flag, not a verb, so `IsVerb` covers it explicitly. Nothing else
  new: `help` is not added, and a bare `circuitRF` stays the GUI.

## R-aut13-2. Windows — the stub, standard handles, and an interactive console

Two ways reach the CLI on Windows, and both must work:

1. **A program that spawns `circuitRF.exe` with redirected pipes** — every MCP client and every
   agent's shell tool. The per-user installs put the launcher stub (`circuitrf-stub.c`) there, and it
   starts the versioned `app-<version>\circuitRF.exe`. The stub already inherits handles, waits and
   returns the child's exit code, but it:
   - **does not set `STARTF_USESTDHANDLES`.** Set it, with the three handles from `GetStdHandle`, so
     a GUI-subsystem child writes into the caller's pipes. Verify it by capturing `--version` through
     a pipe; do not reason about it.
   - **shows a `MessageBoxW` on every failure.** A modal dialog on a headless agent or CI box hangs
     until someone clicks it, and nobody will. When stdout is a pipe or a file — i.e. not a GUI launch
     — write to stderr only and exit 1. The dialog stays for a launch from a shortcut.
   The per-machine MSI has no stub (`circuitRF.wxs` installs `circuitRF.exe` directly), so this route
   needs nothing there beyond R-aut13-1.

2. **A person typing `circuitrf check .` in cmd or PowerShell.** A GUI-subsystem executable run from
   a console gets no console handles, and neither shell waits for it: the prompt returns at once and
   the output is lost. The standard fix is a **console-subsystem twin, `circuitRF.com`**, beside
   `circuitRF.exe`. `PATHEXT` lists `.COM` before `.EXE`, so a typed `circuitrf` resolves to it, while
   the shortcuts, file associations and a `CreateProcess("circuitrf")` (which appends `.exe`) still
   reach the `.exe`. **Build it from the SAME `circuitrf-stub.c`**, a second time, with the console
   subsystem instead of `-Wl,--subsystem,windows` — one source, as the file's own header insists for
   the three application names. Its size is the stub's: kilobytes.
   - `build-stub.sh` and `build-stub.ps1` both read the built PE's subsystem field back and refuse a
     wrong one. Extend that check to demand 3 (CONSOLE) for the `.com`, exactly as it demands 2 (GUI)
     for the `.exe`.
   - It must work in a per-machine install too, where there is no `current` file: fall back to the
     `circuitRF.exe` beside it.
   - This is our own C in an existing build step, not a new native dependency.

**PATH.** The MSI adds the install folder to PATH: the USER's `PATH` for the `-user.msi` scope and the
system `PATH` for per-machine, removed on uninstall (WiX `Environment`, `Part="last"`). It must be the
stub's folder, which never changes across updates — never an `app-<version>` folder, which does.

## R-aut13-3. macOS and Linux

- **macOS.** `/Applications/circuitRF.app/Contents/MacOS/circuitRF <verb> …` works as soon as
  R-aut13-1 lands; nothing about the bundle changes. The user docs give that full path and the
  optional `ln -s … /usr/local/bin/circuitrf` for someone who wants it on the PATH. **No in-app
  "install command-line tool" command in this brief** — `/usr/local/bin` needs an administrator on a
  fresh machine, and that is a separate decision.
- **Linux.** The `postinst` link and `install.sh`'s `~/.local/bin/circuitrf` already point at the
  application binary, so they become correct with no change. Their names stay lowercase `circuitrf`,
  the spelling every doc uses: Linux is case-sensitive, and the link name is independent of the
  target's.

## R-aut13-4. A long-running `serve` and an update

`serve` can run for hours out of an installation that the GUI then updates. Measure, do not assume,
on each platform:
- **Windows:** the running `serve` holds `app-<old>\circuitRF.exe` open. Update-debris reclaim must
  skip a version folder still in use, as it already must for a GUI instance that is still open.
- **macOS:** the update EXCHANGES the `.app` bundle underneath the running process.
  `docs/design/auto-update.md` §"Never swap mid-session" is why the GUI never does that to itself;
  a `serve` started from the same bundle gets no such protection. Run `serve`, apply an update from
  the GUI, then call a tool `serve` has not called yet — code that has never run is exactly what a
  swap breaks.
- **Linux:** the `current` symlink flips while `serve` runs from the old version directory.

A `serve` that fails there must fail loudly — a JSON-RPC error, then exit — never silently, and its
next launch must start from the new version.

## R-aut13-5. The docs an agent will actually read

- `docs/user/src/reference/cli.md` §Invoking it: where the installed CLI is on each platform (the
  per-user and per-machine paths on Windows, the bundle path on macOS, `/usr/bin` and `~/.local/bin`
  on Linux); keep the source-checkout form as the second case, not the first.
- §`serve`: correct the tool table, which lists thirteen tools and omits `lvs`; and add a client-configuration example — the command is the installed CLI, the arguments are
  `serve --root <dir>`. One generic JSON example, plus a single `claude mcp add` line. Name no other
  product.
- **An "Installing for an agent" section**, the steps an unattended agent can follow:
  1. Pick the asset from the latest release's `update-manifest.json`, or with
     `gh release download --pattern …`. The asset names carry the version, so there is no fixed
     "latest" URL, and the page must say so rather than inventing one.
  2. Install without prompting: `msiexec /i <file>-user.msi /qn` (no administrator);
     `hdiutil attach` + copy the `.app` + `hdiutil detach`; `tar -xzf` + `install.sh`.
  3. Check the install with `circuitrf --version`.
  4. Register `serve --root <dir>` with the client.
  5. **Say that most clients only load a newly registered server at their NEXT session start.** In
     the session that did the install, the agent drives the same verbs through its shell; every MCP
     tool is a verb returning the same JSON (cli.md §11), so nothing is lost by that.
- The repository's `README.md` gets one line pointing at that section.

---

## Gates

**The gate that was missing for 32 releases: each packaging script runs what it built.** After
producing its artifacts, each script runs the CLI OUT OF ITS OWN PUBLISH OUTPUT — the tree that goes
into the installer, not `src/Cli/bin` — and fails the build unless all three pass:
- `<app> --version` prints exactly the `VERSION` file's contents and exits 0;
- `<app> reference --json` exits 0 and parses;
- `<app> serve --root <tmp>` answers an `initialize` request with a result carrying `serverInfo`,
  then `tools/list` naming every tool `ToolCatalog` defines (compared with the catalogue, not a
  count — the user guide still says "thirteen", and there are fourteen since `lvs`), then exits
  cleanly on stdin close.

On Windows the same three run through the stub's pipe route (R-aut13-2 route 1). **The `.com`
cannot be exercised through a pipe:** run it from a real console once by hand at the phase boundary
and record that in `packaging/RESOLVED.md` (create it if none exists) — a pipe-driven test gives it a
pipe, which is the one condition it does not exist for. `PackagingScriptTests` gains a TEXT test that
all three scripts contain the smoke step, which is what keeps it from being deleted quietly.

**Size, measured, not asserted:** record each of the 15 artifacts' size before and after, in
`packaging/RESOLVED.md`. Expected growth is about the size of `CircuitRF.Cli.dll` per artifact, plus
two kilobyte-scale `.com` files in the Windows ones. Anything over 2 MB is a finding to explain, not
a number to accept.

**Dispatch-order gate (`tests/Ui.Tests`):** a source-order test in the style of the comment-stripped
scans elsewhere — in `Program.Main`, the `IsVerb` dispatch precedes `CrashReporter.Install`,
`AppRelaunch`, `ReleaseNotesGate`, `UpdateStartup.RunBeforeUi` and every mutex/lock/socket. This is
the ordering whose violation fails silently (§R-aut13-1).

**Double-click gate — opening a document must be untouched.** What a double-click passes is fixed by
each platform: Windows' associations pass `"%1"` to `circuitRF.exe` (`circuitRF.wxs`, one `open` verb
per registered type, `TargetFile="CircuitRfExe"` — never the `.com`), Linux passes `%F`
(`circuitrf.desktop`), and macOS passes no argument at all (an Apple Event, `App.OnActivated`).
Assert that `IsVerb` is false for every shape those deliver: a full path to each registered document
type, on both path syntaxes, including a file literally named after a verb (`…/check`, `C:\x\sparam`)
— the comparison is against the WHOLE first argument, never its file name. Assert, as a text test,
that every `open` verb in `circuitRF.wxs` still targets the `.exe` and the `.desktop` `Exec=` line
still ends in `%F`. By hand, at the phase boundary, on each platform: double-click a `.csch` and a
`.clay` with circuitRF closed and again with it open, and record in `packaging/RESOLVED.md` that
both opened in the GUI (the second in the window already open).

**Parity gate:** `circuitRF check <fixture> --json` run through the application executable is
byte-identical to the same call through `CircuitRF.Cli.dll`. That is the existing byte-identity
pattern of `RenderCliVerbTests` and `EmCliVerbTests`, applied to the new route.

**Out of scope, deliberately:** an MCP-registry listing, an in-app command-line-tool installer on
macOS, and CLI dispatch in `harmonicaRF`/`wBond`.

## On completion

Write the findings to the relevant `RESOLVED.md` (`packaging/RESOLVED.md`, `src/Cli/RESOLVED.md`);
**never to a `CLAUDE.md`.** Update `docs/design/cli.md` (the installed forms, and the dispatch) and
the user docs sources named in R-aut13-5 — **do not regenerate the user docs**; the owner does that at
the end of a series. See `brief-automation-7-mcp-hardening.md` §7 for the series-level completion
rules.
