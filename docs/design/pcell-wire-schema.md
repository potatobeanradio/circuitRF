# circuitRF — PCell Wire Schema

**Status:** Shipped (wire version 5) · **Date:** 2026-08-04 · **Phase:** B1, amended by C2

**Reads with:** `pcell-contract.md` (the contract this carries across a process boundary),
`layout-view.md` §3.1 (the shape vocabulary), `pdk-external-devices.md` §4 (the frame split this
reuses the reasoning of).

---

## 0. What this is, and why it is its own document

`pcell-contract.md` defines what a PCell *is*: `Generate(parameters, technology) → {shapes, pins}`.
It is deliberately host-neutral — §9 leaves open whether a third party writes one in C#, in a
compiled plugin, or in a script.

This document defines how that contract crosses a **process boundary**: the bytes on the wire when
the generator is not in circuitRF's own process. It exists separately because it has a different
lifetime. The contract can be revised while there are no third-party cells; **the wire cannot be
revised once anyone has shipped a cell against it**, because their cell is a program that speaks
these bytes and nothing else. Every rule below is chosen for that.

**This is the part of Track B that is irreversible.** The rest can be revised.

---

## 1. The one decision everything else follows from: the wire has no metres in it

`pcell-contract.md` R7 says length parameters are SI metres and the conversion to DBU happens in
one place with one documented rounding rule. Across a process boundary that is not a documentation
note, it is a schema constraint — an API that hands a script a length in metres has, on day one,
two rounding rules: circuitRF's and whatever the script author wrote.

**So the host converts, and the wire is unable to express the alternative.**

| | On the wire |
|---|---|
| Every length, coordinate, thickness, radius, width | **int64 DBU** |
| Dimensionless quantities (relative permittivity, loss tangent, a reflection coefficient) | double |
| Angles | double **degrees** |
| Conductivity | double S/m — *not a length*, see below |

**There are no metres in any message, and that is still the mechanism.** A script cannot convert
metres to DBU because it is never given a metre. Length parameters arrive already converted; there
is nothing for a script to convert and so there is still exactly one rounding rule across the
boundary.

> **Amended in wire version 2 (2026-08-04, C2).** Version 1 also withheld `dbuPerMicron`, on the
> reasoning that removing the resolution as well as the metres left no field the arithmetic could
> happen in. The generate request now carries it (`Technology.dbu_per_micron` on the Python side).
>
> **What changed and what did not.** Metres are still absent, and length parameters are still
> converted by the host — so the property that actually matters is untouched. What version 2 adds is
> the ability to express a constant the **generator itself holds**: a process dimension out of a
> kit's own data, which is a physical length in micrometres and had no other way of becoming a
> coordinate. That case was named below as needing a version bump, and adapting a vendor PCell
> library is it — those cells compute in absolute microns throughout.
>
> **The narrower door, stated so it is not widened by accident.** A generator may scale *its own*
> constants. It still cannot be handed a metre and convert it, because it is still never handed one.
> A future change that put metres on the wire would undo the actual guarantee and is a different
> decision from this one.
>
> **Done while nothing in the field speaks version 1.** A bump refuses every older script, and the
> cost of that rises with every third-party cell ever written; at the time of the change there were
> none. It rides on the technology object rather than becoming a third generator argument — a new
> argument is a CONTRACT change and would break every generator, while an extra attribute breaks
> none.

**Why this does not break the in-process contract, and does not change any built-in generator.**
R7's "one place" is `PCellUnits.MetresToDbu`. An in-process C# generator calls it itself; for an
out-of-process one the *host* calls the same function on the way out. Same function, same rounding,
one rule — the script simply never participates. This is what keeps B7's gate reachable: the same
cell written twice, in C# and in a script, must produce byte-identical geometry, and it does
because neither one is doing its own conversion.

**Which parameters are lengths is the generator's own declaration**, reported by `describe`
(§4.1). That is what makes `describe` load-bearing rather than a handshake pleasantry: the host
cannot convert what it has not been told is a length.

**The former limitation, and how it was resolved.** Version 1 was scale-free: a script was given a
conductivity in S/m but no metre and no resolution, so it could compute a length from a **ratio**
(which covers the closed-form microstrip relations, all functions of `W/h`) but not from a physical
constant. That was stated here as needing a version bump rather than a workaround — and version 2 is
that bump, for the case that forced it: a vendor PCell library whose cells carry the process's own
dimensions in micrometres. See the amendment above for exactly what widened and what did not.

