# Sonnet Brief — SL3: Reporting a changed cell interface

**Read `brief-shared-library-0-overview.md` first.** SL3 is independent of SL1 and SL2.

**Scope: telling a user that a cell they instanced has changed shape underneath them** — and telling
them *what* changed, in the session where it first matters, instead of leaving them to find a wire that
looks connected in the picture and is not connected in the netlist.

**This is not a version-control feature, a locking feature, or an approval workflow.** It records one
fact at placement and compares it at resolve.

---

## 1. Why this is a shared-library problem specifically

`workspace-and-project-tree.md` §4 already states the hazard and accepts it:

> Different `.csym` files in a cell may declare different pin counts/positions. When the primary
> changes, pins re-resolve from the new `.csym`, and any wire that no longer meets a pin shows as
> unconnected … The risk is made visible (dangling wires + re-rendered glyph), and the user accepts it.

**That bargain is sound when the person who changed the cell and the person who accepts the risk are the
same person, in the same minute.** It stops being sound the moment a librarian changes a cell that
forty designs reference: the change and its discovery are separated by people and by weeks, "made
visible" means a wire that renders slightly differently, and the netlist is quietly different from the
one that was simulated last month. Connectivity here is **positional** — a port is connected when
something else shares its P-cell (`EditableSchematic.BuildRenderModel`, `ConnectTolerance` at `:1165`) —
so moving a pin by one grid cell silently disconnects it, and nothing in the file records that it used
to be connected.

There is nothing to pin against either. `.clib` was specified to record a version; **nothing parses it**
(`WorkspaceScanner.ResolveLibrary` matches the extension at `:291` and never opens the file; there is no
`ClibFile` type in the tree). R-sl-6 refuses to build a version resolver, and §5 below says why that
refusal is the right one.

**R-sl3-1. The remedy is a REPORT, not a refusal and not an automatic repair.** The librarian's new
symbol is the truth and must render; auto-rewiring is `symbol-editor.md` §6's deferred Option B and stays
deferred. What is missing is only that the user is not told.

---

## 2. What "the interface" is

**R-sl3-2. A cell's interface is what an instance in a parent depends on, and nothing else:**

- the **pins**, in `PortIndex` order: `PortIndex`, `LocalX`, `LocalY`, `Name`
  (`SymbolModel.cs:323-336`);
- the symbol's **`PortCount`** (`SymbolModel.cs:355`), which is not the same number as the pin count;
- the **declared parameter names** from the `.ccell` — the cell's interface record in
  `workspace-and-project-tree.md` §2's own words.

**Not** included, deliberately: the drawing primitives (a redrawn glyph that keeps its pins breaks
nothing), the parameter DEFAULTS (an instance that overrides one is unaffected, and one that does not is
*meant* to follow the library), the schematic behind the symbol, or anything about the layout view.
Reporting a change that cannot break a referencing design trains the user to dismiss the report, which
costs more than the report is worth.

**R-sl3-3. The interface is compared as a hash, and the hash is stored, not the interface.** A short
content hash over R-sl3-2's fields in a fixed order. Storing the whole signature per instance would put a
copy of the library's interface into every referencing file — a second source of truth, which is the
thing every reference form in this codebase is built to avoid.

---

## 3. Where the hash lives

**R-sl3-4. The instance records the hash it was placed against, in the file that holds the instance.**
`CschComponent` (`SchematicPersistence.cs:50`) beside its `CellRef` (`:87`), and `LayoutInstance`
(`LayoutModel.cs:585`) beside its own. One optional string, `[JsonIgnore(WhenWritingNull)]`, **no
`FormatVersion` bump** — an absent field on an existing file reads as null, which is the established
convention for every optional field these formats have gained (`CwsFile.DefaultTechRef`,
`PdkRefs`, `ReferencedWorkspaces` all say so in their own doc comments).

**R-sl3-5. Absent is not a warning.** Every file written before this feature has no recorded hash, and
so does every instance a user places by hand-editing a file. Absent means *never recorded* and renders
exactly as today. A feature whose first act is to mark every existing design as suspect is a feature that
gets turned off.

**R-sl3-6. The hash is written wherever a `CellRef` is written, and by the same helper.**
`ExternalCellRef.MakeCellRef` (`:142`) is already the ONE producing rule for a cell reference, adopted
precisely because call sites that each did their own thing drifted. The recorded hash has the same
property and must not acquire a second producing site.

---

## 4. The state, and where it surfaces

**R-sl3-7. `InterfaceChanged` is a fourth state, and it does not collapse into the other three.**
`CellSymbolState` today is `Resolved` / `NotFound` / `PrimaryMissing`
(`CellSymbolResolver.cs:21-29`), and §4.2 is explicit that the three must stay separate paths because
the right user response differs in each. The same argument applies here, and the response is different
again: the cell resolves, the symbol is fine, the drawing is correct — **and the design may not mean what
it did.** It renders normally; it is marked and explained.

