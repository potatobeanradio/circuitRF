# Sonnet Brief — Phase GI2: the stackup skeleton

**Series:** `docs/sonnet-briefs/brief-gi-series.md`. Second of five.
**Consumes:** L4g (orchestration), the via/stackup work, and GI1's plating field.
**Touches:** `src/Design/Layout/Interchange/GerberStackupMapping.cs`,
`src/Design/Layout/Interchange/GerberImport.cs`, `src/Design/Layout/TechValidation.cs`.

**Test loop:**
```
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

---

## 1. The state this phase exists to end

An import of a six-layer set with no job file identifies six copper layers, works out their order top
to bottom, binds each to a drawing layer, reports all of it in words — and then writes a technology
whose stackup contains **two via entries and nothing else**. The Technology editor then opens on a
warning the import produced about a document the import itself authored:

> The stackup names a via layer but no conductor layers, so nothing says what the via connects or what
> the board is made of.

The person who imported the board is left to hand-author eleven stackup rows, in the right order, with
the right drawing-layer bindings, reproducing by hand a result the importer had already computed and
discarded.

## 2. Why the current refusal is right and still wrong

`GerberStackupMapping.cs:51-63` bails out completely when there is no job file, and its own header
explains why in terms that are not being relaxed here:

> Never infer permittivity from a material name … An invented stackup is worse than none, because
> nothing downstream will ever question it and it WILL be simulated.

That is correct and it is the load-bearing rule of this phase. The error is a conflation. Two different
things are being refused together:

| | Is it in the files? | Currently |
|---|---|---|
| How many conductors, in what order, called what, bound to which drawing layer | **Yes — already resolved** | discarded |
| Conductor thickness, dielectric thickness, εᵣ, tanδ | **No** | correctly refused |
| Conductor conductivity | Not stated, but it is copper | correctly defaulted *in the job-file branch only* |

Only the middle row is a substrate. The first row is bookkeeping the import has already done, and the
third has an established precedent two files away: `PcbStackupMapping.cs:87` writes
`DefaultCopperConductivitySm` and names it as a default in its own message (R-L4d-7), and
`GerberStackupMapping.cs:95` does the same thing in the branch that *does* build a stackup. Copper's
conductivity is physics, not a guess about this board.

**R-gi2-1. Emit the structure; refuse the values.** With no job file, build the stackup's *shape* from
what the identity cascade resolved, and leave every quantity that describes the substrate unset.

## 3. What a skeleton contains

**R-gi2-2. One Conductor entry per resolved copper layer, in the resolved top-to-bottom order.** Named
as its drawing layer is named, binding that drawing layer, `SigmaSm = DefaultCopperConductivitySm`
(through the same constant `PcbStackupMapping` already exposes — do not introduce a second copy),
`ThicknessDbu = 0`.

**R-gi2-3. One Dielectric entry between each adjacent pair — N conductors give N−1 dielectrics.**
`ThicknessDbu = 0`, `TanD = 0`, and **`Epsr = 0`**.

`Epsr = 0` is deliberate and is the crux of the whole phase. `StackupLayer.Epsr` defaults to `1.0`,
which is **air** — a perfectly valid, entirely simulatable substrate, and exactly the fabrication
R-L4g-9 forbids. A skeleton that ships `Epsr = 1.0` is worse than the empty stackup it replaces,
because it will run. `Epsr = 0` is already outside `TechValidation`'s `Epsr < 1` check and outside every
extractor's own refusal, so it is the spelling of "unset" that the codebase already treats as
unusable. **Zero thickness plus zero permittivity is what makes a skeleton unsimulatable, and that is
the point of it.**

**R-gi2-4. A skeleton must be unsimulatable, and there must be a test that says so.** Assert directly
that `CrossSectionExtractor` and `PlanarExtractor` refuse a freshly imported skeleton, with a sentence
naming what is missing. A test that asserts a skeleton *runs* would be asserting the bug this phase is
carefully not introducing.

**R-gi2-5. Name dielectrics neutrally and positionally.** "Dielectric 1", or the pair of conductors it
separates. **Never** "core", "prepreg", "FR-4" or any laminate name — the number and construction of
the layers between two copper sheets is a fabrication decision that no artwork file states, and a
plausible name is the thing that stops someone checking.

**R-gi2-6. Soldermask, paste and legend layers are not stackup entries.** A set commonly carries mask
artwork on both sides. Mask is genuinely a dielectric in the physical stack, but its presence as
*artwork* states neither its thickness nor its permittivity, and adding it silently changes the
conductor-to-conductor geometry a solver sees. Leave it out and say so, once: *"soldermask and paste
drawing layers were imported as artwork and are not in the stackup; add them if your run needs them."*

**R-gi2-7. The via entries now have conductors to name.** `GerberImport.cs:622-631` already sets
`SpanFromLayer`/`SpanToLayer` from the stackup's own conductor entries when there are any — with a
skeleton there always are, so a through-hole set spans the topmost to the bottommost conductor and
says so. GI1's plating field and GI3's wall-thickness default apply here unchanged.

**R-gi2-8. The job-file branch is untouched.** A set with a `.gbrjob` still takes that file's stackup
whole, in its own order, with its own thicknesses. The skeleton is the *no job file* branch only, and a
job file that carries a stackup must never be topped up from the skeleton — a partial job-file stackup
is a statement about the board and the skeleton is not.

## 4. The validator has to change with it, or this makes things worse

`TechValidation.Analyze` opens with a deliberate simplification (`TechValidation.cs:26-32`):

> a stackup carrying vias but no conductors is reported once as the missing stackup rather than three
> times per via … a real imported board produced 22 messages describing 2 facts, which is a wall of
> text nobody reads.

A skeleton has conductors, so `stackupIsSubstrateless` goes false and every per-row check re-engages at
once: eleven "non-positive thickness" problems, five "εᵣ < 1" problems, one "no ground reference". That
is the 22-message wall arriving by a different door, and it would make the skeleton a regression.

**R-gi2-9. Recognise the skeleton state and report it once.** Where `stackupIsSubstrateless` is today,
add a second state: a stackup whose entries exist but whose substrate values are unset. Report **one**
`TechProblemArea.Stackup` problem naming the counts and the fields — *"6 conductors and 5 dielectrics
were created from the artwork; N still need a thickness and M still need εᵣ and tanδ"* — and suppress
the per-row thickness and permittivity problems it summarises. Per-row problems return the moment a row
is partly filled in, so someone working down the list sees their remaining work shrink.

**R-gi2-10. The "no ground reference" problem stands.** It is not part of the skeleton summary: it is a
real, separate decision that no artwork file can make, and a microstrip cannot resolve a substrate
without it. Leave it reported on its own.

**R-gi2-11. Do not report a skeleton as an error when it is being edited into.** The distinction that
matters is *unset* versus *wrong*. Zero thickness on a freshly imported row is unset; a negative
thickness someone typed is wrong. Keep them separable in the message.

## 5. What the import says

**R-gi2-12. One paragraph replaces the current "left EMPTY" message**, and it must still say plainly
that no substrate was invented. Shape:

> Six conductor layers and five dielectric layers were created from the artwork, in the order the copper
> files were resolved into, each bound to its drawing layer. **No substrate was invented**: every
> thickness is zero and every dielectric's εᵣ and tanδ are unset, because an individual Gerber file
> states nothing about the substrate at all. Conductor conductivity is defaulted to 5.8e7 S/m (copper)
> and is named here as a default. The technology cannot be simulated until the missing values are
> entered on the Technology editor's Stackup tab.

## 6. Gates

1. **Structure without values** — a six-copper set with no job file yields exactly 6 Conductor and 5
   Dielectric entries, top to bottom in the resolved order, each conductor bound to its own drawing
   layer, every `ThicknessDbu` zero, every dielectric `Epsr` zero.
2. **Unsimulatable** (R-gi2-4) — both extractors refuse it, naming what is missing.
3. **Not air** — assert `Epsr == 0`, explicitly, with a comment saying why `1.0` would be a bug. This
   is the test most likely to be "corrected" by someone later.
4. **One message, not twenty-two** (R-gi2-9) — `TechValidation.Analyze` on a fresh skeleton returns a
   bounded number of Stackup problems; assert the count, not just the content.
5. **Progressive** — filling in one dielectric's thickness and εᵣ reduces the counts in the summary and
   raises no new problems.
6. **Job file still wins** (R-gi2-8) — the same set plus a `.gbrjob` produces that file's stackup,
   bit-identically to today. A snapshot comparison, not a spot check.
7. **Vias resolve** (R-gi2-7) — the via entries name real conductor layers and the validator reports no
   unknown-span problem.
8. **Masks excluded** (R-gi2-6) — a set with soldermask top and bottom yields no mask stackup entry and
   one message saying so.
9. **Round trip** — the resulting `.ctech` reloads through `TechPersistence` with every field intact,
   including the zeros.

## 7. On completion

Write **"Phase GI2 — COMPLETE"** at the top of `src/Design/RESOLVED.md` — **not** `CLAUDE.md`. Record:

1. **How many rows a skeleton actually saves** on the largest set on hand — the honest measure of this
   phase is "entries created" against "values still required", both as numbers.
2. **Whether `Epsr = 0` survived contact with the rest of the tree.** Every consumer of `Stackup` was
   written against a default of `1.0`; name any that treated zero as air, as a divide-by-zero, or as
   valid. This is the phase's real risk and the finding worth carrying forward.
3. **What the validator's message count went to**, before and after, on a skeleton — the number that
   says whether §4 did its job.
4. **Anything the artwork turned out to state about thickness that this phase left on the table.**
