# Schematic — local conventions for `src/Ui/Schematic/`

## Imported kit parts: symbol reader + palette (2026-07-30)

**`DsnSymbolReader`** reads the record-based ASCII symbol-description format (`.dsn`) into a
`Symbol`. It reads the **format**, not any part — nothing in it is specific to a kit, and it must
stay that way. Two conversions are load-bearing and easy to get wrong silently:

- **Y is negated.** The file is Y-up; symbol local coords are Y-down. Because the flip is a
  *reflection*, it also reverses arc handedness — `BuildArc` negates both the start angle and the
  sweep. Getting this wrong still draws an arc, just a mirrored one, which survives review; there
  is a dedicated test (`PartialSweep_..._HandednessFlippedByTheYAxisReflection`).
- **Pins are snapped to P=100** after scaling, because `SymbolModel` requires it. Two pins that
  collide after snapping are both kept and **reported** — never silently merged.

Scale is a power of ten chosen from the file's own declared view bounding box (record `44`, falling
back to measured content), targeting a 300–30,000 local-unit extent — so a kit authored in a
different drawing unit still lands legible without the reader knowing anything about that kit. All
five real-kit files measured in hand resolve to scale 1.

**Text is anchored from the object's bounding box, deliberately NOT from the text record's own x/y**
— those are min-corner in some files and centre in others, distinguished only by an undocumented
flag. The box is unambiguous everywhere.

**`PdkPartInstaller` installs kit parts as ordinary cells** (`<workspace>/pdk/<kit>/<part>/`), and
this is the whole reason kit parts need no new component species: a cell reference is *already* the
component whose artwork lives in an external file and resolves at render time, so placement,
rendering, pin geometry, hit-testing and the symbol editor all work on kit parts unchanged. Do not
add a parallel "external part" render path — it would duplicate all of that and drift.

**Two artworks, two jobs, on purpose:** the kit's `.bmp` browser icon is the palette tile
(`PaletteGlyphControl.IconPath`); the `.dsn` vector symbol is what goes on the schematic. Each is
used for what it was drawn for. A missing/undecodable icon falls back to the built-in glyph.

**A kit part is identified by kit+part id, never by `SymbolKind`** — every kit part shares one kind,
so an identity check on kind alone lights up every kit tile at once (`PaletteTool.ArmedFor`,
`PlacementService.Toggle(PaletteItem)`). There is a test for exactly that.

### A provider-backed cell is a LEAF, not a hierarchy

`CcellFile.ExternalProvider`/`ExternalType` (both `WhenWritingNull`, so every existing `.ccell` is
byte-identical) mark a cell whose behaviour comes from a registered external device provider. Such a
cell has a symbol and **deliberately no schematic**.

`NetExtractor.TryEmitExternalDeviceInstance` is checked BEFORE `EmitCellInstance` and emits one
`ExtDevice` instance — `Provider=`/`Type=` from the `.ccell`, every other parameter forwarded
verbatim for the provider to match against its own descriptor. Returns null for an ordinary cell, so
the hierarchical path is untouched. `Provider`/`Type` on the instance are dropped, not merged: a
stray override must never shadow the cell's own identity.

**An unconnected pin is not an error here.** The engine's external-device mapping makes every node
its own ground-referenced port, so an open thermal terminal is ordinary and correct — it just gets
its own auto-named net. Do not add a "floating pin" conflict for these.

**The Parameter Editor needs no kit-specific surface.** A part's declared parameters are written as
the cell's published interface (`.ccell` `Parameters`), and cell placement already seeds instance
parameters from that — so the ordinary editor works on kit parts for free. Defaults are left BLANK
on purpose: the provider owns them, and a value invented at install time would silently override
whatever the kit itself specifies.


`SymbolKind.NonlinearC` + registry + glyph (brief-nonlinearc-symbol, 2026-06-19) — COMPLETE: Added `NonlinearC` to end of `SymbolKind` enum (`SchematicModel.cs`). 5 `ComponentTypeRegistry.cs` edits: `Registry` entry (`"NLC"` display name, `"C"` prefix, `Lumped`, not IsCommon), `EngineReference("NonlinearC")`, `DefaultParameters` seeding `C0=1pF`, `UserParamTemplate` for `C1,C2,…` (raw SI, `None` dimension, `FirstAddIndex=1`), `TryParseCode("NLC")`. `BuiltInSymbols.cs`: `_nonlinearC` cache field, `Primitives` case, `BuildNonlinearC()` (capacitor glyph + 3 diagonal slashes). `SymbolPortDefs.For` falls through to `default` (2-terminal vertical), no separate case needed. Updated 2 `LibraryCatalogTests` that hardcoded Lumped = R/L/C. 1 Engine integration test (`T1_ConstantC_NonlinearC_MatchesLinearCapacitor`). Build 0W/0E, 1901 total tests.



Read with root `CLAUDE.md` and `src/Ui/CLAUDE.md`.

## SDD placement defaults (brief-p1tone-num-sddx-defaults, 2026-06-17)

`ComponentTypeRegistry.DefaultParameters(SymbolKind.Sdd, portCount)` now returns:
- `NumPorts = portCount`
- One `I[x,0] = _vx/50` per port x ∈ [1, portCount] (`ShowOnSchematic = true`)

This means a freshly-placed SDD acts as N independent 50 Ω conductances without any user edits — it
can be run through S-parameter analysis immediately and produces physically meaningful results.

The notation `_vx` is the port-x voltage (`V(net[2x]) − V(net[2x+1])`) in SDD equation syntax. The
engine parses `I[x,0]` as a two-index port-x current at harmonic 0 (the DC/baseband member in HB;
the only current in S-param mode where the SDD is treated as linear).

**Do not change these defaults without also updating `ParameterEditorRegistryTests` and
`SddDefaultParamsTests`** — they are the gate tests for this behavior.

## P1Tone Num parameter and port-number pool (brief-p1tone-num-sddx-defaults, 2026-06-17)

`DefaultParameters(SymbolKind.P1Tone, 0)` now includes `Num` as the first parameter (before Pavl/Z/Freq/Phase).
`Num` is the S-parameter port index; it is auto-assigned at placement from the **shared Term + P1Tone pool**
(`NextFreeTermNum` scans both symbol kinds) so Term:T1 (Num=1) and P1Tone:P1 (Num=1) can never coexist
on the same testbench top level.

`CommitPlacement` and `CommitInlineEdit` both handle the P1Tone case, mirroring the Term case.
