# Brief 4 — the schematic netlist, and one canonical device type

**Series:** [LVS](brief-lvs-0-overview.md) · **Tag:** `R-lvs4-n` · **Design note:** [`lvs.md`](../design/lvs.md) §5, §6.1
**Area:** `src/Design/Layout/Lvs/SchematicRead.cs` (new), `DeviceType.cs` (new),
`src/Design/Schematic/NetExtractor.cs`, `src/Cli/CircuitSource.cs`,
`src/Ui/Layout/LayoutToSchematicGenerator.cs`
**Depends on:** 1 · **Blocks:** 6, 7

---

## 0. What this brief delivers

A `.csch` becomes the **same** `LvsNetlist` type brief 3 produces from a `.clay`, and both sides
name a device's type through one function.

Almost all of this already exists. What is new is the projection into `LvsNetlist`, the exclusion
list being **shared** rather than restated, and the canonical type.

---

## 1. `R-lvs4-1` — the read goes through the `.cnl`, in memory

**`R-lvs4-1a`** `NetExtractor.Extract → CnlWriter.Write → CnlReader.Read`, as a string, via
`src/Cli/CircuitSource.cs` — the round trip the GUI's own Simulate performs.

**`R-lvs4-1b`** This is not ceremony. `cli.md` §10.3 records what skipping it costs: a schematic
parameter is an **expression**, so `BiasTee=on` read straight out of extraction fails elaboration
with *"Unresolved name 'on'"*, while the same value written to a `.cnl` and read back is quoted by
`CnlReader` and elaborates. Skipping the round trip reports errors the application does not have —
the mirror image of the rule that a validator must be one the GUI uses, and just as bad.

**`R-lvs4-1c`** The round trip happens **in memory**. Nothing is written (R-aut4-6).

---

## 2. `R-lvs4-2` — the design model, not the elaborated netlist

**`R-lvs4-2a`** The comparison runs on the `TestBench` + `Library` design model. The `Elaborator`
flattens hierarchy and uniquifies nets by instance path; LVS needs the hierarchy intact (brief 9)
and needs to report in the user's own names.

**`R-lvs4-2b`** What LVS takes from elaboration is **resolved parameter values and nothing else**
(brief 10). It runs the elaborator to get them — parameters, expressions, cycles all resolved
top-down — and reads the values off the elaborated components by instance path.

**`R-lvs4-2c`** An elaboration failure is a **refusal**, not a partial comparison. A design whose
parameters do not resolve has no values to compare and its topology may depend on them. The
refusal is the elaborator's own sentence, unmodified.

---

## 3. `R-lvs4-3` — the exclusion list is shared, not restated

**`R-lvs4-3a`** Excluded from the schematic netlist: `VAR`, `MEAS`, `Ground`, `Pin`, and every
component whose `DisableState` disables it.

**`R-lvs4-3b` The list lives in ONE place and `NetExtractor` and LVS both read it.** A component
that is electrical to one and not the other produces a mismatch whose cause is in neither
document, and that is the worst kind of finding this tool can emit. Today `NetExtractor` has these
exclusions inline; this brief lifts them to a named, shared set.

**`R-lvs4-3c` `Ground` is excluded as a device and is the reason net `"0"` exists.**
`NetExtractor` already resolves ground → `"0"` before net labels are applied, so `"0"` always wins
a ground-label conflict. LVS inherits that rule and does not re-derive it.

**`R-lvs4-3d` `Pin` is a connectivity marker** the elaborator already skips; LVS skips it for the
same reason and does not treat it as a zero-ohm device.

---

## 4. `R-lvs4-4` — ports, terms, and what the default unit of comparison is

**`R-lvs4-4a`** The default compares the **cell**, not the testbench (owner, note §12.3 /
R-lvs-29). `circuitrf lvs <cell>` compares a cell's own primary schematic view against its own
primary layout view, and a **cell port corresponds to a layout boundary pin**.

**`R-lvs4-4b`** Cell port order is `NetExtractor.BuildCellPorts` — sorted by the `Port Num=`
parameter, which is already the schematic's own answer. The layout boundary pins are ordered by
brief 1's terminal map. The two are joined through the map, **never positionally**.

**`R-lvs4-4c`** `--testbench` includes the fixture: `Port`, `Term` and the tuner family become
devices, for the designer who has actually drawn the launches. Without it they are excluded and
their nets become boundary nets.

