using Kansei.Physics;
using Touge.Formats;
using Touge.Race;
using Touge.Ui;

namespace Touge.Story;

/// <summary>
///     --story-check: the chapter table and every scene script read from the disc, the English lined up against them (parts,
///     utterances, named speakers), then each chapter driven headless by the autopilot against its rival with the chapter's
///     goal; one line per chapter and a summary (the playthrough log).
/// </summary>
public static class StoryHeadless
{
    /// <summary>Loads the chapter table from the ELF (<see cref="StoryScript.ReadChapters"/>).</summary>
    public static StoryScript.Chapter[] Chapters(Iso9660 iso) => StoryScript.ReadChapters(iso.ReadFile(StoryScript.ElfPath));

    /// <summary>The disc's script of chapter <paramref name="n"/> (STRnn.BIN), null for the chapters without one.</summary>
    public static List<List<StoryScript.Line>>? Script(Afs objects, int n) =>
        objects.Find($"STR{n:00}.BIN") is { } e ? StoryScript.ParseScript(objects.Read(e)) : null;

    /// <summary>
    ///     Differences between the disc's script and the English of chapter <paramref name="n"/>: part or utterance counts, and
    ///     lines where the disc names a speaker the English does not (empty = lined up).
    /// </summary>
    public static List<string> Mismatches(List<List<StoryScript.Line>> disc, string[][] english, int n)
    {
        var errors = new List<string>();
        if (disc.Count != english.Length) errors.Add($"Kapitel {n}: {disc.Count} Teile auf der Disc, {english.Length} übersetzt");
        for (var p = 0; p < Math.Min(disc.Count, english.Length); p++)
        {
            if (disc[p].Count != english[p].Length) errors.Add($"Kapitel {n} Teil {p}: {disc[p].Count} Zeilen auf der Disc, {english[p].Length} übersetzt");
            for (var i = 0; i < Math.Min(disc[p].Count, english[p].Length); i++)
            {
                var jp = disc[p][i].Speaker;
                var who = StoryText.Split(english[p][i]).Who;
                if (jp != "" && StoryText.Speakers.TryGetValue(jp, out var en) && en != who)
                    errors.Add($"Kapitel {n} Teil {p} Zeile {i}: Sprecher {jp} = {en}, übersetzt {who}");
            }
        }
        return errors;
    }

    /// <summary>The shows of a chapter (<see cref="StoryMode.Shows" />) from the ELF's manga tables.</summary>
    public static Manga.Chapter[] MangaChapters(Iso9660 iso) => Manga.ReadChapters(iso.ReadFile(StoryScript.ElfPath));

