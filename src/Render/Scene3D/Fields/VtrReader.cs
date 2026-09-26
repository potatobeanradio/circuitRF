// brief-em3d-29 R-em3d29-6 — openEMS's frequency-domain field dump, read. What the pinned openEMS writes was
// read off a real run and its own source (processfields_fd.cpp, vtk_file_writer.cpp), not assumed:
//
//   * a dump box with FileType 0 (VTK) and DumpType 10 writes, per frequency, <name>_f=<Hz>_abs.vtr and
//     <name>_f=<Hz>_arg.vtr — each component's MAGNITUDE and PHASE — plus 21 real snapshots at fixed
//     phases (_p=000 … _p=342), which carry nothing the pair does not;
//   * each is a VTK XML RectilinearGrid: Coordinates (three Float64 arrays), and the field ("E-Field",
//     three Float32 components) as PointData — at the grid's cell centres, with DumpMode 2;
//   * INLINE binary (format="binary"), zlib-compressed (compressor="vtkZLibDataCompressor"), UInt32 headers:
//     each array is a base64 header [blocks, block size, last block size, compressed sizes…] followed by
//     the base64 of the compressed blocks, each block its own zlib stream.
//
// HDF5 would have needed a native library (CLAUDE.md, ask first); the VTK route needs none, and
// System.IO.Compression's ZLibStream is .NET's own.
//
// THE SAME FIELD MODEL (R-em3d29-6b): every grid cell is six linear tetrahedra over its eight corners, so the
// slicer, the surfaces, the sampler and the GPU pass draw openEMS's field with no special case. On a grid
// line the slice is exact, as the brief asks: every tetrahedron's corners are grid nodes.

using System.Buffers.Text;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CircuitRF.Render.Scene3D.Fields;

/// <summary>A rectilinear grid's lines and one vector array on its nodes.</summary>
public sealed record VtrField(double[] X, double[] Y, double[] Z, string Name, int Components, float[] Values);

public static class VtrReader
{
    /// <summary>Reads <paramref name="path"/>: its coordinates and its first point-data array
    /// (<see cref="CircuitRF.Design.Em3d.VtrFile"/> decodes it).</summary>
    public static VtrField Read(string path)
    {
        try
        {
            var d = CircuitRF.Design.Em3d.VtrFile.Read(path);
            return new VtrField(d.X, d.Y, d.Z, d.Name, d.Components, d.Values);
        }
        catch (InvalidDataException e) { throw new FieldReadException(e.Message); }
    }

    /// <summary>
    /// A rectilinear grid's cells as linear tetrahedra — six per cell over its eight corners, about the
    /// cell's (0,0,0)–(1,1,1) diagonal — sharing the grid's nodes. Attribute 0 throughout: a dump box
    /// carries no material. Positions are the grid's own (metres, for openEMS).
    /// </summary>
    public static FieldMesh Mesh(VtrField f, double toMetres = 1)
    {
        int nx = f.X.Length, ny = f.Y.Length, nz = f.Z.Length;
        var points = new float[3 * nx * ny * nz];
        for (int k = 0, p = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++, p += 3)
                {
                    points[p] = (float)f.X[i]; points[p + 1] = (float)f.Y[j]; points[p + 2] = (float)f.Z[k];
                }
        long cells = (long)Math.Max(0, nx - 1) * Math.Max(0, ny - 1) * Math.Max(0, nz - 1);
        var conn = new int[checked(24 * cells)];
        ReadOnlySpan<int> six = [0, 1, 3, 7, 0, 1, 5, 7, 0, 2, 3, 7, 0, 2, 6, 7, 0, 4, 5, 7, 0, 4, 6, 7];
        int Id(int i, int j, int k) => (k * ny + j) * nx + i;
        int c = 0;
        for (int k = 0; k + 1 < nz; k++)
            for (int j = 0; j + 1 < ny; j++)
                for (int i = 0; i + 1 < nx; i++)
                    foreach (int b in six)
                        conn[c++] = Id(i + (b & 1), j + ((b >> 1) & 1), k + ((b >> 2) & 1));
        return new FieldMesh
        {
            Shape = FieldCellShape.Tetrahedron, Order = 1, NodesPerCell = 4, Points = points, Cells = conn,
            Attribute = new int[6 * cells], ToMetres = toMetres,
        };
    }
}
