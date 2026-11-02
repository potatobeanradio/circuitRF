# Sonnet Brief — TM1: Moving cells and files inside the Project Tree

**Read `docs/design/workspace-and-project-tree.md` §4/§5 and
`docs/sonnet-briefs/brief-multi-workspace-3-workspace-dnd.md` first.** MW3 built the drag *gesture* on
this tree and the drop plumbing it needs; this brief adds the one drop MW3 deliberately made inert —
a drop inside the tree the drag started in.

**`brief-tree-move-2-moves-across-a-shared-library.md` is the other half** and does not depend on this
one landing first, but the two share one file format (§7) and should be read together before either is
built.

---

## 0. What the owner asked for

Drag and drop inside the Project Tree, so a workspace can be organised without leaving the application:
a cell or a file into a folder, and out of a sub-folder into a folder above it. A glyph during the drag
saying where the thing will land. A cell's own insides — `schematic/`, `symbol/`, `layout/` and what is
in them — are not rearrangeable. **And, stated separately and as a requirement rather than a wish:
moving a cell inside a workspace must not hurt the cells that reference it.**

That last sentence is the whole engineering content of this brief. The gesture is an afternoon; the
reference repointing is the work.

---

## 1. What already works, and must not be rebuilt

- **The tree is already a drag SOURCE** for cell nodes, `.npy` nodes and loose file nodes
  (`ProjectTreeView.axaml.cs:207-283`). The payloads — `CellDragPayload`,
  `WorkspaceFileDragPayload`, `NpyFileDragPayload` — already carry an **absolute path** and already
  travel on the platform pasteboard, which is why the cross-window case works at all.
- **The tree is already a drop TARGET**, with exactly one `AllowDrop` surface and one pair of handlers
  (`:38-40`, `OnFileDragOver`/`OnFileDrop` at `:365-400`). MW3's R-mw3-3 is explicit that a second drop
  path is the one that gets missed; obey it.
- **`DropFolderFromSource` (`:428-440`) already computes the destination folder from the row under the
  cursor**, with the rule this brief needs: a folder node is itself, a *cell* node resolves to its
  PARENT (a cell folder holds views, not cells), a file resolves to its own directory.
- **`TreeDrop.ForPayload` (`TreeDropIntent.cs`) is already the single rule asked identically by
  `DragOver` and `Drop`**, so the effect the cursor promises and the thing that happens cannot drift.
- **`CellUsageScanner.RewriteCellReferences` already repoints by RESOLUTION, not by name** (MW2
  R-mw2-15), already scans other workspaces open in this process, and already takes a `ws://` reference
  apart with its own parser rather than `Path.GetDirectoryName`.
- **`WorkspaceWritability.IsWritable`** (SL2 R-sl2-1) already answers "can circuitRF write here".
- **`OwnedByThisWorkspace`** (`WorkspaceViewModel.cs:10218`) already refuses a destructive operation on
  a folder that belongs to a referenced library or another workspace.

---

## 2. What breaks — the reason moving was deferred

`WorkspaceViewModel.cs:10434-10441` records the deferral in as many words: moving a cell would have to
rewrite every `CellRef` pointing at it, and `RewriteCellReferences` **matches and rewrites the last path
SEGMENT** — it was built for Rename. It is not a prefix rewriter and it is not a relocator.

That is only half the problem, and the smaller half.

**R-tm1-1. A rename changes ONE set of references; a move changes TWO.** A rename keeps the cell in its
parent folder, so its *depth* is unchanged and every reference stored **inside** the cell —
`../../lib/Rload`, `../Other/x.wBond` — still resolves after the rename with no edit at all. A move
changes the depth, so:

- **inbound**: every reference from elsewhere *into* the moved subtree is now wrong, and
- **outbound**: every relative reference stored *inside* the moved subtree, pointing anywhere outside
  it, is now wrong too.

A move implementation that handles only the inbound half is a move that silently guts the cell it just
tidied away. This is the single most likely way to ship this feature broken, and §4's gate exists
mostly to catch it.

**R-tm1-2. Both halves are one map.** Define, for the operation, a relocation function

```
Relocate(abs) = abs is inside oldRoot ?  newRoot + abs.Substring(oldRoot.Length)  :  abs
```

Then the whole rewrite is: for every path-shaped reference in every affected file, resolve it to an
absolute path **before** the directory is moved, and re-store it afterwards as
`MakeRef(Relocate(target), Relocate(fileDir))`. A referrer and target that moved together produce an
unchanged string and no write; everything else falls out. Do not write two rewriters — a second one is
where the two halves drift apart.

