using System.Numerics;
using System.Runtime.InteropServices;
using Penelope;

namespace Kansei.Graphics;

/// <summary>
///     Frame pipeline + forward renderer for prelit static geometry. A frame is <see cref="BeginScene"/> (HDR RGBA16F
///     target, 4× MSAA in <see cref="HighQuality"/>, analytic sky from <see cref="Atmosphere"/>), draws, then
///     <see cref="EndScene"/> (resolve → bloom → ACES tonemap/grade into the swapchain or a <see cref="FrameCapture"/>).
///     World: sRGB texture (mipmapped, trilinear + anisotropic) × baked vertex light re-lit by the sun with cascaded
///     shadows (<see cref="RenderShadows"/>, world.frag), + <see cref="Lights"/>, alpha test (alpha-to-coverage with
///     MSAA), per-pixel distance + height fog (fog.glsl). World and car shaders share one per-draw uniform block
///     (scene_push.glsl, <see cref="WritePush"/>, <see cref="SetScene"/>) and bind group 1 (shadow atlas + env maps, <see cref="SetEnvironment"/>). No backface culling (the PS2 draws foliage cards from both sides);
///     exact duplicates must be removed by the caller. Reversed-Z (use <see cref="Perspective"/>) with GreaterEqual:
///     overlay layers a few cm apart stay stable at distance; coplanar ones (&lt; 1 mm, decals, overlapping sections) come
///     as <see cref="MeshBatch.Layer"/> and are pulled <see cref="LayerOffset"/> per layer towards the camera, so the later
///     one wins like on the PS2 instead of fighting over float rounding.
/// </summary>
public sealed class WorldRenderer : IDisposable
{
    internal const int PushBytes = 720; // scene_push.glsl (a uniform slice per draw, SetScene)
    /// <summary>
    ///     Metres an overlay layer (<see cref="MeshBatch.Layer"/>) is pulled towards the camera per layer (world.vert,
    ///     car.vert): above the gap up to which layers are formed (1 mm, Touge.Formats.ZFight.FightGap) and far above the
    ///     depth noise of float world coordinates (~0.1 mm at 1–2 km from the origin), small enough not to show.
    /// </summary>
    public const float LayerOffset = 0.002f;
    private const int LayerPush = 572; // uPointColor.w
    private const int SkyPushBytes = 192;
    private const float AlphaCutoff = 0.3f; // world.frag
    private const TextureFormat DepthFormat = PostProcess.DepthFormat;
    internal static readonly TextureFormat ShadowFormat = TextureFormat.Depth32Float;

    private readonly IPenelopeDevice _device;
    private readonly ShaderHandle _shader, _skyShader;
    private readonly BindGroupLayoutHandle _layout, _sceneLayout;
    private readonly ShadowMap _shadow;
    private readonly Dictionary<(int[], int[]), BindGroupHandle> _sceneGroups = [];
    private BindGroupHandle _sceneGroup;
    private float _envMix;
    private bool _shadowsThisFrame;
    private readonly Vector4[] _points = new Vector4[4];
    private readonly Func<int, BindGroupHandle> _textureGroup;
    // index 0: 1 sample, 1: 4× MSAA
    private readonly RenderPipelineHandle[] _pipeline = new RenderPipelineHandle[2], _skyMesh = new RenderPipelineHandle[2], _sky = new RenderPipelineHandle[2];
    private readonly SamplerHandle _sampler;
    private readonly PostProcess _post;
    private readonly List<(TextureHandle Tex, TextureViewHandle View, BindGroupHandle Group)> _textures = [];
    private TextureHandle _depth, _msaa, _msaaGbuf;
    private TextureViewHandle _depthView, _msaaView, _msaaGbufView;
    private Matrix4x4 _viewRotProj, _proj;
    private int _w, _h, _samples;

    public IPenelopeDevice Device => _device;
    internal BindGroupLayoutHandle TextureLayout => _layout;
    internal static TextureFormat Depth => DepthFormat;
    internal BindGroupHandle TextureGroup(int texture) => _textures[texture].Group;
    internal int Quality => HighQuality ? 1 : 0;
    internal static int Samples(int quality) => quality == 1 ? 4 : 1;

    public Atmosphere Atmosphere { get; set; } = new();
    public SceneLights Lights { get; } = new();
    /// <summary>4× MSAA (+ alpha-to-coverage), bloom and shadows; off = 1 sample, no bloom/shadows (tonemapping stays).</summary>
    public bool HighQuality = true;
    /// <summary>Seconds, animates rain ripples.</summary>
    public float Time;

