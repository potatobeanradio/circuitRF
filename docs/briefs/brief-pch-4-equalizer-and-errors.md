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

## Completion note (2026-11-15)

**Built.**
- `src/RfCore/Signals/SignalEqualizer.cs`: the offset is estimated from each symbol's or bit's phase against the
  played record, with each subcarrier's response divided out first. It is removed by three passes of
  re-demodulation in the time domain (`ToNative` and `Demodulate` gained a drift term; `ReceivedSymbols` carries
  each symbol's time, row and whether it wraps the window). OFDM genie uses the per-subcarrier response over the
  record, or an `IChannelResponse` times one gain (the interface PCH-6 fills). OFDM pilot uses the comb, each
  pilot fitted by least squares over ±4 pilot symbols, interpolated in frequency (extrapolated at the edges), then in
  time. DBPSK genie is a frequency-domain MMSE; DBPSK pilot is a symbol-spaced FIR trained by least squares.
- `SignalDecisions.cs`: QAM decided per axis with Gray coding, DBPSK differentially, FSK by sign, CSS by the
  de-chirped bin; the exact binomial interval (`RfCore.Statistics.BetaFunction`); the conditional BERs, the
  semi-analytic average and the kurtosis test.
- Keys `Equalizer`, `Pilots`, `Taps`, `Training`, plumbed through the reader, writer, schema, `.csch` DTO, duplicate,
  CLI rebuild, runner plan and the GUI analysis card (a Demodulation section).
- `EnvelopeDataSet` adds `CFO_Hz`, `CFOdrift_Hzps`, `SER`, `BER`, `BitErrors`, `Bits`, `BERlo`, `BERhi`, `BERest`,
  `SINR_dB` [branch, carrier, element, subcarrier] and `__Equalizer`. The far-from-Gaussian note runs with
  EvmSplit.
- The Signal quality preset names the equalizer on the received trace and adds CFO, SER, BER and BERest to its table.
- The ideal Delay block is stepped as its delay in an envelope run (see below).

**Gates.**
1. **Unchanged.** `Equalizer=none` is the old measurement by construction: the same `SignalEvm.Evaluate` and
   `SignalQuality.Evaluate`, on the window as it came. The envelope EVM, noise and line-delay classes (69 tests)
   and the Ui examples and preset tests that read committed EVMs pass with their tolerances unchanged.
2. **Static multipath, genie** (`EnvelopeEqualizerTests`, ~1 s). A splitter, `Delay`, `Atten` and combiner,
   unshaped LTE. With the echo inside the prefix, EVM is the no-channel EVM to 1e-3 % (one gain reads 48 %). Beyond
   it, 23.6 % with a BER of 0.089.
3. **Pilot approaches genie.** At 40 dB per element through a two-path channel, pilot is within 0.5 dB of genie.
4. **Offset.** 5 kHz is reported to 1 Hz, and the genie EVM with it removed equals the EVM with no offset to 1e-3 %.
5. **Counted BER.** QPSK at 0, 4 and 7 dB and 16QAM at 4, 8 and 10 dB: the closed form lies in the exact 95 %
   interval in all six.
6. **Semi-analytic.** At Eb/N0 = 12 dB, QPSK's `BERest` matches the closed form to 1 %.

Gates 3–6 are in `SignalEqualizerTests` (9 tests, ~1 s). A key round-trip test and the preset label test complete the
set.

**What changed from the brief, and why.**
- **Gate 2 is held to 1e-3 %, not 1e-6 %.** The no-channel run itself reads 8e-4 % (the envelope's playback of the
  record; the same at `Tol=1e-12`), so 1e-6 % is below what the run resolves. The source has to be unshaped for
  the comparison: shaping filters the record after the prefix is inserted.
- **The Delay block needed a fix of its own.** In an envelope run it was a rational fit: 35 s per run and only
  approximately a delay. It is now stepped as a lossless line.
- **The offset is reported after three passes in every mode,** because the first estimate is biased by the offset's
  own inter-carrier interference (2.3 Hz at 5 kHz). `none` still measures the window as it came.
- **The pilot estimate averages over ±4 pilot symbols in time.** A single pilot element is as noisy as the data, so
  this is needed to come within 0.5 dB of genie at 40 dB. It assumes the channel changes slowly over about 2 ms at
  15 kHz.
- **`Equalizer` is set for the analysis, as `EvmSplit` is,** and applies to every probe.

Findings in `src/RfCore/RESOLVED.md` and `src/Engine/RESOLVED.md`.
