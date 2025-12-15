using System;

namespace AtlasNet.Rpc
{
    /// <summary>
    /// Marks a method as a ServerRpc.
    /// In AtlasNet, this means the call intent is sent to the server via messaging.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ServerRpcAttribute : Attribute
    {
    }
}
