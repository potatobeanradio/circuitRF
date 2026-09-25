// brief-em3d-4 R-em3d4-3 — the bond process's geometry: foot length, ball diameter, ball height.
//
// WHERE THE DEFAULTS LIVE IS OWNER DECISION D2, answered 2026-09-25: the `.wasm`. It is the assembly
// house's own document, and it already resolves per workspace with a per-`.wBond` override
// (`AssemblyRef`), which is exactly the override chain em-3d.md §6.6 asks for. THIS FILE IS THE ONLY
// CODE THAT KNOWS THAT. Moving the defaults (to the `.ctech`, say) changes Resolve and nothing else.
//
// The chain, each level visible to `explain` (brief 5) through WireBondValue.Source:
//   foot length    wire, then array, then the assembly rules' DefaultFootLengthNm, then built in;
//   ball diameter  the assembly rules' DefaultBallDiameterNm, then built in;
//   ball height    the assembly rules' DefaultBallHeightNm, then built in.

using CircuitRF.Design.Layout.Assembly;
using CircuitRF.Design.Workspace;
using CircuitRF.WBond;

namespace CircuitRF.Design.Layout.Em3d;

/// <summary>Which level of the chain a bond-process value came from.</summary>
public enum WireBondValueSource
{
    /// <summary>The wire's own field.</summary>
    Wire,

    /// <summary>The wire's array.</summary>
    Array,

    /// <summary>The resolved <c>.wasm</c>'s default.</summary>
    AssemblyRules,

    /// <summary>Nothing stated one, so the built-in starting value was used — and the run says so.</summary>
    BuiltIn,
}

/// <summary>One resolved bond-process length, in nanometres, and where it came from.</summary>
public readonly record struct WireBondValue(long Nm, WireBondValueSource Source);

/// <summary>A wire's resolved bond-process geometry. <see cref="AssemblyRules"/> is the <c>.wasm</c>
/// that was consulted, when one resolved, so a note can name the file to edit.</summary>
public sealed record WireBondProcessValues(
    WireBondValue FootLength, WireBondValue BallDiameter, WireBondValue BallHeight,
    WasmResolution AssemblyRules);

/// <summary>
/// Where a <c>.wBond</c> sits, for resolving the assembly rules it bonds under. The resolution is made
/// once per <see cref="WBondDesign.AssemblyRef"/> and kept, so asking per wire costs nothing.
/// </summary>
public sealed class WireBondWorkspace
{
    private readonly string? _wbondPath;
    private readonly string? _cwsPath;
    private readonly WasmCache _cache;
    private readonly Dictionary<string, WasmResolution> _byRef = new(StringComparer.Ordinal);

    /// <param name="wbondPath">The <c>.wBond</c>'s absolute path; its directory anchors
    /// <see cref="WBondDesign.AssemblyRef"/>. Null for a design that is not a file.</param>
    /// <param name="workspaceCwsPath">The workspace's <c>.cws</c>, whose <c>DefaultAssemblyRef</c> is
    /// the fallback. Null when there is none.</param>
    public WireBondWorkspace(string? wbondPath, string? workspaceCwsPath, WasmCache? cache = null)
    {
        _wbondPath = wbondPath;
        _cwsPath   = workspaceCwsPath;
        _cache     = cache ?? new WasmCache();
    }

    /// <summary>The workspace a <c>.wBond</c> belongs to, found by the ordinary walk-up.</summary>
    public static WireBondWorkspace ForFile(string wbondPath) =>
        new(wbondPath, WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(wbondPath)));

    /// <summary>No files at all: nothing resolves, and every default is the built-in one.</summary>
    public static WireBondWorkspace None => new(null, null);

    internal WasmResolution RulesFor(WBondDesign design)
    {
        string key = design.AssemblyRef ?? "";
        if (_byRef.TryGetValue(key, out var hit)) return hit;

        string? defaultRef = null;
        if (_cwsPath is not null)
            try { defaultRef = WorkspacePersistence.LoadFromFile(_cwsPath).DefaultAssemblyRef; }
            catch { /* a corrupt .cws states no default — ResolveWorkspaceAssemblyRules' rule */ }

        var resolved = WasmResolver.Resolve(
            design.AssemblyRef, _wbondPath is null ? null : Path.GetDirectoryName(_wbondPath),
            _cwsPath is null ? null : Path.GetDirectoryName(_cwsPath), defaultRef, _cache);
        _byRef[key] = resolved;
        return resolved;
    }
}

public static class WireBondProcess
{
    // ── The built-in starting values (R-em3d4-3b) ────────────────────────────────────────────────
    //
    // UNVERIFIED. §6.6's "about twice the diameter" for the foot, and conventional first guesses for
    // the ball. They stand until the owner's assembly data (brief-em3d-1 §1) replaces them, and every
    // run that uses one says so — a silently-used guess is the one outcome to avoid. Recorded as
    // unverified in src/Design/RESOLVED.md.

    /// <summary>Wedge-foot length, in wire diameters. Unverified.</summary>
    public const double BuiltInFootLengthDiameters = 2.0;

    /// <summary>Flattened-ball diameter, in wire diameters. Unverified.</summary>
    public const double BuiltInBallDiameterDiameters = 2.5;

    /// <summary>Flattened-ball height, in wire diameters. Unverified.</summary>
    public const double BuiltInBallHeightDiameters = 0.5;

    /// <summary>
    /// The foot length, ball diameter and ball height that apply to <paramref name="wire"/> — the one
    /// function that knows where the defaults live.
    /// </summary>
    public static WireBondProcessValues Resolve(Wire wire, WireArray array, WBondDesign design,
                                                WireBondWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(array);
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(workspace);

        var rules = workspace.RulesFor(design);
        var wasm  = rules.Rules;
        long d = wire.DiameterNm;

        // A non-positive stated value is not a length; WasmValidation reports it, and here it simply
        // does not count as stated.
        static long? Stated(long? v) => v is > 0 ? v : null;

        WireBondValue Chain(long? fromWasm, double builtInDiameters) =>
            Stated(fromWasm) is { } w ? new(w, WireBondValueSource.AssemblyRules)
                                      : new((long)Math.Round(builtInDiameters * d), WireBondValueSource.BuiltIn);

        var foot = Stated(wire.FootLengthNm) is { } fw ? new WireBondValue(fw, WireBondValueSource.Wire)
                 : Stated(array.FootLengthNm) is { } fa ? new WireBondValue(fa, WireBondValueSource.Array)
                 : Chain(wasm?.DefaultFootLengthNm, BuiltInFootLengthDiameters);

        return new WireBondProcessValues(
            foot,
            Chain(wasm?.DefaultBallDiameterNm, BuiltInBallDiameterDiameters),
            Chain(wasm?.DefaultBallHeightNm, BuiltInBallHeightDiameters),
            rules);
    }
}
