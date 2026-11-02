# Brief ENV-SNP — S-parameter blocks in the circuit envelope, exactly

**Tag:** `R-envsnp-n` · **Depends on:** the envelope analysis as merged (PR #16 and its follow-up 7c2b8e58)
**Area:** `src/Engine/Envelope/` (`EnvelopeEngine.cs`, `EnvelopeEngineMt.cs`, `EnvelopeRunner.cs`), the existing
`src/Engine/Modeling/VectorFit.cs`, `src/Core/Devices/SnpModel.cs` (read, not changed). Findings go in
`src/Engine/RESOLVED.md`. HB, S-parameter, DC and loadpull runs must be **byte-identical** after this brief. If any of
them changes, stop and say why.

---

## 0. The gap

An S-parameter block (`SnP`) in an envelope run is accepted, but it is not simulated correctly across the signal's
bandwidth.

Each envelope step solves `(G + (j k w0 + alpha) C) x_k = …` per harmonic k (header of `EnvelopeEngine.cs`). The
engine gets `G` and `C` by stamping the linear network at `k w0 ± dlt`, a hair either side of the harmonic
(`EnvelopeEngine.cs:200-229`, the same code in `EnvelopeEngineMt.cs:150-170`). That is exact for lumped R/L/C, whose
stamp is affine in `jω`. For an `SnP` (and any line or `Z(freq)` model) it is a **first-order tangent at k·f0**: the
block's value and slope at the carrier. Gain, phase and group delay at the carrier survive. Everything that bends
across the band is lost: filter skirts, passband ripple, group-delay variation. Those skirts are exactly what shapes
ACPR.

How bad it gets:

- **Single fundamental:** the tangent must hold over ± half the signal rate. It is fine for a smooth match and a 20 MHz
  signal, and wrong for a filter or duplexer whose response bends inside or near the channel.
- **`Envelope = single` over widely spaced carriers:** the tangent must hold over hundreds of MHz.
- **Two fundamentals (`EnvelopeEngineMt`):** the same tangent, and the error is **not measured at all** there.
- **The single-fundamental error is measured but hidden.** It is stored as `__EnvTangentError` and printed by the CLI.
  The GUI never warns, and the `__` prefix keeps it out of the trace picker.
- **No envelope test contains an `SnP` in a way that checks its response.**
  `tests/Engine.Tests/Parametric/SweptEnvelopeReferenceBaseTests.cs` has one, but it is a flat 1-port load that checks
  path resolution only.

The fix: give the envelope a model of each `SnP` that **is** affine in `jω`. A rational (pole-residue) fit realized as
extra linear state unknowns is affine by construction. The engine's existing two-stamp `C` recovery then becomes exact
for it, and nothing in the stepping changes. `VectorFit` already exists (shared poles across responses, conjugate
pairs, unstable poles reflected) and is used by `PassiveAnalysis`. Use it, do not write a second one.

## 1. `R-envsnp-1` — the gate, written first and shown to fail

Write the gate before the fix. Record what the **current** tangent method gives on it in `RESOLVED.md`: the ACPR error
in dB and the output NMSE. That number is the reason for this brief.

**The circuit:** `MOD_Source` → 2-port `SnP` → 50 Ω, with the Touchstone reference at 50 Ω on both ports, so the
source and load are reflectionless. The signal is a single NR or LTE carrier, using a carrier spec the existing tests
already use. With the source and load both matched, the path is linear and the output is exactly the input filtered
by S21.

**The reference, independent of the envelope engine:**
- Output spectrum: `PSD_out(f) = PSD_in(f) · |S21(f)|²`.
- Output waveform: `y(t) = IFFT(X(f) · S21(f))`.

Both come from the `MOD_Source`'s own I/Q record, FFT'd directly in the test. From them, compute the reference
channel power, ACPR1 and ACPR2 with the same `SignalMeasure` band definitions the engine uses. Account for the
source's `Z` divider: the available-power drive into a matched 50 Ω port is half the open-circuit voltage.

**Two Touchstone fixtures**, generated in the test from a closed form and written at a realistic file pitch (a few
hundred points). Neither is lab data.
- **(a) Rational:** a 5th-order Chebyshev bandpass, with its skirt inside the adjacent channel and 0.5 dB passband
  ripple across the main channel. A rational fit should reproduce it to round-off.
- **(b) Not rational:** the same filter cascaded with an ideal 1 ns delay (`e^{-jωτ}`). This makes the fit work for
  its accuracy, and the reported fit error then has to bound the envelope's error.

## 2. `R-envsnp-2` — the fit

Fit once per `SnP` per run, on the **S** matrix:

- **Fit S, not Y or Z.** S is bounded for every passive file, including ones whose Y or Z does not exist (a through,
  an open, a short).
- **Shared poles over all N² entries:** `VectorFit.FitByError`, sampled at the file's own frequency points, not
  interpolated ones.
- **Tolerance and order:** start at a relative error of 1e-3 and a maximum order of 60, or `MaxOrder` if lower.
  **Choose the defaults by measurement** on the §1 fixtures, and record why. Do not add a setup key unless a gate case
  shows one is needed. If you think one is, stop and ask.
- **Cache the fit** by the file's absolute path, its last-write time, and the fit settings. Parallel-in-time segments,
  a parametric sweep's points and several instances of one file then all share one fit.

**DC and beyond the file.** A Touchstone file rarely starts at DC and often stops below K·f0. The HB start point
stamps the `SnP` through its interpolator (`WarnClamp` by default), and the fit extrapolates differently. If the start
point and the stepping use different models, the run starts away from its own equilibrium. **So the envelope run uses
the fitted model for every stamp it makes, the HB start point included.** Start and stepping then agree by
construction. Report how far the fit is from the interpolator at DC and at each K·f0 outside the file's range, so a
user can see where the two models part.

**Stability and passivity.** `VectorFit` already reflects unstable poles. Also measure passivity: the largest
singular value of `S_fit` on a dense grid from DC to beyond `K·f0 + band`. Report it with the fit. Passivity
**enforcement** is not in scope. If a gate case misbehaves because of a non-passive fit, stop and report it.

## 3. `R-envsnp-3` — the realization, and where it plugs in

An engine-side `ComponentModel` (in `src/Engine/Envelope/`) wraps the fit. **Its `Stamp(ω)` must be exactly
`G + jω·C`, with the same unknowns at every ω, including ω = 0.** One realization that meets this, written in waves
against each port's Touchstone reference `Z0`:

- `a = (V + Z0·I) / (2√Z0)` and `b = (V − Z0·I) / (2√Z0)`: algebraic rows.
- `b = D·a + Σ_p C_p·x_p`: algebraic.
- `(s − a_p)·x_p = B_p·a`: the only rows that carry `s`.

Realize each conjugate pole pair as a real 2×2 block, so the k = 0 (baseband) envelope stays real. Any other
realization is fine if it has the same property.

**Two checks that it is right:**
- Its `__EnvTangentError` contribution falls to round-off.
- A plain S-parameter sweep of the realized model reproduces `S_fit` to round-off. Assert this in a unit test.

**Plugging it in.** `ElaboratedComponent.Model` is get-only. The swap must be scoped to the envelope run: for example,
the envelope building its elaborated netlist with the substituted model, or a seam the extractor consults. Every other
analysis must keep stamping the real `SnpModel`. If the cleanest seam needs a Core API change bigger than a
constructor overload or a clone, stop and describe it.

**Both envelope engines.** `EnvelopeEngineMt` stamps through the same `EnvelopeEngine.Stamp`, so lattice mode should
get the realization for free. Gate it anyway (§5.4).

## 4. `R-envsnp-4` — what stays approximate, and saying so

Lines (`TLine`, microstrip, stripline, coplanar, coupled lines) and `Z_Port` with a `Z(freq)` keep the tangent. A
line is a delay, and a rational fit of one needs many poles. Whether to fit them too is an owner decision for later,
not this brief.

Make the tangent's error **visible**:

- **Measure it per element.** Measure each non-affine element's own band-edge error, not the whole matrix's (today's
  `RelDiff` over the whole matrix cannot say which element it is). A warning can then name the instance.
- **Warn above a threshold.** Emit one warning per run, via `ElaboratedNetlist.AddWarningOnce`, naming the
  element(s) and the error. Choose the threshold by measurement: the tangent error at which ACPR on the §1 fixture
  moves by 0.1 dB. Record it.
- **Add the same band-edge check to `EnvelopeEngineMt`**, which has none.
- **Warn on a poor fit.** When an `SnP`'s fit misses its tolerance at the maximum order, warn with the instance, the
  order and the error, and continue with the best fit.

## 5. Tests (targeted, per `feedback-minimal-tests`)

New file: `tests/Engine.Tests/Envelope/EnvelopeSnpTests.cs`.

1. **Fixture (a), single fundamental.** Channel power, ACPR1 and ACPR2 are within 0.05 dB of the exact reference, and
   the output NMSE against `y(t)` is better than −40 dB. The tangent method's failing numbers go in `RESOLVED.md`, not
   in a test.
2. **Fixture (b).** The same quantities, within a bound stated in terms of the reported fit error. Report the numbers.
   Do not loosen a tolerance to pass.
3. **Start-point consistency.** A CW drive through an `SnP` into a nonlinear device (any built-in FET or diode
   circuit an existing HB test uses), where the file stops below K·f0. The envelope holds its HB start point over the
   run, with no drift beyond `Tol`.
4. **Lattice mode.** Two carriers far enough apart to get their own fundamentals, through fixture (a). Each carrier's
   channel power matches the exact reference.
5. **Nothing else moves.** An existing lumped-only envelope circuit gives a bit-identical `DataSet` and performs no
   fit. An HB run with an `SnP` is byte-identical to before.
6. **The warning.** A `TLine` envelope run with a wide signal warns and names the line, in both engines. Fixture (a)'s
   `SnP` run does not warn.

Gate: that class, the realization's unit test, and `tests/Firewall.Tests`. No full suite.

## 6. Docs

Edit `docs/user/src/reference/simulations.md`, in the Circuit Envelope section only. Edit the doc source only;
DocGen is the owner's.

- **S-parameter blocks:** they are fitted with a rational model and simulated exactly across the signal band. The fit
  order and error are reported, and a poor fit warns.
- **Lines and `Z(freq)` blocks:** they are still approximated by a straight line in frequency around each harmonic,
  and a warning names any whose error is large enough to matter.

## Non-goals

- Fitting lines or `Z(freq)` models (§4).
- Passivity enforcement (§2).
- Using the fit in HB, S-parameter, DC or loadpull runs.
- Any GUI for the fit settings.
- The Pout/Pdc/DE simplifications already documented under `#envelope-power`.
