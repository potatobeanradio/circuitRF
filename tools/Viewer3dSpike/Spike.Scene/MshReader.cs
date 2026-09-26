namespace Viewer3dSpike.Scene;

/// <summary>
/// Throwaway Gmsh MSH 2.2 reader (ASCII or binary) — exactly enough for F0 case A's mesh: nodes,
/// surface triangles (3- or 6-node) and tetrahedra (4- or 10-node), corner nodes only. Brief 27 §2.1
/// asks for "a throwaway parser in the spike"; nothing here is meant to survive into src/.
/// </summary>
public sealed class MshMesh
{
    public float[] Nodes = [];                  // xyz per node, packed; node index = position / 3
    public List<(int A, int B, int C, int Physical, int Entity)> Triangles = [];
    public List<(int A, int B, int C, int D, int Physical)> Tets = [];
    public Dictionary<int, string> PhysicalNames = [];
}

public static class MshReader
{
    public static MshMesh Read(string path)
    {
        using var fs = File.OpenRead(path);
        var r = new BinaryReader(fs);
        var mesh = new MshMesh();
        bool binary = false;
        var idToIndex = new Dictionary<int, int>();
        while (fs.Position < fs.Length)
        {
            string line = ReadLine(r).Trim();
            switch (line)
            {
                case "$MeshFormat":
                {
                    var p = ReadLine(r).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (!p[0].StartsWith("2.")) throw new InvalidDataException($"MSH {p[0]}: only 2.x is read");
                    binary = p[1] == "1";
                    if (binary)
                    {
                        if (r.ReadInt32() != 1) throw new InvalidDataException("big-endian MSH");
                        ReadLine(r);
                    }
                    Expect(r, "$EndMeshFormat");
                    break;
                }
                case "$PhysicalNames":
                {
                    int n = int.Parse(ReadLine(r).Trim());
                    for (int i = 0; i < n; i++)
                    {
                        var s = ReadLine(r).Trim();
                        var q = s.Split(' ', 3);
                        mesh.PhysicalNames[int.Parse(q[1])] = q[2].Trim('"');
                    }
                    Expect(r, "$EndPhysicalNames");
                    break;
                }
                case "$Nodes":
                {
                    int n = int.Parse(ReadLine(r).Trim());
                    mesh.Nodes = new float[n * 3];
                    for (int i = 0; i < n; i++)
                    {
                        int id; double x, y, z;
                        if (binary) { id = r.ReadInt32(); x = r.ReadDouble(); y = r.ReadDouble(); z = r.ReadDouble(); }
                        else
                        {
                            var q = ReadLine(r).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            id = int.Parse(q[0]); x = double.Parse(q[1]); y = double.Parse(q[2]); z = double.Parse(q[3]);
                        }
                        idToIndex[id] = i;
                        mesh.Nodes[3 * i] = (float)x; mesh.Nodes[3 * i + 1] = (float)y; mesh.Nodes[3 * i + 2] = (float)z;
                    }
                    if (binary) ReadLine(r);
                    Expect(r, "$EndNodes");
                    break;
                }
                case "$Elements":
                {
                    int n = int.Parse(ReadLine(r).Trim());
                    if (binary)
                    {
                        int read = 0;
                        while (read < n)
                        {
                            int type = r.ReadInt32(), count = r.ReadInt32(), ntags = r.ReadInt32();
                            int nn = NodesPer(type);
                            var nodes = new int[nn];
                            var tags = new int[ntags];
                            for (int e = 0; e < count; e++)
                            {
                                r.ReadInt32();
                                for (int t = 0; t < ntags; t++) tags[t] = r.ReadInt32();
                                for (int k = 0; k < nn; k++) nodes[k] = r.ReadInt32();
                                Add(mesh, idToIndex, type, tags, nodes);
                            }
                            read += count;
                        }
                        ReadLine(r);
                    }
                    else
                    {
                        for (int i = 0; i < n; i++)
                        {
                            var q = ReadLine(r).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                            int type = q[1], ntags = q[2];
                            Add(mesh, idToIndex, type, q[3..(3 + ntags)], q[(3 + ntags)..]);
                        }
                    }
                    Expect(r, "$EndElements");
                    break;
                }
                default:
                    if (line.StartsWith('$'))
                    {
                        string end = "$End" + line[1..];
                        while (ReadLine(r).Trim() != end) { }
                    }
                    break;
            }
        }
        return mesh;
    }

    static void Add(MshMesh m, Dictionary<int, int> map, int type, int[] tags, int[] nodes)
    {
        int phys = tags.Length > 0 ? tags[0] : 0, ent = tags.Length > 1 ? tags[1] : 0;
        switch (type)
        {
            case 2: case 9: m.Triangles.Add((map[nodes[0]], map[nodes[1]], map[nodes[2]], phys, ent)); break;
            case 4: case 11: m.Tets.Add((map[nodes[0]], map[nodes[1]], map[nodes[2]], map[nodes[3]], phys)); break;
        }
    }

    static int NodesPer(int type) => type switch
    {
        1 => 2, 2 => 3, 3 => 4, 4 => 4, 5 => 8, 6 => 6, 7 => 5, 8 => 3, 9 => 6, 10 => 9, 11 => 10, 15 => 1,
        _ => throw new InvalidDataException($"element type {type}")
    };

    static string ReadLine(BinaryReader r)
    {
        var sb = new System.Text.StringBuilder();
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            char c = (char)r.ReadByte();
            if (c == '\n') break;
            if (c != '\r') sb.Append(c);
        }
        return sb.ToString();
    }

    static void Expect(BinaryReader r, string s)
    {
        var l = ReadLine(r).Trim();
        if (l != s) throw new InvalidDataException($"expected {s}, got '{l}'");
    }
}
