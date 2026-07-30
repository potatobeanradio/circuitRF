# Sonnet Brief — Stability, passivity, and Touchstone files as first-class data sources

**Read §1 before planning.** Much of this is already designed and partly built; the brief implements a
resolved design rather than inventing one.

**Primary reference:** `docs/design/data-display.md` — the dual-source `Trace` design (~line 331), the
S-param-control gating (~line 339), and the **per-port reference impedance** resolution (~lines 351–355).

Gate command is plain `dotnet test`.

---

## 1. What already exists — do not rebuild it

Three things are already settled, and each removes work:

**A `Trace` is already dual-source.** It is backed by either an **SNP** (Touchstone — carrying "the existing
full S-parameter machinery: S→Y/Z, Z0 renorm, **stability circles, Mu/MuPrime/MaxGain**, marker impedance")
**or** a **DataSet cube**. So the stability mathematics exists and is exercised; only the cube path lacks it.

**The design already says where these controls belong:** *"S-param-only controls (S/Y/Z, Z0,
derived/stability) stay gated to the S-cube trace kind — aligns with §2.8 per-trace-kind card bodies."* The
typed card is the specified home. Implement it; do not re-decide it.

**R-stb-1. The cube path calls the *same* stability functions the SNP path uses.** Two implementations of μ
and K will eventually disagree, and the one not under test is the one that is wrong. This is the third time
this pattern has appeared here — Hammerstad-Jensen shared with the MoM oracle, and `FormatFreq` diverging
from `AnalysisPreviewHelper`.

## 2. The real technical gap: reference impedance

This is the part that makes the cube path genuinely different from Touchstone, and it is already documented
at ~lines 351–355.

**R-stb-2. Renormalize per-port → uniform real before computing anything.** A simulator may produce S
referenced to **per-port, possibly complex** terminations. Touchstone v1.1 is always uniform, which is why the
SNP path never had to care. The stability formulas require a **uniform real** reference — the existing
implementation "already (correctly) renormalizes to a uniform real reference internally before computing."

So the cube path is: **renorm-per-port → uniform-real → the existing stability formulas.** Do not skip the
renormalization because a particular test cube happens to be uniform 50 Ω; the failure would appear only on
the designs that most need stability analysis.

**`SNP` stays uniform-only by design** — do not extend it.

## 3. Port-pair selection — the reason a card beats a checkbox

Stability's 2-port formulas need to know *which* 2-port. A 4-port result containing two FETs has several
candidate pairings and only the user knows which is meaningful — which is why an automatic "compute stability
when the data is 2-port" rule cannot serve the owner's stated case.

**R-stb-3. The card offers an ordered *input port* and *output port* selection, valid for any N ≥ 2.** Two
devices in one run are then two traces with different port choices. This generalises: gain circles, MSG and
noise circles will all want the same selection.

**Two independent selectors, not an enumerated list of pairs.** The number of pairs grows as `N(N−1)/2` — 28
for an 8-port, 190 for a 20-port — so a pair dropdown does not scale. Two port selectors do, for any N.

**R-stb-3a. The selection is ordered, and the order changes the answer.** Port roles are not symmetric: μ is
the *load* stability factor and μ′ the *source* stability factor, so swapping input and output swaps which is
which. Label them **input** and **output**, never "port A / port B", and never treat `(1,2)` and `(2,1)` as the
same selection.

When the data is exactly 2-port, default to input = 1, output = 2 and hide the selectors.

**R-stb-3b. Support any `.sNp` / N-port cube for N ≥ 2 — nothing about a specific N is hardcoded.** 3-port,
5-port and 12-port data must work by the same path as 2-port, with no per-N branches.

**N = 1 is out of scope for this card**, per the owner. Disable it with a reason rather than showing an empty
metric list. Worth noting for later: **passivity alone *is* well-defined at N = 1** (`|S₁₁| ≤ 1`), so if
one-port passivity is ever wanted it is a small addition rather than a rethink — the 2-port-only restriction
belongs to the stability metrics, not to passivity (§4).

**R-stb-4. State the termination assumption in the card.** Extracting a 2-port sub-matrix from an N-port is
valid **only because the other ports are assumed terminated in the reference impedance**. That is standard and
correct, but someone comparing against a bench measurement where port 3 saw something else gets a mismatch
with no explanation. One line of text is cheap insurance.

