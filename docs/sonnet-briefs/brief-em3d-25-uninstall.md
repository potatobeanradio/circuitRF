# Brief 25 — uninstall: per tool, all of them, and circuitRF itself

**Series:** [3D EM, second series](brief-em3d-20-overview.md) · **Tag:** `R-em3d25-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §7.2 ("What the assistant installs, it can uninstall"
and "Uninstalling circuitRF uninstalls the solvers it installed")
**Area:** `src/Design/Em3d/Install/SolverUninstaller.cs` (new), `src/Design/Em3d/Em3dRunService.cs`
(in-use registry), `src/Cli/Solver.cs`, Settings ▸ 3D EM, a new *Uninstall circuitRF…* command,
`packaging/windows/circuitRF.wxs` + `packaging/windows/stub/`, `packaging/linux/install.sh`,
`src/Ui/Updates/` (upgrade paths, read-only here)
**Depends on:** 24 · **Blocks:** 26
**Owner decision D6:** §4–§5 (*Uninstall circuitRF…*) are in this series by default. They can ship
later without harm (§7.2 point 4). If the owner defers them, §1–§3 stand alone.

---

## 0. What this brief delivers

A from-source Palace, with its Spack tree and build cache, runs to gigabytes. A user who is done with
3D EM gets that space back:
- from the Settings row (*Uninstall*);
- with *Remove all 3D solvers*;
- with `solver remove`;
- with *Uninstall circuitRF…*, which removes circuitRF and the solvers it installed, with a warning.

---

## 1. `R-em3d25-1` — removing one tool

**`R-em3d25-1a` Only what the install record names** (brief 24 R-em3d24-2f). Because each version's
home is self-contained, removal is that home directory. Nothing outside it is touched.
- A tool found any other way (Settings path, environment variable, `PATH`, Spack, Conda) **has no
  Uninstall action**, in the GUI or the CLI. `solver remove` on it refuses, naming where it was found
  and that circuitRF did not install it.

**`R-em3d25-1b` The confirmation shows the measured space**, measured at confirmation time, not taken
from the record. It also says:
- that removal is permanent;
- that getting the tool back is a reinstall, stating the recipe's measured time ("about 52 minutes on
  an M4");
- **that documents are not touched**: setups, meshes, results and run directories live in workspaces.

**`R-em3d25-1c` Refused while in use.** `Em3dRunService` holds an in-use count per solver home for the
length of a run, and an install in progress holds its home too. The refusal names what is using it.
This is **in process** only: a CLI run in another process holds a lock file in the home
(`in-use.<pid>`), and a lock whose pid is dead is ignored and removed.

**`R-em3d25-1d` Delete safely.** Rename the home to `<home>.removing` first, then delete. If the delete
fails partway (a file held open on Windows), what remains is not a discovery location, and the next
attempt finishes it. Report the files that could not be removed by path.

---

## 2. `R-em3d25-2` — *Remove all 3D solvers*

One action, beside the per-tool ones, that removes every circuitRF-installed home **after one
confirmation** listing each with its size and the total. It refuses as a whole if any is in use. It
does not remove some and leave others. The CLI spelling is `solver remove --all [--yes]`.

---

## 3. `R-em3d25-3` — superseded versions

When brief 24 publishes a newly validated version of a tool, any circuitRF-installed older version of
that tool is **listed with its size** in the success message and in the Settings row, and offered for
removal. It is never removed automatically, because a user may still want to compare old results
against the old version.

---

## 4. `R-em3d25-4` — *Uninstall circuitRF…* (§7.2 points 1, 2, 5)

**`R-em3d25-4a`** A command in the application menu on every platform:
1. show the warning (the solvers go too; reinstalling means reinstalling them, with the measured
   times; solvers other users installed are theirs and stay);
2. remove the solvers exactly as *Remove all 3D solvers* does (refused while in use);
3. remove circuitRF:
   - **Windows:** the MSI uninstall for this product code;
   - **macOS:** move the `.app` to the Trash;
   - **Linux tarball:** `install.sh --uninstall`;
   - **Linux `.deb`:** print the one package-manager command and stop, because the package manager
     removes a `.deb`.

**`R-em3d25-4b` Windows Apps-list routing** (§7.2 point 2). For the perUser layout, the uninstall
entry Windows shows is pointed at the stub `circuitRF.exe`
(`packaging/windows/stub/`, which never changes). The stub, invoked for uninstall, runs the warning
and then the MSI uninstall. perMachine gets the same through the installed executable. This is a
`.wxs` change (`ARPNOMODIFY`, and the uninstall string of the Apps entry). **Only the owner can verify
it**, on Windows (§6).

**`R-em3d25-4c` Headless:** `solver remove --all` plus the platform's own uninstall. There is no
`circuitrf uninstall` verb, because removing the program from inside the program's own CLI process is
not something a build machine should do.

---

## 5. `R-em3d25-5` — an upgrade never removes a solver (§7.2 point 3, a gate)

**`R-em3d25-5a`** Nothing attached to uninstall runs inside an upgrade:
- **Windows perMachine:** `MajorUpgrade` works by uninstalling the previous version. Any removal hook
  is conditioned on a real removal (`REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE`).
- **perUser updater:** the version swap never calls `SolverUninstaller`.
- **macOS:** the bundle exchange never calls it.

**`R-em3d25-5b`** The circuitRF solver homes are under `AppDataRoot`, **outside** anything an
installer owns. An MSI's own file removal cannot reach them by construction. Keep it that way: no
component in the `.wxs` references `AppDataRoot`.

**`R-em3d25-5c`** Leftovers are reused (§7.2 point 4). A circuitRF reinstalled after a Trash or
package-manager removal finds the homes through brief 24's `Installed` route, and their rows still
carry *Uninstall*. This is true by construction. Gate 7 holds it.

---

## 6. Gate

`tests/Ui.Tests/Em3d/SolverUninstallTests.cs` and an addition to `tests/Ui.Tests/PackagingScriptTests.cs`.

1. **By record only.** A fake install home is removed, and a sibling directory the record does not
   name survives.
2. **Not ours, not removable.** A tool found on `PATH` has no uninstall action, and `solver remove`
   refuses naming its path.
3. **In use.** A held in-use count refuses removal and names the holder. A stale `in-use.<pid>` lock
   with a dead pid is ignored.
4. **Remove all is all or nothing.** With one of two homes in use, neither is removed.
5. **Interrupted delete.** A read-only file inside a `.removing` home leaves no discovery location.
   The next attempt completes.
6. **Upgrade paths.** A source scan finds no call from `src/Ui/Updates/` or the bundle exchange to
   `SolverUninstaller`. The `.wxs` removal action carries the `NOT UPGRADINGPRODUCTCODE` condition, and
   no `.wxs` component references the solver homes.
7. **Leftovers reused.** A home present before startup is discovered as `Installed`, with its
   uninstall action.
8. **Headless consent.** `solver remove palace` without `--yes` exits 1 and removes nothing.
9. **Documents untouched.** A workspace with a run directory under `results/` is byte-identical after
   *Remove all*.

## 7. Owner check (Windows and macOS; nothing here can be seen from this session)

- Windows perUser: Apps list ▸ circuitRF ▸ Uninstall shows the warning, then removes solvers and app.
- Windows perMachine: the same through the installed executable.
- Windows: installing a newer `.msi` over an older one leaves the Palace home in place.
- macOS: *Uninstall circuitRF…* moves the app to the Trash after removing the solvers.
- macOS: trash the app by hand, reinstall, and Palace is found and still uninstallable.

## 8. Scope

- Removes only circuitRF's own homes. The Linux subsystem distribution and any system packages the
  user installed on circuitRF's advice are never removed (brief 26 states that in its confirmation).
