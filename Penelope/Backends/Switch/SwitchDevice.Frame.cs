namespace Penelope.Backends.Switch;

public sealed unsafe partial class SwitchDevice
{
    public bool BeginFrame()
        => throw NvnNotImplemented(nameof(BeginFrame),
            "nvnWindowAcquireTexture(NvnWindow, sync, &textureIndex) — block until a swapchain " +
            "texture is available, then patch the swapchain proxy texture's NvnTexture pointer " +
            "to the acquired texture so CurrentSwapchainView resolves correctly this frame.");

    public ICommandEncoder BeginCommands(string? debugName = null)
        => throw NvnNotImplemented(nameof(BeginCommands),
            "Allocate / reuse an NVNcommandBuffer (typically pool-recycled per frame), " +
            "nvnCommandBufferBeginRecording, wrap in a SwitchCommandEncoder.");

    public FenceHandle Submit(ICommandEncoder encoder)
        => throw NvnNotImplemented(nameof(Submit),
            "nvnCommandBufferEndRecording, nvnQueueSubmitCommands(NvnQueue, 1, &cmdHandle), " +
            "nvnQueueFenceSync to signal a NVNsync we hand back as a Penelope fence.");

    public void EndFrame()
        => throw NvnNotImplemented(nameof(EndFrame),
            "nvnQueuePresentTexture(NvnQueue, NvnWindow, currentTextureIndex) + nvnQueueFlush.");
}
