# Brief EM-SEV — a run that deleted part of the circuit must not read like an ordinary result

**What happened.** A user drew a 3.8 nH spiral in series with a 1.0838 pF MIM capacitor, ran the EM
setup, got an `.s2p`, wired it into a testbench and saw a flat open across the band. Three separate
mechanisms had removed the capacitor from the solve. circuitRF reported **all three**, correctly and
in good English, as three of **thirty-five** notes at identical weight:

```
note: 1 signal conductor layer(s) carry artwork but are NOT in this EM setup's analysis levels …
note: 'MIM Dielectric' is a patterned thin film tied to 'MIM Metal', and 'MIM Metal' is not in this run …
note: 1 via shape(s) span a conductor (MIM Metal) that is neither an analysis level nor the ground plane … ignored
```

Between and around them: the equal-area via substitution, the quasi-static γ crossover, the
Ludwig-3 reference, the core count, "annotation is not artwork". Every one of those is worth having.
**None of them changed what was solved, and the three above deleted half the user's circuit.**

The prose is not the defect. The defect is that a run's findings carry **no severity**, so the panel
and the CLI have nothing to rank by, and the reader is asked to do the ranking — on the one day they
are least able to, because they do not yet know what went wrong.

## What is being asked for

**R-emsev-1 — a run finding carries a severity, and the three classes are defined by one question:
did this change what was solved?**

| class | means | examples |
|---|---|---|
| **refusal** | nothing was written | today's `PlanarExtractionResult.No` |
| **warning** | an answer was produced and something in it is not what was drawn | a level with artwork dropped; a drawn via discarded; `cell/separation` past the validated bound |
| **note** | the run explaining itself | mesh sizing, the equal-area substitution, γ, core count |

`notes` is a `List<string>` today and `"WARNING: "` is already prefixed by hand in one place
(`BuildVias`' carried-away note) — which is the shape of the answer and also the reason it does not
work: nothing downstream can read a prefix it was not told about. Carry the class as data.
`src/Cli/CliEntry.cs`'s em printer and the GUI Messages panel then group rather than interleave, and
warnings come first.

**R-emsev-2 — a DRAWN VIA whose far terminal is not in the run is a WARNING.** This is the one with
the least ambiguity in it. A designer who drew a via stated a connection; excluding the level it
lands on severs that connection. `PlanarExtractor.BuildVias` counts it in `wrongGround` and reports
it in the same breath as a legitimate ground-pour case. On the user's file this one line is the
difference between an inductor-plus-capacitor and two disconnected pieces of metal.

`unknownLevels` and `noSpan` are the same claim and should move with it.

**R-emsev-3 — a patterned dielectric deactivated while its plate's artwork is PRESENT is a
WARNING.** The existing note is exactly right when no plate was drawn — that is MIM-7's whole
point, and an interconnect-only run must stay silent and bit-identical. It is not right when the
layout contains the plate and the analysis level list excludes it. The extractor knows both halves
(`signalBands` has the plate's band; `analysed` does not). Split the sentence on that, and keep
today's wording for today's case.

**R-emsev-4 — `cell/separation` past `ValidatedCellOverSeparation` is a WARNING, and past the ladder's
wrong-sign rung it is a REFUSAL.** `PlanarSolve.LevelSeparationNotes` already prints
`CELL/SEPARATION` in capitals, which is the author reaching for a severity the type system does not
have. §MIM-3's ladder says within 10 % to 5, 1.46× at 12.5, **wrong sign at 25**. A published `.s2p`
whose dominant element has the wrong sign is not a degraded answer, it is a fabricated one, and it
gets cached, read by a schematic and plotted. Refuse it, name the ratio, name the bound, and say
that `brief-em-mim-8-cross-level-quadrature.md` is the fix. **Do not build R-emsev-4's refusal before
MIM-8 lands unless MIM-8 is declined** — the two must not both be in flight, or the one shipped
structure this technology has becomes unrunnable with no path forward.

**R-emsev-5 — `circuitrf check <path>.cem` must run the extraction and report its warnings.** Today
it prints `1 document(s) checked: 0 error(s), 0 warning(s), 0 note(s)` on the file above. That is
the highest-value item in this brief and the cheapest: **every finding in this report is produced
before the first frequency point is solved.** The extract-and-mesh phase of the user's run completes
in a second or two; the solve is eleven minutes. `check` is already read-only, already infers kinds,
and already calls `EmSetupResolver` — R-aut4-3's rule is that it writes no validation logic of its
own, and this obeys it exactly: the findings come from `PlanarExtractor` and
`PlanarSolve.LevelSeparationNotes`, which the GUI's own Simulate calls.

Scope: extraction and mesh only, never a solve. Warnings exit non-zero at the default severity, per
`check`'s own rule.

**R-emsev-6 — the GUI needs one line, not a panel to go read.** The EM setup editor already knows
its levels and its technology. A structure whose artwork is on a level the setup excludes, or whose
drawn via lands on one, is knowable while the dialog is open. One line under the analysis-level
list — *"`MIM Metal` carries artwork in this layout and is not ticked"* — costs nothing and is the
only one of these the user would have seen before spending eleven minutes.

## Out of scope, named because it turned up in the same investigation

- **An `SnP` consumed outside its own frequency range says nothing.** The user's testbench sweeps
  1–10 GHz against a 0.5–5 GHz `.s2p` with `ExtrapMode=NearestEdge`, so the top half of every curve
  is the 5 GHz value held flat, and no note is emitted anywhere. Real, separate, and an elaboration
  concern rather than an EM one.

## Gates

- The user's `.cem` as found: `check` reports ≥ 2 warnings naming `MIM Metal`, exits non-zero,
  writes nothing, and does not solve. Time it — the gate is that it is seconds, not minutes.
- An interconnect-only run on the same technology (MIM-7's
  `AnAirbridgePost_SolvesOnTheOneTechnology_…` fixture): **zero warnings**, and the note text
  unchanged from today. The patterned-dielectric sentence for the no-plate case is byte-identical.
- The CLI's em printer groups warnings ahead of notes; the existing byte-for-byte `.sNp` identity
  gate in `tests/Ui.Tests/Em/EmCliVerbTests.cs` still passes — **severity changes reporting, never a
  number.**
- `tests/Ui.Tests/Em/MimCapacitorTests.cs` gains the excluded-plate case: the same series capacitor
  extracted with `MIM Metal` left out of `AnalysisLevelNames` produces the three warnings, and the
  two ports are shown to be electrically separate.
