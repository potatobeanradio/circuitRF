# Sonnet Brief — RP-1: an explicit return plane for a run

**Scope: one new `.cem` field that names the conductor R-em-4 would otherwise infer, and the refusals
that keep it honest.** No kernel change, no new port type, no per-port anything — RP-2 is where that
lives, and the two briefs must not be merged. This one is small on purpose.

Read `src/Design/Layout/Em/PlanarExtractor.cs`' §"R-em-4" block first; that is the whole subject.

---

## 1. Why

R-em-4 resolves the return plane as **the top surface of the highest ground-designated conductor
below the lowest analysis level**, and that is the correct rule — a port returns through a plane
beneath the conductor it feeds, and the layered Green's function terminates on exactly one PEC. It is
also the *only* way to state the answer today, and it is stated in the wrong vocabulary for two real
situations:

1. **Two designated planes below the structure.** A 6-layer board with a plane on L2 and another on
   L5, and a trace on L1 that is genuinely referenced to L5 (an intentionally-voided L2). R-em-4
   picks L2 because it is highest, and the only way to say otherwise is to un-tick L2's "Ground
   reference" in the *technology* — which changes it for every other design that uses that
   technology, and makes it a meshed signal conductor everywhere.
2. **Answering "what if".** Comparing the same structure against two different reference planes is a
   routine sanity check and today it requires two technologies.

The remedy the notes currently offer — "designate that one as the ground reference in the technology
editor" — is correct for the common case and is the wrong granularity for both of these: the
technology is shared, and the question is per-run.

**A 2026-09-10 user report is the immediate motive** and is *not* fixed by this brief: on a
Gerber-imported 4-layer board, inner-layer via pads made an inner layer the lowest analysis level and
dragged the return plane to the bottom of the board. That was a level-selection defect and was fixed
where it belonged, in the incidental-level trim. **Do not re-fix it here.** It is named because the
temptation to make this field the remedy for it will be strong, and a per-run override that papers
over a bad level set is worse than no override at all — the levels would still be wrong.

---

## 2. The field

`EmSetupModel` gains one nullable string, beside `SignalStackupLayerName`:

```csharp
public string GroundStackupLayerName { get; set; } = "";
```

Persisted by `EmSetupPersistence` on the same terms as `SignalStackupLayerName`: **omitted from the
file entirely when empty, no `FormatVersion` bump**, and an older `.cem` reads back identical. It
reaches `PlanarExtractor` through `EmExtractionSettings`, which is where the extractor's existing
`SignalStackupLayerName` / `AnalysisLevelNames` already arrive.

**R-rp1-1. Empty means R-em-4, not "no ground".** Every existing document and every fixture must take
the inferred path byte for byte. This is the same discipline `LabelShape.PortDirection`'s null
already follows and for the same reason.

---

## 3. What it does, and the four refusals

In `PlanarExtractor.Extract`, the override is consulted where `HighestGroundBelow` is called today.
The refusals are the substance of this brief — an override that silently produces a different-looking
answer is the failure mode the whole area is written against.

**R-rp1-2. Named conductor must exist.** No conductor stackup entry by that name → refuse, naming the
technology, exactly as `SignalStackupLayerName`'s own refusal does.

**R-rp1-3. It must be BELOW the lowest analysis level.** A conductor at or above it cannot be a
return path — the slab would have zero or negative height. Refuse by name, and **say which level and
which heights**, because the user is looking at a stackup table and cannot see the analysis levels.
This is R-em-4's own physics, not a limitation of the override.

**R-rp1-4. It need NOT be ground-designated, and that is the point — but say so.** A conductor the
technology does not mark as a ground reference is accepted and used as the terminating PEC, with a
note stating that the technology does not designate it and that this run overrode that. Without the
note the `.ctech` and the run disagree with nothing on screen to say which won. **A conductor that is
also an analysis level is a refusal**, not a note: it cannot be both the meshed metal and the
laterally-infinite plane the meshed metal returns to.

**R-rp1-5. The existing "Every port returns through …" note must say the answer was overridden**, and
must keep naming the height. That note is the only place the return plane is visible; a run whose
plane came from the `.cem` rather than from R-em-4 must read differently from one that did not.

**R-rp1-6. The skipped-plane WARNING still applies and is not suppressed.** A designated plane between
the levels and the chosen return is absorbed into the surrounding dielectric — its metal is modelled
as substrate — and that is exactly as true when the user chose the return as when R-em-4 did. If
anything the warning matters more here, because the override is how someone reaches that state on
purpose.

---

## 4. Where it is exposed

**R-rp1-7. The `.cem` panel gets one combobox**, in the same block as the analysis levels, listing the
conductor stackup entries with an "(automatic)" first row that writes the empty string. Ground-
designated entries are marked in the list; nothing is filtered out of it, because R-rp1-4 permits a
non-designated conductor and a list that hides the legal choices is a list that teaches the wrong
rule.

**R-rp1-8. `circuitrf explain` reports it.** `explain` on a `.cem` already reports the layout and
technology walks; it must report the return plane, its height, and whether it came from R-em-4 or
from the field. That is the headless half of R-rp1-5 and costs one line.

**R-rp1-9. No CLI flag.** `em` takes a `.cem` and needs no other arguments (repo-root `CLAUDE.md`),
and every other override in that verb replaces the directive in the document. Adding `--ground` would
be a second way to say a thing the document already says.

---

## 5. Gates

1. **Empty field is bit-identical.** Run an existing multi-level fixture with the field absent and
   with it set to the empty string; the `PlanarProblem` must compare equal — medium stack, slab,
   level z's. This is the gate that matters most.
2. **The override actually changes the medium**, and by the right amount: on the shipped 4-layer
   starter, a trace on Top Copper with the field set to Bottom Copper produces a slab of the full
   board thickness rather than the prepreg, and the `LayerStack` carries the intervening layers.
   Assert the slab height as a NUMBER; a note assertion alone passes with the medium built wrong.
3. **Each of R-rp1-2, R-rp1-3 and R-rp1-4's level-collision case refuses**, and each refusal names the
   conductor and the remedy. Assert the sentence, not just `Ok == false`.
4. **A non-designated conductor is accepted with the R-rp1-4 note**, and the resulting medium
   terminates on it.
5. **R-rp1-6**: a fixture with a designated plane between the levels and an overridden return still
   emits the skipped-plane warning.
6. **`explain` agrees with the run**: for the same `.cem`, the plane `explain` reports is the one the
   extraction used. Assert against the extraction, not against a transcription of the rule.
7. **Round trip**: a `.cem` with the field set, written and re-read, is unchanged; one without it
   gains no key.

---

## 6. On completion

Write the findings to `src/Design/RESOLVED.md` — **never to a `CLAUDE.md`**. Update
`docs/design/cli.md`'s `explain` section if R-rp1-8 changed its output.

**Report, do not silently absorb:**
- Whether any existing fixture's `PlanarProblem` moved at all under gate 1. It must not.
- Any place R-em-4's rule turned out to be spelled a second time. `HighestGroundBelow` was factored
  out on 2026-09-10 to make this brief a one-site change; if that turns out to be false, the second
  site is the finding.
