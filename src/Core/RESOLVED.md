# src/Core — resolved briefs (detail, off the CLAUDE.md growth path)

`src/Core/CLAUDE.md` was archived to `src/Core/HISTORY.md` once already (see that file's own note).
Going forward, a completed brief's detail lands here instead — one `##` section per brief, sparingly,
only for findings that are still true, still surprising, and would cost someone real time to
rediscover. Mirrors `src/Ui/DataDisplay/RESOLVED.md`'s own pattern.


## SRLC and PRLC, and the interface that let a Mutual reach them (2026-08-31)

Two lumped parts — `SeriesRlcModel` (`SRLC`) and `ParallelRlcModel` (`PRLC`), both linear,
two-terminal, R/L/C parameters, defaults 1 Ω / 1 nH / 1 pF. The owner's brief said `C = 1 pH`; pH is
an inductance, so it was read as 1 pF (also the standalone capacitor's own default) and confirmed
before building.

**1. SRLC is arithmetically the same branch `InductorModel` already stamps for optional `R=` plus
`C=`, and it is a separate model anyway.** The shared arithmetic is deliberate and is pinned by a
test that runs both and requires bit-identical S-parameters. What is NOT shared is everything a user
sees: a series-RLC glyph rather than a coil, three required and shown parameters rather than one
required and two optional, and a netlist line reading `SRLC:` rather than `L:`. The commonest reason
to place one is a ceramic capacitor whose vendor states an ESR and an ESL — a part that *is* a series
RLC, and which as three wired elements is three instance names and a schematic that no longer matches
the bill of materials. Folding it into `L` would have saved a file and cost the whole point.

**2. PRLC's inductor cannot be an admittance, and that is what makes a Mutual possible.** R and C go
in through `AddAdmittance`; L takes its own Group-2 branch, because `1/(jωL)` diverges as ω→0 and
there is no Group-1 form of an ideal inductor at DC. The branch constraint `V_a − V_b − jωL·i = 0`
degenerates cleanly to `V_a − V_b = 0` at DC — the exact short, no gmin fudge. The side benefit is
the one the brief asked for: that branch carries a bare `−jωL` diagonal with no R or C mixed in, so a
mutual's `−jωM` off-diagonal lands on exactly the term it means to.

**3. Six sites pattern-matched `InductorModel` by TYPE, and a pattern match was the wrong shape.**
`IInductiveBranch` (one member, `LastBranchIndex`) now carries the contract, and
`MutualInductanceModel`, `SParameterEngine`'s regularization and SDD control-current resolution,
`HbLinearExtractor`'s regularization and branch labelling, and both engines' SDD validation read it
instead. The failure mode a type check leaves behind has no error message: inductance regularization
that silently skips a branch is not a warning, it is a singular matrix reported somewhere else
entirely, and an SDD reference that "is not a referenceable device class" for a part that obviously
carries a current reads as a bug in the SDD. Two follow-on repairs came with it — the Mutual's
refusal now names the kinds that DO work instead of an internal class name (`is not an
InductorModel` gave a user who had pointed a Mutual at a resistor nothing to act on), and
`HbLinearExtractor`'s branch labels now name the component TYPE rather than always saying `L`, so a
diagnostic about an SRLC does not send the reader hunting for an inductor the schematic never had.

**4. The oracle is a closed-form impedance, never a second circuitRF path.** `SeriesParallelRlcTests`
computes each element's own `Z(ω)` by hand and turns it into S11 with the one-port reflection
formula; the mutual cases build the coupled 2×2 Z (SRLC) or invert the coupled inductive block and
add the shunt admittance (PRLC). Comparing an SRLC against three wired elements would have been
cheaper to write and much weaker — the two paths share the whole engine underneath, so a sign error
in a constraint diagonal would have agreed with itself.

---

## The ideal mixer: what it can be, and why S-parameters say nothing (2026-08-31)

`MixerModel` (`Mixer`), placed by two schematic tiles — `SymbolKind.Mixer` (3 pins, ground-referenced)
and `SymbolKind.MixerD` (6 pins, differential) — over ONE engine component, the TermG pattern.

**1. The mixing law had to be a PRODUCT, and that was not a preference.** `Evaluate` is memoryless in
the port voltages: no time argument, no internal oscillator, no way to put a local oscillator inside
the device. So the LO must arrive through a port, and the only ideal mixing law expressible that way
is `v_if(open) = K·v_rf·v_lo`. The alternative worth wanting is a **commutating** mixer — the LO
switching rather than scaling, which is why a real diode mixer's conversion loss barely moves once
the LO is hard enough to switch, and why its conversion is `2/π` regardless of drive. That needs
`sgn`, whose derivative is a delta function, and no Newton step survives it. A `tanh` approximation
of the switch is expressible but changes the gain calibration (the fundamental of `B·tanh(cos θ)` is
not `B`), so it was left out rather than shipped miscalibrated.

The honest cost is stated everywhere it matters: **conversion gain tracks LO amplitude**, +3 dB for
+3 dB. That is why `ConvGain` is meaningless without `Plo`, why the two are the only parameters shown
on the schematic, and why they are quoted together in the docs the way a datasheet quotes them.

**2. The user never types the multiplier constant, and one closed form connects the two ends.** With
every port matched, `G = P_if/P_rf = K²·B²·Zrf/(16·Zif)` — two factors of two, one from the
product-to-sum identity and one from the `Zif`/load divider — so `K = (4/B)·√(G·Zif/Zrf)` at
`B = √(2·Plo·Zlo)`. The RF amplitude is absent from it, which is what makes the result a *gain*.
Nothing but a test connects the dB the user types to the volt⁻¹ the model runs on, so
`MixerModelTests.ConvGain_IsWhatComesOutOfTheStatedGain` does the arithmetic independently rather
than reading a number back out of the model, and `MixerHbTests` closes it end to end through the real
two-tone solver (−20 dBm in at −7 dB reads −27.000 dBm out).

**3. An S-parameter run of a mixer reports NO conversion, and that is an answer, not a gap.** It
looks exactly like a missing feature — sweep a mixer, read S21 = 0, conclude the device was never
stamped — so it is worth knowing why. The linear engines route a nonlinear device through
`ComponentModel.StampLinearized`, which linearises at the DC operating point; the mixer's RF-to-IF
small-signal gain is `∂i_if/∂v_rf = −K·v_lo/Zif`, and `v_lo` at DC is zero. S-parameters are a
single-frequency measurement and conversion is the business of moving energy *between* frequencies,
so there is nothing there for them to report. What they DO report is real and worth having: the port
matches, and the three leakage paths. `MixerSParamTests` pins both halves — including a detuned
`Zrf` reading Γ = −1/3 exactly, which is what proves the device is in the matrix rather than absent
from it. (A side effect worth knowing: give the LO port a DC bias and the same device becomes a
linear amplifier in S-parameters, because a multiplier driven by a constant is exactly that.)

**4. The non-idealities are OFF at a large number, and "off" is snapped to EXACTLY zero.** The
expression engine has no `inf`, so the three isolations default to 200 dB and `IIP3` to 100 dBm.
Those are honest numbers, not sentinels — but a leakage coefficient of 1e-10 is not the same thing as
an absent one: `StampLinearized` skips a zero admittance (`if (y == Complex.Zero) continue`), so the
model snaps anything above `IsolationOff` (150 dB) / `CompressionOff` (90 dBm) to a hard zero and the
ideal mixer stamps no off-diagonal terms at all.

**5. The compression limiter is `tanh`, not the textbook `a₁x − a₃x³`.** A bare cubic turns over and
goes NEGATIVE past its peak; Newton then finds that root and the run converges cleanly onto a wrong
answer — the expensive failure mode. `tanh` is monotone and bounded everywhere and has the same
third-order term, so matching it to `IIP3 = √(4a₁/3a₃)` gives `IIP3 = 2·Vsat` exactly.

**6. Leakage is deliberately one-directional** (LO→RF, LO→IF, RF→IF and nothing back). It cannot form
a loop the solver has to break, and each isolation names one path — getting two of them the same way
round is the kind of mistake that survives every other test, so `MixerSParamTests` asserts the
destination port of each.

**7. The single-ended tile mints its own ground returns at extraction**, in `NetExtractor`, the same
way `TermG` does with Term's port 2 — three pins in, six nets out, `[rf+, 0, lo+, 0, if+, 0]`. The
engine never learns there are two tiles. A wrong net count from a hand-written netlist is refused by
name in `Elaborator.ResolveParameters`, because without that it is an index-out-of-range thrown from
inside a Newton iteration, where nothing left on the stack can say which instance was wrong.


## The two current sources: ITone and the VCCS (2026-08-29)

`CurrentToneSourceModel` (`I_1Tone`/`I_nTone`) and `VccsModel` (`VCCS`). Five things worth keeping.

**1. The two sources point in OPPOSITE directions, deliberately, and each glyph says which.**

- **ITone delivers into pin 1** (arrow up). It is an independent source and calls
  `AddCurrentInjection`, whose convention is fixed in `src/Engine/CLAUDE.md` → "Current-source
  direction": `J` injects into its FIRST node. That is the opposite of the SPICE `I` element.
- **The VCCS draws current out of `out+` and delivers it at `out−`** (arrow down, owner's call,
  2026-08-29). It never calls `AddCurrentInjection` — it stamps admittance — so the injection
  convention does not reach it, and it is drawn the way a small-signal transconductance is drawn in
  every device model. This IS the SPICE `G` element's direction, so a VCCS across a grounded load is
  inverting.

Neither direction is discoverable from a result: the sign flips and everything still solves,
converges and plots. So both are pinned by SIGNED assertions rather than magnitudes — the S-param
gate asserts **S21 = −0.25**, the DC gate **V = −2 V**, and one glyph test asserts the two arrowheads
point opposite ways so a later "make them consistent" tidy-up cannot silently flip one alone.

**2. The tone machinery is now a base class, and engine code must test the BASE.** `ToneSourceModelBase`
holds the tone table, the DC offset, the sweep-time re-evaluation of amplitude/phase expressions and the
zero-Hz warning; the two leaves differ only in `Stamp` (Group 2 branch constraint vs Group 1 RHS
injection). Every engine site that asks "is this a tone source" — the two commensurability checks,
`UpdateSweepPoint`'s re-evaluation, `HbLinearExtractor`'s drive-zeroing — was re-pointed at the base.
A check left naming only `ToneSourceModel` would let a current tone source sit off the mixing grid, or
skip its `ReevaluateFromGlobals`, and both failures are silent.

**3. A current source is NOT an SDD control-current reference, and the generic refusal was the wrong
message.** It allocates no branch: its current is an input, not a solved unknown. All three resolvers
(DC, HB, S-param) therefore gained an explicit arm naming it, rather than letting it fall through to
"allowed: Vdc, V_1Tone/V_nTone, IProbe, L, SnP, Z_Port" — from which the obvious inference, "but the
other tone source is on the list", is exactly wrong. The arm points at the remedy: a series `IProbe`.

**4. The VCCS's control rows must stay EMPTY, and that is the only thing making it ideal.** The stamp
is four entries (`Y[out+,c+] += G`, `Y[out+,c−] −= G`, `Y[out−,c+] −= G`, `Y[out−,c−] += G`), all in
the two OUTPUT rows against the two CONTROL columns. A control-row entry would
draw current through the sense pair, and the resulting error is a few percent on a divider — small
enough to pass any tolerance set from the output voltage alone. `Vccs_ControlPairDrawsNoCurrent_…`
therefore asserts the CONTROL node's own voltage against an unloaded 1 k/1 k divider, which is where
the loading would actually show.

**5. Both work in HB, because both are `ModelKind.Linear`** — stamped into the linear partition at
every retained harmonic, like a resistor, and therefore in DC, S-parameters and everything built on
them (parametric sweep, loadpull, pursuit) too. `CurrentSourceHbTests` measures that rather than
asserting it: the VCCS's transconductance is checked at k=0, k=1 AND k=2 of one run, because a device
stamped only into the DC solve passes the first and fails the others. The corollary the user
documentation now states: an ideal transconductance has no frequency dependence, no compression and
no delay — `G` is the same number at every harmonic, and anything else needs an SDD.

## The prefixed voltage, current and power units did not scale (2026-08-29)

`Vdc=2 mV` resolved to **two volts** and `I=2 mA` to **two amps** — measured directly, not inferred.
`Units.cs` kept `mV`, `kV`, `uV`, `nV`, `mA`, `uA`, `nA`, `mW`, `uW`, `kW` in `_identityUnits`, so
`Units.Scale` returned null for every one of them and `Evaluator.ApplyUnit` fell through to a
multiplier of **exactly 1**. The value parsed, stamped, converged and plotted; nothing anywhere
reported it. Surfaced by ITone, whose natural default unit is `mA`.

**Identical in kind, cause and fix to the `nm`/`cm` bug of 2026-08-07**, whose own note in
`_identityUnits` already stated the rule this violated: a prefixed unit is physics, not a
dimensionless marker, and it belongs in `_scales` with its real value. Fixed the same way.

**The three BASE symbols `V`, `A` and `W` deliberately did NOT move**, and that is the part worth
remembering. Their multiplier of 1 is already correct through `ApplyUnit`'s identity fallback, so
there is nothing to gain — and moving them would flip `Units.IsKnown`, which the CONSERVATIVE `.cnl`
token gates read (`CnlReader`'s parameter-declaration peek at line 316, and `SplitTrailingUnit` with
`includeIdentityUnits: false`). **`W` is this codebase's own name for a microstrip WIDTH**, so
`L = 2 * W` would have started splitting into the expression `2 *` and the unit `W` — a new silent
bug traded for the old one. The repo's own `GenuineIdentityUnits_AreStillIdentityOnly` already held
that line for `V`; it now covers `A` and `W` too.

**Second-order fix, the same one R-len-2 recorded for `nm`/`cm`.** Several `.cnl` and vendor-dialect
token gates consume a trailing unit through `IsKnown` rather than `IsRecognizedUnit`. While these
units were identity-only, `IsKnown` was false and those sites left the token **unconsumed** —
silently dropping the unit from a cell-parameter declaration, which is the same shape as the
phantom-node failure `Units.cs`'s `TOhm` comment records. All three parse sites (instance line,
top-level variable assignment, cell-parameter declaration) are now gated separately in
`ElectricalUnitsTests`, because `src/Core/CLAUDE.md` warns they are separate code paths and that
fixing one has repeatedly left the others broken.

**The sweep path needed no engine change, and that is asserted rather than assumed.**
`ParametricSweepEngine`'s own rule — *scale and mark must come from the same unit or they contradict
each other* — was being broken by the null `Scale`: it scaled by 1 while re-attaching the base symbol
that MARKS the values as already-SI. A 1 → 3 mV sweep therefore ran at 1 → 3 volts, which is the 2 GHz
→ 2 Hz bug in a new dimension. `SweptElectricalUnitTests` covers it in both directions (spec-carries-
the-unit, and spec-inherits-the-VAR's), mirroring `SweptLengthUnitTests`.

**Blast radius was small:** nothing in `testdata/` used one of these spellings. Any design that did
was previously running 1000× off and is now correct.

## The bipolar transistor: six things that were not obvious (2026-08-29)

`BjtModel` — the charge-control BJT, both polarities, added as `BJT_NPN`/`BJT_PNP`. The equations are
the published ones and hold no surprises; these seven do.

**1. The reverse Early voltage moves the current the OTHER way, and it is not a sign slip.** `Vaf`
enters through `Vbc` and `Var` through `Vbe`, both inside `q1 = 1/(1 − Vbc/Vaf − Vbe/Var)` — and `q1`
ends up in the DENOMINATOR of the transport current through `qb`. A reverse-biased collector junction
therefore RAISES `Ict` (the familiar Early slope) while a forward-biased emitter junction LOWERS it.
A model that made both raise it would have `Var` backwards and would still look completely plausible
on an output-characteristic plot, because nothing there varies `Vbe`. `T5` in `BjtModelTests` pins the
ratio at `1 − Vbe/Var` exactly, not merely the direction.

**2. A few-millivolt `Vtf` turns the transit-time enhancement into a step, at every bias.** The term is
`Xtf·(Icc/(Icc+Itf))²·exp(Vbc/(1.44·Vtf))`. With `Vtf` around 4 mV — which is what an extraction
returns when the fit found no `Vbc` dependence worth naming — the exponent is about −550 anywhere in
forward active (underflows to zero, so the parameter is inert) and about +130 anywhere in saturation
(e^130, so the stored charge and its derivative reach values that wreck the conditioning of the whole
matrix). Neither is a bias a user would consider unusual. Hence `MaxTransitEnhancement = 1e4`, which
holds the factor in value AND slope: base pushout multiplies a transit time by tens, not thousands, so
it never binds on a card that means something by the term. **The same shape is why the palette-wiring
test's activation table uses `Vtf = 0.5 V` rather than the shipped default** — at the default there is
no bias at which a perturbation test can tell the parameter from an unwired one.

**3. `Xcjc`'s split needed no conditional port, because two ports across the SAME node pair simply
add.** The fraction `Xcjc` of `Cjc` belongs on the internal base node and the rest from the external
base to the internal collector. The obvious design is a fourth intrinsic port present only when `Rb`
is non-zero — and it is unnecessary: when `Rb` is zero the elaborator gives that port the same two
nets as the internal one, the engine stamps both, and `Xcjc·Qjc + (1 − Xcjc)·Qjc` is `Qjc`. The port
is therefore unconditional and the conditional code does not exist. (This is the same reason
`FetModelBase` can put two ports on a shared source net.)

**4. The Jacobian is polarity-invariant everywhere except the ohmic base port.** Writing the p-n-p as
"every internal voltage and current negated" makes `dI/dv` carry one factor of the sign from each,
and `s² = 1` — so `Dg` and `Dc` are literally the same matrices for both polarities. The one exception
is the base-resistance port: its current is ohmic and does NOT flip, while the junction voltages that
modulate its resistance do, so those two cross terms carry a single factor of `s`. Getting that wrong
is invisible in any test that only ever builds an n-p-n.

**5. The same node voltages are not the same operating point for the two polarities.** An n-p-n and a
p-n-p biased identically are in forward-active and reverse-active respectively, and in reverse-active
everything that lives in forward conduction — `Tf` most obviously — reads as an unwired parameter.
Any probe over the family has to MIRROR its bias grid; `BjtModel.IsNpn` exists for that and for
nothing else.

**6. `TerminalNames` is ONE name per port, and two older models in this directory say otherwise.**
The base class's own default is `Enumerable.Range(1, PortCount)` and both consumers — the HB branch-
current key builder (`I:Q1:collector`) and harmonicaRF's port axis — index it by PORT.
`MicrostripTeeModel` and `MicrostripCrossModel` agree. `DiodeModel` and `FetModelBase` return TWO
entries per port (`["gate","source","drain","source"]`), which predates both consumers; the second
entry of each pair is simply never read, so a FET's port-1 branch key reads `source`. Copying that
shape for the BJT — the obvious move, since the ports genuinely span named net pairs — would have
mislabelled every published trace. The BJT's ports are therefore named for what they CARRY
(`ibe`, `ibc`, `ic`, `icx`) with only the three ohmic ports named for a terminal, because those
alone carry the external terminal current exactly.

**7. Package parasitics are not part of the transistor, and `Eg` is read as the 0 K bandgap.** A
published model for a packaged part typically wraps the intrinsic device in lead inductances and
package capacitances; those are ordinary components a user places around it, exactly as they are for
the diode and the FET family, and folding them in would make the component a packaged part rather
than a transistor. Separately, circuitRF's shared junction relations (`Temperature`) read `Eg` as
`Eg(0)` — the value Varshni narrowing subtracts from — while several parameter tables quote a
room-temperature figure there. It costs nothing at `Temp == Tnom`, where every relation is the
identity; it shifts the SLOPE of the saturation currents away from it. Same reading as the diode and
the FET family, so at least the three cannot disagree.

## An SnP with no Touchstone file named neither itself nor its problem (2026-08-26)

Owner-reported. A schematic with an unset `File` on an SnP failed the run with:

```
Running 'S-Param_Test.csch' 0 / 101 - 'SP1': SnP: Touchstone file not found: '/…/Documents/<workspace>'
```

Two independent defects, one line.

**1. The only name in the line belonged to the ANALYSIS.** `SchematicRunService` prefixes every
analysis failure with `pa.ResultName`, so `'SP1'` is the S-parameter analysis — not the component.
`SnpModel` had no idea who it was: `ComponentModelFactory` hands it resolved parameter VALUES, and
resolved values carry no identity. On a design with several SnPs, one of them nested inside a cell,
there was nothing to search the schematic for.

The name does exist — `ElaboratedComponent.InstancePath`, the dotted top-down path the elaborator
builds (`X1.X2.SP1`) — but only AFTER flattening, and `Stamp(mna, c, omega)` is the first place the
model sees it. So the refusal moved into the stamp and now reads `SnP 'X1.X2.SP1': …`. The path is
carried VERBATIM: read left to right it already is the route through the hierarchy, so a first cut
that appended `(inside 'X1' then 'X2')` was saying the same thing twice and the owner had it removed
the same day. **A missing `File` parameter is therefore no longer refused in the factory either** — it
constructs with an empty path and lets the model raise the named message, rather than throwing an
anonymous `"SnP: File parameter is missing or not a string"` from a place that cannot name anyone.

**2. A blank path resolved to a real directory.** `Path.Combine(dir, "")` is `dir`, and both readers
resolved a quoted relative `File` unconditionally — so `File=""` became the netlist's OWN folder, and
"not found" then pointed at a path that exists and that the user never typed. `CnlReader` and
`VendorAReader` now leave a blank path blank, which is what lets the model distinguish three cases the
one message used to collapse: no file specified, a path naming a folder, and a genuinely absent file.
The folder case is kept as its own sentence because a blank path is no longer the only way to reach it
— one can still be typed or pasted.

Gate: `tests/Engine.Tests/Linear/SnpMissingFileMessageTests.cs` (5 tests), T5 of which drives the
whole `.cnl` → `Elaborator` → `Stamp` path and asserts the netlist's own directory does NOT appear in
the message.


## `DeviceWorkerProcessTests` is FLAKY — one static event, two workers (2026-08-26, observed, not fixed)

Found while checking a full-suite run for an unrelated change (`brief-cli-em-verb.md`), and recorded
because a flake nobody has written down gets re-diagnosed every time it appears.

`DeviceWorkerProcessTests.AWorkersOwnLog_ReachesTheHost_WhenItIsAskedFor` fails intermittently:

```
Assert.Contains() Failure: Item not found in collection
Collection: ["osdi-worker: dlopen failed: dlopen(/var/folders/k7"...]
Not found:  "measured node 6 as undriven"
```

**It is not about the reference worker.** `ProcessDeviceWorkerTransport.Logged` is a PROCESS-WIDE
static event, and `OsdiWorkerTests` / `OsdiModelDiscoveryTests` / `CompiledModelValidationTests` run
concurrently in the same assembly and publish into the same subscriber. The test's
`WaitForDelivery(transport, () => seen.Count > 0)` then returns on the FIRST line to arrive — which
on this machine is a foreign `osdi-worker: dlopen failed` — and asserts against a collection the
reference worker has not written to yet.

Measured: `dotnet test tests/Core.Tests` on its own fails 2 runs in 3. It needs no other test project
and no change to reproduce, so **it is intra-assembly and pre-existing** — worth stating, because it
turns up in a full-suite run of a change that touched nothing near it and reads like a regression.

The fix, when someone picks it up, is to wait for the line the test is actually about rather than for
any line at all (`seen.Any(l => l.Contains(expected))`), or to key the collector on the transport it
started. Left alone here deliberately: it is in a subsystem this change did not touch, and a "while I
was in there" edit to someone else's flaky test is how two problems become one confusing commit.

## `at(x, "axis", index)` — a reference value that survives adding a sweep (2026-08-19)

Owner's question, and it is the right shape of question: *"AMPM = trans_phase − phase(HB1.V[0, "Vout", 1])
works when only Pin is swept. Add an RFfreq sweep and it's invalid. Is there a single expression that
works either way?"* The answer was **no**, and the reason is a real gap rather than a missing convenience.

**The accessor is shape-independent for KEEPING axes and positional for FIXING one.** `HB1.V("Vout", 1)`
locates node and harmonic by name and keeps every sweep, so it survives any sweep depth. But the
reference term has to fix the Pin axis, and both notations fix by POSITION: `HB1.V("Vout", 1, 0)` /
`HB1.V[0, "Vout", 1]` with one sweep become `HB1.V("Vout", 1, All, 0)` / `HB1.V[0, 0, "Vout", 1]` with
two. There is no third spelling.

**The two failure modes are asymmetric, and the quiet one is the dangerous one.** The bracket form
ERRORS when the axis count changes ("expected 4 axis token(s), got 3") — that is what the owner saw. The
accessor form does not: `EvalQualifiedAccessor`'s sweep loop is bounded by the cube's OWN sweep count,
so surplus arguments are silently dropped. `HB1.V("Vout", 1, All, 0)` — correct with an outer RFfreq
sweep — quietly means "all Pin points" without one, and AM-PM comes out identically 0.00 with nothing
reported anywhere.

**The capability already existed one layer down and was simply unreachable.** `DataCube.At(axisName,
index)` pins by name and keeps the rest, and `ElementWise`/`UnionAxes` already broadcast by axis NAME —
so `[RFfreq, Pin] − [RFfreq]` lines each curve up with its own reference. Both verified numerically
before proposing the feature, not assumed. Exposing `at` as one evaluator function is therefore the
whole fix:

```
trans_phase = phase(HB1.V("Vout", 1))
AMPM        = trans_phase - at(trans_phase, "Pin", 0)
```

End-to-end on the owner's own netlist through `Cli hb`: `[RFfreq:3 × Pin:4]` with **every row starting
at exactly 0** (a per-frequency reference, not one global number), and the single-sweep run reproducing
the 2 GHz row value-for-value from the identical expression text.

**Two things the wiring had to fix, both in the no-sweep direction.** `DataCube.At` did `result.Cube!`
on a `SliceResult` — but pinning the ONLY axis leaves a bare element with a null `Cube`, so it threw a
`NullReferenceException` on exactly the case a shape-independent expression hits first (one sweep, no
RFfreq). It now returns a rank-0 cube. And `AxisIndex`'s "No axis named 'x'." said nothing about what
was there; it now names the axes, because its callers are user expressions.

**Strict by choice.** A missing axis, a scalar argument, and an out-of-range index are all errors — the
last naming both usable ranges (`0..2, or -1..-3`). Returning the value unchanged would have made a
mistyped axis name read as "AM-PM is identically zero", which is the very failure mode the function was
added to remove. Negative indices count from the end (`-1` = last), because "referenced to the top of
the sweep" should not require knowing the sweep length.

Tests: `tests/Core.Tests/Expressions/AtAxisPinTests.cs` (10) — the same expression text against a
`[Pin, …]` and a `[RFfreq, Pin, …]` fixture, the per-frequency reference, negative indexing, the rank-0
result, and each refusal.

## Starting a worker is announced, once, before it starts (2026-08-19)

Owner report: the first time a worker is launched to evaluate an external model there is no feedback
at all.

**It is the one step in evaluating an external model that a user waits on and cannot see.** The
worker process starts, loads the vendor's model library and describes its device types — and on macOS
all of that happens inside the Linux VM circuitRF ships, which has to boot first. Until it finishes, a
run proceeding perfectly normally is indistinguishable from one that has hung, and the next thing
printed is whatever the run says NEXT, which is a result or a failure and never mentions the worker.

**`ProcessDeviceWorkerTransport.Starting`**, a static event carrying a `DeviceWorkerStart(Provider,
Command)`. Three things about the placement are load-bearing:

- **Raised immediately BEFORE `Process.Start`, not after.** The wait is the whole reason for
  announcing it; a message that waited for a successful start would arrive after the thing it
  explains, and would be missing entirely from the case a user most needs it in — a start that never
  completes.
- **At the process, not at the provider lookup.** `ExternalDeviceRegistry.Find` keeps what it
  resolved, so a design placing forty devices from one kit starts one worker and gets one line. That
  is what makes "once" true without anything having to remember whether it has spoken. A worker
  genuinely started a second time — a different kit, or the same kit after the workspace changed — is
  a second thing happening and is reported as one.
- **Structured, not a formed sentence.** How it is worded belongs to whoever shows it; a headless
  host may want it in a different shape entirely. `src/Ui`'s `WorkspaceViewModel` subscribes once, in
  its constructor, beside the other process-lifetime static it already listens to, and posts through
  the dispatcher because this arrives on whatever thread the run is on.

A subscriber that throws is swallowed: a host's own reporting must never be the reason a worker fails
to start, because the failure would then be attributed to the kit, and the kit would be fine.

**Gate:** `tests/Core.Tests/Devices/External/DeviceWorkerStartNotificationTests.cs`, 5 tests —
announced before the process exists (checked by starting a path that is deliberately not an
executable, so the announcement has to precede the failure), one event per started process, a caller
with no provider name still announces, a throwing subscriber does not stop the worker, and a second
lookup of the same provider starts nothing and says nothing. The UI half is a source-level check in
`tests/Ui.Tests/KitLayoutGeneratorRefreshTests.cs`, since the event means nothing until something
subscribes and the subscription is one line in a constructor.

## An ExtDevice selector taken verbatim swallowed a REFERENCE, not just a literal (2026-08-19)

Owner report: a kit transistor that used to simulate failed every operating point, and the worker's
own per-create log read:

```
create <TYPE>: File=File (NOT READABLE HERE)
create <TYPE>: TSNK=-1 (supplied)
create <TYPE>: RTH=1e-06 (supplied)
create <TYPE>: probe eval at zero bias FAILED
```

**The `File` parameter arrived as the literal four characters `File`.** The numeric parameters beside
it — and they varied correctly per device, so the design was plainly reaching the model — did not,
which is what makes this read as a model or a bias problem rather than a plumbing one.

**Root cause.** `Elaborator.ResolveExtDeviceParameters` treats `Provider`, `Type`, `File` and `Model`
as SELECTORS and stored them **verbatim, never evaluated**. That rule exists for good reasons and
they are still good: a path is not an expression (a leading `/` alone stops the parser at position 0
— the same trap `SnP`'s `File=` hit), and falling back to verbatim only when evaluation throws is not
enough, because a path that happens to parse as arithmetic would be silently turned into a number.

What the rule did not allow for is that `File=File` is not a literal at all. It is the ordinary way a
netlist passes a value down: the kit's device cell declares its data file as a cell parameter and
forwards it into the device by name, which is the only way one part can be instantiated at several
file-backed sizes. Verbatim turned the reference into its own name.

**Fix: the QUOTING decides, and the netlist already states it.**

- Quoted → a literal, always. Never looked up, not even when something in scope is spelled the same.
- A bare name the scope BINDS → resolved through the ordinary evaluator, in the scope the device sits
  in, so both an instance override and a cell's declared default work, at any nesting depth. Handed
  on as text whatever kind it resolved to, because `ComponentModelFactory` requires `Provider` and
  `Type` to be strings.
- Anything else — an unquoted path, an enum value, a name nothing binds — is left exactly as it was.

So nothing that worked before changes, and this is a reading of the netlist rather than a heuristic
about it.

**Verified against the reported design end to end**, by elaborating its real `.cnl` against a stub
provider: five devices, each now handed its own correct `.mdl` path, with the thermal parameters
differing per device as the design says. Before the fix all five got `File`.

**Gate:** `tests/Core.Tests/Devices/External/ExtDeviceSelectorForwardingTests.cs`, 8 tests — four for
the forwarding (one instance, two instances at different files, the cell's declared default, and the
non-selector parameters that were already right and must stay so) and four for what must NOT change
(a quoted literal is never looked up even when a scope binding shares its spelling; an unquoted path
stays verbatim; a bare name nothing binds stays verbatim; `Provider`/`Type` follow the same rule).
The first four were confirmed to fail before the change and the last four to pass throughout.

## The SDD evaluator: what `dut.Evaluate` actually costs, and what closing the gap bought (brief-harmonicarf-r3b §1, 2026-08-13)

**The measurement that started this:** on the shipped default harmonicaRF document (Hero 2's GaN
HEMT as an SDD, K = 3, no package), `dut.Evaluate` cost ~9–13 µs and was reported as "100% expression
evaluation, nothing else in it" — `SddEvaluator.EvalDual` tree-walked the parsed AST against a
**freshly-built `Dictionary<string,Dual>` on every single call**, with `"_v{i+1}"` string
interpolation and a string-hashed lookup at every `RefExpr`, even for a one-node expression.

**Fix, in two measured steps, `SddEvaluator.EvalDual`/`EvalDouble` left completely UNTOUCHED as the
reference implementation:**

1. **`CompiledSddExpr`/`SddCompiler` (new, `src/Core/Expressions/SddCompiled.cs` +
   `CompiledSddExpr.cs`)** — each `RefExpr` is resolved to an integer SLOT once, when `SddModel`
   compiles its equations in its own constructor (alongside where it already caches the parsed AST),
   not on every `Evaluate` call. A parameter's `Dual` (zero gradient, correct width) is built ONCE per
   model and reused — it never changes across calls, so building it fresh every time (as the
   dictionary path did) was pure waste. Evaluation touches no `Dictionary`, hashes no string,
   interpolates nothing. **Bit-identical by construction, verified by a corpus test**
   (`SddCompiledBitIdenticalTests`, 123 cases across the shipped default's own two equations, every
   equation in `testdata/`, and hand-written ones exercising `^`, conditionals, every function, and
   the `ExpCap`/`LogFloor` clamp paths — asserts exact `double` equality, not a tolerance) that must
   stay green; it is the gate against a "faster but slightly different" evaluator, which would move
   every SDD-based hero golden at once.
2. **`in T` parameters on `IAdScalar<T>`** (`Add`/`Sub`/`Mul`/`Div`/`Pow`/`Neg`/the function table) —
   `Dual` is a ~144-byte struct carrying a fixed 16-wide gradient regardless of the actual port+control
   count (2, here), so a by-value binary op was copying ~288 bytes of INPUT on every `Add`/`Mul`/etc.
   Read-only-reference parameters cut that; the RESULT is still returned by value (there is no
   caller-owned slot to write into without restructuring the whole evaluator around `Span<double>`
   buffers — out of this step's scope; see the open item below).

**Measured, shipped-default fixture, both steps together:** `dut.Evaluate` **13.4 → 9.5 µs (~29%
faster)**. Every hero golden (Engine.Tests, 1177 tests + the 6-project full-solution run) still
passes — nothing moved.

**A correction worth keeping so nobody re-derives a frame budget from the wrong number.** An earlier
draft of the brief this work came from quoted "~2 ms per solve" from `HarmonicaGridDragCostTests`.
That figure is real but belongs to a DIFFERENT fixture — K = 5 **with an Rd/Rs/Ls package**, a
materially bigger circuit. On the shipped default (K = 3, no package) a warm solve is **0.69–0.86 ms
before this work, 0.36–0.37 ms after** — nowhere near 2 ms either way. The solve COUNT in a grid was
never the bottleneck; the per-solve evaluator cost was.

**The surprising part, worth keeping so nobody re-derives it:** the compiled node-walk ITSELF,
isolated from the per-call setup it replaces, was measured to be *slower* than the dictionary-based
reference on the shipped default's 80-node drain equation (~10.5 µs compiled vs ~8.9 µs reference,
before the `in`-parameter step; the gap narrowed to ~13% after it, but did not close). The equation
has only two names to resolve ("_v1"/"_v2" — the shipped default's coefficients are literal, not
named parameters), so the dictionary it replaces was already tiny and cheap to query; nearly all of
the ~105 ns/node cost the brief measured is genuinely **per-node walk cost** (dispatch + the struct
traffic of a functional, expression-tree-shaped evaluator), not lookup cost — and killing the
dictionary does not touch it. This is exactly what the brief's own step 3 (compile to a flat
instruction array over `Span<double>`, i.e. leave the tree-walk-of-boxed-nodes shape entirely) is
for. **Reordering the compiled dispatch from a type-pattern switch (a chain of `isinst` checks) to an
explicit integer-discriminant switch was tried and measured to NOT close this gap** — the per-node
cost is structural to the tree-walk shape, not an artifact of how one particular walk dispatches.

**Step 3 — built after the above was reported and confirmed with the owner.**
`SddRegisterCompiler`/`RInstr` (`SddRegisterProgram.cs`) flatten the whole equation into a linear
three-address-code array at compile time — no boxed node objects, no recursion, one register per
instruction, walked by a single `for` loop with a `switch` on a byte opcode. Register space is one
flat `Dual[]`: indices `[0, totalSlots)` are the input slots (exactly as steps 1–2 already laid out),
index `totalSlots + i` is instruction `i`'s own output. A bare name/leaf reference costs NO
instruction at all (it is just a slot index), which is why the program is shorter than the
equivalent node tree. **This finally lands the win the per-node-cost finding above said step 3 would
need to reach**, because it genuinely leaves the "one struct-returning virtual call per node" shape
behind rather than reorganising it.

**Scope, deliberately not fully general — and why that is fine.** The compiler handles every
expression EXCEPT a conditional (`if(...)`/ternary); `SddRegisterCompiler.ContainsConditional` scans
the AST once at compile time and routes an equation containing one to step 1's node-tree walk instead
(still correct, still faster than the original dictionary path). A correct, jump-based bytecode VM
with short-circuit `&&`/`||` is a real undertaking with its own correctness surface, and **no SDD
equation in this repository — the shipped default, or anything in `testdata/` — contains a
conditional**; building branch-handling for a construct with zero measured real-world use was judged
not worth the risk. If a future SDD equation genuinely needs one, it still works correctly (via the
fallback), just without step 3's speedup.

**Measured, shipped-default fixture (steps 1–3 together, against the ORIGINAL pre-brief baseline):**

| quantity | before | after | factor |
|---|---|---|---|
| `dut.Evaluate` | 13.4 µs | 4.5 µs | **3.0×** |
| `EvalDual` big (drain eqn), reference dict path | 8.9–11.9 µs | — (superseded) | — |
| `CompiledSddExpr.EvalDual` big, register machine | — | 3.0–3.1 µs | **~2.9×** vs reference |
| warm `ctx.Solve` | 0.69–0.86 ms | 0.36–0.37 ms | **~2×** |
| cold `ctx.Solve` | 0.85–1.06 ms | 0.55–0.57 ms | **~1.7×** |
| `PinSearch.Sweep` (46 solves, tier-A ladder) | 35.7–43.0 ms | 18.3–18.8 ms | **~2×** |

Bit-identical corpus (123 cases, both the register path and the conditional fallback exercised) and
the full solution (Core/Engine/Harmonica/Ui/RfCore/WBond/Firewall) all still pass — nothing moved.
`SddEvaluator.EvalDual`/`EvalDouble` remain completely untouched as the reference implementation.

**What this means for the frame-rate target:** tier-A's 46-solve sweep is now ~18.5 ms — well short
of the brief's own "step 3 optional if the sweep lands near 2–3 ms" bar, but a real 2× cut from the
evaluator alone. Whether an L1-drag frame hits >60 fps depends on the REST of the frame (render,
readout-strip rebuild, pool/dispatcher overhead) — see the harmonicaRF-side write-up for that
breakdown; it is a separate cost this brief's §1.4 measures independently.

## `RFfreq = 2 GHz` in a schematic VAR produced no variable at all (2026-08-18)

> Reported together with the parametric-sweep unit bug, which lives in the engine — see
> `src/Engine/RESOLVED.md` §"A parametric sweep's unit". One schematic reached both.

The other half of that report. A `.cnl` has no unit column, so `CnlReader.SplitExprUnit` has always
lifted a trailing unit into `Variable.Unit`. A schematic VAR row *does* have a unit column, and
`NetExtractor` passed the expression through verbatim — so the identical text meant two different
things, and the schematic one meant **nothing at all**: `Parser.Parse("2 GHz")` is a parse error (the
grammar has no unit-suffix production), and `Elaborator` skips a global it cannot resolve inside a bare
`catch {}`. Nothing anywhere reported it. Downstream, `LoadpullPursuitEngine.Resolve` catches the
resulting `UnresolvedNameException` and substitutes **1 GHz**, and the sweep row had no unit to inherit.

**The rule now lives once, in `Units.SplitTrailingUnit`.** The schematic caller
(`NetExtractor.LiftInlineUnit`) **verifies the split against the parser** instead of trusting the unit
table: every bare SI prefix is a unit name, so a token-only rule tears `"2 * f"` into `"2 *"` + femto
and `"R * m"` into `"R *"` + milli. Split only when the unsplit text does not parse and the split text
does — which makes it reachable by exactly the rows it is for and by no row that already worked. A
netlist keeps the greedy rule; it has no alternative spelling to fall back on.

Related, and already recorded in `src/Core/CLAUDE.md`'s trap list: a cell-parameter declaration, a
top-level variable assignment and an instance-line param are three separate parse sites for the same
unit token, and fixing one has repeatedly left the others wrong. The schematic VAR row was the fourth.


## SP-P1 — an SnP fits its splines once, and a sweep parses each file once (2026-08-30)

`brief-sp-p1-snp-spline-cache.md`. `SnpModel.Stamp` called `RFNetwork.Interpolate(snp, [hz], …)`
once per frequency, and that call re-fits all 2·N² splines from scratch on every call. Three pieces
now: `SnpFit` (the frequency-independent half — domain conversion, component extraction, phase
unwrap, spline solve), `SnpInterpolator` (the public wrapper — out-of-range policy, warn-once flag,
`Evaluate(double)` / `Evaluate(double[])`), and `TouchstoneCache` (process-wide parse + fit cache).
`RFNetwork.Interpolate` is now `new SnpInterpolator(...).Evaluate(targets)`, so there is exactly one
fitting path.

### Measured

Release, single thread, scratch console harness against the pre-change tree in a `git worktree` —
NOT a `Category=Benchmark` test. 200-section RLC ladder, 401 frequency points; the SnP variant
replaces 20 of its inductors with 2001-point 2-port Touchstone files.

| | before | after | |
|---|---|---|---|
| ladder, no SnP | 211.5 µs/pt, 558 KB/pt | 215.4 µs/pt, 558 KB/pt | unchanged, as expected |
| ladder, 20 SnPs | **5,737.9 µs/pt, 33,284 KB/pt** | **204.5 µs/pt, 600 KB/pt** | **28.1× faster** |
| Hero 1 (one 84-pt .s2p) | 8.5 µs/pt, 25.0 KB/pt | 6.9 µs/pt, 13.9 KB/pt | 1.23×, allocation 1.8× |

The SnP ladder now costs what the plain ladder costs (204.5 vs 215.4 µs/pt) — the brief's target.
**Allocation: 55× overall, but that understates it and the honest number is the difference.** The
600 KB/pt that remains is the ladder's own MNA assembly (the no-SnP row measures 558 KB/pt of it);
the SnP-attributable part went 32,726 → 42 KB/pt, **779×**. Closing the remaining 558 KB is SP-P2's
job, not this one's.

**Bit-identical, proven end to end, not just by the goldens.** The harness prints an FNV-1a hash of
the whole `S` cube; all three fixtures hash the same in both trees
(`BC1057644B6D8669` / `C8FAAE3188D0CCC0` / `3CF16DEF32479870`). No tolerance was loosened anywhere.

### What did NOT get faster — and one small, deliberate regression

**The batch `Interpolate` path is ~1.2× SLOWER** (0.266 → 0.320 ms for one 401-target call on a
2001-point file, both warmed and averaged over 200 reps). That is the cost of routing it through a
`SnpFit` object instead of stack locals, and it is the price of the single fitting path. In absolute
terms it is 54 µs on a call the Data Display makes once per plot. Two things were tried and did not
recover it, so don't re-try them: reordering `EvaluateAll` to (row, col)-outermost, and hoisting the
`Spline1D` structs into locals — **both are kept because they ARE worth ~1.5× on their own**, but
the residual is object-allocation overhead, not loop shape. The first measurement of this looked
like 1.6× and was mostly tier-0 JIT: the pre-change tree had already executed that exact code 401
times before the batch call was timed, the post-change tree had not. **Warm both paths before
comparing them, or the new one is measured cold.**

### Traps

- **A pure series two-port cannot be an SnP fixture.** The first harness generated each .s2p as a
  bare series R+L, and every run threw `SingularMatrixException`. `SnpModel` stamps through Z, and a
  pure series element has no impedance matrix at all (this is the same fact that `Chain` exists for
  — see `src/Core/CLAUDE.md`). The fixture is a pi-network (series R+L, shunt C at each port).
- **The out-of-range warning had to be split away from the cached fit.** The brief has the cache
  hold the interpolator itself, keyed additionally by policy. Done that way the warn-once flag
  becomes process-wide, so a SECOND run of a design that extrapolates past the end of its file
  would say nothing at all. So the cache holds the immutable `SnpFit` and hands out a fresh
  `SnpInterpolator` wrapper each call — the fit is shared, the warning is per consumer (once per
  model per run, where it used to be once per frequency point and the engine's drain deduped it).
- **The policy is not part of the fit key.** `OutOfRangePolicy` only selects `Eval` vs `EvalExtrap`
  at evaluation time; it changes no coefficient. Keying the cache by it would have doubled the
  fits for nothing. The key is (path identity, method, format, interpolateIn).
- **`Interpolate`'s two `ArgumentException`s fire in the original order** — empty source before
  empty target grid — which is why `RFNetwork.Interpolate` still checks `source.IsEmpty` itself
  rather than letting the constructor raise it after the target check.
- **Nothing mutates a cached `SNP`.** `SnpModel` reads `Z0` and nothing else; `SnpFit` reads
  `Frequencies`/`Matrices`/`Format`/`Z0`. The instance is shared, and `SNP` does have public
  setters (`Type`, `Format`, `Z0`), so anything new that reaches `TouchstoneCache.Get` must treat
  the result as immutable or take a copy.
- **Two strings moved file and the firewall gate caught it.** `UserFacingTextGateTests` keys its
  allowlist on PATH plus text, so relocating an existing message into a new file reads as a new
  user-facing message. Both lines were re-added under `src/RfCore/SnpInterpolator.cs`.

### Call sites not converted

`RFNetwork.Interpolate` has exactly one production caller — `SnpModel.Stamp`, now converted. Every
other caller in the repo is a test. The Data Display's plotting path does not call it at all (the
brief expected it to, batched); nothing there needed changing.

### Gates

`SnpInterpolatorTests` (RfCore.Tests) compares per-frequency `Evaluate` against batch `Interpolate`
with `Assert.Equal` on `Complex` — **no tolerance** — over 2- and 5-port files × 3 methods ×
2 formats, at stored knots and between them, and out of range under both policies. That test is
also what gates the two evaluation loop orders against each other. `TouchstoneCacheTests` covers
parse-once, fit-once-per-settings-tuple, per-consumer warnings, and re-read after an mtime change.
`SnpCacheTests` (Engine.Tests) runs `SParameterEngine.Run` twice on one netlist and once on a fresh
elaboration and asserts the three `S` cubes are bit-identical — the shape `ParametricSweepEngine`
produces — plus a rewritten .s2p reaching the second run.

## HB-P4 — the SDD evaluates the whole time grid in one call (2026-08-30)

`docs/sonnet-briefs/brief-hb-p4-sdd-grid-evaluate.md`, Core side.
`src/Core/Expressions/SddGridEvaluator.cs` + `GridScratch.cs` + `GridDomainWarnings.cs`,
`CompiledSddExpr.EvalDualGrid`, `SddModel.EvaluateGrid`, `ComponentModel.EvaluateGrid`/`EvaluateInto`,
`GridResult.cs`, `NonlinearEvalDiagnostics.cs`.

The compiled register program is the same instruction sequence for every sample of an HB time grid.
It is now walked ONCE per grid with each register laid out as `value[S]` + `grad[k][S]`, vectorised
across samples, instead of once per sample through 136-byte `Dual` structs. The scalar path
(`EvalDual`, `Dual`, `SddEvaluator`) is untouched and is the oracle.

**Measured (Release, M4, 10 cores, a 2-port SDD carrying the hero gate + drain + a charge equation):**

| S | scalar ns/sample | grid serial | speedup | grid parallel | speedup |
|---|---|---|---|---|---|
| 16 | 4,983 | 543 | 9.2x | — | — |
| 32 | 4,135 | 389 | 10.6x | — | — |
| 128 | 3,966 | 280 | 14.2x | 368 | 10.8x |
| 256 | 3,929 | 265 | 14.8x | 199 | 19.7x |
| 1024 | 3,944 | 243 | 16.2x | 102 | 38.6x |
| 2048 | 3,941 | 245 | 16.1x | 86 | 46.1x |

### The vector helpers' FORM was worth 2.3x — spans are not free here

The obvious spelling — `new Vector<double>(a.Slice(i, w)) + new Vector<double>(b.Slice(i, w))` —
measured **2.3x slower** than `Vector.LoadUnsafe(ref a, i)` against a `ref double` from
`MemoryMarshal.GetReference`: 14.0 ns against 6.3 ns per multiply instruction per sample, and the
hero drain equation 545 ns/sample against 236. `Vector<double>(ReadOnlySpan<T>)` carries a length
check the JIT did not hoist out of the loop, so every element paid for a branch it could not need.
This is the single largest factor in the kernel and it is invisible in the source: both spellings
read as "a vectorised add".

### Blocking the walk into cache-sized chunks measured WORSE, at every size

The plausible theory — a 1,024-sample grid of the drain equation is a 1.3 MB register file, so walk
it in L1-sized blocks — was implemented, measured and REVERTED. Per sample, at S = 1,024: 530 ns at a
16-sample block, 390 at 32, 318 at 64, 284 at 128, 266 at 256, **250 at the whole grid**. The
per-instruction setup is paid once per block, so a small block multiplies it, and the streaming
access pattern never produced the cache pressure the blocking was meant to relieve. The serial path
walks the whole range in one go. (The parallel path chunks for load balance, not for cache: 2x the
core count, which measured better than one chunk per core.)

### The transcendentals are NOT the residue — the brief's ~30% estimate is off by an order of magnitude

Swapping `exp`/`ln`/`tanh` for `abs` in the hero drain equation — identical program shape, identical
register count, so the only difference is the function evaluated — moves the kernel by **0-6%**.
`Math.Exp`/`Log`/`Tanh` themselves are ~1-3% of the grid kernel; what an `exp` instruction actually
costs is its per-sample loop (8.5 ns/sample against a multiply's 6.3). **No vector transcendental was
ever used**, so the brief's contingency — "if a vector transcendental differs in the last bit, use
`Math.X`" — never arose: every transcendental is `Math.X` in a scalar loop from the start, which is
what makes bit identity free. Vectorising the transcendental instructions' GRADIENT lanes (the value
stays a scalar `Math.X` loop; the derivative factor is parked in a spare register and the lanes go
through the same `VMul`/`VDiv` as the arithmetic opcodes) was worth ~3.5% and is what shipped.
A vector transcendental library would be chasing the 1-3%.

### Bit identity needed the gradient WIDTH tracked, not just the arithmetic

`Dual` carries an `N` that a binary operator resolves as `max(a.N, b.N)`, and lanes at or above it
are never written — they stay +0.0. Computing every lane unconditionally is NOT equivalent: a lane
the scalar path leaves at +0.0 can come back as `-0.0` or `NaN` from `0*bv` or `0/0` when the value
is non-finite. N is structural (a literal is 0, a slot is n, everything else is the max of its
operands), so `BuildHasGrad` precomputes it once; a gradient-free register's lanes are neither
written nor read (reads come from a shared zero run). That is both faster and exact.

**The one residual inequivalence, recorded rather than fixed:** `min`/`max` copy the CHOSEN operand's
`N`, which is per-sample, so the grid uses `max(a, b)`. The two agree on every lane whose inputs are
finite; they can differ only where a `min`/`max` over operands of DIFFERENT structural width feeds a
non-finite intermediate. No SDD in this repository has that shape.

### `SddModel.PrefersGridEvaluate` is false for a CONTROL SDD, and that is a model-level decision

Found by the engine test, not by review. `HbNewton.RunDevicePass` gates the grid path on
`cRefTime is null`, which reads as "this device has no control seeds this pass" — but a device WITH
`C[n]` references reaches that state routinely (before its branch index resolves, and on any path
that carries no `ControlCurrentContext` at all, which is every `RunSinglePoint` caller by design).
The grid evaluator then got an empty control span for an equation compiled with `nC = 1` and threw
`ArgumentOutOfRange`. The engine's own test stays, but the door is closed by the model, which is the
one thing that knows it has controls. `EvalDualGrid` itself seeds controls per sample and is gated
bit-for-bit against the scalar path — it is only that no engine feeds it.

### A `Parallel.For` allocates its closure where the captured LOCALS are, not where the lambda is

The zero-allocation claim failed at 40 bytes per call — on the SERIAL path, at every grid size. The
`Parallel.For` in the untaken branch of `EvaluateGrid` captures locals declared at the top of the
method, so the compiler allocates the display class at the top, unconditionally. Moving the branch
into its own method (`EvalParallel`) is what makes "allocates nothing" true rather than nearly true.

### `GridResult` layout is chosen so the common case needs no copy

`EvalDualGrid` writes gradient lane `k` at `k*S + t` from a base; `GridResult.Dg` wants
`(p*P + q)*S + t`. Those are the same run of memory when the gradient width equals the port count, so
a device with no control references writes its Jacobian block in place. Only a control SDD (width
P+C) needs the intermediate buffer, because its last C lanes belong to a different block.


## `IdealSBlockModel`: one stamp for every ideal S-matrix (brief-sys-2, 2026-08-31)

`src/Core/Devices/System/` — the abstract base plus its first two users, `AttenuatorModel`
(`Atten`) and `SwitchModel` (`Switch`, serving both switch tiles). Nothing in `src/Engine` was
touched, and nothing needs to be for the rest of the SYS series.

### The stamp is the DEFINITION of S, and that is the whole reason it exists

One branch-current unknown per port, `v_p` across the port's own ± pair (the 2N-net convention
`ZPortModel` and `MixerModel` already use), and one constraint row per port:

```
(v_p − Z0_p·i_p)/√Z0_p  −  Σ_q S_pq·(v_q + Z0_q·i_q)/√Z0_q  =  0
```

Every existing N-port stamp in the repository is a *derived* form — `SnpModel` and `ZPortModel` are
Z(ω), `ChainModel` is ABCD — and each of them fails on a block a system diagram is actually made of.
The ideal circulator has `det(I−S) = 0` exactly and therefore no Z at all; the ideal through
(`S = [[0,1],[1,0]]` — a closed switch, a lowpass at DC) has no Y; the ideal open (`S = I`) has no Z.
The rows above have no singular case: the through reduces to `v1 = v2`, `i1 = −i2` and the open to
`i_p = 0`, both of which MNA represents routinely. `Atten Loss=0` and `Switch State=0` are the two
shipping components that sit exactly on those two matrices, and both solve — in S-parameters, in
DC and in HB, at ω = 0 and above it.

### The unequal-Z0 case is a lossless ideal transformer, and it is the only gate that finds a dropped √Z0

With every reference impedance equal, the `√Z0` factors cancel out of the answer, so a stamp missing
them still measures right on every uniform block. Two gates cover it:

- **Reference-impedance independence.** The same 50 Ω pad measured through 50 Ω ports and through
  75 Ω ports must renormalise into each other. Note the 0 dB case is *vacuous* here and deliberately
  left in the theory: an ideal through is a wire, and a wire is matched in every reference system, so
  it shows no mismatch to renormalise. The renormalisation used is
  `S' = (S − Γ)(I − Γ·S)⁻¹`, `Γ = (Z0' − Z0)/(Z0' + Z0)` — the uniform-real case, where the diagonal
  scaling matrix commutes through and cancels — and the test checks that formula against a one-port
  with a known load before relying on it.
- **`Z0₁ = 50`, `Z0₂ = 75` with `S = [[0,1],[1,0]]`.** Adding the two constraint rows gives
  `√Z0₁·i₁ = −√Z0₂·i₂` and subtracting them gives `v₂/√Z0₂ = v₁/√Z0₁`, i.e. `v₂ = n·v₁`,
  `i₁ = −n·i₂` with `n = √(Z0₂/Z0₁)`: a **lossless ideal transformer**. Measured in a uniform 50 Ω
  system it has the closed form `S11 = (1−n²)/(1+n²)`, `S21 = 2n/(1+n²)`, which the test computes
  itself. No SYS-2 component exposes per-port Z0, so the gate builds a two-line subclass and adds it
  to an elaborated netlist by hand — the point being that the `√Z0` lives in the BASE, and its two
  users happening not to need it proves nothing.

### `S(−ω) = conj(S(ω))` is inert on today's engine, and is implemented anyway

Checked rather than assumed: **the HB linear extractor never hands a model a negative ω.**
`HbEngine.ExtractMix` (HbEngine.cs:738 and :1015) extracts at `|ω|` and conjugates the whole `Y`
matrix and Norton vector itself, precisely so an explicit complex `Z_Port` stays consistent with the
L/C elements; `HbLinearExtractor` keys its caches on the ω it is given and has its own DC entry. So
the rule costs one line and currently changes nothing. It is in the base rather than in each
subclass because the moment it *does* matter — the quadrature coupler's `±j` in SYS-3/SYS-4 — a
subclass that forgot it would be wrong in a way no gate on a real-S block can see. `FillS` is
therefore only ever asked for `|ω|`, and the base conjugates.

### A loss is not a suppression, and they must not share a threshold

`MixerModel`'s "ideal means the entry is ABSENT" convention carries over — `SuppressedAmplitude`
snaps to exactly zero at ≥ 150 dB, and `Stamp` skips a zero S entry, so a default-placed block puts
no leakage term in the matrix rather than a 1e-10 one. But it applies **only to suppressions** (a
return loss, an isolation). A `Loss` or an `IL` is what the part is FOR, so `Loss=200` is a 200 dB
pad and is stamped as `1e-10`, not as an open. Two helpers, two names, one gate each.

### The SPDT's two throws are not symmetric, and the throw-to-throw term is a PRODUCT

Writing `ι = 10^(−IL/20)`, `σ = 10^(−Isolation/20)`, `ρ = 10^(−RL/20)`, each throw `p` gets its own
transmission to the common port — `t_p = ι` if it is the closed throw, `σ` otherwise — and then
`S[0,p] = t_p`, `S[p,q] = t_p·t_q` for two throws, `S[p,p] = ρ` on the closed path and `1`
(reflective) or `0` (absorptive) elsewhere. The throw-to-throw entry being the *product* is what
makes it vanish exactly when the isolation is off: a signal reaches the far throw only by leaking to
the common node and being carried on from there, so with nothing leaking there is nothing for it to
be a product with. The same table covers the SPST (one throw) and `State = 0` (nothing closed), and
a `State` naming a throw that does not exist closes nothing **by the same rule rather than by a
special case** — which is what makes a parametric sweep over `State` safe at every value.

### `State` had to become a number, and that renumbered a UI enum

SYS-1 shipped the SPST tile with `State = On` because the glyph reads it. That cannot survive
contact with the engine: an enum NAME is a bare identifier the expression evaluator either fails on
or, worse, resolves against a global that happens to share its spelling — and a swept string is not
a thing, while sweeping `State` is the feature. So `State` is a plain evaluated number naming which
throw is closed, and `SwitchState` was renumbered `{ Off = 0, On = 1 }` so `Enum.TryParse` resolves
the numeral against the underlying value — exactly the reasoning `SwitchThrow { T1 = 1, T2 = 2 }`
already carried. Both spellings still parse. Two consequences worth knowing:

- `default(SwitchState)` is now **Off**, so `DocSymbolGlyph.SwitchPosProperty` states its default
  explicitly. An Avalonia `StyledProperty` registered without one silently takes `default(T)`.
- `OffState` is still an enum name and *does* need the Match/`Response` treatment — a dedicated
  `ResolveSwitchParameters` in the `Elaborator` that stores it verbatim rather than evaluating it.

### One default changed: the attenuator tile places a 10 dB pad, not a 3 dB one

SYS-1 seeded `Atten.Loss = 3` as a placeholder for the label the bowtie is drawn around, before
there was a model to disagree with; brief-sys-2's parameter table states 10 dB, which is what the
factory falls back to when the parameter is absent. Two different "defaults" for one parameter means
a placed tile and a hand-written netlist line with no `Loss` are different pads, so they were
aligned on 10. The glyph is untouched — only the number printed beside it.

### The net-count refusal is now one check for the whole 2N-net family

The `Mixer`'s was the first; SYS-2 adds two more and the series adds seven. It moved to
`Elaborator.ValidatePortPairNetCount`, driven by `ExpectedPortPairNets`, and runs **after** the
overrides are resolved rather than before — because the `Switch`'s expected count depends on
`Throws`, a parameter. The mixer's own sentence is unchanged and is gated by a test, since it is the
one a user has already seen. One allow-list line replaced one allow-list line.

### `src/Core/Devices/System/` is a FOLDER, not a namespace

The files live where the brief put them; the namespace stays the flat `CircuitRF.Core.Devices`. A
namespace segment literally spelled `System` shadows the BCL root from every file inside it —
`System.Numerics.Complex` resolves as `CircuitRF.Core.Devices.System.Numerics.Complex` and fails to
compile — and the cures are a `global::` prefix on every BCL reference or a file-scoped alias.
Neither is worth a directory name. (`Devices/Fet` and `Devices/External` do match their folders;
this one cannot.)

### `Kind` is virtual from the start, on purpose

SYS-4's passive-intermod overlay makes a block `Nonlinear` when PIM is on and `Linear` when it is
off, and `ModelKind` is read off the model INSTANCE rather than the type name
(`SParameterEngine`, `NonlinearDcEngine`, `ElaboratedComponent.IsNonlinear`). Writing
`public override ModelKind Kind => ModelKind.Linear` on the base rather than sealing it means that
change is a subclass property later instead of an edit to the shared stamp.

### Gates

`tests/Core.Tests/Devices/IdealSBlockModelTests.cs` (the S each parameter set produces, the three
rules, and what actually reaches the matrix — via `CapturingMnaContext`),
`tests/Core.Tests/Elaboration/PortPairNetCountTests.cs`,
`tests/Engine.Tests/Devices/IdealSBlockSParamTests.cs` (S in / S out to 1e-12, renormalisation, the
transformer, the cascades, the DC degeneracies),
`tests/Engine.Tests/HarmonicBalance/IdealSBlockHbTests.cs` (two tones down 10 dB and **nothing
created** — the absence assertion is the half a "two tones came out" check cannot make; note a
wholly linear netlist does run under HB, checked rather than assumed),
`tests/Ui.Tests/SystemBlockElaborationTests.cs` (a freshly placed tile elaborates into the model its
glyph promises — the only thing that exercises registry defaults, ground-return extraction and
parameter resolution together).


## Balun, circulator, directional coupler and 90° hybrid (brief-sys-3, 2026-08-31)

`src/Core/Devices/System/` — three more `IdealSBlockModel` subclasses (`CirculatorModel`,
`CouplerModel`, `BalunModel`) and their factory, elaborator and registry wiring. **No machinery was
added**: SYS-2's wave-constraint stamp took all three unchanged, and `src/Engine` was not touched.

### The circulator is the component the whole family was built for

`S = [[0,0,1],[1,0,0],[0,1,0]]` is a permutation matrix, so 1 is one of its eigenvalues and
`det(I − S) = 0` **exactly** — no tolerance, no near-singularity, no conditioning argument. `Z` does
not exist. Its `Y` does (`det(I + S) = 2`) and is `(1/Z₀)·[[0,1,−1],[−1,0,1],[1,−1,0]]`:
antisymmetric, zero diagonal, and itself singular because every row and column sums to zero, as a
floating network's must. All three facts are asserted **from the simulated S**, not from the model's
own buffer, in `SystemBlockSParamTests`.

It is also the only component in the repository with `S ≠ Sᵀ`, and that is measured rather than
assumed: the simulated `|S21|/|S12|` equals the stated isolation in dB on all three port pairs, and
reversing `Direction` transposes the matrix and moves nothing else. At the default isolation the
reverse entry is not stamped at all, so the ratio is infinite rather than 200 dB — which is what
makes an isolator (a circulator with one port terminated) behave as one.

CW and CCW are one table, not two: CW carries port `p` to port `(p+1) mod 3`, and CCW is the
transpose, written as one so there is nothing to keep in step.

### `Phase` and `PhaseImb` arrive in RADIANS, and this cost a test to find

**The Elaborator applies a parameter's own angle unit before the factory ever sees it.** An authored
`Phase=90 deg` reaches the model as π/2. `TLineModel`'s `E` established that convention and
documents it; the coupler's `Phase` and the balun's `PhaseImb` now follow it, and their factory
fallback is `Math.PI/2`, not `90`.

This was NOT visible from a hand-written netlist. A `.cnl` line saying a bare `Phase=90` carries no
unit, so the value passes through untouched and every S-parameter gate passed — the failure appeared
only in the UI test, where the tile's registry row declares `"deg"` / `UnitDimension.Angle` and a
90° coupler measured 1.5708°. **Any new angle-valued parameter has this shape**: gate it through a
placed tile, not only through a netlist line, or the unit scale is untested.

*Found in passing here, and fixed straight afterwards on the owner's instruction — see "Every
source's Phase" below:* all four source models took their `Phase` as **degrees** while their
registry rows declared `"deg"`/`UnitDimension.Angle`, so a placed tone source with `Phase = 45 deg`
drove the circuit at 0.785°.

### The brief's balun formula double-counts the 180°, and the gate says which half is right

brief-sys-3 states `S31 = −(1/√2)/k·10^(−IL/20)·exp(−j·(180 + PhaseImb)·π/180)` — a leading minus
**and** a 180° in the exponent, which multiply to `+1` and give outputs that are IN PHASE. Its own
gate says the opposite ("`AmpImb`/`PhaseImb` at zero give exactly antiphase outputs"). The gate is
right and the formula has one negation too many, so what shipped is
`S31 = −(1/√2)/k·ℓ·e^(−j·PhaseImb)` — the same number, with the 180° in the **sign** rather than in
the exponent. That placement is not cosmetic: `e^(−jπ)` in floating point carries a 1.2e−16
imaginary residue, and "exactly antiphase at zero imbalance" is a property the gate checks and a
user reads off a plot.

### The balun's `1/2` block, explained by its modal form

`S22 = S33 = S23 = S32 = 1/2` looks like a mistake and is not. In the modal basis
(`d = (2 − 3)/√2`, `c = (2 + 3)/√2`) the ideal matrix is

```
S(unb, d, c) = [[0, 1, 0],
                [1, 0, 0],
                [0, 0, 1]]
```

— an **ideal through** from the unbalanced port to the differential mode and a **total reflection**
for the common mode. A lossless reciprocal three-port cannot have all three ports matched (a
theorem), and a real balun does not isolate its balanced ports from each other either; what a user
reads as a mismatch at ports 2 and 3 individually is the common-mode open, seen one port at a time.
The differential mode's reference is `2·Zbal` and the unbalanced port's is `Zunb`, so the block is
an ideal transformer of ratio `n = √(2·Zbal/Zunb)` and a differential load `R` is seen as
`R·Zunb/(2·Zbal)`. Gated at five `(R, Zunb, Zbal)` combinations against that closed form.

### A floating differential load across an ideal balun is a genuine floating node

Worth knowing before someone writes the obvious netlist. The ideal balun's common mode is an OPEN
(`S_cc = +1`, i.e. `i₂ + i₃ = 0`), and a resistor floating between BAL+ and BAL− says exactly the
same thing — two identical rows, a **numerically exact rank deficiency**, and an undetermined
common-mode potential. `SParameterEngine` diagnoses it correctly (its own "matrix singular —
regularization (gmin) applied … likely floating node(s)" warning) and its gmin fallback still lands
within ~5e−11 of the right answer instead of on it, against the 1e−15 the rest of the file holds.
The fix in a netlist is to give the common mode somewhere to go: **two R/2 halves with the tap
grounded** is the identical differential load, pins the common mode, and changes the answer not at
all because the unbalanced port does not couple to the common mode (`S_1c = 0`). Both forms are in
the gate — the working one as the measurement, the floating one as a named test that records why it
reads 5e−11.

### Only the quadrature coupler is unitary, and that is a theorem

`Coupling` alone sets the split and it is lossless: `t = √(1 − c²)`, so a 20 dB coupler's 0.0436 dB
of main-arm loss comes out of the arithmetic rather than out of a parameter. `IL` is a loss **added**
on top, and it scales all three transmission paths — through, coupled AND isolated — because that is
what keeps `Directivity` meaning what it says; scaling only the first two would quietly turn a 25 dB
directivity into a 23 dB one.

At `Phase = 90` the matrix is unitary. At 0 or 180 each ROW is still of unit norm (energy-consistent
under any single-port excitation) but rows 1 and 4 stop being orthogonal, so it is not
simultaneously realisable — **a lossless, matched, reciprocal four-port with directivity must have
its coupled arm in quadrature with its through arm.** That is a theorem about four-ports, not a
defect in the parametrisation, and it is stamped anyway: a user is allowed to type numbers a
physical part could not have. The same rule covers a coupling above 0 dB, where `t = √(1 − c²)` is
taken as the honest imaginary `j·√(c² − 1)` via `Complex.Sqrt` rather than as a NaN that would
surface as a non-convergence with nothing attached to it.

**Open, for the owner (SYS-1's D1 left it open):** the `Hybrid180` tile ships from SYS-1 and is
seeded here at 3.0103 dB / 180°, which gives antiphase outputs from port 1 as advertised. If it is
meant to be a *realisable* 180° hybrid the antiphase belongs on the `S42` partner instead of on
`S31` (the rat-race form, `S = (1/√2)[[0,1,1,0],[1,0,0,−1],[1,0,0,1],[0,−1,1,0]]`, which IS unitary)
— but that is a different component from the one brief-sys-3 specifies, and the brief names only
`Coupler` and `Hybrid90`.

### `3.0103 dB` is not an exactly equal split, and the back-to-back gate says so out loud

Two hybrids cascaded thru-to-thru and coupled-to-coupled put everything on the second hybrid's ISO
port at −90° and cancel at its IN port — `S(iso) = 2·c·t·e^(−j90°)`, `S(in) = t² − c²`. The
cancellation is a *difference of two equal terms*, which is what makes this the one gate that
catches a sign error no single-block measurement can. At the tile's own 3.0103 dB the residue is
`t² − c² ≈ 1e−8` rather than zero, because 3.0103 dB is 4e−8 away from `20·log10(1/√2)`; the test
computes `t² − c²` itself and asserts the residue **equals** it at both spellings rather than hiding
it behind a tolerance. `|S21| = 1` and `arg = −90°` hold to 1e−12 either way.

### Registry and elaborator wiring

- `Coupler` is ONE engine component for **three** tiles (`Coupler`, `Hybrid90`, `Hybrid180`),
  separated by two seeded numbers — the `Mixer`/`MixerD` and `Switch`/`SwitchD` arrangement. The
  hybrids deliberately keep their **own** instance prefix (`HYB`) rather than sharing `CPL`: a user
  does not swap a hybrid for a directional coupler mid-design, and `HYB1` is the name they expect.
- `Circulator`'s `Direction` is an enum NAME and needs the Switch's `OffState` treatment for the
  same reason (a bare identifier either fails to parse or resolves against a global that shares its
  spelling). `ResolveSwitchParameters` became `ResolveEnumNamedParameters(inst, scope, params
  string[])` rather than gaining a second copy.
- Three more entries in `ExpectedPortPairNets` — 6, 6 and 8 nets. No new allow-list line: the
  generalised message from SYS-2 already covers them.

### Gates

`tests/Core.Tests/Devices/SystemBlockSMatrixTests.cs` (every S entry from the dB and degree values,
at defaults and three non-ideal settings each; `det(I−S) = 0`; the modal form; the unitarity
theorem; the conjugate rule at a negative ω, stamped directly because HB does not supply one),
`tests/Core.Tests/Elaboration/PortPairNetCountTests.cs` (the three net counts and `Direction`'s
enum-name rule), `tests/Engine.Tests/Devices/SystemBlockSParamTests.cs` (S in / S out to 1e-12,
measured non-reciprocity, the simulated Y, energy balance and quadrature across a sweep including
DC, the back-to-back hybrid identity, the balun's impedance transformation and the floating-node
finding), `tests/Engine.Tests/HarmonicBalance/SystemBlockHbTests.cs` (two tones through each block
at the stated level, creating nothing — asserted RELATIVE to the carrier, because with no entry
stamped what remains is the HB solve's own ~2.5e−11 floor and a stamped 200 dB term would sit five
orders above it), `tests/Ui.Tests/SystemBlockElaborationTests.cs` (a freshly placed tile elaborates
into the model its glyph promises — the only thing that exercises registry defaults, the angle unit,
ground-return extraction and parameter resolution together).


## Every source's `Phase` was converted twice, and its sweep path was worse (2026-08-31)

Owner instruction, immediately after brief-sys-3 reported the symptom on `P1Tone`: fix it, and check
`VTone` and `ITone` too. It turned out to be **four models and three distinct defects**, all in the
same handful of lines, and none of them visible from any existing test.

### Defect 1 — the double conversion, in all four source models

**The Elaborator applies a parameter's own unit before the factory ever sees the value.**
`Units.Scale("deg") = π/180`, so an authored `Phase=45 deg` resolves to 0.7853981633974483. That is
the convention `TLineModel`'s `E` established and documents ("do NOT re-apply π/180"), and the one
brief-sys-3's `Coupler.Phase` and `Balun.PhaseImb` now follow. But `P1ToneModel`, `PnToneModel` and
`ToneSourceModelBase` (which serves `V_1Tone`/`V_nTone` **and** `I_1Tone`/`I_nTone`) each multiplied
by π/180 **again**, so a placed tone source asking for 45° drove the circuit at **0.785°**.

Every one of them now takes RADIANS and says so in its doc comment. Nothing else changed: the
registry rows already declared `"deg"`/`UnitDimension.Angle` and were right all along, as was
`UserParamTemplate`'s `Phase[{0}]` group for the added tones of a multi-tone `VTone`/`ITone`.

**Why nothing caught it, and the lesson that generalises.** `Phase` defaults to `0` on every tile and
in every one of the ~40 netlists in the test suite — grepped, not assumed: *no* netlist anywhere in
the repository used a non-zero tone-source phase. **A gate on a default parameter set cannot see a
unit-conversion bug**, because zero scales to zero. Any test of an angle-, prefix- or dB-scaled
parameter has to use a value the conversion can move, and it has to go through a placed tile or an
authored unit — a bare `.cnl` number carries no unit and passes through untouched, which is exactly
why the S-parameter gates in brief-sys-3 all passed while the UI gate failed.

### Defect 2 — a swept phase silently became zero

`ResolveToneSourceParameters` stores `_expr_{name}` for any tone parameter that references a
variable, so the model can re-resolve it at each sweep point. `BuildToneEntry` read `_expr_V` — and
never read `_expr_Phase`. `ToneEntry.PhaseExpr` was therefore only ever non-null in the one case
where `Eval` had *thrown*, so `ReevaluateFromGlobals` fell through to its `0.0` fallback: **a phase
that referenced a global was zero at every sweep point after the first.**

### Defect 3 — a variable-ref amplitude dropped the phase entirely

`BuildToneEntry` applied the phase to the initial phasor only under `if (vExpr is null)`. So
`V=vamp Phase=30 deg` — an ordinary swept-amplitude drive — stamped at **0°**, and defect 2 then
ensured the 30° never arrived on re-evaluation either.

Both are fixed by carrying amplitude and phase **separately** through `ToneEntry`
(`VResolved`/`PhaseRad`, plus `VExpr`/`PhaseExpr`) and forming the phasor in exactly one place,
`ToneSourceModelBase.Phasor`. A literal half keeps its resolved value instead of falling back to a
constant; an expression half is re-evaluated.

### The unit has to travel with the expression, and `_scale_{name}` is how

The stored `_expr_{name}` is **only the expression text** — the unit is not part of it. So
re-evaluating `Phase=phi deg` at a sweep point produces degrees where the first resolution produced
radians, and `I=iamp mA` produces amps where the first produced milliamps: a value that changes by
π/180 or by 1000 the moment the sweep moves off its first point. The Elaborator now records the
multiplier it actually applied as `_scale_{name}` beside each `_expr_{name}`, and
`ReevaluateFromGlobals` applies it.

**It is the multiplier that was applied, not `Units.Scale(unit)`,** and the difference is real:
`Evaluator.Eval` implements a **var-unit-wins** rule — if the expression references a variable that
declares its own unit, the site unit is deliberately *not* applied, because the variable's value
already carries it. `ToneParamUnitScale` reproduces that decision (via
`Evaluator.ReferencesUnitBearingVariable`) and records `1.0` in that case. A test pins it: with
`phi = 45 deg` and `Phase=phi deg`, a sweep must not scale twice.

### Scope, and what was deliberately NOT changed

- **The amplitude's unit scale was fixed alongside the phase's**, because it is the same statement in
  the same method and the mechanism is shared — leaving `V`/`I` broken while fixing `Phase` would
  have been a strange place to stop. It is called out here because it is wider than the instruction.
- **The `VTone` and `ITone` tiles now seed a hidden `Phase = 0 deg`** — raised as a recommendation
  and approved by the owner the same day. It is a product change rather than part of the fix, and
  it is the right one: an angle parameter reaches its model in RADIANS, so a `Phase` row a user
  added BY HAND and left unitless would have silently meant radians. Seeding it with the unit
  already attached takes that off the list of things anyone has to know, and matches what `P1Tone`,
  `PnTone` and `UserParamTemplate`'s added tones already do. Three tests pinned the old lists
  deliberately and were updated with it.

### The seeded `Phase` had to migrate with the rest, or a second tone would have eaten it

`MigrateToneSourceToIndexed` turns the scalar `V`/`Freq` into `V[1]`/`Freq[1]` when the "+" button
adds a second tone. It did not know about `Phase`, and the factory's multi-tone branch reads
`Phase[i]` **and nothing else** — so a scalar `Phase` left behind by the migration would have been
silently dropped, and adding a second tone would have zeroed the FIRST tone's angle. Seeding the row
without fixing the migration would have created that trap rather than closing one. It renames
`Phase` → `Phase[1]` now, unit and all, and a test drives the whole "+"-button sequence and asserts
tone 1 still stamps its 45°.

### Gates

`tests/Core.Tests/Devices/ToneSourcePhaseUnitTests.cs` — every source at a NON-ZERO phase, in both
spellings (`45 deg` and a bare `45`, which are different numbers and must stay so), plus the three
sweep-path cases: a variable-ref phase, a variable-ref amplitude with a literal phase, and a
variable that carries its own unit. `CapturingMnaContext` gained `SourceValues` and
`CurrentInjections`, without which a source model cannot be gated at all without a solve — every
existing use of it tests an admittance or a constraint row, and both RHS methods were no-ops.

`tests/Ui.Tests/ToneSourcePhaseTileTests.cs` — the half that only a PLACED TILE can gate, and the
reason it exists in that project: the row's declared unit is part of the arithmetic, and a `.cnl`
line cannot exercise it, because a bare number in a netlist carries no unit and passes through
untouched. That is exactly how the double conversion survived — the netlist gates were all
vacuous on it.


## Passive intermod on the ideal blocks (brief-sys-4, 2026-08-31)

`src/Core/Devices/System/PimOverlay.cs` plus the overlay half of `IdealSBlockModel`, wired into
`AttenuatorModel`, `CirculatorModel` and `CouplerModel` (which is also both hybrid tiles). Default
off, `ModelKind.Linear`, zero cost, byte-for-byte the SYS-3 behaviour. Nothing in `src/Engine` was
touched.

### The brief's stated mechanism does not work, and the arithmetic says so in one solve

brief-sys-4 specifies the limiter on the PORT VOLTAGES followed by the linear map,
`i = Y·φ(v)`. Under matched terminations that form's third-order escape is

```
   δb = −(1/(3·Vsat²))·(I − S)·v⁽³⁾
```

and for the **ideal circulator driven at port 1**, `v = √Z0·a·(e₁ + e₂)` — port 1 and port 2 carry
the SAME voltage, because a lossless circulator hands the whole wave from one to the other. `(I − S)`
annihilates that vector at port 2 exactly. So the block's headline PIM host produces **zero product
at its forward port**, and what it does produce comes out at port 3, the ISOLATED one. Solving the
brief's own three equations gives `b = [8.33, 0, −8.33]` — the two gates it asks for, "the specified
level comes out" and "PIM routes like the signal", both fail on the mechanism it specifies.

The same cancellation is what makes an attenuator's product vanish as its loss goes to zero, which
is the `Loss = 0` standalone generator the brief holds up as the reason to put PIM on the attenuator
at all.

### What ships instead: the limiter on the wave INCIDENT at each port

The datasheet statement is `b = S·φ(a)` — distortion generated where the signal arrives, then routed
by the block's own S. Written as the memoryless `i = f(v)` this repository requires, with
`T = (I + S)⁻¹`, `G = diag(1/√Z0)` and `ψ(x) = Vsat·tanh(x/Vsat) − x`:

```
   Y = G·T·(I − S)·G        the linear half, unchanged
   R = Re(T·G)              port voltages → the wave incident on each port
   N = −2·G·T·S             the distortion → port currents
   i(v) = Y·v + N·ψ(R·v)
```

Its closed form has no matrix inverse left in it, which is what makes the routing a theorem rather
than a tuning:

```
   δb = −(1/(3·Vsat²))·S·x⁽³⁾ ,   x = R·v
```

**Routing is carried by `N` alone and is exact for every block whatever `R` is** — a product
generated by carriers into port 1 leaves port 2 through `S21` and port 3 through `S31`. Measured:
the circulator's isolated port sits at the linear path's own isolation to six decimals at 20 and
35 dB, and at 179 dB down (double-precision noise on a 44.6 V carrier) when the isolation is ideal.
The coupler's isolated port holds 5e-16 V.

`R` is the exact incident wave whenever S is REAL — the attenuator, the circulator, the in-phase and
180° couplers. For the quadrature coupler `T` is complex and the true incident wave is a Hilbert
transform of the port voltages, which no memoryless function can compute; the real part is its
memoryless projection, and the level calibration below is done against whatever `R` actually is, so
nothing observable is left approximate by it.

### brief-sys-4's Vsat one-liner is right only for a unity-transmission block

The brief gives `IIP3 = (3·PIMPc − PIM)/2`, `Vsat = ½·√(2·10^(IIP3/10)·1e-3·Z0)`. Doing the block's
own third-order arithmetic instead — carriers into `in`, product read at `out`, every port matched,
`ρ = R·G⁻¹·(I + S)·e_in` — gives

```
   Vsat² = Pc^(3/2)·|Λ| / (2·√P_im3) ,    Λ = Σ_q S[out,q]·|ρ_q|²·ρ_q
```

which **reduces to the brief's line exactly when |Λ| = 1**, i.e. a unity-gain box with `R` exact.
`|Λ|` is what the brief's version is missing: a block that attenuates, splits or projects must
distort correspondingly harder to put the stated ABSOLUTE level on its output, and working out by
how much is not the user's job. Gated over 0.01 → 30 dB of pad loss, where the stated −110 dBm comes
back at −110.0000 at every one; the brief's own line would be 30 dB out at the top of that range.

`Vsat` is in incident-wave units (√W), not volts. Converting to volts at a `Z0` port multiplies by
`√Z0`, which is exactly why the brief's volt-based line looks different and is the same statement.

### `tanh(u) − u` must not be computed as written

It IS the whole nonlinearity, and it is a difference of two nearly equal numbers at every drive a
PIM specification describes: a part quoted at −110 dBm against +43 dBm carriers runs at `u ≈ 3e−4`,
so the answer is ~1e−8 of each term and eight of the sixteen digits go. `PimOverlay` uses the series
`−u³/3 + 2u⁵/15 − 17u⁷/315 + 62u⁹/2835` below |u| = 0.1, where its next term is ~1e−12 of the result
and there is no cancellation in it. ψ′ needs no such care: `sech²(u) − 1 = −tanh²(u)` exactly.

### ψ′(0) = 0, so an S-parameter run with PIM on is EXACT, not merely close

The brief asks for agreement with the linear path at 1e−12 with PIM set 60 dB below where it could
matter. It is exact at **every** level, and for a reason worth having rather than a tolerance:
`ψ = φ − x` has `ψ′(0) = 0`, so linearising at the zero-bias operating point gives `Y` with nothing
added to it whatever `Vsat` is. Measured across eight block variants × five frequencies: worst
|ΔS| < 1e−12 between a −170 dBm and a −40 dBm specification.

That equivalence is also the sharpest available check on the `Y` derivation, because it compares an
admittance stamp against the wave constraint the same S was written into. A dropped `√Z0`, a
transposed inverse or an `(I−S)/(I+S)` the wrong way round survives every amplitude test and dies
there.

### The quadrature bucket's sign is fixed by which matrix it carries

`H[2](ω) = +j·sign(ω)`, because the bucket holds `Im(Y)` and `Im(N)`: for ω > 0 the halves recombine
as `Re(Y) + j·Im(Y) = Y` and for ω < 0 as `conj(Y)`, which is the family's own
`S(−ω) = conj(S(ω))`. brief-sys-4 writes `+j·sign(ω)` in its mechanism and brief-sys-series writes
`−j·sign(ω)` in its overview; the two disagree, and the sign is not a convention to pick — the wrong
one passes every amplitude test and fails only `arg(S31) − arg(S21) = −90°`, which it does at nine
decimals at every swept frequency.

### THE OPEN ITEM: a complex-S block's PIM is wrong in a MULTI-TONE HB, and it is an engine gap

**`HbNewton2D` and `HbNewtonNd` read `NonlinearResult.Terms` nowhere at all.** Only the single-tone
`HbNewton` honours weighting buckets (HbNewton.cs:349, :783, :811, :980). So in a two-tone run — the
analysis passive intermod exists for — a 90° hybrid with PIM on loses its `Im(Y)`, and what is left
is `Re(Y)`, which for the ideal quadrature hybrid is **exactly zero**: the block becomes four open
circuits. Measured, not argued: +43 dBm of drive delivers −276 dBm to the through port, and the
source node reads +49 dBm (the open-circuit voltage, 6 dB up) instead of +46.

The failure is loud rather than plausible, and it is gated as a known gap by
`PassiveIntermodHbTests.The90DegreeHybridsQuadratureHalfIsLostInAMultiToneRun`, which FAILS with
instructions the day the gap closes. Every block with a real S — the attenuator, the circulator,
the in-phase and 180° couplers — is unaffected and fully gated.

This is not a defect in the formulation, so brief-sys-series' "stop and report" applies rather than
its "if a block appears to need an engine change the formulation is wrong". The fix is to mirror
`HbNewton`'s bucket handling into the two multi-tone loops; **it would also repair the SDD's own
user-defined `H[w]`, which has exactly the same gap today** and is the larger reason to do it.
`src/Engine` is out of scope for this series, so it is reported and not done here.

### The `Loss = 0` standalone PIM generator cannot exist, and the nearest thing is 0.01 dB

A perfectly matched 0 dB attenuator is a wire; a wire has no Y; a component with no Y cannot be
written as the memoryless `i = f(v)` that every nonlinearity here is. `det(I + S) = 0` **exactly** —
a theorem about the object, not a limit of the implementation. Refused at construction by name, with
the remedy in the message: give it a small loss (0.01 dB is 0.1% of amplitude and invisible on any
plot) or a finite return loss, either of which lifts the degeneracy. A 0 dB coupler is a swap of port
pairs and is refused for the same reason.

**The remedy has a measured cost worth knowing.** `T = (I + S)⁻¹` diverges as the pad approaches an
ideal through (|T| ≈ 435 at 0.01 dB), and what `T` amplifies is the block's own product fed back into
its own argument, so the level error scales as `|T| × (product/carrier amplitude ratio)`:

| pad loss | −153 dBc (a datasheet part) | −100 dBc | −90 dBc |
|---|---|---|---|
| 0.01 dB | +0.0014 dB | +0.66 dB | +2.40 dB |
| 0.1 dB  | +0.0001 dB | +0.063 dB | +0.20 dB |
| 1 dB    | 0.0000 dB  | +0.0056 dB | +0.018 dB |
| 3 dB    | 0.0000 dB  | +0.0012 dB | +0.0039 dB |

At any level a passive part is actually specified at it is invisible, and one decibel of loss — still
an electrically negligible pad — removes it everywhere. Held by
`PassiveIntermodHbTests.ANearlyLosslessPadStopsBeingExact_WhenTheProductStopsBeingPassive`, which
gates the numbers in both directions so the effect cannot quietly grow. The underlying statement is
structural and worth keeping: **for a near-lossless two-port a memoryless model cannot separate the
incident wave from the reflected one**, because the port voltage alone does not distinguish them —
the same degeneracy, seen from the other side.

### `-190 dBm` is the off threshold, and borrowing the family's 150 dB would have been a bug

The rest of this family switches a non-ideality off at 150 dB, but that number is a SUPPRESSION — a
ratio, and 150 dB of it is beyond any part. A PIM figure is an ABSOLUTE level, and −150 dBm is an
ordinary claim for a good passive part; a −150 dBm threshold silently switches off a specification
the user meant. −190 sits 10 dB inside the −200 dBm default, exactly as `MixerModel`'s 90 dBm sits
inside its 100 dBm one.

**This was caught by a wrong answer, not by review.** A −150 dBm product read back as exactly zero
and was written up as a double-precision floor around −190 dBc — a plausible story, and false. With
the threshold moved, −160 dBm against two +43 dBm carriers (−203 dBc) comes back at −160.0000014.
There is no floor anywhere near there.

### Refusing PIM where it cannot live is one check at the factory's entry point

`Balun` and `Switch` are excluded by their own briefs' decisions; `Filter` and `Duplexer` will be,
because their S is frequency-dependent and a memoryless nonlinearity cannot be attached to a rational
transfer function inside one component. Those two do not exist yet, so a per-creator check would have
to be REMEMBERED when they land. `ComponentModelFactory.RefusePimWhereItCannotLive` runs once for
every type before dispatch and names whatever it caught, with the honest remedy: place an attenuator
with a small loss and the PIM specification in front of the block.

### Two smaller things that were easy to get wrong

- **`HasEvaluateInto` must be false when the block has a quadrature bucket.** The base
  `ComponentModel.EvaluateGrid`'s `EvaluateInto` shortcut fills I/Q/Dg/Dc and carries no `Terms` —
  correct for every model that took it before, silently lossy for one that has a bucket. The
  overlay opts into the fast path only when `HasQuadrature` is false.
- **The ideal 90° hybrid is an OPEN CIRCUIT at DC**, and that is recorded rather than papered over
  as the brief asks. `Z0·Y = j·(2t·Q − P)` is purely imaginary, so with `H[2](0) = 0` the block
  contributes nothing to the DC Jacobian. Every netlist that terminates its ports resistively solves
  normally; a hybrid port wired only to reactances floats, which is the ordinary floating-node case
  the DC engine's own gmin already covers and not a special case of this block. No conductance was
  added anywhere.

## The ideal power amplifier (brief-sys-5, 2026-08-31)

`AmplifierModel` (`Amp`), the one block in the system-level family with a nonlinearity of its own.
Two ports, four nets, no DC power consumption of any kind — no bias pin, no supply, no efficiency,
no thermal node, per the owner's specification.

### brief-sys-5's 9.6 dB compression figure is the CUBIC's number, not `tanh`'s

The brief states P1dB falls at `IIP3 − 9.6 dB`, calls that "the tanh limiter's own value", and gates
it "within 0.2 dB". **All three parts cannot hold together.** Computed from `tanh`'s own describing
function `(2/π)∫₀^π tanh(u·cos θ)·cos θ dθ / u = 10^(−1/20)`:

| limiter | 1 dB backoff from the intercept | at the OTHER's backoff |
|---|---|---|
| `tanh` (what this family uses) | **−8.9625 dB** (u = 0.712697) | — |
| `a₁x − a₃x³` (the textbook cubic) | −9.6357 dB (u = 0.659542) | `tanh` is only **0.868 dB** down there |

The two differ by 0.673 dB, more than three times the brief's own tolerance, so the gate as written
would fail on a correct model. The gate that shipped computes both numbers itself, by bisection on
that integral, and asserts the value `tanh` actually has —
`AmplifierHbTests.TheOneDbBackoffIsTanhsOwn_NotTheCubicsThatTheBriefQuotes`. Measured through the
real solver at three gain/intercept combinations, the compression at `IIP3 − 8.9625 dB` is
**1.0000 dB to four decimal places.**

The same fifth-order term shifts IM3 below the two-tone extrapolation, which decides how far down a
gate has to drive: **−0.058 dB at 30 dB below the intercept, −0.18 at 25, −0.56 at 20, −4.7 at 10.**
30 dB is what the 0.1 dB gates use.

### The brief's two closed forms are the MATCHED special case, and using them literally would have
### made return loss silently change the gain

brief-sys-5 gives `i_in = v_in/Zin`, `i_out = (v_out − G·ψ(v_in))/Zout` with
`G = 2·√(10^(Gain/10)·Zout/Zin)`, and separately lists `RLin`/`RLout`/`S12` as parameters. Those are
not compatible as written. A Thevenin source of `G·ψ(v_in)` behind a resistance chosen to produce a
stated `S11` and `S22` delivers

```
   |S21| = √(10^(Gain/10)) · (1 + S11) · (1 − S22)
```

so a 20 dB amplifier given a 10 dB INPUT return loss becomes a **22.4 dB** amplifier and one given a
10 dB output return loss becomes **17.3 dB** — neither of which the user typed, and a datasheet
states gain and return loss as independent measurements. A finite `S12` breaks it further: with a
reverse path the port resistances no longer produce the stated `S11`/`S22` at all.

What shipped instead: the block's S-matrix is the four numbers the parameters name, and its
admittance is derived from it in closed form for the 2×2 case,
`ỹ = (I + S)⁻¹(I − S)`, `Y[p,q] = ỹ[p,q]/√(Z0_p·Z0_q)`. **It reduces to the brief's four terms
exactly** when `S11 = S22 = S12 = 0` — `Y11 = 1/Zin`, `Y22 = 1/Zout`, `Y21 = −G/Zout` with the
brief's own `G` — and every entry is the typed number at every combination.
`AmplifierSParamTests.AStatedReturnLossAndIsolationComeBackAsThemselves` gates the gain across the
mismatched rows, which is the assertion that would fail on the literal form.

`Vsat` carries the same correction, `× (1 + S11)`: an intercept is referred to AVAILABLE input
power, and the port voltage at a given available power is `(1 + S11)` times its matched value. The
factor is exactly 1 at the default return loss, where the expression is the brief's bit for bit.
It is exact for a unilateral amplifier at any `RLin`; with a reverse path the input voltage also
depends on what the load reflects, and no memoryless coefficient can carry that.

### The amplifier is `Linear` on the INSTANCE at its default intercept — the brief says
### `ModelKind.Nonlinear`, and the brief's own gate cannot be met that way

"Ideal is exactly linear: at IP3 = 200 a single tone driven hard produces no harmonics at all —
**assert their absence, not their smallness**." Absence is only available if the block never enters
the nonlinear partition. So `Kind` is read off the instance, exactly as `IdealSBlockModel` already
does for PIM: with `IP3` at its default the amplifier takes the family's wave-constraint stamp,
costs nothing in HB, and `nl.NonlinearComponents` is EMPTY. +20 dBm into a 20 dB amplifier gives
40.000000 dBm at the fundamental and an exact double `0.0` at every harmonic 2 through 5.

`IdealSBlockModel.Kind` stayed `sealed` and grew one hook, `protected virtual bool
HasOwnNonlinearity`, so the rule the base owns is still "a block is Linear unless SOMETHING in it is
not" and all the routing hangs off that one answer rather than off which mechanism supplied it.
`Stamp`'s early-out is now `if (Kind is ModelKind.Nonlinear) return;` rather than a `_pim` test.

**The "off" test reads the number the user TYPED, before `IP3Ref` is applied.** 200 dBm
output-referred on a 20 dB amplifier converts to 180 dBm input-referred, which is BELOW the 190 dBm
threshold — a threshold applied after the conversion would leave a freshly placed amplifier
nonlinear, carrying a limiter nobody asked for, on the default document.

### The one refusal belongs to the NONLINEAR form only

`det(I + S) = (1+S11)(1+S22) − S12·S21` vanishes when the reverse loop gain reaches unity. A
component with no Y cannot be written as the memoryless `i = f(v)` every nonlinearity here is, so
that is refused by name at construction — but **only when an intercept was also asked for.** The
LINEAR amplifier stamps the definition of S and has no such degeneracy, and refusing it too would
refuse a configuration that stamps and solves perfectly well. That is the honest split: an
oscillator has a small-signal S-matrix and does not have a memoryless large-signal one.

### The limiter and the intercept arithmetic are now one implementation

`ThirdOrderLimiter` (internal, `src/Core/Devices/`) holds `SaturationVolts(iip3Dbm, zRef)` and the
`tanh` limiter with its slope; `MixerModel` and `AmplifierModel` both call it. The expressions were
moved verbatim, so the mixer is **bit-identical** — held both by its own unchanged suite and by
`AmplifierModelTests.TheMixerAndTheAmplifierLimitAtExactlyTheSameScale`, which compares the two
models' scales with an EXACT `Assert.Equal(double, double)` rather than a tolerance. Only the "off"
threshold stayed per-component, because the sentinel differs: the mixer's `IIP3` default is 100 dBm
(threshold 90), the amplifier's `IP3` is 200 (threshold 190).

### `Assert.Equal(expected, actual, 1)` is not "within 0.1 dB"

xUnit's decimal-places overload ROUNDS both sides, so it refused a measured −60.0578 dBm against a
predicted −60.0000 — an error of 0.058 dB, comfortably inside the brief's 0.1 dB, and not noise but
`tanh`'s fifth-order term at the chosen backoff. Every dB comparison in `AmplifierHbTests` goes
through a `NearDb(expected, actual, tolDb, what)` helper that compares a difference against a
tolerance stated in dB, and prints the signed error either way.

### Gates

`tests/Core.Tests/Devices/AmplifierModelTests.cs` (S-matrix from the typed dB, the brief's own
voltage-gain algebra done in the test, the limiter law, both intercept references, the refusal, the
mixer bit-identity), `tests/Engine.Tests/Devices/AmplifierSParamTests.cs` (S21 at three gains × three
`Zin`/`Zout` combinations to 1e−9 through a real solve; unilateral and matched with nothing stamped;
the linear and linearized stamps agreeing to 1e−12; flat from 1 Hz to 100 GHz),
`tests/Engine.Tests/HarmonicBalance/AmplifierHbTests.cs` (the intercept both ways and agreeing, the
3:1 slope over 15 dB, the compression point, absence of harmonics at the default, and a
pad/amplifier/pad cascade against the standard cascade formula computed in the test), and
`tests/Ui.Tests/SystemBlockElaborationTests.cs` (a freshly placed tile, including that `IP3Ref`
survives as an enum NAME through extraction and elaboration).

## The ideal filter and the duplexer (2026-08-31)

`FilterModel` (`Filter`) and `DuplexerModel` (`Duplexer`), with the polynomial core in a new
**`src/Core/Systems/`** folder — namespace `CircuitRF.Core.Systems`, three files:
`FilterPrototype` (the response families), `FilterNetwork` (the transformations and the flat
insertion loss), `EllipticPrototype` (the one family needing new mathematics). The models stay in
`src/Core/Devices/System/` with the rest of brief-sys-2's family.

### The prototype is evaluated at a transformed Ω, never as a transformed polynomial

The response is `S11 = α·F(jΩ)/E(jΩ)`, `S21 = β·P(jΩ)/E(jΩ)`, `S22 = −α·F(−jΩ)/E(jΩ)`, with the
lowpass/highpass/bandpass map supplying Ω from ω. **The bandpass transformation doubles the degree
and nothing in the code doubles**: the degree lives in the map, so an `Order = 3` bandpass evaluates
the same four-coefficient `E` a lowpass does. That is also what keeps every prototype-level relation
valid after the transformation — Ω is real for real ω under all three maps.

**`S22` is derived rather than asserted.** Losslessness plus reciprocity gives
`S22 = −conj(S11)·S21/conj(S21)`; with real coefficients `conj(X(jΩ)) = X(−jΩ)`, and with `P` even
(true of all five families) the `P` factors cancel and what is left is the line above. Writing
`S22 = ±S11` from the parity of `F` gives the same answer for the all-pole families and is a parity
assumption that holds until it does not. The gate that catches a wrong `S22` PHASE is column
orthogonality, `S11·conj(S21) + S21·conj(S22) = 0`; every magnitude comparison in the file is blind
to it.

**One `SpectralScale` computed from leading coefficients replaces five per-family normalisations.**
`λ` in `Q(s) = λ·E(s)E(−s)` is `Q[0] / (E·E(−s))[0]`, and `β = 1/√λ`, `α = ε·β`. Getting it wrong
scales the whole response rather than distorting it, which is exactly the error a family-by-family
derivation makes and a closed-form magnitude gate then catches at every frequency at once.

**`F`'s sign is a free choice and is pinned.** Negating `F` gives the dual network — equally
lossless, equally reciprocal, the same magnitudes. It is normalised to a positive leading
coefficient so a highpass at DC is an OPEN at port 1 (the series-first ladder), and `P` is
normalised on its LOWEST-order term so a lowpass at DC is `S21 = +1` rather than `−1`. The two ends
of `P` can disagree in sign, so which end is chosen matters.

### Four of brief-sys-6's gates are not true as written

Each is now gated on what IS true, with the reason in the test's own remarks.

1. **"Every family reaches 20·n dB per decade far into the stopband" — three of five.** Inverse
   Chebyshev and elliptic put their transmission zeros on the jω axis, which is what buys the sharp
   transition; the price is that the far stopband LEVELS OFF at the stated floor. At even order the
   ultimate slope is 0 dB/decade; at odd order it is 20 dB/decade for **every** n, because only the
   one zero that went to infinity is left. Gated on the floor instead, and the odd/even split is its
   own test.
2. **"Highpass at ω = 0 is an exact open" — not at even order for those same two families.** An
   even-order inverse Chebyshev highpass at DC is a `−Astop` pad, not an open, and its bandpass
   likewise. It is still exactly lossless there, which is what the replacement gate asserts.
3. **"The passband has exactly n ripple extrema" — the honest count is two numbers.** Over the whole
   prototype passband `[−1, 1]` there are exactly `n` touches of 0 dB (the reflection zeros, the
   "n ripples") and exactly `n + 1` of `−Ripple`, two of which are the band edges; the interior
   turning points number `2n − 1`. All three are asserted, so the arithmetic is visible rather than
   folded into one number that could be right for the wrong reason.
4. **"Each duplexer arm reproduces the standalone filter's S21 to 1e-9 in its own passband" — the
   measured disagreement is up to 0.144 in amplitude.** See below.

Also: **`Fc` means a different thing for inverse Chebyshev.** It is the STOPBAND edge (where `Astop`
is first met), not the passband edge — the standard convention for the family, and the reason it is
excluded from the "elliptic reaches the floor soonest" comparison: at Ω = 1 it is there by
definition, and the two families are not being asked the same question.

### The duplexer's arms are loaded by the far arm's REACTANCE, not by an ideal open

brief-sys-6 says "the rational reflections here do carry the right phase, so the ideal junction
behaves". Measured: an out-of-band ideal bandpass arm has `|S11| = 0.999999967` — nothing is
dissipated, which is what "ideal" buys — but its ANGLE is **−23.0°** at the neighbouring band's
centre, and a unit-magnitude reflection at a non-zero angle is a reactance. It loads the junction, so
the near arm's transmission is not the standalone one: worst `|ΔS21|` **0.144** for adjacent bands
(0.90–1.00 against 1.10–1.20 GHz, order 5), **0.040** when the bands are widely separated
(0.80–0.90 against 1.30–1.40 GHz), where the angle has walked in to −7.7°. That is the same
statement as "a real duplexer needs a phasing line", and it is why placing a `TLIN` in the arm is the
right answer rather than a hidden length inside the component. It also shows in the ANT match:
**−12.0 dB** worst across the TX band, **−11.5 dB** across the RX band.

**The exact gate replacing it: the duplexer equals two `Filter` instances wired onto the same net,
bit for bit, at every frequency.** That is the executable form of "no new mathematics at all", and
unlike the brief's version it is exact. `IdealSBlockModel.Stamp`'s body was lifted into a public
static `StampWaveConstraints(mna, nodes, s, z0, branchOut, nodeOffset, portNodes)` so the duplexer
calls the family's own stamp twice with `portNodes` `{0,1}` and `{0,2}` — four branch currents, no
internal node. `DuplexerModel` derives from `ComponentModel`, not from `IdealSBlockModel`: that class
is "one component, one S-matrix" and this component is two.

Measured TX→RX isolation at order 5 with the default band plan: **−88.7 dB** at the TX band's lower
edge falling to **−63.1 dB** at its upper edge, and −57.2 dB at the RX band's lower edge — worst
**−57.2 dB** across both bands. There is deliberately no `Isolation` parameter; the only thing
derivable is that the leakage cannot beat the far arm's own rejection at the same frequency (it
tracks it about 6 dB better, the junction having divided the drive), and that is what the test
asserts.

### Elliptic shipped (D6): the degree equation is solved through the NOME, not by iteration

`EllipticPrototype`, ~170 lines, and it did not need the pole/zero formulas at all — only the ZEROS
of the elliptic rational function, handed to the same `FromCharacteristic` road the other families
take. With `k1 = ε_p/ε_s` and `k = 1/ξ`, the degree equation `n·K(k')/K(k) = K(k1')/K(k1)` becomes
`q = q1^(1/n)` in the nome `q = exp(−π K'/K)` — one exponential, written as
`exp(−π·K1'/(n·K1))` rather than `Pow(q1, 1.0/n)` so a demanding specification cannot underflow `q1`
to zero and return an infinitely selective filter in silence. `k` comes back through the
theta-function series `k = (θ₂/θ₃)²`. What was needed: `K(m)` by AGM, Jacobi `cd` by the DESCENDING
Landen transformation (the ascending series loses accuracy exactly as `k → 1`, which is the
selective end every elliptic design lives at), and the zeros `ζ_m = cd((2m−1)K/n, k)` with the poles
at `ξ/ζ_m` from the inversion relation. Roughly a third of a day, well inside the brief's stop-and-
report threshold.

Verified against the two properties that DEFINE the family and nothing else shares: passband
equiripple in exactly `[−Ripple, 0]` dB and stopband equiripple at exactly `−Astop`, both to 6
decimal places, at n = 2…7 and four ripple/floor combinations. Measured transition edges (which are
`ξ`, so finding them also checks the degree equation was solved): 0.1/40 dB gives ξ = 12.82 at n = 2,
3.52 at n = 3, 1.66 at n = 7 for 0.1/80 dB. At n = 5 and 0.1/60 dB it reaches −60 dB at ω = 2.04
against Chebyshev's 3.41, Butterworth's 3.98 and Bessel's 15.57.

**n = 1 elliptic has no finite transmission zero** and is the first-order Chebyshev; its stopband
edge is at ξ = ε_s/ε_p, which is 655 for 0.1/40 dB. Not a defect — there is nowhere to put a zero.

### Bessel needed a spectral factorisation of its own, and the double zero is divided out exactly

There is no `N(Ω)` to rewrite, so `F` comes from Feldtkeller directly:
`F(s)F(−s) ∝ E(s)E(−s) − β²`. That polynomial is even and vanishes to SECOND order at `s = 0` —
`|S11|` is zero at DC and the zero is double, as a spectral density's axis zero must be. The two
factors of `s` are divided out by dropping two coefficients (the constant term is zero by
construction, the `s¹` term by evenness) rather than left for a root finder to discover
approximately; what remains has no imaginary-axis roots, which is the case `MatchPrototypes.Hurwitz`
handles cleanly. Measured `τ(0) = 1.000000000000` at every order 1…7.

**Bessel's gate is group delay and a fixed tolerance is the wrong shape for it.** The delay error
goes as `ω^2n`, so any single number is slack at high order and impossible at n = 1, where
"maximally flat" buys exactly one vanishing derivative and `τ = 1/(1+ω²)` is already 2% down at a
sixth of the corner. It is gated instead as a measurement across orders — the band over which the
delay stays within 1% of DC must GROW with every order, and does: **0.101, 0.564, 1.205, 1.934,
2.713, 3.525, 4.362** for n = 1…7.

### Reuse, and what moved

`MatchPrototypes.Hurwitz` is now `public` — the filter's `E(s)` is the Hurwitz factor of `|E(jω)|²`
in precisely the sense the match synthesis already meant, so a second root-find that could disagree
with it was not written. `ChebyshevT` and `ReverseBessel` MOVED from `MatchPrototypes`'s privates
into `MatchPoly` (both were needed twice over); two copies of a three-term recurrence would never
disagree noisily, they would disagree in the last digits of one family's stopband.

### `Zin ≠ Zout` is free, and that is the whole reason this is not a ladder

A doubly-terminated LC ladder cannot take an arbitrary source/load impedance ratio — the termination
ratio is fixed by the family and the order — so a synthesised filter would have had `Zin`/`Zout` as a
constrained pair with refusals attached, which is `MatchModel`'s territory and already lives there.
Stamped as its S-matrix, the reference impedances are simply what S is defined against: any pair
works, there is no feasibility question, and the block is a lossless impedance transformer in the
bargain. Gated by measuring a `Zin = 50`, `Zout = 25` filter in a UNIFORM 50 Ω system and
renormalising it back onto (50, 25) with arithmetic written in the test.

### `IL` dissipates rather than redistributing

It multiplies `S21` and leaves `S11` and `S22` alone, so `|S11|² + |S21|² < 1` — which is what a real
filter's loss does. A lossless run is exactly unitary, to 1e-12, across every form and family.

### Two smaller things worth not rediscovering

- **A leading `+` continuation line in a `.cnl` is silently DROPPED.** The continuation marker is a
  TRAILING BACKSLASH; a line starting `+` has no `Type:Name` colon, so `ParseInstanceLine` returns
  without a word. Cost an hour here: every parameter on the continuation defaulted, and because the
  tile defaults and the factory fallbacks agree on purpose, the first several gates PASSED on the
  wrong netlist. Not fixed — it is `CnlReader`'s business, not this brief's — but it is the same
  shape as the trailing-`;`-comment trap already recorded above.
- **`Assert.Equal(double, double, int)` rounds both sides**, so two doubles differing in the last bit
  can fail at 12 places when they straddle a rounding boundary. Unitarity is asserted with an
  explicit `Math.Abs(power - 1.0) < 1e-12` instead.

### Gates

`tests/Core.Tests/Devices/FilterPrototypeTests.cs` (every family against its textbook formula in
TRIGONOMETRIC form — `cos(n·acos x)` / `cosh(n·acosh x)`, a different route from the recurrence the
production code builds from; the structural counts; the transformations; the limits; the refusals),
`tests/Core.Tests/Devices/FilterModelTests.cs` (which entries reach the matrix and where, the
duplexer's four branches on a shared node, the conjugate-symmetry rule),
`tests/Engine.Tests/Devices/FilterSParamTests.cs` (the measured S against the stated S at 11
family/form pairs; unitarity; the three DC limits SOLVING; the renormalisation; the duplexer as two
filters, bit for bit),
`tests/Engine.Tests/HarmonicBalance/FilterHbTests.cs` (a two-tone signal through bandpass, highpass
and the duplexer, each tone at the level the response states AT THAT FREQUENCY, and nothing created
anywhere), and `tests/Ui.Tests/SystemBlockElaborationTests.cs` (a freshly placed tile, including that
`Response` and `Form` survive as enum NAMES through extraction and elaboration, four times over on
the duplexer).

---

## Complex port impedances on the ideal system blocks, and the Circulator's per-port detune (2026-08-31)

Owner report: a `Filter` with `Zin = 5+j100` "does not seem to respect a complex Zin", and a
suspicion that `Zout` and other components were the same. Both true, plus a design question about
detuning a circulator's match that turned into a component parameter.

### The bug is not "the imaginary part was dropped" — the whole value was

Every creator in `ComponentModelFactory` reads its numbers through a local helper of the shape

```csharp
double P(string name, double fallback) =>
    parameters.TryGetValue(name, out var v) && v.Kind == ValueKind.Real ? v.AsReal() : fallback;
```

`5+j100` parses (the tokenizer has an implicit-`j` rule, `Parser.cs:95`), evaluates, and arrives as a
perfectly good `ValueKind.Complex` — which **misses the `Real` test and takes the fallback**. The
filter was therefore built at **50 Ω**, not at 5 Ω, not at |Z|, not at anything the user could
recognise as a rounding of what they typed. Nothing was reported. The only symptom available is a
response that does not look like the one asked for, which is the hardest kind of bug to attribute:
the number is on screen, in the netlist, in the `.cws`, and gone by the time the model exists.

**The general lesson, which outlives this component family: a "read it if the kind matches, else use
the default" helper turns a TYPE error into a SILENT VALUE substitution.** `RefuseComplexWhereOnlyRealFits`
now refuses a complex value on a parameter that can only be read as a real number, naming both — for
a positive list of audited types, deliberately not blanket, because `Z_Port`, `Chain` and the tone
sources forward every unrecognised numeric parameter into the expression scope and legitimately
consume complex ones there.

### Reference impedance vs presented impedance: the parameter NAME decides

Kurokawa power waves — which `SParameterEngine` already extracts S with (`SParameterEngine:450`) —
have `S_pp = 0` ⟺ `Z_seen = conj(Z0_p)`. So the reference impedance and the impedance a port
PRESENTS differ by a conjugate, and one of them has to be the number a user types. The rule is:

- **`Z0`** (Atten, Switch, Circulator, Coupler; Balun's `Zunb`/`Zbal`) **is the reference.**
- **`Zin`/`Zout`** (Filter, Amp) **and the Duplexer's `Zant`/`TxZ`/`RxZ` are what the port
  PRESENTS.** Those three classes conjugate on the way into `base(...)`, and that is the only place
  a conjugate appears in the family; the stamp is the textbook form either way.

So `Zin = 5+j100` presents 5+j100 and is conjugate-matched by a `Term` at `5-j100`. **This was
shipped the other way round for a few hours and the owner caught it** — worth recording because the
argument for the other convention is genuinely attractive (a `Term` at the same value as `Zin` then
reads the prototype's own S11, block and measuring port agreeing by construction) and it is wrong
anyway: a parameter called `Zin` names an input impedance, and a designer knows what their part
presents, not what reference someone defined its S against.

**Measured, with a real 50 Ω probe, so the answer depends on no convention:** `Zin = 5+j100` →
port 1 presents 5.013 − j99.772 under the first spelling and 5.013 + j100.228 under the shipped one.
That measurement is now `ZinIsTheImpedanceThePortPresents`, and it is the assertion that decides
which way the family goes — a comparison of the model with its own definition cannot.

### The stamp generalises with one conjugate, and must not move a real block by an ulp

`(v_p − conj(Z0_p)·i_p)/√Re(Z0_p) = Σ_q S_pq·(v_q + Z0_q·i_q)/√Re(Z0_q)`. Rule 2 becomes
`Re(Z0) > 0`. Rule 1 (`S(−ω) = conj(S(ω))`) extends to the reference impedance too, since a physical
reference is conjugate-symmetric; `Z0At(ω)` is that, and is as inert on today's engine paths as rule
1 already was.

**The trap:** `-conj(Z0)/√Re(Z0)` and `-Z0/√Re(Z0)` are algebraically `-√Z0` for a real reference and
are NOT bit-identical to it. Written the general way, two existing gates went red on the last digit
(`-7.375355307487728` vs `...27`). The coefficients are now precomputed per port with an explicit
`Imaginary == 0` branch that keeps `√Z0` for a real reference, so every block that existed before
stamps the bits it always did.

### The nonlinear half refuses, and the Amp's own default puts it on that side

`PimOverlay` and `AmplifierModel`'s compression are a real `i = f(v)` built from a real admittance
matrix; a complex reference makes that admittance complex, which is a second quadrature bucket and a
second calibration rather than a wider type. `RefuseComplexZ0` refuses at CONSTRUCTION, naming the
instance and the port — and quoting the value in the spelling the USER typed
(`PortParameterIsPresentedImpedance` decides which of the conjugate pair to print, or the message
shows a sign of reactance that contradicts the screen).

**Consequence worth stating plainly: the `Amp` tile's default `IP3` is 40 dBm, not 200**, so a
freshly placed amplifier is nonlinear and a complex `Zin`/`Zout` on one is refused until `IP3=200`.
That is the scope line, and it is a test rather than a sentence.

### `IL` is unaffected by any of this, and it was checked rather than assumed

With `Zin = 5+j100` / `Zout = 20-j35`, conjugate-matched at both ends, `IL` is an exact 1:1 loss on
`|S21|`: measured −10.000000, −3.000000, −1.000000, −0.500000 dB at five passband frequencies, with
`S11` bit-identical to the lossless run. It multiplies S21 in the block's own frame, and a
conjugate-matched measurement is that frame.

### The Circulator's per-port VSWR/Ang, and why `Z0` cannot do that job

`VSWR1..3` with `Ang1..3` set that port's own `S_pp = ((VSWR−1)/(VSWR+1))∠Ang`. `VSWR = 1` means
"not stated" and the port falls back to the isotropic `RL`, so nothing changes for a design that
never touches them. Frequency-flat, which keeps the block memoryless so PIM still works over it.

**Reusing `Z0` was the obvious answer and it is wrong, for a reason that is a property of the NETWORK
and not of the matrix.** With the ideal permutation S and all three ports in `Z_L`, a wave entering
port 1 leaves at port 2, reflects, circulates to port 3, reflects again, and only then returns — so

```
Γ₁ = conj(ρ²)      with   ρ = (Z_L − conj(Z0)) / (Z_L + Z0)
```

in the block's own frame, plus a further reference change before a 50 Ω system measures it. Verified
against an independent 6×6 solve at five values of `Z0` before it was written down; the first
statement of it in these notes ("magnitude squared, phase doubled") was right about the mechanism and
wrong about the frame, and the numbers caught that.

### Gates

`tests/Engine.Tests/Devices/ComplexReferenceImpedanceTests.cs` (the presented impedance, measured;
the conjugate-vs-equal termination pair; unitarity with a complex reference; `Zout` read separately;
`IL` as a 1:1 loss; the refusals, and the parameters that are NOT refused so the check cannot be
satisfied by refusing everything), `tests/Core.Tests/Devices/CirculatorDetuneTests.cs`,
`tests/Engine.Tests/Devices/CirculatorDetuneSParamTests.cs` (what a PA on port 1 sees, and the
`conj(ρ²)` closed form for the rejected design).
