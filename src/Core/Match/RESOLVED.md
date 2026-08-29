# src/Core/Match — resolved briefs

Findings from the Match component's own briefs, kept out of any `CLAUDE.md`. Same pattern as
`src/Core/RESOLVED.md`: one `##` section per brief, only what is still true, still surprising, and
would cost someone real time to rediscover.


## Round 7 — the stored payload is JSON, and the order picker is the real remedy (2026-08-20)

### `Encode` writes JSON; only `CnlWriter` makes a token

Owner: *"the .csch is showing a Match component instance with Expression all crazy text. Recall that
all circuitRF file formats are supposed to be human readable."*

Base64 was there for a real reason, and the reason belongs to exactly one format. A `.cnl` is
whitespace-delimited and its only string escape is a pair of quotes, with no way to escape a quote
inside one — so a design's JSON cannot be a `.cnl` token. **A `.csch` is JSON and never had that
problem**, and nothing else in the application writes a design to a token format. So:

- `MatchEmbedding.Encode` → the JSON (also *shorter* than the base64 it replaced, by a third).
- `MatchEmbedding.EncodeToken` / `ToToken` → base64, unpadded, for a `.cnl`.
- `CnlWriter.FormatParam` converts a brace-leading `Design` expression on its way out. Keyed on the
  PARAMETER NAME, not the component type: `"Design"` is the embedded-payload parameter for Match and
  for wBond alike, and `Core` cannot see the wBond assembly to ask it anything. Nothing else in the
  netlist writes a brace-leading expression — the expression language has no token starting with `{`.
- `TryDecode` already accepted both, so every file written before this still loads and a hand-authored
  `.cnl` may still carry base64. **The padding must stay stripped** — `CnlReader`'s spaced-assignment
  merge reads a token ending in `=` as an empty assignment and glues the next parameter on as its
  value.

Tests that hand-build `.cnl` TEXT (`MatchComponentTests`, `MatchStampTests`, `TerminationProbeTests`,
`MatchFlattenPlanTests`) must use `EncodeToken`. Anything writing a component parameter wants `Encode`.

Making the payload legible immediately exposed that four **computed** properties — `MatchDesign.Omega0`,
`W`, `Termination.HasReactance`, `AbsorbedType` — were being serialized as though they were inputs.
They have no setter, so a reader silently ignored them; a person reading the file would not have.
`[JsonIgnore]`, and anything derived from a field on that record belongs behind it too.

### 50 Ω into 5 Ω ∥ 1 pF: order 3 genuinely cannot, order 4 gives −43.5 dB

Owner: *"I find it hard to believe that the Match component cannot match a 50 ohm termination to a
parallel RC of 5 ohms // 1pF at 2 GHz. Am I doing something wrong?"*

Measured over 1.8-2.2 GHz, Chebyshev-Fano, Π N² required = 10:

| order | reachable Π N² | solutions | worst in-band RL after applying |
|------:|---------------:|----------:|--------------------------------:|
| 2     | 1              | 0         | —                               |
| 3     | 1.016          | 0         | —                               |
| 4     | 10             | 6         | **−43.5 dB**                    |
| 5     | 10             | 16        | −48.1 dB                        |

Two-ended Chebyshev reaches it at order 3 (6 solutions). Butterworth and Bessel refuse the prototype
outright at every order for an analysis-end Q of 0.0625 — a very low-Q end is the case those two
families have no positive g-vector for, which is worth knowing before reading their refusal as a bug.

So the refusal was CORRECT and the design was one picker click from working. What it lacked is what
the MoM-ceiling lesson in `src/Engine/Mom/RESOLVED.md` is about: **a remedy is only a remedy if it
binds.** "Allow negative components, change the order, or change the response" named the right knob and
left the user to find the setting by trying five. `MatchDesignerViewModel.FindWaysOut` now re-runs the
search at each permitted order and each FEASIBLE response and names the ones that work by number, so
the sentence ends *"Order 4, 5 and 6 do reach it with this response, and two-ended reaches it at this
order."* It runs only on an already-refused design, on the analysis worker, under the same
cancellation — a design with solutions pays nothing, and `MatchOrders.ValidOrders` is a short list (a
termination pair fixes the parity), never a range.

## Round 7, part 2 — a Q-adjust may inflate an end's Q, never reduce one (2026-08-20)

Owner: *"I press the probe button on Terminal 2 and the parasitic updates to 1000 pH, but the
schematic keeps rendering as 2000 pH."*

### The tell is that the ladder was insensitive to the termination

1 nH and 2 nH at termination 2 produced **element-for-element identical** networks. That is not stale
rendering; it is the synthesis not looking at the termination at all.

`Synthesize` takes `qAna = design.QAdjust > 0 ? design.QAdjust : qAnaActual` and **never checked that
the adjustment is above the end's own Q.** The design carried `QAdjust = 2` — legitimate when that
end's Q was lower — and the probe then wrote a 1 nH parallel L whose Q at band centre is 3.999. So:

1. the end arm was built for Q = 2, which is a 1999 pH shunt inductor;
2. `WithEndSplits` skips its split whenever `qSynth / qActual <= ExcessRatioThreshold`, and here the
   ratio is *below* 1 — so the element kept the **synthesis's** value;
3. …while still carrying `AbsorbedEnd = 2`, so the drawing labelled 1999 pH as supplied by a
   termination that supplies 1000 pH, and the response was computed from an inductor the circuit does
   not contain. The status strip called it matched at −37 dB.

### The far end had this refusal all along; the analysis end never got its counterpart

`FarEndNotAbsorbable` — *"the synthesis reaches Q_far = X against the termination's own Y"* — is the
same statement about the other end. `AnalysisEndNotAbsorbable` is the missing half, checked
immediately after `qAna` is chosen, before any prototype work (it depends on nothing the prototype
produces, and a refusal is cheapest the moment it is knowable). **A parasitic cannot be subtracted**:
if the end arm needs less reactance than the termination already provides there is no network, so this
is a refusal rather than a clamp — §4.6's Q-adjust *inflates* an analysis end's Q by construction.

Guard: `QAdjust > 0 && qAnaActual > 0 && QAdjust < qAnaActual * (1 - 1e-9)`, mirroring the far end's
exact-comparison guard rather than `ExcessRatioThreshold`.

`MatchSynthesis.AnalysisIsTerm1(design)` is public now. The Designer has to know which end a Q-adjust
is about before it can tell whether the stored one is still legal, and re-deriving "highest" at a
second site is how the two would come to disagree.

### Measured, on the owner's own case

50 Ω into 5 Ω ∥ 1 pF, 1.8-2.2 GHz, order 3, termination 2 = 50 Ω ∥ 1 nH (Q = 3.999):

| QAdjust | result |
|--------:|:-------|
| 0       | absorbed L3 = **1000 pH**, exactly `Term2.Value` |
| 2       | refused, `AnalysisEndNotAbsorbable` (was: silently drew 1999 pH) |
| 6       | synthesises — a genuine inflation, which is what §4.6 means |

## Match round 3 — `Discover` promised a pair `Apply` refuses (2026-08-20)

Owner-reported crash, straight out of the *+ add* menu with no dialog and no recovery:

```
System.InvalidOperationException: Making a transform pair adjacent would swap
C2_N1_1_N2_1_N3_2 past C2_N1_1_N2_1_N3_3, which have different orientations
  at NortonTransform.RequireSameOrientation
  at NortonTransform.Apply
  at MatchRebuild.ApplySequence  →  MatchDesignerViewModel.AddTransform
```

### The type test was a proxy for "same arm", and the proxy is only true on a FRESH ladder

`Discover`'s gap-3 case offers a pair three apart, which `Apply` makes adjacent by walking each end
inward one place. Both walks are response-preserving only if each stays inside an arm — that is,
`el[j]` shares an orientation with `el[j+1]`, and `el[j+3]` with `el[j+2]`. What the guard actually
tested was `el[j+1].Type == el[j+2].Type`.

On the ladder the synthesis emits, elements strictly alternate in both type and orientation, and the
type test happens to select the same pairs the orientation test would. **A transform's own three
products break that**: they are all one type and alternate in orientation by construction (pi is
shunt-series-shunt, T is series-shunt-series). A pair straddling a previous transform's triple
therefore passed the type test, was offered in the menu, and hit `Apply`'s assert. The reported names
say it exactly — `_N3_2` and `_N3_3` are products 2 and 3 of one triple, which **always** differ in
orientation.

The fix is the orientation condition, stated rather than proxied. The type test is kept: it selects a
strictly smaller set and removing it would change which transforms the solution search offers.

### The existing test could not have caught it, and the reason is worth stating

`AdjacencyMoves_NeverSwapAcrossAnOrientationBoundary` already walked *every discovered pair at every
reachable order* and applied each one — on the **freshly synthesised** ladder only, which is precisely
the ladder where the proxy holds. Nothing in the suite had ever re-discovered on a ladder that had
already been transformed. `AdjacencyMoves_StayInsideAnArm_OnLaddersThatHaveAlreadyBeenTransformed`
does: four rounds of apply-then-rediscover, both forms, three orders. Reverting the guard makes it
fail with the owner's own message, at round 4.

### A rebuild now reports an unapplicable transform instead of throwing

`MatchRebuild.ApplySequence` already drops a transform *with a note* when its pair no longer exists in
the ladder. An `Apply` that refuses is the same class of event and is handled the same way — the
Designer shows the note, keeps the rest of the sequence, and stays open. This is belt-and-braces, not
the fix: an escaping `InvalidOperationException` from a menu click is an application crash whatever
caused it, and the disagreement itself is fixed at its source in `Discover`.

## MN-1 — the synthesis core (2026-08-19)

Implements `docs/design/match.md` §4 (synthesis), §5 (inductive terminations), §6 (response shapes)
and §7 (data model and rebuild) as pure algorithm. No UI, no `ComponentModel`, no stamping.
Gate: `dotnet test tests/Core.Tests` (1,444 tests, ~4 s) and `dotnet test tests/Firewall.Tests`.

### The namespace is `CircuitRF.Core.Matching`, not `CircuitRF.Core.Match`

The directory is `src/Core/Match/` as the design doc specifies, but the namespace could not be. A
namespace named `Match` under `CircuitRF.Core` is visible as the bare identifier `Match` from every
sibling namespace, which collides with `System.Text.RegularExpressions.Match` — `PdkImporter.cs`
alone has four `foreach (Match m in ...)` loops, and the whole project stops compiling with
`CS0118: 'Match' is a namespace but is used like a type`. Renaming the namespace costs nothing and
touches no file outside this directory; renaming four loop variables in someone else's file to make
room for a namespace is the wrong trade. `Matching` also matches the palette category the design doc
already decided on (§14 item 3).

### The absolute value guards REPORT, they do not repair — and the reason is measurable

