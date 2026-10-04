using Kansei.Physics;

namespace Touge.Race;

/// <summary>
///     Rival roster: the anime characters whose cars the original sets up with their livery (CarParts characters, ELF
///     0x2A2EA0/0x2A3770), with our own driving styles after their portrayal — skill (pace), aggression (passing,
///     blocking, following distance), drift (handbrake flicks, slip held). Names are plain English text.
/// </summary>
public static class Rivals
{
    /// <param name="Id">Short CLI id (--battle keisuke).</param>
    /// <param name="Car">HCAR name (<see cref="Touge.Formats.CarPaint.Cars"/>), driven with the character's livery.</param>
    /// <param name="Team">Team/subtitle for the telop and result sheet.</param>
    public sealed record Rival(string Id, string Name, string Team, string Car, RivalStyle Style);

    public static readonly Rival[] All =
    [
        new("itsuki", "ITSUKI TAKEUCHI", "AKINA SPEEDSTARS", "AE85", new(0.15f, 0.2f, 0.3f)),
        new("iketani", "KOICHIRO IKETANI", "AKINA SPEEDSTARS", "S13", new(0.35f, 0.3f, 0.3f)),
        new("kenji", "KENJI", "AKINA SPEEDSTARS", "ONE80", new(0.4f, 0.35f, 0.5f)),
        new("takeshi", "TAKESHI NAKAZATO", "MYOGI NIGHTKIDS", "R32", new(0.65f, 0.55f, 0.1f)),
        new("shingo", "SHINGO SHOJI", "MYOGI NIGHTKIDS", "EG6", new(0.6f, 0.95f, 0.2f)),
        new("mako", "MAKO SATO", "IMPACT BLUE", "SIL80", new(0.7f, 0.5f, 0.6f)),
        new("kai", "KAI KOGASHIWA", "IROHAZAKA", "MR2", new(0.72f, 0.7f, 0.3f)),
        new("seiji", "SEIJI IWAKI", "TEAM EMPEROR", "EVO4", new(0.72f, 0.65f, 0.1f)),
        new("kyoichi", "KYOICHI SUDO", "TEAM EMPEROR", "EVO3", new(0.85f, 0.6f, 0.15f)),
        new("keisuke", "KEISUKE TAKAHASHI", "AKAGI REDSUNS", "FD3S", new(0.82f, 0.65f, 0.8f)),
        new("ryosuke", "RYOSUKE TAKAHASHI", "AKAGI REDSUNS", "FC3S", new(0.9f, 0.45f, 0.6f)),
        new("wataru", "WATARU AKIYAMA", "SAITAMA", "AE86L", new(0.8f, 0.55f, 0.7f)),
        new("takumi", "TAKUMI FUJIWARA", "PROJECT D", "AE86T", new(0.95f, 0.5f, 0.9f)),
        new("bunta", "BUNTA FUJIWARA", "FUJIWARA TOFU", "IMP3", new(1f, 0.4f, 0.7f)),
    ];

    /// <summary>A rival by id, name part or car (case-insensitive); a car nobody drives gets a nameless driver of medium skill.</summary>
    public static Rival Find(string key)
    {
        var k = key.Trim();
        return All.FirstOrDefault(r => r.Id.Equals(k, StringComparison.OrdinalIgnoreCase))
               ?? All.FirstOrDefault(r => r.Car.Equals(k, StringComparison.OrdinalIgnoreCase))
               ?? All.FirstOrDefault(r => r.Name.Contains(k, StringComparison.OrdinalIgnoreCase))
               ?? (Array.FindIndex(Touge.Formats.CarPaint.Cars, c => c.Equals(k, StringComparison.OrdinalIgnoreCase)) is >= 0 and var i
                   ? new Rival(k.ToLowerInvariant(), "STREET RACER", "LOCAL", Touge.Formats.CarPaint.Cars[i], new(0.6f, 0.5f, 0.4f))
                   : throw new ArgumentException($"--battle {key}: unbekannt, möglich: {string.Join(' ', All.Select(r => r.Id))} oder ein Auto"));
    }
}
