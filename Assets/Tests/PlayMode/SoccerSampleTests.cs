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
            const string root="Assets/Samples/AtlasNet Unity/0.1.0/08 Soccer/Prefabs/";
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
                for(int i=0;i<8;i++){Input(Vector2.right);yield return Step();}
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
            const string root="Assets/Samples/AtlasNet Unity/0.1.0/08 Soccer/Prefabs/";
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
                float ring=player.GetComponent<CapsuleCollider>().radius*player.transform.localScale.x+1f-.02f;
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
        static int Score(NetworkObject obj,string name)
        {
            var component=obj.GetComponent("SoccerMatch");var variable=component.GetType().GetField(name).GetValue(component);
            return (int)variable.GetType().GetProperty("Value").GetValue(variable);
        }
    }
}
#endif