The reference implementation clamps any produced Norton value above 1 H / 1 F to exactly 1.0 and
anything below 1e-24 to 0.0. That clamp is kept as a *condition* (`TransformApplication.GuardFired`)
and dropped as an *action*, because rewriting a value is precisely what breaks the one invariant the
whole transform rack rests on: a "repaired" network no longer has the response it claims, and nothing
anywhere says so.

It is reachable, which is the part worth knowing. `N` held one part in 1e9 inside its positivity
threshold is still one part in 1e9 from a pole, so a solver that parks `N` on the bound produces a
mathematically exact, response-preserving, and completely unbuildable element. **On the design doc's
own §4.9 problem that is a 2.9 kH inductor.** The brief's own §11 sweep never sees it (it samples
2 %..98 % of each range and the guard correctly never fires there); only the solution search's drive
reaches the bound. So "a firing guard means the threshold logic is wrong" is not quite right — here
it meant the *N allocation* was wrong.

### The solution search seeds N at its equal geometric share, not at 1

Brief §8 starts every `N` at 1 and then corrects index by index. The first index then asks for the
entire ratio at once — `sqrt(119.027) = 10.91` on the golden problem — is clamped onto its own
positivity threshold of 5.989, and every later index only mops up the remainder. That is what parks
`N` on the bound and produces the 2.9 kH element above; with the guards clamping as specified, the
network is then silently corrupted and every solution is rejected. Seeding each `N` at
`required^(1/2k)` lands the same two-transform set on `N = 3.303` each, well inside both ranges, with
the same product and the same response. **The index-by-index correction after the seed is unchanged**
and is still what handles a set whose ranges cannot take equal shares.

### The §4.5/§4.6 end splits run LAST, after every transform — not inside the basis ladder

Two independent reasons, both load-bearing:

1. **An extra element inside the basis list breaks pair discovery.** Brief §7.3's move offsets are
   only response-preserving because every swap they ask for is between two elements of the *same*
   arm; that holds because the basis list alternates strictly `(L, C) x (shunt, series)` with period
   four. Insert `CFano` next to the far arm's absorbed capacitor and the period breaks: the gap-3
   move then swaps a shunt element past a series one, which is a different circuit, not a
   re-ordering. (`NortonTransform.Apply` asserts the orientation rather than trusting the table, so
   this fails loudly rather than silently — but it fails.)
2. **Applied last, the split is exact by construction.** The far port has by then reached its target
   resistance, so `X_old` comes out equal to the termination's own value to machine precision. On the
   golden problem: far arm C = 38.114 pF at 1.6803 Ω becomes 0.32022 pF at 200 Ω, and the split gives
   0.19522 pF of `CFano` plus **exactly 0.125 pF** kept. Applied to the basis instead, the kept value
   is `0.125 pF x 119.027` and only becomes the load's own value after the transforms have run.

The arm total is unchanged either way, so the response is unchanged — and that is asserted at 1e-12.

### The excess split is worked in C_eq, not in element values

Brief §5 writes `X_new = (Q_far - Q_actual)/(R*omega0)` for a shunt end. That is a *capacitance*
formula. At a shunt end with an **inductive** termination the two inductors combine reciprocally
(`1/L_tot = 1/L_old + 1/L_new`), and the raw formula is simply wrong there. Working the whole split in
`C_eq` and converting back with `MatchQ.FromCeq` makes one formula serve all four
(series|parallel) x (C|L) combinations, which is exactly the property §5.3 claims for the rule and the
reason it is stated in Q.

Worth recording alongside it: **for every end arm, `C_eq` is just the arm's C value**, whichever
element the termination supplies. A shunt arm has `L = 1/(omega0^2 C)`, so `C_eq(L_arm) = C_arm`
identically. That one identity is what makes the inductive case free everywhere downstream.

### `movable` — the general rule, and a design that proves it is needed

The reference expresses it as *"allowed if the pair's type is L, otherwise neither index may be
absorbed"*, which is correct only because in that implementation the absorbed element is always a
capacitor. The general form is

```
movable(a,b) := type(a) is not an absorbed type  ||  neither a nor b is absorbed
```

Fixture that separates them (`NortonTransformTests.MovableRule_...`): replace the golden problem's
1.25 Ω + 10 pF series load with the dual 1.25 Ω + 153.51694 pH. `L4` is then the absorbed element, and
the `L3/L4` pair must be refused — the reference's rule allows it, because its type is L, and moving
it breaks the absorption. A stronger version of the same design (200 Ω ‖ 8 nH against 1.25 Ω + 10 pF
at n = 2) makes **both** L and C absorbed types and leaves the ladder with **no transformable pair at
all**, which the reference implementation would have reported as four available transforms.

### `NoRealRoot` cannot happen for orders 2..6, and the reason is structural

Both the brief and `match.md` treat "P_n(c) has no real root" as a first-class outcome. It is
implemented, it carries its numbers, and **it is unreachable with the design doc's own coefficient
table**: n = 3 and n = 4 give a *cubic* in r, n = 5 and n = 6 a *quintic*, and a real polynomial of
odd degree always has a real root; n = 2 sets r = 1/2 without root-finding at all. Measured over 240
(n, Q, w) combinations spanning `Q` from 1e-6 to 1e6 and `w` from 1e-4 to 1.9: **zero** refusals.
The path is kept (it is the right shape for a future order or a different table, and the guard on
`d = sqrt(c^2/4 + r)` and on `D > 0` still needs somewhere to go), and `MatchRefusalTests` records the
measurement so a reader does not assume it fires.

### Root ordering, pinned

`FanoG` takes the **smallest** real root of `P_n(c)` that satisfies `c^2/4 + r >= 0`, sorted
ascending. A root finder's own ordering is an implementation detail, and a different member of the
family is a different (and generally worse) design with no error anywhere — so the choice has to be
pinned to something stable. It reproduces the design doc: at n = 4 on §4.9's problem the polynomial
has exactly one real root, r = 0.2278528, giving `D = 1.9176740` and
`g = [1, 1.311823, 1.106975, 1.717201, 0.508891, 1.344236]`.

### `FromRoots` must accumulate in complex arithmetic

The Hurwitz factor is rebuilt from a conjugate-closed root set. Taking the real part *after each
linear factor* — which looks safe, since the finished product is real — silently produces a different
polynomial: only the product over a conjugate **pair** is real. The symptom is total and silent, the
same shape as the leading-coefficient trap the brief warns about: every `Gvalues` call returns null,
every family search reports "infeasible", and `maxQFar` comes back 0.0000 for Butterworth and Bessel
at every order. Accumulate in `Complex[]`, take `Real` once at the end.

### The numerical route reproduces the closed form far better than the 0.5 % gate

`MatchPrototypes.Search` on the Chebyshev family at n = 4, scored by worst in-band |S11|:

```
  numerical: 1.000000, 1.311823, 1.107065, 1.717321, 0.509001, 1.344237
  closed:    1.000000, 1.311823, 1.106975, 1.717201, 0.508891, 1.344236
  worst relative error 0.022 %      score -16.6629 dB vs the closed form's -16.663
```

The design doc's own numerical answer was 0.4 % off; the difference is the local refinement pass
described below, not a better extractor.

### The family search refines around BOTH optima, and the second one is not optional

A coarse grid over the shape parameter finds the right neighbourhood, but two different numbers are
read out of the search and each needs its own refinement:

- the **score** optimum, which is flat near its own minimum — without refinement the winning
  g-vector lands a few per cent off the closed form (0.65 % on a 60-point grid);
- the family's **maximum Q_far**, which is what an infeasible response has to quote in its refusal.
  Un-refined it read 0.196 for Bessel at n = 4 against the design doc's 0.325 — a 40 % understatement
  of how far off the family is, in a message whose entire job is to say how far off it is. Refined:
  0.3153 (n = 4) and 0.1826 (n = 6), against the doc's 0.325 and 0.183.

### Measured: Butterworth and Bessel on the design doc's own problem

Worst in-band |S11| over 3.3-5.0 GHz, and the far-end Q against the 0.6381 required:

| n | Butterworth | Bessel |
|---|---|---|
| 2 | **-9.946 dB**, Q_far 1.1415 | **-7.800 dB**, Q_far 0.6381 |
| 4 | **-13.205 dB**, Q_far 0.6702 | *infeasible*, max Q_far 0.3153 |
| 6 | **-8.470 dB**, Q_far 0.6381 | *infeasible*, max Q_far 0.1826 |

against the design doc's 9.94 / 13.20 / 8.29 dB and 0.325 / 0.183. **The n = 6 Butterworth constraint
really does bind**: the best-return-loss member reaches -14.729 dB at Q_far 0.4985 and is rejected;
the constrained best is -8.470 dB. An unconstrained search returns a network that cannot absorb the
far termination at all, with nothing to show for it.

### Cost, measured

- **Solution enumeration at n = 6: 1.6 ms** (8 pairs, 28 candidate sets, 8 solutions). Including the
  15-step Q-adjust bisection, which re-runs the whole enumeration per step: **9.4 ms**. The brief
  asked for this number and set 1 s as the point at which to stop and explain; it is three orders of
  magnitude clear.
- **The numerical route is the expensive part, and it is per-synthesis**: Butterworth 63 / 332 / 534 ms
  at n = 2 / 4 / 6, Bessel 98 / 787 / 1,453 ms. `MatchPrototypeTests` therefore runs ~4 s as a class
  and the Bessel test alone ~2.4 s — under the ~5 s `Category=Benchmark` threshold, so untagged, but
  it is the one thing in this area that would cross it if the grids grew. The grids
  (32 shape steps, 16 scan steps, 40 bisection steps, 200 Durand-Kerner iterations) were tuned down
  from 2.5x that cost with **no** loss on any gate: the cross-check moved from 0.010 % to 0.022 %.
  MN-3 must cache the search rather than re-run it per keystroke — the Chebyshev closed form is
  microseconds, but Butterworth and Bessel are not.

### Smaller things that are still true

- **Propagation is always *away* from the analysis end.** It falls out of `NGreaterThan1` and
  `PropagateRight` together rather than being stated anywhere, and it is what makes two separate
  claims work: the analysis end's absorbed element stays at exactly the termination's own value
  however many transforms are applied, and `achieved = product(N^2)` is a statement about the **far**
  port rather than an average over both.
- **The ratio tolerance is relative.** The reference uses an absolute `1e3*epsilon` (~2.2e-13) against
  a required ratio of ~119 — that is 1e-15 of the quantity being tested, i.e. an exact-equality test
  wearing a tolerance's clothes.
- **Nets are derived by walking the element list, never stored.** Order plus orientation determines
  the topology completely, which collapses brief §7.4's "swap its nets with the neighbour it
  displaced" into a plain list swap, and makes the pi/T net table (`a-gnd, a-b, b-gnd` /
  `a-t, t-gnd, t-b`) fall out of the walk rather than needing to be implemented.
