# wBond — controlling parameters on the schematic symbol, and Carried-or-Linked

**Phase:** WB-G. Not on `wbond.md` §13's roadmap; it exists because the owner asked (2026-08-17) how a
placed wBond will be tuned, swept and optimised from the schematic once schematic-based tuning arrives,
and the answer exposed a second question — whether the netlist should carry a copy of the wires at all.

**Design authority:** `docs/design/wbond.md` **§5.5.1 (WB44, WB44a)** and **§9.7 (WB45)**, plus owner
decisions **O-10, O-11, O-12** in that document's decision table. Read those first. They settle every
question this brief would otherwise have to argue, and this document does not restate their reasoning.

**Predecessor:** WB-A…WB-F complete. The WB40 attachment move (`.wBond` from the cell root into
`layout/`, stem-paired) landed 2026-08-17 and is **already done** — see `src/Ui/RESOLVED.md`. Nothing
here depends on it beyond the path a `Linked` instance points at.

---

## 0. What this phase is, in one paragraph

A placed wBond exposes four parameters today: `Design`, `Arrays`, `SymbolPitch`, `RefPin`. None of them
is a physical quantity, so there is **no handle a `VAR`, a parametric sweep or an optimiser can turn**.
This phase adds the handles — loop height, wire diameter, wire material, operating temperature, the
ground-plane switch — and settles where a placed instance's wires actually come from.

**Most of the engine half already exists and is the thing to NOT rebuild.** Read §1 before writing any
code.

### 0.1 The measurement that sizes the phase

`ComponentModelFactory.CreateWBondModel` already accepts `Temp`, `GroundPlane`, `LoopHeight` and
`LoopHeight_<profile>`, and `ApplyLoopHeightOverrides` already regenerates every bound wire's polyline
so the inductance matrix is refilled from new geometry rather than scaled. There is a passing gate test
sweeping a `mil`-declared loop height from a placed component
(`WBondSchematicPlacementTests.M4_ASweptLoopHeightInMil_AgreesWithTheHandWrittenNetlist`, 1086.2 pH at
10 mil vs 2206.7 pH at 45 mil).

**The gap is on the schematic side only.** `ComponentTypeRegistry.DefaultParameters` declares none of
them for `SymbolKind.WBond`, and `ParameterEditorViewModel.AllowsAddParameter` is false for wBond — so
the user cannot select these parameters and cannot type them in either. The engine would honour them
today; nothing offers them.

---

## 1. Do not rebuild these

- **`ApplyLoopHeightOverrides` is the pattern.** It mutates the *decoded* design on its way to the
  solver and never writes back to `Design`. That is WB44's property 1, already built — a sweep
  re-elaborates N times and mutates the stored design zero times. Extend it; do not replace it.
- **Absent-means-as-drawn is already correct** (`if (height is null) continue;`). Preserve it exactly.
  WB44 property 2 is not a new behaviour, it is a behaviour that must survive being declared.
- **Both wire sources already exist.** `CreateWBondModel` takes `Design` (base64) *or* `File` (a path),
  with `Design` winning where both are present. WB45 is a decision about which the elaborator *emits*,
  not a new capability.
- **Length units work.** A length-dimensioned global could not be swept before 2026-08-07 — the table
  had no symbol for the metre, `"m"` being the SI prefix *milli* — and it failed **silently**, clamping
  a loop-height sweep to the wire's own foot drop and drawing a plausible flat curve. `Units.cs` now
  carries a distinct `"metre"` base symbol with `mil`/`in`/`inch` mapped to it. **Do not re-diagnose
  this**; if a length sweep looks flat, it is this phase's bug, not the units table's.

---

## 2. M1 — declare the controlling parameters

`ComponentTypeRegistry.DefaultParameters` gains, for `SymbolKind.WBond`, the parameters below. Every one
is **optional and unset by default** — see §2.2, which is the part that can silently break every
existing design if got wrong.

| parameter | dimension | scope | applies to |
|---|---|---|---|
| `LoopHeight` | Length | all arrays | the bound `LoopProfile`'s `LoopHeightNm`, then regenerate |
| `LoopHeight_<array>` | Length | one array | as above, for that array only |
| `Diameter` | Length | all arrays | every `Wire.DiameterNm` |
| `Diameter_<array>` | Length | one array | that array's wires |
| `Material` | (name) | all arrays | every `Wire.Material` |
| `Material_<array>` | (name) | one array | that array's wires |
| `Temp` | Temperature | design | `WBondDesign.OperatingTempC` — **already honoured**, just undeclared |
| `GroundPlane` | (bool) | design | `GroundPlane.Enabled` — **already honoured**, just undeclared |

**`Span` is NOT in this table and is not this phase** (WB44a / O-11). It is not a profile property but
the pad positions; it scales by *factor* not to a value, and it moves a bonded foot off its pad. If it
is wanted later it needs a pinned-foot rule and §8 envelope reporting, neither of which exists.

