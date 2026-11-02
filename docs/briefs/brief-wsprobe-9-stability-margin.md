# Sonnet Brief — WSP-9: the stability margin (Winslow 2024) and the margin envelope (Winslow 2025)

**Read `brief-wsprobe-0-overview.md` first** — §1.2 (the two papers and how they are cited), the
register rows for `M-Eq.` and `E-Eq.`, typo-register entries T-16…T-18, and decisions D-12
(rewritten 2026-09-08) and D-16. **This brief lands after WSP-3 and before WSP-4** (D-1): WSP-4
draws what this brief computes. Depends on WSP-2 (`ZG`, `ZL`, `YG`, `YL`, the Kurokawa search) and
WSP-3 (`wsp_terminate`, `wsp_loadpull`, `wsp_ndf`).

References, both held by the owner outside the repository (`<owner>/claude/Stability/`):

> **[M]** T. A. Winslow, "A Novel Stability Margin for Transfer Functions," *Proc. 19th European
> Microwave Integrated Circuits Conference (EuMIC)*, Paris, Sept. 2024, pp. 291–294,
> DOI 10.23919/EuMIC61603.2024.10732614. Cited here as `(M-Eq. n)`; its four unnumbered displays
> are cited as `(M-rY)`, `(M-iY)`, `(M-rH)`, `(M-iH)`.
>
> **[E]** T. A. Winslow, "Stability Envelope Using Nodal Transfer Functions," *Proc. 20th EuMIC*,
> Utrecht, Sept. 2025, pp. 254–257, DOI 10.23919/EuMIC65284.2025.11233915. Cited as `(E-Eq. n)`.

The vendor rule of overview §1 applies with extra force here: [M] names the author's employer on
its title page and a commercial simulator in its §II; [E] names the same simulator, a commercial
pole-zero tool, **and a transistor part number in its Fig. 5**. None of those strings may enter the
repository. The eleven element values in that figure are public numbers and may.

**Scope.** `src/RfCore/Stability/WspMargin.cs` (the scalar core and its cube wrappers), the
expression-engine built-ins, two new engine default cubes per probe, the run-summary line, the
threshold diagnostic, the margin over the envelope, an NDF over the envelope, the [E] reduction as an
oracle for `wsp_terminate`, and the retirement of the D-12 placeholders. **Nothing in `src/Ui`** —
WSP-4 surfaces every item named here.

---

## 1. What the margin is

[M] §I–II, in a paragraph. `H0` and `Y0` are the rigorous nodal stability functions (their
denominators carry the network determinant), but they have **units**, and their absolute trajectory
in the complex plane is set by the node's impedance level: a 0.05 mm and a 5 mm FET tuned to the same
Rollett `K` have driving-point loci that differ by orders of magnitude, so `1/H0` on a polar chart
gives a binary answer (a Kurokawa crossing or not) and no sense of *how close*. The way out is that
both are sums of bidirectional immittances — `1/H0 = YG + YL` (M-Eq. 1), `1/Y0 = ZG + ZL`
(M-Eq. 2) — and Kurokawa's condition on each (M-Eq. 7, 8) is a statement about the **relative** size
of the two halves: the real parts cancelling, the imaginary parts cancelling. Normalising each half
against the other gives four bounded, unitless proxies, and their mean is a margin that means the same
thing at every node of every circuit.

The paper's own result (§III): a K-band three-stage amplifier simulated stable by polar inspection of
every gate and drain `H0`/`Y0` and by Rollett, which oscillated at 1.16 GHz on the bench after a
capacitor vendor change; the margin functions showed a −50 dB collapse at 1.06 GHz in the *original*
small-signal simulation, and a 10 Ω de-Q resistor recovered it. That is what "first class" has to
mean here: the margin is a default output, it is in the run summary, and a collapse produces a
message without anyone having asked for a plot.

---

## 2. The definitions, transcribed

With `ZG`, `ZL` the bidirectional impedances (Eq. 65–68 of the 2023 document; M-Eq. 3, 4) and
`YG`, `YL` the bidirectional admittances (Eq. 72–78; M-Eq. 5, 6 — **the Y-forms; see T-17**) at one
probe and one frequency:

```
rY = 0                                   if  Re ZG + Re ZL ≤ 0                (M-rY, third case — FIRST, §2.1a)
   = ½ (1 + Re ZL / Re ZG)               if |Re ZG| ≥ |Re ZL|
   = ½ (1 + Re ZG / Re ZL)               otherwise

iY = ½ (1 + Im ZL / Im ZG)               if |Im ZG| ≥ |Im ZL|                (M-iY)
   = ½ (1 + Im ZG / Im ZL)               otherwise

rH, iH:  the same two functions over Re YG, Re YL and Im YG, Im YL    (M-rH, M-iH)

SM_Y0 = ½ (rY + iY)                                                    (M-Eq. 9)
SM_H0 = ½ (rH + iH)                                                    (M-Eq. 10)
```

Each of the four proxies is in `[0, 1]` by construction ([M] §II), so both margins are in `[0, 1]`;
the paper reads them in dB. **Both are required** ([M] §IV): a series-resonant instability is seen
by the series stimulus (`Y0`, hence `SM_Y0`) and a parallel-resonant one by the shunt stimulus
(`H0`, `SM_H0`), and "in rare exceptional circuits" one of them fails to detect — this is §4.10's pole
masking in margin form, and §3 below shows it on a closed form.

### 2.1 Conventions the paper leaves open — decided here, each held by a gate

- **(a) The `≤ 0` case takes precedence.** [M] lists it third. With `Re ZG = 5`, `Re ZL = −10` the
  magnitude branch alone would give `0.25`; the sum is `−5`, Kurokawa's real-part condition (M-Eq. 8)
  holds, and the margin **must** be 0 there. Test the sum first. Verified in the brief's own
  scratch check; gate (a).
- **(b) Equal magnitudes.** Both magnitude branches give the same value (the ratio is `±1`), so `≥`
  on the first branch is not a choice that changes anything. Gate (a) covers the boundary.
- **(c) Both parts zero** (the larger-magnitude term is exactly 0): define the proxy as **0.5**. The
  paper does not say; the function is genuinely discontinuous at the origin (the limit is 1 along
  `Im ZL = Im ZG → 0`, 0 along `Im ZL = −Im ZG → 0`, 0.5 along either axis), so any value is a
  convention. 0.5 is what one purely resistive side gives (`Im ZG = 0`, `Im ZL ≠ 0` ⇒ `½(1 + 0)`)
  and is continuous with it. Document this in the doc-comment.
- **(d) NaN propagates.** A degenerate node (WSP-2 D-7, `wsprobe.degenerate-node`) has NaN
  immittances and gets a NaN margin, never a 0 that reads as an instability.
- **(e) dB is `20·log10`** (overview D-16). [M] plots "Stability Margin (dB)" and never states the
  convention. The margin is a unitless ratio bounded by 1 and circuitRF's Data Display already applies
  `20·log10` to every unitless magnitude (`|Γ|`, `|S|`), so the `dB` axis does the same here with no
  special case. The paper's rule of thumb — investigate "any sudden decrease (below −15 dB)" ([M] §IV)
  — is `0.178` under this convention and the docs print both numbers, so a reader who assumes
  `10·log10` (`0.032`) is not misled silently.
- **(f) Kurokawa's third condition is not in the margin.** `∂Im/∂ω > 0` (M-Eq. 7, 8) is what
  separates a start-up from a benign crossing; the margin measures distance to the first two only.
  `wsp_unstable_freq_kurokawa` stays the *detector*, the margin is the *distance*, and every place
  one is reported the other is beside it (§4, §6, WSP-4 R-wsp4-7).

### 2.2 Properties the definitions guarantee — these are the analytic gates

- `0 ≤ rY, iY, rH, iH ≤ 1`, hence `0 ≤ SM ≤ 1`.
- **Unitless in the sense that matters:** scaling `ZG` and `ZL` by the same positive real leaves
  `SM_Y0` unchanged (likewise `YG`, `YL` for `SM_H0`). This is the cross-node comparability [M] §I
  wants, and the reason the D-12 placeholders are retired (§7).
- `SM_Y0 = 1` iff `ZL = ZG` with `Re > 0`. **A conjugate match is not the top of the scale:**
  `ZL = conj(ZG)` gives `rY = 1`, `iY = 0`, `SM_Y0 = 0.5 = −6.02 dB`. The user docs must say this,
  because a designer who expects a matched node to read 0 dB will read −6 dB as a problem.
