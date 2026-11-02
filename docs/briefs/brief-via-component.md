# Brief — VIA: a schematic via component with a closed-form model and a layout generator

**Tag:** `R-viac-n` · **Series:** designer feedback round 10 (2026-09-29).
**Area:** `src/Core/Devices/` (new `ViaModel`, `ViaFormulas`), `src/Core/Devices/ComponentModelFactory.cs`,
`src/Design/Schematic/ComponentTypeRegistry.cs` + `SchematicModel.cs` (`SymbolKind.Via`, `SymbolKind.ViaGnd`),
`src/Design/Layout/PCells/` (new `ViaPCell`, `PCellRegistry`, `SubstrateResolver`), `src/Design/Layout/Lvs/DeviceType.cs`,
`src/Ui/Layout/SchematicToLayoutGenerator.cs`, the parameter editor's MLIN substrate/impedance partials
(`ParameterEditorViewModel.MicrostripSubstrate.cs` / `.MlinImpedance.cs`), `docs/user/src/reference/` (component page).
**Depends on:** this round's MLIN fixes — the reference plane that Update Layout from Schematic does not generate today,
and a ground reference ABOVE the signal layer (MLIN on Inner 2 referenced to Inner 1). Both change `SubstrateResolver`
and `MlinPCell`, and this brief builds on the result. **Blocks:** —

## Why

A designer drawing a line that changes layers (Top → via → Inner 2, over an Inner 1 plane) asked which via model to use
next to MLIN in a schematic **without an EM run**. Today the only answer is a hand-built series R–L with guessed values
(10 pH? 200 pH? — the chat went both ways, a factor of twenty apart). A second case from the same designer: on FR-4 he
adds about **1.4 pF in parallel with every ideal part that goes to ground** to approximate its pad and via, and gets a
good S21 fit. That number is a property of his stackup and footprint, and it should come from them.

Nothing in the component library models a via today. What exists:

- `src/Design/Layout/Pdn/PdnInductance.cs` — railRF's partial self and mutual inductance of a round barrel
  (Grover §7, uniform-current form, `−3/4`), used for mounting loops. Correct for its purpose; it lives in
  `src/Design` and cannot be called from `src/Core`.
- `ViaShape` (layout), `StackupLayer.SpanFromLayer/SpanToLayer`, `WallThicknessDbu`, `Plated`, `ViaDefaults`
  (25 µm plating) — the stackup already knows everything the model needs.
- `SubstrateResolver` — the seam MLIN uses to turn `SignalLayer`/`GroundReference` into H/Er/T/σ/tanδ.
- The planar MoM and the 3D solver both simulate vias. That is the reference, not the tool the designer asked for.

## 1. `R-viac-1` — two components

| Engine name | Terminals | What it is |
|---|---|---|
| `VIA` | 2 (A on `FromLayer`, B on `ToLayer`) + implicit reference (node 0, as MLIN) | a signal via changing layers |
| `VIAGND` | 1 (A on `FromLayer`); the far end is node 0 | a via to a ground plane — a shunt part's return |

Both derive from `ComponentModel`, `ModelKind.Linear`, stamped fresh per frequency like `MicrostripLineModel`. Register in
the factory; golden test per CLAUDE.md "How to add a component type".

## 2. `R-viac-2` — parameters, drawn from the stackup

Exactly MLIN's pattern: a technology-bound instance shows stackup dropdowns, and the resolved numbers are injected by
the elaborator's substrate seam, never typed twice.

- `FromLayer`, `ToLayer` — conductor names from the stackup (dropdowns, as `SignalLayer`). `VIAGND` takes `FromLayer`
  and `GroundLayer` (defaults to the nearest `IsGroundReference` conductor).
- `Drill` (finished hole diameter), `Pad`, `Antipad` — lengths with units. Defaults: the technology's via layer if it
  declares one, else 0.3 mm / 0.6 mm / 0.9 mm, **named as defaults** in the Messages panel (R-L4d-7 precedent).
