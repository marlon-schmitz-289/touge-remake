using System.Numerics;
using Kansei.Graphics;

namespace Touge.Ui;

/// <summary>
///     The car select in two steps, shared by every mode that picks a car (<see cref="Menu"/>: Time Attack, Legend, Four
///     Passes; <see cref="Versus"/>: VS CPU, split-screen and online lobbies): MAKER — the seven chrome plates (T_MKSEL order),
///     the highlighted maker's lineup on the carbon panel (count, chassis codes, locked cars) — then CAR — that maker's models
///     in a list beside the turning 3D car, its specs and body colours below. UP/DOWN picks, LEFT/RIGHT the colour, DECIDE goes
///     on, BACK goes from the cars to the makers and from the makers to whatever opened it. Locked cars (<see cref="CarLocked"/>)
///     show as ????? and beep. The picker keeps the last maker and car: it opens on them.
/// </summary>
public sealed class CarPicker(Catalog catalog)
{
    public enum Step { Maker, Car }

    /// <summary>Moved: car or colour changed (preview it); MakerChosen: on to the cars; Decide: the car is chosen; Back: leave the step.</summary>
    public enum Result { None, Moved, MakerChosen, Decide, Back }

    public Step Current { get; set; }
    public int Maker { get; private set; }
    public int Car { get; private set; }
    public int Paint { get; private set; }
    /// <summary>Cars not won yet (Legend's secret car): ?????, not selectable.</summary>
    public Func<string, bool>? CarLocked { get; set; }

    public bool Locked(int car) => CarLocked?.Invoke(catalog.Cars[car].Id) == true;
    public int[] Cars(int maker) => [.. Enumerable.Range(0, catalog.Cars.Count).Where(i => catalog.Cars[i].Maker == Catalog.Makers[maker])];
    /// <summary>The highlighted car is locked (the game keeps showing the last open one, the screen covers it).</summary>
    public bool OnLocked => Current == Step.Car && Locked(Car);

    /// <summary>Opens on <paramref name="car"/> (a locked one falls back to the first car) and its maker.</summary>
    public void Open(int car, int paint, Step step = Step.Maker)
    {
        if (car < 0 || car >= catalog.Cars.Count || Locked(car)) (car, paint) = (0, 0);
        (Car, Paint, Current) = (car, Math.Clamp(paint, 0, catalog.Cars[car].Paints.Length - 1), step);
        Maker = Math.Max(0, Array.IndexOf(Catalog.Makers, catalog.Cars[car].Maker));
    }

    /// <summary>Highlights <paramref name="car"/> of the current maker (screenshots).</summary>
    public void Pick(int car) => (Car, Paint) = (car, 0);

    private static int Wrap(int i, int n) => (i % n + n) % n;

    public Result Update((int X, int Y, bool Ok, bool Back) k, Action<string>? sound)
    {
        if (Current == Step.Maker)
        {
            if (k.Y != 0)
            {
                Maker = Wrap(Maker + k.Y, Catalog.Makers.Length);
                sound?.Invoke("SYS005");
            }
            else if (k.Ok && Cars(Maker).Length == 0) sound?.Invoke("BEEP001"); // a catalog without the maker's cars (tests)
            else if (k.Ok)
            {
                sound?.Invoke("SYS006");
                var cars = Cars(Maker);
                // the maker's car shown last stays, another maker opens on its first open car
                if (Array.IndexOf(cars, Car) < 0) (Car, Paint) = (cars.FirstOrDefault(i => !Locked(i), cars[0]), 0);
                Current = Step.Car;
                return Result.MakerChosen;
            }
            else if (k.Back) return Result.Back;
            return Result.None;
        }
        if (k.Y != 0)
        {
            var cars = Cars(Maker);
            (Car, Paint) = (cars[Wrap(Array.IndexOf(cars, Car) + k.Y, cars.Length)], 0);
            sound?.Invoke("SYS005");
            return Result.Moved;
        }
        if (k.X != 0 && !Locked(Car))
        {
            Paint = Wrap(Paint + k.X, catalog.Cars[Car].Paints.Length);
            sound?.Invoke("SYS005");
            return Result.Moved;
        }
        if (k.Ok && Locked(Car)) sound?.Invoke("BEEP001");
        else if (k.Ok)
        {
            sound?.Invoke("SYS006");
            return Result.Decide;
        }
        else if (k.Back) return Result.Back;
        return Result.None;
    }

