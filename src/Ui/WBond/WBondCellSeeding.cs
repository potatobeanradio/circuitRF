using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Schematic;
using CircuitRF.WBond;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// wbond.md §9.5/WB41 — <b>Update Layout from Schematic writes the cell's <c>.wBond</c>.</b>
///
/// <h3>The reported bug</h3>
/// <para>Owner, 2026-08-17: <i>"Update Layout from Schematic with a wBond component will create a
/// layout, but no wBond component. User expects to see wires."</i> A wBond has no layout VIEW to place
/// as an instance — WB23 is explicit that no wire ever enters a <c>.clay</c> — so the generator
/// resolved nothing for it and logged "no layout view — skipped", which is a true statement about a
/// mechanism the user has no reason to know about.</para>
///
/// <h3>Where the wires go instead</h3>
/// <para>Beside the <c>.clay</c>, as the cell's own <c>.wBond</c> (WB40) — the SAME sidecar
/// <see cref="WBondCell"/> already loads for any cell that has one, which is why this one change also
/// answers the second half of the report ("if the user opens a <c>.clay</c> for a cell that has a wBond
/// component, will the wires be shown?" — yes, from the moment the sidecar exists).</para>
///
/// <code>
/// &lt;workspace&gt;/&lt;cell&gt;/
/// ├── layout/&lt;cell&gt;.clay       artwork — pads, traces, die outline
/// ├── layout/&lt;cell&gt;.wBond      the wires        ← written here (WB40, revised 2026-08-17)
/// └── schematic/&lt;cell&gt;.csch    unchanged
/// </code>
///
/// <para>Stem-paired with the <c>.clay</c> rather than sitting at the cell root: the wires are an
/// ATTACHMENT to one layout (<c>workspace-and-project-tree.md</c> §1.2.1), and a cell may hold more than
/// one <c>.clay</c>. See <see cref="WBondCell"/> for the resolution order and the legacy branch.</para>
///
/// <h3>A re-run never overwrites wires the user has moved</h3>
/// <para>That is the whole point of §9.5's layout-driven flow, and the reason WB41 refuses to make this
/// a PCell: a generator would regenerate over the user's edits on the next run. So the sidecar is
/// created ONCE and thereafter left alone; when the schematic's array list has since diverged from it,
/// the divergence is <b>reported</b> and the remedy named (§9.6's "Update Schematic from wBond Layout"),
/// which is WB42's stance exactly — the layout is the editing source of truth, the payload is the
/// simulation one, and drift is a normal recoverable state made visible rather than repaired.</para>
/// </summary>
public static class WBondCellSeeding
{
    /// <summary>What happened, in one value the caller reports on.</summary>
    public enum Outcome
    {
        /// <summary>The schematic holds no wBond component. Nothing to do, nothing to say.</summary>
        NoWBond,

        /// <summary>The sidecar did not exist and was written from the component's payload.</summary>
        Created,

        /// <summary>It already existed and was left exactly as it was.</summary>
        KeptExisting,

        /// <summary>
        /// It already existed and gained an array the schematic had since added — <b>without any wire
        /// already in it being regenerated, re-pointed or moved</b>. Distinct from
        /// <see cref="Created"/> because the WB45a flip to <c>Linked</c> belongs on a first write only:
        /// a merge changes what is drawn, never which of the two sources the next Run reads.
        /// </summary>
        Merged,

        /// <summary>The component's <c>Design</c> payload could not be read; nothing was written.</summary>
        Unreadable,

        /// <summary>The file could not be written (permissions, a read-only location).</summary>
        WriteFailed,
    }

