# Brief DDL-4 — the other overviews and the other consumers

**Series:** DDL-0 … DDL-8 · **Tag:** `R-ddl4-<n>` · **Depends on:** DDL-1, DDL-2
**Area:** `src/Render/DataDisplay/` (`Links/`, `Models/Trace.cs`, `Models/ContourData.cs`,
`Models/DataDisplayConfig.cs`, `ContourResolve.cs`, `TraceExpression.cs`), `src/Ui/DataDisplay/TrialSelection.cs`,
the contour and expression controls on the trace card.

## The gap

DDL-1 links a pinned axis to a marker that sits on the same axis, or on one with the same unit. Four of the cases in
issue #86 need more than that:
- **The I-V overview is not on the detail's axis.** The model data holds `Y[point, freq, 2, 2]` beside `vgs[point]` and
  `vds[point]`. The I-V curves a designer clicks may be a family over (Vgs, Vds) from another cube, or a versus trace.
  The marker's coordinates are a Vgs and a Vds, not a `point`.
- **A contour's frequency is a field of its own** (`ContourData.FreqIndex`, persisted in the contour config), not a
  slice axis. Nor is an expression's (`Trace.ExpressionFreqIndex`).
- **Monte Carlo selections are already shared.** Clicking a member, point or bar selects trials across every display
  (`TrialSelection.Shared`, `data-display.md` "Trials"), but nothing can pin an axis to the selection.
- **HB drive sweeps** are a parametric sweep's axis, written into both the measurement cubes and `HB1.V`. Whether the
  two carry the same axis name and values has to be confirmed on a real run rather than assumed.

## Requirements

**R-ddl4-1 — Companion matching.** When a consumer's pinned axis (`point`) has 1-D companion cubes over it in the same
file, a link resolves through them. A companion is a cube whose only axis is that axis, named like a coordinate the
source supplies (`vgs`, `vds`, matched case-insensitively, then by unit). The index taken is the `point` whose
companion values are nearest the source's, with each dimension scaled by its companion's span. The card says which
point it took and how far it was: "point 41 (Vgs −0.40 V, Vds 6.0 V; m1 at −0.38 V, 6.1 V)". With no companion, rule 4
of DDL-1 applies. This is a nearest match by design. Interpolating between bias points is a model question, and it is
not here (DDL-0, Not here).

**R-ddl4-2 — Markers on families and versus traces.** A marker on a family curve supplies both the family axis and X
(DDL-1 R-ddl1-3 already carries both). A marker on a versus trace (`Id[point]` against `vds[point]`) supplies the
trace's kept axis (`point`) and the X operand's value with its unit. This brief adds tests for both shapes, because
they are the two ways the I-V overview is usually drawn.

**R-ddl4-3 — The contour's frequency.** `ContourData.FreqIndex` may be linked (`ContourConfig.FreqLink`, `FreqLinkName`),
with a link toggle beside the contour card's frequency selector. It resolves by DDL-1's rules against the contour's
own frequency list. A marker on a gain-against-frequency plot then sweeps a multi-frequency loadpull's contours. A
contour refit is slow by kind, so it updates on release (DDL-2 R-ddl2-3). The contour card shows the frequency it
resolved to.

**R-ddl4-4 — The expression's frequency.** `Trace.ExpressionFreqIndex` (`TraceConfig.ExpressionFreqIndex`) may be
linked in the same way, with the same toggle beside its selector.

**R-ddl4-5 — The selected trial.** `TrialSelection` becomes a link source named `selected`, one per source file. It
supplies a `trial` coordinate, the lowest selected trial in the consumer's own source. When several are selected, the
card says so ("3 trials selected; showing trial 12, the lowest"). When none are, it is frozen at the last one, and the
card says so. A marker on a scatter over trials works through DDL-1 with no change. This covers a histogram, whose
bars are not samples a marker can sit on.

**R-ddl4-6 — HB drive.** Run the shipped power amplifier example's HB sweep and confirm that the measurement cubes'
sweep axis and `HB1.V`'s carry the same name, length and values. If they do, a marker on Pout against Pin drives
`HB1.V["drain", :, @m1]` by index, and that is the gate. If they do not, DDL-1 rule 2 must still resolve them by
value: confirm the units match, and fix the writer if they do not. Record which in `src/Render/DataDisplay/RESOLVED.md`.

## Gates

1. **I-V to S-parameters.** A model-data `.npy` with `Y[point, freq, 2, 2]`, `vgs[point]`, `vds[point]`, and an I-V
   family from a gridded `Id[vgs, vds]`. A marker on the curve at Vgs −0.4 V, Vds 6 V picks the point nearest
   (−0.4, 6), and the S(2,1) detail is that point's.
2. **Versus.** The same with an `Id[point]` against `vds[point]` overview: the marker's sample is the point, exactly.
3. **Contour frequency.** A three-frequency loadpull: a marker on Gt against freq at the second frequency puts the
   contours on index 1. Moving it to the third refits on release.
4. **Selected trial.** Select trial 12 from a histogram bar: a trace pinned `@selected` on `trial` draws trial 12.
   Clearing the selection freezes it at 12, with the note.
5. **HB.** The R-ddl4-6 confirmation, and a linked `HB1.V` spectrum that steps with the marker.

## Not here

Interpolating between bias points; a linked frequency on a Touchstone (network) trace, which has no pin; several
selected trials drawn as a family (a later brief, if asked).
