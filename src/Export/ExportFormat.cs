// ================================================================
//  ExportFormat.cs  —  Output format selector for DataSetExporter
//
//  See docs/design/data-export.md §7.1.
// ================================================================

namespace RfCore.Export;

/// <summary>
/// Target file format for <see cref="DataSetExporter.Export"/>.
/// </summary>
public enum ExportFormat
{
    /// <summary>
    /// MATLAB v7.3 / HDF5 file (<c>.mat</c>).
    /// Written via PureHDF (pure managed C#; no native dependencies).
    /// Supports unlimited file size (no 2 GB cap).
    /// Readable by MATLAB R2006b+, Python h5py, and Julia HDF5.jl.
    /// </summary>
    Mat = 0,

    /// <summary>
    /// NumPy packed structured array (<c>.npy</c>).
    /// One file, one <c>dtype</c> — each DataCube becomes a named field
    /// with a sub-array dtype.  JSON axis metadata is stored in
    /// <c>__meta__</c> bytes fields.
    /// Readable by NumPy ≥ 1.9 and any library that supports structured dtypes.
    /// </summary>
    Npy = 1,
}
