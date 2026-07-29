# Sonnet Brief — Housekeeping: tear-off theming, palette entries, TermG, MKlopf console, repo merge, test fixtures

Seven owner items. Two need investigation before code (§1, §5); one is a repository operation with a
prerequisite worth checking first (§6).

Gate command is plain `dotnet test`.

---

## 1. Bug: a torn-off `.cdd` canvas background changes shade

The data-display canvas renders a slightly different grey when the document is torn off than when docked.

**R-hk-1. The leading hypothesis is resource scope, and it should be checked before anything else.** In
Avalonia, `DynamicResource` lookups walk the **visual tree**. A torn-off window is a *separate* visual tree,
so a brush defined on the main window's resources — rather than in `App.Resources` or a merged theme
dictionary — will not resolve there and silently falls back to a default. A small shade difference is exactly
what that produces.

This is the same family as the `$parent[Window]` binding gotcha already documented in `src/Ui/CLAUDE.md`,
and it is worth confirming by inspection rather than by adjusting colours until they match.

**Determine which surface is actually wrong.** The owner notes he cannot tell whether the canvas got darker
or the surrounding window got lighter. Compare the resolved brush values in both trees rather than trusting
the eye — one of them is falling back, and which one it is determines the fix.

**R-hk-2. Fix by moving the resource to application scope, not by hard-coding a colour.** A literal colour in
the torn-off path would drift the moment the theme changes, and would break light/dark switching. If the
resource genuinely belongs to the document rather than the app, the torn-off window must merge the same
dictionary the docked host does.

**Check the other tear-off-capable editors** — schematic, symbol, layout — for the same discrepancy while
there. If they share the defect, say so; it is one fix, not four.

## 2. Explicit S1P/S2P/S3P, Z1P/Z2P/Z3P, SDD1/SDD2/SDD3 palette entries

The dynamic `SNP`, `ZNP` and `SDD` components already accept an arbitrary port count. These are additional
**entry points**, not new components.

**R-hk-3. A placed S2P must be an `SNP` with N = 2 — the same type, not a parallel one.** The palette entry
presets the port-count parameter and nothing else. A second type would fork the netlist path, the `.csch`
round-trip, the parameter editor and every future change to the model. The user gains discoverability; the
codebase gains one line of palette metadata per entry.

**Display name should follow the port count** so a placed component reads as `S2P` rather than `SNP`,
matching what the user picked from the palette. If the display name is derived rather than stored, changing
the port count afterwards renames it automatically — which is the correct behaviour.

**R-hk-4. `Z1P` gets a `Terminals` filter keyword**, per the owner. Only `Z1P` was specified; do not
speculatively add it to the others.

## 3. Microstrip components need the `Transmission Line` filter keyword

**R-hk-5. `MLIN`, `MTEE`, `MCROSS`, `MBEND`, `MTAPER`, `MKLOPF` — and any other microstrip component —
appear when the palette is filtered by `Transmission Line`.**

Check what `TLIN` already carries and match it, so the keyword string is identical rather than merely
similar. A near-miss (`Transmission Lines`, `TransmissionLine`) produces a filter that silently matches
nothing.

## 4. New component: `TermG`

A convenience component: `Term` with port 2 permanently grounded, presenting as a **1-port**.

**R-hk-6. Reuse `Term`'s model with node 2 tied to ground — do not duplicate the model.** Same principle as
§2: `TermG` is a packaging convenience, and any divergence between two copies of the termination model is a
bug waiting to happen.

**R-hk-7. Reuse `Term`'s symbol glyph exactly, and place the existing ground glyph at the port-2 location.**
Do not redraw either, and **do not resize the symbol**.

**R-hk-8. The placed size must match `Term` + `GND` placed separately.** This is the constraint that makes
the component feel native rather than bolted on: the ground glyph sits precisely where a `GND` wired to
port 2 would sit, so the combined bounding box is identical. Assert it — a symbol that is *nearly* the right
size is more jarring than one that is obviously different.

The remaining port keeps `Term`'s port-1 identity, so a schematic that swaps `Term`+`GND` for `TermG` is
electrically identical.

## 5. Bug: MKlopf still prints to the terminal

The Messages-window path now works and must stay. Console output persists.

**These four have already been checked and are clean — do not re-investigate them:**

| | Status |
|---|---|
| `MicrostripKlopfModel.cs` | no `Console.` calls remain |
| `MicrostripValidity.cs` (`MicrostripValidityReporter`) | queues via `Drain()`; its doc comment explicitly records that it no longer writes to `Console.Error` |
| `MicrostripCascadeSectioning.cs` | no `Console.` calls |
| `MKlopfPCell.cs` | no `Console.` calls |

**R-hk-9. The remaining candidates, in order:**

1. **A stale build.** The message the owner quoted earlier carried the doubled `MKLOPF:MKLOPF:` prefix and
   `N = 4096` — both of which the previous brief eliminated. Rule this out first, cheaply, before hunting.
