# Brief AS-2 — TLIN's physical form: length, εeff and per-length loss

**Series:** `brief-artsch-0-overview.md` (D13) · **Tag:** `R-as2-<m>`
**Depends on:** — (independent of every other phase)
**Area:** `src/Core/Devices/TLineModel.cs`, `src/Core/Devices/ComponentModelFactory.cs` (`CreateTLineModel`),
`src/Design/Schematic/ComponentTypeRegistry.cs`, `src/Ui/ViewModels/ParameterEditorViewModel*.cs` (the entry-mode
switch, modelled on MKLOPF's), `docs/design/linear-engine.md` (TLIN section), `docs/user/src` (TLIN reference row)

---

## 1. Goal

TLIN today is specified by an **electrical angle** `E` at a reference frequency `F`. A line read off artwork has a
**physical length**, and a designer working from a board thinks in millimetres (the eval-board designer asked for
exactly this). TLIN gains a second, equivalent way to be specified — `L` and `Eeff` — which is also the general
fallback line AS-5 emits for any cross-section no circuit model covers, with Z0 and εeff from the cross-section
solve.

## 2. Requirements

**R-as2-1 — The physical form.** A TLIN instance states **either** `E` (+ `F`) **or** `L` (+ `Eeff`); both is a
refusal naming the two keys; neither keeps today's default (`E = 90°` at `F`). In the physical form:
- θ(f) = 2π·f·L·√Eeff / c₀ — exact for a non-dispersive TEM line, no reference frequency needed;
- `Eeff` defaults to 1 (an air line), and a value below 1 is a refusal.
Every existing `.cnl` and `.csch` evaluates **byte-identically** (the angle form is unchanged and remains the
default).

**R-as2-2 — Per-length loss.** In the physical form, loss is stated per unit length and scales physically:
- `Ac` — conductor attenuation at `F` in dB/m, scaling as √(f/F);
- `Ad` — dielectric attenuation at `F` in dB/m, scaling as f/F;
- both default to 0; `F` is required when either is non-zero (a refusal otherwise).
The angle form's `A` (total dB at `F`, scaling ∝ f) is unchanged. `A` together with `Ac`/`Ad` is a refusal.
The stamp is `StampUniformLine` with αl(f) = (Ac·√(f/F) + Ad·(f/F))·L / 8.686.

**R-as2-3 — Parameter editor.** An entry-mode switch on TLIN, **Electrical / Physical**, in the style of MKLOPF's
entry-mode switch. Switching converts at `F` (E ↔ L·√Eeff) so the line does not change; switching with no `F`
uses 1 GHz and says so in the status line. Lengths use the technology's length unit
(`ApplyTechnologyLengthUnit`) like every other length in the editor.

**R-as2-4 — Reference and check.** `reference components TLIN` documents both forms. `check` reports the
both-forms and missing-`F` refusals with the line and the keys. `explain` reports the resolved θ at `F` for a
physical-form line.

## 3. Not in this phase
Dispersion of `Eeff` (a fallback line is the cross-section's quasi-static answer by construction); any change to
MLIN, which already stamps its own lossy line.

## 4. Gates (minimal tests, run only these classes)
- `TLineModelLossTests` (existing) — one new case: a physical-form line with `Ac`, `Ad` matches the closed form at
  three frequencies to 1e-12; the angle form is byte-identical to before (an existing case keeps passing).
- `TlinPhysicalFormTests` — `L = c₀/(4·f·√Eeff)` is a quarter wave at `f`; both forms at once and `Eeff < 1` are
  refusals naming the keys; `.cnl` → `.csch` → `.cnl` keeps the physical form.
- `TlinUnitsRegressionTests` (existing) — one case: a physical-form `L` authored in mil reaches the model in metres.
