using System;
using AtlasNet;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Put this beside NetworkManager. Start a host in one Play Mode window and clients in the others.</summary>
public sealed class DemoLauncher : MonoBehaviour
{
    [SerializeField] private NetworkManager manager;
    [SerializeField] private Camera overviewCamera;
    [SerializeField, HideInInspector] private NetworkObject playerPrefab; // Older imported samples stored this here.
    private string error;
    private string joinAddress,joinPort;
    private int joinTransport;
    private NetworkObject extra;
    private bool reportedOwner;
    [SerializeField] private bool advancedDiagnostics = true;
    private bool autoFire;
    private bool logMetrics;
    private float nextMetrics;
    private NetworkObject localPlayer;

    private void Awake()
    {
        Application.runInBackground = true;
        if (manager == null) manager = GetComponent<NetworkManager>();
        joinAddress=manager.Address;joinPort=manager.Port.ToString();
        joinTransport=Application.platform==RuntimePlatform.WebGLPlayer?1:(int)manager.ClientConnection;
        bool crossServer = SceneManager.GetActiveScene().name.Contains("Authority") ||
            (SceneManager.GetActiveScene().name == "04_Physics" || SceneManager.GetActiveScene().name == "07_ClientPhysics");
        manager.PlayerSpawnPosition = crossServer
            ? session => (session.Value & 1) == 1 ? new Vector3(2, 1, 2) : new Vector3(-6, 1, -6)
            : session => new Vector3((session.Value - 1) * 2.5f, advancedDiagnostics ? 1 : 0, 0);
    }

    private void OnEnable() => manager.SessionJoined += OnSessionJoined;
    private void OnDisable() => manager.SessionJoined -= OnSessionJoined;

    private void Start()
    {
        string[] args = Environment.GetCommandLineArgs();
        autoFire = Array.IndexOf(args, "-atlas-test-rpc") >= 0;
        logMetrics = Array.IndexOf(args, "-atlas-metrics") >= 0;
        foreach (string arg in args)
        {
            if(arg.StartsWith("-atlas-address=",StringComparison.Ordinal))joinAddress=arg.Substring("-atlas-address=".Length);
            if(arg.StartsWith("-atlas-port=",StringComparison.Ordinal))joinPort=arg.Substring("-atlas-port=".Length);
            if(arg.StartsWith("-atlas-transport=",StringComparison.Ordinal)&&Enum.TryParse<ClientTransport>(arg.Substring("-atlas-transport=".Length),true,out var transport))joinTransport=(int)transport;
            if (!arg.StartsWith("-atlas-scene=", StringComparison.Ordinal)) continue;
            string scene = arg.Substring("-atlas-scene=".Length);
            if (SceneManager.GetActiveScene().name != scene)
            {
                SceneManager.LoadScene(scene);
                return;
            }
        }
        if(Application.platform==RuntimePlatform.WebGLPlayer)
        {
            var uri=new Uri(Application.absoluteURL);bool directJoin=false;
            foreach(var item in uri.Query.TrimStart('?').Split('&'))
            {
                var pair=item.Split('=');if(pair.Length!=2)continue;
                if(pair[0]=="host"){joinAddress=Uri.UnescapeDataString(pair[1]);directJoin=true;}
                if(pair[0]=="port")joinPort=pair[1];
            }
            if(directJoin){StartSafely(manager.StartClient);return;}
        }
        if (Array.IndexOf(args, "-atlas-host") >= 0) StartSafely(manager.StartHost);
        else if (Array.IndexOf(args, "-atlas-server") >= 0) StartSafely(manager.StartServer);
        else if (Array.IndexOf(args, "-atlas-client") >= 0) StartSafely(manager.StartClient);
        else if (Array.IndexOf(args, "-atlas-worker") >= 0) StartSafely(manager.StartWorker);
    }

    private void OnSessionJoined(SessionId session)
    {
        if (manager.ShouldLog(LogType.Log)) manager.Log(LogType.Log, $"AtlasNet session {session} joined; local role {(manager.IsServer ? "server" : "client")}");
        if (manager.IsServer && manager.PlayerPrefab == null && playerPrefab != null)
            manager.Spawn(playerPrefab, manager.PlayerSpawnPosition(session), playerPrefab.transform.rotation, session);
    }

    private void StartSafely(Action action)
    {
        try
        {
            if(!manager.IsRunning)
            {
                if(!int.TryParse(joinPort,out int value))throw new ArgumentException("Enter a valid port.");
                manager.ConfigureConnection(joinAddress,value,(ClientTransport)joinTransport,manager.ServerConnection);
            }
            action(); error = null;
        }
        catch (Exception exception) { error = exception.Message; if (manager.ShouldLog(LogType.Exception)) Debug.LogException(exception, manager); }
    }