    /// <summary>
    ///     --story-check media: every show of every chapter decoded as the game loads it (pictures, voice track), checked (all
    ///     pictures found, one lip string per page, subtitles in time order and one per utterance) and its subtitles listed at
    ///     their times on the voice track. Returns false on a problem.
    /// </summary>
    public static bool Media(string isoPath, int? only = null)
    {
        using var iso = new Iso9660(isoPath);
        var bad = 0;
        foreach (var c in MangaChapters(iso))
        {
            if (only is { } o && c.Index != o) continue;
            foreach (var r in StoryMode.Shows(c, false).Concat(StoryMode.Shows(c, true)))
            {
                var m = StoryMedia.Decode(isoPath, r);
                var problems = new List<string>();
                if (m.Koma is { } k)
                {
                    var missing = k.Panels.Select(p => p.Name).Concat(k.Backdrops.Select(b => b.Name)).Distinct().Where(n => !m.Decoded.ContainsKey(n)).ToList();
                    if (missing.Count > 0) problems.Add($"fehlt {string.Join(' ', missing)}");
                }
                if (m.Scene is { } s)
                {
                    var obj = iso.OpenAfs("CDVD/DATA/MANGA/MG_OBJ.AFS");
                    var lips = Manga.Lips(obj.Read(obj.Find($"STR{c.Index:00}.BIN")!.Value))[r.Number];
                    var talk = s.Stages.Where(x => x.Step == StoryScript.Step.Page).Select(x => x.Value).ToHashSet();
                    var mute = talk.Count(p => p >= lips.Length || lips[p] == "");
                    if (talk.Count - mute == 0) problems.Add("keine Seite mit Lippensync");
                    Console.WriteLine($"[Story]    {talk.Count} Seiten mit Text, {mute} ohne Lippen-Ziffern");
                    var missing = s.Pictures.Where(n => !m.Decoded.ContainsKey($"P{n:00}") || !m.Backdrops.ContainsKey(n)).ToList();
                    if (missing.Count > 0) problems.Add($"Bild fehlt {string.Join(' ', missing)}");
                    var english = StoryText.Chapters[c.Index].Scene;
                    var part = StoryMedia.Part(Manga.Lips(obj.Read(obj.Find($"STR{c.Index:00}.BIN")!.Value)), r.Number);
                    if (part >= english.Length || english[part].Length != m.Show.Lines.Count) problems.Add("Untertitel ≠ Äußerungen");
                }
                var lines = m.Show.Lines;
                for (var i = 1; i < lines.Count; i++)
                    if (lines[i].Time <= lines[i - 1].Time) problems.Add($"Zeile {i} nicht nach Zeile {i - 1}");
                if (lines.Count > 0 && lines[^1].Time > m.Show.Length) problems.Add("letzte Zeile nach dem Ende");
                Console.WriteLine($"[Story] Kapitel {c.Index,2} {r}: Stimme {m.Voice} {m.VoiceSeconds:0.0} s, Musik {(m.Music != "" ? $"{m.Music} ×{m.MusicGains.Min():0.00}–{m.MusicGains.Max():0.00}" : "-")}, Länge {m.Show.Length:0.0} s, {m.Decoded.Count} Bilder, {lines.Count} Untertitel" +
                                  (problems.Count > 0 ? $"  FEHLER {string.Join("; ", problems)}" : ""));
                bad += problems.Count;
                foreach (var l in lines) Console.WriteLine($"[Story]    {l.Time,7:0.00} s  {l.Line}");
            }
        }
        Console.WriteLine($"[Story] Medien geprüft: {bad} Fehler");
        return bad == 0;
    }

