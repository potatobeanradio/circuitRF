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
