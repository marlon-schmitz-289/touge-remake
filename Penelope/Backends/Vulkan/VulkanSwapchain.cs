using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Penelope.Backends.Vulkan;

internal sealed unsafe class VulkanSwapchain : IDisposable
{
    private readonly Vk _vk;
    private readonly VulkanDevice _device;
    private readonly KhrSwapchain _swapchainExt;
    private readonly KhrSurface _surfaceExt;
    private readonly SurfaceKHR _surface;

    public SwapchainKHR Swapchain;
    public Format Format;
    public ColorSpaceKHR ColorSpace;
    public Extent2D Extent;
    public PresentModeKHR PresentMode;
    public Image[] Images = [];
    /// <summary>Penelope texture-view handle IDs wrapping the swapchain images (one per image).</summary>
    public ulong[] ImageViewIds = [];
    /// <summary>Penelope texture image IDs owning the swapchain images (one per image).</summary>
    public ulong[] ImageIds = [];

    /// <summary>
    ///     Present-wait ("render finished") binary semaphores, one PER SWAPCHAIN IMAGE (not per
    ///     frame-in-flight). vkQueuePresentKHR waits on this; reuse of slot N is gated by the next
    ///     vkAcquireNextImageKHR returning N, which is the only spec-valid proof the prior present
    ///     of N completed. Sizing this per-frame-in-flight instead violates VUID-vkQueueSubmit-
    ///     pSignalSemaphores-00067 and stalls the present queue on RADV.
    /// </summary>
    public Semaphore[] RenderFinished = [];

    public uint CurrentImageIndex;

    public VulkanSwapchain(
        Vk vk,
        VulkanDevice device,
        KhrSwapchain swapchainExt,
        KhrSurface surfaceExt,
        SurfaceKHR surface)
    {
        _vk = vk;
        _device = device;
        _swapchainExt = swapchainExt;
        _surfaceExt = surfaceExt;
        _surface = surface;
    }

    public void Configure(in SwapchainDesc desc)
    {
        // Query surface caps
        _surfaceExt.GetPhysicalDeviceSurfaceCapabilities(_device.PhysicalDevice, _surface, out var caps);

        // Choose format
        uint formatCount = 0;
        _surfaceExt.GetPhysicalDeviceSurfaceFormats(_device.PhysicalDevice, _surface, &formatCount, null);
        var formats = new SurfaceFormatKHR[formatCount];
        fixed (SurfaceFormatKHR* pFmt = formats)
            _surfaceExt.GetPhysicalDeviceSurfaceFormats(_device.PhysicalDevice, _surface, &formatCount, pFmt);

        var wanted = VulkanConvert.ToVk(desc.Format);
        var chosen = formats[0];
        foreach (var f in formats)
        {
            if (f.Format == wanted && f.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            {
                chosen = f;
                break;
            }
        }

        Format = chosen.Format;
        ColorSpace = chosen.ColorSpace;

        // Choose present mode
        uint pmCount = 0;
        _surfaceExt.GetPhysicalDeviceSurfacePresentModes(_device.PhysicalDevice, _surface, &pmCount, null);
        var modes = new PresentModeKHR[pmCount];
        fixed (PresentModeKHR* pPm = modes)
            _surfaceExt.GetPhysicalDeviceSurfacePresentModes(_device.PhysicalDevice, _surface, &pmCount, pPm);

        var wantedMode = VulkanConvert.ToVk(desc.PresentMode);
        PresentMode = PresentModeKHR.FifoKhr; // guaranteed
        foreach (var m in modes)
        {
            if (m == wantedMode)
            {
                PresentMode = m;
                break;
            }
        }

        // Extent
        if (caps.CurrentExtent.Width != uint.MaxValue)
        {
            Extent = caps.CurrentExtent;
        }
        else
        {
            Extent = new Extent2D(
                (uint)Math.Clamp(desc.Width, (int)caps.MinImageExtent.Width, (int)caps.MaxImageExtent.Width),
                (uint)Math.Clamp(desc.Height, (int)caps.MinImageExtent.Height, (int)caps.MaxImageExtent.Height));
        }

        uint imageCount = Math.Max((uint)desc.MinImageCount, caps.MinImageCount);
        if (caps.MaxImageCount > 0 && imageCount > caps.MaxImageCount)
            imageCount = caps.MaxImageCount;

        var oldSwapchain = Swapchain;

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = Format,
            ImageColorSpace = ColorSpace,
            ImageExtent = Extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = PresentMode,
            Clipped = true,
            OldSwapchain = oldSwapchain,
        };

        SwapchainKHR newSwap;
        _swapchainExt.CreateSwapchain(_device.Device, &createInfo, null, &newSwap).ThrowIfError();

        // Destroy old resources
        DestroyImageViewsAndImages();
        if (oldSwapchain.Handle != 0)
            _swapchainExt.DestroySwapchain(_device.Device, oldSwapchain, null);

        Swapchain = newSwap;

        // Grab images
        uint gotCount = 0;
        _swapchainExt.GetSwapchainImages(_device.Device, Swapchain, &gotCount, null);
        Images = new Image[gotCount];
        fixed (Image* pImg = Images)
            _swapchainExt.GetSwapchainImages(_device.Device, Swapchain, &gotCount, pImg);

        // Register images + views with the device resource tables
        ImageIds = new ulong[Images.Length];
        ImageViewIds = new ulong[Images.Length];
        for (var i = 0; i < Images.Length; i++)
        {
            var img = new VulkanImage
            {
                Image = Images[i],
                Memory = default,
                Format = Format,
                Extent = new Extent3D(Extent.Width, Extent.Height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Usage = TextureUsage.ColorAttachment,
                Dimension = TextureDimension.Tex2D,
                CurrentLayout = ImageLayout.Undefined,
                IsSwapchainImage = true,
            };

            // Create default view
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = Images[i],
                ViewType = ImageViewType.Type2D,
                Format = Format,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity, ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity, ComponentSwizzle.Identity),
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            ImageView view;
            _vk.CreateImageView(_device.Device, &viewInfo, null, &view).ThrowIfError();

            var viewObj = new VulkanImageView
            {
                View = view,
                Format = Format,
                Aspect = ImageAspectFlags.ColorBit,
                BaseMip = 0,
                MipCount = 1,
                BaseLayer = 0,
                LayerCount = 1,
            };

            var imageId = _device.RegisterImage(img);
            img.DefaultViewId = _device.RegisterImageView(viewObj);
            viewObj.ImageId = imageId;

            ImageIds[i] = imageId;
            ImageViewIds[i] = img.DefaultViewId;
        }

        // One present-wait semaphore per swapchain image (see RenderFinished docs). Recreated here
        // on every (re)configure; the old set is destroyed in DestroyImageViewsAndImages above.
        RenderFinished = new Semaphore[Images.Length];
        var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        for (var i = 0; i < Images.Length; i++)
        {
            _vk.CreateSemaphore(_device.Device, &semInfo, null, out var sem).ThrowIfError();
            RenderFinished[i] = sem;
        }
    }

