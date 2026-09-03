# Sonnet Brief — Shared cell libraries over a network: overview, findings and decisions

**Read this first; it is the map for `brief-shared-library-1-reaching-the-library.md`,
`-2-read-only-workspaces.md`, `-3-interface-change.md` and `-4-concurrency-and-latency.md`. It contains
no work of its own** — it records what the code does today, decides the questions the four
implementation briefs depend on, and says which parts of the workflow already work so nobody rebuilds
them. Every claim below was read out of the tree; file and line references are given so a later reader
can check them rather than trust them.

**The workflow this series is measured against.** A company maintains one circuitRF workspace of cells
as a shared library, on a network share. Engineers reference cells from it into their own designs
rather than copying them. When the librarian changes a cell, every design that references it picks the
change up. The share is read-only to everyone but the librarian, so nobody can overwrite the masters.
This is an ordinary way for a design group to work, and it is the first workflow that puts several
machines, several people and one set of files in the same picture.

**The verdict, up front: the reference model is right and the file formats are right. Four things break,
one of them on the first click, and none of the four is a redesign.**

---

## 1. What already works, and must not be rebuilt

| Property the workflow needs | Where it already comes from |
|---|---|
| A reference is resolved at load, never baked in | `CellSymbolResolver.Resolve` (`:91`) resolves the `CellRef` on every call; nothing captures a copy |
| The librarian's edit reaches users without a restart | `Resolve` re-stats the `.csym` before it will take a cache hit (`CellSymbolResolver.cs:157`); `CellLayoutResolver` is `(path, mtime)`-keyed |
| Users cannot damage the library from the tree | Rename Cell and Remove Cell refuse on a cell outside this workspace — `WorkspaceViewModel.OwnedByThisWorkspace` (`:10104`), on `WorkspaceRootFinder.IsOutside` |
| Nothing writes into the library during ordinary use | generated PCells go to the **editing** workspace's `.generated-cells` (`GeneratedCellStore.cs:110`); every `.cws` write targets `CurrentWorkspacePath` |
| Relocating the share is one edit, not a rewrite of every file | the `ws://alias/…` form: the alias table in `.cws` is the only place a cross-workspace path is written (`CwsWorkspaceRef`, `WorkspacePersistence.cs:200`) |
| Each user keeping their own copy of the corporate technology works | `ExternalWorkspaceGate` compares the **layer table**, not the resolved path (`workspace-and-project-tree.md` §5C.2 R47a) |
| A write that fails says something true | `FileAccessDiagnostics.TryDescribe` (`src/Diagnostics/`), used at `WorkspaceViewModel.cs:1747`, `:2104` |
| The library is diffable and can live in version control | every format is indented JSON or plain text |

That last row is worth stating as a decision rather than an accident: **the librarian's change history is
git's problem, not circuitRF's.** Nothing in this series adds a revision store, a check-out model or a
cell database — `docs/PRD.md` lists a third-party cell database as out of scope for v1, and CLAUDE.md
requires asking before going near it. Nothing here does.

---

## 2. What breaks

### 2.1 A referenced library or workspace shows only its top-level cells — no recursion

`WorkspaceScanner.ResolveLibrary` (`:311`) and `ResolveReferencedWorkspace` (`:348`) each make one pass:

```
SubDirsSorted(root).Where(d => File.Exists(Path.Combine(d, ".ccell")))
```

The workspace's **own** scan does not stop there — it recurses through `BuildUserFolderNode`
(`WorkspaceScanner.cs:245`), and `workspace-and-project-tree.md` §1.1 explicitly blesses cells inside
user folders at any depth.

So a librarian who organises two hundred cells into `passives/`, `amplifiers/` and `footprints/` — the
first thing anyone does with two hundred cells — publishes a library that renders **empty**. References
that already exist still resolve, because resolution is path arithmetic and never consults the tree; the
library is simply unbrowsable, which is the whole point of a library.

**This is the first click of the workflow, and the fix is small.** It is `brief-shared-library-1`.

### 2.2 There is no read-only concept anywhere in the product

`grep -rn "IsReadOnly\|FileAttributes.ReadOnly" src` returns nothing. Three consequences:

- A document opened from the library is a **foreign document**, and §5A.3 makes those *"fully editable
  and saveable, to their own path"* — deliberately, and correctly, for the case that section was written
  for. Where the librarian has not locked the share down, a user editing a master and pressing Save
  succeeds. Where they have, the same gesture fails at the end, after the work.
