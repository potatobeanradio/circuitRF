// ================================================================
//  AxisSymbols.cs  —  what an ANGLE axis is called where a reader
//  sees it
//
//  Owner, 2026-09-11: "In the trace card for farfield, don't write
//  'theta' or 'phi', use their symbols instead. Same with the y-axis
//  labels."
//
//  The cube's axis NAME is "theta" — an identifier, chosen so a spec
//  can be typed in ASCII (`cube=farfield.U,cut=0,theta=...`) — and it
//  is the right thing to keep in a spec, a file and a refusal. It is
//  the wrong thing to print beside a plot, where every antenna
//  reference in existence writes θ.
//
//  So the mapping lives HERE, once, and both halves read it: the
//  trace card's axis rows (src/Ui) and the pinned-axis tokens the
//  Y-axis label and the marker readouts are built from
//  (TraceResolve.ApplyPinnedAxisDisplay). Two copies would drift, and
//  the whole point of a symbol is that the card and the axis agree.
//
//  It is DISPLAY ONLY. Nothing parses these back: a spec, a slice and
//  a DataSet all keep the ASCII name.
// ================================================================

using System;

namespace CircuitRF.Render.DataDisplay;

public static class AxisSymbols
{
    /// <summary>
    /// The symbol an axis is displayed under, or the name itself when it has none.
    ///
    /// <para>Only the two spherical angles are mapped. <c>cut</c> is NOT — it is a beamwidth cut's
    /// φ and reads as a plane rather than as an angle, and calling it φ would put two different
    /// axes under one symbol on one card. <c>az</c>/<c>el</c> are not either: they are a different
    /// convention with their own names, and an antenna reference that writes azimuth does not write
    /// φ for it.</para>
    /// </summary>
    public static string Display(string? axisName) => axisName switch
    {
        "theta" => "θ",
        "phi"   => "φ",
        _       => axisName ?? "",
    };

    /// <summary>Whether <paramref name="axisName"/> has a symbol at all — so a caller can tell a
    /// substitution from a pass-through without comparing strings.</summary>
    public static bool HasSymbol(string? axisName) => axisName is "theta" or "phi";
}
