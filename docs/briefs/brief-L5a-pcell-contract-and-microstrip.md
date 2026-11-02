# Sonnet Brief — Phase L5a: the PCell contract and the microstrip component library

**Design:** **`docs/design/pcell-contract.md` — the contract now lives there as an official design doc; this
brief implements it.** Read it first. Also `docs/design/layout-view.md` §9 (schematic→layout), §10.4
(stackup), §10.9 (validation oracles), §3.1 (primitives); `docs/design/expressions.md`;
`docs/design/workspace-and-project-tree.md` §2 (`.ccell` parameters), §5A.2 (technology resolution).
**Consumes** all of L0–L4.

**Rule numbering:** the design doc's **R1–R12** are authoritative. This brief's `R-pc-*` numbers map onto
them one-for-one and exist only for convenience while implementing; if the two ever disagree, the design doc
wins and the brief is wrong.

**Why this comes before L5.** §9's schematic→layout resolves each cell to a **static** `.clay` layout view.
MLIN's artwork is parametric — its geometry depends on W and L — and no stored file can express that. So L5
needs parametric cells to be worth demonstrating, and parametric cells need a contract. This phase builds
both; L5 then places them.

**Two things are deliberately separated:**
- **The PCell contract** — the durable interface, designed now because user-authored PDKs will depend on it.
- **The PCell host** — how a *third party* writes one. **Not decided here.** The built-in components in §3
  are plain C#, and the component library is not blocked on choosing a scripting language.

Gate command is plain `dotnet test`.

---

## 1. The PCell contract

**R-pc-1. A PCell is a cell whose layout view is generated rather than stored.** It is a real cell — it has a
folder, a `.ccell`, parameters, a symbol, a project-tree node — so hierarchy, instances, arrays, push-in and
the L3a geometry cache all work unchanged. Only `CellFolder.ResolvePrimary(…, ViewType.Layout)` learns a new
answer: *"this cell's layout is generated."*

### 1.1 Signature

```
Generate(parameters, technology) → { shapes, pins }
```

- **Parameters** come from the cell's `.ccell` — **the same list the symbol shows.** One parameter set, not
  two. A symbol displaying `W` and a generator reading a different `W` is a defect waiting to happen.
- **Technology** is the resolved `.ctech` (§2), supplying the substrate.
- **Shapes** are ordinary `LayoutShape`s in DBU on technology-named layers — no new geometry type.

### 1.2 Pins are part of the output, not an afterthought

**R-pc-2. A PCell returns pins alongside shapes.** Geometry with no pins cannot be connected: §9 stamps nets
onto instance pins, abutment needs to know where the line ends, and §10.6's EM ports attach to them. A pin
carries **name** (matching the symbol's pin), **location**, **layer**, and **width and outward direction** —
the last two because a microstrip connection is an *edge*, not a point, and a bend needs to know which way
its arm faces.

This is the single easiest part of the contract to leave until later and the most expensive to add
afterwards, because every PCell written before it would need revisiting.

### 1.3 Origin and orientation

**R-pc-3. Pin 1 sits at the origin, and the cell's principal axis runs along +X.** State it, because
otherwise every author picks their own and nothing abuts. For MLIN that means the origin is the left-hand end
with the line running right; for MBend, the input arm; for MCross, the centre with arm 1 along +X.

### 1.4 Purity, determinism, and caching

**R-pc-4. `Generate` is pure and deterministic.** Same parameters and technology, byte-identical output,
always. No file reads, no clock, no global state, no RNG.

This is not stylistic. **L3a's per-cell geometry cache (R-L3a-3) must now key on
`(cell, parameter values, technology)`** — and an impure generator breaks that cache *silently*, producing
stale or inconsistent geometry with no error anywhere. It is the same class of failure as a stale technology
resolution, and equally hard to trace.

**R-pc-5. Evaluate once per unique parameter set, never per placement.** A 50×50 array of one PCell
evaluates once and draws 2,500 times, exactly as R-L3a-3 requires for static cells. The caching argument
survives the PCell addition rather than being defeated by it.

### 1.5 Units at the boundary

Parameters are expression values — `Real`, dimensionless as far as the engine is concerned — while geometry
is integer DBU.

**R-pc-6. PCell length parameters are SI metres; the generator converts to DBU at its own boundary with one
documented rounding rule.** Fix the rule (round-half-away-from-zero) and put the conversion in **one**
helper, so two generators can never disagree about where a 2.9 mm edge lands. The parameter editor accepts
unit-suffixed entry (`2.9mm`, `115mil`) through `LayoutUnits.TryParse`, as everywhere else.

### 1.6 Versioning

**R-pc-7. The contract carries a version, from day one.** A PDK will eventually declare which contract
version it targets, and a version field costs nothing now and cannot be retrofitted once third-party cells
exist. Even with one built-in version today, write it down.

## 2. Technology and substrate resolution

**R-pc-8. A microstrip component resolves its substrate from the workspace technology's stackup** — no
legacy-style `MSUB` block referenced by name per instance. Open a PCB workspace, drop an MLIN, and it already
knows FR-4 1.6 mm. Resolution follows `workspace-and-project-tree.md` §5A.2's rule (the *document's*
workspace, via the ancestor-`.cws` walk), so a schematic opened from elsewhere still gets its own substrate.

