# TFR-V reference runs: a metal line on the silicon demo

A 73 µm wide, 640 µm long Metal3 line on `rfic-Si_3LM_TFR_100um`, with no film drawn, against the same line on that stack with its film
level and contact via taken out and their 50 nm added to `ILD2 Lower`, so the total height is unchanged. `tests/Ui.Tests/Em/SiFilmDemoTests.cs`
reads what is here. No solver runs in CI.

| File | Stack | Unknowns |
|---|---|---|
| `refs/p_line.s2p` | `tech/`, the shipped technology | 212 |
| `refs/p_line_noband.s2p` | `noband/tech/no-band.ctech` | 212 |

Planar kernel, CPU, 1, 10 and 40 GHz. The two agree exactly at every point, because the band's height is taken into the oxide next to it,
which has the same permittivity.

`results/`, written by any run here, is git-ignored.