- §5C R49 says a kit part inside a referenced cell resolves only when **that cell's own workspace is
  open in a window**, and the repair offered is to open it. Opening a workspace today means writing its
  dock layout, its open-document list and its settled Python interpreter back into its `.cws` — into a
  file the user cannot write. The repair the product recommends is the one the product cannot perform.
- A PCell edited inside a library layout tries to write `.generated-cells` under the **library** root
  (`LayoutEditorViewModel.Instances.cs:41` derives `WorkspaceRootDir` from the document's own ancestor
  workspace, which is right), and the failure is reported as *the parameters* being at fault
  (`LayoutEditorViewModel.PCells.cs:141-146`).

**`brief-shared-library-2`.**

### 2.3 The librarian can break every user's design silently

§4 records that changing a cell's primary symbol is *risky, surfaced rather than blocked* — surfaced as
**dangling wires the user finds on next open**. That bargain is defensible for your own cell in your own
workspace, where you made the change a second ago. It is not defensible across an organisation, where
the person who made the change and the person who discovers it are different people, weeks apart, and
the discovery is a wire that looks connected in a picture and is not connected in the netlist.

There is nothing to pin against, either: `.clib` records a version (§1.3) that **nothing parses**.
`WorkspaceScanner.ResolveLibrary` uses the file's *extension* to locate a folder (`:291`) and never opens
it; there is no `ClibFile` type in the tree.

**`brief-shared-library-3`.**

### 2.4 Concurrency is process-local, and the network path is stat-heavy

- "Already open in another window" (`WorkspaceViewModel.SwitchToWorkspace`, `:1905`) resolves through
  `App.WindowShowing` — windows of **this process**. Two engineers opening one writable workspace on two
  machines are invisible to each other, and both write `.cws` on save: last writer wins, silently, taking
  the alias table and the kit settings with it. Harmless for a read-only library; a real problem the
  moment a *project* is shared.
