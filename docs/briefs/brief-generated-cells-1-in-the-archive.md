# Generated cells 1 — the archive carries the artwork it was drawn with

**Series:** generated cells (1 of 2) · **Tag:** `R-gc1-n` · **Phase:** behaviour change (owner, 2026-09-28)
**Area:** `src/Ui/Archive/WorkspaceArchiveScanner.cs` (`SkippedDirectories`, the plan's option rows),
`src/Ui/Archive/WorkspaceArchivePlan.cs`, `src/Ui/Archive/WorkspaceArchiveWriter.cs`, the Archive
Workspace dialog, `src/Ui/Layout/PCells/GeneratedCellsLifecycle.cs` (the live-cell set its prune uses)
**Depends on:** nothing · **Blocks:** nothing (brief 2 is independent and makes this one less critical,
never unnecessary)
**Rule:** no vendor, PDK, customer or reporter names in the code, tests or docs.

---

## 0. Why the folder was left out, and why that is wrong

`.generated-cells` is skipped by every archive as "a pure cache every layout can rebuild". That holds
only where something CAN rebuild it:

- **The GUI rebuilds on workspace open** (`RegenerateAllGeneratedCellsAsync`) — but only in a writable
  workspace, and only for a generator the recipient HAS. A kit PCell needs the kit and a Python that can
  run it. A vendor PDK is licensed and very often not in the archive (its row is the sender's choice), so
  the recipient cannot rebuild — and the generated cells were the only copy of that artwork.
- **Nothing headless rebuilds at all** (brief 2): `circuitrf rail/render/em/lvs/check/convert` on an
  unpacked archive flatten each missing cell to nothing with a WARNING ("Instance referencing
  `…/.generated-cells/smt-0402@N_…` does not resolve — skipped"). Seen on the round-9 field-report
  board: every placed part's land pattern vanished from a railRF run.
- A read-only workspace never rebuilds, by design (SL2).

## 1. `R-gc1-1` — size first (the owner's concern: a large MMIC)

The PDK demo's folder is 16 KB, which says nothing about a real MMIC. Measure before deciding the
default:

- Build a stress workspace from the shipped PDK PCells example: several hundred UNIQUE parameter sets
  across its generators (FET finger counts/widths, spirals of varied turns — the curved ones are the
  vertex-heavy case), placed in one layout. Generated cells are content-addressed per unique parameter
  set, so the count of UNIQUE variants — not of instances — is what matters.
- Report: folder size on disk, size inside the zip (deflated), the same for the layout itself, and the
  ratio to the design's own `.clay`s. Also note what an `.npy` of a typical sweep weighs, since results
  are already ticked by default.

Expectation to test, not assume: generated cells are plain-text geometry, compress roughly 5–10× in the
zip, and are the same order of size as the artwork itself (in an MMIC the PCells ARE most of the
artwork), so even hundreds of variants should cost single-digit MB compressed. If the measurement says
otherwise, bring it to the owner before choosing the default.

## 2. `R-gc1-2` — the rule

- `.generated-cells` leaves the SKIPPED list (for archives; see §3 for copies) and becomes an itemised
  option row like results: one row, its size shown, **ticked by default**.
- **Only live cells.** Include exactly the cells some layout in the archive references — the same live
  set `GeneratedCellsLifecycle`'s prune computes. A workspace whose generator or technology changed
  carries dead folders until the next prune; they must not travel. Where the live set cannot be
  computed completely (a `.clay` that will not read), include everything and say so, mirroring the
  prune's own refuse-rather-than-guess rule.
- The row's tooltip says what it buys: "the placed PCell artwork as it was generated — needed by anyone
  who does not have the kit, and by any command-line run".
- Unticked with a kit PCell referenced and the kit NOT included: the archive dialog warns that the
  recipient will see empty placeholders for those parts.

## 3. `R-gc1-3` — what does not change

- `WorkspaceCopy` (Save Workspace As) keeps its current behaviour — same person, same machine, same
  kits; the shared `IsSkipped` list must not change underneath it. Split the predicate the way
  `.cwsuser` already is split (`IsSkippedFromArchive`), with the comment saying why.
- The workspace `.gitignore` keeps ignoring `.generated-cells/`. History is a separate decision (a
  rebuildable cache in git churns on every generator edit); brief 2 is what makes a clone usable
  headlessly. Note it in RESOLVED as an open question for the owner rather than changing it.
- On extraction, the recipient's GUI open regenerates as today; a regenerated cell with the same
  content hash lands on the same folder name, so an archived cell and a rebuilt one never disagree.

## 4. Tests (minimal)

Archive a workspace with one live and one dead generated cell: the live one is in the zip, the dead one
is not, the row is ticked with its size; unticking removes it; `WorkspaceCopy` behaviour unchanged.
`--filter FullyQualifiedName~WorkspaceArchiveTests` plus the copy tests — never the whole of `Ui.Tests`.

## 5. On completion

The §1 measurement table and the findings go in `src/Ui/RESOLVED.md`, never any `CLAUDE.md`. Update the
archive section of the user doc source (no DocGen run).