**This is not an invention.** `StackupLayer.ThicknessDbu` is already DBU in circuitRF's own
technology model; SI metres are a *derived* view computed by `SubstrateResolver` for the electrical
models. The wire carries the stored form.

---

## 2. Frames

Identical layout to the device-worker protocol (`pdk-external-devices.md` §4), for identical
reasons — a JSON control plane so a frame is readable in a hex dump when something goes wrong, and
raw little-endian binary for bulk geometry so a large cell costs no parsing:

```
[ uint32 jsonLen ][ uint32 binLen ][ jsonLen bytes UTF-8 ][ binLen bytes of int64 ]
```

`binLen` is a **byte** count, not an element count — it is what the reader must consume, and an
element count is ambiguous the moment anything but an int64 is ever carried.

**The payload element type is int64, and the device path's is double.** That is the whole reason
this is a sibling codec rather than the same class: the length checks, the desync messages and the
element arithmetic all differ, and the device path is live against a production kit and is not to
be destabilised to share eighty lines. The property both must keep — **a partial read on a pipe is
normal and must be looped, never treated as end-of-stream** — is tested independently on each side,
because getting it wrong produces frames that decode as garbage only under load.

**Why bulk geometry is binary when the round-trip count is tiny.** It is not for speed. §B of the
development plan is explicit that latency does not matter here, unlike the device path. It is
because an int64 array **cannot express a fractional coordinate**: a JSON number can arrive as
`3.5`, and a script emitting one would be silently rounded by whoever read it. The binary payload
is the same kind of constraint as §1 — the schema is unable to say the wrong thing.

---

## 3. Values

A parameter value is kinded exactly as `PCellValue` is: **Real, Int, Bool, String**. The JSON
encoding is **the same one `.clay` uses**, deliberately and not by coincidence:

| Kind | JSON |
|---|---|
| Real | a bare number — `0.05` |
| Bool | `true` / `false` |
| String | a bare string — `"nch_lvt"` |
| Int | `{"int": 4}` |

Int is tagged because JSON has a single number token and Int and Real are different inputs to the
content hash that names a generated cell (see `pcell-contract.md` R6). One encoding for the file
and the wire means a value cannot mean two things in the two places; two encodings would be two
rules to keep in step, and the failure when they drift is a cell that regenerates differently
depending on where its parameters came from.

---

## 4. Messages

Three commands, matching the device worker's shape so the two hosts read alike: `describe`,
`generate`, `shutdown`. Every message carries `"op"`; every reply carries `"ok"` and, when false,
`"error"`.

### 4.1 `describe`

Sent once, on connect. The host learns what the script offers and — critically — **the dimension of
each parameter**, which is what lets §1 hold.

```jsonc
→ { "op": "describe", "wireVersion": 1, "contractVersion": 2 }

← { "ok": true,
    "wireVersion": 1,
    "contractVersion": 2,
    "generators": [
      { "id": "SPIRAL",
        "parameters": [
          { "name": "W",       "kind": "real",   "dimension": "length" },
          { "name": "Turns",   "kind": "int",    "dimension": "none"   },
          { "name": "Angle",   "kind": "real",   "dimension": "angle"  },
          { "name": "Model",   "kind": "string", "dimension": "none"   }
        ] } ] }
```

`dimension` is one of `none` / `length` / `angle`. Deliberately three, not circuitRF's full
`UnitDimension`: those are the only two that change what crosses the wire (length → DBU, angle →
degrees), and offering the rest would invite a script to declare a resistance and expect an
ohm-aware conversion that does not exist.

**Both versions cross in both directions**, and a mismatch is refused rather than negotiated —
§7.

### 4.2 `generate`

```jsonc
→ { "op": "generate",
    "generatorId": "SPIRAL",
    "parameters": { "W": 300000, "Turns": {"int": 4}, "Model": "nch_lvt" },
    "layers": {
      "signal": { "layer": 1, "datatype": 0 },
      "ground": { "layer": 8, "datatype": 0 },
      "table": [ { "layer": 1, "datatype": 0, "name": "Metal1", "purpose": "drawing" } ]
    },
    "stackup": {
      "top": "open", "bottom": "ground",
      "layers": [
        { "kind": "conductor",  "name": "Metal1", "thickness": 35000,
          "sigma": 5.8e7, "isGroundReference": false, "drawingLayers": [ {"layer":1,"datatype":0} ] },
        { "kind": "dielectric", "name": "FR-4",   "thickness": 1600000,
          "epsr": 4.4, "tand": 0.02, "mur": 1.0 }
      ]
    } }
```

