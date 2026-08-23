# src/Ui — resolved briefs (detail, off the CLAUDE.md growth path)

`src/Ui/CLAUDE.md` reached 21,417 lines as an append-only phase log and had to be archived to
`src/Ui/HISTORY.md`. Going forward, a completed brief's detail lands here instead — one `##` section
per brief, sparingly, only for findings that are still true, still surprising, and would cost someone
real time to rediscover. `CLAUDE.md` stays for durable, still-true conventions only. Mirrors
`src/Ui/DataDisplay/RESOLVED.md`'s own pattern.

## Opening a workspace took 2.1 s, and four fifths of it was work already on disk (2026-08-23)

**Reported:** a noticeable lag when opening a particular workspace, presumed to be the PDK it
references. The PDK was a fifth of it.

Measured by timing each stage of `WorkspaceViewModel.SwitchToWorkspace` in the running application,
against a workspace that references one large vendor kit (1,266 files) and places 10 generated cells
from that kit's own script generators. Warm, second open in a session:

| stage | before | after |
|---|---:|---:|
| `RestoreInstalledPdks` — read every referenced kit | 396 ms | 425 ms |
| `RegenerateAllGeneratedCells` | **1650 ms** | **3 ms** |
| dock rebuild, `.cws`, theme, tree state, dock layout, 3 documents | ~48 ms | ~23 ms |
| **total UI-thread block** | **2094 ms** | **451 ms** |

First open of a launch: 2643 ms → 717 ms.

### The generated-cell rebuild was 79% of it, and none of the work was new

`SwitchToWorkspace` deleted the whole `.generated-cells` folder and rebuilt every snapshot the
layouts referenced — R-L5g-7, whose brief says "both are cheap once R-L5g-6 holds". That was true
while every generator was a built-in drawing geometry in-process. **A kit's generators are scripts,
and a script runs per cell:** the ten cells here cost 2-800 ms each. None of it is interpreter
startup — it is per-cell geometry, paid in full on every open, for artwork that was already correct
on disk.

The folder is now kept across sessions and pruned instead. What makes that safe is the same property
that made deleting it safe: **the folder's NAME is the content hash**, so a folder that is there is
by construction the right geometry — which is why `GetOrCreate` already returned a hit without
re-generating or re-verifying anything. `RegenerateAll` gained a prune pass; the wipe survives behind
`GeneratedCellsLifecycle.WipeOnOpenAndClose` (off), gated in the one wrapper all three call sites
share, and is still exercised by a test.

**The prune refuses to run rather than guess.** Deleting a cell some layout still references costs
that layout its artwork, so the live set has to be complete to be actionable: if any `.clay` failed
to parse, or the caller held layouts out of the walk (`skipPaths`, which `ReloadPCellGenerators`
does), nothing is collected. An uncollected folder is harmless; a wrongly collected one is not.

### The wipe was silently covering a real staleness gap

`BuildCellName` hashed the technology's **identity** — the resolved `.ctech` PATH — not its content.
While the folder was wiped on every open that never showed: everything regenerated regardless. Keep
the folder and an in-place technology edit resolves straight back to artwork drawn against the old
layers, and circuitRF ships the editor that makes that edit. The stamp is now over what the file
SAYS.

**What the stamp deliberately leaves out is as load-bearing as what it includes.** The stamp is part
of the cell's name, so anything in it renames every generated cell in the workspace when it changes —
regenerating them all and rewriting every layout that places them. `Visible`, `Selectable`, `Color`,
`FillOpacity`, `FillPattern` and `ZOrder` are toggled while looking at a design and cannot reach the
artwork (a generated shape carries a layer KEY; the renderer resolves the rest live), so hiding a
layer must not trigger a rebuild. It is written as an **exclusion** list, not an inclusion list, so a
field added to `LayerDef` later is stamped by default — the cost of forgetting is an unnecessary
regeneration, not artwork drawn against a process that has since changed. The stamp re-serialises the
JSON rather than hashing the file's bytes, so a reformat on save is not mistaken for a change.

One-time cost of the change: the first open renames every generated cell and rewrites the layouts
that place them, through the repointing machinery that exists for exactly that.

### Placing a component re-imported the whole kit

`PushMruPlaced` — which runs on **every component placement** — called `RestoreInstalledPdks`, a full
re-read of every referenced kit from disk, purely to get the kit tiles back beside the reordered MRU.
So did `RebuildLayoutFrom`, i.e. any dock-layout reset or restore. Both need only
`PublishKitPaletteItems()` (0 ms): the palette tool instance is new, but nothing about a placement or
a panel move can change what a kit holds. This was also the third full kit read per launch that the
open-path trace showed.

### Reading a kit re-read every file it looked at

`PdkImporter.ImportEntries` handed each recogniser a `Peek()` closure that re-OPENED and re-READ the
file on every call. Measured in the application: **4,805 reads and ~83 MB for 1,266 files**, where
one read each — ~21 MB — is the whole of what was being asked for. Memoized per file.

### What is left, and why it is not smaller

The kit read is now ~90% of the open (~400 ms: ~120 ms enumerating and recognising 1,266 files,
~170 ms discovering parts, which SPICE-parses every netlist in the kit). It is split into
`ReadReferencedKit` (pure, worker thread — several kits now read at once) and `ApplyReferencedKit`
(UI thread, reference order, unchanged). `ManagePdksDialog`'s Add and Validate read on a worker too;
File ▸ Import ▸ PDK already did.

**With a single kit that moves the work off the UI thread's stack without shortening the open**, and
the window still does not repaint during it. Making it actually repaint means making
`SwitchToWorkspace` async, which leaves the shell interactive with the dock rebuilt and no documents
open — a reentrancy hazard, not a free win. There is also nothing on the open path to overlap the
read WITH: the kit read must finish before the PCell generators exist, which must exist before the
generated cells rebuild, which must happen before the layouts open. It is a serial dependency chain.
Shortening it further means making the read itself cheaper (a persisted per-kit digest), not moving
it.

## Import PDK: the archive door, the surprise dialog, and a worker message that said the wrong thing (2026-08-23)

Three things reported while measuring the open above, all on the PDK import path.

### The .zip door produced a kit that could not work, and a reference that broke on the next open

`PdkImporter` reads entries straight out of an archive. That is enough to say what a kit CONTAINS
and nothing more: everything downstream resolves paths against a directory, and `PdkPartInstaller`'s
own `haveRoot` guard turns off when the root is not one. An archive import therefore produced **no
symbol artwork and no palette icons, no netlist discovery, no compiled models, no simulation settings
and no manifest** — and then recorded the `.zip` ITSELF as the kit's location, so the very next
workspace open reported "the kit folder does not exist" and every part placed from it went
unresolved. The door was offered in the picker and led there.

An archive is now unpacked into the workspace under `kits/<name>/` — the same folder the
archive/share path already uses for a kit it carries, so a kit handed over as a `.zip` travels with
the workspace — and the folder it became is what gets imported. Replacing an already-unpacked kit is
asked about, never assumed: those are ordinary files in the workspace and a hand-written manifest is
exactly the kind of thing that lives there.

**The unwrap rule is the trap in this.** A kit archive is nearly always packed as `<name>/…`, so
extracting gives `kits/foo/foo/…` and the kit is one level down. The obvious test — "exactly one
directory and no files" — also describes a kit whose whole top level is a single `cells/`, and
descending into that returns something that is not the kit, with every path resolved from it wrong in
a way nothing downstream can detect. The test is the folder's NAME matching the archive's, because
that is what an archiving tool actually does. Not unwrapping is the safe failure: an extra level
costs one path segment, which the importer already handles.

### The technology offer arrived after a silent pause

The scan that decides whether to offer a technology ran AFTER the import report dialog was dismissed
— so the user closed the report, watched nothing happen for as long as the walk took, and was then
asked a question out of nowhere. It now runs as the last stage of the import's own progress row, so
the answer is in hand before the report opens and the offer follows it immediately. The scan result
is threaded through the offer into `RunTechnologyImportAsync`: **one import used to walk the same
tree three times.**

The import as a whole now reports through `Messages.BeginProgress` (reading → installing → looking
for process data, indeterminate — no stage has an honest denominator until it has finished), and
`PdkPartInstaller.Install` runs on a worker thread. That last part matters more than it sounds:
installing starts **one worker process per compiled model** to ask what each implements, and it was
doing that on the UI thread immediately after a file picker closed.

Manage PDKs ▸ Add and ▸ Validate read a kit the same way and froze the dialog while doing it; both
now read off-thread behind an indeterminate bar in the result panel they already write into.

### "Starting the worker…" printed once per compiled model, during an open, promising a run

Both lines were true starts and neither was a run. `OsdiModelDiscovery.Find` starts one worker per
`.osdi` artefact, asks what it implements, and disposes it — so the count follows how many the kit
ships (four in the reported kit), and every one borrowed a sentence written for the worker a run
actually waits on: "the first device waits for it to load its models."

The two kinds of start are **indistinguishable from outside** — same program, same provider name — so
the event now carries `ForDiscovery` and the workspace says nothing for a scan. What the scan found
is already reported once, by the install that asked for it. `DeviceWorkerProvider.LaunchForDiscovery`
is a separate method rather than a defaulted parameter on `Launch`, because that one's signature IS
the `DeviceWorkerProviderResolver.Launcher` delegate and widening it breaks the method-group
conversion a test substitutes through.

### Set as workspace default is now ticked

A technology imported into a workspace that has none is the one it will use. Leaving the box clear
meant every layout in that workspace opened on no technology until the user found the setting;
unticking is one click for the rarer case of importing a second process to compare against.

## The zoomed-far-out layout was slow to pan, zoom and hover — and it was never the renderer (2026-08-23)

**Reported:** a hierarchical layout is still slow when zoomed out extremely far, and the slowness
shows up on pan, on zoom, and on plain mouse movement "due to geometry snap".

**The renderer was already fine and was never the problem.** Measured against a placed-cell design
whose one generated sub-cell carries **156,821 rectangles**, placed 24 times (≈3.8 M rectangles
reachable), on a 1600x1000 canvas, a ladder from Zoom-to-Fit out to 4096x further out:

| zoomed out | render (ms) | snap query (ms) | features examined |
|-----------:|------------:|----------------:|------------------:|
| fit        | 8.9         | 188             |            47,178 |
| 8x         | 7.2         | 238             |         3,550,297 |
| 32x        | 4.9         | 1,488           |        33,169,871 |
| 64x .. 4096x | 3-9       | ~1,500-1,770    |        33,873,203 |

`LayoutRenderer.Draw` never crosses 9 ms at any zoom — the L2c/L2e/L2f chunking, stroke-elision and
coarse tiers do their job. **Every millisecond of the reported slowness was one call to
`LayoutSnapQuery.FindCandidates`**, which the canvas runs on every qualifying pointer move. 33.9
million features distance-tested per mouse move is a frozen application, and it explains all three
symptoms at once: hover runs it directly; a pan drag runs it on each move that is not a middle-drag;
and a wheel zoom changes `snapTolDbu`, which is part of `UpdateSnapMarkerCore`'s own sub-device-pixel
skip guard (`snapTolDbu == _snapQueryLastTolDbu`), so the first move after any zoom step is always a
full query.

### Why it degrades with zoom-out specifically

Snap tolerance is a fixed SCREEN distance — `SnapHitTolerancePixels / zoom` — so it grows without
bound as the user zooms out. Far enough out, the tolerance covers an entire placed cell: every
feature in it qualifies, and a search bounded by the tolerance degenerates into a scan of the whole
cell, per placement, per pointer move. `LayoutSnapFeatureIndex.QueryNear` had two regimes and **both
were bounded by the cell's total feature count rather than by what is near the cursor** — a uniform
bucket sweep allowed up to `_features.Count` bucket probes (20.2 M measured), and past that it fell
back to a linear scan of every feature (33.9 M measured). The existing cap
(`LayoutSnapCandidateSet.Cap`) bounded what was KEPT, never what was WALKED.

Note the tolerance is not buying accuracy at that zoom either: all 33.9 M features project into a
24x24-pixel box. The nearest handful is the whole answer.

### The fix — ring search, per kind

`QueryNear` now walks the grid in rings outward from the cursor's own bucket and stops as soon as the
next ring cannot beat what is already kept, with the sweep clamped to the POPULATED bucket range.
Both bounds are needed: the clamp alone still visits every bucket the cell has, and the ring
termination alone still walks empty space out to the radius.

**Per KIND, and that is the load-bearing part, not an implementation detail.** The query's order is
priority-then-distance (R-snp-5), so a `Pin` anywhere inside the tolerance outranks a `Centroid` one
DBU from the cursor — which means a single mixed grid can never stop searching outward, since a
better-ranked feature might still be out there. Split per kind, the order collapses to distance
alone, "the nearest `cap` of this kind" is settled the moment the ring passes the worst one kept, and
merging the per-kind answers reproduces the mixed order exactly (the final answer can hold at most
`cap` of any one kind, and within a kind those are its nearest).

**Two bounds that look equivalent and are not**, each worth ~40x on its own:

1. **The unswept-distance floor must be a true 2-D distance, not the smaller of two per-axis gaps.**
   For a cursor level with the cell but well off to one side — i.e. 23 of the 24 placements in any
   given frame — the rows just outside the swept band are one bucket away in Y and the whole standoff
   distance away in X. Projected onto Y alone that reads as one bucket: a floor so weak it never
   passes anything, so the sweep runs to the end of the range anyway. What is left after ring d is
   the populated box minus the swept box, up to four rectangular slabs, and the bound is the nearest
   of them measured as an honest point-to-rectangle distance (`UnsweptDistanceSq`). With the per-axis
   version the numbers barely moved: 33.9 M -> 24.3 M examined.
2. **The same test again per BUCKET, one level down.** A ring is a Chebyshev shell, a poor stand-in
   for distance once the cursor is outside the cell: first contact then happens at a large radius and
   that shell is hundreds of buckets long, while only the few nearest its closest point can
   contribute. Four comparisons against a bucket's worth of distance arithmetic. 489 K -> 41 K.

**Result, same fixture and ladder:** extreme zoom-out **1,768 ms -> 8 ms**, 33.9 M -> 41 K features
examined; Zoom-to-Fit **188 ms -> 0.4 ms**. Rendering is untouched.

### The one-time index build, moved off the pointer-move path

Bounding the query left the per-cell index BUILD where it was: lazy, inside the snap query, on
whichever pointer move arrived first. Measured in Release on that same generated cell (1,411,373
features), that was **133 ms and 235 MB allocated** — a visible hitch on an input event. Two changes,
and they address different halves:

**Building it costs a fifth of what it did** — 133 ms -> ~35 ms, 235 MB -> 59 MB. Both numbers were
overhead, not features: the build grew a `List<IntrinsicSnapFeature>` to a six-figure length (doubling
its way there) and then copied it out again to split it by kind, and each grid bucket was a
`List<int>` doing the same on a smaller scale. Now `FeatureSink` runs the ONE traversal twice — once
counting, once writing into arrays that count sized — and the grid is a dense CSR table (`BucketStart`
+ `Entries`) filled by counting sort. The dense table is affordable because bucket size is the kind's
own extent over 64, so the populated range can never exceed 65 buckets a side; a probe is now two
array reads rather than a hash lookup, which speeds the query up as well.

**And it no longer happens on a pointer move at all.**
`LayoutEditorViewModel.PrewarmPlacedCellSnapIndices` warms every DISTINCT placed cell on a background
task. Three things about it are deliberate:

- **Hung off `ApplyTechResolution`, not the constructor.** The index bakes in the cell's pins, which
  `CellPins.Resolve` reads through the technology, and `Get(view, tech)` caches by VIEW alone — warming
  before the technology is known would cache an index built against a different one. Every
  `new LayoutEditorViewModel(...)` call site applies a resolution immediately afterwards, so this still
  runs at open.
- **Placed cells only, never `Model` itself.** A resolved sub-cell's view is loaded from disk and not
  edited through the editor, so reading it off-thread is safe. The top-level document is invalidated on
  every change, so a background build racing an edit could publish a snapshot taken BEFORE the edit but
  stored AFTER the invalidation — a silently stale snap index. It stays lazy, which costs one ~35 ms
  build on a six-figure FLAT layout and nothing on any other.
- **Distinct cells, deduped on the resolved cell directory.** A design placing one generated capacitor
  two dozen times has one index to build, not two dozen.

`SnapPrewarm` is exposed internally so `LayoutSnapPrewarmTests` can AWAIT it — a test of a background
task must not have a deadline. Those four tests assert the mechanism (nested cells warmed, the edited
document deliberately not, no task at all when nothing is placed, one index shared across placements)
and never a duration.

**End to end on that design, Release:** open costs 18 ms on the UI thread, the prewarm finishes in the
background, and the first snap query is 20 ms — nearly all of it first-call JIT, since the second is
0.3 ms.

### What holds it

Both new tests in `LayoutSnapFeaturesTests` are correctness/counter tests — no clock, deliberately.

- `QueryNear_MatchesAnExhaustiveScan_AtEveryToleranceAndCursor` — the bounded sweep against an
  independent brute-force scan over randomised layouts, at six tolerances (up to 9e8, far past the
  geometry), twelve cursors (inside, on the edge, and well outside), and four caps including
  unbounded. Compared as (kind, distance) rather than identity, because where several features tie on
  both, which one lands on the cap boundary is arbitrary in either implementation and is never
  observable — the marker is drawn at a position, not at an identity. **This is the test that matters:
  an early termination that is one ring too eager silently returns a WRONG nearest feature, not a
  slow one.**
- `QueryNear_OverAWholeCellTolerance_ExaminesOnlyWhatIsNearTheCursor` — 10,000 rects (90,000
  features), a tolerance ten times the field's own extent, four cursor positions; asserts
  `FeaturesExamined` stays two orders below the feature count. A counter, so it is deterministic and
  cannot flake on a loaded machine.

**One existing assertion had to be re-pointed, and it is worth knowing why.**
`LayoutSnapDenseCostTests.ADenseFieldInsideTolerance_…` asserted `FeaturesExamined > 5_000` to
establish that its fixture was dense enough for the cap to be bounding something real. That was the
same number back when a wide tolerance scanned the whole cell; it now reads a few hundred, so the
check failed on a query that had got strictly better. It now asserts the FIXTURE's own
`FeatureCount`, which is a property of the geometry that no future tightening can invalidate.

## Opening a document from the OS file system — what was already there, and the four things that were not (2026-08-23)

**Asked for:** double-clicking a `.csch` / `.clay` / `.cdd` / `.csym` / `.ctech` (and, added on the
way, `.cem`) in Finder, Explorer or a Linux file manager should open it in circuitRF, as an ORPHAN
when it is not part of the open workspace and as an ordinary document when it is; a `.cws` should
open that workspace, prompting to save first.

**Most of this was already built.** `App.OpenFiles` is the one dispatcher all three arrival routes
funnel through — argv, the macOS Apple Event (`IActivatableLifetime.Activated`), and the Windows
second-instance named pipe — and every by-path opener the new types need already existed and was
already workspace-independent: `OpenOrActivateSchematic`, `…Layout`, `…Symbol`, `…DataDisplay`,
`…Tech`, `…EmSetup`. They were `private`, which is the only reason `App` could not reach them.

### `WorkspaceViewModel.OpenDocumentByPath` — dispatch on the EXTENSION, because that is all there is

`OpenNode` dispatches on `NodeKind`, which the project tree has already worked out. The operating
system hands over a path and nothing else, so the new public `OpenDocumentByPath` switches on the
extension and lands on the same `OpenOrActivate*` methods — same session registry, same
`_openDocsByPath` dedup, same dirty hooks. A file opened from the desktop is therefore
indistinguishable from the same file opened from the tree.

**No workspace-membership special case was written, and writing one would have been the way to get it
wrong.** `IsForeign` / `SourceWorkspaceName` are computed live from the file's own ancestor `.cws`
against `CurrentWorkspacePath` (brief-foreign-documents.md §4), and the dedup is keyed on the absolute
path. So a double-clicked file that IS in the open workspace opens as an ordinary document of it — and
activates the tab the tree would have opened, rather than a second copy — with no code saying so.

### 1. A `.cws` named alongside documents discarded them

`OpenFiles` acted on paths in arrival order. A workspace switch REPLACES the window's documents, so
multi-selecting `Amp.csch` and `Proj.cws` opened the schematic and then threw it away — the same
"launched circuitRF and opened nothing" failure the method's own doc comment exists to have fixed.

Now sorted into workspace-vs-documents before anything opens, and the documents are opened **after
awaiting** the switch (`OpenWorkspacePathAsync`, the awaitable form of `OpenWorkspacePath`; both still
route through `OpenRecentWorkspaceCommand`, so the dirty-work prompt and the missing-file pruning stay
in one place). Awaited, not posted: the switch can prompt to save and the user can cancel it.

### 2. Linux had no second-instance forwarding

macOS delivers an Apple Event to the running app; Windows forwards over a named pipe. Linux had
neither, so every double-click `exec`s the `.desktop` entry's `Exec=` line and gets a **whole new
copy** of circuitRF. That was survivable with three registered types and is not with nine: a user
inspecting three files would get three applications, and the second copy's answer to "is this file
part of the open workspace?" is no — because that copy has no workspace open.

`Program.cs` now mirrors the Windows branch over a Unix domain socket in `XDG_RUNTIME_DIR`. **The
lock file, not the socket, decides who is first** — two launches racing can both find no socket to
connect to and would then both bind one; .NET implements `FileShare.None` on Unix with `flock()`, so
an exclusive `FileStream` is real cross-process exclusion. A socket file outlives its process, so a
stale one is unlinked before binding, which is safe **only** while holding that lock. When the lock is
held by someone we then cannot reach (still starting, or wedged), the fallback is to start normally:
the user asked to see a file, and a second window showing it beats no window at all.

`HandleFilesInternal` also calls `Activate()` now. A forwarded open that leaves circuitRF behind the
file manager looks exactly like nothing happened.

### 3. A double-clicked file got the DEFAULT dock layout, not the user's

`OnFrameworkInitializationCompleted`'s startup-file branch skipped `ApplyLaunchSettings` outright.
That is right for the launch ACTION (the file the user picked owns the window, not Welcome or
open-last-workspace) and wrong for the other two things that method does: `ApplyWindowLayout` and
`ApplyShowDockersOnLaunchPreference` describe the SHAPE of the window and have nothing to do with what
it opens. So a file opened from the desktop came up in the default layout with the user's own Window
Layout preference ignored. The macOS Apple-Event path had it too, from the other direction —
`OnActivated` sets `_launchHandled`, which suppresses the whole of `ApplyLaunchSettings`.

Split out as `ApplyLayoutPreferences`, called by both file-open startup paths. **Synchronous on
purpose**: `ApplyWindowLayout` calls `RebuildLayoutFrom`, so it has to finish before a document is
opened into the dock, and an awaited version makes that ordering something each caller has to
remember. `OnActivated` applies it only when the event IS the launch (`isLaunch = !_launchHandled`
before the flag is set) — a later Apple Event, delivered to an app that is already up, would otherwise
rebuild the dock out from under the documents already in it.

### 4. The dispatcher has two halves and both can forget a type

`App.OpenFiles` accepts the extension; `OpenDocumentByPath` opens it. A type listed in the first and
missing from the second falls out of `default: return false` — accepted, silently dropped, no error
anywhere. `EveryDocumentTypeTheAppDispatcherAccepts_IsOpenedByTheWorkspaceViewModel` holds that shut,
alongside the three per-platform packaging parity tests (see `packaging/RESOLVED.md`).

### The three worries that turned out to need nothing

- **A `.clay` with a wBond overlay.** Nothing extra. `WBondCell.TryAttach` runs inside
  `BuildLayoutSessionVm`, the single funnel both "open as a tab" and "push in" go through, and the
  sidecar is found by STEM (`Amp.clay` ↔ `Amp.wBond`, WB40). A Finder-opened `.clay` gets its wires on
  exactly the same terms as one opened from the tree.
- **A `.cdd` with relative data-source references.** `OpenOrActivateDataDisplay` already catches and
  reports through `Messages`; traces it cannot resolve render nothing. Deliberately not guarded
  against — "let me look at this file" is the whole point of the feature.
- **A `.cws` double-click with unsaved work.** Already prompts: `OpenRecentWorkspace` does
  `HasAnyDirtyWork` → `PromptSaveBeforeClose` before `SwitchToWorkspace`.

### Left as found, and worth knowing

A **foreign** `.clay`'s wire DRC resolves its assembly rules from the CURRENTLY open workspace
(`ResolveWorkspaceAssemblyRules` reads `CurrentWorkspacePath`), not from the layout's own ancestor
`.cws` — unlike technology resolution, which does walk. Pre-existing; a foreign layout is now much
easier to reach, so it is easier to hit.

## What a page-scroll in the .ctech editor actually costs (2026-08-23)

Reported as Page Up/Down being "a little slow to respond" on a 377-layer technology, "even though
only ~15 to 20 layers are actually viewed". **Measured under a headless Avalonia host** rather than
reasoned about, and two of the three obvious explanations were wrong.

### What it is not

- **Not a virtualization failure.** The list realizes 22 containers out of 377. The `ListBox`
  (rather than a bare `ItemsControl`) and the bounded `*` grid row are both doing their job.
- **Not the key handler.** The keystroke path resolves its `ScrollViewer` with a
  `GetVisualDescendants()` walk, which reads as the expensive thing and is **0.0003 ms** — the
  scroller sits near the top of the list's own template, so a depth-first walk finds it immediately.
  This was the first hypothesis and it was wrong by four orders of magnitude.
- **Not the fill-pattern dropdown's items.** Measured at 54, 8 and 0 patterns: 176 / 165 / 176 ms. A
  closed `ComboBox` does not realize its items.
- **Not a Debug artifact.** Release measured the same.

### What it is

A page-scroll realizes a whole fresh page of rows, and **a row of this table was 199 visual nodes**.
Not because it has eleven columns — because a stock `TextBox` is expensive to realize: its template
carries a `ScrollViewer`, that scroller's **two `ScrollBar`s**, and a `DataValidationErrors` wrapper.
Seven text cells per row produced **12 `ScrollBar`s in a row of single-line fields that can never
scroll**. 22 rows x 199 nodes ~= 4,400 nodes per keystroke, at roughly 40 us each.

**Setting the scroll-bar visibilities to `Disabled` does nothing** — measured, no change at all, in
nodes or in time. The bars are built by the template whatever their visibility; only replacing the
template removes them.

So `TextBox.cell.compact` is a stripped template — a `Border` named `PART_BorderElement` (so the
theme's focus and hover states still find it) around a `PART_TextPresenter`, and nothing else.
Typing, selection, caret and Home/End were verified through the headless host, not assumed.

| | nodes/row before → after | page-scroll before → after |
|---|---|---|
| Layers | 199 → 127 | ~185 ms → ~140 ms |
| Stackup | 273 → 235 | ~67 ms → ~48 ms |
| DRC Rules | 156 → 137 | ~140 ms → ~116 ms |
| Interchange | 115 → 115 | unchanged — see below |

### The rule that limits where it can be used, and the two things it costs

**Only a FIXED-width field holding a short, bounded value.** What the stripped template drops is the
`ScrollViewer`, and the `ScrollViewer` is what scrolls a long value sideways to keep the caret
visible while it is being typed. Four digits in a 34 px box cannot outgrow it; a layer name in a
stretch column very much can, and would clip silently with the caret off-screen. Every free-text
column keeps the stock template.

It also drops the watermark. That is why **Interchange gained nothing**: its only two bounded numeric
cells put the layer's own number in `PlaceholderText` as the value the GDSII field falls back to when
blank, which is real information, so both keep the stock template. A tab can be measured, found
expensive, and still have nothing safe to give up.

Both constraints are held by a test that scans every compact cell for an explicit `Width` and the
absence of `PlaceholderText` — it caught three of the twelve on the first run, which is exactly the
mistake it exists to catch.

### Still open

~140 ms is better, not instant. The floor is the remaining ~127 nodes x 22 rows, and the rest of that
is the editable controls themselves — two checkboxes, a colour button, a dropdown and four command
buttons per row. Getting materially below it means fewer controls per row (a display-only row that
becomes editable on click), which is a product decision rather than an optimization, so it was not
made here.

## Page Up/Down dead in the .ctech editor until something was clicked (2026-08-23)

Reported as "Page Up / Page Down are not recognized on the Layers tab when I first open the .ctech
file". Clicking any field made the same keys start working, which is what made it read as an
intermittent rather than as a missing line.

**The editor's scroll handler tunnels from the view root**, deliberately — a `ListBox` handles those
keys itself by moving the selection, and these lists are flattened so that selection is invisible, so
the keystroke would appear to do nothing while quietly changing what was selected. Getting there
first is what makes the key scroll the pane.

The cost of tunnelling from the view is that the handler only ever sees a keystroke **already routing
through that view** — something inside it has to hold focus. On first open nothing does: the document
is activated before its view is bound, so focus is still wherever it was and the key routes somewhere
else entirely.

`TechDocument` already implements `IActivatableDocument` and the workspace already raises
`RequestActivationFocus` on it. **`TechEditorView` was the only one of the four document views that
never subscribed** — the schematic, symbol and layout views all take focus through this same hook,
including the `ConsumeActivationFocus` half that catches the activation which fired before the view
existed to hear it. That pending-flag read is the first-open case specifically; subscribing without
it fixes every activation except the one that was reported.

**Focus lands on the view root, not on the visible tab's list and not on a row's field.** Focusing the
list would make the first thing the user sees a control with a selection, in lists whose rows are
flattened precisely so selection stays invisible; focusing a field would put a caret in an editable
process value nobody asked to edit. The root lands on `TargetScrollViewer`'s own already-written
fallback — "the visible tab's row list" — which resolves correctly for whichever tab is showing and
keeps working across tab changes. That fallback existing at all is a good sign it was the intended
design; only the focus half was missing.

The root needs `Focusable="True"` to be a focus target and `IsTabStop="False"` so a control that
exists *only* as a programmatic target does not join the Tab cycle the user walks through.

**Undocking is the same dead keyboard by a second route, and the activation hook does not cover it.**
Floating the editor builds a NEW window around the view; a new window's activation is not the dock's,
so no `IActivatableDocument` request fires and the subscription above never runs. Attaching to a
visual tree is the one event both routes share, so focus is taken there as well — **but only when
nothing else already holds it**. An attach also happens on an ordinary dock rearrangement, and taking
focus unconditionally would pull the caret out of whatever panel the user was typing in. The check is
made INSIDE the deferred action, not before it: on the undock path the view is still moving between
windows when the attach fires, so a top level asked any earlier is the one being left rather than the
one being entered.

**Guarded by a source scan, not by an exercise** — there is no headless Avalonia host in this suite,
which is the same reason `TechEditorScrollKeys` exists as a framework-free type beside its view. The
test asserts, for every activatable document, that its view both subscribes AND consumes the pending
flag; verified to fail on the unfixed file with the diagnosis in the message, so a fifth document view
added later cannot repeat this silently.

## Layer fill patterns — a process's stipples are read and rendered (2026-08-23)

Layer display was colour and one opacity. A process layer table is not: it runs to hundreds of rows
over a few dozen colours and separates them by a repeating **fill mask**, not by hue. Reading the
colour and discarding the mask rendered a whole process as a few dozen indistinguishable washes.

### The measurement that settles what was actually wrong

The reported symptom was "every layer has the same alpha, we must be dropping the kit's
transparency". The transparency half is false and worth recording so nobody re-chases it: one open
vendor kit's layer table states an explicit transparency flag on **all 377 layers and it is `false`
on every one**. There is no per-layer transparency in it to drop, and the importer's flat 0.35 was
faithful on that axis.

What the same file *does* vary:

| | |
|---|---|
| layers | 377 |
| distinct fill colours | 38 — **373 of the 377 share theirs with another layer** |
| distinct fill masks | 54 |
| distinct colour+mask appearances | 132 |

So the collisions were real and the cause was the mask. Worst single colour: 43 layers on one hue,
separated by 16 different masks. Frame colour differs from fill colour on only 21 rows and both
brightness fields are 0 on all 377 — neither is a second axis worth reading.

**After the change, that table imports as 132 distinguishable appearances instead of 38**, 291 layers
stippled and 86 correctly drawn outline-only.

### What is read, and the two references that are not in the file

A mask reference is a letter and a number: one letter means "the pattern list this file defines for
itself", the other means "a built-in every reader is assumed to have". Only the file's own list is in
the file — the built-ins are the writing tool's, and the file states 108 pattern blocks for the first
kind and nothing at all for the second.

Only **two** built-ins are honoured: index 0 is solid, index 1 is hollow. Any other built-in index
falls back to solid and is counted in the import notes. Inventing a bitmap for one would draw a
pattern the process never specified, which is worse than a flat fill because it looks authoritative.

A pattern's own stated position — not its position in the list — is what a reference names, so a file
that numbers sparsely or out of order still resolves. Patterns are carried by NAME into the
technology (an index is invalidated by a reordering edit, silently, in a way that repaints layers
rather than failing), deduplicated on the way in, and pruned to what the layers actually reference.

**Hollow is expressed as a zero opacity, not as a synthetic all-clear mask** — the model already says
exactly that, and a synthetic entry would be one more table row meaning what a zero already means.
The converse still has to work, because a process states "outline only" both ways: a mask with no set
texel must paint nothing. It first fell through to the solid-fill branch and painted everything —
the exact inverse of its one instruction — which a pixel test caught.

**A stippled layer imports at full opacity, not at the 0.35 wash.** The mask is already what lets the
layer beneath show through; a sparse pattern behind a 35% wash is invisible.

### The stipple is a SCREEN-space texture

A bitmap shader with an identity local matrix is in path space, so it would zoom with the geometry —
a moiré field at low zoom, enormous stripes at high zoom, useless as an identifier at either end. The
local matrix is a pure scale of one device pixel per texel, which keeps the density fixed through
zoom while still letting the pattern travel with the geometry under a pan (a device-space anchor
would make it swim across the artwork instead). Inside a placed instance the magnification is folded
in the same way the stroke width already is, or one cell's metal reads as a different layer from
another's.

### It costs nothing, and the guard for that is a counter

The intuition runs the other way — a shader per fill sounds expensive next to a flat colour — so it
was measured on a dense via array at 25k, 100k and 500k shapes, at full extent and zoomed 8x.
Stippled vs solid came out between **x0.88 and x1.29, and at 500k it was x0.99 / x0.97**; several
configurations were *faster* stippled, because a sparse mask writes fewer pixels than a flat fill.

The reason is structural: **the paint, and with it the shader, is built once per layer per frame**,
the mask bitmap is cached across frames keyed by (mask, colour), and what changes per shape is only
the inner pixel loop. Three call sites build a layer's fill paint (the per-layer draw, the placed
instance, the drag/paste ghost) and they were three independent copies of the same two lines; that
was harmless while a fill was a colour and an alpha and stops being harmless the moment it is also a
mask at a zoom-compensated scale, so they now share `LayerFillPaint`.

There is **no timing test** for any of this, deliberately. With no measurable margin there is nothing
to defend, and a wall-clock assertion would measure the machine and flake. What is gated is the
structural property, as a counter: `PaintsCreated` must not scale with shape count. Moving the
construction inside the per-shape loop is the obvious refactor for anyone who later wants a per-shape
colour, and on a 20,000-via array it turns one shader into 20,000 — invisible in a screenshot,
invisible in a small scene, and visible only as a frame time on a file too big to bisect quickly.

### Smaller things

- The Tech Editor gets a **Pattern** column: a dropdown over the technology's own table, never a text
  field (a typed name matching nothing repaints the layer solid with nothing to say why) and never a
  circuitRF-invented palette (a stipple is process data; offering our own masks would let a layer be
  given a fill its process never specified). Three grids in that view share one column layout — the
  filter bar, the header and the row template — and all three have to grow together or the columns
  stop lining up.
- A name that resolves to nothing fills **solid**, not blank: a dangling reference should be visible
  and recoverable, not a layer that silently disappears or a technology that will not open.
- `.ctech` carries the table as an optional field, so every file written before this reads back with
  no patterns and every layer solid — which is exactly what those files meant.

## PCell parameters get the editor the kit already declared (2026-08-23)

Every PCell parameter rendered as the same free-text box. A model name, a yes/no flag, a gate count
and a capacitance the cell *derives from its own geometry* were visually identical and equally
editable. Wire version 7 carries the metadata a kit was already publishing, and the parameter list
renders four editors instead of one.

### A kit does say all of this — it was being discarded at the bridge

A vendor cell's `defineParamSpecs` takes a label and an optional constraint on every parameter, and
`cni/bridge.py` read neither. Measured against one open vendor kit — 34 generators, 577 parameter
rows:

| | count |
|---|---|
| `ChoiceConstraint` | 127, of which 42 are two-valued yes/no pairs |
| `RangeConstraint` | 9 |
| declared Python `bool` | 14 |
| declared `int` | 43 |

After the change, **153 of those 577 rows (26%) render as a checkbox, a dropdown or read-only text.**
The rest stay free text, correctly — nothing is declared about them.

There is no side-car file, no `editable` flag anywhere in the kit. `defineParamSpecs` plus the cell's
own code is the whole of what it states, which is why the inference below is measured rather than
read off.

### A solve-for selector is a real signal and its stated value can be the opposite of the truth

Ten of that kit's cells declare a CDF-style selector — a choice list like `['C','w','l','w&l']` on a
capacitor, `['R','w','l']` on a resistor, `['R,A','w,A','l,A',…]` on another. It is recognised
**structurally**, not by the parameter being spelled "Calculate": a selector is any choice list whose
entries, split on `&` and `,`, are all names of *other* parameters of the same cell. Nothing keys off
the spelling, because the next kit spells it differently and a name list is a table to maintain.

**Read literally it was wrong on every cell that has one.** The capacitor defaults its selector to
`'w&l'`, which says w and l are the outputs and C is the input. The code does the reverse:
`setupParams` reads only w and l, and C is never read at *any* setting of the selector. Generating
the cell with C at its default and at two other values gives byte-identical geometry; changing w or l
changes it. The vendor's own dialog is where the back-solve lived, and the layout port kept the
declaration without the behaviour. A second capacitor cell in the same kit makes it explicit —
`setupParams` *overwrites* its own C from w and l and throws away what it was sent.

So the rule is the intersection of two things, and **each half alone gets a different case wrong**:

- **Selector alone** → believes the declaration, locks w and l, leaves C editable. Exactly backwards.
- **Reads alone** → the model name, the multiplier, the initial condition and the temperature rise
  are never read either. They are netlist parameters that never touched the artwork and are still the
  user's to type into. This would take the model name away.

Reads are observed by handing the cell a `dict` subclass that records subscript access
(`_TrackingValues`), so it is what the cell *did*, not what it declares.

Where the two disagree the parameter stays **editable**, which is the failure direction that costs
nothing: one resistor cell in that kit assigns its resistance parameter to an instance attribute and
then never uses it, so it reads as an input and keeps its box.

### Why a derived value could not simply be shown live

An output is not read, so the value stored with the design is whatever it was stored with — it stops
matching the geometry the moment a dimension moves, and the old text box displayed that stale number
as though it were an input. Two things were tried before settling:

- **Reading the value back off the cell instance after generation.** Refuted: a cell commonly does
  `self.w = Numeric(params['w']) * 1e6`, so the attribute holds a micrometre number where the
  parameter holds engineering-notation text — a different unit under the same name. An attribute that
  happens to match a parameter name is not that parameter.
- **Re-running `defineParamSpecs` to recompute the default.** It computes the derived value from
  process constants rather than from the instance, and typically feeds the one value to both
  dimensions. Only correct for a square device.

Nothing in those cells computes the quantity for arbitrary geometry — the vendor's dialog callback
did, and the kit's calculators are reachable only by name. So the wire says both things separately: a
`computed` name list, and an optional `computedValues` map. A name with no value is the honest claim
"this is derived, and I cannot tell you to what" — enough to stop offering an edit box, without
inventing a number. A generator that *can* state the value gets a live-updating row for free, through
`circuitrf_pcell.reports_computed`, which is how a kit's own calculators are wired back up from the
kit's setup script without touching the vendor's files.

**The ordering inside that hook is the part that is easy to get wrong.** The host learns a parameter
is an output by MEASURING the cell, and records the name with no value; a calculator supplied beside
the cell runs afterwards and must fill that in. A plain `setdefault` sees the name already present,
keeps the empty claim, and drops every value silently — the readout looks wired up and never shows a
number. `None` is treated as an absence, not as a value.

A second trap in the same hook: the callback must be handed the parameters **with declared defaults
applied**. The host sends the values it holds and no others, so a parameter left at its default may
be absent entirely. A generator never notices, because it passes its own fallback to every accessor;
a calculator standing outside the cell has no way to know what each parameter should fall back to and
reads 0 for every one. `Parameters.with_defaults` exists for exactly this.

### Where it is persisted, and why not in memory

The derived facts ride in the generated cell's own `.clay`, on `PCellOrigin`. A generated cell folder
is reused on a plain existence check — nothing regenerates on a hit — so a memory-only cache would be
populated for the cell just placed and empty for every cell already on disk, and the same capacitor
would offer an edit box for its C in one session and not the next. They are **not** inputs to the
folder's content hash: an output is a function of the parameters that already are, so hashing it
would only add a second spelling of the same thing.

### Smaller things worth keeping

- **A two-valued choice list is not automatically a checkbox.** A shape choice of two named shapes is
  two choices with no unchecked state; it stays a dropdown. The pair is matched against a vocabulary
  (yes/no, true/false, on/off, and SKILL's `t`/`nil`), never inferred from the count.
- **Ticking a checkbox writes the kit's own word.** A flag a kit spells with words goes back in those
  words, not as `false` — the kit has no case for a Bool there. A parameter genuinely declared `bool`
  does get a Bool. The vocabulary follows the declaration, not the control.
- **An out-of-list value is offered, not corrected.** A value from an older file that the generator
  does not list is appended to the dropdown and selected. Snapping it to the nearest listed choice
  would change artwork nobody asked to change, and rendering an empty box would make the first click
  do that silently.
- **A derived parameter is refused at the commit, not merely disabled in the view.** A write would
  produce a new content hash, a new generated cell, and a value the generator overwrites on its next
  run — an edit that appears to take, changes nothing visible, and orphans a cell.
- **Choices cross in the parameter's own kind.** A choice list flattened to strings for an int-kinded
  parameter never compares equal to that parameter's value: the dropdown shows the right items with
  none of them selected.
- **A label identical to the name is not sent.** It would say nothing and would suppress the name the
  host shows in its place. Where a label *is* shown, the name stays reachable in the tooltip — it is
  what every commit and every netlist still keys by.
- `_declare`'s "nothing is converted, in either direction" rule is untouched. All of this is display
  metadata; what a cell *receives* is unchanged.

## The project tree's dirty mark on FILE nodes — two reports, one shape (2026-08-21)

Two owner reports a few minutes apart:

1. *"after I saved a .cdd file to my results directory, the project tree view still indicated it was
   dirty in the tree."*
2. *"a dirty .cem does not show as dirty in the Project tree /em folder."*

Both are about the same mechanism from opposite ends — a mark that would not clear, and a mark that
would not appear — and both were invisible failures: nothing threw, nothing logged.

### 1 — the `.cdd` that would not go clean

**The `.cdd` node had no push site at all.** Cells (`.csch`) and technology files (`.ctech`) each have
one — `ProjectTreeTool.SetCellDirty` / `SetTechFileDirty` (as the setters were then named),
called from `WorkspaceViewModel` whenever
the corresponding editor's dirty state changes. A Data Display node's mark was written by exactly one
thing: `RestoreDirtyFlags`, the pass round-7 added so a REBUILD re-derives every node's mark from
`ITreeActions.IsNodeDirty`. The rebuild runs on the workspace window's `Activated`. **Saving raises no
`Activated`**, so the mark a previous focus change had put there stayed on the node until some later,
unrelated one happened to rebuild the tree — the document tab's own bullet cleared immediately, and
the two disagreed for as long as the user stayed in the window.

Note the symmetry with round 7's item 1, and that it is the mirror image rather than the same bug:
there, a rebuild DROPPED a mark that was still true; here, no rebuild happens and a stale mark that is
no longer true SURVIVES. A pushed flag on a rebuildable view object needs both halves — the rebuild
must pull it back, and every state change must push it.

The fix adds the missing push (`ProjectTreeTool.SetFileDirty`, wired by
`WorkspaceViewModel.WireDataDisplayTreeDirty` at all three `DataDisplayDocument` creation sites: New
Data Display, open-or-activate, and the post-run auto-open), plus two edges that would otherwise have
been the same staleness in a different door:

- **The pushed value is `DisplayWindowViewModel.HasUnsavedChanges()`, not
  `DataDisplayDocumentViewModel.IsDirty`.** The baseline comparison is what `IsNodeDirty`, the
  close/quit prompt and the Window menu all use; the VM flag only mirrors the `DirtyChanged` event and
  can lag a live edit (`WorkspaceViewModel.WindowMenu.cs` already re-derives around it for the same
  reason). The tree must not disagree with the prompt that decides whether work is lost.
- **A scratch display saved through the picker refreshes the tree instead of pushing.** Its file did
  not exist when the tree was last scanned, so there is no node to mark — and the `DirtyChanged` the
  save raises arrives while `FilePath` is still null, because `SaveAllAsync` fires it BEFORE
  `ConfigPathSaved` sets the path. `SaveDataDisplayDoc` therefore calls `ProjectTreeTool.Refresh()`
  after `Materialize`, and the node the rebuild creates asks `IsNodeDirty` for itself, arriving clean.
  This is also the path a post-run auto-created display takes: it is registered in `_openDocsByPath`
  under its would-be path but with `FilePath` still null, so its first save goes through the picker.
- **`OnDockableClosed` clears the mark of a closing display** (generalised below to every file-backed
  document whose dirty answer comes from the open documents alone). A "Don't Save" close removes the
  document, so nothing can answer the question any more — and the mark it pushed would otherwise stand
  until the next rescan.

Gate: `tests/Ui.Tests/DataDisplayTreeDirtyTests.cs` (6). The tree-tool half is a real behavioural test
(`ProjectTreeTool` needs only a scanned folder — no Avalonia, no dock factory); the save ordering is
tested for real too (a save raises `DirtyChanged` with `HasUnsavedChanges()` ALREADY false, which is
what makes the push clear rather than re-assert the mark); the `WorkspaceViewModel` wiring is a source
claim naming the mechanism.

### 2 — the `.cem` that never went dirty

`WorkspaceViewModel.HookEmSetupDirty` is a copy of `HookTechFileDirty` and called the `.ctech` setter,
`ProjectTreeTool.SetTechFileDirty`. That setter guarded `node is { Kind: NodeKind.TechFile }` — and a
`.cem` node is `NodeKind.EmSetupFile`, so **every push was found, kind-tested, and thrown away in
silence.** The hook fired correctly, the path was right, the node existed, and the mark never appeared.

Worth keeping the two facts about where a `.cem` lives, because they are what makes the node kind
correct in the first place: the file goes in `<workspace>/em/`, an ordinary user folder listed by
extension, and NOT beside its `.clay` — a cell's `layout/` sub-folder is enumerated with
`"*" + ViewExtension(vt)`, i.e. `*.clay` only, so a `.cem` written there is invisible in the tree
entirely (`OpenOrCreateEmSetupForLayout` documents this).

### The shared fix: one setter, not one per kind

`SetTechFileDirty` and the new `SetDataDisplayDirty` are gone; there is one
`ProjectTreeTool.SetFileDirty(path, isDirty)` that marks whichever file node is at that path (guarded
only by "is this a kind something can EDIT" — `ViewFile`, `DataDisplayFile`, `TechFile`, `EmSetupFile`,
`HarmonicaFile`, `WBondFile`). `SetCellDirty` stays separate: a cell is the one node kind that is a
directory. The path already identified the node uniquely, so the per-kind parameter added nothing but a
way to pick the wrong one — and picking the wrong one cost nothing at compile time, at run time, or in
any log. **A guard that can only ever be wrong about its own caller is a silent failure waiting for the
next file type.** `SetFileDirty` also normalises with `GetFullPath`, so a relative `FilePath` reaches
its node instead of missing by spelling.

`OnDockableClosed` now clears the mark for a closing `.cdd`, `.ctech` or `.cem` — the three whose dirty
answer `IsNodeDirty` derives from the OPEN documents alone. Deliberately not for schematic/symbol/layout:
a dirty session for those OUTLIVES its tab in the session registry (that is what the orphaned-dirty-session
prompt exists for), so their mark is still true after the tab closes.

Gate: `tests/Ui.Tests/Em/EmSetupTreeDirtyTests.cs` (6) — a real `.cem` node in a real scanned `em/`
folder, asserted to be `EmSetupFile` and markable; one setter proven to serve `.cem`, `.ctech` and
`.cdd` while leaving a `.npy` alone; an edit→save round trip through the real `EmSetupEditorViewModel`
and `EmSetupDocument`; and a scan that no kind-specific setter has come back.

## Match Designer round 7 — and the three application-wide defects it exposed (2026-08-20)

The owner's seventh round. Three of the items were not Match Designer bugs at all: one is in the
project tree, one is in a control the Data Display shares, and one is in a file format.

**1. "Closing the Match Designer made the project tree's cell go from dirty to NOT dirty, while the
document stayed dirty." Closing ANY window does that, and the Designer only happened to be the
second window open.** The dirty indicator is *pushed* onto a tree node — `ProjectTreeTool.SetCellDirty`
sets `node.IsDirty`, and that flag lives nowhere else. `ProjectTreeView` re-scans the workspace on the
hosting window's `Activated` (a debounced `tool.Refresh()`), which is exactly what closing a second
window raises, and `RebuildVmTree` throws every node away and builds new ones whose `IsDirty` is the
field default. So every unsaved cell in the tree silently went clean on a focus change. Nothing had
LOST the state: `ITreeActions.IsNodeDirty` — which the per-node Save context menu already uses —
answers the same question from the session registry and the open documents, and both survive a rescan.
`RebuildVmTree` now walks the new tree and asks it (`RestoreDirtyFlags`). Worth remembering as a shape:
**a flag pushed onto a rebuildable view object needs the rebuild to pull it back**, or the push site
looks correct forever and the bug reads as belonging to whatever raised the rebuild.

**2. The hard crash — `NullReferenceException` inside `Avalonia.Controls.Primitives.Popup.RootTemplateApplied`,
reached from `IconSelectButton.OnButtonClick`'s `IsOpen = true`.** The stack is entirely inside
Avalonia and the exception kills the process. The defect on our side is an ORDER-of-operations one in
`IconSelectButton.OnListBoxSelectionChanged`: it published `SelectedItem` FIRST and closed the popup
afterwards, so everything the application does in reaction to a selection ran inside the ListBox's own
`SelectionChanged`, **with the popup still open**. In the Match Designer that reaction includes
`MatchDesignerViewModel.RefreshOrderChoices`, which `Clear()`s and refills the very
`ObservableCollection` the open popup's ListBox is bound to — a re-entrant mutation of a live popup
that makes the ListBox raise `SelectionChanged` again from inside the handler, and leaves the
`PopupRoot` being torn down a beat later with a layout pass still owing. Fixed by closing first and
posting the publish to the next dispatcher turn, so **no consumer of this control can run while its
popup is open, whatever that consumer does**. A second, smaller bug fell out of the same place: a
REPLACED `ItemsSource` clears the ListBox's selection with nothing telling the control, so the popup
opened next with nothing highlighted — `OnPropertyChanged` now re-syncs on `ItemsSourceProperty` too.
The Designer's Order selector is the only `IconSelectButton` in the application whose item list is
rebuilt by the act of choosing from it, which is why it is the one that crashed.

**3. "The `.csch` shows a Match instance with Expression all crazy text."** It was base64, and the
reason base64 existed is real but belongs to ONE format: a `.cnl` is whitespace-delimited and its only
string escape is a pair of quotes with no way to escape a quote inside one, so a design's JSON — which
is all quotes — cannot be a `.cnl` token. **A `.csch` is JSON and never had that problem.** So the
constraint is discharged where it applies: `MatchEmbedding.Encode` writes plain JSON (shorter than the
blob it replaced, as well as legible), and `CnlWriter.FormatParam` converts a brace-leading `Design`
expression to a bare unpadded token on its way into a `.cnl`. `MatchEmbedding.TryDecode` already read
both spellings, so every existing file still loads, and `EncodeToken` is there for the handful of
tests that hand-build `.cnl` text. Making the payload readable also exposed that four COMPUTED
properties (`Omega0`, `W`, `Termination.HasReactance`, `AbsorbedType`) were being serialized as if they
were inputs; they carry `[JsonIgnore]` now.

**4. "The error messages in the grid area don't go away."** `InlineEditNote` was cleared only on the
way *into* the next inline edit, so a refusal about an element outlived the ladder it was about —
change the termination, the order or a transform and the sentence stayed on screen naming a value the
network no longer had. It is cleared at the top of `Refresh` now. The one path that both refreshes AND
reports (`SetElementValue`'s partial solve) already writes its note after its `Refresh`, which is what
makes the clear safe; a test pins that ordering rather than trusting it.

**5. "After a probe the R, L and C units don't match the Settings units."** They never did — the
termination fields carried their own unit (a hard-coded `"Ω"`, and `AutoUnit` for the reactance) with
no connection to §9.9's per-dimension display units. While the user TYPES the values the two agree by
accident, because a typed unit pins the field. A probe writes a measured value nobody typed, Auto then
picks per value (0.34 pF arrives as "340 fF"), and the disagreement becomes visible. The settings unit
is the default now and a typed one is an override; `ResetDisplayUnits` drops the override on both
probe-application paths. **A typed unit that equals the settings unit deliberately does NOT pin** — the
resistance field re-commits its unit on every edit, so pinning unconditionally would freeze the field
against Settings the first time anyone typed a number into it.

**6. Round 7's own small bugs, each with a cause worth one line.**
- *"Terminal 2 is matched and I get no warnings, but the instance still says `Z = 50 Ω (target 50 Ω)`."*
  The guard was NUMERIC (a relative 1e-9) under a label RENDERED at three significant digits. A probed
  termination carries 49.999999999999993; the ladder reached it to a part in 10¹⁰ but not 10⁹. The
  comparison is on the rendered strings now — the same rule `CommitInlineEdit`'s `RendersAsShown`
  already applies in the same pane, and the exact shortfall still lives on the Π N² line.
- *The inline editor on a `TermG` opened holding the `(target …)` suffix, which its own parser then
  refused.* The window seeded the box from the LABEL — what the canvas drew, annotations and all — so
  the user's way out of an unmatched design was the one field the unmatched design had broken.
  `MatchInlineEditTarget.SeedText` is computed from the value instead.
- *An absorbed L or C could not be edited.* It went down the ELEMENT path, which aims the transform
  rack at a target, and no transform can move an absorbed element — so the refusal ("C1 is set by the
  synthesis. Add a transform…") was truthful and wrong. An absorbed element IS `Termination.Value`;
  it now resolves to `MatchInlineEditKind.TerminationReactance` and is written straight through, like
  the `TermG` beside it. **`MatchLadderElement.AbsorbedEnd` is the new carrier** — the layout is all the
  canvas's hit-test can see.
- *…and the tolerance that guards "nothing changed" cannot be copied between quantities.*
  `SetTerminationResistance`'s `1e-12 * max(1, R)` reads as rounding noise in ohms; the same expression
  on farads is **an absolute floor of one picofarad**, and it swallowed 1 pF → 2 pF whole. A reactance
  has no natural scale, so its tolerance is purely relative.

**7. "Can it really not match 50 Ω to 5 Ω ∥ 1 pF at 2 GHz?" — it can, at order 4, and the refusal was
right but not actionable.** Measured over 1.8-2.2 GHz: Chebyshev-Fano needs Π N² = 10, and at **order 3
the whole rack reaches 1.016**, so there is genuinely nothing to offer; **order 4 reaches it and gives
−43.5 dB** worst in-band return loss, order 5 −48.1 dB, and two-ended Chebyshev reaches it at order 3.
The user was one picker click away and the refusal — "Allow negative components, change the order, or
change the response" — named the right knob and left them to find the setting by trying five. That is
the failure mode this repository already has a standing lesson about (the MoM ceiling refusal that
named three inert knobs): **a remedy is only a remedy if it BINDS.** `MatchDesignerViewModel.FindWaysOut`
now re-runs the solution search at each permitted order and each feasible response and names the ones
that work, by number. It costs one search per permitted order — the picker offers two or three, never a
range — it runs ONLY on a design that has already refused, and it runs on the analysis worker under the
same cancellation, so a design with solutions pays nothing.

## Match Designer round 7, part 2 — four more, and two were the same shape (2026-08-20)

**8. "Changing the Filter Response leaves the Termination unsatisfied until I tweak a slider — even
sliding it past the value it was before."** Every response family asks for a DIFFERENT Π N².
`RelinkAfterSpecChange` — which re-solves the rack against the ratio the specification now requires —
had exactly ONE caller, `SetTermination`, because that is the edit the owner reported first. Every
other specification edit wrote `Refresh(specChanged: true); Commit();` and left the rack where it
was, so `SetTransformN`'s own linkage quietly did the work the spec edit should have done the next
time a slider moved. Measured on 50 Ω into 5 Ω ∥ 1 pF at order 4 with one applied transform: Fano
needs Π N² = 10.134, two-ended 13.554, so switching the family alone left the far termination
presenting **37.4 Ω against a declared 50 Ω** — and the ORDER (order 6 → 10.054) and the BAND
(f2 → 2.6 GHz → 10.875) do the same. Fixed with one shared entry point, `CommitSpecChange`, that every
spec setter now uses including `SetTermination`; a source test holds them together so the next setter
added cannot reintroduce it. `RelinkAfterSpecChange` also short-circuits on `Status.OnTarget` now,
which is what makes it safe to call from everywhere — otherwise a spec edit that does not move the
target would put a no-op transform write on the schematic's undo stack.

**9. "The Filter Response card has more text than height."** `RippleNote` was three sentences and
therefore five wrapped lines in a ~300 px column, present whenever either termination carries a
reactance — most designs. It is one short line now ("Termination 2's reactance sets this.") and §6.6's
paragraph moved to the row's own tooltip. **Deleting it outright was the owner's other option and
would have put the round-6 bug straight back**: a disabled `InlineEditText` rests as a bare
`TextBlock`, Avalonia does not dim one, and the row then reads as live and swallows the double-click.
The closed Response combo also gained a tooltip of its own (the items always had theirs; the control
the user is left looking at afterwards had none).

**10. The window title names the schematic.** `SchematicViewModel.DocumentName`, written in
`RegisterSession` — the single choke point every path-backed session goes through, Save As included —
and in the two scratch paths, which have a title and no file. `EditModel.SchematicDirectory` is the
FOLDER and cannot answer this: a cell's schematic file is not obliged to be named after its cell. One
string (`MatchDesignerViewModel.Title`) feeds the OS window title, the pane's own title bar and the
Window menu entry.

**11. "I probe Terminal 2, the parasitic updates to 1000 pH, and the schematic keeps rendering
2000 pH." — not stale rendering, and the tell is that the ladder was INSENSITIVE to the termination
entirely** (1 nH and 2 nH produced element-for-element identical networks). `QAdjust` was 2,
legitimately set while that end's Q was lower; the probe made termination 2 a 1 nH parallel L whose
own Q at band centre is 3.999. `MatchSynthesis` takes `qAna = QAdjust > 0 ? QAdjust : qActual` with
**no check that the adjustment is above the end's own Q**, so the end arm was built for Q = 2 — a
1999 pH shunt inductor — and `WithEndSplits` skips its split whenever `qSynth <= qActual`, so that
element kept the SYNTHESIS's value while still carrying `AbsorbedEnd = 2`. The drawing then labelled
1999 pH "supplied by the external termination" when the termination supplies 1000 pH, and the status
strip called it matched at −37 dB from a network the circuit does not contain.

The far end has had exactly this refusal all along (`FarEndNotAbsorbable`, "the synthesis reaches
Q_far = X against the termination's own Y"); the analysis end simply never got its counterpart.
`AnalysisEndNotAbsorbable` is that counterpart, checked before any prototype work since it depends on
nothing the prototype produces. **A parasitic cannot be subtracted** — §4.6's Q-adjust *inflates* an
end's Q — so this is a refusal, not a clamp. On top of it, the UI clears a stored Q-adjust that the
terminations have overtaken and says so in one line (`AdjustQAdjustForAnalysisEnd`, the treatment
`AdjustOrderForParity` already gives an automatic order change), because otherwise the probe — a
button whose whole job is to make the specification correct — hands back a refusal. It is CLEARED
rather than clamped: zero is always legal and is what the design would have carried had the user
probed first, while clamping to the new Q invents a number nobody chose. Only the two paths that can
invalidate it *without being about it* call it; a Q-adjust typed low by hand still gets the refusal,
which names the number to use.

**12. The design is a nested OBJECT in the `.csch` now**, not a string. Part 1 made the payload plain
JSON, which left it inside a JSON *string* — every quote escaped, the whole design one long line.
`CschParameter.Value` (a `JsonNode`) writes it as an ordinary nested object under `WriteIndented`: one
field per line, no escapes, diffable. **The rule is general, not a Match special case** — any
expression beginning with `{`, which is exactly the set that cannot be an expression, since
circuitRF's expression language has no brace token. Same discriminator `MatchEmbedding.TryDecode` and
`CnlWriter.FormatParam` already branch on, which is what keeps the three from disagreeing about what a
payload is. `Expression` is nullable and omitted when `Value` carries the parameter, and a value that
merely *starts* with a brace but is not valid JSON survives verbatim rather than being lost to an
exception on save.

## Match Designer round 7, part 3 — a short is an answer, and an echo can now go stale (2026-08-20)

**13. "With shunt L = 0, Termination 2 does not probe correctly (can't find an impedance)."** An ideal
0 H shunt inductor IS a short to ground, so the pin genuinely presents 0 Ω and applying nothing was
right. **The message was wrong, and pointed at the wrong thing entirely.** With no guard the
measurement fell through to the fitter, where all five models fitted it PERFECTLY — residual exactly
0 — with R = 0 or NaN, and each was then rejected for a non-positive R. The user was told "none of the
five models fits this network with a positive resistance… a negative element is not a termination
anyone can build": the fitter blamed, negative elements invoked, and the actual answer (the pin is
shorted) never said.

`TerminationProbe` has always caught the OPEN case before the fit and said so plainly
(`|1 − Γ| < 1e-14`). The SHORT — `|1 + Γ| < 1e-14` — simply had no counterpart. It does now, and it
names the net to go and look at rather than the fitter. **This is the second missing-mirror-image
defect in one round** (the other being `AnalysisEndNotAbsorbable` against the long-standing
`FarEndNotAbsorbable`): when a guard is written for one degenerate end of something, check for the
other end in the same sitting. `MatchProbeFitRowViewModel.PhysicalNote` also says "zero or negative"
now, because zero is what a shorted pin produces and calling it negative sends the reader looking for
a sign error that is not there.

**14. Why a Match carries F1/F2/Order twice, and what the readable payload changed about it.** The six
parameters beside `Design` are ECHOES: they are drawn beside the symbol (F1, F2, Order have
`ShowOnSchematic`), they make the `.cnl` instance line legible — where the payload is still a base64
token — and **nothing reads them back** (`MatchComponentTests.TheEchoParameters_AreNeverReadBack`
pins it; match.md §7.2 makes the design authoritative). An instance has to CARRY a value to be able
to show one, which is why they are duplicated rather than derived at render time. So the duplication
is deliberate and stays.

What changed is the failure mode. While the payload was base64 nobody could hand-edit it, so an echo
could only ever fall behind through a bug. **The payload is now readable JSON in the `.csch` — the
whole point of items 3 and 12 — which invites a hand edit, and a hand-edited band leaves the echoes
behind with the schematic still drawing the old one.** `CheckEchoParameters` states it on load and
`Commit` clears it, because the next edit genuinely does refresh them. It deliberately does NOT
rewrite them on load: that would put an edit on the schematic's undo stack that nobody made and mark
the document dirty the moment it is opened — which is the very confusion item 1 of this round was
about.

## Match Designer round 6: a hold that never started, and a Designer with nothing behind it (2026-08-20)

The owner's sixth round. Two items were real defects with non-obvious causes; the rest was layout and
one new entry point.

**1. The plots "glitching" during a slider drag: the hold existed and was never STARTED.** The rack's
sliders were wired with XAML attributes — `PointerPressed="OnSliderPressed"` /
`PointerReleased="OnSliderReleased"` — which subscribe to the **bubbling** route with
`handledEventsToo: false`. Avalonia's `Thumb` marks both the press and the release handled before
either reaches the `Slider`, so **grabbing the thumb, which is the ordinary way to drag a slider,
never called `BeginTransformDrag` and never called `EndTransformDrag`.** Every intermediate value
therefore ran the full held-back path: a 401-point S-parameter sweep, both plots rebuilt, and
`Autoscale(force: true)` on each — which is exactly the axis movement the owner was describing.
Clicking the slider's *track* did work, which is why it survived review: the gesture is correct for
the one interaction nobody uses. The handlers are registered from `Loaded` now, **tunnelling** (so
they run before the Thumb can mark anything handled) and `handledEventsToo: true` as well;
`PointerCaptureLost` is wired too, or a drag interrupted by a focus change leaves the plots held for
the rest of the session. The table of wired sliders is a `ConditionalWeakTable` because an
`ItemsControl` builds a new Slider per row per rebuild and `Loaded` fires again on every re-attach.

**2. "Is Ripple, dB supposed to be an input?" — yes, and it was disabled with nothing to show for
it.** The row is `IsEnabled="{Binding RippleEnabled}"`, false whenever either termination carries a
reactance (match.md §6.6: the prototype is then the singly- or doubly-prescribed one and the ripple
follows from the terminations). The trap is that **an `InlineEditText` at rest is a bare `TextBlock`,
and Avalonia's Fluent theme dims neither a disabled `TextBlock` nor a disabled `Panel`** — so a
switched-off field is pixel-identical to a live one and simply swallows the double-click. Fixed in
two places rather than by making the field always editable: a window-scope
`ctl|InlineEditText:disabled` style (plus one for `TextBlock.detailLabel`), which now covers every
settable value in the window, and a `RippleNote` line that names *which* termination is the reason.
`:disabled` follows `IsEffectivelyEnabled`, so disabling the row's **Grid** dims its children too.

**3. `IsStandalone` is NOT `IsOrphaned`, and conflating them would have been the bug.** Tools ▸ Match
Designer opens a Designer with no component and no schematic. `IsOrphaned` is the *other* window with
no component — the one whose component was **deleted** — and it is deliberately frozen read-only,
because writing to a component that is no longer in the drawing is the thing that must not happen. A
standalone Designer has nothing to write to in the first place, so every setter runs and `Commit` is
simply the no-op it already was for a null target. Three capabilities are shut off where each is
decided rather than at every call site: undo/redo (no schematic stack — `Revert` still restores the
opening design), Probe (`MatchProbeAvailability` already answers `NoSchematic` from a null model), and
replace-in-place on flatten. Flatten itself goes through `MatchFlatten.Write` **directly** rather than
through `FlattenMatchCommand`, whose whole job is the half this case does not have; the cell is
identical because the write is the same primitive, and the destination is unconfined because a cell no
schematic references has nothing to be relative to.

**4. A `ColumnDefinition` is not in the logical tree, so a `{Binding}` on its `Width` silently
resolves to nothing.** The two new pane expanders (network over response, response over network) have
to move a column width — hiding a pane alone leaves its 380 px column standing and the window shows a
hole. The width is therefore set from code-behind (`SyncPaneLayout`), reacting to the view-model's own
property change, with `IsVisible` bound in the AXAML as the other half. The two toggles are mutually
exclusive in the view-model rather than in the two bindings, because each takes the *other* pane's
column and both on at once is a state with no width left to describe.

**5. `Button.seg-btn` paints the icon selector's face transparent from APPLICATION scope, and a host
cannot reliably out-style it.** The owner wanted the C/L/– selector to read as a button rather than as
text in a box. Style-vs-style precedence across scopes is not a thing to bet a look on, so
`SegmentedSelect.axaml`'s control theme now passes the `IconSelectButton`'s own `Background` down to
`PART_Button` as a **template value** — a local value, which outranks any style — defaulting to
`Transparent`, so every existing selector in the application is pixel-identical and only a host that
sets `Background` sees a change.

**6. "Not aligned" was a column-width mismatch, not a margin.** The termination card's R row sized its
label column `Auto` to a ~7 px glyph while the reactance row beneath it sized to a 24 px button, so the
two started 10 px apart and no amount of margin tweaking would have held. Both rows share one fixed
24 px column now, with the "R" centred in it.

Also in this round, without incident: the Topology label and the Conjugate checkbox removed from the
termination card (the design's `Term1Conjugate`/`Term2Conjugate` stay, so an older design still
decodes); the probe's four-candidate listing removed — it was already applying the best fit, and doing
so is one `SetParametersCommand`, so a single undo puts the terminations back; the N field turned into
an inline editor that **refuses** out-of-range input with the range named rather than clamping into it
silently; a Zoom to Fit button overlaid on the network pane (the schematic editor's own glyph, padding
and tooltip, held together by a source-scan test) with the F key handled as an `OnKeyDown` override
rather than a `KeyBindings` entry — a `TextBox` does not mark `KeyDown` handled for an ordinary
character, so a bare-letter window binding would re-frame the drawing every time the user typed an "f";
and the double-click re-frame removed, which also stops a double-click that misses a value label by a
pixel from re-framing the whole drawing under the cursor.

**The Plot Properties item was disabled and did not LOOK disabled.** Avalonia's Fluent `MenuItem`
`:disabled` greys the header text and leaves the `Icon` at full colour, which on macOS reads as a live
row that ignores the click. `PlotControl` now dims the item as well as disabling it, through one
`ApplyMenuAvailability` called from the menu **builder** as well as from `Opening` — so the first open
does not depend on that event having fired.

### Round 6 follow-ups — four more, and two of them were the same class of "the control is lying"

**7. The empty tooltip is a rectangle.** Owner: *"there's some kind of transparent rectangle rendering
over the sliders when I move my mouse over them (it has a black stroke outline)."* The transform
slider bound `ToolTip.Tip` straight to `DisabledReason`, which is `""` for every ordinary enabled row
— and **Avalonia's tooltip service opens on any tip that is not `null`, an empty string included**, so
hovering a perfectly normal slider popped the tooltip panel's own bordered box with nothing in it.
This applies to every `ToolTip.Tip="{Binding …}"` in the application whose source can be blank; the
Response family's own `Tooltip` was the second instance (blank until the first background analysis
landed) and is seeded now. Fixed by having something to say, not by binding null — a slider whose
range is recomputed on every rebuild is exactly the control whose bounds are worth stating.

**8. The slider was bounded by the wrong range.** Owner: *"sometimes I can move the slider control
higher, but the transform is already at its maximum level."* `TransformRange` is the **positivity**
bound — how far a transform can go before one of its own three products goes negative — and it is the
right bound for `MatchLinkage.Redistribute` to clamp against. It is **not** how far the user can drag.
With link on, `Redistribute` ends by recomputing the dragged transform from what the *others* settled
at (`n[current] = Clamp(sqrt(required / rest))`), so once the other unlocked transforms are parked on
their own bounds `rest` stops moving and the dragged N stops with it, while the thumb keeps
travelling. With every other transform locked, N is a constant and the slider moved its whole length
for nothing. The reachable end points are now **measured, not derived**: ask `Redistribute` — the very
function that will run — what it settles on for a request at each end of the positivity range. The
settled value is monotone non-decreasing in the request, so those two probes are the interval's ends,
and a second analytic implementation of the same rule would only be a second thing to disagree with
it. A row whose reachable interval has collapsed is disabled with the reason, distinguishing the two
ways it happens (link holding it, versus a pair with no positivity range at all) because the remedies
differ. **This also invalidated an existing assertion**: `LinkWithOneTransform_DisablesTheSliderAndThe
NumericBox` claimed a second transform always makes the first movable again — on its own fixture
`AvailablePairs().First()` picks a pair whose range is the single point `[1, 1]`, which gives nothing
back, and the test had been passing because `CanEditN` only counted rows.

**9. Apply could never light, and never did what its name said.** Owner: *"the Apply button is always
disabled, even after I make changes. What does apply do?"* It never sent the design anywhere — every
edit in the window already writes the component's `Design` parameter as it is made, as one
`SetParametersCommand` on the schematic's undo stack. What it flushed was a number half-typed in a box
that had not lost focus; once the last such box became an `InlineEditText` (2026-08-19/20) that state
stopped existing, because the control commits on Return *and* on LostFocus and every `*Entry` setter
parses and writes in the same breath. `HasPendingEdits` was therefore structurally false. The button
and its plumbing are gone rather than left on the footer as a control the user is entitled to think
does something.

**10. Order is a selector now**, whose items are `OrderOptions` — only the parities the termination
pair permits (match.md §4.2). The refusal the typed field had to spell out is made by not offering the
order. The item list is a **stable `ObservableCollection` whose contents change**, rewritten only when
the permitted set actually differs: this repository has already been bitten by a selector losing its
selection when its list was replaced underneath it (see `src/Ui/CLAUDE.md` on ComboBox notification
order). The setter still refuses anything outside the list, because a control that is correct only
because its UI never offers the wrong value is one restyling away from not being correct.

Also: the `probed <date>` line is gone from the termination card (it appeared only after a probe, so
the card changed shape under the user); the provenance itself is untouched on the design and is still
what §10.5's "a hand edit clears the badge" rule is written against. And the typed-N tolerance is
sized to **the display quantum of the format the field prints with** (`"0.#####"`, so 5e-6) rather
than a token 1e-9 — below that, typing back the bound the field had just shown was refused.

### Round 6, last three — and one of them is why the whole "empty tooltip" story ends in a deletion

**11. The slider tooltip is gone outright.** Owner: *"completely remove the slider tooltips… I don't
want to see anything rendered."* Note what this does NOT mean: binding the tip to `null` or to `""`.
An empty tip **is** a rendered box (finding 7 above); the only way nothing renders is for the control
to carry no `ToolTip.Tip` at all. Nothing is lost — the N field immediately to the slider's left
already states the accepted range when the row is live, and the disabled reason when it is not.
**An empty string is not "no tooltip"** is worth carrying out of this round as a general rule for this
codebase.

**12. The inline editor did not close on a click away, because almost nothing here is focusable.**
The box dismisses itself on `LostFocus`, which never came: the Match Designer's schematic canvas is a
plain `Control`, and so are the pane backgrounds, the `TextBlock`s and the borders — a click on any of
them moves focus nowhere, so the box was never told. **The schematic PAGE does not have this problem
because its canvas takes focus for its own keyboard tools**, which is exactly why the same wiring,
copied from there, worked there and not here. It is now committed from the window's own tunnelling
`PointerPressed` handler, checked *before* that handler's left-button and modifier guards: a
right-click or a Ctrl-click away from the box is just as much "away" as a plain one.

**13. Clicking Probe made the specification pane "glitch out and return to normal".** The probe's
progress row lived inside that pane's stack and was visible only for the duration of the run, so every
card under it slid down when the probe started and back up when it finished. It reports from the
footer now — a fixed `Auto` row whose content is always present, so two lines and a Cancel button cost
no layout at all. This is the same shape as finding 2's dimming and the `InlineEditText` measure
override: **a control that appears and disappears inside a stack is a layout shift, and in this window
they are all treated as bugs.**

### The probe refused the simplest termination there is

Owner-reported, 2026-08-20, on a schematic whose port 2 is a bare 50 Ω `Term`: *"the Match designer
probe cannot seem to find my simple 50 ohm port 2 termination… I get a 'None of the four two-element
models…' error. I would have thought finding the 50 ohm solution would have been super easy."*

**Every one of the four models fitted it perfectly and every one was then thrown away.** The probe's
four candidates are series R+C, series R+L, parallel R‖C and parallel R‖L, each a linear least squares
in its natural domain. Against a flat 50 + j0 all four return **R = 50 Ω with a residual around
1e-16** — and the reactance, which the fit has no information about, lands at its degenerate end:
C = ∞, L = 0, C = 0, L = ∞. `ProbeFit.Physical` demanded a finite, strictly positive reactance, so all
four were non-physical, `best` was null, and the refusal reported a fit problem where the fit was
exact. The message even named the number it had found.

`ReactanceKind.None` is first class everywhere else in this feature — it is what `Termination.Resistive`
makes, it is what the kind selector shows as "–", and `Termination.CeqAt`/`QAt` both answer 0 for it —
so the fitter simply had no way to say the thing the rest of the code was built to represent. There is
a **fifth model, R alone**, scored by the same Γ residual as the other four; it wins a flat impedance
outright and loses to a real reactance, so nothing is special-cased. `Physical` now reads "R > 0, and
the reactive value > 0 **for the models that have one**".

**The gap had a test-shaped cause too.** The probe fixture has had 200 Ω ‖ 0.125 pF on pin 1 and a bare
50 Ω on pin 2 since MN-4 landed, and every test in the file probed **pin 1**. The one case that was
already sitting in the fixture was the one nobody asked for.

### Round 6, the last four — two labels, one selector that could not matter, and a refusal to a question nobody asked

**13c. …and then the rack went dead, which is the more important half.** Owner: *"I can't slide any
transforms anymore — they are all disabled, even when they are unlocked."* Finding 8 gated
`CanEditN` on the reachable interval being non-degenerate, and **a collapsed interval does not mean
"this N cannot move"** — it means "the linkage cannot improve `Π N²` from where the rack stands".
Those are different statements, and the state that produces the second is an UNREACHABLE ratio:
5 Ω into 200 Ω needs a product of 54 while every transform's positivity range caps at 1, so every
probe clamps to the same bound at BOTH ends and every row reads as pinned. Disabling on that reading
took the entire rack away in exactly the design the user was trying to rescue — and the message it
offered ("every other transform is locked or already on a bound") was half wrong, since nothing was
locked. **Nothing is ever disabled for lack of computed travel now.** The narrowing applies only when
it describes a real interval; otherwise the slider keeps its positivity range and behaves exactly as
it did before any of this, so the change is strictly an improvement where it applies and a no-op
where it does not. Locking is once again the only thing that switches a row off.

**13b. Locking a slider moved its thumb — a regression from finding 8, and the shape is worth keeping.**
Owner: *"when I lock a slider it is disabled (good), but the position of the slider circle changes
between locked and unlocked state (bad)."* The reachable-range pass fell back to the POSITIVITY range
for a locked row, so its bounds widened the instant it was locked — and **a thumb draws at a fraction
of its range**, so N stood perfectly still while the circle jumped. A lock decides whether a value may
be MOVED and says nothing about where that value could live; `MatchLinkage.Redistribute` agrees, since
it ignores the driven slot's own lock. Dropping `slots[i].Locked` from the early-out makes the locked
and unlocked bounds identical **by construction** rather than by coincidence. (Locking a row still
narrows its NEIGHBOURS' reachable ranges — that one is truthful: a locked neighbour genuinely cannot
absorb any more.)

**14. The axis labels were never the Match Designer's to get wrong — the trace KIND was.** Owner:
*"the left y / right y axis labels need to use the same transform language as the data display —
instead of `dB(S(1,1))` it should say `S(1,1) dB20`. Don't hard code it in; have the plot render it
just the way it's done in the data display so that they won't ever drift."* The Designer's plots
already go through `TraceLabeler.ComputeMinimalLabels` and `AxesRenderer` like every other plot in the
application. What differed is that a trace built from an `SNP` is **network-bound** and read
`Trace.ShortDescription` — the function-call form — while a trace built from a simulation cube read
`BuildCubeQuantity`, the name-then-transform form. **Two forms for one quantity, chosen by which path
produced the data.** Both branches now end with one `TransformSuffix` table, and the network branch
maps `DependentVarFormat` → `CubeTransform` (`Db` is **dB20**, because every network path in `Trace`
computes 20·log10 for it). This moves the Data Display's own Touchstone traces onto the same language,
which is the point rather than a side effect. `Trace.ShortDescription` is deliberately unchanged:
`BuildPickerYExpression` reads it as an EXPRESSION fallback, where a suffix would not parse.

**15. "The series/parallel graphic doesn't update" — it does, when there is something to draw.** With
`ReactanceKind.None` the pictogram is a resistor on its own, and both topologies are the same picture.
So is everything else: `Termination.CeqAt` and `QAt` both answer 0 for a reactance-free end, and
`MatchOrders.ValidOrders` **short-circuits on `!HasReactance` before it ever compares the two
topologies**. On a bare R the choice changes the pictogram, the synthesis, the order parity and the
ladder exactly not at all. The fix is not to the drawing: the selector is disabled, with a tooltip
saying why. A control that can be moved to no effect is the bug.

**16. Committing an unedited value was refused, correctly, about a question nobody asked.** Owner:
*"if I double click on a component value in the LC ladder and then close the inline text editor
without changing anything, I get 'L1_N1 cannot reach x pH; no transform in this rack moves it…'."*
The editor seeds itself from the LABEL, and the label is **rounded to the display's significant
digits** — `L1` reads "725 pH" for an element that is 724.93 pH. Committing that text unchanged asked
`MatchElementSolve` to reach 725 pH *exactly*, which it truthfully cannot, so the refusal was a right
answer to a question the user never asked. `CommitInlineEdit` now returns early when the typed value
**formats to the same string the field was showing** — at display precision, which is the only
precision the field has. Comparing the stored doubles instead would answer "different" for every
unedited commit, which is the bug restated.

### "I can't match terminal 2" on a matched network — three findings from one report

Owner-reported, 2026-08-20, on `matchedRFTest.csch`: *"I'm getting a warning that I can't match
terminal 2, but it looks matched to 50 ohms to me."* Decoding the stored design and running the
rebuild against it answered all three parts.

**17. "On target" was a floating-point equality test wearing an engineering label.** The design was
off by **2e-9** — two parts in a billion — against a tolerance of `1e-9`, so the status strip read
`Π N² 10 / 10 ✘ not reached` and `RefreshStatus` flagged termination 2 (the ⚑ is driven by
`!Status.OnTarget`, and the far end is the flagged one by construction). `MatchLinkage.Redistribute`
reaches its target by a sequential pass of divides and clamps, and `Π N²` **squares** every term, so
a few units in the last place of a ratio near 10 land exactly there — a re-link after a dropped
transform lands there routinely. The tolerance is now one shared constant,
`MatchLinkage.RatioTolerance = 1e-6`, read by `LinkResult.OnTarget`, `MatchRebuild.OnTarget`,
`Redistribute`'s own convergence break and `MatchSolutionSearch.RatioTolerance` — the last of which
matters on its own: a solution the list offers must not be reported as "not reached" the moment it is
applied. A part per million of an impedance ratio is 4e-6 dB.

**18. The strip printed a cross beside two numbers that looked equal.** `Π N² 10 / 10 ✘ not reached`
is unreadable whatever the tolerance says. Three decimals is the right density for the ✔ case and
hides the entire disagreement in the ✘ one, so `RatioText` now states the shortfall as a percentage
when there is one — `Π N² 1 / 54.215 ✘ not reached (-98.1555%)`.

**19. An infeasible response family refuted itself.** Butterworth and Bessel were listed with
*"cannot absorb termination 2 at order 3 — its far-end Q reaches only **0** against the **0**
needed."* `PrototypeSearchResult.MaxQFar` is 0 in **two** different situations — the family's best
really was 0, and *nothing was evaluated at all* (the `-∞` sentinel is normalised to 0 on the way
out) — and against a purely resistive far end the required Q is 0 as well, so the two zeros met and
the sentence said nothing. The real reason here is the second: with an analysis-end Q of 0.0625 the
one-parameter search finds no member with a positive g-vector at order 3. `AnyMember` now tells the
two apart and each gets its own sentence.

## Match Designer round 5: editing a value that nothing owns, and four host-shaped omissions (2026-08-20)

The owner's fifth round on the Match Designer. Most of it was layout; four items were the same class
of bug, and one was a design problem with no obvious right answer.

**1. An element value in a match network is a TARGET, not a setting — there is nothing to write it
to.** The owner asked to "use inline text editor to change any value in the schematic… all other
components are updated accordingly (using the available transforms N1, N2, N3 etc.) to maintain the
frequency response". Every value in a synthesised ladder is fixed by the specification; the only
degree of freedom is where each Norton transform sits (match.md §4.7). So a typed value runs
`MatchElementSolve` — a sweep-plus-ternary search over one transform's range, with every candidate
put through `MatchLinkage.Redistribute(link: true)` so `Π N²` stays on target. **Link is forced on
regardless of the design's own Link setting**: without it the far termination is rescaled and the
network is matched to a resistance the user does not have, which is precisely the "maintain the
frequency response" the ask is about.

**2. The search must be seeded with the rack AS IT STANDS, or an unreachable value rearranges every
slider for nothing.** In a ladder whose transforms all act at one end, the elements at the other end
are genuinely unreachable — every N gives the same value. The first version scored every sample
identically, kept whichever it saw first, and applied it: the element did not move, the response did
not move, and three sliders had jumped. Seeding `best` with the current setting and requiring a
candidate to beat it by a real margin makes "it cannot be done" mean "nothing happens".

**3. "Nothing may move" and "nothing CAN reach it" are different answers and must not share a
message.** Both surface as "the search returned the rack unchanged". Eligibility (any transform
unlocked, with a usable range?) is therefore counted in `MatchDesignerViewModel.SetElementValue`
BEFORE the search runs, so an all-locked rack says "unlock one first" rather than "this value is
unreachable" — which would send the user looking at the wrong thing entirely.

**4. Four owner-reported bugs, one shape: a raw `PlotControl` is not a Data Display, and the host has
to do the host's half.** Round 4 fixed four of these by giving the Designer a real
`DataDisplayViewModel`; two more were left. `PlotControl.HandleDoubleTapAt` is documented as "called
by the host on DoubleTapped" — `PlotContainerView` calls it and the Designer did not, so
double-clicking a trace added no marker. `Delete` is a `KeyBinding` on `DataDisplayView` and this
window had none, so a selected marker could not be deleted. **Neither fails loudly; both are simply a
gesture that does nothing.** When hosting a Data Display control outside the Data Display, walk
`PlotContainerView`/`DataDisplayView` and check what each one does that the new host does not.

The Designer's Delete deliberately binds `DeleteSelectedMarkersCommand`, **not** the Data Display's
own `DeleteSelected`: that method also removes selected plot CONTAINERS, and these two plots are
declared undeletable in the same AXAML (`CanDeletePlot="False"`).

**5. `SuggestedFileName` and `DefaultExtension` are not independent.** "Export Touchstone file picker
shows .s2p twice in its suggested file name" — Avalonia's storage provider appends
`DefaultExtension` to a `SuggestedFileName` that has none, so supplying both spelled it twice
(`MN1.s2p.s2p`). Drop the extension from the NAME, never the `DefaultExtension`: that is what gives a
name the user types without one an extension.

**6. A non-modal editor window has to survive its subject being deleted.** Owner: "what happens if
user goes back to schematic and deletes its instance? Perhaps the window becomes orphaned? I am ok
with that. Just need to handle it gracefully." The window stays open and readable and every write is
shut off at ONE choke point — `MatchDesignerViewModel.Commit`, which every setter in the class already
funnels through. Guarding the two dozen setters instead would have been two dozen chances to miss
one. It is reversible: an undo of the deletion puts the component back, `CheckOrphaned` sees it on the
next model change, and the Designer is live again.

**6b. Non-modal is not the same claim as "can go behind".** Follow-up report on the same day: "the
Match Designer window is always in front. I can't get back to the workspace with the designer window
open." The window had been non-modal since it was built — `Show(owner)`, never `ShowDialog`. But
`Show(owner)` makes an **owned** window, and every platform pins an owned window above its owner in
the z-order for as long as it exists; clicking the workspace raises the workspace *underneath* it.
For a prompt that belongs to one window (the Flatten-to-Cell name dialog) that is exactly right. For
a window the user works alongside their schematic it is indistinguishable from modal.

`MatchDesignerWindow.ShowUnowned` therefore shows it as an independent top-level and takes on the two
things the owner had been doing for free: **placement** (`WindowStartupLocation.CenterOwner` requires
an `Owner`, so the position is computed from the owner's frame — before `Show`, off the declared size,
because an unshown window reports no frame and repositioning afterwards is a visible jump — and
clamped to the owner's screen) and **lifetime** (an owned window closes with its owner; this one is
wired to). Dropping the owner without either would trade the bug for a window that opens in a corner
and outlives the session.

**The placement is a CASCADE off the workspace's top-left, not a centring** — corrected the same day
("needs to open slightly down and to the right of the parent window that opened it"). A centred
Designer covers exactly the part of the workspace a user reaches for to get back to it, which
re-creates most of the feeling of the bug the unowning fixed. The 36-unit offset is scaled by
`RenderScaling` or it is a different physical distance on every display.

**And then the cascade was computed correctly and thrown away by its own safety clamp** — reported as
"window placement on open is still in top left corner of my screen", which looked exactly like the fix
never landing. **A window-placement clamp must not contain the new window's SIZE.** The obvious guard
is "keep the whole window inside the working area", i.e. clamp `x` to `area.X + area.Width − width` —
but that needs the width in the units `Window.Position` uses, and those are not knowable from managed
code. Converting a DIP width with `RenderScaling` assumes `Screen.WorkingArea` is in physical pixels;
**on macOS it is in points, the same space as the DIP width**, so a 1360-DIP window measured 2720
against a 1728-wide area, the upper bound went negative, `Math.Max` floored it at `area.X`, and the
window was pinned to exactly (0, 0).

The guard is now expressed without the size at all — keep `MinOnScreen` (240) of the window's leading
corner inside the working area. Unit-agnostic, it is what the guard was actually for (a window you can
still grab), and cascading a small offset off a window that is itself on screen means it essentially
never fires. The arithmetic moved to `Views/Match/MatchWindowPlacement`, pure and asserted by test with
a working area SMALLER than the window — placement is otherwise only checkable by opening the
application and looking, which is how this reached the owner twice.

The position is also assigned **before `Show()` and again after**: before so a window positioned only
once it is on screen is not a visible jump, after because whether a platform honours a `Move` on a
window it has not shown yet is the platform's business, and the second assignment is a no-op when the
first one took.

**6c. The label hit bands OVERLAP, and first-match order gave the overlap to the wrong row.**
Reported as "cannot double click on TermG value to get the inline text editor", and it was not about
`TermG` — every value label had it. `SchematicComponent.LabelRowGeometry`'s band runs 76 world units
above the baseline and 25.6 below (101.6 tall) on a row PITCH of 72, so consecutive rows share 29.6
units. A hit-test that returns the first containing row hands all of that to the row above; because a
label's glyphs sit above their own baseline, **the top 34% of the value text resolved to the
instance-name row** — which is not editable, so the gesture did nothing and said nothing, about a
third of the time. `SchematicHitTest.TestComponentLabels` has the same shape and the same latent
issue; it is masked on a page, where a row is tens of pixels tall.

Rows are now resolved by **distance to the row's own GLYPHS**, not by the band. Text intervals are
`[baseline − capHeight, baseline]` — 70 units on a 72 pitch — so they abut and never overlap, which
makes "is the point on this row's text?" a question with exactly one answer; only a point on no row's
text falls back to the nearest. Anything derived from the bands (first match, nearest band centre)
has to split the shared strip somewhere arbitrary and takes a slice of a real row's text with it.

The second half of the same report: **a world-unit hit band is the wrong size in a pane that frames a
whole network.** `MatchSchematicCanvas` therefore passes a tolerance of `PickPixels / _zoom`, so the
pick area has a floor in screen pixels and shrinks to nothing as the user zooms in. The geometry moved
to `MatchSchematicLabels` — pure, and asserted by calling it, because "the double-click does nothing"
is a report with no way to tell a wrong band from a pointer that never arrived.

**Neither was enough, and the third report is the one worth keeping: the target itself was one label
row.** "I still cannot use inline text editor on the TermG Z value." The two fixes above were real
defects and both were on the way to the wrong place — *which* row wins, and *how much slack* it gets,
are irrelevant if the row is five pixels tall.

**The measurement that ended it came out of the pane's own committed figure.** `match-designer.svg` is
a vector capture of a real render, so its text elements carry the actual on-screen positions: the three
`TermG` rows sit at y 204.3 / 209.15 / 214, a pitch of **4.85 px**, and `font-size="4.72"`. That fixes
the pane's zoom at 0.0674 and every other number with it — the value text is 4.7 px tall, the label
block 16.7, the `TermG` glyph 31.7 x 7.7. **A committed vector figure is a measurement of the running
interface, and it is available without building or clicking anything** — worth remembering the next
time a "the click does nothing" report needs a real number.

**And none of that was it either.** Third report: "still not fixed", plus — independently confirming
it — "the inline text edit also does not work in the regular schematic now."

**A subclass of a templated Avalonia control renders NOTHING unless it declares the style key it is
themed by.** Avalonia resolves a templated control's implicit `ControlTheme` by the control's OWN type
and does not fall back to a base type. Fluent keys its theme on `typeof(TextBox)`, so
`SchematicInlineEditBox` found no theme, got no `Template`, built no visual children and measured
**zero high** — while remaining focusable, holding its text, and reporting `IsVisible = true`. Nothing
throws and nothing warns. The gesture, the hit-test, the resolve and the open were all working the
whole time; the box was simply invisible. One line fixes it:

```csharp
protected override Type StyleKeyOverride => typeof(TextBox);
```

**`UserControl` and `Window` subclasses are not at risk**, which is why this application is full of
`: UserControl` views that render fine: Fluent themes those through a `Style` whose selector matches
by "is", subclasses included, while every templated PRIMITIVE is themed by a `ControlTheme` keyed on
an exact style key. `MatchRound5Tests.EverySubclassedTemplatedControl_IsThemed_OrItDrawsNothing`
asserts the rule over the whole assembly with that exclusion, because the same one line took out the
schematic PAGE's inline editor at the same time — the moment its AXAML started declaring this type
instead of a bare `TextBox`, label editing broke on every schematic in the application, silently.

**How it was finally found, which is the part worth keeping.** Three fixes had been reasoned from the
source and all three were wrong about the cause. What settled it was building the real window under
`tools/DocGen`'s headless host in a throwaway project, laying it out, and asking the live control
directly: `AnchorFor` gave a screen point, `HitLabel` at that point returned the TermG's value row,
`InputHitTest` returned the canvas, a raised two-click press produced `LabelDoubleTapped`, and the
editor reported `IsVisible=true, Text='50 Ω'` — and then `CaptureRenderedFrame` showed the label with
**nothing over it**. A plain `TextBox` in the same panel reported `template=set, visualChildren=1`
against the subclass's `template=NULL, visualChildren=0`. **`tools/DocGen`'s `HeadlessHost` is a
general-purpose way to drive and photograph the real UI without a display**, and it is the right first
move for any "the click does nothing" report — not the fourth.

So, alongside that, the target is now the whole COMPONENT. `MatchDesignerViewModel.ResolveInlineEdit` no longer takes a
row at all: only the value row is editable — the type is what the synthesis produced and the name is
the key every stored transform resolves through — so the other two rows had no meaning of their own to
compete with, and were two thirds of the block sitting on top of the third that does something.
`MatchSchematicLabels.HitGlyph` adds the symbol itself, which is the schematic page's own convention
(`SchematicView.OnComponentDoubleTapped` opens the parameter editor for the component that was hit) and
is the part a user actually aims at. A shunt arm's own `Ground` has no labels and still resolves to
nothing, so empty space and grounds still re-frame. 4.7 px became 16.7, or 31.7 on the `TermG` that was
reported.

**6d. Nothing deselected a marker, because the Data Display does it from a control this window does
not have.** "Clicking away from a marker needs to deselect it (even clicking in another panel or
opening the inline text editor)." `PlotCanvasView.OnCanvasPointerPressed` clears the selection on a
press against the empty plot-canvas BACKGROUND; the Designer lays its two plots out itself and has no
canvas, so a marker selected by clicking its info box stayed selected for the session — highlighted,
and still the target of Delete and the arrow keys. The handler is on the WINDOW and **tunnels**: a
press that lands on a plot, a slider or a specification field is handled there and bubbles nowhere a
deselect could hang off. Two exclusions, or the gesture would undo itself in the same event — a press
inside a `MarkerInfoBoxView` (that IS the selecting gesture) and a Ctrl/Meta press (the additive one).

**6e. A re-declared termination did not move the LADDER, only the specification.** "I changed the
TermG and it updated in the Termination 1 specification, but did not update in the schematic (the old
value was retained)." The glyph was not stale: it quotes `MatchNetwork.R1`/`R2`, the reference the
plotted response is actually referenced to, which is deliberate. But **the far end's reference is the
analysis end's scaled by Π N²** (§4.8), so re-declaring it does nothing until the transforms are
re-solved — measured on the shipped default, setting termination 2 to 25 Ω left the ladder presenting
15 Ω while the specification pane read 25, with the only hint (`Π N²  ✘ not reached`) three panes away.

Two halves, and both are needed:

- `MatchDesignerViewModel.RelinkAfterSpecChange` re-drives one unlocked transform at its CURRENT N so
  `MatchLinkage.Redistribute` puts the others where the new required ratio needs them. This is the
  same move `LinkTransforms`'s own setter already makes the instant it is switched on. It runs from
  **user edits only** — never `SetTarget` or `OnModelChanged`, because a design must not rewrite
  itself just by being opened, and a commit on load would put an edit nobody made on the schematic's
  undo stack. It replaces the edit's own `Commit`, so an edit is still exactly one undo entry.
- When the linkage cannot get there — Link off, every transform locked, the target outside their
  ranges — the glyph reads `25 Ω (target 40 Ω)`. A symbol that silently displays a number the user did
  not type is the bug however defensible the number is; `MatchLadderLayout.Build`'s `ohmsText` now
  takes the END as well as the resistance, which is what lets the caller compare the two.

**7. The Window menu now enumerates `ICrfMenuWindow`, not a list of window classes.**
`WorkspaceViewModel.EnumerateWindowEntries` listed the shell plus floating `CrfHostWindow`s; a Match
Designer is neither. An `OfType<MatchDesignerWindow>()` there would have been a menu the NEXT
standalone editor window is silently missing from.

**8. The inline text editor is now ONE control with two hosts.** `Controls/SchematicInlineEditBox`
carries the box's appearance, the placement arithmetic (`LeftPad`/`TopPad`/`AscenderRatio`/
`MarginFor`) and the label font-size rule; `SchematicView` and the Designer's network pane both use
it. The page's own `CalcInlineEditWidth` was an assumed 0.55-em-per-character estimate and is now a
real Skia measurement against the label typeface — the same correction `ReadoutStripView` already
took for its strip editor. What is deliberately NOT shared is what a hit MEANS: on a page it writes an
`EditableParameter`, here it runs the transform search above.

`MatchSchematicCanvas` cannot call `SchematicHitTest.TestComponentLabels` for its own label
hit-testing — that walks an `EditableSchematic`, and this pane is a projection with no edit model
behind it. It reads `SchematicComponent.LabelRowGeometry` with the same port count and glyph
half-height `SchematicRenderer.DrawLabels` passes, which is the part that must not drift.

**9. `docs/user` was already 264 files out of date with its generator on this machine, BEFORE any of
this round's changes** (verified by stashing the whole change and regenerating from a clean tree —
same 264). Round 5 moves several Match figures, so a regeneration is genuinely owed; it was not done
here because folding an unrelated 264-file diff into this change would bury it. Run
`tools/DocGen/check-docs-current.sh` deliberately, as its own commit.

## The workspace figure: capturing the whole shell, and making a capture machine-independent (2026-08-20)

Added `workspace-overview` and `workspace-regions` to the docs factory — the real `WorkspaceWindow`,
its dock tree, its panels and its open documents, rendered to SVG. `docs/design/user-docs-factory.md`
§11 carries the architecture. What is here is what fails **silently**.

**1. A detached window content has no DataContext, and the whole dock tree binds through it.** The
generator renders the window's *content* (it draws its own frame, §3.3), so the fixture takes the
content off the `WorkspaceWindow`. That loses the inherited DataContext. `DockControl.Layout` then
binds to nothing, and the capture is a correctly-rendered toolbar above **an empty grey rectangle** —
no exception, no warning. Set the DataContext on the content after detaching it.

**2. `DocsApp` needed App.axaml's DataTemplates, not just the `ViewLocator`.** The ViewLocator maps
`XViewModel` → `XView` by name. Every Dock tool and document view-model is named nothing like its view
(`ProjectTreeTool` → `ProjectTreeView`, `SchematicDocument` → `SchematicView`) and is mapped by an
explicit template. Without them every dock panel renders as **the literal text of its view-model's
type name** — `CircuitRF.Ui.ViewModels.Dock.ProjectTreeTool` in a box where the tree should be.
`DocsApp` now copies them off a real `App` instance instead of restating nineteen of them:
`App.Initialize()` is only `AvaloniaXamlLoader.Load(this)`, and it is
`OnFrameworkInitializationCompleted` — never called there — that opens windows and loads PDKs.

**3. A figure was carrying the machine that generated it, and this was NOT specific to the
workspace.** `WorkspaceViewModel`'s constructor reads the real `preferences.json` and restores the
PDKs installed from it. The capture therefore inherited the generating developer's **Window Layout**
preference (visibly: the Library panel sat in a different column), colour scheme, and installed kits
in the palette. With `check-docs-current.sh` regenerating and diffing, that is not cosmetic — the
output is not reproducible at all.

`CircuitRF.Ui.AppDataRoot` is now the single definition of `LocalApplicationData/circuitRF`
(`AppPreferencesIo` and `RecoveryManager` each computed it separately before), and `tools/DocGen`
redirects it to a throwaway directory first thing, so a docs run is always a first-launch
installation. **The environment cannot do this job** — measured, not assumed: on macOS .NET resolves
`SpecialFolder.LocalApplicationData` to `~/Library/Application Support` from the platform, so setting
`XDG_DATA_HOME` or `HOME` in-process changes nothing at all.

Two smaller churn sources are handled in the fixture: message timestamps go to the application's own
**Hidden** mode for the capture, and the message log — which correctly names the *absolute* path of
the temporary workspace just opened — is cleared and restated without it. Verified by generating
twice and diffing: byte-identical.

**4. The in-window menu bar is `IsVisible="{OnPlatform True, macOS=False}"`.** macOS puts those menus
in the system menu bar, which is in no visual tree and cannot be captured. Left alone, a figure
generated on macOS is missing a menu bar that Windows and Linux readers have on screen, *and* differs
from one generated on Linux. The fixture forces it on; the page says where macOS puts it.

**5. A numbered region is located by finding its control, never by a coordinate.**
`WorkspaceRegions.Catalog` is one row per region — number, title, sentence, a locator
(`ByType<ProjectTreeView>()`, the toolbar `StackPanel` by style class, `DocumentControl` for the tab
strip) and which corner of it the dot sits in. A region that cannot be found, or that arranged too
small to carry its number, fails the run and names itself. The legend beside the figure is generated
from the same list, so the number in the picture and the number in the table cannot disagree.

**A watch-out, found by diffing:** `CalloutDot` was extracted so the toolbar's dots and the
workspace's are one definition. Deriving the font size as `diameter * 0.583` instead of
`diameter * 10.5 / 18.0` changed every existing toolbar figure from `font-size="10.5"` to `"10.49"` —
a full-figure diff for a rounding difference nobody would ever look for.

**Unrelated, same session: `figure.figure .frame` was full-width.** It is a flex container, and a
flex container is block-level, so a narrow figure — a 440 px trace card, the Wire Profile panel — sat
marooned in a box the width of the whole article (owner). `width: fit-content` plus
`margin-inline: auto` shrinks the frame to the SVG's own intrinsic width (every generated file
carries `width`/`height` as well as a `viewBox`) and re-centres it; `max-width: 100%` still holds a
wide figure to the column, at which point the SVG scales down inside it. Same fix is not needed for
`figure.symbol`, which is an inline-block and already shrink-wraps.

## User docs, content build-out — the tool-chapter half (2026-08-20)

`brief-user-docs-content.md`, the four chapters it left as stubs: `reference/em-setup.md`,
`harmonicarf.md`, `wbond.md` and `match.md`, their figures, and the double-check pass. What is here
is the part that cost time to find or that changes what somebody should do next; the pages
themselves say what the software does.

### Three source fixes the docs factory measured, and one it could not

The factory's font check reports every character no circuitRF typeface covers, because Skia then
substitutes a **platform** font and bakes its name into the SVG. Two of those were real defects
rather than doc-generation noise:

- **`🔒` and `🔗` in the Match Designer** (U+1F512, U+1F517 — the transform-rack lock and the link
  toggle) are covered by **no shipped fallback either**: the redirect target, DejaVu Sans, does not
  have them, so the figures carried tofu and the app itself depends on a platform colour-emoji font
  being present. Replaced with `mi:MaterialIcon` `Lock` / `LinkVariant`. This is the same failure
  the Technology `▾` had, and the same fix.
- **`▸` / `▾` in three Match Designer buttons** — Solutions, Export, `+ add`. Covered by DejaVu, so
  they rendered, but they still substituted a platform font. Replaced with `ChevronRight` /
  `ChevronDown`.
- **What is left is deliberate**: `∠` in the harmonicaRF readouts (U+2220) and `✔`/`✘` in the Match
  status strip. Both are *content* — the angle operator and a matched/not-matched verdict — not
  decoration, and DejaVu covers both. The cost is that **DejaVu Sans now ships with the docs
  (3 faces, ~2.1 MB)** the moment any page cites a figure containing one. Worth knowing before
  adding a fourth uncovered glyph: the bundle already pays for the family.

### Back-annotation of an EM result into a schematic is reachable only from tests

`EmBackAnnotation.Annotate` places-or-updates an ordinary `SnP` component pointing at a run's
`.sNp`, is idempotent, and is exercised by `PlanarRunTests` and `EmCoSimulationTests`. **Nothing in
`src/Ui` calls it** — no button, no menu item, no command. The shipped co-simulation route is
therefore manual: run the EM setup, place an `SnP`, point its `File` at the written path.
`reference/em-setup.html` documents that as it ships and says the automatic route is not wired.

### A `.cem` figure records the machine that generated it

`em-setup-loaded.svg` contains the Solver-options line *"This machine reports 10 core(s)"*, because
the Cores row prints the host's own core count. It is honest and it is a committed artefact, so a
regeneration on a different machine produces a real diff in that one figure. Nothing is wrong; it is
the one figure in the set that is not machine-independent.

### Headless capture: what renders and what does not

- **A `ContextMenu` opens and composites** (`DocFixtures.OpenContextMenu` — the schematic
  context-menu figure proves it). **A `MenuItem`'s submenu does not**: neither `MenuItem.Open()` nor
  setting `IsSubMenuOpen` produced a visual root that drew anything, so a figure of the harmonicaRF
  menu bar is not obtainable this way and the menus are documented as tables instead.
- The in-window `Menu` is `IsVisible="{OnPlatform True, macOS=False}"` and the docs are generated on
  macOS, so even the bar itself is invisible unless a fixture forces it.
- **The Layout Editor's wire buttons are gated on a reachable workspace shell**
  (`UpdateWirePanelButtonStates`), which a headless fixture has not — so Draw Wire / Rotate Wire /
  Transform / the two wBond panel buttons are **absent from `toolbar-layout`** and from its
  manifest. The wBond chapter uses `{{toolbar: wbond}}` (the standalone editor, which shows them) as
  its button reference and says so.
- **Blocking on an async view-model call deadlocks the run.**
  `DocDataDisplayFixtures.Await` pumps the dispatcher for exactly this reason and its comment says
  so; using `GetAwaiter().GetResult()` instead hung the generator with no output for six minutes
  before it was killed. It is now `internal` so other fixture files use the same helper.

### The Wire Profile panel's default plane is YZ, and wires usually fly along x

`WBondViewState.DefaultProfileAxisDegrees` is 90°, so a profile view opened on an array bonded along
x draws it **edge-on** — ten vertical sticks where the arch should be. It is not a bug (the plane is
a picker, and 37° is a legal answer), but it is the first thing to change when the profile view looks
empty. The doc fixture commits `XZ` through `CommitProfileAxisText`, the same path the combo uses.

### Two brief statements that the shipped code has moved past

Both are recorded because the brief asked for them and the pages deliberately say something else:

- **"the profile editor edits every wire carrying that profile"** — there is no loop *profile* any
  more. `LoopShape` replaced it on 2026-08-18 and a wire's own points are the only truth about its
  shape. The shipped unit of bulk editing is the **array**: the profile view's alt-drag scales every
  wire in the group. `reference/wbond.html` documents the array and carries a note for anyone who
  read the older design text.
- **"`alt` drag adjusts loop height AND span"** — true in the **profile** view (both axes,
  independently, every frame, applied to the whole array). In the **layout** view alt-drag scales
  **span only**, and correctly so: that view has no z axis to have meant anything by, and the drag is
  projected onto the wire's own chord. Both are stated on the page.

### A placed `Match` does not carry `MatchDesign`'s C# defaults

`MatchDesign`'s field initialisers are the interstage case (200 Ω ‖ 0.125 pF to 1.25 Ω + 10 pF,
3.3-5.0 GHz); `MatchEmbedding.DefaultDesign()` — what placement actually seeds — is 50 Ω to 10 Ω over
1.8-2.2 GHz **with a solution already applied**. Reading the record's defaults tells you nothing about
what a user sees. The docs show both: the placed default, and the interstage case built explicitly.

### An all-inductor Norton solution is DC-singular, and the solutions list still offers it

`MatchEmbedding.DefaultDesign`'s own comment records why the shipped default prefers a *capacitive*
transform: three ideal inductors in a π are, at DC, a loop of ideal shorts, so the network sweeps
S-parameters perfectly and refuses to DC-solve. That is a property of the solution, not of the
default — the list offers every transform — so `reference/match.html` carries it as a callout: if the
design has to DC-solve, take the capacitive solution.

## Match round 3 — the Designer's schematic, its grid, and two crashes (2026-08-20)

The owner's third pass. Most of it is presentation and is recorded in the code itself; four findings
cost real time to reach and are kept here. The `NortonTransform.Discover` half is a Core finding and
lives in `src/Core/Match/RESOLVED.md`.

### An open inline editor was in the LAYOUT, which is why the panel around it grew

Owner: *"When the user invokes the inline text editor, the view around it shifts by a few pixels. For
example the entire Band Group box gets larger when the user double-clicks on the f1 value."*

`InlineEditText` is a `Panel`, and a `Panel` measures to the union of its children. Opening the
editor adds a `TextBox` as a second child, and a `TextBox` is bigger than the `TextBlock` it covers in
**both** directions — a border, a 2 px horizontal padding, and a taller line box. So the control grew,
which grew its `Auto` grid row, which grew the card, which moved everything below it. **Nothing was
wrong with the editor's own size**; the fault was letting layout see it at all — which is why the
round-2 fix for "the box is too wide" (measuring its width from its text) reduced the shift without
removing it.

`MeasureOverride` now returns `_display.DesiredSize` and nothing else, in both states, and
`ArrangeOverride` places the editor over the resting text at its own desired size — overflowing the
control's bounds by the couple of pixels it is bigger, which paints outside without moving anything
because nothing above it in the tree ever hears about it. The typing handler calls
`InvalidateArrange`, **never** `InvalidateMeasure`. The alternative — reserving an editor-sized slot
permanently — would pay those pixels in every row of every panel forever to save an overflow nobody
can see.

*This is a shared control.* The Match Designer's specification pane is where it was reported, but
every `InlineEditText` in the application inherits the fix.

### A fit performed FROM the render pass has nothing to invalidate

Owner: *"I changed a PI to T network and app crashed: `Visual was invalidated during the render pass`
at `MatchSchematicCanvas.ZoomToFit()` at `MatchSchematicCanvas.Render()`."*

A structural change clears `_fitted`; the next `Render` re-fits; `ZoomToFit` ended with
`InvalidateVisual()`; Avalonia refuses an invalidate raised from inside the render pass outright
rather than silently re-entering. Guarding the call site is the wrong fix — the right one is that the
frame a render-pass fit would ask for **is the frame being drawn**. `Fit()` is the arithmetic with no
invalidate in it; `ZoomToFit()` is `Fit()` plus the repaint. `Render` calls `Fit()`.

The general shape, worth remembering outside this file: **any `Render` that lazily computes state
must not use the same entry point the event handlers use**, because that entry point almost always
ends in a repaint request.

### The grid headers could not line up, because they divided a different column pool

Owner: *"The column headers in the grid view are not horizontally aligned with the contents below it
in its own column."*

The header row's `ColumnDefinitions` was `1.4*,*,1.2*,0.6*,0.9*,Auto` and the item template's was
`1.4*,*,1.2*,0.6*,0.9*`. The sixth `Auto` column held the **Copy as CSV button**, so the header
divided a star pool ~80 px narrower than the rows did and every boundary landed somewhere else — the
misalignment grew with the button's own width and was invisible in any single-column inspection.
Moving copy to the listing's context menu (which the owner asked for separately) removed the sixth
column, and the two strings are now identical with a test holding them so.

Secondary and real: a `Button` header centres its content and pads it, while the row beneath it is a
bare `TextBlock` starting at the column's left edge. `Button.gridhdr` is borderless, transparent,
`HorizontalContentAlignment="Left"`, and has no horizontal padding.

### `Copy as CSV` is built in code-behind, not declared in the AXAML

A `ContextMenu` is a popup with its own visual root, so a `MenuItem` declared inside one is not
reliably reachable by `FindControl` from the window — and a handler that silently never attaches is a
menu entry that does nothing, with no error anywhere. Every other menu in `MatchDesignerWindow`
(Add transform, Export, Settings) is already built in code for the same reason; the CSV menu now is
too, attached to the `ItemsControl` (which *is* in the window's name scope) at `OnLoaded`.

### The brace's shape lives in `MatchBraceGeometry`, not in the draw operation

A shape asked for on **aesthetic** grounds has to be inspectable without a running application, and
the canvas's draw op is a private nested class inside a `Control` — nothing outside a live Avalonia
render pass can reach it. `MatchBraceGeometry.Outline` returns the under-brace as world-space path
steps and `Stem` returns the drop to the label; the canvas maps each through its own world-to-screen
transform and owns no shape of its own. That is what let the round-3 result be rendered to a PNG and
looked at, and it is what the brace test asserts against — the tip points down, the ends curl up, and
**every turn's control point is the corner itself**, which is the whole difference between a brace and
a bracket with rounded corners.

### Two smaller things that were quietly wrong

- **The pane's shunt glyph box lied by ten units.** `MatchSchematicModel.Element` declared a shunt
  element's glyph as ±210 when both built-in glyphs run lead tip to lead tip at ±200. That box feeds
  `SchematicComponent.LabelBaseYFor`, which pushes labels clear of a glyph deeper than the default
  offset — so ten units of invented margin moved every shunt label ten units down, and the flattened
  cell (which computes the box from the real primitives) disagreed with the pane by exactly that.
  The label offset is now derived from the editor's own label metrics rather than hand-tuned, so it
  tracks a change to them; the hand-tuned `−482` it replaces had gone stale.
- **An absorbed element is placed by the LAYOUT, not by the synthesis.** `MatchSynthesis.Build`
  emits each arm L-then-C, so an end arm whose absorbed half is the C has the arm's own L standing
  between the parasitic and the termination, and `WithEndSplits` inserts the Fano/detune element
  further out still. `MatchLadderLayout.DisplayOrder` walks the absorbed element out to its end —
  **stepping only over elements it provably commutes with**: two adjacent ladder elements share an
  arm exactly when they share an orientation (a shunt run hangs off one node in parallel, a series
  run is one chain), so the walk stops at the first orientation change. A blanket "move it to index
  0" would be a different circuit whenever a Norton transform had left an absorbed element somewhere
  other than an end arm.

## Match round 2 — the Designer's panes (2026-08-19)

The owner's second pass, all of it about how the Designer LOOKS and reads. Four findings are worth
keeping. The N = 1 half of the same round is a Core finding and lives in
`src/Core/Match/RESOLVED.md`.

### The network pane is now a real `SchematicModel`, not a second drawing of one

Owner: *"The network schematic does not look good. It looks very different than a regular circuitRF
schematic. Can we host a virtual schematic view in the Match Design… that way the network schematic
look and feel will always be linked to a circuitRF schematic."*

`MatchLadderPreview` drew the network itself: its own symbol walk over `BuiltInSymbols.Primitives`,
its own wires, its own three-line label block, its own colour lookups. Every convention it shared
with the editor was a convention somebody had copied — and the round-1 bug list proves the point:
the walker had no `PolygonPrimitive` case, so the four interface pins (a hexagon plus a stem) drew as
*nothing at all* while every other glyph was fine.

It is replaced by two pieces with a much smaller contract:

- **`MatchSchematicModel.Build(ladder)`** projects the ladder onto the read model the schematic
  editor's canvas consumes — placed `SymbolKind.Inductor` / `Capacitor` components (R0 for a shunt
  arm, R90 for a series one, which is the built-in glyph's own orientation and a page rotation of
  it), `SymbolKind.Pin` on each interface terminal, wires, junction dots, `GridSize = 100`.
- **`MatchSchematicCanvas`** hands that to `SchematicRenderer.Draw` and adds wheel-zoom and
  drag-pan. No overlay is passed, no hit-test is done: there is no `EditableSchematic` behind this
  and a component here has no persisted position to move.

The grid, the glyphs, the LOD fade, the connected-pin markers and the three label roles are now the
editor's by construction. **Two things a schematic has no way to say** are drawn by the canvas over
the finished frame: an *absorbed* element is washed towards the background (the renderer has one
symbol colour and no notion of an element this component does not contain), and an *out-of-range*
value gets a warning box **around the glyph, not over it** — the capacitor is an ordinary capacitor,
its value is the unbuildable part.

**The trap in that overlay:** `SchematicComponent.FullBb` is inflated by
`LabelWidthFor`, a deliberately generous per-character estimate floored at 500 world units. It is
right for culling and about four times too wide for a mark the user is meant to read as being about
one component — the box has to come from `GlyphBb`.

### A rebuild that changed no shape must not re-fit the view

Dragging a transform's N rebuilds the ladder on every frame. A pane that re-ran zoom-to-fit on each
new `MatchLadderLayout` would swim under the pointer for the whole gesture. `MatchSchematicCanvas`
compares the incoming layout's SHAPE — names, orientation, type and position — and keeps the user's
zoom and pan when only values moved; a structural change (an element added, removed or reordered)
re-fits, because the old viewport is then framing a circuit that no longer exists.

### The `IconSelectButton` theme had to move before a second window could use it

Owner: *"Replace all radio UI selectors with the custom UI element we created for the trace card
S/Z/Y selection."* That control is `IconSelectButton`, and its ninety-line `ControlTheme` lived in
`PlotInspectorView.axaml`'s own resources — reachable from the Data Display's visual tree and nowhere
else. It is now `src/Ui/Styles/SegmentedSelect.axaml`, merged into `CircuitRfResources.axaml`, with
its `PART_Button`'s `seg-btn` base look moved to `CircuitRfStyles.axaml` alongside it (both
application scope, both therefore in *both* Applications by construction — the rule those two files
exist to enforce). The Data Display keeps only the selectors that name controls only it hosts, the
`MaterialIcon` and `PlotTypeGlyphControl` foregrounds.

The control is **list-driven**, which is the part that reaches the view-model: it shows one choice
and opens the rest in a popup, so a selector needs its options as a list and its state as one value
rather than as one boolean per option. `TopologyChoice`, `KindChoice` and `FormChoice` are those
values; the `IsSeries`/`IsC`/`IsPi` booleans stay, because they are what the design round-trips and
what the existing gate tests assert against.

### The N row's height was the Fluent Slider, and the fix already existed

Owner: *"The N indicator in the TRANSFORMS panel has a very large height. Perhaps the slider is
messing with it."* It was: Fluent's `Slider` reserves room for a tick band and a full-size thumb, so
one in a row sets that row's height. **Setting an explicit `Height` is the wrong fix** — it squashes
the thumb off the track. The Data Display's inspector had already solved this and `Slider.compact` is
its solution: no `Height`, a negative vertical margin (`4,-7`) that pulls the layout footprint back
in, `VerticalAlignment=Center`. The slider keeps its star column, so its width is untouched.

### The unit combos were duplicating what the inline editor already does

Round 1 turned the specification fields into `InlineEditText`, whose entry string CARRIES its unit
(`"50 Ω"`, `"1.5 nH"`) and whose commit both parses a typed unit and pins the display unit. The
`ComboBox` beside each field survived that change and had been a second, redundant way to set the
same thing ever since. All three are gone (R, X, and the shared band unit). `InlineEditText` gained
`HorizontalContentAlignment` in the same round so a value can sit against the right edge of a
label-left/value-right row and **stay there when the editor opens** — a value that jumps to the left
edge on double-click reads as a different control appearing, not as the same one opening.

## Match round 1 + group delay (2026-08-19)

The owner's first pass over the shipped Match component and Designer. Four findings are worth
keeping; the rest of the list was ordinary polish.

### The shipped default was not a matching problem, and its own order picker had a dead end

`MatchEmbedding.DefaultDesign` was 1-2 GHz, order 3, **50 Ω to 50 Ω**, both ends resistive. It
synthesised, so nothing was ever red — but Π N² is 1/1, there is nothing for a Norton transform to
do, and the FIRST entry of its own order picker (order 2) returned no solutions at all. Giving either
end a reactance — the obvious first thing to try — landed on "Termination 2 is not absorbable at
order 3" immediately. The new default is **1.8-2.2 GHz, order 4, 50 Ω down to 10 Ω**, chosen by
MEASURING robustness rather than by taste: every valid order, every response family, and every single-
and double-step change reachable from the specification pane (either topology, either reactance kind,
at either end) still returns at least one solution.

### A real transformation has to arrive with a solution APPLIED, and which one is not free

50 Ω into 10 Ω needs transforms to REACH it, so an unapplied default opens on
`Π N² 1 / 0.271 ✘ not reached` with termination 2 flagged — the wall of warning text the change was
supposed to remove. `DefaultDesign` now runs the (deterministic) solution search and applies one.

**But the first-ranked solution transforms the INDUCTOR pair, and that network cannot DC-solve.** A
Norton transform replaces one element with a π of three of its own KIND; three ideal inductors in a
loop is a loop of ideal shorts, which is a singular MNA system — the S-parameter sweep runs perfectly
while `NonlinearDcEngine` returns residual 1 and never converges. The pick therefore prefers a
solution whose products are all CAPACITORS (a series capacitor in the middle branch is a DC open).
This is a property of the DEFAULT only: an inductor Norton transform is a legitimate thing for a user
to apply, and every one is still offered. Gated by
`MatchStampTests.TheShippedDefault_IsNotAnInductorLoop_AndDcSolves`.

### The ladder preview drew no interface pins because it had no polygon case

`MatchLadderPreview.DrawPrimitives` handled Line/Polyline/Arc/QuadCurve/Circle. The built-in `Pin`
glyph is a **hexagon `PolygonPrimitive`** plus one stem line — so the hexagon drew as nothing and the
stem landed exactly on the wire it connects to. The visible result was *no pin at all* rather than
half of one, which is why it read as "the pins were never added". Nothing else the preview draws is a
polygon, which is how the gap survived.

Two other geometry traps in the same file: the spine must be drawn in the GAPS between series
elements (a built-in glyph carries its own leads out to ±200, so a full-width port-to-port line lays a
second wire across every series body), and the vertical air must exceed the horizontal (`AirY` 500 vs
`AirX` 340) or a series element's three-line label is clipped off the top.

### A slash centred exactly on its wave does not "cross" it

The Match symbol's strikethroughs were re-sloped and shortened. A symmetric slash centred on the
wave's own centre passes THROUGH the point `(0, ∓55)` that the wave itself passes through, and two
segments meeting at a shared point do not strictly cross — which
`MatchComponentPlacementTests.TheSlashesCrossTheOuterWaves_AndNotTheMiddleOne` correctly reads as "not
struck through". The 4-unit y offset in `BuildMatch` is load-bearing, not cosmetic.

### An inline editor that only commits on LostFocus never closes

`InlineEditText` (new, `src/Ui/Controls/`) shares harmonicaRF's two pure decisions —
`InlineEdit.ValueSelectionLength` and `InlineEdit.MeasureWidth`, now one implementation for both — but
hosts them differently: the strip floats its box in a `Canvas` overlay because its columns are
width-shared, while a Designer row is a fixed grid cell and swaps in place. Three things had to be
got right and each was reported: it opens on **double**-click (a single click opens an editor the user
meant to click past); the box carries an explicit measured `Width` and `HorizontalAlignment=Left` (a
stretched box fills its whole `*` column however short the value is); and a click outside is caught by
**tunnelling from the top level**, because clicking a non-focusable area — a label, a drawn canvas,
the pane background — moves focus nowhere at all, so `LostFocus` never fires and the editor just stays
open. A `Panel` with no `Background` is also not hit-testable, so the double-click only landed when
the pointer hit a glyph exactly.

### Group delay now exists in RfCore

`RFNetwork.GroupDelay` (seconds) plus a `NetworkMetrics.GroupDelay` cube adapter, and
`DerivedParameters.GroupDelay` in the Data Display beside μ/μ′. It is **not** a `NetworkMetric` member
and that is structural: every member of that enum is a function of ONE matrix, which is what lets
`EvaluateTwoPort` loop point by point; group delay is a derivative ALONG the sweep. It is also **not
reference-independent** — S21's phase is defined against the terminations — so the adapter
renormalises to a uniform real reference first, exactly like every other 2-port metric. The Match
Designer's own private copy is gone. Supersedes this file's earlier "Group delay does not exist
anywhere in circuitRF, and was not made to".

## Match MN-5 — Flatten to Cell (2026-08-19)

`docs/design/match.md` §11. A designed `Match` becomes an ordinary cell folder — `.ccell`,
`schematic/<name>.csch`, `symbol/<name>.csym` — whose schematic is the LC network it synthesised, with
both terminations carried along disabled and the design recorded twice (in a text annotation and, as
the blob, on the cell). One path serves both entry points (`MatchFlattenService`), reached from the
Designer's footer button and from the schematic context menu, where the item is **shown only for a
`Match`** and disabled-with-its-reason when it cannot run.

Files: `src/Core/Match/MatchFlattenPlan.cs` (the framework-free topology), `src/Ui/Match/MatchFlatten.cs`
(layout, annotation, symbol copy, transactional write), `src/Ui/Match/MatchFlattenService.cs`,
`src/Ui/Commands/Schematic/FlattenMatchCommand.cs`, `src/Ui/Views/Dialogs/MatchFlattenDialog.axaml`.

### A base64 blob cannot be a declared cell parameter — every placement would refuse to elaborate

The brief says the `Design` blob is "carried onto the cell". The obvious home — a `CcellParameter` —
is a **trap**. A cell's declared parameters are seeded onto every placed instance as *overrides*, and
`Elaborator.BuildCellScope` evaluates an override **eagerly, as an expression**, in the parent scope.
A base64 payload is not an expression (it contains `+` and `/` and resolves as an unbound identifier
at best), so every instance of a flattened cell would have failed at elaboration — not at flatten
time, and not visibly related to the parameter that caused it. A cell *default* is bound lazily and
would have survived, which is exactly what makes this the kind of defect that ships.

It lives on `CcellFile.MatchDesign` instead — cell metadata beside `ExternalNetlistPath`,
`WhenWritingNull` so every other `.ccell` stays byte-identical, and read by
`MatchFlatten.TryReadDesign(cellDir)`. The replacement instance therefore carries **no parameters at
all**, which `TheDesignBlob_IsCellMetadata_NotADeclaredParameter` pins from both sides.

### The nets must be walked over the STAMPED ladder, not the whole one

`MatchNetwork.AssignNets()` mints a node per series element across *every* element. An absorbed
element is not in the cell's live netlist — the external network supplies it, which is §8.2's whole
premise — so walking the full list mints a node for an end series arm that is not there and shifts
every net after it. `MatchFlattenPlan.Build` filters first; that is what makes its `Port1Net`/
`Port2Net` the same two nodes `MatchModel` stamps as `Nodes[0]`/`Nodes[1]`.

### Six significant digits misses the 1e-12 equivalence gate by five orders of magnitude

The gate is "the component and the flattened cell agree to 1e-12 in S-parameters". The Designer's own
display precision (`MatchValueFormat.Format`, clamped to 12 digits and used at 4–6) carries a relative
1e-7 into every element value. `MatchFlatten.Exact` writes **G15** in an engineering unit; the unit
scale is a power of ten and therefore not exact in binary, so the round trip costs about an ulp.
**Measured agreement: 1.26e-15** end to end (real files, real extraction, `SParameterEngine`), and
4.3e-16 at netlist level. The digit count is load-bearing, not a display choice.

### The `Term`s carry the LADDER's port resistances, not the terminations' — on purpose

For §4.9's golden design the network's own ends are 1.68 Ω and 1.25 Ω while the terminations are
200 Ω and 1.25 Ω; the missing factor is exactly the Π N² = 119.03 the transforms have not been applied
to reach. Writing the *termination's* R would break §11.3's stated purpose — enabling both `Term`s and
running the cell alone must reproduce the Designer's plot, and that plot is referenced to the ladder's
ends (`MatchResponse.At` uses `network.R1`/`R2`). In a finished design the two numbers coincide, which
is what the transforms are for. In an unfinished one the annotation now says which is which, because
1.68 Ω sitting beside a 200 Ω termination otherwise reads as a bug.

### Undo deletes the cell folder here, and deliberately does NOT in Layout's Group into Cell

Two features, opposite answers, and both are right. Layout's `CommitGroupIntoCell` keeps its folder
(R-L3c-6): there the *cell* is the deliverable, it may already have been opened and edited, and the
instance is incidental. Here the *replacement* is the deliverable and the cell is written from a design
still sitting in the schematic, so a Redo can rewrite it byte-identically — and an undo that left the
folder behind would make the next Flatten refuse the name it had just handed back.
`FlattenMatchCommand.Undo` deletes only when the folder still holds **exactly** the three files it
wrote, byte for byte; anything else and it stays, with a warning naming it.

### Two MN-3 tests were superseded rather than deleted

`FlattenIsWiredAndDisabled_AndSaysWhichBriefItWaitsOn` asserted the footer button was dead and named
MN-5 in its tooltip. It is now
`ADisabledFooterControl_NamesAConditionTheUserCanActOn_NeverABriefItWaitsOn`, which is the property
that survived MN-4 building the probe and MN-5 building the flatten. `MatchDesignerHostingTests`'
"the Designer never calls `ShowDialog`" was narrowed to `window.ShowDialog` — the Designer window is
still non-modal, and the *name prompt it opens* is modal and rightly so.

### What was reused, and the one seam that was added

Reused unchanged: `CellFolder.CreateCellFolder`, `CellPersistence`, `SchematicPersistence.SaveToFile`,
`SymbolPersistence`, `AtomicFile`, `NameValidator`, `InputNameDialog`'s shape, `ChangeComponentTypeCommand`'s
replace-in-place pattern, and `MatchNetwork.AssignNets`. **`SavePlan`/`SavePlanExecutor` were read and
not used**: every step of that machinery is expressed in terms of a `SchematicDocument` being
materialised out of scratch (`SaveStep.Document`, `Document.Materialize`), and a flatten has no
document — it has a `SchematicEditModel` built from a design. What it *does* have that the executor
does not is **rollback**: `MatchFlatten.Write` creates the folder itself and removes it again if any
later step throws, so a failure part-way leaves nothing behind. There is no portable way to make a
filesystem write fail at that exact point, so `Write` has an `internal` overload taking a
`faultAfterSchematic` action — null everywhere in the application, and the only reason
`AFailedWrite_LeavesNothingBehind` is testing the guarantee rather than the argument check.

### Deferred, named rather than forgotten: the Designer does not open on a flattened cell

The blob round-trips (`MatchFlatten.TryReadDesign(cellDir)`, pinned by
`ReopeningTheGeneratedCell_ReconstructsTheOriginalDesign` — same ladder, same transforms, same N's), but
there is **no menu item that opens the Match Designer on a flattened cell instance**. Binding it there
would be a lie in two directions: `MatchDesignerViewModel.Commit` writes the design onto its *target
component's* `Design` parameter, so an edit would change nothing about the cell's already-written
components, and the parameter it wrote would be the expression-shaped landmine the first finding above
exists to avoid. The honest version is a mode whose only action is "flatten again to a new cell"; that
is a small brief of its own, not a line of plumbing.

## Match MN-4 — the Probe button (2026-08-19)

`docs/design/match.md` §10.4/§10.5. `src/Ui/Match/MatchProbeAvailability.cs` answers "can this pin be
probed, and if not, which reason is it"; `MatchDesignerViewModel.Probe.cs` runs one off the UI thread
with the engine's own `RunControl`. The measurement, the fits and the ranking are
`CircuitRF.Engine.Matching.TerminationProbe`'s — see `src/Engine/RESOLVED.md` for what was found there,
including why the conjugate is applied to the fit rather than to the data.

### "The pin is unconnected" is not a state an extracted testbench can report

MN-4 §5 lists *the pin is unconnected* and *the pin's net carries no component other than the `Match`*
as two disabling reasons. **After extraction they are one reason, plus a different one that is not
about wiring.** An unwired pin still gets a net of its own out of `NetExtractor` — the union-find seeds
component pin positions — so it reads as `NetIsBare` ("net 'n3' carries nothing but MN1 itself"), not as
ground. What genuinely reads as unconnected is a pin the extractor could give no net to *or* one wired
straight to ground, and those two are indistinguishable from a testbench and mean the same thing here.
So `PinUnconnected` is spelled as "sits on ground — either wired there, or not wired to anything, which
reads the same way once the schematic is extracted", and the two states have separate fixtures
(`APinTiedToGroundIsDisabled_AndSaysSo`, `ANetCarryingOnlyTheMatchIsDisabled_AndSaysSo`). Deciding this
from the canvas instead would mean a second implementation of what a net is, and the two would
eventually disagree on exactly the cases the button exists to catch.

### The Designer is handed a `SchematicViewModel` and nothing else, so the cell resolver had to be wired onto the session

A hierarchical extraction needs an `ICellResolver`, and the only one is `WorkspaceViewModel`. The
Designer never sees it — `MatchDesignerWindow.Show(schematicVm, comp, owner)` is the whole binding — so
`SchematicViewModel.CellResolverProvider` joins `WorkspaceRootProvider` and
`WorkspaceDisplayUnitProvider` on the session, set at the two places a session is built. Without it a
probe on a schematic containing cell instances would silently measure a flat circuit missing them. The
probe also takes its base directory from `WorkspaceRoot`, which is what a Run passes, so a file-backed
model resolves the same way in both.

### The provenance rule lives in `SetTermination`, not at the edit sites

§10.5 — "the user's override always wins and is never silently re-probed" — is one line in the single
method every termination edit already goes through: anything not flagged `fromProbe` clears
`Probed`/`ProbedAtUtc`. Spelling it at the six call sites (R, topology, kind, value, and the two staged
text commits) is how it would eventually be forgotten at the seventh.

### Small things

- The four fits are listed **including the non-physical ones**, labelled and with their residuals —
  §10.2 is explicit that the residual is data the user is entitled to see, never a hidden gate. Each
  physical row carries a *Use* button, which is §10.2's "take the second-best when you know better", and
  a hand-taken second-best is still probed provenance.
- The `Conjugate` toggle is on `MatchDesign` (`Term1Conjugate`/`Term2Conjugate`), not on the view-model:
  it changes what the next probe produces, so it has to still be set after a reload. It does **not**
  change a stored termination — flipping it re-probes nothing, per §10.5's snapshot rule.
- `MatchDesignerSettings.ProbeResidualWarning` defaults to 0.05 (match.md §14.5) and only ever adds a
  warning; the best physical fit is applied at any setting.

## Match MN-3 — the Match Designer (2026-08-19)

`docs/design/match.md` §9: the component's own window — specification pane, live ladder preview,
response plots, the linked Norton-transform rack, and the solutions list. View-models under
`src/Ui/Match/`, views under `src/Ui/Views/Match/`, tests in `tests/Ui.Tests/Match/`. It adds no
synthesis: every number comes out of `src/Core/Match`.

### `namespace CircuitRF.Ui.Match` breaks four unrelated files, and the error names none of them

`Match` is `System.Text.RegularExpressions.Match`. A namespace of that name anywhere under
`CircuitRF.Ui` makes the bare identifier resolve to the NAMESPACE from every other `CircuitRF.Ui.*`
file, so `src/Ui/Layout/TechImport`'s three regex readers stopped compiling with
`CS0118: 'Match' is a namespace but is used like a type` — in files that have nothing to do with
matching networks. **The namespace is `CircuitRF.Ui.Matching`; the DIRECTORIES stay `Match/`**, which
is what the brief actually asks for. MN-1 had already made the same choice
(`CircuitRF.Core.Matching`) and the reason was not written down.

### A transform range that does NOT move is a theorem, not a stale bound

The obvious test of "the slider's range is recomputed, never stored" — add two transforms and check
the second's bounds moved — **fails on most pairs, correctly**. `NortonTransform.Range`'s positivity
threshold is `1 + z1/z2` (or its reciprocal): a RATIO. Absorbing the ideal transformer scales
everything on one side by N², which leaves every such ratio untouched. So a pair lying entirely on
one side of an earlier transform has *exactly* the same range before and after, and could never tell
a recomputed bound from a stored one.

The pairs whose range does move are the ones containing a transform's own PRODUCTS. That is what
`SliderBounds_AreRecomputedAgainstTheLadderAsItStands` uses, and it makes the assertion stronger than
the brief's version: the same pair, the same row, two different bounds, from two different values of
the first transform's N.

### With Link on and ONE transform, N is pinned — and on a real problem it pins onto the pole

`MatchLinkage` with a single slot sets `N = clamp(sqrt(required))`. On match.md §4.9's own interstage
problem that is `sqrt(119.03) = 10.91` against a positivity threshold of `5.989`, so it clamps onto
the bound and one of the three π products comes out at kilohenries — exactly the pathology
`MatchSolutionSearch.Drive`'s remarks describe, reached here through the UI instead of through the
search. **Nothing repairs it**: the status strip says `Π N² 35.87 / 119.03 ✘ not reached`, the ladder
shows the value, and adding a second transform on a genuinely different pair (`L1/L2` + `L3/L4`)
lands both on N = 3.303 and the product exactly on target. The user-facing consequence is that the
first useful gesture is usually the solutions list, not `+ add` — the list enumerates the sets that
work and ranks the simplest first.

A second transform taken from the FIRST one's products is offered and is nearly always useless: its
range comes out degenerate (`[1, 1]`) because the products are already extreme. It is still offered,
because hiding a legal pair would be a rule the user cannot see; the row simply shows its own bounds.

### An infinite element value surfaces three layers away, as an unresolved NAME

A transform parked a part in 1e9 from its threshold produces an infinite element. `double.ToString("R")`
writes that as the literal `Infinity`, `CnlReader` accepts it as a token, and the failure arrives from
the expression engine as `UnresolvedNameException: Unresolved name 'Infinity' in scope 'global'` —
from inside `Elaborator.ResolveParameters`, with nothing anywhere naming the transform that caused it.
`UpdatePlots` now refuses at the netlist boundary and names the element and the cause; the ladder, the
grid and the status strip stay usable.

### Group delay does not exist anywhere in circuitRF, and was not made to — SUPERSEDED 2026-08-19

> It exists now: `RFNetwork.GroupDelay` / `NetworkMetrics.GroupDelay`, and
> `DerivedParameters.GroupDelay` in the Data Display. See "Match round 1 + group delay" above. The
> Designer still INJECTS the numbers through `Trace.SetCubeData` for the reason below — only the
> arithmetic moved.

`DependentVarFormat` is `{Complex, Db, Mag, Phase, Real, Imaginary}` and RfCore has no group-delay
metric — a repo-wide search for the term finds only prose. §9.6 asks for it on the second plot.

It is computed in the Designer and injected through `Trace.SetCubeData(..., transformBaked: true)`,
the seam the Data Display already uses for any value it has reduced to numbers itself. A sixth
`DependentVarFormat` was rejected deliberately: a new member has to mean something for a Smith trace,
a table cell, a marker readout and a persisted `.cdd`, and none of those wanted it. The delay is
`-dφ/dω` on the UNWRAPPED S21 phase — unwrapping first is not optional, since a raw `Complex.Phase`
jumps by 2π somewhere in every passband and a difference across that jump is a delay spike the width
of the grid.

### Touchstone cannot carry the design's two port references

§9.9 asks for "the per-port references R1/R2 written as the file's own". Touchstone's option line
carries ONE real R, which is why `TouchstoneIO`'s own per-port note prints the uniform value N times.
The export writes the data **unrenormalised** (it is referenced to R1 and R2, and renormalising to
hide the format's limit would change the numbers a reader gets), puts R1 on the option line, and
states both references in header comments above it. This is the one thing in §9 that is not buildable
as written.

### Opening the Designer writes nothing, so a hand-authored echo can be stale

The six ECHO parameters (`F1`, `F2`, `Order`, `Response`, `R1`, `R2`) are rewritten on every committed
edit and on none other. A `Design` blob written by hand into a `.cnl`, with the echoes left at their
placement defaults, therefore still shows `F1 = 1 GHz` on the page until the user changes something.
The alternative — syncing on open — is an undo entry and a dirty document produced by nothing more
than looking at a component, which is worse.

### The plots are held for the drag; nothing else is

Measured on §4.9's problem, order 4, 11 elements, 2 transforms, 401 plot points:

| | per step |
|---|---|
| live drag step — linkage, rebuild, ladder, grid, status | **0.20 ms** |
| the same step with the response sweep | **6.6 ms** |
| release — one response sweep | **5.8 ms** |

The sweep is ~30× the rest of the chain, so `BeginTransformDrag`/`EndTransformDrag` hold the two
`PlotControl`s for the gesture and refresh once on release. The ladder, the element values and the
status strip are never throttled — they track the slider live, which is the thing the window exists
for. `MatchDesignerDragCostTests` measures it and is deliberately NOT `Category=Benchmark`: at ~3 s
for the file it belongs in the default gate, where a regression in the drag path is noticed.

The same reasoning governs two other costs. **Response feasibility** (Butterworth and Bessel go
through `MatchPrototypes.Search` — a 33-point shape sweep with two refinement rounds, each building a
ladder and scoring it over 201 frequencies) and **the solution search** both run only when the
SPECIFICATION changes, never on a transform drag. Neither can be changed by moving a slider.

### Three colour roles, and `Default.ccolor` needs no edit

`Match.Absorbed`, `Match.Negative`, `Match.Bracket` — added to `ColorRole` (constants + `All`) and to
both variants of `ColorTheme.BuiltIn`. `ColorThemeTests.DefaultPresetFile_MatchesBuiltIn` still
passes without touching the asset, because `ColorTheme.Resolve` falls back to `BuiltIn` for a role a
theme leaves unsaid — a new role only has to be added to the FILE when its shipped value differs from
the built-in one.

Absorbed carries its own alpha rather than being a lighter grey: dimming has to read as dimming over
whatever the preview's background happens to be. Out-of-range takes precedence over absorbed in
`MatchLadderLayout.RoleOf` — a negative value is the one thing the user has to act on, and dimming it
would hide it.

### This project has no `DataGrid`

`Avalonia.Controls.DataGrid` is not referenced. The value grid is a header-plus-rows `Grid` with
click-to-sort header buttons (`MatchDesignerViewModel.SortElements`); adding a package for five
read-only columns is not a trade worth making. Sorting is display order only and never touches the
ladder, whose order IS the topology.

### The guided mode is deliberately not built — do not re-derive it as a gap

The reference implementation has a one-click "add the transform that reaches the required ratio".
brief §7.2 says not to build it and this records why: the solutions panel already enumerates every
valid transform set and ranks the simplest first, which is the same answer with the reasoning visible.
A second, opaque path to the same place is worse than one.

### Two things are wired and disabled, and say which brief they wait on

`Probe` (per termination) and `Flatten to Cell…` are present, disabled, and carry tooltips naming
**MN-4** and **MN-5**. Neither is stubbed in a way that looks implemented.

## Match MN-2 — the symbol, the registry entry and a new palette category (2026-08-19)

The schematic half of `docs/design/match.md` §8.4: `SymbolKind.Match`, the bandpass glyph, prefix
`MN`, and `ComponentCategory.Matching` — a new category for one component, on the owner's decision
and deliberately against the `wBond` precedent of not inventing one. Findings about the model, the
stamp and the elaborator are in `src/Core/Match/RESOLVED.md`.

### A new `ComponentCategory` has FOUR sites, and missing one fails quietly in a different way each time

The enum, `LibraryCatalog.CategorySortKey`, `LibraryCatalog.AllFilterPinnedOrder`, and
`PaletteTool.BuildCategories`. Nothing errors if one is missed:

- **no sort key** → the category falls to the catch-all rank and sorts among `Other`, so the tile
  appears in a place nobody would look for it;
- **not in `BuildCategories`** → the category exists, `ByCategory` returns its members, and the
  picker simply never offers it — a filter that cannot be selected;
- **`AllItems` needs nothing at all**, because it is derived from `Enum.GetValues<SymbolKind>()`.
  That is the one that lulls you: the tile shows up unaided under "All", which reads as done.

`PaletteTool.RealDisplayName` genuinely needs no entry — "Matching" is one word and falls through to
`ToString()`. Asserted rather than assumed, since the two-word categories beside it do need one.

### `PaletteFilterOrderingTests` hard-codes the pinned-row COUNT

`AllItemsPinnedOrder_EverythingAfterThePinnedRows_KeepsAllItemsOwnRelativeOrder` splits the list at a
literal 22 — the length of `AllFilterPinnedOrder`. Pinning `Match` made it 23 and the test failed on
a diff that reads like an ordering bug rather than a stale constant. It is now a named constant with
a note; anything that pins or unpins a row has to bump it.

### The strikethroughs are plain lines and the waves are `SinePrimitive`s — so the test intersects them

The glyph is three stacked full-cycle sines with a slash across the top one and the bottom one. The
sine primitive rotates itself; the slashes do not know what they cross, and a slash that reads as a
strikethrough only at 0 degrees is the specific mistake the brief warns about. The gate samples the
wave from its own parameterisation, runs both it and the slash segment through the same
`SchematicGeometry.LocalToWorld` the renderer uses, and asserts a genuine segment crossing at all
four rotations x mirrored — **and that the MIDDLE wave is not crossed**, since a third slash there
inverts the glyph from bandpass to bandstop while still looking deliberate.

### The exported netlist puts the payload FIRST on the instance line, and that is the risky order

File ▸ Export Netlist writes `Match:MN1  n1  n2  Design=<base64>  F1=1 GHz  F2=2 GHz  …` through
`CnlWriter`, unquoted. `CnlReader`'s spaced-assignment merge reads a token ENDING in `=` as an empty
assignment and glues the next token on as its value — so a PADDED payload followed by any other
parameter on the same line arrives as one run-on string and decodes to nothing. `MatchEmbedding`
strips the padding for exactly this reason (MN-1), and the echo parameters sitting after `Design` are
what make the trap reachable rather than theoretical.
`MatchComponentPlacementTests.TheDesignSurvivesAnExportedNetlist` is the round trip that would notice.

### Only `Design` is hidden from the parameter rows — the echoes stay visible on purpose

`IsMatchPanelParameter` covers `Design` alone, following `IsWBondPanelParameter`'s mechanism. The
echoes (`F1`, `F2`, `Order`, `Response`, `R1`, `R2`) are the only description of the network a user
has until MN-3 ships, so hiding them would leave a component with nothing readable about it at all.
They are read-only in the sense that matters — `MatchModel` never reads them back, which
`MatchComponentTests.TheEchoParameters_AreNeverReadBack` pins by contradicting every one of them and
showing the ladder is unchanged — but they are still TYPABLE in the generic editor. Making them
genuinely read-only needs a row-level mechanism `ParameterRowViewModel` does not have; that belongs
with MN-3's panel, not here.

## The built-in assembly rule set, and the two routing faults that hid it (2026-08-19)

Owner: *"Do we have a DRC rule for touching wires? ... If no `.wasm` has been referenced by the user
yet, have circuitRF use a built-in rule set."* Then, on the same rule: *"have a 'clearance' value.
Default is 0.5 mil from the outer edges of the wires."*

### The rule existed. Two things about it were wrong, and both were silent.

**1. `FindIntersections()` could not see wires that touch EXACTLY.** It was `FindCloserThan(0.0)`, and
that method reports `clearance < limit`. So interpenetration was caught and exact contact was not —
and exact contact is not an exotic geometry to draw: **an array laid out on a pitch equal to its own
wire diameter produces a clearance of exactly 0.0**, which is both the case a bonder cannot run and
the case the check stayed quiet about. The limit therefore has to be strictly positive; it is
`WBondBuiltInRules.MinimumClearanceNm`, one picometre — a hundredth of an atom, so no real clearance
is swallowed by treating it as contact.

**2. Zero was the wrong threshold anyway.** Zero is the point at which the design is impossible, not
the point at which it is buildable: two wires a nanometre apart pass a zero test and short on the
first sweep of encapsulant. The rule now enforces a real guard band — **0.5 mil surface-to-surface**,
between the wires' outer edges, which is what `WireGeometry3D.Clearance` already returns.

### The set is named, and it is named everywhere a check reports

`WBondBuiltInRules` (in `src/Ui/Layout/Assembly/`, beside `WasmDefaults` rather than in `Drc/`, for
that folder's own reason). The panel line, the diagnostics and `RulesEvaluated` all say which of the
two rule sets answered. The old wording — *"No assembly rules resolved for this design, so only the
wire geometry itself was checked"* — described a check that had, in fact, run one rule; a user reading
it beside a clean result could not tell whether the result meant anything.

**What may go in this set is deliberately narrow**, and the 0.5 mil is the interesting case. A `.wasm`
rule is a STATEMENT BY A HOUSE, and a number circuitRF invented reported as though a house had stated
it would pass a design the house rejects — the failure `WasmDefaults`' "PLACEHOLDER" wording exists to
prevent. The clearance is legitimate here for exactly two reasons, and would not be otherwise: it is
reported under this set's own name, never a house's, and **the user can change it**. Any further
built-in rule wanting a number of its own has to clear the same bar.

The value lives in `AppPreferences.WireClearanceMil` via `WBondWireClearance` (the `EmSolveCores`
accessor-with-test-override shape), edited in Settings ▸ Wirebonds. Per USER, following
`CheckDrcOnExport`'s own argument: a workspace arriving from someone else must not silently loosen a
check you rely on. It cannot live in the `.wasm` — storing it in the document whose absence it exists
to cover is circular.

**It keeps running when a `.wasm` IS resolved**, and that is not an oversight: a house's rule file is
not what makes overlapping metal invalid, so a file stating a looser spacing rule does not repeal it.

### The rule was unreachable from the one editor whose subject is wires

Two independent routing faults, either of which alone would have hidden it:

- **`ActivateWBondDocumentForProperties` called `_factory.DrcTool?.SetActiveLayout(null)`.** A wBond
  document's wires live on its REFERENCE LAYOUT — `WBondDocumentViewModel.OnReferenceLayoutChanged`
  installs the design there, and the layout's own DRC is where the assembly half is evaluated — so
  emptying the panel for that document type meant the wire rules could only ever be run from a `.clay`
  that happened to contain a wirebond cell. `CheckDesignRules`' `CanExecute` had the same shape
  (`IsLayoutDocumentActive`), so the menu item, the shortcut and the toolbar button were dead there
  too. Both now resolve through `ResolveDrcTargetLayout`.
- **A `.wBond` opened from disk with no embedded geometry had no reference layout at all**, so the fix
  above would have worked for a NEW document and silently not for an opened one. `EnsureReferenceLayout`
  moved from `NewWBond` into `TrackNewWBond`, which every entry point already goes through. (It also
  fixes a second, older silence: a cell dragged in from the project tree had nowhere to land.)

### The DRC panel's own Check button posted nothing to Messages

Three entry points, and only two of them reported: the Design menu and the torn-off-window fallback
each grew the same six lines, and the panel's button grew none — it ran the check, filled the list, and
left Messages silent. That mattered the moment the run had something to say beyond a violation count.
One `DrcRunReport.Post`, three callers. Diagnostics first, verdict last, for the ordering reason
`WBondBackgroundRun` already argues: an answer above its own footnotes reads as belonging to the run
before it.

### Touchstone export: the link was there, and then something else landed on top of it

`WBondPublishCommands` did post `Success("Wrote s-parameters", path)` — the Messages panel renders a
path as a reveal-in-file-manager link. But the LAYOUT-hosted entry point then posted the outcome
**again** through `LayoutEditorViewModel.ReportMessage`, which goes to the same panel and carries no
path. So the last line after an export was a linkless duplicate and the line with the link sat above
it, looking like part of the progress trace. `Outcome` now carries `Posted`; the terse
"Wrote s-parameters" is replaced by the full "Exported wirebonds.s3p — 3 port(s), …" line, posted once,
last, with the path. `WBondEditorView` still calls `ShowStatus` — that is its own status line, a
different surface.

### Cost

Nothing here is quadratic. The rule runs on every check of every wirebond design, so it goes through
`WirePairSweep`'s uniform-grid broad phase (600 wires = 179,700 unordered pairs; ~2 ms against ~417 ms
naive), built with the same limit it is queried at so the broad phase cannot prune a pair the narrow
phase would report. The routine-tier guard asserts on the sweep's own COUNTERS rather than on
wall-clock — a timing assertion in the default gate measures the machine under parallel load; a
`TestedPairs` vs `AllPairs` ratio catches an accidental all-pairs scan immediately and costs nothing.

## Dragging a wire over another: the refusal that undid the gesture, and the crash behind it (2026-08-19)

Two owner reports, one mechanism:

1. *"I was dragging a wire in the wBond host layout, but circuitRF crashed"* — `InvalidOperationException`
   out of `CapacitanceReduction.Compute`, via `Republish` and `OnPointerMoved`.
2. *"when I drag wires overtop of other wires, the dragged wires move back to their old position during
   the drag and my mouse is no longer overtop of the wires that I was dragging. This seems like a glitch."*
3. *"during drag, if wires land on other wire, the Array Inductance panel stops updating (even when wires
   are moved off of the other wires during the same drag)."* — the first cut of the fix for (2), which
   held correctly and then never let go.

The physics — why a wire in the ground plane makes **P** singular, and why `L` hides it — is in
`src/WBond/RESOLVED.md`. This is the editor's half.

### Report 2 is the refusal doing what it was designed to do, in the wrong place

`RefuseEdit` rolls the design back to the most recent undo snapshot, which during a drag is the
**pre-gesture** state. Passing one wire over another is an ordinary thing to do with a mouse, and for
the instant the two coincide the matrices are singular — so an ordinary drag was being classified as a
failed *edit* and undone underneath the cursor. The hand then carried on dragging from a grab point
with nothing under it.

**A transient degeneracy is not an edit; it is a position the geometry is passing through.** So the
geometry now keeps moving with the hand and only the NUMBERS stop — the same priority the quality
ladder already applies when a frame cannot afford its fill (*"the geometry always moves and the canvas
always redraws; the FILL is the only thing that can be skipped"*). `RefuseFill` splits on `_inGesture`:

- **mid-gesture** → hold. `ReadoutIsHeld` goes true and the panel keeps the numbers from before.
- **outside one** → `RefuseEdit`, unchanged.

`EndGesture` settles it. If the wires were **dropped** on a degenerate position rather than dragged
across one, *that* is an edit and undoing it is right — at the moment the button comes up, where nothing
moves out from under the cursor.

### Report 3: what "held" must keep holding, and what it must not

The first cut held the whole fill, which is what report 3 is: once the wires touched, nothing recovered
until the release. The separation that fixes it is worth stating plainly, because it is not obvious from
either report:

> **L is well defined at every position the wires can occupy. It is only its Cholesky *factor* that
> ceases to exist while two of them coincide.**

So `MoveWiresUnfactored` keeps the mesh and the matrix exactly up to date through the held frames and
lets only the factor lapse (`IncrementalFill.FactorIsStale`), and each frame retries `TryRefactor()`.
One of them succeeds the instant the wires separate and the panel is live again mid-drag. Recovery costs
one fresh factorisation — O(N³/3), ~23 ms at N = 600 — paid on that frame alone; the alternative, a full
rebuild, is the O(N²) *fill*, which is the expensive half and is what makes "just rebuild each frame"
unaffordable on a real design.

Two consequences fell out and are held by their own code rather than by memory:

- `RefuseFill` re-runs the move **unfactored** on entry, because `MoveWires` threw part-way down its own
  loop and the wires after the failure never got their rows. Without that the matrix is not exact and
  there is nothing for `TryRefactor` to recover *to*.
- `IncrementalFill.Reduce()` refactorises when the factor is stale rather than reducing against it.
  A reduction over a factor the matrix has moved past is *silently wrong*, which is worse than the throw
  a genuinely singular matrix earns.

### Report 1: two guards, and the crash went between them

`CommitPointMove` and `CommitStructuralChange` each wrapped only their own **inductance** work in
`RefuseEdit`. `Republish` — which refills and refactorises **P**, and reduces the array-basis matrix —
sat downstream of both with nothing around it. Since `IncrementalFill` revisits only the *moved* wires'
rows while `RefreshCapacitance` refills the *whole* mesh, a wire left degenerate by an earlier refused
edit is invisible to every guard upstream and fatal there. Three changes, in order of how much they
carry:

1. **`RefreshCapacitance` owns its own degeneracy.** A singular **P** is not an unevaluable edit — it is
   one quantity of the readout going away, exactly as when `IncludeCapacitance` is off. Rolling the
   drag back over it would be wrong twice: the drag did not cause it, and the inductance it would
   discard is still perfectly well defined. So the capacitance is dropped, `CapacitanceRefusal` says
   which wire and why, and it is said **once** rather than once per frame.
2. **`RefuseEdit` rebuilds afterwards** (`RebuildAfterFailedFill`). `IncrementalFill.MoveWires` is not
   transactional — it mutates mesh, matrix and factor before throwing — so a "refused" edit had been
   leaving the degenerate geometry in the mesh and a half-applied rank-1 update in the factor. A factor
   carrying half of one is *silently wrong* rather than loudly broken, which is the worse failure.
3. **`Republish` is a guarded region**, as a backstop for the array reduction's own small factorisation.
   Kept even though (1) covers the known case, because the lesson of the crash is precisely that
   "the fill would have caught it" is not a property anything enforces.

### A refusal decided in the constructor has no listener yet

The view attaches `EditRefused` after the view-model exists, so a design that arrives already
unevaluable is diagnosed with nobody listening. `Report` holds the reason and the event's `add`
accessor replays it to the first subscriber — otherwise the panel comes up silently missing its
capacitance rows with nothing on screen saying why.

## harmonicaRF opens in its own window, and its New/Close finally work (2026-08-19)

Owner: *"today when user selects Tools ▸ harmonicaRF, a docked harmonicaRF document is created.
Instead of docking it, create the document undocked. Size it roughly the same as the current
Workspace window, but a few pixels down and to the right so the user can still see the Workspace
window's title bar."* And: *"fix the harmonicaRF New menu command — it is supposed to create a new
harmonicaRF document but I don't think it currently does."*

### The float reuses the drag tear-off path, and must

`WorkspaceViewModel.OpenDocumentInOwnWindow` opens the document normally and then takes it straight
back out through `IFactory.SplitToWindow` — the same path a user's own tab drag takes, and the same
one `RestoreFloatingDocumentWindows` already uses. Hand-assembling a window model instead is what
produced the "a floating panel cannot be re-docked at all" bug recorded on
`CircuitRfDockFactory.FloatTool`. Consequences worth knowing: the window can be re-docked, it is
captured by the saved layout, it survives a layout rebuild, and — per R-fgn-2 — it now **survives a
workspace switch** instead of closing with the tabs. For an instrument that needs no workspace at all
that is the right side of that rule, but it is a behaviour change.

The two deferred posts after the float are not optional. A **programmatic** float does not go through
`OnDocumentDockPropertyChanged`, so without `TryWireHostWindowsUndo` and `TryWireWindowFocusTracking`
the new window shows "Close Workspace" instead of "Close Window" and, on macOS, no menu bar at all.

### Trimming, not sliding — the maximized-shell case decides the design

The obvious implementation asks for the shell's rectangle offset down-right and hands it to
`ScreenPlacement.Place`. **That silently undoes the whole request when the shell is maximized.** The
offset copy overhangs the working area by exactly the offset, and `Place` repairs an overhang by
clamping the POSITION — sliding the window straight back onto the shell's corner with its title bar
hidden behind it, which is the one thing the offset existed to prevent.

`TrimToScreen` gives up the offset's worth of width and height instead, leaving the corner where it
was put. `Place` then returns it byte-identical (its own gate 11: an already-reachable window is never
nudged), so the safety net is a safety net rather than the placement. Both halves are pinned by tests,
including the negative one that shows the untrimmed rectangle *does* get slid back.

The offset is `ScreenPlacement.TitleBarHeight`, not a taste: at that value the new window's frame top
lands exactly at the bottom of the shell's title bar, which is the stated requirement.

`Window.Position` is DEVICE pixels and `ClientSize` is already logical — mixing them is the bug
`AvaloniaScreenSource` exists to prevent, and it is invisible on an unscaled display.

### Why New was dead, and it was not where it looked

`HarmonicaView.NewDocument` was `Workspace?.NewHarmonicaCommand` **and nothing else**, so it needed a
`WorkspaceViewModel` to do anything. Two hosts have none:

- **The standalone `harmonicaRF` binary.** `HarmonicaShellWindow` is one plain window with no Dock and
  no workspace, so File ▸ New was a silent no-op — the item enabled, the click handled, nothing
  happening. It now opens another `HarmonicaShellWindow`, which is that shell's own stated model
  ("several documents means several windows") and the same answer `WBondShellWindow` already gives.
- **A FLOATING document window.** Dock sets a `CrfHostWindow`'s DataContext to the `IDockWindow`, not
  to the workspace, so `(TopLevel as Window)?.DataContext as WorkspaceViewModel` was null for a
  torn-off instrument. That was reachable by dragging the tab out before this change and is **the
  default path now**, so the fallback resolver (walk the app's windows for `WorkspaceWindow`, the same
  one `WirePanelKeys` and `LayoutEditorView` use) was mandatory, not a nicety.

`CloseDocument` had the identical hole in the adjacent method — no factory to ask, so it did nothing.
Standalone now closes the hosting window, which *is* the document there.

**Not verified here:** the macOS menu-bar handover. A docked harmonicaRF injects its own top-level
items into circuitRF's app menu (`HarmonicaAppMenuInjector`); a windowed one owns its window's menu
outright (`RecomputeAttachment`'s tear-off branch). This change makes the second path the default. It
is the path a torn-off document already took, but it cannot be exercised headlessly.

## wBond in the background, with the EM run's own progress rows (2026-08-19)

Owner request: *"When exporting Touchstone or computing anything with the MoM wire engine, we need to
do it in the background and update the Message panel with a progress bar. This infrastructure has
already been built for the Planar EM kernel so we should reuse it."*

Reused unchanged: `IMessageSink.BeginProgress`, `IProgressMessage`, `LiveProgressMessage`, and
`WorkspaceViewModel.ReportEmProgress`'s two-row split (sweep row = how far through, stage row = what
it is doing now). The new piece is `WBondBackgroundRun` — the adapter and the `Task.Run` wrapper —
plus one progress type the kernel can actually see.

### The one thing that could NOT be reused: `RunControl` itself

`CircuitRF.Engine.RunControl` is unreachable from `src/WBond`, and not by oversight.
**`src/Core` references `src/WBond`** (that is how the wBond `ComponentModel` reaches the physics
without a cycle) and **`src/Engine` references `src/Core`**, so `WBond → Engine` closes
`WBond → Engine → Core → WBond` and does not compile. `src/WBond/CircuitRF.WBond.csproj` says "NO
PROJECT REFERENCES, DELIBERATELY" and means it.

So `WBondRunControl`/`WBondProgress` are a deliberate near-copy, field for field, **so the UI reporter
reads identically for both** — `ReportEmProgress` and `WBondBackgroundRun.Report` are the same six
lines against the same five fields. Hoisting `RunControl` into a new shared leaf project was
considered and rejected: one 100-line type is not worth a project every consumer has to know about.
If `RunControl` gains a concept the wirebond kernel needs, **add it to both** rather than trying to
share the type again.

One real difference from `RunControl`, and it is not cosmetic: the report throttle is an
`Interlocked.CompareExchange` on a timestamp, not a `Stopwatch.Restart()`. The two setup fills tick
once per matrix ROW from every worker of a `Parallel.For` — hundreds of thousands of ticks — and a
stopwatch restart lets every thread through in the same interval. The CAS admits exactly one.

### The setup is the half that needed the bar, and the point counter cannot show it

A distributed run is `Create` (mesh, `L` fill, `P` fill, two Choleskys + inverses, `K̃`/`W`/`H`) then
`Solve` (one factorisation per point). **Setup is 34.5 s at N_s = 4,800 against 14 s a point** — and
during all of it the frequency counter is honestly stuck at 0 of N. That is exactly the "bar sitting
still is indistinguishable from a hang" case the EM two-row split was built for, so every setup step
is its own named stage with its own denominator (rows, Cholesky columns). Cancellation reaches inside
them too, for the same reason: a Stop that only landed between frequency points would leave the user
waiting half a minute after pressing it.

**The fills tick per ROW, not per entry, and the pace is therefore not uniform** — row *p* fills the
upper triangle from *p* to N, so early rows are the expensive ones and the bar accelerates. Rows are
still the right unit: they are checkable against the unknown count the mesh report already showed,
and counting entries buys a linear bar at the price of a counter reading `1,204,517 / 2,345,678`.

### The Compare dialog is MODAL, so a Messages-panel bar alone would be invisible

`WBondMomCompareDialog` is shown with `ShowDialog`, so the panel is behind it and unreadable for the
entire run — which is minutes on a large design. It gets its own `ProgressBar` and stage line inside
the dialog, fed from the **same** observations via `WBondBackgroundRun`'s `mirror` hook, and Run
becomes Cancel while a run is in flight (the EM panel's Simulate/Cancel arrangement). The panel rows
are still posted; they are the record afterwards.

### Two traps found while wiring it

**Handing one control to both models double-counts the sweep.** `WBondMomCompareViewModel.Compare`
runs the lumped model and then the distributed one, and both loop over the same frequency grid ticking
once per point — passing `run` to both would have counted 2N against a denominator of N and parked
the bar at 200%. The lumped half gets a stage label and no control; it is milliseconds anyway. A test
asserts no stage or sweep counter ever reports past its own denominator.

**A process-wide "one run at a time" latch belongs at the UI boundary, not in the view model.** It was
first put inside `RunAsync`, which makes two xUnit test classes that each run a comparison fail each
other under parallel class execution. It lives in `WBondMomCompareDialog.OnRun` and
`WBondPublishCommands.ExportTouchstoneAsync` now. The latch is real and is about memory:
`WireMomCost.SolveThreadCount` sizes the thread count against a quarter of available memory *on the
assumption that it is the only such run*, and the export button stays live while a run is in flight.

### The standalone binary has no Messages panel, and now says so out loud

`WBondShellWindow` is one window around one editor — no Dock, no workspace, no Messages region. Rather
than run silently for minutes there, `WBondEditorView.ResolveMessages` falls back to
`WBondStatusMessageSink`, an `IMessageSink` over the view's own status line. **No rule is needed to
pick which of the two live rows the single line shows**: `Report` writes the sweep row then the stage
row inside one synchronous callback, so the stage text is simply what is there when the frame is
drawn — and it is the more useful half for a host with no panel.

### A test-only ordering hazard worth knowing about

`Progress<T>` captures whatever `SynchronizationContext` is current when it is constructed. In the app
that is the Avalonia dispatcher, so the last observation and the `await`'s own continuation go through
one ordered queue and `Finish` always runs last. **In a test there is no context**, so it falls back to
the thread pool and a late observation can overwrite a settled row — assertions on a finished row then
pass or fail on timing. `WBondBackgroundRunTests` installs an inline `SynchronizationContext` so the
test is as ordered as production is, rather than hoping the pool happens to be.

## wBond MoM WM-2 — a Model option on the export, and a Compare dialog (2026-08-18)

brief-wbond-mom-w2 §7. The physics and every measurement are in `src/WBond/Mom/RESOLVED.md`; this is
the UI half and the two things about it that would cost someone time.

**§7.4's premise is wrong: the wBond menu item does NOT reach circuitRF.** The brief says
`ProgramWBond.cs` is a second entry point into the same assembly and that "the menu item and the
dialog therefore come along for free". The dialog does; the menu item does not. **`WBondMenuView`
lives only in `WBondShellWindow`** — the standalone shell — and in circuitRF the wBond editor is a
document tab under `WorkspaceWindow`'s own menu bar, which has **no wBond Design submenu at all**
(`WorkspaceWindow.axaml`'s Design menu is the *layout* DRC). So a menu item alone would have shipped
the feature to one binary of the two, silently and with a passing test.

**The entry point that genuinely reaches both is a toolbar button on `WBondEditorView`** — the view
`App.axaml` data-templates for `WBondDocument` *and* the view `WBondShellWindow` hosts. The standalone
menu item binds to that same view method (`CompareDistributedModelAsync`), which is the pattern the
shell already uses for undo/redo/copy/paste and the reason a menu item and its gesture cannot
diverge. `WBondOneEditorTests.EverySurvivingClickHandler_IsWBondsOwn`'s allowlist gained
`OnCompareDistributedModel`, which is the test that would otherwise have caught it as a *violation*
rather than as a fix.

**A source scan of both menu trees is not enough on its own.** Avalonia resolves a missing `Command`
binding to nothing: the menu item renders, is enabled, and does nothing, with no error anywhere. So
the XAML scan asserts the exact command name in both trees *and* a reflection assertion confirms
`WBondMenuViewModel.CompareDistributedModelCommand` exists and invokes its hook. Either half alone
passes on a typo.

**`Predict` does not refuse, so the panel had to ask separately.** `WireMomMesh.Predict` is
deliberately allocation-free and non-refusing (WM-1: the report exists so a number can be shown before
anyone waits). A dialog that shows the report and only discovers "this design has no ground plane"
after Run is exactly the failure the report exists to prevent, so `WireMomMesh.RefusalFor` was added
and the compare panel shows the refusal *instead of* the report. `Build` is still the only thing that
throws.

**The lumped export path was not touched, reorganised or shared.** `TerminalAdmittances(design, freqs)`
is byte-for-byte the method it was; the distributed branch is a separate method and `BuildNetwork`
chooses between them. Round-trip tests against a real `SParameterEngine` solve hold the lumped bits,
and a refactor that keeps those passing while moving the last bits is the kind of change nobody catches
for a year.

**`Distributed` + `ArrayPairs` is refused, not silently corrected** — the dialog disables Export and
shows the reason rather than switching the port basis for the user. An array-pair port is a floating
pair; the distributed model's whole content is a shunt to the reference plane, and a floating pair has
no terminal for that current to return through.

**A wirebond design is reachable from TWO editors, and I assumed one.** The owner reported this twice
before it was right (2026-08-18: *"the export UI can't be accessed from circuitRF — only from wBond"*,
then *"I still don't see any of the new buttons in the wBond hosted layout. They are supposed to appear
to the right of the Transform button"*). The second sentence is the whole diagnosis and I had missed it
in the first:

- **`WBondEditorView`** — a `.wBond` opened as a document of its own, and the entirety of the standalone
  `wBond` binary. Its own toolbar row carries Export Touchstone (since WB-E) and now Compare.
- **`LayoutEditorView`** — a `.clay` with a `.wBond` beside it (WB40), which is **how a wirebond is
  normally worked on inside circuitRF**. There is **no `WBondEditorView` anywhere in that document**.
  The wire tools live in the layout editor's own toolbar, in the group
  `UpdateWirePanelButtonStates` shows on a wirebond cell — Wire Profile, Array Inductance, Draw Wire,
  Angle Wire, Transform Wires — and **that group ended at Transform**. So Export Touchstone was never
  reachable from that editor either; it is not a regression from this brief, it is a gap WB-E left and
  nobody hit until now.

The fix is two buttons at the end of that group and one shared implementation,
**`WBondPublishCommands`** — the picker flow, the extension handling and the refusal reporting are
subtle enough that a handler per view would have drifted, and the repo's own rule is to route every
entry point through the same accessor. `WBondEditorView.Touchstone.cs` was rewritten to call it too, so
there is one copy rather than two.

**A button added to that group without being added to its gate is visible on every ordinary layout in
the application** — the gate is one `IsVisible = show;` assignment per control in code-behind, so a new
member is one forgotten line from being permanently on screen with nothing failing.
`TheNewWireButtons_AreGatedWithTheRestOfTheWireGroup` reads the method body and requires each name in it.

*What my first fix did, and why it was not enough.* The Compare button was moved out of the
view/panel-toggle group into `WBondEditorView`'s publish group beside Export Touchstone, and restyled to
match its neighbours (`Background="Transparent" BorderThickness="0"` — it had default button chrome).
That was worth doing and is kept, but it only ever touched the editor the owner was not looking at.

**No DataGrid.** This repo carries no DataGrid package (`Avalonia could not generate code-behind
property … Unable to resolve type DataGrid`), so the comparison table is a header `Grid` plus one
`Grid` per row sharing one column string, inside a `ScrollViewer`. Ten columns did not need more.

## wBond round 7 — the loop-profile object is removed (2026-08-18)

Owner: *"User does not care if the profile is any of these 'profiles'…"* And the answer that
settled it: *"User would never share one loop shape for multiple arrays and want to edit it in one
place."* `LoopProfile`, `Wire.ProfileBinding`, `WireArray.Profile`, `WBondDesign.Profiles` and the
ball/wedge designation are gone; `LoopShape` holds the same arithmetic as stateless functions.

**The designation was never load-bearing and the codebase said so.** `BallBond` and `WedgeBond` were
one function, `Peaked`, at 0.30 and 0.50 peak span. **Nothing in the repo ever branched on ball
versus wedge** — the only place either word was read back was `WireTableCsv`, choosing which factory a
CSV column called. It was a seed shape and nothing else, which is exactly the owner's reading.

**A group loop-height change was straightening hand-routed wires, and the editor and the netlist
disagreed about the same wire.** `WBondViewModel.Groups.SetGroupLoopHeight` read a wire's shape, put a
new height on it and stamped it back — and stamping writes X and Y by linear interpolation between the
feet, so a wire routed around an obstacle came back as a plain planar arc. The same wire's
`LoopHeight_G1` controlling parameter had gone through `WireEdits.SetLoopHeightPreservingPath` since
2026-08-17. Both now do. `SetWireLoopHeight` had the same defect. **`SetGroupSpan` and the group flip
deliberately still go Read → transform → Write**, because both of those genuinely ARE X-Y operations
and interpolating between the feet is correct there.

**One `ColorRole` was serving two unrelated meanings, and BOTH of them had to go.** `wBond.FreeWire`
meant "no binding" in the layout view (`WBondRenderer:228`) and "a non-representative member drawn
behind the band" in the profile view (`:400`). The first pass deleted the layout half and renamed the
second to `wBond.Member`, keeping it because distinguishing the one editable curve from the members
behind it is real information about that picture. **The owner rejected that within the hour**:
*"profile wire still renders in 'bad' color depending on shape… I don't want the wires ever changing
colors based on geometry."* The role is now deleted outright — `wBond.Wire` is the only wire colour,
with `wBond.WireStart` and `wBond.WireVertex` as per-POINT accents every wire carries alike. **A role
deletion has to touch `src/Ui/Assets/Color/Default.ccolor` as well**, which two tests read directly.

*Worth keeping about the pixel oracle that gates this*, because both traps cost real time: the
envelope band is the wire's own RGB at alpha ~60, so **band and wire are the same HUE and only
brightness separates them** — a ratio-based colour probe counts a six-figure band pixel count as
"wire" unless it also applies a brightness floor. Worse, `wBond.WireStart` is a DARKER version of the
same hue, so the band matched the input-end accent by ratio too; with an accent-fringe dilation in
play that marked the whole canvas as accent and the probe silently passed against a deliberately
broken renderer. Verify a pixel oracle by breaking the thing it watches — the first version of this
one passed three separate injected regressions before the histogram showed why.

**No `.wBond` format-version bump and no compatibility shim — and none is needed.** `System.Text.Json`
ignores members the target type does not declare, so a file carrying `Profiles`, `arrays[].profile`
and `wires[].profileBinding` reads cleanly and stops carrying them on its next save. **No geometry is
lost in either direction**, because `Points` was always stored explicitly. Gated by a STRING fixture
in `WBond.Tests/PersistenceTests` — a round trip cannot test this, since no current code path can
write the old fields.

**"Which array does a new wire join" was answered by profile identity, which is why the layout drop
path had a `FreeProfileName` helper.** It invented a uniquely-named throwaway profile purely to force
`AddWire` to create a NEW array. `AddWire` now takes `arrayName`, and the drop path passes
`editor.NextArrayName()`. **`arrayName: null` deliberately means "the first array", not "a new one"** —
an array IS a pin pair on the generated symbol, so making one per drawn wire would grow the symbol
every time a user draws a wire in the editor. The two interactive draw tools pass null; only the drop
path asks for a new group.

**Kept: Copy / Paste Profile Coordinates** (`ProfileCoordinateText`). It is a one-shot transfer the
user asks for by name, not a persistent link — the link is what was rejected. Its signature changed
from `LoopProfile` to a `ShapePoint` list plus a loop height; `ProfileForGroup` became
`ShapeForGroup` and reads the shape off the array's first wire.

**Two hit tests that must agree are two that can disagree — the profile view could not click-drag.**
Owner: *"In Wire Profile view, I cannot click and drag wire points or segments. I must first
drag-select with marquee tool, then it allows me to drag."* `WBondProfileCanvas` resolved its hit with
its own `SpanMode` and `Azimuth`, then handed the raw coordinates to `WBondPointerController.Press`,
which hit-tested them AGAIN with `HitTestProfile`'s defaults — `azimuthRadians: null`, meaning AUTO,
each wire on its own chord. **The shipped profile plane has been YZ since 2026-08-16**, so the two
answered for different pictures: the canvas found a wire, the controller found nothing, cleared the
selection, and the canvas then declined to arm a drag on an empty selection. **It hid completely
whenever anything was selected**, because a press on an already-selected element takes the
`keepSelection` branch and never calls the controller — which is exactly why marquee-selecting first
made dragging work. Fixed by passing the resolved `WireHitTest.Hit` rather than a second set of
projection parameters; the plane-blind overload stays for the layout view, which has no plane.

**"Group Wires As…" was disabled on a right-clicked wire.** Owner: *"For a single wire right-click,
this menu should be available and it should say 'Group Wire As…'."* The item was selection-scoped by
deliberate design (*"a group change that quietly applied to one wire when the user meant forty is
expensive to notice"*), while `BuildStraightenItem` immediately below it already used
selection-if-multi-else-clicked-wire. Group now uses the same rule, which answers the original risk
better than the refusal did — a multi-selection always wins, so a right-click can never shrink a
forty-wire subject to one. `WBondGroupCommand.Label` already had the singular string; nothing had ever
reached it, because the only way to get a count of one was a single selection and a single selection
loses to the click.

**A profile-view drag moved one wire; it now moves the group.** Owner: *"I want all the wires within
that group to move."* Alt-drag was moved onto the array on 2026-08-17 (WB24c) and the plain drag was
not moved with it, so the two gestures in the same view disagreed about their subject. All three
paths — drag, alt-drag, arrow-key nudge — now resolve through `WBondViewModel.ProfileGroupSubject`.
**The `Selection` is deliberately NOT promoted**, matching what alt-drag already did: it is the
subject of one edit, not a re-selection. That has a consequence worth knowing —
`WBondPointerController.BeginDrag` had to learn the subject, because the quality ladder collapses
those wires, the incremental fill is told about those wires, and `EndDrag` recomputes the exact answer
for those wires; a subject wider than the selection would otherwise move on screen and leave the
inductance stale for every sibling.

**The envelope has now been narrowed twice and both narrowings were the same mistake.** Owner: *"the
envelope for a wire doesn't get drawn/included in the envelope if I drag a point/segment too far
away."* The band first spanned only the members bound to a `LoopProfile` (so detaching removed a
wire), and then only the members that are `IsProfileEditable` (so dragging one point past its
neighbour made the XY path backtrack and removed a wire). In both cases **a wire fell out of the thing
that claims to describe its own group, as a side effect of an edit about something else.** It now
spans every member unconditionally; `ArrayProfile.NonMonotone` reports the backtracking ones as a
SUBSET of `Members` rather than as an exclusion. The general shape is worth carrying: a summary view
that quietly drops its outliers is worse than one that shows an awkward spread.

**The envelope band was drawing spread that no wire in the group had.** Owner: *"envelope rendering
appears a little strange if wires within the same group have a different number of vertices."*
Sampling every member's own vertices — the 2026-08-16 fix — makes each member a straight LINE between
consecutive samples, but **the envelope of a set of lines has a corner wherever two of them cross**,
and the band joined its samples with a straight line. Near a crossing the drawn max ran above every
member and the drawn min below every member. It is invisible while an array's members share a vertex
lattice, because then they cross only AT shared vertices, which are already sampled; **mixed vertex
counts interleave two lattices** and the members cross repeatedly in mid-interval. Measured on a
7-point and a 9-point wire: 2,591 nm of overshoot, at a span that is a vertex of neither. Fixed by
inserting a sample wherever the argmin or argmax changes, solved in closed form from the two samples
that bracket it — at most one extra sample per interval per edge, so the drawn curve count is
unchanged. **The oracle has to probe BETWEEN the band's samples**: at a sample the band is min/max by
construction, so a test that only looked there passes against the broken version.

Two things found alongside it and worth keeping:

- **The dedupe could eat the band's own final sample.** Sample positions are collapsed with a 1e-3
  tolerance keeping the FIRST of a cluster, so a member vertex a hair short of 1.0 swallowed the
  ladder's 1.0 and the band stopped short of the output foot. The two endpoints are the one pair of
  samples that are not negotiable.
- **Most of the visible band thickness was real and stays.** Two wires at the same loop height but
  different point counts are genuinely different arches: `LoopShape.Seed` spaces points uniformly in
  span, so unless `(points − 1) × peakSpan` is a whole number no vertex lands on the crest, and
  normalising the coarser polyline to measure the requested max-z-minus-min-z leaves the rest of its
  curve lower. A 7-point and a 9-point wire, both measuring exactly 20.0000 mil, differ by 1.84 mil
  above the chord at span 0.30 (11 points lands a vertex exactly on 0.30 and agrees with 9 to 0.003
  mil, which is the tell). Putting a vertex on `SeedPeakSpan` would collapse it — and would move
  shipped default geometry, so it was reported rather than done.

**Not in scope, and the name collision is most of why this concept confused the owner:**
`ProfileAxisSetting` / `ProfileProjection` are the profile **view** and its Auto/XZ/YZ plane, nothing
to do with the profile **object**. Untouched.

## The workspace toolbar's three panel toggles — Library, Properties, Messages (2026-08-18)

Owner: *"To the left of the messages button in the workspace toolbar, add buttons for Library Palette and
Properties Inspector… pressing the button again closes them… give the Messages button this same
functionality."*

**Nothing new was built for the toggle itself, on purpose.** `ToggleToolPanelCommand` already toggles
(press shows, press again hides, round again), already restores a panel to the place it was
last in *in this workspace*, and already gets the tricky cases right — a floating panel is hidden by closing
its window rather than by Dock's hide, a docked one is hidden rather than closed so its `ToolDock` cannot
collapse and force a rebuild, and the restored tab is made the front one so the next press reads as "hide"
instead of "bring forward". Every one of those was a separate owner report during the P/A work above. The
three buttons are that command with a different panel id.

What the work actually was:

- **The toolbar's own `ToggleMessagesRegion` command is deleted.** It only ever called
  `SetActiveDockable` — it could not close the panel, so a second press did nothing, and the button said
  nothing about whether Messages was on screen. Leaving it beside the toggle would have been a second,
  weaker way to show one panel.
- **The checked state is bound ONE WAY** to `IsLibraryPanelShowing` / `IsPropertiesPanelShowing` /
  `IsMessagesPanelShowing`, which are computed from the dock tree on every read and renotified from
  `RaiseToolPanelVisibilityChanged` — the notification every route that can change what is on screen already
  raises. A `ToggleButton` flips its own `IsChecked` on click; the binding's job is to *correct* that flip
  whenever the tree disagrees, which is why the notification is unconditional rather than change-gated. The
  layout editor's two wBond buttons push the same truth from code-behind instead, only because their
  `DataContext` is a `LayoutDocument` and not the workspace.
- **No local `Background` on the buttons** (`Classes="PanelToggle"`, chrome in `CircuitRfStyles.axaml`). A
  local value outranks a style setter, so `Background="Transparent"` written on the button — the way every
  plain button in that toolbar is written — would also beat the theme's `:checked` and `:pointerover` fills
  and the toggle would never look checked. The flat look is set from the style; the checked fill is left to
  the theme so it follows the active accent, and only the icon is re-coloured, because the
  `StackPanel.Toolbar mi|MaterialIcon` rule paints every toolbar icon 60% grey and that is unreadable on an
  accent fill. That rule and this one both match, so the checked one must come *after* it in the file.
- **A panel behind another tab now comes to the front** (owner, same day, on seeing it: *"if the
  Properties is tabbed behind Analyses, I want Properties to come to the front. This should be true to
  [any] window tool that is behind another pane"*). This reverses the two-state ruling of 2026-08-17 —
  and reverses it in the shared command, so the wBond P and A keys and the layout editor's two buttons
  follow it too. It is safe this time because `IsToolPanelShowing` changed with it: a panel behind
  another tab reports as NOT showing, so the raise is a visible state change rather than the press that
  "did nothing" which made the original middle state read as a three-press cycle. Every control bound to
  it still behaves as a two-state toggle. `BringToFront` sets the active dockable and, for a floating
  panel, activates its window — but never keyboard focus, or the next bare P or A would be typed into
  the panel's own field.
- **A visible Library with an unlit button, at launch** (owner, same day). Not a rendering problem — a
  missed notification, and the reversal above is what created it. The shell is BUILT from
  `DockLayoutDefaults.Default()`, where the Library is tabbed BEHIND the Project Tree and so genuinely is
  not in view; a moment later `ApplyWindowLayout` rebuilds it into the `ProjectTreeAndLibrary` preset,
  where the Library is alone in its own column and plainly is. Properties and Messages are the front tab
  of their group in *both*, which is exactly why only the Library was wrong. Two gaps, both fixed:
  `RebuildLayoutFrom` did not renotify (`ApplyDockLayout` always did, and the Window Layout preset and
  Reset Layout do not go through it), and **nothing at all renotified on a tab switch** — which since
  "showing" means "in view" changes the answer for two panels without any panel being added, removed or
  moved. `ActiveDockableChanged` is now subscribed for that, and deliberately NOT routed through
  `OnDockArrangementChanged`, which would arm a `.cws` write on every click. Both are pinned by tests
  that drive the real factory rather than reading the source.
- **Two restore gaps that only a default-layout panel could expose**: the `CaptureDockLayoutForPersistence`
  overwrite described under *"And the place survives a restart"* below, and `RestorePanelToItsHome`
  returning `false` the moment `_panelHomes` had no record. Returning was right while only the wBond panels
  could reach it — no layout names those — but for a panel the arrangement *does* place, the arrangement's
  own closed entry is a better answer than `ShowToolPanel`'s float, which drops a window over the canvas.
  It now falls back to that entry, and reopens it in place rather than adding a second entry naming the same
  panel (R-dock-1: the id is the identity).

## Schematic context menu ▸ Edit Parameters opened the inline editor (2026-08-17)

Owner: it "opens an inline text editor. It is supposed to open the Component Parameters dialog box."
`SchematicView.axaml.cs`; `tests/Ui.Tests/SchematicEditParametersMenuTests.cs` (3); `Ui.Tests` 7,730
passing, `Firewall.Tests` 6.

**A placeholder left from before the dialog existed** — its own comment said so ("Full ParameterDialog
deferred to a later phase"). It called `BeginInlineEdit` on the component's **first** parameter, which
is neither the dialog nor a choice the user made: a component with several parameters silently offered
exactly one of them, and one with `Parameters.Count == 0` returned early and did nothing at all. It
also had no version of the double-click path's VAR/MEAS branch, so Edit Parameters on a VAR opened a
one-line box over a multi-line equation block.

**Fixed by making the two entry points ONE implementation**, not by copying the dialog code into the
menu handler: `OnComponentDoubleTapped` and `OnCtxEditParameters` both call `OpenParameterEditorFor`.
Two copies is exactly how the menu came to answer differently from the double-click, and a second copy
would let it happen again.

**The test is a SOURCE scan, and it strips comments first.** View code-behind has no headless test host
in this project, so the technique is the one the Harmonica view tests already use. Stripping matters
here more than usual: this file's own comments now name the bug, the old method and the new one, so a
scan over raw text would pass on the prose alone. **Verified to fail against the old implementation**
rather than assumed — restoring the `BeginInlineEdit` body turns it red.

## The pin dialog needed Cancel pressed twice, and its labels were the odd size out (2026-08-17)

Two follow-ups on the pin port-number dialog. `SymbolEditorCanvas`, `SymbolEditorView`,
`AutoSymbolGenerator`; 12 new tests across `SymbolPinPortEditTests` and `AutoSymbolLabelStyleTests`;
`Ui.Tests` 7,727 passing, `Firewall.Tests` 6.

**Cancel needed two presses because the CANVAS still held the pointer capture.**
`SymbolEditorCanvas.OnPointerPressed` calls into the view model FIRST and captures the pointer AFTER —
and the view model's double-click handler is what asks the view to open the dialog. `ShowDialog` puts
the modal up synchronously before the first `await`, so by the time `e.Pointer.Capture(this)` runs the
dialog is already on screen; the release that would have freed that capture is delivered to the modal
instead, and the canvas keeps the pointer for the dialog's entire life. **The dialog's first click is
then spent breaking the capture rather than pressing the button under it.** Fixed on both ends: the
view POSTS the dialog at `DispatcherPriority.Background` so the whole press/release cycle drains first,
and `SymbolEditorCanvas.ReleasePointerCapture` covers what posting cannot — a user still holding the
button down when the post runs. The canvas now tracks the captured `IPointer` rather than trusting the
matching release to arrive.

**Ruled out first, and worth recording because it would have made the above pointless:** the other
explanation for "press it twice" is two identical modals stacked, which a capture fix does nothing
about. `ARealDoubleClick_RaisesTheRequestExactlyOnce` drives the real two-press sequence (ClickCount 1
then 2) and pins it at one raise. **`IsCancel="True"` is not the culprit** — in Avalonia it only listens
for Escape on the root; it does not close the window on click.

**Any canvas here that captures the pointer AFTER calling its view model has this shape**, because a VM
handler is free to raise something the view answers with a modal.

**The auto-generated symbol's port labels were the one dynamic symbol still on the old font size.**
`AutoSymbolGenerator` carried a private `FontSize = 12.0` while SnP, SDD and ZPort had all been raised
to `BuiltInSymbols.SddPortLabelFontSize` (18) — nothing held the two together, so the generator simply
kept the value the others moved away from. It now REFERENCES that constant. Two things had to change
with it: the labels were on `TextPrimitive`'s default `VAlign.Baseline`, which anchors the glyph's
baseline on the lead and so hangs the whole number ABOVE the stub it names (a small offset at 12, an
obvious one at 18) — SnP uses `Middle` and now so does this; and the inset moved from 5-inside-the-INNER
rect to 20-inside-the-OUTER, matching what `BuildSnpSymbol` insets its own labels from its body edge and
leaving real clearance rather than a glyph flush against the line. The clearance test asserts the
GEOMETRY, not the constants, so a later change to either inset is caught by the thing that matters.

## A placed cell instance with no symbol renders no pins (2026-08-17)

Owner, immediately after the ordinary-cell fix below: *"my schematic does not render the pins of the
&lt;cell&gt; instance after I performed an Update Schematic from Layout."* `CellPortCount`,
`LayoutToSchematicGenerator`, `WorkspaceViewModel.SchematicToLayout`, `SchematicViewModel`;
`tests/Ui.Tests/CellPortCountTests.cs` (8) plus 3 more in `LayoutToSchematicGeneratorTests`;
`Ui.Tests` 7,715 passing, `Firewall.Tests` 6.

**The instance was placed correctly — the cell it names simply has no `.csym`, so it draws as a bare
placeholder with no pins.** Two separate defects made that the outcome:

**1. "No primary symbol" is TWO different questions and the create path answered only one of them.**
The fix below warned and placed a placeholder, described at the time as matching the Library palette.
It does not: placeholder-and-warn is the palette's *other* branch (several symbols exist, none marked
primary). When a cell has NO symbol view at all, `SchematicViewModel.CommitCellPlacementAsync` **offers
to auto-generate one** — the same cell dropped from the palette gets a symbol and the same cell arriving
through Update Schematic from Layout got a blank box. The generator now separates the two states and
returns `CellsWithoutSymbols`; the command offers generation **once for the whole run** (it places many
instances; a prompt per cell would be a queue of dialogs where the palette has one click), and reports
the placeholder when declined or headless. Deliberately *not* offered for "symbols exist, none primary"
— which of several is primary is a question only the user can answer, and generating a further one
answers it by adding to the pile.

**2. `.ccell NumPorts` is a declaration nothing in circuitRF ever DERIVES — so it is routinely zero, and
the fallback was a hardcoded 2.** It is written by the Cell Parameter editor and the PDK installer and
by nothing else, so a cell whose schematic the user drew with N `Pin` components, and whose cell editor
they never opened, declares zero ports. Auto-generation then produced a **two-pin symbol for a
four-port cell** — which is the same bug the user would have hit next, and which **the palette path had
all along**. `CellPortCount.Resolve` reads the declaration when it says anything and otherwise counts
the cell's own primary schematic's `Pin` components; both call sites use it, so there is one rule.
The count is the **highest port number declared, not the number of pins** — pins numbered 1, 2, 4 mean
four ports with the third open, and answering three renumbers the user's port 4 into a port 3 that
connects somewhere else. Unnumbered pins are still ports, so the answer is never below the pin count.

**The generated symbol must go through `OnCellSymbolAutoGenerated`, not just `SymbolPersistence.Save`.**
The placed component was already rendered against the absence of that file, so without the resolver-cache
drop and the open-schematic rebuild it keeps drawing the placeholder — indistinguishable from the symbol
never having been created.

## Symbol editor: a detached inspector, and pin port-number editing (2026-08-17)

Owner: *"sometimes the Property Inspector does not update when I click on a Pin in the Symbol editor"*,
plus a request for a double-click-a-pin port-number dialog and *"make sure the text is properly
validated."* `tests/Ui.Tests/SymbolCanvasActivationTests.cs` (3) and `SymbolPinPortEditTests.cs` (31);
`Ui.Tests` 7,705 passing, `Firewall.Tests` 6.

**The "sometimes" is whether the Project Tree has been touched since, and this exact bug was already
found and fixed for the LAYOUT editor.** Every `PropertiesTool` context setter clears every *other*
context on its way past, so a project-tree file click routes the panel to a file inspector and calls
`SymbolInspectorVm.SetContext(null)` — detaching the inspector from the VM. The symbol document never
left `DocumentDock.ActiveDockable` (the tree is a different dock region), so
`OnDocumentDockPropertyChanged` never re-fires, and **clicking back into the canvas restores nothing**:
the VM registers every pin click perfectly well and the panel simply never hears about any of them. The
fix is `SymbolEditorDocument.CanvasInteracted`, raised by the view on **canvas** `GotFocus` — a
transcription of `LayoutDocument.CanvasInteracted` and `OnLayoutCanvasInteracted`, which carry the same
reasoning in their own comments. **Any dock document whose panel context can be hijacked by a different
dock region needs this;** the schematic editor is the remaining one that does not have it, and was left
alone rather than changed without a report against it.

**Found while writing the reproduction: `SymbolPrimitiveInspectorViewModel.SetEmpty` did not release the
pin latch.** `IsPinSelected`/`_lastPinIndex` survived it, and those two are exactly what `SetPinView`'s
`switching` test reads — so re-selecting the same pin after the panel had been emptied in between
computed "already showing" and skipped `HideAllGroups`. Invisible today only because the view hides the
whole field area behind `IsNotEmptyState`. Cleared now.

**The port dialog hit-tests pins BEFORE primitives, unlike the double-click branch it sits next to.** A
pin sits on top of the artwork it is attached to, so the pre-existing text-primitive double-click order
would let the artwork win every double-click aimed at a pin — the single-click path already hit-tests
pins first for this reason. **Select tool only:** under the Pin tool a click on empty canvas *places* a
pin, so a double-click there would place one and then open a dialog on it, and the two cannot be told
apart from the second press alone.

**Validation is a plain `TextBox` plus a framework-free `SymbolPinPortInput.Validate`, deliberately not
a `NumericUpDown`.** A spinner answers bad text by silently reverting it or going null — which reads as
the dialog ignoring what was typed — and a null reaching OK would have pushed a "changed to nothing"
through the undo stack. Digits only: parsing leniently and range-checking afterwards accepts `+2` and
`2.0` and quietly rounds `2.7`, and a port number is an ordinal. Overflow is reported as *too large*
rather than as *not a number* — same text, two different corrections. A number past the cell's declared
port count is a **note, not a refusal**: a symbol is routinely authored before its `.ccell` agrees, and
the canvas's unmapped-port overlay is what keeps that visible. `SetPinPortNumber` re-checks the lower
bound anyway, because the dialog is not the only caller and a validator that is also the only guard is
one refactor from being neither.

## Update Schematic from Layout skipped ordinary cell instances (2026-08-17)

Owner: *"I performed an Update Schematic from Layout, but my cell instance was not placed in the
schematic (even though it has a symbol)."* `LayoutToSchematicGenerator`,
`tests/Ui.Tests/LayoutToSchematicGeneratorTests.cs` (5 new cases); `Ui.Tests` 7,671 passing,
`Firewall.Tests` 6.

**The command only ever handled PCell-backed instances, and skipped everything else in SILENCE** —
one `continue` on `PCellOrigin is not { } origin` that neither created a component nor added a report
line, so the run posted its ordinary "0 added, 0 updated, 0 unchanged" success having ignored the only
instance in the layout. Nothing was broken; the ordinary hierarchical case was never written.

**Its stated reason was wrong, which is why this stayed unwritten so long.** The scope note argued
such an instance had "no existing symbol this command could safely fabricate". Nothing needs
fabricating: a schematic component names a cell in `EditableComponent.CellRef` and resolves that
cell's own primary `.csym` at render time — which is exactly what dropping the cell from the Library
palette already produces. The create path is therefore the *same* seeding
`SchematicViewModel.CommitCellPlacementAsync` performs (placeholder `SymbolKind.Generic`, the cell
reference, an `X` name, parameters from the cell's published `.ccell` interface), and the kit-part
helper already in this file **was** that code — it only had to stop being named after kits. A
component created from a layout and one dropped from the palette have to be the same component.

**A cell with no primary symbol is placed anyway, with a warning**, mirroring the palette drop's own
placeholder-and-warn. Refusing would have reinstated the original bug in a narrower form, and the
component is the thing that makes the missing symbol visible at all.

**Second, independent defect found on the way, latent since L5 on the PCell/kit path:
`SchematicEditModel.NextAvailableName` scans `schematic.Components`, and nothing in `Run` executes a
command** — the caller executes the whole chain once, as one undoable action (R-L5-12/22). So
`Components` still holds only what was there *before* the run, and every instance created in one pass
was handed the same name. Two new instances both became `X1`, the second `PlaceComponentCommand`
overwrote the first's identity, and both layout `SchematicId`s then pointed at one component. It needs
a run-scoped claim list feeding `NextAvailableName` alongside the existing components — shared by both
create paths, not local to the new one. **Any generator that queues `PlaceComponentCommand`s instead
of executing them has this shape**; a single-instance test cannot see it.

## wBond round 6 — the owner's thirteen (2026-08-17)

Owner bug/change list, no brief. `tests/Ui.Tests/WBondRound6Tests.cs` (24 new cases); full `Ui.Tests`
**7,600** passing, `Core.Tests` 1,361, `Firewall.Tests` 6. Only the findings worth keeping are below —
the rest of the list was ordinary wiring.

**The blank Group combo was a NOTIFICATION ORDER bug in the view, not a model bug — and it had two
independent halves, either of which reproduces it.** (1) `SetEmpty` left `GroupName` standing, so
clicking a wire in the *same group* as the last one re-assigned the value it already held; an
`[ObservableProperty]` raises nothing on an unchanged write, so the view was never told to put the
combo back after an item-list rebuild had dropped its selection. (2) `ItemsSource` was bound in XAML
while the *selection* was pushed from the code-behind's own `PropertyChanged` handler — and that
handler is subscribed in `OnDataContextChanged`, which runs **before** the DataContext reaches the
ComboBox and attaches its binding. So on an `AvailableGroups` change the selection was assigned first,
resolved against the OLD item list, and silently dropped (Avalonia clears a `SelectedItem` its items do
not contain). **The fix is to own both halves in code and assign items *then* selection.** Anywhere
else in this codebase that pushes a ComboBox selection from a VM notification has the same latent
shape.

**Delete and copy/paste of wires were missing in the LAYOUT host only, and for one structural
reason.** Both work in the standalone wBond editor because `WBondEditorView` has a tunnel key handler
and its own clipboard methods — an ancestor that *does not exist* in the ordinary Layout Editor, where
the wires reach the canvas through the `ILayoutCanvasOverlay` seam instead. Delete therefore belongs on
`WBondLayoutOverlay.OnKeyDown` (gated on a non-empty wire selection, so a Delete meant for a shape
still falls through), and the clipboard on `LayoutEditorView` reusing `WBondMixedClipboard` +
`WBondClipboardWriter` rather than a second implementation. `LayoutClipboard.PasteAsync` already
unwrapped the mixed envelope for its half — only the wire half had nowhere to go.

**A wBond cannot be dropped through the PCell path and never will be**, so the palette drop needed its
own route (`LayoutEditorViewModel.CommitWBondDrop`): it produces this session's *wire layer*, not a
shape, and `CanDropPaletteComponent` was right to refuse it. Two traps: the file must NOT be written at
drop time — attaching plus `MarkWiresDirty` lets the ordinary save write it, which is also what makes
the drop work on a scratch layout that has no path yet — and the drop must be refused when the canvas
is showing a **host's** wires (`_canvasOverlay` is not the frame's own `WireOverlay`), or the wBond
editor's reference layout gains a second wire design that is saved to disk and never drawn.

**The Layout Editor's two geometry-snap toggles never reached the wires, and the overlay had no
property that could have expressed the answer** (owner, 2026-08-17: *"geometry snap toggle is not
respected in the wBond layout host"*). Two halves. **(1)** `WBondLayoutOverlay.SnapEnabled` conflated
geometry snap with grid snap, while the Layout Editor keeps them separate — `S`/`F3` turns geometry
snap off and a rectangle still lands on the pitch in the Snap box. So `GeometrySnapEnabled` is now its
own property, gating only the geometry query, and `SnapEnabled` stays the master (off means the pointer
position, grid included). **(2)** Nothing in the layout host ever WROTE either one. The overlay's
defaults are permissive — snap on, and `includeIntersections` was a hard-coded `false` — so the toggles
governed every shape in the view except the wires, and Include Intersections silently did nothing for a
wire no matter what it said. Both are pushed by `PushLayoutSnapAndUnitToWires` now, on every change and
not only at attach, with a `NotifyChanged()` because the layout recomputes its own marker on the toggle
rather than waiting for the next pointer move (R-snp-7) — without it a stale snap glyph sits on screen
saying the toggle did nothing. **The general shape: a permissive default plus no writer reads exactly
like an ignored setting**, and it is invisible in review because both halves look fine alone. *Still
outstanding and NOT part of this fix:* the overlay's `SnapToleranceNm` is a fixed 1 mil, while the
layout's geometry-snap tolerance is a screen-pixel distance the canvas converts per event, so wire snap
reach does not change with zoom the way shape snap does.

**The panel arrangement is gated PER WORKSPACE, and its first gate — a per-user preference — was the
wrong scope** (owner, 2026-08-17: a new workspace, a new cell, a wBond, Update Layout from Schematic,
and *both panels floating*). It ran once per installation, recorded in `preferences.json` as
`wbond_panels_arranged`. **A panel's home is per-workspace; the flag was per-user**, so the second
workspace on a machine found the flag spent, fell through to plain `ShowToolPanel`, and that method's
only answer for a panel with nowhere remembered is to float it — which is the exact state the
arrangement exists to prevent. The flag could only ever have been right for the first workspace a
person opened. **The correct gate was already in the code one level down and per panel:**
`IsPlacedAnywhere` — this layout already names it, so open it and move nothing. That is self-limiting
in every workspace independently, needs nothing remembered between runs, and cannot tell a
someone-else's-workspace apart from a workspace the user arranged themselves *because there is nothing
to tell apart*: both name the panels, and both should be left alone. The preference is deleted, not
rescoped; a leftover key in an existing `preferences.json` is ignored on load and dropped on the next
save. **The general trap: a per-installation flag cannot gate per-document state, and it fails silently
on the second document rather than the first.**

**The first-use panel arrangement transcribes the owner's `.cws` with ONE number deliberately not
transcribed.** That file records `Sides: {Left, 0.8, Inboard: true}` for the Array Inductance column;
0.8 is the proportion of the container holding the column *and* the documents, not the column's own, so
restoring it opens the panel across four fifths of the window. The panel's own `Proportion` (0.1886) is
the number that means "this column's share of the document row" for a column holding one tool dock —
`DockLayoutCapture` says so where it falls back to the tool dock's proportion for exactly this shape.

**The colour-theme half was done twice, and the second answer is the right one.** Making
`wBond-Orchid` the *default theme* left the winning palette as a selectable thing sitting beside
`Default` — so the owner's follow-up was to fold its six roles into `Default` itself and delete the
file. Two copies had to be updated, not one: `Assets/Color/Default.ccolor` (what the Settings editor
shows) **and** `ColorTheme.BuiltIn` (the per-role fallback for anything a theme leaves unsaid) — and
Default.ccolor stated **no** `wBond.*` role at all before this, so the colours actually in force came
from the in-code copy. A test now holds the two together.

That fold also exposed **two tests measuring the same property with different metrics**:
`TheVertexRole_IsADistinctAccentInBothVariants` used a Manhattan sum > 150 while round 4's palette test
used Euclidean > 60. The owner's dark pair (wire 214,122,182 / vertex 142,122,255) measures 145 Manhattan
and 102 Euclidean — it failed one and passed the other while plainly being a blue-violet dot on a pink
wire. Unified on the Euclidean bound rather than tuning a threshold.

**Changing the theme did not repaint the wires**, because a theme change is TWO events: the view already
handled `ActualThemeVariantChanged` (light vs dark) and nothing handled `ThemeService.ThemeChanged` (a
different theme selected). `LayoutCanvas` invalidates itself on the second, but it redraws the overlay
from the `WBondRenderTheme` the HOST handed it — a plain object with no notifications — so the layout
repainted underneath wires still in the old colours.

**Alt-drag scaled every wire in the design because a `LoopProfile` was used as a proxy for the
ARRAY.** `ScaleSelection` resolved the selected wire's `ProfileBinding` and called
`WireEdits.ScaleBoundWires`, which rescales every wire following that profile *and writes the profile's
own height*. A profile is an editor-internal sharing mechanism the user never sees (O-10) and it
routinely spans every array — the shipped default creates ONE and every array references it — so a drag
in G1 moved G2 as well.

**Two owner reports, one rule, and the second corrected the first.** "It should only be the wires that
are selected" → selection-only; then "it needs to change ALL the wires in the group at once" → the unit
is the ARRAY. `ScaleSelection` now takes `wholeArray`, expanded through `WireMesh.ArrayOfWire` (the same
mapping `SelectArray` uses, so "the group" means one thing in the class). **The two views pass opposite
values, deliberately:** the PROFILE view promotes to the group — it draws a group as one superimposed
shape under one envelope band, and a bond group is one loop program on one bonder — while the LAYOUT
view does not, because there each wire is drawn at its own place among the pads and an alt-drag stretches
THAT wire onto THAT pad.

Either way **nothing writes the profile any more**, which is the same correction `ControllingParameters`
had taken hours earlier (*"its setting should never affect the geometry that the user authors"*). Safe
without a detach because the envelope band is computed from the bound WIRES, not from the profile's
nominal shape. `ScaleProfileHeight`/`ScaleProfileSpan` still exist for a profile-CURVE gesture and are
reached by no gesture today; both now say so.

**The wire tool in the Layout host needed a HOME for its armed state, and the session is it.** The
overlay has had `WireDrawArmed` since WB-C; only the wBond editor ever set it, through its own
`WBondTool` enum. In the Layout Editor two things have to agree with it — the toolbar's toggle and
Escape — so it is a property on `LayoutEditorViewModel`. The trap worth knowing: **the two tool sets
must be mutually exclusive in BOTH directions**, because the overlay gives an armed LAYOUT tool first
refusal on every press (`LayoutToolArmed`), so a wire tool armed alongside Rectangle would look armed
and never see a click. Escape is two-stage by what is IN PROGRESS, never a counter: disarm first, then
clear all three selections (shapes, instances and the wires, which `SetSelection` cannot reach).

**Pasting wires into a layout that had none dropped them silently, because the paste path required a
wire editor to already exist.** That is EVERY ordinary layout — a cell only gains one once something
has put wires in it — so a mixed copy pasted into a fresh `.clay` delivered the geometry and lost the
wires (owner, 2026-08-17). `EnsureWireLayer` now creates the layer on demand, with an EMPTY design (the
caller is about to add what it carries; a default wire would be a spare nobody asked for), marks the
session dirty so the sidecar is written, and reports that the layout has become a wirebond cell. Guarded
against the wBond editor's HOSTED layout for the same reason the palette drop is: a wire layer created
there would be a second, invisible design on the reference layout.

**Both menu entry points to the standalone wBond window are COMMENTED OUT for v1, not deleted** (owner,
2026-08-17): Tools ▸ wBond first, then File ▸ Open ▸ Open wBond… — the standalone wirebond window is a
v2 feature and the second item was the other way in, so leaving it would have made the withdrawal a
half-measure. `NewWBondCommand`, `OpenWBondFileCommand`, `WBondDocument` and the whole editor stay; only
the way in is deferred. Both are commented out on BOTH hand-mirrored surfaces (the macOS `NativeMenu`
and the in-window `Menu`). **The test asserts on the COMMAND BINDING, not on the header**
(`FileMenuRestructureTests.NeitherEntryPointToTheStandaloneWBondWindow_IsWiredUp…`) — an exact-order
list would still pass if the item returned under another spelling or in another submenu, and a header
scan would misfire on File ▸ Import ▸ **Wirebond Wires…** / **Wirebond as Cell…** the day someone
respells those, which are the COMPONENT path and must keep working. `XDocument` parses a comment as
`XComment`, never an element or attribute, so a commented-out item is genuinely invisible to that test.

**Carried/Linked is the LAST row of the wBond parameter panel** (owner, 2026-08-17: "an advanced
feature that only wBond experts would use"). It sat third, under the design summary, which put the
panel's most consequential and least-used control in front of the arrays, the per-array overrides and
the artwork rows. **The ordinary flow never sets it:** a placed wBond is Carried by construction, and
Update Layout from Schematic is what flips it to Linked and announces it (WB45a, `WBondCellSeeding`) —
the box exists to go BACK to Carried for a schematic that must travel alone, and to repair a link.
Moved within the `IsWBond` panel, not below the generic rows underneath it, which are last by the
owner's own earlier decision; a wBond's only generic row is `Temp`. The consequence note stays directly
beneath its own box. One consequence to know: a **linked file that has gone missing** is named on that
note, which is now at the bottom of a scrolling panel — it is still refused BY NAME at the next Run
(`NetExtractor`), so the loud report is unaffected, but the quiet one moved down with the control.

**A third door is deliberately still open: double-clicking a `.wBond` in the Project Tree**
(`WorkspaceViewModel`'s `NodeKind.WBondFile` → `OpenWBondPath`). Not removed, because it is how a user
opens a file they can see rather than a menu that advertises a feature — and disabling a double-click
was not the ask. Worth knowing when judging "is the standalone reachable": it is, from there.

**The standalone has drifted from the in-layout wire tools, and the cause is ONE fact, not many missed
fixes** (owner, 2026-08-17; deferred to v2 by the same decision). Wires live in two different places.
Standalone: on `WBondDocumentViewModel.Editor`, reaching the canvas as a HOST overlay through
`LayoutEditorView.CanvasOverlay`, with the hosted `LayoutEditorViewModel.WireEditor` **null**. WB40:
on the layout view model itself. Every wire feature added on the WB40 side gates on that null —
`HasWireDesign` (the `W` key, `Alt+R`, all six toolbar buttons), `WireEditorWithSelection`
(copy/cut/paste), `vm.WireEditor` (the Delete routing and the layer-on-paste, which explicitly refuses
the host case) — so they are all off in the standalone by construction. **The marquee is the sharpest
example and it is not missing code at all:** the companion marquee that picks up wires during a shape
drag only runs where `WireMarqueeEnabled` is FALSE, which is WB40's setting; the standalone sets it
TRUE from its own toolbar toggle, making marquee an XOR — wires or shapes, never both. The fix, when v2
comes, is to attach the document's editor TO the hosted layout view model rather than pushing an
overlay past it; the cost is in WB27's push-into-sub-cell (which is why the host overlay outranks the
frame's) and in retiring one of the two `EditSequence` undo reconcilers. What keeps it fixed is a
capability-parity fixture asserting both surfaces answer the same, plus a single overlay construction
site — without those it re-drifts.

**Shift on the angle-wire tool snaps the wire's ABSOLUTE bearing, in 15° steps.** Relative increments
were the bug: a wire at 7° could only reach 22°, 37°, 52° — every one as crooked as it started, and
0/90/180/270 unreachable by the gesture that exists to reach it. 15° rather than 45° (owner) gives up
nothing, since 15 divides 45, 90 and 180, and it reaches the fan-out angles (30°, 60°) that a coarser
grid cannot express at all.

**Two of the three angle-wire reports were ONE defect, and it was in the pivot.** `RotateFrame`
measured the swing about `Selection.TouchedWires().First()` — and that is a HASH SET, so "first" is an
arbitrary member: with several wires selected, the wire under the cursor turned by an angle computed
about some other wire's foot. Measured before the fix, a quarter turn asked for on the grabbed wire came
out about a third of one, which reads as both "it rotates about the wrong point" and "sometimes it does
not rotate". The grabbed wire index is now captured at `BeginRotate` and used for the pivot and the
angle.

**The disabled "Straighten Wire" was a STRANDED GESTURE, and the mechanism is worth knowing because it
can lose geometry.** While a drag runs, `WBondPointerController` may collapse a wire to its two feet
(the quality ladder's cheapest rung) and restore the interior points at `EndDrag`. A drag whose release
went elsewhere — the pointer left the window, a toolbar button took focus — leaves the wire **collapsed**:
it draws as a straight line, `WhyCannotStraighten` correctly reports that it has only its two feet, and
the next `BeginDrag` clears the record of what was collapsed, at which point **the loop is gone for
good**. The owner's "close the menu and reopen it" worked because the mouse movement delivered the move
that unwound the gesture. Both `OnFocusLost` and `BuildContextMenuItems` now unwind explicitly.

**Alt-drag ignored the snap setting because the thing being snapped has to be the TARGET VALUE, not
the cursor or the factor.** A snapped cursor still leaves an arbitrary span whenever the wire started
off-grid, and a snapped ratio means nothing — so both alt-drags now quantise the span (and, in the
profile view, the loop height) itself, with a floor of one pitch so a big shrinking drag cannot round to
a zero-length chord. **Alt deliberately does NOT suppress this snap**, though R-snp-11 makes it suppress
every other one: Alt is what SELECTS the gesture, so letting it also mean "ignore the grid" would leave
no way to ask for a snapped scale at all. Note "span" is the 3D CHORD everywhere in this app (it is what
the Properties panel shows and what `ScaleSpan` scales along), so on a descending wire it is longer than
the XY footprint — a test asserting an x extent needs level feet or it measures the wrong thing.

**The stale snap glyph during an alt-drag was the SnapMarker freeze, one gesture further along.**
`AltDragFrame` returns before `SnapPoint` is ever called, so the marker from the previous gesture was
neither refreshed nor cleared while `_dragging` stayed true — exactly the failure
`ILayoutCanvasOverlay.SnapMarker` was introduced to prevent. An alt-drag scales and places no point, so
it publishes nothing at all now.

**The Del key could not delete a segment because it never read the selection.** It called
`DeleteSelectedWires` unconditionally, so the finest thing it could remove was a whole wire — while the
context menu, one right-click away, removed exactly the segment that was picked. `DeleteSelection` now
dispatches: whole wires whole, segments and vertices as points (descending order, since every index
past a removed one shifts), one undo entry, and a refusal by name rather than silently leaving a wire
at two points. **Add Vertex** and **Straighten Wire** joined the wire menu with it — the first inserts a
point that changes the shape not at all (one `Lerp` gives both "collinear with its neighbours" and
"interpolated z"), the second removes lateral bow in XY with the feet anchored — and with several wires selected it
straightens all of them, each about its OWN feet (a shared chord would collapse a fan-out onto one
line). Straighten is the only item on that menu that reads the selection, and only when there is more
than one wire in it: one selected wire must never redirect a right-click aimed at a different one. Add Vertex is on the
PROFILE menu too and Straighten is not: that view's horizontal axis IS position along the wire's path,
so there is no XY plane there to straighten in.

**Rotating a wirebond cell needed TWO gestures kept apart, and the interesting part was the undo.**
`R` turns the selection 90° as one rigid body — wires included, in the same pivot and the same
`CompositeCommand` as the shapes and instances. The shared pivot keeps a wire on its pad; the single
command is what stops one Ctrl+Z putting the pad back and leaving the wire turned. That needed a wire
primitive that pushes NO entry on the wire stack (`MapWirePointsXy`) plus a layout-stack command that
snapshots the points (`TransformWiresCommand` — the inverse of a mirror composed with a re-centring
translation drifts a DBU per undo; a snapshot cannot). `Alt+R` arms WB26a's swing-about-the-far-end,
which was **already fully implemented on the overlay** and reachable only from the wBond editor's tool
enum — the owner guessed exactly that. `T` opens the wBond editor's own transform dialog on the same
wire editor.

**Wire z-height became a setting because the two creation paths had quietly disagreed.** A wire drawn
in the layout view landed at z = 0 (that view has no z axis, so the overlay's `FootZNm` had nothing to
read) while a new component's wires sat at 4 mil. The owner's call — *"being consistent is more
important than being right, and we can't guess what height the user wants"* — is now one preference
feeding five creation paths through `WBondDefaults.FootZNm`. Two traps: **zero is a VALUE here**, not
"unset" (a foot on the reference plane; negative is a cavity foot), so the `is > 0` guard the other
wBond defaults use would silently discard it; and `src/WBond` cannot read a preference at all, so
`DefaultDesign` takes the z as a parameter and every UI caller passes the resolver. `DefaultPayload` —
the registry's cached default `Design` string — cannot honour a preference either, which is why
`WBondPlacement.BuildCarrying` now writes the payload itself.

**Deleting the last wire un-makes a wirebond cell, and the undo question has one answer: put the file
deletion on the SAVE.** Owner: the schematic must lose its wBond component, the `.wBond` should go, and
undo/redo must still work. The component half is `DeleteCommand` — the schematic's own, which restores
at the original list index, so undo needs no inverse of its own. The FILE half deliberately does not
happen at delete time or at Update-Schematic time: `SaveWireDesignIfDirty` writes the sidecar when the
design has wires and **deletes it when it has none**, so the file goes on mirroring the SAVED state and
nothing is detached. The session keeps its `WireEditor`, its overlay and its wire undo history — Ctrl+Z
brings the wires back in memory, marks it dirty, and the next save re-creates the file. An empty
`.wBond` would otherwise leave the cell a wirebond cell for ever, because `WBondCell` resolves wires by
the file's PRESENCE.

**`GroundPlane` left the generic expression rows, and that is a real trade, not a tidy-up.** It is read
as a boolean (`ComponentModelFactory.IsTrue`), so a text box offered three usable values and infinite
unusable ones with nothing saying which — typing "yes" silently *disabled* the plane. It is a picker
now, which costs the ability to type a `VAR` in it (WB44 property 4). Acceptable only because a ground
plane is not a sweepable quantity; `Temp` and the loop heights stay generic rows for that exact reason.
A value the picker does not offer is appended to its item list rather than rewritten.

## WB-G — controlling parameters on the schematic symbol, and Carried-or-Linked (2026-08-17)

`brief-wbond-controlling-parameters.md`. Design authority: `wbond.md` §5.5.1 (WB44/WB44a), §9.7
(WB45/WB45a), owner decisions O-10/O-11/O-12. Gates 1–8 all green;
`tests/Ui.Tests/WBondControllingParametersTests.cs` (27) and six new cases in
`WBondParameterPanelTests`. Full `Ui.Tests` 7,558 · `Core.Tests` 1,361 · `Engine.Tests` 1,195 ·
`Firewall.Tests` 6, all passing.

**The engine half was already there and the brief was right to say so.** `CreateWBondModel` honoured
`Temp`, `GroundPlane`, `LoopHeight` and `LoopHeight_<profile>`; `ApplyLoopHeightOverrides` already
regenerated bound wires so **L** was refilled from new geometry rather than scaled. What was missing was
entirely on the schematic side — `ComponentTypeRegistry.DefaultParameters` declared none of them, so the
user could not select them and could not type them either. Extending that method rather than replacing it
is what kept gate 1 cheap.

**Gate 2's numbers land exactly on the existing M4 gate's**: `LoopHeight` 10 mil → **1086.2 pH**,
45 mil → **2206.7 pH**, driven through the placed-component path from the new `LoopHeight` parameter
rather than a hand-added one. Gate 4: `Diameter` 0.7 mil → 1544.0 pH against 2.0 mil → 1245.0 pH;
`Material` Gold → 439.138 mΩ against Aluminium → 465.309 mΩ, cross-checked against `InternalImpedance`
at 5 GHz with `QParameter > 3` asserted so the R-tier is provably skin-effect-active rather than a DC
comparison wearing a 5 GHz label.

**Gate 1 moved nothing.** A placed wBond with the parameters declared-but-blank produces
**bit-identical** S-parameters to the same schematic with them removed entirely — asserted with no
tolerance, because a tolerance is exactly where a leaked default would hide. Two things make that safe
and both were already in place: `NetExtractor` drops a blank-valued wBond parameter rather than emitting
it (the empty-parameter-value trap in `src/Core/CLAUDE.md`), and the elaborator's own `catch` skips an
unresolvable override.

**§2.1's clone-on-write WAS needed in practice — the data model does not prevent it.** The brief asks
this be recorded either way. `WireArray.Profile` and `Wire.ProfileBinding` are both plain names into one
shared `design.Profiles` list, and `MakeDesign`-style fixtures (and the real editor) routinely put two
arrays on ONE `LoopProfile` — which is the whole point of a profile. Without the clone, `LoopHeight_G1`
mutates the object G2's wires are also generated from, and G2 regenerates to a height nobody asked for
with every number still finite. Gate 3's oracle is therefore **G2's z-coordinates**, not its measured
loop height and not a message.

**The clone is scoped to a per-ARRAY override only.** A global `LoopHeight` sets every array to the same
value, so there is nothing for a shared profile to be dragged away from; cloning there would work and
would leave the decoded design carrying one profile per array for no reason.

**Found while implementing, not in the brief: the legacy profile spelling had to gain its own
regeneration.** The old code regenerated wires if *any* `LoopHeight_` key existed at all. Restructuring
around the array scope broke that for `LoopHeight_<profile>` — the profile's `LoopHeightNm` was set and
no wire was rewritten, so the design stated one height and measured another, and the solver reads the
wires. Caught by the test written for O-10's fall-through rule, not by review. A profile touched only by
the legacy spelling now triggers regeneration of every array bound to it.

**§2.0's precedence table is the part with no prior specification, and it is resolved by REPORTING.**
Three different precedences live under one parameter, because `ApplyLoopHeightOverrides` regenerates
between a wire's own existing feet and skips a wire whose `ProfileBinding` is null (WB2/WB24 — an
individually-dragged wire detaches):

| the layout edit | with `LoopHeight_G1` also set |
|---|---|
| moved a foot (XY or z) | **layout wins** — the override never moves feet |
| dragged the loop of a **bound** wire | **schematic wins** — regenerated at solve time |
| dragged the loop of a **detached** wire | **layout wins** — silently skipped |

Rows two and three are both silent and point in opposite directions, from one design. Making the
override touch detached wires would fix the asymmetry by breaking WB2, which is load-bearing for the
whole editor — so `ReportDetached` names the count and the remedy instead. Gate 3a asserts **both** the
z-coordinates and the message, because the message alone passes while the geometry is wrong and the
geometry alone is the silent behaviour the gate exists to prevent.

**Diameter and material deliberately DO reach a detached wire**, and that asymmetry is the point rather
than an oversight: detachment is about the loop *shape*. A wire dragged loose still has a diameter and a
metal, and there is nothing to regenerate to apply them.

**`Material` is refused by name, per §5's proposal** — validated against the decoded `design.Materials`
(user-extensible) rather than the built-in four, so a design's own metal stays nameable from the
schematic while a typo does not silently fall back to gold.

**The warning channel wBond needed did not exist.** `WBondModel` now implements `IReportsWarnings`. The
notes are built in the factory (which has the parameters) and **phrased at the first `Stamp`** (which is
the first moment an `ElaboratedComponent`, and therefore the instance path, is in hand) — a model does
not know its own name, and a message without one is not actionable.

### WB45 — Carried or Linked

`Source` (Carried/Linked) and `File` are declared on the component; `WBondPlacement` owns the axis.
**Carried by construction** for anything that does not say, which covers every schematic written before
this phase.

- **The netlist names exactly one source.** `NetExtractor` emits `Design` for a carried instance and
  `File` + `Arrays` for a linked one, dropping the payload. The payload stays on the *component*
  regardless — it is what draws the symbol, and §3.3 is explicit that retiring it is not in scope.
- **Relative in the DOCUMENT, absolute in the NETLIST.** The stored value is relative to the schematic
  (`../layout/<cell>.wBond`), exactly as `workspace-and-project-tree.md` §4 resolves a cell reference;
  the extractor resolves it, because that is where `SchematicDirectory` is known and the netlist is a
  generated intermediate written wherever the run writes it. Gate 6 moves the whole cell folder and
  asserts the answer is unchanged to 12 places.
- **WB45a: the flip lives in `WBondCellSeeding.Seed` and only on `Created`.** A `Carried` instance whose
  cell already has a `.wBond` is a legitimate state — someone who kept the portable payload — and is not
  auto-converted. A flip on a later scan noticing the file exists would change which wires simulate with
  nothing on screen.
- **§3.2's drift check runs at ELABORATION**, in the factory, against the `Arrays` record the netlist now
  forwards for a linked instance. Worth knowing: that record is written as `G1|G2`, and gate 7's
  end-to-end case exists specifically to pin that the `|` survives the `.cnl` round trip — a blank record
  is (correctly) read as "nothing is known about what this was wired against", so a regression there
  would silently retire the check rather than fail loudly.

### Not built, on purpose

- **`Span`** — WB44a/O-11. Not a profile property but the pad positions; scales by factor not to a value,
  and moves a bonded foot off its pad. Needs a pinned-foot rule and §8 envelope reporting, neither of
  which exists.
- **Retiring the `Design` payload** (§3.3). WB45 is *both*, chosen per instance.
- The panel does not offer `Linked` when nothing is linked — the box snaps back and the note says why.
  §5's second owner question (should `Linked` be offered for a `.wBond` outside the workspace?) is
  **unanswered**: nothing here forbids it, and `LinkTo` will store a relative path to anywhere, but no
  UI offers a picker outside the seeding path.

### Two owner reports the same day, and they are the same shape

**Report 1: three arrays set to 30/20/15 mil all arrived in the layout at 20 mil.** 20 mil is
`WBondEmbedding.DefaultWire.LoopHeightMils` — the drawn default. `WBondCellSeeding` wrote the raw
`Design` payload, because the override layer had only ever been applied on the way to the *solver*.
**Update Layout from Schematic writes what the schematic asks for**, so the geometry moved out of
`ComponentModelFactory` into **`src/WBond/ControllingParameters.cs`**, with a `WBondOverrides` input
(lengths already in metres, names as strings) that both callers reduce to. A second copy in `src/Ui`
would have been a second set of clone-on-write and detached-wire rules to keep in step.

**Applying a controlling parameter twice is the identity, and the fix depends on it.** Seeding bakes
the value into the file and then flips the instance to `Linked`, so the next Run reads the baked file
and applies the same parameter again. That is only safe because every one of them sets an ABSOLUTE
value — a height, a diameter, a metal — never a delta or a factor. **This is the same property that
made `Span` (which scales by FACTOR, WB24c) the one of the six that had to be deferred**, so the gate
is worth keeping if Span is ever revisited. The parameters are deliberately NOT cleared off the
instance afterwards: that would be an edit-on-write, would break WB44 property 1, and would silently
retire the handle a sweep turns.

**A `VAR` reference cannot be baked, and that is principled rather than a shortfall.** It is the whole
point of these being sweepable, and it is exactly why it has no single value to draw. Those wires are
written as drawn and the parameter is named. There is no scope to resolve one against on this path
either — the command runs on a schematic that need not elaborate at all.

**The panel's own summary had the same defect one surface over** and was fixed with it: total wire
length moves with loop height, so a component carrying a 45 mil override over 20 mil wires reported a
length no run ever uses. It now describes the effective geometry and appends "· with overrides".
**It decodes a SECOND design to do it**, and only when an override is actually set — the caller's
design goes on to build the array rows, and the write-back paths (`AddWBondArray` and friends) each
decode their own. Applying an override on a path that writes back would bake it into the payload.

**Report 2: a loop-height change in the layout never reached the schematic, "even if I use Update
Schematic from Layout".** `LayoutToSchematicGenerator` walks a layout's `LayoutInstance`s and had **no
knowledge of the wire layer whatsoever** — and no wire is ever a `LayoutInstance` (WB23: no wire enters
a `.clay`). §9.6 specifies this reconcile and **two shipped messages already named the command as the
remedy**, including `WBondCellSeeding`'s own. The command existed; the half that handles wires did not.
`WBondSchematicReconcile` is that half, called from `UpdateSchematicFromLayout` after the instance walk,
reading the **live** `LayoutEditorViewModel.WireDesign` so an unsaved wire edit reconciles too.

**Linking buys GEOMETRY, not the array list — and that is why the deletion case is the important one.**
A placed wBond's **pins come from its carried payload** (`WBondSymbolProvider`), so deleting an array in
the layout leaves the symbol still showing that array's two terminals, still wired to whatever the user
connected them to, while the model behind it has one branch fewer. Under `Carried` this command is how a
layout edit reaches the simulation at all; under `Linked` it is how a layout edit reaches the *symbol*.
Neither is optional.

**The Source note was overselling and the owner caught it.** It said a wire edited in the layout "no
longer needs bringing back into the schematic" — true of geometry, false of the array list. Both the
seeding message and the panel's own note now say which half is which, and name the command for the
other half. **Whenever a note claims something no longer needs doing, the claim needs the same scoping
the mechanism has.**

### Label ORDER on the symbol, and two orphan cases it exposed

**Report 3 (owner, 2026-08-17): "G1, G2, G3 in the dialog render as `LoopHeight_G2`, `LoopHeight_G1`,
`LoopHeight_G3` on the symbol."** Labels are built by walking `Parameters` in list order
(`EditableComponent.BuildRenderModel`), and a per-array override is **appended** when its box is first
committed — so the on-symbol order was the order the user's focus happened to visit the boxes in.
Nothing made it wrong; nothing made it right either.

**Sorted at the SOURCE, not at render time** (`WBondPlacement.InCanonicalOrder`): registry-declared
names in registry order, then one group per array in ARRAY order — which is pin order, and is what the
dialog lists — each group being loop height, diameter, material, then anything unrecognised keeping its
relative position. Sorting the list itself is what makes the dialog, the symbol, the `.csch` and the
netlist all agree; re-sorting on the way out would leave three of them disagreeing. The array order is
read from the **`Arrays` record**, not by decoding the payload — it is the same string
`WBondSymbolProvider.RefFor` generates pin order from, so the two cannot drift, and it costs no base64
decode on a path that runs per keystroke-commit.

**Writing this exposed two orphan cases, both of which draw a wrong label rather than merely an untidy
one** (`ReconcilePerArrayParameters`):
- **Renaming an array** left `LoopHeight_G2` behind. The override stops reaching anything — silently —
  while its value is still in the dialog and still drawn on the symbol.
- **Deleting an array** (from the panel, or from the layout via the reconcile) left its override behind,
  drawing a label for a pin pair the symbol no longer has. The layout-side case was caught by the
  render-model test, not by review.

**On the redraw half of that report:** the chain `SetParametersCommand.Execute` → `NotifyChanged` →
`SchematicViewModel.RebuildRenderModel` is now gated directly, asserting the symbol's **pin count** and
its **labels** off `vm.RenderModel` after the reconcile executes. Nothing below the view model is
reachable from a test, so if the on-screen canvas still fails to repaint, the cause is the view's own
invalidation and not this path — worth knowing before anyone re-investigates the model side.

**Still open, deliberately not decided here:** after Update Schematic from Layout the payload holds the
layout's geometry *and* the controlling parameters are still set, so the next Run re-applies them on
top. That is §2.0's row-two precedence ("schematic wins") behaving as specified, but it arrives
confusingly right after a command whose whole purpose was to make the schematic match the layout —
change the loop height in the layout, reconcile, and the schematic override silently forces it back.
Clearing the overrides would fix that and would destroy a `VAR`-bound sweep handle, so it needs an
owner decision rather than a default.

### Report 4: a re-run of Update Layout dropped an array added since — WB41 was too broad

**Owner, 2026-08-17: "Update Layout from Schematic, add another array in Component Parameters, Update
Layout again — the new array does not show up in the layout."** The sidecar was created ONCE and
thereafter left *entirely* alone. WB41's rule — *a re-run never overwrites wires the user has moved* —
is what that was protecting, and **it is right about existing arrays and was wrong about a new one.**
Adding an array touches no wire that is already drawn, so refusing to add it protected nothing and
silently dropped the thing the command had just been asked to do.

`MergeIntoExisting` narrows the rule to what it was actually defending: **existing wires are never
regenerated, re-pointed or moved; a missing array is appended with the wires the schematic draws for
it.** New outcome `Merged`, distinct from `Created` because the WB45a flip to `Linked` belongs on a
first write only — a merge changes what is *drawn*, never which of the two sources the next Run reads.

**Array ORDER is realigned to the schematic's, and that is not cosmetic.** Under `Linked` the model's
terminals come from the file while the symbol's pins come from the payload, so leaving the two lists in
different orders wires every array to the wrong branch. Reordering moves no wire in space — it only
realigns the two lists — which is what makes it safe to do unasked.

**Two things deliberately still not done**, each a refusal rather than an omission:
- **An existing array's geometry is never rewritten.** Those wires may have been dragged onto real pads,
  and a schematic-side loop-height change already reaches the SOLVER as an override without overwriting
  the drawing. Re-baking here would undo layout work to change a number that has already taken effect.
- **An array the schematic no longer has is kept, not deleted.** Deleting is the one direction that
  destroys drawn work irrecoverably, and the array may have been removed from the component by accident.

**The old message named the wrong remedy, and in the dangerous direction.** It said *"use Update
Schematic from Layout to bring them back into the component, or delete the file to re-seed it"* — advice
that told a user who had just ADDED an array on the schematic to pull the layout back over it, i.e. to
throw away the array they had come there to add. Each direction now names the remedy that matches it.

**A test asserted the old behaviour and had to be inverted** (`WBondRound5Tests`,
`AnArrayListThatDiverged_IsReportedAsDrift` → `AnArrayAddedOnTheSchematic_IsMergedIntoTheSidecar`). Worth
knowing that the previous contract was deliberate and gated, not an oversight — what changed is which
half of "the array list diverged" is resolvable.

**…and that fix was still not visible, because it went through the FILE while an editor was open.**
The owner came back with the workspace attached: the `.wBond` on disk held G1 *and* G2 with real
distinct geometry, while the layout on screen showed only G1. **Reading the artifact settled in two
commands what four rounds of reasoning had not** — the merge was demonstrably working and the bug was
somewhere else entirely.

An open `LayoutEditorViewModel` **holds its own `WBondDesign` object and mutates it in place** (that is
`AttachWireDesign`'s explicit contract — "the design object itself, not a copy"). So writing the file
underneath it changed nothing on screen. **And it was worse than a stale view: the live design still
held G1 alone, and the layout's own save path writes that object back — the next save of the layout
would have silently deleted the array the merge had just added.** Reading and writing a document's file
behind a live editor is not a display bug, it is a lost-edit bug.

`Seed` now takes the live design; when it is there it is the authority, the merge mutates *it*, and the
file is left for that editor to write — dirty until saved, exactly as after any other edit to an open
document. `LayoutEditorViewModel.NotifyWireDesignChangedExternally` rebuilds what depends on the
design's structure. **A re-attach would have been the wrong hammer**: it builds a whole new
`WBondViewModel`, discarding the wire undo history and handing the view a different overlay to bind to,
for an edit that only appends arrays. **The wire selection IS cleared**, and not for tidiness — a
selection is a set of flat indices across the whole design, and realigning array order moves every
one of them, so a surviving selection would point at different wires than the user picked
(`WBondViewModel.Restore` clears it on a structural undo for exactly this reason).

**The generalisable rule: any command that edits a document's file must ask whether that document is
open, and go through the session if it is.** This is the second time in this phase — `WBondSchematicReconcile`
reads `layoutVm.WireDesign` rather than the `.wBond` for the same reason, and that one was got right
first time only because the reconcile direction made the live object the obvious source.

### Report 5, and the owner decision that changed the answer: a loop height RESCALES a wire, it does not regenerate one

**"I changed the G1 loop height to 10 mil in schematic, then did an Update Layout from Schematic, but
the loop height still looks like it's 20 mil."** Confirmed straight from the attached workspace:
`LoopHeight_G1 = '10' mil` on the component, both wires at 508000 nm (20 mil) in the layout, G1 still
bound to profile `ball`. A re-run applied the controlling parameters only to arrays it was ADDING — the
refusal recorded one round earlier as deliberate.

**Then, mid-fix: "I don't like this ball/wedge profile thing. It doesn't offer the user anything. Its
setting should never affect the geometry that the user authors."** That is what settled the shape of
the fix rather than merely its existence.

**The old application went through `LoopProfile.ApplyTo`, which writes X and Y by linear interpolation
between the feet** — so applying a loop height *straightened any path the user had routed by hand*.
The owner's own G1 wire is exactly that case: its interior points wander to x = −203200 nm while its
feet stay put, and re-applying the profile would have returned a plain planar arc. WB41 was defending
something real; it was defending it with far too blunt an instrument.

`WireEdits.SetLoopHeightPreservingPath` changes the one quantity asked for and nothing else: every
point's X and Y are kept, both feet are bit-exact, and only the rise above the chord is rescaled. The
scale factor is found by **bisection**, not by the closed form `LoopProfile.SolveAmplitudeNm` uses —
that form is exact only while no point dips BELOW the chord, which a hand-routed wire may well do.
It also needs no span ordering, which matters because `LoopProfile.Validate` demands strictly
increasing spans and a wire that doubles back in XY does not have them.

**Three things fell out, and all three are simplifications:**
- **The shared-profile clone-on-write is gone.** Nothing writes a profile any more, so one array's
  override cannot drag another's wires. *This supersedes this phase's own earlier finding that the
  clone "was needed in practice" — it was needed by the mechanism that has now been retired.*
- **The bound-vs-detached asymmetry is gone**, and with it §2.0's whole precedence table and its
  "N wires were skipped" report. Loop height is a property of the WIRE (`Wire.LoopHeightNm` is defined
  as its own max z minus min z), not of its generator, so a wire dragged loose is reached like any
  other. §2.0's three-way precedence collapses to one rule: **the layout owns the route and the feet,
  the schematic sets the height.**
- **`Update Layout from Schematic` now applies the settings to arrays already drawn**, which is the
  reported bug. Two things make that safe rather than a WB41 violation: the wBond editor's OWN "set
  this array's loop height" command does exactly this (`SetGroupLoopHeight` → `ReapplyToArray`), so
  there is no destruction here the editor would not also do; and what WB41 was actually protecting —
  the route and the feet — now survives it.

**The measured inductances did not move: 10 mil → 1086.2 pH, 45 mil → 2206.7 pH, identical to before
the change.** That is the useful cross-check on a semantic rewrite of this size: for a wire that its
profile genuinely generated, rescaling its own rise reproduces regenerating it, so the new path differs
only where the old one was destroying something.

**A re-run that has nothing left to apply must stay silent**, and is gated — the before/after wire
comparison exists so Update Layout does not mark the layout dirty every single time it is run.

**Still open, and NOT decided here.** The owner's objection was to the profile mechanism itself, not
only to its effect on this one path. `LoopProfile` still exists: it is what the profile view edits, what
a freshly-seeded wire is generated from, and what `.wBond` stores. Removing it is a data-model change
touching the profile panel, `WBondViewModel`'s group commands, WB2/D1's "a binding is a generator", and
the file format — its own phase, not a fix folded into this one. What has changed is that **no
controlling parameter reads or writes a profile any more**, so the setting can no longer affect authored
geometry, which is the half of the objection this report was about.

### Report 6: the reconcile brought geometry back but left the override stating the old number

**"I changed the loop height in layout using the Array Inductance double-click. Then I did an Update
Schematic from Layout, but the loop height was not updated in the schematic."** This was the item
flagged two rounds earlier as *"still open, deliberately not decided"* — the owner has now decided it.

The reconcile wrote the payload and never touched `LoopHeight_G1`. Two consequences, and the second is
the severe one: the dialog went on showing the old number, **and the next Run applied that old number
straight back over the wires that had just been imported**, silently undoing the command the user had
just run. The override is the schematic's *statement* of the loop height; after a command whose whole
purpose is to make the schematic match the layout, it has to state what the layout has.

`WBondPlacement.WriteBackControllingParameters` writes each array's measured loop height, diameter and
material back, **in the parameter's own unit** so the dialog reads "15" rather than 0.000381. Three
rules keep it from doing damage:
- **Only what is already SET.** Blank means "as drawn" and the payload now carries what was drawn —
  writing a number into every blank row on every reconcile would invent overrides nobody asked for.
- **An expression is never overwritten.** `LoopHeight_G1 = loopH` is the handle a sweep turns;
  replacing it with a literal would silently retire the sweep. Reported with the measured value, so
  the decision stays the user's.
- **Wires that disagree are reported, not averaged.** An individually dragged wire can leave an array
  with no single loop height, and inventing one states something about the layout that is not true.

**The hole underneath it is worth more than the fix.** "Nothing changed" was decided by comparing the
`Design` payload alone — so a layout whose geometry *already* matched the payload returned "already
identical" and left the stale override in place, which is very close to the owner's own state on disk
(`LoopHeight_G1 = 10` against a layout at 20 mil). **Whatever a command would write is what decides
whether it has anything to do**; comparing one of the several things it writes is how a no-op check
silently stops covering the rest. It now compares the finished parameter list, order included.

### Not interactively verified

Everything below was gated by test only; none of it has been driven in a running application.

- The parameter panel's new controls — the `Source` combo and its note line, the `Material` dropdown, and
  the per-array `LoopHeight`/`Diameter`/`Material` row grid. **The XAML layout in particular is
  unverified**: the per-array row is a four-column grid inside a panel whose other rows are
  `80,*`-shaped, and nothing has looked at it.
- `File`'s Browse… picker for a wBond (`IsFilePathParameter` now returns true for it) — the row is built
  by the shared `ParameterRowViewModel` path, but a wBond has never had one before.
- The Messages-pane rendering of the new elaboration warnings (detached-wire counts, array drift). The
  drain path itself is exercised end to end through `RunResult.Warnings`.
- Update Layout from Schematic performing the flip in the real command, with the schematic then needing
  a save. `Seed` calls `model.NotifyChanged()`; whether that reaches the dirty indicator the way the
  owner expects has not been watched.
- **Update Schematic from Layout's wire half driven from the menu.** `WBondSchematicReconcile.Run` is
  gated directly (7 cases) and the call site is three lines, but the whole command — layout open, wires
  edited, menu item, Messages pane, symbol repaint after the pin count changes — has not been run. The
  symbol *does* repaint in test (`WBondSymbolProvider.Resolve` returns the new pin count after the
  command executes), but that is not the same as watching the schematic redraw.

## WB40 revised: a `.wBond` is an ATTACHMENT, and lives in `layout/` (2026-08-17)

Owner asked whether the cell architecture should gain a `wires/` view sub-folder, since Update Layout
from Schematic dropped a `.wBond` at the cell root. **Answer: no — into `layout/`, stem-paired with the
`.clay`.** Design authority is now `workspace-and-project-tree.md` §1.2.1 and `wbond.md` WB40 (revised),
with owner decisions O-8/O-9 in that document's table. Three things are worth knowing before touching it.

**A view sub-folder would have encoded the OPPOSITE of WB28.** A view sub-folder is not merely "a folder
in a cell" — it carries §2's contract: N files, **at most one primary**, and an instance resolves
*through* that primacy. That means a view is an alternative description of the cell, of which one is in
force. But WB28 deliberately refuses a wBond singleton, so two `.wBond` in one cell means **both are real
and both are solved**. "One primary, the rest inert" would silently drop one from the simulation. The
mechanical cost (a `ViewType` member, a `primary_wires` in `.ccell`, a fourth empty sub-folder in every
resistor in the standard library) was the smaller objection.

**Cell-root placement was a rationalisation of an assumption the model never made.** WB40's original text
argued the sidecar "is not a view of the cell… which is why it is found by looking one level up." The
first half is right and survives — it is an *attachment*, the third file shape, now defined in §1.2.1
alongside cell views and loose workspace files. The second half assumed one `.wBond` per cell. A cell may
hold several `.clay` files, and wires are drawn over **specific pads at specific coordinates**, so "the
cell's wires" stops being well formed the moment there are two layouts — the old `FindFor` could only
guess (prefer `<cell>.wBond`, else the sole `*.wBond`, else give up).

**Two branches must SPEAK, and they are the price of the placement, not decoration.**
- **Legacy.** Pre-2026-08-17 workspaces keep wires at the cell root. They are still read, and the move is
  named. More importantly `WBondCellSeeding` must *not* write a fresh `layout/<cell>.wBond` when a legacy
  one exists: attachment resolution prefers the stem-paired file, so seeding would **shadow** the user's
  edited wires with a regeneration from the schematic payload — a re-run of Update Layout quietly
  reverting their layout work. It returns `KeptExisting` pointing at the legacy file instead.
- **Orphan.** A `.wBond` in `layout/` pairing with no `.clay` is reported. Renaming a `.clay` in Finder
  detaches its wires, and unlike every other Finder-edit failure mode (§4.1's "Not Found" glyph, the
  tree's System.Warning row) that one would otherwise remove wires from a simulation the user believes
  includes them — silent, and in the direction of a wrong answer.

`WBondCell.Resolve` returns `(Path, Note)` so both branches have somewhere to say it; `FindFor` is now
just its path half. **Not built here:** the project tree does not yet render an attachment as a child of
its view file, nor surface the orphan at the sub-folder level — §1.2.1 and §3.1 specify both.

**Stem pairing broke Save As, which had been working by accident.** Found the same day, tracing the
owner's question about how wire geometry reaches disk. `WireDesignPath` was set once in
`AttachWireDesign` and never followed `CurrentLayoutPath`; under cell-root placement that did not matter,
because resolution looked one level UP and `amp_v2.clay` found the same `<cell>.wBond` as `amp_v1.clay`
did. Stem pairing removes the accident: Save As wrote the artwork to the new name and the wires back into
the OLD file, so the layout the user had just created opened with **no wires at all** while their edits
sat somewhere they never asked for. `RetargetWiresForSaveAs` re-points the path and **forces the dirty
flag** — a Save As with no wire edits must still produce wires at the new name, or the copy is silently
missing the thing the cell is about. An ordinary Save deliberately does not retarget, so a legacy
cell-root file is written back where it lives rather than silently migrated into a duplicate that then
wins resolution over it. The guard was **verified by disabling the fix and watching it go red**, not
assumed.

**How the wire layer is persisted, since it is not obvious from either file alone.** A wire edit raises
`WBondViewModel.Republish` → `DirtyChanged` → sets `_wireDirty` *and* `IsDirty`. The separate flag is
load-bearing: a wire edit puts **no entry on the layout's undo stack** (the wires have their own
history), so `RefreshDirty`'s `_undoRedo.IsModified` term alone would report the cell clean with unsaved
wires in it. The write happens in `MarkSaved` → `SaveWireDesignIfDirty`, **not** in `PerformSave`, because
the workspace writes sub-cell sessions with a bare `LayoutPersistence.SaveToFile` — `MarkSaved` is the
one call every save path shares. A failed `.wBond` write reports through the same `SaveError` seam as a
failed `.clay` write and does **not** clear the flag, so the next save retries instead of dropping the
edits.
**Not interactively verified:** only the resolution, seeding and attach paths are covered by tests; the
legacy-workspace open was not exercised against a real pre-move workspace.

**Also settled the same day, specified and NOT built** — `docs/sonnet-briefs/brief-wbond-controlling-parameters.md`:
loop height / diameter / material as array-scoped *controlling* parameters on the schematic symbol
(`wbond.md` §5.5.1, WB44), span deferred (WB44a), and Carried-or-Linked wire source (§9.7, WB45). The
engine half of the loop-height parameter **already exists** in `ComponentModelFactory` and must not be
rebuilt; the gap is that `ComponentTypeRegistry` declares none of them, so nothing is offerable.

Two things were found the same day by the owner asking what these states actually mean, both now in the
brief and neither previously specified anywhere:

- **`Carried`, not `Embedded`.** §9.1 has always spent *embedded* and *referenced* on a **different
  axis** — whether a `.wBond` file embeds the layout artwork it was drawn over or references cells by
  path. WB45 first reused those two words for where a placed component's *wires* come from, which made
  "does embedded actually mean referenced?" an entirely reasonable question. The axes are independent
  and now have separate vocabulary; `Carried` is §5.0's own verb.
- **A schematic loop-height override and a layout loop drag have no specified precedence, and today's
  code gives three different ones.** `ApplyLoopHeightOverrides` regenerates between a wire's own feet
  (so a moved foot survives — correct), but it regenerates only wires with a `ProfileBinding` — and an
  individually-dragged wire **detaches** (WB2/WB24). So a schematic parameter silently overwrites a
  layout loop edit on a bound wire, and silently does nothing on a detached one, within the same array.
  The fix is a report, not a behaviour change: touching detached wires would break WB2.

## P/A: the key was not repeatable, and the panel came back floating (2026-08-17)

Owner: *"Pressing 'A' hides the Array Inductance panel (good). But pressing 'A' again does not bring it
back — I have to click on the layout canvas first. Also, when I press 'A' to bring it back, it appears as a
floating window."* Two independent defects behind one gesture.

### Two states, not three — the middle one made the key non-deterministic

Follow-up the same day: *"I press 'A' to open Array Inductance but the Wire Profile gets focus, so pressing
'A' does not hide the Array Inductance… I should be able to press A repeatedly and the view toggle on and
off."*

**Two defects, and the first one is mine from the round above.** `ToggleToolPanel` had a middle state —
showing but behind another tab meant *bring it forward* — which reads reasonably in a spec and is wrong at
the keyboard: a panel tabbed with another needed THREE presses for one cycle, and which press did what
depended on a tab order the user was not thinking about. **A key that means "show/hide this" has to mean
that every time.** Showing ANYWHERE now means the next press hides it. (The View ▸ Panels menu is
unaffected — it still means "show me that panel", which is why it stays a separate command.)

> **Superseded 2026-08-18 — the middle state is back, and the diagnosis above was half right.** Owner:
> *"if the Properties is tabbed behind Analyses, I want Properties to come to the front. This should be
> true to [any] window tool that is behind another pane."* What actually made the old middle state
> non-deterministic was not the state, it was that **`IsToolPanelShowing` counted a panel behind another
> tab as showing** — so the press that merely raised it looked like a press that did nothing, and one
> cycle read as three. "Showing" now means *in view*, the raise is a visible state change, and every
> press moves between the two states the user can see. Anything bound to `IsToolPanelShowing` still reads
> as a plain two-state toggle; see the 2026-08-18 toolbar section at the top of this file.

**And the panel really was coming back behind the other one.** `BuildSide` resolves a group's front tab as
`ordered.FirstOrDefault(p => p.Active)` over panels sorted by `Order`, and the live capture the restore
builds on already had the OTHER panel marked active — so the lower `Order` won. Only one panel in a group
can be in front, so the restore clears the flag across the group it is rejoining, and the targeted path
states `dock.ActiveDockable = tool` directly rather than trusting an insert to imply it.

### The two are ONE root cause: restoring by REBUILD

Reported three times before it was actually fixed, and the third report — *"I also see the entire workspace
dock redraw when the Array Inductance is brought back… when I dock it manually using the Dock system there
is no flash"* — is what finally named it.

**Closing a panel lets its emptied `ToolDock` collapse out of the tree, so the only way back was
`ApplyDockLayout` — a full rebuild.** That rebuild is both symptoms: the flash the owner could see, and the
reason the key stopped working, because the view handling it was re-created underneath it.

**Dock has the mechanism already: `HideDockable` / `RestoreDockable`.** Hide moves the dockable to the
root's `HiddenDockables` and records `IDockable.OriginalOwner`; restore puts it back into that owner at the
same proportion, touching nothing else. Verified directly against the library before building on it — and
the test asserts the tree is byte-identical afterwards, which is what "no flash" means structurally.

Hide leaves an **empty `ToolDock`** behind, which is correct for the library — it is what makes the restore
exact — and is left strictly alone. An early version detached it, on the untested belief that an empty
proportional child would show as a blank strip; it renders at 0 px, and the detach caused a bug of its own.
See *The panel shrank on every toggle*, below.

The rebuild path survives for the two cases hide/restore cannot serve: a placement read back from a `.cws`
(nothing is hidden in a session that just started), and a parent that has since left the tree.

### And the key: gated on focus, performing an action that moves focus

The P/A handler was a tunnel handler on the layout view gated on `LayoutCanvasCtrl.IsKeyboardFocusWithin`.
**That shape cannot work for this action.** Closing a dockable moves keyboard focus off the canvas — Dock
focuses what is left in the dock it just emptied, and the surrounding content is re-realised — so *the very
act the key performs disarms the key*. The first press worked; the second needed a click first.

**Re-asserting canvas focus afterwards did not fix it, and was the wrong shape too.** It is a patch on the
symptom, it races Dock's own focus handling, and it loses often enough to be useless — the owner reported
the same bug again with that patch in place.

**The handler belongs on the SHELL WINDOW** — `WorkspaceWindow.OnWindowKeyDownTunnel`, beside the
placement-rotate shortcut that is there for the identical stated reason ("regardless of which control has
focus"). The gate stops being *which control is focused*, which the action changes, and becomes *which
document is active*, which it does not (`WorkspaceViewModel.WirePanelKeysApply`).

An intermediate attempt registered per-view on the `TopLevel`; that removed the focus dependency but kept a
lifetime problem (attach/detach, `IsEffectivelyVisible`, an `e.Handled` backstop for split panes). One
registration on the window has none of those. Its two guards are `WirePanelKeysApply` — a layout with
wirebonds, not mid-label — and `IsTypingInAField()`, the same three control types `WBondEditorView` uses,
so a bare letter typed into a field stays a letter.

`ToggleWirePanel` now touches focus not at all, so it is left wherever the user wants it — including inside
the panel that has just appeared.

**The general lesson:** a keyboard shortcut gated on a specific control's focus is only safe when the action
cannot disturb focus. When it can, the gate belongs at the window, with the *intent* (which document, is the
user typing) as the guard instead of a focus location.

### It came back floating because `ShowToolPanel` has only one answer

Its answer for a panel that is not in the tree is *float one* — right for a View-menu item, wrong for a
toggle whose whole purpose is to undo the hide. **Nothing had remembered where the panel was.**

`_panelHomes` records **two** things per panel, because they answer different questions:

- **The live `IToolDock` plus the index in it** — an exact restore via `InsertDockable` that needs no
  rebuild. **This path exists to avoid a rebuild, and that is not an optimisation:** `ApplyDockLayout`
  re-realises every document's view, which would throw away the pan and zoom of every open canvas. Not a
  price a keystroke should pay. The remembered dock is verified with `DockLayoutCapture.Contains` before
  use — a collapsed or dragged-away dock is a live object with a stale place in it, and inserting there
  puts the panel where nobody can see it.
- **The schema placement** (side, group, order, width, inboard, or the floating rectangle) — for when the
  column no longer exists at all because that panel was the only thing in it, and for a place read back
  from a `.cws`.

Remembering happens on `DockableClosing` as well as inside the toggle, so closing by the tab's own X leaves
the same trail back.

### And the place survives a restart

A closed panel is not in the live tree, so `Capture` cannot see it — the place would be forgotten the moment
the workspace was saved with the panel hidden, and next session's first press would float it again.
`CaptureDockLayoutForPersistence` writes an `Open = false` entry for each remembered place; every reader
already ignores it (`BuildSide` filters on `Open`), and `SeedPanelHomesFrom` reads them back **before** the
layout is applied, since the apply drops them. Deliberately not folded into `DockLayoutCapture.Capture`,
which is a pure walker of a live tree and has no business knowing what a view model remembers.

**Writes, meaning OVERWRITES — and that only started mattering when a panel of the DEFAULT layout got a
toggle of its own (2026-08-18).** `Capture` itself already emits an `Open = false` entry for every panel of
the default layout it cannot find in the tree, *at the default placement*. The two wBond panels are in no
default layout, so for them the remembered place was simply added and nothing collided. Library, Properties
and Messages are in it — so an add-if-absent pass would leave the shipped placement standing and quietly
discard the user's own, and the first press of their toolbar button after reopening the workspace would move
the panel to the default instead of back where they left it. `RecordClosedPanelPlacement` therefore
overwrites a CLOSED entry and only adds when there is none; an OPEN entry is left alone, because the live
tree is then the truth and the remembered record is the stale one.

### The same two symptoms again, for a panel in a FLOATING window (2026-08-17)

Owner: *"lots of issues getting A or P to toggle when they are floating — their window contents disappears
and the window is not closed, and I see that flash bug too."*

Both are one measured fact about the library, and it was **checked against a real `Factory` before anything
was built on it** — the previous three attempts at this bug were each reasoned out and each wrong.
**`HideDockable` files a floating tool under the FLOAT's own root, not the shell's:**

```
after HideDockable(arr):
  shellRoot.Hidden = []          ← where the restore looks
  floatRoot.Hidden = [arr]       ← where it actually went
  floatToolDock.Visible = []     ← the vanished contents
  shellRoot.Windows = 1          ← the window that stayed open
```

So the empty window sits there, and the shell-root hidden check misses, and the restore falls all the way
through to `ApplyDockLayout` — the flash. That measurement is pinned as a test
(`DocksOwnHide_FilesAFloatingToolUnderTheFloatRootAndLeavesTheWindowOpen`); if a future Dock release changes
it, the test says so and the floating branch can go.

**The fix is not a workaround for that — it is what the two cases actually are.** A docked panel's place is a
*slot in a tree*, which the library holds open for us — hence hide/restore and nothing more. A floating panel's place is a *rectangle on a screen*, which is a **value**: write it
down, close the window outright, and re-open one at that rectangle on the way back
(`FloatTool` → no shell rebuild → no flash). The remembered rectangle still goes through
`FloatingWindowPlacer` (R-dock-6) — the monitor it was on may be gone.

Two things fall out of it:

- **A float the user dragged a second panel into is still that other panel's window.** `HoldsOtherTools`
  decides; a shared float closes only the one panel. `RememberPanelHome` already promised the restore its
  *own* rectangle rather than a seat back in a window it no longer shares.
- **Closing raises `DockableClosing`, which arrives back at `RememberPanelHome` after the window has left the
  tree** — a second pass that finds nothing and would overwrite the rectangle recorded a moment earlier with
  "nowhere". A record naming no place carries no information, so `RememberPanelHome` now keeps the older one.
  Ordering, not defensive padding: without it the panel reappears as a fresh default-placed float.

`CircuitRfDockFactory.CloseFloatingWindow` is `CloseFloatingToolWindows`' body, extracted rather than
copied — the `HostWindows` deregistration in it was paid for once already (a missed removal crashes the next
window drag inside `SortWindowsByZOrder`), and there must be exactly one copy of that.

### The panel shrank on every toggle — the "tidying up" was the bug

Owner: *"if the panels are docked and I press A or P repeatedly, the height is not respected — the panel
gets smaller and smaller."*

`Hide` used to detach the emptied `ToolDock` and one adjacent splitter and re-attach them on the way back,
reasoning that *a proportional child with no content is a blank strip taking its share of the window*.
**That reasoning was never measured, and it is false.** Laid out for real, an emptied dock and its splitter
both render at **0 px** — Dock collapses them itself.

The detach was also the cause of the shrink, by a route that **cannot be fixed from the layer that caused
it**:

1. Removing the dock leaves its sibling alone in the column, so `ProportionalStackPanel` renormalises the
   sibling's **control** to 1.0 as a *local* value, which two-way-binds back to the model.
2. Re-inserting the dock and re-asserting the remembered proportions on the **model** cannot undo that: a
   local value on a control outranks the style-priority binding, so the survivor's control keeps its 1.0 and
   never sees the model write.
3. The next layout pass normalises 0.668 against 1.0 → 0.40/0.60 and writes *that* back. Measured across
   cycles: 0.668 → 0.4005 → 0.2860 → 0.2224 → 0.1819.

Left alone, the collapse is Dock's own and reverses exactly — 0.668/0.332 returns to 0.668/0.332 for as many
cycles as you like. **The fix was deleting the mechanism**, not adding to it: `DockPanelHiding` is now
`HideDockable` / `RestoreDockable` plus a reachability guard, and the `DetachedOwner` record, the proportion
bookkeeping and `_detachedOwners` are all gone.

**Two failed attempts preceded this, both from reasoning about the library instead of measuring it**, and
the second is the more instructive: recording every sibling's proportion at hide time and writing it back on
restore is a correct-sounding fix that passes a model-level test and changes nothing on screen, because the
value it writes never reaches the control. The escape was a throwaway headless Avalonia probe — a real
`DockControl`, the real Fluent theme, a real layout pass — which reproduced the exact drift in one run and
then showed the no-detach variant holding steady. **When a mechanism spans model and view, a model-only
experiment cannot settle it**; standing up the real stack in a scratch project is cheap next to a third
wrong answer.

The in-repo tests can only gate the model half (`Ui.Tests` calls no Avalonia runtime API), so they assert the
thing that *is* model-visible and that the probe identified as decisive: hiding a panel leaves its column's
children and every proportion in the tree **byte-identical**, over five cycles. That is exactly the property
whose absence caused the bug.

### …and then the key died after exactly two presses — a float is another `TopLevel`

Owner: *"when those windows are floating I can only toggle them twice before I am forced to click on the
canvas. This works perfectly when they are docked."* Two presses is the tell, and it counts out exactly:
press (close, focus never left the shell), press (reopen — **presenting a window activates it**), press
(delivered to the panel's own OS window, which had no handler on it). Docked panels never showed it because
everything is inside the one window.

**The handler is now registered per `TopLevel`** — the shell and every `CrfHostWindow` — with one shared
body in `Views/WirePanelKeys.cs`. A float has no view model of its own, so it resolves the workspace through
the shell window's DataContext; in the standalone wBond app that finds nothing and the shortcut is simply
absent, rather than needing a second gate.

**Explicitly not solved by keeping focus in the shell when a panel floats.** Stealing focus back from a
window the user just asked to see is the same class of patch as the three that lost to Dock's own focus
handling, and it would make the panel unusable — its own fields could never be typed into.

**The rule, now stated in the code:** a shortcut whose own action can move focus must not be gated on focus,
*and* must be reachable from every surface focus can land on. The previous fix got the first half (off the
view, onto the window, gated on which document is active); this is the second. Each attempt covered one more
surface — canvas, then window, then every window — which is the shape of the mistake: the question was never
"where is focus", it was "where can focus be".

## `Side` could not say WHICH column — a panel docked beside the documents restored below the outer one (2026-08-17)

Owner: *"I docked the Array Inductance window to the left of the layout document (kind of 'inside' the
document), but when I re-opened the workspace it was loaded on the left side, but below the Properties
Inspector."*

**`SideOf` was right; the schema was not expressive enough.** It captured `Left` correctly — it
deliberately walks outward past any container that does not separate the tool from the documents, which is
the 2026-08-14 fix and still correct. What it is silent on is **which left column**, and there are two: the
outer one at the window edge, and one between it and the document tabs. With only "Left" to work from,
`BuildSide` did the only thing it could — stacked the panel as another ROW of the outer column, under
whatever was already there.

**This is the THIRD owner-reported bug from this one area**, and worth naming as a family: `Alignment` is
not a column (2026-07-30); a container that does not separate says nothing about the side (2026-08-14); and
a side does not identify a column (this one). Anything added to `SideOf` needs a test that the *other*
arrangements still capture — a naive fix for one has twice traded it for another.

### `CwsDockPanel.Inboard`

One additive bool. Capture answers it with a single question asked at the OUTERMOST proportional container:
*does it separate the tool from the documents?* Same branch → everything distinguishing them happens
further in, which is what inboard means. Different branches → an outer column.

Three consequences worth knowing:

- **The group counter is keyed on `(Side, Inboard)`.** Two Left columns are two places; a shared counter
  would tell an inboard panel and an outer one they are in the same group and rebuild them into one
  column — the reported bug in a second form.
- **An inboard column gets its OWN `Sides` entry, flagged `Inboard`.** Two Left columns can have two
  widths, so the side alone cannot be the key — it is `(Side, Inboard)`, and the caller keeps the first
  entry per key, which is also what stops an inboard column from silently replacing the outer one's width.
  *This is the corrected form: the width used to be inferred from the panel instead — see below for what
  that cost.*
- **The builder wraps the DOCUMENT AREA**, not the document column, in the inboard horizontal split. That
  is the shape Dock's own drop produces — the split replaces the document dock and leaves top/bottom docks
  outside it — so a restore is indistinguishable from the drag that made it.

### The layout document lost its width — a proportion that answered a different question

Owner, 2026-08-17, with the workspace that showed it: *"the width of my layout document was not respected
when I re-opened my workspace."*

An inboard column's width was read off its first PANEL's `Proportion`. **A panel's proportion is its share
of its own column, measured DOWN; a column's is its share of the document row, measured ACROSS.** The
owner's `.cws` stacked two wirebond panels 0.668/0.332 in a right inboard column, so it reopened with the
column claiming **0.668 of the window's width** and the layout document squeezed into the third left over.

The 0.668 is the whole trap in one number: a perfectly valid proportion, in the right range, in the right
field — simply an answer to a different question, so nothing could complain. The original note above
reasoned its way to the panel because `Sides` was keyed on the side alone and could not hold two Left
widths; the answer was to widen the key to `(Side, Inboard)`, not to find another field that happened to
have a number in it.

`CwsDockSide.Inboard` is additive, so the version does not move (same reasoning as below). A file written
before it has no inboard entry and takes the default width — correct, because there is nothing trustworthy
in it to recover; the exact width is kept from that workspace's next save onward. Verified by running the
owner's actual `.cws` through the real factory: the column comes out at 0.20 rather than 0.668, leaving the
document ~80% of the row instead of 33%.

### Not a version bump, deliberately

`CwsDockLayout.CurrentVersion` stays 1. Bumping it would make an older build refuse the whole block as
"newer than this build understands" and fall back to the default layout — **losing every panel position to
gain one flag.** An unknown JSON property is simply ignored on read, so an additive field costs a round
trip through an older build nothing. `Inboard` is normalised to false on top/bottom, which are inboard by
construction and where the distinction does not exist.

## Nothing wrote the `.cws` because a PANEL MOVED (2026-08-17)

Owner: *"The Wire Profile and Array Inductance dockable positions are not respected when I re-open the
saved workspace."*

**The persistence chain was not the bug.** Capture → JSON → read → re-apply on a fresh factory round-trips
both panels exactly, docked and floating; that is now pinned by `BothPanels_SurviveASaveAndReopen_Docked`
and `…_Floating`, which run the whole two-session sequence.

**The bug is that the `.cws` was only ever written by accident.** Its callers are an explicit save, the
tree-filter debounce, clean exit, and a workspace switch — **none of them a dock rearrangement.** So an
arrangement was recorded only when something unrelated happened to trigger a save while the panels were
where the user wanted them. That is the *identical* failure shape already documented one layer along on
`PersistOutgoingWorkspaceSession` ("no path that LEAVES a workspace called it, so the session was only
ever recorded by accident") — the same hole, a different trigger.

### Why it showed up on these two panels and nothing else

**Every other panel is in the shipped default layout, at roughly where users expect it.** So when the
saved block is missing or stale they still land somewhere plausible and *look* respected. The two wBond
panels are deliberately absent from both defaults (most designs have no wirebonds), so a stale block loses
them completely and they come back **closed** — "including whether they were docked or not", exactly as
reported. **A defect in this mechanism is invisible on any panel that has a default position.**

### The fix, and the guard that matters more than the fix

`WireDockArrangementPersistence` subscribes to the Dock events that mean "the arrangement changed"
(`DockableDocked/Undocked/Closed/Moved/Swapped`, `WindowMoveDragEnd/Opened/Closed`) and arms the existing
3-second `ScheduleCwsSave` debounce. Deliberately **not** `DockableAdded`/`DockableRemoved` or the
activation events: those fire in bulk while a layout is being built, and on every tab switch, which is not
an arrangement change and would arm a disk write on every click.

**`_layoutRebuildDepth` is the half that prevents data loss.** Applying a layout raises those very events,
so a restore would arm a save of what it just applied — and when a restore has DEGRADED to the default
(R-dock-5's own fallback), that debounced write lands three seconds later and **overwrites the user's good
saved arrangement with the fallback**. Raised around `ApplyDockLayout` and around the workspace-open
clean-slate rebuild, which is the one that could actually clobber.

**Known limit, stated rather than papered over:** dragging a floating panel by its ordinary OS title bar
routes through no Dock event at all (the same fact `LiveGeometryOf` exists for), so that move alone arms
nothing. Its geometry is read LIVE at capture time, so the position is still correct in whatever save comes
next — clean exit and workspace-switch both do.

## wBond round 5d — the docked panels became first-class surfaces (2026-08-17)

Six items, all about the two dockables being real places to work rather than side views of the wBond app.

### The plane control belonged to the profile VIEW, not to a toolbar

It lived in the wBond editor's toolbar, so **it did not exist at all in the dockable Wire Profile panel**
— where the setting matters just as much. Moved onto the canvas as a floating control in the top-right
corner, which means it travels with the view into every host: one control, one implementation, always
reachable. Top RIGHT because this canvas is read from the left (span increases rightwards, the wires
start at the left edge), so that corner is the one a control can occupy without covering the geometry.
Its `DockPanel` wrapper in the wBond toolbar went with it — it existed only to right-align that combo.

### Each panel's tab now says WHOSE wires it shows

A workspace can hold several cells with a wBond in each, and both panels follow whichever layout is
active — so a tab reading only "Wire Profile" says nothing, and the answer changes under the user as they
switch tabs. `WBondToolBase.Subject` appends the cell name. **The `Id` deliberately does not move**: it is
what a `.cws` stores and what layout capture/restore matches on, so a retitled panel still comes back
where the user put it. The wBond app does not have the problem (one document, one layout) and does not use
these panels.

### "The wires are already in the layout" is not news

Owner: *"I already know that the wires are in the layout. I am updating it, so why would the system give
me this warning?"* Removed. `DescribeExisting` returns **null** for the agreed-and-kept case now, and null
is the ordinary outcome. The two messages that remain are things the user cannot see for themselves: an
unreadable sidecar, and array-list DRIFT. Reporting the expected outcome as a warning trains people to
skim the pane, which costs the messages that matter.

### Revealing the panels, and reaching them afterwards

- **Shown on the FIRST seed only** (`Outcome.Created`). Someone who has just generated wires has no
  reason to know two panels exist. A re-run leaves the arrangement exactly as they have since set it —
  a command that re-opens a panel you closed on purpose is worse than one that never opened it. Through
  `ShowToolPanel`, never `ToggleToolPanel`, so a reveal can never CLOSE an already-open panel.
- **Two toolbar buttons on the hosted layout editor, plus `P` and `A`.** They TOGGLE
  (`WorkspaceViewModel.ToggleToolPanel`: open when closed, bring forward when behind another tab, close
  when already in front) — which is what makes them read as state rather than as two more "open
  something" buttons, and is why they are not the View ▸ Panels command, where closing what you asked for
  would be a trap.
- **Gated on wires AND on a reachable workspace shell.** The second half is what keeps them out of the
  standalone wBond app (owner): that window hosts both panels inline and has no dock at all, so a button
  to show one has nothing to show it in. Same "is a shell reachable" test the DRC and EM buttons beside
  them already use.
- Bare `P`/`A` are free in the layout editor (only `Ctrl+A` is taken), and are gated on
  `!IsTypingLabel` so a letter typed into a label stays a letter.

### The Properties panel had no wire route from a wirebond cell

Clicking a wire changed `WireEditor.Selection` — which the layout inspector cannot see and nothing was
watching, so the panel went on showing the artwork's own empty selection.
`RefreshLayoutPropertiesContext` mirrors `RefreshWBondPropertiesContext`'s rule exactly, including why
**wires win a tie**: a layout selection can outlive a wire press, because the overlay consumes a press on
a wire without the layout editor ever seeing it, so reading that stale one as intent would flip the panel
away from the wire just clicked. Watched only while a layout document is active, on the same rule as the
wBond watch beside it.

## wBond round 5c — the docked panels had no coupling to the layout they follow (2026-08-17)

Six owner items. Four of them are one omission.

### The wires were never coupled to the layout's Snap and Unit

In the wBond editor `WBondDocumentViewModel` keeps the wires' snap pitch and display unit in step with
its reference layout. **A wirebond cell in the ordinary Layout Editor had nothing doing that**, and three
of the six reports fall straight out of it:

- the docked Wire Profile view drew **no grid at all** (pitch 0, because nobody pushed one);
- its **rulers stayed on the wBond default** while the layout's own Unit box said something else;
- and Snap changes reached neither.

`LayoutEditorViewModel.PushLayoutSnapAndUnitToWires` is the coupling, run at attach and on every
`SnapDbu`/`DisplayUnit` change — so the one Snap box and the one Unit box in that editor govern the wires
too. The docked profile tool needs the pitch as a value it can push into a plain CLR property on the
control, hence `WBondProfileTool.GridPitchNm` + `WireGridPitchChanged`.

### The array double-click: the FIRST fix was inert, and the real cause was a one-shot push

Reported twice. The first round made a selection change REPAINT — and that was correct but inert,
**because the selection was never happening at all.**

`WBondInductancePanelView` had its editor pushed in by each host, and the docked host pushed it exactly
once: on its own `DataContextChanged`, which fires when the TOOL is bound and never again. **A dock tool
instance lives for the whole session while the editor it points at changes with every document
activation** — so the property was null for the life of the panel, and every gesture on it (the array
double-click and all four settable rows) returned immediately.

It lives on the FORMATTER now (`WBondPanelViewModel.Editor`), set beside `Unit` by both hosts: every host
that has rows to format has the editor that produced them, and both are assigned together, so **there is
no second moment to forget**. `NoHostPushesTheEditorIntoThePanel` is the source scan that keeps a push
from being added back.

**The lesson worth carrying:** a one-shot `DataContextChanged` push is safe only when the pushed value
cannot outlive the DataContext. For a dock tool it never can — the tool IS the long-lived object.

### And the repaint half, which was still needed

Double-clicking an array name **did** then select its wires — and still nothing redrew. The canvas
repaints on `ReadoutChanged`, and a selection raises none; the overlay object itself was never touched
either. Two subscriptions fix it, and both belong where they are:

- `LayoutEditorViewModel` watches `WireEditor.Selection`/`PreviewSelection` and pokes
  `WBondLayoutOverlay.NotifyChanged()` — the overlay's first API for "something changed that was not one
  of my gestures".
- `WBondProfileView` watches the same two, so the SHARED control repaints itself in either host rather
  than relying on the wBond editor's code-behind, which is the only reason this ever worked there.

**Also fixed while in there:** `WBondProfileCanvas` never unsubscribed `ReadoutChanged` when its view
model was replaced. Harmless while it was constructed once per editor; now that it is also a dock tool
re-pointed on every document activation, a stale handler repaints it for a design it no longer shows and
keeps that design alive.

### The parameter dialog

- **Temp and GroundPlane moved to the bottom** by putting the custom panels ABOVE the generic
  `ItemsControl` in the StackPanel. Nothing changes for any type without a custom panel (all the rest),
  and SnP hides the generic rows entirely, so order cannot matter there.
- **The panel's own "Update Layout" button** sits on the Design row, directly above the arrays' Add
  button. It runs `WorkspaceViewModel.UpdateLayoutForWBond`, which is `RunLayoutUpdate` with an
  `onlyWBond` component — **the instance generator is skipped entirely**, so nothing else in the layout
  moves under a user who is editing wires. Three details worth keeping:
  - The schematic document is found **from the view model**, not from the active dockable: this is a
    NON-MODAL dialog, so the user may well have clicked another tab since it opened.
  - **The dialog closes on the view model's `WBondLayoutUpdated` event, not from a `Click` handler** —
    Avalonia raises `Click` *before* it executes `Command`, so closing from there would tear the
    DataContext down before the update ran. Gated on the host being a `ParameterEditorDialog`
    specifically, because the same control is also the docked Properties inspector.
  - The button is **absent** with no workspace rather than present and only able to refuse.
- **A targeted seed refuses by name** when its component is no longer in the schematic (deleted while the
  dialog was open), instead of silently falling back to a different wBond's wires.

### The Wire Profile view drew a grid it then ignored

Owner: *"the Wire Profile view is not respecting the snap resolution."* `WBondProfileCanvas.GridPitchNm`
was read in exactly one place — the renderer. Nothing in its pointer handling snapped: a vertex dragged
there went wherever the pixel said, and a wire drawn there placed both feet off-grid.

**That is verbatim the failure the layout overlay's own note warns about** — *"the metadata bar would show
a Snap distance, both canvases would draw a grid at that pitch, and the wires would ignore both"* —
guarded there when it was written and never guarded here. Four places now snap, and the set is the point:

- the drag's **baseline** at press (measured from the raw point, the whole drag inherits whatever
  sub-step offset the hand pressed at);
- the drag's **per-frame** cursor, so it steps grid point to grid point;
- the wire tool's **ghost** and its **commit**, which must be the same snapped point or the wire lands
  somewhere the ghost was not.

**Alt-drag is deliberately NOT snapped** — it scales rather than places, and Alt is the app-wide snap
suppressor anyway (R-snp-11), so both readings agree. **Grid only, no geometry**: this canvas's axes are
span and z, and there is no artwork in that plane to land on.

### The panel said its own name twice

A dock TAB titled "Array Inductance" over a panel whose first row is the words "Array Inductance".
`WBondInductancePanelView.ShowHeading` is false for the dock tool and true inline in the wBond editor,
which has no tab of its own and where that heading is the only label there is.

## wBond round 5b — a wirebond CELL had no undo and no marquee, and pressing a pad ate the wire selection (2026-08-17)

Two owner reports about a wirebond cell in the ordinary Layout Editor, plus a third defect found while
fixing the second.

### Undo could not reach a wire edit, and the menu item was DISABLED

The workspace routes Undo to `LayoutDocument.UndoRedo` — the session's **command** stack — and a wire
edit lives in `WireEditor`'s **snapshot** stack. Nothing reached it from the Layout Editor at all.

`IUndoableDocument` gained four defaulted members (`UndoLast`/`RedoLast`/`CanUndoLast`/`CanRedoLast`,
plus the two descriptions) so **every other document type is untouched**, and `LayoutDocument` forwards
them to the active session, which picks the history with the newer `EditSequence` stamp. The rule itself
is now `EditSequence.UndoTakesFirst`/`RedoTakesFirst`, shared with the wBond editor's own Ctrl+Z — the
two ask the identical question and a second copy of the comparison is a second chance to get the
direction backwards (undo takes the LARGER stamp, redo the SMALLER).

**The half that made it look completely dead:** a wire edit raises no `UndoRedoStack` notification, so
`CanUndo` was never re-evaluated and the command stayed disabled — Ctrl+Z did literally nothing rather
than the wrong thing. `LayoutEditorViewModel.WireHistoryChanged` is the signal, raised off
`WBondViewModel.DirtyChanged` (which `Republish` fires on edits **and** on undo/redo, so one hook covers
both), and both the shell's Undo command and a torn-off window's key bindings subscribe to it.

**A pre-existing bug found on the way:** `SetActiveUndoTarget` followed a `SchematicDocument`'s
`ActiveViewModelChanged` but not a `LayoutDocument`'s — so after pushing into a sub-cell, Undo stayed
hooked to the PARENT cell's stack. Same shape, one line.

### The marquee: one gesture, two selections, and the overlay consumes nothing

A wirebond cell ships `WireMarqueeEnabled = false` (there the artwork is the subject), which meant wires
could not be marquee-selected at all. The answer is **a companion marquee**: the overlay follows the box
the LAYOUT editor is dragging and declines every event, so one drag selects the shapes it caught *and*
the wires it caught. That is §6.3's "two independent selections held at once" applied to a drag instead
of a click, and it needs no new mode.

Two details worth keeping:

- **`_marqueeActive` stays false for it.** The layout editor draws its own box for the same gesture, and
  a second one at the same coordinates is a visible double stroke. Only the wire PREVIEW is published.
- **A press on layout geometry starts no companion box** — that gesture is a MOVE drag, and a box there
  would replace the wire selection every time a pad was nudged.

### Pressing a bond pad cleared the wire selection

Found by the companion-marquee test, and a defect of its own. `WBondLayoutOverlay.OnPointerPressed`
resolved the WIRE selection *before* discovering the press belonged to the layout editor — so nudging a
pad silently threw away the wires the user had picked. **That contradicts §6.3's own contract**, which is
the entire basis for holding both selections at once. The routing decision now comes FIRST: a press on a
thing the layout owns is declined untouched; a press on genuinely empty space still clears, because
nothing was clicked. Round 4's own gate test
(`APressOnLayoutGeometry_IsDeclinedSoTheLayoutEditorCanHaveIt`) still passes unchanged — it asserted the
routing, which is what moved, not the clearing, which is what was wrong.

## wBond round 5 — three of the five reports were ONE seam, and the wBond finally reaches the layout (2026-08-17)

Five owner items, all downstream of WB-F's hosting change.

### The snap glyph and the layout selection: one root cause

`LayoutCanvas` offers the overlay every press and move first, and **anything it consumes never reaches
`LayoutEditorViewModel.OnPointerMoved`** — which is the only thing that ever refreshed or cleared
`_currentSnapCandidate`, and the only thing that ever cleared the layout's own selection. Three
symptoms:

- **The glyph freezes** on the vertex a wire was grabbed by, while the wire is dragged away from it. It
  is the last HOVER's marker, left standing for the whole gesture.
- **No glyph mid-draw**, even though the second foot is snapped on every frame — the answer existed and
  had nowhere to go.
- **Clicking empty space did not deselect layout geometry**, because the wire marquee consumed that
  press.

Both halves are now published by the overlay, through two defaulted `ILayoutCanvasOverlay` members
(`SnapMarker`, `ConsumedPressWasEmptySpace`) that the canvas reads after every consumed gesture.
**`WBondLayoutOverlay.SnapPoint` is the single place the marker is set**, so what is drawn is by
construction the feature the geometry actually landed on rather than a second computation of it.

Three things about it that are decisions, not accidents:

- **`SetOverlaySnapMarker` is DISPLAY-only** — `_snapCandidateIsRealTarget` stays false, exactly as for
  the synthetic grab echo. Letting it feed `RecomputeMoveDelta`'s absolute-position branch would move
  layout SHAPES to a point chosen for a wire.
- **A GRID snap marks nothing.** The layout editor's own marker has never marked the grid, and a glyph
  under every cursor position carries no information.
- **A ROTATE marks nothing either**, and this one is a trap: `BeginRotate` returns from the press path
  *before* `SnapPoint` is reached, so a rotate never computes a snap — a marker during one could only be
  the previous gesture's. The guard is `_drawStart is not null || _dragging`, deliberately not
  `_rotating`, and the press clears the field as well.

### `Update Layout from Schematic` and a wBond: the wires go in the CELL, not in an instance

A wBond was `IsPhysical` to `SchematicToLayoutGenerator`, resolved no layout view, and reported
*"no layout view — skipped"* — a true statement about a mechanism the user has no reason to know about.
**WB23 is why there is nothing to place: no wire ever enters a `.clay`.** So §9.5/WB41's answer is the
cell's own `.wBond` sidecar (`WBondCellSeeding`), which is the SAME file `WBondCell` already loads —
**one change answers both halves of the report**, the wires-after-generate one and the
wires-when-I-reopen-the-`.clay` one.

- **A re-run never overwrites wires the user moved.** That is the entire reason WB41 refuses to make
  this a PCell. The sidecar is written once and thereafter kept, with a drift line through
  `WBondPlacement.DriftBetween` when the array lists have since diverged, naming §9.6 as the remedy.
- **`WireDesign` is assigned LAST in `AttachWireDesign`**, because it is the notification a view attaches
  the overlay on — and attaching to an *already open* document is now the ordinary case, since the seed
  writes into a layout the command has just brought to the front.
- Two wBonds in one schematic have no single answer (merging arrays breaks each one's array-to-pin
  mapping), so the first is written and **the rest are named**.
- **Not done, on purpose:** a cell whose layout predates this change has no sidecar, and opening its
  `.clay` shows no wires until Update Layout from Schematic is run once. Seeding on OPEN would violate
  R-L5-23 ("no save hook, no open hook, no document-activation hook") and that guardrail is worth more
  than the one-time convenience.

### The parameter panel painted over itself because its container was a `Panel`

`ParameterEditorView`'s parameter area was a `Panel` — every child gets the whole area. Harmless while
SnP was the only custom panel, because SnP *replaces* the generic rows (`IsVisible="{Binding !IsSnp}"`).
**The wBond panel shows BESIDE them** (`Temp` and `GroundPlane` stay ordinary rows), so it painted
straight over them: the owner's "the Add button and some other text render overtop of the parameters
fields". A vertical `StackPanel` fixes it and changes nothing for SnP, since a hidden child takes no
space either way.

Also: `WBondSymbolGenerator.Describe` now takes a `LayoutUnit` — the workspace `.ctech`'s
`DefaultDisplayUnit`, reached through a new `SchematicViewModel.WorkspaceDisplayUnitProvider`. **Wired on
the schematic session, not on each parameter editor**, because three places construct one of those and
exactly one place builds a session. The fallback is **mils**, not millimetres: the only length a
schematic currently reports is a wirebond's, and a bonder works in mils. And the `G1.i / G1.o` column is
gone — it was an internal spelling of "this array's + and − terminals", and the terminals are on the
symbol where they are actually wired.

## WB-F — the wBond editor HOSTS `LayoutEditorView` instead of transcribing it (2026-08-16)

The owner, after round 4: *"I just tried the new geometry shape tools in wBond and there are many many
bugs (that we had previously resolved when we hardened the Layout Editor)."* **They were not in the
geometry.** `LayoutEditorViewModel` is one object shared by both editors and always has been — its
snapping, hit-testing, drawing tools, commands and undo stack are the hardened ones. What was
duplicated was the ~2,700-line *view shell*, and that is where every one of those bugs lived: a tool
armed with no Escape to disarm it, arrow keys reaching the wrong handler, no breadcrumb bar, focus that
never came back after a toolbar click. Hosting the real control deletes them all at once.

**The seam needed nothing new.** `LayoutDocument(title, viewModel, path)` already takes an existing
view model, so `WBondDocumentViewModel` builds one around its reference layout in
`OnReferenceLayoutChanged` — the single funnel all three creation points share — and the XAML binds
`ViewModel.LayoutDocument`. No interface extraction, no view-model surgery. Push-in, pop-out and the
breadcrumb bar arrive with it; the wBond editor never had any of the three.

### Four things that had to move rather than be deleted, and why each is where it is

- **The wire context menu is on the OVERLAY** (`ILayoutCanvasOverlay.BuildContextMenuItems`, defaulted
  to empty). One shared canvas means one `ContextMenu` and one `Opening` handler — the Layout
  Editor's. A second menu declared by the wBond view would have to replace it, which is how the shell
  got duplicated in the first place. **This is also what gives a wirebond CELL its wire menu in the
  ordinary Layout Editor with no wBond code in that view at all.**
- **`Ctrl+Z` routes by an `EditSequence` stamp, not by focus.** There are two genuine histories now:
  wire snapshots and the layout's command stack. Routing by focus is *wrong* — a WIRE drag happens on
  the LAYOUT canvas — and "wires first" would undo a wire move made ten minutes ago instead of the
  rectangle just drawn. Each recorded entry carries a stamp from one process-wide counter, and undo
  takes the newer. **An undone entry keeps the stamp it was recorded with**; re-stamping on undo would
  make every later Ctrl+Z pick the same history forever. An edit that changed nothing drops its stamp
  with its entry (`WBondViewModel.DropUndoEntry`), or that history would look more recently edited
  than it is.
- **`Delete` is now gated on a non-empty WIRE selection.** The wBond key handler is a *tunnel* handler
  on an ancestor of the hosted view, so its unconditional `e.Handled` would have swallowed every
  Delete meant for a selected shape or instance.
- **The Unit arrow runs both ways.** The wBond metadata bar is gone in favour of the hosted one, so
  the visible picker writes `LayoutEditorViewModel.DisplayUnit` while every wBond readout follows
  `Editor.DisplayUnit`. §6.5 is untouched — its rule is that a wBond is not forced onto the `.ctech`'s
  unit, and it still is not.

### Two traps in hosting a document view inside another document

- **`TornOffFileMenuView` keys off the TopLevel being a `CrfHostWindow`** — which a torn-off wBond tab
  is, so the nested layout half would have shown a second File menu describing a different file.
  `LayoutEditorView.IsHostedInAnotherDocument` suppresses it.
- **A host's overlay must outrank the frame's own.** In the wBond editor the wires are the *document*
  and stay on screen while the user pushes into a sub-cell to nudge the pad under them (WB27); a
  wirebond cell reached from there must not replace them with its own.

### WB27 was unreachable until this landed

`WBondDescent` — the descent transform, the locked-reference-at-depth rule and its refusal path — has
existed since WB-C with **no push-in in the wBond editor to trigger any of it**, so it was reachable
only from its own tests. Hosting gives the editor a frame stack, and `PushDescentChain` is the wiring
that milestone always needed.

### WB40 — a wirebond cell, and where its save hook has to go

A cell folder holding a `.wBond` beside its `.clay` (`WBondCell.FindFor`, one level UP from the
artwork) loads it into `WireDesign` at `WorkspaceViewModel.BuildLayoutSessionVm` — the one funnel both
"open as a tab" and "push in" go through. **The write-back hangs off `LayoutEditorViewModel.MarkSaved`,
not off `PerformSave`**: the workspace saves sub-cell sessions with a bare
`LayoutPersistence.SaveToFile`, so no single writer sees them all. A cell overlay ships with
`WireMarqueeEnabled = false` and an armed-tool check — the opposite of the wBond editor's defaults,
because there the wires are the subject and here the artwork is.

### The measurement, after — and where the subtraction actually is

The wBond view shell was **2,452 lines** (`.axaml` 803, `.axaml.cs` 988, `LayoutTools.cs` 226,
`Selection.cs` 211, `ProfileMenu.cs` 224). After: **1,401** in the editor view itself, **706** in the
profile view and inductance panel — which are now controls **hosted twice**, inline by this editor and
by a dock tool — and **183** in the overlay's context menu, likewise shared. `LayoutEditorView` gained
~100 lines of host surface and the WB40 overlay wiring.

**Deleting the transcribed toolbar alone was −430**; the rest of the phase is roughly flat, because M3
turned two panels into shared controls rather than deleting them, and M4 added ~420 lines of genuinely
new capability (wirebond cells, the two dock tools, the edit-sequence stamp). **Do not read a flat
total as the phase having failed its own §4.3 test** — what §4.3 is about is the SHELL, and the shell
is smaller and no longer duplicated. `EverySurvivingClickHandler_IsWBondsOwn` is the test that keeps a
new transcribed handler from creeping back.

## Full-suite flakes: a WALL-CLOCK BUDGET decides what a counter test observes (2026-08-16)

Owner: *"that same test always fails under load and passed in isolation — do something so that it
doesn't slow us down all the time."* Six tests were failing intermittently under a full
`dotnet test` while passing alone. They are **two mechanisms**, and only one of them is what it
looks like.

**Genuinely wall-clock gates — tagged `Category=Benchmark`, the documented remedy.**
`Hero1BTests`'s 10 s import+solve budget (SPLIT: the correctness half — component and port counts,
reciprocity, passivity — stays in the default gate at ~2 s, only the budget is tagged) and
`PerfBenchmarkTests.BuildRenderModel_10k_Under50ms`. The latter had already been hardened once
(best-of-5 instead of the mean, threshold widened to 500 ms) and flaked anyway, which is exactly the
`Rbf2DPerfTests` precedent in root `CLAUDE.md`: fast, but wall-clock-sensitive, and no statistic
survives the parallel-start burst. **Do not untag either on the grounds that it runs quickly.**

**The interesting four: tests that assert only COUNTERS or POSITIONS, and still fail under load.**
`WBondCanvasTests.ADragFrame_UsesTheIncrementalPath`,
`WBondOverlayTests.ADrag_MovesTheWire_ViaTheIncrementalPath`,
`MKlopfGripAndProfileTests.DraggingTheFarMiddleGrip_MovesTheFarEndCapGripsLive…`, and
`PCellGripSnapAndOverlapTests.DraggingLShort_StopsBeforeTheGeometryFolds`. Nothing in any of them
measures time. **Each sits downstream of a live-degradation budget that does:**

- `QualityLadder.FrameBudgetMs` (16.7 ms) — overrun it and `WBondPointerController.DragFrame` stops
  calling `CommitPointMove` at all, so `IncrementalUpdateCount` stops rising.
- `LayoutEditorViewModel.LivePreviewBudgetMs` (16 ms) — overrun it on a gesture's FIRST solve and the
  drag defers: `PreviewHandles` goes null, so only the dragged grip moves and the overlap guard never
  gets intermediate artwork to stop on.

Both are correct behaviour on a busy machine, which is why the failures look like real bugs and are
not. **The fix is to make the budget unreachable in those four tests, not to tag them out** — what
they pin (a point move must not take the structural path; every grip on a cell moves when the cell
regenerates; the guard stops a fold) is not a statement about machine speed. `WBondPointerController`
and `WBondLayoutOverlay` gained an optional `frameBudgetMs` constructor parameter, and
`LivePreviewBudgetMs` became an internal instance property — **instance-scoped on purpose**, since a
process-wide switch would leak into `PCellHandleDegradationTests`, whose whole subject is a genuinely
slow cell hitting that budget for real.

**The general lesson, worth applying before adding the next such budget:** any test downstream of a
measured-time fallback is a timing test whether or not it contains a `Stopwatch`. Give the budget a
seam when you add it.

**Not fixed, observed once:** `Core.Tests`'s `SpiceCornerDiscoveryTests.C5_AFileThatIsNothingButSectionsIsStillRecognised`
also failed once under load. Different area, different mechanism, untouched by this round — recorded
rather than guessed at.

## wBond editor, round 4 — the drag slip was the quality ladder throwing the drag away; the wire marquee owned every press (2026-08-16)

Twelve owner items. Most were mechanical. The four below have root causes nobody would guess from the
symptom, and two of them are questions the owner asked outright.

### "The cursor slips off the vertex I grabbed" — the LADDER, not the pointer

Fast dragging is the whole tell. `QualityLadder` is fed measured frame times, so it degrades only when
frames overrun, and at `DragQuality.Chord` `WBondPointerController` collapses every MOVING wire onto
its two feet (WB15). Two independent defects follow, both invisible at 60 fps:

- **`RestoreFromChord` put the CAPTURED array back verbatim**, discarding every frame of motion applied
  while the wire was a chord. The wire sprang back to where it stood at the instant the ladder stepped
  down, while the cursor had moved on. It now re-places the interior points by their own chord
  parameter and height above the chord — `ScaleSpan`'s parameterisation — so translate, span-scale and
  rotate all carry through with one rule. Byte-exact short-circuit when the feet have not moved, so
  "a solving shortcut, never an edit" still holds.
- **A collapsed wire has no interior point to move at all.** `WireSelection.MovingPoints` went on
  naming point 3 of a two-point wire, so an interior-vertex drag froze — and indexed past the end.
  `ChordIsFaithful` now skips the collapse for any wire whose moving set is not just its feet; the
  case the shortcut was built for (many whole wires at once) still collapses. `WireEdits.Translate`
  additionally skips an out-of-range index rather than throwing: a selection legitimately outlives the
  point list it was resolved against.

Gate: `WBondRound4Tests.AnInteriorVertexDrag_IsNeverCollapsedOntoTheChord`, **verified to fail with the
guard removed** (7 points became 2). It runs at a 1 ns frame budget, so the ladder is at its most
degraded — anything less proves nothing.

### A cell instance could not be moved because the WIRE MARQUEE had already eaten the press

`LayoutCanvas` offers the overlay every left press first. The overlay's miss branch read *"no wire
here → start a wire marquee"*, which is every press on a pad, a shape or a placed instance. The only
way through was the marquee toggle — a mode switch for something that is not a mode. It now asks
`LayoutHitTest.HitStack` **and** `HitInstanceStack` and declines when either answers; the toggle still
decides who gets genuinely empty space, which is the real ambiguity it was added for. Asking only about
shapes fixes pads and leaves instances exactly as stuck.

An armed LAYOUT tool (the new second toolbar row) takes every press outright, wire or not —
`WBondLayoutOverlay.LayoutToolArmed`. Without it, arming Rectangle draws nothing and starts a marquee.
The two tool states are made mutually exclusive in `WBondEditorView.LayoutTools.cs`, in both
directions; either toolbar can be clicked at any moment.

### A PCell drop was refused with "no workspace is open" while one plainly was

`ResolvePCellCellRef` needs `WorkspaceRootDir`, which derives from `WorkspaceTechDir`, which walks up
from `CurrentLayoutPath` to the nearest ancestor `.cws`. The wBond editor's reference layout HAS a path
— under the recovery session directory, outside any workspace — so the walk found nothing and the
fallback was deliberately skipped (`CurrentLayoutPath is not null` means "a real document with its own
workspace", brief-foreign-documents R-fgn-3). **`LayoutEditorViewModel.IsScratchSurface` is the opt-in
that says "this file is not a document at all"**, and only then is the host's own workspace used. An
ordinary loose `.clay` is untouched and still reads null — that rule is not being relaxed.

The same seam had never been wired at all for wBond: `WorkspaceViewModel.TrackNewWBond` now installs
`WireRetargetSeam` through `WBondDocumentViewModel.ConfigureReferenceLayout`, a hook rather than a
constructor argument because the reference layout is created on demand, replaced on unpack, and set
from three creation points.

### The envelope "disappears when I move the segment too far" — `IsProfileEditable`

Asked outright, and the answer is one method. `ProfileEnvelope.Build` puts a wire in `BoundWires` only
when it follows its array's profile **and** `IsProfileEditable(wire)` — which requires the wire's
points to be MONOTONE in normalised span. Drag one vertex past its neighbour along the chord and the
span goes backwards, the wire moves to `FreeWires`, and the band is rebuilt over what is left. If it
was the array's only bound member, `bound.Count == 0` → no bands → `envelope.Bands.Count > 1` is false
and **no band is drawn at all**. The wire itself keeps rendering, in the free-wire colour.

That is correct behaviour (a band spanning a curve that folds back on itself would be meaningless) and
it is silent, which is the actual complaint. Left as-is this round; the two profile-binding buttons'
tooltips now state what a binding is and that detaching leaves the band. **Do not "fix" this by
loosening the monotonicity test** — the band's whole coordinate is normalised span, and a non-monotone
member has no single height at a given span.

## wBond editor, round 4b — the parameter panel, and the measurement that settles "how much of wBond is a duplicate of the Layout Editor" (2026-08-16)

### `Design` was "gibberish" because it was never meant to be a row

`Design` (the base64 of the whole wirebond design) and `Arrays` (the drift-detection record) are both
documented HIDDEN in `wbond.md` §5.0/§9.2 — and both were rendering as generic text rows anyway. The
fix is a wBond panel in the Parameter Editor, mirroring SnP's: `ParameterEditorViewModel.SetTarget`
filters the four panel-owned parameters out of the generic rows, and the panel shows a SUMMARY where
`Design` was. `Temp` and `GroundPlane` stay generic rows — they are real engine values, and asserting
that in the test is what keeps this a filter rather than a blanket suppression.

**`Pitch` is now `SymbolPitch`** (owner): on a wirebond component "pitch" reads as the WIRE pitch, the
centre-to-centre bond spacing. SnP has no such collision and keeps the short name.

### The external reference pin: WB20 said mandatory, and what WB20 protects is elsewhere

`REF` is now optional and **off by default**, matching SnP's `RefNode`. §5.4/WB20 wrote it as
mandatory — *"the UI does not permit a port without one"* — but what WB20 actually protects is
`WBondModel.RefuseIfReturnPathUndeclared`, and **that keys off `GroundPlane.Enabled`, not off the
pin**. `REF` never stamped. So an undeclared return path is still refused by name either way, and
nothing about the physics moved.

Two facts make the flag safe, and both are worth keeping: **`REF` is the LAST terminal**, so removing
it renumbers nothing; and the symbol generator and `ComponentModelFactory` read the **same `RefPin`
instance parameter**, so the pin count and the port count cannot disagree. `RefPin` is therefore the
one wBond artwork parameter NOT filtered out of the extracted netlist — `Arrays` and `SymbolPitch`
are.

### An added array carries one wire, deliberately

The array editor answers "there's no way to add new arrays". A new array arrives with **one default
wire**, offset from the ones already there. Not empty, because `WBondDesign.Validate` refuses an empty
array (rank-deficient mapping matrix, singular array-basis inductance) — a schematic that could
declare one would place a component that cannot be simulated until someone visits another editor. And
offset, because two wires at the same place have infinite mutual coupling. Reordering is deliberately
NOT offered: pin order IS array order (§9.2/WB35a).

### The measurement: the ENGINE is shared, only the SHELL is duplicated

Worth writing down because the intuition runs the other way. Counted:

| | lines | copies |
|---|---|---|
| `LayoutEditorViewModel` (+10 partials), `LayoutCanvas`, `LayoutSnapQuery`, `LayoutHitTest` | ~9,500 | **one**, used by both editors |
| `LayoutEditorView` shell (XAML + code-behind) | ~1,750 | Layout Editor only |
| `WBondEditorView` shell (XAML + code-behind) | ~2,690 | wBond only |

The wBond editor's layout half **is** a `LayoutEditorViewModel` inside a `LayoutCanvas` — the same
objects, not a port of them. So a fix to snapping, hit-testing, rendering, the commands or the undo
stack lands in both editors automatically, and always has. What is duplicated is the **view shell**:
the toolbar, the keyboard routing, the context menu, the breadcrumbs, the focus handling.

**That is exactly where round 4's new geometry-tool bugs live**, and it is why they read as "the
Layout Editor's hardening was thrown away" when the hardening is all still there, one layer down. The
fix is not to re-fix the tools; it is for the wBond editor to HOST `LayoutEditorView` (over a
`LayoutDocument` wrapping its own reference layout — that constructor already exists) instead of
transcribing its toolbar. Do not fix the transcribed row bug-by-bug.

## wBond editor, round 3 — an empty group is illegal, copy was text-only, and visibility was a function of the selection (2026-08-16)

Nine owner items. Six were straightforward; the three below cost real time and would cost it again.

**An EMPTY wire group is not legal, and four call sites believed it was.** `MoveWireToGroup`,
`DeleteSelectedWires` and (as written) the new `DeleteWire` all carried the same comment: *"the empty
source group is LEFT in place — a group is a named terminal (§3.4), and moving the last wire off a pin
is not the same statement as deleting the pin."* It is a good argument and this layer cannot honour
it: **`WBondDesign.Validate` rejects an array with no wires outright** — a group with no wires is a
zero row in the mapping matrix, so the array-basis reduction is rank-deficient and the reduced
inductance singular. The failure mode is the expensive part: the edit runs, `CommitStructuralChange`
rebuilds, `Validate` throws, `RefuseEdit` rolls the whole thing back, and the user sees the command
**do nothing** while a message about a singular inductance matrix appears in the toolbar strip. It
looks like a physics problem and is a bookkeeping one. `WBondViewModel.PruneEmptyGroups` is now called
by every edit that can empty a group, and it deliberately stops at one array — because `Validate`
refuses a design with *no* arrays too, which is the same rule from the other end and is why
`WhyCannotDeleteWire` refuses the last wire in its own words rather than letting that message escape.

**Presence must be a function of geometry; colour is the function of selection.** §6.2 idea 3's
clutter rule ("one editable curve per array plus a translucent band") was implemented in
`WBondRenderer.DrawProfile` as *hide every bound member but the representative unless the selection
touches it* — with no geometric test anywhere in it. So a group whose members differ in shape or
position drew one of them, and the rest materialised only when a marquee caught them: the owner's
*"some previously invisible wires become visible… I don't like having wires appear to disappear
depending on wire selection."* The fix is a coincidence test (`ProjectsOnto`, compared in **projected**
(span, z) rather than world coordinates, because the projection is exactly what differs — two wires
5 mil apart are one curve under AUTO and two curves in the YZ plane).

**The selection still has to be consulted, and the pixel test is what proved it.** The obvious version
of the fix — drop the selection from the visibility test entirely — turns red on
`WBondEditorRound2Tests.TheProfileView_AccentsSelectedPointsOfABoundMember`, and rightly: under AUTO a
same-shape array's members genuinely coincide, so with the selection out of the test a marquee over
one of them highlights nothing at all. The rule that satisfies both reports is *skip a member only if
it coincides **and** is untouched*: drawing a coincident member adds no curve anywhere, it recolours
pixels already on screen, so nothing "appears". Do not simplify this back to a pure geometry test.

**wBond's Copy had never written anything but text.** `clipboard.SetTextAsync(json)`, full stop —
which is why pasting into PowerPoint or Keynote produced raw JSON or nothing, while the separate
Shift+⌘C "Copy as Graphic" worked. `WBondClipboardWriter` is a deliberate transcription of
`LayoutClipboard.CopyAsync`, not a new design: content-framed page from what is actually PAINTED,
PDF/SVG/PNG best-effort with the JSON always present as the fallback, and **the Windows bypass** —
one P/Invoke session, CF_ENHMETAFILE first, because Word and PowerPoint take the first format they
recognise. See `WindowsClipboard`'s header for why a second Avalonia clipboard session fails on
Windows. The layout half of a mixed paste now moves by **(0, dy)** rather than (dy, dy), so it stays
on top of the wire half.

**Follow-ups the owner found on the built round.**

- **The clipboard picture clipped its own points, worst on a straight wire.** The content bbox is of
  the wire POINTS; what is drawn at each is a dot of `theme.DotRadiusPx` and a stroke of
  `theme.LineWidthPx`, both in SCREEN pixels that no world-space bbox knows about. Two compounding
  causes: no pixel allowance for the glyph, and a pan derived as `MinX − W·Pad` — which equals
  centring only while the page is exactly the padded content size, and it never is, because the two
  axes share one zoom and each page dimension is clamped to an 80 px floor. **A north/south wire has
  W = 1 DBU**, so its page clamped up to 80 px wide while the pan still said "start 0.15 DBU left of
  the wire", putting the wire on the left EDGE with its dots hanging off. Fixed by reserving a
  `GlyphMarginPx` before choosing the zoom and by **centring on the content**, which makes both the
  shared zoom and the clamp harmless. Note `WBondGraphicExport.FitViewport` (Shift+⌘C) never had this
  — it already centres, against a fixed page with a 6 % margin ≈ 47 px.
- **A pasted north/south wire landed end-to-end with its original.** The offset was hardcoded to +y.
  A bond array is pitched PERPENDICULAR to its wires — that is what a pitch is — so the step now runs
  across the mean chord azimuth of the payload: east/west steps +y (what it always did), north/south
  steps +x, and a wire at 37° steps at 127° rather than being forced onto an axis it does not lie on.
  Two details worth keeping: the chords are summed as **vectors folded onto a half-plane**, or two
  anti-parallel members of one array cancel out of the mean and leave a direction perpendicular to
  neither; and the perpendicular is **canonicalised to face east, or north when purely vertical**, so
  the copy lands to the right of a north/south wire rather than to its left. Off-axis the offset
  cannot be exact — it rounds to integer nm like every wBond coordinate — so a test of its length
  needs a ±1 nm tolerance, not `Assert.Equal`.

**Naming, at the owner's request (2026-08-16).** Role keys are `wBond.*` with a lowercase w (the
product's own spelling), and **the Settings colour list shows every role under its full key**. The
schematic dozen used to be shortened there — `Schematic.Wire` as "Wire", `System.Warning` as
"Warning" — from when they were the only roles that existed; every family added since shows its
prefix, so the short ones read as a nameless group and three different colours all appeared as
"Wire". Deleting the `RoleLabels` map was the whole change; the row label already fell back to the
key. No migration was asked for and none exists: a `.ccolor` holding stale `WBond.*` keys loads
fine, those entries match no role, and `ColorTheme.Resolve`'s built-in fallback answers — and every
`ColorThemeIo.LoadFile` call in `ThemeResolver` was already inside a `catch`, so an unreadable file
cannot crash the app either.

**Two smaller traps.** (1) The wBond canvases drew `WBondRenderTheme.Fallback` in light and dark
alike — no `FromTheme` existed — so "the light selection colour is too pale" was a **wiring** bug, not
a tuning one: nothing was reading a light palette at all. `Fallback` is now the built-in *dark*
projection rather than a private copy of it, so the two cannot drift again. (2) `WBondViewState` must
serialise **nulls explicitly** now that the default plane is YZ: null means AUTO *and* means "key
absent", and with a non-null default a design deliberately left on Auto reopens in YZ.

## wBond editor, round 2 — the toolbar, the arrangement, and four gesture rules that were quietly inverted or absent (2026-08-16)

**Two invariants worth keeping in mind before touching either canvas** (they were written into
`src/Ui/CLAUDE.md` in round 1 and moved here):

- **A `LayoutCanvas` overlay clips ITSELF, and is handed the layout's own `LayoutRenderTheme`.**
  Nothing else clips the overlay pass — the layout underneath is culled against the viewport before it
  is drawn, but an overlay draws whatever it holds, on screen or not, and `LayoutCanvas` sets no
  `ClipToBounds`. `ILayoutCanvasOverlay.Draw` takes the theme so shared visual language (the selection
  accent above all) cannot drift from the layout's own.
- **A wire edit repaints through `WBondViewModel.ReadoutChanged`, and BOTH canvases must listen** — a
  wBond edit deliberately never touches `LayoutView.Changed` (WB23/WB17), which is the only thing that
  repaints `LayoutCanvas` on its own. A SELECTION change raises neither, so it needs its own
  subscription; without it, clicking empty space in the layout view left the same wires drawn as
  selected in the profile view.

**The profile view's absolute span axis had its origin on the wire's own input foot, and three
owner-reported bugs were that one fact.** `Points[0]` sat at span 0 permanently: it could not move in
that view whatever happened to it in the world, and any motion of it was rendered as motion of
everything ELSE. So an alt-drag anchored on the output foot DREW the output foot moving — while the
layout view drew the truth, which is why the two views disagreed about the same gesture — and a plain
drag of the start point left it glued in place while the rest of the curve slid out from under the
cursor ("regular click-drag of the start of a wire is changing span"). Absolute now measures from the
WORLD origin, along the wire's own chord direction under AUTO and along the view direction under a
fixed plane. **`SpanMode.Normalised` keeps the foot-relative 0..1 origin** — that is the
shape-comparison mode §6.2 argues for, and it still overlays wires of any angle and length; Absolute's
whole stated purpose is "true geometry", and a true picture cannot re-origin itself on the point being
dragged. The envelope band had to follow: it carries a NORMALISED span, so it is mapped onto the
reference wire's projected origin AND extent, not just its length. Separately, the profile canvas's
alt-drag was missing the anchor SIGN FLIP the layout overlay has carried since it was written — pulling
the input foot backwards along the axis is what lengthens a wire.

**`LayoutCanvas.ZoomToFit` unions the layout's shapes and instances — and an overlay's content is in
neither.** A wBond document on an empty scratch layout therefore fitted to an EMPTY extent and landed
at `LayoutViewport.Default`, with every wire off screen. `ILayoutCanvasOverlay` now declares
`ContentBounds()`, in the canvas's own DBU (the nm→DBU bridge crossed there, and the descent transform
applied first — framing untransformed coordinates would frame a place the wires are not). Note that a
wire-less design is unreachable: `WBondDesign.Validate` refuses both an empty array and an array-less
design, because either makes the array-basis inductance singular. The one reachable empty case is
depth with an uncomposable chain (WB27), where the wires are deliberately not drawn.

**The layout renderer accented whole WIRES and nothing finer**, so a segment picked in the profile view
lit up there and showed nothing in the layout view — and a picked INPUT foot lit up nowhere at all,
because the input-end colour (WB3) outranked the accent unconditionally. Both views now share one
`SegmentSelected`/`PointSelected` pair, and a selected point outranks the input-end colour: the accent
is transient and says what the user is holding, and the end is still identifiable while selected
because it is still the wire's first dot. The per-kind nature of this defect is why it kept resurfacing
— whole-wire selection always worked, which is exactly what hid it — so the guard is a pixel oracle run
over all four kinds (wire, segment, interior point, input foot) in BOTH views.

**A live marquee's contents belong on the shared view-model, not inside the canvas that owns the
gesture** (`WBondViewModel.PreviewSelection` / `EffectiveSelection`; every renderer reads the latter,
none reads `Selection` to draw). A wire caught by a box dragged in the profile view is the same wire in
the layout view and has to light up in both. Two more things had to change for the profile half to
show anything at all: **it accented whole WIRES only**, so an enclose marquee — whose whole job is
catching some of a wire's vertices — appeared to select nothing; and it **skipped every bound member
but the representative**, so a marquee catching members of a twenty-wire array highlighted nothing. A
selected wire is now always drawn individually, because the band is one shape over the whole array and
cannot carry a highlight. A counter cannot tell "drawn" from "drawn highlighted", so that one is
guarded by a pixel oracle.

**A press must not resolve the selection, and a press must not open a gesture.** Three separate
owner-reported defects were the same two lines of `OnPointerPressed` in each canvas:

- *"Clicking on the selection starts a new selection."* The press re-resolved the hit unconditionally,
  so grabbing three selected segments to move them collapsed the selection to the one element under
  the cursor and dragged only that. Now a press on something the selection already covers **keeps** it
  — and, so an element inside a selected wire stays reachable, a gesture that turns out to be a plain
  click re-resolves on RELEASE (`_deferredPress`). That click-through is why
  `HoldingW_PromotesAClickToTheWholeWire` now has to release before asserting.
- *"Clicking on the start point of a wire changes the span."* The press opened the gesture immediately
  and the first move measured its delta from the UNSNAPPED press point — so a click with a pixel of
  hand-shake snapped the grabbed foot onto the nearest pad corner, and a moved foot is a changed span.
  Two fixes: the drag baseline is the **snapped** press point, and nothing happens at all until the
  pointer leaves the hit tolerance (`_dragThresholdNm`, `DragThresholdPixels`). A click therefore also
  leaves no undo entry, which it previously did on every single click.

**The alt-drag anchor was inverted, and the double negative is where it went wrong.** WB26a's rule is
"grabbing near an end IS the instruction to move that end". `ScaleSpan` takes `moveOutputFoot`, so the
helper must answer *which foot moves* — a first version answered *which foot was grabbed near*, and an
alt-drag on the output end pulled the INPUT end, shrinking the wire when the hand said grow. The
helper is now named `GrabMovesOutputFoot` for that reason. **Alt-drag also now scales span AND height
together, every frame**: the old code declared one axis on the first few pixels of travel and ignored
the other for the rest of the gesture, so a diagonal alt-drag silently did half of what it looked
like. And it **works on a detached wire** — it used to look up the selection's bound profile, find
none, and do nothing while saying nothing (`WireEdits.ScaleWires` / `WBondViewModel.ScaleSelection`).
**Alt-drag in the LAYOUT view scales span too**, which it never did; the displacement is projected onto
the wire's own chord, so a drag perpendicular to the wire correctly changes nothing.

**The profile view's plane is now a SETTING, not a derivation.** Round 1 labelled the plane it happened
to be showing; the owner's answer is that a user needs to *choose* it. `ProfileProjection.Project`
takes an optional azimuth: null is AUTO (each wire on its own chord — §6.2's parameterisation, and the
only mode in which two wires of different angle are comparable), a number fixes the plane. Under a
fixed plane a wire running perpendicular is foreshortened to **nothing**, which is what looking down a
wire actually looks like and is why AUTO is still the default. `ProfileAxisSetting` owns the text round
trip so the combo, the persisted view state and the parser cannot disagree ("90" reads back as "Y-Z").
The **band** had to be projected too: it carries normalised span, so it is scaled by the reference
wire's extent *in the current projection* — reading the plain chord length would leave the band at full
width in a plane the curves are foreshortened in.

**The profile view got its own marquee, and it is resolved against span and z**, not world x —
`SelectionResolver.ResolveMarquee`'s `spanOf` hook exists for exactly this and was previously unused.
Live preview, kept separate from the committed selection, same as the layout side.

**A `LayoutView`'s `SnapDbu` defaults to ZERO, and zero means "no grid" to `LayoutRenderer` as well as
"no snapping" to the editor** — which is why the wBond layout view drew no grid at all. A reference
layout attached to a wBond document now gets 1 mil if it has none (`OnReferenceLayoutChanged`). The
metadata bar's Snap box is the reference layout's OWN ladder and its own three handlers, bound
straight through; it sets the grid pitch for **both** canvases and the fallback wire-point snap
(geometry first, grid second — a grid that overrode a pad corner would pull the foot back off the pad).
The profile canvas reuses `LayoutGridMath.ComputeGridPitch` rather than `LayoutRenderer.DrawGrid`,
which is bound to a `LayoutView` and a `LayoutViewport` this view does not have: the part that can be
*wrong* is the decimation, and that is the part that is shared.

**Hiding a focused control orphans the focus, and this view's key handler is gated on
`IsKeyboardFocusWithin`** — so cycling away from the canvas the user was working in left the editor
deaf to its own shortcuts until they clicked something ("pressing V repeatedly does not cycle unless I
click on a canvas between keystrokes"). `ApplyArrangement(restoreFocus: true)` puts focus back on a
canvas that is still on screen, and only ever when focus was already inside this view, so it can never
yank focus out of a field elsewhere in the application. **The cycle key is `V`, not `Tab`**: Tab is the
focus-navigation key every Avalonia control expects, so claiming it would have to out-race the focus
manager in every host this view is embedded in, and would leave keyboard users unable to walk the
toolbar.

**The Snap box is formatted in the LAYOUT's `DisplayUnit`, which defaults to microns** — so a document
set to `mil` offered a snap ladder in µm right beside a Unit box saying mil. The editor's chosen unit
is now mirrored into the reference layout (`PushDisplayUnitToReferenceLayout`), which carries the
ladder, the snap text, the cursor readout, the extent and Zoom 1:1 with it. §6.5's "independent of the
`.ctech` display unit" is untouched: that rule is about the wBond not being FORCED to follow the
technology's unit, and the arrow here still points the other way. The two enums list the same five
units in the same order, and the mapping is written out rather than cast for exactly that reason — an
ordinal cast would keep compiling and start lying the moment either gains a member.

**The snap ladder gained two SUB-unit rungs** (`SnapLadderMultipliers` is now
`0.1 · 0.5 · 1 · 5 · 10 · 25 · 50`, in `decimal` because a `double` cannot hold 1 mil = 25,400 nm
exactly). It stays R-snp-2's RELATIVE ladder — multiples of the technology's own default snap — so on a
1 mil process the new rungs are the "0.5 mil" and "0.1 mil" the owner asked for, and on any other
process they are the same fractions of its step. A rung that quantises to zero DBU is dropped: zero is
`LayoutSnapping`'s OFF state, not a fine snap, so offering it in a distance list would be a trap.
**This changes the Layout Editor's ladder too**, deliberately — it is one control with one rule.

**A new wBond document snaps at 0.1 mil off a 1 mil LADDER, and those are two separate statements.**
With no technology the ladder falls back to the document's own snap, so seeding the snap to 0.1 mil
would have re-based the whole ladder and offered a 0.01 mil finest rung. `SnapLadderBaseDbu` is the
explicit base a host can state; it is a base and never a selection, so R-crash-1's "an items collection
must not be a function of the selection made from it" is untouched. `RefreshSnapLadder()` exists
because the layout view-model's constructor builds the ladder before the wBond document has seeded
anything — without it the editor kept offering the µm-scale fallback rungs all session.

**View arrangement persists in the `.wBond`'s own opaque `ViewState` field, with NO format-version
bump** — that field exists so the UI can persist what the framework-free half must not parse, and an
older build reads the string, understands none of it and writes it back unaltered. Every field is
optional and malformed JSON takes the defaults: a view setting is never worth refusing to open a design
over. **The row/column collapse is written from code-behind, not bound**: a `GridSplitter` writes a
concrete `GridLength` straight into the definition it resizes, silently replacing any binding on that
property, so a bound collapse would work exactly until the first time the user dragged the splitter.

**Panel:** lengths now carry per-unit precision (`Decimals`) chosen so one digit is worth roughly the
same physical amount in every unit — mil pinned at one decimal per the owner, pH likewise. The card is
name + inductance + expander, collapsed by default; the "redundant pH readout" was the SELF term being
listed again among the mutuals, so only that entry is dropped and cross-array mutuals stay (under the
fold). The return-path line is suppressed for the ordinary image plane at z = 0 — a sentence that says
the same expected thing on every document costs a row and tells nobody anything, while the UNDECLARED
case WB20/RW13 exists for is unconditional.

## wBond editor — eleven owner-reported defects, and the two that were the same bug wearing two faces (2026-08-16)

**The profile view's "one editable curve per array" was never drawn — only the band was.** `wbond.md`
§6.2 idea 3 is *one curve plus a translucent min/max envelope*; `WBondRenderer.DrawProfile` drew the
envelope and `continue`d past every bound member. The envelope is a min/max over the bound members, so
**whenever those members share a shape, min == max at every sample and the band is a zero-area path
that fills nothing.** Two ordinary situations hit that: a ONE-WIRE array (the shipped default
document), and *any* array mid-drag once `QualityLadder` has collapsed its members onto their chords
(WB15) — which is exactly the owner's "the profile view sometimes disappears while dragging wire
segments in the layout view". The fix draws `envelope.BoundWires[0]` as the representative curve in
`theme.Wire`. **A counter test cannot see this**: the old code emitted a path, it just filled nothing,
so the guard is a rendered-pixel oracle on a single bound wire.

**Nothing clipped the wire pass.** The layout underneath is culled against the viewport before it is
drawn; `WBondRenderer.Draw` iterates *every* wire in the design regardless of where it lands on
screen, and `LayoutCanvas` sets no `ClipToBounds`. So a wire off the left edge painted straight across
the inductance panel docked beside the canvas. `WBondLayoutOverlay.Draw` now saves/clips/restores
against `viewport.Width`/`Height`. Verified by removing the clip and watching the test go red.

**A Properties-panel edit repainted the profile canvas and not the layout canvas**, because only
`WBondProfileCanvas` listened to `WBondViewModel.ReadoutChanged` — the overlay only ever raised
`OverlayChanged` from its own gestures, and the layout canvas repaints on `LayoutView.Changed`, which
a wire edit deliberately never touches (WB23/WB17). Reported as "changing the Span takes seconds; Loop
ht is fast", and the asymmetry is the tell rather than the cost: **the model path is ~0.05 ms per
commit, measured, and Span and Loop height are within noise of each other** — span's visible effect is
in the layout view (a foot moves in XY) and loop height's is in the profile view, which was already
repainting. `WBondEditorView` now subscribes `ReadoutChanged → RepaintBoth`.

**The profile view's horizontal axis now moves geometry, and the mapping is the chord.** A plain drag
used to be z-only, on the stated grounds that span is derived and "move this point sideways" has no
single answer. It does: displacement **along that wire's own XY chord**. `WireEdits.Translate` owns
it, so the drag and the arrow-key nudge got it together, and a wire with coincident feet in XY is
skipped rather than guessed. The old code's profile `dx` was applied as world x, which for any wire
not already parallel to x moved the point *off* its chord and barely changed its span.

Smaller, but each a real defect: the panel's Total length / Landing span were **hard-coded to mm**
(now `WBondPanelViewModel.Unit`, pushed from `Editor.DisplayUnit`; inductance deliberately stays pH
per WB27a/D9); the toolbar unit picker showed the bare enum (`Mil`, `Um`, `Inch`) and now shows the
**suffix strings themselves**, so the picker offers exactly what `WBondUnits.TryParseUnit` accepts;
the marquee's fill alpha, hairline stroke and dash period are now transcribed from
`LayoutRenderer.DrawMarquee` and its colour is the **same `LayoutRenderTheme` object the layout
underneath was drawn with** — `ILayoutCanvasOverlay.Draw` takes the theme for that reason; the marquee
highlight is live, with the preview kept **separate from the committed selection** for the reason L1i
already established (the committed selection is also the Shift-base, so a self-writing preview can
never shrink), and preview and commit share one `WBondPointerController.ResolveMarquee`.

**Escape belongs to the VIEW, not the canvases.** `WBondLayoutOverlay.OnKeyDown` could already cancel
a half-placed wire and clear a selection — but it cannot un-press a `ToggleButton`, so the tool stayed
armed, the next click started another wire, and Escape read as doing nothing. `WBondEditorView`'s
tunnel handler now unwinds one step at a time: disarm Draw/Rotate (which cancels a half-placed wire
through `WireDrawArmed`'s own setter) → clear the selection → leave the key **unhandled** so an
ancestor still sees it.

**The profile view states its plane** (`ProfileProjection.AxisLabel` → `WBondViewModel.ProfileAxisLabel`):
X-Z, Y-Z, or the azimuth for a diagonal array. The layout view needs no counterpart — it is always
X-Y. An axis is named only within 5°; rounding 45° to "X-Z" would be a plausible-looking wrong answer.
Rendered as a `TextBlock` over the canvas, not Skia text, to stay clear of the headless-typeface trap.

## Round 11 — a K edit must not invent a marker, and per-band menus need their own signal (2026-08-15)

`HarmonicaViewModel.RetargetTerminations` used to rebuild the marker list wholesale, which applied
§4.2's "S1/L1 are always present" rule and so **made an S1 marker appear whenever HB Order was
edited**, on a document that deliberately had none (R8B §3: S1/S2 start with no marker). It now prunes
`Markers` instead — dropping only bands above the new K, keeping the surviving instances and the
session state hanging off them. The load path still rebuilds wholesale, and must: a loaded `.charm`
has nothing but its terminations to reconstruct markers from.

**The coupling that broke when it was removed, and the reason this is here.**
`HarmonicaMenuViewModel.RebuildBandMenus` learned about a K change ONLY by observing
`Markers.CollectionChanged` — its own doc comment called that "one signal, three lists". It worked by
accident of the wholesale rebuild, so removing the rebuild broke *raising* K (which drops no marker
and notified nobody) while lowering K still worked, and the failure surfaced as a Contour Harmonic
menu stuck at 3 items. K now raises `HarmonicaViewModel.HarmonicCountChanged` in its own right.

**Ctrl/⌘+L toggles Display ▸ Grid Points, and the two modifiers live on DIFFERENT surfaces on
purpose.** ⌘L is a `Gesture` on both NativeMenu surfaces (`HarmonicaAppMenuInjector` and
`HarmonicaMenuView.axaml`); Ctrl+L is a `KeyBinding` installed on `HarmonicaView` alongside the menu
view model. A macOS menu key equivalent is consumed by AppKit before Avalonia's input pipeline runs,
so declaring the same gesture on both surfaces would give one keystroke two live handlers and toggle
the setting twice — i.e. do nothing. The in-window `MenuItem`'s `InputGesture` is display-only in
Avalonia (`HotKey` is the functional one), which is why it can safely label Ctrl+L without becoming a
second handler for it.

## Round 10 follow-up — current probes, node labels and a PA measurement block on the exported testbench (2026-08-15)

The owner supplied a hand-drawn testbench (`Example.csch`) showing the shape wanted: `IProbe`s in the
signal and bias paths, net labels on the four interesting nodes, and `MEAS` blocks whose equations
name them. The export now writes all three, and the exported schematic reports Pout / Gp / Gt / IRL /
Zin / Idc / Pdc / DE / PAE on its own.

### The orientation question, which is the whole of the risk

An `IProbe` reports the current flowing `np → nm`. Its pins are local `(0, +100)` and `(+100, +100)`,
so **`np` sits at the component's own X in BOTH mirror states** and `MirrorX` is what decides which
side `nm` lands on — that asymmetry is what makes the placement arithmetic a single case. Insert one
backwards and every derived number keeps its magnitude and flips its sign, which is exactly what the
owner warned about. Four probes, and they are NOT all oriented alike:

| probe | measures | orientation |
|---|---|---|
| `Iin` | current into the DUT's gate plane | np left (chain is built outward, power flows inward) |
| `Iout` | current out of the DUT's drain plane | np left (chain and power both run outward) |
| `IDC` | current leaving VDD, which sits RIGHT of its choke | np right — **mirrored** |
| `Igate` | current leaving VGG, which sits LEFT of its choke | np left — **not** mirrored |

`PlaceProbe` takes `currentAlongTravel` rather than a mirror flag, because "does the current I want run
the same way I am building this chain?" is what a caller actually knows.

**The example file mirrors `Igate` as well as `IDC`**, which makes its own gate term negative. Ours
does not, deliberately: both probes measure current OUT of their own supply, so `V(supply)·I(probe)` is
power DELIVERED on both sides with no sign correction anywhere. **That term is not negligible on the
shipped device** — its gate is a plain 50 Ω to source, so at Vgs = −3.05 V it draws −61 mA and the
negative supply really delivers +0.186 W. Dropping or flipping it moves DE by about 1.6 points.

### Verified end to end, through the product's own path

`HarmonicaExportedMetricsTests` extracts, elaborates, runs `HbEngine` and evaluates the block through
`MeasurementEvaluator` — the same four steps `Cli hb` takes. At the shipped default, 20 dBm, load
80 + j10 Ω:

```
Pin_avail 20 dBm · Pin_deliv 0.1 W (20 dBm) · IRL −4e-10 dB · Zin 50.000 + j2.0e-7
Pout 5.545 W (37.44 dBm) · Gp 17.44 dB · Gt 17.44 dB
Idc 0.2367 A · Pdc 11.543 W (= 48·0.2367 + (−3.05)(−0.061)) · DE 48.04 % · PAE 47.17 %
```

IRL is identically zero because the source presents 50 Ω and the DUT's gate IS 50 Ω — which also means
a reversed `Iin` would give a NEGATIVE `Pin_deliv` and `10*log10` would throw rather than land on 0 dB.
**And the cross-check that matters:** the exported schematic's own `Zin` measurement (a stamped
netlist, solved by `HbEngine`, read through an `IProbe`) agrees with harmonicaRF's own closed-form
termination closure to **~1e-11** — two genuinely different routes to one number.

### TWO ROUTER BUGS THIS SHOOK OUT, both silent, both found by the 3-port SDD

- **`AddComponent` registered a component's pins BEFORE its mirror was applied.** `MirrorX` used to be
  set by the caller afterwards, which was harmless while nothing here was mirrored — `IProbe` is the
  first. The result is a phantom obstacle at a coordinate no pin occupies AND no obstacle at the one
  that does. `MirrorX` is a constructor argument now, so the two cannot get out of order.
- **The staircase's escape leg travels along the very axis the obstacle sits on**, so it only worked
  while the obstacle was further from `a` than the step. Put a component pin one grid step short of a
  grounded DUT terminal — routine once a probe is inserted on a 3-port SDD, where two ports share one
  column and the third sits 200 units off the chain — and every candidate either lands ON the obstacle
  or crosses it on the way. All were rejected and `ConnectStraightSafely` fell back to the direct wire
  it was trying to avoid: **a short**, whose only symptom is a `SingularMatrixException` from the
  engine with nothing in the drawing to point at. `EscapeCandidatesDbu` now leads with **0** — turn
  perpendicular immediately, an ordinary Z-bend, which needs no room along the blocked axis at all —
  and ends with the negative half as a last resort. `TryStaircase` became `TryRoute`, a general
  point-list route that collapses consecutive duplicates first, which is what lets the zero-escape
  candidate degenerate gracefully instead of failing its own midpoint check against `a`.

## Round 10 — the `.csch` export rebuilt, the VSWR circle unrestricted, the intrinsic glyph un-compressed (harmonicaRF fixes, 2026-08-15)

### The `.csch` export carried a SIGN INVERSION on both supplies, and nobody could see it

`HarmonicaSchematicExport.PlaceTerminationTail` grounded the `Vdc`'s **pin 0** and fed the bias choke
from **pin 1**. Pin 0 is the "+" terminal (`BuiltInSymbols.BuildVdcSource` draws the `+` marker at
local y = −100; `VdcModel.Stamp` constrains `V(Nodes[0]) − V(Nodes[1]) = Vdc`) — so **every schematic
this exporter ever wrote put −Vgs on the gate and −Vds on the drain**. It was invisible because the
only gate the export had was "does it extract, elaborate and converge", and a sign-flipped bias
converges perfectly well; it just answers for a different amplifier. Found while implementing the
owner's §12 (a wire drawn straight down through the Vdc symbol), which is the same code. The supplies
are now placed to the OUTSIDE — gate supply left, drain supply right, both one pitch above the choke —
so the "+" wire leaves the pin sideways before turning down, and the "−" is grounded where it stands.
`HarmonicaSchematicExportR10Tests.BothSupplies_FeedTheirChokeFromThePlusPin_...` pins both halves.

### A Tuner under a plain `type=hb` run presented `Z[1]` AT EVERY HARMONIC — fixed in the engine

The owner asked for the load to be a `LoadTuner` named `Load` (§8) instead of the tone-less `PnTone`
this file used to write. That exposed a live engine defect: `TunerModel.GetZ` takes its "S-param mode"
branch whenever `_toneFreqHz <= 0`, and `_toneFreqHz` was only ever set by
`LoadpullEngine`/`LoadpullPursuitEngine` (`SetTone`) — `HbEngine.Run` configured `P1Tone`/`PnTone` tone
context and **nothing else**. So *any* Tuner on an ordinary HB testbench declared `Z[2]`, `Z[3]`… and
had them quietly ignored; it ran, converged, and answered for a different circuit. This is not new to
the export — it has been true of every hand-written `type=hb` netlist with a Tuner in it.

**Fixed** (owner-approved) by `HbEngine.GiveTunerItsBandRuler`, called from the same tone-context loops
that already configure `P1Tone`/`PnTone` in both `Run` and the two-tone path. Two things make it safe:
it is **role-gated to `Load`** (a Source-role tuner's `StampSource` stamps a `V_1Tone` branch as soon as
its tone is set, at a `|Vs|` only `SetSourceDrive` computes — so an unconfigured one would stamp a
0 V source, i.e. a SHORT where there was an open; nothing outside the loadpull engines assigns a role,
so every tuner on a plain-HB testbench is already `Load`), and the loadpull path goes through
`RunSinglePoint`, which has **no** tone-context pass of its own.

**Measured both ways** (`TunerPerHarmonicZInPlainHbTests`, a square-law SDD into a Tuner whose `Z[2]`
is the only thing that varies): *without* the fix the second-harmonic load voltage is `5.0000E-002` for
`Z[2]` = 1e-6, 50 **and** 1e6 Ω alike — bit-identical, all three solved at `Z[1]`. *With* it the
implied `I₂ = |V₂|/Z[2]` is **1.0000e-3 A across twelve decades of `Z[2]`**, i.e. the tuner presents
exactly what it declares. (The 1 MΩ case reads 2 ppm low because the ideal 1 H choke is 25 GΩ at 4 GHz
and no longer utterly negligible next to it — stated in the test rather than tolerated silently.)

### The rest of the rebuild, and one trap that only a 3-port SDD can spring

- **`Num` was `"G17"`** — round-trip-safe by brute force, printing all 17 digits whether or not they
  carry information (`1e-6 H` came out `9.9999999999999995E-07`). It is `"R"` now, which has meant
  *shortest* round-trippable since .NET Core 3.0, plus an exponent tidy (`1E-06` → `1e-6`). No value
  changed; only how much of it is written down.
- **The bias network's L and C carry a unit whose SI PREFIX is chosen from the magnitude**
  (`Engineering`). A fixed `uH`/`uF` pair — the literal request — reads well at microscale and badly
  anywhere else: the shipped default is now the ideal 1 H / 1 F, which a fixed micro prefix would write
  as `1000000 uH`. The clean single digit the owner asked for is the request; the prefix is the means.
- **A ground now sits EXACTLY on the pin it grounds**, with no wire and no offset search — a Ground's
  one pin is at its own origin, so the coincidence rule `NetExtractor` already applies unions them.
  The SDD's "−" terminals each get their own symbol rather than sharing one through a wire.
- **A series element is oriented along its own run** (R90 for a left/right run, R0 for up/down), which
  is what makes the DC blocks horizontal (§7) and removes the L-bend a chain used to need to reach a
  component lying across it.
- **`BiasTee` must be written QUOTED (`"off"`), not bare.** A `.cnl` says `BiasTee=off` and a schematic
  parameter is an *expression*, so a bare `off` resolves as a variable name and elaboration fails with
  `Unresolved name 'off' in scope 'global'`. `CreateTunerModel` wants a `ValueKind.String` and only ever
  compares it to `"on"`. (This also means the registry's own `DefaultParameters` spelling for a
  hand-placed Tuner has the same shape — worth knowing before "fixing" the quotes.)
- **THE TRAP: `CoincidesWithWireInterior` cannot see a wire's own ENDPOINTS.** `PointOnSegmentInterior`
  excludes them by definition, so a brand-new component pin landing exactly on an existing wire's
  *corner* passed every obstruction check and shorted anyway. On the 3-port SDD — whose gate and drain
  sit on the SAME column, so both bias chains route up it — the drain choke's near pin landed precisely
  on the corner of the gate supply's route, tying VGG and VDD together through their chokes. The
  symptom was a `SingularMatrixException` from the engine, with nothing in the drawing to point at.
  `Ctx.WireVertices` is the fix, and it is checked by `IsObstructed` alongside the interior test.

### VSWR: the restriction was a search BRACKET, and the inverse has a closed form in RfCore

`HarmonicaVswrHandle` bisected `f(v) = |Γ_drag − ctr(v)| − rad(v)` over `[1.001, 10⁶]`, 60 iterations a
pointer-move. That whole apparatus is unnecessary: the drawn locus is the image of the power-wave
circle `|s_c| = ρ` about the marker's own impedance, so inverting it is "map the drag point back to
`s_c` and read its magnitude" — which is exactly `RfCore.RfHelpers.VswrFromGamma`. One call.

**And that is what unlocked the owner's ask.** `ρ > 1` — a drag OUTSIDE the image of the passive disk
— makes `(1+ρ)/(1−ρ)` *negative*, which `LoadpullSurface.VswrCircleGamma` draws perfectly well (it
squares ρ for the centre and takes `|ρ|` for the radius). The old bracket could not express it at all,
which is why every drag past the rim pinned at the ceiling. R8B's own note — "a passive marker's whole
VSWR family stays strictly inside |Γ| = 1, so it literally cannot be dragged outside the chart" — is
true only of the **positive** half of the family; the family continues past the rim with ρ > 1 and the
theorem was being read one clause too far. `MinVswr`/`MaxVswr` are gone, along with the floor in
`SetMarkerVswr` and the "at least 1" refusal in the Set… dialog; the only two values still refused are
the two the owner named (NaN is dropped, ±∞ becomes ±`InfiniteVswr` = 1e9).

### The intrinsic glyph was drawn on a DIFFERENT radial scale from its own marker

`IntrinsicGlyphScale` compressed everything past `|Γ| = 1` into a bounded annulus (asymptotic to
1 + 0.25) while a marker is drawn through the raw `GammaToCanvas`. Inside the disc the two agree
exactly, which is why this never showed — but drag a marker OUTSIDE the chart on the default DUT (a
bare SDD: intrinsic plane == extrinsic plane, so the two values are *the same impedance*) and the glyph
sits at radius ≤ 1.25 while its marker sits at 1.6. `IntrinsicGlyphScale.Compress` is now `false` and
`DisplayRadius`/`TrueRadius` are the identity up to `MaxTrueMagnitude`. **The cost is stated rather than
hidden**: a glyph with a large `|Γ_intr|` can be clipped at the panel edge again, which is exactly what
the compression existed to prevent — the same trade already accepted for `AnnulusHeadroom = 0`. The
curve is kept behind the flag, not deleted.

### Also: the picker showed `.csch` twice

`SuggestedFileName = "harmonica-testbench.csch"` **plus** a `FileTypeChoices` entry whose pattern is
`*.csch` — the picker appends the type's extension itself. The suggested name carries no extension now.
(`ExportGamAsync` has the same shape of `SuggestedFileName` but declares no `FileTypeChoices`, so it was
left alone.)

## R9D — S1 "Match to Zin*", and the PA-class preset terminations (brief-harmonicarf-r9d, 2026-08-15)

**§2 — `Match to Zin*` reuses the frame-carried-outcome plumbing verbatim, and costs TWO frames, not
one.** `HarmonicaSolver.Options.ConjugateMatchBackoffDb` asks a frame to also read Zin off an
already-solved rung of the tier-A drive-up (`HarmonicaSolver.IndexOfBackoffStep`, a pure function
pinned on a synthetic ladder — including the "target below the ladder's first rung lands on the first
rung" case); the answer rides home as `HarmonicaFrame.ConjugateMatch`
(`ConjugateMatchOutcome`), exactly the shape `HarmonicaFrame.Inverse` already uses and for the same
stated reason ("computed on a WORKER, UI-visible state"). `HarmonicaViewModel.RequestConjugateMatch`
submits a `SkipContours` measurement-only frame first; its `PublishFrame` → `ApplyConjugateMatch` then
writes S1 (via `SetMarkerImpedance`, never a second mechanism) and requests the REAL frame that
regenerates the iso-lines. A `Found: false` outcome writes nothing (R-h6-9) and only sets the message.

**§3 — the preset walk is a straight read of `CircuitModel.IntrinsicDragAllowed`'s existing four-way
predicate, not a hand-rolled re-diagnosis.** `HarmonicaViewModel.ApplyPaClassPreset` builds a
transform-only model copy (nonlinear Cgs/Cdg/Cds replaced by the SAME linearized value
`Inputs`'s own strip row already shows, falling back to `Coefficients[0]` — "(at V=0)" — when nothing
has been solved yet, exactly the strip's own fallback) and then asks `IntrinsicDragAllowed` about
THAT copy: true means the ABCD transform runs per band (`IntrinsicAbcd.ExtrinsicFor`, refusing only a
per-band pole with the band left unchanged); false — for whichever of the OTHER three reasons the
predicate names (non-SDD DUT, a non-absent Cdg, or a package that couples input/output) — means every
band is written straight at the extrinsic plane, with a message naming why. One predicate call handles
every row of §3.4's table without the view-model code needing to know which refusal it hit. Only
Load-side markers that ALREADY EXIST are written (`Markers.Where(Side == Load)`) — a preset never
creates one, so `K=5` with markers only up to L3 leaves L4/L5 reporting `TerminationSet.
UnmarkedBandOhms`, exactly the owner's own example, now a gate. One `RequestScheduledFrame` after
every band is written, never one per band.

**The menu item wiring caught a stale source-scan pin, not a design bug.** Adding the optional
`KeyGesture? gesture` parameter to `HarmonicaAppMenuInjector.Item` broke
`HarmonicaAppMenuInjectorTests.Item_AlwaysConstructsAFreshInstance...`, which pins the method's exact
source text — expected, since the whole point of that test is to catch an accidental change to how a
`NativeMenuItem` is built; its expected strings were updated alongside the signature, not relaxed.

Gate: `tests/Ui.Tests/Harmonica/HarmonicaConjugateMatchTests.cs` (8 tests — the found/not-found/no-marker
cases via the same `PublishFrame` seam `ApplyInverseOutcome`'s own tests use, the
"only-when-set" check, and `IndexOfBackoffStep` pinned directly), `HarmonicaPaClassPresetTests.cs` (6
tests — the owner's own K=5/L1-L3 example, no-marker-created, source-untouched, the Cdg best-effort
path, the nonlinear-Cgs linearized-copy path with a real Rd/Ld to prove the transform actually ran, and
the one-frame-per-application count), `HarmonicaR9dPresetTerminationsSourceScanTests.cs` (8 tests — all
three menu surfaces' headers/parameters/gestures, plus the command's own string→enum mapping exercised
end to end). All existing `Ui.Tests` (7,048) and `Harmonica.Tests` (241) still green.

## R9C — SolveAtOptimum never reports a failed search, and the launch frame stops lying about grid size (brief-harmonicarf-r9c, 2026-08-15)

Companion entry to `src/Harmonica/RESOLVED.md`'s own R9C section (the ladder fix and the neighbour-seed
distance-guard finding); this one covers the two things that changed in `src/Ui`. §0's investigation
and its two measurement tables are recorded there, not duplicated here.

**§2 — `SolveAtOptimum` used to fall back to a failed search's LAST SURVIVING PROBE and hand it to
`AddMxColumn` as though it were the compression point.** On the shipped default at ZL1 = 132.3 Ω that
probe was Pin 11 dBm at 15.72 dB gain, published as "MXE Pout 26.72 dBm" while the strip's own P-3dB
read 39.28 dBm at the identical termination — the owner's exact bug report. Fixed two ways together:

1. **The drive-up is now `PinSearch.Sweep` at the document's own ladder settings**
   (`PinStartDbm`/`PinMaxDbm`/`PinStepDbm`) — literally the same call tier-A's own drive-up makes — not
   `PinSearch.Run`. MXP/MXE and the strip's operating-point column then agree by construction (one
   function, one definition of "P-x dB", one running-`gMax` rule), not by coincidence. Cost: ~38 solves
   each, two per full-quality frame (was ~11 with `Run`), measured on
   `HarmonicaOptimumSolveTests.MeasuredCost_TheOptimumSolvesCostRoughlyTwoDriveUps`.
2. **A search that did not reach the compression target now REFUSES rather than reports.**
   `SmithPanelData.SmithOptimum` gained `string? UnsolvedReason` (populated from the failed search's
   own `PinStopReason` — `PinMax` and `NonConvergence` read as different sentences, per R3B §3.3's own
   rule that the two stay distinguishable) and `CompressionReadout? SolvedCompression` (the ladder's
   own interpolated/one-real-solve reading AT the target — read for Pout/Eff/PAE/Gain/Pdc, falling back
   to `Solved`'s own nearest-rung numbers only for a hypothetical future `Run()`-based caller, the
   identical `sc?.X ?? at.X` shape the operating-point column already uses). `AddMxColumn`'s "no
   optimum" tooltip now reads `optimum.UnsolvedReason` when present, falling back to the original two
   sentences (every grid point a hole / mid-drag) otherwise — R7C §1.4's row-SHAPE rule is untouched:
   the same ten rows either way, gated by `HarmonicaOptimumSolveTests.RowCount_IsIdenticalBetween-
   ASolvedAndAFailedSearch`.

**`SolveAtOptimum` and `AddMxColumn` are now `internal` (not `private`), for the gate tests' own
sake.** Reproducing "the interpolated argmax lands somewhere that genuinely fails to re-solve" through
the natural pipeline (drive `InterpolatedArgmax` toward a real failure by tuning `PinMaxDbm`) turned out
to be a narrow, fixture-specific band rather than a reliable scenario — scanned directly on the shipped
default: `PinMaxDbm` from 14 to 20 dBm moves the grid from "36/37 holes, `Optimum` itself null" straight
to "33/37 holes, cleanly solved", with no dBm step in between landing on "an interpolated optimum exists
but its own fresh re-solve fails" (the InterpolatedArgmax seed and a fresh `Sweep` at that exact Γ
apparently succeed or fail together almost everywhere on this device). Rather than chase a fragile
fixture, the two methods are exposed via `InternalsVisibleTo("CircuitRF.Ui.Tests")` (already wired for
several other Ui view-model test seams) and called directly with a hand-picked failing termination
(`PinMaxDbm` set far below `PinStartDbm`, mirroring `ContourGridTests`' own
`D4_ANonCompressingPointStopsAtPinMaxAndSaysSo` fixture) and a hand-crafted `SmithOptimum`.

**§4 — the launch frame is solved at full quality, like every other frame.** Two changes, both
needed:

1. `HarmonicaView.EnsureFirstSolve` now calls `RequestScheduledFrame(dragging: false)` instead of a
   bare `RequestFrame()`. The bare call took `Options`' own (coarse) defaults, so a document's FIRST
   frame swept 25 points while every later one swept 37 — measured (§0, `src/Harmonica/RESOLVED.md`) to
   move the DE optimum from Z = 122.579 − j0.805 to Z = 132.319 − j1.786 and carry 4 holes instead of 1,
   which is what the owner saw as "the contours change when I move L1". Cost: ~65 extra solves on the
   document's own opening frame (measured 451 ms whole, in Debug) — paid once, deliberately.
2. **`HarmonicaSolver.Options`' `Rings`/`Spokes` defaults changed from `FrameScheduler.CoarseRings`/
   `CoarseSpokes` to `FullRings`/`FullSpokes`.** A bare `new Options()` is used by tests and reachable
   by any future caller; leaving it silently coarse would re-arm the same trap under a different name.
   Nothing reads the OLD doc comment's "fast rather than correct-and-slow" framing any more — the
   record's own comment now states the opposite and why.

**Benchmarks re-measured and their recorded numbers updated, per §5's own instruction that a
tripled grid solve does not free anyone from re-checking the ladder threshold or the recorded cost
comments:**

- **`FrameScheduler` was checked, not just reasoned about** — `HarmonicaFrameTierCostTests.
  Tier9_FrameTimeAtEachDegradationTier` still passes with only RELATIVE assertions (no rung threshold
  hardcodes an absolute number that could go stale), so no threshold needed moving. Re-measured on the
  shipped default: Full 37 pts / 892 solves / 468.9 ms total; CoarseRaster 37 pts / 892 solves /
  309.2 ms; CoarseGrid 25 pts / 639 solves / 227.3 ms; FrozenContours 0 pts / 40 solves / 11.7 ms.
- **`HarmonicaGridDragCostTests`'s recorded numbers changed, and its OWN doc comment now says why:**
  the per-point search became `PinSearch.Sweep`'s ladder (every 2 dB rung from PinStart to PinMax,
  rather than a ~5-solve secant), so both halves' SOLVE COUNTS rose — full rebuild 272 → **1319** HB
  solves, one dragged point 3 → **23** HB solves. R-h7-12's own reuse mechanism (keyed on Γ,
  search-independent) is UNCHANGED — still exactly 60 of 61 points reused, confirming the reuse itself
  was never the thing that moved. Wall-clock stayed well inside budget despite the solve-count rise:
  full rebuild 547.8 → **476.2** ms (each ladder rung is a cheap, well-warm-started solve), one dragged
  point 3.3 → **7.3** ms — still ~65× faster than a full rebuild, which is the number that actually
  gates whether a drag holds frame rate.

## R9B — the Appearance tab becomes circuitRF's Color Theme layout (brief-harmonicarf-r9b, 2026-08-15)

Pure layout/gesture parity — `HarmonicaColorEditor` (the model) did not move, and
`HarmonicaColorEditorTests` needed no edits. `HarmonicaAppearanceSettingsView.axaml`/`.axaml.cs` were
rewritten to transcribe `SettingsView.axaml`'s "Color Theme" tab shape: a role list with a 14 px colour
swatch per row (bound to a reused, namespace-level `RoleRowModel` — no second row-model type), RGBA
sliders + linked integer boxes, a hex field, and double-click-a-swatch (`OnRoleDoubleTapped`) opening
`ColorPickerDialog` in place of the former "Pick…" button, which is gone.

**The one structural difference from `SettingsView` is deliberate and stated at the top of the new
`.axaml`:** no theme-name combo, no `Save Theme…`, no `ForkToCustomIfNeeded`, no working-copy
dictionaries — harmonicaRF runs standalone with no workspace open, so a theme name has nothing to
resolve against (`HarmonicaColorEditor`'s own header, R-h7-15). Every edit still writes straight
through `HarmonicaColorEditor.Set` immediately (R-h7-16, live preview stays free — no re-solve,
re-fit, or re-factorization on this path). `Import .ccolor…` / `Export .ccolor…` / `Reset All Colours`
keep their place as harmonicaRF's answer to the theme combo.

**One addition beyond the brief's letter, needed once swatches exist:** `RefreshAllSwatches()` (the
`OnVariantChanged` counterpart the brief specifies) is also called from `OnRevertClick`,
`OnResetAllClick`, and `OnImportClick` — each can change a role's resolved colour without the user
touching that row directly, and without the call the list would show a stale swatch until the next
variant flip. `SettingsView` has no equivalent case (it has no per-role revert), so there was nothing
to transcribe here.

**§5 checked, not assumed:** the standalone harmonicaRF entry point (`HarmonicaApp.axaml`) already
carries `CircuitRfStyles.axaml`'s `Application.Styles` include (its own header calls this a
"superset by construction"), which pulls in `Avalonia.Controls.ColorPicker`'s Fluent theme
(`CircuitRfStyles.axaml:31`) — so `ColorPickerDialog`'s `ColorView` renders correctly standalone.
Nothing needed to change there; this brief just raises the stakes on it staying true, since the
picker is now the only way to reach a colour wheel at all.

**Gate:** `HarmonicaAppearanceSettingsView` is a `UserControl` and cannot be constructed headlessly
(same limitation as `HarmonicaSetTerminationDialogTests`), so the check is a source scan —
`HarmonicaR9bAppearanceParityTests` — asserting the double-tap gesture, all four RGBA sliders/boxes,
the hex box, the swatch-bound `Rectangle` inside the role list's `DataTemplate`, `PickButton`'s and the
theme-combo/Save-Theme controls' absence, and that `SettingsView.axaml` (the file this one was copied
from) still carries the same gesture and binding. `dotnet build`, `dotnet test tests/Ui.Tests`, and
`dotnet test tests/Firewall.Tests` all green. No screenshot verification (per brief). No `CLAUDE.md`
edit.

## R9A — the readout strip, the menus, and four defaults (brief-harmonicarf-r9a, 2026-08-15)

Eleven small, independent owner items. Nothing here moves a solved number except §7/§8, which are
explicitly defaults.

**§1 — "Add Source Marker" not drawing was a stale SNAPSHOT, not a missing redraw.** The panels draw
`SmithPanelData.Markers`, a copy taken once inside `RequestFrame` and carried onto the frame — not
`HarmonicaViewModel.Markers`, which `HarmonicaHitTest` reads live. `Refresh()`/`InvalidateVisual()`
after `AddMarkerBand` redrew the SAME stale snapshot, so the new marker was immediately hit-testable
and completely invisible. Fixed with `SyncMarkerSnapshotIntoFrame` (a pure re-projection via `Frame
with { Markers = …, SmithPower = Frame.SmithPower with { Markers = … }, … }`, no re-solve) called from
both `AddMarkerBand` and `RemoveMarkerBand`, plus `AddMarkerBandAndShow` (now the menu's own call site)
requesting a real frame afterward so the strip gains its row and the intrinsic glyph appears.

**§2 — `rgs` moved into the Capacitance chunk by inserting one key into `SettingsColumnKeys`,
between the spacer and `KeyCgs`.** Everything else (the label's `(Ω):` suffix, the missing `*`,
double-click-to-edit) fell out for free from the existing per-key dispatch — re-implementing any of
it would have been two answers to the same question. `EffectiveSettingsColumnKeys`'s existing
`ContainsKey(KeyCgs)` gate already covers `rgs` too (both are SDD-only, emitted by the same branch),
so it needed no second condition.

**§3/§6/§9 — pure text/structure removals**, gated by source-scan (`HarmonicaR9aSourceScanTests`):
the two rule `Border`s are gone from `ReadoutStripView.axaml` (kept `Spacing="3"`, only the lines
went); "Add Point(s)" → "Add Grid Points" on both menu rows; "DE" → "Drain Efficiency" on all three
menu surfaces (`HarmonicaView.axaml.cs`, `HarmonicaMenuView.axaml` ×2, `HarmonicaAppMenuInjector.cs`)
— `CommandParameter` stays the string `"DE"` everywhere, only the display text changed.

**§4 — `FormatComplex` gained optional `partDecimals`/`magDecimals` parameters (default the existing
constants) rather than a second formatter body.** `FormatZCompact` (1 decimal, `MxHeaderZDecimals`) is
the MXP/MXE header's own impedance now — an argmax off a fitted RBF surface does not carry the three
digits every other complex row (`FormatZ`) claims. `AddMxColumn`'s own `Zin` row is untouched.

**§5 — the power-sweep plot's dashed operating-point cursor is gone, by owner ruling, not superseded
by anything.** `PowerSweepPanelData.CursorIndex` still drives which step the glyphs/loadline/readouts
read; it simply has no mark on the curve any more. Pinned by a DIFFERENTIAL render test (same panel
at `CursorIndex = -1` vs. a valid index must be pixel-identical) — a single-column pixel probe cannot
tell a cursor line from a grid line, the same trap H4–H5 recorded for iso-lines vs. Smith chrome.

**§7 — the default L1 marker is `80+j0 Ω`, not `80+j10 Ω`.** 80 Ω is both the default DUT's own
R_opt and the default `HarmonicaSettings.Z0`, so the shipped document now opens with L1 at Γ = 0, the
centre of its own Smith chart. Default-model path only — `RebuildMarkersFromTerminations` (the load
path) is untouched, and every test fixture elsewhere in the suite that explicitly sets `80+j10`
(there are many, all independent of the constructor default) is unaffected.

**§10 — "Locked" now shares `Toggle`'s own checkbox glyph pair** (`CheckboxOutline`/
`CheckboxBlankOutline`, the same pair "Show Grid Points" uses) instead of a `Lock`/`LockOpenVariant`
pair, by routing both `AddAutoscaleLockedItems` rows through the shared `Toggle(header, on, onClick)`
helper (`Toggle("Autoscale", autoscaleOn, …)` / `Toggle("Locked", !autoscaleOn, …)`) rather than
hand-building two `MenuItem`s. `Toggle` never sets `ToggleType` (R7A §2.3's own finding about the
Fluent template's icon/check-glyph slot collision), so that invariant carries over unchanged.

**§11 — nothing is posted to the message line while a gesture is live**, gated on
`HarmonicaCanvas.Gesture.IsLive` (covers a marker drag, an intrinsic-glyph drag, a grid-point drag,
and an Edit Display grab — every case the owner can be inside). Extracted as a pure
`HarmonicaView.MessageLineText(gestureLive, statusMessage, idleSummary)` so `Ui.Tests` can pin all
combinations without a live control. The idle solve-cost summary used to update on every mid-drag
frame — a changing line under a moving hand, which is exactly what R1C's §2 said this line must
never be. A solve error raised mid-drag still surfaces, one `Refresh()` after release.

**§8's blast radius is in `src/Harmonica/RESOLVED.md`** (touches `CircuitModel`/`CharmIo`, not `src/Ui`).

## R8C — the readouts carry live impedance, γ suppresses its own noise, and the intrinsic drag stops solving (brief-harmonicarf-r8c, 2026-08-15)

**§1 — `HarmonicaTitles.MxHeaderRow` gained a `zText` parameter rather than an `HarmonicaReadoutFormatting`
reference, because the two files sit on opposite sides of the UI wall** (`src/Harmonica` is
framework-free by rule; `HarmonicaReadoutFormatting` is `src/Ui`). `AddMxColumn` computes the real
optimum's Z (`HarmonicaDataSet.ImpedanceOf(optimum.Gamma, z0)`, never the marker's, per the owner's own
explicit instruction) ONLY inside the solved branch — the no-optimum branch still calls `MxHeaderRow`
with `zText: null`, keeping R7C §1.4's "row shape must not change between branches" rule intact
(computing it unconditionally from `optimum?.Gamma` looked simpler at first but leaked a Z into the
"no optimum" header text, which the brief explicitly forbids).

**Header rows became `SelectableTextBlock`, and the one hazard the brief flagged (R-h9r2-15: it eats a
double-tap before `DoubleTapped` fires) genuinely does not apply — headers have no `DoubleTapped`
handler at all**, confirmed by reading `BuildColumnRowShell`'s own early return for `isHeader`.

**§2 — the γ phase floor (`GammaPhaseNoiseFloor = 1e-3`) collided with an EXISTING test's own
fixture**, `HarmonicaGammaFactorTests.GammaRow_IsComputedThreeTimes_FromThreeDifferentDataSets`: the
shipped default document's OP/MXP/MXE operating points all carry |γ| comfortably under the new floor,
so their FORMATTED strings collapsed to the identical `"0.000∠—"` — correct display behaviour (the
whole point of §2), not evidence the three computations merged into one. Fixed by comparing the raw
`Complex` γ from each chunk's own `V_intr` cube (via the SAME private `ReadComplex`/`GammaFactor`
reflection the file's other tests already use) instead of the rendered text — a strictly BETTER test
than the string comparison it replaced, decoupled from any future formatting change.

**§4 — `HarmonicaPanelRenderer.MarkerRadius` is now the one place either the round marker or the
triangular intrinsic glyph computes its own radius from**, hoisted out of `DrawMarkers`. The 0.9
scale factor collides in NAME ONLY with the triangle's own unrelated 0.9/0.75 shape-proportion
literals a few lines below — commented at both sites so a reader does not fold them together.

**§5 is where the real design work landed — see `src/Harmonica/RESOLVED.md`'s own R8C entry for the
`IntrinsicAbcd` chain-order finding and the round-trip residual.** What lands here: `BeginIntrinsicDrag`/
`EndIntrinsicDrag` became no-ops (clearing `InverseMessage`); `DragIntrinsicGlyph` calls
`IntrinsicAbcd.ExtrinsicFor` synchronously on the UI thread and writes the result through the SAME
`SetMarkerImpedance` an extrinsic drag uses, then routes the forward frame through
`RequestFrameOnMarkerRelease` — the SAME pacing/dedup machinery an extrinsic marker drag already uses
(its own doc comment already named `DragIntrinsicGlyph` as a caller, apparently written in
anticipation of this exact change). `_inverse`/`_inverseMarker`/`_inverseBands`/`_inverseTargets` and
`RequestInverseFrame` are now genuinely unreferenced from anywhere in this class (not merely "from the
drag path") — `_inverse`/`_inverseMarker` needed explicit `= null` initializers or the compiler's
CS0649 ("field is never assigned") turns into a build ERROR in this project's config, not a warning.

**`HarmonicaHitTest.Resolve` gained `intrinsicDragAllowed` (default `true`, so every existing direct
test keeps today's behaviour); Pass 2 does not run at all when it is false** — a grab that starts and
then refuses to move is worse than no grab, per the brief's own instruction. A NEW hit-test helper,
`IsOverIntrinsicGlyph`, runs the identical Pass-2 distance check independent of the allowed flag, used
ONLY so `HarmonicaPointer.PointerDown` can still tell the user WHY a click that visibly landed on the
glyph did nothing (`InverseMessage`), without granting the grab itself.

**`ShowReachableRegion` now defaults `false`.** The property, sampler and `DrawReachableRegion` all
stay in place (nothing here is deleted) — only the default flipped, since the shading answered "what
can the retired inverse solve reach," a question the closed form's exact inversion makes uninteresting
(everything is reachable except the pole).

Gate: see `src/Harmonica/RESOLVED.md`'s own R8C entry for the full test list across both projects.

## Terminations, the marker Γ, and the context menus — a re-entrancy flag cannot express "who owns this box"; only identity can (brief-harmonicarf-r8b, 2026-08-15)

**§1 — the "can't type 50" bug (reported three times) was never in `TryParse`.** The Set Termination
dialog's three combined-text boxes stayed in sync by having each `TextChanged` handler write the other
two's `Text`, guarded by a single `bool _loading` set for the duration of that write. `_loading` is a
*window in time*, not a statement about identity — an echo landing after the window closes (a deferred
raise, a re-entrant write) is processed as if the user had typed it, which is what rewrote the Z box
under the user's own caret. Fixed by replacing the flag with **ownership**: `TerminationEditModel`
(new, no Avalonia reference) tracks `Editing` (which field, or none) and every `Edit(field, text)` call
for a field that isn't the current `Editing` one is simply ignored, regardless of when or how it was
raised. The dialog is now a thin shell — `GotFocus` sets `Editing`, `TextChanged` forwards to
`Edit`, `LostFocus` clears `Editing` *before* reformatting so that reformat's own echo is disowned too.
`TerminationEditModelTests` drives the actual echo call the old bug depended on
(`AnEchoFromAnotherField_WhileEditingZ_DoesNotMoveTheModel`) — the case three prior "fixes," each
verified only against a hand-built simulation of the handler shape, could never observe. **Not
interactively verified against a live `TextBox`** (no headless-Avalonia harness for this dialog in this
repo, and the brief asked for no screenshot verification) — the model-level fix is pinned directly; if
the live control still misbehaves after this, that would be a SECOND, unlocated defect, not a
regression of this one.

**§2 — the marker glyph and its own VSWR circle were drawn on two different radial mappings, and only
one of them was ever meant for a marker.** `IntrinsicGlyphScale`'s compressed radial map exists for the
INTRINSIC glyph (`|Γ_intr|` is unbounded, R-h45-4) — `MarkerToCanvas`/`CanvasToMarker` composed that
same map into the EXTRINSIC termination marker's own canvas transform too, invisibly inside the unit
disc and wrong the moment `|Γ| > 1`. Both wrappers are deleted outright (not merely unused) so the
composition cannot be silently reintroduced by "reusing the marker helper" — every extrinsic call site
(hit-test passes, the drag gesture, `DrawMarkers`) now goes straight through the plain
`GammaToCanvas`/`CanvasToGamma` affine map, exactly like a grid point or the VSWR locus already did.
Consequence, intended: an active marker can now leave the panel entirely (`DrawMarkers` carries no
`ClipRect`), and `IntrinsicGlyphScale.MaxTrueMagnitude`'s soft saturation at |Γ|=10 no longer applies to
a marker drag — the practical bound is now whatever the panel's own pixel extent reaches (~1.3 at the
chart margins), a harder and more honest one. **Measured, not assumed:** `GammaToCanvas`/`CanvasToGamma`
round-trips to 1e-9 only near the origin; `SKPoint` is `float` (32-bit), so a value near the rim
(|Γ|≈0.9–0.999) already loses precision to ~1e-7 absolute, and a value well outside the rim loses far
more to the underlying chart viewport's own finite window — neither is a regression, both are properties
of the transform this brief exposed rather than introduced.

**§3 — an unmarked band is a near-short (1e-6 Ω), so "S1/S2 off by default" and "S1 defaults to 50 Ω"
are the SAME change, not two.** Deleting the S1 marker without also writing its termination would have
silently turned the DUT's source into a near-short the instant the marker vanished — exactly what
`AddMarkerBand`'s own comment says must never happen. Fixed by writing `Terminations.Set(Source, 1, 50Ω)`
in the constructor even though no marker exists for it — `TerminationSet`'s own ctor already does this
for both S1/L1, so the explicit call is a second, defensive statement of the same fact rather than a
behavior change on its own. A fresh document now ships **L1/L2/L3 only** (three markers, not five) —
S1/S2 are added from the Smith panel's new "Add Source Marker" item. **Band 1 is removable on both
sides now** (`RemoveMarkerBand`, the Markers-menu `HarmonicaBandMenuItem.CanRemove`) — it used to refuse
outright; removing it now leaves the termination in place, same as bands ≥ 2 leave their absence as the
unmarked value. The Source readout column keeps its header row even with zero markers on it (R7C §1.4's
row-shape-stability rule), with a tooltip naming the fix rather than a silent gap.

**§7.3 — "I can't drag the VSWR circle outside the chart" was two separate findings, and only one of
them is fixable.** (1) A THEOREM: a passive marker's (`|Γ|<1`) whole VSWR family stays strictly inside
`|Γ|=1` for every finite VSWR — the underlying Möbius map is an automorphism of the passive half-plane,
so a passive marker's circle *cannot* be dragged outside the chart, ever, by construction. (2) A
saturation that hid (1) badly: `VswrThrough`'s bisection silently returned the clamped `MaxVswr` (1e6)
the instant a drag point fell outside its search bracket, which reads as "the number stopped moving."
Fixed by reporting the clamp instead of hiding it (`VswrThroughEx` → `(Vswr, Saturated)`,
`HarmonicaReadoutFormatting.FormatVswr(vswr, saturated)` → `"VSWR: > 10⁶"`), in both the live drag
readout and the marker menu's own row. §2's fix is the other half the owner actually wanted: an ACTIVE
marker's whole VSWR family sits entirely *outside* `|Γ|=1`, and with the marker itself no longer
compressed onto the intrinsic scale, that family now draws (and drags) concentric with its marker,
genuinely outside the Smith chart, unclipped.

**§5 — the Fluent `MenuItem` template trap (R7A §2.3 first found it) gets closed for good, not
patched twice.** `ToggleType.CheckBox` and `Icon` share the SAME leading slot in this Avalonia build —
an item with both shows a missing icon, a missing checkmark, or a doubled indent depending on theme.
R7A §2.3 fixed exactly two items this way (Autoscale/Locked) and left every other toggle on
`ToggleType`; this brief converts the rest through one shared builder (`Toggle(header, on, onClick,
glyph: MenuGlyph.Check|Radio)`) and pins it with a source scan (`HarmonicaView.axaml.cs` contains zero
occurrences of `MenuItemToggleType`). Power Sweep/Time Domain — genuinely a two-state radio, not two
independent checkboxes — is now one `Mode ▸` submenu row with the current mode in its own header text;
its own row carries no `Click` (a `MenuItem` with children never raises one — R7A §2.4's trap, again).
`HarmonicaAppMenuInjector.cs`'s one `NativeMenuItem.ToggleType` use was checked and left alone: it never
sets `Icon` alongside it, so the slot-collision this rule exists for cannot occur there.

**§6 — the Ω icon on the marker menu's Γ/Z rows was never anything but a placeholder** to satisfy
`Item`'s old non-nullable `MaterialIconKind icon` parameter; made explicitly nullable (`icon: null`
leaves `Icon` unset) rather than swapped for a different glyph that would mean something equally
nothing.

## Iso-line labels: a 30.0-vs-2π unit mismatch meant ZERO ever drew on a Smith/Polar plot; harmonicaRF's own toggle was wired end to end except the draw call (brief-harmonicarf-r8a §4, 2026-08-15)

**`ContourRenderer.DrawIsoLines`' label placer walks in WORLD units, and Data Display's own Γ-plane
default was 5× the longest polyline that can exist there.** `ContourData.LabelSpacing` seeded
Smith/Polar contours at 30.0 — the SAME number used for a rectangular dB-vs-frequency axis, where it's
sensible. But the Γ world is the unit disc: the longest closed polyline is the rim, arc length 2π ≈
6.28, and the walk's first target is `startFrac × spacing ≥ 0.15 × 30 = 4.5`. For almost every real
contour (arc 1–3) that target is never reached, so the `while (targetArcW <= segEnd)` loop's body never
executes and **not one label was ever drawn, for every contour on every Smith/Polar plot, at any zoom,
regardless of the `DrawLabels` toggle** — invisible on a Rect plot (hundreds of world units, so 30.0
works fine there), which is exactly why nobody had noticed. Fixed two ways, both required: the seed
default is now plane-dependent (0.35 for Γ, matching the disc's own scale), AND the placer itself
(`ContourRenderer.ComputeLabelAnchors`) now falls back to placing exactly ONE label when the configured
spacing exceeds the polyline's own total arc length, rather than silently placing none — a user asking
for a wide spacing wants FEWER labels, never zero, and zero is indistinguishable from "broken".

**harmonicaRF's `ShowIsoLineLabels` toggle was wired end to end — the menu item, the `.charm` round
trip, the render-cache key — except for the one thing it names.** `HarmonicaPanelRenderer.DrawContours`
stroked polylines and returned; the toggle's entire observable effect was busting the Layer-A raster
cache key. This was not a rendering bug to hunt for — it was a feature shipped with its last step
missing. Fixed by extracting Data Display's own placement arithmetic into
`ContourRenderer.DrawIsoLineLabel`/`ComputeLabelAnchors` (Skia draw / pure arc-walk, split so the
arithmetic is unit-testable without a canvas) and calling it from harmonicaRF too — one placer, two
renderers, rather than a second hand-rolled one. Each label gets the SAME ramped alpha byte its own
polyline got (`IsoLineAlphaRamp.AlphaByte`), so a faded low-rank contour never carries a fully-opaque
label — with the fade floor now 0.01 (below), that would have been the loudest possible artifact.

**Measured on the shipped default document** (37-point grid, `HarmonicaViewModel.DefaultModel()`,
load side band 1): 55–68 label anchors across 11–15 polylines per metric at the new 0.35 spacing — 4–5
labels per contour, matching the "~5–6 around a full rim-scale ring" estimate the new default was
chosen from.

**Tab split trap, named so it doesn't reappear:** moving the fade sliders and the label checkbox from
the Appearance tab to the Advanced tab moved their MARKUP, but both write through the SAME
`HarmonicaColorEditor` instance the Appearance tab was already handed — construct a second editor for
the Advanced tab (the obvious way to wire a newly-independent view) and the two tabs silently diverge,
with whichever one loads last winning. `HarmonicaSettingsDialog` now hands `vm.ColorEditor` explicitly
to both `Attach` calls; there is exactly one editor per document, never two.

## `Grid.IsSharedSizeScope` does not align columns hosted in a `StackPanel` — five failed attempts had the wrong culprit; units moved into the labels, γ landed (brief-harmonicarf-r7c, 2026-08-14)

**The readout strip's label/value misalignment was never a width bug. `Grid.IsSharedSizeScope` set on
a `StackPanel` host is a no-op in this Avalonia build (12.0.3) — confirmed by an isolated repro, not
inferred.** A throwaway headless-Avalonia harness (`AppBuilder.Configure<T>().UseHeadless(...).
UseSkia()`, real Skia text shaping, no display needed) built the minimal case directly: a `StackPanel`
with `Grid.SetIsSharedSizeScope(host, true)`, two child `Grid`s each with a `SharedSizeGroup`'d
`Auto,Auto` column set, one row labelled `"Short:"` and one `"A Much Longer Label:"`. Their VALUE
cells measured at **X = 39 and X = 138** — never aligned, because each row's label column sized to
its OWN text, exactly as if `IsSharedSizeScope` had never been set. R-hui-4 (2026-08-14, the brief
before this one) built the whole three-column layout on this premise and it never worked; R-hui-5
through R-hui-7 kept re-diagnosing the SYMPTOM (a jittering value column) as a width-reservation bug
and re-fixing the value column's own width, which never touched the actual defect — the LABEL
column, silently un-shared the entire time. Five attempts, one root cause, never named until this
brief actually built the isolated case rather than staring at the full control tree.

**The fix (§1.5's own explicit fallback): every chunk's label column is pinned to a MEASURED width,
the same discipline `ReservedValueWidth` already used for the value column** — `ChunkLabelWidth`
(readout columns), `UpdateSettingsColumn`'s own `labelWidth` (the Settings column), and a second pass
over `Items.Children` after building (`General`, which rebuilds every call and so has no persistent
row to probe a typeface from before all its rows exist). `SharedSizeGroup`/`Grid.SetIsSharedSizeScope`
are deleted outright, not left in as inert scaffolding — "drop it entirely," per the brief.

**A second, genuine bug was found and fixed while building the label-width measurement, and it is
worth naming on its own: measuring against a control's OWN `.FontSize` property reads ONE FRAME
STALE.** `ChunkLabelWidth`/`UpdateSettingsColumn`'s first draft read `probe.FontSize` (the label
TextBlock's own current property) rather than the `fontSize` PARAMETER `SetItems` was just called
with — `UpdateColumnRow` is what writes the new value onto that property, and it had not run yet at
the point `ChunkLabelWidth` needed it. Harmless when font size never changes frame to frame (the
stale read equals the current one), but caught directly: the SAME headless harness re-solved twice at
the SAME font size and the whole `OperatingPointColumn` value column shifted 4–5 px between the two
calls anyway — the tell was that `ChunkLabelWidth`'s own measured width (69.35 px, printed both
before and after) was DIFFERENT on the very first call (74.69 px) than every call after, which only
happens if the number being measured AT depends on something other than the label text and the font
size actually requested. **Any future "measure once, pin the result" helper in this file must take
its font size as an explicit parameter, never read it back off a control this same call is in the
middle of updating.**

**Measured, replacing the guess:** the widest complex value's worst case (`"−0000.000−j0000.000"` /
`"0000.000∠−000.0°"`, the wider of the two) at the strip's own SemiBold weight, 13 px — a realistic
mid-range font size for this panel — is **120.71 px**. The OLD `22 * fontSize * 0.55` formula
(`RectComplexChars`'s old character budget) would have reserved **157.30 px** — 30% too generous,
which is a real cost: every OTHER row-kind's column in the same chunk pays for a complex row's own
padding once `ReservedValueWidth`'s max wins, even though nothing on screen actually needs that much
room. §1.3's own prediction (the constant is wrong by "a different amount for every glyph and every
non-integer font size") is not approximately right, it is measurably 30% off at one realistic size.

**§1.4's row-count churn was real, not hypothetical — the pre-fix test asserted it directly.** Before
this brief, `HarmonicaReadoutColumnsTests.MxpColumn_SaysNoOptimum_OnASkipContoursFrame` asserted
`Assert.Single(mxp)` on a `SkipContours` frame — i.e. the MXP chunk genuinely collapsed from nine rows
to one every time a degraded ladder rung or a `SkipContours` frame carried no fresh optimum, and
expanded back to nine the instant a full-quality frame supplied one. That test is what proved the bug
was live, not merely theoretically possible from reading `AddMxColumn`'s branch. Fixed by always
emitting the same nine (now ten, with γ) rows and rendering `"—"` when unavailable — pinned by the
SAME test, now asserting the opposite: row count invariant across both states.

**γ, the input nonlinearity factor (§2 of the brief), landed as a NON-complex row on purpose** — see
`HarmonicaSolver.GammaFactor`/`AddGammaRow` and `HarmonicaReadoutFormatting.FormatGammaFactor`. Marking
it `IsComplex: false` is structural, not cosmetic: a `true` row would both offer a real/imaginary menu
that means nothing for `γ = V₂·conj(V₁)²/|V₁|³` (no sensible real/imaginary split) and collide with
Zin's own saved format state, since `HarmonicaReadout.FormatKey` resolves any complex row in a given
column to the SAME key (`"MXP.Zin"` etc.) regardless of which row it is.

**Not verified in this brief, and it should be before the layout is called fully closed:** the
headless harness above proves column alignment and drag-stability ALGEBRAICALLY (measured X
positions, before/after a re-solve, across four font sizes) — it does not prove what a human eye
sees. `screencapture`/AppleScript UI automation was attempted from this session and blocked by macOS
Screen Recording permission not being granted to the sandboxed shell; no screenshot of the running app
was taken. The four gate screenshots §4 asks for (rest state, a live drag, MXP/MXE's nine dashes,
γ under Pdc) are still owed.

## Active-termination Γ bug was the compressed radial scale's clamp; fly menus are real context menus now (brief-harmonicarf-r7a, 2026-08-14)

**§1 — `IntrinsicGlyphScale.MaxTrueMagnitude = 10.0`.** The old inverse (`TrueRadius`) saturated at
`u = 1 - 1e-9`, a near-pole that put every pointer position at or beyond drawn radius `1 + margin`
at the SAME Γ ≈ −1.0000000282×10⁹ (measured, reproduced by a test that pins the exact figure) — a
value that does not survive its own Γ↔Z round trip (`GammaOf(ImpedanceOf(Γ))` was off by ~41) and so
disagreed with itself everywhere it was re-derived from Z. At `|Γ| = 10`: **Z = −40.91 Ω at Z0 = 50**,
**Z = −65.45 Ω at Z0 = 80** — past every physically interesting active termination and small enough
that the round trip is exact to double precision. The clamp is now derived algebraically from the
constant (`u_max = k(MaxTrueMagnitude−1)/(1+k(MaxTrueMagnitude−1))`), so the two can never drift.
`HarmonicaSetTerminationDialog`'s live Z preview had the SAME bug's sibling — it clamped `|Γ|` to
0.999 before previewing, which is wrong for this brief's whole subject (typing Γ = −3 showed the Z of
−0.999, not −25 Ω). Fixed by deleting the clamp entirely: the preview is now exactly
`HarmonicaDataSet.ImpedanceOf`, which already nudges only the genuine `|1−Γ| < 1e-12` singularity.
Extracted as `HarmonicaSetTerminationDialog.PreviewImpedance` (internal static) since the dialog is a
`Window` and cannot be constructed headlessly in `tests/Ui.Tests`.

**§2 — every fly menu routes through one `Item(header, icon, onClick)` helper**, plus a shared
`AddAutoscaleLockedItems` for the two panels that both carry an Autoscale/Locked pair. No
`MaterialIconKind` name from the brief's own suggested map needed substituting — every one of
`ContentCopy, Pencil, Cog, PlusCircleOutline, PlusCircleMultipleOutline, Delete, Magnet, MagnetOn,
ChartBellCurve, SineWave, Percent, ChartLine, Waveform, Omega, Lock, LockOpenVariant, ArrowExpandAll`
compiled — checked exhaustively against `Enum.GetNames(typeof(MaterialIconKind))` from a throwaway
console probe before writing any call site, not by trial and error. The one gap: no
`ArrowExpandAllOutline` (or any outline variant) exists for the inactive Autoscale state, so §2.3's
own documented fallback is what shipped — the same `ArrowExpandAll` glyph at reduced opacity (0.35)
rather than a different icon.

**§2.3's Fluent `MenuItem` Icon/checkmark trap — NOT VISUALLY VERIFIED.** The owner's chosen
resolution (Autoscale/Locked carry an icon and no `ToggleType` at all) was applied as specified, which
sidesteps the trap by construction rather than by observing it. Whether the trap is real for the
OTHER checkbox items the brief named to leave icon-free (Snap to Grid, Show Grid Points, Power
Sweep/Time Domain, Contour Plane/Harmonic/Efficiency Metric's children) was not checked — the
`/run`-and-screenshot half of this brief's own §4 gate was explicitly declined for this round (running
the app means driving a real mouse/window on the owner's own machine), so this is deferred to whoever
next has a live session open, not silently assumed passing.

**§2.4 — the actual bug, generalized.** `MenuItem` with children never raises `Click`: true of the
VSWR row (a checkbox carrying a lone "Set…" child) AND of all three format rows (Γ real/imag, Γ
mag/angle, Z real/imag — each just a lone "Set…" child with no Click of its own). All four flattened;
pinned structurally (`HarmonicaR7aMenuTests`) by asserting `ItemsSource` never appears in
`BuildMarkerMenu`/`BuildFormatRow`'s own (comment-stripped) source, rather than re-deriving the
specific defect shape by hand at every call site.

## Persisted axis limits + autoscale: one mechanism, three plots (brief-harmonicarf-r6e, 2026-08-14)

**§1 — the Drain Sweep bug was the Grid's own `*` row, not a positioning mistake.**
`HarmonicaDcivSweepsDialog.axaml`'s `RowDefinitions="Auto,Auto,*,Auto"` put the "Drain sweep (Vds)"
title in row 2 — the ONE row marked `*` — so it floated at the top of a row that stretched to fill
whatever space `Height="300"` left over, while its own fields (row 3) were pushed to the bottom of
the window. Fixed by making every row `Auto` and switching the Window to `SizeToContent="Height"`
(dropping the explicit `Height` entirely) rather than picking a new fixed number — the same fix this
brief's own §3.1 addition (a whole new "Axis limits" section) needed anyway, so the window grows to
fit rather than needing a second guess at a magic height.

**§2 — the three-state rule, expressed as one small pure function plus one write-back method.**
`HarmonicaPanelRenderer.StoredAxisWindow` (X/Y/Y2 min+max + `Autoscale`) is read by
`ApplyStoredWindow`, called at the END of `BuildLoadlinePlot`/`BuildPowerSweepPlot`/
`BuildTimeDomainPlot` — strictly AFTER `AutoScale`, `PinAxisPin` and the right-edge headroom, so an
explicit stored limit always wins over all three. `Autoscale == true` makes `ApplyStoredWindow` a
pure no-op, which is the trick that lets ONE call site serve both "read the stored window" (ordinary
render) and "tell me what AutoScale/PinAxisPin/headroom would compute right now" (the capture path,
below) — the caller decides which question it's asking by what it does with `Autoscale` and with the
result, not by a second code path.

**The write-back is a SEPARATE method (`HarmonicaViewModel.CaptureAxisWindows`), fired from
`OnFrameChanged` — never from inside the renderer.** The renderer is a pure function called from
several places (the live canvas, Copy Plot, export) that must never have a mutating side effect;
`CaptureAxisWindows` is the one place anything WRITES a stored limit, and it only fires per SOLVED
FRAME, never per repaint. It calls each `Build*Plot` with `Autoscale` forced to `true` (so
`ApplyStoredWindow` no-ops and the returned `Axes.Window`/`WindowSecondary` is always today's
natural fit), then writes that back into `HarmonicaSettings` under exactly two conditions: autoscale
is actually ON (every frame — this is what makes "turn it off" freeze exactly what's on screen), or
autoscale is OFF and nothing has ever been stored (ONCE, from the first frame that has real data —
checked against the panel's own arrays, not against the Window looking "big enough", because
`Axes`'s own default `Window` is `(-50,-50,150,150)`, not `(0,0,0,0)`, and would otherwise read as a
plausible captured value before any data exists). Neither condition holds once a limit is held with
autoscale off — the anti-breathing property itself, not a special case of it.

**Time Domain gets its OWN thirteen-field-shaped block, not a shared one with Power Sweep** — same
panel slot, different quantity (time/V/A vs power/dB/%), so `TimeDomainXMin`/… are separate
`HarmonicaSettings` fields and a separate `HarmonicaPowerSweepAxesDialog(vm, timeDomain: bool)`
construction, never a shared set gated by the current mode. Confirmed by test
(`ApplyTimeDomainAxisLimits_IsIndependentOfPowerSweepAxisLimits`) that setting one leaves the other
untouched.

**One dialog class serves two modes.** `HarmonicaPowerSweepAxesDialog` relabels its Y/Y2 rows at
construction time (`Gain`/`Efficiency|PAE` vs `Vds`/`Ids`) and calls whichever `Apply…AxisLimits`/
`Set…Autoscale` pair matches the mode it was opened in — cheaper than two near-identical dialog
classes, and the brief's own "say if you find a cheaper representation" invited exactly this.

Tests: `AxisLimitsPersistenceTests` (`Harmonica.Tests`, 3 — round-trip, all-absent-on-an-old-file,
no-bloat-when-untouched), `HarmonicaR6eAxisLimitsTests` + `HarmonicaR6eDialogsAndMenusTests`
(`Ui.Tests`, 19 — the §1 layout fix, the §2.4 precedence ordering pinned against values PinAxisPin/
headroom would NOT have produced, the anti-breathing property both as a direct render-level assertion
and end-to-end through a real `HarmonicaViewModel` solve, and the dialog/fly-menu wiring). Full gate:
`Ui.Tests` 6,756, `Harmonica.Tests` 175, `Firewall.Tests` 6 — all green. **Not verified interactively**
(no live Avalonia session in this environment) — the owner-check items in the brief's own §5.5 (typed
limits surviving a drag and a save/reopen, the checkbox and fly-menu item agreeing on screen) are
covered by the tests above at the API/mechanism level but not watched happen in the running app.

## The power-sweep right axis, drawn twice: the third fix stops covering and just doesn't draw the underlying one (brief-harmonicarf-r6d, 2026-08-14)

**Third time reporting the identical symptom — "the right axis renders in two colours" — after two
prior fixes that both, in different ways, tried to make the COVER match the COVERED exactly** (R-h9b-9
added the cover; R3C §5 fixed the cover's paint SHAPE so it matched `AxesRenderer.DrawBorder`'s stroke
field-for-field, see this file's own r3c entry above). Both were real fixes for the bug they diagnosed
and neither could be the last fix, because **the thing being covered was still there** — any future
change to either paint's shape (a stroke width formula, a cap style, an AA setting) reopens the exact
same symptom with a new mismatch. The owner's own framing this round: "Do not render the green line
underneath it" — not "match it better."

**The actual fix: stop drawing the underlying secondary-axis chrome at all**, rather than adding a
third generation of paint-matching. `HarmonicaPanelRenderer.DrawWithSuppressedSecondaryChrome` swaps
`plot.Axes` for a deep copy (`Axes`'s own copy constructor, `Axes.cs:161`) with `ShowSecondary = false`
for the ONE `PlotRenderer.Draw` call, then restores the original before the (renamed, colour-
parametrized) overlay draws the axis for the first time. Confirmed safe by reading rather than assumed,
per the brief's own instructions:

- **Trace rendering never reads `Axes.ShowSecondary`** — grepped the whole `PlotRenderer`/
  `TraceRenderer` stack; only `AxesRenderer`'s chrome (border, ticks, tick numbers, label) and a few
  interaction call sites in `PlotControl` (irrelevant here — harmonicaRF never uses that control) branch
  on it. So the efficiency/loadline trace itself renders identically whichever way the flag is set.
- **The viewport does not move.** `PlotRenderer.ComputeViewport` only re-derives a `ShowSecondary`-
  dependent viewport for a Rect plot with NO pinned `Axes.Viewport` — `BuildPowerSweepPlot`/
  `BuildTimeDomainPlot` both pin `PowerSweepShapedViewport()` explicitly (R-h9b-11), so that formula
  never runs for these panels regardless of which `Axes` instance is live when `Draw` is called.

The general lesson, sharpened from r3c's own "the cover must match the covered exactly" note: **a cover
that must match the covered exactly is the wrong design in the first place — check whether the
covered draw can simply be suppressed before reaching for a better-matched cover.** Here it could,
because the ONE thing keying the covered draw (`Axes.ShowSecondary`) was a plain bool with an existing
copy-and-flip path, and nothing downstream of the `Draw` call needed it to stay true. That will not
always be true (a shared renderer might branch on a dozen fields, or the caller might not own a cheap
copy of its own state) — when it is not, r3c's paint-matching route is still the right fallback, not a
mistake to avoid on principle.

A headless pixel test (`HarmonicaPanelTests.PowerSweepPanel_RightAxis_NoOrdinaryAxisColourSurvivesUnderneathTheOverlay`)
is the gate the previous two fixes could never have written, because the previous two never made it
true: it renders the panel and asserts NO pixel along the right axis, its tick-number band or its
rotated label carries the ordinary `Harmonica.AxisLine` colour — not "the fringe is small enough not to
notice," an assertion that is exact by construction now rather than approximately true by paint-shape
coincidence.

## The readout strip: 2×4 grid, intrinsic chunks, stable widths, per-chunk copy (brief-harmonicarf-r6c, 2026-08-14)

**§1 — the six-column horizontal `StackPanel` became an 8-cell `Grid` (2 rows × 4 columns), re-parenting
only.** Every `x:Name` from the old `Columns` row survives unchanged (`SettingsColumn`,
`OperatingPointColumn`, `SourceColumn`, `LoadColumn`, `MxpColumn`, `MxeColumn`, plus two new ones), so
`UpdateReadoutColumn`'s build-once/update-in-place machinery needed no changes at all — only the XAML
`Grid.Row`/`Grid.Column` placement and `ReadoutColumn`'s own doc comment moved. Row 1: Settings ·
OperatingPoint · MXP · MXE. Row 2: **Load · Source** · IntrinsicVDS · IntrinsicIDS — Load left of
Source, the reverse of R1C's own left-to-right order, per the owner's explicit (row, column)
specification. `HarmonicaR3cStripTests`' own column-order test now reads `Grid.Row`/`Grid.Column` off
the XAML rather than trusting declaration order (a `Grid`'s children may appear in any order in markup).

**§2 — two new chunks (`ReadoutColumn.IntrinsicVds`/`IntrinsicIds`) read `V_intr`/`I_intr` at
`ctx.IntrinsicPorts.DrainPort`, never recomputed.** `HarmonicaSolver.ReadComplex(ds, cubeName,
sideIndex, harmonic)` already generically indexes any `[axis0, harmonic]` complex cube — it needed no
change to serve a `[port, harmonic]` cube with `sideIndex = DrainPort` instead of a `[side, harmonic]`
one with `sideIndex = (int)TerminationSide`. **These two chunks default to magnitude ∠ angle, unlike
every other complex row's real/imaginary default** — `HarmonicaReadoutFormatting.DefaultReadoutFormat`
special-cases the `VDSi.`/`IDSi.` key namespace (one place, shared by `HarmonicaSolver`'s null-resolver
fallback and `HarmonicaViewModel.ReadoutFormatLookup`'s own unrecognized-key fallback, so the two
cannot disagree) — the row is still an ordinary `IsComplex` row with a working format flyout, the
owner's default preference is just the OTHER format from everywhere else. `SetItems`'s column-routing
switch is now **exhaustive over `ReadoutColumn`** (it used to fall through to `default: mxe.Add(item)`,
which would have silently swallowed both new columns into MXE).

**§4 — TWO independent sources of column-width churn, and the fix for one nearly broke the other.**
Trailing-zero trimming (`0.###` turns 10.01 into 10.1, one character shorter) is fixed by fixed decimal
places; the INTEGER side growing (an impedance running 0.5 Ω → 5000 Ω) additionally needs
`HarmonicaReadoutFormatting.FixedWidth(value, decimals, budget)` — pads to a stated per-quantity
character budget, or switches to a fixed-width exponent form past it, so a row's rendered length is a
function of WHAT KIND of row it is, never of its current value.

**The trap: `FixedWidth`'s padding must NEVER reach an editable `TextBox`.** The strip's inline editor
(`BeginInlineEdit`) and `HarmonicaSetTerminationDialog`'s three boxes both used to SEED from the exact
same formatted string the strip DISPLAYS — so baking left-padding spaces into `FormatZ`/`FormatGamma`'s
output put whitespace ahead of the caret in a live edit box. Concretely: typing "200" into a freshly
opened Z field now inserted after the leading pad spaces of a PRIOR reformat, landing the digits in the
wrong place — reproduced directly by `HarmonicaSetTerminationDialogTests`' own old-algorithm simulation,
which is exactly the caret-under-a-rewrite defect class brief-harmonicarf-r6a §6 already fixed once for
a different cause. **Fixed by splitting the two purposes**: `FixedWidth`/`FormatComplex`/`FormatZ`/
`FormatGamma` gained a `pad` parameter (default `true`, for display); every EDITABLE-text call site
passes `pad: false` — `ReadoutStripView.EditSeedValue` (new, parallel to `DisplayValue`), and
`HarmonicaSetTerminationDialog.LoadFields`. The marker context menu's read-only `"Γ = …"`/`"Z = …"`
header rows also pass `pad: false` — not because they are editable, but because the padding exists
ONLY to reserve a strip COLUMN's width, and a `MenuItem` header has no column to protect.

**Column width itself is reserved on the CONTROL, not inferred from the padded string.**
`HarmonicaReadoutFormatting.ReservedValueChars(item)` is a pure function of a row's KIND (Label/
IsComplex/IsGamma — never its value or even its current format, since it takes the WIDER of
rectangular and polar so a live format toggle cannot move the column either) — `ReadoutStripView`
writes `chars * fontSize * 0.55` to the value control's `Width` on every refresh, which is a no-op on
screen for a value-only update and stays correct across a live font-size change. The Settings column
gets the same discipline from a small per-key budget table (`SettingsValueWidth`), since §3's label
renames widened the LABEL column and the brief calls out rechecking the VALUE column too.

**§5 — one `ContextMenu` per chunk (not per row), relying on Avalonia's own ContextRequested
bubbling.** A complex row's existing per-row format flyout (`row.ContextMenu`, set only for `IsComplex`
rows) wins on a right-click landing inside it; everything else in a chunk (its header row, a plain
scalar row, the chunk's own whitespace) has no row-level `ContextMenu` and falls through to the
chunk-level one, built once per chunk in the constructor and populated lazily on `Opening` — the same
pattern `BuildLiveFormatMenu` already uses. `HarmonicaClipboard.RowsText(IEnumerable<(string, string)>)`
(new) factors out the one `label\tvalue\n` loop shared by the whole-canvas text-clipboard flavour, the
existing `Edit ▸ Copy Readouts`, and this new per-chunk Copy — the per-chunk version reads straight off
the chunk's own built controls (label/value/unit `TextBlock`s) rather than off `HarmonicaReadout`
objects, since the Settings chunk has no `HarmonicaReadout` backing at all.

## Smith charts — grab-anywhere VSWR, Add Point, fly menus (brief-harmonicarf-r6b, 2026-08-14)

**§1 — the VSWR circle has no gripper; the whole circumference is grabbable, and the drag is
unclamped.** `HarmonicaHitTest.Resolve`'s Pass 2.5 now hit-tests point-to-SEGMENT distance against
`LoadpullSurface.VswrLocus`'s own default-resolution polyline (the Data Display's `HitTestVswrLocus`
pattern), not a single θ = 0 handle point; `HarmonicaPanelRenderer.DrawVswrLocus` lost its square
handle glyph to match. `HarmonicaVswrHandle.HandleGamma` is gone — nothing needs "the" grab point any
more. The old display clamp (`VswrOf`/`RhoOf`'s `Math.Clamp(rho, 0, 0.99)` → `MaxVswr = 199`) is
gone too; `HarmonicaViewModel.SetMarkerVswr` now only floors at `MinVswr = 1.001` (VSWR ≥ 1 is
geometric, not policy). `VswrThrough`'s own rim-clamp on the DRAG POINT (`|Γ| < 0.999`) was also
removed — it was copied from `SetMarkerGamma`'s Γ = 1 guard, which matters there because that Γ
becomes a termination; here the drag point is only ever compared against a circle's centre/radius, so
the guard bought nothing but silently capping the exact gesture this brief exists to unlock.
`MaxVswr` is now `1e6` — a bisection SEARCH ceiling, not a display cap.

**MEASURED, NOT ASSUMED — the reason `MaxVswr` almost never actually bites.** For an ordinary
(passive, `|marker.Gamma| < 1`) marker, the WHOLE VSWR family stays strictly inside `|Γ| = 1` for
every finite VSWR — it approaches the rim as VSWR → ∞ but provably never reaches or crosses it (the
underlying power-wave Möbius map is an automorphism of the passive half-plane). Probed directly
across several passive centres up to VSWR = 1e6: max `|Γ|` on the locus never exceeded ~0.99999.
The MIRROR case — an ACTIVE marker (`|Γ| > 1`, R-h6-10's own flag) — has its ENTIRE family sitting
OUTSIDE `|Γ| = 1` instead, for every VSWR down to the floor. So "the user drags the circle outside
the Smith chart" (§1.2's own framing) cashes out as: a passive marker's circle can be dragged
arbitrarily CLOSE to the rim (any VSWR up to 1e6, never saturating at the old 199), while only an
ACTIVE marker's circle is ever actually beyond it. `HarmonicaVswrHandleTests` pins both regimes.

**§1.3 — the live readout is gesture state, not view-model state**, tracked on `HarmonicaGesture`
itself (`VswrReadoutActive`/`VswrReadoutPointer`/`VswrReadoutText`, set on press AND move, cleared on
release/cancel — mirrors `PlotControl._vswrReadoutActive`). Drawn by
`HarmonicaPanelRenderer.DrawVswrReadout`, called from `HarmonicaCanvas`'s own draw operation AFTER
`HarmonicaCanvasRenderer.DrawAll` (i.e. outside every panel's own clip rect) — the same "unclipped,
last" rule Data Display's `vswrReadout` block follows. `HarmonicaReadoutFormatting.FormatVswr` is the
ONE formatter both this readout and §2.1's menu header use, so the number a drag lands on is the
number the menu then shows.

**§2 — `Add Point`/`Add Points to VSWR` needed a THIRD layer in the grid model, not a second
`CustomGrid` contract.** `HarmonicaViewModel.AddedGridPoints` (an `ObservableCollection<Complex>`) is
additive on top of whatever `HarmonicaSolver.Options.GammaGrid` resolves to — the ring/spoke lattice
by default, or an imported `.gam`/dragged scatter when `GammaGrid` supersedes it.
`HarmonicaSolver.Solve` composes `(opt.GammaGrid ?? RingGrid(...)) ++ opt.AddedGridPoints` right
before calling `ContourGrid.Build`; no partial-reuse path was built (§2.2's own "either is
acceptable" — the node SET moves, which invalidates the RBF factorization cache by construction
anyway, so a full re-solve was the honest, not-noticeably-worse choice). **Measured**: a shipping
3 × 12 (37-point) grid re-solve is ~250–280 ms either way; adding one point (38 points) costs the
same order of magnitude, not noticeably more — one HB solve per point dominates regardless. Cleared
by `ResetGrid()` and by `SetGridPreset()` (the owner's own ruling: "the preset must always describe
exactly what is on screen"), NOT by a `.gam` import (`SetGammaGrid` only ever replaces the base — the
brief names this explicitly and does not list import as a third clearing trigger). Persists in the
`.charm` via a new `CharmIo.CharmDocument.AddedGridPoints` string array (`"re,im"` per entry, same
encoding `TerminationsToJson` already uses), absent-block-when-empty like every other optional
`.charm` field.

**§2.1 — `HarmonicaSetVswrDialog` (new)**, sized/shaped like `HarmonicaSetZ0Dialog` (a single field,
OK/Cancel gated) rather than `HarmonicaSetTerminationDialog`'s three-synced-rows shape — the closer
precedent for "one number." Reject-and-keep on non-finite or < 1, never a silent substitution; OK
commits through `SetMarkerVswr`, which now also flips `VswrEnabled` on (typing a value and seeing
nothing happen was the failure mode named in the brief).

**§3 — the MXP/MXE cross is gone from the Smith panels, deliberately (deferred to v2), but the
DATA is untouched.** `HarmonicaPanelRenderer.DrawOptima` is deleted; `SmithPanelData.Optimum` still
gets computed and populated exactly as before (the readout columns read it). `Optimum` came OUT of
`HarmonicaBackdropCache`'s `LayerAKey` — it was the only thing forcing a full Layer-A raster rebuild
every time the argmax moved during a drag, for a pixel difference that no longer exists.
`HarmonicaBackdropCacheTests.ChangingOptimum_RebuildsLayerA` inverted to
`..._DoesNotRebuildLayerA`, per the brief's own "invert, don't delete" instruction.

**§4 — one dispatch, two new panel-scoped fly menus, reusing (never re-deriving) existing
geometry.** `HarmonicaPanelRenderer.TitleBandHeight` went `private` → `public` so
`HarmonicaView.OnCanvasContextMenuOpening` can resolve a title-band click against the SAME band the
renderer draws into. Dispatch order: marker/glyph/VSWR-handle (unchanged) → `HarmonicaHitTest.PanelAt`
resolves a Smith panel → title vs body by `local.Y < TitleBandHeight(size)` → the Edit-Display panel
branches (power sweep / loadline), unchanged. Body: Copy (via `HarmonicaClipboard.CopyAsync` with the
RESOLVED panel id, never `Canvas.PanelUnderPointer()`) + Show Grid Points. Title: Contour Plane +
Contour Harmonic (built from `HarmonicaMenuViewModel.ContourHarmonics`, never hardcoded f₀/2f₀/3f₀ —
the exact bug that list already exists to prevent) on both charts, + Efficiency Metric on the
efficiency chart only — every item bound to the SAME `ICommand` the `Display` menu uses, checked to
show the current selection. This brief's own §4 note ("R6D and R6E extend this") means the dispatch
shape here is the pattern to copy, not a one-off.

**Tests**: `HarmonicaVswrHandleTests` (rewritten, 17), `HarmonicaSetVswrDialogTests` (5, new),
`HarmonicaSmithFlyMenuTests` (7, new), `HarmonicaAddedGridPointsTests` (12, new),
`CharmTracesAndGridReuseTests` (+2, `Harmonica.Tests`), `HarmonicaBackdropCacheTests` (1 inverted),
`HarmonicaR3cStripTests` (2 fixed for `TitleBandHeight`'s new visibility). `dotnet test tests/Ui.Tests`
6,702 passed; `tests/Harmonica.Tests` 172 passed; `tests/Firewall.Tests` 6 passed.

## The docked menu injection, the Settings merge, and a reformat-under-caret bug (brief-harmonicarf-r6a, 2026-08-13)

**§1.2 — "Markers shows, Display/Grid do not" did NOT reproduce as a throw under a normal
Inject/Withdraw/re-Inject cycle, headlessly.** `HarmonicaAppMenuInjectorTests` already proved (before
this brief) that a plain two-round Inject/Withdraw round-trip against hand-built stand-in items is
clean. What the old `HarmonicaAppMenuInjector.Inject` genuinely lacked was ATOMICITY: a bare `foreach`
appending one item at a time with no rollback, so if item 2 of 3 ever threw for ANY reason (an item
that already carries a `Parent` — `NativeMenu`'s own list validator refuses that), item 1 would already
be sitting in `appMenu.Items` while items 2 and 3 never landed — exactly the reported shape. Fixed to
be atomic (build a scratch `added` list, roll it all back on any exception) and failure-visible
(`InjectDockedItemsIfNeeded` now catches and reports through `HarmonicaViewModel.SolveError` instead of
losing the failure silently). `HarmonicaAppMenuInjectorTests.Inject_NeverLeavesAPartialSet_...` proves
the OLD code's exact vulnerability class (a poisoned item mid-list leaves a partial `appMenu.Items`)
and that the fix closes it.

**§2.1 — none of the three "Settings" paths the owner could reach were actually dead; the docked one
was unreachable, which reads the same from the outside.** circuitRF's own `Settings…` (File menu /
macOS app menu ⌘,) opens circuitRF's OWN app-level dialog and does something — it is just not
harmonicaRF's. harmonicaRF's own `Edit ▸ Preferences…` (torn-off/in-window) already worked, from an
earlier round's `RunHook`/error-reporting fix. What genuinely had no route at all: harmonicaRF's own
settings **while docked** — before this brief's §1.3, the docked injected set was Markers/Display/Grid
only, and the in-window `Menu` (which carries `Preferences…`) is hidden on macOS whenever docked. The
owner's report ("Edit ▸ Settings does nothing") is the visible symptom of clicking circuitRF's own item
believing it is harmonicaRF's — an easy thing to do, since docked, harmonicaRF's own Edit menu is not
visible at all. §1.3's injected `harmonicaRF` top-level menu (with its own `Settings…` item) is what
actually closes this, not a fix to any of the three items themselves.

**§6.1 — the exact "typed 200, committed 190" figure did NOT reproduce under the most plausible
headless caret model, and that is recorded rather than papered over.** `HarmonicaSetTerminationDialog`
is a `Window` (uninstantiable headlessly, same constraint as every other dialog in this file); the
mechanism was instead driven against the REAL `HarmonicaReadoutFormatting` parse/format functions under
a simulated "CaretIndex preserved across a programmatic Text rewrite, clamped to the new length" model
— the one documented, ordinary Avalonia `TextBox` behaviour. Under that model, typing "200" into a
FRESH, empty (selected-then-replaced) box happens NOT to corrupt — confirmed by test, not assumed. What
DOES reproduce, under the identical mechanism: typing into a box that already carries text (resuming
mid-edit rather than replacing a selection) corrupts outright, and so does anything with an imaginary
term or an exponent (`"-25+j40"` loses its imaginary part; `"1e3"` comes out as `0+j31`) — because the
reformatted string's own structure (a `+j0 Ω` tail, or a totally different digit grouping) shifts under
a caret index that does not know the string got longer or shorter. The fix removes the mechanism
entirely rather than chasing one caret model: the box currently being edited is now NEVER
programmatically rewritten (`LoadFields(except:)`), so no caret assumption is needed at all — reformat
happens exactly once, on blur (`OnFieldLostFocus`) or OK. See
`tests/Ui.Tests/Harmonica/HarmonicaSetTerminationDialogTests.cs` for both the (partial) reproduction and
the fixed algorithm's own gate.

## The instrument, the strip rebuild, and drag starvation (brief-harmonicarf-r5, 2026-08-13)

**§6's own bar — the owner's real drag, with the overlay on — is met.** Two prior briefs (R3B §1.4, R4
§4.6) each ended with "not measured this pass — requires a live interactive Avalonia session, which
this session had no way to drive." This one closes it: reported directly by the owner, from the
shipped build, first thing after landing —

> `last 16.7  mean 34  p95 17.5  p99 144.9  max 1632.0 ms   >33ms: 2/96`

**Read exactly, not smoothed over — the mean sitting ABOVE p95 is real and says something, not a
typo.** 94 of 96 frames are fast (p95 17.5 ms is comfortably under the 33.3 ms/30 fps line, matching
`last` 16.7 ms), and only 2 of 96 crossed the budget at all. The mean (34) and p99 (144.9) are both
being pulled hard by a single outlier — `max` 1632 ms is almost certainly one cold/first-touch frame
(JIT, first backdrop-cache fill, or a one-time GC pause), not a representative drag frame; one 1632 ms
sample alone contributes ~17 ms to a 96-sample mean, which is most of the gap between `mean` and `p95`
on its own. **This is exactly the right shape for "conflate-and-pace fixed the starvation, and the
strip rebuild fixed the steady-state cost, with one unrelated warm-up hitch left over"** — a
19 ms-ish stutter magnitude concentrated in ~2% of frames, not the ~90 ms/11 fps `EVERY` frame the
brief opened with. Matches the owner's own words ("extremely fast... exactly the UX I was looking
for") independent of the numbers. **Not yet separately isolated**: whether the `max` outlier is
specifically the document's first solve (a known, one-time, already-understood cost — first backdrop
fill, first HB solve, JIT) rather than a genuine mid-drag hitch. Worth a look only if it recurs; a
single first-frame outlier in an otherwise-clean 96-sample window is not a regression to chase.
`LastSetItemsMs`/`LastRenderMs`/the solve-stage breakdown/`SolvePool` counters/GC deltas were not part
of the reported line — the frame-interval read alone is what the owner chose to report, and it is
the one §0's whole diagnosis turned on ("stutter is frame-interval VARIANCE... no number anywhere in
this repo has ever measured it"), so it is the one that actually closes the brief.

**§1 — the instrument, built.** `HarmonicaDiagnosticsOverlay` (new, `src/Ui/Harmonica/`, framework-free
— a rolling 120-frame ring buffer of interval/GC samples, `Compute()` returning
mean/p95/p99/max/`>33ms` count fresh from the buffer every call rather than maintained running
aggregates) plus `HarmonicaDiagnosticsOverlayRenderer` (new, `Renderers/` — the Skia draw, plain text,
`IsAntialias = false` throughout, times its own draw and writes `LastDrawMs` back for the NEXT frame to
show, the same one-frame-behind convention `LastRenderMs` already uses). Owned by `HarmonicaViewModel`
(`Diagnostics`), not by the canvas, so `Display ▸ Reset Diagnostics Overlay` reaches it with no hook
back into the view. `HarmonicaCanvas`'s draw operation records a sample and draws the HUD, both gated on
`ShowDiagnosticsOverlay` (default OFF, persisted per document exactly like `ShowGridPoints` — new
`CharmAppearance.ShowDiagnosticsOverlay`, an untouched document still re-serialises byte-for-byte). It
shows every number §1.1 asked for: frame-interval last/mean/p95/p99/max + `>33ms` count,
`FrameTiming`'s own per-stage breakdown + `LastRenderMs`, the readout strip's `LastSetItemsMs` **and**
`LastSetInputsMs` (new — §1.1 also asked for this half to be timed "if it isn't already"; it wasn't),
`SolvePool`'s `StartedCount`/`CompletedCount`/`SupersededCount` + the completed/started ratio,
`NoOpDragFrameSkipCount`, `Lever1DisabledCount` (new VM passthrough to the solver's own counter), and
the GC gen0/gen1 deltas across the window. Deterministic tests (`HarmonicaDiagnosticsOverlayTests`, fed
a clock the same D1 convention `FrameScheduler` uses) pin the rolling-window arithmetic itself —
mean/max/percentile-ordering/window-eviction/reset-clears-the-seed — since the DRAW cost and a real
frame cadence are exactly the two things this environment cannot produce.

**§2 — `SetItems`, build-once/update-in-place, done and measured (headlessly, where it can be).**
Applied the Settings-column's own pattern (a per-column SHAPE SIGNATURE — label, header-or-not,
`IsComplex`, `Editable`, joined per row — compared before any `.Clear()`) to all five non-General
columns (OperatingPoint/Source/Load/Mxp/Mxe), independently: `_columnSignatures` is keyed by
`ReadoutColumn`, so adding an L2 marker rebuilds ONLY Load. `SettingsRowMayBeOverwritten` — the exact
predicate R3C built — now guards these rows' value slots too, closing R3C's own named follow-up "for
free": an open Source/Load inline editor is no longer destroyed and reopened as a stale row every
published frame, because the row is no longer destroyed at all in the steady state. The per-row
context menu (real/imaginary ⇄ magnitude/angle, "Set…") moved from eagerly rebuilt every `SetItems`
call to built once and populated lazily on `ContextMenu.Opening` — a user right-click, not a published
frame. The General column is explicitly untouched (still rebuilds every call) — it carries no editors
and is typically 0–1 rows, so it was never where the ~70–110-control cost lived. All 480 Harmonica
`Ui.Tests` pass, including 7 new tests pinning the signature's own dependence on the marker set (not on
the current VALUE) and the per-column independence claim at the data level. **`LastSetItemsMs` itself,
in the steady state of a drag, could not be measured this pass for the same reason §1's primary gate
could not — it needs the readout strip actually rebuilding real Avalonia controls, which needs the live
host.** The overlay reads it live now; that reading is what closes this.

**§3 — latest-wins starvation, real, fixed, and demonstrated (though not against a real pointer).**
Confirmed by reading exactly as the brief predicted: `HarmonicaViewModel.RequestFrameOnMarkerRelease`'s
`dragging: true` branch called `RequestScheduledFrame` — and through it, `SolvePool.Submit` — on EVERY
pointer-move with no pacing, and `Submit` cancels whatever was in flight before the new job even starts.
**Fixed with conflate-and-pace, not with a change to `SolvePool`** (guardrail 2 holds — latest-wins is
untouched for every other submitter): a mid-drag call now checks `DragSolveInFlight` — computed from
the POOL's own `LastCompletedSequence` against the sequence this class itself last submitted, not from
a private flag a completion callback would have to remember to clear — and conflates into a pending
slot rather than submitting when one is still outstanding. `OnPoolSettled` (called by whoever marshals
the pool's `Completed`/`Failed` events to the UI thread — `HarmonicaView` in the live app) submits the
conflated move the moment the in-flight one finishes, reading the marker's Γ at THAT moment rather than
whatever it was when the move first arrived. The marker glyph itself is never paced — `SetMarkerGamma`
still runs on every pointer event, unconditionally, before any of this. **This is where an existing
test's own assertion had to invert, and that is worth recording rather than quietly rewriting past.**
`HarmonicaDragTests.ASyntheticDrag_...` used to assert `SupersededCount > 20` on a 40-move burst as
proof latest-wins was collapsing the drag — correct for the OLD mechanism, and now the WRONG signature
for the fix: conflate-and-pace collapses the same burst by never submitting most of the 40 in the first
place, so `SupersededCount` stays near zero and the right assertion is that far fewer than 40 solves
ever START. Rewritten accordingly, plus three new deterministic tests
(`ConflateAndPace_*`) pinning the mechanism directly — a second move arriving before the first settles
does not reach the pool; the conflated move resubmits automatically once the in-flight one completes,
with no further pointer event; a 30-move synchronous burst starts far fewer than 30 solves, the glyph
still tracks the last move, and release still submits a real full-quality solve. **What could not be
produced: the `CompletedCount / StartedCount` ratio from an actual drag**, and with it, whether the
starvation was actually large enough to explain the owner's ~11 fps in practice rather than merely real
in principle. §3.2's own confirm-before-fix instruction is answered "yes, mechanically, by reading and
by a synthetic burst" — not yet answered "yes, and here is how much it cost" — for the same reason
everything else in this note carries the same caveat.

**§4 — the Avalonia dispatcher-priority finding, established by reading the installed 12.0.3 assembly,
not from memory.** `DispatcherPriority` in this version is a struct (not an enum), with an ordered
integer `.Value`. Reflecting the actual shipped `Avalonia.Base.dll` (12.0.3, the version this repo
pins): `Invalid −7, Inactive −6, SystemIdle −5, ApplicationIdle −4, ContextIdle −3, Background −2,
Input −1, Default 0, Loaded 1, UiThreadRender 2, Render 4, BeforeRender 5, AsyncRenderTargetResize 6,
DataBind 7, Normal 8, Send 9` (mirrors WPF's own canonical list, same names, same relative order).
`Dispatcher.Post(Action action, DispatcherPriority priority = default)` — confirmed via
`MethodInfo.GetParameters()[1].DefaultValue` and directly via `default(DispatcherPriority) ==
DispatcherPriority.Default` (`Value == 0`) — so `HarmonicaCanvas.OnRedrawRequested`'s
`Dispatcher.UIThread.Post(InvalidateVisual)`, which supplies no explicit priority, posts at `Default`
(0), confirmed **above** `Input` (−1) (`DispatcherPriority.Default.CompareTo(DispatcherPriority.Input) >
0`). So §4's suspected mechanism is real as stated: a redraw posted this way can win the dispatcher's
attention ahead of queued pointer-input processing during a burst. **Not acted on** — §4's own
guardrail is "only worth pursuing if the overlay shows the stutter clustering... rather than
throughout," which is exactly the reading this note cannot yet produce. `OnRedrawRequested` is
unchanged.

**Guardrails held.** Nothing in `PinSearch`/`ContourGrid`/`HarmonicaContext`/any solver path changed.
`SolvePool`'s latest-wins semantics are untouched for every submitter but the marker-drag path.
`SetItems`' rendered output is unchanged (source-scanned and behaviourally pinned, not eyeballed). The
overlay ships off by default, persisted, and every recording call site is gated on the toggle — no
timer runs and no buffer fills when it is off. `PlotRenderer`/`AxesRenderer` untouched.

**Full gate.** `dotnet build` clean across the whole solution. `dotnet test` (no flags, the routine
gate): Firewall.Tests 6/6, Core.Tests 1361/1361 (1 pre-existing unrelated skip), Harmonica.Tests
167/167, WBond.Tests 237/237, RfCore.Tests 298/298, Ui.Tests 6645/6645 (486 of them are this brief's own
— 480 Harmonica + a mix of new §1/§2/§3 tests). **One unrelated failure, confirmed a pre-existing
full-suite-load flake, not a regression**: `Engine.Tests`' `Hero1B_ImportElaborateAndSolve_
WithinBudgetAndConsistent` (a performance-budget gate, 12.4 s against a 10 s ceiling under full-suite
contention) — re-run alone, 1 s, comfortably under budget. Nothing in this brief touches `src/Engine`,
`src/Core`, or anything the Hero 1B fixture exercises; this matches this repo's own documented pattern
(`verify-races-under-full-suite-load` memory) of timing-sensitive gates flaking only under parallel
contention.

**Closed.** The owner's own reading (above) confirms what §2 and §3 argued for from reading and from
synthetic tests: the drag is fast now, and fast in the specific shape (a clean p95, two rare outliers)
that a fixed starvation-plus-rebuild-cost problem should produce rather than a merely-averaged-down one.
The per-stage numbers (`LastSetItemsMs`, the solve breakdown, the pool ratio, GC deltas) remain
available in the overlay for whenever a future regression needs them — that is what §1 built the
instrument FOR — but are not needed to close this brief, since the frame-interval read alone already
answers the question §0 opened with.

**Owner follow-up, same day — the two Display menu items removed, the code behind kept.** "Remove the
2 diagnostic menu items, but keep the code behind so we can turn this back on easily." Both AXAML lines
(`NativeMenuItem`/`MenuItem` for Toggle and Reset) removed from `HarmonicaMenuView.axaml`, on both menu
surfaces, each replaced with a comment naming exactly what to re-add. Nothing else moved:
`HarmonicaMenuViewModel.ToggleDiagnosticsOverlay`/`ResetDiagnosticsOverlay` (the commands themselves),
`HarmonicaViewModel.ShowDiagnosticsOverlay`/`Diagnostics`, the overlay/renderer classes, and the
`.charm` persistence are all untouched and still fully wired to each other — "turning it back on" is
re-adding the two lines the comments point at, nothing more. Pinned by test rather than left to the
comment alone: one test asserts the AXAML no longer references either command, a second drives both
commands directly (no menu in the loop at all) and confirms they still flip `ShowDiagnosticsOverlay`,
write `Appearance`, and reset the rolling window exactly as before.

## A batch of owner follow-ups: marker clamp, Contour Harmonic, a settings dialog, silent hooks (2026-08-13)

**`HarmonicaViewModel.SetMarkerGamma`'s own clamp was redundant with — and stricter than —
`HarmonicaDataSet.ImpedanceOf`'s already-correct handling of the SAME edge case.** The owner asked
for markers to be draggable outside the unit circle (negative Z, an active termination); the clamp
(`if (mag > 0.999) gamma = gamma/mag*0.999`) silently forbade ANY `|Γ| > 0.999`, forever. But
`ImpedanceOf`, one call downstream, already nudges only the true singularity (Γ = 1 exactly, where
`1−Γ` is the pole) and its own doc comment already says "`|Γ| > 1` is left alone, because an active
termination is a legitimate thing... to land on" — so the fix was deleting the redundant guard in
`SetMarkerGamma`, not narrowing it. **Lesson worth keeping: when a caller pre-clamps "to be safe"
before handing a value to a callee that already has its own, correct handling of the dangerous case,
check the callee before assuming the caller's guard is load-bearing** — this one had been silently
overriding a design decision made lower in the stack the whole time.

**Contour Harmonic was three hardcoded XAML items (f₀/2f₀/3f₀) on EACH menu surface, on a document
whose K is a live setting.** `SetGridHarmonicCommand` itself was already K-aware (validates
`k <= Terminations.HarmonicCount`) — only the ITEM LIST was frozen at 3, so K=5 had no menu path to
the bands it actually has. Fixed by mirroring the Markers menu's own `SourceBands`/`LoadBands`
pattern exactly (`HarmonicaMenuViewModel.ContourHarmonics`, an `ObservableCollection` rebuilt to K's
own length, triggered by the SAME `Markers.CollectionChanged` event the band checkboxes already used
— K only ever moves through `RetargetTerminations`, which always touches `Markers`, so no new
"K changed" signal was needed). Both surfaces (in-window `ItemsSource`, NativeMenu's own
code-behind `Fill`) share the pattern the band checkboxes already established; a new test
(`DisplayMenu_ListsTheSameItems_OnBothSurfaces`) checks SUBMENU parity specifically, since the
existing menu-parity test only ever compared top-level headers and would not have caught either
surface drifting alone.

**The SAME silent-guard bug R-h9c-10 diagnosed and fixed once (`ShowSetDutAsync`) was still sitting,
unfixed, in two sibling hooks in the identical file — `ShowPreferencesAsync` (the owner's own "Edit ▸
Settings does nothing" report — there is no menu item literally named "Settings"; it's Preferences…)
and `ShowSetZ0Async` (found alongside it, same shape, not yet reported).** `if (_doc is null ||
TopLevel.GetTopLevel(this) is not Window owner) return;` — a bare early return throws nothing, so
`RunHook`'s own exception-reporting fix (R-h9a-13) cannot help with it; the failure is silent by
construction, not by an exception slipping past a handler. **Worth stating plainly: R-h9c-10's own
note ("every OTHER dialog-opening hook in this file shares the identical guard shape... fixed because
it is the one under report") was accurate and specific — the SAME class of bug was always going to
resurface in the next sibling hook someone happened to exercise, and it did, twice.** Both are now
fixed the identical way (`Vm is not { } h → return`, then a NAMED `SolveError` + `Refresh()` on a
missing `TopLevel`) — any FUTURE dialog-opening hook copy-pasted from one of these now copies the
reporting shape too, not the silent one.

**A new per-document dialog (`HarmonicaAdvancedSettingsDialog`) for the four inputs the strip no
longer renders** (loadline pts / FFT× / charge / M — owner: "remove... from the display... set via a
menu item AND a settings in a separate dialog"). `HarmonicaInputs.Build` is UNCHANGED and still
returns all four — only `ReadoutStripView.SetInputs` stopped rendering them
(`HiddenFromStripKeys`, alongside the pre-existing `SettingsColumnKeys` split) — so the dialog reads
and writes through the exact same `HarmonicaViewModel.ApplyInput`/`HarmonicaInputs` keys the strip
row used to, per `HarmonicaSetZ0Dialog`'s own established "second surface, never a second write path"
rule. Four independent fields, each its own key — unlike `HarmonicaPowerSweepDialog`'s combined
Start/Stop/Step, there is no cross-field relationship to validate together here.

**Owner: "Idq should display in mA, not A; convert to A when searching for the proper Vgs."**
`BiasSpec.Idq` itself stays amps (the unit `SolveVgsForIdq` and every other solver-side consumer
expect) — the mA/A boundary is exactly ONE place, `HarmonicaInputs.Build`/`Apply`'s own Idq rows.
**Owner, same conversation: "keep Idq to 1 decimal place, Vgs to 3 — the inline editor should still
show the full value."** This needed a real DISPLAY-vs-EDIT split that did not exist before:
`HarmonicaInput.EditText` (falls back to `Text` when absent — every other input has no separate
rounding) is what an inline editor now seeds from, while `Text` is what the row shows at rest.
`ReadoutStripView`'s `SettingsRowState` gained `EditSeedText`, refreshed every
`UpdateSettingsColumnRow` call alongside the existing placeholder bookkeeping, so a double-click
reads the CURRENT full-precision value live rather than closing over a build-time one — the identical
staleness concern that already justified reading `value.Text`/`state.IsPlaceholder` live instead of
capturing `input` in R3C's own Settings-column closure.

## The strip's columns, Smith titles, and the efficiency axis fringe (brief-harmonicarf-r3c, 2026-08-13)

**The antialias/cap mismatch behind the two-colour axis line, and it will recur.** The power-sweep
plot's right axis showed a green fringe under the red efficiency-axis overlay because
`HarmonicaPanelRenderer.DrawEfficiencyAxisOverlay`'s cover stroke (`linePaint`/`tickPaint`) was drawn
`IsAntialias = false` with the default `Butt` cap, over `AxesRenderer.StrokePaint`'s antialiased,
`Square`-capped stroke of the identical nominal width. An antialiased stroke covers a wider pixel
footprint than a hard-edged one of the same width, and a `Square` cap extends half a stroke-width past
each endpoint where `Butt` does not — so the underlying border was always going to show as a border
around the cover, on every side and past both ends, regardless of colour choice. **The general lesson:
when one renderer paints over another's stroke to recolour it (rather than to add a new one), the
cover's `SKPaint` must match the covered one's shape field-for-field — width and colour are not
enough.** Fixed by matching `AxesRenderer.StrokePaint` exactly (`IsAntialias = true`, `StrokeCap =
Square`) rather than by widening `AxesRenderer` itself, per the standing "never widen `PlotRenderer`/
`AxesRenderer` for a harmonicaRF need" rule.

**Two owner-reported follow-ups on the inline editor itself, both found after the first pass landed —
worth keeping because they will recur wherever this codebase floats an editor over live content.**

- **Escape was silently eaten by `WorkspaceWindow`'s own `<KeyBinding Gesture="Escape"
  Command="{Binding DisarmPlacementCommand}"/>`.** A docked document sits inside that window, and a
  `KeyBinding` gesture is resolved BEFORE ordinary tunnel/bubble routing ever reaches the focused
  control — so the editor's own `box.KeyDown` Escape branch never ran. This is not a new failure mode:
  `SchematicView.OnViewKeyDownTunnel` documents hitting the IDENTICAL problem for its own inline
  editor, and the fix is the same shape — a `Tunnel`-routed `KeyDownEvent` handler registered with
  `handledEventsToo: true` (the only way to still see a key the KeyBinding already marked `Handled`),
  intercepting Escape for whichever editor currently has focus. **Any future inline editor hosted
  inside `WorkspaceWindow` needs this same handler — Escape does not work there by default.**
- **A spliced-in editor widens its own row, and a `StackPanel` column sizes to its widest row.** The
  original R-h9c-8 scheme removed the value control and inserted the `TextBox` in its place — so the
  box's `MinWidth` (70px) became that ROW's width the moment it opened, and every column laid out
  after it in `Columns` (a horizontal `StackPanel`) visibly shifted right. Fixed by floating the box in
  a new transparent `Canvas` (`EditorOverlay`, layered on top of the content in a shared `Panel`) at
  the original control's translated position, while the original control merely goes `Opacity = 0`
  (which reserves its layout slot; removing it would not). **The general lesson: an editor that needs
  to be WIDER than its cell must never become a literal member of that cell's layout container — float
  it in an overlay that shares the container's coordinate space instead.** A useful side effect: since
  `EditorOverlay` is untouched by `SetItems`'s per-frame `.Clear()` of the Source/Load columns, an open
  Source/Load editor now survives a published-frame refresh better than it did before this change,
  even though that specific hazard (previous bullet) was not itself the target here.
- **A third follow-up, same session: the flat `MinWidth = 70` this bullet's own fix carried over
  (unused once nothing else in the row constrained it, but still oversized for a short value like
  "-1.5") was itself owner-reported.** Replaced with `ReadoutStripView.CalcInlineEditWidth(text,
  fontSize)` — the IDENTICAL formula `SchematicView.CalcInlineEditWidth` already uses for its own
  inline editor (average per-char width for IBM Plex Sans, floored at two characters) — set on open and
  recomputed on every `TextChanged`, so the box genuinely grows and shrinks live as the user types
  rather than being sized once. Growing to the right falls out for free from the overlay shape above:
  the box's `Canvas.Left` is set once at open time and never touched again, so widening only moves the
  RIGHT edge.

**The title-band padding was NEVER the real cause of "the title renders too high above the chart" —
and two prior fixes (R-h9r2-13, then this brief's own §4) both tuned it anyway, because nobody had
measured the actual gap.** The 3rd owner report of the identical complaint prompted actually measuring
it against the shipped code rather than adjusting the same few-pixel constant a third time: on a
representative panel the gap between the title band and the VISIBLE Smith circle was **~63px, ~11% of
the chart's own height** — two orders of magnitude bigger than `TitleBottomPaddingFraction`'s few
pixels, which is exactly why tuning it twice never visibly helped. **The real cause was
`HarmonicaPanelRenderer.AnnulusHeadroom`**, R-h45-4's panel-wide 20% shrink (`k=1/(1+0.25)`,
`IntrinsicGlyphScale.DefaultMargin`) that reserves room around the ENTIRE Smith circle so a marker for
a device whose intrinsic Γ is legitimately outside the unit circle (§4.5 consequence 2 — ordinary, not
an error) is never clipped at the panel edge. That shrink is applied UNIFORMLY on all four sides via a
scale about the panel's own centre — so half of the freed-up space sits above the circle where the
title already lives, and half below where nothing does; neither prior fix touched it because both
were reasoning about the title band in isolation from what the chart itself does within `chartSize`.
Presented with the actual trade-off (a real, deliberately-built, but never empirically-measured-against
real device data safety margin, vs. a visibly tight chart), the owner chose to **remove the margin
entirely** (`AnnulusHeadroom = 0`, AskUserQuestion, 2026-08-13) and explicitly accepted that a
sufficiently far-out intrinsic glyph can be clipped at the panel edge again — the exact failure mode
R-h45-4 was built to prevent. `IntrinsicGlyphScale.DefaultMargin` itself is untouched (0.25) — it
governs the compression CURVE (how a glyph's position reads), a distinct question from whether the
panel shrinks to make room for it, and the request was about the panel, not the curve. **General
lesson worth keeping: when a repeated visual complaint survives a plausible-looking fix twice, measure
the actual pixel gap against the shipped renderer before touching the same constant a third time** —
the fix that finally worked took five minutes once the real number was in hand; the two before it
spent that same five minutes each on the wrong knob.

**The strip-rebuild-destroys-an-open-editor hazard, and how it was closed for the new Settings
column.** `ReadoutStripView.SetItems` (Source/Load/MXP/MXE) and `SetInputs` (the input half) both run
on every published frame and both used to handle this differently: `SetItems` clears and rebuilds its
four columns unconditionally (safe only because none of THOSE rows survive a rebuild anyway — an open
editor there gets destroyed and reopened as a stale row every published frame, a pre-existing gap this
brief did not touch), while `SetInputs`'s original always-live-`TextBox` scheme used a shape signature
plus per-row `UpdateInPlace` specifically so a solve landing mid-keystroke could not stomp the caret.
R3C's new Settings column (double-click-to-edit, like Source/Load) needed the SAME discipline
`SetInputs` already had, extended to cover "a row is mid-edit" rather than just "a TextBox has focus":
the column is built ONCE (its shape — the same 7 keys, in the same order, every time — never changes,
since `HarmonicaInputs.Build` always emits them) and every later call only WRITES into the existing
rows, skipping a row's value slot entirely while its own `SettingsRowState.IsEditing` is true. The
decision itself (`ReadoutStripView.SettingsRowMayBeOverwritten(bool isEditing)`) is a bare pure
predicate for exactly this reason — Ui.Tests cannot construct a live Avalonia control to prove a real
`TextBox` survives a refresh, but the boolean logic gating it is fully testable without one.

**The title band's render/hit-test coupling** (`HarmonicaPanelRenderer.TitleBandHeight`/
`GammaToCanvas`/`CanvasToGamma`) needed nothing new here beyond what R1B already documented — the 85%
size factor and the bottom-padding constant both flow through the same `TitleBandHeight` both
directions already call, so the coupling that fixed R1B's render-vs-hit-test bug could not be
reopened by construction. One thing worth stating that the existing comments do not: **the 7.0pt
floor is deliberately NOT scaled by the new 0.85× factor** — a panel small enough to hit the floor is
already at the smallest legible size, and shrinking the floor itself would only make an
already-clamped title harder to read for no space saved.

**A real, pre-existing gap found while surfacing the "solved Vgs" R3C §3 asked for, worth flagging
here because a future maintainer touching bias/Idq will otherwise assume the opposite.** The removed
readout-half "Vgs" row used to show the literal text `"(from Idq)"` whenever the bias was
current-driven — never an actual number. Searching the whole repo for how `Bias.Idq` is consumed
confirms why: `HarmonicaContext.Apply` substitutes a bare `model.Bias.Vgs ?? 0.0` whenever `Vgs` is
null, and nothing anywhere runs the "1-D secant on the DC solve" the tooltips and doc comments
describe. `Idq` is round-tripped and persisted (`.charm`, `CharmIo`) but never actually drives a
solve. R3C §3 preserves the informational text (now the Vgs Settings input's own `Placeholder`) rather
than inventing a number — implementing the secant itself is solver work and out of this brief's scope
(§6's guardrails).

## §1.4 — the drag frame's render cost, not the solve, is most of what the owner saw (brief-harmonicarf-r3b, 2026-08-13)

**The solve is no longer the story.** After §1's evaluator work, a mid-drag L1-marker frame's SOLVE
side (tier-A 46-solve sweep + dataset + loadline) measures **7.3 ms** — down from the brief's own
~33 ms baseline. What was never measured before is the REST of the frame, and it turns out to be the
larger half.

**Measured** (`HarmonicaDragFrameBreakdownTests`, `Category=Benchmark`, real solver + real
`HarmonicaPanelRenderer` SkiaSharp draw calls, a REAL carried-forward contour layer — the drag starts
from an already-solved 37-point grid, exactly as §1's own carry-forward rule keeps its polylines on
screen frozen through every drag frame, which a from-empty measurement would have understated):

| stage | 1x (1600×1000) | 2x / Retina (3200×2000 px) |
|---|---|---|
| solve (tier A + dataset + loadline) | 7.3 ms | 7.3 ms |
| **render** (2 Smith panels w/ 30 carried polylines + loadline + power sweep) | **11.5 ms** | **21.2 ms** |
| SolvePool.Submit → Completed round trip | ~0.0 ms | ~0.0 ms |
| **measured total** | **18.9 ms (53 fps upper bound)** | **28.5 ms (35 fps upper bound)** |

**The render is real and was previously invisible** — `HarmonicaRenderBudgetTests`' own R4 note said
the readout strip "costs a layout pass, not a frame of this number," which was correct but left the
CANVAS render itself unmeasured for an actual drag-shaped frame (a carried contour layer, not an
empty grid). It roughly **doubles from 1x to 2x**, which matters directly: a Retina/HiDPI display
(the ordinary case on macOS, one of this repo's three target platforms) pays close to the WHOLE
60 fps frame budget on the render alone, before the solve, the readout strip, or anything Avalonia
itself does are even added.

**Per-panel breakdown, the four panels drawn in isolation at their own real placement size** (not the
whole canvas — an earlier pass of this measurement drew each at full-canvas size, overstating every
panel; fixed to each panel's own sub-rect: Smith 800×600, loadline/power-sweep 640×500, matching
`RenderAt`'s own layout):

| panel | @1x | @2x |
|---|---|---|
| SmithPower | 2.40 ms | 7.01 ms |
| SmithEfficiency | 2.24 ms | 6.76 ms |
| Loadline | 1.13 ms | 1.42 ms |
| PowerSweep | 0.25 ms | 0.42 ms |

**Neither the loadline nor the power-sweep panel is the bottleneck** — combined they are 1.4 ms @1x /
1.8 ms @2x, a small fraction of the total. **The two Smith charts dominate**, at roughly 4–17× the
cost of the other two panels each, and scale far worse with device pixel count (nearly 3× from 1x to
2x, against loadline's ~1.3× and power-sweep's ~1.7×) — consistent with them being the panels that
draw the grid-point dots (37), markers, glyphs, contour polylines AND the Smith-chart chrome (circles,
grid lines, title rows) all at once, where the other two panels draw a handful of simple curves.

**Frozen contour DATA is not the same as frozen contour PIXELS — worth stating precisely, since it is
easy to mis-hear "carried forward" as "free."** R-h9r2-1's freeze means the 30 iso-line polylines are
not re-solved/re-fit/re-rastered during a drag, and that is genuinely true and unchanged. But
`HarmonicaPanelRenderer.DrawContours` is immediate-mode Skia with, by its own doc comment, "no
geometry cache" — it re-issues every `DrawPath` call from scratch on every repaint, and the panel DOES
repaint every drag frame (the marker glyph and power-sweep curve are live, which triggers
`InvalidateVisual` on the whole canvas). **Measured, isolated** (re-rendering the same frame with
`Contours` cleared): the 30 frozen polylines cost **1.0 ms @1x / 1.4 ms @2x** of the render total above
— real, but a small (~7–9%) share. The render cost is dominated by everything else on the panel (37
grid-point dots, markers/glyphs, Smith-chart chrome, the loadline and power-sweep curves), not by the
contours specifically. Caching the frozen layer as a pre-rendered picture/bitmap and compositing it
was considered as a follow-up but not built — the measured payoff (≤1.4 ms) does not justify it on
its own; it would only be worth doing as part of a broader render-caching pass across the whole panel.

**What could not be measured, and why, named explicitly rather than left implicit:**
- **The §7.5 readout-strip rebuild** (`ReadoutStripView.SetItems` — real Avalonia
  `StackPanel`/`TextBlock` construction, ~37 items → ~70–110 controls for this fixture, every
  frame). `Ui.Tests` may not call Avalonia runtime APIs (a hard project rule — SkiaSharp canvas
  drawing is not one of those, which is why the render above IS measurable), so this cannot be
  benchmarked headlessly. **`ReadoutStripView.LastSetItemsMs` (new)** self-times the call; reading it
  during the interactive check below is how this gets a real number.
- **The Avalonia compositor/dispatcher round trip** (the worker-to-UI-thread `Dispatcher.Post`,
  `InvalidateVisual`, and whatever layout/compositing Avalonia itself does around the raw canvas
  draw) — structurally unmeasurable outside a live `Application`/`Window`, for the same reason.

**The honest accounting:** measured solve+render+pool is 18.9–28.5 ms depending on device scale,
against the owner's ~90 ms (~11 fps) observation. The gap (~60–70 ms) is therefore concentrated in
exactly the two unmeasurable stages above, not spread thin across many small costs — which is a
useful, falsifiable claim for the interactive check to confirm or refute (read `LastSetItemsMs` and
compare a real drag's actual fps against the 35–53 fps upper bound this file computes from the
measurable stages alone).

## §4 — the render backdrop cache, and the pixel-mismatch bug that guarded it (brief-harmonicarf-r4, 2026-08-13)

`HarmonicaBackdropCache` (new, `src/Ui/Harmonica/Renderers/`) rasterises a Smith panel's Layer A
(chrome + frozen contour polylines + optimum cross) and Layer B (grid-point dots) once into offscreen
`SKSurface`s and blits them back — one instance per panel, owned by `HarmonicaCanvas`, never static.
`HarmonicaPanelRenderer.DrawSmithPanel` falls back to its original, byte-identical uncached draw when
no cache is supplied (export, Copy Plot, a one-off render).

**§4.5's own correctness gate — cache-on vs cache-off must be pixel-identical — did not hold on the
first cut, and the reason was subtle enough to be worth recording precisely.** `HarmonicaBackdropCacheTests`
caught it directly (`CacheOnVsOff_ArePixelIdentical_ForAStaticScene` et al.), initially failing with
~5% of pixels differing by up to 199 levels/channel — nothing like ordinary antialiasing rounding.
Root-caused to **three independent, compounding effects**, fixed in order:

1. **AA sub-pixel phase mismatch (the dominant one, ~9500 px).** An offscreen raster's own pixel grid
   always starts at phase 0 at its local origin. The live canvas, by contrast, places chart-local
   (0,0) at whatever FRACTIONAL device pixel its accumulated transform happens to land on — `ChartBox`'s
   margin/title-band arithmetic is essentially never pixel-integral. Rasterising Layer A/B at phase 0
   and blitting onto that fractional position forces Skia to resample the whole image, reprocessing
   every antialiased edge in the backdrop differently from the uncached vector draw. **Fixed** by
   reading `canvas.TotalMatrix` at the point of render, baking that exact matrix into the offscreen
   surface (`SetMatrix`, not a bare `Scale(deviceScale)`) shifted by only the INTEGER part of where
   local (0,0) lands (`floorX`/`floorY` — an integer translate cannot change AA phase), then blitting
   that integer shift back in raw device space (`canvas.SetMatrix(Identity)`, bypassing whatever CTM
   was active) — an integer-aligned, same-size copy needs no resampling at all. General on purpose
   (matrix-derived, not `deviceScale`-arithmetic-derived): verified to hold under an outer 2x HiDPI
   scale composed with a fractional outer translate too
   (`CacheOnVsOff_ArePixelIdentical_At2xWithAnOuterFractionalTransform`), not just the test harness's
   simplest identity-CTM case.
2. **Fractional destRect size (~300 px on its own).** `chartSize` (a `double`) fed a `Ceiling`d integer
   pixel size for the raster but the blit `destRect` used the un-ceiling'd fractional `chartSize`
   directly — a tiny (`pixelSize/deviceScale`)⁄`chartSize` rescale on every blit. Folded away by the
   same fix: `destRect` is now the raster's own integer extent, never `chartSize`.
3. **Double alpha-blend rounding through a transparent offscreen background (~28 px, ≤2 levels/channel
   — real, not merely theoretical).** Layer A clears to `SKColors.Transparent`, so every antialiased
   edge is 8-bit-rounded once when rasterised and AGAIN when composited onto the live canvas — two
   roundings where the uncached draw does one. **Fixed for Layer A** by clearing to the panel's real
   (opaque) background color instead: every edge blends against it exactly once, matching the uncached
   draw, and the blit degenerates to an exact copy (opaque source, no blend math needed). **Layer B
   (the grid-point dots) can't take the same fix** — it's sparse, so it can't be pre-filled with a
   uniform opaque background without occluding Layer A underneath it. Instead Layer B is **fused**
   directly onto a COPY of Layer A's already-opaque pixels in one compositing pass
   (`HarmonicaBackdropCache.GetOrRenderFusedWithLayerB`) rather than blitted as its own second
   translucent layer — exactly one rounding step per pixel, the same as the uncached path drawing dots
   directly over the already-rendered chrome. `LayerBRebuilds` still counts only when Layer B's OWN key
   (grid points/theme/chartSize/matrix/pixel size) changes, not when a recompose is forced by Layer A
   changing underneath it (`ChangingContours_RebuildsLayerA_NotLayerB` pins this distinction) — an F16
   offscreen color format was tried first as a precision fix and made things WORSE (6365 px, likely an
   implicit linear-light blend Skia applies for F16 targets), which is why the fused-compositing
   approach was built instead of chasing more bits.

**After all three: 0/176,400 differing pixels, cache-on vs cache-off, including at 2x with an outer
fractional transform.** All 15 `HarmonicaBackdropCacheTests` (bit-exact identity, and one test per
invalidation-key field — contours, levels, optimum, title/subtitle, grid points, panel rect, device
pixel scale, theme, `ShowGridPoints` toggle, iso-line labels, the R-h9r2-1 carried-list-reference
case) pass.

**Per-panel render cost, warm steady state of a marker drag** (`HarmonicaDragFrameBreakdownTests`,
`Category=Benchmark`, same 37-point carried-forward fixture §1.4 used, best of 9, measured alone),
directly against §1.4's own "before" table:

| panel | @1x before | @1x after (cache warm) | @2x before | @2x after (cache warm) |
|---|---|---|---|---|
| SmithPower | 3.30 ms | **0.16 ms** | 10.06 ms | **0.53 ms** |
| SmithEfficiency | 3.03 ms | **0.16 ms** | 9.84 ms | **0.53 ms** |

(The §1.4 "before" figures quoted above are re-measured on today's tree, not the original 2.40/2.24/
7.01/6.76 ms figures — this tree carries 37 Γ points/39 polylines against §1.4's 37/30, and §1/§3's
already-landed convergence fixes changed the exact grid, so the two are close but not identical; both
are reported as measured rather than reconciled, per this repo's own measurement-honesty convention.)

**Far better than §4.2's own "roughly halved, 3–4 ms @2x" prediction, and worth explaining rather than
just believing.** The prediction priced a naive two-separate-translucent-layer blit against "order 1–2
ms" for a raw 7.7 MB RGBA CPU copy. What's actually being blitted after the fused-compositing fix is
ONE opaque, axis-aligned, integer-pixel-aligned image — a case Skia's raster backend copies near
memcpy-speed rather than through general blend math, and the fusion means there is only ONE blit per
frame (not two) plus a handful of cheap live draws (marker glyphs, the reachable-region wash). The
speedup (≈20×, not ≈2×) reflects that the STATIC content (grid + 30–39 polylines + dots) was the
overwhelming majority of the original render cost, and a warm cache now pays for essentially none of
it every frame — consistent with, not contradicting, §4.1's own diagnosis that the cacheable share was
"most of each [panel], not 1.4 ms across both."

**§4.6 — `ReadoutStripView.LastSetItemsMs` was not read this pass.** It requires a live interactive
Avalonia session (real `StackPanel`/`TextBlock` construction — `Ui.Tests` may not call Avalonia runtime
APIs, per §1.4's own note above), which this session had no way to drive. Per the brief: not fixed
here regardless (out of scope), and the number is still worth reading in the owner's own interactive
check — §1.4's own estimate (~60–70 ms of the observed ~90 ms sitting in the strip rebuild + Avalonia
round trip) is now the DOMINANT term by a wider margin than before, since §4 just cut the render side
from ~7–10 ms/panel to ~0.2–0.5 ms/panel.

**A pre-existing, unrelated test failure was found and fixed while running the full suite as this
brief's own gate.** `HarmonicaPanelTests.Tier8_AGridWithAHole_DrawsNoContourAndNoFillInsideTheExcludedDisc`'s
own fixture (`BuildGridWithADeliberateHole`, `maxGamma: 0.85`) started failing its own precondition
(`Assert.InRange(grid.HoleCount, 1, …)`, actual 0) — not from anything in this brief, but from §3's
already-landed `PinSearch.Run` bracket fix (`src/Harmonica/RESOLVED.md`'s own §3 entry), which closed
most of the bracket-stage holes this smaller 31-point fixture used to rely on for "a few holes."
Scanned `maxGamma` 0.85–0.98 in 0.02 steps (deterministic — no RNG in this solve path): 0.90 reproduces
2/31 holes reliably; the test now uses that instead of 0.85, with a comment recording why.

## §5 — the drag-size FPS asymmetry: measured, not guessed, and it is real but small (brief-harmonicarf-r4, 2026-08-13)

The owner's own diagnosis (§5.1) named the mechanism exactly: `PinSearch.Sweep`'s `priorLevelSpectra`
(R-h9r2-19's "lever 1" — the previous FRAME's converged spectrum, tried first at every Pin level)
is a near-perfect seed on a small drag move and can be an actively misleading one on a large move that
lands the termination in a different HB solution basin, since the solution surface across the
termination plane is not smooth. Measured directly rather than assumed
(`tests/Harmonica.Tests/DragSeedPolicyTests.cs`, `Category=Benchmark`, Hero 2's GaN HEMT under
25 Ω/80+j10 Ω — the same fixture §1/§3 already use, chosen because the shipped default's own
unmarked-band terminations don't compress at all — shipped `PinMaxDbm=50`, §1's early stop already
landed, best-of-5 per frame after one discarded warm-up run):

| policy | small jump (\|ΔΓ\|≈0.004) | tangential control (\|ΔΓ\|≈0.13, const \|Γ\|=0.5) | large jump (\|ΔΓ\|≈0.99) |
|---|---|---|---|
| A — today (always reuse) | **9.23 ms** | **10.72 ms** | 13.76 ms |
| B — owner's (never reuse) | 12.10 ms | 12.01 ms | **11.94 ms** |
| C — hedged (below) | 9.22 ms | — | **11.88 ms** |

**Policy B does not win outright — measured, not assumed away.** The brief's own decision rule was
"if B's small-drag time is within noise of A, delete lever 1 and take B." It is not: B is ~24% SLOWER
than A at |ΔΓ| ≈ 0.004, a small, reproducible, above-noise gap (stable across repeated runs), and the
tangential control shows the same thing at a genuinely large per-frame Γ MOVEMENT (0.13) that never
approaches a harder region — so this is not the "large is also hard" confound §5.3 warned about; lever 1
is genuinely still winning there. So Policy B is not adopted outright.

**The threshold was found by scanning the crossover, not picked**
(`AvsB_CrossoverPoint_WhereLever1StopsHelping`, same fixture, single jump from a converged base point at
each size): A wins clearly through |ΔΓ| ≈ 0.15, ties through ~0.20–0.25, and B starts winning from
~0.30. `HarmonicaSolver.LeverOneDeltaGammaThreshold = 0.20` sits just past where A stops winning
outright — Policy C, the hedge, is what shipped: lever 1 is read only when the LARGEST single-band Γ
move since the previous frame (a freshly-marked band counts as infinite) is under this threshold.
Wired in `HarmonicaSolver.Solve` (new fields `_lastTerminationGammas`, `Lever1DisabledCount` — a
counter, not a stopwatch, gated by `HarmonicaSeedPolicyTests`), not in `PinSearch.Sweep` itself, which
is unchanged and still does exactly what its own doc comment says.

**Gradual, with one real cliff, not a clean either/or.** `PolicyA_FrameTimeVsJumpSize_GradualOrCliff`:
frame time rises smoothly from 9.7 ms (|ΔΓ|=0.01) to 11.9 ms (|ΔΓ|=0.20), then SPIKES to 18.4 ms at
|ΔΓ|=0.30 (only 103 Newton iterations there — fewer than the 118 at 0.20 — so the extra ~6.5 ms is not
"more iterations," it is one or more rungs' Newton solve taking an internal continuation-stepping
detour, §5.3's own predicted cliff mechanism), then drops back to 12.8/12.0 ms at 0.45/0.60. The cliff
is narrow and Γ-position-dependent rather than a clean function of |ΔΓ| alone — worth knowing, not
worth chasing further this pass.

**A large-jump drag frame's own factor over a small one, stated rather than asserted against a
target:** under the SHIPPED policy (C), 11.88 ms / 9.22 ms ≈ **1.29×**. Nothing like the owner's
subjective "roughly two thirds unaccounted for" 11 fps experience — which is exactly what §4's own
combined reading (below) explains.

**§5.4 — the no-op frame, independent of the policy work, landed too.** A mid-drag marker frame whose
Γ has not moved (quantised to `HarmonicaViewModel.DragNoOpGammaTolerance = 1e-4`, an order of magnitude
under both a Smith glyph's own on-screen resolution and every readout's decimal precision) past the
last frame ACTUALLY submitted to the pool never reaches `SolvePool.Submit` at all — `RequestFrameOnMarkerRelease`
returns `-1` (matching `DragGridPoint`'s own sentinel) and increments `NoOpDragFrameSkipCount`, a
counter. **Release is never skipped by this**, even when it lands within tolerance of the last mid-drag
frame — a real, full-quality solve always runs on release, matching `DragGridPoint`'s own "mid-drag is
free, release is real" shape. Gated on counters, not a stopwatch, exactly as the brief asked
(`HarmonicaDragTests.MidDragMarkerFrame_WithinToleranceOfLastSubmitted_IsSkipped_GatedOnACounterNotAStopwatch`,
`.MarkerReleaseAlwaysSolves_EvenWithinTheNoOpTolerance`).

**§4 and §5 measured together, as the brief's own §5.5 asked.** With Layer A/B's cache warm (§4:
~0.16–0.53 ms per Smith panel, down from ~3–10 ms), the render's contribution to a drag frame is now a
small fraction of the SOLVE side above (9–14 ms) rather than comparable to or larger than it — so the
solve, and specifically the seed-policy asymmetry this section measures, is now the dominant and
VISIBLE cost in a drag frame, confirming §5.5's own prediction ("the asymmetry will be more visible
after §4 than before it, not less") rather than needing a separate render-included re-measurement:
render is close enough to zero now that solve-only numbers above already stand in for total frame time
to within the ~1–3 ms `HarmonicaDragFrameBreakdownTests` measured for the non-Smith panels.

**Not chased further, named rather than silently dropped:** the ~60–70 ms `ReadoutStripView.LastSetItemsMs`
gap from §1.4/§4.6 is unmeasured in this headless environment and is now, by a wide margin, the largest
unaccounted-for piece of the owner's original ~90 ms/11 fps observation — bigger than everything §4 and
§5 together move.

## A grid-point drag was costing the whole tier-A power sweep (brief-harmonicarf-r3b §2, 2026-08-13)

**A gesture that changes no circuit state was costing 46 HB solves.** `HarmonicaViewModel.
DragGridPoint(dragging: true)` routed every mid-drag frame through `RequestFrame`, whose
`OptionsFor(..., dragging: true)` sets `SkipContours = true` — but `SkipContours` only ever skips the
CONTOUR GRID build; `HarmonicaSolver.Solve` runs tier A's whole `PinSearch.Sweep` ladder
unconditionally, every frame, at terminations a grid-point drag never touches at all (the dragged Γ
is a sample the grid sweeps LATER, not a termination anything solves against). R-h9r2-4 chose the
"splice the moved point into the carried `GridPoints` list, display only" shape precisely so this
gesture would be cheap, then routed it through the full frame pump anyway.

**Fix:** a mid-drag grid-point frame no longer calls `RequestFrame`/touches `_pool` at all. It splices
the moved Γ into the CURRENTLY PUBLISHED `Frame.SmithPower`/`SmithEfficiency` grid-point lists
directly (the existing `ApplyGridPointOverride` helper, already built for exactly this splice) and
sets `Frame` — an `[ObservableProperty]`, so the assignment itself raises `RedrawRequested` via
`OnFrameChanged`. Same no-re-solve shape as `SetMarkerVswr`/`ToggleMarkerVswrEnabled`, applied to a
grid point instead of a marker overlay. `CustomGrid` stays untouched mid-drag (unchanged from
before — only committed on release), and release (`dragging: false`) is unchanged: it still commits
into `CustomGrid` and requests a real frame with `ReuseUnchangedGridPoints = true`.

**Gated on a counter** (`HarmonicaGridPointDragTests.
MidDragGridPointFrame_CostsZeroHbSolves_GatedOnACounterNotAStopwatch`): five simulated pointer-move
events during a drag leave `SolvePool.StartedCount` and `HarmonicaSolver.LastSolveCount` unchanged,
while the glyph's own Γ visibly tracks the last move — and release still submits a real solve. All
6563 `Ui.Tests` pass.

## macOS native menu: docked focus and the crash (brief-harmonicarf-r3a, 2026-08-13)

The macOS "menu not shown when docked" bug and the "crashed switching apps / opening Settings" crash
were ONE bug, not two, and R2B's own diagnosis of the crash ("a genuine Avalonia.Native race this
view cannot see into") was wrong — the mechanism is fully knowable from Avalonia 12.0.3's own source
(`src/Avalonia.Native/AvaloniaNativeMenuExporter.cs`, `IAvnMenu.cs`).

**The standing invariant, from here on: on macOS, a window's `NativeMenu` instance is chosen ONCE
and never changes for that window's whole lifetime. To change what the menu bar shows, mutate that
instance's `Items` — never call `NativeMenu.SetMenu` on a window a second time with a different
instance.** Four facts pin this down:

1. **One `AvaloniaNativeMenuExporter` per `TopLevel`, created once, never torn down.** Every
   `NativeMenu.SetMenu(window, x)` for that window routes to the SAME exporter, for the window's
   whole life.
2. **The exporter binds to the FIRST `NativeMenu` instance it is ever given, permanently.**
   `__MicroComIAvnMenuProxy.Initialize` is called only on that first bind. Its own `Update`:
   ```csharp
   internal void Update(IAvaloniaNativeFactory factory, NativeMenu menu)
   {
       if (menu != ManagedMenu)
           throw new ArgumentException("The menu being updated does not match.", nameof(menu));
   ```
   A second, different instance handed to the same window throws — synchronously, on the calling
   thread, out of `NativeMenu.SetMenu` itself.
3. **`SetMenu(window, null)` is not a clear — it substitutes a brand-new empty `NativeMenu`**
   (`_menu = menu ?? new NativeMenu();`), so calling it on a window that already holds a real menu
   ALSO throws, for the same reason (the throwaway empty menu is not `ManagedMenu` either) — R2B's
   own "defensive clear" was therefore a poisoning step, not a safety step, and is now gone.
4. **`_menu` is assigned BEFORE the throw, and a later dispatcher-queued reset re-reads it.** Any
   `NativeMenuItem` added to or removed from the exporter's *original* menu calls `QueueReset()` →
   `Dispatcher.UIThread.Post(DoLayoutReset, ...)`. That queued call re-runs `SetMenu` with the now
   *poisoned* `_menu` and throws again — on the dispatcher, where no call-site `try`/`catch` can
   reach it. This is the exact owner-reported crash: a menu-item mutation (rebuilding the Window menu
   on `Activated`, or opening Settings) some time AFTER the poisoning attach is what actually brings
   the process down, which is why the failure looked delayed/intermittent rather than immediate.

**The fix (`HarmonicaMenuView.RecomputeAttachment`, split into `AttachToWindowOutright` +
inject/withdraw):** a torn-off document or the standalone binary still owns its hosting window
outright via `NativeMenu.SetMenu` (that window has never had a menu, so this is always the FIRST
bind and always succeeds). A **docked** document never calls `NativeMenu.SetMenu` on the
`WorkspaceWindow` at all — that window's exporter is already permanently bound to circuitRF's own
app-menu instance (`WorkspaceWindow.AttachNativeMenuAtApplicationScope`, at startup). Instead, on
docked focus, the document's own top-level items (Markers / Display / Grid — not File/Edit/Help,
which circuitRF's bar already shows) are appended to that SAME instance's `Items`
(`HarmonicaAppMenuInjector.Inject`), and removed again — by reference, never by header match — on
blur (`.Withdraw`).

**The item-`Parent` validator forces a THIRD rendering, not a copy.** `NativeMenu`'s list validator
throws `InvalidOperationException` for any item that already has a `Parent` — so the injected items
must be freshly-built `NativeMenuItem`s from `HarmonicaMenuViewModel`'s own collections/commands
(`HarmonicaAppMenuInjector`), never `_ownMenu`'s own children. This mirrors the view's existing
"TWO SURFACES, HAND-MIRRORED" shape (the in-window `Menu` and the standalone `NativeMenu` are already
two independent renderings of one source) — the injected set is simply a third.

**`WorkspaceViewModel.TryWireWindowFocusTracking`'s Harmonica/WBond exclusion already closed the
§2.3 ordering trap**, before this brief: `AttachSharedNativeMenuIfMacOS` is gated on
`doc is not HarmonicaDocument and not WBondDocument`, so a torn-off harmonicaRF/wBond window can
never receive circuitRF's shared app-menu instance regardless of activation order (each owns its own
per-window attach). This makes the invariant type-based rather than order-dependent — verified, and
now pinned by a dedicated test, rather than left as "today's ordering happens to favour it."

**`Dispatcher.UIThread.UnhandledException` (`App.WireNativeMenuDispatcherBackstop`) is a floor, not
the fix** — it exists only because a queued `DoLayoutReset` throw is, structurally, unreachable by
any call-site `try`/`catch`. It matches ONLY `ArgumentException("...menu being updated does not
match...")` whose stack contains `Avalonia.Native`; a blanket handler was rejected on purpose.

## harmonicaRF menu round (owner bug list, 2026-08-15)

**Display ▸ Contour Harmonic going stale after a K edit was real, and had TWO independent causes —
the first fix pass only caught the first one.**

1. `HarmonicaAppMenuInjector.BuildDisplay` — the THIRD rendering of the menu, injected into
   circuitRF's own app menu while a **docked** harmonicaRF document has focus (the in-window `Menu`
   and the standalone/torn-off `NativeMenu` are the other two) — built Contour Harmonic from three
   hardcoded items (`f₀`/`2f₀`/`3f₀` via `SetGridHarmonicCommand`), the exact bug
   `HarmonicaHarmonicMenuItem`'s own doc comment already named as fixed elsewhere. Fixed by building
   the submenu from `vm.ContourHarmonics` directly, the same collection the other two surfaces read.

2. **The real reason the bug survived that fix, on macOS specifically.** Neither NativeMenu-based
   surface (standalone/torn-off, or docked-injected) subscribes to `ContourHarmonics` directly —
   `HarmonicaMenuView` only listens to `SourceBands`/`LoadBands.CollectionChanged`
   (`OnBandsChanged`), and rebuilds the NativeMenu's Contour Harmonic submenu as a side effect of
   that. `HarmonicaMenuViewModel.RebuildBandMenus` used to call `Sync(SourceBands, …)` /
   `Sync(LoadBands, …)` — whose own `Clear()`/`Add()` raise that CollectionChanged SYNCHRONOUSLY —
   **before** `SyncContourHarmonics()`. So by the time `OnBandsChanged` fired and read
   `vm.ContourHarmonics`, that collection had not been rebuilt for the new K yet — it read the OLD
   K-length list, one call behind. `SyncContourHarmonics()` never got a rebuild trigger of its own,
   so nothing rebuilt the NativeMenu submenu again once it finally did update. This is why it looked
   *intermittent* even after fix #1: correct immediately after some OTHER band edit trips
   `OnBandsChanged` a second time, wrong right after the K edit itself. The in-window `Menu` was never
   affected — its `ContourHarmonics` `ItemsSource` binding updates independently of call order. Fixed
   by reordering `RebuildBandMenus` to call `SyncContourHarmonics()` FIRST, so every later
   `SourceBands`/`LoadBands` subscriber — on any surface, present or future — sees the new K's
   `ContourHarmonics` already in place. Pinned by
   `HarmonicaMenuAndInputTests.ContourHarmonicMenu_IsAlreadyAtTheNewK_WhenSourceBandsCollectionChangedFires`,
   which reproduces the ordering directly against a `SourceBands.CollectionChanged` subscriber with no
   Avalonia/NativeMenu platform involved (confirmed to fail — observed count 3, not 5 — against the
   old call order before the fix).

**"harmonicaRF ▸ Copy Plot/Copy Readouts/Copy Termination Set" (owner's literal wording) turned out
to mean the *Edit* menu's copy of these three items, not only the docked-injected `harmonicaRF`
top-level menu's copy.** The first pass removed them from `HarmonicaAppMenuInjector.BuildHarmonicaRf`
(the one place literally titled "harmonicaRF") on the reasoning that the other `X->Y` bugs in the same
report all named their literal parent menu. The owner still saw them on macOS afterward — they meant
Edit ▸ Copy Plot / Copy Readouts / Copy Termination Set, which every surface still carried. Removed
from both the NativeMenu and in-window Edit menus too; `CopyPlotCommand`/`CopyReadoutsCommand`/
`CopyTerminationsCommand` and their hooks stay wired, same convention as Grid ▸ Solve Now and
Markers ▸ Reset to Defaults above.

**The `.npy.npy` suggested-filename bug (Export Data…) was the identical class of bug
`ExportTestbenchAsync` had already fixed once, in the same file, and the fix note said so at the
time.** `SaveFilePickerAsync`'s `DefaultExtension` already appends the extension; `ExportDataAsync`'s
`SuggestedFileName` was separately appending `".npy"` on top of it. `ExportTestbenchAsync`
(`HarmonicaView.axaml.cs`) carries a comment recording the exact same trap for `.csch` — the two call
sites simply hadn't been kept in sync.

**"Running the coarsest contour grid to keep up" (`FrameScheduler.RecordFrame`'s D4 message) is
retired** — harmonicaRF no longer has a coarse-grid tier-B rung worth naming in a user-facing string
(R-h9r2-2 already retired every OTHER per-rung message for the same reason; D4's was the one
message that survived that pass). `TierAHealthy` still latches `false` and is still the signal any
future caller should read — only the string is gone, so `StatusMessage` is simply `null` in this
case now.

## Rename Cell left the layout — and therefore the wires — behind (2026-08-18)

**Reported as:** "Rename Cell from the Project Tree renamed only the `.csch`; the `.clay` was not
renamed. Should a `.wBond` also be renamed if it's in the layout directory?"

**The layout half is a one-word omission with no defence.** `RenameCellAsync`'s primaries loop read
`new[] { ViewType.Schematic, ViewType.Symbol }`. Every other piece already handled Layout —
`CellFolder.ViewExtension` returns `.clay`, `ResolvePrimary` resolves it, `CellPersistence` has
`PrimaryLayout`. Only the list did not, and `UpdateCcellPrimary`'s own switch had no Layout arm to
match, so the `.ccell` kept naming the old file **and that is why the drift stayed invisible**: the
layout still opened, under a name that no longer matched its cell.

**The wBond answer is yes, and it is not cosmetic — it is the reason the layout fix cannot ship
alone.** `WBondCell.Resolve` attaches wires to artwork by **shared stem and nothing else** (WB40,
revised 2026-08-17). So renaming `layout/Amp.clay` to `layout/PowerAmp.clay` without its
`layout/Amp.wBond` does not leave two names disagreeing — it **detaches the wires**. The layout
reopens with none, and the bonds sit in a file paired with nothing, which `Resolve` then reports as an
orphan: the first the user hears of it, after the fact. Adding Layout to that loop without the wBond
rename would have introduced a wire-loss path that did not previously exist.

`WBondCell.RenamePairedWires` is now the single place that pairing is maintained across a rename;
any future caller that renames a `.clay` owes it that call. It refuses an occupied target stem rather
than overwriting (that file belongs to a different `.clay`), and touches only the file actually paired
with the old artwork — a differently-named `.wBond` in the same folder is an assembly house's bond
list, not this rename's business.

### The link rewrite matches by RESOLUTION, not by name

A placed wBond stores `File` as a path **relative to its own schematic**
(`WBondPlacement.ResolveLinkedPath`), so the folder rename alone leaves a same-cell link resolving
perfectly and it is renaming the FILE that breaks it — the two are one operation.
`CellUsageScanner.RewriteWBondLinks` resolves each candidate and compares it against the file that
moved, because two cells may each legitimately own a `layout/top.wBond` and a name-only match would
repoint the wrong one. It substitutes the old cell-folder segment first, which also **repairs a
cross-cell link (`../../oldName/layout/x.wBond`) that the folder rename had already broken** — a
pre-existing gap, since `RewriteCellReferences` only ever rewrote `CellRef`, never a wBond link.

**Known limitation, inherited from `RewriteCellReferences` and not made worse here:** the rewrite
edits other cells' `.csch` files on disk through `JsonNode`, while `RenameCellAsync` force-closes only
the documents under the renamed cell. A schematic in another cell that links this wBond and happens to
be open is rewritten underneath its session.

## A VAR row may write its unit inline, and used to lose the variable entirely (2026-08-18)

`RFfreq = 2 GHz` typed into a VAR's expression column silently produced **no variable at all** — see
`src/Core/RESOLVED.md` for the mechanism (parse error, swallowed by `Elaborator`) and for why the lift
is verified against the parser rather than the unit table. The Ui-side consequence:
`SweepAxisRowViewModel.GetVarUnit` reads the row's unit COLUMN, so it now applies the same lift. Both
have to agree or the editor would show a blank inherited unit and a preview reading "3 pts: 2 … 3"
for a sweep the engine runs at 2 … 3 GHz.

## Paste landed on top of the originals, and a wire click then froze the copy (2026-08-19)

**Reported as:** *"I selected all the objects in the schematic, copied them, pasted them… the paste
placed the components where I could not move them. Even though they were selected, I tried to
click-drag to move them, but they appear stuck and could only move in the horizontal direction."*
Plus a wish: *"it would be nice to paste objects relative to the user's current view."*

**Two independent defects, and the second is the one that actually froze the selection.** Reproduced
headlessly from the owner's own `.csch` (5 parts, 4 wires): copy-all + paste, then a press on the
pasted content.

### The press on a wire silently threw the selection away

`HandleSelectPress`'s per-segment branch (B1) called `Selection.SelectOneSegment` **unconditionally**,
while the branch right below it has always guarded the equivalent case for every other object kind
(`else if (!Selection.IsSelected(hit.Id)) Selection.SelectOne(hit.Id)` — i.e. *keep* a selection the
press landed inside). So pressing on a wire that was part of a 9-object pasted selection dropped all
nine and left a **single wire segment** selected, and a segment drag moves only perpendicular to its
own segment by construction. The owner pressed on a vertical run: horizontal-only motion, components
untouched. "Stuck, and only moves horizontally" is a literal description of a segment drag, not of a
constrained multi-drag — which is why looking at the drag clamps (`ApplyWireSlideClamp`,
`IsWireEndpointConnectedToUnselected`) first was a dead end: those *were* firing on the overlapped
paste, but they exclude pinned wires, and the components were free to move the whole time. The
headless repro is what separated the two — it showed the components moving 500 units while the wires
stayed put, which no "everything is stuck" theory survives.

A press on a wire that is part of a **multi-object** selection now moves the whole selection.
Segment editing still owns the click in every other case (nothing selected, or that wire alone
selected, or Shift held) — those three are pinned by tests, because the naive fix takes B1 out
entirely.

### Paste was paste-IN-PLACE, which is unusable inside one schematic

Nothing offset a pasted fragment: it kept its source coordinates, so copy-all + paste in the same
schematic buried every pasted object exactly under its original. That state is worse than untidy —
every pasted wire endpoint then coincides with an *unselected* original port, so the pasted wires are
**pinned** (they re-route instead of translating) and each moved pin sprouts an auto-wire stub the
moment the copy is dragged. Even with the click bug fixed, dragging the copy out of that pile left
eight stub wires behind.

`SchematicPasteGeometry` now places the fragment: outside the view → its bbox centre goes to the
viewport centre; fully inside the view → one connection-grid step off the original, so the copy is
visibly its own object without jumping somewhere the user is not looking. The delta is always a whole
multiple of P, which is what keeps every pasted pin on the connection grid (R7). A final pass nudges
diagonally if a pasted component would still land exactly on an existing one — that is the only thing
protecting the **headless / not-yet-laid-out** path, where there is no viewport and paste stays
in-place.

The viewport reaches the VM as `ViewportProvider`, a callback the canvas installs alongside
`ZoomToRectCallback` — pan lives in `SchematicCanvas` and only ever existed there. Both paste paths
(canvas Ctrl+V and the Edit menu) were separately constructing `SchematicPasteCommand`; they now share
`SchematicViewModel.PasteFragment`, because a placement rule applied by only one of them is a bug
waiting to be reported as "paste works differently from the menu".

### "Save Schematic As…" suggested `.csch` twice — the third sighting of one trap

`SaveFilePickerAsync` appends `DefaultExtension` itself, so a `SuggestedFileName` that already carries
the extension comes back doubled. This is the same defect already fixed twice in Harmonica
(`ExportTestbenchAsync`, then `ExportDataAsync`'s `.npy.npy`), and both fixes left a comment saying so
— but nothing audited the *schematic* save pickers, whose `SuggestedFileName` is `doc.Id`, and `doc.Id`
is the tab identity, which for anything opened from disk is the file name **with** its extension.
Scratch documents (`Id` = a plain title) never showed it, which is why it survived. Both tiers of
Save-As now pass the stem. The picked path also gets `.csch` appended when it comes back with no
extension at all, so the fix cannot turn into an extension-less file on a picker that does not apply
`DefaultExtension` (`DataExporterDialog`'s loadpull export records seeing exactly that with the
non-standard `.lpcwave`).

## wBond round 8 — the layout host: select-all, the snap glyph, the wire point, span, profile order (2026-08-19)

Six owner reports against the wBond layout host. Four had a cause that was not the obvious one.

### Ctrl/Cmd+A selected the geometry and no wires — the fix RETURNS FALSE on purpose

`LayoutEditorViewModel.SelectAllCommand` was doing its whole job (shapes *and* instances, a lesson it
learned once already); the wires live in the overlay's own selection and nothing reached them, so on a
wirebond cell — where the wires are most of what is on screen — the key read as doing nothing.

`WBondLayoutOverlay.OnKeyDown` now claims Ctrl/Cmd+A, selects every wire, and **returns false**.
`LayoutCanvas` treats false as "not consumed", so the same keystroke goes on to
`LayoutEditorViewModel.OnKeyDown` and its own Select All runs immediately after. Returning true would
have fixed the wires and broken the half that already worked — which is why the test asserts the
return value, not just the selection. Same pairing the context menu's own "Select All" already makes.

### The snap glyph was UNDER the wires, and a bigger glyph cannot fix that

Reported as "the geometry snap glyphs do not render if the zoom level is too high… perhaps they need
to be slightly larger?" They were rendering — inside `LayoutRenderer.Draw`, which is followed
immediately by `ILayoutCanvasOverlay.Draw` on the same Skia lease. The overlay paints **on** the
layout by design (WB23), so the glyph is under every wire.

Size is not a remedy and the arithmetic says so: the glyph is a fixed ~8.47 device pixels while a
wire's stroke and its vertex dots are proportional to zoom **with no clamp**, so any fixed glyph is
covered at some zoom. A bump moves the zoom at which it disappears; it does not remove it. The size
constants are untouched. What changed is order: `LayoutRenderOptions.DeferSnapMarker` suppresses the
in-pipeline draw, and `LayoutRenderer.DrawSnapMarkerOnTop` — which rebuilds the same path-space
transform from the same viewport rather than being handed one — runs after the overlay.
`LayoutCanvas` sets the flag **only when it has an overlay**; every export, thumbnail and one-shot
test render paints nothing afterwards and is byte-identical. A test asserts both halves (nothing
painted when deferred, and the same pixels as the in-pipeline draw once drawn on top).

### The vertex hitbox already matched the circle — the SEGMENTS were stealing the press

"It feels smaller than the circle, so I get a lot of misses." The tolerance was right:
`WireHitTest.VertexRadiusNm` is a floor under the caller's screen tolerance and equals the drawn
radius exactly. The misses were real anyway, because **both segments meeting at an interior vertex
pass through it**, so their distance there is ~0 while the vertex's own is however far off centre the
user clicked. With `pointBias = 2`, everything past half the radius went to the segment: the visible
circle was clickable only in the two lobes perpendicular to the wire — a hitbox that genuinely is
smaller than the circle, in the direction the hand moves along it.

`pointBias` cannot fix it. It is a ratio, so it only moves where along the wire the handover happens,
and raising it far enough to cover the dot starts stealing presses meant for the segment well outside
it. The rule is categorical instead: a press inside the drawn dot outranks any segment, full stop.
Just outside the dot the segment takes the press back — both directions are pinned, because a hitbox
*bigger* than the circle is the same complaint from the other side.

`VertexToWireDiameterRatio` also went 0.66 → 0.726 (the requested ×1.1). One constant still, read by
the renderer and the hit test alike.

### The span gesture moved a foot's z — and it exposed that "span" meant two different things

`ScaleSpan` lerped the moved foot along the chord in **all three** coordinates, so on the ordinary
wire (die at one height, substrate at another) changing the span raised or lowered the foot by
`Δz × (factor − 1)`. Invisible on a flat wire, which is why it survived.

The first fix held z and compensated the plan factor so the **3-D** foot-to-foot distance still scaled
by exactly `factor`, on the grounds that that was the number the panel printed. The owner then settled
the underlying question instead: *"the span of a wire should be defined as the XY distance of the wire
geometry. There should be no Z anywhere in the span calculation."* With that, the gesture is a plain
plan-view scale and the compensation is gone. **See the next section — this bug was a symptom.**

`WireEditsTests.Tier2_ScalingAnArraysSpan_PreservesTheRatiosBetweenMembers` is the test that caught
the first, uncompensated attempt: it measured the 3-D chord, and 1.4 came back 1.3991. It now measures
`SpanMetres` and is exact.

### Span is XY — and half the application already knew that

`Wire.ChordLengthMetres()` (3-D, feet to feet) is gone; `Wire.SpanMetres()` (XY) replaced it, and
nothing wanted the 3-D form. Every consumer was a *span*: the Array Inductance panel's Span row and
its landing-span diagnostic, the properties inspector's Span field, the DRC `span()` predicate, both
alt-drags' reference span, `ProfileProjection.PreferredMode`'s absolute-vs-normalised choice, and
`ScaleSpan`'s factor.

**The profile view was already right, and had been since WB-C1 (2026-08-07).** `WireEdits.ChordParameter`
— the span parameterisation the profile axis, the height scaling and the nudge all share — is XY only,
and wbond.md §6.2 records *why* in detail: a 3-D parameter makes a point's loop height feed back into
its own span position, so "scale the height" stops being well defined (a nominal 1.5× height scale
measured 1.498×). So the codebase held two spans that agree on every level-footed wire and diverge by
the foot drop on every chip-and-wire one — the axis said one thing, the readout beside it said another,
and the setter set the second. That is the shape of the z-drift bug above, one level down.

Level feet hide all of it, which is why nothing failed for this long — and, more pointedly, why
changing the definition broke **no existing test at all**: 7,949 `Ui.Tests` and every pre-existing
`WBond.Tests` case passed against both definitions, because the fixtures that exercise span have level
feet. A change no test can see is exactly the one to gate deliberately, so the new gate is in
`LoopHeightDefinitionTests`, beside §3.0's own definition tests and for the same stated reason: a suite
built on level feet cannot tell two definitions apart, and this is the second definition that needed
exactly that fixture. All three new cases were checked against the old 3-D form and fail there.

Untouched, deliberately: `Wire.PathLengthMetres` and `Grover`'s per-filament segment lengths are true
developed 3-D lengths and are what the physics integrates along. `Wire.FootDropNm` remains the feet's
z separation, and §3.0's floor — a loop height can never be below the foot drop — still holds.
Definition documented at `docs/design/wbond.md` §3.0a.

### The profile view drew the selected wire under its neighbours

Colour cannot express order. The wires of one array run within microns of each other in span-z, so a
neighbour drawn afterwards covers the accent completely. `DrawProfile` now collects every wire the
selection *touches* (a single picked vertex counts) and draws them last, in their existing relative
order — everything else keeps its exact place in the stack. The oracle is the accent's own pixel
count with and without a coincident neighbour: 292 vs **0** before the change.

### The Frequency row now holds its place instead of vanishing

It was hidden when capacitance was off, on the honest ground that the effective inductance is `L_arr`
at every frequency and the row would say nothing. But it sits directly under the switch, above the
cards the switch exists to be compared with, so hiding it moved every self-inductance up a row on each
flip — mid-comparison. `ShowFrequency` has stopped being a visibility flag and become "is there
anything to say": the row is unconditional in XAML, dimmed and hit-test-transparent, printing
`WBondPanelViewModel.FrequencyDisplay` — the frequency, or an em dash.

## The overmold `er` reaches four surfaces, and each is a separate route (2026-08-19)

The physics is in `src/WBond/RESOLVED.md` — one division, applied where **P** is filled. What is worth
recording here is the plumbing, because *"a setting written on one surface and silently dropped on
another"* is the failure this family of commands has produced repeatedly (the loop height that a design
stated and its wires disagreed with, owner 2026-08-17). The permittivity has that exact shape **and
moves no wire**, so nothing on screen would show it.

**The parameter is `er`, declared `"1"` rather than blank.** Blank is the convention for `Temp` and
`GroundPlane` — "the design decides" — but `er` sits beside the Include-capacitance checkbox in the
parameter panel, and a box showing nothing would not say what medium the capacitance beside it was
computed in. It inherits from an imported design through `WBondPlacement.ApplyDesign`, exactly as
`IncludeCapacitance` does; that one moment of inheritance is the whole connection between the wBond
editor's setting and a placed component's.

**It is the ONE wBond parameter matched case-insensitively** (`ComponentModelFactory.TryGetIgnoringCase`).
`Er`, `ER` and `er` are the same symbol to a reader, and this is the parameter a hand-authored `.cnl` is
most likely to spell differently. A silently ignored permittivity is a wrong capacitance with nothing
anywhere saying so.

**It stays an expression, so it stays sweepable.** It is deliberately *not* in
`Elaborator.ResolveWBondParameters`' name-valued list, and the panel gives it a **TextBox** rather than
the stepper the export dialog has: `er = moldEr` is typable, and characterising a package against
measured data is exactly the thing a sweep or an optimiser is for. Clearing the box commits `"1"`, not
empty — `er` is always emitted, so an empty expression would reach the evaluator as a parse error at
Run rather than meaning air.

**The Touchstone option is `double?`, and the nullability is load-bearing.** `Options.OvermoldEr = null`
means "the design's own". Had it defaulted to `1.0`, every Options built elsewhere — the Compare
view-model, a test, a future caller — would have silently stripped an encapsulated design back to air.
The override is applied once, at `BuildNetwork`, through `WBondDesign.WithOvermoldEr`, which returns a
**shallow** view sharing the wires: an export is a read, and writing ε_r back onto the live design would
mean the schematic quietly simulating whatever the last export dialog was set to. The written file
states the medium **unconditionally, air included** — "the medium is not stated" and "the medium is air"
are different claims to someone reading a `.sNp` a year later, and nothing else in the file distinguishes
two exports of one design at two permittivities.

**`MergeInto`'s "nothing changed" shortcut had to learn about it.** Update Layout from Schematic decides
whether to write by comparing wire geometry (`CountChanged`), and ε_r moves no wire — so a changed
permittivity was applied to the in-memory design and then never written. Counted explicitly
(`remolded`). The reverse direction needed nothing: Update Schematic from Layout goes through
`ApplyDesign`, so both halves cannot drift apart.

### The export dialog's own follow-ups (owner, same day)

Three of these are one measurement each and are recorded because the reasoning is not visible from the
markup. **A `NumericUpDown`'s column width is not its text width** — the spinner buttons and the
control's own padding take most of it, so the 90 px columns left the segment count showing about a
character and a half and the permittivity about one. Sized for the value instead: 110 for a count that
reaches 200, 120 for `3.4`. **The number format now defaults to MA**, not RI. And the label there is
`εr` alone rather than a word, because it sits in a column of one-word labels and the symbol is what
the file's own header calls it.

**The port list is a `ListBox`, and it has to be.** Port count is 2 × the array count on the terminal
basis, so a 25-array design is 50 rows. An `ItemsControl` inside a plain `ScrollViewer` would realise
every one of them — the ScrollViewer hands it infinite height, so no row is ever out of view to skip.
Only `ListBox`'s own template puts a `VirtualizingStackPanel` inside its own `ScrollViewer`. Capped at
six rows: enough to read a two-array design whole, short enough that a large one scrolls instead of
pushing the Export button off the dialog. The selection and hover visuals are styled out, scoped to
that one control, so it still reads as the flat list it replaced.

## Cancel from the progress bar (owner, 2026-08-19)

**"Cancel" was three different things, so it now goes through one.** `RunCancellation`
(`src/Ui/Messages/`) is the handle a long operation hands to every surface that offers to stop it: it
answers *can this still be cancelled*, *has somebody already asked*, and *what is being stopped*, and
raises `StateChanged` when any of those change. A `CancellationTokenSource` answers none of them, which
is why a Cancel button and a bar's context menu could not previously read one truth. `MessageEntry`
binds it (`CanCancelRun`/`CancelTooltip`); every long run's `finally` calls `Finish()`.

**Two rows of one run share ONE handle.** An EM run and a wirebond run each post a sweep row and a
stage row; they are two views of one computation, so both bars bind the same instance and either one
stops all of it. The handle is idempotent, so right-clicking both in turn is one request. Same reason
the EM panel's Cancel button, the `Simulate ▸ Stop` menu item and the toolbar Stop all route through
it rather than each calling `cts.Cancel()`.

**The Touchstone export was passing `CancellationToken.None`.** The 3-D wire MoM kernel had been
checking a token at every work boundary since it was written — nothing was ever handing it one that
could be cancelled, and the export has no window of its own once the options dialog closes, so it
could not be stopped from the UI at all. A source-scan test (`ProgressBarCancellationTests`) now holds
that shut, comments stripped first.

**A `ContextMenu` on the Messages row's progress bar does not work, and the reason is structural**
(owner report, same day: *"the Copy All Messages context menu interferes with the progress bar cancel
context menu"*). The bar is an `InlineUIContainer` **inside** the row's `SelectableTextBlock`, which
already owns the Copy menu — two menus on one right-click, and which opens depends on which control
the hit test resolved to. The row's text and its bar are one target and get **one** menu, with Cancel
at the top, `IsVisible`-bound to `HasProgress` so an ordinary message's menu is unchanged. The
standalone bars (the Compare dialog's, harmonicaRF's) have no such parent and keep their own.

**A pending stop is a STATE, not an instant** — cancellation lands at a work boundary, so a full-wave
point can run for tens of seconds after the click. Every surface says so and goes inert meanwhile
(`EmSetupEditorViewModel.IsCancelling`/`CancelButtonText`, `WBondMomCompareViewModel.RunButtonText`,
`CanStopAnalysis`), and the Compare dialog's `ShowProgress` drops observations while cancelling rather
than overwriting "Stopping…" with the name of the stage still running.

**harmonicaRF's grid bar settles its own state.** `SolvePool.CancelCurrent` cancels the in-flight job,
which raises neither `Completed` nor `Failed` — that silence is what keeps latest-wins cheap on a drag
— so `HarmonicaViewModel.CancelSolve` clears `IsSolving`/`IsSolvingGrid` itself and posts a
`CancelNotice` the next published frame clears. The frame on screen is kept: the previous answer is
still the last true one.

**Still uncancellable, deliberately: a Touchstone export from the standalone `wBond` binary.** That
shell has no Messages panel and no bar — `WBondStatusMessageSink` is one line of text, and its own
docs give the reason ("inventing a glyph for a percentage would be a second progress widget to keep in
step with the panel's real one"). Inside circuitRF the same export is cancellable from either row.

## Multi-tone HB (3–6 tones) in the UI: the Data Display needed nothing (2026-08-19)

**The headline, because it is the part that could have gone badly.** Extending HB to 3–6 tones
changed **zero files under `src/Ui/DataDisplay/`**. Its `mixIndex` handling is already tag-agnostic:
it decides "this is a spectrum" from the axis NAME, positions stems from the axis VALUES (signed
product frequencies in Hz), and prints the axis LABEL verbatim (`Trace.cs` ~2005/2060,
`PlotInspectorViewModel.ApplyPinnedSpectral`). So the engine's T-tone path emits the identical cube
shape and only widens the tag from `"(k1,k2)"` to `"(k1,…,kT)"`, and a three-tone spectrum renders,
slices and reads out through the frozen two-tone path unchanged. Measured, not assumed — the marker
box for the two cases is the same rows in the same order:

```
3-tone: m1 | mixIndex=(1,-1,0) | freq=0.01 GHz | V=0.012
2-tone: m1 | mixIndex=(1,-1)   | freq=0.01 GHz | V=0.012
```

`SliceTokenParser.SplitTokens` already ignores commas inside a quoted label, so `V["n_drain",
"(1,1,-1)"]` works for the same reason the two-tuple did. `HbMultiToneSpectrumTests` pins BOTH sides
— every three-tone assertion has a two-tone twin — so a future change cannot fix one by breaking the
other.

**A freshly-placed `PnTone` carries only two tone groups, so adopting a 3+ tone analysis has to
CREATE parameters, not just assign them.** `AdoptHbTonesIntoPnTone` previously looped `i = 1..2` and
skipped any `Freq[i]` that did not exist. Against a three-tone analysis that is silent and wrong in
the worst way: the source keeps driving two tones while the analysis declares three, everything
elaborates, and the only symptom is a commensurability error at Run that names the source rather than
the mismatch. It now adds the whole `Freq[i]`/`Pavl[i]`/`Phase[i]` group — a tone with a frequency
and no `Pavl` drives nothing — seeding the new rows' drive from tone 1.

**`ToneCoeff`/`Tone2Coeff` were kept as named accessors onto rows 1 and 2 of the new tone list, and
that was not cosmetic.** `Tones` is canonical, but the dialog's existing suite, `LpBodyViewModel`,
`LppBodyViewModel` and the multi-tone `ToneExpr` mirror all address the first two tones by those
names — several as object-initializer *setters*. Forwarding properties (with change notification in
both directions, so an edit through `Tones[0]` surfaces on `ToneCoeff` and vice versa) kept all 60
existing dialog tests green with no edits.

**The tone-list accessors must survive being read while the list is EMPTY, and no ordinary test
finds that.** Rebuilding the list (`SetTones`) clears the `ObservableCollection` before refilling
it, and `Clear()` raises `CollectionChanged` → `PropertyChanged` for `ToneCoeff`/`ToneUnit`/
`TonePreview` — which a bound control answers by READING them, mid-clear, with no row 0 to read.
Headless tests all passed because nothing in them subscribes. The reachable user path is the
ordinary one: dialog open (bindings live), user clicks **Multi** with a `PnTone` on the schematic,
so the list is replaced by the source's tones → `ArgumentOutOfRangeException`. The getters are now
empty-safe. `HbToneListTests.ReadingToneAccessors_FromInsideAChangeNotification_NeverThrows`
subscribes BEFORE the click and touches every accessor from the handler — verified to throw against
the unguarded getters, so it is a real gate and not a vacuous one. **A first attempt at that test
passed against the buggy code**, because it subscribed only after the rebuild had already happened;
a notification-ordering test is worthless unless the subscription predates the mutation.

**`FromAnalysis` must seed a tone row by CONSTRUCTION, not by assigning `Unit` then `Coeff`.**
`HbToneRowViewModel` rescales the coefficient on a unit change so the Hz value is preserved — the
right behaviour for a user picking a different unit, and exactly wrong for restoring a stored pair,
where it would silently multiply the coefficient. The old code sidestepped this with a `_prevToneUnit`
field primed before the assignment; the row VM now takes both in its constructor instead, which
removes the trap rather than documenting it.

**The dialog shows the retained-product count live beside `Max mix order`.** The engine caps
multi-tone analyses at 600 retained products and the count grows steeply with tone count — 6 tones at
the *default* `MaxMixOrder=5` asks for 1,827 and is refused. Putting `"6 tones, order 3 → 189 mixing
products (limit 600)"` next to the knob that sets it means the refusal is visible while authoring
instead of arriving at Run. It reports the over-cap case explicitly (`— OVER the 600 limit`) rather
than just showing a large number, and stays blank when the order is an expression rather than a
literal, since only the engine can resolve that.

## User-Docs Factory (DF1–DF6) — vector UI capture and one-command doc generation (2026-08-20)

`docs/sonnet-briefs/brief-docs-factory-infrastructure.md`. `tools/DocGen` now regenerates every
user-doc figure and every generated page from the live application:

```
dotnet run --project tools/DocGen -- --out docs/user           # figures + toolbars + fonts + HTML
dotnet run --project tools/DocGen -- --slides docs/slides      # landscape PDF decks
```

The brief's four enabling facts held exactly as written and are not restated here. What follows is
what the build found that the brief and the design note did not.

### The Skia black-alpha trap is wider than the brief's one case, in three specific ways

The brief names Fluent's light-theme `ButtonBackground` (`#33000000`). All three of the following were
found by running the lint over real captures, and each would have shipped a visibly wrong figure:

1. **`Brushes.Transparent` is pure black too** (`#00000000`). It loses its paint the same way, so an
   *invisible* border or background serialises as an **opaque black slab**. The generator's own
   `Window.Background` was `Brushes.Transparent`, which painted a full-canvas black rectangle over
   every dark-variant figure. The remap re-points a transparent brush to `#00010101` — still
   invisible, but Skia now writes `fill="#010101" fill-opacity="0"`.
2. **Opaque black had to be remapped as well, even though dropping its `fill` renders correctly.** A
   light theme's icon foreground is opaque black, so a Material icon serialises with no paint at all —
   byte-for-byte what a dropped paint looks like. Leaving it alone left the lint unable to tell a
   correct icon from a black slab, and a lint with dozens of benign findings is a lint nobody reads.
   Cost: about twenty bytes per glyph run. Benefit: every surviving finding is real.
3. **A theme resource can be a `Color`, not a `Brush`, and a `Color` assigned to a brush property is
   converted on the way in** — so it reaches Skia as a pure-black paint just the same, and a *brush*
   override does not type-match it and is silently ignored. This is not hypothetical:
   `CircuitRfStyles.axaml` softens toolbar icons with
   `Foreground="{DynamicResource SystemBaseMediumColor}"`, which is `#99000000`, and **every** toolbar
   icon in the Schematic and Data Display editors serialised with no paint until `DocsPaintRemap`
   grew a `case Color`.

**Strokes are NOT affected.** Measured directly: a `#99000000` pen emits
`stroke="black" stroke-opacity="0.6"`, correctly. Only fills lose their paint — which is why
`SvgLint` treats *a shape with neither attribute*, rather than "any black", as the defect.

**The remap is discovered, not listed.** `DocsPaintRemap.Build` walks the live application's style
tree for keys and resolves each **per theme variant through the ordinary resource lookup**. That
second half is load-bearing: the Fluent theme dictionaries store **deferred items**, so enumerating
their raw values finds nothing (a first attempt reported zero pure-black brushes while
`TryGetResource("ButtonBackground", Light)` returned `#33000000`). The current run remaps **842**
brushes and colours.

### The lint's own false positives, and where they came from

Two exclusions are not tidiness — without either, the lint is unusable:

- **Anything inside `<clipPath>`/`<mask>`/`<defs>` is geometry, not ink.** Avalonia emits one clip per
  control; a 320×200 four-control probe panel produced **thirteen** clip rectangles, every one of them
  a paintless `<rect>`.
- **The post-pass's own `<defs>` entries.** Path deduplication hoists repeated `d=` into `<defs>` with
  the paint left on the `<use>` elements, so the definitions are paintless by construction.

### Size reduction: 2.12×, not the 3–5× the brief expected — reported rather than rounded up

Measured over a full run: **4,660,338 bytes before, 2,198,968 after (2.12×)**, from 908 no-op clips
dropped and 1,100 repeated paths hoisted, plus 2-dp coordinate rounding. The shortfall is structural
and not worth chasing: what dominates a captured window is **Skia's quadratic approximation of every
rounded-rect corner** — roughly thirty `Q` segments per corner — and those are real geometry. Only the
full-canvas clips can be proven redundant; the per-control text clips cannot be, cheaply.

Whole-run numbers, for the record: **118 files, 3,673,125 bytes emitted (897,425 of it fonts),
3.1 s wall clock.** Well inside the brief's 60 s threshold, so no complaint is due.

### `circuitRF_demo/` is git-ignored, so fixtures cannot be built from it

Both the brief (§3.2) and the design note (§5.2) say to build fixtures from the shipped example
workspaces. **`circuitRF_demo/` is in `.gitignore`** — it is not in the repository, so a fresh clone
and CI do not have it, and a fixture reading from it would work only on the author's own machine. The
tracked equivalent that satisfies the same requirement is
**`src/Ui/resources/schematic-templates/*.csch`**: four real, authored, version-controlled schematics,
embedded in the assembly and read through the very `SchematicPersistence` a user's own file goes
through. `DocFixtures` uses `FET_S-Parameters` from that set.

### Popups: the two hosting modes produce the same figure, and one of them draws the window twice

An open popup is either given its own top level or hosted in the parent's overlay layer, depending on
the platform. Overlay-hosted, it is **already inside the window's visual tree**, so compositing it
again draws the entire window a second time — measured: the context-menu figure came out at 178 KB
against the same window's 93 KB, one window stacked on the other. `PopupCapture` therefore carries the
popup's own **content** (what "did it actually render?" is asked of — asking it of the *root* would be
trivially true in the overlay case and prove nothing) and a **`SeparateRoot` that is null unless the
popup really went to its own top level**.

Two smaller traps in the same area: `ContextMenu.Open` refuses any control other than the one the menu
is attached to, and a menu opened at the control's origin sits on top of the synthetic title bar and
reads as a rendering fault — `HorizontalOffset`/`VerticalOffset` place it over the canvas instead.

### `DrawingContextHelper.RenderAsync` ignores the canvas transform in force when it is called

It installs the visual's own. Measured twice, both times as a silently wrong picture rather than an
error: a composited popup landed at the origin instead of its offset, and a slide figure drew at full
size across the whole page instead of scaled into its box. **Record to an `SKPicture` and draw *that*
with a matrix** — `UiArtworkGenerator.Record` exists for this and is the only way to place or scale a
captured visual.

### The toolbars needed a name in the XAML, and the manifest needed two filters

Each of the five editor toolbars now carries `x:Name="DocsToolbar"`. Finding it by shape ("the first
docked panel with three or more buttons") would silently pick a different panel after a refactor and
produce a confident, wrong figure. Two things the traversal had to learn:

- **Skip collapsed children.** A collapsed control arranges to a zero rectangle *at the panel's
  origin*, so ten state-dependent items on the Layout toolbar stacked their callout numbers on top of
  button 1 and put ten numbers in the table with nothing under them in the figure.
- **Do not number separators.** They are not buttons, and numbering the gaps makes the prose count
  wrong the first time somebody reads it out.

The indexed variant positions each callout from its item's **arranged bounds in both axes**, not in a
strip underneath: the Layout and wBond toolbars are WrapPanels and genuinely reflow onto a second row,
where a strip put row two's numbers on top of row one's, out of order, and looked deliberate.

**Every toolbar button in all five editors has a tooltip** (26 + 38 + 41 + 21 + 32 items). The brief
asked for any missing one to be reported as a UI bug; there are none.

### What the component registry can and cannot generate

`ComponentTypeRegistry` knows a parameter's **name, default, unit and on-schematic visibility** — the
facts that drift — but **not what it is for**: `ParameterDescription` covers `VerilogA` and nothing
else. So `{{table: components/<Kind>}}` emits four columns and adds a *Meaning* column only where the
registry has meanings to put in it. The words stay in the Markdown beside the table, because the
alternative is prose in a C# string literal, which the brief forbids for good reason.

### Symbol figures were missing their pins entirely, and the fix is a shared call

`DrawSymbol` renders a primitive list only; on a real schematic the pin markers come from the render
loop's `DrawPortMarkers` and the SDD/ZPort stubs from `DrawVariadicPortLeads`. Both are now reachable
from a pin list alone (`SchematicRenderer.DrawUnconnectedPortMarker`,
`DrawVariadicPortLeads(kind, pins, …)`) and the generator calls them rather than carrying a second
copy of the geometry — so a documentation figure and the canvas cannot disagree. The bbox fit also had
to grow by `PortMarkerWorldHalf`, or an outlying pin's marker is drawn outside the glyph box and
clipped.

Fifteen components gained a figure (`Diode Match Mlin MBend MTee MCross Mtaper Mklopf VerilogA WBond`
and the five FET laws), and `wbond` is the one whose symbol is **generated from the shipped default
design** rather than being a built-in. `fet.svg`/`fet-dark.svg` were hand-made, produced by nothing,
and are deleted; a test now fails if either returns or if a page references it.

### Doc size is app size, so three things are excluded from the bundle

`CircuitRF.Ui.csproj` copies `docs/user/**` into the application output. Newly excluded, with the
reason in the csproj: the Markdown sources under `docs/user/src/`, and **`assets/figures/**`** — every
page *inlines* its figure as an `<svg>` element (an SVG referenced with `<img>` cannot see the page's
`@font-face` rules), so the standalone captures are build intermediates and shipping them would ship
every figure twice, for about 2 MB.

Fonts are extracted **only for the families the emitted pages actually reference**, plus the body font,
and stale faces are deleted. Shipping all eleven faces unconditionally costs 4.25 MB — most of it
DejaVu — for faces no page may cite; the current set is 897 KB.

### `reference/components.html` is now generated, and that was forced by the anchor contract

The design note's migration plan names it as the right first page to port, and the anchor test made it
the necessary one: `DocLauncher` can deep-link to `components.html#<symbolkind-lowercase>` for every
placeable kind, and the hand-written page was missing **sixteen** of those anchors. Its prose is
preserved; the figures and the parameter tables are placeholders now. **The new sections' prose is
deliberately minimal** — writing the words for those sixteen components is
`brief-user-docs-content.md`'s job, and this brief stopped at making the anchors exist and the
pipeline work. `simulations.html` and `plot-types.html` already satisfy their own anchors and were
left alone; migration stays incremental and un-ported pages are copied through untouched.

### Every symbol caption shipped in the wrong weight, and the font gate had a hole that let it

Reported by the owner as "the symbol generator is not using the correct font". It was not the
generator's choice of typeface — it was what Skia writes into the SVG and what a browser does with it.

For a face loaded from its own file, Skia's SVG device writes the font's **full name first, its family
second, and a weight that matches neither**:

```
font-family="IBM Plex Sans SemiBold, IBM Plex Sans"  font-weight="500"      (the face is weight 600)
```

Neither half survives. `IBM Plex Sans SemiBold` is not a declared `@font-face` family, so the browser
skips it; the fallback `IBM Plex Sans` **is** declared, at 400/600/700 — and CSS font matching for a
requested **500** tries 500, then descends (400) before it ascends. It lands on **Regular**. All
**76 symbol captions** were drawn SemiBold and shipped looking Regular, and 28 runs inside the Data
Display capture did the same.

The mis-weighting is specific to this case, which is why it is not obvious: Skia writes `Light` as 300
correctly, and a weight set as a *property* (the toolbar callout numbers) as 600 correctly. It is the
"distinct SemiBold file" path that emits 500.

`SvgFontNormalizer` now rewrites every `<text>` to the base family and restates the weight from the
full-name suffix, with an explicit nine-name table; **an unrecognised face-name word is a generation
error, not a guess**, so a new face cannot ship silently mis-weighted. Counts after the fix, measured:
366 `IBM Plex Sans` unweighted, 84 at 600 (8 pre-existing + the 76 captions), 268 `Inter` unweighted,
266 at 600, 2 at 300.

**One self-inflicted regression on the way, worth recording because the shape recurs:** the first
version *removed* `font-weight` whenever it had none to restate, silently un-bolding the 238 runs Skia
had already got right. Null must mean "leave alone", never "clear".

**The owner kept the SemiBold caption** (2026-08-20) rather than matching the canvas's `PlexRegular`
type label — a figure caption reads as a heading, not as an annotation floating beside a symbol. Note
that the caption SIZE cannot be matched to the canvas even in principle: the canvas sizes it
`zoom * LabelWorldHeight`, and each figure is fitted to its own box, so across this catalog the fit
ranges 0.189 (wBond) to 1.308 (GND) — which would set GND's caption at 92 px and wBond's at 13 px on
the same page. The fixed 15 pt sits just under the 17.2 px the two-terminal majority would produce.

### Skia bakes a PLATFORM font into a figure when a glyph is uncovered

Found by the same investigation: four text runs came out as **`Lucida Grande`**, and the text was a
single `▾` (U+25BE) in the Layout and wBond status bars. Neither Inter nor IBM Plex Sans covers it, so
Skia fell back to a macOS system font and wrote its name into the file. Two problems at once — the
figure stops being reproducible across machines (a regenerate-and-diff check fails on the wrong OS),
and the reader is pointed at a font the docs do not ship.

The normaliser redirects any unshipped family to **DejaVu Sans**, which circuitRF does ship and which
covers the geometric shapes the interface uses, and **reports every substitution by family and by
codepoint** rather than quietly correcting it. The report is the point: the interface is drawing a
glyph its own typefaces do not have, and that is worth fixing at the source — on macOS today the real
application is silently substituting there too.

### The font gate that let all of this through

`EveryFontFamilyAnInlinedFigureUsesIsShippedWithTheDocs` accepted any name that *started with* a
shipped family, so `IBM Plex Sans SemiBold` counted as covered by the `IBM Plex Sans` `@font-face`.
It is replaced by two exact checks: **no emitted `font-family` may be outside the shipped set**, and
**every (family, weight, style) an inlined figure asks for must be declared in the stylesheet**.
Verified to bite rather than assumed — deleting the `IBM Plex Sans` 600 rule from the CSS fails the
second one immediately, naming `reference/components.html: IBM Plex Sans 600 normal`.

Same round, same shape: `EveryToolbarButtonHasATooltip` began `if (!File.Exists(json)) continue;`, so
it would have passed on a fresh clone with no manifests. A gate that passes because its input is
missing is worse than no gate; it asserts now.

### A slides-only run reported success and wrote nothing

`--slides` with no `--out` has to find the docs root on its own, and the obvious marker — "walk up
until you see a `docs/user` directory" — finds the WRONG one. `tools/DocGen` references
`CircuitRF.Ui`, which copies `docs/user` into its own build output, so the walk stopped at
`tools/DocGen/bin/Debug/net10.0/docs/user`: a bundle copy with no `src/` in it (the `.md` sources are
excluded from the bundle), so no source pages were found, no deck was produced, and the run printed
"Slides regenerated in 0.0 s". The marker is now `circuitRF.slnx`, and a slides run that finds no
sources throws instead of congratulating itself.

Related, and the reason the deck lives at `docs/slides/` and not `docs/user/slides/`: **everything
under `docs/user` is copied into the application bundle**, and a PDF deck is not a runtime asset. It
is git-ignored as a build product, like the app icons — a committed PDF could only ever be reviewed
as an opaque blob.

### Two build-mechanics notes

- **An XML comment cannot contain a double hyphen**, so no `.csproj`, no generated SVG banner and no
  generated HTML comment can spell `--project` or `--out` literally. Every banner writes the flags in
  words and says why. `DocGen.csproj` failed to *load* on the first attempt for exactly this.
- **`Ui.Tests` deliberately touches no Avalonia runtime API**, so `DocsFactoryTests` asserts over the
  generated artefacts in `docs/user/` rather than re-rendering. That is not the weaker test it looks
  like: the generator already fails hard at capture time on an empty figure, a dropped paint, an
  unopened popup, an unknown placeholder and an unresolvable cross-link. What the tests add is that
  nobody can hand-edit or delete the result unnoticed, and that the catalog and the anchor contract
  stay in step with the code. `tools/DocGen/check-docs-current.sh` is the regenerate-and-diff check;
  **this repository has no CI workflow to add it to**, which is why it is a script.

---

## User-Docs Factory — the first read-it-in-a-browser pass (2026-08-20)

The owner opened the generated site in a real browser and reported fourteen defects. **Four of them
were one bug, two more were one bug, and one was a defect in the application rather than in the
docs.** Recorded here because every one of them failed *silently* — a wrong picture, never an error.

### 1. Inlined figures share the page's id namespace (four reports, one cause)

Figures are inlined as `<svg>` rather than referenced with `<img>` (so the page's `@font-face` rules
reach them). Inline SVG shares the HTML document's single id namespace, and **two of our own passes
number ids from zero in every file**: `SvgPostPass.DedupePaths` writes `d0, d1, …`, and Skia numbers
embedded rasters `img_0, img_1`. A second figure's `<use href="#d0">` therefore resolved to the
*first* figure's geometry.

Four reports, all of them this:

| Symptom | What was actually happening |
|---|---|
| "the Data Display toolbar is glitched out, can't see the button numbers" | its `d0` resolved to the plot figure above it |
| "the six snaps SVG is glitched, though the SVG looks fine by itself" | the standalone/in-page difference IS the tell |
| "the Wire Profile panel renders crazy — rulers, no wires" | same |
| "harmonicarf.html uses the LIGHT image in dark mode" | the dark figure's `<use href="#img_0">` found the light figure's raster, because the light span is first in the document |

**Fix:** `SvgPostPass.ScopeIds` prefixes every id with the emitted file's stem and rewrites both
reference spellings (`#id` and `url(#id)`). Gated by
`DocsFactoryTests.NoGeneratedPageHasTwoElementsWithTheSameId`.

### 2. Two decimal places is a two-thirds error on a matrix scale

Every ComboBox in the docs drew **half a chevron** — the left stroke, no right stroke. The Fluent
chevron is a 2010-unit-wide geometry scaled into a 12 px icon box, `matrix(0.00597 …)`, and
`SvgPostPass.RoundAll` rounded that to **`0.01`** — 67 % too big, inside a clip that was still 12 px
wide. Skia's own clip did the rest.

**Fix:** below a magnitude of one, round to four significant figures instead of two decimal places.
The size win is unchanged (small numbers are short either way). Gated by
`SmallMagnitudesKeepTheirPrecision`.

### 3. No `viewBox` means an inline SVG does not scale — it clips

Skia writes `width`/`height` and nothing else. `figure.figure svg { max-width: 100% }` then narrows
the *element box* and the drawing is cut off at full size rather than resized ("the schematic svg is
not sized to the frame"). The symbol figures never showed it only because they are smaller than the
column they sit in. `SvgPostPass.AddViewBox` adds one; `EveryEmittedFigureCarriesAViewBox` gates it.

### 4. Inter drops its own hyphen — through the *contextual alternates* feature

Not reported, found while investigating: **`circuitRF — Data Display` was rendering as
`circuitRF Data Display`**, `C-V Editor - C1` as `CV Editor C1`, and the C-V editor's whole negative
column as unsigned numbers (`-4` → `4`, `6.2E-13` → `6.2E13`). Probe string
`"PROBE A-B c-d E - F g - h"` came out `"PROBE AB c-d E F g - h"`: **the hyphens next to a CAPITAL
were gone, the ones next to a lower-case letter were intact.**

Inter's `calt` feature substitutes a case-height hyphen (and case-height parentheses) beside
uppercase. Those alternate glyphs have **no cmap entry**, so Skia's SVG device cannot map them back
to a character and omits them — leaving the advance width behind, which is why it read as a gap
rather than as missing text. `UiArtworkGenerator.SuppressContextualAlternates` turns `calt` off for
the capture only, via the inherited `TextElement.FontFeatures`.

### 5. A "transparent" window background is an opaque slab

`SKSvgCanvas` writes a zero-alpha fill as an opaque one. Framed figures hid it under their own body
colour; the bare-panel figures showed a **near-black rectangle** behind the panel in dark mode.
Captures are now backed with the docs stylesheet's own `--surface` colour
(`WindowFrame.DocsSurface`), so the unavoidable slab is the colour the figure's frame already is.

### 6. `SchematicCanvas.ZoomToFit` fitted to a hit-test envelope, not to the drawing

**This one is an application defect, not a docs one.** A component's `Bb` is a FIXED square around
its origin (`EditableComponent.GetBoundingBox` — `X ± HalfBound`, the same size for a resistor and
for a twelve-port SnP), and `ZoomToFit` fitted the model box that aggregates them. Any symbol bigger
than that square over-zoomed: a four-array wBond ran a fifth of its own height off the top and bottom
of the view **at every window size**, in View ▸ Zoom to Fit as much as in the capture.
`SchematicCanvas.DrawnExtent` now unions each component's `FullBb` (the value the renderer and the
spatial index already cull against), the wires and the bitmaps.

### 7. Smaller things, each with a reason

- **The SDD and ZPort glyphs had no pin leads.** The body is 180 wide and the pins sit at ±200, so a
  wire attached to one ended 110 units short of the box with nothing between. Every other
  box-with-terminals glyph (VerilogA, SnP) already drew them. `BuiltInSymbols` now does too — which
  is why `PrimitiveCount_MatchesExpected` went from 5 to 9 for both kinds.
- **A figure cannot be Zoom-to-Fitted at construction time.** Fitting is a viewport operation and a
  control that has not been arranged has no viewport, so the request was silently ignored.
  `FigureScene.AfterLayout` runs it once the window has been measured and arranged. The fixtures call
  the canvas directly rather than the document's `RequestZoomToFit` event — through the event it did
  nothing and said nothing about it.
- **The indexed toolbar figure's separators ran past the buttons** into the row of callout numbers,
  because a stretched one-pixel `Border` grows with the taller indexed frame.
  `ToolbarCatalog.WithCallouts` now pins the toolbar to the plain figure's own height.
- **Match's and wBond's `Design` is base64 of the whole design**, not a parameter. The parameter panel
  already refuses to show it; the docs table listing it invited exactly the hand edit the interface
  declines to offer. `DocTables.IsOpaquePayload` drops it.
- **The toolbar table's `Button` and `Icon` columns said the same thing twice** — an `x:Name` and an
  icon's enum name — and neither answered "which one of these is it on the toolbar". Each button is
  now captured on its own (`DocGenRun.ToolbarButtons`) and drawn in the cell, at ~2.6 rem; the Icon
  column is gone. Same reasoning put the real snap glyphs in `layout-editor.md`'s feature table
  (`InlineGlyphArtwork`, drawing through `LayoutRenderer.DrawSnapGlyph` so there is one
  implementation), in the surrounding text's colour rather than a layer colour — there is no layer in
  a table cell.
- **wbond.md was wrong about its own panel:** it claimed the Array Inductance panel reports `R/ωL`.
  It does not, and there is no such quantity in the array reduction. The page now says so, and says
  where to read R instead. It also now credits Grover (2nd ed., Dover) for every closed form in the
  section, and states R(f) and its two asymptotes explicitly.

### What the browser could not have told us, and the tooling that did

Reproducing an id collision needs *two* figures in one document — a standalone file renders
perfectly. The throwaway harness that found all of this inlines several generated SVGs into one HTML
page and rasterises it through `qlmanage` (macOS QuickLook, WebKit). **A per-file check would have
passed on every one of these bugs.** That is why the gate is a page-level duplicate-id test rather
than a per-file one.

## The schematic editor gets its own chapter (2026-08-20)

`docs/user/src/reference/schematic-editor.html` is new: the canvas, the Library Palette, the two
placement gestures, the context menu, the toolbar button by button, and — the part the page exists
for — **that a simulation is called an *Analysis*, is configured through Simulate ▸ Setup
Analyses…, and only then runs**. Three things about it are worth not rediscovering.

**The schematic figure and the schematic toolbar table have MOVED OUT of `grid.md`.** That page had
carried `{{ui: schematic-editor}}` and `{{toolbar: schematic}}` under a `#editor` heading since
before there was an editor chapter. Leaving them there would have put ~180 KB of the same inlined
SVG on two pages and given the schematic toolbar two owners — exactly the drift this pipeline
exists to remove. `grid.md#editor` is now three lines and a link; the anchor is kept because
removing it is free to do later and not free to undo. Nothing deep-links to it (`DocAnchors` does
not name it, and no other page did).

**Two of the three new figures are captured at their dialogs' OWN declared sizes, less the synthetic
title bar** — `SetupAnalysesDialog` is 520×420 so the row states 520×386, and `AnalysisEditorDialog`
is 520 wide and sizes to content up to `MaxHeight="650"` so the row states 520×616. The
consequence is visible and is correct: at 616 the HB body genuinely overflows and the sweep's
Start/Stop/Step row is cut off behind a scrollbar, **because that is what the real dialog does**.
The figure caption and the page both say so, rather than the capture being quietly grown to a size
no reader's build ever opens.

**`library-palette` is captured at 280 px wide for a reason that is not aesthetic.** The tile grid
is a `WrapPanel` of fixed-width tiles, so its column count is a pure function of panel width, and
the default dock layout gives the left column 20 % of the window (~280 px). Captured wider, the
figure would show a number of columns per row that no default install has.

Two fixtures were added (`DocSchematicFixtures`), both on the shipped
`FET_Harmonic_Balance_Sweep` template with a `DcAnalysis` inserted ahead of its HB — a real
two-analysis test bench rather than a hand-built view-model. Both lift the **real dialog's own
`Content`** out of the `Window` and re-attach its `DataContext` (a `Window` cannot be hosted inside
the capture window, and detaching takes the inherited DataContext with it), so the figure is the
dialog a reader opens and not a reconstruction of it.

`_nav.txt` also reordered: **Simulate now reads before Layout & EM**, since a user runs a simulation
long before they lay anything out. `reference/index.md`'s explicit `{{toc: section:…}}` list is a
second copy of that order and was moved to match — it does not follow `_nav.txt` automatically.

## Match Designer round 4 (2026-08-20)

The owner's round-4 list. Four separate reports about the response plots turned out to be **one
missing thing**, and two of the schematic items exposed problems that had nothing to do with what was
asked.

### The response plots had no host, and that was four bugs

Reported: no marker info box ever appeared; the plot's own **Copy** did nothing; axis, grid, tick and
marker colours were wrong; and the pane's background did not match the Data Display's. They are one
cause. A `PlotControl` is not self-sufficient — it asks its host for the next marker index, for a
marker's info-box view-model, for the selected markers, and for the `PlotContainerViewModel` to
export. The Match Designer hosted two bare `PlotControl`s in an `AspectRatioPanel`, so every one of
those providers was null, and **a null host answers all of them with silence rather than an error**:
`PlotExporter.CopyPlotToClipboardAsync` returns immediately on a null container, and nothing creates
an info box on `MarkerAdded` because that path runs through the container.

The fix is that the window now owns a real `DataDisplayViewModel` with exactly two containers in it
(`MatchDesignerViewModel.PlotHost`), laid out by the window instead of by a canvas. The containers'
logical rectangles are kept equal to the `PlotControl`s' real ones on every layout pass, because that
is the coordinate space an info box is positioned and dragged in.

**Two consequences worth knowing.** First, `DataDisplayViewModel` subscribes to the process-wide
`AppSettingsViewModel.Instance` and — until this round — never unsubscribed; harmless for a Data
Display window (one per session), an unbounded leak for a Designer (one per component, per open). It
is `IDisposable` now; existing call sites simply do not call it, which is the behaviour they already
had. Second, an edit rebuilds both plots' trace lists from scratch, so the host has to be told
(`container.OnPlotChanged`) or its info boxes go on pointing at `Trace` objects no longer in any plot.

`PlotControl` grew `CanDeletePlot` / `CanEditPlotProperties`, both defaulting true and both **re-read
on every menu open** — the context menu is cached for the control's lifetime, so a flag consulted at
build time would ignore a host that set it later.

### A fixed display unit exposed a formatter bug that Auto had been hiding

The owner asked for pH and pF as the default display units. `MatchValueFormat.Format` rendered with
`"G{digits}"`, and .NET's `G` switches to exponential the moment the decimal exponent reaches the
precision — so a perfectly ordinary 1.23 nH inductor displayed in pH at three significant digits read
**`"1.23E+03 pH"`**. Auto had masked this for the life of the feature by always choosing a unit that
puts the mantissa in [1, 1000), which is exactly the freedom a fixed unit gives up. `Format` now
rounds to the digit count and renders fixed-point, trimming the padding zeros, and falls back to `G`
only outside 10⁻⁶ … 10¹⁴ where plain notation would be a screenful of zeros.

### "Does this label overlap that component" is a measurement, not a character count

A shunt arm's labels sit beside its symbol; when they are wider than the gap to the next column they
now go under the arm's own ground instead (`MatchShuntLabels`, shared by the pane and by
`MatchFlatten` so the two drawings agree). The first cut estimated width at a per-character rate. That
is wrong by ~20 % **in the direction that decides the outcome**: an ordinary `"C = 0.435 pF"` counts
as 12 characters (480 estimated) and measures 407, because the spaces around the `=` are narrow — the
difference between the fallback firing on a normal design and firing only on a long name. It measures
with `SkiaFonts.PlexRegular` at the renderer's own label size now; the per-character rate survives
only as a fallback for a headless host with no bundled font, and is calibrated against the real
advances.

Note that the flattened cell writes its values at 15 significant digits, so its value row is
genuinely wider than the pane's and its shunt labels legitimately land under the ground where the
pane's do not. Each drawing measures the rows *it* draws; that is the intended asymmetry, not drift.

### A leak test that counts handlers on a singleton measures the suite, not the fix

The first version of the disposal test tallied `AppSettingsViewModel.Instance`'s invocation-list
length before and after. It passed alone and failed under a `~Match` filtered run: every other test in
the file builds a Designer, and therefore a display, in parallel. It asks whether **this host's own**
handler is still in the list instead.

### Schematic geometry

Terminations are upright (`R0`) with their ground bars down and their pin on the spine; shunt
elements moved up a lead-length so their upper pin is on the spine too. Between those and the ground
components each arm already carried, **the drawing now contains no vertical wire at all** — every
endpoint that used to need one coincides with the thing it connects to. A right-click on the pane no
longer pans it (the canvas captured the pointer on any button, so a right-click slid the drawing
under the context menu it was aiming at).

`Copy` on the pane and on the value listing goes through `SchematicClipboard.CopyAsync` — the
schematic editor's own writer, so JSON, SVG, PDF, PNG and Windows CF_ENHMETAFILE come for free.
`MatchSchematicCopy` supplies only the thing that call cannot: a selection, which this pane does not
have because it is a projection of a ladder rather than an `EditableSchematic`. It names the two
terminations `T1`/`T2` rather than the pane's captions, because an instance name with a space in it
has to survive a netlist reader.

## Match Designer speed — the two expensive answers move to a worker, and the two edits the owner named become cache hits (2026-08-20)

Owner: *"Match Designer appears slow to user when updated parameters. Move the calculations off the
UI thread and also look for speed optimizations in the calculations themselves,"* then, mid-work:
*"I found it the slowest when I change network order or filter response type. (I believe the step
that involves solving the low pass prototype.)"*

**Both edits are the same edit.** `Order` and `Response` are the two setters that reach
`Refresh(specChanged: true)`, and that is where the lowpass-prototype search runs. Measured on the
design doc's order-4 interstage problem, one specification edit cost **1,161 ms** with Chebyshev
selected and over two seconds with Butterworth. The numeric work itself was cut ~6x first — see
`src/Core/Match/RESOLVED.md` — and what follows is the UI half.

### Where the 1,161 ms actually was, before guessing

| step | cost | share |
|---|---|---|
| `RefreshResponseOptions` — four probe syntheses, for enablement and tooltips | **1,143 ms** | **98.5 %** |
| `MatchRebuild.Rebuild` + rows + ladder + grid + status + flatten availability | ~0.4 ms | |
| `UpdatePlots` — elaborate and run `SParameterEngine` over 401 points | ~1.0 ms | |
| `RefreshSolutions` | ~2.6 ms | |

**The response plots are not the problem and never were**, which is worth recording because they look
like the expensive thing on the screen. Four prototype searches per keystroke, run to decide which
entries of a ComboBox are greyed out, were.

### Only those two go to the worker

`MatchDesignerViewModel.Analysis.cs` holds them. Everything else — the rebuild, the transform rows,
the ladder, the grid, the status strip, the response plots — totals about 1.5 ms and stays
synchronous, so it still lands on the same frame as the keystroke.

- **Superseded, not queued.** Working the order spinner produces one request per step and every
  intermediate answer is dead on arrival. Each request bumps a generation, cancels the one in flight,
  and a result that comes back stale is dropped rather than applied. **This is the one thing about
  the move that could be silently wrong** — a slow early pass completing after a fast later one would
  leave the panel describing a design the user has moved off, with nothing anywhere saying so — so
  `MatchDesignerSpecEditCostTests` runs a burst of edits with no waiting and checks the settled state
  against a hand-run of the same probes.
- **Nothing reads the live design.** The worker gets `MatchDesign.Clone()`. The badges —
  "current", "previously applied" — are then decided when the result is *applied*, from the design as
  it stands then.
- **Enablement goes stale for a moment, and deliberately does not blank.** A family picked inside
  that window is accepted and then refused by the rebuild instead: the status strip carries the
  refusal with its numbers and the option disables itself when the pass lands. Blanking the
  enablement while a pass runs would grey out every family for the duration of a search whose whole
  purpose is to say which ones are fine.

### The rebuild cannot be deferred, so it is done early instead

`Rebuild` is the one synthesis that genuinely blocks: it is what produces the ladder, the grid and
the plot being looked at. Cold on the numerical route it is ~120 ms at order 6.

**A response change is already covered for free** — the feasibility pass probes all four families at
the current order, so changing the response asks for exactly one of the designs it just synthesised
and `MatchSynthesis`'s memo answers in microseconds. The **order** axis was the one still cold, so the
background pass now also synthesises the current response at every *other* order the picker offers
and throws the results away; the memo is the entire product. `MatchOrders.ValidOrders` is the short
list the picker actually offers — a like or mixed termination pair fixes the parity, so it is two or
three entries, never a range — which is what makes speculating here bounded rather than a guess.

### Measured, on the same problem

| edit | UI thread before | UI thread after |
|---|---|---|
| order change, Chebyshev | 1,161 ms | **4–9 ms** |
| response change | 1,161 ms | **0–7 ms** |
| order change, Butterworth selected | ~2,200 ms | **7–21 ms** |
| slider drag step, Butterworth selected | 110 ms | **1.3 ms** |

### The test seam

`AnalysisTask` / `WaitForAnalysis()` are public because a test asserting on `ResponseOptions` or
`Solutions` right after an edit is otherwise asserting on whatever the previous pass left there — six
existing tests were doing exactly that and went red the moment the work moved. Nothing in the
application awaits it; the window binds to the collections and to `IsAnalysing`, which replaces the
solutions summary in the footer while a pass runs (the two share a slot because the summary *is* what
that pass produces).

**The result scheduler is captured at construction**, from `SynchronizationContext.Current` — the
Avalonia dispatcher in the application, and nothing under xUnit, whose `AsyncTestSyncContext` posts to
the pool rather than requiring a pump. That is what lets `WaitForAnalysis()` block without
deadlocking; it was verified with a throwaway test rather than assumed.

---

## New workspace window opens low and cut off on Windows (2026-08-21)

**Reported:** "on Windows, when a new workspace is created, its window appears lower on the screen
than macOS. On macOS, the placement is perfect. On Windows, the lower portion of the window is cutoff
the screen." Follow-up: "could be different resolutions. Want new window to be in the center."

**Two independent causes.** Fixing only the visible one leaves the window unusable on a small display.

1. **Nothing asked for the window to be placed.** `WorkspaceWindow.axaml` declared no
   `WindowStartupLocation`, so it took Avalonia's default — `Manual` with no `Position` — which hands
   placement to the OS. macOS cascades within the visible frame and will not push a window past the
   bottom of the screen; Win32's `CW_USEDEFAULT` cascades down-and-right from the top-left and does
   not care whether the result fits, stepping further down for each window. Same code, different
   placement. It now declares `WindowStartupLocation="CenterScreen"`.

2. **1200x800 DIPs is bigger than a common Windows working area, and centring does not fix that** —
   it splits the overflow between top and bottom instead of dumping it all at the bottom. A 1920x1080
   display at 150% scaling is **1280x693 DIPs** of working area once the taskbar is gone, so the
   declared 800-DIP height overflows by ~110 DIPs wherever it is placed. macOS never showed it because
   its working area is reported in points and is far taller in DIPs. `WorkspaceWindow`'s constructor
   now shrinks the declared size to fit (`WorkspaceWindowPlacement.Fit`, less a 48-DIP edge margin),
   never below `MinWidth`/`MinHeight`, and leaves it alone when the platform can name no screen.

**`Screen.Scaling`, never the window's `RenderScaling` — and it is measured, not assumed.** A screen's
`WorkingArea` is in physical pixels on Windows and in points on macOS, so converting a DIP size with
`RenderScaling` (2 on Retina) doubles it against an area that was never scaled — the bug that pinned
the Match Designer to the screen corner (`MatchWindowPlacement`), and here it would have halved the
window on every Retina Mac. A throwaway Avalonia 12.0.3 probe on the owner's own machine settled what
macOS actually reports:

```
Bounds=0,0,1920,1080  WorkingArea=0,30,1920,996  Screen.Scaling=1  (window RenderScaling=2)
CenterScreen on a 1200x800 window -> Position=360,128     # (1920-1200)/2 = 360
```

So `Screen.Scaling` is **1** on macOS even at `RenderScaling` 2, and it is the factor that maps DIPs
into that screen's own units on both platforms. It is also the factor Avalonia's own `CenterScreen`
uses to convert `ClientSize`, so the fit computed here and the centring Avalonia then performs agree
by construction.

**The fit runs in the constructor, not `OnOpened`.** `CenterScreen` is applied by `Show()` off the size
the window has by then; resizing afterwards centres the *old* size and leaves the window visibly
off-centre, and jumping.

Gate: `tests/Ui.Tests/WorkspaceWindowPlacementTests.cs` — the arithmetic against synthetic screens
(including the owner's measured macOS display, so the Windows fix is proven not to move the case that
was already right), plus the three wiring facts (the window asks to be centred; the fit runs before
`Show`; it reads `Screen.Scaling`).

---

## Doc figures' text unreadable in Firefox — a trailing comma (2026-08-21)

**Reported:** "the user docs SVG text does not render correctly in Linux Ubuntu (tested using default
Firefox). Seems like bad fonts. The text rendering is either missing or else really small." Follow-up:
"it currently renders perfectly on macOS and Windows."

**It is not a font problem, and it is not a Linux problem.** Both readings are wrong in a way that
would have sent the fix to the wrong place:

- **Not fonts.** Reproduced in a Debian/Firefox 140 ESR container with no Inter or IBM Plex installed:
  on the same page, at the same moment, a `<p>` in Inter and a plain `<svg><text font-family="Inter">`
  both render perfectly while the figure's text does not. The `@font-face` faces load
  (`document.fonts.status: loaded`).
- **Not Linux.** Gecko is strict where Blink and WebKit are lenient. macOS defaults to Safari
  (WebKit) and Windows to Edge (Blink); Ubuntu defaults to Firefox. **The same figure is broken in
  Firefox on macOS and Windows too** — the platform correlation is a browser-default correlation.

**Cause: Skia writes an invalid list.** The SVG device emits a per-glyph position list with a
separator after the *last* entry:

```xml
<text ... font-size="12.5" font-family="Inter" x="0, 8.11, 15.52, …, 87.37, " y="12.11, ">Setup Analyses</text>
```

An SVG `<list-of-coordinates>` may not end in a separator. Gecko applies SVG's strict error handling
and treats the whole attribute as unspecified, so `x` **and** `y` fall back to 0: every run is drawn at
the element's origin instead of on its baseline — one line too high — and the enclosing control's own
clip then removes all but a sliver of each glyph. That is the "missing, or really small": what survives
the clip is a 1-2 px shaving off the top of each letter.

Measured on the first run of `analyses-setup.svg`, in Firefox, both states on the same page:

| | `getBBox().y` | renders |
|---|---|---|
| as shipped (`y="12.11, "`) | **-12.00** | slivers |
| trailing comma stripped in the DOM (`y="12.11"`) | **+0.11** | correctly |

Everything else already measured correct — `getComputedTextLength` 105.83, `numberOfChars` 14, computed
font-family `Inter`, computed font-size `12.5px`. Only the *painting* was wrong, which is why "bad
fonts" was the natural first read.

**Fix:** `SvgFontNormalizer.TrimCoordinateLists`, applied to every `<text>` in `SvgPostPass.Run` — so
figures, symbols and inline glyphs all get it. It strips the trailing separator, and *removes* an
`x=""`/`y=""` outright (an empty list is equally invalid and means what having no attribute means).
`docs/user` regenerated: 370 files, 4,672 runs, and the whole diff is that one attribute pair.

**Two traps inside the fix itself, both caught by a test rather than by review:**

1. The trim runs **before** the `font-family` early-return. That return used to skip any run without a
   `font-family` — and the trailing comma is Skia's, not the font's, so such a run would have kept it.
2. `RemoveAttr` needs an attribute **boundary**. Without a leading `\s`, removing `y` matches the tail
   of `font-famil|y="Inter"` and eats the font off the run — the empty-list test is what found it.

**Noted, not changed:** the 76 symbol figures are referenced with `<img src=…>`, and an SVG loaded as
an image cannot see the page's `@font-face` rules — the docs CSS says so in its own comment. Their
captions are therefore set in whatever the reader happens to have installed, which on a stock Ubuntu is
not IBM Plex Sans. That is cosmetic (the caption is one letter), pre-existing, and independent of this
bug; inlining them like the figures, or converting their text to paths, would be the fix if it matters.

Gates: `SvgPaintAndPostPassTests` (four normaliser cases + one that the repair survives the whole
post-pass, since `RoundNumbers` rewrites `x`/`y` afterwards) and
`DocsFactoryTests.NoShippedFigureCarriesATrailingSeparatorInAPositionList`, which is over the shipped
artefacts because the failure that reaches a reader is a figure regenerated by an older build and
committed — verified to flag the pre-fix file and pass the regenerated one.

### Follow-up, same day: the app's own SVG exports had it too

The documentation generator was only one of eight places in `src/Ui` that write SVG with Skia. The
other five that emit text put files in front of a user — and a file that leaves the application is
worse than a figure in our own docs, because we do not control what opens it:

| seam | what it is |
|---|---|
| `PlotExporter.BuildSvgString` / `WriteSvg` | Data Display's Export SVG — axis labels, titles, contour labels |
| `SchematicClipboard.TryRenderToSvg` | a schematic copied as SVG — refdes, values, net labels |
| `SymbolClipboard.TryRenderToSvg` | a symbol copied as SVG |
| `LayoutClipboard.TryRenderToSvg` | a layout copied as SVG — labels and ports |
| `WBondClipboardWriter.TryRenderToSvg` | a wire-bond assembly copied as SVG |

All five now pass their document through **`SvgFontNormalizer.RepairPositionLists`**, a new public
entry point that fixes the invalid lists and does nothing else.

**Deliberately not `Normalize`.** That one also rewrites family and weight, and it *throws* on a
face-name word it cannot weigh — right for a docs build, where a silently mis-weighted caption must
not ship, and wrong for an export: a copy-to-clipboard must not be able to fail because of a font's
name. Rewriting the family is also less clearly desirable for a file the user opens in a vector
editor, where Skia's full face name is what resolves to the exact face.

`WriteSvg` now builds in memory and writes the result rather than streaming to `SKFileWStream` — the
document has to be complete before it can be repaired, and an exported plot is a few hundred kB.

Gates (`SvgExportPositionListTests`), and the first one is the one that matters:

- **A vacuity guard.** Every other assertion in the file asserts an *absence*; a raw `SKSvgCanvas`
  render is checked to still *contain* the trailing separator, so the day Skia fixes this upstream the
  suite says so instead of quietly passing for the wrong reason.
- The two seams a headless test can actually drive end to end — `PlotExporter.BuildSvgString` and
  `LayoutClipboard.TryRenderToSvg` with a `LabelShape` — asserting on real Skia output, not on the
  repair function.
- **`EverySvgCanvasInTheUiRoutesThroughTheRepair`** — every `SKSvgCanvas.Create` in `src/Ui` must be
  in a file that also calls `RepairPositionLists` or `SvgPostPass.Run`. This is what covers the three
  writers that need a live document to render, and any export seam added later. Comments are stripped
  first: `ContourRenderer` discusses `SKSvgCanvas` in its header without creating one.

## The layout model is read from two threads — a Delete could corrupt the spatial index (2026-08-22)

Owner-reported crash, from a plain Delete of some geometry in the layout editor:

```
System.IndexOutOfRangeException: Index was outside the bounds of the array.
   at System.Collections.Generic.Dictionary`2.TryInsert(TKey key, TValue value, InsertionBehavior behavior)
   at CircuitRF.Ui.Layout.LayoutSpatialIndex.StrPackLeaves(List`1 entries)
   at CircuitRF.Ui.Layout.LayoutSpatialIndex.RebuildFullShapes(IReadOnlyList`1 shapes)
   at CircuitRF.Ui.Layout.LayoutSpatialIndex.Apply(IReadOnlyList`1 shapes, LayoutChangeInfo info)
   at CircuitRF.Ui.Layout.LayoutView.NotifyChanged(LayoutChangeInfo info)
   at CircuitRF.Ui.Commands.Layout.DeleteShapesCommand.Execute()
```

**Read the exception type, not the frame it points at.** `StrPackLeaves` is a plain loop over its own
freshly-built list — it cannot index anything out of range, and `_leafOf[(kind, index)] = node` cannot
either. An `IndexOutOfRangeException` raised *inside* `Dictionary.TryInsert` is a Dictionary being
written by two threads at once: a half-resized instance throws from a bucket index its own array no
longer has. So the question was never "what is wrong with the delete" — it was "who else is writing".

### Who else was writing

**Avalonia runs `LayoutCanvas`'s `ICustomDrawOperation.Render` on the RENDER thread**, not the UI
thread, and that operation calls `LayoutRenderer.Draw` — whose first act is the L2b culling query,
`view.SpatialIndex.QueryIntersecting(view.Shapes, viewportRect)`.

**That query is not read-only.** The index self-heals: `if (!IsBuilt || _syncedCount != shapes.Count)
RebuildFullShapes(shapes)`. It is documented as "always safe to read"; what that sentence never said
is *from one thread*.

A delete is the one edit that arms both sides at the same instant, and its ordering is why:

1. `DeleteShapesCommand.Execute` calls `Shapes.RemoveAt(...)`. The list is now shorter, and the index's
   `_syncedCount` disagrees with it — but nothing has been announced yet.
2. Any frame that starts in that window queries a stale index, takes the self-heal branch, and starts
   a full STR rebuild **on the render thread**.
3. `NotifyChanged` then runs `SpatialIndex.Apply` → `RebuildFullShapes` **on the UI thread**.

Both write `_leafOf`. The window is microseconds wide, which is why this survived so long and why it
finally showed up on a design with a few thousand shapes, where a rebuild takes long enough to overlap.

The same two threads reach a second piece of shared state with a worse failure mode: `LayoutPathCache`
is filled by the render thread (`GetOrBuild`) and invalidated by the UI thread (`LayoutCanvas.
OnModelChanged` → `Apply`), and invalidation **disposes native `SKPath` objects** — potentially the
ones Skia is drawing from at that moment. That is a native crash with no managed stack at all, and it
had never been reported only because the timing is narrower.

### The fix, in three layers

1. **`LayoutSpatialIndex` takes its own lock** (`_gate`) across all four public entry points. The lock
   is in the index rather than at the call sites deliberately: hit-test, marquee, snapping, DRC and the
   renderer all query it, and self-healing makes every one of them a potential writer — a rule that
   every caller must remember to lock is a rule that gets one call site wrong. The combined query now
   resolves every instance bbox **before** taking the lock, because `instanceBboxOf` reaches the cell
   resolver and the file system; nothing unknown runs underneath `_gate`.
2. **`LayoutView.RenderLock`**, held for a whole frame by `LayoutDrawOperation.Render` and for the whole
   of `NotifyChanged` (including raising `Changed`, which is what makes a `LayoutPathCache` eviction wait
   for the frame drawing those paths). The list-mutating commands — `DeleteShapesCommand`,
   `AddShapeCommand`, `ReplaceShapesCommand`, `AddInstanceCommand`, `DeleteInstancesCommand` — and
   `RegeneratePCell`'s whole-list swap take it across mutation *and* notification, so the two are one
   step as far as a frame is concerned. `_onResult` stays outside the lock and **posts** rather than
   invokes: a render thread that waited on the UI thread while holding this would deadlock.
3. **`LayoutRenderer` bounds-checks the candidate indices** it gets from the index (`DrawLayers`'
   `byLayer` grouping and `DrawBitmapShapes` were the only two of eight index-driven reads that did
   not). This is the belt to the lock's braces: a stale candidate names a shape the list no longer has,
   and skipping it costs one slightly-stale frame while indexing it throws on a thread with nothing to
   catch it.

### Gates — `LayoutRenderThreadSafetyTests`

Every one was checked to FAIL with its own fix reverted, which is the only reason to trust a race test:

- `QueryingWhileEditing_NeverCorruptsTheSpatialIndex` — the owner's crash, reproduced in **~14 ms**
  before the fix (600 ms even after the commands started taking `RenderLock`, since the query side is
  what was unguarded).
- `CombinedQueryingWhileEditing_…` — the same for the instance path (`RefreshInstances`, plus a ticking
  resolution version).
- `RenderingWhileEditing_NeverThrowsOnAStaleCandidateIndex` — renders **without** `RenderLock` on
  purpose, so it tests the layer underneath it. Fails in 149 ms with the bounds checks removed
  (`ArgumentOutOfRangeException`).
- `NotifyChanged_TakesTheRenderLock` / `ADeleteHoldsTheRenderLockAcrossItsListMutationAndItsNotification`
  — the lock's own contract, asserted by blocking on it from another thread.

### What this does NOT claim

`RenderLock` does not make arbitrary reads of `Shapes` atomic. An in-place field edit
(`SetShapeFieldCommand`, a drag preview) can still be observed half-applied by a frame already in
flight; the result is one stale frame, never a crash. Serializing *that* would mean routing every
mutation in `src/Ui` through the lock, and the 51 call sites include importers, generators and
persistence working on views no renderer has ever seen.

## Panning a dense PCell — the compiled cell was one path, so zooming in bought nothing (2026-08-23)

Reported symptom: at Zoom-to-Fit a MIM capacitor's via array reads as one undifferentiated glob, and
panning is slow.

The layout itself is tiny — 22 shapes, 2 instances. One of the instances resolves to a generated cell
holding a **24,964-rect via field**: 0.42 µm squares on a 1.26 µm pitch (158 × 158) filling a 201 µm
square.

### The measurement that pointed at the cause

Release, 1600 × 1000, median of 15 warmed frames:

| view | before | after |
|---|---|---|
| Zoom-to-Fit | 35 ms | **7 ms** |
| 4× in | 23 ms | 12 ms |
| 16× in | 18 ms | **2.4 ms** |
| 256× in | 16 ms | **1.6 ms** |

**The bottom row is the diagnosis, not the top one.** At 256× only ~640 of the 24,964 vias are on
screen, and it still cost 16 ms — within 2× of the full-extent frame. Zooming in is supposed to be
nearly free. A cost that refuses to fall as the visible geometry falls is missing culling, not too much
geometry, and no amount of tuning the pan path would have found it.

`CompileCell` flattened a whole sub-cell into ONE aggregate `SKPath` per layer. That makes path
CONSTRUCTION a once-per-cell cost, which is what the compile cache is for and it works. But Skia
rasterizes by walking every segment of the path it is handed, so RASTERIZATION stayed proportional to
the cell's total geometry at every zoom. `LodPixelThreshold` did not help: it asks "is this whole
INSTANCE under 2 device pixels", so a 1000-pixel instance full of 2-pixel vias sails through at full
cost.

### Two fixes

**1. Chunking + culling.** Each layer compiles into a grid of chunks (~256 primitives each, capped at a
32 × 32 grid), every chunk carrying its own bounds. `DrawInstances` inverts the placement matrix — every
one of them is a similarity, so the inverse-mapped viewport is exact — and skips chunks that miss it.
This is the same culling `LayoutSpatialIndex` already does one level up, finally applied one level down,
and it is pixel-identical by construction because a chunk's bounds are the union of what it draws.

**2. Stroke elision.** Fill and stroke were TWO passes over the same path — and the stroke was
80–90 % of the frame (measured at Zoom-to-Fit: fill 20 ms, **stroke 82 ms**), because Skia tessellates
an outline for every one of ~100k segments. On a via that is 2.1 device pixels wide, that outline *is*
the entire visible shape. Below `DefaultStrokeElisionDevicePixels` (4) a chunk now draws one solid fill
of its primitives' bounding rects, grown by half the stroke width it would have been given.

### Traps worth keeping

- **For an opaque axis-aligned rect the elided tier is EXACTLY the exact tier** — fill plus a centred
  2-px outline covers precisely the bbox grown by 1 px, in the same colour. That is why a via field
  loses nothing visually, and it is also why the first version of
  `StrokeElision_DoesNotEngageOnceGeometryIsBigEnoughToSee` was worthless: built on a rect field, it
  passed with the engagement threshold mutated **a thousandfold**. It needs L-shaped polygons on a
  partially transparent layer to have anything to see.
- **A raw pixel buffer's channel order is platform-dependent** (`Rgba8888` here) and the Light theme's
  background is `#F6F6F4`, whose red and blue differ. Comparing `px[i]` against `bg.Blue` therefore
  marked **every** background pixel as painted, and both assertions built on it — painted bbox and ink
  count — were vacuous while green. Use `SKBitmap.Pixels` (normalized `SKColor`) for "is this pixel
  painted"; raw bytes are only safe for comparing two renders against each other.
- **The elided path's cache is published as an immutable record, deliberately.** Avalonia renders off
  the UI thread and a compiled cell is shared by every placement on every canvas, so a rewind-in-place
  cache would be two threads writing one `SKPath`. A miss builds a fresh path and swaps the whole
  record in with one reference assignment; racing threads waste work, never corrupt.

### What is NOT done

The full-extent frame is now bounded by rasterizing 24,964 antialiased fills — the honest cost of
drawing them all truthfully. Merging genuinely sub-pixel clusters into coverage bins measured
24,964 rects → 6,889 (**98 ms → 0.66 ms**) and would remove that floor, but it changes how a dense field
LOOKS when zoomed far out, so it was scoped out rather than slipped in. Two related cliffs stay open and
were measured, not guessed: geometry snap costs **8.8 ms/query** at Zoom-to-Fit (tolerance is a fixed
pixel distance in DBU, so zoomed out it sweeps a 3 µm radius and returns 162 candidates — unusable as
UX, never mind as cost) against 0.04 ms zoomed in; and `EffectiveVisibleLabelHeightDbu` *boosts* any
label under 8 device pixels UP to 8, so labels never get cheaper as you zoom out — 10,000 of them cost
39 ms/frame piled into unreadable mush.

## The same dense-PCell frame again, 30× further down — and the bigger half was never in the renderer (2026-08-23)

The round above left Zoom-to-Fit "bounded by rasterizing 24,964 antialiased fills — the honest cost of
drawing them all truthfully", and scoped out coverage-binning because "it changes how a dense field
LOOKS when zoomed far out". A second design, one order of magnitude denser, showed both halves of that
conclusion were too pessimistic: the cost was **not** honest, and the merge **need not** change how
anything looks.

The design: 26 shapes and **24 instances of one generated capacitor**, each holding a 396 × 396 field
of 0.42 µm vias on a 1.26 µm pitch — **156,816 rects per placement, 3.76 million per frame**.

Release, 1600 × 1000, median of 24 warmed frames, grid on:

| view | before | after |
|---|---|---|
| Zoom-to-Fit | 247 ms | **8.0 ms** |
| 2× in | 156 ms | **11.0 ms** |
| 4× in | 74.8 ms | **11.2 ms** |
| 16× in | 13.5 ms | 12.5 ms |
| 64× in | 10.5 ms | 9.9 ms |

Draw calls at Zoom-to-Fit: **15,170 → 194**.

### Half the frame was a bbox scan, and it was not in the renderer at all

`CellHierarchy.InstanceBbox` unions `LayoutGeometry.BboxOf(shape)` over every shape of the resolved
sub-cell, and `DrawInstances` calls it **per placement, per frame** — it is what the instance-level LOD
decision is taken against. 24 placements × 156,816 shapes is **3.76 million bbox unions every frame of
every pan**, before a single pixel is drawn. It is also what the spatial index and Zoom-to-Fit measure
with, so the same scan runs again on those paths.

It cost ~20 ms of the post-chunking frame here and was invisible in the previous round for a plain
arithmetic reason: 2 placements of a 24,964-shape cell is 50k unions, which rounds to nothing. The
defect did not change; the design got 75× bigger.

A view's own shapes' bbox is now memoized on the view REFERENCE
(`CellHierarchy.ShapesBbox`), piggybacking on `CellLayoutResolver`'s (path, mtime) cache lifecycle
exactly as the renderer's compiled-geometry cache already does — a file change produces a new
`LayoutView` and therefore a natural miss, with no invalidation call to forget.

- **Deliberately the shapes only, never the recursive result.** `CellBboxRecursive`'s answer depends on
  the `visiting` set and the `depth` it was reached at, so the same sub-cell down two different chains
  can legitimately have two different effective bboxes. Its own shapes depend on neither, and they are
  where all the time was.
- **The eviction belongs in `CellLayoutResolver.SetLive`, not at the workspace seam.** A live view is
  the one view mutated IN PLACE, and `SetLive` is the single moment it is republished.
  `LayoutHierarchyLiveRefreshTests.GrowingSubCellExtent_…` caught this immediately when the eviction was
  hung off `InvalidateCompiledGeometry` instead — that test asserts a grown sub-cell's bbox self-heals
  from `SetLive` **alone**, with no explicit reindex, which is a stricter contract than the compiled
  geometry's and is the right one.

### Coverage is what makes the merge free rather than lossy

The previous round was right that collapsing sub-pixel clusters is a visual change *in general*. It is
not one where it actually pays, and the condition is checkable.

The elision tier grows every primitive by half the hairline stroke on each side. On a uniform pitch p
with grown side s, the fraction of a chunk's bounds its primitives cover is exactly (s/p)² — so
**coverage ≥ 1 is s ≥ p is "adjacent grown primitives touch", and their union IS the bounding box.**
Zoom a via field out and the elided tier is already painting a solid block; it just gets there by
tessellating and merging ~150 mutually overlapping rectangles per chunk, per placement, and throwing
the overlap away.

So the coarse tier (`BuildCoarse`) contributes ONE rect — the chunk's own grown bounds — for every
chunk that is both on the elision tier and at coverage ≥ 1, and batches all of them into **one path per
layer**. Coverage comes from three sums banked at compile time (Σ area, Σ (w+h), count), so
coverage(g) = (ΣA + 2g·ΣS + 4g²N)/boundsArea is a few flops per chunk per zoom step.

- **Both gates are functions of the grow amount ALONE**, which is why the split is cacheable for a whole
  pan gesture. grow is `GeometryStrokeDevicePixels / 2` device pixels expressed in cell-local path
  space, so it already carries the zoom *and* the placement's magnification; the elision test
  `MaxExtent · placementScale · scaleUm < threshold` rewrites to `MaxExtent < threshold · 2g /
  GeometryStrokeDevicePixels` with no second key needed.
- **The size gate is not redundant with the coverage gate**, and a test has to be built specifically to
  say so. Coverage is an AREA measure, so geometry that overlaps itself can sum past its own bounding
  box while leaving a large part of that box empty — 40 overlapping 50 µm squares clumped at one end of
  a 400 µm extent reach coverage 1.28 with a 250-px hole. Only the elision tier's few-device-pixel size
  limit keeps that off the coarse tier. Mutation-checked both ways:
  `CoarseTier_OnAFieldTooSparseForItsGrownPrimitivesToMeet_…` goes red when the coverage gate is
  slackened, `CoarseTier_OnLargePrimitivesThatOverlapButLeaveAGap_…` when the size gate is removed, and
  neither catches the other's mutation.
- **The equivalence assertion is about WHERE two renders differ, not how much.** Inside the field both
  tiers paint the same opaque colour over the same pixels and must agree EXACTLY; along the field's
  outer edge they antialias one boundary two ways and land a fraction of a pixel apart (max channel
  difference 35/255 on a handful of edge pixels). A whole-frame tolerance loose enough to admit that
  would also admit a wrong interior — so the test partitions by position and demands zero interior
  differences.
- **The earlier fixture trap repeats one level up.** A field of RECTS cannot tell the coarse tier from
  the elided one at any zoom, for the same reason it could not tell the elided tier from the exact one:
  the substitution is exact for an opaque axis-aligned rect. The gate tests need the clumped/sparse
  fixtures above, not a denser via field.

### What the frame is now, and what is NOT worth doing

At 8.0 ms, **the grid is 4.5 ms of it and the artwork 3.5 ms** (measured against an empty view: 4.5 ms
with the grid, 0.1 ms without). The grid is ~25,000 antialiased round-cap dots at the 8-device-pixel
minimum spacing, and the cost is Skia rasterizing them, **not** building them: restructuring
`DrawGrid` to hoist the per-row work out of the per-point loop (25,000 64-bit modulos and world-to-screen
transforms became a few hundred) and to fill exactly-sized arrays instead of growing two lists —
~400 KB of allocation a frame — measured **no change at all**, and was reverted rather than kept as
unmeasured complexity. Note also that this whole harness is CPU raster; the application leases a
GPU-backed canvas, where 25k points is a different proposition from 3.7M tessellated rects.

## Once the frame got fast, the pointer became the bottleneck — and it was never the renderer (2026-08-23)

Reported after the round above: zooming painfully slow, the geometry-snap glyph slow to follow the
mouse, marquee select slow, and zoom-then-select worst of all. Three of the four are one cause, and it
is not in the renderer at all.

**Measured first, on the same 24-placement / 156,816-via-per-placement design.** A frame is 5–16 ms at
1600 x 1000 at EVERY zoom from full extent to 4096x — the render was not what any of these
complaints were about. The one outlier was the snap query: **10.9 ms per pointer move, returning
15,621 candidates.**

### Why a snap query can return fifteen thousand candidates

Snap tolerance is a fixed SCREEN distance converted to world units. How many features land inside it is
therefore a property of how dense the geometry is on screen, and nothing the query controls: at full
extent, eight device pixels is tens of microns, which over a 1.26 um via pitch is thousands of vias and
~9 intrinsic features each. Every one was collected, and the whole list sorted, on every pointer move —
for a caller that reads `[0]`. The only consumer that wants more is the click-cycle stack, and it cannot
ask anyone to page through fifteen thousand indistinguishable vias either.

### The cost was not where it looked

Bounding the candidate list (`LayoutSnapCandidateSet`, cap 64, trimmed at twice that) took 10.9 ms to
3.5 ms — so the list and its sort were real, but not the whole story. Pushing the visibility filter down
into the feature index so the cap could not discard a survivor then made it **worse: 23.9 ms**. That is
the measurement that found the actual defect.

`LayoutSnapQuery.ResolveLayer` was a **linear scan of `Technology.Layers`**, and a real process stack
here is ~380 layers. It was called once per snap FEATURE. Moving the filter earlier simply called it
more often, which is why the "improvement" ran backwards and why the first bound looked like it had
fixed more than it had. Replaced with a map built once per query (plus a one-entry memo in front of it,
which answers nearly everything, because features arrive grouped by shape and a dense field is one
layer): **0.9 ms.** The map is deliberately per-query rather than cached on the `Technology`, so no edit
to a technology can leave it stale.

- **First wins, not last.** The scan returned the first matching layer, so the map is built with
  `TryAdd`. A technology with a duplicate key would otherwise silently resolve to the other one.
- **The filter must run BEFORE the cap, and a test has to say so.** Applied the other way round, a
  dense field on a HIDDEN layer fills the cap and the single visible feature — the only thing that
  should come back — is discarded before it is ever tested.
  `ADenseFieldOnAHiddenLayer_ContributesNothing_…` goes red on exactly that inversion.

### The marquee was paying for a query it cannot use

`UpdateSnapMarker` runs at the top of `HandleSelectMove`, before the drag-kind switch, so it ran on
every move of a marquee drag — the one gesture that deliberately sweeps the cursor across as much
geometry as possible. A marquee's rectangle is built from the RAW pointer position and its commit reads
only `ComputeMarqueeSelection`; nothing consumes the snap answer. It is now skipped, alongside the
existing scale-drag and out-of-scope-handle guards. `MarqueeDrag_RunsNoSnapQuery` holds it, with
`TheSameMovesWithoutADrag_DoRunTheSnapQuery` as the control that stops the guard being deleted
silently.

### What remains, measured rather than assumed

- **A slow zoom BAND, and it is honest.** At 3200 x 2000 a frame is 17–40 ms almost everywhere but
  ~60 ms between roughly 5x and 22x of full-extent zoom. That is exactly the window where the coarse
  tier has switched off (grown primitives no longer touch) and the elision tier has not yet
  (primitives still under a few device pixels): the vias are individually resolvable, so ~180k
  separate antialiased rects is what drawing them truthfully costs.
- **Quantising the cache key to survive a zoom gesture was tested and refuted.** In the band, pan-only
  frames and fresh-zoom-every-frame frames measure IDENTICALLY (66.5 vs 67.8 ms at 8x; 62.8 vs 62.7 at
  22.6x), so the per-zoom-step rebuild of the elided/coarse paths costs nothing measurable and there is
  nothing for a coarser cache key to save. The band is rasterisation.
- **The first hover over a dense cell still costs ~120 ms**, building that cell's snap-feature index —
  ~1.4 million features for a six-figure via field. One-time per cell per session, and unchanged here.
- This harness is CPU raster; the application leases a GPU-backed canvas, where per-rect rasterisation
  is a very different proposition. The snap and marquee costs above are CPU either way.
