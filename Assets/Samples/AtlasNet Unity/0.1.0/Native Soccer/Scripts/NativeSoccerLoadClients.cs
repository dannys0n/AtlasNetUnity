#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AtlasNet;
using AtlasNet.Samples.Soccer;
using UnityEngine;
using NativeClient = AtlasNet.Native.AtlasNetNativeClient;

// Owns native connections on one background thread. Bots do not instantiate
// Unity replicas, animators, scenes, or a separate process.
internal sealed class NativeSoccerLoadClients : IDisposable
{
    private const int Limit = 200;
    private readonly bool scaleMode;
    private const int ParallelJoins = 8;
    private readonly string endpoint;
    private readonly double moveInterval;
    private readonly Thread worker;
    private volatile int target, connected, moving, failed;
    private volatile bool stopping;
    private volatile bool patrol;
    private volatile string error;
    private string count = "1";

    public NativeSoccerLoadClients(string host, int port, int tickRate, bool scaleMode = false)
    {
        this.scaleMode = scaleMode;
        endpoint = $"{host}:{port}";
        moveInterval = 1d / tickRate;
        worker = new Thread(Run) { IsBackground = true, Name = "AtlasNet Soccer load clients" };
        worker.Start();
    }

    public void SetTarget(int value) => target = Mathf.Clamp(value, 0, Limit);
    public string Status => $"Load clients {connected}/{target}  |  Active {moving}  |  Failed {failed}";

    public void DrawControls(bool isConnected)
    {
        GUILayout.Space(14);
        GUILayout.Label(Status);
        if (!scaleMode)
            patrol = GUILayout.Toggle(patrol, "Patrol (off: Soccer AI)");
        GUILayout.BeginHorizontal();
        GUILayout.Label("Clients per action");
        count = GUILayout.TextField(count, 3, GUILayout.Width(45));
        GUILayout.EndHorizontal();
        int.TryParse(count, out int amount);
        bool enabled = GUI.enabled;
        GUI.enabled = enabled && isConnected && amount > 0 && target < Limit;
        GUILayout.Space(7);
        if (GUILayout.Button("Spawn headless clients", GUILayout.Height(28))) SetTarget(target + amount);
        GUI.enabled = enabled && amount > 0 && target > 0;
        GUILayout.Space(10);
        if (GUILayout.Button("Despawn headless clients", GUILayout.Height(28))) SetTarget(target - amount);
        GUI.enabled = enabled && target > 0;
        GUILayout.Space(10);
        if (GUILayout.Button("Despawn all headless clients", GUILayout.Height(28))) SetTarget(0);
        GUI.enabled = enabled;
        if (!string.IsNullOrEmpty(error)) GUILayout.Label(error);
    }

    private void Run()
    {
        var bots = new List<Bot>();
        var buffer = new byte[65536];
        var time = Stopwatch.StartNew();
        var random = new System.Random();
        var joins = new List<Task<NativeClient.Handle>>();
        double nextJoin = 0;
        int nextBot = 0;
        try
        {
            while (!stopping)
            {
                double now = time.Elapsed.TotalSeconds;
                int desired = target;
                int active = 0;
                bool usePatrol = patrol;
                for (int i = bots.Count - 1; i >= 0; --i)
                {
                    var bot = bots[i];
                    if (i >= desired || !bot.Update(buffer, time, usePatrol))
                    {
                        bot.Dispose();
                        bots.RemoveAt(i);
                        continue;
                    }
                    if (bot.IsActive(usePatrol)) active++;
                }
                connected = bots.Count;
                moving = active;
                for (int i = joins.Count - 1; i >= 0; --i)
                {
                    if (!joins[i].IsCompleted) continue;
                    var handle = joins[i].GetAwaiter().GetResult();
                    joins.RemoveAt(i);
                    if (handle == null)
                    {
                        failed++;
                        nextJoin = now + .18;
                    }
                    else if (bots.Count < desired && !stopping)
                        bots.Add(new Bot(handle, now, .5f + (float)random.NextDouble() * .5f, scaleMode, nextBot++, moveInterval));
                    else handle.Dispose();
                }
                while (!stopping && bots.Count + joins.Count < desired &&
                    joins.Count < (scaleMode ? ParallelJoins : 1) && now >= nextJoin)
                {
                    joins.Add(Task.Run(Connect));
                    if (!scaleMode) nextJoin = now + .18;
                }
                Thread.Sleep(4);
            }
        }
        catch (Exception exception) { error = exception.Message; }
        finally
        {
            foreach (var bot in bots) bot.Dispose();
            connected = moving = 0;
            foreach (var join in joins)
                _ = join.ContinueWith(task => task.Result?.Dispose(), TaskScheduler.Default);
        }
    }

    private NativeClient.Handle Connect()
    {
        NativeClient.Handle handle = null;
        try
        {
            handle = NativeClient.anu_create();
            if (!handle.IsInvalid && NativeClient.anu_connect(handle, endpoint, Array.Empty<byte>(), 0) == NativeClient.Result.Ok)
                return handle;
        }
        catch (Exception exception) { error = exception.Message; }
        handle?.Dispose();
        return null;
    }

