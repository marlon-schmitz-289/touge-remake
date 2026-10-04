using System.Numerics;
using Kansei.Graphics;
using Touge.Race;

namespace Touge.Ui;

/// <summary>A decided battle for the finish banner and result sheet (<see cref="Menu.Battle"/>).</summary>
/// <param name="Gap">Time gap when decided, s, + = the player ahead.</param>
/// <param name="Time">Battle time when decided; <paramref name="PlayerTime"/>/<paramref name="RivalTime"/> = goal times (null: did not get there).</param>
public sealed record BattleReport(BattleOutcome Outcome, string Reason, BattleRule Rule, bool PlayerLed, string Rival, string Team, string RivalCar,
    float Gap, float GapMetres, float Time, float? PlayerTime, float? RivalTime, int Overtakes, int PlayerPasses, int Contacts, float MaxImpact)
{
    public static BattleReport Of(RaceSession race, Rivals.Rival rival, string rivalCar)
    {
        var b = race.Battle!;
        return new BattleReport(b.Outcome, b.Reason, b.Rule, b.StartLeader == 0, rival.Name, rival.Team, rivalCar, b.DecidedGap, b.DecidedGapMetres, b.DecidedAt,
            race.Cars[0].FinishedAt, race.Cars[1].FinishedAt, b.Overtakes, b.PlayerPasses, race.Contacts, race.MaxImpact);
    }

    /// <summary>One line on how it was decided, from the player's side.</summary>
    public string Verdict => (Outcome, Reason) switch
    {
        (BattleOutcome.Win, "GOAL") => Rule == BattleRule.LeadChase ? "HELD THE LEAD TO THE GOAL" : "FIRST TO THE GOAL",
        (BattleOutcome.Lose, "GOAL") => Rule == BattleRule.LeadChase ? "COULD NOT CLOSE THE GAP" : "BEATEN TO THE GOAL",
        (BattleOutcome.Win, "BREAKAWAY") => "PULLED AWAY",
        (BattleOutcome.Lose, "BREAKAWAY") => "LEFT BEHIND",
        (BattleOutcome.Win, "OVERTAKE") => "PASSED AND HELD THE LEAD",
        (BattleOutcome.Lose, "OVERTAKE") => "OVERTAKEN",
        (BattleOutcome.Draw, _) => "NO GAP AT THE GOAL",
        (BattleOutcome.Win, "TIME") => "STAYED WITH HIM TO THE END",
        (BattleOutcome.Lose, "TIME") => "TIME UP",
        _ => "",
    };
}

/// <summary>
///     In-race battle HUD, top right opposite the timing panel (the original's RACEVIEW POSITION/ADVANTAGE plates, rebuilt):
///     VS + rival name, the player's position (1ST/2ND) with a LEAD/CHASE tag (lead/chase: the role; race: who is ahead),
///     ADVANTAGE (time gap, green ahead / red behind) and a gap meter from −breakaway to +breakaway with the pass-and-hold
///     progress of a lead/chase pass; a centre banner on every lead change (OVERTAKE! / OVERTAKEN).
/// </summary>
public sealed class BattleHud(string rival, string team)
{
    /// <summary>Panel size in HUD units (top right; the music toast goes below it).</summary>
    public const float W = 330, H = 178;
    private int _overtakes;
    private float _flash = 99;
    private bool _flashGood;
    private float _last;

    public void Build(Overlay o, int width, int height, Battle b, float time)
    {
        var dt = Math.Clamp(time - _last, 0, 0.1f);
        _last = time;
        if (b.Overtakes != _overtakes) (_overtakes, _flash, _flashGood) = (b.Overtakes, 0, b.PlayerLeads);
        _flash += dt;

        var g = Style.Safe(width, height);
        var u = g.U;
        var at = new Vector2(g.Right - W * u, g.Top);
        Style.Slanted(o, at, at + new Vector2(W, H) * u, Style.Panel, -0.22f * 150 / H);
        o.Rect(Vector2.Round(at + new Vector2(W - 5, 0) * u), Vector2.Round(at + new Vector2(W, H) * u), Style.Red);
        var x0 = at.X + 44 * u;
        var x1 = at.X + (W - 22) * u;

        // VS + rival
        var vs = Style.Label(o, "VS ", new Vector2(x0, at.Y + 28 * u), 17 * u, Style.Red, 0, Style.Slant, 0.3f * u);
        Style.Label(o, rival, new Vector2(x0 + vs, at.Y + 28 * u), 19 * u, Style.Text, 0, Style.Slant, 0.2f * u);
        Style.Label(o, team, new Vector2(x1, at.Y + 46 * u), 12 * u, Style.Dim, 1);

        // position and the role tag
        var lead = b.PlayerLeads;
        Style.Label(o, lead ? "1ST" : "2ND", new Vector2(x0 - 4 * u, at.Y + 92 * u), 46 * u, lead ? Style.Amber : Style.Text, 0, Style.Slant, 0.4f * u);
        var role = b.Rule == BattleRule.LeadChase ? b.StartLeader == 0 ? "LEAD" : "CHASE" : lead ? "LEAD" : "CHASE";
        var roleColour = role == "LEAD" ? Overlay.Rgba(0.1f, 0.55f, 0.95f) : Overlay.Rgba(0.85f, 0.1f, 0.08f);
        Vector2 tMin = new(x0 + 96 * u, at.Y + 62 * u), tMax = tMin + new Vector2(84, 28) * u;
        Style.Slanted(o, tMin, tMax, roleColour, 0.3f);
        o.Text(role, new Vector2((tMin.X + tMax.X) / 2 - 3 * u, tMax.Y - 7 * u), 19 * u, Style.Text, 0.5f, 0.3f * u, 0, Style.Slant);

        // advantage
        Style.Label(o, "ADVANTAGE", new Vector2(x1, at.Y + 70 * u), 12 * u, Style.Dim, 1);
        var gap = b.Gap;
        Style.Label(o, (gap >= 0 ? "+" : "-") + MathF.Abs(gap).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), new Vector2(x1, at.Y + 98 * u), 28 * u,
            gap >= 0 ? Style.Green : Style.Red, 1, Style.Slant, 0.3f * u);

