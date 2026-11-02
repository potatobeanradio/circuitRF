# Brief SI-2 — The `.kicad_sch` reader

**Series:** `brief-schimp-0-overview.md` · **Tag:** `R-si2-<m>`
**Depends on:** SI-1
**Area:** new `src/Design/Schematic/Import/SchematicSexprReader.cs`; read-only reuse of
`src/Design/Layout/Interchange/PcbSexpr.cs` and `ComponentSymbolSexprReader.cs`.

---

## 1. Goal

Read a `.kicad_sch` file — and the sheets it references — into SI-1's `ForeignSchematic`. Written from the publicly
documented file format; fixtures authored for the tests.

## 2. Facts about the format this reader must honour
- One file is one sheet. **`lib_symbols` embeds every symbol used, in the same grammar as `.kicad_sym`** — so each
  entry goes through `ComponentSymbolSexprReader` (factor its per-`symbol` node read so it takes a parsed node, not
  only a file; its tests unchanged). No external library lookup, ever.
- **Units are millimetres; the sheet is Y DOWN, the embedded symbols Y UP** — the opposite conventions inside one
  file. Sheet coordinates convert with `mm / 0.0254` to mils with no flip; symbol geometry goes through the existing
  symbol reader (which emits Y up — SI-1 R-si1-1).
- A placed `symbol` carries `lib_id`, `(at x y angle)`, optional `(mirror x|y)`, `(unit n)`, its `property` nodes
  (`Reference`, `Value`, `Footprint`, …), and per-pin `uuid`s. The connection point of a pin is the library pin's
  `(at …)` transformed by the instance's mirror-then-rotate — **derive and test the order** against a fixture whose
  answer is known, do not assume it.
- Multi-unit symbols name their units `<name>_<unit>_<style>` inside `lib_symbols`; unit 0 holds graphics common to
  all units.
- Older files place symbols' references in an `instances`/`symbol_instances` block keyed by sheet path rather than on
  the symbol; read both epochs. **The file's version is reported, never branched on beyond this** (the `.kicad_pcb`
  rule).
- `wire`, `junction`, `no_connect`, `label`, `global_label`, `hierarchical_label`, `bus`, `bus_entry`, `text`,
  `polyline`. **Power symbols** are symbols flagged `(power)` in `lib_symbols`: their `Value` is the global net name;
  they map to `ForeignLabel(Power)`, never to a symbol cell (and ground-named ones to ground).
- Legacy hidden power-input pins join the global net of their pin name — `ImplicitNet` = pin name, for epochs where the
  format still does that; report which epoch applied.
- `sheet` nodes reference a child file (`Sheetfile` property) and carry `pin`s that match the child's
  `hierarchical_label`s by name. The child path is relative to the parent file; a missing child is a refusal naming
  it. A child file used twice is read once (overview D5).
- Buses: `bus` + `bus_entry` with labels of the form `D[0..7]` or `{A B C}`; expand to member nets where the labels
  say so, otherwise a note (overview D11).

## 3. Requirements
**R-si2-1** `SchematicSexprReader.Read(path) → ForeignSchematic` (or a refusal). Pure: no CellFolder, no Messages.
**R-si2-2** `DeclaredNets` is null — the format declares no nets; SI-1's geometric nets are the check.
**R-si2-3** Everything not carried (images, tables, rule areas, field styling) → one `Notes` sentence per kind,
with a count.
**R-si2-4** A cross-check, local only: when `testdata/schematic-sources/` holds a `.kicad_sch` and the netlist its
own editor exported beside it, compare `(reference, pin)` sets with SI-1's check (`FixtureFact`; never committed).

## 4. Fixtures (committed, authored, tiny)
`testdata/schematic-import/sexpr/`: `divider.kicad_sch` (R, C, ground, a power label, one wire with a junction);
`opamp_units.kicad_sch` (a two-unit part, one mirrored, one rotated 90°); `hier_parent.kicad_sch` +
`hier_child.kicad_sch` (child used twice). Written by hand from the format reference or produced with the free
editor — either way **authored for this repo**, never copied from a third-party project.

## 5. Gates
One test per fixture asserting SI-1's connectivity check is `Matched` and two named pin positions; one test for the
older `symbol_instances` epoch; one for a missing child sheet's refusal.
