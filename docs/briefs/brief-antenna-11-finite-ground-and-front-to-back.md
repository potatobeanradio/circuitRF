# Brief — ANT-11: a finite ground plane, and activating front-to-back

**Series:** `brief-antenna-0-overview.md` §2, §4. **Depends on:** ANT-4, ANT-5. **Blocks:** nothing.

The last brief in the series, and the most research-shaped. It is where the front-to-back refusal
staged in ANT-5 **narrows**.

**Do not start this until ANT-4 and ANT-5 have shipped and their oracles have held.** Every number
here is a correction to theirs, and a correction on top of an unvalidated primary is unfalsifiable.

---

## 1. The problem, stated exactly

The ground plane is analytically infinite. That is not an approximation the mesher makes — it is what
the layered Green's function *is*, and it is why the kernel needs no airbox and no PML. So:

- there is **no field at all** at θ > 90°;
- the front-to-back ratio is infinite;
- power launched into a surface wave never returns, so ANT-5 books it as loss.

On a real board none of the three is true. The measured example's ground is 70 × 70 mm = **0.40 λ₀**
at 1.74 GHz — small enough that the pattern will have real back radiation, real ripple, and a
directivity meaningfully below what the infinite model reports.

**Meshing the ground plane as finite metal is NOT the full answer, and that matters to the decision.**
It is commonly assumed to be, so the reason is worth stating once:

- **The dielectric stays laterally infinite either way.** The 2.5-D premise cannot express a lateral
  dielectric boundary at all — see the overview's §2 bullet and `PatternedDielectric.Deactivate`'s own
  sentence. Drawn dielectric artwork (MIM-7) decides *whether* a layer is in the run, never *where it
  stops*. So a meshed finite ground still gives you a board with **no edge**: the surface wave still
  never reaches one and still never diffracts back. **That is §3.3's dominant back-radiation mechanism
  on a thin substrate**, so the expensive path fixes the secondary mechanism and leaves the primary one
  exactly as absent as it is today.
- **It costs more than it looks, and the ceiling moves the wrong way.** It makes every problem
  two-level, and `PlanarAimGeometry.Build` refuses a multi-level or via-bearing mesh regardless of what
  the caller asks for, so such a run is judged against `SurfaceMesher.UnknownCeiling` (5,000, dense) and
  not `AcceleratedUnknownCeiling` (12,000). The measured board solves at N = 4,854 today; its
  70 × 70 mm ground is ~2.4× the patch's area, so even at the *same* pitch it lands around 15,000 — 3×
  past a ceiling it just forfeited the accelerator for.
- **The pitch on the ground is set by h, not by λ.** The return current is essentially the patch's
  mirror image, concentrated within ~h laterally; h = 203.2 µm here, so resolving it wants ~344 cells a
  side, ≈ 118,000 cells, ≈ 2×10⁵ unknowns. And an under-resolved ground does not degrade gracefully —
  it fails to short the field under the patch, which shifts the resonance and inflates radiated power.
  The infinite-plane image is **exact and free**.
- **The port reference disappears.** `PlanarExtractor`'s `UngroundedRefusal` names it: Z_c = γ/(jωC_pul)
  and the calibration standard's end run is measured in substrate heights. MIM-4 generalised the
  electrostatics to an arbitrary `LayerStack` (`InteriorStaticGreens`), so the blocker is now the
  narrower and more physical one — a single conductor with no return has no C_pul at all. The probe-fed
  patch this series calls the cleanest fit is clean *because* its port's − terminal is the analytic
  plane.

**So it is a different-size project that buys part of one mechanism.** It is out of scope here and
should stay out until something forces it — and if something does, the thing that forces it will
probably be a structure where the ground plane IS the radiator (a PCB monopole or IFA), which is a case
no post-process rescues, rather than a patch.

**What is in scope is the cheap, standard, honest correction.**

## 2. What is already available and currently thrown away

The extractor reports, on the measured board:

> 9 shape(s) on the ground-designated conductor layer were ignored. The ground plane is the laterally
> infinite plane the Green's function handles analytically; a finite ground pour is not meshed, and
> modelling one is not part of L9.

**The outline of the real ground pour is in the `.clay` and the extractor already finds it — it just
discards it.** So step one of this brief is to *read* that outline without meshing it, and carry it
into the problem as a described boundary rather than as artwork.

That alone is worth doing even if §3 is never built, because it lets the solver say something it
cannot say today: **how big the ground plane is in wavelengths**, which is the single number that
predicts whether the infinite assumption is defensible. A run on a 0.4 λ ground should say so.

## 3. M1 — the edge-diffraction estimate

