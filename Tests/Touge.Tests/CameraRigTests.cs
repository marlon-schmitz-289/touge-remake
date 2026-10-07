using System.Numerics;
using Touge.Formats;
using Touge.Ui;

namespace Touge.Tests;

public class CameraRigTests
{
    [Fact]
    public void C_cycles_every_view_and_wraps()
    {
        var seen = new List<CameraView>();
        var v = CameraView.Chase;
        for (var i = 0; i < CameraRig.Order.Length; i++, v = CameraRig.Next(v)) seen.Add(v);
        Assert.Equal([CameraView.Chase, CameraView.Far, CameraView.Hood, CameraView.Cockpit, CameraView.Bumper], seen);
        Assert.Equal(CameraView.Chase, v);
        Assert.Equal(["CHASE", "FAR CHASE", "HOOD", "COCKPIT", "BUMPER"], CameraRig.Order.Select(CameraRig.Name));
        // the replay viewer: TV, the five driving views, free
        Assert.Equal([null, CameraView.Chase, CameraView.Far, CameraView.Hood, CameraView.Cockpit, CameraView.Bumper, null],
            Enum.GetValues<ReplayViewer.Camera>().Select(ReplayViewer.Driving));
    }

    [Theory]
    [InlineData("\"BumperCam\": true", CameraView.Bumper)]
    [InlineData("\"BumperCam\": false", CameraView.Chase)]
    [InlineData("\"Camera\": \"Cockpit\"", CameraView.Cockpit)]
    [InlineData("\"BumperCam\": true, \"Camera\": \"Hood\"", CameraView.Hood)]
    [InlineData("\"Camera\": \"Sideways\"", CameraView.Chase)]
    [InlineData("\"Version\": 2, \"BumperCam\": true", CameraView.Bumper)]
    public void Old_and_new_files_give_the_start_camera(string fields, CameraView expected)
    {
        var s = Settings.FromJson("{ " + fields + " }");
        Assert.Equal(expected, s.Camera);
        Assert.DoesNotContain("BumperCam", s.ToJson());
        Assert.Equal(expected, Settings.FromJson(s.ToJson()).Camera);
    }

    /// <summary>A car shaped like HCAR's: bonnet low in front, cabin up to a roof, a raked screen in the front half, headlamp glass low at the nose.</summary>
    private static (Mesh Body, Mesh Wind) BoxCar()
    {
        static Mesh.Vertex V(float x, float y, float z) => new(new Vector3(x, y, z), Vector3.UnitY, default, default);
        static Mesh M(params Mesh.Vertex[] t) => new() { Textures = [], Nodes = [], Materials = [new Mesh.Material(-1, 0, 0, [.. t])] };
        var body = M(V(-0.85f, -0.1f, -2.1f), V(0.85f, -0.1f, 2.1f), V(0.85f, 0.6f, 2.0f), // corners of the box
            V(-0.7f, 0.62f, 1.3f), V(0.7f, 0.62f, 1.3f), V(0, 0.6f, 1.6f), // bonnet
            V(-0.7f, 1.05f, -0.2f), V(0.7f, 1.05f, -0.2f), V(0, 1.05f, -1.2f)); // roof
        var wind = M(V(-0.7f, 0.65f, 0.95f), V(0.7f, 0.65f, 0.95f), V(0, 1.0f, 0.15f), // screen, facing forward and up
            V(0.6f, 0.3f, 2.0f), V(0.4f, 0.45f, 2.05f), V(0.6f, 0.45f, 2.05f)); // headlamp cover (EK9)
        return (body, wind);
    }

    [Fact]
    public void Mounts_sit_inside_the_car_on_the_drivers_side()
    {
        var (body, wind) = BoxCar();
        var m = CameraRig.Measure([body], wind, 0);
        Assert.InRange(m.Hood.Z, 0.95f, 1.2f); // just ahead of the screen base, not at the headlamp
        Assert.InRange(m.Hood.Y, 0.7f, 0.8f); // 10 cm over the bonnet
        Assert.True(m.Eye.X < -0.2f); // right-hand drive
        Assert.InRange(m.Eye.Y, 0.6f, 0.95f); // under the roof
        Assert.InRange(m.Eye.Z, -0.6f, 0.0f); // behind the screen
        Assert.True(Inside(m.Eye, m) && Inside(m.Hood, m));
        Assert.True(m.Nose.Z > m.Max.Z);
    }

    private static bool Inside(Vector3 p, in CameraRig.Mounts m) =>
        p.X > m.Min.X && p.X < m.Max.X && p.Y > m.Min.Y && p.Y < m.Max.Y && p.Z > m.Min.Z && p.Z < m.Max.Z;

