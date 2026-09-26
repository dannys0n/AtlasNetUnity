using System.Collections.Generic;
using AtlasNet;
using UnityEngine;

// Local-only sample view. It is not a NetworkObject and never decides authority or interest.
public sealed class LocalDebugView : MonoBehaviour
{
    [SerializeField] private Material regionFill;
    [SerializeField] private Material interestFill;
    [SerializeField] private KeyCode toggleKey = KeyCode.F8;
    [SerializeField] private float cameraHeight = 4.2f;
    [SerializeField] private float serverSize = 18f;
    [SerializeField] private float overlayHeight = 0.12f;

    private readonly Dictionary<NetworkObject, GameObject> interestDiscs = new Dictionary<NetworkObject, GameObject>();
    private readonly Dictionary<NetworkObject, GameObject> serverMarkers = new Dictionary<NetworkObject, GameObject>();
    private readonly List<NetworkObject> stale = new List<NetworkObject>();
    private NetworkManager manager;
    private Camera overview;
    private NetworkObject subscribedPlayer;
    private Mesh regionMesh;
    private Mesh discMesh;
    private GameObject regionSurface;
    private Material authorityMarker;
    private Material ghostMarker;
    private int shownRegionVersion = -1;
    private bool shownClientRegion;
    private bool clientVisible;
    private bool serverRegionRequested;
    private bool serverViewInitialized;
    private bool serverRegionCentered;
    private Vector2 serverViewCenter;
    private float serverZoom;
    private Vector3 lastMousePosition;

    private void Awake()
    {
        discMesh = CreateDiscMesh();
        if (regionFill != null)
        {
            authorityMarker = new Material(regionFill) { name = "Debug authority marker" };
            authorityMarker.SetColor("_Color", new Color(0.1f, 0.65f, 1f, 0.95f));
            ghostMarker = new Material(regionFill) { name = "Debug ghost marker" };
            ghostMarker.SetColor("_Color", new Color(1f, 0.55f, 0.12f, 0.95f));
        }
    }

    private void LateUpdate()
    {
        if (manager == null) manager = FindFirstObjectByType<NetworkManager>();
        if (manager == null || !manager.IsRunning)
        {
            SetVisible(false);
            serverRegionRequested = false;
            serverViewInitialized = false;
            serverRegionCentered = false;
            subscribedPlayer = null;
            return;
        }

        bool server = manager.IsServer;
        if (!server && manager.IsClient && Input.GetKeyDown(toggleKey)) clientVisible = !clientVisible;
        NetworkObject ownedPlayer = FindOwnedPlayer();
        bool visible = server || (manager.IsClient && clientVisible && ownedPlayer != null);
        if (!visible)
        {
            Unsubscribe();
            SetVisible(false);
            return;
        }

        if (server)
        {
            if (!serverRegionRequested) serverRegionRequested = manager.RequestLocalRegionForDebug();
            Unsubscribe();
        }
        else if (subscribedPlayer != ownedPlayer)
        {
            Unsubscribe();
            if (manager.SetClientDebugRegionEnabled(ownedPlayer, true)) subscribedPlayer = ownedPlayer;
        }

        if (server) UpdateServerCamera();
        else if (overview != null) overview.enabled = false;

        bool clientRegion = !server;
        int version = clientRegion ? manager.ClientDebugRegionVersion : manager.LocalRegionVersion;
        bool matchesOwner = server || manager.ClientDebugRegionEntity == ownedPlayer.EntityId &&
            manager.ClientDebugRegionWorker == ownedPlayer.SimulationWorker &&
            manager.ClientDebugRegionEpoch == ownedPlayer.AuthorityEpoch;
        if (version != shownRegionVersion || clientRegion != shownClientRegion || !matchesOwner && regionMesh != null)
        {
            shownRegionVersion = version;
            shownClientRegion = clientRegion;
            RebuildRegion(matchesOwner
                ? clientRegion ? manager.ClientDebugRegion : manager.LocalRegion
                : null);
        }
        if (regionSurface != null) regionSurface.SetActive(true);
        UpdateDiscs(server, ownedPlayer);
        UpdateMarkers(server);
    }

