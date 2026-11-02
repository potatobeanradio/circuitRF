# Sonnet Brief — SP-P2: `MnaSystem` keeps its sparsity pattern across frequencies

**Design:** `docs/design/linear-engine.md` §3 (`MnaSystem` and the stamping API), §6 (sparse solve — the
AMD permutation computed once from the topology and reused), §11 (performance). **Code:** `src/Engine/MnaSystem.cs`
(`Accum`, `Reset`, `BuildCscMatrix`, `Factorize`, `FindZeroRows`/`FindZeroCols`, `BuildRhs`),
`src/Engine/SParameterEngine.cs` (`RunWavePath`, `RunLegacyPath`, `StampAll`),
`src/Engine/HarmonicBalance/HbLinearExtractor.cs` (`BuildCsc` at :302, :528, :576; `BuildRhs`),
`src/Engine/NonlinearDcEngine.cs:262-299` (`GetEntry`/`GetRhs` readback into a dense matrix).

**One sentence:** every frequency point rebuilds the matrix from a `Dictionary<(int,int),Complex>`
through a `CoordinateStorage` and a sort into CSC, then scans it twice for structurally zero rows
and columns — all of which is invariant across ω — so record the stamp sequence once, write values
straight into the CSC value array by slot afterwards, and do the structural checks once.

**Why (S-parameter engine performance review, 2026-08-30).** Release, M4, single thread, one
frequency point of a 2000-node RLC ladder (Size 4,003, nnz 12,004): stamp into the dictionary
0.35 ms · `BuildCscMatrix` 0.19 ms · `FindZeroRows`+`FindZeroCols` 0.095 ms · `SparseLU.Create`
0.31 ms · two port solves 0.19 ms; **3.8 MB allocated per point** (1.5 GB for a 401-point sweep).
A prototype of the pattern-cached assembly (an `IMnaContext` in a scratch harness, same models,
same `SparseLU`) measured on stamp + CSC + LU:

| ladder | dictionary path | pattern-cached | ratio |
|---|---|---|---|
| 20 nodes | 17.4 ms / 15.0 MB | 7.4 ms / 10.8 MB | **2.35×** |
| 200 nodes | 31.0 ms / 145 MB | 20.5 ms / 105 MB | **1.51×** |
| 2000 nodes | 315 ms / 1,429 MB | 194 ms / 1,042 MB | **1.62×** |

(401 points each; matrices compared entry-by-entry — max difference exactly 0.) The LU itself was
untouched in the prototype; the remaining allocation is `SparseLU.Create`'s own L/U buffers, which
is out of scope here (§7). Fewer allocations per point is also what SP-P3 needs: the GC is what
capped its parallel scaling.

**Structural facts.**

1. **The stamp SEQUENCE is what is invariant, not just the pattern.** `StampAll` visits
   `netlist.Components` in one fixed order, each model issues the same `Accum` calls in the same
   order, and `AddBranch` hands out the same indices — so call k of pass 2 hits the same (row, col)
   as call k of pass 1. That is what makes a slot map (`call index → CSC value index`) possible with
   no hashing at all.
2. **…except when it isn't.** `InductorModel.Stamp` skips its diagonal when `diag == Complex.Zero`
   (an ideal inductor at ω = 0 with no R), and takes a different branch entirely when it has a `C`
   and ω = 0; `MatchModel` has the same shape; a nonlinear device's `StampLinearized` can drop a
   zero conductance. An S-parameter grid can contain 0 Hz. So the cached pass MUST verify
   `(row, col)` at every call against the recorded sequence, and on the first mismatch invalidate
   the pattern, finish the pass in recording mode, and rebuild. The check is two int compares per
   call — the prototype ran with it on. A pattern that is silently wrong would put a value in the
   wrong cell and produce a plausible, wrong answer; verification is not optional.
3. **`FindZeroRows`/`FindZeroCols` answer a structural question** and allocate two `HashSet`s per
   call. Run them once when the pattern is (re)built. Their diagnostics (`nodeNamer`/`branchNamer`)
   are unchanged; only the frequency at which they run changes. A matrix that is structurally full
   at ω₁ is structurally full at ω₂ — unless the sequence changed, which is fact 2, which rebuilds.
4. **The value array is zeroed, not rebuilt, on `Reset`.** `Array.Clear(values)` on 12k complexes is
   ~µs. The RHS becomes a `Complex[Size]` cleared the same way (branch count is known after the
   first pass; grow if `AddBranch` exceeds it — that is also a mismatch).
5. **The dictionary stays as the recording representation** — or a `List<(int,int,Complex)>` in
   call order, which is what the prototype used and is simpler: the CSC build sorts by (col, row),
   merges duplicates into one cell, and writes `slot[callIndex]`. Both are fine; the list avoids
   the tuple hashing on the recording pass too.