**R-stb-5. The card offers only metrics that fit the plot it is being added to.** μ, μ′, K, |Δ| and passivity
are **scalars versus frequency** → rectangular. **Stability circles are loci in the Γ plane** → Smith. Since
the two do not mix in one plot, offer what fits and refuse the rest **with a reason** rather than producing an
empty trace.

## 4. Passivity — new mathematics

**R-stb-6. Passivity is `σ_max(S) ≤ 1`** — the largest singular value of the scattering matrix. Equivalently
`I − Sᴴ S ⪰ 0`. Plot `σ_max` versus frequency, so the passivity boundary is the line at 1 and the reader sees
*how far* from passive, not merely whether.

Three things that distinguish it from the other metrics:

- **It is not 2-port-limited.** μ, μ′ and K are 2-port formulas; passivity is defined for any N. So the
  port-pair selector is **optional** here — offer whole-network passivity as the default and a selected pair
  as a choice.
- **Passivity of an extracted sub-matrix is not passivity of the device.** A 2-port extracted from a 4-port
  can test passive while the full network is not. If a pair is selected, say so in the card (R-stb-4's line
  covers the same ground).
- **It needs R-stb-2's renormalization too.** The `SᴴS ⪯ I` test assumes a uniform real reference; under
  per-port complex references it is not the right test.

Implement it beside the existing stability functions, in the engine, so both trace paths reach one copy
(R-stb-1).

## 5. Touchstone files as workspace data sources

The owner wants to drop a `.s2p` — or any `.sNp`, N ≥ 2 — into the workspace and have it become a selectable
data source with the same card. **He has clarified that a "Known File" is a *reference*, and may point outside
the workspace.** That changes the storage rule below, and corrects one written in the multi-file brief.

**R-stb-7. The design already anticipates this**: `docs/design/data-display.md` line ~24 states the display
"plots from **files**: `.npy` (circuitRF native) and Touchstone (`.sNp`)", and the **data-source library
(§2.2) decides which path per file**. Read §2.2 and extend it rather than adding a parallel mechanism.

**R-stb-8. A dropped `.sNp` is listed alongside `.npy` files in the source picker**, using the same Datasets
surface and alias mechanism the multi-file work added. From the user's side there is one list of sources; the
SNP-versus-cube distinction is an implementation detail.

**R-stb-8a. Determine whether "Known Files" already exists as a concept, and if so make the Datasets list
*be* that surface rather than a second one.** The owner speaks of it as an existing mechanism. Two parallel
lists of referenced files — one for Known Files, one for Data Display sources — would be a duplication users
would have to reconcile by hand. **Report what §2.2 and the Known Files mechanism currently are before
building either.**

### 5.1 Storage — this corrects the multi-file brief's R-dd-6

`brief-data-display-multifile-ui.md` **R-dd-6** said a stored source is *"a bare filename resolved against the
workspace's `results/` — never a rooted path, and never one containing a directory separator."* **That is too
strict now**, and it was written before the reference-outside-the-workspace requirement was known. It fails in
two ways: it cannot express a file elsewhere in the workspace, and it cannot express one outside it at all.

**R-stb-10. Store a workspace-relative path when the target is inside the workspace; an absolute path only
when it is outside.** Inside stays fully portable — move or share the workspace and it resolves. Outside
cannot be made portable by any encoding, so the honest thing is to store it plainly and **tell the user**
(R-stb-11).

**R-stb-11. Normalize separators to `/` in stored relative paths, and convert on load.** R-dd-6's
no-separator rule existed to dodge the macOS-versus-Windows separator problem; now that relative paths may
have directories, normalizing is how that problem stays dodged. This is the git/URI convention and it is the
part most likely to be skipped, because it only fails when a workspace crosses platforms.

**R-stb-12. Mark external references in the Datasets list.** R-dd-4's rows already carry a status column;
an outside-the-workspace source shows as **external**. A user about to share a workspace can then see which
sources will not travel — without that, the failure surfaces on someone else's machine as a missing file with
no explanation.

On another machine an unresolvable external reference is exactly R-res-5's missing case: **report by name,
preserve trace configuration, allow re-pointing.** The Datasets list is already the place that happens, so
recovery needs no new mechanism — which is the strongest argument for R-stb-8a's single surface.

