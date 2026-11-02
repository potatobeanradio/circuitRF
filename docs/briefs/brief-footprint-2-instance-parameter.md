# Brief 2 — the `Footprint` parameter: one combobox, one label, and the elaborator's blindness

**Series:** [SMT footprints](brief-footprint-0-overview.md) · **Tag:** `R-fp2-n` · **Phase:** P1
**Area:** `src/Design/Schematic/`, `src/Core/Elaboration/`, `src/Ui/ViewModels/ParameterEditorViewModel*`,
`src/Ui/Views/ParameterEditor/`, `src/Render/Renderers/SchematicRenderer*`
**Depends on:** [brief 1](brief-footprint-1-land-pattern-generator.md) · **Blocks:** briefs 3-5

---

## 0. What this brief delivers

The schematic half. A component carries a `Footprint`, the parameter editor shows a combobox for it,
the symbol can draw its name as a third label, and **nothing electrical ever sees it.**

No artwork is produced here — brief 3 does that. This brief is done when a `.csch` round-trips a
footprint choice and a simulation is bit-identical with and without it.

---

## 1. `R-fp2-1` — `Footprint` is an ordinary instance parameter

Not a new field on `EditableComponent`. An entry in `Parameters`, exactly as `PinConfig`, `Pitch`,
`RefNode`, `Form`, `State` and `Direction` already are (`src/Design/Schematic/EditableSchematic.cs:639`).

**`R-fp2-1a`** Read through the established accessor shape —
`EditableComponent.GetEnumParam`/`Parameters.FirstOrDefault` — and written through the parameter
editor's `Apply…Param` path, so it undoes, redoes, dirties and persists with everything else. There
is no second write path.

**`R-fp2-1b`** Absent means **None**. A `.csch` written before this existed carries no `Footprint`
and reads as None, which is what it was. No migration, no default written on load.

**`R-fp2-1c`** The VALUE is the reference string brief 1 R-fp1-5 defines: `smt:<case>@<density>`, or
a relative path, or absent. It is stored exactly as chosen and is never normalised, rewritten or
re-derived on load — a stored reference that the application rewrites is a reference that changes
under a user who did not change it.

---

## 2. `R-fp2-2` — which components may carry one

**Every component that has a layout view to place, which is every component.** The owner's intent is
explicit: an `S2P` standing in for a capacitor, an `SRLC`, an `S4P` standing in for a hybrid coupler.
There is no allow-list of component kinds.

What IS gated is the DEFAULT — R-fp2-3 — and what is OFFERED — brief 4.

---

## 3. `R-fp2-3` — the default is 0201, and only where 0201 means something

> **Decision, owner 2026-09-20:** `0201` is the default for the discrete RLC family on a
> PCB-class technology. **None** everywhere else.

**`R-fp2-3a`** The discrete RLC family: `R`, `C`, `L`, and the six two-element RLC parts added on
2026-09-20 (commit `800695c3`) — the parts whose physical realisation actually IS a chip component.

**`R-fp2-3b`** PCB-class means the resolved technology has a conductor whose drawing layer carries an
`Interchange.PcbLayerName` — which is how `pcb-*` technologies differ from `mmic-*` and from a
technology somebody wrote by hand. **Not a name match on the technology's title**, which is a string
a user owns.

**`R-fp2-3c`** None everywhere else, without exception:
- a component with a `CellRef` — its artwork is the cell's, and a footprint would be a second answer;
- a component with a registered PCell generator (`MLIN`, `MBEND`, `MTEE`, `MCROSS`, `MTAPER`,
  `MKLOPF`) — same reason;
- a kit part — the kit's layout cell is the answer;
- any component on a technology that is not PCB-class;
- `SnP`, `SpiceModel`, `SDD`, sources, ports, wBond, Match, the system blocks — anything whose
  physical realisation is not knowable from the schematic.

**`R-fp2-3d`** The default is applied **at placement only**, and is then an ordinary stored value the
user owns. It is never re-applied, never re-derived, and never written to a component that already
has one — including a component whose technology later changes. A default that follows the
technology around is a design that changes when you open it somewhere else.

**`R-fp2-3e`** The default is one function, `FootprintDefaults.For(symbol, technology)`, with
its own test. Not a literal in the placement path, because there are several placement paths
(palette drop, paste, duplicate, `.cnl` import) and a literal in one of them is a default that
depends on how the part got there.

---

## 4. `R-fp2-4` — the combobox

In the parameter editor, as a first-class row, in the same shape as SnP's `PinConfig` and `Pitch`
(`src/Ui/ViewModels/ParameterEditorViewModel.cs:250`): a declared option list, an
`[ObservableProperty] int` index, an `On…IndexChanged` partial that writes the parameter.

**`R-fp2-4a`** The rows, in this order: **None**, then the case sizes brief 1 ships, then any
workspace cell that qualifies (brief 4), then **Custom…**. `Custom…` opens a file picker for a
`.clay` and writes the chosen path; cancelling leaves the previous value untouched.

**`R-fp2-4b`** Every case row reads its metric twin and its millimetres — the overview's §1e:

```
0402   (metric 1005)   1.00 x 0.50 mm
```

This is not decoration. It is the whole defence against the 2.4x error, and a row that reads only
`0402` is the defect.

**`R-fp2-4c`** The density is a second, narrow combobox beside it — `Nominal` / `Most` / `Least` —
visible only when a built-in case is selected, and disabled for None, a workspace cell and Custom.
Nominal is pre-selected. It writes the `@` suffix of the same parameter; there is no second
parameter.

