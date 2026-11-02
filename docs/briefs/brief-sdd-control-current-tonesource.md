# Brief #5 (SDD control currents): add V_1Tone / V_nTone to the referenceable set

Design ref: `sdd-control-current.md` §2/§7 (referenceable kinds), `sdd.md` §8.2 (the table). Depends on briefs
#1–#4 (all landed). This brief makes **tone voltage sources** (`V_1Tone` and `V_nTone` — one model,
`ToneSourceModel`) referenceable as control currents, alongside the existing five kinds (Vdc, IProbe, L, SnP,
ZnP). **P1Tone is deliberately NOT included** — see "Out of scope" below.

Rationale: `ToneSourceModel` is a Group-2 branch-current element — its `Stamp` calls `mna.AddBranch()` and
pins `Va − Vb = E(ω)`, structurally identical to `VdcModel`. A tone source *is* an independent voltage source;
its branch current is a first-class solved unknown. The original exclusion just followed VendorA's "independent
voltage sources" list literally. There is no architectural reason to exclude it. Because `V_1Tone` and
`V_nTone` share one model, both are covered by one change — `V_nTone` is free.

Core + Engine. Build **0W/0E**. Every existing test stays green (additive — no circuit that doesn't reference a
tone source changes behavior).

---

## 1. Expose `LastBranchIndex` on `ToneSourceModel` (the one real gap)

`VdcModel`/`InductorModel`/`IProbeModel` each expose a public `LastBranchIndex` set in `Stamp`. `ToneSourceModel`
does **not** — that's the only thing the resolvers lack. Add it, mirroring the others exactly.

In `src/Core/Devices/ToneSourceModel.cs`, the `Stamp` method currently does:
```csharp
int br = mna.AddBranch();
mna.AddConstraint(br, va, new Complex(+1, 0));
mna.AddConstraint(br, vb, new Complex(-1, 0));
mna.AddBranchCurrent(br, va, vb);
```
Add a public auto-property and capture `br` into it:
```csharp
/// <summary>
/// Branch index for the tone source's current (set each Stamp). −1 before first stamp.
/// Mirrors VdcModel.LastBranchIndex — exposes the source current as a referenceable
/// control-current branch (SDD C[n]=<toneSrc>).
/// </summary>
public int LastBranchIndex { get; private set; } = -1;
```
and in `Stamp`, replace `int br = mna.AddBranch();` with `LastBranchIndex = mna.AddBranch();` then use
`LastBranchIndex` in the three following stamps (or keep a local `int br = LastBranchIndex;` to minimize the
diff). Behavior is otherwise unchanged — the branch is allocated exactly as before, just recorded.

> Note the early return: `Stamp` begins `if (c.Nodes.Length < 2) return;` before allocating the branch. In that
> degenerate case `LastBranchIndex` stays −1, which the resolvers already treat as "not assigned" and error on
> cleanly. Fine as-is.

## 2. Add `ToneSourceModel` to the referenceable-kind set — THREE sites

The kind validation is duplicated (one per engine), all structurally identical. Add a `ToneSourceModel` case to
each, validating it as a **two-terminal / single-port** branch (same as Vdc — `Cport` absent or 1). Use the kind
label **"V_1Tone/V_nTone"** in messages. Update the allowed-kinds string in each site too.

**Site A — `src/Engine/NonlinearDcEngine.cs`, `GetControlBranchIndex`:**
```csharp
const string AllowedKinds = "Vdc, V_1Tone/V_nTone, IProbe, L (Inductor), SnP, Z_Port";
return target.Model switch
{
    VdcModel        vdc  => ValidateSinglePortBranch(sddName, n, port, vdc.LastBranchIndex,  "Vdc"),
    ToneSourceModel tone => ValidateSinglePortBranch(sddName, n, port, tone.LastBranchIndex, "V_1Tone/V_nTone"),
    IProbeModel   probe  => ValidateSinglePortBranch(sddName, n, port, probe.LastBranchIndex,"IProbe"),
    InductorModel ind    => ValidateSinglePortBranch(sddName, n, port, ind.LastBranchIndex,  "Inductor"),
    SnpModel      snp    => ValidateMultiPortBranch(sddName, n, port, snp.PortBranchIndices, "SnP"),
    ZPortModel    zp     => ValidateMultiPortBranch(sddName, n, port, zp.PortBranchIndices,  "Z_Port"),
    _ => throw new InvalidOperationException(
        $"SDD '{sddName}': C[{n}]={target.InstancePath} references a '{target.ComponentType}' " +
        $"which is not a referenceable device class. Allowed: {AllowedKinds}.")
};
```

**Site B — `src/Engine/HarmonicBalance/HbEngine.cs`, `GetControlBranchIndexHb`:** add the same
`ToneSourceModel tone => ValidateSinglePortBranchHb(sddName, n, port, tone.LastBranchIndex, "V_1Tone/V_nTone")`
case and update the `Allowed` string to include `V_1Tone/V_nTone`.

