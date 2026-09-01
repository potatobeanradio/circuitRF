# Brief SYS-7 — the System Components chapter

**Read first:** `brief-sys-series.md`; `docs/design/user-docs-factory.md`;
`docs/user/src/reference/components.md` (the Ideal Mixer section commit `e925f9c` added, and the
`### Matching Network (Match)` stub at its line 72, which is the exact pattern this brief follows);
`docs/user/src/reference/match.html`'s source as an example of a component with its own chapter;
`docs/user/src/_nav.txt`; `docs/user/src/reference/index.md`; `tools/DocGen/Pipeline/Placeholders.cs`
and `SiteNav.cs`.

Documentation is written in `docs/user/src/**.md`; everything under `docs/user/` outside `src/` is
GENERATED. Never hand-edit a generated page.

**Owner request, 2026-08-31: the system components get a whole chapter of their own** — an
introduction saying what these blocks can and cannot do, the passive-intermod discussion in one
place, and every component listed with its glyph. That is a new page, and a new page in this
factory means four things must agree.

## The new page: `docs/user/src/reference/system-components.md`

### 1. What these blocks are, and what they are not

The opening section, and the most important prose in the series. It has to make a reader confident
about the class before they meet a single parameter:

- **What they do.** Each behaves the way a datasheet says: a coupling in dB, an isolation in dB, an
  intercept in dBm. That is enough to answer the questions a system diagram is drawn to answer —
  level plans, cascaded gain and intercept, image and spurious paths, band plans, isolation budgets,
  switch-state coverage — **before any of the parts exist**.
- **What they are not.** They are ideal by construction: frequency-flat where a real part is not,
  exactly matched where a real part is not, exactly isolated unless a number says otherwise, and
  lossless except where a loss is typed in. An ideal 90° hybrid holds its quadrature at *every*
  frequency, which no physical coupler does. There is no manufacturing spread, no temperature, no
  DC power and no noise.
- **What to reach for instead, and when.** When bandwidth, dispersion or a real part's own
  behaviour is the question, the answer is a real network: transmission lines, a Touchstone file,
  an EM run, a device model. Say where each of those lives.
- **How they run.** S-parameters answer match, isolation, coupling and filter response; harmonic
  balance answers intercepts, compression, mixing products and PIM. The mixer's own section already
  makes the general point — an S-parameter run cannot see a device convert — and this chapter should
  point at it rather than re-argue it.

### 2. Passive intermodulation, in one place

The topic is the reason several of these blocks have parameters a user has never had to type
before, and scattering it across nine component entries would leave nobody able to find it. One
section covering:

- **What PIM is here:** a deterministic memoryless nonlinearity that produces odd-order products,
  not a random or noise-like process. A user who types −150 dBc gets −150 dBc.
- **How it is specified**, with the datasheet conversion worked through: a product level in dBm
  against two stated carriers is what a supplier quotes, and the dBc form is the same number
  differently dressed. Show the arithmetic once so a reader can check their own part.
- **Which blocks carry it and why the others do not.** Circulator, coupler, 90° hybrid, switch and
  attenuator can; the filter and duplexer cannot, because their behaviour is frequency-dependent and
  the nonlinearity that makes PIM has to be memoryless. **This is where the workaround is
  documented:** an attenuator at `Loss = 0` with a PIM figure is a PIM generator you can place in
  front of anything, including a filter.
- **What turning it on costs.** It is off by default and the block is then exactly linear. Turning
  it on makes that block a nonlinear component, which changes what an S-parameter run does under the
  hood (it solves a DC operating point first) without changing what it reports.
- **What the higher orders do.** Fifth and seventh order products come out at the limiter's own
  fixed ratios — an idealisation, not a fit to any measurement. Say so plainly; a reader who
  compares against a measured fifth-order product needs to know that was never claimed.

### 3. Every component, with its glyph

One short entry each, in palette order, each carrying `{{symbol: <stem>}}` and a paragraph on what
the block is for and what its ports are. This is the chapter's catalogue: it is meant to be
readable end to end, so keep each entry to a few sentences and let the parameter tables live in
`components.md` (below). Include the two mixer tiles — they are System components now, and a
chapter that omits them is a chapter a reader will not trust.

The one surprise per component, which is where the words are worth spending:

| Component | The thing to say |
|---|---|
| Circulator | The only non-reciprocal component in circuitRF: `S21 ≠ S12` on purpose. `Direction` is drawn on the symbol, so a schematic never hides which way it circulates. |
| Coupler / 90° hybrid | The quadrature is exact at every frequency, which no real coupler is. An ideal 20 dB coupler already loses 0.044 dB through its main arm, and `IL` is on top of that. Point at four `TLIN` arms when the bandwidth is the question. |
| Balun | A lossless reciprocal 3-port cannot have all three ports matched, and neither can a real balun — the balanced ports see each other. `Zbal` is per port, so the differential impedance is twice it. An exact ideal transformer is a 2-port with unequal port impedances. |
| Switch | `State` is a parameter, not a pin, so **a parametric sweep over it simulates every switch position in one run** — and the symbol is drawn in the position it is set to. `Reflective` versus `Absorptive` is the choice that changes the neighbouring circuit. |
| Amplifier | No DC power consumption and no bias pins, by design. `IP3Ref` says whether the number typed is input- or output-referred. P1dB is not a separate knob because one nonlinearity sets both. Unilateral unless `S12` is turned on, which is what makes it unconditionally stable. |
| Attenuator | At `Loss = 0` with a PIM figure it is a PIM generator — cross-link to the PIM section. |
| Filter | **It wears the Match component's symbol, and that is deliberate**: impedance matching is a form of filtering. Tell them apart by the type label and instance name, and link to `match.html`. `Order` is the PROTOTYPE order — a 3rd-order bandpass is a 6th-degree network. Unequal `Zin`/`Zout` makes it a transformer as well as a filter. |
| Duplexer | Its isolation is a consequence of the two responses meeting at one node, not a parameter — that is the point of the component. A phasing line is a `TLIN` in the arm. |
| Mixer / MixerD | Already documented in `components.md`; this entry is a pointer, not a second copy. |

## The four things that must agree for a new page to exist

1. **`docs/user/src/_nav.txt`** — the ONE reading order, which drives the table of contents, the
   Previous/Next chain, and an orphan check that fails the run by name in both directions. Add
   `reference/system-components.html` under `== Design`, **immediately after
   `reference/components.html`**: a reader who has just met the standard library meets the system
   blocks next, and the entry that follows (`dynamic-symbols.html`) is where four of these glyphs
   turn up again.
2. **`docs/user/src/reference/index.md`** — the Reference Guide's own contents. Its "Three chapters
   that are easy to miss" callout is now four, or becomes a different sentence; make the call and
   keep the paragraph readable rather than appending to it.
3. **`components.md` keeps a short entry per system component** — glyph, one paragraph, a callout
   linking on to the chapter, and `{{table: components/<SymbolKind>}}`. This is **not optional and
   not duplication**: the in-app Help button on the Component Parameter Editor deep-links to
   `reference/components.html#<symbolkind-lowercase>` for every kind, that correspondence is emitted
   from `src/Ui/Diagnostics/DocAnchors.cs`, and the generator and `DocsFactoryTests` both fail if any
   of it does not resolve. The `### Matching Network (Match)` section is the exact precedent — a
   stub whose callout says the component has its own chapter, followed by the generated parameter
   table.
4. **The parameter tables stay generated.** `{{table: components/<Kind>}}` reads the live registry;
   if a description reads badly the fix is `ComponentTypeRegistry.ParameterDescription`, which is
   also what the parameter editor shows. Never hand-write one.

`dynamic-symbols.md` gains sections for `Filter` (the wave stack follows `Form`, from the same
shared code `Match` uses), `Circulator` (`Direction`) and the two switches (`State`), cross-linked
both ways. `schematic-editor.md` gains the `System` palette filter, and its palette figure changes.
`simulations.md` gains a sentence on which analysis answers what for these blocks.

## Milestones

1. `system-components.md` — the three sections above.
2. `_nav.txt`, `reference/index.md`, and the `components.md` stub entries with their anchors.
3. `dynamic-symbols.md`, `schematic-editor.md`, `simulations.md`.
4. **The Doc Gen run**, and the figure triage below.

## The Doc Gen run, and its known trap

Regenerating figures rewrites more files than it changes. **Classify before reporting**: an SVG
whose only difference is the element-id counter (which is HEXADECIMAL — a `\d+` pattern
mis-classifies it) or the fourth decimal of a rotation matrix has NOT changed, and must be put back
rather than committed. The mixer commit did exactly this: four symbol figures and one palette figure
were real, nine others were noise and were reverted. Report the count of each.

Expected real changes: one symbol figure per new tile (light and dark), the palette figure (twelve
new tiles and a new filter), the new chapter, and the pages listed above.

## Must NOT

- Hand-edit anything under `docs/user/` that is not under `docs/user/src/`.
- Add the new page without placing it in `_nav.txt`. The run fails by name, which is the design
  working; do not "fix" it by removing the orphan check.
- Drop the `components.md` entries in favour of the chapter. The Help anchors are a contract.
- Hand-write a parameter table that `{{table: components/…}}` can generate.
- Name a vendor, a commercial simulator or a specific process kit — root `CLAUDE.md`
  §Commercial Vendor References. Grep the whole diff before finishing.
- Quote the owner. Paraphrase.
- Document a behaviour without running it. Every number in these pages — a coupling, an isolation, a
  filter's rejection at a stated offset, a PIM level — either comes from a gate in an earlier brief
  or is measured before it is written down.

## Gates

`tests/Ui.Tests/DocsFactoryTests.cs` green: it is what catches an unresolved `{{…}}` placeholder
reaching a shipped page, a broken `{{anchor:}}`, an orphan page, and a Help anchor that does not
resolve. Every system component has a chapter entry with a glyph, a `components.md` anchor and a
generated parameter table; every dynamic glyph is documented in `dynamic-symbols.md`; the
Previous/Next chain and the site table of contents both include the new page. `dotnet test
tests/Ui.Tests` and `tests/Firewall.Tests`. Write-up in `src/Ui/RESOLVED.md`, including the figure
triage counts.
