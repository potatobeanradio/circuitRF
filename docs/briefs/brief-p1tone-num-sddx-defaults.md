# Sonnet Brief — P1Tone Num parameter + s-param port behavior + SDDX default params

Three changes spanning Core (model/factory/elaboration), Engine (s-param port discovery), the UI
component registry, and placement auto-numbering. Build 0W/0E (TreatWarningsAsErrors) after each
part. The symbol redraws and rendering fixes are in the sibling brief
`brief-component-library-rendering.md` — do NOT touch symbol geometry here.

Files:
- `src/Ui/Schematic/ComponentTypeRegistry.cs` — P1Tone gains `Num`; SDDX default params (Parts 1,3).
- `src/Ui/ViewModels/SchematicViewModel.cs` — auto-assign P1Tone `Num` at placement & type-change (Part 1).
- `src/Engine/SParameterEngine.cs` — discover P1Tone instances as s-param ports (Part 2).
- `src/Core/Devices/P1ToneModel.cs` — expose port nodes / Z0 for s-param read-back (Part 2).
- `src/Core/Devices/ComponentModelFactory.cs` — pass `Num` through to the model (Part 1/2).
- `docs/design/p1tone-harmonic-terminations.md` — document the Num + s-param behavior (Part 4).

Key facts already true (do not re-derive):
- `P1ToneModel.Stamp` already has an S-param branch: when `_fc <= 0` (S-param mode, the default
  before `SetToneContext`), it stamps `StampZPort(mna, nExt, nRef, GetZ(omega))` with no drive
  branch. `GetZ` in S-param mode returns `GetDeclaredZ(1)` = `_harmonicZ[1]` if declared, else
  `_zDefault` (the scalar `Z` parameter). **So P1Tone already stamps its `Z[1]`≡`Z` impedance as a
  port load in S-param mode** — per the doc, `Z[1]` defaults to the internal reference `Z`. The
  user's "ALWAYS uses its Z[1] parameter to stamp the MNA in s-param" is already satisfied by the
  model; this brief does NOT change the stamping math.
- P1Tone's node layout: `Nodes[0]=nExt` (external/DUT-facing), `Nodes[1]=nRef` (reference),
  `Nodes[2]=internal drive node` (UNUSED in S-param mode — no drive branch is stamped when `_fc<=0`).
- The S-param engine discovers ports ONLY from `PortModel`/`TermModel` (`CollectPortsAndBranchLabels`
  in `SParameterEngine.cs`), reading `Num` and `Z`. P1Tone is `ModelKind.Linear` and is currently
  stamped as an ordinary component but never enrolled as a port — that's the gap Part 2 closes.

---

## PART 1 — Add a `Num` parameter to P1Tone, auto-assigned like Term/Pin

### 1a. Registry default (`ComponentTypeRegistry.DefaultParameters`)
P1Tone currently returns `[Pavl, Z, Freq, Phase]`. Add `Num` as the FIRST param, mirroring Term's
placeholder convention (Term emits `Num="1"` and `CommitPlacement` overwrites it with the next free
integer):
```csharp
case SymbolKind.P1Tone: return [
    new("Num",   "1",  "",    true,  UnitDimension.None),   // s-param port index; auto-assigned at placement
    new("Pavl",  "0",  "dBm", true,  UnitDimension.Power),
    new("Z",    "50",  "Ω",   true,  UnitDimension.Resistance),
    new("Freq",  "1",  "GHz", true,  UnitDimension.Frequency),
    new("Phase", "0",  "deg", false, UnitDimension.Angle)];
```

### 1b. Auto-assign at placement & type-change (`SchematicViewModel.cs`)
Term and Pin get their next-free `Num` at placement-commit and on type-change via
`NextFreeTermNum(EditModel)` / `NextFreePinNum(EditModel)`. The design intent (s-param correctness):
**a P1Tone used in lieu of a Term must share the Term port-number space** so the two never collide as
s-param ports. So P1Tone should draw from the SAME free-number pool as Term.

Find `NextFreeTermNum` (scans existing components' "Num" param among Term instances and returns the
lowest free positive int). Change it to scan BOTH `Term` AND `P1Tone` instances (any component whose
`Symbol` is `Term` or `P1Tone`, reading its `Num` param). Rename is optional, but the simplest is to
make `NextFreeTermNum` consider both kinds, since they share the port space. Then:

