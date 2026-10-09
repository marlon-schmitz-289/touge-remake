using Kansei.Physics;

namespace Touge.Race;

/// <summary>
///     LEGEND OF THE STREETS (公道最速伝説): the original's rival roster, read out of the ELF (FORMATS.md "Legend of the
///     Streets"): per course grid slot up to 8 rival ids (table 0x29B2E0), per rival an 8-byte record (0x2A2D90: id, course,
///     direction, weather, night, figure = setup record of the car with the character's livery); two of them only show
///     once a condition holds (0x1663B0: Bunta on Akina, Takumi of Project D on Irohazaka). Names/teams after the
///     original's name strips (KCRSSEL2 r_selnm, own English), the AI styles are the remake's own (a ladder per course,
///     stronger towards its end). Each rival also has a theme on the disc (MANGA/MG_BGM.AFS), played on the VS card.
/// </summary>
public static class Legend
{
    /// <summary>
    ///     One rival of the mode. <paramref name="Key"/> = course id + "/" + rival id (unique; the progress key).
    ///     <paramref name="Reverse"/> = the course's reverse line (_O; touge uphill), <paramref name="Night"/>/<paramref name="Wet"/>
    ///     the conditions of the record, <paramref name="Figure"/> its setup record (0x2A2EA0), <paramref name="Theme"/> its
    ///     MG_BGM.AFS track, <paramref name="Secret"/> = shown only once <see cref="Unlocked"/> lets it.
    /// </summary>
    public sealed record Entry(string Key, string CourseId, int Slot, Rivals.Rival Rival, bool Reverse, bool Night, bool Wet, int Figure, string Theme, bool Secret = false);

    /// <summary>Course grid slots of the original (Catalog order): 0–5 the arcade's six courses, 6–10 the Special Stage additions.</summary>
    public static readonly string[] CourseIds = ["MYOUGI0", "USUI0", "AKAGI", "AKINA", "HAPPOU", "IROHA", "MYOUGI", "USUI", "SHOMARU", "MOMIJI", "SHIONA"];

    /// <summary>The six main courses; the additions (slots 6–10) open once this many of them are cleared.</summary>
    public const int MainCourses = 6, ExtraUnlock = 3;

    /// <summary>
    ///     The car every car select keeps as ????? (Time Attack, Legend, Versus; his Impreza): the original hides it until the
    ///     Story's last chapter is cleared (FORMATS.md), the remake also opens it with a win against Bunta; <see cref="CarLocked"/>.
    /// </summary>
    public const string SecretCar = "IMP3";

    /// <summary>The car select's hint on <see cref="SecretCar"/> while it is locked.</summary>
    public const string SecretCarHint = "LOCKED: clear the last STORY chapter or beat BUNTA in LEGEND";

    private static Rivals.Rival R(string id, string name, string team, string car, float skill, float aggression, float drift, int paint = 0) =>
        new(id, name, team, car, new RivalStyle(skill, aggression, drift), paint);