        // gap meter: −breakaway (rival pulls away) … +breakaway (player does)
        var span = b.Breakaway > 0 ? b.Breakaway : 10;
        float mx0 = x0, mx1 = x1, my = at.Y + 122 * u, mh = 10 * u;
        var mid = (mx0 + mx1) / 2;
        o.Rect(Vector2.Round(new Vector2(mx0, my)), Vector2.Round(new Vector2(mx1, my + mh)), Style.Faint);
        var k = Math.Clamp(gap / span, -1, 1);
        var fx = mid + k * (mx1 - mx0) / 2;
        o.Rect(Vector2.Round(new Vector2(MathF.Min(mid, fx), my)), Vector2.Round(new Vector2(MathF.Max(mid, fx), my + mh)), k >= 0 ? Style.Green : Style.Red);
        o.Rect(Vector2.Round(new Vector2(mid - u, my - 4 * u)), Vector2.Round(new Vector2(mid + u, my + mh + 4 * u)), Style.Text);
        // ends: the breakaway marks
        foreach (var (ex, c) in new[] { (mx0, Style.Red), (mx1, Style.Green) })
            o.Rect(Vector2.Round(new Vector2(ex - 1.5f * u, my - 3 * u)), Vector2.Round(new Vector2(ex + 1.5f * u, my + mh + 3 * u)), c);
        Vector2 d = new(0, 7 * u), e = new(6 * u, 0), p = new(fx, my - 6 * u);
        o.Triangle(p - e - d, p + e - d, p, Style.Text);

        // distance and, in lead/chase, the hold of a pass
        var metres = MathF.Abs(b.GapMetres);
        Style.Label(o, $"{(lead ? "AHEAD" : "BEHIND")} {metres:0} m", new Vector2(x0, at.Y + 160 * u), 15 * u, Style.Dim);
        Style.Label(o, $"BREAKAWAY {span:0} s", new Vector2(x1, at.Y + 160 * u), 13 * u, Style.Dim, 1);
        if (b.Rule == BattleRule.LeadChase && b.Leader != b.StartLeader && b.Outcome == BattleOutcome.None)
        {
            var hold = Math.Clamp(b.LeaderFor / b.PassHold, 0, 1);
            o.Rect(Vector2.Round(new Vector2(x0, at.Y + 166 * u)), Vector2.Round(new Vector2(x0 + (x1 - x0) * hold, at.Y + 170 * u)), Style.Amber);
        }

        // lead change banner
        if (_flash < 1.6f && b.Overtakes > 0)
        {
            var a = Style.Ease(_flash * 6) * Style.Ease((1.6f - _flash) * 3);
            var cx = width / 2f;
            var y = height * 0.25f;
            var text = _flashGood ? "OVERTAKE!" : "OVERTAKEN";
            Style.Slanted(o, new Vector2(cx - 220 * u, y - 46 * u), new Vector2(cx + 220 * u, y + 16 * u),
                Style.Fade(_flashGood ? Overlay.Rgba(0.04f, 0.3f, 0.75f, 0.85f) : Overlay.Rgba(0.7f, 0.05f, 0.04f, 0.85f), a), 0.3f);
            Style.Label(o, text, new Vector2(cx - 8 * u, y), 50 * u, Style.Fade(Style.Text, a), 0.5f, Style.Slant, 0.5f * u);
        }
    }
}

