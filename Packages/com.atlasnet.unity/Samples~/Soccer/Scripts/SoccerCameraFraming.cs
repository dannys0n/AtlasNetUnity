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
            // The near, upper corner of the goal has the narrowest horizontal view.
            // Use that depth so the whole mouth, including the posts, remains visible.
            Vector3 nearGoalTop = new(focus.x, SoccerBall.GoalHeight, -SoccerBall.GoalHalfWidth - 2);
            float depth = Mathf.Max(view.nearClipPlane, Vector3.Dot(nearGoalTop - position, rotation * Vector3.forward));
            float halfWidth = view.orthographic ? view.orthographicSize * view.aspect :
                depth * Mathf.Tan(view.fieldOfView * .5f * Mathf.Deg2Rad) * view.aspect;
            // Leave eight percent of the viewport beyond the goal for comfortable framing.
            float limit = Mathf.Max(0, SoccerBall.GoalLineX - halfWidth * .84f);
            focus.x = Mathf.Clamp(focus.x, -limit, limit);
            view.transform.SetPositionAndRotation(focus + offset, rotation);
        }
    }
}