    // ---------------------------------------------------------------- drawing

    private static readonly uint Grey = Overlay.Rgba(0.72f, 0.73f, 0.75f), Dim = Overlay.Rgba(1, 1, 1, 0.35f), HintRed = Overlay.Rgba(1, 0.2f, 0.15f);

    /// <summary>Model of a catalog name without its chassis code ("TRUENO GT-APEX [AE86]" → TRUENO GT-APEX): the lineup tells same-chassis cars apart (same model: the full name).</summary>
    internal static string Model(string name) => name.LastIndexOf('[') is var i and > 0 ? name[..i].TrimEnd() : name;

    /// <summary>MAKER: the plates on the left, the lineup of the highlighted one on the right; <paramref name="back"/> names BACK's target.</summary>
    public void DrawMaker(Canvas c, float theta, string back)
    {
        for (var i = 0; i < Catalog.Makers.Length; i++)
        {
            float x = 24, y = 70 + i * 52;
            var sel = i == Maker;
            c.Plate(x, y, 220, 40, sel ? 1 : 0.62f);
            // white name field with the maker in heavy dark letters (plain text, no brand logos), the car count on the plate's end
            Vector2 min = Vector2.Round(c.P(x + 14, y + 7)), max = Vector2.Round(c.P(x + 176, y + 33));
            c.O.Rect(min, max, Canvas.Shade(0.96f, 0.96f, 0.97f, sel ? 1 : 0.7f));
            c.Fit(Catalog.Makers[i], x + 95, y + 28, 140, 0.5f, Canvas.Shade(0.12f, 0.12f, 0.14f, 1), 0.08f, 0, 20);
            c.Text(Cars(i).Length.ToString(), x + 198, y + 28, 18, Canvas.Shade(0.1f, 0.1f, 0.1f, sel ? 1 : 0.7f), 0.5f, 0.15f, 0, 0.3f);
        }
        c.Glow(18, 64 + Maker * 52, 250, 116 + Maker * 52, Canvas.Pulse(theta));
        // lineup panel with its tab: maker, count, the chassis codes (locked: ?????)
        var cars = Cars(Maker);
        var locked = cars.Count(Locked);
        c.Carbon(262, 150, 496, 382);
        c.Plate(272, 132, 90, 24, 1);
        c.Text("LINEUP", 317, 149, 13, Canvas.Shade(0.1f, 0.1f, 0.1f, 1), 0.5f, 0.15f);
        var name = Catalog.Makers[Maker];
        c.Lettering(name, 379, 212, MathF.Min(36, 200 * c.Kx / c.O.Font!.Measure(name, c.Ky)), Overlay.Rgba(0.35f, 0.45f, 1), Canvas.BrushBlue, 0.5f, 0.12f, true);
        c.Text($"{cars.Length} {(cars.Length == 1 ? "CAR" : "CARS")}", 379, 238, 13, Canvas.White, 0.5f, 0.15f, 0.06f);
        for (var i = 0; i < cars.Length; i++)
        {
            float x = 274 + i % 2 * 112, y = 264 + i / 2 * 19;
            var model = Model(catalog.Cars[cars[i]].Name);
            if (cars.Count(j => Model(catalog.Cars[j].Name) == model) > 1) model = catalog.Cars[cars[i]].Name; // R32/R34: same model, the code tells them apart
            c.Fit(Locked(cars[i]) ? "?????" : model, x, y, 104, 0, Locked(cars[i]) ? Dim : Grey, 0.12f, 0.05f, 11);
        }
        if (locked > 0) c.Text($"{locked} LOCKED", 379, 368, 11, HintRed, 0.5f, 0.12f);
        Menu.Hint(c, $"UP/DOWN: Select maker    DECIDE: Cars    BACK: {back}");
    }

