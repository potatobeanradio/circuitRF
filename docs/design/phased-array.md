# circuitRF — Phased antenna arrays

**Status:** design, nothing built · **Series:** [`brief-paa-0-overview.md`](../briefs/brief-paa-0-overview.md) ·
**Builds on:** [`sar.md`](sar.md) §11 and the co-simulation series (`brief-cosim-pattern-*`: the per-port far field and
its recombination), [`chain-budget.md`](chain-budget.md) (the array at system level), [`sparameter-noise.md`](sparameter-noise.md)
(correlated noise through a terminated N-port), [`harmonic-balance.md`](harmonic-balance.md).

The goal is the far field of a whole array, of up to 4096 elements (64×64), computed from **one EM simulation of one
element or of a small sub-array**, driven by a circuit or by system blocks, steerable and sweepable, quickly enough to
tune by hand. A schematic component, the **Phased Antenna Array**, holds it; a tool window shows it when the array is too
large to look at in a schematic. Small arrays can also be simulated by brute force: every element a port of one EM result,
every port driven by its own power amplifier in harmonic balance, so each amplifier's loading by its neighbours is seen.

---

## 1. What circuitRF already has

| Piece | Where | What it gives an array |
|---|---|---|
| A far field per port, stored with the result | `PortFarFieldSet` (`src/Design/Em3d`), the `farfield` group of a result's `.npy` | The **embedded element pattern** of every port of an EM run |
| Recombination of those patterns with the waves a circuit puts on the ports | `FieldCosimulation.PublishFarField`, `PortFarFieldSet.Weights` / `AddWeighted` | The exact pattern of an array that was solved whole, coupling included (§3, L2) |
| Several drives with complex weights, tunable | `analysis … type=fields drive=1,2 weights=1,W2` | Beam steering of a small array today, as an MRI transmit array is steered |
| Metrics of a pattern | `Em3dRadiation.Publish`, `CosimPatternMetrics` (`RealizedGainDbi`, `DirectivityDbi`, `PeakEirpDbm` …) | Gain per watt accepted, realized gain with mismatch, polarization |
| The array at system level | RF Chain Budget: an N-element branch, the N-way `Combiner` with `Phase[k]`, the Steering bench | Array gain, per-element noise, a closed-form array factor of isotropic elements on a line |
| Indexed parameters | `Phase[k]`, `IL[k]` on the `Combiner` | The spelling for per-element overrides |
| Correlated noise through an N-port | `SParameterNoise` (adjoint solves, `Cs`) | The noise of channels coupled through an array, once the array is a block in the circuit |
| N-port blocks in harmonic balance | `EmFieldData` is an `SnpModel`; HB's linear partition takes it | Brute-force HB of PAs on an array's ports already solves; only the pattern from HB's waves is missing |

What is missing is: a pattern for an element that is **not** solved inside the full array (the array factor, §2), the
coupling of a large array from a small one (§3, L1), lattices in two dimensions at up to 4096 elements, an angular resolution fine enough for the narrow beams those make (§6.2), a component that
says all of this in a schematic, the pattern from harmonic balance's waves, the receive side, and a view that keeps up
with a hand on a slider.

## 2. The physics

### 2.1 Superposition is exact

A linear antenna structure with N ports has, for each port k, an **embedded element pattern** **E**ₖ(θ,φ): the far field
with a unit incident wave at port k and every other port terminated in its reference impedance. For any incident waves
**a** the far field is exactly

```
E(θ,φ) = Σₖ aₖ · Eₖ(θ,φ)                                                            (1)
```

and the waves leaving the ports are **b** = S·**a**. Equation (1) is what `FieldCosimulation` already evaluates; the
only differences there are the normalisation (`√Z0·(1+S_kk)`, since a stored pattern is per volt) and the planar case,
which stores each pattern with the other gaps **shorted** rather than terminated and sums over gap voltages instead
(`PortFarFieldSet.Weights`, `Planar`). A terminated embedded pattern is formed from a shorted set by solving the
circuit "port k driven, others in Z0" through that same `Weights` path, so every solver's result yields (1).

### 2.2 The array factor

In a large regular array an interior element's embedded pattern is, to good approximation, the same as every other
interior element's, displaced. With **E**₀ the embedded pattern of one element referred to that element's own position,
element n at position **r**ₙ = (xₙ, yₙ, 0), wavenumber k = 2πf/c and direction û = (sinθcosφ, sinθsinφ, cosθ):

