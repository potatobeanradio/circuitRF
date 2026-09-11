using System;

namespace RfCore.Data;

/// <summary>
/// <b>A LEVEL is only meaningful against a reference, and this is how a <see cref="DataSet"/> says
/// what its reference is.</b>
///
/// <para>Most quantities in a result are absolute (watts, ohms, hertz) or are ratios that need no
/// reference at all (a directivity, an efficiency, a gain in dBi). A few are neither: a dBm is a
/// level above a stated power, and the same number means different things against different ones.
/// An antenna's total radiated power and its peak EIRP are the pair this exists for — they are what
/// the antenna radiates when a transmitter of a stated power is connected, and the stated power is
/// an input to the analysis rather than a result of it.</para>
///
/// <para><b>The reference is published as a cube beside the levels it applies to</b>, exactly as a
/// network's per-port reference impedance is (<see cref="NetworkMetrics.Z0CubeName"/>), and for
/// exactly the same reason: <i>a number whose reference is not in the file cannot be reproduced from
/// the file</i>. That is also what makes a level RE-REFERENCEABLE after the fact — the correction is
/// a subtraction and an addition, both of them exact, so a display can read a level against a
/// different reference with no re-run of whatever produced it.</para>
///
/// <para><b>Detection is a RELATIONSHIP, never a list of names.</b> A cube is a referenced level
/// when its own unit is dBm and its group publishes a reference — so anything that opts in by
/// carrying the unit and the sibling cube gets the behaviour, and nothing has to be told about any
/// particular engine's metric names. This mirrors
/// <see cref="NetworkMetrics.IsNetworkParamCubeSpec"/>, which decides the same kind of question the
/// same way.</para>
/// </summary>
public static class LevelReference
{
    /// <summary>The cube naming the conducted power a group's dBm levels are referenced to. One
    /// name, agreed between whatever publishes it and whatever reads it — the
    /// <see cref="NetworkMetrics.Z0CubeName"/> arrangement.</summary>
    public const string ReferencePowerCubeName = "ReferenceInputPowerDbm";

    /// <summary>The unit that marks a cube as a level rather than an absolute or a ratio.</summary>
    public const string LevelUnit = "dBm";

    /// <summary>
    /// Whether <paramref name="cubeSpec"/> is a dBm level whose reference this
    /// <paramref name="ds"/> publishes, and what that reference is.
    ///
    /// <para>False for the reference cube itself: it IS the reference, and re-referencing it would
    /// be asking what 0 dBm is above 10 dBm. False for a group that publishes no reference, whatever
    /// the unit says — a dBm with no reference in the file is not re-referenceable, and offering to
    /// re-reference it would mean guessing what it is currently against.</para>
    /// </summary>
    /// <param name="referenceDbm">The published reference, or NaN when this returns false.</param>
    public static bool IsReferencedLevel(DataSet? ds, string? cubeSpec, out double referenceDbm)
    {
        referenceDbm = double.NaN;
        if (ds is null || string.IsNullOrEmpty(cubeSpec)) return false;

        int    dot  = cubeSpec.LastIndexOf('.');
        string bare = dot < 0 ? cubeSpec : cubeSpec[(dot + 1)..];
        if (bare == ReferencePowerCubeName) return false;

        if (!ds.Contains(cubeSpec)) return false;
        if (!string.Equals(ds[cubeSpec].Unit, LevelUnit, StringComparison.OrdinalIgnoreCase))
            return false;

        string group   = dot < 0 ? "" : cubeSpec[..dot];
        string refSpec = group.Length == 0
            ? ReferencePowerCubeName : $"{group}.{ReferencePowerCubeName}";
        if (!ds.Contains(refSpec)) return false;

        var cube = ds[refSpec];
        if (cube.DataKind != DataKind.Real || cube.BufferLength == 0) return false;

        // The reference is ONE setting for the run, replicated over whatever axes the group's
        // metrics carry, so any element is the same element. Read the first rather than the one at
        // this trace's own slice: a trace may be pinned somewhere the reference cube has no matching
        // axis at all, and a reference that varied along an axis would be a different quantity from
        // the one this class is about.
        double v = cube.RealValues[0];
        if (!double.IsFinite(v)) return false;

        referenceDbm = v;
        return true;
    }
}
