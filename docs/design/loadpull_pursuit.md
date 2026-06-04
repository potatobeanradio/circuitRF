# circuitRF — Loadpull Pursuit: MXP/MXE Search & Auto-Zsource (Phase 4b-2 Design)

**Status:** Draft for review · **Date:** 2026-06-03
**Reads with:** `docs/design/loadpull.md` (the 4b-1 engine this builds on — the `Tuner`, the `Loadpull` directive, the 2-D sweep, the live measurements, VSWR warm-start), `docs/design/harmonic-balance.md` (the inner HB solve), `docs/design/measurements.md` (Pout/Pdc/DE/PAE), `docs/design/linear-engine.md` (§4.4 Tuner/Z_Port, §2.2 power convention). RfCore `RFNetwork.VSWR` is the distance metric throughout.
**Reference:** Baylis et al., "Efficient Optimization Using Experimental Queries: A Peak-Search Algorithm for Efficient Load-Pull Measurements" (steepest-ascent); Pedro et al., "A Simple Method to Estimate the Output Power and Efficiency Load-Pull Contours of Class-B Power Amplifiers" (the MXP↔MXE VSWR coupling).

This note specifies **Phase 4b-2**: the **`loadpull_pursuit`** analysis — a query-minimizing search that finds the **MXP** (max output power) and **MXE** (max efficiency) load (or source) terminations at constant compression, reports the conjugate-match **Zsource** at output backoff, and emits a **`.gam` grid file** of recommended terminations for a follow-up standard loadpull. It builds entirely on the proven 4b-1 engine; each search "query" is one 4b-1 adaptive-drive-to-compression run.

## 0. What `loadpull_pursuit` is, and what it is NOT

`loadpull_pursuit` does **not** supersede a standard loadpull — it is a *different, complementary* tool. A PA designer uses it to (1) find the **useful terminations** (MXP, MXE) to design matching circuits toward; (2) get the **region of interest** to then standard-loadpull around (out to ~2 VSWR from MXP/MXE) to see performance tradeoffs — query points far (> ~5 VSWR) from the optima are not useful and can even corrupt a 2-D contour interpolation; and (3) **avoid non-convergent terminations**. It automates the painful, error-prone loadpull setup, making it repeatable across many DUTs (valuable for DOE) and less prone to setup inconsistency.

It is a **new analysis type on the LP engine**, `type=loadpull_pursuit`, sharing all `loadpull` parameters **except `Grid`** (it generates terminations rather than reading them), plus the extra parameters in §3.

## 1. The search engine — steepest ascent in the VSWR plane

One general search engine serves both MXP and MXE; the only difference is the scalar **criterion** evaluated at each queried termination:
- **MXP:** Pout at compression (the P-xdB point the 4b-1 inner sweep already finds).
- **MXE:** efficiency at compression — **DE by default, PAE user-selectable** (§2).

Each **query** = one 4b-1 inner adaptive-Pin drive-to-compression run at a candidate termination, returning the criterion value (and the full inner sweep, cached — §4). Queries are expensive (a whole HB power sweep each), so the search minimizes query count.

### 1.1 The algorithm (Baylis steepest-ascent, VSWR-metric)
Adapted from Baylis et al., with **distance measured as `RFNetwork.VSWR` between two complex terminations** (identical whether expressed as Γ or Z — so the search is grid-representation-agnostic):

1. **Tangent-plane stage.** At the start point, query two neighbors a step `Dn` (a small VSWR step) away — one in each coordinate (Re/Im of the termination, or equivalently the Z/Γ components). From the three criterion values, fit the tangent plane `∆C = m1·∆x + m2·∆y` (Baylis Eq. 1) and take the steepest-ascent direction perpendicular to the equi-criterion line (Eq. 2).
2. **Ascend.** Step distance `Ds` along the ascent line to the next candidate (Eq. 3 intersection, the `∆C > 0` solution). Query it. If the criterion increased, repeat from the new point. If not, **shrink `Ds` to one-third** and re-query.
3. **Converge.** When `Ds` falls below a threshold (`Dn`), do the **final refinement**: query the points around the candidate (4 directions + the already-known neighbors) and fit a second-order polynomial `∆C = m1∆x + m2∆y + ½[m11∆x² + 2m12∆x∆y + m22∆y²]` (Baylis Eq. 4); the optimum is where its gradient is zero. That analytic optimum is the reported MXP (or MXE) termination.

