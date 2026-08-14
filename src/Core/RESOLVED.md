# src/Core — resolved briefs (detail, off the CLAUDE.md growth path)

`src/Core/CLAUDE.md` was archived to `src/Core/HISTORY.md` once already (see that file's own note).
Going forward, a completed brief's detail lands here instead — one `##` section per brief, sparingly,
only for findings that are still true, still surprising, and would cost someone real time to
rediscover. Mirrors `src/Ui/DataDisplay/RESOLVED.md`'s own pattern.

## The SDD evaluator: what `dut.Evaluate` actually costs, and what closing the gap bought (brief-harmonicarf-r3b §1, 2026-08-13)

**The measurement that started this:** on the shipped default harmonicaRF document (Hero 2's GaN
HEMT as an SDD, K = 3, no package), `dut.Evaluate` cost ~9–13 µs and was reported as "100% expression
evaluation, nothing else in it" — `SddEvaluator.EvalDual` tree-walked the parsed AST against a
**freshly-built `Dictionary<string,Dual>` on every single call**, with `"_v{i+1}"` string
interpolation and a string-hashed lookup at every `RefExpr`, even for a one-node expression.

**Fix, in two measured steps, `SddEvaluator.EvalDual`/`EvalDouble` left completely UNTOUCHED as the
reference implementation:**

1. **`CompiledSddExpr`/`SddCompiler` (new, `src/Core/Expressions/SddCompiled.cs` +
   `CompiledSddExpr.cs`)** — each `RefExpr` is resolved to an integer SLOT once, when `SddModel`
   compiles its equations in its own constructor (alongside where it already caches the parsed AST),
   not on every `Evaluate` call. A parameter's `Dual` (zero gradient, correct width) is built ONCE per
   model and reused — it never changes across calls, so building it fresh every time (as the
   dictionary path did) was pure waste. Evaluation touches no `Dictionary`, hashes no string,
   interpolates nothing. **Bit-identical by construction, verified by a corpus test**
   (`SddCompiledBitIdenticalTests`, 123 cases across the shipped default's own two equations, every
   equation in `testdata/`, and hand-written ones exercising `^`, conditionals, every function, and
   the `ExpCap`/`LogFloor` clamp paths — asserts exact `double` equality, not a tolerance) that must
   stay green; it is the gate against a "faster but slightly different" evaluator, which would move
   every SDD-based hero golden at once.
2. **`in T` parameters on `IAdScalar<T>`** (`Add`/`Sub`/`Mul`/`Div`/`Pow`/`Neg`/the function table) —
   `Dual` is a ~144-byte struct carrying a fixed 16-wide gradient regardless of the actual port+control
   count (2, here), so a by-value binary op was copying ~288 bytes of INPUT on every `Add`/`Mul`/etc.
   Read-only-reference parameters cut that; the RESULT is still returned by value (there is no
   caller-owned slot to write into without restructuring the whole evaluator around `Span<double>`
   buffers — out of this step's scope; see the open item below).

**Measured, shipped-default fixture, both steps together:** `dut.Evaluate` **13.4 → 9.5 µs (~29%
faster)**. Every hero golden (Engine.Tests, 1177 tests + the 6-project full-solution run) still
passes — nothing moved.

**A correction worth keeping so nobody re-derives a frame budget from the wrong number.** An earlier
draft of the brief this work came from quoted "~2 ms per solve" from `HarmonicaGridDragCostTests`.
That figure is real but belongs to a DIFFERENT fixture — K = 5 **with an Rd/Rs/Ls package**, a
materially bigger circuit. On the shipped default (K = 3, no package) a warm solve is **0.69–0.86 ms
before this work, 0.36–0.37 ms after** — nowhere near 2 ms either way. The solve COUNT in a grid was
never the bottleneck; the per-solve evaluator cost was.

**The surprising part, worth keeping so nobody re-derives it:** the compiled node-walk ITSELF,
isolated from the per-call setup it replaces, was measured to be *slower* than the dictionary-based
reference on the shipped default's 80-node drain equation (~10.5 µs compiled vs ~8.9 µs reference,
before the `in`-parameter step; the gap narrowed to ~13% after it, but did not close). The equation
has only two names to resolve ("_v1"/"_v2" — the shipped default's coefficients are literal, not
named parameters), so the dictionary it replaces was already tiny and cheap to query; nearly all of
the ~105 ns/node cost the brief measured is genuinely **per-node walk cost** (dispatch + the struct
traffic of a functional, expression-tree-shaped evaluator), not lookup cost — and killing the
dictionary does not touch it. This is exactly what the brief's own step 3 (compile to a flat
instruction array over `Span<double>`, i.e. leave the tree-walk-of-boxed-nodes shape entirely) is
for. **Reordering the compiled dispatch from a type-pattern switch (a chain of `isinst` checks) to an
explicit integer-discriminant switch was tried and measured to NOT close this gap** — the per-node
cost is structural to the tree-walk shape, not an artifact of how one particular walk dispatches.