```
E(θ,φ) ≈ E₀(θ,φ) · AF(θ,φ),       AF = Σₙ aₙ · exp(j·k·û·rₙ)                        (2)
```

"Referred to its own position" matters: a solver writes every port's pattern with the phase of the run's coordinate
origin, so the pattern taken from an element at **r**_c is multiplied by `exp(−j·k·û·r_c)` before (2) uses it. The
polarization is kept: the array factor is scalar and multiplies Eθ and Eφ alike.

To steer to (θ₀,φ₀) at frequency f₀ with phase shifters, `aₙ = |aₙ|·exp(−j·k₀·û₀·rₙ)`. With true time delays the delay
is `τₙ = −û₀·rₙ / c` and the phase scales with frequency, so a phase-shifter array squints across a band and a delay
array does not; the array model gets both for free by evaluating (2) at each analysed frequency.

### 2.3 Coupling, active reflection and why it matters to the circuit

With the array's S-matrix the wave each port returns is `bₙ = Σₘ Sₙₘ aₘ`, so the reflection a driving circuit sees at
port n, the **active reflection coefficient**, is

```
Γ_act,n = bₙ / aₙ = Σₘ Sₙₘ · aₘ / aₙ                                                 (3)
```

It depends on the steering, and it is what each power amplifier is loaded with: a PA matched at boresight is mismatched
when the beam is scanned, its output power and efficiency move, and at a scan blindness angle Γ_act approaches 1 and
almost nothing is radiated. circuitRF holds loadpull data, so the consequence for each amplifier — its output power and
PAE on the loadpull contours at Γ_act — is something it can show directly (§7).

### 2.4 Power, gain and a self-check

Accepted power is `P_acc = ½·Σₙ (|aₙ|² − |bₙ|²)` in the peak-wave convention the co-simulation uses, and the radiated
power is the integral of |E|² over the sphere (the hemisphere above an infinite ground). Gain is per watt accepted and
realized gain per watt available, as for a single EM run (`Em3dRadiation.Publish`). On a lossless structure the two
powers are equal; on the approximate levels of §3 they are not quite, and the ratio is reported as the **power balance**,
a readout of how far the approximation has drifted rather than a number to hide.

## 3. Three levels of fidelity

| Level | Patterns | Coupling | Valid for | EM needed |
|---|---|---|---|---|
| **L0** — isolated | One element's pattern, solved alone, in (2) | None: each port reflects its own S₁₁ | Sparse arrays, a first look | One element |
| **L1** — embedded | The embedded pattern of the centre element of a small solved sub-array (or one per position class, §3.1), in (2) | The sub-array's S by element offset, extended to the whole array (§3.2) | Interior of a large regular array; scan loss and active reflection | A sub-array, e.g. 5×5 |
| **L2** — exact | Every port's own pattern, (1) | The run's own S | Arrays solved whole: tens of ports | The whole array |

L2 exists today. L0 and L1 are this series; brief PAA-1 measures how far each is from L2 before anything is built on
them, and fixes the tolerances the later gates use.

### 3.1 Position classes

An element at the corner of an array sees fewer neighbours than one in the middle, and its embedded pattern is
measurably different. A 5×5 sub-array holds nine classes — four corners, four edges, the interior — and an element of a
large array can take the pattern of its class rather than the centre's. Whether this is worth its cost (nine patterns
read and re-referenced instead of one) is decided by PAA-1's numbers, not assumed.

### 3.2 Coupling from a sub-array

In a regular array the coupling between two elements depends, to good approximation, only on their offset
(Δrow, Δcol). A sub-array of R×C elements measures every offset up to ±(R−1, C−1). For each offset the pair nearest the
sub-array's centre is used (it has the most interior-like surroundings); offsets beyond what was measured are zero, and
the run says what range was measured. The result is an N×N block-Toeplitz matrix that is **sparse by construction**: a
5×5 sub-array gives at most 9×9 = 81 non-zeros per row, whatever N is, so at 4096 elements it holds about 330,000
entries instead of the 16.8 million (256 MB) a dense matrix would. It is stored and factorised sparse
(`CSparse.NET`, already a dependency), and nothing in the array path forms a dense N×N matrix. The truncation is the main
error of L1 and shows up in the power balance (§2.4).

### 3.3 What a spacing sweep means at each level

