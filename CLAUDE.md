# circuitRF

Lightweight cross-platform RF circuit simulator (DC, S-parameters, harmonic balance,
loadpull/sourcepull). **NOT a SPICE simulator.** See `docs/PRD.md` for scope, the five
hero circuits, and non-goals. This file is standing project memory — keep it current.

## Searching this repo

**Use `grep` (or ripgrep) directly whenever possible instead of spawning a search agent.** This
repo's structure is well-known and its files are plain text — a targeted `grep -n` finds a
symbol, class, or XAML control faster and far cheaper than delegating a "find X" task to an
agent. Reach for an agent only when the search genuinely needs multi-step reasoning across many
unrelated locations, not for straightforward lookups.

## Resolving Issues
If significant findings were found during bug fixes or changes, write to the relevant RESOLVED.md 
files never CLAUDE.md. This helps keep the CLAUDE.md files small.

Never quote the owner or users in git commit comments, RESOLVED.md files or anywhere in the code.
Instead, briefly paraphrase owner/user messages. Pre-existing quotes are ok.

## Stack
- .NET 10 (LTS), C# 14
- Avalonia 12 (UI), SkiaSharp (canvas rendering), CommunityToolkit.MVVM (MVVM)
- CSparse.NET (sparse complex LU for large MNA), NumFlat (dense linear algebra)
- `RfCore` (Touchstone I/O, network params, the `DataSet`/`DataCube` result types, interpolation,
  renormalization, plotting) is **a first-class project of this repository, exactly like `Core`,
  `Engine`, and `Ui`** — `src/RfCore/` with its tests at `tests/RfCore.Tests/`, both listed in
  `circuitrf.slnx`, referenced via ordinary `ProjectReference`.

  **It is NOT a subtree, and there is nothing left to "un-subtree" (2026-07-30).** It arrived via
  `git subtree add` on 2026-07-29 purely to preserve history
  (brief-housekeeping-tearoff-palette-repo.md §6 — splotRF, the other consumer of the standalone
  RfCore repo, was being retired). **"Being a subtree" is not a persistent state in git**: there is
  no `.gitmodules`, no config, no live link to anything. RfCore's 24 original commits are a
  permanent *second parent* of merge `0bd04db`, and `git blame` on any file under `src/RfCore/`
  still resolves to its original author and date. The only residue is a three-line
  `git-subtree-dir/-mainline/-split` trailer in that one old commit message, which is inert unless
  someone runs `git subtree pull` — **so don't.** Treat `src/RfCore/` as ordinary first-party code.

  *Known git wrinkle, not a history loss:* `git log --follow <path>` does not cross the merge (a
  documented `--follow`-vs-merges limitation). `git blame` does. To read the pre-merge history
  directly: `git log 0bd04db^2 -- src/Data/DataCube.cs` (the *old* path, on the pre-merge parent).

  **The architectural boundary is unchanged and does not depend on directory placement** — it is
  enforced by assembly-reference checks in `tests/Firewall.Tests`, which is why moving RfCore under
  `src/` cost nothing. RfCore still references no UI framework, and nothing in it may.

