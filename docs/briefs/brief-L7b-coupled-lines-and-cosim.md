# Sonnet Brief — Phase L7b: coupled lines + co-simulation

**Design:** `docs/design/layout-view.md` §10 — §10.3 (the kernel), §10.6 (ports), §10.8 (results +
`.snp` co-simulation). Phase table row **L7b**, whose own words are: *"Multiconductor [L][C], modal
decomposition, coupled-line s-parameters, `.snp` back-annotation into the schematic"*, gated on
*"Even/odd-mode oracle for coupled microstrip; an EM-derived `.snp` drops into an HB testbench and
runs."*

**Read `src/Engine/Mom/CLAUDE.md` and `src/Ui/Layout/Em/CLAUDE.md` first.** Both halves of L6/L7 are
complete, validated and documented there. This brief is an **addition to kernel A, not a rewrite** —
`[C]`, `[C₀]` and `[L]` have been full N×N matrices since the engine half landed, and
`RlgcToSparams`' own header already says so.

Gate command is plain `dotnet test`.

---

## 0. Read this before planning anything: the one thing that is NOT available

**NumFlat cannot decompose `[Z][Y]`.** Its complex eigensolver is Hermitian-only. From NumFlat
1.3.0's own XML documentation for `EigenValueDecompositionComplex`:

> *"The matrix to be decomposed must be symmetric positive definite. Note that this implementation
> does not verify whether the input matrix is symmetric. Specifically, only the upper triangular part
> of the input matrix is used, and the rest is ignored."*

and its eigenvalues come back as `Vec<double>` — **real**. For a lossy multiconductor line
`[Z][Y] = (R + jωL)(G + jωC)` is a general non-Hermitian complex matrix whose eigenvalues (γ²) are
genuinely complex. Handing it to `MatrixDecompositions.Evd` would silently read the upper triangle
of a matrix that is not symmetric and return real numbers for a quantity that is not real — a
smooth, plausible, wrong answer, which is the failure mode this whole area is built to avoid.

**This is verified, not assumed** — read the XML yourself before designing around it:
`~/.nuget/packages/numflat/1.3.0/lib/net8.0/NumFlat.xml`.

Everything in §1's staging follows from this one fact.

---

## 1. Decisions taken

**D1 (owner). L7b ships the SYMMETRIC COUPLED PAIR, and that is the whole phase.** A symmetric pair
— two identical conductors, mirror-symmetric about a plane — decouples into even and odd modes with
a **fixed** modal matrix `[1 1; 1 −1]/√2`, by symmetry alone. No eigensolver is involved at any
point, with or without loss: that matrix diagonalises *any* 2×2 of the form `[a b; b a]` whatever `a`
and `b` are. The phase table's own gate is *"even/odd-mode oracle for coupled microstrip"*, and the
symmetric pair is what edge-coupled filters, directional couplers, and differential pairs actually
are. **This is the case with no research risk in it, and it is most of the value.**

**D2 (owner). The general case — asymmetric pairs and N > 2 — is a LATER brief (L7b-b), not a
stretch goal here.** It needs a non-Hermitian complex eigensolver, which this project does not have.
Two candidate routes exist and **neither is settled here**; do not let L7b-a quietly pick one:

- **Real symmetric generalized eigenproblem.** For a *lossless* line, `[L]` and `[C]` are real
  symmetric positive definite, and the modal problem can be posed as `Gevd([C], [L]⁻¹)` — which
  NumFlat's `GeneralizedEigenValueDecompositionDouble` genuinely does handle. Loss would then be
  carried perturbatively. **The error that approximation introduces has to be measured, not
  assumed** — that measurement is the first deliverable of L7b-b, not an aside.
- **A small dense complex QR eigensolver** (Hessenberg reduction + shifted QR). Bounded, well-known,
  and a real numerical-methods commitment in a solo project.

If while doing L7b-a you find the general case is a two-hour job, **stop and report** rather than
building it — the reason to stage it is the eigensolver, and if that turns out not to be needed the
staging decision itself was wrong and should be revisited deliberately.

