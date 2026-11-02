# Sonnet Brief — RP-2c: de-embedding a coplanar edge port

**Read `brief-em-return-plane-2-per-port-reference.md`, then RP-2a
(`brief-em-return-plane-2a-two-cut-port-kernel.md`), which this completes.** RP-2a ships
conductor-referenced `InternalDeltaGap` ports — which have no error box by construction — and
**refuses conductor-referenced EDGE ports by name, pointing here.** This brief is that refusal's
arrival.

**Do not start this before RP-2a's gate 2 has selected an excitation normalisation.** A calibration
built against an unverified excitation cannot be told apart from a wrong calibration.

---

## 1. Why it is separate, and why it is the largest third

An edge port has a feed outside the cut, so its s-parameters are meaningless until the two-line
calibration removes it. **`PlanarCalibration`'s standards are uniform lines over the plane** — it
copies the port's own `TransverseLines` verbatim into a rectangle (D4) and builds a microstrip.

A coplanar port's standard is a **coplanar** line: different `Z_c`, different β, different static
capacitance, and a cross-section that is three conductors rather than one. **Calibrating a coplanar
port against a microstrip standard produces s-parameters that are plausible and referenced to
nothing** — RP-2's R-rp2-4, and the reason RP-2a is allowed to ship without it.

`PlanarPortResolution` is the concrete obstacle: `WidthM`, `ReferencePlaneM`, `OuterEdgeM`,
`TransverseLines` and `LongitudinalRunM` are all singular, one conductor each. RP-2a adds the negative
terminal's counterparts; this brief is what reads them.

---

## 2. What has to be built

**R-rp2c-1. The standard is built in the port's own reference.** Both conductors, both slots, the same
transverse lines the DUT's port resolved to — extended longitudinally to the two calibration lengths.
D4's rule is unchanged and is the whole point: the standard must rebuild the port's neighbourhood
*exactly*, and the neighbourhood is now three pieces of metal.

**R-rp2c-2. The two cuts stay at one station in the standard too**, on RP-2a's R-rp2a-2 terms. A
standard whose two cuts are skewed calibrates out a length that is not in the DUT.

**R-rp2c-3. The calibration ALGEBRA is not re-derived.** The two-line trace, the γ branch resolution
and the T-matrix cascade are exact regardless of cross-section and are already gated
(`PlanarDeembedTests`). What changes is the STANDARD's geometry. If the algebra appears to need
changing, say why before changing it.

**R-rp2c-4. The mixed case is the ordinary case.** A run with a microstrip port 1 and a coplanar port
2 needs two differently-shaped standards in one calibration. `PlanarSolve` shares one DCIM fit across
the DUT and both standards because the fit depends on (slab, frequency) alone — that stays true, and
it is what keeps the cost of two standard shapes bearable. Measure it and say what it cost.

**R-rp2c-5. The de-embedding reference plane is still not user-positionable** (§10.6), and it is now
two planes that must be one. The plane DRAWN over the layout must come from the engine's reported
coordinates, not a Ui re-derivation — the rule §10.6 already states.

---

## 3. Gates

1. **RP-2a's bit-identity, again**: every existing ground-referenced de-embedded result is unchanged,
   exactly. The calibration path is the one every L8/L9 acceptance number goes through.
2. **A de-embedded coplanar uniform section is perfectly matched at the two calibration lengths.**
   That is four equations fixing four unknowns and it is exact — L8d measured |S₁₁| = 8.5e-16 for the
   microstrip case. A coplanar section that is not exact there means the standard is not the port's
   neighbourhood.
3. **γ two ways.** The two-line trace and an independent travelling-wave fit that shares no algebra
   with it, agreeing on the coplanar standard to the same order L8d recorded for microstrip
   (2.5e-4 … 3.9e-3 over 2–10 GHz). State the number rather than a pass.
4. **A de-embedded CBCPW line against the closed form** RP-2a's gate 2 used — now with the feed
   removed, which is the number a user actually reads.
5. **R-rp2c-4's mixed calibration runs**, and its cost is reported against the same run with both
   ports on the plane.
6. **The refusal RP-2a left behind is gone**, and nothing else was quietly un-refused with it.

---

## 4. If it is too large

**Stopping here with a refusal that names the arrival place is a legitimate outcome** — R-mom-17, and
RP-2's own §2c says so explicitly. What is NOT legitimate is a calibration that runs and references
the answer to a microstrip standard. **If the standard cannot be built correctly, leave RP-2a's
refusal standing and report why**, in full, in `src/Engine/Mom/RESOLVED.md`.

---

## 5. On completion

Findings to `src/Engine/Mom/RESOLVED.md` — **never a `CLAUDE.md`**. Update `docs/design/mom-engine.md`
§10.6's de-embedding paragraphs and its port-type list.

**Report, do not silently absorb:**
- Gate 2's |S₁₁|. If it is not at the 1e-15 level the standard is not the port's neighbourhood, and no
  amount of tolerance-widening makes that acceptable.
- What a coplanar standard costs relative to a microstrip one at the same N, measured.
- Whether the direct port-to-port radiative coupling L8d identified (|S₁₁| = 3.9e-4 at 2 GHz rising as
  f² and NOT monotone in standard length) behaves the same way with coplanar grounds present. It may
  be smaller, and if so that is worth knowing.
