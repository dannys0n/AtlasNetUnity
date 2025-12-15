using System;
using System.Runtime.InteropServices;

namespace AtlasNet.Serialization
{
	/// <summary>
	/// Serializes blittable structs to/from byte arrays.
	/// This is a minimal stepping stone; later we switch to pooled buffers and streams.
	/// </summary>
	public static class BlittableSerializer
	{
		public static byte[] ToBytes<T>(in T value) where T : struct
		{
			int size = Marshal.SizeOf<T>();
			var bytes = new byte[size];

			IntPtr ptr = Marshal.AllocHGlobal(size);
			try
			{
				Marshal.StructureToPtr(value, ptr, false);
				Marshal.Copy(ptr, bytes, 0, size);
				return bytes;
			}
			finally
			{
				Marshal.FreeHGlobal(ptr);
			}
		}

		public static T FromBytes<T>(byte[] bytes) where T : struct
		{
			int size = Marshal.SizeOf<T>();
			if (bytes == null || bytes.Length != size)
				throw new ArgumentException($"Invalid byte[] size for {typeof(T).Name}. Expected {size}, got {bytes?.Length ?? 0}.");

			IntPtr ptr = Marshal.AllocHGlobal(size);
			try
			{
				Marshal.Copy(bytes, 0, ptr, size);
				return Marshal.PtrToStructure<T>(ptr);
			}
			finally
			{
				Marshal.FreeHGlobal(ptr);
			}
		}
	}
}
