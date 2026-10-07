namespace Touge.Story;

/// <summary>
///     The remake's English for the story mode: chapter titles, who drives against whom, a short blurb, and the scenes. The
///     scenes of chapters 2–30 translate the original's lines (MANGA/MG_OBJ.AFS STRnn.BIN, read at runtime by
///     <see cref="Formats.StoryScript.ParseScript"/>) one utterance for one, in the original's order and split into the same parts
///     (before the battle, after it; chapter 30 has an epilogue). The disc names a speaker only now and then (F_…); the
///     English names every one. Chapters 0 and 1 have no script on the disc: their short scenes are the remake's own.
///     Each line is "SPEAKER|text"; text in parentheses is a thought. The scenes are not in the repo (the original's
///     script): <c>StoryText.Local.cs</c> next to this file (gitignored) fills them in, without it every chapter shows a placeholder.
/// </summary>
public static partial class StoryText
{
    /// <param name="Hero">Who the player drives as (the original's setup record for the chapter).</param>
    /// <param name="Rival">Rival id (<see cref="StoryRivals"/>), null for a run alone.</param>
    public sealed record Chapter(string Title, string Hero, string? Rival, string Blurb, string[][] Scene);

    /// <summary>The three parts of the original's chapter select (ELF 0x2A2990: part, number in part).</summary>
    public static readonly (string Title, string Subtitle, int First)[] Parts =
    [
        ("PART 1", "THE LEGEND OF AKINA", 0),
        ("PART 2", "THE TAKAHASHI BROTHERS", 19),
        ("PART 3", "PROJECT D", 24),
    ];

    /// <summary>Speaker names of the script (F_…) in English, for checking that the translation lines up with the disc.</summary>
    public static readonly IReadOnlyDictionary<string, string> Speakers = new Dictionary<string, string>
    {
        ["拓海"] = "TAKUMI", ["文太"] = "BUNTA", ["池谷"] = "IKETANI", ["イツキ"] = "ITSUKI", ["健二"] = "KENJI", ["啓介"] = "KEISUKE",
        ["涼介"] = "RYOSUKE", ["史浩"] = "FUMIHIRO", ["中里"] = "NAKAZATO", ["慎吾"] = "SHINGO", ["沙雪"] = "SAYUKI", ["真子"] = "MAKO",
        ["京一"] = "KYOICHI", ["なつき"] = "NATSUKI", ["二人"] = "IKETANI & ITSUKI", ["走り屋"] = "RED SUNS MEMBER", ["走り屋１"] = "GALLERY 1",
        ["走り屋２"] = "GALLERY 2", ["走り屋３"] = "GALLERY 3", ["ＯＢ１"] = "TODO OB 1", ["ＯＢ２"] = "TODO OB 2", ["ＯＢ３"] = "TODO OB 3", ["社長"] = "PRESIDENT",
    };

    /// <summary>The scenes by chapter, from the local <c>StoryText.Local.cs</c> (empty without it).</summary>
    private static readonly Dictionary<int, string[][]> Translated = Local();

    private static Dictionary<int, string[][]> Local()
    {
        var scenes = new Dictionary<int, string[][]>();
        LocalScenes(scenes);
        return scenes;
    }

    static partial void LocalScenes(Dictionary<int, string[][]> scenes);

    /// <summary>The translated scene of <paramref name="chapter"/>, or one placeholder line per part (<paramref name="parts"/> as on the disc).</summary>
    private static string[][] Scene(int chapter, int parts) =>
        Translated.GetValueOrDefault(chapter) ?? [.. Enumerable.Range(0, parts).Select(_ => new[] { "STORY|(Translation not installed.)" })];

    public const string Takumi = "TAKUMI FUJIWARA", Keisuke = "KEISUKE TAKAHASHI", Ryosuke = "RYOSUKE TAKAHASHI";