`Dn`, `Ds` (initial), and the convergence threshold are **VSWR-denominated** (e.g. `Dn`≈1.05, `Ds`≈1.3 initial). The acceptance target for the reported optimum vs a hypothetical high-resolution loadpull is **≤ 1.1 VSWR** (the owner's "reasonable estimate" bar).

### 1.2 Z vs Γ
The search math runs on the termination's complex value; **distance is always VSWR** (RfCore), so it is identical in Z or Γ. The internal working representation is **Z** (the VSWR-circle builder, §5, has a validated Z form only). Convert to/from the grid representation via RfCore at the boundaries.

## 2. Efficiency calculation (added to the LP engine)

4b-1 exposed the Tuners' bias-supply V/I nodes but did not compute efficiency. 4b-2 adds it (the "live efficiency detection" 4b-1 §8 reserved):
- **Pdc** = Σ over the Tuners' internal DC bias supplies of `Vdc · Idc` (real DC power drawn), read from the bias-supply nodes the Tuner exposes.
- **DE** = Pout / Pdc.
- **PAE** = (Pout − Pin_delivered) / Pdc.
- **MXE criterion = DE by default; PAE user-selectable** (`EffType=DE|PAE`, default DE — most users want DE).

Pout and Pin_delivered already exist from 4b-1 §4.

## 3. The `loadpull_pursuit` directive

`analysis Name type=loadpull_pursuit …`. **All `loadpull` keys except `Grid`** (LoadTuner, SourceTuner, Sweep, Tone, TuneHarm, MaxHarm, Compression, GainType, PinStart, PinStep, **PinMax**, Tickle, MaxIter, FFTOverSample, Tol, DriveStepping, GuardHarmonic), plus:

| Key | Meaning | Default |
|---|---|---|
| `EffType` | MXE criterion: `DE` or `PAE` | DE |
| `ZsourceOBO` | output back-off (dB from compression) at which Zin is extracted for the auto-Zsource report (§6). Approximate — accuracy limited by `PinStep` | 5 |
| `OutputGrid` | path to write the recommended-terminations `.gam` file (§5). **If absent, no file is written** | none |
| `VSWR1` (`VSWR_focused`) | focused box size (VSWR circle radius) around MXP and MXE | 1.5 |
| `VSWR1_resolution` (`focused_resolution`) | grid spacing (NxN samples) inside the focused boxes | 4 |
| `VSWR2` (`VSWR_broad`) | broad box size (VSWR circle radius) for the surrounding coarse grid | 3 |
| `VSWR2_resolution` (`broad_resolution`) | grid spacing (NxN samples) for the broad box | 4 |
| `keepNonconvergingPoints` | if false, exclude grid points within `nonconvergentVSWR` of any termination found non-converging during the search; warn on removal | false |
| `nonconvergentVSWR` | exclusion radius around known non-convergent terminations | 1.05 |

Returns (to the result set / report): **MXP termination & its Pout**, **MXE termination & its efficiency**, **Zsource = Zin\*** at `ZsourceOBO` backoff for the MXP and MXE cases (§6), and (if `OutputGrid` set) the recommended-terminations `.gam` file (§5).

**Generality:** `loadpull_pursuit` works for any `TuneHarm` (1=fund, 2=2f0, …) on `Sweep=Load` or `Source`. Fundamental-load is the common case; 2f0-load is useful. A 3f0/source pursuit may return mostly numerical noise — the algorithm must **not crash** on it (degrade gracefully, report low/garbage criterion without erroring).

## 4. Sharing data between the two searches (MXP then MXE)

The two searches share one engine and **share data** so the second is much cheaper:
- **Cache every query** (its full inner compression sweep) keyed by termination, VSWR-deduplicated (a requested termination within a tiny VSWR of a cached one is a cache hit — no re-solve). Both MXP's Pout and MXE's efficiency are computable from the *same* cached inner sweep, so once MXP has queried a set of terminations, MXE gets their efficiency for free.
- **Seed MXE from MXP via the Pedro coupling.** For a stable FET the MXP↔MXE separation is empirically **~2–2.5 VSWR** (Pedro et al. — it follows from the FET turn-on characteristic). So after MXP converges, **seed the MXE search ~2.25 VSWR from MXP** (rather than cold-starting), turning MXE into a short refinement. (Run MXP first, then MXE seeded from it.)

## 5. The recommended-terminations `.gam` builder

When `OutputGrid` is set, after finding MXP and MXE, build a `.gam` file designed to produce clean loadpull contours for a downstream 2-D interpolator — dense near the optima, sparse further out:

1. Find MXP and MXE (the search, §1).
2. Build a **`VSWR1` (focused, default 1.5) VSWR circle** around **each** of MXP and MXE (§5.1).
3. In the grid domain, take each circle's min/max X and min/max Y → **box1** (around MXP) and **box2** (around MXE).
4. Sample box1 and box2 each with **`VSWR1_resolution` × `VSWR1_resolution`** (default 4×4) equally-spaced points in X and Y — the **focused** (high-resolution) sampling.
5. Build a **`VSWR2` (broad, default 3) VSWR circle** around MXE → **box3** (min/max X,Y).
6. Combine box1, box2, box3 extents → **box4** (overall min/max X,Y).
7. Sample box4 with **`VSWR2_resolution` × `VSWR2_resolution`** (default 4×4) equally-spaced points, but **discard any that fall inside box1 or box2** — the broad sampling is effectively coarser (same point count over a larger area).
8. Write the focused points (step 4), the broad points (step 7), and the **MXP and MXE points themselves** to the `.gam` file.

**Non-convergent exclusion** (unless `keepNonconvergingPoints`): drop any output point within `nonconvergentVSWR` (default 1.05) of a termination found non-converging during the search, and **warn the user** that points were removed.

### 5.1 VSWR circle (Z domain)
Given a center impedance `z` and a VSWR, the circle of constant-VSWR-from-`z` is (validated Z form; no validated Γ form, so this runs in Z and converts on write via RfCore):
```
gamma   = (vswr − 1)/(vswr + 1)
z_center = z.real·(1+gamma²)/(1−gamma²) + j·z.imag
z_radius = z.real·2·gamma/(1−gamma²)
point(t) = (z_center.real + z_radius·cos t) + j·(z_center.imag + z_radius·sin t),  t ∈ [0, 2π)
```
(The owner's reference implementation oversamples `t` with a VSWR-dependent step; circuitRF only needs the **min/max X and Y** of the circle for the box, so it can compute the extents directly from `z_center ± z_radius` rather than sampling `t`.)

## 6. Auto-Zsource (conjugate match at backoff)

After MXP/MXE is found, the engine extracts the source impedance to recommend for the input match. **Zin is NOT computed at every query** — only once, after the optimum is found:
1. At the MXP (and MXE) load termination, set drive to **`ZsourceOBO` dB backed off from compression** (default 5 dB below the P-xdB drive level — approximate, granularity set by `PinStep`).
2. Compute the DUT input impedance **Zin** at the fundamental at that operating point (from the converged HB V/I at the source-Tuner DUT-facing port: `Zin = V/I` at f0).
3. Report **Zsource = Zin\*** (conjugate match).

Zin depends on the load termination for a non-unilateral device, so Zsource is reported **per optimum** (one for the MXP load, one for the MXE load). This is the auto-Zsource differentiator: the user gets a recommended source match without a separate sourcepull.

## 7. Non-compression exit (the search cannot proceed without compression)

MXP/MXE are defined *at compression*; if the DUT does not compress, they do not exist. The 4b-1 inner sweep already drives to `PinMax` (or non-convergence). Rules:
- A candidate that **fails to reach `Compression` within `PinMax`** is **unscorable** — it yields no criterion value. The search rejects it as an ascent step (shrink `Ds` / try elsewhere), and excludes it like a non-convergent point.
- If the **start point itself is unscorable** (or no neighbor compresses, so no tangent plane can be formed), the search **aborts with a clear message** ("DUT does not compress within PinMax=… — cannot find MXP/MXE; raise PinMax or check bias/load").
- **Do not silently raise `PinMax`** — it is a user safety cap. Raising it is the user's decision. (The test circuit `hero3B_at_compression.cnl` uses `PinMax=30` to reach compression; setting it to −18 deliberately triggers this exit.)

## 8. Hero 3B — the 4b-2 acceptance anchor

`testdata/Hero3B/hero3B_at_compression.cnl` — the Hero-3 PA with `PinMax` raised (to 30 dBm) so the DUT compresses, and a `loadpull_pursuit` directive. Acceptance:
- MXP and MXE found within the query budget; the two optima separated by ~2–2.5 VSWR (Pedro sanity check) for this stable FET.
- MXE search demonstrably cheaper than MXP (cache hits + Pedro seed).
- Reported optima within **≤ 1.1 VSWR** of a high-resolution reference loadpull (owner-verified, self-generated).
- Auto-Zsource (Zin\* at 5 dB backoff) reported for both optima.
- `OutputGrid` `.gam` written with the focused+broad structure; non-convergent points excluded (with warning) unless `keepNonconvergingPoints`.
- **Non-compression exit verified:** lowering `PinMax` (e.g. to −18) makes the search abort cleanly with the no-compression message, not crash.
- Self-generated regression golden (à la Hero 2/3), owner-verified, labeled not-independently-validated.

## 9. Open items
1. **Directive `\` continuation in `hero3B_at_compression.cnl`** — the proposed directive's middle lines (after `VSWR2_resolution=4`, `keepNonconvergingPoints=false`, `Compression=3`'s line) are missing `\` continuations and would parse as broken separate directives. **Owner to fix** the line continuations.
2. **Tuner termination syntax `Z[1]`/`Z[2]`** — the test circuit uses indexed-bracket `Z[1]`/`Z[2]` (vs the `Z1`/`Z2` in loadpull.md §1.3). Reconcile loadpull.md to the bracket form if that's the intended syntax (it matches the SDD `I[p,w]` bracket convention — arguably more consistent). Owner to confirm.
3. **`Dn`/`Ds`/threshold VSWR defaults** (§1.1) — tune empirically on Hero 3B; the values above are starting estimates.
4. **Frequency/power "superalgorithm"** (Baylis Table 4 — chaining searches across power/freq seeding each from the last) — **out of scope** (owner: diminishing returns).

## 10. Summary of decisions
- `loadpull_pursuit` = new LP-engine analysis; all `loadpull` keys except `Grid`, plus the §3 search/output keys. Complements, does not replace, standard loadpull.
- **One steepest-ascent engine** (Baylis), criterion = Pout (MXP) or DE/PAE (MXE); **distance = RFNetwork.VSWR** (Z/Γ-agnostic), internal working rep = Z.
- **Each query = one 4b-1 drive-to-compression run**; cache all inner sweeps (VSWR-dedup) so MXE reuses MXP's data; **seed MXE ~2.25 VSWR from MXP** (Pedro coupling) for a cheap second search.
- **Efficiency added** to the LP engine: Pdc from Tuner bias-supply V/I; **DE default, PAE selectable**.
- **Auto-Zsource:** after the optimum, extract Zin at `ZsourceOBO` (default 5 dB) backoff, report **Zsource = Zin\*** per optimum (load-dependent for non-unilateral devices). Zin computed once, not per query.
- **`.gam` builder:** focused `VSWR1` (1.5) boxes around MXP & MXE at `VSWR1_resolution` (4×4) + a broad `VSWR2` (3) box at `VSWR2_resolution` (4×4) minus the focused regions + the optima; exclude non-convergent neighborhoods (`nonconvergentVSWR` 1.05, warn) unless `keepNonconvergingPoints`. VSWR circle in Z domain (extents from `z_center ± z_radius`).
- **Non-compression exit:** unscorable candidate → rejected; unscorable start → abort with a clear message; never silently raise `PinMax`.
- **Generality:** any `TuneHarm`, load or source; must not crash on a noisy 3f0/source pursuit.
- Hero 3B (`PinMax` raised to compress) is the gate; ≤ 1.1 VSWR vs a high-res reference; self-generated regression golden.
