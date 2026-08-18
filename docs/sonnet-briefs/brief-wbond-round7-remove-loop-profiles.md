# wBond Round 7 — remove loop profiles

**Owner decision, 2026-08-18.** Delete the `LoopProfile` object, the ball/wedge designation, the
profile *binding*, and the free-versus-bound rendering. A wire's points become the only truth about
its shape, in the model and in the UI.

> *"User does not care if the profile is any of these 'profiles'. Wires should not get this
> designation… Also remove how the wire colors change when a wire converts to 'free'."*

The decisive answer to the one question that could have saved the concept:

> *"User would never share one loop shape for multiple arrays and want to edit it in one place. Each
> array is generally its own shape and I want flexibility for user to change each wire within the
> array."*

Shared-shape propagation was the only thing binding uniquely bought. It is not wanted, so the object
has no remaining purpose.

**This is the third and last step of a retreat already two-thirds done**, and the brief should be read
that way rather than as a new direction. `ControllingParameters` already carries the owner's
2026-08-17 note — *"I don't like this ball/wedge profile thing. It doesn't offer the user anything.
Its setting should never affect the geometry that the user authors."* — and already applies loop
height through `WireEdits.SetLoopHeightPreservingPath` instead of regenerating from a profile.
`WBondViewModel.ScaleSelection` already moved alt-drag off profiles onto the array
(`WBondViewModel.cs:388-402`). What is left is the stored object and its rendering.

---

## 0. What the profile actually is today, so the size of this is not overstated

`LoopProfile` is a normalised (span, height) shape plus one `LoopHeightNm`, with an exact
amplitude solve (`SolveAmplitudeNm`). Ball and wedge are the same code:

```csharp
// src/WBond/LoopProfile.cs:166-171
public static LoopProfile BallBond(...)  => Peaked("ball",  ..., peakSpan: 0.30);
public static LoopProfile WedgeBond(...) => Peaked("wedge", ..., peakSpan: 0.50);
```

Nothing in the repo branches on ball-versus-wedge. The only place either word is read back is
`WireTableCsv.cs:185`, choosing which factory a CSV column calls. **The designation is a seed shape
and nothing else** — which is exactly the owner's reading, and it is complete.

`ProfileBinding` is a generator link. The functions still keyed on it:

| Site | What it does today |
|---|---|
| `WBondViewModel.ReapplyProfileToSelection` (`:654`) | re-stamp selected wires from their bound shape |
| `WBondViewModel.DetachSelection` (`:451`) | clear the binding |
| `WBondViewModel.ScaleProfileHeight` / `ScaleProfileSpan` (`:353`, `:366`) | reached by **no gesture today** — dead by the file's own admission |
| `ProfileEnvelope.Build` | which array members go in the band and which are drawn individually |
| `ControllingParameters` (`:134-160`) | the legacy `LoopHeight_<profile>` spelling |
| `WBondRenderer` (`:228-230`, `:400-406`) | the free-wire colour, in **two different meanings** — see §4 |
| `WBondIo` | persisted `Profiles`, `WireArray.Profile`, `Wire.ProfileBinding` |

**Everything the user actually reaches for already has a binding-free path.** `SetGroupLoopHeight`,
`SetGroupSpan` and the group flip all take a free branch that synthesises a shape from the wire's own
points, applies it, and clears the binding again (`WBondViewModel.Groups.cs:110-125, 250-265,
430-445`). So "set the whole array's loop height at once" — the thing binding sounds like it exists
for — does not need binding and has not needed it for some time.

---

## 1. The shape the removal leaves behind

Three operations survive, all of them stateless:

1. **Seed** — create a wire with an arch, at a requested loop height and point count.
2. **Reshape** — change one wire's or one array's loop height / span / crest position, *preserving
   the authored X-Y path*.
3. **Transfer** — read one array's shape as normalised text and stamp it onto another (the existing
   Copy/Paste Profile Coordinates menu items, §9).

None of the three needs a stored, named object. Transfer is the only one that moves a shape between
arrays, and it is a one-shot copy the user asks for explicitly — which is not what the owner
objected to.

### 1.1 `LoopProfile` → `LoopShape`

