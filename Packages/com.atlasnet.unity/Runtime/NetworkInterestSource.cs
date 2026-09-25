using UnityEngine;

namespace AtlasNet
{
    /// <summary>Optional local-demo player interest origin. The backend owns replica routing.</summary>
    [AddComponentMenu("AtlasNet/Network Interest Source")]
    public sealed class NetworkInterestSource : MonoBehaviour
    {
        [SerializeField, Min(0f)] private float radius = 8f;
        [SerializeField, Min(0f), Tooltip("Extra distance before an existing ghost is removed.")]
        private float exitPadding = 1f;

        public float Radius => Mathf.Max(0f, radius);
        public float ExitPadding => Mathf.Max(0f, exitPadding);
    }
}
