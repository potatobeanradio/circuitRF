# WB-B2 — placing a wBond in a schematic: the symbol that comes from a file

**Phase:** WB-B2 — the unfinished row of **WB-B**, not a new phase. `docs/design/wbond.md` §13's table
marks WB-B as "the component"; everything in that row shipped except the one thing that makes it
reachable, so this closes it rather than opening WB-E.

**Design authority:** `docs/design/wbond.md` §5 (WB18–WB21), §9.2 (the three routes), §7 (WB28–WB30a,
the coupling audit). Read those first — this brief implements them and does not restate their
reasoning.

**Predecessor:** WB-A, WB-B, WB-C and WB-D are all complete. See `src/Ui/CLAUDE.md`'s wBond entries.

---

## 0. What this phase is, in one paragraph

A `.wBond` design can be **placed in a schematic**, wired, simulated and swept. Everything below the
placement line already exists and is tested: `ComponentModelFactory` dispatches the `wBond` type,
`Elaborator.ResolveWBondParameters` resolves its `File`, `WBondModel` stamps 2M+1 terminals and
refuses an undeclared return path, and `WBondSymbolGenerator.Build` produces the dynamic symbol. What
is missing is the schematic layer: **there is no `SymbolKind.WBond` and no `ComponentTypeRegistry`
entry**, so the component cannot be placed at all, and §9.2's routes 2 and 3 are blocked behind that.

**This is a small-looking gap with one genuinely new design question inside it, and the whole brief
turns on that question:** a wBond's symbol is generated from a **referenced file's contents** — the
pin count and the pin NAMES come from the `.wBond`'s array list — and **no existing placement path in
this codebase handles that.** Every other symbol's shape is known at registry time, chosen by the
user, or read from a cell folder on disk. Get the mechanism right and the rest is wiring; get it
wrong and you will ship a symbol that goes stale against its own file.

---

## 1. What exists, and what you will reuse

Read these before writing anything. Signatures are given so you do not have to re-derive them.

### 1.1 The engine half — complete, do not modify

```csharp
// src/Core/Devices/ComponentModelFactory.cs
//   "wBond" is in _parameterizedTypes and dispatches to CreateWBondModel(parameters).
//   Requires a `File` parameter naming the .wBond; throws FileNotFoundException by name if absent.

// src/Core/Elaboration/Elaborator.cs
//   inst.Reference == "wBond" → ResolveWBondParameters(inst, parentScope)
//   `File` is stored VERBATIM and resolved against the workspace root, exactly as SnP's is.

// src/Core/Devices/WBondModel.cs
public override int PortCount            // 2M + 1
public override IReadOnlyList<string> …  // "G1.i", "G1.o", "G2.i", "G2.o", …, "REF"
//   Nodes[2k] / Nodes[2k+1] are array k's input and output. REF DECLARES the return path; it is not
//   stamped. The model REFUSES (by name) when the ground plane is disabled and no array is nominated
//   as the return — WB20 / R-wbb-4.
```

### 1.2 The symbol generator — complete, and its contract is the wiring

```csharp
// src/Ui/Schematic/WBondSymbolGenerator.cs   (internal static)
internal const int ContentVersion = 1;
internal static string  ContentKey(WBondDesign design);   // "wbond-v1:G1|G2|D1"
internal static Symbol? Build(WBondDesign design);        // null when the design declares no arrays
internal static string  Describe(WBondDesign design);     // "3 arrays · 47 wires · 128.4 mm total"
```

Pin numbers are 1-based and **follow the model's terminal order exactly**: `2k+1` = array *k*'s input,
`2k+2` = its output, `2m+1` = `REF`. That is not presentation — it is the wiring.

### 1.3 The schematic placement machinery you will extend

