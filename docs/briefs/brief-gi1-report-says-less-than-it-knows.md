# Sonnet Brief — Phase GI1: the report says less than the import already knows

**Series:** `docs/sonnet-briefs/brief-gi-series.md`. First of five; nothing depends on it, and GI2
reads better after it.
**Consumes:** L4e (the Gerber reader), L4f (Excellon + via pairing), L4g (orchestration).
**Touches:** `src/Design/Layout/Interchange/ExcellonFormat.cs`,
`src/Design/Layout/Interchange/GerberImport.cs`, `src/Design/Layout/TechModel.cs`,
`src/Design/Layout/Em/PlanarExtractor.cs`.

**Test loop:**
```
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

---

## 1. Why this is its own phase

**Three separate facts are established during an import and then either contradicted, discarded, or
undersold.** None of the three is a parsing bug — the numbers are right in every case. What is wrong is
what the import *says about* them, and in one case what it *builds* from them. That makes this the
cheapest phase in the series and the one whose absence does the most quiet damage: a reader who is told
the format was guessed when it was declared, or told a hole is a plated barrel when the file said it is
not, makes decisions on that.

Each of the three is independently small. Do all three; they are grouped because they share one gate
file and one idea.

## 2. The format sentence contradicts its own evidence

`GerberImport.cs:388` prints a drill file's format twice — once as a headline, once as a list of
evidence sentences:

```
messages.Add($"{file.FileName}: {read.Format}. {string.Join(" ", read.Format.Evidence)}");
```

`DrillFormatInference.ToString()` (`ExcellonFormat.cs:104`) renders a **two-way** switch over a
**three-way** question:

```csharp
(DecimalCoordinates ? "decimal-point coordinates" :
 ZeroOmission == GerberZeroOmission.Leading ? "leading zeros suppressed" : "trailing zeros suppressed")
