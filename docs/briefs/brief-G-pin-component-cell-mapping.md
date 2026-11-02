# Brief G — Pin component (interface terminal) + cell-port mapping + v1 differential

**Scope:** add the **`Pin`** component — the hierarchical interface terminal that maps a schematic
net to a cell interface port and the symbol pin. Pure connectivity, no electrical model. Plus the
schematic→cell port-declaration path and v1 differential interface ports. **Read
`docs/design/ports-pins-and-terms.md` first.** Do **not** start this until Brief F has landed
(it depends on the Term/Port separation being done).

**Firewall:** UI registry/symbols are framework-free; `Cell`, `Elaborator`, `ElaboratedNetlist`,
`ComponentModelFactory` are `src/Core` (no Avalonia).

---

## Read first (real names)

- `docs/design/ports-pins-and-terms.md` — spec. Pin = one terminal, `Num` (+ optional `Name`),
  inert, resolves to a subckt terminal; interface mapping is **explicit via Pin `Num`**, never
  inferred from order; differential interface port = a grouped `+`/`−` Pin pair (v1).
- `src/Ui/Schematic/ComponentTypeRegistry.cs` — add `SymbolKind.Pin`. `EngineReference`,
  `DefaultParameters`, `TryParseCode`, `Category Terminals`.
- `src/Ui/Schematic/BuiltInSymbols.cs` — add `BuildPin()` + `_pin` + switch arm. `SymbolPortDefs`
  → one pin for `Pin`.
- `SymbolKind` enum — add `Pin`.
- `src/Core/Design/` — the **`Cell`** model. Grep `class Cell`: it has `Ports` (`List<string>` of
  port NAMES), `Instances`, `Parameters`, `Variables`. **`Cell.Ports` is the interface-port list.**
- `src/Core/Elaboration/Elaborator.cs` — `FlattenInstances`: sub-cell instantiation already maps
  `subCell.Ports[i]` ↔ `inst.NetBindings[i]` positionally and threads `parentNetMap`
  (port-name → parent net). `ComponentModelFactory.IsPrimitive(inst.Reference)` splits primitives
  from sub-cells. **This is the binding mechanism you plug into.**
- `src/Core/Devices/ComponentModelFactory.cs` — `IsPrimitive` / `TryCreate`. A Pin must be neither a
  stamping primitive nor a sub-cell — decide how the elaborator treats it (Layer 3).
- `src/Ui/Schematic/NetExtractor.cs` — schematic → `Instance`s / nets. This is where the cell's
  primary schematic's Pins must become the cell's declared ports. Grep how it currently produces a
  cell's port list (if at all) — TODAY a cell's `Ports` may be populated from the symbol pins or
  left implicit; you are making **schematic Pins** the authority for the port net binding.
- `src/Ui/Schematic/CellSymbolResolver.cs`, `CellPersistence.cs` (`CcellFile.NumPorts` from E2),
  `EditableSymbol.ExternalPortCount` + the symbol editor's unmapped-port panel — the conformance
  surface (cell `NumPorts` vs symbol pins vs schematic Pins).