Replace `src/WBond/LoopProfile.cs` with `src/WBond/LoopShape.cs`: the same arithmetic, no identity.

```csharp
public readonly record struct ShapePoint(double Span, double Height);

public static class LoopShape
{
    public const double SeedPeakSpan = 0.30;

    /// The seed arch: N points, crest at SeedPeakSpan, measuring loopHeightNm.
    public static IReadOnlyList<ShapePoint> Seed(int points = 7, double peakSpan = SeedPeakSpan);

    /// Writes an arched polyline between two feet. Feet are written exactly, never interpolated.
    public static void Write(Wire wire, Point3 start, Point3 end,
                             IReadOnlyList<ShapePoint> shape, long loopHeightNm);

    /// Unchanged, and it is the good part: the closed-form amplitude solve.
    public static double SolveAmplitudeNm(IReadOnlyList<ShapePoint> shape,
                                          long loopHeightNm, long startZ, long endZ);

    /// A wire's own geometry read back as a normalised shape (today's private SynthesiseProfile).
    public static IReadOnlyList<ShapePoint> Read(Wire wire);

    public static IReadOnlyList<ShapePoint> Flip(IReadOnlyList<ShapePoint> shape);
    public static void Validate(IReadOnlyList<ShapePoint> shape);
}
```

`SolveAmplitudeNm`, `Validate`, `Flip` and the feet-are-exact rule move over unchanged. Their doc
comments are load-bearing and must come with them — particularly the two that explain *why* loop
height is max-z-minus-min-z and why the feet cannot move. **Keep `SeedPeakSpan` at 0.30** so no
shipped default geometry moves and no golden test shifts; it is a one-line knob if the owner ever
wants a symmetric seed instead.

`BallBond` and `WedgeBond` are deleted, not renamed.

### 1.2 Model fields deleted

- `Wire.ProfileBinding` (`WBondDesign.cs:45`)
- `WireArray.Profile` (`WBondDesign.cs:133`)
- `WBondDesign.Profiles` and `WBondDesign.ProfileByName` (`:163`, `:197`)

---

## 2. Reshaping: use the primitives that already exist, and fix a bug on the way

`WireEdits` already has the right primitive and it is better than the profile path:

- `SetLoopHeightPreservingPath` (`WireEdits.cs:621`) — scales every point's rise above its own chord
  by one factor found by bisection, so **X and Y are left exactly as authored**.
- `ScaleSpan`, `ScaleHeightAboutChord` — the alt-drag primitives.

**The bug.** `LoopShape.Write`/`LoopProfile.ApplyTo` writes X and Y by *linear interpolation between
the feet*. `WBondViewModel.Groups.cs` still reshapes free wires through synthesise-then-apply, so
**a group loop-height change straightens a hand-routed wire's X-Y path**, while the same wire's
`LoopHeight_G1` controlling parameter — fixed on 2026-08-17 — preserves it. The editor and the
netlist therefore disagree about the same wire.

Fix it as part of this work: route `SetGroupLoopHeight` and `SetWireLoopHeight` through
`WireEdits.SetLoopHeightPreservingPath`. `SetGroupSpan` and the group flip keep using
`LoopShape.Read` → transform → `LoopShape.Write`, because both of those *are* X-Y operations and
interpolating between the feet is correct there.

Note `SetLoopHeightPreservingPath` returns `false` on a dead-straight wire (nothing honest to scale).
Preserve that refusal; do not fall back to the interpolating path to paper over it.

---

## 3. Wire creation

`WBondViewModel.AddWire` (`:927`) currently finds-or-invents a profile and joins the first array that
references it. Rewrite:

```csharp
public int AddWire(Point3 start, Point3 end, long diameterNm, string material,
                   string? arrayName = null, int points = 7, long? loopHeightNm = null)
```

The wire joins the **named array**, or a new one from `NextArrayName()`. This is a straight
improvement: today "which array does a new wire join" is answered by profile identity, which is why
`LayoutEditorViewModel.WBondDrop.cs:113-140` has to invent a uniquely-named throwaway profile —
`FreeProfileName` — purely to force a new array. That whole helper goes away and the drop path asks
for a new array by name, which is what it meant.

