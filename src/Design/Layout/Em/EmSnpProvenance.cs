// R-em-20 — the .snp carries a provenance stamp, and a stale one is DETECTED rather than silently
// believed. §10.8 is explicit that this is the one failure mode the design introduces (an .snp
// referenced by a schematic can outlive the layout it came from) and that the whole mitigation is a
// header stamp plus a warning.
//
// **Hash the extracted EmProblem, not the raw file bytes.** A cosmetic layout edit — moving a
// silkscreen label, renaming a net, nudging a via — must NOT report staleness, and a change that
// genuinely moves the cross-section must ALWAYS report it. The EmProblem is exactly "everything the
// answer depends on and nothing else", so hashing it makes both halves of that true by construction
// rather than by a heuristic about which edits matter.

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em;

/// <summary>The fields of a provenance stamp, parsed back out of an existing <c>.snp</c> header.</summary>
public sealed record EmProvenanceStamp(string GeometryHash, string MeshHash, string PortHash,
                                       string? ModelRevision = null)
{
    public const string Marker         = "circuitRF-EM";
    public const string GeometryPrefix = "circuitRF-EM geometry: ";
    public const string MeshPrefix     = "circuitRF-EM mesh: ";
    public const string PortPrefix     = "circuitRF-EM ports: ";

    /// <summary>
    /// <b>Which PHYSICS wrote this file</b> — <see cref="CircuitRF.Engine.Mom.PlanarKernel.ModelRevision"/>,
    /// in plain text rather than hashed, so a reader can see it. The three hashes above answer "did
    /// the DOCUMENT change"; this is the one that answers "did the SOLVER change", which no hash of
    /// an <c>EmProblem</c> can.
    ///
    /// <para><b>Absent on a file written before it existed, and that is exactly the case it is for</b>
    /// — such a file was produced by a kernel whose metal was a perfect conductor, so it reads as
    /// stale once and correctly. It is emitted by the PLANAR overload only; kernel A did not change
    /// and must not have every one of its files marked.</para>
    /// </summary>
    public const string ModelPrefix    = "circuitRF-EM model: ";

    /// <summary>
    /// <b>PCAL2/R-pcal2-2 — a line saying this file's numbers were produced outside the condition
    /// the arithmetic that produced them is valid under.</b> Its own prefix rather than a free
    /// comment so <see cref="EmSnpProvenance.ReadCaveats"/> can find it again: the whole finding was
    /// that a run's notes do not survive onto the file, and a caveat nothing can read back has the
    /// same defect one step further on.
    /// </summary>
    public const string CaveatPrefix   = "circuitRF-EM caveat: ";

    /// <summary>
    /// <b>MIM-10 — which drawn port became which port NUMBER of this matrix.</b> The run says it in
    /// its notes and the notes do not survive onto the file, which is the same finding
    /// <see cref="CaveatPrefix"/> exists for: a result meant to be composed in a circuit is read
    /// months later by someone who has only the file.
    ///
    /// <para>Swapping two ports of a reciprocal part — which a spiral and a capacitor both are —
    /// produces a curve that is perfectly plausible and is not this structure, so there is no
    /// symptom to notice. The reference impedance rides on the same line because it is the other
    /// half of the same mistake: Touchstone states ONE <c>R</c> for the whole file, so a run whose
    /// ports differ has already lost that in the format.</para>
    /// </summary>
    public const string PortPrefixNumbered = "circuitRF-EM port ";
}

public static class EmSnpProvenance
{
    /// <summary>The header lines to stamp into a freshly written <c>.snp</c>.</summary>
    public static IReadOnlyList<string> BuildHeader(
        EmProblem problem, EmMeshSettings mesh, string setupName, string layoutRef, DateTimeOffset when)
        => BuildHeader(QuasiStaticKernel.KernelName,
                       GeometryHash(problem), MeshHash(mesh), PortHash(problem),
                       setupName, layoutRef, when);

    /// <summary>
    /// D9 — the planar counterpart. <b>A planar run through the same <c>.snp</c> path with no planar
    /// hashes would write a stamp that CANNOT go stale, which is worse than no stamp at all</b>: the
    /// warning §10.8 calls "the one failure mode this design introduces" would simply stay silent
    /// while a schematic went on reading a result the layout no longer produces.
    /// </summary>
    public static IReadOnlyList<string> BuildHeader(
        PlanarProblem problem, PlanarMeshSettings mesh, IReadOnlyList<PlanarPort> ports,
        string setupName, string layoutRef, DateTimeOffset when,
        IReadOnlyList<string>? caveats = null,
        IReadOnlyList<string>? portMap = null)
        => BuildHeader(PlanarKernel.KernelName,
                       GeometryHash(problem), MeshHash(mesh), PortHash(ports),
                       setupName, layoutRef, when, caveats, PlanarKernel.ModelRevision, portMap);

