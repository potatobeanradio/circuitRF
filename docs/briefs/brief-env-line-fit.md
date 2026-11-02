# Brief ENV-FIT — transmission lines and Z(freq) blocks fitted across the envelope band, like S-parameter blocks

**Tag:** `R-envfit-n` · **Depends on:** brief ENV-TLIN (`brief-env-tlin-convergence.md`) merged first: a run with a line
in it must complete before its accuracy can be measured.
**Area:** `src/Engine/Envelope/` (`EnvelopeSnpFit.cs`, `EnvelopeTangentCheck.cs`, `EnvelopeBaseband.cs`,
`EnvelopeEngine.cs`, `EnvelopeEngineMt.cs`), `src/Engine/Modeling/VectorFit.cs` only if §2 shows it must change,
`tests/Engine.Tests/Envelope/`, `docs/user/src/reference/simulations.md`. Findings go in `src/Engine/RESOLVED.md`.
HB, S-parameter, DC and loadpull runs must be **byte-identical** after this brief, and so must every envelope run whose
linear elements are all affine in jω (R, L, C, sources) or are already fitted (SnP, System Filter, Duplexer). If any
of them changes, stop and say why.

---

## 0. The gap

Each envelope step solves `G + (jkω₀ + α)C` at every harmonic k (and every lattice product). That needs every linear
element's stamp to be `G + jωC` with fixed G and C, which R, L and C are. An element that is not, has today one of
three treatments:

| element | across each product's band | in the baseband (bias-path) envelope |
|---|---|---|
| R, L, C, sources | exact | exact |
| SnP, System Filter, Duplexer | **rational (pole-residue) fit**, realized with extra linear unknowns so its stamp is exactly `G + jωC` (`EnvelopeSnpFit`) — tolerance 1e-3, order ≤ 60, passivity reported | exact (the same realization) |
| TLIN, microstrip, stripline, coplanar and coupled lines, Impedance N-Port with `Z(freq)` | **first-order only**: its value and slope at k·ω₀ (the two-stamp tangent) | **held at its DC stamp** (`BasebandHeldModel`) |

The first-order treatment is right at the carrier and drifts across the band: a line's response is a delay, its phase
keeps turning across the band, and the tangent leaves it at the band edges, the more so the longer the line. The
held-DC treatment ignores a line's frequency dependence across the baseband altogether. circuitRF measures both errors
per element and warns, naming the element (`EnvelopeTangentCheck.Warn`, `EnvelopeBaseband` warning), but it cannot
correct them.

**The machinery to correct them exists.** `EnvelopeSnpFit.FitSystemBlock` fits a block from samples of its OWN stamp,
via `EnvelopeTangentCheck.NodeS` (every node it touches terminated in 50 Ω): a coarse grid from DC to the top of the
run, plus 41 points across each product's band, cached per block definition. Nothing in that path is specific to
filters; it is gated on `ec.Model is FilterModel or DuplexerModel`.

## 1. What to build

### 1a. Fit every linear element that is not affine

- Replace the type gate with a measured one: a linear element whose stamp is not affine in jω (its two-stamp
  tangent error from `EnvelopeTangentCheck.Measure` is above round-off, as the tangent check already distinguishes)
  goes through the same fit-and-realize path as a System Filter. SnPs keep their own path (they fit file data).
  Lines and `Z(freq)` blocks are the known members; any other model that turns out non-affine is included by the
  same rule, and RESOLVED lists which ones the measurement found.
- The realized model replaces the element **for the envelope run only**, as SnPs are today; every other analysis
  keeps stamping the real element.
- Because the realization is exactly `G + jωC` at every ω, DC included, the baseband product needs no special
  treatment for a fitted element: **a fitted element is not wrapped in `BasebandHeldModel`.** Holding at DC remains
  only for an element whose fit was refused (§1c).

### 1b. Measure what a line costs before settling the method

A lossless line is a delay; its S-parameters turn by 360° per (1/τ) Hz. One fit from DC to the top of the run must
follow every turn. On the reference bench of §3 (top of run ≈ 10.8 GHz at `MaxHarm=5` around 2.14 GHz):

