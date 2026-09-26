using System;
using AtlasNet;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Put this beside NetworkManager. Start a host in one Play Mode window and clients in the others.</summary>
public sealed class DemoLauncher : MonoBehaviour
{
    [SerializeField] private NetworkManager manager;
    [SerializeField, HideInInspector] private NetworkObject playerPrefab; // Older imported samples stored this here.
    private string error;
    private NetworkObject extra;
    private bool reportedOwner;
    private bool autoFire;
    private bool logMetrics;
    private float nextMetrics;
    private NetworkObject localPlayer;

    private void Awake()
    {
        Application.runInBackground = true;
        if (manager == null) manager = GetComponent<NetworkManager>();
        bool crossServer = SceneManager.GetActiveScene().name.EndsWith("CrossServer", StringComparison.Ordinal);
        manager.PlayerSpawnPosition = crossServer
            ? session => session.Value == 1 ? new Vector3(2, 1, 2) : new Vector3(-6, 1, -6)
            : session => new Vector3((session.Value - 1) * 2.5f, 1, 0);
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
            if (!arg.StartsWith("-atlas-scene=", StringComparison.Ordinal)) continue;
            string scene = arg.Substring("-atlas-scene=".Length);
            if (SceneManager.GetActiveScene().name != scene)
            {
                SceneManager.LoadScene(scene);
                return;
            }
        }
        if (Array.IndexOf(args, "-atlas-host") >= 0) StartSafely(manager.StartHost);
        else if (Array.IndexOf(args, "-atlas-server") >= 0) StartSafely(manager.StartServer);
        else if (Array.IndexOf(args, "-atlas-client") >= 0) StartSafely(manager.StartClient);
        else if (Array.IndexOf(args, "-atlas-worker") >= 0) StartSafely(manager.StartWorker);
    }

    private void OnSessionJoined(SessionId session)
    {
        Debug.Log($"AtlasNet session {session} joined; local role {(manager.IsServer ? "server" : "client")}");
        if (manager.IsServer && manager.PlayerPrefab == null && playerPrefab != null)
            manager.Spawn(playerPrefab, manager.PlayerSpawnPosition(session), playerPrefab.transform.rotation, session);
    }

    private void StartSafely(Action action)
    {
        try { action(); error = null; }
        catch (Exception exception) { error = exception.Message; Debug.LogException(exception); }
    }

    private void Update()
    {
        if (!reportedOwner && manager.IsClient)
        {
            foreach (var obj in manager.SpawnedObjects)
            {
                if (!obj.IsOwner) continue;
                localPlayer = obj;
                Debug.Log($"AtlasNet local player spawned: entity {obj.EntityId}, prefab {obj.PrefabId}");
                if (autoFire) obj.GetComponent<SimpleRpcExample>()?.Fire();
                reportedOwner = true;
                break;
            }
        }
        if (!manager.IsServer || manager.IsWorker) return;
        if (logMetrics && Time.unscaledTime >= nextMetrics)
        {
            nextMetrics = Time.unscaledTime + 5f;
            Debug.Log($"AtlasNet metrics: entities={manager.SpawnedCount} observers={manager.ObserverCopies} tickMs={manager.LastTickMilliseconds:F3} allocated={AllocationText()} bytesLastTick={manager.BytesSentLastTick}");
        }
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
        GUILayout.Label("AtlasNet local demo");
        if (!manager.IsRunning)
        {
            if (SceneManager.GetActiveScene().name == "ScaleDemo")
            {
                if (GUILayout.Button("Start World (Server)")) StartSafely(manager.StartServer);
            }
            else
            {
                if (GUILayout.Button("Start Host")) StartSafely(manager.StartHost);
                if (GUILayout.Button("Start Server Only")) StartSafely(manager.StartServer);
            }
            if (GUILayout.Button("Join as Client")) StartSafely(manager.StartClient);
            if (GUILayout.Button("Join as Worker")) StartSafely(manager.StartWorker);
        }
        else
        {
            GUILayout.Label($"Role: {(manager.IsWorker ? "Worker" : manager.IsServer ? (manager.IsClient ? "Host" : "Server") : "Client")}");
            GUILayout.Label($"Session: {manager.LocalSession}   Tick: {manager.Tick}");
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
            GUILayout.Label($"Entities: {manager.SpawnedCount}   Observers: {manager.ObserverCopies}");
            GUILayout.Label($"Simulating here: {manager.LocalAuthorityCount}   Ghosts: {manager.GhostCount}");
            if (SceneManager.GetActiveScene().name == "ScaleDemo" && manager.IsServer)
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
            if (manager.IsServer && !manager.IsWorker && (manager.PlayerPrefab != null || playerPrefab != null))
            {
                if (GUILayout.Button("Spawn extra (P)")) SpawnExtra();
                if (GUILayout.Button("Despawn extra (O)")) DespawnExtra();
            }
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