**R-tm1-3. Resolve before, write after.** Path arithmetic needs no filesystem, but `ResolvePrimary`,
the alias table and `Directory.Exists` do, and the alias table is memoised. Capture the resolved
absolute targets while the tree is still in its old shape, then move, then write. `RewriteCellReferences`
already relies on the opposite trick (it runs *after* `Directory.Move` because a stale reference still
spells the old path); do not mix the two conventions in one operation.

---

## 3. The reference inventory, and why it needs a registry

A `CellRef` is not the only path-shaped field, and a move breaks all of them equally. Verified in the
tree today (line numbers are where to start, not a promise the list is closed):

| Field | File | Stored relative to |
| --- | --- | --- |
| `Components[].CellRef` | `.csch` | the `.csch`'s own directory, or `ws://alias/…` |
| `Components[].ImagePath` | `.csch` (`SchematicPersistence.cs:230`) | the `.csch`'s own directory |
| wBond link | `.csch` (`WBondPlacement.cs:121`) | the schematic's directory |
| SnP `File` parameter | `.csch` (`SnpPathPolicy.ToStored`) | workspace root, ≤ 2 levels above; else absolute |
| SPICE model `File` | `.csch` (`SpiceModelSymbolProvider`) | relative, or absolute (both legal) |
| `Instances[].CellRef` | `.clay` (`LayoutModel.cs:585`) | the `.clay`'s own directory, or `ws://alias/…` |
| `ImagePathRef` | `.clay` (`:562`) | the `.clay`'s own directory |
| `TechRef` | `.clay` (`:780`) | the `.clay`'s own directory |
| embedded-geometry `CellRef` | `.wBond` (`WBondGeometryEmbedding.cs:234`) | the design's base directory |
| `LayoutRef` | `.cem` (`EmSetupResolver.MakeLayoutRef`) | **workspace root** when inside it, else absolute |
| `LibraryRefs`, `KnownFiles`, `DefaultTechRef`, `DefaultAssemblyRef`, `PdkRefs[].Path`, `ReferencedWorkspaces[].Path` | `.cws` | workspace root (or absolute) |
| open-document list, `ActiveDocumentPath`, dock-layout document paths | `.cws` (`WorkspaceViewModel.Docking.cs:1216`, `.cs:1735/1760`) | workspace root |

Two entries are already immune and should be confirmed rather than "fixed": **`.cdd` source refs are
relative to the RESULTS ROOT** (`DataDisplayViewModel.ComputeSourceKey`), not to the `.cdd`, so moving a
`.cdd` changes nothing; and `.ccell` primaries are bare file names.

**R-tm1-4. The rewrite is driven by ONE registry of (file glob, JSON path, base-directory rule), and a
format that is not registered is not rewritten.** `CellUsageScanner.ScannedKinds` is already this shape
for two of the rows and is the natural place to grow it. A per-call-site rewrite is how the table above
acquires a fourteenth row that nobody rewrites, and the symptom of a missed row is a dangling reference
in a file the user did not touch — which reads as data loss, not as a missing feature.

**R-tm1-5. A reference that is stored ABSOLUTE stays absolute and is still relocated.** `Relocate` is
defined on absolute paths precisely so the absolute-storage cases (SnP above two levels, a rooted SPICE
model, an absolute `KnownFile`) are not a separate branch.

**R-tm1-6. What a move must NOT do is rewrite a reference that did not move.** The matching rule stays
MW2 R-mw2-15's: compare **resolved absolute paths**, never last segments, never string-prefix matches on
the stored spelling. Two cells legitimately named `R0402` in `parts/` and `board/` is the case the
name-keyed rewriter got wrong, and a prefix rewriter gets `cells/AmpX` wrong when `cells/Amp` moves.

---

## 4. What can be dragged, and where it can land

**R-tm1-7. Movable:** `Cell`, `UserFolder`, and any loose file node the tree already lets you drag —
`OtherFile`, `DataDisplayFile`, `TechFile`, `ColorThemeFile`, `EmSetupFile`, `WBondFile`,
`HarmonicaFile`. This is the same list `GetLooseFileNodeFromSource` already carries plus the two folder
kinds, and it should be *read from one place*, not respelled here.

