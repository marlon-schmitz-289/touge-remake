using System.Text;
using Silk.NET.OpenGL;

namespace Penelope.Backends.OpenGL;

/// <summary>
///     OpenGL 4.5 backend for Penelope. Linux-friendly: any platform with a GL 4.5 context will work
///     (Mesa on Intel/AMD/NVIDIA all qualify). The backend is built to mirror the Vulkan backend's
///     semantics as closely as possible:
///
///       1. NDC matches Vulkan via <c>glClipControl(GL_UPPER_LEFT, GL_ZERO_TO_ONE)</c> — top-left
///          clip-space origin and depth in [0,1]. The same vertex shader output drawn through this
///          backend lands in the same screen pixel as Vulkan.
///       2. CPU texture uploads keep the caller's top-down row order; clip-control (point 1) makes
///          GL sample with a top-left origin, so UV (0,0) returns the first row uploaded — matching
///          Vulkan without any row flipping on upload.
///       3. Push constants are emulated via a reserved UBO at binding
///          <see cref="ShaderLib.PushConstantBinding"/>; <see cref="ShaderLib"/> rewrites the
///          <c>layout(push_constant)</c> block into a UBO at the same slot.
///
///     Construction requires a <see cref="GLContextProvider"/> that wraps the host windowing
///     library's GL function loader and back-buffer swap (SDL, GLFW, ...). The backend never
///     creates a window/context itself.
/// </summary>
public sealed unsafe partial class OpenGLDevice : IPenelopeDevice
{
    /// <summary>
    ///     Host-supplied bridge between the GL context and Penelope. <see cref="GetProcAddress"/>
    ///     is called once to load functions; <see cref="SwapBuffers"/> is invoked from
    ///     <see cref="EndFrame"/>; <see cref="GetDrawableSize"/> reports the current framebuffer
    ///     size in pixels (HiDPI-aware).
    /// </summary>
    public sealed class GLContextProvider
    {
        public required Func<string, nint> GetProcAddress { get; init; }
        public required Action SwapBuffers { get; init; }
        public required Func<(int width, int height)> GetDrawableSize { get; init; }
    }

    internal readonly GL Gl;
    private readonly GLContextProvider _context;

    /// <summary>
    ///     Raw Silk.NET GL handle. Exposed for tools that need direct GL access (screenshot
    ///     capture, debug overlays, RenderDoc bookmarks). Not part of the cross-backend
    ///     <see cref="IPenelopeDevice"/> contract — only call this from OpenGL-aware code.
    /// </summary>
    public GL RawGL => Gl;

    // Resource tables.
    private readonly HandleTable<OpenGLBuffer> _buffers = new("Buffer");
    private readonly HandleTable<OpenGLTexture> _textures = new("Texture");
    private readonly HandleTable<OpenGLTextureView> _textureViews = new("TextureView");
    private readonly HandleTable<OpenGLSampler> _samplers = new("Sampler");
    private readonly Dictionary<SamplerDesc, SamplerHandle> _samplerCache = new();
    private readonly HandleTable<OpenGLShader> _shaders = new("Shader");
    private readonly HandleTable<OpenGLRenderPipeline> _renderPipelines = new("RenderPipeline");
    private readonly HandleTable<OpenGLComputePipeline> _computePipelines = new("ComputePipeline");
    private readonly HandleTable<OpenGLBindGroupLayout> _bindGroupLayouts = new("BindGroupLayout");
    private readonly Dictionary<BindGroupLayoutKey, BindGroupLayoutHandle> _bgLayoutCache = new();
    private readonly HandleTable<OpenGLBindGroup> _bindGroups = new("BindGroup");
    private readonly HandleTable<OpenGLRenderTarget> _renderTargets = new("RenderTarget");
    private readonly HandleTable<OpenGLQueryPool> _queryPools = new("QueryPool");
    private readonly HandleTable<OpenGLFence> _fences = new("Fence");