Same simplification at `ParameterEditorViewModel.WBond.cs:348-352` (Add Array) and
`WBondEmbedding.DefaultDesign` (`:213`).

---

## 4. The profile view and the colour

### 4.1 Re-key the envelope on geometry, not on binding

`ProfileEnvelope.Build` splits an array's wires into `BoundWires` and `FreeWires` by
`array.Profile == wire.ProfileBinding && IsProfileEditable(wire)`. Drop the first clause. The split
becomes **editable versus not**, where not-editable means exactly what `IsProfileEditable` already
tests: an XY path that backtracks, so it has no monotone span and cannot be drawn against normalised
span without self-overlap.

Rename the record fields to say so: `BoundWires` → `Members`, `FreeWires` → `Unrepresentable`, and
`ProfileName` is deleted. The band then spans every drawable member of the array — which is precisely
the owner's *"each array is generally its own shape"*.

### 4.2 The colour, which is the part the owner actually minds

`ColorRole.WBondFreeWire` is used for **two unrelated things**:

- `WBondRenderer.cs:228-230` — layout view: a wire with no binding. **Delete this.** Free and bound
  wires render identically.
- `WBondRenderer.cs:400-406` — profile view: a *non-representative* member of an array, i.e. one
  drawn behind the band. **Keep this, renamed** to `ColorRole.WBondMember` (`wBond.Member`), because
  it distinguishes the one editable curve from the members behind it, which is real information and
  has nothing to do with binding.

Keeping one role under two meanings is what made the layout view recolour wires as a side effect of
editing. The transitions were involuntary and unexplained: inserting or deleting a point detaches
(`WBondViewModel.WireShape.cs:67`, `Deletes.cs:71,164`), and so does a group height / span / flip.

Both palettes in `ColorTheme.cs:144` and `:223` keep their existing values under the new name — the
profile view's appearance does not change.

---

## 5. Persistence

Delete `ProfileDto`, `WBondDocument.Profiles`, `ArrayDto.Profile`, `WireDto.ProfileBinding` from
`WBondIo.cs`.

**No format-version bump and no compatibility shim.** `System.Text.Json` with the options at
`WBondIo.cs:27` ignores unknown members, so an existing `.wBond` carrying `Profiles` and
`ProfileBinding` reads cleanly into the new DTOs. **No geometry is lost, in either direction**,
because `Points` is stored explicitly and always has been — the file only stops recording *which
shape a wire was generated from*, which after this brief names nothing.

`WBondCellSeeding.ProfileNamesOf` (`:528`) and its callers are deleted. `WBondClipboard`'s
`ProfileBinding` field and its profile-carrying logic (`:38, 85, 98, 179`) go with them; the
clipboard already carries points, which is the truth.

The undo snapshot (`WBondViewModel.cs:1012-1031, 1129-1133`) drops `Bindings` and `ProfileHeights`
from `Snapshot` and `ArraySnapshot.Profile` from the array record — three fewer things to keep in
step across an undo.

---

## 6. Controlling parameters

Delete the `byProfile` block (`ControllingParameters.cs:132-160`) and the `LoopHeight_<profile>`
legacy spelling with it, along with the array-name-collides-with-profile-name note it raises. Array
scope is the only scope. `ApplyTo` gets shorter and loses its one remaining reason to look at
`design.Profiles`.

The class doc's §"A loop height rescales the wire; it does not regenerate it" survives and should be
promoted — it is now the whole story rather than a correction to one.

---

## 7. Import paths

**CSV** (`WireTableCsv.cs`). Delete `Settings.DefaultProfile` and the `profile` column's effect.
A `profile` column in an existing file is **read and ignored**, not an error — a header this tool
used to write must not start refusing files. Every wire is generated from the seed arch at the
table's `loopheight`. Say so in the class doc.

**DXF** (`DxfWireIo.cs:243`). The comment already says imported wires arrive free "because a polyline
carries a shape, not the intent behind it" — under this brief that sentence stops being a caveat and
becomes ordinary. Reword; no behaviour change.

---

## 8. UI surfaces removed