    /// <summary>
    ///     --story-check audio:n: the shows of chapter n played headless through a loopback device (48 kHz) as the game does,
    ///     DECIDE/AUTO/skip scripted: AUTO off (each held line waits 1.5 s for DECIDE), then from 30 s AUTO on with DECIDE every
    ///     4 s, the skip at 45 s (fade out). Per 0.5 s: level of the mix, show clock, which track plays (V drama, M music), held.
    ///     Counts the 0.1-s blocks without sound (no track playing or &lt; −80 dBFS) while a line is held, the drama is not over,
    ///     or it fades out after the skip (the music stopping).
    /// </summary>
    public static bool Audio(string isoPath, int chapter)
    {
        const int Rate = 48000, Hz = 60;
        using var dev = new Kansei.Audio.AudioDevice(Rate);
        if (!dev.Enabled) return false;
        using var iso = new Iso9660(isoPath);
        var media = new StoryMedia(isoPath, null) { Audio = dev };
        var c = MangaChapters(iso)[chapter];
        int silentHeld = 0, silentRun = 0, silentFade = 0;
        foreach (var r in StoryMode.Shows(c, false).Concat(StoryMode.Shows(c, true)))
        {
            var m = StoryMedia.Decode(isoPath, r);
            media.Upload(m, all: true);
            var s = m.Show;
            s.Auto = false;
            var pcm = new short[Rate / Hz * 2];
            double sq = 0, held = 0, nextOk = 0;
            float fade = -1;
            var skippedAt = double.MaxValue;
            int heldBlocks = 0, n = 0;
            Console.WriteLine($"[Story] {r}: Spur {m.Voice} {m.VoiceSeconds:0.0} s" + (m.Music != "" ? $", Musik {m.Music} ×{m.MusicGains.Min():0.00}–{m.MusicGains.Max():0.00}" : ", keine Musikspur (hält nicht)"));
            for (var tick = 0; fade < StoryMode.Fade; tick++)
            {
                var t = tick / (double)Hz;
                if (fade >= 0) media.Play(m, 1.0 / Hz, false, 1 - fade / StoryMode.Fade);
                else
                {
                    held = s.Held ? held + 1.0 / Hz : 0;
                    if (t >= 30 && !s.Auto) s.Auto = true;
                    if ((s.Held && held >= 1.5) || (s.Auto && t >= nextOk + 4 && t < 45))
                    {
                        s.Next();
                        (nextOk, held) = (t, 0);
                    }
                    if (t >= 45)
                    {
                        skippedAt = s.Time;
                        s.End();
                    }
                    media.Play(m, 1.0 / Hz, true);
                    if (s.Done) fade = 0;
                }
                if (fade >= 0) fade += 1f / Hz;
                dev.Render(pcm);
                foreach (var v in pcm) sq += (double)v * v;
                if (s.Held) heldBlocks++;
                if (++n % (Hz / 10) != 0) continue;
                var db = 10 * Math.Log10(sq / (pcm.Length * n) / (32768.0 * 32768) + 1e-12);
                var running = m.Track?.Playing == true || m.MusicTrack?.Playing == true;
                var still = (!running || db < -80) && m.VoiceSeconds > 0;
                if (still && fade >= 0 && fade < StoryMode.Fade * 0.8f && skippedAt < m.VoiceSeconds - 0.2) silentFade++;
                else if (still && fade < 0 && heldBlocks > 0) silentHeld++;
                else if (still && fade < 0 && s.Time < m.VoiceSeconds - 0.2) silentRun++;
                if (tick % (Hz / 2) == Hz / 2 - 1 || still)
                    Console.WriteLine($"[Story]   {t,6:0.0} s  Uhr {s.Time,6:0.00}  {db,6:0.0} dBFS  {(m.Track?.Playing == true ? "V" : "-")}{(m.MusicTrack?.Playing == true ? "M" : "-")}" +
                                      $"{(s.Held ? "  gehalten" : "")}{(fade >= 0 ? $"  Ausblende {1 - fade / StoryMode.Fade:0.00}" : "")}{(still ? "  STILL" : "")}");
                (sq, n, heldBlocks) = (0, 0, 0);
            }
            media.Free(m);
        }
        Console.WriteLine($"[Story] Ton Kapitel {chapter}: stille 0,1-s-Blöcke {silentHeld} beim Halten, {silentRun} vor dem Spurende, {silentFade} beim Ausblenden");
        return silentHeld + silentRun + silentFade == 0;
    }

    public static bool Run(Iso9660 iso, int? only = null)
    {
        var chapters = Chapters(iso);
        var objects = iso.OpenAfs("CDVD/DATA/MANGA/MG_OBJ.AFS");
        int lines = 0, bad = 0;
        Console.WriteLine("[Story] Kapiteltabelle (ELF 0x2A3290/0x2A33C0/0x2A3770/0x2A2EA0) und Szenen (MG_OBJ STRnn.BIN):");
        foreach (var c in chapters)
        {
            var text = StoryText.Chapters[c.Index];
            var goal = StoryRules.Of(c.Rule, c.Rival >= 0);
            var disc = Script(objects, c.Index);
            var counts = disc == null ? "keine Szene auf der Disc (eigener Text)" : string.Join("+", disc.Select(p => p.Count));
            Console.WriteLine($"[Story] {c.Index,2} {text.Title,-26} {StoryScript.Courses[c.Course]}{(c.Reverse ? " bergauf" : "")}{(c.Wet ? " nass" : "")} " +
                              $"{CarPaint.Cars[c.Hero]} gegen {(c.Rival < 0 ? "-" : CarPaint.Cars[c.Rival])}, Code {c.Rule} ({c.Param}) = {goal}; Szene {counts}");
            if (disc == null) continue;
            lines += disc.Sum(p => p.Count);
            foreach (var e in Mismatches(disc, text.Scene, c.Index))
            {
                Console.WriteLine($"[Story]    FEHLER {e}");
                bad++;
            }
        }
        Console.WriteLine($"[Story] {lines} Zeilen auf der Disc übersetzt, {bad} Abweichungen");

        Console.WriteLine($"[Story] Durchlauf mit dem Autopiloten (Fähigkeit {BattleRun.Autopilot.Skill:0.00}):");
        var results = new List<(int, BattleOutcome)>();
        foreach (var c in chapters)
        {
            if (only is { } o && c.Index != o) continue;
            var outcome = Play(iso, c, out var log);
            results.Add((c.Index, outcome));
            Console.WriteLine($"[Story] {c.Index,2} {StoryText.Chapters[c.Index].Title,-26} {outcome,-4} {log}");
        }
        Console.WriteLine($"[Story] Ergebnis: {results.Count(r => r.Item2 == BattleOutcome.Win)} von {results.Count} gewonnen" +
                          (results.Any(r => r.Item2 != BattleOutcome.Win) ? $", nicht: {string.Join(' ', results.Where(r => r.Item2 != BattleOutcome.Win).Select(r => r.Item1))}" : ""));
        return bad == 0;
    }

