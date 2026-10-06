using Silk.NET.SDL;

namespace Kansei.Windowing;

/// <summary>
///     SDL window — opened with either Vulkan or OpenGL surface flags depending on
///     <see cref="WindowSettings.Backend"/>. Penelope's Vulkan backend creates its surface through
///     the window handle exposed here (via <see cref="VulkanSurfaceBridge"/>); the OpenGL backend
///     uses the GL context this class creates and exposes via <see cref="GLContext"/>.
///
///     Three coordinate systems coexist:
///     <list type="bullet">
///       <item><b>Logical</b> — game-internal resolution (<see cref="LogicalWidth"/> ×
///       <see cref="LogicalHeight"/>), fixed at construction. Game/UI math uses this.</item>
///       <item><b>Window</b> — OS window coords (<see cref="WindowWidth"/> ×
///       <see cref="WindowHeight"/>). SDL mouse events arrive in this space.</item>
///       <item><b>Drawable</b> — actual GPU framebuffer pixels (<see cref="DrawableWidth"/>
///       × <see cref="DrawableHeight"/>). On HiDPI displays this is N× the window size; the
///       swapchain + scissor must use this.</item>
///     </list>
///     <see cref="DpiScale"/> = Drawable / Window. Defaults to 1.0 on non-HiDPI displays.
/// </summary>
public sealed class GameWindow : IDisposable
{
    private readonly Sdl _sdl;
    internal unsafe Window* NativeHandle { get; }

    /// <summary>
    ///     Underlying SDL handle. Public for the Vulkan surface bridge + future host-platform
    ///     code; non-SDL platforms (Switch) won't have a window of this type at all so the
    ///     accessor is irrelevant there.
    /// </summary>
    public Sdl Sdl => _sdl;

    /// <summary>
    ///     SDL_GLContext for the OpenGL backend. Null when running under Vulkan. The engine
    ///     forwards this (and the SDL functions for proc-loading + swap) to Penelope's
    ///     OpenGLDevice.GLContextProvider.
    /// </summary>
    public unsafe void* GLContext { get; private set; }

    /// <summary>
    ///     SDL_MetalView handle for the Metal backend (null otherwise). The engine pulls a
    ///     CAMetalLayer pointer off this view via SDL_Metal_GetLayer and hands it to Penelope's
    ///     MetalDevice.MetalContextProvider.
    /// </summary>
    public unsafe void* MetalView { get; private set; }

    public GraphicsBackend Backend { get; }

    public unsafe GameWindow(Sdl sdl, WindowSettings settings)
    {
        _sdl = sdl;
        Backend = settings.Backend;

        LogicalWidth = settings.BaseWidth;
        LogicalHeight = settings.BaseHeight;

        var initW = settings.InitialWindowWidth;
        var initH = settings.InitialWindowHeight;

        var flags = (uint)((settings.Hidden ? WindowFlags.Hidden : WindowFlags.Shown) | WindowFlags.Resizable);
        flags |= Backend switch
        {
            GraphicsBackend.Vulkan => (uint)WindowFlags.Vulkan,
            GraphicsBackend.OpenGL => (uint)WindowFlags.Opengl,
            GraphicsBackend.Metal => (uint)WindowFlags.Metal,
            _ => 0u,
        };
        if (settings.AllowHighDpi) flags |= (uint)WindowFlags.AllowHighdpi;
        flags |= settings.FullscreenMode switch
        {
            FullscreenMode.Borderless => SdlWindowFullscreenDesktop,
            FullscreenMode.Exclusive => SdlWindowFullscreen,
            _ => 0u
        };

        FullscreenMode = settings.FullscreenMode;

        if (Backend == GraphicsBackend.OpenGL)
        {
            // Request GL 4.5 core profile — required by Penelope's OpenGL backend (glClipControl
            // + ARB_direct_state_access subset). Set BEFORE CreateWindow.
            _sdl.GLSetAttribute(GLattr.ContextMajorVersion, 4);
            _sdl.GLSetAttribute(GLattr.ContextMinorVersion, 5);
            _sdl.GLSetAttribute(GLattr.ContextProfileMask, (int)GLprofile.Core);
            _sdl.GLSetAttribute(GLattr.Doublebuffer, 1);
            _sdl.GLSetAttribute(GLattr.DepthSize, 24);
            _sdl.GLSetAttribute(GLattr.StencilSize, 8);
        }

        // SDL must use the same loader as Penelope (macOS: SDK/brew path, see VulkanDevice.LoaderPath).
        if (Backend == GraphicsBackend.Vulkan && Penelope.Backends.Vulkan.VulkanDevice.LoaderPath() is { } loader)
            _sdl.VulkanLoadLibrary(loader);

        NativeHandle = _sdl.CreateWindow(
            settings.Title,
            Sdl.WindowposCentered,
            Sdl.WindowposCentered,
            initW,
            initH,
            flags);

        if (NativeHandle == null)
            throw new Exception($"Failed to create SDL window: {_sdl.GetErrorS()}");

        if (Backend == GraphicsBackend.OpenGL)
        {
            GLContext = _sdl.GLCreateContext(NativeHandle);
            if (GLContext == null)
                throw new Exception($"Failed to create GL context: {_sdl.GetErrorS()}");
            _sdl.GLMakeCurrent(NativeHandle, GLContext);
            // VSync intent — also reflected in PresentMode for Vulkan in Engine.Run.
            _sdl.GLSetSwapInterval(settings.VSync ? 1 : 0);
        }
        else if (Backend == GraphicsBackend.Metal)
        {
            // SDL_Metal_CreateView returns an SDL_MetalView (NSView*) wrapping a CAMetalLayer.
            // The engine reads the layer off this via SDL_Metal_GetLayer and hands it to
            // Penelope's MetalDevice. SDL handles the CAMetalLayer lifecycle.
            MetalView = _sdl.MetalCreateView(NativeHandle);
            if (MetalView == null)
                throw new Exception($"Failed to create Metal view: {_sdl.GetErrorS()}");
        }

        RefreshSizes();
        DisplayIndex = _sdl.GetWindowDisplayIndex(NativeHandle);
    }

