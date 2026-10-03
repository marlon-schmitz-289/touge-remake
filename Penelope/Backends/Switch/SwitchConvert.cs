namespace Penelope.Backends.Switch;

/// <summary>
///     Penelope ↔ NVN2 enum conversions. Stubbed pending the Nintendo SDK headers — every
///     mapping is documented with the matching NVN enum so the implementation is mechanical
///     once <c>nvn_Cpp.h</c> is on the include path.
///
///     <para>The structure mirrors VulkanConvert / OpenGLConvert / MetalConvert so the
///     pattern is the same and a developer with SDK access fills in the right-hand side.</para>
/// </summary>
internal static class SwitchConvert
{
    // Pixel formats — NVN_FORMAT_RGBA8 etc. live in nvn::Format.
    public static int ToNvnFormat(TextureFormat fmt) => throw NvnNotImplemented(nameof(ToNvnFormat));

    // Texture target — NVN distinguishes 2D / 2D_ARRAY / 3D / CUBEMAP via NVNtextureTarget.
    public static int ToNvnTextureTarget(TextureDimension d) => throw NvnNotImplemented(nameof(ToNvnTextureTarget));

    // NVNbufferAccess — { READ, WRITE, READ_WRITE }; combine with NVNmemoryPoolFlags for storage.
    public static int ToNvnBufferUsage(BufferUsage usage) => throw NvnNotImplemented(nameof(ToNvnBufferUsage));

    // NVNsamplerWrapMode — REPEAT / MIRRORED_REPEAT / CLAMP_TO_EDGE / CLAMP_TO_BORDER.
    public static int ToNvnAddressMode(AddressMode a) => throw NvnNotImplemented(nameof(ToNvnAddressMode));

    // NVNminFilter / NVNmagFilter pair.
    public static int ToNvnFilter(FilterMode f) => throw NvnNotImplemented(nameof(ToNvnFilter));

    // NVNblendFunc — same ordering as Vulkan/D3D for the most part (ZERO/ONE/SRC_COLOR/...).
    public static int ToNvnBlendFunc(BlendFactor f) => throw NvnNotImplemented(nameof(ToNvnBlendFunc));

    // NVNblendEquation — ADD / SUB / REV_SUB / MIN / MAX.
    public static int ToNvnBlendEquation(BlendOp op) => throw NvnNotImplemented(nameof(ToNvnBlendEquation));

    // NVNcompareFunc — NEVER / LESS / EQUAL / LESS_OR_EQUAL / GREATER / NOT_EQUAL / GREATER_OR_EQUAL / ALWAYS.
    public static int ToNvnCompareFunc(CompareFunc f) => throw NvnNotImplemented(nameof(ToNvnCompareFunc));

    // NVNdrawPrimitive — POINTS / LINES / LINE_STRIP / TRIANGLES / TRIANGLE_STRIP / TRIANGLE_FAN.
    public static int ToNvnPrimitive(PrimitiveTopology t) => throw NvnNotImplemented(nameof(ToNvnPrimitive));

    // NVNfrontFace — CCW / CW.
    public static int ToNvnFrontFace(FrontFace f) => throw NvnNotImplemented(nameof(ToNvnFrontFace));

    // NVNpolygonMode — POINT / LINE / FILL.
    public static int ToNvnPolygonMode(PolygonMode p) => throw NvnNotImplemented(nameof(ToNvnPolygonMode));

    // NVNloadOp — LOAD / DONT_CARE / CLEAR.
    public static int ToNvnLoadOp(LoadOp op) => throw NvnNotImplemented(nameof(ToNvnLoadOp));

    // NVNstoreOp — STORE / DONT_CARE.
    public static int ToNvnStoreOp(StoreOp op) => throw NvnNotImplemented(nameof(ToNvnStoreOp));

    // NVNvertexAttribFormat — pairs of (size, type, normalized) similar to GL.
    public static int ToNvnVertexFormat(VertexFormat f) => throw NvnNotImplemented(nameof(ToNvnVertexFormat));

    // NVNindexType — UNSIGNED_SHORT / UNSIGNED_INT.
    public static int ToNvnIndexType(IndexType t) => throw NvnNotImplemented(nameof(ToNvnIndexType));

    private static NotImplementedException NvnNotImplemented(string method) =>
        new($"SwitchConvert.{method}: NVN2 mapping not implemented yet — fill in once the " +
            "Nintendo SDK headers are on the include path. See nvn_Cpp.h for the matching " +
            "enum (search for the comment above this method).");
}
