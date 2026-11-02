# Brief VL-13 — a model's tolerances and corners

**Series:** [Model libraries](brief-vendorlib-6-model-library-format.md) · **Tag:** `R-vl13-n` · **Phase:** statistics, grammar, UI
**Area:** `src/Core/Expressions/Evaluator.Statistical.cs` (read; probably unchanged), `src/Core/Netlist/CnlReader.cs` and
the `.cnl` writer (`corner` inside `define`), `src/Core/Design/StatisticsSetup.cs` (`CornerDefinition`, the
`statistics` line), `src/Design/Statistics/CornerGenerator.cs` and `CornerRun.cs`, `src/Design/Cells/` (`.ccell`
`Corners`), `NetExtractor.cs`, the Export dialog (brief 10), the Yield panel's Variables and Corners views, `src/Cli`
(`yield corners --generate`)
**Depends on:** 9, 10

---

## 0. Why

A published part has spread, and a designer needs it in two forms: the statistical spread a Monte Carlo
draws, and the stated limits (min/typ/max) a worst-case run checks. circuitRF has both for a design's own
values (`tune … dist=`, the `corner` line) and for a kit (distribution functions in the model, corner
library sections). It has neither for a library part. This brief reuses both mechanisms rather than
adding a third.

## 1. `R-vl13-1` — tolerances: distribution functions in the model

- **`R-vl13-1a` Verify first.** The expression engine already has `agauss`, `gauss`, `aunif`, `unif` and
  `limit`. It already treats a call evaluated in a **cell instance's** scope as a **mismatch** draw (one
  per instance per trial), and evaluates it to its nominal outside a trial
  (`Evaluator.Statistical.cs`). So a model whose parameter default or element value is written
  `gauss(10 pF, 0.05, 3)` should already draw per instance in a yield run. Build that fixture through a
  placed library part (brief 9) and confirm both halves: a different draw per instance, and nominal in an
  ordinary run, bit for bit. Record the result in `src/Design/RESOLVED.md`. Fix only what fails.
- **`R-vl13-1b` The Export dialog's Parameters table gains a Tolerance column.**
  - The **Spread** is typed in the Yield panel's own grammar (`± 2 %`, `± 2 % at 2σ`, `σ 1 Ω`,
    `45 … 55 Ω`, with the distribution chosen first). Use **that parser**, not a second one.
  - It is written as the distribution function on the parameter's default in the **source cell's
    `.ccell`**, so the next export keeps it. The dialog says the cell is changed.
  - The mapping: Gaussian `± x %` at kσ → `gauss(nom, x/100, k)`; Gaussian `σ a` → `agauss(nom, a, 1)`;
    Uniform `± x %` → `unif(nom, x/100)`; Uniform `a … b` → `aunif((a+b)/2, (b−a)/2)`.
  - Lognormal, Discrete and truncation have no distribution function, so they are not offered, and the
    column's tooltip says so. A value inside the model that is not a parameter takes a function written
    in the value itself. The format page shows how.
- **`R-vl13-1c` The consumer's Yield panel** gets a **Library parts** group: one row per placed part
  number whose model holds a distribution, showing each spread read-only in the panel's own notation, with
  a checkbox. Unticked, every draw inside that part's cells evaluates to its nominal.
  - Persisted as one `statistics` key listing the parts held at nominal. Pick the spelling with the
    existing `statistics` keys in view, and add it to the `reference statistics` grammar.
  - The panel's global **Mismatch** switch turns these draws off too, because by the scope rule that is
    what they are.
  - Brief 5's SPICE-part tolerance rows (`tol=part` on a `tune` line) are a different mechanism, and they
    stay as they are.

## 2. `R-vl13-2` — corners: named parameter sets in the model

- **`R-vl13-2a` Grammar.** Inside a `define`: `corner <name> <param>=<value> …`. It binds **only that
  cell's own declared parameters**. These are refusals, each naming the line: `temp=` (a part does not
  choose its temperature: the bench does), a name that is not a parameter, a duplicate corner, and the
  name `nominal`, which is reserved. Reader, writer, `check`, and the `reference` netlist topic.
- **`R-vl13-2b` Source.** A cell's corners live in its `.ccell` as `Corners`: a name, plus a value text
  per parameter, where a blank means nominal. Every extraction of that cell emits them into its `define`,
  `ModelNetlist.FromCell` included. A workspace cell can have corners too, and Copy to Workspace (brief
  12) keeps them.
- **`R-vl13-2c` The Export dialog's Corners table**: rows are corner names, columns are the cell's
  parameters, and a blank cell is nominal. It has add and remove. It writes the source `.ccell`, as 1b does.
- **`R-vl13-2d` On a placed part**, a **Corner** parameter offers `nominal` plus the model's corners,
  read from its `.cnl` when the part is selected (one small read, at selection, not on every redraw).
  - It is a selector, never forwarded. A model that declares its own parameter named `Corner` is refused at
    export (`Corner` is reserved).
  - The chosen corner's bindings become the instance's parameter values. **An explicit instance override
    still wins**, and the Parameter Editor marks that parameter *"overrides corner min"*.
- **`R-vl13-2e` Bench corners.** A bench `corner` line can set a library part's corner as an axis,
  spelled `part:<part number>=<corner>`, as in `corner slow temp=85 part:TSTFET100=min`. The `part:`
  prefix cannot collide with a variable or with `instance.param`.
  - It applies to **every instance of that part number** in the design, the worst-case convention that
    every part comes from one lot.
  - During that corner it overrides each instance's own `Corner` parameter.
  - Store it in `CornerDefinition` beside the kit axis selections.
- **`R-vl13-2f` Generate.** The Corners panel's **Generate…** lists each placed part with corners as an
  axis, with a tick box per corner name, as it lists a kit's sections. `yield corners --generate` takes
  `part:TSTFET100=min,max` beside `temp=` and the kit axes.
- **`R-vl13-2g` MC at each corner.** At a corner that sets a part's corner, **that part's own draws are
  off**: the corner is its limit, as a kit's process draws are off at a kit corner. Every other draw
  continues.

## 3. Tests (minimal)

1. **1a's fixture:** two instances of one library part with a `gauss` default draw differently in one
   trial, and both are exactly nominal in an ordinary S-parameter run.
2. 1b's mapping: each of the four spreads round-trips through the Yield parser to the expected function
   text.
3. Unticking a part in the Library parts group holds its draws at nominal while another part keeps
   drawing.
4. `corner` inside a `define`: it round-trips through the reader and writer, and each 2a refusal is
   refused.
5. A placed part on `Corner=min` gives the same result as the same part with min's values typed in by
   hand. An explicit override beats the corner.
6. A bench corner `part:TSTFET100=max` moves both instances of that part and no other part. Generate
   crosses `part:` with `temp=` and counts correctly.
7. MC at a part corner: that part's draws are off and another part's are on.

Run only the classes you touch.

## 4. Docs

`model-library-format.md` (distributions in a model, `corner` in a `define`), `yield.md` (the Library parts
group, `part:` corners), `netlist.md` (`corner` inside `define`), `cli.md` (`--generate part:`). UiSnapshot:
the Export dialog's Tolerance column and Corners table, the Yield panel's Library parts group, and a
placed part's Corner choice. Inspect each and list them in the completion note.
