# Brief series — ideal system-level components

**Origin.** Owner request, 2026-08-31: after the ideal mixer shipped (`e925f9c`), add the rest of
the blocks a user needs to build a **system block diagram** in circuitRF and run it in both
S-parameters and harmonic balance — balun, circulator, switch, ideal power amplifier, directional
coupler, 90° hybrid, ideal filter, attenuator, duplexer — gather them (and the two mixer tiles)
under a new **System** filter in the Library Palette, and document them.

---

## The one finding that shapes the whole series

**Every block on the list except the amplifier is an ideal S-matrix, and circuitRF can already
stamp an arbitrary S-matrix — it just has never been asked to.** The repository has two
frequency-domain N-port stamps today (`SnpModel`, Z(ω) from a Touchstone; `ZPortModel`, Z(ω) from
expressions) and one two-port chain stamp (`ChainModel`, ABCD). All three are *derived* forms, and
each fails on a block that has no such matrix:

- **The ideal circulator has no Z matrix at all.** For `S = [[0,0,1],[1,0,0],[0,1,0]]`, `det(I−S) = 0`
  exactly, so `Z = Z₀(I+S)(I−S)⁻¹` does not exist. Its Y *does* (`det(I+S) = 2`), and equals
  `(1/Z₀)·[[0,1,−1],[−1,0,1],[1,−1,0]]` — antisymmetric, **zero diagonal**, and itself singular
  (every row and column sums to zero, as a floating network's must).
- **The ideal through (a closed switch, a lowpass at DC) has no Y**, and the ideal open (an open
  switch, a highpass at DC) has no Z.

One formulation covers all of them and never degenerates, because it is the *definition* of S
rather than a transformation of it. With one branch-current unknown `iₚ` per port and `vₚ` the port
voltage across that port's own ± pair:

```
   (vₚ − Z₀ₚ·iₚ)/√Z₀ₚ  =  Σ_q  S_pq · (v_q + Z₀_q·i_q)/√Z₀_q        (one constraint row per port)
   iₚ flows  port p+ → port p−                                        (the KCL coupling)
```

Both halves are ordinary `IMnaContext` calls (`AddBranch`, `AddConstraint`, `AddBranchConstraint`,
`AddBranchCurrent`) — the same Group-2 machinery `SnpModel` and `ZPortModel` already use, and the
linear partition of the HB engine already stamps per harmonic ω = k·ω₀ **including DC**
(`HbLinearExtractor.StampAt`). Nothing in `src/Engine` has to change for any component in this
series. **SYS-2 builds that shared stamp once; every other brief supplies only its own `S(ω)`.**

## The second finding: PIM and frequency dependence pull in opposite directions

The owner wants an optional passive-intermod on the circulator, the coupler and the hybrid. A
nonlinearity in this repository is `Evaluate(v)` — **memoryless in the port voltages** — and
`ComponentModel`'s partition rule is that a component is entirely linear or entirely nonlinear.
So a PIM-capable block must be expressible as `i = f(v)` with no memory, which means its ideal S
must be **frequency-flat**, and its Y must exist.

That is true of the circulator, the attenuator, the switch and the in-phase/180° coupler. It is
**not** true of the quadrature (90°) path, whose ±j is a frequency-domain operator, nor of the
filter and duplexer, whose whole purpose is frequency dependence. Three facts make this tractable
rather than a wall:

1. **`ModelKind` is read off the model INSTANCE, not the type name** (`SParameterEngine:300`,
   `NonlinearDcEngine:281/1139`, `ElaboratedComponent.IsNonlinear`). A block may therefore be
   `Linear` when PIM is off — the exact ideal S stamp, zero HB nonlinear cost — and `Nonlinear`
   when the user turns PIM on. The **net convention does not change with it**: `ZPortModel` is
   proof that a LINEAR model may use the 2N-net ± pair convention that nonlinear models require.
2. **The weighting-function mechanism carries a frequency-domain factor INSIDE a nonlinear model.**
   `NonlinearResult.Terms` contributes `H[w](ω)·FT{f_w(v(t))}`, and `ComponentModel.Weight(w, ω)` is
   virtual — honoured by HB (`HbNewton:349/783/980`), by DC (`NonlinearDcEngine:1206`, which already
   expects `H(0)` to be zero for some w) and by `StampLinearized`. A quadrature term is
   `H[2](ω) = −j·sign(ω)`, which is what makes PIM available on the 90° hybrid at all.
3. **An attenuator with `Loss = 0` and a PIM spec is a standalone PIM generator.** Anything that
   cannot host PIM internally (the filter, the duplexer) is served by placing one in front of it,
   and that is a better answer than bolting a memoryless nonlinearity onto a rational transfer
   function.

## The blocks

| Kind (tile) | Engine ref | Ports (nets) | Linear form | PIM |
|---|---|---|---|---|
| `Balun` | `Balun` | 3 (6) | flat S, matched unbalanced port, amplitude/phase imbalance | no (SYS-3 decision D3) |
| `Circulator` | `Circulator` | 3 (6) | flat real S; IL, isolation, return loss | yes |
| `Coupler` | `Coupler` | 4 (8) | flat S; coupling, directivity, 0/90/180° | yes |
| `Hybrid90` | `Coupler` | 4 (8) | the same component at 3.01 dB, 90° | yes |
| `Switch` / `SwitchD` | `Switch` | 2 / 3 (4 / 6) | flat real S per `State`; IL, isolation, reflective or absorptive off state | yes |
| `Atten` | `Atten` | 2 (4) | flat real S; loss, return loss | yes |
| `Amp` | `Amp` | 2 (4) | **nonlinear**: gain, return loss, IP3, unilateral | n/a (it has IP3) |
| `Filter` | `Filter` | 2 (4) | rational S(ω) from a prototype; dynamic glyph | refused, by name |
| `Duplexer` | `Duplexer` | 3 (6) | two `Filter` stamps sharing the common node | refused, by name |
| `Mixer` / `MixerD` | `Mixer` | 3 (6) | already shipped — gains the System filter only | n/a |

## The briefs

| # | Brief | Depends on | What it buys |
|---|---|---|---|
| SYS-1 | `brief-sys-1-symbols-and-palette.md` | — (**owner approval gate**) | every glyph, the `System` palette category, the tiles. No electrical behaviour. |
| SYS-2 | `brief-sys-2-ideal-s-block.md` | — (engine half needs no glyph) | `IdealSBlockModel` + the wave-constraint stamp + `Atten` and `Switch` as its first two users |
| SYS-3 | `brief-sys-3-balun-circulator-coupler.md` | SYS-2 | balun, circulator, directional coupler, 90° hybrid — PIM off |
| SYS-4 | `brief-sys-4-passive-intermod.md` | SYS-3 | the PIM overlay: the nonlinear mode, the quadrature weighting term, the intercept arithmetic |
| SYS-5 | `brief-sys-5-ideal-amplifier.md` | SYS-2 (for the S half only) | the ideal PA: gain, return loss, IIP3/OIP3, unilateral, no DC |
| SYS-6 | `brief-sys-6-ideal-filter.md` | SYS-2 | response synthesis (Butterworth, Chebyshev, inverse Chebyshev, Bessel, elliptic), the dynamic glyph, the duplexer |
| SYS-7 | `brief-sys-7-user-docs.md` | all of the above | a dedicated **System Components** chapter, the per-component entries the in-app Help contract needs, and the Doc Gen run |

SYS-1 is first because the owner must approve the glyphs and because every tile in every later
brief lands on one. SYS-2's **engine half does not wait for that approval** — the model, the stamp
and the Core tests are independent of the artwork.

## Decisions the owner must make before or during the work

Each is stated again, in place, in the brief that needs it.

- **D1 (SYS-1) — SETTLED, 2026-08-31.** The glyphs were approved off a rendered sheet. Two
  corrections and one substitution are already in SYS-1: the duplexer's antenna lead now reaches its
  pin and its TX/RX labels moved inside the body, and **the filter glyph IS the match glyph** —
  the same picture, built out of `Match`'s own primitives, because impedance matching is a form of
  filtering and the library should say so. Still open there: the balun's frame, and whether a 180°
  hybrid ships beside the 90° one.
- **D2 (SYS-1).** Where `System` sits in the palette's category order, and that the two mixer tiles
  join it as an `ExtraCategories` membership while keeping `Devices` primary.
- **D3 (SYS-3).** The balun: a 3-port, ground-referenced, with imbalance knobs (recommended), or a
  2-port whose second port is the floating balanced pair — an exact ideal transformer, but with
  no way to express imbalance.
- **D4 (SYS-4).** PIM stated as an absolute product level in dBm at a stated carrier power
  (datasheet form, recommended) or in dBc.
- **D5 (SYS-5).** The amplifier's intercept as one `IP3` field plus an `IP3Ref` selector
  (Input/Output, recommended) or as two fields that can contradict each other.
- **D6 (SYS-6).** Whether the elliptic response ships in the first pass. It is the only item in the
  series needing mathematics the repository does not have (Jacobi elliptic functions and the degree
  equation); everything else reuses `MatchPoly`.

## Conventions binding every brief here

- **Write-ups go to the area's `RESOLVED.md`** — `src/Core/RESOLVED.md` for models and the stamp,
  `src/Ui/RESOLVED.md` for symbols, palette and net extraction. **Never write to any `CLAUDE.md`.**
  If a sentence already in a `CLAUDE.md` or a `docs/design/*.md` becomes false, correct that
  sentence in place with a dated note and add nothing else.
- **`src/Engine` is not touched by this series.** Every block is an ordinary `ComponentModel`. If a
  block appears to need an engine change, stop and report — it is a sign the formulation is wrong,
  not that the engine is missing something.
- **The gate does its own arithmetic.** Never assert a model's behaviour by reading a number back
  out of the model. Compute the expected S (or the expected IM3 level) independently in the test
  from the parameters the user typed, exactly as `MixerModelTests` does.
- **Ideal means the term is absent, not small.** Follow `MixerModel`'s two constants: a
  non-ideality's "off" default is an honest large number (200 dB, 100 dBm) and the model snaps it to
  EXACTLY ideal above a threshold, so a freshly placed block stamps no leakage entry at all.
- **A non-real S obeys `S(−ω) = conj(S(ω))`**, keyed on the sign of the `omega` the stamp is handed.
  Verify what HB actually passes rather than assuming it only stamps ω ≥ 0; the rule is correct
  either way and costs one line.
- **No new timing tests.** Structural and closed-form gates only (root `CLAUDE.md`; the existing
  `Category=Benchmark` tier is not to grow for this work).
- **A message thrown below the UI firewall needs its line in
  `tests/Firewall.Tests/user-facing-text-allowlist.txt`** — prefer a `Diagnostic` where the file's
  own header says to.
- **Name no vendor, no commercial tool and no specific process kit** anywhere — root `CLAUDE.md`
  §Commercial Vendor References. Grep before finishing.
- **Never commit.** The owner commits.
- Run the test suite ONCE and read `tests/<Project>.Tests/TestResults/last-run.trx` for failures;
  scope the run to the projects the change can reach.