```csharp
// src/Ui/Schematic/SchematicModel.cs        enum SymbolKind          (no WBond member — add one)
// src/Ui/Schematic/ComponentTypeRegistry.cs
public sealed record ComponentTypeInfo(string DisplayName, string InstancePrefix, …);
public static string DisplayName(SymbolKind kind, int portCount = 2);
public static string EngineReference(SymbolKind kind);          // must return "wBond"
public static IReadOnlyList<EditableParameter> DefaultParameters(SymbolKind kind);
public static SymbolKind? TryParseCode(string code);

// src/Ui/Schematic/EditableSchematic.cs
public int PortCount { get; set; }                              // variadic kinds (SnP, SDD, ZPort)
public string? CellRef { get; set; }                            // a symbol from elsewhere
public IReadOnlyList<(string Name, double X, double Y)> PortDefsOf(EditableComponent c);

// src/Ui/Schematic/CellSymbolResolver.cs
public static CellSymbolResolution Resolve(string cellRef, string baseDir);
//   Checks PdkKitRegistry.IsKitRef(cellRef) FIRST and never falls through to the path branch.
//   Three states, kept distinct: Resolved / NotFound / PrimaryMissing.
public static void Invalidate(string cellAbsDir);
public static void InvalidateAll();

// src/Ui/Schematic/NetExtractor.cs
//   GetEffectivePortDefs + BuildCellRefResolutions — terminal ORDER is the contract (see §2).
```

### 1.4 The wBond side

`WBondDesign.Arrays` / `.AssemblyRef` / `.EmbeddedGeometryJson`, `WBondIo.ReadFile/WriteFile`,
`WBondDocument.Open(path, scratchDir)`, `WBondGeometryEmbedding.Unpack(json, scratchDir)` (returns
`{ Root, BaseDir }` — real cell folders, because `CellLayoutResolver` requires `Directory.Exists`),
`CellFolder.CreateCellFolder(parentDir, name)`.

---

## 2. The traps — read this section twice

### R-wbb2-1 (THE BIG ONE) — a symbol whose shape comes from a FILE has no existing home

Three mechanisms in this codebase produce a component's symbol. **None of them fits, and the reason
each one does not fit is the specification for what you build.**