    public WorldRenderer(IPenelopeDevice device)
    {
        _device = device;
        _shader = device.CreateShader(ShaderLoader.LoadGraphics(typeof(WorldRenderer).Assembly, "world", "world", "world"));
        _skyShader = device.CreateShader(ShaderLoader.LoadGraphics(typeof(WorldRenderer).Assembly, "fullscreen", "sky", "sky"));
        _layout = device.GetBindGroupLayout(new BindGroupLayoutDesc(
            [new BindGroupLayoutEntry(0, BindingType.CombinedImageSampler, ShaderStage.Fragment)], "world-tex"));
        _sampler = device.GetSampler(SamplerDesc.LinearWrap with { MaxAnisotropy = 8 });
        _post = new PostProcess(device);
        _sceneLayout = device.GetBindGroupLayout(new BindGroupLayoutDesc(
            [.. Enumerable.Range(1, 9).Select(b => new BindGroupLayoutEntry(b < 6 ? b : b + 1, BindingType.CombinedImageSampler, ShaderStage.Fragment)),
                PostProcess.UniformEntry(6, ShaderStage.Vertex | ShaderStage.Fragment)], "scene"));
        _shadow = new ShadowMap(device, _layout);
        _textureGroup = TextureGroup;
        var grey = AddTexture(1, 1, [118, 118, 118, 255], "env-grey"); // 18 % linear until a course sets its maps
        int[] greys = [grey, grey, grey, grey];
        SetEnvironment(greys, greys, 0);
        for (var q = 0; q < 2; q++)
        {
            var ms = MultisampleState.Disabled with { SampleCount = Samples(q), AlphaToCoverageEnabled = q == 1 };
            _pipeline[q] = ScenePipeline(_shader, WorldVertex.Layout, ms, true, "world");
            _skyMesh[q] = ScenePipeline(_shader, WorldVertex.Layout, ms with { AlphaToCoverageEnabled = false }, false, "sky-mesh");
            _sky[q] = PostProcess.Fullscreen(device, _skyShader, PostProcess.HdrFormat, BlendState.Opaque, [_layout, _sceneLayout], 0, Samples(q), DepthFormat, true);
        }
    }

    /// <summary>
    ///     Pipeline into the HDR scene pass: no culling, reversed-Z GreaterEqual (or no depth at all for the sky).
    ///     With a <paramref name="blend"/> state it tests depth but does not write it (transparent effects).
    /// </summary>
    internal RenderPipelineHandle ScenePipeline(ShaderHandle shader, VertexLayout layout, MultisampleState ms, bool depth, string name,
        BlendState? blend = null) =>
        _device.CreateRenderPipeline(new RenderPipelineDesc(
            shader, layout, PrimitiveTopology.TriangleList, RasterizerState.Default,
            depth ? DepthStencilState.DepthLessWrite with { DepthCompare = CompareFunc.GreaterEqual, DepthWriteEnabled = blend == null } : DepthStencilState.Disabled, ms,
            [new ColorTargetState(PostProcess.HdrFormat, blend ?? BlendState.Opaque), new ColorTargetState(PostProcess.GbufFormat, blend ?? BlendState.Opaque)], DepthFormat,
            [_layout, _sceneLayout], [], name));

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
    ///     Env maps the cars reflect from now on: two sets of ENV_TOP/BOTTOM/LEFT/RIGHT texture indices, <paramref name="mix"/>
    ///     of <paramref name="b"/> (car.frag fades between them). Bind groups are cached per pair of set arrays (by reference).
    /// </summary>
    public void SetEnvironment(int[] a, int[] b, float mix)
    {
        _envMix = mix;
        if (_sceneGroups.TryGetValue((a, b), out _sceneGroup)) return;
        var sampler = _device.GetSampler(SamplerDesc.Linear);
        _sceneGroup = _device.CreateBindGroup(new BindGroupDesc(_sceneLayout,
        [
            BindGroupEntry.CombinedImageSampler(1, _shadow.View, _shadow.Sampler),
            .. a.Select((t, i) => BindGroupEntry.CombinedImageSampler(2 + i, _textures[t].View, sampler)),
            .. b.Select((t, i) => BindGroupEntry.CombinedImageSampler(7 + i, _textures[t].View, sampler)),
            BindGroupEntry.UniformBuffer(6, _post.UniformBuffer, 0, PostProcess.UniformBytes),
        ], "scene"));
        _sceneGroups[(a, b)] = _sceneGroup;
    }

