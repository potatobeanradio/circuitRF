# circuitrf_pcell — PCell generators in Python

A generated cell is a function of its parameters and its technology, and nothing else. Write one,
declare its parameters, call `run()`.

```python
from circuitrf_pcell import Parameter, Pin, Rect, Result, generator, run

@generator("MLIN", [Parameter.length("W"), Parameter.length("L")])
def mlin(params, tech):
    w = params.length("W")          # already in database units
    length = params.length("L")
    layer = tech.signal_layer
    return Result(
        shapes=[Rect(layer, 0, -(w // 2), length, w // 2)],
        pins=[Pin("1", 0, 0, layer, w, 180.0),
              Pin("2", length, 0, layer, w, 0.0)],
    )

run()
```

`example/mlin.py` is a working version of exactly that, plus a via array exercising counts, flags,
text parameters and the diagnostics channel.

---

## The five things worth knowing before writing a real cell

**1. There are no metres.** Every length arrives in **database units**, converted by circuitRF with
its own single rounding rule before it was sent. The resolution is deliberately not on the wire, so
a script *cannot* do its own metre conversion — there is nothing to do it with. That is what keeps
one rounding rule across the process boundary instead of two.

**2. Round your own arithmetic with `dbu()`.** Not `round()`, which is banker's rounding in Python
(`round(0.5)` is `0`, `round(2.5)` is `2`), and not `int()`, which truncates. Both disagree with
circuitRF at exactly the midpoints where two adjacent shapes decide whether they abut. Passing a
fractional float where a coordinate is expected is **refused by name** rather than silently rounded
— being made to write `dbu(w / 2)` is the point.

**3. A pin carries width and outward direction, and they are required arguments.** A connection is
an **edge**, not a point; a bend needs to know which way its arm faces. This is the field most
easily omitted and the most expensive to add later — every cell written without it would need
revisiting, which is why the constructor will not let you.

**4. Declare each parameter's dimension correctly.** `Parameter.length("W")` is what tells circuitRF
to convert `W` from metres to database units. A length you forget to declare arrives unconverted and
your geometry is off by nine orders of magnitude; a non-length you declare as one is silently scaled.
This is the one declaration worth checking twice.

**5. A generator must be deterministic given its declared inputs.** No clock, no ambient or global
state, no randomness, no set-iteration order, no accumulation whose order varies between runs. Two
users on different machines must get identical geometry — when they do not, what they see is a design
that changed by itself, and the cache that keyed on those inputs is quietly poisoned. Reading a file
is fine *provided its content is part of your cell's cache key*.

---

## What you get

| | |
|---|---|
| Shapes | `Rect` `RoundedRect` `Circle` `Polygon` (with holes) `Curve` `Path` `Via` `Label` |
| Curved edges | `Edge.line()` `Edge.arc(bulge)` `Edge.cubic(...)` — **do not flatten your own curves**; flattening is a rendering decision made at screen resolution |
| Pins | `Pin(name, x, y, layer, width, outward_deg)` |
| Parameters | `params.length()` `.real()` `.integer()` `.flag()` `.text()`, each with a default |
| Technology | `tech.signal_layer` `tech.ground_layer` `tech.layer_named(...)` `tech.stackup` |
| Warnings | `Result(diagnostics=[...])` |
| Refusing | `raise` — circuitRF reports it naming the cell |

`tech.signal_layer` and `tech.ground_layer` are the **resolved** answer, computed by circuitRF before
the request was sent. Do not re-derive them from the stackup: a second implementation of that rule
fails silently, putting geometry on a plausible but wrong layer. They can be `None` — a layout with
no technology still generates geometry — so have a fallback.

`diagnostics` is **not** an error channel. It is for a generator that *did* produce geometry and has
a caveat about it. To refuse outright, raise.

**`stdout` is the wire and must carry nothing else.** A stray `print()` lands in the middle of a
frame and desynchronises the stream, which surfaces as circuitRF reporting a malformed reply nowhere
near the print. Write to `sys.stderr`; circuitRF surfaces that.

---

## Declaring generated artwork to circuitRF

A `pcell-generators.json` beside the kit — run-time data, never a list inside circuitRF:

```json
{
  "schemaVersion": 1,
  "entry": "pcells/main.py",
  "pythonPath": ["lib"],
  "sources": ["pcells", "lib"],
  "dataFiles": ["tables/pads.csv"],
  "interpreter": null
}
```

- `entry` — the script that calls `run()`. Relative to this file's own folder, so the kit can be
  moved or copied whole and still resolve.
- `pythonPath` — added to `PYTHONPATH` so the kit's own modules import with nothing to configure.
- `sources` — what your generators are built from, for the cache key that decides whether an
  already-generated cell can be reused. Omit it and circuitRF uses the entry script's own directory,
  which is the ordinary layout. **Not** `pythonPath`: that may point at a shared environment you do
  not own, and hashing a virtual environment on every workspace open is a cost nobody would trace
  back to here.
- `dataFiles` — files your geometry depends on but which are not source: a table of pad sizes, a
  profile, a device list. **Reading a file from a generator is fine provided its content is part of
  your cell's cache key, and declaring it here is how it becomes part of it.** Undeclared, an edit to
  it changes nothing — circuitRF will keep handing out the cells built before you changed it.
- `interpreter` — usually omitted; circuitRF finds one. Set it when the kit's cells need packages
  that live in a particular environment. **circuitRF does not bundle an interpreter and does not
  install packages on your behalf** — a kit that needs an environment declares it.

It deliberately does **not** list the generators the kit offers: `describe` is the only source of
that, and a second one would be a cache that can silently disagree with the script.

---

## Checking your work

```
python3 tools/pcell-python/verify.py
```

Self-contained — no circuitRF, no .NET, no pytest. The C# side has its own tests that drive this
package as a real subprocess, including one asserting that a cell written here and the same cell
written as a circuitRF built-in produce **byte-identical geometry**.

This package is written from the specification (`docs/design/pcell-wire-schema.md`), not ported from
circuitRF's own codec. That is on purpose: two implementations arrived at independently agreeing is
evidence about the format, whereas one implementation agreeing with itself is not.

## Requirements

Python 3.9+. No third-party packages, by design — a cell author should need nothing but an
interpreter to get started.