- **The −12 dB floor.** If `Re ZG > 0` and `Re ZL > 0` then the ratio is in `(0, 1]`, `rY ∈ (0.5, 1]`,
  and `SM_Y0 ≥ 0.25` (−12.04 dB). Contrapositive, and the interpretive rule the docs carry: **a margin
  below −12 dB certifies that one side of the node presents negative resistance at that
  frequency.** The same for `SM_H0` with conductances.
- `SM_Y0 = 0` iff `Re(ZG + ZL) ≤ 0` and `Im ZL = −Im ZG` — the two static Kurokawa conditions on
  `1/Y0`. So at a frequency the Kurokawa search reports on `1/Y0`, `rY = 0` exactly (the real part
  is negative on the crossing) and `iY → 0` as the grid is refined; gate (e) states the sampled bound.
- `SM_Y0` and `SM_H0` are **not** functions of each other, even with zero feedback (`YG = 1/ZG`,
  `YL = 1/ZL` there, and the ratio of reciprocals is not the reciprocal of the ratio). §3 shows the two
  collapsing at different frequencies on one fixture.

---

## 3. The closed form — a split series resonator

**Do not use WSP-1's resonator as printed for this gate.** There the probe sits at the Term, so
`ZG = RS` is purely real, `Im ZG = 0`, and `iY ≡ 0.5` at every frequency by §2.1c — the margin is
**flat** (`0.375` for `R1 = −5 Ω`, `0.25` for `−20 Ω`) and shows no resonance at all, because the
paper's proxy normalises one reactance *by the other* and a resistive side has none to offer. That is
the definition, not a defect, and the docs say so in one sentence ("a probe against a purely
resistive termination reads the resonance on the other side's margin only"). The fixture that shows
the resonance splits the reactance across the probe:

```
Term RS = 10 Ω  ──  L1 = 1 nH  ──[ WSProbe, G left ]──  C1 = 10 pF  ──  R1 (−5 Ω or −20 Ω)  ──  ground
ZG = RS + jωL1          ZL = R1 − j/(ωC1)          f0 = 1/(2π√(L1C1)) = 1.5915 GHz
YG = 1/ZG, YL = 1/ZL    (a single loop has no feedback across the probe: Eq. 89)
```

Closed forms: `rY = ½(1 + R1/RS)` when `RS + R1 > 0`, else 0 — a constant; `iY = ½(1 − 1/(ω²L1C1))`
above `f0` and `½(1 − ω²L1C1)` below (the two reactances have opposite signs, so the ratio is negative
and passes through −1 at `f0`); `SM_Y0` is their mean. On the 0.5–3 GHz, 2001-point grid of WSP-1:

| `R1` | `rY` | `SM_Y0` min (dB) at | `SM_H0` min (dB) at | `Re(YG + YL) ≤ 0` on |
|---|---|---|---|---|
| −5 Ω (stable at 10 Ω) | 0.25 | 0.12509 (−18.06 dB) at 1.5913 GHz | 0.10175 (−19.85 dB) at 1.7337 GHz | 1.7337–3.0 GHz |
| −20 Ω (unstable) | 0 | 0.00009 (−80.5 dB) at 1.5913 GHz | 0.15852 (−16.00 dB) at 1.8600 GHz | 1.8613–3.0 GHz |

Three things to read off, each a gate in §8(c): the `SM_Y0` notch sits at `f0` and its depth is set by
`rY` (the negative-resistance ratio) — at `−20 Ω` it is `½·iY` exactly and reaches the grid's
resolution of zero; `SM_H0`'s minimum is at a **different** frequency (where `Re(YG + YL)` changes
sign — the admittance sum is negative above that frequency although the impedance sum is positive
everywhere for `−5 Ω`), which is §4.10's masking made visible; and the stable `−5 Ω` case already sits
below the paper's −15 dB rule at `f0` — a node one negative-resistance step from oscillating *has*
little margin, and the threshold message (§4.3) fires on it by design.

---

## 4. The library and the engine

### 4.1 `src/RfCore/Stability/WspMargin.cs`

