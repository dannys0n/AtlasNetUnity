using System;
using System.Runtime.InteropServices;

namespace AtlasNet.Serialization
{
	internal sealed class BlittableBufferReader
	{
		private readonly byte[] _buffer;
		private int _offset;

		public BlittableBufferReader(byte[] buffer)
		{
			_buffer = buffer;
		}

		public object ReadBlittable(Type type)
		{
			int size = Marshal.SizeOf(type);

			IntPtr ptr = Marshal.AllocHGlobal(size);
			try
			{
				Marshal.Copy(_buffer, _offset, ptr, size);
				_offset += size;
				return Marshal.PtrToStructure(ptr, type);
			}
			finally
			{
				Marshal.FreeHGlobal(ptr);
			}
		}
	}
}
