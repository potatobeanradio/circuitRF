# Brief MC-29 — Guided extrapolation: a gate current's added conductance follows the edge it leaves

**Series:** `brief-modelcomposer-0-overview.md` · **Tag:** `R-mc29-<m>` · **Depends on:** MC-2 (R-mc2-3), MC-7, MC-26
**Area:** `src/Core/Devices/Composed/GuidedExtrapolation.cs`, `ComposedModel.cs` (`ModelExtrapolation`), `CmodelFile.cs`
(read/validate), `src/Design/Modeling/CurrentFit.cs` (`Gx`), `CmodelExport.cs`; the Verilog-A writer's continuation
(MC-26: `CmodelVerilogA`); `docs/design/model-composer.md` §(guided extrapolation) and its open-item note "The
extrapolation conductance near a data edge"; `docs/user/src/reference/model-composer.md`
**Read first:** MC-2 R-mc2-3 (the continuation and its required properties); `GuidedExtrapolation.Evaluate` (the exact
gradient through the projection); MC-26's continuation section (the `.va` emits the same formula)

---

## 0. The gap, measured

Outside a table or ANN current's training domain the continuation is

```
f(p) = f(p_b) + ∇f(p_b)·(p − p_b) + G_x · s · d / d₀        s = own·(p − p_b),  d = |u − u_b|,  d₀ = 0.05
```

with one `G_x` per current, **ten times the largest own-branch conductance anywhere in the training data**
(`CurrentFit.Gx`). For a gate current the largest is the forward diode's, so the same tens of siemens that guide a
forward overdrive back toward the data also apply when `vgs` leaves the data on the REVERSE side, where the measured
current is zero.

Found on the Pulsed Radar example's device (`examples/Pulsed Radar/radar-gan`, a 12 mm GaN at 48 V, 3 GHz):

- With its I–V grids stopping at Vgs = −6 V, `Extrapolation.Gx.IgGs` was **55 S**. At Vgs = −7 V (1 V past the data)
  the model drew **~1 A of reverse gate current** through a 1 kΩ gate feed, holding the gate at −6 V. The truth (the
  SDD that made the data) draws nothing.
- Under drive the intrinsic gate swings to −8 V at the 34 dBm design drive. From Pin 30.5 dBm, where the swing first
  passes −6 V, the clamp rectified the gate bias upward: the model's gain expanded early and it saturated about 1 dB of
  drive before the truth (traps off, isothermal: within 0.02 dB to 30.25 dBm, then 0.2–1.4 dB high to 34 dBm). Its
  pulse droop read 0.06 dB where the truth's is ~0.4 dB, because a saturated stage does not droop.
- The example was fixed by extending the data to −10 V, past the swing (ENV-PCW follow-up). That is the right data, but
  a user's measured data will often stop a few volts short of an RF swing (a DC instrument cannot reach it, and a pulsed
  set is taken on a fixed grid), and the model should degrade gently there, not clamp.

Drain current is not affected in the same way: its own branch is `vds`, and a `vgs` excursion past the data moves
`p − p_b` along `vgs` only, so `s = 0`. `IgGd` is affected along `vgs − vds` exactly as `IgGs` is along `vgs`.

## 1. Requirements

**R-mc29-1 — The added conductance is local to the edge.** Replace the global `G_x` in the added term with one that
follows the constituent's own-branch conductance at the boundary point:

```
G_loc(p_b) = max( K · |g_own(p_b)| , G_floor )        g_own = ∇f(p_b)·own
f(p) = f(p_b) + ∇f(p_b)·(p − p_b) + G_loc(p_b) · s · d / d₀
```

