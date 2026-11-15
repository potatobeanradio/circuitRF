# Brief PCH-5 — a single-carrier PSK/QAM/APSK waveform

**Series:** PCH-0 … PCH-12 · **Tag:** `R-pch5-<n>` · **Depends on:** PCH-4 · **Design note:** §7.5
**Area:** `src/RfCore/Signals/` (new `ScQam.cs`, `SignalSpec`, `SignalQuality`), `src/Core/Devices/ModSourceModel.cs`,
`src/Ui/Signals/` (Signal Designer), `src/RfCore/RESOLVED.md`.

## The gap

The modulated sources are OFDM (LTE, NR), FSK, DBPSK, chirp spread spectrum, pulsed CW and imported I/Q. Satellite and
many point-to-point links are single carrier with root-raised-cosine shaping, and higher-order satellite links use APSK,
whose rings tolerate a compressed amplifier better than square QAM. None can be generated or demodulated.

## Requirements

**R-pch5-1 — The carrier.** `Carrier[k]="SC;Rs=…;Mod=BPSK|QPSK|8PSK|16QAM|64QAM|256QAM|16APSK|32APSK;RollOff=…;
Rings=…;Data=PN9|PN15|PN23;Preamble=…;PilotBlock=…;Off=…;P=…"`: root-raised-cosine shaping over a stated span, APSK ring
ratios and per-ring phase offsets as parameters (stated defaults), Gray mapping (APSK: the natural ring-wise mapping,
stated), an optional preamble and periodic pilot blocks. The record is whole symbols and periodic, as the other carriers'.

**R-pch5-2 — Demodulation.** Matched filter, timing from the known record (the delay fit `SignalEvm` already makes),
then PCH-4's offset removal, equalizer and decisions. EVM against the ideal points (APSK EVM normalised to the
constellation's mean power, stated), MER, constellation, SER/BER.

**R-pch5-3 — Signal Designer.** The carrier is offered and drawn (spectrum, constellation, CCDF) like the others.

**R-pch5-4 — PAPR.** Reported per record; the `Clip` option applies as to the other carriers.

## Gates

1. **Native record.** EVM of the unfiltered native record is 0 to 1e-9 % for every modulation.
2. **Shaping.** The spectrum's occupied bandwidth equals Rs·(1 + roll-off) to one bin; the matched-filtered eye has zero
   intersymbol interference at the symbol instants to 1e-6.
3. **Through a pad.** A matched pad reads the native EVM, and a delay of 3.3 symbols is recovered.
4. **AWGN BER.** 8PSK and 16APSK counted BER within the interval of the closed form (8PSK) and of an independent
   simulation script (16APSK, `testdata/propagation/apsk_ber_reference.py`).

## Not here

Any standard's framing, coding or physical-layer signalling; the carrier is a modulation, not a standard.
