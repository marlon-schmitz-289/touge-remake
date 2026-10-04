namespace Touge.Ui;

/// <summary>
///     Run timing along the driving line (<paramref name="length"/> m): armed at the start, runs once the car passes
///     <see cref="Gate"/> metres past it, splits at every quarter of the line, stops at the goal. Restarts armed when the
///     car is back at the start. <see cref="Best"/> = cumulative splits of the best run (the last one is the total time);
///     a run that beats it replaces it and <see cref="Record"/> fires.
/// </summary>
public sealed class LapTimer(float length, float[]? best)
{
    public const int Sectors = 4;
    public const float Gate = 2;

    public enum State { Ready, Running, Finished }

    public State Phase { get; private set; }
    public float Time { get; private set; }
    /// <summary>Cumulative time at the end of each sector of this run (valid below <see cref="Sector"/>).</summary>
    public float[] Splits { get; } = new float[Sectors];
    /// <summary>Sector being driven (0..3), <see cref="Sectors"/> once finished.</summary>
    public int Sector { get; private set; }
    public float[]? Best { get; private set; } = best;
    /// <summary>Seconds since the last split (for the delta pop-up).</summary>
    public float SinceSplit { get; private set; } = float.MaxValue;
    public bool NewRecord { get; private set; }
    public event Action<float[]>? Record;
    private float _prev = float.MaxValue;

    /// <summary>Car at <paramref name="along"/> metres along the line, <paramref name="dt"/> seconds after the last update.</summary>
    public void Update(float along, float dt)
    {
        SinceSplit += dt;
        switch (Phase)
        {
            case State.Ready when _prev <= Gate && along > Gate && along < length / Sectors:
                (Phase, Time, Sector, NewRecord) = (State.Running, 0, 0, false);
                break;
            case State.Running:
                Time += dt;
                while (Sector < Sectors && along >= length * (Sector + 1) / Sectors - (Sector == Sectors - 1 ? Gate : 0))
                {
                    Splits[Sector++] = Time;
                    SinceSplit = 0;
                }
                if (Sector == Sectors)
                {
                    Phase = State.Finished;
                    if (Best == null || Time < Best[^1])
                    {
                        (Best, NewRecord) = ((float[])Splits.Clone(), true);
                        Record?.Invoke(Best);
                    }
                }
                break;
            case State.Finished when along <= Gate:
                Phase = State.Ready;
                break;
        }
        _prev = along;
    }

    /// <summary>Split of sector <paramref name="i"/> minus the best run's (null without a best run or before that split).</summary>
    public float? Delta(int i) => Best != null && i < Sector ? Splits[i] - Best[i] : null;

    /// <summary>Back to armed (car reset to the start).</summary>
    public void Restart() => (Phase, Time, Sector, SinceSplit, _prev) = (State.Ready, 0, 0, float.MaxValue, 0);
}