    /// <summary>Acquire next image. Returns true on success; false on OutOfDate (caller should resize).</summary>
    public bool AcquireNextImage(Semaphore signalSem, out uint imageIndex)
    {
        uint idx = 0;
        var result = _swapchainExt.AcquireNextImage(
            _device.Device, Swapchain, ulong.MaxValue, signalSem, default, &idx);
        CurrentImageIndex = idx;
        imageIndex = idx;
        if (result == Result.ErrorOutOfDateKhr) return false;
        if (result != Result.Success && result != Result.SuboptimalKhr) result.ThrowIfError();
        return true;
    }

    public bool Present(Semaphore waitSem, uint imageIndex)
    {
        var swap = Swapchain;
        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSem,
            SwapchainCount = 1,
            PSwapchains = &swap,
            PImageIndices = &imageIndex,
        };
        var result = _swapchainExt.QueuePresent(_device.GraphicsQueue, &presentInfo);
        if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr) return false;
        result.ThrowIfError();
        return true;
    }

    private void DestroyImageViewsAndImages()
    {
        // Present-wait semaphores are per-image, so they're recreated with the images. Callers
        // (Configure on recreate, Dispose) WaitIdle first, so none are in use here.
        foreach (var sem in RenderFinished)
            if (sem.Handle != 0)
                _vk.DestroySemaphore(_device.Device, sem, null);
        RenderFinished = [];

        if (ImageViewIds.Length == 0) return;
        foreach (var vid in ImageViewIds)
        {
            if (_device.TryGetImageView(vid, out var v))
            {
                _vk.DestroyImageView(_device.Device, v.View, null);
                _device.UnregisterImageView(vid);
            }
        }
        foreach (var iid in ImageIds)
        {
            _device.UnregisterImage(iid); // swapchain-owned image; no vkDestroyImage
        }
        ImageViewIds = [];
        ImageIds = [];
    }

    public void Dispose()
    {
        DestroyImageViewsAndImages();
        if (Swapchain.Handle != 0)
            _swapchainExt.DestroySwapchain(_device.Device, Swapchain, null);
    }
}

internal static class ResultExtensions
{
    public static void ThrowIfError(this Result r)
    {
        if (r != Result.Success)
            throw new Exception($"Vulkan call failed: {r}");
    }
}
