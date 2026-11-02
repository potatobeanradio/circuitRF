# Sonnet Brief — Phase GI5: the netlist companion

**Series:** `docs/sonnet-briefs/brief-gi-series.md`. Fifth of five, and the largest. Depends on GI1's
plating field and GI4's fourth file kind.
**Touches:** `src/Design/Layout/Interchange/GerberFileClassifier.cs`,
`src/Design/Layout/Interchange/DrillViaPairing.cs`,
`src/Design/Layout/Interchange/GerberImport.cs`, `src/Design/Layout/LayoutModel.cs`, plus one new
reader file.

**Write from public documentation only** — §8's standing rule against ingesting GPL sources applies to
this format exactly as it does to Gerber and Excellon.

**Test loop:**
```
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

---

## 1. Three apologies this phase retires

A Gerber import of a real multi-layer board currently prints all three of these, in the same run:

> …hole(s) were reconstructed as vias **without the file saying so**: with no ViaDrill/ComponentDrill
> attribute and no ViaPad flash, a via and a plated component hole are indistinguishable from artwork
> alone… **The distinction was not available.**

> This layer paints N clear object(s), which `LayoutShape` cannot represent directly, so the whole layer
> was composited through Clipper in paint order. Its individual shape identities and **its per-object
> net names are gone**; the geometry is exact.

> **No layer span was declared**, so these holes are assumed to go through the whole board. A set with
> blind or buried vias declares a span per drill file.

Every one of those sentences is honest about the *artwork*. None of them is true about the **folder**.
A production output set routinely ships a board netlist alongside its artwork — an **IPC-D-356 /
IPC-D-356A** file: plain text, column-oriented, publicly documented, and a standards-body format in the
same class as Gerber and Excellon rather than any one tool's private one. It carries one record per pad
and per hole:

* the **net name** at that location;
* whether the hole is **plated**;
* the **component reference and pin**, when there is one — whose *absence* is what identifies a via;
* the **layers the feature reaches** — which is the layer span.

That file is currently classified `Other` and skipped. The information the import says is "not
available" is in the same folder.

## 2. The governing rule

**R-gi5-1. The netlist is EVIDENCE about the artwork. It is never geometry.** No shape is created from
it, no shape is moved by it, no shape is deleted because of it. The Gerber and drill readers remain the
sole source of every coordinate, every diameter and every outline. This phase attaches *facts* to
objects the existing readers already built, and reports anything it cannot attach.

That boundary is what keeps this phase safe to add to a mature importer, and it is the first thing to
check in review.

**R-gi5-2. Everything it contributes is additive and optional.** A set without a netlist imports
**bit-identically** to today — same `.clay`, same `.ctech`, same message list minus nothing. This is a
gate, not an aspiration.

## 3. What it settles

**R-gi5-3. Via versus plated component hole.** This is the distinction `DrillViaPairing.cs:301-304`
declares unavailable, and the netlist makes it directly: a record carrying a component reference and a
pin is a component hole; a record carrying a net and no component reference is a via. Report the split
as counts, and report how many holes the netlist did not cover.

**R-gi5-4. Plating.** Where a drill file declares nothing, the netlist's per-record plating flag
settles it. GI1's plating field on the via stackup entry is where it lands, and GI1's rule follows: a
non-plated hole does not become a conductor in the EM extraction. A board's mounting holes are the case
that matters — millimetre-scale barrels shorting every layer they pass through produce a run that
completes cleanly and is wrong.

When GI4's tool listing and the netlist both speak, they must agree; if they do not, report it and
prefer neither silently.

**R-gi5-5. Layer span.** A record states which layers the feature reaches. That is exactly what
`ViaShape` and the via stackup entry need and what the drill files did not say. A through hole and a
buried hole are then genuinely distinguishable, rather than every hole being assumed through.

**Do not synthesise stackup entries per distinct span in this phase.** Read the spans, report the
distinct ones found, and apply them where a via stackup entry already exists. Blind/buried support as a
first-class stackup concept is its own piece of work and must not arrive as a side effect of a netlist
reader.

**R-gi5-6. Net names — including on layers whose net names were destroyed.** This is the one that gives
something back that is otherwise permanently lost. `LayoutShape.Net` exists and is documented
"Unpopulated until L5"; a clear-polarity layer composited through Clipper has had its per-object
identities unioned away, and no amount of re-reading the artwork recovers them. A netlist coordinate,
however, still falls *inside* the composited region — so the union that destroyed the pad's identity
did not destroy its **location**, and a net name can be attached to the region that contains it.

## 4. Matching, and its failure cases

**R-gi5-7. Match by containment first, nearest-within-tolerance second, and state the tolerance.** A
coordinate inside exactly one shape on a layer names that shape. A coordinate inside none takes the
nearest shape within a stated tolerance. Both the tolerance and the counts at each rung are reported.

**R-gi5-8. A coordinate that matches nothing is reported, never dropped.** And a coordinate matching
*several* shapes on one layer is reported too — after compositing, a pour is one enormous region, so a
whole net's worth of records can land in one shape. That is not an error; it is the expected outcome on
a ground pour and the report should say so rather than reading as a fault.

**R-gi5-9. One region, many nets, is a real state and must not be resolved by picking one.** A
composited pour genuinely contains pads of more than one net. `LayoutShape.Net` is a single nullable
string, so a region holding several nets cannot honestly carry one of them. Leave it null, count it,
and say so: *"N composited region(s) contain pads of more than one net and were left unnamed."*
Writing whichever name arrived first would be a fabrication that nothing downstream would question.

**R-gi5-10. The netlist has its own units and coordinate format, and getting that wrong is the usual
catastrophe.** Read them from the file, apply L4f's own doctrine — state the inference and its evidence,
never guess silently — and **cross-check against the artwork's extents** exactly as
`ExcellonReader.CrossCheckExtents` already does for drill data. A netlist read at the wrong scale
matches nothing, which is at least loud; read at a wrong scale that still lands inside the board it
mislabels everything, which is not. The cross-check is what separates the two.

**R-gi5-11. Disagreement with the artwork is reported, and neither side silently wins.** A netlist
naming a pad the artwork does not have, or a hole the drill file does not have, is information about
the set — usually that one file is from a different revision. Say so with counts. Do not repair
anything.

## 5. Reporting

**R-gi5-12. Replace the apologies rather than adding to them.** Where the netlist settled a question,
the message says what settled it — not the old sentence plus a new one. Where it did not, the old
sentence stands unchanged. A run that reads a netlist and still cannot tell a via from a component hole
must say *that*, specifically.

**R-gi5-13. One summary line with the counts that matter**: records read, pads matched, holes
classified as via / component / unclassified, nets attached, regions left unnamed for ambiguity,
coordinates matched to nothing, spans found.

## 6. Gates

1. **Absent is bit-identical** (R-gi5-2) — the same set with and without the netlist produces the same
   `.clay` and `.ctech` bytes, with the netlist's own messages the only difference. Compare bytes.
2. **Never geometry** (R-gi5-1) — a netlist naming pads the artwork does not have creates no shape;
   assert the shape count is unchanged and the discrepancy is reported.
3. **Via versus component hole** (R-gi5-3) — a fixture with both yields the right split, and the
   "distinction was not available" message is **gone** from that run.
4. **Non-plated does not conduct** (R-gi5-4) — a netlist marking holes non-plated yields no
   `PlanarVia` for them; the counterpart with the marking removed does. The pairing is the assertion.
5. **Span** (R-gi5-5) — a fixture with a buried span reports it and does not report every hole as
   through-hole; no stackup entry is synthesised.
6. **Nets on a composited layer** (R-gi5-6) — a clear-polarity layer that composites to one region, plus
   a netlist, attaches net names to the regions that contain the coordinates. This is the phase's
   headline capability and needs its own fixture.
7. **Ambiguity stays null** (R-gi5-9) — a region containing two nets carries no net name and is
   counted. A test asserting it picked one would be asserting the bug.
8. **Scale cross-check** (R-gi5-10) — a netlist at a wrong scale is caught by the extent comparison and
   refused or reported, not silently matched.
9. **Tolerance is stated** — the near-match rung reports its own tolerance and the count matched by it.
10. **Round trip** — net names survive `.clay` save/load, and a re-export is unaffected by them.
11. **Firewall** — the reader lands in `src/Design`, references no UI framework, and
    `tests/Firewall.Tests` proves it.
12. **No timing assertions.**

## 7. Out of scope

* Building a connectivity model or running net extraction from this data. `NetExtractor` derives nets
  from geometry; this phase attaches names, and the two must not be conflated.
* Blind/buried vias as a first-class stackup concept (R-gi5-5).
* Component placement, footprints or hierarchy. `LayoutShape.Component` already carries a declared
  reference and `GerberImport` already declines to build hierarchy from it; that decision stands.
* Any netlist dialect other than IPC-D-356/356A. A second dialect is a second reader and its own
  decision.

## 8. On completion

Write **"Phase GI5 — COMPLETE"** at the top of `src/Design/RESOLVED.md` — **not** `CLAUDE.md`. Record:

1. **The match rate on every real set available** — records read against pads matched, split by rung
   (containment, near-match, unmatched). This is the number that says whether the reader is useful or
   merely present, and an impression is not a substitute for it.
2. **How many composited regions came back with a usable net name**, and how many were left null for
   ambiguity. If most large pours are ambiguous, say so — it bounds what §3's headline capability is
   actually worth and the next person should not have to rediscover it.
3. **Whether the via/component split agreed with the pairing heuristic** it replaces, and where it did
   not.
4. **Every disagreement between the netlist and the artwork**, by kind. Revision skew between files in
   one output set is the interesting finding here and it is invisible without this reader.
5. **What the format turned out not to carry** that this phase assumed it would.
