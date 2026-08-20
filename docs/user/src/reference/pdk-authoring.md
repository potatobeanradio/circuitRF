---
title: PDK Authoring
slug: reference/pdk-authoring.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > PDK authoring
lede: Building a kit: the OpenPDK layout, Python PCells, models, technology and artwork.
---

This chapter is about **producing** a kit. If you are consuming one, read
[PDK Integration](pdk-integration.html) instead.

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#openpdk">Follow OpenPDK</a></li>
<li><a href="#four-halves">The four things a part needs</a></li>
<li><a href="#python">PCell generation with Python</a></li>
<li><a href="#worked">A complete worked example: a microstrip line</a></li>
<li><a href="#handles">Parameter handles</a></li>
<li><a href="#spiral">A second example: a spiral inductor</a></li>
<li><a href="#declaring">Declaring your generators to circuitRF</a></li>
<li><a href="#traps">Traps worth a callout</a></li>
</ol>
</nav>

## Follow OpenPDK {#openpdk}

**circuitRF supports the OpenPDK standard** — <https://github.com/fossi-foundation/open-pdks>.

It is what the importer expects to find, it keeps a kit portable between tools, and it means the
structure of your kit is a published thing rather than a convention you have to explain to every user.
Produce that structure.

Beyond the structure itself, circuitRF reads a kit **entirely at run time**. Nothing about your kit is
compiled into the product: every device type, parameter name, pin count and node role is learned from
what you supply. Anything circuitRF cannot derive, you declare in a run-time data file beside the kit —
never in a list inside circuitRF.

## The four things a part needs {#four-halves}

A complete part is four artefacts, and it is worth being explicit that they are separate:

| | What it is | Where it comes from |
|---|---|---|
| **Schematic side** | A symbol with pins, and a published parameter interface | A symbol description your kit ships, translated on import |
| **Model** | The device equations | A compiled model library, or [Verilog-A](components.html#veriloga), or an [SDD](sdd.html) written in circuitRF's own expression language |
| **Technology** | Layers, stackup, DRC rules | A `.ctech`, or the OpenPDK technology description |
| **Artwork** | The layout | A stored `.clay` for a fixed cell, or a **PCell generator** for a parameterised one |

The parameter interface is the join between the first two and the fourth. **A PCell reads the same
parameter list its symbol displays** — one list, not two. A symbol showing `W` and a generator reading a
different `W` is a defect that only surfaces as wrong artwork.

## PCell generation with Python {#python}

A generated cell is a function of its parameters and its technology, and nothing else. Write the
function, declare its parameters, call `run()`.

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

The support package is `tools/pcell-python/circuitrf_pcell`. **Python 3.9 or later, and no third-party
packages** — a cell author should need nothing but an interpreter to get started.

### What you get

| | |
|---|---|
| Shapes | `Rect` `RoundedRect` `Circle` `Polygon` (with holes) `Curve` `Path` `Via` `Label` |
| Curved edges | `Edge.line()` `Edge.arc(bulge)` `Edge.cubic(…)` |
| Pins | `Pin(name, x, y, layer, width, outward_deg)` |
| Parameters | `params.length()` `.real()` `.integer()` `.flag()` `.text()`, each with a default |
| Technology | `tech.signal_layer` `tech.ground_layer` `tech.layer_named(…)` `tech.stackup` |
| Warnings | `Result(diagnostics=[…])` |
| Refusing | `raise` — circuitRF reports it, naming the cell |

`tech.signal_layer` and `tech.ground_layer` are the **resolved** answer, worked out by circuitRF before
the request was sent. Do not re-derive them from the stackup: a second implementation of that rule fails
silently, putting geometry on a plausible but wrong layer. They can be `None` — a layout with no
technology still generates geometry — so have a fallback.

`diagnostics` is **not** an error channel. It is for a generator that *did* produce geometry and has a
caveat about it. To refuse outright, raise.

### Five things worth knowing before writing a real cell

**1. There are no metres.** Every length arrives in **database units**, converted by circuitRF with its
own single rounding rule before it was sent. The resolution is deliberately not on the wire, so a script
*cannot* do its own conversion — there is nothing to do it with. That is what keeps one rounding rule
across the process boundary instead of two.

**2. Round your own arithmetic with `dbu()`.** Not `round()`, which is banker's rounding in Python
(`round(0.5)` is `0`, `round(2.5)` is `2`), and not `int()`, which truncates. Both disagree with
circuitRF at exactly the midpoints where two adjacent shapes decide whether they abut. Passing a
fractional float where a coordinate is expected is **refused by name** rather than silently rounded —
being made to write `dbu(w / 2)` is the point.

**3. A pin carries width and outward direction, and they are required arguments.** A connection is an
**edge**, not a point; a bend needs to know which way its arm faces. This is the field most easily
omitted and the most expensive to add later, which is why the constructor will not let you.

**4. Declare each parameter's dimension correctly.** `Parameter.length("W")` is what tells circuitRF to
convert `W` from metres. A length you forget to declare arrives unconverted and your geometry is off by
nine orders of magnitude; a non-length you declare as one is silently scaled. **This is the one
declaration worth checking twice.**

**5. A generator must be deterministic given its declared inputs.** No clock, no ambient or global
state, no randomness, no set-iteration order, no accumulation whose order varies between runs. Two users
on different machines must get identical geometry — when they do not, what they see is a design that
changed by itself, and the cache keyed on those inputs is quietly poisoned. Reading a file is fine
*provided its content is part of your cell's cache key* — see [`dataFiles`](#declaring).

<div class="callout warn">
<span class="label">stdout is the wire</span>
<p>A stray <code>print()</code> lands in the middle of a frame and desynchronises the stream, which
surfaces as circuitRF reporting a malformed reply nowhere near the print. Write to
<code>sys.stderr</code>; circuitRF surfaces that.</p>
</div>

## A complete worked example: a microstrip line {#worked}

The full, running version is `tools/pcell-python/example/mlin.py` in the repository. It is deliberately
written **from the contract**, not transcribed from circuitRF's own built-in MLIN — the test that
compares the two asserts byte-identical geometry, and that proves something only because they were
arrived at independently.

Walking the four artefacts for this one part:

**Schematic side.** MLIN's symbol declares two pins and two parameters, `W` and `L`. Whatever your kit
ships as a symbol description is translated on import.

**Model.** MLIN's electrical model is circuitRF's own microstrip line — it reads `W`, `L` and the
substrate from the technology. A kit supplying its own device would point at a compiled model library
instead; see [PDK Integration](pdk-integration.html#models).

**Technology.** The generator asks for `tech.signal_layer` and gets the resolved answer. The substrate
underneath it comes from the same `.ctech`, which is also what the electrical model reads — so the
artwork and the model cannot disagree about which stack they are on.

**Artwork.** The generator above. Three things in it are the contract rather than a choice:

```python
w = params.length("W")           # DATABASE UNITS, already converted
length = params.length("L")
half = w // 2                    # integer halving — an odd width lands the same way in C# and Python

return Result(
    shapes=[Rect(layer, 0, -half, length, half)],
    pins=[
        Pin("1", 0,      0, layer, w, 180.0),   # pin 1 AT THE ORIGIN, facing out to the left
        Pin("2", length, 0, layer, w,   0.0),   # the line runs along +X
    ],
)
```

- **Pin 1 sits at the cell origin, and the principal axis runs along +X.** Every PCell follows this, or
  nothing abuts.
- **The pins carry width and outward direction.** That is what makes them connectable, snappable and
  usable as EM ports.
- **The width is halved in integer arithmetic.** circuitRF's built-in does the same, so an odd width
  straddles the axis identically on both sides.

### Checking your work

```text
python3 tools/pcell-python/verify.py
```

Self-contained: no circuitRF, no .NET, no test framework. It drives the example generators as real
subprocesses over the real wire.

## Parameter handles {#handles}

A [parameter handle](pcells.html#handles) makes a piece of your generated artwork **draggable**, with the
drag editing the parameter that produced it. Declaring one is a list argument on the same `Result`, in
the same function, in the same file — there is no separate declaration file, no second language and no
registration step:

```python
@generator("MLIN", [Parameter.length("W"), Parameter.length("L")])
def mlin(params, tech):
    w = params.length("W")
    l = params.length("L")
    layer = tech.signal_layer
    return Result(
        shapes=[Rect(layer, 0, -(w // 2), l, w // 2)],
        pins=[Pin("1", 0, 0, layer, w, 180.0), Pin("2", l, 0, layer, w, 0.0)],
        handles=[
            Handle("L", anchor=(0, 0),     at=(l, 0),     axis=0),
            Handle("W", anchor=(l // 2, 0), at=(l // 2, w // 2), axis=90),
        ],
    )
```

A handle states four things: **which parameter** it drives, **where the grip is** in cell-local database
units, **which way it moves**, and — optionally — a **label** and a **legal range**. That is all.

<div class="callout note">
<span class="label">You never state how much the parameter changes per unit of travel</span>
<p>circuitRF measures it, by asking your generator: at the start of a drag it perturbs the parameter,
regenerates in memory, and reads where the same handle moved to. That is why no units appear in the
declaration above — and why the same cell written in C# and in Python reads identically.</p>
</div>

**Declaring no handles is a complete and correct choice.** A generator that declares none behaves
exactly as it always did, and no existing cell has to be revisited.

## A second example: a spiral inductor {#spiral}

`tools/pcell-python/example/spiral.py`. Where MLIN is the smallest cell that is still real, this is the
smallest cell that is still *interesting*: four parameters that interact, so it shows what a
parameterised cell actually buys you.

```python
@generator("SPIRAL", [Parameter.length("Width"), Parameter.length("Space"),
                      Parameter.length("Inner"), Parameter.integer("Turns")])
def spiral(params, tech):
    w     = params.length("Width", 10_000)
    s     = params.length("Space", 10_000)
    inner = params.length("Inner", 100_000)
    turns = params.integer("Turns", 3)

    if turns < 1:
        raise ValueError(f"a spiral needs at least one turn; got {turns}")

    layer = tech.signal_layer or FALLBACK_LAYER
    pitch = w + s
    half, half_inner = dbu(w / 2), dbu(inner / 2)

    shapes = []
    for t in range(turns):
        r      = half_inner + half + (turns - 1 - t) * pitch   # this turn's centre-line radius
        r_next = r - pitch
        shapes.append(Rect(layer, -r - half,  r - half, r + half,  r + half))   # top
        shapes.append(Rect(layer,  r - half, -r - half, r + half,  r + half))   # right
        shapes.append(Rect(layer, -r - half, -r - half, r + half, -r + half))   # bottom
        top = r_next + half if t < turns - 1 else half                          # left, stopped short
        shapes.append(Rect(layer, -r - half, -r + half, -r + half, top))
        if t < turns - 1:                                                       # step inward
            shapes.append(Rect(layer, -r - half, r_next - half, -r_next + half, r_next + half))
    ...
```

Three things in it are worth copying:

- **`Turns` is an `integer`, not a `length`.** It carries no dimension, so circuitRF does not scale it.
  Getting this wrong is trap 4 above, in its most literal form.
- **The artwork is a chain of overlapping rectangles, not one closed outline.** A spiral drawn as a
  single polygon needs a mitre rule at every corner, and getting that rule wrong is the classic way a
  spiral's inductance comes out plausible and wrong. Overlapping rectangles on one layer union exactly,
  because the coordinates are integers, and every corner is square by construction.
- **Zero turns raises.** An empty `Result` would be a silently empty cell, which looks like it worked.

The inner terminal is left facing +Y rather than being routed out: reaching it needs an air bridge or an
underpass on another layer, which belongs to whatever places the cell, not to the cell.

## Declaring your generators to circuitRF {#declaring}

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

| Key | What it is for |
|---|---|
| `entry` | The script that calls `run()`. Relative to this file's own folder, so the kit can be moved or copied whole and still resolve. |
| `pythonPath` | Added to `PYTHONPATH`, so your kit's own modules import with nothing to configure. |
| `sources` | What your generators are built from — the cache key that decides whether an already-generated cell can be reused. Omit it and the entry script's own directory is used, which is the ordinary layout. **Not** `pythonPath`: that may point at a shared environment you do not own. |
| `dataFiles` | Files your geometry depends on but which are not source — a table of pad sizes, a device list. **Reading a file is fine provided its content is part of your cell's cache key, and declaring it here is how it becomes part of it.** Undeclared, editing it changes nothing: circuitRF keeps handing out the cells built before you changed it. |
| `interpreter` | Usually omitted; circuitRF finds one. Set it when your cells need packages that live in a particular environment. **circuitRF does not bundle an interpreter and does not install packages on your behalf** — a kit that needs an environment declares it. |

It deliberately does **not** list the generators the kit offers. The `describe` call is the only source
of that, and a second one would be a cache that can silently disagree with the script.

## Traps worth a callout {#traps}

<div class="callout warn">
<span class="label">A parameter declared as text silently ignores a number</span>
<p>Kit PCell parameters are commonly declared as <strong>strings</strong>. Supply a numeric value where a
string is expected and it can be <em>ignored</em> — the generator falls back to its default. There is no
error message. The symptom is <strong>wrong artwork</strong>, and it will look entirely plausible.</p>
<p><strong>Tell your users to check the produced geometry</strong>, not the messages. And when you author
a parameter, ask whether it really needs to be text: a number declared as a number cannot fail this
way.</p>
</div>

<div class="callout warn">
<span class="label">A unit belongs in the row's unit field, not in the value</span>
<p>circuitRF's expression parser has no unit-suffix production, so a value written as
<code>60u</code> in an expression is a <em>parse error</em> — and an unresolvable value is skipped rather
than reported at the point you typed it. Some kits' own scripting accepts that spelling, which means the
difference does not show up until Run. See <a href="units.html#unit-field">Units</a>.</p>
</div>

**Non-determinism poisons the cache silently.** It is worth repeating because it is the failure with no
symptom: generated cells are cached on `(cell, parameter values, technology)`, so a generator that
consults anything outside those inputs produces stale or inconsistent geometry with no error anywhere.

**Do not flatten your own curves.** Emit `Edge.arc(bulge)` and `Edge.cubic(…)` and let circuitRF flatten
at screen resolution. Flattening is a rendering decision, and a generator that pre-flattens has made it
once, at the wrong time, for every zoom level.

<p class="small">See also: <a href="pcells.html">PCells</a> ·
<a href="pdk-integration.html">PDK Integration</a> · <a href="units.html">Units</a> ·
<a href="layout-editor.html#technology">Technology</a> ·
<a href="components.html#veriloga">Compiled Verilog-A models</a> · <a href="sdd.html">The SDD</a>.</p>