- `Plating` — defaults to the stackup via layer's `WallThicknessDbu`, else `ViaDefaults.PlatedWallThicknessUm`.
- Resolved and injected (not user-typed): barrel length `h` = the z-distance between the two conductors' mid-planes (the
  reference the 3D gate uses; D2), σ of the plating, εr of every dielectric the barrel passes, and **the list of
  ground-reference planes the barrel crosses** between From and To (each gets an antipad and a C term).
- `SubstrateResolver` gains a `ResolveViaSpan(technology, from, to)` beside `ResolveElectrical`. No second stackup walk.
- Standalone use (no technology): every value typed, exactly as MLIN falls back today.

## 3. `R-viac-3` — the model, and where it is valid

All formulas live in **one** new file, `src/Core/Devices/ViaFormulas.cs`, each with its citation in the doc comment.

**Inductance — Goldfarb & Pucel**, "Modeling via hole grounds in microstrip", *IEEE Microwave and Guided Wave Letters*
1(6), pp. 135–137, 1991:

    L = (µ₀/2π) · [ h·ln( (h + √(r² + h²)) / r ) + 1.5·( r − √(r² + h²) ) ]

`r` = drill radius, `h` = barrel length. Valid for an isolated cylinder with its return at a distance (the partial
self-inductance); for `h ≫ r` it tends to Grover's `h·[ln(2h/r) − 1]`, the surface-current form. It overestimates when a
return via is close (no mutual term) — the model says so in its doc comment, and the stitching-via case is out of
scope (§8). **Do not change railRF's `PdnInductance` numbers**; its `−3/4` is a documented choice for a loop that
cancels. Note the relationship in both doc comments.

**Resistance — same paper:** `R = R_dc · √(1 + f/f_δ)`, `R_dc = h / (σ·π·(r² − (r − t)²))`, `f_δ = 1/(π·µ₀·σ·t²)`,
`t` = plating. Valid while the wall is a thin tube; a filled via (`ViaFillKind`) uses the solid-cylinder `R_dc` instead.

**Capacitance (optional, `IncludeC = true` by default)** — pad-to-plane, per ground plane crossed, the widely used
closed form from Johnson & Graham, *High-Speed Digital Design* (1993), §7.1:

    C ≈ 1.41 · εr · T · D₁ / (D₂ − D₁)   [pF, with T, D₁, D₂ in inches]

`D₁` = pad, `D₂` = antipad, `T` = the dielectric thickness it is taken over. This is an **estimate** (the source itself
presents it as one); the brief's gate (§6) measures how far off it is on a real stackup and records that number in the
component's help, rather than claiming an accuracy. An override `C=` parameter replaces it outright.

