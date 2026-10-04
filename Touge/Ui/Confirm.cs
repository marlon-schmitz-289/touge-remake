using Kansei.Graphics;

namespace Touge.Ui;

/// <summary>A YES/NO question over a screen (NO first), drawn and voiced like <see cref="QuitPrompt"/>.</summary>
public sealed class Confirm
{
    public bool Open { get; private set; }
    public string Question { get; private set; } = "";
    public string? Detail { get; private set; }
    private bool _yes;

    public void Show(string question, string? detail = null) => (Open, Question, Detail, _yes) = (true, question, detail, false);

    /// <summary>Left/right choose, decide closes it, back is NO. True once YES was decided.</summary>
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

    public void Draw(Canvas c, float theta)
    {
        if (!Open) return;
        c.Fill(Overlay.Rgba(0, 0, 0, 0.55f));
        c.O.FadeText(0.12f);
        c.Carbon(116, 280, 396, 406);
        c.Lettering(Question, 256, 322, Math.Min(22, 250 * c.Kx / c.O.Font!.Measure(Question, c.Ky)), Canvas.White, Overlay.Rgba(0.72f, 0.73f, 0.75f));
        if (Detail != null) c.Text(Detail, 256, 342, 11, Overlay.Rgba(1, 1, 1, 0.8f), 0.5f, 0.12f);
        string[] labels = ["YES", "NO"];
        for (var i = 0; i < 2; i++)
        {
            var x = 166 + i * 100;
            c.Plate(x, 362, 80, 26, 1);
            c.Text(labels[i], x + 40, 381, 14, Canvas.Shade(0.08f, 0.08f, 0.08f, 1), 0.5f, 0.18f);
        }
        var sx = _yes ? 166 : 266;
        c.Glow(sx - 4, 358, sx + 84, 392, Canvas.Pulse(theta));
    }
}
