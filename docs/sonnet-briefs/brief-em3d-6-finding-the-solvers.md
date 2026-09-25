# Brief 6 — finding the solvers: discovery, validated versions, capability, the GPL line

**Series:** [3D EM, first series](brief-em3d-0-overview.md) · **Tag:** `R-em3d6-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §7.1, §5.3 (validated versions), §2 (ParMETIS), §12 (the GPL boundary)
**Area:** `src/Design/Em3d/SolverDiscovery.cs` (new), `src/Ui/Theming/AppPreferences.cs`,
`src/Ui/Views/Dialogs/` (a Settings page), `src/Cli/Explain.cs`, `tests/Firewall.Tests/`,
`docs/user/src/reference/em-setup.md` (a manual-install section; source only, no DocGen)
**Depends on:** 1 (the validated versions, Q9, Q10) · **Blocks:** 7, 9

---

## 0. What this brief delivers

circuitRF can answer, for each of **Palace**, **Gmsh** and **openEMS**: *is it here, where, which
version, is that version validated, and can it do what this setup needs?* It answers the same way
from the CLI, from Settings, and at the top of every 3D run. A missing or unvalidated program is a
refusal that names the remedy.

It also puts the **GPL boundary** under test before any code that talks to a GPL program exists.

**It installs nothing.** The install assistant is the next series (overview §4).

---

## 1. `R-em3d6-1` — one discovery, shaped like the two that exist

**`R-em3d6-1a`** Model it on `VerilogACompilerDiscovery` (`src/Core/Devices/External/VerilogACompiler.cs`)
and `GitDiscovery` (`src/Design/Revision/GitDiscovery.cs`). Both already solve this problem, and
`src/Core/RESOLVED.md` records what they learned. **Read those entries before writing a line.**
Specifically:
- **Order:** a path the user names in Settings, then an environment variable, then `PATH`, then the
  directories a GUI-launched process cannot see through `PATH`. A Finder-launched app's `PATH` holds
  only the four system directories.
- **A named program that does not work is reported, never silently replaced** by one found
  elsewhere.
- **Resolve to an absolute path once, and start the process from there** (`GitInstallation`'s
  comment on why a bare name is not started).
- **`CandidateCommands` per tool is the one place that names the executable.** The search
  directories derive from it.
- The preference lives above the firewall and is **injected** as a `Func<string?>`
  (`PreferredCommand`), exactly as those two do. `src/Design` never reads a Ui preference.

**`R-em3d6-1b`** One class, `SolverDiscovery`, with one instance per tool, not three copies:
`SolverDiscovery.Palace`, `.Gmsh`, `.OpenEms`. Each has its candidate list, its environment
variable (`CIRCUITRF_PALACE`, `CIRCUITRF_GMSH`, `CIRCUITRF_OPENEMS`), its version parser and its
validated list. A private copy of the discovery walk per tool is how three drift apart.

**`R-em3d6-1c`** `Find(out rejected)` returns a `SolverInstallation(Path, Version, Banner, HowFound,
Validated)` or null, with every rejected candidate and why, as the two precedents do.
`DescribeFailure(rejected)` renders the refusal.

---

## 2. `R-em3d6-2` — validated versions

**`R-em3d6-2a`** Each tool carries a **short list** of validated versions, starting with exactly
the one F0 validated (brief 1 R-em3d1-4). It is data in one place per tool, with the F0 findings
file cited beside it.

**`R-em3d6-2b` Version check before every run** (§5.3, §7.1). An unvalidated version is a
**refusal naming the validated versions**, never a warning. For Palace this matters most: its
configuration changes meaning across versions, and a silently misread key produces a plausible
wrong answer. For Gmsh, the `.geo` language is stabler, so the refusal can be relaxed to a
**warning** for a newer *minor* version **if** F0 found the `.geo` constructs brief 7 uses
unchanged across it. That is a finding to cite, not a default to assume.

**`R-em3d6-2c`** The version is read the way F0 found works for each tool (Q9, Q11). If a tool
has no machine-readable version flag, parse its banner with a regex that is tested against the
**exact** banner F0 recorded, committed as a fixture.

---

## 3. `R-em3d6-3` — capability probe (Palace)

**`R-em3d6-3a`** A user-built Palace may lack things circuitRF needs (§7.1). This brief needs only
**driven solves with lumped ports**, and the probe checks for exactly that. It checks nothing
speculative. The mechanism is whatever F0's Q9 found: build info if Palace reports it, else the
cheapest fail-fast probe run, in a temporary directory, deleted afterwards.

**`R-em3d6-3b` Cached per executable path + file timestamp + size**, under `UserStateDirectory`, so
a probe run happens once per installed binary, not once per run. A changed binary re-probes.

**`R-em3d6-3c`** A missing capability is a refusal **at setup time**, naming the capability and the
Palace build option that provides it. It is not a failure minutes into a run.

**`R-em3d6-3d`** The probe interface takes a set of required capabilities. The wave-port series
adds "eigensolver" to it without changing the mechanism.

---

## 4. `R-em3d6-4` — where the answer shows

**`R-em3d6-4a` Settings.** A new *3D EM solvers* page, one row per tool: what was found, where, which
version, validated or not, *how* found (Settings / environment / `PATH` / default directory), and
a path box with Browse. Blank means search. **The solvers are named** in all user-facing text
(§7.1, owner's call 2026-09-24). This is unlike the Verilog-A compiler, so the
`UserFacingTextGateTests` allowlist needs the three names added **deliberately, with a comment
citing §7.1**. It is not a blanket exception.

**`R-em3d6-4b` The ParMETIS sentence** (§2, §7.1). Palace's row carries one sentence: a default
Palace build includes ParMETIS, whose licence permits commercial use for evaluation only, and a
user who builds Palace accepts those terms. It appears once, in the row, in plain words, and is
not a dialog.

**`R-em3d6-4c` `explain`** on a 3D setup gains a *solver* section: each tool the setup's
`Solver3D` needs, what discovery found, and whether the run would proceed. This **may** run version
probes (the cached result where there is one). It still starts **no solve**, and brief 5's counter
gate is narrowed to "no solve process" with a comment saying why.

**`R-em3d6-4d` The refusal on a missing tool** names, in order: the tool; how to point circuitRF at
an existing install (the Settings page, or the environment variable); and the manual-install
section (§5). **On Windows, the Palace refusal also says that Palace does not run natively on
Windows**, that the Linux-subsystem route is not in this build, and that openEMS runs natively
here. That is overview §4's honesty rule.

---

## 5. `R-em3d6-5` — the manual-install section

A section in `docs/user/src/reference/em-setup.md` (edit the **source**; do not run DocGen), per tool: the
validated version, the upstream route F0 used on each platform it tried, and the Settings row that
confirms it worked. **Only what F0 actually did** is written as instructions. A platform F0 did not
try is listed as *not yet verified* with upstream's own link. The per-platform installation page and
the assistant come with the next series.

---

## 6. `R-em3d6-6` — the GPL boundary, under test before it can be crossed

**`R-em3d6-6a`** `tests/Firewall.Tests` gains `SolverBoundaryTests`, the §12 risk made
executable:
- **No circuitRF assembly references** a Gmsh, openEMS or CSXCAD library: no `PackageReference`,
  no `Reference`, no native library shipped in any output folder. Check the assemblies' references
  and the build outputs, as `UiFirewallTests` checks for Avalonia.
- **No `DllImport`/`LibraryImport`** in `src/` names `gmsh`, `openEMS`, `CSXCAD` or `palace`, in any
  casing.
- **No source file under `src/` or `tools/`** carries a Gmsh/openEMS/CSXCAD copyright header or
  licence notice (a scan for the upstream projects' header lines, recorded as fixtures from their
  source).
- **No Python or Octave interface is invoked.** No process start in `src/` names `python` with a
  `CSXCAD`/`openEMS` module argument, and no `.py`/`.m` file ships in `src/`. §5.2 forbids their
  scripting interfaces.

**`R-em3d6-6b`** Each rule gets a planted-violation self-test: the scanner is pointed at a
temporary file that violates it and must fail. That proves the rule is not vacuous, as the
Avalonia firewall was proven against a deliberate reference.

**`R-em3d6-6c`** `THIRD-PARTY-NOTICES.md` gains **no** entry. A test asserts none of the three names
appears in it (§7.1: circuitRF redistributes nothing).

---

## 7. Gate

`tests/Ui.Tests/Em3d/SolverDiscoveryTests.cs` and `tests/Firewall.Tests/SolverBoundaryTests.cs`.

1. **Order**: with fake executables (tiny scripts that print F0's recorded banners) placed in a
   Settings path, an env-var path and a `PATH` directory, Settings wins, then env, then `PATH`.
2. **A broken named program is reported**, and the working one on `PATH` is **not** used.
3. **Unvalidated version** is a refusal naming the validated list.
4. **Banner parsing** against F0's committed banners, for each tool.
5. **Capability cache**: a second `Find` on the same binary runs no probe (counter); touching the
   file re-probes.
6. **Windows Palace refusal** text names openEMS (test the text builder with the platform
   injected; do not require Windows).
7. **Every `SolverBoundaryTests` rule** and its planted-violation self-test.
8. **Real tools, when present**: on a machine with F0's installs, all three are found and
   validated. **Skipped, with a reason, when absent** (overview §1i).

## 8. Scope

- **No install, no uninstall, no download.** Next series.
- **No Linux-subsystem, container or remote location.** Next series.
- **No solve.** Briefs 7 and 9.
