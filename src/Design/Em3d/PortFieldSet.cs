// brief-sar-9 R-sar9-1 / R-sar9-2 / R-sar9-5 — the per-port fields of a driven openEMS run, kept beside its .sNp: what a
// schematic's `fields` analysis recombines into the field of any circuit around the block, with no new EM run.
// docs/design/sar.md §11.
//
//   <stem>.fields/fields.json          PortFieldManifest: the ports and their Z0, each SAR frequency's S, tissue matrix and files,
//                                      the tissue names and regions, and the stamp the .sNp carries too
//   <stem>.fields/f<j>.{x,y,z}faces.npy the cells' faces along each axis, float64, metres (n + 1 values)
//   <stem>.fields/f<j>.{sigma,rho,volume}.npy  σ, ρ and V per cell, float32, C order over (x, y, z)
//   <stem>.fields/f<j>.owner.npy       each cell's tissue, int16: an index into the manifest's Tissues, −1 background
//   <stem>.fields/p<n>.f<j>.E.npy      port n's E, complex64, shape (nx, ny, nz, 3), V/m per √W incident
//   <stem>.fields/p<n>.f<j>.H.npy      port n's H, the same, A/m per √W incident (with B1 only)
//
// <stem> is the .sNp's own path without its extension, so the folder sits beside the file and is named after it.
//
// PER UNIT INCIDENT WAVE (R-sar9-2). Run k's dump F_k is the field of every port's incident wave in that run: port k's from its
// source, and whatever the other ports' terminations reflect back — round-off when they are matched, which openEMS's port
// resistors are. With A the incident-wave matrix (A_ik = port i's incident wave in run k, as power waves: |a|² is the incident
// power in watts), F = Ê·A, so Ê = F·A⁻¹ — the same matrix FdtdPortTransform inverts to form S, so the fields and the S-matrix
// share one definition of a wave. The power wave of port i is a = (U + Z0·I)/(√2·√Z0) in circuitRF's DFT of the probes:
// openEMS's dumps carry a ×2 against that DFT (SarVolumeFiles' header), and with it P_inc = |a|² exactly.
//
// STALE (R-sar9-5). The manifest and the .sNp's provenance both carry the run's stamp, written in that order, so the .sNp is
// never older than its fields. A set whose stamp is not the .sNp's belongs to another run and is refused naming every port; a
// port whose file is missing or the wrong size is refused naming that port. File times are not compared: a git checkout or a
// copy sets them arbitrarily, and a committed field set would read as stale on every fresh clone.
//
// READ MEMORY-MAPPED. A whole body at 2 mm is ~1.6 GB per port, so a field file is never read whole: each read maps the file
// and copies one run of cells, through a view stream (no unsafe code in this assembly).

using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Em3d.Sar;
using RfCore;
using RfCore.Hdf5;

namespace CircuitRF.Design.Em3d;

/// <summary>brief-sar-9 — one port of a field set: its result number, its name in the document, and its reference impedance.</summary>
public sealed class PortFieldPort
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public double Z0Re { get; set; }
    public double Z0Im { get; set; }

    public Complex Z0 => new(Z0Re, Z0Im);
}

/// <summary>brief-sar-9 — one file of a field set, by name, with its size (a truncated copy is refused) and its port (0: shared).</summary>
public sealed class PortFieldFile
{
    public string Name { get; set; } = "";
    public long Bytes { get; set; }
    public int Port { get; set; }
}

/// <summary>brief-sar-9 — one SAR frequency of a field set: its cells, the S-matrix and tissue matrix there, and its files.</summary>
public sealed class PortFieldFrequency
{
    /// <summary>The SAR frequency's index in the setup, 1-based (the <c>j</c> of <c>f&lt;j&gt;</c>).</summary>
    public int Index { get; set; }
    public double Hz { get; set; }
    public int[] Shape { get; set; } = [];
    public bool HasH { get; set; }

    /// <summary>S at this frequency, from the same probes and transform as the .sNp: rows of interleaved (re, im).</summary>
    public List<double[]> S { get; set; } = [];

