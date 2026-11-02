# Brief ENV-MEAS-1 — power through every IProbe in an envelope run, and `Zmeas` retired

**Series:** Envelope measurements · **Tag:** `R-envm1-n` · **Briefs:** 1 (this), [2](brief-env-meas-2-power-shorthand.md)
**Depends on:** the envelope analysis as merged, through ENV-PCW
**Area:** `src/Engine/Envelope/EnvelopeEngine.cs`, `EnvelopeEngineMt.cs`, `EnvelopeRunner.cs`, `EnvelopeDataSet.cs`,
`EnvelopePulse.cs`; `src/Core/Design/Analysis.cs` (`EnvelopeAnalysis.ZmeasExpr`), `src/Core/Netlist/CnlReader.cs`,
`CnlWriter.cs`, `AnalysisDirectiveSchema.cs`; `src/Design/Schematic/AnalysisSerialization.cs`; `src/Cli/Envelope.cs`;
`src/Ui/ViewModels/EnvBodyViewModel.cs`, `src/Ui/Views/Analyses/EnvBodyView.axaml`,
`src/Ui/Commands/Analysis/DuplicateAnalysisCommand.cs`; `docs/user/src/reference/simulations.md`.
Findings go in `src/Engine/RESOLVED.md`, with the UI half in `src/Ui/RESOLVED.md`.

**Rule for the whole series:** the shipped examples that run an envelope (`examples/Envelope/*`,
`examples/Pulsed Radar/PulsedPA_pCW`) open and run exactly as they do today. Every cube they produce today keeps its
name, its axes and its values **bit for bit**. New cubes are allowed. If any existing value moves, stop and say why.

---

## 0. The gap

An envelope run measures power from a node voltage alone. `Probes` is a list of nodes; the run keeps
`V[time, node, harmonic]` for those nodes and the DC supply currents, and nothing else. Every power it reports is
|V|² / (2 · `Zmeas`), with one real `Zmeas` for every probe.

| Scalar | How it is computed today (`EnvelopeDataSet.cs`) | Right when |
|---|---|---|
| `Pin_avg_dBm` | available power of the **summed** MOD_Source drives, with source 0's Re Z | one source; any source impedance |
| `Pout_avg_dBm` | \|V₁\|² / (2·Zmeas) at the **last** probe | that node is loaded by a resistor equal to Zmeas |
| `Gt_dB` | Pout / Pavs | Pout is right |
| `Pdc_W` | −V·⟨I⟩ over **every** non-zero Vdc source | one PA |
| `DE_pct` | Pout / Pdc | one PA, and Pout right |

So a load that is not a resistor equal to `Zmeas`, a probe at the drain before the output match, or two PAs on
different loads all report a wrong Pout, Gt and DE. They give no warning, and the user has no way to measure the
right number:

- **IProbes are ignored.** Every Envelope example already has `Iin`, `Iout` and `IDC` in series where a designer
  would measure. The envelope engines never read their branch currents, though they solve them at every step.
- **A complex `Zmeas` is silently truncated.** `EnvelopeRunner.Resolve` keeps only the real part, so `50+j10` runs
  as 50.
- **There is no PAE**, because nothing measures delivered input power.

`measure` lines already run against an envelope result, and against the stacked result of a parametric sweep around
one, exactly as they do for HB (`CircuitEvaluation` builds one measurement context from every analysis). The only
thing missing for an exact power is the current.

## 1. `R-envm1-1` — record every IProbe

Both engines record, at every kept step and for every `IProbeModel` in the netlist:

- the probe's branch current at every harmonic (single envelope) or mixing product (lattice). The row is
  `LastBranchIndex`, the absolute MNA row, read from the same state vector the supply currents are read from
  (`products[k].Xm1[row]`, `prods[m].Xm1[row]`).
- the voltage at the probe's **first** node, from the same vector. An IProbe is a 0 V series element, so its two
  nodes carry the same voltage.

That is all IProbes, with no setting to choose them, so placing an IProbe is the whole of "setting up" a power
measurement. A `WSProbe` is not an IProbe for this purpose; it is excluded, as in HB's `__ProbeBranches`.

The sign follows the engine's fixed convention: a branch current flows from the element's first node to its second.
So the power reported is the power flowing **through the probe from its first node to its second**. In the examples,
`IProbe:Iout Vout n6` reads the power into the load, `Iin` the power into the gate side, and `IDC` the supply power.
None of these is negative in a normal PA.

## 2. `R-envm1-2` — the cubes

Every IProbe adds to the DataSet. The `branch` axis is labelled with the IProbe instance names, exactly as HB's `I`
cube is:

| Cube | Axes | Kind | Always? | Meaning |
|---|---|---|---|---|
| `I` | `time, branch, harmonic` (or `mixIndex`) | complex | with `SaveWaveforms=yes`, like `V` | the probe current envelopes, peak phasors |
| `Pprobe_W` | `branch, harmonic` (or `mixIndex`) | real | **always** | time-averaged power per harmonic: ⟨½·Re(Vₖ·Iₖ*)⟩ for k ≥ 1 and ⟨V₀·I₀⟩ for k = 0 |
| `Pprobe_fund_W` | `branch` | real | **always** | the fundamental-band power: harmonic 1, or on the lattice the sum over the carrier products (the same set `Pavg_dBm` sums, `r.CarrierMix`) |
| `Pprobe_t_W` | `time, branch` | real | with `SaveWaveforms=yes` | instantaneous fundamental-band power, ½·Re(V₁(t)·I₁(t)*) summed over the same products, for plotting against time |

Why each of these:

- **The averages are computed in the engine, over the same window as the built-in scalars**: after the warm-up,
  and after the settle skip of a cold run (`t0` in `EnvelopeDataSet.Build`). A probe power and `Pout_avg_dBm` at a
  true 50 Ω load then agree, and a user never has to know the skip exists.
- **They are always emitted**, so a run with `SaveWaveforms=no` (the cheap one) still measures power.
- **They contain no time axis**, so a parametric sweep stacks them like any scalar: `Pprobe_fund_W[Pin, branch]`,
  `[a, b, branch]` under two sweeps. A Data Display trace of `ENV1 › Pprobe_fund_W › Iout` against `Pin` therefore
  plots output power vs drive with **no measure line at all**, which is the fewest-clicks path for one PA.
- **½·Re(Vₖ·Iₖ*) is exact power for envelope phasors**: the cross terms between harmonics average out over a
  carrier period, so the per-step product is the fundamental-band instantaneous power. The time average of the
  product is the right average, not the product of the averages. This matters for `k = 0` too, where V₀ moves with
  the modulation behind any supply resistance.
- **On the lattice, the DC product (0,0) is in the state**, so `Pprobe_W` at `"(0,0)"` gives a two-fundamental run
  a supply power for the first time. Today `Pdc_W` and `DE_pct` are absent there.

Also emit `__ProbeBranches` (axis `probe`, labels the IProbe names), as HB and DC do, so the Data Display's
signal picker treats envelope IProbes as it treats HB's.

Every existing cube (`V`, `Idc`, `PSD`, `ACLR`, `Pavg_dBm`, `Pin_avg_dBm`, `Pout_avg_dBm`, `Gt_dB`, `Pdc_W`, `DE_pct`,
the pulse cubes, the `__Env*` metadata) is unchanged in name, axes and value. **`V` keeps exactly the nodes listed in
`Probes`**; an IProbe's node voltage goes into the power cubes and is not appended to `V`, or the examples' `V` and
`PSD` node axes would grow.

## 3. `R-envm1-3` — `Zmeas` retired; envelope powers are into 50 Ω

`Zmeas` is no longer a setting. Every power the run computes from a node voltage alone (`PSD`, `ACLR`'s `Pch_dBm`,
`Pavg_dBm`, `Pout_avg_dBm`, `Gt_dB`, `DE_pct`, the pulse cubes) is into **50 Ω**, a named constant in
`EnvelopeDataSet`. The rule the user docs state is one sentence:

> Without IProbes, an envelope run reads each probe node as if it were loaded by 50 Ω. That suits a system-level
> chain of 50 Ω blocks. To measure the power into any other load, place an IProbe in series with it and read
> `Pprobe_fund_W` (or `ENV1.P("Iout")`, brief 2).

- **Reader:** `CnlReader` and the `.csch` DTO still accept `Zmeas=`. A value that evaluates to 50 is read
  silently. **Any other value** runs at 50 and adds one warning to the run (D2), naming the analysis and pointing
  at IProbes. The schema entry stays and is marked deprecated, like HB's `Sweep=`, so `check` does not report it as
  an unknown key.
- **Writer:** `CnlWriter` and `AnalysisSerialization` never write `Zmeas`. The example `.csch` files are **not**
  edited; they carry `"EnvZmeas": "50"`, which reads silently, and the key drops out the next time someone saves
  one.
- **GUI:** the Zmeas field leaves the envelope card. `EnvBodyViewModel`, `DuplicateAnalysisCommand` and
  `AnalysisClonePropertyParityTests` lose the property.
- **Engine:** `EnvelopeParams.MeasureZ` and `Plan.Zmeas` go. `EnvelopePulse.Add` takes the constant.
- **CLI:** `src/Cli/Envelope.cs` stops passing it. `explain --analysis` stops listing it.

The built-in scalars otherwise keep their arithmetic. They stay a quick look at **one** signal path: Pout at the
last probe into 50 Ω, Pin from all MOD_Sources, Pdc from every non-zero supply. The docs say so plainly, and say that
two PAs, a non-50 Ω load and PAE are measured at IProbes.