/// <summary>Battle variants of the finish banner and the result sheet (Menu, canvas 512×448 like the original).</summary>
public static class BattleScreens
{
    /// <summary>YOU WIN / YOU LOSE / DRAW with how it was decided and the gap.</summary>
    public static void Banner(Canvas c, BattleReport r, float t)
    {
        var pop = Style.Ease(t / 0.25f);
        var (text, top, bottom) = r.Outcome switch
        {
            BattleOutcome.Win => ("YOU WIN!!", Overlay.Rgba(1, 0.85f, 0.3f), Overlay.Rgba(1, 0.38f, 0)),
            BattleOutcome.Lose => ("YOU LOSE", Overlay.Rgba(0.55f, 0.65f, 1), Canvas.WordBlue),
            _ => ("DRAW", Overlay.Rgba(0.95f, 0.96f, 0.98f), Overlay.Rgba(0.42f, 0.45f, 0.5f)),
        };
        c.Lettering(text, 256, 196, 58 * (1.8f - 0.8f * pop), top, bottom, 0.5f, 0.2f, false, true, pop);
        c.Text(r.Verdict, 256, 224, 15, Style.Fade(r.Outcome == BattleOutcome.Win ? Style.Amber : Canvas.White, pop), 0.5f, 0.15f, 0.1f, 0.3f);
        c.Text($"VS {r.Rival}", 256, 252, 13, Style.Fade(Canvas.White, pop), 0.5f, 0.15f, 0.06f);
        c.Text($"GAP {Style.Delta(r.Gap)[..^1]} s", 256, 280, 24, Style.Fade(r.Gap >= 0 ? Style.Green : Style.Red, pop), 0.5f, 0.15f, 0.06f, 0.4f);
    }

    /// <summary>Result: the battle sheet (outcome, rival, how, gap, times) and the race sheet (lead changes, contacts); rows appear per <paramref name="row"/>.</summary>
    public static void Sheet(Canvas c, BattleReport r, Func<int, float> row)
    {
        c.Fill(Overlay.Rgba(0, 0, 0, 0.25f));
        void Line(int i, float x0, float x1, float y, string label, string value, uint color)
        {
            var a = row(i);
            c.Rule(x0, x1, y + 6, 1);
            if (a <= 0) return;
            c.Text(label, x0 + 6, y - 2, 9.5f, Style.Fade(Canvas.White, a), 0, 0.2f, 0, 0.2f);
            c.Fit(value, x1 - 6, y + 2, x1 - x0 - 90, 1, Style.Fade(color, a), 0.15f, 0, 17);
        }
        var win = r.Outcome == BattleOutcome.Win;
        c.Sheet(30, 78, 252, 300, "Battle");
        Line(0, 30, 252, 104, "RESULT", r.Outcome switch { BattleOutcome.Win => "WIN", BattleOutcome.Lose => "LOSE", _ => "DRAW" }, win ? Style.Amber : Canvas.White);
        Line(1, 30, 252, 134, "RIVAL", r.Rival, Canvas.White);
        Line(2, 30, 252, 164, "CAR", r.RivalCar, Canvas.White);
        Line(3, 30, 252, 194, "DECIDED BY", r.Verdict, Canvas.White);
        Line(4, 30, 252, 224, "GAP", Style.Delta(r.Gap)[..^1] + " s", r.Gap >= 0 ? Style.Green : Style.Red);
        Line(5, 30, 252, 254, "YOUR TIME", r.PlayerTime is { } pt ? Style.Time(pt) : $"- ({Style.Time(r.Time)})", Canvas.White);
        Line(6, 30, 252, 284, "RIVAL TIME", r.RivalTime is { } rt ? Style.Time(rt) : "-", Canvas.White);
        c.Sheet(268, 78, 486, 194, "Race");
        c.Text(r.Rule == BattleRule.LeadChase ? r.PlayerLed ? "LEAD / CHASE  -  YOU LEAD" : "LEAD / CHASE  -  YOU CHASE" : "RACE  -  SIDE BY SIDE", 274, 98, 11, Canvas.White, 0, 0.15f, 0, 0.2f);
        Line(7, 268, 486, 130, "LEAD CHANGES", $"{r.Overtakes}  (YOU {r.PlayerPasses})", Canvas.White);
        Line(7, 268, 486, 162, "CONTACTS", r.Contacts == 0 ? "CLEAN" : $"{r.Contacts}  ({r.MaxImpact * 3.6f:0} km/h)", Canvas.White);
        if (win && row(7) > 0)
        {
            var s = row(7);
            c.Lettering("WIN!!", 377, 240, 40 * (1.6f - 0.6f * s), Overlay.Rgba(1, 0.85f, 0.3f), Overlay.Rgba(1, 0.38f, 0), 0.5f, 0.2f, false, true, s);
        }
    }
}
