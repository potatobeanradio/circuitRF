# circuitRF — Filter synthesis: from a specification to a placed filter

**Status:** in progress · **Date:** 2026-11-16

A filter is asked for by its specification (`FilterSpec`: response, band, order, edges, ripple, stopband, Z0 and any
transmission zeros) and built by a **realiser**: one family and form that turns the specification into one built-in
part with its parameters, plus the values its lumped sites want (`FilterRealisation`). The parts are the geometry-stored
filter families of the RF passives (docs/design/rf-passives.md: MLAD, MCRC, MCLA, and the lumped sites), so a
synthesised filter is an ordinary placed part the user can still edit by hand. Realisers are found by type
(`FilterRealisers.All`); each family adds its own.

## 1. Prototypes and lumped ladders

(to be written)

## 2. The coupling matrix

(to be written)

## 3. Ladders on MLAD

(to be written)

## 4. Coupled resonators on MCRC

(to be written)

## 5. Coupled arrays on MCLA

(to be written)

## 6. The Filter Designer, the `filter` verb and the MCP tool

**One seam, three entry points.** The window, `circuitrf filter` and the MCP `create` tool's `filter` mode all go
through `FilterSynthesisService` (`src/Design/Filters/`). Each one asks every realiser in `FilterRealisers.All` whether
it `Supports` the specification, and calls `Realise` on the one chosen. Nothing in the service designs anything. It
does four things:
- **`Substrate`** resolves the technology's microstrip between the chosen signal and ground layers through
  `SubstrateResolver`, as a run does. It is null with the reason when the stack has none.
- **`Part`** makes the realised part as a palette drop would, then sets each row the realisation states. A value with
  a spaced unit (`50 Ohm`) is split into the expression and the unit, as `Units.SplitTrailingUnit` splits an inline
  unit. A glued unit (`1.2mm`) or a word stays the expression, with no unit.
- **`Cnl`** writes the test bench: two ports of Z0, the part with pins 1 and 2 on them and every other pin on a net of
  its own name, one ordinary part per site, and an S-parameter sweep across the band.
  - Each site part is an R, L or C of the site's value, with the part's `Footprint`, else the R/C default on the
    technology (`FootprintDefaults.For`). A via site gets a VIAGND.
  - The part's line states the substrate (`H`, `T`, `Er`, `Sigma`, `TanD`), so the netlist runs the same in any folder,
    and it is the substrate the realiser designed on. `CnlTechnologyBinding` leaves a line that states every substrate
    parameter as it is.
  - The pin order is the symbol's own (`SymbolPortDefs.For` on the part), so a part whose pins follow its parameters
    (MLAD's G pins, MCLA's E pins) is wired the way a schematic would wire it.
- **`Ideal`** gives the response the realisation is judged against: the doubly terminated prototype from
  `LowpassPrototype.GValues`, series element first, transformed to the band by the textbook frequency transformations
  (Matthaei, Young and Jones), as L and C parts run by the engine.
  - Port 2 is the prototype's own load, so an even-order Chebyshev shows its ripple and not a mismatch.
  - The ideal `Filter` block was the other choice. It has no bandstop form, and it is not the prototype the realisers
    start from. The ladder covers all four bands, and it covers every response `GValues` learns as the realisers add
    them.

**The verb** (`src/Cli/FilterVerb.cs`) takes the specification as flags.
- `--tech` names a `.ctech`, a folder in a workspace, or a shipped id. Without it, the global `--workspace` is used,
  then the workspace the output is written in.
- Without `--realiser` it lists every realiser with `yes` or `no` and the reason. With `--json`, each realiser is a
  `filter.realiser.listed` diagnostic carrying `name`, `supports` and `why`, so no new member was needed in the result
  envelope.
- With `--realiser` it writes the bench to `-o`. `--to-schematic` draws that bench through `NetlistToSchematic`, the
  path `netlist --to-schematic` takes.
