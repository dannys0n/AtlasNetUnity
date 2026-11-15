using System;
using AtlasNet;
using UnityEngine;

[RequireComponent(typeof(NetworkManager))]
public sealed class BackendSmokeLauncher : MonoBehaviour
{
    [SerializeField] private WorldBackendProvider backendProvider;
    [SerializeField] private Camera overviewCamera;
    [SerializeField] private bool spectatorMode;
    [SerializeField] private Material spectatorRegionMaterial;
    [SerializeField] private string spectatorJoinUrl = "http://localhost:8094/";
    private NativeSoccerSpectator spectator;

    private NetworkManager manager;
    private string address = "127.0.0.1";
#if UNITY_WEBGL && !UNITY_EDITOR
    private string port = "5921";
#else
    private string port = "5919";
#endif
    private string error;
    private bool showRegions;
    private bool traceSoccer;
    private double nextTrace;
    private string goalFixture;

    private void Awake()
    {
        manager = GetComponent<NetworkManager>();
        if (TryGetComponent(out AtlasNet.Samples.Soccer.SoccerSession soccer))
            soccer.UseExternalConnection = true;
        Application.runInBackground = true;
        if (spectatorMode && overviewCamera)
            spectator = new NativeSoccerSpectator(manager, overviewCamera, spectatorRegionMaterial, ResolveJoinUrl());
#if UNITY_WEBGL && !UNITY_EDITOR
        if (Uri.TryCreate(Application.absoluteURL, UriKind.Absolute, out Uri page))
        {
            address = page.GetLeftPart(UriPartial.Authority);
            if (page.Scheme == "https")
            {
                address += "/atlasnet";
                port = page.Port.ToString();
            }
        }
#endif
    }

    private void Start()
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        string[] args = Environment.GetCommandLineArgs();
        traceSoccer = Array.IndexOf(args, "-atlasnet-trace-soccer") >= 0;
        if (Array.IndexOf(args, "-atlasnet-socket") >= 0)
        {
            goalFixture = Environment.GetEnvironmentVariable("ATLASNET_SOCCER_GOAL_FIXTURE");
            if (TryGetComponent(out AtlasNet.Samples.Soccer.SoccerSession soccer))
                soccer.TryClaimInitialBall = ClaimBall;
            StartSession(true);
            return;
        }

        int join = Array.IndexOf(args, "-atlasnet-join");
        if (join < 0) return;
        if (join + 2 >= args.Length)
        {
            Debug.LogError("Usage: -atlasnet-join <host> <port>", this);
            return;
        }
        address = args[join + 1];
        port = args[join + 2];
        StartSession(false);
#endif
    }

    private string ResolveJoinUrl()
    {
        if (!Uri.TryCreate(spectatorJoinUrl, UriKind.Absolute, out var configured) ||
            (configured.Scheme != "http" && configured.Scheme != "https")) return null;
        if (!configured.IsLoopback) return configured.AbsoluteUri;
        var url = new UriBuilder(configured);
#if UNITY_WEBGL && !UNITY_EDITOR
        if (Uri.TryCreate(Application.absoluteURL, UriKind.Absolute, out var page))
        {
            url.Host = page.Host;
            url.Scheme = page.Scheme;
            url.Port = page.Port;
        }
#else
        url.Host = AtlasNet.Samples.Soccer.SoccerSession.LanAddress();
#endif
        return url.Uri.AbsoluteUri;
    }

    private void OnDestroy() => spectator?.Dispose();

    private static bool ClaimBall()
    {
        string path = Environment.GetEnvironmentVariable("ATLASNET_SOCCER_MATCH_CLAIM");
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Set ATLASNET_SOCCER_MATCH_CLAIM to a fresh shared path for this match.");
        try
        {
            using var claim = new System.IO.FileStream(path, System.IO.FileMode.CreateNew);
            return true;
        }
        catch (System.IO.IOException) when (System.IO.File.Exists(path))
        {
            return false;
        }
    }

    private void StartSession(bool server)
    {
        try
        {
            if (!backendProvider)
                throw new InvalidOperationException("Assign the real AtlasNet backend provider on Backend Smoke Launcher.");

            if (!server)
            {
                if (string.IsNullOrWhiteSpace(address) || !int.TryParse(port, out int portNumber) ||
                    portNumber < 1 || portNumber > 65535)
                    throw new ArgumentException("Enter an AtlasNet ingress address and a valid port.");
                manager.ConfigureConnection(address.Trim(), portNumber);
                manager.ConnectionData = spectatorMode ? NativeShardBackend.SpectatorAdmission : Array.Empty<byte>();
            }

            manager.SetBackend(backendProvider);
            if (server) manager.StartServer();
            else manager.StartClient();
            error = null;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            Debug.LogError($"AtlasNet backend smoke test: {error}", this);
            if (server && !Application.isEditor) Application.Quit(1);
        }
    }

    private void Update()
    {
        if (TryGetComponent(out ClientBoundaryOverlay overlay)) overlay.Visible = showRegions;
        TryRunGoalFixture();
        if (!traceSoccer || !manager.IsConnectedClient || Time.unscaledTimeAsDouble < nextTrace) return;
        nextTrace = Time.unscaledTimeAsDouble + 0.25;
        foreach (var replica in manager.SpawnedObjects)
        {
            if (!replica.TryGetComponent(out AtlasNet.Samples.Soccer.SoccerMatch match)) continue;
            Debug.Log($"Soccer session={manager.LocalSession.Value} ball={replica.EntityId.Value} " +
                $"worker={replica.SimulationWorker} epoch={replica.AuthorityEpoch} " +
                $"position={replica.transform.position:F2} blue={match.BlueScore.Value} orange={match.OrangeScore.Value}");
            break;
        }
    }

    private void TryRunGoalFixture()
    {
        if (string.IsNullOrEmpty(goalFixture) || !manager.IsServer ||
            !System.IO.File.Exists(goalFixture)) return;

        foreach (var replica in manager.SpawnedObjects)
        {
            if (!replica.HasAuthority || !replica.TryGetComponent(out AtlasNet.Samples.Soccer.SoccerBall _) ||
                !replica.TryGetComponent(out Rigidbody ball)) continue;

            System.IO.File.Delete(goalFixture);
            goalFixture = null;
            // Use the actual goal detection and score replication after placing the ball.
            ball.position = new Vector3(51f, 1f, 0f);
            ball.linearVelocity = Vector3.zero;
            ball.angularVelocity = Vector3.zero;
            Debug.Log($"Soccer goal fixture placed ball {replica.EntityId.Value}");
            break;
        }
    }

    private void LateUpdate()
    {
        if (!overviewCamera) return;
        if (spectator != null)
        {
            spectator.Update();
            return;
        }
        bool showOverview = true;
        if (manager.IsConnectedClient)
        {
            foreach (var replica in manager.SpawnedObjects)
            {
                if (!replica.IsOwner) continue;
                showOverview = false;
                break;
            }
        }

        overviewCamera.enabled = showOverview;
        if (overviewCamera.TryGetComponent<AudioListener>(out var listener))
            listener.enabled = showOverview;
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(12, 12, 400, 310), GUI.skin.box);
        GUILayout.Label(spectatorMode ? "AtlasNet Soccer spectator" : "AtlasNet backend smoke test");
        if (!manager.IsRunning)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            GUILayout.Label("WebSocket address / port");
