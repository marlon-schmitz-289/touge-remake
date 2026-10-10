using System.Numerics;
using System.Text.Json;
using Kansei.Input;
using Kansei.Physics;
using Touge.Ui;
using SdlKey = Silk.NET.SDL.KeyCode;
using GameControllerAxis = Silk.NET.SDL.GameControllerAxis;

namespace Touge.Tests;

/// <summary>Bindings → driver input with simulated devices (no hardware): keyboard, a G29-like wheel, calibration, rebinding, force feedback.</summary>
public class ControlsTests
{
    const float Dt = 1 / 60f;

    /// <summary>A virtual G29: axis 0 wheel, 1 throttle, 2 brake, 3 clutch (pedals rest at +1), 20 buttons, one hat.</summary>
    static (InputSnapshot, JoystickState, DriverInput) Rig(ControlSettings? cfg = null)
    {
        var input = new InputSnapshot();
        var wheel = new JoystickState("G29 (sim)", 4, 20, 1, wheel: true);
        for (var i = 1; i < 4; i++) wheel.SetAxis(i, 1);
        input.AddVirtual(wheel);
        return (input, wheel, new DriverInput(cfg ?? new ControlSettings()));
    }

    static void Frame(InputSnapshot input, DriverInput d, JoystickState? w = null)
    {
        d.Update(input, Dt);
        input.Keyboard.BeginFrame();
        w?.BeginFrame();
    }

    [Fact]
    public void Keyboard_DefaultsAsBefore_SteeringRampsIn()
    {
        var input = new InputSnapshot();
        var d = new DriverInput(new ControlSettings());
        input.Keyboard.OnKeyDown((SdlKey)Key.Up);
        input.Keyboard.OnKeyDown((SdlKey)Key.D);
        Frame(input, d);
        Assert.Equal((1f, 0f), (d.Throttle, d.Brake));
        Assert.Equal(6 * Dt, d.Steer, 4); // from the centre 6/s, then 3/s (as before)
        for (var i = 0; i < 30; i++) Frame(input, d);
        Assert.Equal(1, d.Steer, 4);
        Assert.False(d.DirectSteer);
        input.Keyboard.OnKeyDown((SdlKey)Key.Escape);
        Frame(input, d);
        Assert.True(d.Pressed(Control.Pause));
    }

    [Fact]
    public void Wheel_DirectSteer_RotationAndSensitivity_SoftLock()
    {
        var (input, w, d) = Rig();
        Frame(input, d, w);
        Assert.Equal(DeviceKind.Keyboard, d.Active); // a wheel at rest takes nothing over
        Assert.Same(w, d.Wheel);
        w.SetAxis(0, 0.5f); // 900° wheel: 225° right; full lock at 450° (the car's 900° rack) → half lock
        Frame(input, d, w);
        Assert.Equal(DeviceKind.Wheel, d.Active);
        Assert.True(d.DirectSteer);
        Assert.Equal(0.5f, d.Steer, 3);
        d.Settings.Sensitivity = 2; // full lock at 225°
        Frame(input, d, w);
        Assert.Equal(1, d.Steer, 3);
        w.SetAxis(0, -0.8f); // past the lock: clamped, the excess is for the force feedback's soft lock
        Frame(input, d, w);
        Assert.Equal(-1, d.Steer, 3);
        Assert.True(d.SteerBeyond < -1.5f);
        d.Settings.InvertSteer = true;
        Frame(input, d, w);
        Assert.Equal(1, d.Steer, 3);
        Assert.Equal(d.Steer, d.Vehicle(0).Steer);
        Assert.True(d.Vehicle(0).DirectSteer);
    }

    [Fact]
    public void Wheel_PedalsRestAtPlusOne_DeadzoneAndInvert()
    {
        var (input, w, d) = Rig();
        Frame(input, d, w);
        Assert.Equal((0f, 0f, 0f), (d.Throttle, d.Brake, d.Clutch));
        w.SetAxis(1, -1);
        w.SetAxis(2, 0);
        w.SetAxis(3, 0.96f); // throttle floored, brake half, clutch inside the 3 % dead zone
        Frame(input, d, w);
        Assert.Equal(1, d.Throttle, 3);
        Assert.Equal(0.5f / 0.97f - 0.03f / 0.97f, d.Brake, 3);
        Assert.Equal(0, d.Clutch);
        d.Settings.InvertBrake = true;
        Frame(input, d, w);
        Assert.Equal((0.5f - 0.03f) / 0.97f, d.Brake, 3);
        Assert.True(d.Down(Control.Throttle));
    }

