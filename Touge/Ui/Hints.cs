using System.Text.RegularExpressions;
using Kansei.Input;

namespace Touge.Ui;

/// <summary>
///     Button hints for the device used last (keyboard, pad or wheel; <see cref="Track"/> follows it, --hint-device forces
///     it): <see cref="Menu"/> turns the menu words of a hint line (DECIDE, BACK, arrows) into that device's keys,
///     <see cref="Of"/> names a rebindable action's binding, <see cref="Pick"/> chooses a screen's own text per device.
/// </summary>
public static partial class Hints
{
    /// <summary>The device that gave input last.</summary>
    public static DeviceKind Last { get; set; }
    /// <summary>--hint-device: always this device's hints (screenshots, tests).</summary>
    public static DeviceKind? Forced { get; set; }
    public static DeviceKind Device => Forced ?? Last;
    /// <summary>The bindings shown (the game's <see cref="Settings.Controls"/>).</summary>
    public static ControlSettings Controls { get; set; } = new();

    public static string Pick(string keys, string pad, string wheel) => Device switch { DeviceKind.Pad => pad, DeviceKind.Wheel => wheel, _ => keys };

    /// <summary>The binding of <paramref name="c"/> on the current device (the first slot that is set), "-" when unbound.</summary>
    public static string Of(Control c) => Controls.Get(Device, c).FirstOrDefault(b => b.Source != Source.None)?.Label ?? "-";

    [GeneratedRegex(@"\b(?:DECIDE|BACK|UP/DOWN|LEFT/RIGHT|ARROWS|UP|DOWN|LEFT|RIGHT)\b")]
    private static partial Regex MenuWord();

    /// <summary>
    ///     A hint line "KEYS: Action    KEYS: Action" with the menu words in the KEYS parts replaced: keyboard ENTER/ESC, pad
    ///     A/B and D-PAD, wheel its MENU DECIDE/BACK bindings and the HAT. Parts without a colon stay as they are.
    /// </summary>
    public static string Menu(string text) => string.Join("", Regex.Split(text, @"(\s{2,})").Select(part =>
        part.IndexOf(':') is var colon and > 0 ? MenuWord().Replace(part[..colon], m => Key(m.Value)) + part[colon..] : part));

    /// <summary>One menu word (DECIDE, BACK, UP/DOWN, LEFT/RIGHT, ARROWS, UP …) as the current device's key.</summary>
    public static string Key(string w) => (Device, w) switch
    {
        (DeviceKind.Keyboard, "DECIDE") => "ENTER",
        (DeviceKind.Keyboard, "BACK") => "ESC",
        (DeviceKind.Keyboard, _) => w,
        (DeviceKind.Pad, "DECIDE") => "A",
        (DeviceKind.Pad, "BACK") => "B",
        (DeviceKind.Pad, "ARROWS") => "D-PAD",
        (DeviceKind.Pad, _) => "D-PAD " + w,
        (_, "DECIDE") => Of(Control.MenuOk),
        (_, "BACK") => Of(Control.MenuBack),
        (_, "ARROWS") => "HAT",
        _ => "HAT " + w,
    };

    /// <summary>Follows the device used last: a key, a pad button or stick, a wheel button or hat (the mouse does not count).</summary>
    public static void Track(InputSnapshot input, IReadOnlyList<GamepadState> pads, JoystickState? wheel)
    {
        if (input.Keyboard.PressedThisFrame.Any()) Last = DeviceKind.Keyboard;
        foreach (var pad in pads)
            if (pad.PressedThisFrame.Any() || Enum.GetValues<GamepadAxis>().Any(a => MathF.Abs(pad.GetAxis(a)) > 0.5f))
                Last = DeviceKind.Pad;
        if (wheel != null && (Enumerable.Range(0, wheel.ButtonCount).Any(wheel.ButtonPressed)
                              || Enumerable.Range(0, wheel.HatCount).Any(h => wheel.HatPressed(h, wheel.Hat(h)))))
            Last = DeviceKind.Wheel;
    }
}