    /// <param name="Path">The sidecar's absolute path, or null when nothing was looked at.</param>
    /// <param name="Messages">
    /// Lines to report, most important first — empty for the ordinary "nothing to say" cases. Warnings
    /// and information are not distinguished here: the caller already has a message sink with both, and
    /// which of these is which follows from <see cref="Outcome"/>.
    /// </param>
    public readonly record struct Result(Outcome Outcome, string? Path, IReadOnlyList<string> Messages)
    {
        public static Result None => new(Outcome.NoWBond, null, []);

        /// <summary>True when the sidecar is on disk after this call — created now or already there.</summary>
        public bool HasSidecar => Outcome is Outcome.Created or Outcome.KeptExisting or Outcome.Merged;

        /// <summary>
        /// True when the merge mutated the <b>live</b> wire design an open layout editor is holding,
        /// rather than the file. The caller must then tell that editor its geometry moved — nothing
        /// here can, and a design changed underneath an editor that does not know is both invisible on
        /// screen and lost on the next save.
        /// </summary>
        public bool LiveDesignChanged { get; init; }
    }

    /// <summary>
    /// Writes <c>&lt;cellDir&gt;/&lt;cellName&gt;.wBond</c> from the schematic's wBond component, unless
    /// it is already there.
    /// </summary>
    /// <param name="cellName">
    /// The cell's own name — which is the schematic file's, and therefore the stem of the <c>.clay</c>
    /// this command writes (<c>layout/&lt;cellName&gt;.clay</c>). That identity is what makes the wires
    /// land stem-paired with their artwork, which is how <see cref="WBondCell.Resolve"/> finds them.
    /// </param>
    /// <param name="only">
    /// Seed from THIS component alone, ignoring every other wBond in the schematic — the wBond parameter
    /// dialog's own <b>Update Layout</b> button (owner, 2026-08-17), which updates the component the user
    /// is editing and nothing else. Null takes the schematic's first wBond, which is the whole-schematic
    /// command's behaviour.
    /// </param>
    /// <param name="liveDesign">
    /// The wire design a layout editor currently has OPEN for this cell, when there is one
    /// (<c>LayoutEditorViewModel.WireDesign</c>).
    ///
    /// <para><b>Supplying it is not an optimisation — it is what keeps the merge correct.</b> An open
    /// editor holds its own design object and mutates it in place, so merging through the file would
    /// change nothing on screen AND be overwritten by that editor's next save. When it is here, it is
    /// the authority and the file is left for the editor to write.</para>
    /// </param>
    public static Result Seed(SchematicEditModel model, string cellDir, string cellName,
                              EditableComponent? only = null, WBondDesign? liveDesign = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        var wBonds = model.Components
            .Where(c => c.Symbol == SymbolKind.WBond)
            .ToList();

        if (wBonds.Count == 0) return Result.None;

        var messages = new List<string>();

        // A named component that is no longer in the schematic (deleted while the dialog was open) is a
        // refusal rather than a silent fall-back to a DIFFERENT wBond's wires.
        if (only is not null && !wBonds.Contains(only))
            return new Result(Outcome.NoWBond, null,
                [$"wBond '{only.InstanceName}' is no longer in this schematic — nothing was written."]);

        var comp = only ?? wBonds[0];

        // §7/WB28: more than one wBond per cell view is allowed by the data model and discouraged by
        // convention, and there is no single answer here — merging two components' arrays into one file
        // would break each one's array-to-pin mapping. So one is seeded and the rest are NAMED, rather
        // than silently dropped: what is missing from the layout has to be sayable. Reported for the
        // targeted call too, and for the same reason — the user asked for one component, and the file
        // they get holds only that one.
        if (wBonds.Count > 1)
            messages.Add(
                $"This schematic holds {wBonds.Count} wBond components. Only '{comp.InstanceName}' was " +
                $"written to the cell's wirebond file; " +
                string.Join(", ", wBonds.Where(c => !ReferenceEquals(c, comp)).Select(c => $"'{c.InstanceName}'")) +
                " has no wires in the layout. One wBond per cell view is the convention (wbond.md §7).");

        // WB40 (revised 2026-08-17): the wires are an ATTACHMENT to the .clay, so they live in layout/
        // sharing that .clay's stem — not at the cell root. Schematic → Layout emits layout/<cell>.clay,
        // so the stem is the cell name.
        string layoutDir = Path.Combine(cellDir, CellFolder.LayoutSubFolder);
        string path      = Path.Combine(layoutDir, cellName + ".wBond");

        // A pre-2026-08-17 workspace keeps its wires at the cell root. Seeding a fresh file into layout/
        // would SHADOW them (attachment resolution prefers the stem-paired one), so the user's edited
        // wires would silently stop being the ones drawn and simulated. Keep theirs, name the move.
        string? legacy = File.Exists(path) ? null : WBondCell.LegacyRootPath(layoutDir);

        if (File.Exists(path) || legacy is not null)
        {
            string existing = legacy ?? path;

            if (legacy is not null)
                messages.Add(
                    $"This cell's wires are still at the cell root ('{Path.GetFileName(legacy)}'). They are " +
                    $"being used as they are; move the file to 'layout/{cellName}.wBond' so it stays attached " +
                    "to its artwork if this cell ever gains a second layout.");

            // An OPEN editor is the authority over its own wires — see MergeIntoLive for the owner
            // report that says why this branch is not optional.
            return liveDesign is not null
                ? MergeIntoLive(comp, liveDesign, existing, messages)
                : MergeIntoExisting(comp, existing, messages);
        }

        string? payload = comp.Parameters.FirstOrDefault(p => p.Name == WBondEmbedding.DesignParameter)?.Expression;
        if (!WBondEmbedding.TryDecode(payload, out var design) || design is null)
        {
            messages.Add(
                $"wBond '{comp.InstanceName}': its embedded design could not be read, so no wirebond " +
                "file was written for this cell. Open the component's parameters to repair it.");
            return new Result(Outcome.Unreadable, path, messages);
        }

        // The controlling parameters (§5.5.1/WB44) are applied BEFORE the file is written — Update
        // Layout from Schematic writes what the schematic asks for. See ApplyControllingParameters
        // below for the owner report this exists for, and for why applying them twice is harmless.
        try
        {
            messages.AddRange(ApplyControllingParameters(comp, design));
            messages.AddRange(ApplyOvermold(comp, design));
        }
        catch (InvalidOperationException ex)
        {
            // A refused value — a non-positive length, a metal the design does not declare. Refusing
            // to write is right: the alternative is a layout quietly holding geometry the schematic
            // has already been told is wrong, which the next Run would then also refuse.
            messages.Add(
                $"wBond '{comp.InstanceName}': {ex.Message} No wirebond file was written for this cell.");
            return new Result(Outcome.Unreadable, path, messages);
        }

        try
        {
            Directory.CreateDirectory(layoutDir);
            WBondIo.WriteFile(path, design);
        }
        catch (Exception ex)
        {
            messages.Add($"Could not write '{Path.GetFileName(path)}': {ex.Message}");
            return new Result(Outcome.WriteFailed, path, messages);
        }

        messages.Add(
            $"wBond '{comp.InstanceName}' → '{Path.GetFileName(path)}' " +
            $"({design.Arrays.Count} array(s), {design.WireCount} wire(s)). " +
            "Its wires now draw over this cell's layout and are edited there.");

        // WB45a — THIS is where a placed wBond becomes Linked, and it says so.
        //
        // The state must change only at a moment the user can see. A freshly placed wBond is Carried
        // by construction (there is no cell and no file to link to); the file comes into existence
        // here, and flipping here is what keeps "which wires simulate" tied to something that happened
        // on screen. It must NEVER flip as a side effect of a later scan noticing the file exists.
        //
        // Only on Created. A Carried instance whose cell already has a .wBond is a legitimate state —
        // someone who deliberately kept the portable payload — and is not auto-converted.
        string stored = WBondPlacement.LinkTo(comp, path, model.SchematicDirectory);
        model.NotifyChanged();

        // The GEOMETRY half of this sentence is what linking buys; the ARRAY half is what it does not,
        // and saying only the first is what produced the owner's second report (2026-08-17: "this
        // contradicts the 'nothing needs bringing back into the schematic' text"). A placed wBond's
        // PINS come from its carried payload, so an array added or removed in the layout still has to
        // be brought back or the symbol and the model disagree about the terminal count.
        messages.Add(
            $"wBond '{comp.InstanceName}' is now LINKED to '{stored}': the next Run simulates the wires " +
            "in the layout, so moving a wire or changing a loop height there needs nothing further. " +
            "ADDING OR REMOVING AN ARRAY still does — the symbol's pins come from the schematic's own " +
            "copy — so run Update Schematic from Layout after that. Set Source back to Carried in its " +
            "parameters if the schematic should travel on its own.");

        return new Result(Outcome.Created, path, messages);
    }

