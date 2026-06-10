# Phase 6e — Step 1: the net extractor core (headless) (Claude Code / Sonnet)

The heart of net extraction: a **headless, deterministic, framework-free** pass that turns a
`SchematicEditModel` into the engine's **Design model** (`TestBench` with `Instance`s whose `NetBindings` are
in terminal order). Reuses the existing `ComputeConnectivityGeometry` for connectivity — does **not** re-derive
it. **This brief is ONLY step 1** — the extractor core + its unit tests. **No `.cnl` text emission, no
oracle, no engine run, no `netlist.cnl` write, no UI, no Units normalization, no analysis authoring** — those
are steps 2+. Read `net-extraction-and-run.md` §2 first. Sub-gated; **report and stop between every layer.**
Firewall green.

> Read first: `docs/design/net-extraction-and-run.md` §1 (boundary), §2 (the algorithm — union-find, net
> naming incl. **same-name net labels union**, terminal-order emission), §2.1.6 (label union — the owner's
> decision). Context code: `src/Ui/Schematic/EditableSchematic.cs` (`SchematicEditModel`,
> `ComputeConnectivityGeometry` — **reuse for unions**; `EditableComponent` w/ `Symbol`/`CellRef`/`Parameters`/
> `PortCount`/`GetPortWorldCoord`; `SymbolPortDefs.For`; `EditableWire`; `EditableDot`; `EditableNetLabel`),
> `src/Core/Design/TestBench.cs` (`TestBench` — the **target**: `Instances`, `GlobalVariables`), `src/Core/
> Design/Instance.cs` (`Instance`: `InstanceName`, `Reference`, **`NetBindings` (ordered = terminal order)**,
> `Overrides`, **`RefNetBinding`** for N-port shared reference), `src/Core/Design/Cell.cs`,
> `ParameterDeclaration.cs`/`Variable.cs` (`ParameterAssignment` for overrides), `src/Ui/Schematic/
> ComponentTypeRegistry.cs` (`SymbolKind` → engine type string "R"/"C"/"L"/"Port"/… ; the type the `.cnl`
> `Reference` uses), `src/Core/Netlist/CnlReader.cs` (the *consumer* — read it to match terminal-order/
> ref-node conventions, esp. the N-or-N+1 SnP rule). Design docs win on any conflict.

## The spine (do not violate)
- **`SchematicEditModel → TestBench` (Design model), headless + framework-free.** The extractor lives where it
  can be unit-tested with hand-built edit models, references **no Avalonia/Skia**. (The `SchematicEditModel`
  and connectivity geometry are already framework-free.)
- **Reuse `ComputeConnectivityGeometry` for connectivity** — its vertex hash, auto-dots (T-junctions), and
  `IsCrossingAtDot` (dot-gated crossings) are the single source. The extractor's union-find **consumes** these
  outputs; it does NOT re-implement T-junction or crossing logic. (The "6e extraction note" comments in that
  file are the normative spec.)
- **Connection = exact on-`P` equality** (`grid-and-connectivity.md` R1–R7) — union by integer `P`-cell, not
  tolerance.
- **Terminal order is the contract** — each `Instance.NetBindings` lists nets in the **symbol's terminal
  order** (1-based user-facing → positional in the list). Walk terminals in order; do NOT transpose or
  off-by-one. This is the bug-prone seam the oracle (step 2) will guard.
- **Same-name net labels UNION** (§2.1.6, owner's decision) — all nets carrying a label of the same name are
  one net, including across physically-disjoint wires.
- **Scope fence (step 1):** `SchematicEditModel → TestBench` + unit tests. NO `.cnl` text, NO oracle, NO
  engine, NO file write, NO UI, NO Units glyph normalization, NO analysis authoring. (Analyses/measurements:
  carry through any that already exist on the model as `RawDirective`s if present, but step 1 does NOT add an
  authoring path — the schematic likely has none yet, which is fine.)

---

## LAYER 1 — union-find over connection points → nets

A framework-free `NetExtractor` (e.g. `src/Ui/Schematic/NetExtractor.cs`, or a `src/Core`-adjacent headless
location — keep it where `SchematicEditModel` lives so it stays framework-free and testable):
1. **Seed union-find** with every distinct on-`P` connection point: each component's pins-in-world
   (`GetPortWorldCoord` for built-ins; resolved `.csym` pins for cell-refs), and each wire vertex. Key points
   by integer `P`-cell (the same `QuantKey`/`GridSize` quantization the connectivity pass uses).
2. **Apply the geometric unions from `ComputeConnectivityGeometry`:** wires union their own vertices;
   coincident points (same `P`-cell) union; auto-dots (T-junctions) union the incident ends; user dots on
   real crossings (`IsCrossingAtDot`) union the crossing wires. **Call the existing method and consume its
   outputs** — don't recompute.
3. **Apply the label-name union (§2.1.6):** group `EditableNetLabel`s by (case-sensitive) name; for each
   group, union the nets of the wires/points the labels sit on. (A label sits on a wire when its position is
   on that wire — reuse the on-wire/point test; a label not on any wire is a no-op / reportable later.)
4. Result: a map from each connection point (`P`-cell) → a **net id** (union-find root).

**Layer 1 gate:** headless tests — two pins on the same wire share a net; two pins on different wires don't; a
T-junction unions three ends; an un-dotted crossing stays two nets, a dotted one unions; two disjoint wires
labeled `vdd` become one net. Report.

---

## LAYER 2 — name the nets

Assign a stable name to each net (union-find root):
- **Ground:** any net containing a `Ground` component's pin → the engine reference (`"0"` / `gnd` — match what
  `CnlReader`/`Elaborator` expect for node 0). If multiple grounds, they're all the one ground net.
