using System.Numerics;
using System.Runtime.InteropServices;
using Penelope;

namespace Kansei.Graphics;

/// <summary>
///     Frame pipeline + forward renderer for prelit static geometry. A frame is <see cref="BeginScene"/> (HDR RGBA16F
///     target, 4× MSAA in <see cref="Msaa"/>, analytic sky from <see cref="Atmosphere"/>), draws, then
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
    /// <summary>Headlight reach (m, lighting.glsl windows the beam to it): low beam cut-off hits the road at ~60 m.</summary>
    private const float LowBeamRange = 75, HighBeamRange = 160;
    private const TextureFormat DepthFormat = PostProcess.DepthFormat;
    internal static readonly TextureFormat ShadowFormat = TextureFormat.Depth32Float;

    private readonly IPenelopeDevice _device;
    private readonly ShaderHandle _shader, _opaqueShader, _skyShader, _glowShader;
    private readonly RenderPipelineHandle _glow;
    private BindGroupHandle _depthGroup;
    private Matrix4x4 _viewProj;
    private Vector3 _eye;
    private readonly BindGroupLayoutHandle _layout, _sceneLayout;
    private readonly ShadowMap _shadow;
    private readonly Dictionary<(int[], int[]), BindGroupHandle> _sceneGroups = [];
    private BindGroupHandle _sceneGroup;
    private float _envMix;
    private bool _shadowsThisFrame;
    private readonly Vector4[] _points = new Vector4[4];
    private readonly Func<int, BindGroupHandle> _textureGroup;
    // index 0: 1 sample, 1: 4× MSAA
    private readonly RenderPipelineHandle[] _pipeline = new RenderPipelineHandle[2], _opaque = new RenderPipelineHandle[2], _skyMesh = new RenderPipelineHandle[2],
        _sky = new RenderPipelineHandle[2];
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
    internal int Quality => Msaa ? 1 : 0;
    internal static int Samples(int quality) => quality == 1 ? 4 : 1;

    public Atmosphere Atmosphere { get; set; } = new();
    public SceneLights Lights { get; } = new();
    /// <summary>4× MSAA (+ alpha-to-coverage); off = 1 sample.</summary>
    public bool Msaa = true;
    /// <summary>Cascaded sun shadows (where <see cref="Atmosphere.Shadows"/>), ambient occlusion, bloom.</summary>
    public bool Shadows = true, Ao = true, Bloom = true;
    /// <summary>Wet-ground SSR (rain); off in low settings and for measuring (<c>--flicker</c> motion).</summary>
    public bool Reflections = true;
    /// <summary>Scene resolution relative to the output (0.5 … 2); post and tonemap scale it back up/down, the 2D overlay stays sharp.</summary>
    public float RenderScale = 1;

    /// <summary>All of MSAA, shadows, AO, bloom and SSR on (F2 / <c>--quality off</c> switch them together).</summary>
    public bool HighQuality
    {
        get => Msaa && Shadows && Ao && Bloom && Reflections;
        set => Msaa = Shadows = Ao = Bloom = Reflections = value;
    }
    /// <summary>Seconds, animates rain ripples.</summary>
    public float Time;
    /// <summary>Indexed draws issued so far (scene + shadow passes); a counter for benches, the caller resets it.</summary>
    public int DrawCalls;

    public WorldRenderer(IPenelopeDevice device)
    {
        _device = device;
        _shader = device.CreateShader(ShaderLoader.LoadGraphics(typeof(WorldRenderer).Assembly, "world", "world", "world"));
        _opaqueShader = device.CreateShader(ShaderLoader.LoadGraphics(typeof(WorldRenderer).Assembly, "world", "world_opaque", "world-opaque"));
        _skyShader = device.CreateShader(ShaderLoader.LoadGraphics(typeof(WorldRenderer).Assembly, "fullscreen", "sky", "sky"));
        _glowShader = device.CreateShader(ShaderLoader.LoadGraphics(typeof(WorldRenderer).Assembly, "fullscreen", "glow", "glow"));
        _layout = device.GetBindGroupLayout(new BindGroupLayoutDesc(
            [new BindGroupLayoutEntry(0, BindingType.CombinedImageSampler, ShaderStage.Fragment)], "world-tex"));
        _sampler = device.GetSampler(SamplerDesc.LinearWrap with { MaxAnisotropy = 8 });
        _post = new PostProcess(device);
        _sceneLayout = device.GetBindGroupLayout(new BindGroupLayoutDesc(
            [.. Enumerable.Range(1, 9).Select(b => new BindGroupLayoutEntry(b < 6 ? b : b + 1, BindingType.CombinedImageSampler, ShaderStage.Fragment)),
                PostProcess.UniformEntry(6, ShaderStage.Vertex | ShaderStage.Fragment)], "scene"));
        _shadow = new ShadowMap(device, _layout);
        _glow = PostProcess.Fullscreen(device, _glowShader, PostProcess.HdrFormat, BlendState.Additive with { SrcColor = BlendFactor.One }, [_layout, _sceneLayout], 0);
        _textureGroup = TextureGroup;
        var grey = AddTexture(1, 1, [118, 118, 118, 255], "env-grey"); // 18 % linear until a course sets its maps
        int[] greys = [grey, grey, grey, grey];
        SetEnvironment(greys, greys, 0);
        for (var q = 0; q < 2; q++)
        {
            var ms = MultisampleState.Disabled with { SampleCount = Samples(q), AlphaToCoverageEnabled = q == 1 };
            _pipeline[q] = ScenePipeline(_shader, WorldVertex.Layout, ms, true, "world");
            _opaque[q] = ScenePipeline(_opaqueShader, WorldVertex.Layout, ms with { AlphaToCoverageEnabled = false }, true, "world-opaque");
            _skyMesh[q] = ScenePipeline(_shader, WorldVertex.Layout, ms with { AlphaToCoverageEnabled = false }, false, "sky-mesh");
            _sky[q] = PostProcess.Fullscreen(device, _skyShader, PostProcess.HdrFormat, BlendState.Opaque, [_layout, _sceneLayout], 0, Samples(q), DepthFormat, true);
        }
    }

    /// <summary>
    ///     Pipeline into the HDR scene pass: no culling, reversed-Z GreaterEqual (or no depth at all for the sky).
    ///     With a <paramref name="blend"/> state it tests depth but does not write it (transparent effects), unless <paramref name="depthWrite"/>.
    /// </summary>
    internal RenderPipelineHandle ScenePipeline(ShaderHandle shader, VertexLayout layout, MultisampleState ms, bool depth, string name,
        BlendState? blend = null, bool? depthWrite = null) =>
        _device.CreateRenderPipeline(new RenderPipelineDesc(
            shader, layout, PrimitiveTopology.TriangleList, RasterizerState.Default,
            depth ? DepthStencilState.DepthLessWrite with { DepthCompare = CompareFunc.GreaterEqual, DepthWriteEnabled = depthWrite ?? blend == null } : DepthStencilState.Disabled, ms,
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

    /// <summary>Number of textures added so far: the index the next <see cref="AddTexture"/> returns.</summary>
    public int TextureCount => _textures.Count;

    /// <summary>
    ///     Frees the textures from index <paramref name="first"/> on (the last ones added, e.g. a car's being replaced).
    ///     The GPU must be done with them (WaitIdle) and none may be an env map of <see cref="SetEnvironment"/>.
    /// </summary>
    public void ReleaseTextures(int first)
    {
        for (var i = _textures.Count - 1; i >= first; i--)
        {
            var (tex, view, group) = _textures[i];
            _device.DestroyBindGroup(group);
            _device.DestroyTextureView(view);
            _device.DestroyTexture(tex);
        }
        _textures.RemoveRange(first, _textures.Count - first);
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
    ///     <see cref="Atmosphere.Shadows"/> or <see cref="Shadows"/> is off.
    /// </summary>
    public void RenderShadows(ICommandEncoder encoder, Vector3 eye, Vector3 forward, float fovY, float aspect, StaticMesh world,
        ReadOnlySpan<(StaticMesh Mesh, Matrix4x4 Model)> cars)
    {
        _shadowsThisFrame = Atmosphere.Shadows && Shadows;
        if (!_shadowsThisFrame) return;
        _shadow.Update(eye, forward, fovY, aspect, Vector3.Normalize(Atmosphere.SunDirection), _device.Backend == BackendKind.Vulkan);
        DrawCalls += _shadow.Render(encoder, _textureGroup, world, cars);
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
        var share = a.LocalLightShare;
        for (var i = 0; i < 2; i++)
        {
            MemoryMarshal.Write(push[(416 + i * 16)..], new Vector4(l.HeadlightPosition[i], l.HighBeam));
            MemoryMarshal.Write(push[(448 + i * 16)..], new Vector4(l.HeadlightDirection[i], i == 0 ? l.LampGlow : l.Reverse));
        }
        MemoryMarshal.Write(push[480..], new Vector4(l.HeadlightColor * share, float.Lerp(LowBeamRange, HighBeamRange, l.HighBeam)));
        for (var i = 0; i < 4; i++) MemoryMarshal.Write(push[(496 + i * 16)..], _points[i]);
        MemoryMarshal.Write(push[560..], new Vector4(l.StreetLightColor * share, 0));
        WriteFog(push[576..]);
        MemoryMarshal.Write(push[608..], new Vector4(a.Zenith, Time));
        // uTailPos[0].w: extinction of the height fog at the camera (fog.glsl's density at the eye; lighting.glsl dims lamp light with it)
        var sigma = a.HeightFogDensity * MathF.Exp(-Math.Clamp((eye.Y - a.HeightFogBase) / a.HeightFogScale, -4, 40));
        if (sigma < 0.004f) sigma = 0; // thin haze (< 2 % over 5 m): not worth the shaders' exps
        for (var i = 0; i < 2; i++) MemoryMarshal.Write(push[(624 + i * 16)..], new Vector4(l.TailLightPosition[i], i == 0 ? sigma : _envMix));
        MemoryMarshal.Write(push[656..], new Vector4(l.TailLightColor, share));
        MemoryMarshal.Write(push[672..], new Vector4(a.SunColor, a.Specular));
        MemoryMarshal.Write(push[688..], new Vector4(a.ShadeSky, a.ContactShadow));
        MemoryMarshal.Write(push[704..], new Vector4(a.ShadeGround, a.FogDrift));
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
    ///     Opens the HDR scene pass sized like the swapchain (or <paramref name="target"/>, or the <paramref name="viewport"/> of it:
    ///     split screen, one scene per view into its rectangle) and fills it with the analytic sky seen through
    ///     <paramref name="view"/>/<paramref name="proj"/>. Close with <see cref="EndScene"/> (same viewport).
    /// </summary>
    public IRenderPassEncoder BeginScene(ICommandEncoder encoder, in Matrix4x4 view, in Matrix4x4 proj, FrameCapture? target = null, Core.Viewport? viewport = null)
    {
        var scale = Math.Clamp(RenderScale, 0.25f, 2);
        var (ow, oh) = viewport is { } vp ? (vp.Width, vp.Height) : (target?.Width ?? _device.SwapchainWidth, target?.Height ?? _device.SwapchainHeight);
        EnsureTargets(Math.Max(1, (int)(ow * scale)), Math.Max(1, (int)(oh * scale)), Samples(Quality));
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
        (_eye, _viewProj) = (camera.Translation, view * proj);
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

    /// <summary>Ends the scene pass and runs bloom + tonemapping into the swapchain or <paramref name="target"/> (into <paramref name="viewport"/> of it, the rest kept).</summary>
    public void EndScene(ICommandEncoder encoder, IRenderPassEncoder pass, FrameCapture? target = null, Core.Viewport? viewport = null)
    {
        pass.Dispose();
        DrawGlow(encoder);
        var out0 = viewport ?? new Core.Viewport(0, 0, target?.Width ?? _device.SwapchainWidth, target?.Height ?? _device.SwapchainHeight);
        _post.Run(encoder, Atmosphere, Ao, Bloom, Reflections, target?.View ?? _device.CurrentSwapchainView, out0.Width, out0.Height, _viewRotProj, _proj,
            _device.Backend == BackendKind.Metal ? -1 : 1, out0.X, out0.Y, viewport != null);
    }

    /// <summary>
    ///     Adds the glow of the dynamic lights in the fog (glow.frag) onto the resolved scene, once per pixel along the
    ///     ray to the resolved depth; nothing when <see cref="Atmosphere.LightGlow"/> is 0 (day).
    /// </summary>
    private void DrawGlow(ICommandEncoder encoder)
    {
        if (Atmosphere.LightGlow <= 0) return;
        if (_depthGroup.IsNull)
            _depthGroup = _device.CreateBindGroup(new BindGroupDesc(_layout,
                [BindGroupEntry.CombinedImageSampler(0, _post.DepthView, _device.GetSampler(SamplerDesc.Nearest))], "glow-depth"));
        using var pass = encoder.BeginRenderPass(new RenderPassDesc([new ColorAttachment(_post.SceneView, LoadOp.Load, StoreOp.Store, ClearColor.Black)],
            DebugName: "glow"));
        pass.SetViewport(0, 0, _w, _h);
        pass.SetScissor(0, 0, _w, _h);
        Matrix4x4.Invert(_viewProj, out var inv);
        Span<byte> push = stackalloc byte[PushBytes];
        var screen = new Matrix4x4 { M11 = 1f / _w, M12 = 1f / _h, M13 = _device.Backend == BackendKind.Metal ? -1 : 1 };
        WritePush(push, inv, screen, _eye, false, false);
        pass.SetPipeline(_glow);
        pass.SetBindGroup(0, _depthGroup);
        SetScene(pass, push);
        pass.Draw(3);
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
        DrawBatches(pass, mesh, push, viewProj, eye, sky ? default : _opaque[Quality], pipeline);
    }

    /// <summary>
    ///     Draws <paramref name="mesh"/>'s batches with <paramref name="push"/> (filled by <see cref="WritePush"/>), resent
    ///     with the layer offset whenever the layer changes. With <see cref="StaticMesh.Bounds"/> (the course): batches
    ///     outside <paramref name="mvp"/> are skipped, the rest drawn front to back from <paramref name="eye"/> within each
    ///     layer, so the depth test rejects hidden fragments before the alpha-tested shading (no hidden-surface removal).
    ///     With an <paramref name="opaque"/> pipeline, <see cref="MeshBatch.Opaque"/> batches come first with it, then the
    ///     rest with <paramref name="cutout"/> (layers are depth-offset, so their order across the two groups does not matter).
    /// </summary>
    internal void DrawBatches(IRenderPassEncoder pass, StaticMesh mesh, Span<byte> push, in Matrix4x4 mvp, Vector3 eye,
        RenderPipelineHandle opaque = default, RenderPipelineHandle cutout = default)
    {
        var n = 0;
        var bounds = mesh.Bounds;
        if (_order.Length < mesh.Batches.Count) (_order, _keys) = (new int[mesh.Batches.Count], new float[mesh.Batches.Count]);
        for (var i = 0; i < mesh.Batches.Count; i++)
        {
            if (bounds != null && !Frustum.Visible(mvp, bounds[i].Min, bounds[i].Max)) continue;
            // opaque group first, then by layer (batches are sorted by layer), then by the distance to the box (0 inside)
            _keys[n] = bounds == null ? i
                : (mesh.Batches[i].Opaque || opaque.IsNull ? 0 : 1e7f) + mesh.Batches[i].Layer * 1e6f + Vector3.Distance(eye, Vector3.Clamp(eye, bounds[i].Min, bounds[i].Max));
            _order[n++] = i;
        }
        if (bounds != null) Array.Sort(_keys, _order, 0, n);
        int layer = -1, texture = -1, group = -1;
        for (var k = 0; k < n; k++)
        {
            var b = mesh.Batches[_order[k]];
            if (!opaque.IsNull && (b.Opaque ? 0 : 1) != group)
            {
                group = b.Opaque ? 0 : 1;
                pass.SetPipeline(b.Opaque ? opaque : cutout);
                layer = texture = -1; // rebind after the pipeline change
            }
            if (b.Layer != layer)
            {
                layer = b.Layer;
                MemoryMarshal.Write(push[LayerPush..], layer * LayerOffset);
                SetScene(pass, push);
            }
            if (b.Texture != texture) pass.SetBindGroup(0, _textures[texture = b.Texture].Group);
            pass.DrawIndexed(b.IndexCount, 1, b.FirstIndex);
            DrawCalls++;
        }
    }

    private int[] _order = [];
    private float[] _keys = [];

    private void EnsureTargets(int w, int h, int samples)
    {
        if (w != _w || h != _h) ReleaseDepthGroup();
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

    private void ReleaseDepthGroup()
    {
        if (!_depthGroup.IsNull) _device.DestroyBindGroup(_depthGroup);
        _depthGroup = default;
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
        ReleaseDepthGroup();
        _shadow.Dispose();
        _post.Dispose();
        foreach (var p in _pipeline.Concat(_opaque).Concat(_skyMesh).Concat(_sky).Append(_glow)) _device.DestroyRenderPipeline(p);
        _device.DestroyShader(_shader);
        _device.DestroyShader(_opaqueShader);
        _device.DestroyShader(_glowShader);
        _device.DestroyShader(_skyShader);
    }
}
