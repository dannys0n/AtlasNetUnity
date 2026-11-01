#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AtlasNet.Demos
{
    // Editor-only demonstration harness. Each manager still uses the real transport/backend.
    // Local PhysX scenes prevent duplicate replicas from interacting across workers.
    [DefaultExecutionOrder(10000)]
    public sealed class LocalWorkerDemo : MonoBehaviour
    {
        sealed class WorkerWorld
        {
            public NetworkManager Manager;
            public Scene Scene;
            public GameObject Geometry;
            public bool Draining;
            public readonly HashSet<NetworkObject> Prepared = new();
        }
        static T[] Instances<T>() where T : UnityEngine.Object
        {
#if UNITY_6000_6_OR_NEWER
            return FindObjectsByType<T>();
#else
            return FindObjectsByType<T>(FindObjectsSortMode.None);
#endif
        }
        static LocalWorkerDemo instance;
        readonly List<WorkerWorld> worlds = new();
        readonly Dictionary<EntityId, uint> epochs = new();
        readonly Dictionary<EntityId, ulong> previousWorkers = new();
        readonly Dictionary<ScaleRegionOverlay, NetworkManager> originalRegions = new();
        readonly Dictionary<Renderer, bool> originalVisibility = new();
        readonly HashSet<NetworkManager> visibleServers = new();
        readonly Dictionary<NetworkManager, ScaleRegionOverlay> extraRegions = new();
        readonly Dictionary<EntityId, NetworkObject> displayed = new();
        NetworkManager primary;
        bool selectionInitialized, handoffsConfigured;
        int workerSerial;
        Rect window;
        Vector2 scroll;
        bool expanded = true, geometryDirty;
        bool originalHandoffs;
        float nextRefresh;
        int transfers;
        string status = "Start a local Host or Server first.";
        string[] rows = Array.Empty<string>();
        public NetworkManager Primary => primary;
        public NetworkManager InspectedServer => visibleServers.FirstOrDefault(m => m) ?? primary;
        public bool IsServerVisible(NetworkManager manager) => visibleServers.Contains(manager);
        public void SetServerVisible(NetworkManager manager, bool visible)
        {
            if (manager != primary && !worlds.Any(w => w.Manager == manager))
                throw new ArgumentException("Select a server in this Editor instance.", nameof(manager));
            if (visible) visibleServers.Add(manager); else visibleServers.Remove(manager);
            selectionInitialized = true;
            epochs.Clear(); previousWorkers.Clear(); transfers = 0; nextRefresh = 0;
        }
        public void InspectServer(NetworkManager manager)
        {
            visibleServers.Clear(); SetServerVisible(manager, true);
        }
        public bool RemoveWorker(NetworkManager manager)
        {
            var world = worlds.FirstOrDefault(w => w.Manager == manager);
            if (world == null || world.Draining || manager.LocalWorkerId == ulong.MaxValue) return false;
            if (!primary.BeginLocalWorkerDrain(manager.LocalWorkerId)) return false;
            world.Draining = true;
            return true;
        }
        void FinishDrains()
        {
            foreach (var world in worlds.ToArray())
            {
                if (!world.Draining || !primary.IsLocalWorkerDrained(world.Manager.LocalWorkerId) ||
                    world.Manager.LocalAuthorityCount != 0 || world.Manager.PendingHandoffCount != 0) continue;
                visibleServers.Remove(world.Manager);
                if (extraRegions.Remove(world.Manager, out var region) && region) Destroy(region.gameObject);
                world.Manager.Stop();
                worlds.Remove(world);
                if (world.Scene.IsValid() && world.Scene.isLoaded) SceneManager.UnloadSceneAsync(world.Scene);
                nextRefresh = 0;
            }
        }
        public int ExtraWorkerCount => worlds.Count;
        public int ObservedTransfers => transfers;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatic() => instance = null;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (instance) return;
            var root = new GameObject("Local worker demonstration (Editor only)");
            DontDestroyOnLoad(root);
            instance = root.AddComponent<LocalWorkerDemo>();
        }
        void Awake()
        {
            window = new Rect(Mathf.Max(10, Screen.width - 370), Mathf.Max(10, Screen.height - 415), 360, 260);
            SceneManager.sceneLoaded += SceneLoaded;
            SceneManager.sceneUnloaded += SceneUnloaded;
        }
        void SceneLoaded(Scene scene, LoadSceneMode mode) { if (scene.GetPhysicsScene() == Physics.defaultPhysicsScene) geometryDirty = true; }
        void SceneUnloaded(Scene scene) => geometryDirty = true;
        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8)) expanded = !expanded;
            if (worlds.Count > 0 && (!primary || !primary.IsRunning)) ReleaseWorkers();
            if (primary && primary.IsRunning) FinishDrains();
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + .25f;
            if (!primary || !primary.IsRunning)
                primary = Instances<NetworkManager>()
                    .FirstOrDefault(m => !worlds.Any(w => w.Manager == m) && m.IsRunning && m.IsServer && !m.IsWorker);
            if (!primary || !primary.IsRunning) return;
            if (!selectionInitialized) { visibleServers.Add(primary); selectionInitialized = true; }
            foreach (var renderer in originalVisibility.Keys.Where(r => !r).ToArray()) originalVisibility.Remove(renderer);
            var selected = visibleServers.Where(m => m && m.IsRunning).ToArray();
            var workerIds = new HashSet<ulong>(selected.Select(m => m.LocalWorkerId));
            rows = selected.Select(m => $"Server {m.LocalWorkerId}: {m.LocalAuthorityCount} authority / {m.GhostCount} ghosts / {m.PendingHandoffCount} pending").ToArray();
            foreach (var manager in selected)
                foreach (var obj in manager.SpawnedObjects)
                {
                    if (epochs.TryGetValue(obj.EntityId, out uint old) && obj.AuthorityEpoch > old &&
                        (workerIds.Contains(previousWorkers[obj.EntityId]) || workerIds.Contains(obj.SimulationWorker))) transfers++;
                    if (!epochs.TryGetValue(obj.EntityId, out old) || obj.AuthorityEpoch > old)
                    { epochs[obj.EntityId] = obj.AuthorityEpoch; previousWorkers[obj.EntityId] = obj.SimulationWorker; }
                }
            status = $"{selected.Length} server views | {transfers} local transfers observed";
        }
        public void AddWorker()
        {
            if (!primary || !primary.IsRunning || !primary.IsServer || primary.IsWorker)
                throw new InvalidOperationException("Start a local coordinating Host or Server first.");
            if (worlds.Count >= 3) return;
            var config = new SerializedObject(primary);
            if (config.FindProperty("backendProvider").objectReferenceValue)
                throw new InvalidOperationException("This demo harness is for the default dummy backend.");
            if (!handoffsConfigured)
            {
                handoffsConfigured = true;
                originalHandoffs = primary.AutomaticLocalHandoffs;
                config.FindProperty("automaticLocalHandoffs").boolValue = true;
                config.ApplyModifiedPropertiesWithoutUndo();
                epochs.Clear(); previousWorkers.Clear(); transfers = 0;
            }
            var world = new WorkerWorld();
            world.Scene = SceneManager.CreateScene($"Embedded worker {Guid.NewGuid():N}-{worlds.Count + 1}", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            var root = new GameObject($"Embedded worker {++workerSerial}");
            SceneManager.MoveGameObjectToScene(root, world.Scene);
            world.Manager = root.AddComponent<NetworkManager>();
            var target = new SerializedObject(world.Manager);
            foreach (string field in new[] { "playerPrefab", "networkPrefabsLists", "tickRate", "logLevel", "localPartitionCenter", "localRegionDebugExtent", "localBoundaryMargin", "transportProvider" })
                target.CopyFromSerializedProperty(config.FindProperty(field));
            target.ApplyModifiedPropertiesWithoutUndo();
            world.Manager.ConfigureConnection("127.0.0.1", primary.Port);
            world.Manager.ValidateRegistry();
            worlds.Add(world);
            try { BuildGeometry(world); world.Manager.StartWorker(); }
            catch { primary.Stop(); ReleaseWorkers(); throw; }
        }
        void BuildGeometry(WorkerWorld world)
        {
            if (world.Geometry) { world.Geometry.SetActive(false); Destroy(world.Geometry); }
            world.Geometry = new GameObject("Static collision environment (no visuals)");
            SceneManager.MoveGameObjectToScene(world.Geometry, world.Scene);
            foreach (var source in Instances<Collider>())
            {
                if (!source.enabled || source.gameObject.scene.GetPhysicsScene() != Physics.defaultPhysicsScene ||
                    source.GetComponentInParent<NetworkObject>() || source.attachedRigidbody) continue;
                var clone = new GameObject(source.name);
                clone.layer = source.gameObject.layer;
                clone.transform.SetParent(world.Geometry.transform);
                clone.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                clone.transform.localScale = source.transform.lossyScale;
                var collider = (Collider)clone.AddComponent(source.GetType());
                EditorUtility.CopySerialized(source, collider);
            }
        }
        void PrepareWorlds()
        {
            if (geometryDirty)
            {
                geometryDirty = false;
                foreach (var world in worlds) BuildGeometry(world);
            }
            foreach (var world in worlds)
            {
                if (!world.Manager || !world.Manager.IsRunning) continue;
                foreach (var obj in world.Manager.SpawnedObjects)
                {
                    if (obj.gameObject.scene != world.Scene)
                        SceneManager.MoveGameObjectToScene(obj.gameObject, world.Scene);
                    if (!world.Prepared.Add(obj)) continue;
                    // Worker replicas have no local input/cameras/audio. Their simulation remains active.
                    foreach (var camera in obj.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
                    foreach (var audio in obj.GetComponentsInChildren<AudioListener>(true)) audio.enabled = false;
                }
                world.Prepared.RemoveWhere(o => !o);
            }
        }
        void FixedUpdate()
        {
            if (worlds.Count == 0) return;
            PrepareWorlds();
            // At this execution order, actors have already queued their FixedUpdate forces.
            foreach (var world in worlds)
                if (world.Manager && world.Manager.IsRunning) world.Scene.GetPhysicsScene().Simulate(Time.fixedDeltaTime);
        }
        // Pick one replica per entity, favoring a selected server's departure/arrival
        // feedback, then selected authority, then a resident ghost. This avoids overlap.
        static int DisplayPriority(NetworkObject obj)
        {
            var state = obj.Manager.GetHandoffStateForDebug(obj);
            return state == HandoffDebugState.Exiting ? 3 : state == HandoffDebugState.Entering ? 2 : obj.HasAuthority ? 1 : 0;
        }
        void LateUpdate()
        {
            if (!primary || !primary.IsRunning) return;
            PrepareWorlds();
            var managers = new[] { primary }.Concat(worlds.Select(w => w.Manager)).Where(m => m).ToArray();
            displayed.Clear();
            foreach (var manager in managers)
            {
                if (!visibleServers.Contains(manager)) continue;
                foreach (var obj in manager.SpawnedObjects)
                {
                    if (manager.IsServer && !manager.ShouldRenderWorkerCopyForDebug(obj)) continue;
                    if (!displayed.TryGetValue(obj.EntityId, out var old) || DisplayPriority(obj) > DisplayPriority(old)) displayed[obj.EntityId] = obj;
                }
            }
            foreach (var manager in managers)
                foreach (var obj in manager.SpawnedObjects)
                    foreach (var renderer in obj.GetComponentsInChildren<Renderer>(true).Concat(
                        obj.GetComponentsInChildren<ScaleInterestOverlay>(true).Select(o => o.DebugRenderer).Where(r => r)))
                    {
                        if (!originalVisibility.ContainsKey(renderer)) originalVisibility.Add(renderer, renderer.forceRenderingOff);
                        renderer.forceRenderingOff = originalVisibility[renderer] || !displayed.TryGetValue(obj.EntityId, out var winner) || winner != obj;
                    }
            UpdateRegions();
        }
        void UpdateRegions()
        {
            // Original scene overlays describe the coordinator. Embedded workers receive
            // their own small overlay object; no environment or gameplay objects are cloned.
            foreach (var overlay in Instances<ScaleRegionOverlay>())
            {
                if (extraRegions.Values.Contains(overlay)) continue;
                if (!originalRegions.ContainsKey(overlay)) originalRegions.Add(overlay, overlay.InspectedManager);
                overlay.InspectManager(primary);
                var renderer = overlay.DebugRenderer;
                if (!renderer) continue;
                if (!originalVisibility.ContainsKey(renderer)) originalVisibility.Add(renderer, renderer.forceRenderingOff);
                renderer.forceRenderingOff = originalVisibility[renderer] || !visibleServers.Contains(primary);
            }
            var template = originalRegions.Keys.FirstOrDefault(o => o);
            if (!template) return;
            foreach (var world in worlds)
            {
                if (!extraRegions.TryGetValue(world.Manager, out var overlay) || !overlay)
                {
                    var root = new GameObject("Worker region overlay"); DontDestroyOnLoad(root);
                    var mesh = root.AddComponent<MeshFilter>();
                    var renderer = root.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = new SerializedObject(template).FindProperty("regionRenderer").objectReferenceValue is MeshRenderer source ? source.sharedMaterial : null;
                    overlay = root.AddComponent<ScaleRegionOverlay>();
                    var config = new SerializedObject(overlay);
                    config.FindProperty("regionMeshFilter").objectReferenceValue = mesh;
                    config.FindProperty("regionRenderer").objectReferenceValue = renderer;
                    config.ApplyModifiedPropertiesWithoutUndo();
                    overlay.InspectManager(world.Manager);
                    extraRegions[world.Manager] = overlay;
                }
                overlay.GetComponent<MeshRenderer>().forceRenderingOff = !visibleServers.Contains(world.Manager);
            }
        }
        public void StopSession()
        {
            if (primary && primary.IsRunning) primary.Stop();
            ReleaseWorkers();
        }
        void ReleaseWorkers()
        {
            foreach (var pair in originalVisibility) if (pair.Key) pair.Key.forceRenderingOff = pair.Value;
            originalVisibility.Clear();
            foreach (var pair in originalRegions) if (pair.Key) pair.Key.InspectManager(pair.Value);
            originalRegions.Clear();
            foreach (var overlay in extraRegions.Values) if (overlay) Destroy(overlay.gameObject);
            extraRegions.Clear(); visibleServers.Clear(); displayed.Clear(); selectionInitialized = false;
            foreach (var world in worlds)
            {
                if (world.Manager) world.Manager.Stop();
                if (world.Scene.IsValid() && world.Scene.isLoaded) SceneManager.UnloadSceneAsync(world.Scene);
            }
            if (primary && handoffsConfigured)
            {
                var config = new SerializedObject(primary);
                config.FindProperty("automaticLocalHandoffs").boolValue = originalHandoffs;
                config.ApplyModifiedPropertiesWithoutUndo();
            }
            handoffsConfigured = false;
            worlds.Clear(); epochs.Clear(); previousWorkers.Clear(); rows = Array.Empty<string>(); transfers = 0;
            status = "Start a local Host or Server first.";
        }
        void OnDestroy()
        {
            SceneManager.sceneLoaded -= SceneLoaded; SceneManager.sceneUnloaded -= SceneUnloaded;
            // Shut down the coordinator before removing its workers, including during domain reload.
            if (worlds.Count > 0) StopSession(); else ReleaseWorkers();
            if (instance == this) instance = null;
        }
        void OnGUI()
        {
            window.width = Mathf.Min(360, Screen.width - 20);
            window.x = Mathf.Clamp(window.x, 0, Mathf.Max(0, Screen.width - window.width));
            window.y = Mathf.Clamp(window.y, 0, Mathf.Max(0, Screen.height - 35));
            window.height = expanded ? 260 : 48;
            var color = GUI.color;
            GUI.color = new Color(.06f, .07f, .08f, 1f);
            GUI.DrawTexture(window, Texture2D.whiteTexture);
            GUI.color = color;
            window = GUI.Window(0x41a7, window, DrawWindow, "Local workers — Editor only (F8)");
        }
        void DrawWindow(int id)
        {
            if (GUILayout.Button(expanded ? "Collapse" : "Expand")) expanded = !expanded;
            if (expanded)
            {
                scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(200));
                GUILayout.Label(status);
                GUI.enabled = primary && primary.IsRunning && worlds.Count < 3;
                if (GUILayout.Button("Add local worker"))
                    try { AddWorker(); } catch (Exception ex) { status = ex.Message; Debug.LogException(ex); }
                GUI.enabled = worlds.Count > 0;
                if (GUILayout.Button("Stop whole local session")) StopSession();
                GUI.enabled = true;
                var servers = new[] { primary }.Concat(worlds.Select(w => w.Manager)).Where(m => m && m.IsRunning).ToArray();
                if (servers.Length > 0)
                {
                    GUILayout.Label("Visible servers (independent toggles):");
                    foreach (var server in servers)
                    {
                        GUILayout.BeginHorizontal();
                        bool wasVisible = IsServerVisible(server);
                        bool visible = GUILayout.Toggle(wasVisible, server.LocalWorkerId == ulong.MaxValue ? "Joining..." : $"Server {server.LocalWorkerId}", GUI.skin.button);
                        if (visible != wasVisible) SetServerVisible(server, visible);
                        var world = worlds.FirstOrDefault(w => w.Manager == server);
                        if (world != null)
                        {
                            GUI.enabled = !world.Draining && server.LocalWorkerId != ulong.MaxValue;
                            if (GUILayout.Button(world.Draining ? "Draining..." : "Remove", GUILayout.Width(95)))
                                try { RemoveWorker(server); } catch (Exception ex) { status = ex.Message; Debug.LogException(ex); }
                            GUI.enabled = true;
                        }
                        GUILayout.EndHorizontal();
                    }
                }
                foreach (string row in rows) GUILayout.Label(row);
                GUILayout.Label("Workers use isolated physics worlds and loopback UDP.\nOne Editor = one main thread, not a parallel CPU benchmark.");
                GUILayout.EndScrollView();
            }
            GUI.DragWindow(new Rect(0, 0, window.width, 20));
        }
    }
}
#endif
