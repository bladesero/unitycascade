using System;
using System.Runtime.InteropServices;

namespace SGame.Rendering.OffscreenParticles
{
    // Development-player helper. Does not load/inject RenderDoc; it only uses an
    // already-injected, user-launched RenderDoc API when --osp-renderdoc is present.
    internal static class OffscreenParticleCapture
    {
#if UNITY_STANDALONE_WIN && DEVELOPMENT_BUILD
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetApi(int version, out IntPtr api);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetPath([MarshalAs(UnmanagedType.LPStr)] string path);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Trigger();
#endif
        public static void Queue(string path)
        {
#if UNITY_STANDALONE_WIN && DEVELOPMENT_BUILD
            var module = GetModuleHandle("renderdoc.dll");
            if (module == IntPtr.Zero) return;
            var address = GetProcAddress(module, "RENDERDOC_GetAPI");
            if (address == IntPtr.Zero) return;
            var getApi = Marshal.GetDelegateForFunctionPointer<GetApi>(address);
            if (getApi(10400, out IntPtr api) != 1) return;
            // RenderDoc 1.4.0 ABI: SetCaptureFilePathTemplate is slot 11; TriggerCapture is slot 15.
            Marshal.GetDelegateForFunctionPointer<SetPath>(Marshal.ReadIntPtr(api, 11 * IntPtr.Size))(path);
            Marshal.GetDelegateForFunctionPointer<Trigger>(Marshal.ReadIntPtr(api, 15 * IntPtr.Size))();
#endif
        }
    }
}
