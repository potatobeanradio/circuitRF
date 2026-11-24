// ================================================================
//  LoadpullFiles.cs — the one way a loadpull file is opened
//
//  Brief LPW, R-lpw-6. Four formats read into the same canonical
//  loadpull DataSet: .spl, .lpcwave, .satwave and .cst. Every place that
//  opens one — the Data Display's library, `render`, the Model Composer's
//  validation — calls Read here, so a file that opens in one opens in all
//  of them, and a fifth format is added in one place. A source scan
//  (LoadpullFilesDispatchTests) holds that no other file calls a reader.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using RfCore.Data;

namespace RfCore.Loadpull
{
    public enum LoadpullFileFormat { Spl, Lpcwave, Satwave, Cst }

    public static class LoadpullFiles
    {
        /// <summary>The extensions this dispatcher reads, with the dot, lower case.</summary>
        public static readonly IReadOnlyList<string> Extensions = new[] { ".spl", ".lpcwave", ".satwave", ".cst" };

        /// <summary>File-picker patterns for <see cref="Extensions"/> (<c>*.spl</c>, …).</summary>
        public static readonly IReadOnlyList<string> Patterns = new[] { "*.spl", "*.lpcwave", "*.satwave", "*.cst" };

        /// <summary>True when <paramref name="extension"/> (with or without the dot) is a loadpull file's.</summary>
        public static bool IsLoadpullExtension(string? extension) => FormatOfExtension(extension) is not null;

        /// <summary>The format an extension names, or null.</summary>
        public static LoadpullFileFormat? FormatOfExtension(string? extension)
        {
            if (string.IsNullOrEmpty(extension)) return null;
            string e = extension.StartsWith('.') ? extension : "." + extension;
            return e.ToLowerInvariant() switch
            {
                ".spl"     => LoadpullFileFormat.Spl,
                ".lpcwave" => LoadpullFileFormat.Lpcwave,
                ".satwave" => LoadpullFileFormat.Satwave,
                ".cst"     => LoadpullFileFormat.Cst,
                _          => null,
            };
        }

        /// <summary>
        /// Which reader a file needs: by extension, and for an unknown one by its first lines. Null when neither says.
        /// </summary>
        public static LoadpullFileFormat? Classify(string path)
        {
            if (FormatOfExtension(Path.GetExtension(path)) is { } byExt) return byExt;
            if (!File.Exists(path)) return null;
            try
            {
                using var r = new StreamReader(path);
                for (int i = 0; i < 200 && r.ReadLine() is { } line; i++)
                {
                    var t = line.Trim();
                    if (t.StartsWith("#MEASUREMENT TYPE", StringComparison.OrdinalIgnoreCase)
                        || t.StartsWith("#FUNDAMENTAL FREQUENCY", StringComparison.OrdinalIgnoreCase)) return LoadpullFileFormat.Cst;
                    if (t.StartsWith("! TITLES:", StringComparison.OrdinalIgnoreCase)) return LoadpullFileFormat.Satwave;
                    if (t.StartsWith("Point", StringComparison.OrdinalIgnoreCase) && t.Contains("Gamma", StringComparison.OrdinalIgnoreCase))
                        return LoadpullFileFormat.Lpcwave;
                    // A .spl carries each load as a gamma_ldN real/imaginary column pair.
                    if (t.Contains("gamma_ld", StringComparison.OrdinalIgnoreCase)) return LoadpullFileFormat.Spl;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        /// <summary>
        /// Reads a loadpull file into the canonical loadpull DataSet. Throws <see cref="FormatException"/> for a file that
        /// is not one of the four formats, or one that does not read.
        /// </summary>
        public static DataSet Read(string path) => Classify(path) switch
        {
            LoadpullFileFormat.Spl     => SplReader.ReadSpl(path),
            LoadpullFileFormat.Lpcwave => LpcwaveReader.ReadLpcwave(path),
            LoadpullFileFormat.Satwave => SatwaveReader.Read(path),
            LoadpullFileFormat.Cst     => CstReader.Read(path),
            _ => throw new FormatException($"'{Path.GetFileName(path)}' is not a loadpull file (.spl, .lpcwave, .satwave or .cst)."),
        };
    }
}