    [Fact]
    public void On_board_and_chase_views_ride_with_the_body()
    {
        var (body, wind) = BoxCar();
        var m = CameraRig.Measure([body], wind, 0);
        var pose = Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateTranslation(100, 5, -40);
        var model = Matrix4x4.CreateTranslation(0, -0.07f, 0.1f) * pose;
        foreach (var view in new[] { CameraView.Hood, CameraView.Cockpit })
        {
            var f = new ChaseCamera();
            var (pos, look, _) = CameraRig.Place(view, ref f, false, 1 / 60f, pose, model, Vector3.Zero, m, 1);
            Assert.True(Vector3.Distance(pos, Vector3.Transform(view == CameraView.Hood ? m.Hood : m.Eye, model)) < 1e-4f);
            Assert.True(Vector3.Dot(Vector3.Normalize(look - pos), Vector3.TransformNormal(Vector3.UnitZ, pose)) > 0.99f);
            Assert.Equal(0.05f, CameraRig.Near(view));
        }
        Assert.Equal(0.3f, CameraRig.Near(CameraView.Chase));
        // snapped: far chase stands further back and higher than chase, same aim and fov
        ChaseCamera c = default, fa = default;
        var (chase, look1, fov) = CameraRig.Place(CameraView.Chase, ref c, true, 0, pose, model, Vector3.Zero, m, 1);
        var (far, look2, farFov) = CameraRig.Place(CameraView.Far, ref fa, true, 0, pose, model, Vector3.Zero, m, 1);
        Assert.True(Vector3.Distance(far, model.Translation) > Vector3.Distance(chase, model.Translation) + 2 && far.Y > chase.Y + 0.7f);
        Assert.True(Vector3.Distance(look1, look2) < 1e-4f && fov == 1 && farFov == 1);
    }

    [Fact]
    public void Driver_card_goes_from_the_cabin_only()
    {
        static Mesh.Material Card(float x, bool lengthwise, int texture = 0) => new(texture, 0x2000, 0,
            lengthwise
                ? [new(new(x, 0.3f, -0.4f), default, default, default), new(new(x, 0.9f, -0.4f), default, default, default), new(new(x, 0.9f, 0.2f), default, default, default)]
                : [new(new(x - 0.2f, 0.3f, 0.5f), default, default, default), new(new(x + 0.2f, 0.3f, 0.5f), default, default, default), new(new(x, 0.6f, 0.5f), default, default, default)]);
        var interior = new Mesh
        {
            Textures = ["seat", "driver", "wheel"], Nodes = [],
            Materials = [Card(-0.32f, true, 1), Card(0.32f, true), Card(-0.3f, false, 2), Card(-0.32f, true, -1)],
        };
        Assert.Equal([false, true, true, true], interior.Materials.Select(m => CameraRig.WithoutDriver(interior).Materials.Contains(m)));
    }

    /// <summary>Every HCAR car (when the ISO is at hand): eye and hood inside the body, eye in the cabin on the right, hood ahead of it.</summary>
    [Fact]
    public void Every_car_from_the_disc_gets_mounts_inside_its_body()
    {
        var path = Environment.GetEnvironmentVariable("INITIALD_ISO") ?? "Initial D - Special Stage (Japan) (v2.00).iso";
        if (!File.Exists(path)) return;
        using var iso = new Iso9660(path);
        var afs = iso.OpenAfs("CDVD/DATA/MODEL/HCAR.AFS");
        foreach (var car in CarPaint.Cars)
        {
            var pac = afs.Read(afs.Find(car + ".PAC")!.Value);
            var parts = Pac.Entries(pac).Where(e => e.Type == 3 && Mesh.IsCmd(pac.AsSpan(e.Offset, e.Size)))
                .ToDictionary(e => e.Name[(car.Length + 1)..], e => Mesh.Parse(pac.AsSpan(e.Offset, e.Size)));
            var day = CarParts.Body(car, parts, Livery.Rival, 0);
            var m = CameraRig.Measure(day.Select(p => p.Mesh), day.First(p => p.Name.StartsWith("wind")).Mesh, 0);
            Assert.True(Inside(m.Eye, m) && Inside(m.Hood, m), car);
            Assert.True(m.Eye.X < -0.25f && m.Eye.Y > 0.6f && m.Eye.Y < m.Max.Y - 0.08f && m.Hood.Z > m.Eye.Z + 0.6f, $"{car}: {m}");
            Assert.Single(parts["other00"].Materials, CameraRig.IsDriver);
        }
    }
}
