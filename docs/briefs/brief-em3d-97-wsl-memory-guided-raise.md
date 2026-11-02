# Brief 97 — Give the Linux subsystem more memory, from the warning

**Tag:** `R-em3d97-n` · **Series:** designer feedback round 11 follow-ups.
**Area:** `src/Design/Em3d/Wsl/` (a new `WslConfigFile` editor and `WslMemory` operation beside `WslPalace.cs`),
`src/Ui/Messages/` (a row carrying both a file link and an action), `src/Ui/ViewModels/WorkspaceViewModel.cs`
(`RunEmSetupAsync`'s warning loop, `ConfirmEmMemory`), a new dialog under `src/Ui/Views/Dialogs/`,
Settings ▸ 3D EM (the Palace **Location** row), `docs/user/src/reference/em-setup.md` (the Palace-on-Windows
**Memory** paragraph)
**Depends on:** the round 11 follow-up already in main: `WslPalace.SuggestedMemoryGb`, `WslPalace.WslConfigRemedy`
(offered only where it can make the run fit), `Em3dMemoryVerdict.File`, `EmRunResult.MessageFiles` (the `.wslconfig`
path as the Messages row's reveal link) · **Blocks:** —

## Why

Palace on Windows runs in the Linux subsystem's virtual machine, which WSL gives **half the computer's memory** by
default. The first Palace-on-Windows user had an 8 GB laptop, so a 3.8 GB VM, and the Launch example's 93,086
tetrahedra came out at 4.2 GB. The warning now names the right value (`memory=6GB`) and links the file. The user
still has to open a hidden file in their profile, edit INI syntax by hand, and know that nothing changes until
`wsl --shutdown`. On the machines where this matters most (8–16 GB laptops), it's one button's worth of work.

**Not a circuitRF setting.** The value WSL uses lives in `%UserProfile%\.wslconfig`. A stored circuitRF preference
would be a second copy that drifts from it the first time the user (or Docker Desktop's own settings) edits the file.
circuitRF stores nothing. It reads the file and the live VM size every time, and writes only when asked.

**Two facts shape every step:**
- `.wslconfig` is **machine-wide for this user**. It applies to every distribution and to Docker Desktop's WSL backend.
- A change takes effect only after `wsl --shutdown`, which **stops everything running in WSL**: other shells,
  containers, and a Palace run of circuitRF's own.

## 1. `R-em3d97-1` — `WslConfigFile`: edit one key, keep everything else (`src/Design/Em3d/Wsl/`)

A small INI editor with no UI, so every gate runs on macOS:
- `Read(path)` returns the current `memory=` under `[wsl2]` (null when absent), plus the text.
- `WithMemory(text, gb)` returns the new text and changes **only** that key:
  - No file: `[wsl2]\r\nmemory=6GB\r\n`.
  - No `[wsl2]` section: append one at the end, after a blank line.
  - Section present, key absent: insert `memory=6GB` directly after the section header.
  - Key present: replace that line's value only, keeping its leading whitespace and any trailing `# comment`.
  - The key repeated in the section (WSL reads the last one): replace the last, leave the rest, and say so in the dialog.
  - Section and key names are case-insensitive. `[WSL2]` and `Memory=` match.
- **Kept byte for byte:** every other line, comments (`#` and `;`), blank lines, the file's line endings (CRLF or LF, by
  majority), its encoding, and a BOM if there was one.
- `Write` keeps the previous file once as `.wslconfig.circuitrf-backup` (overwritten on later writes) and writes
  through a temp file plus a replace, so a failed write leaves the original untouched.

## 2. `R-em3d97-2` — the dialog: *Linux subsystem memory*

One dialog, reachable from two places (§3). It shows:
- **Now:** the VM's memory as `free -b` reads it (`WslSession.MemoryBytes`), the `memory=` the file says (or "not set:
  WSL's default, half this computer's memory"), and this computer's memory.
- **Proposed:** `SuggestedMemoryGb(host)` in an editable whole-GB box. The box refuses a value above the host's memory,
  and below 2 GB.
- **The change:** the one line, before and after, and the file's path as a reveal link.
- **What restarting stops:** "Applying it restarts the Linux subsystem: every program running in it stops (other
  terminals, containers, Docker Desktop's engine)." Plus the distributions `wsl --list --running` reports right now.
  `WslDistributions` already parses that output.

Three buttons: **Save and restart the subsystem**, **Save only** (applies at the next restart; the dialog says so),
and **Cancel**. Save-and-restart asks no second question, because the dialog *is* the question. It has to name what
stops (above), and the button text has to say "restart".

After a restart: run `free -b` again in the distribution (this boots the VM) and report the new figure in Messages.
If it didn't move by at least half the change, say so and name the likely cause. One example: an older WSL that
ignores `.wslconfig` (WSL 1 never reads it; brief 26 refuses WSL 1 already).

## 3. `R-em3d97-3` — where it's offered

- **On the memory warning row.** When `EmRunResult.MessageFiles` names a `.wslconfig` for a warning (only the case
  where raising it can make the run fit, per the round 11 change), the row keeps its reveal link **and** gains the
  action **Give the subsystem more memory…**. `MessageEntry` takes `filePath` and an action today, but
  `IMessageSink.PostAction` passes `filePath: null`. Add a `filePath` parameter to `PostAction` (default null) rather
  than a second posting call.
- **In the 150 % confirmation** (`ConfirmEmMemory`). It is the only point *before* a run where this can help: the
  warning row arrives with the run's result, after the swapping. When the verdict's `File` is a `.wslconfig`, the
  question gets a third answer, **Give the subsystem more memory first…**. It opens the dialog and stops the run
  (Simulate again afterwards). It must not restart WSL and then carry on, because the run's Palace runner was set up
  against the old VM.
- **Settings ▸ 3D EM**, under Palace's **Location** row, on Windows only and only when a distribution is the location
  (or Automatic resolves to one): a read-only line "Linux subsystem: 3.8 GB of 7.6 GB" with **Change…** opening the
  same dialog. This is the place a user looks before a large run, and it reads live every time it's shown (D2).

## 4. `R-em3d97-4` — refusals

- **A circuitRF run or install in the subsystem is in flight** (`_emWorkInFlight` holds a Palace-in-WSL run, or
  `SolverInUse` holds a subsystem home): Save-and-restart is disabled, and the reason names it ("the 3D EM run of
  'Launch Palace' is running in Ubuntu"). Save only stays available.
- **Not Windows, or `wsl.exe` absent** (`IWsl.Available`): none of the three entry points exist.
- **`.wslconfig` unreadable or unwritable:** the dialog says so with the OS's message and offers the reveal link. It
  never falls back to writing elsewhere.
- `wsl --shutdown` runs through `IWsl.Run(["--shutdown"], …)`, so the fake records it. Its failure is reported with
  its own output, and the file change is kept (it applies at the next restart either way).

## 5. Out of scope

- **No CLI verb or `serve` tool.** This edits machine-wide configuration and stops other programs. An out-of-process
  agent should be told the value (the warning already prints the sentence and the path), not handed a switch that
  restarts someone's Docker engine.
- `swap=`, `processors=` and disk size. Core count is already the distribution's own (brief 26). WSL's virtual disk
  grows on demand, so a disk shortage is the Windows drive being full, which is a different message.

## 6. Decisions (settled 2026-10-01; the owner delegated all three)

- **D1 — the suggestion rule: keep what is in main.** This computer's memory less max(2 GB, ¼), rounded down to
  whole GB (6 GB of 8, 12 of 16, 24 of 32, 48 of 64). The 2 GB floor keeps Windows usable on an 8 GB laptop. The
  quarter keeps a large machine's desktop applications and file cache out of swap. A whole-GB value is what a user
  would type, and rounding down never overshoots. The dialog's box lets the user pick any value from 2 GB up to the
  computer's memory, so the rule is only a starting point.
- **D2 — the Settings ▸ 3D EM line: yes.** The warning row arrives after a run has already swapped. The 150 %
  confirmation catches only the worst runs. A line read live in Settings is the one place a user can look *before* a
  large run, and it costs one row that exists only on Windows with a subsystem location.
- **D3 — the backup file: yes,** `.wslconfig.circuitrf-backup` beside the original, overwritten on each write. The file
  is machine-wide and may hold settings another program (Docker Desktop) or the user wrote. One copy makes a write
  circuitRF got wrong recoverable without anyone having to remember what the file said. The dialog names the backup
  after saving.

## 7. Gates (headless; the fake `IWsl` from `WslLocationTests`)

1. `WslConfigFile.WithMemory` over the six shapes of §1: no file, no section, no key, key with a trailing comment, key
   repeated, mixed case. Plus one file with CRLF, a BOM and comments that comes back identical except for the one
   line.
2. Save-and-restart on the fake: the file is written, the backup exists, `--shutdown` is recorded, then `free -b` is
   recorded. The Messages line reports the new figure.
3. Save only: the file is written and **no** `--shutdown` is recorded.
4. With a Palace-in-WSL run registered in flight: Save-and-restart is disabled with the run's name, and no
   `--shutdown` is recorded.
5. The warning row: a result whose `MessageFiles` names a `.wslconfig` posts a row with both the file link and the
   action. A result naming no file posts neither.
6. The confirmation: choosing *more memory first* returns "do not run" and records no `--shutdown`.

Each gate is one claim. No GUI pixels can be seen from this machine, so the dialog's layout is the owner's to look at.

## On completion

Findings go in `src/Design/RESOLVED.md` (the editor) and `src/Ui/RESOLVED.md` (the dialog and the message row).
Never in a `CLAUDE.md`. Update the **Memory** paragraph of `docs/user/src/reference/em-setup.md` to name the button and
the Settings line. Do not run DocGen; the owner regenerates `docs/user` at the end of a series.
