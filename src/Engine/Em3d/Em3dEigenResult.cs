// brief-em3d-23 R-em3d23-4 — an eigenmode solve's modes as a DataSet.
//
// Like a static matrix, a set of modes is not S: there is no sweep and no Touchstone file. It lands as
// single-kind REAL cubes along one axis, "Mode", 1-based as Palace numbers them:
//   * f           the resonant frequency, Hz (the real part of Palace's complex eigenfrequency);
//   * Q           Palace's Q = Re f / (2 Im f): the LOADED Q — every loss the problem has, its lumped ports'
//                 resistances included, because an eigenmode solve treats a port as the load it is;
//   * Q_ext       [Mode, Port]: each lumped port's external Q, as Palace writes it (port-Q.csv);
//   * Q_unloaded  1 / (1/Q − Σ 1/Q_ext): the Q with the ports' loading taken out — arithmetic on the two
//                 figures Palace wrote, present only when it wrote both, and labelled as derived;
//   * Participation [Mode, Domain]: the fraction of each mode's electric energy in each meshed region
//                 (domain-E.csv's p_elec[k]) — what says "this mode lives in the lid cavity".

using RfCore.Data;

namespace CircuitRF.Engine.Em3d;

/// <summary>One mode as circuitRF reports it.</summary>
public sealed record Em3dMode(int Index, double FrequencyHz, double Q);

public static class Em3dEigenResult
{
    public const string ModeAxis = "Mode";
    public const string PortAxis = "Port";
    public const string DomainAxis = "Domain";

    public const string FrequencyCube = "f";
    public const string QCube = "Q";
    public const string ExternalQCube = "Q_ext";
    public const string UnloadedQCube = "Q_unloaded";
    public const string ParticipationCube = "Participation";

    /// <summary>The group the result's notes are carried in, as labels.</summary>
    public const string NotesGroup = "eigenmode";

    /// <summary>The notes cube in <see cref="NotesGroup"/>.</summary>
    public const string NotesCube = "Notes";

    /// <summary>
    /// The DataSet of an eigenmode solve. <paramref name="externalQ"/> is [mode, port] over
    /// <paramref name="ports"/>, or null; <paramref name="participation"/> is [mode, domain] over
    /// <paramref name="domains"/>, or null.
    /// </summary>
    public static DataSet Build(IReadOnlyList<Em3dMode> modes, IReadOnlyList<int> ports, double[,]? externalQ,
                                IReadOnlyList<string> domains, double[,]? participation, IReadOnlyList<string> notes)
    {
        int n = modes.Count;
        var modeAxis = new Axis(ModeAxis, [.. modes.Select(m => (double)m.Index)], "");
        var ds = new DataSet();
        ds.Add(FrequencyCube, new DataCube([modeAxis], [.. modes.Select(m => m.FrequencyHz)]) { Unit = "Hz" });
        ds.Add(QCube, new DataCube([modeAxis], [.. modes.Select(m => m.Q)]));

        if (externalQ is not null && ports.Count > 0)
        {
            var portAxis = new Axis(PortAxis, [.. ports.Select(p => (double)p)], "");
            ds.Add(ExternalQCube, new DataCube([modeAxis, portAxis], Flatten(externalQ)));
            ds.Add(UnloadedQCube, new DataCube([modeAxis], [.. Enumerable.Range(0, n).Select(i => UnloadedQ(modes[i].Q, Row(externalQ, i)))]));
        }
        if (participation is not null && domains.Count > 0)
        {
            var domainAxis = new Axis(DomainAxis, [.. Enumerable.Range(1, domains.Count).Select(k => (double)k)], "", [.. domains]);
            ds.Add(ParticipationCube, new DataCube([modeAxis, domainAxis], Flatten(participation)));
        }
        if (notes.Count > 0)
            ds.AddToGroup(NotesGroup, NotesCube, new DataCube(
                [new Axis("note", [.. Enumerable.Range(0, notes.Count).Select(k => (double)k)], "", [.. notes])],
                new double[notes.Count]));
        return ds;
    }

    /// <summary>
    /// The Q with the ports' loading removed: the losses add as 1/Q, so 1/Q_u = 1/Q − Σ 1/Q_ext. NaN when a
    /// figure is missing; +∞ when the ports account for every loss (a lossless cavity with loaded ports).
    /// </summary>
    public static double UnloadedQ(double loaded, IReadOnlyList<double> external)
    {
        if (!double.IsFinite(loaded) || external.Any(double.IsNaN)) return double.NaN;
        double inv = 1 / loaded - external.Where(double.IsFinite).Sum(q => 1 / q);
        return inv > 0 ? 1 / inv : double.PositiveInfinity;
    }

    private static double[] Row(double[,] m, int i) => [.. Enumerable.Range(0, m.GetLength(1)).Select(j => m[i, j])];

    private static double[] Flatten(double[,] m)
    {
        int r = m.GetLength(0), c = m.GetLength(1);
        var v = new double[r * c];
        for (int i = 0; i < r; i++)
            for (int j = 0; j < c; j++)
                v[i * c + j] = m[i, j];
        return v;
    }
}
