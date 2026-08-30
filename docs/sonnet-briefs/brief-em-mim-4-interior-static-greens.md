# Brief MIM-4 — the interior-height static Green's function ("all of Part B")

**Problem.** The only electrostatic Green's function in the repository is an image series over ONE
grounded slab (`PlanarKernelTerms.StaticScalar`), valid for a source on that slab's top surface.
Everything de-embedding rests on it, so three refusals fence it in — and every realistic MIM run,
whose feed arrives on an upper metal level, hits one of them:

1. `PlanarSolve` throws on a de-embeddable EDGE port whose level is not the slab top
   (`LevelIsOnSlabTop` — "would renormalise every published s-parameter by the wrong reference").
2. `PlanarExtractor` refuses more than one dielectric between ground and the LOWEST analysis level
   ("merge the layers under the feed", losing the real stratification).
3. `LayeredStaticGreens`, `DcimModel.Evaluate` and `SommerfeldIntegral.EvaluateLayered` refuse
   interior sources by name.

`src/Engine/Mom/CLAUDE.md` §7 already calls this "the most valuable remaining work here". It is
research-grade — the same class of work as L8a/L9c, where twice the ORACLE was wrong before the
method was — so it is staged oracle-first, and each milestone lands separately.

**Why it is tamer than the dynamic problem.** At ω = 0 the spectral kernel is algebraic in k_ρ: no
branch points, no surface-wave poles, no oscillation — the transmission-line cascade at DC is a
ratio of exponentials that a GPOF/image fit or a plain tail-accelerated quadrature inverts without
DCIM's pathologies. The delicacy is elsewhere: the §L9c partition (every reflection referenced to
the source's own interface; an interior source needs a DIFFERENT partition), and the recorded trap
that the image constant K must be COMPLEX for lossy dielectrics (a real-εr fit sits a
frequency-independent 1.1e-6 off and reads exactly like a convergence floor).

Read first: `src/Engine/Mom/CLAUDE.md` §L9c and §7; `PlanarKernelTerms.StaticScalar`;
`LayeredStaticGreens` (its refusal wording is this brief's contract); `PlanarDeembed`
(`CapacitancePerMetre` and how Z_c = γ/(jωC_pul) is consumed); `PlanarSolve`'s buried-port throw;
`PlanarExtractor`'s stratified-slab refusal; `docs/design/mom-engine.md` §10.9's closed-form
ladder — tier 2's two-layer coax is the only cheap closed form that genuinely exercises an
interface, and it is this brief's tier-2 oracle again.

## Milestones

1. **The spectral static kernel for a general `LayerStack` at arbitrary (z, z′)**, oracle'd before
   any inversion exists: εᵣ = 1 collapses to free space plus one image; source on a one-slab top
   surface reproduces the existing image series' spectral form; symmetry G(z, z′) = G(z′, z).
2. **The spatial function** (fit or quadrature — decide by measurement, record the loser): gated
   against adaptive numerical inversion at interior heights, and against the two-layer closed
   forms (series-stacked parallel-plate C; two-layer coax from §10.9's ladder).
3. **C_pul at an interior height.** `PlanarDeembed.CapacitancePerMetre` takes the port level's own
   z. The one-level, on-slab-top path stays on the EXISTING code, bit-identical — the R-mlp-1
   pattern: the new machinery activates only where the old one refused.
4. **The refusals retire, in their own order.** First the `PlanarSolve` buried-port throw (feed on
   an upper level of a single-slab stack — the GaAs MIM case); then `PlanarExtractor`'s
   stratified-sub-feed refusal (carry the real layers instead of "merge"); each with de-embedded
   S gates: reference-plane invariance, a 2L-line-equals-two-L-lines check on a buried level, and
   agreement with the raw solve at the port plane to the calibration's own residual.
5. **Refusal wording swept.** Every message that said "nothing else in this repository provides
   it" or "merge the layers" is corrected in place — a refusal that outlives its truth is the
   failure mode R-mom-17 exists for.

## Must NOT

- Perturb any currently-passing de-embedded result: existing fixtures are bit-identical (milestone
  3's gate), because the shipped path is untouched.
- Reach for the dynamic DCIM machinery "since it is there" — the static problem has its own,
  simpler structure, and coupling the two would put §L8c's fragility on the de-embedding path.
- Attempt frequency-dependent Z_c or buried-level CALIBRATION STANDARDS beyond what the existing
  two-line algebra already does — the standard stays a single-level uniform line on the port's own
  level; only its electrostatics generalise.

## Gates

Milestone oracles as tests (closed forms fast and routine; anything ≥ ~5 s tagged Benchmark);
bit-identity on every existing de-embedded fixture; the retired refusals' replacement behaviour
tested by name. Write-up in `src/Engine/Mom/RESOLVED.md`, tables in `HISTORY.md`;
`docs/design/mom-engine.md` §10.12 and the §7 "Refused by name" list corrected in place; the
user-facing EM reference's "Cannot" list updated. `dotnet test tests/Engine.Tests` and
`tests/Ui.Tests` green.
