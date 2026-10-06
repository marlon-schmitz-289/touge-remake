using Kansei.Audio;
using Kansei.Input;
using Touge.Formats;
using Touge.Ui;
using Light = Touge.DualSenseSettings.Light;
using MicLight = Touge.DualSenseSettings.MicLight;

namespace Touge;

/// <summary>DualSense output (<see cref="DualSenseFeedback"/>), its options page (shown while one is connected) and --dualsense-test.</summary>
public sealed partial class TougeGame
{
    /// <summary>--dualsense-test: every DualSense effect in turn for <see cref="DualSenseFeedback.ScriptSeconds"/>, logged, then quit.</summary>
    public bool DualSenseTest { get; init; }
    private DualSenseFeedback? _ds;
    private PadSpeaker? _padSpeaker;
    private Options.Page? _dsPage;
    private float _dsTime, _dsDt;
    // per-frame context of DsSeat, delegates made once (no per-frame allocation)
    private bool _dsDriving;
    private int _dsP2Pad = -1;
    private VsCar? _dsP2;
    private Func<int, DualSenseFeedback.Seat>? _dsSeatOf;
    private Func<int, float>? _dsRoughness;
    private Func<int, DualSenseFeedback.Output?>? _dsTestScript;
    private UnhandledExceptionEventHandler? _dsCrash;

    private void LoadDualSense(Iso9660 iso)
    {
        _padSpeaker = new PadSpeaker(Window.Sdl);
        _ds = new DualSenseFeedback(_settings.DualSense, _padSpeaker);
        _ds.LoadSounds(iso);
        _dsCrash = (_, _) => _ds.Reset(Input.Pads); // a crash skips Dispose: pads dark and free anyway (SDL is still up here)
        AppDomain.CurrentDomain.UnhandledException += _dsCrash;
        if (_menu == null) return;
        _dsPage = DualSensePage(_settings.DualSense, _ds);
        _menu.Options.Show(_dsPage, Input.Pads.Any(p => p.IsDualSense)); // --menu options:dualsense finds it at once
    }

    private static Options.Page DualSensePage(DualSenseSettings s, DualSenseFeedback f) =>
        new("DUALSENSE", "PS5 controller: lightbar, adaptive triggers, rumble, speaker, LEDs, touchpad, tilt.")
        {
            Rows =
            {
                Options.Row.Choice("LIGHTBAR", ["OFF", "CAR COLOUR", "RPM", "PLAYER"], () => (int)s.Lightbar, i => s.Lightbar = (Light)i,
                    "CAR COLOUR: the paint.  RPM: green, yellow, red, flashing at the shift point.", "PLAYER: blue, red … per player.  Red pulse on contacts."),
                Options.Row.Slider("BRIGHTNESS", () => s.Brightness, v => s.Brightness = v, "Lightbar brightness (menus: a calm green)."),
                Options.Row.Action("LIGHTBAR TEST", "TEST", () => f.Test("LIGHTBAR"), "DECIDE: two seconds of rainbow."),
                Options.Row.Toggle("TRIGGERS", () => s.Triggers, v => s.Triggers = v,
                    "R2: vibrates with wheel spin, kicks on gear changes.", "L2: stiffens with brake pressure, pulses when a wheel locks."),
                Options.Row.Slider("TRIGGER FORCE", () => s.TriggerStrength, v => s.TriggerStrength = v, "Strength of the adaptive triggers."),
                Options.Row.Action("TRIGGER TEST", "TEST", () => f.Test("TRIGGERS"), "DECIDE, then pull R2 (vibration) and L2 (resistance)."),
                Options.Row.Toggle("RUMBLE", () => s.Rumble, v => s.Rumble = v,
                    "Engine, kerbs and grass, contacts, landings,", "drift slides and gear changes."),
                Options.Row.Slider("RUMBLE STRENGTH", () => s.RumbleStrength, v => s.RumbleStrength = v, "Strength of the rumble."),
                Options.Row.Action("RUMBLE TEST", "TEST", () => f.Test("RUMBLE"), "DECIDE: heavy and light motor in turn."),
                new Options.Row("SPEAKER", () => f.SpeakerAvailable ? ["ON", "OFF"] : ["USB ONLY"], () => s.Speaker ? 0 : 1, i =>
                    {
                        if (f.SpeakerAvailable) s.Speaker = i == 0;
                    },
                    () => f.SpeakerAvailable
                        ? ["Countdown, menu sounds, gear clicks and wall hits", "also from the controller's speaker."]
                        : ["The speaker needs the USB cable:", "over Bluetooth a computer gets no controller audio."]),
                Options.Row.Slider("SPEAKER VOLUME", () => s.SpeakerVolume, v => s.SpeakerVolume = v, "Volume of the controller's speaker."),
                Options.Row.Action("SPEAKER TEST", "TEST", () => f.Test("SPEAKER"), "DECIDE: the GO sound from the controller."),
                Options.Row.Toggle("PLAYER LEDS", () => s.PlayerLeds, v => s.PlayerLeds = v, "Player number under the touchpad (split screen: 1 and 2)."),
                Options.Row.Choice("MIC LED", ["OFF", "MUSIC OFF", "HEADLIGHTS"], () => (int)s.Mic, i => s.Mic = (MicLight)i,
                    "The mute button's light: on while the music is off,", "or while the headlights are on."),
                Options.Row.Toggle("TOUCHPAD", () => s.Touchpad, v => s.Touchpad = v, "Swipe left/right: next song.  Click: course map (N)."),
                Options.Row.Toggle("TILT STEER", () => s.Gyro, v => s.Gyro = v, "Steer by tilting the controller like a wheel", "(added to the stick)."),
                Options.Row.Slider("TILT SENSITIVITY", () => s.GyroSensitivity, v => s.GyroSensitivity = v, "Full lock at 60° (low) … 20° (high) of tilt."),
            },
        };

