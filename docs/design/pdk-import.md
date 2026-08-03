# circuitRF — PDK Import Design

**Status:** Shipped · **Date:** 2026-08-01

§2 describes what an import USED to do and is kept only as the "before" this replaced. §4–§8 are the
behaviour as built (`docs/sonnet-briefs/brief-pdk-in-memory-import.md`); §9–§10 are decisions and open
questions.

Companion to **`pdk-external-devices.md`** — the full PDK stack: reading a kit, the provider seam, node
and slaved-node elaboration, the worker protocol, and the macOS VM. **Read that one first**; this doc
covers only what an import puts in the workspace and how that is changing. Also
`workspace-and-project-tree.md` (cells, primacy, `.cws`), `pcell-contract.md` (the other kind of
externally-defined component), and `src/Ui/Schematic/CLAUDE.md` (the implementation notes).

---

## 0. The one standing rule

**No vendor or product names appear anywhere in circuitRF — code, comments, tests, fixtures, log output,
or documentation.** A kit's identity is *data it supplies at run time*, never something written down in
the product. No manufacturer, part number, model-family name, formulation name, EDA-tool name, or library
filename from any kit.

This is not a style preference; it is the same principle that already decides three separate mechanisms
here, and each was chosen over the naming alternative for the same reason:

| Mechanism | Instead of |
|---|---|
| Scan a library's own export table for the ABI entry points | A compiled-in list of library names |
| Read the host module name out of the model's own PE import table | A remembered module name |
| Read node aliases from a run-time data file | A compiled-in table |

A kit is recognised **structurally**, so a synthetic test fixture (`SampleKit`, `PART_A`,
`KITLIB_DEVICE_v1`, `TYPEA`/`TYPEB`) exercises the real rule.

**This rule is held by authoring and review, NOT by an automated scan — and it cannot be.** A scan needs
a list of the names to look for, and that list is itself a page of vendor names sitting in the repo. One
was written and deleted; it needed a self-exclusion so it would not fail on its own contents, which is the
clearest possible sign the approach is self-defeating. Do not re-add one. Where a real measurement must be cited, cite
the number and not the kit. Anything a kit itself needs to declare and circuitRF cannot derive goes in a
run-time data file, never in the product.

---

## 1. What a PDK is here

A vendor kit is a read-only tree containing, in whatever layout that vendor chose: symbol descriptions
(`.dsn`), netlists defining its parts, palette icons (`.bmp`), model data files, and — usually in a
*separate* package beside it — the compiled model library its netlists name but never define.

**A kit part is an ordinary cell reference.** That is the load-bearing design decision of this whole area:
a cell reference is *already* the component whose artwork lives in an external file and resolves at render
time, so placement, rendering, pin geometry, hit-testing, net extraction and the symbol editor all work on
kit parts unchanged. There is no "external part" species and there must not be one — a parallel render path
would duplicate all of that and drift.

Three things a kit does not tell us, which circuitRF therefore works out and records:

- **which library implements its device types** — a delivery is several kits beside one shared library
  package, and no kit says which. Established by scanning candidate libraries for the entry points
  circuitRF's own worker calls.
- **which of a dozen builds of that library to use** — same name, many toolchains. The most specifically
  named build for the target wins, and the choice is reported because it was made automatically.
- **which internal nodes of a model are not free unknowns** — detectable structurally (identically-zero
  Jacobian rows), but *which node each follows* is not derivable from anything the model reports. Supplied
  as run-time data.

---

## 2. How an import used to work (superseded by §4)

`Import PDK` reads the kit and **installs its parts as real cells** under `<workspace>/pdk/<kit>/`:

```
<workspace>/pdk/<kit>/
  device-provider.json          circuitRF's own working-out: library per platform, variants, paths
  <part>/.ccell                 the part's parameter interface + provider/type/netlist bindings
  <part>/symbol/<part>.csym     the translated symbol
```

A placed part's `CellRef` is a relative path from the schematic to that cell folder, and
`CellSymbolResolver.Resolve(cellRef, baseDir)` reads `.ccell` → resolves the primary → loads the `.csym`.

Measured on a real 26-pin part: **40 KB, five files.**

**None of this happens any more** — see §4. It is kept because §3's findings are about this shape, and
because the one thing worth remembering from it is *why* it looked reasonable: a kit part really is an
ordinary cell reference, and that is exactly the property §5 preserves without writing anything down.

---

## 3. Why that is being changed (shipped behaviour, measured)

Three findings, all verified against a kit and a real workspace.

