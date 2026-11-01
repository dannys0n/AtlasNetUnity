#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
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
    public sealed class ClientSoccerTests
    {
        static void Set(object obj,string field,object value)=>obj.GetType().GetField(field,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(obj,value);
        [UnityTest]
        public IEnumerator OwnersDriveAcrossHandoffsAndKickTheServerBallWithoutNetworkInterpolation()
        {
            var player=AssetDatabase.LoadAssetAtPath<NetworkObject>("Assets/M2/Prefabs/M2_SoccerClientAuthorityPlayer.prefab");
            var ballPrefab=AssetDatabase.LoadAssetAtPath<NetworkObject>("Assets/M2/Prefabs/M2_SoccerUnbufferedBall.prefab");
            var registry=AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>("Assets/M2/Prefabs/M2NetworkPrefabs.asset");
            foreach(var prefab in new[]{player,ballPrefab})Assert.IsFalse(new SerializedObject(prefab.GetComponent<NetworkTransform>()).FindProperty("interpolate").boolValue);
            Assert.AreEqual(TransformWriter.Owner,player.GetComponent<NetworkTransform>().PositionWriter);
            Assert.AreEqual(AnimatorWriter.Owner,player.GetComponent<NetworkAnimator>().Writer);
            NetworkManager Create(string name,ClientTransport transport=ClientTransport.Udp)
            {
                var m=new GameObject(name).AddComponent<NetworkManager>();Set(m,"playerPrefab",player);Set(m,"networkPrefabsLists",new[]{registry});Set(m,"automaticLocalHandoffs",true);Set(m,"localBoundaryMargin",.05f);m.ConfigureConnection("127.0.0.1",17949,transport);return m;
            }
            var server=Create("Client soccer server");var worker=Create("Client soccer worker");var udp=Create("Client soccer UDP");var web=Create("Client soccer WebSocket",ClientTransport.WebSocket);
            var managers=new[]{server,worker,udp,web};var scenes=managers.Select(m=>SceneManager.CreateScene(m.name,new CreateSceneParameters(LocalPhysicsMode.Physics3D))).ToArray();
            foreach(var scene in scenes){var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.transform.position=new Vector3(0,-.5f,0);floor.transform.localScale=new Vector3(105,1,68);SceneManager.MoveGameObjectToScene(floor,scene);}
            void Prepare(){for(int i=0;i<managers.Length;i++)foreach(var obj in managers[i].SpawnedObjects.ToArray()){if(obj.gameObject.scene!=scenes[i])SceneManager.MoveGameObjectToScene(obj.gameObject,scenes[i]);}}
            IEnumerator Step(){Prepare();yield return new WaitForFixedUpdate();Prepare();foreach(var scene in scenes)scene.GetPhysicsScene().Simulate(Time.fixedDeltaTime);yield return null;}
            IEnumerator Until(Func<bool> ready,string message){float end=Time.realtimeSinceStartup+10;while(!ready()&&Time.realtimeSinceStartup<end)yield return Step();Assert.IsTrue(ready(),message);}
            NetworkObject Authority(AtlasNet.EntityId id)=>managers.Where(m=>m.IsServer).SelectMany(m=>m.SpawnedObjects).First(o=>o.EntityId==id&&o.HasAuthority);
            bool Waving(NetworkObject obj){var a=obj.GetComponent<Animator>();return a.GetCurrentAnimatorStateInfo(1).IsName("Wave")||(a.IsInTransition(1)&&a.GetNextAnimatorStateInfo(1).IsName("Wave"));}
            try
            {
                server.PlayerSpawnPosition=s=>new Vector3(-6,0,(s.Value%2==0?-6:6));server.StartServer();worker.StartWorker();udp.StartClient();web.StartClient();
                yield return Until(()=>server.WorkerCount==2&&udp.IsConnectedClient&&web.IsConnectedClient&&udp.SpawnedCount==2&&web.SpawnedCount==2,"Both native transports join the variant");
                var local=udp.SpawnedObjects.Single(o=>o.IsOwner);var other=web.SpawnedObjects.Single(o=>o.IsOwner);Vector2 drive=Vector2.right,webDrive=Vector2.zero;
                var movement=local.GetComponent("SoccerPlayer");movement.GetType().GetProperty("KeyboardInput").SetValue(movement,new Func<Vector2>(()=>drive));
                var webMovement=other.GetComponent("SoccerPlayer");webMovement.GetType().GetProperty("KeyboardInput").SetValue(webMovement,new Func<Vector2>(()=>webDrive));
                uint epoch=local.AuthorityEpoch;var body=local.GetComponent<Rigidbody>();
                yield return Until(()=>local.AuthorityEpoch>epoch&&body.position.x>6,"Owner continues moving while its logical worker changes");
                Assert.IsFalse(body.isKinematic);Assert.That(body.linearVelocity.x,Is.EqualTo(16f).Within(.1f));
                foreach(var m in new[]{server,worker})foreach(var obj in m.SpawnedObjects)Assert.IsTrue(obj.GetComponent<Rigidbody>().isKinematic,"Server player copies do not simulate owner physics");
                drive=Vector2.zero;yield return Step();yield return Step();
                yield return Until(()=>web.TryGet(local.EntityId,out var remote)&&Vector3.Distance(remote.transform.position,local.transform.position)<.1f,"Unbuffered remote pose converges");
                webDrive=Vector2.up;for(int i=0;i<4;i++)yield return Step();Assert.Greater(other.GetComponent<Rigidbody>().linearVelocity.z,15);webDrive=Vector2.zero;
                var ball=server.Spawn(ballPrefab,body.position+new Vector3(3,1,0),Quaternion.identity);yield return Until(()=>udp.TryGet(ball.EntityId,out _),"Ball reaches owner client");
                drive=Vector2.right;bool kicked=false;float end=Time.realtimeSinceStartup+3;
                while(!kicked&&Time.realtimeSinceStartup<end){yield return Step();var component=Authority(ball.EntityId).GetComponent("SoccerBall");var contacts=(IDictionary)component.GetType().GetField("contacts",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(component);kicked=contacts.Count>0;}
                Assert.IsTrue(kicked,"Owner contacts request an impulse from the authoritative ball");drive=Vector2.zero;
                var match=Authority(ball.EntityId).GetComponent("SoccerMatch");int team=(int)movement.GetType().GetProperty("Team").GetValue(movement);match.GetType().GetMethod("RecordGoal").Invoke(match,new object[]{team});
                yield return Until(()=>Waving(local)&&web.TryGet(local.EntityId,out var remote)&&Waving(remote),"Server goal celebration reaches the owning Animator and its observer");
            }
            finally
            {
                foreach(var m in managers){m.Stop();Object.DestroyImmediate(m.gameObject);}foreach(var scene in scenes)SceneManager.UnloadSceneAsync(scene);
            }
        }
    }
}
#endif
