using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AtlasNet.Editor
{
    [CustomEditor(typeof(NetworkManager))]
    [CanEditMultipleObjects]
    internal sealed class NetworkManagerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.LabelField("Network Settings", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("tickRate"), new GUIContent("Tick Rate"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("localAddress"), new GUIContent("Local Address"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("port"), new GUIContent("Port"));
            EditorGUILayout.HelpBox("Local TCP development mode. The first server coordinates clients and optional Unity workers; production AtlasNet transport selection is not connected yet.", MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Local Worker Regions", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("automaticLocalHandoffs"), new GUIContent("Automatic Handoffs"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("localWorldMin"), new GUIContent("World Minimum X/Z"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("localWorldMax"), new GUIContent("World Maximum X/Z"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("localBoundaryMargin"), new GUIContent("Boundary Margin"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Prefab Settings", EditorStyles.boldLabel);
            var player = serializedObject.FindProperty("playerPrefab");
            var lists = serializedObject.FindProperty("networkPrefabsLists");
            EditorGUILayout.PropertyField(player, new GUIContent("Default Player Prefab"));
            EditorGUILayout.PropertyField(lists, new GUIContent("Network Prefabs Lists"), true);
            if (!serializedObject.isEditingMultipleObjects)
            {
                if (lists.arraySize == 0)
                    EditorGUILayout.HelpBox("Assign a Network Prefabs List before spawning registered prefabs.", MessageType.Warning);
                else if (player.objectReferenceValue is NetworkObject playerObject && !IsRegistered(playerObject, lists))
                    EditorGUILayout.HelpBox("The default player prefab must also be present in an assigned Network Prefabs List.", MessageType.Warning);
            }
            serializedObject.ApplyModifiedProperties();

            if (!EditorApplication.isPlaying || targets.Length != 1) return;
            var manager = (NetworkManager)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Runtime", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Role", manager.IsWorker ? "Worker" : manager.IsHost ? "Host" : manager.IsServer ? "Server" : manager.IsClient ? "Client" : "Stopped");
                EditorGUILayout.LongField("Network Tick", manager.Tick);
                EditorGUILayout.IntField("Spawned Objects", manager.SpawnedCount);
                EditorGUILayout.IntField("Remote Clients", manager.RemoteClientCount);
                EditorGUILayout.TextField("Local Worker ID", manager.LocalWorkerId.ToString());
                EditorGUILayout.IntField("Workers", manager.WorkerCount);
                EditorGUILayout.IntField("Simulating Here", manager.LocalAuthorityCount);
                EditorGUILayout.IntField("Ghosts", manager.GhostCount);
                EditorGUILayout.IntField("Pending Handoffs", manager.PendingHandoffCount);
            }
            if (manager.IsRunning)
            {
                if (GUILayout.Button("Stop")) manager.Stop();
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Start Host")) manager.StartHost();
                    if (GUILayout.Button("Start Server")) manager.StartServer();
                    if (GUILayout.Button("Start Client")) manager.StartClient();
                    if (GUILayout.Button("Join Worker")) manager.StartWorker();
                }
            }
        }

        private static bool IsRegistered(NetworkObject player, SerializedProperty lists)
        {
            for (int i = 0; i < lists.arraySize; i++)
            {
                var list = lists.GetArrayElementAtIndex(i).objectReferenceValue as NetworkPrefabsList;
                if (list == null) continue;
                foreach (var prefab in list.Prefabs)
                    if (prefab == player) return true;
            }
            return false;
        }
    }

    [CustomEditor(typeof(NetworkObject))]
    [CanEditMultipleObjects]
    internal sealed class NetworkObjectEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            if (targets.Length != 1)
            {
                EditorGUILayout.HelpBox("Prefab identity is generated separately for each prefab asset.", MessageType.Info);
                return;
            }
            var networkObject = (NetworkObject)target;
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.TextField(new GUIContent("Prefab ID", "Generated from the prefab asset's Unity identity; distinct from Entity ID."),
                    string.IsNullOrWhiteSpace(networkObject.PrefabId) ? "Not a saved prefab asset" : networkObject.PrefabId);
            if (string.IsNullOrWhiteSpace(networkObject.PrefabId))
                EditorGUILayout.HelpBox("Save this as a prefab asset before adding it to a Network Prefabs List.", MessageType.Info);
            if (!EditorApplication.isPlaying) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Runtime", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.Toggle("Is Spawned", networkObject.IsSpawned);
                EditorGUILayout.TextField("Entity ID", networkObject.IsSpawned ? networkObject.EntityId.ToString() : "—");
                EditorGUILayout.TextField("Owner Session", networkObject.IsSpawned ? networkObject.OwnerSession.ToString() : "—");
                EditorGUILayout.TextField("Simulation Worker", networkObject.IsSpawned ? networkObject.SimulationWorker.ToString() : "—");
                EditorGUILayout.LongField("Authority Epoch", networkObject.IsSpawned ? networkObject.AuthorityEpoch : 0);
                EditorGUILayout.Toggle("Is Owner", networkObject.IsOwner);
                EditorGUILayout.Toggle("Has Simulation Authority", networkObject.HasAuthority);
                EditorGUILayout.ObjectField("Network Manager", networkObject.Manager, typeof(NetworkManager), true);
            }
        }
    }

    [CustomEditor(typeof(NetworkTransform))]
    [CanEditMultipleObjects]
    internal sealed class NetworkTransformEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("target"), new GUIContent("Target"));
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Axes to Synchronize", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("syncPosition"), new GUIContent("Position"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("syncRotation"), new GUIContent("Rotation"));
            EditorGUILayout.HelpBox("AtlasNet currently synchronizes whole position and rotation values, not individual axes or scale.", MessageType.None);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Authority", EditorStyles.boldLabel);
            if (serializedObject.FindProperty("syncPosition").boolValue)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("positionWriter"), new GUIContent("Position Writer"));
            if (serializedObject.FindProperty("syncRotation").boolValue)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("rotationWriter"), new GUIContent("Rotation Writer"));
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Interpolation", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("interpolate"), new GUIContent("Interpolate Remote Copies"));
            serializedObject.ApplyModifiedProperties();
        }
    }

    [CustomEditor(typeof(NetworkAnimator))]
    [CanEditMultipleObjects]
    internal sealed class NetworkAnimatorEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var animator = serializedObject.FindProperty("animator");
            EditorGUILayout.PropertyField(animator, new GUIContent("Animator"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("writer"), new GUIContent("Authority Mode"));
            serializedObject.ApplyModifiedProperties();
            if (!serializedObject.isEditingMultipleObjects && animator.objectReferenceValue == null)
                EditorGUILayout.HelpBox("Assign an Animator, or AtlasNet will search children when this object spawns.", MessageType.Info);
            EditorGUILayout.HelpBox("Parameters and states are synchronized. Use NetworkAnimator.SetTrigger for transient triggers.", MessageType.None);
        }
    }

    [CustomEditor(typeof(NetworkPrefabsList))]
    [CanEditMultipleObjects]
    internal sealed class NetworkPrefabsListEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("prefabs"), new GUIContent("Network Prefabs"), true);
            serializedObject.ApplyModifiedProperties();
            if (serializedObject.isEditingMultipleObjects) return;
            var list = (NetworkPrefabsList)target;
            var ids = new HashSet<string>();
            for (int i = 0; i < list.Prefabs.Count; i++)
            {
                var prefab = list.Prefabs[i];
                if (prefab == null)
                    EditorGUILayout.HelpBox($"Entry {i} is missing a NetworkObject prefab.", MessageType.Warning);
                else if (string.IsNullOrWhiteSpace(prefab.PrefabId))
                    EditorGUILayout.HelpBox($"{prefab.name} has no generated Prefab ID. Reimport or resave its prefab asset.", MessageType.Warning);
                else if (!ids.Add(prefab.PrefabId))
                    EditorGUILayout.HelpBox($"Duplicate Prefab ID '{prefab.PrefabId}'. Each registered prefab needs a unique ID.", MessageType.Warning);
            }
        }
    }
}