**3.1 — Sharing is already broken, not merely leaky.** `.ccell` and `device-provider.json` are full of
**absolute paths**: the kit root, the model library per platform, the part's icon, its netlist, and the
worker command. A colleague opening a shared workspace gets a symbol that renders — it is in the workspace
— and everything else dangling, failing *quietly* until they press Run.

**3.2 — The translated symbol is in the workspace, so sharing a workspace shares it.** 18 KB of translated
symbol geometry per part, derived from the library's own drawing.

**3.3 — But removing the folder does NOT stop a workspace carrying vendor parameter data.** Instance
parameters are copied into the schematic at placement and persisted there — this is how instance overrides
work for *every* component, not a PDK quirk. A `.csch` placing a kit part already contains, in shape:

```
CellRef: ../../pdk/SampleKit/PART_A_MODEL
Params : [('ModelAs','TYPEA'), ('TAMB','-1'), ('RTH1','1E-06'), ('CTH1','1E-07'),
          ('RTH2','2E-06'), ('CTH2','5E-08')]
```

**This is an accepted limitation of the design in §4, not a gap to close.** The change removes symbol
geometry from the workspace; it does not remove the part name, the kit name, or the parameter values. Do
not re-litigate it as a bug.

**3.4 — Kit parts pollute the Project Tree.** They appear as ordinary cell folders under `pdk/`, as if
they were the user's own cells.

---

## 4. An import writes nothing into the workspace

The workspace records a **reference** to the kit and **the decisions circuitRF made about it**. Every
translated artifact — symbols, parameter interfaces, palette icons — is rebuilt in memory when the
workspace opens.

**Persist what circuitRF *decided*; rebuild what it *translated*.** An import both translates and decides.
The translations are the thing being removed from the workspace. The decisions are tiny, carry no geometry,
and are the difference between a workspace that opens the same way twice and one that quietly re-decides —
so they are recorded.

Per referenced kit, `.cws` records: kit path, provider name, resolved model-library path per platform,
chosen variant defaults, and the translation version (§6). **Nothing else.**

**Path storage follows `WorkspaceRefs`' existing rule** — relative when inside the workspace tree, absolute
otherwise, separators normalised to `/`. A kit is normally *outside* the workspace, so the absolute branch
is the common case, which is exactly why §8's repair flow has to be good.

---

## 5. A kit part's `CellRef` becomes virtual

A kit part's reference becomes an explicitly virtual form (e.g. `pdk://<kit>/<part>`), **not** a relative
path that happens not to resolve. A missing kit and a mistyped path must be distinguishable; otherwise
every "is this cell reachable" check has to guess which it is looking at, and the repair flow can tell the
user nothing useful.

**Resolution stays in the existing funnel.** `CellSymbolResolver.Resolve` is already the single static
entry point with its own cache; the in-memory branch goes there, mirroring the two patterns this codebase
already has for exactly this shape — `CellLayoutResolver.SetLive`/`ClearLive` and `TechnologyCache`'s
separate `_live` dictionary checked ahead of the file-backed one. A *separate* dictionary, for the same
reason `TechnologyCache` uses one: dropping the override must fall back cleanly without forcing a disk read
of a file that was never there.

**Symbols are centralised; `.ccell` is not, and that is the bulk of the work.** `.ccell` is read directly
by parameter seeding at placement, by `PdkPartInstaller.LoadInstalled` at every workspace open, and by
`CellFolder.ResolvePrimary`. All of those must resolve a virtual kit part from memory.

**A welcome consequence:** placing a cell currently requires a *saved* schematic, because `CellRef` is
computed relative to the schematic's own directory. A virtual reference has no such need.

**Kit parts stop appearing in the Project Tree**, since nothing is on disk. That is correct — they are not
the user's cells — and it is why §8's dialog is required rather than optional.

---

## 6. Translation versioning — the rule that protects placed wires

`DsnSymbolReader` snaps every pin to the P=100 connection grid. If that reader changes — a scale fix, a
snap fix, anything touching pin placement — re-derivation **moves pins, and wires attached to them silently
disconnect.**

Today the frozen `.csym` is what prevents this. In-memory removes that protection, so a **translation
version is pinned per kit reference** to replace it. A mismatch between the recorded version and the current
reader is **reported and refused, never applied silently**: the design still opens, and the upgrade is an
explicit action the user takes — because the thing on the other side of that upgrade is pins moving under
placed wires.

**This cannot be retrofitted once designs exist.** It is designed in from the start.

---

## 7. Loading, and the budget

**Silent on success.** A workspace open re-reads and re-processes every referenced kit and must produce no
import report and no per-part messages when everything resolves. The report becomes an explicit action (§8).

**Recorded decisions are read, not re-derived.** This is what keeps the open both fast and deterministic.

