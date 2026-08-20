using System;
using System.Collections.Generic;
using CircuitRF.Ui.Diagnostics.Fixtures;

namespace CircuitRF.Ui.Diagnostics;

/// <summary>
/// Every user-doc figure that is a capture of the live interface, one row each.
///
/// <para>The shape is deliberately the same as <see cref="SymbolArtworkGenerator.Catalog"/>, because
/// that is the property which has kept the symbol generator alive for a year: <b>adding a figure is
/// one row</b>, and there is nowhere else to remember to touch.</para>
///
/// <para><b><see cref="Row.Id"/> is a contract.</b> It is the file stem AND the
/// <c>{{ui: …}}</c> key a documentation page cites. Renaming one breaks a page — and breaks it
/// loudly, because an unresolvable placeholder fails generation.</para>
///
/// <para><b>Every row states an explicit capture size.</b> There is no natural-size fallback: a
/// control captured on its own has no size of its own, and an unsized capture is an empty one.</para>
/// </summary>
public static class FigureCatalog
{
    /// <summary>One figure.</summary>
    /// <param name="Id">File stem and placeholder key.</param>
    /// <param name="Build">Puts the interface into the state worth photographing.</param>
    /// <param name="Width">Capture width, in device-independent pixels. Excludes any window frame.</param>
    /// <param name="Height">Capture height. Excludes any window frame.</param>
    /// <param name="Chrome">A synthetic window frame, or null for a bare panel.</param>
    /// <param name="Caption">The figure caption written under the picture.</param>
    /// <param name="MustContainPopup">
    /// True when this figure declares a popup. Generation fails if the popup contributed nothing —
    /// "the menu silently did not render" is otherwise indistinguishable from "the menu is closed".
    /// </param>
    public readonly record struct Row(
        string Id,
        Func<FigureScene> Build,
        int Width,
        int Height,
        WindowFrame? Chrome,
        string Caption,
        bool MustContainPopup = false);

    public static readonly IReadOnlyList<Row> Catalog =
    [
        new("schematic-editor", DocFixtures.SchematicEditor, 1100, 700,
            WindowFrame.Titled("circuitRF — FET S-Parameters"),
            "The schematic editor with the shipped FET S-parameter test bench open."),

        new("schematic-context-menu", DocFixtures.SchematicContextMenu, 1100, 700,
            WindowFrame.Titled("circuitRF — FET S-Parameters"),
            "Right-clicking a component opens its context menu.",
            MustContainPopup: true),

        new("symbol-editor", DocFixtures.SymbolEditor, 1100, 700,
            WindowFrame.Titled("circuitRF — Symbol editor"),
            "The symbol editor, showing the SDD's variadic body and its pins."),

        new("layout-editor", DocFixtures.LayoutEditor, 1100, 700,
            WindowFrame.Titled("circuitRF — Layout editor"),
            "The layout editor."),

        new("data-display", DocFixtures.DataDisplay, 1100, 700,
            WindowFrame.Titled("circuitRF — Data Display"),
            "The Data Display document."),

        new("em-setup-editor", DocFixtures.EmSetup, 980, 680,
            WindowFrame.Titled("EM Setup"),
            "The EM Setup editor: stackup, ports and the frequency sweep."),

        new("wbond-editor", DocFixtures.WBondEditor, 1100, 700,
            WindowFrame.Titled("wBond"),
            "The wBond editor carrying the shipped default wirebond design."),
    ];
}