Geometrical/uniform theory of diffraction from the ground rim: the infinite-ground pattern illuminates
the edge, the edge re-radiates, and the sum has back radiation and ripple. Standard, well documented,
and a post-process — it needs no change to the fill, the mesh, the ports or the solve.

Three non-negotiables:

1. **It is an estimate and it is labelled one, in its own trace.** Never merged into the primary
   pattern cube, never silently replacing it. A user must be able to plot both and see the
   difference — that difference *is* the information about whether their ground plane is big enough.
2. **It has a stated validity range and refuses outside it.** UTD is an asymptotic high-frequency
   method; on a ground plane of a fraction of a wavelength it degrades, and the measured board is
   exactly there at 0.40 λ₀. **Find where it breaks and refuse past it** rather than returning a
   plausible curve. This is the same discipline `Dcim.WithinValidatedRange` applies.
3. **The surface-wave contribution is part of it, not separate.** On a thin substrate the dominant
   back-radiation mechanism is often the surface wave reaching the board edge and diffracting, not the
   space wave reaching the ground rim. ANT-5 books surface-wave power as loss; here some of it comes
   back as radiation. **If only the space-wave diffraction is modelled, say so** — a back lobe that is
   present but under-predicted is more misleading than one that is refused.

## 4. M2 — activating front-to-back

- The θ axis extends to **180°**. ANT-4 built it as data rather than a constant, and ANT-7 and ANT-10
  both derive their hemisphere notes from the axis range, so all three follow automatically. **If any
  of them needs editing here, the staging in ANT-4 was not done and that is worth recording.**
- `FrontToBackDb` in ANT-5's registry **narrows** from always-refused to: available when a ground
  outline is present and the estimate is inside its validity range; still refused, with a sentence,
  otherwise. One predicate.
- **The refusal is not deleted.** `LayeredMedium.CanHost`'s rule: deleting a refusal instead of
  narrowing it is how a kernel starts silently answering questions it cannot answer.
- Every metric that integrates over the sphere — directivity, efficiency, beamwidth — now has a lower
  hemisphere to integrate. **Decide and state whether the published metrics use the corrected pattern
  or the primary one.** Recommendation: the primary stays primary and the corrected metrics are their
  own labelled set, for the same reason as §3.1. A directivity that silently changes because a
  post-process was enabled is worse than two directivities with different names.

## 5. Gates

- **A known analytic case**: the pattern of a source over a finite circular ground plane has published
  UTD results. Compare against those, not against a second circuitRF path.
- **The infinite limit**: as the ground outline grows, the corrected pattern must converge to the
  primary one, and the F/B must grow without bound. This is the strongest self-test available and it
  needs no external data.
- **The validity refusal fires** on a ground plane small enough to be outside the method's range, and
  the threshold is chosen from a measurement rather than from a rule of thumb. **Report the curve that
  chose it.**
- **`FrontToBackDb` still refuses** when there is no outline — assert it, so the narrowing did not
  become a deletion.
- ANT-7's and ANT-10's hemisphere notes change with the axis and **were not edited** — assert by
  diffing.
- The estimate is a separate trace and the primary cube is unchanged, byte for byte, when the estimate
  is off.
- The ground-size-in-wavelengths note (§2) appears on every run, including runs with no estimate.
- `RESOLVED.md` write-up in `src/Engine/Mom`; `CLAUDE.md` gains the narrowed refusal and the validity
  range.

## 6. Must NOT

- **Do not mesh the ground plane.** Different project, and a PARTIAL answer even when taken — §1.
  Say both if it is wanted.
- **Do not merge the estimate into the primary pattern.**
- **Do not extrapolate past the validity range.**
- **Do not delete the front-to-back refusal.** Narrow it.
- **Do not let enabling the estimate silently change a previously-published metric.**
- **Do not claim the surface-wave contribution if it is not modelled.**

## 7. If this is not taken

That is a defensible outcome and the series still stands: F/B stays refused with its reason, the
infinite-ground pattern is correct for what it models, and the limit is documented in ANT-12's
Cannot list. **The one thing that must happen either way is §2's ground-size note** — a user on a
0.4 λ ground plane needs to know that before they trust a directivity, and that costs almost nothing.

## 8. Reading order

`src/Design/Layout/Em/PlanarExtractor.cs` (where the ground pour is found and discarded, and the note
that says so) · `src/Engine/Mom/LayeredMedium.cs` `CanHost` (the refusal pattern) ·
`src/Engine/Mom/SurfaceWavePoles.cs` (the surface-wave modes the §3.3 mechanism needs) ·
`src/Engine/Mom/Dcim.cs` `WithinValidatedRange` (how a validity range is expressed as a refusal here) ·
ANT-4 §3 and ANT-5 §2a (what is being narrowed).