`parameters` carries **length values already in DBU** (`W` above is 300 000 DBU = 300 µm), because
`describe` said `W` is a length. A Real stays a Real — the kind describes what the parameter *is*
(continuous, versus a count), not what unit it is in.

**`signal` and `ground` are the resolved answer, never the question.** circuitRF's own substrate
resolution (`SubstrateResolver`, "topmost conductor, nearest ground-designated conductor beneath",
plus any per-instance override) runs host-side and the result crosses. A script re-deriving it
would be a second implementation of a rule whose failure is silent — geometry on a plausible but
wrong layer.

### 4.3 The reply

```jsonc
← { "ok": true,
    "shapes": [
      { "kind": "poly",  "layer": {"layer":1,"datatype":0}, "net": null,
        "xy": {"at": 0, "count": 8},
        "holes": [ {"at": 8, "count": 8} ] },
      { "kind": "path",  "layer": {"layer":1,"datatype":0},
        "xy": {"at": 16, "count": 6}, "width": 300000, "end": "round",
        "edges": [ {"kind":"line"}, {"kind":"arc","bulge":0.414213562373095} ] }
    ],
    "pins": [
      { "name": "1", "x": 0, "y": 0, "layer": {"layer":1,"datatype":0},
        "width": 300000, "outwardDeg": 180.0 } ],
    "diagnostics": [ "turn 4 is inside the minimum bend radius" ],
    "handles": [
      { "parameter": "L", "kind": "linear",
        "span": {"at": 22, "count": 4}, "axisDeg": 0.0,
        "label": "Length", "min": 50000,
        "crossParameter": "Offset", "keepAnchorFixed": true } ],
    "preview": "deferred" }
```

**`handles` (wire version 6) is optional** — draggable parameter grips, see
`pcell-parameter-handles.md`. Absent, which is what every generator written before version 6 emits,
simply means the cell is edited through its parameter list. Each entry's `span` is exactly four
int64 elements: **anchorX, anchorY, x, y**.

`min`/`max` are parameter **values**, not coordinates, so they stay in the JSON and may legitimately
be fractional — §3's encoding, not §2's payload rule. That is the line: a bound of 20.5 Ω is a real
quantity; a coordinate of 20.5 DBU is not.

`crossParameter` (with optional `crossLabel` / `crossMin` / `crossMax`) declares a **second**
parameter driven by dragging ACROSS the grip's own axis — the far end of a taper is "how long" along
it and "how far off centre" across it. Absent on an ordinary one-degree-of-freedom grip.

`keepAnchorFixed` (bool, absent reads as false) asks the host to hold the grip's ANCHOR still in
world space while the grip is dragged, translating the placed instance to do it. It is what makes
"drag this end, keep the other end fixed" expressible at all — a generator cannot move its own
origin, so without it a left-edge drag grows the cell rightwards. See `pcell-parameter-handles.md`
R-pch-4b. Additive within version 6: a script written before the field existed omits it and behaves
exactly as it did.

`preview` is `"auto"` (the default, and omitted when it is) or `"deferred"`: a generator that already
knows it is too expensive to redraw per frame says so and is believed, saving the host the one full
regeneration it otherwise spends measuring.

**An unrecognised `kind` drops that one handle and is reported once per distinct kind — it never
fails the generate and never affects the cell's other handles.** That is what lets a further kind be
added without the next bump becoming a cliff: a newer script talking to an older host loses only the
grips that host cannot draw.

**An unrecognised `preview` reads as `"auto"` and is not reported at all**, which is the opposite
trade from `kind` and deliberately so: `preview` is a performance hint with no effect on the answer,
so refusing over one would cost a working cell to honour a preference.

**Every coordinate is a span into the binary payload — `{"at": i, "count": n}`, both in int64
elements, `n` even.** No coordinate ever appears in the JSON. This is what makes "a fractional
coordinate is unrepresentable" structural rather than a validation rule someone could forget to
apply: there is nowhere to write one.

Scalars that are lengths but not coordinates — a path width, a circle radius, a via drill — are
plain JSON integers, and are **rejected if they arrive with a fractional part**. They are in the
JSON because they are single values and a span for each would be noise; the check is what keeps
them honest.

### 4.4 The shape vocabulary, and what is deliberately absent

`rect` · `poly` (with holes) · `rrect` · `circle` · `curve` (edge list, with holes) · `path` ·
`via` · `label`.

