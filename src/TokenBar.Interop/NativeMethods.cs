using System.Runtime.InteropServices;

namespace TokenBar.Interop;

// P/Invoke surface for crates/tb_core_ffi (contract: include/ctb.h).
// "tb_core_ffi" resolves to tb_core_ffi.dll on Windows and
// libtb_core_ffi.dylib on macOS (the macOS-side test loop) via default
// runtime probing; the native binary is copied next to the assembly by
// src/Directory.Build.targets.
internal static partial class NativeMethods
{
    private const string Lib = "tb_core_ffi";

    [LibraryImport(Lib)]
    internal static partial nint tb_probe();

    [LibraryImport(Lib)]
    internal static partial void tb_free(nint ptr);
}