**R-tm1-8. Not movable, and the drag never starts:** `CellViewFolder` and `ViewFile` — the owner's rule,
and it is also structural. `CellFolder` resolves `schematic/`, `symbol/` and `layout/` by name and the
`.ccell` names primaries within them; a cell whose views have been rearranged is not a cell with a
different shape, it is a cell that no longer resolves. Also not movable: the `Workspace` root, every
synthetic group node (`LibrariesGroup`, `KnownFilesGroup`, `ReferencedWorkspacesGroup`), `NotReadYet`,
and anything under a `Library` or `ReferencedWorkspace` sub-tree — those are someone else's disk, which
is `OwnedByThisWorkspace`'s existing question and brief TM2's subject.

**R-tm1-9. Valid destinations are `Workspace`, `UserFolder`, and — resolving to its parent — a `Cell`.**
`DropFolderFromSource` already computes exactly this and must be reused rather than reimplemented.
Dropping on a `Cell` targeting its parent is MW3's shipped behaviour and stays; it is also the gesture
that makes "drop next to that cell" work without the user having to aim at whitespace.

**R-tm1-10. Four refusals, each with its own message, each answered in `DragOver` so the cursor already
says no:**
1. the destination is inside the moved subtree (moving a folder into itself);
2. the destination already holds an entry of that name;
3. the source or the destination workspace is not writable (SL2 R-sl2-1);
4. the destination is not owned by this workspace.

A refusal that only appears on release has already cost the user the gesture.

**R-tm1-11. Single selection only.** `TheTreeView` has no `SelectionMode` set, so it is `Single`; one
node per drag. Multi-select move is out of scope and should not be smuggled in — it multiplies §4's
partial-failure states, which §6 below already treats as the hard part.

---

## 5. The drop indicator

**R-tm1-12. Highlight the destination ROW; do not draw an insertion line.** The tree is *sorted*, not
user-ordered — `FilteredChildren` is rebuilt by the scanner on every refresh — so an insertion caret
between two rows would promise an ordering the tree cannot keep, and the first thing the user would do
after dropping is watch the row jump elsewhere. A row highlight promises exactly what the operation
delivers: *this folder*.

**R-tm1-13. The highlight is on the row that will actually receive it**, which for a drop on a cell is
that cell's parent — so a hover over `Amp` inside `cells/` highlights `cells/`. An indicator that
highlights what is under the cursor rather than what will receive the drop is worse than none, because
it teaches a rule that is false.

**R-tm1-14. Effect: `Move` inside one workspace, `Copy` across two.** MW3's cross-workspace drop is a
copy-or-reference prompt and keeps `DragDropEffects.Copy`; this one is a move and must say so — that is
free platform-native feedback (the cursor badge) on all three OSes and it is the difference the user
most needs to see.

**Implementation note, not a rule:** the highlight belongs on the `TreeViewItem` as a pseudo-class or a
bound `Classes.` trigger driven from a single "current drop target" property on the tool, in the same
shape as the existing `pt-warning` / `pt-bold` styles (`ProjectTreeView.axaml:237-252`). Do not adorn
with a second overlay layer.

---

## 6. Doing the move safely

**R-tm1-15. Open documents under the moved subtree are saved and closed first**, exactly as
`RenameCellAsync` does (`WorkspaceViewModel.cs:10876-10892`) — `PromptSaveBeforeClose`,
`ForceCloseDockable`, then `RetireSessionIfUnreferenced` / `RetireLayoutSessionIfUnreferenced`. Reuse
that block; do not write a second one. Cancelling the save prompt cancels the move.

**R-tm1-16. `Directory.Move` first, rewrite second, and a rewrite failure is REPORTED, never rolled
back.** This is Rename's shipped bargain and it is the right one: a partial rewrite leaves references
that a re-run repairs, whereas an attempted rollback moves the folder back underneath references that
were already updated. Collect the failures and surface every one (`Messages.Warning` per file, as Rename
does), and state the count of rewritten files.

**R-tm1-17. Reopen what was closed, at its new path.** Rename does not do this and it is a small
annoyance there; here it is a big one, because organising a workspace is a dozen moves in a row. If
reopening turns out to be more than reusing the existing open path with `Relocate` applied, **stop and
report it** rather than growing the brief.

**R-tm1-18. One `Refresh()` at the end**, not one per rewritten file.

