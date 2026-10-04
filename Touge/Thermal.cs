using System.Runtime.InteropServices;

namespace Touge;

/// <summary>macOS thermal state (NSProcessInfo.thermalState) for --bench: a fanless Mac throttles from "fair" on.</summary>
public static class Thermal
{
    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    /// <summary>nominal, fair, serious, critical; "-" off macOS.</summary>
    public static string State => !OperatingSystem.IsMacOS() ? "-"
        : Send(Send(objc_getClass("NSProcessInfo"), sel_registerName("processInfo")), sel_registerName("thermalState")) switch
        {
            0 => "nominal", 1 => "fair", 2 => "serious", _ => "critical",
        };
}