    /// <summary>
    /// brief-em3d-7 R-em3d7-5d — a 3D run's stamp: the planar one EXTENDED, not a second header format.
    /// The three hashes cover what the solver was handed (the geometry script, the solver section, the
    /// ports); the model line names the solver and its version; <paramref name="solverLines"/> carry
    /// the mesh it started and finished on, the adaptive passes and the operating temperature, which
    /// the notes say and the file must say too.
    /// </summary>
    public static IReadOnlyList<string> BuildHeader3D(
        string solverName, string geometryHash, string meshHash, string portHash, string solverVersion,
        IReadOnlyList<string> solverLines, string setupName, string layoutRef, DateTimeOffset when)
        => BuildHeader(solverName, geometryHash, meshHash, portHash, setupName, layoutRef, when,
                       caveats: null, modelRevision: solverVersion, portMap: solverLines);

    /// <summary>The same SHA-256 the planar stamps use, over text a caller has already made canonical.</summary>
    public static string HashText(string canonical) => Sha(canonical);

    private static IReadOnlyList<string> BuildHeader(
        string kernelName, string geometry, string mesh, string ports,
        string setupName, string layoutRef, DateTimeOffset when,
        IReadOnlyList<string>? caveats = null, string? modelRevision = null,
        IReadOnlyList<string>? portMap = null)
    {
        var lines = new List<string>
        {
            // ASCII, for the reason the caveat lines below are (and MIM-9 measured): the encoding a
            // Touchstone file is written in turns this em dash into "?", so every .sNp in every
            // workspace opened with "Generated by circuitRF ? EM:".
            $"Generated by circuitRF - EM: {kernelName}",
            $"circuitRF-EM setup: {setupName}",
            $"circuitRF-EM layout: {layoutRef}",
            $"circuitRF-EM written: {when.ToString("u", CultureInfo.InvariantCulture)}",
            EmProvenanceStamp.GeometryPrefix + geometry,
            EmProvenanceStamp.MeshPrefix     + mesh,
            EmProvenanceStamp.PortPrefix     + ports,
        };

        // Emitted only where there is one — the planar overload. A kernel-A file gains no byte, so
        // no cross-section .snp in any workspace is marked stale to record a token kernel A has not
        // got. See PlanarKernel.ModelRevision.
        if (modelRevision is not null) lines.Add(EmProvenanceStamp.ModelPrefix + modelRevision);

        // MIM-10 — the port map, one line per port, straight after the hash that covers the same
        // ports. Additive and omitted when absent, so kernel A's files and any caller that does not
        // supply one gain no byte.
        if (portMap is { Count: > 0 })
            foreach (string line in portMap) lines.Add(line);

        // Empty on every run that has nothing to declare, so an ordinary .sNp gains no byte and
        // stays byte-identical to one written before PCAL2 — the same omit-at-default rule the
        // `.cem` format and the mesh hash already hold themselves to.
        if (caveats is { Count: > 0 })
            foreach (string c in caveats) lines.Add(EmProvenanceStamp.CaveatPrefix + c);

        lines.Add(
            "circuitRF-EM: re-running the EM setup rewrites this file. If circuitRF reports it as " +
            "stale, the layout's cross-section changed after this file was written.");
        return lines;
    }