- **Reverse by ARM, not by element.** Turning an analysis-end-first ladder into a Term1-first one by
  reversing the flat element list puts C before L inside every arm; reversing the arm order and
  keeping `(L, C)` inside each preserves the strict type alternation that the move offsets need.
- **The Q-adjust bracket moves are right and their label is not.** Brief §6 annotates both bracket
  updates "move toward MORE detune"; the operations it gives (series `lo = guess`, parallel
  `hi = guess`) both narrow toward LESS detune on a successful guess, which is what finding the
  *minimum* such Q requires — and is what §4.6 asks for. The operations are implemented; the label is
  not. Note also that on the golden problem the minimum Q-adjust is essentially the true Q (the
  design already completes without one), so no `CDetune` is produced there — a test that expects one
  has to set `QAdjust` explicitly.
- **`MatchResponse` is production code, and the test oracle is a second implementation.** The
  constrained family search scores members by the worst in-band return loss of the resulting bandpass
  ladder, so a response evaluator has to exist in `src/Core`; `tests/Core.Tests/Match/MatchAbcdOracle`
  is written separately from it, so the golden numbers are not checked by the code that produced them.
- **Two files beyond the design doc's §12 list**: `MatchResponse.cs` (above) and `MatchRebuild.cs`
  (§7.3's sequential rebuild, which both the Designer's load path and the solution search's inner loop
  need). `MatchPoly` lives inside `MatchPrototypes.cs` to keep the file list otherwise exact.
- **`MatchDesign` uses properties, not fields.** The brief writes `public double F1, F2;`;
  `System.Text.Json` needs `IncludeFields` for those and nullable analysis does not track them.
  The JSON property names are identical either way.

## MN-2 — the component, the symbol and the palette (2026-08-19)

Makes a `Match` placeable, elaboratable and simulatable: `MatchModel`, the factory and elaborator
wiring, a symbol, a registry entry and a new `Matching` palette category. No Designer — MN-3.
Gate: `Core.Tests` 1,454 in 4 s, `Engine.Tests` 1,257 in 4 m 52 s, `Ui.Tests` 8,129 in 30 s,
`Firewall.Tests` 6. All green.

### The elaborator needed a SECOND edit, and without it nothing decodes

The brief allows exactly one change to an existing Core file outside `src/Core/Devices/` — the
internal-node mint — and that is not enough to run a `Match` at all. `Design` is base64 and
`Response` is an enum name; both reach `Elaborator.ResolveParameters`' generic branch, which calls
the expression evaluator on every override with **no** try/catch. Base64 tokenises as an identifier,
so a placed `Match` throws during elaboration before the factory is ever reached. `wBond` already has
exactly this branch for exactly this reason, so `ResolveMatchParameters` is that same rule applied
again: `Design` and `Response` verbatim, the numeric echoes evaluated, and — unlike the generic
branch — a failed echo **swallowed**, since an echo is display-only and `Design` carries the truth.

That branch is also what lets a refusal name the instance. The factory sees a parameter dictionary
and no instance name, so `ResolveMatchParameters` injects `MatchName` the way `ResolveChainParameters`
injects `ChainName`. Without it the message could only have named the type, which on a schematic
holding several `Match`es is not a message.

### One series ARM is one branch, and that is what fixes the internal-node count

`MatchNetwork.AssignNets` gives every SERIES ELEMENT its own node — correct as a topology
description, and not what gets stamped. The stamp groups each maximal run of through-path elements
into ONE branch carrying `Z = jwL + 1/(jwC)` (`InductorModel`'s `L=` plus `C=` shape, DC-open case
included), so `InternalNodeCount` is *series runs − 1*, not *series elements − 1*.