```csharp
public static class WspMargin
{
    public static double Proxy(double g, double l);                 // (M-rY)/(M-iY) over one part pair, §2.1 a–c
    public static double ProxyReal(double g, double l);             // Proxy with the sum-rule branch first
    public static (double rY, double iY, double sm) FromZ(Complex zg, Complex zl);   // M-Eq. 9
    public static (double rH, double iH, double sm) FromY(Complex yg, Complex yl);   // M-Eq. 10
    public static WspMargins Of(in WspProbeQuad q);                 // both, from one probe's block via WspNodal.ZG/ZL/YG/YL
}
public readonly record struct WspMargins(double rY, double iY, double SmY0, double rH, double iH, double SmH0)
{ public double Sm => Math.Min(SmY0, SmH0); }
```

Doc-comments cite [M] by equation and display, and §2.1 by letter. **No `1e-15` in any denominator**
(D-7): `Proxy` tests the larger magnitude for exact zero and returns 0.5.

### 4.2 Built-ins (`Evaluator.Wsp.cs`, the `ScalarFromWsp` pattern with a Real result)

| Name | Returns | Cite |
|---|---|---|
| `wsp_rY(wsp, idx)`, `wsp_iY(wsp, idx)`, `wsp_rH(wsp, idx)`, `wsp_iH(wsp, idx)` | Real `{…, freq}` in `[0, 1]` | M-rY … M-iH |
| `wsp_SM_Y0(wsp, idx)`, `wsp_SM_H0(wsp, idx)` | Real `{…, freq}` in `[0, 1]` | M-Eq. 9, 10 |
| `wsp_stability_margin(wsp, idx)` | `min(SM_Y0, SM_H0)` elementwise — the reserved name of D-12, now answering | this brief |
| `wsp_sm_z(ZG, ZL)`, `wsp_sm_y(YG, YL)` | the margin of any two same-shaped Complex immittance cubes | M-Eq. 9, 10 |

`wsp_sm_z`/`wsp_sm_y` are the primitives the others call (`wsp_SM_Y0(wsp, i) ≡ wsp_sm_z(wsp_ZG(wsp, i),
wsp_ZL(wsp, i))` — gate (d)); they exist so the margin can be taken of a probe pair's in-situ blocks,
of the envelope's re-terminated immittances (§5), or of a `Z_in` against a source impedance from a
plain S-parameter source in the Data Display. The `dB` of any of these is the ordinary `db(...)`
(§2.1e); no `_dB` variant is added.

### 4.3 Engine defaults, accessors, summary, threshold

**R-wsp9-1. Two more default cubes per probe** in `SParameterEngine.AddWspCubes`, beside the six of
WSP-1: `SM_Y0:<label>` and `SM_H0:<label>`, Real over `{freq}`, computed through `WspMargin.Of` on
the same `WspProbeQuad` (D-2: one implementation, gate (d) holds bit-identity with the built-in).
The accessor list in `Evaluator.cs` (`"idx" or "H0" or …`) gains `SM_Y0` and `SM_H0`, so
`SP1.SM_Y0("GATE")` resolves like `SP1.H0("GATE")`. A parametric sweep stacks them as it stacks `H0`.

**R-wsp9-2. The run summary.** `PrintWsProbes` (`src/Cli/CliEntry.cs`) appends to each probe's line
the minimum of each margin over the sweep with its frequency, in dB, and `--json`'s `wsprobes[]`
rows gain `smY0Min`, `smY0MinHz`, `smH0Min`, `smH0MinHz` (linear values in JSON; dB is a display):

```
WSProbe GATE idx=1  H0(0.5 GHz)=…  ZG(0.5 GHz)=…  SM_Y0 min −18.1 dB @ 1.5913 GHz  SM_H0 min −19.8 dB @ 1.7337 GHz
```