    /// <summary>
    ///     All 34 rivals in the original's id order (0x2A2D90 rows 0–33), grouped by slot as 0x29B2E0 lists them. Skill on the
    ///     one scale (<see cref="RivalPilot.Pace"/>) by rung, the top of it (VS CPU's HARD and LEGEND: the slides of
    ///     <see cref="RivalPilot.SlideShare"/>, a fast player's pace): a main course's ladder 0.8–0.84 (with <see cref="RungPower"/>),
    ///     0.85–0.9, 0.85–0.95, bosses 0.95–1; the additions 0.95–1 stock, the secret ones 1 (LEGEND_ALL=1 --legend-sim
    ///     --player-skill 1: 21 of 34 won, README).
    ///     Byte 3 → <see cref="Entry.Reverse"/>, 4 → <see cref="Entry.Wet"/>, 5 → <see cref="Entry.Night"/>, 6 → <see cref="Entry.Figure"/>.
    /// </summary>
    public static readonly Entry[] All = Tiered(
    [
        // MYOGI (Takumi in Itsuki's AE85 is row 1 of the original, the remake's ladder has him last: the hero is the course's boss, not its second rung)
        E(0, "itsuki", R("itsuki", "ITSUKI TAKEUCHI", "AKINA SPEEDSTARS", "AE85", 0.84f, 0.2f, 0.3f), false, false, false, 0, "ITSUKI.adx"),
        E(0, "shingo", R("shingo", "SHINGO SHOJI", "MYOGI NIGHTKIDS", "EG6", 0.9f, 0.95f, 0.2f), false, false, true, 2, "SHINGO.adx"),
        E(0, "takeshi", R("takeshi", "TAKESHI NAKAZATO", "MYOGI NIGHTKIDS", "R32", 0.85f, 0.55f, 0.1f), true, false, true, 3, "NAKAZATO.adx"),
        E(0, "takumi", R("takumi", "TAKUMI FUJIWARA", "AKINA SPEEDSTARS", "AE85", 0.95f, 0.4f, 0.85f), true, false, false, 1, "TAKUMI01.adx"),
        // USUI
        E(1, "tokyo", R("tokyo", "THE TWO FROM TOKYO", "TOKYO", "S15", 0.82f, 0.55f, 0.4f), false, false, false, 4, "DEBU.adx"),
        E(1, "nobuhiko", R("nobuhiko", "NOBUHIKO AKIYAMA", "SAITAMA", "ALTEZ", 0.85f, 0.45f, 0.35f), true, false, false, 5, "NOBUHIKO.adx"),
        E(1, "mako", R("mako", "MAKO SATO & SAYUKI", "IMPACT BLUE", "SIL80", 0.88f, 0.5f, 0.6f), false, false, true, 6, "MAKO.adx"),
        E(1, "sakamoto", R("sakamoto", "SAKAMOTO", "SAITAMA NORTHWEST ALLIANCE", "CAPPU", 0.95f, 0.5f, 0.4f), true, true, true, 7, "SAKAMOTO.adx"),
        // AKAGI
        E(2, "kyoko", R("kyoko", "KYOKO IWASE", "TEAM KYOKO", "FD3S", 0.82f, 0.75f, 0.5f, 3), true, false, false, 8, "KYOUKO.adx"),
        E(2, "kenta", R("kenta", "KENTA NAKAMURA", "AKAGI REDSUNS", "S14Q", 0.85f, 0.6f, 0.6f), false, true, false, 9, "KENTA.adx"),
        E(2, "keisuke", R("keisuke", "KEISUKE TAKAHASHI", "AKAGI REDSUNS", "FD3S", 0.9f, 0.65f, 0.8f), true, false, true, 10, "KEISUKE01.adx"),
        E(2, "ryosuke", R("ryosuke", "RYOSUKE TAKAHASHI", "AKAGI REDSUNS", "FC3S", 0.95f, 0.45f, 0.6f), false, false, true, 11, "RYOSUKE.adx"),
        // AKINA
        E(3, "kenji", R("kenji", "KENJI", "AKINA SPEEDSTARS", "ONE80", 0.8f, 0.35f, 0.5f), true, false, false, 12, "KENJI.adx"),
        E(3, "iketani", R("iketani", "KOICHIRO IKETANI", "AKINA SPEEDSTARS", "S13", 0.85f, 0.3f, 0.3f), false, false, false, 13, "IKETANI.adx"),
        E(3, "wataru", R("wataru", "WATARU AKIYAMA", "SAITAMA", "AE86L", 0.9f, 0.55f, 0.7f), true, false, true, 14, "WATARU.adx"),
        E(3, "takumi", R("takumi", "TAKUMI FUJIWARA", "AKINA SPEEDSTARS", "AE86T", 0.97f, 0.5f, 0.9f), false, false, true, 15, "TAKUMI02.adx"),
        // HAPPOGAHARA
        E(4, "suetsugu", R("suetsugu", "TORU SUETSUGU", "SEVEN STAR LEAF", "NA6C", 0.82f, 0.5f, 0.6f), false, false, true, 16, "SUETSUGU.adx"),
        E(4, "daiki", R("daiki", "DAIKI NINOMIYA", "TODO SCHOOL", "EK9", 0.9f, 0.6f, 0.2f), true, false, true, 17, "DAIKI.adx"),
        E(4, "sakai", R("sakai", "SMILEY SAKAI", "TODO SCHOOL", "INTGR", 0.95f, 0.55f, 0.2f), false, false, true, 18, "SAKAI.adx"),
        E(4, "tachi", R("tachi", "TOMOYUKI TACHI", "TODO SCHOOL ALUMNI", "EK9", 1f, 0.6f, 0.25f), true, false, true, 19, "TACHI.adx"),
        // IROHAZAKA
        E(5, "seiji", R("seiji", "SEIJI IWAKI", "TEAM EMPEROR", "EVO4", 0.82f, 0.65f, 0.1f), false, false, false, 20, "SEIJI.adx"),
        E(5, "kyoichi", R("kyoichi", "KYOICHI SUDO", "TEAM EMPEROR", "EVO3", 0.86f, 0.6f, 0.15f), false, false, false, 21, "KYOICHI.adx"),
        E(5, "kai", R("kai", "KAI KOGASHIWA", "IROHAZAKA", "MR2", 0.9f, 0.7f, 0.3f), false, false, true, 22, "KAI.adx"),
        E(5, "keisuke", R("keisuke", "KEISUKE TAKAHASHI", "PROJECT D", "FD3S", 0.95f, 0.65f, 0.8f), false, false, true, 23, "KEISUKE02.adx"),
        E(5, "takumi", R("takumi", "TAKUMI FUJIWARA", "PROJECT D", "AE86T", 1f, 0.5f, 0.9f), false, false, true, 24, "TAKUMI03.adx", true),
        // MYOGI+, USUI+, SHOMARU, MOMIJI LINE, SHIONA (Special Stage's own courses, night only)
        E(6, "shingo", R("shingo", "SHINGO SHOJI", "MYOGI NIGHTKIDS", "EG6", 1f, 0.95f, 0.2f), false, false, true, 26, "SHINGO.adx"),
        E(6, "takeshi", R("takeshi", "TAKESHI NAKAZATO", "MYOGI NIGHTKIDS", "R32", 0.95f, 0.55f, 0.1f), true, false, true, 27, "NAKAZATO.adx"),
        E(7, "mako", R("mako", "MAKO SATO & SAYUKI", "IMPACT BLUE", "SIL80", 0.97f, 0.5f, 0.6f), false, false, true, 28, "MAKO.adx"),
        E(8, "wataru", R("wataru", "WATARU AKIYAMA", "SAITAMA", "AE86L", 0.95f, 0.55f, 0.7f), false, false, true, 29, "WATARU.adx"),
        E(9, "suetsugu", R("suetsugu", "TORU SUETSUGU", "SEVEN STAR LEAF", "NA6C", 0.97f, 0.5f, 0.6f), false, false, true, 30, "SUETSUGU.adx"),
        E(9, "kawai", R("kawai", "ATSURO KAWAI", "SEVEN STAR LEAF", "ER34", 0.97f, 0.5f, 0.45f), true, false, true, 31, "ATSUO.adx"),
        E(10, "daiki", R("daiki", "DAIKI NINOMIYA", "TODO SCHOOL", "EK9", 1f, 0.6f, 0.2f), false, false, true, 32, "DAIKI.adx"),
        E(10, "sakai", R("sakai", "SMILEY SAKAI", "TODO SCHOOL", "INTGR", 1f, 0.55f, 0.2f), true, false, true, 33, "SAKAI.adx"),
        // AKINA's fifth: Bunta (row 33 of 0x2A2D90, figure 25)
        E(3, "bunta", R("bunta", "BUNTA FUJIWARA", "FUJIWARA TOFU", "IMP3", 1f, 0.4f, 0.7f), false, false, true, 25, "BUNTA.adx", true),
    ]);

