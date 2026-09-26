// brief-em3d-28 R-em3d28-3a — a Gmsh MSH 2.2 reader: nodes, tetrahedra, and boundary triangles with
// their physical tags. Below the firewall, so a headless picture of a mesh stays possible.
//
// BOTH ENCODINGS. The brief names ASCII, but GmshGeoWriter asks Gmsh for BINARY (`Mesh.Binary = 1`,
// the format Palace was validated reading in F0), so a run directory holds binary and this reads it;
// ASCII is read too, since it is what a user hand-meshing a .geo gets by default.
//
// IT STREAMS. A 1.5 M-tetrahedron mesh is ~150 MB of text; nothing here reads the file into a string
// or splits it into an array of lines. One buffered pass, one line at a time for text, raw little-endian
// words for binary. Second-order elements (Palace runs at order 2: 10-node tetrahedra, 6-node
// triangles) keep their CORNER nodes only — a picture of the mesh draws straight edges.
//
// A MALFORMED FILE REFUSES WITH ITS PLACE: the line number for text, the byte offset inside a binary
// section, and what was expected there.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace CircuitRF.Render.Scene3D;

/// <summary>A mesh as read: packed node coordinates and corner-node connectivity.</summary>
public sealed class MshMesh
{
    /// <summary>x, y, z per node, in file order; a node's index is its position / 3.</summary>
    public double[] Nodes = [];
    /// <summary>Four corner-node indices per tetrahedron.</summary>
    public int[] Tets = [];
    public int[] TetPhysical = [];
    /// <summary>Three corner-node indices per boundary triangle.</summary>
    public int[] Triangles = [];
    public int[] TrianglePhysical = [];
    public int[] TriangleEntity = [];
    /// <summary>Every element in the file, whatever its type — the count Gmsh's log prints.</summary>
    public long ElementCount;
    public bool Binary;
    /// <summary>Physical group names by tag.</summary>
    public Dictionary<int, string> PhysicalNames { get; } = [];

    public int NodeCount => Nodes.Length / 3;
    public int TetCount => TetPhysical.Length;
    public int TriangleCount => TrianglePhysical.Length;

    /// <summary>A physical group's name, or <c>physical N</c>.</summary>
    public string GroupName(int tag) => PhysicalNames.TryGetValue(tag, out var n) ? n : "physical " + tag.ToString(CultureInfo.InvariantCulture);
}