    /// <summary>
    /// Applies the instance's controlling parameters (<c>wbond.md</c> §5.5.1/WB44) to the design about
    /// to be written.
    ///
    /// <h3>The owner report this exists for (2026-08-17)</h3>
    /// <para><i>"I placed a wBond into the schematic, added 2 more arrays, changed their loop heights to
    /// 30, 20 and 15 mil. Then I did an Update Layout from Schematic, but all 3 arrays had a loop height
    /// of 20 mil."</i> 20 mil is the DRAWN default (<c>WBondEmbedding.DefaultWire.LoopHeightMils</c>) —
    /// this command wrote the raw payload, because the override layer had only ever been applied on the
    /// way to the solver. <b>Update Layout from Schematic writes what the schematic asks for.</b></para>
    ///
    /// <h3>Applying them here and again at the run is harmless, and that is not a coincidence</h3>
    /// <para>Every controlling parameter sets an ABSOLUTE value — a loop height, a diameter, a metal —
    /// never a delta or a factor. So applying one to geometry that already satisfies it is the identity,
    /// and a linked instance whose file was written by this command gets the same answer whether or not
    /// the parameters are still set on it. (This is the same property that made <c>Span</c> — which
    /// scales by factor, WB24c — the one of the six that had to be deferred.) The parameters are
    /// therefore NOT cleared off the instance afterwards: clearing them would be an edit-on-write, would
    /// break WB44 property 1, and would silently retire the handle a sweep turns.</para>
    ///
    /// <h3>An expression that is not a literal cannot be baked, and says so</h3>
    /// <para>A <c>VAR</c> reference is the whole point of these parameters being sweepable — and it is
    /// exactly why it has no single value to write into a file. There is no scope to resolve one against
    /// here either (this command runs on a schematic that need not elaborate at all). So the geometry is
    /// written as drawn and the parameter is NAMED, rather than a number being invented for it.</para>
    /// </summary>
    private static IReadOnlyList<string> ApplyControllingParameters(EditableComponent comp, WBondDesign design)
    {
        var read = WBondPlacement.ReadControllingParameters(comp);

        var messages = ControllingParameters.ApplyTo(design, read.Overrides)
            .Select(note => $"wBond '{comp.InstanceName}': {note}")
            .ToList();

        foreach (string named in read.Unbakeable)
            messages.Add(
                $"wBond '{comp.InstanceName}': {named} is an expression, not a number, so it has no " +
                "single value to draw. Those wires were written as drawn; the parameter still applies " +
                "at every Run.");

        return messages;
    }

