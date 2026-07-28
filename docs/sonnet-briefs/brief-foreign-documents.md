# Sonnet Brief — Foreign documents: editing files from outside the current workspace

A document from another workspace can be open, edited and saved alongside the current one — for reference
while authoring, or simply because it survived a workspace switch. **Consumes all of L0–L4.**

**Sequencing:** overlaps `brief-file-menu-restructure.md`, whose **R-menu-6** asks for the current
workspace-teardown behaviour to be *reported, not changed*. Land that brief first and read its finding —
it tells you what the starting point actually is.

---

## 1. The model: two orthogonal axes

The existing code conflates two things that are not related.

|  | **Docked** | **Torn off** |
|---|---|---|
| **Workspace-bound** | normal case | **also normal — full privileges** |
| **Foreign** | opened from outside the workspace | reference document in its own window |

**R-fgn-1. Tearing a document off is a presentation act, not a semantic one.** A document from the current
workspace keeps every privilege when torn off — tree node, dirty dot, Save-All, Remove/Rename Cell
participation, `.cws` session membership. A user who wants a bigger canvas must not be penalised for it.
If any existing behaviour keys off "is torn off" to decide anything other than presentation, that is the
bug.

**A document is foreign when its file does not belong to the currently open workspace** — determined by
its own path, never by how it was opened or where it is displayed.

### 1.1 The two ways a document becomes foreign

1. **Opened from outside** — `File ▸ Open ▸ …` on a path outside the current workspace. This already
   happens today and is the reason the "every open document belongs to the workspace" invariant was already
   false.
2. **Orphaned by a workspace switch** — the workspace it belonged to is closed or replaced.

**R-fgn-2. A workspace switch replaces the contents of the window it happens in; other windows are not
affected.** So docked documents close as they do today, and **torn-off windows survive**, becoming foreign.

This is the framing that keeps R-fgn-1 intact: tear-off does not grant a document special status, but a
switch performed *in the main window* has no business reaching into separate windows. **State this
interpretation in the completion note** — it is the one place the owner's two requirements need reconciling,
and he should be able to correct it if he meant something else.

A dirty torn-off document does **not** prompt on switch; it stays open and dirty.

## 2. Technology resolution — the part that must be right

**R-fgn-3. `TechRef = null` resolves against the document's OWN parent workspace, not the currently open
one.**

L0c's convention is that null means "the workspace default." The correct reading is *the document's*
workspace: a `.clay` in workspace A means what A's technology says it means, regardless of what happens to
be open. The current behaviour — resolving against whatever workspace is loaded — would silently reinterpret
a foreign layout's layers, and since both starter technologies use keys `(1,0)`–`(8,0)`, Drill would quietly
become Substrate with nothing missing and no warning. That is the L1g collision arriving through a new door.

**Mechanism: walk up from the document's own absolute path to the nearest ancestor `.cws`.** The git /
solution-root pattern. No new state to carry and no field to keep in sync — the document already knows its
path, and it stays correct when a project folder is moved wholesale.

**Do not freeze or copy the technology.** Resolving live against the parent workspace means a later edit to
that workspace's `.ctech` is still seen. A snapshot would go stale silently.

`TechnologyCache` is keyed by absolute path and needs no change.

### 2.1 When no parent workspace exists

A loose file with no ancestor `.cws` has nothing to resolve against.

**R-fgn-4. Prompt, offering three routes**: browse for a `.ctech`; pick one from the **current** workspace;
or use a built-in starter technology.

- **Session-scoped.** Remember the answer for that document so it is asked once, not on every resolve.
- **Do not write a `TechRef` into the user's file.** The prompt is a transient "for now." If they want it
  permanent, **L1g's `Change Technology…` already does exactly that** — explicit, undoable, and it
  materialises the choice properly. Point at it in the prompt rather than duplicating it.
- Falling back to `FallbackPalette` without asking is **not** acceptable here: unlike an unknown layer
  inside a known technology, this is the whole technology missing, and silently generated colours would look
  like the document rendering wrongly.

## 3. What a foreign document does and does not participate in

**Editable and saveable** — that is the owner's requirement. It saves to its own path.

| | Foreign document |
|---|---|
| Edit, save, undo | **Yes**, fully |
| Push-in / hierarchy navigation | **Yes** — `CellRef` is relative to the `.clay`, so it resolves against its own workspace's files on disk |
| Save All, quit prompt | **Yes** — see R-fgn-5 |
| Project tree node, dirty dot | No — it is not in this workspace |
| Remove Cell / Rename Cell rewriting | **No** — and workspace operations must not reach it |
| `.cws` session membership | **No** — R-fgn-6 |

**R-fgn-5. Save All and the quit prompt must sweep open documents, not tree nodes.** A dirty document with
no tree node that cannot be reached by Save All, and does not prompt on quit, is a data-loss trap. Verify
both paths; if either is tree-driven today, fix it.

**R-fgn-6. A foreign document is never recorded in the current workspace's `.cws`.** Owner's decision. It is
not part of this workspace's session and must not reappear when the workspace is reopened.

**Verify workspace operations cannot reach a foreign document**: `CellUsageScanner`, Remove Cell and Rename
Cell all operate over the current workspace and must neither rewrite nor force-close a document belonging to
another one. L3b's `LayoutSessionRegistry` is keyed per path, so it should already behave — confirm rather
than assume.