    /// <summary>FBO cache keyed by attached views — created on demand at BeginRenderPass.</summary>
    internal readonly Dictionary<long, uint> FboCache = new();

    private ulong _nextHandleId = 1;

    // Swapchain proxy: a synthetic Penelope texture/view that maps to the default framebuffer
    // so existing code can treat CurrentSwapchainView like any other texture view.
    private TextureFormat _swapchainFormat;
    private PresentMode _presentMode;
    private int _swapchainWidth;
    private int _swapchainHeight;
    private ulong _swapchainTextureId;
    private ulong _swapchainViewId;

    private bool _frameActive;
    private bool _disposed;

    // Per-frame slot tracking. GL is immediate-mode, so slot rotation is a CPU-side bookkeeping
    // construct: we place a glFenceSync at EndFrame on the slot we just submitted, then in
    // BeginFrame on the next-but-N rotation we glClientWaitSync that slot's prior fence so
    // any caller-side per-frame buffers (e.g. TransientBufferRing) can safely overwrite their
    // slab. Sized to match the Vulkan backend (2) — single-buffered would force a full
    // CPU-GPU stall every frame.
    private const int FramesInFlightCount = 2;
    private readonly nint[] _slotFences = new nint[FramesInFlightCount];
    private int _frameSlot;

    /// <summary>Reusable UBO for push-constant emulation; sized to MaxPushConstantsSize.</summary>
    internal uint PushConstantUbo;
    internal const int PushConstantBufferSize = 256;
    internal readonly byte[] PushConstantStaging = new byte[PushConstantBufferSize];

    /// <summary>
    ///     True once the push-constant UBO has been bound to its indexed binding point. The binding
    ///     persists for the context's lifetime (nothing else binds <see cref="ShaderLib.PushConstantBinding"/>
    ///     — the SetBindGroup set&gt;0 guard guarantees it), so subsequent SetPushConstants only need
    ///     the BufferSubData upload, not a redundant BindBufferRange each draw.
    /// </summary>
    internal bool PushConstantRangeBound;

    public BackendKind Backend => BackendKind.OpenGL;
    public int FramesInFlight => FramesInFlightCount;
    public int CurrentFrameSlot => _frameSlot;
    public AdapterInfo Adapter { get; private set; }
    public DeviceFeatures Features { get; private set; }
    public DeviceLimits Limits { get; private set; }

    public int SwapchainWidth => _swapchainWidth;
    public int SwapchainHeight => _swapchainHeight;
    public TextureFormat SwapchainFormat => _swapchainFormat;
    public PresentMode PresentMode => _presentMode;

    public TextureViewHandle CurrentSwapchainView => new(_swapchainViewId);

    public double TimestampPeriodNs => 1.0; // glGetQueryObjectui64v reports nanoseconds directly.

    private OpenGLDevice(GLContextProvider context)
    {
        _context = context;
        Gl = GL.GetApi(context.GetProcAddress);
    }