1. In the placement-commit path (where Term gets `np.Expression = NextFreeTermNum(EditModel).ToString()`),
   add a `SymbolKind.P1Tone` case that does the same:
   ```csharp
   else if (kind == SymbolKind.P1Tone)
   {
       var np = newComp.Parameters.FirstOrDefault(p => p.Name == "Num");
       if (np is not null) np.Expression = NextFreeTermNum(EditModel).ToString();
   }
   ```
2. In the type-change path (`CommitInlineEdit` → `InlineEditKind.ComponentType`, where the existing
   code special-cases `Term` and `Pin`), add the identical `SymbolKind.P1Tone` branch using
   `NextFreeTermNum`.

> Sharing the Term pool means: place Term#1, then a P1Tone → P1Tone gets Num=2 (not 1). This is
> exactly what's needed so a 2-port s-param sim with one Term + one P1Tone sees ports 1 and 2. If you
> find `NextFreeTermNum` is private/static taking the model, just widen its instance filter; keep one
> shared implementation, don't add a parallel `NextFreeP1ToneNum`.

### 1c. Factory passes `Num` through (`ComponentModelFactory.CreateP1ToneModel`)
The model needs `Num` available so the engine can read the port number. Two options — use (i):
- (i) **Read `Num` from the elaborated parameters in the engine** (Part 2) directly off
  `ElaboratedComponent.Parameters["Num"]`, exactly as `SParameterEngine.GetPortNum` already does for
  Term/Port. Then the factory/model need NOT carry Num at all. This is the least invasive and matches
  how Term works (Term's Num is read from `ec.Parameters`, not from `TermModel`). **Prefer this.**

So Part 1c is effectively: ensure `Num` (and `Z`) survive elaboration into
`ElaboratedComponent.Parameters` for a P1Tone. They will, since they're ordinary resolved params.
Verify the elaborator doesn't strip `Num` for P1Tone (it doesn't special-case it). No factory change
needed for Num.

---

## PART 2 — Discover P1Tone instances as s-parameter ports

**Goal:** a top-level P1Tone participates in an S-parameter sim exactly like a Term — it defines a
port at its `Num`, with reference impedance `Z` (= its `Z[1]`), and the engine reads its port
voltage/current to fill the S column. The model already stamps the right impedance in S-param mode;
the engine must now (a) enroll the P1Tone as a `PortEntry`, and (b) read its port quantities.

### 2a. The wrinkle — P1Tone stamps a Z-port branch, not a 0 V source
`PortModel`/`TermModel` stamp a 0 V branch and the engine reads `LastBranchIndex`:
- **Legacy path** (any port Re(Z0) ≤ 0): drives unit voltage on that branch, reads branch currents
  → Y-matrix. Requires a branch index.
- **Wave path** (all ports Re(Z0) > 0, the common case): ports stamp a conductance `1/Z0` between
  their nodes; excitation is a Norton current injection; S read from port voltages via Kurokawa. Uses
  `Node0/Node1` + `Z0`, NOT a branch.

P1Tone in S-param mode stamps a **Z-port branch** between `nExt` and `nRef` (its `GetZ(omega)`), which
is its own series impedance — NOT a clean port reference. For the engine to treat a P1Tone as a port
identical to a Term, the cleanest approach is:

**Skip the P1Tone's own S-param stamp and let the engine stamp it as a standard port** (conductance in
the wave path / 0 V branch in the legacy path), reading back exactly like a Term. The port reference
impedance is the P1Tone's `Z` parameter. Concretely:

1. In `P1ToneModel.Stamp`, S-param mode currently stamps a Z-port. Add a guard so that in S-param mode
   the model stamps **nothing** when the engine will stamp it as a port (mirror how `StampAll` skips
   `PortModel`/`TermModel`). Simplest: give `P1ToneModel` a public flag or detect via the engine
   skipping it (preferred — keep the model passive in the engine, see step 2). Do not delete the
   existing S-param Z-port stamp wholesale; instead the ENGINE will skip calling P1Tone's `Stamp`
   during S-param assembly (like it skips Term/Port), and stamp the port itself. That keeps HB
   behavior (which DOES want the Z-port + drive) completely untouched — the skip is S-param-only.

