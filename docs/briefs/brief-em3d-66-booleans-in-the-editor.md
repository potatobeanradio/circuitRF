# Brief 66 — booleans in the editor

**Series:** [3D EM, fourth series](brief-em3d-60-overview.md) · **Tag:** `R-em3d66-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §6.3 (Tier B), §6.3a (precedence), §6.4 (naming), §8.2
point 3 (*a drag is a preview*); overview §1d (absent), §1f (a small tree per object), §1g (names), §1h (the
managed kernel stays), §1j (the worker is never on the frame path)
**Area:** `src/Ui/ThreeD/` (new `C3dEditorViewModel.Boolean.cs`; `TreeMenu`, `TreeGrouping`, `Operations`,
`FaceEdit`, `Hierarchy`), `src/Ui/ThreeD/C3dPropertiesViewModel.cs`, `src/Ui/Views/ThreeD/C3dEditorView.axaml`
(the Boolean panel), `src/Ui/Views/WorkspaceWindow.axaml` (the `3D` menu, both copies),
`src/Render/Scene3D/` (a transient preview batch, ghost draw state)
**Depends on:** 63 (the client, the capability, async preview), 64 (the `Boolean` object, its elaboration,
its names) · **Blocks:** 67 (a fillet on a boolean result), 70
**Owner decisions:** D4 (Tool and Blank), D5 (the tree, **Enabled**), D10 (*Keep tools* off), D11 (Unite of
different materials), D13 (no direct face edits on a result)

---

## 0. What this brief delivers

In **Object** mode, with two or more solids selected, the canvas context menu, the tree's context menu and
*3D ▸ Boolean* offer:

| Operation | Key | What it does |
|---|---|---|
| **Subtract…** | — | the Blank, less every Tool |
| **Unite…** | — | the Blank and every Tool as one solid, with the Blank's name and material |
| **Intersect…** | — | what the Blank and every Tool have in common |
| **Dissolve Boolean** | — | on a selected boolean: its operands become top-level objects again |
| **Edit Operands** | double-click, `Ctrl/Cmd+]` | enter a boolean to select and edit its operands; `Esc` or `Ctrl/Cmd+[` leaves |

Each of the first three opens the **Boolean panel** — the "dialog" the owner asked for, built as a popover
at the view's top-right exactly as *Array…* is (`C3dEditorView.axaml`, the `ArrayOpen` border; brief 46
R-em3d46-4b): no gesture is in progress, the camera stays free, and the preview is live. It holds the
operation, **one row per selected object** with a **Blank** radio, **Swap** (two objects), **Keep tools**, a
line saying what the result keeps, the refusal text when there is one, and **OK** / **Cancel**.

No key is proposed for the three operations: they are chosen far less often than Move or Rotate, and every
free letter is worth more to a later brief than to a menu item that is two clicks away.

A committed boolean is a **node in the object tree** with its Blank and Tools beneath it, and the
**Properties inspector** shows its Operation, **Enabled**, *Keep tools* and its Blank.

---

## 1. `R-em3d66-1` — what may be an operand, and why

**`R-em3d66-1a` Legal operands are solids:** Box, Prism, Cylinder, Polyhedron, a **Boolean** (nesting), a
Fillet or Chamfer (brief 67), and a **Step** part (brief 68). Anything with a volume the kernel can build.

**`R-em3d66-1b` Refused operands**, each with its own sentence in the disabled item's tooltip — the
`Viewer3DMenuItem(…, Enabled: false, Tip: why)` pattern `TreeMenu` already uses for the air box:

| Selected | Why not | The sentence points to |
|---|---|---|
| **Sheet** | a boolean on a sheet is an imprint or a split, a different operation (overview §4) | — |
| **Polyline** | construction geometry; it is never in the problem | *Extrude* it first |
| **Wire** | its shape is regenerated from its points and its pads (brief 50); a cut wire would stop being a wire | — |
| **Instance** | its solids belong to its own cell and resolve through its own technology (brief 42 §1i); cutting them from the parent would edit another cell | *Push into* the cell, or *Flatten* it (brief 48) |
| a face or vertex selection | booleans act on whole objects | *switch to Object mode (O)* — the sentence `Operations.cs` already uses |

With fewer than two legal solids selected, the three items are disabled with *"Select two or more solids:
the first one you select is a Tool."*. An object with **no material** is a legal operand: as a Tool its
material is not used; as a Blank the result has no material and is warned about as any such object is
(em-3d.md §6.4).

**`R-em3d66-1c` Without the kernel** (brief 63's capability, overview §1d) every boolean item — the menu's
three, *Dissolve*, *Edit Operands*, the inspector's fields — is **shown disabled, never hidden**, with the
capability's own sentence and action as its tooltip. This editor never builds that sentence itself: it
reads it from the capability, so the tooltip, the open refusal (brief 64) and `check` say the same words.

## 2. `R-em3d66-2` — the Boolean panel

**`R-em3d66-2a` Rows, Tool and Blank (D4).** The rows are the selected solids **in selection order**
(`Viewer3DViewModel.SelectedObjects()`, which already keeps it). Each row: the kind's icon, the name, the
material, and a **Blank** radio — exactly one row is the Blank; every other row is a **Tool**. The default:
- the **first-selected** object is a Tool (owner);
- the **last-selected** object is the Blank;
- every object between them is a Tool.

With two objects that is simply *first = Tool, second = Blank*. **Swap** (shown for two rows only) exchanges
them; with more rows the radio is the control. The rule is written once, in a pure function of the
selection order, and the tooltip on the rows says it: *"The first object you select is a Tool; the last is
the Blank. Choose another Blank here."*

**`R-em3d66-2b` The operation** is a combo preset from the menu item that opened the panel; changing it
re-requests the preview. The panel's title is the operation's verb.

**`R-em3d66-2c` What the result keeps (D11)** — one line, rewritten on every change:
- Subtract: *"'lid' keeps its name and Gold; 'cavity' and 'via1' are removed from it."*
- Unite: *"The result is 'lid', Gold. 'pin' (Copper) becomes Gold."* — the replaced materials are named,
  because a silent material change is the error nobody sees;
- Intersect: *"The result is 'lid', Gold: what 'lid' and 'pin' share."*

**`R-em3d66-2d` Keep tools (D10)** — a checkbox, **off** by default, remembered for the editor's lifetime
(editor state, not document state, as the tree's grouping is). Checked, the Tools stay in the model as
solids of their own beside the result: subtract a dielectric slug from a lid and keep it as the fill — the
case em-3d.md §6.3a could not state before this series. The tooltip says exactly that.

**`R-em3d66-2e` The preview is asynchronous (overview §1j).** Every change — a row's role, the operation,
*Keep tools* — sends one preview request through brief 63's client, and:
- the view keeps rendering; the camera stays free;
- a newer request **supersedes** an older one: only the latest reply is drawn (counter: replies discarded);
- while a request is outstanding the panel shows a small busy mark and **OK is disabled**;
- the Blank and the Tools' committed batches draw in the **ghost** state — translucent and not pickable —
  and the preview result draws as a **transient batch**, uploaded **once** per reply, never per frame, and
  released when the panel closes. A Tool draws with the overlay's red tint so it reads as "what is taken
  away" in Subtract.

**`R-em3d66-2f` Refusals are the worker's words, made readable.** An empty result (*"'lid' and 'pin' share
nothing"*), a subtraction that removes everything, a kernel failure, a worker crash (brief 63: the worker
is restarted, the request refused) — each shows in red in the panel, where `ArrayError` shows, and **OK stays
disabled**. A result in several pieces (a lid cut in two) is **not** a refusal: the note says *"2 pieces"*
and the result is one object.

**`R-em3d66-2g` OK commits once.** One document change, **one undo entry** (*"Subtract cavity from lid"*),
through `C3dEdit`'s ordinary path, the same one `ChangeObjects` uses. The Boolean takes the **Blank's place**
in construction order; the Tools leave the top-level list. The reply the preview already received is handed
to brief 63's cache under the committed tree's hash, so **the commit makes zero further worker calls**
(gate 3). *Cancel* and **Esc** change nothing and release the preview batch. **Enter** is OK when OK is
enabled.

## 3. `R-em3d66-3` — the tree (D5)

**`R-em3d66-3a` A boolean is a node.** Its row shows the operation's icon (the Material icon set the
toolbar already uses: `VectorDifferenceBa`, `VectorUnion`, `VectorIntersection`), its name and its
material; beneath it, the **Blank** first, then the **Tools** in order, each labelled *Blank* / *Tool* in
the row's detail. A nested boolean is a node inside a node. Expansion is kept by `ExpansionKey`, as every
node's is.

**`R-em3d66-3b` Grouping.** Every object appears **once** in the tree:
- **By material:** the boolean's row is under **its Blank's material** — it is a solid of that material;
  its operands are its children and are **never** listed again in their own materials' groups;
- **By type:** booleans have a group of their own, *Booleans*, beside the primitives'.

`C3dEditorViewModel.TreeGrouping.cs` states that a group is found by its **role**, never its header; the
Booleans group gets a role in `C3dTreeGroupRole` for the same reason. The filter hides whole top-level rows
only; an operand is never filtered out of a visible boolean.

**`R-em3d66-3c` A row addresses a path, not an index.** `C3dTreeItem.ObjectIndex` is a top-level index,
and `ChangeObjects`, `DeleteObjects` and `DocumentIndex` all take one. An operand has no top-level index.
This brief adds an **object path** — the top-level index, then the operand's place (`Blank`, `Tools[i]`) at
each level — and routes the operations that act on a row through it. The top-level index stays the fast
path for everything that is not inside a boolean, so no existing call changes shape.

**`R-em3d66-3d` The tree's menu** on a boolean: *Edit Operands*, *Dissolve Boolean*, *Rename…*,
*Enabled* (a check item), *Hide*, *Delete*. On an operand: *Rename…*, *Material*, *Make Blank* (on a Tool),
*Remove from Boolean* (on a Tool, when more than one remains; it becomes a top-level object right after the
boolean), *Hide*. Every item is the same function the inspector and the canvas call (the rule `TreeMenu.cs`
states in its header).

## 4. `R-em3d66-4` — the Properties inspector

For a selected boolean, the inspector shows:
- **Operation** — a combo (Subtract, Unite, Intersect);
- **Enabled** — a toggle;
- **Keep tools** — a toggle;
- **Blank** — a combo of its operands' names;
- **Swap** — a button, when it has exactly two operands;
- the result's material (the Blank's, read-only here — change it on the Blank) and its piece count.

Each is a typed edit in the inspector's existing sense: **one undo entry**, committed on change. It is then
re-evaluated through the client. **An evaluation that fails is not rolled back:** the document keeps the
edit, and the node carries a **refusal** exactly as a wire whose end is on no pad does (`C3dTreeItem.Refusal`,
brief 50 R-em3d50-4) — flagged in the tree, its sentence in Properties, and a run refused naming it. Undo is
one keystroke away, and a user changing Intersect to Subtract should not have the combo snap back under
their cursor.

**`R-em3d66-4a` Enabled (overview §1f).** Unticked, the boolean elaborates **its Blank and its Tools as
ordinary independent objects**, the Blank at the boolean's place and the Tools right after it, in order —
drawn, pickable and editable, and each in the solved problem. The node stays in the tree, its icon dimmed.
Toggling it back re-evaluates from the cache (zero worker calls when nothing else changed). **Disabled
elaborates exactly what Dissolve would write** (gate 5) — that is the definition, and it is what makes the
toggle an honest A/B experiment.

## 5. `R-em3d66-5` — reaching and editing an operand

**`R-em3d66-5a` Selecting.** In the tree, click the operand. In the view, **double-click the result** (or
*Edit Operands*, or **Ctrl/Cmd+]** with the boolean selected) to **enter** it: the result draws as a ghost,
its operands draw solid and pickable, and a breadcrumb on the status line says *"Editing operands of 'lid' —
Esc to leave"*. **Esc** or **Ctrl/Cmd+[** leaves.

*Why this gesture:* Alt is already taken twice in the pane — Alt+left pans (`Viewer3DPane`), and holding Alt
suspends geometry snapping (brief 44) — so an Alt+click pick would collide with both. Entering a thing to
edit its parts is already this editor's idiom: push into an instance (brief 48, `Ctrl/Cmd+]` / `Ctrl/Cmd+[`).
And a plain double-click in Object mode, with no tool armed, does nothing today. Entering a boolean is
**editor state**, not a document frame: it does not appear in the push-in frame stack and there is nothing
to save on leaving it.

**`R-em3d66-5b` Editing.** An operand is an ordinary object: Move, Rotate, Mirror, Duplicate (a copy of an
operand is a new top-level object), material, name, placement, **and face and vertex edits** (brief 47) all
apply. A drag is a preview: the operand's batch moves under its per-batch transform, the ghosted result
stays where it is, and **the worker is called zero times until release** (brief 46 R-em3d46-1a's counter,
overview §1h). On release the document changes once, **one** undo entry is written, and the boolean is
re-evaluated **once**.

**`R-em3d66-5c` Faces of a result** are selectable in Face mode for everything that **reads** a face: a
port, a face boundary (brief 49), Measure, snapping, *Drawing Plane from Face*. *Move Along Normal*, *Move*,
*Extrude to New Solid*, *Align to Face* and Vertex mode's *Move* on a result are **refused (D13)**, each with
*"'lid' is made by a boolean: edit its operands (double-click it) or the operation in Properties."* — the
menu item disabled with that tooltip, and the key refused on the status line with the same words.

**`R-em3d66-5d` Names survive (overview §1g; brief 64 owns the rule).** The gate this brief holds is the
user-visible one: **a port or a face boundary on the Blank's face before the boolean is on the same face of
the result after it**, and a wire landing on the Blank still lands. Brief 64 decides the spelling; this
brief's gate 6 fails if applying a boolean silently drops or moves a reference, and a reference whose face
no longer exists is a refusal naming it, never a guess.

## 6. `R-em3d66-6` — Dissolve, Delete, nesting, undo

- **Dissolve Boolean** writes the Blank at the boolean's place and the Tools after it — one undo entry,
  zero worker calls (the operands are managed objects or already cached).
- **Delete** on a boolean deletes it and its operands (the tree's row is one thing). **Delete** on an
  operand inside an entered boolean is *Remove from Boolean* followed by a delete of the removed object;
  deleting the Blank is refused: *"A boolean needs its Blank: choose another Blank first, or Dissolve."*
- **Nesting:** a boolean may be an operand (§1a), and the panel treats it as any other row. There is no
  depth limit in the UI; the tree indents.
- **Undo/redo** restore the document and re-adopt the cached result: **zero worker calls** (gate 3).
- **Save** writes what brief 64 defines; nothing in this brief adds a field.

## 7. `R-em3d66-7` — the `3D` menu

A **Boolean** submenu under *3D*, in **both** copies the menu has — the macOS `NativeMenu` and the in-window
`Menu`, which `WorkspaceWindow.axaml` hand-mirrors (brief 43 §1m) — with Subtract…, Unite…, Intersect…,
Dissolve Boolean and Edit Operands, each dispatched by name through the existing `ThreeDModifyCommand` the
way *Extrude to New Solid* is. Every item's tooltip ends *"Requires an active 3D editor."* like its
neighbours', and the kernel sentence when that is why it is disabled.

---

## 8. Gate

View-model tests in `tests/Ui.Tests`, headless. Every test that needs the worker is a `KernelFact` (brief 63)
and **skips with a reason** without it; the rest run everywhere.

1. **Legality (no kernel needed).** For each row of §1b's table, the three menu items are disabled and the
   tooltip is that row's sentence. Two boxes: enabled. Without the kernel: disabled with the capability's
   sentence, read from the capability (asserted equal to it, not to a literal).
2. **Tool and Blank (no kernel needed).** Selection orders of 2, 3 and 4 objects give §2a's defaults; Swap
   exchanges two; choosing a Blank by the radio leaves exactly one Blank.
3. **Counters.** Open the panel, change the operation twice quickly: ≥ 1 reply discarded, one drawn. OK:
   **zero** worker calls at commit. Undo, redo: zero. A preview reply uploads **one** transient batch.
4. **One undo entry** per commit, per inspector edit, per Dissolve, and per operand drag.
5. **Disabled equals Dissolved.** A boolean with Enabled off elaborates to an `Em3dProblem` **equal** to the
   one its dissolved document elaborates to (`KernelFact`, since the enabled form needs the worker to exist
   for comparison; the disabled form alone needs none — test that too, with the kernel absent).
6. **References survive.** A port and a face boundary on the Blank's `zmax`, and a wire landing on the
   Blank: after Subtract, all three resolve to the result's corresponding face and no note says otherwise.
7. **Drag counter.** Entering a boolean and dragging an operand across 50 mouse moves makes **zero** worker
   calls; the release makes **one**.
8. **D13 refusals.** Each face and vertex operation on a result is refused with the §5c sentence; the same
   operations on an entered operand run.
9. **Refusal is not rollback.** Change a working Subtract to an Intersect of disjoint solids in the
   inspector: the document keeps it, the node carries a refusal, one undo restores the working state.
10. **Tree.** By material, a boolean appears under its Blank's material and its operands under it only;
    By type, under *Booleans*. Filtering a material never removes an operand from a visible boolean.

## 9. Owner check (pixels not seen)

In the **Debug** build:
- draw a lid and a cylinder through it; select the cylinder, then the lid; right-click ▸ *Boolean ▸
  Subtract…*; confirm the cylinder is the Tool and the lid the Blank; see the preview; OK;
- open the panel again on three objects and choose a different Blank; try Swap on two;
- untick **Enabled** on the boolean in Properties; see both solids back; tick it again;
- double-click the result, drag the cylinder, see the hole follow on release; Esc out;
- try *Move Along Normal* on the result's top face and read the refusal;
- Unite two solids of different materials and read the line that names the change;
- in a build without the worker, confirm every boolean item is disabled and its tooltip says why.

"Does the preview arrive before you have finished reading the panel" is the question. So is whether
*first-selected = Tool* feels natural after ten booleans, or whether the owner wants it the other way.

## 10. Scope

- No sheet booleans, imprint or split (overview §4).
- No instances as operands; *Flatten* first (brief 48).
- No direct face or vertex edits on a result (D13).
- No key for the three operations (§0).
- No new document field; brief 64 owns the format and the names.
- No timing test. Counters only.