**Site C — `src/Engine/SParameterEngine.cs`, `ResolveSParamBranchIndex`:** add
`ToneSourceModel tone => tone.LastBranchIndex,` to the switch and add `V_1Tone/V_nTone` to the allowed-kinds
string in that method's error message.

> **S-param branch existence (Site C subtlety):** the resolver's `br < 0` check already errors if the referenced
> device allocated no branch in the S-param matrix. A tone source is **not** an S-param port (`IsSParamPort` is
> Port/Term/P1Tone only), so `StampAll` stamps it normally via `ec.Model.Stamp` in **both** wave and legacy
> paths → its branch exists and `LastBranchIndex` is set. No special-casing needed. (Tone sources stamp E=0 at
> the S-param drive frequencies, i.e. they act as a short / 0 V branch — exactly the right small-signal behavior
> for a quiet source, and the branch current is well-defined.)

## 3. (No factory change needed — but verify)

`ComponentModelFactory.CreateSddModel` parses `C[n]`/`Cport[n]` and only checks that each `_cn` has a `C[n]`;
it does **not** validate the referenced kind (that's the resolvers' job, sites A–C). So no factory change is
required. **Verify** this is still true on disk — if any kind allow-list crept into the factory, add
`ToneSourceModel` there too. (As of brief #1 the factory stored raw instance names without kind-checking.)

## 4. Docs

`docs/design/sdd.md` §8.2 — add a row to the referenceable-devices table:
```
| Tone voltage source (`V_1Tone` / `V_nTone`) | no | source branch current |
```
Place it just after the `Vdc` row (they're the two independent-voltage-source kinds). No other doc text needs
to change — `_cn` semantics, sign convention, and §8.5 (which analyses honor `_cn`) are identical for a tone
source. Also update `sdd-control-current.md` §2/§7 referenceable lists to include V_1Tone/V_nTone (one phrase
each).

## 5. Tests (`tests/Engine.Tests` + `tests/Core.Tests/Devices`)

- **Resolver (all three engines):** an SDD with `I[1,0]=_c1`, `C[1]=Vt1` referencing a `V_1Tone:Vt1` resolves
  to the tone source's branch index in DC, HB, and S-param. One test per engine (mirror the existing Vdc resolver
  tests).
- **DC read-through:** SDD with `I[1,0]=_c1`, `C[1]=Vt1` where the tone source carries a known DC current (give
  it a `Vdc=` term + a resistor) → `_c1` equals that current. (At DC the tone's AC part is inert; the branch
  current is the DC bias current — a clean check.)
- **HB mirror:** SDD `I[2,0]=beta*_c1`, `C[1]=Vt1`, with the tone source driving a load → the SDD's drain
  current spectrum mirrors `beta ×` the tone-source branch current spectrum (read the reference branch from the
  back-solver, as in the brief-#2 inductor-mirror test).
- **V_nTone:** one test referencing a `V_nTone` (multi-frequency) instance — confirms the shared-model path
  resolves identically. `Cport` absent/1 (two-terminal).
- **Cport rejection:** `C[1]=Vt1 Cport[1]=2` → the single-port validator errors (`V_1Tone/V_nTone` is
  two-terminal). Confirms the message names the kind.
- **Regression:** every existing control-current + S-param + HB test green; no tone-source reference → identical.

## Out of scope — P1Tone (with reason)

`P1Tone` is **not** added, by design. Unlike a tone source it is not a clean two-terminal branch source:
- It's a 3-node device (external, reference, internal `__drv` junction).
- In HB it stamps **two** branches — a V-drive branch (`brDrive`, nDrv→nRef) plus an internal Z_Port branch
  (nExt→nDrv) — with the source behind an internal reference impedance.
- Its public `LastBranchIndex` is set **only** by `StampAsSParamPort` (the legacy-path 0 V port branch), not by
  the HB drive branch. So "the current in P1Tone" is ambiguous (drive-branch current? delivered current through
  Z_Port? — they differ by the internal-impedance drop), and the HB drive-branch index isn't even exposed.

Supporting it would require **defining** which current `_cn` means and exposing that specific branch per engine
— a design decision plus non-trivial plumbing, not a free add. Left out until/unless there's a concrete need;
if so, it's its own brief that first pins the semantics.

## Gate
Build 0W/0E; all green; an SDD can reference a `V_1Tone`/`V_nTone` current via `C[n]` in DC, HB, and S-param,
validated identically to `Vdc`. The referenceable set is now {Vdc, V_1Tone/V_nTone, IProbe, L, SnP, ZnP}.

## Risk note
Very low risk — purely additive, one new branch-index property + three identical switch cases + a doc row. The
only thing to get right is using `LastBranchIndex` (now exposed) consistently and keeping the two-terminal
`Cport` validation (a tone source has no port selector). The existing Vdc resolver tests are the template for
all of it.
