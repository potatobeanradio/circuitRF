# Propagation model reference data (briefs PCH-1, PCH-2, PCH-7, PCH-8, PCH-10)

Fixed data the large-scale propagation models (`src/RfCore/Propagation`) are checked against, by
`tests/RfCore.Tests/Propagation/PropagationModelTests.cs`. None of it is produced by circuitRF.

| Folder | What | Source |
|---|---|---|
| `p1812/` | 19 terrain profiles and `cases.csv`: 63 cases with their inputs and every intermediate and final value of an ITU-R P.1812-8 prediction | The validation profiles and results of the Rec. ITU-R P.1812 software package version 8.0, which its copyright holder makes available free from copyright restriction. Converted to circuitRF's profile CSV (distance, terrain height, representative clutter height, radio-climatic zone) and one table of cases; nothing else changed. |
| `reference/` | `reference.csv` (67 cases across every model and added loss), `spreads_38901.csv`, and `satellite_geometry.csv` (a pass's range, range rate and drift by finite differences of the simulated orbit) | `propagation_reference.py`, an independent implementation written from the recommendations and the 3GPP reports alone, standard library only. Run it to regenerate all three files. |
| `ici_reference.py`, `ici_reference.json` | Gate 6 of brief PCH-7: for f_D = 300, 600 and 1200 Hz, the error power a genie-equalized OFDM symbol (30 kHz) has through one flat Rayleigh drop, on a 2 µs grid of symbol-middle times | `ici_reference.py`, which writes the fading generator again from its stated definition (src/RfCore/Propagation/Fading.cs header) and computes each symbol's inter-carrier interference by Parseval over its useful part, reading none of circuitRF's demodulator. Standard library only; run it to regenerate the JSON. |
| `array_link_reference.py`, `array_link_rays.json`, `array_link_reference.json` | Gates 5 and 6 of brief PCH-8: the feed power of a 16 × 16 isolated-element array through the line of sight, through CDL-C, and from a signal and an interferer through two channels | `array_link_rays.json` holds each case's rays as the channel realizes them (powers, phases, delays, polarization matrices, the two ends' fields along each ray, the arrival in the array's frame), written by the gate test with `CRF_WRITE_ARRAY_LINK=1`; the test fails if the channel's rays move from it. `array_link_reference.py` computes the feed power from those rays alone: each element's wave by PAA-7's eq. (4), the array factor from the lattice, the matched beamformer's sum. Standard library only. |
| `scenario_drop_reference.py`, `scenario_drop_reference.json` | Gate 3 of brief PCH-10: for UMi and InH, line of sight and not, at 28 GHz, the 199 quantiles (0.5 % to 99.5 %) of a drop's realized RMS delay spread, circular azimuth spreads, zenith spreads and K over 10⁴ drops, and of the large-scale parameters drawn; in line of sight also the variant with the −25 dB cluster removal on eq. 7.5-6's powers, the reading circuitRF takes | `scenario_drop_reference.py`, an independent implementation of TR 38.901 V18.0.0 §7.5 Steps 4-10 written from the report alone (every table value it uses is in the file with its table), numpy only; run it to regenerate the JSON. The test compares circuitRF's drops against the quantiles by a two-sample Kolmogorov-Smirnov distance at α = 0.001. |
| `sg3-validation/` | Not committed (git-ignored) | ITU-R Study Group 3's Earth-space validation workbook (P.618, P.676, P.838, P.840). The test that reads it skips when the folder is absent; `src/RfCore/RESOLVED.md` says how to produce it. |
| `p526-validation/` | Not committed (git-ignored) | ITU-R Study Group 3's delta-Bullington validation examples for P.526 §4.5 (Document 3J/77). The test that reads them skips when the folder is absent; `src/RfCore/RESOLVED.md` says how to produce it. |

A terrain profile is a CSV whose header names each column with its unit in parentheses:

```
distance (km), height (m), clutter (m), zone
0,     754.4, 10, inland
0.2,   754.4, 10, inland
```
