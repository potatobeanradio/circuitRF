# Brief PCH-8 — instance ends, clustered delay lines, polarization

**Series:** PCH-0 … PCH-12 · **Tag:** `R-pch8-<n>` · **Depends on:** PCH-7, the PAA series · **Design note:** §2.1,
§4.2, §5.2, §5.4
**Area:** `PropagationChannelModel`, `ArrayIncidentWaveModel`, `FieldCosimulation` (the departure field), `EnvelopeChannel`,
`src/Engine/Array/`, `src/RfCore/Propagation/` (CDL tables, ray-list reader), the dynamic symbol, `src/Engine/RESOLVED.md`.

## The gap

A tapped delay line has no directions, and an array's gain in a real channel depends on them: a beam collects the
clusters it points at and loses the rest. The transmit array's field and the receive array's incident waves both exist
(PAA-6b, PAA-7) and nothing joins them.

## Requirements

**R-pch8-1 — Instance ends.** `Tx=` / `Rx=` name a `PhasedArray` (either mode) or an EM Field Data block with far fields;
that end has no pin; the two kinds mix (D5). The symbol draws only the remaining pins.

**R-pch8-2 — Ray unknowns.** Linear analyses and HB: K auxiliary unknowns y_r = M_r·g_tx(Ω_D,r)·a and a source
Σ_r c_m(Ω_A,r)·y_r on each receive element (note §5.4), with c_m from PAA-7's eq. (4). Never an N_r × N_t matrix.

**R-pch8-3 — Envelope.** The departure field per ray from the waves at t − τ_r (PAA-6b's basis and weight, without its
16-direction cap for rays), projected on each receive element per step. Cost counters: multiply-adds per step, rays,
elements.

**R-pch8-4 — Receive collapse.** Where the receive array meets the fast-path conditions (PAA-8), its beamformed output is
formed from Σ_r AF_rx(Ω_A,r)·… instead of element by element, only when gate 3 holds.

**R-pch8-5 — CDL.** `Multipath=cdl Profile=`: 3GPP TR 38.901 CDL-A…E and 3GPP TR 38.811 NTN-CDL, scaled by `DelaySpread`
and angle spreads, rays at the report's intra-cluster offsets, rotated to the ends' line (note §5.3); random initial
phases per ray and polarization pair per drop; cross-polar ratio per cluster.

**R-pch8-6 — User rays.** `Multipath=rays File=rays.csv`: delay, power, departure θ/φ, arrival θ/φ, and a cross-polar
ratio or a 2×2 complex matrix; `RayFrame=global|ends`.

**R-pch8-7 — Doppler per ray.** ν_r from the ends' velocities and the ray's directions (note §4.2); a satellite end from
its orbit.

**R-pch8-8 — Polarization.** Each end's field as (θ̂, φ̂) components; M_r carries the ray's polarization; P.531 Faraday
rotation on satellite links below 3 GHz as a rotation of the incident polarization.

**R-pch8-9 — Interference.** Several channels may end on one instance receive end; their contributions add. The result
names each channel's received power and the SINR at the feed.

**R-pch8-10 — Far field.** `check` warns when d < 2D²/λ for either end (note §5.2).

## Gates

1. **The existing reciprocity gate, through the channel.** One line-of-sight ray between a transmit pin of known EIRP and
   an 8×8 receive array equals PAA-7's incident wave from the same direction at the free-space power density, to 1e-10.
2. **Ray unknowns are sparse.** A 64×64 to 64×64 link with CDL-A holds no array of N_r × N_t (an allocation counter).
3. **Collapse equals elements.** On a 16×16 receive array with identical linear channel cells, the collapsed output equals
   the element-by-element output to 1e-12.
4. **CDL tables.** Each profile's cluster powers sum to 1 and its scaled delay and angle spreads equal the stated ones to
   1e-9; rotation puts the line-of-sight cluster of CDL-D on the ends' line to 1e-12 rad.
5. **Beam versus spread.** A fixed boresight beam on a 16×16 array in CDL-C loses array gain against line of sight by the
   amount the independent script predicts from the same rays, to 0.01 dB.
6. **Interference.** Two transmitters through two channels into one array: received powers add in the uncorrelated case
   and SINR equals the script's to 0.01 dB.

## Not here

Choosing the beam (PCH-9). Scenario drops (PCH-10). Near-field coupling between ends.
