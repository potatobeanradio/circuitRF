# DE-1 — The network inverse and the TRL solver (`src/RfCore`)

Read `brief-de-0-overview.md` first.

**What.** Two pure functions in `src/RfCore`, beside `RFNetwork`, with no circuit, schematic or file I/O in them:

1. `NetworkInverse.Invert(S, k)`: the S-matrix of `F⁻¹` for a 2K-port `F`, per frequency.
2. `TrlCalibration.Solve(thru, reflect, line, reflectKind)`: fixture A and fixture B as `SNP`s, plus diagnostics, from
   three 2-port measurements on one frequency grid.

Everything later in the series calls these. Nothing else re-derives the algebra.

## 1. The inverse

Group a 2K-port's ports as side 1 (ports 1…K) and side 2 (ports K+1…2K), and write its S in K×K blocks
`S11, S12, S21, S22`. With `a`/`b` the incident and reflected waves, define the cascade matrix by

```
[b1]       [a2]
[a1] = T · [b2]
```

so that a chain multiplies: `T(X then Y) = T_X · T_Y`, where X's side 2 connects to Y's side 1.

```
S → T:   T = [ S12 − S11·S21⁻¹·S22    S11·S21⁻¹ ]
             [ −S21⁻¹·S22             S21⁻¹     ]

T → S:   S = [ T12·T22⁻¹    T11 − T12·T22⁻¹·T21 ]
             [ T22⁻¹        −T22⁻¹·T21          ]
```

`Invert` is `T → S` of `(S → T)⁻¹`. Both directions of cascading then give a through connection (`D·F = F·D = I`), which
is D1. All ports share the file's single reference impedance. A file whose ports state different references
(Touchstone 2) is renormalised to one first, or refused. Pick the first if `RFNetwork` already renormalises, otherwise
refuse with a sentence.

**Refusal.** `S21` must be invertible, meaning every outer port reaches the inner side. Use the condition number, not an exact
zero: refuse above a threshold you choose and document (1e12 is a reasonable start). The refusal is a typed result that
carries the frequency, so the model can name the instance (DE-2). A fixture with a series DC block reaches this at 0 Hz.

**Prefer a direct solve to explicit inverses** where it is just as short. `T22⁻¹` and `S21⁻¹` only ever multiply something.

## 2. TRL

Inputs: three `SNP`s on **one** frequency grid (refuse otherwise, naming the file whose grid differs and the first point
that differs), plus `reflectKind ∈ {Open, Short}`.

Write the measured cascade matrices `Mt` (Thru) and `Ml` (Line), and the reflect measurements `Γ1 = Reflect.S11`,
`Γ2 = Reflect.S22`. The model is `Mt = A·B` and `Ml = A·L·B`, with `L = diag(e^(−γℓ), e^(+γℓ))` and ℓ the Line's extra length over
the Thru.

At each frequency:

1. `P = Ml·Mt⁻¹ = A·L·A⁻¹`. Its eigenvalues are `e^(∓γℓ)`, and the eigenvector for `e^(−γℓ)` is A's first column,
   the other its second. From the ratios: `a = S11_A` (second column, top/bottom) and `b = Δ_A / S22_A` (first column).
2. `Q = Mt⁻¹·Ml = B⁻¹·L·B`. Its **left** eigenvectors are B's rows. From the ratios: `α = S22_B` (row for `e^(+γℓ)`, `−first/second`)
   and `β = Δ_B / S11_B` (row for `e^(−γℓ)`, `−first/second`). Pair each row with the eigenvalue matched to step 1's, by
   nearness, never by sort order.
3. Thru: `c·γ = (Mt.S11 − a) / (Mt.S11 − b)`, with `c = S22_A` and `γ = S11_B`.
4. Reflect, the same unknown Γ on both sides: `c·Γ = (Γ1 − a)/(Γ1 − b)` and `γ·Γ = (Γ2 − α)/(Γ2 − β)`. Divide them to get
   `c/γ`. Then `c = ±√((c·γ)·(c/γ))`, choosing the sign that puts `Γ = (c·Γ)/c` on the side `reflectKind` states
   (Re Γ > 0 for Open, < 0 for Short). Then `γ = (c·γ)/c`.
5. Transmission, with the fixtures assumed reciprocal: `S21_A = S12_A = √(c·(a − b))`. Then
   `S21_B = Mt.S21·(1 − c·γ)/S21_A` and `S12_B = γ·(α − β)/S21_B`.
6. `A = [[a, S21_A], [S21_A, c]]` and `B = [[γ, S12_B], [S21_B, α]]`.

**Continuity across frequency (required, per DE-0's measurements):**

- **Eigenvalue assignment.** At the lowest frequency, `e^(−γℓ)` is the eigenvalue with the negative imaginary part, which is
  right while the line is under 180°. After that, take the eigenvalue nearest the previous point's, extrapolated in phase
  from the two before it. This carries the assignment through 180°, 360° and beyond. The prototype holds to 1e-15 at 333°.
- **The S21_A root.** At the first point take the root with Re ≥ 0. After that, take whichever of ±√ is nearer the previous
  point's. Without this a fixture file interpolates through zero between two samples.
- The DUT does not depend on either sign. Write that in a comment, because it is the obvious thing to "simplify" away.

**Diagnostics returned with the fixtures, per point:**

- `LineDegrees`: the electrical length of Line minus Thru, unwrapped, from the tracked eigenvalue.
- `Gamma`: the solved reflect.
- `Usable`: whether `LineDegrees mod 180` lies in [20°, 160°], the interval in which the line separates the two eigenvalues
  well enough. Also the count of points outside it, for DE-4 to warn with.

**State in the doc comment, because a user will ask:**

- The reference plane is the middle of the Thru. A non-zero Thru moves it there, and `ℓ` is then Line minus Thru.
- The de-embedded S is referenced to the **Line's characteristic impedance**, not the instrument's 50 Ω. That is a property
  of TRL, not a defect.
- The fixtures are assumed reciprocal. Only the products `S21_A·S12_A` and `S21_B·S12_B` are determined, and the reciprocal
  split is what makes A and B individual files.
- The Reflect only needs to be the same on both ports and roughly known (open or short). Its value comes out of the solve.

## Tests (`tests/RfCore.Tests`, one class each)

- `NetworkInverseTests`: K = 1, 2, 3 random complex networks, `D·F` and `F·D` against a through connection ≤ 1e-12, plus
  the refusal on a network with an open outer port.
- `TrlCalibrationTests`: synthetic fixtures built in the test, as the DE-0 prototype built them. Two different lossy,
  mismatched launches (a series L, a shunt C and a mismatched line), a lossy line standard, and a short of −0.98 with a 3 ps
  offset, 0.5-20 GHz. Assert A, B and a DUT through `Invert` to ≤ 1e-10, across the 180° crossing. Then the same data with
  the continuity rules off, to show they are the reason. Then a grid mismatch refused by name. Then an open
  `reflectKind` on short data, which must give the wrong-sign Γ and not crash.

No circuit runs in this brief.