    /// <summary>
    ///     Sun shadow cascades for this frame around the camera (<paramref name="eye"/>, <paramref name="forward"/>,
    ///     <paramref name="fovY"/>, <paramref name="aspect"/>): <paramref name="world"/> and the car meshes
    ///     (<see cref="CarVertex"/>, model matrices) as casters. Call before <see cref="BeginScene"/>; skipped when
    ///     <see cref="Atmosphere.Shadows"/> or <see cref="HighQuality"/> is off.
    /// </summary>
    public void RenderShadows(ICommandEncoder encoder, Vector3 eye, Vector3 forward, float fovY, float aspect, StaticMesh world,
        ReadOnlySpan<(StaticMesh Mesh, Matrix4x4 Model)> cars)
    {
        _shadowsThisFrame = Atmosphere.Shadows && HighQuality;
        if (!_shadowsThisFrame) return;
        _shadow.Update(eye, forward, fovY, aspect, Vector3.Normalize(Atmosphere.SunDirection), _device.Backend == BackendKind.Vulkan);
        _shadow.Render(encoder, _textureGroup, world, cars);
    }

    /// <summary>
    ///     Fills the shared push block (scene_push.glsl) for one draw. <paramref name="car"/> picks the sun strength
    ///     for lit objects (<see cref="Atmosphere.SunIntensity"/>) instead of the baked-light factor.
    /// </summary>
    internal void WritePush(Span<byte> push, in Matrix4x4 mvp, in Matrix4x4 model, Vector3 eye, bool sky, bool car)
    {
        var a = Atmosphere;
        var l = Lights;
        MemoryMarshal.Write(push, in mvp);
        MemoryMarshal.Write(push[64..], in model);
        MemoryMarshal.Write(push[128..], new Vector4(a.FogColor, a.LightGlow));
        MemoryMarshal.Write(push[144..], new Vector4(eye, sky ? 1 : 0));
        MemoryMarshal.Write(push[160..], new Vector4(Vector3.Normalize(a.SunDirection), car ? a.SunIntensity : a.BakedSun));
        MemoryMarshal.Write(push[176..], new Vector4(a.Ambient, a.BakedKeep));
        MemoryMarshal.Write(push[192..], new Vector4(_shadowsThisFrame ? 1 : 0, a.Wetness, a.EnvStrength, l.Brake));
        MemoryMarshal.Write(push[208..], new Vector4(_shadow.TexelWorld[0], _shadow.TexelWorld[1], _shadow.TexelWorld[2], 1f / ShadowMap.TileSize));
        for (var c = 0; c < ShadowMap.Cascades; c++) MemoryMarshal.Write(push[(224 + c * 64)..], in _shadow.Lookup[c]);
        float tanH = MathF.Tan(l.HeadlightSpreadDegrees * MathF.PI / 180), tanV = MathF.Tan(l.HeadlightSpreadVerticalDegrees * MathF.PI / 180);
        for (var i = 0; i < 2; i++)
        {
            MemoryMarshal.Write(push[(416 + i * 16)..], new Vector4(l.HeadlightPosition[i], tanH));
            MemoryMarshal.Write(push[(448 + i * 16)..], new Vector4(l.HeadlightDirection[i], tanV));
        }
        MemoryMarshal.Write(push[480..], new Vector4(l.HeadlightColor, l.HeadlightRange));
        for (var i = 0; i < 4; i++) MemoryMarshal.Write(push[(496 + i * 16)..], _points[i]);
        MemoryMarshal.Write(push[560..], new Vector4(l.StreetLightColor, 0));
        WriteFog(push[576..]);
        MemoryMarshal.Write(push[608..], new Vector4(a.Zenith, Time));
        for (var i = 0; i < 2; i++) MemoryMarshal.Write(push[(624 + i * 16)..], new Vector4(l.TailLightPosition[i], 0));
        MemoryMarshal.Write(push[656..], new Vector4(l.TailLightColor, 0));
        MemoryMarshal.Write(push[672..], new Vector4(a.SunColor, a.Specular));
        MemoryMarshal.Write(push[688..], new Vector4(a.ShadeSky, a.ContactShadow));
        MemoryMarshal.Write(push[704..], new Vector4(a.ShadeGround, _envMix));
    }

