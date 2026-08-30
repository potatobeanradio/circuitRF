# Brief MIM-2 — the GaAs starter technology gains MIM capacitor support

**Problem.** No shipped technology can state a MIM capacitor, so every capability the rest of this
series builds has no in-tree structure to run on. The GaAs starter is the natural host: it already
carries `Cap Dielectric` and `Nitride` DRAWING layers (unbound to any stackup entry), a two-metal
stack with an airbridge post via, a backside via to a ground-designated back metal — everything a
shunt MIM needs except the plate itself.

**The two representations must move together.** The authoritative artifact is the authored file
`src/Ui/resources/technologies/mmic-GaAs_2LM_100um.ctech` (embedded resource, parsed through
`TechPersistence` — R-misc-6: shipped technologies are never transcribed into C#). Separately,
`StarterTechnologies.MmicGaAs()` builds the same starter in code for tests. Extend BOTH, keep them
in step, and say so in the write-up; if they have already drifted, report the drift rather than
papering over it.

Read first: `src/Ui/Layout/StarterTechnologies.cs` (`MmicGaAs`, and the header's "differ only in
data" rule); `src/Ui/Layout/ShippedTechnologies.cs`; `src/Design/Layout/TechModel.cs`
(`StackupLayer`, `SpanFromLayer`/`SpanToLayer`); `src/Design/Layout/Em/PlanarExtractor.cs` (level
selection, `BuildVias`); `brief-em-mim-1-region-vias.md` (the capacitor via is a REGION — this
technology is authorable before MIM-1 lands but only runnable after).

## The stackup, top to bottom

| Entry | Kind | Values (generic textbook figures, no process implied) |
|---|---|---|
| Metal2 | Conductor | unchanged (3 µm, σ 4.1e7) |
| Air | Dielectric | **2.55 µm** (was 3 µm — the two new entries below take the difference, keeping Metal2's height) |
| MIM Metal | Conductor | 0.25 µm, σ 4.1e7, bound to a NEW drawing layer `MIM Metal` |
| MIM Dielectric | Dielectric | 0.2 µm, εr 6.8, tanδ 0.001 (silicon-nitride-class; ≈ 0.30 fF/µm²) |
| Metal1 | Conductor | unchanged |
| GaAs | Dielectric | unchanged (100 µm, 12.9) |
| Backside Metal | Conductor | unchanged, ground reference |

New Via entry: **`MIM Via`** — `SpanFrom = "MIM Metal"`, `SpanTo = "Metal2"`, solid fill, bound to
a NEW drawing layer `MIM Via`. The existing `Backside Via` (Metal1 → Backside Metal) and
`Metal1-Metal2 Post` entries are untouched.

**The stackup entry is named `MIM Dielectric`, deliberately NOT `Cap Dielectric`.** The starter has
always carried `Cap Dielectric` and `Nitride` DRAWING layers, unbound and ignored, and they stay
exactly that — mask-documentation artwork a process's deck would carry. A stackup DIELECTRIC is a
different kind of thing entirely: it is never drawn, has no artwork, and is laterally infinite by
the 2.5D premise (§10.12 of the design note). Physically the nitride exists only where the process
leaves it — embedded in the interlayer dielectric, under and just beyond the plates — but the model
carries it as a full 0.2 µm sheet at its true height: exactly right beneath the plates (where the
capacitor's field lives), and a negligible perturbation to any interconnect elsewhere in the stack,
which is the standard trade in the planar-EM class of tool. Giving the two different names is what
stops the next reader asking which one the solver reads. State all of this in the technology's own
description note.

**Both capacitor forms are now expressible.** Shunt: bottom plate on Metal1 over one or more
backside vias; top plate on MIM Metal; feed lands on Metal2 through a `MIM Via` region. Series:
feed in on Metal1 (which IS the bottom plate), out on Metal2 through the plate via. One stated
consequence to document with the technology: with MIM Metal selected as an analysis level, the
`Metal1-Metal2 Post` spans non-adjacent levels and is dropped with the extractor's own
`notAdjacent` note — exclude MIM Metal from that analysis, or don't mix airbridge posts and
capacitor plates in one EM setup.

## Milestones

1. The `.ctech` file and `MmicGaAs()` extended as above; the technology validates
   (`TechValidation`) and round-trips.
2. Three extraction fixtures in `tests/Ui.Tests`, built in code on this technology — a shunt cap,
   a series cap, and **two series caps joined by a Metal1 line (the MMIC acceptance shape — a
   capacitor is never used alone)** — asserting the extracted levels, the region via footprints
   (after MIM-1), the backside-via ground attachment, and the extractor notes. Nothing in the
   extractor is per-capacitor — every shape on a selected level and every via region is taken — so
   the two-cap fixture asserts a fact that should already hold; if it does not, that is a finding.
3. One small raw-solve smoke test (plates ~10 × 10 µm, coarse mesh, one frequency): reciprocity
   and passivity hold, and |Y11| at the lowest frequency is within a LOOSE band (±25%) of the
   parallel-plate ε₀εᵣA/d — a wiring gate, not an accuracy gate. Accuracy is MIM-3's job; do not
   tighten this band here.
4. The existing starter's consumers still pass unchanged (`ForeignDocumentsTests`,
   `NewWorkspaceTechnologyPickerTests`, DRC and palette tests) — adding entries must not disturb
   microstrip substrate resolution for the existing layers (Metal1/Metal2 lines still resolve
   against Backside Metal; assert it).

## Must NOT

- Rename or renumber any existing layer, entry or DRC rule — user workspaces copy this file at
  creation and diffs should read as pure additions.
- Name any foundry, process or vendor kit anywhere in the file, the code or the tests.
- Add a PCell for the capacitor — drawn-artwork examples only; a MIM PCell is its own future brief.

## Gates

Milestones 2–4 as tests; `dotnet test tests/Ui.Tests` green. Write-up in `src/Ui/RESOLVED.md`;
the user-facing technology/starter documentation page gains the MIM rows and the laterally-infinite
dielectric approximation note, kept consistent with `docs/design/mom-engine.md` §10.12.
