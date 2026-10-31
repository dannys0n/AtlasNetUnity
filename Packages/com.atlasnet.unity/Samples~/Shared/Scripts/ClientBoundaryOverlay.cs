using System.Collections.Generic;
using AtlasNet;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Local-only diagnostic walls. Geometry comes from the backend, never from an authority calculation here.</summary>
public sealed class ClientBoundaryOverlay : MonoBehaviour
{
    [SerializeField] private NetworkManager manager;
    [SerializeField] private Material wallMaterial;
    [SerializeField, Min(1)] private float viewRadius = 80f;
    [SerializeField, Min(1)] private float wallHeight = 12f;
    [SerializeField] private bool showBoundaries = true;
    private NetworkObject subscribedPlayer;
    private Mesh mesh;
    private GameObject walls;
    private MeshRenderer wallRenderer;
    private int shownVersion = -1;
    private Vector3 meshCenter;
    private readonly List<Vector3> vertices = new List<Vector3>();
    private readonly List<int> triangles = new List<int>();

    public bool Visible
    {
        get => showBoundaries;
        set
        {
            if (showBoundaries == value) return;
            showBoundaries = value;
            if (!value) Unsubscribe();
            shownVersion = -1;
        }
    }

    private void LateUpdate()
    {
        if (!showBoundaries || manager == null || !manager.IsRunning || !manager.IsClient || manager.IsServer)
        {
            Unsubscribe();
            return;
        }
        if (subscribedPlayer == null || !subscribedPlayer.IsSpawned || !subscribedPlayer.IsOwner)
        {
            Unsubscribe();
            foreach (var player in manager.SpawnedObjects)
                if (player.IsOwner && manager.SetClientDebugRegionEnabled(player, true))
                {
                    subscribedPlayer = player;
                    break;
                }
        }
        if (subscribedPlayer == null || manager.ClientDebugRegionEntity != subscribedPlayer.EntityId) return;
        if (walls == null) CreateWalls();
        Vector3 center = subscribedPlayer.transform.position;
        float rebuildDistance = Mathf.Max(1f, viewRadius * .1f);
        if (shownVersion != manager.ClientDebugRegionVersion || (center - meshCenter).sqrMagnitude > rebuildDistance * rebuildDistance)
            Rebuild(center);
        wallRenderer.enabled = mesh.vertexCount > 0;
    }

    private void CreateWalls()
    {
        mesh = new Mesh { name = "Nearby server boundary walls" };
        walls = new GameObject("Client boundary walls (debug only)");
        walls.transform.SetParent(transform, false);
        walls.AddComponent<MeshFilter>().sharedMesh = mesh;
        wallRenderer = walls.AddComponent<MeshRenderer>();
        wallRenderer.sharedMaterial = wallMaterial;
        wallRenderer.shadowCastingMode = ShadowCastingMode.Off;
        wallRenderer.receiveShadows = false;
        wallRenderer.lightProbeUsage = LightProbeUsage.Off;
        wallRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        var tint = new MaterialPropertyBlock();
        tint.SetColor("_Color", new Color(.08f, .65f, 1f, .18f));
        wallRenderer.SetPropertyBlock(tint);
    }

    private void Rebuild(Vector3 center)
    {
        meshCenter = center;
        shownVersion = manager.ClientDebugRegionVersion;
        vertices.Clear(); triangles.Clear();
        var outline = manager.ClientDebugRegion;
        var near = new Vector2(center.x, center.z);
        for (int i = 0; i < outline.Count; i++)
        {
            Vector2 start = outline[i], direction = outline[(i + 1) % outline.Count] - start;
            float lengthSquared = direction.sqrMagnitude;
            if (lengthSquared < .0001f) continue;
            float nearest = Vector2.Dot(near - start, direction) / lengthSquared;
            float distanceSquared = (start + direction * nearest - near).sqrMagnitude;
            if (distanceSquared >= viewRadius * viewRadius) continue;
            float reach = Mathf.Sqrt((viewRadius * viewRadius - distanceSquared) / lengthSquared);
            float first = Mathf.Max(0, nearest - reach), last = Mathf.Min(1, nearest + reach);
            if (first >= last) continue;
            Vector2 a = start + direction * first, b = start + direction * last;
            float bottom = center.y - wallHeight * .5f, top = center.y + wallHeight * .5f;
            int index = vertices.Count;
            vertices.Add(transform.InverseTransformPoint(new Vector3(a.x, bottom, a.y)));
            vertices.Add(transform.InverseTransformPoint(new Vector3(b.x, bottom, b.y)));
            vertices.Add(transform.InverseTransformPoint(new Vector3(b.x, top, b.y)));
            vertices.Add(transform.InverseTransformPoint(new Vector3(a.x, top, a.y)));
            triangles.Add(index); triangles.Add(index + 1); triangles.Add(index + 2);
            triangles.Add(index); triangles.Add(index + 2); triangles.Add(index + 3);
        }
        mesh.Clear(); mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.RecalculateBounds();
    }

    private void Unsubscribe()
    {
        if (subscribedPlayer != null && manager != null && manager.IsRunning)
            manager.SetClientDebugRegionEnabled(null, false);
        subscribedPlayer = null;
        shownVersion = -1;
        if (wallRenderer != null) wallRenderer.enabled = false;
    }
    private void OnDisable() => Unsubscribe();
    private void OnDestroy()
    {
        Unsubscribe();
        if (walls != null) Destroy(walls);
        if (mesh != null) Destroy(mesh);
    }
}