    [Fact]
    public void CombinedPedals_OneAxisTwoHalves()
    {
        var cfg = new ControlSettings { PedalDeadzone = 0 };
        cfg.Set(DeviceKind.Wheel, Control.Throttle, 0, Bind.JoyAxis(1, 0, -1));
        cfg.Set(DeviceKind.Wheel, Control.Brake, 0, Bind.JoyAxis(1, 0, 1));
        Assert.Equal(Source.JoyAxis, cfg.Get(DeviceKind.Wheel, Control.Throttle)[0].Source); // other half: no conflict
        var (input, w, d) = Rig(cfg);
        w.SetAxis(1, -0.4f);
        Frame(input, d, w);
        Assert.Equal((0.4f, 0f), (d.Throttle, d.Brake));
        w.SetAxis(1, 0.7f);
        Frame(input, d, w);
        Assert.Equal((0f, 0.7f), (d.Throttle, d.Brake));
    }

    [Fact]
    public void HShifter_GearHeldOrNeutral_PaddlesBackToSequential()
    {
        var (input, w, d) = Rig();
        var car = new Vehicle(CarSpec.AE86) { AutomaticGearbox = false };
        car.Reset(Vector3.Zero, 0);
        Frame(input, d, w);
        Assert.Null(d.HGear);
        w.SetButton(14, true); // shifter 3rd (button 15)
        Frame(input, d, w);
        Assert.Equal(3, d.HGear);
        Assert.Equal(2, d.Shift(car)); // 1st → 3rd in one go
        w.SetButton(14, false);
        Frame(input, d, w);
        Assert.Equal(0, d.HGear); // out of gear = neutral
        w.SetButton(18, true);
        Frame(input, d, w);
        Assert.Equal(-1, d.HGear);
        w.SetButton(18, false);
        w.SetButton(4, true); // right paddle
        d.Update(input, Dt);
        Assert.Null(d.HGear);
        Assert.Equal(1, d.Shift(car));
        car.AutomaticGearbox = true;
        Assert.Equal(0, d.Shift(car));
    }

