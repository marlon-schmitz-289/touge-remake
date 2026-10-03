using System.Runtime.InteropServices;
using Penelope.Backends.Vulkan;
using Silk.NET.Core.Native;
using Silk.NET.SDL;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Kansei.Windowing;

/// <summary>
///     Glue between SDL and Penelope's Vulkan backend. Queries SDL for the required Vulkan instance
///     extensions, and builds a <see cref="VulkanDevice.SurfaceFactory"/> that creates a
///     <see cref="SurfaceKHR"/> for the SDL window on demand.
/// </summary>
public static unsafe class VulkanSurfaceBridge
{
    /// <summary>
    ///     Collect everything Penelope's <see cref="VulkanDevice.Create"/> needs for an SDL window.
    /// </summary>
    public static (string[] instanceExtensions, VulkanDevice.SurfaceFactory factory)
        Prepare(Sdl sdl, Window* window)
    {
        // Query count
        uint count = 0;
        if (sdl.VulkanGetInstanceExtensions(window, &count, (byte**)null) == SdlBool.False)
            throw new Exception($"SDL_Vulkan_GetInstanceExtensions failed: {sdl.GetErrorS()}");

        var names = new string[count];
        var ptrs = stackalloc byte*[(int)count];
        if (sdl.VulkanGetInstanceExtensions(window, &count, ptrs) == SdlBool.False)
            throw new Exception($"SDL_Vulkan_GetInstanceExtensions failed: {sdl.GetErrorS()}");

        for (uint i = 0; i < count; i++)
            names[i] = Marshal.PtrToStringAnsi((nint)ptrs[i]) ?? "";

        VulkanDevice.SurfaceFactory factory = instance =>
        {
            // SDL speaks in opaque handles. Cast Silk.NET.Vulkan.Instance → VkHandle;
            // SDL writes a VkNonDispatchableHandle we cast back to SurfaceKHR.
            var instHandle = new VkHandle(instance.Handle);
            VkNonDispatchableHandle surfaceHandle;
            if (sdl.VulkanCreateSurface(window, instHandle, &surfaceHandle) == SdlBool.False)
                throw new Exception($"SDL_Vulkan_CreateSurface failed: {sdl.GetErrorS()}");
            return new SurfaceKHR(surfaceHandle.Handle);
        };

        return (names, factory);
    }
}