    /// <summary>uFogParams + uFogSun (scene_push.glsl, sky.frag).</summary>
    private void WriteFog(Span<byte> dst)
    {
        var a = Atmosphere;
        MemoryMarshal.Write(dst, new Vector4(a.FogStart, 1 / MathF.Max(a.FogEnd - a.FogStart, 1), a.HeightFogDensity, 1 / a.HeightFogScale));
        MemoryMarshal.Write(dst[16..], new Vector4(a.FogSun, a.HeightFogBase));
    }

    /// <summary>Binds the scene group (shadows, env maps) with <paramref name="block"/> (scene_push.glsl / sky.frag) as its uniform slice.</summary>
    internal void SetScene(IRenderPassEncoder pass, ReadOnlySpan<byte> block) => pass.SetBindGroup(1, _sceneGroup, [_post.Upload(block)]);

    /// <summary>The 4 street lights nearest to <paramref name="eye"/> (radius 0 = slot unused).</summary>
    private void PickStreetLights(Vector3 eye)
    {
        Array.Clear(_points);
        if (Lights.StreetLightColor == Vector3.Zero) return;
        // insertion into the 4 slots, nearest first (no allocations per frame)
        Span<float> dist = [float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue];
        foreach (var p in Lights.StreetLights)
        {
            var d = Vector3.DistanceSquared(p, eye);
            for (var i = 0; i < _points.Length; i++)
            {
                if (d >= dist[i]) continue;
                for (var j = _points.Length - 1; j > i; j--) (dist[j], _points[j]) = (dist[j - 1], _points[j - 1]);
                (dist[i], _points[i]) = (d, new Vector4(p, Lights.StreetLightRadius));
                break;
            }
        }
    }

    /// <summary>
    ///     Opens the HDR scene pass sized like the swapchain (or <paramref name="target"/>) and fills it with the analytic
    ///     sky seen through <paramref name="view"/>/<paramref name="proj"/>. Close with <see cref="EndScene"/>.
    /// </summary>
    public IRenderPassEncoder BeginScene(ICommandEncoder encoder, in Matrix4x4 view, in Matrix4x4 proj, FrameCapture? target = null)
    {
        EnsureTargets(target?.Width ?? _device.SwapchainWidth, target?.Height ?? _device.SwapchainHeight, Samples(Quality));
        // HDR colour + gbuffer (PostProcess.GbufFormat) + depth; with MSAA all three resolve into PostProcess's single-sample targets
        var ms = _samples > 1;
        ColorAttachment[] color = ms
            ? [new(_msaaView, LoadOp.DontCare, StoreOp.DontCare, ClearColor.Black, _post.SceneView), new(_msaaGbufView, LoadOp.Clear, StoreOp.DontCare, ClearColor.Transparent, _post.GbufView)]
            : [new(_post.SceneView, LoadOp.DontCare, StoreOp.Store, ClearColor.Black), new(_post.GbufView, LoadOp.Clear, StoreOp.Store, ClearColor.Transparent)];
        var pass = encoder.BeginRenderPass(new RenderPassDesc(color,
            new DepthStencilAttachment(ms ? _depthView : _post.DepthView, LoadOp.Clear, ms ? StoreOp.DontCare : StoreOp.Store, 0f, LoadOp.Load, StoreOp.DontCare, 0, false,
                false, ms ? _post.DepthView : default),
            DebugName: "scene"));
        pass.SetViewport(0, 0, _w, _h);
        pass.SetScissor(0, 0, _w, _h);

        Matrix4x4.Invert(view, out var camera);
        PickStreetLights(camera.Translation);
        var a = Atmosphere;
        (_viewRotProj, _proj) = (view with { M41 = 0, M42 = 0, M43 = 0 } * proj, proj);
        Matrix4x4.Invert(_viewRotProj, out var inv);
        Span<byte> push = stackalloc byte[SkyPushBytes];
        MemoryMarshal.Write(push, in inv);
        MemoryMarshal.Write(push[64..], new Vector4(a.Zenith, 1f / _w));
        MemoryMarshal.Write(push[80..], new Vector4(a.Horizon, 1f / _h));
        MemoryMarshal.Write(push[96..], new Vector4(Vector3.Normalize(a.SunDirection), 0.99995f));
        // gl_FragCoord.y runs down on Metal/Vulkan; Vulkan's projection is Y-flipped already, Metal's is not
        MemoryMarshal.Write(push[112..], new Vector4(a.SunDisk, _device.Backend == BackendKind.Metal ? -1 : 1));
        MemoryMarshal.Write(push[128..], new Vector4(camera.Translation, 0));
        MemoryMarshal.Write(push[144..], new Vector4(a.FogColor, 0));
        WriteFog(push[160..]);
        pass.SetPipeline(_sky[Quality]);
        SetScene(pass, push);
        pass.Draw(3);
        return pass;
    }