    [Fact]
    public void Rebind_TakesTheInputAwayFromOthers_SurvivesJson()
    {
        var s = new Settings();
        s.Controls.Set(DeviceKind.Keyboard, Control.Handbrake, 1, Bind.OfKey(Key.W)); // W was throttle
        Assert.Equal(Source.None, s.Controls.Get(DeviceKind.Keyboard, Control.Throttle)[0].Source);
        Assert.Equal(Key.Up, (Key)s.Controls.Get(DeviceKind.Keyboard, Control.Throttle)[1].Code);
        s.Controls.Rotation = 540;
        var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        var back = JsonSerializer.Deserialize<Settings>(json, new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        Assert.Equal(Bind.OfKey(Key.W), back.Controls.Get(DeviceKind.Keyboard, Control.Handbrake)[1]);
        Assert.Equal(540, back.Controls.Rotation);
        Assert.Equal(Bind.JoyAxis(1, 1, -1), back.Controls.Get(DeviceKind.Wheel, Control.Throttle)[0]);
        back.Controls.Reset(DeviceKind.Keyboard);
        Assert.Equal(Bind.OfKey(Key.W), back.Controls.Get(DeviceKind.Keyboard, Control.Throttle)[0]);
        // a file from before the controls existed: defaults
        Assert.Equal(Bind.OfKey(Key.Space), JsonSerializer.Deserialize<Settings>("{}")!.Controls.Get(DeviceKind.Keyboard, Control.Handbrake)[0]);

        // menu and driving binds are separate groups: one button may do both
        var c = new ControlSettings();
        c.Set(DeviceKind.Wheel, Control.MenuOk, 0, Bind.Joy(4));
        Assert.Equal(Bind.Joy(4), c.Get(DeviceKind.Wheel, Control.ShiftUp)[0]);
        c.Set(DeviceKind.Wheel, Control.MenuBack, 0, Bind.Joy(4)); // same group: taken from MenuOk
        Assert.Equal(Source.None, c.Get(DeviceKind.Wheel, Control.MenuOk)[0].Source);
        c.Set(DeviceKind.Wheel, Control.ShiftDown, 0, Bind.Joy(4)); // taken from ShiftUp, MenuBack keeps it
        Assert.Equal(Source.None, c.Get(DeviceKind.Wheel, Control.ShiftUp)[0].Source);
        Assert.Equal(Bind.Joy(4), c.Get(DeviceKind.Wheel, Control.MenuBack)[0]);
    }

    /// <summary>DECIDE, then the input already in the slot clears it (the wheel has no DELETE); DELETE still clears on the keyboard page.</summary>
    [Fact]
    public void ControlsScreen_PressSameInputClears()
    {
        var cfg = new ControlSettings();
        var (input, w, d) = Rig(cfg);
        var screen = new ControlsScreen(cfg, input, d);
        screen.Open(DeviceKind.Wheel);
        void Step((int, int, bool, bool) k = default)
        {
            d.Update(input, Dt);
            screen.Update(k, Dt, null);
            w.BeginFrame();
            input.Keyboard.BeginFrame();
        }
        for (var i = 0; i < 21; i++) Step((0, 1, false, false)); // STEER LEFT (15) … SHIFT UP (+6)
        Step((0, 0, true, false));
        Step();
        w.SetButton(4, true);
        Step();
        w.SetButton(4, false);
        Assert.False(screen.Capturing);
        Assert.Equal(Source.None, cfg.Get(DeviceKind.Wheel, Control.ShiftUp)[0].Source);

        screen.Open(DeviceKind.Keyboard);
        screen.Update((0, 1, false, false), Dt, null); // first keyboard row: STEER LEFT
        input.Keyboard.OnKeyDown((SdlKey)Key.Delete);
        screen.Update(default, Dt, null);
        Assert.Equal(Source.None, cfg.Get(DeviceKind.Keyboard, Control.SteerLeft)[0].Source);
    }

    [Fact]
    public void Labels()
    {
        Assert.Equal(["A", "L-SHIFT", "ESC", "AXIS 2 -", "BUTTON 5", "HAT UP", "L-STICK LEFT", "RT", "-"],
            new[] { Bind.OfKey(Key.A), Bind.OfKey(Key.LeftShift), Bind.OfKey(Key.Escape), Bind.JoyAxis(1, 1, -1), Bind.Joy(4), Bind.Hat(1),
                Bind.PadAxis(GamepadAxis.LeftX, -1), Bind.PadAxis(GamepadAxis.TriggerRight, 1), Bind.None }.Select(b => b.Label));
    }

    /// <summary>Wheel page: walk to STEER LEFT, DECIDE, turn the wheel left → bound with rest 0; a pedal from its +1 rest → rest +1, full −1; calibration measures the travel.</summary>
    [Fact]
    public void ControlsScreen_PressToBind_AndCalibrate()
    {
        var cfg = new ControlSettings();
        var (input, w, d) = Rig(cfg);
        var screen = new ControlsScreen(cfg, input, d);
        var sounds = new List<string>();
        screen.Open(DeviceKind.Wheel);
        void Step((int, int, bool, bool) k = default)
        {
            d.Update(input, Dt);
            screen.Update(k, Dt, sounds.Add);
            w.BeginFrame();
            input.Keyboard.BeginFrame();
        }
        for (var i = 0; i < 15; i++) Step((0, 1, false, false)); // tabs → 14 wheel settings → STEER LEFT
        Step((0, 0, true, false));
        Assert.True(screen.Capturing);
        Step();
        w.SetAxis(0, -0.2f); // a centred axis binds after ~90° on a 900° wheel
        Step();
        Assert.False(screen.Capturing);
        Assert.Equal(Source.None, cfg.Get(DeviceKind.Wheel, Control.SteerLeft)[0].Source); // it was already bound there: the same input clears
        w.SetAxis(0, 0);
        Step((0, 0, true, false));
        Step();
        w.SetAxis(0, -0.2f);
        Step();
        Assert.Equal(Bind.JoyAxis(0, 0, -1), cfg.Get(DeviceKind.Wheel, Control.SteerLeft)[0]);
        w.SetAxis(0, 0);
        Step((0, 1, false, false)); // STEER RIGHT
        Step((0, 1, false, false)); // THROTTLE
        Step((1, 0, false, false)); // slot 2
        Step((0, 0, true, false));
        Step();
        w.SetAxis(3, -0.9f); // the clutch pedal pressed
        Step();
        Assert.Equal(Bind.JoyAxis(3, 1, -1), cfg.Get(DeviceKind.Wheel, Control.Throttle)[1]);
        Assert.Equal(Source.None, cfg.Get(DeviceKind.Wheel, Control.Clutch)[0].Source); // taken from the clutch
        w.SetAxis(3, 1);

        // calibration: worn throttle rests at 0.9 and only reaches −0.8
        for (var i = 0; i < 16; i++) Step((0, -1, false, false));
        Step((0, 1, false, false)); // CALIBRATE
        w.SetAxis(1, 0.9f);
        Step((0, 0, true, false));
        Step((0, 0, true, false)); // rest taken
        w.SetAxis(1, -0.8f);
        Step();
        w.SetAxis(0, 0.95f);
        w.SetAxis(1, 0.9f);
        Step();
        w.SetAxis(0, -0.97f);
        Step();
        w.SetAxis(0, 0);
        Step((0, 0, true, false));
        Assert.Equal(Bind.JoyAxis(1, 0.9f, -0.8f), cfg.Get(DeviceKind.Wheel, Control.Throttle)[0]);
        Assert.Equal(Bind.JoyAxis(0, 0, 0.95f), cfg.Get(DeviceKind.Wheel, Control.SteerRight)[0]);
        Assert.Equal(-0.97f, cfg.Get(DeviceKind.Wheel, Control.SteerLeft)[0].Full);
        Assert.DoesNotContain("BEEP001", sounds);
    }

    /// <summary>Keys the game reads itself (T = AT/MT …) are refused while binding.</summary>
    [Fact]
    public void ControlsScreen_Keyboard_RefusesFixedKeys()
    {
        var (input, _, d) = Rig();
        var screen = new ControlsScreen(d.Settings, input, d);
        screen.Open(DeviceKind.Keyboard);
        input.Keyboard.OnKeyDown((SdlKey)Key.T);
        Assert.Null(screen.Detect());
        input.Keyboard.OnKeyDown((SdlKey)Key.Q);
        Assert.Equal(new Bind(Source.Key, (int)Key.Q), screen.Detect());
    }

    /// <summary>A connected wheel nobody uses: a bump keeps keyboard/pad steering; small stick motion claims the pad back.</summary>
    [Fact]
    public void UnattendedWheel_DoesNotStealSteering()
    {
        var (input, w, d) = Rig();
        Frame(input, d, w);
        input.Keyboard.OnKeyDown((SdlKey)Key.D);
        for (var i = 0; i < 30; i++) Frame(input, d, w);
        input.Keyboard.OnKeyUp((SdlKey)Key.D);
        Frame(input, d, w);
        w.SetAxis(0, 0.05f); // ~22° bump
        Frame(input, d, w);
        Assert.Equal(DeviceKind.Keyboard, d.Active);
        Assert.False(d.DirectSteer);
        w.SetAxis(0, 0.2f); // really turned: the wheel steers
        Frame(input, d, w);
        Assert.Equal(DeviceKind.Wheel, d.Active);
        input.Gamepad.OnAxis(GameControllerAxis.Leftx, 10000); // ~0.3 stick, past the dead zone
        Frame(input, d, w);
        Assert.Equal(DeviceKind.Pad, d.Active);
        Assert.False(d.DirectSteer);
        Assert.True(d.Steer > 0);
    }

    [Fact]
    public void Menu_OptionsOpensControls_BackReturns()
    {
        var input = new InputSnapshot();
        var s = new Settings();
        var catalog = new Catalog([], [new Catalog.Car("AE86T", "TOYOTA", "TRUENO", "FR", 130, 940, [1])]);
        var d = new DriverInput(s.Controls);
        var m = new Menu(catalog, s) { Controls = new ControlsScreen(s.Controls, input, d) };
        m.Open(Menu.Screen.Options, "AKINA_DAY", false, "AE86T", 0);
        m.Update((0, -1, false, false), Dt); // section list wraps to CONTROLLER, the last plate
        m.Update((0, 0, true, false), Dt);
        Assert.Equal(Menu.Screen.Controls, m.Current);
        Assert.Equal(Menu.Action.SettingsChanged, m.Update((0, 0, false, true), Dt));
        Assert.Equal(Menu.Screen.Options, m.Current);
        Assert.Equal("CONTROLLER", m.Options.Pages[m.Options.Section].Title);
    }

    // ------------------------------------------------------------------ force feedback

    /// <summary>Plane y = 0 (surface 1 = kerb where x &lt; −2), wall at z = <paramref name="wallZ"/>.</summary>
    sealed class Ground(float wallZ = float.PositiveInfinity) : IGround
    {
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out GroundHit hit)
        {
            hit = default;
            if (direction.Y >= 0 || origin.Y < 0) return false;
            var t = -origin.Y / direction.Y;
            if (t > maxDistance) return false;
            var p = origin + direction * t;
            hit = new GroundHit(p, Vector3.UnitY, t, p.X < -2 ? 1 : 0);
            return true;
        }

        public int CollideWalls(ReadOnlySpan<Vector3> probes, float radius, Span<WallContact> contacts)
        {
            var n = 0;
            for (var i = 0; i < probes.Length && n < contacts.Length; i++)
                if (probes[i].Z + radius - wallZ > 0) contacts[n++] = new WallContact(probes[i] with { Z = wallZ }, -Vector3.UnitZ, probes[i].Z + radius - wallZ, i);
            return n;
        }
    }