| mechanism | how the shape is decided | why it does not fit wBond |
|---|---|---|
| **Built-in `SymbolKind`** (R, C, MLIN) | fixed artwork in `BuiltInSymbols` | a wBond has no fixed pin count |
| **Variadic `SymbolKind` + `PortCount`** (SnP, SDD, ZPort) | the USER sets `NumPorts` | the pin count is a property of the FILE, not a user choice — and the pins carry NAMES (`G1.i`, `D2.o`) that this route has nowhere to put |
| **`CellRef` → cell folder** (a user's own cell, a PDK part) | a `.csym` on disk, resolved by path | there is no `.csym`; writing one makes a second copy of the array list that goes stale the moment the `.wBond` is edited |

**Choose a fourth, and state the criterion you chose it on.** The recommendation is an **in-memory
registry keyed by the referenced design**, resolved through the seam `CellSymbolResolver` already
has — the same shape `PdkKitRegistry` uses, checked before the path branch, for the same reason (the
reference is not a path and must not be reported as a bad one). But the choice is yours to make and
to defend; what the brief requires is that the answer survives this question:

> **When the referenced `.wBond` changes — edited in an open wBond editor, or replaced on disk — what
> happens to a schematic that has one placed?**

A mechanism that answers "the symbol silently keeps its old pins" is the wrong mechanism. Reordering
arrays produces a symbol with **correctly-named pins wired to the wrong nets** — silent, electrically
wrong, and the exact failure `project-brief-L5-followups` already records for MTee, where a generator
fix was invisible because stale on-disk cells survived it. `WBondSymbolGenerator.ContentVersion` and
`ContentKey` exist for this; **something has to actually read them.**

### R-wbb2-2 — terminal ORDER is the contract, and a transposition is a plausible wrong answer

`WBondModel` reads `Nodes[2k]` and `Nodes[2k+1]` as array *k*'s input and output, and `Nodes[2m]` as
`REF`. `NetExtractor` builds `NetBindings[]` by walking the resolved port defs **in order**. A symbol
whose pin numbers do not match that order produces a circuit that solves, converges, and reports the
wrong array's inductance on the wrong net.

- Walk the resolved symbol's pins **by pin number**, not by list position, unless you have proven the
  two coincide.
- **`REF` is a declaration, not a stamped connection** (WB20). Decide explicitly whether it appears in
  `NetBindings` at all, and make `WBondModel`'s own 2M+1 `PortCount` and the extractor agree. Do not
  discover this at run time from a node-count mismatch.
- Gate it with an **oracle, not a shape check**: build a two-array wBond, place it, wire array 1 and
  array 2 to *different* nets, extract, and assert the emitted `NetBindings` name the nets in the
  order `WBondModel` will read them. A test that only counts terminals passes a transposition.

### R-wbb2-3 — `File` is resolved against the WORKSPACE ROOT, and the schematic is not the workspace root

`Elaborator.ResolveWBondParameters` stores `File` verbatim and resolves it against the workspace root,
**exactly as SnP's is** — deliberately, and it is already tested. The placement path must therefore
write a `File` value that resolves that way, which is *not* the same base the schematic's own
`CellRef` values resolve against.

This is the same class of bug as `CnlWriter` writing an SnP path unquoted (already logged in
`src/Ui/CLAUDE.md`): it never bit because the picker always produced an absolute path, and
back-annotation was the first thing to write a relative one. **Test a relative `File` and an absolute
one, from a schematic that is NOT at the workspace root.**

### R-wbb2-4 — the coupling audit (WB30) is load-bearing in v1 and must fire from the placement path

§7/D8 allows more than one wBond in a design and makes the **audit** carry the whole safety burden:
coupling between separate wBond components is not modelled in v1, and the only remedy is a manual
merge. `WBondCouplingAudit` already exists (`src/Core/Devices/WBondCouplingAudit.cs`) and WB-B's own
gate fired it on a constructed two-wBond adjacency.

**Placing a second wBond is the moment that becomes reachable by an ordinary user for the first
time.** Whatever surfaces the audit today, confirm it still fires when the second component arrives by
placement rather than by hand-authored netlist — and that the message names the manual remedy.

---

## 3. Milestones

Each is independently completable and independently gated. **Do M1 first and completely** — it is
where R-wbb2-1 is decided, and M2–M4 all sit on top of that decision.

### M1 — the component exists and can be placed

- `SymbolKind.WBond`, a `ComponentTypeRegistry` entry (`EngineReference` → `"wBond"`,
  `TryParseCode("WBOND")`, `DefaultParameters` carrying at least `File`), and whatever
  `CellSymbolResolver`-side seam R-wbb2-1 settles on.
- A palette tile. Category: **`ComponentCategory.Other`** unless you can argue for a better one —
  do not invent a new category for one component.
- Placement must handle **a wBond with no arrays** (`WBondSymbolGenerator.Build` returns null): it is
  not placeable, and saying so by name beats placing something with no pins.

**Gate M1:** the component appears in the palette; placing it with a valid `File` produces a symbol
with 2M+1 pins named `G1.i`/`G1.o`/…/`REF` in array order; a `.wBond` with no arrays is refused **by
name**; a missing or unreadable `File` draws the existing Not-Found placeholder and reports, never
throws.

### M2 — the symbol tracks its file

This is R-wbb2-1's real gate and the reason M1 is not enough on its own.

**Gate M2:** editing the referenced `.wBond`'s array list (add an array, rename one, **reorder two**)
and saving updates the placed symbol in an open schematic without reopening it; a schematic saved
with the old array list and reopened after the change shows the NEW pins; and — the one that catches
the silent failure — **after reordering two arrays, the wire that was on `G1.i` is still on `G1.i`, or
the change is reported.** Deciding that a reorder legitimately breaks existing wiring is an acceptable
answer; letting it re-point silently is not.

### M3 — §9.2 route 2: add a `.wBond`'s wires to an existing schematic

The user has a schematic open and a `.wBond` on disk; one action places the component wired to
nothing, with `File` already pointing at it.

**Gate M3:** the route is reachable from a real entry point (project-tree double-click on a `.wBond`
offering it, a File-menu item, or palette drag-drop — say which and why); the placed instance extracts
to a `TestBench` whose `NetBindings` are in `WBondModel`'s own terminal order (R-wbb2-2's oracle); a
2-array wBond runs end to end through `Cli hb` or `sparam`; and a `parametric_sweep` over
`X1.G1.LoopHeight` still works from a PLACED component, not only a hand-authored netlist (WB21 — this
is the feature a PA designer actually uses the tool for).

