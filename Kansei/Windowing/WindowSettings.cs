namespace Kansei.Windowing;

/// <summary>
///     Fullscreen presentation modes.
/// </summary>
public enum FullscreenMode
{
    /// <summary>Windowed (decorated, resizable).</summary>
    Windowed,
    /// <summary>Borderless fullscreen window — same as desktop resolution. Fast alt-tab.</summary>
    Borderless,
    /// <summary>Exclusive fullscreen — owns the display. Lowest latency, slow alt-tab.</summary>
    Exclusive
}

/// <summary>
///     Which Penelope backend the engine should boot. Vulkan is the production path; OpenGL is a
///     portable fallback (Linux/macOS without Vulkan, older drivers) that goes through the same
///     <see cref="Penelope.IPenelopeDevice"/> abstraction so engine/game code is unchanged.
/// </summary>
public enum GraphicsBackend
{
    Vulkan,
    OpenGL,
    /// <summary>Apple Metal — macOS / iOS only at runtime; compiles cross-platform.</summary>
    Metal,
    /// <summary>Nintendo NVN2 — Switch / Switch 2 only. Stubbed pending Nintendo SDK link.</summary>
    Switch,
}

/// <summary>
///     Configuration for the game window.
///     <para><b>BaseWidth/BaseHeight</b> = logical (game-internal) resolution. Rendering and
///     UI layout always use this size; the engine scales it onto the actual framebuffer.</para>
///     <para><b>WindowPixelWidth/Height</b> override the initial OS window size. 0 = use
///     <c>BaseWidth × ScaleFactor</c>.</para>
/// </summary>
public sealed record WindowSettings
{
    public string Title { get; init; } = "MogliEngine";
    public int BaseWidth { get; init; } = 320;
    public int BaseHeight { get; init; } = 288;
    public int ScaleFactor { get; init; } = 3;
    public bool VSync { get; init; } = true;
    public FullscreenMode FullscreenMode { get; init; } = FullscreenMode.Windowed;
    public GraphicsBackend Backend { get; init; } = GraphicsBackend.Vulkan;

    /// <summary>
    ///     Opt the SDL window into HiDPI rendering. With this on, the GPU framebuffer is the
    ///     native pixel resolution of the display (e.g. 2× on retina); without it, the OS
    ///     upscales the low-DPI framebuffer and text/sprites read blurry.
    /// </summary>
    public bool AllowHighDpi { get; init; } = true;

    /// <summary>Override initial window width (OS coords). 0 = BaseWidth * ScaleFactor.</summary>
    public int WindowPixelWidth { get; init; } = 0;

    /// <summary>Override initial window height (OS coords). 0 = BaseHeight * ScaleFactor.</summary>
    public int WindowPixelHeight { get; init; } = 0;

    public int InitialWindowWidth => WindowPixelWidth > 0 ? WindowPixelWidth : BaseWidth * ScaleFactor;
    public int InitialWindowHeight => WindowPixelHeight > 0 ? WindowPixelHeight : BaseHeight * ScaleFactor;

    // Back-compat aliases (PixelWidth was the old name when there was no DPI distinction).
    public int PixelWidth => InitialWindowWidth;
    public int PixelHeight => InitialWindowHeight;

    /// <summary>Legacy boolean shortcut. Maps to <see cref="FullscreenMode.Borderless"/>.</summary>
    public bool Fullscreen
    {
        get => FullscreenMode != FullscreenMode.Windowed;
        init => FullscreenMode = value ? FullscreenMode.Borderless : FullscreenMode.Windowed;
    }
}
