using System.Numerics;
using Kansei.Core;
using Kansei.Graphics;
using Kansei.Input;
using Touge.Formats;

namespace Touge;

/// <summary>
///     Sandbox: course from the ISO, AE86 parked one driving-line point ahead of the start, free-fly camera.
///     <paramref name="orbit"/> (degrees, 0 = front, 90 = left side, 180 = rear) puts the camera around the car instead.
///     WASD fly, Q/E down/up, right mouse or arrow keys look, Shift fast, Space jump to driving line, Esc quit.
/// </summary>
public sealed class TougeGame(string isoPath, string courseTime, string? shotPath = null, int startPoint = 0, float? orbit = null) : KanseiGame
{
    private CarRenderer _carRenderer = null!;
    private CarModel _car = null!;
    private Matrix4x4 _carBody;
    private readonly Matrix4x4[] _carWheels = new Matrix4x4[4];
    private FrameCapture? _capture;
    private int _shotState; // 0 none, 1 render next frame into capture, 2 read back
    private WorldRenderer _renderer = null!;
    private CourseLoader.Course _course = null!;
    private Vector3 _pos;
    private float _yaw, _pitch;
    private int _lastMouseX, _lastMouseY, _linePoint;
    private double _fpsTime;
    private int _fpsFrames;

    public override void Load()
    {
        using var iso = new Iso9660(isoPath);
        _renderer = new WorldRenderer(Device);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _course = CourseLoader.Load(iso, courseTime, _renderer);
        Console.WriteLine($"[Touge] {courseTime} geladen in {sw.ElapsedMilliseconds} ms, {_course.World.Batches.Count} Batches");
        _carRenderer = new CarRenderer(_renderer);
        _car = CarModel.Load(iso, "AE86T", 0, _renderer);
        JumpToLine(startPoint);
        ParkCar(startPoint + 1);
        if (orbit is { } deg) OrbitCar(deg * MathF.PI / 180);
        if (shotPath != null) (_capture, _shotState) = (new FrameCapture(Device, 1280, 720), 1);
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

    /// <summary>Car on driving-line point <paramref name="i"/>, facing the next point, wheel centres at line height + radius.</summary>
    private void ParkCar(int i)
    {
        var line = _course.DrivingLine;
        var a = line[i % line.Length];
        var fwd = Vector3.Normalize(line[(i + 1) % line.Length] - a);
        var left = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, fwd));
        var up = Vector3.Cross(fwd, left);
        _carBody = new Matrix4x4(
            left.X, left.Y, left.Z, 0,
            up.X, up.Y, up.Z, 0,
            fwd.X, fwd.Y, fwd.Z, 0,
            a.X + up.X * _car.WheelRadius, a.Y + up.Y * _car.WheelRadius, a.Z + up.Z * _car.WheelRadius, 1);
        for (var w = 0; w < 4; w++) _carWheels[w] = _car.Wheels[w] * _carBody;
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

        _fpsFrames++;
        if (time.TotalTime - _fpsTime >= 1)
        {
            Console.Write($"\r[Touge] {_fpsFrames} fps  pos {_pos.X:F0} {_pos.Y:F0} {_pos.Z:F0}   ");
            (_fpsTime, _fpsFrames) = (time.TotalTime, 0);
        }
    }

    private Vector3 Forward() => new(MathF.Sin(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), MathF.Cos(_yaw) * MathF.Cos(_pitch));

    public override void Render(in FrameContext ctx)
    {
        var shot = _shotState == 1 ? _capture : null;
        var aspect = shot != null ? (float)shot.Width / shot.Height
            : ctx.Viewport.Height > 0 ? (float)ctx.Viewport.Width / ctx.Viewport.Height : 16f / 9f;
        // Game data is right-handed (y up). Vulkan clip space is Y-down, Metal/GL Y-up.
        var proj = WorldRenderer.Perspective(MathF.PI / 3f, aspect, 0.3f, Device.Backend == Penelope.BackendKind.Vulkan);
        var view = Matrix4x4.CreateLookAt(_pos, _pos + Forward(), Vector3.UnitY);

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
        _course.World.Dispose();
        _course.Sky.Dispose();
        _car.Dispose();
        _carRenderer.Dispose();
        _capture?.Dispose();
        _renderer.Dispose();
    }
}
