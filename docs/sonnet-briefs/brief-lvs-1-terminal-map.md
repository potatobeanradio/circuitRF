# Brief 1 — the terminal map: which layout pin is which schematic port

**Series:** [LVS](brief-lvs-0-overview.md) · **Tag:** `R-lvs1-n` · **Design note:** [`lvs.md`](../design/lvs.md) §4.2
**Area:** `src/Design/Layout/TerminalMap.cs` (new), `src/Design/Cells/CellPersistence.cs`,
`src/Design/Layout/ComponentImport.cs`, `src/Design/Cells/CellCreate.cs`,
`src/Design/Layout/PCells/`, `src/Cli/Check.cs`, `src/Ui/ViewModels/`
**Depends on:** — · **Blocks:** 3, 4, 5

---

## 0. What this brief delivers

A cell says which of its layout pins is which of its schematic ports, in its own `.ccell`, and
`check` says so when it does not. **No comparison is built here** — this brief ships as a new
`check` rule on its own account, and it is the thing every later brief needs.

```
.ccell "Terminals":  Port 2  ──►  LayoutPin "D"
                       │                 │
   SymbolPin.PortIndex 2          LayoutView.Pins[i].Name
   Port Num=2 in the .csch
```

## 1. Why this is the one genuine blocker

The correspondence exists today in three different strengths and only one of them is a guarantee.

| Cell origin | What ties layout pin to schematic port | Strength |
|---|---|---|
| Imported (`ComponentImport`) | `ComponentTerminals.Build` — one numbering decided once for both views | **Guaranteed by construction** |
| PCell | `pcell-contract.md` R3: the name *must* match the symbol's pin | **Documented, unenforced** |
| Hand-drawn | nothing | **Absent** |

`LayoutPin.Name` is explicitly allowed to be empty — *"an unnamed pin is still a real connection
point, just one the user must identify"* — and the schematic orders a cell's ports by the `Num`
parameter on its `Port` components (`NetExtractor.BuildCellPorts`), which nothing relates to the
order pins were appended to a `.clay`. **Position is not a correspondence either**: a symbol's pin
positions and a land pattern's pad positions have no reason to agree and usually do not.

---

## 2. `R-lvs1-1` — the `.ccell` block

**`R-lvs1-1a`** `CcellFile` gains `List<CcellTerminal>? Terminals`.

```json
"Terminals": [
  { "Port": 1, "Name": "G", "LayoutPin": "G" },
  { "Port": 2, "Name": "D", "LayoutPin": "D" },
  { "Port": 3, "Name": "S", "LayoutPin": ["S1", "S2"] }
]
```

**`R-lvs1-1b`** `Port` is 1-based and is the number both sides already use — `SymbolPin.PortIndex`
and the `Port Num=` parameter. It is not re-derived and not renumbered.

**`R-lvs1-1c`** `LayoutPin` names an entry in the **primary** `.clay`'s `Pins` by name, or is a
**list** where several pins are one terminal. The list form is not new semantics: it is the
`GND@1`/`GND@2` case `ComponentTerminals` already understands, and a FET's two source pads is the
same shape.

**`R-lvs1-1d`** `Name` is the terminal's own name, for reports. It may differ from `LayoutPin` and
usually does not. Empty is legal.

**`R-lvs1-1e`** **Additive, nullable, omitted when null, no `FormatVersion` bump** — the convention
every field added to `.ccell` and `.clay` already follows. Every existing cell re-serializes byte
for byte. A cell with no block is not marked as suspect, is not warned about, and is not migrated:
see `R-lvs1-3`.

**`R-lvs1-1f`** A `LayoutPin` naming a pin the primary `.clay` does not have, a `Port` outside
`1..NumPorts`, a duplicate `Port`, or one layout pin claimed by two terminals — each is a **`check`
error** (§4). Nothing here throws and nothing silently drops a row; an unreadable block makes the
cell's map **absent**, which is a defined state.

---

## 3. `R-lvs1-2` — `TerminalMap`, the one place the question is answered

