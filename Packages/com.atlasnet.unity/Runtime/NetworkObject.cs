using System;
using UnityEngine;

namespace AtlasNet
{
    [DisallowMultipleComponent]
    public sealed class NetworkObject : MonoBehaviour
    {
        [SerializeField] private string prefabId;
        private NetworkBehaviour[] behaviours;
        public string PrefabId => prefabId;
        public EntityId EntityId { get; private set; }
        public SessionId OwnerSession { get; private set; }
        public NetworkManager Manager { get; private set; }
        public bool IsSpawned => Manager != null;
        public bool IsOwner => IsSpawned && Manager.IsClient && OwnerSession == Manager.LocalSession;
        public bool HasSimulationAuthority => IsSpawned && Manager.CanSimulate(this);
        internal NetworkBehaviour[] Behaviours => behaviours;

        internal void Initialize(NetworkManager manager, EntityId id, SessionId owner)
        {
            if (IsSpawned) throw new InvalidOperationException("NetworkObject was already spawned");
            Manager = manager;
            EntityId = id;
            OwnerSession = owner;
            behaviours = GetComponents<NetworkBehaviour>();
            if (behaviours.Length > byte.MaxValue)
                throw new InvalidOperationException("Too many NetworkBehaviours on one object");
            for (int i = 0; i < behaviours.Length; i++) behaviours[i].Initialize(this, (byte)i);
        }

        internal void Shutdown()
        {
            if (behaviours != null)
                foreach (var behaviour in behaviours) behaviour.OnNetworkDespawn();
            Manager = null;
        }

        public void Despawn()
        {
            if (!IsSpawned) throw new InvalidOperationException("Object is not spawned");
            Manager.Despawn(this);
        }

        internal void WriteSnapshot(NetWriter writer)
        {
            writer.Write((byte)behaviours.Length);
            foreach (var behaviour in behaviours)
            {
                writer.Write(behaviour.GetType().FullName);
                using (var data = new NetWriter())
                {
                    behaviour.WriteSnapshot(data);
                    writer.WriteBytes(data.ToArray());
                }
            }
        }

        internal void ReadSnapshot(NetReader reader)
        {
            int count = reader.ReadByte();
            if (count != behaviours.Length)
                throw new InvalidOperationException($"Behaviour count differs for prefab {prefabId}: local {behaviours.Length}, remote {count}");
            for (int i = 0; i < count; i++)
            {
                string typeName = reader.ReadString();
                if (typeName != behaviours[i].GetType().FullName)
                    throw new InvalidOperationException($"Behaviour {i} differs for prefab {prefabId}: local {behaviours[i].GetType().FullName}, remote {typeName}");
                byte[] data = reader.ReadBytes();
                using (var content = new NetReader(data)) behaviours[i].ReadSnapshot(content);
            }
        }
    }
}