2. In `SParameterEngine`:
   - `StampAll(..., skipPorts)`: extend the skip predicate. Where it currently does
     `if ((ec.Model is PortModel or TermModel) && ec.InstancePath.Contains('.')) continue;` and
     `if (skipPorts && (ec.Model is PortModel or TermModel)) continue;`, add `P1ToneModel` to BOTH
     conditions (buried P1Tone inert like buried Term; wave-path skip like Term). Introduce a local
     helper `static bool IsSParamPort(ComponentModel m) => m is PortModel or TermModel or P1ToneModel;`
     and use it everywhere the two are currently matched.
   - `CollectPortsAndBranchLabels`: in the preliminary stamp pass, P1Tone is now skipped from the
     normal stamp (it's a port). For the **legacy path** a port needs a branch — so in the prelim
     pass, when the component is a P1Tone top-level port, stamp a Term-style 0 V branch to capture its
     `LastBranchIndex`. Easiest: add a `LastBranchIndex` to `P1ToneModel` and a dedicated
     `StampAsSParamPort(mna, c)` method that stamps the 0 V branch (identical to TermModel.Stamp), and
     call THAT in both the prelim pass and the legacy-path `StampAll` for P1Tone ports. For the wave
     path, P1Tone needs only `Node0/Node1` + `Z0` (no branch) — same as Term, which the wave path gets
     from `ec.Nodes[0]`, `ec.Nodes[1]`.
   - The `ports.Add(...)` loop: add a branch for `P1ToneModel` alongside `PortModel`/`TermModel`:
     ```csharp
     else if (ec.Model is P1ToneModel p1)
         ports.Add(new PortEntry(GetPortNum(ec), GetZ0(ec), p1.LastBranchIndex, ec.Nodes[0], ec.Nodes[1]));
     ```
   - `GetPortNum(ec)` already reads `ec.Parameters["Num"]` — works for P1Tone once Num exists (Part 1).
   - `GetZ0(ec)` already reads `ec.Parameters["Z"]` and falls back to 50 Ω — works for P1Tone (its
     reference impedance param is named `Z`, same as Term). ✓ This is the `Z[1]` the user means.

3. **Wave-path port stamping** (`StampPortConductances`): it stamps `1/Z0` between `Node0/Node1` for
   each port — works unchanged for P1Tone ports (they're now in the `ports` list with the right
   nodes + Z0). The P1Tone's internal drive node (`Nodes[2]`) is irrelevant in S-param mode (no drive
   stamped, model skipped), so it floats — add it to gmin/regularization coverage only if a singular-
   matrix warning appears; the existing `ApplyRegularization` adds gmin from every non-ground node to
   ground, so a floating `Nodes[2]` is already covered on the retry path. Verify no spurious singular
   matrix in the smoke test; if it appears, that's the cause.

### 2b. Node-count caution
P1Tone elaborates with 3 nodes ([ext, ref, drv]). When the engine stamps it as a Term-style port it
uses only `Nodes[0]`/`Nodes[1]` — correct (ext is the signal node, ref is the reference). The drv node
is unused in S-param mode. Do NOT change elaboration/node assignment; just don't reference `Nodes[2]`
in the s-param port path.