6. **Other consumers see no change in behaviour.** `HbLinearExtractor` news an `MnaSystem` per
   `BuildMna` call today, so it gets the recording pass every time — no faster, no slower, same
   matrices; when HB-P2 keeps its extractor alive it will inherit the cache for free.
   `NonlinearDcEngine` reads `GetEntry`/`GetRhs` after one stamp — keep those working on both the
   recording and the cached representation (read through the slot map or the CSC). `BuildCsc()`
   must keep returning a matrix the caller can hold: return a COPY when the cached CSC is live,
   since the extractor snapshots `g0` and then factorizes (`MnaSystem.cs` doc comment on `BuildCsc`
   says both build from the same entries — that stays true in spirit; it must not become "both are
   the same object that the next `Reset` zeroes").
7. **`Factorize` reuses `_amdPerm` already.** Nothing changes there, except that on a pattern
   rebuild whose nnz structure differs, the permutation must be recomputed too (`_amdPerm = null`
   on invalidate). Today a changed pattern with a stale permutation would still factorize —
   AMD is a heuristic, any permutation is valid — but it should be recomputed for the same reason
   the pattern is.

---

## 1. M1 — recording pass + slot map

`MnaSystem` gains a `_pattern` (rows, cols, slot map, CSC) and a `_k` call counter. `Accum` becomes:
if a valid pattern exists and `(_rows[_k], _cols[_k]) == (row, col)`, `_csc.Values[_slot[_k++]] += v`;
else if a pattern exists, invalidate (fact 2) and fall through; else record. `Reset` zeroes values
(and the RHS array) when a pattern is valid, or clears the recording lists when not. `Factorize`
calls `EnsurePattern()` (builds CSC + slot map, runs the zero-row/col checks, computes AMD) and then
`SparseLU.Create(_csc, _amdPerm, tol)`. The end-of-pass check that the pass used exactly
`_rows.Count` calls (a pass that stopped SHORT is also a mismatch) lives in `EnsurePattern`.

## 2. M2 — the S-parameter engine stops doing its own per-point structural work

`SParameterEngine` needs no change to benefit; read `RunWavePath`/`RunLegacyPath` to confirm the
retry path (re-stamp with regularization after a `SingularMatrixException`) still works: the retry
adds gmin stamps that were not in the recorded sequence → mismatch → rebuild → correct. Add a test
for exactly that (§4). The per-port `new Complex[mna.Size]` RHS in `RunWavePath` can become one
reused buffer cleared per port — small, do it while you are there.

## 3. Tests

- `Engine.Tests`: stamp a fixture at ω₁, `Factorize`, stamp at ω₂, `Factorize`; compare
  `BuildCsc()` at ω₂ against a FRESH `MnaSystem` stamped at ω₂ — identical structure and values,
  `Assert.Equal` per entry. Fixtures: the Hero 1 netlist (SnP branches + mutual-free), one with a
  `K:` mutual inductance (the two-phase stamp order), one with a nonlinear device (the
  `StampLinearized` path), one legacy-path netlist (a port with reactive Z0).
- Mismatch: a netlist with an ideal inductor swept over `[0, f1, f2]` — the ω = 0 point's sequence
  differs — asserts every point equals a fresh-`MnaSystem` solve, and that the engine produced no
  exception and no wrong `S`. Then the reverse order `[f1, 0, f2]`.
- Regularization retry: a floating-node netlist under `IfNecessary` produces the same `S` and the
  same warning as before this change.
- `BuildCsc()` snapshot survives a subsequent `Reset` + restamp (fact 6).
- All existing `Linear/`, `HarmonicBalance/`, `Nonlinear/` tests unchanged and green — the HB
  extractor and the DC engine are the consumers most likely to notice a representation change.

## 4. Gates

`dotnet test tests/Engine.Tests` green (run once; TRX for failures). Scratch-harness measurement
before/after on 20/200/2000-node ladders and on Hero 1 — report per-point time and allocation; the
target is the prototype's numbers above or better, and allocation per point must fall (if it does
not, `BuildCsc` is probably copying on every call rather than only when a caller asks for it).

## 5. On completion

Findings — before/after table, which fixtures triggered a pattern rebuild and why, and whether any
model turned out to stamp in a value-dependent order that fact 2 did not anticipate — to
**`src/Engine/RESOLVED.md` §SP-P2**. **Never to any `CLAUDE.md`.** Do not commit; the owner
commits.

## 6. Out of scope, deliberately

Replacing `SparseLU.Create` with a fixed-pattern refactorization (it allocates fresh L/U per point,
~2.6 MB at 2000 nodes; the fix is a sparse LU of our own, a separate brief if ever). A dense path
for tiny matrices (measured < 10 % overall). Any change to what the models stamp.