- Every `CellSymbolResolver.Resolve` costs `Directory.Exists` (`:127`) + `CellFolder.ResolvePrimary`
  (`:134` — a second `Directory.Exists`, a `Directory.GetFiles`, and a `.ccell` read when the folder holds
  more than one symbol) + `File.GetLastWriteTimeUtc` (`:157`) — **before** the cache can hit — and
  `EditableSchematic.BuildRenderModel` re-resolves every component on every model change (`:1176`, and
  that method's own doc comment: *"Called by SchematicViewModel after each model change"*). Four to five
  filesystem round trips per component per edit is free on a local disk, free on a LAN, and several
  seconds per keystroke-scale edit over a VPN.

**`brief-shared-library-4`.**

---

## 3. Decisions

**R-sl-1. A shared library is a referenced WORKSPACE (`ws://`), not a referenced library (`.clib`).**
Both mechanisms exist and both keep working, but only one survives the network. `ExternalCellRef.MakeCellRef`
(`:142`) returns the alias form only for a cell inside a **referenced workspace**; a cell in a referenced
library falls through to `Path.GetRelativePath(baseDir, abs)` (`:167`), which across volumes — a different
drive letter, a share versus a local disk — returns the **absolute path**, baked into every document that
places the cell. That is exactly the failure `workspace-and-project-tree.md` §5A R37 names when it rejects
raw paths. The alias form also *names the workspace*, which is what the technology check and kit resolution
both need, and the archive path (§5C R53) already knows how to carry it. **Point the documentation, the
`File ▸ Reference Workspace…` flow and any future guidance at `ws://`. Do not extend `.clib` references,
and do not remove them.**

**R-sl-2. Read-only is a property of the filesystem, discovered — never a flag in a file.** The librarian
enforces it with share permissions, which is the only place it can actually be enforced. A `ReadOnly: true`
field in `.cws` would be advisory, would have to be maintained by hand, and would be wrong on precisely the
machine where it mattered. circuitRF's job is to **notice and behave**, not to enforce.

**R-sl-3. Noticing is one probe, at open, and a failure to probe means read-only.** `File.GetAttributes`
reports the DOS read-only bit, which says nothing about a share ACL or a POSIX mode; the only portable
answer is to attempt a write and see. One probe per workspace root, cached for the session on the same
terms as the `WorkspaceRootFinder` walk-up and dropped by the same `InvalidateCache`.

**R-sl-4. A read-only workspace writes nothing at all** — not the dock layout, not the open-document list,
not the settled interpreter, not the kit settings, not `.generated-cells`. Fifteen sites call
`WorkspacePersistence.SaveToFileAtomic` today and there is no choke point (reads have one — `TryLoadCws`,
`WorkspaceViewModel.cs:2097`; writes do not). One is needed, or the rule will be true in fourteen places.

**R-sl-5. §5C R49 stands unchanged, and read-only mode is what makes obeying it free.** R49's refusal to
mount another workspace's kits without opening that workspace is right for the reason it gives — it would
make *"which workspace is this part from"* unanswerable. The problem was never the rule; it was that
"open it" implied "write to it". Once a workspace can be **opened read-only**, R49's repair costs nothing
and needs no exception. Do not weaken R49.

**R-sl-6. Version pinning is a PATH policy, not a mechanism.** An alias points wherever the librarian says
it points; `…/stdlib/v2.3/.cws` is a complete answer, needs no resolver, no manifest and no comparison
rules, and lets a group run two versions side by side under two aliases. **Do not build a version
resolver.** The one thing missing to make the policy usable is R-sl-7.

**R-sl-7. A stored cross-workspace PATH may carry a named root; a `CellRef` may never.** One user's
`Z:\eda\stdlib` is another's `\\server\eda\stdlib` and a third's `/Volumes/eda/stdlib`, so a site-wide
`.cws` template is impossible today. Token expansion in the alias's `Path` field fixes that in one place.
It must not reach `CellRef`, which is the workspace-relative remainder and has no business naming a
machine.

**R-sl-8. Cross-machine concurrency is advisory, and must be worded as advisory.** circuitRF cannot lock
a network share reliably across three platforms and must not pretend to. An advisory notice that can be
overridden is honest and useful; a lock the product treats as authoritative becomes a stale file that
locks out a team.

**R-sl-9. Every mechanism in this series applies to ordinary local cells too.** The interface-change
report, the read-only marking and the stat behaviour are not "network features" — the same failures exist
on one machine with a smaller blast radius. Nothing here may be conditioned on a path looking remote;
there is no reliable test for that anyway, and a rule that fires only sometimes is a rule nobody learns.

---

## 4. Sequencing

```
SL1  Reaching the library        ← recursive scan + named roots. Ships alone; unblocks the workflow.
SL2  Read-only workspaces        ← independent of SL1. The safety of the SHARE.
SL3  Interface-change reporting  ← independent of both. The safety of the USERS.
SL4  Concurrency and latency     ← last: it is the only one that trades a guarantee away.
```

**SL1 is independently useful and should land first** — it is the difference between a library that can
be browsed and one that cannot, and it is the smallest of the four. SL2 and SL3 are independent of each
other and of SL1; either can follow. **SL4 is deliberately last**, because its stat cache is the only
change in the series that weakens a property the product currently has (§1's "the librarian's edit reaches
users without a restart"), and it should be made against a measurement rather than an intuition.

---

## 5. Out of scope for the whole series, stated so it is not re-litigated

- **A cell database, a revision store, or check-out/check-in.** `docs/PRD.md` non-goals; CLAUDE.md
  requires asking first. Version control of the library folder is the librarian's tool, not ours.
- **Enforcing permissions.** circuitRF observes the filesystem's answer (R-sl-2). A product-level
  permission model on top of a share that already has one is two sources of truth.
- **Per-instance technology.** Already an explicit non-goal (`ExternalWorkspaceGate`'s own note, §5C.2),
  and nothing here changes it.
- **Live update on a librarian's save** (a `FileSystemWatcher` on the share). Watchers over SMB are
  unreliable, and `TechnologyCache` and `CellLayoutResolver` both already record why circuitRF does not
  use them. The mtime check on resolve is the mechanism, and it is enough.
- **Merging two users' concurrent edits to one file.** SL4 detects and reports; it never merges.
- **Any change to `harmonicaRF` or `wBond`,** which have their own shells and no `WorkspaceViewModel`.

---

## 6. On completion

Findings to `src/Ui/RESOLVED.md` (**not** `CLAUDE.md`; create a `src/Design/RESOLVED.md` entry too if the
work reaches `src/Design/Workspace/`).

Update `docs/design/workspace-and-project-tree.md`: §5 (the `.cws` contents, for named roots), §5C (for
read-only workspaces, which is the missing half of R49's repair) and §4 (for the interface-change state,
which sits beside §4.2's three missing-symbol states without collapsing into them). Where the built thing
differs from what a brief here specifies, **change the brief's claim in the design note and say what
changed and why** — do not leave the note describing something that was not built.
