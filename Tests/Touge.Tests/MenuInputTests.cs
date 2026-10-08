using Kansei.Input;
using Touge.Ui;
using SdlKey = Silk.NET.SDL.KeyCode;
using SdlButton = Silk.NET.SDL.GameControllerButton;

namespace Touge.Tests;

/// <summary>Menu auto-repeat of pad/wheel directions (keyboard timing) and the device-aware button hints.</summary>
public class MenuInputTests
{
    const float Dt = 1 / 60f;

    [Fact]
    public void HoldRepeat_FiresOnPress_ThenAfterDelay_ThenEveryInterval_ReleaseResets()
    {
        var r = new HoldRepeat();
        Assert.False(r.Update(false, Dt));
        Assert.True(r.Update(true, Dt)); // the press
        var t = 0f;
        var fires = new List<float>();
        for (var i = 0; i < 60; i++)
        {
            t += Dt;
            if (r.Update(true, Dt)) fires.Add(t);
        }
        Assert.InRange(fires[0], KeyboardState.RepeatDelay, KeyboardState.RepeatDelay + Dt); // first repeat after the delay
        for (var i = 1; i < fires.Count; i++) // then about every interval (whole frames)
            Assert.InRange(fires[i] - fires[i - 1], Dt * 0.99f, KeyboardState.RepeatInterval + Dt);
        Assert.InRange(fires.Count, (int)((1 - KeyboardState.RepeatDelay) / (KeyboardState.RepeatInterval + Dt)), (int)((1 - KeyboardState.RepeatDelay) / KeyboardState.RepeatInterval) + 1);
        Assert.False(r.Update(false, Dt)); // released
        Assert.True(r.Update(true, Dt)); // a new press fires at once
        Assert.False(r.Update(true, Dt)); // and waits for the delay again
    }

    /// <summary>RepeatY: a press (also a quick re-tap) is not a repeat, the steps of a held key after the delay are.</summary>
    [Fact]
    public void RepeatY_OnlyWhileHeld()
    {
        var input = new InputSnapshot();
        var keys = new MenuKeys();
        (int Y, bool Repeat) Frame(bool down)
        {
            if (down) input.Keyboard.OnKeyDown((SdlKey)Key.Down); else input.Keyboard.OnKeyUp((SdlKey)Key.Down);
            var y = keys.Read(input, Dt).Y;
            input.Keyboard.BeginFrame();
            return (y, keys.RepeatY);
        }

        Assert.Equal((1, false), Frame(true));
        Assert.Equal((0, false), Frame(false));
        Assert.Equal((1, false), Frame(true)); // re-tapped two frames later: still a press
        (int, bool) last = default;
        for (var i = 0; i < 30 && last.Item1 == 0; i++) last = Frame(true);
        Assert.Equal((1, true), last); // the first key repeat
    }

    /// <summary>Steps of <paramref name="hold"/> held for one second through <see cref="MenuKeys"/>, after one idle frame.</summary>
    static List<int> HoldSteps(InputSnapshot input, Action<bool> hold, MenuKeys? keys = null, JoystickState? wheel = null)
    {
        keys ??= new MenuKeys();
        var steps = new List<int>();
        for (var i = 0; i < 61; i++)
        {
            if (i == 1) hold(true);
            steps.Add(keys.Read(input, Dt).Y);
            input.Keyboard.BeginFrame();
            input.Gamepad.BeginFrame();
            wheel?.BeginFrame();
        }
        hold(false);
        steps.Add(keys.Read(input, Dt).Y);
        return steps;
    }

    [Fact]
    public void Pad_DpadAndStick_RepeatLikeTheKeyboard()
    {
        var kin = new InputSnapshot();
        var keyboard = HoldSteps(kin, on => { if (on) kin.Keyboard.OnKeyDown((SdlKey)Key.Down); else kin.Keyboard.OnKeyUp((SdlKey)Key.Down); });
        Assert.True(keyboard.Count(s => s == 1) > 10); // the reference: a held key repeats

        var pin = new InputSnapshot();
        pin.Gamepad.IsConnected = true;
        var dpad = HoldSteps(pin, on => { if (on) pin.Gamepad.OnButtonDown(SdlButton.DpadDown); else pin.Gamepad.OnButtonUp(SdlButton.DpadDown); });
        Assert.Equal(keyboard, dpad);

        var sin = new InputSnapshot();
        sin.Gamepad.IsConnected = true;
        var stick = HoldSteps(sin, on => sin.Gamepad.OnAxis(Silk.NET.SDL.GameControllerAxis.Lefty, (short)(on ? 30000 : 0)));
        Assert.Equal(keyboard, stick);
        Assert.Equal(0, stick[^1]); // released: nothing more
    }