Schematics have no `TechRef` field today; resolve purely from the workspace and add a per-schematic override
only if a need appears.

**R-pc-9. Layer selection is `Signal Layer` + `Ground Reference`, not "top/bottom conductor."** From those
two the stackup yields everything the model needs: h from their separation, εr from the dielectric between,
t and σ from the signal metal. Both default from the stackup — topmost conductor, nearest ground-designated
conductor beneath — and both are **per-instance overridable**, because which layer you route on is design
intent rather than a process parameter (the same split the via brief drew).

Unambiguous on a 2-layer board or an MMIC; genuinely ambiguous on a 4-layer board, which is what the override
is for.

**R-pc-10. Warn when the selected layers are not a microstrip.** If the signal layer has ground both above
*and* below, the structure is **stripline** and Hammerstad-Jensen is the wrong model. Report it clearly
rather than returning confident wrong numbers. A stripline component can follow later.

**No technology resolved:** the *geometry* is still generatable (W and L are parameters), but the *electrical
model* is not — there is no εr or h. Generate the artwork, and refuse to stamp with a clear message naming
the missing technology.

## 3. The components

All four are **built-in C#** generators (§0). Parameters live in `.ccell` and are shown by the symbol.

| | Parameters | Geometry | Pins |
|---|---|---|---|
| **MLIN** | `W`, `L` | a `Path` of width `W` along +X (or a rect) | 2, at each end |
| **MBend** | `W`, `Angle`, `Mitered` | two arms meeting at `Angle`, corner cut when mitered | 2 |
| **MTee** | `W1`, `W2` (through arms), `W3` (branch) | two collinear arms plus a perpendicular branch, unioned at the junction | **3** |
| **MCross** | `W1`–`W4` | four arms unioned at the centre | 4 |

**MTee ranks above MCross.** A T-junction is the fundamental branch in RF — stubs, power dividers, bias
tees — while a cross is comparatively rare. If scope has to be cut, **MCross is the one to drop**; MTee is
not optional.

**MTee's arm convention**, following R4: pin 1 at the origin, the **through line** running along +X to
pin 2, and the **branch** along -Y to pin 3 — matching the symbol's own downward port 3 (the schematic
canvas is Y-down; layout is Y-up, so "down" is -Y here, not +Y — see
docs/sonnet-briefs/brief-L5-followups.md §2/R-L5f-5, which corrected an earlier +Y statement here that
had crossed conventions without flipping the sign). `W1` and `W2` are the through arms and may differ —
a tee whose through line steps width is entirely ordinary. The instance transform handles any other
orientation, so the generator only ever emits this one.

**Junction geometry is a union, not overlapping rectangles.** Three arms meeting produce one connected
region; emitting three overlapping rects would leave internal edges that a mask shop, a Gerber region and
the MoM mesher would each have to resolve differently. Union them (Clipper2) and emit one outline.

### 3.1 What is modelled electrically, and what is not

**The models are specified in `docs/design/microstrip-models.md`. Read it before writing any of them.**
It names which published model, which *variant* of it, the validity range, and the accuracy — because
"implement Hammerstad-Jensen" leaves four further decisions open (thickness correction, dispersion model,
conductor-loss treatment, roughness) that two engineers would answer differently.

**MLIN carries a full line model** — five stacked choices, all enumerated in that document's §2.

**R-pc-11. MLIN stamps as the existing TLIN with computed parameters.** Once Z₀, εeff and α are derived from
geometry and substrate, the stamp is the ideal transmission line already in the engine. Reuse it.

