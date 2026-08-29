# Sonnet Brief — MN-DCB: the DC block on an end shunt inductor

**Design:** `docs/design/match.md` §22 (the block: where it is needed, the compensation, the
baseband consequence, the model), §4.4 (the resonant end arm the block sits in), §4.7 (why the
transforms never see it), §8.3 (the stamp), §11 (Flatten), §18.10 (the status strip the new line
joins). **Prerequisite:** MN-FH is landed (the status strip has its ceiling line; `MatchRebuild` is the
one pipeline every consumer reads).

**One sentence:** an end of the ladder whose first element is a shunt inductor and whose termination
carries DC (a drain, a gate) gets a DC-blocking capacitor in series with that inductor, the inductor
is enlarged so the branch's reactance at ω₀ is exactly what the synthesis asked for, the residual
across the band is reported, and nothing in the synthesis, the transforms or the solution search
changes.

**Why (owner, 2026-08-28).** A shunt inductor at a biased node is a short across the supply. Every PA
output network built from a shunt-first bandpass ladder needs the block, every interstage network
whose gate-side arm is shunt needs one at that end too, and today the user adds it by hand *after*
Flatten and re-tunes the inductor by eye. The compensation is one line of arithmetic and the
Designer already knows every number in it. The owner also asked where the control could live given
that the specification pane has no room left — §3 answers that with a control that costs **zero
height**.

**Structural facts — read before touching anything.**

1. **Only the END shunt inductors can ever need it.** In a bandpass ladder every series arm contains
   a capacitor, so an interior shunt inductor is DC-isolated from both terminations by construction;
   in the highpass form the through path is all capacitors, so the same holds. The lowpass form has no
   shunt inductor at all — it passes DC end to end, and blocking THAT needs a series capacitor in the
   through path, which is a highpass pole and a different (harder) compensation. **Out of scope; say
   so in the tooltip rather than offering nothing silently.**
2. **It is a post-rebuild step, not a synthesis input.** The block is attached to *whichever shunt
   inductor sits at the end node after the transforms have run* — resolved by node, not by element
   name, because a Norton π on the first pair replaces L1 by a product that is still a shunt inductor
   at that node. It therefore never enters `MatchSynthesis`, `NortonTransform`, `MatchSolutionSearch`,
   the basis fingerprint or the solution fingerprints. `MatchRebuild.Rebuild` applies it **after**
   `WithEndSplits`, and that is the only place it is applied.
3. **The compensation is exact at ω₀ and second-order elsewhere.**
   ```
     L' = L + 1/(ω₀² C_blk)                       branch reactance at ω₀ unchanged
     f_s = 1/(2π √(L' C_blk))                     the branch's series resonance, in the baseband
     L_eff(ω)/L = (1 − ω_s²/ω²) / (1 − ω_s²/ω₀²)   ⇒ spread ≈ ±2 (f_s/f₀)² (Δf/f₀) across the band
   ```
   Measured (match.md §22.2, scratch ABCD): with L = 99.5 pH at 2 GHz a 500 pF block puts f_s at
   672 MHz, L' = 112.3 pH, L_eff runs 96.6 / 99.5 / 101.8 pH across 1.8 / 2.0 / 2.2 GHz and the worst
   RL goes 21.6 → 18.8 dB; at ≥ 10 nF the spread vanishes and the RL is 21.4 dB; at 1 nF the
   compensated branch gives 20.1 dB where the uncompensated one gave 13.6 dB. **Default the block to
   `C_blk = 100 / (ω₀² L)`**, i.e. `f_s = f₀/10` and a spread under 1 %; **warn above `f_s > f₀/5`.**
4. **The network model cannot express a series pair to ground.** `MatchNetwork.AssignNets` derives
   every net from the flat list — a shunt element is node-to-ground, full stop — and §6.8 excluded
   branches with internal nodes on exactly this ground. Do not add an internal node to the list. Add
   **one property**, `MatchElement.DcBlock` (farads, 0 = none, only ever set on a shunt L), and teach
   the four consumers that compute a shunt admittance to honour it: `MatchResponse.At`, the stamp,
   `MatchFlattenPlan`, and the ladder drawing. Everything else — the transforms, the pair scan, the
   linkage, the fingerprints — sees an ordinary shunt inductor, which is exactly right.
5. **The baseband is the block's other half, and it is NOT in this brief beyond one sentence.** A
   block at the drain end only works when the bias is fed *through* the compensated inductor and the
   block is its far-end decoupling; a separate choke at the drain resonates with the block inside the
   baseband (measured 290 Ω – 30 kΩ peaks at 1.6 – 16 MHz, match.md §22.3) and no lossless network can
   remove that. The status line says which topology the compensation assumes. Nothing here plots a
   baseband impedance or models a bias line.

