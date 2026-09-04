# Sonnet Brief — TM2: When the librarian moves a cell someone else is using

**Read `brief-shared-library-0-overview.md` §2.3 and `brief-shared-library-3-interface-change.md`
first** — this brief is the same failure with a different cause, and it must reuse SL3's answer rather
than invent a parallel one. **Read `brief-tree-move-1-moving-within-a-workspace.md` §7** for the write
side of the file defined here.

---

## 1. The question

A librarian tidies a shared library — drags `Amp` from the library root into `rf/`. Forty designs across
the organisation reference that cell. What happens to them?

Today: **they break, silently, later, on someone else's machine.**

The mechanism is not subtle. A cell in a referenced LIBRARY is stored as an **ordinary relative path**
from the referencing `.csch` to the cell folder — `ExternalCellRef.MakeCellRef`'s own doc comment states
this and states why (a library is not a workspace; it brings no technology and no kit set, so the
`ws://` alias form is deliberately not used for it). A cell in a referenced WORKSPACE is stored as
`ws://alias/rel`, where `rel` is **that workspace's own relative spelling**. Both forms name a *place*.
Move the cell and both name a place that is empty.

The repair pass cannot reach them. `CellUsageScanner.RewriteCellReferences` scans the moving workspace
plus every workspace open **in this process**, and its own doc comment already names the limit: a
referrer in a workspace nobody has open cannot be found. The librarian's process does not have the
users' workspaces open, has no right to write into them, and in general cannot see them at all.

**This is not a defect in TM1's rewriter. It is a class of reference the rewriter is structurally unable
to reach, and the answer has to be a different mechanism.**

---

## 2. What NOT to do

**R-tm2-1. Do not refuse the move.** Organising the library is the librarian's job, and a library that
cannot be reorganised becomes a flat list of four hundred cells, which is the problem the owner asked to
fix. A refusal here would also be inconsistent: renaming a library cell has exactly the same blast
radius and ships today.

**R-tm2-2. Do not chase the referrers.** A "scan the network for workspaces that reference this" pass is
unbounded, slow, and wrong on its own terms — the workspace that matters may be on a laptop that is
closed. SL0 §2.4 already establishes that the network path is stat-heavy and that a shared library must
not be walked more than it already is.