The grouping is not cosmetic: §4.5's `CFano` is inserted **into** an end arm with the same
orientation, so a series end arm can hold three elements and still be one branch — which is exactly
right, because `MatchQ.SplitExcess`'s series case combines reciprocally (`1/C_tot = 1/C_kept +
1/C_added`), i.e. the split element really is in series with the one it came from. Accumulating
`sum(1/C)` reproduces that identically.

Measured: the golden §4.9 design has **2 series runs → 1 internal node** and 7 stamped elements out
of 9 (two absorbed); the shipped default has **1 series run → 0 internal nodes** and 6 elements.

### The hand-built oracle is only an oracle because the node structure DIFFERS

`Engine.Tests`' comparison netlist writes one component per element and one node per series
*element* — deliberately not the component's own grouping. Agreement is then a statement about the
topology and the DC/AC limits rather than a tautology. **Measured worst |ΔS| = 5.4e-16** over nine
frequencies for the golden design and 4.3e-16 for the default, against the brief's 1e-12 gate; the
HB spectra (DC through the 3rd harmonic, driven by a rectifying diode) agree to the same order. The
same fixture stamped **with** the absorbed reactances differs by **0.99** — the invertible mistake is
not subtle once something looks for it, and nothing was looking before.

### Two DC claims need two different circuits, and only one of them is the obvious one

"Shunt arms are shorts" shows up anywhere: every bandpass ladder grounds its through nodes through a
shunt inductor, so both pins read exactly 0 V. **"Series arms are opens" is invisible in that same
circuit** — every node is already grounded, so an open and a short give the same node voltages. It
needs a ladder that STARTS and ENDS with a series arm, which means both terminations declared
`TerminationTopology.Series` (`s[1] = ana.Topology == Series` sets the whole alternation). Then each
pin's only path is through a capacitor-bearing series arm, the source sees no load at all, and the
drop across its own resistor is zero. Tolerance there is 1e-9 and not 1e-12: the DC engine's gmin
leaks ~1e-12 S across every open branch, which is 5e-11 of the source.

### The default design is 1-2 GHz / order 3 / 50 ohms both ends, and it is a real bandpass

Measured through the shipped path: −0.100 dB at both band edges (the 0.1 dB design ripple, exactly),
−0.023 dB mid-band, −27.8 dB an octave out on either side, with |S11| = −16.43 dB at the edges — the
0.1 dB-ripple Chebyshev return loss. Order 3 with both ends parallel-resistive gives
shunt-series-shunt, so it needs no internal net at all.


## Round 2 — N = 1 is a pole, and the range did not exclude it (2026-08-19)

Owner: *"If slider N1 goes to 1, the plots all fail."*

It is not a plotting bug and it is not a clamping accident — **unity is a genuine pole of all four
Norton product formulae**, and `NortonTransform.Range` returned it as a usable bound. Written out at
N = 1 exactly (`Apply`'s own expressions):

| form | products at N = 1 |
|------|-------------------|
| pi   | `N²·z1 / (1 − N)` → **±∞** |
| T    | `(1 − N)·z2` → **0**, which for a capacitor pair inverts to **∞** |

So the ladder acquired a non-finite element and there was no response left to evaluate — not a bad
number the plots could draw and the guards could flag, but no curve at all.

**The threshold end was already an open interval and the unity end was not.** `ThresholdMargin`
(1e-9) had been keeping N strictly inside its positivity threshold since MN-1, for exactly this
reason: *"at the threshold a product is infinite"*. The other end of the same interval is the other
pole of the same formulae, and it was the bare `1.0` — in **both** branches, bounded and
`allowNegative` alike. `UnityMargin` now closes it symmetrically.

**Nothing else moves, and that is checkable rather than hoped for.** At N = 1 ± 1e-9 the products are
enormous but finite, the transfer function is still exactly invariant (a near-identity transformer is
a near-open shunt arm), and the absolute guards report the element as out of range in red — which is
the designed behaviour for an N parked on a bound, not a repair. `MatchRound2Tests` evaluates the
published formula at exactly 1 as its own oracle rather than calling `Apply`, because `Apply` now
clamps and can no longer reach the pole: without that, the exclusion would read as decoration.


## Match speed — the lowpass-prototype search was ~6x slower than it needed to be, and one of those factors was a wrong answer (2026-08-20)

Owner: *"Match Designer appears slow to user when updated parameters… slowest when I change network
order or filter response type. (I believe the step that involves solving the low pass prototype.)"*
Right on both counts. Measured on the design doc's order-4 interstage problem, one specification edit
cost **1,161 ms**, of which **1,143 ms** was `MatchPrototypes.Search`.

### `MatchPoly.Roots` — the Cauchy scaling and the absolute stopping test were one fault, not two

Durand-Kerner needs the roots near the unit circle. The scaling was Cauchy's bound,
`1 + max|a_i/a_0|`, which bounds the **largest** root — so on a polynomial whose coefficients are
skewed it divides the whole set far **below** the unit circle instead of onto it. A Bessel
denominator at `alpha = 5` lands its roots near 1e-3, and the iteration then has to crawl inward from
unit-modulus seeds. The stopping test was an absolute step of 1e-14, which is a different demand at
every root modulus: it passes early on roots that are small, and is unreachable on roots that are
large.

Measured iteration counts at the same degree tell the whole story:

| family, order | degree | iterations, before | after |
|---|---|---|---|
| Butterworth n=4 | 8 | 9–10 | 9 |
| Bessel n=4 | 8 | **112–114** | 10–16 |
| Butterworth n=6 | 12 | 9–10 | 12 |
| Bessel n=6 | 12 | **196–200 (capped)** | 9–14 |

**The n=6 Bessel case was not merely slow — it hit the 200-iteration cap with the roots still
relatively wrong by a factor of 2.5**, and only the Newton polish afterwards made the answer usable at
all. `|a_n/a_0|^(1/n)` is the exact geometric mean of the root moduli (the product of the roots is
`a_n/a_0`), so it puts the set *on* the unit circle; the test is now relative. Residuals were compared
across 36 family/order/shape-parameter combinations and are equal or better everywhere. **16–24x on
the Bessel families.**

### The bracket refinement was 70 % of every `Gvalues` call, at 40 evaluations a bracket

`Search` runs 121 shape values, each scanning 17 points of the second parameter for sign changes, and
each bracket found was refined by **40 fixed bisection steps**. That is ~4,800 of ~6,900 `Gvalues`
calls. The Illinois variant of regula falsi keeps bisection's guarantee (the root stays bracketed, so
it cannot run away on a badly-shaped family the way a bare secant can) and reaches the same interval
in single figures. `SolveOther` also now returns the g-vector **with** the root, which removes the
caller's re-evaluation at the answer — the one `Gvalues` result the old loop computed and threw away.

### The denominator does not depend on the second parameter, in either family

Chebyshev and Butterworth put `K` only in the numerator (`1 + e^2 F^2` against `K + e^2 F^2`); Bessel
puts `C` only in the numerator. But the search holds the shape parameter fixed and sweeps the other
one, so the same denominator was being spectrally factored dozens of times per shape value. Since the
two `Hurwitz` calls are the whole cost of `Gvalues`, hoisting one halves it. A one-entry memo is
enough — the access pattern is a run of calls at one shape value — and it is created per `Search`
call and never shared, which is what keeps it safe on a background thread.

### Round 1 of the refinement computes a number that is only ever printed in a refusal

`Search` refines around **both** optima: the best score, and the family's maximum `Q_far`.
`MaxQFar` is read in exactly one place — `MatchSynthesis`'s `ResponseInfeasible` message — and that
branch is only reached when **no member was feasible**. So when one was, round 1 spent 44 of the
method's 121 shape values on a sentence that was not going to be written. Skipping it changes no
g-vector and no refusal.

**Together: order-4 Butterworth 102 -> 19 ms, order-4 Bessel 199 -> 30 ms, order-6 Butterworth
295 -> 51 ms, order-6 Bessel 342 -> 50 ms.** `Core.Tests`' own Match suite went from 6 s to 2 s.

### `Synthesize` was called four to twenty times per refresh, on identical inputs

`MatchRebuild.Rebuild` does one, `MatchFlattenService.Availability` rebuilds to ask whether there is a
ladder to write, `MatchSolutionSearch.Search` needs the same basis, the Designer probes each of the
four response families — of which the selected one is, again, the same call — and
`MatchSolutionSearch.FindQAdjust` bisects fifteen times with a synthesis inside every step. On a
Chebyshev design that is repetitions of ~2 us. On a Butterworth one it was repetitions of 50 ms.

The memo keys on the complete set of fields `Synthesize` actually reads: F1, F2, Order, Response,
RippleDb, QAdjust, AnalysisEnd and the two `Termination` records. **The transforms are deliberately
absent — they are applied to the basis afterwards and the synthesis never sees them, which is exactly
why this pays on a slider drag**: a non-specification refresh with Butterworth selected went from
**110 ms to 1.5 ms**, because every step of the gesture had been re-deriving a basis that cannot have
changed.

**What comes out is a copy.** The result carries a mutable `MatchNetwork` and a `double[]`; handing
one instance to two callers would let one caller's edit appear in the other's result. Every in-repo
caller happens to treat both as read-only today, and that is not something the cache gets to depend
on.


## MN-LP — lowpass and highpass form (2026-08-28)

match.md §16, §6.9 and brief `brief-match-lowpass-highpass-form.md`. Everything below is a place where
the design note and the implementation disagree, and the implementation is what was measured.

### The orientation is decided by the impedance RATIO, not by the analysis end's topology

**§16.3 and brief §3.3 both say the ladder starts shunt-first for a parallel analysis end and
series-first for a series one, "exactly as today", and that a required orientation which does not
extract is a refusal. There is no such choice to make.**

The family depends on the terminations only through `Γ₀²`, which is invariant under `r ↔ 1/r`, so one
extraction serves both — and the terminating value it produces is `max(r, 1/r)` in **every** cell
measured (5 orders × 6 bandwidths × 6 ratios × 2 families, 360 cells, exact to 1e-13). Reading that
g-vector as a series-first ladder puts `R_far = g_last · R_ana`; reading it as its dual, shunt-first,
puts `R_far = R_ana / g_last`. Exactly one lands on the requested resistance.

Said physically, which is how to remember it: **the low-impedance port sees a series inductor and the
high-impedance port sees a shunt capacitor** (lowpass; series C and shunt L for highpass). That is the
L-match rule, and every order inherits it.

**The consequence is a constraint on absorption that §16.4 does not state.** Its item 1 — a lowpass
ladder absorbs `R ‖ C` and `R + L` — is true, but *which end* takes which is fixed by the ratio. A
shunt capacitance on the LOW-impedance side of a step-up is absorbable by neither lowpass nor highpass
form, and its refusal has to blame the ratio rather than the kind, because "use highpass form" is
wrong advice there. `MatchFormSynthesis.CheckAbsorbable` writes two different sentences for the two
cases; `AnAbsorbableKindOnTheWrongEnd_IsRefusedForTheRatio_NotForTheKind` holds them apart.

A corollary worth knowing before hunting for a bug in the search: **a pair with a reactance at each end
can only ever list ONE of lowpass and highpass**, because the two forms want dual kinds. Only a purely
resistive pair lists all three forms.

### §3.7's physical golden values do not describe a 50 Ω far end

The normalised g-vector is exact and reproduces to 1e-9 (`2.485340, 0.674662, 6.761736, 0.247821,
10.000000` at a = 0.5, n = 2, r = 10, K = 1e-6). The physical line beneath it —
`C1 = 15.8222 pF, L1 = 107.376 pH, C2 = 43.0466 pF, L2 = 39.4420 pH` for *"5 Ω (analysis, parallel
side) → 50 Ω"* — is those g's denormalised at R_ana = 5 Ω and read **shunt-first**, which by the
paragraph above is the r = 0.1 network: 5 Ω down to **0.5 Ω**. Simulated as printed into 50 Ω it is a
**−0.10 dB** match, not the −10.511 dB the same block quotes. Series-first at R_ana = 5 Ω into 50 Ω,
and shunt-first at R_ana = 50 Ω into 5 Ω, both give −10.511 dB exactly.

Goldens B, C and D are all self-consistent as the 5 → 0.5 Ω problem (`g₁_actual = ω_c·R·C` uses
R = 5 Ω throughout), which is how the slip was located rather than guessed at. **Only the "→ 50 Ω"
annotation is wrong**; every number in §16.2's table, and Golden B's `K = 0.086588` with its −8.010 dB,
reproduce to the digits printed.

### The polynomial route cannot solve this family past order 4 — and the fix is two changes, not one

§16.3 says §6.2's spectral factorisation and Cauer extraction "apply unchanged". They do in principle
and do not in double precision: the polynomials are degree **4n in s** (24 at order 6) with
coefficients spanning many decades. Following `MatchPrototypes` literally — build the polynomial,
root-find it, `Extract` — **fails 144 of the 360-cell sweep, including every order-6 cell at every K.**
`MatchFormPrototype` takes it to **zero** with two independent changes; either alone is not enough:

1. **The roots are written down, not searched for.** Both polynomials are `c + ε²Φ`, so their roots
   solve `Φ(x) = −c/ε²` and map through `x → u → s` by exact arithmetic. One arccosine (Chebyshev) or
   one root of unity (Butterworth). No degree-4n polynomial is ever formed. Take the arccosine in the
   `π/2 − j·asinh(y)` form — its argument is `+jy`, the branch where `−j log(z + j√(1−z²))` has no
   cancellation; the other sign of y is the branch where it does.
2. **The continued fraction's degree drop is STRUCTURAL.** `MatchPrototypes.Extract` subtracts `g_k s`
   and then asks a tolerance which leading coefficients cancelled. The top **two** always do,
   identically — but at order 6 the second survives at ~1e-4 of its neighbours after twelve steps of
   cancellation, the length test fails, and the whole extraction returns null. Dropping both because
   they are known to be zero removes the question. **The last removal is the exception**: the remainder
   is then the terminating resistance itself, so only one comes off, which is what the
   `max(1, b.Length - 1)` is for. Getting that wrong costs the terminating ratio and nothing else
   visible — it just returns null.

None of this touched `MatchPrototypes`; the bandpass route is unchanged and still passes its own
cross-checks.

### K's floor is a RETURN-LOSS floor, and 1e-6 is too high

§16.9 is right that K = 0 is a trap (double jω-axis roots make "the left half" undefined and the
tie-break takes both copies of the lowest pairs) and right that the difference is "below 0.01 dB" — but
only above the floor it sets. **K is the in-band return-loss floor as well as a numerical guard**: the
worst `|Γ|²` is `(K + ε²)/(1 + ε²)`, so it caps the response at `10 log10 K` whatever the family could
otherwise reach. At 1e-6 that is −60 dB, which

- costs **0.12 dB** on §16.2's own −44.3 dB cell — twice the 0.05 dB the acceptance test allows, and
- puts the from-DC reduction **2.4e-3** off the textbook 0.1 dB table, against that test's 1e-5.

The error scales as √K and nothing degrades on the way down: every floor from 1e-6 to **1e-14**
extracts all 360 cells with the terminating ratio exact to 1e-13; only K = 0 fails. `KFloor = 1e-12` is
taken — the from-DC reduction reaches 2.4e-6 and the g-values are converged to five significant
figures. match.md's Golden A was computed at 1e-6 and its **Golden E at 1e-9** (its numbers are the
1e-9 values exactly, not the 1e-6 ones), which is how the inconsistency surfaced; both are asserted at
their own K rather than against the constant.

### K is not monotone in the near-end element

§16.4 item 3 calls this "a 1-D monotone problem" and both the brief and the design note prescribe a
bisection. The far element does fall monotonically. **The near element rises and then falls**, peaking
near `K = 0.95 Γ₀²` — measured, a = 0.5, n = 2, r = 10: g₁ goes 2.485 at the floor → **11.00 at
K = 0.64** → 4.35 at K = 0.66942 (Γ₀² = 0.669421). A bisection written for a monotone g₁ finds the
wrong end of the feasible interval or misses it, and it also under-reports the family's largest g₁ in
the refusal — a log grid samples the top of the range twice and gave 7.98 where the truth is 11.00.

What is implemented is a **linear** scan of 128 members over `[K_floor, Γ₀²)` followed by a geometric
bisection on the boundary it brackets. Linear because all the structure is in the top few per cent;
geometric on the refinement because K_min routinely sits decades below the first grid step.

### A bigger termination is HARDER at a higher order — the opposite of the bandpass intuition

With the ratio pinned, more elements means better return loss and a gentler ladder, so the **end
elements shrink with order**: at a = 0.5, r = 10 the near element is 2.485 at order 2 and 0.820 at
order 6. A termination sized for a bandpass order-4 network absorbs at order 2 and is refused at 5 and
6. This bit while choosing a Designer test fixture and would bite a user the same way; it is why
`MatchFormDesignerTests.Problem` uses 0.5 pF and 0.1 nH rather than the round numbers a bandpass
fixture would.

### `MatchSolutionSearch` had no way to say "finished, with no transforms"

`EnumerateSets` emits only NON-EMPTY transform sets, so a basis already on target produced zero
solutions and the panel reported `NoTransformablePairs` — a refusal — about a network that needs none.
§16.5's "each cell contributes exactly one solution" is only true with the extra emission that is now
in `SolutionsFor`, and it is written as a property of the **ratio** (`|required − 1| ≤ tolerance`)
rather than of the form, because that is what it is.

`NortonTransform.Discover` needed nothing: a single-element ladder alternates L-series with C-shunt, so
every like-kind pair shares an orientation and the existing `Opposite` test rejects all of them. It
still returns empty after the end splits, since a surplus element is the same kind and orientation as
the one it sits beside.

The Q-adjusted variant IS skipped in these forms (§16.4 item 3) and had to be skipped explicitly: it
produced a second, identical zero-transform row under a different label, and `FindQAdjust` bisects
toward "some transform set completes", which never happens here.

### The end split is linear in prototype units — all four combinations

`MatchQ.SplitExcess`'s C_eq algebra exists because a bandpass arm holds both an L and a C. A
lowpass/highpass end arm is one element, and **the surplus is `g_synth − g_actual` in every case**: a
shunt C's g is `ωRC` and parallel capacitances add; a series L's is `ωL/R` and series inductances add;
a shunt L's is `R/(ωL)` and parallel inductances add reciprocally, which is the same thing in g; a
series C's is `1/(ωRC)`, likewise. So `MatchFormSynthesis.WithEndSplits` is three lines, and the
absorbed element's value is the termination's own **exactly** rather than to a tolerance.

One deliberate consequence: the feasibility test requires `g_synth ≥ g_actual` with **no** slack. A
member that misses by one part in 1e9 would leave an element marked absorbed while carrying a value the
termination does not supply — the "a parasitic cannot be subtracted" failure §4.6's own 2026-08-20 note
records. The 1e-9 slack that does exist only widens the family's side, and only for the refusal's
wording.

### r = 1 has no member of this family, and is handled as a named special case

The pin reads `|Γ(0)| = 0`, which this family meets only at K = 0 — the trap. So for `Γ₀² ≤ 4·K_floor`
the pin is dropped, K sits on its floor and ε is fixed for a nominal −40 dB in-band, giving **one**
member and no search. Brief §3.2 says this "only matters for a purely resistive equal pair"; that is
where it is honest. A 1:1 match with a reactive end either fits that single member or is refused, and
the result carries a `Notes` line saying so. Making ε the search parameter in that branch would fix it
and was not done — it is a second search with its own bounds, for a case the brief calls degenerate.

### Two smaller things

- **§16.3's "Φ is degree n in u" is a slip.** `T_n(x)` is degree n in x and x is degree 1 in u, so the
  square is degree **2n**. Every conclusion the section draws from it (2n elements, degree 4n in s) is
  the arithmetic for 2n and is right.
- **`ValidOrders` had to take the form, and `AdjustOrderForParity` had to learn that the list can be
  EMPTY.** A like-topology pair has orders 3 and 5 in bandpass form and none in the other two, and the
  old code's `valid.OrderBy(...).First()` throws on an empty list. The solutions FILTER is built from
  the union across the three forms, not from the design's own form: a lowpass design with a like pair
  would otherwise have no order lines while the panel listed bandpass rows at 3 and 5, and `Accepts`
  shows a row whose order has no line — so every one of them would have been unhideable.

## MN-MB1 — dual-band matching, route A (2026-08-28)

match.md §18, §19 and brief `brief-match-multiband-dual.md`. **Unlike MN-LP, the design note's
numbers held: every golden value in §18.4 reproduced to the digits it prints.** What follows is what
the implementation found that the note did not say, plus the numbers it left open.

### §18.4's goldens, all of them, reproduced

Computed here through `MatchFormPrototype.GvaluesAt` + `MatchSynthesis.Build` and scored against the
independent ABCD oracle in `tests/Core.Tests/Match/MatchAbcdOracle.cs`:

| quantity | §18.4 | measured |
|---|---|---|
| effective f1 (band 1 widened) | 2.2008547 GHz | 2.2008547 GHz |
| ω₀/2π, `w`, `a` | 3.5881750 GHz, 1.0169920, 0.7261974 | identical to 8 s.f. |
| Q at ω₀ | 1.1272584 | 1.1272584 |
| g (n = 2, at its stated K, ε²) | 1.1464128, 0.9341372, 2.4818781, 0.4175330, 2.6060058 | agrees to 2e-7 |
| four arms, pH/pF | 786.96065 / 2.5000000, 814.83495 / 2.4144787, 363.50768 / 5.4122698, 364.20823 / 5.4018594 | agrees to 5e-8 relative |
| R_far, Π N² | 7.674580 Ω, 6.515014 | 7.6745793 Ω, 6.5150151 |
| worst \|S11\|, gap max | −31.793 dB, 0.4454 | −31.7933 dB, 0.44537 |
| n = 1 | −19.477 dB, gap 0.3192, R_far 10.321731 Ω | −19.4769 dB, 0.31919, 10.32173 Ω |
| n = 3 | −41.505 dB, gap 0.7122, R_far 3.361512 Ω | −41.5054 dB, 0.71222, 3.361512 Ω |

The absorbed shunt C comes out **2.5000000 pF** — the load's own, to machine precision — which is
§18.9's identity holding.

**Beats single-band by 12.95 dB.** The same eight elements as a Fano-optimum Chebyshev over the whole
2.2009–5.85 GHz span reach −18.847 dB across the two bands; the dual-band network reaches −31.793.
(§18.4's own table says −18.8, and the brief's scratch figure was 13 dB.)

### The search lands at a different K, at the same return loss — as §18.4 predicted it would

The 64-point log scan plus the bounded golden-section settles at **K = 3.890e-5, ε² = 6.2326e-4**
against the note's member at K = 3.654984e-5, ε² = 6.255720e-4. The worst return loss differs by
**0.0002 dB**. This is exactly the insensitivity §18.4 records (≤ 0.1 dB per decade of K), and it is
why the golden test asserts the MEMBER through `GvaluesAt` at its own parameters and the SEARCH only
to 0.05 dB. Conflating the two would make the golden test a statement about the search.

### **No K row shows two brackets in ε²** — §18.9's caveat did not fire

Measured over the whole 64-point K scan at n = 2 on §18.4's problem: every K that brackets the target
at all brackets it **once**. So the "take the one with the smaller ε²" rule the brief asks for is
implemented (the scan breaks on the FIRST sign change) but has never been exercised. g₁ was monotone
in ε² for fixed K everywhere it was looked at, as §18.9 says.

### `GvaluesAt`'s order floor had to come down to 1

It refused `n < MatchOrders.MinOrder`, and `MinOrder` is **2**. **A dual-band order 1 is a real
order** — four elements, one match point per band, and §18.4 quotes its golden values — so the guard
is now `n < 1`. Nothing else changed: the lowpass and highpass order pickers still start at 2, so no
existing path can reach n = 1 through this family.

### Order 1 synthesises and then has no SOLUTION, for a structural reason

On §18.4's own problem the order-1 basis extracts cleanly (−19.48 dB) and the panel still lists
nothing at that order. It is not a defect and not the synthesis: a four-element ladder has exactly
**one** Norton pair, and one transform cannot reach the 4.844 : 1 the far end needs inside its
positivity range. Orders 2 and 3 have five and nine pairs and produce 6 and 33 solutions. The picker
offers 1 anyway — the basis is real, and `MatchSolutionSearch`'s own `TransformsCannotReachTarget` is
the honest thing for the panel to say. `MatchMultibandDesignerTests` asserts orders 2 and 3 are
present and that every listed order is inside 1..3, rather than asserting all three, for this reason.

### Butterworth is 4.5 dB worse, which §18.4 left open

n = 2 on §18.4's problem: **Butterworth −27.299 dB against Chebyshev's −31.793 dB**, R_far 8.861 Ω
against 7.675 Ω, so it also needs a smaller Π N² (5.642 against 6.515). It extracts at every order
1..3.

### The single-band `EffectiveBands` is (F1, F2, F2, F2), on purpose

`MatchDesign.Effective` has to serve `Omega0` and `W` in both modes, and those are the outer pair.
Reporting `(F1, F2, F2, F2)` makes `sqrt(F1·F4)` and `(F4 − F1)/sqrt(F1·F4)` read as the single-band
values with no branch, and makes the gap `(F2, F3)` empty rather than negative. `(F1, F2, F1, F2)`
would have put F3 below F2, which reads as a spec that is not increasing.

### `WithEndSplits` on the BASIS is not the same call as on a solution

Caught by a test, worth knowing before someone else writes one. The split's exactness — the kept
value equalling the termination's own — holds only once the far port has reached its target
resistance, which is `MatchSynthesis.WithEndSplits`' own documented reason for running LAST. Applied
to the basis, whose far port is still at `RFarSynthesised`, it is a legal call that produces a
different (and correct-for-that-network) number. A test that wants the termination's own value must
read it off a `MatchSolution`, not off `Synthesize(...).Network`.

### One deliberate departure from the brief's out-of-scope list

Brief §9 says not to change `MatchSolutionSearch`. One line was changed anyway, and it has to be:
`MatchSolution.WorstReturnLossDb` is what a solution CARD shows, and brief §4.1 requires that number
to be the worst over both effective bands. It now reads
`MatchResponse.WorstReturnLossDb(network, design.Bands)`; for a single band `design.Bands` is
`[(F1, F2)]` at the same 201 points, so the single-band number is bit-identical to what it was.
Nothing structural — the ladder, the pair scan, the ranking and the enumeration are untouched.

### `PlotControl` has no band shading, so nothing is drawn

Brief §4.3 asks for the two in-band spans to be shaded on the |S11| plot "using whatever band-shading
`PlotControl` already offers — if it offers none, draw nothing rather than adding a renderer
feature". **It offers none**: `Plot` carries traces, axes, markers and table state, and there is no
region/span primitive anywhere in `src/Ui/DataDisplay`. So no shading was added. The plot band
default did move to the EFFECTIVE outer pair, so both bands and the gap are on screen, which is the
half of §18.7 that did not need a renderer.

## MN-MB2 — tri-band, the multi-interval Remez family, and the odd element counts (2026-08-28)

match.md §18.5 and brief `brief-match-multiband-tri-remez.md`. **Both milestones landed.** The design
note's tri-band targets reproduced; three of its statements did not, and the corrections are the
first four sections here.

### The design note's numbers, reproduced

Bands 0.5–0.6 / 0.9–1.1 / 1.65–1.98 GHz (already exact log-mirrors about √(f3·f4) = 0.994987 GHz), a
50 Ω ‖ 4 pF analysis end into 50 Ω, Chebyshev. `w = 1.487456`, prototype intervals
`[0, 0.018262] ∪ [0.503333, 1]`:

| order | elements | §18.5 says | measured | R_far | gap max \|S11\| (both gaps) |
|---|---|---|---|---|---|
| 1 | 4 | — | **−8.941 dB** | 23.679 Ω | 0.3511 |
| 2 | 8 | −12.0 dB, R_far ≈ 29.9 Ω | **−11.997 dB** | 29.918 Ω | 0.2513 |
| 3 | 12 | −14.5 dB | **−14.473 dB** | 34.107 Ω | 0.3484 |

Ladder values 2.24–7.06 nH and 3.62–11.41 pF, against the note's illustrative 2.5–7 nH / 3.6–10.5 pF.
The absorbed shunt C comes out **4.0000000 pF** — the load's own — which is §18.9's identity holding
for the Remez family too. **All three bands come out at the same worst return loss to 0.01 dB**,
which is the equiripple property surviving the resonating transform, and the two gaps come out equal
because the mirrored spec is symmetric.

### **The overlap refusal is unreachable, and that is a theorem**

§18.5 asks for a refusal when a widened outer band reaches the middle one. It cannot happen:
`f2' = max(f2, f0²/f5)`, and `f0²/f5 < f0²/f4 = f3` because `f5 > f4`, so **both** arguments of that
max are already below f3. The mirror image of a band above f4 always lands below f3. The symmetric
statement holds at the other end. The guard is implemented anyway (`EffectiveBands.Overlaps`, and the
synthesis names the remedy) because a spec that is not yet ordered reaches the derived properties
mid-edit; `MatchTribandSynthesisTests` asserts the theorem over 20,000 random ordered specs instead of
producing a case that refuses.