    /// <summary>The tissue matrix Q (FieldRecombination.TissueMatrix), W per (√W)²: rows of interleaved (re, im).</summary>
    public List<double[]> TissueMatrix { get; set; } = [];

    /// <summary>Each port's radiated over accepted power here, when the setup asked for a radiation pattern; null otherwise.</summary>
    public List<double?>? RadiationEfficiency { get; set; }

    public List<PortFieldFile> Files { get; set; } = [];

    public Complex[,] SMatrix() => PortFieldSet.Unpack(S);
    public Complex[,] QMatrix() => PortFieldSet.Unpack(TissueMatrix);
}

/// <summary>brief-sar-9 — <c>fields.json</c>: what a field set holds and which run wrote it.</summary>
public sealed class PortFieldManifest
{
    public int FormatVersion { get; set; } = 1;

    /// <summary>The run's stamp, which the .sNp's provenance carries too (R-sar9-5).</summary>
    public string Stamp { get; set; } = "";

    /// <summary>The .sNp the set belongs to, by file name.</summary>
    public string Snp { get; set; } = "";

    public string Setup { get; set; } = "";
    public List<PortFieldPort> Ports { get; set; } = [];
    public List<PortFieldFrequency> Frequencies { get; set; } = [];

    /// <summary>The tissue solids, indexed by the owner file; and each one's material.</summary>
    public List<string> Tissues { get; set; } = [];
    public List<string> TissueMaterials { get; set; } = [];

    /// <summary>The SAR group the run used, so a recombination reports what the run would have.</summary>
    public List<string> Region { get; set; } = [];
    public string RegionName { get; set; } = "";
    public List<string> B1Region { get; set; } = [];
    public string B1RegionName { get; set; } = "";
    public List<double> MassesGrams { get; set; } = [];
    public string Method { get; set; } = nameof(SarMethod.Cube);
    public bool B1 { get; set; }
    public string B0Axis { get; set; } = "+z";
    /// <summary>Whether Region was stated; when not, every tissue is the region.</summary>
    public bool RegionStated { get; set; }

    /// <summary>brief-sar-8b — <c>cylindrical</c> when the cells are a cylindrical grid's (ρ, α, z), with the axis
    /// (<see cref="CylinderAxis"/>, 0 x, 1 y, 2 z) and a point on it (<see cref="CylinderOriginM"/>, metres). The E and H files
    /// hold the WORLD's x, y and z components even then — the writer turned them at each cell's azimuth — so a recombination
    /// needs no frame; only a cell's position does. Null: Cartesian, every set before it.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Grid { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? CylinderAxis { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double[]? CylinderOriginM { get; set; }

    /// <summary>The cells are a cylindrical grid's.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCylindrical => Grid == SarRunRecord.CylindricalGrid;

    /// <summary>A cell centre (ρ, α, z) — or (x, y, z) on a Cartesian set — in the world, metres.</summary>
    public CircuitRF.Engine.Em3d.Point3 World(double x, double y, double z)
    {
        if (!IsCylindrical || CylinderAxis is not int a || CylinderOriginM is not [var ox, var oy, var oz]) return new(x, y, z);
        var c = new double[3];
        double[] o = [ox, oy, oz];
        int u = (a + 1) % 3, v = (a + 2) % 3;
        c[u] = o[u] + x * Math.Cos(y);
        c[v] = o[v] + x * Math.Sin(y);
        c[a] = z;
        return new(c[0], c[1], c[2]);
    }

    public long TotalBytes => Frequencies.Sum(f => f.Files.Sum(x => x.Bytes));
}

/// <summary>
/// brief-sar-9 — one SAR frequency of a field set, opened for reading: an <see cref="IPortFieldSet"/> over the memory-mapped
/// files, with the manifest and the cells' tissue. Dispose it to unmap.
/// </summary>
public sealed class PortFieldSet : IPortFieldSet, IDisposable
{
    /// <summary>The manifest's name in the folder.</summary>
    public const string ManifestFile = "fields.json";