    /// <summary>Ends the scene pass and runs bloom + tonemapping into the swapchain or <paramref name="target"/>.</summary>
    public void EndScene(ICommandEncoder encoder, IRenderPassEncoder pass, FrameCapture? target = null)
    {
        pass.Dispose();
        _post.Run(encoder, Atmosphere, HighQuality, target?.View ?? _device.CurrentSwapchainView, _w, _h, _viewRotProj, _proj,
            _device.Backend == BackendKind.Metal ? -1 : 1);
    }

    /// <summary>
    ///     The course sky mesh, centred under the camera at <paramref name="eye"/> (x/z): horizon fog only, no depth
    ///     (painted over the analytic sky in draw order), vertex alpha ignored.
    /// </summary>
    public void DrawSky(IRenderPassEncoder pass, StaticMesh mesh, in Matrix4x4 viewProj, Vector3 eye) =>
        DrawMesh(pass, _skyMesh[Quality], mesh, viewProj, eye, true);

    public void Draw(IRenderPassEncoder pass, StaticMesh mesh, in Matrix4x4 viewProj, Vector3 eye) =>
        DrawMesh(pass, _pipeline[Quality], mesh, viewProj, eye, false);

    private void DrawMesh(IRenderPassEncoder pass, RenderPipelineHandle pipeline, StaticMesh mesh, in Matrix4x4 viewProj, Vector3 eye, bool sky)
    {
        pass.SetPipeline(pipeline);
        Span<byte> push = stackalloc byte[PushBytes];
        WritePush(push, viewProj, Lights.Car, eye, sky, false); // world.frag: uModel = the car (contact shadow)
        pass.SetVertexBuffer(0, mesh.Vertices);
        pass.SetIndexBuffer(mesh.Indices, IndexType.UInt32);
        DrawBatches(pass, mesh, push);
    }

    /// <summary>
    ///     Draws <paramref name="mesh"/>'s batches with <paramref name="push"/> (filled by <see cref="WritePush"/>), resent
    ///     with the layer offset whenever the layer changes (batches are sorted by layer).
    /// </summary>
    internal void DrawBatches(IRenderPassEncoder pass, StaticMesh mesh, Span<byte> push)
    {
        var layer = -1;
        foreach (var b in mesh.Batches)
        {
            if (b.Layer != layer)
            {
                layer = b.Layer;
                MemoryMarshal.Write(push[LayerPush..], layer * LayerOffset);
                SetScene(pass, push);
            }
            pass.SetBindGroup(0, _textures[b.Texture].Group);
            pass.DrawIndexed(b.IndexCount, 1, b.FirstIndex);
        }
    }

    private void EnsureTargets(int w, int h, int samples)
    {
        _post.Resize(w, h);
        if (w == _w && h == _h && samples == _samples) return;
        ReleaseTargets();
        if (samples > 1) // 1 sample: renders straight into PostProcess's depth/gbuffer
        {
            _depth = _device.CreateTexture(TextureDesc.DepthAttachment(w, h, DepthFormat, "scene-depth") with { SampleCount = samples });
            _depthView = _device.DefaultTextureView(_depth);
            _msaaGbuf = _device.CreateTexture(new TextureDesc(w, h, PostProcess.GbufFormat, TextureUsage.ColorAttachment, SampleCount: samples, DebugName: "gbuffer-msaa"));
            _msaaGbufView = _device.DefaultTextureView(_msaaGbuf);
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
        if (!_msaaGbufView.IsNull) _device.DestroyTextureView(_msaaGbufView);
        if (!_msaaGbuf.IsNull) _device.DestroyTexture(_msaaGbuf);
        (_depth, _depthView, _msaa, _msaaView, _msaaGbuf, _msaaGbufView) = (default, default, default, default, default, default);
    }

    public void Dispose()
    {
        foreach (var g in _sceneGroups.Values) _device.DestroyBindGroup(g);
        foreach (var (tex, view, group) in _textures)
        {
            _device.DestroyBindGroup(group);
            _device.DestroyTextureView(view);
            _device.DestroyTexture(tex);
        }
        ReleaseTargets();
        _shadow.Dispose();
        _post.Dispose();
        foreach (var p in _pipeline.Concat(_skyMesh).Concat(_sky)) _device.DestroyRenderPipeline(p);
        _device.DestroyShader(_shader);
        _device.DestroyShader(_skyShader);
    }
}