### **Butterworth has no tri-band member, structurally**

The maximally-flat member is `x(u)^{2n}` — flat at the centre of ONE interval — and a union of
intervals has no such point to be flat at. On a union the equiripple polynomial is the only member of
this family, which is what a Remez exchange computes. So tri-band offers **Chebyshev only**, the
refusal says why rather than reporting a failure, and the solutions search does not enumerate the
cells that would produce a column of identical refusals. This is a departure from the brief's "× two
families" sweep, taken deliberately; the two families the sweep does cover are the even and weighted
ones.

### **The odd count is about ABSORPTION, not about return loss** — §16.3's claim, corrected

The brief expected the 5-element member to beat the 4-element one. It does not, and cannot.
`Φ(0) = uR · R_k(0)²` is what the DC pin is written against, and it rises **monotonically** in uR
toward the even family's own `T_k(x0)²` without ever passing it. Measured at a = 0.5, r = 5:

| order n | even, 2n elements | odd, 2n + 1 elements | even, 2n − 2 elements |
|---|---|---|---|
| 2 | −14.304 dB | −14.303 dB | −6.505 dB |
| 3 | −23.607 dB | −23.605 dB | −14.304 dB |
| 4 | −33.122 dB | −33.120 dB | −23.607 dB |

So the odd rung sits **0.002 dB under the even member of the same order**, and comfortably above the
even member below it — §16.3's own "approaches the even count below it as the extra element vanishes"
read the right way round. What the odd count buys is a ladder whose **two ends share one
orientation**, which the even family cannot produce at any order and without which the classic
shunt-C-to-shunt-C interstage has no network at all. That is why the parity is decided by the
TERMINATIONS (`MatchOrders.NeedsOddCount`) and not offered as a user choice, and why order keeps
meaning match points in both families.