    /// <summary>
    ///     CAR, drawn over the turning 3D car: the maker's models on the left, the highlighted car's specs and colours below;
    ///     <paramref name="a"/> the entrance, <paramref name="active"/> off under the transmission choice (no list, no hint).
    /// </summary>
    public void DrawCar(Canvas c, float theta, float a, bool active, string back = "Makers")
    {
        var cars = Cars(Maker);
        var locked = Locked(Car);
        if (locked && active) c.Fill(Overlay.Rgba(0, 0, 0, 0.82f)); // the last open car still turns behind: hide it
        var slide = (1 - a) * 60;
        // model list (not under the transmission choice: it would run into the AT/MT panel)
        if (active) DrawList(c, theta, a, cars, slide);
        var row = Array.IndexOf(cars, Car);
        // specs and colours
        var car = catalog.Cars[Car];
        c.Carbon(16, 318 + slide, 496, 424 + slide, a);
        c.Text($"{Catalog.Makers[Maker]}   {row + 1} / {cars.Length}", 32, 340 + slide, 11, Grey, 0, 0.12f);
        c.Fit(locked ? "?????" : car.Name, 32, 366 + slide, 300, 0, Canvas.White, 0.15f, 0.07f, 24);
        c.Text(locked ? "Not won yet" : $"{car.Ps} PS   {car.Kg} kg", 32, 390 + slide, 13, Canvas.White, 0, 0.15f, 0.06f);
        c.Text("DRIVE", 350, 340 + slide, 10, Grey, 0, 0.1f);
        string[] drives = ["FF", "MR", "FR", "4WD"];
        for (var i = 0; i < drives.Length; i++)
        {
            float x = 350 + i * 34, y = 346 + slide;
            var on = !locked && drives[i] == car.Drive;
            c.O.Rect(Vector2.Round(c.P(x, y)), Vector2.Round(c.P(x + 30, y + 18)), on ? Overlay.Rgba(0.8f, 0.07f, 0.06f) : Overlay.Rgba(0.18f, 0.18f, 0.19f));
            c.Text(drives[i], x + 15, y + 14, 12, on ? Canvas.White : Dim, 0.5f, 0.15f);
        }
        c.Text("BODY COLOUR", 350, 384 + slide, 10, Grey, 0, 0.1f);
        if (!locked)
            for (var i = 0; i < car.Paints.Length; i++)
            {
                var at = c.P(358 + i * 22, 404 + slide);
                if (i == Paint) c.Diamond(358 + i * 22, 391 + slide, 4);
                c.O.Disc(at, 8 * c.S, Overlay.Rgba(0, 0, 0, 0.9f));
                c.O.Disc(at, 6.5f * c.S, Catalog.Swatch(car.Paints[i]));
            }
        if (!active) return;
        if (locked) c.Lettering("?????", 300, 220, 54, Overlay.Rgba(1, 0.55f, 0.3f), Canvas.WordRed, 0.5f, 0.15f);
        Menu.Hint(c, locked ? Race.Legend.SecretCarHint : $"UP/DOWN: Car    LEFT/RIGHT: Body colour    DECIDE: OK    BACK: {back}");
    }

    private void DrawList(Canvas c, float theta, float a, int[] cars, float slide)
    {
        c.Carbon(16 - slide, 72, 176 - slide, 104 + cars.Length * 22, a);
        c.Fit(Catalog.Makers[Maker], 28 - slide, 90, 136, 0, Grey, 0.12f, 0, 11);
        for (var i = 0; i < cars.Length; i++)
        {
            var y = 114 + i * 22;
            var sel = cars[i] == Car;
            if (sel) c.Diamond(30 - slide, y - 4, 5, a);
            c.Fit(Locked(cars[i]) ? "?????" : catalog.Cars[cars[i]].Name, 40 - slide, y, 128, 0,
                Style.Fade(Locked(cars[i]) ? Dim : sel ? Canvas.Yellow : Canvas.White, a), 0.12f, 0.05f, 12);
        }
        var row = Array.IndexOf(cars, Car);
        c.Glow(20 - slide, 98 + row * 22, 172 - slide, 120 + row * 22, Canvas.Pulse(theta), a);
    }
}
