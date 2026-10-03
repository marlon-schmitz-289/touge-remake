using System.Globalization;
using System.Numerics;

namespace Touge.Formats;

/// <summary>Wavefront OBJ/MTL writer for debug export to Blender.</summary>
public sealed class Obj : IDisposable
{
    private readonly StreamWriter _obj, _mtl;
    private readonly HashSet<string> _mats = [];
    private int _v;

    public Obj(string objPath)
    {
        _obj = new StreamWriter(objPath);
        _mtl = new StreamWriter(Path.ChangeExtension(objPath, ".mtl"));
        _obj.WriteLine($"mtllib {Path.GetFileName(Path.ChangeExtension(objPath, ".mtl"))}");
    }

    public void Add(string name, Mesh cmd, Matrix4x4 transform)
    {
        _obj.WriteLine($"o {name}");
        var nt = Matrix4x4.Invert(transform, out var inv) ? Matrix4x4.Transpose(inv) : transform;
        foreach (var m in cmd.Materials)
        {
            var tex = m.Texture >= 0 && m.Texture < cmd.Textures.Length ? cmd.Textures[m.Texture] : null;
            var mat = $"{tex ?? "none"}_{m.Rgba:X8}";
            if (_mats.Add(mat))
            {
                var (r, g, b, a) = (m.Rgba & 0xFF, (m.Rgba >> 8) & 0xFF, (m.Rgba >> 16) & 0xFF, (m.Rgba >> 24) & 0xFF);
                _mtl.WriteLine($"newmtl {mat}");
                _mtl.WriteLine(F($"Kd {r / 128f} {g / 128f} {b / 128f}"));
                _mtl.WriteLine(F($"d {Math.Min(1f, a / 128f)}"));
                if (tex != null) _mtl.WriteLine($"map_Kd {tex}.png");
            }
            _obj.WriteLine($"usemtl {mat}");
            foreach (var v in m.Triangles)
            {
                var p = Vector3.Transform(v.Position, transform);
                var n = Vector3.Normalize(Vector3.TransformNormal(v.Normal, nt));
                _obj.WriteLine(F($"v {p.X} {p.Y} {p.Z} {v.Color.X} {v.Color.Y} {v.Color.Z}"));
                _obj.WriteLine(F($"vn {n.X} {n.Y} {n.Z}"));
                _obj.WriteLine(F($"vt {v.Uv.X} {1 - v.Uv.Y}"));
            }
            for (var i = 0; i < m.Triangles.Count; i += 3, _v += 3)
                _obj.WriteLine($"f {_v + 1}/{_v + 1}/{_v + 1} {_v + 2}/{_v + 2}/{_v + 2} {_v + 3}/{_v + 3}/{_v + 3}");
        }
    }

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _obj.Dispose();
        _mtl.Dispose();
    }
}
