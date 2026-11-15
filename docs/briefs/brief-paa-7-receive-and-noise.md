# Brief PAA-7 — receive: incident waves, the array's own noise, G/T

**Series:** PAA-0 … PAA-10 · **Tag:** `R-paa7-<n>` · **Depends on:** PAA-4 · **Design note:** §5
**Area:** `PhasedArrayModel` (sources and noise correlation), `src/Engine/SParameterNoise.cs` (reads a block's own noise
correlation; no change to the method), `src/Engine/HarmonicBalance/` (tones from a block), `src/Engine/Array/`,
`src/Engine/RESOLVED.md`, `src/Core/RESOLVED.md`.

## The gap

A receive array is specified by G/T, noise figure, sensitivity, and how well it rejects an interferer arriving in a
sidelobe. Today an antenna block in a schematic is noisy only as a passive network at its physical temperature, which is
wrong for an antenna, whose ports see the sky and the ground through its pattern; and the only way to put a received
signal on an array is the Chain Budget's splitter with a phase per path, which knows nothing of the element pattern or
of coupling. So G/T cannot be formed, an interferer's attenuation by the pattern is not simulated, and the active noise
figure of coupled LNAs is not seen.

## Requirements

**R-paa7-1 — Incident waves.** Indexed parameters `RxTheta[k]`, `RxPhi[k]`, `RxPd[k]` (power density, dBm/m²),
`RxFreq[k]`, `RxPol[k]` (`theta`, `phi`, `rhcp`, `lhcp`, or an angle) for k = 1…K, all expressions. Each puts on element
n the incident wave of note eq. (4), by reciprocity from the element's embedded pattern at that frequency and direction.
In HB, multi-tone HB and envelope each is a tone at `RxFreq[k]`, as a P1Tone is, so a signal and an interferer pass
through the channel cells' amplifiers together. Works in both modes; in Elements mode the waves appear at the pins.
Fix eq. (4)'s constant from the pattern's normalisation and write it into the note.

**R-paa7-2 — The array's noise correlation.** The component stamps the noise-wave correlation of its ports from note
eq. (5): the brightness model `Tsky` (a temperature or an expression of the elevation angle `el`, a name bound only
while `Tsky` is evaluated and not part of element scope), `Tground` below the horizon (θ > 90° with the array facing +z), and the ohmic part at the
physical temperature from the radiation efficiency. **Computed by offset, never pair by pair** (note §5.2): under L0/L1
the integral is taken once per offset that occurs and the matrix filled from the table, in the same sparse form as the
extended S; under L2 pair by pair, since N is the record's port count. The non-uniform part's reach in offset is
truncated where it falls below a stated fraction of the diagonal, and the truncation is in the run's notes.
With `Tsky = Tground = T0`, the default, the stamp is the Bosma
correlation a passive block stamps today, to 1e-12, so no existing result changes. On a planar (hemisphere) record the
lower half-space is the ground plane and contributes nothing radiated.

**R-paa7-3 — What the existing noise analysis then gives.** No change to `SParameterNoise`'s method: it reads the block's
correlation as it reads any part's. Noise figure and temperature at the feed; per-channel figures through the channel
cells; the **active noise figure**, because each LNA's source is the coupled array and its own noise wave leaving its
input reaches its neighbours through S.