That is circuitRF's own vocabulary (`layout-view.md` §3.1) minus one:

**`bitmap` is excluded, permanently.** A bitmap is a tracing underlay, not artwork — it is already
excluded from booleans, flatten, DRC and every export (R-bmp-3). A generator emitting one would be
emitting something that is not geometry, and admitting it to the wire would mean every future
consumer has to know to ignore it.

An unknown `kind` is **refused by name**, never skipped. Skipping produces a cell that renders,
looks complete, and is missing a piece — the worst failure this boundary can have.

**Curved edges cross as edges, not as flattened points.** `arc` carries the same signed
`tan(sweep/4)` bulge `LayoutEdge` stores, `cubic` carries its two control points as a span. A
script that flattened its own curves would bake a tolerance into the geometry, and `layout-view.md`
§3.2 R9c is explicit that flattening is a *rendering* decision made at screen resolution.

**A flatten tolerance may be declared but is not required.** Absent means the technology's default,
which is the same rule the shape fields already follow.

---

## 5. Errors

A generator that cannot produce geometry replies `{"ok": false, "error": "…"}`. The host surfaces
it **naming the cell** — a script can only say what went wrong, and the host is the only side that
knows which instance asked. That is the same lesson `ExternalDeviceModel` already encodes, reached
from the other direction.

`diagnostics` is for a generator that *did* produce geometry and has something to say about it (the
curvature warning MKlopf already emits). It is not an error channel, and a non-empty `diagnostics`
with `"ok": true` must not be treated as failure.

---

## 6. What is deliberately not here

- **Process lifecycle, transport, interpreter discovery** — B3/B4. This document is the format; how
  two processes come to be talking is a separate, revisable question.
- **The Python-side package** — B2. The wire is language-neutral by construction; the package is a
  convenience over it, not part of it.
- **Trust and sandboxing** — B6. A schema cannot make arbitrary code safe.
- **Instance transforms.** A PCell generates in cell-local coordinates and never sees where it is
  placed (`pcell-contract.md` §6). There is no transform field and there must not be one.
- **Anything that would let a generator read the design around it.** R5's purity is a property of
  the *inputs*, so the way to keep it is to not offer the inputs.

---

## 7. Versioning

`wireVersion` is **6**. It is separate from `contractVersion` (still **2**) and both cross in both
directions.

| Version | Change |
|---|---|
| 1 | Initial format (B1). |
| 2 | `dbuPerMicron` on the generate request, so a generator can turn a constant it holds itself into a coordinate. |
| 3 | `clip` — a frame travelling script→host, §8. |
| 4 | A declared `default` per parameter, so a cell can be PLACED without being told its parameters. |
| 5 | `offset` — grow/shrink, §8. |
| 6 | Optional `handles` and `preview` on the generate reply (§4.3), including a handle's optional `crossParameter`. Additive in shape; the bump is required anyway because versions are compared for equality. |

