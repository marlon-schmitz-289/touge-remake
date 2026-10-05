using System.Buffers.Binary;
using System.Text;

namespace Touge.Formats;

/// <summary>
///     Story mode data of the original (FORMATS.md "Story"): the chapter table in the ELF and the scene scripts
///     MANGA/MG_OBJ.AFS <c>STRnn.BIN</c> ("ROBJ"; nn = chapter 02–30, chapters 0/1 have none).
/// </summary>
public static class StoryScript
{
    /// <summary>ELF on the disc root; file offset = address − <see cref="ElfBase"/>.</summary>
    public const string ElfPath = "SLPM_652.68";
    public const int ElfBase = 0xFFF80, Chapters = 31;

    /// <summary>Course index of the chapter table → course id (ELF course table 0x24CD00).</summary>
    public static readonly string[] Courses = ["MYOUGI0", "USUI0", "AKAGI", "AKINA", "HAPPOU", "IROHA", "MYOUGI", "USUI", "MOMIJI", "SHIONA", "SHOMARU"];

    /// <param name="Course">Index into <see cref="Courses"/>.</param>
    /// <param name="Reverse">Direction byte ≠ 0: the course's _O line (uphill on the passes).</param>
    /// <param name="Wet">Weather byte (only chapter 10, Kenta in the rain).</param>
    /// <param name="Night">Time byte (every chapter: night).</param>
    /// <param name="Rule">Objective code (+0x0E of the 22-byte record, bit field: 1 goal, 2 positions, 4 time, 8 special).</param>
    /// <param name="Param">u16 +0x10: time limit in s (rules with bit 4), 100 for the special runs.</param>
    /// <param name="Hero">Car id (<see cref="CarPaint.Cars"/>) of the story's driver (setup record via 0x2A3770).</param>
    /// <param name="Rival">Car id of the opponent (setup record 0x2A2EA0 + 16 × figure), −1 = a run alone.</param>
    public sealed record Chapter(int Index, int Course, bool Reverse, bool Wet, bool Night, int Rule, int Param, int Hero, int Rival);

    /// <summary>
    ///     The 31 chapters from the ELF as set up by sub_170B50 when a chapter starts: 8 bytes at 0x2A3290 + 8·n → 0x328154
    ///     (chapter, ?, course, direction, weather, time, figure, ?), 22 bytes at 0x2A33C0 + 22·n → 0x32815C (objective),
    ///     the driver's setup record through the pointer table 0x2A3770 → 0x3281B8 and, figure ≥ 0, the opponent's 16-byte
    ///     record 0x2A2EA0 + 16·figure → 0x3281C8 (byte 1 = car id).
    /// </summary>
    public static Chapter[] ReadChapters(ReadOnlySpan<byte> elf)
    {
        var list = new Chapter[Chapters];
        for (var n = 0; n < Chapters; n++)
        {
            var sel = elf.Slice(0x2A3290 - ElfBase + 8 * n, 8);
            var obj = elf.Slice(0x2A33C0 - ElfBase + 22 * n, 22);
            if (sel[0] != n || sel[2] >= Courses.Length) throw new InvalidDataException($"story chapter table: entry {n} looks wrong ({Convert.ToHexString(sel)})");
            var heroAt = BinaryPrimitives.ReadInt32LittleEndian(elf[(0x2A3770 - ElfBase + 4 * n)..]) - ElfBase;
            var figure = (sbyte)sel[6];
            var rival = figure < 0 ? -1 : elf[0x2A2EA0 - ElfBase + 16 * figure + 1];
            int hero = elf[heroAt + 1];
            if (hero >= CarPaint.Cars.Length || rival >= CarPaint.Cars.Length) throw new InvalidDataException($"story chapter {n}: car id out of range");
            list[n] = new Chapter(n, sel[2], sel[3] != 0, sel[4] != 0, sel[5] != 0, obj[14], BinaryPrimitives.ReadUInt16LittleEndian(obj[16..]), hero, rival);
        }
        return list;
    }

    /// <summary>One utterance of a scene: speaker as the script names it (F_…, "" = the panel's own), Shift-JIS text without the command tokens.</summary>
    public sealed record Line(string Speaker, string Text);