### 2c. Mixed Term + P1Tone
Because Part 1 shares the Num pool, a sim with Term(Num=1) + P1Tone(Num=2) yields ports sorted by Num
→ [Term#1, P1Tone#2], a clean 2-port S result. Add a test for this.

**Part 2 tests** (`tests/Engine.Tests/Linear/`):
1. A 1-port network terminated by a single P1Tone (Num=1, Z=50): S11 matches the same network
   terminated by a Term(Num=1, Z=50) — P1Tone behaves identically to Term as an s-param port.
2. A 2-port: Term(Num=1) on port 1, P1Tone(Num=2) on port 2 → S matrix matches Term+Term reference.
3. Non-uniform Z0: P1Tone with Z=75 → its port Z0 in the `Z0` cube is 75 (reads `Z`).
4. Buried P1Tone (dotted InstancePath) is inert (not a port), like buried Term.

---

## PART 3 — SDDX default params: `I[x,0] = _vx/50` for x = 1..X

**Requirement:** a freshly-placed SDD should be functional (no errors) and demonstrate the port-
voltage access notation `_vx`. Default each port's current equation to `I[x,0] = _vx/50`.

**Current** (`ComponentTypeRegistry.DefaultParameters(Sdd, n)`): returns only
`[new("NumPorts", n, "", false, …)]`. The per-port current equations are user-authored, so a
just-placed SDD has no `I[...]` slots and errors when simulated.

**Change:** emit `NumPorts` (hidden) PLUS one shown `I[x,0]` slot per port:
```csharp
case SymbolKind.Sdd:
{
    int n = portCount >= 1 ? portCount : 2;
    var ps = new List<DefaultParam>(1 + n) { new("NumPorts", $"{n}", "", false, UnitDimension.None) };
    for (int x = 1; x <= n; x++)
        ps.Add(new($"I[{x},0]", $"_v{x}/50", "", true, UnitDimension.None));
    return ps;
}
```
Notes:
- `_vx` is the SDD port-voltage notation (port x voltage); `_v1`, `_v2`, … The expression engine /
  SddEvaluator already understands `_vN` (it's how SDD equations reference port voltages). The
  `I[x,0]` two-index form (w=0 = current) is exactly what `ComponentModelFactory.CreateSddModel`
  parses (`RxCurrentEq` = `^I\[(\d+),(\d+)\]$`), binding to `currentAst[x-1]`.
- These are SHOWN on the schematic (`ShowOnSchematic: true`) so the user sees the `_vx` notation —
  per the request ("shows Users the notation for accessing the port voltages").
- This makes a placed N-port SDD a set of N linear `1/50 Ω` conductances to its own port voltage —
  functional, non-singular, no parse/eval error.

**Caution — the variadic-rebuild paths.** When the user changes an SDD's port count (NumPorts edit,
or type-change to `SDD{N}`), the default-parameter template is re-fetched. Verify those paths regen
the `I[x,0]` slots for the new N (they call `DefaultParameters(Sdd, n)`). If a port-count change path
preserves user-authored equations, make sure newly-added ports get the `_vx/50` default and removed
ports' slots are dropped — match whatever the ZPort `Z[i,j]` regen already does (ZPort regenerates
its full matrix from `DefaultParameters`). Keep SDD consistent with that pattern.

**Part 3 tests:**
- `DefaultParameters(Sdd, 3)` returns `NumPorts=3` plus `I[1,0]=_v1/50`, `I[2,0]=_v2/50`,
  `I[3,0]=_v3/50`, all shown.
- Elaborate + run a DC/S-param on a freshly-placed 2-port SDD (no user edits): no exception, no
  parse error; the SDD stamps as two 1/50 conductances. (Extend `SddModelTests` /
  `SddSingleIndexHbTests` style.)

---

## PART 4 — Documentation

Update `docs/design/p1tone-harmonic-terminations.md`:
1. Add a section **"S-parameter use (P1Tone as a Term)"** stating: a top-level P1Tone carries a `Num`
   parameter (auto-assigned at placement from the same free-number pool as Term, so Term and P1Tone
   never collide as s-param ports); in an S-parameter analysis the P1Tone defines a port at `Num` with
   reference impedance `Z` (= its `Z[1]` band-1 impedance), and the engine reads it exactly like a
   Term — the drive branch and harmonic-band Z-port (HB mode) are not stamped in S-param mode. State
   that `Z[1]` ≡ the scalar `Z` parameter (the existing §1 default), so "stamps Z[1] in s-param" and
   "reference impedance is Z" are the same thing.
2. Note the SDDX default `I[x,0] = _vx/50` only if you also touch an SDD doc — otherwise leave SDD
   docs alone (the SDD default is a UI/registry default, not an HB-termination concept). A one-line
   mention in `src/Ui/Schematic/CLAUDE.md` (create if absent) that SDD placement seeds `I[x,0]=_vx/50`
   per port is sufficient.

## Gate
Build 0W/0E (TreatWarningsAsErrors). New + existing tests green. Verify on disk: placing a P1Tone
auto-assigns the next free Num shared with Terms; a P1Tone substitutes for a Term in a 1-port and
2-port s-param sim with matching S; a freshly-placed N-port SDD runs without error and shows
`I[x,0]=_vx/50` on the schematic.

**STOP-and-report checkpoints** (don't guess — report what you find before finalizing):
- The exact name/signature of `NextFreeTermNum` and the placement-commit site that assigns Term's Num
  (so the P1Tone hook lands in the right method).
- Whether `P1ToneModel` needs a `LastBranchIndex` + `StampAsSParamPort` for the legacy s-param path,
  or whether the wave path alone suffices for the test circuits (report which path the tests exercise).
