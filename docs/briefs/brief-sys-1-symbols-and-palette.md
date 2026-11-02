# Brief SYS-1 — the system-block glyphs, and a `System` palette filter

**Read the series brief first:** `brief-sys-series.md`.

**This brief adds no electrical behaviour.** It adds twelve `SymbolKind`s, their glyphs, their
registry entries, their port definitions, their ground-return net extraction, and one new palette
category. Every tile it places is inert until its own later brief supplies a model — which is
deliberate: **the owner approves the artwork before anything is built on it**, and the artwork is
the part users will read most, because these blocks exist to be drawn as system diagrams.

**Read first:** `src/Ui/Schematic/BuiltInSymbols.cs` (the primitive helpers `L`, `A`, `Circ`, `QC`,
`RRect`, `Poly`, `PLine`, `Txt`, and `BuildMixer`/`BuildMixerD` — the closest precedent);
`docs/design/standard-library-symbols.md` (the geometry conventions this brief writes in);
`src/Ui/Schematic/ComponentTypeRegistry.cs`; `src/Ui/Schematic/EditableSchematic.cs`
(`SymbolPortDefs.For`); `src/Ui/Schematic/NetExtractor.cs` (the `SymbolKind.Mixer` branch);
`src/Ui/Schematic/LibraryCatalog.cs`; `src/Ui/ViewModels/Dock/PaletteTool.cs`;
`src/Ui/Diagnostics/SymbolArtworkGenerator.cs`; and the whole of commit `e925f9c`, which is the
worked example of adding a component tile end to end.

## Conventions these glyphs obey

From `standard-library-symbols.md`: component-local coordinates, origin at the symbol centre,
**100 units = one connection-grid square**, +y **down**. **Every pin tip is an exact multiple of
100**, and the symbol's lead ENDS at the pin — the renderer draws the connection marker. Colour
roles only (`SymbolLine`, `SymbolText`, `SymbolPlus`), never literal colours. Port-name text uses
the existing `MixerPortFontSize` (30) and polarity marks `PolarityFontSize`.

Two things are inherited from the mixer and are not up for redesign here: **a signal block reads
left to right** (inputs left, outputs right, a third port bottom), and **a block whose three leads
are not interchangeable labels them**, because a reader who connects the wrong one gets a circuit
that solves and is wrong.

---

## D1 — the glyphs

**Reviewed and approved 2026-08-31**, off a rendered sheet rather than off this coordinate list,
with two corrections and one substitution already folded in below: the duplexer's antenna lead and
its TX/RX label placement, and the filter glyph becoming the match glyph outright. Two smaller
choices are still open and are marked **(alternative)** — the balun's frame and whether a 180°
hybrid ships alongside the 90° one.

Each entry is buildable as written.

### `Balun` — 3 pins · UNB (−300, 0) · BAL+ (300, −100) · BAL− (300, +100)

```
RRect(0, 0, 240, 300, 12)                             # body, x∈[-120,120] y∈[-150,150]
A(-45, -60, 30, ...) A(-45, 0, 30, ...) A(-45, 60, 30, ...)    # primary coil, 3 arcs bulging -x
A( 45, -60, 30, ...) A( 45, 0, 30, ...) A( 45, 60, 30, ...)    # secondary coil, bulging +x
L(-8, -80, -8, 80)   L(8, -80, 8, 80)                 # transformer core, two lines
L(-300, 0, -120, 0)                                   # UNB lead
L(120, -100, 300, -100)  L(120, 100, 300, 100)        # BAL+ / BAL- leads
Txt("+", 215, -155, PolarityFontSize, SymbolPlus)  Txt("-", 215, 155, PolarityFontSize)
```

A transformer inside a box: the box keeps it in the same family as the other system blocks, the
coils say what it is, and the single left lead against a ± pair on the right says which end is
unbalanced without spending text on it. **(alternative)** a bare transformer with no box, primary
drawn down to a ground stub the way `TermG` reuses Ground's own primitives — more traditional,
less consistent with its neighbours, and it makes the tile taller than every other block.

### `Circulator` — 3 pins · P1 (−300, 0) · P2 (300, 0) · P3 (0, 300)