**The source makes no difference here.** A controlling parameter is applied to the **decoded** design and
cannot tell whether it arrived as a carried payload or from a linked file (§3). Do not branch on it.

### 2.0 Precedence against a layout edit — decide this before writing the override

*(Found 2026-08-17 answering an owner question; not previously specified anywhere.)*

`ApplyLoopHeightOverrides` regenerates a wire **between its own existing feet**, and skips any wire whose
`ProfileBinding` is null. Combined with WB2/WB24 — an individually-dragged wire **detaches** from its
profile — that yields three different precedences under one parameter:

| what the user edited in layout | with `LoopHeight_G1` also set on the schematic |
|---|---|
| moved a foot (XY or z) | **layout wins** — the override regenerates between the existing feet and never moves them |
| dragged the loop of a **bound** wire | **schematic wins** — the wire is regenerated and the layout loop-height edit is overwritten at solve time |
| dragged the loop of a **detached** wire | **layout wins** — the override silently does not touch it |

The first row is right and needs nothing. **Rows two and three are the problem**: two wires in the same
array respond differently to the same parameter depending on whether someone once dragged one of them,
and both directions are silent. That produces "I changed it in the layout and it reverted" and "I changed
it in the schematic and nothing happened" from the same design.

**Do not resolve this by making the override touch detached wires** — that would break WB2, which is
load-bearing for the whole editor. Resolve it by **reporting**, in the WB30/WB35 house idiom:

> *"LoopHeight_G1 = 12 mil applied to 4 of 6 wires. 2 wires are detached from their profile and keep
> their drawn loop height. Re-bind them to the profile if the parameter should reach them."*

Count is per array, reported once per run, and the remedy is named. **Gate 3a below is this row.**

### 2.1 Scope is the ARRAY, and the existing profile spelling stays

O-10 settles the namespace: **array-scoped**, because array names *are* the pin names on the symbol and
a `LoopProfile` is an editor-internal sharing mechanism a schematic user never sees. Two consequences:

1. **Map array → profile inside the factory.** `LoopHeight_G1` must find G1's bound profile.
2. **A shared profile must be cloned on write.** Two arrays may bind the same `LoopProfile`; overriding
   one must not drag the other. Cloning is free here — the override is per-elaboration and never
   persisted — and skipping it produces a wrong answer that looks right.

`LoopHeight_<profile>` keeps working for hand-authored `.cnl` files. Resolve the array spelling first;
fall through to the profile spelling. **A name that is both an array and a profile resolves as the
array** and the collision is reported, because the schematic user's namespace is the one on the symbol.

### 2.2 The trap that must not be shipped

A wBond that ships with `LoopHeight = 20 mil` among its **defaults** silently regenerates every existing
design's wires to 20 mil on its next run. The parameters must be declarable-but-unset — the same shape
`AllowsAddParameter` gives P1Tone/ToneSource/ZPort/SDD/VAR today, which is why wBond joins that set
rather than getting a fixed row list.

**Gate:** an existing `.csch` with a placed wBond, opened and re-run after this phase, produces
**bit-identical** S-parameters. If it does not, a default leaked in.

### 2.3 The panel

`ParameterEditorViewModel.WBond.cs` already owns the wBond panel and already hides `Design`/`Arrays`.
Add the controlling parameters there rather than letting them fall to generic text rows:
- `Material` is an enumeration over `design.Materials` (Au/Al/Cu/Ag plus user-extensible) — a dropdown,
  not a text box.
- `LoopHeight`/`Diameter`/`Temp` are expression fields like any other, so a `VAR` reference is typable.
- The per-array rows are generated from the instance's own `Arrays` list, so they name G1/G2 rather than
  asking the user to spell a suffix.
- **Unset must be visibly distinct from zero.** An empty field means "as drawn"; `0` is an error
  (`ApplyLoopHeightOverrides` already refuses a non-positive height, and diameter needs the same).

---

## 3. M2 — Carried or Linked (WB45)

A placed wBond declares its wire source. `Linked` is the default whenever the instance resolves to a
workspace cell whose `layout/` owns a `.wBond`; `Carried` otherwise, and `Carried` remains the only
option for an imported, foreign or workspace-less design.

**`Carried`, not `Embedded`.** §9.1 already spends *embedded* and *referenced* on a different axis —
whether a `.wBond` file embeds the layout artwork it was drawn over or references cells by path. The two
axes are independent and must not share vocabulary; `Carried` is §5.0's own verb.

### 3.0 The lifecycle, which is the part that is easy to get wrong

A freshly placed wBond is `Carried` **by construction** — there is no cell and no file to link to. The
file is created by **Update Layout from Schematic** (§9.5, `WBondCellSeeding`), and **WB45a: that command
is where the instance flips to `Linked`, and it says so.** The flip must never happen as a side effect of
a later scan noticing the file exists — that would change which wires simulate with nothing on screen.

