using System;
using AtlasNet;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AtlasNet.Samples
{
    public sealed class PredictionSample : MonoBehaviour
    {
        [SerializeField] private NetworkManager manager;
        private string address = "127.0.0.1", port = "7777", error;
        private NetworkObject player;
        private PredictedCharacterController character;
        private PredictedDashController dash;

        private void Awake()
        {
            Application.runInBackground = true;
            manager.PlayerSpawnPosition = session => new Vector3((float)(session.Value % 4) * 2 - 3, 1, -4);
        }

        private void Update()
        {
            if (player == null || !player.IsOwner)
            {
                player = null;
                foreach (var obj in manager.SpawnedObjects)
                {
                    if (!obj.IsOwner) continue;
                    player = obj;
                    character = obj.GetComponent<PredictedCharacterController>();
                    dash = obj.GetComponent<PredictedDashController>();
                    break;
                }
                if (player == null) return;
            }

            Vector2 move = default;
            bool sprint = false, jump = false;
#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                move.x = (keyboard.dKey.isPressed ? 1 : 0) - (keyboard.aKey.isPressed ? 1 : 0);
                move.y = (keyboard.wKey.isPressed ? 1 : 0) - (keyboard.sKey.isPressed ? 1 : 0);
                sprint = keyboard.leftShiftKey.isPressed;
                jump = keyboard.spaceKey.wasPressedThisFrame;
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            move.x = (Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0);
            move.y = (Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0);
            sprint = Input.GetKey(KeyCode.LeftShift);
            jump = Input.GetKeyDown(KeyCode.Space);
#endif
            if (character != null) character.SetInput(move, 0, sprint, jump);
            if (dash != null) dash.SetInput(move, jump);
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(12, 12, 390, 200), GUI.skin.box);
            bool custom = manager.PlayerPrefab != null && manager.PlayerPrefab.TryGetComponent<PredictedDashController>(out _);
            GUILayout.Label(custom ? "AtlasNet | Custom Prediction" : "AtlasNet | Client Prediction");
            if (manager.IsRunning)
            {
                GUILayout.Label(manager.IsWorker ? "Worker" : manager.IsHost ? "Host" : manager.IsServer ? "Server" :
                    manager.IsConnectedClient ? "Client connected" : "Connecting...");
                GUILayout.Label(custom ? "WASD move   Space dash" : "WASD move   Shift sprint   Space jump");
                if (dash != null) GUILayout.Label($"Dash cooldown: {dash.CooldownRemaining:0.00}s");
                if (manager.IsServer) GUILayout.Label($"Workers: {manager.WorkerCount}");
                if (player != null && player.IsOwner)
                    GUILayout.Label($"Worker: {player.SimulationWorker}   Epoch: {player.AuthorityEpoch}");
                if (manager.IsClient && !manager.IsServer && TryGetComponent<ClientBoundaryOverlay>(out var boundaries))
                    boundaries.Visible = GUILayout.Toggle(boundaries.Visible, "Show boundary walls (client debug)");
                if (GUILayout.Button("Disconnect")) manager.Stop();
            }
            else
            {
                GUILayout.BeginHorizontal();
                address = GUILayout.TextField(address, GUILayout.Width(165));
                port = GUILayout.TextField(port, GUILayout.Width(65));
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Host")) StartSession(manager.StartHost);
                if (GUILayout.Button("Server")) StartSession(manager.StartServer);
                if (GUILayout.Button("Join")) StartSession(manager.StartClient);
                if (GUILayout.Button("Worker")) StartSession(manager.StartWorker);
                GUILayout.EndHorizontal();
            }
            if (!string.IsNullOrEmpty(error)) GUILayout.Label(error);
            else if (!string.IsNullOrEmpty(manager.DisconnectReason)) GUILayout.Label(manager.DisconnectReason);
            GUILayout.EndArea();
        }

        private void StartSession(Action start)
        {
            try
            {
                if (!int.TryParse(port, out int number)) throw new ArgumentException("Enter a port number.");
                manager.ConfigureConnection(address, number, ClientTransport.Udp);
                start();
                error = null;
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
        }
    }
}
