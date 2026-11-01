using UnityEngine;

namespace AtlasNet.Samples.Soccer
{
    /// <summary>Local camera framing shared by the sample and M2 presentation.</summary>
    public static class SoccerCameraFraming
    {
        public static void Follow(Camera view, Vector3 target, Vector3 offset, Quaternion rotation)
        {
            // Keep the existing sideline framing; horizontal travel depends on the viewport.
            Vector3 focus = new(target.x, 0, Mathf.Clamp(target.z, -18, 18));
            Vector3 position = focus + offset;
            // The near touchline is narrower in perspective than the goal mouth.
            // Frame its ground-level corners so the camera can follow farther toward either end.
            Vector3 nearTouchline = new(focus.x, 0, -33);
            float depth = Mathf.Max(view.nearClipPlane, Vector3.Dot(nearTouchline - position, rotation * Vector3.forward));
            float halfWidth = view.orthographic ? view.orthographicSize * view.aspect :
                depth * Mathf.Tan(view.fieldOfView * .5f * Mathf.Deg2Rad) * view.aspect;
            // Leave eight percent of the viewport beyond the near field edge.
            float limit = Mathf.Max(0, 52.5f - halfWidth * .84f);
            focus.x = Mathf.Clamp(focus.x, -limit, limit);
            view.transform.SetPositionAndRotation(focus + offset, rotation);
        }
    }
}