**R-tm2-3. Do not put a copy of the library's structure in the user's file.** SL3 R-sl3-3 refused
exactly this for the interface signature ("a second source of truth, which is the thing every reference
form in this codebase is built to avoid") and the argument is unchanged.

---

## 3. The mechanism: the mover leaves a forwarding record

**R-tm2-4. A move appends a redirect record to the redirect file at the root of the workspace or library
that OWNS the moved cell.** The mover always has write access to that root — it is the thing being
moved — so the record can always be written exactly when the move can happen. Nothing else needs write
access to anything.

**R-tm2-5. The file is `.cmoves` at that root, JSON, one flat append-only list.**

```json
{ "FormatVersion": 1,
  "Moves": [
    { "From": "Amp",           "To": "rf/Amp",       "When": "2026-09-03T14:02:11Z" },
    { "From": "rf/Amp",        "To": "rf/pa/Amp",    "When": "2026-11-20T09:41:00Z" }
  ] }
```

Paths are **root-relative, forward-slash**, the same convention `WorkspaceRefs.ToStoredRef` normalises
to. A separate file rather than a `.cws` section, for one decisive reason: **a referenced library need
not be a workspace and often has no `.cws` at all** — `WorkspaceScanner.ResolveLibrary` accepts a bare
directory (`:324-328`), and `.clib` is a manifest that *nothing in the tree parses*. A redirect that only
worked for libraries that happen to be workspaces would work in testing and fail in the field.

**Add it to `WorkspaceScanner.IsHiddenTreeFile`.** That predicate is an explicit opt-in list, **not a
dotfile rule** — its own doc comment says so, and `.cws-lock` had to be added to it by name for exactly
this reason. Left out, `.cmoves` renders as a row in every workspace and every library sub-tree.

**R-tm2-6. Append-only, never pruned automatically.** A design authored three years ago is exactly the
one that needs the record. The file is small (one line per move, ever) and read once per root. Pruning
is a librarian's explicit act, and it is honest about what it costs: it breaks every reference older
than the entries removed.

**R-tm2-7. It records folder moves, not just cell moves,** as `From`/`To` of the moved root. A reference
into a cell inside a moved folder resolves by longest-prefix match — one record covers the whole
subtree, which is what keeps a fifty-cell reorganisation from writing fifty records.

---

## 4. Resolution

**R-tm2-8. The redirect is consulted only when direct resolution finds NOTHING.** `ExternalCellRef.
ResolveCellDir` is already the one resolution point for every path-shaped cell reference — its own doc
comment says a call site that splits the forms itself is a call site that will be missed. The redirect
goes there and nowhere else.

Order, and it is not negotiable:
1. resolve the reference as today;
2. if the folder **exists**, that is the answer — done;
3. only then, find the workspace/library root above the resolved path
   (`WorkspaceRootFinder`), read its `.cmoves`, longest-prefix-match the root-relative remainder,
   and retry.

Step 2 before step 3 is what makes the mechanism safe when a **new** cell is later created at the old
path: the new cell wins, the redirect never fires, and the reference means what it says. A redirect
consulted first would silently reroute a live reference to a different cell.

**R-tm2-9. Redirects chain, with a hop cap and a cycle guard.** `Amp → rf/Amp → rf/pa/Amp` must resolve
in one call. Cap the walk (8 hops is generous), and stop on a repeat. A `.cmoves` that has been
hand-edited into a cycle must produce `NotFound`, not a hang.

**R-tm2-10. Resolution through a redirect is memoised beside the alias table**, in
`ExternalCellRef`'s existing memo, and dropped by `WorkspaceRootFinder.InvalidateCache` — which SL2
R-sl2-3 already establishes as the one place per-root memos live and are dropped together. A third memo
with a lifecycle of its own is the one that goes stale. This matters: SL0 §2.4 measured four to five
filesystem round trips per component per edit, and a `.cmoves` read per unresolved reference per
`BuildRenderModel` would be a fifth.

---

## 5. It resolves, and it is REPORTED

**R-tm2-11. `Moved` is a fifth `CellSymbolState`, and it does not collapse into `Resolved`.** SL3
R-sl3-7 made the identical argument for `InterfaceChanged` and the argument holds unchanged: the cell
resolves, the symbol is right, the drawing is right — **and the file on disk says something that is no
longer true.** Silently resolving and saying nothing would mean a workspace could drift arbitrarily far
from its stored references, with the `.cmoves` chain as the only thing holding it together.

**R-tm2-12. The report reuses SL3's three surfaces and adds none:** the Messages panel on open — **one
line per moved cell, not per instance** (forty instances of one moved cell is one problem, which is
SL3 R-sl3-9 verbatim); the instance's Properties inspector; and the §5C R51 chrome marking for a
resolved external reference. **Not the rendered geometry** — R36 holds without exception.

The line says where it went and when: *"`Amp` moved to `rf/Amp` in library `StdParts` on 2026-09-03; 12
instances in 3 cells still reference the old location."*

**R-tm2-13. Adopting the new path is one explicit gesture and is never automatic.** *"Update references"*
— on the message, and on the instance — rewrites the user's own files through TM1's rewriter, against
the user's own workspace, which the user can write. Not on open, not on save, not as a side effect of an
edit. SL3 R-sl3-10 makes the same rule for the same reason: the stored reference is the only evidence
that the design was authored against a different library layout, and erasing it on open implements
nothing.

**R-tm2-14. A reference that resolves through a redirect is NOT a warning colour.** SL4 R-sl4-11 already
draws this distinction for the not-read-yet placeholder: an expected, correct state that happens to be
worth mentioning must not be coloured like a problem, or users learn to ignore the colour that also
marks real breakage. `Moved` is informational; `NotFound` stays the warning.

---

## 6. The cases this does and does not cover

Covered, and it should be said plainly because the value of the mechanism is in the third row:

| Situation | Result |
| --- | --- |
| Librarian moves a cell; user's workspace open in the same process | Rewritten immediately by TM1; the record is written too and never needed |
| Librarian moves a cell; user opens their workspace next week | Resolves through `.cmoves`, reported, one gesture to adopt |
| Librarian moves a cell; user never opens the design again, then simulates it from the CLI a year later | Resolves through `.cmoves` — **the CLI path gets this for free**, because the redirect lives in `ExternalCellRef` and `src/Cli` already resolves through it |
| Two librarians move the same cell from two machines | Both append; last writer wins on the file, and the losing record is lost — see R-tm2-15 |
| Librarian moves a cell, then someone creates a NEW cell at the old path | The new cell wins (R-tm2-8); no redirect fires |
| The library itself is relocated | Not this mechanism — that is the `LibraryRefs` / alias repair that already exists |
| A user copies a library cell into their own workspace and the librarian then moves the original | Nothing to repair; the copy is theirs |

**R-tm2-15. `.cmoves` is written under the same advisory lock SL4 defines for `.cws`, and a lock that
cannot be taken means the record is not written and the move is REFUSED.** A move whose forwarding
record was lost is worse than a move that did not happen: the first breaks forty designs quietly, the
second is a message the librarian reads immediately. This is the one place in the feature where refusing
is correct, and it is the opposite of R-tm2-1 on purpose — R-tm2-1 refuses to block the *organising*,
this refuses to complete a move whose safety net could not be laid.

**R-tm2-16. Not covered, and stated so it is not re-litigated:** a reference that was already broken
before this feature existed. There is no record for a move made in a file manager last year, and there
cannot be. The mechanism starts recording when it ships; TM1's `NotFound` reporting is what those get,
which is what they get today.

---

## 7. Gate

Headless, `tests/Ui.Tests`, real temp directories.

1. **The core case.** Workspace `U` references cell `Amp` in library `L` by relative path. Move `Amp` to
   `L/rf/Amp` **with `U` closed**. Open `U`: the instance resolves, renders its real pins, and reports
   `Moved` once — not once per instance.
2. **`ws://` variant.** The same, where `U` references workspace `W`'s cell through an alias and the
   move happens in `W`.
3. **Existence wins.** After the move, create a new cell at `L/Amp`. `U`'s reference now resolves to the
   NEW cell, the redirect does not fire, and nothing is reported as moved.
4. **Chains.** Two successive moves resolve in one call; a hand-written cycle produces `NotFound` and
   terminates.
5. **Longest prefix.** Move a folder containing three cells; one record; all three resolve.
6. **Adoption is explicit.** Opening, rendering, editing an unrelated part of the schematic and saving
   leaves the stored `CellRef` **byte-identical**. Invoking *Update references* rewrites it, and the
   report then goes quiet.
7. **The CLI.** `circuitrf sparam` on a netlist whose cell reference only resolves through `.cmoves`
   runs and produces the same result as before the move. This is the row of §6's table that justifies
   putting the redirect in `ExternalCellRef` rather than in the UI.
8. **No new hot-path cost.** Count filesystem calls per component per `BuildRenderModel` for a design
   with **no** moved references: unchanged from before the change. (SL0 §2.4 is the standing measurement;
   this asserts the redirect adds nothing to the common path — step 2 of R-tm2-8 short-circuits.)
9. **A move whose record cannot be written is refused** (R-tm2-15), and the library is left untouched.

---

## 8. Stop and report

- `WorkspaceLock` (SL4) **is** already in the tree — `src/Design/Workspace/WorkspaceLock.cs`, one fixed
  lock-file name per workspace root. Use it; do not invent a second locking scheme. If it turns out not
  to cover a *library* root that is not a workspace, report that gap and write `.cmoves` with an atomic
  replace in the meantime, noting that R-tm2-15's refusal is not yet enforceable there.
- If gate 8 shows the redirect *does* add filesystem calls on the common path, **stop and report the
  measurement** rather than adding a cache — SL4 §5 makes the same call for the same reason, and a cache
  here would be paying a weakened guarantee for a cost that may not exist.

---

## 9. On completion

Findings to `src/Ui/RESOLVED.md` (**not** `CLAUDE.md`).

Update `docs/design/workspace-and-project-tree.md` §5 with the `.cmoves` format and the resolution
order, and `docs/design/project-file-formats.md` with `.cmoves` as a workspace-family file — it is a new
on-disk format and that document is where the family is enumerated.

**Report, do not silently absorb:**
- Whether any resolution site was found that does **not** go through `ExternalCellRef.ResolveCellDir`.
  That doc comment claims all of them do; if it is wrong, that is the most important thing this work
  finds, because it is also a latent MW2 bug.
- The measured cost of gate 8, both numbers.
- Whether `Moved` genuinely needed to be a fifth state, or whether it could be carried on the existing
  three without loss. If the latter, say so with the evidence — R-tm2-11 is an argument, not a
  measurement.