    private static Entry E(int slot, string id, Rivals.Rival r, bool reverse, bool wet, bool night, int figure, string theme, bool secret = false) =>
        new($"{CourseIds[slot]}/{id}", CourseIds[slot], slot, r, reverse, night, wet, figure, theme, secret);

    /// <summary>
    ///     Engine torque of the rival's car by its rung on a main course's ladder (the remake's own balance, in the spirit of
    ///     the original's per-course AI speed table 0x2C7930): the first two rivals run slightly detuned (on top of their balanced car, CarSpecs.Bop),
    ///     the third and fourth at full power; the additions and the secret rivals are always stock.
    /// </summary>
    public static float[] RungPower => [0.9f, 0.95f, 1f, 1f]; // a property: All (above) is initialised first

    /// <summary><see cref="RungPower"/> applied to the main courses' regulars.</summary>
    private static Entry[] Tiered(Entry[] all) =>
    [
        .. all.Select(e =>
        {
            var rung = all.Where(x => x.Slot == e.Slot && !x.Secret).ToList().IndexOf(e);
            return e.Slot < MainCourses && !e.Secret && rung < RungPower.Length ? e with { Rival = e.Rival with { Power = RungPower[rung] } } : e;
        }),
    ];

    /// <summary>A slot's rivals in ladder order (regulars as listed, the secret one last).</summary>
    public static Entry[] Of(int slot) => [.. All.Where(e => e.Slot == slot && !e.Secret), .. All.Where(e => e.Slot == slot && e.Secret)];