- `WBondEditorView.axaml:266-288` — the **Reset to loop profile** and **Detach from loop profile**
  buttons, their handlers `OnReapplyProfile` / `OnDetach` (`.axaml.cs:929-931`), and the view-model
  commands `ReapplyProfileToSelection` and `DetachSelection`.
- `WBondViewModel.ScaleProfileHeight` / `ScaleProfileSpan` — already reachable by no gesture.
- `WBondProfileCanvas.SelectedProfileName` (`:991`) and the profile branch of `ReferenceHeightNm`;
  the fallback that reads the wire's own max-minus-min z becomes the only path, which is what it
  should always have been.
- `WBondWirePropertiesViewModel.cs:217` — the `ProfileBinding` / `(free)` row, and its
  `WBondWirePropertiesView.axaml:64` binding.

**Straighten stays** (`wbond.md:1010`) but its rationale changes: it currently keeps the point count
"so a profile can be re-applied". With nothing to re-apply from, the reason is now that a user who
straightens by mistake can undo, and that the point count is the user's own choice. Update the
tooltip and the doc line; do not change the behaviour.

---

## 9. Copy / Paste Profile Coordinates — kept

`WBondProfileView.ContextMenu.cs:150-180` copies an array's shape to the clipboard as text and pastes
it onto another. **This survives**, because it is a one-shot transfer the user asks for by name, not
a persistent link — the thing the owner rejected. Under the new model it is simply:

- Copy → `ProfileCoordinateText.Write(LoopShape.Read(representative), unit)`
- Paste → `LoopShape.Write` onto every member of the target array

`ProfileForGroup` (`Groups.cs:37`) loses its stored-profile branch and keeps its synthesis fallback.
`ApplyProfileToGroup` (`:278`) stops installing anything on the design and just writes the wires.
`ProfileCoordinateText` keeps its file and its format; its type parameters change from `LoopProfile`
to a shape list plus a loop height, and its class doc's "a `LoopProfile` is a SHAPE several wires
share" opening (`:11`) is rewritten.

---

## 10. `docs/design/wbond.md`

This is an architecture change and the design note must move with it. The edits, by line:

| Lines | Change |
|---|---|
| 52-54 | Model tree: drop `LoopProfile[]`, `WireArray.profile`, `Wire.ProfileBinding` |
| 58, 62 | "a `LoopProfile` binding is a *generator*" → points are the only truth; keep the D1 statement, drop the generator sentence |
| 127, 143-145 | §3.1a: the amplitude solve moves to `LoopShape.SolveAmplitudeNm`; keep the whole explanation, it is still correct and still load-bearing |
| 683-687, 713-714, 723 | §5: parameters are array-scoped; delete the "a profile may be shared by two arrays, so overriding one must clone it" paragraph — the clone-on-write problem ceases to exist |
| **794-908** | **§6.2, the main rewrite.** Idea (1), normalised-span parameterisation, is untouched and still the reason the view works. Idea (2), "`LoopProfile` as a first-class named shared object", is **withdrawn** — record the withdrawal and the owner's reason rather than deleting the paragraph. Idea (3), envelope rendering, survives with the band re-keyed on drawability (§4.1). **WB24** ("dragging the profile curve edits the `LoopProfile`, which regenerates every bound wire; dragging an individual wire detaches it") is **withdrawn**: dragging a curve in the profile view edits that wire, and there is nothing to detach from. **WB24a and WB24c** survive as array-scoped alt-drag, which they already are in code |
| 906-908 | The backtracking-XY residual limit survives verbatim — it is now the *only* reason a wire is drawn outside the band |
| 1010 | Straighten's rationale (§8 above) |
| 1151-1153, 1343-1348 | "Removing a point DETACHES the wire from its profile" — delete; there is no detach |
| 1592 | Persistence: "loop profiles and bindings" out of the stored list |
| 1656, 1694 | CSV `profile` column read-and-ignored; DXF wires arrive as ordinary wires |
| 1994 | The WB-C row's "`LoopProfile` binding" and "profile edit propagates to bound wires and detaches on individual drag" gates are retired — mark them so rather than rewriting history |
| 2020 | **D6 is revised.** The decision table is a record of decisions, so add a dated revision line rather than editing the original answer: normalised-span parameterisation and envelope rendering stand; the shared bindable object is withdrawn 2026-08-18 |
| 2037 | Same treatment for the alt-drag row |

