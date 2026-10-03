using Penelope;

namespace Kansei.Core;

/// <summary>
///     Per-frame render state. A <c>ref struct</c> so it cannot escape to the heap or be
///     captured as a field — the encoder and viewport are valid only for the frame in which
///     the context was constructed. <see cref="Engine"/> builds one per frame and passes it
///     <c>in</c> to <see cref="KanseiGame.Render"/>; the game forwards it to its own helpers.
/// </summary>
public readonly ref struct FrameContext
{
    public FrameContext(IPenelopeDevice device, ICommandEncoder encoder, in Viewport viewport, in GameTime time, float tickAlpha = 0f)
    {
        Device = device;
        Encoder = encoder;
        Viewport = viewport;
        Time = time;
        TickAlpha = tickAlpha;
    }

    public IPenelopeDevice Device { get; }
    public ICommandEncoder Encoder { get; }
    public Viewport Viewport { get; }
    public GameTime Time { get; }

    /// <summary>0..1 position between the last two fixed ticks, for render interpolation.</summary>
    public float TickAlpha { get; }
}

/// <summary>
///     Pixel-space viewport rectangle the engine has assigned for the current frame. Helper
///     <see cref="ApplyTo"/> sets viewport+scissor on a render pass with the Y-flip the
///     engine's GL-authored shaders expect.
/// </summary>
public readonly struct Viewport
{
    public Viewport(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>
    ///     Apply this viewport (Y-flipped) and a matching scissor to <paramref name="pass"/>.
    ///     The negative-height viewport flips clip-space Y so GL-authored shaders render the
    ///     same on Vulkan's Y-down clip space.
    /// </summary>
    public void ApplyTo(IRenderPassEncoder pass)
    {
        pass.SetViewport(X, Y + Height, Width, -Height);
        pass.SetScissor(X, Y, Width, Height);
    }
}
