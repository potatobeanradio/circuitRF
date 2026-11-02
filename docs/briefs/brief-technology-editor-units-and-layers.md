# Sonnet Brief — Technology editor: display unit, ground designation, and signal/ground component parameters

Three related gaps, all about technology settings that exist in the model but have no way in or out.

Gate command is plain `dotnet test`.

---

## 1. Ground designation is stored but unreachable

`StackupLayer.IsGroundReference` already exists — a `bool` per conductor entry, whose own comment says it
marks a conductor "as a ground-reference plane, so a microstrip…". It is exactly the right field. Nothing in
the UI can set it.

**R-tec-1. Expose `IsGroundReference` as a checkbox on conductor rows in the stackup editor.** Conductor
entries only — it is meaningless on dielectric and via rows, so it should not render there at all rather than
render disabled.

**R-tec-2. `TechValidation` reports a stackup with no ground reference.** Microstrip components cannot
resolve a ground plane at all in that state, so they fail later and further from the cause. This is a
validation gap, not a UI one, and it is worth closing regardless of §1.

**Multiple ground planes are legal and unambiguous** — "nearest ground-designated conductor beneath" is
well-defined on a four-layer board with two planes. Do not warn on that.

## 2. `DefaultDisplayUnit` — a seed, and it must stay one

`Technology.DefaultDisplayUnit` exists and has no editor.

**R-tec-3. Add it to the Technology Editor, labelled as the default for *new* documents.**

The critical distinction, because conflating these is where the damage would be:

| User intent | Control |
|---|---|
| "I want **this layout** in mils" | the document's display-unit combo — **already exists** |
| "I want **new layouts** in this technology to default to mils" | the Technology Editor — **this change** |

**R-tec-4. Changing it must not touch any open or existing layout.** L0c's invariant — never re-seed an open
layout's `DisplayUnit` or `SnapDbu` — stands unchanged. Each `.clay` stores its own unit; the technology
value is consulted only when a layout is created. Wiring this to live-update open documents would silently
change what every dimension field means mid-session.

Label it so the user is not surprised: something like *"Default display unit for new layouts"*, not
*"Display unit"*.

### 2.1 The audit that makes this safe

The setting is only safe if this holds everywhere:

> **Display unit affects formatting and parsing only. Every stored value is DBU (geometry) or SI (parameters).**

The PCell contract's R-pc-6 already mandates SI metres for parameters with a single conversion helper, and
layout geometry is integer DBU. But if *any* path stores a number in display units, changing the unit
silently reinterprets that data.

**R-tec-5. Audit for stored display-unit values before making the setting reachable, and report the result.**
The case the owner flagged is the right place to start: **MKlopf's `Z1`/`Z2` ↔ `W1`/`W2` entry** (R-klp-3a).
When a user types `W1`, is it converted to SI at the boundary, or retained as "42 in whatever unit is
current"? The latter corrupts on a unit change.

Also confirm R-klp-3a still holds under a unit change: the **last-edited pair stays authoritative and is not
re-derived** from the other's displayed value. A unit change alters the *display* of the non-authoritative
pair and nothing else.

## 3. Signal and Ground belong on the components, and are missing

The PCell contract's **R11** says layer selection is `Signal Layer` + `Ground Reference`, defaulting from the
stackup and **per-instance overridable** — because which layer you route on is design intent, not a process
parameter. The defaults were implemented; **the override half was not.** MLIN, MTee, MBend, MCross, MTaper
and MKlopf have no such parameters in the Component Editor.

**R-tec-6. Add `SignalLayer` and `GroundReference` parameters to every microstrip component, with
"Display on Schematic" defaulted to `false`.** A schematic annotating every MLIN with
`Signal=Top Copper, Ground=Bottom Copper` would be noise, and on a two-layer board it is the default anyway.
The parameters exist to be *available*, not visible.

### 3.1 Where the values come from — three different things

Keeping these distinct is the whole design:

| | Source |
|---|---|
| **The legal options** | the resolved technology's stackup — its **conductor** entries |
| **The default** | inferred: topmost conductor; nearest `IsGroundReference` conductor beneath it |
| **The stored value** | per-instance, on the component |

**R-tec-7. Store the stackup layer's *name*, as a string.** This matches the precedent already set by
`StackupLayer.SpanFromLayer` / `SpanToLayer`, which store layer references as strings — so signal/ground
should not invent a second convention. Names also survive a technology change more meaningfully than indices
or keys, which is the L1g lesson about names carrying intent across vocabularies.

