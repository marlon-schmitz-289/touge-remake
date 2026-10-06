using System.Numerics;
using Kansei.Graphics;
using Touge.Formats;

namespace Touge.Ui;

/// <summary>
///     The original's loading screen (FORMATS.md „Ladebilder“): one of the manga pictures <c>LOAD00–28.PAC</c> in
///     <c>TEXTURE.AFS</c> at random, full screen with its own red "Now Loading...", drawn under the overlay
///     (<see cref="SpriteRenderer"/>); the 4:3 picture's edge columns stretch over the side bars. A new picture each time a
///     loading screen starts (its clock went back). Without the picture (no ISO entry, no renderer): white and the text.
/// </summary>
public static class LoadingArt
{
    public static string? IsoPath;
    public static SpriteRenderer? Sprites;

    private static int _id;
    private static float _lastT = float.MaxValue;

    /// <summary>The loading screen at <paramref name="t"/> s since it came up.</summary>
    public static void Draw(Canvas c, float t)
    {
        if (t < _lastT) Pick();
        _lastT = t;
        if (_id == 0 || Sprites == null)
        {
            c.Fill(Canvas.White);
            c.Text("Now Loading...", 476, 428, 15, Overlay.Rgba(0.92f, 0.08f, 0.06f), 1, 0.22f, 0, 0.4f);
            return;
        }
        // the picture is the top 512×448 of the 512×512 texture
        const float v = 448f / 512, u = 0.5f / 512;
        Vector2 min = c.P(0, 0), max = c.P(512, 448);
        Sprites.Add(_id, new Vector2(0, min.Y), new Vector2(min.X, max.Y), Vector2.Zero, new Vector2(u, v));
        Sprites.Add(_id, new Vector2(max.X, min.Y), new Vector2(c.Width, max.Y), new Vector2(1 - u, 0), new Vector2(1, v));
        Sprites.Add(_id, min, max, Vector2.Zero, new Vector2(1, v));
    }

    private static void Pick()
    {
        if (Sprites == null || IsoPath == null) return;
        Sprites.Free([_id]);
        _id = 0;
        try
        {
            using var iso = new Iso9660(IsoPath);
            var afs = iso.OpenAfs("CDVD/DATA/MODEL/TEXTURE.AFS");
            // the game: rand · 29 (sub_1669A0); LOAD29 only before the race of figure 0x18
            if (afs.Find($"LOAD{Random.Shared.Next(29):00}.PAC") is not { } e) return;
            var pac = afs.Read(e);
            var gim = Pac.Entries(pac).First(p => p.Type == 1);
            var (w, h, rgba) = Gim.Decode(pac.AsSpan(gim.Offset, gim.Size));
            _id = Sprites.Upload(w, h, rgba, "loading");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            Console.WriteLine($"[Loading] Ladebild nicht lesbar: {ex.Message}");
        }
    }
}