A sweep of the element spacing re-evaluates the array factor exactly. Under L0 that is the whole answer. Under L1 and L2
the patterns and the coupling were solved **at one spacing**; at another spacing they are wrong, by an amount that grows
with the distance from it. The array model compares the spacing it is asked for with the spacing of the record's lattice
and warns when they differ by more than a fraction of a wavelength; it does not refuse, because exploring the array factor
alone is a legitimate first step. Re-solving the sub-array at each spacing is what EM cells (`model=em`) already do for a
layout, and is the route for a later series (overview, "Not here").

## 4. The Phased Antenna Array component

One component, `.cnl` type `PhasedArray`, palette name **Phased Antenna Array**, System category.

### 4.1 Two pin modes

| Mode | Pins | The beamformer | Use |
|---|---|---|---|
| **Feed** | One, `F` | Inside: ideal per-element weights (amplitude, phase, delay), quantised; optionally a **channel cell** placed once per element between the weight and the element | Any size up to the limit of §6; system design with mixers, filters, amplifiers outside the component and the per-element chain in the channel cell |
| **Elements** | One per element, numbered row-major | Outside: whatever the user wires to each pin | Small arrays (at most 64 elements): brute force, PAs per element in HB (§7), a hand-built feed network |

In Elements mode the component's weight fields are not used and are greyed. A weight between a PA and its element would
rotate the load the PA sees, which no real phase shifter does, so steering in Elements mode is the circuit's: P1Tone
phases, the `PhaseShifter` block (§4.5), or a feed network.

### 4.2 Parameters

Every one is an ordinary expression, so a VAR referenced from any of them is swept by a parametric sweep, moved by a
tuning slider, varied by yield and set by the optimizer.

| Parameter | Meaning |
|---|---|
| `Record` | The EM result, as EM Field Data's: a saved `.cemr`, read whole and cached (`PortFarFieldSet`) |
| `Level` | `isolated` (L0), `embedded` (L1), `exact` (L2); defaulted from the record: one port → `isolated`, a sub-array → `embedded`, a record whose port count and lattice equal the array's → `exact` |
| `Patterns` | `centre` or `classes` (§3.1); `embedded` only |
| `Mode` | `feed` or `elements` |
| `Rows`, `Cols` | Integers |
| `Lattice` | `rect` or `tri` (every other row offset by dx/2) |
| `dx`, `dy` | Element pitch, **lengths in any unit circuitRF knows** (`um`, `mm`, `cm`, `m`, `mil`, `in`), or an expression such as `0.5*lambda(f0)`; a bare number takes the unit shown beside the field, as every length parameter does |
| `Amp` | Per-element amplitude, linear, in element scope (§4.3); default `1` |
| `Phase` | Per-element phase, degrees, in element scope; default `0` |
| `Delay` | Per-element true time delay, seconds, in element scope; default `0` |
| `Amp[n]`, `Phase[n]`, `Delay[n]` | Per-element overrides, as the Combiner's `Phase[k]` |
| `PhaseBits`, `AttenStep` | Phase-shifter resolution (0 = continuous) and attenuator step in dB (0 = continuous) |
| `Off` | Elements switched off, as a list (`3,17,200`): weight zero and the element terminated in Z0 |
| `Channel` | Feed mode: a cell placed once per element; empty = a direct connection |
| `Coupling` | `on` or `off`; `off` keeps each element's own reflection and drops every Sₙₘ, m ≠ n |
| `Z0` | Reference impedance at the elements; defaults to the record's |
| `RecordDx`, `RecordDy`, `RecordRows`, `RecordCols` | The lattice of a sub-array record that does not store its ports' positions (§10); ignored when it does |
| `Tsky`, `Tground` | The brightness model of §5.2; default `T0` |
| `RxTheta[k]`, `RxPhi[k]`, `RxPd[k]`, `RxFreq[k]`, `RxPol[k]` | Incident waves (§5.1) |

### 4.3 Element scope

`Amp`, `Phase` and `Delay` are evaluated once per element with five names bound in an **element scope** layered over the
instance's scope:

| Name | Value |
|---|---|
| `n` | Element number, 1…N, row-major |
| `row`, `col` | 1-based |
| `x`, `y` | Position of the element from the array's centre, metres (a length, unit-bearing) |

The convention is fixed: looking at the aperture from the front (+z, boresight), rows run down the y axis from the top,
columns run along x from the left, `n = (row−1)·Cols + col`, and the symbol and the tool both draw it that way.