**D3. The coupled pair's ports are numbered `2k−1` = conductor *k*'s NEAR end, `2k` = its FAR end.**
So a pair is a 4-port: 1 and 2 are the two ends of conductor A, 3 and 4 the two ends of conductor B.
State it once, pin it with a test whose *wrong* pairing fails, and never re-derive it. A transposed
port map produces a coupler whose through and coupled ports are swapped — smooth, plausible, wrong,
and invisible in a magnitude plot of a symmetric structure.

**D4. No new result type, again.** The 4-port `S` cube, the per-port `Z0` cube and the `tline` group
are the same plumbing L7 already uses. The `tline` group's scalars become per-mode pairs (§4).

---

## 2. What already exists — read this before designing anything

Five things are already built. Each removes a chunk of scope.

1. **`[C]`, `[C₀]` and `[L]` are already full N×N matrices.** `RlgcExtractor.Extract` runs
   `ChargeSolver.MaxwellCapacitance` over the whole mesh, and `Invert` is a general Gauss-Jordan
   (its own comment says *"M is the conductor count — 1 today"*, not "1 by construction"). A coupled
   pair already produces a real 2×2 `[C]` today.
2. **The extractor already handles two conductors.** `CrossSectionExtractor` extracts them, derives
   the shared propagation axis, refuses a non-parallel pair by name, and reports the **gap** in its
   readback. `TwoParallelLines_ReportGapAndBothWidths_AndLeaveTheL7bRefusalToTheKernel` is the
   existing test that proves it — and that the refusal is the *kernel's*.
3. **An exact closed-form off-diagonal oracle already exists and is already wired.**
   `EmProblemBuilders.TwoWires` + `ClosedFormCapacitanceTests.T1_3` compares
   `C_odd = ½(C₁₁ − C₁₂)` against `πε₀ / acosh(d/2a)` — **exact**, and it already consumes `c[0,1]`.
   That is the first oracle L7b should extend, before any empirical coupled-microstrip fit
   (**R-mom-16**: validate against an exact closed form *before* comparing to a fit).
4. **The `.snp` write path, its provenance stamp and its staleness check are done** (R-em-19/20).
   Back-annotation consumes them; it does not rebuild them.
5. **The `SnP` component already takes what back-annotation must supply** —
   `ComponentTypeRegistry.DefaultParameters(SymbolKind.Snp, portCount)` declares
   `NumPorts`, `File` (a string, `ShowOnSchematic: true`), `PinConfig`, `Pitch`, `InterpMode`,
   `ExtrapMode`. There is no new component to write.

---

## 3. The scalar collapses — every one is a real code site, named

`RlgcExtractor` computes matrices and then **collapses each to element `[0,0]`**. Those collapses are
correct for one conductor and are exactly what L7b has to open up. Do not go hunting; here they are.

**R-cpl-1. `eeff` is `cComplex[0,0].Real / c0[0,0]`.** A coupled pair has **two** effective
permittivities — even and odd — and they differ substantially (the odd mode pulls more field into
the air gap). One number here is not a rounding issue; it is the wrong physical quantity.

**R-cpl-2. Wheeler's `∂L/∂n` is a scalar taken from `[0,0]`, and every conductor recedes TOGETHER.**
The code says so in its own comment: *"Every conductor recedes together, so this single derivative
already sums their surfaces; the per-conductor σ split only matters when they differ, which kernel
A's single-line scope does not exercise. Attribute it to the first conductor's metal."* For a pair,
`[R]` is a matrix, so the recession must be done **one conductor at a time** to get its columns. The
existing note is an accurate statement of a deliberate limit — treat it as the specification of what
to change, not as a bug.

**R-cpl-3. `rdc` sums every conductor into ONE scalar.** It must become a diagonal matrix: each
conductor has its own DC series resistance, and adding them is only right when there is one.

**R-cpl-4. `RlgcToSparams.Build` throws unless `z0PerPort.Length == 2`** and forms a scalar
`Zc·coth(γℓ)` 2-port. This is the modal decomposition's home.