Add a §6.2 note dated 2026-08-18 recording *why*, in the owner's own terms — that each array is its
own shape, that per-wire freedom is the requirement, and that no user would edit one shape in one
place for several arrays. Without that, the next person to read idea (2) will helpfully re-propose it.

---

## 11. Gates

**Build + test green**, scoped to what this can reach — this touches `src/WBond` and `src/Ui`, so
`Engine.Tests` is in scope only for `WBondParameterTests`:

```
dotnet test tests/WBond.Tests --no-build
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
dotnet test tests/Engine.Tests --no-build --filter "FullyQualifiedName~WBond"
```

Roughly 166 references across 23 test files. Most are fixture construction —
`LoopProfile.BallBond(h)` becoming `LoopShape.Seed()` — and are mechanical.
`tests/WBond.Tests/ProfileEnvelopeTests.cs` (21) and `tests/Ui.Tests/WBondRound6Tests.cs` (21) carry
the most real assertions and should be read rather than sed-ed.

New or rewritten tests that must exist:

1. **Geometry survives the schema change.** Write a `.wBond` with the *old* build's fields present in
   the JSON (a string fixture, not a round trip), read it with the new one, assert every point of
   every wire is identical to the nanometre. This is the one thing a user can lose and cannot get
   back.
2. **A group loop-height change preserves X and Y.** Build a wire whose interior points are *off* the
   straight chord in XY — a deliberate dog-leg — set the group loop height, and assert every X and Y
   is unchanged while max-z-minus-min-z is the requested value. This is §2's bug; it must fail before
   the fix and pass after. Give it both a level-feet and an unequal-feet case, since the amplitude
   solve differs between them.
3. **The layout view draws every wire in one colour.** A design mixing wires that would formerly have
   been bound and free renders with no second wire colour. A render-counter or paint-colour probe,
   the way the existing `WBondRenderAndToolsTests` do it.
4. **The band spans every drawable member.** An array whose members have visibly different shapes
   produces a band whose min and max at some span differ by the actual spread, with no member
   excluded for want of a binding. Include one backtracking-XY wire and assert it is the *only*
   member outside the band.
5. **Copy → Paste shape across arrays still works**, from an array with no stored profile to another
   with none, and the target's feet do not move.
6. **`ProfileEnvelope.IsProfileEditable` is unchanged** — keep its existing tests as-is; the
   backtracking rule is not part of this brief.

---

## 12. Guardrails

- **Do not touch `src/Core`, `src/Engine`, or `src/RfCore`.** The one Engine file in scope
  (`tests/Engine.Tests/Devices/WBondParameterTests.cs`) is a test fixture using `LoopProfile.BallBond`
  to build a wire; it changes to `LoopShape` and nothing else does.
- **Do not change any wire geometry.** Seed peak span stays 0.30, seed point count stays 7, the
  amplitude solve is moved verbatim. If a golden geometry test moves by a nanometre, something was
  changed that should not have been — stop and report rather than re-baselining.
- **`ProfileAxisSetting` and `ProfileProjection` are not in scope.** The profile *view* and its
  Auto / XZ / YZ plane setting stay exactly as they are. Only the profile *object* is being removed,
  and the name collision between the two is a large part of why this concept confused the owner in
  the first place — do not let it confuse the implementation.
  - One doc slip to fix while nearby, since it is one line: `ProfileProjection`'s parameter doc
    (`src/WBond/WireHitTest.cs`) says "auto is still the default". It is not — the default is YZ
    (`WBondViewState.DefaultProfileAxisDegrees = 90.0`, owner, 2026-08-16).
- **The diff should remove considerably more than it adds.** If it does not, the shape has been
  re-created under a new name somewhere. Report the line counts.
- **Stop and report** if `ProfileCoordinateText` or the envelope re-keying turns out to need more than
  a signature change — those are the two places where a stored shape could plausibly still be load-
  bearing, and finding that it is would be a real result worth hearing before the rest is spent.
