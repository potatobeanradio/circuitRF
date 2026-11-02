# Sonnet Brief — RC-1: the `.cwsuser` split

**Read `brief-revision-control-0-overview.md` first.** The architecture is
`docs/design/revision-control.md` §3.1, §3.1a and §12 Q1. **This brief depends on nothing and needs
no git installed** — it is worth doing on its own merits and must reach the users who will never have
git.

**Scope: separating one document that was only ever one by accident.** Per-user session state moves
out of the `.cws` into a sibling `.cwsuser`; both files open the workspace; a missing `.cwsuser` is
normal, silent, and never an error.

---

## 1. Why, in one measurement

`WorkspacePersistence` writes the panel arrangement into the `.cws`, so that file changes **on every
session close** for reasons that have nothing to do with the design. Measured on a real workspace's
`.cws` (2,178 bytes of content), §3.1:

| field | bytes | belongs to |
|---|---|---|
| `DockLayout` | 1,700 | **one person's monitor** |
| `TreeViewState` | 221 | **one person's expanded categories** |
| `OpenDocuments` | 170 | **one person's tabs** |
| `ActiveDocumentPath` | 41 | **one person's afternoon** |
| `DefaultTechRef` | 41 | the project |
| `LibraryRefs`, `KnownFiles` | 4 (empty here) | the project |
| `FormatVersion` | 1 | the project |

**Roughly 96% of a real `.cws` is per-user session state.** Under version control every history would
be a majority of "someone moved a panel" commits, and two designers on different monitors would
conflict on every exchange — on a file that also carries real design configuration, so the conflict
could not be resolved by discarding either side.

It is already the wrong thing to put in an archive, in a shared workspace, and in a read-only
referenced one. Version control makes it a third kind of wrong, not the first.

---

## 2. The split

**R-rc1-1. What moves:** `DockLayout`, `TreeViewState`, `OpenDocuments`, `ActiveDocumentPath`.

**R-rc1-2. What stays:** `FormatVersion`, `LibraryRefs`, `KnownFiles`, `DefaultTechRef`,
`DefaultAssemblyRef`, `ReferencedWorkspaces`, `ReferencedCells`, `PdkRefs`, `ColorSchemeName` —
everything that is a property of the project rather than of one person's session.

**`ColorSchemeName` is an open question the architecture flags and does not settle** (rev 3 §12,
"Still open"). It is on the versioned side above because §3.1 put it there, on the grounds that it is a
property of the project — and a house style is a real thing. But it is arguably one person's
preference, and a shared workspace would then impose its author's theme on everyone who opens it. **It
is the one field in §3.1's table whose side was assigned rather than measured.** Get a decision before
this brief lands; the cost of being wrong is trivial in either direction, which is exactly why it will
otherwise never be revisited.

**`PythonInterpreter` is the one judgement call, and it stays in the `.cws`.** Read its own header
(`WorkspacePersistence.cs`): it is a decision circuitRF made automatically and recorded so it is
visible and one line to correct, and it heals itself when the recorded interpreter stops working. It
is per-*machine*, not per-*user* — but the `.cwsuser` is not a per-machine file either, and moving it
would cost a kit-using workspace a process-launch storm on every fresh clone for no gain. **If the
implementation finds a reason this is wrong, report it rather than deciding it silently.**

**R-rc1-3. The sidecar is literally named `.cwsuser`**, beside the `.cws`, one per workspace, the same
no-stem convention the `.cws` already uses. The name states its role, which is the whole reason for
it: a `.gitignore` line reading `.cwsuser` next to a tracked `.cws` is self-explanatory in a directory
listing, in a diff, and to whoever inherits the workspace. Two four-letter extensions differing by one
character would be transcribed wrongly exactly once and then quietly do the wrong thing for a year.

**`.crfw` is not reused and is not touched.** It is a registered spelling of a workspace that no build
has ever written, inert, matched by one `case` label. Retiring it is a packaging question with its own
three-registration blast radius. Recorded here only so the next person does not have to re-derive that
nothing writes one.

---

## 3. Absence is the normal state, not an edge case

**R-rc1-4. A workspace with no `.cwsuser` — missing, never written, or deleted — opens normally.**
This is the invariant that makes the split safe:

- **It is what a gitignored file has to be.** A versioned document that cannot be opened without an
  unversioned one is not split, it is broken. Every clone, every archive and every workspace handed to
  a colleague arrives without one, so absent is the **normal state of a shared workspace** and by far
  the most common way one will be opened by someone other than its author.
- **R-rc1-5. Its absence is never reported.** No warning, no repair prompt, no "recovering your
  layout" message, nothing in the Messages panel. The workspace opens on the default arrangement,
  exactly as a freshly-created one does. Anything else trains users to think something is wrong when
  nothing is.
