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

				// Compile fast invoke delegate ONCE
				Action<NetBehaviour, object[]> invoke = CompileInvokeDelegate(
						behaviourType,
						method,
						parameters.Length
				);

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



		private static Action<NetBehaviour, object[]> CompileInvokeDelegate(
		Type behaviourType,
		MethodInfo method,
		int parameterCount)
		{
			/*
			 * This builds a delegate equivalent to:
			 *
			 * (NetBehaviour beh, object[] args) =>
			 *     ((ConcreteBehaviour)beh).Method(
			 *         (T0)args[0],
			 *         (T1)args[1],
			 *         ...
			 *     );
			 */

			var behParam = System.Linq.Expressions.Expression.Parameter(
					typeof(NetBehaviour), "beh");

			var argsParam = System.Linq.Expressions.Expression.Parameter(
					typeof(object[]), "args");

			var castedBeh = System.Linq.Expressions.Expression.Convert(
					behParam, behaviourType);

			var callArgs = new System.Linq.Expressions.Expression[parameterCount];
			var parameters = method.GetParameters();

			for (int i = 0; i < parameterCount; i++)
			{
				var indexExpr = System.Linq.Expressions.Expression.Constant(i);
				var argAccess = System.Linq.Expressions.Expression.ArrayIndex(argsParam, indexExpr);
				callArgs[i] = System.Linq.Expressions.Expression.Convert(
						argAccess,
						parameters[i].ParameterType
				);
			}

			var callExpr = System.Linq.Expressions.Expression.Call(
					castedBeh,
					method,
					callArgs
			);

			var lambda = System.Linq.Expressions.Expression.Lambda<Action<NetBehaviour, object[]>>(
					callExpr,
					behParam,
					argsParam
			);

			return lambda.Compile();
		}

	}
}
