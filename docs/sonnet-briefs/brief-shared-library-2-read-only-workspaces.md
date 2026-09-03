# Sonnet Brief — SL2: Read-only workspaces

**Read `brief-shared-library-0-overview.md` first.** SL2 is independent of SL1 and SL3; it can land
before or after either.

**Scope: circuitRF noticing that a workspace's files cannot be written, and behaving accordingly** —
before the user has done the work, not after. Nothing here enforces anything: the share's permissions
are the enforcement (R-sl-2), and this brief is entirely about the product telling the truth about them.

---

## 1. What happens today

`grep -rn "IsReadOnly\|FileAttributes.ReadOnly" src` returns nothing. The product has no concept, so
every path takes the optimistic branch and discovers the truth at the write:

- **The library's own documents are editable and saveable.** A file opened from another workspace is a
  *foreign document*, and `workspace-and-project-tree.md` §5A.3 makes those fully editable and saveable
  to their own path — deliberately, for the case §5A was written for (a colleague's file you opened to
  look at, on your own disk). Applied to a share, the same rule offers Save on a master cell. Where the
  librarian has locked the share, the user learns this **after** editing.
- **§5C R49's repair cannot be performed.** R49 says a kit part inside a referenced cell resolves only
  when that cell's own workspace is open in a window, and the repair offered is to open it. Opening a
  workspace writes its dock layout, its open-document list and its settled Python interpreter back into
  its `.cws` (`WorkspaceViewModel.cs:1738` and thirteen other sites). On a read-only share the product
  recommends a repair it cannot carry out, and reports the failure as a save error at an unrelated moment.
- **A PCell edit blames the parameters.** `LayoutEditorViewModel.PCells.cs:137-146` catches everything
  `GeneratedCellStore.GetOrCreate` throws and reports *"could not generate artwork for these
  parameters"*. On a read-only workspace the generator was fine and the directory was not
  (`GeneratedCellStore.cs:110-118` — `Directory.CreateDirectory` under the **document's own** workspace
  root, which is the correct root and an unwritable one).
- **`SavePlanExecutor.ExecuteFileOps` (`:38`)** creates a workspace folder and `.cws` with no
  precondition on the parent being writable.

None of these is silent — `FileAccessDiagnostics.TryDescribe` (`src/Diagnostics/FileAccessDiagnostics.cs:35`)
already turns an `UnauthorizedAccessException` into a sentence that mentions read-only files. They are
all *late*, and lateness is the whole complaint: a refusal before the work is a supported state, and a
failure after it is lost work.

---

## 2. Discovering it

**R-sl2-1. Writability is discovered by attempting a write, once per workspace root, at open.** Create a
uniquely-named file in the workspace root, then delete it. `File.GetAttributes` reports the DOS
read-only bit and says nothing about a share ACL, a POSIX mode, or a mount option; `Directory.Exists`
says nothing at all. The only portable answer on Windows, macOS and Linux is to try.

**R-sl2-2. A probe that throws for ANY reason means read-only.** Not "read-only unless the exception was
`UnauthorizedAccessException`" — an `IOException` from a full disk, a disconnected share or a
locked-down directory all mean the same thing to every caller downstream: do not attempt writes and say
so. Distinguishing them buys nothing and multiplies the states.

**R-sl2-3. The answer is memoised per workspace root and dropped by `WorkspaceRootFinder.InvalidateCache`.**
That is where the two existing per-workspace memos already live and are already dropped together
(`WorkspaceRootFinder.cs:_rootMemo`, `ExternalCellRef._aliasMemo`, `InvalidateCache` at
`WorkspaceRootFinder.cs:85` clearing both). A third with its own lifecycle would be the one that goes
stale.

**R-sl2-4. There is a per-DOCUMENT question too, and it is the same probe on the document's own
directory.** A workspace can be writable while one cell folder inside it is not, and — more commonly —
a document open from a read-only library sits in a workspace this window did not open at all. Ask about
the directory that would actually be written.

---

## 3. Behaving

**R-sl2-5. A read-only workspace writes nothing.** Not the dock layout, not the open-document list, not
the active document, not the settled interpreter, not the kit settings, not the tree view state, not
`.generated-cells`. All of it is *convenience state about a session*, and none of it is worth a failed
write, a diagnostic, or a modal at the end of a session the user is trying to close.

**R-sl2-6. Every `.cws` write routes through one method, which is where R-sl2-5 is enforced.** There are
**fifteen** `WorkspacePersistence.SaveToFileAtomic` call sites today — thirteen in `WorkspaceViewModel`,
one in `WorkspaceViewModel.ExternalRefs.cs:146`, one in `SavePlanExecutor.cs:38` — and no choke point.
Reads have one (`TryLoadCws`, `WorkspaceViewModel.cs:2097`); writes need the mirror of it. Without that,
R-sl2-5 will be true in fourteen places and the fifteenth will be found by a user.

**R-sl2-7. Save is DISABLED on a read-only document, and Save As is offered in its place.** Disabled
before the edit, not refused after it. The wording says which workspace the file belongs to and that it
is read-only — *"`Amp` belongs to `stdlib`, which is read-only on this machine. Save a copy into your
own workspace instead."* §5A.3's "fully editable and saveable" is unchanged for every writable case;
this is the case that section never had to consider.