- **Net-label name:** a net carrying a label → the label text. (Post-union, a net has one name; if a single
  net carries **two different** label names, record a **conflict** — expose it for step-2/run Messages, pick
  deterministically by stable order, don't fail.)
- **Port name:** a net on a `Port`/`Term` component → the port's net name as the design expects (confirm the
  convention against `CnlReader`/how Port instances bind).
- **Auto-name** all remaining nets `n1`, `n2`, … in a **deterministic stable order** (e.g. by the
  lowest-ordered component+pin in the net), so re-extracting an unchanged schematic yields identical names
  (matters for the oracle + clean `netlist.cnl` diffs).

**Layer 2 gate:** headless tests — ground net is `0`/`gnd`; a labeled net takes the label name; auto-names are
stable across re-extraction of the same model; a two-different-labels-on-one-net conflict is recorded. Report.

---

## LAYER 3 — emit instances in terminal order → `TestBench`

Build the `TestBench`:
1. For each `EditableComponent`, create an `Instance`:
   - **`Reference`** = the engine type string for its `SymbolKind` (via `ComponentTypeRegistry`) — or the cell
     name for a cell-ref instance (cell-instance emission may be minimal/deferred per §7; confirm scope — for
     step 1, built-in primitives are the priority).
   - **`InstanceName`** = the component's `InstanceName`.
   - **`NetBindings`** = the net name at **each terminal, in the symbol's terminal order** — walk
     `SymbolPortDefs.For(Symbol, PortCount)` (or the resolved `.csym` pins) in order, look up each pin's
     `P`-cell → net name. **This ordering is the contract** — terminal *k* → list position *k*.
   - **`Overrides`** = the component's `Parameters` as `ParameterAssignment`s (name + expression string; units
     carried as authored — do NOT normalize glyphs here, that's step 3).
   - **`RefNetBinding`** for N-port components (ZPort/Sdd) — the shared reference pin's net, per the N-or-N+1
     rule `CnlReader` uses (the `ref` pin in `SymbolPortDefs.GeneratePorts`). Match `CnlReader`'s handling.
2. **Disabled components** (`DisableState.Open`/`Short`): honor the disable model — Open omits/opens the
   terminals, Short unions its terminals into one net. (If the disable emission is fiddly, implement Open/Short
   minimally and note any deferral — but the union effect of Short must be correct since it changes nets.)
3. **Global variables:** carry any design `Var`s into `TestBench.GlobalVariables`. (Where they live on the
   schematic today may be nothing yet — carry through what exists; don't invent an authoring path.)
4. Return the `TestBench`.

**Layer 3 gate:** headless tests — a small schematic (e.g. R + C + Port + Ground wired up) extracts to a
`TestBench` whose `Instance`s have the right `Reference`, `InstanceName`, params, and **`NetBindings` in
terminal order**; a 3-terminal device (FET d/g/s) emits its nets in d/g/s order (not transposed); an N-port
ZPort sets `RefNetBinding`. Report.

## Acceptance (step 1)
1. A framework-free `NetExtractor` turns a `SchematicEditModel` into a `TestBench` (Design model), reusing
   `ComputeConnectivityGeometry` for connectivity (no re-derivation).
2. Nets are unioned by exact on-`P` coincidence + wires + T-junctions + dotted crossings + **same-name labels**
   (§2.1.6), and named (ground/label/port/auto-stable) with conflicts recorded.
3. Instances emit `NetBindings` in **symbol terminal order** (FET d/g/s correct; ZPort `RefNetBinding` set);
   params carried as authored (no glyph normalization yet); Open/Short disable honored.
4. `dotnet build`/`dotnet test` green; firewall green (extractor framework-free, no Avalonia/Skia); **no `.cnl`
   text, no oracle, no engine, no file write, no UI, no Units normalization, no analysis authoring**
   (steps 2+); nothing else regresses.

## Guardrails
- **Headless + framework-free**; unit-tested with hand-built `SchematicEditModel`s.
- **Reuse `ComputeConnectivityGeometry`** — consume its unions/auto-dots/crossing predicate; don't
  re-implement T-junction or crossing logic.
- **Terminal order is the contract** — net per terminal, in symbol order, positional; no transpose/off-by-one.
- **Same-name labels union** (§2.1.6); record two-different-names-on-one-net conflicts (don't fail).
- **No glyph normalization here** (step 3); carry param units as authored.
- **Scope fence:** `SchematicEditModel → TestBench` + tests only.
- Sub-gate the three layers; report and stop between each; don't run the full suite into the output limit.
- Update `net-extraction-and-run.md` §6 status (step 1 done) and `src/Ui/CLAUDE.md` (the headless NetExtractor:
  reuses connectivity geometry, terminal-order contract, label-union).

*Exit: a headless, framework-free extractor turns a drawn schematic into the engine's `TestBench` Design model
— connectivity by exact on-`P` union (+ T-junctions, dotted crossings, same-name-label union), instances in
terminal order — the core that `.cnl` emission + the oracle (step 2) and the in-app run (step 5) build on.*