    /// <summary>Game-internal logical resolution (constant, set at construction).</summary>
    public int LogicalWidth { get; }
    public int LogicalHeight { get; }

    /// <summary>OS window size — what SDL reports for window size + mouse coords.</summary>
    public int WindowWidth { get; private set; }
    public int WindowHeight { get; private set; }

    /// <summary>Actual GPU framebuffer pixels — what the swapchain + scissor must use.</summary>
    public int DrawableWidth { get; private set; }
    public int DrawableHeight { get; private set; }

    /// <summary>Drawable / Window. 1.0 on standard displays, 2.0 on retina, 1.5 on Win 150% scaling.</summary>
    public float DpiScale => WindowWidth > 0 ? (float)DrawableWidth / WindowWidth : 1f;

    public FullscreenMode FullscreenMode { get; private set; }
    public int DisplayIndex { get; private set; }

    public bool ShouldClose { get; set; }
    public bool WasResized { get; private set; }
    public bool IsFullscreen => FullscreenMode != FullscreenMode.Windowed;

    // Back-compat properties — PixelWidth/Height map to DRAWABLE size (not window). Old
    // call sites that passed these to the swapchain/viewport were already implicitly
    // expecting drawable-pixels on standard displays where Drawable == Window.
    public int PixelWidth => DrawableWidth;
    public int PixelHeight => DrawableHeight;

    public void SetCursorVisible(bool visible)
    {
        _sdl.ShowCursor(visible ? Sdl.Enable : Sdl.Disable);
    }

    public unsafe void Dispose()
    {
        // GL context / Metal view must die before the window is destroyed; for Vulkan there's
        // nothing to clean up here (the device owns the surface).
        if (GLContext != null)
        {
            _sdl.GLDeleteContext(GLContext);
            GLContext = null;
        }
        if (MetalView != null)
        {
            _sdl.MetalDestroyView(MetalView);
            MetalView = null;
        }
        _sdl.DestroyWindow(NativeHandle);
    }

    public unsafe void ToggleFullscreen()
    {
        SetFullscreenMode(FullscreenMode == FullscreenMode.Windowed
            ? FullscreenMode.Borderless
            : FullscreenMode.Windowed);
    }

    public unsafe void SetFullscreen(bool fullscreen)
    {
        SetFullscreenMode(fullscreen ? FullscreenMode.Borderless : FullscreenMode.Windowed);
    }

    /// <summary>Saved windowed-mode size — restored when leaving fullscreen.</summary>
    private int _savedWindowedW;
    private int _savedWindowedH;

