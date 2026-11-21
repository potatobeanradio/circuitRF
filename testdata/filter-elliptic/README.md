# Elliptic and inverse-Chebyshev LC ladder references

The externally generated references for the Filter Designer's ladders with finite transmission
zeros (`docs/sonnet-briefs/brief-filter-designer-elliptic-ladder.md`, gate G1). The C# is
`src/Core/Systems/FilterLadder.Zeros.cs`, `FilterRoots.cs` and `EllipticPrototype.cs`; the test that
reads this file is `tests/Core.Tests/Devices/FilterEllipticReferenceTests.cs`. Produced 2026-11-21.

**Nothing in this directory reads, imports or calls circuitRF**, and nothing in `src/` or `tests/`
imports the script; the test reads only `elliptic-ladders.csv`.

| File | What it is |
|---|---|
| `reference.py` | A second implementation by a different route, in 120-digit arithmetic: mpmath's own theta and Jacobi functions for the modulus and the zeros; the poles from a polynomial root finder (not the closed-form complex-argument formula the C# uses); classical Darlington zero shifting on polynomials with exact division by `s² + ω²` (not the point evaluation the C# uses). Every ladder it writes is asserted to reproduce its own prototype's \|S21\|² to 1e-40 by an ABCD chain. |
| `elliptic-ladders.csv` | One row per specification, normalised to 1 rad/s and a 1 Ω source: family, order, ripple, floor, type, first element, load ratio, the zero order used, and the elements in ladder order (a tank or a stub is its inductor then its capacitor). `none` is a specification for which no order of the zeros gives every element positive. |

The grid: elliptic orders 3–21 at ripple 0.01 / 0.1 / 0.5 / 1 dB and floor 20 / 40 / 60 / 80 dB,
type a at odd order and types b and c at even order, both first elements; inverse Chebyshev at odd
orders 3–21 and the same four floors. A mask whose stop edge sits within 0.1 % of the pass edge is
skipped — its zeros cannot be told apart in double precision, and it is not a filter anyone builds.

No published element table was consulted: the check is the independent route above plus the 1e-40
self-check, which needs no table.

## Versions

| | |
|---|---|
| Python | 3.14.4 |
| mpmath | 1.4.1 (BSD-3-Clause) |

```
python3 -m venv v && v/bin/pip install mpmath
v/bin/python -I reference.py > elliptic-ladders.csv      # about a minute and a half
```