    /// <summary>The provenance line the .sNp carries: <c>circuitRF-EM 3D port fields: '&lt;folder&gt;' stamp &lt;stamp&gt;</c>.</summary>
    public const string ProvenancePrefix = "circuitRF-EM 3D port fields:";

    /// <summary>Bytes one port's E or H takes per cell: three complex64 components.</summary>
    public const int FieldBytesPerCell = 24;

    /// <summary>Bytes the shared arrays take per cell and frequency: σ, ρ, V (float32) and the owner (int16).</summary>
    public const int SharedBytesPerCell = 14;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public PortFieldManifest Manifest { get; }
    public PortFieldFrequency Frequency { get; }
    public string Folder { get; }
    public SarAxis X { get; }
    public SarAxis Y { get; }
    public SarAxis Z { get; }
    public int PortCount => Manifest.Ports.Count;
    public bool HasH => Frequency.HasH;
    public long CellCount => (long)X.Count * Y.Count * Z.Count;

    private readonly Mapped[] _e, _h;
    private readonly Mapped _sigma, _rho, _volume;

    private PortFieldSet(PortFieldManifest manifest, PortFieldFrequency frequency, string folder, SarAxis x, SarAxis y, SarAxis z,
                         Mapped[] e, Mapped[] h, Mapped sigma, Mapped rho, Mapped volume)
    {
        Manifest = manifest; Frequency = frequency; Folder = folder; X = x; Y = y; Z = z;
        _e = e; _h = h; _sigma = sigma; _rho = rho; _volume = volume;
    }