**R-tm1-19. Undo is a re-drag, and the message says so.** There is no in-app undo for Rename or Remove
Cell either (`ITreeActions.RemoveCellAsync`'s own doc comment says so). Do not invent a move-specific
undo stack; do make the success message name both the old and the new location, because that sentence is
what lets a user put it back.

---

## 7. The part that reaches outside this process

**R-tm1-20. Rewrite what is reachable, and record the move for what is not.** `RewriteCellReferences`
scans this workspace plus every other workspace open **in this process** — `CellUsageScanner`'s own doc
comment already states the limit: *a referrer in a workspace nobody has open still cannot be found*.
Within one workspace that limit is invisible. It stops being invisible the moment another workspace
references this one through `ws://alias/…`, because that remainder is **this** workspace's relative
spelling and the move just invalidated it, in a file on someone else's disk.

So every move also **appends a redirect record** to the moving workspace's own redirect file: old
workspace-relative path → new workspace-relative path, with a timestamp. It costs one small write, it is
the same file and the same format brief TM2 defines and consumes, and it is what turns "the librarian
tidied up on Tuesday" from a broken design into a message. **Write it unconditionally**, including for a
move inside a workspace nobody shares — a workspace that is private today is referenced next month, and
a redirect that was never written cannot be reconstructed.

The format, the resolution rule, the reporting and the repair gesture are all TM2. This brief's
obligation is only to *write the record* and to keep the writing in one place.

---

## 8. Gate

Headless, in `tests/Ui.Tests`, over real temp-directory workspaces — the same shape
`RenameCellAsync`'s existing tests use.

1. **Inbound.** `A` references `B`; move `B` into `sub/`; `A`'s stored `CellRef` resolves to `B` at its
   new path, and `A` still renders `B`'s pins. Repeat with the referrer in a `.clay`.
2. **Outbound — the one that catches R-tm1-1.** `B` references `C`; move `B` into `sub/`; `B`'s own
   `CellRef` for `C` still resolves to `C`, which did **not** move. This test fails against any
   implementation that only extends the Rename rewriter.
3. **Both, together.** Move a `UserFolder` containing `B` and `C` where `B` references `C` and `A`
   (outside) references `B`: `B`→`C` is byte-identical afterwards (they moved together), `A`→`B` is
   rewritten.
4. **The near-miss.** `parts/R0402` and `board/R0402`, each referenced; move one; assert the other's
   referrer is **untouched, byte for byte**. Then the prefix variant: `cells/Amp` and `cells/AmpX`, move
   `cells/Amp`, assert `AmpX`'s referrers are untouched.
5. **`ws://` from a second open workspace** is repointed in the same pass (the MW2 R-mw2-15 path, now
   exercised by a move), and a **redirect record is written** for the same move (R-tm1-20).
6. **Every registered field.** One test per row of §3's table: a `.csch` bitmap, a `.clay` `TechRef`, a
   `.cem` `LayoutRef`, a wBond link, a `.cws` `KnownFile`, the open-document list. Assert the file
   resolves after the move. A row with no test is a row that will regress.
7. **Refusals** (R-tm1-10): each of the four is answered by `TreeDrop`'s own rule — asserted on the rule,
   not through the view — so `DragOver` and `Drop` cannot disagree.
8. **Cell insides are not draggable** (R-tm1-8): `CellViewFolder` and `ViewFile` produce no drag payload.
9. **A read-only workspace refuses the move and writes nothing** (SL2 R-sl2-5), including no redirect
   record.

---

## 9. Stop and report

- If §3's registry turns out to need restructuring beyond `ScannedKinds` — for instance if any of the
  path-bearing fields is written by a call site that does not go through a shared producer — **report the
  list of unshared producers and stop.** That list is a real finding about the codebase and it is worth
  more than a rushed fourteen-branch rewriter.
- If R-tm1-17 (reopen after move) needs more than applying `Relocate` to the closed documents' paths,
  leave it out, say so, and ship the rest.

---

## 10. On completion

Findings to `src/Ui/RESOLVED.md` (**not** `CLAUDE.md`).

Update `docs/design/workspace-and-project-tree.md`: §4's rename section gains the move rule and the
inbound/outbound distinction (R-tm1-1), and the "moving is done in the file manager" note in
`WorkspaceViewModel.cs:10434-10441` must be deleted rather than left contradicting the code beneath it.

**Report, do not silently absorb:**
- The final §3 table as built — every row, its base-directory rule, and whether it was already routed
  through a shared producer or had to be. If the shipped table differs from the one above, the
  difference is the finding.
- Whether gate 2 (outbound) failed first against a naive extension of the Rename rewriter. If it passed
  immediately, say so — it means the assumption behind R-tm1-1 was wrong and that is worth knowing.
- Any path-bearing field discovered that is **not** in §3's table.
