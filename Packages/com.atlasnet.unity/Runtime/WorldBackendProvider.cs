using UnityEngine;
namespace AtlasNet
{
    // Optional transport extension: custom implementations can multiplex further
    // listeners without changing entity routing or gameplay scripts.
    public abstract class NetworkTransportProvider : ScriptableObject
    {
        public abstract IMessageTransport CreateTransport(NetworkManager runtime,bool server);
    }

    // Advanced extension point. Gameplay prefabs do not reference a backend implementation.
    public abstract class WorldBackendProvider : ScriptableObject
    {
        public abstract IWorldBackend CreateBackend(NetworkManager runtime);
    }
}