public static class MshReader
{
    public static MshMesh Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Read(fs, Path.GetFileName(path));
    }

    public static MshMesh Read(Stream stream, string name)
    {
        var r = new Cursor(stream, name);
        var m = new MshMesh();
        int[] idToIndex = [];
        bool sawFormat = false;
        var tets = new IntList(); var tetPhys = new IntList();
        var tris = new IntList(); var triPhys = new IntList(); var triEnt = new IntList();

        while (r.TryReadLine(out string? raw))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            switch (line)
            {
                case "$MeshFormat":
                {
                    var p = r.Tokens(r.ReadLine());
                    if (p.Length < 3) throw r.Bad("a format line '2.2 <file-type> <data-size>'");
                    if (!p[0].StartsWith("2.", StringComparison.Ordinal))
                        throw r.Bad($"MSH version 2.x (this file is {p[0]}; ask Gmsh for Mesh.MshFileVersion = 2.2)");
                    m.Binary = p[1] == "1";
                    if (m.Binary)
                    {
                        if (p[2] != "8") throw r.Bad("8-byte doubles");
                        if (r.ReadInt32() != 1) throw r.Bad("the little-endian marker 1 (this file is big-endian)");
                        r.ReadLine();
                    }
                    r.Expect("$EndMeshFormat");
                    sawFormat = true;
                    break;
                }
                case "$PhysicalNames":
                {
                    int n = r.Int(r.ReadLine());
                    for (int i = 0; i < n; i++)
                    {
                        var s = r.ReadLine().Trim();
                        int q1 = s.IndexOf(' '), q2 = q1 < 0 ? -1 : s.IndexOf(' ', q1 + 1);
                        if (q2 < 0) throw r.Bad("'<dim> <tag> \"<name>\"'");
                        int tag = r.Int(s.AsSpan(q1 + 1, q2 - q1 - 1));
                        m.PhysicalNames[tag] = s[(q2 + 1)..].Trim().Trim('"');
                    }
                    r.Expect("$EndPhysicalNames");
                    break;
                }
                case "$Nodes":
                {
                    if (!sawFormat) throw r.Bad("$MeshFormat before $Nodes");
                    int n = r.Int(r.ReadLine());
                    m.Nodes = new double[3 * n];
                    int maxId = 0;
                    idToIndex = new int[n + 1];
                    for (int i = 0; i < n; i++)
                    {
                        int id; double x, y, z;
                        if (m.Binary) { id = r.ReadInt32(); x = r.ReadDouble(); y = r.ReadDouble(); z = r.ReadDouble(); }
                        else
                        {
                            var t = r.ReadLine();
                            var e = new Fields(t);
                            id = r.Int(e.Next()); x = r.Double(e.Next()); y = r.Double(e.Next()); z = r.Double(e.Next());
                        }
                        if (id <= 0) throw r.Bad("a positive node id");
                        if (id >= idToIndex.Length) Array.Resize(ref idToIndex, Math.Max(id + 1, idToIndex.Length * 2));
                        idToIndex[id] = i + 1;          // +1: 0 means "no such node"
                        maxId = Math.Max(maxId, id);
                        m.Nodes[3 * i] = x; m.Nodes[3 * i + 1] = y; m.Nodes[3 * i + 2] = z;
                    }
                    if (m.Binary) r.ReadLine();
                    r.Expect("$EndNodes");
                    break;
                }
                case "$Elements":
                {
                    long n = r.Long(r.ReadLine());
                    m.ElementCount = n;
                    Span<int> tags = stackalloc int[16];
                    Span<int> nodes = stackalloc int[32];
                    if (m.Binary)
                    {
                        long read = 0;
                        while (read < n)
                        {
                            int type = r.ReadInt32(), count = r.ReadInt32(), ntags = r.ReadInt32();
                            int nn = NodesPer(type) ?? throw r.Bad($"a known element type (got {type})");
                            if (ntags > tags.Length || count <= 0) throw r.Bad("an element block header");
                            for (int e = 0; e < count; e++)
                            {
                                r.ReadInt32();
                                for (int t = 0; t < ntags; t++) tags[t] = r.ReadInt32();
                                for (int k = 0; k < nn; k++) nodes[k] = r.ReadInt32();
                                Add(type, tags[..ntags], nodes[..nn]);
                            }
                            read += count;
                        }
                        r.ReadLine();
                    }
                    else
                    {
                        for (long i = 0; i < n; i++)
                        {
                            var f = new Fields(r.ReadLine());
                            r.Int(f.Next());
                            int type = r.Int(f.Next()), ntags = r.Int(f.Next());
                            int nn = NodesPer(type) ?? throw r.Bad($"a known element type (got {type})");
                            if (ntags > tags.Length) throw r.Bad("at most 16 tags");
                            for (int t = 0; t < ntags; t++) tags[t] = r.Int(f.Next());
                            for (int k = 0; k < nn; k++) nodes[k] = r.Int(f.Next());
                            Add(type, tags[..ntags], nodes[..nn]);
                        }
                    }
                    r.Expect("$EndElements");
                    break;
                }
                default:
                    if (!line.StartsWith('$')) throw r.Bad("a section header ($MeshFormat, $Nodes, $Elements, …)");
                    string end = "$End" + line[1..];
                    if (m.Binary && line is "$Periodic" or "$NodeData" or "$ElementData" or "$ElementNodeData")
                        throw r.Bad($"no binary {line} section (not read by this viewer)");
                    while (r.ReadLine().Trim() != end) { }
                    break;
            }
        }
        if (!sawFormat) throw r.Bad("a $MeshFormat section — this is not a Gmsh mesh");

        m.Tets = tets.ToArray(); m.TetPhysical = tetPhys.ToArray();
        m.Triangles = tris.ToArray(); m.TrianglePhysical = triPhys.ToArray(); m.TriangleEntity = triEnt.ToArray();
        return m;

        int Node(int id)
        {
            int ix = id > 0 && id < idToIndex.Length ? idToIndex[id] : 0;
            if (ix == 0) throw r.Bad($"an element to name defined nodes (node {id} is not in $Nodes)");
            return ix - 1;
        }

        void Add(int type, ReadOnlySpan<int> tg, ReadOnlySpan<int> nd)
        {
            int phys = tg.Length > 0 ? tg[0] : 0, ent = tg.Length > 1 ? tg[1] : 0;
            switch (type)
            {
                case 4: case 11:
                    tets.Add(Node(nd[0])); tets.Add(Node(nd[1])); tets.Add(Node(nd[2])); tets.Add(Node(nd[3]));
                    tetPhys.Add(phys);
                    break;
                case 2: case 9:
                    tris.Add(Node(nd[0])); tris.Add(Node(nd[1])); tris.Add(Node(nd[2]));
                    triPhys.Add(phys); triEnt.Add(ent);
                    break;
            }
        }
    }

    /// <summary>Nodes per element for the MSH 2 types a Gmsh 3D mesh can hold; null for another.</summary>
    private static int? NodesPer(int type) => type switch
    {
        1 => 2, 2 => 3, 3 => 4, 4 => 4, 5 => 8, 6 => 6, 7 => 5, 8 => 3, 9 => 6, 10 => 9, 11 => 10,
        12 => 27, 13 => 18, 14 => 14, 15 => 1, 16 => 8, 17 => 20, 18 => 15, 19 => 13,
        _ => null,
    };

    /// <summary>A growable int array that does not box or over-copy.</summary>
    private sealed class IntList
    {
        private int[] _a = new int[1024];
        private int _n;
        public void Add(int v) { if (_n == _a.Length) Array.Resize(ref _a, _a.Length * 2); _a[_n++] = v; }
        public int[] ToArray() => _a[.._n];
    }

    /// <summary>Space-separated fields of one line, without splitting it into strings.</summary>
    private ref struct Fields(string line)
    {
        private readonly string _s = line;
        private int _i;
        public ReadOnlySpan<char> Next()
        {
            while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t')) _i++;
            int start = _i;
            while (_i < _s.Length && _s[_i] != ' ' && _s[_i] != '\t') _i++;
            return _s.AsSpan(start, _i - start);
        }
    }

    /// <summary>A buffered cursor that reads text lines and little-endian binary words from one stream,
    /// counting lines so an error can say where it is.</summary>
    private sealed class Cursor(Stream s, string name)
    {
        private readonly byte[] _buf = new byte[1 << 16];
        private int _pos, _len;
        private long _offset;             // bytes consumed before _buf[0]
        private int _line;
        private bool _binary;
        private readonly StringBuilder _sb = new();

        private bool Fill()
        {
            _offset += _len;
            _len = s.Read(_buf, 0, _buf.Length);
            _pos = 0;
            return _len > 0;
        }

        public bool TryReadLine(out string line)
        {
            _sb.Clear();
            while (true)
            {
                if (_pos == _len && !Fill()) { line = _sb.ToString(); return _sb.Length > 0; }
                int nl = Array.IndexOf(_buf, (byte)'\n', _pos, _len - _pos);
                int end = nl < 0 ? _len : nl;
                for (int i = _pos; i < end; i++) if (_buf[i] != '\r') _sb.Append((char)_buf[i]);
                _pos = end;
                if (nl >= 0) { _pos++; _line++; line = _sb.ToString(); return true; }
            }
        }

        public string ReadLine() => TryReadLine(out var l) ? l : throw Bad("more data (the file ends early)");

        public void Expect(string what)
        {
            string l = ReadLine().Trim();
            if (l != what) throw Bad($"{what}, found '{(l.Length > 40 ? l[..40] + "…" : l)}'");
        }

        private void Need(int n, Span<byte> into)
        {
            for (int k = 0; k < n; k++)
            {
                if (_pos == _len && !Fill()) throw Bad("more binary data (the file ends early)");
                into[k] = _buf[_pos++];
            }
        }

        public int ReadInt32()
        {
            _binary = true;
            if (_len - _pos >= 4) { int v = BinaryPrimitives.ReadInt32LittleEndian(_buf.AsSpan(_pos)); _pos += 4; return v; }
            Span<byte> b = stackalloc byte[4]; Need(4, b); return BinaryPrimitives.ReadInt32LittleEndian(b);
        }

        public double ReadDouble()
        {
            _binary = true;
            if (_len - _pos >= 8) { double v = BinaryPrimitives.ReadDoubleLittleEndian(_buf.AsSpan(_pos)); _pos += 8; return v; }
            Span<byte> b = stackalloc byte[8]; Need(8, b); return BinaryPrimitives.ReadDoubleLittleEndian(b);
        }

        public string[] Tokens(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        public int Int(ReadOnlySpan<char> t)
            => int.TryParse(t.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : throw Bad($"an integer, found '{t.ToString()}'");

        public long Long(ReadOnlySpan<char> t)
            => long.TryParse(t.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : throw Bad($"an integer, found '{t.ToString()}'");

        public double Double(ReadOnlySpan<char> t)
            => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : throw Bad($"a number, found '{t.ToString()}'");

        public InvalidDataException Bad(string expected)
            => new(_binary   // a binary block has no lines, so a count past one names the wrong line
                ? $"{name}, byte {_offset + _pos}: expected {expected}."
                : $"{name}, line {_line} (byte {_offset + _pos}): expected {expected}.");
    }
}
