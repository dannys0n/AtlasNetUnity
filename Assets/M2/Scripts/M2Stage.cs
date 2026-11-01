using AtlasNet;
using AtlasNet.Samples.Soccer;
using UnityEngine;

namespace AtlasNet.M2
{
    public sealed class M2Stage : MonoBehaviour
    {
        public int index;
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
        public Texture2D[] screenshots = System.Array.Empty<Texture2D>();
    }
}
