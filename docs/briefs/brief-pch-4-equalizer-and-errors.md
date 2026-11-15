# Brief PCH-4 — equalizer, frequency offset, bit errors

**Series:** PCH-0 … PCH-12 · **Tag:** `R-pch4-<n>` · **Depends on:** — · **Design note:** §7.1–§7.4
**Area:** `src/RfCore/Signals/` (`SignalEvm`, `SignalQuality`, new `SignalEqualizer.cs`, `SignalDecisions.cs`),
`src/Engine/Envelope/EnvelopeDataSet.cs`, `src/Core/Netlist/AnalysisDirectiveSchema.cs`, the Data Display constellation,
`src/RfCore/RESOLVED.md`.

## The gap

EVM is measured with one complex gain over the record and no equalizer, by design. Through any channel with delay spread
that reads a frequency-selective fade as distortion; a carrier offset rotates the constellation across the record and
reads as error. Nothing decides symbols, so there is no symbol or bit error rate, and a link is specified by BER.

## Requirements

**R-pch4-1 — `Equalizer=none|genie|pilot`** on the envelope analysis, per probe as `EvmSplit` is. `none`, the default,
is today's measurement unchanged.

**R-pch4-2 — `genie`.** OFDM: per subcarrier per symbol from the true channel — a propagation channel's H(f, t) when the
run supplies one (an interface PCH-6 fills), times one fitted complex gain; with none, the per-subcarrier response
averaged over the record. Single carrier (DBPSK, and PCH-5's): a linear MMSE equalizer from the known channel. Not a
per-element least-squares fit, which would fit the error away.

**R-pch4-3 — `pilot`.** OFDM: estimates only from designated resource elements (`Pilots=` a comb spacing in subcarriers
and symbols), interpolated in frequency then time, EVM measured on the rest. Single carrier: an MMSE FIR (`Taps=`)
trained on the first `Training=` symbols and applied to the remainder.

**R-pch4-4 — Frequency offset and drift.** Data-aided estimate of the phase slope and curvature over the record, removed
before the equalizer; `CFO_Hz`, `CFOdrift_Hzps` cubes. With `Equalizer=none` the offset is still reported but not removed,
so `none` stays exactly today's.

**R-pch4-5 — Decisions and counted errors.** Nearest-point decisions after equalization; the generator's own bit mapping
gives the sent bits. Cubes `SER`, `BER`, `BitErrors`, `Bits` per probe and carrier, and the exact binomial interval at
95 %. FSK and chirp carriers decide by their own detectors (frequency sign; de-chirped bin).

**R-pch4-6 — Semi-analytic BER.** Each resource element's post-equalizer SINR from its error vector; the modulation's
closed-form conditional BER; averaged over the record. `BERest` with the note *estimated (Gaussian)*. With `EvmSplit`, a
distortion share far from Gaussian (a stated kurtosis test) is said in the notes.

**R-pch4-7 — Per-element SINR.** A cube over subcarrier and symbol (OFDM) or symbol (single carrier), for the user's own
coded mapping.

**R-pch4-8 — Display.** The constellation plot shows the equalized points when an equalizer ran; the trace card names
which.

## Gates

1. **Unchanged.** `Equalizer=none` reproduces every committed envelope EVM to the last digit.
2. **Static multipath, genie.** An OFDM signal through a two-path channel built from today's blocks (splitter, `Delay`,
   `Attenuator`, combiner) with the second path inside the cyclic prefix: EVM equals the no-channel EVM to 1e-6 %; with
   the second path beyond the prefix, EVM rises (intersymbol interference) and the cube says so.
3. **Pilot approaches genie.** With added noise at 40 dB SNR, `pilot` EVM is within 0.5 dB of `genie`.
4. **Offset.** A 5 kHz offset on an OFDM record: `CFO_Hz` = 5000 to 1 Hz; EVM after removal equals EVM without the offset
   to 1e-3 %.
5. **Counted BER.** QPSK and 16QAM in AWGN at three Eb/N0: counted BER within its 95 % interval of the closed form.
6. **Semi-analytic.** At Eb/N0 = 12 dB (QPSK) `BERest` equals the closed form to 1 %.

## Not here

Forward error correction and coded block error rate. A pilot pattern of any particular standard (the comb is generic).
