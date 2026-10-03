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

    public virtual void Load() { }
    public virtual void Tick(float dt) { }
    public virtual void Update(in GameTime time) { }
    public abstract void Render(in FrameContext ctx);
    public virtual void Dispose() => GC.SuppressFinalize(this);
}
