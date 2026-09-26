using Viewer3dSpike.Render.Metal;
using static Viewer3dSpike.Render.Metal.ObjC;

namespace Viewer3dSpike.Render.Wgpu;

/// <summary>
/// Route B's macOS present step: copy wgpu's finished frame into a shared (IOSurface-backed) image,
/// then signal the compositor.
///
/// The obvious design — encode the copy on wgpu's own MTLCommandQueue so it is ordered after wgpu's
/// submit on the GPU — is not available: in wgpu-native v29.0.1.1 wgpuQueueGetNativeMetalCommandQueue
/// returns NULL (the device and texture getters work). A message to a nil queue is a silent no-op in
/// Objective-C, so the pane's first version committed nothing, never signalled, and the compositor
/// waited on the ready event forever. So the copy runs on a queue of our own on wgpu's device, and
/// ordering comes from the CPU: wgpuDevicePoll(wait) blocks the RENDER thread (never the UI thread)
/// until wgpu's submitted work is done.
/// Shared by the Avalonia pane and the harness's <c>wgpu-bridge</c> route.
/// </summary>
public static unsafe class WgpuMetalBridge
{
    public static (nint Queue, nint Src) Handles(WgpuRenderer wg) =>
        ((nint)wg.Api.QueueGetNativeMetalCommandQueue(wg.Queue), (nint)wg.Api.TextureGetNativeMetalTexture(wg.ColorTexture));

    static nint _device, _queue;
    static nint OwnQueue(WgpuRenderer wg)
    {
        nint device = (nint)wg.Api.DeviceGetNativeMetalDevice(wg.Device);
        if (device != _device) { _device = device; _queue = Send(device, S.newCommandQueue); }
        return _queue;
    }

    public static void Present(WgpuRenderer wg, nint dst, nint readyEvent, ulong value, bool waitCompleted)
    {
        nint pool = PoolPush();
        nint src = (nint)wg.Api.TextureGetNativeMetalTexture(wg.ColorTexture);
        nint queue = OwnQueue(wg);
        if (queue == 0 || src == 0) { PoolPop(pool); throw new InvalidOperationException($"no native Metal handle (queue {queue:X}, texture {src:X})"); }
        wg.Api.DevicePoll(wg.Device, 1, null);      // wgpu's frame is finished before the copy reads it
        nint cb = Send(queue, S.commandBuffer);
        nint blit = Send(cb, S.blitCommandEncoder);
        Send(blit, S.copyFromTextureToTexture, src, dst);
        Send(blit, S.endEncoding);
        if (readyEvent != 0) ((delegate* unmanaged<nint, nint, nint, ulong, void>)MsgSend)(cb, S.encodeSignalEvent, readyEvent, value);
        Send(cb, S.commit);
        if (waitCompleted)
        {
            Send(cb, S.waitUntilCompleted);
            nint err = Send(cb, Sel("error"));
            if (err != 0) { string d = Describe(err); PoolPop(pool); throw new InvalidOperationException("present blit failed: " + d); }
        }
        PoolPop(pool);
    }

    public static ulong SignaledValue(nint evt) => ((delegate* unmanaged<nint, nint, ulong>)MsgSend)(evt, S.signaledValue);
}
