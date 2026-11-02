# Sonnet Brief — SL1: Reaching the library (recursive browsing, portable roots)

**Read `brief-shared-library-0-overview.md` first.** This brief is the smallest of the four and the one
that unblocks the workflow: today a shared library that is organised into folders renders **empty**, and
a `.cws` naming a network share cannot be handed to a second user because their spelling of that share
differs from yours.

**Scope: making the library's cells visible, and making the reference to it portable.** Nothing here
changes what a reference means, how it resolves, or who may write to what.

---

## 1. The library renders empty, and the asymmetry is visible in one file

`WorkspaceScanner.Scan` builds the workspace's own tree by recursing:

```
Scan (:40-46)               → cell folder ? BuildCellNode : BuildUserFolderNode
BuildUserFolderNode (:241)  → cell folder ? BuildCellNode : BuildUserFolderNode   (recursive)
```

Both referenced-tree builders stop at one level:

```
ResolveLibrary (:311)              → SubDirsSorted(libDir).Where(has .ccell)   → BuildCellNode
ResolveReferencedWorkspace (:348)  → SubDirsSorted(otherRoot).Where(has .ccell) → BuildCellNode
```

`workspace-and-project-tree.md` §1.1 states that a user folder may hold cell folders at any depth, that
this was always true and always scanned, and that it costs nothing elsewhere *because a `CellRef` is a
relative path*. That reasoning is sound and is exactly why the bug is invisible from the file formats:
**every reference into a nested library cell resolves correctly today.** Only the browsing is missing —
and browsing is what a library is for.

**R-sl1-1. A referenced library and a referenced workspace render their cells at any depth, by the same
rule the workspace's own scan uses.** A folder that is not a cell renders as a folder; a cell inside it
renders as an ordinary cell node, with the same icon, tooltip and double-click behaviour it has anywhere
else. There is no second rule to learn and no depth limit.

**R-sl1-2. The recursion stops at the referenced workspace's own configuration, exactly as it does
today.** `ResolveReferencedWorkspace`'s own note is the standing decision and it survives this brief
verbatim: the other workspace's libraries, Known Files and referenced workspaces are its business, and
rendering them here would let one reference reach transitively through a chain nobody chose. **Recurse
through FOLDERS; never through another `.cws`.**

**R-sl1-3. The reserved-folder exclusion must move into the recursion, and it is not currently there.**
`IsReservedTreeDir` (`:126`) is applied only in `Scan`'s root loop (`:42`). `BuildUserFolderNode` does
not apply it, and `SubDirsSorted` (`:390`) is a plain `Directory.GetDirectories` with no dot-folder
filter. Today that is latent — `.generated-cells` only ever exists at a workspace root, and the
`has .ccell` predicate skips it there. **The moment R-sl1-1 recurses, a referenced workspace's
`.generated-cells` becomes a browsable folder full of machine-named cells**, which §3.1's own R-L5g-9
note says must never appear in the tree in any form. Apply the exclusion in one place that both the root
loop and the recursion pass through.

**R-sl1-4. `.clib` is still not parsed, and this brief does not start parsing it.** `ResolveLibrary`
matches the *extension* to locate a folder (`:291`) and never opens the file; there is no `ClibFile`
type. Leave it that way — the name/version/metadata a `.clib` was specified to hold has no reader and no
consumer, and inventing one here would be building half of the version mechanism R-sl-6 refuses.

### 1.1 The cost this creates, and the answer to it

Recursion means the on-focus rescan walks the whole library, over the network, on every alt-tab. That is
real and it is **SL4's** problem, not this brief's — SL4 §3 changes *when* a referenced subtree is walked.
Doing it here would couple the smallest brief in the series to the one that needs a measurement first.
**Land R-sl1-1 as written; do not pre-optimise it.**

---

## 2. Named roots — one field, one syntax, one failure mode

**The problem, concretely.** `CwsWorkspaceRef.Path` (`WorkspacePersistence.cs:211`) is workspace-relative
where it can be and absolute otherwise. A library on a share is always the absolute branch, and the
absolute spelling is per-machine: `Z:\eda\stdlib\.cws`, `\\server\eda\stdlib\.cws`,
`/Volumes/eda/stdlib/.cws`. The alias indirection means each user repairs it once — which is R44's first
reason paying for itself — but it also means **a librarian cannot hand out a starter workspace with a
working library reference in it**, and that is the one thing a librarian most wants to hand out.

**R-sl1-5. A stored cross-workspace PATH may contain `${NAME}` tokens, expanded from the environment at
resolution time.** One syntax, `${NAME}`, on every platform — never `%NAME%` and never bare `$NAME`,
because a `.cws` travels between machines and a per-platform spelling would resolve on the machine that
wrote it and nowhere else. A `.cws` containing `${CRF_LIB}/stdlib/v2.3/.cws` is portable to every user
who has `CRF_LIB` set, and version pinning (R-sl-6) becomes a path the librarian publishes.