```
Circ(0, 0, 150)                                       # body circle
A(0, 0, 80, start, sweep=260)                         # the rotation arrow's arc
Poly(filled, ...)                                     # its arrowhead, at the arc's end
L(-300, 0, -150, 0)   L(150, 0, 300, 0)   L(0, 150, 0, 300)
Txt("1", -215, -30)   Txt("2", 215, -30)   Txt("3", 60, 235)
```

The universal circulator symbol. **The arrow is dynamic**: `Direction = CW` circulates 1→2→3→1 and
`CCW` reverses it, and the glyph must show which — same per-instance mechanism as `Match`
(`BuiltInSymbols.PrimitivesForMatch`, cached per variant, dispatched from
`EditableSchematic.BuildRenderModel`). Two variants, cached.

### `Switch` (SPST) — 2 pins · (−300, 0) · (300, 0)

```
Circ(-100, 0, 12, filled)   Circ(100, 0, 12, filled)  # the two contacts
L(-300, 0, -100, 0)   L(100, 0, 300, 0)               # leads
L(-100, 0, 100, 0)          # State=On : the blade, closed
L(-100, 0,  80, -90)        # State=Off: the blade, lifted
```

### `SwitchD` (SPDT) — 3 pins · COM (−300, 0) · T1 (300, −100) · T2 (300, +100)

```
Circ(-100, 0, 12, filled)   Circ(100, -100, 12, filled)   Circ(100, 100, 12, filled)
L(-300, 0, -100, 0)   L(100, -100, 300, -100)   L(100, 100, 300, 100)
L(-100, 0, 100, -100)   # State=1 : blade to throw 1
L(-100, 0, 100,  100)   # State=2 : blade to throw 2
Txt("1", 165, -155)   Txt("2", 165, 155)
```

Both switch glyphs are **dynamic on `State`** — a switch drawn in the position it is actually set
to is the whole point, and a `State` swept parametrically then reads off the schematic. Two
variants each.

### `Amp` — 2 pins · IN (−300, 0) · OUT (300, 0)

```
Poly(stroked, -140,-150,  -140,150,  160,0)           # the amplifier triangle
L(-300, 0, -140, 0)   L(160, 0, 300, 0)
```

Nothing inside it. The gain shows as the parameter label under the symbol, which is where a reader
looks for a number.

### `Coupler` — 4 pins · P1 IN (−300, −100) · P2 THRU (300, −100) · P3 CPL (300, +100) · P4 ISO (−300, +100)

```
RRect(0, 0, 320, 300, 12)                             # body, x∈[-160,160] y∈[-150,150]
L(-300,-100, 300,-100)   L(-300, 100, 300, 100)       # the two arms, leads continuing through
L(-40, -100, 40, 100)    Poly(filled, ...)            # coupling arrow, main arm -> coupled arm
Txt("1", -215, -140)  Txt("2", 215, -140)  Txt("3", 215, 145)  Txt("4", -215, 145)
```

The arrow does real work: it is what separates the coupled port from the isolated one, and a coupler
drawn without it is ambiguous in exactly the way that produces a silently wrong circuit.

### `Hybrid90` — the same 4-port body, plus `Txt("90°", 0, 15, 44)` at the centre

The arms cross the box at y = ±100, so the centre is free for the label. Same pins, same arrow.
**(alternative)** a `Hybrid180` tile for free — identical geometry, `"180°"` in the middle, and a
sign flip in the S it will later carry. Not requested; say yes or no.

### `Filter` — 2 pins · (−200, 0) · (200, 0)

**The filter glyph IS the match glyph — the same picture, not a related one.** Owner decision,
2026-08-31, and the reasoning is the one the two components share: impedance matching is a form of
filtering, the two are built out of the same idea, and a library that draws them the same way says
so. There is no geometry to approve here, because there is no new geometry:

```
L(-200, 0, -110, 0)   L(110, 0, 200, 0)                # Match's own leads, to Match's own pins
RRect(0, 0, 220, 220, 18)                              # Match's own body
MatchWaveStack(prims, form, 1.0, 0, 0)                 # Match's own wave stack, per Form
```

Implement it as reuse rather than as a copy: `PrimitivesForFilter(form)` returns
`Sym([.. PrimitivesForMatch(form, 1).Primitives], SymbolKind.Filter)` — the `TermG` pattern, which
already builds its symbol out of `Term`'s and `Ground`'s own primitives verbatim. That makes the
two glyphs identical **by construction**, so they cannot drift apart later, and the `bandCount`
argument is always 1 (a filter has one band; a multi-band match does not).

