namespace Penelope;

/// <summary>
///     Per-frame transient GPU buffer. Backed by a single underlying <see cref="BufferHandle"/>
///     of <c>sizePerFrameBytes × <see cref="IPenelopeDevice.FramesInFlight"/></c>, divided into
///     equal-size slabs (one per in-flight frame slot). Callers ask for sub-allocations via
///     <see cref="Allocate"/>; the ring serves them out of the slot the device is currently on.
///
///     <para>Safety: <see cref="IPenelopeDevice.BeginFrame"/> waits on the slot's prior occupant
///     before returning, so by the time <c>Allocate</c> hands out memory in slot N, the GPU has
///     finished reading whatever was in slot N from N frames ago. No <c>WaitIdle</c>, no
///     mid-frame stalls.</para>
///
///     <para>Sizing: a single slab must be large enough to hold every allocation in one frame.
///     Overflow throws <see cref="InvalidOperationException"/> — grow the ring at construction
///     rather than catching the throw. Unlike the old per-batcher ring (which oversized 4× and
///     stalled on wrap), there's no fallback path: 1× of peak frame demand per slab is exactly
///     right.</para>
/// </summary>
public sealed class TransientBufferRing : IDisposable
{
    private readonly IPenelopeDevice _device;
    private readonly BufferHandle _buffer;
    private readonly int _slabSizeBytes;
    private readonly int _framesInFlight;
    private readonly int[] _cursors;
    private long _lastFrame = -1;
    private bool _disposed;

    /// <summary>
    ///     Construct a ring sized to hold <paramref name="sizePerFrameBytes"/> of allocations
    ///     in any one frame. Total GPU allocation is <c>sizePerFrameBytes × FramesInFlight</c>.
    /// </summary>
    public TransientBufferRing(
        IPenelopeDevice device,
        int sizePerFrameBytes,
        BufferUsage usage,
        string? debugName = null)
    {
        if (sizePerFrameBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizePerFrameBytes));

        _device = device;
        _slabSizeBytes = sizePerFrameBytes;
        _framesInFlight = device.FramesInFlight;
        _cursors = new int[_framesInFlight];

        _buffer = device.CreateBuffer(new BufferDesc(
            _slabSizeBytes * _framesInFlight,
            usage | BufferUsage.CopyDst,
            BufferAccess.Stream,
            debugName));
    }

    /// <summary>
    ///     The underlying buffer. Use with <see cref="TransientAlloc.Offset"/> when binding
    ///     (e.g. <c>pass.SetVertexBuffer(0, ring.Buffer, alloc.Offset)</c>).
    /// </summary>
    public BufferHandle Buffer => _buffer;

    /// <summary>Bytes the underlying GPU buffer occupies.</summary>
    public int CapacityBytes => _slabSizeBytes * _framesInFlight;

    /// <summary>Bytes available per frame slot.</summary>
    public int SlabSizeBytes => _slabSizeBytes;

    /// <summary>
    ///     Reserve <paramref name="sizeBytes"/> in the current frame's slab. Returns a
    ///     <see cref="TransientAlloc"/> exposing the buffer offset and a writable mapped span.
    ///     Throws if the slab cannot fit the request — size the ring to peak frame demand.
    /// </summary>
    public TransientAlloc Allocate(int sizeBytes, int alignment = 4)
    {
        if (sizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentException("Alignment must be a positive power of two.", nameof(alignment));

        var slot = _device.CurrentFrameSlot;

        // Lazy reset: first allocation in a new frame rewinds that slot's cursor. Keyed on the frame, not the slot:
        // a ring unused for FramesInFlight-1 frames comes back to the same slot in a new frame. The device's
        // BeginFrame already waited on the slot's prior frame, so the slab is GPU-free.
        if (_device.FrameCount != _lastFrame)
        {
            _cursors[slot] = 0;
            _lastFrame = _device.FrameCount;
        }

        var slabBase = slot * _slabSizeBytes;
        var alignedCursor = (_cursors[slot] + alignment - 1) & ~(alignment - 1);
        if (alignedCursor + sizeBytes > _slabSizeBytes)
            throw new InvalidOperationException(
                $"TransientBufferRing slab full: requested {sizeBytes} bytes at cursor {alignedCursor}, " +
                $"slab size {_slabSizeBytes}. Increase sizePerFrameBytes at construction.");

        var offset = slabBase + alignedCursor;
        _cursors[slot] = alignedCursor + sizeBytes;

        var write = _device.MapBuffer(_buffer, offset, sizeBytes);
        return new TransientAlloc(_buffer, offset, write);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _device.DestroyBuffer(_buffer);
    }
}

/// <summary>
///     Result of a <see cref="TransientBufferRing.Allocate"/> call. Hold only for the scope of
///     the writing code — the underlying mapped span is valid for the current frame slot.
/// </summary>
public readonly ref struct TransientAlloc
{
    public TransientAlloc(BufferHandle buffer, int offset, Span<byte> write)
    {
        Buffer = buffer;
        Offset = offset;
        Write = write;
    }

    public BufferHandle Buffer { get; }
    public int Offset { get; }
    public Span<byte> Write { get; }
}