**Step 3 — built after the above was reported and confirmed with the owner.**
`SddRegisterCompiler`/`RInstr` (`SddRegisterProgram.cs`) flatten the whole equation into a linear
three-address-code array at compile time — no boxed node objects, no recursion, one register per
instruction, walked by a single `for` loop with a `switch` on a byte opcode. Register space is one
flat `Dual[]`: indices `[0, totalSlots)` are the input slots (exactly as steps 1–2 already laid out),
index `totalSlots + i` is instruction `i`'s own output. A bare name/leaf reference costs NO
instruction at all (it is just a slot index), which is why the program is shorter than the
equivalent node tree. **This finally lands the win the per-node-cost finding above said step 3 would
need to reach**, because it genuinely leaves the "one struct-returning virtual call per node" shape
behind rather than reorganising it.

**Scope, deliberately not fully general — and why that is fine.** The compiler handles every
expression EXCEPT a conditional (`if(...)`/ternary); `SddRegisterCompiler.ContainsConditional` scans
the AST once at compile time and routes an equation containing one to step 1's node-tree walk instead
(still correct, still faster than the original dictionary path). A correct, jump-based bytecode VM
with short-circuit `&&`/`||` is a real undertaking with its own correctness surface, and **no SDD
equation in this repository — the shipped default, or anything in `testdata/` — contains a
conditional**; building branch-handling for a construct with zero measured real-world use was judged
not worth the risk. If a future SDD equation genuinely needs one, it still works correctly (via the
fallback), just without step 3's speedup.

**Measured, shipped-default fixture (steps 1–3 together, against the ORIGINAL pre-brief baseline):**

| quantity | before | after | factor |
|---|---|---|---|
| `dut.Evaluate` | 13.4 µs | 4.5 µs | **3.0×** |
| `EvalDual` big (drain eqn), reference dict path | 8.9–11.9 µs | — (superseded) | — |
| `CompiledSddExpr.EvalDual` big, register machine | — | 3.0–3.1 µs | **~2.9×** vs reference |
| warm `ctx.Solve` | 0.69–0.86 ms | 0.36–0.37 ms | **~2×** |
| cold `ctx.Solve` | 0.85–1.06 ms | 0.55–0.57 ms | **~1.7×** |
| `PinSearch.Sweep` (46 solves, tier-A ladder) | 35.7–43.0 ms | 18.3–18.8 ms | **~2×** |

Bit-identical corpus (123 cases, both the register path and the conditional fallback exercised) and
the full solution (Core/Engine/Harmonica/Ui/RfCore/WBond/Firewall) all still pass — nothing moved.
`SddEvaluator.EvalDual`/`EvalDouble` remain completely untouched as the reference implementation.

**What this means for the frame-rate target:** tier-A's 46-solve sweep is now ~18.5 ms — well short
of the brief's own "step 3 optional if the sweep lands near 2–3 ms" bar, but a real 2× cut from the
evaluator alone. Whether an L1-drag frame hits >60 fps depends on the REST of the frame (render,
readout-strip rebuild, pool/dispatcher overhead) — see the harmonicaRF-side write-up for that
breakdown; it is a separate cost this brief's §1.4 measures independently.
