// ================================================================
//  LimitArea.cs  —  a hatched region of a Rect plot a curve must stay
//  out of: a filter's rejection, a passband's ripple floor, a mask.
//
//  The user's own, drawn on the plot whatever the traces are; the
//  goal-driven spec lines (SpecLineResolve) are a result's and are
//  drawn separately.
// ================================================================

using System.Text.Json.Serialization;

namespace CircuitRF.Render.DataDisplay;

/// <summary>Which side of a limit is hatched — the side a passing curve stays out of.</summary>
public enum LimitAreaSide
{
    /// <summary>A ceiling: the curve must stay at or below the limit, so the area above it is hatched.</summary>
    Above,
    /// <summary>A floor: the curve must stay at or above the limit, so the area below it is hatched.</summary>
    Below,
}

/// <summary>
/// One hatched limit area of a Rect plot, in the plot's DISPLAYED units — the units its X axis and its window
/// are in (a frequency in the plot's frequency unit), as its axis limits are.
/// </summary>
public sealed class LimitArea
{
    public LimitAreaSide Side { get; set; } = LimitAreaSide.Above;

    /// <summary>The limit, on the Y axis it is drawn against.</summary>
    public double Limit { get; set; }

    /// <summary>Where the area starts along X; NaN runs it from the plot's left edge.</summary>
    public double XStart { get; set; } = double.NaN;

    /// <summary>Where the area ends along X; NaN runs it to the plot's right edge.</summary>
    public double XEnd { get; set; } = double.NaN;

    /// <summary>Drawn against the right-hand (secondary) Y axis.</summary>
    public bool OnSecondaryAxis { get; set; }

    public LimitArea Clone() => (LimitArea)MemberwiseClone();
}

/// <summary>A <see cref="LimitArea"/> as a <c>.cdd</c> stores it; an open end is written as null.</summary>
public sealed class LimitAreaConfig
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LimitAreaSide Side { get; set; } = LimitAreaSide.Above;
    public double  Limit  { get; set; }
    public double? XStart { get; set; }
    public double? XEnd   { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool    OnSecondaryAxis { get; set; }

    public static LimitAreaConfig From(LimitArea a) => new()
    {
        Side = a.Side, Limit = a.Limit,
        XStart = double.IsFinite(a.XStart) ? a.XStart : null,
        XEnd   = double.IsFinite(a.XEnd)   ? a.XEnd   : null,
        OnSecondaryAxis = a.OnSecondaryAxis,
    };

    public LimitArea ToArea() => new()
    {
        Side = Side, Limit = Limit,
        XStart = XStart ?? double.NaN,
        XEnd   = XEnd   ?? double.NaN,
        OnSecondaryAxis = OnSecondaryAxis,
    };
}