Concretely, `WBondCellSeeding.Seed` already returns `Created` / `KeptExisting`; the flip belongs on
`Created`, alongside the existing "wBond 'W1' → 'amp.wBond'" success line.

**A `Carried` instance whose cell already has a `.wBond` is a legitimate state, not an error** — it is
someone who deliberately kept the portable payload. Do not auto-convert it.

### 3.1 What has to be built

- **A `Source` control on the wBond panel**, stating the consequence where the choice is made — the
  same shape as the MKlopf Z1/Z2-vs-W1/W2 entry-mode toggle already in `ParameterEditorViewModel`.
- **Relative-path resolution for `File`.** The factory currently does a bare `File.Exists(path)` with no
  base directory, and wBond was *deliberately removed* from `ComponentTypeRegistry.IsFilePathParameter`
  when the payload landed. Both reverse for the `Linked` case. **An absolute path breaks on every other
  machine** — the stored value is relative to the schematic, exactly as §4 of
  `workspace-and-project-tree.md` resolves a cell reference.
- **A "Not Found" state for wires.** §5.0 wanted zero of these; `Linked` reintroduces one, and it must
  read like the cell-reference one the user already knows, naming the path that failed.

### 3.2 The consequence that must ship WITH it, not after it

Under `Linked`, the array-drift check (§9.2/WB35a) becomes **more** load-bearing, not less. Carried
drift is introduced by an explicit re-import. Linked drift arrives the moment someone reorders arrays in
the `.wBond` — pin order *is* array order, so every pin keeps its position while its name moves to a
different row, under an already-wired schematic. **The check runs at elaboration for a linked instance
and reports**, or linking is strictly more dangerous than carrying on that one axis.

The hidden `Arrays` parameter is what the check reads. It is already maintained by the array editor.

### 3.3 Explicitly not in scope

Retiring the `Design` payload. WB45 is *both*, chosen per instance. §5.0/WB17b still governs the case it
was written for.

---

## 4. Gates

1. **Nothing changes for an existing design.** A placed wBond with no controlling parameter set produces
   bit-identical S-parameters before and after the phase. (§2.2 — the one that catches a leaked default.)
2. **A loop-height sweep runs from a PLACED component, in `mil`, and moves.** Against the hand-written
   `.cnl` pair the existing M4 gate uses. Two distinct heights, two distinct inductances — a flat curve
   is the exact failure mode §1 warns about.
3. **A per-array override reaches one array only.** Two arrays, one shared `LoopProfile`,
   `LoopHeight_G1` set: G1's wires regenerate, G2's are untouched. Oracle is G2's wire z-coordinates,
   not a message.
3a. **A detached wire is skipped, and SAID to be skipped** (§2.0). One array, one wire dragged loose in
   the layout, `LoopHeight_G1` set: the bound wires regenerate, the detached one keeps its drawn
   geometry, and the run names the count. Oracle is both the z-coordinates and the message — the
   message alone would pass while the geometry was wrong, and the geometry alone is the silent
   behaviour this gate exists to prevent.
4. **Diameter and material change the answer in the right direction.** A thicker wire has lower
   inductance; aluminium has higher loss than gold at the same geometry. Cross-check R against
   `InternalImpedance` at a frequency where the R-tier is active, not just L.
5. **A sweep mutates nothing.** After a 5-point loop-height sweep the instance's `Design` payload (or
   the linked `.wBond` on disk) is byte-identical to what it was before.
6. **A linked instance survives a move of the schematic**, and refuses legibly when the `.wBond` is
   gone — path in the message.
7. **A linked instance whose `.wBond` had its arrays reordered is reported**, not silently re-pointed.
   Constructed reorder, not a real edit.
8. **`Firewall.Tests` green** — none of this may pull a UI reference into `Core`.

## 5. Questions for the owner, if they come up

- **Does `Material` on the schematic need to reach user-defined materials?** `WBondDesign.Materials` is
  extensible, but a schematic parameter is a string with no list to validate against until the design is
  decoded. Proposal: validate at elaboration and refuse by name, rather than restricting the dropdown to
  the built-in four.
- **Should `Linked` be offered when the `.wBond` is outside the workspace?** The default rule only
  covers the cell-resident case. Proposal: allow it, treat it as a Known File, and let §3.2's report
  carry the risk.

## 6. Completion note — what to record

House convention, to **`src/Ui/RESOLVED.md`** (not `CLAUDE.md`): what was built, **what was found**,
what was deliberately not built and why, the gate numbers, and an explicit "not interactively verified"
list. Specifically record: whether any existing design's answer moved (gate 1), the two inductances from
gate 2, and whether the shared-profile clone-on-write of §2.1 was needed in practice or was already
prevented by the data model.