    static float Rough(int surface) => surface == 1 ? 1 : 0;

    [Fact]
    public void ForceFeedback_AligningTorqueAgainstTheTurn_LightensPastGrip()
    {
        var ground = new Ground();
        var car = new Vehicle(CarSpec.AE86);
        car.Reset(Vector3.Zero, 0);
        var ffb = new ForceFeedback();
        while (car.SpeedKmh < 60) car.Step(new VehicleInput(1, 0, 0), ground, 1 / 120f);
        float Hold(float steer)
        {
            for (var i = 0; i < 60; i++)
            {
                car.Step(new VehicleInput(0.3f, 0, steer, DirectSteer: true), ground, 1 / 120f);
                ffb.Update(car, Rough, 0, 1, 1 / 120f);
            }
            return ffb.Aligning;
        }
        var light = Hold(0.08f);
        var medium = Hold(0.2f);
        Assert.True(light < 0 && medium < light, $"steering right pulls left, more with more lock: {light:F3} {medium:F3}");
        Assert.True(Hold(-0.2f) > 0);
        var x = MathF.Abs(car.Wheels[0].SlipAngle / car.Spec.PeakSlipAngle);
        // past the peak slip angle the shape falls off again (the wheel goes light when the front washes out)
        static float Shape(float v) => v * MathF.Exp((1 - v * v) / 2);
        Assert.True(Shape(2.5f) < Shape(1) * 0.5f, $"x {x:F2}");
    }