- **R-rc1-6. A malformed `.cwsuser` is treated as an absent one.** The same posture `CwsFile.DockLayout`
  already takes with a structurally malformed block, and for the same reason: per-user convenience
  state must never be able to prevent a design from opening.
- **R-rc1-7. Deleting it is a supported repair**, documented as one — a designer whose panels have
  ended up somewhere unusable closes the workspace, deletes one file, and reopens. That property comes
  free and holds only while **nothing on the versioned side depends on the sidecar**. Nothing may. A
  future field that must survive that deletion belongs in the `.cws`.

**R-rc1-7a. Two designers on a share have one `.cwsuser` between them, and that is accepted here for
the reason it is refused in §4.4.** The sidecar sits in the workspace folder, so on the network share
§4.7 and §7A both assume, the second designer to close overwrites the first one's panel arrangement.
That is the same shape as the identity mistake rev 3 corrected — per-user state in a shared location —
and the brief should say why the same reasoning does not force the same fix, because someone will
otherwise "correct" it. **§4.4's cost is a durable falsehood in a record**; this one's cost is **a panel
that moved**, in a file whose absence is already normal (R-rc1-4) and whose deletion is already a
supported repair (R-rc1-7). The failure is visible, immediate and self-correcting. Splitting it per user
would need a path outside the workspace folder, which loses the property that makes the sidecar
comprehensible at all — that it sits beside the thing it describes.

**R-rc1-8. A `.cwsuser` with no `.cws` beside it is not a workspace**, and says so in a sentence.
Someone who copied one file out of a folder must not get an empty window — that is precisely the
`OpenFiles` failure this repo already fixed once, arriving by a new route.

---

## 4. Where the work actually is

**R-rc1-9. The write is one write becoming two, inside the existing choke point.**
`WorkspacePersistence.SaveToFileAtomic` (`src/Design/Workspace/WorkspacePersistence.cs:397`) is
already the only place a `.cws` is written — SL2 made it so, and its header explains why enforcing a
rule at eighteen call sites means enforcing it at seventeen. **Split there, not at the callers.** A
nineteenth call site must inherit the split without knowing it exists.

The same applies to SL2's read-only rule, which already lives there: **a read-only workspace writes
neither file**, and the `.cwsuser` write is skipped as silently as the `.cws` write is.

**R-rc1-10. The read is one read becoming two, inside `TryLoadCws`** (`WorkspaceViewModel.cs:2531`) —
the corresponding choke point, which has existed since the beginning. The merged result is the same
`CwsFile` shape callers already hold, so **no caller changes**: this is a persistence split, not a
model split. If a caller does have to change, that is a finding worth reporting, because it means
something reads session state through a path the choke point does not cover.

**R-rc1-11. Atomicity is per file, and there is no cross-file transaction.** The `.cws` write must
succeed or leave the old file intact, as today. A `.cwsuser` write that fails after a successful
`.cws` write loses panel positions and nothing else — which R-rc1-4 already says is a survivable
state, and which is exactly why the split puts the *unimportant* half in the second file.

**R-rc1-12. Migration is a read-side default, and there is no rewrite pass.** An older `.cws` carrying
the four moved fields loads exactly as it does today; the fields are honoured on read and land in the
`.cwsuser` on the next save. No `FormatVersion` bump, no upgrade prompt, no batch conversion. **The
stale copies left behind in the `.cws` are dropped on that first save**, which is the whole of the
migration.

---

## 5. Three registrations, and the tests that hold them shut

Per §3.1a, three things change together, and each is currently held by a test.

**R-rc1-13. `App.OpenFiles` gains `case ".cwsuser":` on the workspace branch**
(`src/Ui/App.axaml.cs:627`), resolving to the workspace through the containing folder. Double-clicking
either half opens the whole workspace: they are two halves of one document, a user who double-clicks
either means the same thing by it, and a file that shows a circuitRF icon and then does nothing reads
as a broken file.

**R-rc1-14. All three OS registrations gain the type** — the macOS `Info.plist` UTI declarations, the
WiX `.wxs` extension list, and the Linux `circuitrf-mime.xml` glob.

**R-rc1-15. The three parity tests are extended, never worked around.**
`WBondStandaloneTests.cs:494` (plist), `:732`–`:752` (mime) and the `.wxs` case assert that every type
declared to an operating system has a case in `OpenFiles` and vice versa. They are what makes
"registered but opens nothing" impossible. A test changed to accommodate a gap is the gap shipping.

Note the shared-mime-info detail already recorded in `packaging/RESOLVED.md`: a `<mime-type>` is a
container of globs, so `*.cws` and `*.cwsuser` can sit under one type rather than needing a second.

