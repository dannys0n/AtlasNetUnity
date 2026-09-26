using System.Collections.Generic;
using UnityEngine;

namespace AtlasNet
{
    // Development-only placement and interest rules. The future AtlasNet bridge
    // receives authority/residency decisions; it must not reproduce this policy.
    internal static class LocalWorldPolicy
    {
        internal static bool ShouldClientHold(SessionId session, NetworkObject target, bool alreadyResident,
            IEnumerable<NetworkObject> entities)
        {
            if (target.OwnerSession == session) return true;
            foreach (var source in entities)
            {
                if (source.OwnerSession != session) continue;
                var interest = source.GetComponent<NetworkInterestSource>();
                if (interest == null) continue;
                // The local demo sends every entity from the player's authoring worker.
                // Radius interest adds entities authored by other workers.
                if (target.SimulationWorker == source.SimulationWorker) return true;
                if (!interest.isActiveAndEnabled) continue;
                if (WithinRadius(source.transform.position, target.transform.position,
                    interest.Radius + (alreadyResident ? interest.ExitPadding : 0f))) return true;
            }
            return false;
        }

        internal static bool ShouldWorkerHold(SessionId worker, NetworkObject target, bool alreadyResident,
            IEnumerable<NetworkObject> entities, ILocalAuthorityPlacement world,
            bool targetHandoffPending, bool workerInvolvedInHandoff)
        {
            if (target.SimulationWorker == worker.Value || workerInvolvedInHandoff) return true;
            if (world == null) return false;
            foreach (var source in entities)
            {
                if (source == target || source.OwnerSession.Value == 0 ||
                    source.SimulationWorker != worker.Value) continue;
                var interest = source.GetComponent<NetworkInterestSource>();
                if (interest == null || !interest.isActiveAndEnabled) continue;
                float radius = interest.Radius + (alreadyResident ? interest.ExitPadding : 0f);
                Vector3 center = source.transform.position;
                // The region check only skips unlikely owners. A pending transfer can
                // briefly put an entity outside its recorded owner's region.
                if (!world.IsWithinInterest(target.SimulationWorker, center.x, center.z, radius) &&
                    !targetHandoffPending &&
                    world.OwnerAt(target.transform.position.x, target.transform.position.z) == target.SimulationWorker)
                    continue;
                if (WithinRadius(center, target.transform.position, radius)) return true;
            }
            return false;
        }

        internal static bool TryHandoffDestination(NetworkObject obj, ILocalAuthorityPlacement world,
            float boundaryMargin, out ulong destination)
        {
            destination = 0;
            if (world == null || obj.GetComponent<NetworkTransform>() is not NetworkTransform movement ||
                !movement.SyncPosition || movement.Target != obj.transform ||
                (movement.PositionWriter == TransformWriter.Owner && obj.OwnerSession.Value == 0)) return false;
            Vector3 position = obj.transform.position;
            destination = world.OwnerAt(position.x, position.z);
            return destination != obj.SimulationWorker &&
                world.ShouldMove(obj.SimulationWorker, position.x, position.z, boundaryMargin);
        }

        internal static bool WithinRadius(Vector3 center, Vector3 target, float radius)
        {
            float dx = target.x - center.x, dz = target.z - center.z;
            return dx * dx + dz * dz <= radius * radius;
        }
    }
}