**Budget: a 20-symbol kit loads in under 100 ms.** A miss is a **stop-and-ask**, not a slower number — if a
workspace open cannot re-derive a kit inside that, the in-memory approach is paying a cost the on-disk one
did not, and that is an owner decision. Specifically: do not tune around it, do not cache extra artifacts to
disk to hit it, and do not relax it.

**Measured, and it clears by a wide margin: 0.5 ms** for a synthetic 20-symbol kit whose settings are
replayed — 200× under budget. The same kit with nothing recorded, so that discovery genuinely runs, takes
**199.8 ms**, which is itself over the budget. That gap is the whole justification for recording the
decisions rather than working them out again on every open, and it is pinned by
`tests/Ui.Tests/PdkInMemoryLoadBudgetTests.cs` as a comparison rather than a second absolute number.

Measurements this rests on, taken on a kit:

| | Cost |
|---|---|
| Symbol parsing (two files, a few KB) | negligible |
| Netlist parsing (11 KB + 6 KB) | negligible |
| **Library discovery** | **~62 ms** — byte-scans candidate builds across a separate multi-MB package |
| Kit as a whole | 17 MB / 46 files, almost none of it processed |

Discovery is the only cost worth caring about, and it is exactly the decision §4 records — so it leaves the
open path entirely. 20 symbols should land nearer 10 ms than 100 ms. **If it does not, something is being
re-derived that should have been recorded; look there first, then stop and ask.**

---

## 8. Broken references, validation, and the management dialog

**A broken kit reference is a first-class, repairable state — never a silent failure and never a blocked
open.** A workspace whose kits are absent still opens, with kit parts drawn as the existing `NotFound`
placeholder. Same rule as layout restore and foreign documents: the user's design is their data; a missing
dependency degrades, it does not deny.

**One summary per kit, not one message per part** — forty parts must not produce forty warnings. Details go
to a log file in the workspace, with Messages carrying the summary and a clickable path to it (the existing
`Messages.Post` file-path link, not a second reporting channel). The log is a diagnostic artifact,
overwritten per load, never project state — and §0 applies to its contents.

### `Validate PDK`

Re-reads a referenced kit and reports **drift**, not just breakage: a part the design placed that the kit no
longer offers, or a recorded translation version that no longer matches the reader. A kit that merely
resolves is a one-line "no problems found".

`PdkReferenceManager.Validate` is where this lives, and it is framework-free on purpose — the decisions it
makes (is this reachable, does it still hold what was placed) are the part worth testing, and none of them
need a window.

**It returns what it CHECKED as well as what was wrong** — parts offered, placed parts checked, problems,
notes. "No problems found" on its own cannot be told apart from a check that did nothing, and that is the
one thing a validation must not be ambiguous about. A kit that could not be read reports −1 parts offered
rather than 0: "offers nothing" and "could not be read" are different answers to different questions.

**It checks placed parts against a FRESH read of the kit, never against what happens to be loaded.** The
question is whether the kit still holds the part, not whether this session managed to load it; conflating
the two turns "your kit changed" into "something went wrong at startup".

### `File ▸ Manage PDKs…`

One dialog, workspace-gated, listing every referenced kit with name, stored path, resolved/broken, part
count and translation version.

| Action | Behaviour |
|---|---|
| **Add…** | Folder picker; imports and adds the reference — through the *ordinary* import path, never a second implementation |
| **Remove** | Drops the reference, after warning how many placed parts it will leave unresolved |
| **Reveal** | Opens the kit folder in the platform file manager |
| **Validate** | Runs `Validate PDK` on the selected kit |

Reached by **Ctrl/⌘+P**. Every action shows its result in the dialog *and* posts to Messages — the dialog
is dismissed, and the record of what was added, removed or validated has to outlive it.

### Referencing the model libraries

A vendor delivery is several part kits beside **one shared package holding the compiled models**, and
discovery finds that package by *adjacency*. Once a kit is referenced from somewhere else — a workspace
folder, most obviously — that adjacency is gone and nothing on disk can recover it.

So a workspace may reference the package directly: **Add…** accepts a folder with no parts when it holds a
library our worker can drive, and records it with `IsLibraryOnly`. Those roots are handed to discovery,
searched after the ancestor walk so a library sitting with its kit still wins. They resolve before any part
kit, because a kit loaded first would settle "no library found" and record that.

Widening the ancestor walk instead was rejected: the further out it goes, the less that territory has to do
with the kit, and it would eventually match something by accident.

**Whether sharing the workspace carries a kit is stated per reference, never as a blanket note.** A kit
inside the workspace tree travels with it; one outside does not. A single line covering both is wrong for
one of them, and both outcomes are worth saying — the positive one is the answer to the question the dialog
is usually opened to ask.

