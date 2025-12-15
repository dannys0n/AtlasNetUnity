using System;
using System.Reflection;
using AtlasNet.Serialization;

namespace AtlasNet.Rpc
{
	/// <summary>
	/// Packs and unpacks ServerRpc parameters into a single byte buffer.
	/// </summary>
	internal static class ServerRpcPayloadPacker
	{
		public static byte[] Pack(object[] args, ParameterInfo[] parameters)
		{
			if (args == null || args.Length == 0)
				return Array.Empty<byte>();

			// For now: single struct OR multiple blittable primitives
			// We concatenate each value in order
			using var writer = new BlittableBufferWriter();

			for (int i = 0; i < args.Length; i++)
			{
				writer.WriteBlittable(args[i], parameters[i].ParameterType);
			}

			return writer.ToArray();
		}

		public static object[] Unpack(
				byte[] bytes,
				ParameterInfo[] parameters)
		{
			if (parameters.Length == 0)
				return null;

			var result = new object[parameters.Length];
			var reader = new BlittableBufferReader(bytes);

			for (int i = 0; i < parameters.Length; i++)
			{
				result[i] = reader.ReadBlittable(parameters[i].ParameterType);
			}

			return result;
		}
	}
}