**`R-fp2-4d`** **`ItemsSource` is set before any selection is, and the selection is set once, after.**
The wBond round-6 defect is exactly this control shape: a `ComboBox` whose selection was assigned
before its `ItemsSource` binding attached silently dropped it and read blank. The test asserts a
non-blank selection on a component whose `Footprint` is set, immediately after the panel is built.

**`R-fp2-4e`** A stored value that is not in the list — a case code from a later version, a path to a
`.clay` that has since moved — is shown **as itself, as an extra row, marked unresolved**. It is not
silently reset to None: a design's stored choice is the design's, and a picker that erases what it
cannot display is a picker that loses work.

---

## 5. `R-fp2-5` — the third label, off by default

> **Decision, owner 2026-09-20:** the footprint NAME as a third instance label. Not an outline.

**`R-fp2-5a`** `EditableComponent.ShowFootprintLabel`, default **false**, beside `ShowTypeLabel` and
`ShowInstanceName` (`src/Design/Schematic/EditableSchematic.cs:651`). Persisted only when explicitly
toggled, so a `.csch` gains nothing until somebody asks for it.

**`R-fp2-5b`** It draws the case code alone — `0402` — not the whole reference string and not the
density. A schematic label reading `smt:0402@N` is machine spelling on a human drawing. A Custom
footprint draws the `.clay`'s file name without extension; a workspace cell draws the cell name.

**`R-fp2-5c`** It takes a `LabelOffsets` slot and is draggable like the other two
(`EditableComponent.GetLabelOffset`). **Appended after the parameter labels, not inserted between
name and params** — the existing convention is index 0 = type, 1 = name, 2+ = params, and inserting
at 2 would shift every stored parameter-label offset in every existing `.csch` by one, moving every
hand-placed label on every drawing anyone has ever made. This is the kind of index shift that
produces a hundred silent one-label-out-of-place bugs, so the new slot goes on the end.

**`R-fp2-5d`** Nothing is drawn when the footprint is None, whatever the toggle says.

---

## 6. `R-fp2-6` — the elaborator never sees it

The overview's §1c. **`Footprint` is dropped before parameter resolution, for every component kind,
in one place** — not added to `Elaborator._snpStringParams` (`src/Core/Elaboration/Elaborator.cs:1901`),
which is per-family and would leak the first time somebody puts a footprint on a family nobody
enumerated.

**`R-fp2-6a`** The drop is by NAME, in the one place instance overrides are collected, with a
comment saying why. `Footprint` joins whatever set of artwork-only names exists there; if there is
no such set, this brief creates it with exactly one member and names the invariant it serves.

**`R-fp2-6b`** The gate is a simulation, not an inspection: **the same design, run with and without
a `Footprint` on every component, produces a bit-identical `DataSet`.** That is the only test that
proves the numeric layer did not see it — an assertion about a dictionary's contents proves the
dictionary, not the invariant.

**`R-fp2-6c`** `SchematicToLayoutGenerator.NonPCellParamNames`
(`src/Ui/Layout/SchematicToLayoutGenerator.cs:67`) gains it, so it is never handed to a PCell
generator as a dimension. Without this, an `MLIN` carrying a stray footprint would pass `smt:0402@N`
to `MlinPCell` as a parameter, which reads it as a real and gets zero.

**`R-fp2-6d`** `CnlWriter`/`CnlReader` round-trip it as a string parameter, because
`circuitrf netlist` writes the `.cnl` a run consumes and a footprint dropped there is a footprint a
headless Update Layout could not see. It is written, read, and ignored by the engine.

---

## 7. Gate

`tests/Ui.Tests/Footprints/FootprintParameterTests.cs` — new.

1. **Round trip.** A `.csch` with `Footprint` on an `R`, an `S2P` and an `SRLC` saves, loads and
   compares equal. A `.csch` with none loads with none — no default written on load (R-fp2-1b).
2. **The default is applied where it should be and nowhere else.** Place `R`, `C`, `L`, one
   two-element RLC part, `MLIN`, `S2P`, a cell reference and a kit part, on a PCB technology and on
   `mmic-GaAs_2LM_100um`. Exactly the first four on the PCB technology get `0201`; the other twelve
   placements get nothing. One test, a table.
3. **The default is not re-applied.** Set a component's footprint to None explicitly, save, reopen,
   change the workspace technology, and assert it is still None (R-fp2-3d).
4. **Simulation is unaffected.** R-fp2-6b — bit-identical `DataSet`, with and without.
5. **The combobox shows a selection.** Build the panel for a component whose `Footprint` is
   `smt:0603@N` and assert the selection is non-blank and the density combo reads Nominal
   (R-fp2-4d).
6. **An unresolvable stored value survives.** `Footprint = smt:9999` shows as an unresolved extra
   row, and saving without touching the combobox writes `smt:9999` back unchanged (R-fp2-4e).
7. **Label offsets do not shift.** Load a `.csch` written before this brief that carries hand-dragged
   parameter labels, and assert every label lands where it did — the R-fp2-5c regression.
8. **`.cnl` round trip.** `circuitrf netlist` on a schematic with footprints, read back, compared —
   and `circuitrf run` on the result matches the run without them (R-fp2-6d).

## 8. Scope

- **No artwork.** Brief 3.
- **No picker population from the workspace.** Brief 4 — this brief's combobox holds None, the
  built-ins and Custom, and brief 4 adds the middle section.
- **No footprint outline on the schematic.** R-fp2-5's decision.