    private void UpdateServerCamera()
    {
        if (overview == null)
        {
            overview = gameObject.AddComponent<Camera>();
            overview.orthographic = true;
            overview.clearFlags = CameraClearFlags.SolidColor;
            overview.backgroundColor = new Color(0.08f, 0.1f, 0.13f);
            overview.nearClipPlane = 0.01f;
            overview.farClipPlane = 100f;
            overview.depth = 100f;
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }
        if (!serverViewInitialized)
        {
            serverViewCenter = RegionCenter(manager.LocalRegion);
            serverZoom = serverSize;
            serverViewInitialized = true;
        }
        if (!serverRegionCentered && manager.LocalRegion.Count >= 3)
        {
            serverViewCenter = RegionCenter(manager.LocalRegion);
            serverRegionCentered = true;
        }

        // Middle-drag pans the map; the wheel zooms. Never snap back to the region center.
        Vector3 mousePosition = Input.mousePosition;
        if (Input.GetMouseButton(2) && !Input.GetMouseButtonDown(2))
        {
            Vector3 delta = mousePosition - lastMousePosition;
            float worldPerPixel = 2f * serverZoom / Mathf.Max(1, Screen.height);
            serverViewCenter -= new Vector2(delta.x, delta.y) * worldPerPixel;
        }
        lastMousePosition = mousePosition;
        serverZoom = Mathf.Clamp(serverZoom - Input.mouseScrollDelta.y * 2f, 4f, 60f);
        transform.position = new Vector3(serverViewCenter.x, cameraHeight, serverViewCenter.y);
        overview.orthographicSize = serverZoom;
        overview.enabled = true;
    }

    private NetworkObject FindOwnedPlayer()
    {
        foreach (NetworkObject obj in manager.SpawnedObjects)
            if (obj != null && obj.IsOwner && obj.GetComponent<NetworkInterestSource>() != null)
                return obj;
        return null;
    }

    private void Unsubscribe()
    {
        if (ReferenceEquals(subscribedPlayer, null)) return;
        if (manager != null && manager.IsRunning) manager.SetClientDebugRegionEnabled(null, false);
        subscribedPlayer = null;
    }

    private void SetVisible(bool visible)
    {
        if (overview != null) overview.enabled = visible;
        if (regionSurface != null) regionSurface.SetActive(visible && regionMesh != null);
        foreach (var disc in interestDiscs.Values) if (disc != null) disc.SetActive(visible);
        foreach (var marker in serverMarkers.Values) if (marker != null) marker.SetActive(visible);
    }

    private void RebuildRegion(IReadOnlyList<Vector2> outline)
    {
        if (regionSurface != null) Destroy(regionSurface);
        if (regionMesh != null) Destroy(regionMesh);
        regionSurface = null;
        regionMesh = null;
        if (outline == null || outline.Count < 3 || regionFill == null) return;
        var vertices = new Vector3[outline.Count];
        var triangles = new int[(outline.Count - 2) * 3];
        for (int i = 0; i < outline.Count; i++)
            vertices[i] = new Vector3(outline[i].x, overlayHeight, outline[i].y);
        for (int i = 0; i < outline.Count - 2; i++)
        {
            triangles[i * 3] = 0;
            triangles[i * 3 + 1] = i + 1;
            triangles[i * 3 + 2] = i + 2;
        }
        regionMesh = new Mesh { name = "Current worker debug region" };
        regionMesh.vertices = vertices;
        regionMesh.triangles = triangles;
        regionMesh.RecalculateBounds();
        regionSurface = new GameObject("Current worker debug region");
        regionSurface.AddComponent<MeshFilter>().sharedMesh = regionMesh;
        regionSurface.AddComponent<MeshRenderer>().sharedMaterial = regionFill;
    }

