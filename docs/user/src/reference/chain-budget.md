---
title: RF Chain Budget
slug: reference/chain-budget.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > RF Chain Budget
lede: A receive chain stage by stage, each stage a System block with its own parameters - gain, noise figure, intercepts, compression, sensitivity and dynamic range in closed form, an N-way array included - and one action that writes the chain as test benches whose simulations put a number beside every one of them.
keywords: chain budget, link budget, cascade, receiver, Friis, noise figure, cascaded IP3, IIP3, OIP3, P1dB, sensitivity, MDS, noise floor, SFDR, SNR, phased array, array gain, array factor, beam steering, taper, combiner, combining tree, active combiner, Wilkinson, phase shifter, limiter, splitter, superhet, levels, blocker, IM3, canceller, cancellation depth, EVM, constellation, demodulation, .cchain
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What it is</a></li>
<li><a href="#opening">Opening it</a></li>
<li><a href="#inputs">The system inputs</a></li>
<li><a href="#stages">The stages, and the parts a stage can be</a></li>
<li><a href="#cascade">What the cascade computes</a></li>
<li><a href="#array">Arrays: N elements into a combiner</a></li>
<li><a href="#variants">The combiner's four variants</a></li>
<li><a href="#transfer">Transfer to Schematic</a></li>
<li><a href="#steering">Steering the beam in simulation</a></li>
<li><a href="#levels">Levels at every node</a></li>
<li><a href="#canceller">The canceller</a></li>
<li><a href="#demo">The modulated-signal demo</a></li>
<li><a href="#agreement">How closely the simulation agrees</a></li>
<li><a href="#format">The .cchain file</a></li>
<li><a href="#cli">From the command line</a></li>
</ol>
</nav>

## What it is {#what}

A **chain budget** is the spreadsheet every receiver starts as: an ordered list of stages, each with a gain, a noise
figure and an intercept, and the cascade worked out stage by stage. The RF Chain Budget is that table, with two
differences that matter here:

- **Every stage is a part circuitRF already has.** An amplifier stage is the System [Amplifier](system-components.html#amp)
  with the Amplifier's own `Gain`, `NF`, `IP3` and `IP3Ref`; a filter stage is the [Filter](system-components.html#filter)
  with its own `Response`, `Form`, band and `IL`. The budget reads each part's numbers the way the part's model behaves.
- **One action turns the chain into a simulation.** **Transfer to Schematic** writes the chain as test benches of
  those same parts, with the analyses that check the budget already set up and the budget's numbers written beside the
  simulated ones. The cascade is closed-form arithmetic; the check is circuitRF's own S-parameter, harmonic-balance and
  envelope analyses.

## Opening it {#opening}

- **Tools ▸ RF Chain Budget** opens a new chain: an LNA, a filter and a second amplifier.
- A `.cchain` in the workspace tree opens on a double-click.
- **File ▸ New ▸ Examples ▸ RF Chain Budget** has a 2.45 GHz superhet, a four-element steered array and a front end
  with a canceller, each already transferred.

{{ui: chain-budget}}

The window has three parts, and every edit recomputes all of them at once:

- **The chain**, input on the left, drawn two ways that share one selection:
  - **Blocks**: one idealised block per stage, drawn with its System block's own symbol, with its own gain, NF and
    intercept under it and the cascade at its output (Σ) under that. An array is drawn compactly: the branch's stages
    once, each marked "× N", into one combiner block drawn as its variant — a direct combiner one N:1 symbol, a tree its
    levels of 2:1 (a deep one as "× levels"), an amplifier after each when it is active.
  - **Schematic**: the schematic Transfer would write, drawn as every circuitRF schematic is, without the bench's
    sources, analyses and isolators: each stage is its own parts (a composed stage its composition, a tree its levels
    of Combiners), and in an array every element's branch is drawn into the combiner. Above each stage are four lines:
    its own gain and NF, its own intercept and compression point, and the cascade's gain, NF, IIP3 and SNR at its output.
    Scroll to zoom, drag with the middle button to pan, double-click to fit.

  Clicking a block, or a part in the schematic, selects its stage, and the selected stage is drawn selected in both.
- **The stages**, one row each: the name, the part, and the part's parameters.
- **The cascade, the levels and the canceller**, three [Data Display](data-display.html) tabs: the cascade table, one
  row per stage; the [levels at every node](#levels), the budget's lines with the transferred benches' simulated
  points once they have run, where a marker on a trace selects its node's stage in both views; and, for a chain with a
  canceller, [its depth](#canceller) against its errors. The chain's totals are below them.

## The system inputs {#inputs}

| Input | Meaning |
|---|---|
| Frequency | The input (RF) frequency. After a mixer the chain is at the difference frequency. |
| Bandwidth | The noise bandwidth B the noise floor, the MDS and the SNR are read in. |
| Input power | The signal at the input. In an array, at **each element**. |
| Source temperature | The noise temperature of what drives the input, an antenna's for instance. 290 K is T0. |
| Required SNR | What the demodulator needs: sensitivity is the MDS plus this. |
| Blockers | Carriers at the input besides the signal: an offset from the frequency (MHz when bare) and a level, dBm, each pair separated by `;` (`150 MHz, -40; 20 MHz, -50`). See [Levels](#levels). |

Beside **Transfer to Schematic**, **with a modulated-signal demo** adds the [demo bench](#demo) to what it writes.

## The stages, and the parts a stage can be {#stages}

A stage has a name (its instance name on the schematic), a part, and that part's parameters. Nine parts are System
blocks — the splitter and the array combiner are the library's [Combiner](system-components.html#combiner), an
active combiner that block with an Amplifier after each — and the library has no phase shifter, limiter or canceller,
so three more are **composed** of parts it does have. Transfer places exactly that composition.

| Part | What it is | Budget |
|---|---|---|
| Amplifier (`Amp`) | The System Amplifier. Any gain, negative included: a block with a gain, an NF and an intercept is one. | G = `Gain`, NF = `NF` (blank: noiseless), IIP3 from `IP3` and `IP3Ref`, IP1dB = IIP3 − 8.96 dB |
| Mixer | The System Mixer, down-converting to \|f − `Flo`\|. `Flo` is the LO the bench drives it with, at `Plo`. | G = `ConvGain`, NF = `NF` single-sideband (a DSB figure + 3.01 dB), IIP3 = `IIP3`, IP1dB = IIP3 − 8.96 dB |
| Attenuator (`Atten`) | A matched pad. | G = −`Loss`, NF = `Loss` |
| Filter | The synthesised Filter, in its passband. | G = −`IL`, NF = `IL`; a Chebyshev's ripple is not in the budget |
| Switch | A closed SPST switch. | G = −`IL`, NF = `IL` |
| Isolator (`Circulator`) | A circulator with its third port terminated. | G = −`IL`, NF = `IL` |
| Directional coupler (`Coupler`) | Its through arm, the coupled and isolated ports terminated. | G = 10·log10(1 − 10^(−`Coupling`/10)) − `IL`, NF = −G |
| Phase shifter (composed) | An Amplifier of gain −`IL` with `NF` and `IIP3`, and a TLIN of electrical length `Phase`. | G = −`IL`, NF = `NF` (blank: `IL`), IIP3 = `IIP3` (blank: linear) |
| Limiter (composed) | An Amplifier of gain −`IL`, NF = `IL`, whose intercept puts its input 1 dB compression point at `P1dB`. | G = −`IL`, NF = `IL`, IP1dB = `P1dB` |
| Canceller (composed) | A Directional Coupler whose isolated port takes a reference path that subtracts a copy of an interference from the main path. See [The canceller](#canceller). | G = −(`IL` − 10·log10(1 − c)), NF from the reference path's noise |
| N-way splitter | The [Combiner](system-components.html#combiner) from its common port, one output followed and the others terminated. | G = −(10·log10 N + `IL`), NF = −G |
| N-way array combiner | The [Combiner](system-components.html#combiner), every input an element's branch: one N:1, or a tree of 2:1s, passive or active. See [Arrays](#array) and [the variants](#variants). | Direct, passive: signal +10·log10 N − `IL`, noise −`IL`, NF = `IL` |

**P1dB follows from the intercept** for every part that compresses, because an ideal Amplifier and Mixer have one
nonlinearity, a tanh limiter, whose 1 dB compression point is IIP3 − 8.9625 dB. The budget uses the part's own law, so
the simulation it is checked against compresses where the budget says.

## What the cascade computes {#cascade}

Each row of the cascade is the chain up to and including that stage, referred to the chain's input. With G the
available-power gains, T the noise temperatures and T0 = 290 K:

| Quantity | Formula |
|---|---|
| Gain | G = Π gᵢ |
| Noise (Friis) | Te = Σ Teᵢ / Gᵢ₋₁ with Teᵢ = T0·(Fᵢ − 1); F = 1 + Te/T0; NF = 10·log10 F |
| Each stage's share | (Teᵢ / Gᵢ₋₁) / Te: the same split a noise run's `NFpct` reports |
| Intercept | 1/IIP3 = Σ Gᵢ₋₁ / IIP3ᵢ (in mW); OIP3 = IIP3 + G |
| Compression | 1/IP1dB = Σ Gᵢ₋₁ / IP1dBᵢ; OP1dB = IP1dB + G − 1 dB |
| Output power | Pout = Pin + G |
| Thermal noise floor | 10·log10(k·Ts·B) + 30 dBm, the source's own noise |
| Output noise | 10·log10(k·(Ts + Te)·B·G) + 30 dBm |
| MDS | 10·log10(k·(Ts + Te)·B) + 30 dBm: the input signal at 0 dB SNR |
| Sensitivity | MDS + required SNR |
| SNR | Pin − MDS |
| SFDR | (2/3)·(IIP3 − MDS) |

The intercept sum takes every stage's third-order products in phase, the worst case. For ideal blocks with real gains
that is exactly what happens, and the two-tone simulation agrees to 2×10⁻⁴ dB. The compression sum is an approximation of
the same form: when two stages compress at similar levels the cascade compresses a little later than it predicts (see
[agreement](#agreement)).

**A mixer's noise figure is its single-sideband one**, and an SSB figure is stated with the image sideband terminated at
T0. The budget also takes the image as filtered of every earlier stage's noise, which is what a preselector is for.

## Arrays: N elements into a combiner {#array}

A **Combiner** stage makes the chain an array. Every stage before it is one element's branch, repeated N times; the
stages after it are the one receiver the elements are combined into. Every number is then referred to **one element's**
input, and the input power is per element.

- **Signal** from the N branches arrives in phase and adds in amplitude: the combiner's signal gain is
  +10·log10 N − `IL`.
- **Noise** from the N branches is uncorrelated and adds in power: the combiner's noise gain is −`IL` only. The array's
  noise temperature is therefore one branch's Friis sum, with the combiner's excess loss as a pad, and its NF is one
  element's.
- **SNR** rises by 10·log10 N, the **array gain**: SNR = Pin + 10·log10 N − 10·log10(k·(Ts + Te)·B) − 30. The MDS and
  sensitivity per element fall by the same amount.
- **Intercepts** after the combiner see N times one element's power, so the intercept and compression points referred
  to one element fall by 10·log10 N for the stages after it, and the output-referred ones rise.

`Steer` is the phase step of the arriving wave between adjacent elements. Transfer draws the wave's extra path to
element k as a line of (k − 1)·`Steer` in front of it, and sets element k's phase shifter to `Phase` + (N − k)·`Steer`,
which brings every element to the combiner in phase. Without a phase shifter before the combiner a non-zero `Steer`
misaligns the elements, and the budget says so: its coherent sum assumes they are aligned.

## The combiner's four variants {#variants}

A Combiner stage is one of four, chosen by two parameters:

| | `Kind` = Passive | `Kind` = Active |
|---|---|---|
| **`Topology` = Direct** | one N:1 combiner of loss `IL` | the N:1 combiner, then an amplifier of `Gain`, `NF` and `IIP3` |
| **`Topology` = Tree** | log2 N levels of 2:1 combiners, each of loss `IL` | each level's 2:1 combiners each followed by an amplifier |

| Parameter | Meaning |
|---|---|
| `N` | The number of elements. A tree combines in pairs, so its N is a power of 2; any other N is refused, with Direct as the remedy. |
| `Topology` | `Direct` or `Tree`. |
| `Kind` | `Passive` or `Active`. |
| `IL` | Each combiner's loss above the ideal split, dB. A tree takes one value for every level, or one per level, the first level (next to the elements) first: `0.3, 0.4`. |
| `PathIL` | Passive only: each element path's own extra loss, N values, dB — an amplitude taper, or an imbalance. Blank: none. |
| `Gain`, `NF`, `IIP3` | Active only: each combiner's amplifier; one value, or one per level. Its 1 dB compression point is its intercept's, IIP3 − 8.96 dB, as every amplifier's here. |
| `Steer` | The arriving wave's phase step between elements (see above). |

**Each variant's budget is the cascade of its own levels**, and every number is referred to one element's input:

- The element paths first. With amplitudes a_k = 10^(−`PathIL`_k/20), N in-phase inputs of power P combine to
  (Σa_k)²·P/N and their uncorrelated noise to (Σa_k²/N)·n²; the weighting itself is a matched passive network at T0,
  adding T0·(N/Σa_k² − 1) of noise. With equal paths all three are N·P, n² and nothing.
- Then each level: a passive n-way level of loss L gains n·(1/L) in signal and 1/L in noise and adds T0·(L − 1) of
  noise; an active level's amplifier follows it with its own gain, noise figure and intercept.
- Signal and noise gains, noise temperatures and intercepts cascade through the levels exactly as they do through a
  chain: Friis with the noise gains, the intercept sum with the signal gains. The amplifiers of one level are in
  parallel, each fed by its own pair, and identical, so together they behave as one amplifier seeing the combined
  power.
- The **array gain** is the signal gain over the noise gain: (Σa_k)²/Σa_k², which is N for equal paths and less with a
  taper.

So a passive tree's loss accumulates level by level, and its noise figure is its whole loss; a direct combiner has one
loss. An active combiner's noise figure is set by its first level, and its intercept by its last, which sees the most
power: in the example's array, the active tree's IIP3 is 7 dB below the direct active combiner's.

The example's array, the same four elements into each variant (`IL` 0.5 dB, the amplifiers 10 dB, NF 2 dB,
IIP3 10 dBm), at the combiner's output and per element:

| Variant | Gain | NF | IIP3 | OIP3 | Array gain |
|---|---|---|---|---|---|
| Direct, passive | 18.52 dB | 1.33 dB | −9.19 dBm | 9.33 dBm | 6.02 dB |
| Tree, passive | 18.02 dB | 1.35 dB | −9.19 dBm | 8.83 dBm | 6.02 dB |
| Direct, active | 28.52 dB | 1.43 dB | −11.88 dBm | 16.64 dBm | 6.02 dB |
| Tree, active | 38.02 dB | 1.45 dB | −18.77 dBm | 19.26 dBm | 6.02 dB |

## Transfer to Schematic {#transfer}

**Transfer to Schematic** saves the chain and writes its test-bench cells beside the `.cchain`, named after it, each
with a Data Display of the same name that Simulate opens on the bench's own result. Every chain gets 1-Tone, 2-Tone,
Envelope and Levels; a chain with blockers a Blockers bench; a chain with a canceller a Canceller bench; an array whose
branch has no mixer Steering (when the branch has a phase shifter) and Combiners; and, when asked, the Demo:

| Bench | Source | Analyses | Measures |
|---|---|---|---|
| `<chain> 1-Tone` | a P1Tone per element (port 1 … N), a Port at the output | without a mixer, an [S-parameter run](simulations.html#sp-noise) with `Noise=yes`; with one, [harmonic-balance noise](simulations.html#hb-noise) with the LO running. And a harmonic-balance drive sweep at the predicted IP1dB | gain, NF, each stage's cumulative NF from the run's own shares, IP1dB, OP1dB |
| `<chain> 2-Tone` | a PnTone per element, 1 MHz apart, 50 dB below the predicted IIP3 | [multi-tone harmonic balance](simulations.html#two-tone) | gain, IIP3, OIP3, and each stage's gain and OIP3 at its output |
| `<chain> Envelope` | a MOD_Source per element: an NR carrier, its source noisy at the source temperature | [envelope](simulations.html#envelope-noise) with `Noise=yes` and `EvmSplit=yes` | in-channel SNR, the EVM's noise part, output power and output noise |
| `<chain> Steering` | a plane wave: one Port into a Combiner used as a splitter, path k delayed by the wave's extra path; the elements and the combiner, a Port at its output | an S-parameter run with `Noise=yes` in a sweep of `theta_in` (−90° to 90°) within a sweep of `theta_steer` (−30°, 0°, 30°) | the pattern against the arriving angle beside the closed-form array factor; at each beam's peak the gain, the NF and the array gain. See [Steering](#steering) |
| `<chain> Combiners` | the same elements into each of the four [variants](#variants), each fed by its own wave at broadside | one S-parameter run with `Noise=yes` | each variant's gain and NF, beside its budget, intercepts and array gain |
| `<chain> Levels` | a PnTone per element, two tones 1 MHz apart, each at the input power | multi-tone harmonic balance, on the 2-Tone bench's mixing order | the signal and its IM3 at every node. See [Levels](#levels) |
| `<chain> Blockers` | a PnTone per element with every blocker, an isolator after every stage | harmonic balance | every blocker at every node |
| `<chain> Canceller` | the canceller on its own: a wanted signal and an interference from MOD_Sources | envelope runs, swept | the depth against each error, the wanted signal's EVM, SNR and constellation with and without cancellation. See [The canceller](#canceller) |
| `<chain> Demo` | a MOD_Source of NR 64-QAM and one of DBPSK per element; the LO with a phase-noise mask | envelope with `Noise=yes` and `EvmSplit=yes`, the input power swept | the constellation, the EVM against the input power, its noise and distortion parts. See [The demo](#demo) |

The chain's parts are placed as written. Three things are the bench's and not the chain's:

- the **sources, the load and each mixer's LO**, a P1Tone at `Flo` and `Plo`;
- an **isolator in front of each mixer** (a Circulator whose third port is terminated at the ambient). It terminates the
  mixer's image at T0, the termination its SSB noise figure is stated against. A filter in front of a mixer reflects the
  image instead, and the mixer's noise then differs: in the superhet example the harmonic-balance noise figure reads
  0.28 dB higher without the isolator. Delete it to see what your own image termination does;
- in an array, the **arriving wave's path**: a TLIN of (k − 1)·`Steer` in front of element k (on the Steering and
  Combiners benches, the splitter's own path phases instead).

Every bench sets `temp = 16.85`, which puts every part at 290 K, the temperature a noise figure is stated at. Each row
of its MEAS block reads one analysis, so `circuitrf hb … -a SW1` evaluates the compression rows on its own. The envelope
SNR is measured over the NR carrier's occupied bandwidth (the 15 kHz-spacing channel nearest the chain's bandwidth), and
its prediction beside it is computed in that bandwidth.

An array's 1-Tone bench has N + 1 ports. Its gain is |Σₖ S(N+1, k)|², which is the budget's only if every phase shifter
removes its element's steering, and its NF is one element's: F = 1 + (F₁ − 1)·|S(N+1, 1)|²/Σₖ|S(N+1, k)|², where F₁ is
the noise factor from port 1 with the other elements' terminations noiseless — F = 1 + (F₁ − 1)/N for equal paths.

## Steering the beam in simulation {#steering}

The Steering bench is the array at the combiner, fed by a plane wave and steered from its own VAR block:

| Variable | Meaning |
|---|---|
| `theta_in` | The arriving wave's angle from broadside, degrees. The bench sweeps it from −90° to 90° in 1° steps. |
| `theta_steer` | The angle the phase shifters steer to, degrees. The bench sweeps −30°, 0° and 30°. |
| `d_over_lambda` | The element spacing in wavelengths, 0.5. |
| `taper` | An amplitude taper, dB: the two edge elements' paths lose `taper` more, the others a share in proportion to their distance from the centre. 0. |
| `psi_in`, `dphi` | 360°·d/λ·sin θ for the wave and for the steer: the phase step between adjacent elements. |

The wave is one Port into a Combiner used as a splitter, whose path k has the phase −(k − 1)·`psi_in`: element k
receives the wave (k − 1) element spacings later. Each element's phase shifter advances it by (k − 1)·`dphi` (its line is
(k − 1)·(360° − `dphi`) long, the same phase at the bench's frequency and never a negative length), and the taper sits on
the combiner's paths. Because the splitter shares the wave's power equally and its own isolation network is noisy at
T0, each element sees an equal share of the signal and its own, uncorrelated, thermal noise, as an element of a real
array does from its own antenna.

Its display plots the **pattern**, the simulated gain over the budget's peak gain at the combiner, against `theta_in`
for each steer angle, and beside it the closed-form array factor:

```
AF(θ_in) = | Σₖ a_k · e^(−j·(k − 1)·(ψ_in − ψ_steer)) | / Σₖ a_k,      ψ = 2π·(d/λ)·sin θ
```

with a_k each path's amplitude weight (its `PathIL` and the `taper`). Each peak sits at its steer angle, and there the
table reads the gain, the NF from the wave's port and the **array gain**, 10·log10 N less what the NF says it costs over
one element's own: 6.02 dB for four equal elements, at every steer angle, with the NF unmoved. With a taper the main
lobe widens and the sidelobes fall, and both the pattern and the closed form lose the taper's coherent loss at the
peak. Change `d_over_lambda` to 1 and the grating lobes appear at ±90°.

Edit a sweep's values, or delete the sweeps and set `theta_in` and `theta_steer` by hand: everything is an ordinary
variable.

## Levels at every node {#levels}

The **Levels** tab and the Levels display follow the chain node by node: node 0 is the input and node k the output of
stage k. At every node:

| Quantity | Budget |
|---|---|
| Signal | Pin + G, per tone of two at the input power |
| Noise floor | 10·log10(k·(Ts + Te)·B·Gn) + 30 dBm, the noise in the bandwidth |
| NF | the cumulative noise figure |
| IM3 | 3·P − 2·OIP3, each third-order product of the two tones, absolute |
| Each blocker | its input level plus every stage's gain at its own frequency: a filter's own response there (the model the Filter block simulates with, its IL included), a mixer moving it to \|f − Flo\| |
| A canceller's interference | from the canceller's input, where it is stated, and its residual after it |
| Margin to compression | OP1dB − the larger of the signal and the blockers; before anything compresses, the whole chain's IP1dB less the input's |
| SNR | the signal over the noise floor |

The simulated points come from three benches: the **Levels** bench (two tones each at the input power: the signal and
the IM3), the **Blockers** bench (the blockers alone, an isolator after every stage so that each node's voltage is the
wave going forward, not a reflection standing on a filter's stopband) and the **1-Tone** bench (each stage's
cumulative NF, from the noise run's own shares, and the noise floor that NF gives at the node through the budget's
noise gain). In the window they appear once those benches have been simulated beside the `.cchain`; in the display
they are its points, the budget its lines. The displays plot against a variable `nodeix` that each bench sweeps
around a trivial DC solve, which is how one number per node becomes one curve.

Add a marker to a trace in the window's Levels plots and its node's stage is selected, in the stage list and in both
views; move the marker and the selection follows.

## The canceller {#canceller}

A **Canceller** stage subtracts a weighted copy of an interference from the main path: a reference path from the
interference's own source, through an amplitude setting, a phase setting and a delay, into a directional coupler's
isolated port, the main path being the coupler's through arm.

| Parameter | Meaning |
|---|---|
| `Coupling` | The coupler's coupling, dB. The main path loses 10·log10(1 − c) of it. |
| `IL` | The coupler's excess loss, dB. |
| `Pint` | The interference at the canceller's main input, dBm. |
| `Bint` | The interference's bandwidth. |
| `Pref` | The reference input: the interference at its source, dBm. The path's gain follows from it. |
| `NFref` | The reference path's noise figure, dB. Blank: passive, at T0. |
| `AmpErr`, `PhaseErr`, `DelayErr` | The cancellation path's amplitude error (dB), phase error (degrees, an advance) and delay mismatch (the reference path later, seconds: `0.1 ns`). |

**On the wanted signal** it is the through arm, a loss L_t = 10^(`IL`/10)/(1 − c), and the noise the reference path
injects. That path needs a gain G_ref with G_ref·P_ref·c = P_int·(1 − c); its output is at T0·F_ref·G_ref, coupled by
c/(1 − c) against the through arm, so

```
F = L_t + (F_ref·G_ref − 1)·c/(1 − c)
```

A passive path (F_ref·G_ref = 1) adds nothing to the coupler's loss.

**On the interference** the residual is the fraction R the subtraction leaves. With an amplitude ratio
a = 10^(`AmpErr`/20), a phase error φ and a delay mismatch τ, a carrier of bandwidth B and flat spectrum, centred ν from
where the phase was set, leaves the band's average of |1 − a·e^(j(φ − 2πfτ))|²:

```
R = 1 + a² − 2a·cos(φ − 2πντ)·sinc(πBτ),      sinc x = sin x / x,      depth = −10·log10 R
```

33.5 dB at 0.1 dB and 1°; with 1 ns of delay mismatch alone, 40.8 dB over 5 MHz, 28.8 dB over 20 MHz and 22.8 dB over
40 MHz. The window's **Canceller** tab draws the depth against the phase error for five amplitude errors, and against the
bandwidth for four delay mismatches. The levels carry the interference and its residual node by node.

**The Canceller bench** studies it with real signals. Two MOD_Sources, which the [Signal
Designer](signal-designer.html) edits: the wanted signal, an NR 64-QAM carrier and a QPSK one, and the interference,
three NR carriers of 10, 5 and 20 MHz, the first two under the wanted ones. The interference is split: one share leaks
into the main path through a line of 1 ns, the other is the reference path — an Amp for the amplitude, one path of a
2-way Combiner for a flat phase, a line for the delay — into the coupler's isolated port. Its VAR block sets the errors,
`amp_err`, `phase_err` and `delay_err`, and two switches, `canc_on` (the reference path) and `sig_on` (the wanted
signal). Its envelope runs:

- the interference alone, uncancelled: the reference each depth is measured from;
- the depth against each error, swept, the others at their nominal values, at every interference carrier: beside it the
  closed form above at that carrier's bandwidth and offset, so the 20 MHz carrier shows how a delay mismatch costs a
  wider signal more;
- the wanted carriers with and without cancellation, their noise on: EVM, SNR, and each constellation both ways.

Open either source in the Signal Designer to change its modulation (LTE, NR, DBPSK, FSK, CSS and the rest it offers) or
its bandwidth; the bench measures whatever it sends.

## The modulated-signal demo {#demo}

**with a modulated-signal demo** (or `--demo`, or `"Demo": true` in the `.cchain`) adds `<chain> Demo`: the chain
demodulated end to end. Each element is fed an NR 64-QAM carrier and, from a second source beside it, a DBPSK carrier
at twice the bandwidth's offset, joined by a 2-way Combiner, each at the input power and noisy at the source
temperature. Each mixer's LO carries a phase-noise mask, which an envelope run plays as a random phase. An envelope run
with `Noise=yes` and `EvmSplit=yes` is swept over the input power in seven steps, from 5 dB of SNR to 5 dB past the
input P1dB. Its display:

- the 64-QAM constellation at the output, received points over the ideal ones, at the middle and the highest power;
- the EVM against the input power, 64-QAM and DBPSK, and beside it the budget's from its SNR, EVM ≈ 1/√SNR, which holds
  where noise dominates;
- the EVM's split into its noise and distortion parts: noise at low power, the LO's phase noise as a floor, distortion
  past compression;
- the SNR in the carrier's channel, simulated and the budget's.

## How closely the simulation agrees {#agreement}

Measured on the example's superhet and array, the budget against the simulation of the benches it writes:

| Quantity | Superhet | 4-element array | Why it differs at all |
|---|---|---|---|
| Gain | −1.1×10⁻⁴ dB | −1.1×10⁻⁴ dB | The small-signal drive, 40 dB below IP1dB. An S-parameter bench reads 3×10⁻¹⁰ dB |
| NF, and each stage's | +6.6×10⁻⁵ dB at most | +1.0×10⁻⁴ dB at most | The harmonic-balance noise run's 1 kHz offset from the IF, and its drive |
| IIP3 | +1.8×10⁻⁴ dB | +1.9×10⁻⁴ dB | tanh's fifth-order term, 50 dB below the intercept |
| OIP3, and each stage's gain and OIP3 | 2.6×10⁻⁴ dB at most | 2.6×10⁻⁴ dB at most | as above |
| IP1dB | +0.048 dB | +0.062 dB | The cascaded-P1dB formula is an approximation: the chain compresses a little later |
| SNR | −0.13 dB | −0.27 dB | One noise record's statistics, about ±0.12 dB from seed to seed |

The same array at RF, with no mixer, runs its noise through the S-parameter analysis instead and reads 3×10⁻¹⁰ dB in
gain and 10⁻¹¹ dB in NF. The envelope
SNR is a measurement of one noise record: four seeds of the array read 29.17 to 29.42 dB against 29.37 dB predicted.

The superhet's Levels bench puts the signal on the budget to 2×10⁻⁵ dB at every node and the IM3 to 8×10⁻⁵ dB; its
Blockers bench reads each blocker 0.001 to 0.003 dB below the budget, the blocker's own slight compression at −40 and
−50 dBm into the LNA. The front end's 1-Tone bench reads the canceller's noise, its through arm and the reference path's
coupled noise, on the formula to 10⁻⁶ dB.

The Canceller bench's depth, at each of the three interference carriers and every swept error, is the closed form's to
within 1.4 % of the depth, 0.7 dB at 49.7 dB deep and under 0.3 dB everywhere shallower than 45 dB. With cancellation the
64-QAM carrier's EVM falls from 840 % to 3.2 % and the QPSK one's from 1036 % to 8.6 %.

The superhet's Demo bench reads the SNR 0.20 dB under the budget at its lowest power, where noise dominates, and the
EVM's noise part 57.0 % against the budget's 1/√SNR of 56.2 %. Higher up, two floors the budget does not model take
over: the LO's phase noise, 1.23 % of EVM in the distortion part, which is the mask integrated over the channel
(−38.2 dBc), and a noise part that stops at about 1 % (40 dB of SNR), which the Canceller bench's NR carriers show as
well. At 5 dB past the input P1dB the distortion part is 15.5 %.

The array's Steering bench puts its pattern on the closed-form array factor to 3×10⁻⁹ dB within 3 dB of each peak and
to 10⁻⁶ dB down to −60 dB, and its gain, NF and array gain at each peak on the budget to 10⁻¹⁴ dB. The Combiners bench
reads each variant's gain and NF to 10⁻¹⁴ dB; each variant's intercepts and compression point, transferred on its own,
read as the table above does.

## The .cchain file {#format}

A `.cchain` is JSON. The system inputs are numbers whose names carry their unit; a stage's parameters are text, exactly
as the part's netlist line carries them.

```json
{
  "FormatVersion": 1,
  "FrequencyHz": 2450000000,
  "BandwidthHz": 4500000,
  "InputPowerDbm": -70,
  "TemperatureK": 290,
  "RequiredSnrDb": 10,
  "Stages": [
    { "Name": "LNA", "Part": "Amp", "Parameters": { "Gain": "16", "NF": "1", "IP3": "15", "IP3Ref": "Output" } },
    { "Name": "RFBPF", "Part": "Filter", "Parameters": { "Response": "Butterworth", "F1": "2.35 GHz", "F2": "2.55 GHz", "IL": "1.5" } },
    { "Name": "MIX", "Part": "Mixer", "Parameters": { "ConvGain": "-7", "NF": "7", "IIP3": "10", "Plo": "7", "Flo": "2.1 GHz" } }
  ]
}
```

| Key | Meaning |
|---|---|
| `FormatVersion` | 1. A file from a newer circuitRF is refused, not read in part. |
| `FrequencyHz`, `BandwidthHz`, `InputPowerDbm`, `TemperatureK`, `RequiredSnrDb` | The [system inputs](#inputs). Absent: 2.45 GHz, 1 MHz, −70 dBm, 290 K, 10 dB. |
| `Stages` | In signal order. `Name` follows the instance-name rule (a letter, then letters, digits and `_`). |
| `Part` | `Amp`, `Mixer`, `Atten`, `Filter`, `Switch`, `Circulator`, `Coupler`, `PhaseShifter`, `Limiter`, `Splitter` or `Combiner`. |
| `Parameters` | The part's parameters by their own names. An absent one takes the part's default (a System block's own placement default); one the part does not list is carried onto the schematic unchanged and not read by the budget. |
| `Blockers` | Optional: `[{"OffsetHz": 150000000, "LevelDbm": -40}, …]`, each an offset from `FrequencyHz` and a level at the input. |
| `Demo` | Optional: `true` to write the [demo bench](#demo) too. |

A chain has at most one Combiner.

## From the command line {#cli}

```text
circuitrf chain Superhet.cchain                      the cascade, as the window shows it
circuitrf chain Superhet.cchain -o budget.npy        the budget as a DataSet: per-stage cubes over a stage axis
circuitrf chain Superhet.cchain --to-schematic       the benches and their displays, beside the .cchain
circuitrf hb "Superhet 2-Tone/schematic/Superhet 2-Tone.csch"
```

`--to-schematic [dir]` writes into another folder, `--name` names the benches, `--demo` adds the demo bench, and an
existing bench is a refusal unless `--replace` is given. `circuitrf check` reads a `.cchain` through the same budget and reports its refusals. Over the MCP
server the verb is `run analysis=chain`.

<p class="small">See also: <a href="system-components.html">System Components</a> ·
<a href="simulations.html#noise">Simulations ▸ Noise</a> ·
<a href="measurements.html">Measurements</a> ·
<a href="data-display.html">Data Display</a>.</p>
