using System.Numerics;
using Touge.Formats;

namespace Touge.Tests;

public class CourseLampsTests
{
    /// <summary>
    ///     AKINA at night (with the ISO): its 8 lit lamps and the 8 the original leaves dark, all beside the road up high,
    ///     none at CRS_LIGHT's broken 3rd point; IROHA keeps its CRS_LIGHT lamps.
    /// </summary>
    [Fact]
    public void NightCoursesGetEveryStandingLamp()
    {
        var path = Environment.GetEnvironmentVariable("INITIALD_ISO") ?? "";
        if (!File.Exists(path)) return;
        using var iso = new Iso9660(path);
        var models = Afs.FromBytes(iso.ReadFile("CDVD/DATA/MODEL/COURSE.AFS"), iso.ReadFile("CDVD/DATA/MODEL/COURSE.TBL"));
        var data = Afs.FromBytes(iso.ReadFile("CDVD/DATA/COURSE/CRS_DATA.AFS"), iso.ReadFile("CDVD/DATA/COURSE/CRS_DATA.TBL"));
        Vector3[] Lamps(string course)
        {
            var meshes = CourseLoader.Meshes(models.Read(models.Find(course + "_NIT.PAC")!.Value), false);
            var road = CourseRoad.Read(data.Read(data.Find($"CRS_ROAD_{course}.BIN")!.Value));
            return CourseLamps.Find(meshes, road, CourseRoad.ReadLights(data.Read(data.Find($"CRS_LIGHT_{course}.BIN")!.Value)), true);
        }
        var akina = Lamps("AKINA");
        Assert.Equal(16, akina.Length);
        Assert.DoesNotContain(akina, l => Vector3.Distance(l, new Vector3(166.7f, 1137.7f, -276.2f)) < 1);
        Assert.Contains(akina, l => Vector3.Distance(l, new Vector3(166.6f, 1137.3f, -298.0f)) < 1.5f);
        Assert.InRange(Lamps("IROHA").Length, 30, 33);
    }
}