2. **The warning *consumer*.** Something drains `ElaboratedNetlist.Warnings` into Messages; if the simulation
   runner *also* echoes them to stdout, that is the leak — and it would affect every warning, not just
   MKlopf's. Follow `IReportsWarnings` to its consumers.
3. **Another component or engine path** writing directly to the console.

**R-hk-10. Whatever the source, fix it at the single point where warnings leave the engine, not per call
site.** The previous brief's lesson was that the leak was one shared channel, not two call sites; if there is
a second echo, it is the same shape of problem.

Report which of the three it was.

## 6. Merge `RfCore` into `circuitRF`

The owner wants one repository — `splotRF` is being retired, and two repos confuse new contributors.

**R-hk-11. Establish first whether `RfCore` is currently a git submodule.** `circuitRF/.git/config` contains a
`[submodule] active = .` section, which strongly suggests it is. That changes the procedure entirely:

- **If a submodule:** `git submodule deinit`, remove the entry from `.gitmodules` and `.git/config`,
  `git rm` the submodule path, then bring the files in.
- **If merely a sibling checkout referenced by path:** the project references change and nothing else.

**Report which, before doing anything.**

**R-hk-12. Preserve `RfCore`'s history if it is worth preserving — decide explicitly.** `git subtree add`
retains it; copying the files does not. The owner has not said, and it is not reversible after the fact.
**Ask rather than assume**, and note that a merged history makes `git log` on the combined repo noticeably
busier.

**Also required:**

- **Solution and project references** updated from the submodule/external path to the in-repo path.
- **`.gitignore` and Git LFS** — `circuitRF/.git/config` shows LFS in use. If `RfCore` tracks anything via
  LFS, its `.gitattributes` patterns must come across or those files arrive as pointer text.
- **CI and build scripts** that reference the submodule or a sibling path.
- **`README.md` Getting Started §2** — the owner named this explicitly. It presumably tells a new developer
  to clone two repositories; it should now describe one.

**R-hk-13. The gate for this section is that a fresh clone builds and tests green.** Not "it builds on the
machine where the merge happened" — clone into a clean directory and run from there. A stale local reference
to the old path will otherwise mask a broken checkout for everyone else.

## 7. New developers cannot run `dotnet test` — missing `.spl` and `.lpcwave` fixtures

The raw test data is not in the repository, so tests that need it fail rather than skip.

**R-hk-14. Tests skip with a stated reason when a fixture is absent; they never fail.** A fresh clone must
produce a green `dotnet test` whose output says plainly which tests were skipped and why. A red suite on
first run teaches a new contributor that the suite is unreliable, which is a much more expensive lesson than
a few skipped tests.

The skip message must name **the missing path** and **how to obtain it** — a skip the reader cannot act on is
only marginally better than a failure.

**R-hk-15. Consider committing the fixtures via Git LFS, since LFS is already configured** (`[lfs]` in
`.git/config`). That is the outcome that actually lets a new developer run the full suite, and the
infrastructure cost is already paid.

**Decide and record why**, because the right answer depends on facts not stated here:

- **Size** — if they are large, LFS handles it; if they are enormous, skipping may still be right.
- **Licensing** — if the data is customer or vendor material, it must not be committed at all, and
  skip-with-reason is the only option.
- **Reproducibility** — if the fixtures can be *generated*, a small generator committed to the repo beats
  either option: no large binaries, and every developer gets full coverage.

**R-hk-16. Do not solve this by excluding the tests with a category filter.** That was the right tool for
slow benchmarks, where the tests still run on demand and everyone knows they exist. Here it would silently
reduce coverage for every new contributor, who would never learn the tests were there. Skipping is visible;
filtering is not.

Document whatever is chosen in the README beside the Getting Started changes from §6 — a new developer meets
both problems in the same five minutes.

## 7A. Remove the FET component

The SDD-based `FET` library component is no longer used. Because it is an SDD underneath, anything relying on
it can be replaced by an equivalent SDD.

### 7A.1 What has already been checked

**The demo workspace does *not* use the library FET, and it already demonstrates the replacement.**

- `circuitRF_demo/FET_curve_tracer/schematic/FET_curve_tracer.csch` places `Vdc`, `IProbe`, `Ground`, `Var`,
  `Meas` and one `Generic` — a **cell instance**, not a library FET.
- That instance resolves to `circuitRF_demo/MyFET`, whose schematic is built from **`Sdd` directly**
  (`Pin` ×2, `Ground` ×2, `Var`, `Sdd`).

So `MyFET` is a hand-built, SDD-based FET cell — exactly the pattern this removal steers users toward. It is
worth naming as the worked example rather than writing a new one.

No `*Fet*` or `*FET*` source files exist under `src/`, so the component is almost certainly registered in a
shared component table rather than owning a file.

### 7A.2 Verify before removing

**R-hk-17. Grep every committed `.csch` for the FET symbol string before deleting anything.** The schematic
format identifies components by a `"Symbol"` field (`"Sdd"`, `"Vdc"`, `"Ground"`…), so the check is exact
rather than approximate. Cover the demo workspace, any example or fixture designs, and the test tree.

If the repository is clean, removal is straightforward. If it is not, §7A.4 applies.

