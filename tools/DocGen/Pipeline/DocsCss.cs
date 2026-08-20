using System;
using System.IO;
using System.Text.RegularExpressions;

namespace CircuitRF.DocGen.Pipeline;

/// <summary>
/// Maintains the ONE generated region inside the otherwise hand-written documentation stylesheet.
///
/// <para>The stylesheet is a good, commented, hand-maintained file and the brief is explicit that
/// the look does not change — so the generator rewrites a single delimited block inside it rather
/// than owning the whole file. The delimiters are load-bearing: a run that cannot find them appends
/// the block rather than silently doing nothing.</para>
/// </summary>
public static class DocsCss
{
    public const string Begin = "/* ==== BEGIN GENERATED FONT BLOCK (tools/DocGen) ==== */";
    public const string End   = "/* ==== END GENERATED FONT BLOCK ==== */";

    public static void WriteFontBlock(string cssPath, string block)
    {
        string css = File.Exists(cssPath) ? File.ReadAllText(cssPath) : "";
        string replacement = Begin + "\n" + block.TrimEnd() + "\n" + End;

        var rx = new Regex(Regex.Escape(Begin) + ".*?" + Regex.Escape(End), RegexOptions.Singleline);
        css = rx.IsMatch(css)
            ? rx.Replace(css, replacement.Replace("$", "$$"))
            : css.TrimEnd() + "\n\n" + replacement + "\n";

        File.WriteAllText(cssPath, css);
    }
}
