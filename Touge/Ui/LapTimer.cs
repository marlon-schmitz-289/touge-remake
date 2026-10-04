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
    private float? _armedAt;
    private float[]? _ref = best; // the best run this run is compared with (Best may become this run at the finish)

    /// <summary>Car at <paramref name="along"/> metres along the line, <paramref name="dt"/> seconds after the last update.</summary>
    public void Update(float along, float dt)
    {
        SinceSplit += dt;
        switch (Phase)
        {
            case State.Ready:
                // armed where the car stands: some lines start off the road, so the car spawns past point 0
                _armedAt ??= along;
                var gate = MathF.Max(Gate, _armedAt.Value + Gate);
                if (_prev <= gate && along > gate && along < length / Sectors)
                    (Phase, Time, Sector, NewRecord, _ref) = (State.Running, 0, 0, false, Best);
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
                (Phase, _armedAt) = (State.Ready, null);
                break;
        }
        _prev = along;
    }

    /// <summary>Split of sector <paramref name="i"/> minus the best run's at the start of this run (null without one or before that split).</summary>
    public float? Delta(int i) => _ref != null && i < Sector ? Splits[i] - _ref[i] : null;

    /// <summary>Back to armed (car reset to the start).</summary>
    public void Restart() => (Phase, Time, Sector, SinceSplit, _prev, _ref, _armedAt) = (State.Ready, 0, 0, float.MaxValue, 0, Best, null);
}