**R-sl3-8. The report names what actually changed, not that something did.** The comparison has both
sides in hand at the moment it fires, so it can say *"`Amp`'s interface changed: 4 pins → 5, pin `vg`
moved"* — and, most usefully, **which of this instance's ports are now unconnected**, since that is the
electrical consequence and it is computable from the connectivity pass that is already running
(`BuildRenderModel`, `:1176`). A report that only says "changed" makes the user do the diff by eye
against a file they cannot see.

**R-sl3-9. Three surfaces, all already built:** the Messages panel on open (one line per affected cell,
not per instance — forty instances of one changed cell is one problem), the instance's Properties
inspector, and the chrome marking §5C R51 already defines for a resolved external reference. **Not the
rendered geometry** — R36's rule holds without exception.

**R-sl3-10. Accepting the change is one explicit gesture, and it is never automatic.** *"Accept the new
interface"* rewrites the recorded hash for the selected instances, or for every instance of that cell in
the document. It must not happen on open, on save, or as a side effect of any edit: the recorded hash is
the only evidence that the design was authored against a different interface, and a product that erases
that evidence on open has implemented nothing.

**R-sl3-11. This applies to every cell reference, not only `ws://` ones.** The same failure exists for a
cell in your own workspace — §4 says so — with a smaller blast radius. Conditioning the check on the
reference form would make it fire only sometimes, which is a rule nobody learns, and would mean the
local case that §4 already describes as risky stays unreported forever.

---

## 5. Version pinning, and why it is a path and not a mechanism

R-sl-6 fixes this: **an alias points wherever the librarian says it points.** `…/stdlib/v2.3/.cws` is a
complete pinning story — it needs no manifest, no comparison rules, no "which version is newer"
question, and it lets a group run two versions side by side under two aliases (`stdlib` and
`stdlib_next`) with no product feature at all. SL1's `${CRF_LIB}` expansion is what makes that path
portable enough for a librarian to publish.

**What a real version mechanism would have to answer, none of which has an obvious answer here:** what a
version *is* when membership is the filesystem (§intro) and nothing enumerates members; what happens when
a referenced cell's own sub-cells come from a different version; whether a pin is compatible across
versions, which is exactly the question R-sl3-2's hash refuses to guess at; and who decides that a
change is breaking. **Do not start down this road as part of SL3.** The hash reports the one fact that
can be established without answering any of them.

---

## 6. Gate

`tests/Ui.Tests` (do not touch `src/Core`, `src/Engine`, `RfCore`). `ExternalCellReferenceTests.cs` and
the symbol-resolver tests are the existing homes.

1. **Hash stability** — the same cell hashes the same across two resolves, across a process restart,
   and after the `.csym`'s drawing primitives are changed without touching a pin (R-sl3-2's exclusion,
   asserted rather than assumed).
2. **Each interface change fires**: a pin moved, a pin added, a pin renamed, `PortCount` changed, a
   declared parameter removed. Five cases, five assertions.
3. **Each non-change does not fire**: primitives redrawn, a parameter DEFAULT changed, the cell's
   schematic edited, the layout view edited.
4. **Absent hash renders exactly as today** (R-sl3-5) — an existing fixture opened unchanged produces no
   report and no marking.
5. **The report names the newly-unconnected ports** (R-sl3-8) against a fixture where moving one pin
   disconnects one wire — the electrical consequence is the point of the feature, so it is gated, not
   just the detection.
6. **Accept rewrites the hash and clears the state** (R-sl3-10), and **open/save do not** — the second
   half written as its own test, because it is the half that would be lost to a convenience change later.
7. **A local (non-`ws://`) cell reports identically** (R-sl3-11).

---

## 7. On completion

Findings to `src/Ui/RESOLVED.md` (**not** `CLAUDE.md`).

Update `docs/design/workspace-and-project-tree.md` §4 — the "risky user operation, surfaced not blocked"
paragraph now has a mechanism, and §4.2's three missing-symbol states gain a fourth that is *not* a
missing symbol and must be recorded as distinct from them for the reason §4.2 itself gives. Add the
recorded-hash field to `docs/design/project-file-formats.md`.

**Report, do not silently absorb:**
- Whether R-sl3-2's exclusions held up on a real library, and in particular whether a changed parameter
  DEFAULT turned out to be something users wanted reported after all — that is the exclusion most likely
  to be wrong, and the owner should hear it from a fixture rather than from a bug.
- The cost of computing the hash on the resolve path, as a **call count**, not a time.
- Anything else in the tree that stores a `CellRef` and would therefore need the field (R-sl3-6's
  producing rule should make that list short; if it is not, say so).