    /// <summary>
    ///     Create a new OpenGL device. The caller must already have an active GL 4.5 core context
    ///     on the current thread before calling this.
    /// </summary>
    public static OpenGLDevice Create(
        GLContextProvider context,
        in DeviceDesc deviceDesc,
        in SwapchainDesc swapchainDesc)
    {
        var dev = new OpenGLDevice(context);

        // Verify version. GL 4.5 is required for glClipControl + ARB_direct_state_access subset.
        var versionStr = dev.Gl.GetStringS(StringName.Version) ?? "";
        var rendererStr = dev.Gl.GetStringS(StringName.Renderer) ?? "Unknown";
        var vendorStr = dev.Gl.GetStringS(StringName.Vendor) ?? "Unknown";

        var (major, minor) = ParseGlVersion(versionStr);
        if (major < 4 || (major == 4 && minor < 5))
            throw new InvalidOperationException(
                $"Penelope OpenGL backend requires OpenGL 4.5+. Got '{versionStr}'.");

        // Clip-control is set per-pass in BeginRenderPass: UPPER_LEFT for offscreen RTs (so
        // sampling round-trips match Vulkan), LOWER_LEFT for the swapchain (so display orientation
        // is right-side up). Initialize once with the swapchain default so any rendering before
        // the first BeginRenderPass behaves predictably.
        dev.Gl.ClipControl(GLEnum.LowerLeft, GLEnum.ZeroToOne);

        // Standard pixel-store: tightly packed rows for upload; we explicitly flip rows ourselves.
        dev.Gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        dev.Gl.PixelStore(PixelStoreParameter.PackAlignment, 1);

        if (deviceDesc.EnableValidation)
            dev.EnableDebugCallback();

        // Swapchain proxy.
        dev._swapchainFormat = swapchainDesc.Format;
        dev._presentMode = swapchainDesc.PresentMode;
        dev._swapchainWidth = swapchainDesc.Width;
        dev._swapchainHeight = swapchainDesc.Height;
        var (sw, sh) = context.GetDrawableSize();
        if (sw > 0 && sh > 0) { dev._swapchainWidth = sw; dev._swapchainHeight = sh; }
        dev.CreateSwapchainProxy();

        // Push-constant UBO.
        dev.PushConstantUbo = dev.Gl.GenBuffer();
        dev.Gl.BindBuffer(BufferTargetARB.UniformBuffer, dev.PushConstantUbo);
        dev.Gl.BufferData(BufferTargetARB.UniformBuffer, (nuint)PushConstantBufferSize, null, BufferUsageARB.DynamicDraw);

        dev.Adapter = new AdapterInfo(
            BackendKind.OpenGL,
            rendererStr,
            $"{versionStr} (vendor: {vendorStr})",
            VendorId: 0,
            DeviceId: 0,
            IsDiscrete: !rendererStr.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase)
                        && !rendererStr.Contains("software", StringComparison.OrdinalIgnoreCase));
        dev.Features = dev.BuildFeatures();
        dev.Limits = dev.BuildLimits();

