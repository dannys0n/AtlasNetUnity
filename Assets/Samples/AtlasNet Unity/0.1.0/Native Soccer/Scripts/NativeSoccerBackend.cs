using System;
using System.Collections.Generic;
using AtlasNet;
using AtlasNet.Samples.Soccer;
using UnityEngine;
using EntityId = AtlasNet.EntityId;

internal sealed class NativeSoccerBackend : NativeShardBackend
{
    private readonly Dictionary<EntityId, double> lastRemoteContacts = new();
    private readonly bool traceRemoteContacts =
        Environment.GetEnvironmentVariable("ATLASNET_SOCCER_TRACE_REMOTE_CONTACT") == "1";

    public NativeSoccerBackend(NetworkManager manager, NetworkObject observerPrefab)
        : base(manager, observerPrefab) { }

    protected override bool UsesSpatialPoseBatch(NetworkObject obj) => obj.TryGetComponent<ScaleMotion>(out _);

    protected override byte GetAdmissionTeam(NetworkObject obj) =>
        obj.TryGetComponent(out SoccerPlayer player) ? (byte)player.Team : (byte)0;

    protected override void OnReplicaRemoved(EntityId id) => lastRemoteContacts.Remove(id);
    protected override void OnStopped() => lastRemoteContacts.Clear();

    protected override void ReceiveSampleAuthorityCommand(NetworkObject obj, uint method, byte[] arguments)
    {
        if (method == RemoteBallContactMethod) ApplyRemoteBallContact(obj, arguments);
    }

    protected override void ReceiveSampleClientCommand(NetworkObject obj, uint method, byte[] arguments)
    {
        if (method == ScalePoseMethod && arguments.Length == 12 &&
            obj.OwnerSession.Value != 0 && obj.TryGetComponent<ScaleMotion>(out _))
        {
            using var pose = new NetReader(arguments);
            Vector3 position = pose.ReadVector3();
            if (float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z) &&
                Mathf.Abs(position.x) <= 15 && position.y >= 0 && position.y <= 3 &&
                Mathf.Abs(position.z) <= 12)
                obj.transform.position = position;
        }
        if (method == LoadMoveMethod && arguments.Length == 8 && obj.TryGetComponent(out SoccerPlayer bot))
        {
            using var input = new NetReader(arguments);
            bot.ApplyMoveInput(new Vector2(input.ReadFloat(), input.ReadFloat()));
        }
        if (method == RemoteBallContactMethod && arguments.Length == sizeof(ulong) && obj.OwnerSession.Value != 0)
            ReportRemoteBallContact(obj, arguments);
    }

    protected override void ProbeSampleContact()
    {
        AtlasNet.Samples.Soccer.SoccerBall ball = null;
        NetworkObject player = null;
        foreach (var obj in manager.SpawnedObjects)
        {
            if (obj.IsOwner && obj.TryGetComponent<AtlasNet.Samples.Soccer.SoccerPlayer>(out _))
                player = obj;
            if (obj.TryGetComponent(out AtlasNet.Samples.Soccer.SoccerBall candidate)) ball = candidate;
        }
        if (!player || !ball || player.SimulationWorker == ball.NetworkObject.SimulationWorker ||
            !player.TryGetComponent(out CapsuleCollider capsule)) return;

        if (!AtlasNet.Samples.Soccer.SoccerBall.TryGetContactCapsule(capsule,
                out Vector3 center, out float halfSegment, out float radius)) return;
        // The local player's collider can be disabled for physics on a native
        // replica. Query its geometry directly and retry while contact is plausible.
        if (!ball.IsWithinContactReach(center, halfSegment, radius, .65f) ||
            !CanSendSampleCommand) return;
        SendClientCommand(AdapterBehaviour, RemoteBallContactMethod, writer => writer.Write(ball.NetworkObject.EntityId.Value));
    }

    private void ReportRemoteBallContact(NetworkObject source, byte[] arguments)
    {
        if (!source.TryGetComponent(out SoccerPlayer player) || player.MoveInput.sqrMagnitude < .01f ||
            !source.TryGetComponent(out CapsuleCollider capsule) ||
            !SoccerBall.TryGetContactCapsule(capsule, out Vector3 center,
                out float halfSegment, out float radius)) return;
        using var input = new NetReader(arguments);
        var target = new EntityId(input.ReadULong());
        if (target.Value == 0 || manager.TryGet(target, out _)) return;

        double now = Time.unscaledTimeAsDouble;
        if (lastRemoteContacts.TryGetValue(source.EntityId, out double last) && now - last < .075) return;
        lastRemoteContacts[source.EntityId] = now;

        using var payload = new NetWriter();
        payload.Write(source.EntityId.Value);
        payload.Write(LocalWorkerId);
        payload.Write(source.transform.position);
        payload.Write(player.MoveInput.x);
        payload.Write(player.MoveInput.y);
        payload.Write(center);
        payload.Write(halfSegment);
        payload.Write(radius);
        SendRemoteAuthorityInteraction(source, target, AdapterBehaviour, RemoteBallContactMethod, payload.ToArray());
    }

    private void ApplyRemoteBallContact(NetworkObject target, byte[] arguments)
    {
        if (!target.TryGetComponent(out SoccerBall ball)) return;
        using var payload = new NetReader(arguments);
        ulong player = payload.ReadULong();
        ulong sourceWorker = payload.ReadULong();
        Vector3 playerPosition = payload.ReadVector3();
        Vector2 move = new(payload.ReadFloat(), payload.ReadFloat());
        Vector3 center = payload.ReadVector3();
        float halfSegment = payload.ReadFloat();
        float radius = payload.ReadFloat();
        if (payload.HasRemaining || sourceWorker == LocalWorkerId) return;

        if (ball.TryRemoteContact(player, playerPosition, move, center, halfSegment, radius) && traceRemoteContacts)
            Debug.Log($"Soccer cross-shard kick player={player} from={sourceWorker} ball={target.EntityId.Value} to={LocalWorkerId}");
    }

 }