    private void Update()
    {
        if (!manager.IsRunning)
        {
            reportedOwner = false; localPlayer = null; extra = null;
            if (!string.IsNullOrEmpty(manager.DisconnectReason)) error = manager.DisconnectReason;
        }
        if (overviewCamera != null)
        {
            bool overview = !manager.IsRunning || manager.IsServer || !advancedDiagnostics;
            overviewCamera.enabled = overview;
            if (overviewCamera.TryGetComponent<AudioListener>(out var listener))
                listener.enabled = overview && manager.IsRunning;
        }
        if (!reportedOwner && manager.IsClient)
        {
            foreach (var obj in manager.SpawnedObjects)
            {
                if (!obj.IsOwner) continue;
                localPlayer = obj;
                if (manager.ShouldLog(LogType.Log)) manager.Log(LogType.Log, $"AtlasNet local player spawned: entity {obj.EntityId}, prefab {obj.PrefabId}");
                if (autoFire) obj.GetComponent<SimpleRpcExample>()?.Fire();
                reportedOwner = true;
                break;
            }
        }
        if (manager.IsRunning && logMetrics && Time.unscaledTime >= nextMetrics)
        {
            nextMetrics = Time.unscaledTime + 5f;
            if (manager.ShouldLog(LogType.Log)) manager.Log(LogType.Log, $"AtlasNet metrics: role={(manager.IsWorker ? "worker" : manager.IsServer ? "server" : "client")} worker={manager.LocalWorkerId} tick={manager.Tick} tracked={manager.TrackedEntityCount} localReplicas={manager.SpawnedCount} authority={manager.LocalAuthorityCount} ghosts={manager.GhostCount} pending={manager.PendingHandoffCount} observers={manager.ObserverCopies} tickMs={manager.LastTickMilliseconds:F3} allocated={AllocationText()} bytesLastTick={manager.BytesSentLastTick}");
        }
        if (!manager.IsServer || manager.IsWorker) return;
        if (manager.PlayerPrefab != null || playerPrefab != null)
        {
            if (Input.GetKeyDown(KeyCode.P)) SpawnExtra();
            if (Input.GetKeyDown(KeyCode.O)) DespawnExtra();
        }
    }

    private void SpawnExtra()
    {
        if (extra == null)
            extra = manager.Spawn(manager.PlayerPrefab != null ? manager.PlayerPrefab : playerPrefab,
                new Vector3(0, 1, 5), Quaternion.identity);
    }

