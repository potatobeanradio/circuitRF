# Brief VL-7 — a Symbol for a Verilog-A part

**Series:** [Model libraries](brief-vendorlib-6-model-library-format.md) · **Tag:** `R-vl7-n` · **Phase:** symbol, UI
**Area:** `src/Design/Schematic/BuiltInSymbols.cs` (`BuildVerilogASymbol`), `src/Design/Schematic/CellSymbolResolver.cs`,
`src/Design/Schematic/ComposedFetSymbolProvider.cs` (the rule to share), new `src/Design/Schematic/FetPinBinding.cs`,
`src/Core/Devices/ComponentModelFactory.cs` (the reserved-name predicate and one stale message),
`src/Core/Elaboration/Elaborator.cs` (the selector list), `src/Design/Schematic/NetExtractor.cs`,
`src/Ui/ViewModels/ParameterEditorViewModel.cs` (the VerilogA panel)
**Depends on:** nothing. It can be built before brief 6.

---

## 0. Why

A placed Verilog-A transistor model always draws as a box. `BuildVerilogASymbol`'s comment gives the
reason: circuitRF does not know what the model is. That is the right **default** for a file circuitRF
knows nothing about. It is the wrong **only choice**, because the person placing the part does know. A
ComposedFET already solves this with a per-instance `Symbol` parameter (`ComposedFetSymbolProvider`). A
Verilog-A part gets the same, and since the user is the one choosing, circuitRF is still not asserting
anything.

## 1. `R-vl7-1` — the parameter

- **`R-vl7-1a`** A `Symbol` parameter on `VerilogA`, **artwork only**. Add it to the reserved-name
  predicate in `ComponentModelFactory` (the one the forwarding filter, the parameter editor and the
  elaborator's selector list all ask), so it never reaches the model. A model that itself declares a
  parameter named `Symbol` is the `TYPE` case the elaborator's comment already describes. Follow
  `ReservedKey`'s existing exact-case rule, and test it.
- **`R-vl7-1b`** What it may name, matching ComposedFET's rule: a built-in symbol kind by name (`FetCurtice`
  draws the FET glyph), the name `ComposedFET` for the 4-pin FET glyph with its thermal pin, or a `.csym`
  path. A `.csym` resolves through the **same** rule as `File` (`SnpPathPolicy`; brief 8 makes that rule
  owning-root aware), so a model and its symbol travel together.
- **`R-vl7-1c`** **Blank means the default.** The default is the FET glyph when the model's connected
  terminal count (after `Pins`) is **3 or 4** and §2 binds it; otherwise it is today's box. A model whose
  terminals cannot be bound by name keeps the box: the default never guesses.

## 2. `R-vl7-2` — binding pins to the glyph

- **`R-vl7-2a`** New `FetPinBinding.For(IReadOnlyList<string> terminalNames, IReadOnlyList<int> thermal)`
  returns, for each glyph pin (gate, drain, source, and a fourth), the model terminal index, or null when
  the names do not identify gate, drain and source. Match on the model's own terminal labels
  (`VerilogAModelIntrospection.CachedTerminalLabels`), case-insensitively: `g`/`gate`, `d`/`drain`,
  `s`/`source`. The fourth glyph pin takes the remaining terminal: a thermal terminal (the introspection's
  `ThermalTerminals`, or `t`/`dt`/`tj`/`thermal`) or a bulk (`b`/`bulk`). Brief 10's symbol picker calls the
  same function on a cell's pin names. It is one rule in one place.
- **`R-vl7-2b`** **Connectivity follows the binding.** The netlist line keeps the model's terminal order.
  The extractor maps each net from the glyph pin it was wired to onto the model terminal `For` assigned it,
  so `d g s b` in the model and `g d s` on the glyph produce the same netlist as the box wired the same way.
  This is the crux of the brief, and §4's test 1 is the gate.
- **`R-vl7-2c`** **A FET chosen explicitly on a model whose names do not bind** binds by position (glyph
  pin k ↔ model terminal k, ComposedFET's by-number rule), and **every pin is labelled with the model's own
  terminal name**, so what is wired where can be seen. The extractor reports it once: *"VerilogA 'X1': its
  terminals (a, b, c) do not name gate, drain and source; the FET symbol's pins take them in order."* It
  is a warning, never a refusal.
- **`R-vl7-2d`** A wire count that does not match is the existing `Pins` behaviour, unchanged. The
  4-pin glyph is used only when four terminals are connected. With `Pins` = 3 on a self-heating model,
  the 3-pin glyph is used and the thermal-terminal note in the editor still says what it says now.

## 3. `R-vl7-3` — changing it on a placed part

- **`R-vl7-3a`** The Parameter Editor's VerilogA panel gets ComposedFET's picker: the current symbol as
  one picture, and a click opens the menu of every symbol this model can take (box, the FET glyphs §2
  allows, Browse… for a `.csym`). Extract the ComposedFET picker's listing into one function both panels
  call if it currently lives in a view model.
- **`R-vl7-3b`** A symbol change keeps wires attached **by pin**, not by position, exactly as a ComposedFET
  symbol change does today. If that behaviour is not a reusable command, stop and report rather than
  write a second one.
- **`R-vl7-3c`** One undo entry.

## 4. `R-vl7-4` — one stale message

`CreateVerilogAModel`'s missing-file refusal says circuitRF "does not compile Verilog-A itself". It does
(`VerilogASourceCompiler`). Change it to say the `File` takes a compiled `.osdi` or Verilog-A source.

## 5. Tests (minimal)

1. **Same netlist through either symbol:** a synthetic 3-terminal model declared `d g s`, placed once with
   the box and once with the FET glyph, wired to the same three nets, extracts byte-identical netlist
   lines.
2. `FetPinBinding`: `d g s` / `gate drain source` / `g d s dt` (thermal) / `d g s b` bind; `a b c` does not.
3. Default: a 3-terminal `g d s` model draws the FET glyph; `a b c` draws the box; a 5-terminal model draws
   the box.
4. `Symbol` never reaches the model: the elaborated instance's forwarded parameters do not contain it.
5. Explicit FET on `a b c` warns once and labels its pins `a b c`.

Use a synthetic model through the existing fake-provider test seam. No compiler is needed. Run only the
classes you touch.

## 6. Docs

`docs/user/src/reference/veriloga.md` § Terminals: replace "the symbol is a plain box, deliberately" with
the default and the choice, keeping the reason the default is what it is. The `{{table: components/VerilogA}}`
row comes from the registry. UiSnapshot: one PNG of a placed `g d s` model with the FET glyph, inspected
and listed in the completion note.
