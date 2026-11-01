using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AtlasNet.Demos
{
    // Local runtime demonstration harness. Each manager still uses the real transport/backend.
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
        LocalWorkerPanel panel;
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
                throw new ArgumentException("Select a server in this local instance.", nameof(manager));
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
            if (instance || Application.platform == RuntimePlatform.WebGLPlayer) return;
            var root = new GameObject("Local worker demonstration");
            DontDestroyOnLoad(root);
            instance = root.AddComponent<LocalWorkerDemo>();
        }
        void Awake()
        {
            panel = gameObject.AddComponent<LocalWorkerPanel>();
            panel.Initialize(this);
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

            if (!handoffsConfigured)
            {
                handoffsConfigured = true;
                originalHandoffs = primary.AutomaticLocalHandoffs;
                primary.SetLocalDemoHandoffs(true);
                epochs.Clear(); previousWorkers.Clear(); transfers = 0;
            }
            var world = new WorkerWorld();
            world.Scene = SceneManager.CreateScene($"Embedded worker {Guid.NewGuid():N}-{worlds.Count + 1}", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            var root = new GameObject($"Embedded worker {++workerSerial}");
            SceneManager.MoveGameObjectToScene(root, world.Scene);
            world.Manager = root.AddComponent<NetworkManager>();
            world.Manager.ConfigureLocalDemoWorker(primary);
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
                Collider collider;
                if (source is BoxCollider box) { var c=clone.AddComponent<BoxCollider>(); c.center=box.center; c.size=box.size; collider=c; }
                else if (source is SphereCollider sphere) { var c=clone.AddComponent<SphereCollider>(); c.center=sphere.center; c.radius=sphere.radius; collider=c; }
                else if (source is CapsuleCollider capsule) { var c=clone.AddComponent<CapsuleCollider>(); c.center=capsule.center; c.radius=capsule.radius; c.height=capsule.height; c.direction=capsule.direction; collider=c; }
                else if (source is MeshCollider mesh) { var c=clone.AddComponent<MeshCollider>(); c.sharedMesh=mesh.sharedMesh; c.convex=mesh.convex; collider=c; }
                else if (source is TerrainCollider terrain) { var c=clone.AddComponent<TerrainCollider>(); c.terrainData=terrain.terrainData; collider=c; }
                else { Destroy(clone); continue; }
                collider.isTrigger=source.isTrigger; collider.sharedMaterial=source.sharedMaterial;
                collider.contactOffset=source.contactOffset;
                collider.includeLayers=source.includeLayers; collider.excludeLayers=source.excludeLayers;
                collider.layerOverridePriority=source.layerOverridePriority;
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
                    renderer.sharedMaterial = template.RegionMaterial;
                    overlay = root.AddComponent<ScaleRegionOverlay>();
                    overlay.Configure(mesh, renderer);
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
                primary.SetLocalDemoHandoffs(originalHandoffs);
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
        public bool Expanded => expanded;
        public void ToggleExpanded() => expanded = !expanded;
        public string Status => status;
        public string[] Rows => rows;
        public NetworkManager[] Servers => new[] { primary }.Concat(worlds.Select(w => w.Manager)).Where(m => m && m.IsRunning).ToArray();
        public bool IsDraining(NetworkManager manager) => worlds.Any(w => w.Manager == manager && w.Draining);
        public void RunAction(Action action)
        {
            try { action(); } catch (Exception ex) { status=ex.Message; Debug.LogException(ex); }
        }
    }
}