    // Raw SDL2 fullscreen flags. Silk.NET's WindowFlags enum names exist but using the raw
    // values matches what SDL2 actually expects in SDL_SetWindowFullscreen — avoids any
    // enum-value drift between Silk.NET versions.
    private const uint SdlWindowFullscreen = 0x00000001;
    private const uint SdlWindowFullscreenDesktop = 0x00001001;

    public unsafe void SetFullscreenMode(FullscreenMode mode)
    {
        // macOS: true exclusive fullscreen does a CGDisplay mode switch that fights the OS
        // (spaces animation, scaled retina modes, notch) and leaves the desktop rescaled.
        // SDL's own guidance is to use FULLSCREEN_DESKTOP there — coerce silently.
        if (mode == FullscreenMode.Exclusive && OperatingSystem.IsMacOS())
            mode = FullscreenMode.Borderless;

        if (FullscreenMode == mode) return;

        // Save size before leaving Windowed so we can restore it on return.
        if (FullscreenMode == FullscreenMode.Windowed)
        {
            _savedWindowedW = WindowWidth;
            _savedWindowedH = WindowHeight;
        }

        // SDL2's SetWindowFullscreen takes ONLY fullscreen flags (0 / FULLSCREEN /
        // FULLSCREEN_DESKTOP). Clear first so transitioning between fullscreen variants
        // (Borderless ↔ Exclusive) doesn't leave SDL in an inconsistent state.
        _sdl.SetWindowFullscreen(NativeHandle, 0);

        var sdlFlags = mode switch
        {
            FullscreenMode.Borderless => SdlWindowFullscreenDesktop,
            FullscreenMode.Exclusive => SdlWindowFullscreen,
            _ => 0u
        };
        var rc = _sdl.SetWindowFullscreen(NativeHandle, sdlFlags);
        if (rc != 0)
            // SDL refused the mode change. Stay in the current state, don't fire the event.
            return;

        // When returning to Windowed, SDL leaves the window at desktop size. Restore the
        // pre-fullscreen size so the user gets their window back.
        if (mode == FullscreenMode.Windowed && _savedWindowedW > 0 && _savedWindowedH > 0)
            _sdl.SetWindowSize(NativeHandle, _savedWindowedW, _savedWindowedH);

        FullscreenMode = mode;
        RefreshSizes();
        WasResized = true; // engine recreates swapchain
        FullscreenModeChanged?.Invoke(mode);
    }

    /// <summary>
    ///     Triggered when the framebuffer needs to be rebuilt with a new present mode (vsync).
    ///     The engine subscribes; Penelope re-Configures the swapchain.
    /// </summary>
    public event Action<bool>? VSyncChangeRequested;

    /// <summary>
    ///     Fired whenever the fullscreen mode actually changed (via F11, the SettingsUI button,
    ///     or any other path). Game settings layers subscribe to keep their persisted state in
    ///     sync without polling.
    /// </summary>
    public event Action<FullscreenMode>? FullscreenModeChanged;

    public void SetVSync(bool vsync)
    {
        // For Vulkan the engine listens on VSyncChangeRequested and re-Configures the swapchain
        // present mode. For OpenGL we apply directly via SDL since GL has no swapchain object.
        if (Backend == GraphicsBackend.OpenGL)
            _sdl.GLSetSwapInterval(vsync ? 1 : 0);
        VSyncChangeRequested?.Invoke(vsync);
    }

    /// <summary>Swap the OpenGL back buffer. No-op for Vulkan (Penelope's swapchain handles it).</summary>
    public unsafe void SwapBuffers()
    {
        if (Backend == GraphicsBackend.OpenGL)
            _sdl.GLSwapWindow(NativeHandle);
    }

    /// <summary>Look up a GL function pointer. Throws if called outside the OpenGL backend.</summary>
    public unsafe nint GLGetProcAddress(string name)
    {
        if (Backend != GraphicsBackend.OpenGL)
            throw new InvalidOperationException("GLGetProcAddress called on a non-OpenGL window.");
        return (nint)_sdl.GLGetProcAddress(name);
    }

