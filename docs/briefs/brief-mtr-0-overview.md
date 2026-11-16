# Brief series MTR — modulated sources in transient

**Goal.** A `MOD_Source` (LTE, NR, single-carrier QAM/APSK, FSK, DBPSK, CSS, pulsed CW, an imported I/Q record) plays its
signal in the native transient analysis, carrier-resolved, over a window of its record. By default, the results come back
at the signal's baseband rate as the same envelopes the envelope analysis reports. That gives the same PSD, ACLR, EVM, AM-AM
and constellation cubes, read by the same code. Storing every RF sample instead is an option, and the analysis card warns
about it.

**Series:** MTR-0 … MTR-3 · **Tag:** `R-mtr<n>-<k>` · **Depends on:** the TRN series (TRN-1, TRN-2, TRN-5)
**Design note:** [`docs/design/transient.md`](../design/transient.md) §6.4 (added by MTR-1) and §8.1 (added by MTR-2).

## Why

The envelope analysis is the right tool for a modulated signal when the circuit's response is a slowly varying set of
harmonics of the carrier. Some circuits break that assumption, and for those the only honest answer is a run that
resolves every carrier cycle:

- switching or saturating stages (class-D/E, limiters, detectors, comparators) driven by a real modulated signal;
- a modulated signal beside an unrelated tone (an LO), injection locking or pulling, start-up and settling while the
  modulation runs;
- a check of an envelope result on the same bench, over a short window.

Until now, a `MOD_Source` in a transient analysis is refused by name, and the bench has no carrier-resolved run at all.

## The cost, and what it shapes

A whole record is out of reach. A 1 ms subframe on a 2.4 GHz carrier is 2.4 million carrier periods and roughly 50
million steps. A window is affordable: 10 µs is about 24,000 periods and roughly half a million steps. So the feature runs
a **window** of the record, picked by where it starts (`RecordStart`) and by the analysis's `Stop`.

Storing the waveform at RF rate is the second cost. Every recorded net becomes millions of points. The run therefore
downconverts as it steps and keeps only the envelopes, unless asked otherwise.

## The series

| Brief | Delivers |
|---|---|
| [MTR-1](brief-mtr-1-source.md) | `MOD_Source` in native transient: its signal behind its Z, a window of the record (`RecordStart`), a soft start (`Ramp`), the step bound |
| [MTR-2](brief-mtr-2-baseband-results.md) | `Store=auto\|baseband\|rf` on the tran card: the streaming downconverter, envelope-shaped results, the envelope measurements |
| [MTR-3](brief-mtr-3-surface-and-docs.md) | The card (Store, the RF-rate warning), the `.cnl`/`.csch`/duplicate/CLI round trip, `check`, the user pages |

## Decisions

- **D1 — Native only.** ngspice keeps refusing a `MOD_Source`. A sampled stimulus written into a deck is an
  approximation, and the deck writer translates exactly or refuses.
- **D2 — Baseband is the default** whenever the bench has a `MOD_Source` (`Store=auto`). Without one, `auto` is RF, as
  every transient run is today.
- **D3 — One measurement path.** Baseband results are built as an envelope run result and measured by
  `EnvelopeDataSet.Build`. A modulated transient result therefore has the envelope's cubes, names and units, and nothing
  re-implements EVM or ACLR.
- **D4 — HB and oscillator seeds play the source as CW.** The seed runs of TRN-8 want the steady state harmonic balance
  solves for, which for a `MOD_Source` is its CW tone at `Fc`.

## Not here

ngspice; a modulated source at more than one centre frequency in one baseband run (refused, with `Store=rf` named);
Delay on a `MOD_Source`; noise and phase noise of the envelope analysis.
