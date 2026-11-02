# Sonnet Brief — Phase GI3: the substrate fields someone has to fill in

**Series:** `docs/sonnet-briefs/brief-gi-series.md`. Third of five. Reads best after GI2, which is what
creates the rows this phase makes fillable, but does not depend on it.
**Touches:** `src/Ui/Views/Layout/TechEditorView.axaml`,
`src/Ui/Layout/StackupLayerRowViewModel.cs`, `src/Ui/Layout/TechEditorViewModel.cs`,
`src/Design/Layout/TechValidation.cs`, `src/Design/Layout/Interchange/GerberImport.cs`.

**Test loop:**
```
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

---

## 1. Why this is its own phase

**Every other field on the Stackup tab's detail panel carries guidance; the two that a board import
leaves blank do not.** "EM sheet at", "Patterned with", "Ground reference" and the via span all have
tooltips explaining what they mean and what to choose. `σ (S/m)` (`TechEditorView.axaml:545`) and the
plated via's `Wall` (`TechEditorView.axaml:631`) are bare `TextBox`es with no tooltip, no preset and —
after a Gerber import — no value.

Those are exactly the two fields someone importing a board has to fill in and has no way to check. The
question "which wall thickness should I choose?" has a good answer, it is already in this repository,
and nothing in the application says it.

Three smaller things ride along because they are the same tab and the same session's work: there is no
way to see whether a stackup adds up, duplicate entry names silently break name-based references, and
via rows sit inside a list whose order means z.

## 2. Conductor conductivity

**R-gi3-1. A material preset writes into the existing conductivity field.** A combo box beside `σ`
offering a short list of metals; choosing one sets `StagedSigmaSm` and commits through the existing
`CommitSigmaSm`. A value that matches no preset displays as "Custom" and is still freely typeable — the
preset is a shortcut, never a constraint, and the field remains the source of truth.

**R-gi3-2. Elements only, and single-sourced.** Copper 5.80e7, silver 6.30e7, gold 4.10e7, aluminium
3.77e7, nickel 1.43e7 S/m at 20 °C. These are element conductivities out of any physics handbook, not
product data — which is why this list is allowed to exist at all where the laminate equivalent is not
(root `CLAUDE.md` §"Commercial Vendor References"; `GerberStackupMapping`'s own header refuses the
permittivity table for the same reason, and that refusal stands).

Two of these numbers are already hard-coded in the tree — `PcbStackupMapping.DefaultCopperConductivitySm`
is 5.8e7, and `StarterTechnologies` repeats 5.8e7 and 4.1e7 across five sites. **Put the table in one
place in `src/Design` and have every one of those read it.** A sixth copy is the failure mode this
requirement exists to prevent.

**R-gi3-3. `σ` gets a tooltip.** What it is for, in one sentence: conductor loss and skin depth. Say
that a conductor with non-positive conductivity is refused by the EM path, since that is the error
someone meets if they leave it blank, and the validator already reports it
(`TechValidation.cs:71-73`).

## 3. Via wall thickness

**R-gi3-4. The default already exists; the import just does not use it.** Every shipped PCB technology
writes `WallThicknessDbu: 25000` — 25 µm — and `StarterTechnologies.cs:112` writes `Um(25)`. A Gerber
import mints its via entries at `GerberImport.cs:624-631` with no wall thickness at all, so the one
document in the product that is *guaranteed* to need this field is the one document that ships without
it, while five documents that already have a stackup all agree on the answer.

Default an imported plated via to the same 25 µm, through the same constant, and **name it as a
default** in the import message — the R-L4d-7 pattern the conductivity default already follows.

**R-gi3-5. The field gets a tooltip that answers the question actually being asked.** The relevant
facts, and they are already written down in `TechModel.cs`'s own documentation of `ViaFillKind`:

* it is a **plating** thickness — the metal on the barrel wall, not the hole radius;
* 20–25 µm is typical for a plated through-hole board;
* **for RF above roughly 1 GHz it barely matters** — a wall a few µm thick is already many skin depths,
  so a solver may treat plated and solid identically;
* **for thermal it is a direct multiplier** on conductive cross-section, which is the whole reason
  thermal via arrays are sized on it.

Someone who reads that stops worrying about getting it exactly right for an S-parameter run, which is
the useful outcome.

## 4. Does the stackup add up?

**R-gi3-6. Show the total stack height on the Stackup tab.** The sum of `ThicknessDbu` over Conductor
and Dielectric entries, in the editor's current display unit, live as rows are edited. Via entries are
excluded — a via has no z band of its own, which `TechValidation.cs:79-83` already relies on.

This is the cheapest possible check on a hand-entered stackup and it catches the common error directly:
a stack whose parts were transcribed one at a time and never added up does not match the board it came
from, and nothing in the application currently says so at any point before a run gives a wrong answer.

**R-gi3-7. When a board thickness is known, show both and flag disagreement.** A job file states
`BoardThickness` (`GerberJobFile.cs:163`) and it is currently read, reported once in the no-stackup
message, and then dropped. Carry it onto the `Stackup` as an additive nullable field, display it beside
the computed total, and mark a disagreement beyond a stated tolerance. Do not auto-correct anything:
the two numbers disagreeing is information, and which one is wrong is not something the application
knows.

## 5. Two structural gaps on the same tab

**R-gi3-8. Duplicate stackup entry names must be reported.** `SpanFromLayer`, `SpanToLayer` and
`PresentWithLayer` all resolve a stackup entry **by name**, and `TechValidation` builds `conductorNames`
as a `HashSet<string>` — so two conductors sharing a name collapse to one and every reference to that
name becomes ambiguous with no diagnostic at all. Hand-authored stackups grow duplicate names easily,
because the natural names for the layers between six copper sheets repeat.

Report once per duplicated name, naming every entry that carries it — the one-problem-per-cause rule
`TechValidation.cs:26-32` already states. Duplicates among dielectrics are only latent today (nothing
references a dielectric by name yet) but are reported on the same terms: the cost of allowing them is
paid later, by whoever adds the next name-based reference.

**R-gi3-9. Via rows are outside the z order and must not read as inside it.** `Stackup.Layers` is
documented "Ordered TOP to BOTTOM" and the editor binds it directly (`TechEditorViewModel.cs:559`), so
a Via entry — which has no position in that order — renders as a row in the middle of the stack, and a
via added before the conductors renders above the top copper. Present via entries as their own group,
below the ordered entries, or otherwise visibly outside the order.

**The underlying list order for Conductor and Dielectric entries must not change.** It *is* z order, and
a reversed stack simulates cleanly and answers a different question — L4d's R-L4d-5, restated by
`GerberStackupMapping`'s R-L4g-10.

**R-gi3-10. Moving a via row up or down is meaningless and should not be offered.**
`MoveStackupLayer` (`TechEditorViewModel.cs:591-599`) swaps adjacent list entries unconditionally, so it
will happily swap a via with a conductor and change nothing but the reading order — while, under
R-gi3-9's grouping, appearing to do nothing at all. Disable the control on a via row.

## 6. Gates

1. **Preset writes through** — choosing each metal sets the conductivity the table states; a typed
   custom value survives and displays as Custom; the round trip through `.ctech` is exact.
2. **One table** — a source scan (comment-stripped, per the established pattern) proving no second copy
   of the conductivity constants remains in `StarterTechnologies` or the interchange mappings.
3. **Import default** — a Gerber import's via entry carries 25 µm and one message names it as a
   default; the value equals the shipped technologies' own, read from the same constant.
4. **Tooltips exist** — assert both fields have non-empty `ToolTip.Tip`. Thin, but this phase exists
   because they did not, and the next XAML edit can silently drop one.
5. **Total** — a stackup of known entries reports the expected sum; adding a via does not change it;
   editing a thickness updates it live.
6. **Board thickness** — a job-file import carries the stated thickness onto the stackup, the editor
   shows both numbers, and a disagreement is flagged and **not** corrected.
7. **Duplicate names** — two conductors sharing a name yield exactly one problem naming both entries;
   a via spanning that ambiguous name is not additionally reported (one cause, one message).
8. **Via rows grouped** — the presented order puts vias outside the z sequence while
   `Stackup.Layers`'s own Conductor/Dielectric order is byte-identical before and after; move is
   disabled on a via row.
9. **No timing assertions anywhere.**

## 7. Out of scope

* A laminate/dielectric material table. Refused, permanently, for the reason `GerberStackupMapping`'s
  header gives. εᵣ and tanδ stay typed in by whoever knows the board.
* Reordering `Stackup.Layers` in the model. R-gi3-9 is a presentation change only.
* Anything that reads a file the import currently skips — GI4 and GI5.

## 8. On completion

Write **"Phase GI3 — COMPLETE"** at the top of `src/Design/RESOLVED.md` — **not** `CLAUDE.md`. Record:

1. **Every site the conductivity constants were consolidated out of**, by name — so the next person
   adding a starter technology knows where the table is.
2. **Whether the total-height readout caught anything on the stackups already in the tree**, shipped or
   fixture. If a shipped technology does not sum to its own stated thickness, that is a finding and it
   belongs here rather than being quietly adjusted.
3. **What duplicate names turned out to exist** across the fixtures and shipped technologies, and
   whether any of them were load-bearing.
