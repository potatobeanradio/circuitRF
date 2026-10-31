// ================================================================
//  DataSourceView.cs  —  the two things a loaded source GAINS before a
//  trace can be resolved against it
//
//  RND-4 (R-rnd4-2). Both were `DataSourceEntryViewModel`'s, and neither
//  is view-model work: each is a pure function of a DataSet, in RfCore
//  terms. They came down because `circuitrf render` was silently WORSE
//  without them, in two ways that both looked like an empty plot:
//
//   • A trace on "SP1.Z" resolved to nothing, because Z is a VIRTUAL
//     cube — a simulated S-parameter run carries S and Z0 and no Z at
//     all, and the conversion happens on first read.
//   • Every DERIVED trace (Max Gain, µ, a stability circle) was dropped
//     as the display opened, because a simulated run has no SNP by
//     design and the network view that stands in for one was built in
//     the view model.
//
//  Both were found by §5.2's per-kind gate, which is exactly what that
//  gate is for: each is a picture that draws, exports cleanly, and is
//  missing a curve.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class DataSourceView
{
    /// <summary>
    /// Adds virtual <c>Z</c> and <c>Y</c> cubes to every NAMED analysis group that carries both
    /// <c>S</c> and a per-port <c>Z0</c> — a simulated S-parameter run has neither cube today
    /// (brief-dd-network-params-and-stability.md §2). Idempotent: a group that already has them is
    /// left alone.
    ///
    /// <para><b>Named groups only</b>, never the default group. A flat/Touchstone-shaped DataSet's
    /// <c>S</c> already lives there AND is exposed as an SNP, so it is offered through the NETWORK
    /// path (matrix-element picker items), not the cube path; materializing <c>Z</c> and <c>Y</c>
    /// there too would offer the same values a second time, as cube items.</para>
    ///
    /// <para>Once materialized these are ORDINARY cubes — every consumer that already understands
    /// <c>SP1.S</c> (the spec parser, TraceExpression, the Table, export, `.cdd` persistence)
    /// understands them with no further change, because <c>DataSet.Contains</c> and the indexer now
    /// genuinely resolve them. That is why nothing in the render path special-cases a Z trace.</para>
    /// </summary>
    public static void MaterializeNetworkParamCubes(DataSet? ds)
    {
        if (ds is null) return;

        foreach (var group in ds.Groups)
        {
            if (group == DataSet.DefaultGroup) continue;

            var cubes = ds.CubesIn(group);
            if (!cubes.TryGetValue(NetworkMetrics.SCubeName,  out var sCube))  continue;
            if (!cubes.TryGetValue(NetworkMetrics.Z0CubeName, out var z0Cube)) continue;
            if (cubes.ContainsKey("Z") || cubes.ContainsKey("Y")) continue;   // don't clobber
            if (sCube.Rank < 3) continue;                                     // not [.., i, j]-shaped

            int nPorts    = sCube.Axes[sCube.Rank - 1].Length;
            var z0PerPort = z0Cube.ComplexValues;
            if (z0PerPort.Length != nPorts) continue;   // not a genuine per-port Z0 for this S cube

            ds.AddToGroup(group, "Z", NetworkMetrics.ConvertSCube(sCube, z0PerPort, MatrixType.Z));
            ds.AddToGroup(group, "Y", NetworkMetrics.ConvertSCube(sCube, z0PerPort, MatrixType.Y));
        }

        MaterializeWspReducedTwoPorts(ds);
    }

    /// <summary>
    /// A binary simulation dataset states each port's reference impedance as a <c>PortZ</c> cube
    /// <c>[sweep…, port]</c> beside its <c>S</c>, a value at every point. Where every point holds the same
    /// impedance per port, this replaces it with the <c>Z0 [port]</c> cube circuitRF's own S results
    /// carry — without which the virtual <c>Z</c>/<c>Y</c> cubes never materialise. A group whose
    /// <c>PortZ</c> varies keeps it, gains no <c>Z0</c>, and is named in the returned notes.
    ///
    /// <para>The uniform <c>PortZ</c> is REMOVED, not kept beside the new <c>Z0</c>: it says nothing the
    /// <c>Z0</c> does not, and exporting the source again would write <c>PortZ[p]</c> twice — once from
    /// the cube, once from the <c>Z0</c> beside the <c>S</c>.</para>
    /// </summary>
    public static IReadOnlyList<string> AddZ0FromPortZ(DataSet ds)
    {
        var notes = new List<string>();
        foreach (var group in ds.Groups.ToList())
        {
            var cubes = ds.CubesIn(group);
            if (cubes.ContainsKey(NetworkMetrics.Z0CubeName)) continue;
            if (!cubes.TryGetValue(NetworkMetrics.SCubeName, out var s) || s.Rank < 3) continue;
            if (!cubes.TryGetValue("PortZ", out var pz) || pz.Rank < 1) continue;
            int n = s.Axes[s.Rank - 1].Length;
            if (pz.Axes[pz.Rank - 1].Name != "port" || pz.Axes[pz.Rank - 1].Length != n) continue;

            Complex[] all = pz.DataKind == DataKind.Complex
                ? pz.ComplexValues
                : [.. pz.RealValues.Select(r => new Complex(r, 0))];
            var z0 = new Complex[n];
            Array.Copy(all, z0, n);
            bool uniform = true;
            for (int k = n; k < all.Length && uniform; k++)
            {
                Complex a = all[k], b = z0[k % n];
                uniform = Complex.Abs(a - b) <= 1e-9 * Math.Max(1.0, Complex.Abs(b));
            }

            string label = group == DataSet.DefaultGroup ? "PortZ" : $"{group}.PortZ";
            if (!uniform) { notes.Add($"{label} varies over the sweep, so no Z0 was added and Z and Y are not offered."); continue; }

            ds.RemoveFromGroup(group, "PortZ");
            ds.AddToGroup(group, NetworkMetrics.Z0CubeName, DataSetBuilder.BuildZ0Cube(z0));
        }
        return notes;
    }

    /// <summary>
    /// The name of the virtual group holding one probe's reduced two-port — the document's
    /// "two-port network reduction" (§4.2) made visible (WSP-4 R-wsp4-8).
    /// </summary>
    public static string ReducedTwoPortGroup(string analysisGroup, string probeLabel)
        => analysisGroup == DataSet.DefaultGroup
            ? $"WSProbe {probeLabel} \u25b8 reduced 2-port"
            : $"{analysisGroup} \u25b8 WSProbe {probeLabel} \u25b8 reduced 2-port";

    /// <summary>
    /// One virtual NETWORK group per WSProbe, holding the reduced two-port <c>[Y]</c> at that probe
    /// (Eq. 44) as <c>Y</c>, <c>Z</c>, <c>S</c> and a per-port <c>Z0</c>.
    ///
    /// <para><b>This is the cheapest new capability in the series</b>, and it is cheap for exactly
    /// the reason the virtual <c>Z</c>/<c>Y</c> cubes above are: once the reduction is an ordinary
    /// network group, µ, µ', K, |Δ|, MAG/MSG and both stability circles apply to it with no code at
    /// all — they are functions of an S matrix and this IS one. The document's own reading of the
    /// reduced two-port (WSP-2 §3) becomes every metric the Data Display already has.</para>
    ///
    /// <para><b>Appended after the analysis groups, never before.</b> <c>FindCubeSpec</c> answers
    /// with the FIRST group carrying an <c>S</c>, and that has to keep being the run's own — a
    /// network view built from a probe's reduced two-port would quietly replace the amplifier's own
    /// S-parameters in every metric that takes one.</para>
    ///
    /// <para>Idempotent, like the pass above: a group that is already here is left alone.</para>
    /// </summary>
    private static void MaterializeWspReducedTwoPorts(DataSet ds)
    {
        foreach (string group in WspSource.GroupsWithProbes(ds).ToList())
        {
            string wspSpec = WspSource.WspCubeSpec(group);
            foreach (var (label, _) in WspSource.Probes(ds, group))
            {
                string reduced = ReducedTwoPortGroup(group, label);
                if (ds.Groups.Contains(reduced)) continue;
                if (!WspSource.TryReducedTwoPort(ds, wspSpec, label, out var yCube, out _)) continue;

                // The reduction is an ADMITTANCE matrix (Eq. 44); S and Z come from it by the
                // ordinary conversions, at the analysis group's own port-1 reference so that a
                // circle read off this group and one read off the run agree about what 50 ohms is.
                var z0 = ReferenceOf(ds, group);
                var z0PerPort = new[] { z0, z0 };
                var sCube = WspSource.ScatteringOf(yCube!, z0);

                ds.AddToGroup(reduced, "Y",  yCube!);
                ds.AddToGroup(reduced, "S",  sCube);
                ds.AddToGroup(reduced, "Z",  NetworkMetrics.ConvertSCube(sCube, z0PerPort, MatrixType.Z));
                ds.AddToGroup(reduced, NetworkMetrics.Z0CubeName,
                              new DataCube([new Axis("port", [1.0, 2.0])], z0PerPort));
            }

            // wsp_ymatrix over EVERY probe, in idx order (Eq. 185) — the one probe set that needs no
            // picker, and the only one materialized eagerly. Every other set is the card's own
            // ordered multi-select and arrives through EnsureWspProbeSetGroup.
            var allLabels = WspSource.Probes(ds, group).Select(p => p.Label).ToList();
            if (allLabels.Count > 0) EnsureWspProbeSetGroup(ds, group, allLabels);
        }
    }

    /// <summary>
    /// The virtual network group holding one of a probe PAIR's two-ports (R-wsp4-6's second half).
    /// The arrow is the orientation and is part of the identity: probe 1 is the generator side of
    /// Fig. 40, so <c>GATE\u2192DRAIN</c> and <c>DRAIN\u2192GATE</c> are two different pairs of
    /// blocks, not one written twice.
    /// </summary>
    public static string PairBlockGroup(
        string analysisGroup, string label1, string label2, WspBlockKind kind)
    {
        string body = $"WSProbe {label1}\u2192{label2} \u25b8 {WspSource.BlockKindName(kind)}";
        return analysisGroup == DataSet.DefaultGroup ? body : $"{analysisGroup} \u25b8 {body}";
    }

    /// <summary>The virtual network group holding <c>wsp_ymatrix</c> over an ordered probe set
    /// (Eq. 185). The set is in the name because it is in the answer — the matrix is indexed in the
    /// set's own order.</summary>
    public static string ProbeSetYGroup(string analysisGroup, IReadOnlyList<string> labels)
    {
        string body = $"WSProbes {string.Join(", ", labels)} \u25b8 [Y]";
        return analysisGroup == DataSet.DefaultGroup ? body : $"{analysisGroup} \u25b8 {body}";
    }

    /// <summary>
    /// Materializes the four block groups of one ordered probe pair, if they are not already there.
    ///
    /// <para><b>On demand rather than eagerly, and that is the whole reason this is a method.</b>
    /// A run with N probes has N(N-1) ordered pairs and four blocks each; materializing them all
    /// would be 4N(N-1) network groups of four cubes, which on the 30-probe matrix WSP-3's NDF work
    /// contemplates is thousands of cubes nobody asked for. The card's own "with probe" picker is
    /// what enables the pair (R-wsp4-6), so picking it is what creates them — and a <c>.cdd</c> that
    /// names one re-creates it by the same call when it loads.</para>
    /// </summary>
    /// <returns>False, with no change, when the pair does not resolve on this source.</returns>
    public static bool EnsureWspPairBlockGroups(DataSet? ds, string analysisGroup, string label1, string label2)
    {
        if (ds is null || label1.Length == 0 || label2.Length == 0 || label1 == label2) return false;

        string wspSpec = WspSource.WspCubeSpec(analysisGroup);
        if (!ds.Contains(wspSpec)) return false;
        var z0 = ReferenceOf(ds, analysisGroup);
        bool any = false;

        foreach (var kind in WspSource.BlockKinds)
        {
            string name = PairBlockGroup(analysisGroup, label1, label2, kind);
            if (ds.Groups.Contains(name)) { any = true; continue; }
            if (!WspSource.TryPairBlock(ds, wspSpec, label1, label2, kind, z0, out var sCube, out _)) continue;
            AddNetworkGroup(ds, name, sCube!, z0, fromScattering: true);
            any = true;
        }
        return any;
    }

    /// <summary>
    /// Materializes <c>wsp_ymatrix</c> over an ordered probe set as a virtual network group, if it
    /// is not already there. On demand for the same reason the pair blocks are: a set is what the
    /// card's ordered multi-select names, and the number of sets is not bounded by anything.
    /// </summary>
    public static bool EnsureWspProbeSetGroup(DataSet? ds, string analysisGroup, IReadOnlyList<string> labels)
    {
        if (ds is null || labels.Count == 0) return false;

        string wspSpec = WspSource.WspCubeSpec(analysisGroup);
        if (!ds.Contains(wspSpec)) return false;

        string name = ProbeSetYGroup(analysisGroup, labels);
        if (ds.Groups.Contains(name)) return true;
        if (!WspSource.TrySetYMatrix(ds, wspSpec, labels, out var yCube, out _)) return false;

        AddNetworkGroup(ds, name, yCube!, ReferenceOf(ds, analysisGroup), fromScattering: false);
        return true;
    }

    /// <summary>
    /// One virtual network group from either an S or a Y cube: <c>S</c>, <c>Y</c>, <c>Z</c> and a
    /// per-port <c>Z0</c>, all at the analysis group's own reference so a circle read off a block
    /// and one read off the run agree about what 50 ohms is.
    ///
    /// <para><b>The cube it was GIVEN is filed unconverted.</b> A library function returns exactly
    /// one of these two matrices — <c>wsp_block_breakout</c> returns S, <c>wsp_ymatrix</c> returns Y
    /// — and round-tripping that one through the other loses the last few digits for nothing. A test
    /// comparing the group against the library call would then fail on the very quantity the group
    /// exists to expose, and the only fix available to it would be a tolerance.</para>
    /// </summary>
    private static void AddNetworkGroup(DataSet ds, string name, DataCube cube, Complex z0, bool fromScattering)
    {
        int nPorts = cube.Axes[^1].Length;
        var z0PerPort = Enumerable.Repeat(z0, nPorts).ToArray();
        var sCube = fromScattering ? cube : WspSource.ScatteringOf(cube, z0);

        ds.AddToGroup(name, "S", sCube);
        ds.AddToGroup(name, "Y", fromScattering
            ? NetworkMetrics.ConvertSCube(sCube, z0PerPort, MatrixType.Y)
            : cube);
        ds.AddToGroup(name, "Z", NetworkMetrics.ConvertSCube(sCube, z0PerPort, MatrixType.Z));
        ds.AddToGroup(name, NetworkMetrics.Z0CubeName,
                      new DataCube([new Axis("port", [.. Enumerable.Range(1, nPorts).Select(k => (double)k)])],
                                   z0PerPort));
    }

    /// <summary>The analysis group's own port-1 reference, else 50 ohms — real, because every
    /// conversion here is defined at a real reference.</summary>
    private static Complex ReferenceOf(DataSet ds, string group)
    {
        var cubes = ds.CubesIn(group);
        if (cubes.TryGetValue(NetworkMetrics.Z0CubeName, out var z0) && z0.BufferLength > 0)
        {
            var v = z0.DataKind == DataKind.Complex ? z0.ComplexValues[0] : new Complex(z0.RealValues[0], 0);
            if (v.Real > 0) return new Complex(v.Real, 0);
        }
        return new Complex(50, 0);
    }

    /// <summary>
    /// An SNP view of a source for NETWORK-METRIC purposes (stability, passivity, MaxGain), built
    /// from a grouped run's own <c>SP1.S</c> cube. Null when the DataSet is not network-shaped, or
    /// when building it throws.
    ///
    /// <para><b>Why this is not simply "the source's SNP", and must stay separate.</b> A simulated
    /// run's S cube deliberately does NOT become the source's SNP
    /// (brief-sparam-run-add-trace): it is offered through the CUBE path, which can carry axes an
    /// SNP structurally cannot — a swept S cube is rank 4. Making it an SNP would break swept
    /// sources and offer S(1,1) twice, once per path. But the 2-port metric formulae need matrices
    /// and only matrices, so they get this narrow view, gated on
    /// <see cref="NetworkMetrics.IsNetworkShaped"/> so a swept cube is refused rather than silently
    /// flattened to one arbitrary slice.</para>
    /// </summary>
    public static SNP? NetworkViewOf(DataSet? ds)
    {
        if (ds is null) return null;
        if (!NetworkMetrics.IsNetworkShaped(ds)) return null;
        if (NetworkMetrics.FindSCubeSpec(ds) is not { } spec) return null;
        try { return DataSetBuilder.ToSnp(ds, spec); }
        catch { return null; }
    }
}
