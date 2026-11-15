# Brief PCH-6 — the Propagation Channel component, pin ends

**Series:** PCH-0 … PCH-12 · **Tag:** `R-pch6-<n>` · **Depends on:** PCH-1 (PCH-2 for its models) · **Design note:**
§0, §2, §5.1, §5.3–§5.6
**Area:** `src/Core/Devices/System/PropagationChannelModel.cs` (new), `ComponentModelFactory`, `ComponentTypeRegistry`,
`src/Engine/Envelope/EnvelopeChannel.cs` (new), the HB tone set, `src/Cli` (`check`, `explain`, `reference`),
`BuiltInSymbols`, the parameter editor, `src/Core/RESOLVED.md`, `src/Engine/RESOLVED.md`.

## The gap

Nothing in a schematic carries a signal from a transmit antenna to a receive antenna. A link today is two benches and a
hand calculation between them, so a PA's distortion, a channel and a receiver's response are never in one result.

## Requirements

**R-pch6-1 — The component.** `PropagationChannel:CH1 tx rx …`, two pins, palette entry under System, a symbol that
reads as a propagation path. Parameters per note §5.5 that do not need instance ends or fading (those are PCH-7/8),
every one an expression; structural words are words (D12).

**R-pch6-2 — Ends.** Each pin end: `TxGain`/`RxGain`, or `TxPattern`/`RxPattern` naming one port of an EM result's far
field (`PortFarFieldSet`), oriented by azimuth, elevation and roll; `TxZ`/`RxZ`, or the record's S11 when a pattern is
named. The receive pin's noise is `RxTant`, default the ambient temperature.

**R-pch6-3 — Geometry.** `Distance` with heights, or positions; for satellite models altitude and elevation; velocities
(note §5.3). A deterministic ray list from the geometry: the line-of-sight ray, `tworay`'s ground ray, and
`Multipath=file` taps held static (fading is PCH-7).

**R-pch6-4 — Large scale.** Any PCH-1/PCH-2 model by name, its parameters from the registry; shadowing `X` held at 0 and
`Rain=drawn` refused here (both drawn in PCH-10); `Rain=fixed` works. The breakdown in the result (note §5.6).

**R-pch6-5 — Linear analyses.** S-parameter and noise: a one-way two-port, S21 = H(f) at `TimeOffset`, S12 = 0,
S11/S22 the ends' impedances; no noise of its own beyond `RxTant`.

**R-pch6-6 — HB.** The frozen H at every tone the transmit end carries, each at its frequency; no Doppler; a pattern
record lacking a tone's frequency uses its nearest and says so.

**R-pch6-7 — Envelope, explicit.** `EnvelopeChannel` per note §5.4: the transmit pin's outgoing wave recorded each step;
the receive pin driven by Σ over rays of the delayed, rotated, scaled wave; simulated latency of at least one step plus
the excess delays; a common Doppler ν₀ and its drift as a time-varying phase; quadratic Lagrange interpolation on solved
steps only. The receive side's first τ_max is excluded from measurement windows and the notes say so.

**R-pch6-8 — Reverse.** `Reverse=CH1` makes a channel the transposed realization of another (D3).

**R-pch6-9 — Genie interface.** The run exposes the channel's H(f, t) on the signal band to PCH-4's `genie` equalizer.

**R-pch6-10 — `check` and `explain`.** `check`: model validity, a cycle of channels in an envelope analysis (refused,
naming the instances), a receive pin driven by two channels. `explain --analysis`: model and edition, breakdown,
τ₀, ν₀, the regime (f_D·T_record, f_D/Δf_sc, coherence bandwidth against signal bandwidth) and the ray count.

**R-pch6-11 — Editor and symbol.** Parameter groups as note §5.5; UiSnapshot PNG of the symbol and the editor, looked at.

## Gates

1. **Friis.** Free space, two isotropic pin ends, S21 = λ/(4πd) to 1e-12 in an S-parameter analysis; HB delivered power
   equals the closed form to 1e-9 dB.
2. **Explicit equals joint.** A PA → channel → LNA envelope run equals, to 1e-12, the receive side run alone driven by the
   recorded transmit wave (the partition PCH-10 will reuse).
3. **Doppler.** A CW through a single ray with ν = 1 kHz appears at f + 1 kHz in the receive envelope spectrum, to one bin;
   an envelope of a two-ray channel with distinct Dopplers beats at their difference.
4. **Delay.** A two-ray static channel's envelope frequency response equals H(f) to 1e-6 over the band, including a delay
   that is a non-integer number of steps.
5. **One-way.** Changing the receive side changes nothing on the transmit side, bit for bit.
6. **check.** A two-channel cycle in an envelope bench is refused naming both.

## Not here

Fading and TDL profiles (PCH-7), instance ends (PCH-8), drawing anything random (PCH-10).
