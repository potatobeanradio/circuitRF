// ================================================================
//  DataSetImporter.cs  —  Public entry point for .npy import
//
//  Reconstructs a DataSet from a circuitRF .npy file (Level 1).
//  When __linnet_* fields are present, also returns an
//  ImportedLinearNetwork (Level 2 data — see data-file-format.md).
//
//  See docs/design/data-file-format.md for the full consumer contract,
//  Level-1 examples, and the Level-2 reconstruction recipe.
// ================================================================

using System;
using System.IO;
using RfCore.Data;

namespace RfCore.Export;

/// <summary>
/// Imports a circuitRF <c>.npy</c> file back into a <see cref="DataSet"/>.
/// This is the Level-1 importer: it rehydrates cubes, axes, kinds, and metadata.
/// When the file contains <c>__linnet_*</c> fields it also returns an
/// <see cref="ImportedLinearNetwork"/> — the payload for Level-2 reconstruction.
/// </summary>
public static class DataSetImporter
{
    /// <summary>
    /// Load a circuitRF <c>.npy</c> file.
    /// </summary>
    /// <param name="path">Path to the <c>.npy</c> file.</param>
    /// <returns>
    /// The reconstructed <see cref="DataSet"/> and, if the file includes
    /// <c>__linnet_*</c> linear-network data, a non-null <see cref="ImportedLinearNetwork"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="path"/> is null.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="InvalidDataException">
    /// Thrown when the file is not a valid circuitRF <c>.npy</c>, has an unsupported version,
    /// or has a <c>format_version</c> mismatch.  Alpha files are not backward-compatible —
    /// regenerate from the current exporter.
    /// </exception>
    public static (DataSet DataSet, ImportedLinearNetwork? LinearNetwork) Import(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException("File not found.", path);

        return NpyReader.Read(path);
    }
}