    [Fact]
    public void Wheel_HatRepeats_AndIsTrackedAsTheDevice()
    {
        var input = new InputSnapshot();
        var wheel = new JoystickState("G29 (sim)", 4, 20, 1, wheel: true);
        input.AddVirtual(wheel);
        var keys = new MenuKeys { Wheel = new ControlSettings() };
        var steps = HoldSteps(input, on => wheel.SetHat(0, (byte)(on ? 4 : 0)), keys, wheel);
        Assert.True(steps.Count(s => s == 1) > 10);
        Assert.Equal(1, steps[1]);
        Assert.Equal(0, steps[2]);
        Assert.Equal(DeviceKind.Wheel, Hints.Last);

        input.Keyboard.OnKeyDown((SdlKey)Key.Enter);
        keys.Read(input, Dt);
        Assert.Equal(DeviceKind.Keyboard, Hints.Last);
        input.Gamepad.IsConnected = true;
        input.Gamepad.OnButtonDown(SdlButton.A);
        keys.Read(input, Dt);
        Assert.Equal(DeviceKind.Pad, Hints.Last);
    }

    [Fact]
    public void Hints_PerDevice()
    {
        const string line = "UP/DOWN: Select    LEFT/RIGHT: Change    DECIDE: OK    BACK: Return    Best time per route";
        try
        {
            Hints.Controls = new ControlSettings();
            Hints.Forced = DeviceKind.Keyboard;
            Assert.Equal("UP/DOWN: Select    LEFT/RIGHT: Change    ENTER: OK    ESC: Return    Best time per route", Hints.Menu(line));
            Assert.Equal("R", Hints.Of(Control.ResetCar));
            Hints.Controls.Set(DeviceKind.Keyboard, Control.ResetCar, 0, Bind.OfKey(Key.G)); // rebound: the hint follows
            Assert.Equal("G", Hints.Of(Control.ResetCar));

            Hints.Forced = DeviceKind.Pad;
            Assert.Equal("D-PAD UP/DOWN: Select    D-PAD LEFT/RIGHT: Change    A: OK    B: Return    Best time per route", Hints.Menu(line));
            Assert.Equal("D-PAD RIGHT/B: Skip", Hints.Menu("RIGHT/BACK: Skip"));
            Assert.Equal("Y", Hints.Of(Control.ResetCar));
            Assert.Equal("pad", Hints.Pick("keys", "pad", "wheel"));

            Hints.Forced = DeviceKind.Wheel;
            Assert.Equal("HAT UP/DOWN: Select    HAT LEFT/RIGHT: Change    BUTTON 1: OK    BUTTON 3: Return    Best time per route", Hints.Menu(line));
            Assert.Equal("BUTTON 4", Hints.Of(Control.ResetCar));
        }
        finally
        {
            (Hints.Forced, Hints.Controls) = (null, new ControlSettings());
        }
    }

    [Fact]
    public void ControlsFooter_FollowsTheRow()
    {
        var input = new InputSnapshot();
        var s = new Settings();
        var screen = new ControlsScreen(s.Controls, input, new DriverInput(s.Controls));
        try
        {
            Hints.Forced = DeviceKind.Keyboard;
            screen.Open();
            screen.Update((0, 0, false, false), Dt, null);
            Assert.StartsWith("LEFT/RIGHT: Device", screen.Footer); // the tab row: the same as its help, no DECIDE: Bind
            screen.Update((0, 1, false, false), Dt, null); // first keyboard row: an action
            Assert.Equal("UP/DOWN: Select    LEFT/RIGHT: Slot    ENTER: Bind    DELETE: Clear    ESC: Options", screen.Footer);
            Hints.Forced = DeviceKind.Pad;
            Assert.Equal("D-PAD UP/DOWN: Select    D-PAD LEFT/RIGHT: Slot    A: Bind    X: Clear    B: Options", screen.Footer);
        }
        finally
        {
            Hints.Forced = null;
        }
    }
}
