# Brief 4 — the fast graph extractor, the classification, and the gate that keeps the default honest

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail4-n`
**Area:** `src/Design/Layout/Pdn/` · **Depends on:** 3 · **Blocks:** 5, 7, 8
**Design note:** [`railrf.md`](../design/railrf.md) §2.9, §4.6, §7, §9

---

## 0. What this brief delivers

The **default** model, and the three rules that make having two speeds safe.

| Type | File | What it is |
|---|---|---|
| `PdnCopperClassifier` | `src/Design/Layout/Pdn/PdnCopperClassifier.cs` | Which copper is trace-shaped and which is not. Emits a per-region classification **and the reason**. |
| `PdnGraphExtractor` | `src/Design/Layout/Pdn/PdnGraphExtractor.cs` | The fast reading: trace sections as single resistances, vias as barrels, pads as nodes, pours meshed coarsely. |
| `PdnModelKind` | `src/Design/Layout/Pdn/PdnModelKind.cs` | `Fast` \| `Accurate`, carried in `PdnProvenance` and onto every result. |

**Same currency.** §4.6: *"It produces the same kind of netlist as §4.1, so the solver, the result model,
the tables, the plots and the exports are identical and only the extractor differs. That is the reason
the fast path is safe to have at all — it is not a second simulator, it is a second reading of the
geometry."*

So `PdnGraphExtractor` returns a `PdnNetlist`, the same record brief 3 defined, with the same `Origins`,
the same `NodeCells` and the same `Ports`. If it needed a different result type, it would be a second
simulator and this brief would be wrong.

---

## 1. `R-rail4-1` — the arithmetic, and it is deliberately nothing new

§4.6:

```
Each trace section between junctions    one resistance, R = ρ·L / (W·T)
Each via                                its barrel resistance (brief 3's PdnViaModel, unchanged)
Each pad                                a node
Each part                               its library model (brief 2's PartLibrary, unchanged)
Copper that is NOT trace-shaped         meshed, and coarsely — brief 3's mesher at a coarse Δ
```

The result is a netlist of a few hundred elements that **solves in single-digit milliseconds**, so it
re-solves on every keystroke. That is §2.9's whole justification: the owner asked for a default fast
enough for live interaction with the full calculation behind an explicit button.

`ρ·L/(W·T)` is the same closed form brief 3's gate uses. That is not a coincidence and it is not
circular: brief 3's mesh is gated against the *closed form*, and this extractor **is** the closed form
along a path — so `R-rail4-5`'s fast-vs-accurate gate is really a gate on the path-finding and the
classification, which is exactly where this extractor can be wrong.

---

## 2. The three rules, from §2.9

### `R-rail4-2` — rule 1: every result says which model produced it

> A pass in Fast mode is reported as **a fast-model pass**, never as a pass.

`PdnProvenance.ModelKind` is set by the extractor and is carried:

- on the plot (brief 12),
- on each table (briefs 5, 6, 12),
- in the status strip — *"Fast model · 4.1 ms"*, always on screen (brief 7),
- in the provenance of **every** export (briefs 10, 12, 16).

A result object that can be constructed without a `ModelKind` is a result that can reach a user without
one. Make it `required`.

### `R-rail4-3` — rule 2: the classification is VISIBLE and CORRECTABLE

§2.9, and §9 names this as the failure that would not announce itself:

> a wide supply polygon treated as a trace is **optimistic**, and the number looks entirely ordinary.

So the classifier does not return a boolean per region. It returns a **decision with its reason**, and the
decision is drawable and overridable:

```csharp
public enum PdnCopperClass
{
    /// <summary>Current fills the conductor's width along its length: the closed form applies.</summary>
    Trace,

    /// <summary>Current spreads: a pour, a plane, the fan-out under a BGA. Meshed.</summary>
    Spreading,
}

public sealed record PdnClassification(
    PdnRegionRef Region,
    PdnCopperClass Class,
    /// <summary>Why — the aspect ratio, the width variation, the branch count, the port count on the
    /// region. SHOWN in the class overlay's readout, because "trust me" is not correctable.</summary>
    string Reason,
    /// <summary>Set where the user forced this region either way. A forced region is drawn
    /// differently from an inferred one: the user needs to see what they have overridden.</summary>
    bool Forced);
```

Brief 8 draws this as the `class` tab of §11.3's board panel, and a region can be **forced either way**
from there. The overrides live on the `RailDocument` (brief 1), keyed by region identity, so they survive
a re-import — which is exactly when a classification would otherwise silently change.

**Drawing it is what makes the failure mode neither silent nor invisible.** That sentence is from §2.9 and
it is the justification for the overlay existing at all.

### `R-rail4-4` — rule 3: Fast is REFUSED where it cannot be honest

> Above the frequency where the shunt branch matters — the cavity band — Fast does not offer a number;
> the button says so.

Not a warning, not a degraded number: **no number**. `PdnGraphExtractor` refuses above a stated frequency
and the refusal names `Accuracy` as the answer. Brief 7's Run button carries that sentence.

The threshold is derived rather than typed — it is where the shunt branch stops being negligible, which
§2.8 puts well above the excitation set on these boards but which depends on the plane pair. State the
derivation in the code and expose the computed number in the refusal, because *"Fast cannot answer above
about 180 MHz on this stackup"* is a sentence a user can act on and *"Fast cannot answer here"* is not.

---

## 3. `R-rail4-5` — rule 4, and it is the gate: the two are compared on the USER'S OWN board

§2.9's fourth rule is the one that turns the other three into something checkable:

> Running Accuracy **keeps the fast curve on the plot beside the accurate one**, so the error is measured
> on this design rather than promised in a document.

Two halves, and both are this brief's:

1. **In the product.** After an Accuracy run, the fast result stays on the plot and in the tables as a
   second series. Brief 12 draws it; brief 4 has to make it *available* — so `PdnNetlist` results are
   retained per model kind rather than replaced.
2. **In the test suite.** §7's gate:

   > On a **trace-dominated** DC path the two must agree to **5 %**; on a **pour-dominated** one the fast
   > model must **refuse** rather than differ.

That second half is the important one and it is easy to skip. A fast model that produces a slightly
different number on a pour has failed the gate just as surely as one that produces a wildly different one
— the required behaviour is a **refusal**, because the error there is unbounded and optimistic.

### The test board

Build it synthetically and deliberately, with three regions whose answers are known:

| Region | Shape | Expected |
|---|---|---|
| A | a stepped trace, three widths, no branches | Fast and Accurate within 5 % |
| B | a trace with two stubs and a T | Fast and Accurate within 5 % |
| C | a 20 mm × 15 mm supply polygon with a port in one corner and a source in the other | classified `Spreading`; Fast **refuses** the path through it |

And the negative that catches the real defect: **force region C to `Trace`** and assert that Fast then
produces an answer that is optimistic against Accurate by more than 5 %. That is the misclassification
failure, reproduced on purpose, and it is what proves the classifier is load-bearing rather than
decorative.

---

## 4. `R-rail4-6` — the path finding is where this extractor can be wrong

Brief 3's mesh has no notion of a path: current goes where the copper is. The graph extractor has to
**find** the sections, and that is the step with no closed form behind it. Three failure shapes, each
worth its own test:

1. **A junction missed** — two sections merged into one, and the branch current that left in the middle
   is lost. The drop is then under-estimated: optimistic.
2. **A section's width taken at one point** rather than along its length. A taper read at its wide end is
   optimistic; at its narrow end, pessimistic. Take the **integral** — sum `ΔL/W` along the section, which
   is the same "number of squares" arithmetic §2.8 describes — and never a single sample.
3. **A via treated as a junction when it is a parallel group.** A layer transition with six vias is one
   node and six parallel barrels, not one barrel.

Each of these produces a plausible number and no error, which is the recurring shape of every risk in
this design.

---

## 5. Tests — `tests/Ui.Tests/RailRf/PdnFastExtractorTests.cs`

- **`R-rail4-1`**: a straight trace through the graph extractor equals `ρL/(WT)` to under 1 % — the same
  closed form brief 3's mesh is gated on, so both readings are anchored to arithmetic rather than to
  each other.
- **`R-rail4-5`**: the three-region board above, all three rows, plus the forced-misclassification
  negative.
- **`R-rail4-3`**: every region in the result carries a non-empty `Reason`; a forced region reports
  `Forced` and the override survives a document round trip (brief 1's format).
- **`R-rail4-4`**: the refusal above the shunt-band threshold contains the computed frequency, and no
  result is produced.
- **`R-rail4-6`**: a T-junction produces three sections, not one. A linear taper's resistance matches the
  integral to under 1 % and differs from both single-sample answers by a stated margin — the test is that
  it is *neither* endpoint's answer. A six-via transition is one node and six parallel elements.
- **Same currency**: the two extractors' results on the same trace-only board have identical `Ports`,
  identical port bindings and the same `NodeCells` coverage over the pad set — so everything downstream
  reads them identically. This is what "not a second simulator" means, asserted.

**No timing tests.** "Single-digit milliseconds" is a design intent, not an assertion — the status strip
displays elapsed time and nothing gates on it. Assert element counts instead: the fast extraction of the
note's own worked-example board is a few hundred elements and the mesh is thousands, and *that* ratio is
the structural property that makes it fast.

---

## 6. Scope

- **No solve.** Brief 5.
- **No inductance.** The graph extractor's section inductance (the "and, above DC, one loop inductance"
  of §2.9) is **brief 13**, with the mesh's. This brief is DC only, exactly as brief 3 is.
- **No overlay, no drawing.** Brief 8 draws the classification; this brief computes it and makes it
  overridable.
- **No Accuracy button.** Brief 7. This brief makes both extractors callable and carries the model kind;
  the control that chooses is the window's.

**On completion:** record findings in `src/Design/RESOLVED.md`. Never in a CLAUDE.md.
