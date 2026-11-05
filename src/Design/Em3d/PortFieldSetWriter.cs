// brief-sar-9 R-sar9-1 / R-sar9-2 — writes a driven openEMS run's per-port fields beside its .sNp (PortFieldSet's header has the
// layout). Everything is read from what the run left in its port directories — each port's raw SAR dump and, with B1, its H
// dump — so, like OpenEmsSarResults, nothing here starts a process.
//
// TWO PASSES, so no port's field is ever in memory whole. The first transcribes each port's dump, as solved, into its complex64
// file one x slab at a time (and the first port's σ, ρ and V beside it). The second maps every port's file for writing and
// replaces each slab by its normalised value, Ê = F·A⁻¹ (PortFieldSet's header), reading every port's slab at once — O(N) slabs
// in memory, never O(N) volumes. Then the tissue matrix is computed from the files just written, through the reader a
// recombination uses.
//
// ON A CYLINDRICAL GRID (brief-sar-8b) the cells are (ρ, α, z) and openEMS's dumps carry (ρ, α, z) components. Pass 1 turns each
// cell's E and H onto the world's x, y and z at the cell's own azimuth, so every file holds world components, a recombination
// and B1± need no frame, and |E|² — all SAR reads of E — is what it was. The manifest records the grid and its axis for the
// cells' positions. B1's H dump is openEMS's HDF5 there (OpenEmsSarResults.CylindricalHFile).

using System.Numerics;
using System.Runtime.InteropServices;
using CircuitRF.Design.Layout;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Em3d.Sar;
using RfCore;
using RfCore.Hdf5;

namespace CircuitRF.Design.Em3d;

