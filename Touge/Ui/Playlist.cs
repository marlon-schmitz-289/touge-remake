using System.Numerics;
using Kansei.Graphics;

namespace Touge.Ui;

/// <summary>
///     Options → PLAYLIST: every race song (<see cref="Jukebox.Songs"/>) with an ON/OFF plate in the Options style, first row
///     ALL SONGS; switched-off songs go to <see cref="Settings.MusicOff"/> (all off = silence in races).
/// </summary>
public sealed class Playlist(Settings settings)
{
    private const int Visible = 9;
    private int _song, _top;

    /// <summary>The OPTIONS page with this screen.</summary>
    public static Options.Page Page(Settings s)
    {
        var p = new Playlist(s);
        return new Options.Page("PLAYLIST", "Race songs on or off; they play shuffled.") { Input = p.Update, Draw = p.Draw };
    }

    private static float RowY(int i) => 66 + i * 28;

    private int SongsOn => Jukebox.Songs.Count(s => !settings.MusicOff.Contains(s.File));

    private Options.Result Update((int X, int Y, bool Ok, bool Back) k, Action<string>? sound)
    {
        var rows = Jukebox.Songs.Length + 1;
        if (k.Back) return Options.Result.Leave;
        if (k.Y != 0)
        {
            _song = ((_song + k.Y) % rows + rows) % rows;
            _top = Math.Clamp(_top, _song - Visible + 1, _song);
            sound?.Invoke("SYS005");
        }
        else if (k.X != 0 || k.Ok)
        {
            var off = settings.MusicOff;
            // LEFT = ON plate, RIGHT = OFF plate, CONFIRM toggles
            var files = _song == 0 ? Jukebox.Songs.Select(s => s.File).ToArray() : [Jukebox.Songs[_song - 1].File];
            var turnOff = k.X != 0 ? k.X > 0 : _song == 0 ? off.Count == 0 : !off.Contains(files[0]);
            if (turnOff) off.UnionWith(files);
            else off.ExceptWith(files);
            sound?.Invoke("SYS005");
            return Options.Result.Changed;
        }
        return Options.Result.None;
    }

    private static readonly uint Grey = Overlay.Rgba(0.72f, 0.73f, 0.75f);

    private void Draw(Canvas c, float theta)
    {
        var off = settings.MusicOff;
        for (var j = 0; j < Visible; j++)
        {
            var i = _top + j;
            if (i > Jukebox.Songs.Length) break;
            var y = RowY(j);
            var all = i == 0;
            var song = all ? null : Jukebox.Songs[i - 1];
            var on = all ? off.Count < Jukebox.Songs.Length : !off.Contains(song!.File);
            // dark-steel tab: number, title, artist
            Vector2 min = Vector2.Round(c.P(36, y)), max = Vector2.Round(c.P(380, y + 26));
            c.O.Rect(min, max, Overlay.Rgba(0.5f, 0.51f, 0.53f));
            c.O.RectGradient(min + new Vector2(1.5f, 1.5f) * c.S, max - new Vector2(1.5f, 1.5f) * c.S, Overlay.Rgba(0.24f, 0.25f, 0.26f), Overlay.Rgba(0.1f, 0.1f, 0.11f));
            var ink = on ? Canvas.White : Overlay.Rgba(1, 1, 1, 0.4f);
            if (all) c.Text("ALL SONGS", 50, y + 18, 13, ink, 0, 0.1f);
            else
            {
                c.Text($"{i:00}", 50, y + 18, 11, Overlay.Rgba(1, 0.72f, 0.1f, on ? 1 : 0.4f), 0, 0.1f);
                var tw = c.Text(song!.Title, 72, y + 18, 13, ink, 0, 0.1f);
                c.Fit(song.Artist, 370, y + 18, Math.Clamp(290 - tw, 40, 140), 1, on ? Grey : Overlay.Rgba(0.72f, 0.73f, 0.75f, 0.4f), 0, 0, 10);
            }
            // chrome plate ON / OFF, the active one lit
            c.Plate(388, y, 92, 26, 1);
            var mixed = all && off.Count > 0 && on;
            for (var v = 0; v < 2; v++)
            {
                var lit = !mixed && v == 0 == on;
                c.Text(v == 0 ? "ON" : "OFF", 411 + v * 46, y + 18, 13, lit ? Overlay.Rgba(0.05f, 0.3f, 0.1f) : Overlay.Rgba(0.35f, 0.42f, 0.38f, 0.55f), 0.5f, 0, 0, lit ? 0.6f : 0);
            }
        }
        // scroll bar
        var rows = Jukebox.Songs.Length + 1;
        float top = RowY(0), bottom = RowY(Visible - 1) + 26;
        c.O.Rect(Vector2.Round(c.P(486, top)), Vector2.Round(c.P(490, bottom)), Overlay.Rgba(0, 0, 0, 0.35f));
        float t0 = top + (bottom - top) * _top / rows, t1 = top + (bottom - top) * (_top + Visible) / rows;
        c.O.Rect(Vector2.Round(c.P(486, t0)), Vector2.Round(c.P(490, t1)), Overlay.Rgba(1, 0.85f, 0.2f, 0.9f));
        var gy = RowY(_song - _top);
        c.Glow(32, gy - 4, 484, gy + 30, Canvas.Pulse(theta));
        c.Carbon(36, 324, 480, 426, 1, false);
        var count = SongsOn;
        c.Text(count == 0 ? "ALL SONGS OFF: NO MUSIC IN THE RACE." : $"{count} OF {Jukebox.Songs.Length} SONGS ON", 50, 348, 13, count == 0 ? Overlay.Rgba(1, 0.3f, 0.2f) : Canvas.White, 0, 0.12f, 0, 0.4f);
        c.Text("In the race every song plays once in random order, then a new round.", 50, 372, 11.5f, Canvas.White, 0, 0.12f);
        c.Text("M (pad: D-pad right) skips to the next song.", 50, 392, 11.5f, Canvas.White, 0, 0.12f);
        Menu.Hint(c, "UP/DOWN: Select    LEFT/RIGHT: On/Off    BACK: Options");
    }
}