The two components are then told apart on the schematic by their type label and their instance
name (`FLT1` against `MN1`), which is the same way the five FET laws — which also share one glyph —
are told apart today. Note the consequence in the registry comment so the next reader does not
"fix" it: the duplicate is deliberate.

**Dynamic on `Form`** — `Lowpass`, `Bandpass`, `Highpass` — through the same struck-line convention
and the same per-variant cache `Match` uses.

### `Atten` — 2 pins · (−300, 0) · (300, 0)

```
RRect(0, 0, 240, 160, 12)
Poly(filled, -80,-60, -80,60, 0,0)   Poly(filled, 80,-60, 80,60, 0,0)   # the bowtie
L(-300, 0, -120, 0)   L(120, 0, 300, 0)
```

The pinched bowtie reads as "signal made smaller" and collides with nothing else in the library.
The loss shows as the parameter label.

### `Duplexer` — 3 pins · ANT (−300, 0) · TX (300, −100) · RX (300, +100)

```
RRect(0, 0, 340, 320, 14)                             # body, x in [-170,170]  y in [-160,160]
L(-300, 0, -170, 0)                                   # ANT lead, pin to body edge
L(-170, 0, -90, 0)   L(-90, 0, -30, -90)   L(-90, 0, -30, 90)    # the junction and its two arms
MatchWaveStack(scale 0.45, centre (55, -90))          # TX passband
MatchWaveStack(scale 0.45, centre (55,  90))          # RX passband
L(170, -100, 300, -100)   L(170, 100, 300, 100)
Txt("TX", 130, -90)   Txt("RX", 130, 90)              # INSIDE the body, BESIDE their own stack
```

One junction splitting into two filters is what a duplexer *is*, and the glyph says so. Two details
are owner corrections, 2026-08-31, and both are the kind a rendered figure makes obvious and a
coordinate list hides: **the ANT lead must run from the pin to the body edge** — a port whose lead
stops short of the frame reads as unconnected — and **the TX and RX labels belong inside the body**,
not outside it where they collide with whatever is wired next to the block.

**Each label sits BESIDE its own passband stack, not above it, and the arithmetic is why.** At
scale 0.45 a stack reaches |y| = 120.6 including its strike lines, so the gap between it and the
frame at |y| = 160 is 39 units — a 34-unit label cannot fit in that with clearance at both ends, and
padding it away from the frame only pushes it into the waves. Beside is where the room is: the stack
spans x ∈ [25.75, 84.25], so a label centred at x = 130 clears the waves by 26 units and the frame by
20, and it names the arm it is level with. **(alternative)** keep the labels above and below and grow
the body to h = 360, which buys the vertical room at the cost of the tallest tile in the palette.

---

## D2 — the `System` palette filter

Add `ComponentCategory.System` with a doc comment in the shape of `Matching`'s: it names a class of
parts a user goes looking for. It is the **primary** `Category` for all twelve new tiles and an
**`ExtraCategories`** membership for `Mixer` and `MixerD`, whose primary stays `Devices` (the
reason recorded in `ComponentTypeRegistry` at the mixer entry is still true — a mixer is a device
you put in the signal path — and `Nonlinear` keeps carrying them too).

Three places list categories and all three must agree; a fourth pins the result:

1. `ComponentCategory` — the enum member itself.
2. `PaletteTool`'s ordered category list (currently Lumped, Devices, Nonlinear, Sources, Terminals,
   TransmissionLine, Microstrip, Matching, DataFiles). **Recommended position: immediately after
   `Devices`.** A system block is a signal-path part, and a user who has found `Devices` is one row
   away from the blocks they want.
3. `LibraryCatalog.CategorySortKey`, which orders `AllItems` and therefore the "All" filter.
4. `tests/Ui.Tests/PaletteFilterOrderingTests.cs` — `AllItemsPinnedOrder` and its `PinnedRows`
   constant. **Adding a category reshuffles `AllItems`**, so expect this file to need updating and
   update it deliberately rather than to make red go green: the pinned list is an owner-stated
   order, and nothing new belongs in it unless the owner says so.

`IsCommon` (the "Common" virtual filter) — recommended `true` for `Amp`, `Filter`, `Coupler`,
`Atten`, `Circulator`; `false` for the rest.

## The rest of each tile