public static class PortFieldSetWriter
{
    /// <summary>
    /// The field set of a finished run, into <paramref name="folder"/> (created; anything there before is replaced). A run hands it
    /// its staging folder (EmResultStaging), so a failure here leaves the previous result's set alone. Returns the
    /// manifest written, or the sentence why not. <paramref name="portNumbers"/> orders the runs and <paramref name="z0"/>, as
    /// FdtdPortTransform.Solve takes them.
    /// </summary>
    public static (PortFieldManifest? Manifest, string? Error) Write(
        string folder, string stamp, string snpFileName, string setupName, Em3dSarPlan plan, Em3dProblem problem, string runDir,
        IReadOnlyList<int> portNumbers, IReadOnlyList<string> portNames, IReadOnlyList<Complex> z0,
        IReadOnlyList<IReadOnlyList<FdtdPortProbes>> runs, IReadOnlyList<(double Hz, int Port, double Efficiency)>? efficiencies = null,
        RunControl? control = null, CancellationToken ct = default, FdtdCylinder? cylinder = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(problem);
        int n = portNumbers.Count;
        var s = plan.Settings;
        var tissue = problem.Solids.Where(x => plan.Densities.ContainsKey(x.Name)).OrderByDescending(x => x.Order).ToList();
        var manifest = new PortFieldManifest
        {
            Stamp = stamp, Snp = snpFileName, Setup = setupName,
            Ports = [.. Enumerable.Range(0, n).Select(k => new PortFieldPort
            {
                Number = portNumbers[k], Name = portNames[k], Z0Re = z0[k].Real, Z0Im = z0[k].Imaginary,
            })],
            Tissues = [.. tissue.Select(t => t.Name)], TissueMaterials = [.. tissue.Select(t => t.Material)],
            Region = [.. plan.Region], RegionName = plan.RegionName, RegionStated = s.Region is not null,
            B1Region = [.. plan.B1Region], B1RegionName = plan.B1RegionName,
            MassesGrams = [.. s.MassesGrams], Method = s.Method.ToString(), B1 = s.B1, B0Axis = s.B0AxisName,
            Grid = cylinder is null ? null : SarRunRecord.CylindricalGrid, CylinderAxis = cylinder is null ? null : cylinder.A,
            CylinderOriginM = cylinder is null ? null : [cylinder.Origin.X, cylinder.Origin.Y, cylinder.Origin.Z],
        };

        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, $"the per-port fields could not be written to '{folder}' ({e.Message}).");
        }

        control?.BeginStage("per-port fields", s.FrequenciesHz.Count * (long)n, "port fields");
        for (int jj = 0; jj < s.FrequenciesHz.Count; jj++)
        {
            ct.ThrowIfCancellationRequested();
            double hz = s.FrequenciesHz[jj];
            int j = jj + 1;
            var at = FdtdPortTransform.Solve(runs, [hz], portNumbers, z0);
            if (at.Error is { } singular) return (null, singular);
            var inverse = IncidentInverse(at.U[0], at.I[0], z0, out string? weak);
            if (inverse is null) return (null, $"the per-port fields at {MaterialDispersion.Hz(hz)} cannot be referred to an incident wave: {weak}");

            string? error;
            PortFieldFrequency freq;
            try { (freq, error) = WriteFrequency(folder, j, hz, plan, tissue, runDir, portNumbers, inverse, control, ct, cylinder); }
            catch (Exception e) when (e is IOException or InvalidDataException or OpenEmsHdf5Exception or ArgumentException or UnauthorizedAccessException)
            {
                return (null, $"the per-port fields at {MaterialDispersion.Hz(hz)} could not be written ({e.Message}).");
            }
            if (error is not null) return (null, error);

            var sm = new Complex[n, n];
            for (int i = 0; i < n; i++) for (int k = 0; k < n; k++) sm[i, k] = at.S[0][i, k];
            freq.S = PortFieldSet.Pack(sm);
            if (efficiencies is { Count: > 0 })
                freq.RadiationEfficiency = [.. portNumbers.Select(p => Efficiency(efficiencies, p, hz))];
            using (var set = PortFieldSet.OpenUnchecked(folder, manifest, freq))
                freq.TissueMatrix = PortFieldSet.Pack(FieldRecombination.TissueMatrix(set));
            manifest.Frequencies.Add(freq);
        }
        PortFieldSet.WriteManifest(folder, manifest);
        return (manifest, null);
    }

    /// <summary>The disk a field set takes, for the run's estimate before it starts (overview §7).</summary>
    public static long EstimateBytes(Em3dSarPlan plan, long cells, int ports)
        => plan.Settings.PerPortFields
            ? plan.Settings.FrequenciesHz.Count * (cells * PortFieldSet.SharedBytesPerCell +
                                                   (long)ports * cells * PortFieldSet.FieldBytesPerCell * (plan.Settings.B1 ? 2 : 1))
            : 0;

    // ── one SAR frequency ───────────────────────────────────────────────────────────────────────────

    private static (PortFieldFrequency, string?) WriteFrequency(string folder, int j, double hz, Em3dSarPlan plan, List<Em3dSolid> tissue,
                                                                string runDir, IReadOnlyList<int> portNumbers, Complex[,] inverse,
                                                                RunControl? control, CancellationToken ct, FdtdCylinder? cylinder)
    {
        int n = portNumbers.Count;
        var freq = new PortFieldFrequency { Index = j, Hz = hz };
        string Raw(int k) => Path.Combine(runDir, OpenEmsRun.PortDirectory(portNumbers[k]), Em3dSar.RawDumpFile(j - 1));
        for (int k = 0; k < n; k++)
            if (!File.Exists(Raw(k)))
                return (freq, $"port {portNumbers[k]}'s run wrote no SAR dump at {MaterialDispersion.Hz(hz)} ('{Raw(k)}'), so its field cannot be kept.");

        var layout = OpenEmsHdf5.ReadSarRawLayout(Raw(0));
        var mesh = layout.Mesh;
        var ax = SarAxis.FromCentres(mesh.X, layout.CellWidthX);
        var ay = SarAxis.FromCentres(mesh.Y, layout.CellWidthY);
        var az = SarAxis.FromCentres(mesh.Z, layout.CellWidthZ);
        freq.Shape = [ax.Count, ay.Count, az.Count];
        long cells = (long)ax.Count * ay.Count * az.Count;
        SarVolumeFiles.WriteFloat64(Path.Combine(folder, PortFieldSet.FacesFile(j, 'x')), ax.Faces);
        SarVolumeFiles.WriteFloat64(Path.Combine(folder, PortFieldSet.FacesFile(j, 'y')), ay.Faces);
        SarVolumeFiles.WriteFloat64(Path.Combine(folder, PortFieldSet.FacesFile(j, 'z')), az.Faces);

        // ── each cell's tissue: the run's own rule (OpenEmsSarResults.Owners) ───────────────────────────
        var grid = OpenEmsSarGrid.Read(Raw(0));
        int[] owner = OpenEmsSarResults.Owners(grid, tissue, plan.Densities, cylinder);
        var owners = new short[owner.Length];
        for (int c = 0; c < owner.Length; c++) owners[c] = (short)owner[c];
        WriteWhole(Path.Combine(folder, PortFieldSet.CellFile(j, "owner")), "<i2", MemoryMarshal.AsBytes(owners.AsSpan()), freq.Shape);

        // ── pass 1: each port's dump as solved, and the first port's σ, ρ, V ─────────────────────────────
        int plane = ay.Count * az.Count;
        int step = Math.Clamp(OpenEmsSarGrid.SlabCells / Math.Max(1, plane), 1, ax.Count);
        long[] fieldShape = [ax.Count, ay.Count, az.Count, 3];
        bool hasH = plan.Settings.B1;
        for (int k = 0; k < n; k++)
        {
            ct.ThrowIfCancellationRequested();
            using var e = Begin(Path.Combine(folder, PortFieldSet.FieldFile(portNumbers[k], j, 'E')), "<c8", fieldShape);
            using var sigma = k == 0 ? Begin(Path.Combine(folder, PortFieldSet.CellFile(j, "sigma")), "<f4", freq.Shape.Select(v => (long)v).ToArray()) : null;
            using var rho = k == 0 ? Begin(Path.Combine(folder, PortFieldSet.CellFile(j, "rho")), "<f4", freq.Shape.Select(v => (long)v).ToArray()) : null;
            using var vol = k == 0 ? Begin(Path.Combine(folder, PortFieldSet.CellFile(j, "volume")), "<f4", freq.Shape.Select(v => (long)v).ToArray()) : null;
            for (int x = 0; x < ax.Count; x += step)
            {
                int count = Math.Min(step, ax.Count - x);
                var slab = OpenEmsHdf5.ReadSarRaw(Raw(k), 0, x, count);
                if (slab.Ex.Length != count * plane)
                    return (freq, $"port {portNumbers[k]}'s SAR dump at {MaterialDispersion.Hz(hz)} is not on port {portNumbers[0]}'s cells.");
                var buf = new float[6 * slab.Ex.Length];
                for (int c = 0; c < slab.Ex.Length; c++)
                {
                    if (cylinder is not null)
                    {
                        // (Eρ, Eα, Ez) turned onto the world's axes at the cell's azimuth (the file header)
                        double alpha = ay.Centre(c / az.Count % ay.Count);
                        var (wx, wy, wz) = cylinder.WorldComponents(slab.Ex[c].ToComplex(), slab.Ey[c].ToComplex(), slab.Ez[c].ToComplex(), alpha);
                        buf[6 * c] = (float)wx.Real; buf[6 * c + 1] = (float)wx.Imaginary;
                        buf[6 * c + 2] = (float)wy.Real; buf[6 * c + 3] = (float)wy.Imaginary;
                        buf[6 * c + 4] = (float)wz.Real; buf[6 * c + 5] = (float)wz.Imaginary;
                        continue;
                    }
                    buf[6 * c] = slab.Ex[c].Re; buf[6 * c + 1] = slab.Ex[c].Im;
                    buf[6 * c + 2] = slab.Ey[c].Re; buf[6 * c + 3] = slab.Ey[c].Im;
                    buf[6 * c + 4] = slab.Ez[c].Re; buf[6 * c + 5] = slab.Ez[c].Im;
                }
                e.Write(MemoryMarshal.AsBytes(buf.AsSpan()));
                sigma?.Write(MemoryMarshal.AsBytes(slab.Conductivity.AsSpan()));
                rho?.Write(MemoryMarshal.AsBytes(slab.Density.AsSpan()));
                vol?.Write(MemoryMarshal.AsBytes(slab.Volume.AsSpan()));
            }
            if (hasH && !(cylinder is null ? WriteH(folder, j, portNumbers[k], runDir, ax, ay, az, fieldShape)
                                           : WriteCylindricalH(folder, j, portNumbers[k], runDir, ax, ay, az, fieldShape, cylinder)))
                hasH = false;
            control?.TickStage();
        }
        if (!hasH && plan.Settings.B1)
            foreach (int p in portNumbers)
            {
                string h = Path.Combine(folder, PortFieldSet.FieldFile(p, j, 'H'));
                if (File.Exists(h)) File.Delete(h);
            }
        freq.HasH = hasH;

        // ── pass 2: Ê = F·A⁻¹, in place ─────────────────────────────────────────────────────────────
        Normalise(folder, j, portNumbers, 'E', cells, inverse);
        if (hasH) Normalise(folder, j, portNumbers, 'H', cells, inverse);

        foreach (string file in Directory.EnumerateFiles(folder, $"*f{j}.*.npy"))
        {
            string name = Path.GetFileName(file);
            int port = name.StartsWith('p') && int.TryParse(name[1..name.IndexOf('.')], out int pn) ? pn : 0;
            freq.Files.Add(new PortFieldFile { Name = name, Bytes = new FileInfo(file).Length, Port = port });
        }
        freq.Files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return (freq, null);
    }

    /// <summary>Port <paramref name="port"/>'s H dump (openEMS's VTK, cell centres, the raw dump's box) on the field set's cells,
    /// as solved; false when its run wrote none or it is not on those cells.</summary>
    private static bool WriteH(string folder, int j, int port, string runDir, SarAxis ax, SarAxis ay, SarAxis az, long[] shape)
    {
        string dir = Path.Combine(runDir, OpenEmsRun.PortDirectory(port));
        string? abs = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, $"{Em3dSar.HDump(j - 1)}_f=*_abs.vtr").FirstOrDefault() : null;
        if (abs is null) return false;
        string arg = abs[..^"_abs.vtr".Length] + "_arg.vtr";
        if (!File.Exists(arg)) return false;
        var m = VtrFile.Read(abs);
        var a = VtrFile.Read(arg);
        if (m.Components != 3 || a.Values.Length != m.Values.Length) return false;
        if (OpenEmsSarResults.Map(ax, m.X) is not { } mx || OpenEmsSarResults.Map(ay, m.Y) is not { } my ||
            OpenEmsSarResults.Map(az, m.Z) is not { } mz) return false;
        int hnx = m.X.Length, hny = m.Y.Length;
        using var h = Begin(Path.Combine(folder, PortFieldSet.FieldFile(port, j, 'H')), "<c8", shape);
        var row = new float[6 * az.Count];
        for (int i = 0; i < ax.Count; i++)
            for (int jj = 0; jj < ay.Count; jj++)
            {
                for (int k = 0; k < az.Count; k++)
                {
                    int node = (mz[k] * hny + my[jj]) * hnx + mx[i];
                    for (int comp = 0; comp < 3; comp++)
                    {
                        var v = Complex.FromPolarCoordinates(m.Values[3 * node + comp], a.Values[3 * node + comp]);
                        row[6 * k + 2 * comp] = (float)v.Real;
                        row[6 * k + 2 * comp + 1] = (float)v.Imaginary;
                    }
                }
                h.Write(MemoryMarshal.AsBytes(row.AsSpan()));
            }
        return true;
    }

    /// <summary>brief-sar-8b — port <paramref name="port"/>'s HDF5 H dump on a cylindrical grid, its (Hρ, Hα, Hz) turned onto the world's
    /// axes at each cell's azimuth; false when its run wrote none or it is not on the field set's cells.</summary>
    private static bool WriteCylindricalH(string folder, int j, int port, string runDir, SarAxis ax, SarAxis ay, SarAxis az, long[] shape,
                                          FdtdCylinder cylinder)
    {
        string file = Path.Combine(runDir, OpenEmsRun.PortDirectory(port), OpenEmsSarResults.CylindricalHFile(j - 1));
        if (!File.Exists(file)) return false;
        var h = OpenEmsHdf5.ReadField(file);
        if (!h.Mesh.Cylindrical || OpenEmsSarResults.Map(ax, h.Mesh.X) is not { } mx || OpenEmsSarResults.Map(ay, h.Mesh.Y) is not { } my ||
            OpenEmsSarResults.Map(az, h.Mesh.Z) is not { } mz) return false;
        int hny = h.Mesh.Ny, hnz = h.Mesh.Nz;
        using var fs = Begin(Path.Combine(folder, PortFieldSet.FieldFile(port, j, 'H')), "<c8", shape);
        var row = new float[6 * az.Count];
        for (int i = 0; i < ax.Count; i++)
            for (int jj = 0; jj < ay.Count; jj++)
            {
                double alpha = ay.Centre(jj);
                for (int k = 0; k < az.Count; k++)
                {
                    int node = (mx[i] * hny + my[jj]) * hnz + mz[k];
                    var (wx, wy, wz) = cylinder.WorldComponents(h.X[node].ToComplex(), h.Y[node].ToComplex(), h.Z[node].ToComplex(), alpha);
                    row[6 * k] = (float)wx.Real; row[6 * k + 1] = (float)wx.Imaginary;
                    row[6 * k + 2] = (float)wy.Real; row[6 * k + 3] = (float)wy.Imaginary;
                    row[6 * k + 4] = (float)wz.Real; row[6 * k + 5] = (float)wz.Imaginary;
                }
                fs.Write(MemoryMarshal.AsBytes(row.AsSpan()));
            }
        return true;
    }

    /// <summary>Pass 2: every port's file of <paramref name="quantity"/> replaced, slab by slab, by Σ_k F_k·A⁻¹[k, m].</summary>
    private static void Normalise(string folder, int j, IReadOnlyList<int> portNumbers, char quantity, long cells, Complex[,] inverse)
    {
        int n = portNumbers.Count;
        var files = new PortFieldSet.Mapped[n];
        try
        {
            for (int k = 0; k < n; k++)
                files[k] = PortFieldSet.Mapped.Open(Path.Combine(folder, PortFieldSet.FieldFile(portNumbers[k], j, quantity)), "<c8", cells * 3, writable: true);
            int slab = (int)Math.Clamp(OpenEmsSarGrid.SlabCells / Math.Max(1, n), 1, cells);
            var input = new float[n][];
            var output = new float[n][];
            for (int k = 0; k < n; k++) { input[k] = new float[6 * slab]; output[k] = new float[6 * slab]; }
            for (long start = 0; start < cells; start += slab)
            {
                int count = (int)Math.Min(slab, cells - start);
                int floats = 6 * count;
                for (int k = 0; k < n; k++) files[k].Read(start * PortFieldSet.FieldBytesPerCell, MemoryMarshal.AsBytes(input[k].AsSpan(0, floats)));
                for (int m = 0; m < n; m++)
                {
                    var o = output[m];
                    for (int f = 0; f < floats; f += 2)
                    {
                        double re = 0, im = 0;
                        for (int k = 0; k < n; k++)
                        {
                            Complex w = inverse[k, m];
                            double fr = input[k][f], fi = input[k][f + 1];
                            re += fr * w.Real - fi * w.Imaginary;
                            im += fr * w.Imaginary + fi * w.Real;
                        }
                        o[f] = (float)re;
                        o[f + 1] = (float)im;
                    }
                }
                for (int m = 0; m < n; m++) files[m].Write(start * PortFieldSet.FieldBytesPerCell, MemoryMarshal.AsBytes(output[m].AsSpan(0, floats)));
            }
        }
        finally
        {
            foreach (var f in files) f?.Dispose();
        }
    }

    /// <summary>
    /// A⁻¹, A_ik = (U_ik + Z0_i·I_ik)/(√2·√Z0_i) — port i's incident power wave in run k (PortFieldSet's header); null, with the
    /// port whose wave is not independent, when A is singular.
    /// </summary>
    internal static Complex[,]? IncidentInverse(Complex[,] u, Complex[,] current, IReadOnlyList<Complex> z0, out string? weak)
    {
        int n = z0.Count;
        var a = new Complex[n, 2 * n];
        for (int i = 0; i < n; i++)
        {
            Complex scale = Math.Sqrt(2) * RFNetwork.ComplexSqrt(z0[i]);
            for (int k = 0; k < n; k++) a[i, k] = (u[i, k] + z0[i] * current[i, k]) / scale;
            a[i, n + i] = Complex.One;
        }
        double largest = 0;
        for (int i = 0; i < n; i++) for (int k = 0; k < n; k++) largest = Math.Max(largest, a[i, k].Magnitude);
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++) if (a[r, c].Magnitude > a[p, c].Magnitude) p = r;
            if (!(a[p, c].Magnitude > FdtdPortTransform.SingularRatio * largest))
            {
                weak = "a port has no incident wave in any run.";
                return null;
            }
            if (p != c) for (int k = 0; k < 2 * n; k++) (a[p, k], a[c, k]) = (a[c, k], a[p, k]);
            Complex d = a[c, c];
            for (int k = 0; k < 2 * n; k++) a[c, k] /= d;
            for (int r = 0; r < n; r++)
            {
                if (r == c || a[r, c] == Complex.Zero) continue;
                Complex f = a[r, c];
                for (int k = 0; k < 2 * n; k++) a[r, k] -= f * a[c, k];
            }
        }
        weak = null;
        var inv = new Complex[n, n];
        for (int i = 0; i < n; i++) for (int k = 0; k < n; k++) inv[i, k] = a[i, n + k];
        return inv;
    }

    private static double? Efficiency(IReadOnlyList<(double Hz, int Port, double Efficiency)> table, int port, double hz)
    {
        var pts = table.Where(t => t.Port == port && double.IsFinite(t.Efficiency)).OrderBy(t => t.Hz).ToList();
        for (int i = 0; i < pts.Count; i++)
        {
            if (Math.Abs(pts[i].Hz - hz) <= 1e-9 * hz) return pts[i].Efficiency;
            if (i + 1 < pts.Count && pts[i].Hz < hz && hz < pts[i + 1].Hz)
                return pts[i].Efficiency + (pts[i + 1].Efficiency - pts[i].Efficiency) * (hz - pts[i].Hz) / (pts[i + 1].Hz - pts[i].Hz);
        }
        return null;
    }

    private static FileStream Begin(string path, string descr, long[] shape)
    {
        var fs = File.Create(path);
        fs.Write(PortFieldSet.NpyHeader(descr, shape));
        return fs;
    }

    private static void WriteWhole(string path, string descr, ReadOnlySpan<byte> data, int[] shape)
    {
        using var fs = Begin(path, descr, [.. shape.Select(v => (long)v)]);
        fs.Write(data);
    }
}
