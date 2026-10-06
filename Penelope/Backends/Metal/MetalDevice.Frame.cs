using System.Runtime.Versioning;
using SharpMetal.Metal;

namespace Penelope.Backends.Metal;

[SupportedOSPlatform("macos")]
public sealed unsafe partial class MetalDevice
{
    public bool Offscreen { get; set; }

    public bool BeginFrame()
    {
        if (_frameActive) throw new InvalidOperationException("Frame already active.");

        // Wait on the prior occupant of this slot — guarantees per-slot buffers are GPU-free
        // before any caller-side per-frame allocator hands out the slot's slab again.
        var prior = _slotBuffers[_frameSlot];
        if (prior.NativePtr != 0)
        {
            prior.WaitUntilCompleted();
            _slotBuffers[_frameSlot] = default;
        }

        // Pick up host-driven resize before acquiring the drawable.
        var (dw, dh) = _context.GetDrawableSize();
        if (dw > 0 && dh > 0 && (dw != _swapchainWidth || dh != _swapchainHeight))
        {
            _swapchainWidth = dw;
            _swapchainHeight = dh;
            if (_textures.TryGetValue(_swapchainTextureId, out var t))
            { t.Width = dw; t.Height = dh; }
        }

        if (!Offscreen)
        {
            _currentDrawable = Layer.NextDrawable;
            if (_currentDrawable.NativePtr == 0)
                // Layer wasn't ready (window minimised, drawable timeout). Skip frame.
                return false;
        }

        // Patch the swapchain proxy texture so any pass that uses CurrentSwapchainView this
        // frame sees the right MTLTexture. The drawable's texture lifecycle is owned by
        // CAMetalDrawable — we don't dispose it.
        var proxy = _textures[_swapchainTextureId];
        proxy.Texture = _currentDrawable.Texture;

        _currentCommandBuffer = Queue.CommandBuffer();
        _frameActive = true;
        return true;
    }

    public ICommandEncoder BeginCommands(string? debugName = null)
    {
        if (!_frameActive) throw new InvalidOperationException("Call BeginFrame first.");
        return new MetalCommandEncoder(this, _currentCommandBuffer, debugName);
    }

    public FenceHandle Submit(ICommandEncoder encoder)
    {
        var enc = (MetalCommandEncoder)encoder;
        enc.Finish();
        // Single-command-buffer-per-frame model: the actual Commit happens in EndFrame so all
        // encoders for the frame land on the same MTLCommandBuffer (matching the Vulkan/OpenGL
        // backends' behaviour where Submit is a no-op-style record point).
        return FenceHandle.Null;
    }

    public void EndFrame()
    {
        if (!_frameActive) return;
        if (_currentDrawable.NativePtr != 0)
            _currentCommandBuffer.PresentDrawable(_currentDrawable);
        _currentCommandBuffer.Commit();

        // Park the just-committed command buffer on this slot so the next BeginFrame on the
        // same slot can wait on it before reusing slot-owned per-frame buffers.
        _slotBuffers[_frameSlot] = _currentCommandBuffer;
        _frameSlot = (_frameSlot + 1) % FramesInFlightCount;
        FrameCount++;

        _frameActive = false;
        _currentDrawable = default;
        _currentCommandBuffer = default;
    }
}
