using System.Numerics;
using System.Runtime.InteropServices;
using Penelope;

namespace Kansei.Graphics;

/// <summary>
///     Frame pipeline + forward renderer for prelit static geometry. A frame is <see cref="BeginScene"/> (HDR RGBA16F
///     target, 4× MSAA in <see cref="HighQuality"/>, analytic sky from <see cref="Atmosphere"/>), draws, then
///     <see cref="EndScene"/> (resolve → bloom → ACES tonemap/grade into the swapchain or a <see cref="FrameCapture"/>).
///     World: sRGB texture (mipmapped, trilinear + anisotropic) × vertex colour, alpha test (alpha-to-coverage with
///     MSAA), distance fog in the horizon colour. No backface culling (the PS2 draws foliage cards from both sides);
///     exact duplicates must be removed by the caller. Reversed-Z (use <see cref="Perspective"/>) with GreaterEqual:
///     overlay layers a few cm apart stay stable at distance, exactly coplanar layers resolve by draw order (later wins).
/// </summary>
public sealed class WorldRenderer : IDisposable
{
    private const int PushBytes = 96; // mvp 64 + fog 16 + eye 16
    private const int SkyPushBytes = 128;
    private const float AlphaCutoff = 0.3f; // world.frag
    private static readonly TextureFormat DepthFormat = TextureFormat.Depth32Float;

    private readonly IPenelopeDevice _device;
    private readonly ShaderHandle _shader, _skyShader;
    private readonly BindGroupLayoutHandle _layout;
    // index 0: 1 sample, 1: 4× MSAA
    private readonly RenderPipelineHandle[] _pipeline = new RenderPipelineHandle[2], _skyMesh = new RenderPipelineHandle[2], _sky = new RenderPipelineHandle[2];
    private readonly SamplerHandle _sampler;
    private readonly PostProcess _post;
    private readonly List<(TextureHandle Tex, TextureViewHandle View, BindGroupHandle Group)> _textures = [];
    private TextureHandle _depth, _msaa;
    private TextureViewHandle _depthView, _msaaView;
    private int _w, _h, _samples;

    public IPenelopeDevice Device => _device;
    internal BindGroupLayoutHandle TextureLayout => _layout;
    internal static TextureFormat Depth => DepthFormat;
    internal BindGroupHandle TextureGroup(int texture) => _textures[texture].Group;
    internal int Quality => HighQuality ? 1 : 0;
    internal static int Samples(int quality) => quality == 1 ? 4 : 1;

    public Atmosphere Atmosphere { get; set; } = new();
    /// <summary>4× MSAA (+ alpha-to-coverage) and bloom; off = 1 sample, no bloom (tonemapping stays).</summary>
    public bool HighQuality = true;

    public WorldRenderer(IPenelopeDevice device)
    {
        _device = device;
        _shader = device.CreateShader(ShaderLoader.LoadGraphics(typeof(WorldRenderer).Assembly, "world", "world", "world"));
        _skyShader = device.CreateShader(ShaderLoader.LoadGraphics(typeof(WorldRenderer).Assembly, "fullscreen", "sky", "sky"));
        _layout = device.GetBindGroupLayout(new BindGroupLayoutDesc(
            [new BindGroupLayoutEntry(0, BindingType.CombinedImageSampler, ShaderStage.Fragment)], "world-tex"));
        _sampler = device.GetSampler(SamplerDesc.LinearWrap with { MaxAnisotropy = 8 });
        _post = new PostProcess(device);
        for (var q = 0; q < 2; q++)
        {
            var ms = MultisampleState.Disabled with { SampleCount = Samples(q), AlphaToCoverageEnabled = q == 1 };
            _pipeline[q] = ScenePipeline(_shader, WorldVertex.Layout, ms, true, PushBytes, "world");
            _skyMesh[q] = ScenePipeline(_shader, WorldVertex.Layout, ms with { AlphaToCoverageEnabled = false }, false, PushBytes, "sky-mesh");
            _sky[q] = PostProcess.Fullscreen(device, _skyShader, PostProcess.HdrFormat, BlendState.Opaque, null, SkyPushBytes, Samples(q), DepthFormat);
        }
    }

