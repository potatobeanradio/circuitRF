# Brief 78 — RF harmonic currents in wires

**Series:** [3D thermal, fifth series](brief-em3d-71-thermal-overview.md) · **Tag:** `R-em3d78-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §6.6; [`wbond.md`](../design/wbond.md) §3.4, §3.5; overview §1h, D6–D8
**Area:** `src/Thermal/Electrothermal/` (RF heat per unit length), `src/Design/Thermal/` (array currents, the
share), `src/WBond/` (an entry point from resolved centrelines, if needed — read-only otherwise),
`src/Design/Layout/Em/EmSetupModel.cs` (`Currents[].Harmonics`), `src/Ui/ThreeD/`, `tests/`
**Depends on:** 72 (W3), 77 · **Blocks:** 79, 81

---

## 0. What this brief delivers

Scenario 3 at RF: a port carries **DC plus harmonic currents**, each entered as **Peak or RMS**, and every wire
heats by the skin-effect resistance at its own solved temperature:

1. **The input**: harmonics per port, explicit Peak/RMS, a fundamental frequency (§1).
2. **Which array carries it** (§2).
3. **How the array shares it**: wBond's inductive share, for `.wBond` arrays **and** `.c3d` wires (§3).
4. **RF heat per unit length** from the exact Bessel solution at the solved temperature, inside conductive
   balance (§4).

---

## 1. `R-em3d78-1` — the input

```jsonc
"Currents": [ { "Port": 1, "Dc": "Id",
                "F0": "Ffund",
                "Harmonics": [ { "N": 1, "Amp": "I1", "As": "Peak" },
                               { "N": 2, "Amp": "0.12", "As": "Rms" } ] } ]
```

- `As` is **required** on every harmonic — never inferred (D8). A missing `As` is a `check` error naming the
  entry. Internally everything is a **peak** phasor magnitude: RMS × √2.
- `F0` is Hz (an expression with a unit, or a bare number in Hz), required when any harmonic is present.
- **Phase is not taken**: harmonics are orthogonal and add in power (overview §1h).
- The Setups dialog's Currents group shows, per port, the **DC-equivalent RMS** √(I_dc² + Σ|Iₙ|²/2) as a
  **readout** — labelled *"for comparison only: RF heats more than this DC current would, because of skin
  effect"* — never as an input.

## 2. `R-em3d78-2` — which array carries a port's RF current

At RF the current is not solved through the 3D metal (overview §1h): it is assigned to **wire arrays**.

- A **wire array** is the set of wires whose two ends land on the same two conductors (the grouping wBond's
  `A` matrix states for a `.wBond`; for `.c3d` wires the lowering forms it from the resolved end pads).
- A port's harmonic current goes to the **one** array with an end on the port's positive conductor. More than
  one such array is **refused**, listing them, with the remedy: state the current per array instead —
  `Currents` accepts `{ "Array": "<name>", … }` in place of `Port`.
- When **both** ends of an array have a port with harmonics (a two-port driven from HB, brief 79), the two
  currents differ by the shunt current between them; the lowering uses the **larger** magnitude per harmonic
  (the conservative choice), and the notes print both and the difference.

## 3. `R-em3d78-3` — the per-wire share (D7)

- At RF the share among an array's wires is set by the **inductance matrix**: per-wire current per unit array
  current is a column of X = L⁻¹A in wBond's `ArrayReduction` — frequency-independent in the inductive regime,
  so **one share serves every harmonic**. It is taken from wBond's code, **not re-derived**.
- **`.wBond` arrays** — the design's own `ImpedanceReduction`/`ArrayReduction` path.
- **`.c3d` wires** — the same computation from the **resolved centrelines** (`C3dWires.Resolve`). If wBond's
  entry points take only a `WBondDesign`, add one that takes resolved centrelines, diameters and grouping, and
  make the `.wBond` path go through it too — one path, so the two cannot drift. A `.c3d` wire set built to
  match a `.wBond` fixture gives the **identical** share (gate 2).
- The DC share stays the conduction solve's (brief 77 §2), which includes σ(T); the RF share does not depend
  on σ, and the notes say so once per run.

## 4. `R-em3d78-4` — RF heat per unit length

**`R-em3d78-4a`** For each wire element at temperature T: q′_RF = Σₙ ½·|I_wire,n|²·R′_ac(n·F0, σ(T)), with
R′_ac = Re Z_int from `src/WBond/InternalImpedance.cs` (the exact Bessel solution, wbond.md §3.5), **evaluated
at σ(T)**. This replaces, for this run, the fixed 85 °C every wBond evaluation assumes; wBond's own default
is unchanged.

**`R-em3d78-4b`** The Newton Jacobian gains dq′_RF/dT through dσ/dT and the skin-depth dependence
(R′_ac/R′_dc is a function of a/δ alone, and δ ∝ σ^{-1/2}); derived analytically, checked against a finite
difference in a test.

**`R-em3d78-4c`** The RF heat is **per unit length, uniform around the wire**: in a wire tens of microns
across, its radial temperature difference is negligible, so where in the section the skin current flows does
not matter to the temperature. The notes say this once.

**`R-em3d78-4d` Electrically long.** The current along a wire is taken as uniform. When a wire is longer than
a tenth of the wavelength in its surrounding material at the highest harmonic, the run adds a note naming the
wire and the harmonic (a standing wave would move the hot spot); it does not refuse.

**`R-em3d78-4e`** The wire table (brief 77 §6) gains the RF heat per wire, per harmonic, and the per-wire
peak current per harmonic.

## 5. Gates

1. **W3**: a single wire at 1 A peak, 2 GHz, fixed ends — the RF heat per length equals the Bessel R′_ac at the
   solved temperature × ½|I|², and the peak temperature matches the reference to its tolerance.
2. **Share identity**: a `.wBond` fixture of five wires and a `.c3d` wire set with the same centrelines give
   the identical per-wire share (bit for bit) and the identical temperatures.
3. **Share vs wBond**: the per-wire share equals `ArrayReduction`'s X column for the same fixture to 1e-12.
4. **Peak/RMS**: 1 A RMS and 1.41421356… A peak give identical results; a missing `As` is a `check` error.
5. **Orthogonality**: harmonics at 1 and 2 of F0 heat as the sum of each alone (with `SigmaOfT` off, where the
   problem is linear in heat) to 1e-12.
6. **Jacobian**: the analytic dq′_RF/dT against a central difference, at 5 and 50 skin depths, to 1e-6.
7. **Refusals**: two arrays on one port's positive conductor → the refusal listing both; `Array` form accepted.
8. **Both ends driven**: two unequal port currents on one array → the larger used, the difference in the
   notes.

## 6. Owner check list (Debug build)

1. On the output-wire model, give port 1 DC plus two harmonics (one Peak, one RMS); read the DC-equivalent
   readout.
2. Simulate; compare the hottest wire against the same DC-equivalent current entered as DC only (RF should be
   hotter).
3. Put the same wires in a `.wBond` and in the `.c3d`; compare the per-wire currents in the probe table.

## 7. Scope

- **RF heat in wires only** (D6); pads, leads and flanges take DC heat only.
- **No EM solve**; the share is wBond's.
- **wBond's own results do not move**: its default 85 °C evaluation and every wBond test are unchanged.
- Findings in `src/Thermal/RESOLVED.md`, `src/WBond/RESOLVED.md` (if an entry point was added); never
  `CLAUDE.md`.