**R-pc-12. One implementation serves both MLIN and §10.9's MoM oracle.** Two that disagree is the worst
possible outcome for a validation reference — L7's ±2% gate would pass or fail on which one the test called.
And per `microstrip-models.md` R8, that tolerance is only meaningful against the **named variant**, within
the **stated validity range**.

**R-pc-15. Do not transcribe formulas from memory, and do not take them from a brief.** Work from the primary
sources `microstrip-models.md` cites. A closed-form expression written from recollection looks authoritative,
gets implemented faithfully, and is then checked against a test table produced by the same recollection — so
the error is self-confirming and invisible. Every acceptance value must carry its provenance.

**R-pc-16. Out-of-range parameters are reported, never silently extrapolated** (`microstrip-models.md` R4).
The formulas keep evaluating outside their published range and return plausible nonsense.

**MBend, MTee and MCross each carry their published discontinuity model** — `microstrip-models.md` §4. An
earlier draft of this brief made them ideal junctions; that was wrong, because **a placed component with an
ideal model earns nothing**: MLIN–MBend–MLIN with an ideal bend is electrically identical to one longer
MLIN. The junction is the whole reason the component exists.

**R-pc-17. Specify and implement the equivalent circuit, not only the formulas** (`microstrip-models.md` R6)
— the network topology is what determines the stamp, and MTee's is a 3-port. **Reference-plane shifts are
part of each model and must not be absorbed** (R7); dropping one produces a small, frequency-dependent length
error that reads as a drafting mistake rather than a modelling one.

**R-pc-18. Mitered and unmitered bends are distinct discontinuities and are separately gated** (R15). Letting
one fall through to the other yields an error that is small, plausible, and frequency-dependent.

**R-pc-19. `microstrip-models.md` §4 now carries the primary references — start from Garg & Bahl 1978**, which
covers open ends, gaps, step, bend, tee *and* cross in one paper at ~5% accuracy. Note **R12 there**: the most
accessible written-up survey of these models is GPL-licensed, and equations must come from the primary papers
instead, per the root licensing rule.

**Mitered bend:** the standard optimal miter is Douville & James — cut ≈ `W·(0.52 + 0.65·e^(−1.35·W/h))`
along the diagonal. Cite it in the code; it is the kind of constant that looks arbitrary six months later.

## 4. Regeneration, and the escape hatch for hand-editing

Generated geometry is **derived**, so it must not be hand-edited — an edit would be silently discarded on the
next regeneration.

**R-pc-13. A PCell's layout is read-only in the editor. To modify it, flatten it.** L3c's
**Flatten Hierarchy** already converts an instance into ordinary geometry, and that is exactly the right
escape hatch: flatten, then edit freely, accepting that the result no longer tracks its parameters. Say so in
the disabled-edit tooltip (R13a) rather than silently ignoring edits.

Regeneration triggers on a parameter change or a technology change, through the same invalidation seam L3b
built for sub-cell edits — **not** a new mechanism.

## 5. MStep — deliberately not built

**R-pc-14. The width-step discontinuity is not modelled in this phase, and the omission is documented.**
MBend, MTee and MCross **are** modelled (§3.1); the step is the exception, and for a structural reason rather
than a scope one.

It is the only junction carrying no information the schematic does not already have — it is fully determined
by the two widths — so it would have to be **synthesized from connectivity** rather than placed. That is a
different question from the three placed components, and it was settled separately: a per-component flag
double-counts, since a junction has two sides and any tie-break between them is arbitrary.

State the limitation where a user will meet it — component documentation and the analysis report:
*"Width-step discontinuities are not modelled; use EM simulation where the transition matters."*

Reserve the design without building it: it belongs in the **elaborator**, driven by a **single switch on the
analysis** rather than a per-component flag (a per-component flag double-counts, since a junction has two
sides and any tie-break is arbitrary), with junctions classified by arm count — 2 = step, 3 = tee, 4 = cross.
Leave that as a comment where the elaborator handles nets. Revisit after L8.

## 6. Guardrails

- **No scripting host, no PDK loading, no third-party PCell mechanism.** The contract is designed; the host
  is not chosen (§0).
- No MStep, no MTEE, no discontinuity modelling of any kind (§5).
- No stripline component — R-pc-10 warns, nothing more.
- No changes to §9's schematic→layout; that is L5, and it consumes what this builds.
- No new geometry primitive — PCells emit existing `LayoutShape`s.
- Don't touch `src/Core`'s existing device stamps beyond reusing the TLIN path (R-pc-11).

## 7. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Contract shape** — `Generate` returns shapes **and** pins; pins carry name, location, layer, width and
   outward direction; pin names match the symbol's (R-pc-2).
