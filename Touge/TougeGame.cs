using System.Numerics;
using Kansei.Audio;
using Kansei.Core;
using Kansei.Graphics;
using Kansei.Input;
using Kansei.Physics;
using Touge.Formats;

namespace Touge;

/// <summary>
///     Course from the ISO with a drivable AE86 (default) and a free-fly camera (F1).
///     Drive: W/S or ↑/↓ throttle/brake (automatic: hold S at standstill to reverse), A/D or ←/→ steer, Space handbrake, T auto/manual, Shift/Ctrl gear up/down (manual),
///     R reset onto the driving line, C chase/bumper camera. Pad: left stick, triggers, A handbrake, bumpers shift.
///     Fly: WASD, Q/E down/up, right mouse or arrow keys look, Shift fast, Space jump along the driving line. Esc quit.
///     <paramref name="orbit"/> (degrees, 0 = front, 90 = left, 180 = rear) puts the fly camera around the car;
///     <paramref name="autodrive"/> lets the line pilot drive that many seconds before the first frame (for --shot);
///     <paramref name="bench"/> lets it drive live for that many seconds, then logs avg/max frame time and quits.
///     Sound: engine, tyres, walls, wind, race BGM (M next track, F3 music on/off); none for --shot.
/// </summary>
public sealed class TougeGame(string isoPath, string courseTime, string? shotPath = null, int startPoint = 0, float? orbit = null, float? autodrive = null, float? bench = null)
    : KanseiGame
{
    private AudioDevice? _audioDevice;
    private GameAudio? _audio;
    private double _benchTime, _benchMax;
    private int _benchFrames, _benchSlow;

    private CarRenderer _carRenderer = null!;
    private CarModel _car = null!;
    private Matrix4x4 _carBody, _carPose, _modelToBody;
    private readonly Matrix4x4[] _carWheels = new Matrix4x4[4];
    private FrameCapture? _capture;
    private int _shotState; // 0 none, 1 render next frame into capture, 2 read back
    private WorldRenderer _renderer = null!;
    private CourseLoader.Course _course = null!;
    private Drive _drive = null!;

    // physics → render interpolation
    private Vector3 _prevPos;
    private Quaternion _prevRot;

    // driver input, sampled per frame, consumed per tick
    private float _throttle, _brake, _steer;
    private bool _handbrake;
    private int _pendingShift;

    // cameras
    private bool _fly, _bumperCam, _camSnap = true;
    private Vector3 _pos, _camLook;
    private float _yaw, _pitch, _fov = MathF.PI / 3;
    private int _lastMouseX, _lastMouseY, _linePoint;
    private double _statusTime;
    private int _frames;

    public override void Load()
    {
        using var iso = new Iso9660(isoPath);
        _renderer = new WorldRenderer(Device);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _course = CourseLoader.Load(iso, courseTime, _renderer);
        _drive = new Drive(iso, courseTime);
        Console.WriteLine($"[Touge] {courseTime} geladen in {sw.ElapsedMilliseconds} ms, {_course.World.Batches.Count} Batches, {_drive.Ground.Walls.Length} Wandsegmente");
        _carRenderer = new CarRenderer(_renderer);
        _car = CarModel.Load(iso, "AE86T", 0, _renderer);

        // model space → physics body space (origin CoG): model wheel centres onto the physics wheel centres at rest
        var spec = _drive.Car.Spec;
        var modelWheels = Vector3.Zero;
        foreach (var w in _car.Wheels) modelWheels += w.Translation / 4;
        _modelToBody = Matrix4x4.CreateTranslation(new Vector3(0, spec.WheelRadius - spec.CogHeight, spec.Wheelbase * (0.5f - spec.FrontWeight)) - modelWheels);

        _drive.ResetTo(startPoint);
        if (autodrive is { } seconds) _drive.AutoDrive(seconds);
        SyncPose();
        JumpToLine(startPoint);
        if (orbit is { } deg)
        {
            _fly = true;
            UpdateCarMatrices(1);
            OrbitCar(deg * MathF.PI / 180);
        }
        if (shotPath != null) (_capture, _shotState) = (new FrameCapture(Device, 1280, 720), 1);
        else
        {
            _audioDevice = new AudioDevice();
            _audio = new GameAudio(iso, courseTime, _audioDevice);
            _audio.PlayTrack(background: true);
        }
    }

    private void SyncPose() => (_prevPos, _prevRot, _camSnap) = (_drive.Car.Position, _drive.Car.Orientation, true);

    public override void Tick(float dt)
    {
        var car = _drive.Car;
        (_prevPos, _prevRot) = (car.Position, car.Orientation);
        var input = autodrive != null || bench != null ? _drive.Pilot.Drive(car)
            : _fly ? new VehicleInput(0, 0, 0, true)
            : new VehicleInput(_throttle, _brake, _steer, _handbrake, _pendingShift);
        _pendingShift = 0;
        car.Step(input, _drive.Ground, dt);
        _audio?.Update(car, input.Throttle, input.Handbrake, dt);
    }

    private void JumpToLine(int i)
    {
        var line = _course.DrivingLine;
        _linePoint = i % line.Length;
        var a = line[_linePoint];
        var b = line[Math.Min(_linePoint + 2, line.Length - 1)];
        _pos = a + new Vector3(0, 1.2f, 0);
        _yaw = MathF.Atan2(b.X - a.X, b.Z - a.Z);
        _pitch = 0;
    }

    private void OrbitCar(float angle)
    {
        var target = Vector3.Transform(new Vector3(0, 0.4f, 0), _carBody);
        var dir = Vector3.TransformNormal(new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)), _carBody);
        _pos = target + dir * 5.5f + new Vector3(0, 1.3f, 0);
        var look = Vector3.Normalize(target - _pos);
        (_yaw, _pitch) = (MathF.Atan2(look.X, look.Z), MathF.Asin(look.Y));
    }

    public override void Update(in GameTime time)
    {
        if (_shotState == 2)
        {
            Png.Write(shotPath!, _capture!.Width, _capture.Height, _capture.ReadRgba());
            Console.WriteLine($"[Touge] Screenshot -> {shotPath}");
            Window.ShouldClose = true;
            return;
        }
        var k = Input.Keyboard;
        var dt = time.DeltaTime;
        if (k.IsKeyPressed(Key.Escape)) Window.ShouldClose = true;
        if (_audio != null && k.IsKeyPressed(Key.M)) _audio.NextTrack();
        if (_audio != null && k.IsKeyPressed(Key.F3)) _audio.MusicOn = !_audio.MusicOn;
        if (bench is { } benchSeconds && time.TotalTime > 1) // first second: window/pipeline warm-up hitches (also without audio)
        {
            (_benchTime, _benchMax, _benchFrames) = (_benchTime + dt, Math.Max(_benchMax, dt), _benchFrames + 1);
            if (dt > 0.025) _benchSlow++; // a missed vsync at 60 Hz shows as ≥ 33 ms
            if (_benchTime >= benchSeconds)
            {
                Console.WriteLine($"\n[Bench] {_benchFrames} Frames in {_benchTime:F1} s (nach 1 s Aufwärmen): Ø {_benchTime / _benchFrames * 1000:F2} ms ({_benchFrames / _benchTime:F1} fps), max {_benchMax * 1000:F2} ms, {_benchSlow} Frames > 25 ms");
                Window.ShouldClose = true;
            }
        }
        if (k.IsKeyPressed(Key.F1))
        {
            _fly = !_fly;
            if (_fly) (_yaw, _pitch) = (MathF.Atan2(_camLook.X - _pos.X, _camLook.Z - _pos.Z), 0);
            else _camSnap = true;
        }
        if (_fly) UpdateFly(time);
        else UpdateDriver(dt);

        _frames++;
        if (time.TotalTime - _statusTime >= 0.1)
        {
            var c = _drive.Car;
            Console.Write($"\r[Touge] {c.SpeedKmh,4:F0} km/h  Gang {Drive.Gear(c),-2}  {c.Rpm,5:F0} rpm  Schräglauf {c.SlipAngle * 180 / MathF.PI,6:F1}°  {_frames / (time.TotalTime - _statusTime),4:F0} fps   ");
            (_statusTime, _frames) = (time.TotalTime, 0);
        }
    }

    private void UpdateDriver(float dt)
    {
        var k = Input.Keyboard;
        var pad = Input.Gamepad;
        var car = _drive.Car;
        if (k.IsKeyPressed(Key.R))
        {
            _drive.ResetNearest();
            SyncPose();
        }
        if (k.IsKeyPressed(Key.C))
            (_bumperCam, _camSnap) = (!_bumperCam, true);
        if (k.IsKeyPressed(Key.T)) car.AutomaticGearbox = !car.AutomaticGearbox;
        if (!car.AutomaticGearbox)
        {
            if (k.IsKeyPressed(Key.LeftShift) || k.IsKeyPressed(Key.RightShift) || pad.IsButtonPressed(GamepadButton.RightShoulder)) _pendingShift = 1;
            if (k.IsKeyPressed(Key.LeftCtrl) || k.IsKeyPressed(Key.RightCtrl) || pad.IsButtonPressed(GamepadButton.LeftShoulder)) _pendingShift = -1;
        }

        float steerKey = (k.IsKeyDown(Key.D) || k.IsKeyDown(Key.Right) ? 1 : 0) - (k.IsKeyDown(Key.A) || k.IsKeyDown(Key.Left) ? 1 : 0);
        // keyboard steering ramps in (3/s) and returns faster (6/s), the pad stick is used as is
        var rate = (steerKey == 0 || MathF.Sign(steerKey) != MathF.Sign(_steer) ? 6 : 3) * dt;
        var stick = pad.IsConnected ? pad.GetAxis(GamepadAxis.LeftX) : 0;
        _steer = stick != 0 ? stick : _steer + Math.Clamp(steerKey - _steer, -rate, rate);
        var keyGas = k.IsKeyDown(Key.W) || k.IsKeyDown(Key.Up) ? 1f : 0;
        var keyBrake = k.IsKeyDown(Key.S) || k.IsKeyDown(Key.Down) ? 1f : 0;
        _throttle = MathF.Max(keyGas, pad.IsConnected ? pad.GetAxis(GamepadAxis.TriggerRight) : 0);
        _brake = MathF.Max(keyBrake, pad.IsConnected ? pad.GetAxis(GamepadAxis.TriggerLeft) : 0);
        _handbrake = k.IsKeyDown(Key.Space) || pad.IsButtonDown(GamepadButton.A);
    }

    private void UpdateFly(in GameTime time)
    {
        var k = Input.Keyboard;
        var dt = time.DeltaTime;
        if (k.IsKeyPressed(Key.Space)) JumpToLine(_linePoint + 40);

        var m = Input.Mouse;
        if (m.IsButtonDown(3))
        {
            _yaw -= (m.X - _lastMouseX) * 0.004f;
            _pitch = Math.Clamp(_pitch - (m.Y - _lastMouseY) * 0.004f, -1.5f, 1.5f);
        }
        (_lastMouseX, _lastMouseY) = (m.X, m.Y);
        if (k.IsKeyDown(Key.Left)) _yaw += 1.8f * dt;
        if (k.IsKeyDown(Key.Right)) _yaw -= 1.8f * dt;
        if (k.IsKeyDown(Key.Up)) _pitch = MathF.Min(_pitch + 1.2f * dt, 1.5f);
        if (k.IsKeyDown(Key.Down)) _pitch = MathF.Max(_pitch - 1.2f * dt, -1.5f);

        var fwd = Forward();
        var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
        var move = Vector3.Zero;
        if (k.IsKeyDown(Key.W)) move += fwd;
        if (k.IsKeyDown(Key.S)) move -= fwd;
        if (k.IsKeyDown(Key.D)) move += right;
        if (k.IsKeyDown(Key.A)) move -= right;
        if (k.IsKeyDown(Key.E)) move += Vector3.UnitY;
        if (k.IsKeyDown(Key.Q)) move -= Vector3.UnitY;
        var speed = k.IsKeyDown(Key.LeftShift) ? 120f : 25f;
        if (move != Vector3.Zero) _pos += Vector3.Normalize(move) * speed * dt;
        _camLook = _pos + Forward();
        _fov = MathF.PI / 3;
    }

    private Vector3 Forward() => new(MathF.Sin(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), MathF.Cos(_yaw) * MathF.Cos(_pitch));

    /// <summary>Body and wheel world matrices from the physics state, body pose interpolated between the last two ticks.</summary>
    private void UpdateCarMatrices(float alpha)
    {
        var car = _drive.Car;
        _carPose = Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(_prevRot, car.Orientation, alpha))
                   * Matrix4x4.CreateTranslation(Vector3.Lerp(_prevPos, car.Position, alpha));
        _carBody = _modelToBody * _carPose;
        var restY = car.Spec.WheelRadius - car.Spec.CogHeight;
        for (var i = 0; i < 4; i++)
        {
            // CarParts node order fr_l, fr_r, re_l, re_r = physics FL, FR, RL, RR; model node X/Z, physics suspension travel
            var w = car.Wheels[i];
            var node = _car.Wheels[i];
            var spinSteer = Matrix4x4.CreateRotationX(w.SpinAngle) * Matrix4x4.CreateRotationY(-w.SteerAngle);
            var flip = node with { M41 = 0, M42 = 0, M43 = 0 };
            _carWheels[i] = flip * spinSteer * Matrix4x4.CreateTranslation(node.Translation + new Vector3(0, w.LocalCenter.Y - restY, 0)) * _carBody;
        }
    }

    /// <summary>Chase camera (spring towards a point behind/above the car, looks a bit ahead) or bumper camera.</summary>
    private void UpdateDriveCamera(float dt)
    {
        var car = _drive.Car;
        var pos = _carPose.Translation; // interpolated CoG
        var fwd = Vector3.TransformNormal(Vector3.UnitZ, _carPose);
        if (_bumperCam)
        {
            var up = Vector3.TransformNormal(Vector3.UnitY, _carPose);
            _pos = pos + up * 0.15f + fwd * (car.Spec.Length / 2 + 0.05f);
            _camLook = _pos + fwd * 10;
            _fov = MathF.PI / 3;
            _camSnap = false;
            return;
        }
        var flat = Vector3.Normalize(fwd with { Y = 0 });
        var desired = pos - flat * 5.8f + Vector3.UnitY * 1.9f;
        var look = pos + flat * 4f + Vector3.UnitY * 0.6f;
        float a = _camSnap ? 1 : 1 - MathF.Exp(-6 * dt), b = _camSnap ? 1 : 1 - MathF.Exp(-12 * dt);
        _pos = Vector3.Lerp(_pos, desired, a);
        _camLook = Vector3.Lerp(_camLook, look, b);
        _fov = MathF.PI / 3 + MathF.Min(car.SpeedKmh / 180, 1) * 0.2f;
        _camSnap = false;
    }

    public override void Render(in FrameContext ctx)
    {
        var shot = _shotState == 1 ? _capture : null;
        var aspect = shot != null ? (float)shot.Width / shot.Height
            : ctx.Viewport.Height > 0 ? (float)ctx.Viewport.Width / ctx.Viewport.Height : 16f / 9f;
        UpdateCarMatrices(shot != null ? 1 : ctx.TickAlpha);
        if (!_fly) UpdateDriveCamera(ctx.Time.DeltaTime);
        // Game data is right-handed (y up). Vulkan clip space is Y-down, Metal/GL Y-up.
        var proj = WorldRenderer.Perspective(_fov, aspect, 0.3f, Device.Backend == Penelope.BackendKind.Vulkan);
        var view = Matrix4x4.CreateLookAt(_pos, _camLook, Vector3.UnitY);

        var pass = _renderer.BeginScene(ctx.Encoder, _renderer.FogColor, shot);
        // sky follows the camera, no fog
        var fog = _renderer.FogDistance;
        _renderer.FogDistance = float.MaxValue;
        _renderer.Draw(pass, _course.Sky, Matrix4x4.CreateTranslation(_pos with { Y = 0 }) * view * proj, Vector3.Zero);
        _renderer.FogDistance = fog;
        _renderer.Draw(pass, _course.World, view * proj, _pos);
        _carRenderer.Draw(pass, _car.Body, _car.Wheel, _carBody, _carWheels, view * proj, _pos);
        pass.Dispose();
        if (shot != null)
        {
            shot.Copy(ctx.Encoder);
            _shotState = 2;
        }
    }

    public override void Dispose()
    {
        _audio?.Dispose();
        _audioDevice?.Dispose();
        _course.World.Dispose();
        _course.Sky.Dispose();
        _car.Dispose();
        _carRenderer.Dispose();
        _capture?.Dispose();
        _renderer.Dispose();
    }
}