A new `src/Design/Layout/TerminalMap.cs`, framework-free, with `CellPins`' character — total, never
throws, caches on the resolved view reference.

```csharp
public sealed record Terminal(int Port, string Name, IReadOnlyList<string> LayoutPins);

public enum TerminalMapOrigin { Declared, ImportTable, ByName, ByOrder, None }

public sealed record TerminalMapResult(
    IReadOnlyList<Terminal> Terminals,
    TerminalMapOrigin Origin,
    IReadOnlyList<string> Notes);

public static TerminalMapResult Resolve(string cellDir, Symbol? symbol, LayoutView? layout);
```

**`R-lvs1-2a`** One function. Three callers will exist (brief 3's layout read, brief 4's schematic
read, `check`) and a second copy of a four-rule precedence is exactly the shape `CellPins`' own
header warns about.

**`R-lvs1-2b`** The **origin is returned, always**, and every consumer that reports anything about
this cell states it. A derived map that does not say it was derived is indistinguishable from a
declared one, and the two have very different failure modes.

---

## 4. `R-lvs1-3` — the four derivation rules, in order

Absent is not a warning and not a failure. A cell with no block derives one:

**`R-lvs1-3a` — `ImportTable`.** The cell's `.ccell` carries `ImportedFrom` provenance, so
`ComponentTerminals`' own numbering applies: `SymbolPin.PortIndex i` ↔ `LayoutView.Pins[i-1]`. This
is the guaranteed case; it needs writing down, not recomputing.

**`R-lvs1-3b` — `ByName`.** Case-insensitive, ordinal-ignore-case, between `SymbolPin.Name` and
`LayoutPin.Name`. This is what `pcell-contract.md` R3 already promises. **Making the derivation
explicit is what turns an unenforced sentence into a checked one** — a PCell whose generator names
a pin differently from its symbol now has a finding instead of a silent mismatch later.

**`R-lvs1-3c` — `ByOrder`.** Only when **every** pin on **both** sides is unnamed and the counts
match exactly. Approved by the owner (2026-09-21, note §12.2) with a condition that is not
negotiable: it reports `lvs.terminals.derived-by-order` at **warning, on every run that does it,
including an otherwise clean one**. It is a guess that happens to be right most of the time, and a
guess that is never announced is the shape of a wrong answer nobody finds.

**`R-lvs1-3d` — `None`.** Counts disagree, or names match partly. The map is absent, the note names
**both lists** — the unmatched symbol pins and the unmatched layout pins, by name — and brief 3
reports the device as unmatchable rather than matching it against a fabricated terminal list.

**`R-lvs1-3e`** The partial-name case is the one that matters and it must not fall through to
`ByOrder`. Three of five names matching is evidence the author meant them to match and got two
wrong; reading the whole thing positionally would then produce a confident wrong answer over a
visible clue. `ComponentTerminals`' own refusal takes the same view.

---

## 5. `R-lvs1-4` — `check` validates it, and so does the application

**`R-lvs1-4a`** `src/Cli/Check.cs` gains `TerminalMap` to its validator table (`cli.md` §10.2), on
that table's terms: the verb adds the walk and the reporting and **no rule of its own**. Findings:

| id | Severity | When |
|---|---|---|
| `check.terminals.unknown-layout-pin` | error | a `LayoutPin` the primary `.clay` has no pin named |
| `check.terminals.port-out-of-range` | error | a `Port` outside `1..NumPorts` |
| `check.terminals.duplicate-port` | error | two rows claim one port |
| `check.terminals.pin-claimed-twice` | error | two terminals claim one layout pin |
| `check.terminals.unmapped-port` | warning | a declared port no row names |
| `check.terminals.unmapped-pin` | warning | a layout pin no row names — a mounting or shield pad is exactly this and is ordinary |
| `check.terminals.derived-by-order` | warning | `R-lvs1-3c` fired |
| `check.terminals.underivable` | error | `R-lvs1-3d` |

**`R-lvs1-4b` The last two rows are reported by `check` on a cell with NO block at all.** That is
the point: `check` tells a user their cell cannot be compared before they ever ask for a
comparison.