**R-cpl-5. `QuasiStaticKernel.CanSolve` carries three refusals that L7b must narrow, not delete.**
`Ports.Count != 2`, *"Ports 1 and 2 are on different conductors"*, and *"This cross-section has N
signal conductors"*. Each must become "…for the case still unsupported", keeping the L7b-b message
for an asymmetric pair or N > 2 and the L8/L9 messages untouched. **Deleting a refusal instead of
narrowing it is how a kernel starts silently answering questions it cannot answer.**

**R-cpl-6. `CrossSectionExtractor` builds exactly two ports, both on `conductors[0]`.** It must
build `2N` per D3. `EmSetup` likewise carries `Port1Z0`/`Port2Z0` only, and the `.cem` panel two Z₀
fields — both need to become per-port lists. **`.cem` is `FormatVersion` 1 and additive is
possible**: a `Port1Z0`/`Port2Z0` pair plus an optional list, or a list that defaults from the pair.
Pick one, and keep every existing `.cem` loading unchanged.

---

## 4. The modal decomposition, and the symmetry that has to be checked rather than assumed

**R-cpl-7. Symmetrise before decomposing, and REPORT the residual.** This is the single most
important requirement in this brief, and the engine half already wrote it down for you:

> *"Point collocation on a piecewise-constant basis does **not** make the system matrix symmetric —
> only a Galerkin discretisation would. The residual `C₁₂ − C₂₁` is therefore a discretisation-error
> indicator (~3% on a coupled pair at default settings, shrinking under refinement), not a bug. L7b
> is the first phase that consumes the off-diagonals; it should refine or symmetrise rather than
> assume the raw matrix is symmetric."* — `src/Engine/Mom/CLAUDE.md`

So: symmetrise as `(M + Mᵀ)/2`, and surface `|C₁₂ − C₂₁| / |C₁₂ + C₂₁|` as a **named number in the
mesh/RLGC notes**, not as a silent internal step. It is the best available estimate of the
discretisation error in the coupled result, and a user tightening the mesh should be able to watch it
fall. A value above a stated threshold gets a warning telling the user to refine.

**R-cpl-8. Geometric symmetry is a separate check from matrix symmetry, and it decides whether the
even/odd split is legal at all.** `C₁₁ ≈ C₂₂` (and `L₁₁ ≈ L₂₂`) is what makes `[1 1; 1 −1]` the
correct modal matrix. Two lines of different widths, or on different metal levels, produce
`C₁₁ ≠ C₂₂`, and the even/odd split is then simply **wrong** — not approximate. That case must be
**refused with the L7b-b message**, using the same specific wording rule every refusal here follows:
name the asymmetry, give both numbers, say where the capability arrives.

**R-cpl-9. The modal quantities, stated once.** With the symmetrised matrices and the Maxwell
capacitance convention already in use (off-diagonals negative):

```
C_even = C₁₁ + C₁₂        C_odd = C₁₁ − C₁₂
L_even = L₁₁ + L₁₂        L_odd = L₁₁ − L₁₂
Z_e = √(L_even/C_even)    Z_o = √(L_odd/C_odd)
ε_eff,e = C_even / C₀,even        ε_eff,o = C_odd / C₀,odd
γ_e = √(Z_e·Y_e)          γ_o = √(Z_o·Y_o)      Re(γ) ≥ 0, same branch rule as kernel A
```

**The sign convention on `C₁₂` is the thing that will silently invert this.** A Maxwell capacitance
matrix has negative off-diagonals; a "mutual capacitance" matrix has positive ones. Getting it
backwards swaps even and odd — both answers look physical, and on a symmetric structure many
magnitude plots barely move. Pin it with a test that asserts **Z_o < Z_e** for edge-coupled
microstrip, which is true for every real coupled line and is the cheapest possible sanity gate.

**R-cpl-10. Form the 4-port Z-matrix and convert with `RFNetwork.ZToS` (R-mom-14, unchanged).** Do
not write a second ABCD→S, and do not write a coupled-line-specific S formula. The mode-to-terminal
transform plus the existing per-port `ZToS` is the whole path, which is what keeps reciprocity
structural rather than hoped for.

---

## 5. `.snp` back-annotation into the schematic

The phase table's second half, and the thing that makes L7b visible to a user rather than an engine
improvement.