    /// <summary>Pipeline into the HDR scene pass: no culling, reversed-Z GreaterEqual (or no depth at all for the sky).</summary>
    internal RenderPipelineHandle ScenePipeline(ShaderHandle shader, VertexLayout layout, MultisampleState ms, bool depth, int pushBytes, string name) =>
        _device.CreateRenderPipeline(new RenderPipelineDesc(
            shader, layout, PrimitiveTopology.TriangleList, RasterizerState.Default,
            depth ? DepthStencilState.DepthLessWrite with { DepthCompare = CompareFunc.GreaterEqual } : DepthStencilState.Disabled, ms,
            [new ColorTargetState(PostProcess.HdrFormat, BlendState.Opaque)], DepthFormat,
            [_layout], [new PushConstantRange(ShaderStage.Vertex | ShaderStage.Fragment, 0, pushBytes)], name));

    /// <summary>
    ///     Right-handed, infinite far plane, reversed-Z (depth 1 at <paramref name="near"/>, 0 at infinity, range 0..1).
    ///     <paramref name="flipY"/> for Y-down clip space (Vulkan).
    /// </summary>
    public static Matrix4x4 Perspective(float fovY, float aspect, float near, bool flipY)
    {
        var f = 1f / MathF.Tan(fovY / 2);
        return new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, flipY ? -f : f, 0, 0,
            0, 0, 0, -1,
            0, 0, near, 0);
    }

    /// <summary>
    ///     Uploads an sRGB RGBA8 texture with a full mip chain (<see cref="Mipmaps"/>, coverage kept for
    ///     <paramref name="alphaCutoff"/>), returns its index for <see cref="MeshBatch.Texture"/>.
    /// </summary>
    public int AddTexture(int width, int height, ReadOnlySpan<byte> rgba, string? name = null, float alphaCutoff = AlphaCutoff)
    {
        var mips = Mipmaps.Build(width, height, rgba, alphaCutoff);
        var tex = _device.CreateTexture(TextureDesc.Sampled2D(width, height, TextureFormat.Rgba8UnormSrgb, mips.Count + 1, name), rgba);
        for (var i = 0; i < mips.Count; i++)
            _device.WriteTexture(tex, i + 1, 0, 0, 0, 0, mips[i].W, mips[i].H, 1, mips[i].Rgba, mips[i].W * 4, mips[i].H);
        var view = _device.DefaultTextureView(tex);
        var group = _device.CreateBindGroup(new BindGroupDesc(_layout, [BindGroupEntry.CombinedImageSampler(0, view, _sampler)], name));
        _textures.Add((tex, view, group));
        return _textures.Count - 1;
    }

    /// <summary>
    ///     Opens the HDR scene pass sized like the swapchain (or <paramref name="target"/>) and fills it with the analytic
    ///     sky seen through <paramref name="view"/>/<paramref name="proj"/>. Close with <see cref="EndScene"/>.
    /// </summary>
    public IRenderPassEncoder BeginScene(ICommandEncoder encoder, in Matrix4x4 view, in Matrix4x4 proj, FrameCapture? target = null)
    {
        EnsureTargets(target?.Width ?? _device.SwapchainWidth, target?.Height ?? _device.SwapchainHeight, Samples(Quality));
        var color = _samples > 1
            ? new ColorAttachment(_msaaView, LoadOp.DontCare, StoreOp.DontCare, ClearColor.Black, _post.SceneView)
            : new ColorAttachment(_post.SceneView, LoadOp.DontCare, StoreOp.Store, ClearColor.Black);
        var pass = encoder.BeginRenderPass(new RenderPassDesc([color],
            new DepthStencilAttachment(_depthView, LoadOp.Clear, StoreOp.DontCare, 0f, LoadOp.Load, StoreOp.DontCare, 0, false, false),
            DebugName: "scene"));
        pass.SetViewport(0, 0, _w, _h);
        pass.SetScissor(0, 0, _w, _h);

        var a = Atmosphere;
        Matrix4x4.Invert(view with { M41 = 0, M42 = 0, M43 = 0 } * proj, out var inv);
        Span<byte> push = stackalloc byte[SkyPushBytes];
        MemoryMarshal.Write(push, in inv);
        MemoryMarshal.Write(push[64..], new Vector4(a.Zenith, 1f / _w));
        MemoryMarshal.Write(push[80..], new Vector4(a.Horizon, 1f / _h));
        MemoryMarshal.Write(push[96..], new Vector4(Vector3.Normalize(a.SunDirection), 0.99995f));
        // gl_FragCoord.y runs down on Metal/Vulkan; Vulkan's projection is Y-flipped already, Metal's is not
        MemoryMarshal.Write(push[112..], new Vector4(a.SunDisk, _device.Backend == BackendKind.Metal ? -1 : 1));
        pass.SetPipeline(_sky[Quality]);
        pass.SetPushConstants(ShaderStage.Fragment, 0, push);
        pass.Draw(3);
        return pass;
    }

    /// <summary>Ends the scene pass and runs bloom + tonemapping into the swapchain or <paramref name="target"/>.</summary>
    public void EndScene(ICommandEncoder encoder, IRenderPassEncoder pass, FrameCapture? target = null)
    {
        pass.Dispose();
        _post.Run(encoder, Atmosphere, HighQuality, target?.View ?? _device.CurrentSwapchainView, _w, _h);
    }

    /// <summary>The course sky mesh: no fog, no depth (painted over the analytic sky in draw order), vertex alpha ignored.</summary>
    public void DrawSky(IRenderPassEncoder pass, StaticMesh mesh, in Matrix4x4 viewProj) =>
        DrawMesh(pass, _skyMesh[Quality], mesh, viewProj, Vector3.Zero, true);

    public void Draw(IRenderPassEncoder pass, StaticMesh mesh, in Matrix4x4 viewProj, Vector3 eye) =>
        DrawMesh(pass, _pipeline[Quality], mesh, viewProj, eye, false);

    private void DrawMesh(IRenderPassEncoder pass, RenderPipelineHandle pipeline, StaticMesh mesh, in Matrix4x4 viewProj, Vector3 eye, bool sky)
    {
        pass.SetPipeline(pipeline);
        Span<byte> push = stackalloc byte[PushBytes];
        MemoryMarshal.Write(push, in viewProj);
        MemoryMarshal.Write(push[64..], new Vector4(Atmosphere.Horizon, sky ? 0 : 1f / Atmosphere.FogDistance));
        MemoryMarshal.Write(push[80..], new Vector4(eye, sky ? 1 : 0));
        pass.SetPushConstants(ShaderStage.Vertex | ShaderStage.Fragment, 0, push);
        pass.SetVertexBuffer(0, mesh.Vertices);
        pass.SetIndexBuffer(mesh.Indices, IndexType.UInt32);
        foreach (var b in mesh.Batches)
        {
            pass.SetBindGroup(0, _textures[b.Texture].Group);
            pass.DrawIndexed(b.IndexCount, 1, b.FirstIndex);
        }
    }

    private void EnsureTargets(int w, int h, int samples)
    {
        _post.Resize(w, h);
        if (w == _w && h == _h && samples == _samples) return;
        ReleaseTargets();
        _depth = _device.CreateTexture(TextureDesc.DepthAttachment(w, h, DepthFormat, "scene-depth") with { SampleCount = samples });
        _depthView = _device.DefaultTextureView(_depth);
        if (samples > 1)
        {
            _msaa = _device.CreateTexture(new TextureDesc(w, h, PostProcess.HdrFormat, TextureUsage.ColorAttachment, SampleCount: samples, DebugName: "scene-msaa"));
            _msaaView = _device.DefaultTextureView(_msaa);
        }
        (_w, _h, _samples) = (w, h, samples);
    }

    private void ReleaseTargets()
    {
        if (!_depthView.IsNull) _device.DestroyTextureView(_depthView);
        if (!_depth.IsNull) _device.DestroyTexture(_depth);
        if (!_msaaView.IsNull) _device.DestroyTextureView(_msaaView);
        if (!_msaa.IsNull) _device.DestroyTexture(_msaa);
        (_depth, _depthView, _msaa, _msaaView) = (default, default, default, default);
    }

    public void Dispose()
    {
        foreach (var (tex, view, group) in _textures)
        {
            _device.DestroyBindGroup(group);
            _device.DestroyTextureView(view);
            _device.DestroyTexture(tex);
        }
        ReleaseTargets();
        _post.Dispose();
        foreach (var p in _pipeline.Concat(_skyMesh).Concat(_sky)) _device.DestroyRenderPipeline(p);
        _device.DestroyShader(_shader);
        _device.DestroyShader(_skyShader);
    }
}
