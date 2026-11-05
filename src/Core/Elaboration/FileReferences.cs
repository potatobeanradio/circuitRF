using System.Text.RegularExpressions;
using CircuitRF.Core.Devices;

namespace CircuitRF.Core.Elaboration;

/// <summary>How a parameter's value holds a file reference (<see cref="FileReferences.KindOf"/>).</summary>
public enum FileValueKind
{
    /// <summary>Not a file reference.</summary>
    None,
    /// <summary>The whole value is one path, optionally quoted.</summary>
    Path,
    /// <summary>A modulated source's carrier text, whose <c>File=</c> field is a path
    /// (<c>SignalSpec.MapFilePath</c>).</summary>
    CarrierSpec,
}

/// <summary>
/// Which instance parameters hold a FILE the elaborator resolves with
/// <see cref="Elaborator.ResolveFileReference"/>, collected in one place (brief vendorlib-8 R-vl8-2d)
/// so the extractor can rebase exactly those when it emits a cell that lives somewhere other than the
/// run's base.
///
/// <para><b>This list follows the elaborator; it does not drive it.</b> Every call site of
/// <c>ResolveFileReference</c> must be covered here, and a source scan
/// (<c>tests/Ui.Tests/VendorLibraries/OwningRootPathTests.cs</c>) fails when a new site appears that is not,
/// so the next file-valued parameter cannot be forgotten.</para>
///
/// <para>A virtual reference (<c>vendorlib://</c>, <c>pdk://</c>, anything with a scheme) is not a
/// path and is never rebased. Nor is an absolute path, which the elaborator already passes through
/// unchanged.</para>
/// </summary>
public static class FileReferences
{
    private static readonly Regex RxCarrier  = new(@"^Carrier\[\d+\]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RxBareName = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>How <paramref name="parameterName"/> on an instance of <paramref name="reference"/>
    /// holds a file reference, if it does.</summary>
    public static FileValueKind KindOf(string reference, string parameterName)
    {
        bool Is(string r) => reference.Equals(r, StringComparison.OrdinalIgnoreCase);
        bool Named(string p) => parameterName.Equals(p, StringComparison.OrdinalIgnoreCase);

        if (Is("SnP") || Is(EmFieldDataRecord.TypeName) || Is(ComponentModelFactory.ComposedFetTypeName) || Is(ComponentModelFactory.ComposedCoreTypeName))
            return Named("File") ? FileValueKind.Path : FileValueKind.None;
        if (Is("VerilogA"))
            return Named(ComponentModelFactory.VerilogAFileParam) ? FileValueKind.Path : FileValueKind.None;
        if (Is("wBond"))
            return Named("File") || Named("MaterialLibrary") ? FileValueKind.Path : FileValueKind.None;
        if (Is("MOD_Source"))
            return RxCarrier.IsMatch(parameterName) ? FileValueKind.CarrierSpec : FileValueKind.None;
        return FileValueKind.None;
    }

    /// <summary>True when the parameter holds a file reference.</summary>
    public static bool IsFileValued(string reference, string parameterName)
        => KindOf(reference, parameterName) != FileValueKind.None;

    /// <summary>A scheme-qualified reference (<c>vendorlib://…</c>, <c>pdk://…</c>) names something a
    /// resolver looks up, not a path.</summary>
    public static bool IsVirtual(string value) => value.Contains("://", StringComparison.Ordinal);

    /// <summary>
    /// <paramref name="expression"/> with each relative path in it made absolute against
    /// <paramref name="baseDirectory"/>, or null when there is nothing to rebase (not a file
    /// parameter, blank, absolute, virtual, or a bare name the elaborator may look up in scope).
    /// A rebased path is always quoted, since an absolute path may hold a space.
    /// </summary>
    public static string? Rebase(string reference, string parameterName, string expression, string baseDirectory)
    {
        var kind = KindOf(reference, parameterName);
        if (kind == FileValueKind.None || string.IsNullOrWhiteSpace(expression)) return null;

        string text = expression.Trim();
        bool quoted = text.Length >= 2 && text[0] == '"' && text[^1] == '"';
        string inner = quoted ? text[1..^1].Trim() : text;

        if (kind == FileValueKind.Path)
        {
            if (!NeedsRebase(inner)) return null;
            // A VerilogA's unquoted bare word may name a variable the elaborator binds in scope.
            if (!quoted && reference.Equals("VerilogA", StringComparison.OrdinalIgnoreCase) && RxBareName.IsMatch(inner))
                return null;
            return Quote(Elaborator.ResolveFileReference(inner, baseDirectory));
        }

        string mapped = RfCore.Signals.SignalSpec.MapFilePath(inner,
            f => NeedsRebase(f) ? Elaborator.ResolveFileReference(f, baseDirectory) : f);
        if (mapped == inner) return null;
        return quoted || mapped.AsSpan().ContainsAny(' ', '\t') ? Quote(mapped) : mapped;
    }

    private static bool NeedsRebase(string path)
        => path.Length > 0 && !IsVirtual(path) && !Path.IsPathRooted(path);

    private static string Quote(string v) => $"\"{v}\"";
}