    private static readonly Lazy<Encoding> Sjis = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    });

    /// <summary>
    ///     A "ROBJ" scene script as parts (one per fade, E_n; empty parts dropped) of utterances. Header: "ROBJ", u32, offsets;
    ///     u32 at 0x18 = a 12-byte block right before the token pool: NUL-separated Shift-JIS tokens up to the font name
    ///     (KSTnn/KNJnn). Commands are ASCII letters with an optional _arg: P_ picture, C_ panel, A_ frame cue, W_/WF_
    ///     wait, N new page, K_ page style, Q_ effect, U_ shake, F_ speaker of the next balloon, E_ fade (end of a part).
    ///     Text tokens are balloons ('\n' = line break); the balloons up to the next wait, page break or speaker are one utterance.
    /// </summary>
    public static List<List<Line>> ParseScript(ReadOnlySpan<byte> robj) => Parse(robj, null, null);

    /// <summary>
    ///     For each utterance of <see cref="ParseScript" /> the time in s on the part's voice track (<see cref="Manga.SceneVoice" />)
    ///     when it appears: the last A_n (page) or WF_n (balloon) before it, n in 1/30 s of ADX time (the game doubles it to
    ///     60-Hz frames, 0x1D0CD0/0x1CFC28); 0 before the first cue.
    /// </summary>
    public static List<List<double>> Times(ReadOnlySpan<byte> robj)
    {
        var times = new List<List<double>>();
        Parse(robj, times, null);
        return times;
    }

    /// <summary>What a scene's staging step does (<see cref="Staging" />).</summary>
    public enum Step
    {
        /// <summary>P_n: show portrait n (MG_STR <c>nn.ICP</c>).</summary>
        Picture,
        /// <summary>C_k: face k talks (lip sync on).</summary>
        Talk,
        /// <summary>K_k: face k stops talking.</summary>
        Quiet,
        /// <summary>The first balloon of page n (after N): its lip string (<see cref="Manga.Lips" />) starts here.</summary>
        Page,
        /// <summary>Utterance n of <see cref="ParseScript" /> appears.</summary>
        Line,
        /// <summary>E_: the part fades out.</summary>
        End,
    }

    public readonly record struct Stage(double Time, Step Step, int Value);

    /// <summary>
    ///     Per part (as <see cref="ParseScript" />) the staging on the part's voice track in s: portraits, who talks, pages and
    ///     utterances at the cue in effect (A_/WF_, 1/30 s) when their token comes.
    /// </summary>
    public static List<List<Stage>> Staging(ReadOnlySpan<byte> robj)
    {
        var stage = new List<List<Stage>>();
        Parse(robj, null, stage);
        return stage;
    }

    private static List<List<Line>> Parse(ReadOnlySpan<byte> robj, List<List<double>>? times, List<List<Stage>>? staging)
    {
        if (robj.Length < 0x20 || !robj[..4].SequenceEqual("ROBJ"u8)) throw new InvalidDataException("no ROBJ script");
        var at = BinaryPrimitives.ReadInt32LittleEndian(robj[0x18..]) + 12;
        var parts = new List<List<Line>> { new() };
        var cues = new List<List<double>> { new() };
        var stage = new List<List<Stage>> { new() };
        var text = new StringBuilder();
        var speaker = "";
        int cue = 0, start = 0, page = 0;
        var paged = false;
        void Add(Step step, int value) => stage[^1].Add(new Stage(cue / 30.0, step, value));
        void Flush()
        {
            if (text.Length > 0)
            {
                stage[^1].Add(new Stage(start / 30.0, Step.Line, parts[^1].Count));
                parts[^1].Add(new Line(speaker, text.ToString()));
                cues[^1].Add(start / 30.0);
            }
            (speaker, text.Length) = ("", 0);
        }
        while (at < robj.Length)
        {
            var len = robj[at..].IndexOf((byte)0);
            if (len < 0) len = robj.Length - at;
            var raw = robj.Slice(at, len);
            at += len + 1;
            if (raw.Length == 0) continue;
            var tok = Sjis.Value.GetString(raw);
            if (IsCommand(tok, out var name))
            {
                if (name is "KST" or "KNJ") break; // the font of the page: end of the script
                if (name == "F")
                {
                    Flush();
                    speaker = tok[2..];
                }
                else if (name == "E")
                {
                    Flush();
                    Add(Step.End, 0);
                    (cue, page, paged) = (0, 0, false); // every part has its own voice track
                    if (parts[^1].Count > 0)
                    {
                        parts.Add([]);
                        cues.Add([]);
                        stage.Add([]);
                    }
                    else stage[^1].Clear();
                }
                else if (name is "W" or "WF" or "N") Flush();
                if (name == "N") (page, paged) = (page + 1, false); // pages are counted by N (lip strings are numbered so)
                var arg = int.TryParse(tok[Math.Min(tok.Length, name.Length + 1)..], out var n) ? n : -1;
                if (name is "A" or "WF" && arg >= 0) cue = Math.Max(cue, arg); // waits for that time: an earlier one (STR06) passes at once
                else if (name == "P" && arg >= 0) Add(Step.Picture, arg);
                else if (name == "C" && arg >= 0) Add(Step.Talk, arg);
                else if (name == "K" && arg >= 0) Add(Step.Quiet, arg);
                continue;
            }
            if (!paged) Add(Step.Page, page);
            paged = true;
            if (text.Length == 0) start = cue;
            text.Append(tok.Replace("\n", "")); // '\n' breaks lines inside a balloon
        }
        Flush();
        if (parts[^1].Count == 0)
        {
            parts.RemoveAt(parts.Count - 1);
            cues.RemoveAt(cues.Count - 1);
            stage.RemoveAt(stage.Count - 1);
        }
        times?.AddRange(cues);
        staging?.AddRange(stage);
        return parts;
    }

    /// <summary>A command token: ASCII capitals, then nothing, "_arg" or (fonts) two digits.</summary>
    private static bool IsCommand(string tok, out string name)
    {
        var i = 0;
        while (i < tok.Length && tok[i] is >= 'A' and <= 'Z') i++;
        name = tok[..i];
        if (i == 0 || i > 3) return false;
        if (i == tok.Length) return true;
        if (tok[i] == '_') return true;
        return name is "KST" or "KNJ" && tok.Length == i + 2 && char.IsAsciiDigit(tok[i]) && char.IsAsciiDigit(tok[i + 1]);
    }
}
