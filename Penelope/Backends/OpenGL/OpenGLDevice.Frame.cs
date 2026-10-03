using Silk.NET.OpenGL;

namespace Penelope.Backends.OpenGL;

public sealed unsafe partial class OpenGLDevice
{
    public bool BeginFrame()
    {
        if (_frameActive) throw new InvalidOperationException("Frame already active.");

        // Wait on the prior occupant of this slot. The fence was placed by EndFrame the last
        // time _frameSlot wrapped around to this index. First N frames have no prior fence.
        var prior = _slotFences[_frameSlot];
        if (prior != 0)
        {
            // Block until GPU finishes everything submitted before that fence — guarantees the
            // slot's per-frame buffers (TransientBufferRing slabs) are no longer being read.
            Gl.ClientWaitSync(prior, SyncObjectMask.Bit, ulong.MaxValue);
            Gl.DeleteSync(prior);
            _slotFences[_frameSlot] = 0;
        }

        // Pick up HiDPI / window-resize-driven backbuffer changes.
        var (dw, dh) = _context.GetDrawableSize();
        if (dw > 0 && dh > 0 && (dw != _swapchainWidth || dh != _swapchainHeight))
        {
            _swapchainWidth = dw;
            _swapchainHeight = dh;
            if (_textures.TryGetValue(_swapchainTextureId, out var tex))
            {
                tex.Width = dw;
                tex.Height = dh;
            }
        }

        _frameActive = true;
        return true;
    }

    public ICommandEncoder BeginCommands(string? debugName = null)
    {
        if (!_frameActive) throw new InvalidOperationException("Call BeginFrame first.");
        return new OpenGLCommandEncoder(this, debugName);
    }

    public FenceHandle Submit(ICommandEncoder encoder)
    {
        var enc = (OpenGLCommandEncoder)encoder;
        enc.Finish();

        // GL is single-context-thread / immediate-execution: by the time we get here, all commands
        // have been issued. We return FenceHandle.Null (like the Vulkan/Metal backends): no caller
        // consumes the submit fence, and DestroyFence is never invoked, so minting a glFenceSync +
        // _fences entry every Submit was an unbounded per-frame leak. Per-frame resource-reuse sync
        // is handled by the slot fences placed in EndFrame. Callers needing explicit completion sync
        // should use a dedicated fence API (see WaitForFence / IsFenceSignaled, Null-tolerant).
        return FenceHandle.Null;
    }

    public void EndFrame()
    {
        if (!_frameActive) return;
        NotifySwapBuffers();

        // Place a fence pinning everything submitted this frame. The next time we cycle back
        // to this slot, BeginFrame waits on this fence before reusing slot resources.
        _slotFences[_frameSlot] = Gl.FenceSync(SyncCondition.SyncGpuCommandsComplete, SyncBehaviorFlags.None);
        _frameSlot = (_frameSlot + 1) % FramesInFlightCount;

        _frameActive = false;
    }
}
