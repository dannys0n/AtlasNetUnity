using System;
using System.Collections.Generic;
using AtlasNet;
using AtlasNet.Samples.Soccer;
using AtlasNet.Samples.Soccer.QrCodeGenerator;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

internal sealed class NativeSoccerSpectator
{
    private sealed class Region
    {
        public JObject Shape;
        public Color Color;
        public GameObject Object;
        public Mesh Mesh;
    }

    private readonly NetworkManager manager;
    private readonly Camera camera;
    private readonly Material regionMaterial;
    private readonly Vector3 startPosition;
    private readonly Quaternion startRotation;
    private readonly Dictionary<ulong, Region> regions = new();
    private readonly HashSet<ulong> hiddenShards = new();
    private readonly List<NetworkObject> candidates = new();
    private readonly List<ulong> staleRegions = new();
    private readonly List<Vector2> outline = new();
    private readonly Texture2D qr;
    private enum CameraMode { Static, FollowPlayer, FollowBall }
    private static readonly string[] CameraModes = { "Static", "Follow player", "Follow ball" };
    private CameraMode cameraMode;
    private NetworkObject focus;
    private NetworkObject ball;
    private bool showRegions = true;
    private double nextFocus;
    private Vector2 scroll;
    private int nextColor;

    public NativeSoccerSpectator(NetworkManager manager, Camera camera, Material regionMaterial, string joinUrl)
    {
        this.manager = manager;
        this.camera = camera;
        this.regionMaterial = regionMaterial;
        startPosition = camera.transform.position;
        startRotation = camera.transform.rotation;
        JoinUrl = joinUrl;
        if (string.IsNullOrEmpty(joinUrl)) return;

        var code = QrCode.EncodeText(joinUrl, QrCode.Ecc.Medium);
        const int border = 4;
        int size = code.Size + border * 2;
        qr = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                pixels[(size - y - 1) * size + x] = code.GetModule(x - border, y - border)
                    ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255);
        qr.SetPixels32(pixels);
        qr.Apply(false, true);
    }

    public string JoinUrl { get; }
    private bool Includes(ulong worker) => !hiddenShards.Contains(worker);

    public void Update()
    {
        UpdateRegions();
        NetworkObject target = null;
        if (manager.IsConnectedClient)
        {
            if (cameraMode == CameraMode.FollowPlayer)
            {
                if (!focus || !focus.IsSpawned || !Includes(focus.SimulationWorker) ||
                    Time.unscaledTimeAsDouble >= nextFocus)
                    PickFocus();
                target = focus;
            }
            else if (cameraMode == CameraMode.FollowBall)
            {
                if (!ball || !ball.IsSpawned)
                    foreach (var replica in manager.SpawnedObjects)
                        if (replica.TryGetComponent<SoccerBall>(out _))
                        {
                            ball = replica;
                            break;
                        }
                target = ball;
            }
        }
        if (target && target.IsSpawned)
            SoccerCameraFraming.Follow(camera, target.transform.position,
                new Vector3(0, 35, -28), Quaternion.Euler(51, 0, 0));
        else
            camera.transform.SetPositionAndRotation(startPosition, startRotation);
    }

    private void PickFocus()
    {
        nextFocus = Time.unscaledTimeAsDouble + 5;
        var previous = focus;
        focus = null;
        if (!manager.IsConnectedClient) return;
        candidates.Clear();
        foreach (var replica in manager.SpawnedObjects)
            if (replica != previous && Includes(replica.SimulationWorker) && replica.TryGetComponent<SoccerPlayer>(out _))
                candidates.Add(replica);
        if (candidates.Count > 0)
            focus = candidates[UnityEngine.Random.Range(0, candidates.Count)];
        else if (previous && previous.IsSpawned && Includes(previous.SimulationWorker))
            focus = previous;
    }

    private void UpdateRegions()
    {
        if (!regionMaterial) return;
        foreach (var entry in NativeShardBackend.DebugRegions)
        {
            if (!regions.TryGetValue(entry.Key, out Region region))
            {
                var color = Color.HSVToRGB(Mathf.Repeat(nextColor++ * .618034f + .52f, 1), .86f, 1);
                var obj = new GameObject($"Shard region {entry.Key}");
                obj.transform.SetParent(manager.transform, false);
                var mesh = new Mesh { name = obj.name };
                obj.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = obj.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = regionMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                var tint = new MaterialPropertyBlock();
                var fill = new Color(color.r, color.g, color.b, .24f);
                tint.SetColor("_Color", fill);
                tint.SetColor("_BaseColor", fill);
                renderer.SetPropertyBlock(tint);
                region = new Region { Object = obj, Mesh = mesh, Color = color };
                regions.Add(entry.Key, region);
            }
            if (!JToken.DeepEquals(region.Shape, entry.Value))
            {
                region.Shape = entry.Value;
                Rebuild(region.Mesh, entry.Value);
            }
            region.Object.SetActive(showRegions && Includes(entry.Key));
        }

        staleRegions.Clear();
        foreach (var entry in regions)
            if (!NativeShardBackend.DebugRegions.ContainsKey(entry.Key)) staleRegions.Add(entry.Key);
        foreach (ulong worker in staleRegions)
        {
            DestroyRegion(regions[worker]);
            regions.Remove(worker);
            hiddenShards.Remove(worker);
        }
    }

    private void Rebuild(Mesh mesh, JObject shape)
    {
        NativeShardBackend.BuildRegionOutline(shape, outline);
        mesh.Clear();
        if (outline.Count < 3) return;
        var vertices = new Vector3[outline.Count];
        var indices = new int[(outline.Count - 2) * 3];
        for (int i = 0; i < outline.Count; i++)
        {
            vertices[i] = manager.transform.InverseTransformPoint(new Vector3(outline[i].x, .09f, outline[i].y));
            if (i < 2) continue;
            int triangle = (i - 2) * 3;
            indices[triangle] = 0;
            indices[triangle + 1] = i;
            indices[triangle + 2] = i - 1;
        }
        mesh.vertices = vertices;
        mesh.triangles = indices;
        mesh.RecalculateBounds();
    }

    public void DrawControls()
    {
        if (cameraMode == CameraMode.FollowPlayer && focus) GUILayout.Label($"Following {focus.EntityId} · shard {focus.SimulationWorker}");
        GUILayout.Space(8);
        cameraMode = (CameraMode)GUILayout.Toolbar((int)cameraMode, CameraModes, GUILayout.Height(26));
        GUILayout.Space(8);
        showRegions = GUILayout.Toggle(showRegions, "Show authority regions");
        scroll = GUILayout.BeginScrollView(scroll, GUILayout.MaxHeight(136));
        foreach (var entry in regions)
        {
            Color previous = GUI.contentColor;
            GUI.contentColor = entry.Value.Color;
            if (GUILayout.Toggle(Includes(entry.Key), $"Shard {entry.Key}"))
                hiddenShards.Remove(entry.Key);
            else
                hiddenShards.Add(entry.Key);
            GUI.contentColor = previous;
        }
        GUILayout.EndScrollView();
    }

    public void DrawJoinCode()
    {
        if (!qr) return;
        // Match the Soccer HUD's 1600x900 reference canvas and equal axis weighting.
        float scale = Mathf.Sqrt(Screen.width / 1600f * (Screen.height / 900f));
        float size = Mathf.Min(Mathf.Round(340 * scale), Screen.width - 56);
        float panelWidth = size + 32;
        GUILayout.BeginArea(new Rect(Screen.width - panelWidth - 12, 12, panelWidth, size + 50), GUI.skin.box);
        GUILayout.Label("Join Native Soccer");
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        Rect qrRect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
        GUI.DrawTexture(qrRect, qr, ScaleMode.ScaleToFit);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Label(JoinUrl);
        GUILayout.EndArea();
    }

    public void Dispose()
    {
        foreach (var region in regions.Values) DestroyRegion(region);
        if (qr) UnityEngine.Object.Destroy(qr);
    }

    private static void DestroyRegion(Region region)
    {
        UnityEngine.Object.Destroy(region.Mesh);
        UnityEngine.Object.Destroy(region.Object);
    }
}
