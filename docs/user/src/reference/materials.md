---
title: Materials
slug: reference/materials.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Materials
lede: What a material is, where it is kept — a technology's own list or a .cmat library it names — and how to enter one in the Materials editor, step by step, with its electrical, thermal and anisotropic values and its look in the realistic view.
keywords: material, materials, tissue, body tissue, Cole-Cole, dispersion, frequency-dependent, phantom, tissue-simulating liquid, SAR, Gabriel, .cmat, material library, .ctech, technology, generic materials, built-in materials, dielectric, conductor, permittivity, εr, er, loss tangent, tanδ, tan delta, permeability, μr, conductivity, σ, sigma, temperature coefficient, α, alpha, σ(T), k(T), thermal conductivity, density, specific heat, anisotropic, anisotropy, tensor, laminate, appearance, realistic view, colour, source, delete material, rename material, Air, material.conflict
---

A **material** is a named record of values: a relative permittivity, a conductivity, a thermal conductivity, a colour, a
look. Every stackup entry, 3D solid, bond wire and air box names one, and every solver reads its values from it.
Nothing is a dielectric or a conductor by nature: **what a material states decides what it is.** Stating εr makes it
a dielectric, stating σ₂₀ makes it a conductor, and a value left empty is *not stated* — never zero. Where a material is
used decides what it must state, and `check` says when it does not: a thermal run, for instance, refuses a solid whose
material states no thermal conductivity.

