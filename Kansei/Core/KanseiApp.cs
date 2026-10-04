using System.Runtime.Versioning;
using Kansei.Input;
using Kansei.Windowing;
using Penelope;
using Penelope.Backends.Metal;
using Penelope.Backends.OpenGL;
using Penelope.Backends.Vulkan;
using SharpMetal.QuartzCore;
using Silk.NET.SDL;

namespace Kansei.Core;

/// <summary>Desktop entry point: SDL window + input, Penelope device, fixed-tick loop.</summary>
public static class KanseiApp
{
    /// <summary>
    ///     Backend from <c>--backend vulkan|opengl|metal</c> or <c>PENELOPE_BACKEND</c>;
    ///     default Metal on macOS (no MoltenVK needed), Vulkan elsewhere.
    /// </summary>
    public static GraphicsBackend ResolveBackend(string[] args)
    {
        var s = Environment.GetEnvironmentVariable("PENELOPE_BACKEND");
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--backend") s = args[i + 1];
        return s?.ToLowerInvariant() switch
        {
            "opengl" or "gl" => GraphicsBackend.OpenGL,
            "vulkan" or "vk" => GraphicsBackend.Vulkan,
            "metal" or "mtl" => GraphicsBackend.Metal,
            _ => OperatingSystem.IsMacOS() ? GraphicsBackend.Metal : GraphicsBackend.Vulkan,
        };
    }

    public static unsafe void Run(KanseiGame game, WindowSettings settings)
    {
        // Penelope's GL backend needs 4.5 core (glClipControl, DSA, GLSL 450); macOS stops at 4.1.
        if (settings.Backend == GraphicsBackend.OpenGL && OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("OpenGL needs a 4.5 core context, macOS only offers 4.1: use --backend metal or vulkan (MoltenVK).");
        var sdl = Sdl.GetApi();
        if (sdl.Init(Sdl.InitVideo | Sdl.InitGamecontroller | Sdl.InitHaptic) < 0)
            throw new Exception($"SDL init failed: {sdl.GetErrorS()}");
        try
        {
            using var window = new GameWindow(sdl, settings);
            var input = new InputSnapshot(sdl);
            using var device = CreateDevice(sdl, window, settings);
            Console.WriteLine($"[Kansei] Backend: {settings.Backend} — {device.Adapter.DeviceName}");

            game.Window = window;
            game.Device = device;
            game.Input = input;
            game.Load();
            Loop(game, sdl, window, input, device);
            device.WaitIdle();
            game.Dispose();
        }
        finally
        {
            sdl.Quit();
        }
    }

    private static void Loop(KanseiGame game, Sdl sdl, GameWindow window, InputSnapshot input, IPenelopeDevice device)
    {
        var freq = (double)sdl.GetPerformanceFrequency();
        var start = sdl.GetPerformanceCounter();
        var last = start;
        var step = 1.0 / game.TickRate;
        var acc = 0.0;
        long frame = 0;

        while (!window.ShouldClose)
        {
            var now = sdl.GetPerformanceCounter();
            if (game.FrameCap > 0)
            {
                // sleep most of the remaining frame, spin the last ~1 ms (sleep granularity)
                var due = last + (ulong)(freq / game.FrameCap);
                while (now < due)
                {
                    var left = (due - now) / freq;
                    if (left > 0.002) System.Threading.Thread.Sleep(TimeSpan.FromSeconds(left - 0.0015));
                    else System.Threading.Thread.SpinWait(50);
                    now = sdl.GetPerformanceCounter();
                }
            }
            var dt = (now - last) / freq;
            last = now;
            acc += Math.Min(dt, 0.25); // ponytail: drop time after hitches instead of spiral-of-death catch-up

            input.BeginFrame();
            window.PollEvents(input.ProcessEvent);
            input.EndFrame();
            if (input.Keyboard.IsKeyPressed(Key.F11)) window.ToggleFullscreen();
            if (window.WasResized) device.ResizeSwapchain(window.DrawableWidth, window.DrawableHeight);

            var cpu0 = sdl.GetPerformanceCounter();
            while (acc >= step)
            {
                game.Tick((float)step);
                acc -= step;
            }

            var time = new GameTime { DeltaTime = (float)dt, TotalTime = (now - start) / freq, FrameCount = ++frame };
            game.Update(time);
            var cpu1 = sdl.GetPerformanceCounter();

            if (!device.BeginFrame()) continue;
            var cpu2 = sdl.GetPerformanceCounter();
            var encoder = device.BeginCommands("frame");
            game.Render(new FrameContext(device, encoder,
                new Viewport(0, 0, window.DrawableWidth, window.DrawableHeight), time, (float)(acc / step)));
            device.Submit(encoder);
            device.EndFrame();
            game.CpuMs = (cpu1 - cpu0 + sdl.GetPerformanceCounter() - cpu2) * 1000 / freq;
        }
    }

    private static unsafe IPenelopeDevice CreateDevice(Sdl sdl, GameWindow window, WindowSettings settings)
    {
        var deviceDesc = new DeviceDesc(AdapterPreference.HighPerformance, EnableValidation: Environment.GetEnvironmentVariable("PENELOPE_VALIDATION") == "1", DebugName: "Kansei");
        var swapDesc = new SwapchainDesc(window.PixelWidth, window.PixelHeight, TextureFormat.Bgra8Unorm,
            settings.VSync ? PresentMode.Mailbox : PresentMode.Immediate);
        switch (settings.Backend)
        {
            case GraphicsBackend.Vulkan:
                var (ext, factory) = VulkanSurfaceBridge.Prepare(sdl, window.NativeHandle);
                return VulkanDevice.Create(factory, deviceDesc, swapDesc, ext);
            case GraphicsBackend.OpenGL:
                return OpenGLDevice.Create(new OpenGLDevice.GLContextProvider
                {
                    GetProcAddress = window.GLGetProcAddress,
                    SwapBuffers = window.SwapBuffers,
                    GetDrawableSize = window.GetDrawableSize,
                }, deviceDesc, swapDesc);
            case GraphicsBackend.Metal:
#pragma warning disable CA1416 // guarded by the OS check inside
                return CreateMetal(window, deviceDesc, swapDesc);
#pragma warning restore CA1416
            default:
                throw new PlatformNotSupportedException(settings.Backend.ToString());
        }
    }

    [SupportedOSPlatform("macos")]
    private static IPenelopeDevice CreateMetal(GameWindow window, in DeviceDesc deviceDesc, in SwapchainDesc swapDesc)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Metal requires macOS");
        return MetalDevice.Create(new MetalDevice.MetalContextProvider
        {
            Layer = new CAMetalLayer(window.GetMetalLayer()),
            GetDrawableSize = window.GetDrawableSize,
        }, deviceDesc, swapDesc);
    }
}
