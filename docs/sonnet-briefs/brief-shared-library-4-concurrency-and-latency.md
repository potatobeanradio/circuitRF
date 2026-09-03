# Sonnet Brief — SL4: Concurrent users, and the cost of a reference over a network

**Read `brief-shared-library-0-overview.md` first, and land SL1 before this** — SL1's recursive scan is
what makes the referenced-subtree cost in §3 worth measuring, and its completion note is required to hand
this brief a call count to start from.

**Scope: two people, and a wire.** Everything else in the series is about one user and a set of files;
this is about what happens when the files are on the other end of a cable and someone else has them open
too.

**This brief is last on purpose.** §3 is the only change in the series that trades away a property the
product currently has, and §2 is the only one that can produce a false statement about another person.
Both deserve a measurement and a stated bound rather than an intuition.

---

## 1. Two users, one workspace

**What exists.** `WorkspaceViewModel.SwitchToWorkspace` (`:1905`) refuses to open a workspace twice, and
its own comment gives the reason precisely: *two view models over one `.cws` means two independent
edit-session registries over the same files — two undo stacks, two dirty flags, last-save-wins.* That
reasoning is entirely correct and entirely **process-local**: the check is `App.WindowShowing(cwsPath)`,
which enumerates this process's windows. Every consequence it names is equally true of two people on two
machines, and none of it is detected there.

The concrete loss is not the documents — those are ordinary files and a clobbered `.csch` is at least
visible. It is the `.cws`: two users with the same workspace open both write dock layout, open-document
list, kit settings and **the alias table** on save. Last writer wins, silently, and a referenced-workspace
alias that vanished from someone else's file is not a symptom anyone attributes correctly.

**R-sl4-1. Concurrent open is detected with an advisory lock file, written beside the `.cws`.** A small
JSON file recording the user, the host, the process id and the time it was taken. Written when the
workspace is opened **and is writable** (SL2's probe answers that; a read-only library takes no lock and
needs none — nobody can write it). Removed on close.

**R-sl4-2. It is advisory, and the wording says so.** The notice names who and where — *"`stdlib` was
opened by another user on `<host>` about 20 minutes ago. Open read-only, or open anyway?"* — and both
answers are available, always. **A lock this product treats as authoritative becomes a stale file that
locks out a team**, which is a worse failure than the one being prevented, and it is unfixable by anyone
who does not know the file exists. This is R-sl-8, and it is not negotiable for a convenience gain.

**R-sl4-3. A stale lock is treated as stale, by two independent rules**: it names this host and a process
id that is not running, or it is older than a generous threshold (hours, not minutes). Both are
heuristics and both may be wrong; that is acceptable precisely because R-sl4-2 makes the answer
overridable either way.

**R-sl4-4. Do not try to hold the lock with an open file handle.** `CrashReporter` uses exactly that
trick — a handle held with `FileShare.Read` so that an exclusive open by a probe proves ownership
(`CrashReporter.cs:110-113`) — and `Program.cs:95-98` uses the same idiom for single-instance detection.
It is the right mechanism **locally**, and its guarantees do not survive SMB, NFS or a dropped
connection. A handle-based lock over a share fails in the direction that produces a confident false
statement about another person, which is the one direction this feature must not fail in.

**R-sl4-5. Nothing merges.** Detect, report, let the user choose. Reconciling two `.cws` files or two
`.csch` files is a different product.

---

## 2. What the reference actually costs over a network

**Measured from the code, not assumed.** One `CellSymbolResolver.Resolve` — before the cache can be
consulted — performs:

| Step | Call | Line |
|---|---|---|
| 1 | `Directory.Exists(cellAbsDir)` | `CellSymbolResolver.cs:127` |
| 2 | `Directory.Exists(symbolSubFolder)` | via `CellFolder.ResolvePrimary`, `CellFolder.cs:138` |
| 3 | `Directory.GetFiles(symbolSubFolder, "*.csym")` | `CellFolder.cs:141` |
| 4 | read `.ccell` (only when the folder holds more than one symbol) | `CellFolder.ReadNamedPrimary` |
| 5 | `File.GetLastWriteTimeUtc(symPath)` | `CellSymbolResolver.cs:157` |

**Only then** is the `(cellAbsDir, primaryName)` cache consulted, and it hits only if the mtime matches
(`:162`). `EditableSchematic.BuildRenderModel` calls `ResolveAllCellRefs` (`:1176`) — every component —
and its own doc comment says it is *"called by `SchematicViewModel` after each model change"*. So the
per-edit cost is roughly **four to five filesystem round trips per referenced component**.

On a local disk and on a LAN that is free, and the design is right: it is what makes *"the librarian's
edit reaches every user without a restart"* true, which is the property the whole workflow rests on.
Over a link with tens of milliseconds of latency, a forty-component schematic is a few hundred round
trips per edit.