**`R-lvs1-4c` A rule that exists only in `check` is a rule the application does not enforce**
(R-aut4-2). The GUI's surface is the cell Properties panel: a **Terminals** section listing the
resolved map, its origin, and any finding above, editable, writing the block on commit. A cell that
opens with `None` shows the two unmatched lists side by side, because that is a two-minute fix the
user can only make if they can see both.

**`R-lvs1-4d`** `check` runs this on a **cell folder**, a **workspace** (recursive) and a bare
`.ccell`, exactly as it infers every other kind from the path. It writes nothing.

---

## 6. `R-lvs1-5` — the three authoring paths write the block

**`R-lvs1-5a` `ComponentImport`** already has the table (`ComponentTerminals.Result.Terminals`) and
writes the two views from it. It writes the block too, `Declared`. Nothing is recomputed; the same
`IReadOnlyList<ComponentTerminal>` that numbered the pins is serialized.

**`R-lvs1-5b` The PCell path** writes it from the generator's own pins, whose names R3 already
constrains, paired against the registered symbol's `SymbolPin.Name`. A generator whose pin names do
not match its symbol's now **fails the cell's own creation with a named refusal**, rather than
producing a cell that silently cannot be compared. This will find existing mismatches; that is a
feature and each one is a real defect.

**`R-lvs1-5c` `CellCreate`** writes an empty `Terminals: []`, which is distinct from absent: it
means *this cell was created by circuitRF and has no terminals yet*, and it is what a brand-new
cell with no views legitimately is.

**`R-lvs1-5d` Nothing is retrofitted onto an existing cell.** No migration, no rewrite on open, no
"upgrade your cells" gesture. §4's derivation is what they get, permanently and correctly, and the
origin says which rule answered.

---

## 7. Gate

`tests/Ui.Tests/Lvs/TerminalMapTests.cs`.

1. **Round trip.** A `.ccell` with a `Terminals` block loads, re-saves **byte for byte**, and one
   without it re-saves byte for byte with no block (`R-lvs1-1e`).
2. **Every existing shipped cell re-serializes unchanged.** Walk `examples/` and `src/Design/resources/`,
   load and save every `.ccell`, assert byte identity. This is the gate on additivity.
3. **The four derivations, one test each**, asserting the **origin** as well as the terminals
   (`R-lvs1-2b`): an imported cell → `ImportTable`; a PCell with matching names → `ByName`; a
   two-pin cell with no names anywhere → `ByOrder` **and the warning**; a five-pin cell with three
   names matching → `None` **and both lists named in the note** (`R-lvs1-3e`).
4. **Bonded pins.** A terminal whose `LayoutPin` is a two-element list resolves to one terminal
   covering two pins, and `check` does not report either pin as unmapped (`R-lvs1-1c`).
5. **Every `check` row above fires on a purpose-built broken cell**, by **id**, and the exit code
   is 0 for the warning-only cases and 1 for the error cases (`R-lvs1-4a`).
6. **`check` on a cell with no block at all** reports `derived-by-order` or `underivable` as
   applicable and **exits 0 for the former** (`R-lvs1-4b`).
7. **`ComponentImport` writes the block**, and the block it writes equals the
   `ComponentTerminals.Build` table it already computed — asserted against that call, not against a
   second derivation (`R-lvs1-5a`).
8. **A PCell whose generator pin names disagree with its symbol refuses at creation**, naming both
   (`R-lvs1-5b`). Run it against every registered generator: **any existing failure here is a real
   defect and is fixed in this brief**, not waived.
9. **`check` writes nothing.** Run it on a read-only copy of a workspace; assert no file mtime
   changed (R-aut4-6, already this verb's rule — asserted here because this brief adds a reader of
   `.ccell` and `.clay` to it).

## 8. Scope

- **No comparison.** Briefs 3-7.
- **No migration of existing cells** (`R-lvs1-5d`).
- **No change to `ComponentTerminals`' numbering.** It is the reference, not a subject.
- **No new expression language, no new file format.** One additive block in an existing file.
