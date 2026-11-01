using AtlasNet;
using AtlasNet.Samples.Soccer;
using UnityEngine;

namespace AtlasNet.M2
{
    public enum M2StageKind { Install, InterestAndScale, AnimatorSync, Soccer }

    public sealed class M2Stage : MonoBehaviour
    {
        public int index;
        public M2StageKind kind;
        public bool hasLiveDemo = true;
        [Tooltip("Present the live demo before its media slides.")]
        public bool slidesAfterDemo;
        public string title;
        public NetworkObject playerPrefab;
        public NetworkObject extraPrefab;
        [Range(0,200)] public int entityCount = 50;
        public Camera view;
        public bool followOwner;
        public Vector3 cameraOffset = new(0,32,-26);
        public Vector3 cameraAngles = new(51,0,0);
        public SoccerGoalPointer goalPointer;
        public ParticleSystem blueConfetti, orangeConfetti;
        [Tooltip("Shared presentation slide set; only the host/server navigates this list.")]
        public M2Slide[] slides = System.Array.Empty<M2Slide>();
        [Tooltip("Show full/left/right layout previews until real media is assigned.")]
        public bool previewSlidesWhenEmpty = true;
        [HideInInspector] public Texture2D[] screenshots = System.Array.Empty<Texture2D>();
    }
}