    [Fact]
    public void ForceFeedback_KerbRumble_SoftLock_WallJolt()
    {
        var car = new Vehicle(CarSpec.AE86);
        car.Reset(new Vector3(-5, 0, 0), 0); // on the kerb strip
        var ffb = new ForceFeedback();
        var ground = new Ground(60);
        var signs = new HashSet<int>();
        float maxJolt = 0;
        for (var i = 0; i < 120 * 8; i++)
        {
            car.Step(new VehicleInput(1, 0, 0, DirectSteer: true), ground, 1 / 120f);
            ffb.Update(car, Rough, 0, 1, 1 / 120f);
            if (car.SpeedKmh > 20 && car.Position.Z < 50) signs.Add(MathF.Sign(ffb.Kerb));
            maxJolt = MathF.Max(maxJolt, MathF.Abs(ffb.Jolt));
        }
        Assert.Contains(1, signs);
        Assert.Contains(-1, signs);
        Assert.True(maxJolt > 0.3f, $"wall jolt {maxJolt:F2}");
        car.Reset(Vector3.Zero, 0);
        var ffb2 = new ForceFeedback();
        Assert.Equal(-1, ffb2.Update(car, Rough, 1.2f, 0.5f, 1 / 120f), 3); // 20 % past the lock: full spring back
        Assert.Equal(0, new ForceFeedback().Update(car, Rough, 1.2f, 0, 1 / 120f)); // strength 0 = off
    }

