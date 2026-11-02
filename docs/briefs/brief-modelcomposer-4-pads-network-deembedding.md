# Brief MC-4 — Pads, the six-pin extrinsic network, de-embedding and intrinsic re-referencing

**Series:** `brief-modelcomposer-0-overview.md` (D5, D6, D13) · **Tag:** `R-mc4-<m>` · **Depends on:** MC-3
**Area:** `src/Design/Modeling/` (pads, network, de-embedding, re-referencing, stock networks);
`src/Engine/Modeling/` (network-parameter algebra); the linear engine, used as is
**Read first:** [P1] §5.4.1.3 (manifolds by EM), §5.5 (Y/Z shells, eqs. 5.3–5.10), §5.6.1–5.6.3 (the intrinsic
model must be frequency-independent; eqs. 5.11–5.16), §6.2.6.1 (eq. 6.12, re-referencing, Fig. 6.5), §6.3.2
(eqs. 6.22–6.24); [R11] ch. 3.2

---

## 1. Goal

From any data set and any extrinsic network: the **intrinsic** voltages, currents, Y-parameters and small-signal
elements at every bias point, with the reference-plane pitfall handled explicitly.

## 2. Requirements

**R-mc4-1 — Pads (the launch, never part of the model).** `Pads` is one of:
- `None`;
- `OpenShort`, from open and short dummy S-files;
- `TwoPorts`, a Touchstone 2-port per side, e.g. from EM;
- `Network`, a user `.csch` per side.

Removal is in Y/Z for open-short (the standard parallel-then-series subtraction) and by cascade inversion for
two-ports. The result is the data set at the `device` plane. A pad file whose frequency axis does not cover the
data's is a refusal naming both ranges — never an extrapolation.

**R-mc4-2 — The network (D5).**
- A `.csch` with exactly six pins. A wrong pin count, or a duplicated or missing number, is a refusal that lists
  the pins found.
- Its variables are listed with value and unit. The `.cmc` stores, per variable, `Min`, `Max`, `Lock`, `Tie`
  (another variable, optionally times a constant) and `Scaling` (D9).
- The stock networks of D5 ship as embedded `.csch` resources; choosing one copies it into the document's folder,
  so it becomes an ordinary user file.
- Defaults:
  - `Min` is 0 for R, L, C and mutual M;
  - `Max` is 100× the starting value;
  - the coupling factor `|k| = |M|/√(L1·L2) < 1` is enforced as a constraint, not a bound.

**R-mc4-3 — De-embedding through any network.**
- Solve the network with the linear engine as a **4-port**, port A (pins 1–3), B (2–3), C (4–6), D (5–6), giving
  `Y_N = [[Y_aa, Y_ab], [Y_ba, Y_bb]]` (2×2 blocks) at every data frequency.
- The intrinsic core then follows exactly:
  `Y_int = Y_ba (Y_aa − Y_ext)⁻¹ Y_ab − Y_bb`. This is the inverse of
  `Y_ext = Y_aa − Y_ab (Y_bb + Y_int)⁻¹ Y_ba`, which holds because the 3-terminal core takes its current on C and D
  and returns it on pin 6.
- Report the condition number of `(Y_aa − Y_ext)`, `Y_ab` and `Y_ba` per frequency. A frequency where any exceeds
  1e10 is flagged and left out of later averaging, never silently used.
- Ports with a floating negative terminal (pin 6 is not ground) are ordinary for the engine. If that ever proves
  false, stop and report rather than re-implementing MNA here.

**R-mc4-4 — Re-referencing to intrinsic voltages (D6).** For every bias point:
1. Move from the stated plane to the `device` plane: subtract `BiasPathOhms × I` for `instrument`; pads carry no
   DC drop unless their network has series R, which is solved the same way as step 2.
2. A **DC solve of the network**: inductors short, capacitors open, the extrinsic voltages imposed on pins 1/2
   (pin 3 the reference), and the measured terminal currents drawn from pins 4/5 and returned on pin 6. This
   gives `Vgs_i = V4 − V6`, `Vds_i = V5 − V6` — [P1] eq. 6.12 for any network.
3. A data set that carries intrinsic voltages (simulated tables) reports `max |ΔV|` between its own and the
   computed ones, in one line.

The extrinsic and intrinsic point clouds are both kept, for the plot that shows the pitfall ([P1] Fig. 6.5).

**R-mc4-5 — Intrinsic quantities.** From `Y_int` per bias and frequency:
- `Gm = Re Y21`, `Gds = Re Y22`;
- `Cij = Im Yij / ω` (eq. 6.22);
- the equivalent-circuit set `Cgs = C11 + C12`, `Cgd = −C12`, `Cds = C22 + C12`, `Cm = C21 − C12` (eq. 6.24, with
  the book's sign convention);
- `τ = −Cm/Gm`, the transconductance delay that `Cm` represents to first order ([P1] eqs. 5.18, 5.21) — a readout
  only; the model carries it as `Cm` in the charges (D4).

A **frequency-flatness metric** per quantity — the spread across a chosen band ÷ its mean — is the quantity MC-5
minimises ([P1] §5.6.3). The band is a document setting; its default is the band above 1/10 of the data's top
frequency and below 1/2 of it.

**R-mc4-6 — Results.** The intrinsic data is a `DataSet` with cubes per quantity on `(bias point, frequency)` and a
per-point cube of intrinsic voltages and currents. Later steps and the plots read only this; nothing recomputes it
privately. It is cached like imported data (MC-3) and keyed by the network values, so an unchanged network
reopens instantly.

**R-mc4-7 — Long work.** Through `RunControl`: stage per data set, cancellable, no partial result kept.

## 3. Not in this phase
Choosing the network's values (MC-5); any constituent.

## 4. Gates (minimal tests, run only these classes)
- `DeembedThroughNetworkTests` — S generated by circuitRF from a known linear core inside the GaN network (and
  inside the classic shell) de-embeds to the known core's Y to 1e-9 at every frequency.
- `PadDeembedTests` — a synthetic open/short pad around a known 2-port is removed to 1e-9; a short frequency range
  is refused naming both ranges.
- `IntrinsicReReferencingTests` — computed intrinsic voltages equal the SDD truth's internal node voltages
  (`SyntheticFetData`) to 1e-9; `instrument` with bias-path R gives the same intrinsic points as `device` after
  the drop; a simulated set's own intrinsic voltages are reported against the computed ones.
- `IntrinsicQuantitiesTests` — `Cgs`, `Cgd`, `Cds`, `Cm` and `Gm` of a known linear core recovered exactly; a
  deliberately wrong `Lg` makes the flatness metric grow (the [P1] §5.6.3.1 symptom).
