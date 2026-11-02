# Brief EMI-2 — Installing EMerge, finding it, and its row in Settings ▸ Solvers

**Tag:** `R-emi2-n` · **Series:** EMI (see `brief-emi-0-overview.md`) · **Depends on:** EMI-1
**Area:** `src/Design/Em3d/Install/` (recipes, `SolverRecipe`, `SolverInstaller`, `SolverHomes`, `SolverUninstaller`,
`KnownFailures`), `src/Design/Em3d/SolverDiscovery.cs`, a new `src/Design/Em3d/Solvers/EMerge/`,
`src/Ui/Views/Dialogs/Em3dSolverSettingsView.axaml(.cs)`, `SolverInstallConsentDialog.cs`, `SolverInstallRunner.cs`,
`src/Cli/Solver.cs`, the MCP `solver` tool, `docs/user/src/reference/settings.md`. Findings in
`src/Design/RESOLVED.md`.
**Out of scope:** the faster sparse solver (EMI-3; this brief installs the baseline that works everywhere).

---

## 0. What has to be on the machine

A Python (3.10–3.13), an environment with the pinned EMerge and its dependencies (numpy, scipy, Gmsh's Python package,
…), and nothing else. Measured: 72 s and 837 MB for the environment on macOS arm64 (EMI-0 §2). EMerge 3.0 publishes
compiled wheels for macOS arm64/x64, Linux x64/arm64 and Windows x64, so **the baseline install compiles nothing**.

## 1. `R-emi2-1` — the recipes

**R-emi2-1a — one recipe per platform/architecture**, `emerge-<version>-<platform>-<arch>.json`, for macos-arm64,
macos-x64, linux-x64, linux-arm64, windows-x64 and windows-arm64 (O2: x64 Python under emulation). `layout:
relocatable` if the environment survives a rename of the home, else `in-place` (measure; a uv-managed interpreter and
a venv that points at it by absolute path is probably in-place).

**R-emi2-1b — the steps**, all existing step kinds (`Download`, `Extract`, `Run`, `Write`):
1. **Download `uv`** — the pinned release's archive for this platform, checked against the release's published
   SHA-256 (put it in `sources[].sha256`, as openEMS's recipe does).
2. **Extract** it into the home.
3. **Run** `uv python install <pinned 3.13.x>` with `UV_PYTHON_INSTALL_DIR=${home}/python` and
   `UV_CACHE_DIR=${home}/uv-cache` — inside the home, so the user's own `uv`/Python is neither read nor changed.
4. **Run** `uv venv ${home}/env --python <that interpreter>`.
5. **Write** `${home}/requirements.lock` from the recipe's own lines: the pinned EMerge and **every** transitive
   dependency at an exact version **with hashes** (generated once with `uv pip compile --generate-hashes` per platform
   and committed in the recipe). A dependency released tomorrow must not change what a user gets today.
6. **Run** `uv pip install --python ${home}/env --require-hashes -r ${home}/requirements.lock`.
7. **Run** the probe (R-emi2-3) as the last step, with `expect`, so a broken environment fails the install, not the
   first run.

`environment`/`clearEnvironment` clear `PYTHON*`, `VIRTUAL_ENV`, `UV_*` (then set the two above) and `PIP_*`, so a
user's shell configuration cannot leak in. `prerequisites`: none on any platform for the baseline.

**R-emi2-1c — measured block.** Fill `measured` per platform from a real install (minutes, GB). The owner's Windows is
an ARM64 VM; the x64 Windows recipe is validated there under emulation and that is stated in the block.

**R-emi2-1d — knownFailures.** Seed with what the first installs show (a proxy blocking the Python download is likely;
uv's own error names it). Do not invent entries.

## 2. `R-emi2-2` — the home and uninstall

`SolverHomes` gains the `emerge` tool. The home holds `uv`, `python/`, `env/`, `uv-cache/`, `requirements.lock` and
the install record. Uninstall is the existing `SolverUninstaller` path (delete the home; refuse while a run uses it —
`SolverInUse`). After an install, **delete `uv-cache/`** (it roughly doubles the footprint and nothing reads it again).

## 3. `R-emi2-3` — discovery and the probe

**R-emi2-3a — what is found.** The program circuitRF runs is a **Python interpreter with EMerge importable**. Discovery
order is the existing one: the Settings path (a Python interpreter, or a folder holding a venv), then
`CIRCUITRF_EMERGE`, then the circuitRF-installed home. **Not `PATH`**: a `python` on `PATH` with some EMerge in it is
far more likely to be a user's unrelated environment than an intended solver; a user who wants it names it in
Settings.

**R-emi2-3b — the probe** is one short process: `<python> -I -c "<fixed probe source>"`, the source a constant in
`src/Design` (no user string in it). It prints one JSON line: EMerge's version, Python's version, the platform, the
sparse solvers EMerge reports available (it exposes these as module-level flags; read them by name and treat a missing
name as "not available", since the flags are internal and may move), the logical core count, and Gmsh's version.
Parse it; refuse an unparseable answer by quoting its first line.

**R-emi2-3c — cache** the verdict like Palace's capability probe (`solver-capabilities.json`), keyed on the
interpreter's path, size and timestamp, so Settings and every run do not pay ~1 s of Python start-up each time.

## 4. `R-emi2-4` — Settings ▸ Solvers: the EMerge row

A section after openEMS, built exactly like the others (`Em3dSolverSettingsView.axaml`): heading **EMerge (FEM)**, a
path box (placeholder: "Leave blank to search (CIRCUITRF_EMERGE, then the copy circuitRF installed)"), Browse…, a
selectable status line, **Install EMerge…** when `SolverStatus.OfferInstall` says so, and one Uninstall … per installed
home. The status line carries, in this order: version and whether it is validated, **the speed tier** EMI-3 defines
("fast sparse solver" / "baseline sparse solver — <what would make it fast>"), and where it was found. The heading's
first sentence of the section intro ("circuitRF includes none of these programs…") already covers EMerge.

The consent dialog (`SolverInstallConsentDialog`) lists what will be fetched, from where, the disk estimate, and the
licences: EMerge GPL-2.0, Gmsh GPL, and **on x64 the vendor-licensed sparse-solver library EMerge depends on** (a
proprietary licence, as Palace's row states ParMETIS's terms). One sentence each, no vendor names.

## 5. `R-emi2-5` — CLI and MCP

`circuitrf solver install emerge`, `solver list` (the probe's line), `solver uninstall emerge` — through the same
functions the GUI calls, no logic in `src/Cli`. MCP `solver` reports EMerge and its tier.

## 6. `R-emi2-6` — validated versions

A validated-versions list for EMerge (as Gmsh's, brief-em3d-135): one entry, the pinned version. A found EMerge of
another version is **refused before a run**, naming the validated one and offering the install — the same rule as
`em-3d.md` §5.3, and the only defence against an API change producing a plausible wrong answer. The script EMI-4
writes also calls EMerge's own version check, as a second guard.

## 7. Tests (targeted)

1. Each recipe validates against `recipe.schema.json` and pins every source by checksum; the lock file in each recipe
   has a hash on every line (a parse, not a network fetch).
2. The probe parse: canned JSON → version, solvers, cores; garbage → refusal quoting it.
3. Discovery order: Settings path beats environment beats home; a `python` on `PATH` alone is not found.
4. The validated-version refusal sentence names the validated version.
5. CLI `solver list` includes EMerge from the registry (no special case in `src/Cli`).

No test installs anything. One real install per platform is the field check; record results in the recipe's
`measured` block and `src/Design/RESOLVED.md`.