**R-sl4-6. Measure before changing anything, and measure a COUNT.** A scratch harness that resolves a
fixture N times and reports **calls to the filesystem**, per component and per edit — not milliseconds.
CLAUDE.md's own rule applies: a timing assertion measures the machine, flakes, and inverts under a debug
build. The count is the number that describes the algorithm, and it is the number a regression would
move.

**R-sl4-7. The freshness guarantee is weakened deliberately, by a stated bound, or not at all.** Today's
guarantee is *"a change on disk is seen at the next resolve."* A short-lived positive-stat cache makes it
*"a change on disk is seen within T of the next resolve."* T must be small enough to be invisible to a
person walking between two machines (order one to two seconds), it must be **stated in the code and in
the design note**, and it must be the only thing that changed. Do not cache the resolution result longer
than the stat; the mtime check is the mechanism, and a stale mtime is a stale drawing.

**R-sl4-8. Never cache a negative.** A cell that did not resolve — a share momentarily unreachable, a
folder mid-rename by the librarian — must be re-asked immediately. Caching "not found" for even a second
turns a transient network blip into a design full of Not-Found glyphs that persist after the network has
recovered, which reads as data loss and is not.

**R-sl4-9. The cache is keyed and dropped exactly as the existing per-workspace memos are** — beside
`WorkspaceRootFinder._rootMemo` and `ExternalCellRef._aliasMemo`, dropped by the same
`WorkspaceRootFinder.InvalidateCache` (`:85`), for the reason that file already gives: a third memo with
its own lifecycle is the one that goes stale.

---

## 3. The tree rescan

`ProjectTreeTool.RefreshAsync` (`:385`) is well built for a local disk: the scan runs off the UI thread,
a focus storm cannot queue duplicate scans (`_scanInFlight`), and a 64-bit signature over the scanned
tree skips the rebuild when nothing changed (`SignatureOf`, `:468`). But **the signature is computed from
the scan's result**, so the walk always happens — on open, on every window activation, and on every
dialog close. After SL1 that walk includes every folder of the referenced library.

**R-sl4-10. A referenced subtree is walked on explicit Refresh, on first expansion, and on workspace
open — not on every focus.** The workspace's own folders keep today's behaviour exactly: they are local
almost always, they are the ones the user is editing, and they are the reason the on-focus rescan exists.
A referenced library is neither: it changes on someone else's schedule, and the user's own gesture
(expanding it, or pressing Refresh) is a better trigger than alt-tab.

**R-sl4-11. A referenced subtree that has not been walked yet renders as itself, not as empty.** A node
that says nothing has been read yet, or that keeps the previous walk's contents, is honest. An empty
library is the exact symptom SL1 exists to remove, and it must not come back through a caching rule.

---

## 4. Gate

`tests/Ui.Tests` (do not touch `src/Core`, `src/Engine`, `RfCore`).

**No timing tests.** CLAUDE.md's benchmark-tier rule and the repo's standing preference both apply:
assert **counters** for the structural property, never a wall-clock threshold.

1. **Filesystem-call count per edit** — a counting seam around the resolver's filesystem access, a
   fixture with N referenced components, and an assertion on calls per edit before and after §2's cache.
   This is the test that catches the regression the cache exists to prevent, and the one that catches a
   future change re-introducing a per-component walk.
2. **The freshness bound holds** (R-sl4-7): change a `.csym` on disk, resolve after T, assert the new
   symbol. Drive the clock through a seam — do not sleep.
3. **A negative is never cached** (R-sl4-8): resolve a missing cell, create it, resolve again
   immediately, assert Resolved.
4. **The referenced subtree is not walked on focus** (R-sl4-10), and **is** walked on Refresh and on
   first expansion — three assertions on the same counting seam as (1).
5. **A partially-walked referenced node renders its previous contents** (R-sl4-11).
6. **Lock file behaviour** (R-sl4-1/-2/-3): taken on a writable open, **not** taken on a read-only one,
   removed on close, a stale one detected by each of the two rules, and the notice offering both answers.
   Drive the host/pid/clock through a seam; a real second machine is not testable and is not needed.

---

## 5. Stop and report

If §2's measurement shows the per-edit cost is already dominated by something other than the resolver —
the connectivity pass, the render, the `.ccell` reads — **stop and report the measurement instead of
adding the cache.** The cache costs a stated weakening of a guarantee the product currently has, and
paying that for a component of the cost that does not dominate is a bad trade that would be hard to
reverse later.

---

## 6. On completion

Findings to `src/Ui/RESOLVED.md` (**not** `CLAUDE.md`).

Update `docs/design/workspace-and-project-tree.md` §3 — the refresh rule gains the referenced-subtree
exception (R-sl4-10), and the freshness bound T belongs in writing beside the *"no `FileSystemWatcher`"*
note it qualifies.

**Report, do not silently absorb:**
- The measured call counts, before and after, per component and per edit. These are the brief's actual
  product; the cache is only what the numbers justified.
- The chosen T, and why that value.
- Whether the advisory lock produced any false statement about another user during the work, in either
  direction — that is the failure R-sl4-2 exists to bound, and it should be reported from observation
  rather than assumed absent.