Standard functions for weights, usable anywhere and defaulting their position arguments to `x`, `y` in element scope:

| Function | Value |
|---|---|
| `steer(theta0, phi0, f0[, x, y])` | Phase in degrees that points a phase-shifter beam to (θ₀,φ₀) at f₀ |
| `steerDelay(theta0, phi0[, x, y])` | Delay in seconds that points a true-time-delay beam |
| `taylor(sll, nbar)`, `chebyshev(sll)`, `cosq(q)` | Separable amplitude tapers over rows and columns, normalised to 1 at the peak |
| `lambda(f)` | c/f, a length |

So a steered, tapered 32×32 array is

```
theta0 = 30 deg
phi0   = 0 deg
f0     = 5.8 GHz
PhasedArray:PA1 feed Record=results/patch5x5.cemr Rows=32 Cols=32 dx=0.5*lambda(f0) dy=0.5*lambda(f0) Phase=steer(theta0,phi0,f0) Amp=taylor(-30,4) PhaseBits=6
```

(the record's spelling follows EmFieldData's), and a sweep of `theta0` is an ordinary `parametric_sweep`. A reference to `n`, `row`, `col`, `x` or `y` outside a
PhasedArray weight field is refused by name. Cycle detection is unchanged: element scope adds names, never edges back to
the instance's scope.

### 4.4 What it stamps

- **Feed mode, no channel.** An (N+1)-port: the feed, the quantised weights, the array network. The array network is
  the record's S (L2), the extended S (L1, `Coupling=on`), or its diagonal (L0, or `Coupling=off`). Its only port is F.
- **Feed mode with a channel.** Elaboration places the channel cell N times, as an ordinary hierarchical instance named
  `PA1.ch[n]`, between the weight's output and element n's port. Channel parameters may reference `n`, `row`, `col`, `x`,
  `y`; a channel that does not is **identical across elements**, which is what §6's fast path needs.
- **Elements mode.** The N-port array network, one pin per port.

In every mode the component is also the block a `type=fields` analysis names, so `analysis F1 type=fields block=PA1`
gives the array's pattern, and its far field is formed from (1) at L2 and (2) at L0/L1.

### 4.5 A PhaseShifter System block

An ideal matched two-port with `Phase` (degrees), `IL`, `Bits`, and optionally `Delay`, for a channel cell or an
Elements-mode bench. The Chain Budget makes a flat phase from one path of a 2-way Combiner today; this is the block that
stands for itself.

### 4.6 The dynamic symbol

The symbol is drawn from the instance's resolved parameters and cached per variant, as the Combiner's is per N:

- **Feed mode:** a body holding a thumbnail of the aperture — one small square per element up to 8×8, the triangular
  lattice offset as drawn, larger arrays drawn as 8×8 with an ellipsis and `32 × 32` written under it — one pin `F` on
  the left, and a beam lobe leaving the aperture tilted by the resolved θ₀ with `θ0 30°  φ0 0°` beside it when the
  weights are a `steer()`.
- **Elements mode:** the thumbnail on the right with corner elements numbered, and one pin per element down the left,
  numbered `1…N` at the Combiner's 100-unit pitch; a hover on a pin names its row, column and position.
- Elements switched off are drawn hollow; the thumbnail redraws when `Rows`, `Cols`, `Lattice` or `Off` change.

Values are evaluated at the nominal point. In Elements mode a sweep that changes `Rows` or `Cols` would change the pin
count during a run and is refused by `check` and by the run; in Feed mode it is allowed.

## 5. Receive and noise

### 5.1 Incident waves by reciprocity

A plane wave arriving from (θ,φ) with power density S (W/m²) and polarization **p̂** puts on element n an incident wave
proportional to its embedded transmit pattern in that direction, by reciprocity:

```
aₙ^rx ∝ λ · √(S / η₀) · p̂ · Eₙ(θ,φ)          (constant fixed by the pattern's normalisation)   (4)
```

so a received signal and every interferer become sources on the element ports with no new EM run. The component takes
them as indexed parameters, `Rx[k]` with its own `RxTheta[k]`, `RxPhi[k]`, `RxPd[k]` (dBm/m²), `RxFreq[k]` and
`RxPol[k]`, and in harmonic balance and envelope they are tones like a P1Tone's: an interferer arriving in a sidelobe is
attenuated by the pattern before it reaches the channel's amplifiers, which is what an array's spatial filtering and its
dynamic range rest on. The gate is internal: at boresight the received power equals S·λ²G/4π from the same pattern.