**R-wsp9-3. The threshold diagnostic.** The S-parameter analysis line takes
`MarginThreshold=<dB>` (default **−15**, the paper's rule; `MarginThreshold=none` disables). After the
sweep, for each probe whose `min(SM_Y0, SM_H0)` falls below it, one **Info**-severity diagnostic
`wsprobe.margin-below-threshold:<label>`:

> WSProbe 'GATE': stability margin below −15 dB (0.178): SM_Y0 = −18.1 dB at 1.5913 GHz, SM_H0 =
> −19.8 dB at 1.7337 GHz. A margin below −12 dB means one side of the node presents negative
> resistance there. Winslow (EuMIC 2024) §IV: find the root cause of any sudden decrease.

It reaches the Messages pane and the CLI through the existing warnings channel. It is Info, not
Warning, because the −5 Ω resonator of §3 is stable and fires it: the message is "look here", not
"this is wrong". `HarmonicBalanceAnalysis` takes the same key for WSP-5's `ssfreq` sweep. The
text goes in `tests/Firewall.Tests/user-facing-text-allowlist.txt` (the series' convention).

**R-wsp9-4. `explain --analysis`** lists `MarginThreshold` with its effective value beside the probe
list, and `check` says nothing new (the margin is a run result, not a document property).

---

## 5. The envelope: margin and NDF under mismatch, with no re-simulation

[E]'s thesis is that the margin swept over source and load VSWR is a stability envelope that "tracks
NDF perfectly, yet is more informative" — its Fig. 8/9: at `ρ = 0.875` NDF encircles at two phase
points and the margin collapses there; at `ρ = 0.874` NDF says stable (zero encirclements) and the
margin still reads −40 dB, because the circuit is on the edge and NDF is binary. circuitRF already has
the mechanism — WSP-3's rank-1 re-termination gives the **complete** `wsp'` of the mismatched circuit
— so both halves of [E]'s comparison are post-processing calls.

**R-wsp9-5. `wsp_loadpull_margin(wsp, idxS, idxL, idx, gammaS, gammaL [, Z0 = 50])`.** Same
arguments and precondition as `wsp_loadpull` (WSP-3 §6.2; `wsprobe.envelope-probe-not-at-termination`
unchanged). For each grid point, `wsp_terminate`, then `WspMargin.Of` on probe `idx`'s block of the
re-terminated matrix — the `Z^R_G`, `Z^R_L`, `Y^R_G`, `Y^R_L` of E-Eq. 11/12 (**with T-16's
correction**). Returns `SM_Y0env {gS, gL, freq}`, `SM_H0env {gS, gL, freq}` (Real) and
**`SMenv {gS, gL}`** = the minimum over frequency of `min(SM_Y0, SM_H0)` — the one number per
termination that [E]'s Fig. 6–9 plot against phase. The Γ grids are carried as axis labels exactly as
`wsp_loadpull` carries them.

**R-wsp9-6. `wsp_loadpull_ndf(wsp, wsp_passive, idxS, idxL, probes, gammaS, gammaL [, Z0])`.** The
NDF of the re-terminated circuit over the probe set, per grid point: `wsp_terminate` applied to `wsp`
**and, with the same terminations, to `wsp_passive`** (WSP-3's `wsp_ndf` inputs — a second run with
the active devices' controlled sources scaled to zero, the `NDFgm` pattern of WSP-6 §3, or WSP-6's
native `wsp_passive` once it exists), then `wsp_ndf` on the pair. Returns `NDFenv {gS, gL, freq}`
(Complex) and `NDFenc {gS, gL}` (Real, the rounded final encirclement count). This is exact — both
matrices are the exact `wsp` of the re-terminated network — with the document's own caveat
(p. 112–113) that it is the *reduced* NDF over the probed nodes and is complete only if the probe set
covers every node that can hide a pole; say so in the doc-comment and the user docs. [E] re-ran a full
NDF sweep per grid point to draw its blue curves; circuitRF does not have to.

**R-wsp9-7. The [E] reduction as an oracle for `wsp_terminate`.** Implement, **in the test project
only**, E-Eq. 1–8 as printed (with E-Eq. 4's transpose — D-9's rule, which [E] states independently):
build `V_m`, `I_m` from the `wsp` entries of the source, load and suspect probes per the Appendix
mapping, `Y = (V_m⁻¹ I_m)ᵀ`, add `ȲS`, `ȲL` (E-Eq. 5), reduce to the suspect probe's two terminals by
E-Eq. 6–8, and read `H0`, `Y0`, `ZG`, `ZL`, `YG`, `YL` by E-Eq. 9–12. Compare against
`wsp_terminate` + WSP-2's functions on a **non-reciprocal** three-probe fixture (WSP-3's two-stage
amplifier with global feedback). This is a second, independent derivation of WSP-3 §6's rank-1 result
and pins it from the other side — gate (f), which also asserts that E-Eq. 11 **as printed** disagrees,
so T-16 is held by a test rather than a note.

---

## 6. Where the margin appears (the contract WSP-4 draws to)

Everything below is a call into this brief's library; WSP-4 owns the pixels.

- Trace card, WSProbe section, group **Stability margin** (replacing the "Normalised" group of the
  original WSP-4 table): `SM_Y0`, `SM_H0`, `SM` (= min), and the four proxies; Rect only (linear or
  dB); the margin plot draws a dashed reference at the analysis' `MarginThreshold` and the **−12 dB
  floor** as a second, lighter line with the §2.2 sentence in its tooltip.
- Readout beside every margin trace: minimum, its frequency, and the Kurokawa frequencies of the
  matching driving-point function (`SM_Y0` ↔ `1/Y0`, `SM_H0` ↔ `1/H0`) — the two halves of §2.1f
  on one card.
- Envelope card: `SMenv` against `θS` (one curve per `θL`, the family mechanism) and against `θL`,
  in dB; `NDFenc` overlaid on the same axes as a step curve when a passive `wsp` is available —
  [E]'s Fig. 6–9, redrawn. The `|ΓS|`/`|ΓL|` fields become a ladder (`0.9, 0.875, 0.874` is the
  paper's) so the ρ at which encirclements first appear is read off the same card.
- `plot --trace cube=wsp,probe=GATE,metric=SM_Y0,y=db`.

---

## 7. Retiring the D-12 placeholders

The overview's D-12 promised that the published margin would replace circuitRF's normalised loci the
day it arrived. It has. **Remove** `wsp_nZ`, `wsp_nY`, `WspNodal.NormalizedLocusSeries/Shunt`,
`WspNodal.StabilityMargin`'s refusal, `MarginNotTranscribedKey/Message`, the
`wsprobe.margin-not-transcribed` allowlist line, their tests (`WspNodalTests` R-wsp2-14(i)'s
normalised-locus and refusal cases, `WspNodalFunctionTests` ~l. 204, `WsProbeDerivedMetricsCliTests`
~l. 185–211), the "Normalised" rows in `docs/design/expressions.md` §built-ins and
`docs/design/measurements.md`, and design-note §5.6. Two normalised quantities beside each other —
one Winslow's, one ours — is exactly the confusion the notation rule of overview §1 exists to prevent,
and the loci carried nothing the `1/H0`, `1/Y0` polar traces and the margin do not carry between them.
Gate (j) greps for the names.

---

## 8. Gates

`tests/RfCore.Tests/Stability/WspMarginTests.cs`, `tests/Engine.Tests/Linear/WspMarginEngineTests.cs`,
`tests/Ui.Tests/Cli/WsProbeMarginCliTests.cs`. Fixtures analytic and vendor-free (D-15).

**(a) — the transcription.** `Proxy`/`ProxyReal` against a straight second transcription of the four
displays written independently in the test (the three-branch form, in the paper's order, with the
sum test moved first) on 10⁵ random pairs, plus the boundaries: equal magnitudes both signs, one part
zero, both zero (0.5), the precedence example `(5, −10) → 0` and `(10, −5) → 0.25`, NaN in → NaN out.
Every value in `[0, 1]`.

**(b) — the properties of §2.2.** Positive-real scaling invariance (1e-15); `SM_Y0 = 1` iff `ZL = ZG`
with positive real parts; conjugate match reads exactly 0.5; the −12 dB floor: over 10⁵ random pairs
with both real parts positive `SM_Y0 ≥ 0.25` with equality approached, and every pair below 0.25 has a
negative real part on one side.

**(c) — the closed form of §3**, both `R1` values, 2001 points: `SM_Y0` and `SM_H0` from the engine
run equal the closed forms to 1e-12; the minima and their frequencies match the table (to a grid step);
at `−20 Ω`, `rY ≡ 0` and `SM_Y0 ≡ ½·iY` exactly; `SM_H0`'s minimum is not at `f0` and coincides with
the first frequency where `Re(YG + YL) ≤ 0`. And the **flat** case: WSP-1's own resonator (probe at
the Term) reads `SM_Y0 = 0.375` at every point for `−5 Ω` — the sentence in §3 is held, not assumed.

**(d) — one implementation.** `SM_Y0:<label>` from the run is bit-identical to
`wsp_SM_Y0(SP1.wsp, SP1.idx(label))`, to `wsp_sm_z(wsp_ZG(…), wsp_ZL(…))`, and to
`WspMargin.Of` called on the block; `SP1.SM_Y0("X")` resolves; a `Pin` sweep stacks `[Pin, freq]`.

**(e) — Kurokawa consistency.** On the `−20 Ω` fixture, at the frequency `wsp_unstable_freq_kurokawa`
reports on `1/Y0` (1.5915 GHz): `rY = 0` at both neighbouring grid points, and `SM_Y0` at the nearer
one `< 1e-3` (measured 9e-5). On the `−5 Ω` fixture the search reports nothing and the margin still
notches — the "more informative" claim of [E] on the simplest circuit.

**(f) — the [E] oracle** (R-wsp9-7): `H0'`, `Y0'`, `ZG'`, `ZL'`, `YG'`, `YL'` at the suspect probe from
E-Eq. 1–12 (corrected) equal the rank-1 route to 1e-10 on the non-reciprocal fixture at
`ΓS = 0.5∠60°`, `ΓL = 0.3∠−120°`; E-Eq. 11 **as printed** differs from `ZG'` by more than 1e-3
relative; the un-transposed `Y_m` differs from the core `Y`.

**(g) — the margin envelope on WSP-3's terminated resonator** (Term 10 Ω, `R1 = −5 Ω`, `|Γ| = 0.9`,
24 phases): `rY' = 0` exactly on the arc where `Re ZS(θ) ≤ 5 Ω` and `> 0` elsewhere; `SMenv` on that
arc is below −40 dB; `wsp_loadpull_unstable`'s flagged set is a subset of the arc.

**(h) — `wsp_loadpull_ndf`** against a re-run with the Terms changed (the WSP-3 gate (e) pattern) at
four grid points: `NDFenv` equal to 1e-9, encirclement counts equal.

**(i) — Ohtomo's Type-A amplifier** (the fixture WSP-7 also uses), redrawn from M. Ohtomo, "Stability
Analysis and Numerical Simulation of Multidevice Amplifiers," *IEEE Trans. MTT*, vol. 41, no. 6/7,
1993, with the line impedances/lengths of [E] Fig. 4 at 10 GHz, the eleven FET element values of [E]
Fig. 5 as an `SDD` (the `NDFgm` scaling pattern written in), `Rb = 30 Ω`, WSProbes S, G, L as [E]
Fig. 1. Asserted: at nominal 50 Ω the Kurokawa search finds nothing and `NDF_enc = 0`; at `ρ = 0.9`
(both sides, 10° steps) the set of grid points with `NDFenc ≥ 1` is non-empty and `SMenv < −30 dB` on
every one of them; every point `wsp_loadpull_unstable` flags is in that set. **Measured and recorded,
not asserted:** the smallest ρ on a `0.85…0.90` ladder at which any encirclement appears, the `SMenv`
minimum one step below it, and the frequency band of the instability ([E] reports 200–400 MHz,
0.874/0.875 and −40 dB for its model; ours is the same circuit with an `SDD` and a redrawn layout, and
the brief does not promise its numbers). A disagreement between the Kurokawa set and the NDF set at
an arc's edge is printed, not asserted away.

**(j) — retirement.** A source scan of `src/` and `docs/design/` for `wsp_nZ`, `wsp_nY`,
`NormalizedLocus`, `margin-not-transcribed` returns nothing; `wsp_stability_margin(SP1.wsp, 1)`
evaluates to a Real cube.

**(k) — D-13.** A run without probes is byte-identical to before; a run with probes and
`MarginThreshold=none` emits no margin diagnostic; the default emits it for the `−5 Ω` fixture
(minimum −18 dB) and not for a fixture whose minimum is above −15 dB.

**(l) — CLI.** The summary line and the `--json` fields of R-wsp9-2, as a process, against the
in-process `DataSet`.

## On completion

Findings to `src/RfCore/RESOLVED.md` (the §2.1 conventions and the flat-resonator observation belong
there, as the WSP-3 findings do) and `src/Engine/RESOLVED.md`; **never a `CLAUDE.md`**. Append
**§9 "The stability margin"** to `docs/design/stability-wsprobe.md` (definitions, §2.1 a–f, §2.2, the
§3 table, the envelope functions, T-16/T-17) — WSP-5/6/8's sections move down one number, and their
briefs already say §10/§11/§12. Delete design-note §5.6. Update `docs/design/measurements.md` and
`docs/design/expressions.md` for the new built-ins and the removed ones, and `docs/design/cli.md` for
the summary line and `MarginThreshold`.
