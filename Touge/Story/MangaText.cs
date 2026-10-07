using System.Globalization;

namespace Touge.Story;

/// <summary>
///     English subtitles of the manga sequences' audio dramas (MANGAV/MG_KOMAS <c>nn_00.adx</c>, KOMATC32 <c>10_03.adx</c>):
///     the disc has no text for them (speech balloons are part of the pictures), so these are the remake's own translation
///     of what is heard, each line "seconds|SPEAKER|text" at the time it starts on the track (speaker empty: the narrator, or
///     voices the drama does not name). Keyed by panel timeline (KOMATCn = chapter n; 32 = chapter 9 after the race; 27 and
///     30 have no speech). Also the English of the title cards (MTnn episode titles, the M… part/date cards).
/// </summary>
public static partial class MangaText
{
    public static IReadOnlyList<ShowLine> Lines(int timeline) =>
        Koma.TryGetValue(timeline, out var lines) ? [.. lines.Select(Parse)] : [];

    private static ShowLine Parse(string line)
    {
        var bar = line.IndexOf('|');
        return new ShowLine(double.Parse(line[..bar], CultureInfo.InvariantCulture), line[(bar + 1)..]);
    }

    /// <summary>English of a KOMABG title card (MTnn: the manga episode's title; M…: part and date cards), null for skies.</summary>
    public static string? Card(string name) => Cards.GetValueOrDefault(name);

    public static readonly Dictionary<string, string> Cards = new()
    {
        ["MT00"] = "LET'S BUY AN 86! (1)", ["MT01"] = "LET'S BUY AN 86! (2)", ["MT02"] = "THE ULTIMATE TOFU-SHOP DRIFT", ["MT03"] = "DOGFIGHT",
        ["MT04"] = "IKETANI'S PRECIOUS EXPERIENCE", ["MT05"] = "YOU FOOL OF A DAD, GIVE THE 86 BACK!!", ["MT06"] = "BATTLE AT THE LIMIT!!",
        ["MT07"] = "DEATH MATCH OF MADNESS", ["MT08"] = "SPARKS FLY AT THE LINE CROSS!!", ["MT09"] = "HOT WIND!! WILD RUN!! USUI PASS",
        ["MT10"] = "LEARN THE TERROR OF A RAIN BATTLE!!", ["MT11"] = "HIGH TECH VS. SUPER TECHNIQUE", ["MT12"] = "TAKUMI, TAKE THE WHEEL!!",
        ["MT13"] = "READY TO GO DOWN IN FLAMES - FIREBALL BATTLE", ["MT14"] = "RELEASED FROM THE SEAL", ["MT15"] = "A FEELING OF AWAKENING",
        ["MT16"] = "CHALLENGE!! IROHAZAKA", ["MT17"] = "INSIDE THE INSIDE LINE!!", ["MT18"] = "A COURSE RECORD IN FLAMES",
        ["MT19"] = "THE RIVALS' EVE", ["MT20"] = "CLASH OF PRIDE!!", ["MT21"] = "THE OPENING SKIRMISH", ["MT22"] = "THE FD'S CLOSE CALL!!",
        ["MT23"] = "STRIKE THROUGH THE TERROR!!", ["MT24"] = "WHICH ONE OF US IS CRAZY!?", ["MT25"] = "DON'T LOOK BACK",
        ["MT26"] = "ATTACK, TAKUMI!!", ["MT27"] = "BLIND ATTACK", ["MT28"] = "FLAT-OUT HILL CLIMB!!", ["MT29"] = "THE MAN WHO DRIVES THE FF TURBO",
        ["MT30"] = "THE NIGHTMARE MACHINE",
        ["M00A"] = "THE HACHI-ROKU OF AKINA", ["M19A"] = "THE AKAGI REDSUNS", ["M23A"] = "AND ONE YEAR LATER...", ["M24A"] = "PROJECT D",
        ["M31A"] = "APRIL", ["M31B"] = "APRIL - THE GUNMA AREA",
    };

    /// <summary>The dramas' lines by timeline, from the local <c>MangaText.Local.cs</c> (gitignored, a translation of the original's audio); empty without it.</summary>
    public static readonly Dictionary<int, string[]> Koma = Local();

    private static Dictionary<int, string[]> Local()
    {
        var koma = new Dictionary<int, string[]>();
        LocalKoma(koma);
        return koma;
    }

    static partial void LocalKoma(Dictionary<int, string[]> koma);
}
