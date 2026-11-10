# RF chain budget

A receive chain's budget in closed form, and its transfer to test benches of circuitRF's own System blocks whose
simulations check every number of it. The user-facing page is `docs/user/src/reference/chain-budget.md`; this note is
how it is built and how it was verified.

## 1. Architecture

```mermaid
flowchart LR
    doc[".cchain<br/>stages + system inputs"] --> io["ChainDesignIo"]
    io --> calc["ChainBudget.Evaluate<br/>(closed form, pure)"]
    parts["ChainParts<br/>each part's budget;<br/>System-block defaults from<br/>ComponentTypeRegistry"] --> calc
    calc --> ds["ChainBudget.ToDataSet<br/>per-stage cubes over a stage axis"]
    ds --> verb["circuitrf chain / MCP run analysis=chain<br/>-o budget.npy"]
    ds --> table["cascade table<br/>(Data Display Table plot)"]
    calc --> emit["ChainBenches emitter<br/>.cnl text"]
    emit -- "diagram: the chain alone" --> draw["NetlistSchematic.Build<br/>+ per-stage notes"] --> canvas["SchematicRenderer<br/>(the window's drawing)"]
    emit -- "the benches" --> write["ChainBenches.Write<br/>drawn, staged, validated,<br/>checkpointed, CellCreate"]
    win["RF Chain Budget window<br/>(Tools ▸ RF Chain Budget)"] --- table
    win --- canvas
    win --- blocks["Blocks view<br/>palette glyphs per stage,<br/>an array's branch × N"]
    win -- "Transfer to Schematic" --> write
    verb -- "--to-schematic" --> write
    write --> one["1-Tone bench<br/>S-parameter or HB noise,<br/>HB compression sweep"]
    write --> two["2-Tone bench<br/>multi-tone HB: IIP3, OIP3"]
    write --> env["Envelope bench<br/>noise + EvmSplit: SNR, EVM"]
    write --> steer["Steering bench (array)<br/>plane wave, sweeps of<br/>theta_in × theta_steer"]
    write --> comb["Combiners bench (array)<br/>the four variants side by side"]
    one --> cdd["one .cdd per bench<br/>(ChainBudgetDisplay):<br/>budget | simulated | difference"]
    two --> cdd
    env --> cdd
    steer --> cdd
    comb --> cdd
```

- **`src/Design/Chain`** — framework-free, below the firewall. `ChainDesign` and `ChainDesignIo` (the document),
  `ChainParts` (what each part does to a budget), `ChainBudget` (the cascade, and the same as a `DataSet`),
  `ChainBenches` (the transfer and the diagram). The emitter writes netlist TEXT and hands it to
  `NetlistSchematic.Build` — the drawing `circuitrf netlist --to-schematic` makes — with an `ExactPlacement` that puts an
  array's elements in rows. `Write` draws all the chain's benches, writes each into a temporary staging folder with
  `CellCreate.Create` (the function New Cell calls) and reads it back through `CellViewFileValidator`; only when every
  bench passes does it take a history checkpoint (`WorkspaceCheckpoints.BeforeWrite`) and create the cells in the
  workspace. `Diagram` emits the chain alone (ports in and out, no sources, analyses or isolators), draws it the same way
  and adds four text notes above each stage, its own and its cascaded numbers; it returns which drawn component belongs
  to which stage.
- **`src/Render/DataDisplay/ChainBudgetDisplay`** — the displays, ordinary Table plots. One per bench, of the bench's
  measurement rows, named after it, because `RunResultsWriter.AutoDisplayCandidates` opens an authored display beside
  the bench when the bench is simulated; and the window's cascade table, a Table plot over the budget `DataSet`'s stage
  axis.
- **`src/Cli/Chain.cs`** — the verb: parsing, refusals, reporting. `run analysis=chain` on the MCP server.
- **`src/Ui/Chain`, `src/Ui/Views/Chain`** — the window, a top-level tool window as the Match Designer's is: Tools ▸ RF
  Chain Budget, or a `.cchain` from the tree. It holds no budget arithmetic. The chain is drawn two ways: the
  Schematic tab, where `ChainSchematicCanvas` hands the diagram's model to `SchematicRenderer.Draw` with the renderer's
  own selection overlay; and the Blocks tab, an idealised block per stage — an `ItemsControl` of the palette's own
  `PaletteGlyphControl` symbols, an array's branch once with "× N", a combiner as its variant's levels — fed by the
  core's numbers. The cascade is a `PlotControl` over the Data Display table. A selection in either view and in the
  stage list is one `SelectedStage`.
