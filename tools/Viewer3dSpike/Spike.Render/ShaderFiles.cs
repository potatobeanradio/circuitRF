namespace Viewer3dSpike.Render;

/// <summary>The committed shaders (tools/Viewer3dSpike/shaders/, generated from scene.wgsl by
/// tools/ShaderGen), found by walking up from the executable and then from the working directory.</summary>
public static class ShaderFiles
{
    public static string Find(string name)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            {
                var p = Path.Combine(d.FullName, "shaders", name);
                if (File.Exists(p)) return p;
            }
        throw new FileNotFoundException($"shaders/{name} not found above {AppContext.BaseDirectory} or {Environment.CurrentDirectory}");
    }
}
