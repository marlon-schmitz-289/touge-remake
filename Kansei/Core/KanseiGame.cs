using Kansei.Input;
using Kansei.Windowing;
using Penelope;

namespace Kansei.Core;

/// <summary>
///     Game base. <see cref="Tick"/> runs at a fixed rate (physics, net), <see cref="Update"/> and
///     <see cref="Render"/> once per displayed frame. <see cref="FrameContext"/> carries the
///     interpolation factor between the last two ticks.
/// </summary>
public abstract class KanseiGame : IDisposable
{
    public GameWindow Window { get; internal set; } = null!;
    public IPenelopeDevice Device { get; internal set; } = null!;
    public InputSnapshot Input { get; internal set; } = null!;

    /// <summary>Fixed simulation rate in Hz.</summary>
    public virtual int TickRate => 120;

    /// <summary>CPU milliseconds of the last frame: ticks, <see cref="Update"/> and <see cref="Render"/>, without waiting for the GPU or the display.</summary>
    public double CpuMs { get; internal set; }

    public virtual void Load() { }
    public virtual void Tick(float dt) { }
    public virtual void Update(in GameTime time) { }
    public abstract void Render(in FrameContext ctx);
    public virtual void Dispose() => GC.SuppressFinalize(this);
}
