using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Matching;

/// <summary>
/// The Designer's network pane <b>as editable schematic objects</b> — what its <c>Copy</c> puts on the
/// clipboard.
/// </summary>
/// <remarks>
/// <b>Owner, 2026-08-20:</b> <i>"add a context menu to the schematic with a menu 'Copy'. This puts the
/// schematic on the clipboard that can be pasted into a real circuitRF schematic or into PowerPoint as
/// EMF. (Reuse the Schematic copy/export code we have already developed…)"</i>
///
/// <para><b>Nothing here renders, serialises or touches the clipboard.</b> All of that is
/// <c>SchematicClipboard.CopyAsync</c>'s — the same call the schematic editor's own Copy makes, so the
/// Designer gets the JSON round-trip, the SVG, the PNG and Windows' CF_ENHMETAFILE for free and cannot
/// drift from the editor's clipboard behaviour. The one thing that call cannot supply is the
/// SELECTION, because this pane has none: it is a projection of a ladder, with no
/// <c>EditableSchematic</c> behind it. That is what this file makes.</para>
///
/// <para><b>It is the drawing on screen, not the flattened cell.</b> <c>MatchFlatten</c> writes a
/// different and deliberately different circuit — interface pins, the terminations parked in annexes
/// and disabled, a design annotation — because its job is a cell that simulates. Copy's job is the
/// picture the user is looking at, which is why the two are separate and why this one places every
/// component at the coordinates <see cref="MatchSchematicModel"/> drew it at.</para>
/// </remarks>
public static class MatchSchematicCopy
{
    /// <summary>
    /// The termination instance names the COPY uses.
    /// </summary>
    /// <remarks>
    /// <c>T1</c> / <c>T2</c>, not the pane's "Termination 1" — the same names <c>MatchFlatten</c>
    /// writes. The pane's spelling is a caption; an instance name with a space in it is a name a
    /// netlist reader has to survive, and the whole point of this menu item is that what lands in a
    /// real schematic is a real schematic.
    /// </remarks>
    public static string TerminationName(int end) => "T" + end.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Projects one ladder layout onto editable objects, ready to be copied.</summary>
    /// <returns>An empty model when there is no design — the caller refuses rather than copying nothing.</returns>
    public static SchematicEditModel Build(MatchLadderLayout? layout)
    {
        var model = new SchematicEditModel { GridSize = 100.0 };
        if (layout is null || layout.Elements.Count == 0) return model;

        foreach (var e in layout.Elements)
        {
            model.Components.Add(Element(e));
            // One ground per COLUMN, under its lowest shunt symbol — the pane's own rule. A blocked
            // arm is an inductor above its block capacitor on one vertical, so a ground per shunt
            // element at the shared ShuntGroundY put two grounds on the inductor's lower lead and
            // none on the capacitor (owner-reported, 2026-08-28).
            if (MatchLadderLayout.GroundYFor(layout, e) is { } gy) model.Components.Add(Ground(e.X, gy));
        }

        foreach (var t in layout.Terminations)
            model.Components.Add(Termination(t));

        foreach (var w in SpineSegments(layout))
            model.Wires.Add(w);

        return model;
    }

    private static EditableComponent Element(MatchLadderElement e)
    {
        bool isL = e.Type == ElementType.L;
        string type = isL ? "L" : "C";
        var (text, unit) = MatchValueFormat.Split(e.ValueText);

        var c = new EditableComponent
        {
            InstanceName = e.Name,
            Symbol = isL ? SymbolKind.Inductor : SymbolKind.Capacitor,
            X = e.X, Y = e.Y,
            Rotation = e.IsShunt ? SymbolRotation.R0 : MatchSchematicModel.SeriesRotation,
        };
        c.Parameters.Add(new EditableParameter
        {
            Name = type,
            Expression = text,
            Unit = unit,
            Dimension = isL ? UnitDimension.Inductance : UnitDimension.Capacitance,
        });

        // The pane's own label decision, made by the same method and the same argument (the lead
        // below the symbol, which is the same length for an ordinary arm and for a block), so the
        // copy is the same drawing — including whether this arm's label sits beside the symbol or
        // under its ground.
        if (e.IsShunt)
        {
            var (dx, dy) = MatchShuntLabels.Offsets(
                [type, e.Name, $"{type} = {e.ValueText}"],
                MatchLadderLayout.Pitch, MatchSchematicGeometry.LeadHalf);
            for (int i = 0; i < 3; i++) c.LabelOffsets.Add((dx, dy));
        }

        return c;
    }

    private static EditableComponent Ground(double x, double y) =>
        new() { Symbol = SymbolKind.Ground, X = x, Y = y, ShowTypeLabel = false, ShowInstanceName = false };

    private static EditableComponent Termination(MatchLadderTermination t)
    {
        var (text, unit) = MatchValueFormat.Split(t.ResistanceText);
        var c = new EditableComponent
        {
            InstanceName = TerminationName(t.End),
            Symbol = SymbolKind.TermG,
            X = t.X, Y = MatchLadderLayout.SpineY + MatchSchematicGeometry.LeadHalf,
            Rotation = SymbolRotation.R0,
        };
        c.Parameters.Add(new EditableParameter
        {
            Name = "Num",
            Expression = t.End.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        c.Parameters.Add(new EditableParameter
        {
            Name = "Z", Expression = text, Unit = unit, Dimension = UnitDimension.Resistance,
        });
        return c;
    }

    /// <summary>
    /// The spine, in the gaps between series elements — the same segments the pane draws, and for the
    /// same reason (a built-in glyph carries its own leads, so a port-to-port line would lay a second
    /// wire across every series body).
    /// </summary>
    private static IEnumerable<EditableWire> SpineSegments(MatchLadderLayout layout)
    {
        const double lead = MatchSchematicGeometry.LeadHalf;
        double y = MatchLadderLayout.SpineY;
        double cursor = layout.PortLeftX;

        foreach (var e in layout.Elements.Where(e => !e.IsShunt).OrderBy(e => e.X))
        {
            double left = e.X - lead;
            if (left > cursor) yield return Wire(cursor, y, left, y);
            cursor = Math.Max(cursor, e.X + lead);
        }
        if (layout.PortRightX > cursor) yield return Wire(cursor, y, layout.PortRightX, y);
    }

    private static EditableWire Wire(double x0, double y0, double x1, double y1)
    {
        var w = new EditableWire();
        w.Points.Add((x0, y0));
        w.Points.Add((x1, y1));
        return w;
    }
}