    /// <summary>Tilt steering for <see cref="DriverInput.Tilt"/>: the pad read, or (merged pads) the first DualSense.</summary>
    private float TiltOf(GamepadState? pad)
    {
        var s = _settings.DualSense;
        if (!s.Gyro) return 0;
        var ds = pad is { IsDualSense: true } ? pad : pad == Input.Gamepad ? Input.Pads.FirstOrDefault(p => p.IsDualSense) : null;
        return ds == null ? 0 : DualSenseFeedback.Tilt(ds.Accel, s.GyroSensitivity);
    }

    /// <summary>Per frame (after the driver input): every DualSense from its player's car; split screen: player 2's pad from player 2's car.</summary>
    private void UpdateDualSense(float dt)
    {
        if (_ds == null) return;
        if (_menuAudio != null) _menuAudio.Played ??= _ds.Speak; // made after the menus
        _driver.Tilt ??= TiltOf;
        if (_p2Input != null) _p2Input.Tilt ??= TiltOf;
        (_ds.MusicOff, _ds.LightsOn) = (!_settings.MusicOn, _lights.State != Headlights.Mode.Off);
        var driving = _dsDriving = !Frozen && !_fly && _inRace;
        _dsP2Pad = _vsSplit && _versusUi != null && _p2Input?.PadOf != null ? _versusUi.P2Device : -1;
        _dsP2 = _dsP2Pad >= 0 && _vsCars.Count > 0 ? _vsCars[0] : null;
        (_dsTime, _dsDt) = (_dsTime + dt, dt);
        if (_dsRoughness?.Target != _drive) _dsRoughness = _drive.Roughness; // a new run makes a new Drive
        _ds.Update(Input.Pads, _dsSeatOf ??= DsSeat, _dsRoughness, dt, DualSenseTest ? _dsTestScript ??= TestScript : null);
        if (DualSenseTest) TestStep();
        if (_dsPage != null) _menu!.Options.Show(_dsPage, _ds.Connected);
        if (_ds.Swiped && _jukebox != null)
        {
            _settings.MusicOn = true;
            _jukebox.Next();
        }
        if (_ds.Clicked && driving)
        {
            _hud.NextMode();
            _settings.MapMode = _hud.Mode;
        }
    }

    private DualSenseFeedback.Seat DsSeat(int pad) => pad == _dsP2Pad
        ? new(_dsDriving ? _dsP2?.Race?.Vehicle : null, _p2Input!.Brake, _dsP2 == null ? 0 : PaintOf(_dsP2.Car, _dsP2.Paint), 1)
        : new(_dsDriving ? _drive.Car : null, _brakeLight, PaintOf(_carName, _paint), 0);

    private uint PaintOf(string car, int paint) =>
        _catalog?.Cars.FirstOrDefault(c => c.Id == car)?.Paints is { Length: > 0 } p ? p[Math.Clamp(paint, 0, p.Length - 1)] : 0xFFFFFF;

    private float _dsLastLog = -1;

    /// <summary>--dualsense-test, once per frame: speaker cues at 13–16 s, tilt/touch of the first DualSense logged in the last part, quit at the end (with or without a pad).</summary>
    private void TestStep()
    {
        var t = _dsTime;
        if (Crossed(13) || Crossed(14) || Crossed(15)) _ds!.Speak("CAR010");
        if (Crossed(16)) _ds!.Speak("CAR011");
        if (Crossed(17)) _settings.DualSense.Gyro = true; // motion reports on for the tilt part
        if (t > 17 && t - _dsLastLog >= 0.5f && Input.Pads.FirstOrDefault(p => p.IsDualSense) is { } p)
        {
            _dsLastLog = t;
            Console.WriteLine($"[DualSense] {t:0.0} s Beschleunigung {p.Accel.X:0.00} {p.Accel.Y:0.00} {p.Accel.Z:0.00} m/s², Neigung {DualSenseFeedback.Tilt(p.Accel, 0.5f):+0.00;-0.00}, Touch {(p.Touch is { } tp ? $"{tp.X:0.00} {tp.Y:0.00}" : "-")}");
        }
        if (t >= DualSenseFeedback.ScriptSeconds && !Window.ShouldClose)
        {
            Console.WriteLine(_ds!.Connected ? "[DualSense] Test fertig" : "[DualSense] Test fertig: kein DualSense angeschlossen");
            Window.ShouldClose = true;
        }

        bool Crossed(float at) => t >= at && t - _dsDt < at;
    }

    /// <summary>--dualsense-test: the scripted output of pad <paramref name="pad"/>.</summary>
    private DualSenseFeedback.Output? TestScript(int pad) => DualSenseFeedback.Script(_dsTime, Input.Pads[pad].Accel);

    private void DisposeDualSense()
    {
        if (_dsCrash != null) AppDomain.CurrentDomain.UnhandledException -= _dsCrash;
        _ds?.Reset(Input.Pads);
        _padSpeaker?.Dispose();
    }
}