    /// <summary>
    /// <b>MIM-10 — which drawn port label became which port NUMBER, where it sits, and at what
    /// reference impedance.</b> One line per port, ASCII, for
    /// <see cref="EmProvenanceStamp.PortPrefixNumbered"/>.
    ///
    /// <para><b>Why on the FILE and not only in the run's notes.</b> MIM-10's whole recipe is to EM
    /// one half of a circuit and compose the other half around it, so this result is read by a
    /// netlist rather than by the person who ran it — often months later and on another machine,
    /// where the notes are gone. Two ports of a reciprocal part swapped is the failure that has no
    /// symptom: the curve is smooth, passive and wrong.</para>
    ///
    /// <para><b>The label names come from the extraction, index-aligned, never re-derived</b> —
    /// <c>EmPortExtractionResult.SourceLabels</c>' own note says why: a label whose text names no
    /// number is auto-numbered in document order, and a second copy of that ordering is free to
    /// drift from the first.</para>
    ///
    /// <para>Empty when there are no ports, so nothing is emitted for a run that has none.</para>
    /// </summary>
    public static IReadOnlyList<string> PortMap(
        IReadOnlyList<string> levelNames, IReadOnlyList<PlanarPort> ports,
        IReadOnlyList<LabelShape> labels, int dbuPerMicron, LayoutUnit displayUnit)
    {
        if (ports.Count == 0) return [];

        var lines = new List<string>();
        for (int i = 0; i < ports.Count; i++)
        {
            var p = ports[i];
            string name  = i < labels.Count && labels[i].Text is { Length: > 0 } t ? t : "unnamed";
            string level = p.LayerIndex is { } lv && lv >= 0 && lv < levelNames.Count
                               ? $" on '{levelNames[lv]}'" : "";
            lines.Add(
                $"{EmProvenanceStamp.PortPrefixNumbered}{p.Number}: '{name}'{level} at " +
                $"{AsciiCoord(labels, i, dbuPerMicron, displayUnit)}, {KindWord(p.Kind)}, " +
                $"{AsciiOhms(p.Z0)}");
        }

        // Said once, under the list, and only where there is an order to get wrong. The sentence is
        // about what the READER has to do with the file, which is the part no per-port line carries.
        if (ports.Count > 1)
            lines.Add(
                "circuitRF-EM: port N of this file is the label named on the 'port N' line above. " +
                "A circuit that composes this result binds its nets in that order; two ports of a " +
                "reciprocal part swapped gives a plausible curve of a different structure.");

        return lines;
    }

    /// <summary>The label's own anchor in the LAYOUT's display unit — the same choice the run's port
    /// notes make (owner request, 2026-08-11) — with an ASCII suffix, because "µm" does not survive
    /// the encoding a Touchstone file is written in.</summary>
    private static string AsciiCoord(IReadOnlyList<LabelShape> labels, int i,
                                     int dbuPerMicron, LayoutUnit unit)
    {
        if (i >= labels.Count) return "(unknown)";
        string suffix = unit == LayoutUnit.Um ? "um" : LayoutUnits.Suffix(unit);
        return $"({LayoutUnits.Format(labels[i].X, unit, dbuPerMicron)}, " +
               $"{LayoutUnits.Format(labels[i].Y, unit, dbuPerMicron)} {suffix})";
    }

    private static string KindWord(PlanarPortKind kind) => kind switch
    {
        PlanarPortKind.Internal           => "internal, to the ground plane",
        PlanarPortKind.InternalDeltaGap   => "internal delta gap",
        _                                 => "edge, de-embedded",
    };

    /// <summary><see cref="EmPortExtraction"/>'s own spelling with the ohm sign written out.</summary>
    private static string AsciiOhms(Complex z)
        => z.Imaginary == 0
            ? $"{z.Real.ToString("G6", CultureInfo.InvariantCulture)} Ohm"
            : $"{z.Real.ToString("G6", CultureInfo.InvariantCulture)}" +
              $"{(z.Imaginary < 0 ? "-" : "+")}" +
              $"{Math.Abs(z.Imaginary).ToString("G6", CultureInfo.InvariantCulture)}j Ohm";

