# Sonnet Brief — MN-DCB2: the DC block follows the DC PATH, not the end node

**Design:** `docs/design/match.md` §22 (the block; §22.1 and §22.5 are corrected by this brief), §4.4
(absorption — why the end capacitor may not be a real part), §4.7 (Norton π and T), §11 (Flatten,
which already emits absorbed elements as disabled instances). **Prerequisite:** MN-DCB is landed
(`brief-match-dc-block.md`; `MatchDcBlock`, the `Block` toggle, the status line, the tests).

**One sentence:** the block is offered on the **first real shunt inductor reachable from a termination
through real inductors only** — at the end node, or one series inductor in, at either end, after a π
or a T — and it is withheld only when a **real** series capacitor already isolates that end, or when
no shunt inductor exists on the path at all.

**Why (owner, 2026-08-28).** MN-DCB's rule was "enabled only when this end's arm is a shunt inductor",
justified by *"a series arm's capacitor already blocks DC"*. That premise is wrong in two ways the
owner hit immediately:

1. **A series RC termination is a FET input.** The end arm is a series arm whose capacitor is
   *absorbed* — it is the device's own C_gs, inside the package, and `MatchFlattenPlan` rightly emits
   it as a disabled instance. Nothing on the board blocks DC there. The physical path from the gate
   terminal is the arm's series **inductor** to the first internal node, where the ladder's first
   shunt inductor shorts the gate bias to ground. The toggle was disabled for exactly the end that
   needs it most.
2. **A Norton T on the end pair makes a series arm with no capacitor at all.** The T of two inductors
   is series-L / shunt-L / series-L, so after "pi → T" the end arm is a series inductor, the toggle
   went grey, and the T's shunt product behind it still shorts the termination.

Both are one fact: **DC does not stop at an arm boundary, it stops at a real series capacitor.**
Whether the end node's arm is series or shunt is irrelevant; what matters is what a DC current
starting at the termination meets first — a real shunt inductor (needs the block), a real series
capacitor (already isolated), or nothing (lowpass).

**Structural facts.**

1. **Absorbed elements are not on the board.** `MatchElement.IsAbsorbed` (`AbsorbedEnd != 0`) marks
   the termination's own reactance, which §11.3 flattens as a *disabled* instance. For DC purposes
   the walk treats them as transparent — the ladder-side node of an absorbed series element IS the
   device terminal. An excess element (`IsExcess`, `CFano`/`LFano`) or a detune element (`IsDetune`,
   `CDetune`/`LDetune`) is **ours and real**: a series `CFano` is a real capacitor and isolates; a
   shunt `LFano` is a real inductor and is a candidate host.
2. **A series inductor passes DC; a shunt capacitor is invisible to it.** The walk crosses the first
   and ignores the second.
3. **The compensation does not care where the host is.** `L' = L + 1/(ω₀² C)` keeps the *branch's*
   reactance at ω₀; a branch one series inductor in from the port is compensated identically. Nothing
   in §22.2, `Compensate`, `SeriesResonanceHz`, `BandSpread` or the default changes. The through-path
   inductor between the termination and the host is simply the bias feed's route, and the status line
   says so.
4. **Still a post-rebuild step; still by node, never by name.** `MatchRebuild.Rebuild` keeps applying
   it after `WithEndSplits`; the synthesis, the transforms, the solution search and both fingerprints
   still never see it. Only the *resolution* changes.
