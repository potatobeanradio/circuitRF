# Brief SAR-2 — Tissue materials, evaluated at the analysis frequency

**Series:** `brief-sar-0-overview.md` (L2, L6, O2) · **Tag:** `R-sar2-<m>`
**Depends on:** —
**Area:** `src/Design/Layout/TechModel.cs` (`TechMaterial`), the `.cmat` reader, `src/Core` or `src/Engine` for the
dispersion formula (no domain types), `src/Design/resources/technologies/tissue-materials.cmat` (new),
`docs/design/sar.md` §4

---

## 1. Goal

A body tissue's permittivity and conductivity change strongly with frequency (muscle σ is about 0.7 S/m at 64 MHz and
about 1.0 S/m at 1 GHz). So a tissue cannot be one fixed εr and σ as every material record is today. Give a material
an optional **dispersion model** that is evaluated at the frequency a setup needs, and ship a tissue library built
from a published parametric model.

## 2. Requirements

**R-sar2-1 — A Cole-Cole dispersion on a material record.** `TechMaterial` gains an optional `ColeCole` block (the
pattern of every additive, nullable field in em-3d.md §4.1a, with no `FormatVersion` bump):
`EpsInf`, `SigmaIonic` (S/m), and up to four terms of `DeltaEps`, `TauSeconds`, `Alpha`. It gives
ε̂(ω) = ε∞ + Σ Δεₙ / (1 + (jωτₙ)^(1−αₙ)) + σᵢ/(jωε₀). εr and σ at a frequency are the real part of ε̂ and
−ωε₀·Im ε̂. A record with the block also keeps its plain `Epsr`/`SigmaSm`, which an older build reads. `check`
reports when those disagree with the model at 1 GHz by more than 1 % (em-3d.md §4.1a's rule for an edited number
that nothing reads).

**R-sar2-2 — Density and the thermal values.** Tissues carry `DensityKgM3` (it exists) and, for a later bioheat
series, `ThermalConductivity`, `SpecificHeat` (they exist), plus two new optional fields that nothing reads yet:
`PerfusionRate` and `MetabolicHeat`. They are stored and reported only; they are added now so the library is written
once.

**R-sar2-3 — Where the frequency comes from.** A setup that requests SAR (SAR-6) names one **SAR frequency** per
evaluation (L6). Every dispersive material in the problem is evaluated there, once, before lowering, so each solver
still sees plain εr and σ. A setup without SAR evaluates a dispersive material at its sweep's centre frequency, and
the run's notes say so in one line. Nothing is evaluated per FDTD time step.

**R-sar2-4 — The tissue library (O2).** `tissue-materials.cmat`, shipped beside `generic-materials.cmat`, offered to
a workspace the same way. It holds the tissues of Gabriel, Lau and Gabriel, "The dielectric properties of biological
tissues: III. Parametric models for the dielectric spectrum of tissues", *Phys. Med. Biol.* 41 (1996) 2271–2293:
their four-term parameters, entered from the paper and **cited on every record's `Source`**. Densities and thermal
values come from published compilations, each with its own citation; no record carries a number without a source.
Minimum set: skin (dry and wet), fat, muscle, cortical and cancellous bone, brain grey and white matter,
cerebrospinal fluid, blood, eye (vitreous), lung, heart, liver, kidney. Names are plain (`Muscle`, `Fat`), with a
`Tissue/` prefix only if a name collides with an existing material.

**R-sar2-5 — Phantom materials.** Records for the liquids and gels used in phantoms. Head and body tissue-simulating
liquids for phone SAR are given as target εr and σ **at named frequencies**; values come only from free, public
regulator documents, cited (L2), interpolated between the tabulated frequencies and refused outside them. Also a
saline gel of the conductivity commonly used for implant heating tests, and the three tissues of each openEMS
fallback phantom **at the values its script states**, so SAR-7 and SAR-8 match without a model evaluation in between.

**R-sar2-6 — A user's own table (O2, optional).** An importer reads a tissue-parameter table the user has downloaded
themselves (CSV: name, ε∞, σᵢ, four Δε/τ/α, density) into a workspace `.cmat`. Nothing in the repository contains such
a table.

**R-sar2-7 — Readout.** The material editor shows a dispersive material's εr and σ at the active setup's SAR
frequency, labelled "at f", and a small εr/σ-versus-frequency plot. `explain --materials` reports the evaluated values
and the frequency they were evaluated at.

## 3. Not in this phase

Debye or Lorentz fitting for broadband FDTD (overview §8); Palace (it reads the same evaluated values when its series
comes).

## 4. Gates (minimal)

- `ColeColeTests`: muscle, fat and cortical bone at 64, 128, 298, 900 and 1800 MHz against the values printed in the
  paper's own figures and tables, to the precision the paper prints. One `[Theory]` with those rows only.
- `TissueLibraryTests`: the shipped `.cmat` loads, every record has a `Source`, and no record's plain numbers disagree
  with its model.
- The `.cmat` round-trip and an older-build read (plain numbers only) unchanged.
