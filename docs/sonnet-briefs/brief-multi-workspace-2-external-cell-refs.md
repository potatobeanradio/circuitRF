# Sonnet Brief — MW2: Referencing a cell in another workspace

**Read `brief-multi-workspace-0-overview.md` first, and land `brief-multi-workspace-1-windows.md` before
this.** MW2 depends on MW1's per-workspace kit scoping (R-mw1-4/-5) and on its file-level open guard
(R-mw1-10); neither is re-specified here.

**Scope: the reference itself** — creating one on purpose, resolving it correctly, showing honestly when
it cannot be resolved, and not silently corrupting or dropping it. **The drag-and-drop gesture is MW3.**

---

## 1. It already half-works, and that is the problem

`CellRef` is written as `Path.GetRelativePath(schematicDir, cellAbsDir)` at every producing site and read
back as `Path.GetFullPath(Path.Combine(baseDir, cellRef))` (`CellSymbolResolver.cs:117`,
`HierarchyResolver.cs:24`). **Nothing rejects a `../../OtherWorkspace/cells/Amp` form**, so if one gets
into a file — and MW3's drag-drop, or a hand edit, or a moved folder will put one there — the symbol
resolves, the pins resolve, the parameter interface resolves and push-in navigation works.

Everything *around* it is wrong, and the reference FORM itself was never chosen — it is what
`GetRelativePath` happens to produce. §2 chooses one; §3-§8 fix what surrounds it.

**R-mw2-1. An external cell reference is a first-class, supported thing after this brief, or it is
rejected at the point of creation. It must not stay half-supported.** If any section below turns out to
cost more than the brief's "stop and report" threshold, the honest fallback is to refuse the creation
gesture and report why — not to ship a reference that renders and silently misbehaves.

---

## 2. The reference form — answering the question §5A R37 deferred

`workspace-and-project-tree.md` §5A R37 does not merely leave cross-workspace instancing unbuilt; it
**names the decision that has to be made first**: *a named library alias resolved through `.cws`, versus a
raw path recorded in every file*, and it warns that raw paths mean relocating the other project breaks
every document that referenced it. It also says, correctly, that this must not be answered by accident as
a side effect of some other feature. **This brief answers it deliberately.**

**R-mw2-2. An external reference is an ALIAS resolved through the referencing document's own `.cws`, not a
raw relative path.**

```
CellRef:   ws://RfFrontEnd/cells/Amp
.cws:      ReferencedWorkspaces: [ { Alias: "RfFrontEnd", Path: "../rf-front-end/.cws" } ]
```

Five reasons, in the order they matter:

1. **Relocating the other project is one `.cws` edit**, not a rewrite of every document that referenced it
   — which is precisely R37's stated concern.
2. **It reuses a scheme circuitRF already has.** `pdk://kitName/partId` (`PdkKitRegistry.cs:78`) is the
   same shape for the same reason: *"a reference that states its own kind cannot be mistaken for a typo"*
   (that file's own note). A raw `../../` path is indistinguishable from a mistake, so no repair flow can
   say anything useful about it.
3. **It names the other workspace explicitly**, which is exactly what §3's technology check and §4's kit
   resolution both need. A raw path would make them infer the workspace by walking up from a resolved
   path — an inference that fails silently when the path is stale.
4. **The `.cws` already has the slot.** §5 records *Referenced libraries* — "relative-or-absolute paths to
   external library folders" — and this is the same kind of entry for a workspace rather than a `.clib`
   folder. It stays *configuration, never membership*, which is §5's governing rule.
5. **The Project Tree already knows how to draw it.** §3.1 renders each referenced library as its own
   sub-tree; §3.2 already renders an unresolvable one as System.Warning + italics with a reason tooltip.
   That is most of the marking §5 of this brief needs, already designed and already built for libraries.

**R-mw2-3. The path inside the target workspace is workspace-relative, not a second convention.**
`ws://<alias>/<workspace-relative path to the cell folder>`. The alias resolves to a `.cws`; the remainder
resolves under that workspace's root exactly as `WorkspaceRefs` already resolves a workspace-relative path
(`WorkspaceRefs.cs:39`).

**R-mw2-4. The alias entry in `.cws` stores the path to the other `.cws`, relative where it can be**
(`GetRelativePath`), absolute across volumes — the behaviour `WorkspaceRefs.cs:77` and
`SnpPathPolicy.cs:56` already document. **It is the ONLY place a cross-workspace path is written**, which
is what turns R37's "relocating breaks everything" into a one-line repair.

**R-mw2-5. A raw `../../Other/cells/Amp` `CellRef` is NOT blessed by this brief.** It resolves today by
accident (§1) and will go on resolving — removing that would break nothing anyone has — but producing one
would create the second convention R37 warns against. Leave it working, never write one, and do not
document it as a feature.

### 2.1 Creating one

**R-mw2-6. Two entry points, and no more:**

1. **`File ▸ Reference Workspace…`** — pick another workspace's `.cws` and name the alias (defaulting to
   its folder name). It appears in the tree as a sub-tree beside the referenced libraries, and cells inside
   it then place exactly like any other tree cell.