        return dev;
    }

    private void CreateSwapchainProxy()
    {
        var tex = new OpenGLTexture
        {
            Texture = 0, // FBO 0 / default framebuffer — no GL texture id
            Target = TextureTarget.Texture2D,
            Format = _swapchainFormat,
            Width = _swapchainWidth,
            Height = _swapchainHeight,
            Depth = 1,
            MipLevels = 1,
            ArrayLayers = 1,
            SampleCount = 1,
            Usage = TextureUsage.ColorAttachment,
            Dimension = TextureDimension.Tex2D,
            IsSwapchainProxy = true,
        };
        _swapchainTextureId = NewHandleId();
        _textures[_swapchainTextureId] = tex;

        var view = new OpenGLTextureView
        {
            TextureId = _swapchainTextureId,
            Format = _swapchainFormat,
            Aspect = TextureAspect.All,
            BaseMip = 0,
            MipCount = 1,
            BaseLayer = 0,
            LayerCount = 1,
        };
        _swapchainViewId = NewHandleId();
        _textureViews[_swapchainViewId] = view;
        tex.DefaultViewId = _swapchainViewId;
    }

    private static (int major, int minor) ParseGlVersion(string s)
    {
        // Format: "<major>.<minor>[.<patch>] <vendor info>" — we only need major.minor.
        var space = s.IndexOf(' ');
        var head = space > 0 ? s.Substring(0, space) : s;
        var parts = head.Split('.');
        if (parts.Length < 2) return (0, 0);
        if (!int.TryParse(parts[0], out var maj)) return (0, 0);
        if (!int.TryParse(parts[1], out var min)) return (maj, 0);
        return (maj, min);
    }

    private void EnableDebugCallback()
    {
        // KHR_debug — requested via DeviceDesc.EnableValidation. Logs warnings/errors to stderr.
        try
        {
            Gl.Enable(EnableCap.DebugOutput);
            Gl.Enable(EnableCap.DebugOutputSynchronous);
            Gl.DebugMessageCallback((source, type, id, severity, length, message, _) =>
            {
                var text = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(message, length) ?? "";
                if ((GLEnum)severity == GLEnum.DebugSeverityNotification) return;
                Console.Error.WriteLine($"[GL {(GLEnum)severity}] {(GLEnum)type}: {text}");
            }, null);
        }
        catch
        {
            // Older drivers may not support KHR_debug; ignore.
        }
    }

    private DeviceFeatures BuildFeatures()
    {
        var f = DeviceFeatures.ComputeShaders | DeviceFeatures.AnisotropicFiltering
              | DeviceFeatures.DepthBiasClamp | DeviceFeatures.IndirectFirstInstance
              | DeviceFeatures.MultiDrawIndirect;
        if (HasExtension("GL_ARB_texture_compression_bptc")) f |= DeviceFeatures.TextureCompressionBC;
        if (HasExtension("GL_KHR_texture_compression_astc_ldr")) f |= DeviceFeatures.TextureCompressionASTC;
        if (HasExtension("GL_ARB_ES3_compatibility")) f |= DeviceFeatures.TextureCompressionETC2;
        if (HasExtension("GL_ARB_gpu_shader_int64")) f |= DeviceFeatures.ShaderInt64;
        if (HasExtension("GL_ARB_gpu_shader_fp64")) f |= DeviceFeatures.ShaderFloat64;
        if (HasExtension("GL_ARB_blend_func_extended")) f |= DeviceFeatures.DualSourceBlending;
        return f;
    }

    private bool HasExtension(string name)
    {
        Gl.GetInteger(GetPName.NumExtensions, out var count);
        for (uint i = 0; i < (uint)count; i++)
        {
            var s = Gl.GetStringS(StringName.Extensions, i);
            if (s == name) return true;
        }
        return false;
    }

    private DeviceLimits BuildLimits()
    {
        int maxTex2D = GetIntOr(GetPName.MaxTextureSize, 8192);
        int maxTex3D = GetIntOr(GetPName.Max3DTextureSize, 2048);
        int maxLayers = GetIntOr(GetPName.MaxArrayTextureLayers, 256);
        int maxColorAtt = GetIntOr(GetPName.MaxColorAttachments, 8);
        int maxVtxAttr = GetIntOr(GetPName.MaxVertexAttribs, 16);
        int maxVtxBindings = GetIntOr((GetPName)0x82DA /* GL_MAX_VERTEX_ATTRIB_BINDINGS */, 16);
        int maxUboBinding = GetIntOr(GetPName.MaxUniformBufferBindings, 84);
        int maxSsboBinding = GetIntOr((GetPName)0x90DD /* GL_MAX_SHADER_STORAGE_BUFFER_BINDINGS */, 8);
        int maxSamplers = GetIntOr(GetPName.MaxCombinedTextureImageUnits, 96);
        int maxUboSize = GetIntOr(GetPName.MaxUniformBlockSize, 65536);
        int maxSsboSize = GetIntOr((GetPName)0x90DE /* GL_MAX_SHADER_STORAGE_BLOCK_SIZE */, 1 << 27);
        int uboAlign = GetIntOr(GetPName.UniformBufferOffsetAlignment, 256);
        int ssboAlign = GetIntOr((GetPName)0x90DF /* GL_SHADER_STORAGE_BUFFER_OFFSET_ALIGNMENT */, 256);
        int maxAniso = GetIntOr((GetPName)0x84FF /* MAX_TEXTURE_MAX_ANISOTROPY */, 16);

        // Compute workgroup size (3 separate queries).
        Gl.GetInteger(GetPName.MaxComputeWorkGroupSize, 0, out int wgX);
        Gl.GetInteger(GetPName.MaxComputeWorkGroupSize, 1, out int wgY);
        Gl.GetInteger(GetPName.MaxComputeWorkGroupSize, 2, out int wgZ);
        int wgInv = GetIntOr(GetPName.MaxComputeWorkGroupInvocations, 1024);

        return new DeviceLimits(
            MaxTextureDimension2D: maxTex2D,
            MaxTextureDimension3D: maxTex3D,
            MaxTextureArrayLayers: maxLayers,
            MaxBindGroups: 1, // GL backend currently binds set 0 only; set>0 needs a reflection remap (see OpenGLBinder)
            MaxBindingsPerBindGroup: maxUboBinding,
            MaxDynamicUniformBuffersPerPipeline: 8,
            MaxDynamicStorageBuffersPerPipeline: 4,
            MaxSampledTexturesPerShaderStage: maxSamplers,
            MaxSamplersPerShaderStage: maxSamplers,
            MaxStorageBuffersPerShaderStage: maxSsboBinding,
            MaxStorageTexturesPerShaderStage: 8,
            MaxUniformBuffersPerShaderStage: maxUboBinding,
            MaxUniformBufferBindingSize: maxUboSize,
            MaxStorageBufferBindingSize: maxSsboSize,
            MinUniformBufferOffsetAlignment: uboAlign,
            MinStorageBufferOffsetAlignment: ssboAlign,
            MaxVertexBuffers: maxVtxBindings,
            MaxVertexAttributes: maxVtxAttr,
            MaxVertexBufferArrayStride: 2048,
            MaxPushConstantsSize: PushConstantBufferSize,
            MaxColorAttachments: maxColorAtt,
            MaxComputeWorkgroupSizeX: wgX,
            MaxComputeWorkgroupSizeY: wgY,
            MaxComputeWorkgroupSizeZ: wgZ,
            MaxComputeInvocationsPerWorkgroup: wgInv,
            MaxSamplerAnisotropy: maxAniso);
    }

    private int GetIntOr(GetPName name, int fallback)
    {
        try { Gl.GetInteger(name, out int v); return v; } catch { return fallback; }
    }

    public void ConfigureSwapchain(in SwapchainDesc desc)
    {
        _swapchainFormat = desc.Format;
        _presentMode = desc.PresentMode;
        ResizeSwapchain(desc.Width, desc.Height);
    }

    public void SetVSync(bool vsync)
    {
        // OpenGL has no native swapchain to reconfigure — VSync is controlled by the host's
        // GL context (SDL_GL_SetSwapInterval). The host's GameWindow.SetVSync already applies
        // it; this hook just records the preference so PresentMode reads back consistently.
        _presentMode = vsync ? PresentMode.Fifo : PresentMode.Immediate;
    }

    public void ResizeSwapchain(int width, int height)
    {
        var (dw, dh) = _context.GetDrawableSize();
        if (dw > 0 && dh > 0) { width = dw; height = dh; }
        _swapchainWidth = width;
        _swapchainHeight = height;
        if (_textures.TryGetValue(_swapchainTextureId, out var tex))
        {
            tex.Width = width;
            tex.Height = height;
        }
    }

    internal ulong NewHandleId() => _nextHandleId++;

    public void WaitIdle() => Gl.Finish();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WaitIdle();

        foreach (var fbo in FboCache.Values)
        {
            uint id = fbo;
            Gl.DeleteFramebuffer(id);
        }
        FboCache.Clear();

        foreach (var b in _buffers.Values) DeleteBuffer(b);
        foreach (var t in _textures.Values) DeleteTexture(t);
        foreach (var v in _textureViews.Values)
            if (v.ViewTexture != 0) Gl.DeleteTexture(v.ViewTexture);
        foreach (var s in _samplers.Values) Gl.DeleteSampler(s.Sampler);
        foreach (var sh in _shaders.Values) Gl.DeleteProgram(sh.Program);
        foreach (var p in _renderPipelines.Values)
            if (p.Vao != 0) Gl.DeleteVertexArray(p.Vao);
        foreach (var q in _queryPools.Values)
            foreach (var qid in q.Queries) Gl.DeleteQuery(qid);
        foreach (var f in _fences.Values)
            if (f.Sync != 0) Gl.DeleteSync(f.Sync);
        for (var i = 0; i < FramesInFlightCount; i++)
            if (_slotFences[i] != 0) Gl.DeleteSync(_slotFences[i]);

        if (PushConstantUbo != 0) Gl.DeleteBuffer(PushConstantUbo);
        Gl.Dispose();
    }

    public void SetDebugLabel<T>(T handle, string label) where T : struct
    {
        // glObjectLabel (core since GL 4.3; this backend requires 4.5). Resolve the Penelope handle
        // to its GL object id + identifier. Boxing the handle is fine — this is a diagnostic call at
        // resource-creation time, not a per-frame path. Unknown/null ids and GL-less concepts
        // (bind groups: GL has no descriptor objects) fall through to a no-op.
        var (ident, name) = handle switch
        {
            BufferHandle h when _buffers.TryGetValue(h.Id, out var b) => (ObjectIdentifier.Buffer, b.Buffer),
            TextureHandle h when _textures.TryGetValue(h.Id, out var t) => (ObjectIdentifier.Texture, t.Texture),
            TextureViewHandle h when _textureViews.TryGetValue(h.Id, out var v) =>
                (ObjectIdentifier.Texture, v.ViewTexture != 0 ? v.ViewTexture : _textures[v.TextureId].Texture),
            SamplerHandle h when _samplers.TryGetValue(h.Id, out var s) => (ObjectIdentifier.Sampler, s.Sampler),
            ShaderHandle h when _shaders.TryGetValue(h.Id, out var sh) => (ObjectIdentifier.Program, sh.Program),
            RenderPipelineHandle h when _renderPipelines.TryGetValue(h.Id, out var p) => (ObjectIdentifier.Program, p.Program),
            ComputePipelineHandle h when _computePipelines.TryGetValue(h.Id, out var p) => (ObjectIdentifier.Program, p.Program),
            _ => (default, 0u),
        };
        if (name == 0) return;
        Gl.ObjectLabel(ident, name, (uint)label.Length, label);
    }

    private void DeleteBuffer(OpenGLBuffer b)
    {
        if (b.Buffer != 0)
        {
            if (b.MappedPtr != null && b.ImmutableStorage)
            {
                Gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, b.Buffer);
                Gl.UnmapBuffer(BufferTargetARB.CopyWriteBuffer);
                b.MappedPtr = null;
            }
            Gl.DeleteBuffer(b.Buffer);
            b.Buffer = 0;
        }
    }

    private void DeleteTexture(OpenGLTexture t)
    {
        if (t.IsSwapchainProxy) return;
        if (t.Texture != 0) Gl.DeleteTexture(t.Texture);
        t.Texture = 0;
    }

    internal OpenGLBuffer GetBuffer(BufferHandle h) => _buffers[h.Id];
    internal OpenGLTexture GetTexture(ulong id) => _textures[id];
    internal OpenGLTextureView GetTextureView(TextureViewHandle h) => _textureViews[h.Id];
    internal OpenGLBindGroup GetBindGroup(BindGroupHandle h) => _bindGroups[h.Id];
    internal OpenGLBindGroupLayout GetBindGroupLayout(ulong id) => _bindGroupLayouts[id];
    internal OpenGLRenderPipeline GetRenderPipeline(RenderPipelineHandle h) => _renderPipelines[h.Id];
    internal OpenGLComputePipeline GetComputePipeline(ComputePipelineHandle h) => _computePipelines[h.Id];
    internal OpenGLQueryPool GetQueryPool(QueryPoolHandle h) => _queryPools[h.Id];
    internal OpenGLSampler GetSampler(SamplerHandle h) => _samplers[h.Id];
    internal void NotifySwapBuffers() => _context.SwapBuffers();
}
