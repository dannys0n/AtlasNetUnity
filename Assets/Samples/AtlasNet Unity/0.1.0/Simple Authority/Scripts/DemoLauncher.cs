using System;
using AtlasNet;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Put this beside NetworkManager. Start a host in one Play Mode window and clients in the others.</summary>
public sealed class DemoLauncher : MonoBehaviour
{
    [SerializeField] private NetworkManager manager;
    [SerializeField] private string playerPrefabId = "simple-client-player";
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
    }

    private void OnSessionJoined(SessionId session)
    {
        Debug.Log($"AtlasNet session {session} joined; local role {(manager.IsServer ? "server" : "client")}");
        if (!manager.IsServer) return;
        float x = (session.Value - 1) * 2.5f;
        manager.Spawn(playerPrefabId, new Vector3(x, 1, 0), Quaternion.identity, session);
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
        if (!manager.IsServer) return;
        if (logMetrics && Time.unscaledTime >= nextMetrics)
        {
            nextMetrics = Time.unscaledTime + 5f;
            Debug.Log($"AtlasNet metrics: entities={manager.SpawnedCount} observers={manager.ObserverCopies} tickMs={manager.LastTickMilliseconds:F3} allocated={AllocationText()} bytesLastTick={manager.BytesSentLastTick}");
        }
        if (Input.GetKeyDown(KeyCode.P)) SpawnExtra();
        if (Input.GetKeyDown(KeyCode.O)) DespawnExtra();
    }

    private void SpawnExtra()
    {
        if (extra == null) extra = manager.Spawn(playerPrefabId, new Vector3(0, 1, 5), Quaternion.identity);
    }

    private void DespawnExtra()
    {
        if (extra != null) extra.Despawn();
        extra = null;
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 400, 300), GUI.skin.box);
        GUILayout.Label("AtlasNet local demo");
        if (!manager.IsRunning)
        {
            if (GUILayout.Button("Start Host")) StartSafely(manager.StartHost);
            if (GUILayout.Button("Start Server Only")) StartSafely(manager.StartServer);
            if (GUILayout.Button("Join as Client")) StartSafely(manager.StartClient);
        }
        else
        {
            GUILayout.Label($"Role: {(manager.IsServer ? (manager.IsClient ? "Host" : "Server") : "Client")}");
            GUILayout.Label($"Session: {manager.LocalSession}   Tick: {manager.Tick}");
            if (localPlayer != null && localPlayer.IsSpawned)
            {
                GUILayout.Label($"Player: {localPlayer.EntityId}   Prefab: {localPlayer.PrefabId}");
                GUILayout.Label($"Input session: {localPlayer.OwnerSession}   Simulation: {(localPlayer.HasSimulationAuthority ? "local" : "server")}");
                var movement = localPlayer.GetComponent<NetworkTransform>();
                if (movement != null)
                    GUILayout.Label($"Transform writers: position {movement.PositionWriter}, rotation {movement.RotationWriter}");
            }
            GUILayout.Label($"Entities: {manager.SpawnedCount}   Observers: {manager.ObserverCopies}");
            GUILayout.Label($"Tick: {manager.LastTickMilliseconds:F2} ms   Alloc: {AllocationText()}");
            GUILayout.Label($"Sent: {manager.BytesSentLastTick} B/tick   Peers: {manager.RemoteClientCount}");
            if (manager.IsServer)
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
}
