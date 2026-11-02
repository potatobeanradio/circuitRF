# Brief 26 — Palace on Windows, in the user's Linux subsystem

**Series:** [3D EM, second series](brief-em3d-20-overview.md) · **Tag:** `R-em3d26-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §7.3 (the location setting), §7.4, §7.6
**Area:** `src/Design/Em3d/Wsl/` (new), `src/Design/Em3d/SolverDiscovery.cs`,
`src/Design/Em3d/PalaceRun.cs`, `src/Design/Em3d/Em3dRunService.cs`, `src/Design/Em3d/Install/`
(the Linux recipe, run inside a distribution), Settings ▸ 3D EM (a location row)
**Depends on:** 24, 25 · **Blocks:** 30's Windows walk-through
**Owner decision D5:** the Windows install runs in §6 are the owner's. **This brief cannot close until
they are logged.**

---

## 0. What this brief delivers

A Windows user who has, or lets circuitRF build, a Palace inside their own Linux subsystem
distribution gets an FEM solve that behaves like a native one:
- the same Simulate;
- the same progress (brief 21);
- the same `.sNp`;
- the same refusals, plus the two only this location can have: no subsystem, and not enough of the
  subsystem's memory.

Gmsh runs **natively on Windows** (brief 24 installs it), so only Palace crosses the boundary: the
mesh and config go in, the CSVs and field files come out.

---

## 1. `R-em3d26-1` — finding the distributions and what is in them

**`R-em3d26-1a`** `wsl.exe -l -v` lists the distributions. **Its output is UTF-16LE when redirected**,
not the console code page, so decode it explicitly. A parser that assumes UTF-8 sees names
interleaved with NULs and finds nothing, silently. The gate uses a captured byte fixture.

**`R-em3d26-1b`** In each **WSL 2** distribution (WSL 1 is refused, naming the conversion command),
discovery runs §7.1's Linux order **inside** it, through `wsl.exe -d <name> --exec`, with no shell
string:
1. circuitRF's own Linux home (§3);
2. Spack trees;
3. Conda;
4. `PATH` from a login shell (`bash -lc 'command -v palace'` is the one place a shell is used, and the
   argument is a constant).

The first distribution with a validated Palace wins, in the order `wsl -l` lists them. The Settings
row names the distribution.

**`R-em3d26-1c`** The version and capability probes run inside the distribution, exactly as natively.
Their cache key adds the distribution name.

**`R-em3d26-1d` The location setting** (§7.3): *Automatic* (the default), *Native*, or *Linux
subsystem: <distribution>*. On Windows, *Automatic* takes a native Palace if one ever exists, then the
subsystem.

---

## 2. `R-em3d26-2` — running there

**`R-em3d26-2a` Stage inside the Linux filesystem, never on `/mnt/c`** (§7.4). File access across the
boundary is slow, and it is where the failed build report in Palace's tracker was building.
1. The run directory is created at `~/.circuitrf/runs/<run key>/` inside the distribution.
2. `model.msh` and `config.json` are copied in through the `\\wsl.localhost\<distro>\…` path.
3. Palace runs there.
4. `port-S.csv`, the other CSVs and the field files the setup asked for (brief 29) are copied back into
   the Windows run directory.

The Windows run directory stays the one series 1 defined. It holds everything, so a result does not
depend on where it was computed (§7.3).

**`R-em3d26-2b` MPI inside the subsystem** is the Spack-linked `mpirun` next to that Palace, found the
same way series 1's Spack route finds it. Ranks come from the **subsystem's** processor count (brief 21
applies inside).

**`R-em3d26-2c` Memory is the subsystem's** (§7.4). `free -b` inside the distribution gives the virtual
machine's memory, and brief 21's check uses that figure. The warning names `.wslconfig`'s `memory=` as
the setting that raises it, with the file's path under the user's profile.

**`R-em3d26-2d` Cancelling must kill Palace inside Linux.** Killing `wsl.exe` does **not** kill the
Linux processes it started.
- Start Palace through a tiny constant wrapper (`setsid sh -c 'echo $$ > pid; exec …'`) so it leads its
  own process group.
- On cancel, run `wsl.exe -d <name> --exec kill -TERM -<pgid>`, then `-KILL` after 5 s.

Gate 6 checks the Linux process table afterwards, and it is the owner's check on real Windows.

**`R-em3d26-2e` Progress** is brief 21's parser over the relayed stdout. Nothing new.

---

## 3. `R-em3d26-3` — installing Palace in the subsystem

**`R-em3d26-3a`** *Install Palace…* on Windows runs **brief 24's Linux recipe inside the chosen
distribution**, in a circuitRF-owned home inside its Linux filesystem (`~/.circuitrf/solvers/palace/<version>/`).
It uses the same `.partial` publish, the same install record and the same known-failures matcher.
- The record is mirrored on the Windows side under `AppDataRoot`, naming the distribution, so Settings
  and brief 25 see it without starting the subsystem.
- A mirrored record whose home has vanished is shown as *missing*, and offered for clean-up.

**`R-em3d26-3b` Preconditions, each refused with the one step that fixes it**; circuitRF performs none
of them:
- the subsystem feature is not enabled: `wsl --install`, as administrator, once;
- virtualization is off in firmware: say so, and that it is a firmware setting;
- no distribution: `wsl --install -d Ubuntu`;
- a distribution without the build prerequisites: the one `sudo apt install …` line (brief 24 R-em3d24-2b).

**`R-em3d26-3c`** The consent text (brief 24 R-em3d24-2a) says the build takes place inside the named
distribution, and that the distribution and any packages the user installs on circuitRF's advice are
theirs and are never removed (brief 25 §8).

**`R-em3d26-3d` Uninstall** (brief 25) removes the Linux home through `wsl.exe --exec rm -rf <home>`,
after renaming it, then the mirrored record. It is refused while the distribution cannot be started,
naming why.

---

## 4. `R-em3d26-4` — path translation

Every path crossing the boundary goes through one function each way: Windows to Linux through
`wslpath -u` inside the distribution, called once per run and cached, and Linux to Windows through the
`\\wsl.localhost\` prefix. No hand-built string replacements. Gate 3 covers spaces, non-ASCII and a
drive other than `C:`.

---

## 5. Gate

`tests/Ui.Tests/Em3d/WslLocationTests.cs`. Everything below runs on every OS against fixtures and
fakes, because `wsl.exe` is abstracted behind one interface. Nothing runs WSL in the routine gate.

1. **UTF-16 listing.** The captured `wsl -l -v` bytes (fixture) parse to the right names, states and
   versions. Decoding them as UTF-8 fails the test's own control assertion.
2. **Discovery order inside.** A fake distribution with Palace in both Spack and `PATH` reports Spack.
   The first distribution in list order wins.
3. **Paths.** Round-trip translation of the §4 cases.
4. **Staging plan.** For a run, the planned copy-in list is exactly `model.msh` and `config.json`, and
   the copy-out list is the CSVs plus the requested field files. No `/mnt/` path appears in any command.
5. **Memory.** A fake `free -b` of 8 GB triggers brief 21's warning naming `.wslconfig`.
6. **Cancellation command.** Cancelling issues `kill -TERM -<pgid>`, then `-KILL`, with the pgid from
   the wrapper's pid file.
7. **Preconditions.** Each fake precondition failure gives its own refusal text, and nothing is
   attempted.
8. **WSL 1** is refused with the conversion command.

## 6. Owner runs on Windows (required for completion)

Log each in `testdata/em3d/f0/README.md` §Install, F0's format:
- **three clean *Install Palace…* runs** in a fresh Ubuntu distribution, with wall clock, disk and
  every failure verbatim, plus how it was fixed. New failures become `KnownFailures` entries;
- three clean installs each of **Gmsh and openEMS** natively (brief 24's Windows recipes);
- one Simulate of F0 case B through the subsystem: its `.sNp` against the macOS one (|ΔS21| < 0.01 dB,
  because it is the same Palace version and config);
- a Cancel mid-run, then `wsl -d Ubuntu -- ps -ef | grep palace` shows nothing;
- the walk-through in overview R-em3d20-2, on Windows.

## 7. Scope

- No native Windows Palace: that is upstream's (§7.4), and discovery finds it the day it exists.
- No container and no remote location.