5. **Withholding the block behind a real series capacitor is deliberate, and must be said.** With a
   real `Cx` between the termination and the first shunt inductor, a block on that inductor protects
   nothing — the termination is already isolated, and its bias has to be fed on its own side of `Cx`
   (a choke at the terminal, with §22.3's baseband caveat). The tooltip and the note name `Cx` and say
   that, rather than offering a block that does nothing for the device. **Assumption recorded here
   for the owner to overrule:** if the owner would rather the block be offered anyway in that case,
   the walk's series-capacitor stop becomes a warning instead of a withhold — one branch, one test.

**Sequencing.** M1 Core (the walk, the note's new fields, tests). M2 Designer (tooltip, status line,
the rewritten enable test, a T fixture, a series-RC fixture). M3 docs (§22.1/§22.5 corrected in
place, the user reference, the `MatchElement.DcBlock` remarks).

---

## 1. What already exists

- `MatchDcBlock.EndShuntInductorIndex(network, end)` — the node lookup being replaced. It is public
  and the Designer's `DcBlockHost` / `CanDcBlock` / `DcBlockDefault` all go through it, so replacing
  its body (and renaming it) is the whole of the wiring.
- `MatchNetwork.AssignNets()` — nets derived by walking the list: a series element steps the through
  node forward, a shunt element hangs off the current node. Walking the list from index 0 upward IS
  walking the DC path in from termination 1; walking from the last index downward IS walking in from
  termination 2 (a series element steps the node backward, shunts hang off the node between).
- `DcBlockNote` — `Applied`, `ElementName`, `Reason`, … `MatchDesignerViewModel.DcBlock.cs` renders it.
- `MatchDesignerViewModel.DcBlockTooltip(end)` — the three sentences; the "series arm" one goes.
- `MatchLadderLayout.GroundYFor` — per-column ground, so a block on an interior column already draws.
- Fixtures: §4.9's golden ladder is **Term1 shunt end / Term2 series-RC end** — Term2 is the FET-input
  case for free. `TransformForm.T` on the first pair of the same ladder is the second case.

## 2. Core — `MatchDcBlock` (`src/Core/Match/MatchDcBlock.cs`)

### 2.1 The walk

Replace `EndShuntInductorIndex` with:

```csharp
/// Where one end's block would go, walking the DC path in from that termination.
public static DcBlockHost ResolveHost(MatchNetwork network, int end);

public readonly record struct DcBlockHost(
    int Index,                     // the host shunt inductor, or -1
    DcBlockStop Stop,              // why the walk ended
    string StopElementName,        // the real series capacitor that isolates, or ""
    IReadOnlyList<string> Path);   // the real series inductors crossed to reach the host, end-first

public enum DcBlockStop { Host, SeriesCapacitor, NoShuntInductor }
```

The walk, for end 1 over indices `0..n-1` and for end 2 over `n-1..0`:

- **absorbed** → skip (transparent; it is the termination's own reactance).
- **shunt L** → `Host`, return its index and the path so far.
- **shunt C** → continue.
- **series L** → append its name to the path, continue.
- **series C** → `SeriesCapacitor`, naming it; index −1.
- end of list → `NoShuntInductor`, index −1.

Keep `EndShuntInductor(network, end)` as a thin wrapper returning the element or null (the Designer
uses it), and keep the both-ends-first collision rule in `Apply` exactly as it is (a ladder whose two
walks land on one element blocks it once and notes the second end).

**Two real shunt inductors on the host node.** The MN-DCB comment says this does not occur in any
ladder the rebuild produces. Now that a host can be an interior node, **verify it rather than
restate it**: a Core test walks every golden fixture, its π and T variants on the first and last
pair, and both split cases (Fano and detune, shunt kind), and asserts that no node carries two
non-absorbed shunt inductors. If one does, `Apply` must block *both* (a second unblocked one is a
short), and RESOLVED records where it came from. Do not silently block the first.

### 2.2 The note

`DcBlockNote` gains `Path` (the series inductors crossed, end-first; empty when the host sits on the
end node) and `StopElementName`. The inactive reasons become:

- `SeriesCapacitor`: *"DC block at termination N: Cx is a real capacitor in this end's through path
  and already isolates it from DC — a block on a shunt inductor beyond it would not protect this
  termination; feed its bias on the termination's own side of Cx. Stored, not applied."*
- `NoShuntInductor`, lowpass form: the existing lowpass sentence.
- `NoShuntInductor`, otherwise: *"DC block at termination N: no shunt inductor lies on this end's DC
  path — stored, not applied."*

Nothing else about the note or the compensation changes.

### 2.3 Consumers

`MatchResponse.At`, `MatchModel`'s stamp, `MatchFlattenPlan.Build` and the drawing read
`MatchElement.DcBlock` and are position-agnostic already. **Confirm, don't assume**: the flatten ⇔
stamp ⇔ response equivalence test (MN-DCB test 4) is re-run on the two new fixtures, and the DC test
(MN-DCB test 5) is extended so the termination node of the series-RC end sees an OPEN through the
series inductor at DC.

Update the `MatchElement.DcBlock` remarks: "at an end node" → "on the first real shunt inductor of a
termination's DC path".

## 3. What the user sees

### 3.1 The toggle

Enabled exactly when `ResolveHost(end).Index >= 0` on the current rebuild — so it stays enabled
across π ↔ T on the end pair, and it is enabled on a series-RC end. Tooltips:

- Host on the end node: the existing enabled sentence.
- Host one or more series inductors in: *"Insert a DC-blocking capacitor in series with L2, the first
  shunt inductor on this end's DC path (reached through L1 — a series inductor passes DC). L2 is
  enlarged so the branch's reactance at the band centre is unchanged. Edit the value in the network
  pane."*
- `SeriesCapacitor`: *"Cx is a real capacitor in this end's through path and already isolates it from
  DC. A block beyond it would not protect this termination — feed its bias on the termination's side
  of Cx."*
- `NoShuntInductor`: the lowpass sentence for the lowpass form; otherwise *"No shunt inductor lies on
  this end's DC path."*

The sentence *"This end's arm is a series arm — its capacitor already blocks DC"* is **removed
everywhere** (view-model, tests, match.md, the user reference). It is false.

### 3.2 The status line

When `Path` is non-empty, the feed rule names the route:

```
  DC block at termination 2: 1.00 nF in series with L3 (105.9 pH, from 99.5) — the DC path from
  termination 2 reaches L3 through L4; branch resonates at 490 MHz; inductance ±1.2 % across the
  band. Feed the bias through L3; it reaches the termination through L4, not through a separate choke.
```

Unchanged when `Path` is empty. The `warn` class rule is unchanged.

### 3.3 The value survives π ↔ T

`TermNDcBlock` is on the design and the host is re-resolved each rebuild, so switching the end pair's
transform moves the block to the new host with the user's value intact — nothing to do beyond not
breaking it, and one test to hold it (§5 test 10).

### 3.4 Drawing, flatten, inline edit, persistence, undo

Unchanged. The block capacitor draws under its host in that host's column (`GroundYFor` is per
column); `Lnblk` is the name whatever `n` is; `MatchInlineEditKind.DcBlock` still resolves by end.

## 4. Design note and user reference

- `match.md §22.1`: rewrite the "ends, and only the ends" argument as the DC-path rule. The corrected
  claim is: *the block is needed on the first real shunt inductor a termination's DC current meets,
  and there is at most one such inductor per end, because the next real series capacitor ends the
  path.* Keep the lowpass paragraph. State fact 5's withholding rule and that it is an owner-overridable
  assumption. Bump to rev 7 with the date.
- `match.md §22.5`, first bullet: "Enabled only when that end's arm is a shunt inductor" → the walk.
- `docs/user/src/reference/match.md` around line 251: same correction, in the user's terms — *"the
  block goes on the first shunt inductor your bias current would reach; a series inductor in the way
  doesn't stop it, a real series capacitor does, and your FET's own input capacitance is not a real
  capacitor on the board."*

## 5. Tests

`tests/Core.Tests/Match/MatchDcBlockTests.cs` (add; keep all MN-DCB tests, re-pointing the ones that
assert a series end is inactive):

1. **Series-RC end.** §4.9's golden ladder, block at Term2: `ResolveHost(2)` is the shunt inductor
   one series inductor in (name it in the test output), `Path` has one entry, the block applies, the
   compensation identity holds to 1e-12, and the ABCD oracle (an explicit series L–C branch to ground
   at that node) matches `MatchResponse.At` to 1e-12 at 401 points.
2. **T on the end pair.** Same ladder, `TransformForm.T` on the first pair, block at Term1: the host
   is the T's shunt product, the path is the T's first series product, the response with the block
   is within 0.05 dB worst-RL of the block-free T response.
3. **π on the end pair still resolves to the end node** (MN-DCB test 6, kept — a regression guard
   that the walk did not lose the simple case; `Path` empty).
4. **A real series capacitor isolates.** A fixture where `WithEndSplits` inserts a series `CFano` or
   `CDetune` at an end (a series-C termination whose Q exceeds the synthesis Q by more than
   `ExcessRatioThreshold`): `Stop == SeriesCapacitor`, `StopElementName` is that element, nothing
   applied, the note carries the sentence in §2.2.
5. **Highpass, series-C end absorbed.** §16.6's dual with a series-RC termination: the host is the
   shunt inductor behind the absorbed capacitor.
6. **Lowpass.** Both ends `NoShuntInductor`, nothing applied.
7. **DC.** Series-RC end with a block: at ω = 0 the flattened cell shows the termination node OPEN to
   ground through the series inductor (the DC operating point at that node is the bias). Without the
   block it is a short (kept).
8. **Both ends, both interior.** A ladder whose two ends are both series (order and terminations
   chosen so): two distinct hosts, both applied; and the same-host collision case from MN-DCB kept.
9. **No node carries two real shunt inductors** — §2.1's verification, over every golden fixture ×
   {none, π, T} on the first and last pair × {no split, Fano, detune}.

`tests/Ui.Tests/Match/MatchDcBlockDesignerTests.cs`:

10. **Rewrite `TheToggle_IsEnabledOnlyWhereAShuntInductorSits_AndNamesTheReasonOtherwise`**: the golden
    ladder's Term2 is now ENABLED with a tooltip naming the host and the path; assert the "already
    blocks DC" string appears nowhere in either tooltip. Lowpass assertions kept.
11. **π ↔ T keeps the block.** Set a block at Term1, apply T on the first pair: toggle still checked,
    design value unchanged, status line names the new host and the path; apply π back: host returns
    to the end node, value unchanged; one undo per step.
12. **Series-capacitor end.** Test 4's fixture in the Designer: disabled, tooltip names the capacitor
    and says where to feed the bias.
13. **Status line with a path** reads the §3.2 sentence for the series-RC fixture.
14. **Drawing.** The block draws under an interior host in that host's column; the end column's ground
    is unmoved.
15. **Flatten.** The series-RC fixture's flattened cell carries `L{n}` compensated, `L{n}blk`, and the
    absorbed C disabled — the element list in the dialog matches.

## 6. Gates

```
dotnet build
dotnet test tests/Core.Tests --no-build
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

Run each ONCE; read the TRX. Grep the diff for vendor or product names before finishing.

## 7. On completion

Findings — whether test 9 found a two-inductor node and where it came from, the measured RL numbers
of tests 1–2, anything the drawing needed for an interior host — to **`src/Core/Match/RESOLVED.md`**
§MN-DCB2 (Designer findings to `src/Ui/RESOLVED.md` §MN-DCB2). **Never to any `CLAUDE.md`.** Do not
commit; the owner commits.

## 8. Out of scope, deliberately

- A series block in the through path (the lowpass need) — unchanged from MN-DCB.
- Modelling the bias feed or the baseband — match.md §22.3–§22.4 unchanged.
- Offering the block behind a real series capacitor — withheld under fact 5, owner-overridable.
- Re-synthesis with the blocked branch as a finite transmission zero — unchanged.