- **`src/Core/Devices/System/CombinerModel`** — the library's N-way Combiner, an `IdealSBlockModel`: S(S,k) =
  10^(−IL_k/20)·e^(j·Phase_k)/√N, every other entry zero. It is the splitter, the direct combiner, each 2:1 of a tree
  and, with an Amp after it, the active combiner; its thermal noise comes from its S like every passive block's.

Nothing new in the engine. The benches are run by the analyses that already exist, and every check is a measurement row.

### 1.1 How it lines up with `docs/design/plugins.md`

It is a built-in tool, in one pull request, but shaped the way that note shapes a plugin, so that it could move to the
plugin route once the plugin runtime exists:

- **P-4, a function of its parameters.** `ChainBudget.Evaluate` is pure: a `.cchain` in, a result out, nothing read from
  the window or the workspace. The window, the verb and the test suite call the same function.
- **P-7, headless parity.** Everything the window does is a verb: `circuitrf chain` budgets a `.cchain` as a `DataSet`,
  `--to-schematic` transfers it, `check` reports its refusals, and the MCP server has it as `run analysis=chain`.
- **P-2, circuitRF draws every view.** The chain is a `.cnl` drawn as `netlist --to-schematic` draws one, rendered by
  `SchematicRenderer`; the cascade and every bench's comparison are Data Display Table and Rect plots (§5.1, §5.2). What
  the tool adds to the drawing is text: four `EditableText` notes per stage, which a plugin's `schematic` view (§5.4)
  has no field for today, so a plugin port would need the view to carry annotations or the notes to move into a Table.
  The Blocks view is the one drawing of the tool's own — XAML over the palette's glyphs, fed by the core — and the
  plugin vocabulary has no block-diagram view kind; a port would ask for one, or show the blocks as a Table.
- **P-5, circuitRF writes the workspace.** Transfer stages the benches, validates each one, takes a checkpoint, and only
  then creates the cells through `CellCreate`; a refusal leaves the workspace untouched. An existing bench is the
  Replace question, in the window and as `--replace` on the CLI.

By §14 a broadly useful calculator that fits circuitRF's standards is a pull request; that is the route taken here.

## 2. The parts

Seven parts are System blocks and carry that block's own parameters, with the block's own placement defaults read from
`ComponentTypeRegistry.DefaultParameters`, so a stage and a freshly placed block cannot disagree. The splitter and the
array combiner are the library's N-way Combiner, added with this tool (`system-components.md`, Combiner). The library
has no phase shifter or limiter; those two are composed of parts it does have, and so is the active combiner:

