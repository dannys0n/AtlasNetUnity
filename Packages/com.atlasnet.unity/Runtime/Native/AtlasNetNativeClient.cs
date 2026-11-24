// Low-level binding. Call from a single networking thread.
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AtlasNet.Native
{
    public static class AtlasNetNativeClient
    {
        private const string Library = "AtlasNetUnityClient";

        public enum Result : int { Error = -1, Empty = 0, Ok = 1, BufferTooSmall = 2 }

        public sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public Handle() : base(true) {}
            protected override bool ReleaseHandle() { anu_destroy(handle); return true; }
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint anu_abi_version();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern Handle anu_create();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void anu_destroy(IntPtr client);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result anu_connect(Handle client,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string endpoint,
            [In] byte[] admission, uint size);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void anu_disconnect(Handle client);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result anu_send(Handle client, [In] byte[] packet, uint size);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result anu_receive(Handle client,
            [Out] byte[] buffer, uint capacity, out uint size);
    }
}
