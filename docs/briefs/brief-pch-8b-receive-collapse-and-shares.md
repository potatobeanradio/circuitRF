# Brief PCH-8b — the receive collapse, each channel's share at the feed, Field Data ends

**Series:** PCH-0 … PCH-12 · **Tag:** `R-pch8b-<n>` · **Depends on:** PCH-8, PAA-8 · **Design note:** §5.2, §5.4, §8.2
**Area:** `src/Engine/Array/PhasedArrayFastPath.cs`, `PropagationChannelModel` (`.Ends.cs`), `EnvelopeChannel`,
`PropagationChannelReport`, the EM Field Data block (`EmFieldDataRecord`, `ComponentModelFactory.CreateEmFieldDataModel`),
`src/Engine/RESOLVED.md`.

## The gap

PCH-8 joined the arrays to the channel, and three parts of that brief remain open:

1. **Every receive element is solved, even when the array is a linear filter of its incident field.** A Feed-mode receive
   array whose channel cells are identical and linear (PAA-8's fast-path conditions) maps its elements' received waves to
   the feed by one closure that does not depend on the field. The channel still injects onto every element's source row,
   the MNA still carries every element, and an envelope run still steps them all. At 64 × 64 that is 4096 elements and
   their channel cells per drop for what is, at the feed, a weighted sum over the rays.
2. **The result does not say what each channel delivers.** Several channels into one array add, and gate 6 measures that
   from harmonic-balance tones. But a run reports no per-channel received power at the feed and no SINR, so a bench with a
   signal and interferers on the same frequency has no way to read either. Note §5.6 and R-pch8-9 ask for both.
3. **An EM Field Data block cannot be a channel end.** It is an S-parameter block with no received-wave source per port.
   Transmitting from one needs only its port waves, which it has. Receiving into one needs the source nodes the Phased
   Antenna Array gained in PAA-7. Today the channel refuses either end on one, naming the array as the alternative.

## Requirements

**R-pch8b-1 — The receive collapse.** When a channel's receive end is a Feed-mode array that meets PAA-8's fast-path
conditions (identical linear channel cells, real Z0, no circuit wired to its internal nodes, nothing nonlinear inside
it), the array is not stamped element by element for the channel's contribution. Its feed takes
`Σ_r AF_rx(Ω_A,r) · (closure response) · y_r`, where `AF_rx` is formed from the beamformer weights, the element positions and
the element pattern (per pattern class at the embedded level), and the closure is PAA-8's `(I − Γ_ch·S)⁻¹` read in the
receive direction. This applies in S-parameter and HB runs, and in the envelope, where the receive side is then not stepped
at all (note §8.2's "a receive side that meets the fast-path conditions is a linear filter of its incident field"). The
decision and its reason go in one sentence that explain prints, as PAA-8's `Decide` does. Anything the fast path refuses
falls back to the element-by-element stamp with that sentence.

**R-pch8b-2 — Counters.** Report what the collapse saved: elements and channel cells not stamped, multiply-adds per step in
the envelope, and solves avoided. These are counters, never timings.

**R-pch8b-3 — Each channel's share at the feed.** For every channel ending on an array, the run reports
`link:<instance>:FeedPower_dBm`: the power that channel alone delivers at the array's feed. On the collapse path this comes
from its own term of the sum; otherwise it comes from a solve with only that channel's coupling present, reusing the
factorization. With several channels on one array, `link:<array>:SINR_dB[channel]` gives each channel's power over the
sum of the others' plus the array's own noise at the feed. The noise comes from the bench's S-parameter noise analysis when
there is one (PAA-7's `SystemTempK` path), and the cube says so when there is none (SIR only). HB runs report one value
per tone a channel carries; envelope runs report averages over the measurement window.

**R-pch8b-4 — EM Field Data ends.** `Tx=`/`Rx=` may name an EM Field Data block with far fields. As a transmit end it uses
its port waves and the record's patterns, exactly as a Phased Antenna Array's elements do. As a receive end it gains one
received-wave source node per port, read through `b − S·a = V_src` as the array's are, so the channel injects onto them.
The block's existing stamp, and every bench that does not use it as an end, must not change by a bit.

## Gates

1. **Collapse equals elements.** On a 16 × 16 Feed-mode receive array with identical linear channel cells (an LNA and an
   attenuator), embedded level with coupling on: S-parameter, HB and envelope feed responses through CDL-C match the
   element-by-element solve to 1e-12 (HB, S-parameter) and to the envelope's own tolerance-limited residual (state it).
2. **The collapse is taken and refused correctly.** It is taken for identical channels, and refused with its sentence for
   a channel cell that reads `n`, a nonlinear channel, and a circuit wired to an element node. The counters show which.
3. **Shares add.** Two channels into one array on one frequency: each `FeedPower_dBm` equals the feed power of a run with
   only that channel, to 1e-9 dB. The SINR against an S-parameter noise analysis equals
   `testdata/propagation/array_link_reference.py`, extended for noise, to 0.01 dB.
4. **Field Data ends.** A one-port EM Field Data block on the e1 record as a receive end puts on its port exactly the wave a
   1 × 1 Phased Antenna Array on the same record receives through the same ray, to 1e-12. As a transmit end it matches the
   1 × 1 array to the same tolerance. Every existing EM Field Data test is unchanged.

## Not here

Choosing the beam (PCH-9). Drops and their reuse (PCH-10). The collapse for Elements-mode arrays (their elements are the
user's circuit).
