// ================================================================
//  TsvWriter.cs  —  Tab-delimited (long-format) DataSet exporter
//
//  One section per cube, across all groups.
//  Section header:  # {group}.{name}  (group. omitted for default group)
//  Column headers:  {axisName}[{unit}]  ([] omitted when unit empty),
//                   then "value" (real) or "re" + "im" (complex).
//  Data rows:       one per element, Cartesian product of axis indices
//                   (row-major, matching ComplexValues/RealValues flat order).
//  Sections separated by a blank line.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RfCore.Data;

namespace RfCore.Export;

internal static class TsvWriter
{
    public static void Write(string path, DataSet ds, ExportOptions opts)
    {
        using var sw = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        bool firstSection = true;

        foreach (var group in ds.Groups)
        {
            foreach (var kvp in ds.CubesIn(group))
            {
                if (!firstSection) sw.Write('\n');
                firstSection = false;

                WriteSection(sw, group, kvp.Key, kvp.Value);
            }
        }
    }

    private static void WriteSection(StreamWriter sw, string group, string name, DataCube cube)
    {
        // Section header
        if (group == DataSet.DefaultGroup)
            sw.Write($"# {name}\n");
        else
            sw.Write($"# {group}.{name}\n");

        bool isComplex = cube.DataKind == DataKind.Complex;
        var axes = cube.Axes;

        // Column header row
        var header = new StringBuilder();
        foreach (var ax in axes)
        {
            if (header.Length > 0) header.Append('\t');
            header.Append(ax.Name);
            if (!string.IsNullOrEmpty(ax.Unit))
            {
                header.Append('[');
                header.Append(ax.Unit);
                header.Append(']');
            }
        }
        if (header.Length > 0) header.Append('\t');
        if (isComplex)
            header.Append("re\tim");
        else
            header.Append("value");
        sw.Write(header.ToString());
        sw.Write('\n');

        // Data rows
        int rank = axes.Count;
        if (rank == 0)
        {
            // Scalar
            if (isComplex)
            {
                var c = cube.ComplexValues[0];
                sw.Write(FormatDouble(c.Real));
                sw.Write('\t');
                sw.Write(FormatDouble(c.Imaginary));
            }
            else
            {
                sw.Write(FormatDouble(cube.RealValues[0]));
            }
            sw.Write('\n');
            return;
        }

        int[] lengths = new int[rank];
        for (int d = 0; d < rank; d++) lengths[d] = axes[d].Length;
        int total = lengths[0];
        for (int d = 1; d < rank; d++) total *= lengths[d];

        int[] coords = new int[rank];
        for (int flatIdx = 0; flatIdx < total; flatIdx++)
        {
            // Decompose flatIdx → coords (row-major)
            int tmp = flatIdx;
            for (int d = rank - 1; d >= 0; d--)
            {
                coords[d] = tmp % lengths[d];
                tmp /= lengths[d];
            }

            var row = new StringBuilder();
            for (int d = 0; d < rank; d++)
            {
                if (d > 0) row.Append('\t');
                var ax = axes[d];
                int idx = coords[d];
                if (ax.Labels != null)
                    row.Append(ax.Labels[idx]);
                else
                    row.Append(ax.Values[idx].ToString("G17", CultureInfo.InvariantCulture));
            }

            row.Append('\t');
            if (isComplex)
            {
                var c = cube.ComplexValues[flatIdx];
                row.Append(FormatDouble(c.Real));
                row.Append('\t');
                row.Append(FormatDouble(c.Imaginary));
            }
            else
            {
                row.Append(FormatDouble(cube.RealValues[flatIdx]));
            }
            sw.Write(row.ToString());
            sw.Write('\n');
        }
    }

    private static string FormatDouble(double v) =>
        v.ToString("G17", CultureInfo.InvariantCulture);
}
