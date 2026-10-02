using System;
using System.Collections.Generic;
using UnityEngine;

namespace AtlasNet
{
    // Semantic Unity/backend boundary, not a native ABI or a transport interface.
    // All operations and callbacks execute on Unity's main thread. A native bridge
    // must drain its inbound queue from PollAndTick before invoking Unity lifecycle code.
    internal interface IWorldBackend
    {
        bool IsServer { get; }
        bool IsClient { get; }
        bool IsHost { get; }
        bool IsRunning { get; }
        SessionId LocalSession { get; }
        uint Tick { get; }
        int TrackedEntityCount { get; }
        int RemoteClientCount { get; }
        int ObserverCopies { get; }
        long BytesSent { get; }
        long BytesSentLastTick { get; }
        long AllocatedBytesLastTick { get; }
        double LastTickMilliseconds { get; }
        bool IsWorker { get; }
        ulong LocalWorkerId { get; }
        int WorkerCount { get; }
        int PendingHandoffCount { get; }
        IReadOnlyList<Vector2> LocalRegion { get; }
        int LocalRegionVersion { get; }
        IReadOnlyList<Vector2> ClientDebugRegion { get; }
        EntityId ClientDebugRegionEntity { get; }
        ulong ClientDebugRegionWorker { get; }
        uint ClientDebugRegionEpoch { get; }
        int ClientDebugRegionVersion { get; }
        int LocalAuthorityCount { get; }
        int GhostCount { get; }
        Func<SessionId, Vector3> PlayerSpawnPosition { get; set; }
        event Action<SessionId> SessionJoined;
        event Action<SessionId> SessionLeft;
        void PollAndTick(float deltaTime);
        void ValidateRegistry();
        void StartServer();
        void StartHost();
        void StartClient();
        void StartWorker();
        void Stop();
        NetworkObject Spawn(string prefabId, Vector3 position, Quaternion rotation, SessionId owner = default);
        NetworkObject Spawn(NetworkObject prefab, Vector3 position, Quaternion rotation, SessionId owner = default);
        void RequestSpawn(NetworkObject source, NetworkObject prefab, Vector3 position, Quaternion rotation, SessionId owner = default);
        void Despawn(NetworkObject obj);
        void HideFrom(NetworkObject obj, SessionId session);
        void ShowTo(NetworkObject obj, SessionId session);
        bool CanSimulate(NetworkObject obj);
        void SendVariable(NetworkBehaviour behaviour, ushort id, INetworkVariable value);
        void SendRpc(NetworkBehaviour behaviour, RpcDestination destination, SessionId target, uint method, Action<NetWriter> write);
        void SendAuthorityInteraction(NetworkObject source, NetworkBehaviour target, uint method, Action<NetWriter> write);
        void SendTransform(NetworkTransform component, byte flags, Vector3 position, Quaternion rotation);
        void SendAnimator(NetworkAnimator component, byte[] payload);
        bool SetClientDebugRegionEnabled(NetworkObject ownedPlayer, bool enabled);
        bool RequestLocalRegionForDebug();
        bool ShouldRenderWorkerCopyForDebug(NetworkObject obj);
    }
}
