# Brief 38 — a via joins every layer it passes, in the readings as in the walk

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail38-n` · **Phase:** investigation, then a model change (owner decides §4 before any code lands)
**Area:** `src/Design/Layout/Pdn/PdnAssembly.cs` (`StampVias`), `src/Design/Layout/Pdn/PdnGraphExtractor.cs`
(`Connect`, `AttachmentPoints`, `PourDominatedRefusal`), `src/Design/Layout/Drc/DrcConnectivity.cs` (the walk's
barrel rule, read-only here), `src/Design/Layout/Pdn/PdnMountingLoop.cs`
**Depends on:** 37 (its anchor rule, chain seeding and `PdnBarrels` are what get the field rail this far) · **Blocks:** nothing
**Evidence:** the round-9 field-report workspace, held by the owner. Brief 37's scratch harness (the field board,
with `--series`, `--netlist`, `--accurate`, `--path`, `--debug`) is the fastest way back in.
**Rule:** the board, its customer and the reporter must not be named anywhere in the repo.

---

## 0. The problem in plain terms

A via is a plated hole. Its copper barrel touches every layer it passes through, and wherever a layer
has copper right up to the hole (no clearance ring), that layer is connected to the others through it.

railRF has two views of the same board, and they disagree about this:

- **The region walk** (`DrcConnectivity`, which decides "which copper is this rail") joins EVERY
  conductor a barrel passes. That was deliberate: its own comment records a four-layer board whose inner
  power plane came back as a separate island until it did.
- **The two readings that price the copper** (Fast and Accurate both stamp vias through
  `PdnAssembly.StampVias`) put ONE resistor per via, between the TOP and BOTTOM of its span, and stamp
  **nothing at all** where either end layer has no rail copper at that point.

So the walk says the rail is connected, and the readings build a netlist in which it is not. On the
field-report rail the current goes Top → via → Inner 2 → via → Top. Neither via has rail copper on the
bottom layer, so neither is stamped, and the load ends up on copper that the netlist does not connect
to the source. Accurate then refuses ("rail copper that nothing in the Accurate reading's mesh joins to a
source at DC"). Fast refuses earlier for a different reason: the path crosses a 71 mm² spreading piece
on Inner 2, and Fast is designed to refuse that. Fast has the same via gap, and would hit it next.

**Why this matters beyond one board.** The refusal is the good outcome. The bad one is a board that
*also* has another, longer route: it solves, the answer is plausible, and it is wrong, because the short
path through the inner layer was left out. Nothing on the report would say so. Every multi-layer board
whose supply changes layer through an inner plane or inner trace is exposed.

**What brief 37 measured.** A scratch patch that stamped one barrel segment between each consecutive
pair of conductors the via passes, wherever each has rail copper, solved the field rail in Accurate:
2.9 mV at the load, 50 mA, both series parts at 0 Ω. It was reverted unreviewed because it changes
numbers on other boards. This brief is the review.

## 1. `R-rail38-1` — measure before changing anything

Produce a short report, with numbers, of:

1. **The field rail.** Confirm with brief 37's `--path` probe that the walk's route is through barrels
   whose span ends have no rail copper, and list each via on the path with the conductors it touches.
2. **How common it is.** On the field board, and on every fixture and shipped example with vias
   (`PdnViaCheckTests`, `PdnDistributedTests`, `PdnMeshExtractorTests`, `PdnFastExtractorTests`,
   `PdnRefusalCauseTests`, the Power Rail example): how many vias today are (a) stamped end to end,
   (b) skipped because an end has no rail copper, (c) stamped but also touching rail copper on an
   intermediate layer. Counters, not timers.
3. **What changes.** For each board where (b) or (c) is non-zero: the drop at each port and the ranked
   breakdown, before and after the scratch rule of §2. Build HEAD in a scratch `git worktree` for the
   "before" side (brief 37 did this; never `git stash`).
4. **Fast's pricing.** With the via rule changed, does Fast still refuse the field rail as
   pour-dominated? If so, what does a class override on the 71 mm² piece give, and how far is that from
   Accurate?

Stop after §1 and hand the report to the owner. §2 is written so the choice is quick, not so it can be
skipped.

## 2. `R-rail38-2` — the rule (after owner approval)

The proposed rule is the walk's own. For each barrel, take the conductors between its span ends in
stackup order. On each, find the rail node at the via's point, **on that layer only**. Stamp one
resistor between each consecutive pair of layers that have one, with a resistance from the barrel's
length **between those two layers** (not the full span). Layers with no rail copper at the point are
passed through: the barrel still carries current past them. Questions the report must answer, not
assume:

- **Segment length.** Measure from the far face of one conductor to the near face of the next, or
  centre to centre? State the choice and show the per-segment values add up to the full-span barrel
  (to 1e-12) when every layer has copper.
- **Clearance rings.** Copper with a clearance ring round the hole does not touch the barrel. The walk
  decides this from geometry (`FirstTouching`); the reading must take the SAME answer from the walk
  rather than re-derive it. Otherwise the two can disagree again from the other side.
- **The reference layer.** A barrel touching the reference plane on its way through is a return via,
  not a rail via. The existing refusals (`MixedReturnRefusal`, the return-routed check) must still fire
  where they fire today. Show one fixture where they do.
- **The current check.** Brief 6's per-barrel current limit reads `PdnViaBarrel`. A barrel now split into
  segments must still be ONE barrel in that check, carrying the largest segment current, not several
  smaller ones that each pass.
- **The mounting loop** (`PdnMountingLoop.cs:182`) still reads `ViaShape` only. Decide whether it should
  read `PdnBarrels.Of` too, and say why either way.

## 3. `R-rail38-3` — the silent case

Whatever the owner decides in §4, add the check that turns "plausible and wrong" into a sentence. When
the walk joins two pieces of rail copper only through a barrel the reading did not stamp, say so in the
run's notes: which via, which layers. This is worth having even with §2 in place, because a
blind or buried span the reading cannot resolve (`UnresolvedViaSpans`) has the same shape.

## 4. Owner decisions

1. Adopt §2's rule, knowing it changes numbers on the boards §1 lists, or keep today's behaviour with
   §3's note only.
2. If §2 is adopted: which segment length (§2 first bullet).

## 5. Gates

1. The field-report rail solves in Accurate with source and load on Top, the series parts declared,
   and no layer typed. Its drop is reported with the barrels on the path named in the breakdown.
2. Every board §1 found with no (b)/(c) vias gives a byte-identical `rail --json`, Fast and Accurate.
   Every board that changes appears in the report with its before and after, and the owner has seen it.
3. The existing via current-check and return-refusal tests pass unchanged, or the report explains each
   change.
4. §3's note appears on a fixture where the walk joins through a barrel the reading cannot stamp (for
   example an unresolvable span), and nowhere else.

## 6. Tests (minimal)

- A three-conductor fixture: rail copper on Top and the inner layer, none on Bottom, joined only by a
  through via. It solves, and the barrel is a breakdown row.
- The same via with every layer carrying rail copper: the segments sum to today's full-span barrel.
- A clearance ring round the via on the inner layer: not joined, exactly as the walk says.
- §3's note on an unresolvable span.

Use counters, never timers. Run with `--filter` on the classes touched plus `PdnFastExtractorTests`,
`PdnRefusalCauseTests`, `PdnViaCheckTests`. Never run the whole of `Ui.Tests`.

## 7. On completion

Record findings in `src/Design/RESOLVED.md`, never in any `CLAUDE.md`. Update the railRF user doc in its
source (`docs/user/src/reference/railrf.md`) where it describes vias and the drop breakdown. Don't run
DocGen; the owner regenerates at the end of the series.