**R-stb-13. Dropped files are references — do not copy them into the workspace**, and do not put them in
`results/`. `results/` holds simulation output the owner intends to delete freely in Finder; a referenced
measurement is an input and must not be swept up by that. If §2.2 has an existing convention for where
references are recorded, follow it and report what it is.

**R-stb-9. Every `.sNp` with N ≥ 2 gets the same card**, with the port selectors active for N > 2. That is
the case the selectors exist for, and it is also the natural way to compare a measured N-port against a
simulated one. Touchstone parsing for arbitrary N already exists — this adds no reader work.

## 6. Optional: the same maths as expression functions

The owner's first instinct was built-in functions. **Implement the mathematics once (R-stb-1), expose the
card as the discoverable route, and optionally surface the same functions in the expression engine** for
users composing them inside a larger measurement expression.

The card is what makes it usable; the functions are what make it composable. **Do not make the functions the
only route** — typing a stability expression correctly is exactly the friction the card removes.

## 7. Guardrails

- Do not write a second implementation of μ, μ′, K or |Δ| (R-stb-1).
- Do not skip renormalization when a test cube is uniform 50 Ω (R-stb-2).
- Do not extend `SNP` beyond uniform reference impedance (R-stb-2).
- Do not auto-add stability traces to every 2-port run — the card is the entry point.
- Don't touch `src/Engine` numerics beyond adding passivity beside the existing stability code.

## 8. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Cube path matches SNP path (R-stb-1)** — the same 2-port data as a simulated cube and as a Touchstone
   file produces **identical** μ, μ′, K and |Δ| within tolerance. This is the headline test: it proves one
   implementation is being used and validates the cube path against the already-trusted one.
3. **Renormalization (R-stb-2)** — a cube with **per-port, complex** reference impedances produces correct
   stability; assert against a hand-renormalized reference. A test using only uniform 50 Ω does not exercise
   this.
4. **Port selection (R-stb-3/3a/3b)** — an N-port cube containing two devices yields different, correct μ for
   input/output = (1,2) and (3,4). **Swapping to (2,1) changes μ and μ′** as expected — assert this, since it
   is what proves the selection is ordered rather than an unordered pair.
4a. **Any N (R-stb-3b)** — the same path handles a **3-port**, a **5-port** and a **12-port** with no per-N
   branching; assert at least one odd N and one N > 8 so a hardcoded 2/4 cannot pass. **N = 1 disables the
   card with a reason.**
5. **Plot-kind gating (R-stb-5)** — the card offers circles only on a Smith plot and scalars only on a
   rectangular plot; the unavailable option is disabled **with a reason**.
6. **Passivity (R-stb-6)** — a known passive network gives `σ_max ≤ 1` at every frequency; an active one
   exceeds 1; whole-network passivity on a 4-port differs from the extracted (1,2) sub-matrix where expected.
7. **Touchstone source (R-stb-7/8/9)** — a dropped `.s2p` and an `.sNp` with N > 2 appear in the source
   picker, carry aliases, and expose the same card; the port selectors work on the multiport.
8. **Portability (R-stb-10/11/12)** — a `.cdd` referencing a Touchstone file **inside** the workspace stores a
   **relative** path with `/` separators and survives moving the workspace; one referencing a file **outside**
   stores an absolute path and is shown as **external** in the Datasets list. Opening a moved workspace on a
   machine where the external file is absent reports it by name and **preserves trace configuration**.
9. **No copying (R-stb-13)** — dropping a Touchstone file does not copy it into the workspace and does not
   place anything in `results/`.

## 9. On completion

Record in `src/Ui/CLAUDE.md`: **that stability mathematics has exactly one implementation**, shared by the SNP
and cube paths; **R-stb-2's renormalize-to-uniform-real step** as the substantive difference between the two
paths, and that Touchstone never needed it; that the **ports are user-selected and ordered** (input/output)
because an N-port contains many candidate 2-ports and because swapping them swaps μ and μ′ — which is why
auto-computation was rejected; that **any N ≥ 2 is supported with no per-N branching**; the passivity
definition and that it is **not** 2-port-limited (and is even valid at N = 1, which the card currently
excludes); and where dropped Touchstone files are stored, with the reason they are kept out of `results/`.