**R-paa7-4 — Receive metrics.** Measurement functions over a `fields` analysis and the noise analysis of the same bench:
`AntennaTempK(F1)`, `SystemTempK(F1, NF1)`, `GoverTDbK(F1, NF1)` (realized gain in the beam direction over the system
temperature at the array's terminals), `SnrDb(F1, NF1, k)` for incident wave k in a stated bandwidth, and `NullDepthDb(F1,
theta, phi)`. Each documented with its formula in the note.

**R-paa7-5 — Interferers and dynamic range.** An `Rx[k]` wave in a sidelobe is attenuated before the channel by exactly
the pattern's ratio; with HB it compresses the channels by what reaches them, not by its free-space density. The bench
of R-paa7-6 shows SFDR against the interferer's angle.

**R-paa7-6 — The bench.** Under `testdata/phased-array/receive-8x8/`: E5's record, an 8×8 Feed-mode array with a channel
of an LNA (Amplifier with NF and IP3) and an Attenuator, a mixer and IF filter after the feed, a signal at boresight and an
interferer swept in angle, S-parameter noise and two-tone HB. No EM run.

## Gates

1. **Reciprocity at boresight.** The power delivered to a matched load at the feed from `RxPd` at boresight equals
   `S·λ²·G_r/4π` with G_r the realized gain of the same array's transmit pattern, to 1e-9.
2. **Uniform sky.** `Tsky = Tground = T0` reproduces today's noise of the same block to 1e-12 (the Bosma identity), on a
   coupled 3×3 array.
3. **Cold sky.** `Tsky = 0 K`, `Tground = 0 K` on a lossless record gives an antenna temperature of 0 K (to 1e-9 K) and a
   system temperature equal to the receiver's alone.
4. **Spatial filtering.** An interferer at a sidelobe whose level is −25 dB reaches the feed 25 dB below the same wave at
   boresight, to 1e-6 dB, with linear channels.
5. **Coupled noise.** On a 1×4 array with LNAs whose input match is poor, the noise figure with `Coupling=on` differs
   from `Coupling=off`, and equals an EmFieldData-block circuit of the same record with explicit LNAs on its four ports
   to 1e-9 dB — the component's noise is the same as the brute-force circuit's.
6. **By offset at size.** On a 64×64 Feed-mode array with a non-uniform sky, the offset-table correlation equals the
   pair-by-pair integral on a 16×16 corner of it to 1e-9, and the full 4096-element noise analysis completes holding no
   dense 4096×4096 array (an allocation counter).

## Not here

A plane-wave EM check of the received voltage: an EM plane-wave run carries no ports today, so it cannot measure what a
port receives; recorded as open. Sky models beyond a temperature and an elevation expression. Digital beamforming.

## Completion note (2026-11-14)

**Built.** Incident waves: `RxTheta[k]`, `RxPhi[k]`, `RxPd[k]`, `RxFreq[k]`, `RxPol[k]` (`theta`, `phi`, `rhcp`, `lhcp`, or an angle) put
cₙ = j·λ·√(2S/η₀)·(p̂·Eₙ)·e^{jk·û·rₙ} on each element (note eq. (4), the constant fixed from G_r = 4π|E|²/η₀ and written into the note),
as a tone source `PA1.rx[k]` (`ArrayIncidentWaveModel`, a `ToneSourceModelBase`) on a source node the array stamps per element — so HB,
multi-tone HB and envelope take it as they take an I_nTone, in both modes. The array's noise is a wave source on the same node (note eq. (5)):
the network's own k·Λ^½·(I − S·Sᴴ)·Λ^½ plus the sky's excess over T̄ through the patterns; `Tsky` (a temperature or an expression of the
elevation `el`, tabulated every 0.01°), `Tground` below the horizon. The Feed-mode beamformer's thermal noise is closed-form. The noise
analysis is unchanged in method; it reads the correlation sparse. Receive metrics `AntennaTempK`, `SystemTempK`, `GoverTDbK`, `SnrDb`,
`NullDepthDb`, formulas in note §5.3. Isolated and embedded arrays now hold their S sparse. The parameter editor lists `Tsky` and `Tground`;
the components page says the receive side. Bench `testdata/phased-array/receive-8x8/` (its README has the numbers).

**Gates** (`tests/Ui.Tests/PhasedArray/PhasedArrayReceiveTests.cs`, 9 tests, 15 s):
1. Reciprocity at boresight: received power over two orthogonal polarizations equals S·λ²·G_r/4π to 1.1e-10 (4×4 Taylor on E1).
2. Uniform sky at the array's own 300 K: the 3×3 array's Cs equals the EM Field Data block's Bosma Cs to 3.5e-15.
3. Cold sky on a lossless record: T_A = 0 K exactly, T_sys equals the array-switched-off value to 1e-9; at 300 K T_A = 300 K and T_sys = T_A + T_rx.
4. Interferer at a −26.16 dB sidelobe sample of an 8×8 Taylor −25 dB array reaches the feed by the pattern's ratio to 2e-11 dB through linear 20 dB channels.
5. 1×4 with LNAs of 3 dB return loss: coupling moves the port's noise 1.07 dB; the component equals the EM Field Data block with explicit LNAs to 4e-15 dB.
6. 64×64 Feed mode, non-uniform sky: the offset table equals the pair-by-pair integral over the 16×16 corner's 65,280 pairs to 1.1e-14; the
   whole noise analysis takes no pair integral, exactly 127² offset integrals and no dense copy (`ArrayCorrelation.DenseCopies`), in ~4 s.
Plus: `SnrDb` equals HB's delivered power over Cs through one-way LNA channels to 1e-9 dB; the bench's linear metrics; the keys survive the `.cnl`.

**What changed from the brief, and why.**
- **Default `Tsky = Tground` = the array's own temperature, not T0**: the default ambient is 300 K, and a T0 default would have moved every
  existing array's noise. With it nothing that ran before moves (gate 2).
- **Gate 3 needs a record that is lossless**, and no EM record is (PAA-1's laminate has tanδ 0.0037, and a planar run loses power to the
  surface wave): the test writes one, a terminated whole-sphere dipole pattern whose radiated power is exactly 1 − |S₁₁|² by the code's own
  quadrature.
- **The sky's part is not filled into a sparse matrix and truncated**; it is held as its offset table (`ArrayOffsetTerm`). Two findings
  forced it. Integrated over the record's 5° grid it never decays (aliasing: the phase turns over a radian per sample past a few wavelengths of
  offset), so it is now a Fourier integral over direction cosines on the lattice's own period (`ArraySkyTransform`). Correctly integrated it
  still reaches across the whole 64×64 aperture — 14,404 of 16,129 offsets above 1e-4 of the diagonal — because a horizon-bright sky is a ring
  whose transform decays as 1/√(kΔ). A fill would be dense; the table is 16,129 numbers and a quadratic form sums pairs from it. The note
  states the reach instead of a truncation. Gate 6's "pair by pair" is the same direction-cosine integral taken per displacement.
- **The receive metrics use receive weights from the circuit, not the fields analysis's transmit waves**: the bench's LNA channels pass
  nothing from the port back to the elements. The fields analysis still names the port, frequency and record.
- **R-paa7-5's "attenuated before the channel by exactly the pattern's ratio" holds at the feed, not before the channel**: in Feed mode the
  channels sit between the elements and the beamformer, so an interferer reaches each LNA through the element's pattern and is suppressed by
  the array factor only after the channels. Gate 4 is at the feed; the bench shows both (an interferer at 30° is −73.7 dBm at every LNA and
  −81.7 dBm at the feed against −44.1 dBm in the beam).
- **The bench is two files.** One HB point with 64 nonlinear channels is about a minute single-threaded (two tones); three tones (signal,
  interferer, LO) did not finish a point in ten minutes. `Receive Bench.cnl` runs the whole chain with the signal and the LO;
  `Interferer Bench.cnl` the front end with two equal interferers whose product falls on the signal, swept in angle. SFDR against angle is
  formed in the README from both (80.3, 82.7, 89.9, 82.4 dB at 0, 10, 20, 30°).

**Found and fixed on the way.** PAA-4 left a 4096-element array's S dense per record frequency (268 MB) and each stamp copied it again; the
isolated and embedded levels are sparse now, and the beamformer's Bosma recording (which would have inverted a dense ~12,000-unknown system)
is closed-form.

**Not here.** A plane-wave EM check of the received voltage (recorded open, as the brief says). The HB cost of many nonlinear channels (PAA-8's
territory, or an HB engine brief). Incident waves at a frequency the record does not hold use its nearest pattern frequency and say so. A
non-uniform sky at exact level integrates pair by pair on the record's own grid, which is fine for the record's few-wavelength offsets.
User docs beyond the components page paragraph are PAA-10's.