| Part | Placed as |
|---|---|
| PhaseShifter | `Amp` (gain −IL, NF, IIP3, `IP3Ref=Input`) + `TLIN` (Z0, E = phase at the stage's frequency) |
| Limiter | `Amp` (gain −IL, NF = IL, IIP3 = P1dB + 8.9625 dB) |
| Splitter | `Combiner` from its common port, output 1 followed, outputs 2…N terminated in Z0 |
| Combiner, Direct | one `Combiner` of N ways, `IL`, each path's `PathIL` as its `IL[k]` |
| Combiner, Tree | log2 N levels of 2:1 `Combiner`s, level l's part j fed by parts 2j−1 and 2j of level l−1 |
| Combiner, Active | either topology, an `Amp` (`Gain`, `NF`, `IIP3`, `IP3Ref=Input`) after each combiner |

An active combiner is a composition rather than a block of its own because the composition gives the same answers
with no new model: its noise, gain and intercept are an Amp's, after a passive combiner's.

**The variants' budget** is the cascade of their levels (`CombinerSpec.Budget`): the paths' weights a_k first —
coherent (Σa_k)²/N², incoherent Σa_k²/N, T0·(N/Σa_k² − 1) of noise by Bosma on the weighting network — then each
level's passive part (n·g signal, g noise, T0·(1/g − 1)) and, active, its amplifier, cascaded with Friis on the noise
gains and the intercept sum on the signal gains. The stage then enters the chain's cascade with that signal gain,
noise gain, noise figure and intercept, and the chain's array gain becomes the product of each stage's signal over
noise gain, (Σa_k)²/Σa_k² for the combiner. A tree with N not a power of 2 is refused, Direct named as the remedy,
rather than given uneven levels: an uneven tree's paths have unequal losses, which is what `PathIL` already states.

## 3. The benches, and what was found building them

**The mixer's image termination.** A mixer's SSB noise figure is stated with its image at T0. In a chain a filter in
front of the mixer reflects the image instead, and the ideal Mixer's equivalent noise current then develops a different
noise voltage there (an open-like reflection about four times the matched one). Measured on the superhet: harmonic-balance
NF 2.833 dB against Friis' 2.549 dB without anything in front of the mixer, 2.5489 dB with an isolator — a circulator whose
third port is terminated at the ambient, i.e. a T0 termination at the image. The benches place one, as the bench's and
not the chain's, and the docs say what removing it shows.

**Mixing order on a lattice with an LO.** Each LO raises every product's order by one. With MaxMixOrder 5 on (f1, f2, LO)
the IF stages' IM3 read 0.012 dB off; at 6 (5 + mixers) 1.8×10⁻⁴ dB, and 7 or 9 change nothing. The compression sweep
likewise converges at 3 + 2·mixers (0.088 dB → 0.048 dB → 0.047 dB at 3, 5, 7). The HB noise run needs 1 + 2·mixers to
keep the image sideband, and its output frequency is offset 1 kHz from the IF, where the noise at the IF is defined: on
the lattice point itself the run returns NaN, by design.

**A swept variable in a measurement is the sweep's axis.** In a schematic with a sweep over `Pin`, every measurement
reading `Pin` reads the swept cube, including one for a different analysis. The 1-Tone bench keeps its small-signal drive
in its own variable, `Pss`, and sets `Pin = Pss`. The Steering bench uses the same fact on purpose: its closed-form
array factor reads `theta_in` and `theta_steer` and comes out as a cube over both sweeps, aligned with the simulated
pattern by axis name.

**Two limits of measurement expressions over a sweep's cube, met on the Steering bench.** `sin`, `cos` and `exp` take a
number, not a cube ("Value is Cube, not Complex"), while `^` broadcasts; the bench writes e^(j·x) as
`2.718281828459045^(j*x)` and sin x as its imaginary part. And a variable DERIVED from a swept one (`psi_in =
…sin(theta_in…)`) reads its nominal value in a measurement, not the swept cube, though the circuit sees it swept; so the
array factor is written from `theta_in` and `theta_steer` themselves.

**A splitter-fed plane wave gives each element its own noise.** One Port into a lossless N-way Combiner used as a
splitter: by Bosma the splitter's isolation network puts kT0·(I − s·s†) on its outputs and the Port's termination
kT0·s·s†, so each element sees kT0 of its own, uncorrelated with the others — what an element of a real array receives
from its own antenna. The NF from that Port at the beam's peak is then one element's, for any steer angle; with unequal
paths it is the budget's plus the taper's loss, 10·log10 N − (Σa_k)²/Σa_k², because the splitter shares the wave equally
where the combiner weighs it unequally.

## 4. Verification

`testdata/chain/plot_chain_compare.py` runs the example's two chains and an RF-only array through the CLI and plots the
budget against the simulation, stage by stage:

![Superhet, stage by stage](../images/rf-chain-budget/per_stage_superhet.png)

![The steered array with a mixer, stage by stage](../images/rf-chain-budget/per_stage_array.png)

![The steered array at RF, S-parameter noise](../images/rf-chain-budget/per_stage_rfarray.png)

![Every quantity's largest deviation](../images/rf-chain-budget/deviation_summary.png)

Everything that is exact in the budget is within the benches' drive levels: 3×10⁻¹⁰ dB in gain and NF from the
S-parameter analysis, 10⁻⁴ dB from harmonic balance at 40 dB below compression and two tones 50 dB below the intercept.
The four combiner variants, each transferred and run, read the same (gain and NF to 10⁻⁶ dB, intercepts to 10⁻³ dB,
`ChainBudgetTests.EveryCombinerVariant_SimulatesToItsBudget`), and on the example's Combiners bench gain and NF to
10⁻¹⁴ dB. The Steering bench's pattern is the closed-form array factor to 3×10⁻⁹ dB within 3 dB of each peak and 10⁻⁶ dB
down to −60 dB:

![The array pattern for three steer angles against the closed-form array factor](../images/rf-chain-budget/steering_pattern.png)

![The four combiner variants, budget and simulation](../images/rf-chain-budget/combiner_variants.png)

Two quantities are not exact and are said to be: the cascaded compression point is a formula of the intercept's form
and reads 0.02 to 0.06 dB early, and the envelope SNR is one record of noise, ±0.12 dB from seed to seed.

## 5. References

Friis 1944 (doi:10.1109/JRPROC.1944.232049); Pozar, *Microwave Engineering*, 4th ed., §10.1 and §10.3; Razavi,
*RF Microelectronics*, 2nd ed., §2.2–2.3; Wilkinson 1960 (doi:10.1109/TMTT.1960.1124668); Van Trees, *Optimum Array
Processing*, §2.2. The feature list of RxCalc (https://github.com/arhiv6/rxcalc, GPL-3) is the inspiration; only its
README was read, and no code is taken from it.
