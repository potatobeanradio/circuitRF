// Importing a generator impedance from a one-port Touchstone file (docs/design/smith-chart.md §3.1;
// brief-smith-1-document.md R-smith1-7).
//
// THIS ADDS NO SECOND TOUCHSTONE INTERPRETATION. TouchstoneIO reads the file; RFNetwork converts
// what it returns. A file with a per-port reference that is not the one in its `#` line is read the
// way TouchstoneIO reads it and no other way — this file has no opinion about `#`-line parsing and
// must not grow one.
//
// THE IMPORT COPIES VALUES IN. The path is recorded as provenance only: it is displayed and it
// drives a Re-import button, and NOTHING resolves it at load. That is the opposite choice from the
// overlays and from an S1P/S2P element's FileRef, and deliberately — an overlay is reference
// material the user is comparing against, while the generator is part of the design, and a design
// that stops opening because a file moved is a design that was never portable.

using RfCore;

namespace CircuitRF.Design.Smith;

/// <summary>Reads a <c>.s1p</c> into generator-table rows. Framework-free.</summary>
public static class SmithGeneratorImport
{
    /// <summary>
    /// One row per file frequency, with S₁₁ converted to Z <b>against the file's own stated
    /// reference impedance</b>.
    ///
    /// <para>For a one-port that reference is unambiguous: <c>SNP.Z0</c> is port 1's value by this
    /// type's own definition, which is what the option line declares and what
    /// <c>SNP.Z0PerPort</c> leaves alone.</para>
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not readable, holds no data, or is not a
    /// one-port — the sentence names the file and what it actually holds.</exception>
    public static IReadOnlyList<SmithGeneratorRow> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        SNP snp;
        try { snp = TouchstoneIO.ReadFile(path, readComments: false); }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' could not be read as Touchstone: {ex.Message}");
        }

        if (snp.IsEmpty)
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' holds no frequency points — the generator needs at "
              + "least one row of impedance.");

        // A refusal naming what the file IS, rather than silently reading S11 out of the corner of a
        // 2-port: an .s2p handed to this dialog is far more likely to be the wrong file than a
        // deliberate request for its input reflection.
        if (snp.Ports != 1)
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' is a {snp.Ports}-port; the generator's impedance comes "
              + "from a one-port file. Use an .s1p.");

        // The conversion RfCore already owns, not a second copy of Z = Z0(1+S)/(1−S) here.
        var z = snp.Type == MatrixType.Z ? snp : RFNetwork.SToZ(snp);

        var rows = new List<SmithGeneratorRow>(z.FrequencyCount);
        for (int i = 0; i < z.FrequencyCount; i++)
        {
            var zi = z.Matrices[i][0, 0];
            // Frequencies are HERTZ in an SNP whatever the option line said — that type states it,
            // and it is the one place the scale is resolved.
            rows.Add(new SmithGeneratorRow(z.Frequencies[i], zi.Real, zi.Imaginary));
        }

        return rows;
    }

    /// <summary>
    /// <see cref="Read"/>, landed on a generator: the rows REPLACE whatever was there, and the path
    /// is recorded as provenance.
    ///
    /// <para>Replace rather than merge — an import is "the generator is this file", and a merge
    /// would leave rows from a previous file in a table nothing says is mixed.</para>
    /// </summary>
    public static void ImportInto(SmithGenerator generator, string path)
    {
        ArgumentNullException.ThrowIfNull(generator);

        var rows = Read(path);

        generator.Rows.Clear();
        foreach (var r in rows) generator.Rows.Add(r);
        generator.SourcePath = path;
    }
}
