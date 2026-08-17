using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// ├── layout/&lt;cell&gt;.clay      artwork — pads, traces, die outline
/// ├── &lt;cell&gt;.wBond            the wires        ← written here
/// └── schematic/&lt;cell&gt;.csch    unchanged
/// </code>
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
        public bool HasSidecar => Outcome is Outcome.Created or Outcome.KeptExisting;
    }

    /// <summary>
    /// Writes <c>&lt;cellDir&gt;/&lt;cellName&gt;.wBond</c> from the schematic's wBond component, unless
    /// it is already there.
    /// </summary>
    /// <param name="cellName">
    /// The cell's own name — which is the schematic file's, so the sidecar is named the way
    /// <see cref="WBondCell.FindFor"/> prefers to find it.
    /// </param>
    /// <param name="only">
    /// Seed from THIS component alone, ignoring every other wBond in the schematic — the wBond parameter
    /// dialog's own <b>Update Layout</b> button (owner, 2026-08-17), which updates the component the user
    /// is editing and nothing else. Null takes the schematic's first wBond, which is the whole-schematic
    /// command's behaviour.
    /// </param>
    public static Result Seed(SchematicEditModel model, string cellDir, string cellName,
                              EditableComponent? only = null)
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

        string path = Path.Combine(cellDir, cellName + ".wBond");

        if (File.Exists(path))
        {
            if (DescribeExisting(comp, path) is { } note) messages.Add(note);
            return new Result(Outcome.KeptExisting, path, messages);
        }

        string? payload = comp.Parameters.FirstOrDefault(p => p.Name == WBondEmbedding.DesignParameter)?.Expression;
        if (!WBondEmbedding.TryDecode(payload, out var design) || design is null)
        {
            messages.Add(
                $"wBond '{comp.InstanceName}': its embedded design could not be read, so no wirebond " +
                "file was written for this cell. Open the component's parameters to repair it.");
            return new Result(Outcome.Unreadable, path, messages);
        }

        try
        {
            Directory.CreateDirectory(cellDir);
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

        return new Result(Outcome.Created, path, messages);
    }

    /// <summary>
    /// What to say about a sidecar that was already there, or <b>null to say nothing</b>.
    ///
    /// <para><b>Null is the ordinary case and the point of this method.</b> "The wires are already in the
    /// layout, and were kept" is not news to someone who just ran Update Layout on a cell they have been
    /// editing — it is the expected outcome stated as a warning (owner, 2026-08-17: <i>"I already know
    /// that the wires are in the layout. I am updating it, so why would the system give me this
    /// warning?"</i>). Reporting it trains people to skim this pane, which costs the messages that DO
    /// matter.</para>
    ///
    /// <para>Two of those remain, and both are things the user cannot see for themselves. The comparison
    /// is the ARRAY LIST, reusing <see cref="WBondPlacement.DriftBetween"/>, because array order IS pin
    /// order (§9.2/WB35a) — differing wire GEOMETRY is the normal state of layout-driven design and was
    /// never reported.</para>
    /// </summary>
    private static string? DescribeExisting(EditableComponent comp, string path)
    {
        string name = Path.GetFileName(path);

        WBondDesign? onDisk = null;
        try { onDisk = WBondIo.ReadFile(path); } catch { /* reported below as unreadable */ }

        if (onDisk is null)
            return $"'{name}' already exists but could not be read — it was left untouched. " +
                   "The layout will show no wires until it is repaired or removed.";

        if (WBondPlacement.DriftBetween(comp, onDisk, name) is { } drift)
            return drift.Message +
                   " The layout's wires were kept: use Design ▸ Update Schematic from Layout to bring " +
                   "them back into the component, or delete the file to re-seed it from the schematic.";

        return null;   // agreed, kept, and nothing worth saying about it
    }
}