**R-rc1-15a. The sidecar is excluded from an ARCHIVE and kept in a Save Workspace As COPY, and the two
share one skip list** (§3.1a, §9A.2 — assigned to this brief in rev 4; rev 3 left it to RC-8, three
stages and two releases later, so the file would have been archived the whole time it existed).

`WorkspaceArchiveScanner.IsSkipped` (`src/Ui/Archive/WorkspaceArchiveScanner.cs:53`) is consulted by
**both** the archive and `WorkspaceCopy.Run` (`src/Ui/Schematic/WorkspaceCopy.cs:130`), and here the two
want **opposite** answers:

- **an archive goes to somebody else**, who has no use for the sender's monitor layout — §9A.2 says it
  is never archived, in either state of RC-8's checkbox;
- **a Save Workspace As copy is the same person's own workspace on the same machine**, which the window
  then switches to. Losing the panel arrangement there is a small annoyance with nothing bought by it.

**So the exclusion belongs to the archive's own list, not the shared one — and that is deliberate,
because it is the REVERSE of `.git`**, where both consumers want the same answer for the same reason
(RC-5 R-rc5-18, R-rc5-19). Say so where the lists are declared, or the next person tidies the two into
one and silently changes whichever half they were not thinking about. **This is the same shared-list trap
RC-5 identifies for `.git`, arriving a second time with the opposite resolution.**

**R-rc1-16. The generated `.gitignore` excludes `.cwsuser`.** RC-3 owns the generator; this brief owns
the line and the reason for it. RC-3 depends on this brief for exactly that.

---

## 6. Gates

1. **Round trip.** A workspace saved and reloaded restores dock layout, tree state, open documents and
   active document identically to today — assert against the pre-split behaviour, not against a
   transcription of the new writer.
2. **Absent sidecar.** Delete the `.cwsuser`, reopen: the workspace opens, on defaults, with **no
   message posted at any level** (assert the sink is empty, not merely that no error appeared).
3. **Malformed sidecar.** A `.cwsuser` containing truncated JSON, and one containing valid JSON of the
   wrong shape: both open on defaults, silently (R-rc1-6).
4. **Orphan sidecar.** A `.cwsuser` with no `.cws`: one sentence, no window (R-rc1-8).
5. **Migration.** An older `.cws` carrying all four moved fields loads with them honoured; after one
   save the `.cws` no longer contains them and the `.cwsuser` does.
6. **Read-only workspace writes neither file** — drive `WorkspaceWritability.WritabilityProbe`, which
   SL2 exposed as exactly this seam, and assert both paths are untouched.
7. **The choke point is the only writer.** Source-scan for a `.cwsuser` write outside
   `WorkspacePersistence` and fail on one. Strip comments before scanning — this repo has been caught
   by a source scan matching its own documentation.
8. **The three parity tests pass unmodified in intent**, with `.cwsuser` added to each registration.
9. **The sidecar is archived by neither and copied by Save As** (R-rc1-15a): archive a workspace and
   assert no `.cwsuser` arrives; Save Workspace As and assert one **does**. Assert both halves — the
   asymmetry is the requirement, and a test for only the first would pass on a shared skip list that
   breaks the second.
10. **Culture invariance** — `FormatCultureInvarianceTests` covers the new file as it covers the rest.

---

## 7. Documentation (§10B.1)

The normative table's row: **panel layout, open tabs, tree expansion — not kept in history,
deliberately, and nothing is lost by that.** In plain words, in `docs/user/src/reference/`:

- what the two files are and which one matters;
- that the sidecar is not versioned, not archived, and not needed;
- **that deleting it is a supported repair** (R-rc1-7), which is the one genuinely useful thing a
  reader can do with this knowledge.

**The §10B.1 row this brief also owns** (new in rev 4): *panel layout, in a Save Workspace As copy —
kept*, which is the one thing a copy keeps that an archive does not. It belongs in the normative table
because the neighbouring rows all say **no** and a reader scanning them will otherwise generalise.

`docs/design/project-file-formats.md` gains the format. This is a user-visible new file type in a
workspace folder, so it is named where a reader would look for it.

---

## 8. On completion

Findings to `src/Design/RESOLVED.md` (the persistence split) and `src/Ui/RESOLVED.md` (the open path
and the registrations) — **never to a `CLAUDE.md`**.

**Report, do not silently absorb:**
- Whether any caller of `TryLoadCws` or `SaveToFileAtomic` needed changing (R-rc1-10) — that would
  mean session state is read or written somewhere the choke point does not cover, which is the most
  valuable thing this brief can find.
- **The `ColorSchemeName` decision**, if it was still open when this brief started (R-rc1-2). Do not
  settle it silently in either direction.
- The `PythonInterpreter` judgement (R-rc1-2) if the implementation disagrees with it.
- The actual byte split measured on a real workspace, against §3.1's table.
