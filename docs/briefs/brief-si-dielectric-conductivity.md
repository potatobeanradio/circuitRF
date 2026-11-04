# Brief SI-SIG — a dielectric's conductivity reaches the solvers, for a silicon substrate

**Tag:** `R-sisig-n`
**Area:** `src/Engine/Mom` (`EmProblem`, `SpectralGreens`, `PlanarSolve`, `LayeredMedium`), `src/Design/Layout/Em/PlanarExtractor.cs`, `src/Design/Layout/Em3d/Em3dLayoutSolids.cs`, `src/Design/Layout/TechPersistence.cs`, `TechValidation`, `src/Design/Layout/PCells/SubstrateResolver.cs`, `src/Ui/Layout/StackupLayerRowViewModel.cs`
**Depends on:** nothing. [TFR-V](brief-tfr-via-contact-film-level.md)'s silicon demo uses it if this lands first, and states a constant loss tangent if not.

---

## The gap

**A `SigmaSm` on a Dielectric entry is carried and read by nothing, and nothing says so.**

| Reader | What it does with a dielectric's conductivity |
|---|---|
| Planar extractor | Drops it. `EmMaterial(EpsR, TanD, MuR)` has no field for it, so `EpsComplex` is `εᵣ(1 − j·tanδ)` |
| 3D from a layout | Drops it. `Em3dLayoutSolids.DielectricMaterial` writes `SigmaSm: 0` |
| 3D, a drawn body | Keeps it. `BodyMaterial` passes `Sigma20`, and Palace's config writes `Conductivity` beside `LossTan` |
| Named material | `TechPersistence` copies εᵣ, tanδ and μᵣ onto a dielectric entry, and not σ |
| Technology editor | A dielectric's card has no field for it, and the drawing's label omits it |
| Schematic line models | `SubstrateResolver` averages tanδ by thickness, with no σ |

So a body drawn in a `.c3d` and the same material in a stackup give different answers, with no message.

**A constant tanδ cannot stand in for it.** Conduction loss is tanδ = σ/(ωε₀εᵣ), which falls as 1/f. For silicon, εᵣ 11.9, calculated:

| Resistivity | σ | tanδ at 1 GHz | 5 GHz | 40 GHz | Field decay depth at 5 GHz |
|---|---|---|---|---|---|
| 10 kΩ·cm | 0.01 S/m | 0.015 | 0.0030 | 0.00038 | 1.8 m |
| 1 kΩ·cm | 0.1 S/m | 0.15 | 0.030 | 0.0038 | 18 cm |
| 10 Ω·cm | 10 S/m | 15 | 3.0 | 0.38 | 2.7 mm |
| 0.01 Ω·cm | 1e4 S/m | 15,000 | 3,000 | 380 | 71 µm |

Across a 1 to 40 GHz sweep, any single value is wrong by up to a factor of 40 at one end. The shipped `High-resistivity silicon` states 0.0015, its value at 10 GHz for 10 kΩ·cm, and its `Source` says so.

**The substrates differ in kind, not just in degree:**

- **High resistivity:** a lossy dielectric, and the wave travels in it.
- **Moderate (about 10 Ω·cm):** the silicon conducts while the oxide does not, so a line over oxide slows down sharply. Reading this as a dielectric gives a wrong phase velocity, not only a wrong loss.
- **Low (about 0.01 Ω·cm, typical under an LDMOS device):** the silicon behaves increasingly like a lossy ground plane. Its skin depth is 71 µm at 5 GHz and 25 µm at 40 GHz, in a 100 µm substrate, so return current moves from the backside metal into the silicon as frequency rises.

## Why it is more than one field

**The risk is the calibration.** Below about 26 GHz the planar de-embedding takes γ from static capacitances: `PlanarSolve` computes C_pul once, as an electrostatic solve, and uses γ = jω√(LC). A conducting substrate makes that capacitance depend on frequency, so a single static number cannot describe the moderate regime, where the error is largest. **It may need a different route below some σ, and this may be the bulk of the work.**

**A sampled Green's function may be affected too.** `SpectralGreens` takes `EpsComplex` at each frequency, so the term itself is straightforward, and the low-resistivity case then has a large |ε| and short field decay. How the fit's sampling path and `SingularExtraction`, whose image coefficient is (1 − ε)/(1 + ε), behave there is unmeasured. `InteriorStaticGreens` also reads `EpsComplex` and calls itself static.

**`EmMaterial` is a record used across the kernel, and `EpsComplex` takes no frequency.** Adding σ means every caller takes ω, or the medium is rebuilt per frequency with an equivalent tanδ.

## What to build

### R-sisig-0 — measure before designing

No code in the product. A throwaway harness passes an equivalent per-frequency tanδ = tanδ₀ + σ/(ωε₀εᵣ) to the medium, on a 50 Ω line over 2 µm of oxide on 100 µm of silicon with backside ground. Sweep 1 to 40 GHz at the four resistivities in the table, with a 3D reference from Palace, whose config already takes `Conductivity`, by drawing the silicon as a body.

Report for each: γ and Z_c against Palace; whether the calibration warns, is passive and reciprocal; run time; and which part fails first.

**Stop and report if the quasi-static route is far off in the moderate regime and no replacement is evident.** The brief's scope then narrows to what works, or a calibration brief comes first. Scope it short: a line, few points, and no whole-board runs.

### R-sisig-1 — the model and the file

