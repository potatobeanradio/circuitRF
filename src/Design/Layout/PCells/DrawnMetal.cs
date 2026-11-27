// Owner: PCD-3 (brief-pcd-0 §5, seam 11; written by PCD-1's seams commit). Framework-free.

using CircuitRF.Core.Expressions;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.PCells;

/// <summary>brief-pcd-0 §4.3: the drawn-metal primitive MSHAPE, its parameter names and Stretch by Parameter.</summary>
public static class DrawnMetal
{
    public const string GeneratorId = "MSHAPE";
    public const string Form = "Form", FormPolygon = "polygon", FormPath = "path", Width = "W";
    public const string Model = "Model", ModelNone = "none", ModelOpen = "open";
    public const string SignalLayer = "SignalLayer", GroundReference = "GroundReference";
    public static string X(int i)      => $"X[{i}]";
    public static string Y(int i)      => $"Y[{i}]";
    public static string PinX(int k)   => $"PinX[{k}]";
    public static string PinY(int k)   => $"PinY[{k}]";
    public static string PinW(int k)   => $"PinW[{k}]";
    public static string PinDir(int k) => $"PinDir[{k}]";

    /// <summary>Seams-commit stub: false. PCD-3: <c>c.Symbol == SymbolKind.MShape</c>.</summary>
    public static bool Is(EditableComponent c) => false;

    /// <summary>A copy of shape whose Moving vertices follow binding.Parameter along binding.AxisDeg from binding.Anchor:
    /// unchanged at the current values, moved by δ when the parameter grows by δ; binding appended to Stretch.</summary>
    public static EditableComponent Stretch(EditableComponent shape, StretchBinding binding, Scope scope, Evaluator evaluator)
        => throw new NotImplementedException("PCD-3");
}
