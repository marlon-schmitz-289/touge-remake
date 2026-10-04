using Kansei.Graphics;

namespace Touge.Ui;

/// <summary>
///     "Quit the game?" YES/NO (NO selected first) over the main menu and the pause menu, in the menus' chrome style with
///     the original's sounds (SYS005 move, SYS006 decide, BEEP001 back). Desktop builds only: a console build has no quit.
/// </summary>
public sealed class QuitPrompt
{
    public static bool Available => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    public bool Open { get; private set; }
    private bool _yes;

    public void Show() => (Open, _yes) = (true, false);

    /// <summary>Left/right choose, decide closes it; back is NO. True once YES was decided.</summary>
    public bool Update((int X, int Y, bool Ok, bool Back) k, Action<string>? sound)
    {
        if (k.X != 0)
        {
            if (_yes != k.X < 0) sound?.Invoke("SYS005");
            _yes = k.X < 0;
        }
        else if (k.Back)
        {
            sound?.Invoke("BEEP001");
            Open = false;
        }
        else if (k.Ok)
        {
            sound?.Invoke("SYS006");
            Open = false;
            return _yes;
        }
        return false;
    }

    /// <summary>Dimmed screen, carbon panel with the question, two chrome plates, the pulsing frame on the choice.</summary>
    public void Draw(Canvas c, float theta)
    {
        if (!Open) return;
        c.Fill(Overlay.Rgba(0, 0, 0, 0.55f));
        c.O.FadeText(0.12f); // text drawn before lies above every shape: nearly hide it
        c.Carbon(136, 296, 376, 406);
        c.Lettering("QUIT THE GAME?", 256, 342, 24, Canvas.White, Overlay.Rgba(0.72f, 0.73f, 0.75f));
        string[] labels = ["YES", "NO"];
        for (var i = 0; i < 2; i++)
        {
            var x = 166 + i * 100;
            c.Plate(x, 360, 80, 26, 1);
            c.Text(labels[i], x + 40, 379, 14, Canvas.Shade(0.08f, 0.08f, 0.08f, 1), 0.5f, 0.18f);
        }
        var sx = _yes ? 166 : 266;
        c.Glow(sx - 4, 356, sx + 84, 390, Canvas.Pulse(theta));
    }
}
