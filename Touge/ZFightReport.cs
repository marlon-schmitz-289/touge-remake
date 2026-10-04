using System.Globalization;
using System.Numerics;
using Touge.Formats;

namespace Touge;

/// <summary>
///     <c>--zfight</c>: <see cref="ZFight.Find"/> over the geometry exactly as the game draws it — every course variant in
///     COURSE.AFS (world after <see cref="CourseLoader.Flatten"/> dedup, and the sky) and every car in HCAR.AFS and
///     CAR.AFS (default body in <see cref="CarModel.Flatten"/> order, wheel). Per asset: pairs, overlap area, gap
///     classes, layered triangles, and the largest (batch, batch) groups with a location.
/// </summary>
public static class ZFightReport
{
    /// <summary>Gap below which the groups are listed: within the depth noise of float world coordinates (~0.1 mm) plus margin.</summary>
    private const float Critical = 0.001f;

    public static void Run(Iso9660 iso, string? filter)
    {
        var models = Afs.FromBytes(iso.ReadFile("CDVD/DATA/MODEL/COURSE.AFS"), iso.ReadFile("CDVD/DATA/MODEL/COURSE.TBL"));
        foreach (var e in models.Entries.Where(e => !e.Name.StartsWith("ENV_") && e.Name.Contains('_')))
        {
            var name = Path.GetFileNameWithoutExtension(e.Name);
            if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var pac = models.Read(e);
            foreach (var sky in new[] { false, true })
            {
                var (corners, batches) = CourseLoader.Flatten(CourseLoader.Meshes(pac, sky));
                Print(sky ? name + " sky" : name, corners.Select(v => v.Position).ToArray(), batches.Select(b => (b.First, b.Label)).ToList());
            }
        }

        foreach (var archive in new[] { "HCAR", "CAR" })
        {
            var afs = Afs.FromBytes(iso.ReadFile($"CDVD/DATA/MODEL/{archive}.AFS"), iso.ReadFile($"CDVD/DATA/MODEL/{archive}.TBL"));
            foreach (var e in afs.Entries.Where(e => e.Name.EndsWith(".PAC")))
            {
                var car = Path.GetFileNameWithoutExtension(e.Name);
                if (filter != null && !car.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                var pac = afs.Read(e);
                var parts = Pac.Entries(pac).Where(p => p.Type == 3 && p.Name.Length > car.Length + 1 && Mesh.IsCmd(pac.AsSpan(p.Offset, p.Size)))
                    .DistinctBy(p => p.Name[(car.Length + 1)..]).ToDictionary(p => p.Name[(car.Length + 1)..], p => Mesh.Parse(pac.AsSpan(p.Offset, p.Size)));
                if (!parts.ContainsKey("body00")) continue;
                PrintCar($"{archive}/{car} body", parts.Where(p => CarParts.IsDefaultBody(p.Key)).Select(p => (p.Key, p.Value)));
                if (parts.TryGetValue("tire00FL", out var tire) && parts.TryGetValue("Bdisk00", out var disk))
                    PrintCar($"{archive}/{car} wheel", [("tire00FL", tire), ("Bdisk00", disk)]);
            }
        }
    }

    private static void PrintCar(string title, IEnumerable<(string, Mesh)> meshes)
    {
        var corners = new List<Vector3>();
        var batches = new List<(int, string)>();
        foreach (var (part, mesh, m) in CarModel.Flatten(meshes))
        {
            if (m.Triangles.Count == 0) continue;
            batches.Add((corners.Count / 3, $"{part}/f{m.Flags:X}:{(m.Texture >= 0 && m.Texture < mesh.Textures.Length ? mesh.Textures[m.Texture] : "-")}"));
            corners.AddRange(m.Triangles.Select(v => v.Position));
        }
        Print(title, corners.ToArray(), batches);
    }

    private static void Print(string title, Vector3[] corners, List<(int First, string Label)> batches)
    {
        var pairs = ZFight.Find(corners);
        var layers = ZFight.Layers(corners.Length / 3, pairs);
        int Batch(int tri)
        {
            var i = batches.BinarySearch((tri, "￿"), Comparer<(int First, string)>.Create((x, y) => x.First.CompareTo(y.First)));
            return i >= 0 ? i : ~i - 1;
        }
        var layered = layers.Count(l => l > 0);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{title}: {corners.Length / 3} Dreiecke, {pairs.Count} Paare, {pairs.Sum(p => p.Area):F2} m², Abstand <1 mm {pairs.Count(p => p.Gap < 0.001f)}, " +
            $"<5 mm {pairs.Count(p => p.Gap < 0.005f)}, <3 cm {pairs.Count}; Ebenen>0: {layered} Dreiecke (max {(layers.Length > 0 ? layers.Max() : 0)})"));
        foreach (var g in pairs.Where(p => p.Gap < Critical).GroupBy(p => (Batch(p.A), Batch(p.B))).OrderByDescending(g => g.Sum(p => p.Area)).Take(8))
        {
            var worst = g.MaxBy(p => p.Area);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    {batches[g.Key.Item2].Label} über {batches[g.Key.Item1].Label}: {g.Count()} Paare, {g.Sum(p => p.Area):F3} m², " +
                $"Abstand max {g.Max(p => p.Gap) * 1000:F1} mm, bei ({worst.At.X:F2}, {worst.At.Y:F2}, {worst.At.Z:F2})"));
        }
    }
}
