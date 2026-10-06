namespace Touge.Ui;

/// <summary>
///     Run timing along the driving line (<paramref name="length"/> m from the start line): armed where the car stands, runs
///     once it crosses the start line (or <see cref="Gate"/> metres past where it stood, if that is further), splits at every
///     quarter of the line, stops at the goal. Restarts armed when the car is back at the start. <see cref="Best"/> = cumulative splits of the best run (the last one is the total time);
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
    /// <summary>First sector this run timed (&gt; 0: started mid-course, <see cref="GoHere"/>; no splits before it, never a best).</summary>
    public int From { get; private set; }
    public float[]? Best { get; private set; } = best;
    /// <summary>Seconds since the last split (for the delta pop-up).</summary>
    public float SinceSplit { get; private set; } = float.MaxValue;
    public bool NewRecord { get; private set; }
    public event Action<float[]>? Record;
    private float _prev = float.MaxValue;
    private float? _armedAt;
    private bool _here;
    private float[]? _ref = best; // the best run this run is compared with (Best may become this run at the finish)

    /// <summary>Car at <paramref name="along"/> metres along the line, <paramref name="dt"/> seconds after the last update.</summary>
    public void Update(float along, float dt)
    {
        SinceSplit += dt;
        switch (Phase)
        {
            case State.Ready when _here:
                _here = false;
                var from = 0;
                while (from < Sectors && along >= length * (from + 1) / Sectors - (from == Sectors - 1 ? Gate : 0)) from++;
                if (from < Sectors) (Phase, Time, Sector, From, NewRecord, SinceSplit, _ref) = (State.Running, 0, from, from, false, float.MaxValue, Best);
                break;
            case State.Ready:
                // the start line is at 0; the car stands behind it, or past it where the road starts later (then it starts on driving off)
                _armedAt ??= along;
                var gate = MathF.Max(0, _armedAt.Value + Gate);
                if (_prev <= gate && along > gate && along < length / Sectors)
                    (Phase, Time, Sector, From, NewRecord, _ref) = (State.Running, 0, 0, 0, false, Best);
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
                    if (From == 0 && (Best == null || Time < Best[^1]))
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
    public float? Delta(int i) => _ref != null && i >= From && i < Sector ? Splits[i] - _ref[i] : null;

    /// <summary>Versus: running from GO wherever the car stands, so every player's clock is the race clock.</summary>
    public void Go() => (Phase, Time, Sector, From, NewRecord, SinceSplit, _ref, _here) = (State.Running, 0, 0, 0, false, float.MaxValue, Best, false);

    /// <summary>Free play turned mid-course: running from where the car is at the next update (its sector on), never a record.</summary>
    public void GoHere() => (Phase, _here) = (State.Ready, true);

    /// <summary>Back to armed (car reset to the start).</summary>
    public void Restart() => (Phase, Time, Sector, From, SinceSplit, _prev, _ref, _armedAt, _here) = (State.Ready, 0, 0, 0, float.MaxValue, 0, Best, null, false);
}