    // ── names ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The folder a .sNp's fields live in: its path without the extension, plus <c>.fields</c>.</summary>
    public static string FolderFor(string snpPath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(snpPath)) ?? "", Path.GetFileNameWithoutExtension(snpPath) + ".fields");

    /// <summary>The folder beside a .sNp written from <paramref name="snpBase"/> (the path before <c>.s&lt;N&gt;p</c>).</summary>
    public static string FolderForBase(string snpBase) => snpBase + ".fields";

    public static string FieldFile(int port, int j, char quantity) => $"p{port}.f{j}.{quantity}.npy";
    public static string FacesFile(int j, char axis) => $"f{j}.{axis}faces.npy";
    public static string CellFile(int j, string what) => $"f{j}.{what}.npy";

    /// <summary>A new run's stamp: when it was written and eight random hex digits.</summary>
    public static string NewStamp()
        => DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" +
           Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    /// <summary>The .sNp's provenance line for a set in <paramref name="folder"/> with <paramref name="stamp"/>.</summary>
    public static string ProvenanceLine(string folder, string stamp) => $"{ProvenancePrefix} '{Path.GetFileName(folder)}' stamp {stamp}";

    /// <summary>The line for a set in <paramref name="folder"/> and the Touchstone at <paramref name="snpPath"/>, wherever each one is:
    /// a Touchstone a run moved with <c>-o</c> names the set by a relative path. Goes with the line in CP-2.</summary>
    public static string ProvenanceLine(string folder, string stamp, string snpPath)
        => $"{ProvenancePrefix} '{Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(snpPath))!, Path.GetFullPath(folder)).Replace('\\', '/')}' stamp {stamp}";

    private static readonly Regex Provenance = new(@"port fields:\s*'([^']+)'\s+stamp\s+(\S+)", RegexOptions.CultureInvariant);

    /// <summary>The folder (beside the .sNp) and stamp a .sNp's provenance names; null when it names none.</summary>
    public static (string Folder, string Stamp)? ReadProvenance(string snpPath)
    {
        if (!File.Exists(snpPath)) return null;
        foreach (string raw in File.ReadLines(snpPath))
        {
            string line = raw.TrimStart();
            if (line.StartsWith('#')) break;
            if (!line.StartsWith('!') || !line.Contains(ProvenancePrefix, StringComparison.Ordinal)) continue;
            if (Provenance.Match(line) is { Success: true } m)
                return (Path.Combine(Path.GetDirectoryName(Path.GetFullPath(snpPath)) ?? "", m.Groups[1].Value), m.Groups[2].Value);
        }
        return null;
    }

    public static PortFieldManifest? ReadManifest(string folder)
    {
        string path = Path.Combine(folder, ManifestFile);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<PortFieldManifest>(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static void WriteManifest(string folder, PortFieldManifest manifest)
        => File.WriteAllText(Path.Combine(folder, ManifestFile), JsonSerializer.Serialize(manifest, Json));

    // ── opening, with the refusals R-sar9-5 asks for ──────────────────────────────────────────────────

    /// <summary>
    /// The field set of <paramref name="snpPath"/> at <paramref name="frequencyHz"/>, or the sentence refusing it: the .sNp names
    /// no fields, the folder or manifest is missing, the set belongs to another run (stale), the frequency is not one the run kept,
    /// or a port's file is missing or truncated — each naming what is wrong and the port.
    /// </summary>
    public static (PortFieldSet? Set, string? Refusal) Open(string snpPath, double frequencyHz)
    {
        string name = Path.GetFileName(snpPath);
        if (!File.Exists(snpPath)) return (null, $"'{snpPath}' does not exist.");
        if (ReadProvenance(snpPath) is not { } prov)
            return (null, $"'{name}' carries no per-port fields: it was written by a run without the setup's Sar.PerPortFields. Turn it on and " +
                          "run the EM setup again.");
        string folder = prov.Folder;
        if (ReadManifest(folder) is not { } manifest)
            return (null, $"'{name}' names its per-port fields in '{Path.GetFileName(folder)}', and {(Directory.Exists(folder) ? $"its {ManifestFile} is missing or unreadable" : "that folder is missing")}: " +
                          "the fields must be kept beside the .sNp they belong to. Run the EM setup again.");
        if (!string.Equals(manifest.Stamp, prov.Stamp, StringComparison.Ordinal))
            return (null, $"The per-port fields in '{Path.GetFileName(folder)}' are stale: they are from another run than '{name}' " +
                          $"(stamp {manifest.Stamp}, the .sNp's {prov.Stamp}), so port{(manifest.Ports.Count == 1 ? "" : "s")} " +
                          $"{string.Join(", ", manifest.Ports.Select(p => p.Number))} would be recombined with the wrong S-matrix. Run the EM setup again.");
        var freq = manifest.Frequencies.FirstOrDefault(f => Math.Abs(f.Hz - frequencyHz) <= 1e-9 * Math.Max(f.Hz, frequencyHz));
        if (freq is null)
            return (null, $"'{name}' keeps per-port fields at {string.Join(", ", manifest.Frequencies.Select(f => MaterialDispersion.Hz(f.Hz)))} only, " +
                          $"not at {MaterialDispersion.Hz(frequencyHz)}: fields are kept at the setup's SAR frequencies (Sar.FrequenciesGHz).");
        foreach (var f in freq.Files)
        {
            string path = Path.Combine(folder, f.Name);
            string who = f.Port > 0 ? $"Port {f.Port}'s" : "The shared";
            if (!File.Exists(path))
                return (null, $"{who} field file '{f.Name}' at {MaterialDispersion.Hz(freq.Hz)} is missing from '{Path.GetFileName(folder)}'. Run the EM setup again.");
            long size = new FileInfo(path).Length;
            if (size != f.Bytes)
                return (null, $"{who} field file '{f.Name}' at {MaterialDispersion.Hz(freq.Hz)} is {size:N0} bytes where its run wrote {f.Bytes:N0}: it is " +
                              "damaged or from another run. Run the EM setup again.");
        }
        try { return (OpenUnchecked(folder, manifest, freq), null); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (null, $"The per-port fields in '{Path.GetFileName(folder)}' could not be read: {e.Message}");
        }
    }

    /// <summary>Opens frequency <paramref name="freq"/> of the set in <paramref name="folder"/> without the stamp's check: the writer's
    /// own read-back, and a test's.</summary>
    internal static PortFieldSet OpenUnchecked(string folder, PortFieldManifest manifest, PortFieldFrequency freq)
    {
        int j = freq.Index;
        SarAxis Axis(char a)
        {
            double[] faces = SarVolumeFiles.ReadFloat64(Path.Combine(folder, FacesFile(j, a)));
            var w = new double[faces.Length - 1];
            for (int i = 0; i < w.Length; i++) w[i] = faces[i + 1] - faces[i];
            return new SarAxis(faces[0], w);
        }
        var x = Axis('x');
        var y = Axis('y');
        var z = Axis('z');
        long cells = (long)x.Count * y.Count * z.Count;
        var e = new Mapped[manifest.Ports.Count];
        var h = new Mapped[manifest.Ports.Count];
        Mapped? sigma = null, rho = null;
        try
        {
            for (int k = 0; k < e.Length; k++)
            {
                int n = manifest.Ports[k].Number;
                e[k] = Mapped.Open(Path.Combine(folder, FieldFile(n, j, 'E')), "<c8", cells * 3);
                if (freq.HasH) h[k] = Mapped.Open(Path.Combine(folder, FieldFile(n, j, 'H')), "<c8", cells * 3);
            }
            sigma = Mapped.Open(Path.Combine(folder, CellFile(j, "sigma")), "<f4", cells);
            rho = Mapped.Open(Path.Combine(folder, CellFile(j, "rho")), "<f4", cells);
            return new PortFieldSet(manifest, freq, folder, x, y, z, e, h, sigma, rho,
                                    Mapped.Open(Path.Combine(folder, CellFile(j, "volume")), "<f4", cells));
        }
        catch
        {
            // a mapped file stays locked on Windows until it is unmapped: release whatever opened before the failure
            foreach (var m in e.Concat(h).Append(sigma).Append(rho)) m?.Dispose();
            throw;
        }
    }

    /// <summary>Each cell's tissue (an index into the manifest's Tissues, −1 background), read whole: 2 bytes a cell.</summary>
    public short[] ReadOwners()
    {
        using var m = Mapped.Open(Path.Combine(Folder, CellFile(Frequency.Index, "owner")), "<i2", CellCount);
        var owners = new short[CellCount];
        m.Read(0, MemoryMarshal.AsBytes(owners.AsSpan()));
        return owners;
    }

    // ── IPortFieldSet ─────────────────────────────────────────────────────────────────────────────

    public void ReadE(int port, long cellStart, int cellCount, Span<float> destination)
        => _e[port].Read(cellStart * FieldBytesPerCell, MemoryMarshal.AsBytes(destination[..(6 * cellCount)]));

    public void ReadH(int port, long cellStart, int cellCount, Span<float> destination)
    {
        if (!HasH) throw new InvalidOperationException("This field set carries no H: its run had B1 off.");
        _h[port].Read(cellStart * FieldBytesPerCell, MemoryMarshal.AsBytes(destination[..(6 * cellCount)]));
    }

    public void ReadCells(long cellStart, int cellCount, Span<float> conductivity, Span<float> density, Span<float> volume)
    {
        _sigma.Read(cellStart * 4, MemoryMarshal.AsBytes(conductivity[..cellCount]));
        _rho.Read(cellStart * 4, MemoryMarshal.AsBytes(density[..cellCount]));
        _volume.Read(cellStart * 4, MemoryMarshal.AsBytes(volume[..cellCount]));
    }

    public void Dispose()
    {
        foreach (var m in _e) m?.Dispose();
        foreach (var m in _h) m?.Dispose();
        _sigma.Dispose();
        _rho.Dispose();
        _volume.Dispose();
    }

    // ── complex matrices in JSON ───────────────────────────────────────────────────────────────────

    internal static List<double[]> Pack(Complex[,] m)
    {
        var rows = new List<double[]>();
        for (int i = 0; i < m.GetLength(0); i++)
        {
            var r = new double[2 * m.GetLength(1)];
            for (int j = 0; j < m.GetLength(1); j++) { r[2 * j] = m[i, j].Real; r[2 * j + 1] = m[i, j].Imaginary; }
            rows.Add(r);
        }
        return rows;
    }

    internal static Complex[,] Unpack(List<double[]> rows)
    {
        int n = rows.Count;
        var m = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n && 2 * j + 1 < rows[i].Length; j++) m[i, j] = new Complex(rows[i][2 * j], rows[i][2 * j + 1]);
        return m;
    }

    // ── one memory-mapped .npy ─────────────────────────────────────────────────────────────────────

    /// <summary>A C-order .npy of a known element type, mapped read-only (or read-write for the writer's normalisation pass).</summary>
    internal sealed class Mapped : IDisposable
    {
        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewStream _view;
        private readonly long _offset;

        private Mapped(MemoryMappedFile file, MemoryMappedViewStream view, long offset) { _file = file; _view = view; _offset = offset; }

        public static Mapped Open(string path, string descr, long elements, bool writable = false)
        {
            long offset = HeaderLength(path, descr, elements);
            var access = writable ? MemoryMappedFileAccess.ReadWrite : MemoryMappedFileAccess.Read;
            var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, access);
            try { return new Mapped(file, file.CreateViewStream(0, 0, access), offset); }
            catch { file.Dispose(); throw; }
        }

        public void Read(long byteOffset, Span<byte> destination)
        {
            _view.Position = _offset + byteOffset;
            _view.ReadExactly(destination);
        }

        public void Write(long byteOffset, ReadOnlySpan<byte> source)
        {
            _view.Position = _offset + byteOffset;
            _view.Write(source);
        }

        public void Dispose()
        {
            _view.Dispose();
            _file.Dispose();
        }

        /// <summary>The data's offset in a .npy, after checking its element type and that it holds <paramref name="elements"/> of them.</summary>
        private static long HeaderLength(string path, string descr, long elements)
        {
            using var fs = File.OpenRead(path);
            Span<byte> lead = stackalloc byte[12];
            if (fs.Read(lead) < 10 || lead[0] != 0x93 || Encoding.ASCII.GetString(lead[1..6]) != "NUMPY")
                throw new InvalidDataException($"'{path}' is not a NumPy array.");
            int major = lead[6];
            int headerLen = major == 1 ? BitConverter.ToUInt16(lead[8..10]) : BitConverter.ToInt32(lead[8..12]);
            int start = (major == 1 ? 10 : 12) + headerLen;
            fs.Position = start - headerLen;
            var header = new byte[headerLen];
            fs.ReadExactly(header);
            string text = Encoding.ASCII.GetString(header);
            if (!text.Contains($"'descr': '{descr}'", StringComparison.Ordinal) || !text.Contains("'fortran_order': False", StringComparison.Ordinal))
                throw new InvalidDataException($"'{path}' is not a C-order {descr} array.");
            int size = descr[^1] - '0';
            if (fs.Length - start != elements * size)
                throw new InvalidDataException($"'{path}' holds {(fs.Length - start) / size:N0} values where {elements:N0} were expected.");
            return start;
        }
    }

    /// <summary>A .npy header for <paramref name="descr"/> and <paramref name="shape"/>, padded to 64 bytes as NumPy writes it.</summary>
    internal static byte[] NpyHeader(string descr, params long[] shape)
    {
        string dims = shape.Length == 1 ? $"{shape[0]}," : string.Join(", ", shape.Select(s => s.ToString(CultureInfo.InvariantCulture)));
        string dict = $"{{'descr': '{descr}', 'fortran_order': False, 'shape': ({dims}), }}";
        int total = 10 + dict.Length + 1;
        int pad = (64 - total % 64) % 64;
        string header = dict + new string(' ', pad) + "\n";
        var bytes = new byte[10 + header.Length];
        bytes[0] = 0x93;
        Encoding.ASCII.GetBytes("NUMPY").CopyTo(bytes, 1);
        bytes[6] = 1;
        BitConverter.GetBytes((ushort)header.Length).CopyTo(bytes, 8);
        Encoding.ASCII.GetBytes(header).CopyTo(bytes, 10);
        return bytes;
    }
}