**R-cpl-11. One action on the EM setup panel: place-or-update an `SnP` component pointing at the
written `.snp`.** Not a new component, not a new analysis kind — set `File` and `NumPorts` on an
ordinary `SnP` (§2 item 5), exactly as the design note's §10.8 co-simulation story assumes.

**R-cpl-12. It must be IDEMPOTENT, and it must key on something stable.** Re-running the EM setup
and re-annotating must **update** the existing component rather than adding a second one beside it.
This is the same problem L5's schematic→layout re-run already solved, and it solved it by keying on
a stable identity rather than on position — read `LayoutInstance.SchematicId` and
`SchematicToLayoutGenerator`'s own idempotency rules before inventing a second scheme.

**R-cpl-13. The stored `File` path follows `WorkspaceRefs`, not a raw absolute path.** That component
already exists and already states the rule: workspace-relative inside the workspace, absolute
outside, separators normalised to `/`, and an outside reference reported rather than silently stored
in a form that will not travel. A `.snp` written into `results/` is inside the workspace, so the
common case is relative — which is what makes the schematic survive being moved or shared.

**R-cpl-14. The R-em-20 staleness warning becomes load-bearing here, and should be surfaced where it
is now actionable.** Until now a stale `.snp` was a file nobody necessarily referenced. Once a
schematic points at it, the warning is telling the user their *simulation results* were computed
from a cross-section that no longer exists. Same detection, same three hashes — but consider whether
the schematic side should surface it too, and say what you decided.

---

## 6. Validation — the gate ladder

`tests/Engine.Tests/Mom/` for the kernel work, `tests/Ui.Tests/Em/` for the setup and
back-annotation. Tag anything measured at or above ~5 s `[Trait("Category","Benchmark")]`; nothing
here should come close.

**Tier C1 — exact closed forms first (R-mom-16), before any empirical fit.**
- **Extend the existing `TwoWires` oracle** to assert the full symmetrised 2×2, not just `C_odd`:
  `C₁₁ ≈ C₂₂` by symmetry, and `C_odd` against `πε₀/acosh(d/2a)` at two spacings. This is the one
  oracle in the ladder that is *exact*, and it already exists — extending it costs almost nothing.
- **The asymmetry residual falls under refinement.** `|C₁₂ − C₂₁|` at default settings versus at
  `EmMeshSettings.Refined(2)`. This is R-cpl-7's number behaving as the discretisation-error
  indicator the engine half says it is — if it does *not* fall, something else is wrong and this
  test is the one that says so.

**Tier C2 — the self-consistency oracles, which need no external data and are unusually strong here.**
- **A pair pushed far apart reproduces two independent single lines.** As the gap grows,
  `C₁₂ → 0`, `Z_e → Z_o → ` the isolated line's own `Z₀`, and both `ε_eff` converge to the single-line
  value. Assert against kernel A's *own* single-line result for the same width — so the coupled path
  is checked against the already-validated path rather than against a number typed into a test.
