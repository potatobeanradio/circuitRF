# Phase 6e — Step 3: Units glyph→ASCII normalization at the extraction boundary (Claude Code / Sonnet)

Small but **mandatory before any real run**: the editor's unit ComboBox uses **glyphs** (`Ω`, `µ`) while the
engine `Units` table is **ASCII-keyed** (`Ohm`, `u`) with `StringComparer.Ordinal`. Extraction emits parameter
units, so an emitted `µF`/`kΩ` → `Units.Scale` returns null → the run throws on the unit. **Normalize glyph
units → ASCII at the extraction boundary** (convert once, where the GUI world meets the engine world). One
focused change. Read `net-extraction-and-run.md` §5 (the Units seam) first. Report when done. Firewall green.

> Context code: `src/Core/Expressions/Units.cs` (the ASCII table — `Ohm`/`kOhm`/`uH`/`uF`/`u`/…, `Ordinal`,
> case-sensitive; the **target spelling**), `src/Ui/Schematic/ComponentTypeRegistry.cs` (`_unitOptions` — the
> **glyph source**: `mΩ`/`Ω`/`kΩ`/`MΩ`/`GΩ`, `pH`/`nH`/`µH`/`mH`/`H`, `fF`/`pF`/`nF`/`µF`/`mF`/`F`,
> `nV`/`µV`/`mV`/`V`/`kV`, `nA`/`µA`/`mA`/`A`, `fW`…`µW`…`dBm`, `nm`/`µm`/`mm`/`cm`/`m`/`mil`, `deg`/`rad`),
> `src/Ui/Schematic/NetExtractor.cs` + `src/Core/Netlist/CnlWriter.cs` (where param units are emitted — the
> **normalization point**). Design docs win on any conflict.

## The principle
- **Convert at the boundary, once** (the `net-extraction-and-run.md` §5 + `project-file-formats.md`
  "convert at the boundary, once" rule). The GUI/editor thinks in glyphs; the engine thinks in ASCII; the
  **extraction emit** is the single crossing point — normalize there, not scattered.
- **The two glyph→ASCII substitutions** that matter: **`Ω` (U+03A9) → `Ohm`**, and **`µ` (micro sign U+00B5)
  → `u`**. Compose with the SI prefix so `kΩ→kOhm`, `MΩ→MOhm`, `GΩ→GOhm`, `mΩ→mOhm`, `µH→uH`, `µF→uF`,
  `µV→uV`, `µA→uA`, `µW→uW`, `µm→um`. (Also handle Greek mu U+03BC `μ` → `u` defensively — some fonts/inputs
  produce it instead of the micro sign.)
- **Don't change the editor or the engine table** — the editor keeps showing glyphs (RF engineers want `Ω`);
  the engine keeps its ASCII keys. Only the **emitted** unit string is normalized.

## The change

1. **A `UnitNormalizer` helper** (framework-free, in `src/Core/Expressions/` next to `Units`, or `src/Ui/
   Schematic/` next to the extractor — pick the layer that keeps it shared and framework-free):
   `string ToEngineUnit(string editorUnit)` that maps a glyph unit string to the engine spelling:
   - replace `Ω` → `Ohm`, `µ`/`μ` → `u`;
   - leave already-ASCII units (`Hz`, `pF`, `nH`, `mil`, `deg`, …) unchanged;
   - `"None"` / empty → empty (no unit).
   Implement as targeted glyph replacement (not a hardcoded per-string table) so any prefix+`Ω`/`µ`
   combination composes correctly.
2. **Apply it at emit.** Wherever extraction emits a parameter's unit into the `.cnl` (the `CnlWriter`
   `param=val unit` formatting, fed from `NetExtractor`'s `Overrides`), pass the unit through
   `ToEngineUnit` first. This is the one place the conversion happens.
3. **Validate against the engine table.** After normalization, the emitted unit should be one `Units.IsKnown`
   accepts (for the units the engine actually scales). If a normalized unit is **not** known to `Units`
   (e.g. `dBm`, `cm`, `V`, `A`, `W` — measurement/units not in the linear-scale table), do **not** crash:
   emit it as-is (the engine/expression layer handles dB/dBm as functions, and bare `V`/`A`/`W` are
   identity/dimensionless at this layer) — but **note** which dimensions have units the table doesn't cover,
   so a follow-up can decide whether `Units` should learn them. (Don't expand the `Units` table in this brief;
   just don't let a normalized-but-unknown unit throw — match the engine's existing tolerance.)

**Gate:** unit tests — `ToEngineUnit("µF")=="uF"`, `"kΩ"=="kOhm"`, `"Ω"=="Ohm"`, `"GΩ"=="GOhm"`,
`"µH"=="uH"`, `"nH"=="nH"` (unchanged), `"None"`→empty; an extracted netlist with a `µF` capacitor and a `kΩ`
resistor emits `uF`/`kOhm` and `Units.Scale` resolves them; the round-trip/oracle (step 2) still passes.
Report.

## Acceptance
1. A framework-free `UnitNormalizer.ToEngineUnit` maps glyph units (`Ω→Ohm`, `µ/μ→u`, composed with prefixes)
   to engine ASCII spellings; ASCII units pass through unchanged; `None`/empty → empty.
2. Extraction applies it at the single emit point; an extracted `.cnl` with glyph-unit params resolves through
   `Units.Scale` (no null/throw for table-covered units); table-uncovered units (dBm/V/A/W/cm) emit without
   crashing and are noted.
3. The editor and the `Units` table are **unchanged**; only the emitted unit string is normalized.
4. `dotnet build`/`dotnet test` green (incl. step-2 oracle); firewall green; nothing else regresses.

## Guardrails
- **Convert at the boundary, once** — normalize only at the extraction emit; don't touch the editor glyphs or
  the engine table.
- **Glyph substitution, not a per-string table** — `Ω→Ohm`, `µ/μ→u` compose with any prefix.
- **Don't throw on table-uncovered units** — emit as-is + note; expanding `Units` is a separate decision.
- One focused change; report when done.
- Update `net-extraction-and-run.md` §6 status (step 3 done) and `src/Ui/CLAUDE.md` (the Units glyph→ASCII
  normalization lives at the extraction boundary; editor glyphs and engine ASCII both unchanged).

*Exit: extracted netlists carry engine-resolvable ASCII units — the long-flagged glyph↔ASCII seam closed at
the one boundary that matters — so a real run won't throw on `µF`/`kΩ`; the in-app Run (step 5) can resolve
units.*