**R-tec-8. Empty means "follow the technology," and that must be reachable from the UI.** An unset parameter
falls back to the inferred default, so:

- a user who never touches it gets the zero-configuration behaviour the substrate design was built for;
- editing the stackup automatically updates every component that never overrode it;
- only explicit overrides are pinned.

This mirrors L0c's `TechRef = null` meaning *workspace default*. **The combobox must therefore include an
explicit `(Default)` entry at the top** — otherwise once a user picks a layer they can never get back to
automatic, which is the same trap the technology picker avoided with `(Workspace default)`.

**R-tec-9. A named layer that no longer exists reports and falls back to the default.** Renames and
technology changes will produce this; failing the component is the wrong response.

### 3.2 Rendering — the one real implementation question

The owner's suggestion of a combobox is right, and it needs a **dynamic** enumeration: the options come from
the *resolved technology*, not from a fixed list compiled into the component.

**R-tec-10. Determine whether the parameter system supports a dynamically-sourced choice parameter, and
report.** Static enums plainly exist — MBend's `Miter` (`None`/`Fifty`/`Optimal`) and `SmoothSteps` are
evidence. A choice list populated at edit time from the technology may not.

- If the plumbing exists, use it.
- If it does not, **say so before building a parallel mechanism** — a one-off dropdown wired only into these
  six components is the kind of thing that becomes three incompatible mechanisms later. A validated text
  field plus a picker is an acceptable interim, and it should be labelled as interim.

Only **conductor** layers appear in the list. Ground should arguably list only conductors with
`IsGroundReference` set — decide, and say which, because listing all conductors invites a selection the model
will then have to reject.

## 4. Guardrails

- Do not add an explicit *signal-layer default* field to `Technology` in this pass. The inferred rule
  (topmost conductor) stays; §3's per-instance override is what gives users control. An explicit technology
  default is a behaviour change for existing designs and deserves its own decision.
- Do not propagate `DefaultDisplayUnit` to existing documents (R-tec-4).
- Do not change how defaults are inferred, only how they are overridden.
- Do not display the new parameters on the schematic by default (R-tec-6).
- Don't touch `src/Core`'s model evaluation beyond reading the new parameters.

## 5. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Ground checkbox (R-tec-1)** — settable on conductor rows, absent on dielectric and via rows, round-trips
   through `.ctech` with no `FormatVersion` change, and takes effect in component resolution.
3. **Validation (R-tec-2)** — a stackup with no `IsGroundReference` conductor is reported; one with two is
   not.
4. **Display unit is a seed (R-tec-3/4)** — changing it alters the unit of a **newly created** layout and
   leaves open and existing layouts untouched, including after save and reload.
5. **Audit reported (R-tec-5)** — the completion note states whether any stored value was found expressed in
   display units, MKlopf's `W1`/`W2` entry specifically. If one was, it is fixed and covered by a test that a
   unit change leaves the stored value unchanged.
6. **Parameters exist (R-tec-6)** — all six microstrip components expose `SignalLayer` and `GroundReference`
   with `Display on Schematic = false`; a schematic renders no new annotation.
7. **Defaults still work (R-tec-8)** — with both unset, behaviour is **identical** to before this change.
   That is the regression guard for the whole section.
8. **Override works** — setting `SignalLayer` to an inner conductor changes the resolved `h` and `εr`, and
   therefore `Z₀`, in the expected direction.
9. **`(Default)` returns to automatic** — selecting it clears the override, and a later stackup edit is
   picked up again.
10. **Missing layer (R-tec-9)** — renaming a layer that a component references reports and falls back; the
    component still stamps.
11. **Options come from the technology** — the combobox lists that technology's conductors, and a component
    in a design using a different technology lists that one's.

## 6. On completion

Record in `src/Ui/CLAUDE.md`: that **`IsGroundReference` existed but was unreachable**; that
**`DefaultDisplayUnit` is a seed, with L0c's no-re-seed invariant restated** so nobody later "improves" it
into a live binding; **the result of R-tec-5's audit**; that signal/ground are stored as **layer name
strings**, matching `SpanFromLayer`/`SpanToLayer`, with **empty meaning follow-the-technology** and
`(Default)` as the way back; and **the answer to R-tec-10** — whether dynamic choice parameters already
existed or an interim mechanism was used.