    public void Dispose() => stopping = true;

    private sealed class Bot : IDisposable
    {
        private readonly NativeClient.Handle handle;
        private readonly double joinDeadline;
        private readonly float speed;
        private readonly bool scaleMode;
        private readonly Vector3 scaleOrigin;
        private readonly double joinedAt;
        private readonly float patrolPhase;
        private ulong ownEntity, ball;
        private int team;
        private Vector3 position, ballPosition;
        private bool hasPosition;
        private bool readySent;
        private ulong nextSequence;
        private readonly double moveInterval;
        private double nextMove, nextContact;
        private double lastOwnTransform = double.NegativeInfinity;

        public Bot(NativeClient.Handle handle, double now, float speed, bool scaleMode, int ordinal, double moveInterval)
        {
            this.handle = handle;
            this.speed = speed;
            this.scaleMode = scaleMode;
            joinedAt = now;
            patrolPhase = ordinal * 2.39996323f;
            this.moveInterval = moveInterval;
            nextMove = now + moveInterval * (ordinal * .61803398875 % 1);
            scaleOrigin = new Vector3(Mathf.Lerp(-14, 14, (ordinal % 15) / 14f), 1,
                Mathf.Lerp(-11, 11, (ordinal / 15 % 14) / 13f));
            joinDeadline = now + 10;
        }

        public bool IsActive(bool patrol) => ownEntity != 0 && hasPosition && (scaleMode || patrol || ball != 0);

        public bool Update(byte[] buffer, Stopwatch time, bool patrol)
        {
            double now = time.Elapsed.TotalSeconds;
            // A busy receive queue must not block this client's next input.
            if (IsActive(patrol) && !SendDueInput(now, patrol)) return false;
            for (int i = 0; i < 96; i++)
            {
                var result = NativeClient.anu_receive(handle, buffer, (uint)buffer.Length, out uint size);
                if (result == NativeClient.Result.Empty) break;
                if (result != NativeClient.Result.Ok) return false;
                ReadSignal(buffer, size, now);
                if (time.Elapsed.TotalSeconds - now >= .00025) break;
            }
            if (!readySent && ownEntity != 0)
            {
                using var ready = new NetWriter();
                if (!Send(NativeShardBackend.ReadyMethod, ready, NetworkDelivery.ReliableOrdered))
                    return false;
                readySent = true;
            }
            return (ownEntity != 0 && hasPosition) || now < joinDeadline;
        }

        private bool SendDueInput(double now, bool patrol)
        {
            if (scaleMode)
            {
                if (now < nextMove) return true;
                AdvanceMoveDeadline(now);
                using var pose = new NetWriter();
                pose.Write(ScaleMotion.Evaluate(scaleOrigin, (float)(now - joinedAt) * 3f));
                return Send(NativeShardBackend.ScalePoseMethod, pose, NetworkDelivery.UnreliableSequenced);
            }
            if (now >= nextMove)
            {
                AdvanceMoveDeadline(now);
                Vector2 direction = patrol
                    ? PatrolDirection(now)
                    : NativeSoccerLoadDriver.Steer(position, ballPosition, team == 0 ? 52 : -52);
                Vector2 movement = direction * speed;
                using var writer = new NetWriter();
                writer.Write(movement.x);
                writer.Write(movement.y);
                if (!Send(NativeShardBackend.LoadMoveMethod, writer, NetworkDelivery.UnreliableSequenced)) return false;
            }
            if (!patrol && now >= nextContact && (position - ballPosition).sqrMagnitude < 25)
            {
                nextContact = now + .1;
                using var writer = new NetWriter();
                writer.Write(ball);
                if (!Send(NativeShardBackend.RemoteBallContactMethod, writer, NetworkDelivery.UnreliableSequenced)) return false;
            }
            return true;
        }

        private Vector2 PatrolDirection(double now)
        {
            float angle = patrolPhase + (float)(now - joinedAt) * speed * .075f;
            Vector2 waypoint = new(Mathf.Cos(angle) * 32f, Mathf.Sin(angle) * 18f);
            return Vector2.ClampMagnitude((waypoint - new Vector2(position.x, position.z)) * .5f, 1f);
        }

        private void AdvanceMoveDeadline(double now)
        {
            // Keep each client's phase and skip missed slots instead of sending stale input in a burst.
            nextMove += (Math.Floor((now - nextMove) / moveInterval) + 1) * moveInterval;
        }

        private bool Send(uint method, NetWriter writer, NetworkDelivery delivery)
        {
            byte[] packet = NativeShardBackend.EncodeGameplayCommand(NativeShardBackend.AdapterBehaviour,
                method, writer.ToArray(), ++nextSequence, delivery);
            return NativeClient.anu_send(handle, packet, (uint)packet.Length) != NativeClient.Result.Error;
        }