3. **Origin convention (R-pc-3)** — for each component, pin 1 is at the origin and the principal axis is +X.
   Two MLINs abut exactly when one is placed at the other's pin-2 location.
4. **Purity (R-pc-4)** — generating the same cell a hundred times, and after a serialize/reload cycle, yields
   byte-identical geometry.
5. **Caching (R-pc-5)** — a 50×50 array of one PCell evaluates the generator **once**; assert the call count,
   not the timing.
6. **Units (R-pc-6)** — `W = 2.9mm` produces exactly 2,900,000 DBU at 1 nm resolution; all generators route
   through the one conversion helper.
7. **Substrate resolution (R-pc-8)** — an MLIN in a PCB workspace picks up FR-4 1.6 mm with **no user
   configuration**; the same schematic opened from another workspace resolves that workspace's technology
   (§5A.2).
8. **Layer selection (R-pc-9, R-pc-10)** — defaults are the top conductor and the nearest ground beneath;
   overrides are honoured; a signal layer with ground above *and* below reports the stripline warning.
9. **No technology** — geometry still generates; the stamp refuses with a message naming what is missing.
10. **MLIN accuracy (R-pc-12)** — Z₀ and εeff match the acceptance table in `microstrip-models.md` §7,
    **including its edge-of-range rows**, and every value in that table carries its source. The **engine uses
    one implementation** shared with the future MoM oracle.
10a. **Variant recorded (R-pc-15)** — all five decisions in `microstrip-models.md` §2 are stated in code and
    in the user-facing summary; no formula or acceptance value originates from this brief or from memory.
10b. **Out of range (R-pc-16)** — a `W/h` or `εr` beyond the model's published bound is **reported**, once
    per distinct violation, naming the parameter and the bound — not silently extrapolated.
11. **MLIN stamps as a TLIN (R-pc-11)** — a lossless MLIN's s-parameters match an ideal TLIN of the same
    computed Z₀ and electrical length.
11a. **MTee (§3)** — three pins at the convention's positions (pin 1 origin, pin 2 along +X, pin 3 along
    -Y, matching the symbol's own downward port 3 — see brief-L5-followups.md §2/R-L5f-5); `W1 ≠ W2` is
    accepted; the emitted geometry is **one unioned outline with no internal edges**, not three
    overlapping rectangles. Same union assertion for MCross.
11b. **Discontinuity models (R-pc-17)** — MBend, MTee and MCross each match their source papers' published
    curves within the stated accuracy, at mid-range **and edge-of-range** points; each model's own validity
    bound is reported separately (`microstrip-models.md` R13); MTee stamps as a 3-port; reference-plane shifts
    are present and relate to the R3 pin positions as documented.
11d. **MCross symmetry (`microstrip-models.md` R11)** — with `W1 ≠ W3` or `W2 ≠ W4`, the component either
    refuses the parameters or **reports** that the opposing-mean approximation is in use and by how much the
    widths diverge. A silent mean substitution fails this gate.
11c. **Miter is not a shortcut (R-pc-18)** — mitered and unmitered bends produce **different** s-parameters
    for the same `W` and substrate, each matching its own reference. A test that passes with the two sharing
    one implementation has not tested this.
12. **Mitered bend** — the Douville-James cut length matches the formula; an unmitered bend is a plain corner.
13. **Read-only (R-pc-13)** — editing tools are disabled on generated geometry **with a reason**; flattening
    produces editable shapes that no longer track parameters.
14. **Regeneration** — changing `W` regenerates the artwork through L3b's existing invalidation seam, and the
    L3a cache reflects the new parameters.

## 8. On completion

**Update `docs/design/pcell-contract.md` rather than only `CLAUDE.md`** — it is the durable record of this
interface, and it was written before the first implementation, so implementation will have found things it
got wrong or left vague. Specifically: correct anything R1–R12 got wrong, fill in §9's open items that this
phase settled (parameter validation is the likely one), move the status line off Draft, and note the
contract version shipped.

Then record in `src/Ui/CLAUDE.md`: that the contract lives in `pcell-contract.md` and is the interface a
future PDK depends on; the purity requirement **and why the geometry cache makes it non-negotiable**; the
units boundary and rounding rule; that the **host is deliberately undecided** and built-ins are C#; that
**Hammerstad-Jensen is shared with L7's oracle**; and that **MStep is absent by decision**, with the hook
reserved and the reasoning in §8 of the design doc.