## 4. `R-envm1-4` — user docs

`docs/user/src/reference/simulations.md`, Envelope:

- Drop the `Zmeas` row from the settings table and add the 50 Ω sentence of §3 under it.
- Add the four new cubes to the **Results** paragraph.
- Rewrite `#envelope-power` around the two routes: the built-in scalars (50 Ω, one path, the caveats already there
  minus the ones about `Zmeas`), then **"Measuring power at an IProbe"**: where to place the probes (in series with
  the load, the input and each supply), the sign rule of §1, and a one-PA and a two-PA example using `Pprobe_fund_W`
  and `Pprobe_W`. The measure-line spelling is brief 2's; this brief writes the cube route only.
- Say once that a probe power is exact for any load, reactive ones included.

Follow the standing rule for the edited page: regenerate that page alone with `DocGen --page`, no figures.

## 5. Gates (one test per claim)

Envelope runs here are short: `Stop` 20 µs or less, one CW or narrowband carrier unless the claim needs NR. Run only
the envelope test classes in `Engine.Tests` and the touched `Ui.Tests` classes, once, and read the TRX.

1. **The examples did not move.** A scratch harness (not a committed test) runs `NR20_ACPR`, `NR20_ACPR_vs_Pin`,
   `NR20_2C_400MHz_single` and `PulsedPA_pCW` with a shortened `Stop` **before and after** the change, and every
   pre-existing cube compares bit for bit. Report the result in `src/Engine/RESOLVED.md`.
2. **Agreement at a true 50 Ω load:** a bench whose last probe node is a 50 Ω resistor behind an IProbe:
   10·log₁₀(`Pprobe_fund_W`·1e3) equals `Pout_avg_dBm` within 1e-6 dB.
3. **Right where the built-in is wrong:** the same bench with a 25 Ω load. `Pprobe_fund_W` equals ⟨|V₁|²⟩ / (2·25)
   at the load node, computed from `V` in the test, within 1e-9 relative. `Pout_avg_dBm` is ~3 dB off, and the test
   asserts that too, so the docs' claim is pinned.
4. **Reactive load:** an R + L load. `Pprobe_fund_W` equals the closed form ⟨|I₁|²⟩·R/2, from `I` in the test.
5. **DC power:** with the IProbe directly between the only supply and the circuit, `Pprobe_W[IDC, 0]` equals
   `Pdc_W` within 1e-9 relative.
6. **Lattice:** a two-fundamental run. `Pprobe_fund_W` equals the sum of `Pprobe_W` over the carrier products, and
   `Pprobe_W` at `(0,0)` is present and positive at a supply probe.
7. **Sweep shape:** inside one parametric sweep `Pprobe_fund_W` is `[sweep, branch]`, and inside two nested sweeps
   `[outer, inner, branch]`.
8. **`SaveWaveforms=no`:** `Pprobe_W` and `Pprobe_fund_W` present, `I` and `Pprobe_t_W` absent.
9. **`Zmeas` retired:** `Zmeas=50` reads with no message; `Zmeas=25` runs at 50 with exactly one warning; the
   writer never emits the key; `check` reports nothing for it.

## 6. Out of scope

- **The measure-line shorthand** (`ENV1.P("Iout")`): brief 2.
- **Changing the built-in scalars' arithmetic** beyond the constant: summing per-source Pin, per-PA DE, a
  delivered-power Gt. Measured at IProbes instead. See D3.
- **Recording arbitrary extra nodes** for measure lines. A measure line that names a node outside `Probes` already
  fails with a message listing the recorded nodes. An automatic scan of the measure lines is a follow-up if users
  hit it.
- **A "PA measurements" button** that writes the measure lines (brief 2, D4).

## 7. Decisions for the owner

- **D1** cube names: `Pprobe_W` / `Pprobe_fund_W` / `Pprobe_t_W` (recommended: they sort beside each other in the
  picker and say what they are), or `Pdel_W` / `Pdel_fund_W` / `Pdel_t_W`.
- **D2** an old file with `Zmeas` ≠ 50: run at 50 with one warning (recommended: a result that silently moves is the
  worse outcome), or refuse until the key is removed, or honour it silently as a hidden setting.
- **D3** with two or more MOD_Sources, `Pin_avg_dBm` sums the drives coherently with source 0's impedance (+6 dB, not
  +3 dB, for two equal in-phase sources). Leave it, documented as "one path" (recommended for this brief, since
  IProbes are the measurement for multi-PA work), or sum each source's own available power in a follow-up.
- **D4** the order of work: §1–2 (engine, cubes) as one commit, then §3–4 (`Zmeas`, docs) as a second
  (recommended), so gate 1 is run once on the engine change alone.