    public unsafe void PollEvents(Action<Event>? eventHandler = null)
    {
        WasResized = false;
        Event evt;
        while (_sdl.PollEvent(&evt) != 0)
        {
            if (evt.Type == (uint)EventType.Quit)
            {
                ShouldClose = true;
            }
            else if (evt.Type == (uint)EventType.Windowevent)
            {
                if (evt.Window.Event == (byte)WindowEventID.SizeChanged
                    || evt.Window.Event == (byte)WindowEventID.Resized)
                {
                    RefreshSizes();
                    WasResized = true;
                }
                else if (evt.Window.Event == (byte)WindowEventID.Moved)
                {
                    // Display can change DPI when window moves between monitors. Re-query
                    // sizes; if drawable changed (DPI shift), flag a resize so the engine
                    // recreates the swapchain.
                    var newDisplay = _sdl.GetWindowDisplayIndex(NativeHandle);
                    if (newDisplay != DisplayIndex)
                    {
                        DisplayIndex = newDisplay;
                        var prevDrawW = DrawableWidth;
                        var prevDrawH = DrawableHeight;
                        RefreshSizes();
                        if (DrawableWidth != prevDrawW || DrawableHeight != prevDrawH)
                            WasResized = true;
                    }
                }
            }

            eventHandler?.Invoke(evt);
        }
    }

    /// <summary>Number of connected displays SDL can see.</summary>
    public int GetDisplayCount()
    {
        var n = _sdl.GetNumVideoDisplays();
        return n > 0 ? n : 1;
    }

    /// <summary>OS name of a display ("Built-in Retina Display", "DELL U2723QE", …).</summary>
    public string GetDisplayName(int index)
    {
        var name = _sdl.GetDisplayNameS(index);
        return string.IsNullOrEmpty(name) ? $"Display {index + 1}" : name;
    }

    /// <summary>
    ///     Unique fullscreen resolutions (w, h) the display supports, smallest first. Refresh-rate
    ///     / pixel-format duplicates are collapsed.
    /// </summary>
    public unsafe List<(int W, int H)> GetResolutions(int display)
    {
        var result = new List<(int, int)>();
        var n = _sdl.GetNumDisplayModes(display);
        for (var i = 0; i < n; i++)
        {
            DisplayMode m;
            if (_sdl.GetDisplayMode(display, i, &m) != 0) continue;
            var entry = (m.W, m.H);
            if (!result.Contains(entry)) result.Add(entry);
        }

        // Fall back to the desktop mode so the selector never renders empty.
        if (result.Count == 0)
        {
            DisplayMode desk;
            if (_sdl.GetDesktopDisplayMode(display, &desk) == 0)
                result.Add((desk.W, desk.H));
        }

        // SDL returns modes grouped by format/refresh-rate, not sorted by size — sort explicitly
        // so the cycler steps through resolutions in a sane order instead of jumping around.
        result.Sort((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2));

        return result;
    }

    /// <summary>
    ///     Desktop area minus menu bar / dock / taskbar for a display, in window coordinates.
    ///     The largest size a windowed-mode window can take without the OS clamping it.
    /// </summary>
    public unsafe (int W, int H) GetUsableBounds(int display)
    {
        Silk.NET.Maths.Rectangle<int> r = default;
        if (_sdl.GetDisplayUsableBounds(display, &r) == 0 && r.Size.X > 0 && r.Size.Y > 0)
            return (r.Size.X, r.Size.Y);

        DisplayMode desk;
        if (_sdl.GetDesktopDisplayMode(display, &desk) == 0)
            return (desk.W, desk.H);
        return (WindowWidth, WindowHeight);
    }

    // SDL_WINDOWPOS_CENTERED_DISPLAY(X) — centers the window on display X.
    private static int CenteredOnDisplay(int index)
    {
        return (int)(0x2FFF0000u | (uint)System.Math.Max(0, index));
    }

    /// <summary>
    ///     Move the window to another display (centered). Fullscreen modes are dropped to
    ///     Windowed for the move and restored afterwards — SDL2 can't hop fullscreen windows
    ///     across displays directly.
    /// </summary>
    public unsafe void MoveToDisplay(int index)
    {
        if (index < 0 || index >= GetDisplayCount() || index == DisplayIndex) return;

        var restore = FullscreenMode;
        if (restore != FullscreenMode.Windowed)
            SetFullscreenMode(FullscreenMode.Windowed);

        var pos = CenteredOnDisplay(index);
        _sdl.SetWindowPosition(NativeHandle, pos, pos);
        DisplayIndex = _sdl.GetWindowDisplayIndex(NativeHandle);

        if (restore != FullscreenMode.Windowed)
            SetFullscreenMode(restore);

        RefreshSizes();
        WasResized = true;
    }