This chapter is in three parts: [where materials are kept](#cmat-and-ctech), which is the part people find confusing;
[entering a material](#new) in the Materials editor, card by card; and [renaming and deleting](#delete) one.

## Where materials are kept: .ctech and .cmat {#cmat-and-ctech}

There are two kinds of file a material can live in, and they hold **exactly the same record**:

- **A technology (`.ctech`)** is your process: its layers, its stackup, its design rules — and its **own list of
  materials**. See [Technology](layout-editor.html#technology) and [Stackup](stackup.html).
- **A material library (`.cmat`)** holds materials and nothing else (and the thermal contact resistances between pairs
  of them; see [Thermal](thermal.html)). It is not a design, has no layers, and is never used on its own.

The two are joined in **one direction only: a technology names its libraries.** It lists each `.cmat` by a path
relative to the `.ctech`, so a workspace moved or copied as a whole still finds them. Several technologies may name the
same library, and that is what a library is for — one list of laminates, or one company's characterised metals, shared
by every process that uses them. A workspace, a schematic, a layout, an EM setup and a 3D view **never** name a library
themselves; they name a technology, and the technology brings its libraries. That is what makes one technology give the
same answer everywhere it is used.

**What a design sees** is the technology's own materials **plus** every material of every library it names, as one
list. There is **no order of precedence** between them. A name may appear in more than one place only if every copy
states the **same values**; then they are one material. If two copies differ, there is no right answer to pick, so the
technology **does not load**: `check` reports `material.conflict`, naming the material and both files, and the
technology's Materials tab says the same. Make the values equal, or rename one.

{{ui: materials-technology-tab}}

**A material belongs to one file, and is edited there.** On a technology's Materials tab its own materials are edited in
place; a library's rows are listed below them, read-only, because a change to a library changes every technology that
names it. *Edit in generic-materials.cmat* on such a row, or **Open Library**, opens the library as its own document,
with its own undo and its own Save. The strip at the foot of the tab manages which libraries the technology names:

| Control | What it does |
|---|---|
| **Add Library…** | Names an existing `.cmat` — stored relative to this `.ctech`. |
| **New Library…** | Writes an empty `.cmat` beside the technology and names it. |
| **Add Generic Materials** | Copies circuitRF's generic library beside the technology and names it. A different file of that name already there is never overwritten; you are told to rename or move it. |
| **Open Library** | Opens that library as its own document. |
| **Remove** | Stops naming the library. The file itself is left where it is. |

A `.cmat` also appears in the **Project Tree** beside the technologies; double-click it to open it.

**Which to use.** Put a material in the technology's own list when it belongs to that process alone — the MIM dielectric
of one wafer process, say. Put it in a library when more than one technology should share it, so that a corrected value
is corrected once.

**The generic library.** circuitRF ships `generic-materials.cmat`: metals, ceramics, semiconductors, laminates, glass,
die-attach and thermal materials, every value with its source. When **New Workspace** copies a shipped technology, it
copies the libraries that technology names beside it. From then on the copy is the **workspace's own file**: you may edit
it, and a later circuitRF release never changes it. The package button in the editor's toolbar lists the same materials
as **built-in**; assigning or editing a built-in material copies it into one of your lists, where it is yours from then
on.

## The Materials editor {#editor}

One editor shows materials everywhere they can be edited, and it opens in three places:

- **A `.cmat` document** — the library itself (double-click it in the Project Tree, or *Open Library*).
- **A technology's Materials tab** — its own materials, then its libraries' rows below them.
- **The Materials dialog of a 3D view** — *Assign Material…*, *New Material…*, or *Edit…* beside the Inspector's
  Material box; see [The 3D Editor](drawing-in-3d.html#view). Edits there go to the file each material belongs to when
  you press **OK**, as an unsaved change in that file.

On the left is the list: one row per material, with its colour, its name, what it is (*Dielectric*, *Conductor*,
*Air*, *States nothing*, or *Ambiguous* — it states both εr and σ₂₀, so an object made of it must be given a
**Role** in the 3D view) and the file it comes from. **Filter** narrows the list by name, role or file. On the right
is everything the selected material states, on one form, in cards: **Dielectric**, **Conductor**, **Thermal**,
**Appearance** and **Notes**. A row's swatch is the material's colour: its ordinary-view colour when one is set, else its
base colour in the realistic view.

Every field commits when you press **Enter** or leave it; **Escape** puts the shown value back. Each change is one undo
step, and nothing reaches the disk until you **Save** the file it belongs to. An empty field is *not stated* and is left
out of the file.

## Entering a new material {#new}

The example below adds a laminate, *My laminate*, to a `.cmat`. The steps are the same on a technology's Materials tab.

**1. Add it and name it.** Press **＋ New**. A row called *Material1* is added and selected, with its name ready to be
typed over. Type the name and press Enter. A name is unique within the technology, ignoring case, and `@` is reserved.
**Duplicate** instead copies the selected material — every value and table — under a new name, which is the quick way to
make a variant of something that already exists.

When the editor holds more than one list (a technology tab with libraries, or the 3D view's dialog), *New, duplicated and
built-in materials go to* chooses which file a new material is written to.

**2. Say what it is.** For a dielectric, type its **relative permittivity εr** and **loss tangent tan δ** on the
Dielectric card; **μr** is needed only for a magnetic material. The chip beside the name changes to *Dielectric*. For a
metal, type its **conductivity σ₂₀** at 20 °C on the Conductor card instead, and its **temperature coefficient α₂₀** if a
3D setup or a thermal run should follow its temperature: σ(T) = σ₂₀ / (1 + α₂₀ (T − 20 °C)).

{{ui: materials-new}}

A material can state both εr and σ₂₀: that is a **conducting dielectric**, such as silicon, and a stackup entry naming it takes both. One with
σ₂₀ and no εr is a conductor. The library holds `Silicon, 10 kΩ·cm`, `Silicon, 800 Ω·cm`, `Silicon, 10 Ω·cm` and `Silicon, 0.01 Ω·cm`, each with tan δ 0 because the
conductivity is the whole of its loss. `High-resistivity silicon` is the constant tan δ of 0.0015 instead. See
[A conducting substrate](stackup.html#conducting-substrate).

**3. Say where the values come from.** Every solver takes εr and tan δ as constants, so the **Notes** card at the foot
of the form is where to record the frequency they were stated at — and the datasheet or paper they came from. The
note is for people: the solvers read the values themselves, never the note.

A material named **Air** is air, whatever it states: it is never meshed by a thermal run and fills an air box by default.
Renaming a material to or from *Air* changes its role, and the editor says so.

## Thermal properties {#thermal}

A thermal run needs a **thermal conductivity k** for every solid it meshes, and refuses one whose material states
neither k nor a k(T) table — the card says so while it is missing. **Density** and **specific heat** are what a thermal
impedance Z_th(jω) reads; a steady-state run does not need them.

Thermal conductivity usually falls as a material heats, and for a substrate under a hot device that matters. Open
**k(T) table** to state it point by point: **＋ Add point** adds 20 °C at the constant k on an empty table, then one
25 °C above the last point. Each point is a temperature and a value; the table keeps itself in order of temperature and
refuses two points at one temperature. Once stated, **the table wins over k**: a thermal run interpolates it linearly and
holds its end values beyond its range, and its notes say when it did. `check` warns when the table and k disagree at
20 °C by more than 1 %. **Clear table** removes it, and the run reads k again.

The Conductor card's **σ(T) table** works the same way for electrical conductivity. A thermal run's electrical half
(a bond wire's or a trace's resistive heating) reads it in place of σ₂₀ and α₂₀; every EM solver still reads σ₂₀, so
stating a table moves no EM answer. A **wBond** component's wires read the shipped metals' tables too, at the
component's `Temp` — held at the table's nearest end outside it, with a warning in the Messages panel.

{{ui: materials-thermal}}

## Body tissues and phantom liquids {#tissue}

A body tissue's permittivity and conductivity change strongly with frequency: muscle's σ is about 0.69 S/m at 64 MHz and
0.98 S/m at 1 GHz. A material can therefore state a **frequency-dependent model** instead of one fixed pair:

- a **Cole-Cole model**, the parametric form body tissues are published in. circuitRF evaluates it at any frequency.
- a **table of target values at named frequencies**, the way a tissue-simulating liquid is specified. It is
  interpolated between rows and **refused outside them**.

A 3D run evaluates every such material **once**, at the setup's first SAR frequency when it computes SAR and otherwise
at the sweep's centre, and the solver uses those values across the whole sweep. The run's notes give the frequency and each value. `explain` with `--materials` lists the problem's
materials as the solver receives them. Because the values hold at that one frequency only, a result such as SAR is valid
there and nowhere else. To work at another frequency, make a setup for it. A tissue is a lossy dielectric, so an
object made of one needs no Role.

**Add Tissue Materials**, on the technology editor's Materials tab and at the top of the 3D view's Materials dialog,
copies circuitRF's tissue library beside the technology and names it. In the dialog its tissues are listed at once, to
assign, and the library is named when you press OK; Cancel leaves nothing behind. The library holds:

- sixteen body tissues from the 1996 parametric model of Gabriel, Lau and Gabriel;
- head and body tissue-simulating liquids for phone SAR, from the FCC's published target table;
- a saline gel of the kind used for implant heating tests;
- the tissue values openEMS's own SAR and MRI examples use.

Every record cites its source. Where no source gives a tissue's density, the record states none: add one, with its
source, to your own copy before computing SAR in that tissue.

The whole library, its sources and the phantoms it fills: [Tissues and phantoms](phantoms.html).

Selecting a tissue shows a **Frequency dependence** card: εr and σ **at f**, for a frequency you type (`298M`,
`298 MHz`), and a small plot of both against frequency. The model is in the record and is edited in the file. εr and
σ₂₀ on the cards above are the model's values at 1 GHz, which is what a build without the model reads. `check` warns
when they disagree with the model.

## Anisotropic materials {#anisotropic}

A woven-glass laminate has a different permittivity in the plane of the board than through it, and a crystalline
substrate such as sapphire differs along its axes. Tick **Anisotropic εr** to state three values instead of one: **εr xx**,
**εr yy** and **εr zz**, along the 3D view's own x, y and z axes. Ticking it fills all three with εr, so start from there
and change the ones that differ. Unticking it removes the three values.

{{ui: materials-anisotropic}}

**Who reads which.** A **3D solver** reads the three values and not εr. The **planar solvers**, such as the [MoM engine](mom-engine.html), have no
direction, so they keep reading **εr**. Keep εr stated, at the value a planar solve
should use.

Thermal conductivity has the same choice on the Thermal card: tick **Anisotropic k** for **k xx**, **k yy** and **k zz**,
at 25 °C — a laminate conducts heat along its glass far better than through it. A thermal run's volume solve reads the
three values; anything with no direction (a bond wire, an effective block's mixing, `explain`) keeps reading k or the
k(T) table. With both stated, each direction follows the table's change from its 25 °C value. The axes are the view's,
fixed: **the tensor does not rotate with a rotated solid.**

## How it looks {#appearance}

The **Appearance** card decides how a material is drawn, in both 3D views, and no solver reads any of it. It holds two
colours because the two views have different jobs:

- **Base colour** and the fields with it set how it looks in the [realistic view](drawing-in-3d.html#realistic): how
  metallic, how rough, how see-through, and the rest of the glTF metallic-roughness set. A metal's base colour is its
  true reflectance — silver's is nearly white. A field it does not state shows, greyed, the value it takes from its role;
  **Like** borrows another material's whole look. The sphere is the material under the default studio, so a look can be
  judged with no 3D view open. Every field is described under [Appearance](drawing-in-3d.html#appearance), and
  [Changing how things look](drawing-in-3d.html#changing-look) says when to set a look on the material and when on one
  object.
- **Ordinary-view colour** sets the colour the ordinary 3D view draws every object of the material in. That view is a
  diagram, so by default it draws each conductor in its layer's colour (top and bottom metal stay apart) and each
  dielectric in the view's palette; leave the field empty for that.

{{ui: materials-appearance}}

Changing a colour or an appearance never makes a result out of date: two materials that differ only in how they look are
the same material to every solver.

## Renaming and deleting {#delete}

**Renaming** a material in its own file renames it everywhere it is used in files that are open — each file gets one
undo step of its own. A file that is not open is listed rather than rewritten; `check` then reports it as naming an unknown
material until you change it. The 3D view's dialog renames only a material it has just made, because renaming one that
other files already name is the file's own editor's job.

**Deleting**: select the material and press **Delete**, or right-click its row and choose **Delete Material**. If nothing
in the workspace uses it, it goes at once. If anything does, a `.cmat` lists every use first — stackup entries and bodies
in the technologies that name the library, objects in open 3D views, and `.c3d` and `.ctech` files that are not open —
and deletes only when you press **Delete Anyway**. Nothing that names it is rewritten, so `check` reports each of those
as an unknown material afterwards. The delete is one undo step, so it can be taken back until the file is saved.

The 3D view's **Materials dialog** deletes the same way, with two differences. A material you made in the dialog goes at
once, because no file names it yet. And, like every edit there, a delete reaches its file only when you press **OK**;
**Cancel** keeps the material. The dialog has its own **Undo** and **Redo** (the arrows beside OK, or Ctrl/⌘+Z and
Ctrl/⌘+Shift+Z outside a text box), which take back any edit made in it, a delete included. After OK, the whole change is
one undo step in each file it went to. On a technology's Materials tab a material in use is not deleted at all; the uses are listed
instead. A library's row on that tab and a built-in material cannot be deleted from where they are shown — open the
library to delete one of its materials.

{{ui: materials-delete-menu}}

## In the file {#file}

A `.cmat` is JSON, and each material is one record in its `Materials` array — the same record a technology's own
`Materials` list holds. Every key but `Name` is optional, and a key left out is a value not stated:

```json
{
  "FormatVersion": 1,
  "Materials": [
    {
      "Name": "My laminate",
      "Epsr": 3.5,
      "EpsrTensor": [3.66, 3.66, 3.5],
      "TanD": 0.004,
      "ThermalK": 0.62,
      "ThermalKTensor": [0.8, 0.8, 0.62],
      "ThermalKVsTemp": [ { "TempC": 20, "Value": 0.62 }, { "TempC": 150, "Value": 0.57 } ],
      "DensityKgM3": 1900,
      "SpecificHeat": 900,
      "Source": "The supplier's datasheet, stated at 10 GHz.",
      "Appearance": { "BaseColor": "#C9B98A", "Roughness": 0.55 }
    }
  ]
}
```

A conductor states `Sigma20` (S/m) and `Alpha20` (1/K), and may state `SigmaVsTemp` in the same form as
`ThermalKVsTemp`; `Mur` is μr; `Color` is the ordinary-view colour. A technology names its libraries in its own
`MaterialLibraries` list: `"MaterialLibraries": [ "generic-materials.cmat" ]`. See
[File formats](file-formats.html) for every document type.
