using AtlasNet;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>A local-only, camera-facing marker for this viewer's avatar.</summary>
[RequireComponent(typeof(NetworkObject))]
public sealed class SampleOwnerMarker : MonoBehaviour
{
    [SerializeField] Material material;
    [SerializeField] Transform visualAnchor;
    NetworkObject entity;
    Transform marker;
    static Mesh disc;

#if UNITY_EDITOR
    AtlasNet.Demos.LocalWorkerDemo console;
#endif
    void Awake()
    {
        entity = GetComponent<NetworkObject>();
#if UNITY_EDITOR
        console = FindAnyObjectByType<AtlasNet.Demos.LocalWorkerDemo>();
#endif
    }

    void LateUpdate()
    {
        var viewer = entity.Manager;
#if UNITY_EDITOR
        if (console && console.Primary && console.Primary.IsRunning) viewer = console.Primary;
#endif
        bool owned = entity.IsSpawned && viewer && viewer.IsClient &&
            entity.OwnerSession.Value != 0 && entity.OwnerSession == viewer.LocalSession;
        if (!owned) { if (marker) marker.gameObject.SetActive(false); return; }
        if (!marker)
        {
            // Cache the head height before adding this renderer to the hierarchy.
            Transform anchor = visualAnchor ? visualAnchor : transform;
            float height = transform.position.y + 1.8f;
            foreach (var body in GetComponentsInChildren<Renderer>())
                height = Mathf.Max(height, body.bounds.max.y);
            var visual = new GameObject("Local owner marker");
            marker = visual.transform;
            marker.SetParent(anchor, false);
            marker.position = new Vector3(anchor.position.x, height + .75f, anchor.position.z);
            visual.AddComponent<MeshFilter>().sharedMesh = Disc();
            var renderer = visual.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
        marker.gameObject.SetActive(true);
        var camera = Camera.main;
        if (camera) marker.rotation = camera.transform.rotation;
    }

    static Mesh Disc()
    {
        if (disc) return disc;
        const int segments = 24;
        var vertices = new Vector3[segments + 1];
        var triangles = new int[segments * 3];
        for (int i = 0; i < segments; i++)
        {
            float angle = i * Mathf.PI * 2f / segments;
            vertices[i + 1] = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0) * .085f;
            triangles[i * 3] = 0;
            triangles[i * 3 + 1] = i + 1;
            triangles[i * 3 + 2] = (i + 1) % segments + 1;
        }
        disc = new Mesh { name = "Sample owner circle", vertices = vertices, triangles = triangles };
        disc.RecalculateBounds();
        return disc;
    }
}
