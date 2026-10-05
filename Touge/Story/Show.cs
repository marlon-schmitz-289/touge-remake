using System.Numerics;
using Touge.Formats;

namespace Touge.Story;

/// <summary>A subtitle: from <see cref="Time" /> (s on the show's voice track) "SPEAKER|text" until the next one.</summary>
public readonly record struct ShowLine(double Time, string Line);

/// <summary>
///     The clock and controls of a story show (a manga sequence or a portrait scene) played to its voice track: the time
///     follows the track while it plays (<see cref="Tick" />), the subtitle is the last line started. DECIDE (<see cref="Next" />)
///     jumps to the next line; with AUTO off the show holds at the end of each line until DECIDE. <see cref="Seeked" /> tells
///     the player to restart the track at <see cref="Time" />.
/// </summary>
public sealed class Show(IReadOnlyList<ShowLine> lines, double length, IReadOnlyList<double>? steps = null)
{
    public IReadOnlyList<ShowLine> Lines => lines;
    public double Length => length;
    public double Time { get; private set; }
    public bool Auto { get; set; } = true;
    /// <summary>Waiting for DECIDE at the end of a line (AUTO off).</summary>
    public bool Held { get; private set; }
    public bool Done => Time >= length;
    public bool Seeked { get; set; }
    /// <summary>Where DECIDE jumps to: line starts, or (no subtitles) the given steps (panel starts).</summary>
    private readonly double[] _steps = (lines.Count > 0 ? lines.Select(l => l.Time) : steps ?? []).Order().ToArray();
    private double _holdAt;

    /// <summary>Index of the subtitle shown at the current time, −1 = none yet.</summary>
    public int Line => LineAt(Time);

    public int LineAt(double t)
    {
        var i = -1;
        while (i + 1 < lines.Count && lines[i + 1].Time <= t) i++;
        return i;
    }

    /// <param name="audio">The voice track's position while it plays (it leads the clock), else null (the clock runs on).</param>
    public void Tick(double dt, double? audio)
    {
        if (Held || Done) return;
        var before = Time;
        Time = audio is { } a ? Math.Max(Time, a) : Time + dt; // a track just restarted may report a hair before where it was put
        if (Auto) return;
        // AUTO off: stop right before the next step a line was shown before
        var next = _steps.FirstOrDefault(s => s > before, double.MaxValue);
        if (Time < next || (LineAt(before) < 0 && lines.Count > 0)) return;
        (Time, Held, _holdAt) = (next - 1e-3, true, next); // the line still shows
    }

    /// <summary>DECIDE: on to the next step (line), or the end after the last one.</summary>
    public void Next()
    {
        var at = Held ? _holdAt : _steps.FirstOrDefault(s => s > Time + 1e-6, length);
        (Time, Held, Seeked) = (Math.Min(at, length), false, true);
    }

    public void Seek(double t) => (Time, Held, Seeked) = (Math.Clamp(t, 0, length), false, true);

    /// <summary>Skip the rest of the show.</summary>
    public void End() => (Time, Held) = (length, false);
}

/// <summary>
///     A manga sequence (MG_KOMAM <c>KOMATCnn</c>, FORMATS.md "Story – Manga") ready to draw at any frame (60 Hz of the drama
///     track): panels with position, slide, fade in/stay/fade out, backgrounds (skies scrolling, title cards) with fades.
///     Evaluated from the frame alone, so seeking costs nothing.
/// </summary>
public sealed class KomaSequence
{
    /// <param name="Name">kk_nn: picture nn of MG_KOMAF KOMAkk.FPK.</param>
    /// <param name="At">P_: top-left on the 640 × 448 screen.</param>
    /// <param name="From">S_: slides in from At + offset over <paramref name="Slide" /> frames.</param>
    public sealed record Panel(string Name, int Start, Vector2 At, Vector2 From, int Slide, int In, int Life, int Out, bool Dark);