    /// <summary>
    ///     Apply a resolution. Windowed: resizes + recenters the window. Exclusive: sets the
    ///     display mode and re-asserts fullscreen so it takes effect. Borderless always uses
    ///     the desktop resolution — no-op there.
    /// </summary>
    public unsafe void SetResolution(int w, int h)
    {
        if (w <= 0 || h <= 0) return;

        switch (FullscreenMode)
        {
            case FullscreenMode.Windowed:
                // Clamp to the usable desktop area — a window at full desktop resolution
                // gets clamped/repositioned by the OS (macOS especially), which then never
                // matches the requested size and confuses resolution selectors.
                var (maxW, maxH) = GetUsableBounds(DisplayIndex < 0 ? 0 : DisplayIndex);
                w = System.Math.Min(w, maxW);
                h = System.Math.Min(h, maxH);
                if (w == WindowWidth && h == WindowHeight) return;
                _sdl.SetWindowSize(NativeHandle, w, h);
                var pos = CenteredOnDisplay(DisplayIndex);
                _sdl.SetWindowPosition(NativeHandle, pos, pos);
                _savedWindowedW = w;
                _savedWindowedH = h;
                break;

            case FullscreenMode.Exclusive:
                if (w == WindowWidth && h == WindowHeight) return;
                // SDL2 requires the display mode to be set while the window is NOT
                // fullscreen; changing it in-place mode-switches the live display and
                // leaves the swapchain out of sync. Exit → set mode → re-enter.
                _sdl.SetWindowFullscreen(NativeHandle, 0);
                var mode = new DisplayMode { W = w, H = h };
                if (_sdl.SetWindowDisplayMode(NativeHandle, &mode) != 0)
                {
                    _sdl.SetWindowFullscreen(NativeHandle, SdlWindowFullscreen);
                    return;
                }
                _sdl.SetWindowFullscreen(NativeHandle, SdlWindowFullscreen);
                break;

            case FullscreenMode.Borderless:
                return;
        }

        RefreshSizes();
        WasResized = true;
    }

    public unsafe (int w, int h) GetWindowSize()
    {
        int w = 0, h = 0;
        _sdl.GetWindowSize(NativeHandle, ref w, ref h);
        return (w, h);
    }

    public unsafe (int w, int h) GetDrawableSize()
    {
        int w = 0, h = 0;
        switch (Backend)
        {
            case GraphicsBackend.Vulkan: _sdl.VulkanGetDrawableSize(NativeHandle, ref w, ref h); break;
            case GraphicsBackend.OpenGL: _sdl.GLGetDrawableSize(NativeHandle, ref w, ref h); break;
            case GraphicsBackend.Metal:  _sdl.MetalGetDrawableSize(NativeHandle, ref w, ref h); break;
        }
        return (w, h);
    }

    /// <summary>
    ///     Pointer to the CAMetalLayer associated with this window. Throws if not running under
    ///     the Metal backend. The engine forwards this to <c>MetalDevice.MetalContextProvider</c>.
    /// </summary>
    public unsafe nint GetMetalLayer()
    {
        if (Backend != GraphicsBackend.Metal || MetalView == null)
            throw new InvalidOperationException("GetMetalLayer called on a non-Metal window.");
        return (nint)_sdl.MetalGetLayer(MetalView);
    }

    /// <summary>Re-query window + drawable sizes from SDL. Called on resize/move/fullscreen change.</summary>
    private unsafe void RefreshSizes()
    {
        int ww = 0, wh = 0;
        _sdl.GetWindowSize(NativeHandle, ref ww, ref wh);
        WindowWidth = ww;
        WindowHeight = wh;

        int dw = 0, dh = 0;
        switch (Backend)
        {
            case GraphicsBackend.Vulkan: _sdl.VulkanGetDrawableSize(NativeHandle, ref dw, ref dh); break;
            case GraphicsBackend.OpenGL: _sdl.GLGetDrawableSize(NativeHandle, ref dw, ref dh); break;
            case GraphicsBackend.Metal:  _sdl.MetalGetDrawableSize(NativeHandle, ref dw, ref dh); break;
        }
        // Some SDL builds fail GetDrawableSize before the surface is ready — fall back to
        // window size so we never store zero (which would crash the swapchain create).
        DrawableWidth = dw > 0 ? dw : ww;
        DrawableHeight = dh > 0 ? dh : wh;
    }
}