2. **The MW3 drag-drop gesture**, whose "Reference" outcome creates the alias if it does not exist yet and
   reuses it when it does.

The palette and every existing placement path stay **workspace-scoped**. An external reference is a
deliberate act, never something a user arrives at by not noticing.

---

## 3. Technology — the hazard that must be settled before anything else

**A layout's whole instance hierarchy is compiled against ONE technology**: `CompileCell(subView, tech,
…)` passes the same `Technology? tech` down every level (`LayoutRenderer.Instances.cs:232`, `:279`,
`:495`), and layer lookup is a flat `tech?.Layers.ToDictionary(l => l.Key)` (`:452`). So an external
cell's shapes are drawn using the **host** workspace's layer table, matched by numeric layer key.

This is exactly the collision `brief-foreign-documents.md` §2 already named: both starter technologies use
keys `(1,0)`–`(8,0)`, so a Drill shape from workspace A silently becomes Substrate in workspace B — right
colours, right geometry, wrong meaning, nothing missing and no warning.

**R-mw2-7. An external cell reference is only permitted when the two workspaces resolve to the SAME
technology**, compared by the resolved `.ctech` absolute path (`TechnologyResolver.ResolveForDocument`,
which already walks up to the referencing document's own ancestor `.cws` —
`TechnologyResolver.cs:103-110`).

- **Same `.ctech`** → permitted, no marking needed on technology grounds.
- **Different `.ctech`** → **refuse the reference at creation**, naming both technologies and both
  workspaces. Offer the two routes the user actually has: copy the cell instead (MW3), or change one
  workspace's technology (`Change Technology…` already exists — L1g).

**Do not attempt per-instance technology.** Rendering a sub-hierarchy under a different layer table is a
substantial change to `CompileCell`'s signature and its caching, it makes DRC's meaning ambiguous, and it
makes a single layout view mean two different things at once. It is a real feature, and it is not this
one. **Record it as an explicit non-goal in the completion note** so the next reader does not assume it
was overlooked.

**Schematics have no equivalent hazard** — a schematic carries no technology — so a schematic-only
external reference is unaffected by R-mw2-7. Gate on the technology only when a **layout** view is
involved.

---

## 4. Kits inside the referenced cell

MW1's R-mw1-5 already settles the mechanism: **a `pdk://` reference resolves against the referencing
document's own parent workspace**, found by `WorkspaceRootFinder.FindAncestorCws`.

That rule composes with §2 without a special case, and it is worth seeing why: an alias resolves to a cell
folder that physically sits inside workspace A, so walking up from *that* document lands on A's `.cws` —
the same answer the alias itself names. **Two routes, one answer**, which is what makes the alias an
addressing convenience rather than a second source of truth. Nothing new is needed here; what is needed is
that the three consequences are correct and *tested*:

**R-mw2-8.** A cell in workspace A that places a kit part resolves that part when **A is open in some
window** — the window it is referenced *from* need not have that kit. This is the owner's "could we load
the referenced cell's PDK into memory?", and the answer is that MW1 already loads it, in A's own scope.