    /// <param name="Name">KOMABG picture (sky summer_d/…, title card MTnn, M00A…).</param>
    /// <param name="Scroll">BS_: offset reached after <paramref name="ScrollFrames" />.</param>
    public sealed record Backdrop(string Name, int Start, Vector2 At, int In, Vector2 Scroll, int ScrollFrames, int OutAt, int Out)
    {
        public bool Sky => char.IsLower(Name[0]);
    }

    public const int DefaultIn = 30;
    public List<Panel> Panels { get; } = [];
    public List<Backdrop> Backdrops { get; } = [];
    /// <summary>QUIT: the end (frames).</summary>
    public int Quit { get; private set; }

    public static KomaSequence Parse(IEnumerable<Manga.Cue> cues)
    {
        var k = new KomaSequence();
        foreach (var cue in cues)
        {
            Vector2 at = default, slide = default;
            int slideN = 0, fin = 0, life = 0, fout = 0;
            var bgIn = -1;
            Vector2? bgAt = null;
            foreach (var tok in cue.Tokens)
            {
                var (name, arg) = Split(tok);
                var n = Numbers(arg);
                switch (name)
                {
                    case "P" when n.Length >= 2: at = new Vector2(n[0], n[1]); break;
                    case "S" when n.Length >= 3: (slide, slideN) = (new Vector2(n[0], n[1]), n[2]); break;
                    case "I" when n.Length >= 1: fin = n[0]; break;
                    case "L" when n.Length >= 1: life = n[0]; break;
                    case "O" when n.Length >= 1: fout = n[0]; break;
                    case "F" or "BORNDARK":
                        k.Panels.Add(new Panel(arg, cue.Frame, at, at + slide, slideN, fin, life, fout, name == "BORNDARK"));
                        (at, slide, slideN, fin, life, fout) = (default, default, 0, 0, 0, 0);
                        break;
                    case "BG":
                        k.Backdrops.Add(new Backdrop(arg, cue.Frame, default, DefaultIn, default, 0, int.MaxValue, 0));
                        break;
                    case "BI" when n.Length >= 2: bgAt = new Vector2(n[0], n[1]); break;
                    case "BI" when n.Length == 1: bgIn = n[0]; break;
                    case "BS" when n.Length >= 3 && k.Backdrops.Count > 0:
                        k.Backdrops[^1] = k.Backdrops[^1] with { Scroll = new Vector2(n[0], n[1]), ScrollFrames = n[2] };
                        break;
                    case "BO" when n.Length >= 1 && k.Backdrops.Count > 0:
                        k.Backdrops[^1] = k.Backdrops[^1] with { OutAt = cue.Frame, Out = n[0] };
                        break;
                    case "QUIT": k.Quit = cue.Frame; break;
                }
            }
            if (k.Backdrops.Count > 0 && k.Backdrops[^1].Start == cue.Frame)
                k.Backdrops[^1] = k.Backdrops[^1] with { At = bgAt ?? default, In = bgIn >= 0 ? bgIn : DefaultIn };
        }
        if (k.Quit == 0) k.Quit = k.Panels.Select(p => p.Start + p.Life + p.Out).Concat(k.Backdrops.Select(b => b.OutAt == int.MaxValue ? b.Start : b.OutAt + b.Out)).DefaultIfEmpty(0).Max();
        return k;
    }

    private static (string Name, string Arg) Split(string tok)
    {
        var bar = tok.IndexOf('_');
        return bar < 0 ? (tok, "") : (tok[..bar], tok[(bar + 1)..]);
    }

