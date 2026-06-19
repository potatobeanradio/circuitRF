// ================================================================
//  DataSetSubset.cs  —  Build a DataSet containing only the named groups
// ================================================================

using System.Collections.Generic;
using RfCore.Data;

namespace RfCore.Export;

/// <summary>
/// Builds a new <see cref="DataSet"/> containing only the named groups' cubes.
/// Cubes are immutable — shallow copy via <see cref="DataSet.AddToGroup"/> is safe.
/// Unknown group names are silently skipped.
/// </summary>
public static class DataSetSubset
{
    public static DataSet SelectGroups(DataSet ds, IEnumerable<string> groups)
    {
        var outp = new DataSet();
        foreach (var g in groups)
        {
            if (!ds.ContainsGroup(g)) continue;
            foreach (var kvp in ds.CubesIn(g))
                outp.AddToGroup(g, kvp.Key, kvp.Value);
        }
        return outp;
    }
}
