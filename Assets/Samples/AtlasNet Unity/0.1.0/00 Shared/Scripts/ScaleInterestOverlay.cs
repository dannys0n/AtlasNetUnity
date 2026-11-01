using AtlasNet;
using UnityEngine;

/// <summary>Shows each server's resident interest sources, optionally also the owning client's source.</summary>
public sealed class ScaleInterestOverlay : MonoBehaviour
{
    [SerializeField] private NetworkObject player;
    [SerializeField] private NetworkInterestSource interest;
    [SerializeField] private Material fillMaterial;
    [SerializeField] private bool showOnClients = true;

    private GameObject disc;
    private Mesh discMesh;
    public Renderer DebugRenderer => disc ? disc.GetComponent<Renderer>() : null;

    private void LateUpdate()
    {
        bool show = player != null && player.IsSpawned && interest != null && interest.Radius > 0f &&
            (player.Manager.IsServer || (showOnClients && player.IsOwner));
        if (!show)
        {
            if (disc != null) disc.SetActive(false);
            return;
        }

        if (disc == null) CreateDisc();
        if (!disc.activeSelf) disc.SetActive(true);
        disc.transform.position = new Vector3(transform.position.x, 0.02f, transform.position.z);
        disc.transform.localScale = new Vector3(interest.Radius, 1f, interest.Radius);
    }

    private void CreateDisc()
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

        discMesh = new Mesh { name = "Client interest radius" };
        discMesh.vertices = vertices;
        discMesh.triangles = triangles;
        discMesh.RecalculateBounds();
        disc = new GameObject("Client interest radius");
        disc.AddComponent<MeshFilter>().sharedMesh = discMesh;
        disc.AddComponent<MeshRenderer>().sharedMaterial = fillMaterial;
    }

    private void OnDisable()
    {
        if (disc != null) disc.SetActive(false);
    }

    private void OnDestroy()
    {
        if (disc != null) Destroy(disc);
        if (discMesh != null) Destroy(discMesh);
    }
}