### **An odd extraction's terminating value is a CONDUCTANCE ratio** — the parity trap

Each Cauer removal swaps impedance for admittance, so the remainder after an odd number of them is
the reciprocal of what it is after an even number: `Extract` returns **min(r, 1/r)** for an odd count
where it returns max(r, 1/r) for an even one. `MatchFormSynthesis` was reading the far resistance off
`shuntFirst`, the NEAR end's orientation — correct only for an even count — which inverted the far
port and turned a −14.3 dB match into **−0.5 dB with every element value correct**. Keying on the FAR
element's own orientation states it once for both parities and is bit-identical for even counts:

```csharp
double rFarSynth = farIsShunt ? chosen[m] * rAna : rAna / chosen[m];
```

`MatchSynthesis.Build` (bandpass) never had the bug — it already keys on `s[n + 1]`, the far arm's own
flag, which tracks parity by construction. Verified by rebuilding the odd multiband ladders with the
reciprocal far resistance: −20.2 → −6.6 dB, −31.6 → −3.0 dB, −41.3 → −0.6 dB.

**The orientation itself does NOT flip**: `shuntFirst = ratio < 1` for both parities. With an odd
count both ends then take that orientation, so a like pair of PARALLEL ends must be analysed from the
HIGHER resistance and a like pair of SERIES ends from the lower. The other way round is a refusal
naming the analysis end rather than the form, because an odd ladder's ends flip together.

### The far-end feasibility test was in the wrong units (pre-existing, MN-LP)

Every element `MatchFormSynthesis` builds is denormalised at **R_ana** — the lowpass prototype's own
convention, in which the terminating resistance is a ratio and the element values do not rescale at
the far port. The near end's own reactance was being normalised at R_ana and the far end's at
`rFarTarget`, so the two sides of the far-end test were a factor of the ratio apart. Measured: a
50 Ω ‖ 0.4 pF to 10 Ω + 1 nH lowpass design builds a **1.4755 nH** far element, which absorbs the
termination's 1 nH with room to spare, and it was refused (1.2566 against 0.3708). `WithEndSplits` was
never affected — it reads both sides at the end's own resistance, a consistent pair — so no shipped
network was ever wrong; the form simply refused more than it had to. Fixed, and no existing test moved.

### Roots in u are exact; the EXTRACTION is what limits agreement at order 6

Brief §3 asks for `GvaluesAtPolynomial ≡ GvaluesAt` to 1e-9 over §MN-LP's 360-cell sweep. **All 360
cells extract through both routes**, and the g-vectors agree to **9.2e-11 at n ≤ 4** — but only to
**7.3e-5 at n = 5, 6**, and no implementation can do better. Measured, in this order:

- the two routes' roots **in u** agree to **2–5e-16** in every cell, machine precision, and everything
  downstream of the roots is literally the same code;
- `Extract` amplifies an **incoherent** one-ulp perturbation of the root set by about **1e11** at
  order 6 — twelve steps of deliberate cancellation, which is §MN-LP's own finding restated;
- a **coherent** perturbation (moving ε² itself, so every root slides along the family) is amplified
  by only 2e5 — which is why the family is perfectly usable and why this is a statement about two
  implementations of one member rather than about the member;
- the two ladders' **response** agrees to **6.7e-6 dB** even where the g-vectors differ in the fifth
  digit.

The multiband path asks for n ≤ 3, where the routes agree to 1e-10, so nothing operational is
affected. Two smaller findings on the way there:

- **`LeftHalfPlane` now SORTS the root set before accumulating the product.** `MatchPoly.FromRoots`
  multiplies linear factors in the order it is handed them, and at degree 12 the partial products span
  decades, so two orderings of the SAME set differ in their last bits — worth 1.4e-4 → 7.3e-5 of the
  disagreement above, purely because one route emits roots by arccosine and the other by
  Durand–Kerner. Sorting makes the accumulation a function of the set rather than of how it was found.
- **Coefficients in u are not good enough to root-find from past order 4.** A degree-6 polynomial whose
  roots cluster in [0.53, 1] has coefficients spanning five decades and Horner's rule cancels four of
  them. `MatchPrototypePolynomial` therefore carries the affine map the Remez exchange solved in, and
  `GvaluesAtPolynomial`'s scaled overload is the one the synthesis calls; the flat `double[]` overload
  the design note names is kept, and documented as good to four or five digits and no more.

### Remez convergence on a union — no surprises

The exchange reproduces the shifted Chebyshev polynomial on one interval to **4e-16 relative** at
every order 1–6 and every a in {0, 0.5, 0.73}, and the quadratic-mapping polynomial `T_m(q(u))` on two
equal-length intervals to **2e-15**. On unions with no closed form it equioscillates at exactly n + 1
points of magnitude 1 ± 1e-9 over every cell of a six-band-set × six-order sweep. **The zeros divide
themselves between the intervals** — nothing chooses how many go where, and for the tri-band sets
measured the split follows the intervals' lengths, so a very narrow middle band gets few or none at
low order. That is visible in the response as a middle band matched by the skirt of the outer bands'
ripple rather than by its own zeros, and it is the polynomial being right rather than a defect.

Two numerical notes worth keeping: the initial reference must be distributed across the intervals **in
proportion to their length** (an equal split on `[0, 0.05] ∪ [0.5, 1]` spends the whole iteration
budget walking points back out of the tiny interval), and the grid's job is to BRACKET each extremum
rather than to locate it — 2,001 Chebyshev-spaced points per interval plus a golden-section polish
inside the bracketing cell reaches machine precision, where a grid dense enough to do it alone would
need ~1e12 points.

### The weighted family degenerates past uR ≈ 1e7

`√uR · R_k` converges on the unweighted polynomial as **1/√uR** — 2.5e-4 at uR = 1e3, 2.5e-10 at
1e9 — and the extra element vanishes at the same rate. The EXTRACTION stops being conditioned first:
at uR = 1e8 the drift of the first 2k elements from the even family's own jumps to 0.33 while the
extra element is still 2e-4. The scan's grid therefore stops at 600 (lowpass) and 150 (multiband),
which is already past the point where the odd member has converged onto the even one.

### Cost

The odd family's search is ~10 pole positions × a 12-point K scan, then one full 64-point search at
the winner — **0.37 s at multiband order 1, 0.9 s at order 2, 2.2 s at order 3**, against milliseconds
for the even family, because a weighted member is two Durand–Kerner runs at degree 2n + 1 and an
extraction where an even member is one arccosine. A single-stage search at full K resolution measured
2.5 s at order 1 alone. It is background work in the solutions search and no timing test guards it, per
the brief.

The tri-band even family costs nothing extra: the Remez polynomial is memoised on (degree, uR, band
set), so the ~130 members one search asks for share a single exchange.

## MN-FH — feasibility hints: the Fano ceiling and the gap rise (2026-08-28)

Owner report: a tri-band spec — 100 Ω ‖ 0.125 pF into 1.25 Ω + 5 pF series, bands 2.5–3 / 4.5–5 /
9–10 GHz — produced two solutions, both a flat −2.6…−3.0 dB from 2.25 to 10 GHz. **The synthesis was
correct.** Two closed-form facts explained it and nothing on screen said either. `MatchFanoBound`
now says both. Full design note: `docs/design/match.md` §18.10, decisions §21.

### The ceiling is a theorem, and it held on every fixture