**Sequencing.** M1 Core (model property, rebuild step, response, flatten, stamp; tests). M2 Designer
(the toggle, the drawing, the inline edit, the status line, persistence, undo; tests). M3 docs
(design-note cross-check, user reference). One optional owner-gated line in §6.

---

## 1. What already exists

- `MatchRebuild.Rebuild` → `MatchSynthesis.WithEndSplits` is the last step before consumers read the
  network. The block goes immediately after it.
- `MatchElement` carries three role flags (`AbsorbedEnd`, `IsExcess`, `IsDetune`) and
  `MatchLadderLayout.RoleOf` maps them to `MatchElementRole` for the drawing; the block's capacitor is
  drawn in the **Detune** role's manner (ours, distinct, not dimmed) — reuse the role rather than add
  one unless the drawing needs to tell them apart, in which case add `DcBlock` to the enum and to
  `RoleOf`.
- `MatchResponse.At` is the ABCD sweep every plot and every worst-RL number reads.
- `MatchModel` (`src/Core/Devices/MatchModel.cs`) stamps shunt elements as `ShuntElement(node, value,
  isL)`. `MatchFlattenPlan.Build` walks `AssignNets` and emits one instance per element.
- `MatchInlineEditText` / `MatchInlineEditKind` resolve a double-clicked label in the network pane;
  `TerminationReactance` is the precedent for "a value that is a specification input, shown in the
  ladder".
- The termination card's header row is `Grid ColumnDefinitions="Auto,*,Auto,Auto"` — heading, **an
  empty stretch column**, Probe, flag (`MatchDesignerWindow.axaml`, the `TerminationTemplate`).
- `MatchDesign` is JSON, additive; `QAdjust` shows the pattern for a per-design double that is zero
  when absent. Design edits go through `MatchDesignerViewModel.Commit` → one `SetParametersCommand`,
  which is what makes them undoable from either window.
- `MatchFanoBound` and the status strip's ceiling line (MN-FH) — the new line sits directly under it.

## 2. Core — `MatchDcBlock` (`src/Core/Match/MatchDcBlock.cs`, new)

```csharp
public static class MatchDcBlock
{
    /// Attaches the design's blocks to the end shunt inductors of a rebuilt network. Pure; returns
    /// a clone when anything changed, the same instance when nothing did.
    public static MatchNetwork Apply(MatchNetwork network, MatchDesign design, double omega0,
                                     out IReadOnlyList<DcBlockNote> notes);

    /// The compensated inductance: L + 1/(ω₀² C).
    public static double Compensate(double inductance, double blockFarads, double omega0);

    /// The default block for an inductor: f_s = f₀/10  ⇒  100/(ω₀² L), capped at maxFarads (§3.1).
    public static double DefaultFor(double inductance, double omega0, double maxFarads);

    /// f_s, the branch's series resonance.
    public static double SeriesResonanceHz(double compensatedL, double blockFarads);

    /// The band-edge spread of L_eff/L, as a fraction (0.012 = ±1.2 %).
    public static double BandSpread(double compensatedL, double blockFarads, double omega0, double f1, double f2);

    public const double WarnAboveRatio = 0.2;   // f_s > f₀/5 warns
}
```

`Apply`, per end with `design.TermNDcBlock > 0`:

1. Find the end node — `"p1"` for end 1, `network.RightPortNet()` for end 2 — and the **first**
   non-absorbed shunt element of type L on that node. (Two shunt inductors on one node do not occur
   in any ladder the rebuild produces; if one ever does, take the first and note it.)