    public static Entry? Find(string key) => All.FirstOrDefault(e => e.Key == key);

    /// <summary>Every regular rival of the slot beaten.</summary>
    public static bool Cleared(int slot, Progress p) => Of(slot).Where(e => !e.Secret).All(e => p.Beaten(e.Key));

    public static int ClearedMain(Progress p) => Enumerable.Range(0, MainCourses).Count(s => Cleared(s, p));

    /// <summary>The slot can be picked: main courses always, the additions after <see cref="ExtraUnlock"/> main courses are cleared.</summary>
    public static bool CourseOpen(int slot, Progress p) => slot < MainCourses ? Of(slot).Length > 0 : slot < CourseIds.Length && ClearedMain(p) >= ExtraUnlock;

    /// <summary>
    ///     The rival can be challenged. Regulars: a ladder per course, each after the one before it is beaten (the remake's
    ///     order; the original lists them all at once). Bunta: once figures 0–23 are beaten (0x1663B0 case 4) = every regular
    ///     of the six main courses; Takumi of Project D: once every other rival but Bunta is (case 6). Beaten rivals stay open.
    /// </summary>
    public static bool Unlocked(Entry e, Progress p)
    {
        if (!CourseOpen(e.Slot, p)) return false;
        if (p.Beaten(e.Key)) return true;
        if (e.Secret)
            return e.Rival.Id == "bunta"
                ? All.Where(x => x.Slot < MainCourses && !x.Secret).All(x => p.Beaten(x.Key))
                : All.Where(x => x != e && x.Rival.Id != "bunta").All(x => p.Beaten(x.Key));
        var ladder = Of(e.Slot);
        var i = Array.IndexOf(ladder, e);
        return i == 0 || p.Beaten(ladder[i - 1].Key);
    }

    /// <summary>Shown on the ladder: regulars always, a secret one once unlocked.</summary>
    public static bool Visible(Entry e, Progress p) => !e.Secret || Unlocked(e, p);

    /// <summary>A car the menus keep locked until won: <see cref="SecretCar"/> until the Story's last chapter is cleared or Bunta is beaten.</summary>
    public static bool CarLocked(string car, Progress p) =>
        car == SecretCar && !p.Beaten("AKINA/bunta") && !p.IsCleared(Story.StoryMode.Key(Story.StoryText.Chapters.Length - 1));

    /// <summary>
    ///     Course to load (COURSE.AFS name) for <paramref name="e"/> on a course with <paramref name="times"/>: wet → the _RIN
    ///     course (the disc has rain only by day, so wet beats night), else _NIT or _DAY. A rematch against a beaten rival is
    ///     wet: the original copies the rival's win counter into the weather byte (0x170A00, FORMATS.md).
    /// </summary>
    public static (string CourseTime, bool Wet) Conditions(Entry e, IReadOnlyCollection<string> times, Progress p)
    {
        var wet = (e.Wet || p.Beaten(e.Key)) && times.Contains("RIN");
        return ($"{e.CourseId}_{(wet ? "RIN" : e.Night && times.Contains("NIT") ? "NIT" : "DAY")}", wet);
    }

    /// <summary>The battle: the original's side-by-side race (first to the goal; the remake's breakaway win at 8 s).</summary>
    public static BattleSetup Setup(Entry e) => new(e.Rival, BattleRule.Race);

    /// <summary>Difficulty stars 1–5 from the AI's skill: a Legend rival's within the ladder, any other's (VS CPU) on the whole scale.</summary>
    public static int Stars(Entry e) => Math.Clamp((int)MathF.Round((e.Rival.Style.Skill - 0.78f) / 0.055f) + 1, 1, 5); // within the ladder's 0.8–1

    public static int Stars(float skill) => Math.Clamp((int)MathF.Round(skill / 0.22f) + 1, 1, 5);
}