    public static readonly Chapter[] Chapters =
    [
        new("THE TOFU DELIVERY", Takumi, null, "Four in the morning. The tofu has to reach the hotel at Lake Akina - up the mountain, the same as every day.", Scene(0, 2)),
        new("A CUP OF WATER", Takumi, null, "Bunta puts a paper cup of water in the holder. Down the mountain without spilling a drop - and before his cigarette burns out.", Scene(1, 2)),
        new("THE GHOST OF AKINA", Takumi, "keisuke", "Keisuke of the Red Suns owns Akina tonight. Then a pair of headlights closes in from behind - an old Hachi-Roku.", Scene(2, 2)),
        new("DOWNHILL SPECIALIST", Takumi, "keisuke", "The Red Suns challenge the Speed Stars to a downhill battle. Iketani is counting on Bunta - but Bunta sends his son.", Scene(3, 2)),
        new("ROLLER COASTER", Takumi, null, "Iketani and Itsuki beg for a ride down Akina. They want to see the downhill specialist flat out.", Scene(4, 2)),
        new("THE PROMISE AT TEN", Takumi, null, "Nakazato is waiting at the summit at ten. Takumi has the car - and almost no time left to get up there.", Scene(5, 2)),
        new("GT-R VS HACHI-ROKU", Takumi, "takeshi", "Takeshi Nakazato of the Myogi NightKids and his R32 GT-R. Four-wheel drive against an old FR on the Akina downhill.", Scene(6, 2)),
        new("GUM TAPE DEATH MATCH", Takumi, "shingo", "Shingo Shoji of the NightKids wants revenge for Nakazato - with his right hand taped to the steering wheel. And Takumi's too.", Scene(7, 2)),
        new("THE WHITE COMET", Takumi, "ryosuke", "Ryosuke Takahashi, Akagi's White Comet, has been waiting for this battle. A fun night, he says.", Scene(8, 2)),
        new("IMPACT BLUE", Takumi, "mako", "On Usui, Mako and Sayuki of Impact Blue rule the night in their Sileighty. They let Takumi choose the position.", Scene(9, 2)),
        new("RAIN ON MYOGI", Takumi, "kenta", "The Red Suns visit Myogi in the rain. Kenta Nakamura wants the 86 for himself - on a wet downhill.", Scene(10, 2)),
        new("THE EMPEROR ARRIVES", Takumi, "seiji", "Team Emperor from Tochigi is sweeping Gunma in their Lancer Evolutions. Seiji Iwaki comes for the 86 on Akina.", Scene(11, 2)),
        new("CHASING THE S13", Takumi + " (180SX)", "couple", "A night drive with Natsuki in Tsukamoto's 180SX. The S13 couple from earlier blows past - and Natsuki wants them caught.", Scene(12, 2)),
        new("THE SEMINAR", Takumi, "kyoichi", "Kyoichi Sudo, leader of the Emperor, takes the 86 on at Akagi himself. It is not a battle, he says. It is a seminar.", Scene(13, 2)),
        new("THE SEALED ENGINE", Takumi, null, "The 86 has a new engine, and it feels wrong. Wataru Akiyama of Saitama rides along to find out why.", Scene(14, 2)),
        new("DEATH MATCH AT SHOMARU", Takumi, "wataru", "Wataru's Levin turbo against the new 86 on the narrow Shomaru pass. A death match with no time limit.", Scene(15, 2)),
        new("REMATCH AT IROHAZAKA", Takumi, "kyoichi", "Kyoichi on his home downhill, Irohazaka. Takumi leads: reach the goal without being passed.", Scene(16, 2)),
        new("CARPET OF LEAVES", Takumi, "kai", "Kai Kogashiwa and his MR2 on Irohazaka in autumn. Bunta's only advice: use the step at the edge of the asphalt.", Scene(17, 2)),
        new("A DREAM", Takumi, null, "Spring. Takumi has made up his mind about his future, and wants Natsuki to see how he really drives.", Scene(18, 2)),
        new("BLACK R32", Keisuke, "takeshi", "The week before Ryosuke's battle, Keisuke can't settle. On the Akina uphill a black GT-R falls in behind him.", Scene(19, 2)),
        new("MYOGI'S DEEP VALLEYS", Keisuke, "takeshi", "The Red Suns' expedition to Myogi. Nakazato against Keisuke on the uphill, with rain in the air.", Scene(20, 2)),
        new("KING OF THE TOUGE", Keisuke, "seiji", "The Emperor comes to Akagi after their loss on Akina. Seiji Iwaki's Evo IV against Keisuke on the uphill.", Scene(21, 2)),
        new("CHARISMA OF THE STREETS", Ryosuke, "kyoichi", "The real match of the exchange: Ryosuke's FC against Kyoichi's Evo III on the Akagi downhill.", Scene(22, 2)),
        new("THE LEVIN AMBUSH", Keisuke, "wataru", "Wataru Akiyama waits for Keisuke's FD on Akagi. An ambush on the Red Suns' home downhill.", Scene(23, 2)),
        new("SEVEN STAR LEAF", Takumi, "toru", "Project D's first expedition: the Momiji Line in Tochigi. Toru Suetsugu's Roadster against the 86 on the downhill.", Scene(24, 2)),
        new("FOUR HUNDRED HORSEPOWER", Keisuke, "atsuro", "Atsuro Kawai of Seven Star Leaf promises a flat-out hillclimb. Keisuke's FD on the Momiji Line uphill.", Scene(25, 2)),
        new("THE TODO SCHOOL", Takumi, "daiki", "Tochigi's Todo School: Daiki Ninomiya's Civic Type R. Ryosuke's orders - lead the first run and don't look in the mirror.", Scene(26, 2)),
        new("CLEVER STRATEGY", Takumi, "daiki", "The second run on Shiona. Ryosuke's simulation hinges on Takumi's sense - and on the 86 finally revving out.", Scene(27, 2)),
        new("TURBO INTEGRA", Keisuke, "sakai", "Hiroya Sakai's Integra Type R - with a turbo bolted on. The Todo School wants to settle it on the first run up Shiona.", Scene(28, 2)),
        new("THE PROFESSIONAL", Takumi, "tomo", "Tomoyuki Tachi, a professional racer, in the Todo Shokai demo car at Happogahara. Project D sends the 86.", Scene(29, 2)),
        new("THE IMPREZA", Takumi, "bunta", "The Akina downhill late at night. A car Takumi doesn't know sits on his bumper - and it is impossibly fast.", Scene(30, 3)),
    ];

    /// <summary>Speaker and text of a line ("SPEAKER|text").</summary>
    public static (string Who, string Text) Split(string line)
    {
        var bar = line.IndexOf('|');
        return (line[..bar], line[(bar + 1)..]);
    }
}
