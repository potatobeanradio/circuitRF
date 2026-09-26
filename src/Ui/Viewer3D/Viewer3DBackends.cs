// brief-em3d-28 R-em3d28-1b — the backend for this platform, chosen at run time: Metal on macOS,
// D3D11 on Windows, Vulkan on Linux (route A, D3 decided 2026-09-25). A platform where route A cannot
// present would take route B here, beside them (R-em3d28-1c).

namespace CircuitRF.Ui.Viewer3D;

public static class Viewer3DBackends
{
    public static Viewer3DBackend Create()
    {
        if (OperatingSystem.IsMacOS()) return new Metal.MetalViewer3DBackend();
        if (OperatingSystem.IsWindows()) return new D3D11.D3D11Viewer3DBackend();
        if (OperatingSystem.IsLinux()) return new Vulkan.VulkanViewer3DBackend();
        throw new Viewer3DPresentFault(
            $"The 3D view has no GPU backend for {System.Runtime.InteropServices.RuntimeInformation.OSDescription} in this build.");
    }
}