- **`Z_o < Z_e`** for edge-coupled microstrip (R-cpl-9's sign gate).
- **The 4-port is reciprocal, passive, and lossless when every tanδ and every σ is ideal** — the same
  four properties `NetworkPropertyTests` already checks for the 2-port, extended.

**Tier C3 — coupled microstrip against a published even/odd fit.** ±2–3%, over a stated span of
`W/h` and `S/h`. **State where the fit came from and that it is a fit** — the engine half's own
history has two cases where the closed-form "oracle" was the thing that was wrong, and a coupled
fit has *more* fitted parameters than the single-line Hammerstad-Jensen this project already
learned that lesson on. If the fit and the solver disagree by a few percent at tight coupling,
suspect the fit at least as hard as the solver, and settle it with Tier C1/C2 rather than by
adjusting the solver toward the fit.

**Tier C4 — refusals stay specific.** An asymmetric pair (different widths) refuses by name with
both `C₁₁` and `C₂₂` and points at L7b-b; three conductors refuses; a pair on two different metal
levels refuses. Each asserts the wording is *specific*, not merely non-empty — the bar every refusal
in this area is already held to.

**Tier C5 — co-simulation, end to end. This is the phase gate.**

> **Extract a coupled pair from a real layout, Simulate, back-annotate into a schematic, and run an
> HB analysis that uses it.** The `.snp` must land, the `SnP` component must resolve it, and the run
> must produce results. Re-running the EM setup and re-annotating must update the same component
> rather than adding a second one.

That is the phase table's own gate, and it is the only test that proves the two halves of L7b
actually meet.

---

## 7. Milestones, each with its own gate

| | Content | Gate |
|---|---|---|
| **C1** | Open up §3's five scalar collapses in `RlgcExtractor`; `[R]`, `rdc` and `∂L/∂n` become matrices. **No modal decomposition yet, no s-parameters.** | Tier C1 green; the existing single-line Tier 3 microstrip oracles **still pass byte-identically** |
| **C2** | Symmetry checks + the even/odd decomposition; modal `Z_e`/`Z_o`/`ε_eff,e`/`ε_eff,o` in the `tline` group | Tier C2 green |
| **C3** | The 4-port Z-matrix → `RFNetwork.ZToS` → `DataSet`; `CanSolve` narrowed | Tier C3 + the network-property half of Tier C2 green |
| **C4** | `.cem` per-port Z₀, the extractor's `2N` ports, the panel's port list | Tier C4 green; every existing `.cem` still loads |
| **C5** | Back-annotation into the schematic | **Tier C5 green — the L7b phase gate** |

**C1's gate is the one to take seriously.** Opening the collapses changes code that the single-line
Tier 3 oracles run through, and those oracles are the reason anyone trusts this kernel. If a
single-line number moves *at all* during C1, stop: the matrix generalisation has changed the
one-conductor case, which it must not.

Stop and report at any gate that does not go green rather than proceeding with a tolerance loosened
to make it pass.

---

## 8. Explicitly out of scope

- **Asymmetric pairs and N > 2 (L7b-b)** — D2. Refused by name, with the reason, not attempted.
- **A non-Hermitian eigensolver** — §0. If L7b-a needs one, something has gone wrong; stop and report.
- **The manual cut-line tool** (§10.3.3's escape hatch) — still its own brief.
- **The current-density heat map** on the mesh layer (§10.5) — needs per-segment solved charge
  surfaced from the engine; a small engine addition and a separate decision.
- **Stripline**, **full-wave (L8/L9)**, **wirebonds (LW1/LW2)**.
- **Adaptive frequency sampling** — kernel A is frequency-independent by construction (R-mom-11);
  there is still nothing to adapt.

---

## 9. File map (indicative)

```
src/Engine/Mom/
  RlgcExtractor.cs        — §3's five collapses opened up (existing file)
  ModalDecomposition.cs   — the even/odd split, symmetry checks, the R-cpl-7 residual (new)
  RlgcToSparams.cs        — the 4-port Z-matrix (existing file)
  QuasiStaticKernel.cs    — R-cpl-5's narrowed refusals (existing file)

src/Ui/Layout/Em/
  EmSetupModel.cs         — per-port Z₀ (existing file)
  CrossSectionExtractor.cs— 2N ports, D3's numbering (existing file)
  EmBackAnnotation.cs     — place-or-update the SnP component, framework-free (new)

tests/Engine.Tests/Mom/
  ClosedFormCapacitanceTests.cs — Tier C1, extending the existing TwoWires oracle
  CoupledLineTests.cs           — Tiers C2/C3/C4
tests/Ui.Tests/Em/
  EmCoSimulationTests.cs        — Tier C5
```

---

## 10. Two things to report back on, whatever else happens

1. **Whether D2's staging was right.** If the general N-conductor case turns out not to need an
   eigensolver after all, say so — the staging decision rests entirely on §0, and §0 should be
   re-checked against whatever NumFlat version is current when this is picked up.
2. **What the R-cpl-7 asymmetry residual actually measures** on a realistic coupled microstrip at
   default mesh settings, and how it moves under refinement. The engine half recorded ~3% as an
   incidental observation; L7b is the first phase that depends on it, so it deserves a real number
   in `src/Engine/Mom/CLAUDE.md` rather than an approximate one carried forward.