**`R-lvs4-4d`** A testbench cell (`.ccell`'s `IsTestBench`) compared without `--testbench` reports
`lvs.scope.testbench-excluded` at info naming the flag, because *"nothing matched"* on a testbench
would otherwise be a mystery.

---

## 5. `R-lvs4-5` — the canonical device type

Four namespaces name a device type today: `SymbolKind`, `CellRef`, `PCellOrigin.GeneratorId`, and
`PartKind`/`FootprintRef`. `LayoutToSchematicGenerator.ReverseGeneratorMap` is the seed of a bridge
and covers six microstrip generators.

**`R-lvs4-5a`** One function, in `src/Design/Layout/Lvs/DeviceType.cs`, called by **both** sides:

```csharp
public readonly record struct DeviceType(DeviceKind Kind, string? CellDir, string? Name);
public static DeviceType OfSchematic(Instance inst, string schematicDir);
public static DeviceType OfLayout(LayoutInstance inst, string layoutDir);
```

**`R-lvs4-5b` Precedence.** The **resolved cell directory** where both sides reference a cell — an
absolute path is an unambiguous identity and needs no name matching, and it is what makes a
user-authored PDK part, an imported component and a hand-drawn cell all work identically. Else the
generator-id ↔ `SymbolKind` map. Else `PartKind`. Else the built-in kind.

**`R-lvs4-5c` The generator map is completed.** Six entries today; every registered
`PCellRegistry` generator gets one, and a generator with no `SymbolKind` counterpart is a **build-
time** failure of the map's own completeness test, not a runtime fallback. A silent fallback here
produces two devices of "unknown" type that then match each other.

**`R-lvs4-5d`** `ReverseGeneratorMap` is **deleted** and its two callers point at the new map. Two
maps with one meaning drift, which is this repo's recurring scar.

**`R-lvs4-5e` Two devices of different canonical type never match**, and brief 7 reports the type
mismatch **as such**: *"R1 is a resistor in the schematic and a capacitor in the layout"* is one
sentence a user can act on; two "unmatched device" lines are a puzzle.

---

## 6. `R-lvs4-6` — multi-terminal ordering and the net binding

**`R-lvs4-6a`** `Instance.NetBindings` is positional and matches the referenced type's port order.
That ordering is the schematic's terminal numbering and it is what brief 1's `Port` refers to.

**`R-lvs4-6b`** `Instance.RefNetBinding` — the shared reference node an N-port carries under the
N-or-N+1 rule — becomes an ordinary terminal with port number `N+1` and name `REF`. Dropping it
would make an `SnP` referenced to something other than ground compare as though it were grounded.

**`R-lvs4-6c`** A wBond's ports are `2M` (+ reference), ordered by array, and its terminals are
named from the array names. This brief emits them; brief 13 is what finds their copper.

---

## 7. Gate

`tests/Ui.Tests/Lvs/SchematicNetlistTests.cs`.

1. **The divider again.** The same two-resistor circuit as brief 3, from the schematic side,
   produces the same device count, terminal count and net partition — asserted structurally, since
   the net *numbering* is each side's own.
2. **A quoted parameter survives.** A component with `BiasTee=on` reads without error through the
   round trip and **fails** without it (`R-lvs4-1b`). Assert both directions; the negative is what
   pins the reason.
3. **Exclusions.** A schematic carrying a `VAR`, a `MEAS`, three `Ground`s, a `Pin` and a disabled
   resistor yields devices for none of them, and the ground nets are all `"0"` (`R-lvs4-3`).
4. **The exclusion list is shared.** A source scan asserting `NetExtractor` and `SchematicRead`
   name the same set, and a test that adding a kind to the set changes both (`R-lvs4-3b`).
5. **Cell ports.** A three-port cell whose `Port Num=` are 2, 3, 1 in drawing order produces
   boundary nets in port order 1, 2, 3 (`R-lvs4-4b`).
6. **`--testbench` on and off** on one testbench cell: with it, `Port`/`Term` are devices; without,
   they are boundary nets and `lvs.scope.testbench-excluded` is reported (`R-lvs4-4c`, `d`).
7. **`DeviceType` agreement.** For every kind circuitRF can place — built-in R/C/L, an `SnP`, an
   `SDD`, each microstrip generator, an imported component, a kit part, a hand-drawn cell — the
   schematic side and the layout side produce an **equal** `DeviceType` (`R-lvs4-5b`). This is the
   single most valuable test in the brief.
8. **The generator map is complete**, asserted over `PCellRegistry`'s own registration list, and
   the test **fails on a new generator with no entry** (`R-lvs4-5c`).
9. **`ReverseGeneratorMap` is gone**, source scan (`R-lvs4-5d`).
10. **An `SnP` with a non-ground reference** carries an `N+1` terminal bound to that net
    (`R-lvs4-6b`).
11. **Elaboration failure refuses** with the elaborator's own sentence and produces no partial
    netlist (`R-lvs4-2c`).

## 8. Scope

- **No comparison.** Brief 7.
- **No reduction.** Brief 6.
- **No property reading beyond collecting resolved values** into `LvsDevice.Parameters`; judging
  them is brief 10.
- **No change to `NetExtractor`'s own output.** It gains a shared exclusion set and loses nothing.