    /// <summary>
    /// Carries the instance's <c>er</c> parameter — the plastic overmold — onto the design about to
    /// be written (wbond.md §3.7).
    ///
    /// <para><b>The same direction and the same rule as the controlling parameters above</b>: Update
    /// Layout from Schematic writes what the schematic asks for, so the component's own permittivity
    /// wins over whatever the layout's <c>.wBond</c> last recorded. Without this, setting ε_r on the
    /// schematic and then seeding the layout leaves two documents disagreeing about the medium, with
    /// the panel in the layout editor quoting one and the netlist the other.</para>
    ///
    /// <para><b>An EXPRESSION is left alone and named</b>, exactly as a <c>VAR</c>-valued loop height
    /// is: <c>er = moldEr</c> has no single value to write into a file, and it still applies at every
    /// Run. A value below 1 is refused rather than clamped, for the reason
    /// <c>WBondDesign.Validate</c> gives.</para>
    /// </summary>
    private static IReadOnlyList<string> ApplyOvermold(EditableComponent comp, WBondDesign design)
    {
        string? text = comp.Parameters.FirstOrDefault(p => p.Name == "er")?.Expression?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return [];

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double er)
            || !double.IsFinite(er))
            return [
                $"wBond '{comp.InstanceName}': er is an expression, not a number, so it has no single " +
                "value to write into the wirebond file. The wires were written with the file's own " +
                "permittivity; the parameter still applies at every Run."
            ];

        if (er < 1.0)
            throw new InvalidOperationException(
                $"er is {text}. The overmold relative permittivity must be at least 1 " +
                "(1 = air, no encapsulant).");

        if (design.OvermoldEr == er) return [];

        double was = design.OvermoldEr;
        design.OvermoldEr = er;

        return [
            $"wBond '{comp.InstanceName}': the overmold permittivity was set to " +
            $"{er.ToString("0.###", CultureInfo.InvariantCulture)} from the schematic " +
            $"(was {was.ToString("0.###", CultureInfo.InvariantCulture)})."
        ];
    }

    /// <summary>
    /// Brings a sidecar that is ALREADY there up to date with the schematic — <b>additively</b>.
    ///
    /// <h3>The reported bug (owner, 2026-08-17)</h3>
    /// <para><i>"If I do an Update Layout from Schematic, then go back to the schematic Component
    /// Parameters and add another array, then do another Update Layout from Schematic, the new array
    /// that I created in schematic does not show up in the layout."</i></para>
    ///
    /// <para>The sidecar was created once and thereafter left <b>entirely</b> alone. WB41's rule — "a
    /// re-run never overwrites wires the user has moved" — is what that was protecting, and it is right
    /// about EXISTING arrays and wrong about a NEW one. <b>Adding an array touches no wire that is
    /// already there</b>, so refusing to add it protects nothing and silently drops the thing the
    /// command was just asked to do.</para>
    /// </summary>
    private static Result MergeIntoExisting(EditableComponent comp, string path, List<string> messages)
    {
        string name = Path.GetFileName(path);

        WBondDesign? onDisk = null;
        try { onDisk = WBondIo.ReadFile(path); } catch { /* reported just below */ }

        if (onDisk is null)
        {
            messages.Add(
                $"'{name}' already exists but could not be read — it was left untouched. " +
                "The layout will show no wires until it is repaired or removed.");
            return new Result(Outcome.KeptExisting, path, messages);
        }

        var status = MergeInto(onDisk, comp, name, messages);

        if (status is MergeStatus.Refused) return new Result(Outcome.KeptExisting, path, messages);
        if (status is MergeStatus.Unchanged) return new Result(Outcome.KeptExisting, path, messages);

        try
        {
            WBondIo.WriteFile(path, onDisk);
        }
        catch (Exception ex)
        {
            messages.Add($"Could not update '{name}': {ex.Message}");
            return new Result(Outcome.WriteFailed, path, messages);
        }

        return new Result(Outcome.Merged, path, messages);
    }

    /// <summary>
    /// The same merge, into the wire design a LAYOUT EDITOR currently has open — <b>never through the
    /// file</b>.
    ///
    /// <h3>Why this exists, and it is the second half of the same owner report</h3>
    /// <para>The merge above wrote the corrected file and the user still saw no new array (2026-08-17,
    /// with the workspace attached — the <c>.wBond</c> on disk held G1 <i>and</i> G2 while the layout
    /// showed only G1). An open <c>LayoutEditorViewModel</c> holds its own <see cref="WBondDesign"/>
    /// object and mutates it in place; writing the file underneath it changed nothing on screen.</para>
    ///
    /// <para><b>And it was worse than a stale view.</b> The live design still held G1 alone, and the
    /// layout's own save path writes that object back — so the next save of the layout would have
    /// silently deleted the array the merge had just added. Reading and writing the file behind a live
    /// editor is not a display bug, it is a lost-edit bug.</para>
    ///
    /// <para>So when the editor is live it is the authority: the merge mutates ITS design and the file
    /// is left alone, because that editor owns writing it. Which is the ordinary contract for every
    /// other edit made to an open document — dirty until saved.</para>
    /// </summary>
    private static Result MergeIntoLive(
        EditableComponent comp, WBondDesign live, string path, List<string> messages)
    {
        var status = MergeInto(live, comp, Path.GetFileName(path), messages);

        return status is MergeStatus.Changed
            ? new Result(Outcome.Merged, path, messages) { LiveDesignChanged = true }
            : new Result(Outcome.KeptExisting, path, messages);
    }

    private enum MergeStatus { Unchanged, Changed, Refused }

    /// <summary>
    /// Adds the arrays <paramref name="comp"/> declares and <paramref name="target"/> lacks, and
    /// realigns the array ORDER to the schematic's — touching no wire that is already there.
    ///
    /// <h3>What is deliberately NOT done, each a refusal rather than an omission</h3>
    /// <list type="bullet">
    ///   <item><b>An existing array's geometry is never rewritten.</b> That is WB41 exactly: those
    ///     wires may have been dragged onto real pads, and a schematic-side loop-height change is
    ///     already carried to the SOLVER as an override (§5.5.1/WB44) without needing to overwrite the
    ///     drawing. Re-baking it here would undo layout work to change a number that has already taken
    ///     effect.</item>
    ///   <item><b>An array the schematic no longer has is kept, not deleted.</b> Deleting is the one
    ///     direction that destroys drawn work irrecoverably, and the array may have been removed from
    ///     the component by accident. It is REPORTED, and the remedy named is the one that matches this
    ///     direction — which the old message got backwards, telling a user who had just added an array
    ///     on the schematic to pull FROM the layout, i.e. to throw that array away.</item>
    /// </list>
    /// </summary>
    private static MergeStatus MergeInto(
        WBondDesign target, EditableComponent comp, string name, List<string> messages)
    {
        string? payload = comp.Parameters.FirstOrDefault(p => p.Name == WBondEmbedding.DesignParameter)?.Expression;
        if (!WBondEmbedding.TryDecode(payload, out var wanted) || wanted is null)
        {
            messages.Add(
                $"wBond '{comp.InstanceName}': its embedded design could not be read, so '{name}' was " +
                "left exactly as it is. Open the component's parameters to repair it.");
            return MergeStatus.Refused;
        }

        var byName = target.Arrays.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

        var added = wanted.Arrays.Where(a => !byName.ContainsKey(a.Name)).ToList();
        var orphaned = target.Arrays
            .Where(a => !wanted.Arrays.Any(w => w.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Array order IS pin order, and under `Linked` the model's terminals come from the FILE while
        // the symbol's pins come from the component's payload — so leaving the two in different orders
        // wires every array to the wrong branch. Reordering moves no wire in space; it only realigns
        // the two lists, which is what makes it safe to do unasked.
        var merged = wanted.Arrays
            .Select(w => byName.TryGetValue(w.Name, out var existing) ? existing : w)
            .Concat(orphaned)      // kept, and reported below — never silently dropped
            .ToList();

        bool reordered = !merged.Select(a => a.Name).SequenceEqual(
            target.Arrays.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);

        target.Arrays.Clear();
        target.Arrays.AddRange(merged);

        // ── The controlling parameters reach the arrays that were ALREADY drawn ──
        //
        // Owner, 2026-08-17: "I changed the G1 loop height to 10 mil in schematic, then did an Update
        // Layout from Schematic, but the loop height still looks like it's 20 mil." This used to be a
        // deliberate refusal — WB41's "a re-run never overwrites wires the user has moved" — and it was
        // too coarse in the same way the never-write-the-file-again rule was. Two things settle it:
        // the wBond editor's OWN "set this array's loop height" command does exactly this, so there is
        // no new destruction here that the editor would not also do; and since these are applied
        // path-preservingly (WireEdits.SetLoopHeightPreservingPath), every X and Y the user authored
        // survives, which is what WB41 was actually defending.
        //
        // Applied AFTER the merge, so one pass covers both the arrays that were already there and the
        // ones just added.
        var before = WireGeometry(target);
        double erBefore = target.OvermoldEr;

        try
        {
            messages.AddRange(ApplyControllingParameters(comp, target));
            messages.AddRange(ApplyOvermold(comp, target));
        }
        catch (InvalidOperationException ex)
        {
            messages.Add($"wBond '{comp.InstanceName}': {ex.Message} '{name}' was left exactly as it is.");
            return MergeStatus.Refused;
        }

        int reshaped = CountChanged(before, WireGeometry(target));

        // The permittivity moves NO wire, so it is not in `reshaped` and has to be counted here or the
        // "nothing changed" shortcut below would drop it on the floor — the design would be updated in
        // memory and never written, which is the silent half of the failure.
        bool remolded = target.OvermoldEr != erBefore;

        if (added.Count == 0 && !reordered && reshaped == 0 && !remolded)
        {
            // Agreed, kept, and nothing worth saying about it. "The wires are already in the layout"
            // is not news to someone who just ran this on a cell they have been editing (owner,
            // 2026-08-17), and reporting it trains people to skim the pane.
            if (orphaned.Count > 0) messages.Add(DescribeOrphaned(comp, orphaned, name));
            return MergeStatus.Unchanged;
        }

        if (reshaped > 0)
            messages.Add(
                $"wBond '{comp.InstanceName}': the component's loop height, diameter and material " +
                $"settings were applied to {Plural(reshaped, "wire")} in '{name}'. Every wire's route " +
                "and both its feet are unchanged — only the quantities the schematic sets moved.");

        if (added.Count > 0)
            messages.Add(
                $"wBond '{comp.InstanceName}': {Plural(added.Count, "new array")} " +
                $"({string.Join(", ", added.Select(a => $"'{a.Name}'"))}) added to '{name}'. " +
                "The wires already in the layout were left exactly where they are.");

        if (reordered && added.Count == 0)
            messages.Add(
                $"wBond '{comp.InstanceName}': the arrays in '{name}' were re-ordered to match the " +
                "schematic, so its pins line up with the component's. No wire moved.");

        if (orphaned.Count > 0) messages.Add(DescribeOrphaned(comp, orphaned, name));

        return MergeStatus.Changed;
    }

    /// <summary>
    /// Every wire's geometry and metal, flattened — the before/after snapshot that decides whether the
    /// controlling parameters actually moved anything.
    ///
    /// <para><b>Compared rather than assumed</b>, because a re-run with an override already satisfied
    /// must NOT mark the layout dirty: that would make Update Layout from Schematic leave an unsaved
    /// document behind every single time it was run.</para>
    /// </summary>
    private static List<(Point3[] Points, long Diameter, string Material)> WireGeometry(WBondDesign design)
        => [.. design.AllWires().Select(
               w => (Points: w.Points.ToArray(), Diameter: w.DiameterNm, Material: w.Material))];

    private static int CountChanged(
        List<(Point3[] Points, long Diameter, string Material)> before,
        List<(Point3[] Points, long Diameter, string Material)> after)
    {
        int changed = 0;
        for (int i = 0; i < Math.Min(before.Count, after.Count); i++)
        {
            if (before[i].Diameter != after[i].Diameter
                || !string.Equals(before[i].Material, after[i].Material, StringComparison.Ordinal)
                || !before[i].Points.SequenceEqual(after[i].Points))
                changed++;
        }
        return changed;
    }

    /// <summary>
    /// An array drawn in the layout that the schematic component no longer declares.
    ///
    /// <para><b>The remedy named here is the one that matches THIS direction.</b> The message this
    /// replaces said "use Update Schematic from Layout, or delete the file to re-seed it" — advice that
    /// told a user who had just added an array on the schematic to pull the layout back over it, i.e.
    /// to throw away the array they had come here to add.</para>
    /// </summary>
    private static string DescribeOrphaned(EditableComponent comp, List<WireArray> orphaned, string name)
        => $"wBond '{comp.InstanceName}': {Plural(orphaned.Count, "array")} " +
           $"({string.Join(", ", orphaned.Select(a => $"'{a.Name}'"))}) " +
           $"{(orphaned.Count == 1 ? "is" : "are")} drawn in '{name}' but no longer declared on the " +
           "component, so its pins are not on the symbol. The wires were kept: add the array back in " +
           "the component's parameters, or delete those wires in the layout.";

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