**Add is also Repair, and that is the whole point of keying a reference on the kit's NAME.** A kit that
moved is re-added at its new path under the same name, and every placed part resolves again without a
single schematic being touched (`PdkReferenceManager.AddOrRepair`, which replaces rather than appends).

**Removal is warned and reversible.** Nothing is deleted from any schematic: the parts keep their
references and become unresolvable until the kit is added back. The warning counts parts in schematics that
are currently OPEN — deliberately, since scanning a whole workspace to produce a number would make the
dialog wait on file I/O — which is why removal is stated as reversible rather than as safe because nothing
appeared to use it.

**Reveal reuses what exists** — the platform-specific `open -R` / `explorer /select,` / `xdg-open` was
extracted to `WorkspaceViewModel.RevealPathInFileManager` and both the tree and this dialog call it. Do not
write a second reveal and do not hard-code "Finder" in a label. Reveal on a missing kit is disabled.

**Repairing a reference does not disturb any design.** Virtual refs are keyed on the kit *name*, not its
path, so re-pointing re-resolves every placed part with **no schematic edit**. That is the whole reason §5
makes the reference virtual rather than a path.

**Removing a reference while parts are placed** is allowed, warned with the affected instance count, and
reversible — nothing is deleted from any schematic; those parts become broken references until the kit is
re-added.

---

## 9. Decisions taken, and one rejected

**No migration.** circuitRF is alpha; a workspace built by the on-disk code is not supported across this
change and needs its kit re-imported. No conversion path is written, and an existing `pdk/` folder is
neither read nor deleted.

**A per-user disk cache of translated artifacts was considered and rejected.** It would keep the freeze that
§6's translation version replaces, avoid re-derivation entirely, and cost far less to build — but it puts a
translation back on disk, which is the thing being removed. Recorded here so it is not re-proposed as an
optimisation.

**Vendor parameter data in `.csch` is accepted** (§3.3). Not in scope.

---

## 10. Open questions

- **Kit version identity.** Two users with different kit revisions will see different symbols — arguably
  correct, since each should use their own licensed kit, but a *pin-count* change is a broken design rather
  than a cosmetic difference. Recording a kit version alongside the reference would let the mismatch be
  reported rather than silently rendered. Not designed here.
- **Offline opens.** After this change, opening a design without its kits mounted means every kit part is a
  placeholder. Honest, and repairable through §8 — but it makes kit availability a hard dependency of
  opening a design, and whether that wants any further affordance is unsettled.

- ~~**A kit that declares its parts in a CATALOG yields no parts, and says nothing about it.**~~
  **CLOSED (2026-08-01)** — `ComponentCatalogRecognizer` plus a catalog pass in `DiscoverParts`.
  Measured on the same kit: **0 parts → 109**, grouped by catalog file, and a compiled model library
  is now recognised so the import warns that the parts are placeable but will not simulate until a
  device provider is available. A zero-part import now always reports why. The original text is kept
  below because the *reasoning* about findings is the part worth not losing.

  <details><summary>the original entry</summary>

- **A kit that declares its parts in a CATALOG yields no parts, and says nothing about it.**
  `DiscoverParts` (§1) finds parts two ways: a `<cell>/<view>/<file>` database tree, or the subcircuits a
  netlist declares. A third shape exists and is not handled — a kit whose parts are listed in a **catalog
  file** (part name, symbol reference, icon, description, one entry per part), with the behaviour supplied
  by a compiled model library rather than by any netlist. There is no netlist to read and no cell directory
  to walk, so the importer recognises the data files and finds **zero parts**.

  *Measured of that shape:* 107 assets — 86 Touchstone files correctly recognised and
  `Supported` — and **0 parts, 0 findings**. The kit's own name was derived as a bare version number,
  because the version directory is the deepest common folder.

  **The missing findings are the worse half.** This area's design says the three `PdkAssetSupport` states
  exist precisely so "I do not know what this is" and "I know exactly what this is and cannot read it yet"
  are different messages, and `PdkFinding.SuggestedAction` exists so an import that cannot produce a part
  says what would make it work. An import that yields nothing and reports nothing satisfies neither: the
  user references a kit, sees an empty palette, and is told no reason. Whatever else changes here, a
  zero-part import should be a finding.

  Reaching the intended experience — reference a kit, place a part from the palette, run an analysis,
  without the user knowing or caring what format the kit came in — needs part discovery to learn this
  shape, a symbol for each part, and the model library reached through the existing device-worker seam.
  Only the first of those is a gap in *this* document's area.