- `src/Ui/Schematic/SchematicModel.cs` / `EditableSchematic.cs` — how components carry instance
  name + params (for the Pin's `Num`/`Name`).

---

## Spine (do-not-violate)

1. A **Pin has no electrical model** and **never** appears in the flattened netlist as a component
   or as a `Port:` object. It is resolved at elaboration into the cell's terminal net binding.
2. **Interface mapping is explicit by `Num`.** Cell interface port `k` ↔ schematic `Pin(Num=k)` ↔
   symbol pin `k`. Never positional-by-placement, never net-name matching.
3. The **cell owns the count** (`CcellFile.NumPorts`, E2). Schematic Pins and symbol pins conform;
   mismatches surface in the existing unmapped-port panel. No silent drift.
4. `Cell.Ports` ordering = ascending Pin `Num` (1-based). The elaborator's positional
   `subCell.Ports[i]` binding must line up with this ordering.
5. v1 differential interface port = two Pins grouped `+`/`−` (two symbol pins). No mixed-mode.

---

## Layer 1 — The `Pin` component (registry + symbol)

- `SymbolKind.Pin`. Registry: `new("Pin", "Pin", Category: Terminals, SearchTerms: ["Pin","P","io",
  "terminal","interface","port"], IsCommon: true)`. `TryParseCode`: `"PIN"` → `SymbolKind.Pin`.
- `EngineReference(SymbolKind.Pin)`: a Pin is not a netlisted primitive; give it a sentinel (e.g.
  `"Pin"`) that `ComponentModelFactory.IsPrimitive` returns **false** for, OR is explicitly handled/
  skipped at elaboration (Layer 3). State your choice.
- `DefaultParameters(Pin)`: `[ new("Num","<next>","",true,None), new("Name","","",false,None) ]`.
  `Num` integer, unique within the schematic, auto-assigned next-free on placement (mirror Brief F's
  Term `Num` wiring). `Name` optional (defaults to e.g. `P{Num}`).
- `BuildPin()` art: a simple IO-terminal glyph — a short lead to one pin `(0,-200)` (name from
  `Name`/`Num`) plus a small open square/arrow “flag” marking it as an interface terminal. One pin
  only. `SymbolPortDefs.For(Pin)` → one pin at the lead tip.

**Gate 1:** A Pin can be placed and wired (one connection). It shows `Num` (and `Name` if set).

---

## Layer 2 — Cell declares its ports from the primary schematic's Pins

When a cell's primary schematic is turned into the `Cell` model (the elaboration/extraction path),
**populate `Cell.Ports` from the Pin components**, ordered by `Num`:
- Collect every `Pin` in the schematic, sort by `Num`, and set `Cell.Ports = [Pin.Name (or "P{Num}")
  for each]`. The Pin's **net** is the cell-internal net realizing that port.
- The mapping the elaborator needs: cell port `k` (name `Cell.Ports[k]`) ↔ the schematic net the
  `Pin(Num=k+1)` is attached to. Record this so flattening can merge the parent net into that
  internal net (the `parentNetMap` already carries port-name → parent-net; you supply the
  port-name → internal-net side).
- Find the schematic→Cell builder (grep `new Cell(` / where `Cell.Ports` is assigned today — likely
  in `NetExtractor` or a cell-build path invoked by `CellPersistence`/elaboration). Make Pins the
  source of `Ports`.

**Gate 2:** A cell whose primary schematic has `Pin(Num=1,Name="in")` and `Pin(Num=2,Name="out")`
yields `Cell.Ports = ["in","out"]`. Instantiating that cell in a parent and wiring two nets to its
symbol pins connects the parent nets to the `in`/`out` internal nets (verify via a flattened netlist
or a small elaboration test).

---

## Layer 3 — Elaboration treats Pin as a connectivity marker (not a component)

In `Elaborator.FlattenInstances`:
- A `Pin` instance must **not** be emitted as an `ElaboratedComponent` and must **not** create a
  model. When flattening a cell's internals, a Pin merely identifies which internal net is port `k`;
  the actual parent↔port net merge is already handled by `parentNetMap` + the positional
  `subCell.Ports[i]` binding.
- Implement by either (a) `ComponentModelFactory.IsPrimitive("Pin") == false` AND not a sub-cell →
  add an explicit "skip Pin / it's a terminal marker" branch in `FlattenInstances`; or (b) treat Pin
  as a primitive whose model is a no-op (no `Stamp`) — **but (a) is cleaner** (a Pin shouldn't be a
  component at all in the flat netlist). Recommend (a); state what you did.
- Ensure a Pin at the **top testbench** (not inside a cell) is harmless (a top-level Pin has no
  parent to bind to — treat as an unconnected label / no-op, optionally warn).

**Gate 3:** A flattened netlist of a design instantiating a Pin-bearing cell contains **no** Pin
components and **no** `Port:` objects from Pins; the cell's internal nets are correctly merged with
the parent nets. S-param of the parent (with its own Terms) is unaffected by the presence of Pins.

---

## Layer 4 — Conformance: cell `NumPorts` ↔ schematic Pins ↔ symbol pins

- The cell owns `NumPorts` (E2). The primary schematic should have exactly `NumPorts` Pins
  (`Num` = 1..N, unique) and the primary symbol exactly `NumPorts` pins.
- Surface mismatches through the **existing unmapped-port panel** (`ExternalPortCount`, fed from the
  cell per E2's `TryCellPortCount`): too few/many Pins, gaps in `Num`, duplicate `Num`. Do not throw
  — warn and let the user fix. (Reuse the panel; don't build a new warning system.)

**Gate 4:** A cell with `NumPorts=2` but only one schematic Pin shows the unmapped-port warning;
adding the second Pin (Num=2) clears it.

---

## Layer 5 — v1 differential interface ports (grouped `+`/`−` Pin pair)

- A differential interface port = two Pins tagged as a `+`/`−` pair sharing one logical port. v1
  representation (pick the simplest that round-trips; state it): e.g. a Pin gains an optional
  `Polarity` (`None`/`Plus`/`Minus`) and a shared `Num`, so `Pin(Num=1,Plus)` + `Pin(Num=1,Minus)`
  form differential port 1, exposing two symbol pins. `Cell.Ports` then lists both half-terminals
  (e.g. `"1+"`,`"1-"`) in a defined order, or a single differential entry — choose and document.
- The symbol shows two pins for the differential port. No mixed-mode S-parameter math here (v2).
- A differential port pairs naturally with a differential **Term** (Brief F: `+`/`−` across the two
  half-nets) in the testbench.

**Gate 5:** A cell can declare one single-ended port and one differential port; the symbol exposes
1 + 2 = 3 pins; instantiation binds all three nets; the flattened netlist merges them correctly.

---

## Acceptance
- `Pin` component exists (inert, `Num`/`Name`); never appears as a netlist component or `Port:`. ✅
- Cell ports are declared by the primary schematic's Pins, ordered by `Num`, mapped explicitly. ✅
- Instantiation merges parent nets into the cell's port nets via existing `parentNetMap`. ✅
- Cell `NumPorts` vs Pins vs symbol pins conformance shows in the unmapped-port panel. ✅
- v1 differential interface ports work (grouped `+`/`−` Pins, two symbol pins). ✅

## Guardrails
- A Pin is connectivity only — no `Stamp`, no model, never a `Port:` object. If you find yourself
  giving Pin an electrical effect, stop.
- Don't disturb the Term/`Port:` path from Brief F.
- Reuse the unmapped-port panel for conformance; don't build a parallel validator.
- Keep `Cell`/`Elaborator`/registry framework-free.
- Minimal diff; list files touched.

## Scope fence (NOT here)
- No Term work (Brief F). No scoping rule / engine stamping / linter (Brief H).
- No mixed-mode S-parameters; no dedicated differential-Pin primitive (v2).

## Exit / report
State: where `Cell.Ports` is built and how Pins now populate it; how the elaborator skips Pins
(option a/b); the differential representation chosen; the conformance hook; and confirmation the
5 gates run mentally against the final code.
