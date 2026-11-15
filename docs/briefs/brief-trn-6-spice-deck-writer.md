# Brief TRN-6 — the SPICE deck writer

**Series:** TRN-0 … TRN-9 · **Tag:** `R-trn6-<n>` · **Depends on:** TRN-2, TRN-3 · **Design note:** §2 D7, D9, §9.2,
§11
**Area:** `src/Core/Netlist/Spice/SpiceDeckWriter.cs` (new), the element table shared with the reader, `src/Cli`
(`netlist`, `check`), the MCP `netlist` tool, `docs/design/spice-models.md`, `docs/design/cli.md`, `src/Core/RESOLVED.md`.

## The gap

A circuitRF bench cannot be written in the SPICE dialect. There is no translation, no list of what would not
translate, and no check that a translation means what the schematic means.

## Requirements

**R-trn6-1 — The writer.** `SpiceDeckWriter` writes an elaborated bench with a tran analysis as one flat deck, per note
§9.2: header comment (bench, circuitRF version), name map as comments, elements each with its circuitRF path as a
trailing comment, `.options` from `RelTol`/`VTol`/`ITol`/`Method` and `SpiceOptions` verbatim, `.ic` for held nets,
`.tran` with `uic` under `InitialState=zero`, `.save` for every recorded net and probe current.

**R-trn6-2 — The element table** of note §9.2.1, as data the reader and the writer both read, so a mapping exists in
both directions or neither. Exact entries only (D7).

**R-trn6-3 — Device models.** For the diode, BJT, MOS level 1 and MOS level 3, tabulate each circuitRF parameter against
the dialect model's: equal equations, equal units and defaults. A parameter that maps is written; one that does not,
set to a non-default value, refuses the instance by parameter name ("<instance>: <param> has no counterpart in the
SPICE dialect's <model>"); one left at its default is omitted if the dialect's default is the same, refused otherwise.
The table is committed in `spice-models.md` as a new section.

**R-trn6-4 — SDD** as behavioural current sources, and each charge as a behavioural voltage source driving a unit
capacitor: the inverse of the charge idiom `spice-models.md` §8.8 reads. Expressions are written through the inverse of
`SpiceExpression`, function by function; a function with no dialect spelling refuses the instance naming it.

**R-trn6-5 — Placed SPICE models and Verilog-A.** A `SpiceModel` instance includes its file as it is and writes its
instance line as read. A Verilog-A instance loads the same `.osdi` circuitRF compiled (`OsdiBuilds` picks this
platform's build) and instantiates it in the dialect's OSDI form.

**R-trn6-6 — Names** per note §9.2.2: sanitized, unique after case folding, mapped both ways; the map written in the
deck header and returned to the caller.

**R-trn6-7 — Refusals.** Every element not in the table is refused by name with a reason, all of them listed at once
(not first-failure). Nothing is written when anything is refused.

**R-trn6-8 — `SpiceFit=yes`** per note §9.2.3: frequency-defined elements (and lines, where `SpiceLines` is not `rlgc`) written as subcircuits realizing TRN-4's fit,
lossy lines as `T` plus the causal residual's realization; each such element labelled in the deck and recorded for
TRN-7's result note. If TRN-4 has not landed, the key is refused with that sentence.

**R-trn6-9 — Lines as `TXL`/`CPL`, `SpiceLines=rlgc`** per note §9.2.3. Every microstrip, stripline, coplanar and
lossy ideal line written as `TXL`, every coupled set of them as `CPL`, with R, L, G, C (matrices for `CPL`) extracted
from circuitRF's own model at `SpiceLineFreq` (default the highest source frequency). For each line, the largest |ΔS|
between the `TXL`/`CPL` line's closed-form S-parameters and circuitRF's model over `1/Stop` to `f_max`, and where it
occurs, carried into the deck as a comment and returned for TRN-7's result note. Confirm against the current ngspice
manual which per-unit-length forms `TXL` and `CPL` accept (in particular whether `CPL` takes full R and G matrices)
before writing either; a coupled set ngspice cannot express is refused by name. Default `SpiceLines=refuse`.

**R-trn6-10 — Discontinuities.** Classify every bend, tee, gap, step, cross and open model: one whose circuitRF model is a
fixed lumped network with values that depend only on geometry is written exactly as `L`, `C` and `R` (an entry in the
exact table); one whose values vary with frequency is refused by name unless `SpiceFit=yes`. The classification is a
table in `spice-models.md` beside R-trn6-3's.

**R-trn6-11 — CLI, MCP, `check`.** `circuitrf netlist <bench> -o out.cir` (format from the extension, as `plot` and
`render` do); exit 1 and nothing written on any refusal. MCP `netlist` with a `.cir` output. `check` under
`Solver=ngspice` reports each refusal coded `check.tran.untranslatable`.

## Gates

1. **Round trip.** A bench using every exact entry, written and read back by circuitRF's own reader, elaborates to the
   same netlist (component types, values, connectivity).
2. **Parameter refusal.** A diode with a parameter that has no dialect counterpart, set off its default, is refused by
   that parameter's name; at its default it is written.
3. **All refusals at once.** A bench with a microstrip, an SnP and a Curtice FET lists all three and writes nothing;
   with `SpiceLines=rlgc` it lists the last two.
4. **Names.** Nets `Out` and `out` and a net with a dot in its name map to distinct legal names and back.
5. **CLI.** `netlist -o x.cir` as a process equals the in-process writer byte for byte.
6. **No second writer.** A comment-stripped source scan finds the deck written only by `SpiceDeckWriter`.
7. **Line extraction.** For a microstrip line, the `TXL` values reproduce circuitRF's model's Zc and propagation constant
   at `SpiceLineFreq` to 1e-9 relative, and the reported largest |ΔS| equals an independent recomputation on the same
   grid. A coupled pair's `CPL` matrices reproduce both modes' impedances and propagation constants at that frequency.
8. **Discontinuity table.** Every discontinuity model is in the classification table; an exact one round-trips through
   the reader, a frequency-varying one is refused by name without `SpiceFit=yes`.

## Not here

Running ngspice (TRN-7), FET-law translations, writing analyses other than tran, reading a deck's analyses.
