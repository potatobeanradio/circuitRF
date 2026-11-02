# Brief 73 — thermal materials and the document

**Series:** [3D thermal, fifth series](brief-em3d-71-thermal-overview.md) · **Tag:** `R-em3d73-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §4.1a, §9.2; overview §1b–§1e, §1i, §1j, D1, D4, D11
**Area:** `src/Design/resources/technologies/generic-materials.cmat`, `src/Design/Layout/TechModel.cs`,
`src/Design/Layout/MaterialValidation.cs`, `src/Design/ThreeD/C3dDocument.cs` (+ persistence, resolver),
`src/Design/Layout/Em/EmSetupModel.cs` (+ persistence), `src/Engine/Em3d/Em3dProblem.cs`
(`Em3dProblemType.Thermal`), `src/Cli/` (`check`, `explain`, the reference pages), `tests/`
**Depends on:** 72 (the material tables) · **Blocks:** 74, 75, 76

---

## 0. What this brief delivers

Everything a thermal run **reads**, written down and checked — and nothing that solves:

1. **Materials that carry thermal properties** — every record in the shipped library, plus the materials the
   three scenarios are made of (§1).
2. **How σ(T) and k(T) resolve** — the table wins (§2).
3. **Interface resistances** in the technology (§3).
4. **The document's thermal places**: heat sources, probes, mesh regions and contact overrides in the `.c3d`
   (§4).
5. **The thermal setup** (§5).
6. **`check` and `explain`** over all of it, and the generated reference pages (§6).

After this brief a thermal setup can be written by hand, is checked, is explained, and is refused by `em`
with *"the thermal solver is not built yet (brief 74)"* — never silently run as an EM setup.

---

## 1. `R-em3d73-1` — the shipped materials

**`R-em3d73-1a`** Every record in `generic-materials.cmat` gains `ThermalK` (W/(m·K)), `DensityKgM3` where
absent and `SpecificHeat` (J/(kg·K)), each with its reference added to the record's `Source`. The four
wire metals — **gold, copper, aluminium and silver** — gain `ThermalKVsTemp` and `SigmaVsTemp` tables from
brief 72 §4, 20 °C to below each one's melting point, with the melting point in `Source`. **No existing electrical value moves**: `Sigma20`, `Alpha20`, `Epsr`, `TanD` are unchanged, and
so is every existing golden (overview gate 3). A shipped technology that repeats a library name repeats the
new values too (the brief-53 §8a rule: names shared carry identical values).

**`R-em3d73-1b`** New records, each with a cited `Source` and a colour:

| Name | Why | Notes |
|---|---|---|
| Silicon carbide (4H, semi-insulating) | scenario 2 substrate | k(T) table — its conductivity falls steeply with temperature |
| Gallium nitride | scenario 2 epitaxy | k(T) table |
| CVD diamond | heat spreaders | constant k is acceptable |
| Gold–tin solder (80/20) | die attach | |
| Sintered silver | die attach | the `Source` states the porosity the value belongs to |
| Copper–tungsten, copper–molybdenum | package flanges | name the composition in the record name |
| Mould compound (generic) | overmold | the glass-transition temperature in the `Source` as a note; values are a typical range's midpoint and say so |
| Thermal interface material (generic) | board to heatsink | the same honesty as the mould compound |
| Solder (generic lead-free) | board attach | |

Names are generic; **no manufacturer or product name** appears anywhere (CLAUDE.md).

**`R-em3d73-1c`** A material with no `ThermalK` used by a solid in a thermal run is a **refusal naming the
material and the object**, never a default. Air never needs one (it is not meshed, overview §1b).

## 2. `R-em3d73-2` — resolving σ(T) and k(T): the table wins

**`R-em3d73-2a`** One resolver, in `src/Design/Layout`, answers σ(T) and k(T) for a material record, used by
the thermal lowering and by nothing that exists today (so no current answer moves):

- **k(T)**: `ThermalKVsTemp` when present — piecewise-linear in T, **held constant beyond its ends** with a
  run note naming the material and the temperature reached; else `ThermalK`, constant.
- **σ(T)**: `SigmaVsTemp` when present (same interpolation and end rule); else
  `Sigma20 / (1 + Alpha20·(T − 20))` — exactly `WireMaterial.SigmaAt`'s formula, so a wire's σ at 85 °C is
  the number wBond already uses.
- The derivatives dk/dT and dσ/dT come from the same resolver (piecewise-constant slopes for a table), for
  brief 77's Jacobian.

**`R-em3d73-2b`** `check` **warns** when a record states both a table and its coefficient/constant and they
disagree at 20 °C by more than 1 %, naming the record and both numbers. This retires the *"deliberately
undecided"* info `MaterialValidation` prints today (owner decision, 2026-09-27); the `TechModel` doc
comments on `SigmaVsTemp` and `ThermalKVsTemp` are rewritten to say what is now decided.

## 3. `R-em3d73-3` — interface resistances in the technology

**`R-em3d73-3a`** `TechModel` gains `ThermalInterfaces`: a list of
`{ "MaterialA": …, "MaterialB": …, "ResistanceM2KW": …, "Source": … }`. The pair is unordered. Additive and
nullable, no `FormatVersion` bump (em-3d.md §4.1a's pattern). A `.cmat` may carry the same list, read by the
same reader, resolved with the same duplicate rule as materials: equal values merge, different values are a
`check` error and a refusal to load.

**`R-em3d73-3b`** The shipped library gains **one** record — GaN on SiC — with a cited value and a `Source`
that says such values vary by growth process by several times, so the user should replace it with their own.

## 4. `R-em3d73-4` — the document's thermal places

Four new lists on `C3dDocument`, beside `Ports`, each **omitted from the file when empty** so every existing
`.c3d` round-trips byte for byte. Every dimension is a DBU integer or an expression (brief 51), as everywhere
in the `.c3d`. Names are unique across the document's objects, ports and these lists (`NameValidator`).

**`R-em3d73-4a` `HeatSources`** — a named place heat is put:
- `Sheet`: `Plane`, `Offset` and either `Rect` or `Outline`(+`Holes`), exactly as a `C3dSheet` spells them;
  it must lie **inside or on** one solid (checked at elaboration, §6). A source straddling two solids is
  refused: the power split between them would be a guess.
- `Solid`: the name of an object (`U1/channel` allowed) — a volumetric source over that solid.
- `Power`: an optional **default** expression, W (`"Pdiss"`, `"0.5 W"`); the setup may override it (§5).
  `Density`: `Total` (W, the default) or `PerArea` (W/m², a sheet) / `PerVolume` (W/m³, a solid).

**`R-em3d73-4b` `Probes`** — a named place temperature is read:
- `Point` (a 3D point), `Face` (`object/face`, the §6.4 naming, split pieces folded), `Solid` (an object),
  `Spot` (a disk: a face, a centre on it and a diameter — the IR microscope's spot, overview §1i), `Line`
  (two points; the result is T along it), `Wire` (a wire's name, or an array element `w1[3]`; the result is
  T(s) and its maximum).
- `Stat`: `Max`, `Min`, `Avg` (area- or volume-weighted) — `Point` and `Line` take none.
- `LimitC`: optional; the result flags where it is crossed (D11).

**`R-em3d73-4c` `MeshRegions`** — a named box (`Min`, `Size`, as `C3dBox`) with a target element size
(`SizeUm`, an expression) and an optional `Grading`. Used by **every** 3D setup that meshes with Gmsh
(brief 74 lowers it for thermal **and** Palace). openEMS ignores it and says so in its notes when one exists.

**`R-em3d73-4d` `ContactResistances`** — `{ "Between": ["die", "flange"], "ResistanceM2KW": … }`: overrides
the technology's material-pair value for the contact between two named objects (overview §1e). The two must
touch (checked at elaboration).

**`R-em3d73-4e`** None of these reaches an EM lowering. A test lowers a `.c3d` carrying all four (bar the
mesh region) to Palace and to openEMS and compares byte for byte with the same document without them.

## 5. `R-em3d73-5` — the thermal setup

**`R-em3d73-5a`** `Em3dProblemType` gains `Thermal` (D1). A thermal setup is an embedded setup in the `.c3d`
with `"Problem3D": "Thermal"`, **no `Solver3D`** (stating one is refused with a sentence: the thermal solver
is circuitRF's own), and a `Thermal` section. Every `switch` over `Em3dProblemType` in the Palace and openEMS
paths handles `Thermal` by refusing — the run service never sends it there, and the compiler finds every
switch. `C3dSetups.Read` stops calling a thermal setup *"a planar analysis"*. A `.cem` naming
`Problem3D: Thermal` is refused (D1: thermal lives in the `.c3d`).

**`R-em3d73-5b` — the `Thermal` section**, every value an expression in the document's variables:

```jsonc
"Thermal": {
  "Sources":    [ { "Name": "fingers", "Power": "Pdiss" } ],           // overrides a source's default; unnamed sources use theirs
  "Boundaries": [ { "Face": "heatsink/zmin", "Kind": "FixedT", "TempC": "Ths" },
                  { "Face": "*exposed*",     "Kind": "Convection", "H": "10", "AmbientC": "25" } ],
  "Currents":   [ ],                                                    // brief 77/78 fill this in; empty here
  "Sweep":      [ { "Var": "Pdiss", "Start": "1", "Stop": "10", "Points": 10 } ],   // up to two axes, a product
  "Measures":   [ "Rth = (Tmax(die_top) - Tavg(flange_bot)) / Pdiss" ],
  "Mesh":       { "Order": 2, "SizeFromSources": 8, "MinThroughThickness": 2, "Grading": 1.4 },
  "Balance":    { "SigmaOfT": true, "KOfT": true, "Tolerance": 1e-6, "MaxIterations": 30 }
}
```

- `Kind`: `FixedT` | `Convection`; `Face` is a named face or `*exposed*` (every face not touching another
  solid and not otherwise conditioned). At least one `FixedT` or `Convection` is required (overview §1b).
- A `Sweep` variable that **any geometry expression** reads is refused in this series: a geometric sweep
  re-meshes per point, which brief 74 does not do. The refusal names the variable and the first dimension that
  reads it.
- `Measures` use the functions `Tmax`, `Tmin`, `Tavg`, `T` (a point or spot probe) over probe names, and any
  variable. They are parsed now (errors reported by `check`) and evaluated by brief 74.
- `Balance` is parsed and stored here; brief 74 reads `KOfT`, brief 77 reads both.

**`R-em3d73-5c`** The section is additive; an older build reading a `.c3d` with a thermal setup keeps it
verbatim in its unread keys (the existing `Unread` mechanism) and lists it as unreadable, never drops it.

## 6. `R-em3d73-6` — `check`, `explain`, reference pages

**`R-em3d73-6a` `check`** reports, through validators the GUI will also call (CLAUDE.md: a rule in `check`
alone is a rule the application does not enforce):
- a heat source not inside one solid, or straddling two; a contact override between objects that do not touch;
  a probe face that does not exist; a wire probe naming no wire;
- a thermal setup with no `FixedT`/`Convection`; a boundary on a face that does not exist; a source override
  naming no source; a solid whose material lacks `ThermalK`;
- a sweep variable read by geometry; a measure that does not parse, or names an unknown probe;
- §2b's table/coefficient disagreement (warning).

**`R-em3d73-6b` `explain --analysis`** lists the thermal setup: sources with resolved powers at the first sweep
point, boundaries, interfaces in force (material pair or override, and its value), probes, the sweep in base SI
**with unit and scale** (CLAUDE.md), and the materials' k at 25 °C.

**`R-em3d73-6c`** `circuitrf reference 3d` and `reference technology` describe the new fields the day they exist
(they are generated from the types — every new member carries its `[Description]`).

## 7. Gates

1. **Byte identity**: every existing `.c3d`, `.ctech` and `.cmat` fixture round-trips unchanged; every Palace
   and openEMS golden is unchanged (§4e, overview gate 3).
2. **Resolver**: σ(T) from `Alpha20` equals `WireMaterial.SigmaAt` at 20, 85 and 200 °C to 1e-12; a table
   interpolates, holds at its ends, and reports the hold; the table wins when both exist.
3. **Round trip**: a `.c3d` with one of each new record and a full `Thermal` section reads and writes byte for byte.
4. **`check`**: one fixture per §6a finding, each named in the output; a clean thermal fixture exits 0.
5. **Refusals**: `em` on a thermal setup refuses with the brief-74 sentence; a `.cem` with `Problem3D: Thermal`
   refuses; a thermal setup stating `Solver3D` refuses.
6. **The four wire metals** — gold, copper, aluminium and silver — each carry a `ThermalKVsTemp` and a
   `SigmaVsTemp` table equal to brief 72's `testdata/thermal/metals/<metal>.csv`, ending below the metal's
   melting point; gold's k at 125 °C and 927 °C is within the tolerance brief 72 recorded against the owner's
   figures.

## 8. Scope

- **No solver, no mesh, no UI.** Brief 74 solves; brief 75 draws.
- **No existing number moves.**
- Findings in `src/Design/RESOLVED.md`, never `CLAUDE.md`.