- **`StackupLayer.SigmaSm` is meaningful on a Dielectric.** No new field and no `FormatVersion` change. Default 0, and every technology that states none stays identical to the bit.
- **A `TechMaterial` states `Sigma20` beside `Epsr`.** `TechPersistence` copies it onto a dielectric entry on read, as it does εᵣ and tanδ. A material with `Sigma20` and no `Epsr` stays a conductor, which is what `BodyMaterial` already decides. `Alpha20` for a dielectric is out of scope here, because semiconductor conductivity's temperature law is not a metal's.
- **`High-resistivity silicon` keeps its values.** Add `Silicon, 10 Ω·cm` and `Silicon, 0.01 Ω·cm` to `generic-materials.cmat`, with tanδ 0 and each `Source` stating the derivation. Adding them is additive to a library.
- **`TechValidation`:** a dielectric with both a tanδ and a σ is legal, because they are different mechanisms, and is stated in the card. A negative σ is an error.

### R-sisig-2 — the planar kernel

Per R-sisig-0's result. A dielectric's complex permittivity becomes εᵣ(1 − j·tanδ) − j·σ/(ωε₀), and σ = 0 is bit-identical, which a test holds on the existing goldens.

- **0 Hz:** the conduction solve of the conductors is unchanged. The silicon is under oxide, so it opens no DC path. A via into it is out of scope.
- **A run says what it did:** a note stating the conductivity, the tanδ it amounts to at the sweep's ends, and which regime the substrate is in.
- **Any regime the calibration cannot serve is a refusal or a warning naming the frequency range,** on the pattern of the de-embedding reliability note. It must not read as a clean result.

### R-sisig-3 — the 3D route

- `DielectricMaterial` passes the entry's σ. Palace writes it as it does for a body.
- **EMerge** treats a non-conductor above 1e4 S/m as a conductor, and refuses it with a message (`EmergeLoweringRules`). The 0.01 Ω·cm row sits exactly on that line, so measure it and state the result. Do not change the limit here.
- **openEMS:** check that `CsxcadWriter` writes a dielectric's conductivity, and fix it if not.
- **The skin depth sets the mesh.** At 0.01 Ω·cm it is 25 µm at 40 GHz in a 100 µm substrate. Report what that costs, and whether the mesh sizing already accounts for a lossy volume.

### R-sisig-4 — the editor

- **The dielectric's card gains a conductivity row,** with its number and a **combobox** for the unit: `S/m`, `Ω·cm`. Stored in S/m. Choosing a material fills it, and a `—` in the box means none.
- **The material combobox** lists the new silicon entries. No typed material names.
- **The drawing's label states σ only where it is stated,** as a film's optional values do, so every existing technology's picture is unchanged.
- **Render it with `tools/UiSnapshot --ctech` and inspect it.**

### R-sisig-5 — the other readers

- **`SubstrateResolver` and the line models.** `MLIN` and its closed forms assume a low-loss substrate. On a σ above a stated bound, the part and the line calculator say the closed form does not apply and point at an EM run, rather than quietly reading tanδ alone.
- **Impedance readouts and the `impedance` tool:** the same statement, and σ in the printed substrate line.
- **Provenance:** σ enters the geometry hash only when non-zero, so no existing `.snp` goes stale.

### R-sisig-6 — demo and docs

- **TFR-V's `rfic-Si_3LM_TFR_100um.ctech`** states a conductivity once this lands, if it has landed first. Otherwise this brief changes that entry and its example's recorded numbers. `mmic-GaAs_2LM_100um.ctech` is not edited.
- **`docs/user/src/reference/stackup.md`:** a section on a conducting substrate, with the three regimes and when to state σ rather than tanδ. `materials.md` for the new entries, `mom-engine.md` for the calibration's limits. Regenerate those pages.
- **Findings go in `src/Engine/Mom/RESOLVED.md` and `src/Design/RESOLVED.md`.**

## Order and gates

R-sisig-0, and stop to report. Then 1 and 2, then 3, then 4, 5 and 6.

| Gate | Holds |
|---|---|
| 1 | Every technology with no dielectric σ: planar and 3D goldens are unchanged to the bit, and no `.snp` provenance hash moves |
| 2 | A σ reaches the medium: complex ε at a stated frequency, against the formula, at the Green's function's input |
| 3 | At 1 kΩ·cm, |ΔS| ≤ 0.02 between planar and Palace across the sweep, on a committed reference |
| 4 | At 10 Ω·cm and 0.01 Ω·cm: within 0.02, or a refusal or warning naming the range. Never a clean result outside it |
| 5 | A body in a `.c3d` and the same material in a stackup give the same answer in Palace |
| 6 | A `.ctech` with a dielectric σ round-trips through the editor byte for byte, and both unit choices store S/m |
| 7 | `MLIN` on a conducting substrate says the closed form does not apply |

One test per claim, under the ~5 s `Category=Benchmark` threshold. No new timing benchmarks, and solvers do not run in CI: references are committed.

## Not in this brief

- **Active-device substrate coupling** and any junction, depletion or carrier model. Conductivity here is a fixed bulk number.
- **Temperature dependence of a semiconductor's conductivity.**
- **A doped layer with a profile.** A stack of uniform dielectric layers can approximate one, which the model already allows.
- **A via or contact into the silicon** that carries conduction current to it.
- **Thermal-solve use of these materials**, which already read `ThermalK` from the library.