For every new kind, the pattern is `e925f9c`'s, file for file:

- `SchematicModel.cs` — the `SymbolKind` member, **appended at the end of the enum** (the enum is
  persisted ordinally by `.csch`; never insert in the middle), with a doc comment saying what the
  block is and what its pin ORDER is.
- `ComponentTypeRegistry.cs` — the `Registry` entry (display name, instance prefix, `System`
  category, search terms), `EngineReference`, `DefaultParameters`, `ParameterDescription`, and
  `TryParseCode`. Instance prefixes: `BAL`, `CIRC`, `SW` (both switch tiles share it — swapping SPST
  for SPDT should not renumber, the mixer's own reason), `AMP`, `CPL`, `HYB`, `FLT`, `ATT`, `DPX`.
  Short type codes: `BALUN`, `CIRC`, `SW`, `SWD`, `AMP`, `CPL`, `HYB`, `FLT`, `ATT`, `DPX`.
- `EditableSchematic.cs` — `SymbolPortDefs.For`, the pin list, **in the engine's own port order**.
- `NetExtractor.cs` — the ground-return branch. Every tile here is ground-referenced: it shows N
  pins and the extractor emits 2N nets by appending `"0"` after each, exactly as the
  `SymbolKind.Mixer` branch does. **Generalise that branch to a set of kinds rather than copying it
  ten times**, and keep its comment's reasoning.
- `BuiltInSymbols.cs` — the cached `Symbol`, the `Primitives` case, and for the four dynamic ones a
  per-variant cache and a `PrimitivesFor…` entry point beside `PrimitivesForMatch`.
- `SymbolArtworkGenerator.cs` — one `Catalog` row per tile (`(SymbolKind.Xxx, "file-stem", ports)`),
  which is what makes the documentation figure exist. The dynamic ones need the `DocSymbolGlyph`
  treatment `Match` already has.

## Milestones

1. **Glyphs first, and render them before going on.** Build every `Build…` method and its
   `Primitives` case, add the `SymbolArtworkGenerator.Catalog` rows, and run the artwork generator.
   The geometry is approved, so this is a check that the drawing matches the specification, not a
   second approval round — but a glyph is still the one thing in this repository that cannot be
   verified by a test alone, so **look at the output** before building anything on top of it. The
   two open alternatives above need an answer at this point and not before.
2. **Registry, ports, extraction, palette.** The tiles place, drag, rotate, save and reload; the
   `System` filter lists them; the two mixer tiles appear there too.
3. **The four dynamic glyphs.** `Circulator` on `Direction`, `Switch`/`SwitchD` on `State`,
   `Filter` on `Form`. Cached per variant, dispatched exactly where `Match` is dispatched.

## Must NOT

- Redraw, resize or re-lead the existing `Match` glyph. Factoring `MatchWaveStack` out for reuse
  must leave `Match`'s own primitives byte-identical — assert it.
- Insert a `SymbolKind` anywhere but the end of the enum.
- Give a tile a model, a parameter that does anything, or an `EngineReference` that resolves — a
  tile placed after this brief and simulated must fail the way any unimplemented primitive does,
  and SYS-2 onwards makes each one real.
- Put port names on a glyph whose ports are interchangeable, or leave them off one whose ports are
  not.

## Gates

Every glyph's pins land on multiples of 100 and every lead ends exactly at its pin (assert
mechanically over all new kinds, not by eye). Round-trip: a schematic with one of each tile saves
and reloads with identical geometry and parameters. `LibraryCatalog` lists each new kind exactly
once, under `System`, and `Mixer`/`MixerD` appear under `System` and `Devices` both. The dynamic
glyphs return a DIFFERENT primitive list per variant and the same list for the same variant
(cache identity). **`Filter`'s primitive list is element-for-element equal to `Match`'s for the same
form** — assert all three, so the two cannot drift apart when either is next touched — and `Match`'s
own primitives are unchanged by the refactor that shares them. The duplexer's ANT lead reaches its
pin, and both its text labels lie inside the body rectangle **with clearance** — assert a minimum gap
to the frame AND to the wave stacks, not mere containment, since a label that merely fits is exactly
what a coordinate list hides and a reader sees immediately. `dotnet test tests/Ui.Tests` and `tests/Firewall.Tests` green. Write-up in `src/Ui/RESOLVED.md`.
