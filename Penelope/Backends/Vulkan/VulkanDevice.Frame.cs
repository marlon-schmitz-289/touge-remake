using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Penelope.Backends.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    internal sealed class FrameData
    {
        public CommandPool Pool;
        // ImageAvailable + InFlight are per-frame-in-flight. The present-wait ("render finished")
        // semaphore is NOT here — it must be per-swapchain-image; see VulkanSwapchain.RenderFinished.
        public Semaphore ImageAvailable;
        public Silk.NET.Vulkan.Fence InFlight;
        public List<CommandBuffer> AllocatedBuffers = new();
        public List<VulkanCommandEncoder> LiveEncoders = new();
        // How many of AllocatedBuffers have been handed out this frame. Reset to 0 each BeginFrame;
        // buffers below the cursor are reused, the pool reset returns them to a recordable state.
        public int BufferCursor;
    }

    private FrameData CreateFrameData()
    {
        var f = new FrameData();

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = GraphicsQueueFamily,
            Flags = CommandPoolCreateFlags.TransientBit,
        };
        Vk.CreateCommandPool(Device, &poolInfo, null, out f.Pool).ThrowIfError();

        var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        Vk.CreateSemaphore(Device, &semInfo, null, out f.ImageAvailable).ThrowIfError();

        var fenceInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit, // start signaled so first BeginFrame doesn't block
        };
        Vk.CreateFence(Device, &fenceInfo, null, out f.InFlight).ThrowIfError();

        return f;
    }

    private void DestroyFrameData(FrameData f)
    {
        Vk.DestroyCommandPool(Device, f.Pool, null);
        Vk.DestroySemaphore(Device, f.ImageAvailable, null);
        Vk.DestroyFence(Device, f.InFlight, null);
    }

    /// <summary>Recreate the swapchain at the current drawable size. Safe point only (no frame in flight).</summary>
    private void RecreateSwapchain()
    {
        WaitIdle();
        _swapchain.Configure(new SwapchainDesc(
            (int)_swapchain.Extent.Width,
            (int)_swapchain.Extent.Height,
            VulkanConvert.FromVk(_swapchain.Format),
            FromVkPresentMode(_swapchain.PresentMode)));
        _needsRecreate = false;
    }

    /// <summary>
    ///     Begin a frame. Returns false if the swapchain was out of date and the frame must be
    ///     skipped (the swapchain is recreated here so the next call succeeds) — the caller must
    ///     NOT record/submit/present this frame.
    /// </summary>
    public bool BeginFrame()
    {
        if (_frameActive) throw new InvalidOperationException("Frame already active.");

        // Recreate at a safe point (nothing in flight) before acquiring, if a prior acquire/present
        // reported OutOfDate/Suboptimal. Configure re-reads the surface's current extent.
        if (_needsRecreate) RecreateSwapchain();

        var frame = _frames[_frameIndex];

        // Wait for prior frame in this slot
        var fence = frame.InFlight;
        Vk.WaitForFences(Device, 1, &fence, true, ulong.MaxValue);

        // Acquire image. On OutOfDate, flag for recreation and skip this frame (do NOT reset the
        // fence or advance _frameIndex — the slot's resources stay consistent for the retry).
        if (!_swapchain.AcquireNextImage(frame.ImageAvailable, out _))
        {
            _needsRecreate = true;
            _frameActive = false;
            return false;
        }

        // Reset fence after successful acquire (so AcquireNextImage throwing won't leave a stuck fence)
        Vk.ResetFences(Device, 1, &fence);

        // Recycle this frame slot's command buffers. Reset the POOL (not individual buffers) and
        // deliberately do NOT pass ReleaseResourcesBit: that flag hands the buffers' backing memory
        // back to the driver every frame, which then re-faults on next record — a periodic
        // frame-time spike (1% lows). A plain reset keeps the memory, so it's nearly free. The
        // buffers themselves are reused across frames (BufferCursor in BeginCommands) rather than
        // freed + reallocated, so they neither accumulate (RSS leak) nor churn the allocator.
        Vk.ResetCommandPool(Device, frame.Pool, 0);
        frame.BufferCursor = 0;
        foreach (var enc in frame.LiveEncoders) enc.Invalidate();
        frame.LiveEncoders.Clear();
        _submittedThisFrame = false;
        _presentSignaled = false;

        _frameActive = true;
        return true;
    }

    public ICommandEncoder BeginCommands(string? debugName = null)
    {
        if (!_frameActive) throw new InvalidOperationException("Call BeginFrame first.");
        var frame = _frames[_frameIndex];

        // Reuse a buffer allocated on an earlier frame when one is free this frame — the BeginFrame
        // pool reset returned it to the recordable state. Only allocate a new one when this frame
        // needs more concurrent encoders than any previous frame did.
        CommandBuffer cb = default;
        if (frame.BufferCursor < frame.AllocatedBuffers.Count)
        {
            cb = frame.AllocatedBuffers[frame.BufferCursor];
        }
        else
        {
            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = frame.Pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            Vk.AllocateCommandBuffers(Device, &allocInfo, &cb).ThrowIfError();
            frame.AllocatedBuffers.Add(cb);
        }
        frame.BufferCursor++;

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Vk.BeginCommandBuffer(cb, &beginInfo).ThrowIfError();

        var enc = new VulkanCommandEncoder(this, cb, debugName);
        frame.LiveEncoders.Add(enc);
        return enc;
    }

    public FenceHandle Submit(ICommandEncoder encoder)
    {
        var enc = (VulkanCommandEncoder)encoder;
        if (enc.IsInvalid) throw new InvalidOperationException("Encoder invalid (frame ended without submit).");

        // Transition swapchain image from ColorAttachmentOptimal to PresentSrcKHR, if the encoder
        // has written to it. Simpler strategy: always transition the current swapchain image at
        // submission end if the encoder flagged it.
        enc.Finish();

        var frame = _frames[_frameIndex];
        var cb = enc.CommandBuffer;

        // Compute first-submit LIVE (not latched at encoder creation): only the first submit of the
        // frame waits on ImageAvailable. Only the submit that actually rendered to the swapchain
        // image signals that image's present-wait semaphore — exactly one per frame, matching the
        // single QueuePresent. Same-queue submission order keeps non-present submits ordered.
        var isFirst = !_submittedThisFrame;
        var touchesSwapchain = enc.TouchedSwapchain;

        var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        var waitSem = frame.ImageAvailable;
        var signalSem = touchesSwapchain ? _swapchain.RenderFinished[_swapchain.CurrentImageIndex] : default;

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = isFirst ? 1u : 0u,
            PWaitSemaphores = isFirst ? &waitSem : null,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &cb,
            SignalSemaphoreCount = touchesSwapchain ? 1u : 0u,
            PSignalSemaphores = touchesSwapchain ? &signalSem : null,
        };

        // No fence here: a frame may submit several times; EndFrame signals InFlight after the last one.
        Vk.QueueSubmit(GraphicsQueue, 1, &submitInfo, default).ThrowIfError();
        _presentSignaled |= touchesSwapchain;
        enc.Invalidate();
        _submittedThisFrame = true;

        // Register a user-visible fence wrapper so the app can wait on it.
        // For simplicity, reuse the in-flight fence — but we can't destroy it. Return a null
        // fence for now; WaitForFence(Null) returns true. A richer impl would allocate a
        // one-shot VkFence per submit.
        return FenceHandle.Null;
    }

    public void EndFrame()
    {
        if (!_frameActive) return;
        if (!_presentSignaled)
        {
            // Nothing drew to the swapchain (offscreen-only frame, e.g. a FrameCapture shot): still move the
            // image to PresentSrc and signal its semaphore, or the present below waits forever (device lost).
            var enc = (VulkanCommandEncoder)BeginCommands("present-only");
            enc.MarkSwapchainView(CurrentSwapchainView.Id);
            Submit(enc);
        }
        // Empty batch: its fence signal waits for everything submitted before it on this queue.
        var signalOnly = new SubmitInfo { SType = StructureType.SubmitInfo };
        Vk.QueueSubmit(GraphicsQueue, 1, &signalOnly, _frames[_frameIndex].InFlight).ThrowIfError();
        var imageIndex = _swapchain.CurrentImageIndex;
        // Present waits on THIS image's present-wait semaphore (signaled by the swapchain-touching
        // submit above). On OutOfDate/Suboptimal, flag the swapchain for recreation next frame.
        if (!_swapchain.Present(_swapchain.RenderFinished[imageIndex], imageIndex))
            _needsRecreate = true;
        _frameActive = false;
        _frameIndex = (_frameIndex + 1) % FramesInFlightCount;
    }
}