    /// <summary>
    /// <b>PCAL2/R-pcal2-2 — what a planar run has to declare about itself on the file it writes.</b>
    /// Today that is exactly one thing: the port de-embedding was applied outside the geometry it is
    /// valid for, because the setup asked for that explicitly.
    ///
    /// <para>Empty for every other run, including a run that had a breach and was REFUSED — that one
    /// writes no file at all.</para>
    /// </summary>
    public static IReadOnlyList<string> ValidityCaveats(PlanarSolveResult? solve)
    {
        if (solve is null) return [];
        var caveats = new List<string>();

        var breached = new List<PlanarFeedClearance>();
        foreach (var c in solve.FeedClearances) if (c.Breached) breached.Add(c);
        if (breached.Count > 0)
        {
            var parts = new List<string>();
            foreach (var b in breached)
                parts.Add($"port {b.PortNumber} at {b.Heights.ToString("0.##", CultureInfo.InvariantCulture)} h " +
                          $"(needs {b.RequiredHeights.ToString("0.#", CultureInfo.InvariantCulture)})");

            caveats.Add(
                "the port de-embedding was applied OUTSIDE the geometry it is valid for: " +
                string.Join(", ", parts) +
                ". Another conductor sits inside the run of line the calibration standard reproduces, so " +
                "the error box was measured on a structure that is not the one being corrected. These " +
                "s-parameters are not a measurement of this structure. Re-run with the feeds separated, " +
                "or with port de-embedding off, before comparing them with anything.");
        }

        // ── R-pcal7-4 — A ROW THAT IS NOT A NETWORK IS NAMED ON THE FILE'S OWN FACE ─────────────
        //
        // PCAL2's finding, one case further on: the run says NOT PASSIVE in its notes and the notes
        // do not survive onto the `.sNp`. On the shipped MMIC spiral the four points below about
        // 0.8 GHz read 110 nH against a real 2.8 nH, with σ_max up to 1.09 — and a user opening that
        // file in the Data Display six months later sees a smooth, plausible curve with nothing on
        // it to say the bottom decade is not an answer. σ_max > 1 is not an inaccuracy that needs a
        // threshold argued for: a passive structure cannot do it, so the excess is the ANALYSIS.
        // The frequencies are quoted because a reader acts on them — by moving the sweep's lower
        // edge — and a count cannot be acted on.
        if (solve.NonPassivePoints.Count > 0)
        {
            var np = solve.NonPassivePoints;
            double worst = 0;
            foreach (var e in np) if (e.SigmaMax > worst) worst = e.SigmaMax;

            string which = np.Count == 1
                ? Eng(np[0].FrequencyHz) + "Hz"
                : $"{Eng(np[0].FrequencyHz)}Hz to {Eng(np[^1].FrequencyHz)}Hz";

            // ASCII ONLY, and that is not a style choice. Touchstone is written in an encoding this
            // writer transliterates to, and a sigma or a subscripted a21 comes out of it as "?" —
            // measured on this very line, which first read "worst ?_max(S)". A caveat the file
            // cannot spell is a caveat nobody can act on.
            // ── MIM-9 item 4 — THE CAUSE COMES FROM THE RUN, NOT FROM A SECOND GUESS HERE ──────
            //
            // What used to follow "the excess is this analysis rather than the design" was a
            // sentence blaming the peel, written independently of the one PlanarSolve's own panel
            // note carries. Two copies of one guess: MIM-9 corrected the attribution in the panel
            // and this one would have gone on saying the old thing in every .sNp ever written —
            // which is the copy that outlives the session and is the first thing anyone reads six
            // months later. The engine decides it once, from its own counters, and this reads it.
            caveats.Add(
                $"{np.Count} of these rows are NOT A PASSIVE NETWORK and should not be used: " +
                $"{which}, worst sigma_max(S) = " +
                $"{worst.ToString("0.0###", CultureInfo.InvariantCulture)}. " +
                "A passive structure cannot exceed 1, so the excess is this analysis rather than the " +
                "design." +
                (solve.NonPassivityCause.Length > 0 ? " " + solve.NonPassivityCause : ""));
        }

        return caveats;
    }

    /// <summary>Engineering notation for a frequency, so a caveat reads the way the run's own note
    /// does. The engine's own <c>SurfaceMesher.Eng</c> is internal to it, and one caveat line is not
    /// a reason to widen that.</summary>
    private static string Eng(double v)
    {
        (double scale, string suffix) = Math.Abs(v) switch
        {
            >= 1e9 => (1e9, "G"),
            >= 1e6 => (1e6, "M"),
            >= 1e3 => (1e3, "k"),
            _      => (1.0, ""),
        };
        return (v / scale).ToString("0.###", CultureInfo.InvariantCulture) + " " + suffix;
    }

    /// <summary>Every caveat line stamped into an existing <c>.snp</c>, in file order. Empty when
    /// there are none, which is the normal case and is not the same as the file being unstamped.</summary>
    public static IReadOnlyList<string> ReadCaveats(string snpPath)
    {
        var found = new List<string>();
        if (!File.Exists(snpPath)) return found;
        try
        {
            foreach (var raw in File.ReadLines(snpPath))
            {
                string line = raw.TrimStart();
                if (line.StartsWith('#')) break;          // the option line ends the header
                if (!line.StartsWith('!')) continue;
                line = line[1..].Trim();
                if (line.StartsWith(EmProvenanceStamp.CaveatPrefix, StringComparison.Ordinal))
                    found.Add(line[EmProvenanceStamp.CaveatPrefix.Length..].Trim());
            }
        }
        catch (IOException) { return found; }
        return found;
    }

    /// <summary>
    /// The stackup identity, conductor outlines and length — everything the extracted cross-section
    /// consists of. Deliberately excludes port reference impedances and mesh settings, which get
    /// their own lines so a mismatch can say WHICH of the three moved.
    /// </summary>
    public static string GeometryHash(EmProblem p)
    {
        var sb = new StringBuilder();
        sb.Append("L=").Append(R(p.LengthMeters)).Append('|');
        foreach (var c in p.Conductors)
        {
            sb.Append("C:").Append(c.Name).Append(':').Append(R(c.SigmaSm)).Append(':');
            foreach (var pt in c.Outline) sb.Append(R(pt.X)).Append(',').Append(R(pt.Y)).Append(';');
            sb.Append('|');
        }
        foreach (var r in p.Regions)
            sb.Append("R:").Append(R(r.YBottom)).Append(':').Append(R(r.YTop)).Append(':')
              .Append(R(r.Material.EpsR)).Append(':').Append(R(r.Material.TanD)).Append(':')
              .Append(R(r.Material.MuR)).Append('|');
        sb.Append("G:").Append(p.Ground is null ? "none" : R(p.Ground.Y) + ":" + R(p.Ground.SigmaSm));
        return Sha(sb.ToString());
    }

    public static string MeshHash(EmMeshSettings m)
        => Sha($"{m.MinCellsAcrossWidth}|{m.EdgeCells}|{R(m.EdgeFractionOfWidth)}|" +
               $"{R(m.EdgeGrowthRatio)}|{R(m.TruncationHeights)}|{m.TruncationTailCells}");

    public static string PortHash(EmProblem p)
    {
        var sb = new StringBuilder();
        foreach (var port in p.Ports)
            sb.Append(port.Number).Append(':').Append(port.Conductor).Append(':')
              .Append(port.ReferenceConductor ?? "gnd").Append(':')
              .Append(R(port.Z0.Real)).Append(',').Append(R(port.Z0.Imaginary)).Append('|');
        return Sha(sb.ToString());
    }

    // ── D9: the planar hashes ─────────────────────────────────────────────────────────────────
    //
    // Same rule as kernel A's, applied to the other problem type: hash EXACTLY what the answer
    // depends on. For a planar run that is the conductor artwork in metres, the slab, and the
    // sweep's top frequency (which is what sizes the mesh, R-msh-3) — plus the mesh controls and the
    // ports, each on their own line so a mismatch can still say WHICH of the three moved.

    public static string GeometryHash(PlanarProblem p)
    {
        var sb = new StringBuilder();
        sb.Append("F=").Append(R(p.MaxFrequencyHz)).Append('|');
        sb.Append("S:").Append(R(p.Slab.HeightM)).Append(':')
          .Append(R(p.Slab.Material.EpsR)).Append(':')
          .Append(R(p.Slab.Material.TanD)).Append(':')
          .Append(R(p.Slab.Material.MuR));

        // CL7 — the ONE-SLAB floor's own metal. CL4 closed this on the MediumStack spelling below;
        // a one-slab problem has MediumStack == null and contributed only the four numbers above, so
        // two runs differing only in the GROUND's σ would have shared a cached .snp — silently, and
        // with a plausible answer. §CL6 §7 found it and reserved it for this brief, which is the one
        // that makes the termination reachable. Appended only for a surface impedance, deliberately:
        // writing a kind unconditionally would change the hash of every stack that exists and
        // invalidate every cached .snp in every workspace to record nothing.
        if (p.Slab.Floor.Kind == TerminationKind.SurfaceImpedance)
            sb.Append(":g").Append(R(p.Slab.Floor.ConductivitySm)).Append(',')
              .Append(R(p.Slab.Floor.ThicknessM));
        sb.Append('|');

        // L9d — the general MEDIUM and each level's own z, or the answer changes with nothing the
        // hash can see. Before L9d there was one level on one slab and the slab line above said all
        // of it; a two-level problem that keeps the same artwork and moves a spacer is a different
        // structure, and staleness has to notice.
        if (p.MediumStack is { } ms)
        {
            // CL4 — a CONDUCTING plane's σ and thickness. A half-space's εᵣ used to be the whole of
            // what a termination contributed, and for a PEC or a PMC that was every bit of
            // information there is. It is not any more: two runs differing only in the ground
            // plane's metal produce different s-parameters, and a hash reading Material.EpsR alone
            // sees EmMaterial.Air on both. Same failure CL3 found on the strip's own σ and t — a
            // cached .snp taken with the wrong metal was still the right .snp — one surface over.
            //
            // APPENDED only for a surface-impedance termination, deliberately: writing the KIND
            // unconditionally would change the hash of every stack that exists, invalidating every
            // cached .snp in every workspace to record a capability none of them can reach (§CL4 §9).
            // A PEC and a PMC hash exactly as they did.
            sb.Append("M:").Append(R(ms.Bottom.Material.EpsR)).Append(':')
              .Append(R(ms.Top.Material.EpsR)).Append(':');
            foreach (var (t, which) in new[] { (ms.Bottom, 'b'), (ms.Top, 'u') })
                if (t.Kind == TerminationKind.SurfaceImpedance)
                    sb.Append(which).Append(R(t.ConductivitySm)).Append(',')
                      .Append(R(t.ThicknessM)).Append(':');
            foreach (var ml in ms.Layers)
                sb.Append(R(ml.ThicknessM)).Append(',').Append(R(ml.Material.EpsR)).Append(',')
                  .Append(R(ml.Material.TanD)).Append(',').Append(R(ml.Material.MuR)).Append(';');
            sb.Append('|');
        }

        foreach (var via in p.ViaList)
        {
            sb.Append("V:").Append(via.LowerLayerIndex).Append("->").Append(via.UpperLayerIndex)
              .Append(':').Append(R(via.SigmaSm)).Append(':');
            foreach (var poly in via.Polygons)
                foreach (var pt in poly.Outer) sb.Append(R(pt.X)).Append(',').Append(R(pt.Y)).Append(';');
            sb.Append('|');
        }

        for (int li = 0; li < p.Layers.Count; li++)
        {
            var layer = p.Layers[li];
            sb.Append("L:").Append(layer.Name).Append(':').Append(R(layer.SigmaSm)).Append(':')
              .Append(R(layer.ThicknessM)).Append(':').Append(R(p.LevelZ(li))).Append('|');
            foreach (var poly in layer.Polygons)
            {
                foreach (var pt in poly.Outer) sb.Append(R(pt.X)).Append(',').Append(R(pt.Y)).Append(';');
                sb.Append('/');
                foreach (var hole in poly.HoleRings)
                {
                    foreach (var pt in hole) sb.Append(R(pt.X)).Append(',').Append(R(pt.Y)).Append(';');
                    sb.Append('/');
                }
                sb.Append('|');
            }
        }
        return Sha(sb.ToString());
    }

    /// <summary>
    /// <b>The BOUNDARY-CELL model is in the hash, and leaving it out would have been the exact
    /// staleness failure R-em-20 exists to prevent.</b> A staircased disc and a conformal disc are
    /// different geometry, so an <c>.snp</c> produced with one boundary model is not current for the
    /// other — and without this term the stamp would go on saying it was, silently. One line, and
    /// easy to forget.
    ///
    /// <para><b><see cref="PlanarMeshSettings.MeshFrequencyHz"/> is in it for exactly the same
    /// reason.</b> An <c>.snp</c> produced with the mesh sized at 10 GHz is not current for one
    /// sized at 20 GHz — different cells, different unknowns, different numbers — and the hash is
    /// the only thing that can say so. One more line, equally easy to forget.</para>
    ///
    /// <para><b><see cref="PlanarMeshSettings.MinCellsAcrossConductor"/> too</b> (2026-09-09) — it
    /// sets the transverse pitch directly, so it changes the cells, the unknowns and the answer.
    /// <b>Appended at the END and only when it is off its default</b>, so every <c>.snp</c> stamped
    /// before this control existed keeps the hash it already carries and does not read as stale.</para>
    ///
    /// <para><b><see cref="PlanarMeshSettings.CurrentModel"/> too</b> (2026-09-09 as the
    /// transmission-line boolean, 2026-09-10 as ANT-3's three-way) — it changes both pitches, so it
    /// changes the cells, the unknowns and the answer more than any other mesh control does.
    /// <b>Appended at the END and only when it is off its default</b>, for the same reason.</para>
    ///
    /// <para><b>ANT-3 kept the transmission-line term's exact BYTES — <c>|tline=True</c> — rather
    /// than re-spelling it as the enum.</b> That value's meaning did not change, only its name in
    /// C#, so an <c>.snp</c> stamped under the boolean must go on reading as current; a tidier
    /// <c>|model=TransmissionLine</c> would have marked every one of them stale for no reason a user
    /// could see. The new value gets its own term.</para>
    ///
    /// <para><b><see cref="PlanarMeshSettings.DetailFloorDivisor"/> too</b> (ANT-2, 2026-09-10) — it
    /// decides which drawn geometry is allowed to set the pitch, so it changes the mesh and therefore
    /// the answer. <b>Appended at the END and ALWAYS, which breaks the omit-at-default rule above on
    /// purpose.</b> That rule exists so a <c>.snp</c> stamped before a control existed keeps the hash
    /// it carries, and it is sound for every control whose default reproduces the older behaviour —
    /// which is all of them so far. This one's default is ON. A file stamped before ANT-2 describes a
    /// mesh built with NO detail floor, and on any artwork carrying sub-λ_g/200 detail that is a
    /// different mesh; omitting the divisor at its default would leave such a file reading as current
    /// while describing a result the tool can no longer reproduce, which is precisely what this field
    /// exists to prevent. So every pre-ANT-2 planar <c>.snp</c> reads as stale once, correctly.</para>
    /// </summary>
    public static string MeshHash(PlanarMeshSettings m)
        => Sha($"{m.Auto}|{m.CellsPerWavelength}|{m.EdgeMesh}|{m.EdgeCells}|{m.BoundaryCells}|" +
               $"{(m.MeshFrequencyHz is { } f ? R(f) : "auto")}" +
               (m.MinCellsAcrossConductor == PlanarMeshSettings.DefaultMinCellsAcrossConductor
                    ? "" : $"|across={m.MinCellsAcrossConductor}") +
               (m.CurrentModel == PlanarCurrentModel.TransmissionLine ? "|tline=True" : "")
             + (m.CurrentModel == PlanarCurrentModel.Sheet ? "|sheet=True" : "")
             + $"|detail={m.DetailFloorDivisor}");

    /// <summary>
    /// A port's identity for staleness purposes is its number, its POSITION, its inferred side and
    /// its reference impedance. Position is in because moving a port label to the other end of the
    /// conductor changes the answer completely and changes nothing else the other two hashes see.
    /// </summary>
    public static string PortHash(IReadOnlyList<PlanarPort> ports)
    {
        var sb = new StringBuilder();
        foreach (var p in ports)
        {
            sb.Append(p.Number).Append(':').Append(p.Side).Append(':').Append(p.LayerIndex).Append(':')
              .Append(R(p.Location.X)).Append(',').Append(R(p.Location.Y)).Append(':')
              .Append(R(p.Z0.Real)).Append(',').Append(R(p.Z0.Imaginary)).Append(':')
              .Append(p.Reference);

            // ── THE PORT TYPE IS PART OF THE ANSWER, AND IT IS APPENDED ONLY WHEN IT IS NOT THE
            //    DEFAULT — the same omit-at-default rule the `.cem` itself follows, for a reason
            //    that is specific to a HASH rather than cosmetic.
            //
            // Changing an edge port into an internal delta gap moves the excitation and turns
            // de-embedding off for it, so an `.snp` written under one type is emphatically not
            // current for the other: leaving it out would be exactly the staleness failure R-em-20
            // exists to prevent. But appending it unconditionally would change the hash of every
            // all-edge port set, i.e. of every `.snp` this application has ever written — reporting
            // a one-time false staleness on files nothing has actually invalidated. Appending it
            // only when a port is internal makes the pre-existing case bit-identical and still moves
            // the hash the moment any port's type does.
            if (p.Kind != PlanarPortKind.Edge) sb.Append(":K=").Append(p.Kind);

            sb.Append('|');
        }
        return Sha(sb.ToString());
    }

    /// <summary>Reads a stamp back out of a <c>.snp</c>'s header comments. Null when the file is not
    /// circuitRF-EM-stamped (a hand-written or third-party Touchstone), which is not staleness —
    /// there is simply nothing to compare against.</summary>
    public static EmProvenanceStamp? TryRead(string snpPath)
    {
        if (!File.Exists(snpPath)) return null;
        string? geo = null, mesh = null, ports = null, model = null;
        try
        {
            foreach (var raw in File.ReadLines(snpPath))
            {
                string line = raw.TrimStart();
                if (line.StartsWith('#')) break;          // the option line ends the header
                if (!line.StartsWith('!')) continue;
                line = line[1..].Trim();
                if (line.StartsWith(EmProvenanceStamp.GeometryPrefix, StringComparison.Ordinal))
                    geo = line[EmProvenanceStamp.GeometryPrefix.Length..].Trim();
                else if (line.StartsWith(EmProvenanceStamp.MeshPrefix, StringComparison.Ordinal))
                    mesh = line[EmProvenanceStamp.MeshPrefix.Length..].Trim();
                else if (line.StartsWith(EmProvenanceStamp.PortPrefix, StringComparison.Ordinal))
                    ports = line[EmProvenanceStamp.PortPrefix.Length..].Trim();
                else if (line.StartsWith(EmProvenanceStamp.ModelPrefix, StringComparison.Ordinal))
                    model = line[EmProvenanceStamp.ModelPrefix.Length..].Trim();
            }
        }
        catch (IOException) { return null; }

        return geo is null || mesh is null || ports is null
            ? null
            : new EmProvenanceStamp(geo, mesh, ports, model);
    }

    /// <summary>
    /// Compares an existing <c>.snp</c>'s stamp against what a run would produce NOW. Returns null
    /// when there is nothing to warn about (no file, no stamp, or everything matches).
    /// </summary>
    public static string? DescribeStaleness(
        string snpPath, EmProblem problem, EmMeshSettings mesh)
        => Compare(snpPath, GeometryHash(problem), MeshHash(mesh), PortHash(problem),
                   "the cross-section geometry", "the port reference impedances");

    /// <summary>D9 — the planar counterpart, so a planar run's stamp can go stale at all.</summary>
    public static string? DescribeStaleness(
        string snpPath, PlanarProblem problem, PlanarMeshSettings mesh, IReadOnlyList<PlanarPort> ports)
        => Compare(snpPath, GeometryHash(problem), MeshHash(mesh), PortHash(ports),
                   "the layout geometry", "the ports", PlanarKernel.ModelRevision);

    /// <param name="modelRevision">
    /// <b>The physics the run about to be written solves</b>, or null for a kernel that has no such
    /// token (kernel A). Compared as plain text against the stamp's own, so a file written by a
    /// DIFFERENT model of the same document is stale — which is a thing no hash of an
    /// <c>EmProblem</c> can say. See <see cref="CircuitRF.Engine.Mom.PlanarKernel.ModelRevision"/>.
    /// </param>
    private static string? Compare(
        string snpPath, string geometry, string mesh, string ports,
        string geometryLabel, string portLabel, string? modelRevision = null)
    {
        var stamp = TryRead(snpPath);
        if (stamp is null) return null;

        var changed = new List<string>();
        if (stamp.GeometryHash != geometry) changed.Add(geometryLabel);
        if (stamp.MeshHash     != mesh)     changed.Add("the mesh settings");
        if (stamp.PortHash     != ports)    changed.Add(portLabel);

        // The document is only half the question. A file whose three hashes all match can still hold
        // numbers this build would not produce, because the SOLVER moved — and the conductor-loss
        // series moved it by more than any edit to the document could. An absent token is the
        // pre-token kernel, whose metal was a perfect conductor, so it is a mismatch rather than a
        // free pass.
        bool modelMoved = modelRevision is not null && stamp.ModelRevision != modelRevision;
        if (changed.Count == 0 && !modelMoved) return null;

        string what = changed.Count > 0
            ? $"{string.Join(" and ", changed)} changed since"
            : "the document has not changed, but circuitRF's own EM physics has";

        string model = modelMoved
            ? " The solver that wrote it was " +
              (stamp.ModelRevision is { Length: > 0 } was
                  ? $"'{was}'"
                  : "an earlier one with no model stamp — which is a build whose metal was a PERFECT " +
                    "CONDUCTOR, so its conductor and ground-plane loss are missing entirely") +
              $" and this run is '{modelRevision}'."
            : "";

        return $"'{Path.GetFileName(snpPath)}' was written from a different setup — " +
               $"{what}.{model} Any schematic referencing it was " +
               "using stale s-parameters; this run has just rewritten it.";
    }

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Sha(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
}
