# Brief 2 — named materials and 3D bodies in the technology

**Series:** [3D EM, first series](brief-em3d-0-overview.md) · **Tag:** `R-em3d2-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §4.1a
**Area:** `src/Design/Layout/TechModel.cs`, `TechPersistence.cs`, `TechValidation.cs`,
`src/Design/resources/technologies/*.ctech`, `src/Ui/Layout/StackupLayerRowViewModel.cs`,
`src/Ui/Controls/StackupInlineEditor.cs`, `src/Cli/Check.cs` (through `TechValidation` only)
**Depends on:** — · **Blocks:** 3, 4

---

## 0. What this brief delivers

The `.ctech` gains three additive, nullable things: a **`Materials`** list, a **`Material`** name on
a stackup entry, and a **`Bodies`** list for 3D-only solids. No solver reads `Bodies` yet (brief 3
does). **No planar answer changes**, and that is this brief's main gate.

---

## 1. `R-em3d2-1` — `Technology.Materials`

**`R-em3d2-1a`** A new `TechMaterial` class beside `TechConstant` in `TechModel.cs`, with:

| Field | Type | Meaning |
|---|---|---|
| `Name` | string | the key; unique within the technology, compared **ordinal, case-insensitive** (as `WireMaterials.ByName` already does) |
| `Epsr` | double? | relative permittivity |
| `EpsrTensor` | double[3]? | optional xx/yy/zz; when present, `Epsr` is not read by a 3D solver |
| `TanD` | double? | loss tangent |
| `Mur` | double? | relative permeability |
| `Sigma20` | double? | conductivity at 20 °C, S/m |
| `Alpha20` | double? | temperature coefficient of resistance at 20 °C, 1/K |
| `ThermalK` | double? | W/(m·K), for F3; carried now, read by nothing yet |
| `DensityKgM3` | double? | for F3 and for `WireMaterial` parity |
| `SpecificHeat` | double? | J/(kg·K), for F3 |

Null means *not stated*. A material used as a dielectric needs `Epsr`; one used as a conductor
needs `Sigma20`. That requirement is a `check` error at the point of **use** (§4), not a property of
the material, because gold is a conductor in one place and nothing in another.

**`R-em3d2-1b`** `Technology.Materials` is a `List<TechMaterial>`, **omitted from the file when
empty**. Every `.ctech` written before this round-trips byte-identically, and there is **no
`FormatVersion` bump**. That is the rule `DeviceRules` and `Constants` already follow.

**`R-em3d2-1c`** `k(T)` is **not** added in this brief. The note allows it "optionally" for F3, and
F3 can add it without breaking anything. Adding it now would be a field that nothing reads and that
`check` cannot validate.

---

## 2. `R-em3d2-2` — `StackupLayer.Material`, resolved on read

**`R-em3d2-2a`** `StackupLayer.Material` is a `string?`, omitted when null.

**`R-em3d2-2b` Resolve on read, in the one loader** (overview §1b). `TechPersistence.Deserialize`,
after constructing the model, overwrites each named entry's own `Epsr`/`TanD`/`Mur` (dielectric)
or `SigmaSm` (conductor, from `Sigma20`, **at 20 °C**, overview §1c) with the named material's
values. It must be the **single** place this happens:
- No reader may consult `Material` to pick values. A source scan (gate 5) finds any read of
  `StackupLayer.Material` outside `TechPersistence`, `TechValidation`, the stackup editor and brief
  3's generator.
- About 30 files read the four numbers directly today (`PlanarExtractor`, `CrossSectionExtractor`,
  `RlgcExtractor`, `StackupScene`, `GerberStackupMapping`, `PdnCavity`, …). **None of them
  changes.**

**`R-em3d2-2c`** A null field on the material leaves the entry's own number alone. A material
that states `Epsr` but not `TanD` overrides εr and keeps the entry's tanδ. That is the only reading
under which a partly-stated material does something predictable, and `check` reports it at info
(§4) so it is never silent.

**`R-em3d2-2d`** An entry naming a material the technology does not define keeps its own numbers
and is a `check` **error** listing the materials that exist. It is never a silent fallback, and
never an exception that makes the technology unloadable. `DeviceRule.Kind`'s comment in
`TechModel.cs` records why a typo must not make the whole file unopenable.

**`R-em3d2-2e`** Via entries: a via's `Material` names its fill or plating metal and resolves
`SigmaSm` the same way. `Fill`/`Plated`/`WallThicknessDbu` are unaffected.

---

## 3. `R-em3d2-3` — `Technology.Bodies`

**`R-em3d2-3a`** A new `TechBody` class:

| Field | Type | Meaning |
|---|---|---|
| `Name` | string | unique among bodies **and** stackup entries, because brief 3 names solids by it |
| `Material` | string | a `Materials` name; required |
| `SitsOn` | string | a stackup entry name; the body's bottom face is that entry's top |
| `ThicknessDbu` | long | its height |
| `OutlineLayers` | `List<LayerKey>` | the drawing layers whose shapes give its outline; **empty means the whole problem laterally** (an overmold) |

**`R-em3d2-3b`** `Technology.Bodies` is omitted when empty, with no `FormatVersion` bump. It is a
separate list, not a new `StackupKind`, so an older build ignores it and the planar extractor has
nothing to skip (§4.1a).

**`R-em3d2-3c`** **The planar extractors must never see a body.** Gate 4 asserts that adding an
overmold body to a shipped technology leaves every planar `.cem` in the repo producing an
identical `.sNp`.

---

## 4. `R-em3d2-4` — `check`

All of this goes in `TechValidation.Analyze`, which `check` already calls. `check` writes no rule of
its own (CLAUDE.md, the `check` verb's standing rule).

| Id | Severity | When |
|---|---|---|
| `tech.material.unknown` | error | a stackup entry or body names a material not in `Materials`; lists the real names |
| `tech.material.duplicate` | error | two materials share a name, case-insensitively |
| `tech.material.missing-property` | error | a dielectric entry names a material with no `Epsr`, a conductor entry one with no `Sigma20`, a body one with neither |
| `tech.material.partial` | info | a named material leaves one of the entry's four numbers to the entry (§2c) |
| `tech.material.disagrees` | **warning** | the **raw file's** entry numbers differ from the named material's |
| `tech.body.sits-on-unknown` | error | `SitsOn` names no stackup entry |
| `tech.body.name-clash` | error | a body name equals another body's or a stackup entry's |
| `tech.body.outline-layer-unknown` | error | an `OutlineLayers` key names no layer |

**`R-em3d2-4a` `tech.material.disagrees` reads the raw file**, before §2b's normalisation. After
normalisation the numbers always agree, so a check on the loaded model can never fire. That would be
the silent, inert rule the note specifically forbids ("an edited number that nothing reads must not
pass silently"). `TechValidation` gets a raw entry point for this one rule, and gate 3 proves it
fires.

---

## 5. `R-em3d2-5` — the shipped technologies and the editor

**`R-em3d2-5a`** Each shipped `.ctech` gains a `Materials` list holding the four bond-wire metals
with the values in `src/WBond/Materials.cs` (gold, aluminium, copper, silver: σ₂₀, α₂₀, density),
plus that technology's own dielectric and conductor as named materials.
**Its stackup entries do not name them in this brief.** Naming would change no number, but it
would change the files' bytes and every downstream golden with them, for no behaviour. Brief 3 is
where a named material is first read by something new.

**`R-em3d2-5b`** `WireMaterials.All` stays in code for kernel W. The technology's copies are what
brief 4 reads in a 3D setup (§4.1a: "wire metals resolve through the technology in a 3D setup"). A
test holds the two lists equal, so they cannot drift before a later brief retires the code copy.

**`R-em3d2-5c`** The stackup editor (`StackupLayerRowViewModel`, `StackupInlineEditor`). A row
gains a material picker (the technology's `Materials`, plus *none*). While a material is named, the
row's four numbers **display the material's values and are read-only**, with a tooltip naming the
material. Choosing *none* makes them editable again and keeps the values that were showing. Saving
therefore writes numbers equal to the material's (§4.1a).

**`R-em3d2-5d`** A `Materials` table in the technology editor is **not** built in this brief. A
client authors materials by writing the `.ctech` (§4.6), and `circuitrf reference technology`
describes the new fields with no page to write (it is generated from the reader's types; check that
it is). The GUI table is a later, small brief. Say so in `src/Design/RESOLVED.md`.

---

## 6. Gate

`tests/Ui.Tests/Em3d/TechMaterialsTests.cs`. Run that class and `Firewall.Tests`, not the suite.

1. **Round-trip.** Every shipped `.ctech` and every `.ctech` under `examples/` and `testdata/`,
   loaded and saved, is byte-identical with the new code (proves omit-when-empty).
2. **Resolve on read.** An entry naming a material with `Epsr = 4.4` and its own `Epsr = 3.0` loads
   as 4.4; `PlanarExtractor` sees 4.4 (assert on its extracted problem, not on a solve).
3. **`tech.material.disagrees` fires** on that same file through `check`, and **does not fire**
   after a GUI-path save.
4. **The planar gate, which is the important one.** For every `.cem` under `examples/`, extract
   it before and after adding (a) a `Materials` list and (b) an overmold `Bodies` entry to its
   technology. The **extracted problem** each planar extractor hands its kernel must be identical,
   field by field. Compare the extraction, not a solve: the kernels are deterministic given their
   input, and a solve per example would make this gate minutes long for no extra coverage. **One**
   small example is also solved end to end and its `.sNp` compared byte for byte, apart from the
   provenance timestamp, to prove the extraction comparison is not missing a path.
5. **One door.** Source scan: no read of `StackupLayer.Material` outside the files §2b lists.
6. **Every `check` id** in §4 has one test.
7. **Code and technology agree** on the four wire metals (§5b).
8. `circuitrf reference technology` output contains `Materials`, `Material` and `Bodies`.

## 7. Scope

- **No 3D solid is generated.** Brief 3.
- **No `k(T)`, no thermal solve.** F3.
- **No change to `.wBond` or kernel W.** Brief 4 touches the `.wBond`; kernel W never changes.
- **No materials editor table in the GUI** (§5d).