    private static int[] Numbers(string arg) =>
        arg.Trim('(', ')').Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var v) ? v : int.MinValue).TakeWhile(v => v != int.MinValue).ToArray();

    /// <summary>The panels on screen at <paramref name="frame" /> with position and opacity, oldest first (a later one with the same name replaces it).</summary>
    public IEnumerable<(Panel Panel, Vector2 At, float Alpha)> PanelsAt(double frame)
    {
        for (var i = 0; i < Panels.Count; i++)
        {
            var p = Panels[i];
            var t = frame - p.Start;
            if (t < 0 || t >= p.Life + p.Out) continue;
            var replaced = false;
            for (var j = i + 1; j < Panels.Count && Panels[j].Start <= frame; j++) replaced |= Panels[j].Name == p.Name;
            if (replaced) continue;
            var a = p.In > 0 ? Math.Min(1, t / p.In) : 1;
            if (t > p.Life && p.Out > 0) a *= 1 - (t - p.Life) / p.Out;
            var at = p.Slide > 0 && t < p.Slide ? Vector2.Lerp(p.From, p.At, (float)(t / p.Slide)) : p.At;
            yield return (p, at, (float)a);
        }
    }

    /// <summary>The background at <paramref name="frame" /> (the last one started) with its scroll offset and opacity.</summary>
    public (Backdrop Backdrop, Vector2 Offset, float Alpha)? BackdropAt(double frame)
    {
        var b = Backdrops.LastOrDefault(x => x.Start <= frame);
        if (b == null) return null;
        var t = frame - b.Start;
        var a = b.In > 0 ? Math.Min(1, t / b.In) : 1;
        if (frame >= b.OutAt) a *= Math.Max(0, 1 - (frame - b.OutAt) / Math.Max(1, b.Out));
        var s = b.ScrollFrames > 0 ? b.Scroll * (float)(t / b.ScrollFrames) : Vector2.Zero;
        return (b, s, (float)a);
    }

    /// <summary>Panel starts in s (where DECIDE jumps without subtitles).</summary>
    public IEnumerable<double> Steps => Panels.Select(p => p.Start / 60.0).Distinct();
}

/// <summary>
///     A portrait scene part (ROBJ slot) at any time of its voice track: the portrait shown, the mouth frame of each face
///     (lip-sync digits of the page from its first balloon on, for the faces talking) and the eyes (a blink every few seconds).
/// </summary>
/// <param name="missing">Portraits the archive lacks (STR21 P_11, STR22): the one before stays, its faces keep still.</param>
public sealed class PortraitScene(IReadOnlyList<StoryScript.Stage> stages, IReadOnlyList<string> lips, IReadOnlySet<int>? missing = null)
{
    public const int Faces = 3;
    public IReadOnlyList<StoryScript.Stage> Stages => stages;

    /// <summary>Portrait numbers used (to load).</summary>
    public IEnumerable<int> Pictures => stages.Where(s => s.Step == StoryScript.Step.Picture).Select(s => s.Value).Where(n => missing?.Contains(n) != true).Distinct();

    /// <summary>Portrait at <paramref name="t" /> (−1 = none yet) and its faces' mouth frames 0–5 (0 = closed).</summary>
    public (int Picture, int[] Mouth) At(double t)
    {
        var picture = -1;
        var talking = new bool[Faces];
        var mouth = new int[Faces];
        string? digits = null;
        var page = 0.0;
        var still = false;
        foreach (var s in stages)
        {
            if (s.Time > t) break;
            switch (s.Step)
            {
                case StoryScript.Step.Picture when missing?.Contains(s.Value) == true: still = true; break;
                case StoryScript.Step.Picture: (picture, still) = (s.Value, false); break;
                case StoryScript.Step.Talk when s.Value < Faces: talking[s.Value] = true; break;
                case StoryScript.Step.Quiet when s.Value < Faces: talking[s.Value] = false; break;
                case StoryScript.Step.Page:
                    (digits, page) = (s.Value < lips.Count ? lips[s.Value] : null, s.Time);
                    break;
            }
        }
        var f = (int)((t - page) * 60);
        var d = digits != null && f >= 0 && f < digits.Length ? digits[f] - '0' : 0;
        for (var k = 0; k < Faces; k++) mouth[k] = talking[k] && !still ? Math.Clamp(d, 0, 5) : 0;
        return (picture, mouth);
    }

    /// <summary>Eye frame 0 open, 1 half, 2 shut: a blink (half, shut, half; 3 frames each) every 2.5–5 s, per face out of step.</summary>
    public static int Eyes(double t, int face)
    {
        var period = 2.5 + (face * 0.77 + 0.31) % 1 * 2.5;
        var f = (int)(((t + face * 1.3) % period) * 60);
        return f switch { < 3 => 1, < 6 => 2, < 9 => 1, _ => 0 };
    }
}