2. No such element → the block is **inactive**: a note *"DC block at termination N: this end's arm
   has no shunt inductor to sit in (a series arm's capacitor already blocks DC) — stored, not
   applied."* Keep the design value; the user may be mid-way through changing order or form.
3. Otherwise set `element.DcBlock = C`, `element.Value = Compensate(element.Value, C, ω₀)`, and add
   the informational note the status strip renders (§3.3). **`ω₀ = 2π√(f1·f2)` of the design's OUTER
   band** — the same centre every arm is resonated at, multiband included.

Consumers:

- **`MatchResponse.At`** — for a shunt L with `DcBlock > 0`, `y = 1 / (jωL + 1/(jωC))`. At ω = 0 the
  admittance is zero (the branch is open at DC); guard it rather than divide.
- **`MatchModel`** — `ShuntElement` gains a `BlockFarads`; the stamp contributes the same admittance
  as a two-terminal branch between the node and ground. No internal node: a two-terminal branch has a
  driving-point admittance and that is what MNA stamps. At ω = 0 (the DC analysis) it contributes
  nothing, which is the physics. Keep the zero-value guard: a block of 0 never reaches the stamp
  because `Apply` only sets `DcBlock` when positive.
- **`MatchFlattenPlan.Build`** — an element with `DcBlock > 0` becomes **two instances and one minted
  internal node**: `L` from the node to `nB`, `C` from `nB` to ground, named `{L}` and `{L}blk`
  (`L1` / `L1blk`). The flattened cell's S-parameters must equal `MatchResponse.At`'s to 1e-9, which
  is the test.
- **`MatchLadderLayout` / `MatchSchematicModel`** — the shunt branch draws the inductor above the
  capacitor on the same vertical, the capacitor's label reading `L1blk 1.00 nF`, in the Detune (or
  new DcBlock) role. `ShuntGroundY` moves down by one symbol height for that column only if the
  layout is per-column; if it is a shared constant, the block capacitor is drawn at half scale between
  the inductor and ground rather than moving every ground symbol — pick whichever the layout code
  makes cheap, and say which in RESOLVED.

## 3. What the user sees

### 3.1 The control — in the termination header row, the column that is empty today

The `TerminationTemplate` header is `Auto,*,Auto,Auto`: heading, empty, Probe, flag. Put a
**`ToggleButton`, content `Block`, FontSize 10, the Probe button's exact padding and margin, in
column 1, right-aligned** so it reads as Probe's neighbour: `[Termination 1        Block  Probe ⚑]`.
It costs no height (the row exists), no width the card does not already have, and it sits on the card
of the end it acts on — which is the one placement where "which end?" needs no label.

- **Checked** → `TermNDcBlock = DefaultFor(L_end, ω₀)` if it was 0, else unchanged (re-checking after
  an uncheck restores the user's value, so **uncheck stores the value under a shadow field on the
  view-model, not on the design**; the design holds 0 when the toggle is off).
- **The default is a seed, and it is capped** (owner, 2026-08-28: too big a capacitor can be
  impossible to build). `DefaultFor` returns `min(100/(ω₀²L), MatchDesignerSettings.DcBlockMaxFarads)`,
  a new Settings entry beside `Qmin` — *"DC block default: f_s = f₀/10, at most [10 nF]"*. At a low
  band with a small end inductor the f₀/10 rule alone reaches tens of nF, fine on a board and absurd
  on an MMIC. **The value is the user's after that**: any positive capacitance is accepted, the
  compensation is exact at ω₀ for all of them, and the status line and the plot show what a small one
  costs. Nothing refuses a value; the warn class is a hint.
- **Enabled only when this end's arm is a shunt inductor** in the current rebuild. Disabled tooltip
  names the reason: *"This end's arm is a series arm — its capacitor already blocks DC."* or, for the
  lowpass form, *"A lowpass ladder passes DC end to end; a series block in the through path is not a
  shunt-inductor block and is not offered here."*
- Tooltip when enabled: *"Insert a DC-blocking capacitor in series with this end's shunt inductor.
  The inductor is enlarged so the branch's reactance at the band centre is unchanged. Edit the value in
  the network pane."*

### 3.2 The value — in the network pane, like every other value

The block capacitor is an element row in the grid and a labelled symbol in the schematic. Double-click
edits it through a new `MatchInlineEditKind.DcBlock` (End = 1 or 2, Quantity = capacitance) that
writes `TermNDcBlock` through `Commit` — the `TerminationReactance` path is the template. Typing 0
unchecks the toggle. No slider, no transform: the block is a specification input the user owns, and
the compensated inductor is displayed as the ordinary element it is.

### 3.3 Status strip — one line per active block, under the ceiling line

```
  DC block at termination 1: 1.00 nF in series with L1 (105.9 pH, from 99.5); branch resonates at
  490 MHz; inductance ±1.2 % across the band. Feed the bias through L1, not through a separate choke.
```

`Classes="warn"` when `f_s > f₀/5`, with the sentence *"— the block is small enough to detune the
band; 10× larger keeps the spread under 1 %."* The inactive case (§2 step 2) renders its note in the
same place, not as a refusal: nothing is wrong, the block simply has nowhere to be.

### 3.4 Flatten

The dialog's element list shows `L1` at its compensated value and `L1blk`; the flattened cell carries
both. The terminations remain disabled instances exactly as §11.3 specifies.

### 3.5 Persistence and undo

`MatchDesign.Term1DcBlock`, `Term2DcBlock` — doubles, farads, 0 = none, additive (an older payload
decodes with 0 and no toggle lit). Toggling and editing go through `Commit`, so Ctrl/Cmd+Z in either
window reverts them. The Solutions list is unaffected: solutions are fingerprinted on the basis and the
transforms, and the block is applied to whichever solution is current.