#else
            GUILayout.Label("Ingress address / port");
#endif
            address = GUILayout.TextField(address);
            port = GUILayout.TextField(port);
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && backendProvider != null;
            if (GUILayout.Button(spectatorMode ? "Connect spectator" : "Join AtlasNet")) StartSession(false);
            GUI.enabled = wasEnabled;
            if (!backendProvider)
                GUILayout.Label("Assign the real backend provider in the Inspector.");
        }
        else
        {
            GUILayout.Label(manager.IsServer ? "Shard server" :
                manager.IsConnectedClient ? "Connected client" : "Connecting...");
            GUILayout.Label($"Session {manager.LocalSession}  |  Tick {manager.Tick}  |  Replicas {manager.SpawnedCount}");
            foreach (var replica in manager.SpawnedObjects)
            {
                if (!replica.IsOwner) continue;
                GUILayout.Label($"Entity {replica.EntityId}  |  Worker {replica.SimulationWorker}  |  Epoch {replica.AuthorityEpoch}");
                GUILayout.Label($"Position {replica.transform.position:F2}");
                break;
            }
            if (spectator != null) spectator.DrawControls();
            else
            {
                GUILayout.Label("WASD to move");
                showRegions = GUILayout.Toggle(showRegions, "Shard ownership");
                if (showRegions) DrawRegions();
            }
            if (GUILayout.Button("Disconnect")) manager.Stop();
        }

        if (!string.IsNullOrEmpty(error)) GUILayout.Label(error);
        var reason = manager.DisconnectReason;
        if (!string.IsNullOrEmpty(reason)) GUILayout.Label(reason);
        GUILayout.EndArea();
        spectator?.DrawJoinCode();
    }
    private static void DrawRegions()
    {
        if (NativeShardBackend.DebugRegions.Count == 0) GUILayout.Label("Waiting for shard region data...");
        foreach (var entry in NativeShardBackend.DebugRegions)
        {
            var shape = entry.Value;
            GUILayout.Label($"Worker {entry.Key} · region {shape["id"]} · gen {shape["generation"]}");
            GUILayout.Label($"{shape["min"]?.ToString(Newtonsoft.Json.Formatting.None)} to {shape["max"]?.ToString(Newtonsoft.Json.Formatting.None)}");
        }
    }
    internal static void Clip(System.Collections.Generic.List<Vector2> polygon, Vector2 normal, float offset)
    {
        if (polygon.Count == 0) return;
        var input = polygon.ToArray();
        polygon.Clear();
        var previous = input[input.Length - 1];
        float distance = Vector2.Dot(normal, previous) - offset;
        foreach (var current in input)
        {
            float next = Vector2.Dot(normal, current) - offset;
            if ((distance <= 0) != (next <= 0))
                polygon.Add(Vector2.Lerp(previous, current, distance / (distance - next)));
            if (next <= 0) polygon.Add(current);
            previous = current;
            distance = next;
        }
    }

}
