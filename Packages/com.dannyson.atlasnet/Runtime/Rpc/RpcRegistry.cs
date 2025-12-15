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
        private static readonly Dictionary<Type, Dictionary<ulong, MethodInfo>> _registry
            = new();

		    private static readonly Dictionary<MethodInfo, ulong> _reverseLookup
		        = new();


		/// <summary>
		/// Registers all [ServerRpc] methods on a NetBehaviour type.
		/// </summary>
		public static void Register(Type behaviourType)
        {
            if (_registry.ContainsKey(behaviourType))
                return;

            var map = new Dictionary<ulong, MethodInfo>();
            var methods = behaviourType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            ulong nextId = 1;

			      foreach (var method in methods)
			      {
				        if (!Attribute.IsDefined(method, typeof(ServerRpcAttribute)))
					          continue;

				        map[nextId] = method;
				        _reverseLookup[method] = nextId;

								var parameters = method.GetParameters();
								if (parameters.Length == 0)
								{
									// No-arg: treat payload bytes as null/empty.
									// We'll just call via MethodInfo once for now OR you can add a separate no-arg registry later.
								}
								else if (parameters.Length == 1)
								{
									var payloadType = parameters[0].ParameterType;

									// Create open instance delegate: (TBehaviour, TPayload) -> void
									// We do this with reflection once at registration time.
									var registerMethod = typeof(ServerRpcHandlerRegistry)
											.GetMethod(nameof(ServerRpcHandlerRegistry.Register), BindingFlags.Public | BindingFlags.Static);

									var genericRegister = registerMethod.MakeGenericMethod(behaviourType, payloadType);

									// Build strongly typed delegate for handler method
									// Signature must match Action<TBehaviour, TPayload>
									var actionType = typeof(Action<,>).MakeGenericType(behaviourType, payloadType);
									var del = Delegate.CreateDelegate(actionType, null, method);

									genericRegister.Invoke(null, new object[] { nextId, del });
								}


								nextId++;
			      }


			      _registry[behaviourType] = map;
        }

        /// <summary>
        /// Resolves a method for a given behaviour type and RPC id.
        /// </summary>
        public static MethodInfo Resolve(Type behaviourType, ulong rpcId)
        {
            if (_registry.TryGetValue(behaviourType, out var map) &&
                map.TryGetValue(rpcId, out var method))
            {
                return method;
            }

            return null;
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
