using System;
using System.Collections.Generic;
using System.Reflection;

namespace AtlasNet.Rpc
{
  /// <summary>
  /// Central registry mapping RPC method IDs to MethodInfo.
  /// This mirrors NGO's internal RPC tables.
  /// </summary>
  internal static class RpcRegistry
  {
		private static readonly Dictionary<Type, ServerRpcTable> _tables = new();


		private static readonly Dictionary<MethodInfo, ulong> _reverseLookup
						= new();


		/// <summary>
		/// Registers all [ServerRpc] methods on a NetBehaviour type.
		/// </summary>
		public static void Register(Type behaviourType)
		{
			if (_tables.ContainsKey(behaviourType))
				return;

			var table = new ServerRpcTable();

			var methods = behaviourType.GetMethods(
					BindingFlags.Instance |
					BindingFlags.Public |
					BindingFlags.NonPublic);

			ulong nextId = 1;

			foreach (var method in methods)
			{
				if (!Attribute.IsDefined(method, typeof(ServerRpcAttribute)))
					continue;

				var rpcId = nextId++;
				_reverseLookup[method] = rpcId;

				var parameters = method.GetParameters();

				// Build open instance invoke delegate
				Action<NetBehaviour, object[]> invoke = (beh, args) =>
				{
					method.Invoke(beh, args);
				};

				table.Add(new ServerRpcDescriptor(
						rpcId,
						parameters,
						invoke
				));
			}

			_tables[behaviourType] = table;
		}

		public static bool TryGetDescriptor(
		Type behaviourType,
		ulong rpcId,
		out ServerRpcDescriptor descriptor)
		{
			if (_tables.TryGetValue(behaviourType, out var table))
				return table.TryGet(rpcId, out descriptor);

			descriptor = null;
			return false;
		}

		/// <summary>
		/// Gets the RPC id for a specific ServerRpc method.
		/// </summary>
		public static ulong GetRpcId(MethodInfo method)
		{
			return _reverseLookup.TryGetValue(method, out var id) ? id : 0;
		}

	}
}