**Topology.** `VIA`: a symmetric T — `L/2 + R/2`, then `ΣC` to node 0, then `L/2 + R/2`. `VIAGND`: series `R + L`
to node 0, with the pad C from A to node 0 in parallel (that is the designer's 1.4 pF, now computed). Validity: a lumped
T is good while `h < λ_d/20` in the densest dielectric crossed; above that, the model posts one warning per instance
through `IReportsWarnings` (MLIN's `MicrostripValidityReporter` pattern) — never a silent extrapolation.

**A through via used Top → Inner 2 leaves a stub to Bottom.** Model it (D3): the stub as its own open-ended L–C
section, from the same formulas, when `ToLayer` is not the via layer's span end.

## 4. `R-viac-4` — the layout generator

A `ViaPCell` (generator id `VIA`), built as `MlinPCell` is:

- a `ViaShape` at the origin with the drill, pads on `FromLayer` and `ToLayer` (and non-functional pads on the inner
  signal layers only if D4 says so);
- pins `A` on `FromLayer` and `B` on `ToLayer`, both at the origin, so MLIN pins on either layer land on them and
  **connect** (this round's "cannot merge a via and a trace" report is the same connectivity, and the gate checks it);
- **the antipads**: on each ground plane the barrel crosses, the same reference-plane patch MLIN will generate after this
  round's fix, with a circular hole of `Antipad` diameter. One plane-generation helper for both PCells (D1).

`SchematicToLayoutGenerator` passes `FromLayer`/`ToLayer`/`GroundLayer` through as it passes `SignalLayer` /
`GroundReference`; `DeviceType.cs` maps `VIA`/`VIAGND` for LVS.

## 5. `R-viac-5` — the parameter editor

The MLIN substrate panel shows computed Z0/εeff. The VIA panel shows the computed **L, R at the top of the band, ΣC**,
the planes crossed, and the validity frequency (`λ_d/20`), so the user sees the number they would otherwise have typed.

## 6. Gate

1. `ViaFormulas`: Goldfarb–Pucel L and R against the paper's own worked example values (hand-computed, committed); the
   `h ≫ r` limit against Grover's surface form to 1e-3.
2. Golden: `VIA` S-parameters from the netlist equal a hand-built R/L/C T at three frequencies to 1e-9.
3. Resolution: the shipped 4-layer FR-4 technology, `FromLayer = Top`, `ToLayer = Inner 2` — the resolved `h`, the one
   crossed plane (Inner 1) and the injected εr are exactly the stackup's.
4. Layout: `VIA` + two MLINs updated from schematic produce one connected net per side (a connectivity assertion, not a
   picture), with an antipad hole on Inner 1.
5. **Against the 3D solver, once, as a committed reference** (CLAUDE.md: references are generated independently and
   committed): the via_and_trace geometry — a Top microstrip, a via, an Inner 2 stripline-to-Inner-1 line, 20 mil-class
   drill — solved at 5 points to 6 GHz, stored in `testdata/`. The routine test compares `MLIN + VIA + MLIN` against
   it: |S21| to 0.1 dB and ∠S21 to 3° below `λ_d/20`. The EM run itself is a one-off (keep EM runs short) and is **not**
   in any test. Record the measured C error of the Johnson–Graham term here and in the help page.
6. Out-of-range: a via whose `h` exceeds `λ_d/20` at the top of the sweep posts exactly one warning.

**Measured (2026-09-30).** Gate 5 used the committed F0 reference `testdata/em3d/f0/B-via/palace` (a Top
microstrip, a solid 300 µm via through a 900 µm antipad in the middle plane, an inverted microstrip under
it: the via-and-trace shape, one plane, not the Inner 2 stripline), so no EM run was needed. MLIN + VIA +
MLIN: |S21| within 0.031 dB, ∠S21 within 0.17° at 1.2–6 GHz, with each plane's Johnson–Graham `T` taken
as the barrel it owns (half the dielectric to each neighbouring conductor). The C error of the term
itself: 81 fF against 34 fF de-embedded for the whole transition, **+140 %**; Goldfarb–Pucel's 72 pH
against ~220 pH. They cancel in S21; S11 is −25 dB against −44 dB. Gate 1's premise was wrong: the
formula tends to `h·[ln(2h/r) − 3/2]`, not Grover's `− 1`. Details in `src/Core/RESOLVED.md`.

## 7. Decisions for the owner

- **D1** Antipads come from the PCell's own plane patch (consistent with MLIN's plane after this round's fix), not from a
  clearance applied to a user-drawn plane. Recommended; the alternative needs a keepout concept layout does not have.
- **D2** `h` from conductor mid-plane to mid-plane (recommended) vs top-surface to top-surface.
- **D3** Model the through-via stub by default (recommended; it is the dominant effect above a few GHz on thick boards).
- **D4** Non-functional pads on inner signal layers: none by default.
- **D5** Names: `VIA` and `VIAGND` (recommended) vs one `VIA` with a `Grounded` switch.
- **D6** Is a stitching (return) via next to a signal via in scope now? Recommended: no — a later brief adding the
  mutual term from `PdnInductance`'s formula moved to `src/Core`.

## Docs

A component reference page for VIA/VIAGND: the formulas, citations, validity, the measured C error from §6.5, and the
worked replacement for "add 1.4 pF in parallel" on the shipped FR-4 stackup. Doc sources only; no DocGen.

## On completion

`src/Core/RESOLVED.md` and `src/Design/RESOLVED.md`, never CLAUDE.md. Do not commit unless the owner asks.