**R-mw2-9.** A referenced cell whose workspace is **not open** has its kit parts unresolved. That is
`NotFound`, the existing reported and repairable state (`CellSymbolResolver.cs:90-95`), and it is repaired
by opening that workspace. **Do not offer to mount the kit without opening the workspace** — mounting a
kit is a side effect the user did not ask for, and it would make "which workspace is this part from" an
unanswerable question.

**R-mw2-10.** A referenced cell with **no ancestor `.cws` at all** that contains a kit part is a permanent
error, exactly as the owner said. There is no workspace to resolve against and no guess worth making
(unlike a missing technology, where `brief-foreign-documents.md` R-fgn-4's three routes exist because a
`.ctech` can be chosen; a kit cannot be chosen — its identity is the reference). It renders as a bad cell,
§5.

---

## 5. The bad-cell state, and marking

The placeholder already exists — `CellSymbolResolution.NotFoundResult` draws a pin-less box — and it is
what R-mw2-9 and R-mw2-10 land on. What is missing is that it says nothing about *why*, and an external
reference has three distinct failure modes a user must be able to tell apart.

**R-mw2-11. Three states, each visually distinct and each explaining itself in the Properties panel and
in Messages:**

| State | Cause | Repair offered |
|---|---|---|
| **Resolved (external)** | fine, but not from this workspace | none — marked only |
| **Unresolved — workspace not open** | R-mw2-9 | "Open <workspace> in a new window" |
| **Broken — path does not resolve, or an unowned cell with kit content** | R-mw2-10, a moved/deleted folder | "Locate…" / "Copy into this workspace" |

**R-mw2-12. Mark the chrome, never tint the geometry** — the same rule and the same reason as
`brief-foreign-documents.md` R-fgn-7: layer colours are literal user-authored `Rgba` so a layer looks the
same here as in the tool the artwork came from, and tinting it corrupts the one thing the reference exists
to show. Mark the instance's **selection/annotation chrome**, its Properties header, and its Project Tree
entry if it gets one.

**R-mw2-13. A resolved external reference is marked too, not only a broken one.** A user must be able to
see, without clicking, that a cell in their layout is not theirs — that is the whole safety story for a
reference, and it is the difference between this feature and a trap. Name the source workspace where there
is room to (`Amp — [RfFrontEnd]`, the convention `brief-foreign-documents.md` R-fgn-7 already set for
title bars).

---

## 6. Workspace operations must not corrupt external references

Three of them do today, and the third is a live bug even without this brief.

**R-mw2-14. `CellUsageScanner.CountReferencingCells` cannot see an external referrer.** It enumerates cell
folders under `workspaceRootDir` only (`CellUsageScanner.cs:190-200`). So deleting a cell in A that B
references reports "0 references", deletes it, and breaks B with nothing said. **Extend the count to every
OPEN workspace**, and word the confirmation to name the other workspace. A referrer in a workspace that is
not open cannot be found and must not be claimed to be absent: the confirmation says "no other open
workspace references this," not "nothing references this."

**R-mw2-15. `CellUsageScanner.RewriteCellReferences` matches on the LAST PATH SEGMENT, not the resolved
path** (`CellUsageScanner.cs:184-190`). Renaming cell `Amp` rewrites **every** `CellRef` whose last
segment is `Amp` — including one that points at a *different* `Amp` which was never renamed.

**This is a documented limitation, not an undiscovered bug**, and the framing matters.
`workspace-and-project-tree.md` §4.1 states the last-segment match deliberately — it is what a rename
changes — and names its own consequence: *"a name-keyed rewriter cannot tell `parts/R0402` from
`board/R0402`."* Today that is a bounded, accepted risk, which is why in-tree **move** was deferred rather
than built.

**External references take it from bounded to corrupting**, because `ws://Other/cells/Amp` also ends in
`Amp` and repointing it produces a reference to something that does not exist in a workspace the rename
had no business touching. So R-mw2-15 is not "fix a bug we found" — it is **the price of admission for
§2**, and it must be paid in this brief or §2 cannot ship.