### 5.2 The array's own noise

An antenna's ports are not at the temperature of its metal: their noise is what the pattern sees. For a lossless array
looking at a brightness temperature T_b(θ,φ), the noise-wave correlation of its ports is

```
C_nm = k·∫ T_b(Ω) · eₙ(Ω)·eₘ*(Ω) dΩ  (patterns normalised so that a uniform T gives kT·(I − S·Sᴴ))   (5)
```

plus `kT_phys·(…)` for the ohmic part (1 − radiation efficiency). The component stamps (5) as its noise correlation, from
a brightness model on the component — `Tsky` (a constant or an expression of elevation) and `Tground`, with the horizon
at θ = 90° — so the existing S-parameter noise analysis, unchanged, gives what follows.

**Computing (5) by offset.** Evaluated pair by pair it is N²·D operations — about 10¹² at 4096 elements on a 1° sphere —
and it is not needed. Under L1 every element has the same embedded pattern displaced, so C_nm depends only on the offset
between n and m: the integral is taken once per offset that occurs (at most (2R−1)·(2C−1), 16,129 for 64×64, each over
the D directions), and the matrix is filled from the table. With a uniform brightness it reduces to `kT·(I − S·Sᴴ)`,
which has the sparsity of S·Sᴴ (offsets to ±8 for a 5×5 sub-array), and the brightness integral is needed only for
offsets where the non-uniform part is significant; offsets beyond the extended coupling's reach carry only that part.
Under L2 (tens of ports) the pair-by-pair integral is cheap and is used as it stands. The correlation is handed to the
noise analysis in the same sparse form as S.

With the correlation in place, the noise analysis gives:

- the noise figure and noise temperature at the feed, channel by channel included,
- the **active noise figure**: each LNA's source is Γ_act, and its own noise wave leaving its input couples into its
  neighbours through S, both captured because the array is a block in the circuit,
- **G/T**: `G − 10·log10(T_sys)`, with T_sys at the array's terminals from the same correlation.

With `Tsky = Tground = T0` (the default) (5) is exactly the Bosma noise a passive block stamps today, so nothing that
already runs changes.

### 5.3 What a receiver designer reads

G/T against scan angle; antenna temperature; noise figure at the feed and per channel; array gain against per-channel
NF; SNR for an `Rx` signal; IIP3, compression and SFDR with an interferer at a chosen angle; null depth on an interferer;
the cost of phase and attenuator bits; and EVM against scan through the existing envelope analysis.

## 6. Speed

### 6.1 The pattern sum

Measured with numpy on the development machine, 32×32 elements, a full sphere at 1° (65,160 directions, the grid
`PortFarFieldSet` stores), arbitrary per-element weights:

| Method | Time |
|---|---|
| Every direction × every element, with the exponential evaluated each time | 728 ms |
| Row and column phase tables precomputed, the sum as two small products (exact on a rectangular lattice; 6.6e-13 from the first) | 7.8 ms |
| One 361-point cut | 3.2 ms |
| A 256×256 FFT of the weights in (u,v) | 0.5 ms |

The same row/column method on larger arrays, on the upper hemisphere:

| Array | 1° (32,760 directions) | 0.25° (519,840 directions) |
|---|---|---|
| 32×32 = 1024 | 4.1 ms, 34 MB of tables | 66 ms, 532 MB |
| 45×45 = 2025 | 6.2 ms, 47 MB | 97 ms, 749 MB |
| 64×64 = 4096 | 9.4 ms, 67 MB | 143 ms, 1.07 GB |

The second line works because `exp(j·k·(x·u + y·v))` is the product of an x factor and a y factor, and the tables do
not depend on the weights; a triangular lattice is two interleaved rectangular ones. A separable weighting (a steer and a
separable taper) reduces it further to O(D·(R+C)). These are indicative numbers, not targets; PAA-8 measures the C# path
and records it.

### 6.2 Angular resolution

The cost is not the problem at 4096 elements; resolution is. The half-power beamwidth of an M-element line at
half-wavelength pitch is about 102°/M at boresight, so **1.6° at 64×64 and 3.2° at 32×32**, and it widens as
1/cosθ₀ with scan. The stored 1° grid then puts one or two samples across a 64×64 beam and three across a 32×32 one:
beamwidth, sidelobe level and pointing error read from it are wrong, and even a 1024-element array is marginal. A uniform
grid fine enough (0.25°) costs 143 ms and 1 GB of tables at 4096, which is neither live nor necessary. So the array
factor is **never evaluated only on the record's grid**:

- **The overview** — the whole-sphere pattern the 3D view and the `farfield` cubes show — is formed on the record's grid,
  as today, and is labelled with its grid.
- **Cuts and readouts** are formed on a fine local grid: the principal cuts through the peak at a step no coarser than
  a tenth of the beamwidth, and a small patch around the peak (and each sidelobe the metrics read) at the same step.
  Beamwidth, sidelobe level, pointing error and scan loss are computed only from these.
- **The (u,v) map** is the 2-D FFT of the weights on the lattice, zero-padded to at least 8× the array in each
  direction (512×512 for 64×64, under a millisecond on the numpy measurement above), sampled onto the visible circle.
- **The element pattern** is smooth over a beamwidth of the array (it is one element's), so at any fine point it is
  interpolated from the record's grid — bilinear in (θ,φ) on Eθ and Eφ separately, magnitude and unwrapped phase — while
  the array factor is evaluated exactly there. This is the step that makes the fine grid cheap, and PAA-2 measures its
  error against a re-sampled exact sum.

### 6.3 The circuit

- **Identical channels.** When every channel instance is identical apart from the component's own weights (§4.4), the
  channel is a two-port solved **once** per frequency; the array then closes with
  `a = (I − Γ_ch·S)⁻¹ · t · w`, whose factorisation does not depend on the weights. With S sparse (§3.2) that is a
  sparse factorisation, done once, and a change of steering or taper is a sparse back-substitution — proportional to the
  factor's non-zeros, not to N² — plus the pattern sum. With `exact` (L2) S is dense, but N is tens of ports.
- **Coupling off.** The closure is diagonal and a change of weights costs only the pattern sum.
- **Otherwise** (a channel that differs per element, a nonlinear channel, HB) the circuit is solved as elaborated, with
  its size reported by `explain` and `run dryRun=true`. Harmonic balance of thousands of nonlinear channels is not interactive
  and is not presented as such.

### 6.4 Live

A tuning slider on `theta0` re-evaluates through the fast path, so the pattern follows the hand. The tool window (§8)
drags the beam directly. Where the fast path does not apply, the drag shows the pattern with the circuit held at its last
solve, marked "estimated", and solves when released. The co-simulation's measured drag cost at 1° in a Debug build
(23 ms for a polar cut, 71 ms for a 3D pattern) sets the expectation for the drawing itself.

## 7. Brute-force harmonic balance

At low element counts the most faithful simulation is the most direct one: an L2 record with a port per element, an
Elements-mode PhasedArray (or the EmFieldData block itself), and one PA instance per port, all in one harmonic-balance
run. It already solves; this series adds:

- **The pattern from HB's waves.** `analysis F1 type=fields block=PA1 waves=HB1` reads the block's terminal voltages from
  the named HB analysis at the fundamental (or `harmonic=k`, when the record holds that frequency) and forms the currents
  from the block's own linear stamp, I = Y·V, so no new unknown is introduced. The pattern is then (1) of those waves.
- **Each amplifier's loading.** Per element: Γ_act from the HB waves, delivered power, and the PA's output power and PAE
  from the bench's measurements, as cubes over an element axis, and a Smith-chart trace of Γ_act against a swept steer.
- **The loadpull overlay.** The loci of every element's Γ_act drawn over the PA's loadpull contours (`.spl`), which is
  the picture that says which elements leave the PA's high-efficiency region at which scan angle.

## 8. The Phased Array tool

A tool window, Tools ▸ Phased Array, that is **a view onto one PhasedArray instance in a schematic** — not a second
document. Every edit writes the instance's parameters with undo, so the schematic and the tool cannot disagree, and the
whole thing runs headlessly from the netlist. For a designer who wants no schematic, *File ▸ New ▸ Phased Array* creates
a cell whose schematic is a Port, the PhasedArray in Feed mode, and a `type=fields` analysis, and opens the tool on it.

What it shows:

| Area | Content |
|---|---|
| Aperture | The element grid as built, each element coloured by a chosen quantity: amplitude, phase, Γ_act, delivered power, PA Pout or PAE (after HB), channel NF. Click to switch an element off (writes `Off`), lasso to select, hover for the element's numbers |
| (u,v) map | The pattern in direction-cosine space with the visible-region circle and the grating-lobe positions marked; **drag the beam** to steer when the weights are a `steer()`, writing its VARs |
| Cuts | Two principal cuts, polar or rectangular, co- and cross-polar, with the L0 array factor dimmed beneath for comparison |
| Readouts | Directivity, gain, realized gain, EIRP or G/T, beamwidth in two planes, sidelobe level, pointing error, scan loss, grating-lobe margin, worst |Γ_act|, power balance |
| Scan maps | Realized gain and worst |Γ_act| over (θ₀,φ₀), computed in the background through the fast path; scan blindness appears as a dark ring |
| Frequency | Beam pointing against frequency (squint), phase shifters against delays |
| Tapers and bits | Taper family and level, phase bits and attenuator step, with the quantisation lobes they make |
| Failures | Elements switched off at random, N trials, with the spread of gain and sidelobe level |
| 3D | The existing 3D pattern view |

The renderers for the aperture and the (u,v) map belong in `src/Render`, so the tool, UiSnapshot and the docs draw them
with the same code.

## 9. Verification

Superposition (1) is not in question; it is linear algebra the co-simulation series already verified. What needs
evidence is how much L0 and L1 lose against L2, and that is an EM question. PAA-1 answers it before anything else is
built, on a planar patch fixture:

| Run | Ports | Purpose |
|---|---|---|
| One element | 1 | L0's pattern and S₁₁ |
| 1×2 in the E plane, 1×2 in the H plane | 2 | The strongest and weakest couplings at the chosen pitch |
| 1×4 in the E plane | 4 | The brute-force HB bench of PAA-6 |
| 2×2 | 4 | Smallest array with corners only |
| 5×5 | 25 | The sub-array L1 reads: centre pattern, nine classes, offsets to ±4 |
| The largest square array the budget allows, aimed at 7×7 | ~49 | The reference L1 is gated against: predicted from the 5×5, compared with its own L2 |
| Optional, by hand: a thick high-permittivity substrate, 1×7 | 7 | Whether L1 predicts a scan blindness |
| Optional, by hand, EMerge Draft: 2×2 on a finite ground | 4 | The size of the edge effect an infinite planar ground leaves out |

Arrays of 32×32 and 64×64 are not solved in EM; the large-N gates are on PAA-1's records extended by the engine, against
the engine's own direct sums at fine resolution (§6.2), so they test the arithmetic at size, not the physics again.

Compared at scan angles θ₀ ∈ {0°, 30°, 45°, 60°} in both principal planes: realized gain at the peak, pointing error,
first sidelobe level, RMS pattern error above −20 dB, |ΔΓ_act| per element, and the power balance. Same solver on both
sides, so a tolerance is legitimate; PAA-1 sets it from the numbers and the later gates use those numbers.

## 10. Placement in the code

| Piece | Project | Why |
|---|---|---|
| Lattice, pattern sum, coupling extension, active reflection, metrics | `src/Engine/Array` | Numeric, no domain types: arrays of positions, weights and pattern samples |
| The `PhasedArray` model, its parameters, element scope, `steer` and the other functions | `src/Core` | A component model and the expression engine |
| Reading a record into embedded patterns (terminated or shorted), the lattice of a record's ports, the `fields` analysis on a PhasedArray and on HB waves | `src/Design/Em3d` | Beside `PortFarFieldSet` and `FieldCosimulation` |
| The dynamic symbol | `src/Design/Schematic/BuiltInSymbols.cs` | Beside the Combiner's |
| Aperture and (u,v) renderers | `src/Render` | Drawn by the tool, UiSnapshot and the docs |
| The tool window, the parameter editor rows | `src/Ui` | Avalonia |

A record does not store its ports' positions today (`EmResultRecord`); PAA-2 adds them for new runs, and an older record
used as a sub-array takes its lattice from the component's `RecordDx`/`RecordDy`, with the port order stated.

## 11. Not in this series

Conformal and arbitrary-position arrays beyond `rect` and `tri` (the pattern sum loses its row/column tables and is
~100× slower); sub-arrays with their own time delays; digital beamforming with ADC models; re-solving the sub-array at
each swept spacing (an EM-cell follow-up, §3.3); a Chain Budget transfer that emits a PhasedArray in place of the
Combiner and Steering bench; random per-element phase and amplitude errors drawn by the yield engine.