1. Fit 50 Ω and 25 Ω TLINs of physical length 3, 30, 100, 300 and 1000 mm (`Eeff=1`), and a lossy one
   (`Ac=20 Ad=20 F=2 GHz`), and record for each: the order reached, the relative error against the 1e-3 tolerance,
   σ_max(S_fit) (passivity), and the fit time. Put the table in RESOLVED.
2. **If the 300 mm line cannot be fitted within tolerance at order ≤ 60 in under ~2 s, stop and report** with the
   table. Do not raise `MaxOrder` or change the fit on your own. The owner will choose between:
   - **(i)** a higher order cap for lines, at the measured cost;
   - **(ii)** fitting only where the run reads the element (DC and each product's band, without the coarse anchor
     between them), with passivity checked on those samples only;
   - **(iii)** a delay realization for lines in the envelope stepping (each harmonic's envelope delayed by τ with
     its phase factor, the method of characteristics), exact for any length, with only the loss fitted.
3. A `Z(freq)` block is fitted from samples of its own expression; record one realistic case (a resonator expression)
   in the same table.

### 1c. When a fit is refused

A fit that misses its tolerance, exceeds the order cap or is not passive is **not used**: the element keeps today's
first-order treatment and DC hold, and the run warns once, naming the element and the reason, as
`env-snp-nofit` already does for a block. Never step a non-passive realization.

### 1d. Messages and text

- The fit report for a line reads like an SnP's: instance, order, error, σ_max.
- **Retire the phrase "straight line in frequency"** from every message and the docs: it does not say what is
  approximated. Where the first-order treatment remains (a refused fit, §1c), say *"approximated to first order
  around each harmonic (its value and slope there)"* and, for the baseband, *"held at its DC value across the baseband
  envelope"*. That covers `EnvelopeTangentCheck.Warn`, the `env-snp-nofit` warning, `EnvelopeEngine.cs`'s comments,
  and the docs.
- `docs/user/src/reference/simulations.md`, **Frequency-dependent blocks:** lines and `Z(freq)` blocks join the fitted
  blocks; the remaining approximation is only for a refused fit, and is named. Regenerate that page only. No change
  history in the page.

## 2. Out of scope

- The nonlinear devices, and the SnP path (already fitted).
- HB, S-parameter and loadpull: they stamp each element at each exact frequency already.

## 3. Tests

In `tests/Engine.Tests/Envelope/`, one test per claim, minimal `InlineData`. Reference bench: the reproduction circuit of
brief ENV-TLIN §1 (a small PA, 2.14 GHz NR 20 MHz source, TL3 in series at the output). Linear tests use a source, the
element and a load only, so a closed form exists:

1. **Accuracy across the band, where first order fails.** Two CW carriers at f₀ − 50 MHz and f₀ + 50 MHz (one
   `MOD_Source`, two `CW` carriers) through a 25 Ω TLIN of 300 mm into 50 Ω: each carrier's output power equals the
   line's closed-form |S21|² at its own frequency within 0.01 dB. Record what the first-order treatment gave before
   the change: the test must be one it fails.
2. **The baseband too.** The same two carriers with the line in a bias feed (as `EnvelopeBasebandTests` does): the
   100 MHz beat across the line matches its closed form within 0.01 dB, where the DC hold could not.
3. **A Z(freq) block:** one CW carrier at a band edge through a resonant `Z(freq)` N-Port matches its closed form within
   0.01 dB.
4. **The reference bench:** with a 300 mm line it completes, posts a fit report for TL3, posts no first-order warning
   for it, and its Pout_avg and ACPR1 at `Vout` are reported in RESOLVED beside the first-order run's.
5. **Refusal:** a line long enough to exceed the order cap keeps the first-order treatment and warns naming it, with
   the new wording.
6. **Unchanged:** an all-lumped envelope run and an SnP run are byte-identical (compare `DataSet`s), and the existing
   `EnvelopeBasebandTests`, `EnvelopeSnpTests` and the ENV-TLIN tests pass.

Run only the envelope test classes (`dotnet test tests/Engine.Tests --filter "FullyQualifiedName~Envelope"`); do not
run the full suite.