They version different things and can move independently: the contract describes what a generator
receives (kinded parameters, R5's guarantees), the wire describes how it crosses. A byte-layout
change need not change the semantics, and a semantic change need not change the bytes. Conflating
them means a host that only speaks a new byte layout claims to implement a new contract.

**A version mismatch is refused with both numbers named, never negotiated.** Negotiation means N
code paths and the rarely-exercised ones are wrong; refusing is one path and the message tells the
user exactly what to update.

**Adding a shape kind, a dimension, or a parameter kind is a version bump**, because a host that
does not know it must refuse rather than silently drop it — see §4.4.

**Adding an optional reply field is also a bump**, even though nothing already on the wire changes
shape: versions are compared for *equality*, so a script that emits the new field must also be
speaking the version that describes it. Version 6 is exactly that case.

**A handle `kind` is the one thing that degrades per-item rather than refusing** (§4.3), and the
distinction is worth keeping straight: an unknown *shape* kind means geometry the user would never
see is missing, so it refuses; an unknown *handle* kind means a grip is missing from artwork that is
otherwise complete and correct, so it drops and reports. Losing a grip is recoverable in the
parameter dialog; losing a shape is not recoverable at all.

---

## 8. The other direction: work a generator asks the host to do

Everywhere in §4 the host asks and the script answers. **Wire version 3 lets one message travel the
other way**, and it exists for exactly one reason: **layer booleans must have exactly one
implementation, and it is not the script's.**

circuitRF already clips with Clipper2, over the same int64 database units this wire speaks. A second
clipper on the script side would be two implementations of one rule on either side of a process
boundary — the same reasoning that keeps metres off the wire (§1), with a worse failure mode: a
boolean result off by a database unit produces geometry that renders perfectly and is wrong. So the
script asks rather than computes, and a generator's booleans agree with circuitRF's own by
construction rather than by testing.

### 8.1 The discriminator, and why no state machine is needed

**Every request carries `op`. No reply ever does — a reply carries `ok`.** So a frame arriving while
the host is waiting for a generate reply is a service request if and only if it names an op. The
host needs no mode flag, no sequence number and no correlation id to tell the two apart.

The host's read becomes a loop rather than a single read: service what arrives, write the answer,
keep waiting for the reply it was actually after. This happens **inside** the exchange, on the same
thread, because a service request arrives on the same pipe as the reply and must be answered before
that reply can be read — it is not a second conversation to be run concurrently, it is the middle of
this one.

### 8.2 `clip`

```json
{ "op": "clip", "rule": "and" | "or" | "not" | "xor",
  "subject": [4, 4], "clip": [4] }
```

The two operands are described **only by their ring vertex counts**; the coordinates ride in the
frame's int64 payload — subject rings first, then clip rings, x and y interleaved. Same division of
labour as every other message here (§2): JSON says what, the payload carries how much, and a
several-thousand-vertex operand never reaches the JSON parser.

`not` is **order-dependent**: subject minus clip. A subtraction applied the wrong way round produces
a plausible-looking region, so the direction is fixed by the field names rather than by argument
position.

Reply:

```json
{ "ok": true, "polygons": [ { "outer": 6, "holes": [4] } ] }
```

Coordinates follow in the payload in the same order: each polygon's outer ring, then each of its
holes. **An island inside a hole is a further entry in its own right**, not a nesting level — the
same flattening circuitRF applies to its own boolean results, so what a script sees and what the
layout stores describe the same thing.

### 8.3 Two rules that are easy to get wrong

**Every incoming ring is normalised to positive orientation.** Under circuitRF's single fill rule
(NonZero, stated once for the whole repository) two rings of opposite winding *cancel* rather than
combine — so a set of separate figures whose winding a generator never thought about would silently
lose regions. Normalising makes an operand's rings a plain union, which is what a list of figures
means. The cost is that an input ring cannot itself carry a hole; a shape needing one is built out
of the result rather than fed in as one.

**A bound on service calls per exchange.** A script looping forever on service calls would otherwise
hold the host's own lock and hang with no diagnosis. Real cells measured against a vendor kit issue
single digits to low hundreds; the bound sits far above any honest use and exists only so a runaway
ends as a message rather than a freeze.

### 8.4 `offset`

```json
{ "op": "offset", "deltaDbu": 250, "subject": [4] }
```

Grow (positive) or shrink (negative) a region. Same shape as `clip` — vertex counts here, coordinates
in the payload — with one operand, so one count list. The reply is identical to `clip`'s: regions and
their holes.

**It is on the wire for exactly `clip`'s reason.** Growing one layer out of another is how a kit
derives a well from the diffusion it must enclose, and circuitRF already offsets with Clipper2. Two
implementations would differ at the boundary — a design-rule violation that draws perfectly. The
service uses the SAME join and end style `LayoutBooleans.Offset` uses, so a script's grow and the
editor's own Offset command agree by construction rather than by testing.

**A shrink that consumes the region yields NO polygons, and that is an answer** — the same outcome the
editor's Offset produces, not a failure to report.

### 8.5 What this must never become

A general "run this on the host" channel. Each op is added deliberately, does one geometric thing,
and touches no file, no process and no part of the document. A generator script is somebody else's
code, and this is the entire surface it can reach.

---

## 9. Where this lives, and why that does not matter

The C# implementation is in `src/Ui/Layout/PCells/Wire/`, beside the contract it carries. It is
framework-free (no Avalonia, no Skia) like everything else under `src/Ui/Layout/`.

The open question in the development plan §3.4 — whether the PCell contract should move down to
`src/Core` so PCells can be generated headlessly from `src/Cli` — **does not need answering before
this schema is frozen, and the reason is worth recording.** The schema is a byte format; which
assembly holds the encoder does not change a single byte of it. That question is also larger than
it looks: the contract's outputs *are* `LayoutShape`/`LayerKey`/`Technology`, so moving the contract
alone is impossible — it would mean moving the whole layout model, which is its own decision and
not one B1 forces.