### 7A.3 Migrate tests by proving equivalence, not by eyeballing it

**R-hk-18. Capture each FET-based test's output *before* removal, then assert the SDD replacement reproduces
it bit-identically.** This is the whole safety argument: if `FET` really is a pure SDD wrapper, the numbers
must match exactly — not within a tolerance.

**A mismatch is a finding, not a rounding problem.** It would mean the FET carried behaviour beyond the SDD
it wraps — default parameters, a topology detail, an extra internal element — and the removal needs
rethinking rather than a loosened tolerance. **Report it rather than adjusting the assertion.**

### 7A.4 Hard removal — decided

**R-hk-19. The owner has chosen hard removal. Do not add a load-time alias or any compatibility shim.**

The accepted consequence: **any saved schematic referencing `FET` will no longer load.** That is fine — but
it makes the *failure mode* matter, because it is now a real path a user could hit.

**R-hk-19a. Verify that an unknown `"Symbol"` value fails gracefully and explanatorily, and fix it if it does
not.** Loading a schematic that names a component the application no longer knows must:

- **report the unknown component by name**, so the user learns *what* is missing rather than that "the file is
  broken";
- **not crash, and not silently drop the component**, which would leave a design that opens looking subtly
  wrong;
- ideally load the rest of the schematic so the damage is visible and bounded.

This is the same principle the owner set for the instance picker — a visible, explanatory statement beats an
invisible absence. **Check the current behaviour rather than assuming it is already good**; if it is, say so,
and if it is not, that fix belongs with this removal rather than after it.

The completion note should still record the R-hk-17 result — whether anything in the repository referenced
`FET` — since that determines whether any committed file needed updating alongside the removal.

### 7A.5 Scope of the removal

The palette registration, the model, its tests (migrated per §7A.3), and any documentation or example
referencing it. **Do not touch `Sdd` itself**, and **do not modify the demo workspace** — §7A.1 shows it does
not depend on the component being removed.

## 8. Guardrails

- §2 and §4 add **entry points and packaging**, never parallel models (R-hk-3, R-hk-6).
- Do not resize `Term`'s glyph (R-hk-7).
- Do not hard-code a colour to fix §1 (R-hk-2).
- Do not begin §6 before answering R-hk-11 and R-hk-12.
- Do not remove `Sdd`, and do not edit the demo workspace, as part of §7A.
- Don't touch `src/Engine` numerics or `RfCore`'s contents as part of the move — §6 relocates files, it does
  not edit them.

## 9. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Tear-off background (R-hk-1/2)** — a `.cdd` canvas renders the same background docked and torn off, in
   both light and dark themes. Assert the resolved brush, not a screenshot.
3. **Palette entries (R-hk-3)** — placing `S2P` yields an `SNP` instance with N = 2; it round-trips through
   `.csch` as that type; the netlist is identical to placing `SNP` and setting N = 2 by hand.
4. **Display name** reads `S2P`, and follows the port count if it is changed afterwards.
5. **Filters (R-hk-4/5)** — `Z1P` appears under `Terminals`; every microstrip component appears under
   `Transmission Line`, using the identical keyword string `TLIN` uses.
6. **`TermG` electrical (R-hk-6)** — s-parameters identical to `Term` with port 2 wired to `GND`.
7. **`TermG` geometry (R-hk-8)** — the placed bounding box matches `Term` + `GND` placed separately; the
   `Term` glyph is unchanged and unscaled.
8. **No console output (R-hk-9)** — an S-parameter simulation with MKlopf produces the Messages entries and
   **nothing on stdout or stderr**. State which of the three candidates it turned out to be.
9. **Fresh clone (R-hk-13)** — clone into a clean directory; build and `dotnet test` both green.
10. **Fixtures (R-hk-14)** — with the data absent, the suite is **green with skips**, and each skip names the
    missing file and how to get it.
11. **README** — Getting Started describes one repository, and covers the fixture situation.
12. **FET removal (R-hk-17/18)** — no `.csch` in the repository references the FET symbol; every migrated
    test asserts **bit-identical** results against the pre-removal FET output; the palette no longer offers
    it; `Sdd` and the demo workspace are untouched. **No compatibility alias exists** (R-hk-19).
12a. **Unknown component loads gracefully (R-hk-19a)** — a hand-crafted `.csch` naming `FET` reports the
    unknown component **by name**, does not crash, does not silently drop it, and the rest of the schematic
    still loads.
13. **Demo still runs** — `circuitRF_demo/FET_curve_tracer` opens and simulates unchanged, since `MyFET` is
    SDD-based and independent of the removed component.

## 10. On completion

Record in `src/Ui/CLAUDE.md`: **what actually caused §1** (which visual tree was falling back, and where the
resource now lives); that §2 and §4 are packaging over existing models with **no second type**; **which of
R-hk-9's three candidates was printing to the console**, since the previous brief believed that path closed;
the answers to **R-hk-11 and R-hk-12** (submodule or not, history preserved or not); and the **§7 decision
with its reasoning**, because the next person to add a fixture-dependent test needs to know the convention.