## 4. Design note

`docs/design/match.md` §22 is written (rev 6, 2026-08-28). Cross-check every number in §22.2 against
the tests as they land — the section quotes the scratch measurement, and the Core test is the first
independent reproduction. Record any discrepancy in `src/Core/Match/RESOLVED.md` §MN-DCB and correct
the section in place.

## 5. Tests

`tests/Core.Tests/Match/MatchDcBlockTests.cs`:

1. **Compensation identity.** For every golden ladder with a shunt end arm (§4.9's Term1 end,
   §18.4's shunt-first 8-element member, §16.2's highpass dual), a block of `DefaultFor` leaves the
   branch reactance at ω₀ unchanged to 1e-12 relative.
2. **Response.** §4.9's ladder with `DefaultFor` at Term1: worst RL within **0.05 dB** of the
   block-free value. With `C = 25/(ω₀²L)` (f_s = f₀/5) the degradation is bounded and the warn flag is
   set; with `C = 4/(ω₀²L)` (f_s = f₀/2) the degradation exceeds 1 dB — the number is recorded, not
   asserted to a tolerance.
3. **Oracle.** `MatchResponse.At` with `DcBlock` equals an ABCD built by hand with an explicit series
   L–C branch to ground, to 1e-12, at 401 points across the plot band.
4. **Flatten ⇔ stamp ⇔ response.** The flattened cell's S-parameters from the elaborator equal
   `MatchModel`'s stamped S-parameters equal `MatchResponse.At`, to 1e-9, with and without a block —
   the existing MN-5 equivalence test extended by a block fixture.
5. **DC.** With a block the DC analysis sees the branch open: the drain node's DC operating point is
   the bias, not zero. Without a block, the shunt inductor is a DC short (the current behaviour, kept).
6. **Node resolution survives a transform.** Apply a π transform on (L1, L2) to §4.9's ladder; the
   block re-attaches to the product shunt inductor at p1 and the response is unchanged to 0.05 dB.
7. **Inactive.** A design whose end arm is series stores the value, applies nothing, and the note
   names the end.
8. **Persistence.** Round-trip with both blocks set; an MN-FH-era payload decodes with both 0.

`tests/Ui.Tests/Match/MatchDcBlockDesignerTests.cs`:

9. Toggle enabled/disabled follows the current rebuild (bandpass shunt-first end: enabled; series
   end: disabled with the series reason; lowpass form: disabled with the lowpass reason).
10. Check → default value, uncheck → 0 on the design and the value shadowed, re-check → restored.
    A design whose f₀/10 value exceeds the Settings cap seeds the cap; a typed 100 pF is accepted,
    shown with its spread and the warn class, and never clamped.
11. Inline edit of `L1blk` writes the design through `Commit`; one undo restores the previous value;
    typing 0 unchecks.
12. The status line text for §22.2's 500 pF fixture reads the numbers quoted there (f_s 672 MHz,
    112.3 pH, ±2.3 %) and carries the warn class; at 10 nF it does not.
13. The drawing places `L1blk` in the shunt column under `L1` and in the block role.

## 6. Optional, owner-gated — the baseband hint (one line)

If the owner wants it, and only then: when `QAdjust > 0` and the analysis end's arm is shunt, add
under the ceiling line *"Q-adjust N makes the end inductor L pH — about X Ω of baseband reactance at
f MHz"* with f the user's choice in Settings (default 500 MHz). It is `ωL` of an element the design
already has. Nothing else of match.md §22.4 is in scope.

## 7. Gates

```
dotnet build
dotnet test tests/Core.Tests --no-build
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

Run each ONCE; read the TRX. Grep the diff for vendor or product names before finishing.

## 8. On completion

Findings — the measured RL numbers of test 2, anything the drawing needed, whether the node
resolution met a two-inductor node — to **`src/Core/Match/RESOLVED.md`** §MN-DCB (Designer findings
to `src/Ui/RESOLVED.md`). **Never to any `CLAUDE.md`.** Do not commit; the owner commits.

## 9. Out of scope, deliberately

- A series block in the through path (the lowpass form's need) — a different compensation.
- Blocks on interior shunt inductors — they are DC-isolated already and a control would be noise.
- Exact re-synthesis with the branch as a finite transmission zero (§6.8's excluded extraction) for a
  second-order residual the status line already reports.
- Any baseband impedance plot, bias-line model or decoupling design — match.md §22.4 records why.
- A block on a termination with no shunt inductor "for later" — the toggle is disabled instead.
