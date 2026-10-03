using Penelope;

namespace Kansei.Graphics;

/// <summary>
///     Offscreen render target in the swapchain format that can be read back to the CPU.
///     Render into <see cref="View"/>, call <see cref="Copy"/> on the same encoder, and after the
///     frame was submitted (next Update) call <see cref="ReadRgba"/>.
/// </summary>
public sealed class FrameCapture(IPenelopeDevice device, int width, int height) : IDisposable
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    private readonly TextureHandle _tex = device.CreateTexture(new TextureDesc(width, height, device.SwapchainFormat,
        TextureUsage.ColorAttachment | TextureUsage.CopySrc, DebugName: "capture"));
    private readonly BufferHandle _buf = device.CreateBuffer(new BufferDesc(width * height * 4,
        BufferUsage.CopyDst | BufferUsage.MapRead, BufferAccess.Dynamic, "capture-readback"));
    private TextureViewHandle _view;

    public TextureViewHandle View => _view.IsNull ? _view = device.DefaultTextureView(_tex) : _view;

    public void Copy(ICommandEncoder encoder) =>
        encoder.CopyTextureToBuffer(_tex, 0, 0, 0, 0, 0, Width, Height, 1, _buf, 0, Width * 4, Height);

    /// <summary>Waits for the GPU and returns RGBA8 (swizzled from BGRA if needed).</summary>
    public byte[] ReadRgba()
    {
        device.WaitIdle();
        var px = device.MapBuffer(_buf, 0, Width * Height * 4).ToArray();
        if (device.SwapchainFormat is TextureFormat.Bgra8Unorm or TextureFormat.Bgra8UnormSrgb)
            for (var i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
        for (var i = 3; i < px.Length; i += 4) px[i] = 255;
        return px;
    }

    public void Dispose()
    {
        if (!_view.IsNull) device.DestroyTextureView(_view);
        device.DestroyTexture(_tex);
        device.DestroyBuffer(_buf);
    }
}