**`Save As` into the current workspace adopts the document** — it becomes workspace-bound, gains a tree node
and resolves technology normally from then on. That is the natural "bring this into my project" gesture and
should fall out of the path-based definition rather than needing special handling.

## 4. Marking — all three surfaces, and never the geometry

**R-fgn-7. Mark the chrome. Never tint the rendered geometry.** §2.2 makes layer colours literal
user-authored `Rgba` specifically so a layer's colour survives a theme change and matches what KLayout
shows. Tinting the drawing would corrupt the one thing a reference document is open to show.

Three surfaces, all of them:

1. **Title bar** — `mylayout — [AmpProject]`, naming the source workspace. Informative rather than
   decorative: it answers *which* workspace, which a marker alone cannot. **Preserve the existing dirty
   bullet** (`• mylayout — [AmpProject]`) — do not use asterisks, which already read as "unsaved."
2. **A thin tinted band along the document's edge**, naming the workspace, with an affordance to open it.
   This is the established pattern for "you are in a different context" — VS Code's remote-window title
   colour, SSMS's per-server status bar.
3. **The tab header tinted** to match, so a docked foreign document is identifiable among its neighbours.

**Use amber or blue, not red.** A foreign document is a normal, supported state; red means error and would
mislabel it. Reuse the existing visual vocabulary for unusual-but-fine states (the missing-cell placeholder,
the broken-bitmap box) rather than inventing a third language.

When no parent workspace exists, the band says so — *"Not part of any workspace"* — rather than naming one.

## 5. Guardrail: no persisted cross-workspace paths

**R-fgn-8. Foreignness is a runtime concept only. Introduce no persisted cross-workspace path format.**

Nothing in this brief writes a cross-workspace reference to disk. That is deliberate: instancing cells from
another workspace — the "Add Library" idea — is a separate feature whose central design question is
**a named library alias resolved through `.cws` (the standard library-alias pattern) versus raw paths in every
file**. Raw paths mean moving a library breaks every document that referenced it.

Answering that by accident here would saddle the library feature with a convention chosen for a different
problem. `CellRef` is currently *relative* to its containing `.clay`, so cross-workspace instancing is a new
mechanism rather than an extension — and it gets its own brief.

## 6. Scope guardrails

- No "Add Library", no cross-workspace instancing, no library aliases (§5).
- No change to how workspace-bound documents behave, docked or torn off (R-fgn-1).
- No change to `TechRef`'s meaning or to `.clay`/`.ctech`/`.cws` formats.
- Do not tint, recolour or otherwise alter rendered geometry.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 7. Gate

Gate command is plain `dotnet test`.

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Tear-off changes nothing (R-fgn-1)** — a torn-off document from the current workspace keeps its tree
   node, dirty dot, Save-All participation, Remove/Rename Cell participation and `.cws` membership. Assert
   each, because this is the requirement most easily broken by the rest of the work.
3. **Switch scope (R-fgn-2)** — switching workspaces closes docked documents and leaves torn-off windows
   open, now marked foreign. A dirty torn-off document survives without prompting.
4. **Technology from the parent workspace (R-fgn-3)** — the headline test. Open a layout from workspace A
   with `TechRef = null` while workspace B is loaded, where **A and B have different technologies sharing
   layer keys `(1,0)`–`(8,0)`**. Assert the layers resolve to **A's** names and colours, not B's. This is
   the silent-corruption case and it must fail loudly if the resolution is wrong.
5. **Live, not frozen** — edit A's `.ctech` on disk while the foreign document is open; the document picks
   the change up.
6. **No parent workspace (R-fgn-4)** — a loose `.clay` prompts once, offers all three routes, honours the
   choice for the session, and **writes no `TechRef` to the file**. It never silently falls back to
   `FallbackPalette`.
7. **Edit and save** — a foreign document edits, undoes and saves to its own path; its own workspace's files
   are updated and the current workspace is untouched.
8. **Push-in works** — hierarchy navigation inside a foreign document resolves against its own workspace.
9. **Save All and quit (R-fgn-5)** — a dirty foreign document appears in Save All and prompts on quit.
10. **Isolation** — Remove Cell and Rename Cell in the current workspace neither rewrite nor close a foreign
    document, even when cell names collide.
11. **No `.cws` pollution (R-fgn-6)** — reopening the current workspace does not reopen the foreign document.
12. **Save As adopts** — saving a foreign document into the current workspace makes it workspace-bound with
    a tree node.
13. **Marking (R-fgn-7)** — all three surfaces show the source workspace; the dirty bullet still works; a
    pixel test asserts rendered geometry colours are **identical** to the same document opened natively.
14. **No persisted cross-workspace path (R-fgn-8)** — grep the written `.clay` and `.cws` after every gate
    above; nothing references another workspace.

## 8. On completion

Record in `src/Ui/CLAUDE.md`: **R-fgn-1** (tear-off is presentation only) and anything that had been keying
off it; **R-fgn-2's interpretation** that a switch is scoped to its own window, flagged as the reconciliation
the owner should confirm; **R-fgn-3** — that `TechRef = null` now means the *document's* workspace, with the
ancestor-`.cws` walk and why a snapshot was rejected; the R-fgn-4 prompt and its session scope; what R-fgn-5
found about Save All and quit being tree-driven or not; and **R-fgn-8** as a standing constraint the future
library feature depends on.
