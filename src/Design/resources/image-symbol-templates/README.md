# Alternate symbol drawings

The drawings other conventions use for the symbols a schematic picture holds — matching templates for
`SymbolTemplates` (`src/Design/Schematic/Recognition/`), never drawn. circuitRF's own symbols are templates too, taken
from `BuiltInSymbols`; these are the rest. Written in-house. The format is in `SymbolTemplates.Parse`'s comment.

Each drawing is set in the frame of its kind's built-in symbol — a two-terminal part upright with pin 1 on top, a line
or an amplifier across with pin 1 on the left, a ground with its lead up, a terminal with its lead to the right — so an
orientation read off any of them means the same thing. Pins are given at the TIP of their lead; the lead (the straight
run from the tip to the first turn) is cut off when the template is made, because on a picture it is wire.

Adding a drawing is adding a `.stroke` file here.
