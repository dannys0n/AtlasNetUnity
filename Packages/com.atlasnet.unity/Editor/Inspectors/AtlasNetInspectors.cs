using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AtlasNet.Editor
{
    [CustomEditor(typeof(NetworkManager))]
    [CanEditMultipleObjects]
    internal sealed class NetworkManagerEditor : UnityEditor.Editor
    {
        private bool showTransportTuning;
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.LabelField("Network Settings", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("logLevel"), new GUIContent("Log Level"));
            bool running = false;
            if (EditorApplication.isPlaying)
                foreach (NetworkManager managerTarget in targets)
                    running |= managerTarget.IsRunning;

            using (new EditorGUI.DisabledScope(running))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("tickRate"), new GUIContent("Tick Rate"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("localAddress"), new GUIContent("Local Address"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("port"), new GUIContent("Port"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("serverTransport"),new GUIContent("Server Transport"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("clientTransport"),new GUIContent("Native Client Transport","Connection type used by non-browser clients. Web builds always use WebSocket. This does not change the server listeners."));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("listenAddress"),new GUIContent("Listen Address"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("transportProvider"),new GUIContent("Transport Override","Optional custom adapter; leave empty for built-in transport selection."));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("backendProvider"),new GUIContent("Backend Override","Leave empty for the dummy backend. This is an adapter extension, not a gameplay prefab requirement."));
                showTransportTuning = EditorGUILayout.Foldout(showTransportTuning, "Advanced Transport", true);
                if (showTransportTuning)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        var udp = serializedObject.FindProperty("udpDisconnectTimeoutMS");
                        var web = serializedObject.FindProperty("webSocketDisconnectTimeoutMS");
                        EditorGUILayout.PropertyField(udp, new GUIContent("UDP Idle Timeout (ms)", udp.tooltip));
                        EditorGUILayout.PropertyField(web, new GUIContent("WebSocket Idle Timeout (ms)", web.tooltip));
                    }
                }
                EditorGUILayout.HelpBox("Default server accepts UDP and WebSocket clients simultaneously on the same port number. TCP-only mode is optional. Browsers use WebSocket. An empty backend override uses the local dummy.",MessageType.None);

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Dummy Backend Partitioning", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("automaticLocalHandoffs"), new GUIContent("Automatic Handoffs"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("localPartitionCenter"), new GUIContent("Partition Center", "The coordinating server samples this Transform at session start."));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("localRegionDebugExtent"), new GUIContent("Debug View Extent", "Rendering crop only. Authority regions extend indefinitely."));
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
                EditorGUILayout.IntField("Tracked Entity IDs", manager.TrackedEntityCount);
                EditorGUILayout.IntField("Local Unity Replicas", manager.SpawnedCount);
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
        private bool showTuning;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var targetTransform = serializedObject.FindProperty("target");
            var syncPosition = serializedObject.FindProperty("syncPosition");
            var syncRotation = serializedObject.FindProperty("syncRotation");
            var interpolate = serializedObject.FindProperty("interpolate");

            using (new EditorGUI.DisabledGroupScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                EditorGUILayout.PropertyField(targetTransform);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("inLocalSpace"));

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Synchronization", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(syncPosition);
                EditorGUILayout.PropertyField(syncRotation);
                DrawAuthority(syncPosition, syncRotation);

                EditorGUILayout.Space();
                EditorGUILayout.PropertyField(interpolate, new GUIContent("Interpolate Remote Copies", interpolate.tooltip));
                showTuning = EditorGUILayout.Foldout(showTuning, "Network Tuning", true);
                if (showTuning)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("sendIntervalTicks"));
                    if (AnyEnabled(syncPosition))
                        EditorGUILayout.PropertyField(serializedObject.FindProperty("positionThreshold"));
                    if (AnyEnabled(syncRotation))
                        EditorGUILayout.PropertyField(serializedObject.FindProperty("rotationThreshold"));
                    if (AnyEnabled(interpolate))
                    {
                        EditorGUILayout.PropertyField(serializedObject.FindProperty("interpolationBufferTicks"));
                        if (AnyEnabled(syncPosition))
                        {
                            var snap = serializedObject.FindProperty("snapOnLargeMove");
                            EditorGUILayout.PropertyField(snap);
                            if (AnyEnabled(snap))
                                EditorGUILayout.PropertyField(serializedObject.FindProperty("snapDistance"));
                        }
                    }
                    EditorGUI.indentLevel--;
                }
            }

            serializedObject.ApplyModifiedProperties();

            if (!serializedObject.isEditingMultipleObjects)
            {
                var syncedTransform = targetTransform.objectReferenceValue as Transform;
                if (syncedTransform == null) syncedTransform = ((NetworkTransform)target).transform;
                if (syncedTransform.TryGetComponent<PredictedBehaviour>(out _))
                    EditorGUILayout.HelpBox("Prediction already synchronizes this transform. Remove NetworkTransform or choose a different target.", MessageType.Warning);
            }
        }

        private static readonly string[] AuthorityOptions = { "Server", "Owner", "Separate Position and Rotation" };

        private void DrawAuthority(SerializedProperty syncPosition, SerializedProperty syncRotation)
        {
            var position = serializedObject.FindProperty("positionWriter");
            var rotation = serializedObject.FindProperty("rotationWriter");
            if (!AnyEnabled(syncPosition) && !AnyEnabled(syncRotation)) return;

            bool mixed = position.hasMultipleDifferentValues || rotation.hasMultipleDifferentValues;
            int mode = position.intValue == rotation.intValue ? position.intValue : 2;
            bool previousMixed = EditorGUI.showMixedValue;
            EditorGUI.showMixedValue = mixed;
            EditorGUI.BeginChangeCheck();
            int selected = EditorGUILayout.Popup(new GUIContent("Authority Mode",
                "Choose who writes the synchronized pose. Separate authority lets position and rotation have different writers."),
                mode, AuthorityOptions);
            bool changed = EditorGUI.EndChangeCheck();
            EditorGUI.showMixedValue = previousMixed;

            if (changed)
            {
                position.intValue = selected == 2 ? (int)TransformWriter.Server : selected;
                rotation.intValue = selected == 2 ? (int)TransformWriter.Owner : selected;
                mode = selected;
            }

            if (mode == 2 || (mixed && !changed))
            {
                if (AnyEnabled(syncPosition)) EditorGUILayout.PropertyField(position);
                if (AnyEnabled(syncRotation)) EditorGUILayout.PropertyField(rotation);
            }
        }

        private static bool AnyEnabled(SerializedProperty property) => property.boolValue || property.hasMultipleDifferentValues;
    }

    [CustomEditor(typeof(PredictedBehaviour), true)]
    [CanEditMultipleObjects]
    internal sealed class PredictedBehaviourEditor : UnityEditor.Editor
    {
        private bool showAdvanced;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            if (target is PredictedCharacterController)
                EditorGUILayout.LabelField("Movement", EditorStyles.boldLabel);

            DrawPropertiesExcluding(serializedObject, "m_Script", "visualRoot", "smoothLocalVisuals",
                "correctionSnapDistance", "snapshotIntervalTicks", "interpolateSpectators",
                "spectatorBufferTicks", "snapSpectatorsOnLargeMove", "spectatorSnapDistance");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Prediction", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                var visualRoot = Draw("visualRoot", "Visual Root");
                SerializedProperty smoothOwner;
                using (new EditorGUI.DisabledScope(!visualRoot.hasMultipleDifferentValues && visualRoot.objectReferenceValue == null))
                    smoothOwner = Draw("smoothLocalVisuals", "Smooth Owner Visuals");
                var interpolate = Draw("interpolateSpectators", "Interpolate Remote Copies");

                showAdvanced = EditorGUILayout.Foldout(showAdvanced, "Advanced", true);
                if (showAdvanced)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        Draw("snapshotIntervalTicks", "State Send Interval (Ticks)");
                        if (AnyEnabled(smoothOwner))
                            Draw("correctionSnapDistance", "Owner Correction Snap Distance");
                        if (AnyEnabled(interpolate))
                        {
                            Draw("spectatorBufferTicks", "Interpolation Buffer (Ticks)");
                            if (AnyEnabled(Draw("snapSpectatorsOnLargeMove", "Snap on Large Movement")))
                                Draw("spectatorSnapDistance", "Snap Distance");
                        }
                    }
                }
            }

            serializedObject.ApplyModifiedProperties();
            EditorGUILayout.HelpBox("The owner predicts movement. The server simulates and confirms it.", MessageType.Info);

            if (serializedObject.isEditingMultipleObjects) return;

            var controller = (PredictedBehaviour)target;
            if (controller.VisualRoot != null && controller.VisualRoot.parent != controller.transform)
                EditorGUILayout.HelpBox("Assign a direct child as the Visual Root.", MessageType.Warning);

            foreach (var networkTransform in controller.GetComponents<NetworkTransform>())
            {
                var settings = new SerializedObject(networkTransform);
                var poseTarget = settings.FindProperty("target").objectReferenceValue;
                if (poseTarget == null || poseTarget == controller.transform)
                {
                    EditorGUILayout.HelpBox("Prediction already synchronizes this root. Remove NetworkTransform or assign a different target.", MessageType.Warning);
                    break;
                }
            }
        }

        private SerializedProperty Draw(string name, string label)
        {
            var property = serializedObject.FindProperty(name);
            EditorGUILayout.PropertyField(property, new GUIContent(label, property.tooltip), false);
            return property;
        }

        private static bool AnyEnabled(SerializedProperty property) => property.boolValue || property.hasMultipleDifferentValues;
    }

    [CustomEditor(typeof(NetworkRigidbody))]
    [CanEditMultipleObjects]
    internal sealed class NetworkRigidbodyEditor : UnityEditor.Editor
    {
        private bool showAdvanced;

        public override void OnInspectorGUI()
        {
            if (targets.Length == 1)
                DrawPoseSetup((NetworkRigidbody)target);

            serializedObject.Update();
            showAdvanced = EditorGUILayout.Foldout(showAdvanced, "Advanced", true);
            if (showAdvanced)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("maxRemotePushSpeed"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("maxRemotePushForce"));
                }
            }
            serializedObject.ApplyModifiedProperties();
        }

        private static void DrawPoseSetup(NetworkRigidbody body)
        {
            var networkTransform = body.GetComponent<NetworkTransform>();
            if (networkTransform == null || body.GetComponent<Rigidbody>() == null || body.GetComponent<NetworkObject>() == null)
            {
                EditorGUILayout.HelpBox("Add a Rigidbody, NetworkObject, and NetworkTransform to this GameObject.", MessageType.Warning);
                return;
            }

            var settings = new SerializedObject(networkTransform);
            var poseTarget = settings.FindProperty("target");
            var syncPosition = settings.FindProperty("syncPosition");
            var syncRotation = settings.FindProperty("syncRotation");
            var positionWriter = settings.FindProperty("positionWriter");
            var rotationWriter = settings.FindProperty("rotationWriter");
            if (syncPosition.boolValue && syncRotation.boolValue &&
                positionWriter.intValue == rotationWriter.intValue &&
                (poseTarget.objectReferenceValue == null || poseTarget.objectReferenceValue == body.transform))
            {
                EditorGUILayout.LabelField("Physics Authority", ((TransformWriter)positionWriter.intValue).ToString());
                return;
            }

            EditorGUILayout.HelpBox("Sync this GameObject's position and rotation with the same writer in NetworkTransform.", MessageType.Warning);
            using (new EditorGUI.DisabledGroupScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (!GUILayout.Button("Configure NetworkTransform for Rigidbody")) return;

                poseTarget.objectReferenceValue = null;
                syncPosition.boolValue = true;
                syncRotation.boolValue = true;
                rotationWriter.intValue = positionWriter.intValue;
                settings.ApplyModifiedProperties();
            }
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