    private void UpdateDiscs(bool server, NetworkObject ownedPlayer)
    {
        stale.Clear();
        foreach (var pair in interestDiscs)
        {
            if (pair.Key == null || !(server ? manager.ShouldRenderWorkerCopyForDebug(pair.Key) : pair.Key == ownedPlayer))
            {
                stale.Add(pair.Key);
                continue;
            }
            var interest = pair.Key.GetComponent<NetworkInterestSource>();
            if (interest == null || interest.Radius <= 0f) stale.Add(pair.Key);
        }
        foreach (NetworkObject obj in stale)
        {
            if (interestDiscs[obj] != null) Destroy(interestDiscs[obj]);
            interestDiscs.Remove(obj);
        }
        if (interestFill == null) return;
        foreach (NetworkObject obj in manager.SpawnedObjects)
        {
            if (obj == null || !(server ? manager.ShouldRenderWorkerCopyForDebug(obj) : obj == ownedPlayer)) continue;
            var interest = obj.GetComponent<NetworkInterestSource>();
            if (interest == null || interest.Radius <= 0f) continue;
            if (!interestDiscs.TryGetValue(obj, out var disc))
            {
                disc = new GameObject("Cross-worker interest radius");
                disc.AddComponent<MeshFilter>().sharedMesh = discMesh;
                disc.AddComponent<MeshRenderer>().sharedMaterial = interestFill;
                interestDiscs.Add(obj, disc);
            }
            disc.SetActive(true);
            disc.transform.position = new Vector3(obj.transform.position.x, overlayHeight + 0.03f, obj.transform.position.z);
            disc.transform.localScale = new Vector3(interest.Radius, 1f, interest.Radius);
        }
    }

    private void UpdateMarkers(bool server)
    {
        stale.Clear();
        foreach (var pair in serverMarkers)
            if (pair.Key == null || !server || !manager.ShouldRenderWorkerCopyForDebug(pair.Key))
                stale.Add(pair.Key);
        foreach (NetworkObject obj in stale)
        {
            if (serverMarkers[obj] != null) Destroy(serverMarkers[obj]);
            serverMarkers.Remove(obj);
        }
        if (!server || authorityMarker == null) return;
        foreach (NetworkObject obj in manager.SpawnedObjects)
        {
            if (obj == null || !manager.ShouldRenderWorkerCopyForDebug(obj)) continue;
            if (!serverMarkers.TryGetValue(obj, out var marker))
            {
                marker = new GameObject("Debug entity marker");
                marker.AddComponent<MeshFilter>().sharedMesh = discMesh;
                marker.AddComponent<MeshRenderer>();
                serverMarkers.Add(obj, marker);
            }
            marker.SetActive(true);
            marker.transform.position = new Vector3(obj.transform.position.x, cameraHeight - 0.2f, obj.transform.position.z);
            marker.transform.localScale = new Vector3(0.45f, 1f, 0.45f);
            marker.GetComponent<MeshRenderer>().sharedMaterial = obj.HasAuthority ? authorityMarker : ghostMarker;
        }
    }

    private static Vector2 RegionCenter(IReadOnlyList<Vector2> region)
    {
        if (region == null || region.Count == 0) return Vector2.zero;
        Vector2 center = Vector2.zero;
        foreach (Vector2 point in region) center += point;
        return center / region.Count;
    }

    private static Mesh CreateDiscMesh()
    {
        const int segments = 48;
        var vertices = new Vector3[segments + 1];
        var triangles = new int[segments * 3];
        for (int i = 0; i < segments; i++)
        {
            float angle = i * Mathf.PI * 2f / segments;
            vertices[i + 1] = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            triangles[i * 3] = 0;
            triangles[i * 3 + 1] = i + 1;
            triangles[i * 3 + 2] = (i + 1) % segments + 1;
        }
        var mesh = new Mesh { name = "Debug disc" };
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
        return mesh;
    }

    private void OnGUI()
    {
        if (manager != null && manager.IsClient && !manager.IsServer)
            GUI.Label(new Rect(10, Screen.height - 26, 360, 22), $"{toggleKey}: {(clientVisible ? "hide" : "show")} local debug view");
    }

    private void OnDestroy()
    {
        Unsubscribe();
        if (regionSurface != null) Destroy(regionSurface);
        if (regionMesh != null) Destroy(regionMesh);
        if (discMesh != null) Destroy(discMesh);
        if (authorityMarker != null) Destroy(authorityMarker);
        if (ghostMarker != null) Destroy(ghostMarker);
        foreach (var disc in interestDiscs.Values) if (disc != null) Destroy(disc);
        foreach (var marker in serverMarkers.Values) if (marker != null) Destroy(marker);
    }
}
