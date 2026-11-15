# Propagation model reference data (brief PCH-1)

Fixed data the large-scale propagation models (`src/RfCore/Propagation`) are checked against, by
`tests/RfCore.Tests/Propagation/PropagationModelTests.cs`. None of it is produced by circuitRF.

| Folder | What | Source |
|---|---|---|
| `p1812/` | 19 terrain profiles and `cases.csv`: 63 cases with their inputs and every intermediate and final value of an ITU-R P.1812-8 prediction | The validation profiles and results of the Rec. ITU-R P.1812 software package version 8.0, which its copyright holder makes available free from copyright restriction. Converted to circuitRF's profile CSV (distance, terrain height, representative clutter height, radio-climatic zone) and one table of cases; nothing else changed. |
| `reference/` | `reference.csv` (62 cases across every model and added loss) and `spreads_38901.csv` | `propagation_reference.py`, an independent implementation written from the recommendations and TR 38.901 alone, standard library only. Run it to regenerate both files. |
| `p526-validation/` | Not committed (git-ignored) | ITU-R Study Group 3's delta-Bullington validation examples for P.526 §4.5 (Document 3J/77). The test that reads them skips when the folder is absent; `src/RfCore/RESOLVED.md` says how to produce it. |

A terrain profile is a CSV whose header names each column with its unit in parentheses:

```
distance (km), height (m), clutter (m), zone
0,     754.4, 10, inland
0.2,   754.4, 10, inland
```
