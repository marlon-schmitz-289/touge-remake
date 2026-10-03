using System.Numerics;
using System.Runtime.InteropServices;
using Penelope;

namespace Kansei.Graphics;

[StructLayout(LayoutKind.Sequential)]
public struct WorldVertex(Vector3 position, Vector2 uv, Vector4 color, Vector3 normal)
{
    public Vector3 Position = position;
    public Vector2 Uv = uv;
    public Vector4 Color = color;
    public Vector3 Normal = normal;

    public const int Size = 48;

    public static readonly VertexLayout Layout = VertexLayout.Interleaved(Size,
        new VertexAttribute(0, VertexFormat.Float3, 0),
        new VertexAttribute(1, VertexFormat.Float2, 12),
        new VertexAttribute(2, VertexFormat.Float4, 20),
        new VertexAttribute(3, VertexFormat.Float3, 36));
}

/// <summary>Lit vertex (cars): <see cref="Color"/> rgb = material colour, a = kind (0 matte, 0.5 glass, 1 paint, 2 rear lamp).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CarVertex(Vector3 position, Vector3 normal, Vector2 uv, Vector4 color)
{
    public Vector3 Position = position;
    public Vector3 Normal = normal;
    public Vector2 Uv = uv;
    public Vector4 Color = color;

    public const int Size = 48;

    public static readonly VertexLayout Layout = VertexLayout.Interleaved(Size,
        new VertexAttribute(0, VertexFormat.Float3, 0),
        new VertexAttribute(1, VertexFormat.Float3, 12),
        new VertexAttribute(2, VertexFormat.Float2, 24),
        new VertexAttribute(3, VertexFormat.Float4, 32));
}

/// <summary>One draw range of a <see cref="StaticMesh"/>, sharing a texture.</summary>
public readonly record struct MeshBatch(int Texture, int FirstIndex, int IndexCount);

/// <summary>Immutable GPU vertex (<see cref="WorldVertex"/> or <see cref="CarVertex"/>) + index buffer (uint32 indices) split into per-texture batches.</summary>
public sealed class StaticMesh : IDisposable
{
    private readonly IPenelopeDevice _device;
    public BufferHandle Vertices { get; }
    public BufferHandle Indices { get; }
    public IReadOnlyList<MeshBatch> Batches { get; }

    public StaticMesh(IPenelopeDevice device, ReadOnlySpan<WorldVertex> vertices, ReadOnlySpan<uint> indices, IReadOnlyList<MeshBatch> batches)
        : this(device, MemoryMarshal.AsBytes(vertices), indices, batches) { }

    public StaticMesh(IPenelopeDevice device, ReadOnlySpan<CarVertex> vertices, ReadOnlySpan<uint> indices, IReadOnlyList<MeshBatch> batches)
        : this(device, MemoryMarshal.AsBytes(vertices), indices, batches) { }

    private StaticMesh(IPenelopeDevice device, ReadOnlySpan<byte> vertices, ReadOnlySpan<uint> indices, IReadOnlyList<MeshBatch> batches)
    {
        _device = device;
        Vertices = device.CreateBuffer(new BufferDesc(vertices.Length, BufferUsage.Vertex, BufferAccess.Immutable, "mesh-vbo"), vertices);
        Indices = device.CreateBuffer(new BufferDesc(indices.Length * sizeof(uint), BufferUsage.Index, BufferAccess.Immutable, "mesh-ibo"),
            MemoryMarshal.AsBytes(indices));
        Batches = batches;
    }

    public void Dispose()
    {
        _device.DestroyBuffer(Vertices);
        _device.DestroyBuffer(Indices);
    }
}