    /// <summary>At speed the wheel clearly centres (well over a belt wheel's dead band), straight it is quiet, and the damper opposes the wheel's motion.</summary>
    [Fact]
    public void ForceFeedback_CentresAtSpeed_DamperOpposesMotion()
    {
        var ground = new Ground();
        var car = new Vehicle(CarSpec.AE86);
        car.Reset(Vector3.Zero, 0);
        var ffb = new ForceFeedback();
        while (car.SpeedKmh < 60) car.Step(new VehicleInput(1, 0, 0), ground, 1 / 120f);
        for (var i = 0; i < 60; i++)
        {
            car.Step(new VehicleInput(0.3f, 0, 0, DirectSteer: true), ground, 1 / 120f);
            ffb.Update(car, Rough, 0, 0.7f, 1 / 120f);
        }
        Assert.True(MathF.Abs(ffb.Output) < 0.02f, $"straight {ffb.Output:F3}");
        for (var i = 0; i < 60; i++)
        {
            car.Step(new VehicleInput(0.3f, 0, 0.1f, DirectSteer: true), ground, 1 / 120f);
            ffb.Update(car, Rough, 0.1f, 0.7f, 1 / 120f);
        }
        Assert.True(ffb.Output < -0.15f, $"0.1 lock at 60 km/h {ffb.Output:F3}");
        ffb.Update(car, Rough, 0.2f, 0.7f, 1 / 120f); // the wheel turned right quickly
        Assert.True(ffb.Damper < 0);
    }

    /// <summary>Parked, the wheel is heavy to turn (friction against its motion) but does not push by itself; the soft knee never clips.</summary>
    [Fact]
    public void ForceFeedback_ParkedFriction_SoftKnee()
    {
        var car = new Vehicle(CarSpec.AE86);
        car.Reset(Vector3.Zero, 0);
        var ffb = new ForceFeedback();
        ffb.Update(car, Rough, 0, 1, 1 / 120f);
        ffb.Update(car, Rough, 0.01f, 1, 1 / 120f); // turned right at 1.2 lock/s
        Assert.True(ffb.Friction < -0.1f, $"friction {ffb.Friction:F3}");
        for (var i = 0; i < 30; i++) ffb.Update(car, Rough, 0.01f, 1, 1 / 120f); // held still
        Assert.True(MathF.Abs(ffb.Output) < 0.02f, $"held {ffb.Output:F3}");
        Assert.Equal(0.5f, ForceFeedback.Compress(0.5f));
        Assert.True(ForceFeedback.Compress(3) is > 0.95f and <= 1);
        Assert.True(ForceFeedback.Compress(0.9f) > ForceFeedback.Compress(0.8f));
    }

    [Fact]
    public void ForceFeedback_Lift_ClearsTheDeadBand()
    {
        Assert.Equal(0, ForceFeedback.Lift(0, 0.05f));
        Assert.Equal(0.0345f, ForceFeedback.Lift(0.01f, 0.05f), 4);
        Assert.Equal(-1, ForceFeedback.Lift(-1, 0.05f), 5);
        Assert.Equal(0.335f, ForceFeedback.Lift(0.3f, 0.05f), 4);
    }

    /// <summary>A T150 (by USB id) gets its own layout while the wheel binds are the untouched defaults; customised binds stay.</summary>
    [Fact]
    public void T150_Profile_AdoptedOnlyOverDefaults()
    {
        JoystickState T150() => new("T150 (sim)", 4, 13, 1, true) { Vendor = 0x044F, Product = 0xB677 };
        var input = new InputSnapshot();
        input.AddVirtual(T150());
        var cfg = new ControlSettings();
        new DriverInput(cfg).Update(input, Dt);
        Assert.Equal(Bind.JoyAxis(2, 1, -1), cfg.Get(DeviceKind.Wheel, Control.Throttle)[0]);
        Assert.Equal(Bind.JoyAxis(1, 1, -1), cfg.Get(DeviceKind.Wheel, Control.Brake)[0]);
        Assert.Equal([Bind.None, Bind.None], cfg.Get(DeviceKind.Wheel, Control.Gear1));
        Assert.Equal([Bind.None, Bind.None], cfg.Get(DeviceKind.Wheel, Control.Clutch));

        var custom = new ControlSettings();
        custom.Set(DeviceKind.Wheel, Control.Camera, 0, Bind.Joy(11));
        new DriverInput(custom).Update(input, Dt);
        Assert.Equal(Bind.JoyAxis(1, 1, -1), custom.Get(DeviceKind.Wheel, Control.Throttle)[0]); // G29 layout kept
        Assert.Equal(Bind.Joy(11), custom.Get(DeviceKind.Wheel, Control.Camera)[0]);
        custom.Reset(DeviceKind.Wheel, T150()); // RESET on the wheel page: the profile
        Assert.Equal(Bind.JoyAxis(2, 1, -1), custom.Get(DeviceKind.Wheel, Control.Throttle)[0]);
    }
}
