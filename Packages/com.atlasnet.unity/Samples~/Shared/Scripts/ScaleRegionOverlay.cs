using AtlasNet;
using UnityEngine;

/// <summary>Draws this server's local Voronoi cell over a local-backend demo floor.</summary>
public sealed class ScaleRegionOverlay : MonoBehaviour
{
    [SerializeField] private NetworkManager manager;
    [SerializeField] private MeshFilter regionMeshFilter;
    [SerializeField] private MeshRenderer regionRenderer;
    private Mesh regionMesh;
    private int shownVersion = -1;
    private bool requested;

    private void LateUpdate()
    {
        if (manager == null || !manager.IsRunning)
        {
            requested = false;
            if (regionRenderer != null) regionRenderer.enabled = false;
            return;
        }
        if (!manager.IsServer) { regionRenderer.enabled = false; return; }
        if (!requested) requested = manager.RequestLocalRegionForDebug();
        if (shownVersion != manager.LocalRegionVersion) RebuildRegion();
        regionRenderer.enabled = regionMesh != null;
    }

    private void RebuildRegion()
    {
        shownVersion = manager.LocalRegionVersion;
        if (regionMesh != null) Destroy(regionMesh);
        regionMesh = null;
        regionMeshFilter.sharedMesh = null;
        var outline = manager.LocalRegion;
        if (outline.Count < 3) return;

        var vertices = new Vector3[outline.Count];
        var triangles = new int[(outline.Count - 2) * 3];
        for (int i = 0; i < outline.Count; i++)
            vertices[i] = new Vector3(outline[i].x, 0.04f, outline[i].y);
        for (int i = 0; i < outline.Count - 2; i++)
        {
            triangles[i * 3] = 0;
            triangles[i * 3 + 1] = i + 1;
            triangles[i * 3 + 2] = i + 2;
        }
        regionMesh = new Mesh { name = "Local worker region" };
        regionMesh.vertices = vertices;
        regionMesh.triangles = triangles;
        regionMesh.RecalculateBounds();
        regionMeshFilter.sharedMesh = regionMesh;
    }

    private void OnDestroy()
    {
        if (regionMeshFilter != null) regionMeshFilter.sharedMesh = null;
        if (regionMesh != null) Destroy(regionMesh);
    }
}
