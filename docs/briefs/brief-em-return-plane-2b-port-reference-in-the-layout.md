# Sonnet Brief — RP-2b: the port's return is part of what the port IS

**Read `brief-em-return-plane-2-per-port-reference.md` and then RP-2a
(`brief-em-return-plane-2a-two-cut-port-kernel.md`), which this depends on and must not start before.**
RP-2a builds the kernel's two-cut port; this brief is how a user says which return a port has, and how
the run tells them what it did.

**Scope: `src/Design` and `src/Ui` only.** No engine change. If this brief needs one, RP-2a is
incomplete and the finding belongs there.

---

## 1. The field

**R-rp2b-1. The reference is a property of the port LABEL, not of the `.cem`** — RP-2's own R-rp2-8. A
port is drawn in the layout and its return is part of what it is; putting it in the `.cem` would split
one port's identity across two files and make a layout non-portable between setups.

`LabelShape` gains two nullable properties, additive, **no `FormatVersion` bump**, null meaning "the
stackup's ground plane" — the same shape `PortDirection` and `PortLayer` already have:

- the reference kind (ground plane / coplanar ground / second conductor), and
- the negative terminal's coordinate, in the layout's own DBU frame.

**R-rp2b-2. `LayoutGeometry.cs` clones `LabelShape` property by property.** A property added to the
record and not to that clone is dropped silently on copy, paste and every geometry transform. Add it
there in the same commit, and gate it — a round-trip test that copies a port and asserts the reference
survived.

**R-rp2b-3. The negative terminal is NOT a second label.** Two labels naming one port number is
already a refusal by name (§10.6); reusing that spelling for a port's two terminals would make the
existing refusal wrong.

**R-rp2b-4. Round trip both ways.** A `.clay` with the fields set, written and re-read, is unchanged;
one without them gains no key, and every existing fixture's `.clay` is byte-identical.

---

## 2. How it resolves

**R-rp2b-5. The negative terminal resolves exactly as the positive one does.** `EmPortExtraction`
already resolves the positive terminal against the mesh and reports how far the chosen gridline landed
from the point asked for. Call the same function. **A second resolution rule is a second chance for the
two terminals to land on different levels without anyone noticing** — RP-2's R-rp2-2, and L9d's whole
reason for making a port's level part of its identity.

**R-rp2b-6. Every `PlanarPort` is still built at one site.** `EmPortExtraction` constructs them in one
place and already branches per-kind for `LayerIndex` and `GroundPathWidthM`; the reference and the
negative terminal are two more arguments there, not a second construction path.

**R-rp2b-7. The refusals name what is missing and where to fix it**, in the extraction's own voice —
the same shape as the existing "internal delta-gap port with no direction on it" refusal:
- a coplanar/second-conductor reference with no negative terminal stated;
- a negative terminal that lands on no conductor, or on the same conductor as the positive one;
- a conductor-referenced EDGE port, which RP-2a refuses in the kernel and which must be caught here
  too, with the layout-side remedy, because a user reads this message and not the kernel's.

---

## 3. What the run says

**R-rp2b-8. "Every port returns through …" becomes per-port where the ports differ** — RP-2's R-rp2-9.
That note lives in `PlanarExtractor.cs`'s R-em-4 block in two spellings (inferred and `.cem`-overridden)
and **both** assert *"That plane is the negative terminal of every port in this run and is not
selectable per port."* This brief makes that sentence false and it must not be left standing.

The shape to aim for: the plane keeps being named once, with its height, because it is still the
medium's boundary condition and still the return for every port that did not say otherwise — and then
the ports that DID say otherwise are named individually, each with the conductor its negative terminal
landed on. A run where every port is ground-referenced must read **exactly** as it does today.

**R-rp2b-9. `circuitrf explain` agrees with the run.** RP-1 made `explain` report the return plane;
it must now report each port's own return, and it must report what the EXTRACTION resolved rather
than a transcription of the rule. Gate it against the extraction, not against the expected sentence.

**R-rp2b-10. The Port tool and the property panel expose it**, on the terms `PortDirection` already
set: a port placed on ordinary metal gets null (the plane) and reads as it always did; naming a return
is a deliberate act. Picking the negative terminal is a click on metal, resolved and reported the same
way — never a nearest-conductor guess, because a guess here silently references the answer to the
wrong conductor.

---

## 4. Gates

1. **Every existing `.clay` round-trips byte-identically**, and every existing fixture's extracted
   `PlanarProblem` and s-parameters are unchanged. Run this first.
2. **R-rp2b-2's clone gate**: copy a port carrying a reference, assert it survived.
3. **A layout-authored mixed run**: two ports drawn in a `.clay`, one plane-referenced and one
   conductor-referenced, extracted and solved end to end, agreeing with RP-2a's directly-constructed
   equivalent.
4. **Each of R-rp2b-7's refusals fires and names its remedy.** Assert the sentence.
5. **The note**: a ground-only run's note is character-for-character what it is today; a mixed run's
   names the differing ports and their conductors.
6. **`explain` agrees with the extraction** for the same `.cem`.

---

## 5. On completion

Findings to `src/Design/RESOLVED.md` — **never a `CLAUDE.md`**. Update `docs/design/layout-view.md`'s
`Label` row and `docs/design/cli.md`'s `explain` section if its output changed.

**Report, do not silently absorb:**
- Whether any existing fixture's `.clay` or `PlanarProblem` moved at all under gate 1. It must not.
- Any second place `LabelShape` is copied field-by-field besides `LayoutGeometry.cs`. One was known;
  a second is the finding.