### M4 — §9.2 route 3: add wires **and** geometry as a new cell

"Someone sent me a package model." The embedded `.clay` becomes the cell's layout view; the wBond
component becomes its schematic view.

- Reuse `WBondGeometryEmbedding.Unpack` and `CellFolder.CreateCellFolder`. **Do not write a second
  unpacker** — WB-C's own note records why `Unpack` writes real cell folders rather than an in-memory
  overlay (`CellLayoutResolver.Resolve` requires `Directory.Exists`).
- A `.wBond` carrying **no** embedded geometry is route 2, not a failure. Say so rather than creating
  an empty layout view.
- WB35 applies: an unresolvable reference is **reported and offered a re-point**, never silently
  substituted.

**Gate M4:** a `.wBond` saved with embedded geometry becomes a cell with a working layout view and a
schematic view holding the wBond instance; the new cell is placeable in another schematic like any
other; a `.wBond` with no embedded geometry is diverted to route 2 with a message; the cell's own
`.ccell` names its primary views.

---

## 4. Guardrails

- **Do not modify the physics, the stamp, or `WBondModel`'s terminal contract.** If the extractor and
  the model disagree about `REF`, the extractor is what changes — and say so in the completion note.
- **Do not bump `.wBond`, `.csch`, `.ccell` or `.cws` format versions.** Anything new is an additive
  nullable field written only when set.
- **Do not write a second symbol generator, a second unpacker, or a second cell-creation path.**
- `src/Engine` and `RfCore` are untouched. `tests/Firewall.Tests` must stay green.
- **No GDSII work**, unchanged from WB-C's and WB-D's own rulings.
- WB-E (the standalone app) is **not** this phase, even where the two touch.

---

## 5. Open questions the owner should settle before or during M1

State the answer you adopted in the completion note either way; do not let a guess become a silent
convention.

1. **What is `File` relative to, in the value the placement path writes?** The workspace root is what
   the elaborator resolves against (R-wbb2-3). A relative value is portable and an absolute one is
   not — but a `.wBond` outside the workspace has no relative form. The `.clay`/`TechRef` and
   Known-File precedents both point at "relative inside, absolute outside, and say which".
2. **Does `REF` appear in `NetBindings`?** The model declares 2M+1 terminals and stamps 2M. The two
   honest answers are "yes, and the model ignores the last one" and "no, and `PortCount` means
   something different from the symbol's pin count". Pick one and make both sides state it.
3. **What happens to existing wiring when arrays are REORDERED?** See M2's gate. Re-pointing silently
   is the one unacceptable answer.
4. **One tile, or one per `.wBond`?** A wBond is more like SnP (one tile, a file parameter) than like
   a PDK part (one tile per part). The recommendation is **one tile**; a workspace's `.wBond` files
   appearing as separate palette entries is a bigger idea that should be its own decision.

---

## 6. Known gaps that are NOT this phase

- **`CouplingDomain`** — v2 by O-3. The audit (R-wbb2-4) carries v1.
- **WB-E, the standalone app** — §13's own next row: third entry point, build config, packaging,
  Touchstone export.
- **WB-F, kernel W** — downstream of `mom-wirebond-kernel.md` LW1; nothing here depends on it.
- **A `.wasm` editor** — deferred by WB-D §5 question 4, unchanged.
- **Tail/stitch land length and reverse-bond allowance** as assembly rules — WB-D reported both as
  requiring a `Wire` MODEL change, not a language one. If this phase touches `Wire` for any other
  reason, note whether that changes.

---

## 7. Completion note — what to record

Follow the house convention in `src/Ui/CLAUDE.md`: what was built, **what was found**, what was
deliberately not built and why, the gate numbers, and an explicit "not interactively verified" list.

Specifically, record: **which of R-wbb2-1's four mechanisms you chose and what you chose it on**; the
answer to every §5 question; whether the extractor and `WBondModel` agreed about `REF` on the first
attempt or had to be reconciled; and — if a reordered array list turned out to re-point wiring
silently before you fixed it — say so plainly, because that is the failure this whole brief is built
around and a second sighting of it is worth more than the fix.