`NoSynthesisedNetwork_BeatsItsOwnFanoCeiling` runs §4.9's interstage problem, §16.2's Golden B and an
inductive highpass dual, §18.4's dual-band problem at n = 1 and 2, §18.5's tri-band problem at n = 1
and 2, and the owner's own fixture through the ABCD oracle. **No fixture beat its ceiling**, and the
smallest headroom is 3.83 dB (the owner's, at −2.60 against −6.43). If that test ever goes red the
weight class in `MatchFanoBound.WeightOf` is wrong for that termination kind — it is not a fixture
drifting.

Measured headroom, for anyone sizing a future gate: golden n=4 4.14 dB · lowpass Golden B 5.89 dB ·
highpass shunt L 58.07 dB · dual n=1 67.46 dB · dual n=2 55.14 dB · tri n=1 25.53 dB · tri n=2
22.47 dB · owner tri n=2 3.83 dB.

### The brief's rise at order 6 is 18.0, not 17.8

Every other gap-rise figure in the brief reproduced to the digit (0.99 / 0.97 / 1.16 / 2.90 / 8.77 at
orders 1–5, `GapOpensAtOrder` = 4). Order 6 measures **18.0054** on a 4,001-point scan against the
brief's 17.8; an independent Python re-implementation of the same exchange gives 18.0054 as well, so
the number recorded here is the one to quote. It changes nothing — the threshold is ×2 and order 6 is
not offered for three bands anyway.

### "At the ceiling" has to be judged against TWO ceilings, and the brief's own test proves it

The brief asks for "— at the ceiling" when the achieved worst RL is within 1.0 dB of the ceiling, and
separately asks the Designer test to see that suffix on the owner's fixture. **Those two cannot both
be satisfied by one comparison.** The owner's network reaches −2.60 dB; the ceiling over the bands is
−6.43 dB (3.83 dB away, not within 1.0) and the ceiling over the outer span is −3.11 dB (0.51 dB away).

Both are real walls and they mean different things. Within a dB of the BAND ceiling: nothing lossless
does better over these bands. Within a dB of the OUTER-SPAN ceiling: the network is spending its whole
budget across the span instead of excluding the gaps — which is exactly the failure the owner
reported, and calling it "not at the ceiling" because it missed a number no order on offer can reach
would be the wrong half of the truth. So the suffix fires on **either**, and the gap-rise note is what
tells the two apart. `MatchStatus.CeilingText` carries the reasoning.

### A ceiling depends on RC only, so ω₀ is inert — which is what licenses reading Q

`For` reads `QAt(ω₀)` rather than `Kind`/`Value` for the arithmetic, because `Q·ω₀` reproduces `R·C`
(or `L/R`) for all four termination combinations. `TheCeiling_DoesNotDependOnWhereOmega0Is` asserts
invariance over 0.1×..10× to 1e-12 relative rather than leaving it as a claim, and
`TheInductiveDual_GivesTheSameCeilingAsItsCapacitiveTwin` pins the two dual pairs (parallel C ↔ series
L, series C ↔ parallel L) so an inductive end never needs a second formula.

### "Better ceiling" is the MORE NEGATIVE one — the mirror remedy got this backwards first

A ceiling of −13.8 dB permits a deeper match than one of −9.6 dB, the same way an achieved return loss
of −13.8 dB is the better match. But the BINDING end is the one with the LESS negative ceiling (the
smaller budget). The two comparisons run in opposite directions in the same file and the mirror remedy
was written with the binding-end comparison, which silently offered the worse of its two candidates
(2.5–3 / 4.5–5 / 7.5–9 GHz at −9.6 dB instead of 2.25–2.5 / 4.5–5 / 9–10 GHz at −13.8 dB). Both
candidates pass `Symmetrise3` with `Widened = false`, so nothing downstream would have caught it.

### The drop remedy has to RE-SYMMETRISE, or its number is 13 dB wrong

"Without band 1 the ceiling over bands 2 and 3" is not the ceiling over the effective bands 2 and 3 of
the tri-band spec — those were widened to mirror a band that is now gone. It is the ceiling of the
DUAL spec made from the remaining TYPED bands, put back through `MatchBands.Symmetrise`. On the
owner's fixture the typed bands 4.5–5 and 9–10 already mirror exactly (5/4.5 = 10/9), so nothing
moves and the answer is −32.1 dB; reading the tri-band effective bands 4.5–5 and 7.5–10 instead gives
−19.3 dB, which is the ceiling of a spec nobody is proposing.

### The unphysical-direction rule is what stops a hint that makes the match worse

`AddReactance` solves for the value that meets the target and then checks it lies the LOOSENING way —
smaller for the Δω class (shunt C, series L), larger for the 1/ω² class (series C, shunt L). Without
that check a design already better than the target is told to grow its shunt capacitance. The
direction is the class's own inequality, not a heuristic.

### One code path serves both band counts, and the dual case is the witness

`EffectiveBands.Intervals` returns ONE interval `[a², 1]` for dual-band, on which the exchange returns
the shifted Chebyshev polynomial; the gap in u is then everything below it and the rise is
`cosh(n·arccosh((1+a²)/(1−a²)))`, matched to 1e-9 at every order on §18.4's fixture. The first draft
of `GapRise` guarded on `intervals.Count < 2` and silently returned no rise for every dual-band
design — the guard is `Count == 0`.

### Milestone 3 — reported, not started

The rise table says orders 4/5/6 are where a narrow-middle-band tri-band becomes three bands
(×2.90 / ×8.77 / ×18.0 against ×0.97 at order 2). The cost is elements: `ValidOrders` caps multiband
at 3 because the count is 4n (mixed pair) or 4n + 2 (like pair), so 4/5/6 are **16 / 20 / 24**
elements (18 / 22 / 26 like) against order 3's 12. `GvaluesAtPolynomial` is proven to degree 6 on the
single-interval sweep but **has never been run at degree 4–6 on a UNION**, so raising the cap is a
measurement job. Owner-gated; not begun.

## MN-FH milestone 3 — tri-band orders 4, 5 and 6 (2026-08-28)

Owner said go, on the strength of §18.10's rise table. `MatchOrders.ValidOrders` now returns
`[1..6]` for `bandCount >= 3`; **dual-band still stops at 3**, and the asymmetry is deliberate — a
dual-band prototype's passband is one interval and it excludes its gap from order 1 (rise 3.2 at
n = 1), so it has no low-order failure mode to escape. `MaxTriBandOrder` is the new constant;
`MaxMultibandOrder` now means the dual-band cap and is documented as such.

### Every cell extracts, and the extraction is right at degree 4-6 on a union

The whole point of the measurement: `GvaluesAtPolynomial` had only ever been proven to degree 6 on a
SINGLE interval, and the design note is explicit that what decays with degree there is the Cauer
extraction's conditioning, not the root finding. Three band sets × orders 1–6 through the ABCD oracle:

| band set | 1 | 2 | 3 | **4** | **5** | **6** |
|---|---|---|---|---|---|---|
| narrow (0.5–0.6 / 0.9–1.1 / 1.65–1.98) | −8.94 | −12.00 | −14.47 | **−18.93** | **−16.29** | **−18.42** |
| wide middle (0.5–0.6 / 0.8–1.25 / 1.65–1.98) | −8.83 | −11.84 | −13.98 | **−13.50** | **−14.00** | **−17.31** |
| wide outers (0.4–0.7 / 0.9–1.1 / 1.4–2.5) | −6.42 | −8.53 | −9.26 | **−9.61** | **−9.80** | **−9.95** |

**No cell refused**, the element count is 4n throughout (24 at order 6), all three bands come out at
the same depth to 0.05 dB in every cell, and the extraction reaches `(K + ε²)/(1 + ε²)` to well under
0.05 dB at every degree 1–6 on a union.

### **Higher order is NOT monotonically better, and no test may assert that it is**

Read the narrow row: order 4 reaches −18.93 dB and order 5 only −16.29. The wide-middle row does the
same at 3 → 4 (−13.98 → −13.50). This is not an extraction defect — every one of those members hits
its own closed form exactly. **A higher degree buys a bigger polynomial, not a better match**: the K
optimum moves between orders, and §18.4's "insensitive to K by 0.1–1 dB" does not carry to a union,
where the swing between adjacent orders reaches 2.6 dB. The gap rise tracks it (order 4 rises ×7.9,
order 5 only ×4.64), so the two are the same story.

Consequences: the solutions list already spans every order and ranks by return loss, which is the
right way to choose; the order picker must never be presented as "more is better"; and
`EveryOrder_PutsAllThreeBandsAtTheSameDepth` deliberately checks the equiripple signature rather than
a monotone improvement.

### The "wide outers" set never opens its gaps at any offered order

Its rises are 0.98 / 0.93 / 0.84 / 0.90 / 1.02 / 1.38 — never above the ×2 threshold — so
`GapOpensAtOrder` returns 0 and §18.10's note says so in as many words. The worst return loss
plateaus at −9.95 dB against a −13.5 dB ceiling: **twenty-four elements buy 0.7 dB over twelve there**,
which is the case the note exists to stop someone paying for. Its bands are far apart in ratio
bandwidth, so §18.3's mirroring widens them heavily and the union's two intervals nearly merge.

---

## MN-DCB — the DC block on an end shunt inductor (2026-08-28)

match.md §22, implemented as specified: one property (`MatchElement.DcBlock`), one post-rebuild step
(`MatchDcBlock.Apply`, called only from `MatchRebuild.Rebuild`, after `WithEndSplits`), and four
consumers taught to honour it — `MatchResponse.At`, `MatchModel`'s stamp, `MatchFlattenPlan.Build` and
the Designer's drawing. `MatchSynthesis`, `NortonTransform`, `MatchSolutionSearch`, `MatchElementSolve`
and both fingerprints are untouched and see an ordinary shunt inductor, which is verified rather than
asserted: `SettingABlock_ChangesNothingButTheHostInductor` compares the two rebuilds element by
element.

### §22.2's table reproduces — except for the spread column, which was the ESTIMATE

The section's own numbers were cross-checked against `MatchDcBlockTests` as the brief asked. L′, f_s
and the L_eff values all come out; three things did not, and §22.2 has been corrected in place.

- **The parenthesised spread is the second-order estimate, not the half-range of the L_eff values
  printed beside it.** `2 (f_s/f₀)² (Δf_half/f₀)` gives ±2.29 % / ±1.21 % / ±0.13 % where the exact
  half-range of the same rows is ±2.60 % / ±1.30 % / ±0.13 %. The estimate is not merely low, it is
  **asymmetric-blind**: the 500 pF row actually runs **−2.86 % at 1.8 GHz and +2.34 % at 2.2 GHz**,
  because `1/(ω²C)` is not symmetric about ω₀, and the estimate happens to track the upper edge. The
  status line and `MatchDcBlock.BandSpread` quote the exact half-range — a single "±" number for a
  range is its half-range, and reporting the smaller of the two edge deviations would understate the
  cost at exactly the edge where it is worst. The `Δf` in the section's formula is also the HALF
  bandwidth; with the full one it is twice what the table says.
- **The middle L_eff column is at ω₀ (1.98997 GHz), not at the 2.0 GHz it is labelled.** L_eff is
  99.5 pH there by construction; at a literal 2.0 GHz the 500 pF row reads 99.628 pH.
- **"< 0.1 % at ≥ 10 nF" is optimistic** — 10 nF measures ±0.13 %.

### The worst-RL column is not reproducible, and that is recorded rather than tuned away

At L₁ = 99.5 pH (which this synthesis reaches at Q-adjust **3.2152** on the §22.4 drain spec) the
Chebyshev/Fano order-4 ladder into 50 Ω is **30.73 dB** block-free, not the 21.6 dB §22.2 quotes — so
the scratch ladder that produced the RL column was not the one this repo synthesises. The
DIFFERENCES, which is what the column exists to show, reproduce and are starker:

| C_blk | compensated | uncompensated |
|---|---|---|
| 500 pF | 23.60 dB | 9.87 dB |
| 1 nF | 26.60 dB | 15.57 dB |
| 10 nF | 30.24 dB | 27.55 dB |

