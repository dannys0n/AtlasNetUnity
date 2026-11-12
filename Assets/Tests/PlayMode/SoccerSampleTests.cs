#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using AtlasNet;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object=UnityEngine.Object;

namespace AtlasNetDemo.Tests
{
    public sealed class SoccerSampleTests
    {
        static void Set(object obj,string name,object value)=>obj.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).SetValue(obj,value);
        [UnityTest]
        public IEnumerator ServerMovementSolidContactsGoalCelebrationLateJoinAndHandoff()
        {
            const string root="Assets/Samples/AtlasNet Unity/0.1.0/Soccer/Prefabs/";
            var player=AssetDatabase.LoadAssetAtPath<NetworkObject>(root+"SoccerPlayer.prefab");
            var registry=AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(root+"SoccerNetworkPrefabs.asset");
            Assert.IsFalse(player.GetComponent<Collider>().isTrigger);
            Assert.AreEqual(TransformWriter.Server,player.GetComponent<NetworkTransform>().PositionWriter);
            Assert.AreEqual(TransformWriter.Server,player.GetComponent<NetworkTransform>().RotationWriter);
            Assert.AreEqual(AnimatorWriter.Server,player.GetComponent<NetworkAnimator>().Writer);
            NetworkManager Create(string name,ClientTransport transport=ClientTransport.Udp)
            {
                var manager=new GameObject(name).AddComponent<NetworkManager>();
                Set(manager,"playerPrefab",player);Set(manager,"networkPrefabsLists",new[]{registry});Set(manager,"automaticLocalHandoffs",true);Set(manager,"localBoundaryMargin",.05f);
                manager.ConfigureConnection("127.0.0.1",17941,transport);return manager;
            }
            var server=Create("Soccer test server");var worker=Create("Soccer test worker");var client=Create("Soccer test UDP");var late=Create("Soccer test WebSocket",ClientTransport.WebSocket);
            var managers=new[]{server,worker,client,late};
            var scenes=managers.Select(m=>SceneManager.CreateScene(m.name,new CreateSceneParameters(LocalPhysicsMode.Physics3D))).ToArray();
            foreach(var scene in scenes)
            {
                var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.name="Soccer physics floor";
                floor.transform.position=new Vector3(0,-.5f,0);floor.transform.localScale=new Vector3(105,1,68);SceneManager.MoveGameObjectToScene(floor,scene);
            }
            void Prepare()
            {
                for(int i=0;i<managers.Length;i++)foreach(var obj in managers[i].SpawnedObjects.ToArray())
                {
                    if(obj.gameObject.scene!=scenes[i])SceneManager.MoveGameObjectToScene(obj.gameObject,scenes[i]);
                    if(!managers[i].IsServer && obj.GetComponent("SoccerPlayer") is Behaviour movement)movement.enabled=false;
                }
            }
            IEnumerator Step()
            {
                Prepare();yield return new WaitForFixedUpdate();Prepare();foreach(var scene in scenes)scene.GetPhysicsScene().Simulate(Time.fixedDeltaTime);yield return null;
            }
            IEnumerator Until(Func<bool> condition,string reason)
            {
                float end=Time.realtimeSinceStartup+10;while(!condition()&&Time.realtimeSinceStartup<end)yield return Step();Assert.IsTrue(condition(),reason);
            }
            NetworkObject Authority(AtlasNet.EntityId id)=>managers.Where(m=>m.IsServer).SelectMany(m=>m.SpawnedObjects).First(o=>o.EntityId==id&&o.HasAuthority);
            try
            {
                server.PlayerSpawnPosition=s=>new Vector3(-12,0,-12);server.StartServer();worker.StartWorker();client.StartClient();
                yield return Until(()=>server.WorkerCount==2&&client.IsConnectedClient&&client.SpawnedCount==1,"UDP joins shared world");
                var local=client.SpawnedObjects.Single(o=>o.IsOwner);var movement=local.GetComponent("SoccerPlayer");((Behaviour)movement).enabled=false;
                var setMove=movement.GetType().GetMethod("SetMoveRpc");
                void Input(Vector2 value)=>setMove.Invoke(movement,new object[]{value});
                Assert.IsTrue(local.GetComponent<Rigidbody>().isKinematic,"owning client does not simulate movement");
                Assert.AreEqual(RigidbodyInterpolation.None,local.GetComponent<Rigidbody>().interpolation,"network interpolation is the client's only smoothing path");
                Assert.IsFalse(local.GetComponent<Rigidbody>().detectCollisions,"clients do not run gameplay contacts");
                var start=local.transform.position;
                for(int i=0;i<12;i++){Input(Vector2.right);yield return Step();}
                Assert.Greater(local.transform.position.x,start.x+.3f,"owner input drives server movement");
                Assert.IsTrue(Authority(local.EntityId).GetComponent<Animator>().GetCurrentAnimatorStateInfo(0).IsName("Walk"));
                Assert.IsFalse(Authority(local.EntityId).GetComponent<Rigidbody>().isKinematic);
                Input(Vector2.zero);yield return Step();
                var ball=server.Spawn(AssetDatabase.LoadAssetAtPath<NetworkObject>(root+"SoccerBall.prefab"),new Vector3(-4,1f,0),Quaternion.identity);
                yield return Until(()=>client.SpawnedCount==2,"player and ball replicated");
                Authority(local.EntityId).GetComponent<Rigidbody>().position=new Vector3(-6.6f,0,0);
                for(int i=0;i<14;i++){Input(Vector2.right);yield return Step();}
                Input(Vector2.zero);yield return Step();
                var body=Authority(ball.EntityId).GetComponent<Rigidbody>();
                Assert.Greater(body.linearVelocity.x,4,"solid moving contact kicks the ball");
                Assert.Greater(body.position.y,1.05f,"player contact gives the ball upward motion");
                float peak=body.linearVelocity.magnitude;
                for(int i=0;i<25;i++)yield return Step();
                body=Authority(ball.EntityId).GetComponent<Rigidbody>();
                Assert.Less(body.linearVelocity.magnitude,peak*.8f,"drag slows ball after contact");
                Assert.GreaterOrEqual(body.position.y,.95f,"gravity and floor keep the ball above the pitch");
                yield return Until(()=>worker.TryGet(ball.EntityId,out var remote)&&remote.HasAuthority,"ball crosses worker boundary");
                int team=(int)movement.GetType().GetProperty("Team").GetValue(movement);
                int direction=team==0?1:-1;
                Authority(ball.EntityId).GetComponent<Rigidbody>().position=new Vector3(direction*48,1f,0);
                Authority(ball.EntityId).GetComponent<Rigidbody>().linearVelocity=Vector3.zero;
                yield return Until(()=>server.PendingHandoffCount==0&&Authority(ball.EntityId).transform.position.x*direction>40,"ball approaches horizontal goal");
                Authority(ball.EntityId).GetComponent<Rigidbody>().position=new Vector3(direction*52,1f,0);
                yield return Until(()=>Score(Authority(ball.EntityId),team==0?"BlueScore":"OrangeScore")==1,"goal scored at horizontal end");
                yield return Until(()=>Vector3.ProjectOnPlane(Authority(ball.EntityId).transform.position,Vector3.up).sqrMagnitude<.01f,"goal resets ball");
                float celebrationDeadline=Time.realtimeSinceStartup+1f;
                while(Time.realtimeSinceStartup<celebrationDeadline)
                {
                    Input(Vector2.right);yield return Step();
                    var animation=Authority(local.EntityId).GetComponent<Animator>();
                    if(animation.GetCurrentAnimatorStateInfo(0).IsName("Walk")&&animation.GetCurrentAnimatorStateInfo(1).IsName("Wave"))break;
                }
                var celebrating=Authority(local.EntityId).GetComponent<Animator>();
                Assert.IsTrue(celebrating.GetCurrentAnimatorStateInfo(0).IsName("Walk"),"goal celebration retains walking base layer");
                Assert.IsTrue(celebrating.GetCurrentAnimatorStateInfo(1).IsName("Wave"),"scoring team automatically waves");
                Input(Vector2.zero);
                late.StartClient();yield return Until(()=>late.IsConnectedClient&&late.SpawnedCount==3,"WebSocket late join");
                Assert.IsTrue(late.TryGet(ball.EntityId,out var lateBall));Assert.AreEqual(1,Score(lateBall,team==0?"BlueScore":"OrangeScore"));
                Assert.AreEqual(0,Score(lateBall,team==0?"OrangeScore":"BlueScore"));
            }
            finally
            {
                foreach(var manager in managers){manager.Stop();Object.DestroyImmediate(manager.gameObject);}
                foreach(var scene in scenes)SceneManager.UnloadSceneAsync(scene);
            }
        }
        [UnityTest]
        public IEnumerator CrowdedBallLiftsClearOfPlayersAndStaysBelowCeiling()
        {
            const string root="Assets/Samples/AtlasNet Unity/0.1.0/Soccer/Prefabs/";
            var player=AssetDatabase.LoadAssetAtPath<NetworkObject>(root+"SoccerPlayer.prefab");
            var ballPrefab=AssetDatabase.LoadAssetAtPath<NetworkObject>(root+"SoccerBall.prefab");
            var registry=AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(root+"SoccerNetworkPrefabs.asset");
            var manager=new GameObject("Soccer crowded ball test").AddComponent<NetworkManager>();Set(manager,"networkPrefabsLists",new[]{registry});manager.ConfigureConnection("127.0.0.1",17943,ClientTransport.Udp);
            var scene=SceneManager.CreateScene("Soccer crowded ball physics",new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.transform.position=new Vector3(0,-.5f,0);floor.transform.localScale=new Vector3(105,1,68);SceneManager.MoveGameObjectToScene(floor,scene);
            var roof=GameObject.CreatePrimitive(PrimitiveType.Cube);roof.transform.position=new Vector3(0,8.25f,0);roof.transform.localScale=new Vector3(105,.5f,68);SceneManager.MoveGameObjectToScene(roof,scene);
            AsyncOperation unload=null;
            try
            {
                manager.StartServer();var ball=manager.Spawn(ballPrefab,Vector3.up,Quaternion.identity);var body=ball.GetComponent<Rigidbody>();SceneManager.MoveGameObjectToScene(ball.gameObject,scene);
                var dirs=new[]{Vector2.right,Vector2.left,Vector2.up,Vector2.down};var players=new NetworkObject[4];
                var capsule=player.GetComponent<CapsuleCollider>();
                float worldRadius=capsule.radius*player.transform.localScale.x;
                // Keep an upward escape corridor past the capsule's widest cross-section.
                // Contact offsets still generate contacts at the lower rounded caps.
                float ring=worldRadius+1f;
                for(int j=0;j<4;j++)
                {
                    players[j]=manager.Spawn(player,new Vector3(-dirs[j].x*ring,0,-dirs[j].y*ring),Quaternion.identity);SceneManager.MoveGameObjectToScene(players[j].gameObject,scene);
                    // Hold the surrounding bodies in place so they cannot open a horizontal escape gap.
                    players[j].GetComponent<Rigidbody>().constraints=RigidbodyConstraints.FreezeAll;
                }
                float peak=body.position.y;
                for(int i=0;i<150;i++)
                {
                    for(int j=0;j<4;j++)
                    {
                        var movement=players[j].GetComponent("SoccerPlayer");movement.GetType().GetProperty("MoveInput").SetValue(movement,dirs[j]);Set(movement,"inputReceived",Time.unscaledTime);
                    }
                    yield return new WaitForFixedUpdate();scene.GetPhysicsScene().Simulate(Time.fixedDeltaTime);
                    peak=Mathf.Max(peak,body.position.y);Assert.GreaterOrEqual(body.position.y,.85f);Assert.LessOrEqual(body.position.y,7.15f);
                }
                float clearHeight=player.GetComponent<CapsuleCollider>().height*player.transform.localScale.y+1f;
                Assert.Greater(peak,clearHeight,"sustained moving contacts must loft the ball above the players");
            }
            finally {manager.Stop();Object.DestroyImmediate(manager.gameObject);unload=SceneManager.UnloadSceneAsync(scene);}
            if(unload!=null)yield return unload;
        }
        [UnityTest]
        public IEnumerator HandoffRestoresHistoricalScoresWithoutReplayingCelebration()
        {
            const string root="Assets/Samples/AtlasNet Unity/0.1.0/Soccer/Prefabs/";
            var manager=new GameObject("Soccer celebration snapshot test").AddComponent<NetworkManager>();
            Set(manager,"networkPrefabsLists",new[]{AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(root+"SoccerNetworkPrefabs.asset")});
            manager.ConfigureConnection("127.0.0.1",17945);
            var flags=BindingFlags.NonPublic|BindingFlags.Instance;
            var write=typeof(NetworkObject).GetMethod("WriteHandoff",flags);
            var read=typeof(NetworkObject).GetMethod("ReadHandoff",flags);
            byte[] Capture(NetworkObject obj) { using var writer=new NetWriter();write.Invoke(obj,new object[]{writer});return writer.ToArray(); }
            void Restore(NetworkObject obj,byte[] bytes) { using var reader=new NetReader(bytes);read.Invoke(obj,new object[]{reader});Assert.IsFalse(reader.HasRemaining); }
            try
            {
                manager.StartServer();
                var ball=manager.Spawn(AssetDatabase.LoadAssetAtPath<NetworkObject>(root+"SoccerBall.prefab"),new Vector3(0,1,0),Quaternion.identity);
                var match=(NetworkBehaviour)ball.GetComponent("SoccerMatch");
                var pending=match.GetType().GetField("pendingCelebration",flags);
                match.GetType().GetMethod("RecordGoal").Invoke(match,new object[]{0});
                Assert.IsTrue((bool)pending.GetValue(match),"A real goal schedules a celebration.");
                byte[] unprocessed=Capture(ball);
                match.OnNetworkTick();Assert.IsFalse((bool)pending.GetValue(match));
                byte[] processed=Capture(ball);
                // A stale resident may have missed a score delta before handoff.
                var sequence=match.GetType().GetField("GoalSequence").GetValue(match);
                using(var writer=new NetWriter()) { writer.Write(0);using var reader=new NetReader(writer.ToArray());((INetworkVariable)sequence).Read(reader); }
                Restore(ball,processed);
                Assert.AreEqual(1,Score(ball,"BlueScore"));
                Assert.IsFalse((bool)pending.GetValue(match),"Historical score restoration must not schedule another wave.");
                Restore(ball,processed);
                Assert.IsFalse((bool)pending.GetValue(match),"An identical snapshot must not schedule another wave.");
                Restore(ball,unprocessed);
                Assert.IsTrue((bool)pending.GetValue(match),"A genuinely unprocessed celebration survives handoff.");
                match.OnNetworkTick();Assert.IsFalse((bool)pending.GetValue(match));
                yield return null;
            }
            finally { manager.Stop();Object.DestroyImmediate(manager.gameObject); }
        }

        [UnityTest]
        public IEnumerator PredictedSoccerMovesImmediatelyKicksAndReconcilesAfterHandoff()
        {
            const string root = "Assets/Samples/AtlasNet Unity/0.1.0/";
            var player = AssetDatabase.LoadAssetAtPath<NetworkObject>(root + "Soccer/PredictedSoccerPlayer.prefab");
            var registry = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(root + "Soccer/PredictedSoccerPrefabs.asset");
            var ballPrefab = AssetDatabase.LoadAssetAtPath<NetworkObject>(root + "Soccer/Prefabs/SoccerBall.prefab");
            Assert.IsNotNull(player);
            Assert.IsNull(player.GetComponent<NetworkTransform>());
            Assert.IsNull(player.GetComponent<Rigidbody>());
            Assert.IsNotNull(player.GetComponent<CharacterController>());

            int port = TestPorts.FindAvailablePort();
            var transport = ScriptableObject.CreateInstance<DelayedPredictionTransport>();
            transport.WorkerCommitDelaySeconds = .35f;
            NetworkManager Create(string name)
            {
                var manager = new GameObject(name).AddComponent<NetworkManager>();
                Set(manager, "playerPrefab", player);
                Set(manager, "networkPrefabsLists", new[] { registry });
                Set(manager, "automaticLocalHandoffs", true);
                Set(manager, "localBoundaryMargin", .05f);
                Set(manager, "transportProvider", transport);
                manager.ConfigureConnection("127.0.0.1", port, ClientTransport.Udp);
                return manager;
            }

            var server = Create("Predicted soccer server");
            var worker = Create("Predicted soccer worker");
            var client = Create("Predicted soccer client");
            var managers = new[] { server, worker, client };
            var scenes = managers.Select(m => SceneManager.CreateScene(m.name,
                new CreateSceneParameters(LocalPhysicsMode.Physics3D))).ToArray();
            void PlaceCube(Scene scene, Vector3 position, Vector3 scale)
            {
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.transform.position = position;
                box.transform.localScale = scale;
                SceneManager.MoveGameObjectToScene(box, scene);
            }
            foreach (var scene in scenes)
                PlaceCube(scene, new Vector3(0, -.5f, 0), new Vector3(105, 1, 68));
            PlaceCube(scenes[1], new Vector3(8, 1, 0), new Vector3(.2f, 3, 4));

            void PlaceReplicas()
            {
                for (int i = 0; i < managers.Length; i++)
                    foreach (var obj in managers[i].SpawnedObjects)
                        if (obj.gameObject.scene != scenes[i])
                            SceneManager.MoveGameObjectToScene(obj.gameObject, scenes[i]);
            }

            IEnumerator Until(Func<bool> condition, string phase)
            {
                float deadline = Time.realtimeSinceStartup + 8f;
                while (true)
                {
                    PlaceReplicas();
                    if (condition()) yield break;
                    if (Time.realtimeSinceStartup >= deadline) Assert.Fail($"Timed out: {phase}");
                    yield return new WaitForFixedUpdate();
                    PlaceReplicas();
                    foreach (var scene in scenes) scene.GetPhysicsScene().Simulate(Time.fixedDeltaTime);
                    yield return null;
                }
            }

            try
            {
                server.PlayerSpawnPosition = _ => new Vector3(-5, 0, 0);
                server.StartServer();
                worker.StartWorker();
                client.StartClient();
                yield return Until(() => server.WorkerCount == 2 && client.IsConnectedClient && client.SpawnedCount == 1,
                    "client and worker join");
                var local = client.SpawnedObjects.Single(o => o.IsOwner);
                Assert.IsTrue(local.GetComponent<CharacterController>().enabled);
                Assert.IsTrue(server.TryGet(local.EntityId, out var initialAuthority));
                ((Behaviour)local.GetComponent("SoccerPlayer")).enabled = false;
                var motor = local.GetComponent("PredictedSoccerMotor");
                var setInput = motor.GetType().GetMethod("SetInput");
                void Drive(Vector2 input) => setInput.Invoke(motor, new object[] { input });

                var ball = server.Spawn(ballPrefab, new Vector3(.75f, 1, 0), Quaternion.identity);
                yield return Until(() => worker.TryGet(ball.EntityId, out var replica) && replica.HasAuthority &&
                    client.TryGet(ball.EntityId, out _), "ball is authoritative on the second worker");
                var ballBody = worker.SpawnedObjects.Single(o => o.EntityId == ball.EntityId).GetComponent<Rigidbody>();
                Assert.IsTrue(client.TryGet(ball.EntityId, out var localBall));
                var controller = local.GetComponent<CharacterController>();
                var original = new Pose(local.transform.position, local.transform.rotation);
                Vector3 contactStart = localBall.transform.position - new Vector3(1.85f, .2f, 0f);
                void PlacePlayer(Pose pose)
                {
                    controller.enabled = false;
                    local.transform.SetPositionAndRotation(pose.position, pose.rotation);
                    controller.enabled = true;
                    Physics.SyncTransforms();
                }
                try
                {
                    PlacePlayer(new Pose(contactStart, original.rotation));
                    var inputType = motor.GetType().GetNestedType("Input");
                    object input = Activator.CreateInstance(inputType);
                    inputType.GetField("Move").SetValue(input, Vector2.right);
                    motor.GetType().GetMethod("Simulate", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(motor, new[] { input, (object)(1f / 16f) });
                    Assert.That(local.transform.position.x - contactStart.x, Is.LessThan(.85f),
                        "The horizontal move must contact the ball.");
                    Assert.That(local.transform.position.y, Is.EqualTo(contactStart.y).Within(.001f),
                        "Ball contact must not push the predicted player up.");
                }
                finally { PlacePlayer(original); }

                uint epoch = local.AuthorityEpoch;
                Drive(Vector2.right);
                yield return Until(() => local.transform.position.x > -4.7f, "owner predicts movement");
                Assert.That(initialAuthority.transform.position.x, Is.EqualTo(-5f).Within(.1f));
                yield return Until(() => ballBody.linearVelocity.x > 4 && ballBody.linearVelocity.y > 0,
                    "authoritative contact kicks the ball across the worker boundary");
                ballBody.position = new Vector3(6, 1, -12);
                ballBody.linearVelocity = Vector3.zero;

                yield return Until(() => local.AuthorityEpoch > epoch && local.SimulationWorker == worker.LocalWorkerId,
                    "the moving player transfers to the worker");
                yield return Until(() => worker.TryGet(local.EntityId, out var authority) && authority.HasAuthority &&
                    authority.transform.position.x > 6 && local.transform.position.x > 8.5f,
                    "owner predicts past the worker-only wall");
                Drive(Vector2.zero);
                yield return Until(() => worker.TryGet(local.EntityId, out var authority) &&
                    Vector3.Distance(local.transform.position, authority.transform.position) < .1f,
                    "owner reconciles to the worker collision");
                Assert.Less(local.transform.position.x, 8f);
                Assert.Greater(transport.AuthorityRpcsBeforeCommit, 0,
                    "The test must deliver an authority RPC for the new epoch before the worker commit.");
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                foreach (var manager in managers)
                {
                    manager.Stop();
                    Object.DestroyImmediate(manager.gameObject);
                }
                foreach (var scene in scenes) SceneManager.UnloadSceneAsync(scene);
                Object.DestroyImmediate(transport);
            }
        }

        static int Score(NetworkObject obj,string name)
        {
            var component=obj.GetComponent("SoccerMatch");var variable=component.GetType().GetField(name).GetValue(component);
            return (int)variable.GetType().GetProperty("Value").GetValue(variable);
        }
    }
}
#endif
