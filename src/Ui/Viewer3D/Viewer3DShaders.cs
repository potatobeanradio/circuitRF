// brief-em3d-28 R-em3d28-1c — the generated shaders, read from this assembly's embedded resources.
// All three come from Shaders/scene.wgsl through tools/ShaderGen; none is edited by hand, and
// Viewer3DFrameGateTests.Gate1b fails when one was generated from a different WGSL than the one committed.

using System.Reflection;

namespace CircuitRF.Ui.Viewer3D;

internal static class Viewer3DShaders
{
    private const string Prefix = "CircuitRF.Ui.Viewer3D.Shaders.";

    public static string Metal => Text("scene.metal");
    public static string Hlsl => Text("scene.hlsl");
    public static byte[] Spirv => Bytes("scene.spv");

    private static Stream Open(string name)
        => typeof(Viewer3DShaders).Assembly.GetManifestResourceStream(Prefix + name)
           ?? throw new Viewer3DPresentFault($"The 3D view's shader '{name}' is not in this build.");

    private static string Text(string name) { using var r = new StreamReader(Open(name)); return r.ReadToEnd(); }

    private static byte[] Bytes(string name)
    {
        using var s = Open(name);
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }
}