Fix by resolving each `CellRef` (alias-expanded, per §2) against the file's own directory and comparing to
the target's absolute path, exactly as `FileReferencesTarget` (`CellUsageScanner.cs:79-88`) already does
correctly in the same file. **This also closes the same-named-cells case §4.1 called out**, which removes
one of the two blockers that section lists for an in-tree move — say so in the completion note, and say
that the *other* blocker (a move changes the path prefix, which the rewriter cannot express) is untouched.
Update §4.1 of the design note accordingly rather than leaving it describing a rewriter that no longer
works that way.

**R-mw2-16. The workspace archive drops external cell references silently.** `DocumentFileRefs` recognises
a reference by whether the string resolves to a **file that exists** (`DocumentFileRefs.cs:152`, `:163`),
and a `CellRef` names a **directory** — so the archive scan never sees it, the dialog never offers it, and
the recipient gets an archive whose layouts reference nothing. That is the same failure mode the
`SnpPathPolicy` note in that file's own header records for Touchstone files, arriving through a new door.

Required: the scan reports external cell references as a listed row, and the writer copies the referenced
cell folder (with its own sub-cells, §7) into the archive and repoints the reference. **A `pdk://`
reference inside a copied external cell travels only as the kit row it already belongs to** — do not
invent a second kit-packaging path.

---

## 7. Sub-cells

An external cell has its own hierarchy, and each level's `CellRef` is relative to *that* level's folder —
so the whole sub-tree resolves automatically as long as the referenced cell's own workspace is intact.
**R-mw2-17. A reference is to one cell; its sub-cells come along by reference, always.** There is no
"reference this cell but copy its sub-cells" mode — it would produce a cell whose contents disagree with
its source. The copy-vs-reference choice for sub-cells belongs to the **copy** gesture only, and that is
MW3 §4.

---

## 8. Elaboration, simulation and the CLI

`NetExtractor` resolves a cell reference through the same path-based mechanism
(`NetExtractor.cs:1963` — `BuildCellRefResolutions`), so an external reference elaborates today.

**R-mw2-18. A `.cnl` / headless run must behave identically to the GUI, including the refusals.** The CLI
has no window and no other open workspace, so R-mw2-9's "workspace not open" cannot apply there — a
kit-bearing external cell must resolve from its **own** workspace's `.cws` on disk, by the same walk-up,
or refuse with a sentence naming the workspace and the kit. Silently elaborating a design with an
unresolved device is the failure this whole section exists to prevent. See `docs/design/cli.md` for the
stdout/stderr split and the exit-code convention.

---

## 9. Gate

`tests/Ui.Tests` (do not touch `src/Core`, `src/Engine`, `RfCore`):

1. **Two-workspace fixture on disk** — A and B, sharing one `.ctech`, B referencing a cell in A. Assert
   the symbol, the pins, the `.ccell` interface and push-in navigation all resolve; assert the rendered
   layer table is the shared one.
2. **The technology refusal** (R-mw2-7): the same fixture with two different `.ctech` files refuses, and
   the message names both.
3. **The kit rules** (R-mw2-8/-9/-10), all three, as three separate tests — including the unowned-cell case,
   which must resolve `NotFound` and not throw.
4. **`RewriteCellReferences` does not repoint a same-named external cell** (R-mw2-15). Write this one
   against the **current** code first and watch it fail; it is a pre-existing defect and the test is the
   proof.
5. **`CountReferencingCells` sees an open external referrer** (R-mw2-14).
6. **Archive round-trip** (R-mw2-16): archive B, extract to a fresh directory with A absent, and assert
   the layout still renders the referenced geometry.
7. **`Cli elab` on the same fixture** matches the GUI's elaborated netlist, and refuses identically when
   the kit cannot be resolved (R-mw2-18).

---

## 10. On completion

Findings to `src/Ui/RESOLVED.md` (**not** `CLAUDE.md`). Add a section to
`docs/design/schematic-hierarchy-navigation.md` and `docs/design/layout-view.md` describing what an
external cell reference is and what it refuses.

**Report, do not silently absorb:**
- Whether R-mw2-7's same-technology restriction turned out to be too strict in practice, and on what
  fixture — that is the decision most likely to need revisiting, and the owner should hear it from
  measurement rather than from a later bug.
- The R-mw2-15 pre-existing defect, separately from the rest.
- Anything §6 found that reaches an external reference and is not listed there.
