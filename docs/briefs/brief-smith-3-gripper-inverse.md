# Brief 3 — the gripper inverse: drag a point, get a component value

**Series:** [Smith Chart](brief-smith-0-overview.md) · **Tag:** `R-smith3-n` · **Phase:** P1
**Area:** `src/Design/Smith/` · **Depends on:** 2 · **Blocks:** 5
**Design note:** [`smith-chart.md`](../design/smith-chart.md) §4.3

---

## 0. What this brief delivers

`SmithInverse` — pure arithmetic, closed form in every case, framework-free. Perhaps 200 lines. No
drag loop, no pointer events, no undo: this brief answers *"the user let go here; what value is that?"*
and nothing else.

---

## 1. `R-smith3-1` — one free scalar, and the answer is a projection

A gripper changes **exactly one parameter** of one element — its `ActiveParameter`. The reachable set of a
single free parameter is a known locus, so the answer is the projection of the drag point onto it, and
every case below is closed form.

```csharp
public static InverseResult Solve(
    SmithDesign d, int elementIndex, SmithParameter p,
    Complex zIn, Complex gammaDrag, double fHz, double z0Chart);

// InverseResult: Value, Pinned (bool), PinReason (string?)
```

**Only the component of the drag the parameter can reach is used; the perpendicular component is
discarded.** That is not an approximation — it is what "drag along the arc" means, and it is why the
gripper follows the curve rather than the cursor.

Writing `Z_d = z0Chart·(1+Γ_d)/(1−Γ_d)`, `Y_d = 1/Z_d`, `Y_in = 1/Z_in`:

```
series R        R     = Re(Z_d) − Re(Z_in)
series L        L     = (Im(Z_d) − Im(Z_in)) / ω
series C        C     = −1 / (ω·(Im(Z_d) − Im(Z_in)))
shunt  R        1/R   = Re(Y_d) − Re(Y_in)
shunt  L        L     = −1 / (ω·(Im(Y_d) − Im(Y_in)))
shunt  C        C     = (Im(Y_d) − Im(Y_in)) / ω
SRLC on L       L     = (X_target + 1/(ωC)) / ω        X_target = Im(Z_d) − Im(Z_in)
SRLC on C       C     = 1 / (ω·(ωL − X_target))
SRLC on R       R     = Re(Z_d) − Re(Z_in)
PRLC            the duals of the three above, in Y
Z1P on Im       Im(Z) = Im(Z_d) − Im(Z_in)             (series; the Y dual for shunt)
TLIN on E       project Γ_d onto the rotation circle, read the angle,
                E = 180·θ/π · F_ref/f
stub on E       θ = atan(Z₀ₗ·B_req)        (open)
                θ = atan(−1/(Z₀ₗ·B_req))   (shorted)
```

`S1P` and `S2P` have no parameter and no gripper. `Solve` on one is a programming error, not a runtime
refusal — assert.

### `R-smith3-2` — the TLIN's Z₀ inverse is the one that is not a one-liner

Requiring `Z_out = Z_d` in the line equation with Z₀ₗ as the unknown:

```
j·tanθ·Z₀ₗ²  +  (Z_k − Z_d)·Z₀ₗ  −  j·tanθ·Z_k·Z_d  =  0
```

a complex quadratic. Compute both roots directly; take the one with **positive real part nearest the
current value**. When neither is physical, pin and say so.

**At `tanθ = 0` the equation degenerates** — a zero-length line transforms nothing — and the drag is
**inert**, which is correct. Return the current value with `Pinned = true` and a reason naming the zero
length, rather than dividing by it.

### `R-smith3-3` — the stub's branch must be unwrapped, not wrapped

`atan` returns a principal value, so a naive stub inverse **jumps a half-turn** the moment the drag crosses
a quarter wave — the gripper leaps to the far side of the chart under a hand that moved two pixels.

Unwrap into the branch the element's **current** `E` is in: add `k·180°` with `k` chosen to minimise the
change from the current electrical length. This is the same continuity problem the trajectory's pole
crossing has (brief 2 `R-smith2-8`), seen from the inverse side, and it is the single most likely defect
in this brief.

### `R-smith3-4` — physicality is enforced at the pin, and reported

L, C and R are non-negative; a TLIN's Z₀ is positive; an electrical length is non-negative. A drag
demanding otherwise **pins at the boundary** and returns `Pinned = true` with a reason naming the
parameter and the limit.

**It does not stop tracking.** Brief 5 keeps the gripper under the cursor at the pin — a handle that stops
moving reads as a broken drag, where a handle that moves along the boundary reads as a limit. And it never
silently produces a negative inductance, which is the outcome this rule exists to prevent.

`NaN`, `±∞` and a Γ_d exactly at 1 (where `Z_d` is infinite) all return the current value pinned, with a
reason. **No case returns `NaN`.**

---

## 2. The gate

`tests/Ui.Tests/Smith/SmithInverseTests.cs` — one test per claim.

1. **Round-trip exactness, over the whole table.** For each parameter in `R-smith3-1`: pick a reachable
   Γ_d, solve, write the value back, re-evaluate through `SmithCascade`, and land on Γ_d to machine
   precision. One parameterised test; the claim is "the inverse inverts", and it is one claim.
   **This is the gate** — it is the only one that cannot pass while a formula is wrong.
2. **The TLIN Z₀ quadratic** picks the physical root, and the degenerate `tanθ = 0` case is inert and
   pinned rather than NaN.
3. **A stub drag across a quarter wave is continuous**: walk Γ_d along the constant-G circle in small
   steps through the pole and assert the returned E changes monotonically and by small increments — no
   half-turn jump (`R-smith3-3`).
4. **Pinning**: a drag demanding a negative L pins at zero, reports `Pinned`, and the reason names `L`.
   Same for R, C, Z₀ and E. One test, a table of cases.
5. **Nothing returns NaN**, for Γ_d at 0, at ±1, at ±j, outside the unit circle, and for a degenerate
   Z_in.

---

## 3. What this brief must NOT do

- **No pointer handling, no drag state, no undo entry.** Brief 5 owns the gesture; this owns the answer.
- **No choosing WHICH parameter is active.** That is `SmithElement.ActiveParameter`, set by brief 6 when a
  slider is touched. This brief is told.
- **No writing to the design.** `Solve` returns a value. The caller decides whether to commit it.
- **No constant-Q inverse.** That is brief 9, with its own arithmetic; it is not a gripper.

---

## 4. On completion

Findings to `src/Design/RESOLVED.md`. **Never a `CLAUDE.md`.**