**R-sl2-8. Editing a read-only document is still allowed, and the edits are still tracked.** Reading a
library cell and pulling it about to understand it is a legitimate and common thing to do; the product
must not make a file un-scrollable because it is un-writable. What changes is where the edits can land:
Save As, or `Save As` into the current workspace, which §5A.3 already defines as the adopt gesture. The
quit prompt must offer that route rather than a Save that cannot succeed.

**R-sl2-9. The marking reuses the foreign-document chrome, and does not invent a second appearance.**
§5A.4's three surfaces — title bar, edge band, tab header — already say *this belongs to another
workspace*, which is true and is half the message. Add *read-only* to the same band's wording rather
than a fourth surface or a second colour. §5A.4's "unusual-but-fine accent, never an error colour"
applies unchanged: a read-only library document is a normal, supported, desirable state.

**R-sl2-10. A PCell that cannot be generated because the workspace is read-only says so, and does not
blame the parameters.** One extra branch at `LayoutEditorViewModel.PCells.cs:141` and at
`LayoutEditorViewModel.PaletteDrag.cs:200`: ask R-sl2-1 before calling `GetOrCreate`, and refuse with the
workspace named. The existing catch stays as the backstop for a genuine generator failure.

---

## 4. Opening a library read-only is the point of the feature

**R-sl2-11. `File ▸ Open Workspace` on an unwritable workspace opens it read-only, with a message
saying so once, and never a refusal.** Everything a user wants from the corporate library — browsing it,
reading a schematic, pushing into a hierarchy, seeing what a cell's parameters are — is a read
operation.

**R-sl2-12. §5C R49 is unchanged, and this is what makes obeying it free.** R49 refuses to mount another
workspace's kits without opening that workspace, for a reason that still holds: it would make *"which
workspace is this part from"* unanswerable. The problem was never that rule — it was that *"open it"*
implied *"write to it"*. Once R-sl2-11 exists, the repair R49 offers costs a window and nothing else.
**Do not weaken R49, and say so in the completion note**, so that the next reader does not mistake this
brief for having quietly dropped it.

**R-sl2-13. `New Workspace` and `Save As` into an unwritable parent refuse at the picker**, naming the
directory. `SavePlanExecutor` runs after a confirmed plan dialog; discovering the directory is unwritable
*inside* the executor means a plan the user confirmed cannot be carried out, and a partially-created
workspace to clean up (it creates the folder and the `.cws` before it creates any cell).

---

## 5. Gate

`tests/Ui.Tests` (do not touch `src/Core`, `src/Engine`, `RfCore`).

**The platform problem, named rather than discovered.** A directory that is genuinely unwritable is
easy to make on macOS and Linux (`chmod 500`) and awkward on Windows, where the equivalent needs an ACL
edit and where a test running elevated may be able to write regardless. **A gate that silently passes on
one platform is not a gate.** So:

1. **The probe is behind a seam** — an injectable writability predicate — and the behavioural tests
   (R-sl2-5 through R-sl2-13) drive that seam. These run identically everywhere and are the tests that
   protect the behaviour.
2. **One real-filesystem test per platform capability**, asserting that the probe itself gives the right
   answer against a directory the test actually made unwritable. Where the platform cannot express it,
   the test **skips with a reason** — the `FixtureFact` pattern `RfCore.Tests` already uses — never
   passes vacuously.
3. **No `.cws` is written when the workspace is read-only** (R-sl2-5/-6): open a fixture read-only, dock
   a panel, open a document, close the workspace, assert the `.cws` bytes are unchanged. Byte equality,
   not "no exception" — the point is that nothing was attempted.
4. **Save is disabled and Save As is offered** on a read-only document (R-sl2-7), and the quit prompt
   routes through Save As rather than offering a Save that fails (R-sl2-8).
5. **A referenced cell's kit resolves once its workspace is open read-only** (R-sl2-12) — the direct
   test that R49's repair now works. `ExternalCellReferenceTests.cs` is its home, beside the existing
   `WorkspaceNotOpen` case.
6. **The PCell refusal names the workspace, not the parameters** (R-sl2-10).

---

## 6. Stop and report

If R-sl2-6's choke point turns out to require restructuring more than the `.cws` write sites — for
instance if the dock-capture path builds its `CwsFile` in a place that cannot ask about the workspace —
**stop and report before restructuring**. The fallback that keeps the brief's promise is narrower and
honest: probe once at open, and have the fifteen sites consult a single flag on the view model. It is
less tidy and it holds the same invariant.

---

## 7. On completion

Findings to `src/Ui/RESOLVED.md` (**not** `CLAUDE.md`).

Update `docs/design/workspace-and-project-tree.md` §5A.3 (the editable/saveable table gains the
read-only case, which does not contradict it — it is the case §5A never had to consider) and §5C.3 (R49's
"open that workspace" repair is now performable against a read-only share, and R49 itself is unchanged).

**Report, do not silently absorb:**
- Which of the fifteen `.cws` write sites were reachable on a read-only workspace in practice, and what
  each one was trying to record — that list is the real evidence for whether R-sl2-5's "none of it is
  worth a failed write" holds.
- Any place the probe's answer was needed on a hot path, since R-sl2-3 memoises for exactly that reason.
- Whether the Windows leg of the gate could be made real, or is skipping.
