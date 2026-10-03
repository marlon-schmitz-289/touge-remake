namespace Kansei.Core;

public readonly struct GameTime
{
    public float DeltaTime { get; init; }
    public double TotalTime { get; init; }
    public long FrameCount { get; init; }
}