```

There is no branch for `DrillFormatEvidence.CoordinateWidth` — the rung that concludes *nothing is
suppressed, because every coordinate is written at its full field width*. That inference falls through
to the `Leading` arm, so a file whose evidence sentence reads

> Zero suppression: none — every coordinate is written at its full width, so neither convention changes
> what the numbers mean.

is headlined, one sentence earlier, as **"leading zeros suppressed"**. The two halves of one message
state opposite things.

**R-gi1-1. `ToString()` must key on `ZeroOmissionEvidence`, not on `ZeroOmission` alone.** When the
evidence is `CoordinateWidth` (or any future rung that means "the question does not arise"), say so —
"full-width coordinates, nothing suppressed" — rather than naming a convention that was never chosen.
The rendered headline and the `Evidence` list are two views of one object and must never disagree.

**Gate.** For every `DrillFormatEvidence` value, `ToString()` and `Evidence` agree: build the inference
at each rung and assert that the headline does not contain the word "suppressed" whenever the evidence
sentence says nothing is. This is a pure-function test over `DrillFormatInference` and needs no files.

## 3. Plating is decided, used for the layer name, and then ignored where it matters

`GerberImport.cs:391` settles plating for each drill file:

```csharp
bool? plated = read.Plated ?? ExcellonReader.PlatingFromFileName(file.Path);
string layerName = plated == false ? "Drill (non-plated)" : "Drill";
```

…and that is the only thing it is ever used for. Two hundred lines later, the technology's via entry is
minted with plating hard-coded (`GerberImport.cs:629`):

```csharp
Fill = ViaFillKind.Plated,
```

`drills` carries the whole `ExcellonReadResult`, so the fact is in scope. **A drill file that declares
itself non-plated still produces a `Plated` via stackup entry.**

That is not cosmetic. `PlanarExtractor` builds a conductive `PlanarVia` from every `StackupKind.Via`
entry, carrying the entry's `SigmaSm`. When a folder holds a separate non-plated drill file — and a
board with mounting holes usually does — those holes become conductive barrels shorting every layer
they pass through. Millimetre-scale mounting holes near a trace do this loudly; a run containing them
completes cleanly and is wrong.

**R-gi1-2. A via stackup entry must state the plating the file stated, and a non-plated hole must not
become a conductor.** `ViaFillKind` has no spelling for "this is not a conductor at all" — `Plated` and
`Solid` are both metal — so this needs one additive, nullable field on `StackupLayer`, in the
`SheetAt`/`PresentWithLayer` mould: **null means plated**, which is bit-identical to every technology
authored before it, and `false` is what a declared-non-plated drill file writes. `PlanarExtractor` and
`CrossSectionExtractor` skip a non-plated entry when assembling vias, and the run's notes say how many
were skipped and why.

**R-gi1-3. A drill file that drilled nothing must not assert a plated through-via across the whole
stack.** A routing file — zero hits, some number of routed slots — is board outline and cutouts, not
interconnect. Today it mints a full `StackupKind.Via` entry, `Plated`, spanning topmost conductor to
bottommost, exactly like a real via file. Keep the entry (the drawing-layer marker is what makes a bare
opening re-export as a routed feature rather than as copper — `GerberImport.cs:611-614` says why), but
do not assert plating or a span from a file that produced no holes. Report it: *"this file routed N
slots and drilled no holes, so it was marked as a routed layer rather than a plated via layer."*

**Gate.** Three fixtures, all synthesised in the test:

1. A drill file declaring `;TYPE=NON_PLATED` → the technology's via entry is not plated, and
   `PlanarExtractor` produces no `PlanarVia` from it.
2. The same file with the declaration removed → unchanged from today, bit-identically (the null case).
3. A rout-only file (slots, no hits) → an entry that claims neither plating nor a span, and one message
   naming the count.

## 4. The stack order message names the evidence it did not have instead of the evidence it did

`GerberImport.cs:431-434` orders copper by `CopperIndex ?? SideRank(Side)`, then `InnerRank`, then file
name. When no job file and no `%TF.FileFunction` ranked the layers, the message is:

> Copper stack order was GUESSED for 6 of 6 copper layer(s) … because neither the job file nor
> %TF.FileFunction ranked them.

That sentence is true and it is also the weakest thing that could be said. **When every conductor file
name carries a distinct monotonic numeric prefix, the set has ranked itself**, and that is a far
stronger signal than "top-ish, inner-ish, bottom-ish, then alphabetical" — which is what the current
fallback actually is. The ordering is very often right by that weaker route and then reported as though
it were arbitrary.

**R-gi1-4. Add a numeric-prefix tiebreak, bounded so it cannot invert a known side.** Applied **only**
as the ordering *within* `SideRank` — top stays top, bottom stays bottom — and **only** when every
conductor file name yields a prefix and the prefixes are distinct. It replaces `InnerRank` +
alphabetical for the inner layers and nothing else.

**R-gi1-5. It is still a guess, and must still read as one.** A numeric prefix is a convention, not a
declaration; a set numbered in export order rather than stack order exists. So this is a new *rung*
below `%TF.FileFunction`, not a promotion out of the guessed class — the message keeps its "GUESSED"
framing and names what it used: *"ordered by the numeric prefix in the file names"*. R-L4g-10's rule
that a guess must never be indistinguishable from a declaration is unchanged.

**Gate.** A six-file conductor set with prefixes `01`…`06` and inner names that sort wrongly
alphabetically (`Layer_10` before `Layer_2`) comes out in prefix order and reports the prefix as its
evidence. The same set with one prefix removed falls back to today's ordering and today's message.

## 5. Out of scope

* Anything that reads a file the import currently skips — that is GI4 and GI5.
* Anything that adds stackup entries — that is GI2.
* Changing what `ViaFillKind` means. `Plated` vs `Solid` stays a fill model; R-gi1-2 adds a separate
  question (is this metal at all) rather than overloading that one.

## 6. On completion

Write **"Phase GI1 — COMPLETE"** at the top of `src/Design/RESOLVED.md` — **not** `CLAUDE.md`. Record:

1. **Whether any other headline/evidence pair in the interchange readers has the same shape as §2** —
   a `ToString()` that renders fewer cases than the object carries. If there are others, name them;
   this one was found by reading a real import log, not by a test.
2. **What `PlanarExtractor` did with a non-plated via before and after**, as a count on a real
   multi-layer set: how many barrels stopped being conductors.
3. **How often the numeric prefix was available** across the fixtures on hand, and whether it ever
   disagreed with the side/inner ordering it replaced. If it never disagreed, say so — that is the
   number that decides whether §4 was worth doing.