    /// <summary>
    ///     --story-check calibrate [--player-skill 0.55]: the autopilot as a player (a NORMAL one with his mistakes, the
    ///     calibration behind the table): per battle chapter the highest rival skill 0.1…1 it still beats (bisection; wins are
    ///     not strictly monotonic in skill, so this is a guide), and where even 0.1 is too strong (a car that outclasses the
    ///     hero's) the highest engine torque 0.6…1 of the rival's car at 0.1; per time-limit chapter its time without a limit.
    ///     The numbers behind <see cref="StoryRules.RivalSkill"/>, <see cref="StoryRules.RivalPower"/> and <see cref="StoryRules.Limit"/>.
    ///     STORY_CAL_AT=k: instead the highest engine torque 0.5…1 the player beats with the rival at skill k.
    /// </summary>
    public static void Calibrate(Iso9660 iso)
    {
        Console.WriteLine($"[Kalibrierung] Spieler: Autopilot mit Können {BattleRun.Autopilot.Skill:0.00}");
        foreach (var c in Chapters(iso))
        {
            var goal = StoryRules.Of(c.Rule, c.Rival >= 0);
            if (goal is Goal.Delivery or Goal.Thrill) continue;
            var drive = Load(iso, c);
            if (goal == Goal.TimeLimit)
            {
                Play(drive, c, out var solo, limit: 100000);
                Console.WriteLine($"[Kalibrierung] {c.Index,2} {goal,-11} Autopilot ohne Grenze: {solo}");
                continue;
            }
            float lo = 0.1f, hi = 1;
            if (Environment.GetEnvironmentVariable("STORY_CAL_AT") is { } at && float.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, out var atSkill))
            {
                // the engine torque at a given skill (a strong driver in a detuned car: the story's last battle)
                (lo, hi) = (0.5f, 1);
                if (Play(drive, c, out _, skill: atSkill, power: hi) == BattleOutcome.Win) lo = hi;
                else
                    for (var i = 0; i < 6; i++)
                    {
                        var mid = (lo + hi) / 2;
                        if (Play(drive, c, out _, skill: atSkill, power: mid) == BattleOutcome.Win) lo = mid;
                        else hi = mid;
                    }
                Console.WriteLine($"[Kalibrierung] {c.Index,2} {goal,-11} höchstes Motormoment bei {atSkill:0.00}, das der Spieler schlägt: {lo:0.000}");
                continue;
            }
            if (Play(drive, c, out _, skill: lo) != BattleOutcome.Win)
            {
                // even the weakest driver is too fast in that car: detune it (engine torque) instead of a skill below the scale
                (lo, hi) = (0.6f, 1);
                if (Play(drive, c, out _, skill: 0.1f, power: lo) != BattleOutcome.Win) hi = lo;
                else
                    for (var i = 0; i < 5; i++)
                    {
                        var mid = (lo + hi) / 2;
                        if (Play(drive, c, out _, skill: 0.1f, power: mid) == BattleOutcome.Win) lo = mid;
                        else hi = mid;
                    }
                Console.WriteLine($"[Kalibrierung] {c.Index,2} {goal,-11} schon 0,1 zu stark; höchstes Motormoment bei 0,1, das der Spieler schlägt: {(hi == lo ? "keins (auch 0,6 nicht)" : $"{lo:0.000}")}");
                continue;
            }
            for (var i = 0; i < 6; i++)
            {
                var mid = (lo + hi) / 2;
                if (Play(drive, c, out _, skill: mid) == BattleOutcome.Win) lo = mid;
                else hi = mid;
            }
            Console.WriteLine($"[Kalibrierung] {c.Index,2} {goal,-11} höchste Fähigkeit, die der Spieler schlägt: {lo:0.000}");
        }
    }

    /// <summary>Course id with time of day as the game loads it (the disc has no wet night: wet chapters run dry at night).</summary>
    public static string CourseTime(StoryScript.Chapter c) => $"{StoryScript.Courses[c.Course]}_{(c.Night ? "NIT" : c.Wet ? "RIN" : "DAY")}";

    /// <summary>One chapter, autopilot against the chapter's rival (or alone), to the decision; returns the outcome.</summary>
    public static BattleOutcome Play(Iso9660 iso, StoryScript.Chapter c, out string log) => Play(Load(iso, c), c, out log);

    private static Touge.Drive Load(Iso9660 iso, StoryScript.Chapter c) => new(iso, CourseTime(c), c.Reverse, CarSpecs.All[CarPaint.Cars[c.Hero]]);

    /// <param name="skill">Rival skill instead of the story's (calibration), <paramref name="power"/> his engine torque; <paramref name="limit"/>: time limit instead of the story's.</param>
    private static BattleOutcome Play(Touge.Drive drive, StoryScript.Chapter c, out string log, float? skill = null, int? limit = null, float? power = null)
    {
        var goal = StoryRules.Of(c.Rule, c.Rival >= 0);
        if (StoryRules.Solo(goal))
        {
            drive.ResetTo(0);
            var pilot = new RivalPilot(drive.Line, BattleRun.Autopilot);
            var judge = new SoloJudge(goal, limit ?? StoryRules.Limit(c));
            var timer = new LapTimer(drive.Pilot.Length - drive.Start, null);
            var drift = new DriftMeter();
            for (var t = 0f; t < 600 && judge.Outcome == BattleOutcome.None; t += Touge.Drive.Dt)
            {
                drive.Car.Step(pilot.Drive(drive.Car, drive.Ground, [], Touge.Drive.Dt), drive.Ground, Touge.Drive.Dt);
                var along = drive.Pilot.Track(drive.Car.Position).Along;
                timer.Update(along - drive.Start, Touge.Drive.Dt);
                drift.Update(drive.Car.SlipAngle, drive.Car.SpeedKmh, drive.Car.WallHit, Touge.Drive.Dt);
                judge.Update(timer.Phase == LapTimer.State.Running, timer.Phase == LapTimer.State.Finished, timer.Time, drive.Car.WallHit,
                    drift.Total + drift.Score, Touge.Drive.Dt);
            }
            log = $"{judge.Reason}: Zeit {Style.Time(judge.Time)} (Grenze {judge.Param} s), Wandtreffer {judge.WallHits}, Drift {judge.Drift:#,0}";
            return judge.Outcome;
        }
        var rival = StoryRivals.Find(StoryText.Chapters[c.Index].Rival!, CarPaint.Cars[c.Rival], c.Index);
        if (skill is { } k) rival = rival with { Style = rival.Style with { Skill = k }, Power = power ?? 1 };
        var setup = StoryRules.Battle(goal, c.Param, rival)!;
        var race = BattleRun.Create(drive, setup, new AiDriver(new RivalPilot(drive.Line, BattleRun.Autopilot)));
        race.RubberBanding = true; // as for a player in the game
        var b = race.Battle!;
        for (var n = 0; n < 600 / Touge.Drive.Dt && b.Outcome == BattleOutcome.None; n++) race.Tick(Touge.Drive.Dt);
        log = $"{b.Reason} nach {b.DecidedAt:F1} s, Abstand {b.DecidedGap:+0.00;-0.00} s, {rival.Name} ({rival.Car}, Fähigkeit {rival.Style.Skill:0.00}), Kontakte {race.Contacts}";
        return b.Outcome;
    }
}