**R-sl1-6. Expansion happens in exactly three stored fields and in no others:**
`CwsWorkspaceRef.Path`, `CwsFile.LibraryRefs` and `CwsFile.KnownFiles`. These are the fields that name a
location outside the workspace. **A `CellRef` is never expanded** — it is the workspace-relative remainder
(§5C R45) and has no business naming a machine; allowing a token there would create a second place a
cross-workspace path can hide, which is the whole thing R44 exists to prevent.

**R-sl1-7. An undefined token is a BROKEN reference naming the token, never an empty expansion.**
`Environment.GetEnvironmentVariable` returns null for an unset variable, and substituting empty produces
`/stdlib/v2.3/.cws` — a rooted path that resolves to somewhere real on some machines and reports a
missing folder on others. Both are worse than the truth. The tree already has the surface for it:
System.Warning + italics with the reason in the tooltip (§3.2), worded so the user knows what to set —
*"Referenced workspace unresolved: `${CRF_LIB}` is not set on this machine."*

**R-sl1-8. Expansion lives in `src/Design/Workspace`, beside the resolver that needs it first.**
`ExternalCellRef.ResolveOtherRoot` (`:226`) already re-implements `WorkspaceRefs.Resolve`'s rule in three
lines rather than calling it, and its own comment says why: `WorkspaceRefs` is in `src/Ui`, on the far
side of the firewall, and a headless `circuitrf convert` or EM run resolves these references too. A token
expander has the same constraint and belongs in the same place, called from `ResolveOtherRoot` and from
`WorkspaceScanner.ResolveRef`.

### 2.1 What is deliberately not included

- **No token DEFINITION mechanism** — no settings page, no `.cws` field mapping names to paths. The
  environment is where a site already configures this, on all three platforms, and a second definition
  site would need its own precedence rules.
- **No `~` expansion, no `%USERPROFILE%`, no path variables of our own.** One syntax, one source.
- **Nothing is ever WRITTEN with a token in it.** circuitRF writes a plain path; a token is something a
  librarian or a user types into a `.cws` by hand, or that a site template ships. This matches R-mw2-5's
  treatment of the raw relative `CellRef`: resolve it, never produce it.

---

## 3. Gate

`tests/Ui.Tests` (do not touch `src/Core`, `src/Engine`, `RfCore`). `WorkspaceScannerTests.cs` and
`ExternalCellReferenceTests.cs` are the existing homes.

1. **Nested cells in a referenced workspace are rendered** — a fixture whose referenced workspace holds
   `passives/R0402/`, `passives/C0603/` and `amplifiers/AmpStage/`, asserting the tree shape (folder
   nodes, cell nodes, and a cell's three view sub-folders) and that a cell three levels down is
   openable. Write this against the current code first and watch it fail; it is the defect the brief
   exists for.
2. **The same for a referenced `.clib` library**, so the two paths cannot drift apart again.
3. **A `.generated-cells` folder nested inside a referenced workspace's user folder does not appear**
   (R-sl1-3), which fails against the current `BuildUserFolderNode` too — record that as a pre-existing
   latent defect in the completion note rather than folding it into the new work.
4. **The recursion does not cross a nested `.cws`** (R-sl1-2): a referenced workspace containing a
   *second* workspace folder renders that folder's cells not at all.
5. **Token expansion** (R-sl1-5/-6): a `.cws` whose `ReferencedWorkspaces[].Path` is
   `${TEST_ROOT}/other/.cws` resolves a `ws://` cell when the variable is set; the same fixture with the
   variable unset yields the broken node whose reason **names the token**, and a `CellRef` containing a
   token is NOT expanded.
6. **`circuitrf convert`/`em` resolve a tokenised alias** headlessly (R-sl1-8) — one test, since the
   whole reason the expander sits in `src/Design` is that the CLI has to reach it.

---

## 4. On completion

Findings to `src/Ui/RESOLVED.md` and, for the expander, `src/Design/RESOLVED.md` (**not** `CLAUDE.md`).

Update `docs/design/workspace-and-project-tree.md`: §3.1 (referenced trees render at depth, like the
workspace's own) and §5 (the `.cws` path fields accept `${NAME}`, with R-sl1-7's failure wording).

**Report, do not silently absorb:**
- The measured cost of the recursive scan on a large library, as a **count of filesystem calls**, not a
  time — a timing number measures the machine (see CLAUDE.md's note on the benchmark tier). SL4 needs
  that count as its starting point.
- Whether anything else in the tree builders assumes one level of depth in a referenced subtree.
- The R-sl1-3 latent defect, separately from the rest.