    private void DespawnExtra()
    {
        if (extra != null) extra.Despawn();
        extra = null;
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 420, 480), GUI.skin.box);
        GUILayout.Label(SceneManager.GetActiveScene().name.Replace("_", " "));
        GUILayout.Label("WASD: move | F: RPC counter | P/O: spawn/despawn extra");
        if (SceneManager.GetActiveScene().name == "07_ClientPhysics")
            GUILayout.Label("Client simulates its Rigidbody. Cubes stay server-authoritative.");
        if (SceneManager.GetActiveScene().name.Contains("AnimatorSync")) GUILayout.Label("Space: wave (replicated Animator trigger)");
        if (!manager.IsRunning)
        {
            GUILayout.Label("Server address / port");
            joinAddress=GUILayout.TextField(joinAddress);joinPort=GUILayout.TextField(joinPort);
            if(Application.platform==RuntimePlatform.WebGLPlayer){joinTransport=1;GUILayout.Label("Browser: WebSocket / TCP");}
            else joinTransport=GUILayout.Toolbar(joinTransport,new[]{"UDP","WebSocket","TCP"});
            GUILayout.Label("Default server accepts UDP + WebSocket together.");
            if (advancedDiagnostics) GUILayout.Label("Worker handoffs use the development dummy backend.");
            if (SceneManager.GetActiveScene().name == "05_InterestAndScale")
            {
                if (GUILayout.Button("Start World (Server)")) StartSafely(manager.StartServer);
            }
            else
            {
                if (GUILayout.Button("Start Host")) StartSafely(manager.StartHost);
                if (GUILayout.Button("Start Server Only")) StartSafely(manager.StartServer);
            }
            if (GUILayout.Button("Join as Client")) StartSafely(manager.StartClient);
            if (advancedDiagnostics && GUILayout.Button("Join as Worker")) StartSafely(manager.StartWorker);
        }
        else
        {
            GUILayout.Label($"Role: {(manager.IsWorker ? "Worker" : manager.IsServer ? (manager.IsClient ? "Host" : "Server") : "Client")}");
            GUILayout.Label($"Session: {manager.LocalSession}   Tick: {manager.Tick}");
            if (advancedDiagnostics)
            {
                GUILayout.Label($"Worker: {manager.LocalWorkerId}   Workers: {manager.WorkerCount}");
                if (manager.AutomaticLocalHandoffs)
                {
                    GUILayout.Label($"Region handoffs: {(manager.AutomaticLocalHandoffs ? "automatic" : "off")}");
                    GUILayout.Label($"Debug region vertices: {manager.LocalRegion.Count}");
                }
                if (localPlayer != null && localPlayer.IsSpawned)
                {
                    GUILayout.Label($"Player: {localPlayer.EntityId}   Prefab: {localPlayer.PrefabId}");
                    var interest = localPlayer.GetComponent<NetworkInterestSource>();
                    if (interest != null) GUILayout.Label($"Player interest radius: {interest.Radius:F1} (exit +{interest.ExitPadding:F1})");
                    GUILayout.Label($"Input session: {localPlayer.OwnerSession}   Simulation worker: {localPlayer.SimulationWorker}   Epoch: {localPlayer.AuthorityEpoch}");
                    var movement = localPlayer.GetComponent<NetworkTransform>();
                    if (movement != null)
                        GUILayout.Label($"Transform writers: position {movement.PositionWriter}, rotation {movement.RotationWriter}");
                }
                GUILayout.Label($"Tracked IDs: {manager.TrackedEntityCount}   Local replicas: {manager.SpawnedCount}   Observers: {manager.ObserverCopies}");
                GUILayout.Label($"Simulating here: {manager.LocalAuthorityCount}   Ghosts: {manager.GhostCount}");
                if (manager.AutomaticLocalHandoffs && manager.IsServer)
                    GUILayout.Label($"Interested ghost visuals: {InterestedGhosts()}");
                foreach (var obj in manager.SpawnedObjects)
                    if (obj != null && obj.AuthorityEpoch > 0)
                    {
                        GUILayout.Label($"Handoff entity {obj.EntityId}: worker {obj.SimulationWorker}, epoch {obj.AuthorityEpoch}");
                        break;
                    }
                GUILayout.Label($"Tick: {manager.LastTickMilliseconds:F2} ms   Alloc: {AllocationText()}");
                GUILayout.Label($"Sent: {manager.BytesSentLastTick} B/tick   Peers: {manager.RemoteClientCount}");
                GUILayout.Label($"Pending handoffs: {manager.PendingHandoffCount}");
                logMetrics = GUILayout.Toggle(logMetrics, "Log metrics every 5 seconds");
                if (manager.IsServer && !manager.IsWorker && TryGetComponent<ScaleSpawner>(out var scale))
                {
                    GUILayout.Label("Transfers: RED exiting | GREEN entering");
                    GUILayout.Label($"Scale entities: {scale.Count}");
                    scale.Count = Mathf.RoundToInt(GUILayout.HorizontalSlider(scale.Count, 0, 200));
                }
            }
            if (!advancedDiagnostics)
            {
                GUILayout.Label($"Players / replicas: {manager.SpawnedCount}");
                foreach (var obj in manager.SpawnedObjects)
                {
                    var counter = obj.GetComponent<SimpleRpcExample>();
                    if (counter != null) GUILayout.Label($"Entity {obj.EntityId}: RPC count {counter.Count}");
                }
                if (localPlayer != null && localPlayer.TryGetComponent<SampleAnimatorInput>(out var animationInput))
                    if (GUILayout.Button("Wave (Space)")) animationInput.Wave();
            }
            if (manager.IsServer && !manager.IsWorker && (manager.PlayerPrefab != null || playerPrefab != null))
            {
                if (GUILayout.Button("Spawn extra (P)")) SpawnExtra();
                if (GUILayout.Button("Despawn extra (O)")) DespawnExtra();
            }
            if (manager.IsClient && !manager.IsServer && TryGetComponent<ClientBoundaryOverlay>(out var boundaries))
                boundaries.Visible = GUILayout.Toggle(boundaries.Visible, "Show boundary walls (client debug)");
            if (GUILayout.Button("Stop")) manager.Stop();
        }
        if (!string.IsNullOrEmpty(error)) GUILayout.Label(error);
        GUILayout.EndArea();
    }

    private string AllocationText() => manager.AllocatedBytesLastTick < 0
        ? "unavailable" : $"{manager.AllocatedBytesLastTick} B";

    private int InterestedGhosts()
    {
        int count = 0;
        foreach (var obj in manager.SpawnedObjects)
            if (obj != null && !obj.HasAuthority && manager.ShouldRenderWorkerCopyForDebug(obj)) count++;
        return count;
    }

}
