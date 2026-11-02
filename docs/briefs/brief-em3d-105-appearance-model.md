# Brief 105 — The appearance model: on a material, on an object, never in a solve

**Tag:** `R-em3d105-n` · **Series:** the realistic view ([overview 103](brief-em3d-103-overview.md)).
**Area:** `src/Design/Layout/TechModel.cs` (`TechMaterial` ~824, new `TechAppearance`), `MaterialLibraryPersistence.cs`,
`MaterialValidation.cs`, `MaterialLibraries.cs` (~300–306), `src/Design/resources/technologies/generic-materials.cmat`,
`src/Design/ThreeD/C3dDocument.cs` (`C3dObject` ~140–175, `C3dInstance` ~557–573), `C3dPersistence.cs`
(`SerializeForRun` ~177–195), `C3dRunDocument.cs` (`HashOf` ~247, the manifest), new
`src/Design/ThreeD/Appearance/AppearanceResolver.cs`, `src/Render/Scene3D/Scene3DBuilder.cs` (the slot),
`src/Cli/DocumentSchema.cs`, `src/Cli/ExplainEm3d.cs`, `docs/user/src/reference/drawing-in-3d.md`
**Depends on:** — (104 for the slot's stream only) · **Blocks:** 106, 108, 110, 111

## Why

The realistic view needs to know what each object looks like: how metallic, how rough, how see-through. That belongs to
**what it is made of** (40 copper objects should not need 40 edits), with an **override per object**, because plating is
usually not modelled. A copper trace that should look gold-plated is the common case.

None of it may reach a solver, and **none of it may mark a result out of date**. The second part is harder than it looks:
§0 shows that today, even editing a material's display `Color` in a `.cmat` makes every 3D run that read it stale.

## 0. What already exists — read this first

| Piece | Where |
|---|---|
| `TechMaterial`: one record for `.cmat` and a `.ctech`'s `Materials` block ("one record type, one contract") | `TechModel.cs` ~824; `MaterialLibraryPersistence.cs` ~12 |
| Display fields `Source` and `Color` (`#rrggbb`), excluded from the solver-equality comparison | `TechModel.cs` ~902–906; `MaterialLibraries.cs` ~300–306 |
| The 3D view takes a material's `Color` when stated, else its palette | `Scene3DBuilder.MaterialColour` ~219 |
| Display properties on a `.c3d` object: `Group`, `Hidden`, `Transparency` (brief 92); an operation carries them for its result | `C3dDocument.cs` ~140–175 |
| `SerializeForRun` strips them, so a run's kept document ignores them (brief 98) | `C3dPersistence.cs` ~177–195 |
| **Brief 87's manifest hashes each input file's RAW BYTES**, a `.cmat` included | `C3dRunDocument.HashOf` ~247; `tests/Ui.Tests/ThreeD/RunInputsTests.cs` ~46 |
| Roles: `Em3dRole { Dielectric, Conductor, Air }`; the scene's kinds `Conductor, Dielectric, Air, Body, Wire, Via, Sheet, Port, Boundary` | `Em3dProblem.cs` ~29; `Scene3DModel.cs` ~21 |
| The built-in library: Gold, Aluminium, Copper, Silver, nine dielectrics, five package materials | `generic-materials.cmat` |

## 1. `R-em3d105-1` — `TechAppearance`, on a material

- **a.** `TechMaterial.Appearance` (`TechAppearance?`), written after `Color`. Every field is optional (null = not stated):

  | Key | Type, range | Meaning |
  |---|---|---|
  | `BaseColor` | `#rrggbb`, sRGB | a metal's reflectance, a dielectric's body colour |
  | `Metallic` | 0–1 | 1 for a metal, 0 for a dielectric; in between only for blending |
  | `Roughness` | 0–1 | 0 mirror, 1 matte |
  | `Transmission` | 0–1 | see-through (glass, quartz, thin laminate) |
  | `Ior` | 1.0–3.0 | **optical** index, a visual value; never derived from εr (overview §1c) |
  | `Clearcoat` | 0–1 | a glossy layer over the body (solder mask, glossy laminate) |
  | `ClearcoatRoughness` | 0–1 | |
  | `AttenuationColor` | `#rrggbb` | the tint light picks up passing through |
  | `AttenuationDistance` | metres, > 0 | how far it travels before taking that tint (SI, as every `.cmat` number is) |
  | `Like` | a material name | take that material's resolved appearance first, then apply this record's own fields (§3c) |

  These are glTF 2.0's metallic-roughness parameters (overview D2), so 111 writes them through unchanged.
- **b.** `MaterialValidation`: out-of-range values are refused, naming the material, the key and the range. `#rrggbb`
  is parsed by the one function `MaterialColour` already uses: move it to Design and call it from both, rather than writing
  a second parser. A `Like` naming no material in scope is a **warning**, and the appearance falls through to the role
  default. A `Like` cycle is refused, naming the chain. Unknown keys inside `Appearance` are handled exactly as unknown
  material keys are today (check `MaterialValidation`, and do the same thing).
- **c.** The solver-equality comparison (`MaterialLibraries.cs` ~306) lists `Appearance` beside `Source` and `Color` as
  taking no part. A test holds it: two records differing only in `Appearance` are the same material to the 3D elaborator.

## 2. `R-em3d105-2` — on an object

- **a.** `C3dObject.Appearance` and `C3dInstance.Appearance` (`TechAppearance?`), `JsonPropertyOrder` after
  `Transparency`. **Field-wise**: each stated field overrides the material's resolved value, and the rest come through. A
  copper object with `{ "Like": "Gold" }` looks gold. A copper object with `{ "Roughness": 0.1 }` is polished copper.
- **b.** An operation carries it for its result, as it carries `Transparency` (same comment shape). A polyline has none.
- **c.** `SerializeForRun` clears it with the others (~179–195), so a kept document never holds it. `Normalised`'s re-read
  (brief 98) makes an older kept document compare equal. A test holds both.
- **d.** Instances: an instance's appearance applies, field by field, to every object inside it, and **a part's own stated
  field wins** because it is the more specific statement. This is not brief 92's rule: `Transparency` multiplies an
  instance's value onto each part's (brief 92 §1, ~35), and a colour or a roughness cannot multiply. Nested instances apply
  outermost first, so the innermost statement wins.

## 3. `R-em3d105-3` — `AppearanceResolver`, the only place a look is decided

`src/Design/ThreeD/Appearance/AppearanceResolver.cs` (Design, because the glTF writer and `explain` need it, and Design draws
nothing):

- **a.** Input: the technology (with its libraries), the material name, the object's role (§3b), and the object's (and
  instance's) override. Output: `ResolvedAppearance`, with **every field concrete and colours in linear space**, plus a
  per-field **provenance** (`object`, `instance`, `material 'Copper' (generic-materials.cmat)`, `Like 'Gold'`, `role default`)
  for `explain`.
- **b.** Role: the resolver takes `AppearanceRole { Conductor, Dielectric, Via, Wire, Body, Sheet }`. `Scene3DBuilder` maps
  its own `Scene3DKind` to it (Render references Design, not the other way round). Air, ports and boundaries have no
  appearance. The realistic view hides them by default, and a `Look` option shown draws them as the default view does
  (overview D11; 106 §2b).
- **c.** Order, per field: object → instance (innermost first, §2d) → material (its `Like` first, then its own fields) → `BaseColor`'s fallback to
  the material's `Color` → the role default. **Nothing is derived from εr, σ or any other physical value.**
- **d.** Role defaults, one table, named constants:

  | Role | Metallic | Roughness | Base colour when nothing is stated | Other |
  |---|---|---|---|---|
  | Conductor | 1 | 0.30 | the scene's conductor palette colour | |
  | Via | 1 | 0.25 | as conductor | |
  | Wire (bond wire) | 1 | 0.20 | as conductor | |
  | Dielectric | 0 | 0.50 | the scene's dielectric palette colour | `Ior` 1.5 |
  | Body / Sheet | by the material's role | | | |

  The palette fallback means the resolver needs the palette colour as an **input** (the builder passes it), never a copy of
  the palette.
- **e.** Pure, deterministic, cached per (technology version, material, role, override) and counted. `explain` and the
  Inspector (108) read its provenance; nothing else re-walks.

## 4. `R-em3d105-4` — the slot

`Scene3DBuilder` asks the resolver once per object, interns the result into the scene's **appearance table** (at most 256
entries, overview D16; equal resolved appearances share a slot), and writes the slot into the object's
`Scene3DShadeVertex.Slot` (104). An object past the 256th distinct appearance takes its role default's slot, and
`Scene3DModel.AppearanceFallbacks` counts it for the status line (106). The table is `Scene3DModel.Appearances`.

## 5. `R-em3d105-5` — appearance must not make a result stale

Brief 87's manifest hashes raw file bytes, so today any `.cmat` edit, a display `Color` included, marks every run that read
the file stale. This brief fixes that **for material libraries and technologies only**:

- **a.** For a `.cmat` or `.ctech` input, the manifest records the SHA-256 of a **canonical physics form**: the file read
  through its own persistence, every material's `Source`, `Color` and `Appearance` cleared, and serialised again. Other
  inputs keep the raw-byte hash.
- **b.** `ManifestVersion` 1 → 2, and each `C3dRunInput` says which kind of hash it holds. A version-1 manifest is compared
  exactly as today (raw bytes), so an old run is never newly called current by a rule it was not written under.
- **c.** A file that no longer parses is compared by raw bytes, and is therefore stale if changed, which is the safe
  answer.
- **d.** Gate (extends `RunInputsTests`): editing Gold's `Appearance` or `Color` in the workspace's `generic-materials.cmat`
  leaves a v2 run current, and editing Gold's `ThermalK` still lists the file as changed (the existing assertion ~46–47).

## 6. `R-em3d105-6` — the built-in library

`generic-materials.cmat` gains an `Appearance` on its metals, with **true reflectances** (sRGB, from linear F0):

| Material | `BaseColor` | `Roughness` |
|---|---|---|
| Copper | `#FAD1C2` | 0.35 |
| Gold | `#FFE39D` | 0.25 |
| Silver | `#FCFAF5` | 0.20 |
| Aluminium | `#F5F6F6` | 0.35 |
| Gold-tin solder, sintered silver, lead-free solder, Cu-W, Cu-Mo | nearest pure metal, adjusted by eye | 0.35–0.5 |

All with `Metallic: 1`. **Do not change `Color`.** The default view's copper stays the recognisable diagrammatic copper; a
true copper reflectance looks pale until it has something to reflect.

The dielectrics get visual values. Suggested starting points, to be tuned in 108's live editor and recorded as visual, not
measured:

| Material | Suggested appearance |
|---|---|
| Alumina | white body, roughness 0.45 |
| Fused silica | transmission 0.95, `Ior` 1.46, roughness 0.02 |
| CVD diamond | transmission 0.9, `Ior` 2.42 |
| PTFE | white, roughness 0.6 |
| FR-4 | yellow-green body, transmission 0.3, roughness 0.4 |
| Polyimide | amber attenuation, transmission 0.6 |
| Silicon, GaAs | dark grey, roughness 0.15 |
| Mould compound | near-black, roughness 0.55 |

**Owner decision D1** below covers shipping these.

If a test holds the examples' copies of `generic-materials.cmat` equal to the resource, update them together; otherwise
leave the examples alone.

## 7. `R-em3d105-7` — schema, `check`, `explain`

- **a.** `DocumentSchema`: `Appearance` on an object and an instance in the `.c3d` table (beside `Transparency`, ~463), and
  in the material record's key list with the table in §1a.
- **b.** `check` reports §1b's refusals and warnings through `MaterialValidation`, and writes no rule of its own.
- **c.** `explain x.c3d --object <name>` (or whatever spelling `ExplainEm3d` already uses for an object) prints the resolved
  appearance with each field's provenance (§3a).

## 8. Gate

1. **Round trip.** A `.cmat` with every appearance key, and a `.c3d` with object and instance overrides (including `Like`),
   load and save byte-identical.
2. **Validation.** Each range refusal, `Like` unknown (warning), `Like` cycle (refusal): table-driven.
3. **Not a solver input.** The equality comparison ignores `Appearance`. `SerializeForRun` holds no `Appearance`. An old
   kept document holding one normalises equal.
4. **Resolver order.** One table-driven test covers every precedence step and every role default, plus provenance strings.
   No physical value moves a result (vary εr and σ and assert the output is unchanged).
5. **Slots.** Equal appearances share a slot. The 257th distinct one falls back and is counted.
6. **Staleness** (§5d), plus the v1-manifest behaviour.
7. **Library.** Copper's resolved base colour in linear space is (0.955, 0.638, 0.538) within 1/255. Copper's `Color` is
   unchanged.

Run only the classes touched (`--filter`), plus `Firewall.Tests --no-build`.

## Decisions — settled by the owner, 2026-10-04 (each as recommended)

- **D1 Ship dielectric appearances in the built-in library?** *Recommended:* yes, as visual values, tuned once 108's live
  editor exists. *Alternative:* metals only, with dielectrics on role defaults.
- **D2 `Like`.** *Recommended:* in. It is how a plated part is spelled without restating four numbers. *Alternative:* the
  object restates the fields.

## Docs

`drawing-in-3d.md`: a short "Appearance" section: what the keys mean, that nothing physical is read from them, that they
never mark a result out of date, and the plated-trace example with `Like`. Edit doc sources only; DocGen runs at the end
of the series.

## On completion

Record in `src/Design/RESOLVED.md`: the raw-byte staleness finding and the canonical-form fix, and the resolver's order.
Never CLAUDE.md. Do not commit unless the owner asks.