- `K` = 10 (MC-2's factor, now applied locally). `G_floor` is the conductance below which the guide is no longer
  needed for Newton's sake — propose 1e-6 × the largest own-branch conductance (state the value chosen and why).
- At the reverse-gate edge `g_own ≈ 0`, so the continuation is the linear one: ~0 current, as the data says. At the
  forward edge it is what it is today.
- **The gradient stays exact.** `G_loc` moves with `p_b`, so its gradient adds `s·d/d₀ · K·sign(g_own)·Pᵀ H own`
  (the Hessian along `own`, which the evaluators can already return: one more `Evaluate(pb, g, own, h)` per outside
  sample). The `max` with the floor gives a gradient jump where `K|g_own| = G_floor`; it lies outside the domain, so it
  is allowed by R-mc2-3, but smooth it (a soft-max) if the FD gate shows Newton cares.
- **Inside the domain nothing changes**, bit for bit.

**R-mc29-2 — The file says which law it carries.** Decide (D1) between:
- (a, recommended) a new field `Extrapolation.Mode: "Local"` with `Gx` kept as the cap, `G_loc = min(G_loc, Gx)` so a
  forward runaway is bounded exactly as before; a file without `Mode` reads as today's global law, so every shipped
  `.cmodel` evaluates unchanged until it is re-exported; or
- (b) reinterpret `Gx` as `K` and bump the format version.
The reader refuses an unknown `Mode` by name, as it refuses any unknown field.

**R-mc29-3 — Verilog-A.** MC-26's writer emits the continuation; it emits the local law for a `Mode: "Local"` model,
and the MC-28 acceptance (`.cmodel` vs `.va` on one bench: DC, S, HB) passes on a model exported with it.

**R-mc29-4 — The composer reports it.** The Current step's extrapolation line names the law and, per current, the
conductance at the lowest and highest `vgs` edge (`IgGs: 2e-9 S at −6 V, 4.9 S at +1 V`), so a user sees which edge
the data stops short at.

## 2. Gates

1. **The reverse edge is quiet.** The radar device refitted from its ORIGINAL grids (Vgs −6 … +1 V; the `GanTruth`
   with `DcVgs`/`PulsedVgs` truncated in the test, not in the example): DC gate current at Vgs −7, −8, −9 V and Vds 50,
   96 V within 1 mA of the truth (today ~1 A). One test.
2. **The swing it causes is gone.** On that model, the `PulsedPA` bench (traps off, isothermal at 70 °C, HB, Pin 29 …
   34 dBm): Pout within 0.2 dB of the truth at every point. This is the failure §0 measured; it is the brief's
   acceptance.
3. **The forward edge is unchanged.** Pout at Pin 34.5 … 36 dBm (gate forward-driven past +1 V) within 0.05 dB of the
   global law, and convergence unchanged on that bench and on the Model Composer example's benches.
4. **Exact gradient.** The existing FD-oracle gradient test of `GuidedExtrapolation`, extended to the local law, on both
   edges and across a polygon vertex's normal cone: ≤ 1e-6 relative.
5. **Nothing inside moves.** Every shipped `.cmodel` without `Mode` evaluates bit-identically (the existing example
   tests unchanged).
6. **Verilog-A.** The MC-28 acceptance on a `Mode: "Local"` export.

Run only the composed-device classes in `Core.Tests`, the composer classes in `Ui.Tests` this touches, and the Verilog-A
acceptance, once; read the TRX.

## 3. Decisions for the owner

- **D1** the format: (a) `Mode: "Local"` with `Gx` as a cap (recommended: old files unchanged, the cap keeps a forward
  runaway bounded), or (b) reinterpret `Gx` and bump the version.
- **D2** re-export the shipped examples (`examples/Model Composer`'s three documents and `examples/Pulsed Radar`'s
  device) with the local law, or leave them on the global one. Their data already spans their benches' swings, so their
  results should not move; re-exporting shows the new law in the examples users copy (recommended).
- **D3** make the local law the composer's default for new exports (recommended), or opt-in per document.

## 4. Out of scope

- Extrapolation of charges (linear continuation only, unchanged) and of equation constituents (global by construction).
- Choosing data grids for the user. The composer already says where its data stops; R-mc29-4 makes the edge visible.