### `f_s = f₀/√(k+1)`, not `f₀/√k` — the brief's warn-threshold expectation was off by that factor

For `C = k/(ω₀²L)` the compensation enlarges L to `L(1 + 1/k)`, so the branch resonates at
`f₀/√(k+1)`. The brief's test 2 expected `k = 25` to fire the `f_s > f₀/5` warning; it lands at
**f₀/5.099** and does not. The threshold stays where it is and the expectation moved: the status line
must quote the branch's REAL resonance, which is the number a user would measure. `k = 4` gives
f₀/√5 = f₀/2.236 and warns, as the brief's third row expects. The default `k = 100` is f₀/10.05.

### The brief's 0.05 dB tolerance for test 2 is a §22.2-fixture number, not a general one

On §4.9's ladder a default block costs **0.169 dB** of worst return loss, not under 0.05. Nothing is
wrong: §22.2's measurements are on a 20 %-bandwidth drain network and §4.9's band is 3.3–5.0 GHz — a
**42 % fractional bandwidth**, where the second-order residual has twice as far to run — and the
design sits at 16.66 dB, where a tenth of a dB is a small absolute change in |S11|. The test asserts
0.25 dB with every figure printed. Measured on the same ladder: `k = 25` costs 0.70 dB, `k = 4` costs
4.26 dB.

### At a very good match, dB is the wrong ruler for "unchanged"

`ANortonTransformOnTheFirstPair_DoesNotLoseTheBlock` was written against the brief's 0.05 dB and
failed at 0.78 dB — on a network matched to **−52 dB**, where |S11| is 0.0024 and three ten-thousandths
of a unit move the dB figure by a whole decibel. The comparison is in linear |S11|: block-free 0.002434
either side of the π (identical to 1e-12, since a Norton transform is exactly response-preserving —
asserted); with the default block, 0.003486 before and 0.003187 after. The block's residual is simply
computed against a differently-split inductor. The test also gates that the block **re-attaches to the
product** (`L1_N1_1`), which is the claim it exists for.

### §4.9's golden ladder offers no Norton pair that names L1

Its `CFano` and its absorbed `C1` both hang off `p1` between `L1` and `L2`, and a pair is two like-type
elements in adjacent arms — so `Discover` returns `L2/L3`, `C2/C3`, `L3/L4` and nothing touching the
end shunt inductor. The transform-survival test uses §22's own drain network (4 Ω ‖ 30 pF into 50 Ω),
which does offer `L1/L2`.

### Two shunt inductors on one node did not occur

`EndShuntInductorIndex` takes the first, as the brief allows. Across every fixture exercised — bandpass
(with and without a Fano/detune element), highpass, dual-band, and after a π on the first pair — no end
node ever carried two, which is what the ladder's own construction predicts: two shunt inductors on one
node would combine. The one collision that IS representable is the degenerate ladder whose two port
nets are the same node (a single shunt arm, no through element); `Apply` resolves both ends up front
and reports the second as stored-not-applied rather than overwriting the first's compensation.

### A highpass block needs an end arm the TERMINATION does not supply

In highpass form each arm is one element, so if the end shunt inductor is the termination's own
absorbed L there is nothing left at that node for a block to sit in and `EndShuntInductorIndex`
correctly returns −1. It is not a gap: the block belongs in OUR inductor, never in the external
network's. Where `WithEndSplits` has inserted an `LExcess1` beside the absorbed one, that is found and
used — which is the right answer and is why the resolution is "first non-absorbed shunt L on the node"
rather than "first shunt L".

## MN-DCB2 — the DC block follows the DC PATH, not the end node (2026-08-28)

match.md §22.1 and §22.5 corrected (rev 7). `MatchDcBlock.EndShuntInductorIndex` — "the shunt
inductor at this end's node" — is replaced by `ResolveHost`, a walk inward from the termination over
the element list (which IS the DC path, since `AssignNets` derives the topology from that order):
absorbed elements are transparent, a shunt L is a host, a shunt C is invisible, a series L joins the
path, a series C stops the walk. `DcBlockNote` gains `Path` and `StopElementName`; nothing about the
compensation changed. The owner's two cases — §4.9's series-RC Term2 (host `L3` through `L4`) and a
Norton T on the drain network's (L1, L2) (host `L1_N1_2` through `L1_N1_1`) — are the fixtures.

### The brief's "at most one host per end" was wrong too — a π of inductors has two

The owner's third case, found on the first build: a Norton **π** on (L1, L2) is shunt `L1_N1_1` /
series `L1_N1_2` / shunt `L1_N1_3`, then `C2`. The series product passes DC, so blocking `L1_N1_1`
alone sends the bias straight through to `L1_N1_3`, which shorts it. The walk therefore does NOT stop
at a host: it records every real shunt inductor it reaches until a real series capacitor, and `Apply`
blocks each of them with the end's one value, each compensated on its own inductance, one note per
host (the Designer renders one status line per host and the flattened record one sentence per host).
`DcBlockHost` carries `Hosts` (outermost first) with `Index`/`Path` as the outermost's for the callers
that only need one; `DcBlockStop` is now why the walk ENDED (`SeriesCapacitor` / `EndOfLadder`) and
means "withheld" only when `Hosts` is empty. The T has one host (series / shunt / series, then the
arm's capacitor); the bases have one per end; the π has two; a chain of transforms could have more.
Test 9's "no node carries two real shunt inductors" still holds and still matters — it is what makes
one block per host the whole answer.

**The end a block belongs to must be read off the walk, not the net.** `MatchLadderLayout.DcBlockEndOf`
said "on `p1` → end 1, else 2", which was right while a host could only be an end node. The π's
second product is on `n1`, so typing into `L1_N1_3blk` wrote `Term2DcBlock` (which was 0) and the
edit was silently a no-op. `MatchDcBlock.EndOf(network, index)` answers from the two walks, with
termination 1 owning a shared host exactly as `Apply` claims it. The seed (`DcBlockDefault`) is sized
from the SMALLEST host so every branch resonates at or below f₀/10.

### Test 9's verification: no node carries two real shunt inductors — 43 ladders, none

Every golden fixture (§4.9, the §22 drain, §16.6 highpass, §18.4 dual-band, and a both-series-ends
ladder at orders 3 and 5) × {none, π, T} on the first and the last discoverable pair × the split
cases they reach. The sweep asserts it actually SAW a shunt `CFano`, a series `CFano`, a shunt
`CDetune` and a series `CDetune` (plus the highpass form's series `CExcess2`), so a synthesis change
cannot silently empty it. No node ever carried two — including after a T on the end pair, whose
shunt product lands on a NEW node between two series products, and after a π, whose two shunt
products are on two different nodes (which is exactly why both need a block — see above). The brief's
"two on one node → block both" branch was not needed; "two on one PATH → block both" was.

### The one collision that IS representable is now interior

Two series-RC ends at order 3 give series / shunt / series, so both walks land on the ONE shunt
inductor. Resolved as MN-DCB resolved the degenerate single-arm ladder: both ends up front, the first
end's block applied, the second end's note says "both ends of this ladder reach the same shunt
inductor". At order 5 the same terminations host two distinct interior blocks (`L2` through `L1`,
`L4` through `L5`).

### What the T costs, and why the test gates the residual's ORDER rather than a number

The brief's test 2 asks for 0.05 dB; MN-DCB already found dB the wrong ruler on the −52 dB drain
fixture. Measured, worst |S11| in band: block-free 0.002434 (52.27 dB) either side of the T;
with the default (f₀/10) block 0.004216 (47.50 dB) — a deviation of 1.8e-3, against 1.1e-3 on the
untransformed `L1` and 0.8e-3 after the π. The T's host is a LARGER inductor (131 pH vs 99.5) at a
node the response is more sensitive to, so the same second-order residual costs more. What proves
the block is correctly attached and compensated is that the residual is second order in the block:
**ten times the capacitance gives 0.002605 (51.68 dB), a deviation of 1.7e-4 — 10.4× smaller.** The
test asserts the ω₀ identity to 1e-12, the 10× shrink (≥ 5×), and that the network is still matched.

### The series-RC end, measured

§4.9's Term2: host `L3` 18.516 → 18.702 pH with the default 8.29 nF, f_s 404 MHz, spread ±0.43 %;
worst RL 16.66 → 15.82 dB (−0.84 dB, on a 42 % band — the same fixture effect MN-DCB recorded for
Term1's −0.17 dB, here on a smaller host inductor). The ABCD oracle, carrying the branch as an
explicit series L-C at the INTERIOR node, agrees with `MatchResponse.At` to 0 (bit-identical) at
401 points. At DC, the stamp shows the termination node open through `L4` (5.0 V) with the block and
shorted (0 V) without — `WithABlockOnTheSeriesRcEnd_TheTerminationNodeIsOpenThroughTheSeriesInductor`.

### A highpass series-C end names its excess `CExcess2`, not `CFano`

`MatchFormSynthesis.WithEndSplits` names the lowpass/highpass surplus element `CExcess{end}`; the
bandpass split names it `CFano`/`CDetune`. The walk does not care (it stops on any real series C),
but a test looking for the bandpass name in a highpass ladder would miss it — recorded so the next
reader does not.

### Found in passing: a Ui test that ran for minutes, not a deadlock

`MatchTribandDesignerTests.ALikePair_StillSynthesisesOverThreeBands` sat 10+ minutes in the owner's
run and reproduced alone. A hang dump (`dotnet test --blame-hang`, read with `dotnet-dump analyze
… clrstack -all`) showed no lock: the test thread was in `WaitForAnalysis` and the search thread was
BUSY in `MatchMultibandSynthesis.SearchOdd → Search → SolveEps → MatchPoly.Roots`. Measured on the
fixture (tri-band, both ends parallel-RC — the odd, weighted-Remez family): one
`MatchSolutionSearch.Search` costs **6.2 s at order 1, 16.2 s at order 2, 38.4 s at order 3** (a
single synthesis is 0.4 / 1.0 / 2.3 s and `FindQAdjust` runs fifteen of them), and the Designer's
`SearchEveryCombination` fans that out over every valid order before `WaitForAnalysis` returns. Not
introduced by MN-DCB — nothing on that stack was touched — it dates from the multiband commit. The
owner chose to drop the test (its only claim the synchronous rebuild does not already prove was the
search's family); the cost itself is unchanged and is a real wait for a user opening such a design.

### Test-time cut (2026-08-28)

`MatchOddCountTests.AMultibandLikePair_GivesFourNPlusTwoElementsWithBothEndsAbsorbed` measured
36.5 s — a tri-band like-pair synthesis, the same odd-family cost as above — and is now
`Category=Benchmark`; routine `Core.Tests` is 2 s. The Ui side, the host that never exited, and
the pre-existing tri-band order-ceiling failures are in `src/Ui/RESOLVED.md`.

