using System;
using System.Runtime.InteropServices;

namespace AtlasNet.Serialization
{
	internal sealed class BlittableBufferWriter : IDisposable
	{
		private byte[] _buffer = new byte[64];
		private int _offset;

		public void WriteBlittable(object value, Type type)
		{
			int size = Marshal.SizeOf(type);
			Ensure(size);

			IntPtr ptr = Marshal.AllocHGlobal(size);
			try
			{
				Marshal.StructureToPtr(value, ptr, false);
				Marshal.Copy(ptr, _buffer, _offset, size);
				_offset += size;
			}
			finally
			{
				Marshal.FreeHGlobal(ptr);
			}
		}

		public byte[] ToArray()
		{
			var result = new byte[_offset];
			Buffer.BlockCopy(_buffer, 0, result, 0, _offset);
			return result;
		}

		private void Ensure(int size)
		{
			if (_offset + size <= _buffer.Length)
				return;

			Array.Resize(ref _buffer, _buffer.Length * 2);
		}

		public void Dispose() { }
	}
}