## Build / test / run
- Build:   `dotnet build`
- Test:    `dotnet test`
- Run CLI: `dotnet run --project src/Cli -- <args>`
  Verbs: `sparam`, `dc`, **`hb`**, **`lp`**, **`lpp`**, **`em`**, **`convert`**, **`new`**,
  **`import`**, **`check`**, **`explain`**, **`history`**, **`render`**, **`netlist`**, **`plot`**,
  **`find`**, **`rail`**, `elab`. **The CLI has its own design doc —
  `docs/design/cli.md`** — covering the five-step anatomy of a run verb, the stdout/stderr split, and
  the rules below; read it before adding a verb. `hb`/`lp`/`lpp` run the netlist's harmonic-balance,
  loadpull and loadpull-pursuit analyses, and each runs the whole sweep when a `parametric_sweep`
  wraps it (naming the inner analysis is promoted to its wrapper, since running the inner alone
  silently drops the sweep axis — a freq-swept loadpull is exactly this shape). They evaluate the
  TestBench's `measure` lines exactly as the GUI does, so a `.cnl` that works headless works when
  opened. `--set var=expr` overrides a global before elaboration; **every other override replaces the
  DIRECTIVE in the TestBench**, because the sweep engine re-resolves it at each point and an override
  handed to one engine instance is discarded after the first. `-o out.{mat,npy,txt}` exports, and `lp`
  also writes `.spl`/`.lpcwave` — the loadpull interchange the Data Display reads back.
  **`em` takes a `.cem` and needs no other arguments** (2026-08-26). Both references it must follow
  are WALK-UPS, not flags: the layout is relative to the nearest ancestor `.cws` above the `.cem`
  (falling back to the `.cem`'s own directory), and the technology resolves against the LAYOUT's own
  parent workspace. It writes exactly where Simulate writes — `EmRunService.ResolveSnpPath` is
  predictable by design so a schematic's SnP reference survives a re-run — and `-o` moves the
  Touchstone only, never the `.npy` that carries the diagnostics group. A refusal stays a refusal:
  `Refused`/`NoLayout`/`EngineError` exit 1 with the run service's own sentence, `Cancelled` exits 130.
  The gate is `tests/Ui.Tests/Em/EmCliVerbTests.cs`, which compares the CLI's `.sNp` **byte for byte**
  against `EmRunService.Run`'s — every line but the provenance write-timestamp, which the file carries
  by design; the `.npy` matches with no exception.
  **`convert` is one import and one export** (2026-09): every ordered pair of `clay`/`gdsii`/`dxf`/
  `gerber`/`board` works because every reader lands on a cell folder plus a technology and every
  writer starts from one — a `.clay` target stops after the import, anything else exports out of a
  scratch directory (`--keep-cells` keeps it). **A `clay` target is a DIRECTORY; a file-shaped path is
  a refusal naming the directory spelling** (AUT-12). Formats infer from the paths (a FOLDER is a
  Gerber file set; an unknown extension is classified by CONTENT through the import's own classifier); `--from`/
  `--to` override. Two things the GUI asks in a dialog: the layer mapping takes the same default the
  dialog pre-selects, and **an unstated Excellon coordinate format is a REFUSAL, never a guess** —
  leading vs trailing suppression differ by four orders of magnitude on identical text, so it prints
  the inference and the flags that answer it. Headless there is no open workspace to graft layers
  onto, so it writes a `.ctech` of its own; **passing a null destination technology to any importer
  silently drops every layer** — `src/Design/RESOLVED.md` records why. Gate:
  `tests/Ui.Tests/ConvertCliVerbTests.cs` (all 24 pairs, plus byte identity against the in-process
  `GdsiiExport`/`GerberExport` the GUI's own File ▸ Export calls).
  **`new workspace <dir>`, `new cell <ws> <name>` and `import part <file> --into <dir>` create a
  correct INITIAL document headlessly** (2026-09-05) — and **none of them contains any creation
  logic**: each calls the one function the GUI's own command calls (`WorkspaceCreate.Create`,
  `CellCreate`, `ComponentImport.Import`, all in `src/Design`). An operation that lives only in a
  view model is not a capability, and a verb that re-implements one diverges from it silently — so
  the extraction is the feature and `src/Cli/Authoring.cs` is only argument parsing, refusals and
  reporting. `new` is ONE verb with a noun, not three; `new schematic` would be a fourth noun, never
  a fourth verb. **There are deliberately no per-primitive edit verbs** — no `place-instance`, no
  `set-parameter`: once a document exists, the way to change it is to WRITE it, because the format
  is the contract. **Every default is the GUI dialog's** — `--tech` opens on what the New Workspace
  combobox opens on (`--tech none` is its "None" row, and an unknown id is a refusal listing the
  real ones, never a fallback), `--views` defaults to `schematic` because that is what New Cell
  creates, and the layer mapping takes the same default `convert`'s does. Anything the dialog would
  have ASKED is a refusal naming the flag that answers it: a source holding several parts prints
  them and stops (`--cell` / `--variant` / `--list-parts`). `import part` **reports the layers its
  technology lacks and writes none** unless `--add-layers` says so — the GUI's own install is
  session-only and writes nothing to disk either. The paths created ARE the result, on stdout and in
  `--json`. Gate: `tests/Ui.Tests/AuthoringCliVerbTests.cs` — the CLI as a process, byte for byte
  against the in-process call, a comment-stripped source scan proving the view model kept no second
  copy, and the end-to-end that authors a design and simulates it with no display at any step.
  `ShippedTechnologies` and its `.ctech` resources moved to `src/Design` for this; moving such a
  class without its `EmbeddedResource` items leaves it enumerating nothing, silently.
  **`check <path>` and `explain <path>` are read-only, and neither runs an analysis**
  (2026-09-05). `check` answers "is it well formed, does it resolve, is it sound" over a
  workspace, a cell folder or one document — the kind inferred from the path exactly as `convert`
  infers a format, and a foreign file classified through `convert`'s OWN classifier so a GDSII or
  Gerber file is named rather than called unreadable. **It writes NO validation logic of its
  own**: every finding comes from a validator the GUI already uses (`CellViewFileValidator`,
  `CellFolder.ResolvePrimary`, `NameValidator`, `TechValidation`, `TechnologyResolver`,
  `EmSetupResolver`, `CellSymbolResolver`, `NetExtractor`, `Elaborator`, `ChainSelector`,
  `DrcPredicateParser`, `DrcEngine`) — a rule living only in `check` is a rule the application
  does not enforce, so a design would pass headlessly and be refused when someone opened it. Exit
  0 unless something at or above `--severity` (default `error`) was found; warnings are ALWAYS
  reported and still exit 0, because a check that hid them to keep the exit code clean makes the
  exit code useless. **It writes nothing at all**, so it runs on a read-only tree and on a
  workspace another process has open. `explain` answers the question that is not a failure — *what
  did circuitRF decide* — and reports the WALK as well as the answer, because resolution here is a
  series of walk-ups and which one produced an answer is the part a caller cannot otherwise see (a
  `.cem`'s layout and technology walks start from different files and can land on different
  workspaces). `--expr` evaluates in the design's own resolved scope through the one expression
  engine, with `--set` applied first exactly as a run verb applies it; `--analysis` reports every
  declared chain, which one dispatches and for which verb, and whether a named inner analysis
  would be promoted; `--ref` resolves a relative reference and says whether it leaves the
  workspace. **A sweep is reported in base SI WITH its unit and its scale** — reading a mark
  without its scale once produced a run at 2 Hz that looked entirely normal. **`--extents` also emits
  a `window` string in the spelling `render --window` takes** (AUT-12). **A `.csch` reaches
  the elaborator through the `.cnl`**, in memory, because that is what the GUI's own Simulate does
  and the two readers disagree about bare words: skipping the round trip reports errors the
  application does not have. The **DRC engine and the `.wasm` rule model moved to `src/Design`**
  to make the layout half of `check` possible. Gate:
  `tests/Ui.Tests/CheckAndExplainCliVerbTests.cs`.
  **`history` is ONE verb with nouns, and every revision-control operation has a spelling on it**
  (`docs/design/revision-control.md` §5.3d) — `checkpoint`, `list`, `restore`, `commit`, `versions`,
  and RC-9's `clone`, `pins`, `pin`, `unpin`, `fetch`, `send`. It exists because the governing motive
  of that whole feature is a floor under an AI-authored edit and **the agent is out of process**: a
  safety net reachable only from a window is worth nothing to it. Each noun calls the `src/Design`
  function the GUI's own command calls and holds no logic of its own — the rule `Authoring.cs` already
  states, and a comment-stripped source scan is what holds it. **The batch's open and close are
  deliberately NOT here**: they carry session state a process that exits after one command cannot, so
  they stay on `serve`; `history checkpoint --intent` IS what a batch's open takes.
  **`clone` derives nothing** — both the address and the destination folder are required, because git
  would work a folder name out of the address and a folder appearing somewhere the caller did not name
  is a surprise nobody is there to notice on a build machine. **`pins`/`pin` name an ALIAS and there is
  no per-cell spelling**: one referenced workspace is one repository with one commit identity, and
  pinning per cell would let one design reference two mutually inconsistent versions of one library.
  `pins` exits 1 on a pin that cannot be honoured — a build machine treating that as a warning would
  produce a result against content the design was never verified against, which is the one outcome the
  pin exists to prevent. **Nothing reaches a network without being asked** (`fetch`/`send` only), and
  **circuitRF holds no credential and asks for none**: it uses whatever git is already configured with,
  and `GIT_TERMINAL_PROMPT=0` turns an operation that would have asked into a refusal rather than a
  process that never returns. `pins`/`pin`/`unpin` need no repository — the pin lives in the consuming
  design's `.cws`, which is very often a workspace circuitRF has never kept a history for. Gates:
  `tests/Ui.Tests/Revision/CloneAndPinsTests.cs` and the RC-5/RC-7 files beside it.
  **`render <path> -o out.{svg,pdf,png}` turns a `.csch`, `.csym` or `.clay` into a picture, and it
  owns no rendering** (2026-09-07) — every pixel comes out of the three Skia renderers in
  `CircuitRF.Render` that the application draws each frame with, which is the whole reason RND-1 put
  that project below the firewall. `src/Cli/Render.cs` is argument parsing, viewport arithmetic,
  refusals and reporting, on `Authoring.cs`' terms. **One verb over every document kind**, inferred
  through `DocumentKinds.Classify` exactly as `check` infers it; a cell folder takes `--view` (more
  than one view is a refusal LISTING them), a workspace takes `--cell`, and **a document belonging to
  no workspace is a first-class input** — it resolves no technology, renders on the fallback palette
  as the layout editor does, and says so as a NOTE rather than a warning. `-o` is required and its
  extension picks the format: **there is no picture on stdout**, because stdout is the result document
  and `--json` has to co-exist with the write. `--fit` (the default), `--window` and `--center/--span`
  are **refused together rather than ordered**, and **on a layout every coordinate carries an SI unit —
  a bare number is a refusal**, because `--window 0,0,500,300` could mean DBU, µm or mm and all three
  are plausible. A window of a different aspect is **letterboxed, never cropped**. `--detail full`
  (the default) turns every LOD tier off so what is STORED is what is drawn; `--detail screen` engages
  them as a canvas would, and on a real board that is the difference between a **23.5 MB SVG in 1.44 s
  and a 6.2 MB one in 0.36 s**. `--layers`/`--hide-layers` apply to a **CLONE** of the resolved
  technology (`TechnologyLayerSelection`, in `src/Design`) — `TechnologyCache` hands back a shared
  instance and mutating it would narrow every later render in the same process, which is the defect
  that only appears on the second call. **`--fit-layers` frames the fit on some layers and draws all
  of them; `--layer-colors name=#rrggbb[aa]` recolours one for this render only — eight digits sets
  FILL OPACITY, the only alpha the renderer reads** (AUT-12). Rulers stay ON (document content, not
  overlay state); every other overlay is off by construction. Progress and cancellation ride `RunHost`'s `RunControl` like
  `em`'s, so a cancelled render exits 130 and **writes nothing**. Gate:
  `tests/Ui.Tests/Render/RenderCliVerbTests.cs`, which compares the verb run as a PROCESS against the
  in-process `CircuitRF.Render` call **byte for byte** — schematic, symbol and layout, SVG and PDF,
  and all four came back identical with no exclusion at all.
  **`netlist <path.csch> [-o out.cnl]` writes the extraction Simulate performs, and every run verb
  now takes a `.csch` too** (2026-09-08) — until then nothing headless could simulate a design anyone
  had DRAWN: a run handed the JSON to `CnlReader`, which reported its first key as a missing cell
  name. Both halves go through `CircuitSource.CnlTextOf`, so the file written is the same BYTES a run
  consumes; the provenance comment is a constant for that reason, and the gate scans `src/Cli` for a
  second `CnlWriter.Write` — the WRITER, not `NetExtractor.Extract`, which `check` calls on its own
  account. Any other document kind is a refusal BY KIND.
  **`plot <result> -o out.svg --trace cube=S,i=2,j=1,y=db` is one picture with no `.cdd` to author
  first**, and not a second plotting path: it builds the document `render --data` consumes and hands
  it to the same composer (`--write-cdd` gives that document back). `cube=` is the trace card's own
  shorthand. **An integer on an `i`/`j` axis is a 1-based PORT NUMBER, not an index** — converting it
  draws S12 for `i=2,j=1` in silence, which is invisible on a reciprocal part.
  **`find <root> [--depth n]` says what is here** — workspaces, cells, views, declared analyses. The
  walk is bounded and **says when it stopped short**; a directory symlink is never followed. Gate for
  all three: `tests/Ui.Tests/Cli/MissingVerbsCliTests.cs`; detail in `src/Cli/RESOLVED.md` and
  `cli.md` §14-16. `new workspace` also creates missing PARENT directories now.
- Package: **exactly one script per platform, and each builds everything that platform ships** —
  `packaging/windows/build-windows.ps1` (9 files: `.msi` x64/arm64/x86 in both install scopes, plus
  the `.zip` the updater fetches), `packaging/macos/build-macos.sh` (2 `.dmg`s, both architectures;
  wraps the existing `src/Ui/bundleFor*MacOS.sh`), `packaging/linux/build-linux.sh` (4 files: `.deb`
  and `.tar.gz` for x64/arm64; `fpm` is needed only for the `.deb`). All write to `dist/`. Flags
  narrow a run; **the no-argument form is the release form**, because 1.0.0-beta.2 shipped 7 of its
  15 artifacts when Windows defaulted to one architecture in one scope and Linux was two scripts —
  silently, since a missing update payload stops updates with no error. Held by
  `tests/Ui.Tests/PackagingScriptTests.cs`.
  **Each must run ON its own platform** (WiX is Windows-only, `codesign`/`hdiutil` macOS-only, and
  the Windows PE icon is only embedded when the publish happens on Windows). Step-by-step
  instructions live in `BUILDING.md`, which `README.md` links to; keep the two in step.
  App icons (`.icns`/`.ico`/`.png`) are **build products** rasterised from the committed brand SVGs
  by `dotnet run --project tools/IconGen`, which every packaging script runs first — no icon binary
  is ever committed.

  **Two packaging rules exist because breaking either fails silently** (both held by
  `tests/Ui.Tests/PackagingScriptTests.cs`, both learned from a real Windows build, 2026-08-18):
  - **Every `.ps1` under `packaging/` must be pure ASCII.** Windows PowerShell 5.1 reads a BOM-less
    `.ps1` as cp1252, so a UTF-8 emoji or box-drawing char decodes to bytes 0x93/0x94 — the curly
    quotes `“ ”`, which PowerShell honours as string delimiters. Nothing errors: the parser swallows
    everything to the next quote-class byte, PRINTS it instead of running it, and continues. One `📦`
    turned the whole `dotnet publish` block into a string literal (verified against the AST, lines
    48-54), and the first visible symptom was a `Get-ChildItem` "cannot find path …\publish\win-x64"
    from a *later* step. A BOM also fixes it and is the wrong fix — invisible, and it does not
    survive an editor round-trip anyone would notice.
  - **What ships is named after the APPLICATION, not the assembly**: `circuitRF(.exe)`,
    `harmonicaRF(.exe)`, `wBond(.exe)`. The assembly stays `CircuitRF.Ui` (RfCore's
    `InternalsVisibleTo` — WB40), and .NET names the published host after the assembly with no
    property to separate them, so `src/Ui/CircuitRF.Ui.csproj`'s `CrfRenameApphost` target renames it
    **after publish only** — a plain `dotnet build`/`dotnet run` is untouched. Five packaging files
    repeat that name as a literal and must be changed together (the `.wxs` + `build-windows.ps1`, the
    Debian `postinst` + `.desktop`, the three `bundleFor*MacOS.sh` + their `Info.plist`s).
- **The version number is written in exactly one place: the repo-root `VERSION` file** (one line,
  e.g. `0.9.0-beta.1`). `Directory.Build.props` reads it into every assembly's
  `Version`/`InformationalVersion` — which is what the About box renders via `src/Ui/AppVersion.cs`
  — and `packaging/version.{sh,ps1}` derive from it the installer file names, the MSI
  ProductVersion, the stamped `CFBundleShortVersionString`/`CFBundleVersion`, and dpkg's `~`
  spelling. Nothing is generated or rewritten; the version strings in `Assets/macOS/*.plist` are
  placeholders the bundle scripts overwrite. **Never hard-code a version anywhere else** — three
  copies had already drifted (About said 0.9.0, the plists 0.1.0, the assembly the 1.0.0 default),
  which is what `tests/Ui.Tests/VersionSingleSourceTests.cs` now holds shut.

### Run the test suite ONCE. Read the TRX for failures — never re-run to find out what broke

**Owner instruction, given three times (2026-08-25).** A full `dotnet test` is ~7 minutes of wall
clock. Running it a second time because the first run's console tail scrolled past the failure is
pure waste, and it is never necessary: **every project writes
`tests/<Project>.Tests/TestResults/last-run.trx` on every run**, and that file holds each test's
name, outcome, duration, failure message, stack trace and captured stdout.

- **`dotnet test`'s console tail only shows the LAST project to finish.** With seven test projects
  running concurrently, a failure in an early-finishing one (`Core.Tests` is 1 s; `Engine.Tests` is
  5+ min) is scrolled off. That is what makes the "just run it again" reflex so tempting — and the
  console is simply the wrong place to look.
- **Get the per-project verdicts in the same run** with `dotnet test 2>&1 | grep -E "^Passed!|^Failed!"`,
  which prints one line per project.
- **Get the failure detail from the TRX**, after the fact, for free:

  ```bash
  python3 - <<'EOF'
  import re, glob
  for f in glob.glob('tests/*/TestResults/last-run.trx'):
      x = open(f, encoding='utf-8', errors='replace').read()
      for m in re.finditer(r'testName="([^"]+)"[^>]*outcome="Failed"', x):
          print(f, m.group(1))
      for m in re.finditer(r'outcome="Failed".*?<Message>(.*?)</Message>', x, re.S):
          print(m.group(1)[:500])
  EOF
  ```

- The same file carries `<StdOut>`, so **a diagnostic `ITestOutputHelper` line is readable without
  re-running** — which is the cheap way to probe a failure, rather than adding a print and running
  the suite again.
- Corollary: **scope the run before starting it**, per the section below — `dotnet test tests/Ui.Tests`
  for layout/UI work is ~40 s against ~7 min for the full solution.

**A failure in code you did not touch: attribute it before re-running anything.** The question is
"is it mine?", not "is it real?" — and **never `git stash` your work to run a clean-tree baseline**
(that is the full run again, plus a restore, plus re-verifying the restored tree, with a
half-finished change at risk). Three checks, in cost order:

1. **`git status --porcelain`** — did the change touch that path at all? Usually settles it.
2. **Read the test's own comment.** Several here document themselves as load-dependent.
3. **Run that one test alone** — `dotnet test tests/Core.Tests --no-build --filter
   "FullyQualifiedName~<Name>"` is well under a second. Fails in isolation → deterministic → probably
   yours. Passes → load-dependent → consistent with a known race, and NOT evidence you caused it.
   (An isolated pass still never proves a race *absent* — it only separates "deterministic break"
   from "load-dependent".)

Then report it and move on: "failed, I touched nothing on that path, here is the evidence" is a
complete answer. Whether a load-dependent test has genuinely regressed is a question about the repo,
not about your change — ask the owner rather than spending minutes of machine time on it unasked.


**Plain `dotnet test`, with no flags, is the routine gate — but it takes a long time to run so use it
only when absolutely nessesary.  Even `dotnet test tests/Ui.Tests` can take a long time to run (> 7 min)
** Repo-root `circuitrf.runsettings` (`TestCaseFilter: Category!=Benchmark`) is wired in via
`Directory.Build.props`'s `RunSettingsFilePath`, so every invocation — `dotnet test` at the root,
`dotnet test tests/Ui.Tests`, an IDE test run, CI — inherits the exclusion automatically. There is
nothing to type and nothing to forget. This supersedes the prior two-tag, filter-must-be-typed schemes
from brief-benchmark-gate-split.md and brief-test-suite-fast-loop.md: `Category=Nightly` is retired
and `Category=Slow` is gone as a category (its former members are either untagged, having been
measured under the threshold, or folded into `Benchmark`).

- **Repo root, no flags: 7,482 tests in ~4 min** (measured 2026-08-06). This is what "build+test
  green" means in every brief from here on, unless that brief's own text says otherwise.
  **`Engine.Tests` is ~3 min 24 s of that on its own** — measured alone, with `--no-build`, so it is
  the suite's own cost and not parallel contention. It grew there gradually across the L8/L9
  electromagnetic phases (65 s at L9b, ~2 min at the ground-via work) and no single test crosses the
  ~5 s `Category=Benchmark` threshold; ~1,000 tests averaging ~0.2 s each simply add up. **The
  earlier "5,169 tests in ~30 s" figure recorded here was stale by roughly an order of magnitude** —
  do not quote it. The other projects are still genuinely fast on their own: `Ui.Tests` ~27 s
  (5,075 tests), `Core.Tests` ~1 s, `RfCore.Tests` ~6 s, `Firewall.Tests` instant, so **scope the
  gate to the projects your change can reach** and keep the full-solution run for phase boundaries.
- **`RfCore.Tests` IS in `circuitrf.slnx` and IS covered by a plain `dotnet test`** (2026-07-30; it was
  not, until then — an older note in `src/Ui/CLAUDE.md` says otherwise and is marked superseded). 281
  routine tests, ~4 s. Its proprietary loadpull fixtures are git-ignored, so on a fresh clone 56 of them
  report **Skipped with a reason** via `FixtureFact`/`FixtureTheory` rather than failing — do not
  "repair" those skips by committing lab data.
- **`Category=Benchmark`** is the *only* opt-in tag. Applied mechanically wherever a test's measured
  wall-clock exceeds ~5 s — and, since 2026-07-30, also to a test that is *fast but wall-clock-sensitive*
  and therefore cannot survive the parallel-start burst of a full-solution run (`RfCore.Tests`'
  `Rbf2DPerfTests`, 4 methods: millisecond-fast, but a ~0.3 ms operation reads ~10 ms per sample under
  full-suite load, so even a best-of-20 gate flaked). **Do not untag those on the grounds that they run
  quickly** — they are tagged for the purpose the mechanism serves, not the letter of the ~5 s rule.
  Currently **128 test methods** repo-wide, counted rather than estimated (97 in `Engine.Tests` — CL4's
  `PlanarLossyGroundTests` are the last 4, 5 m 03 s together; CL1's
  two A-vs-B measurements are the last 2, 1 m 40 s together; 24 in `Ui.Tests`, 6 in
  `Harmonica.Tests`, 1 in `RfCore.Tests`); `brief-em-sweep-performance`'s own
  milestones account for much of the growth past the ~81 recorded below, and M5's accelerator adds the
  last 5 (`AimAccuracyTests`, 5.8 min) — the earlier count of 74 omitted `Harmonica.Tests`'
  own tier entirely; H6 added `InverseSolveCostTests` (3 methods, ~5 s) and
  `HarmonicaDragCostTests` (1 method, ~2 s), and H7 added `HarmonicaGridDragCostTests` (1 method,
  the 61-point grid measurement) and `HarmonicaTestbenchCliTests` (1 method, which launches the real
  `Cli hb` process) — **~5 s together**. The L8/L9 full-wave phases are where nearly all of them came from, because a
  single de-embedded full-wave point costs ~48 s one level and 71.9 s two (and **149.9 s** at the
  two-level-with-vias mesh L9's own phase gate runs on, N = 1,023), so none of those measurements can
  live in the routine tier. **L9's phase gate added 2** (`L9PhaseGateTests.Gate1` 5 m 28 s and
  `Gate2` 6 m 29 s, **11.97 min together, measured alone** — the via-carries-current comparison and the
  two-level degeneracy; their routine counterparts, the three `Gate3Wiring_…` tests, stay in the
  default gate at ~25 ms). **L9e added 7** (`ViaPhysicsTests.T3_1` 54 s — the ℓ/w
  error curve and its convergence sweeps; `AdaptiveSweepTests.T1_2` 16 s / `T4_1` and `T4_2` ~3-4 min
  each — the tolerance curve, the sweep-time measurement and D3's interpolant comparison;
  `PlanarBudgetTests.T4_3` 68 s / `T4_4` ~1 min / `T5_1` 6 s — the run-level memory arithmetic, its
  working-set cross-check and ACA's compression measurement), bringing the opt-in tier to roughly
  40 minutes in total. **The via z-integral follow-up added 3** (`ViaPhysicsTests.T3_1b` 24 s and
  `T3_1c` 1 m 37 s — the subdivision-invariance ladder and the n_z convergence table; `M1_1` 23 s —
  the cost measurement that decided the design), and **re-pointed `T3_1`** from measuring the midpoint
  rule's error to gating the fill's, at 16 s instead of 54 s. **Net ~+2 min.** The older, named ones are: the L8 phase gate
  (`L8PhaseGateTests.Gate1/Gate2/Gate3` × 2 starters, plus
  `EmAcceptanceBudgetTests.R18_WhatTheUserWaitsForAfterSimulate_AtTheShippingMesh`, ~8.5 min together
  — its routine counterpart `Gate3Wiring` stays in the default gate at 2.5 s); the
  500,000-shape `LayoutPerf` TIMED sweeps
  (`LayoutPerformanceBaselineTests.Baseline_500k`/`Baseline_50k` + `R8bCrossoverExperiment`,
  `LayoutLodMergeCacheBenchmarkTests.{LodOnly,Final}_FullExtent_500k` +
  `PathCache_500k_MemoryStaysUnderCap_TimeAndMemoryReported`,
  `LayoutSpatialIndexPerfTests.BulkLoad_500k_BuildTimeRecorded`,
  `LayoutInstanceArrayPerfTests`'s 500k case) plus the handful of `Engine.Tests` loadpull/pursuit
  methods whose individual runtime crosses the threshold (most loadpull/pursuit tests do not and stay
  untagged and routine).
- **Opt in with `dotnet test --settings circuitrf.benchmark.runsettings`, not `--filter`.** This
  SDK's VSTest version ANDs a command-line `--filter` with the project's own `TestCaseFilter` rather
  than overriding it, so `--filter "Category=Benchmark"` resolves to the impossible AND of
  `Category!=Benchmark` and `Category=Benchmark` and silently matches nothing — verified directly, not
  assumed. Passing `--settings` on the command line does override the project-level
  `RunSettingsFilePath` cleanly, so `circuitrf.benchmark.runsettings` (`TestCaseFilter:
  Category=Benchmark`) is the actual one-liner opt-in path. Run it (~5 min) when touching rendering,
  the spatial index, the path/instance caches, or LOD, and at any performance-phase boundary.
- **500k's COUNTER coverage stays in the default gate**, at negligible cost (~5 s total) — this is the
  part that actually catches an algorithmic regression (an accidental O(n)/O(n²) scan that bypasses
  the spatial index): `LayoutSpatialIndexPerfTests.Gated500k_CullingCountersStayCorrect` (one shared
  500k layout PER PROFILE, reused across a full-extent AND a zoomed-in assertion — no timing, no
  warm-up sweep). Verified to actually catch a regression, not just assumed: temporarily disabling the
  spatial-index culling query in `LayoutRenderer.Draw` turns this test red immediately.
- **Tagging a new slow test:** measure it (a TRX run reports per-test duration); if it is at or above
  ~5 s, add `[Trait("Category", "Benchmark")]`. Below that, leave it untagged — it belongs in the
  default gate. A `[Theory]`'s `InlineData` cases can't be tagged individually, so a mixed-cost Theory
  (e.g. `LayoutPerformanceBaselineTests`'s former combined `Baseline`) should be split into separate
  `[Theory]` methods by cost tier so only the slow tier carries the tag.

**Deferred, on purpose, and it must stay visible rather than quietly becoming permanent:** §5.1's 500k
**timing** target is unmet and lives only in `Category=Benchmark` now. L2c's own measured shortfall
(13-15× over the 50 ms floor at full extent) is the reason — closing it needs the tiled raster cache
(L2d), not more per-shape optimization (see L2c's own completion note above). **Re-enabling routine
500k timing coverage is part of L2d's own gate**, when that phase lands; until then,
`Category=Benchmark` via `--settings circuitrf.benchmark.runsettings` is how anyone actively working on
performance checks it.

**Layout/UI work** — the only projects layout work can plausibly touch or break (every layout brief since
L0a carries the guardrail "don't touch `src/Core`, `src/Engine`, `RfCore`"):
```
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```
Run as two commands — this SDK's `dotnet test` rejects more than one explicit project path in a single
invocation (`MSB1008: Only one project can be specified`).

**The full unfiltered suite still exists** (bypass the default filter with an empty override
`--settings` file, or `--filter "Category=Benchmark|Category!=Benchmark"`) — reach for it only at
genuine phase boundaries, or whenever the complete picture (including the 500k timing sweep) is
actually wanted. It is not what routine `dotnet test` runs, and does not need to be.

Moving `Benchmark` tests to a separate runner outside `dotnet test` discovery entirely (so they
wouldn't even need an opt-in filter) was considered and not done — restructuring the ~19 tagged methods
across 3 files into a standalone project/entry point is more than the brief's "stop and report if not
cheap" threshold, and the `--settings` opt-in already satisfies the brief's gates without it.

Add `--no-build` after the first build of a session.

## Architecture — three layers, kept separate
1. **Design layer** (`src/Core`): Cells (Symbol/Schematic/Layout views), instances, nets,
   parameters, libraries — editable, serialized, human-readable. Layout view is a v1 placeholder.
2. **Elaboration layer** (`src/Core`): flatten hierarchy, resolve parameters/sweeps top-down,
   number nodes → an *elaborated netlist*. This is what the engine consumes.
3. **Numeric layer** (`src/Engine`): matrices, unknown vectors, the `DataSet`/`DataCube` result
   model. No UI, no domain types.

Source map: `src/Core` (layers 1–2 + the expression engine), `src/Engine` (layer 3 + analyses),
`src/RfCore` (Touchstone I/O, network params, `DataSet`/`DataCube`, `.npy` export), `src/Design`
(the design-layer DOCUMENT artifacts — the layout model and `.clay` reader, the technology model and
`.ctech` reader, the `.ccell` cell-folder format, the `.cem` EM setup and the extractors that turn
geometry + stackup into an `EmProblem`, **`Layout/Interchange/` — every GDSII, DXF, Gerber, Excellon
and `.kicad_pcb` reader and writer**, and **the functions that CREATE those artifacts** —
`WorkspaceCreate`, `CellCreate`, `ComponentImport` — which the GUI's own New Workspace / New Cell /
Import Component call, not a headless copy of them, and **`Layout/Drc/` + `Layout/Assembly/` — the
DRC engine and the `.wasm` assembly rule model**, so design rules run with no display),
**`src/Render`** (the Skia RENDERERS — `SchematicRenderer`, `SymbolEditorRenderer`,
`LayoutRenderer` and its partials, `WBondRenderer`, their themes and caches, the colour-theme model
and its `.ccolor` reader, the hit-test/handle/snap/overlay geometry they share with the editors,
and `SvgFontNormalizer` — the repair every emitted SVG passes through on the way out of Skia's SVG
device;
below the firewall since 2026-09-07 and referenced by BOTH `src/Ui` and `src/Cli`, so a headless
picture is drawn by the code the GUI draws with rather than by a second renderer that would drift
invisibly),
`src/Ui` (Avalonia), `src/Cli` (headless driver +
test harness). `RfCore` is an ordinary first-party project alongside the rest — see §Stack for why it is
no longer at the repo root, and why that changed nothing architecturally.

**`src/Design` is not a second `src/Core`.** Core is the CIRCUIT design layer — cells as netlists,
parameters, expressions, elaboration; it knows nothing about DBU, stackups or drawing layers. Design
is the artwork side, carved out of `CircuitRF.Ui` in 2026-08 so `src/Cli` could run an EM setup
without pulling Avalonia across the firewall (`docs/sonnet-briefs/brief-cli-em-verb.md`). It draws
nothing, docks nothing and observes no canvas — the layout EDITOR, the PCell generators and the
`.cem` editor all stayed in `src/Ui`. **The DRC ENGINE did not, from 2026-09-05**: it draws nothing
either, and `circuitrf check` needs it (`brief-automation-4-check-and-explain.md` R-aut4-3). What
stayed of it is the two files that are not the engine — `DrcRunReport`, which posts a run to the
Messages panel, and `WBondWireClearance`, which reads a per-USER preference the engine already takes
as a setting. The namespaces that moved with it are
listed once in `src/Ui/GlobalUsings.cs` rather than in ~300 `using` lines.

**The interchange readers and writers moved here in 2026-09** for the same reason and by the same
route — `circuitrf convert` needs them, and `src/Cli` cannot reference `src/Ui`. The exports come
too, not just the imports: SkiaSharp is allowed across the firewall (`tests/Firewall.Tests`), so
`LayoutTextOutline` flattens a label's glyphs here and gives up only its font SOURCE, which loads
through Avalonia's `AssetLoader` and stays in `src/Ui` behind
`LayoutTextOutline.TypefaceSource` (installed by `UiTypefaceInstaller`, a module initializer, because
`src/Ui` has three entry points). Unset, it falls back to `SKTypeface.Default` — so a label flattened
headlessly is a different SHAPE from the same label flattened in the app, which every label-carrying
export reports rather than hides. Detail and the traps in `src/Design/RESOLVED.md`.

`tools/` holds programs that are not part of the application. **A program in there that exists to be
tested against deliberately references no other project in this repo** — an independent
implementation of a contract, not a mirror of ours, since a second copy of our own code agreeing with
itself proves nothing:
- `tools/DeviceWorkerExample` — a reference **device worker**, the kind of separate process circuitRF
  runs to evaluate an externally-supplied device model. See its own `README.md` for the protocol and
  for how a kit declares its worker.
- `tools/senior-worker` — the worker circuitRF actually ships, for compiled vendor model libraries.
  One C source file, three products (a Linux executable; on Windows a DLL holding the callbacks plus
  a launcher stub, because a Windows model imports its host callbacks from a *named module*).
- `tools/fake-model-lib` — a test-only library mimicking that model ABI, so the worker can be driven
  end to end on a machine with no vendor kit on it. Not built by `dotnet build`.
- `tools/IconGen` — rasterises `src/Ui/Assets/artwork/*-app-icon.svg` into the `.icns`/`.ico`/`.png`
  containers the three operating systems read. Writes both containers itself (no `iconutil`, no
  ImageMagick), which is what makes packaging work identically on all three. Not in `circuitRF.slnx`,
  so a plain `dotnet build` neither builds it nor restores its `Svg.Skia` dependency.

**`src/Render` draws; it does not edit.** Nothing in it docks, undoes, observes a canvas or holds a
view model — it draws committed geometry plus the OVERLAY types describing a frame's transient chrome
(a marquee, a handle, a snap marker), which the editors fill in. It is not `src/Design` for the reason
that project's own `.csproj` gives: nothing in `src/Design` draws, and 11,000 lines of drawing code
would make that comment false. **Two silent fallbacks had to be fixed to put it there** and both are
worth knowing about: `SkiaFonts` loaded its `.ttf` faces through Avalonia's `AssetLoader` and CAUGHT
its no-host failure by returning `SKTypeface.Default`, and `ThemeResolver`'s built-in `.ccolor`
provider was installed only by `App.axaml.cs`, so a theme name that resolves in the GUI fell through
to `ColorTheme.BuiltIn`. Both are ordinary embedded resources in `src/Render` now, read with
`Assembly.GetManifestResourceStream`; **`src/Ui` LINKS the same files back as `AvaloniaResource`**
rather than keeping a second copy, so its `avares://CircuitRF.Ui/Assets/{Fonts,Color}/…` URIs are
unchanged. Detail and the rest of the findings in `src/Render/RESOLVED.md`.

**UI firewall:** `RfCore`, `src/Core`, `src/Engine`, `src/Design`, `src/Render`, `src/Cli` must reference **no UI framework**
(no Avalonia) — all UI-framework code lives in `src/Ui`, so circuitRF can be re-skinned by replacing
`src/Ui` only. This is an **enforced** invariant (a CI assembly-reference check fails the build if the
core references Avalonia). Contract across the boundary: design model down, `DataSet` up. See
`docs/design/ui-architecture.md`.

## Invariants — do not violate
- Node 0 is ground.
- All AC / HB signal quantities (voltages, currents, spectra) are `System.Numerics.Complex`
  (double precision). Resolved parameter *values* are kinded **Real or Complex** (not forced
  complex); result cubes are likewise single-kind (`DataKind` Real or Complex).
- **The GUI never simulates the design layer directly — always elaborate first.**
- Never break the linear/nonlinear partition abstraction in the HB engine.
- Every analysis run returns a **`DataSet`** (a named collection of single-kind `DataCube`s);
  nothing invents its own result type. Measurements are added to the DataSet as named cubes.
- The numeric layer sees only fully-resolved parameter values (no expressions, no unbound vars).
- **Analyses attach to a `TestBench`, never to a `Cell`. Measurements also attach to the
  `TestBench`** and reference circuit quantities by absolute downward path (`V(X1.drain)`).

## Expressions, variables & cell parameters
One expression engine (tokenize → Pratt-parse → AST → evaluate; **never string substitution**)
serves global variables, cell parameters, SDD device equations, and measurements. See
`docs/design/expressions.md`.
- Cell parameters pass **top-down**: an instance binds overrides in the parent scope; the cell
  evaluates its own component values and its sub-cell passes in its scope.
- **Cycle detection is mandatory** across variables, cell-parameter defaults, and overrides.
- v1 language: variable refs; `+ - * / ^ ( )`; standard functions (`tan`, `tanh`, …);
  **conditionals** (`< <= > >= == !=`, `&& || !`, `if(cond,then,else)`); user-defined expression
  functions with arbitrary parameters. Values are kinded Real/Complex/Bool. Built to extend
  without breaking v1 files.
- The SDD's equations must stay expressible in an ordinary equation-defined-device form (hero references depend on it).

## How to add a component type
Derive from `ComponentModel` (the single base for passive **and** active parts — "Device" is
reserved for its RF meaning, an active part): declare ports + params, then `Stamp(...)` (linear
contribution — the model *contributes* stamps; the engine *owns* the matrix) and/or `Evaluate(...)`
(nonlinear: returns `i`, `q`, `dg`, `dc`). Register it in the component-model factory. Add a
golden-reference test. See `docs/design/data-model.md` §5.
**The base type must already accommodate the v2 ASM-HEMT/Verilog-A path:** a thermal/self-heating
node, collapsible internal nodes, terminal current, and charge-based capacitances (`q(v)` with
`dq/dv`). The external-device path exercises all four today (`ExternalDeviceModel`, `VerilogA`);
`FetModelBase` does not carry a thermal node of its own.


## Validation expectations
Numerical changes require a `testdata/` regression test within the tolerance in the PRD.
The five heroes are the acceptance anchors (S-params 1e-6; HB Pout/gain ±0.01 dB, eff/PAE ±0.1 pp;
loadpull contours; two-tone IM2–IM5). References are **externally generated** — produced
independently of circuitRF, then committed as fixed data — with the **identical SDD FET definition
on both sides**, so HB comparisons test our math, not a different transistor. CI runs the
suite on Windows, macOS, and Linux.

## Ask before
- Adding native (non-managed) dependencies (cross-platform risk).
- Anything marked out-of-scope for v1 in `docs/PRD.md` (transient, full Verilog-A/ASM-HEMT,
  a third-party cell database, layout view).

## Commit expectations
- Never commit unless given an explicit instruction by owner; if owners asks for commit it is to main

## Commercial Vendor References
- Do not allow references to commercial vendors or their products to leak into the circuitRF repo - not even as a glossery of names to filter out.
- This includes the names of any specific PDK that does not come built into circuitRF
- The only exception to this rule is ".kicad_pcb" - that is a file name extension for a data format.
- Before any commit, always grep search for these names that could pollute the repo.  Remove them, and indicate what was removed via chat.

## Licensing
Core is **MIT**. Never ingest GPL code (some third-party simulators are GPL — learn from, never copy).
Keep a clean extension boundary so a future commercial **circuitRF+** can layer on without forking.

## Glossary
MNA, S-parameters, harmonic balance (HB), conversion matrix, loadpull/sourcepull, APFT, IMn,
DUT, Touchstone/SNP, SDD, OSDI/Verilog-A, `DataSet`/`DataCube`. Terms are defined where they
first appear in `docs/PRD.md` and the `docs/design/` notes.
