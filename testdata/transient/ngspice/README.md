# Native transient engine against ngspice (brief TRN-7 R-trn7-9)

External references for circuitRF's own transient engine, produced by ngspice, not by circuitRF's solver.

| Bench | What it gates | Grid |
|---|---|---|
| `rc.cnl` | an RC step (TRN-1's analytic gate) | 5 µs at 5 ns |
| `rlc.cnl` | a series RLC rung by a step | 2 µs at 1 ns |
| `rectifier.cnl` | a half-wave diode rectifier | 10 µs at 5 ns |
| `buck.cnl` | TRN-3's buck converter, its first 20 cycles | 20 µs at 10 ns |
| `line.cnl` | a lossless line's pulse reflection (TRN-4) | 4 ns at 10 ps |
| `ring.cnl` | a five-stage ring of MOS level-1 inverters, started from zero | 5 ns at 5 ps |

For each bench, `<name>.cir` is the deck circuitRF's translation wrote (`circuitrf netlist <name>.cnl -o <name>.cir`) with one
control block added before `.end`: `run`, `linearize`, `write <name>.raw`. `linearize` is ngspice putting its own solution on the
bench's output grid by its own interpolation, so none of circuitRF's resampling is in the reference. `<name>.csv` is that grid,
read from the rawfile by `generate.py`'s own reader: `time`, then each saved net by its circuitRF name, 10 significant digits.

`ngspice-version.txt` is the ngspice that ran them (Homebrew's build on macOS arm64). To regenerate, from the repository root:

```
dotnet build src/Cli
python3 -I testdata/transient/ngspice/generate.py [path/to/CircuitRF.Cli.dll] [path/to/ngspice]
```

The tests are `tests/Engine.Tests/Transient/NgspiceReferenceTests.cs`; they need no ngspice. Each states its bound and why
the two engines differ at all. A bench started from zero skips its first point: circuitRF reports the zero state there, and
ngspice's first point under `uic` already has the sources applied.

`.options INTERP` would have been the simpler way to get the grid, but ngspice 47 writes a damaged rawfile with it: its header
is repeated inside the data and its point count is one short.
