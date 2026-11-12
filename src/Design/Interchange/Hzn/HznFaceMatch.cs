// HZN-2 R-hzn2-4 — a face named by index, found again by its geometry.
//
// Hertzon records a face reference as `{obj, face, sig}`. circuitRF never stores an index (docs/design/em-3d.md §6.4):
// a face is named, and the name survives an edit. So the INDEX IS NEVER TRUSTED. The `sig` — the face's centroid,
// outward normal and area in metres, which is what the example files' values were found to be (src/Design/RESOLVED.md
// §HZN) — is matched against the faces of the geometry circuitRF built, and the face whose centroid, normal and area
// all agree is the face. No match, or two, is a refusal item.

namespace CircuitRF.Design.Interchange.Hzn;

/// <summary>A face of a built solid, in the document's frame and metres.</summary>
public sealed record HznFaceFrame(string Name, double[] Centroid, double[] Normal, double Area);

public static class HznFaceMatch
{
    /// <summary>A centroid must agree within this many metres (0.1 µm: far under any feature, far over the rounding of a
    /// DBU and of a sig written as a double).</summary>
    public const double CentroidToleranceM = 1e-7;

    /// <summary>Unit normals must agree within this per component.</summary>
    public const double NormalTolerance = 1e-6;

    /// <summary>Areas must agree within this fraction.</summary>
    public const double AreaRelativeTolerance = 1e-6;

    public sealed record Result(string? Name, string? Problem);

    /// <summary>The face of <paramref name="faces"/> that <paramref name="sig"/> describes: the first seven numbers are the
    /// centroid (x, y, z), the normal (x, y, z) and the area; an eighth, when present, was 1 in every example and is not used.</summary>
    public static Result Match(IReadOnlyList<HznFaceFrame> faces, IReadOnlyList<double> sig)
    {
        if (sig.Count < 7 || sig.Take(7).Any(double.IsNaN))
            return new(null, "its signature does not hold a centroid, a normal and an area");
        var hits = new List<string>();
        foreach (var f in faces)
        {
            bool centroid = Enumerable.Range(0, 3).All(i => Math.Abs(f.Centroid[i] - sig[i]) <= CentroidToleranceM);
            bool normal = Enumerable.Range(0, 3).All(i => Math.Abs(f.Normal[i] - sig[3 + i]) <= NormalTolerance);
            bool area = Math.Abs(f.Area - sig[6]) <= AreaRelativeTolerance * Math.Max(Math.Abs(sig[6]), 1e-30);
            if (centroid && normal && area) hits.Add(f.Name);
        }
        return hits.Count switch
        {
            1 => new(hits[0], null),
            0 => new(null, "no face of the geometry has that centroid, normal and area"),
            _ => new(null, $"more than one face fits its signature ({string.Join(", ", hits)})"),
        };
    }
}