- Its refusals are coded `filter.*`. An unknown flag is `filter.args.unknown-option`, which the MCP gate that probes
  every advertised argument reads.
- An internal overload takes the realiser list, so a test can hand the verb a realiser of its own. `FilterRealisers.All`
  finds types in Core's assembly only.

**The MCP tool** is a mode of `create`, not a new tool: what it makes is a document, and every tool on the surface is
a standing cost to a client. Its arguments are named after the flags. The one exception is `technology` for `--tech`,
because `create workspace` already advertises a `tech` that means something else, and the schema is the union across
modes.

**The window** (`src/Ui/Filters/FilterDesignerViewModel.cs`, `src/Ui/Views/Filters/`) is laid out as the Match
Designer is.
- On the left: the specification, the family filter, the substrate it found, then the realisers. Under each one it
  shows whether it can build the filter and, if not, why. Below them, "What Place writes": the parameters and site
  values.
- On the right: one Data Display plot, as the Match Designer hosts its own. It shows |S21| and |S11| of the ideal ladder
  and of the selected realisation's bench, both run by the engine on every edit.
- **Synthesise…** in the Properties of an MLAD, MCRC or MCLA opens it with that family preselected. A family is matched
  against the realiser's name, which by the contract's convention ends in the part type, e.g. "(MCRC)".
- **Place** puts the part to the right of the drawing. It reads the part's sites the way Place Site Parts reads them,
  from the part's own plan (`PassiveSites.Read`, with the part in the schematic for that one read), and fills them with
  `PassiveSites.PlaceParts`. Part, site parts and wires go in one `PlaceSitePartsCommand`, so one Undo takes them all off.

**Tests** (`tests/Ui.Tests/Synthesis/FilterDesignerTests.cs`) use a realiser of their own: a lumped Butterworth or
Chebyshev lowpass written as MLAD sites.
- The verb lists it, and says why it refuses a highpass.
- Realised on the shipped RO4350B board, the verb writes a netlist that states the 0.508 mm substrate and the board's
  default land, and draws it. The netlist's −3 dB point is fc within 3 %.
- The ideal ladder loses exactly the ripple at each passband edge, in all four bands and for an even order too.
- The `create` tool advertises the mode and its arguments. One call through the adapter becomes the verb's command line
  and writes the netlist.
- The Designer preselects the family and lists the realiser, plots four traces, and refuses Place on a band the realiser
  cannot build. Its Place adds the ladder, two inductors and a capacitor, and one Undo takes them all off.

## 7. Possible enhancements

- **Tools ▸ Filter Designer, on the active schematic.** Today the window opens only from a placed MLAD, MCRC or MCLA, so
  you have to place a part before you can synthesise one.
- **Apply to the selected part.** Writing the realisation into the part that opened the Designer, as one undo step,
  would keep its wiring and its place, where Place adds a second filter beside it.
- **Signal and ground layer pickers in the window.** The verb takes `--signal` and `--ground`, but the window uses the
  technology's default layers, so a filter on an inner layer of a board, or on a die's second metal, can only be
  designed headless.
- **A family on `IFilterRealiser`.** The family filter matches the part type at the end of the realiser's name. A
  property would not depend on how a name is written.
- **Markers on the Designer's plot.** The plot is a bare PlotControl with no info-box layer, so a marker shows no
  readout. Hosting it as the Signal Designer hosts its plots would let you read a band edge off the curve.
- **A `result.filter` object in the JSON envelope.** The list rides on `filter.realiser.listed` diagnostics. An object
  with the realisation's parameters and site values would let an agent read what was written without parsing the `.cnl`.

## 8. References

- G. L. Matthaei, L. Young and E. M. T. Jones, *Microwave Filters, Impedance-Matching Networks, and Coupling Structures*,
  McGraw-Hill, 1964: the doubly terminated lowpass prototype, its terminations and its frequency transformations to the
  highpass, bandpass and bandstop forms; a book, no DOI: [the book at the Internet Archive](https://archive.org/details/microwavefilters0000matt).
