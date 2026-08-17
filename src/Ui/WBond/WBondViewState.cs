using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.WBond;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// Which editing tool a click means — the wBond editor's own three, mirroring the Layout Editor's
/// <c>Tool</c> exactly in shape (owner, 2026-08-16).
///
/// <para><b>Mutually exclusive by construction.</b> Draw Wire and Rotate used to be two independent
/// <c>ToggleButton</c>s that each had to remember to un-press the other, and "no tool" was a state
/// with no button of its own — so Escape could leave the toolbar showing nothing selected while the
/// editor was in select mode. One enum makes the third state nameable and the exclusion free.</para>
/// </summary>
public enum WBondTool
{
    /// <summary>Pick, marquee and drag — the resting tool, and where Escape returns.</summary>
    Select,

    /// <summary>Click a start point, click an end point (§6.4). The <c>W</c> key.</summary>
    DrawWire,

    /// <summary>Rotate about an end point (WB26a). The <c>R</c> key.</summary>
    Rotate,
}

/// <summary>Which of the editor's two canvases are showing (owner, 2026-08-16).</summary>
public enum WBondViewMode
{
    /// <summary>Both, split — the shipped default.</summary>
    Both,

    /// <summary>The profile view alone, at the full size of the canvas area.</summary>
    Profile,

    /// <summary>The layout view alone.</summary>
    Layout,
}

/// <summary>
/// The <c>.wBond</c> file's view state — how the editor was arranged, as distinct from what the
/// design is.
///
/// <para><b>It travels in <see cref="WBondDesign.ViewStateJson"/>, which the file format has always
/// carried as an opaque string.</b> That field exists precisely so the UI can persist things the
/// framework-free half must not know about, and it is why none of this needs a
/// <c>WBondIo.CurrentFormatVersion</c> bump: an older build reads the string, understands none of it,
/// and writes it back unaltered. A file saved by this build therefore still opens in one that
/// predates it.</para>
///
/// <para><b>Every field is optional and every reader takes a default.</b> Malformed or absent JSON is
/// not an error — it is a document that was never arranged, which is the normal state of a new one.
/// A view setting is never worth refusing to open a design over.</para>
/// </summary>
public sealed class WBondViewState
{
    public WBondViewMode ViewMode { get; set; } = WBondViewMode.Both;

    /// <summary>Whether the Array Inductance panel is showing (the <c>I</c> key).</summary>
    public bool PanelVisible { get; set; } = true;

    /// <summary>
    /// Whether both canvases show the Layout Editor's rulers along their top and left edges
    /// (owner, 2026-08-16). One switch for both views, because they are one editor.
    /// </summary>
    public bool RulersVisible { get; set; } = true;

    /// <summary>
    /// The shipped profile plane — <b>YZ</b> (owner, 2026-08-16), matching the north/south default
    /// wire, which YZ shows side-on. Auto was the previous default and remains selectable.
    /// </summary>
    public const double DefaultProfileAxisDegrees = 90.0;

    /// <summary>
    /// The profile view's plane, in degrees, or null for AUTO (each wire on its own chord).
    /// Degrees rather than radians because this is a file a person may read.
    ///
    /// <para><b>Null is a real value here, not "absent"</b>, which is why this class serialises nulls
    /// (see <see cref="Options"/>): the default is now YZ, so a design saved in AUTO has to write its
    /// null explicitly or it would reopen in YZ.</para>
    /// </summary>
    public double? ProfileAxisDegrees { get; set; } = DefaultProfileAxisDegrees;

    /// <summary>The editor's display unit (§6.5) — independent of the layout's own, by design.</summary>
    public WBondUnit DisplayUnit { get; set; } = WBondUnit.Mil;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,

        // NOT WhenWritingNull: a null ProfileAxisDegrees MEANS Auto, and the property's own default is
        // now YZ — so an omitted key would silently reopen an Auto design in YZ.
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Reads the state out of a design, falling back to defaults on anything unreadable.</summary>
    public static WBondViewState From(WBondDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);

        if (string.IsNullOrWhiteSpace(design.ViewStateJson)) return new WBondViewState();

        try
        {
            return JsonSerializer.Deserialize<WBondViewState>(design.ViewStateJson, Options)
                   ?? new WBondViewState();
        }
        catch (JsonException)
        {
            // A view setting is never worth refusing to open a design over — see the class remarks.
            return new WBondViewState();
        }
    }

    /// <summary>Writes the state into a design, ready for <c>WBondIo.WriteFile</c>.</summary>
    public void To(WBondDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        design.ViewStateJson = JsonSerializer.Serialize(this, Options);
    }

    /// <summary>The azimuth as the view-model holds it — radians, or null for AUTO.</summary>
    [JsonIgnore]
    public double? ProfileAzimuthRadians
    {
        get => ProfileAxisDegrees is { } d ? d * Math.PI / 180.0 : null;
        set => ProfileAxisDegrees = value is { } r ? r * 180.0 / Math.PI : null;
    }
}
