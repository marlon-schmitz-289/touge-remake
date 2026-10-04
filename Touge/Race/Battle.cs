namespace Touge.Race;

/// <summary>
///     <see cref="Race"/>: the original's battle (Special Stage race code 0x1677C0/0x16B600/0x15E690, FORMATS.md): side by
///     side, first to the goal wins — the original decides when the player reaches the goal by the position flag — plus,
///     as an addition, a breakaway win when the gap reaches <see cref="Battle.Breakaway"/>.
///     <see cref="LeadChase"/>: the anime's one-round lead/chase: the chaser wins by passing and holding the lead
///     <see cref="Battle.PassHold"/> s, the leader by pulling a <see cref="Battle.Breakaway"/> gap or reaching the goal
///     with at least <see cref="Battle.DrawGap"/> in hand; a chaser still glued to the bumper at the goal is a draw.
/// </summary>
public enum BattleRule { Race, LeadChase }

public enum BattleOutcome { None, Win, Lose, Draw }

/// <summary>When each car passed each metre of the line: the time gap = how long ago the leader was where the follower is now.</summary>
public sealed class GapClock
{
    private readonly float[][] _passed;
    private readonly int[] _reached;

    public GapClock(int cars, float length)
    {
        _passed = [.. Enumerable.Range(0, cars).Select(_ => new float[(int)length + 2])];
        _reached = new int[cars];
        Array.Fill(_reached, -1);
    }

    /// <summary>Car <paramref name="car"/> is at <paramref name="along"/> m at <paramref name="time"/>: every metre since its last mark gets a time (interpolated).</summary>
    public void Mark(int car, float along, float time, float prevTime)
    {
        var p = _passed[car];
        var to = Math.Clamp((int)along, 0, p.Length - 1);
        var from = _reached[car];
        if (from < 0)
        {
            // where it starts: everything up to here counts as passed at the start
            Array.Fill(p, time, 0, to + 1);
            _reached[car] = to;
            return;
        }
        for (var m = from + 1; m <= to; m++) p[m] = float.Lerp(prevTime, time, (float)(m - from) / (to - from));
        if (to > from) _reached[car] = to;
    }

    /// <summary>Seconds since <paramref name="leader"/> passed <paramref name="followerAlong"/> (0 if it has not got there).</summary>
    public float Behind(int leader, float followerAlong, float now)
    {
        var p = _passed[leader];
        var at = Math.Clamp(followerAlong, 0, p.Length - 1);
        var m = (int)at;
        if (m > _reached[leader]) return 0;
        var passed = m + 1 <= _reached[leader] ? float.Lerp(p[m], p[m + 1], at - m) : p[m]; // between the metre marks
        return now - passed;
    }
}

/// <summary>
///     Rules of a two-car battle (car 0 = the player, 1 = the rival) from their progress along the course line, one call per
///     physics tick: debounced position (a lead change counts after <see cref="PositionHold"/>, the original's 6 frames),
///     signed time gap (+ = player ahead), overtakes, and the <see cref="Outcome"/> once decided (it then stays).
/// </summary>
public sealed class Battle(BattleRule rule, float goal, int startLeader = 1)
{
    /// <summary>A lead change must hold this long (the original's 6 frames at 60 Hz, 0x15E690).</summary>
    public const float PositionHold = 0.1f;

    public BattleRule Rule { get; } = rule;
    /// <summary>Metres along the line where the goal is.</summary>
    public float Goal { get; } = goal;
    /// <summary>Lead/chase: who leads off (0 player, 1 rival); race: who is ahead on the grid's tie (both side by side).</summary>
    public int StartLeader { get; } = startLeader;
    /// <summary>Time gap that ends the battle (s); 0 = only at the goal.</summary>
    public float Breakaway { get; init; } = rule == BattleRule.LeadChase ? 4 : 8;
    /// <summary>Lead/chase: how long the chaser must hold the lead after a pass, and the gap a leader needs at the goal.</summary>
    public float PassHold { get; init; } = 1.5f;
    public float DrawGap { get; init; } = 1;

    private readonly GapClock _clock = new(2, goal + 50);
    private float _rawSince, _time;
    private int _raw = startLeader;

    /// <summary>Debounced leader: 0 player, 1 rival.</summary>
    public int Leader { get; private set; } = startLeader;
    /// <summary>Seconds the current <see cref="Leader"/> has led.</summary>
    public float LeaderFor { get; private set; }
    /// <summary>Time gap, s: + = the player ahead.</summary>
    public float Gap { get; private set; }
    /// <summary>Distance gap along the line, m: + = the player ahead.</summary>
    public float GapMetres { get; private set; }
    /// <summary>Lead changes so far, and how many of them were the player's passes.</summary>
    public int Overtakes { get; private set; }
    public int PlayerPasses { get; private set; }
    public BattleOutcome Outcome { get; private set; }
    /// <summary>Why it was decided: GOAL, BREAKAWAY, OVERTAKE, NO GAP (draw).</summary>
    public string Reason { get; private set; } = "";
    /// <summary>Battle time when it was decided.</summary>
    public float DecidedAt { get; private set; }
    public float Time => _time;
    /// <summary>The player leads (debounced).</summary>
    public bool PlayerLeads => Leader == 0;

    /// <summary>One tick: <paramref name="dt"/> s passed, the cars are at <paramref name="player"/>/<paramref name="rival"/> m along the line.</summary>
    public void Update(float dt, float player, float rival)
    {
        var prev = _time;
        _time += dt;
        _clock.Mark(0, player, _time, prev);
        _clock.Mark(1, rival, _time, prev);
        GapMetres = player - rival;
        var raw = player > rival ? 0 : player < rival ? 1 : _raw;
        if (raw != _raw) (_raw, _rawSince) = (raw, 0);
        else _rawSince += dt;
        if (_raw != Leader && _rawSince >= PositionHold)
        {
            Leader = _raw;
            LeaderFor = _rawSince;
            Overtakes++;
            if (Leader == 0) PlayerPasses++;
        }
        else LeaderFor += dt;
        Gap = raw == 0 ? _clock.Behind(0, rival, _time) : -_clock.Behind(1, player, _time);
        if (Outcome == BattleOutcome.None) Decide(player, rival);
    }

    private void Decide(float player, float rival)
    {
        bool pDone = player >= Goal, rDone = rival >= Goal;
        if (Rule == BattleRule.Race)
        {
            if (pDone || rDone) End(player >= rival ? 0 : 1, "GOAL");
            else if (Breakaway > 0 && MathF.Abs(Gap) >= Breakaway) End(Gap > 0 ? 0 : 1, "BREAKAWAY");
            return;
        }
        int lead = StartLeader, chase = 1 - StartLeader;
        float leadAlong = lead == 0 ? player : rival, chaseAlong = lead == 0 ? rival : player;
        var leadGap = lead == 0 ? Gap : -Gap; // + = leader ahead
        if (Leader == chase && LeaderFor >= PassHold) End(chase, "OVERTAKE");
        else if (chaseAlong >= Goal && chaseAlong > leadAlong) End(chase, "OVERTAKE");
        else if (Breakaway > 0 && leadGap >= Breakaway) End(lead, "BREAKAWAY");
        else if (leadAlong >= Goal)
        {
            if (leadGap >= DrawGap) End(lead, "GOAL");
            else End(-1, "NO GAP");
        }
    }

    private void End(int winner, string reason)
    {
        Outcome = winner switch { 0 => BattleOutcome.Win, 1 => BattleOutcome.Lose, _ => BattleOutcome.Draw };
        (Reason, DecidedAt) = (reason, _time);
    }
}
