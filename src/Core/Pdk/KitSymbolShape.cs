namespace CircuitRF.Core.Pdk;

/// <summary>
/// One drawn element of a kit's symbol, in the FILE's own coordinates and units.
///
/// <para><b>Why a neutral shape vocabulary rather than circuitRF's own primitives.</b> The reader
/// lives in the core and must stay framework-free; <c>SymbolPrimitive</c> lives in the UI project
/// alongside the renderer. So the reader reports what the file DREW, and the UI turns that into
/// primitives — the same split <see cref="KitSymbolPin"/> already follows for terminals.</para>
///
/// <para>Coordinates are exactly as the file states them: no scale is applied here, because the
/// scale is chosen from the whole drawing at once and that is the consumer's job.</para>
/// </summary>
public abstract record KitSymbolShape;

/// <summary>A straight segment.</summary>
public sealed record KitSymbolLine(double X1, double Y1, double X2, double Y2) : KitSymbolShape;

/// <summary>
/// A rectangle given by two opposite corners. Only rectangles that do NOT declare a terminal reach
/// here — one that does is a pin, and is reported as such instead.
/// </summary>
public sealed record KitSymbolRectangle(double X1, double Y1, double X2, double Y2, bool Filled)
    : KitSymbolShape;

/// <summary>
/// A run of points, closed or open. <paramref name="Xy"/> is a flat list of x,y pairs and never
/// repeats the first point at the end — a closed run says so with <paramref name="Closed"/>.
/// </summary>
public sealed record KitSymbolPath(IReadOnlyList<double> Xy, bool Closed, bool Filled) : KitSymbolShape;

/// <summary>
/// A circular arc.
///
/// <para><b>The angles are the file's own and are NOT circuitRF's.</b> This format measures them
/// counter-clockwise ON SCREEN from the +x axis; circuitRF's own arc primitive measures clockwise.
/// The conversion is the consumer's, and it is a sign flip on both fields — doing it here would
/// bury a rendering convention inside a format reader.</para>
/// </summary>
public sealed record KitSymbolArc(double Cx, double Cy, double Radius, double StartDeg, double SweepDeg)
    : KitSymbolShape;