        private void ReadSignal(byte[] buffer, uint size, double now)
        {
            const int signalHeader = 21;
            const int content = 33;
            const int entityHeader = content + 20;
            if (size < content || BitConverter.ToUInt16(buffer, 11) != 7) return;
            uint signal = BitConverter.ToUInt32(buffer, signalHeader);
            if (signal == NativeShardBackend.SessionSignal && size >= content + 18)
            {
                ownEntity = BitConverter.ToUInt64(buffer, content + 9);
                team = buffer[content + 17];
                return;
            }
            if (signal == NativeShardBackend.SnapshotSignal && size > entityHeader + 1)
            {
                // Only the unowned ball is a world replica in Native Soccer.
                // Unity writes the prefab key as a seven-bit encoded string.
                int offset = entityHeader;
                int nameLength = 0, shift = 0;
                byte next;
                do
                {
                    if (offset >= size || shift >= 21) return;
                    next = buffer[offset++];
                    nameLength |= (next & 127) << shift;
                    shift += 7;
                } while ((next & 128) != 0);
                offset += nameLength;
                if (offset + 20 > size) return;
                ulong entity = BitConverter.ToUInt64(buffer, content);
                ulong owner = BitConverter.ToUInt64(buffer, offset);
                if (owner == 0)
                {
                    ball = entity;
                    ballPosition = ReadPosition(buffer, offset + 8);
                }
                else if (entity == ownEntity && now - lastOwnTransform > .25)
                {
                    position = ReadPosition(buffer, offset + 8);
                    hasPosition = true;
                }
            }
            if (signal == NativeShardBackend.TransformSignal && size >= entityHeader + 14 &&
                (buffer[entityHeader + 1] & 1) != 0)
            {
                ulong entity = BitConverter.ToUInt64(buffer, content);
                if (entity == ownEntity)
                {
                    position = ReadPosition(buffer, entityHeader + 2);
                    lastOwnTransform = now;
                    hasPosition = true;
                }
                else if (entity == ball) ballPosition = ReadPosition(buffer, entityHeader + 2);
            }
        }

        private static Vector3 ReadPosition(byte[] buffer, int offset) =>
            new(BitConverter.ToSingle(buffer, offset), BitConverter.ToSingle(buffer, offset + 4),
                BitConverter.ToSingle(buffer, offset + 8));

        public void Dispose() => handle.Dispose();
    }
}

internal sealed class NativeSoccerLoadDriver
{
    private readonly NetworkManager manager;
    private readonly Task parentPipe;
    private readonly double connectDeadline;
    private readonly float speedMultiplier = UnityEngine.Random.Range(.5f, 1f);
    private SoccerPlayer player;
    private SoccerBall ball;
    private Vector2 movement;
    private double nextDecision;
    private double quitAt;
    private bool connected;
    private int exitCode;

    public NativeSoccerLoadDriver(NetworkManager manager, bool parentControlled)
    {
        this.manager = manager;
        if (parentControlled) parentPipe = Task.Run(() => Console.In.ReadLine());
        connectDeadline = Time.unscaledTimeAsDouble + 20;
    }

    public void Update()
    {
        double now = Time.unscaledTimeAsDouble;
        if (quitAt > 0)
        {
            if (now >= quitAt) Application.Quit(exitCode);
            return;
        }
        connected |= manager.IsConnectedClient;
        bool requested = parentPipe?.IsCompleted == true;
        if (requested || !manager.IsRunning || (!connected && now >= connectDeadline))
        {
            exitCode = requested ? 0 : 1;
            if (manager.IsRunning) manager.Stop();
            quitAt = now + .5;
            return;
        }

        if (now < nextDecision) return;
        nextDecision = now + .1;
        if (!player || !player.IsSpawned || !ball || !ball.IsSpawned)
        {
            foreach (var replica in manager.SpawnedObjects)
            {
                if (replica.IsOwner && replica.TryGetComponent(out SoccerPlayer owner) && player != owner)
                {
                    player = owner;
                    player.WaveKeyEnabled = false;
                    player.KeyboardInput = () => movement;
                }
                if (replica.TryGetComponent(out SoccerBall currentBall)) ball = currentBall;
            }
        }

        movement = !player || !player.IsSpawned || !ball || !ball.IsSpawned
            ? Vector2.zero : Steer(player.transform.position, ball.transform.position, player.AttackingGoal.x) * speedMultiplier;
    }

    internal static Vector2 Steer(Vector3 playerPosition, Vector3 ballPosition, float attackingGoalX)
    {
        Vector3 offset = playerPosition - ballPosition;
        Vector2 relative = new(offset.x, offset.z);
        Vector2 targetGoal = new(attackingGoalX - ballPosition.x, -ballPosition.z);
        Vector2 goalDirection = targetGoal.normalized;
        Vector2 behindBall = -goalDirection * 3.2f;
        Vector2 side = new(-goalDirection.y, goalDirection.x);
        float lateral = Vector2.Dot(relative, side);
        bool canPush = Vector2.Dot(relative, goalDirection) < -.9f && Mathf.Abs(lateral) < 1.5f;
        Vector2 target = canPush ? goalDirection : behindBall;
        if (!canPush && Vector2.Dot(relative, goalDirection) > -.9f)
            target = behindBall + side * (lateral >= 0 ? 3.8f : -3.8f);
        return (target - relative).normalized;
    }
}
#endif
